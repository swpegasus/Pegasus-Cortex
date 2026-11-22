using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;

namespace Pegasus.Cortex.Persistence.PostgreSql.Sync;

/// <summary>
/// Captures the exact public sync evidence before database I/O. Relational projections are
/// derived only from these captured values, while the original canonical bytes remain durable.
/// </summary>
internal static class PostgreSqlSyncCanonicalCodec
{
    private const int MaximumCanonicalEventBytes = 1 * 1024 * 1024;
    private const int MaximumCanonicalSourceBytes = 8 * 1024;
    private const int MaximumCanonicalExtensionsBytes = 128 * 1024;

    internal static PostgreSqlSyncCapturedBundle Capture(CortexSyncCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Scope);
        ArgumentNullException.ThrowIfNull(request.Bundle);
        var scope = request.Scope;
        var bundle = request.Bundle;
        if (!ValidScope(scope) ||
            !string.Equals(scope.StoreId, bundle.StoreId, StringComparison.Ordinal) ||
            !string.Equals(scope.StoreEpoch, bundle.StoreEpoch, StringComparison.Ordinal) ||
            !string.Equals(scope.ProjectId, bundle.ProjectId, StringComparison.Ordinal) ||
            request.ReceivedAt.Offset != TimeSpan.Zero ||
            !CortexSyncContractPolicy.HasValidHash(bundle))
        {
            throw Failure("cortex_postgresql_sync_bundle_invalid", scope);
        }

        var canonical = CortexCanonicalJson.Serialize(bundle);
        if (canonical.Length is < 1 or > CortexSyncContractLimits.MaximumCanonicalBundleBytes)
            throw Failure("cortex_postgresql_sync_bundle_size_invalid", scope);

