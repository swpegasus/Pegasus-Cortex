using Pegasus.Cortex.Contracts.Serialization;
using System.Text.Json;

namespace Pegasus.Cortex.Contracts.Sync;

/// <summary>
/// Canonical integrity, compatibility, and reconciliation boundary for the v1 family.
/// This class performs no filesystem, network, authentication-provider, or persistence I/O.
/// </summary>
public static class CortexSyncContractPolicy
{
    private static readonly IReadOnlySet<string> RequiredFamilyContracts = new HashSet<string>(StringComparer.Ordinal)
    {
        CortexSyncContractIds.OfflineEvent,
        CortexSyncContractIds.OfflineBundle,
        CortexSyncContractIds.SyncReceipt,
        CortexSyncContractIds.SyncCapabilities,
        CortexSyncContractIds.CompatibilityRequest,
        CortexSyncContractIds.CompatibilityDecision
    };

    public static CortexLocalStoreManifestV1 Seal(CortexLocalStoreManifestV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireValidExtensions(value.Extensions);
        var placeholder = Copy(value, CortexSyncContractIds.EmptySha256);
        return Copy(value, CanonicalHash(placeholder));
    }

    public static CortexOfflineEventV1 Seal(CortexOfflineEventV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireValidTypedPayload(value.PayloadBase64Url, value.PayloadSha256, value.PayloadContractId, "event payload");
        RequireValidEventSource(value);
        RequireValidExtensions(value.Extensions);
        var placeholder = Copy(value, CortexSyncContractIds.EmptySha256);
        return Copy(value, CanonicalHash(placeholder));
    }

