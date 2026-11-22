using Microsoft.Win32;
using Pegasus.Cortex.Product;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Setup;

internal sealed record CortexLegacyArpIdentity(
    string InstallRoot, string ReleaseVersion, string DisplayName, string Publisher, string UninstallString);

// This compatibility admission reads only product-owned metadata. It never
// infers ownership from a copied catalog or opens, seeds, or relocates databases.
internal static class CortexLegacyInstallIdentity
{
    private const string ArpKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Pegasus Cortex";

    internal static bool IsEstablished(CortexProductLayout layout, CortexProductCatalogV1 catalog,
        CortexLegacyArpIdentity? identityForTest)
    {
        var arp = identityForTest ?? (layout.IsIsolatedFixture ? null : ReadArp());
        if (arp is null) return false;
        var receiptPath = Path.Combine(layout.InstallStateDirectory, "registration.v1.json");
        if (!File.Exists(receiptPath)) return false;
        if (!layout.IsIsolatedFixture) CortexProductStateProvisioner.RequireInstallReceiptBoundary(layout);
        RequireOrdinaryFile(layout, receiptPath, metadata: true);
        RequireOrdinaryFile(layout, layout.ActiveCatalogPath, metadata: true);
        using var input = new FileStream(receiptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 4096) throw InvalidIdentity();
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() !=
            document.RootElement.EnumerateObject().Count()) throw InvalidIdentity();
        var receipt = JsonSerializer.Deserialize<CortexWindowsRegistrationReceiptV1>(bytes, new JsonSerializerOptions
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw InvalidIdentity();
        var legacyDataRoot = Path.Combine(layout.InstallRoot, "state");
        if (!Equal(arp.InstallRoot, layout.InstallRoot) || arp.DisplayName != "Pegasus Cortex Prod Alpha" ||
            arp.Publisher != "Pegasus" || !Equal(arp.UninstallString, '"' + Path.Combine(layout.InstallRoot, "Uninstall.exe") + '"') ||
            receipt.Contract != CortexWindowsProductRegistration.ReceiptContract || receipt.Version != 1 ||
            !CortexProductIdentity.IsReleaseVersion(receipt.ReleaseVersion) || arp.ReleaseVersion != receipt.ReleaseVersion ||
            receipt.ServiceName != CortexProductIdentity.ServiceName || receipt.RunValueName != "Pegasus Cortex" ||
            !Equal(receipt.PathEntry, layout.BinDirectory) || !Equal(layout.DataRoot, legacyDataRoot) ||
            receipt.DataRoot is not null && !Equal(receipt.DataRoot, legacyDataRoot) ||
            CortexProductInstallPlanner.CompareReleaseVersions(catalog.ReleaseVersion, receipt.ReleaseVersion) < 0)
            throw InvalidIdentity();
        // A later active catalog can be evidence of a partially completed forward
        // install. ARP and the previous completed registration must agree; each
        // catalog/receipt role remains in its own exact version slot under root.
        RequireRegisteredRole(layout, receipt.ServiceExecutable, receipt.ReleaseVersion, "service", "PegasusCortex.ExecutionService.exe");
        RequireRegisteredRole(layout, receipt.RunExecutable, receipt.ReleaseVersion, "host", "PegasusCortex.Host.exe");
        foreach (var (roleName, leaf) in new[] { ("service", "PegasusCortex.ExecutionService.exe"), ("host", "PegasusCortex.Host.exe") })
        {
            var role = catalog.Roles.Single(item => item.Role == roleName);
            var expected = $"versions/execution/{role.ComponentVersion}/{roleName}/{leaf}";
            if (role.RelativePath != expected) throw InvalidIdentity();
        }
        return true;
    }

    private static void RequireRegisteredRole(CortexProductLayout layout, string actual, string version, string role, string leaf)
    {
        var expected = Path.Combine(layout.InstallRoot, "versions", "execution", version, role, leaf);
        if (!Equal(actual, expected)) throw InvalidIdentity();
        RequireOrdinaryFile(layout, actual, metadata: false);
    }

    private static void RequireOrdinaryFile(CortexProductLayout layout, string path, bool metadata)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw InvalidIdentity();
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists || directory.LinkTarget is not null || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw InvalidIdentity();
            if (Equal(directory.FullName, layout.InstallRoot)) break;
        }
        if (!metadata || layout.IsIsolatedFixture) return;
        var security = file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (security.GetOwner(typeof(SecurityIdentifier))?.Value is not ("S-1-5-18" or "S-1-5-32-544"))
            throw InvalidIdentity();
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>())
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0 &&
                rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544")) throw InvalidIdentity();
    }

    private static CortexLegacyArpIdentity? ReadArp()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(ArpKey, writable: false);
        if (key is null) return null;
        string Read(string name)
        {
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value ||
                key.GetValueKind(name) != RegistryValueKind.String || string.IsNullOrWhiteSpace(value) ||
                value.Length > 512 || value.Any(char.IsControl)) throw InvalidIdentity();
            return value;
        }
        return new(Read("InstallLocation"), Read("DisplayVersion"), Read("DisplayName"), Read("Publisher"), Read("UninstallString"));
    }

    private static bool Equal(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static InvalidDataException InvalidIdentity() => new(
        "The legacy Cortex installation requires matching protected product registration, fixed machine ARP identity, canonical version slots, and the unchanged install-local state root.");
}
