using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.Cortex.Product;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.Cortex.Setup;

public enum CortexDirectoryAccessProfile
{
    ImmutableReadExecute,
    StateTraverse,
    UserContainer,
    ServicePrivate,
    UserPrivate,
    ExecutionOwnerPrivate
}

public sealed record CortexDirectoryAclEntryV1(
    string Path,
    string OwnerSid,
    CortexDirectoryAccessProfile Profile);

public sealed record CortexDirectoryAclPlanV1(
    string Contract,
    int Version,
    IReadOnlyList<CortexDirectoryAclEntryV1> Entries);

public static class CortexProductStateProvisioner
{
    internal static IDisposable BeginSystemOwnedCreation() => WindowsPrivilegeScope.EnableRestorePrivilege();
    public const string Contract = "pegasus-cortex-directory-acl-plan";
    private const string SystemSid = "S-1-5-18";
    private const string AdministratorsSid = "S-1-5-32-544";
    private const string UsersSid = "S-1-5-32-545";
    private const string CreatorOwnerSid = "S-1-3-0";

    public static CortexDirectoryAclPlanV1 Plan(CortexProductLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        layout.RequireRuntimePathsWithinInstallRoot();
        var administrators = AdministratorsSid;
        var entries = new List<CortexDirectoryAclEntryV1>
        {
                new(layout.InstallRoot, administrators, CortexDirectoryAccessProfile.ImmutableReadExecute),
                new(layout.BinDirectory, administrators, CortexDirectoryAccessProfile.ImmutableReadExecute),
                new(layout.ToolsDirectory, administrators, CortexDirectoryAccessProfile.ImmutableReadExecute),
                new(Path.Combine(layout.InstallRoot, "manifests"), administrators, CortexDirectoryAccessProfile.ImmutableReadExecute),
                new(Path.Combine(layout.InstallRoot, "installer"), administrators, CortexDirectoryAccessProfile.ImmutableReadExecute),
                new(layout.StateDirectory, administrators, CortexDirectoryAccessProfile.StateTraverse),
                new(layout.InstallStateDirectory, administrators, CortexDirectoryAccessProfile.ImmutableReadExecute),
                new(layout.ServiceStateDirectory, SystemSid, CortexDirectoryAccessProfile.ServicePrivate),
                new(Path.Combine(layout.StateDirectory, "users"), administrators, CortexDirectoryAccessProfile.UserContainer),
                new(layout.UserStateDirectory, layout.OwnerSid, CortexDirectoryAccessProfile.ExecutionOwnerPrivate),
                new(Path.Combine(layout.DataRoot, "users", layout.OwnerSid, "cortex"), layout.OwnerSid, CortexDirectoryAccessProfile.UserPrivate),
                new(layout.CortexDataDirectory, layout.OwnerSid, CortexDirectoryAccessProfile.UserPrivate),
                new(layout.UserConfigDirectory, layout.OwnerSid, CortexDirectoryAccessProfile.UserPrivate),
                new(layout.UserBackupDirectory, layout.OwnerSid, CortexDirectoryAccessProfile.UserPrivate),
                new(layout.UserLogDirectory, layout.OwnerSid, CortexDirectoryAccessProfile.ExecutionOwnerPrivate),
                new(layout.UserReceiptDirectory, layout.OwnerSid, CortexDirectoryAccessProfile.UserPrivate),
                new(layout.UserExecutionDirectory, layout.OwnerSid, CortexDirectoryAccessProfile.ExecutionOwnerPrivate),
                new(layout.TrayStateDirectory, layout.OwnerSid, CortexDirectoryAccessProfile.ExecutionOwnerPrivate)
        };
        if (!string.Equals(layout.DataRoot, layout.StateDirectory, StringComparison.OrdinalIgnoreCase))
        {
            // Only the selected dedicated data root is provisioned. Program and
            // service ACLs never inherit a user's database write permissions.
            entries.InsertRange(10,
            [
                new(layout.DataRoot, administrators, CortexDirectoryAccessProfile.ImmutableReadExecute),
                new(Path.Combine(layout.DataRoot, "users"), administrators, CortexDirectoryAccessProfile.UserContainer),
                new(Path.Combine(layout.DataRoot, "users", layout.OwnerSid), layout.OwnerSid, CortexDirectoryAccessProfile.UserPrivate)
            ]);
        }
        return new CortexDirectoryAclPlanV1(Contract, 1, entries);
    }

