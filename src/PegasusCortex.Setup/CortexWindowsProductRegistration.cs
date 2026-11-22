using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

internal sealed record CortexServiceRecoveryAction(uint Type,uint DelayMilliseconds);
internal sealed record CortexServiceRecoveryPolicy(
    uint ResetPeriodSeconds,
    bool ApplyToNonCrashFailures,
    IReadOnlyList<CortexServiceRecoveryAction> Actions);
internal sealed record CortexRegistrationCleanupFailure(string Stage, string Cause, int? Win32);

public sealed record CortexWindowsRegistrationReceiptV1(
    string Contract,
    int Version,
    string ReleaseVersion,
    string ServiceName,
    string ServiceExecutable,
    bool ServiceCreated,
    string PathEntry,
    bool PathEntryAdded,
    string RunValueName,
    string RunExecutable,
    bool RunValueCreated,
    bool RunValueChanged,
    string? PreviousRunValue,
    string? DataRoot = null);

public sealed record CortexWindowsUnregistrationReceiptV1(
    string Contract,
    int Version,
    string InstallRoot,
    string ServiceName,
    bool ServiceRemoved,
    string PathEntry,
    bool PathEntryRemoved,
    string RunValueName,
    bool RunValueRemovedOrRestored,
    bool StateRetained);

public static class CortexWindowsProductRegistration
{
    internal static CortexServiceRecoveryPolicy RecoveryPolicyForTest =>
        ServiceRegistration.DesiredRecoveryPolicy;
    internal static uint RecoveryRestoreAccessForTest(CortexServiceRecoveryPolicy? policy) =>
        ServiceRegistration.RecoveryRestoreAccess(policy);
    private const string CleanupFailuresKey = "Pegasus.Cortex.Setup.RegistrationCleanup.v1";
    internal const int MaximumCleanupFailures = 6;

    internal static IReadOnlyList<CortexRegistrationCleanupFailure> CleanupFailures(Exception primary) =>
        primary.Data[CleanupFailuresKey] as IReadOnlyList<CortexRegistrationCleanupFailure> ?? [];

    internal static void AttemptCleanup(Exception primary, string stage, Action cleanup)
    {
        try { cleanup(); }
        catch (Exception failure)
        {
            // Cleanup is secondary evidence. Never replace the primary exception
            // or retain arbitrary cleanup messages, paths, or command contents.
            try
            {
                var failures = CleanupFailures(primary);
                if (failures.Count < MaximumCleanupFailures)
                    primary.Data[CleanupFailuresKey] = failures.Append(new CortexRegistrationCleanupFailure(
                        stage.Length <= 48 && stage.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') ? stage : "cleanup",
                        failure.GetType().Name.Length <= 96 ? failure.GetType().Name : "Exception",
                        failure is Win32Exception native ? native.NativeErrorCode : null)).ToArray();
            }
            catch { /* Even diagnostic attachment must not replace the primary failure. */ }
        }
    }
    public const string ReceiptContract = "pegasus-cortex-windows-registration-receipt";
    public const string UnregistrationReceiptContract = "pegasus-cortex-windows-unregistration-receipt";
    private const string RunValueName = "Pegasus Cortex";
    private const string EnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ProductRegistryKey = @"Software\Pegasus\Cortex";

    public static CortexWindowsRegistrationReceiptV1 Apply(
        CortexProductLayout layout,
        CortexProductCatalogV1 catalog) => ApplyCore(layout, catalog, null);

    internal static CortexWindowsRegistrationReceiptV1 ApplyRelocated(CortexProductLayout layout,
        CortexProductCatalogV1 catalog, CortexDataRootRelocation relocation) => ApplyCore(layout, catalog, relocation);

    private static CortexWindowsRegistrationReceiptV1 ApplyCore(CortexProductLayout layout,
        CortexProductCatalogV1 catalog, CortexDataRootRelocation? relocation)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Cortex product registration requires Windows.");
        RequireAdministrator();
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(catalog);
        layout.RequireRuntimePathsWithinInstallRoot();
        catalog.Validate();
        RequireExistingProductLocations(layout, relocation);

