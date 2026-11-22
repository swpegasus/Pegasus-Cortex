using System.Buffers.Binary;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("Pegasus.AgentOps.ProcessRunner.Tests")]

namespace Pegasus.AgentOps.Engine;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, bool StdoutTruncated, bool StderrTruncated);

public sealed class ProcessEnvironment
{
    private readonly ReadOnlyCollection<KeyValuePair<string, string>> _entries;

    private ProcessEnvironment(IEnumerable<KeyValuePair<string, string>> entries) =>
        _entries = Array.AsReadOnly(entries.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray());

    public static ProcessEnvironment Empty { get; } = new([]);

    public static ProcessEnvironment Replace(IReadOnlyDictionary<string, string?> values) =>
        Create(values, inherit: false);

    public static ProcessEnvironment InheritCurrent(IReadOnlyDictionary<string, string?>? overlay = null) =>
        Create(overlay, inherit: true);

    internal IReadOnlyList<KeyValuePair<string, string>> Entries => _entries;

    internal ProcessEnvironment WithInternal(string key, string value)
    {
        ValidateKey(key);
        var updated = _entries.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase);
        updated[key] = ValidateValue(value);
        return Create(updated, inherit: false);
    }

    private static ProcessEnvironment Create(IReadOnlyDictionary<string, string?>? values, bool inherit)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (inherit)
        {
            foreach (var item in new ProcessStartInfo { UseShellExecute = false }.Environment)
                AddExact(result, item.Key, item.Value ?? string.Empty);
        }

        if (values is not null)
        {
            var suppliedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var item in values)
                {
                    ValidateKey(item.Key);
                    if (!suppliedKeys.Add(item.Key)) throw new AgentOpsException("invalid");
                    if (item.Value is null)
                        result.Remove(item.Key);
                    else
                        result[item.Key] = ValidateValue(item.Value);
                }
            }
            catch (InvalidOperationException ex)
            {
                throw new AgentOpsException("invalid", inner: ex);
            }
        }
        return new ProcessEnvironment(result);
    }

    private static void AddExact(IDictionary<string, string> target, string key, string value)
    {
        ValidateKey(key);
        if (!target.TryAdd(key, ValidateValue(value))) throw new AgentOpsException("invalid");
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.IndexOf('\0') >= 0) throw new AgentOpsException("invalid");
    }

    private static string ValidateValue(string value)
    {
        if (value.IndexOf('\0') >= 0) throw new AgentOpsException("invalid");
        return value;
    }
}

public static class ProcessRunner
{
    private const int MaxCaptureChars = 64 * 1024;

    public static Task<ProcessResult> RunAsync(
        string file,
        IReadOnlyList<string> arg,
        TimeSpan timeout,
        string? cwd,
        CancellationToken cancellationToken) =>
        RunAsync(file, arg, timeout, cwd, ProcessEnvironment.Empty, cancellationToken);

    public static async Task<ProcessResult> RunAsync(
        string file,
        IReadOnlyList<string> arg,
        TimeSpan timeout,
        string? cwd,
        ProcessEnvironment environment,
        CancellationToken cancellationToken) =>
        await RunAsync(file, arg, timeout, cwd, environment, MaxCaptureChars, cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<ProcessResult> RunAsync(
        string file,
        IReadOnlyList<string> arg,
        TimeSpan timeout,
        string? cwd,
        ProcessEnvironment environment,
        int maximumCaptureCharacters,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Pegasus AgentOps process execution requires Windows.");
        if (string.IsNullOrWhiteSpace(file) || file.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(file))
            throw new AgentOpsException("invalid");
        if (arg is null || environment is null || maximumCaptureCharacters is < 1 or > 1024 * 1024)
            throw new AgentOpsException("invalid");
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) throw new AgentOpsException("invalid");

        cancellationToken.ThrowIfCancellationRequested();
        if (timeout == TimeSpan.Zero) return TimedOutWithoutLaunch();
        var executable = Path.GetFullPath(file);
        if (!File.Exists(executable) || Directory.Exists(executable)) throw new AgentOpsException("not_found");
        var workingDirectory = string.IsNullOrWhiteSpace(cwd)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(cwd);
        if (!Directory.Exists(workingDirectory)) throw new AgentOpsException("not_found");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            timeoutCts.CancelAfter(timeout);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new AgentOpsException("invalid", inner: ex);
        }

