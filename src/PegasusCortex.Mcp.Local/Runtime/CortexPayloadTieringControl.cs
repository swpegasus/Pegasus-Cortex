using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

/// <summary>Product-owned lifetime, not caller input. StopIngress irreversibly fences semantics;
/// the nonfaulting acknowledgement completes only after actual ingress/callback drain.
/// Signal it BEFORE joining this controller, avoiding a teardown/control dependency cycle.</summary>
internal interface ICortexPayloadTieringRuntimeLifetime
{
    void StopIngress();
    Task WhenIngressDrainedAsync();
}
internal interface ICortexPayloadTieringEnvironment
{
    LocalPeer Current { get; }
    void RequireMaintenanceOpen();
    Task<string?> InspectWorkAsync(CancellationToken token);
}
internal interface ICortexPayloadTieringBackend
{
    ValueTask<SqliteCortexTieringReceipt> ExecuteAsync(SqliteCortexPayloadMaintenanceAction action,
        int expectedSchemaVersion, SqliteCortexTieringRequest? selection, CancellationToken token);
    ValueTask<int> RevalidateAsync(CancellationToken token);
    ValueTask<SqliteCortexPayloadRotationReceipt> RotateAsync(SqliteCortexPayloadRotationRequest request,
        int maximumMetadataHeads, int workMilliseconds, CancellationToken token) =>
        ValueTask.FromException<SqliteCortexPayloadRotationReceipt>(new NotSupportedException("payload_rotation_unavailable"));
}
internal sealed record CortexPayloadTieringCommand(SqliteCortexPayloadMaintenanceAction Action,
    int ExpectedSchemaVersion, SqliteCortexTieringRequest? Selection, string Identity)
{
    public override string ToString() => "CortexPayloadTieringCommand(Content=omitted)";
}

/// <summary>Fixed authenticated owner control, deliberately OUTSIDE ordinary admission.
/// One current/last operation only; RID is correlation, not durable replay protection.</summary>
internal sealed partial class CortexPayloadTieringControl : IAsyncDisposable
{
    internal const string Operation = "cortex_payload_tiering";
    internal const int MaximumBodyBytes = 16 * 1024;
    private readonly LocalPeer _owner;
    private readonly UserHostAdmission _admission;
    private readonly ICortexPayloadTieringEnvironment _environment;
    private readonly TimeSpan _drainTimeout;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private ICortexPayloadTieringBackend? _backend;
    private ICortexPayloadTieringRuntimeLifetime? _runtime;
    private bool _bound, _closing, _unavailable;
    private Pending? _current;
    private (string Rid, string Identity, OperationReceipt Receipt)? _last;
    private TaskCompletionSource? _closed;

    internal CortexPayloadTieringControl(LocalPeer owner, UserHostAdmission admission,
        AgentOpsEngine engine, IMaintenanceModeSource maintenance, TimeSpan? drainTimeout = null)
        : this(owner, admission, new Environment(engine, maintenance), drainTimeout) { }
    internal CortexPayloadTieringControl(LocalPeer owner, UserHostAdmission admission,
        ICortexPayloadTieringEnvironment environment, TimeSpan? drainTimeout = null)
    {
        _owner = owner; _admission = admission; _environment = environment;
        _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(5);
        if (_drainTimeout <= TimeSpan.Zero || _drainTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(drainTimeout));
    }

    internal void BindCurrent(SqliteCortexV2Store store, ICortexPayloadTieringRuntimeLifetime runtime,
        string? configDirectory = null)
    {
        BindCurrent(new Backend(store ?? throw new ArgumentNullException(nameof(store))), runtime);
        if (configDirectory is not null) BindOptionalLifecycleDirectory(configDirectory);
    }
    // Typed dependency seam. Production binds the actual composition.V2Store above;
    // neither a database path nor an arbitrary callback is accepted on the wire.
    internal void BindCurrent(ICortexPayloadTieringBackend backend, ICortexPayloadTieringRuntimeLifetime runtime)
    {
        ArgumentNullException.ThrowIfNull(backend); ArgumentNullException.ThrowIfNull(runtime);
        lock (_gate)
        {
            if (_bound || _closing) throw new InvalidOperationException("payload_control_binding_unavailable");
            _bound = true; _backend = backend; _runtime = runtime;
        }
    }

