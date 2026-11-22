using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace Pegasus.Cortex.Product;

/// <summary>Rejects ancestor substitution before a product image is elevated.</summary>
public static class CortexPrivilegedCodePath
{
    // Shared by the existing service-control and fixed one-shot administrator
    // helper. This preserves their protected installed-image admission; it is
    // not a per-call Authenticode or loaded-byte signature assertion.
    public static void RequireProtectedImage(string installRoot, string executable)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Privileged Cortex images require Windows protection.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var image = Path.GetFullPath(executable);
        RequireProtectedAncestors(root);
        CortexProductLayout.RejectExistingReparseComponents(image);
        if (!File.Exists(image)) throw new FileNotFoundException("The installed privileged Cortex image is unavailable.");
        RequireImmutableImageBoundary(new FileInfo(image).GetAccessControl());
        for (var parent = new FileInfo(image).Directory; parent is not null; parent = parent.Parent)
        {
            RequireImmutableImageBoundary(parent.GetAccessControl());
            if (string.Equals(parent.FullName, root, StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new InvalidDataException("The privileged image escaped the product root.");
    }

    [SupportedOSPlatform("windows")]
    private static void RequireImmutableImageBoundary(FileSystemSecurity security)
    {
        if (new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null ||
            security.GetOwner(typeof(SecurityIdentifier))?.Value is not ("S-1-5-18" or "S-1-5-32-544"))
            throw new UnauthorizedAccessException("The privileged image boundary is not administrator-owned.");
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0 &&
                rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544"))
                throw new UnauthorizedAccessException("The privileged image boundary is writable by an untrusted principal.");
    }

    public static void RequireProtectedAncestors(string installRoot)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Privileged Cortex images require Windows protection.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var trusted = new HashSet<string>(StringComparer.Ordinal) { "S-1-5-18", "S-1-5-32-544" };
        try
        {
            trusted.Add(((SecurityIdentifier)new NTAccount("NT SERVICE", "TrustedInstaller")
                .Translate(typeof(SecurityIdentifier))).Value);
        }
        catch (IdentityNotMappedException) { /* Unknown owners still refuse below. */ }
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("A privileged product ancestor is absent or redirected.");
            var security = directory.GetAccessControl();
            RequireAncestorAccess(security,
                string.Equals(directory.FullName, root, StringComparison.OrdinalIgnoreCase), trusted);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void RequireAncestorAccess(DirectorySecurity security, bool productRoot,
        IReadOnlySet<string> trusted)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.Owner is null || !trusted.Contains(descriptor.Owner.Value) || descriptor.DiscretionaryAcl is null)
            throw new UnauthorizedAccessException("A privileged product ancestor is not machine-owned.");
        const FileSystemRights replacement = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        // Adding a sibling at an ancestor does not replace our protected child.
        // The actual product root still forbids new code files from ordinary
        // writers, and every image/parent inside it retains the strict check.
        var forbidden = (uint)replacement | 0x10000000u | 0x40000000u;
        if (productRoot) forbidden |= (uint)(FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (unchecked((uint)rule.FileSystemRights) & forbidden) != 0 && !trusted.Contains(rule.IdentityReference.Value))
                throw new UnauthorizedAccessException("A privileged product ancestor permits replacement by an untrusted principal.");
    }
}