        byte[] request;
        try
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            request = ProcessHostWire.CreateRequest(executable, arg, workingDirectory, environment, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TimedOutWithoutLaunch();
        }
        try
        {
            return await RunHostAsync(request, maximumCaptureCharacters, timeoutCts.Token, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(request);
        }
    }

    private static ProcessResult TimedOutWithoutLaunch() =>
        new(137, string.Empty, string.Empty, true, false, false);

    private static async Task<ProcessResult> RunHostAsync(
        byte[] request,
        int maximumCaptureCharacters,
        CancellationToken operationToken,
        CancellationToken callerToken)
    {
        var hostPath = ResolveProcessHost();
        using var job = WindowsJob.Create();
        using var process = StartProcessHost(hostPath);

        try
        {
            ProcessRunnerTestHooks.ProcessCreated?.Invoke(process.Id);
            if (ProcessRunnerTestHooks.FailurePoint == ProcessLaunchFailurePoint.Assign)
                throw new AgentOpsException("internal");
            job.AssignAndVerify(process);
        }
        catch
        {
            TryKill(process);
            await WaitAfterKillAsync(process).ConfigureAwait(false);
            throw;
        }

        var responseReader = ProcessRunnerTestHooks.ReadResponseAsync ?? ProcessHostWire.ReadResponseAsync;
        var responseTask = responseReader(process.StandardOutput.BaseStream, _ => job.Terminate());
        var diagnosticTask = ReadBoundedAsync(process.StandardError);

        try
        {
            if (ProcessRunnerTestHooks.FailurePoint == ProcessLaunchFailurePoint.Send)
                throw new IOException("Injected process-host transport failure.");
            var writer = ProcessRunnerTestHooks.WriteRequestAsync ?? ProcessHostWire.WriteRequestAsync;
            await writer(process.StandardInput.BaseStream, request, operationToken).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(request);
            process.StandardInput.Close();
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            process.StandardInput.Dispose();
            job.Terminate();
            await WaitAfterKillAsync(process).ConfigureAwait(false);
            job.Dispose();
            return await CompleteTimedOutHostAsync(process, responseTask, diagnosticTask, maximumCaptureCharacters).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            process.StandardInput.Dispose();
            job.Terminate();
            await WaitAfterKillAsync(process).ConfigureAwait(false);
            job.Dispose();
            await ObserveHostTasksAsync(responseTask, diagnosticTask).ConfigureAwait(false);
            callerToken.ThrowIfCancellationRequested();
            throw new AgentOpsException("internal", inner: ex);
        }

        var timedOut = false;
        try
        {
            var exitTask = process.WaitForExitAsync(operationToken);
            if (await Task.WhenAny(exitTask, responseTask).ConfigureAwait(false) == responseTask)
            {
                try
                {
                    _ = await responseTask.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
                {
                    job.Terminate();
                    await WaitAfterKillAsync(process).ConfigureAwait(false);
                    job.Dispose();
                    await ObserveHostTasksAsync(responseTask, diagnosticTask).ConfigureAwait(false);
                    throw await ProcessHostFailureAsync(ex, diagnosticTask).ConfigureAwait(false);
                }
            }
            await exitTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            timedOut = true;
            job.Terminate();
            await WaitAfterKillAsync(process).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job.Terminate();
            await WaitAfterKillAsync(process).ConfigureAwait(false);
            job.Dispose();
            await ObserveHostTasksAsync(responseTask, diagnosticTask).ConfigureAwait(false);
            callerToken.ThrowIfCancellationRequested();
            throw;
        }

        var hostExitCode = process.ExitCode;
        job.Dispose();
        ProcessHostResponse response;
        try
        {
            response = await responseTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            throw await ProcessHostFailureAsync(ex, diagnosticTask).ConfigureAwait(false);
        }
        _ = await diagnosticTask.ConfigureAwait(false); // Bounded and deliberately not surfaced with request data.

        if (!timedOut && response.Error is { Length: > 0 } error)
            throw new AgentOpsException(error);
        if (!timedOut && !response.Completed)
            throw new AgentOpsException("internal");

        var stdout = DecodeBounded(response.Stdout, response.StdoutTruncated, maximumCaptureCharacters);
        var stderr = DecodeBounded(response.Stderr, response.StderrTruncated, maximumCaptureCharacters);
        return new ProcessResult(
            response.Completed ? response.ExitCode : hostExitCode,
            stdout.Text,
            stderr.Text,
            timedOut,
            stdout.Truncated,
            stderr.Truncated);
    }

    private static async Task<ProcessResult> CompleteTimedOutHostAsync(
        Process process,
        Task<ProcessHostResponse> responseTask,
        Task<(string Text, bool Truncated)> diagnosticTask,
        int maximumCaptureCharacters)
    {
        ProcessHostResponse response;
        try
        {
            response = await responseTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            response = new ProcessHostResponse();
        }

        try
        {
            _ = await diagnosticTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        var stdout = DecodeBounded(response.Stdout, response.StdoutTruncated, maximumCaptureCharacters);
        var stderr = DecodeBounded(response.Stderr, response.StderrTruncated, maximumCaptureCharacters);
        return new ProcessResult(
            response.Completed ? response.ExitCode : process.ExitCode,
            stdout.Text,
            stderr.Text,
            true,
            stdout.Truncated,
            stderr.Truncated);
    }

    private static Process StartProcessHost(string path)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--stdio-v1");

        try
        {
            return Process.Start(start) ?? throw new AgentOpsException("internal");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new AgentOpsException("internal", inner: ex);
        }
    }

    internal static string ResolveProcessHost()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "Pegasus.AgentOps.ProcessHost.exe");
        if (!File.Exists(candidate)) throw new AgentOpsException("internal");
        return Path.GetFullPath(candidate);
    }

    private static async Task ObserveHostTasksAsync(
        Task<ProcessHostResponse> responseTask,
        Task<(string Text, bool Truncated)> diagnosticTask)
    {
        try
        {
            await Task.WhenAll(responseTask, diagnosticTask).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            // The helper was intentionally terminated before a complete frame.
        }
    }

    private static async Task<AgentOpsException> ProcessHostFailureAsync(
        Exception inner,
        Task<(string Text, bool Truncated)> diagnosticTask)
    {
        var diagnostic = await diagnosticTask.ConfigureAwait(false);
        var code = diagnostic.Text.Contains("You must install or update .NET", StringComparison.OrdinalIgnoreCase)
            ? "process_host_runtime"
            : "internal";
        return new AgentOpsException(code, inner: inner);
    }

    private static async Task WaitAfterKillAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var result = new StringBuilder();
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) break;
            var room = MaxCaptureChars - result.Length;
            if (room > 0) result.Append(buffer, 0, Math.Min(room, read));
            if (read > room) truncated = true;
        }
        return (result.ToString(), truncated);
    }

    private static (string Text, bool Truncated) DecodeBounded(
        MemoryStream bytes,
        bool alreadyTruncated,
        int maximumCaptureCharacters)
    {
        var text = Encoding.UTF8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
        if (text.Length <= maximumCaptureCharacters) return (text, alreadyTruncated);
        return (text[..maximumCaptureCharacters], true);
    }
}