        var serviceExecutable = ResolveRole(layout, catalog, "service");
        var userSessionExecutable = ResolveUserSessionExecutable(layout, catalog);
        var paoExecutable = ResolveRole(layout, catalog, "pao");
        if (!string.Equals(Path.GetDirectoryName(paoExecutable), layout.BinDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The stable PAO client is outside the product bin directory.");
        var previousRegistration = ReadPreviousRegistration(layout);

        string? originalPath = null;
        string? originalRun = null;
        var pathChanged = false;
        var runChanged = false;
        var productLocationChanged = false;
        var dataLocationChanged = false;
        var productVersionChanged = false;
        var selectionAttempted = false;
        string? originalProductLocation = null;
        string? originalDataLocation = null;
        string? originalProductVersion = null;
        try
        {
            using (var environment = Registry.LocalMachine.OpenSubKey(EnvironmentKey, writable: true) ??
                   throw new InvalidOperationException("The machine environment registry key is unavailable."))
            {
                originalPath = environment.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? string.Empty;
                var entries = originalPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (!entries.Any(entry => string.Equals(
                        Path.TrimEndingDirectorySeparator(entry.Trim('"')),
                        Path.TrimEndingDirectorySeparator(layout.BinDirectory),
                        StringComparison.OrdinalIgnoreCase)))
                {
                    entries.Add(layout.BinDirectory);
                    environment.SetValue("Path", string.Join(';', entries), RegistryValueKind.ExpandString);
                    pathChanged = true;
                }
            }

            using (var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true) ??
                   throw new InvalidOperationException("The current-user Run registry key is unavailable."))
            {
                originalRun = run.GetValue(RunValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                // Windows logon starts the admitted WinExe host directly. The
                // stable launcher also serves explicit CLI/STDIO modes; logon
                // does not need its extra supervising process and job lifetime.
                var desired = Quote(userSessionExecutable);
                // Preserve the owner's login choice. A fresh install is manual;
                // an existing valid opt-in remains enabled across updates.
                if (originalRun is not null && !string.Equals(originalRun, desired, StringComparison.Ordinal))
                    throw new InvalidDataException("The existing Cortex login startup entry conflicts with this installation; it was preserved.");
            }

            // Prepare the target receipt before publishing a new machine data
            // selection. Runtime never needs to consume a half-prepared target.
            var ownership = CarryForwardOwnership(previousRegistration, pathChanged, runChanged, originalRun);
            var receipt = new CortexWindowsRegistrationReceiptV1(
                ReceiptContract, 1, catalog.ReleaseVersion, CortexProductIdentity.ServiceName, serviceExecutable,
                previousRegistration?.ServiceCreated == true, layout.BinDirectory, ownership.PathEntryAdded,
                RunValueName, userSessionExecutable, ownership.RunValueCreated, ownership.RunValueChanged,
                ownership.PreviousRunValue, layout.DataRoot);
            Directory.CreateDirectory(layout.InstallStateDirectory);
            WriteAtomic(Path.Combine(layout.InstallStateDirectory, "registration.v1.json"), JsonSerializer.Serialize(receipt) + "\n");
            BroadcastEnvironmentChange();

            using (var product = Registry.LocalMachine.CreateSubKey(ProductRegistryKey, writable: true) ??
                   throw new InvalidOperationException("The Cortex product registry key is unavailable."))
            {
                originalProductLocation = product.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                originalDataLocation = product.GetValue("DataLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                originalProductVersion = product.GetValue("ReleaseVersion", null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                if (!string.Equals(originalProductLocation, layout.InstallRoot, StringComparison.OrdinalIgnoreCase))
                {
                    product.SetValue("InstallLocation", layout.InstallRoot, RegistryValueKind.String);
                    productLocationChanged = true;
                }
                if (!string.Equals(originalProductVersion, catalog.ReleaseVersion, StringComparison.Ordinal))
                {
                    product.SetValue("ReleaseVersion", catalog.ReleaseVersion, RegistryValueKind.String);
                    productVersionChanged = true;
                }
                if (!string.Equals(originalDataLocation, layout.DataRoot, StringComparison.OrdinalIgnoreCase))
                {
                    RequireExistingProductLocations(layout, relocation);
                    selectionAttempted = true;
                    product.SetValue("DataLocation", layout.DataRoot, RegistryValueKind.String);
                    dataLocationChanged = true;
                }
                product.Flush();
            }

            // Installing/updating the ordinary product never installs, enables,
            // or starts the optional privileged execution service. Its existing
            // startup policy is preserved until an explicit Cortex control.
            return receipt;
        }
        catch (Exception primary)
        {
            // An attempted relocation commit can have succeeded even if its
            // acknowledgement/flush failed. Preserve the actual key for review;
            // never silently switch back and resume the old data.
            if ((relocation is null || !selectionAttempted) &&
                (productLocationChanged || dataLocationChanged || productVersionChanged))
                AttemptCleanup(primary, "restore-product-registration", () =>
            {
                using var product = Registry.LocalMachine.CreateSubKey(ProductRegistryKey, writable: true);
                if (product is not null)
                {
                    RestoreValue(product, "InstallLocation", originalProductLocation);
                    RestoreValue(product, "DataLocation", originalDataLocation);
                    RestoreValue(product, "ReleaseVersion", originalProductVersion);
                }
                DeleteProductKeyIfEmpty();
            });
            if (runChanged)
                AttemptCleanup(primary, "restore-user-run", () =>
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (run is not null)
                {
                    if (originalRun is null) run.DeleteValue(RunValueName, throwOnMissingValue: false);
                    else run.SetValue(RunValueName, originalRun, RegistryValueKind.String);
                }
            });
            if (pathChanged && originalPath is not null)
                AttemptCleanup(primary, "restore-machine-path", () =>
            {
                using var environment = Registry.LocalMachine.OpenSubKey(EnvironmentKey, writable: true);
                environment?.SetValue("Path", originalPath, RegistryValueKind.ExpandString);
                BroadcastEnvironmentChange();
            });
            throw;
        }
    }

    internal static bool StopOwnedServiceForUpdate(CortexProductLayout layout)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Cortex service lifecycle control requires Windows.");
        RequireAdministrator();
        ArgumentNullException.ThrowIfNull(layout);
        layout.RequireRuntimePathsWithinInstallRoot();
        return ServiceRegistration.StopOwnedForUpdate(
            CortexProductIdentity.ServiceName, layout.InstallRoot);
    }

