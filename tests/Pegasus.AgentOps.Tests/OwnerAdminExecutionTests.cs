using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class OwnerAdminExecutionTests
{
    private const string TaskId = "01a12d3b-7157-7352-ac1f-a5f549ceb103";

    internal static async Task InvocationDeadlineAtClaimAsync()
    {
        using var fixture=new Fixture();var scope=fixture.Scope();True((await fixture.Admit(scope)).Ok,"reviewed interval");
        var expiring=fixture.Definition(scope) with{ExpiresAtUtc=fixture.Clock.Now.AddMinutes(1)};
        fixture.Launcher.PrepareHook=_=>fixture.Clock.Now=expiring.ExpiresAtUtc;
        var rejected=await fixture.Call(expiring);
        Equal("admin_admission_expired",rejected.Err);Equal(0,fixture.Launcher.Created);True(fixture.Launcher.Last is null,"expired preparation created no native child");
        True(fixture.Clock.Now<scope.ExpiresAtUtc,"reusable scope remains valid when invocation expires");
        using(var database=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(fixture.Root,"state","owner-admin","execution.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
        {
            database.Open();using var query=database.CreateCommand();query.CommandText="SELECT (SELECT COUNT(*) FROM jobs WHERE rid=$rid)+(SELECT COUNT(*) FROM maintenance_native_jobs WHERE rid=$rid)";query.Parameters.AddWithValue("$rid",expiring.ExpectedRid);
            Equal(0L,Convert.ToInt64(query.ExecuteScalar()));
        }
        fixture.Launcher.PrepareHook=null;
        var admitted=fixture.Definition(scope) with{ExpiresAtUtc=fixture.Clock.Now.AddMinutes(1)};
        var original=await fixture.Call(admitted);Equal("running",original.State);Equal(1,fixture.Launcher.Created);
        fixture.Clock.Now=admitted.ExpiresAtUtc.AddSeconds(1);
        var replay=await fixture.Call(admitted);Equal(original.Jid,replay.Jid);Equal(original.Rid,replay.Rid);Equal(original.Data!["pid"],replay.Data!["pid"]);Equal(original.Data["start"],replay.Data["start"]);Equal(1,fixture.Launcher.Created);
        Equal(original.Jid,(await fixture.Stat(original.Jid)).Jid);
        fixture.Launcher.Last!.Exit=0;var terminal=await fixture.WaitTerminal(original.Jid);Equal("done",terminal.State);Equal(0,Convert.ToInt32(terminal.Data!["exit"]));
    }
    internal static async Task SecurityAsync()
    {
        using var fixture = new Fixture();
        var scope = fixture.Scope();
        var manager = fixture.Manager;
        fixture.Launcher.Elevated = false;
        var directGrant = new ProtectedExecutionGrant(fixture.Approval.Current.ConnectionId,
            ProtectedAccessPolicy.Off, ProtectedAccessPolicy.Off);
        var directAdmission = new OperationRequest("admin", new Dictionary<string,string>{{"op","admit"}},
            Guid.NewGuid().ToString("N"), Body: fixture.Body(scope));
        Equal("admin_review_required", (await manager.HandleAsync(directAdmission, fixture.Peer, directGrant, default)).Err);
        True((await fixture.Admit(scope)).Ok, "current Full-control ordinary Host scope admission");
        fixture.Launcher.Elevated = true;
        var definition = fixture.Definition(scope);
        Equal("auth", (await fixture.Broker.HandleUserAsync(fixture.Request("run", definition), fixture.Peer, default)).Err);
        Equal("auth", (await fixture.Broker.HandleApplicationAsync(fixture.Request("run", definition), default)).Err);
        Equal("full_control_required", (await fixture.Call(definition, approved: false)).Err);
        Equal("full_control_required", (await fixture.Call(definition, peer: fixture.Peer with { Session = fixture.Peer.Session + 1 })).Err);
        Equal("full_control_required", (await fixture.Call(definition, peer: fixture.Peer with { Sid = "foreign-owner" })).Err);
        Equal("full_control_required", (await fixture.Call(definition, peer: fixture.Peer with { ApplicationPath = fixture.Executable })).Err);
        Equal("admin_scope_mismatch", (await fixture.Call(definition with { TaskId = Guid.NewGuid().ToString("D") })).Err);
        Equal("admin_scope_mismatch", (await fixture.Call(definition with { WriteRoots = [Path.GetPathRoot(fixture.Root)!] })).Err);
        Equal("admin_scope_mismatch", (await fixture.Call(definition with { DeclaredEffects = ["service_control"] })).Err);
        Equal("admin_admission_mismatch", (await fixture.Call(definition, sha: new string('0', 64))).Err);
        Equal("invalid", (await fixture.CallRaw("run", new Dictionary<string,string>
            { ["cfg"] = scope.Id, ["sha"] = OwnerAdminExecution.Digest(definition), ["__execution_context_system"] = "1" }, fixture.Body(definition), definition.ExpectedRid)).Err);
        var code = File.ReadAllText(fixture.Script); File.WriteAllText(fixture.Script, code + "#changed");
        Equal("admin_input_changed", (await fixture.Call(definition)).Err);
        File.WriteAllText(fixture.Script, code);
        Equal(0, fixture.Launcher.Created);
        var duplicateJson = fixture.Body(definition).Replace("\"version\":1", "\"version\":1,\"Version\":1", StringComparison.Ordinal);
        Throws("invalid", () => OwnerAdminExecution.Parse(duplicateJson));
        Throws("invalid", () => OwnerAdminExecution.Parse(fixture.Body(definition).TrimEnd('}') + ",\"unexpected\":true}"));
        Throws("invalid", () => OwnerAdminExecution.Parse(fixture.Body(definition with { NonSecretInputs = false })));
        Throws("invalid", () => OwnerAdminExecution.Parse(fixture.Body(definition with { ExecutablePath = Path.Combine(fixture.Root,"cmd.exe") })));
        var off = ProtectedAccessPolicy.Off;
        var pathDenied = new ProtectedExecutionGrant(fixture.Approval.Current.ConnectionId, off, new("deny", [fixture.Root]));
        Equal("root_path_denied", (await manager.HandleAsync(fixture.Request("run", definition), fixture.Peer, pathDenied, default)).Err);
        var appDenied = new ProtectedExecutionGrant(fixture.Approval.Current.ConnectionId, new("deny", [fixture.Peer.ApplicationPath!]), off);
        Equal("root_app_denied", (await manager.HandleAsync(new("admin",new Dictionary<string,string>{{"op","admit"}},Guid.NewGuid().ToString("N"),Body:fixture.Body(scope)), fixture.Peer, appDenied, default)).Err);
        Equal(0, fixture.Launcher.Created);
        // This exercises the real Windows ACL boundary read-only. User-owned
        // source code is not an administrator-protected executable admission.
        Throws("admin_protected_input_required", () => new WindowsOwnerAdminLauncher(fixture.Root).ValidateProtectedInputs(definition));

        using var revoked = new Fixture();
        var revokedScope = revoked.Scope();
        True((await revoked.Admit(revokedScope)).Ok, "revocation fixture scope admission");
        var revokedDefinition = revoked.Definition(revokedScope);
        revoked.Launcher.PrepareHook = _ => revoked.Approval.Enabled = false;
        var rejectedBeforeCreate = await revoked.Call(revokedDefinition);
        Equal("connection_revoked", rejectedBeforeCreate.Err);
        Equal(0, revoked.Launcher.Created);
        True(revoked.Launcher.Last is null, "revocation during input validation created a process");
    }

    internal static async Task ReusableAndReplayAsync()
    {
        using var fixture = new Fixture();
        var scope = fixture.Scope(revisions: true);
        True((await fixture.Admit(scope)).Ok, "initial review");
        fixture.Launcher.Elevated = false; // Every execution below is the ordinary owner client.
        var first = fixture.Definition(scope);
        var receipt = await fixture.Call(first);
        Equal("running", receipt.State); Equal(1, fixture.Launcher.Created);
        var replay = await fixture.Call(first);
        Equal(receipt.Jid, replay.Jid); Equal(receipt.Rid, replay.Rid); Equal(1, fixture.Launcher.Created);
        var mismatch = first with { Arguments = ["different"] };
        Equal("integrity", (await fixture.Call(mismatch)).Err);
        // A revised approved script and a fresh intended RID use the SAME scope,
        // with no elevated reviewer, tool registration, or additional admission.
        fixture.Launcher.Last!.Exit = 0;
        await fixture.WaitTerminal(receipt.Jid);
        True((await fixture.End(scope)).Ok,"explicit handback before staging revised code");
        File.WriteAllText(fixture.Script, "#revision two\n");
        True((await fixture.Begin(scope)).Ok,"fresh exclusive interval after staging revised code");
        var second = fixture.Definition(scope);
        var successor = await fixture.Call(second);
        Equal("running", successor.State); Equal(2, fixture.Launcher.Created);
        True(successor.Jid != receipt.Jid && successor.Rid != receipt.Rid, "successor identity");
        var revoked = await fixture.CallRaw("revoke", new() { ["cfg"] = scope.Id });
        True(revoked.Ok, "revoke");
        var third = fixture.Definition(scope);
        Equal("admin_admission_revoked", (await fixture.Call(third)).Err);
        var original = await fixture.Stat(receipt.Jid);
        Equal("done", original.State); Equal(receipt.Rid, original.Rid);
        var existing = await fixture.Call(second);
        Equal(successor.Jid, existing.Jid); Equal(2, fixture.Launcher.Created);
        Equal("running", existing.State);
        fixture.Launcher.Last!.Exit = 1603;
        var failed = await fixture.WaitTerminal(successor.Jid);
        Equal("failed", failed.State); Equal("external_failed", failed.Err);
        Equal(1603, Convert.ToInt32(failed.Data!["exit"]));
    }

    internal static async Task ObservationAndRecoveryAsync()
    {
        using var fixture = new Fixture();
        var scope = fixture.Scope(); True((await fixture.Admit(scope)).Ok, "review");
        var definition = fixture.Definition(scope);
        var first = await fixture.Call(definition);
        var process = fixture.Launcher.Last!;
        var timed = await fixture.Call(definition, tmo: "1");
        Equal(first.Jid, timed.Jid); Equal("running", timed.State);
        True((bool)timed.Data!["observation_expired"]!, "diagnostic timeout");
        True(!process.Disposed && process.ResumeCount == 1, "timeout did not retire or restart child");
        using (var cancel = new CancellationTokenSource(30))
        {
            try { await fixture.Call(definition, tmo: "60", cancellation: cancel.Token); throw new InvalidOperationException("expected observation cancellation"); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        }
        Equal("running", (await fixture.Stat(first.Jid)).State);
        Equal("admin_lane_busy",(await fixture.Broker.HandleUserAsync(new("mkdir",new Dictionary<string,string>{{"dst",Path.Combine(fixture.Root,"blocked")}}),fixture.Peer,default)).Err);
        Equal(1, fixture.Launcher.Created); True(!process.Disposed, "disconnect leaves monitor alive");
        fixture.Manager.Dispose();
        True(process.Disposed && process.ResumeCount == 1, "handle retirement without child termination");
        fixture.ReplaceManager();
        var unknown = await fixture.Call(definition);
        Equal(first.Jid, unknown.Jid); Equal(first.Rid, unknown.Rid);
        Equal("recovery_required", unknown.State); Equal("admin_outcome_unknown", unknown.Err);
        Equal(process.Pid, Convert.ToInt32(unknown.Data!["pid"])); Equal(process.Start, unknown.Data["start"]);
        Equal(1, fixture.Launcher.Created);
        // Only nonterminal rows may be hydrated/recovered at startup. A terminal
        // history row is deliberately unreadable; unrelated exact reads still work.
        fixture.Manager.Dispose();
        using (var db = new SqliteConnection($"Data Source={Path.Combine(fixture.Root,"state","owner-admin","execution.db")};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO jobs(jid,rid,admission_id,definition,state,body) VALUES('terminal-history','old-rid','old-scope','unread','done','unread')";
            command.ExecuteNonQuery();
        }
        fixture.ReplaceManager();
        Equal(first.Jid, (await fixture.Stat(first.Jid)).Jid);
    }

    internal static async Task CliMcpAsync()
    {
        using var fixture = new Fixture();
        var scope = fixture.Scope();
        await using var dispatch = new TestDispatcher(fixture);
        var outText = new StringWriter(); var errText = new StringWriter();
        Equal(0, await PaoProgram.RunCliAsync(["--json","admin","admit","system=true","--stdin"],dispatch,outText,errText,default,new StringReader(fixture.Body(scope))));
        outText.GetStringBuilder().Clear();
        Equal(0,await PaoProgram.RunCliAsync(["--json","admin","begin","system=true","cfg="+scope.Id,"sha="+fixture.ScopeSha(scope),"rid="+Guid.NewGuid().ToString("N")],dispatch,outText,errText,default));
        using(var begun=JsonDocument.Parse(outText.ToString()))fixture.Epoch=begun.RootElement.GetProperty("data").GetProperty("epoch").GetString();
        var definition = fixture.Definition(scope);
        var body = fixture.Body(definition);
        outText.GetStringBuilder().Clear();
        Equal(0, await PaoProgram.RunCliAsync(["--json","admin","validate","--stdin"],dispatch,outText,errText,default,new StringReader(body)));
        using (var validated = JsonDocument.Parse(outText.ToString()))
            Equal(OwnerAdminExecution.Digest(definition), validated.RootElement.GetProperty("data").GetProperty("sha").GetString());
        Equal(2, dispatch.Calls); // Admission + begin; pure validation made no broker call.
        outText.GetStringBuilder().Clear();
        Equal(0, await PaoProgram.RunCliAsync(["--json","admin","run","system=true","cfg="+scope.Id,"sha="+OwnerAdminExecution.Digest(definition),"rid="+definition.ExpectedRid,"--stdin"],dispatch,outText,errText,default,new StringReader(body)));
        using var cliReceipt = JsonDocument.Parse(outText.ToString());
        var jid = cliReceipt.RootElement.GetProperty("jid").GetString()!;
        var parameters = JsonSerializer.SerializeToElement(new { op="admin_run", cfg=scope.Id, sha=OwnerAdminExecution.Digest(definition), rid=definition.ExpectedRid, system=true, text=body, output="structured" });
        var mcp = await PaoProgram.CallUnifiedMcpToolAsync(parameters,dispatch,default);
        Equal(jid, mcp["structuredContent"]!["jid"]!.GetValue<string>());
        Equal(1, fixture.Launcher.Created);
        var fields = PaoProgram.UnifiedMcpToolDefinition()["inputSchema"]!["properties"]!["op"]!["enum"]!.ToJsonString();
        True(fields.Contains("admin_run",StringComparison.Ordinal) && fields.Contains("admin_validate",StringComparison.Ordinal), "advertised MCP operations");
        foreach (var operation in new[] {"admin_validate","admin_admit","admin_run","admin_stat","admin_revoke","admin_begin","admin_end","admin_interval","admin_ack"})
        {
            outText.GetStringBuilder().Clear();
            Equal(0, await PaoProgram.RunCliAsync(["help",operation],dispatch,outText,errText,default));
            True(outText.ToString().Contains(operation,StringComparison.Ordinal), "CLI help");
        }
        var invalid = JsonSerializer.SerializeToElement(new {op="admin_run", cfg=scope.Id, sha=OwnerAdminExecution.Digest(definition),rid=definition.ExpectedRid,system=true,text=body,arg=new[]{"-Command","bad"}});
        var rejected = await PaoProgram.CallUnifiedMcpToolAsync(invalid,dispatch,default);
        True(rejected["isError"]!.GetValue<bool>(), "closed MCP fields");
        outText.GetStringBuilder().Clear();
        Equal(2, await PaoProgram.RunCliAsync(["admin","run","cfg="+scope.Id,"sha="+OwnerAdminExecution.Digest(definition),"rid="+definition.ExpectedRid,"--stdin"],dispatch,outText,errText,default,new StringReader(body)));
        foreach (var suffix in new[] { "execution.db", "execution.db-wal" })
        {
            var path = Path.Combine(fixture.Root,"state","owner-admin",suffix);
            if (!File.Exists(path)) continue;
            using var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var bytes = new MemoryStream(); stream.CopyTo(bytes);
            var retained=Encoding.UTF8.GetString(bytes.ToArray());
            True(!retained.Contains("root.verifier",StringComparison.Ordinal) && !retained.Contains("root_code",StringComparison.Ordinal), "retired credential material referenced in durable database or WAL");
        }
    }

    internal static async Task ConcurrentAndFailedLaunchAsync()
    {
        using var fixture = new Fixture(); var scope = fixture.Scope(); await fixture.Admit(scope);
        var definition = fixture.Definition(scope);
        var receipts = await Task.WhenAll(Enumerable.Range(0,8).Select(_ => Task.Run(() => fixture.Call(definition))));
        Equal(1, fixture.Launcher.Created); Equal(1, receipts.Select(receipt => receipt.Jid).Distinct().Count());
        fixture.Launcher.Last!.Exit = 0; await fixture.WaitTerminal(receipts[0].Jid);
        fixture.Launcher.Failure = "admin_owner_token_unavailable";
        var successor = fixture.Definition(scope);
        var failed = await fixture.Call(successor);
        Equal("failed", failed.State); Equal("admin_owner_token_unavailable", failed.Err);
        Equal(1314,Convert.ToInt32(failed.Data!["win32_error"]));Equal("fixture_token_query",failed.Data["native_phase"]);
        Equal(failed.Jid, (await fixture.Call(successor)).Jid); Equal(2, fixture.Launcher.Created);
    }

    internal static async Task AuthenticatedPipeAsync()
    {
        using var fixture = new Fixture(); var scope = fixture.Scope();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pipe = "pao-admin-test-"+Guid.NewGuid().ToString("N");
        var host = AuthenticatedPipeHost.RunOperationsAsync(pipe,fixture.Peer.Sid,fixture.Broker.HandleSystemAsync,stop.Token);
        try
        {
            await using var dispatcher = new NamedPipeDispatcher(pipe,expectedServer:fixture.Peer);
            var admission = await dispatcher.DispatchAsync(new("admin",new Dictionary<string,string>{["op"]="admit"},Guid.NewGuid().ToString("N"),System:true,ConnectionAuthority:ApprovedConnectionFixture.DefaultAuthority,Body:fixture.Body(scope)),stop.Token);
            True(admission.Ok,"authenticated admission transport");
            var begun=await dispatcher.DispatchAsync(new("admin",new Dictionary<string,string>{{"op","begin"},{"cfg",scope.Id},{"sha",fixture.ScopeSha(scope)}},Guid.NewGuid().ToString("N"),System:true,ConnectionAuthority:ApprovedConnectionFixture.DefaultAuthority),stop.Token);
            True(begun.Ok,"authenticated interval begin transport");fixture.Epoch=begun.Data!["epoch"]!.ToString();
            var definition = fixture.Definition(scope);
            var request = fixture.Request("run",definition) with {System=true,ConnectionAuthority=ApprovedConnectionFixture.DefaultAuthority};
            var started = await dispatcher.DispatchAsync(request,stop.Token);
            Equal("running",started.State);
            var replay = await dispatcher.DispatchAsync(request,stop.Token);
            Equal(started.Jid,replay.Jid); Equal(1,fixture.Launcher.Created);
            fixture.Launcher.Last!.Exit=0;
            Equal("done",(await fixture.WaitTerminal(started.Jid)).State);
        }
        finally { stop.Cancel(); try { await host; } catch(OperationCanceledException)when(stop.IsCancellationRequested){} }
    }

    internal static async Task StdioAsync()
    {
        using var fixture = new Fixture(); var scope = fixture.Scope(); await fixture.Admit(scope);
        var definition = fixture.Definition(scope);
        var initialize = JsonSerializer.Serialize(new {jsonrpc="2.0",id=1,method="initialize",@params=new {protocolVersion="2025-06-18",capabilities=new{},clientInfo=new{name="owner-admin-fixture",version="1"}}});
        var initialized = JsonSerializer.Serialize(new {jsonrpc="2.0",method="notifications/initialized"});
        var validate = JsonSerializer.Serialize(new {jsonrpc="2.0",id=2,method="tools/call",@params=new {name="pao",arguments=new {op="admin_validate",text=fixture.Body(definition),output="structured"}}});
        await using var dispatcher = new TestDispatcher(fixture);
        var output = new StringWriter(); var errors = new StringWriter();
        Equal(0,await PaoProgram.RunMcpAsync(dispatcher,new StringReader(string.Join('\n',initialize,initialized,validate)+"\n"),output,errors,default,defaultWs:"unrelated-workspace"));
        var responses = output.ToString().Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>JsonDocument.Parse(line)).ToArray();
        try
        {
            var response = responses.Single(item=>item.RootElement.TryGetProperty("id",out var id)&&id.ValueKind==JsonValueKind.Number&&id.GetInt32()==2);
            Equal(OwnerAdminExecution.Digest(definition),response.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data").GetProperty("sha").GetString());
            Equal(0,dispatcher.Calls); Equal(0,fixture.Launcher.Created);
        }
        finally { foreach(var response in responses)response.Dispose(); }
    }
    internal static async Task FencingStorageAndExpiryAsync()
    {
        using(var fixture=new Fixture())
        {
            var scope=fixture.Scope(revisions:true);await fixture.Admit(scope);
            var first=fixture.Definition(scope);var second=fixture.Definition(scope);
            var results=await Task.WhenAll(Task.Run(()=>fixture.Call(first)),Task.Run(()=>fixture.Call(second)));
            Equal(1,fixture.Launcher.Created);Equal(1,results.Count(result=>result.State=="running"));
            var original=results.Single(result=>result.State=="running");
            var copyRequest=new OperationRequest("cp",new Dictionary<string,string>{{"src",fixture.Script},{"dst",Path.Combine(fixture.Root,"must-not-copy")}},Guid.NewGuid().ToString("N"),System:true,ConnectionAuthority:ApprovedConnectionFixture.DefaultAuthority);
            Equal("admin_lane_busy",(await fixture.Broker.HandleSystemAsync(copyRequest,fixture.Peer,default)).Err);
            Equal("admin_lane_busy",(await fixture.Broker.HandleApplicationAsync(copyRequest with{Root=null,System=false,ConnectionAuthority=null},default)).Err);
            True(!File.Exists(Path.Combine(fixture.Root,"must-not-copy")),"no overlapping privileged mutation");
            Equal("admin_lane_busy",(await fixture.Broker.HandleUserAsync(copyRequest with{Root=null,System=false,ConnectionAuthority=null},fixture.Peer,default)).Err);
            Equal("admin_reconciliation_required",results.Single(result=>result.State!="running").Err);
            fixture.Clock.Now=fixture.Clock.Now.AddHours(3);
            Equal(original.Jid,(await fixture.Stat(original.Jid)).Jid);
            var originalDefinition=original.Rid==first.ExpectedRid?first:second;
            Equal(original.Jid,(await fixture.Call(originalDefinition)).Jid);
            Equal("admin_admission_expired",(await fixture.Call(fixture.Definition(scope))).Err);
            fixture.Clock.Now=DateTimeOffset.UtcNow;
            fixture.Manager.Dispose();fixture.ReplaceManager();
            Equal("recovery_required",(await fixture.Stat(original.Jid)).State);
            Equal("admin_reconciliation_required",(await fixture.Call(fixture.Definition(scope))).Err);
            var proof=new OwnerAdminResolution(1,original.Jid,original.Rid,(string)original.Data!["sha"]!,Convert.ToInt32(original.Data["pid"]),(string)original.Data["start"]!,new string('a',64),true);
            Equal("admin_lane_busy",(await fixture.CallRaw("reconcile",new(){["jid"]=original.Jid},fixture.Body(proof))).Err);
            fixture.Launcher.Last!.Exit=0;
            var reconciled=await fixture.CallRaw("reconcile",new(){["jid"]=original.Jid},fixture.Body(proof));
            Equal("reconciled",reconciled.State);True((bool)reconciled.Data!["effect_fence_released"]!,"explicit quiescence review");
            True(reconciled.Data["exit"] is null,"unknown exit was not invented");
            await fixture.RecoverInterval(scope);
            Equal("running",(await fixture.Call(fixture.Definition(scope))).State);
        }
        using(var fixture=new Fixture(ordinaryUnknown:true))
        {
            var scope=fixture.Scope();True((await fixture.CallRaw("admit",new(),fixture.Body(scope))).Ok,"review before seeding pre-interval ordinary unknown");
            await fixture.Broker.RegisterTrayAsync(new(fixture.Peer.Sid,fixture.Peer.Session,"isolated-fake-callback"),fixture.Peer);
            var unknown=await fixture.Broker.HandleUserAsync(new("run",new Dictionary<string,string>{{"cfg","fixture"}},Guid.NewGuid().ToString("N")),fixture.Peer,default);
            Equal("recovery_required",unknown.State);
            fixture.OrdinaryEffect=unknown;
            var blocked=await fixture.Begin(scope);
            Equal("maintenance_busy",blocked.Err);
            var blockers=(IReadOnlyList<MaintenanceEffect>)blocked.Data!["blockers"]!;
            Equal(unknown.Jid,blockers.Single().Jid);Equal(0,fixture.Launcher.Created);
        }
        foreach(var phase in new[]{"created_suspended","running"})
        {
            using var fixture=new Fixture();var scope=fixture.Scope();await fixture.Admit(scope);
            var definition=fixture.Definition(scope);var injected=false;
            fixture.PersistHook=value=>{if(value==phase&&!injected){injected=true;throw new IOException("fixture storage failure");}};
            var receipt=await fixture.Call(definition);
            Equal("recovery_required",receipt.State);Equal("admin_persistence_unavailable",receipt.Err);
            Equal(1,fixture.Launcher.Created);True(receipt.Data!["pid"] is not null&&receipt.Data["start"] is not null,"original process evidence");
            Equal(receipt.Jid,(await fixture.Call(definition)).Jid);
            Equal(phase=="running"?1:0,fixture.Launcher.Last!.ResumeCount);
            True(!fixture.Launcher.Last.Disposed,"original monitor attached after write failure");
            Equal("admin_reconciliation_required",(await fixture.Call(fixture.Definition(scope))).Err);
            fixture.Launcher.Last.Exit=0;
            await fixture.WaitTerminal(receipt.Jid);
            Equal(1,fixture.Launcher.Created);
        }
        using(var fixture=new Fixture())
        {
            var scope=fixture.Scope();await fixture.Admit(scope);
            foreach(var op in new[]{"admit","run","stat","revoke","reconcile","begin","end","interval","ack"})
                foreach(var marker in new[]{false,true})
                {
                    var arg=new Dictionary<string,string>{{"op",op}};if(marker)arg["__execution_context_system"]="1";
                    Equal("auth",(await fixture.Broker.HandleApplicationAsync(new("admin",arg,Guid.NewGuid().ToString("N")),default)).Err);
                }
            Equal(0,fixture.Launcher.Created);
            Equal("running",(await fixture.Call(fixture.Definition(scope))).State);Equal(1,fixture.Launcher.Created);
        }
        var folders=WindowsOwnerAdminLauncher.OwnerFolders(new Dictionary<string,string>{{"APPDATA",@"D:\Redirected\Roaming"},{"LOCALAPPDATA",@"D:\Redirected\Local"}});
        Equal(@"D:\Redirected\Roaming",folders.AppData);Equal(@"D:\Redirected\Local",folders.LocalAppData);
    }

    internal static async Task NestedMutationAsync()
    {
        var variable=AgentOpsExecutionContext.MutationDepthEnvironmentVariable;
        var old=Environment.GetEnvironmentVariable(variable);
        var environment=new Dictionary<string,string>();
        AgentOpsExecutionContext.MarkMutationChildEnvironment(environment);
        try
        {
            Environment.SetEnvironmentVariable(variable,environment[variable]);
            var user=new CaptureDispatcher();var service=new CaptureDispatcher();var system=new CaptureDispatcher();
            await using var route=new RoutingDispatcher(user,service,system);
            var run=await route.DispatchAsync(new("run",new Dictionary<string,string>{{"cfg","fixture"}}));
            Equal("reentrant_mutation",run.Err);
            var admin=await route.DispatchAsync(new("admin",new Dictionary<string,string>{{"op","run"}},System:true,ConnectionAuthority:ApprovedConnectionFixture.DefaultAuthority));
            Equal("reentrant_mutation",admin.Err);
            Equal(0,user.Calls);Equal(0,service.Calls);Equal(0,system.Calls);
            True((await route.DispatchAsync(new("stat",new Dictionary<string,string>{{"src",@"C:\fixture"}}))).Ok,"ordinary read remains available");
            Equal(1,user.Calls);
        }
        finally{Environment.SetEnvironmentVariable(variable,old);}
    }
    private sealed class CaptureDispatcher:IOperationDispatcher
    {
        internal int Calls;
        internal OperationRequest? Last;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request,CancellationToken cancellationToken=default)
        {Calls++;Last=request;return Task.FromResult(new OperationReceipt(true,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"done",OperationNames.Public(request)));}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }

    internal static async Task IntervalCliMcpAsync()
    {
        foreach(var action in new[]{"begin","end","interval","ack"})
        {
            await using var dispatch=new CaptureDispatcher();
            var cfg=Guid.NewGuid().ToString("N");var rid=Guid.NewGuid().ToString("N");var epoch=Guid.NewGuid().ToString("N");var sha=new string('a',64);
            var args=new List<string>{"--json","admin",action,"cfg="+cfg,"system=true","rid="+rid};
            var fields=new Dictionary<string,object?>{{"op","admin_"+action},{"cfg",cfg},{"system",true},{"rid",rid}};
            if(action!="interval"){args.Add("sha="+sha);fields["sha"]=sha;}
            if(action=="end"){args.Add("epoch="+epoch);fields["epoch"]=epoch;}
            // This dispatcher proves transport shape only. Strict proof JSON is
            // admitted by controller tests, never by this capture stub.
            const string proof="{\"version\":1}";
            if(action is "ack" or "end"){args.Add("--stdin");fields["text"]=proof;}
            var output=new StringWriter();var errors=new StringWriter();
            var cliExit=await PaoProgram.RunCliAsync(args.ToArray(),dispatch,output,errors,default,action is "ack" or "end"?new StringReader(proof):null);
            True(cliExit==0,$"interval CLI {action} exit={cliExit}: {errors}");
            Equal(action,dispatch.Last!.Arg["op"]);Equal(rid,dispatch.Last.Rid);True(dispatch.Last.System && dispatch.Last.Root is null && dispatch.Last.ConnectionAuthority is null,"public intent contains no approval credential");
            Equal(action is "ack" or "end"?proof:null,dispatch.Last.Body);
            var result=await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(fields),dispatch,default);
            True(result["isError"]?.GetValue<bool>()!=true,"fixed interval MCP operation admitted");
            Equal(2,dispatch.Calls);Equal(action,dispatch.Last!.Arg["op"]);Equal(rid,dispatch.Last.Rid);
            if(action=="end")Equal(epoch,dispatch.Last.Arg["epoch"]);
            fields["wait"]=false;
            var rejected=await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(fields),dispatch,default);
            True(rejected["isError"]!.GetValue<bool>(),"interval controls reject wait");Equal(2,dispatch.Calls);fields.Remove("wait");
            if(action is "ack" or "end")fields.Remove("text");
            else if(action=="end")fields.Remove("epoch");
            else fields["text"]=proof;
            rejected=await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(fields),dispatch,default);
            True(rejected["isError"]!.GetValue<bool>(),"interval operation rejects missing required or irrelevant body");Equal(2,dispatch.Calls);
        }
    }

    internal static async Task ArgvAsync()
    {
        var argv = new[] { "", "plain", "two words", "quote\"here", "C:\\space here\\", "$(must stay data)", "a;b&c", "λ字符" };
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var command = WindowsOwnerAdminLauncher.CommandLine(dotnet, new[] { typeof(OwnerAdminExecutionTests).Assembly.Location,"--owner-admin-argv-probe" }.Concat(argv).ToArray());
        var executablePrefix = WindowsOwnerAdminLauncher.CommandLine(dotnet, []);
        using var process = Process.Start(new ProcessStartInfo(dotnet)
        { Arguments=command[(executablePrefix.Length+1)..], UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Equal(0, process.ExitCode);
        True(argv.SequenceEqual(JsonSerializer.Deserialize<string[]>(output)!), "real medium-token argv roundtrip");
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(),"pao-admin-"+Guid.NewGuid().ToString("N"));
        internal string Executable { get; }
        internal string Script { get; }
        internal LocalPeer Peer { get; } = LocalIdentity.Current();
        internal FakeLauncher Launcher { get; } = new();
        internal FakeClock Clock {get;}=new();
        internal Action<string>? PersistHook;
        internal string? Epoch;
        internal OperationReceipt? OrdinaryEffect;
        private readonly MaintenanceHostIdentity _host;
        internal OwnerAdminExecution Manager { get; private set; }
        internal ApprovedConnectionFixture Approval { get; }
        internal AuthenticatedServiceBroker Broker { get; private set; }
        internal Fixture(bool ordinaryUnknown=false)
        {
            Directory.CreateDirectory(Root);
            _host=new(Peer.Sid,Peer.Session,Peer.ProcessId??1U,DateTime.UtcNow.ToString("O"),Peer.ApplicationPath!);
            Executable=Path.Combine(Root,"pwsh.exe"); Script=Path.Combine(Root,"reviewed.ps1");
            File.WriteAllText(Executable,"fixture executable - never launched"); File.WriteAllText(Script,"#revision one\n");
            var state = Path.Combine(Root,"state"); Directory.CreateDirectory(state);
            Approval=new ApprovedConnectionFixture(Peer);
            Manager=new OwnerAdminExecution(state,Peer.Sid,Launcher,protectState:false,clock:Clock,beforePersist:phase=>PersistHook?.Invoke(phase));
            UserDispatch=ordinaryUnknown?(_,_,request,_)=>Task.FromResult(new OperationReceipt(false,Guid.NewGuid().ToString("N"),request.Rid!,"recovery_required",OperationNames.Public(request),"recovery_required")):null;
            Broker=MakeBroker();
        }
        private readonly Func<LocalPeer,string,OperationRequest,CancellationToken,Task<OperationReceipt>>? UserDispatch;
        private AuthenticatedServiceBroker MakeBroker()
        {
            var broker=new AuthenticatedServiceBroker(Path.Combine(Root,"router"),Peer.Sid,Approval.Authorize,false,UserDispatch,null,null,Manager,ownerHostVerifier:Approval.IsTrustedHost);
            // Deterministic protocol fixtures: no user host, service, or process
            // is contacted. Runtime host authentication has separate tests.
            Manager.SetMaintenanceProbes((request,_)=>Task.FromResult(Probe(request,"user")),request=>Probe(request,"broker"),_=>{});
            return broker;
        }
        private MaintenanceProbeResult Probe(MaintenanceProbeRequest request,string store)
        {
            IReadOnlyList<MaintenanceEffect> effects=store=="broker"&&OrdinaryEffect is{} receipt
                ?[new("broker",receipt.Jid,receipt.Rid,receipt.State,receipt.Op,
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Body(receipt)))),1)]:[];
            return new(1,request.Epoch,_host,effects,effects.Count==0?request.After:1,false,0,0);
        }
        internal void ReplaceManager()
        { Broker.DisposeAsync().AsTask().GetAwaiter().GetResult(); Manager=new OwnerAdminExecution(Path.Combine(Root,"state"),Peer.Sid,Launcher,protectState:false,clock:Clock,beforePersist:phase=>PersistHook?.Invoke(phase)); Broker=MakeBroker(); }
        internal OwnerAdminAuthorization Scope(bool revisions=false)=>new(1,Guid.NewGuid().ToString("N"),TaskId,Peer.Sid,Peer.Session,
            Peer.ApplicationPath!,Hash(Peer.ApplicationPath!),Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),DateTimeOffset.UtcNow.AddHours(2),
            [new(Executable,Hash(Executable))],[new(Script,Hash(Script),revisions)],[Root],
            [Root,Path.GetDirectoryName(Peer.ApplicationPath!)!],[Root],["filesystem"],true,true,true);
        internal OwnerAdminDefinition Definition(OwnerAdminAuthorization scope)=>new(1,scope.Id,scope.TaskId,Peer.Sid,Peer.Session,Peer.ApplicationPath!,scope.ClientSha256,scope.OwnerProfile,
            Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow.AddHours(1),"pwsh",Executable,Hash(Executable),Root,["-ResultPath",Path.Combine(Root,"result.json")],[],scope.ReadRoots,scope.WriteRoots,["filesystem"],true,Script,Hash(Script),Epoch);
        internal string Body<T>(T value)=>JsonSerializer.Serialize(value,OwnerAdminExecution.Json);
        internal OperationRequest Request(string op,OwnerAdminDefinition definition)=>new("admin",new Dictionary<string,string>
            { ["op"]=op,["cfg"]=definition.Id,["sha"]=OwnerAdminExecution.Digest(definition)},definition.ExpectedRid,Body:Body(definition));
        internal string ScopeSha(OwnerAdminAuthorization scope)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Body(scope))));
        internal async Task<OperationReceipt> Admit(OwnerAdminAuthorization scope)
        {
            var admitted=await CallRaw("admit",new(),Body(scope));
            if(!admitted.Ok)return admitted;
            var begun=await Begin(scope);
            return begun.Ok?admitted:begun;
        }
        internal async Task<OperationReceipt> Begin(OwnerAdminAuthorization scope)
        {
            var receipt=await CallRaw("begin",new(){["cfg"]=scope.Id,["sha"]=ScopeSha(scope)});
            if(receipt.Ok)Epoch=receipt.Data!["epoch"]!.ToString();
            return receipt;
        }
        internal async Task<OperationReceipt> End(OwnerAdminAuthorization scope)
        {
            var original=await CallRaw("interval",new(){["cfg"]=scope.Id});
            var proof=new MaintenanceHandback(1,Epoch!,scope.Id,original.Jid,original.Rid,original.Data!["native_receipts_sha"]!.ToString()!,new string('c',64),true);
            return await CallRaw("end",new(){["cfg"]=scope.Id,["sha"]=ScopeSha(scope),["epoch"]=Epoch!},Body(proof));
        }
        internal async Task RecoverInterval(OwnerAdminAuthorization scope)
        {
            var original=await CallRaw("interval",new(){["cfg"]=scope.Id});
            if(original.Data?.GetValueOrDefault("phase")?.ToString()=="uncertain")
            {
                var proof=new MaintenanceAcknowledgement(1,"interval",original.Jid,original.Rid,original.Data["receipt_sha"]!.ToString()!,
                    new string('b',64),scope.Id,scope.TaskId,Peer.Sid,Peer.Session,Clock.Now,true);
                True((await CallRaw("ack",new(){["cfg"]=scope.Id,["sha"]=ScopeSha(scope)},Body(proof))).Ok,"explicit interval uncertainty review");
            }
            True((await End(scope)).Ok,"reviewed old interval handback");
            True((await Begin(scope)).Ok,"new interval after reviewed handback");
        }
        internal Task<OperationReceipt> Call(OwnerAdminDefinition definition,LocalPeer? peer=null,bool approved=true,string? sha=null,string? tmo=null,CancellationToken cancellation=default)
        { var values=new Dictionary<string,string>{["cfg"]=definition.Id,["sha"]=sha??OwnerAdminExecution.Digest(definition)}; if(tmo is not null)values["tmo"]=tmo; return CallRaw("run",values,Body(definition),definition.ExpectedRid,peer,approved,cancellation); }
        internal Task<OperationReceipt> CallRaw(string op,Dictionary<string,string> values,string? body=null,string? rid=null,LocalPeer? peer=null,bool approved=true,CancellationToken cancellation=default)
        { values["op"]=op; return Broker.HandleSystemAsync(new("admin",values,rid??Guid.NewGuid().ToString("N"),Body:body,System:true,ConnectionAuthority:approved?Approval.Current:null),peer??Peer,cancellation); }
        internal Task<OperationReceipt> Stat(string jid)=>CallRaw("stat",new(){["jid"]=jid});
        internal async Task<OperationReceipt> WaitTerminal(string jid)
        { for(var attempt=0;attempt<100;attempt++){var receipt=await Stat(jid);if(receipt.State is not("starting" or "running"))return receipt;await Task.Delay(20);}throw new TimeoutException("fixture monitor"); }
        public void Dispose()
        { Broker.DisposeAsync().AsTask().GetAwaiter().GetResult(); Manager.Dispose(); /* Preserve bounded failed-test evidence; no broad cleanup. */ }
    }
    private sealed class FakeLauncher : IOwnerAdminLauncher
    {
        internal bool Elevated=true; internal int Created; internal string? Failure; internal FakeProcess? Last;
        internal Action<OwnerAdminDefinition>? PrepareHook;
        public bool IsElevatedOwner(LocalPeer peer)=>Elevated;
        public bool IsOriginalProcessAlive(int pid,string start)=>Last is{Exit:null} process&&process.Pid==pid&&process.Start==start;
        public void ValidateProtectedInputs(OwnerAdminDefinition definition) { PrepareHook?.Invoke(definition); }
        public IOwnerAdminProcess CreateSuspended(OwnerAdminDefinition definition)
        { Interlocked.Increment(ref Created); if(Failure is not null){var error=new AgentOpsException(Failure,inner:new System.ComponentModel.Win32Exception(1314));error.Data["native_phase"]="fixture_token_query";throw error;}return Last=new FakeProcess(); }
    }
    private sealed class FakeClock:TimeProvider {internal DateTimeOffset Now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class FakeProcess : IOwnerAdminProcess
    {
        internal int? Exit; internal int ResumeCount; internal bool Disposed;
        public int Pid { get; }=42000;
        public string Start { get; }=DateTime.UtcNow.ToString("O");
        public void Resume()=>ResumeCount++;
        public bool TryGetExit(out int exit){exit=Exit??0;return Exit.HasValue;}
        public void Dispose()=>Disposed=true;
    }
    private sealed class TestDispatcher(Fixture fixture):IOperationDispatcher
    {
        internal int Calls;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request,CancellationToken cancellationToken=default)
        { Calls++;return fixture.Broker.HandleSystemAsync(fixture.Approval.System(request),fixture.Peer,cancellationToken); }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();}
    private static void True(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static void Equal<T>(T expected,T actual){if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new InvalidOperationException($"expected={expected} actual={actual}");}
    private static void Throws(string code,Action action){try{action();}catch(AgentOpsException ex)when(ex.Code==code){return;}catch(JsonException)when(code=="invalid"){return;}throw new InvalidOperationException("expected failure "+code);}
}