    internal static void ValidateLegacySemanticParent(CortexProductLayout layout, bool requireWritable = true)
    {
        var semanticParent = new CortexDirectoryAclEntryV1(
            Path.Combine(layout.DataRoot, "users", layout.OwnerSid, "cortex"),
            layout.OwnerSid, CortexDirectoryAccessProfile.UserPrivate);
        RejectExistingReparseComponents(semanticParent.Path);
        if (File.Exists(semanticParent.Path)) throw new IOException("The semantic parent identifies a file.");
        if (Directory.Exists(semanticParent.Path))
        {
            CortexUserStateDirectories.RequireWindowsUserStateAccess(
                new DirectoryInfo(semanticParent.Path).GetAccessControl(), new SecurityIdentifier(layout.OwnerSid),
                requireWritable ? CortexUserStateAccess.Modify : CortexUserStateAccess.Read);
        }
    }

    public static void Provision(CortexProductLayout layout)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Cortex product ACL provisioning requires Windows.");
        var plan = Plan(layout);
        ValidateLegacySemanticParent(layout);
        using var restorePrivilege = WindowsPrivilegeScope.EnableRestorePrivilege();
        foreach (var entry in plan.Entries)
        {
            var root = Path.TrimEndingDirectorySeparator(layout.DataRoot);
            var mutable = string.Equals(entry.Path, root, StringComparison.OrdinalIgnoreCase) ||
                entry.Path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            ProvisionDirectory(entry, preserveExisting: mutable);
        }
    }

    internal static void ProvisionDirectory(CortexDirectoryAclEntryV1 entry, bool preserveExisting,
        Action<CortexDirectoryAclEntryV1>? apply = null)
    {
        RejectExistingReparseComponents(entry.Path);
        if (File.Exists(entry.Path)) throw new IOException("A Cortex directory path identifies a file.");
        if (Directory.Exists(entry.Path) && preserveExisting)
        {
            // Validate existing data without normalizing its Windows permissions.
            Validate(entry);
            return;
        }
        if (!Directory.Exists(entry.Path))
            new DirectoryInfo(entry.Path).Create(DescribeDirectorySecurity(entry));
        else (apply ?? Apply)(entry); // immutable program boundary only
        RejectExistingReparseComponents(entry.Path);
        Validate(entry);
    }

    internal static bool RequireLegacySemanticParentAdmission(DirectorySecurity security, string ownerSid)
    {
        var actualOwner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        if (actualOwner == ownerSid) return false;
        if (actualOwner is not (SystemSid or AdministratorsSid))
            throw new UnauthorizedAccessException("The exact semantic parent is owned by an unrecognized principal; setup will not adopt it.");
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null)
            throw new UnauthorizedAccessException("The legacy semantic parent has no private access list.");
        var ownerFullControl = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                rule.IdentityReference.Value != ownerSid && rule.IdentityReference.Value is not (SystemSid or AdministratorsSid))
                throw new UnauthorizedAccessException("The legacy semantic parent contains an unowned access rule; setup will not adopt it.");
            if (rule.IdentityReference.Value == ownerSid &&
                (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0) ownerFullControl = true;
        }
        if (!ownerFullControl)
            throw new UnauthorizedAccessException("The legacy semantic parent does not establish the intended user's full access.");
        return true;
    }

    private static void RepairLegacySemanticParent(CortexDirectoryAclEntryV1 entry)
    {
        var current = new DirectoryInfo(entry.Path).GetAccessControl();
        if (!RequireLegacySemanticParentAdmission(current, entry.OwnerSid)) return;
        // MAXIMUM_ALLOWED deliberately selects SetSecurityInfo's documented
        // non-propagating handle path: only this directory changes, never its
        // existing children or database ACLs. No subtree is enumerated/opened.
        using var handle = OpenDirectory(entry.Path, 0x02000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "The legacy semantic parent could not be opened safely.");
        if (!GetFileInformationByHandleEx(handle, 9, out var attributes, 8))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The legacy semantic parent attributes are unavailable.");
        if ((attributes.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
            (attributes.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new IOException("The legacy semantic parent is not an ordinary directory.");
        // Recheck admission while the no-delete-sharing handle pins this target.
        _ = RequireLegacySemanticParentAdmission(new DirectoryInfo(entry.Path).GetAccessControl(), entry.OwnerSid);
        var desired = new DirectorySecurity();
        desired.SetOwner(new SecurityIdentifier(entry.OwnerSid));
        desired.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(desired, SystemSid); AddFullControl(desired, AdministratorsSid); AddFullControl(desired, entry.OwnerSid);
        var descriptor = new RawSecurityDescriptor(desired.GetSecurityDescriptorBinaryForm(), 0);
        var sid = new byte[descriptor.Owner!.BinaryLength]; descriptor.Owner.GetBinaryForm(sid, 0);
        var acl = new byte[descriptor.DiscretionaryAcl!.BinaryLength]; descriptor.DiscretionaryAcl.GetBinaryForm(acl, 0);
        var error = SetSecurityInfo(handle, 1, 0x80000005, sid, IntPtr.Zero, acl, IntPtr.Zero);
        if (error != 0) throw new Win32Exception((int)error, "The exact legacy semantic parent could not be finalized.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { public uint FileAttributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out FileAttributeTagInfo information, uint size);
    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(SafeFileHandle handle, int objectType, uint information, byte[] owner, IntPtr group, byte[] dacl, IntPtr sacl);

    public static void SealServiceConfiguration(CortexProductLayout layout)
    {
        // This narrow helper remains available for fixtures and explicit
        // configuration repair. Production installation seals the complete
        // service-state ACL tree before the execution service is started.
        var path = Path.Combine(layout.ServiceStateDirectory, "service.v1.json");
        RejectExistingReparseComponents(path);
        using var restorePrivilege = WindowsPrivilegeScope.EnableRestorePrivilege();
        ApplyServiceFile(path);
        ValidateServiceEntry(path);
    }

    internal static void RequireInstallReceiptBoundary(CortexProductLayout layout)
    {
        var security = new DirectoryInfo(layout.InstallStateDirectory).GetAccessControl();
        if (!security.AreAccessRulesProtected ||
            security.GetOwner(typeof(SecurityIdentifier))?.Value is not (SystemSid or AdministratorsSid))
            throw new UnauthorizedAccessException("The install location receipt directory is not administrator-owned and protected.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IsInherited)
                throw new UnauthorizedAccessException("The install location receipt directory has unexpected access rules.");
            if (rule.IdentityReference.Value is SystemSid or AdministratorsSid) continue;
            if (rule.IdentityReference.Value != UsersSid ||
                (rule.FileSystemRights & ~(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) != 0)
                throw new UnauthorizedAccessException("The install location receipt directory is writable by an unowned principal.");
        }
    }

    public static void EnsureServiceConfiguration(CortexProductLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        layout.RequireRuntimePathsWithinDeclaredRoots();
        var directory = new CortexDirectoryAclEntryV1(layout.ServiceStateDirectory,
            SystemSid, CortexDirectoryAccessProfile.ServicePrivate);
        RejectExistingReparseComponents(directory.Path);
        if (!layout.IsIsolatedFixture)
        {
            using var privilege = WindowsPrivilegeScope.EnableRestorePrivilege();
            if (!Directory.Exists(directory.Path))
                new DirectoryInfo(directory.Path).Create(DescribeDirectorySecurity(directory));
            Validate(directory);
        }
        else Directory.CreateDirectory(directory.Path);

        var path = Path.Combine(directory.Path, "service.v1.json");
        RejectExistingReparseComponents(path);
        if (!File.Exists(path))
        {
            // Create only the missing default. Never reseal an existing service
            // tree or rewrite the ACL/owner of retained SQLite companions.
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                System.Text.Json.JsonSerializer.Serialize(stream, new
                {
                    contract = "pegasus-cortex-execution-service", version = 1, ownerSid = layout.OwnerSid
                });
                stream.Flush(flushToDisk: true);
            }
            if (!layout.IsIsolatedFixture)
            {
                using var privilege = WindowsPrivilegeScope.EnableRestorePrivilege();
                ApplyServiceFile(path);
            }
        }
        var info = new FileInfo(path);
        if (info.Length is < 1 or > 16384)
            throw new InvalidDataException("The existing service configuration is absent or oversized.");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var value = document.RootElement;
        if (value.ValueKind != System.Text.Json.JsonValueKind.Object ||
            value.EnumerateObject().Count() != 3 ||
            !value.TryGetProperty("contract", out var contract) || contract.ValueKind != System.Text.Json.JsonValueKind.String ||
            contract.GetString() != "pegasus-cortex-execution-service" ||
            !value.TryGetProperty("version", out var version) || version.ValueKind != System.Text.Json.JsonValueKind.Number ||
            !version.TryGetInt32(out var number) || number != 1 ||
            !value.TryGetProperty("ownerSid", out var owner) || owner.ValueKind != System.Text.Json.JsonValueKind.String ||
            owner.GetString() != layout.OwnerSid)
            throw new InvalidDataException("The existing service configuration conflicts with this installation; it was preserved.");
        if (!layout.IsIsolatedFixture)
        {
            var security = info.GetAccessControl();
            if (security.GetOwner(typeof(SecurityIdentifier))?.Value is not (SystemSid or AdministratorsSid) ||
                new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null)
                throw new UnauthorizedAccessException("The existing service configuration has no trusted administrative owner and access list.");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow &&
                    rule.IdentityReference.Value is not (SystemSid or AdministratorsSid))
                    throw new UnauthorizedAccessException("The existing service configuration has an unexpected grant.");
        }
    }

    public static void SealServiceState(CortexProductLayout layout)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Cortex service-state ACL sealing requires Windows.");
        ArgumentNullException.ThrowIfNull(layout);
        layout.RequireRuntimePathsWithinInstallRoot();
        var root = layout.ServiceStateDirectory;
        RejectExistingReparseComponents(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The Cortex service-state root is absent.");

        // Upgrade compatibility is an ACL-only operation. It enumerates bounded
        // names, attributes, and security descriptors, but never opens, copies,
        // seeds, migrates, or rewrites database contents.
        var entries = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (entries.Count >= 100_000)
                    throw new IOException("The Cortex service-state tree exceeds its bounded entry count.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The Cortex service-state tree contains a reparse point.");
                entries.Add(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }

        using var restorePrivilege = WindowsPrivilegeScope.EnableRestorePrivilege();
        Apply(new CortexDirectoryAclEntryV1(root, SystemSid, CortexDirectoryAccessProfile.ServicePrivate));
        foreach (var entry in entries.OrderBy(value => value.Count(character => character == Path.DirectorySeparatorChar)))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
                Apply(new CortexDirectoryAclEntryV1(entry, SystemSid, CortexDirectoryAccessProfile.ServicePrivate));
            else
                ApplyServiceFile(entry);
        }
        Validate(new CortexDirectoryAclEntryV1(root, SystemSid, CortexDirectoryAccessProfile.ServicePrivate));
        foreach (var entry in entries)
            ValidateServiceEntry(entry);
    }

    internal static void RequireServiceEntryAccess(FileSystemSecurity security)
    {
        if (security.GetOwner(typeof(SecurityIdentifier))?.Value is not (SystemSid or AdministratorsSid) ||
            new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null)
            throw new UnauthorizedAccessException("Service data requires a trusted administrative owner and access list.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Value is not (SystemSid or AdministratorsSid))
                throw new UnauthorizedAccessException("Privileged service data grants access to an ordinary principal.");
    }

    private static void Apply(CortexDirectoryAclEntryV1 entry)
        => new DirectoryInfo(entry.Path).SetAccessControl(DescribeDirectorySecurity(entry));

    internal static DirectorySecurity DescribeDirectorySecurity(CortexDirectoryAclEntryV1 entry)
    {
        if (entry.Profile is CortexDirectoryAccessProfile.UserPrivate or CortexDirectoryAccessProfile.ExecutionOwnerPrivate)
            return CortexUserStateDirectories.CreateWindowsUserDirectorySecurity(new SecurityIdentifier(entry.OwnerSid));
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(entry.OwnerSid));
        if (entry.Profile != CortexDirectoryAccessProfile.ExecutionOwnerPrivate)
        {
            AddFullControl(security, SystemSid);
            AddFullControl(security, AdministratorsSid);
        }
        switch (entry.Profile)
        {
            case CortexDirectoryAccessProfile.ImmutableReadExecute:
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(UsersSid),
                    FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                break;
            case CortexDirectoryAccessProfile.StateTraverse:
                // Browse the container without inheriting these grants into
                // private user or privileged service children.
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(UsersSid),
                    FileSystemRights.Traverse | FileSystemRights.ListDirectory |
                    FileSystemRights.ReadAttributes |
                    FileSystemRights.ReadExtendedAttributes |
                    FileSystemRights.ReadPermissions |
                    FileSystemRights.Synchronize,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                break;
            case CortexDirectoryAccessProfile.UserContainer:
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(UsersSid),
                    FileSystemRights.Traverse | FileSystemRights.ListDirectory | FileSystemRights.ReadPermissions | FileSystemRights.ReadAttributes |
                    FileSystemRights.ReadExtendedAttributes | FileSystemRights.CreateDirectories |
                    FileSystemRights.Synchronize,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
                // Users may create their own private first-use folder, but cannot
                // read/write another user's child or write files in this container.
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(CreatorOwnerSid), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.InheritOnly, AccessControlType.Allow));
                break;
            case CortexDirectoryAccessProfile.UserPrivate:
                AddFullControl(security, entry.OwnerSid);
                break;
            case CortexDirectoryAccessProfile.ExecutionOwnerPrivate:
                AddFullControl(security, entry.OwnerSid);
                break;
            case CortexDirectoryAccessProfile.ServicePrivate:
                break;
            default:
                throw new InvalidDataException("The Cortex ACL profile is unsupported.");
        }
        return security;
    }

    private static void Validate(CortexDirectoryAclEntryV1 entry)
    {
        RequireDirectoryAccess(new DirectoryInfo(entry.Path).GetAccessControl(), entry);
    }

    internal static void RequireDirectoryAccess(DirectorySecurity security, CortexDirectoryAclEntryV1 entry)
    {
        if (entry.Profile is CortexDirectoryAccessProfile.UserPrivate or CortexDirectoryAccessProfile.ExecutionOwnerPrivate)
        {
            CortexUserStateDirectories.RequireWindowsUserStateAccess(security, new SecurityIdentifier(entry.OwnerSid));
            return;
        }
        if (security.GetOwner(typeof(SecurityIdentifier))?.Value is not (SystemSid or AdministratorsSid) ||
            new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null)
            throw new UnauthorizedAccessException("A Cortex shared directory has no trusted administrative owner and access list.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { SystemSid, AdministratorsSid };
        if (entry.Profile is CortexDirectoryAccessProfile.ImmutableReadExecute or CortexDirectoryAccessProfile.StateTraverse or CortexDirectoryAccessProfile.UserContainer)
            allowed.Add(UsersSid);
        if (entry.Profile == CortexDirectoryAccessProfile.UserContainer)
            allowed.Add(CreatorOwnerSid);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     targetType: typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                !allowed.Contains(rule.IdentityReference.Value))
            {
                throw new UnauthorizedAccessException("A Cortex directory ACL contains an unowned access rule.");
            }
            if (rule.IdentityReference.Value == UsersSid)
            {
                var rights = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
                if (entry.Profile == CortexDirectoryAccessProfile.UserContainer) rights |= FileSystemRights.CreateDirectories;
                if ((rule.FileSystemRights & ~rights) != 0 ||
                    (entry.Profile is CortexDirectoryAccessProfile.StateTraverse or CortexDirectoryAccessProfile.UserContainer &&
                     (rule.InheritanceFlags != InheritanceFlags.None || rule.PropagationFlags != PropagationFlags.None)))
                    throw new UnauthorizedAccessException("A Cortex shared container grants ordinary-user write or inherited child access.");
            }
        }
    }

    internal static void ValidateExecutionOwnerPrivate(string path, string ownerSid) =>
        Validate(new CortexDirectoryAclEntryV1(path, ownerSid,
            CortexDirectoryAccessProfile.ExecutionOwnerPrivate));

    private static void AddFullControl(DirectorySecurity security, string sid) =>
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sid),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static void ApplyServiceFile(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(SystemSid));
        AddFullControl(security, SystemSid);
        AddFullControl(security, AdministratorsSid);
        new FileInfo(path).SetAccessControl(security);
    }

    private static void AddFullControl(FileSecurity security, string sid) =>
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sid),
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static void ValidateServiceEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        FileSystemSecurity security = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        if (!security.AreAccessRulesProtected ||
            !string.Equals(
                security.GetOwner(typeof(SecurityIdentifier))?.Value,
                SystemSid,
                StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("A Cortex service-state entry owner or inheritance boundary is invalid.");
        }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     targetType: typeof(SecurityIdentifier)))
        {
            if (rule.IsInherited ||
                rule.AccessControlType != AccessControlType.Allow ||
                rule.IdentityReference.Value is not (SystemSid or AdministratorsSid))
            {
                throw new UnauthorizedAccessException("A Cortex service-state entry contains an unowned access rule.");
            }
        }
    }

    internal static void RejectExistingReparseComponents(string path)
    {
        var canonical = Path.GetFullPath(path);
        var root = Path.GetPathRoot(canonical) ??
            throw new InvalidDataException("A Cortex product path has no filesystem root.");
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, canonical).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A Cortex product path crosses a reparse point.");
        }
    }

    /// <summary>
    /// An elevated administrator token contains SeRestorePrivilege in a disabled
    /// state. Windows requires it when the installer assigns SYSTEM or the
    /// Administrators group as an owner. Enable it only around the bounded ACL
    /// transaction, then restore the token's prior privilege state.
    /// </summary>
    private sealed class WindowsPrivilegeScope : IDisposable
    {
        private const uint TokenAdjustPrivileges = 0x0020;
        private const uint TokenQuery = 0x0008;
        private const uint SePrivilegeEnabled = 0x0002;
        private const string SeRestorePrivilege = "SeRestorePrivilege";

        private IntPtr _token;
        private TokenPrivileges _previous;

        private WindowsPrivilegeScope(IntPtr token, TokenPrivileges previous)
        {
            _token = token;
            _previous = previous;
        }

        public static WindowsPrivilegeScope EnableRestorePrivilege()
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The setup process token could not be opened.");

            try
            {
                if (!LookupPrivilegeValue(null, SeRestorePrivilege, out var luid))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The Windows restore privilege is unavailable.");

                var desired = new TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SePrivilegeEnabled
                };
                if (!AdjustTokenPrivileges(
                        token,
                        disableAllPrivileges: false,
                        ref desired,
                        (uint)Marshal.SizeOf<TokenPrivileges>(),
                        out var previous,
                        out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The Windows restore privilege could not be enabled.");
                }

                var adjustmentError = Marshal.GetLastWin32Error();
                if (adjustmentError != 0)
                    throw new Win32Exception(adjustmentError, "The elevated token does not hold the Windows restore privilege.");

                return new WindowsPrivilegeScope(token, previous);
            }
            catch
            {
                _ = CloseHandle(token);
                throw;
            }
        }

        public void Dispose()
        {
            if (_token == IntPtr.Zero)
                return;

            var previous = _previous;
            _ = AdjustTokenPrivileges(
                _token,
                disableAllPrivileges: false,
                ref previous,
                bufferLength: 0,
                IntPtr.Zero,
                IntPtr.Zero);
            _ = CloseHandle(_token);
            _token = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges
        {
            public uint PrivilegeCount;
            public Luid Luid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out IntPtr tokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(
            string? systemName,
            string name,
            out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(
            IntPtr tokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TokenPrivileges newState,
            uint bufferLength,
            out TokenPrivileges previousState,
            out uint returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(
            IntPtr tokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TokenPrivileges newState,
            uint bufferLength,
            IntPtr previousState,
            IntPtr returnLength);
    }
}