    internal static void RequireExistingProductLocations(CortexProductLayout layout, CortexDataRootRelocation? relocation = null)
    {
        relocation?.RequirePublished(layout);
        using var product = Registry.LocalMachine.OpenSubKey(ProductRegistryKey, writable: false);
        foreach (var (name, expected) in new[] { ("InstallLocation", layout.InstallRoot),
                     ("DataLocation", relocation?.Source.DataRoot ?? layout.DataRoot) })
        {
            var value = product?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null) continue; // only missing defaults may be filled
            if (value is not string text || product!.GetValueKind(name) != RegistryValueKind.String)
                throw new InvalidDataException("The existing Cortex location registration is invalid; it was preserved.");
            RequireMatchingLocation(text, expected);
        }
    }

    internal static void RequireMatchingLocation(string existing, string expected)
    {
        if (string.IsNullOrWhiteSpace(existing) || !Path.IsPathFullyQualified(existing) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(existing)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The existing Cortex location conflicts with this installation; it was preserved.");
    }

    internal static void RequireOwnedServiceStopped(CortexProductLayout layout)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Cortex update admission requires Windows.");
        _ = ServiceRegistration.RequireOwned(CortexProductIdentity.ServiceName,
            Path.Combine(layout.InstallRoot, CortexProductCatalogV1.StableRolePaths["service"].Replace('/', Path.DirectorySeparatorChar)),
            requireStopped: true);
    }

    internal static bool HasOwnedOptionalService(CortexProductLayout layout, string executable) =>
        ServiceRegistration.RequireOwned(CortexProductIdentity.ServiceName, executable, requireStopped: false);

    internal static bool PreviouslyCreatedService(CortexProductLayout layout)
    {
        var previous = ReadPreviousRegistration(layout);
        if (previous is null) return false;
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(layout.InstallStateDirectory, "registration.v1.json")));
        if (!document.RootElement.TryGetProperty("ServiceCreated", out var created) ||
            created.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("The existing service-ownership receipt is incomplete; maintenance defaults were not created.");
        return previous.ServiceCreated;
    }

    internal static bool InstallOptionalService(CortexProductLayout layout, string serviceExecutable)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Cortex service installation requires Windows.");
        RequireAdministrator();
        layout.RequireRuntimePathsWithinDeclaredRoots();
        var change = ServiceRegistration.Apply(CortexProductIdentity.ServiceName,
            "Pegasus Cortex Execution", Quote(serviceExecutable), layout.InstallRoot,
            desiredStartType: 3, preserveExisting: true);
        return change.Created;
    }

    public static CortexWindowsUnregistrationReceiptV1 Remove(
        CortexProductLayout layout,
        CortexProductCatalogV1 catalog)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Cortex product unregistration requires Windows.");
        RequireAdministrator();
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(catalog);
        layout.RequireRuntimePathsWithinInstallRoot();
        catalog.Validate();

        var serviceExecutable = ResolveRole(layout, catalog, "service");
        var userSessionExecutable = ResolveUserSessionExecutable(layout, catalog);
        var registrationPath = Path.Combine(layout.InstallStateDirectory, "registration.v1.json");
        CortexWindowsRegistrationReceiptV1? registration = null;
        if (File.Exists(registrationPath))
        {
            registration = JsonSerializer.Deserialize<CortexWindowsRegistrationReceiptV1>(
                File.ReadAllText(registrationPath)) ??
                throw new InvalidDataException("The Cortex registration receipt is invalid.");
            if (registration.Contract != ReceiptContract || registration.Version != 1 ||
                (registration.DataRoot is not null &&
                 !string.Equals(registration.DataRoot, layout.DataRoot, StringComparison.OrdinalIgnoreCase)) ||
                !string.Equals(registration.PathEntry, layout.BinDirectory, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(registration.RunExecutable, userSessionExecutable, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(registration.ServiceExecutable, serviceExecutable, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Cortex registration receipt does not match this product layout.");
        }

        var serviceRemoved = ServiceRegistration.RemoveOwned(
            CortexProductIdentity.ServiceName, serviceExecutable, layout.InstallRoot);
        var pathRemoved = false;
        if (registration?.PathEntryAdded == true)
        {
            using var environment = Registry.LocalMachine.OpenSubKey(EnvironmentKey, writable: true) ??
                throw new InvalidOperationException("The machine environment registry key is unavailable.");
            var current = environment.GetValue(
                "Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? string.Empty;
            var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var index = entries.FindIndex(entry => string.Equals(
                Path.TrimEndingDirectorySeparator(entry.Trim('"')),
                Path.TrimEndingDirectorySeparator(layout.BinDirectory),
                StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                entries.RemoveAt(index);
                environment.SetValue("Path", string.Join(';', entries), RegistryValueKind.ExpandString);
                pathRemoved = true;
            }
        }

        var runChanged = false;
        if (registration?.RunValueChanged == true)
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true) ??
                throw new InvalidOperationException("The current-user Run registry key is unavailable.");
            var current = run.GetValue(RunValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
            if (string.Equals(current, Quote(userSessionExecutable), StringComparison.Ordinal))
            {
                if (registration.PreviousRunValue is null)
                    run.DeleteValue(RunValueName, throwOnMissingValue: false);
                else
                    run.SetValue(RunValueName, registration.PreviousRunValue, RegistryValueKind.String);
                runChanged = true;
            }
        }
        if (pathRemoved) BroadcastEnvironmentChange();
        using (var product = Registry.LocalMachine.OpenSubKey(ProductRegistryKey, writable: true))
        {
            var currentLocation = product?.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
            if (product is not null && string.Equals(currentLocation, layout.InstallRoot, StringComparison.OrdinalIgnoreCase))
            {
                // Ordinary uninstall retains the association with preserved data. A later
                // reinstall must not silently create an unrelated empty semantic store.
                // Removing install-local state must not unlink a retained external database.
                product.SetValue("DataLocation", layout.DataRoot, RegistryValueKind.String);
                product.DeleteValue("ReleaseVersion", throwOnMissingValue: false);
            }
        }
        DeleteProductKeyIfEmpty();

        return new CortexWindowsUnregistrationReceiptV1(
            UnregistrationReceiptContract, 1, layout.InstallRoot,
            CortexProductIdentity.ServiceName, serviceRemoved,
            layout.BinDirectory, pathRemoved,
            RunValueName, runChanged, StateRetained: true);
    }

    internal static string ResolveRole(CortexProductLayout layout, CortexProductCatalogV1 catalog, string role)
    {
        var value = catalog.Roles.SingleOrDefault(item => item.Role == role) ??
            throw new InvalidDataException($"The product catalog omits role '{role}'.");
        var path = Path.GetFullPath(Path.Combine(
            layout.InstallRoot,
            value.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(layout.InstallRoot, path);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("A product role escaped the install root.");
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("A product role executable is absent or unsafe.", path);
        return path;
    }

    internal static string ResolveUserSessionExecutable(
        CortexProductLayout layout,
        CortexProductCatalogV1 catalog) => ResolveRole(layout, catalog, "host");

    internal static (bool PathEntryAdded, bool RunValueCreated, bool RunValueChanged, string? PreviousRunValue)
        CarryForwardOwnership(
            CortexWindowsRegistrationReceiptV1? previous,
            bool pathChanged,
            bool runChanged,
            string? originalRun)
    {
        var pathEntryAdded = pathChanged || previous?.PathEntryAdded == true;
        var previousRunValue = previous?.RunValueChanged == true
            ? previous.PreviousRunValue
            : originalRun;
        var runValueChanged = runChanged || previous?.RunValueChanged == true;
        return (
            pathEntryAdded,
            runValueChanged && previousRunValue is null,
            runValueChanged,
            previousRunValue);
    }

    private static CortexWindowsRegistrationReceiptV1? ReadPreviousRegistration(CortexProductLayout layout)
    {
        var path = Path.Combine(layout.InstallStateDirectory, "registration.v1.json");
        if (!File.Exists(path)) return null;
        var value = JsonSerializer.Deserialize<CortexWindowsRegistrationReceiptV1>(File.ReadAllText(path)) ??
            throw new InvalidDataException("The previous Cortex registration receipt is invalid.");
        if (value.Contract != ReceiptContract || value.Version != 1 ||
            (value.DataRoot is not null &&
             !string.Equals(value.DataRoot, layout.DataRoot, StringComparison.OrdinalIgnoreCase)) ||
            value.ServiceName != CortexProductIdentity.ServiceName || value.RunValueName != RunValueName ||
            !string.Equals(value.PathEntry, layout.BinDirectory, StringComparison.OrdinalIgnoreCase) ||
            !IsContained(layout.InstallRoot, value.ServiceExecutable) ||
            !IsContained(layout.InstallRoot, value.RunExecutable))
            throw new InvalidDataException("The previous Cortex registration receipt does not match this product layout.");
        return value;
    }

    private static bool IsContained(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        return relative != "." && !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Cortex machine registration requires an elevated administrator token.");
    }

    private static string Quote(string value)
    {
        if (value.Contains('"') || value.Any(char.IsControl))
            throw new InvalidDataException("A product executable path cannot be quoted safely.");
        return '"' + value + '"';
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RestoreValue(RegistryKey key, string name, string? value)
    {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    private static void DeleteProductKeyIfEmpty()
    {
        using var product = Registry.LocalMachine.OpenSubKey(ProductRegistryKey, writable: false);
        if (product is null || product.ValueCount != 0 || product.SubKeyCount != 0)
            return;
        product.Dispose();
        Registry.LocalMachine.DeleteSubKey(ProductRegistryKey, throwOnMissingSubKey: false);
    }

    private static void BroadcastEnvironmentChange()
    {
        var result = SendMessageTimeout(
            new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "Environment",
            0x0002, 5000, out _);
        if (result == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The machine environment change broadcast failed.");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, UIntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);

    private sealed record ServiceChange(
        string Name,
        bool Created,
        string? PreviousBinaryPath,
        uint PreviousStartType,
        CortexServiceRecoveryPolicy? PreviousRecoveryPolicy,
        string DesiredBinaryPath,
        string InstallRoot);

    internal static void RequireServiceReady(string expectedExecutable) =>
        ServiceRegistration.RequireReady(CortexProductIdentity.ServiceName, expectedExecutable);

    private static class ServiceRegistration
    {
        private const uint ScManagerConnect = 0x0001;
        private const uint ScManagerCreateService = 0x0002;
        private const uint ServiceQueryConfig = 0x0001;
        private const uint ServiceChangeConfig = 0x0002;
        private const uint ServiceStart = 0x0010;
        private const uint ServiceStop = 0x0020;
        private const uint ServiceQueryStatus = 0x0004;
        private const uint Delete = 0x00010000;
        private const uint ServiceWin32OwnProcess = 0x00000010;
        private const uint ServiceAutoStart = 0x00000002;
        private const uint ServiceErrorNormal = 0x00000001;
        private const uint ServiceNoChange = 0xffffffff;
        private const uint ServiceConfigFailureActions = 2;
        private const uint ServiceConfigFailureActionsFlag = 4;
        private const uint ScActionNone = 0;
        private const uint ScActionRestart = 1;
        private const int ErrorServiceExists = 1073;
        private const int ErrorServiceAlreadyRunning = 1056;
        private const int ErrorServiceDoesNotExist = 1060;
        private const int ErrorServiceNotActive = 1062;
        private const uint ServiceControlStop = 0x00000001;
        private const uint ServiceStopped = 0x00000001;
        private const uint ServiceStartPending = 0x00000002;
        private const uint ServiceRunning = 0x00000004;

        internal static CortexServiceRecoveryPolicy DesiredRecoveryPolicy { get; } = new(
            ResetPeriodSeconds: 24 * 60 * 60,
            ApplyToNonCrashFailures: true,
            Actions:
            [
                new(ScActionRestart, 5_000),
                new(ScActionRestart, 15_000),
                new(ScActionRestart, 60_000)
            ]);

        internal static bool RequireOwned(string name, string expectedExecutable, bool requireStopped)
        {
            var manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var service = OpenService(manager, name, ServiceQueryConfig | ServiceQueryStatus);
                if (service == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorServiceDoesNotExist) return false;
                    throw new Win32Exception(error);
                }
                try
                {
                    var configuration = Query(service);
                    if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (!string.Equals(UnquoteExecutable(configuration.BinaryPath), expectedExecutable, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(configuration.Account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The registered Cortex service belongs to a different image or account.");
                    if (requireStopped && status.CurrentState != ServiceStopped)
                        throw new InvalidOperationException("Exit Cortex and stop its optional administrator service before updating. Setup did not stop or reconfigure it.");
                    return true;
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        public static void RequireReady(string name, string expectedExecutable)
        {
            var manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var service = OpenService(manager, name, ServiceQueryConfig | ServiceQueryStatus);
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var configuration = Query(service);
                    if (!QueryServiceStatus(service, out var status))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (status.CurrentState != ServiceRunning ||
                        !string.Equals(Path.GetFullPath(UnquoteExecutable(configuration.BinaryPath)),
                            Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The admitted Cortex execution service is not running at the expected product path.");
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        public static ServiceChange Apply(string name, string displayName, string binaryPath, string installRoot,
            uint desiredStartType = ServiceAutoStart, bool preserveExisting = false)
        {
            var manager = OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var service = CreateService(
                    manager, name, displayName,
                    ServiceQueryConfig | ServiceChangeConfig | ServiceStart | Delete,
                    ServiceWin32OwnProcess, desiredStartType, ServiceErrorNormal,
                    binaryPath, null, IntPtr.Zero, null, null, null);
                if (service != IntPtr.Zero)
                {
                    try
                    {
                        SetRecoveryPolicy(service, DesiredRecoveryPolicy);
                        return new ServiceChange(name, true, null, desiredStartType, null, binaryPath, installRoot);
                    }
                    catch (Exception primary)
                    {
                        AttemptCleanup(primary, "remove-created-service", () =>
                        {
                            if (!DeleteService(service)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        });
                        throw;
                    }
                    finally { CloseServiceHandle(service); }
                }
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorServiceExists) throw new Win32Exception(error);
                service = OpenService(manager, name, ServiceQueryConfig | ServiceChangeConfig | ServiceStart);
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var previous = Query(service);
                    if (preserveExisting)
                    {
                        if (!string.Equals(Path.GetFullPath(UnquoteExecutable(previous.BinaryPath)),
                                Path.GetFullPath(UnquoteExecutable(binaryPath)), StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(previous.Account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("The existing Cortex service identity conflicts with the admitted optional service.");
                        // An already installed Disabled service stays Disabled.
                        // Install is not an implicit enable, update, or start.
                        return new ServiceChange(name, false, previous.BinaryPath, previous.StartType,
                            null, binaryPath, installRoot);
                    }
                    var previousRecovery = QueryRecoveryPolicy(service);
                    var executable = UnquoteExecutable(previous.BinaryPath);
                    var relative = Path.GetRelativePath(installRoot, executable);
                    if (Path.IsPathRooted(relative) || relative == ".." ||
                        relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidOperationException("An existing service with the Cortex identity is not product-owned.");
                    try
                    {
                        if (!ChangeServiceConfig(
                                service, ServiceNoChange, ServiceAutoStart, ServiceNoChange,
                                binaryPath, null, IntPtr.Zero, null, null, null, displayName))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        SetRecoveryPolicy(service, DesiredRecoveryPolicy);
                        return new ServiceChange(name, false, previous.BinaryPath, previous.StartType, previousRecovery, binaryPath, installRoot);
                    }
                    catch (Exception primary)
                    {
                        AttemptCleanup(primary, "restore-service-config", () =>
                        {
                            if (!ChangeServiceConfig(
                                    service, ServiceNoChange, previous.StartType, ServiceNoChange,
                                    previous.BinaryPath, null, IntPtr.Zero, null, null, null, null))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                        });
                        AttemptCleanup(primary, "restore-service-recovery", () => SetRecoveryPolicy(service, previousRecovery));
                        throw;
                    }
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        public static void Start(string name, string serviceStateDirectory)
        {
            var manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var service = OpenService(manager, name, ServiceStart | ServiceQueryStatus);
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    if (!StartService(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != ErrorServiceAlreadyRunning)
                    {
                        var error = Marshal.GetLastWin32Error();
                        throw new Win32Exception(error, ServiceStartDiagnostic(serviceStateDirectory, error));
                    }
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (true)
                    {
                        if (!QueryServiceStatus(service, out var status))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        if (status.CurrentState == ServiceRunning)
                        {
                            Thread.Sleep(500);
                            if (!QueryServiceStatus(service, out status))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                            if (status.CurrentState == ServiceRunning)
                                break;
                        }
                        if (status.CurrentState == ServiceStopped)
                            throw new InvalidOperationException(ServiceStartDiagnostic(
                                serviceStateDirectory, checked((int)status.Win32ExitCode)));
                        if (status.CurrentState != ServiceStartPending || DateTime.UtcNow >= deadline)
                            throw new TimeoutException(ServiceStartDiagnostic(serviceStateDirectory, 1053));
                        Thread.Sleep(100);
                    }
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        private static string ServiceStartDiagnostic(string serviceStateDirectory, int error)
        {
            var message = $"The Cortex execution service failed to reach RUNNING state (win32={error}).";
            try
            {
                var path = Path.Combine(serviceStateDirectory, "startup.log");
                if (!File.Exists(path)) return message + " No bounded service startup log was produced.";
                var lines = File.ReadLines(path).TakeLast(8)
                    .Select(line => line.Replace('\r', ' ').Replace('\n', ' ').Trim())
                    .Where(line => line.Length > 0)
                    .Select(line => line.Length <= 256 ? line : line[..256]);
                var tail = string.Join(" | ", lines);
                return tail.Length == 0 ? message : message + " startup=" + tail;
            }
            catch
            {
                return message + " The bounded service startup log could not be read.";
            }
        }

        public static bool StopOwnedForUpdate(string name, string installRoot)
        {
            var manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var service = OpenService(
                    manager, name, ServiceQueryConfig | ServiceQueryStatus | ServiceStop);
                if (service == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorServiceDoesNotExist) return false;
                    throw new Win32Exception(error);
                }
                try
                {
                    var executable = Path.GetFullPath(UnquoteExecutable(Query(service).BinaryPath));
                    var relative = Path.GetRelativePath(installRoot, executable);
                    if (Path.IsPathRooted(relative) || relative == ".." ||
                        relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            "The existing Cortex service is outside the admitted product root.");

                    if (!QueryServiceStatus(service, out var status))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (status.CurrentState == ServiceStopped)
                        return false;
                    if (!ControlService(service, ServiceControlStop, out _) &&
                        Marshal.GetLastWin32Error() != ErrorServiceNotActive)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    var deadline = DateTime.UtcNow.AddSeconds(15);
                    while (true)
                    {
                        if (!QueryServiceStatus(service, out status))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cortex service shutdown could not be confirmed.");
                        if (status.CurrentState == ServiceStopped) return true;
                        if (DateTime.UtcNow >= deadline)
                            throw new TimeoutException("The Cortex execution service did not stop within 15 seconds.");
                        Thread.Sleep(100);
                    }
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        public static void RequireOwned(string name, string installRoot)
        {
            var manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var service = OpenService(manager, name, ServiceQueryConfig);
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var executable = Path.GetFullPath(UnquoteExecutable(Query(service).BinaryPath));
                    var relative = Path.GetRelativePath(installRoot, executable);
                    if (Path.IsPathRooted(relative) || relative == ".." ||
                        relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidOperationException("The existing Cortex service is outside the admitted product root.");
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        internal static uint RecoveryRestoreAccess(CortexServiceRecoveryPolicy? policy) =>
            ServiceChangeConfig | Delete |
            (policy?.Actions.Any(action => action.Type == ScActionRestart) == true ? ServiceStart : 0u);

        public static void Rollback(ServiceChange change, Exception primary)
        {
            var manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                // Restoring restart actions requires SERVICE_START even though
                // rollback does not itself launch the service.
                var access = RecoveryRestoreAccess(change.PreviousRecoveryPolicy);
                var service = OpenService(manager, change.Name, access);
                if (service == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorServiceDoesNotExist) return;
                    throw new Win32Exception(error);
                }
                try
                {
                    if (change.Created)
                    {
                        if (!DeleteService(service)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    else if (change.PreviousBinaryPath is not null)
                    {
                        AttemptCleanup(primary, "restore-service-config", () =>
                        {
                            if (!ChangeServiceConfig(
                                    service, ServiceNoChange, change.PreviousStartType, ServiceNoChange,
                                    change.PreviousBinaryPath, null, IntPtr.Zero, null, null, null, null))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                        });
                        if (change.PreviousRecoveryPolicy is not null)
                            AttemptCleanup(primary, "restore-service-recovery", () => SetRecoveryPolicy(service, change.PreviousRecoveryPolicy));
                    }
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        public static bool RemoveOwned(string name, string expectedExecutable, string installRoot)
        {
            var manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var service = OpenService(
                    manager, name, ServiceQueryConfig | ServiceQueryStatus | ServiceStop | Delete);
                if (service == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorServiceDoesNotExist) return false;
                    throw new Win32Exception(error);
                }
                try
                {
                    var current = Path.GetFullPath(UnquoteExecutable(Query(service).BinaryPath));
                    var relative = Path.GetRelativePath(installRoot, current);
                    if (Path.IsPathRooted(relative) || relative == ".." ||
                        relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                        !string.Equals(current, Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The existing Cortex service is not owned by this admitted product.");

                    if (!ControlService(service, ServiceControlStop, out _) &&
                        Marshal.GetLastWin32Error() != ErrorServiceNotActive)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    var deadline = DateTime.UtcNow.AddSeconds(15);
                    while (QueryServiceStatus(service, out var status) && status.CurrentState != ServiceStopped)
                    {
                        if (DateTime.UtcNow >= deadline)
                            throw new TimeoutException("The Cortex execution service did not stop within 15 seconds.");
                        Thread.Sleep(100);
                    }
                    if (!DeleteService(service)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    return true;
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        private static ServiceConfiguration Query(IntPtr service)
        {
            _ = QueryServiceConfig(service, IntPtr.Zero, 0, out var bytesNeeded);
            var error = Marshal.GetLastWin32Error();
            if (bytesNeeded == 0 || error != 122) throw new Win32Exception(error);
            var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
            try
            {
                if (!QueryServiceConfig(service, buffer, bytesNeeded, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var value = Marshal.PtrToStructure<QueryServiceConfigValue>(buffer);
                return new ServiceConfiguration(
                    Marshal.PtrToStringUni(value.BinaryPathName) ?? throw new InvalidDataException("The service binary path is absent."),
                    value.StartType,
                    Marshal.PtrToStringUni(value.ServiceStartName) ?? string.Empty);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static CortexServiceRecoveryPolicy QueryRecoveryPolicy(IntPtr service)
        {
            _ = QueryServiceConfig2(service, ServiceConfigFailureActions, IntPtr.Zero, 0, out var bytesNeeded);
            var error = Marshal.GetLastWin32Error();
            if (bytesNeeded == 0 || error != 122) throw new Win32Exception(error);
            var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
            try
            {
                if (!QueryServiceConfig2(service, ServiceConfigFailureActions, buffer, bytesNeeded, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var value = Marshal.PtrToStructure<ServiceFailureActionsValue>(buffer);
                if (value.RebootMessage != IntPtr.Zero || value.Command != IntPtr.Zero)
                    throw new InvalidOperationException("The existing Cortex service recovery policy cannot be preserved safely.");
                if (value.ActionCount > 0 && value.Actions == IntPtr.Zero)
                    throw new InvalidDataException("The existing Cortex service recovery action array is invalid.");
                var actions = new List<CortexServiceRecoveryAction>(checked((int)value.ActionCount));
                var actionSize = Marshal.SizeOf<ServiceActionValue>();
                for (var index = 0; index < value.ActionCount; index++)
                {
                    var action = Marshal.PtrToStructure<ServiceActionValue>(
                        IntPtr.Add(value.Actions,checked(index*actionSize)));
                    var preserved = new CortexServiceRecoveryAction(action.Type,action.DelayMilliseconds);
                    ValidateRecoveryAction(preserved);
                    actions.Add(preserved);
                }
                return new(value.ResetPeriodSeconds,QueryFailureActionsFlag(service),actions);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static bool QueryFailureActionsFlag(IntPtr service)
        {
            var size = (uint)Marshal.SizeOf<ServiceFailureActionsFlagValue>();
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!QueryServiceConfig2(service, ServiceConfigFailureActionsFlag, buffer, size, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return Marshal.PtrToStructure<ServiceFailureActionsFlagValue>(buffer).Enabled != 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static void SetRecoveryPolicy(IntPtr service,CortexServiceRecoveryPolicy policy)
        {
            // An existing product-owned service can legitimately have no recovery
            // actions. Keep that state representable so an interrupted update can
            // restore it exactly; the desired Cortex policy itself is tested nonempty.
            if (policy.Actions.Count > 8)
                throw new InvalidDataException("The Cortex service recovery action count is invalid.");
            var actionSize = Marshal.SizeOf<ServiceActionValue>();
            var actions = Marshal.AllocHGlobal(checked(actionSize*policy.Actions.Count));
            try
            {
                for (var index = 0; index < policy.Actions.Count; index++)
                {
                    var action = policy.Actions[index];
                    ValidateRecoveryAction(action);
                    Marshal.StructureToPtr(
                        new ServiceActionValue{Type=action.Type,DelayMilliseconds=action.DelayMilliseconds},
                        IntPtr.Add(actions,index*actionSize),
                        false);
                }
                var native = new ServiceFailureActionsValue
                {
                    ResetPeriodSeconds=policy.ResetPeriodSeconds,
                    ActionCount=(uint)policy.Actions.Count,
                    Actions=actions
                };
                if (!ChangeServiceConfig2FailureActions(service,ServiceConfigFailureActions,ref native))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var flag = new ServiceFailureActionsFlagValue{Enabled=policy.ApplyToNonCrashFailures?1:0};
                if (!ChangeServiceConfig2FailureActionsFlag(service,ServiceConfigFailureActionsFlag,ref flag))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(actions); }
        }

        private static void ValidateRecoveryAction(CortexServiceRecoveryAction action)
        {
            if (action.Type is not (ScActionNone or ScActionRestart) || action.DelayMilliseconds > 5*60*1000)
                throw new InvalidDataException("The Cortex service recovery action is invalid.");
        }

        private static string UnquoteExecutable(string command)
        {
            var trimmed = command.Trim();
            if (trimmed.StartsWith('"'))
            {
                var end = trimmed.IndexOf('"', 1);
                if (end <= 1) throw new InvalidDataException("The service image path is malformed.");
                return Path.GetFullPath(trimmed[1..end]);
            }
            var space = trimmed.IndexOf(' ');
            return Path.GetFullPath(space < 0 ? trimmed : trimmed[..space]);
        }

        private sealed record ServiceConfiguration(string BinaryPath, uint StartType, string Account);

        [StructLayout(LayoutKind.Sequential)]
        private struct QueryServiceConfigValue
        {
            public uint ServiceType;
            public uint StartType;
            public uint ErrorControl;
            public IntPtr BinaryPathName;
            public IntPtr LoadOrderGroup;
            public uint TagId;
            public IntPtr Dependencies;
            public IntPtr ServiceStartName;
            public IntPtr DisplayName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceFailureActionsValue
        {
            public uint ResetPeriodSeconds;
            public IntPtr RebootMessage;
            public IntPtr Command;
            public uint ActionCount;
            public IntPtr Actions;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceActionValue
        {
            public uint Type;
            public uint DelayMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceFailureActionsFlagValue
        {
            public int Enabled;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateService(
            IntPtr manager, string serviceName, string displayName, uint desiredAccess,
            uint serviceType, uint startType, uint errorControl, string binaryPath,
            string? loadOrderGroup, IntPtr tagId, string? dependencies,
            string? serviceStartName, string? password);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeServiceConfig(
            IntPtr service, uint serviceType, uint startType, uint errorControl,
            string? binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies,
            string? serviceStartName, string? password, string? displayName);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceConfig(IntPtr service, IntPtr config, uint bufferSize, out uint bytesNeeded);
        [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceConfig2(IntPtr service, uint infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);
        [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeServiceConfig2FailureActions(IntPtr service, uint infoLevel, ref ServiceFailureActionsValue value);
        [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeServiceConfig2FailureActionsFlag(IntPtr service, uint infoLevel, ref ServiceFailureActionsFlagValue value);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool StartService(IntPtr service, uint argumentCount, IntPtr arguments);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus serviceStatus);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus serviceStatus);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteService(IntPtr service);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
