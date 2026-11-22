using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PegasusCortex.Product.Tests")]
[assembly: InternalsVisibleTo("Pegasus.AgentOps.Tests")]

namespace Pegasus.Cortex.Product;

public sealed partial record CortexProductLayout(
    string InstallRoot,
    string BinDirectory,
    string ToolsDirectory,
    string NsisToolDirectory,
    string NsisCompilerPath,
    string StateDirectory,
    string InstallStateDirectory,
    string ServiceStateDirectory,
    string UserStateDirectory,
    string CortexDataDirectory,
    string CortexDatabasePath,
    string LegacyCortexDatabasePath,
    string UserConfigDirectory,
    string UserBackupDirectory,
    string UserLogDirectory,
    string UserReceiptDirectory,
    string UserExecutionDirectory,
    string TrayStateDirectory,
    string ActiveCatalogPath,
    string OwnerSid)
{
    public const int MaximumWindowsPathCharacters = 240;

    // DataRoot is the complete mutable product-state root. A registered legacy
    // installation may still select InstallRoot\state; fresh installations use
    // the configured ProgramData root. Immutable binaries and tools never move.
    public string DataRoot { get; init; } = Path.Combine(InstallRoot, "state");
    public string? RegisteredDataRoot { get; internal init; }

    private bool IsolatedDataRoot { get; init; }
    public bool IsIsolatedFixture { get; private init; }

    public static CortexProductLayout ResolveCurrent() => CortexDataLocation.Load(ResolveExecution());

    /// <summary>
    /// Resolves signed-in-user execution paths without consulting machine
    /// registration. Registered runtime callers use ResolveCurrent; this seam is
    /// retained for bootstrap and isolated layout construction.
    /// </summary>
    public static CortexProductLayout ResolveExecution()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The unified Cortex product layout requires Windows.");

        var ownerSid = WindowsIdentity.GetCurrent().User?.Value ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        return Create(
            ResolveExecutionInstallRoot(),
            ownerSid,
            requireExistingAncestorsSafe: true,
            validateSemanticPaths: false);
    }

    public static string ResolveMachineInstallRoot()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The unified Cortex product layout requires Windows.");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
            throw new InvalidOperationException("The machine Program Files directory is unavailable.");
        var canonicalProgramFiles = RequireCanonicalAbsoluteDirectory(programFiles, nameof(programFiles));
        var installRoot = Path.GetFullPath(Path.Combine(
            canonicalProgramFiles,
            CortexProductIdentity.CompanyDirectory,
            CortexProductIdentity.ProductDirectory));
        RejectExistingReparseComponents(installRoot);
        RequirePathBudget(installRoot);
        return installRoot;
    }

    public static string ResolveMachineServiceStateDirectory()
    {
        // LocalSystem resolves the machine-registered state root without
        // fabricating a signed-in user identity.
        var installRoot = ResolveExecutionInstallRoot();
        var dataRoot = CortexDataLocation.ResolveMachineDataRoot(installRoot);
        var directory = Path.Combine(dataRoot, "service");
        RequireContained(dataRoot, directory);
        RequirePathBudget(directory);
        RejectExistingReparseComponents(directory);
        return directory;
    }

    /// <summary>
    /// Resolves the nonsecret maintenance barrier without inspecting the private
    /// service leaf. Ordinary readers must not need access to private service state.
    /// </summary>
    public static string ResolveMachinePublicMaintenanceDirectory() =>
        ResolvePublicMaintenanceDirectory(
            CortexDataLocation.ResolveMachineDataRoot(ResolveExecutionInstallRoot()),
            RejectExistingReparseComponents);

    internal static string ResolvePublicMaintenanceDirectory(
        string validatedDataRoot, Action<string> requireSafePath)
    {
        ArgumentNullException.ThrowIfNull(requireSafePath);
        var dataRoot = RequireCanonicalAbsoluteDirectory(validatedDataRoot, nameof(validatedDataRoot));
        var directory = Path.Combine(dataRoot, "execution-maintenance");
        RequireContained(dataRoot, directory);
        RequirePathBudget(directory);
        requireSafePath(directory);
        return directory;
    }

    private static string ResolveExecutionInstallRoot()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The unified Cortex product layout requires Windows.");
        return TryResolveContainingInstallRoot(AppContext.BaseDirectory) ?? ResolveMachineInstallRoot();
    }

    public static CortexProductLayout ForMachine(string programFilesRoot, string ownerSid)
    {
        var canonicalProgramFiles = RequireCanonicalAbsoluteDirectory(
            programFilesRoot,
            nameof(programFilesRoot));
        var installRoot = Path.GetFullPath(Path.Combine(
            canonicalProgramFiles,
            CortexProductIdentity.CompanyDirectory,
            CortexProductIdentity.ProductDirectory));
        return CortexDataLocation.Load(Create(installRoot, ownerSid, requireExistingAncestorsSafe: true));
    }

    /// <summary>
    /// Creates a production layout at an installer-selected local fixed-drive
    /// root. User profiles, temporary folders, Windows, network paths, drive
    /// roots, and reparse paths are rejected. Machine registration selects the
    /// complete mutable-state root; immutable programs remain beneath this root.
    /// </summary>
    public static CortexProductLayout ForInstallRoot(string installRoot, string ownerSid)
    {
        var canonical = RequireCanonicalAbsoluteDirectory(installRoot, nameof(installRoot));
        RequireProductionInstallRoot(canonical);
        return CortexDataLocation.Load(Create(canonical, ownerSid, requireExistingAncestorsSafe: true));
    }

    /// <summary>
    /// Creates the same one-root layout beneath an isolated test or package
    /// staging root. Production callers use <see cref="ResolveCurrent"/> or
    /// <see cref="ForMachine"/>; this method never relaxes path containment or
    /// reparse validation for the resolved user domain. Installer/service actions
    /// additionally validate their privileged paths before using them.
    /// </summary>
    public static CortexProductLayout ForIsolatedRoot(string installRoot, string ownerSid) =>
        Create(installRoot, ownerSid, requireExistingAncestorsSafe: true) with { IsIsolatedFixture = true };

    /// <summary>
    /// Creates an explicit two-root fixture without reading installed registry
    /// state. Temporary roots are permitted; canonical paths, exact semantic
    /// mapping, nonoverlapping roots, and reparse rejection remain enforced.
    /// </summary>
    public static CortexProductLayout ForIsolatedRoots(string installRoot, string dataRoot, string ownerSid) =>
        ForIsolatedRoot(installRoot, ownerSid)
            .SelectDataRoot(dataRoot, isolated: true);

    internal static string? TryResolveContainingInstallRoot(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory) || !Path.IsPathFullyQualified(startDirectory))
            return null;

        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        for (var depth = 0; current is not null && depth <= 8; depth++, current = current.Parent)
        {
            var entryPoint = Path.Combine(current.FullName, "PegasusCortex.exe");
            var bin = Path.Combine(current.FullName, "bin");
            if (!File.Exists(entryPoint) || !Directory.Exists(bin))
                continue;
            RejectExistingReparseComponents(entryPoint);
            RejectExistingReparseComponents(bin);
            if ((new FileInfo(entryPoint).Attributes & FileAttributes.ReparsePoint) != 0 ||
                (new DirectoryInfo(bin).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The installed Cortex root marker is a reparse point.");
            return RequireCanonicalAbsoluteDirectory(current.FullName, nameof(startDirectory));
        }
        return null;
    }

    /// <summary>
    /// Strict installer/migration entry point: validates both declared roots and
    /// privileged service state. Signed-in-user clients use the domain-specific
    /// resolvers and do not need access to the SYSTEM-owned service directory.
    /// </summary>
    public void RequireRuntimePathsWithinInstallRoot()
    {
        RequireRuntimePathsWithinDeclaredRoots();
        RequireServiceStatePath();
    }

    public void RequireRuntimePathsWithinDeclaredRoots()
    {
        RequireExecutionPathsWithinInstallRoot();
        var dataRoot = RequireDataRoot(InstallRoot, DataRoot, IsolatedDataRoot);

        // All mutable paths are exact, owner-qualified descendants of DataRoot.
        var semantic = Path.Combine(dataRoot, "users", OwnerSid, "cortex");
        RequireExactPath(CortexDataDirectory, Path.Combine(semantic, "data"));
        RequireExactPath(CortexDatabasePath, Path.Combine(semantic, "data", "cortex-v2.db"));
        RequireExactPath(LegacyCortexDatabasePath, Path.Combine(semantic, "data", "cortex.db"));
        RequireExactPath(UserBackupDirectory, Path.Combine(semantic, "backups"));
    }

    internal void RequireExecutionPathsWithinInstallRoot(Action<string>? requireSafePath = null)
    {
        RequireCanonicalAbsoluteDirectory(InstallRoot, nameof(InstallRoot));
        RequireCanonicalOwnerSid(OwnerSid);
        var dataRoot = RequireDataRoot(InstallRoot, DataRoot, IsolatedDataRoot);
        RequireServicePathShape();
        var probe = requireSafePath ?? RejectExistingReparseComponents;
        foreach (var path in new[]
                 {
                     BinDirectory, ToolsDirectory,
                     NsisToolDirectory, NsisCompilerPath
                 })
        {
            RequireCanonicalAbsoluteDirectory(path, nameof(path));
            RequireContained(InstallRoot, path);
            probe(path);
        }
        foreach (var path in new[]
                 {
                     StateDirectory, InstallStateDirectory, UserStateDirectory,
                     UserConfigDirectory, UserLogDirectory, UserReceiptDirectory,
                     UserExecutionDirectory, TrayStateDirectory, ActiveCatalogPath
                 })
        {
            RequireCanonicalAbsoluteDirectory(path, nameof(path));
            RequireContained(dataRoot, path);
            probe(path);
        }
    }

    internal void RequireServiceStatePath(Action<string>? requireSafePath = null)
    {
        RequireServicePathShape();
        (requireSafePath ?? RejectExistingReparseComponents)(ServiceStateDirectory);
    }

    private void RequireServicePathShape()
    {
        RequireCanonicalAbsoluteDirectory(ServiceStateDirectory, nameof(ServiceStateDirectory));
        RequireContained(DataRoot, ServiceStateDirectory);
        if (!PathsEqual(ServiceStateDirectory, Path.Combine(DataRoot, "service")))
            throw new InvalidDataException("The Cortex service path is not the exact registered service-state directory.");
    }

    /// <summary>
    /// Selects the complete validated mutable-state root without reading,
    /// creating, moving, or otherwise touching its content.
    /// </summary>
    public CortexProductLayout WithDataRoot(string dataRoot) => SelectDataRoot(dataRoot, isolated: false);

    private CortexProductLayout SelectDataRoot(string dataRoot, bool isolated)
    {
        var canonical = RequireDataRoot(InstallRoot, dataRoot, isolated);
        var sid = RequireCanonicalOwnerSid(OwnerSid);
        var user = Path.Combine(canonical, "users", sid);
        var semantic = Path.Combine(user, "cortex");
        var layout = this with
        {
            DataRoot = canonical,
            IsolatedDataRoot = isolated,
            StateDirectory = canonical,
            InstallStateDirectory = Path.Combine(canonical, "install"),
            ServiceStateDirectory = Path.Combine(canonical, "service"),
            UserStateDirectory = user,
            CortexDataDirectory = Path.Combine(semantic, "data"),
            CortexDatabasePath = Path.Combine(semantic, "data", "cortex-v2.db"),
            LegacyCortexDatabasePath = Path.Combine(semantic, "data", "cortex.db"),
            UserConfigDirectory = Path.Combine(user, "config"),
            UserBackupDirectory = Path.Combine(semantic, "backups"),
            UserLogDirectory = Path.Combine(user, "logs"),
            UserReceiptDirectory = Path.Combine(user, "receipts"),
            UserExecutionDirectory = Path.Combine(user, "execution"),
            TrayStateDirectory = Path.Combine(user, "tray"),
            ActiveCatalogPath = Path.Combine(canonical, "install", "active.v1.json")
        };
        layout.RequireRuntimePathsWithinDeclaredRoots();
        return layout;
    }

    private static CortexProductLayout Create(
        string installRoot,
        string ownerSid,
        bool requireExistingAncestorsSafe,
        bool validateSemanticPaths = true)
    {
        var root = RequireCanonicalAbsoluteDirectory(installRoot, nameof(installRoot));
        var sid = RequireCanonicalOwnerSid(ownerSid);
        if (requireExistingAncestorsSafe)
            RejectExistingReparseComponents(root);

        string Under(params string[] segments)
        {
            var path = Path.GetFullPath(Path.Combine(new[] { root }.Concat(segments).ToArray()));
            RequireContained(root, path);
            RequirePathBudget(path);
            return path;
        }

        var state = Under("state");
        var user = Under("state", "users", sid);
        var cortexData = Under("state", "users", sid, "cortex", "data");
        var layout = new CortexProductLayout(
            root,
            Under("bin"),
            Under("tools"),
            Under("tools", "nsis", CortexProductIdentity.NsisVersion),
            Under("tools", "nsis", CortexProductIdentity.NsisVersion, "Bin", "makensis.exe"),
            state,
            Under("state", "install"),
            Under("state", "service"),
            user,
            cortexData,
            Under("state", "users", sid, "cortex", "data", "cortex-v2.db"),
            Under("state", "users", sid, "cortex", "data", "cortex.db"),
            Under("state", "users", sid, "config"),
            Under("state", "users", sid, "cortex", "backups"),
            Under("state", "users", sid, "logs"),
            Under("state", "users", sid, "receipts"),
            Under("state", "users", sid, "execution"),
            Under("state", "users", sid, "tray"),
            Under("state", "install", "active.v1.json"),
            sid);
        if (validateSemanticPaths)
            layout.RequireRuntimePathsWithinDeclaredRoots();
        else
            layout.RequireExecutionPathsWithinInstallRoot();
        return layout;
    }

    private static string RequireCanonicalOwnerSid(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 184 || !SidPattern().IsMatch(value))
            throw new ArgumentException("A canonical Windows owner SID is required.", nameof(value));
        if (value is "S-1-5-7" or "S-1-5-18" or "S-1-5-19" or "S-1-5-20" or "S-1-5-32-544")
            throw new ArgumentException("A privileged, anonymous, or service SID cannot own user Cortex state.", nameof(value));

        if (OperatingSystem.IsWindows())
        {
            var parsed = new SecurityIdentifier(value);
            if (!string.Equals(parsed.Value, value, StringComparison.Ordinal))
                throw new ArgumentException("The Windows owner SID is not canonical.", nameof(value));
        }
        return value;
    }

    internal static string RequireCanonicalAbsoluteDirectory(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("A fully qualified local directory is required.", parameterName);
        }

        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!string.Equals(
                canonical,
                Path.TrimEndingDirectorySeparator(value),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("The directory path must already be canonical.", parameterName);
        }
        var pathRoot = Path.GetPathRoot(canonical) ??
            throw new ArgumentException("A filesystem root is required.", parameterName);
        foreach (var segment in canonical[pathRoot.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 ||
                Encoding.UTF8.GetByteCount(segment) > 255 || ReservedPathSegmentPattern().IsMatch(segment))
                throw new ArgumentException("The Cortex product path contains an unsafe filesystem segment.", parameterName);
        }
        RequirePathBudget(canonical);
        return canonical;
    }

    private static void RequireContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".")
            return;
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A Cortex product path escaped the unified install root.");
        }
    }

    internal static void RequirePathBudget(string path)
    {
        if (OperatingSystem.IsWindows() && path.Length > MaximumWindowsPathCharacters)
            throw new PathTooLongException("A Cortex product path exceeds the 240-character budget.");
    }

    internal static void RequireProductionInstallRoot(string path, string rootKind = "install")
    {
        var volumeRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(volumeRoot) ||
            string.Equals(Path.TrimEndingDirectorySeparator(path), volumeRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The Cortex {rootKind} root cannot be a drive root.", nameof(path));

        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType != DriveType.Fixed)
            throw new ArgumentException($"The Cortex {rootKind} root must be on a local fixed drive.", nameof(path));

        foreach (var forbidden in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                     Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                     Path.GetTempPath(),
                     Environment.GetEnvironmentVariable("OneDrive"),
                     Environment.GetEnvironmentVariable("OneDriveConsumer"),
                     Environment.GetEnvironmentVariable("OneDriveCommercial")
                 }.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (IsSameOrContained(forbidden!, path))
                throw new ArgumentException(
                    $"The Cortex {rootKind} root cannot be beneath a user, temporary, Windows, or OneDrive directory.",
                    nameof(path));
        }
    }

    internal static string RequireDataRoot(string installRoot, string dataRoot, bool isolated = false)
    {
        var install = RequireCanonicalAbsoluteDirectory(installRoot, nameof(installRoot));
        var canonical = RequireCanonicalAbsoluteDirectory(dataRoot, nameof(dataRoot));
        RejectExistingReparseComponents(canonical);
        if (File.Exists(canonical))
            throw new InvalidDataException("The Cortex data root must be a directory.");

        var legacy = Path.Combine(install, "state");
        if (!PathsEqual(canonical, legacy))
        {
            if (!isolated)
                RequireProductionInstallRoot(canonical, "data");
            if (IsSameOrContained(install, canonical) || IsSameOrContained(canonical, install))
                throw new ArgumentException(
                    "The semantic data root must be separate from the installation, or exactly its legacy state directory.",
                    nameof(dataRoot));
        }
        return canonical;
    }

    internal static bool PathsEqual(string left, string right) => string.Equals(
        left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void RequireExactPath(string actual, string expected)
    {
        RequireCanonicalAbsoluteDirectory(actual, nameof(actual));
        RequirePathBudget(expected);
        if (!PathsEqual(actual, expected))
            throw new InvalidDataException("A Cortex semantic path does not match its declared data root and owner SID.");
        RejectExistingReparseComponents(actual);
    }

    private static bool IsSameOrContained(string parent, string candidate)
    {
        var canonicalParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var canonicalCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var relative = Path.GetRelativePath(canonicalParent, canonicalCandidate);
        return relative == "." ||
               (!Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    internal static void RejectExistingReparseComponents(string path)
    {
        var canonical = Path.GetFullPath(path);
        var root = Path.GetPathRoot(canonical) ??
            throw new ArgumentException("The Cortex product path has no filesystem root.", nameof(path));
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, canonical).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The unified Cortex product path crosses a reparse point.");
            if ((attributes & FileAttributes.Directory) == 0 && !PathsEqual(current, canonical))
                throw new IOException("A Cortex product path crosses a regular file.");
        }
    }

    [GeneratedRegex(@"^S-1-(?:\d+-)+\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();

    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedPathSegmentPattern();
}
