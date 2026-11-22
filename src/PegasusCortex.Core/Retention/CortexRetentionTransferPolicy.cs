using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Query;

namespace Pegasus.Cortex.Core.Retention;

/// <summary>
/// Creates deterministic bundles from already validated retained records.
/// Storage adapters remain responsible for selecting the complete project set.
/// </summary>
public static class CortexRetentionTransferBundleFactory
{
    public static CortexRetentionTransferBundleV1 Create(
        string bundleId,
        DateTimeOffset capturedAt,
        CortexRetentionTransferProjectScope project,
        CortexRetentionTransferProvenance provenance,
        IEnumerable<CortexRetainedDocument> authoritativeDocuments,
        IEnumerable<CortexRetainedDocument>? derivedBriefEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(provenance.EvidenceHashes);
        ArgumentNullException.ThrowIfNull(authoritativeDocuments);

        var authoritative = authoritativeDocuments
            .Select(document => document?.Snapshot() ??
                throw new ArgumentException("A transfer document is missing.", nameof(authoritativeDocuments)))
            .OrderBy(document => CortexRetentionTransferBundlePolicy.KindOrder(document.Kind))
            .ThenBy(document => document.DocumentId, StringComparer.Ordinal)
            .ThenBy(document => document.Revision)
            .Select((document, sequence) =>
                CortexRetentionTransferDocument.FromRetainedDocument(document, sequence))
            .ToArray();
        var derived = (derivedBriefEvidence ?? [])
            .Select(document => document?.Snapshot() ??
                throw new ArgumentException("A comparison brief is missing.", nameof(derivedBriefEvidence)))
            .OrderBy(document => document.DocumentId, StringComparer.Ordinal)
            .ThenBy(document => document.Revision)
            .Select((document, sequence) =>
                CortexRetentionTransferDocument.FromRetainedDocument(document, sequence))
            .ToArray();
        var normalizedProvenance = provenance with
        {
            EvidenceHashes = Array.AsReadOnly(
                provenance.EvidenceHashes
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray())
        };
        return CortexRetentionTransferBundlePolicy.Seal(new CortexRetentionTransferBundleV1(
            CortexRetentionTransferContract.SchemaVersion,
            CortexRetentionTransferContract.ContractId,
            CortexRetentionTransferContract.Canonicalization,
            bundleId,
            capturedAt,
            project,
            normalizedProvenance,
            Array.AsReadOnly(authoritative),
            Array.AsReadOnly(derived),
            CortexRetentionTransferContract.EmptySha256));
    }
}

/// <summary>
/// Exact integrity, scope, chain, typed-contract, and content validation for a
/// provider-neutral transfer. It performs no persistence or network I/O.
/// </summary>
public static class CortexRetentionTransferBundlePolicy
{
    private static readonly string[] TransferableKinds =
    [
        CortexRetainedDocumentKinds.TaskSnapshot,
        CortexRetainedDocumentKinds.WorkPacket,
        CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
        CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt
    ];

    public static IReadOnlyList<string> AuthoritativeKinds { get; } =
        Array.AsReadOnly(TransferableKinds);

    public static CortexRetentionTransferBundleV1 Seal(
        CortexRetentionTransferBundleV1 bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var placeholder = Capture(bundle with
        {
            BundleHash = CortexRetentionTransferContract.EmptySha256
        });
        ValidateCore(placeholder, requireSelfHash: false);
        var sealedBundle = placeholder with
        {
            BundleHash = CortexCanonicalJson.Sha256(
                CortexCanonicalJson.Serialize(placeholder))
        };
        return Validate(sealedBundle);
    }

    public static CortexRetentionTransferBundleV1 Validate(
        CortexRetentionTransferBundleV1 bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var captured = Capture(bundle);
        ValidateCore(captured, requireSelfHash: true);
        return captured;
    }

