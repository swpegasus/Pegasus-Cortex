using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;

namespace Pegasus.Cortex.Mcp.Integrated.Recovery;

/// <summary>
/// One exact verifier for every current-recovery projection path. It compares
/// scope, authority, selection, task, gate, source, and freshness fields without
/// treating agent-reported state as execution authority.
/// </summary>
internal static class CortexIntegratedCurrentRecoveryBriefVerifier
{
    public static async ValueTask<bool> MatchesAsync(
        ICortexProjectBriefReadModel readModel,
        CortexProjectBriefSnapshot brief,
        CortexProjectBriefReadScope scope,
        AgentTaskSnapshotV1 source,
        string authoritySetHash,
        string retainedContentHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        CortexProjectBriefSnapshot? previous = null;
        if (brief.PreviousContextSetId is not null)
        {
            previous = await readModel.FindAsync(
                scope,
                brief.PreviousContextSetId,
                cancellationToken).ConfigureAwait(false);
            if (previous is null)
                return false;
        }

        try
        {
            var checkpoint = CortexProjectBriefReferencePolicy.CreateRetained(
                scope.ProjectLinkId,
                scope.ProjectId,
                scope.ProjectNamespace,
                CortexRetainedDocumentKinds.TaskSnapshot,
                source.TaskSetId,
                source.SnapshotRevision,
                retainedContentHash,
                source.SnapshotHash);
            var expected = CortexProjectBriefProjector.Project(
                source,
                new CortexProjectBriefProjectionInput(
                    scope.ProjectId,
                    scope,
                    [checkpoint],
                    [],
                    SkippedSourceChainProof: null),
                previous);
            return string.Equals(
                       authoritySetHash,
                       expected.AuthoritySetHash,
                       StringComparison.Ordinal) &&
                   CortexCanonicalJson.Serialize(expected).AsSpan().SequenceEqual(
                       CortexCanonicalJson.Serialize(brief));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
