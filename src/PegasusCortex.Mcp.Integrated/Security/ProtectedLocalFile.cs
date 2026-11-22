using System.Text;

namespace Pegasus.Cortex.Mcp.Integrated.Security;

internal static class ProtectedLocalFile
{
    public static byte[] ReadAllBytes(string path, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("A fully qualified protected-file path is required.");
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.Length is < 1 || info.Length > maximumBytes)
        {
            throw new InvalidOperationException("The protected local file failed its bounded path or size check.");
        }

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(fullPath);
            const UnixFileMode rejected =
                UnixFileMode.GroupWrite |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherWrite |
                UnixFileMode.OtherExecute;
            if ((mode & rejected) != 0)
                throw new InvalidOperationException("The protected local file has unsafe Unix permissions.");
        }

        using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadBounded(input, maximumBytes);
    }

    internal static byte[] ReadBounded(Stream input, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        // The opened object supplies the allocation bound; a prior path stat is
        // only path admission and must never authorize a later unbounded read.
        var length = input.Length;
        if (length is < 1 || length > maximumBytes)
            throw new InvalidOperationException("The protected local file failed its opened-handle size check.");
        var bytes = new byte[checked((int)length)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1 || input.Length != length)
            throw new InvalidOperationException("The protected local file changed during its bounded read.");
        return bytes;
    }

    public static string ReadUtf8(string path, int maximumBytes)
    {
        var bytes = ReadAllBytes(path, maximumBytes);
        try
        {
            return new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }
}