    /// <summary>
    /// Reads only exact canonical bundle JSON. Callers should not accept a
    /// deserialize-reserialize normalization at the import boundary.
    /// </summary>
    public static CortexRetentionTransferBundleV1 ReadCanonical(
        ReadOnlySpan<byte> canonicalUtf8)
    {
        try
        {
            var value = CortexCanonicalJson.Deserialize<CortexRetentionTransferBundleV1>(
                canonicalUtf8);
            if (!CortexCanonicalJson.Serialize(value).AsSpan().SequenceEqual(canonicalUtf8))
                Reject("transfer_bundle_not_canonical");
            return Validate(value);
        }
        catch (CortexRetentionTransferValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or JsonException or
                InvalidOperationException or NotSupportedException)
        {
            throw new CortexRetentionTransferValidationException(
                "transfer_bundle_decode_invalid");
        }
    }

    internal static int KindOrder(string kind) => kind switch
    {
        CortexRetainedDocumentKinds.TaskSnapshot => 0,
        CortexRetainedDocumentKinds.WorkPacket => 1,
        CortexRetainedDocumentKinds.WorkPacketHandoffReceipt => 2,
        CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt => 3,
        _ => int.MaxValue
    };

    private static void ValidateCore(
        CortexRetentionTransferBundleV1 bundle,
        bool requireSelfHash)
    {
        if (bundle.SchemaVersion != CortexRetentionTransferContract.SchemaVersion ||
            !string.Equals(bundle.ContractId, CortexRetentionTransferContract.ContractId, StringComparison.Ordinal) ||
            !string.Equals(bundle.Canonicalization, CortexRetentionTransferContract.Canonicalization, StringComparison.Ordinal))
        {
            Reject("transfer_bundle_header_invalid");
        }
        RequireIdentifier(bundle.BundleId, "transfer_bundle_id_invalid");
        RequireUtc(bundle.CapturedAt, "transfer_bundle_capture_time_invalid");
        ValidateProject(bundle.Project);
        ValidateProvenance(bundle.Provenance);

        if (bundle.AuthoritativeDocuments is null ||
            bundle.AuthoritativeDocuments.Count is < 1 or > CortexRetentionTransferContract.MaximumDocuments ||
            bundle.DerivedBriefEvidence is null ||
            bundle.DerivedBriefEvidence.Count > CortexRetentionTransferContract.MaximumDerivedBriefs)
        {
            Reject("transfer_bundle_document_count_invalid");
        }

        long payloadBytes = 0;
        foreach (var document in bundle.AuthoritativeDocuments)
            payloadBytes = CheckedPayload(payloadBytes, document?.CanonicalUtf8);
        foreach (var document in bundle.DerivedBriefEvidence)
            payloadBytes = CheckedPayload(payloadBytes, document?.CanonicalUtf8);
        if (payloadBytes > CortexRetentionTransferContract.MaximumCanonicalPayloadBytes)
            Reject("transfer_bundle_payload_too_large");

        ValidateDocumentOrdering(bundle.AuthoritativeDocuments, derived: false);
        ValidateDocumentOrdering(bundle.DerivedBriefEvidence, derived: true);
        var typed = ValidateAuthoritativeDocuments(bundle);
        ValidateDerivedBriefEvidence(bundle, typed.TaskSnapshots);
        ValidateTypedChains(bundle, typed);

        if (bundle.CapturedAt < bundle.AuthoritativeDocuments.Max(item => item.CapturedAt) ||
            bundle.DerivedBriefEvidence.Count > 0 &&
            bundle.CapturedAt < bundle.DerivedBriefEvidence.Max(item => item.CapturedAt))
        {
            Reject("transfer_bundle_capture_precedes_content");
        }

        if (requireSelfHash)
        {
            RequireHash(bundle.BundleHash, "transfer_bundle_hash_invalid");
            var placeholder = bundle with
            {
                BundleHash = CortexRetentionTransferContract.EmptySha256
            };
            var expected = CortexCanonicalJson.Sha256(
                CortexCanonicalJson.Serialize(placeholder));
            if (!string.Equals(bundle.BundleHash, expected, StringComparison.Ordinal))
                Reject("transfer_bundle_hash_mismatch");
        }
        else if (!string.Equals(
                     bundle.BundleHash,
                     CortexRetentionTransferContract.EmptySha256,
                     StringComparison.Ordinal))
        {
            Reject("transfer_bundle_placeholder_hash_invalid");
        }
    }

