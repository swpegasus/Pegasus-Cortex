using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;

namespace Pegasus.Cortex.Core.Query;

/// <summary>
/// One externally hash-bound evidence reference for an agent-reported evidence
/// identifier. The projector never fabricates a digest for identifier-only data.
/// </summary>
public sealed record CortexProjectBriefProjectionEvidence(
    string EvidenceId,
    CortexProjectBriefReference Reference);

/// <summary>
/// Compact proof for an exact retained source range that could not materialize
/// a brief. The host validates every immutable snapshot while streaming the
/// range; the projector independently binds its bounds and hash endpoints.
/// </summary>
public sealed record CortexProjectBriefSkippedSourceChainProof(
    long FirstRevision,
    long LastRevision,
    int RevisionCount,
    string? StartingPreviousSnapshotHash,
    string EndingSnapshotHash);

/// <summary>
/// Explicit registration and reference inputs which are absent from the task
/// snapshot contract. Supplying them is a trust-boundary decision by the host,
/// not an inference made by Cortex.
/// </summary>
public sealed record CortexProjectBriefProjectionInput(
    string SourceProjectId,
    CortexProjectBriefReadScope RegisteredScope,
    IReadOnlyList<CortexProjectBriefReference> CheckpointReferences,
    IReadOnlyList<CortexProjectBriefProjectionEvidence> EvidenceReferences,
    CortexProjectBriefSkippedSourceChainProof? SkippedSourceChainProof);

/// <summary>
/// Provider-neutral projection from one validated agent-authored task snapshot.
/// Selection considers only reported state and ordinal; it does not infer task
/// ownership, approval, scheduling, priority, or execution authority.
/// </summary>
public static class CortexProjectBriefProjector
{
    public const string SelectionVersion = "agent-ordinal-state-v1";
    public const string MissingNextAction =
        "No next action was reported by the source agent.";

    public static CortexProjectBriefSnapshot Project(
        AgentTaskSnapshotV1 source,
        CortexProjectBriefProjectionInput input,
        CortexProjectBriefSnapshot? previousBrief = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.RegisteredScope);
        ArgumentNullException.ThrowIfNull(input.CheckpointReferences);
        ArgumentNullException.ThrowIfNull(input.EvidenceReferences);
        RequireRegisteredScope(input.RegisteredScope);
        RequireSource(source, input);
        RequirePrevious(previousBrief, source, input);

        var ordered = source.Tasks.OrderBy(task => task.Ordinal).ToArray();
        if (ordered.Length == 0)
            throw new ArgumentException("A project brief requires at least one reported task.", nameof(source));

        // An explicitly active task is current. If none is reported active, the
        // first non-terminal ordinal is current; an all-terminal snapshot keeps
        // its first ordinal visible rather than inventing active work.
        var current = ordered.FirstOrDefault(task =>
                task.ReportedState == AgentTaskStateIds.Active) ??
            ordered.FirstOrDefault(task => !IsTerminal(task.ReportedState)) ??
            ordered[0];
        var hotCandidates = ordered
            .Where(task =>
                !ReferenceEquals(task, current) &&
                !IsTerminal(task.ReportedState))
            .ToArray();
        var freshness = ProjectFreshness(source.Freshness);
        var projectedCurrent = ProjectTask(
            current,
            selectionRank: 0,
            source,
            freshness);
        var hotTasks = hotCandidates
            .Take(CortexProjectBriefContract.MaximumHotTasks)
            .Select((task, index) =>
                ProjectTask(task, index + 1, source, freshness))
            .ToArray();
        var references = CaptureReferences(
            input,
            [current, .. hotCandidates.Take(hotTasks.Length)]);
        var stateRevision = source.SnapshotRevision;
        var authoritySetHash = CortexProjectBriefService.ComputeAuthoritySetHash(
            source.SourceRevisions);