    public static CortexOfflineBundleV1 Seal(CortexOfflineBundleV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Events.Any(item => !HasValidHash(item)))
            throw new ArgumentException("Every bundled event must carry a valid canonical hash.", nameof(value));
        RequireValidExtensions(value.Extensions);
        var placeholder = Copy(value, CortexSyncContractIds.EmptySha256);
        var bytes = CortexCanonicalJson.Serialize(placeholder);
        if (bytes.Length > CortexSyncContractLimits.MaximumCanonicalBundleBytes)
            throw new ArgumentException("The canonical offline bundle exceeds the v1 byte bound.", nameof(value));
        return Copy(value, CortexCanonicalJson.Sha256(bytes));
    }

    public static CortexSyncCapabilitiesV1 Seal(CortexSyncCapabilitiesV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireValidExtensions(value.Extensions);
        var placeholder = Copy(value, CortexSyncContractIds.EmptySha256);
        return Copy(value, CanonicalHash(placeholder));
    }

    public static CortexSyncRemoteHead Seal(CortexSyncRemoteHead value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var placeholder = Copy(value, CortexSyncContractIds.EmptySha256);
        return Copy(value, CanonicalHash(placeholder));
    }

    public static CortexSyncSubmitRequest Seal(CortexSyncSubmitRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!HasValidHash(value.Bundle))
            throw new ArgumentException("The gateway submit request contains an invalid bundle.", nameof(value));
        var placeholder = Copy(value, CortexSyncContractIds.EmptySha256);
        return Copy(value, CanonicalHash(placeholder));
    }

    public static CortexSyncSubmitResult Seal(CortexSyncSubmitResult value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!HasValidSemanticHash(value.Receipt) || !HasValidHash(value.Head))
            throw new ArgumentException("The gateway submit result contains invalid receipt or head evidence.", nameof(value));
        var placeholder = Copy(value, CortexSyncContractIds.EmptySha256);
        return Copy(value, CanonicalHash(placeholder));
    }

    public static CortexLegacyMigrationManifestV1 Seal(CortexLegacyMigrationManifestV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireValidExtensions(value.Extensions);
        var placeholder = Copy(value, CortexSyncContractIds.EmptySha256);
        return Copy(value, CanonicalHash(placeholder));
    }

    public static CortexLegacyMigrationInventoryV1 Seal(CortexLegacyMigrationInventoryV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireValidExtensions(value.Extensions);
        var placeholder = Copy(value, CortexSyncContractIds.EmptySha256);
        return Copy(value, CanonicalHash(placeholder));
    }

    public static CortexLegacyMigrationCursorV1 Seal(CortexLegacyMigrationCursorV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireValidExtensions(value.Extensions);
        var placeholder = Copy(value, CortexSyncContractIds.EmptySha256);
        return Copy(value, CanonicalHash(placeholder));
    }

    public static CortexLegacyMigrationPageV1 Seal(CortexLegacyMigrationPageV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireValidExtensions(value.Extensions);
        if (!ValidLegacyMigrationPageLinks(value))
            throw new ArgumentException("The migration page does not extend its exact hash-bound cursor.", nameof(value));
        var placeholder = Copy(value, CortexSyncContractIds.EmptySha256);
        var bytes = CortexCanonicalJson.Serialize(placeholder);
        if (bytes.Length > CortexSyncContractLimits.MaximumCanonicalBundleBytes)
            throw new ArgumentException("The canonical migration page exceeds the v1 byte bound.", nameof(value));
        return Copy(value, CortexCanonicalJson.Sha256(bytes));
    }

    public static bool HasValidHash(CortexLocalStoreManifestV1 value) =>
        value is not null && ValidExtensions(value.Extensions) &&
        string.Equals(value.ManifestHash, CanonicalHash(Copy(value, CortexSyncContractIds.EmptySha256)), StringComparison.Ordinal);

    public static bool HasValidHash(CortexOfflineEventV1 value) =>
        value is not null && ValidTypedPayload(value.PayloadBase64Url, value.PayloadSha256, value.PayloadContractId) &&
        ValidEventSource(value) &&
        ValidExtensions(value.Extensions) &&
        string.Equals(value.EventHash, CanonicalHash(Copy(value, CortexSyncContractIds.EmptySha256)), StringComparison.Ordinal);

    public static bool HasValidHash(CortexOfflineBundleV1 value)
    {
        if (value is null || value.Events.Any(item => !HasValidHash(item)) || !ValidExtensions(value.Extensions))
            return false;
        var placeholder = Copy(value, CortexSyncContractIds.EmptySha256);
        var bytes = CortexCanonicalJson.Serialize(placeholder);
        return bytes.Length <= CortexSyncContractLimits.MaximumCanonicalBundleBytes &&
            string.Equals(value.BundleHash, CortexCanonicalJson.Sha256(bytes), StringComparison.Ordinal);
    }

    public static bool HasValidHash(CortexSyncCapabilitiesV1 value) =>
        value is not null && ValidExtensions(value.Extensions) &&
        string.Equals(value.CapabilitiesHash, CanonicalHash(Copy(value, CortexSyncContractIds.EmptySha256)), StringComparison.Ordinal);

    public static bool HasValidHash(CortexSyncRemoteHead value) =>
        value is not null &&
        string.Equals(value.HeadHash, CanonicalHash(Copy(value, CortexSyncContractIds.EmptySha256)), StringComparison.Ordinal);

    public static bool HasValidHash(CortexSyncSubmitRequest value) =>
        value is not null && HasValidHash(value.Bundle) &&
        string.Equals(value.RequestHash, CanonicalHash(Copy(value, CortexSyncContractIds.EmptySha256)), StringComparison.Ordinal);

    public static bool HasValidHash(CortexSyncSubmitResult value) =>
        value is not null && HasValidSemanticHash(value.Receipt) && HasValidHash(value.Head) &&
        string.Equals(value.ResultHash, CanonicalHash(Copy(value, CortexSyncContractIds.EmptySha256)), StringComparison.Ordinal);

    public static bool HasValidHash(CortexLegacyMigrationManifestV1 value) =>
        value is not null && ValidExtensions(value.Extensions) &&
        string.Equals(value.ManifestHash, CanonicalHash(Copy(value, CortexSyncContractIds.EmptySha256)), StringComparison.Ordinal);

    public static bool HasValidHash(CortexLegacyMigrationInventoryV1 value) =>
        value is not null && ValidExtensions(value.Extensions) &&
        string.Equals(value.InventoryHash, CanonicalHash(Copy(value, CortexSyncContractIds.EmptySha256)), StringComparison.Ordinal);

    public static bool HasValidHash(CortexLegacyMigrationCursorV1 value) =>
        value is not null && ValidExtensions(value.Extensions) &&
        string.Equals(value.CursorHash, CanonicalHash(Copy(value, CortexSyncContractIds.EmptySha256)), StringComparison.Ordinal);

    public static bool HasValidHash(CortexLegacyMigrationPageV1 value)
    {
        if (value is null || !ValidExtensions(value.Extensions) || !ValidLegacyMigrationPageLinks(value))
            return false;
        var placeholder = Copy(value, CortexSyncContractIds.EmptySha256);
        var bytes = CortexCanonicalJson.Serialize(placeholder);
        return bytes.Length <= CortexSyncContractLimits.MaximumCanonicalBundleBytes &&
            string.Equals(value.PageHash, CortexCanonicalJson.Sha256(bytes), StringComparison.Ordinal);
    }

    /// <summary>
    /// Hashes only the semantic receipt body. Authentication and ReceiptHash are deliberately
    /// excluded so a proof can sign this value without creating a proof/hash cycle.
    /// </summary>
    public static string ComputeReceiptSemanticHash(CortexSyncReceiptV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        RequireValidExtensions(receipt.Extensions);
        return CanonicalHash(new ReceiptSemanticBody(
            receipt.SchemaVersion,
            receipt.ContractId,
            receipt.Canonicalization,
            receipt.ReceiptId,
            receipt.ServerInstanceId,
            receipt.StoreId,
            receipt.StoreEpoch,
            receipt.ProjectId,
            receipt.BundleId,
            receipt.BundleHash,
            receipt.ReceivedAt,
            receipt.Durability,
            receipt.Outcomes,
            receipt.Reconciliation,
            receipt.Extensions));
    }

    public static bool HasValidSemanticHash(CortexSyncReceiptV1 receipt)
    {
        if (receipt is null ||
            !ValidEncodedHash(receipt.Authentication.ProofBase64Url, receipt.Authentication.ProofSha256) ||
            !string.Equals(receipt.Authentication.CoveredReceiptHash, receipt.ReceiptHash, StringComparison.Ordinal))
            return false;
        try
        {
            return string.Equals(receipt.ReceiptHash, ComputeReceiptSemanticHash(receipt), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Stable exact-set negotiation. Unknown required base contracts, capabilities, or
    /// extensions produce bounded fail-closed issues; optional extension payloads never affect
    /// the decision.
    /// </summary>
    public static CortexSyncCompatibilityDecisionV1 EvaluateCompatibility(
        CortexSyncCapabilitiesV1 server,
        CortexSyncCompatibilityRequestV1 client)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(client);
        var issues = new List<CortexSyncCompatibilityIssueV1>();

        if (!HasValidHash(server))
            AddIssue(issues, "sync_capabilities_integrity_invalid", "The server capability document failed canonical integrity validation.");
        if (!ValidExtensions(client.Extensions))
            AddIssue(issues, "sync_client_extension_integrity_invalid", "A client extension payload hash does not match its decoded bytes.");
        if (RequiredFamilyContracts.Except(server.SupportedContractIds, StringComparer.Ordinal).Any())
            AddIssue(issues, "sync_server_family_incomplete", "The server does not advertise the complete stable v1 synchronization family.");
        if (RequiredFamilyContracts.Except(client.SupportedContractIds, StringComparer.Ordinal).Any())
            AddIssue(issues, "sync_client_family_incomplete", "The client does not advertise the complete stable v1 synchronization family.");
        if (client.RequiredServerContractIds.Except(server.SupportedContractIds, StringComparer.Ordinal).Any())
            AddIssue(issues, "sync_required_contract_unknown", "A client-required server contract is unsupported.");
        if (server.RequiredClientCapabilityIds.Except(client.SupportedCapabilityIds, StringComparer.Ordinal).Any())
            AddIssue(issues, "sync_required_client_capability_unknown", "A server-required client capability is unknown.");
        if (client.RequiredServerCapabilityIds.Except(server.SupportedCapabilityIds, StringComparer.Ordinal).Any())
            AddIssue(issues, "sync_required_server_capability_unknown", "A client-required server capability is unknown.");
        if (server.RequiredClientExtensionContractIds.Except(client.SupportedExtensionContractIds, StringComparer.Ordinal).Any())
            AddIssue(issues, "sync_required_client_extension_unknown", "A server-required client extension is unknown.");
        if (client.RequiredServerExtensionContractIds.Except(server.SupportedExtensionContractIds, StringComparer.Ordinal).Any())
            AddIssue(issues, "sync_required_server_extension_unknown", "A client-required server extension is unknown.");
        if (client.Extensions.Where(item => item.Required)
            .Any(item => !server.SupportedExtensionContractIds.Contains(item.ContractId, StringComparer.Ordinal)))
            AddIssue(issues, "sync_required_extension_payload_unknown", "A required client extension payload is unsupported by the server.");
        if (server.Extensions.Where(item => item.Required)
            .Any(item => !client.SupportedExtensionContractIds.Contains(item.ContractId, StringComparer.Ordinal)))
            AddIssue(issues, "sync_required_server_extension_payload_unknown", "A required server extension payload is unsupported by the client.");

        var payloads = client.OfferedPayloadContractIds
            .Intersect(server.AcceptedPayloadContractIds, StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var protectionSuite = client.SupportedProtectionSuiteIds
            .Intersect(server.SupportedProtectionSuiteIds, StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault();
        var receiptProof = client.SupportedReceiptProofContractIds
            .Intersect(server.SupportedReceiptProofContractIds, StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault();
        if (payloads.Length == 0)
            AddIssue(issues, "sync_payload_contract_unavailable", "No mutually supported payload contract is available.");
        if (protectionSuite is null)
            AddIssue(issues, "sync_protection_suite_unavailable", "No mutually supported payload-protection suite is available.");
        if (receiptProof is null)
            AddIssue(issues, "sync_receipt_proof_unavailable", "No mutually supported receipt-proof contract is available.");

        if (issues.Count != 0)
        {
            return new CortexSyncCompatibilityDecisionV1(
                CortexSyncContractIds.SchemaVersion,
                CortexSyncContractIds.CompatibilityDecision,
                CortexSyncContractIds.Canonicalization,
                client.RequestId,
                false,
                [],
                null,
                null,
                null,
                issues);
        }

        return new CortexSyncCompatibilityDecisionV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.CompatibilityDecision,
            CortexSyncContractIds.Canonicalization,
            client.RequestId,
            true,
            payloads,
            protectionSuite,
            receiptProof,
            EffectiveLimits(server.Limits, client.Limits),
            []);
    }

    /// <summary>
    /// Validates an externally authenticated receipt against the exact submitted bundle.
    /// DurablyReconciled is evidence for a later approval gate, never cleanup authority itself.
    /// </summary>
    public static CortexSyncReceiptValidationV1 ValidateReceipt(
        CortexSyncReceiptV1 receipt,
        CortexOfflineBundleV1 bundle,
        bool authenticationProofVerified)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(bundle);
        var issues = new List<CortexSyncCompatibilityIssueV1>();
        if (!authenticationProofVerified)
            AddIssue(issues, "sync_receipt_authentication_invalid", "The trusted receipt-proof adapter did not authenticate this receipt.");
        if (!HasValidSemanticHash(receipt))
            AddIssue(issues, "sync_receipt_integrity_invalid", "The receipt semantic hash or proof payload hash is invalid.");
        if (!HasValidHash(bundle))
            AddIssue(issues, "sync_bundle_integrity_invalid", "The source bundle failed canonical integrity validation.");
        if (!string.Equals(receipt.StoreId, bundle.StoreId, StringComparison.Ordinal) ||
            !string.Equals(receipt.StoreEpoch, bundle.StoreEpoch, StringComparison.Ordinal) ||
            !string.Equals(receipt.ProjectId, bundle.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(receipt.BundleId, bundle.BundleId, StringComparison.Ordinal) ||
            !string.Equals(receipt.BundleHash, bundle.BundleHash, StringComparison.Ordinal))
        {
            AddIssue(issues, "sync_receipt_bundle_mismatch", "The receipt does not identify the exact submitted store epoch and bundle.");
        }
        if (receipt.Outcomes.Count != bundle.Events.Count ||
            receipt.Outcomes.Zip(bundle.Events, (outcome, item) =>
                outcome.Sequence == item.Sequence &&
                string.Equals(outcome.EventId, item.EventId, StringComparison.Ordinal) &&
                string.Equals(outcome.EventHash, item.EventHash, StringComparison.Ordinal)).Any(matches => !matches))
        {
            AddIssue(issues, "sync_receipt_outcome_mismatch", "The receipt does not account for every source event in order.");
        }
        if (receipt.Durability.CommittedAt < receipt.ReceivedAt)
            AddIssue(issues, "sync_receipt_durability_time_invalid", "The durable commit timestamp predates server receipt of the bundle.");

        var accepted = issues.Count == 0;
        var durablyReconciled = accepted && receipt.Reconciliation.State == "reconciled" &&
            receipt.Outcomes.All(item => item.Durable &&
                item.Disposition is CortexSyncDispositionIds.Applied or CortexSyncDispositionIds.Duplicate);
        return new CortexSyncReceiptValidationV1(accepted, durablyReconciled, issues);
    }

    /// <summary>
    /// Validates a newly sealed migration snapshot against its immutable predecessor. Source
    /// provenance and prior evidence are append-only, lifecycle cannot regress, and a terminal
    /// state cannot be silently rewritten.
    /// </summary>
    public static CortexSyncMigrationProgressValidationV1 ValidateMigrationProgress(
        CortexLegacyMigrationManifestV1 previous,
        CortexLegacyMigrationManifestV1 current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var issues = new List<CortexSyncCompatibilityIssueV1>();
        if (!HasValidHash(previous) || !HasValidHash(current))
            AddIssue(issues, "sync_migration_integrity_invalid", "A migration progress snapshot failed canonical integrity validation.");
        if (current.ManifestRevision != previous.ManifestRevision + 1 ||
            !string.Equals(current.PreviousManifestHash, previous.ManifestHash, StringComparison.Ordinal))
            AddIssue(issues, "sync_migration_chain_invalid", "The migration snapshot does not extend the exact preceding manifest hash.");
        if (!SameMigrationSource(previous, current) || current.CapturedAt < previous.CapturedAt)
            AddIssue(issues, "sync_migration_source_changed", "Migration progress changed immutable source provenance or moved capture time backwards.");

        var comparer = previous.SourcePathCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        if (previous.SourcePathCaseSensitive != current.SourcePathCaseSensitive ||
            !string.Equals(previous.SourcePathStyle, current.SourcePathStyle, StringComparison.Ordinal) ||
            previous.Files.Count != current.Files.Count)
        {
            AddIssue(issues, "sync_migration_file_set_changed", "Migration progress changed path semantics or the inventoried source-file count.");
        }
        else
        {
            var currentByPath = current.Files.ToDictionary(item => item.SourceRelativePath, comparer);
            foreach (var priorFile in previous.Files)
            {
                if (!currentByPath.TryGetValue(priorFile.SourceRelativePath, out var nextFile) ||
                    !SameFileSource(priorFile, nextFile) ||
                    LifecycleRank(nextFile.LifecycleState) < LifecycleRank(priorFile.LifecycleState) ||
                    IsTerminal(priorFile.LifecycleState) && !string.Equals(priorFile.LifecycleState, nextFile.LifecycleState, StringComparison.Ordinal) ||
                    priorFile.Disposition != CortexLegacyFileDispositionIds.DiscoveredUnmapped &&
                        !string.Equals(priorFile.Disposition, nextFile.Disposition, StringComparison.Ordinal) ||
                    priorFile.MappedIds.Except(nextFile.MappedIds, StringComparer.Ordinal).Any() ||
                    priorFile.MappedPayloadContractIds.Except(nextFile.MappedPayloadContractIds, StringComparer.Ordinal).Any() ||
                    priorFile.EvidenceIds.Except(nextFile.EvidenceIds, StringComparer.Ordinal).Any() ||
                    priorFile.ReceiptIds.Except(nextFile.ReceiptIds, StringComparer.Ordinal).Any())
                {
                    AddIssue(issues, "sync_migration_file_progress_invalid", "A file changed provenance, regressed, or removed prior mapping evidence.");
                    break;
                }
            }
        }

        return new CortexSyncMigrationProgressValidationV1(issues.Count == 0, issues);
    }

    public static CortexSyncBundleContinuationValidationV1 ValidateBundleContinuation(
        CortexOfflineBundleV1 previous,
        CortexOfflineBundleV1 current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var issues = new List<CortexSyncCompatibilityIssueV1>();
        if (!HasValidHash(previous) || !HasValidHash(current))
            AddIssue(issues, "sync_bundle_chain_integrity_invalid", "A bundle in the continuation chain failed canonical integrity validation.");
        if (!string.Equals(previous.StoreId, current.StoreId, StringComparison.Ordinal) ||
            !string.Equals(previous.StoreEpoch, current.StoreEpoch, StringComparison.Ordinal) ||
            !string.Equals(previous.ProjectId, current.ProjectId, StringComparison.Ordinal))
            AddIssue(issues, "sync_bundle_chain_scope_changed", "The bundle continuation changed store epoch or project scope.");
        if (previous.LastSequence == CortexSyncContractLimits.MaximumSequence ||
            current.FirstSequence != previous.LastSequence + 1 ||
            !string.Equals(current.PreviousBundleHash, previous.BundleHash, StringComparison.Ordinal) ||
            !string.Equals(current.PriorEventHash, previous.Events[^1].EventHash, StringComparison.Ordinal))
            AddIssue(issues, "sync_bundle_chain_link_invalid", "The next bundle does not extend the exact prior bundle and event hashes.");
        return new CortexSyncBundleContinuationValidationV1(issues.Count == 0, issues);
    }

    public static CortexSyncLegacyMigrationPageValidationV1 ValidateLegacyMigrationPage(
        CortexLegacyMigrationPageV1 page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var issues = new List<CortexSyncCompatibilityIssueV1>();
        if (!HasValidHash(page))
            AddIssue(issues, "sync_migration_page_integrity_invalid", "The migration page or one of its hash-bound members failed canonical integrity validation.");
        if (!ValidLegacyMigrationPageLinks(page))
            AddIssue(issues, "sync_migration_page_cursor_invalid", "The migration page does not advance the exact source, scope, sequence, and bundle cursor.");
        return new CortexSyncLegacyMigrationPageValidationV1(issues.Count == 0, issues);
    }

    /// <summary>
    /// Identifies the only migration position that may be accepted without a prior
    /// durably reconciled migration-page receipt. Sequence and chain hashes are still
    /// checked against the repository's current server head before the bundle is written.
    /// </summary>
    public static bool IsCanonicalInitialLegacyMigrationCursor(
        CortexLegacyMigrationCursorV1 cursor) =>
        cursor is not null &&
        HasValidHash(cursor) &&
        cursor.PageNumber == 1 &&
        cursor.NextFileIndex == 0 &&
        cursor.NextChunkIndex == 0 &&
        !cursor.Completed;

    private static CortexSyncLimitsV1 EffectiveLimits(CortexSyncLimitsV1 left, CortexSyncLimitsV1 right) => new(
        Math.Min(left.MaximumEventPayloadBytes, right.MaximumEventPayloadBytes),
        Math.Min(left.MaximumEventsPerBundle, right.MaximumEventsPerBundle),
        Math.Min(left.MaximumCanonicalBundleBytes, right.MaximumCanonicalBundleBytes),
        Math.Min(left.MaximumExtensions, right.MaximumExtensions),
        Math.Min(left.MaximumExtensionPayloadBytes, right.MaximumExtensionPayloadBytes),
        Math.Min(left.MaximumReceiptOutcomes, right.MaximumReceiptOutcomes),
        Math.Min(left.MaximumMigrationFiles, right.MaximumMigrationFiles),
        Math.Min(left.MaximumJsonDepth, right.MaximumJsonDepth));

    private static bool SameMigrationSource(CortexLegacyMigrationManifestV1 left, CortexLegacyMigrationManifestV1 right) =>
        string.Equals(left.MigrationId, right.MigrationId, StringComparison.Ordinal) &&
        string.Equals(left.StoreId, right.StoreId, StringComparison.Ordinal) &&
        string.Equals(left.StoreEpoch, right.StoreEpoch, StringComparison.Ordinal) &&
        string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal) &&
        string.Equals(left.SourceRootId, right.SourceRootId, StringComparison.Ordinal) &&
        string.Equals(left.SourceSchemaVersion, right.SourceSchemaVersion, StringComparison.Ordinal) &&
        string.Equals(left.SourceProjectRootExact, right.SourceProjectRootExact, StringComparison.Ordinal) &&
        string.Equals(left.SourceMetaPathExact, right.SourceMetaPathExact, StringComparison.Ordinal) &&
        left.SourceMetaBytes == right.SourceMetaBytes &&
        string.Equals(left.SourceMetaSha256, right.SourceMetaSha256, StringComparison.Ordinal) &&
        left.CopyMode == right.CopyMode && left.SourceMutationAllowed == right.SourceMutationAllowed &&
        left.CleanupState == right.CleanupState && left.CutoverState == right.CutoverState &&
        left.ReconciliationRequired == right.ReconciliationRequired;

    private static bool SameFileSource(CortexLegacyMigrationFileV1 left, CortexLegacyMigrationFileV1 right) =>
        string.Equals(left.Role, right.Role, StringComparison.Ordinal) &&
        string.Equals(left.ExactSourcePath, right.ExactSourcePath, StringComparison.Ordinal) &&
        string.Equals(left.SourceRelativePath, right.SourceRelativePath, StringComparison.Ordinal) &&
        left.SourceBytes == right.SourceBytes && string.Equals(left.SourceSha256, right.SourceSha256, StringComparison.Ordinal);

    private static bool ValidLegacyMigrationPageLinks(CortexLegacyMigrationPageV1 page)
    {
        var current = page.CurrentCursor;
        var next = page.NextCursor;
        var bundle = page.Bundle;
        var inventory = page.SourceInventory;
        if (!HasValidHash(inventory) || !HasValidHash(current) || !HasValidHash(next) || !HasValidHash(bundle) ||
            current.Completed ||
            bundle.Events.Count is < 1 or > CortexSyncContractLimits.MaximumLegacyMigrationPageEvents ||
            bundle.LastSequence == CortexSyncContractLimits.MaximumSequence ||
            current.NextFileIndex >= inventory.Files.Count ||
            current.NextChunkIndex >= inventory.Files[current.NextFileIndex].ChunkCount)
        {
            return false;
        }

        var sameCursorScope =
            string.Equals(current.MigrationId, next.MigrationId, StringComparison.Ordinal) &&
            string.Equals(current.SourceManifestHash, next.SourceManifestHash, StringComparison.Ordinal) &&
            string.Equals(current.SourceInventoryHash, next.SourceInventoryHash, StringComparison.Ordinal) &&
            current.SourceFileCount == next.SourceFileCount &&
            string.Equals(current.StoreId, next.StoreId, StringComparison.Ordinal) &&
            string.Equals(current.StoreEpoch, next.StoreEpoch, StringComparison.Ordinal) &&
            string.Equals(current.ProjectId, next.ProjectId, StringComparison.Ordinal) &&
            string.Equals(current.SourceRootId, next.SourceRootId, StringComparison.Ordinal);
        var inventoryScope =
            string.Equals(inventory.MigrationId, current.MigrationId, StringComparison.Ordinal) &&
            string.Equals(inventory.SourceManifestHash, current.SourceManifestHash, StringComparison.Ordinal) &&
            string.Equals(inventory.InventoryHash, current.SourceInventoryHash, StringComparison.Ordinal) &&
            inventory.Files.Count == current.SourceFileCount &&
            string.Equals(inventory.StoreId, current.StoreId, StringComparison.Ordinal) &&
            string.Equals(inventory.StoreEpoch, current.StoreEpoch, StringComparison.Ordinal) &&
            string.Equals(inventory.ProjectId, current.ProjectId, StringComparison.Ordinal) &&
            string.Equals(inventory.SourceRootId, current.SourceRootId, StringComparison.Ordinal);
        var bundleScope =
            string.Equals(bundle.StoreId, current.StoreId, StringComparison.Ordinal) &&
            string.Equals(bundle.StoreEpoch, current.StoreEpoch, StringComparison.Ordinal) &&
            string.Equals(bundle.ProjectId, current.ProjectId, StringComparison.Ordinal);
        if (!ValidLegacyPageEvents(
                bundle,
                inventory,
                current,
                out var expectedNextFileIndex,
                out var expectedNextChunkIndex))
        {
            return false;
        }
        var completed = expectedNextFileIndex == inventory.Files.Count && expectedNextChunkIndex == 0;
        return sameCursorScope && inventoryScope && bundleScope &&
            next.PageNumber == current.PageNumber + 1 &&
            next.NextFileIndex == expectedNextFileIndex &&
            next.NextChunkIndex == expectedNextChunkIndex &&
            next.Completed == completed &&
            bundle.FirstSequence == current.NextSequence &&
            string.Equals(bundle.PreviousBundleHash, current.PreviousBundleHash, StringComparison.Ordinal) &&
            string.Equals(bundle.PriorEventHash, current.PriorEventHash, StringComparison.Ordinal) &&
            next.NextSequence == bundle.LastSequence + 1 &&
            string.Equals(next.PreviousBundleHash, bundle.BundleHash, StringComparison.Ordinal) &&
            string.Equals(next.PriorEventHash, bundle.Events[^1].EventHash, StringComparison.Ordinal);
    }

    private static bool ValidLegacyPageEvents(
        CortexOfflineBundleV1 bundle,
        CortexLegacyMigrationInventoryV1 inventory,
        CortexLegacyMigrationCursorV1 current,
        out int nextFileIndex,
        out int nextChunkIndex)
    {
        nextFileIndex = current.NextFileIndex;
        nextChunkIndex = current.NextChunkIndex;
        try
        {
            var expectedSequence = current.NextSequence;
            var expectedPreviousEventHash = current.PriorEventHash;
            foreach (var item in bundle.Events)
            {
                if (nextFileIndex >= inventory.Files.Count)
                    return false;
                var expectedFile = inventory.Files[nextFileIndex];
                var expectedOffset = checked((long)nextChunkIndex * CortexLegacyFileCopyPayloadV1.MaximumChunkBytes);
                var expectedBytes = checked((int)Math.Min(
                    CortexLegacyFileCopyPayloadV1.MaximumChunkBytes,
                    expectedFile.SourceBytes - expectedOffset));
                if (!string.Equals(item.PayloadContractId, CortexSyncContractIds.LegacyMigrationRecord, StringComparison.Ordinal) ||
                    !string.Equals(item.Source.SourceKind, "legacy_file_copy", StringComparison.Ordinal) ||
                    item.Sequence != expectedSequence ||
                    !string.Equals(item.PreviousEventHash, expectedPreviousEventHash, StringComparison.Ordinal) ||
                    !string.Equals(item.Source.SourceRootId, inventory.SourceRootId, StringComparison.Ordinal) ||
                    !string.Equals(item.Source.SourceRelativePath, expectedFile.SourceRelativePath, StringComparison.Ordinal) ||
                    item.Source.SourceBytes != expectedFile.SourceBytes ||
                    !string.Equals(item.Source.SourceSha256, expectedFile.SourceSha256, StringComparison.Ordinal) ||
                    item.Source.CapturedAt != inventory.CapturedAt)
                {
                    return false;
                }
                var payload = CortexCanonicalJson.Deserialize<CortexLegacyMigrationRecordV1>(
                    CortexSyncContractShapes.DecodeBase64Url(item.PayloadBase64Url));
                if (payload.Tombstone ||
                    !string.Equals(payload.SourceRootId, inventory.SourceRootId, StringComparison.Ordinal) ||
                    !string.Equals(payload.SourceManifestHash, inventory.SourceManifestHash, StringComparison.Ordinal) ||
                    !string.Equals(payload.SourceInventoryHash, inventory.InventoryHash, StringComparison.Ordinal) ||
                    payload.SourceFileIndex != nextFileIndex ||
                    !string.Equals(payload.NamespaceHint, item.NamespaceHint, StringComparison.Ordinal) ||
                    !string.Equals(payload.RecordKind, expectedFile.Role, StringComparison.Ordinal) ||
                    !string.Equals(payload.RecordId, expectedFile.RecordId, StringComparison.Ordinal) ||
                    !string.Equals(payload.RevisionId, expectedFile.RevisionId, StringComparison.Ordinal) ||
                    !string.Equals(payload.SourceRelativePath, expectedFile.SourceRelativePath, StringComparison.Ordinal) ||
                    payload.SourceBytes != expectedFile.SourceBytes ||
                    !string.Equals(payload.SourceSha256, expectedFile.SourceSha256, StringComparison.Ordinal) ||
                    payload.ChunkCount != expectedFile.ChunkCount ||
                    payload.ChunkIndex != nextChunkIndex + 1 ||
                    payload.ChunkOffset != expectedOffset ||
                    payload.ContentBytes != expectedBytes)
                {
                    return false;
                }

                expectedPreviousEventHash = item.EventHash;
                expectedSequence++;
                nextChunkIndex++;
                if (nextChunkIndex == expectedFile.ChunkCount)
                {
                    nextFileIndex++;
                    nextChunkIndex = 0;
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is
            JsonException or
            FormatException or
            ArgumentException or
            InvalidOperationException or
            OverflowException)
        {
            return false;
        }
    }

    private static int LifecycleRank(string value) => value switch
    {
        CortexLegacyFileLifecycleIds.Discovered => 0,
        CortexLegacyFileLifecycleIds.Copied => 1,
        CortexLegacyFileLifecycleIds.Validated => 2,
        CortexLegacyFileLifecycleIds.Uploaded => 3,
        CortexLegacyFileLifecycleIds.ReadBack => 4,
        CortexLegacyFileLifecycleIds.Reconciled => 5,
        CortexLegacyFileLifecycleIds.Conflict or CortexLegacyFileLifecycleIds.Rejected => 100,
        _ => int.MaxValue
    };

    private static bool IsTerminal(string value) =>
        value is CortexLegacyFileLifecycleIds.Reconciled or CortexLegacyFileLifecycleIds.Conflict or CortexLegacyFileLifecycleIds.Rejected;

    private static string CanonicalHash<T>(T value) => CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(value));

    private static void AddIssue(List<CortexSyncCompatibilityIssueV1> issues, string code, string message)
    {
        if (issues.Count == CortexSyncContractLimits.MaximumCompatibilityIssues)
            return;
        issues.Add(new CortexSyncCompatibilityIssueV1(code, message));
    }

    private static void RequireValidExtensions(IReadOnlyList<CortexSyncExtensionV1> extensions)
    {
        if (!ValidExtensions(extensions))
            throw new ArgumentException("An extension payload hash is invalid.", nameof(extensions));
    }

    private static bool ValidExtensions(IReadOnlyList<CortexSyncExtensionV1> extensions) =>
        extensions.All(item => ValidTypedPayload(item.PayloadBase64Url, item.PayloadSha256, item.ContractId));

    private static void RequireValidTypedPayload(
        string base64Url,
        string expectedHash,
        string expectedContractId,
        string label)
    {
        if (!ValidTypedPayload(base64Url, expectedHash, expectedContractId))
            throw new ArgumentException($"The {label} is not canonical typed JSON or its SHA-256 is invalid.");
    }

    private static void RequireValidEventSource(CortexOfflineEventV1 value)
    {
        if (!ValidEventSource(value))
            throw new ArgumentException("Client-operation provenance does not match the exact event payload bytes.", nameof(value));
    }

    private static bool ValidEventSource(CortexOfflineEventV1 value) =>
        value.Source.SourceKind != "client_operation" ||
        value.Source.SourceBytes == value.PayloadBytes &&
        string.Equals(value.Source.SourceSha256, value.PayloadSha256, StringComparison.Ordinal);

    private static bool ValidTypedPayload(string base64Url, string expectedHash, string expectedContractId)
    {
        try
        {
            var bytes = CortexSyncContractShapes.DecodeBase64Url(base64Url);
            if (!string.Equals(CortexCanonicalJson.Sha256(bytes), expectedHash, StringComparison.Ordinal))
                return false;
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = CortexSyncContractLimits.MaximumJsonDepth
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("contractId", out var contractId) ||
                contractId.ValueKind != JsonValueKind.String ||
                !string.Equals(contractId.GetString(), expectedContractId, StringComparison.Ordinal) ||
                !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
                schemaVersion.ValueKind != JsonValueKind.Number || !schemaVersion.TryGetInt32(out var version) || version < 1)
            {
                return false;
            }
            return CortexCanonicalJson.Serialize(root).AsSpan().SequenceEqual(bytes);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void RequireValidEncodedHash(string base64Url, string expectedHash, string label)
    {
        if (!ValidEncodedHash(base64Url, expectedHash))
            throw new ArgumentException($"The {label} SHA-256 does not match its decoded bytes.");
    }

    private static bool ValidEncodedHash(string base64Url, string expectedHash)
    {
        try
        {
            return string.Equals(
                CortexCanonicalJson.Sha256(CortexSyncContractShapes.DecodeBase64Url(base64Url)),
                expectedHash,
                StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static CortexLocalStoreManifestV1 Copy(CortexLocalStoreManifestV1 value, string hash) => new(
        value.SchemaVersion, value.ContractId, value.Canonicalization, value.StoreId, value.StoreEpoch,
        value.ProjectId, value.ProjectDisplayName, value.CreatedAt, value.StateRootRelativePath,
        value.BundleDirectory, value.ReceiptDirectory, value.QuarantineDirectory,
        value.PayloadProtectionRequired, value.SourceKind, value.SourceMigrationId,
        value.ContentTreatment, value.Executable, value.Extensions, hash);

    private static CortexOfflineEventV1 Copy(CortexOfflineEventV1 value, string hash) => new(
        value.SchemaVersion, value.ContractId, value.Canonicalization, value.StoreId, value.StoreEpoch,
        value.ProjectId, value.Sequence, value.EventId, value.PreviousEventHash, value.OccurredAt,
        value.ProducerInstanceId, value.OperationId, value.TenantIdHint, value.NamespaceHint,
        value.ObservedPolicyRevision, value.PayloadContractId, value.PayloadBase64Url, value.PayloadBytes,
        value.PayloadSha256, value.Source, value.ContentTreatment, value.Executable, value.Extensions, hash);

    private static CortexOfflineBundleV1 Copy(CortexOfflineBundleV1 value, string hash) => new(
        value.SchemaVersion, value.ContractId, value.Canonicalization, value.BundleId, value.StoreId,
        value.StoreEpoch, value.ProjectId, value.BaseServerRevision, value.PreviousBundleHash,
        value.PriorEventHash, value.FirstSequence, value.LastSequence, value.CreatedAt,
        value.Events, value.Extensions, hash);

    private static CortexSyncCapabilitiesV1 Copy(CortexSyncCapabilitiesV1 value, string hash) => new(
        value.SchemaVersion, value.ContractId, value.Canonicalization, value.ServiceVersion,
        value.SupportedContractIds, value.AcceptedPayloadContractIds, value.SupportedCapabilityIds,
        value.RequiredClientCapabilityIds, value.SupportedExtensionContractIds,
        value.RequiredClientExtensionContractIds, value.SupportedProtectionSuiteIds,
        value.SupportedReceiptProofContractIds, value.Limits, value.Extensions, hash);

    private static CortexSyncRemoteHead Copy(CortexSyncRemoteHead value, string hash) => new(
        value.SchemaVersion,
        value.ContractId,
        value.Canonicalization,
        value.Exists,
        value.ProjectLinkId,
        value.StoreId,
        value.StoreEpoch,
        value.ProjectId,
        value.HighWaterSequence,
        value.LastEventId,
        value.LastEventHash,
        value.LastBundleId,
        value.LastBundleHash,
        value.AuthorityHash,
        value.RetainedPayloadHash,
        value.RetainedSnapshotRevision,
        value.RetainedSnapshotHash,
        value.ContextSetId,
        value.ServerRevision,
        value.ObservedAt,
        hash);

    private static CortexSyncSubmitRequest Copy(CortexSyncSubmitRequest value, string hash) => new(
        value.SchemaVersion,
        value.ContractId,
        value.Canonicalization,
        value.ProjectLinkId,
        value.Bundle,
        hash);

    private static CortexSyncSubmitResult Copy(CortexSyncSubmitResult value, string hash) => new(
        value.SchemaVersion,
        value.ContractId,
        value.Canonicalization,
        value.Receipt,
        value.Head,
        hash);

    private static CortexLegacyMigrationManifestV1 Copy(CortexLegacyMigrationManifestV1 value, string hash) => new(
        value.SchemaVersion, value.ContractId, value.Canonicalization, value.MigrationId, value.StoreId,
        value.StoreEpoch, value.ProjectId, value.SourceRootId, value.SourceSchemaVersion,
        value.SourcePathStyle, value.SourcePathCaseSensitive, value.SourceProjectRootExact,
        value.SourceMetaPathExact, value.SourceMetaBytes, value.SourceMetaSha256, value.CapturedAt,
        value.ManifestRevision, value.PreviousManifestHash,
        value.Files, value.CopyMode, value.SourceMutationAllowed, value.CleanupState, value.CutoverState,
        value.ReconciliationRequired, value.Extensions, hash);

    private static CortexLegacyMigrationInventoryV1 Copy(CortexLegacyMigrationInventoryV1 value, string hash) => new(
        value.SchemaVersion, value.ContractId, value.Canonicalization, value.MigrationId,
        value.SourceManifestHash, value.StoreId, value.StoreEpoch, value.ProjectId, value.SourceRootId,
        value.CapturedAt, value.SourcePathCaseSensitive, value.Files, value.TotalSourceBytes,
        value.ContentTreatment, value.Executable, value.Extensions, hash);

    private static CortexLegacyMigrationCursorV1 Copy(CortexLegacyMigrationCursorV1 value, string hash) => new(
        value.SchemaVersion, value.ContractId, value.Canonicalization, value.MigrationId,
        value.SourceManifestHash, value.SourceInventoryHash, value.SourceFileCount,
        value.StoreId, value.StoreEpoch, value.ProjectId, value.SourceRootId,
        value.PageNumber, value.NextFileIndex, value.NextChunkIndex, value.NextSequence,
        value.PreviousBundleHash, value.PriorEventHash, value.Completed,
        value.ContentTreatment, value.Executable, value.Extensions, hash);

    private static CortexLegacyMigrationPageV1 Copy(CortexLegacyMigrationPageV1 value, string hash) => new(
        value.SchemaVersion, value.ContractId, value.Canonicalization, value.PageId,
        value.SourceInventory, value.CurrentCursor, value.Bundle, value.NextCursor, value.AcknowledgmentRequired,
        value.SourceMutationAllowed, value.ContentTreatment, value.Executable, value.Extensions, hash);

    private sealed record ReceiptSemanticBody(
        int SchemaVersion,
        string ContractId,
        string Canonicalization,
        string ReceiptId,
        string ServerInstanceId,
        string StoreId,
        string StoreEpoch,
        string ProjectId,
        string BundleId,
        string BundleHash,
        DateTimeOffset ReceivedAt,
        CortexSyncReceiptDurabilityV1 Durability,
        IReadOnlyList<CortexSyncReceiptOutcomeV1> Outcomes,
        CortexSyncReceiptReconciliationV1 Reconciliation,
        IReadOnlyList<CortexSyncExtensionV1> Extensions);
}

public sealed record CortexSyncReceiptValidationV1
{
    public CortexSyncReceiptValidationV1(
        bool accepted,
        bool durablyReconciled,
        IReadOnlyList<CortexSyncCompatibilityIssueV1> issues)
    {
        Issues = CortexSyncContractShapes.SnapshotList(
            issues,
            CortexSyncContractLimits.MaximumCompatibilityIssues,
            nameof(issues));
        if (accepted != (Issues.Count == 0) || durablyReconciled && !accepted)
            throw new ArgumentException("The receipt validation result is inconsistent.");
        Accepted = accepted;
        DurablyReconciled = durablyReconciled;
    }

    public bool Accepted { get; }
    public bool DurablyReconciled { get; }
    public IReadOnlyList<CortexSyncCompatibilityIssueV1> Issues { get; }
}

public sealed record CortexSyncMigrationProgressValidationV1
{
    public CortexSyncMigrationProgressValidationV1(
        bool accepted,
        IReadOnlyList<CortexSyncCompatibilityIssueV1> issues)
    {
        Issues = CortexSyncContractShapes.SnapshotList(
            issues,
            CortexSyncContractLimits.MaximumCompatibilityIssues,
            nameof(issues));
        if (accepted != (Issues.Count == 0))
            throw new ArgumentException("The migration progress result is inconsistent.");
        Accepted = accepted;
    }

    public bool Accepted { get; }
    public IReadOnlyList<CortexSyncCompatibilityIssueV1> Issues { get; }
}

public sealed record CortexSyncBundleContinuationValidationV1
{
    public CortexSyncBundleContinuationValidationV1(
        bool accepted,
        IReadOnlyList<CortexSyncCompatibilityIssueV1> issues)
    {
        Issues = CortexSyncContractShapes.SnapshotList(
            issues,
            CortexSyncContractLimits.MaximumCompatibilityIssues,
            nameof(issues));
        if (accepted != (Issues.Count == 0))
            throw new ArgumentException("The bundle continuation result is inconsistent.");
        Accepted = accepted;
    }

    public bool Accepted { get; }
    public IReadOnlyList<CortexSyncCompatibilityIssueV1> Issues { get; }
}

public sealed record CortexSyncLegacyMigrationPageValidationV1
{
    public CortexSyncLegacyMigrationPageValidationV1(
        bool accepted,
        IReadOnlyList<CortexSyncCompatibilityIssueV1> issues)
    {
        Issues = CortexSyncContractShapes.SnapshotList(
            issues,
            CortexSyncContractLimits.MaximumCompatibilityIssues,
            nameof(issues));
        if (accepted != (Issues.Count == 0))
            throw new ArgumentException("The migration-page validation result is inconsistent.");
        Accepted = accepted;
    }

    public bool Accepted { get; }
    public IReadOnlyList<CortexSyncCompatibilityIssueV1> Issues { get; }
}