    private static TypedDocuments ValidateAuthoritativeDocuments(
        CortexRetentionTransferBundleV1 bundle)
    {
        var taskSnapshots = new Dictionary<DocumentRevision, AgentTaskSnapshotV1>();
        var workPackets = new Dictionary<DocumentRevision, AgentWorkPacketV1>();
        var handoffs = new Dictionary<string, AgentWorkPacketHandoffReceiptV1>(StringComparer.Ordinal);
        var acceptances = new Dictionary<string, AgentWorkPacketAcceptanceReceiptV1>(StringComparer.Ordinal);
        var idempotencyKeys = new HashSet<RetentionIdempotencyKey>();

        foreach (var document in bundle.AuthoritativeDocuments)
        {
            if (!TransferableKinds.Contains(document.Kind, StringComparer.Ordinal) ||
                !string.Equals(document.RepositoryProjectId, bundle.Project.ProjectId, StringComparison.Ordinal))
            {
                Reject("transfer_bundle_authoritative_kind_or_scope_invalid");
            }
            ValidateEnvelope(document);
            if (!idempotencyKeys.Add(new RetentionIdempotencyKey(
                    document.RepositoryProjectId,
                    document.Kind,
                    document.ProducerId,
                    document.IdempotencyKey)))
            {
                Reject("transfer_bundle_idempotency_key_reused");
            }

            switch (document.Kind)
            {
                case CortexRetainedDocumentKinds.TaskSnapshot:
                {
                    var value = Exact<AgentTaskSnapshotV1>(document);
                    if (!AgentTaskSnapshotPolicy.HasValidHash(value) ||
                        !string.Equals(document.ContractId, AgentTaskSnapshotContractIds.Snapshot, StringComparison.Ordinal) ||
                        !string.Equals(value.ProjectId, bundle.Project.ProjectId, StringComparison.Ordinal) ||
                        !string.Equals(value.ProjectLinkId, bundle.Project.ProjectLinkId, StringComparison.Ordinal) ||
                        !string.Equals(value.TaskSetId, document.DocumentId, StringComparison.Ordinal) ||
                        value.SnapshotRevision != document.Revision ||
                        !string.Equals(value.IdempotencyKey, document.IdempotencyKey, StringComparison.Ordinal) ||
                        !string.Equals(ProducerId(value.Producer), document.ProducerId, StringComparison.Ordinal) ||
                        value.CapturedAt != document.CapturedAt ||
                        value.ExpiresAt != document.ExpiresAt ||
                        !string.Equals(
                            CortexAgentRetentionSearchProjection.Create(value),
                            document.SearchText,
                            StringComparison.Ordinal))
                    {
                        Reject("transfer_bundle_task_snapshot_invalid");
                    }
                    if (!taskSnapshots.TryAdd(
                            new DocumentRevision(document.DocumentId, document.Revision),
                            value))
                    {
                        Reject("transfer_bundle_revision_duplicated");
                    }
                    break;
                }
                case CortexRetainedDocumentKinds.WorkPacket:
                {
                    var value = Exact<AgentWorkPacketV1>(document);
                    if (!AgentWorkPacketPolicy.HasValidHash(value) ||
                        !string.Equals(document.ContractId, AgentWorkPacketContractIds.Packet, StringComparison.Ordinal) ||
                        !string.Equals(value.ProjectId, bundle.Project.ProjectId, StringComparison.Ordinal) ||
                        !string.Equals(value.ProjectLinkId, bundle.Project.ProjectLinkId, StringComparison.Ordinal) ||
                        !string.Equals(value.WorkId, document.DocumentId, StringComparison.Ordinal) ||
                        value.PacketRevision != document.Revision ||
                        !string.Equals(value.IdempotencyKey, document.IdempotencyKey, StringComparison.Ordinal) ||
                        !string.Equals(ProducerId(value.Owner), document.ProducerId, StringComparison.Ordinal) ||
                        value.IssuedAt != document.CapturedAt ||
                        value.ExpiresAt != document.ExpiresAt ||
                        !string.Equals(
                            CortexAgentRetentionSearchProjection.Create(value),
                            document.SearchText,
                            StringComparison.Ordinal))
                    {
                        Reject("transfer_bundle_work_packet_invalid");
                    }
                    if (!workPackets.TryAdd(
                            new DocumentRevision(document.DocumentId, document.Revision),
                            value))
                    {
                        Reject("transfer_bundle_revision_duplicated");
                    }
                    break;
                }
                case CortexRetainedDocumentKinds.WorkPacketHandoffReceipt:
                {
                    var value = Exact<AgentWorkPacketHandoffReceiptV1>(document);
                    if (document.Revision != 1 || document.PreviousHash is not null ||
                        !AgentWorkPacketPolicy.HasValidHash(value) ||
                        !string.Equals(document.ContractId, AgentWorkPacketContractIds.HandoffReceipt, StringComparison.Ordinal) ||
                        !string.Equals(value.ReceiptId, document.DocumentId, StringComparison.Ordinal) ||
                        !string.Equals(value.IdempotencyKey, document.IdempotencyKey, StringComparison.Ordinal) ||
                        !string.Equals(ProducerId(value.FromOwner), document.ProducerId, StringComparison.Ordinal) ||
                        value.HandedOffAt != document.CapturedAt ||
                        value.PacketExpiresAt != document.ExpiresAt ||
                        !string.Equals(
                            ReceiptSearchText(value.WorkId, value.PacketStage, "handoff", value.ReceiptId),
                            document.SearchText,
                            StringComparison.Ordinal) ||
                        !handoffs.TryAdd(value.ReceiptId, value))
                    {
                        Reject("transfer_bundle_handoff_receipt_invalid");
                    }
                    break;
                }
                case CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt:
                {
                    var value = Exact<AgentWorkPacketAcceptanceReceiptV1>(document);
                    if (document.Revision != 1 || document.PreviousHash is not null ||
                        !AgentWorkPacketPolicy.HasValidHash(value) ||
                        !string.Equals(document.ContractId, AgentWorkPacketContractIds.AcceptanceReceipt, StringComparison.Ordinal) ||
                        !string.Equals(value.ReceiptId, document.DocumentId, StringComparison.Ordinal) ||
                        !string.Equals(value.IdempotencyKey, document.IdempotencyKey, StringComparison.Ordinal) ||
                        !string.Equals(ProducerId(value.AcceptedBy), document.ProducerId, StringComparison.Ordinal) ||
                        value.AcceptedAt != document.CapturedAt ||
                        !string.Equals(
                            ReceiptSearchText(value.WorkId, value.PacketStage, value.Disposition, value.ReceiptId),
                            document.SearchText,
                            StringComparison.Ordinal) ||
                        !acceptances.TryAdd(value.ReceiptId, value))
                    {
                        Reject("transfer_bundle_acceptance_receipt_invalid");
                    }
                    break;
                }
            }
        }

        return new TypedDocuments(taskSnapshots, workPackets, handoffs, acceptances);
    }