    internal Task<OperationReceipt> HandleAsync(OperationRequest request, LocalPeer peer, CancellationToken token)
    {
        if (!Authorized(peer)) return Task.FromResult(Reply(request, "auth"));
        if (IsLifecycleRequest(request)) return HandleLifecycleAsync(request, token);
        CortexPayloadTieringCommand command;
        try { command = ValidateRequest(request); }
        catch (ArgumentException) { return Task.FromResult(Reply(request, "invalid")); }
        if (token.IsCancellationRequested) return Task.FromResult(Reply(request, "canceled"));
        Pending pending; ICortexPayloadTieringBackend backend; ICortexPayloadTieringRuntimeLifetime runtime;
        lock (_gate)
        {
            if (TryConfigurationCorrelation(request.Rid!, command.Identity, out var configuration)) return configuration;
            if (_current is { } active)
            {
                if (SameRid(active.Rid, request.Rid!))
                    return active.Identity == command.Identity ? CorrelateAsync(active.Completion.Task, request.Rid!) :
                        Task.FromResult(Reply(request, "precondition"));
                return Task.FromResult(Reply(request, "payload_control_busy"));
            }
            if (_last is { } last && SameRid(last.Rid, request.Rid!))
                return Task.FromResult(last.Identity == command.Identity ? last.Receipt with { Rid = request.Rid! } :
                    Reply(request, "precondition"));
            if (_closing) return Task.FromResult(Reply(request, "host_stopping"));
            if (_unavailable || _backend is null || _runtime is null) return Task.FromResult(Reply(request, "payload_store_unavailable"));
            backend = _backend; runtime = _runtime;
            pending = new(request.Rid!, command.Identity);
            _current = pending; // Publish ownership BEFORE any synchronous callback.
        }
        _ = RunOwnedAsync(pending, request, command, backend, runtime, token);
        return pending.Completion.Task;
    }

