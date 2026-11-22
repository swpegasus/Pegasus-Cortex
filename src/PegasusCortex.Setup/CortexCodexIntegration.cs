using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

public sealed record CortexCodexIntegrationSelectionV1(
    string Contract, int Version, string InstallRoot, string OwnerSid,
    bool Register, bool Guidance, string? Home, string? AgentsPath, bool LegacyPluginDisabled = false);

internal sealed record CortexCodexIntegrationPlan(
    CortexCodexIntegrationSelectionV1 Selection,
    CortexCodexMcpRegistrationPlan? Registration,
    CortexCodexGuidancePlan? Guidance,
    CortexCodexMcpRegistrationPlan? RegistrationRemoval,
    CortexCodexGuidancePlan? GuidanceRemoval,
    bool RecordSelection);

internal static class CortexCodexIntegration
{
    private const string Contract = "pegasus-cortex-codex-integration";
    private static readonly JsonSerializerOptions JsonOptions = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static CortexCodexIntegrationPlan Prepare(
        CortexInstallPackageOptions options, CortexProductLayout layout,
        Func<string, CortexCodexGuidancePlan> prepareGuidance,
        Func<string>? resolveDefaultHome = null,
        string sharedEndpoint = CortexCodexMcpRegistration.DefaultSharedEndpoint)
    {
        var registerAction = EffectiveAction(options.CodexRegisterAction, options.CodexRegister);
        var guidanceAction = EffectiveAction(options.CodexGuidanceAction, options.CodexGuidance);
        // Ordinary install/upgrade/repair deliberately leaves optional Codex
        // integrations alone. In particular, it does not read their receipt,
        // resolve a user home, or inspect either external target.
        if (registerAction == CortexCodexFeatureAction.Keep && guidanceAction == CortexCodexFeatureAction.Keep)
            return new(new(Contract, 1, layout.InstallRoot, layout.OwnerSid, false, false, null, null),
                null, null, null, null, false);

        var previous = ReadPrevious(layout);
        var selection = ResolveSelection(options, layout, previous, resolveDefaultHome ?? ResolveDefaultHome);
        var accessSelection = selection with
        {
            Register = registerAction == CortexCodexFeatureAction.Install ||
                registerAction == CortexCodexFeatureAction.Remove && previous?.Register == true,
            Guidance = guidanceAction == CortexCodexFeatureAction.Install ||
                guidanceAction == CortexCodexFeatureAction.Remove && previous?.Guidance == true
        };
        RequireInstallingOwner(layout, accessSelection);
        var registration = registerAction == CortexCodexFeatureAction.Install
            ? CortexCodexMcpRegistration.Prepare(Path.Combine(selection.Home!, "config.toml"), layout.InstallRoot, sharedEndpoint)
            : null;
        var guidance = guidanceAction == CortexCodexFeatureAction.Install ? prepareGuidance(selection.AgentsPath!) : null;
        var registrationRemoval = registerAction == CortexCodexFeatureAction.Remove && previous?.Register == true
            ? CortexCodexMcpRegistration.PrepareRemove(Path.Combine(previous.Home!, "config.toml"), layout.InstallRoot, sharedEndpoint)
            : null;
        var guidanceRemoval = guidanceAction == CortexCodexFeatureAction.Remove && previous?.Guidance == true
            ? CortexCodexGuidanceInstaller.PrepareRemove(previous.AgentsPath!)
            : null;
        if (!layout.IsIsolatedFixture)
        {
            if (registration is not null) registration = registration with
                { Update = registration.Update with { CreationOwnerSid = layout.OwnerSid } };
            if (guidance is not null) guidance = guidance with
                { Update = guidance.Update with { CreationOwnerSid = layout.OwnerSid } };
            if (registrationRemoval is not null) registrationRemoval = registrationRemoval with
                { Update = registrationRemoval.Update with { CreationOwnerSid = layout.OwnerSid } };
            if (guidanceRemoval is not null) guidanceRemoval = guidanceRemoval with
                { Update = guidanceRemoval.Update with { CreationOwnerSid = layout.OwnerSid } };
        }
        return new(selection, registration, guidance, registrationRemoval, guidanceRemoval, true);
    }

