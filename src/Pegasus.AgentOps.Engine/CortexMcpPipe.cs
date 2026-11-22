using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Engine;

public sealed record CortexMcpConnection(string Id, uint? ProcessId, string Application, DateTimeOffset OpenedUtc);

/// <summary>Bounded live connection state, not retained request bodies or a second job store.</summary>
public sealed class CortexMcpConnections
{
    private sealed record Entry(CortexMcpConnection Info, CancellationTokenSource Stop);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _accepting = 1;
    public bool Accepting { get => Volatile.Read(ref _accepting) != 0; set => Volatile.Write(ref _accepting, value ? 1 : 0); }
    public IReadOnlyList<CortexMcpConnection> Snapshot() => _entries.Values.Select(entry => entry.Info).OrderBy(entry => entry.OpenedUtc).ToArray();
    public bool Disconnect(string id)
    {
        if (!_entries.TryGetValue(id, out var entry)) return false;
        try { entry.Stop.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }
    internal string Add(LocalPeer peer, CancellationTokenSource stop)
    {
        var id = Guid.NewGuid().ToString("N");
        var name = Path.GetFileName(peer.ApplicationPath ?? "unknown");
        _entries[id] = new(new(id, peer.ProcessId, name.Length <= 96 ? name : name[..96], DateTimeOffset.UtcNow), stop);
        return id;
    }
    internal void Remove(string id) => _entries.TryRemove(id, out _);
}

/// <summary>MCP JSON-RPC lines over a local authenticated duplex named pipe.</summary>
[SupportedOSPlatform("windows")]
public static class CortexMcpPipe
{
    // Matches the MCP protocol bounds. A pipe instance is a session, not a server process.
    public const int MaximumRequestBytes = 256 * 1024;
    public const int MaximumResponseBytes = 512 * 1024;
    public const int MaximumConnections = 31; // leave one of the Windows pipe's 32 instances for admission
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static object DescribeCurrentEndpoint()
    {
        var owner = LocalIdentity.Current();
        return new { schema = "pegasus.cortex.mcp-pipe/1", transport = "named-pipe", pipe = CortexProductIdentity.UserMcpPipe(owner.Session),
            protocol = "mcp-jsonrpc-2.0", framing = "utf8-json-lines", authentication = "windows-owner-session", multiplexing = "one-duplex-pipe-instance-per-client",
            maximumConnections = MaximumConnections, maximumRequestBytes = MaximumRequestBytes, maximumResponseBytes = MaximumResponseBytes,
            management = "Cortex tray: MCP connections", stdioAdapter = "PegasusCortex.exe stdio", network = false };
    }

    public static async Task RunServerAsync(string pipeName, LocalPeer owner, CortexMcpConnections connections,
        Func<TextReader, TextWriter, CancellationToken, Task> serveSession, CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(MaximumConnections);
        var sessions = new ConcurrentDictionary<string, Task>(StringComparer.Ordinal);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = LocalPipeFactory.Create(pipeName, owner.Sid);
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    var accepted = pipe;
                    pipe = null;
                    var key = Guid.NewGuid().ToString("N");
                    // Install the task before it can finish/remove itself.
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var task = HandleAsync(accepted, key, ready.Task);
                    sessions[key] = task;
                    ready.SetResult();
                }
                catch { pipe?.Dispose(); slots.Release(); throw; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(sessions.Values).ConfigureAwait(false); }

        async Task HandleAsync(NamedPipeServerStream pipe, string key, Task ready)
        {
            await ready.ConfigureAwait(false);
            string? connectionId = null;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var close = stop.Token.Register(pipe.Dispose);
            try
            {
                var peer = LocalIdentity.Capture(pipe);
                if (!peer.SameIdentity(owner) || !connections.Accepting) return;
                connectionId = connections.Add(peer, stop);
                using var reader = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, Utf8, 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
                await serveSession(reader, writer, stop.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or AgentOpsException or DecoderFallbackException) { }
            finally
            {
                if (connectionId is not null) connections.Remove(connectionId);
                pipe.Dispose();
                slots.Release();
                sessions.TryRemove(key, out _);
            }
        }
    }

