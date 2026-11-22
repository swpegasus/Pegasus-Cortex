using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Pegasus.AgentOps.Engine;

public enum WindowsDriveMappingState
{
    Local,
    Mapped,
    Unavailable
}

public sealed record WindowsDriveMappingResolution(
    WindowsDriveMappingState State,
    string? UniversalPath = null,
    int NativeError = 0);

/// <summary>
/// Resolves a canonical drive-letter path in the interactive user's mapping
/// namespace. Implementations must not create, reconnect, or change mappings.
/// </summary>
public interface IWindowsDriveMappingResolver
{
    WindowsDriveMappingResolution Resolve(string canonicalDrivePath);
}

/// <summary>
/// A path frozen in the user context before it crosses the service boundary.
/// Kind and Atomicity intentionally use receipt-ready stable values.
/// </summary>
public sealed record WindowsPathContextResult
{
    internal WindowsPathContextResult(string canonicalPath, string kind, string atomicity)
    {
        CanonicalPath = canonicalPath;
        Kind = kind;
        Atomicity = atomicity;
    }

    public string CanonicalPath { get; }
    public string Kind { get; }
    public string Atomicity { get; }
}

/// <summary>
/// An alias root normalized once in the user context. Resolving children from
/// this value never repeats mapped-drive discovery.
/// </summary>
public sealed record WindowsPathAliasRoot
{
    internal WindowsPathAliasRoot(string alias, WindowsPathContextResult root)
    {
        Alias = alias;
        Root = root;
    }

    public string Alias { get; }
    public WindowsPathContextResult Root { get; }
}

/// <summary>
/// Canonicalizes Windows paths before service submission without invoking a
/// shell or probing a remote share. Existing local components are checked for
/// reparses; network components remain explicitly remote_unverified.
/// </summary>
public sealed class WindowsPathContextNormalizer
{
    public const int MaximumPathCharacters = 32_767;
    public const int MaximumAliasCharacters = 32;

    private const string LocalKind = "local";
    private const string NetworkKind = "network";
    private const string LocalAtomicity = "local";
    private const string RemoteAtomicity = "remote_unverified";

    private readonly IWindowsDriveMappingResolver _mappingResolver;

    public WindowsPathContextNormalizer()
        : this(new NativeWindowsDriveMappingResolver())
    {
    }

    public WindowsPathContextNormalizer(IWindowsDriveMappingResolver mappingResolver)
    {
        _mappingResolver = mappingResolver ?? throw new ArgumentNullException(nameof(mappingResolver));
    }

    public WindowsPathContextResult Normalize(string path)
    {
        EnsureWindows();
        var canonical = CanonicalAbsolute(path);
        if (IsUnc(canonical)) return Network(canonical);

        if (!IsDrivePath(canonical)) throw new AgentOpsException("invalid");
        var resolution = _mappingResolver.Resolve(canonical);
        return resolution.State switch
        {
            WindowsDriveMappingState.Local => Local(canonical),
            WindowsDriveMappingState.Mapped => Network(CanonicalMappedPath(resolution.UniversalPath)),
            WindowsDriveMappingState.Unavailable => throw MappingUnavailable(resolution.NativeError),
            _ => throw new AgentOpsException("invalid")
        };
    }

    public WindowsPathAliasRoot NormalizeAliasRoot(string alias, string absoluteRoot)
    {
        ValidateAlias(alias);
        var root = Normalize(absoluteRoot);
        var canonicalRoot = TrimNonRootEndingSeparator(root.CanonicalPath);
        if (!StringComparer.Ordinal.Equals(canonicalRoot, root.CanonicalPath))
            root = new WindowsPathContextResult(canonicalRoot, root.Kind, root.Atomicity);
        return new WindowsPathAliasRoot(alias, root);
    }

    public WindowsPathContextResult ResolveAliasPath(WindowsPathAliasRoot aliasRoot, string relativePath)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(aliasRoot);
        ValidateRelative(relativePath);

        string combined;
        try
        {
            combined = Path.GetFullPath(relativePath, aliasRoot.Root.CanonicalPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AgentOpsException("invalid", inner: ex);
        }

        if (combined.Length > MaximumPathCharacters || !Contains(aliasRoot.Root.CanonicalPath, combined))
            throw new AgentOpsException("precondition");

        combined = NormalizeDriveLetter(combined);
        ValidateSyntax(combined, absolute: true);
        if (aliasRoot.Root.Kind == LocalKind)
        {
            PathNormalizer.RejectReparseComponents(aliasRoot.Root.CanonicalPath, combined);
            return Local(combined);
        }

        // A service must not reinterpret or re-resolve an already frozen alias
        // root. Remote filesystem semantics remain explicitly unverified.
        return Network(combined);
    }

