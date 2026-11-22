using System.Security.Principal;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

internal sealed record CortexCodexFeatureReceiptV1(
    string Contract,
    int Version,
    string Feature,
    string Action,
    string Disposition,
    bool Installed);

internal static class CortexCodexFeatureOperation
{
    internal static CortexCodexFeatureReceiptV1 Execute(CortexCodexFeatureOptions request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var package = CortexSignedProductPackageVerifier.Verify(Path.GetFullPath(request.PackageRoot));
        var ownerSid = WindowsIdentity.GetCurrent().User?.Value ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var layout = CortexProductLayout.ForInstallRoot(Path.GetFullPath(request.InstallRoot), ownerSid);
        var registered = CortexProductLayout.ResolveCurrent();
        if (!string.Equals(layout.InstallRoot, registered.InstallRoot, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(layout.ActiveCatalogPath))
            throw new InvalidDataException("The selected Codex feature requires the registered Cortex installation.");
        var catalog = CortexProductCatalogV1.ReadExact(layout.ActiveCatalogPath);
        if (!string.Equals(catalog.ReleaseVersion, package.ReleaseVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The selected Codex feature package does not match the installed Cortex release.");

        var options = new CortexInstallPackageOptions(
            package.Root,
            layout.InstallRoot,
            request.CodexAgentsPath,
            CodexHome: request.CodexHome,
            CodexRegisterAction: request.Feature == CortexCodexFeature.Registration ? request.Action : CortexCodexFeatureAction.Keep,
            CodexGuidanceAction: request.Feature == CortexCodexFeature.Guidance ? request.Action : CortexCodexFeatureAction.Keep);
        var plan = CortexCodexIntegration.Prepare(options, layout, target =>
            CortexCodexGuidanceInstaller.PrepareFromVerifiedPackage(
                package, CortexProductCatalogV1.ReadExact(package.CatalogPath), target));

        // Product installation is independent of Codex, but a selected Codex
        // feature changes Codex-owned files. Enforce the close requirement at
        // that exact mutation boundary, after every read-only admission check.
        CortexProductInstallLifecycle.PrepareCodexFeatureChange();

        string disposition;
        if (request.Feature == CortexCodexFeature.Registration)
        {
            var receipt = request.Action == CortexCodexFeatureAction.Install
                ? plan.Registration?.Apply()
                : plan.RegistrationRemoval?.Apply();
            if (receipt is null)
                throw new InvalidOperationException("The registration feature did not produce its selected operation.");
            disposition = receipt.Disposition;
            plan = plan with
            {
                Selection = plan.Selection with
                {
                    LegacyPluginDisabled = request.Action == CortexCodexFeatureAction.Install
                        ? receipt.LegacyPluginDisabled
                        : plan.Selection.LegacyPluginDisabled
                }
            };
        }
        else
        {
            var receipt = request.Action == CortexCodexFeatureAction.Install
                ? plan.Guidance?.Apply()
                : plan.GuidanceRemoval?.Apply();
            if (receipt is null)
                throw new InvalidOperationException("The guidance feature did not produce its selected operation.");
            disposition = receipt.Disposition;
        }

        CortexCodexIntegration.WriteSelection(layout, plan.Selection);
        return new(
            "pegasus-cortex-codex-feature",
            1,
            request.Feature.ToString().ToLowerInvariant(),
            request.Action.ToString().ToLowerInvariant(),
            disposition,
            request.Feature == CortexCodexFeature.Registration
                ? plan.Selection.Register
                : plan.Selection.Guidance);
    }
}
