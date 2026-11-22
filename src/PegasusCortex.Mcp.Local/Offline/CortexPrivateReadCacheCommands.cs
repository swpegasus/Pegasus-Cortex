using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Mcp.Local.Transport;
using Pegasus.Cortex.Mcp.Protocol;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Mcp.Local.Offline;

/// <summary>Explicit passive reads; never provisions a profile, central task or offline spool.</summary>
public static class CortexPrivateReadCacheCommands
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private const string CommandSchema = "pegasus.cortex.private-read-cache-command/1";

    public static async ValueTask<(int ExitCode, string Json)> RunAsync(
        IReadOnlyList<string> args,
        CortexCurrentUserLayout? layout = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var invocation = CortexPrivateReadCacheArguments.Parse(args);
            layout ??= CortexCurrentUserLayout.Resolve();
            // Resolve only. Store admission precedes creation of its cache-specific private leaf.
            var cache = new CortexPrivateReadCache(layout.ConfigDirectory, timeProvider);
            return await ExecuteAsync(invocation, cache, ReadFreshPreviewAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            return Failure(error);
        }
    }

    // Fixtures inject the same command/store orchestration without connecting to installed state.
    internal static async ValueTask<(int ExitCode, string Json)> RunAsync(
        IReadOnlyList<string> args,
        CortexPrivateReadCache cache,
        Func<CortexPrivateReadCacheRequest, CancellationToken, ValueTask<string>> freshReader,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ExecuteAsync(CortexPrivateReadCacheArguments.Parse(args), cache, freshReader, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            return Failure(error);
        }
    }

    private static async ValueTask<(int, string)> ExecuteAsync(
        CortexPrivateReadCacheInvocation invocation,
        CortexPrivateReadCache cache,
        Func<CortexPrivateReadCacheRequest, CancellationToken, ValueTask<string>> freshReader,
        CancellationToken cancellationToken)
    {
        var binding = await cache.BindAsync(invocation.Request, cancellationToken).ConfigureAwait(false);
        var result = invocation.Command switch
        {
            "cache-init" => await cache.InitAsync(binding, cancellationToken).ConfigureAwait(false),
            "cache-status" => await cache.StatusAsync(binding, cancellationToken).ConfigureAwait(false),
            "cache-read" => await cache.ReadAsync(binding, cancellationToken).ConfigureAwait(false),
            "cache-refresh" => await cache.RefreshAsync(binding, invocation.ExpectedSnapshotGeneration,
                token => freshReader(invocation.Request, token), cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException("Unsupported private-cache command.")
        };
        if (invocation.Command == "cache-read" && result.State == "snapshot" && result.Snapshot is not null)
        {
            // Return one stable bounded HOT body. A busy/missing result must stay diagnostic metadata.
            var snapshot = JsonSerializer.Serialize(result.Snapshot, Json);
            if (Encoding.UTF8.GetByteCount(snapshot) > CortexPrivateReadCache.MaximumSnapshotUtf8Bytes)
                throw new InvalidDataException("Private-cache snapshot exceeds the output bound.");
            return (0, snapshot);
        }
        return (0, JsonSerializer.Serialize(new
        {
            schema = CommandSchema,
            mode = "private-passive-read-cache",
            result.State,
            authoritative = false,
            coreStoreIdentity = (string?)null,
            loadedRevisionProof = false,
            result.BindingSha256,
            result.BindingGeneration,
            result.SnapshotGeneration,
            snapshot = (CortexPrivateReadCacheSnapshot?)null
        }, Json));
    }

    private static (int, string) Failure(Exception error)
    {
        var security = error is UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException ||
            error is CortexPrivateReadCacheException { IsSecurityError: true } ||
            error is ProviderException { Security: true } || error is AgentOpsException { Code: "auth" };
        var code = error switch
        {
            CortexPrivateReadCacheException cache => cache.Code,
            ProviderException provider => provider.Code,
            ArgumentException => "private_cache_arguments_invalid",
            _ when security => "private_cache_security_rejected",
            _ => "private_cache_unavailable"
        };
        return (1, JsonSerializer.Serialize(new
        {
            schema = CommandSchema,
            mode = "private-passive-read-cache",
            state = security ? "security-rejected" : "failed",
            code,
            authoritative = false,
            coreStoreIdentity = (string?)null,
            loadedRevisionProof = false
        }, Json));
    }

    private static async ValueTask<string> ReadFreshPreviewAsync(
        CortexPrivateReadCacheRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new ProviderException("provider_unavailable", false);
        var owner = LocalIdentity.Current();
        var product = CortexProductLayout.ResolveCurrent();
        // Reuse ordinary adapter admission and peer/hello verification. No alternate client enrollment.
        CortexResidentRuntime.RequireSelectedAdapter(product.InstallRoot, product.ActiveCatalogPath,
            Environment.ProcessPath ?? throw new ProviderException("provider_identity_unavailable", true),
            CortexLocalProductCommands.Version);
        var credential = CortexClientRegistry.ReadProtectedCredential(
            Path.Combine(product.UserConfigDirectory, "connections", "codex.key"));
        if (credential.ClientId != "codex") throw new ProviderException("provider_identity_rejected", true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await using var pipe = await CortexMcpPipe.ConnectAsync(CortexProductIdentity.UserMcpPipe(owner.Session),
                owner, Path.Combine(product.InstallRoot, "bin", "host", "PegasusCortex.Host.exe"), deadline.Token)
                .ConfigureAwait(false);
            using var close = deadline.Token.Register(pipe.Dispose);
            await BinaryProtocol.WriteAsync(pipe, new
            {
                op = "hello", contract = CortexResidentServer.McpContract,
                credential.ClientId, credential.KeyId, credential.ApiKey, cwd = request.ContentRoot
            }, deadline.Token, 4096).ConfigureAwait(false);
            var hello = await BinaryProtocol.ReadAsync<JsonElement>(pipe, deadline.Token, CortexResidentServer.ResponseBytes)
                .ConfigureAwait(false);
            if (!CortexResidentRuntime.ValidHello(hello, CortexResidentServer.McpContract, owner, credential.ClientId))
                throw new ProviderException("provider_identity_rejected", true);
            using var input = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
            await using var output = new StreamWriter(pipe, new UTF8Encoding(false, true), 4096, leaveOpen: true)
                { NewLine = "\n" };
            var lines = new BoundedJsonLineReader(input, CortexResidentServer.ResponseBytes);
            await WriteAsync(output, new
            {
                jsonrpc = "2.0", id = "private-cache-init", method = "initialize",
                @params = new { protocolVersion = McpProtocol.Version, capabilities = new { },
                    clientInfo = new { name = "pegasus-private-read-cache", version = CortexLocalProductCommands.Version } }
            }, deadline.Token).ConfigureAwait(false);
            using (var initialized = await ResponseAsync(lines, "private-cache-init", deadline.Token).ConfigureAwait(false))
            {
                var result = initialized.RootElement.GetProperty("result");
                if (Text(result, "protocolVersion") != McpProtocol.Version ||
                    Text(result.GetProperty("serverInfo"), "name") != McpProtocol.ServerName)
                    throw new ProviderException("provider_protocol_rejected", true);
            }
            await WriteAsync(output, new { jsonrpc = "2.0", method = "notifications/initialized" }, deadline.Token)
                .ConfigureAwait(false);
            await WriteAsync(output, new
            {
                jsonrpc = "2.0", id = "private-cache-read", method = "tools/call",
                @params = new { name = "cortex_start", arguments = new
                {
                    namespaces = request.ReadNamespaces,
                    intent = "refresh explicit passive private read cache",
                    taskId = request.TaskId,
                    maxUtf8Bytes = 4096,
                    format = "detailed"
                    // No knownContextCursor: every refresh performs a fresh bounded read.
                } }
            }, deadline.Token).ConfigureAwait(false);
            using var response = await ResponseAsync(lines, "private-cache-read", deadline.Token).ConfigureAwait(false);
            return ProjectPreview(response.RootElement.GetProperty("result"), request);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ProviderException("provider_timeout", false); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or KeyNotFoundException or InvalidOperationException)
        { throw new ProviderException("provider_integrity_rejected", true); }
    }

    private static async ValueTask WriteAsync(TextWriter writer, object message, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(message, Json);
        if (Encoding.UTF8.GetByteCount(json) > CortexResidentServer.RequestBytes)
            throw new ProviderException("provider_request_bound", true);
        await writer.WriteAsync(json.AsMemory(), token).ConfigureAwait(false);
        await writer.WriteAsync("\n".AsMemory(), token).ConfigureAwait(false);
        await writer.FlushAsync(token).ConfigureAwait(false);
    }

    private static async ValueTask<JsonDocument> ResponseAsync(BoundedJsonLineReader reader, string id, CancellationToken token)
    {
        var line = await reader.ReadAsync(token).ConfigureAwait(false);
        if (line.EndOfStream || line.ExceededLimit) throw new ProviderException("provider_response_incomplete", true);
        var response = JsonDocument.Parse(line.Line!, new JsonDocumentOptions { MaxDepth = 32 });
        try
        {
            UniqueObject(response.RootElement);
            if (Text(response.RootElement, "jsonrpc") != "2.0" || Text(response.RootElement, "id") != id ||
                response.RootElement.TryGetProperty("error", out _))
                throw new ProviderException("provider_response_rejected", true);
            UniqueObject(response.RootElement.GetProperty("result"));
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    internal static string ProjectPreview(JsonElement tool, CortexPrivateReadCacheRequest request)
    {
        UniqueObject(tool);
        if (!tool.TryGetProperty("isError", out var error) || error.ValueKind != JsonValueKind.False)
            throw new ProviderException("provider_operation_rejected", true);
        var content = tool.GetProperty("structuredContent");
        UniqueObject(content);
        var namespaces = content.GetProperty("effectiveNamespaces");
        if (namespaces.ValueKind != JsonValueKind.Array ||
            !namespaces.EnumerateArray().Select(value => value.GetString()).SequenceEqual(request.ReadNamespaces, StringComparer.Ordinal))
            throw new ProviderException("provider_namespace_rejected", true);
        var text = new StringBuilder("Passive preview only. Always stale; central store identity is unknown; loaded revisions are not proven.\n");
        if (content.TryGetProperty("currentTask", out var task) && task.ValueKind != JsonValueKind.Null)
        {
            UniqueObject(task);
            if (Text(task, "taskId") != request.TaskId || !request.ReadNamespaces.Contains(Text(task, "namespace"), StringComparer.Ordinal))
                throw new ProviderException("provider_task_rejected", true);
            text.Append("Requested task preview: ").Append(Text(task, "title")).Append('\n');
        }
        else text.Append("The requested task was not returned. No task was created.\n");
        var items = content.GetProperty("items");
        if (items.ValueKind != JsonValueKind.Array)
            throw new ProviderException("provider_items_rejected", true);
        foreach (var item in items.EnumerateArray())
        {
            UniqueObject(item);
            var ns = Text(item, "namespace");
            if (!request.ReadNamespaces.Contains(ns, StringComparer.Ordinal))
                throw new ProviderException("provider_namespace_rejected", true);
            text.Append(ns).Append('/').Append(Text(item, "recordType")).Append('/')
                .Append(Text(item, "recordId")).Append(" (preview)\n")
                .Append(Text(item, "title")).Append('\n').Append(Text(item, "excerpt")).Append('\n');
        }
        // Bound the callback input independently; the store then fits its immutable envelope.
        var preview = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.ToString().EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 4096) break;
            preview.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return preview.ToString();
    }

    private static string Text(JsonElement value, string name)
    {
        var text = value.GetProperty(name);
        if (text.ValueKind != JsonValueKind.String) throw new ProviderException("provider_integrity_rejected", true);
        return text.GetString()!;
    }

    private static void UniqueObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(item => item.Name)
                .Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
            throw new ProviderException("provider_integrity_rejected", true);
    }

    private sealed class ProviderException(string code, bool security) : Exception(code)
    {
        public string Code { get; } = code;
        public bool Security { get; } = security;
    }
}
