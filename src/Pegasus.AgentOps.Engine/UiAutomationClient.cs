using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pegasus.AgentOps.Engine;

internal static class UiAutomationClient
{
    private const int ProtocolVersion = 1;
    private const int MaxRequestBytes = 256 * 1024;
    private const int MaxResponseBytes = 512 * 1024;
    private const int MaxDiagnosticBytes = 16 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    internal static async Task<UiHostResponse> ExecuteAsync(
        UiHostRequest request,
        TimeSpan timeout,
        bool mutation,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new UiAutomationException("platform", remedy: "UI automation requires Windows x64.");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            throw new UiAutomationException("invalid", "tmo", "Use a timeout from 1 through 30 seconds.");

        var host = ResolveHost();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        if (bytes.Length is < 2 or > MaxRequestBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new UiAutomationException("invalid", "request", "Reduce the bounded UI request.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var job = WindowsJob.Create();
        using var process = StartHost(host);
        var submitted = false;
        var assignedToOwnedJob = false;
        try
        {
            try
            {
                assignedToOwnedJob = job.AssignAndVerifyOrAcceptInherited(process);
            }
            catch
            {
                TryKill(process);
                await WaitQuietlyAsync(process).ConfigureAwait(false);
                throw;
            }

            var stderrTask = ReadBoundedAsync(process.StandardError, MaxDiagnosticBytes);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await process.StandardInput.BaseStream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(deadline.Token).ConfigureAwait(false);
            submitted = true;
            process.StandardInput.Close();

            await ReadExactlyAsync(process.StandardOutput.BaseStream, header, deadline.Token).ConfigureAwait(false);
            var responseLength = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (responseLength is < 2 or > MaxResponseBytes)
                throw new UiAutomationException("ui_protocol", remedy: "Repair the AgentOps UI host payload and retry only after inspecting the target.", recoveryRequired: mutation && submitted, partial: mutation && submitted);
            var responseBytes = new byte[responseLength];
            try
            {
                await ReadExactlyAsync(process.StandardOutput.BaseStream, responseBytes, deadline.Token).ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<UiHostResponse>(responseBytes, Json)
                    ?? throw new UiAutomationException("ui_protocol", remedy: "Repair the AgentOps UI host payload.");
                if (response.Version != ProtocolVersion || !StringComparer.Ordinal.Equals(response.Id, request.Id) || response.Operation != request.Operation)
                    throw new UiAutomationException("ui_protocol", remedy: "Repair the AgentOps UI host payload and retry only after inspecting the target.", recoveryRequired: mutation && submitted, partial: mutation && submitted);
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                _ = await stderrTask.ConfigureAwait(false);
                return response;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(responseBytes);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            TerminateHost(job, process, assignedToOwnedJob);
            await WaitQuietlyAsync(process).ConfigureAwait(false);
            if (mutation && submitted)
                throw new UiAutomationException("recovery_required", remedy: "Inspect the exact target before deciding whether to retry the UI mutation.", partial: true, recoveryRequired: true);
            throw new UiAutomationException(cancellationToken.IsCancellationRequested ? "canceled" : "timeout", remedy: "Retry the read-only UI query with a bounded timeout after confirming the tray is ready.");
        }
        catch (UiAutomationException)
        {
            TerminateHost(job, process, assignedToOwnedJob);
            await WaitQuietlyAsync(process).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or Win32Exception)
        {
            TerminateHost(job, process, assignedToOwnedJob);
            await WaitQuietlyAsync(process).ConfigureAwait(false);
            throw new UiAutomationException(
                mutation && submitted ? "recovery_required" : "ui_unavailable",
                remedy: mutation && submitted
                    ? "Inspect the exact target before deciding whether to retry the UI mutation."
                    : "Keep the signed-in AgentOps tray running or repair the product-local UI provider payload.",
                partial: mutation && submitted,
                recoveryRequired: mutation && submitted,
                inner: ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static string ResolveHost(string? baseDirectory = null)
    {
        baseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "Pegasus.AgentOps.UiHost.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "ui", "Pegasus.AgentOps.UiHost.exe"))
        };
        foreach (var candidate in candidates)
        {
            var directory = Path.GetDirectoryName(candidate)!;
            var stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(candidate));
            var requiredPayload = new[]
            {
                candidate,
                stem + ".dll",
                stem + ".deps.json",
                stem + ".runtimeconfig.json",
                Path.Combine(directory, "AutoItX3_x64.dll")
            };
            if (requiredPayload.Any(path =>
                    !File.Exists(path) || Directory.Exists(path) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                continue;
            return Path.GetFullPath(candidate);
        }
        throw new UiAutomationException("ui_unavailable", remedy: "Repair Cortex so one complete product-local x64 UI host payload is present beside the tray or in the adjacent UI directory.");
    }

    private static Process StartHost(string path)
    {
        var start = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--stdio-v1");
        start.Environment.Clear();
        try
        {
            return Process.Start(start) ?? throw new UiAutomationException("ui_unavailable", remedy: "Repair or re-enable the product-local UI host.");
        }
        catch (Win32Exception ex)
        {
            throw new UiAutomationException("ui_unavailable", remedy: "Repair or re-enable the product-local UI host.", inner: ex);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxUtf8Bytes)
    {
        var buffer = new char[1024];
        var builder = new StringBuilder();
        var bytes = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) break;
            var accepted = Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
            if (bytes + accepted <= maxUtf8Bytes) builder.Append(buffer, 0, read);
            bytes += accepted;
        }
        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    private static void TerminateHost(WindowsJob job, Process process, bool assignedToOwnedJob)
    {
        if (assignedToOwnedJob) job.Terminate();
        else TryKill(process);
    }

    private static async Task WaitQuietlyAsync(Process process)
    {
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
    }
}
