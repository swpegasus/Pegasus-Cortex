using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

if (args is ["--app-stdio-v1", "hidden"]) return await ApplicationHost.RunAsync(false);
if (args is ["--app-stdio-v1", "visible"]) return await ApplicationHost.RunAsync(true);
if (args is not ["--stdio-v1"]) return 2;

ProcessHostResponseWriter? response = null;
try
{
    var request = await ProcessHostRequest.ReadAsync(Console.OpenStandardInput());
    response = new ProcessHostResponseWriter(Console.OpenStandardOutput());
    if (!IsCurrentProcessInJob())
    {
        response.Failure("internal");
        return 8;
    }

    var start = new ProcessStartInfo(request.Executable)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = false,
        CreateNoWindow = true,
        WorkingDirectory = request.WorkingDirectory
    };
    start.Environment.Clear();
    foreach (var item in request.Environment) start.Environment[item.Key] = item.Value;
    foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);

    Process process;
    try
    {
        process = Process.Start(start) ?? throw new InvalidOperationException("process start returned null");
    }
    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
    {
        response.Failure("external_failed");
        return 6;
    }

    using (process)
    {
        if (!IsProcessInJob(process.Handle, 0, out var targetInJob) || !targetInJob)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            response.Failure("internal");
            return 8;
        }

        var stdoutTask = PumpAsync(process.StandardOutput.BaseStream, ProcessHostResponseWriter.StdoutFrame, response);
        var stderrTask = PumpAsync(process.StandardError.BaseStream, ProcessHostResponseWriter.StderrFrame, response);
        await process.WaitForExitAsync();
        var pumps = Task.WhenAll(stdoutTask, stderrTask);
        if (await Task.WhenAny(pumps, Task.Delay(250)) != pumps)
        {
            // A descendant retained one of the redirected handles. Publish the
            // root result, then exit so the parent closes the owned job and
            // terminates the remaining tree instead of waiting forever for EOF.
            response.RootExited(
                process.ExitCode,
                !stdoutTask.IsCompletedSuccessfully || stdoutTask.Result,
                !stderrTask.IsCompletedSuccessfully || stderrTask.Result);
            return 0;
        }

        var truncated = await pumps;
        response.Complete(process.ExitCode, truncated[0], truncated[1]);
        return 0;
    }
}
catch (Exception ex) when (ex is IOException or InvalidDataException or DecoderFallbackException or ArgumentException or Win32Exception or InvalidOperationException)
{
    try
    {
        response ??= new ProcessHostResponseWriter(Console.OpenStandardOutput());
        response.Failure("internal");
    }
    catch (IOException)
    {
    }
    Console.Error.WriteLine("process_host_internal");
    return 8;
}

static async Task<bool> PumpAsync(Stream source, byte frameType, ProcessHostResponseWriter response)
{
    const int maxBytes = 1024 * 1024;
    var buffer = new byte[8192];
    var accepted = 0;
    var truncated = false;
    while (true)
    {
        var read = await source.ReadAsync(buffer);
        if (read == 0) return truncated;
        var keep = Math.Min(read, maxBytes - accepted);
        if (keep > 0)
        {
            response.Chunk(frameType, buffer.AsSpan(0, keep));
            accepted += keep;
        }
        if (keep < read) truncated = true;
    }
}

static bool IsCurrentProcessInJob()
{
    if (!IsProcessInJob(GetCurrentProcess(), 0, out var result))
        throw new Win32Exception(Marshal.GetLastWin32Error());
    return result;
}

[DllImport("kernel32.dll")]
static extern nint GetCurrentProcess();

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