    private static void ValidateTypedChains(
        CortexRetentionTransferBundleV1 bundle,
        TypedDocuments typed)
    {
        var taskChains = typed.TaskSnapshots
            .GroupBy(item => item.Key.DocumentId, StringComparer.Ordinal)
            .ToArray();
        if (taskChains.Length != 1)
            Reject("transfer_bundle_task_chain_count_invalid");

        foreach (var chain in taskChains)
        {
            var ordered = chain.OrderBy(item => item.Key.Revision).Select(item => item.Value).ToArray();
            if (ordered[0].SnapshotRevision != 1 || ordered[0].PreviousSnapshotHash is not null)
                Reject("transfer_bundle_task_chain_initial_invalid");
            for (var index = 1; index < ordered.Length; index++)
            {
                if (!AgentTaskSnapshotPolicy.ValidateRevision(ordered[index - 1], ordered[index]).Valid)
                    Reject("transfer_bundle_task_chain_invalid");
            }
        }

        foreach (var chain in typed.WorkPackets.GroupBy(item => item.Key.DocumentId, StringComparer.Ordinal))
        {
            var ordered = chain.OrderBy(item => item.Key.Revision).Select(item => item.Value).ToArray();
            if (ordered[0].PacketRevision != 1 || ordered[0].PreviousPacketHash is not null)
                Reject("transfer_bundle_packet_chain_initial_invalid");
            for (var index = 1; index < ordered.Length; index++)
            {
                if (!AgentWorkPacketPolicy.ValidateContinuation(ordered[index - 1], ordered[index]).Valid)
                    Reject("transfer_bundle_packet_chain_invalid");
            }
        }

        foreach (var handoff in typed.Handoffs.Values)
        {
            if (!typed.WorkPackets.TryGetValue(
                    new DocumentRevision(handoff.WorkId, handoff.PacketRevision),
                    out var packet) ||
                !AgentWorkPacketPolicy.ValidateHandoff(packet, handoff).Valid)
            {
                Reject("transfer_bundle_handoff_link_invalid");
            }
        }
        foreach (var acceptance in typed.Acceptances.Values)
        {
            if (!typed.Handoffs.TryGetValue(acceptance.HandoffReceiptId, out var handoff) ||
                !AgentWorkPacketPolicy.ValidateAcceptance(handoff, acceptance).Valid)
            {
                Reject("transfer_bundle_acceptance_link_invalid");
            }
            var envelope = bundle.AuthoritativeDocuments.Single(item =>
                item.Kind == CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt &&
                string.Equals(item.DocumentId, acceptance.ReceiptId, StringComparison.Ordinal));
            if (envelope.ExpiresAt != handoff.PacketExpiresAt)
                Reject("transfer_bundle_acceptance_expiry_invalid");
        }

        var latestTask = taskChains[0]
            .OrderByDescending(item => item.Key.Revision)
            .First().Value;
        var authoritySetHash = CortexProjectBriefService.ComputeAuthoritySetHash(
            latestTask.SourceRevisions);
        if (!string.Equals(
                authoritySetHash,
                bundle.Project.AuthoritySetHash,
                StringComparison.Ordinal))
        {
            Reject("transfer_bundle_authority_hash_mismatch");
        }
    }

