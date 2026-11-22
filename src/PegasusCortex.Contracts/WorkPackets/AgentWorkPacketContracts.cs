using System.Text.Json.Serialization;
using Pegasus.Cortex.Contracts.Serialization;

namespace Pegasus.Cortex.Contracts.WorkPackets;

public sealed record AgentWorkOwnerV1
{
    [JsonConstructor]
    public AgentWorkOwnerV1(string authorityId, string subjectId, string serviceId)
    {
        AgentWorkPacketShapes.RequireIdentifier(authorityId, nameof(authorityId));
        AgentWorkPacketShapes.RequireIdentifier(subjectId, nameof(subjectId));
        AgentWorkPacketShapes.RequireIdentifier(serviceId, nameof(serviceId));
        AuthorityId = authorityId;
        SubjectId = subjectId;
        ServiceId = serviceId;
    }

    public string AuthorityId { get; }
    public string SubjectId { get; }
    public string ServiceId { get; }
}

/// <summary>
/// A reported approval state. Even a satisfied gate is passive evidence that an
/// execution host must revalidate with the named authority.
/// </summary>
public sealed record AgentWorkApprovalGateV1
{
    [JsonConstructor]
    public AgentWorkApprovalGateV1(
        string state,
        string? authorityId,
        string? approvalReferenceId,
        string summary,
        bool grantsAuthority)
    {
        if (state is not (
                AgentWorkApprovalGateStateIds.NotRequired or
                AgentWorkApprovalGateStateIds.Pending or
                AgentWorkApprovalGateStateIds.Satisfied or
                AgentWorkApprovalGateStateIds.Denied) ||
            grantsAuthority)
        {
            throw new ArgumentException("The approval gate state is invalid or attempts to grant authority.");
        }
        if (state == AgentWorkApprovalGateStateIds.NotRequired)
        {
            if (authorityId is not null || approvalReferenceId is not null)
                throw new ArgumentException("A not-required approval gate cannot cite approval authority.");
        }
        else
        {
            AgentWorkPacketShapes.RequireIdentifier(authorityId, nameof(authorityId));
            if (state is AgentWorkApprovalGateStateIds.Satisfied or AgentWorkApprovalGateStateIds.Denied)
                AgentWorkPacketShapes.RequireIdentifier(approvalReferenceId, nameof(approvalReferenceId));
            else if (approvalReferenceId is not null)
                AgentWorkPacketShapes.RequireIdentifier(approvalReferenceId, nameof(approvalReferenceId));
        }
        AgentWorkPacketShapes.RequireText(
            summary,
            AgentWorkPacketContractLimits.MaximumApprovalSummaryCharacters,
            nameof(summary));
        State = state;
        AuthorityId = authorityId;
        ApprovalReferenceId = approvalReferenceId;
        Summary = summary;
        GrantsAuthority = grantsAuthority;
    }

    public string State { get; }
    public string? AuthorityId { get; }
    public string? ApprovalReferenceId { get; }
    public string Summary { get; }
    public bool GrantsAuthority { get; }
}

public sealed record AgentWorkFreshnessV1
{
    [JsonConstructor]
    public AgentWorkFreshnessV1(
        string state,
        DateTimeOffset observedAt,
        DateTimeOffset? validUntil,
        string basis)
    {
        if (state is not (
                AgentWorkFreshnessStateIds.Current or
                AgentWorkFreshnessStateIds.Stale or
                AgentWorkFreshnessStateIds.Blocked or
                AgentWorkFreshnessStateIds.Unverified or
                AgentWorkFreshnessStateIds.Unknown))
        {
            throw new ArgumentException("The freshness state is invalid.", nameof(state));
        }
        AgentWorkPacketShapes.RequireUtc(observedAt, nameof(observedAt));
        if (validUntil is not null)
        {
            AgentWorkPacketShapes.RequireUtc(validUntil.Value, nameof(validUntil));
            if (validUntil <= observedAt ||
                validUntil - observedAt > AgentWorkPacketContractLimits.MaximumPacketLifetime)
            {
                throw new ArgumentException("The freshness validity window is invalid.", nameof(validUntil));
            }
        }
        if (state == AgentWorkFreshnessStateIds.Current && validUntil is null)
            throw new ArgumentException("Current data requires a bounded validity deadline.", nameof(validUntil));
        AgentWorkPacketShapes.RequireText(
            basis,
            AgentWorkPacketContractLimits.MaximumItemCharacters,
            nameof(basis));
        State = state;
        ObservedAt = observedAt;
        ValidUntil = validUntil;
        Basis = basis;
    }

