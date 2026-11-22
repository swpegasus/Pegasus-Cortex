using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

/// <summary>Parent admission for a private data child, not an executable-image policy.</summary>
internal static class CortexDataParentBoundary
{
    internal static void RequireExistingAncestors(string parent)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if (!Path.IsPathFullyQualified(parent) || canonical.Length > CortexProductLayout.MaximumWindowsPathCharacters ||
            !string.Equals(canonical, Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A canonical local data parent is required.");
        for (var directory = new DirectoryInfo(canonical); directory is not null; directory = directory.Parent)
        {
            var attributes = File.GetAttributes(directory.FullName);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new UnauthorizedAccessException("A data parent is absent or redirected.");
            RequireAccess(directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
        }
    }

    internal static void RequireAccess(DirectorySecurity security)
    {
        var trusted = new HashSet<string>(StringComparer.Ordinal) { "S-1-5-18", "S-1-5-32-544" };
        try
        {
            trusted.Add(((SecurityIdentifier)new NTAccount("NT SERVICE", "TrustedInstaller")
                .Translate(typeof(SecurityIdentifier))).Value);
        }
        catch (IdentityNotMappedException) { }
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.Owner is null || !trusted.Contains(descriptor.Owner.Value) || descriptor.DiscretionaryAcl is null)
            throw new UnauthorizedAccessException("A data parent has no trusted administrative owner and access list.");
        const FileSystemRights replacement = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            // Creating unrelated siblings or changing parent attributes is not
            // permission to replace our protected child. Inherit-only grants do
            // not apply here and cannot enter the protected staging descriptor.
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 || trusted.Contains(rule.IdentityReference.Value)) continue;
            if (((uint)rule.FileSystemRights & ((uint)replacement | 0x10000000u)) != 0)
                throw new UnauthorizedAccessException("A data parent permits replacement by an ordinary principal.");
        }
    }
}
