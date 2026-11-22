using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Persistence.Sqlite.V2;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

internal interface ICortexPayloadLifecycleTicker : IAsyncDisposable
{
    ValueTask<bool> WaitAsync(CancellationToken token);
}

internal sealed partial class CortexPayloadTieringControl
{
    private static readonly JsonSerializerOptions LifecycleJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    private ICortexPayloadLifecycleSettingsStore? _lifecycleSettings;
    private CortexPayloadLifecycleSettings? _lifecycleSaved;
    private TimeProvider _lifecycleClock = TimeProvider.System;
    private Task _lifecycleLoop = Task.CompletedTask;
    private Pending? _configurationCurrent;
    private (string Rid, string Identity, OperationReceipt Receipt)? _configurationLast;
    private CancellationTokenSource? _lifecyclePassCancellation;
    private bool _lifecycleBound, _lifecycleBinding, _lifecycleRunning, _lifecyclePaused;
    private int? _lifecycleSchema, _lifecycleSelected, _lifecycleMetadata;
    private int _lifecycleNamespaceIndex;
    private string _lifecycleState = "unobserved";
    private string? _lifecycleIssue, _lifecycleNamespace;
    private DateTimeOffset _lifecycleNext;
    private DateTimeOffset? _lifecycleStarted, _lifecycleFinished;
    private long? _lifecycleEvicted;
    private bool? _lifecycleTarget;
    private bool _lifecycleImmutableObjectsMayRemain;
    private bool _lifecycleSelectedPolicyVerified;
    private long _lifecycleSettingsEpoch;
    private readonly Dictionary<string, SqliteCortexPayloadRotationCursor> _lifecycleCursors = new(StringComparer.Ordinal);

    internal void BindOptionalLifecycleDirectory(string configDirectory,
        Func<ICortexPayloadLifecycleTicker>? createTicker = null)
    {
        lock (_gate)
        {
            if (!_bound || _closing || _lifecycleBound || _lifecycleBinding)
                throw new InvalidOperationException("payload_lifecycle_binding_unavailable");
            _lifecycleBinding = true;
        }
        try
        {
            CortexPayloadLifecycleSettingsStore settings;
            try { settings = new CortexPayloadLifecycleSettingsStore(configDirectory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            {
                // The optional policy is not a second core-store admission gate.
                // Preserve the actual bound backend/runtime and start no ticker;
                // another path or a default policy cannot rescue this bad binding.
                lock (_gate)
                {
                    _lifecycleBound = true; _lifecycleSaved = null; _lifecyclePaused = true;
                    _lifecycleIssue = "payload_policy_binding_unavailable"; _lifecycleState = "settings_unavailable";
                }
                return;
            }
            // Binding-state/programming errors remain visible. The reservation
            // prevents another bind from being overwritten by our failure path.
            BindLifecycleCore(settings, null, createTicker, directoryReservation: true);
        }
        finally { lock (_gate) _lifecycleBinding = false; }
    }

    // Only the actual resident bind passes its fixed private config directory.
    // Tests pass a typed store/ticker; an unbound fixture never resolves ambient state.
    internal void BindLifecycle(ICortexPayloadLifecycleSettingsStore settings,
        TimeProvider? clock = null, Func<ICortexPayloadLifecycleTicker>? createTicker = null) =>
        BindLifecycleCore(settings, clock, createTicker, directoryReservation: false);

    private void BindLifecycleCore(ICortexPayloadLifecycleSettingsStore settings,
        TimeProvider? clock, Func<ICortexPayloadLifecycleTicker>? createTicker, bool directoryReservation)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            if (!_bound || _closing || _lifecycleBound || _lifecycleBinding && !directoryReservation)
                throw new InvalidOperationException("payload_lifecycle_binding_unavailable");
            _lifecycleSettings = settings; _lifecycleClock = clock ?? TimeProvider.System;
            _lifecycleBound = true; _lifecycleNext = _lifecycleClock.GetUtcNow();
        }
        ReloadLifecycleSettings();
        lock (_gate)
        {
            if (!_closing)
                _lifecycleLoop = LifecycleLoopAsync(createTicker ?? (() => new LifecycleTicker(_lifecycleClock)));
        }
    }