    internal static CortexCodexIntegrationSelectionV1 ResolveSelection(
        CortexInstallPackageOptions options, CortexProductLayout layout,
        CortexCodexIntegrationSelectionV1? previous, Func<string> resolveDefaultHome)
    {
        if (previous is not null) Validate(previous, layout);
        var registerAction = EffectiveAction(options.CodexRegisterAction, options.CodexRegister);
        var guidanceAction = EffectiveAction(options.CodexGuidanceAction, options.CodexGuidance);
        var register = Apply(registerAction, previous?.Register ?? false);
        var guidance = Apply(guidanceAction, previous?.Guidance ?? false);
        var registerTarget = registerAction == CortexCodexFeatureAction.Install ||
            registerAction == CortexCodexFeatureAction.Remove && previous?.Register == true;
        var guidanceTarget = guidanceAction == CortexCodexFeatureAction.Install ||
            guidanceAction == CortexCodexFeatureAction.Remove && previous?.Guidance == true;
        if (!registerTarget && !guidanceTarget)
            return new(Contract, 1, layout.InstallRoot, layout.OwnerSid, register, guidance,
                previous?.Home, previous?.AgentsPath, previous?.LegacyPluginDisabled ?? false);
        string? home = null;
        var previousAgents = options.CodexHome is null ? previous?.AgentsPath : null;
        if (registerTarget || guidanceTarget && options.CodexAgentsPath is null && previousAgents is null)
        {
            home = CortexCodexFileUpdate.RequireCanonical(options.CodexHome ?? previous?.Home ?? resolveDefaultHome());
            CortexCodexFileUpdate.RequireDirectory(home, allowMissing: true);
        }
        var agents = guidanceTarget ? CortexCodexFileUpdate.RequireTarget(options.CodexAgentsPath ??
            previousAgents ?? Path.Combine(home!, "AGENTS.md"), "AGENTS.md") : null;
        // A selected action must retain the other feature's recorded target.
        // Home belongs to registration when that feature remains installed.
        if (registerAction == CortexCodexFeatureAction.Keep && register) home = previous?.Home;
        if (guidanceAction == CortexCodexFeatureAction.Keep && guidance) agents = previous?.AgentsPath;
        return new(Contract, 1, layout.InstallRoot, layout.OwnerSid, register, guidance, home, agents,
            previous?.LegacyPluginDisabled ?? false);
    }

    private static CortexCodexFeatureAction EffectiveAction(CortexCodexFeatureAction? action, bool? legacy) =>
        action ?? (legacy == true ? CortexCodexFeatureAction.Install : CortexCodexFeatureAction.Keep);

    private static bool Apply(CortexCodexFeatureAction action, bool current) => action switch
    {
        CortexCodexFeatureAction.Keep => current,
        CortexCodexFeatureAction.Install => true,
        CortexCodexFeatureAction.Remove => false,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static string ResolveDefaultHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.GetDirectoryName(CortexCodexGuidanceInstaller.ResolveDefaultTarget(profile))!;
    }

