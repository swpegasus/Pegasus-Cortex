using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

/// <summary>Explicit, reviewed preparation of the four owner-session directories.
/// This never visits their children or the semantic database sibling.</summary>
public static class CortexExecutionStatePreparation
{
    public sealed record Receipt(string Contract, int Version, string InstallRoot, string DataRoot,
        string OwnerSid, IReadOnlyList<string> Paths, int Prepared);

    private const string EvidenceSchema = "pegasus.cortex.setup-fixed-four-metadata/1";
    private const string ReceiptContract = "pegasus-cortex-execution-state-preparation";
    private const int MaximumEvidenceBytes = 16384;

    public static Receipt Prepare(CortexProductLayout layout, string evidencePath, string evidenceSha256)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ArgumentNullException.ThrowIfNull(layout);
        layout.RequireRuntimePathsWithinInstallRoot();
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != layout.OwnerSid ||
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Execution-state preparation requires the elevated exact owner.");
        if (string.IsNullOrWhiteSpace(evidencePath) ||
            !Path.IsPathFullyQualified(evidencePath) ||
            evidencePath != Path.GetFullPath(evidencePath) ||
            evidenceSha256.Length != 64 ||
            !evidenceSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("An exact evidence path and SHA-256 pin are required.");
        if ((File.GetAttributes(evidencePath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The preparation evidence is a linked file.");
        byte[] bytes;
        using (var input = new FileStream(evidencePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length is <= 0 or > MaximumEvidenceBytes)
                throw new InvalidDataException("The preparation evidence is empty or oversized.");
            bytes = new byte[input.Length];
            input.ReadExactly(bytes);
        }
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), evidenceSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reviewed preparation evidence changed.");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.GetProperty("schema").GetString() != EvidenceSchema ||
            root.GetProperty("installRoot").GetString() != layout.InstallRoot ||
            root.GetProperty("dataRoot").GetString() != layout.DataRoot ||
            root.GetProperty("ownerSid").GetString() != layout.OwnerSid)
            throw new InvalidDataException("The evidence belongs to another installed layout.");
        var rows = root.GetProperty("directories");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != 4)
            throw new InvalidDataException("The exact four directory rows are required.");
        var paths = new[] { layout.UserStateDirectory, layout.UserExecutionDirectory,
            layout.UserLogDirectory, layout.TrayStateDirectory };
        var opened = new List<SafeFileHandle>(4);
        try
        {
            // Admit and pin every target before changing the first ACL. A missing,
            // linked, foreign-owned, or changed fourth target leaves all four intact.
            for (var i = 0; i < paths.Length; i++)
            {
                var row = rows[i];
                if (row.GetProperty("path").GetString() != paths[i] ||
                    row.GetProperty("pathSha256").GetString() != Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(paths[i]))).ToLowerInvariant() ||
                    !row.GetProperty("exists").GetBoolean() || row.GetProperty("file").GetBoolean() ||
                    row.GetProperty("linkTarget").ValueKind != JsonValueKind.Null)
                    throw new InvalidDataException("The reviewed directory selection changed.");
                var path = paths[i];
                if (!Directory.Exists(path) || File.Exists(path) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("An execution-state target is absent, linked, or not a directory.");
                opened.Add(OpenPinnedDirectory(path));
                Admit(path, layout.OwnerSid, row);
            }
            for (var i = 0; i < paths.Length; i++)
            {
                SetOwnerOnlyAcl(opened[i], paths[i], layout.OwnerSid, i);
            }
            return new Receipt(ReceiptContract, 1, layout.InstallRoot, layout.DataRoot,
                layout.OwnerSid, paths, paths.Length);
        }
        finally
        {
            for (var i = opened.Count - 1; i >= 0; i--) opened[i].Dispose();
        }
    }

    internal static SafeFileHandle OpenPinnedDirectory(string path)
    {
        var handle = OpenDirectory(path, 0x02000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!GetFileInformationByHandleEx(handle, 9, out var attributes, 8) ||
                (attributes.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
                (attributes.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
                throw new IOException("A pinned execution-state target is not an ordinary directory.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void SetOwnerOnlyAcl(SafeFileHandle handle, string path, string ownerSid, int completed)
    {
        var desired = CortexProductStateProvisioner.DescribeDirectorySecurity(new(
            path, ownerSid, CortexDirectoryAccessProfile.ExecutionOwnerPrivate));
        var descriptor = new RawSecurityDescriptor(desired.GetSecurityDescriptorBinaryForm(), 0);
        var dacl = new byte[descriptor.DiscretionaryAcl!.BinaryLength];
        descriptor.DiscretionaryAcl.GetBinaryForm(dacl, 0);
        // MAXIMUM_ALLOWED and no delete sharing follow the existing semantic-parent
        // single-directory handle path; no descendant is enumerated or opened.
        var error = SetSecurityInfo(handle, 1, 0x80000004, IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
        if (error != 0) throw new Win32Exception((int)error,
            $"Execution-state ACL preparation stopped after {completed} target(s).");
        CortexProductStateProvisioner.ValidateExecutionOwnerPrivate(path, ownerSid);
    }

    internal static void Admit(string path, string ownerSid, JsonElement row)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null ||
            security.GetOwner(typeof(SecurityIdentifier))?.Value != ownerSid ||
            security.AreAccessRulesProtected != row.GetProperty("protected").GetBoolean() ||
            new DirectoryInfo(path).Attributes.ToString() != row.GetProperty("attributes").GetString())
            throw new UnauthorizedAccessException("An execution-state ACL owner, inheritance, or type changed.");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        var expected = row.GetProperty("rules");
        var installerSid = ResolveInheritedInstallerSid();
        if (expected.ValueKind != JsonValueKind.Array || expected.GetArrayLength() != rules.Length ||
            descriptor.DiscretionaryAcl.Count != rules.Length)
            throw new UnauthorizedAccessException("An execution-state access rule changed.");
        for (var i = 0; i < rules.Length; i++)
        {
            if (descriptor.DiscretionaryAcl[i] is not CommonAce ace ||
                ace.AceType != AceType.AccessAllowed || ace.IsCallback)
                throw new UnauthorizedAccessException("An execution-state ACL has an unsupported rule.");
            var rule = rules[i];
            var value = expected[i];
            if (!SupportedInheritedRule(rule, ownerSid, installerSid) ||
                value.GetProperty("sid").GetString() != rule.IdentityReference.Value ||
                value.GetProperty("mask").GetInt32() != (int)rule.FileSystemRights ||
                value.GetProperty("type").GetString() != rule.AccessControlType.ToString() ||
                value.GetProperty("inherited").GetBoolean() != rule.IsInherited ||
                value.GetProperty("inheritance").GetString() != rule.InheritanceFlags.ToString() ||
                value.GetProperty("propagation").GetString() != rule.PropagationFlags.ToString())
                throw new UnauthorizedAccessException("The reviewed execution-state ACL rule changed.");
        }
        if (!rules.Any(rule => rule.IdentityReference.Value == ownerSid &&
                (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0))
            throw new UnauthorizedAccessException("The current owner lacks full access.");
    }

    // Program Files inherits this Windows servicing principal, not the Cortex service.
    internal static string ResolveInheritedInstallerSid() =>
        ((SecurityIdentifier)new NTAccount("NT SERVICE", "TrustedInstaller")
            .Translate(typeof(SecurityIdentifier))).Value;

    internal static bool SupportedInheritedRule(FileSystemAccessRule rule, string ownerSid, string installerSid)
    {
        return SupportedInheritedRule(rule.IdentityReference.Value, (int)rule.FileSystemRights,
            rule.AccessControlType == AccessControlType.Allow, rule.IsInherited,
            rule.InheritanceFlags, rule.PropagationFlags, ownerSid, installerSid);
    }

    internal static bool SupportedInheritedRule(string sid, int mask, bool allow, bool inherited,
        InheritanceFlags inheritance, PropagationFlags propagation, string ownerSid, string installerSid)
    {
        if (!allow || !inherited || propagation != PropagationFlags.None) return false;
        if (sid == installerSid)
            return mask == (int)FileSystemRights.FullControl &&
                inheritance == InheritanceFlags.ContainerInherit;
        if (sid is "S-1-15-2-1" or "S-1-15-2-2")
            return mask == (int)(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize) &&
                inheritance == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        if (sid == ownerSid || sid is "S-1-5-18" or "S-1-5-32-544")
            return mask == (int)FileSystemRights.FullControl &&
                inheritance == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { public uint FileAttributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint sharing, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileAttributeTagInfo information, uint size);
    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(SafeFileHandle handle, int objectType, uint information,
        IntPtr owner, IntPtr group, byte[] dacl, IntPtr sacl);
}
