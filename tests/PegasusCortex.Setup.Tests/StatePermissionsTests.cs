using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.Cortex.Product;
using Pegasus.Cortex.Setup;

internal static class StatePermissionsTests
{
    internal static void Run(string root)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var path = Path.Combine(root, "retained-permissions");
        new DirectoryInfo(path).Create(CortexUserStateDirectories.CreateWindowsUserDirectorySecurity(user));
        var file = Path.Combine(path, "retained.db");
        File.WriteAllText(file, "retained fixture bytes");
        // An existing ordinary Modify grant is sufficient; setup must not
        // normalize ownership, inheritance, DACLs or children during admission.
        var modified = new DirectoryInfo(path).GetAccessControl();
        modified.SetAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(modified);
        var before = new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm();
        var child = new FileInfo(file).GetAccessControl().GetSecurityDescriptorBinaryForm();
        var bytes = File.ReadAllBytes(file);
        CortexProductStateProvisioner.ProvisionDirectory(new(path, user.Value, CortexDirectoryAccessProfile.UserPrivate),
            preserveExisting: true, apply: _ => throw new Exception("Existing data was resealed"));
        Check(before.SequenceEqual(new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm()) &&
            child.SequenceEqual(new FileInfo(file).GetAccessControl().GetSecurityDescriptorBinaryForm()) &&
            bytes.SequenceEqual(File.ReadAllBytes(file)), "Valid existing state changed during admission");

        var synthetic = CortexUserStateDirectories.CreateWindowsUserDirectorySecurity(user);
        foreach (var owner in new[] { user.Value, "S-1-5-18", "S-1-5-32-544" })
        {
            synthetic.SetOwner(new SecurityIdentifier(owner));
            CortexProductStateProvisioner.RequireDirectoryAccess(synthetic, new(path, user.Value, CortexDirectoryAccessProfile.UserPrivate));
        }
        synthetic.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.Read, AccessControlType.Allow));
        Throws<UnauthorizedAccessException>(() => CortexProductStateProvisioner.RequireDirectoryAccess(synthetic,
            new(path, user.Value, CortexDirectoryAccessProfile.UserPrivate)));

        var service = new FileSecurity();
        service.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            service.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
        CortexProductStateProvisioner.RequireServiceEntryAccess(service);
        service.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Write, AccessControlType.Allow));
        Throws<UnauthorizedAccessException>(() => CortexProductStateProvisioner.RequireServiceEntryAccess(service));
        var nullAcl = new FileSecurity();
        nullAcl.SetSecurityDescriptorSddlForm("O:BAG:SYD:NO_ACCESS_CONTROL");
        Throws<UnauthorizedAccessException>(() => CortexProductStateProvisioner.RequireServiceEntryAccess(nullAcl));

        foreach (var role in new[] { CortexDirectoryAccessProfile.StateTraverse, CortexDirectoryAccessProfile.UserContainer })
        {
            var security = CortexProductStateProvisioner.DescribeDirectorySecurity(new(path, "S-1-5-32-544", role));
            var rule = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Single(item => item.IdentityReference.Value == "S-1-5-32-545");
            Check((rule.FileSystemRights & FileSystemRights.ListDirectory) != 0 && rule.InheritanceFlags == InheritanceFlags.None,
                "Shared containers must be browsable without disclosing private children");
        }
        Console.WriteLine("PASS existing state Modify admission with exact scratch bytes/DACL preservation; synthetic trusted owners/service isolation and noninheriting container listing; no installed access");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