    internal static void RequireInstallingOwner(CortexProductLayout layout, CortexCodexIntegrationSelectionV1 selection)
    {
        if ((!selection.Register && !selection.Guidance) || layout.IsIsolatedFixture) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Codex installation requires the installing Windows user identity.");
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != layout.OwnerSid)
            throw new UnauthorizedAccessException("The selected Codex integration does not belong to the current installing user SID.");
        var targets = new List<string>();
        if (selection.Register) targets.Add(Path.Combine(selection.Home!, "config.toml"));
        if (selection.Guidance) targets.Add(selection.AgentsPath!);
        foreach (var target in targets)
        {
            _ = CortexCodexFileUpdate.RequireTarget(target, Path.GetFileName(target));
            var directory = new DirectoryInfo(Path.GetDirectoryName(target)!);
            while (!directory.Exists)
                directory = directory.Parent ?? throw new UnauthorizedAccessException("The selected Codex target has no installing-user directory boundary.");
            var directorySecurity = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            RequireOwnerSid(Owner(directorySecurity), layout.OwnerSid);
            RequireSafeWriteAccess(directorySecurity, layout.OwnerSid);
            if (File.Exists(target))
            {
                var fileSecurity = new FileInfo(target).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
                // A previous elevated installer legitimately created managed
                // files owned by Administrators/SYSTEM inside this user's home.
                RequireOwnerSid(Owner(fileSecurity), layout.OwnerSid, allowSystemFileOwner: true);
                RequireSafeWriteAccess(fileSecurity, layout.OwnerSid);
            }
        }
    }

    private static string Owner(FileSystemSecurity security) =>
        security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier sid ? sid.Value :
            throw new UnauthorizedAccessException("The selected Codex owner SID is unavailable.");

    private static void RequireSafeWriteAccess(FileSystemSecurity security, string expected)
    {
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>())
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & writes) != 0 && rule.IdentityReference.Value != expected &&
                rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544"))
                throw new UnauthorizedAccessException("The selected Codex integration grants write access to a foreign principal. Setup will not alter its ACL.");
    }

    internal static void RequireOwnerSid(string actual, string expected, bool allowSystemFileOwner = false)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal) &&
            !(allowSystemFileOwner && actual is "S-1-5-18" or "S-1-5-32-544"))
            throw new UnauthorizedAccessException("The selected Codex target is owned by a different security principal. Use the intended installing user's exact home; setup will not adopt or change its ownership.");
    }

    internal static string ReceiptPath(CortexProductLayout layout) => Path.Combine(
        layout.InstallStateDirectory, "codex-integration." + layout.OwnerSid + ".v1.json");

    internal static CortexCodexIntegrationSelectionV1? ReadPrevious(CortexProductLayout layout)
    {
        var path = ReceiptPath(layout);
        if (!File.Exists(path)) return null;
        if (!layout.IsIsolatedFixture) CortexProductStateProvisioner.RequireInstallReceiptBoundary(layout);
        _ = CortexCodexFileUpdate.RequireTarget(path, Path.GetFileName(path));
        var bytes = CortexCodexFileUpdate.ReadBounded(path, 4096);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() !=
            document.RootElement.EnumerateObject().Count())
            throw new InvalidDataException("The Codex integration receipt contains duplicate or invalid fields.");
        var previous = JsonSerializer.Deserialize<CortexCodexIntegrationSelectionV1>(bytes, JsonOptions) ??
            throw new InvalidDataException("The Codex integration receipt is empty.");
        Validate(previous, layout);
        return previous;
    }

    internal static void WriteSelection(CortexProductLayout layout, CortexCodexIntegrationSelectionV1 selection)
    {
        Validate(selection, layout);
        if (!layout.IsIsolatedFixture) CortexProductStateProvisioner.RequireInstallReceiptBoundary(layout);
        var path = CortexCodexFileUpdate.RequireTarget(ReceiptPath(layout), Path.GetFileName(ReceiptPath(layout)));
        var existed = File.Exists(path);
        var original = CortexCodexFileUpdate.ReadBounded(path, 4096);
        var result = JsonSerializer.SerializeToUtf8Bytes(selection, JsonOptions);
        new CortexCodexFileUpdate(path, existed, original, result).Apply();
    }

    private static void Validate(CortexCodexIntegrationSelectionV1 value, CortexProductLayout layout)
    {
        if (value.Contract != Contract || value.Version != 1 || value.OwnerSid != layout.OwnerSid ||
            !string.Equals(value.InstallRoot, layout.InstallRoot, StringComparison.OrdinalIgnoreCase) ||
            value.Register && value.Home is null || value.Guidance && value.AgentsPath is null)
            throw new InvalidDataException("The Codex integration receipt does not bind this installing user and product.");
        if (value.Home is not null) _ = CortexCodexFileUpdate.RequireCanonical(value.Home);
        if (value.AgentsPath is not null)
        {
            _ = CortexCodexFileUpdate.RequireCanonical(value.AgentsPath);
            if (!string.Equals(Path.GetFileName(value.AgentsPath), "AGENTS.md", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Codex guidance receipt target is invalid.");
        }
    }
}