    public string State { get; }
    public DateTimeOffset ObservedAt { get; }
    public DateTimeOffset? ValidUntil { get; }
    public string Basis { get; }
}

public sealed record AgentWorkSourceRevisionV1
{
    [JsonConstructor]
    public AgentWorkSourceRevisionV1(string sourceId, string revisionId, string sha256)
    {
        AgentWorkPacketShapes.RequireIdentifier(sourceId, nameof(sourceId));
        AgentWorkPacketShapes.RequireIdentifier(revisionId, nameof(revisionId));
        AgentWorkPacketShapes.RequireHash(sha256, nameof(sha256));
        SourceId = sourceId;
        RevisionId = revisionId;
        Sha256 = sha256;
    }

    public string SourceId { get; }
    public string RevisionId { get; }
    public string Sha256 { get; }
}

public sealed record AgentWorkVerificationEvidenceV1
{
    [JsonConstructor]
    public AgentWorkVerificationEvidenceV1(
        string verificationId,
        string evidenceId,
        string result,
        string summary)
    {
        AgentWorkPacketShapes.RequireIdentifier(verificationId, nameof(verificationId));
        AgentWorkPacketShapes.RequireIdentifier(evidenceId, nameof(evidenceId));
        if (result is not (
                AgentWorkVerificationResultIds.Passed or
                AgentWorkVerificationResultIds.Failed or
                AgentWorkVerificationResultIds.NotRun or
                AgentWorkVerificationResultIds.Inconclusive))
        {
            throw new ArgumentException("The verification result is invalid.", nameof(result));
        }
        AgentWorkPacketShapes.RequireText(
            summary,
            AgentWorkPacketContractLimits.MaximumItemCharacters,
            nameof(summary));
        VerificationId = verificationId;
        EvidenceId = evidenceId;
        Result = result;
        Summary = summary;
    }

    public string VerificationId { get; }
    public string EvidenceId { get; }
    public string Result { get; }
    public string Summary { get; }
}

public sealed record AgentWorkCloseoutV1
{
    [JsonConstructor]
    public AgentWorkCloseoutV1(
        string disposition,
        string publicSummary,
        IReadOnlyList<string> changes,
        IReadOnlyList<AgentWorkVerificationEvidenceV1> verificationEvidence,
        IReadOnlyList<string> assumptions,
        IReadOnlyList<string> risks,
        string? nextAction,
        AgentWorkOwnerV1? nextAuthority,
        string? candidateReusableLesson)
    {
        if (disposition is not (
                AgentWorkCloseoutDispositionIds.Solved or
                AgentWorkCloseoutDispositionIds.Partial or
                AgentWorkCloseoutDispositionIds.Blocked or
                AgentWorkCloseoutDispositionIds.Failed))
        {
            throw new ArgumentException("The closeout disposition is invalid.", nameof(disposition));
        }
        AgentWorkPacketShapes.RequireText(
            publicSummary,
            AgentWorkPacketContractLimits.MaximumPublicSummaryCharacters,
            nameof(publicSummary));
        var changeSnapshot = AgentWorkPacketShapes.SnapshotTextList(
            changes,
            AgentWorkPacketContractLimits.MaximumChanges,
            AgentWorkPacketContractLimits.MaximumItemCharacters,
            nameof(changes));
        var verificationSnapshot = AgentWorkPacketShapes.SnapshotList(
            verificationEvidence,
            AgentWorkPacketContractLimits.MaximumVerificationEvidence,
            nameof(verificationEvidence),
            requireNonEmpty: true);
        var assumptionSnapshot = AgentWorkPacketShapes.SnapshotTextList(
            assumptions,
            AgentWorkPacketContractLimits.MaximumAssumptions,
            AgentWorkPacketContractLimits.MaximumItemCharacters,
            nameof(assumptions));
        var riskSnapshot = AgentWorkPacketShapes.SnapshotTextList(
            risks,
            AgentWorkPacketContractLimits.MaximumRisks,
            AgentWorkPacketContractLimits.MaximumItemCharacters,
            nameof(risks));

        if (verificationSnapshot.Select(item => item.VerificationId)
                .Distinct(StringComparer.Ordinal).Count() != verificationSnapshot.Count)
        {
            throw new ArgumentException("Verification identifiers must be unique.", nameof(verificationEvidence));
        }
        if (disposition == AgentWorkCloseoutDispositionIds.Solved)
        {
            if (nextAction is not null || nextAuthority is not null ||
                verificationSnapshot.Any(item => item.Result != AgentWorkVerificationResultIds.Passed))
            {
                throw new ArgumentException("A solved closeout must be fully verified and have no delegated next action.");
            }
        }
        else
        {
            AgentWorkPacketShapes.RequireText(
                nextAction,
                AgentWorkPacketContractLimits.MaximumItemCharacters,
                nameof(nextAction));
            ArgumentNullException.ThrowIfNull(nextAuthority);
        }
        if (candidateReusableLesson is not null)
        {
            AgentWorkPacketShapes.RequireText(
                candidateReusableLesson,
                AgentWorkPacketContractLimits.MaximumReusableLessonCharacters,
                nameof(candidateReusableLesson));
        }

        Disposition = disposition;
        PublicSummary = publicSummary;
        Changes = changeSnapshot;
        VerificationEvidence = verificationSnapshot;
        Assumptions = assumptionSnapshot;
        Risks = riskSnapshot;
        NextAction = nextAction;
        NextAuthority = nextAuthority;
        CandidateReusableLesson = candidateReusableLesson;
    }

