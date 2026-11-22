using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.Cortex.Product;

internal static class PrivilegedCodePathTests
{
    internal static Task RunAsync()
    {
        var trusted = new HashSet<string>(StringComparer.Ordinal) { "S-1-5-18", "S-1-5-32-544" };
        var user = new SecurityIdentifier("S-1-5-21-100-200-300-1001");
        var adjacent = Describe();
        adjacent.AddAccessRule(new(user, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories |
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, AccessControlType.Allow));
        adjacent.AddAccessRule(new(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.InheritOnly, AccessControlType.Allow));
        CheckAccess(adjacent, false, true);
        CheckAccess(adjacent, true, false);
        var createDirectory = Describe();
        createDirectory.AddAccessRule(new(user,FileSystemRights.CreateDirectories,AccessControlType.Allow));
        CheckAccess(createDirectory,false,true);
        CheckAccess(createDirectory,true,false);
        foreach (var right in new[] { FileSystemRights.Delete, FileSystemRights.DeleteSubdirectoriesAndFiles,
            FileSystemRights.ChangePermissions, FileSystemRights.TakeOwnership, FileSystemRights.WriteAttributes,
            FileSystemRights.WriteExtendedAttributes })
        {
            var unsafeParent = Describe();
            unsafeParent.AddAccessRule(new(user, right, AccessControlType.Allow));
            CheckAccess(unsafeParent, false, false);
            CheckAccess(unsafeParent, true, false);
        }
        foreach (var generic in new[] { "GA", "GW" })
        {
            var unsafeParent = new DirectorySecurity();
            unsafeParent.SetSecurityDescriptorSddlForm($"O:BAG:BAD:P(A;;FA;;;BA)(A;;{generic};;;{user.Value})");
            CheckAccess(unsafeParent, false, false);
            CheckAccess(unsafeParent, true, false);
        }
        foreach (var owner in trusted)
        {
            var machineOwned = Describe(owner);
            machineOwned.AddAccessRule(new(new SecurityIdentifier(owner), FileSystemRights.FullControl, AccessControlType.Allow));
            CheckAccess(machineOwned, false, true);
            CheckAccess(machineOwned, true, true);
        }
        CheckAccess(Describe(user.Value), false, false);
        var nullAcl = new DirectorySecurity();
        var administrator = new SecurityIdentifier("S-1-5-32-544");
        var raw = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative,
            administrator, administrator, null, null);
        var bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0);
        nullAcl.SetSecurityDescriptorBinaryForm(bytes);
        CheckAccess(nullAcl, false, false);
        Console.WriteLine("PASS privileged-code ancestor admission permits sibling creation, rejects replacement/root writes and preserves descriptors (pure Windows ACL fixtures)");
        return Task.CompletedTask;

        void CheckAccess(DirectorySecurity security, bool root, bool allowed)
        {
            var before = security.GetSecurityDescriptorBinaryForm();
            var accepted = true;
            try { CortexPrivilegedCodePath.RequireAncestorAccess(security, root, trusted); }
            catch (UnauthorizedAccessException) { accepted = false; }
            if (accepted != allowed || !before.SequenceEqual(security.GetSecurityDescriptorBinaryForm()))
                throw new InvalidOperationException("Code boundary admission or nonmutating descriptor contract failed.");
        }
    }

    private static DirectorySecurity Describe(string owner = "S-1-5-32-544")
    {
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(owner)); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new(new SecurityIdentifier("S-1-5-32-544"), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        return security;
    }
}
