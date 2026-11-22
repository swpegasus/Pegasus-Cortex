using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Sync.Client.Contracts;

namespace Pegasus.Cortex.Sync.Client.Persistence;

/// <summary>
/// Canonical sealing and validation for the crash-replay submit boundary. The original request
/// bytes are authoritative; the parsed request and duplicated metadata must reproduce them exactly.
/// </summary>
public static class CortexSyncPendingAttemptPolicy
{
    internal const int MaximumCanonicalRequestBytes =
        CortexSyncContractLimits.MaximumCanonicalBundleBytes + (64 * 1024);

    public static CortexSyncPendingSubmitAttempt Create(
        CortexSyncSubmitRequest request,
        CortexSyncRemoteHead remoteParent)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(remoteParent);
        if (!CortexSyncContractPolicy.HasValidHash(request) ||
            !CortexSyncContractPolicy.HasValidHash(remoteParent))
        {
            throw new InvalidDataException(
                "A pending attempt requires a sealed request and sealed remote parent.");
        }

        var bundle = request.Bundle;
        if (!SameScope(request, remoteParent) ||
            bundle.FirstSequence != remoteParent.HighWaterSequence + 1 ||
            !string.Equals(
                bundle.BaseServerRevision,
                remoteParent.Exists ? remoteParent.ServerRevision : null,
                StringComparison.Ordinal) ||
            !string.Equals(bundle.PreviousBundleHash, remoteParent.LastBundleHash, StringComparison.Ordinal) ||
            !string.Equals(bundle.PriorEventHash, remoteParent.LastEventHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The submit request does not extend its exact authenticated remote parent.");
        }

        var bytes = CortexCanonicalJson.Serialize(request);
        if (bytes.Length is <= 0 or > MaximumCanonicalRequestBytes)
            throw new InvalidDataException("The canonical submit request exceeds its fixed local bound.");

        var candidate = new CortexSyncPendingAttemptV1(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncClientContractIds.PendingAttempt,
            CortexSyncClientContractIds.Canonicalization,
            "attempt-" + request.RequestHash[..24],
            request.ProjectLinkId,
            bundle.ProjectId,
            bundle.StoreId,
            bundle.StoreEpoch,
            bundle.BundleId,
            bundle.BundleHash,
            bundle.FirstSequence,
            bundle.LastSequence,
            remoteParent.Exists,
            remoteParent.HighWaterSequence,
            remoteParent.LastEventId,
            remoteParent.LastEventHash,
            remoteParent.LastBundleId,
            remoteParent.LastBundleHash,
            remoteParent.AuthorityHash,
            remoteParent.RetainedPayloadHash,
            remoteParent.ServerRevision,
            bundle.CreatedAt,
            bytes.Length,
            CortexCanonicalJson.Sha256(bytes),
            Convert.ToBase64String(bytes),
            CortexSyncClientContractIds.EmptySha256);
        var evidence = candidate with { AttemptHash = ComputeHash(candidate) };
        return ValidateAndDecode(evidence);
    }

    public static CortexSyncPendingSubmitAttempt ValidateAndDecode(
        CortexSyncPendingAttemptV1 evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateShape(evidence);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(evidence.CanonicalRequestBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "The pending attempt does not contain canonical request bytes.",
                exception);
        }