        var extensions = CaptureBounded(
            CortexCanonicalJson.Serialize(bundle.Extensions),
            MaximumCanonicalExtensionsBytes,
            "cortex_postgresql_sync_bundle_extensions_invalid",
            scope);
        var events = new PostgreSqlSyncCapturedEvent[bundle.Events.Count];
        long totalPayloadBytes = 0;
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        var eventHashes = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < bundle.Events.Count; index++)
        {
            var item = bundle.Events[index];
            if (!CortexSyncContractPolicy.HasValidHash(item) ||
                !eventIds.Add(item.EventId) || !eventHashes.Add(item.EventHash) ||
                !string.Equals(item.TenantIdHint, scope.TenantId, StringComparison.Ordinal) ||
                !string.Equals(item.NamespaceHint, scope.Namespace, StringComparison.Ordinal) ||
                item.ObservedPolicyRevision is not null &&
                    item.ObservedPolicyRevision != scope.PolicyRevision)
            {
                throw Failure("cortex_postgresql_sync_event_invalid", scope, item.EventId);
            }

            var payload = DecodeBase64Url(item.PayloadBase64Url, scope, item.EventId);
            totalPayloadBytes = checked(totalPayloadBytes + payload.Length);
            if (payload.Length != item.PayloadBytes ||
                !string.Equals(CortexCanonicalJson.Sha256(payload), item.PayloadSha256, StringComparison.Ordinal))
            {
                throw Failure("cortex_postgresql_sync_event_payload_invalid", scope, item.EventId);
            }

            var eventCanonical = CaptureBounded(
                CortexCanonicalJson.Serialize(item),
                MaximumCanonicalEventBytes,
                "cortex_postgresql_sync_event_size_invalid",
                scope,
                item.EventId);
            var sourceCanonical = CaptureBounded(
                CortexCanonicalJson.Serialize(item.Source),
                MaximumCanonicalSourceBytes,
                "cortex_postgresql_sync_source_size_invalid",
                scope,
                item.EventId);
            var eventExtensions = CaptureBounded(
                CortexCanonicalJson.Serialize(item.Extensions),
                MaximumCanonicalExtensionsBytes,
                "cortex_postgresql_sync_event_extensions_invalid",
                scope,
                item.EventId);
            events[index] = new PostgreSqlSyncCapturedEvent(
                item,
                eventCanonical,
                CortexCanonicalJson.Sha256(eventCanonical),
                payload,
                sourceCanonical,
                eventExtensions);
        }

        if (totalPayloadBytes > CortexSyncContractLimits.MaximumCanonicalBundleBytes)
            throw Failure("cortex_postgresql_sync_payload_total_invalid", scope);

        var canonicalSha256 = CortexCanonicalJson.Sha256(canonical);
        return new PostgreSqlSyncCapturedBundle(
            scope with { },
            bundle,
            PostgreSqlTimestamp(request.ReceivedAt),
            canonical,
            canonicalSha256,
            extensions,
            Array.AsReadOnly(events));
    }

    internal static PostgreSqlSyncCapturedMigrationPage Capture(
        CortexLegacyMigrationPageCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bundle = Capture(new CortexSyncCommitRequest(
            request.Scope,
            request.Page.Bundle,
            request.ReceivedAt));
        if (!CortexSyncContractPolicy.ValidateLegacyMigrationPage(request.Page).Accepted)
            throw Failure("cortex_postgresql_sync_migration_page_invalid", request.Scope, request.Page.PageId);

        var canonical = request.CanonicalPage.ToArray();
        if (!CortexCanonicalJson.Serialize(request.Page).AsSpan().SequenceEqual(canonical) ||
            !string.Equals(CortexCanonicalJson.Sha256(canonical), request.CanonicalSha256, StringComparison.Ordinal))
        {
            throw Failure("cortex_postgresql_sync_migration_page_canonical_mismatch", request.Scope, request.Page.PageId);
        }

        return new PostgreSqlSyncCapturedMigrationPage(
            bundle,
            request.Page,
            canonical,
            request.CanonicalSha256);
    }

    internal static CortexOfflineBundleV1 DecodeBundle(
        byte[] canonical,
        string expectedSha256,
        CortexSyncRepositoryScope scope)
    {
        if (canonical.Length is < 1 or > CortexSyncContractLimits.MaximumCanonicalBundleBytes ||
            !string.Equals(CortexCanonicalJson.Sha256(canonical), expectedSha256, StringComparison.Ordinal))
        {
            throw Failure("cortex_postgresql_sync_bundle_canonical_mismatch", scope);
        }

        try
        {
            var value = CortexCanonicalJson.Deserialize<CortexOfflineBundleV1>(canonical);
            if (!CortexSyncContractPolicy.HasValidHash(value) ||
                !CortexCanonicalJson.Serialize(value).AsSpan().SequenceEqual(canonical))
            {
                throw Failure("cortex_postgresql_sync_bundle_canonical_mismatch", scope);
            }
            return value;
        }
        catch (PostgreSqlPersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
        {
            throw Failure("cortex_postgresql_sync_bundle_canonical_invalid", scope, cause: exception);
        }
    }

    internal static CortexOfflineEventV1 DecodeEvent(
        byte[] canonical,
        string expectedSha256,
        CortexSyncRepositoryScope scope,
        string eventId)
    {
        if (canonical.Length is < 1 or > MaximumCanonicalEventBytes ||
            !string.Equals(CortexCanonicalJson.Sha256(canonical), expectedSha256, StringComparison.Ordinal))
        {
            throw Failure("cortex_postgresql_sync_event_canonical_mismatch", scope, eventId);
        }

        try
        {
            var value = CortexCanonicalJson.Deserialize<CortexOfflineEventV1>(canonical);
            if (!CortexSyncContractPolicy.HasValidHash(value) ||
                !CortexCanonicalJson.Serialize(value).AsSpan().SequenceEqual(canonical))
            {
                throw Failure("cortex_postgresql_sync_event_canonical_mismatch", scope, eventId);
            }
            return value;
        }
        catch (PostgreSqlPersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
        {
            throw Failure("cortex_postgresql_sync_event_canonical_invalid", scope, eventId, exception);
        }
    }

    internal static CortexSyncReceiptV1 DecodeReceipt(
        byte[] canonical,
        string expectedSha256,
        CortexSyncRepositoryScope scope,
        string receiptId)
    {
        if (canonical.Length is < 1 or > CortexSyncContractLimits.MaximumCanonicalBundleBytes ||
            !string.Equals(CortexCanonicalJson.Sha256(canonical), expectedSha256, StringComparison.Ordinal))
        {
            throw Failure("cortex_postgresql_sync_receipt_canonical_mismatch", scope, receiptId);
        }

        try
        {
            var value = CortexCanonicalJson.Deserialize<CortexSyncReceiptV1>(canonical);
            if (!CortexSyncContractPolicy.HasValidSemanticHash(value) ||
                !CortexCanonicalJson.Serialize(value).AsSpan().SequenceEqual(canonical))
            {
                throw Failure("cortex_postgresql_sync_receipt_canonical_mismatch", scope, receiptId);
            }
            return value;
        }
        catch (PostgreSqlPersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
        {
            throw Failure("cortex_postgresql_sync_receipt_canonical_invalid", scope, receiptId, exception);
        }
    }

    internal static CortexLegacyMigrationPageV1 DecodeMigrationPage(
        byte[] canonical,
        string expectedSha256,
        CortexSyncRepositoryScope scope,
        string pageId)
    {
        if (canonical.Length is < 1 or > CortexSyncContractLimits.MaximumCanonicalBundleBytes ||
            !string.Equals(CortexCanonicalJson.Sha256(canonical), expectedSha256, StringComparison.Ordinal))
        {
            throw Failure("cortex_postgresql_sync_migration_page_canonical_mismatch", scope, pageId);
        }

        try
        {
            var value = CortexCanonicalJson.Deserialize<CortexLegacyMigrationPageV1>(canonical);
            if (!CortexSyncContractPolicy.ValidateLegacyMigrationPage(value).Accepted ||
                !CortexCanonicalJson.Serialize(value).AsSpan().SequenceEqual(canonical))
            {
                throw Failure("cortex_postgresql_sync_migration_page_canonical_mismatch", scope, pageId);
            }
            return value;
        }
        catch (PostgreSqlPersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
        {
            throw Failure("cortex_postgresql_sync_migration_page_canonical_invalid", scope, pageId, exception);
        }
    }

    internal static byte[] DecodeBase64Url(
        string value,
        CortexSyncRepositoryScope scope,
        string evidenceId)
    {
        if (string.IsNullOrEmpty(value) || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw Failure("cortex_postgresql_sync_encoded_evidence_invalid", scope, evidenceId);
        }

        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw new FormatException("Invalid base64url length.")
            };
            return Convert.FromBase64String(padded);
        }
        catch (FormatException exception)
        {
            throw Failure("cortex_postgresql_sync_encoded_evidence_invalid", scope, evidenceId, exception);
        }
    }

    internal static DateTimeOffset PostgreSqlTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    internal static bool ValidScope(CortexSyncRepositoryScope? scope) =>
        scope is not null &&
        ValidIdentifier(scope.TenantId, 128) &&
        ValidIdentifier(scope.Namespace, 256) &&
        ValidIdentifier(scope.AuthorityId, 128) &&
        ValidIdentifier(scope.SubjectId, 128) &&
        ValidIdentifier(scope.ServiceId, 128) &&
        scope.PolicyRevision > 0 &&
        ValidIdentifier(scope.StoreId, 128) &&
        ValidIdentifier(scope.StoreEpoch, 128) &&
        ValidIdentifier(scope.ProjectId, 128) &&
        ValidIdentifier(scope.Operation, 128);

    internal static bool ValidIdentifier(string? value, int maximum = 128) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    internal static bool ValidHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static PostgreSqlPersistenceException Failure(
        string code,
        CortexSyncRepositoryScope? scope,
        string? evidenceId = null,
        Exception? cause = null) =>
        new(
            code,
            ValidIdentifier(scope?.Operation, 128) ? scope!.Operation : "cortex.sync.persistence",
            retryable: false,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["tenantId"] = ValidIdentifier(scope?.TenantId, 128) ? scope!.TenantId : null,
                ["namespace"] = ValidIdentifier(scope?.Namespace, 256) ? scope!.Namespace : null,
                ["storeId"] = ValidIdentifier(scope?.StoreId, 128) ? scope!.StoreId : null,
                ["projectId"] = ValidIdentifier(scope?.ProjectId, 128) ? scope!.ProjectId : null,
                ["evidenceId"] = ValidIdentifier(evidenceId, 128) ? evidenceId : null
            },
            innerException: cause);

    private static byte[] CaptureBounded(
        byte[] value,
        int maximum,
        string failureCode,
        CortexSyncRepositoryScope scope,
        string? evidenceId = null)
    {
        if (value.Length is < 1 || value.Length > maximum)
            throw Failure(failureCode, scope, evidenceId);
        return value;
    }
}

internal sealed record PostgreSqlSyncCapturedBundle(
    CortexSyncRepositoryScope Scope,
    CortexOfflineBundleV1 Bundle,
    DateTimeOffset ReceivedAt,
    byte[] Canonical,
    string CanonicalSha256,
    byte[] ExtensionsCanonical,
    IReadOnlyList<PostgreSqlSyncCapturedEvent> Events);

internal sealed record PostgreSqlSyncCapturedEvent(
    CortexOfflineEventV1 Event,
    byte[] Canonical,
    string CanonicalSha256,
    byte[] Payload,
    byte[] SourceCanonical,
    byte[] ExtensionsCanonical);

internal sealed record PostgreSqlSyncCapturedMigrationPage(
    PostgreSqlSyncCapturedBundle Bundle,
    CortexLegacyMigrationPageV1 Page,
    byte[] Canonical,
    string CanonicalSha256);
