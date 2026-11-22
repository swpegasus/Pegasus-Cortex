using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

internal sealed record OwnerAdminInput(string Path, string Sha256, string Kind = "code");

// A reusable administrator-reviewed scope, separate from User tool registration.
// Scopes describe reviewed effects; a Windows administrator token is not a sandbox.
internal sealed record OwnerAdminProgram(string Path, string Sha256, bool AllowRevisions = false);
internal sealed record OwnerAdminAuthorization(
    int Version, string Id, string TaskId, string OwnerSid, int OwnerSession,
    string ClientApplicationPath, string ClientSha256, string OwnerProfile,
    DateTimeOffset ExpiresAtUtc, IReadOnlyList<OwnerAdminProgram> Programs,
    IReadOnlyList<OwnerAdminProgram> Scripts, IReadOnlyList<string> InputRoots,
    IReadOnlyList<string> ReadRoots, IReadOnlyList<string> WriteRoots,
    IReadOnlyList<string> DeclaredEffects, bool AllowUnconfinedAdministratorEffects,
    bool AllowMaintenanceIntervals = false, bool AllowOrdinaryQuiescenceAcknowledgements = false);

internal sealed record OwnerAdminDefinition(
    int Version, string Id, string TaskId, string OwnerSid, int OwnerSession,
    string ClientApplicationPath, string ClientSha256, string OwnerProfile,
    string ExpectedRid, DateTimeOffset ExpiresAtUtc, string Action,
    string ExecutablePath, string ExecutableSha256, string WorkingDirectory,
    IReadOnlyList<string> Arguments, IReadOnlyList<OwnerAdminInput> Inputs,
    IReadOnlyList<string> ReadRoots, IReadOnlyList<string> WriteRoots,
    IReadOnlyList<string> DeclaredEffects, bool NonSecretInputs,
    string? ScriptPath = null, string? ScriptSha256 = null, string? MaintenanceEpoch = null);

internal interface IOwnerAdminProcess : IDisposable
{
    int Pid { get; }
    string Start { get; }
    void Resume();
    bool TryGetExit(out int exit);
}

internal interface IOwnerAdminLauncher
{
    bool IsElevatedOwner(LocalPeer peer);
    bool IsOriginalProcessAlive(int pid,string start);
    void ValidateProtectedInputs(OwnerAdminDefinition definition);
    IOwnerAdminProcess CreateSuspended(OwnerAdminDefinition definition);
}

internal sealed record OwnerAdminJob(string Jid, string Rid, string AdmissionId, string DefinitionSha,
    string State, int? Pid = null, string? Start = null, int? Exit = null, string? Error = null,
    string Phase = "not_started", bool StorageUnavailable = false, string? EvidenceSha256 = null,
    int? NativeError = null, string? NativePhase = null);
internal sealed record OwnerAdminResolution(int Version,string Jid,string Rid,string DefinitionSha,int? Pid,string? Start,string EvidenceSha256,bool EffectsQuiescent);

