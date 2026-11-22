using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using Pegasus.AgentOps.Certificates;
using Pegasus.Cortex.Mcp.Hosting;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

internal sealed record CortexMcpSettingsSaveResult(bool Ok, string? Error = null);

internal interface ICortexMcpSettingsController
{
    Task<CortexMcpSettingsSaveResult> SaveAsync(CortexMcpSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// Saves only enablement and the next owner through one fixed installed-image UAC helper.
/// The helper reads the current protected references and network policy; no listener,
/// service, certificate, private key or startup entry is changed by this control.
/// </summary>
internal sealed class CortexMcpSettingsControl : ICortexMcpSettingsController
{
    internal static CortexMcpSettings ReadCurrent() =>
        CortexMcpSettingsStore.ReadForInstallRoot(ResolveInstalledHost().Root);

    public async Task<CortexMcpSettingsSaveResult> SaveAsync(CortexMcpSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            settings = CortexMcpSettingsStore.Validate(settings);
            var installed = ResolveInstalledHost();
            var start = new ProcessStartInfo(installed.Host)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(installed.Host)!
            };
            foreach (var argument in new[] { "--shared-mcp-settings", settings.Enabled ? "enabled" : "disabled", settings.Owner.ToString() })
                start.ArgumentList.Add(argument);
            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(start);
            if (process is null) return new(false, "The elevated MCP settings helper could not be started.");
            // Once UAC has started the original process, observe it naturally. A page
            // closing cannot retract a delivered save or authorize another attempt.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (process.ExitCode != 0)
                return new(false, $"MCP settings were not confirmed (helper exit {process.ExitCode}). Refresh before trying again.");
            var saved = CortexMcpSettingsStore.ReadForInstallRoot(installed.Root);
            return saved.Enabled == settings.Enabled && saved.Owner == settings.Owner
                ? new(true)
                : new(false, "The saved MCP selection could not be confirmed. Refresh before making another change.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { return new(false, "Windows elevation was cancelled; MCP settings were not saved."); }
        catch (OperationCanceledException)
        { return new(false, "The save was cancelled before the elevated helper started."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException or
            ArgumentException or System.Text.Json.JsonException)
        { return new(false, "MCP settings could not be confirmed: " + error.GetType().Name + ". Refresh before trying again."); }
    }

    internal static int RunElevated(string enabled, string owner)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 4;
            var selection = ParseSelection(enabled, owner);
            var installed = ResolveInstalledHost();
            _ = CortexProcessorPolicy.ApplyForInstallRoot(installed.Root);
            // Read inside the elevated action, preserving current certificate and
            // network fields rather than replacing them with an old page snapshot.
            CortexMcpSettingsStore.UpdateForInstallRoot(installed.Root,
                current => current with { Enabled = selection.Enabled, Owner = selection.Owner });
            var saved = CortexMcpSettingsStore.ReadForInstallRoot(installed.Root);
            return saved.Enabled == selection.Enabled && saved.Owner == selection.Owner ? 0 : 8;
        }
        catch (InvalidDataException) { return 2; }
        catch (UnauthorizedAccessException) { return 12; }
        catch (Exception error) when (error is IOException or Win32Exception or ArgumentException or NotSupportedException or
            System.Text.Json.JsonException)
        { return 8; }
    }

    internal static (bool Enabled, CortexMcpOwner Owner) ParseSelection(string enabled, string owner) =>
        (enabled switch { "enabled" => true, "disabled" => false, _ => throw new InvalidDataException("Invalid MCP enablement.") },
         owner switch { "Application" => CortexMcpOwner.Application, "Service" => CortexMcpOwner.Service,
             _ => throw new InvalidDataException("Invalid MCP owner.") });

    // A separate explicit setup action selects existing certificates. It neither
    // issues keys nor installs trust and never changes enabled/owner/network policy.
    internal static int RunCertificateBindingElevated(string owner, string serverCertificateId, string authorityCertificateId,
        string expectedOwnerSid)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 4;
            if (identity.User?.Value != expectedOwnerSid || expectedOwnerSid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20")
                return 12;
            var role = ParseSelection("disabled", owner).Owner;
            var installed = ResolveInstalledHost();
            _ = CortexProcessorPolicy.ApplyForInstallRoot(installed.Root);
            CortexMcpSettings? selected = null;
            CortexMcpSettingsStore.UpdateForInstallRoot(installed.Root,current =>
            {
                var candidate = SelectCertificates(current,role,serverCertificateId,authorityCertificateId);
                using var certificates = ValidateCertificates(candidate,role,serverCertificateId);
                return selected = candidate;
            });
            var confirmed = selected ?? throw new InvalidDataException("Certificate selection was not confirmed.");
            var saved = CortexMcpSettingsStore.ReadForInstallRoot(installed.Root);
            var savedLeaf = role == CortexMcpOwner.Application ? saved.ApplicationCertificate : saved.ServiceCertificate;
            var selectedLeaf = role == CortexMcpOwner.Application ? confirmed.ApplicationCertificate : confirmed.ServiceCertificate;
            return savedLeaf == selectedLeaf && saved.CertificateAuthority == confirmed.CertificateAuthority ? 0 : 8;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        { return 2; }
        catch (Exception error) when (error is UnauthorizedAccessException or CortexMcpHostingException or CertificateOperationException)
        { return 12; }
        catch (Exception error) when (error is IOException or Win32Exception or NotSupportedException or System.Security.Cryptography.CryptographicException)
        { return 8; }
    }

    internal static CortexMcpSettings SelectCertificates(CortexMcpSettings current,CortexMcpOwner role,
        string serverCertificateId,string authorityCertificateId)
    {
        if(!Enum.IsDefined(role))throw new InvalidDataException("Invalid MCP owner.");
        var leaf = new CortexMcpCertificateReference(role == CortexMcpOwner.Application ? "CurrentUser" : "LocalMachine",
            "My", serverCertificateId);
        return CortexMcpSettingsStore.Validate(current with
        {
            CertificateAuthority = new("LocalMachine", "Root", authorityCertificateId),
            ApplicationCertificate = role == CortexMcpOwner.Application ? leaf : current.ApplicationCertificate,
            ServiceCertificate = role == CortexMcpOwner.Service ? leaf : current.ServiceCertificate
        });
    }

    internal static CortexMcpCertificates ValidateCertificates(CortexMcpSettings selected,CortexMcpOwner role,
        string serverCertificateId,ICortexMcpCertificateResolver? resolver=null)
    {
        // Resolve the chosen role for validation without changing saved owner policy.
        var certificates = (resolver ?? new CortexMcpCertificateResolver()).Resolve(selected with { Owner = role },role);
        try
        {
            if(selected.CertificateAuthority is not { } ca ||
                !certificates.Authority.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)
                    .Equals(ca.CertificateId,StringComparison.OrdinalIgnoreCase))
                throw new CortexMcpHostingException("certificate_authority_identity_mismatch");
            var now = DateTimeOffset.UtcNow;
            var names = ServerCertificateMaterial.Require(certificates.Server,serverCertificateId,now,privateKey:true);
            var matches = IPAddress.TryParse(selected.ServerName,out var address)
                ? names.IpAddresses.Contains(address.ToString(),StringComparer.Ordinal)
                : names.DnsNames.Contains(selected.ServerName,StringComparer.OrdinalIgnoreCase);
            if(!matches || !CertificateChainOperations.VerifyServerAuthentication(certificates.Server,serverCertificateId,
                new(CertificateTrustSelection.CustomRoots,CertificateRevocationSelection.NotChecked,now,
                    [certificates.Authority.RawData])).Trusted)
                throw new CortexMcpHostingException("certificate_name_or_chain_invalid");
            ServerCertificateMaterial.ProvePrivateKey(certificates.Server);
            return certificates;
        }
        catch { certificates.Dispose();throw; }
    }

    internal static (string Root, string Host) ResolveInstalledHost()
    {
        var host = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidDataException("The installed Host path is unavailable."));
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(host)!, "..", ".."));
        var expected = Path.Combine(root, CortexProductCatalogV1.StableRolePaths["host"].Replace('/', Path.DirectorySeparatorChar));
        if (!StringComparer.OrdinalIgnoreCase.Equals(host, expected))
            throw new InvalidDataException("MCP settings must be saved by the installed Cortex Host.");
        CortexPrivilegedCodePath.RequireProtectedImage(root, host);
        _ = CortexDataLocation.ResolveMachineDataRoot(root);
        return (root, host);
    }
}