    private static void ValidateDerivedBriefEvidence(
        CortexRetentionTransferBundleV1 bundle,
        IReadOnlyDictionary<DocumentRevision, AgentTaskSnapshotV1> taskSnapshots)
    {
        var sourceSnapshots = taskSnapshots.Values.ToDictionary(
            value => value.SnapshotHash,
            value => value,
            StringComparer.Ordinal);
        var typedBriefs = new List<CortexProjectBriefSnapshot>();
        foreach (var document in bundle.DerivedBriefEvidence)
        {
            if (!string.Equals(document.Kind, CortexRetainedDocumentKinds.ProjectBrief, StringComparison.Ordinal) ||
                !string.Equals(document.RepositoryProjectId, bundle.Project.ProjectLinkId, StringComparison.Ordinal))
            {
                Reject("transfer_bundle_comparison_brief_scope_invalid");
            }
            ValidateEnvelope(document);
            var brief = Exact<CortexProjectBriefSnapshot>(document);
            if (!string.Equals(document.ContractId, CortexProjectBriefContract.ContractId, StringComparison.Ordinal) ||
                document.Revision != brief.BriefRevision ||
                !string.Equals(document.DocumentId, bundle.Project.ProjectNamespace, StringComparison.Ordinal) ||
                !string.Equals(brief.TenantId, bundle.Project.TenantId, StringComparison.Ordinal) ||
                !string.Equals(brief.ProjectLinkId, bundle.Project.ProjectLinkId, StringComparison.Ordinal) ||
                !string.Equals(brief.ProjectId, bundle.Project.ProjectId, StringComparison.Ordinal) ||
                !string.Equals(brief.ProjectNamespace, bundle.Project.ProjectNamespace, StringComparison.Ordinal) ||
                !string.Equals(brief.ContextSetId, CortexProjectBriefService.ComputeContextSetId(brief), StringComparison.Ordinal) ||
                brief.Freshness.ReviewAfter is null ||
                brief.Freshness.ObservedAt != document.CapturedAt ||
                brief.Freshness.ReviewAfter != document.ExpiresAt ||
                !sourceSnapshots.TryGetValue(brief.SourceHash, out var source) ||
                !string.Equals(
                    brief.AuthoritySetHash,
                    CortexProjectBriefService.ComputeAuthoritySetHash(source.SourceRevisions),
                    StringComparison.Ordinal))
            {
                Reject("transfer_bundle_comparison_brief_invalid");
            }
            typedBriefs.Add(brief);
        }

        foreach (var chain in typedBriefs.GroupBy(
                     brief => brief.ProjectNamespace,
                     StringComparer.Ordinal))
        {
            var ordered = chain.OrderBy(brief => brief.BriefRevision).ToArray();
            if (ordered[0].BriefRevision != 1 ||
                ordered[0].PreviousContextSetId is not null)
            {
                Reject("transfer_bundle_comparison_brief_initial_invalid");
            }
            for (var index = 1; index < ordered.Length; index++)
            {
                if (ordered[index].BriefRevision != ordered[index - 1].BriefRevision + 1 ||
                    !string.Equals(
                        ordered[index].PreviousContextSetId,
                        ordered[index - 1].ContextSetId,
                        StringComparison.Ordinal))
                {
                    Reject("transfer_bundle_comparison_brief_chain_invalid");
                }
            }
        }
    }