    public string Disposition { get; }
    public string PublicSummary { get; }
    public IReadOnlyList<string> Changes { get; }
    public IReadOnlyList<AgentWorkVerificationEvidenceV1> VerificationEvidence { get; }
    public IReadOnlyList<string> Assumptions { get; }
    public IReadOnlyList<string> Risks { get; }
    public string? NextAction { get; }
    public AgentWorkOwnerV1? NextAuthority { get; }
    public string? CandidateReusableLesson { get; }
}

/// <summary>
/// One bounded work revision. All descriptive content is passive data; the execution host
/// must obtain current scheduling, claim, cancellation, and approval decisions elsewhere.
/// </summary>
public sealed record AgentWorkPacketV1
{
    [JsonConstructor]
    public AgentWorkPacketV1(
        int schemaVersion,
        string contractId,
        string canonicalization,
        string mode,
        string stage,
        string workId,
        long packetRevision,
        string? previousPacketHash,
        string idempotencyKey,
        string projectId,
        string? projectLinkId,
        string contextSetId,
        string? parentWorkId,
        int depth,
        int maxFanOut,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        string objective,
        string currentState,
        IReadOnlyList<string> constraints,
        AgentWorkApprovalGateV1 approvalGate,
        string expectedOutput,
        IReadOnlyList<string> referenceIds,
        IReadOnlyList<string> evidenceIds,
        IReadOnlyList<string> relativeProjectPaths,
        IReadOnlyList<string> completedWork,
        IReadOnlyList<string> remainingWork,
        string nextAction,
        IReadOnlyList<string> blockers,
        IReadOnlyList<string> knownFailures,
        IReadOnlyList<AgentWorkSourceRevisionV1> sourceRevisions,
        AgentWorkFreshnessV1 freshness,
        AgentWorkOwnerV1 owner,
        int maxContextBytes,
        bool cancellationRequested,
        string fallback,
        AgentWorkCloseoutV1? closeout,
        string contentTreatment,
        bool executable,
        bool grantsSchedulingAuthority,
        bool grantsTaskClaim,
        bool grantsAuthority,
        string safetyNotice,
        string packetHash)
    {
        AgentWorkPacketShapes.RequireHeader(
            schemaVersion,
            contractId,
            AgentWorkPacketContractIds.Packet,
            canonicalization);
        if (mode is not (AgentCoordinationModeIds.Standalone or AgentCoordinationModeIds.Integrated))
            throw new ArgumentException("The coordination mode is invalid.", nameof(mode));
        if (stage is not (
                AgentWorkPacketStageIds.Start or
                AgentWorkPacketStageIds.Continue or
                AgentWorkPacketStageIds.Closeout))
        {
            throw new ArgumentException("The work-packet stage is invalid.", nameof(stage));
        }
        AgentWorkPacketShapes.RequireIdentifier(workId, nameof(workId));
        AgentWorkPacketShapes.RequireIdentifier(idempotencyKey, nameof(idempotencyKey));
        AgentWorkPacketShapes.RequireIdentifier(projectId, nameof(projectId));
        if (projectLinkId is not null)
            AgentWorkPacketShapes.RequirePrefixedHash(
                projectLinkId,
                "plink_",
                nameof(projectLinkId));
        if (mode == AgentCoordinationModeIds.Integrated && projectLinkId is null)
            throw new ArgumentException("Integrated work requires an exact project-link identifier.", nameof(projectLinkId));
        AgentWorkPacketShapes.RequirePrefixedHash(
            contextSetId,
            "ctx_",
            nameof(contextSetId));
        if (previousPacketHash is not null)
            AgentWorkPacketShapes.RequireHash(previousPacketHash, nameof(previousPacketHash));
        if ((stage == AgentWorkPacketStageIds.Start &&
                (packetRevision != 1 || previousPacketHash is not null)) ||
            (stage != AgentWorkPacketStageIds.Start &&
                (packetRevision is < 2 or > 9_007_199_254_740_991 || previousPacketHash is null)))
        {
            throw new ArgumentException("The packet revision does not match its stage.");
        }
        if (parentWorkId is not null)
        {
            AgentWorkPacketShapes.RequireIdentifier(parentWorkId, nameof(parentWorkId));
            if (string.Equals(parentWorkId, workId, StringComparison.Ordinal))
                throw new ArgumentException("Work cannot name itself as its parent.", nameof(parentWorkId));
        }
        if ((parentWorkId is null && depth != 0) ||
            (parentWorkId is not null && depth is < 1 or > AgentWorkPacketContractLimits.MaximumDepth) ||
            maxFanOut is < 0 or > AgentWorkPacketContractLimits.MaximumFanOut)
        {
            throw new ArgumentException("The delegation depth or fan-out is invalid.");
        }
        AgentWorkPacketShapes.RequireUtc(issuedAt, nameof(issuedAt));
        AgentWorkPacketShapes.RequireUtc(expiresAt, nameof(expiresAt));
        if (expiresAt <= issuedAt ||
            expiresAt - issuedAt > AgentWorkPacketContractLimits.MaximumPacketLifetime)
        {
            throw new ArgumentException("The work-packet lifetime is invalid.", nameof(expiresAt));
        }

        AgentWorkPacketShapes.RequireText(
            objective,
            AgentWorkPacketContractLimits.MaximumObjectiveCharacters,
            nameof(objective));
        AgentWorkPacketShapes.RequireText(
            currentState,
            AgentWorkPacketContractLimits.MaximumStateCharacters,
            nameof(currentState));
        AgentWorkPacketShapes.RequireText(
            expectedOutput,
            AgentWorkPacketContractLimits.MaximumExpectedOutputCharacters,
            nameof(expectedOutput));
        AgentWorkPacketShapes.RequireText(
            nextAction,
            AgentWorkPacketContractLimits.MaximumItemCharacters,
            nameof(nextAction));
        AgentWorkPacketShapes.RequireText(
            fallback,
            AgentWorkPacketContractLimits.MaximumFallbackCharacters,
            nameof(fallback));

        var constraintSnapshot = AgentWorkPacketShapes.SnapshotTextList(
            constraints,
            AgentWorkPacketContractLimits.MaximumConstraints,
            AgentWorkPacketContractLimits.MaximumItemCharacters,
            nameof(constraints));
        var referenceSnapshot = AgentWorkPacketShapes.SnapshotIdentifierSet(
            referenceIds,
            AgentWorkPacketContractLimits.MaximumReferences,
            nameof(referenceIds));
        var evidenceSnapshot = AgentWorkPacketShapes.SnapshotIdentifierSet(
            evidenceIds,
            AgentWorkPacketContractLimits.MaximumEvidence,
            nameof(evidenceIds));
        var pathSnapshot = AgentWorkPacketShapes.SnapshotRelativeProjectPaths(
            relativeProjectPaths,
            nameof(relativeProjectPaths));
        var completedSnapshot = AgentWorkPacketShapes.SnapshotTextList(
            completedWork,
            AgentWorkPacketContractLimits.MaximumWorkItems,
            AgentWorkPacketContractLimits.MaximumItemCharacters,
            nameof(completedWork));
        var remainingSnapshot = AgentWorkPacketShapes.SnapshotTextList(
            remainingWork,
            AgentWorkPacketContractLimits.MaximumWorkItems,
            AgentWorkPacketContractLimits.MaximumItemCharacters,
            nameof(remainingWork));
        var blockerSnapshot = AgentWorkPacketShapes.SnapshotTextList(
            blockers,
            AgentWorkPacketContractLimits.MaximumBlockers,
            AgentWorkPacketContractLimits.MaximumItemCharacters,
            nameof(blockers));
        var failureSnapshot = AgentWorkPacketShapes.SnapshotTextList(
            knownFailures,
            AgentWorkPacketContractLimits.MaximumKnownFailures,
            AgentWorkPacketContractLimits.MaximumItemCharacters,
            nameof(knownFailures));
        var sourceSnapshot = AgentWorkPacketShapes.SnapshotList(
                sourceRevisions,
                AgentWorkPacketContractLimits.MaximumSourceRevisions,
                nameof(sourceRevisions),
                requireNonEmpty: true)
            .OrderBy(item => item.SourceId, StringComparer.Ordinal)
            .ToArray();
        if (sourceSnapshot.Select(item => item.SourceId)
                .Distinct(StringComparer.Ordinal).Count() != sourceSnapshot.Length)
        {
            throw new ArgumentException("Source identifiers must be unique.", nameof(sourceRevisions));
        }

        ApprovalGate = approvalGate ?? throw new ArgumentNullException(nameof(approvalGate));
        Freshness = freshness ?? throw new ArgumentNullException(nameof(freshness));
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        if (Freshness.ObservedAt > issuedAt + AgentWorkPacketContractLimits.MaximumClockSkew ||
            (Freshness.State == AgentWorkFreshnessStateIds.Current &&
                Freshness.ValidUntil < expiresAt))
        {
            throw new ArgumentException("The packet lifetime exceeds its reported source freshness.", nameof(freshness));
        }
        if (maxContextBytes is < 1 or > AgentWorkPacketContractLimits.MaximumContextBytes)
            throw new ArgumentOutOfRangeException(nameof(maxContextBytes));

        if ((stage == AgentWorkPacketStageIds.Start &&
                (completedSnapshot.Count != 0 || remainingSnapshot.Count == 0)) ||
            (stage == AgentWorkPacketStageIds.Continue && remainingSnapshot.Count == 0) ||
            (stage == AgentWorkPacketStageIds.Closeout && closeout is null) ||
            (stage != AgentWorkPacketStageIds.Closeout && closeout is not null))
        {
            throw new ArgumentException("The work progress is inconsistent with the packet stage.");
        }
        if (closeout is not null)
        {
            if ((closeout.Disposition == AgentWorkCloseoutDispositionIds.Solved &&
                    remainingSnapshot.Count != 0) ||
                (closeout.Disposition != AgentWorkCloseoutDispositionIds.Solved &&
                    remainingSnapshot.Count == 0) ||
                closeout.VerificationEvidence.Any(item => !evidenceSnapshot.Contains(
                    item.EvidenceId,
                    StringComparer.Ordinal)) ||
                (closeout.NextAction is not null &&
                    !string.Equals(closeout.NextAction, nextAction, StringComparison.Ordinal)))
            {
                throw new ArgumentException("The closeout does not match packet progress or evidence.", nameof(closeout));
            }
        }

        if (!string.Equals(contentTreatment, AgentWorkPacketContractIds.ContentTreatment, StringComparison.Ordinal) ||
            executable || grantsSchedulingAuthority || grantsTaskClaim || grantsAuthority ||
            !string.Equals(safetyNotice, AgentWorkPacketContractIds.SafetyNotice, StringComparison.Ordinal))
        {
            throw new ArgumentException("The packet must remain passive untrusted data without authority.");
        }
        AgentWorkPacketShapes.RequireHash(packetHash, nameof(packetHash));

        SchemaVersion = schemaVersion;
        ContractId = contractId;
        Canonicalization = canonicalization;
        Mode = mode;
        Stage = stage;
        WorkId = workId;
        PacketRevision = packetRevision;
        PreviousPacketHash = previousPacketHash;
        IdempotencyKey = idempotencyKey;
        ProjectId = projectId;
        ProjectLinkId = projectLinkId;
        ContextSetId = contextSetId;
        ParentWorkId = parentWorkId;
        Depth = depth;
        MaxFanOut = maxFanOut;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        Objective = objective;
        CurrentState = currentState;
        Constraints = constraintSnapshot;
        ExpectedOutput = expectedOutput;
        ReferenceIds = referenceSnapshot;
        EvidenceIds = evidenceSnapshot;
        RelativeProjectPaths = pathSnapshot;
        CompletedWork = completedSnapshot;
        RemainingWork = remainingSnapshot;
        NextAction = nextAction;
        Blockers = blockerSnapshot;
        KnownFailures = failureSnapshot;
        SourceRevisions = Array.AsReadOnly(sourceSnapshot);
        MaxContextBytes = maxContextBytes;
        CancellationRequested = cancellationRequested;
        Fallback = fallback;
        Closeout = closeout;
        ContentTreatment = contentTreatment;
        Executable = executable;
        GrantsSchedulingAuthority = grantsSchedulingAuthority;
        GrantsTaskClaim = grantsTaskClaim;
        GrantsAuthority = grantsAuthority;
        SafetyNotice = safetyNotice;
        PacketHash = packetHash;

        if (CortexCanonicalJson.Serialize(this).Length >
            AgentWorkPacketContractLimits.MaximumSerializedPacketBytes)
        {
            throw new ArgumentException("The canonical work packet exceeds eight KiB.");
        }
    }

