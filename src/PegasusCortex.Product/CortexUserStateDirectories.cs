using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

[assembly: InternalsVisibleTo("PegasusCortex.Mcp.Local")]
[assembly: InternalsVisibleTo("PegasusCortex.Mcp.Local.Tests")]

namespace Pegasus.Cortex.Product;

/// <summary>Required data capability; private ownership and grant checks apply in either mode.</summary>
public enum CortexUserStateAccess { Modify = 0, Read = 1 }

/// <summary>Owner-bound first-use directory creation shared by semantic and execution clients.</summary>
public static class CortexUserStateDirectories
{
    private const string SecurityPreparationRequiredMessage =
        "cortex_user_state_security_preparation_required: The user-state directory failed owner access admission. " +
        "Startup will not change existing permissions; request owner review before preparation.";

    public static void EnsureExecution(CortexProductLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        layout.RequireExecutionPathsWithinInstallRoot();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Cortex execution user-state requires Windows.");
        if (!string.Equals(layout.OwnerSid, CurrentWindowsSid().Value, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Execution state must belong to the current Windows user.");

        // Registration has already selected the shared mutable root. Create only
        // this user's execution/log/tray paths; do not touch semantic DB paths or
        // the privileged service domain.
        foreach (var path in new[]
                 {
                     layout.UserStateDirectory, layout.UserExecutionDirectory,
                     layout.UserLogDirectory, layout.TrayStateDirectory
                 })
            EnsureCurrentUserDirectory(path);
    }

    public static void EnsureCurrentUserDirectory(string path)
    {
        var canonical = CortexProductLayout.RequireCanonicalAbsoluteDirectory(path, nameof(path));
        CortexProductLayout.RejectExistingReparseComponents(canonical);
        if (File.Exists(canonical))
            throw new IOException("A Cortex user-state directory path identifies a file.");

        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(canonical);
            CortexProductLayout.RejectExistingReparseComponents(canonical);
            File.SetUnixFileMode(canonical,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }

        var identity = CurrentWindowsSid();
        if (!Directory.Exists(canonical))
        {
            // Security is supplied at creation only. If another directory wins
            // the race, Create leaves it unchanged and strict admission follows.
            new DirectoryInfo(canonical).Create(CreateWindowsUserDirectorySecurity(identity));
        }
        CortexProductLayout.RejectExistingReparseComponents(canonical);
        try
        {
            // Existing state must already satisfy the owner access policy.
            // Startup never adopts or normalizes its owner, ACL or inheritance.
            RequireWindowsUserStateAccess(ReadWindowsDirectorySecurity(canonical), identity);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw SecurityPreparationRequired(exception);
        }
    }

    private static UnauthorizedAccessException SecurityPreparationRequired(UnauthorizedAccessException cause) =>
        new(SecurityPreparationRequiredMessage, cause);

    [SupportedOSPlatform("windows")]
    public static DirectorySecurity CreateWindowsUserDirectorySecurity(SecurityIdentifier user) =>
        CortexUserStateSecurity.CreateDirectory(user);

    [SupportedOSPlatform("windows")]
    public static FileSecurity CreateWindowsUserFileSecurity(SecurityIdentifier user) =>
        CortexUserStateSecurity.CreateFile(user);

    /// <summary>
    /// Validates private user state without editing security. Direct user access
    /// must include directory Modify, or file read/write/delete, plus synchronize;
    /// permission to change ACLs or ownership is not required. SYSTEM and local
    /// Administrators may own/access the entry; unrelated grants are refused.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void RequireWindowsUserStateAccess(FileSystemSecurity security, SecurityIdentifier user) =>
        RequireWindowsUserStateAccess(security, user, CortexUserStateAccess.Modify);

    /// <summary>Requires only the selected data capability; privacy admission is identical for readers and writers.</summary>
    [SupportedOSPlatform("windows")]
    public static void RequireWindowsUserStateAccess(FileSystemSecurity security, SecurityIdentifier user,
        CortexUserStateAccess requiredAccess)
    {
        ArgumentNullException.ThrowIfNull(security);
        ArgumentNullException.ThrowIfNull(user);
        CortexUserStateSecurity.RequireAccess(security, user, requiredAccess);
    }

    [SupportedOSPlatform("windows")]
    internal static DirectorySecurity CreatePrivateWindowsDirectorySecurity(SecurityIdentifier identity) =>
        CreateWindowsUserDirectorySecurity(identity);

    [SupportedOSPlatform("windows")]
    internal static void RequireUserStateDirectoryAclForProtection(
        DirectorySecurity security,
        SecurityIdentifier identity,
        bool requirePrivate = false)
    {
        // The legacy internal flag remains source-compatible; all user-state
        // callers now share one policy instead of two conflicting ACL templates.
        RequireWindowsUserStateAccess(security, identity);
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity ReadWindowsDirectorySecurity(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Cortex user-state path is not a regular directory.");
        return new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentWindowsSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            sid.IsWellKnown(WellKnownSidType.LocalServiceSid) ||
            sid.IsWellKnown(WellKnownSidType.NetworkServiceSid) ||
            sid.IsWellKnown(WellKnownSidType.AnonymousSid))
            throw new UnauthorizedAccessException("Service and anonymous identities cannot initialize Cortex user state.");
        return sid;
    }
}