    private static void ValidateDocumentOrdering(
        IReadOnlyList<CortexRetentionTransferDocument> documents,
        bool derived)
    {
        CortexRetentionTransferDocument? previous = null;
        var chains = new Dictionary<ChainKey, CortexRetentionTransferDocument>();
        for (var index = 0; index < documents.Count; index++)
        {
            var document = documents[index] ??
                throw new CortexRetentionTransferValidationException(
                    "transfer_bundle_document_missing");
            if (document.Sequence != index)
                Reject("transfer_bundle_sequence_invalid");
            if (previous is not null && CompareDocuments(previous, document, derived) >= 0)
                Reject("transfer_bundle_order_invalid");

            var key = new ChainKey(
                document.RepositoryProjectId,
                document.Kind,
                document.DocumentId);
            if (!chains.TryGetValue(key, out var predecessor))
            {
                if (document.Revision != 1 || document.PreviousHash is not null)
                    Reject("transfer_bundle_chain_initial_invalid");
            }
            else if (document.Revision != predecessor.Revision + 1 ||
                     !string.Equals(document.PreviousHash, predecessor.ContentHash, StringComparison.Ordinal))
            {
                Reject("transfer_bundle_chain_gap_or_hash_invalid");
            }
            chains[key] = document;
            previous = document;
        }
    }

    private static int CompareDocuments(
        CortexRetentionTransferDocument left,
        CortexRetentionTransferDocument right,
        bool derived)
    {
        var result = derived ? 0 : KindOrder(left.Kind).CompareTo(KindOrder(right.Kind));
        if (result != 0)
            return result;
        result = string.CompareOrdinal(left.RepositoryProjectId, right.RepositoryProjectId);
        if (result != 0)
            return result;
        result = string.CompareOrdinal(left.DocumentId, right.DocumentId);
        if (result != 0)
            return result;
        return left.Revision.CompareTo(right.Revision);
    }

    private static void ValidateEnvelope(CortexRetentionTransferDocument document)
    {
        try
        {
            _ = document.ToAppendRequest().Validate();
        }
        catch (Exception exception) when (
            exception is ArgumentException or CortexRetentionContentRejectedException)
        {
            Reject("transfer_bundle_retained_envelope_invalid");
        }
        if (ContainsAbsoluteMachinePath(document.CanonicalUtf8) ||
            ContainsAbsoluteMachinePath(document.SearchText))
        {
            Reject("transfer_bundle_absolute_path_prohibited");
        }
    }