    private static WindowsPathContextResult Local(string canonical)
    {
        PathNormalizer.RejectAbsoluteReparseComponents(canonical);
        return new WindowsPathContextResult(canonical, LocalKind, LocalAtomicity);
    }

    private static WindowsPathContextResult Network(string canonical) =>
        new(canonical, NetworkKind, RemoteAtomicity);

    /// <summary>
    /// Revalidates local path components without causing a service identity to
    /// probe a user-authenticated remote share. Network access and entry-level
    /// no-follow checks remain the responsibility of the user-context operation.
    /// </summary>
    internal static void RevalidateLocalNoFollow(WindowsPathContextResult context, string? admittedRoot = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Kind == NetworkKind) return;
        if (admittedRoot is null) PathNormalizer.RejectAbsoluteReparseComponents(context.CanonicalPath);
        else PathNormalizer.RejectReparseComponents(admittedRoot, context.CanonicalPath);
    }

    private static string CanonicalAbsolute(string path)
    {
        ValidateBounded(path);
        if (IsDeviceNamespace(path) || !Path.IsPathFullyQualified(path))
            throw new AgentOpsException("invalid");
        ValidateSyntax(path, absolute: true);

        string canonical;
        try
        {
            canonical = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AgentOpsException("invalid", inner: ex);
        }

        canonical = NormalizeDriveLetter(canonical);
        ValidateBounded(canonical);
        ValidateSyntax(canonical, absolute: true);
        if (!IsDrivePath(canonical) && !IsUnc(canonical)) throw new AgentOpsException("invalid");
        if (IsUnc(canonical)) ValidateUncRoot(canonical);
        return canonical;
    }

    private static string CanonicalMappedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new AgentOpsException("mapping_unavailable");
        var canonical = CanonicalAbsolute(path);
        if (!IsUnc(canonical)) throw new AgentOpsException("mapping_unavailable");
        return canonical;
    }

    private static string NormalizeDriveLetter(string path)
    {
        if (!IsDrivePath(path) || char.IsUpper(path[0])) return path;
        return char.ToUpperInvariant(path[0]) + path[1..];
    }

    private static string TrimNonRootEndingSeparator(string path)
    {
        var root = Path.GetPathRoot(path) ?? throw new AgentOpsException("invalid");
        return path.Length == root.Length ? path : Path.TrimEndingDirectorySeparator(path);
    }

    private static void ValidateRelative(string path)
    {
        ValidateBounded(path);
        if (Path.IsPathRooted(path) || Path.IsPathFullyQualified(path) || IsDeviceNamespace(path))
            throw new AgentOpsException("precondition");
        ValidateSyntax(path, absolute: false);

        var components = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0 || components.Any(component => component is "." or ".."))
            throw new AgentOpsException("precondition");
    }

    private static void ValidateBounded(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathCharacters)
            throw new AgentOpsException("invalid");
    }

    private static void ValidateSyntax(string path, bool absolute)
    {
        if (path.IndexOfAny(['\0', '*', '?']) >= 0 || path.Any(char.IsControl))
            throw new AgentOpsException("precondition");

        var allowedColon = absolute && IsDrivePath(path) ? 1 : -1;
        for (var index = 0; index < path.Length; index++)
            if (path[index] == ':' && index != allowedColon)
                throw new AgentOpsException("precondition");
    }

    private static void ValidateAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias) || alias.Length > MaximumAliasCharacters ||
            alias.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new AgentOpsException("invalid");
    }

    private static void ValidateUncRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || !root.StartsWith("\\\\", StringComparison.Ordinal))
            throw new AgentOpsException("invalid");
        var components = root[2..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length < 2 || components.Any(component => component is "." or ".."))
            throw new AgentOpsException("invalid");
    }

    private static bool Contains(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static bool IsDrivePath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' &&
        path[2] is '\\' or '/';

    private static bool IsUnc(string path) =>
        path.StartsWith("\\\\", StringComparison.Ordinal) && !IsDeviceNamespace(path);

    private static bool IsDeviceNamespace(string path) =>
        path.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
        path.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
        path.StartsWith("\\??\\", StringComparison.Ordinal);

    private static AgentOpsException MappingUnavailable(int nativeError) => nativeError == 0
        ? new AgentOpsException("mapping_unavailable")
        : new AgentOpsException("mapping_unavailable", inner: new Win32Exception(nativeError));

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new AgentOpsException("platform");
    }
}

