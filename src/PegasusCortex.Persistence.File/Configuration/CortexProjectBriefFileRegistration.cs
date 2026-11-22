using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Persistence.File.Storage;

namespace Pegasus.Cortex.Persistence.File.Configuration;

/// <summary>
/// Explicit local root and read authority for the single file-backed brief.
/// The registration is captured once; no environment or project discovery is used.
/// </summary>
public sealed class CortexProjectBriefFileRegistration
{
    public const string SnapshotRelativePath =
        ".cortex/generated/project-brief.json";

    public CortexProjectBriefFileRegistration(
        string rootPath,
        CortexProjectBriefReadScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RootPath = CortexProjectBriefFilePathPolicy.CaptureRegisteredRoot(rootPath);
        SnapshotPath = CortexProjectBriefFilePathPolicy.ResolveSnapshotPath(RootPath);
        Scope = scope with { };
    }

    public string RootPath { get; }
    public string SnapshotPath { get; }
    public CortexProjectBriefReadScope Scope { get; }
}