    private static T Exact<T>(CortexRetentionTransferDocument document)
    {
        try
        {
            var value = CortexCanonicalJson.Deserialize<T>(document.CanonicalUtf8);
            if (!CortexCanonicalJson.Serialize(value)
                    .AsSpan().SequenceEqual(document.CanonicalUtf8))
            {
                Reject("transfer_bundle_typed_document_not_canonical");
            }
            return value;
        }
        catch (CortexRetentionTransferValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or JsonException or
                InvalidOperationException or NotSupportedException)
        {
            throw new CortexRetentionTransferValidationException(
                "transfer_bundle_typed_document_invalid");
        }
    }

    private static bool ContainsAbsoluteMachinePath(byte[] canonicalUtf8)
    {
        try
        {
            using var json = JsonDocument.Parse(canonicalUtf8);
            return ContainsAbsoluteMachinePath(json.RootElement);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool ContainsAbsoluteMachinePath(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject()
                .Any(property => ContainsAbsoluteMachinePath(property.Value)),
            JsonValueKind.Array => value.EnumerateArray()
                .Any(ContainsAbsoluteMachinePath),
            JsonValueKind.String => ContainsAbsoluteMachinePath(value.GetString() ?? string.Empty),
            _ => false
        };
    }

    internal static bool ContainsAbsoluteMachinePath(string value) =>
        FindAbsoluteMachinePathStart(value, 0) >= 0;

    /// <summary>
    /// Replaces each absolute machine-path segment with one caller-supplied
    /// portable marker. Semicolon and line boundaries are preserved so useful
    /// adjacent hashes and counts survive without carrying the local path.
    /// </summary>
    internal static string RedactAbsoluteMachinePaths(
        string value,
        string replacement)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement);
        var start = FindAbsoluteMachinePathStart(value, 0);
        if (start < 0)
            return value;