    private async Task LifecycleLoopAsync(Func<ICortexPayloadLifecycleTicker> createTicker)
    {
        // A closed tray synchronization context must not own the resident loop.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await using var ticker = createTicker();
            while (await ticker.WaitAsync(_shutdown.Token).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (_closing) break;
                    if (_lifecycleSaved?.Policy.Enabled != true || _lifecyclePaused || _lifecycleSchema == 2 || _current is not null ||
                        _lifecycleClock.GetUtcNow() < _lifecycleNext) continue;
                }
                // No new queue or catch-up loop. A busy owner is deferred until
                // a later cadence, and the original pass is always joined.
                var request = new OperationRequest(Operation, new Dictionary<string, string>(),
                    Guid.NewGuid().ToString("N"), Body: "{\"version\":1,\"action\":\"lifecycle_run\"}");
                _ = await StartLifecyclePassAsync(request, ValidateLifecycleRequest(request).Identity,
                    _shutdown.Token, automatic: true).ConfigureAwait(false);
            }
            lock (_gate)
                if (!_closing && !_shutdown.IsCancellationRequested)
                { _lifecyclePaused = true; _lifecycleIssue = "payload_lifecycle_timer_stopped"; _lifecycleState = "paused"; }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            lock (_gate) { _lifecyclePaused = true; _lifecycleIssue = "payload_lifecycle_loop_failed"; _lifecycleState = "paused"; }
        }
    }

    private static bool IsLifecycleRequest(OperationRequest request)
    {
        if (request.Body is null || request.Body.Length > MaximumBodyBytes) return false;
        try
        {
            using var json = JsonDocument.Parse(request.Body, new JsonDocumentOptions { MaxDepth = 8 });
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("action", out var action) &&
                action.ValueKind == JsonValueKind.String && action.GetString() is "lifecycle_status" or "lifecycle_save" or "lifecycle_run";
        }
        catch (JsonException) { return false; }
    }

    private Task<OperationReceipt> HandleLifecycleAsync(OperationRequest request, CancellationToken token)
    {
        LifecycleCommand command;
        try { command = ValidateLifecycleRequest(request); }
        catch (ArgumentException) { return Task.FromResult(Reply(request, "invalid")); }
        if (token.IsCancellationRequested) return Task.FromResult(Reply(request, "canceled"));
        if (command.Action == "lifecycle_run") return StartLifecyclePassAsync(request, command.Identity, token, automatic: false);
        if (command.Action == "lifecycle_status")
        {
            lock (_gate)
            {
                if (TryAnyLifecycleCorrelation(request, command.Identity, out var previous)) return previous;
                if (_closing) return Task.FromResult(Reply(request, "host_stopping"));
            }
            ReloadLifecycleSettings();
            lock (_gate)
            {
                var status = LifecycleStatusLocked();
                if (!Authorized(_owner)) return Task.FromResult(Reply(request, "auth"));
                var publicationUnknown = status.Issue == "payload_policy_recovery_required";
                var result = LifecycleReply(request, status,
                    publicationUnknown ? "payload_policy_recovery_required" : status.Saved is null ? "payload_policy_unavailable" : null,
                    unknown: publicationUnknown);
                if (status.Issue is "payload_policy_cleanup_required" or "payload_policy_review_required" or "payload_policy_recovery_required")
                    result = WithPublicationEvidence(result, null,
                        !publicationUnknown && _lifecycleSelectedPolicyVerified && status.Saved is { Revision: > 0 });
                return Task.FromResult(result);
            }
        }
        Pending pending;
        lock (_gate)
        {
            if (TryAnyLifecycleCorrelation(request, command.Identity, out var previous)) return previous;
            if (_closing) return Task.FromResult(Reply(request, "host_stopping"));
            if (_configurationCurrent is not null) return Task.FromResult(Reply(request, "payload_policy_busy"));
            if (_lifecycleSettings is null) return Task.FromResult(Reply(request, "payload_lifecycle_unavailable"));
            pending = new(request.Rid!, command.Identity);
            _configurationCurrent = pending;
        }
        _ = SaveLifecycleOwnedAsync(pending, request, command);
        return pending.Completion.Task;
    }

    private async Task SaveLifecycleOwnedAsync(Pending pending, OperationRequest request, LifecycleCommand command)
    {
        // Keep publication/cleanup on the owner lifetime after the requesting UI
        // has closed, without requiring that UI's message loop to resume us.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        OperationReceipt receipt;
        var committed = false; var attempted = false;
        try
        {
            if (!Authorized(_owner)) throw new InvalidOperationException("auth");
            RequireOpen();
            attempted = true;
            var saved = _lifecycleSettings!.Save(command.Policy!, command.ExpectedRevision);
            if (saved.Revision != checked(command.ExpectedRevision + 1) ||
                !SameLifecyclePolicy(saved.Policy, command.Policy!)) throw new InvalidDataException("payload_policy_result_invalid");
            saved = saved with { Policy = CortexPayloadLifecycleSettingsStore.Validate(saved.Policy) };
            committed = true;
            CancellationTokenSource? cancel;
            lock (_gate)
            {
                _lifecycleSaved = saved; _lifecyclePaused = false; _lifecycleIssue = null;
                _lifecycleSelectedPolicyVerified = true;
                _lifecycleSettingsEpoch++;
                _lifecycleState = saved.Policy.Enabled ? "waiting" : "disabled";
                _lifecycleNamespaceIndex = 0; _lifecycleCursors.Clear();
                _lifecycleNext = _lifecycleClock.GetUtcNow();
                cancel = _lifecyclePassCancellation;
            }
            // A policy change can stop a pass, but does not detach it or certify
            // that an already-confirmed commit did not occur.
            if (cancel is not null)
                try { await cancel.CancelAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { /* The original already settled. */ }
            lock (_gate)
            {
                if (Authorized(_owner)) receipt = LifecycleReply(request, LifecycleStatusLocked(), null, pending.Jid);
                else
                {
                    _lifecyclePaused = true; _lifecycleIssue = "auth";
                    receipt = new(false, pending.Jid, request.Rid!, "failed", Operation, "auth", Partial: true);
                }
            }
        }
        catch (CortexPayloadLifecycleSettingsPublicationException error)
        {
            receipt = await HandlePublicationFailureAsync(pending, request, command, error, attempted).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            var knownRefusal = error is ArgumentException || error is InvalidOperationException &&
                error.Message is "payload_policy_changed" or "auth";
            var unknown = attempted && !committed && !knownRefusal;
            var code = committed ? "payload_policy_saved_cancel_failed" : error is InvalidOperationException &&
                error.Message is "payload_policy_changed" or "auth" ? error.Message : unknown ? "payload_policy_save_unverified" : "payload_policy_save_failed";
            CancellationTokenSource? failedCancel;
            lock (_gate)
            {
                failedCancel = unknown ? _lifecyclePassCancellation : null;
                if (committed || unknown) { _lifecyclePaused = true; _lifecycleIssue = code; }
                if (unknown) { _lifecycleSaved = null; _lifecycleSelectedPolicyVerified = false; _lifecycleSettingsEpoch++; }
            }
            if (failedCancel is not null)
                try { await failedCancel.CancelAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { }
                catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
                { code = "payload_policy_save_unverified_cancel_failed"; }
            lock (_gate)
            {
                if (committed || unknown) _lifecycleIssue = code;
                receipt = code == "auth"
                    ? new(false, pending.Jid, request.Rid!, "failed", Operation, "auth", Partial: committed)
                    : LifecycleReply(request, LifecycleStatusLocked(), code, pending.Jid, partial: committed || unknown, unknown: unknown);
            }
        }
        lock (_gate)
        {
            _configurationLast = (pending.Rid, pending.Identity, receipt);
            if (ReferenceEquals(_configurationCurrent, pending)) _configurationCurrent = null;
        }
        pending.Completion.TrySetResult(receipt);
    }

    private async Task<OperationReceipt> HandlePublicationFailureAsync(Pending pending, OperationRequest request,
        LifecycleCommand command, CortexPayloadLifecycleSettingsPublicationException failure, bool attempted)
    {
        var allowedCode = failure.Code is "payload_policy_cleanup_required" or "payload_policy_recovery_required";
        var actual = TrySelectedPolicy(failure.ConfirmedSettings, out var selected);
        var cleanup = allowedCode && failure.Code == "payload_policy_cleanup_required" && actual && attempted;
        var applied = cleanup && selected!.Revision == checked(command.ExpectedRevision + 1) &&
            SameLifecyclePolicy(selected!.Policy, command.Policy!);
        var unchanged = cleanup && selected!.Revision == command.ExpectedRevision;
        var known = applied || unchanged;
        var issue = known ? "payload_policy_cleanup_required" : "payload_policy_recovery_required";
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            _lifecycleSettingsEpoch++;
            if (known)
            {
                if (_lifecycleSaved is null || _lifecycleSaved.Revision <= selected!.Revision)
                    _lifecycleSaved = selected;
                _lifecycleSelectedPolicyVerified = true;
                if (applied)
                {
                    _lifecycleNamespaceIndex = 0; _lifecycleCursors.Clear();
                    _lifecycleNext = _lifecycleClock.GetUtcNow();
                }
            }
            else { _lifecycleSaved = null; _lifecycleSelectedPolicyVerified = false; }
            _lifecyclePaused = true; _lifecycleIssue = issue;
            _lifecycleState = known ? "paused" : "settings_unavailable";
            cancel = _lifecyclePassCancellation;
        }
        var cancelFailed = false;
        if (cancel is not null)
            try { await cancel.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* The original already settled. */ }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            { cancelFailed = true; }
        lock (_gate)
        {
            if (!Authorized(_owner))
            {
                _lifecycleIssue = "auth";
                return new(false, pending.Jid, request.Rid!, "failed", Operation, "auth", Partial: applied || !known);
            }
            var result = LifecycleReply(request, LifecycleStatusLocked(), issue, pending.Jid,
                partial: applied || !known, unknown: !known);
            result = WithPublicationEvidence(result, known ? applied : null,
                known && _lifecycleSelectedPolicyVerified && _lifecycleSaved is { Revision: > 0 });
            if (cancelFailed)
            {
                var data = result.Data!.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                data["cancellation_signal_failed"] = true;
                result = result with { Data = new ReadOnlyDictionary<string, object?>(data) };
            }
            return result;
        }
    }

    private Task<OperationReceipt> StartLifecyclePassAsync(OperationRequest request, string identity,
        CancellationToken token, bool automatic)
    {
        Pending pending; ICortexPayloadTieringBackend backend; ICortexPayloadTieringRuntimeLifetime runtime;
        CortexPayloadLifecycleSettings saved; string ns; SqliteCortexPayloadRotationCursor? after;
        CancellationTokenSource cancel;
        lock (_gate)
        {
            if (TryAnyLifecycleCorrelation(request, identity, out var previous)) return previous;
            if (_current is not null) return Task.FromResult(Reply(request, "payload_control_busy"));
            if (_closing) return Task.FromResult(Reply(request, "host_stopping"));
            if (_unavailable || _backend is null || _runtime is null || _lifecycleSaved is null)
                return Task.FromResult(LifecycleReply(request, LifecycleStatusLocked(), "payload_lifecycle_unavailable"));
            if (!_lifecycleSaved.Policy.Enabled)
                return Task.FromResult(LifecycleReply(request, LifecycleStatusLocked(), "payload_policy_disabled"));
            if (_lifecyclePaused)
                return Task.FromResult(LifecycleReply(request, LifecycleStatusLocked(), "payload_lifecycle_paused"));
            saved = _lifecycleSaved; backend = _backend; runtime = _runtime;
            ns = saved.Policy.Namespaces[_lifecycleNamespaceIndex % saved.Policy.Namespaces.Count];
            _lifecycleCursors.TryGetValue(ns, out after);
            cancel = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
            _lifecyclePassCancellation = cancel;
            pending = new(request.Rid!, identity); _current = pending;
            _lifecycleRunning = true; _lifecycleNamespace = ns; _lifecycleStarted = _lifecycleClock.GetUtcNow();
            _lifecycleFinished = null; _lifecycleEvicted = null; _lifecycleSelected = null; _lifecycleTarget = null;
            _lifecycleMetadata = null; _lifecycleImmutableObjectsMayRemain = false;
        }
        _ = RunLifecycleOwnedAsync(pending, request, saved, ns, after, backend, runtime, cancel, automatic);
        return pending.Completion.Task;
    }

    private async Task RunLifecycleOwnedAsync(Pending pending, OperationRequest request, CortexPayloadLifecycleSettings saved,
        string ns, SqliteCortexPayloadRotationCursor? after, ICortexPayloadTieringBackend backend,
        ICortexPayloadTieringRuntimeLifetime runtime, CancellationTokenSource cancel, bool automatic)
    {
        OperationReceipt receipt;
        string? outcome = null;
        try
        {
            receipt = await ExecuteMaintenanceAsync(pending, backend, runtime, async ct =>
            {
                var schema = await backend.RevalidateAsync(ct).ConfigureAwait(false);
                ObserveLifecycleSchema(schema);
                if (schema == 2) return new(null, "payload_schema_upgrade_required", true, true);
                if (schema != 3) return new(null, "payload_schema_unavailable", false);
                DateTimeOffset cutoff;
                try { cutoff = _lifecycleClock.GetUtcNow().ToUniversalTime().AddDays(-saved.Policy.MinimumArchiveAgeDays); }
                catch (ArgumentOutOfRangeException)
                { return new(null, "payload_archive_clock_range", true, true); }
                var requestValue = new SqliteCortexPayloadRotationRequest(1, ns, cutoff,
                    checked(saved.Policy.TargetHotMiB * 1024 * 1024), after);
                var value = await backend.RotateAsync(requestValue, saved.Policy.MaximumMetadataHeads,
                    saved.Policy.WorkMilliseconds, ct).ConfigureAwait(false);
                if (!ValidLifecycleRotation(value, ns, saved.Policy.MaximumMetadataHeads)) return new(null, "payload_result_invalid", false);
                outcome = value.State;
                lock (_gate)
                {
                    _lifecycleEvicted = value.Tiering?.HotBytesEvicted ?? (value.State == "no_selection" ? 0 : null);
                    _lifecycleSelected = value.State == "deferred_budget" ? null : value.Plan.Selection?.Records.Count ?? 0;
                    _lifecycleTarget = value.TargetSatisfied;
                    _lifecycleMetadata = value.MetadataObservations;
                    _lifecycleImmutableObjectsMayRemain = value.ImmutableObjectsMayRemain;
                    if (_lifecycleSaved?.Revision == saved.Revision && value.State != "deferred_budget" &&
                        value.ObservationIssue is null && value.Tiering?.CleanupIncomplete != true)
                    {
                        if (value.Plan.NextCursor is { } next) _lifecycleCursors[ns] = next;
                        else _lifecycleCursors.Remove(ns);
                    }
                }
                var issue = value.Tiering?.CleanupIncomplete == true ? "payload_cleanup_incomplete" :
                    value.State == "deferred_budget" ? null : value.ObservationIssue;
                return new(value.Tiering, issue, true, value.Tiering is null && !value.ImmutableObjectsMayRemain);
            }, cancel.Token, idleOnly: automatic).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { receipt = Result(pending, "payload_control_failed", null, null, false); }
        lock (_gate)
        {
            _lifecycleRunning = false; _lifecycleFinished = _lifecycleClock.GetUtcNow();
            if (ReferenceEquals(_lifecyclePassCancellation, cancel)) _lifecyclePassCancellation = null;
            if (_lifecycleSaved?.Revision == saved.Revision)
            {
                if (pending.Entered) _lifecycleNamespaceIndex = (_lifecycleNamespaceIndex + 1) % saved.Policy.Namespaces.Count;
                _lifecycleNext = _lifecycleClock.GetUtcNow().AddMinutes(saved.Policy.IntervalMinutes);
            }
            _lifecycleState = outcome ?? (receipt.Err == "work_active" || receipt.Err == "payload_control_busy" ? "deferred_busy" : "waiting");
            if (!receipt.Ok && receipt.Err is not ("work_active" or "payload_control_busy" or "payload_schema_upgrade_required"))
            { _lifecyclePaused = true; _lifecycleIssue = receipt.Err ?? "payload_lifecycle_outcome_unavailable"; }
            else if (!(_lifecyclePaused && IsPublicationReviewIssue(_lifecycleIssue)))
                _lifecycleIssue = receipt.Err == "payload_schema_upgrade_required" ? receipt.Err :
                    outcome == "deferred_budget" ? "payload_work_budget_exhausted" : null;
            if (receipt.Err != "auth") receipt = WithLifecycle(receipt, LifecycleStatusLocked());
        }
        cancel.Dispose();
        CompletePending(pending, receipt);
    }

    private void ReloadLifecycleSettings()
    {
        ICortexPayloadLifecycleSettingsStore? store;
        long epoch;
        lock (_gate) { store = _lifecycleSettings; epoch = _lifecycleSettingsEpoch; }
        if (store is null) return;
        try
        {
            var saved = store.Read();
            var policy = CortexPayloadLifecycleSettingsStore.Validate(saved.Policy);
            if (saved.Revision < 0 || saved.Revision == 0 &&
                !SameLifecyclePolicy(policy, CortexPayloadLifecycleSettingsStore.DefaultSettings.Policy))
                throw new InvalidDataException("payload_policy_invalid");
            lock (_gate)
            {
                if (epoch != _lifecycleSettingsEpoch) return;
                if (saved.Revision == 0 && IsPublicationReviewIssue(_lifecycleIssue))
                {
                    _lifecycleSaved = null; _lifecycleSelectedPolicyVerified = false; _lifecyclePaused = true;
                    _lifecycleIssue = "payload_policy_recovery_required"; _lifecycleState = "settings_unavailable";
                    return;
                }
                if (_lifecycleSaved is { } current && current.Revision > saved.Revision) return;
                _lifecycleSaved = saved with { Policy = policy };
                _lifecycleSelectedPolicyVerified = saved.Revision > 0;
                if (_lifecyclePaused && IsPublicationReviewIssue(_lifecycleIssue)) _lifecycleIssue = "payload_policy_review_required";
            }
        }
        catch (CortexPayloadLifecycleSettingsPublicationException error)
        {
            CortexPayloadLifecycleSettings? selected = null;
            var cleanup = error.Code == "payload_policy_cleanup_required" && TrySelectedPolicy(error.ConfirmedSettings, out selected);
            lock (_gate)
            {
                if (epoch != _lifecycleSettingsEpoch || cleanup && _lifecycleSaved is { } current && current.Revision > selected!.Revision) return;
                _lifecycleSaved = cleanup ? selected : null;
                _lifecycleSelectedPolicyVerified = cleanup;
                _lifecyclePaused = true; _lifecycleState = cleanup ? "paused" : "settings_unavailable";
                _lifecycleIssue = cleanup ? "payload_policy_cleanup_required" : "payload_policy_recovery_required";
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            lock (_gate)
            {
                if (epoch != _lifecycleSettingsEpoch) return;
                _lifecycleSaved = null; _lifecycleSelectedPolicyVerified = false; _lifecyclePaused = true;
                _lifecycleIssue = "payload_policy_unavailable"; _lifecycleState = "settings_unavailable";
            }
        }
    }

    private void ObserveLifecycleSchema(int version)
    {
        lock (_gate)
        {
            var previous = _lifecycleSchema; _lifecycleSchema = version;
            if (version == 3 && previous == 2)
            {
                if (_lifecycleIssue == "payload_schema_upgrade_required") _lifecycleIssue = null;
                _lifecycleNext = _lifecycleClock.GetUtcNow();
            }
        }
    }

    private CortexPayloadLifecycleStatus LifecycleStatusLocked()
    {
        var state = _closing ? "stopping" : _lifecycleRunning
            ? _lifecycleSaved?.Policy.Enabled == false ? "stopping" : "running"
            : !_lifecycleBound ? "unobserved" : _lifecycleSaved is null ? "settings_unavailable"
            : !_lifecycleSaved.Policy.Enabled ? "disabled" : _lifecyclePaused ? "paused"
            : _lifecycleSchema == 2 ? "requires_explicit_upgrade" : _lifecycleSchema is null ? "schema_unobserved" : _lifecycleState;
        return new("pegasus.cortex.payload-lifecycle-status/1", _lifecycleSaved,
            _bound && !_unavailable, _lifecycleSchema, _lifecycleRunning, state, _lifecycleIssue,
            _lifecycleNamespace, _lifecycleStarted, _lifecycleFinished, _lifecycleEvicted,
            _lifecycleSelected, _lifecycleTarget, _lifecycleMetadata, _lifecycleImmutableObjectsMayRemain);
    }

    private bool TryAnyLifecycleCorrelation(OperationRequest request, string identity, out Task<OperationReceipt> result)
    {
        if (TryConfigurationCorrelation(request.Rid!, identity, out result)) return true;
        if (_current is { } current && SameRid(current.Rid, request.Rid!))
        { result = current.Identity == identity ? CorrelateAsync(current.Completion.Task, request.Rid!) : Task.FromResult(Reply(request, "precondition")); return true; }
        if (_last is { } last && SameRid(last.Rid, request.Rid!))
        { result = Task.FromResult(last.Identity == identity ? last.Receipt with { Rid = request.Rid! } : Reply(request, "precondition")); return true; }
        result = null!; return false;
    }

    private bool TryConfigurationCorrelation(string rid, string identity, out Task<OperationReceipt> result)
    {
        if (_configurationCurrent is { } current && SameRid(current.Rid, rid))
        { result = current.Identity == identity ? CorrelateAsync(current.Completion.Task, rid) : Task.FromResult(CorrelationRefusal(rid)); return true; }
        if (_configurationLast is { } last && SameRid(last.Rid, rid))
        { result = Task.FromResult(last.Identity == identity ? last.Receipt with { Rid = rid } : CorrelationRefusal(rid)); return true; }
        result = null!; return false;
    }

    private static OperationReceipt CorrelationRefusal(string rid) =>
        new(false, Guid.NewGuid().ToString("N"), rid, "failed", Operation, "precondition");
    private static OperationReceipt LifecycleReply(OperationRequest request, CortexPayloadLifecycleStatus status,
        string? error, string? jid = null, bool partial = false, bool unknown = false) =>
        new(error is null, jid ?? Guid.NewGuid().ToString("N"), request.Rid!, error is null ? "done" : unknown ? "recovery_required" : "failed", Operation,
            error, new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>
                { ["lifecycle"] = status, ["outcome_unknown"] = unknown }), Partial: partial);
    private static OperationReceipt WithLifecycle(OperationReceipt receipt, CortexPayloadLifecycleStatus status)
    {
        var data = receipt.Data is null ? new Dictionary<string, object?>() : receipt.Data.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        data["lifecycle"] = status;
        return receipt with { Data = new ReadOnlyDictionary<string, object?>(data) };
    }

    private static bool SameLifecyclePolicy(CortexPayloadLifecyclePolicy left, CortexPayloadLifecyclePolicy right) =>
        JsonSerializer.Serialize(left, LifecycleJson) == JsonSerializer.Serialize(right, LifecycleJson);

    private static bool IsPublicationReviewIssue(string? issue) => issue is "payload_policy_cleanup_required" or
        "payload_policy_recovery_required" or "payload_policy_review_required" or "payload_policy_save_unverified" or
        "payload_policy_save_unverified_cancel_failed" or "payload_policy_saved_cancel_failed";

    private static bool TrySelectedPolicy(CortexPayloadLifecycleSettings? candidate, out CortexPayloadLifecycleSettings? verified)
    {
        verified = null;
        if (candidate is null || candidate.Revision < 1) return false;
        try { verified = candidate with { Policy = CortexPayloadLifecycleSettingsStore.Validate(candidate.Policy) }; return true; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return false; }
    }

    private static OperationReceipt WithPublicationEvidence(OperationReceipt receipt, bool? applied, bool verified)
    {
        var data = receipt.Data is null ? new Dictionary<string, object?>() : receipt.Data.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        data["selected_policy_verified"] = verified;
        if (applied is { } value) data["policy_save_applied"] = value;
        return receipt with { Data = new ReadOnlyDictionary<string, object?>(data) };
    }

    private static bool ValidLifecycleRotation(SqliteCortexPayloadRotationReceipt value, string ns, int maximumMetadataHeads)
    {
        if (value is null || value.Version != 1 || value.Plan is null || value.Plan.Version != 1 || value.Plan.Namespace != ns ||
            value.MetadataObservations is not { } observations || observations < 0 || observations > maximumMetadataHeads ||
            value.ObservedHotCompressedBytesAfter is < 0 || value.State is not
                ("rotated" or "rotated_cleanup_incomplete" or "no_selection" or "deferred_budget")) return false;
        if (value.State == "deferred_budget") return value.Tiering is null && value.Plan.Selection is null &&
            value.ObservedHotCompressedBytesAfter is null && value.TargetSatisfied is null &&
            value.ObservationIssue == "payload_work_budget_exhausted";
        if (value.Plan.ScannedHeads is not (>= 0 and <= 256) || value.Plan.ProtectedRecords is not (>= 0 and <= 256) ||
            value.Plan.ReferenceProtectedRecords is not (>= 0 and <= 16) || value.Plan.OverBoundRecords is not (>= 0 and <= 256) ||
            value.Plan.HotCompressedBytes is not (>= 0))
            return false;
        if (value.State == "no_selection") return value.Tiering is null && value.Plan.Selection is null;
        var tiering = value.Tiering;
        return tiering is not null && tiering.Version == 1 && tiering.SchemaVersion == 3 && tiering.State == "offloaded" &&
            tiering.RecordMetadataUnchanged && tiering.PhysicalBytesReclaimed == 0 && tiering.HotBytesRestored == 0 &&
            tiering.SelectedRecords is >= 1 and <= 16 && tiering.SelectedRecords == value.Plan.Selection?.Records.Count &&
            tiering.PayloadsChanged is >= 0 and <= 256 && tiering.SharedPayloadsKeptHot is >= 0 and <= 256 &&
            tiering.HotBytesEvicted is >= 0 and <= 16 * 1024 * 1024 &&
            tiering.CleanupIncomplete == (value.State == "rotated_cleanup_incomplete") &&
            value.Plan.Selection!.Records.All(record => record.Namespace == ns);
    }

    internal static LifecycleCommand ValidateLifecycleRequest(OperationRequest request)
    {
        if (request.Op != Operation || request.Arg is null || request.Arg.Count != 0 || request.Root is not null ||
            request.Ws is not null || request.Cwd is not null || request.PathRoots is not null || request.Quiet || request.Detail ||
            !Guid.TryParseExact(request.Rid, "N", out _) || request.Body is null || request.Body.Length > MaximumBodyBytes ||
            Encoding.UTF8.GetByteCount(request.Body) > MaximumBodyBytes) throw Invalid();
        try
        {
            using var json = JsonDocument.Parse(request.Body, new JsonDocumentOptions { MaxDepth = 8 });
            RequireUniqueLifecycleNames(json.RootElement);
            var action = json.RootElement.GetProperty("action").GetString();
            string[] allowed = action == "lifecycle_save" ? ["version", "action", "expectedPolicyRevision", "policy"] : ["version", "action"];
            var fields = Fields(json.RootElement, allowed);
            if (fields.Count != allowed.Length || Integer(fields, "version") != 1 ||
                action is not ("lifecycle_status" or "lifecycle_save" or "lifecycle_run")) throw Invalid();
            CortexPayloadLifecyclePolicy? policy = null; long revision = 0;
            if (action == "lifecycle_save")
            {
                if (!fields["expectedPolicyRevision"].TryGetInt64(out revision) || revision < 0 || revision == long.MaxValue) throw Invalid();
                var p = fields["policy"];
                if (Fields(p, ["version", "enabled", "namespaces", "minimumArchiveAgeDays", "targetHotMiB", "intervalMinutes", "workMilliseconds", "maximumMetadataHeads"]).Count != 8) throw Invalid();
                policy = CortexPayloadLifecycleSettingsStore.Validate(p.Deserialize<CortexPayloadLifecyclePolicy>(LifecycleJson) ?? throw Invalid());
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { action, expectedPolicyRevision = revision, policy }, LifecycleJson);
            return new(action!, policy, revision, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw Invalid(); }
    }

    private static void RequireUniqueLifecycleNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw Invalid(); RequireUniqueLifecycleNames(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RequireUniqueLifecycleNames(item);
    }

    internal sealed record LifecycleCommand(string Action, CortexPayloadLifecyclePolicy? Policy,
        long ExpectedRevision, string Identity)
    { public override string ToString() => "PayloadLifecycleCommand(Content=omitted)"; }
    private sealed class LifecycleTicker(TimeProvider clock) : ICortexPayloadLifecycleTicker
    {
        private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(1), clock);
        public ValueTask<bool> WaitAsync(CancellationToken token) => _timer.WaitForNextTickAsync(token);
        public ValueTask DisposeAsync() { _timer.Dispose(); return ValueTask.CompletedTask; }
    }
}
