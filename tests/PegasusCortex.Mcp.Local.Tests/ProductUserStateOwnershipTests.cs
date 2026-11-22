using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Product;

internal static class ProductUserStateOwnershipTests
{
    public static Task OwnerAdmissionPrecedesProtectionAsync()
    {
        var owner = new SecurityIdentifier("S-1-5-21-101-202-303-1001");
        var other = new SecurityIdentifier("S-1-5-21-101-202-303-1002");
        var admitted = Security(owner, owner, "S-1-5-18", "S-1-5-32-544");
        CortexUserStateDirectories.RequireUserStateDirectoryAclForProtection(admitted, owner);

        var inherited = new DirectorySecurity();
        inherited.SetSecurityDescriptorSddlForm(
            $"O:{owner.Value}D:(A;CIID;FA;;;{owner.Value})(A;CIID;FA;;;SY)(A;CIID;FA;;;BA)");
        CortexUserStateDirectories.RequireUserStateDirectoryAclForProtection(inherited, owner);

        var foreignOwner = Security(other, owner, other.Value);
        RejectUnchanged(foreignOwner, owner, "Another user's precreated directory was admitted.");

        var foreignAccess = Security(owner, owner, other.Value);
        RejectUnchanged(foreignAccess, owner, "A directory retaining another user's access was admitted.");

        var broadAccess = Security(owner, owner, "S-1-5-32-545");
        RejectUnchanged(broadAccess, owner, "A directory retaining ordinary-user access was admitted.");

        var nullDacl = new DirectorySecurity();
        nullDacl.SetSecurityDescriptorSddlForm($"O:{owner.Value}D:NO_ACCESS_CONTROL");
        RejectUnchanged(nullDacl, owner, "An unrestricted null DACL was admitted.");

        PrivateCreationRejectsInheritedAccessAndPreservesOccupiedDirectories();

        return Task.CompletedTask;
    }