internal sealed class NativeWindowsDriveMappingResolver : IWindowsDriveMappingResolver
{
    private const int MaximumNativeBufferBytes = 128 * 1024;
    private const int UniversalNameInfoLevel = 1;

    private const int NoError = 0;
    private const int ErrorMoreData = 234;
    private const int ErrorBadDevice = 1200;
    private const int ErrorNotConnected = 2250;

    private const uint DriveUnknown = 0;
    private const uint DriveNoRootDirectory = 1;
    private const uint DriveRemovable = 2;
    private const uint DriveFixed = 3;
    private const uint DriveRemote = 4;
    private const uint DriveCdRom = 5;
    private const uint DriveRamDisk = 6;

    public WindowsDriveMappingResolution Resolve(string canonicalDrivePath)
    {
        if (!OperatingSystem.IsWindows()) throw new AgentOpsException("platform");
        if (canonicalDrivePath.Length < 3 || canonicalDrivePath[1] != ':')
            throw new AgentOpsException("invalid");

        var universal = TryGetUniversalName(canonicalDrivePath, out var universalError);
        if (universal is not null)
            return new WindowsDriveMappingResolution(WindowsDriveMappingState.Mapped, universal);

        var driveName = canonicalDrivePath[..2];
        var remoteRoot = TryGetConnection(driveName, out var connectionError);
        if (remoteRoot is not null)
        {
            var suffix = canonicalDrivePath.Length > 3 ? canonicalDrivePath[3..] : string.Empty;
            var mappedPath = suffix.Length == 0 ? remoteRoot : Path.Combine(remoteRoot, suffix);
            return new WindowsDriveMappingResolution(WindowsDriveMappingState.Mapped, mappedPath);
        }

        var driveType = GetDriveTypeW(canonicalDrivePath[..3]);
        if (driveType is DriveRemovable or DriveFixed or DriveCdRom or DriveRamDisk)
            return new WindowsDriveMappingResolution(WindowsDriveMappingState.Local);

        var error = connectionError != ErrorBadDevice && connectionError != ErrorNotConnected
            ? connectionError
            : universalError;
        if (driveType is DriveRemote or DriveUnknown or DriveNoRootDirectory)
            return new WindowsDriveMappingResolution(WindowsDriveMappingState.Unavailable, NativeError: error);

        return new WindowsDriveMappingResolution(WindowsDriveMappingState.Unavailable, NativeError: error);
    }

    private static string? TryGetUniversalName(string localPath, out int error)
    {
        var bufferSize = 2 * 1024;
        while (true)
        {
            if (bufferSize <= 0 || bufferSize > MaximumNativeBufferBytes)
            {
                error = ErrorMoreData;
                return null;
            }

            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var supplied = bufferSize;
                var result = WNetGetUniversalNameW(localPath, UniversalNameInfoLevel, buffer, ref supplied);
                if (result == NoError)
                {
                    var information = Marshal.PtrToStructure<UniversalNameInfo>(buffer);
                    var value = Marshal.PtrToStringUni(information.UniversalName);
                    error = NoError;
                    return string.IsNullOrWhiteSpace(value) ? null : value;
                }

                if (result == ErrorMoreData && supplied > bufferSize)
                {
                    bufferSize = supplied;
                    continue;
                }

                error = result;
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static string? TryGetConnection(string driveName, out int error)
    {
        var characters = 512;
        while (true)
        {
            if (characters <= 0 || characters > WindowsPathContextNormalizer.MaximumPathCharacters + 1)
            {
                error = ErrorMoreData;
                return null;
            }

            var remoteName = new StringBuilder(characters);
            var supplied = characters;
            var result = WNetGetConnectionW(driveName, remoteName, ref supplied);
            if (result == NoError)
            {
                error = NoError;
                return remoteName.ToString();
            }

            if (result == ErrorMoreData && supplied > characters)
            {
                characters = supplied;
                continue;
            }

            error = result;
            return null;
        }
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetGetUniversalNameW(
        string localPath,
        int infoLevel,
        nint buffer,
        ref int bufferSize);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetGetConnectionW(
        string localName,
        StringBuilder remoteName,
        ref int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string rootPathName);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UniversalNameInfo
    {
        public readonly nint UniversalName;
    }
}