internal enum ProcessLaunchFailurePoint
{
    None,
    Assign,
    Send
}

internal static class ProcessRunnerTestHooks
{
    internal static ProcessLaunchFailurePoint FailurePoint { get; set; }
    internal static Action<int>? ProcessCreated { get; set; }
    internal static Func<Stream, byte[], CancellationToken, Task>? WriteRequestAsync { get; set; }
    internal static Func<Stream, Action<int>?, Task<ProcessHostResponse>>? ReadResponseAsync { get; set; }

    internal static void Reset()
    {
        FailurePoint = ProcessLaunchFailurePoint.None;
        ProcessCreated = null;
        WriteRequestAsync = null;
        ReadResponseAsync = null;
    }
}

internal sealed class ProcessHostResponse
{
    internal MemoryStream Stdout { get; } = new();
    internal MemoryStream Stderr { get; } = new();
    internal bool StdoutTruncated { get; set; }
    internal bool StderrTruncated { get; set; }
    internal bool Completed { get; set; }
    internal int ExitCode { get; set; }
    internal string? Error { get; set; }
}

internal static class ProcessHostWire
{
    private const uint RequestMagic = 0x31514850; // PHQ1
    private const uint ResponseMagic = 0x31524850; // PHR1
    private const ushort Version = 1;
    internal const int MaxRequestBytes = 2 * 1024 * 1024;
    private const int MaxStringBytes = 256 * 1024;
    private const int MaxArguments = 4096;
    private const int MaxEnvironmentEntries = 8192;
    private const int MaxFrameBytes = 16 * 1024;
    internal const int MaxStreamBytes = 1024 * 1024;
    private const byte StdoutFrame = 1;
    private const byte StderrFrame = 2;
    private const byte CompletedFrame = 3;
    private const byte FailureFrame = 4;
    private const byte RootExitedFrame = 5;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] CreateRequest(
        string file,
        IReadOnlyList<string> arguments,
        string cwd,
        ProcessEnvironment environment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var argumentCount = arguments.Count;
        if (argumentCount is < 0 or > MaxArguments) throw new AgentOpsException("invalid");
        var frozenArguments = new string[argumentCount];
        try
        {
            for (var index = 0; index < frozenArguments.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = arguments[index];
                if (value is null || value.IndexOf('\0') >= 0) throw new AgentOpsException("invalid");
                frozenArguments[index] = value;
            }
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new AgentOpsException("invalid", inner: ex);
        }

        var frozenEnvironment = environment.Entries;
        if (frozenEnvironment.Count > MaxEnvironmentEntries) throw new AgentOpsException("invalid");

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true);
        writer.Write(RequestMagic);
        writer.Write(Version);
        WriteString(writer, stream, file);
        WriteString(writer, stream, cwd);
        EnsureRoom(stream, sizeof(int));
        writer.Write(frozenArguments.Length);
        foreach (var argument in frozenArguments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteString(writer, stream, argument);
        }
        EnsureRoom(stream, sizeof(int));
        writer.Write(frozenEnvironment.Count);
        foreach (var item in frozenEnvironment)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteString(writer, stream, item.Key);
            WriteString(writer, stream, item.Value);
        }
        writer.Flush();
        if (stream.Length > MaxRequestBytes) throw new AgentOpsException("invalid");
        return stream.ToArray();
    }

    internal static async Task WriteRequestAsync(Stream stream, byte[] request, CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, request.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ProcessHostResponse> ReadResponseAsync(Stream stream, Action<int>? onRootExited = null)
    {
        var response = new ProcessHostResponse();
        var header = new byte[sizeof(uint) + sizeof(ushort)];
        if (!await TryReadExactlyAsync(stream, header).ConfigureAwait(false)) return response;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != ResponseMagic ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(sizeof(uint))) != Version)
        {
            throw new InvalidDataException("process_host_protocol");
        }

        var frameHeader = new byte[sizeof(byte) + sizeof(int)];
        var frameCount = 0;
        var payloadBytes = 0L;
        while (await TryReadExactlyAsync(stream, frameHeader).ConfigureAwait(false))
        {
            if (++frameCount > (MaxStreamBytes * 2) + 2) throw new InvalidDataException("process_host_frames");
            var type = frameHeader[0];
            var length = BinaryPrimitives.ReadInt32LittleEndian(frameHeader.AsSpan(sizeof(byte)));
            if (length < 0 || length > MaxFrameBytes) throw new InvalidDataException("process_host_frame");
            payloadBytes = checked(payloadBytes + length);
            if (payloadBytes > (MaxStreamBytes * 2L) + 64) throw new InvalidDataException("process_host_frames");
            var payload = new byte[length];
            if (!await TryReadExactlyAsync(stream, payload).ConfigureAwait(false)) return response;

            switch (type)
            {
                case StdoutFrame:
                    response.StdoutTruncated |= AppendBounded(response.Stdout, payload);
                    break;
                case StderrFrame:
                    response.StderrTruncated |= AppendBounded(response.Stderr, payload);
                    break;
                case CompletedFrame:
                    if (payload.Length != sizeof(int) + 2) throw new InvalidDataException("process_host_complete");
                    response.ExitCode = BinaryPrimitives.ReadInt32LittleEndian(payload);
                    response.StdoutTruncated |= payload[sizeof(int)] != 0;
                    response.StderrTruncated |= payload[sizeof(int) + 1] != 0;
                    response.Completed = true;
                    return response;
                case FailureFrame:
                    response.Error = DecodeError(payload);
                    return response;
                case RootExitedFrame:
                    if (payload.Length != sizeof(int) + 2) throw new InvalidDataException("process_host_root_exit");
                    response.ExitCode = BinaryPrimitives.ReadInt32LittleEndian(payload);
                    response.StdoutTruncated |= payload[sizeof(int)] != 0;
                    response.StderrTruncated |= payload[sizeof(int) + 1] != 0;
                    response.Completed = true;
                    onRootExited?.Invoke(response.ExitCode);
                    break;
                default:
                    throw new InvalidDataException("process_host_frame");
            }
        }
        return response;
    }

    private static void WriteString(BinaryWriter writer, MemoryStream stream, string value)
    {
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException ex)
        {
            throw new AgentOpsException("invalid", inner: ex);
        }
        if (bytes.Length > MaxStringBytes) throw new AgentOpsException("invalid");
        EnsureRoom(stream, checked(sizeof(int) + bytes.Length));
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void EnsureRoom(MemoryStream stream, int bytes)
    {
        if (bytes < 0 || stream.Length > MaxRequestBytes - (long)bytes)
            throw new AgentOpsException("invalid");
    }

    private static bool AppendBounded(MemoryStream target, byte[] payload)
    {
        var room = MaxStreamBytes - checked((int)target.Length);
        if (room > 0) target.Write(payload, 0, Math.Min(room, payload.Length));
        return payload.Length > room;
    }

    private static string DecodeError(byte[] payload)
    {
        if (payload.Length is 0 or > 64) throw new InvalidDataException("process_host_error");
        string value;
        try
        {
            value = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("process_host_error", ex);
        }
        return value is "external_failed" or "internal" or "invalid" ? value : "internal";
    }

    private static async Task<bool> TryReadExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..]).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}