    private static void PrivateCreationRejectsInheritedAccessAndPreservesOccupiedDirectories()
    {
        var current = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The test user's Windows SID is unavailable.");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cortex-owner-" + Guid.NewGuid().ToString("N")));
        var fixtureSecurity = Security(current, current, "S-1-5-32-545");
        fixtureSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        new DirectoryInfo(root).Create(fixtureSecurity);
        try
        {
            var child = Path.Combine(root, "private");
            var layout = Layout(child);
            layout.EnsureCreated();
            var protectedBefore = new DirectoryInfo(child).GetAccessControl()
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
            layout.EnsureCreated();
            CortexLocalRegistryAclPolicy.RequireProtectedDirectory(child);
            LocalAssert.Equal(protectedBefore, new DirectoryInfo(child).GetAccessControl()
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner),
                "Repeated first-use initialization changed the protected directory descriptor.");

            var executionLayout = CortexProductLayout.ForIsolatedRoots(
                Path.Combine(root, "program"), Path.Combine(root, "unavailable-data"), current.Value);
            CortexUserStateDirectories.EnsureExecution(executionLayout);
            foreach (var path in new[]
                     {
                         executionLayout.UserStateDirectory, executionLayout.UserExecutionDirectory,
                         executionLayout.UserLogDirectory, executionLayout.TrayStateDirectory
                     })
            {
                LocalAssert.True(Directory.Exists(path), "Execution initialization omitted a required user directory.");
                CortexLocalRegistryAclPolicy.RequireProtectedDirectory(path);
            }
            LocalAssert.True(Directory.Exists(executionLayout.DataRoot) &&
                !Directory.Exists(executionLayout.CortexDataDirectory),
                "Execution initialization omitted the selected state root or created semantic database directories.");

            ExistingStartupDirectoriesAreValidatedWithoutPermissionChanges(root, current);

            var occupied = Path.Combine(root, "occupied");
            new DirectoryInfo(occupied).Create(fixtureSecurity);
            var marker = Path.Combine(occupied, "untouched.txt");
            File.WriteAllText(marker, "existing fixture content");
            var before = new DirectoryInfo(occupied).GetAccessControl()
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
            var rejected = false;
            try { Layout(occupied).EnsureCreated(); }
            catch (UnauthorizedAccessException) { rejected = true; }
            LocalAssert.True(rejected, "An existing directory with unrelated access was adopted.");
            LocalAssert.Equal(before, new DirectoryInfo(occupied).GetAccessControl()
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner),
                "Rejected occupied-directory admission changed its existing descriptor.");
            LocalAssert.Equal("existing fixture content", File.ReadAllText(marker),
                "Rejected occupied-directory admission changed existing file bytes.");
            LocalAssert.True(Directory.EnumerateFileSystemEntries(occupied).Count() == 1,
                "Rejected occupied-directory admission created a child directory.");
        }
        finally
        {
            // Only the exact GUID-owned fixture is removed; no links are used.
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void ExistingStartupDirectoriesAreValidatedWithoutPermissionChanges(
        string root, SecurityIdentifier owner)
    {
        var privatePath = Path.Combine(root, "existing-private");
        var privateSecurity = new DirectorySecurity();
        privateSecurity.SetOwner(owner);
        privateSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        // This is valid private access without the creation helper's inheritable
        // ACE flags. Replacing it with that template would be an observable edit.
        privateSecurity.AddAccessRule(new FileSystemAccessRule(
            owner, FileSystemRights.FullControl, AccessControlType.Allow));
        new DirectoryInfo(privatePath).Create(privateSecurity);
        RequireStartupLeavesExistingStateUnchanged(privatePath, expectRefusal: false);

        var setupPath = Path.Combine(root, "existing-setup-policy");
        var setupSecurity = Security(owner, owner, "S-1-5-18", "S-1-5-32-544");
        setupSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        new DirectoryInfo(setupPath).Create(setupSecurity);
        RequireStartupLeavesExistingStateUnchanged(setupPath, expectRefusal: false);

        var partialPath = Path.Combine(root, "existing-partial-owner");
        var partialSecurity = new DirectorySecurity();
        partialSecurity.SetOwner(owner);
        partialSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        partialSecurity.AddAccessRule(new FileSystemAccessRule(
            owner, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new DirectoryInfo(partialPath).Create(partialSecurity);
        var before = new DirectoryInfo(partialPath).GetAccessControl().GetSecurityDescriptorBinaryForm();
        RequireStartupPreparationRefusal(partialPath);
        LocalAssert.True(before.SequenceEqual(new DirectoryInfo(partialPath).GetAccessControl()
            .GetSecurityDescriptorBinaryForm()), "Startup granted missing owner rights on existing state.");
    }

    private static void RequireStartupLeavesExistingStateUnchanged(string path, bool expectRefusal)
    {
        var marker = Path.Combine(path, "retained.txt");
        File.WriteAllText(marker, "retained fixture state");
        var before = new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm();
        var fileBefore = new FileInfo(marker).GetAccessControl().GetSecurityDescriptorBinaryForm();
        if (expectRefusal)
            RequireStartupPreparationRefusal(path);
        else
            CortexUserStateDirectories.EnsureCurrentUserDirectory(path);
        LocalAssert.True(before.SequenceEqual(new DirectoryInfo(path).GetAccessControl()
            .GetSecurityDescriptorBinaryForm()), "Startup changed an existing directory's permissions.");
        LocalAssert.True(fileBefore.SequenceEqual(new FileInfo(marker).GetAccessControl()
            .GetSecurityDescriptorBinaryForm()), "Startup changed existing child permissions.");
        LocalAssert.Equal("retained fixture state", File.ReadAllText(marker),
            "Startup changed existing fixture content.");
        LocalAssert.True(Directory.EnumerateFileSystemEntries(path).Count() == 1,
            "Startup added or removed existing fixture entries.");
    }

    private static void RequireStartupPreparationRefusal(string path)
    {
        try { CortexUserStateDirectories.EnsureCurrentUserDirectory(path); }
        catch (UnauthorizedAccessException exception)
        {
            LocalAssert.True(exception.Message.StartsWith(
                    "cortex_user_state_security_preparation_required:", StringComparison.Ordinal) &&
                exception.Message.Length <= 256 && exception.InnerException is UnauthorizedAccessException,
                "Startup refusal omitted its bounded preparation code or underlying admission cause.");
            return;
        }
        throw new InvalidOperationException("Startup silently prepared existing non-private state.");
    }

    private static CortexCurrentUserLayout Layout(string root) => new(
        root, Path.Combine(root, "data"), Path.Combine(root, "data", "cortex.db"),
        Path.Combine(root, "config"), Path.Combine(root, "backups"),
        Path.Combine(root, "logs"), Path.Combine(root, "receipts"));

    private static DirectorySecurity Security(
        SecurityIdentifier owner,
        SecurityIdentifier allowedOwner,
        params string[] additionalSids)
    {
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        foreach (var sid in new[] { allowedOwner.Value }.Concat(additionalSids))
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static void RejectUnchanged(DirectorySecurity security, SecurityIdentifier owner, string message)
    {
        var before = security.GetSecurityDescriptorBinaryForm();
        var rejected = false;
        try { CortexUserStateDirectories.RequireUserStateDirectoryAclForProtection(security, owner); }
        catch (UnauthorizedAccessException) { rejected = true; }
        LocalAssert.True(rejected, message);
        LocalAssert.True(before.SequenceEqual(security.GetSecurityDescriptorBinaryForm()),
            "Rejected directory admission changed the existing owner or access list.");
    }
}
