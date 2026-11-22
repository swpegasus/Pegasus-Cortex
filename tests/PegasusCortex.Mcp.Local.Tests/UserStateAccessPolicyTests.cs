using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Product;

internal static class UserStateAccessPolicyTests
{
    internal static string? RetainedFixtureRoot { get; private set; }

    internal static Task DescriptorAdmissionAsync()
    {
        var user = new SecurityIdentifier("S-1-5-21-101-202-303-1001");
        const string other = "S-1-5-21-101-202-303-1002";
        var memberOf = new HashSet<string>(StringComparer.Ordinal)
            { user.Value, "S-1-1-0", "S-1-5-11", "S-1-5-32-545", "S-1-5-21-101-202-303-2001" };

        Accept(DirectoryAcl($"O:{user.Value}D:P(A;;FA;;;{user.Value})"));
        Accept(DirectoryAcl($"O:{user.Value}D:P(A;;0x1301bf;;;{user.Value})(A;;FA;;;SY)(A;;FA;;;BA)"));
        Accept(DirectoryAcl($"O:SYD:(A;CIID;0x1301bf;;;{user.Value})(A;CIID;FA;;;SY)(A;CIID;FA;;;BA)"));
        Accept(DirectoryAcl($"O:BAD:P(A;;FA;;;BA)(A;;0x1301bf;;;{user.Value})(A;;FA;;;SY)"));
        Accept(DirectoryAcl($"O:{user.Value}D:P(D;;WDWO;;;{user.Value})(A;;0x1301bf;;;{user.Value})"));
        Accept(DirectoryAcl($"O:{user.Value}D:P(D;;FR;;;{other})(A;;FA;;;{user.Value})"));
        Accept(FileAcl($"O:BAD:(A;ID;0x13019f;;;{user.Value})(A;ID;FA;;;BA)(A;ID;FA;;;SY)"));
        Accept(DirectoryAcl($"O:{user.Value}D:P(A;;GA;;;{user.Value})"));

        var readOnlyFile = FileAcl($"O:{user.Value}D:P(A;;FR;;;{user.Value})(A;;FA;;;SY)(A;;FA;;;BA)");
        CheckUnchanged(readOnlyFile, shouldAccept: true, CortexUserStateAccess.Read);
        CheckUnchanged(readOnlyFile, shouldAccept: false, CortexUserStateAccess.Modify);
        var readOnlyDirectory = DirectoryAcl($"O:BAD:(A;CIID;FRFX;;;{user.Value})(A;CIID;FA;;;SY)(A;CIID;FA;;;BA)");
        CheckUnchanged(readOnlyDirectory, shouldAccept: true, CortexUserStateAccess.Read);
        CheckUnchanged(readOnlyDirectory, shouldAccept: false, CortexUserStateAccess.Modify);
        foreach (var grant in new[] { "FR", "FW" })
            CheckUnchanged(FileAcl($"O:{user.Value}D:P(A;;FR;;;{user.Value})(A;;{grant};;;{other})"),
                shouldAccept: false, CortexUserStateAccess.Read);

        Reject(DirectoryAcl($"O:{other}D:P(A;;FA;;;{user.Value})"));
        Reject(DirectoryAcl($"O:{user.Value}D:NO_ACCESS_CONTROL"));
        Reject(DirectoryAcl($"O:{user.Value}D:P(A;;FR;;;{user.Value})(A;;FA;;;BA)"));
        Reject(DirectoryAcl("O:BAD:P(A;;FA;;;BA)(A;;FA;;;SY)"));
        Reject(DirectoryAcl($"O:{user.Value}D:P(A;CIIO;FA;;;{user.Value})(A;;FA;;;SY)"));
        foreach (var principal in new[] { other, "BU", "AU", "WD" })
        {
            Reject(DirectoryAcl($"O:{user.Value}D:P(A;;FA;;;{user.Value})(A;;FR;;;{principal})"));
            Reject(DirectoryAcl($"O:{user.Value}D:P(A;;FA;;;{user.Value})(A;CIIO;FW;;;{principal})"));
        }
        Reject(DirectoryAcl($"O:{user.Value}D:P(D;;FW;;;{user.Value})(A;;FA;;;{user.Value})"));
        Reject(DirectoryAcl($"O:{user.Value}D:P(D;;FR;;;WD)(A;;FA;;;{user.Value})"));
        Reject(DirectoryAcl($"O:{user.Value}D:P(D;;FW;;;S-1-3-4)(A;;FA;;;{user.Value})"));
        Reject(DirectoryAcl($"O:{user.Value}D:P(D;;FR;;;S-1-5-21-101-202-303-2001)(A;;FA;;;{user.Value})"));

        AssertCreation(CortexUserStateDirectories.CreateWindowsUserDirectorySecurity(user));
        AssertCreation(CortexUserStateDirectories.CreateWindowsUserFileSecurity(user));
        return Task.CompletedTask;

        void Accept(FileSystemSecurity security) => CheckUnchanged(security, shouldAccept: true);
        void Reject(FileSystemSecurity security) => CheckUnchanged(security, shouldAccept: false);
        void CheckUnchanged(FileSystemSecurity security, bool shouldAccept,
            CortexUserStateAccess requiredAccess = CortexUserStateAccess.Modify)
        {
            var before = security.GetSecurityDescriptorBinaryForm();
            var accepted = true;
            try { CortexUserStateSecurity.RequireAccess(security, user, memberOf, groupsKnown: true, requiredAccess); }
            catch (UnauthorizedAccessException) { accepted = false; }
            LocalAssert.Equal(shouldAccept, accepted, "User-state descriptor admission differed.");
            LocalAssert.True(before.SequenceEqual(security.GetSecurityDescriptorBinaryForm()),
                "Descriptor admission rewrote existing permissions.");
        }
        void AssertCreation(FileSystemSecurity security)
        {
            Accept(security);
            var principals = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Where(rule => rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
                .Select(rule => rule.IdentityReference.Value).ToHashSet(StringComparer.Ordinal);
            LocalAssert.True(principals.SetEquals([user.Value, "S-1-5-18", "S-1-5-32-544"]),
                "Fresh user state omitted normal owner, SYSTEM or Administrator access.");
        }
    }

    internal static async Task OwnedFilesystemAdmissionAsync()
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows user unavailable.");
        var root = Path.Combine(Path.GetTempPath(), "cortex-uacl-" + Guid.NewGuid().ToString("N"));
        RetainedFixtureRoot = root;
        new DirectoryInfo(root).Create(CortexUserStateDirectories.CreateWindowsUserDirectorySecurity(user));

        var legacy = Path.Combine(root, "owner-only");
        new DirectoryInfo(legacy).Create(DirectoryAcl($"O:{user.Value}D:P(A;;FA;;;{user.Value})"));
        AdmitUnchanged(legacy);

        var normal = Path.Combine(root, "normal-modify");
        new DirectoryInfo(normal).Create(DirectoryAcl(
            $"O:{user.Value}D:P(A;OICI;0x1301bf;;;{user.Value})(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"));
        AdmitUnchanged(normal);
        var inherited = Path.Combine(normal, "inherited");
        Directory.CreateDirectory(inherited);
        LocalAssert.False(new DirectoryInfo(inherited).GetAccessControl().AreAccessRulesProtected,
            "Fixture did not exercise inherited access.");
        AdmitUnchanged(inherited);

        var registryFile = Path.Combine(inherited, "registry-fixture.json");
        File.WriteAllText(registryFile, "retained fixture bytes");
        // This existing file deliberately lacks WRITE_OWNER. Fixture setup
        // changes only its DACL; its actual owner and primary group stay intact.
        var fileAccess = new FileSecurity();
        fileAccess.SetSecurityDescriptorSddlForm(
            $"D:P(A;;0x13019f;;;{user.Value})(A;;FA;;;SY)(A;;FA;;;BA)", AccessControlSections.Access);
        try { new FileInfo(registryFile).SetAccessControl(fileAccess); }
        catch (UnauthorizedAccessException error)
        { throw new UnauthorizedAccessException("fixture_stage=modify_file_dacl", error); }
        var fileAcl = new FileInfo(registryFile).GetAccessControl().GetSecurityDescriptorBinaryForm();
        var fileBytes = File.ReadAllBytes(registryFile);
        CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(registryFile);
        LocalAssert.True(fileAcl.SequenceEqual(new FileInfo(registryFile).GetAccessControl().GetSecurityDescriptorBinaryForm()) &&
            fileBytes.SequenceEqual(File.ReadAllBytes(registryFile)), "Registry admission changed its existing file.");

        var fresh = Path.Combine(root, "fresh");
        CortexUserStateDirectories.EnsureCurrentUserDirectory(fresh);
        var freshRules = new DirectoryInfo(fresh).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(rule => rule.IdentityReference.Value).ToHashSet(StringComparer.Ordinal);
        LocalAssert.True(freshRules.SetEquals([user.Value, "S-1-5-18", "S-1-5-32-544"]),
            "First-use directory creation omitted normal privileged access.");

        var occupied = Path.Combine(root, "foreign-read");
        new DirectoryInfo(occupied).Create(DirectoryAcl($"O:{user.Value}D:P(A;;FA;;;{user.Value})(A;;FR;;;BU)"));
        var marker = Path.Combine(occupied, "untouched.txt");
        File.WriteAllText(marker, "do not adopt");
        var before = new DirectoryInfo(occupied).GetAccessControl().GetSecurityDescriptorBinaryForm();
        var rejected = false;
        try { CortexUserStateDirectories.EnsureCurrentUserDirectory(occupied); }
        catch (UnauthorizedAccessException) { rejected = true; }
        LocalAssert.True(rejected && before.SequenceEqual(new DirectoryInfo(occupied).GetAccessControl().GetSecurityDescriptorBinaryForm()) &&
            File.ReadAllText(marker) == "do not adopt" && Directory.GetFileSystemEntries(occupied).Length == 1,
            "Unsafe occupied-directory refusal changed existing permissions or data.");
        await ReadOnlyUserStateTests.RunAsync(Path.Combine(root, "readers"), user);
        // Only the new GUID-owned fixture above is used; retain it for review.

        static void AdmitUnchanged(string directory)
        {
            var marker = Path.Combine(directory, "retained.txt");
            File.WriteAllText(marker, "existing owner data");
            var before = new DirectoryInfo(directory).GetAccessControl().GetSecurityDescriptorBinaryForm();
            var fileBefore = new FileInfo(marker).GetAccessControl().GetSecurityDescriptorBinaryForm();
            CortexUserStateDirectories.EnsureCurrentUserDirectory(directory);
            CortexLocalRegistryAclPolicy.RequireProtectedDirectory(directory);
            LocalAssert.True(before.SequenceEqual(new DirectoryInfo(directory).GetAccessControl().GetSecurityDescriptorBinaryForm()) &&
                fileBefore.SequenceEqual(new FileInfo(marker).GetAccessControl().GetSecurityDescriptorBinaryForm()) &&
                File.ReadAllText(marker) == "existing owner data", "Safe occupied-directory admission resealed existing state.");
        }
    }

    private static DirectorySecurity DirectoryAcl(string sddl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return security;
    }

    private static FileSecurity FileAcl(string sddl)
    {
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(sddl);
        return security;
    }
}
