using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Pegasus.Cortex.Geometry.Shadow;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

/// <summary>Owner-private read-only diagnostics. Never connects to the host, opens a database,
/// starts a collector, creates directories or treats a snapshot as a live heartbeat.</summary>
internal static class CortexShadowResearchReadout
{
    internal static (int Exit, string Json) ReadCurrent() => Read(CortexCurrentUserLayout.Resolve().ConfigDirectory);

    internal static (int Exit, string Json) Read(string configDirectory)
    {
        var selected = CortexShadowConfiguration.ReadOwner(configDirectory);
        if (selected.State != "selected")
            return Result(selected.State, selected.Issue, null);
        try
        {
            var path = Path.Combine(selected.ResearchRoot!, "status.json");
            CortexProductLayout.RejectExistingReparseComponents(path);
            CortexLocalRegistryAclPolicy.RequireProtectedDirectory(selected.ResearchRoot!);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 16384) return Result("snapshot-unavailable", "snapshot-too-large", null);
            RequireOwnerSnapshot(path);
            var snapshot = JsonSerializer.Deserialize<CortexShadowStudySummary>(stream);
            if (snapshot is null || snapshot.Schema != "cortex.shadow.summary/1" ||
                snapshot.Status is null || snapshot.Status.Counters is null ||
                snapshot.Generation != selected.Profile!.Generation ||
                snapshot.Status.Generation != selected.Profile.Generation)
                return Result("snapshot-unavailable", "snapshot-binding-mismatch", null);
            return Result(snapshot.Final ? "stopped-snapshot" : "recorded-snapshot", null, snapshot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                   JsonException or ArgumentException or System.Security.SecurityException)
        { return Result("snapshot-unavailable", "snapshot-" + error.GetType().Name, null); }
    }

    private static void RequireOwnerSnapshot(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new UnauthorizedAccessException("Owner unavailable.");
        var acl = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier sid || !sid.Equals(owner) ||
            acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().Any(rule =>
                rule.AccessControlType != AccessControlType.Allow || !rule.IdentityReference.Equals(owner)))
            throw new UnauthorizedAccessException("Snapshot is not owner-private.");
        // Inherited owner-only file ACEs are valid under the already protected owner-only directory.
    }

    private static (int Exit, string Json) Result(string state, string? issue, CortexShadowStudySummary? snapshot) =>
        (issue is null ? 0 : 2, JsonSerializer.Serialize(new
        {
            schema = "cortex.shadow.host-status/1", state, issue,
            hostLivenessVerified = false, snapshot,
            snapshotAgeSeconds = snapshot is null ? (double?)null :
                (DateTimeOffset.UtcNow - snapshot.SnapshotAtUtc).TotalSeconds,
            meaning = "Timestamped owner-private research snapshot; not host health or accuracy."
        }));
}
