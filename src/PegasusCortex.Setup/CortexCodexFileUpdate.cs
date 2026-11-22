using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

// Shared bounded, preimage-checked replacement for the two explicitly selected
// installing-user integration files. No backup contains unrelated user config.
internal sealed record CortexCodexFileUpdate(string Target, bool Existed, byte[] Original, byte[] Result)
{
    internal string? CreationOwnerSid { get; init; }
    internal bool Changed => !Original.AsSpan().SequenceEqual(Result);
    internal string Disposition => Changed ? Existed ? "updated" : "created" : "unchanged";
    internal string OriginalSha256 => Convert.ToHexStringLower(SHA256.HashData(Original));
    internal string ResultSha256 => Convert.ToHexStringLower(SHA256.HashData(Result));

    internal void Apply()
    {
        _ = RequireTarget(Target, Path.GetFileName(Target));
        if (!Changed)
        {
            if (File.Exists(Target) != Existed)
                throw new IOException("Codex integration changed during setup.");
            if (Existed)
            {
                using var source = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read);
                RequirePreimage(source);
            }
            return;
        }
        var directory = Path.GetDirectoryName(Target)!;
        CreateSelectedDirectory(directory);
        RequireDirectory(directory, allowMissing: false);
        var temporary = Path.Combine(directory, ".cortex-" + Guid.NewGuid().ToString("N") + ".tmp");
        if (temporary.Length > CortexProductLayout.MaximumWindowsPathCharacters)
            throw new PathTooLongException("The Codex integration staging path exceeds the product path budget.");
        var owned = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owned = true;
                output.Write(Result); output.Flush(flushToDisk: true);
            }
            if (CreationOwnerSid is not null)
            {
                var staged = new FileInfo(temporary);
                var security = staged.GetAccessControl(AccessControlSections.Owner);
                security.SetOwner(new SecurityIdentifier(CreationOwnerSid));
                staged.SetAccessControl(security);
            }
            _ = RequireTarget(Target, Path.GetFileName(Target));
            if (Existed)
            {
                // Keep the preimage open without write sharing until replacement.
                using var source = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                RequirePreimage(source);
                File.Replace(temporary, Target, destinationBackupFileName: null, ignoreMetadataErrors: false);
            }
            else File.Move(temporary, Target, overwrite: false);
            if (!ReadBounded(Target, Result.Length).AsSpan().SequenceEqual(Result))
                throw new IOException("The selected Codex integration did not preserve the composed bytes.");
        }
        finally { if (owned && File.Exists(temporary)) File.Delete(temporary); }
    }

    private void RequirePreimage(FileStream source)
    {
        if (source.Length != Original.Length) throw new IOException("Codex integration changed during setup.");
        var current = new byte[Original.Length]; source.ReadExactly(current);
        if (!current.AsSpan().SequenceEqual(Original)) throw new IOException("Codex integration changed during setup.");
    }

    private void CreateSelectedDirectory(string directory)
    {
        if (CreationOwnerSid is null) { Directory.CreateDirectory(directory); return; }
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != CreationOwnerSid)
            throw new UnauthorizedAccessException("Codex creation ownership must be the current installing user SID.");
        var missing = new Stack<DirectoryInfo>();
        var current = new DirectoryInfo(directory);
        while (!current.Exists)
        {
            missing.Push(current);
            current = current.Parent ?? throw new InvalidDataException("The Codex directory has no ordinary parent.");
        }
        while (missing.TryPop(out var next))
        {
            RequireDirectory(next.Parent!.FullName, allowMissing: false);
            // Native creation distinguishes our new directory from an existing
            // concurrent target. Preserve its inherited ACL; set only ownership.
            if (!CreateDirectoryNative(next.FullName, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The selected Codex directory could not be created exclusively.");
            var security = next.GetAccessControl(AccessControlSections.Owner);
            security.SetOwner(new SecurityIdentifier(CreationOwnerSid));
            next.SetAccessControl(security);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryNative(string path, IntPtr securityAttributes);

    internal static string RequireTarget(string path, string fileName)
    {
        var canonical = RequireCanonical(path);
        if (!string.Equals(Path.GetFileName(canonical), fileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Codex integration target filename is invalid.");
        RequireDirectory(Path.GetDirectoryName(canonical)!, allowMissing: true);
        var info = new FileInfo(canonical);
        if (Directory.Exists(canonical) || info.LinkTarget is not null || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The Codex integration target is not an ordinary file.");
        return canonical;
    }

    internal static string RequireCanonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("A canonical local Codex integration path is required.");
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(canonical, Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase) ||
            canonical.Length > CortexProductLayout.MaximumWindowsPathCharacters)
            throw new InvalidDataException("The Codex integration path is noncanonical or oversized.");
        var volume = Path.GetPathRoot(canonical)!;
        if (canonical.Length <= volume.Length) throw new InvalidDataException("A Codex integration target cannot be a volume root.");
        foreach (var segment in canonical[volume.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            if (segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 ||
                Encoding.UTF8.GetByteCount(segment) > 255 || Regex.IsMatch(segment, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("The Codex integration path contains an unsafe segment.");
        return canonical;
    }

    internal static void RequireDirectory(string path, bool allowMissing)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = new DirectoryInfo(current);
            if (File.Exists(current) || info.LinkTarget is not null || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0 || !allowMissing && !info.Exists)
                throw new InvalidDataException("A Codex integration directory is absent, non-directory, or a reparse point.");
        }
    }

    internal static byte[] ReadBounded(string target, int maximum)
    {
        if (!File.Exists(target)) return [];
        using var source = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length > maximum) throw new InvalidDataException("The Codex integration file exceeds its bounded size.");
        var bytes = new byte[checked((int)source.Length)]; source.ReadExactly(bytes); return bytes;
    }
}
