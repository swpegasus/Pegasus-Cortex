using System.Security.Principal;
using System.Text.Json;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

internal sealed record CortexOptionalServiceReceiptV1(
    string Contract, int Version, string ReleaseVersion, string Service,
    bool Created, string Startup, bool StartRequested);

/// <summary>Fixed elevated service installation; ordinary installation is independent.</summary>
internal static class CortexOptionalServiceOperation
{
    internal static CortexOptionalServiceReceiptV1 Install(string installRoot)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Optional Cortex service installation requires Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Installing the optional Cortex service requires Windows elevation.");
        var registered = CortexProductLayout.ResolveCurrent();
        var requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        if (!string.Equals(requested, registered.InstallRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Optional service installation requires the registered Cortex product.");
        var admitted = CortexSignedProductPackageVerifier.VerifyInstalledService(registered);
        var existed = CortexWindowsProductRegistration.HasOwnedOptionalService(registered, admitted.Executable);
        CortexPrivilegedCodePath.RequireProtectedAncestors(Directory.Exists(registered.ServiceStateDirectory)
            ? registered.ServiceStateDirectory : Path.GetDirectoryName(registered.ServiceStateDirectory)!);
        CortexProductStateProvisioner.EnsureServiceConfiguration(registered);
        CortexOptionalMaintenanceDefaults.Ensure(registered, allowFresh: !existed);
        var created = CortexWindowsProductRegistration.InstallOptionalService(registered, admitted.Executable);
        var receipt = new CortexOptionalServiceReceiptV1("pegasus-cortex-optional-service", 1,
            admitted.Catalog.ReleaseVersion, CortexProductIdentity.ServiceName, created,
            created ? "manual" : "preserved", StartRequested: false);
        // A small install receipt is sufficient; no database or runtime startup
        // is involved, and an existing Disabled service is never enabled here.
        var destination = Path.Combine(registered.InstallStateDirectory, "optional-service.v1.json");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, receipt);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return receipt;
    }
}
