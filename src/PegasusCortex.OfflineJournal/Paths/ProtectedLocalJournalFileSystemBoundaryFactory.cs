using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Pegasus.Cortex.OfflineJournal.Paths;

/// <summary>
/// Single-user standalone boundary over the existing bounded no-reparse path
/// implementation. The journal destination must already be owner-private.
/// This profile trusts processes running as that same OS user; deployments
/// requiring protection from same-user races must use a handle-relative
/// platform boundary instead.
/// </summary>
public sealed class ProtectedLocalJournalFileSystemBoundaryFactory :
    IJournalFileSystemBoundaryFactory
{
    public static ProtectedLocalJournalFileSystemBoundaryFactory Instance { get; } =
        new();

    private ProtectedLocalJournalFileSystemBoundaryFactory()
    {
    }

    public JournalFileSystemBoundarySafety Safety =>
        JournalFileSystemBoundarySafety.ProtectedLocalSingleUser;

    public IJournalFileSystemBoundary OpenJournalRoot(
        string requestedRoot,
        string approvedRoot)
    {
        if (!JournalPathPolicy.TryValidateLexicalTestRoot(
                requestedRoot,
                approvedRoot,
                out var policy,
                out var reason))
        {
            throw new ArgumentException(reason, nameof(requestedRoot));
        }
        RequireOwnerPrivateDirectory(policy!.Root);
        return new LexicalTestOnlyJournalFileSystemBoundary(policy, Safety);
    }

    public ILegacySourceFileSystemBoundary OpenLegacySourceRoot(
        string requestedRoot,
        string approvedRoot) =>
        new LexicalTestOnlyLegacySourceFileSystemBoundary(
            requestedRoot,
            approvedRoot,
            Safety);

    private static void RequireOwnerPrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RequireOwnerPrivateWindowsDirectory(path);
            return;
        }

        var expected =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute;
        if (File.GetUnixFileMode(path) != expected)
        {
            throw new UnauthorizedAccessException(
                "The protected local journal root must use owner-only permissions.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RequireOwnerPrivateWindowsDirectory(string path)
    {
        var current = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException(
                "The current Windows user SID is unavailable.");
        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.AreAccessRulesProtected ||
            security.GetOwner(typeof(SecurityIdentifier)) is not
                SecurityIdentifier owner ||
            !owner.Equals(current))
        {
            throw new UnauthorizedAccessException(
                "The protected local journal root is not owned and protected by the current OS user.");
        }

        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>();
        if (rules.Any(rule =>
                rule.AccessControlType != AccessControlType.Allow ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                !sid.Equals(current)))
        {
            throw new UnauthorizedAccessException(
                "The protected local journal root grants access outside the current OS user.");
        }
    }
}
