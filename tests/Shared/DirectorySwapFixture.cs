using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.Cortex.Tests.IO;

/// <summary>All aliases, destinations and destructive cleanup stay in one uniquely owned scratch tree.</summary>
internal sealed class DirectorySwapFixture : IDisposable
{
    internal string Root { get; }
    internal string Project { get; }
    internal string Outside { get; }
    internal DirectorySwapFixture()
    {
        Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pct-swap-" + Guid.NewGuid().ToString("N")));
        Project = Path.Combine(Root, "project"); Outside = Path.Combine(Root, "outside");
        Directory.CreateDirectory(Project); Directory.CreateDirectory(Outside);
    }

    internal static IEnumerable<string> AliasKinds => OperatingSystem.IsWindows()
        ? new[] { "junction", "symlink" } : new[] { "symlink" };

    internal bool ProbeAlias(string kind)
    {
        var alias = Path.Combine(Root, "probe");
        try { CreateAlias(alias, Outside, kind); RemoveAlias(alias); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException or Win32Exception)
        {
            if (Directory.Exists(alias) || File.Exists(alias)) RemoveAlias(alias);
            Console.WriteLine("SKIP " + kind + ": scratch alias creation is unavailable (" + error.GetType().Name + ").");
            return false;
        }
    }

    internal void CreateAlias(string alias, string target, string kind)
    {
        CheckOwned(alias); CheckOwned(target);
        if (kind == "junction") CreateJunction(alias, target);
        else Directory.CreateSymbolicLink(alias, target);
    }

    internal bool TrySwap(string path, string retained, string kind)
    {
        CheckOwned(path); CheckOwned(retained);
        try { Directory.Move(path, retained); }
        catch (IOException) when (OperatingSystem.IsWindows()) { return false; }
        try { CreateAlias(path, Outside, kind); }
        catch { Directory.Move(retained, path); throw; }
        return true;
    }

    internal void Restore(string path, string retained)
    {
        CheckOwned(path); CheckOwned(retained);
        if (!Directory.Exists(retained)) return;
        RemoveAlias(path);
        Directory.Move(retained, path);
    }

    internal void RemoveAlias(string path)
    {
        CheckOwned(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0)
            throw new IOException("The scratch alias was replaced by an ordinary object.");
        if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, recursive: false);
        else File.Delete(path);
    }

    internal void CheckOwned(string path)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(full, Root, comparison) && !full.StartsWith(Root + Path.DirectorySeparatorChar, comparison))
            throw new IOException("The fixture path is outside its exact scratch root.");
    }

    public void Dispose()
    {
        CheckOwned(Root);
        if (Directory.Exists(Root)) DeleteNoFollow(Root);
    }

    private void DeleteNoFollow(string path)
    {
        CheckOwned(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) { RemoveAlias(path); return; }
        if ((attributes & FileAttributes.Directory) == 0)
        { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); return; }
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteNoFollow(child);
        Directory.Delete(path, recursive: false);
    }

    private static void CreateJunction(string alias, string target)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(alias);
        var success = false;
        try
        {
            using var handle = CreateFileW(alias, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var substitute = Encoding.Unicode.GetBytes(target.StartsWith("\\\\", StringComparison.Ordinal)
                ? "\\??\\UNC\\" + target[2..] : "\\??\\" + target);
            var print = Encoding.Unicode.GetBytes(target);
            var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xa0000003);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
            substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 18 + substitute.Length);
            if (!DeviceIoControl(handle, 0x900a4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            success = true;
        }
        finally { if (!success) Directory.Delete(alias, recursive: false); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength,
        IntPtr output, int outputLength, out int returned, IntPtr overlapped);
}
