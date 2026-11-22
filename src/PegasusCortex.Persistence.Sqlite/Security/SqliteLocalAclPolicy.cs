using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Pegasus.Cortex.Persistence.Sqlite.Security;

/// <summary>
/// Applies an explicit current-user boundary to local Cortex state. The protected
/// directory rule is inheritable so SQLite WAL and shared-memory sidecars receive
/// the same boundary as the primary database.
/// </summary>
internal static class SqliteLocalAclPolicy
{
    public static void ProtectDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            ProtectWindowsDirectory(path);
            return;
        }
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void ProtectFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            ProtectWindowsFile(path);
            return;
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void RequireProtectedDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RequireProtectedWindowsDirectory(path);
            return;
        }
        var mode = File.GetUnixFileMode(path);
        var forbidden = UnixFileMode.GroupRead |
                        UnixFileMode.GroupWrite |
                        UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead |
                        UnixFileMode.OtherWrite |
                        UnixFileMode.OtherExecute;
        if ((mode & forbidden) != 0)
            throw new UnauthorizedAccessException("The local Cortex directory is accessible outside the current OS user.");
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectWindowsDirectory(string path)
    {
        var identity = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetOwner(identity);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectWindowsFile(string path)
    {
        var identity = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var security = new FileSecurity();
        security.SetOwner(identity);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void RequireProtectedWindowsDirectory(string path)
    {
        var identity = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.AreAccessRulesProtected ||
            security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !owner.Equals(identity))
        {
            throw new UnauthorizedAccessException("The local Cortex directory is not owned and protected by the current OS user.");
        }
        var allowed = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .Distinct()
            .ToArray();
        if (allowed.Length != 1 || !allowed[0].Equals(identity))
            throw new UnauthorizedAccessException("The local Cortex directory grants access outside the current OS user.");
    }
}