        var output = new StringBuilder(value.Length + replacement.Length);
        var cursor = 0;
        while (start >= 0)
        {
            output.Append(value, cursor, start - cursor);
            output.Append(replacement);
            var end = start;
            while (end < value.Length && value[end] is not (';' or '\r' or '\n'))
                end++;
            cursor = end;
            start = FindAbsoluteMachinePathStart(value, cursor);
        }
        output.Append(value, cursor, value.Length - cursor);
        return output.ToString();
    }

    private static int FindAbsoluteMachinePathStart(string value, int startIndex)
    {
        for (var index = startIndex; index < value.Length; index++)
        {
            var boundary = index == 0 || char.IsWhiteSpace(value[index - 1]) ||
                value[index - 1] is '(' or '[' or '{' or '"' or '\'' or
                    '=' or '<' or '>' or '`' or ':' or ';' or ',' or '|' or '~';
            if (!boundary)
                continue;
            if (index + 2 < value.Length && char.IsAsciiLetter(value[index]) &&
                value[index + 1] == ':' && value[index + 2] is '/' or '\\')
            {
                return index;
            }
            if (index + 1 < value.Length && value[index] == '\\' && value[index + 1] == '\\')
                return index;
            if (value[index] == '/' &&
                (index + 1 >= value.Length || value[index + 1] != '/'))
            {
                return index;
            }
        }
        return -1;
    }

    private static void ValidateProject(CortexRetentionTransferProjectScope project)
    {
        if (project is null)
            Reject("transfer_bundle_project_missing");
        RequireIdentifier(project.TenantId, "transfer_bundle_tenant_invalid");
        RequireIdentifier(project.ProjectLinkId, "transfer_bundle_project_link_invalid");
        RequireIdentifier(project.ProjectId, "transfer_bundle_project_id_invalid");
        RequireIdentifier(project.ProjectNamespace, "transfer_bundle_namespace_invalid");
        RequireHash(project.AuthoritySetHash, "transfer_bundle_authority_hash_invalid");
    }

    private static void ValidateProvenance(CortexRetentionTransferProvenance provenance)
    {
        if (provenance is null)
            Reject("transfer_bundle_provenance_missing");
        RequireIdentifier(provenance.SourceProviderId, "transfer_bundle_provider_invalid");
        RequireIdentifier(provenance.ExportToolId, "transfer_bundle_export_tool_invalid");
        if (provenance.SourceStoreEpoch < 1 ||
            provenance.EvidenceHashes is null ||
            provenance.EvidenceHashes.Count > CortexRetentionTransferContract.MaximumEvidenceHashes)
        {
            Reject("transfer_bundle_provenance_invalid");
        }
        string? previous = null;
        foreach (var hash in provenance.EvidenceHashes)
        {
            RequireHash(hash, "transfer_bundle_evidence_hash_invalid");
            if (previous is not null && string.CompareOrdinal(previous, hash) >= 0)
                Reject("transfer_bundle_evidence_hash_order_invalid");
            previous = hash;
        }
    }

    private static CortexRetentionTransferBundleV1 Capture(
        CortexRetentionTransferBundleV1 bundle)
    {
        var authoritative = bundle.AuthoritativeDocuments?
            .Select(item => item?.Snapshot() ??
                throw new CortexRetentionTransferValidationException(
                    "transfer_bundle_document_missing"))
            .ToArray() ?? [];
        var derived = bundle.DerivedBriefEvidence?
            .Select(item => item?.Snapshot() ??
                throw new CortexRetentionTransferValidationException(
                    "transfer_bundle_comparison_brief_missing"))
            .ToArray() ?? [];
        var evidenceHashes = bundle.Provenance?.EvidenceHashes?.ToArray() ?? [];
        return bundle with
        {
            Project = bundle.Project is null
                ? throw new CortexRetentionTransferValidationException(
                    "transfer_bundle_project_missing")
                : bundle.Project with { },
            Provenance = bundle.Provenance is null
                ? throw new CortexRetentionTransferValidationException(
                    "transfer_bundle_provenance_missing")
                : bundle.Provenance with
                {
                    EvidenceHashes = Array.AsReadOnly(evidenceHashes)
                },
            AuthoritativeDocuments = Array.AsReadOnly(authoritative),
            DerivedBriefEvidence = Array.AsReadOnly(derived)
        };
    }

    private static long CheckedPayload(long current, byte[]? value)
    {
        if (value is null)
            Reject("transfer_bundle_document_bytes_missing");
        try
        {
            return checked(current + value.Length);
        }
        catch (OverflowException)
        {
            Reject("transfer_bundle_payload_too_large");
            return 0;
        }
    }

    private static string ProducerId(AgentWorkOwnerV1 owner) =>
        $"agent:{CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(owner))}";

    private static string ReceiptSearchText(
        string workId,
        string stage,
        string disposition,
        string receiptId) =>
        string.Join(
            '\n',
            "passive untrusted work transfer receipt",
            workId,
            stage,
            disposition,
            receiptId);

    private static void RequireIdentifier(string? value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > CortexAgentRetentionLimits.MaximumIdentifierCharacters ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or ':' or '-')))
        {
            Reject(code);
        }
    }

    private static void RequireHash(string? value, string code)
    {
        if (value is not { Length: 64 } ||
            value.Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
        {
            Reject(code);
        }
    }

    private static void RequireUtc(DateTimeOffset value, string code)
    {
        if (value.Offset != TimeSpan.Zero)
            Reject(code);
    }

    [DoesNotReturn]
    private static void Reject(string code) =>
        throw new CortexRetentionTransferValidationException(code);

    private sealed record DocumentRevision(string DocumentId, long Revision);

    private sealed record ChainKey(
        string RepositoryProjectId,
        string Kind,
        string DocumentId);

    private sealed record RetentionIdempotencyKey(
        string RepositoryProjectId,
        string Kind,
        string ProducerId,
        string IdempotencyKey);

    private sealed record TypedDocuments(
        IReadOnlyDictionary<DocumentRevision, AgentTaskSnapshotV1> TaskSnapshots,
        IReadOnlyDictionary<DocumentRevision, AgentWorkPacketV1> WorkPackets,
        IReadOnlyDictionary<string, AgentWorkPacketHandoffReceiptV1> Handoffs,
        IReadOnlyDictionary<string, AgentWorkPacketAcceptanceReceiptV1> Acceptances);
}
