using Microsoft.Win32;

namespace Pegasus.Cortex.Product;

/// <summary>
/// Reads the administrator-owned installation registration. It only selects
/// paths; database initialization, upgrades, and all data access belong to the
/// application. There is no environment, profile, or descriptor-file override.
/// </summary>
public static class CortexDataLocation
{
    public const string RegistryKeyPath = @"SOFTWARE\Pegasus\Cortex";
    public const string InstallLocationValueName = "InstallLocation";
    public const string DataLocationValueName = "DataLocation";

    /// <summary>Default selection for a fresh installer, not a runtime fallback.</summary>
    public static string DefaultDataRoot()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The unified Cortex product layout requires Windows.");
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
            throw new InvalidOperationException("The machine ProgramData directory is unavailable.");
        var root = CortexProductLayout.RequireCanonicalAbsoluteDirectory(
            Path.Combine(programData, CortexProductIdentity.CompanyDirectory, CortexProductIdentity.ProductDirectory),
            nameof(programData));
        CortexProductLayout.RequireProductionInstallRoot(root, "data");
        CortexProductLayout.RejectExistingReparseComponents(root);
        if (File.Exists(root))
            throw new InvalidDataException("The default Cortex data root must be a directory.");
        return root;
    }

    /// <summary>
    /// Loads only the two bounded REG_SZ location values from the 64-bit HKLM
    /// product key. Missing registration retains the exact legacy state root;
    /// malformed, mismatched, or unavailable registered locations never fall back.
    /// </summary>
    public static CortexProductLayout Load(CortexProductLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!OperatingSystem.IsWindows())
            return ApplyRegistration(layout, null, null);

        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var registration = machine.OpenSubKey(RegistryKeyPath, writable: false);
        return ApplyRegistration(
            layout,
            ReadLocation(registration, InstallLocationValueName),
            ReadLocation(registration, DataLocationValueName));
    }

    /// <summary>
    /// Resolves the complete machine state root for the privileged service.
    /// It reads only the bounded machine registration and preserves the exact
    /// legacy InstallRoot\state fallback when no registration exists.
    /// </summary>
    public static string ResolveMachineDataRoot(string installRoot)
    {
        var canonicalInstall = CortexProductLayout.RequireCanonicalAbsoluteDirectory(
            installRoot, nameof(installRoot));
        if (!OperatingSystem.IsWindows()) return Path.Combine(canonicalInstall, "state");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var registration = machine.OpenSubKey(RegistryKeyPath, writable: false);
        var registeredInstall = ReadLocation(registration, InstallLocationValueName);
        var registeredData = ReadLocation(registration, DataLocationValueName);
        if (registeredInstall is null)
        {
            if (registeredData is not null)
                throw new InvalidDataException("The registered Cortex data location has no installation binding.");
            return Path.Combine(canonicalInstall, "state");
        }
        var admittedInstall = CortexProductLayout.RequireCanonicalAbsoluteDirectory(
            registeredInstall, nameof(registeredInstall));
        if (!CortexProductLayout.PathsEqual(admittedInstall, canonicalInstall))
            throw new InvalidDataException("The registered Cortex installation does not match the executing product root.");
        var selected = registeredData is null
            ? Path.Combine(canonicalInstall, "state")
            : CortexProductLayout.RequireDataRoot(canonicalInstall, registeredData);
        CortexProductLayout.RejectExistingReparseComponents(selected);
        return selected;
    }

    /// <summary>
    /// Applies explicit registration values without accessing the registry.
    /// Null means absent, whereas empty or malformed values are rejected. This
    /// seam allows focused tests without consulting or changing live machine state.
    /// </summary>
    public static CortexProductLayout ApplyRegistration(
        CortexProductLayout layout,
        string? registeredInstallRoot,
        string? registeredDataRoot)
    {
        ArgumentNullException.ThrowIfNull(layout);
        layout.RequireExecutionPathsWithinInstallRoot();
        if (registeredInstallRoot is null)
        {
            if (registeredDataRoot is not null)
                throw new InvalidDataException("The registered Cortex data location has no installation binding.");
            return layout.WithDataRoot(Path.Combine(layout.InstallRoot, "state"));
        }

        var installRoot = CortexProductLayout.RequireCanonicalAbsoluteDirectory(
            registeredInstallRoot, nameof(registeredInstallRoot));
        CortexProductLayout.RejectExistingReparseComponents(installRoot);
        if (!CortexProductLayout.PathsEqual(installRoot, layout.InstallRoot))
            throw new InvalidDataException("The registered Cortex installation does not match the executing product root.");

        if (registeredDataRoot is null)
        {
            var legacy = layout.WithDataRoot(Path.Combine(layout.InstallRoot, "state"));
            return legacy with { RegisteredDataRoot = legacy.DataRoot };
        }

        // An explicit isolated fixture may preserve its already validated test
        // roots; production factories never set that exception. Selecting any
        // different root always passes normal durable-root validation.
        var selected = CortexProductLayout.PathsEqual(layout.DataRoot, registeredDataRoot)
            ? layout
            : layout.WithDataRoot(registeredDataRoot);
        selected.RequireRuntimePathsWithinDeclaredRoots();
        // A configured drive/folder becoming unavailable must not silently
        // produce a new empty store beneath the installation or a user profile.
        var attributes = File.GetAttributes(selected.DataRoot);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The registered Cortex data location is not a regular directory.");
        return selected with { RegisteredDataRoot = selected.DataRoot };
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadLocation(RegistryKey? registration, string name)
    {
        if (registration is null)
            return null;
        var value = registration.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
            return null;
        if (registration.GetValueKind(name) != RegistryValueKind.String ||
            value is not string text || string.IsNullOrWhiteSpace(text) ||
            text.Length > CortexProductLayout.MaximumWindowsPathCharacters)
            throw new InvalidDataException($"The registered Cortex {name} must be a bounded canonical REG_SZ directory.");
        return text;
    }
}
