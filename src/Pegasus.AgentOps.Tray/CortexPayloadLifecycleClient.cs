using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Local.Runtime;

namespace Pegasus.AgentOps.Tray;

/// <summary>Attach-only, fixed owner control. No database path, startup,
/// alternate identity, automatic resend or transport fallback is accepted.</summary>
internal sealed class CortexPayloadLifecycleClient : ICortexPayloadLifecycleClient
{
    internal const string StatusSchema = "pegasus.cortex.payload-lifecycle-status/1";
    private const string CleanupIssue = "payload_policy_cleanup_required";
    private const string ReviewIssue = "payload_policy_review_required";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 12
    };
    private readonly Func<CancellationToken, Task<ICortexHostExitConnection?>> _connect;
    internal CortexPayloadLifecycleClient(Func<CancellationToken, Task<ICortexHostExitConnection?>>? connect = null) =>
        _connect = connect ?? CortexHostExitControl.ConnectCurrentAsync;

    public Task<CortexPayloadLifecycleClientResult> ReadAsync(CancellationToken token) =>
        SendAsync(new { version = 1, action = "lifecycle_status" }, true, token);

    public async Task<CortexPayloadLifecycleClientResult> SaveAsync(CortexPayloadLifecyclePolicy policy,
        long expectedRevision, CancellationToken token)
    {
        CortexPayloadLifecyclePolicy requested;
        try
        {
            requested = CortexPayloadLifecycleSettingsStore.Validate(policy);
            if (expectedRevision < 0 || expectedRevision == long.MaxValue) throw new ArgumentException();
        }
        catch (ArgumentException) { return new("invalid", "payload_policy_invalid"); }
        var result = await SendAsync(new { version = 1, action = "lifecycle_save", expectedPolicyRevision = expectedRevision,
            policy = requested }, true, token, isSave: true).ConfigureAwait(false);
        if (result.State is "done" or "done_cleanup_incomplete" &&
            (result.Status?.Saved is not { } saved || saved.Revision != expectedRevision + 1 || !SamePolicy(saved.Policy, requested)))
            return result with { State = "unknown", Error = "saved_policy_unverified" };
        return result;
    }

    public Task<CortexPayloadLifecycleClientResult> RunOnceAsync(CancellationToken token) =>
        SendAsync(new { version = 1, action = "lifecycle_run" }, true, token);

    public async Task<CortexPayloadLifecycleClientResult> UpgradeAsync(CancellationToken token)
    {
        var prepared = await SendAsync(new { version = 1, action = "upgrade", expectedSchemaVersion = 2 }, false, token)
            .ConfigureAwait(false);
        if (prepared.State == "done_cleanup_incomplete") return new("prepared_status_unavailable",
            "The original preparation request returned success, but connection cleanup was incomplete and schema3 owner status was not verified. Refresh before another change.", RequestId: prepared.RequestId);
        if (prepared.State != "done") return prepared;
        // This is one read after a confirmed original operation, never a resend
        // of the upgrade after a lost or partial acknowledgement.
        var current = await ReadAsync(token).ConfigureAwait(false);
        return current.State == "done" && current.Status is { OwnerBound: true, StoreSchemaVersion: 3 }
            ? current : new("prepared_status_unavailable",
                "The original preparation request returned success, but a bound schema3 owner has not been verified. Refresh and review the original request.",
                current.Status, prepared.RequestId);
    }

    private async Task<CortexPayloadLifecycleClientResult> SendAsync(object body, bool requireStatus, CancellationToken token,
        bool isSave = false)
    {
        if (token.IsCancellationRequested) return new("invalid", "canceled_before_submission");
        var serialized = JsonSerializer.Serialize(body, Json);
        if (Encoding.UTF8.GetByteCount(serialized) > CortexPayloadTieringControl.MaximumBodyBytes)
            return new("invalid", "payload_request_too_large");
        var request = new OperationRequest(CortexPayloadTieringControl.Operation, new Dictionary<string, string>(),
            Guid.NewGuid().ToString("N"), Body: serialized);
        ICortexHostExitConnection? connection = null;
        var submitted = false;
        var cleanupIncomplete = false;
        CortexPayloadLifecycleClientResult result;
        try
        {
            token.ThrowIfCancellationRequested();
            connection = await _connect(token).ConfigureAwait(false);
            if (connection is null) return new("unavailable", "owner_unavailable");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(30));
            submitted = true;
            var receipt = await connection.SendAsync(request, deadline.Token).ConfigureAwait(false);
            if (receipt.Op != request.Op || receipt.Rid != request.Rid ||
                !Guid.TryParseExact(receipt.Jid, "N", out _) ||
                receipt.State is not ("done" or "failed" or "canceled" or "recovery_required") ||
                receipt.Ok != (receipt.State == "done") || receipt.Ok && receipt.Err is not null)
                result = new("unknown", "reply_unverified", RequestId: request.Rid);
            else
            {
                var status = ReadStatus(receipt, required: receipt.Ok && requireStatus);
                var selectedVerified = Flag(receipt, "selected_policy_verified", true) && Flag(receipt, "outcome_unknown", false);
                // This narrowly identifies an applied save with remaining publication cleanup.
                // SaveAsync still verifies the new revision and requested policy before confirming it.
                if (isSave && receipt.State == "failed" && receipt.Err == CleanupIssue && status is not null &&
                    Flag(receipt, "policy_save_applied", true) && selectedVerified)
                    result = new("done_cleanup_incomplete", CleanupIssue, status, request.Rid);
                else if (status is { Issue: CleanupIssue or ReviewIssue } &&
                    (!selectedVerified || status.Saved is not { Revision: > 0 }))
                    result = new("unknown", "selected_policy_snapshot_unverified", status, request.Rid);
                else if (receipt.Partial || receipt.State == "recovery_required" ||
                    Flag(receipt, "outcome_unknown", true))
                    result = new("unknown", Code(receipt.Err, "outcome_unknown"), status, request.Rid);
                else result = receipt.Ok
                    ? new("done", Status: status, RequestId: request.Rid)
                    : new("refused", Code(receipt.Err, "operation_refused"), status, request.Rid);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            result = new(submitted ? "unknown" : "unavailable", submitted ? "reply_unavailable" : "owner_unavailable",
                RequestId: submitted ? request.Rid : null);
        }
        finally
        {
            if (connection is not null)
            {
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                { cleanupIncomplete = true; }
            }
        }
        // Keep a verified saved snapshot/operation result when only the local
        // connection cleanup fails. Never turn it into a new save.
        if (cleanupIncomplete) result = result with { State = result.State == "done" ? "done_cleanup_incomplete" : result.State,
            Error = result.Error ?? "owner_connection_cleanup_incomplete" };
        return result;
    }

    private static CortexPayloadLifecycleStatus? ReadStatus(OperationReceipt receipt, bool required)
    {
        if (receipt.Data?.GetValueOrDefault("lifecycle") is not { } raw)
        {
            if (required) throw new InvalidDataException("lifecycle_status_missing");
            return null;
        }
        var element = JsonSerializer.SerializeToElement(raw);
        var text = element.GetRawText();
        if (Encoding.UTF8.GetByteCount(text) > CortexPayloadLifecycleSettingsStore.MaximumBytes)
            throw new InvalidDataException("lifecycle_status_too_large");
        UniqueNames(element);
        if (element.ValueKind != JsonValueKind.Object || !BooleanField(element, "ownerBound") || !BooleanField(element, "passRunning"))
            throw new InvalidDataException("lifecycle_status_invalid");
        var status = element.Deserialize<CortexPayloadLifecycleStatus>(Json) ?? throw new InvalidDataException("lifecycle_status_invalid");
        if (status.Schema != StatusSchema || status.StoreSchemaVersion is not (null or 2 or 3) ||
            !Identifier(status.State) || status.Issue is { Length: > 256 } ||
            status.Namespace is { } ns && !Identifier(ns) || status.HotBytesEvicted < 0 || status.SelectedRecords < 0 || status.MetadataObservations < 0 ||
            status.LastStartedUtc is { } started && started.Offset != TimeSpan.Zero ||
            status.LastFinishedUtc is { } finished && finished.Offset != TimeSpan.Zero)
            throw new InvalidDataException("lifecycle_status_invalid");
        if (status.Saved is { } saved)
        {
            if (saved.Revision < 0) throw new InvalidDataException("lifecycle_status_invalid");
            status = status with { Saved = saved with { Policy = CortexPayloadLifecycleSettingsStore.Validate(saved.Policy) } };
        }
        return status;
    }

    internal static bool SamePolicy(CortexPayloadLifecyclePolicy left, CortexPayloadLifecyclePolicy right) =>
        left.Version == right.Version && left.Enabled == right.Enabled && left.Namespaces.SequenceEqual(right.Namespaces, StringComparer.Ordinal) &&
        left.MinimumArchiveAgeDays == right.MinimumArchiveAgeDays && left.TargetHotMiB == right.TargetHotMiB &&
        left.IntervalMinutes == right.IntervalMinutes && left.WorkMilliseconds == right.WorkMilliseconds &&
        left.MaximumMetadataHeads == right.MaximumMetadataHeads;

    private static bool Identifier(string? value) => value is { Length: > 0 and <= 128 } &&
        char.IsAsciiLetterOrDigit(value[0]) && value.Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');
    private static string Code(string? value, string fallback) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl) ? value : fallback;
    private static bool Flag(OperationReceipt receipt, string key, bool expected) => receipt.Data?.GetValueOrDefault(key) switch
    {
        bool value => value == expected,
        JsonElement value => value.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False),
        _ => false
    };
    private static bool BooleanField(JsonElement value, string name) => value.EnumerateObject().Any(property =>
        property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False);
    private static void UniqueNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { if (!seen.Add(property.Name)) throw new InvalidDataException("lifecycle_status_duplicate"); UniqueNames(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueNames(item);
    }
}