    public static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, LocalPeer owner,
        string expectedHostPath, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        try
        {
            PathNormalizer.RejectAbsoluteReparseComponents(expectedHostPath);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var server = LocalIdentity.CaptureServer(pipe);
            if (!server.SameIdentity(owner) || server.ApplicationPath is null ||
                !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(server.ApplicationPath), Path.GetFullPath(expectedHostPath)))
                throw new AgentOpsException("auth");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    public static async Task<int> RunCurrentStdioAdapterAsync(CancellationToken cancellationToken)
    {
        var owner = LocalIdentity.Current();
        var pipeName = CortexProductIdentity.UserMcpPipe(owner.Session);
        var layout = CortexProductLayout.ResolveExecution();
        var hostPath = Path.Combine(layout.InstallRoot, "bin", "host", "PegasusCortex.Host.exe");
        NamedPipeClientStream pipe;
        try { pipe = await ConnectAsync(pipeName, owner, hostPath, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Cortex is off or unavailable. Start Cortex explicitly to connect.");
            return 3;
        }
        await using (pipe)
        {
            using var input = new StreamReader(Console.OpenStandardInput(), Utf8, false, 4096);
            await using var output = new StreamWriter(Console.OpenStandardOutput(), Utf8, 4096) { AutoFlush = true, NewLine = "\n" };
            await RelayAsync(pipe, input, output, cancellationToken).ConfigureAwait(false);
        }
        return 0;
    }

    public static async Task RelayAsync(Stream pipe, TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var close = stop.Token.Register(pipe.Dispose);
        using var incoming = new StreamReader(pipe, Utf8, false, 4096, leaveOpen: true);
        await using var outgoing = new StreamWriter(pipe, Utf8, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var gate = new object();
        var pending = 0;
        var eof = false;
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = SendAsync();
        var receive = ReceiveAsync();
        try
        {
            var first = await Task.WhenAny(send, receive).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (first == send) await drained.Task.WaitAsync(stop.Token).ConfigureAwait(false);
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(send, receive).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
        }

        async Task SendAsync()
        {
            while (await ReadBoundedLineAsync(input, MaximumRequestBytes, stop.Token).ConfigureAwait(false) is { } line)
            {
                var expectsResponse = true;
                try
                {
                    using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
                    var root = document.RootElement;
                    // Valid notifications have no reply. Invalid shapes still receive protocol errors.
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("jsonrpc", out var version) && version.GetString() == "2.0" &&
                        root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && !root.TryGetProperty("id", out _)) expectsResponse = false;
                }
                catch (JsonException) { }
                lock (gate) if (expectsResponse) pending++;
                await outgoing.WriteLineAsync(line.AsMemory(), stop.Token).ConfigureAwait(false);
            }
            lock (gate) { eof = true; if (pending == 0) drained.TrySetResult(); }
        }
        async Task ReceiveAsync()
        {
            while (await ReadBoundedLineAsync(incoming, MaximumResponseBytes, stop.Token).ConfigureAwait(false) is { } line)
            {
                await output.WriteLineAsync(line.AsMemory(), stop.Token).ConfigureAwait(false);
                await output.FlushAsync(stop.Token).ConfigureAwait(false);
                lock (gate) { if (pending > 0) pending--; if (eof && pending == 0) drained.TrySetResult(); }
            }
            drained.TrySetResult();
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(TextReader reader, int maximumBytes, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var one = new char[1];
        // The byte ceiling is no larger than this char ceiling. UTF-8 byte count
        // is checked once, preserving surrogate pairs without quadratic encoding.
        while (await reader.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
        {
            if (one[0] == '\n') break;
            if (line.Length >= maximumBytes) throw new InvalidDataException("mcp_message_too_large");
            line.Append(one[0]);
        }
        if (line.Length == 0 && one[0] != '\n') return null;
        if (line.Length != 0 && line[^1] == '\r') line.Length--;
        var result = line.ToString();
        if (Utf8.GetByteCount(result) > maximumBytes) throw new InvalidDataException("mcp_message_too_large");
        return result;
    }
}