internal sealed class WindowsJob : IDisposable
{
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint ForcedExitCode = 137;
    private SafeFileHandle? _handle;
    private readonly Dictionary<int, SafeProcessHandle> _observedProcesses = [];

    private WindowsJob(SafeFileHandle handle) => _handle = handle;

    private nint Handle => _handle is { IsInvalid: false, IsClosed: false }
        ? _handle.DangerousGetHandle()
        : throw new ObjectDisposedException(nameof(WindowsJob));

    internal static WindowsJob Create()
    {
        var rawHandle = CreateJobObjectW(0, null);
        if (rawHandle == 0) throw InternalFailure("CreateJobObjectW");
        var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
        try
        {
            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, pointer, false);
                if (!SetInformationJobObject(handle.DangerousGetHandle(), JobObjectExtendedLimitInformationClass, pointer, (uint)size))
                    throw InternalFailure("SetInformationJobObject");
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
            return new WindowsJob(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal void AssignAndVerify(Process process)
    {
        if (!AssignProcessToJobObject(Handle, process.Handle)) throw InternalFailure("AssignProcessToJobObject");
        if (!IsProcessInJob(process.Handle, Handle, out var assigned) || !assigned)
            throw InternalFailure("IsProcessInJob");
    }

    internal int ActiveProcessCount()
    {
        if (!QueryInformationJobObject(Handle, 1, out var accounting, (uint)Marshal.SizeOf<JobAccounting>(), out _))
            throw InternalFailure("QueryInformationJobObject");
        return checked((int)accounting.ActiveProcesses);
    }

    internal (int Assigned, bool Truncated, int[] ProcessIds) ProcessIdInventory(int capacity = 64)
    {
        if (capacity is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(capacity));
        var size = 8 + capacity * IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(Handle, 3, buffer, (uint)size, out _) && Marshal.GetLastWin32Error() != 234)
                throw InternalFailure("QueryInformationJobObject(ProcessIdList)");
            var assigned = Marshal.ReadInt32(buffer);
            var count = Marshal.ReadInt32(buffer, 4);
            if (assigned < 0 || count < 0 || count > capacity) throw new AgentOpsException("internal");
            var ids = new int[count];
            for (var index = 0; index < count; index++)
                ids[index] = checked((int)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size));
            return (assigned, count < assigned, ids);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal int LiveProcessCount()
    {
        // Basic accounting retains exited processes while anybody holds a
        // reference. Only exact member handles and their signal state establish
        // liveness; neither executable names nor accounting totals do so.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var inventory = ProcessIdInventory(256);
            if (inventory.Truncated) throw new AgentOpsException("app_observation_incomplete");
            foreach (var retained in _observedProcesses.ToArray())
            {
                if (WaitForSingleObject(retained.Value.DangerousGetHandle(), 0) == 0)
                { _observedProcesses.Remove(retained.Key); retained.Value.Dispose(); }
            }
            foreach (var pid in inventory.ProcessIds)
            {
                if (_observedProcesses.ContainsKey(pid)) continue;
                if (_observedProcesses.Count >= 256) throw new AgentOpsException("app_observation_incomplete");
                const uint queryAndSynchronize = 0x00101000;
                var process = OpenProcess(queryAndSynchronize, false, pid);
                if (process.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error(); process.Dispose();
                    if (error == 87) continue;
                    throw new AgentOpsException("app_observation_incomplete");
                }
                if (!IsProcessInJob(process.DangerousGetHandle(), Handle, out var assigned))
                { process.Dispose(); throw new AgentOpsException("app_observation_incomplete"); }
                if (!assigned) { process.Dispose(); continue; }
                _observedProcesses.Add(pid, process);
            }
            var alive = 0;
            foreach (var process in _observedProcesses.Values)
            {
                var signal = WaitForSingleObject(process.DangerousGetHandle(), 0);
                if (signal == 258) alive++;
                else if (signal != 0) throw new AgentOpsException("app_observation_incomplete");
            }
            // Keep live handles across observations and termination. A member
            // disappearing from the next PID list is not proof it has signaled.
            var after = ProcessIdInventory(256);
            if (after.Truncated) throw new AgentOpsException("app_observation_incomplete");
            if (after.ProcessIds.All(inventory.ProcessIds.Contains)) return alive;
        }
        throw new AgentOpsException("app_observation_incomplete");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        internal long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        internal uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(nint job, int informationClass, out JobAccounting accounting, uint length, out uint returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(nint job, int informationClass, nint information, uint length, out uint returnedLength);

    internal bool AssignAndVerifyOrAcceptInherited(Process process)
    {
        if (AssignProcessToJobObject(Handle, process.Handle))
        {
            if (!IsProcessInJob(process.Handle, Handle, out var assigned) || !assigned)
                throw InternalFailure("IsProcessInJob");
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        const int errorAccessDenied = 5;
        if (error == errorAccessDenied &&
            IsProcessInJob(process.Handle, 0, out var inherited) && inherited)
        {
            // Some interactive Windows hosts are placed in an OS-owned job.
            // The fixed UI helper has no command or child-process surface, so
            // its caller may retain exact-tree termination as the bounded
            // fallback. General external tools still fail closed through
            // AssignAndVerify and never use this exception.
            return false;
        }

        throw InternalFailure("AssignProcessToJobObject", error);
    }

    // Long-lived application owners must retain accounting after an unconfirmed
    // termination request. Ordinary run keeps its existing close-on-failure path.
    internal bool RequestTermination()
    {
        var handle = _handle;
        return handle is not null && !handle.IsClosed && !handle.IsInvalid &&
            TerminateJobObject(handle.DangerousGetHandle(), ForcedExitCode);
    }

    internal void Terminate()
    {
        var handle = _handle;
        if (handle is null || handle.IsClosed || handle.IsInvalid) return;
        if (!TerminateJobObject(handle.DangerousGetHandle(), ForcedExitCode)) Dispose();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _handle, null)?.Dispose();
        foreach (var process in _observedProcesses.Values) process.Dispose();
        _observedProcesses.Clear();
    }

    private static AgentOpsException InternalFailure(string operation) =>
        new("internal", inner: new Win32Exception(Marshal.GetLastWin32Error(), operation));

    private static AgentOpsException InternalFailure(string operation, int error) =>
        new("internal", inner: new Win32Exception(error, operation));

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, uint infoClass, nint info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(nint job, uint exitCode);
}