        if (bytes.Length != evidence.CanonicalRequestBytes ||
            !string.Equals(Convert.ToBase64String(bytes), evidence.CanonicalRequestBase64, StringComparison.Ordinal) ||
            !string.Equals(CortexCanonicalJson.Sha256(bytes), evidence.CanonicalRequestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The pending attempt request bytes do not match their length or digest.");
        }

        CortexSyncSubmitRequest request;
        try
        {
            request = CortexCanonicalJson.Deserialize<CortexSyncSubmitRequest>(bytes);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException)
        {
            throw new InvalidDataException("The pending submit request is malformed.", exception);
        }

        var bundle = request.Bundle;
        if (!CortexSyncContractPolicy.HasValidHash(request) ||
            !CortexCanonicalJson.Serialize(request).AsSpan().SequenceEqual(bytes) ||
            !string.Equals(evidence.AttemptId, "attempt-" + request.RequestHash[..24], StringComparison.Ordinal) ||
            !string.Equals(evidence.ProjectLinkId, request.ProjectLinkId, StringComparison.Ordinal) ||
            !string.Equals(evidence.ProjectId, bundle.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(evidence.StoreId, bundle.StoreId, StringComparison.Ordinal) ||
            !string.Equals(evidence.StoreEpoch, bundle.StoreEpoch, StringComparison.Ordinal) ||
            !string.Equals(evidence.BundleId, bundle.BundleId, StringComparison.Ordinal) ||
            !string.Equals(evidence.BundleHash, bundle.BundleHash, StringComparison.Ordinal) ||
            evidence.FirstSequence != bundle.FirstSequence ||
            evidence.LastSequence != bundle.LastSequence ||
            evidence.CreatedAt != bundle.CreatedAt ||
            bundle.FirstSequence != evidence.RemoteParentHighWaterSequence + 1 ||
            !string.Equals(
                bundle.BaseServerRevision,
                evidence.RemoteParentExists ? evidence.RemoteParentServerRevision : null,
                StringComparison.Ordinal) ||
            !string.Equals(bundle.PreviousBundleHash, evidence.RemoteParentBundleHash, StringComparison.Ordinal) ||
            !string.Equals(bundle.PriorEventHash, evidence.RemoteParentEventHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The pending request is noncanonical or mismatched with its sealed scope and parent.");
        }

        return new CortexSyncPendingSubmitAttempt(evidence, request, bytes);
    }

    public static void Validate(CortexSyncPendingSubmitAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var decoded = ValidateAndDecode(attempt.Evidence);
        if (!decoded.CanonicalRequestBytes.Span.SequenceEqual(attempt.CanonicalRequestBytes.Span) ||
            !string.Equals(decoded.Request.RequestHash, attempt.Request.RequestHash, StringComparison.Ordinal) ||
            !CortexCanonicalJson.Serialize(attempt.Request).AsSpan().SequenceEqual(
                attempt.CanonicalRequestBytes.Span))
        {
            throw new InvalidDataException(
                "The in-memory pending attempt differs from its sealed canonical evidence.");
        }
    }

    public static string ComputeHash(CortexSyncPendingAttemptV1 value) =>
        CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(value with
        {
            AttemptHash = CortexSyncClientContractIds.EmptySha256
        }));

    private static void ValidateShape(CortexSyncPendingAttemptV1 value)
    {
        var base64Limit = checked(((MaximumCanonicalRequestBytes + 2) / 3) * 4);
        if (value.SchemaVersion != CortexSyncContractIds.SchemaVersion ||
            !string.Equals(value.ContractId, CortexSyncClientContractIds.PendingAttempt, StringComparison.Ordinal) ||
            !string.Equals(value.Canonicalization, CortexSyncClientContractIds.Canonicalization, StringComparison.Ordinal) ||
            !ValidIdentifier(value.AttemptId) ||
            !ValidProjectLink(value.ProjectLinkId) ||
            !ValidIdentifier(value.ProjectId) ||
            !ValidIdentifier(value.StoreId) ||
            !ValidIdentifier(value.StoreEpoch) ||
            !ValidIdentifier(value.BundleId) ||
            !CortexSyncCursorPolicy.IsHash(value.BundleHash) ||
            value.FirstSequence < 1 ||
            value.LastSequence < value.FirstSequence ||
            value.LastSequence > CortexSyncContractLimits.MaximumSequence ||
            value.RemoteParentHighWaterSequence < 0 ||
            value.RemoteParentHighWaterSequence > CortexSyncContractLimits.MaximumSequence ||
            !CortexSyncCursorPolicy.IsHash(value.RemoteParentEventHash) ||
            !CortexSyncCursorPolicy.IsHash(value.RemoteParentBundleHash) ||
            value.CreatedAt.Offset != TimeSpan.Zero ||
            value.CanonicalRequestBytes is <= 0 or > MaximumCanonicalRequestBytes ||
            value.CanonicalRequestBase64.Length is <= 0 ||
            value.CanonicalRequestBase64.Length > base64Limit ||
            !CortexSyncCursorPolicy.IsHash(value.CanonicalRequestSha256) ||
            !CortexSyncCursorPolicy.IsHash(value.AttemptHash) ||
            !string.Equals(value.AttemptHash, ComputeHash(value), StringComparison.Ordinal) ||
            value.RemoteParentExists &&
            (value.RemoteParentHighWaterSequence < 1 ||
             !ValidIdentifier(value.RemoteParentLastEventId) ||
             !ValidIdentifier(value.RemoteParentLastBundleId) ||
             !CortexSyncCursorPolicy.IsHash(value.RemoteParentAuthorityHash) ||
             !CortexSyncCursorPolicy.IsHash(value.RemoteParentPayloadHash) ||
             !ValidIdentifier(value.RemoteParentServerRevision) ||
             string.Equals(value.RemoteParentEventHash, CortexSyncClientContractIds.EmptySha256, StringComparison.Ordinal) ||
             string.Equals(value.RemoteParentBundleHash, CortexSyncClientContractIds.EmptySha256, StringComparison.Ordinal)) ||
            !value.RemoteParentExists &&
            (value.RemoteParentHighWaterSequence != 0 ||
             value.RemoteParentLastEventId is not null ||
             value.RemoteParentLastBundleId is not null ||
             value.RemoteParentAuthorityHash is not null ||
             value.RemoteParentPayloadHash is not null ||
             value.RemoteParentServerRevision is not null ||
             !string.Equals(value.RemoteParentEventHash, CortexSyncClientContractIds.EmptySha256, StringComparison.Ordinal) ||
             !string.Equals(value.RemoteParentBundleHash, CortexSyncClientContractIds.EmptySha256, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The pending sync attempt is malformed, unsupported, or modified.");
        }
    }

    private static bool SameScope(CortexSyncSubmitRequest request, CortexSyncRemoteHead parent) =>
        string.Equals(request.ProjectLinkId, parent.ProjectLinkId, StringComparison.Ordinal) &&
        string.Equals(request.Bundle.ProjectId, parent.ProjectId, StringComparison.Ordinal) &&
        string.Equals(request.Bundle.StoreId, parent.StoreId, StringComparison.Ordinal) &&
        string.Equals(request.Bundle.StoreEpoch, parent.StoreEpoch, StringComparison.Ordinal);

    private static bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= CortexSyncContractLimits.MaximumIdentifierCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool ValidProjectLink(string? value) =>
        value is { Length: 70 } &&
        value.StartsWith("plink_", StringComparison.Ordinal) &&
        value.Skip(6).All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
