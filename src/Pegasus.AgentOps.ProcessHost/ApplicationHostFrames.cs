using System.Buffers.Binary;

namespace Pegasus.AgentOps.Internal;

// Linked into the two private endpoints; no additional deployed assembly or generic transport.
internal readonly record struct ApplicationHostFrame(byte Kind, int Pid, long StartTicks, int ExitCode)
{
    internal const byte Started = 1, RootExited = 2, Failed = 3;
    private const uint Magic = 0x31414850; // PHA1
    private const int Size = 23;
    internal async Task WriteAsync(Stream stream)
    {
        var bytes = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        bytes[6] = Kind;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(7), Pid);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(11), StartTicks);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(19), ExitCode);
        await stream.WriteAsync(bytes); await stream.FlushAsync();
    }
    internal static async Task<ApplicationHostFrame> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[Size]; await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) != 1 || bytes[6] is < Started or > Failed)
            throw new InvalidDataException("app_host_protocol");
        return new(bytes[6], BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(7)), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(11)), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(19)));
    }
}