        var template = new CortexProjectBriefSnapshot(
            CortexProjectBriefContract.SchemaVersion,
            CortexProjectBriefContract.ContractId,
            input.RegisteredScope.TenantId,
            input.RegisteredScope.ProjectLinkId,
            input.SourceProjectId,
            input.RegisteredScope.ProjectNamespace,
            $"ctx_{new string('0', 64)}",
            previousBrief?.ContextSetId,
            previousBrief?.BriefRevision + 1 ?? 1,
            stateRevision,
            source.SnapshotHash,
            SelectionVersion,
            projectedCurrent,
            Array.AsReadOnly(hotTasks),
            // Exact-read follow-up remains necessary whenever any source task,
            // including a terminal/history task, is omitted from the bounded
            // current-plus-hot projection.
            ordered.Length > 1 + hotTasks.Length,
            references.Checkpoints,
            references.Evidence,
            true,
            CortexProjectBriefContract.FallbackRecoveryOrder,
            freshness,
            authoritySetHash);
        var snapshot = template with
        {
            ContextSetId = CortexProjectBriefService.ComputeContextSetId(template)
        };
        if (CortexCanonicalJson.Serialize(snapshot).Length >
            CortexProjectBriefContract.MaximumUtf8Bytes)
        {
            throw new ArgumentException(
                "The projected project brief exceeds 8 KiB without safe truncation.",
                nameof(source));
        }
        return snapshot;
    }

    private static CortexProjectBriefTask ProjectTask(
        AgentTaskV1 task,
        int selectionRank,
        AgentTaskSnapshotV1 source,
        CortexProjectBriefFreshness freshness)
    {
        RequireProjectedText(
            task.Objective,
            CortexProjectBriefContract.MaximumTitleCharacters,
            nameof(task.Objective));
        RequireProjectedText(
            task.CurrentState,
            CortexProjectBriefContract.MaximumTextCharacters,
            nameof(task.CurrentState));
        var nextAction = task.NextAction ?? MissingNextAction;
        RequireProjectedText(
            nextAction,
            CortexProjectBriefContract.MaximumTextCharacters,
            nameof(task.NextAction));
        var blocker = task.Blockers.Count == 0
            ? null
            : string.Join('\n', task.Blockers);
        if (blocker is not null)
        {
            RequireProjectedText(
                blocker,
                CortexProjectBriefContract.MaximumTextCharacters,
                nameof(task.Blockers));
        }

        return new CortexProjectBriefTask(
            task.TaskId,
            selectionRank,
            task.Objective,
            task.ReportedState,
            task.CurrentState,
            nextAction,
            new CortexProjectBriefGate(
                task.ApprovalGate.State,
                task.ApprovalGate.AuthorityId,
                task.ApprovalGate.ApprovalReferenceId,
                task.ApprovalGate.Summary,
                GrantsAuthority: false),
            blocker,
            new CortexProjectBriefTaskSource(
                source.ContractId,
                source.TaskSetId,
                source.SnapshotId,
                source.SnapshotHash,
                $"hash://{source.SnapshotHash}"),
            freshness with { });
    }

    private static CortexProjectBriefFreshness ProjectFreshness(
        AgentWorkFreshnessV1 source)
    {
        if (source.State != AgentWorkFreshnessStateIds.Current ||
            source.ValidUntil is null)
        {
            throw new ArgumentException(
                "Only explicitly current, bounded source freshness can produce a hot brief.",
                nameof(source));
        }
        return new CortexProjectBriefFreshness(
            source.State,
            source.ObservedAt,
            source.ValidUntil,
            source.Basis);
    }

    private static void RequireSource(
        AgentTaskSnapshotV1 source,
        CortexProjectBriefProjectionInput input)
    {
        if (!AgentTaskSnapshotPolicy.HasValidHash(source) ||
            source.SchemaVersion != AgentTaskSnapshotContractIds.SchemaVersion ||
            !string.Equals(
                source.ContractId,
                AgentTaskSnapshotContractIds.Snapshot,
                StringComparison.Ordinal) ||
            !string.Equals(
                source.Coverage,
                AgentTaskSnapshotContractIds.Coverage,
                StringComparison.Ordinal) ||
            source.Executable ||
            source.GrantsSchedulingAuthority ||
            source.GrantsTaskClaim ||
            source.GrantsAuthority)
        {
            throw new ArgumentException(
                "The source task snapshot failed its integrity or passive-data boundary.",
                nameof(source));
        }
        if (!ValidIdentifier(input.SourceProjectId) ||
            !string.Equals(
                input.SourceProjectId,
                input.RegisteredScope.ProjectId,
                StringComparison.Ordinal) ||
            !string.Equals(
                source.ProjectId,
                input.SourceProjectId,
                StringComparison.Ordinal) ||
            source.ProjectLinkId is not null &&
            !string.Equals(
                source.ProjectLinkId,
                input.RegisteredScope.ProjectLinkId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The source project identity does not match the explicit registration.",
                nameof(source));
        }
    }

    private static void RequirePrevious(
        CortexProjectBriefSnapshot? previous,
        AgentTaskSnapshotV1 source,
        CortexProjectBriefProjectionInput input)
    {
        var registeredScope = input.RegisteredScope;
        if (previous is not null &&
            (previous.SchemaVersion != CortexProjectBriefContract.SchemaVersion ||
            !string.Equals(
                previous.ContractId,
                CortexProjectBriefContract.ContractId,
                StringComparison.Ordinal) ||
            !string.Equals(previous.TenantId, registeredScope.TenantId, StringComparison.Ordinal) ||
            !string.Equals(
                previous.ProjectLinkId,
                registeredScope.ProjectLinkId,
                StringComparison.Ordinal) ||
            !string.Equals(
                previous.ProjectId,
                source.ProjectId,
                StringComparison.Ordinal) ||
            !string.Equals(
                previous.ProjectNamespace,
                registeredScope.ProjectNamespace,
                StringComparison.Ordinal) ||
            !string.Equals(previous.SelectionVersion, SelectionVersion, StringComparison.Ordinal) ||
            !ValidSha256(previous.AuthoritySetHash) ||
            !string.Equals(
                previous.ContextSetId,
                CortexProjectBriefService.ComputeContextSetId(previous),
                StringComparison.Ordinal) ||
            previous.BriefRevision < 1 ||
            previous.StateRevision < previous.BriefRevision))
        {
            throw new ArgumentException(
                "The previous brief does not bind the exact source predecessor and registration.",
                nameof(previous));
        }

        var expectedRevision = previous?.StateRevision + 1 ?? 1;
        var expectedPreviousHash = previous?.SourceHash;
        var skippedCount = source.SnapshotRevision - expectedRevision;
        if (skippedCount < 0 ||
            skippedCount > CortexProjectBriefContract.MaximumSkippedSourceRevisions)
        {
            throw new ArgumentException(
                "The skipped source chain exceeds the bounded recovery window.",
                nameof(input));
        }

        var proof = input.SkippedSourceChainProof;
        if (skippedCount == 0)
        {
            if (proof is not null)
            {
                throw new ArgumentException(
                    "A skipped source proof was supplied for an adjacent source.",
                    nameof(input));
            }
        }
        else
        {
            if (proof is null ||
                proof.FirstRevision != expectedRevision ||
                proof.LastRevision != source.SnapshotRevision - 1 ||
                proof.RevisionCount != skippedCount ||
                proof.RevisionCount is < 1 or >
                    CortexProjectBriefContract.MaximumSkippedSourceRevisions ||
                !string.Equals(
                    proof.StartingPreviousSnapshotHash,
                    expectedPreviousHash,
                    StringComparison.Ordinal) ||
                !ValidSha256(proof.EndingSnapshotHash) ||
                string.Equals(
                    proof.EndingSnapshotHash,
                    expectedPreviousHash,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The skipped source proof does not bind the exact bounded source range.",
                    nameof(input));
            }
            expectedRevision = source.SnapshotRevision;
            expectedPreviousHash = proof.EndingSnapshotHash;
        }
        if (source.SnapshotRevision != expectedRevision ||
            !string.Equals(
                source.PreviousSnapshotHash,
                expectedPreviousHash,
                StringComparison.Ordinal) ||
            string.Equals(
                source.SnapshotHash,
                expectedPreviousHash,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The source does not extend the exact previous brief and skipped source chain.",
                nameof(source));
        }
    }

    private static bool ValidSha256(string? value) =>
        value is { Length: 64 } &&
        value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private static CapturedReferences CaptureReferences(
        CortexProjectBriefProjectionInput input,
        IReadOnlyList<AgentTaskV1> selectedTasks)
    {
        var checkpoints = input.CheckpointReferences
            .Take(CortexProjectBriefContract.MaximumReferences + 1)
            .ToArray();
        var evidence = input.EvidenceReferences
            .Take(CortexProjectBriefContract.MaximumReferences + 1)
            .ToArray();
        if (checkpoints.Length == 0 ||
            checkpoints.Length > CortexProjectBriefContract.MaximumReferences ||
            evidence.Length > CortexProjectBriefContract.MaximumReferences ||
            !ValidReferences(checkpoints, input) ||
            evidence.Any(item =>
                item is null ||
                !ValidIdentifier(item.EvidenceId) ||
                item.Reference is null) ||
            evidence.Select(item => item.EvidenceId)
                .Distinct(StringComparer.Ordinal).Count() != evidence.Length ||
            !ValidReferences(
                evidence.Select(item => item.Reference).ToArray(),
                input))
        {
            throw new ArgumentException(
                "Projection references are missing, ambiguous, unbounded, or invalid.",
                nameof(input));
        }

        var reportedEvidenceIds = selectedTasks
            .SelectMany(task => task.EvidenceIds)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var suppliedEvidenceIds = evidence
            .Select(item => item.EvidenceId)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (!reportedEvidenceIds.SequenceEqual(
                suppliedEvidenceIds,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Every selected reported evidence identifier requires one explicit hash-bound reference.",
                nameof(input));
        }

        return new CapturedReferences(
            Array.AsReadOnly(checkpoints
                .OrderBy(item => item.Reference, StringComparer.Ordinal)
                .Select(item => item with { })
                .ToArray()),
            Array.AsReadOnly(evidence
                .Select(item => item.Reference)
                .OrderBy(item => item.Reference, StringComparer.Ordinal)
                .Select(item => item with { })
                .ToArray()));
    }

    private static void RequireRegisteredScope(CortexProjectBriefReadScope scope)
    {
        if (!ValidIdentifier(scope.TenantId) ||
            !IsPrefixedHash(scope.ProjectLinkId, "plink_") ||
            !ValidIdentifier(scope.ProjectId) ||
            !ValidIdentifier(scope.ProjectNamespace) ||
            !ValidIdentifier(scope.AuthorityId) ||
            !ValidIdentifier(scope.SubjectId) ||
            !ValidIdentifier(scope.ServiceId) ||
            scope.PolicyRevision < 1)
        {
            throw new ArgumentException(
                "The project-brief registration scope is invalid.",
                nameof(scope));
        }
    }

    private static void RequireProjectedText(
        string value,
        int maximumCharacters,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumCharacters ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(character =>
                char.IsControl(character) &&
                character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException(
                "The reported value cannot be represented safely in a project brief.",
                parameterName);
        }
    }

    private static bool IsTerminal(string state) =>
        state is AgentTaskStateIds.Done or AgentTaskStateIds.Cancelled;

    private static bool ValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexQueryLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or ':' or '-');

    private static bool IsPrefixedHash(string value, string prefix) =>
        value.Length == prefix.Length + 64 &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool ValidReferences(
        IReadOnlyList<CortexProjectBriefReference> values,
        CortexProjectBriefProjectionInput input) =>
        values.All(item =>
            CortexProjectBriefReferencePolicy.IsValidForProject(
                item,
                input.RegisteredScope.ProjectLinkId,
                input.SourceProjectId,
                input.RegisteredScope.ProjectNamespace)) &&
        values.Select(item => item.Reference)
            .Distinct(StringComparer.Ordinal).Count() == values.Count;

    private sealed record CapturedReferences(
        IReadOnlyList<CortexProjectBriefReference> Checkpoints,
        IReadOnlyList<CortexProjectBriefReference> Evidence);
}