    private async Task RunOwnedAsync(Pending pending, OperationRequest request, CortexPayloadTieringCommand command,
        ICortexPayloadTieringBackend backend, ICortexPayloadTieringRuntimeLifetime runtime, CancellationToken token)
    {
        OperationReceipt receipt;
        try { receipt = await ExecuteAsync(pending, request, command, backend, runtime, token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { receipt = Result(pending, "payload_control_failed", null, null, false); }
        CompletePending(pending, receipt);
    }

    private Task<OperationReceipt> ExecuteAsync(Pending pending, OperationRequest request, CortexPayloadTieringCommand command,
        ICortexPayloadTieringBackend backend, ICortexPayloadTieringRuntimeLifetime runtime, CancellationToken token) =>
        ExecuteMaintenanceAsync(pending, backend, runtime, async ct =>
        {
            var value = await backend.ExecuteAsync(command.Action, command.ExpectedSchemaVersion, command.Selection, ct).ConfigureAwait(false);
            return ValidReceipt(command, value)
                ? new MaintenanceObservation(value, value.CleanupIncomplete ? "payload_cleanup_incomplete" : null, true)
                : new MaintenanceObservation(null, "payload_result_invalid", false);
        }, token);

    // The only callers below are fixed typed owner operations. No callback or
    // database choice is supplied by a wire caller.
    private async Task<OperationReceipt> ExecuteMaintenanceAsync(Pending pending,
        ICortexPayloadTieringBackend backend, ICortexPayloadTieringRuntimeLifetime runtime,
        Func<CancellationToken, ValueTask<MaintenanceObservation>> execute, CancellationToken token, bool idleOnly = false)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        UserHostAdmission.CloseLease? closing = null;
        SqliteCortexTieringReceipt? observed = null;
        int? schema = null;
        string? error = null;
        var stopped = false;
        try
        {
            if (!Authorized(_owner)) return Result(pending, "auth", null, null, false);
            RequireOpen();
            if (idleOnly && _admission.ActiveCount != 0) return Result(pending, "work_active", null, null, false);
            closing = _admission.TryClose();
            if (closing is null) return Result(pending, "payload_control_busy", null, null, false);
            using (var drain = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token))
            {
                drain.CancelAfter(_drainTimeout);
                while (true)
                {
                    if (idleOnly && _admission.ActiveCount != 0) return Result(pending, "work_active", null, null, false);
                    await closing.WhenIdleAsync(drain.Token).ConfigureAwait(false);
                    if (!Authorized(_owner)) return Result(pending, "auth", null, null, false);
                    RequireOpen();
                    var blocker = await _environment.InspectWorkAsync(drain.Token).ConfigureAwait(false);
                    if (blocker is null) break;
                    if (blocker != "work_active") return Result(pending, "payload_work_unavailable", null, null, false);
                    if (idleOnly) return Result(pending, "work_active", null, null, false);
                    await Task.Delay(25, drain.Token).ConfigureAwait(false);
                }
            }
            cancel.Token.ThrowIfCancellationRequested();
            if (!Authorized(_owner)) return Result(pending, "auth", null, null, false);
            RequireOpen();
            pending.Entered = true;
            try
            {
                // Retain the ORIGINAL backend task through cancellation, rollback
                // and payload-lease cleanup. No observer timeout detaches it.
                var value = await execute(cancel.Token).ConfigureAwait(false);
                observed = value.Tiering;
                pending.OutcomeObserved = value.Confirmed;
                pending.KnownNoEffects = value.KnownNoEffects;
                error = value.Error;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
            { error = failure is OperationCanceledException ? "canceled" : "payload_operation_failed"; }

            // A failed/ambiguous commit or canceled reply cannot bypass resident
            // schema admission. Caller cancellation is deliberately not used here.
            try
            {
                var version = await backend.RevalidateAsync(CancellationToken.None).ConfigureAwait(false);
                if (version is not (2 or 3) || observed is { } confirmed && version != confirmed.SchemaVersion)
                    throw new InvalidDataException("payload_schema_unavailable");
                schema = version;
                ObserveLifecycleSchema(version);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
            { error = "payload_schema_unavailable"; }
            var ownerPresent = Authorized(_owner);
            if (schema is null || !ownerPresent)
            {
                lock (_gate) { _unavailable = true; _backend = null; }
                // The runtime's fixed implementation latches unavailability first.
                // Its drain ACK must precede its join of this very control task.
                try { runtime.StopIngress(); }
                catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
                { pending.StopRequestFailed = true; error ??= "payload_semantic_stop_failed"; }
                // Even a stop-request exception cannot bypass the actual drain proof.
                await runtime.WhenIngressDrainedAsync().ConfigureAwait(false);
                stopped = true;
                if (!ownerPresent) { error = "auth"; observed = null; pending.LifecycleStatus = null; }
            }
            return Result(pending, error, observed, schema, stopped);
        }
        catch (OperationCanceledException)
        { return Result(pending, token.IsCancellationRequested ? "canceled" : _shutdown.IsCancellationRequested ? "host_stopping" : "work_active", observed, schema, stopped); }
        catch (AgentOpsException failure)
        {
            var code = failure.Code is "maintenance_busy" or "maintenance_state_unavailable" ? failure.Code : "payload_control_failed";
            return Result(pending, code, observed, schema, stopped);
        }
        finally { closing?.Dispose(); } // No permanent Commit: PAO and normal Exit remain available.
    }

    private bool Authorized(LocalPeer peer)
    {
        try
        {
            var current = _environment.Current;
            return Ordinary(_owner) && Ordinary(peer) && Ordinary(current) &&
                peer.SameIdentity(_owner) && current.SameIdentity(_owner) &&
                _owner.ApplicationPath is { } expected && peer.ApplicationPath is { } actual &&
                current.ApplicationPath is { } process &&
                Path.GetFullPath(actual).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFullPath(process).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return false; }
    }
    private static bool Ordinary(LocalPeer peer) => peer.Session > 0 && !peer.Elevated &&
        peer.Sid is not ("S-1-5-18" or "S-1-5-19" or "S-1-5-20");
    private void RequireOpen()
    {
        try { _environment.RequireMaintenanceOpen(); }
        catch (AgentOpsException) { throw; }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        { throw new AgentOpsException("maintenance_state_unavailable"); }
    }

    internal Task CloseAndDrainAsync()
    {
        TaskCompletionSource completion; Task active;
        lock (_gate)
        {
            if (_closed is not null) return _closed.Task;
            _closing = true; _backend = null;
            completion = _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            active = Task.WhenAll(_current?.Completion.Task ?? Task.CompletedTask,
                _configurationCurrent?.Completion.Task ?? Task.CompletedTask, _lifecycleLoop);
        }
        _ = FinishCloseAsync(active, completion);
        return completion.Task;
    }
    private async Task FinishCloseAsync(Task active, TaskCompletionSource completion)
    {
        try
        {
            try { await _shutdown.CancelAsync().ConfigureAwait(false); }
            finally { await active.ConfigureAwait(false); }
            _shutdown.Dispose(); completion.TrySetResult();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        { completion.TrySetException(new InvalidOperationException("payload_control_close_failed")); }
    }
    public ValueTask DisposeAsync() => new(CloseAndDrainAsync());
    private static async Task<OperationReceipt> CorrelateAsync(Task<OperationReceipt> original, string rid) =>
        (await original.ConfigureAwait(false)) with { Rid = rid };
    private static bool SameRid(string first, string second) => StringComparer.OrdinalIgnoreCase.Equals(first, second);
    private static OperationReceipt Reply(OperationRequest request, string code) =>
        new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"),
            code == "canceled" ? "canceled" : "failed", Operation, code);
    private static OperationReceipt Result(Pending pending, string? error, SqliteCortexTieringReceipt? observed, int? schema, bool stopped)
    {
        var unknown = pending.Entered && !pending.OutcomeObserved;
        var data = new Dictionary<string, object?>
        {
            ["operation_entered"] = pending.Entered, ["outcome_unknown"] = unknown,
            ["schema_revalidated"] = schema is not null, ["semantic_ingress_stopped"] = stopped,
            ["semantic_stop_request_failed"] = pending.StopRequestFailed,
            ["rid_replay_scope"] = "current_or_last_in_process_only"
        };
        if (error is not null) data["control_code"] = error;
        if (schema is { } version) data["schema_version"] = version;
        if (observed is not null) data["tiering"] = observed;
        if (pending.LifecycleStatus is not null) data["lifecycle"] = pending.LifecycleStatus;
        return new(error is null, pending.Jid, pending.Rid,
            error is null ? "done" : unknown ? "recovery_required" : error == "canceled" ? "canceled" : "failed",
            Operation, unknown ? "recovery_required" : error, new ReadOnlyDictionary<string, object?>(data),
            Partial: error is not null && pending.Entered && !pending.KnownNoEffects);
    }
    private static bool ValidReceipt(CortexPayloadTieringCommand command, SqliteCortexTieringReceipt value) =>
        value is not null && value.Version == 1 && value.SchemaVersion == 3 && value.RecordMetadataUnchanged &&
        value.SelectedRecords == (command.Selection?.Records.Count ?? 0) && value.PayloadsChanged is >= 0 and <= 256 &&
        value.SharedPayloadsKeptHot is >= 0 and <= 256 && value.HotBytesEvicted is >= 0 and <= 16 * 1024 * 1024 &&
        value.HotBytesRestored is >= 0 and <= 16 * 1024 * 1024 && value.PhysicalBytesReclaimed == 0 &&
        (command.Action switch
        {
            SqliteCortexPayloadMaintenanceAction.Upgrade => (value.State is "upgraded" or "already_upgraded") &&
                value.PayloadsChanged == 0 && value.SharedPayloadsKeptHot == 0 && value.HotBytesEvicted == 0 && value.HotBytesRestored == 0,
            SqliteCortexPayloadMaintenanceAction.Offload => value.State == "offloaded" && value.HotBytesRestored == 0,
            _ => value.State == "rehydrated" && value.HotBytesEvicted == 0
        });

    internal static CortexPayloadTieringCommand ValidateRequest(OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Op != Operation || request.Arg is null || request.Arg.Count != 0 || request.Root is not null ||
            request.Ws is not null || request.Cwd is not null || request.PathRoots is not null || request.Quiet || request.Detail ||
            !Guid.TryParseExact(request.Rid, "N", out _) || request.Body is null ||
            request.Body.Length > MaximumBodyBytes || Encoding.UTF8.GetByteCount(request.Body) > MaximumBodyBytes)
            throw new ArgumentException("payload_request_invalid");
        try
        {
            using var json = JsonDocument.Parse(request.Body, new JsonDocumentOptions { MaxDepth = 4 });
            var fields = Fields(json.RootElement, ["version", "action", "expectedSchemaVersion", "records"]);
            if (Integer(fields, "version") != 1) throw Invalid();
            var action = Text(fields, "action") switch
            {
                "upgrade" => SqliteCortexPayloadMaintenanceAction.Upgrade,
                "offload" => SqliteCortexPayloadMaintenanceAction.Offload,
                "rehydrate" => SqliteCortexPayloadMaintenanceAction.Rehydrate,
                _ => throw Invalid()
            };
            var schema = Integer(fields, "expectedSchemaVersion");
            SqliteCortexTieringRequest? selection = null;
            if (action == SqliteCortexPayloadMaintenanceAction.Upgrade)
            {
                if (schema != 2 || fields.ContainsKey("records")) throw Invalid();
            }
            else
            {
                if (schema != 3 || !fields.TryGetValue("records", out var records) || records.ValueKind != JsonValueKind.Array ||
                    records.GetArrayLength() is < 1 or > 16) throw Invalid();
                var frozen = new List<SqliteCortexTieringCoordinate>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var record in records.EnumerateArray())
                {
                    var values = Fields(record, ["namespace", "kind", "recordId", "expectedHeadRevision", "expectedHeadHash"]);
                    if (values.Count != 5) throw Invalid();
                    var ns = Identifier(Text(values, "namespace")); var kind = Text(values, "kind");
                    var id = Identifier(Text(values, "recordId")); var hash = Text(values, "expectedHeadHash");
                    if (kind is not ("task" or "knowledge") || hash.Length != 64 ||
                        hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                        !values.TryGetValue("expectedHeadRevision", out var revision) || revision.ValueKind != JsonValueKind.Number ||
                        !revision.TryGetInt64(out var number) || number < 1 || !seen.Add(ns + "\n" + kind + "\n" + id)) throw Invalid();
                    frozen.Add(new(ns, kind, id, number, hash));
                }
                selection = new(1, Array.AsReadOnly(frozen.ToArray()));
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, action, expectedSchemaVersion = schema, selection });
            try { return new(action, schema, selection, Convert.ToHexStringLower(SHA256.HashData(bytes))); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or OverflowException)
        { throw Invalid(); }
    }
    private static Dictionary<string, JsonElement> Fields(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !result.TryAdd(property.Name, property.Value)) throw Invalid();
        return result;
    }
    private static string Text(Dictionary<string, JsonElement> fields, string field) =>
        fields.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid();
    private static int Integer(Dictionary<string, JsonElement> fields, string field) =>
        fields.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw Invalid();
    private static string Identifier(string value)
    {
        if (value.Length is < 1 or > CortexV2Limits.MaximumIdentifierCharacters || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or ':' or '-'))) throw Invalid();
        return value;
    }
    private static ArgumentException Invalid() => new("payload_request_invalid");
    private sealed class Pending(string rid, string identity)
    {
        internal string Rid { get; } = rid;
        internal string Identity { get; } = identity;
        internal string Jid { get; } = Guid.NewGuid().ToString("N");
        internal bool Entered, OutcomeObserved, StopRequestFailed, KnownNoEffects;
        internal CortexPayloadLifecycleStatus? LifecycleStatus;
        internal TaskCompletionSource<OperationReceipt> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Environment(AgentOpsEngine engine, IMaintenanceModeSource maintenance) : ICortexPayloadTieringEnvironment
    {
        public LocalPeer Current => LocalIdentity.Current();
        public void RequireMaintenanceOpen() => UserHostStartup.RequireOpen(maintenance);
        public Task<string?> InspectWorkAsync(CancellationToken token) => engine.UserExitBlockerAsync(token);
    }
    private sealed class Backend(SqliteCortexV2Store store) : ICortexPayloadTieringBackend
    {
        public ValueTask<SqliteCortexTieringReceipt> ExecuteAsync(SqliteCortexPayloadMaintenanceAction action,
            int expectedSchemaVersion, SqliteCortexTieringRequest? selection, CancellationToken token) =>
            store.ExecuteTrustedPayloadMaintenanceAsync(action, expectedSchemaVersion, selection, token);
        public ValueTask<int> RevalidateAsync(CancellationToken token) => store.RevalidatePayloadSchemaAdmissionAsync(token);
        public ValueTask<SqliteCortexPayloadRotationReceipt> RotateAsync(SqliteCortexPayloadRotationRequest request,
            int maximumMetadataHeads, int workMilliseconds, CancellationToken token) =>
            store.ExecuteTrustedPayloadRotationAsync(request, maximumMetadataHeads, workMilliseconds, token);
    }

    private sealed record MaintenanceObservation(SqliteCortexTieringReceipt? Tiering, string? Error,
        bool Confirmed, bool KnownNoEffects = false);

    private void CompletePending(Pending pending, OperationReceipt receipt)
    {
        lock (_gate)
        {
            if (receipt.Partial || receipt.State == "recovery_required")
            { _lifecyclePaused = true; _lifecycleIssue = receipt.Err ?? "payload_lifecycle_outcome_unavailable"; }
            _last = (pending.Rid, pending.Identity, receipt);
            if (ReferenceEquals(_current, pending)) _current = null;
        }
        pending.Completion.TrySetResult(receipt);
    }
}