    public int SchemaVersion { get; }
    public string ContractId { get; }
    public string Canonicalization { get; }
    public string Mode { get; }
    public string Stage { get; }
    public string WorkId { get; }
    public long PacketRevision { get; }
    public string? PreviousPacketHash { get; }
    public string IdempotencyKey { get; }
    public string ProjectId { get; }
    public string? ProjectLinkId { get; }
    /// <summary>
    /// Identity of the exact bounded recovery context used for this revision.
    /// This is not the work lineage key and may change in a valid successor.
    /// </summary>
    public string ContextSetId { get; }
    public string? ParentWorkId { get; }
    public int Depth { get; }
    public int MaxFanOut { get; }
    public DateTimeOffset IssuedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string Objective { get; }
    public string CurrentState { get; }
    public IReadOnlyList<string> Constraints { get; }
    public AgentWorkApprovalGateV1 ApprovalGate { get; }
    public string ExpectedOutput { get; }
    public IReadOnlyList<string> ReferenceIds { get; }
    public IReadOnlyList<string> EvidenceIds { get; }
    public IReadOnlyList<string> RelativeProjectPaths { get; }
    public IReadOnlyList<string> CompletedWork { get; }
    public IReadOnlyList<string> RemainingWork { get; }
    public string NextAction { get; }
    public IReadOnlyList<string> Blockers { get; }
    public IReadOnlyList<string> KnownFailures { get; }
    public IReadOnlyList<AgentWorkSourceRevisionV1> SourceRevisions { get; }
    public AgentWorkFreshnessV1 Freshness { get; }
    public AgentWorkOwnerV1 Owner { get; }
    public int MaxContextBytes { get; }
    public bool CancellationRequested { get; }
    public string Fallback { get; }
    public AgentWorkCloseoutV1? Closeout { get; }
    public string ContentTreatment { get; }
    public bool Executable { get; }
    public bool GrantsSchedulingAuthority { get; }
    public bool GrantsTaskClaim { get; }
    public bool GrantsAuthority { get; }
    public string SafetyNotice { get; }
    public string PacketHash { get; }

    internal AgentWorkPacketV1 CopyWithPacketHash(string packetHash) => new(
        SchemaVersion,
        ContractId,
        Canonicalization,
        Mode,
        Stage,
        WorkId,
        PacketRevision,
        PreviousPacketHash,
        IdempotencyKey,
        ProjectId,
        ProjectLinkId,
        ContextSetId,
        ParentWorkId,
        Depth,
        MaxFanOut,
        IssuedAt,
        ExpiresAt,
        Objective,
        CurrentState,
        Constraints,
        ApprovalGate,
        ExpectedOutput,
        ReferenceIds,
        EvidenceIds,
        RelativeProjectPaths,
        CompletedWork,
        RemainingWork,
        NextAction,
        Blockers,
        KnownFailures,
        SourceRevisions,
        Freshness,
        Owner,
        MaxContextBytes,
        CancellationRequested,
        Fallback,
        Closeout,
        ContentTreatment,
        Executable,
        GrantsSchedulingAuthority,
        GrantsTaskClaim,
        GrantsAuthority,
        SafetyNotice,
        packetHash);
}