/// <summary>
/// Service-owned admission and durable, at-most-once launch. Request cancellation
/// only ends observation. It cannot terminate a child or make an uncertain launch retryable.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class OwnerAdminExecution : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    private static readonly Regex Id = new("^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Effects = new(StringComparer.Ordinal)
        { "filesystem", "backup", "installer", "service_control", "owner_process" };
    private readonly object _gate = new();
    private readonly SqliteConnection _db;
    private readonly string _ownerSid;
    private readonly IOwnerAdminLauncher _launcher;
    private readonly TimeProvider _clock;
    private readonly Action<string>? _beforePersist;
    private readonly Action<MaintenanceInitializationStage,bool?>? _initializationProgress;
    private readonly ConcurrentDictionary<string,OwnerAdminJob> _liveJobs = new();
    private readonly ConcurrentDictionary<string, Task> _monitors = new();
    private readonly IDisposable? _stateLease;
    private volatile bool _disposed;

    internal OwnerAdminExecution(string stateRoot, string ownerSid, IOwnerAdminLauncher launcher,
        bool protectState = true, TimeProvider? clock = null, Action<string>? beforePersist = null,
        Action<MaintenanceInitializationStage,bool?>? initializationProgress = null)
    {
        _ownerSid = ownerSid;
        _clock = clock ?? TimeProvider.System; _beforePersist = beforePersist;
        _initializationProgress = initializationProgress;
        _launcher = launcher;
        var root = Path.GetFullPath(Path.Combine(stateRoot, "owner-admin"));
        if (protectState) _stateLease = WindowsOwnerAdminLauncher.ProtectState(root);
        else Directory.CreateDirectory(root); // Internal deterministic-test construction only.
        _initializationProgress?.Invoke(MaintenanceInitializationStage.OwnerStore,null);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "execution.db"), Pooling = false }.ToString());
        try
        {
        _db.Open();
        using var command = _db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS admissions(id TEXT PRIMARY KEY, sha TEXT NOT NULL, definition TEXT NOT NULL, root_label TEXT NOT NULL, revoked INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS jobs(jid TEXT PRIMARY KEY, rid TEXT NOT NULL UNIQUE, admission_id TEXT NOT NULL, definition TEXT NOT NULL, state TEXT NOT NULL, body TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_owner_admin_jobs_state ON jobs(state);
            """;
        command.ExecuteNonQuery();
        // A lost process handle cannot prove an exit code. Retain identity and unknown
        // outcome, including the pre-launch crash window; never replay on startup.
        using var recovery = _db.CreateCommand();
        recovery.CommandText = """
            UPDATE jobs SET state='recovery_required',
              body=json_set(body,'$.state','recovery_required','$.error','admin_outcome_unknown')
            WHERE state IN ('starting','running')
            """;
        recovery.ExecuteNonQuery(); // Indexed nonterminal recovery; terminal history stays on disk.
        InitializeMaintenance(stateRoot,protectState);
        }
        catch{_db.Dispose();_stateLease?.Dispose();throw;}
    }

    internal async Task<OperationReceipt> HandleAsync(OperationRequest request, LocalPeer peer,
        ProtectedExecutionGrant authorization, CancellationToken observationCancellation,
        Func<bool>? currentConnectionApproval=null)
    {
        void RequireConnection()
        {
            if(currentConnectionApproval is null)return;
            bool active;
            try{active=currentConnectionApproval();}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException){active=false;}
            if(!active)throw new AgentOpsException("connection_revoked");
        }
        OwnerAdminJob? original = null;
        OwnerAdminDefinition? originalDefinition = null;
        try
        {
            if (_disposed) throw new AgentOpsException("service_unavailable");
            RequireConnection();
            if (peer.Session <= 0 || !StringComparer.Ordinal.Equals(peer.Sid, _ownerSid))
                throw new AgentOpsException("auth");
            if (request.Rid is null) request = request with { Rid = Guid.NewGuid().ToString("N") };
            if (!ValidArguments(request)) throw new AgentOpsException("invalid");
            var operation = request.Arg["op"];
            if(operation is "begin" or "end" or "interval" or "ack")return await HandleMaintenanceAsync(request,peer,authorization,observationCancellation);
            if (operation == "admit")
            {
                // The broker supplies this callback only after authenticating the
                // registered Host and its standing UAC-approved Full-control grant.
                // Direct manager callers still need actual elevated-owner review.
                if (!_launcher.IsElevatedOwner(peer)&&currentConnectionApproval is null) throw new AgentOpsException("admin_review_required");
                var admittedScope = ParseAuthorization(request.Body!);
                if (admittedScope.OwnerSid != _ownerSid || admittedScope.OwnerSid != peer.Sid || admittedScope.OwnerSession != peer.Session)
                    throw new AgentOpsException("admin_admission_mismatch");
                // An elevated CLI can review a distinct managed-MCP executable;
                // the root application policy must independently permit both.
                if (!ProtectedAccessRules.AllowsApplication(authorization, admittedScope.ClientApplicationPath))
                    throw new AgentOpsException("root_app_denied");
                ValidatePolicy(admittedScope.ReadRoots.Concat(admittedScope.WriteRoots), authorization);
                if (admittedScope.ExpiresAtUtc <= _clock.GetUtcNow() || admittedScope.ExpiresAtUtc > _clock.GetUtcNow().AddDays(7))
                    throw new AgentOpsException("admin_admission_expired");
                var digest = Hash(JsonSerializer.Serialize(admittedScope, Json));
                RequireConnection();
                lock (_gate)
                {
                    var existing = FindAdmission(admittedScope.Id);
                    if (existing is not null)
                    {
                        if (existing.Value.Sha != digest || existing.Value.RootLabel != authorization.Label || existing.Value.Revoked)
                            throw new AgentOpsException("admin_admission_conflict");
                    }
                    else
                    {
                        using var insert = _db.CreateCommand();
                        insert.CommandText = "INSERT INTO admissions(id,sha,definition,root_label) VALUES($id,$sha,$definition,$label)";
                        insert.Parameters.AddWithValue("$id", admittedScope.Id);
                        insert.Parameters.AddWithValue("$sha", digest);
                        insert.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(admittedScope, Json));
                        insert.Parameters.AddWithValue("$label", authorization.Label);
                        insert.ExecuteNonQuery();
                    }
                }
                return new(true, Guid.NewGuid().ToString("N"), request.Rid!, "done", "admin_admit",
                    Data: new Dictionary<string, object?> { ["cfg"] = admittedScope.Id, ["sha"] = digest,
                        ["mode"] = "owner_admin", ["reusable"] = true, ["task"] = admittedScope.TaskId });
            }

            if (operation == "revoke")
            {
                lock (_gate)
                {
                    var stored = FindAdmission(request.Arg["cfg"]) ?? throw new AgentOpsException("not_found");
                    ValidateOwner(stored.Scope.OwnerSid, stored.Scope.OwnerSession, stored.Scope.ClientApplicationPath, peer);
                    if (stored.RootLabel != authorization.Label) throw new AgentOpsException("auth");
                    using var revoke = _db.CreateCommand();
                    revoke.CommandText = "UPDATE admissions SET revoked=1 WHERE id=$id";
                    revoke.Parameters.AddWithValue("$id", request.Arg["cfg"]); revoke.ExecuteNonQuery();
                }
                return new(true, Guid.NewGuid().ToString("N"), request.Rid!, "done", "admin_revoke");
            }

            OwnerAdminJob? job;
            OwnerAdminDefinition admitted;
            string sha;
            OwnerAdminAuthorization scope;
            bool revoked;
            lock (_gate)
            {
                job = operation is "stat" or "reconcile" ? FindJob("jid", request.Arg["jid"]) : FindJob("rid", request.Rid!);
                var cfg = operation is "stat" or "reconcile" ? job?.AdmissionId : request.Arg["cfg"];
                var stored = cfg is null ? null : FindAdmission(cfg);
                if (stored is null) throw new AgentOpsException("not_found");
                if (stored.Value.RootLabel != authorization.Label) throw new AgentOpsException("auth");
                scope = stored.Value.Scope;
                revoked = stored.Value.Revoked;
                admitted = operation == "run" ? Parse(request.Body!) : FindDefinition(job!.Jid);
                sha = Digest(admitted);
            }
            ValidateIdentity(admitted, peer);
            ValidateOwner(scope.OwnerSid, scope.OwnerSession, scope.ClientApplicationPath, peer);
            ValidatePolicy(admitted, authorization);
            if (job is not null && job.DefinitionSha != sha) throw new AgentOpsException("integrity");
            if (operation == "reconcile")
            {
                if (job is null) throw new AgentOpsException("not_found");
                if (!_launcher.IsElevatedOwner(peer)) throw new AgentOpsException("admin_review_required");
                using (var doc=JsonDocument.Parse(request.Body!)) RejectDuplicates(doc.RootElement);
                var proof=JsonSerializer.Deserialize<OwnerAdminResolution>(request.Body!,Json)??throw new AgentOpsException("invalid");
                if(proof.Version!=1||!proof.EffectsQuiescent||proof.Jid!=job.Jid||proof.Rid!=job.Rid||proof.DefinitionSha!=job.DefinitionSha||proof.Pid!=job.Pid||proof.Start!=job.Start)
                    throw new AgentOpsException("admin_admission_mismatch");
                RequireSha(proof.EvidenceSha256);
                if(job.Pid is not null&&_launcher.IsOriginalProcessAlive(job.Pid.Value,job.Start!)) throw new AgentOpsException("admin_lane_busy");
                if(_monitors.TryGetValue(job.Jid,out var retiring)) await retiring.WaitAsync(observationCancellation);
                job=GetJob(job.Jid);
                // Explicit domain quiescence review releases only the effect fence.
                // It cannot invent an exit code, claim success, terminate or replay.
                job=job with{State="reconciled",EvidenceSha256=proof.EvidenceSha256};
                Save(job);
                return Receipt(GetJob(job.Jid),admitted);
            }
            if (operation == "run")
            {
                if (request.Arg["sha"] != sha || request.Rid != admitted.ExpectedRid)
                    throw new AgentOpsException("admin_admission_mismatch");
                if (job is not null && (job.AdmissionId != admitted.Id || job.DefinitionSha != sha))
                    throw new AgentOpsException("admin_admission_conflict");
                if (job is null)
                {
                    if (revoked) throw new AgentOpsException("admin_admission_revoked");
                    ValidateScope(scope, admitted);
                    if(BlockingJob() is null)RequireActiveMaintenance(admitted,scope,authorization);
                    ValidateLive(admitted);
                    var lease = AcquireFiles(admitted);
                    try
                    {
                        lock (_gate)
                        {
                            job = FindJob("rid", request.Rid!);
                            if (job is not null && (job.AdmissionId != admitted.Id || job.DefinitionSha != sha))
                                throw new AgentOpsException("admin_admission_conflict");
                            if (job is null)
                            {
                                var currentScope = FindAdmission(admitted.Id) ?? throw new AgentOpsException("not_found");
                                if (currentScope.Revoked) throw new AgentOpsException("admin_admission_revoked");
                                if(BlockingJob() is not null)throw new AgentOpsException("admin_reconciliation_required");
                                RequireActiveMaintenance(admitted,currentScope.Scope,authorization);
                                ValidateScope(currentScope.Scope, admitted);
                                // Commit the original RID BEFORE calling a process API. A crash
                                // in any subsequent window requires reconciliation, not retry.
                                using var tx = _db.BeginTransaction();
                                using var capacity = _db.CreateCommand(); capacity.Transaction = tx;
                                capacity.CommandText = "SELECT COUNT(*) FROM jobs WHERE state IN ('starting','running','recovery_required')";
                                if (Convert.ToInt64(capacity.ExecuteScalar(), CultureInfo.InvariantCulture) != 0 || _liveJobs.Values.Any(active=>active.State is "starting" or "running" or "recovery_required"))
                                    throw new AgentOpsException("admin_reconciliation_required");
                                job = new OwnerAdminJob(Guid.NewGuid().ToString("N"), request.Rid!, admitted.Id, sha, "starting");
                                using var insert = _db.CreateCommand();
                                insert.Transaction = tx;
                                insert.CommandText = "INSERT INTO jobs(jid,rid,admission_id,definition,state,body) VALUES($jid,$rid,$id,$definition,'starting',$body)";
                                insert.Parameters.AddWithValue("$jid", job.Jid);
                                insert.Parameters.AddWithValue("$rid", job.Rid);
                                insert.Parameters.AddWithValue("$id", job.AdmissionId);
                                insert.Parameters.AddWithValue("$definition", JsonSerializer.Serialize(admitted, Json));
                                insert.Parameters.AddWithValue("$body", JsonSerializer.Serialize(job, Json));
                                // File preparation may outlast the invocation deadline.
                                // Check while owning the final claim gate, immediately
                                // before new durable admission; original jobs skip this branch.
                                if(admitted.ExpiresAtUtc<=_clock.GetUtcNow())throw new AgentOpsException("admin_admission_expired");
                                RequireConnection();
                                insert.ExecuteNonQuery();
                                using var intervalClaim=_db.CreateCommand();intervalClaim.Transaction=tx;
                                intervalClaim.CommandText="INSERT INTO maintenance_native_jobs(epoch,jid,rid,definition_sha) VALUES($epoch,$jid,$rid,$sha)";
                                intervalClaim.Parameters.AddWithValue("$epoch",admitted.MaintenanceEpoch!);intervalClaim.Parameters.AddWithValue("$jid",job.Jid);intervalClaim.Parameters.AddWithValue("$rid",job.Rid);intervalClaim.Parameters.AddWithValue("$sha",sha);intervalClaim.ExecuteNonQuery();
                                tx.Commit();
                                original = job; originalDefinition = admitted;
                                IOwnerAdminProcess? process = null;
                                try
                                {
                                    RequireConnection();
                                    process = _launcher.CreateSuspended(admitted);
                                    job = job with { Pid = process.Pid, Start = process.Start, Phase = "created_suspended" };
                                    _liveJobs[job.Jid]=job;
                                    var ownedProcess=process; var ownedLease=lease; var monitoredJob=job;
                                    // Monitor ownership is established BEFORE either post-create
                                    // write. A SQLite failure cannot abandon the original child.
                                    _monitors[job.Jid]=Task.Run(()=>MonitorAsync(monitoredJob,ownedProcess,ownedLease));
                                    _ = _monitors[job.Jid].ContinueWith(_=>_monitors.TryRemove(monitoredJob.Jid,out var retired),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
                                    process=null; lease=null!;
                                    if(Save(job))
                                    {
                                        job=job with{Phase="resume_attempted"}; _liveJobs[job.Jid]=job;
                                        ownedProcess.Resume();
                                        job=job with{State="running",Phase="running"};
                                        Save(job);
                                    }
                                }
                                catch (Exception ex) when (ex is AgentOpsException or IOException or UnauthorizedAccessException or SqliteException or System.ComponentModel.Win32Exception)
                                {
                                    job=_liveJobs.GetValueOrDefault(job.Jid)??job;
                                    job = job with { State = job.Pid is null ? "failed" : "recovery_required",
                                        Error = job.Pid is null && ex is AgentOpsException failure ? failure.Code : "admin_outcome_unknown",
                                        NativeError=(ex.InnerException as System.ComponentModel.Win32Exception)?.NativeErrorCode,
                                        NativePhase=ex.Data["native_phase"] as string };
                                    Save(job);
                                }
                                finally { process?.Dispose(); }
                            }
                        }
                    }
                    finally { lease?.Dispose(); }
                }
            }
            if (job is null) throw new AgentOpsException("not_found");
            if (request.Arg.TryGetValue("tmo", out var wait) && _monitors.TryGetValue(job.Jid, out var monitor))
            {
                try { await monitor.WaitAsync(TimeSpan.FromSeconds(int.Parse(wait, CultureInfo.InvariantCulture)), observationCancellation); }
                catch (TimeoutException) { return Receipt(GetJob(job.Jid), admitted, observationExpired: true); }
            }
            return Receipt(GetJob(job.Jid), admitted);
        }
        catch (AgentOpsException ex) { return Failure(request, ex.Code, original, originalDefinition,ex); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or OverflowException)
        { return Failure(request, "invalid", original, originalDefinition); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or CryptographicException)
        { return Failure(request, "integrity", original, originalDefinition); }
    }

    private async Task MonitorAsync(OwnerAdminJob job, IOwnerAdminProcess process, IDisposable lease)
    {
        try
        {
            while (!_disposed)
            {
                if (process.TryGetExit(out var exit))
                {
                    var current=GetJob(job.Jid);
                    var knownStarted=current.Phase=="running";
                    Save(current with { State = knownStarted?(exit==0?"done":"failed"):"recovery_required", Exit = exit, Phase="exited",
                        Error = knownStarted?(exit==0?null:"external_failed"):"admin_outcome_unknown", StorageUnavailable=false });
                    return;
                }
                await Task.Delay(100);
            }
            Save(GetJob(job.Jid) with { State = "recovery_required", Error = "admin_outcome_unknown" });
        }
        catch (Exception ex) when (ex is AgentOpsException or IOException or System.ComponentModel.Win32Exception)
        { Save(GetJob(job.Jid) with { State = "recovery_required", Error = "admin_outcome_unknown" }); }
        finally { process.Dispose(); lease.Dispose(); }
    }

    private void ValidateLive(OwnerAdminDefinition definition)
    {
        if (definition.ExpiresAtUtc <= _clock.GetUtcNow() || definition.ExpiresAtUtc > _clock.GetUtcNow().AddDays(1))
            throw new AgentOpsException("admin_admission_expired");
        _launcher.ValidateProtectedInputs(definition);
    }

    private void ValidateIdentity(OwnerAdminDefinition definition, LocalPeer peer)
    {
        ValidateOwner(definition.OwnerSid, definition.OwnerSession, definition.ClientApplicationPath, peer);
    }

    private void ValidateOwner(string sid, int session, string application, LocalPeer peer)
    {
        if (sid != _ownerSid || peer.Sid != sid || peer.Session != session ||
            !StringComparer.OrdinalIgnoreCase.Equals(peer.ApplicationPath, application)) throw new AgentOpsException("admin_admission_mismatch");
    }

    private static void ValidatePolicy(OwnerAdminDefinition definition, ProtectedExecutionGrant authorization)
    {
        // Existing folder policy applies to EVERY declared effect root, not just
        // request src/dst. It is conjunctive with the separately reviewed program.
        ValidatePolicy(definition.ReadRoots.Concat(definition.WriteRoots), authorization);
    }

    private static void ValidatePolicy(IEnumerable<string> roots, ProtectedExecutionGrant authorization)
    {
        foreach (var root in roots)
            if (!ProtectedAccessRules.AllowsFolders(authorization,
                new OperationRequest("stat", new Dictionary<string, string> { ["src"] = root })))
                throw new AgentOpsException("root_path_denied");
    }

    private void ValidateScope(OwnerAdminAuthorization scope, OwnerAdminDefinition definition)
    {
        if (scope.ExpiresAtUtc <= _clock.GetUtcNow()) throw new AgentOpsException("admin_admission_expired");
        if (scope.Id != definition.Id || scope.TaskId != definition.TaskId || scope.OwnerSid != definition.OwnerSid ||
            scope.OwnerSession != definition.OwnerSession || scope.ClientApplicationPath != definition.ClientApplicationPath ||
            scope.ClientSha256 != definition.ClientSha256 || scope.OwnerProfile != definition.OwnerProfile ||
            definition.ExpiresAtUtc > scope.ExpiresAtUtc ||
            !scope.Programs.Any(program => program.Path == definition.ExecutablePath && (program.AllowRevisions || program.Sha256 == definition.ExecutableSha256)) ||
            definition.ScriptPath is not null && !scope.Scripts.Any(script => script.Path == definition.ScriptPath && (script.AllowRevisions || script.Sha256 == definition.ScriptSha256)) ||
            definition.Inputs.Any(input => !scope.InputRoots.Any(root => PathNormalizer.Contains(root, input.Path))) ||
            definition.ReadRoots.Any(root => !scope.ReadRoots.Any(allowed => PathNormalizer.Contains(allowed, root))) ||
            definition.WriteRoots.Any(root => !scope.WriteRoots.Any(allowed => PathNormalizer.Contains(allowed, root))) ||
            definition.DeclaredEffects.Any(effect => !scope.DeclaredEffects.Contains(effect, StringComparer.Ordinal)))
            throw new AgentOpsException("admin_scope_mismatch");
    }

    internal static OwnerAdminAuthorization ParseAuthorization(string body)
    {
        if (Encoding.UTF8.GetByteCount(body) > 64 * 1024) throw new AgentOpsException("invalid");
        using (var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 })) RejectDuplicates(doc.RootElement);
        var scope = JsonSerializer.Deserialize<OwnerAdminAuthorization>(body, Json) ?? throw new AgentOpsException("invalid");
        if (scope.Version != 1 || !IsId(scope.Id) || !Guid.TryParseExact(scope.TaskId, "D", out _) ||
            string.IsNullOrWhiteSpace(scope.OwnerSid) || scope.OwnerSession <= 0 || !scope.AllowUnconfinedAdministratorEffects ||
            scope.Programs is null || scope.Programs.Count is < 1 or > 16 || scope.Scripts is null || scope.Scripts.Count > 16 ||
            scope.InputRoots is null || scope.InputRoots.Count > 32 || scope.ReadRoots is null || scope.ReadRoots.Count is < 1 or > 32 ||
            scope.WriteRoots is null || scope.WriteRoots.Count > 32 || scope.DeclaredEffects is null || scope.DeclaredEffects.Count is < 1 or > 8 ||
            scope.DeclaredEffects.Any(effect => !Effects.Contains(effect))) throw new AgentOpsException("invalid");
        RequirePath(scope.ClientApplicationPath); RequireSha(scope.ClientSha256); RequirePath(scope.OwnerProfile);
        foreach (var program in scope.Programs.Concat(scope.Scripts))
        { if (program is null) throw new AgentOpsException("invalid"); RequirePath(program.Path); RequireSha(program.Sha256); }
        foreach (var root in scope.ReadRoots.Concat(scope.WriteRoots).Concat(scope.InputRoots)) RequirePath(root);
        return scope;
    }

    internal static OwnerAdminDefinition Parse(string body)
    {
        if (Encoding.UTF8.GetByteCount(body) > 64 * 1024) throw new AgentOpsException("invalid");
        using (var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 }))
            RejectDuplicates(document.RootElement);
        var definition = JsonSerializer.Deserialize<OwnerAdminDefinition>(body, Json) ?? throw new AgentOpsException("invalid");
        if (definition.Version != 1 || !IsId(definition.Id) || !Guid.TryParseExact(definition.TaskId, "D", out _) || !IsId(definition.ExpectedRid) ||
            string.IsNullOrWhiteSpace(definition.OwnerSid) || definition.OwnerSession <= 0 || !definition.NonSecretInputs ||
            definition.Action is not ("exe" or "pwsh") || definition.Arguments is null || definition.Inputs is null ||
            definition.ReadRoots is null || definition.WriteRoots is null || definition.DeclaredEffects is null ||
            definition.Arguments.Count > 128 || definition.Inputs.Count > 64 || definition.ReadRoots.Count is < 1 or > 32 ||
            definition.WriteRoots.Count > 32 || definition.DeclaredEffects.Count is < 1 or > 8 ||
            definition.DeclaredEffects.Any(effect => !Effects.Contains(effect))) throw new AgentOpsException("invalid");
        RequirePath(definition.ClientApplicationPath); RequireSha(definition.ClientSha256);
        RequirePath(definition.OwnerProfile); RequirePath(definition.ExecutablePath); RequireSha(definition.ExecutableSha256);
        RequirePath(definition.WorkingDirectory);
        if(definition.MaintenanceEpoch is not null&&!IsId(definition.MaintenanceEpoch))throw new AgentOpsException("invalid");
        if (!definition.ClientApplicationPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !definition.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new AgentOpsException("invalid");
        foreach (var root in definition.ReadRoots.Concat(definition.WriteRoots)) RequirePath(root);
        foreach (var input in definition.Inputs)
        { if (input is null || input.Kind is not ("code" or "data")) throw new AgentOpsException("invalid"); RequirePath(input.Path); RequireSha(input.Sha256); }
        if (definition.Arguments.Any(argument => argument is null || argument.Contains('\0') ||
                Encoding.UTF8.GetByteCount(argument) > 4096) || definition.Arguments.Sum(argument => Encoding.UTF8.GetByteCount(argument)) > 24000)
            throw new AgentOpsException("invalid");
        if (definition.Action == "pwsh")
        {
            RequirePath(definition.ScriptPath!); RequireSha(definition.ScriptSha256!);
            if (!definition.ScriptPath!.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(definition.ExecutablePath).Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase))
                throw new AgentOpsException("invalid");
        }
        else if (definition.ScriptPath is not null || definition.ScriptSha256 is not null) throw new AgentOpsException("invalid");
        if (definition.Action == "exe" && new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe" }
            .Contains(Path.GetFileName(definition.ExecutablePath), StringComparer.OrdinalIgnoreCase)) throw new AgentOpsException("invalid");
        foreach (var file in Files(definition))
            if (!definition.ReadRoots.Any(root => PathNormalizer.Contains(root, file.Path))) throw new AgentOpsException("admin_scope_mismatch");
        if (!definition.ReadRoots.Concat(definition.WriteRoots).Any(root => PathNormalizer.Contains(root, definition.WorkingDirectory)))
            throw new AgentOpsException("admin_scope_mismatch");
        // Round-trip freezes caller-owned collections and rejects omitted/null required members.
        return definition;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string Digest(OwnerAdminDefinition definition) => Hash(JsonSerializer.Serialize(definition, Json));

    internal static IReadOnlyList<string> Arguments(OwnerAdminDefinition definition) => definition.Action == "pwsh"
        ? new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", definition.ScriptPath! }.Concat(definition.Arguments).ToArray()
        : definition.Arguments;

    private static IEnumerable<OwnerAdminInput> Files(OwnerAdminDefinition definition) =>
        new[] { new OwnerAdminInput(definition.ExecutablePath, definition.ExecutableSha256),
            new OwnerAdminInput(definition.ClientApplicationPath, definition.ClientSha256) }
        .Concat(definition.ScriptPath is null ? [] : new[] { new OwnerAdminInput(definition.ScriptPath, definition.ScriptSha256!) })
        .Concat(definition.Inputs);

    private static IDisposable AcquireFiles(OwnerAdminDefinition definition)
    {
        var streams = new List<FileStream>();
        try
        {
            PathNormalizer.RejectAbsoluteReparseComponents(definition.WorkingDirectory);
            foreach (var file in Files(definition))
            {
                PathNormalizer.RejectAbsoluteReparseComponents(file.Path);
                var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                streams.Add(stream);
                if (!StringComparer.Ordinal.Equals(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), file.Sha256))
                    throw new AgentOpsException("admin_input_changed");
            }
            return new FileLease(streams);
        }
        catch { foreach (var stream in streams) stream.Dispose(); throw; }
    }

    private sealed class FileLease(List<FileStream> streams) : IDisposable
    { public void Dispose() { foreach (var stream in streams) stream.Dispose(); } }

    private static void RequirePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Length > 240 ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), path))
            throw new AgentOpsException("invalid");
        _ = new WindowsPathContextNormalizer().Normalize(path);
    }
    private static void RequireSha(string sha) { if (sha is null || !Sha.IsMatch(sha)) throw new AgentOpsException("invalid"); }
    private static bool IsId(string? value) => value is not null && Id.IsMatch(value);
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new AgentOpsException("invalid"); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    internal static bool ValidArguments(OperationRequest request)
    {
        if (request.Op != "admin" || request.Root is not null || request.Ws is not null || request.PathRoots is not null ||
            !IsId(request.Rid) || !request.Arg.TryGetValue("op", out var op)) return false;
        var allowed = op switch
        { "admit" => new[] { "op", "rid" }, "revoke" => new[] { "op", "cfg", "rid" }, "run" => new[] { "op", "cfg", "sha", "rid", "tmo" },
          "stat" => new[] { "op", "jid", "rid", "tmo" }, "reconcile" => new[]{"op","jid","rid"},
          "begin"=>new[]{"op","cfg","sha","rid"},"end"=>new[]{"op","cfg","sha","epoch","rid"},"interval"=>new[]{"op","cfg","rid"},"ack"=>new[]{"op","cfg","sha","rid"}, _ => Array.Empty<string>() };
        if (allowed.Length == 0 || request.Arg.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal) &&
                !(key == OperationRequest.BodyOmittedKey && request.Body is not null && request.Arg[key] == "1"))) return false;
        if (op is "admit" or "run" or "reconcile" or "ack" or "end" ? request.Body is null : request.Body is not null) return false;
        if (op is "revoke" or "begin" or "end" or "interval" or "ack" && !IsId(request.Arg.GetValueOrDefault("cfg"))) return false;
        if(op is "begin" or "end" or "ack"&&!Sha.IsMatch(request.Arg.GetValueOrDefault("sha","")))return false;
        if(op=="end"&&!IsId(request.Arg.GetValueOrDefault("epoch")))return false;
        if (op == "run" && (!IsId(request.Arg.GetValueOrDefault("cfg")) || !Sha.IsMatch(request.Arg.GetValueOrDefault("sha", "")))) return false;
        if (op is "stat" or "reconcile" && !IsId(request.Arg.GetValueOrDefault("jid"))) return false;
        return !request.Arg.TryGetValue("tmo", out var tmo) || int.TryParse(tmo, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 1 and <= 60;
    }

    private (OwnerAdminAuthorization Scope, string Sha, string RootLabel, bool Revoked)? FindAdmission(string id)
    {
        lock(_gate)
        {
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT definition,sha,root_label,revoked FROM admissions WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var definition = ParseAuthorization(reader.GetString(0));
        var sha = reader.GetString(1);
        if (Hash(JsonSerializer.Serialize(definition, Json)) != sha) throw new AgentOpsException("integrity");
        return (definition, sha, reader.GetString(2), reader.GetInt32(3) != 0);
        }
    }
    private OwnerAdminDefinition FindDefinition(string jid)
    {
        using var command = _db.CreateCommand(); command.CommandText = "SELECT definition FROM jobs WHERE jid=$jid";
        command.Parameters.AddWithValue("$jid", jid);
        return Parse(command.ExecuteScalar() as string ?? throw new AgentOpsException("not_found"));
    }
    private OwnerAdminJob? FindJob(string field, string value)
    {
        var live=field=="jid"?_liveJobs.GetValueOrDefault(value):_liveJobs.Values.FirstOrDefault(job=>job.Rid==value);
        if(live is not null)return live;
        using var command = _db.CreateCommand();
        command.CommandText = field == "jid" ? "SELECT body FROM jobs WHERE jid=$value" : "SELECT body FROM jobs WHERE rid=$value";
        command.Parameters.AddWithValue("$value", value);
        var body = command.ExecuteScalar() as string;
        return body is null ? null : JsonSerializer.Deserialize<OwnerAdminJob>(body, Json);
    }
    private OwnerAdminJob GetJob(string jid) { lock (_gate) return FindJob("jid", jid) ?? throw new AgentOpsException("not_found"); }
    private bool Save(OwnerAdminJob job)
    {
        lock (_gate)
        {
            _liveJobs[job.Jid]=job;
            try
            {
            _beforePersist?.Invoke(job.Phase);
            using var command = _db.CreateCommand();
            command.CommandText = "UPDATE jobs SET body=$body,state=$state WHERE jid=$jid";
            command.Parameters.AddWithValue("$jid", job.Jid);
            command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(job, Json));
            command.Parameters.AddWithValue("$state", job.State);
            command.ExecuteNonQuery();
            if(job.State is "done" or "failed" or "reconciled") _liveJobs.TryRemove(job.Jid,out var retired);
            return true;
            }
            catch(Exception ex)when(ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                _liveJobs[job.Jid]=job with{State="recovery_required",Error="admin_persistence_unavailable",StorageUnavailable=true};
                return false;
            }
        }
    }
    private static OperationReceipt Receipt(OwnerAdminJob job, OwnerAdminDefinition definition, bool observationExpired = false) =>
        new(job.Error is null, job.Jid, job.Rid, job.State, "admin_run", job.Error,
            new Dictionary<string, object?> { ["cfg"] = job.AdmissionId, ["sha"] = job.DefinitionSha,
                ["mode"] = "owner_admin", ["owner_sid"] = definition.OwnerSid, ["owner_session"] = definition.OwnerSession,
                ["profile"] = definition.OwnerProfile, ["pid"] = job.Pid, ["start"] = job.Start, ["exit"] = job.Exit,
                ["output"] = "status_only", ["process_scope"] = "root_process", ["retry"] = "never_replay",
                ["observation_expired"] = observationExpired, ["child_terminated"] = false,
                ["phase"]=job.Phase,["storage_unavailable"]=job.StorageUnavailable,["reconciliation_evidence_sha"]=job.EvidenceSha256,
                ["effect_fence_released"]=job.State=="reconciled",["native_phase"]=job.NativePhase,["win32_error"]=job.NativeError },
            Partial: job.State == "recovery_required");
    private OperationReceipt Failure(OperationRequest request,string code,OwnerAdminJob? original=null,OwnerAdminDefinition? definition=null,AgentOpsException? nativeFailure=null)
    {
        if(original is not null&&definition is not null) return Receipt(_liveJobs.GetValueOrDefault(original.Jid)??original with{State="recovery_required",Error=code},definition);
        var blocker=code=="admin_reconciliation_required"?BlockingJob():null;
        return new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"failed",OperationNames.Public(request),code,
            blocker is null?(nativeFailure?.InnerException is System.ComponentModel.Win32Exception native?new Dictionary<string,object?>{["native_phase"]=nativeFailure.Data["native_phase"] as string,["win32_error"]=native.NativeErrorCode}:null):new Dictionary<string,object?>{["original_jid"]=blocker.Jid,["original_rid"]=blocker.Rid,["original_pid"]=blocker.Pid,["original_start"]=blocker.Start});
    }
    internal bool HasOriginalRid(string? rid)
    { lock(_gate) return rid is not null && FindJob("rid",rid) is not null; }
    internal OwnerAdminJob? BlockingJob()
    {
        lock(_gate)
        {
            var live=_liveJobs.Values.FirstOrDefault(job=>job.State is "starting" or "running" or "recovery_required");
            if(live is not null)return live;
            using var command=_db.CreateCommand();command.CommandText="SELECT body FROM jobs WHERE state IN ('starting','running','recovery_required') LIMIT 1";
            return command.ExecuteScalar() is string body?JsonSerializer.Deserialize<OwnerAdminJob>(body,Json):null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        // Monitors poll locally, without a child lifetime cancellation token.
        // Await retirement before closing SQLite; handle disposal never kills.
        Task.WhenAll(_monitors.Values).GetAwaiter().GetResult();
        _db.Dispose();
        _stateLease?.Dispose();
    }
}

/// <summary>Pure, non-executing definition inspection shared by CLI and MCP clients.</summary>
[SupportedOSPlatform("windows")]
public static class OwnerAdminContract
{
    public static OperationReceipt Validate(OperationRequest request)
    {
        try
        {
            if (request.Root is not null || request.Body is null || request.Arg.GetValueOrDefault("op") != "validate" ||
                request.Arg.Keys.Any(key => key is not ("op" or "rid"))) throw new AgentOpsException("invalid");
            var definition = OwnerAdminExecution.Parse(request.Body);
            return new(true, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), "done", "admin_validate",
                Data: new Dictionary<string, object?> { ["cfg"] = definition.Id, ["sha"] = OwnerAdminExecution.Digest(definition),
                    ["execution_rid"] = definition.ExpectedRid, ["execution"] = "not_dispatched" });
        }
        catch (Exception ex) when (ex is AgentOpsException or JsonException or ArgumentException or OverflowException)
        {
            return new(false, Guid.NewGuid().ToString("N"), request.Rid ?? Guid.NewGuid().ToString("N"), "failed", "admin_validate",
                ex is AgentOpsException failure ? failure.Code : "invalid");
        }
    }
}