sealed record ProcessHostRequest(
    string Executable,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment)
{
    private const uint RequestMagic = 0x31514850; // PHQ1
    private const ushort Version = 1;
    private const int MaxRequestBytes = 2 * 1024 * 1024;
    private const int MaxStringBytes = 256 * 1024;
    private const int MaxArguments = 4096;
    private const int MaxEnvironmentEntries = 8192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<ProcessHostRequest> ReadAsync(Stream input)
    {
        var prefix = new byte[sizeof(int)];
        await ReadExactlyAsync(input, prefix);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaxRequestBytes) throw new InvalidDataException("request_length");
        var payload = new byte[length];
        await ReadExactlyAsync(input, payload);

        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: false);
            if (reader.ReadUInt32() != RequestMagic || reader.ReadUInt16() != Version)
                throw new InvalidDataException("request_header");

            var executable = ReadString(reader);
            var cwd = ReadString(reader);
            if (string.IsNullOrWhiteSpace(executable) || executable.IndexOf('\0') >= 0 ||
                !Path.IsPathFullyQualified(executable) ||
                !string.Equals(Path.GetFullPath(executable), executable, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(cwd) || cwd.IndexOf('\0') >= 0 ||
                !Path.IsPathFullyQualified(cwd) ||
                !string.Equals(Path.GetFullPath(cwd), cwd, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("request_path");
            }

            var argumentCount = reader.ReadInt32();
            if (argumentCount is < 0 or > MaxArguments) throw new InvalidDataException("request_arguments");
            var arguments = new string[argumentCount];
            for (var index = 0; index < arguments.Length; index++)
            {
                arguments[index] = ReadString(reader);
                if (arguments[index].IndexOf('\0') >= 0) throw new InvalidDataException("request_argument");
            }

            var environmentCount = reader.ReadInt32();
            if (environmentCount is < 0 or > MaxEnvironmentEntries) throw new InvalidDataException("request_environment");
            var environment = new Dictionary<string, string>(environmentCount, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < environmentCount; index++)
            {
                var key = ReadString(reader);
                var value = ReadString(reader);
                if (string.IsNullOrEmpty(key) || key.IndexOf('\0') >= 0 || value.IndexOf('\0') >= 0 || !environment.TryAdd(key, value))
                    throw new InvalidDataException("request_environment");
            }

            if (stream.Position != stream.Length) throw new InvalidDataException("request_trailing");
            return new ProcessHostRequest(executable, cwd, arguments, environment);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length is < 0 or > MaxStringBytes) throw new InvalidDataException("request_string");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return StrictUtf8.GetString(bytes);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..]);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}

sealed class ProcessHostResponseWriter
{
    internal const byte StdoutFrame = 1;
    internal const byte StderrFrame = 2;
    private const byte CompletedFrame = 3;
    private const byte FailureFrame = 4;
    private const byte RootExitedFrame = 5;
    private const uint ResponseMagic = 0x31524850; // PHR1
    private const ushort Version = 1;
    private const int MaxFrameBytes = 16 * 1024;
    private readonly Stream _stream;
    private readonly object _gate = new();
    private bool _terminal;

    internal ProcessHostResponseWriter(Stream stream)
    {
        _stream = stream;
        Span<byte> header = stackalloc byte[sizeof(uint) + sizeof(ushort)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ResponseMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[sizeof(uint)..], Version);
        _stream.Write(header);
        _stream.Flush();
    }

    internal void Chunk(byte type, ReadOnlySpan<byte> payload)
    {
        if (type is not (StdoutFrame or StderrFrame) || payload.Length > MaxFrameBytes)
            throw new InvalidDataException("response_frame");
        lock (_gate)
        {
            if (_terminal) return;
            WriteFrame(type, payload);
        }
    }

    internal void Complete(int exitCode, bool stdoutTruncated, bool stderrTruncated)
    {
        Span<byte> payload = stackalloc byte[sizeof(int) + 2];
        BinaryPrimitives.WriteInt32LittleEndian(payload, exitCode);
        payload[sizeof(int)] = stdoutTruncated ? (byte)1 : (byte)0;
        payload[sizeof(int) + 1] = stderrTruncated ? (byte)1 : (byte)0;
        Terminal(CompletedFrame, payload);
    }

    internal void Failure(string code) => Terminal(FailureFrame, Encoding.UTF8.GetBytes(code));

    internal void RootExited(int exitCode, bool stdoutTruncated, bool stderrTruncated)
    {
        Span<byte> payload = stackalloc byte[sizeof(int) + 2];
        BinaryPrimitives.WriteInt32LittleEndian(payload, exitCode);
        payload[sizeof(int)] = stdoutTruncated ? (byte)1 : (byte)0;
        payload[sizeof(int) + 1] = stderrTruncated ? (byte)1 : (byte)0;
        Terminal(RootExitedFrame, payload);
    }

    private void Terminal(byte type, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (_terminal) return;
            _terminal = true;
            WriteFrame(type, payload);
        }
    }

    private void WriteFrame(byte type, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[sizeof(byte) + sizeof(int)];
        header[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(header[sizeof(byte)..], payload.Length);
        _stream.Write(header);
        _stream.Write(payload);
        _stream.Flush();
    }
}
