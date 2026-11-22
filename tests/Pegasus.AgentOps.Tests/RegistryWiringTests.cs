using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Windows.Registry;

internal static class RegistryWiringTests
{
    private const string OwnerSid = "S-1-5-21-100-200-300-1001";
    private static readonly RegistryKeyIdentity Target = new(RegistryHiveId.CurrentUser, RegistryViewId.Registry64, "Software\\Registry fixture 雪😀 ");
    private static readonly string ZeroDigest = new('0', 64);

    internal static async Task RunAsync()
    {
        await ContractAsync();
        await OwnerAdmissionAsync();
        await ReceiptRecoveryAsync();
        await EnginePersistenceAsync();
        await EngineRecoveryAsync();
        await EngineRefusalAsync();
        await FailedRegistryApplicationCollisionAsync();
        await InFlightRegistryReplayAndWaitAsync();
    }

    internal static Task ContractAsync()
    {
        Check(RegistryOperationContract.Actions.Count == 7 && RegistryOperationContract.Actions.All(RegistryOperationContract.IsKnownAction),
            "read-only action discovery comes from the closed operation map");
        foreach (var (action, operation) in new[]
        {
            ("inspect", RegistryOperation.InspectKey), ("browse", RegistryOperation.Browse), ("read", RegistryOperation.ReadValue),
            ("create", RegistryOperation.CreateKey), ("set", RegistryOperation.SetValue),
            ("delete_value", RegistryOperation.DeleteValue), ("delete_empty_key", RegistryOperation.DeleteEmptyKey)
        })
        {
            var parsed = RegistryOperationContract.Parse(Request(action, Body(action), "fixture-" + action));
            Check(parsed.Operation == operation && parsed.Action == action, "closed verb typed mapping");
            Check(parsed.Key == Target && parsed.RequestIdentity.Length == 64 && parsed.ResourceIdentity.Length == 64, "bounded exact identities");
            Check(parsed.IsMutation == !RegistryOperationContract.IsReadOnly(action), "closed read classification");
            Check(RegistryOperationContract.PublicName(action) == "registry_" + action, "fixed public name");
        }
        var read = RegistryOperationContract.Parse("read", Body("read", valueName: ""));
        Check(read.ValueIdentity!.ValueName == "", "empty default value preserved");
        var quoted = " 'Ω 日本 😀' ";
        var exact = RegistryOperationContract.Parse("read", Body("read", valueName: quoted));
        Check(exact.Key.KeyPath.EndsWith(' ') && exact.ValueIdentity!.ValueName == quoted, "names are not trimmed or unwrapped");
        var reordered = JsonNode.Parse(Body("set"))!.AsObject();
        var reverse = new JsonObject();
        foreach (var pair in reordered.Reverse()) reverse.Add(pair.Key, pair.Value?.DeepClone());
        var set = RegistryOperationContract.Parse("set", Body("set"));
        Check(set.RequestIdentity == RegistryOperationContract.Parse("set", reverse.ToJsonString()).RequestIdentity, "JSON field order is not request identity");
        Check(set.ResourceIdentity == RegistryOperationContract.Parse("set", Body("set", base64: "Ag==")).ResourceIdentity,
            "changing data preserves exact resource identity");
        Check(set.RequestIdentity != RegistryOperationContract.Parse("set", Body("set", base64: "Ag==")).RequestIdentity,
            "changing data changes immutable request identity");
        Check(set.RequestIdentity != RegistryOperationContract.Parse("set", Body("set", view: 32)).RequestIdentity, "view changes request identity");
        Check(set.ResourceIdentity != RegistryOperationContract.Parse("set", Body("set", hive: "local_machine")).ResourceIdentity,
            "hive changes resource identity");

        foreach (var action in new[] { "", "READ", "delete_tree", "eval", "set_acl", "load_hive" })
            Refused(() => RegistryOperationContract.Parse(action, Body("inspect")), "unknown operation rejected");
        foreach (var invalid in new[]
        {
            "null", "[]", "{}", "{\"version\":1,\"version\":1}",
            Body("inspect").Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            Body("inspect").Replace("\"hive\":\"current_user\"", "\"hive\":\"HKCU\"", StringComparison.Ordinal),
            Body("inspect").Replace("\"view\":64", "\"view\":0", StringComparison.Ordinal),
            Body("inspect").Replace("\"view\":64", "\"view\":\"64\"", StringComparison.Ordinal),
            Body("inspect").Replace("\"view\":64", "\"view\":64,\"view\":32", StringComparison.Ordinal),
            Body("inspect").Replace("\"version\":1", "\"version\":1,\"force\":true", StringComparison.Ordinal),
            Body("inspect").Insert(1, "/*comment*/"), Body("inspect")[..^1] + ",}",
            Body("read", valueName: "bad\0name"), Body("read", valueName: new string('n', RegistryLimits.MaxValueNameChars + 1)),
            Body("set", base64: "AA==\n"), Body("set", type: "link"), Body("set", type: "dword"),
            Body("set").Replace("\"valueDigest\":null", "\"valueDigest\":null,\"valueDigest\":null", StringComparison.Ordinal),
            Body("set").Replace(",\"valueDigest\":null", "", StringComparison.Ordinal),
            Body("set", base64: Convert.ToBase64String(new byte[RegistryLimits.MaxValueBytes + 1])),
            new string(' ', RegistryOperationContract.MaxRequestBytes + 1),
            "{\"extra\":" + new string('[', 20) + "0" + new string(']', 20) + "}"
        })
            Refused(() => RegistryOperationContract.Parse("set", invalid), "malformed/ambiguous/oversized JSON rejected");
        foreach (var invalid in new[]
        {
            Body("inspect").Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            Body("inspect").Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            Body("inspect").Replace("\"hive\":\"current_user\"", "\"hive\":\"HKCU\"", StringComparison.Ordinal),
            Body("inspect").Replace("\"view\":64", "\"view\":0", StringComparison.Ordinal),
            Body("inspect").Replace("\"view\":64", "\"view\":\"64\"", StringComparison.Ordinal),
            Body("inspect").Replace("\"view\":64", "\"view\":64,\"view\":32", StringComparison.Ordinal)
        }) Refused(() => RegistryOperationContract.Parse("inspect", invalid), "closed version/hive/view fields and duplicates rejected");
        foreach (var path in new[] { "\\Software", "Software\\..\\target", "Software\\\\target", "Software/target", "Software\\bad\0name" })
        {
            var body = JsonNode.Parse(Body("inspect"))!.AsObject();
            body["key"]!["keyPath"] = path;
            Refused(() => RegistryOperationContract.Parse("inspect", body.ToJsonString()), "module path validation reused");
        }
        var root = JsonNode.Parse(Body("inspect"))!.AsObject();
        root["key"]!["keyPath"] = "";
        Check(RegistryOperationContract.Parse("inspect", root.ToJsonString()).Key.KeyPath == "", "explicit read root admitted");
        var rootDelete = JsonNode.Parse(Body("delete_empty_key"))!.AsObject();
        rootDelete["key"]!["keyPath"] = "";
        Refused(() => RegistryOperationContract.Parse("delete_empty_key", rootDelete.ToJsonString()), "root mutation refused");
        foreach (var argument in new[] { "root", "wait", "src", "dst", "arg0", "env0", "__execution_context_system", "__authenticated_frozen", "admin" })
        {
            var request = Request("inspect", Body("inspect"));
            var arguments = new Dictionary<string, string>(request.Arg) { [argument] = "fixture" };
            Refused(() => RegistryOperationContract.Parse(request with { Arg = arguments }), "extra envelope field refused");
        }
        var valid = Request("read", Body("read"));
        foreach (var request in new[]
        {
            valid with { Root = "AB12" }, valid with { Ws = "fixture" }, valid with { Cwd = @"C:\fixture" },
            valid with { PathRoots = new Dictionary<string, string> { ["src"] = @"C:\fixture" } },
            Request("set", Body("set")) with { Rid = null },
            valid with { Arg = new Dictionary<string, string> { ["op"] = "read", ["rid"] = "different" } },
            valid with { Arg = new Dictionary<string, string> { ["op"] = "read", [OperationRequest.BodyOmittedKey] = "1" }, Body = null }
        }) Refused(() => RegistryOperationContract.Parse(request), "invalid context/identity/body refused");
        var marker = valid with { Arg = new Dictionary<string, string> { ["op"] = "read", [OperationRequest.BodyOmittedKey] = "1" } };
        Check(RegistryOperationContract.Parse(marker).Action == "read", "known body marker carries no extra authority");
        var pagingNative = new MemoryRegistry();
        pagingNative.Seed("one"); pagingNative.Seed("two"); pagingNative.Seed("three");
        var paging = new RegistryOwnerBinding(OwnerSid, () => new(OwnerSid, 7), admission => new RegistryOperations(pagingNative, admission));
        var page = paging.Operations.Browse(new(Target, RegistryBrowseKind.Values, 1)).Value!;
        Check(page.HasMore && page.Continuation is not null, "fixture produces a real module continuation");
        var cursorBody = JsonNode.Parse(Body("browse"))!.AsObject();
        cursorBody["cursor"] = JsonSerializer.SerializeToNode(page.Continuation, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Check(RegistryOperationContract.Parse("browse", cursorBody.ToJsonString()).Browse!.Cursor == page.Continuation,
            "returned typed cursor round trips without name/enum rewriting");
        cursorBody["cursor"]!["kind"] = 1;
        Refused(() => RegistryOperationContract.Parse("browse", cursorBody.ToJsonString()), "cursor kind substitution refused");
        return Task.CompletedTask;
    }

    internal static Task OwnerAdmissionAsync()
    {
        var native = new MemoryRegistry();
        var peer = new LocalPeer(OwnerSid, 7, @"C:\fixture\host.exe", 123, false);
        var binding = new RegistryOwnerBinding(OwnerSid, () => peer, admission => new RegistryOperations(native, admission));
        var inspect = RegistryOperationContract.Parse("inspect", Body("inspect"));
        Check(Execute(inspect, null).Err == "auth" && native.Calls == 0, "default composition denies without registry access");
        foreach (var denied in new[]
        {
            peer with { Sid = "S-1-5-18", Session = 0 }, peer with { Sid = "S-1-5-21-100-200-300-1002" },
            peer with { Session = 0 }, peer with { Session = 8 }, peer with { Elevated = true }
        })
        {
            peer = denied;
            Check(Execute(inspect, binding).Err == "auth" && native.Calls == 0, "wrong owner/session/elevation refused before OS");
        }
        peer = new(OwnerSid, 7, @"C:\fixture\host.exe", 123, false);
        var result = Execute(inspect, binding);
        Check(result.Ok && native.Calls > 0, "bound ordinary owner executes injected boundary");
        var read = Execute(RegistryOperationContract.Parse("read", Body("read", valueName: "")), binding);
        var snapshot = (RegistryValueSnapshot)read.Data!["registry"]!;
        Check(!snapshot.Exists && snapshot.Identity.ValueName == "", "missing unnamed value is exact live result");
        var body = Body("set", valueName: "", generation: snapshot.Key.Generation);
        var set = Execute(RegistryOperationContract.Parse("set", body), binding);
        Check(set.Ok && native.Writes == 1 && set.Data!["atomic_compare_and_swap"] is false, "guarded default-value mutation is non-atomic");
        var replayedPreimage = Execute(RegistryOperationContract.Parse("set", body), binding);
        Check(!replayedPreimage.Ok && replayedPreimage.Err == "precondition" && native.Writes == 1, "changed preimage does not write twice");
        var current = Execute(RegistryOperationContract.Parse("read", Body("read", valueName: "")), binding);
        var value = (RegistryValueSnapshot)current.Data!["registry"]!;
        Check(value.Exists && value.Value!.Base64 == "AQ==", "live raw bytes preserved");
        var deletion = Execute(RegistryOperationContract.Parse("delete_value", Body("delete_value", valueName: "", generation: value.Key.Generation,
            digest: value.Digest)), binding);
        Check(deletion.Ok && native.Writes == 2, "exact guarded value deletion mapped");
        var before = native.Calls;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var cancelled = RegistryReceiptAdapter.Execute(RegistryOperationContract.Parse("set", Body("set")), binding, "fixture-jid", "fixture-rid", canceled.Token);
        Check(cancelled.Err == "canceled" && !cancelled.Partial && native.Calls == before, "pre-invocation cancellation is not uncertainty");
        var absent = (RegistryValueSnapshot)Execute(RegistryOperationContract.Parse("read", Body("read", valueName: "lost")), binding).Data!["registry"]!;
        native.AfterWrite = () => throw new RegistryNativeException(RegistryNativeFailure.Failed, "fixture_reply_lost");
        var uncertain = Execute(RegistryOperationContract.Parse("set", Body("set", valueName: "lost", generation: absent.Key.Generation)), binding);
        Check(uncertain.State == "recovery_required" && uncertain.Partial && uncertain.Failure!.Execution.Value == OperationExecutionState.Unknown &&
            uncertain.Failure.Retry.Value == OperationRetryDisposition.NotSupported && native.Writes == 3,
            "consumed uncertain native write cannot be retried");
        return Task.CompletedTask;
    }

    internal static Task ReceiptRecoveryAsync()
    {
        var native = new MemoryRegistry();
        var binding = new RegistryOwnerBinding(OwnerSid, () => new(OwnerSid, 7, null, null, false), admission => new RegistryOperations(native, admission));
        const string valueName = "fixture name kept only in live body";
        var readBody = Body("read", valueName: valueName);
        var read = Execute(RegistryOperationContract.Parse("read", readBody), binding);
        var snapshot = (RegistryValueSnapshot)read.Data!["registry"]!;
        var request = Request("set", Body("set", valueName: valueName, generation: snapshot.Key.Generation,
            base64: Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture raw registry content"))), "original-registry-rid");
        var initial = AgentOpsEngine.RegistryInitialReceipt(new(true, "original-jid", request.Rid!, "queued", "registry_set"), request);
        Check(initial.Data!["requestIdentity"] is string { Length: 64 } && initial.Data.Count == 1, "initial claim retains only bounded request hash");
        var live = RegistryReceiptAdapter.Execute(RegistryOperationContract.Parse(request), binding, "original-jid", request.Rid!, CancellationToken.None);
        Check(live.Ok && native.Writes == 1, "fixture mutation completes once");
        var durable = AgentOpsEngine.RegistryDurableReceipt(live with { Detail = "fixture detail must not persist" }, request);
        var serialized = JsonSerializer.Serialize(durable);
        Check(!serialized.Contains("fixture raw registry content", StringComparison.Ordinal) &&
            !serialized.Contains("fixture detail", StringComparison.Ordinal) && !serialized.Contains(valueName, StringComparison.Ordinal) &&
            !serialized.Contains(Target.KeyPath, StringComparison.Ordinal) && !serialized.Contains("Base64", StringComparison.Ordinal) &&
            !durable.Data!.ContainsKey("registry") && !durable.Data.ContainsKey("resource_identity"), "durable projection excludes targets/value/body/result");
        var liveReadRequest = Request("read", readBody);
        var liveRead = Execute(RegistryOperationContract.Parse(liveReadRequest), binding);
        var rawRead = (RegistryValueSnapshot)liveRead.Data!["registry"]!;
        var durableRead = AgentOpsEngine.RegistryDurableReceipt(liveRead, liveReadRequest);
        Check(rawRead.Exists && rawRead.Value!.Base64 == Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture raw registry content")) &&
            !JsonSerializer.Serialize(durableRead).Contains(rawRead.Value.Base64, StringComparison.Ordinal) &&
            !durableRead.Data!.ContainsKey("registry"), "raw exact read result cannot enter durable history");
        Check(AgentOpsEngine.RegistryReplayMatches(request, durable), "same request identity replays original metadata");
        var decoded = JsonSerializer.Deserialize<OperationReceipt>(JsonSerializer.Serialize(durable))!;
        var projectedAgain = AgentOpsEngine.RegistryDurableReceipt(decoded, request);
        Check(projectedAgain.Data!["registry_write_invoked"] is true && projectedAgain.Data["atomic_compare_and_swap"] is false &&
            AgentOpsEngine.RegistryReplayMatches(request, projectedAgain), "deserialized primitive metadata and hash survive safe projection");
        var changed = request with { Body = Body("set", valueName: valueName, generation: snapshot.Key.Generation, base64: "Ag==") };
        Check(!AgentOpsEngine.RegistryReplayMatches(changed, durable), "same RID with changed body refuses");
        Check(!AgentOpsEngine.RegistryReplayMatches(request, durable with { Op = "mkdir" }), "unrelated operation RID collision refuses");
        var compact = durable with { Data = new Dictionary<string, object?>
        {
            ["requestIdentity"] = durable.Data!["requestIdentity"], ["history_pruned"] = true,
            ["history_discarded"] = false, ["log_available"] = false
        } };
        var before = native.Calls;
        Check(AgentOpsEngine.IsHistoryPruned(compact) && AgentOpsEngine.RegistryReplayMatches(request, compact) &&
            !AgentOpsEngine.RegistryReplayMatches(changed, compact) && native.Calls == before, "pruned metadata-only replay never reexecutes");
        Check(ReferenceEquals(compact, AgentOpsEngine.RegistryDurableReceipt(compact, request)), "pruned tombstone is not reinflated or replaced");
        var persisted = request with { Body = null, Arg = new Dictionary<string, string> { ["op"] = "set", [OperationRequest.BodyOmittedKey] = "1" } };
        var lost = RegistryReceiptAdapter.LostBody(persisted, "original-jid", request.Rid!);
        Check(lost.State == "recovery_required" && lost.Partial && lost.Failure!.Retry.Value == OperationRetryDisposition.NotSupported &&
            native.Calls == before && !AgentOpsEngine.RegistryReplayMatches(persisted, durable), "lost body never reconstructs or retries native effects");
        var durableLost = AgentOpsEngine.RegistryDurableReceipt(lost with { Data = initial.Data }, persisted);
        Check(durableLost.Data!["requestIdentity"]!.ToString() == initial.Data!["requestIdentity"]!.ToString(), "lost-body receipt keeps original hash barrier");
        var recovered = AgentOpsEngine.RegistryDurableReceipt(lost, persisted, durable);
        Check(recovered.State == "recovery_required" && recovered.Data!["requestIdentity"]!.ToString() == initial.Data!["requestIdentity"]!.ToString() &&
            native.Calls == before, "fresh recovery receipt inherits only original matching hash");
        Check(AgentOpsEngine.RegistryDurableReceipt(lost, null, durable).Data!["requestIdentity"]!.ToString() == initial.Data!["requestIdentity"]!.ToString() &&
            native.Calls == before, "request-less recovery uses fixed receipt metadata only");
        var foreignPrior = durable with { Rid = "another-rid" };
        Check(!AgentOpsEngine.RegistryDurableReceipt(lost, persisted, foreignPrior).Data!.ContainsKey("requestIdentity"),
            "recovery cannot adopt a different original identity");
        Check(ReferenceEquals(compact, AgentOpsEngine.RegistryDurableReceipt(lost, persisted, compact)) && native.Calls == before,
            "fresh recovery cannot rewrite a matching pruned tombstone");
        return Task.CompletedTask;
    }

    internal static async Task EnginePersistenceAsync()
    {
        using var fixture = new EngineFixture();
        var native = new MemoryRegistry();
        await using var engine = fixture.Create(native);
        foreach (var action in new[] { "inspect", "browse", "read" })
        {
            var result = await engine.ExecuteAsync(Request(action, Body(action), "snapshot-" + action));
            Check(result.Ok && result.Op == "registry_" + action, "central engine dispatches exact fixed registry read");
        }
        Check(fixture.RowCount() == 0, "all registry reads are nondurable snapshots");
        var oldTarget = Path.Combine(fixture.Root, "old-operation");
        var old = await engine.ExecuteAsync(new("mkdir", new Dictionary<string, string> { ["dst"] = oldTarget }, "unrelated-old-rid"));
        Check(old.Ok, "scratch nonregistry operation established old RID");
        await RetiredAsync(engine);
        var rowsBeforeRead = fixture.RowCount();
        var freshRead = await engine.ExecuteAsync(Request("inspect", Body("inspect"), old.Rid));
        Check(freshRead.Ok && freshRead.Op == "registry_inspect" && freshRead.Jid != old.Jid && fixture.RowCount() == rowsBeforeRead,
            "read never substitutes an unrelated old RID receipt or creates a row");

        const string name = "isolated central value name";
        const string text = "isolated central raw registry bytes";
        var readRequest = Request("read", Body("read", valueName: name), "central-preimage");
        var preimage = (RegistryValueSnapshot)(await engine.ExecuteAsync(readRequest)).Data!["registry"]!;
        var request = Request("set", Body("set", valueName: name, generation: preimage.Key.Generation,
            base64: Convert.ToBase64String(Encoding.UTF8.GetBytes(text))), "central-original");
        var original = await engine.ExecuteAsync(request);
        Check(original.Ok && original.Data!["registry"] is RegistryMutationObservation { WriteInvoked: true, PostconditionObserved: true },
            "central live mutation returns its non-atomic observation");
        await RetiredAsync(engine);
        var persisted = fixture.Stored(request.Rid!);
        Check(persisted.State == "done" && persisted.Request is null && !persisted.Receipt.Data!.ContainsKey("registry") &&
            Hash(persisted.Receipt) == RegistryOperationContract.Parse(request).RequestIdentity,
            "central Finish stores only scrubbed metadata and original request identity");
        var exact = (RegistryValueSnapshot)(await engine.ExecuteAsync(readRequest)).Data!["registry"]!;
        Check(exact.Exists && exact.Value!.Base64 == Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), "central read returns exact live raw bytes");
        fixture.AssertHistoryExcludes(name, text, exact.Value!.Base64, Target.KeyPath, request.Body!);
        var calls = native.Calls;
        var rows = fixture.RowCount();
        var replay = await engine.ExecuteAsync(request);
        Check(replay.Ok && replay.Jid == original.Jid && replay.Rid == original.Rid && !replay.Data!.ContainsKey("registry") &&
            native.Calls == calls && native.Writes == 1 && fixture.RowCount() == rows,
            "same RID replays durable metadata only without registry execution");
        var changed = request with { Body = Body("set", valueName: name, generation: preimage.Key.Generation, base64: "Ag==") };
        var changedSelector = Request("delete_empty_key", Body("delete_empty_key"), request.Rid);
        foreach (var rejected in new[] { changed, changedSelector, request with { Rid = old.Rid } })
        {
            var result = await engine.ExecuteAsync(rejected);
            Check(!result.Ok && result.Err == "precondition" && native.Calls == calls && fixture.RowCount() == rows,
                "changed body, selector or nonregistry original RID refuses without effect");
        }
        var reverseTarget = Path.Combine(fixture.Root, "must-not-create");
        var reverse = await engine.ExecuteAsync(new("mkdir", new Dictionary<string, string> { ["dst"] = reverseTarget }, original.Rid));
        Check(!reverse.Ok && reverse.Err == "precondition" && !Directory.Exists(reverseTarget) && native.Calls == calls,
            "nonregistry mutation cannot reuse an existing registry RID");

        using (var closed = fixture.Admission.TryClose() ?? throw new InvalidOperationException("fixture admission close failed"))
        {
            var closedReplay = await engine.ExecuteAsync(request);
            Check(closedReplay.Jid == original.Jid && closedReplay.Ok && native.Calls == calls,
                "closed admission permits exact metadata replay only");
            foreach (var rejected in new[] { changed, changedSelector, request with { Rid = "closed-new-rid" } })
            {
                var result = await engine.ExecuteAsync(rejected);
                Check(!result.Ok && result.Err == "host_stopping" && native.Calls == calls && fixture.RowCount() == rows,
                    "closed user admission validates registry replay identity before returning original");
            }
            var closedReverse = await engine.ExecuteAsync(new("mkdir", new Dictionary<string, string> { ["dst"] = reverseTarget }, original.Rid));
            Check(!closedReverse.Ok && closedReverse.Err == "host_stopping" && !Directory.Exists(reverseTarget) && native.Calls == calls,
                "closed admission cannot rescue a different operation with a registry RID");
        }
        var before = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var prune = await engine.ExecuteAsync(new("job", new Dictionary<string, string>
            { ["op"] = "prune", ["mode"] = "details", ["before"] = before, ["max"] = "32" }, "registry-prune-fixture"));
        Check(prune.Ok, "existing central pruning path accepts isolated terminal history");
        await RetiredAsync(engine);
        var tombstone = fixture.Stored(request.Rid!);
        Check(AgentOpsEngine.IsHistoryPruned(tombstone.Receipt) && Hash(tombstone.Receipt) == Hash(persisted.Receipt),
            "actual SQLite prune preserves registry request identity tombstone");
        var compactReplay = await engine.ExecuteAsync(request);
        Check(compactReplay.Jid == original.Jid && AgentOpsEngine.IsHistoryPruned(compactReplay) && native.Calls == calls && native.Writes == 1,
            "central pruned replay stays metadata-only and never reexecutes");
        Check(!(await engine.ExecuteAsync(changed)).Ok && native.Calls == calls, "changed identity cannot reuse pruned registry RID");
    }

    internal static async Task EngineRecoveryAsync()
    {
        // This lane admits an actual queued central-engine request, then shuts
        // down while its maintenance source holds it before native execution.
        using (var fixture = new EngineFixture())
        {
            var native = new MemoryRegistry();
            var engine = fixture.Create(native);
            var request = Request("set", Body("set"), "actual-queued-body-loss");
            var identity = RegistryOperationContract.Parse(request).RequestIdentity;
            fixture.Mode.HoldAfterNextRead();
            var pending = engine.ExecuteAsync(request);
            try
            {
                await UntilAsync(() => fixture.TryStored(request.Rid!)?.State == "queued");
                var queued = fixture.Stored(request.Rid!);
                Check(Hash(queued.Receipt) == identity && queued.Request?.Body is null &&
                    queued.Request?.Arg.GetValueOrDefault(OperationRequest.BodyOmittedKey) == "1" && native.Calls == 0,
                    "actual queued row preserves identity and omitted-body barrier before native access");
                fixture.AssertHistoryExcludes(request.Body!, Target.KeyPath);
            }
            finally
            {
                await engine.DisposeAsync();
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            }
            Check(fixture.Stored(request.Rid!).State == "queued" && native.Calls == 0, "held shutdown preserves original accepted queue state");
            fixture.Mode.Open();
            await using var reopened = fixture.Create(native);
            var recovered = fixture.Stored(request.Rid!);
            Check(recovered.State == "recovery_required" && recovered.Request is null && Hash(recovered.Receipt) == identity && native.Calls == 0,
                "actual restart quarantines lost queued body with same identity and no native replay");
            var original = await reopened.ExecuteAsync(request);
            Check(original.Jid == recovered.Receipt.Jid && original.State == "recovery_required" && native.Calls == 0,
                "recovered original RID returns unresolved metadata only");
            var changed = request with { Body = Body("set", base64: "Ag==") };
            Check((await reopened.ExecuteAsync(changed)).Err == "precondition" && native.Calls == 0,
                "recovered identity barrier rejects changed body");
            var retry = await reopened.ExecuteAsync(new("job", new Dictionary<string, string> { ["op"] = "retry", ["jid"] = original.Jid }, "must-not-retry"));
            Check(!retry.Ok && native.Calls == 0, "job control never replays a body-loss registry mutation");
        }

        // Capture the actual central running receipt while the injected native
        // boundary is fenced. Restoring that capture below models a lost-process
        // durable snapshot; it is not evidence of a real process crash.
        using (var fixture = new EngineFixture())
        {
            var native = new MemoryRegistry();
            var request = Request("set", Body("set", generation: NativeGeneration(native)), "captured-running-body-loss");
            StoredRow running;
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            native.BeforeWrite = () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("fixture registry fence timed out");
            };
            await using (var engine = fixture.Create(native))
            {
                var pending = engine.ExecuteAsync(request);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    running = fixture.Stored(request.Rid!);
                    Check(running.State == "running" && Hash(running.Receipt) == RegistryOperationContract.Parse(request).RequestIdentity &&
                        running.Request?.Body is null && running.Request?.Arg.GetValueOrDefault(OperationRequest.BodyOmittedKey) == "1" && native.Writes == 0,
                        "actual running row preserves hash and no private body before mutation publication");
                }
                finally { release.Set(); native.BeforeWrite = null; }
                Check((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Ok && native.Writes == 1, "fixture original write completes once");
                await RetiredAsync(engine);
            }
            var calls = native.Calls;
            fixture.RestoreCapturedRow(running);
            await using var reopened = fixture.Create(native);
            var recovered = fixture.Stored(request.Rid!);
            Check(recovered.State == "recovery_required" && recovered.Request is null && Hash(recovered.Receipt) == Hash(running.Receipt) &&
                native.Calls == calls && native.Writes == 1, "running-snapshot recovery preserves uncertainty and never invokes native again");
            Check((await reopened.ExecuteAsync(request)).Jid == running.Receipt.Jid && native.Calls == calls,
                "running-snapshot original RID replays only retained metadata");
        }
    }

    internal static async Task EngineRefusalAsync()
    {
        using var fixture = new EngineFixture();
        var native = new MemoryRegistry();
        var request = Request("inspect", Body("inspect"), "refused-registry-read");
        await using (var unbound = fixture.Create(native, bind: false))
        {
            Check((await unbound.ExecuteAsync(request)).Err == "auth" && native.Calls == 0 && fixture.RowCount() == 0,
                "default unbound central engine refuses before registry access/history");
        }
        await using (var noAdmission = fixture.Create(native, bind: false, useAdmission: false))
        {
            var denied = false;
            try { noAdmission.BindRegistryOwner(Binding(native)); } catch (AgentOpsException error) when (error.Code == "auth") { denied = true; }
            Check(denied && (await noAdmission.ExecuteAsync(request)).Err == "auth" && native.Calls == 0,
                "engine without shared user admission cannot bind registry owner");
        }
        await using (var direct = fixture.Create(native, bind: false, direct: true))
        {
            var denied = false;
            try { direct.BindRegistryOwner(Binding(native)); } catch (AgentOpsException error) when (error.Code == "auth") { denied = true; }
            Check(denied && !(await direct.ExecuteAsync(request)).Ok && native.Calls == 0 && fixture.RowCount() == 0,
                "direct/system central engine cannot bind or dispatch registry");
        }
        await using (var bound = fixture.Create(native))
        {
            Check((await bound.ExecuteAuthenticatedFrozenDirectAsync(request)).Err == "auth" && native.Calls == 0,
                "service-frozen central route refuses registry even on a bound engine");
            Check((await bound.ExecuteAsync(request with { Root = "AB12" })).Err == "auth" && native.Calls == 0 && fixture.RowCount() == 0,
                "root cannot rescue or elevate a registry request");
            var valid = await bound.ExecuteAsync(request);
            Check(valid.Ok && fixture.RowCount() == 0, "normal bound owner read stays available and nondurable");
        }
    }

    internal static async Task FailedRegistryApplicationCollisionAsync()
    {
        foreach (var uncertain in new[] { false, true })
        {
            using var fixture = new EngineFixture();
            var native = new MemoryRegistry();
            await using var engine = fixture.Create(native);
            var request = Request("set", Body("set", generation: uncertain ? NativeGeneration(native) : ZeroDigest),
                uncertain ? "app-collision-registry-uncertain" : "app-collision-registry-failed");
            if (uncertain) native.AfterWrite = () => throw new RegistryNativeException(RegistryNativeFailure.Failed, "fixture_lost_reply");
            var original = await engine.ExecuteAsync(request);
            Check(!original.Ok && original.State == (uncertain ? "recovery_required" : "failed"),
                "fixture establishes a failed or unresolved registry mutation receipt");
            await RetiredAsync(engine);
            var stored = fixture.Stored(request.Rid!);
            var calls = native.Calls;
            var writes = native.Writes;
            var rows = fixture.RowCount();
            foreach (var action in new[] { "start", "status" })
            {
                var arguments = new Dictionary<string, string> { ["op"] = action, ["name"] = "must-not-start" };
                if (action == "start") arguments["cfg"] = "fixture-never-registered";
                var result = await engine.ExecuteAsync(new("app", arguments, original.Rid));
                Check(!result.Ok && result.Err == "precondition" && result.Op == "app_" + action &&
                    native.Calls == calls && native.Writes == writes && fixture.RowCount() == rows &&
                    fixture.Stored(request.Rid!).RawReceipt == stored.RawReceipt,
                    "app start/status cannot adopt a failed or unresolved registry RID");
            }
            // This isolated engine has no application/terminal manager enabled;
            // neither collision may create a new row, invoke registry or enter
            // the application execution branch instead of returning refusal.
            Check(!Directory.Exists(Path.Combine(fixture.Root, "must-not-start")), "collision created no fixture application artifact");
        }
    }

    internal static async Task InFlightRegistryReplayAndWaitAsync()
    {
        using var fixture = new EngineFixture();
        var native = new MemoryRegistry();
        var request = Request("set", Body("set", generation: NativeGeneration(native)), "running-replay-original");
        await using var engine = fixture.Create(native);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        native.BeforeWrite = () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("fixture registry replay fence timed out");
        };
        var originalTask = engine.ExecuteAsync(request);
        Task<OperationReceipt> waitTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var running = fixture.Stored(request.Rid!);
            var calls = native.Calls;
            var replay = await engine.ExecuteAsync(request).WaitAsync(TimeSpan.FromSeconds(2));
            Check(replay.Ok && replay.State == "running" && replay.Jid == running.Receipt.Jid && replay.Rid == request.Rid &&
                !replay.Data!.ContainsKey("registry") && Hash(replay) == RegistryOperationContract.Parse(request).RequestIdentity &&
                !originalTask.IsCompleted && native.Calls == calls && native.Writes == 0,
                "same-RID in-flight replay returns stored running metadata without joining the live completion");
            waitTask = engine.ExecuteAsync(new("job", new Dictionary<string, string>
                { ["op"] = "wait", ["jid"] = running.Receipt.Jid }, "registry-concurrent-wait"));
            Check(!waitTask.IsCompleted && !originalTask.IsCompleted && native.Writes == 0,
                "explicit job wait observes the still-fenced original completion");
        }
        finally { release.Set(); native.BeforeWrite = null; }
        var original = await originalTask.WaitAsync(TimeSpan.FromSeconds(5));
        var waited = await waitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Check(original.Ok && original.Data!["registry"] is RegistryMutationObservation { WriteInvoked: true, PostconditionObserved: true },
            "only original submitting caller receives the live registry result");
        Check(waited.Ok && waited.Jid == original.Jid && waited.Rid == original.Rid && !waited.Data!.ContainsKey("registry") &&
            waited.Data["registry_write_invoked"] is true && waited.Data["postcondition_observed"] is true &&
            Hash(waited) == Hash(original) && native.Writes == 1,
            "concurrent job wait projects completion to scrubbed original metadata without a second effect");
        await RetiredAsync(engine);
        Check(!fixture.Stored(request.Rid!).Receipt.Data!.ContainsKey("registry") && fixture.RowCount() == 1,
            "wait and replay retain only the original scrubbed SQLite receipt");
    }

    private static string NativeGeneration(MemoryRegistry native) => Binding(native).Operations.InspectKey(Target).Value!.Generation;
    private static RegistryOwnerBinding Binding(MemoryRegistry native) => new(OwnerSid, () => new(OwnerSid, 7), admission => new RegistryOperations(native, admission));
    private static string? Hash(OperationReceipt receipt) => receipt.Data?.GetValueOrDefault("requestIdentity") switch
    { string value => value, JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(), _ => null };
    private static Task RetiredAsync(AgentOpsEngine engine) => UntilAsync(() => engine.ResidentCompletionCount == 0 && engine.ResidentJobCount == 0);
    private static async Task UntilAsync(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, deadline.Token);
    }

    private sealed record StoredRow(string State, OperationReceipt Receipt, OperationRequest? Request, string RawReceipt, string? RawRequest);
    private sealed class FixtureMode : IMaintenanceModeSource
    {
        private readonly object _gate = new();
        private bool _holdAfterRead;
        private bool _closed;
        internal void HoldAfterNextRead() { lock (_gate) _holdAfterRead = true; }
        internal void Open() { lock (_gate) { _holdAfterRead = false; _closed = false; } }
        public MaintenanceModeRecord? Read()
        {
            lock (_gate)
            {
                var closed = _closed;
                if (_holdAfterRead) { _holdAfterRead = false; _closed = true; }
                return closed ? new(1, "fixture-epoch", "active", "fixture-jid", "fixture-rid", "fixture-scope", "fixture-task",
                    OwnerSid, 7, "fixture-service", null, 1) : null;
            }
        }
        public bool IsAcknowledged(MaintenanceEffect effect) => false;
    }
    private sealed class EngineFixture : IDisposable
    {
        private readonly TempRoot _root = new();
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        internal string Root => _root.Path;
        internal string State { get; }
        internal UserHostAdmission Admission { get; } = new();
        internal FixtureMode Mode { get; } = new();
        internal EngineFixture() { State = _root.Dir("registry-engine"); }
        internal AgentOpsEngine Create(MemoryRegistry native, bool bind = true, bool direct = false, bool useAdmission = true)
        {
            var engine = new AgentOpsEngine(State, null, direct, null, false, null, maintenanceMode: Mode,
                userHostAdmission: useAdmission ? Admission : null);
            if (bind) engine.BindRegistryOwner(Binding(native));
            return engine;
        }
        internal long RowCount()
        {
            using var connection = Connection(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM jobs"; return Convert.ToInt64(command.ExecuteScalar());
        }
        internal StoredRow Stored(string rid) => TryStored(rid) ?? throw new InvalidOperationException("missing isolated registry fixture row");
        internal StoredRow? TryStored(string rid)
        {
            using var connection = Connection(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT state,receipt,request FROM jobs WHERE rid=$rid"; command.Parameters.AddWithValue("$rid", rid);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var receipt = reader.GetString(1); var request = reader.IsDBNull(2) ? null : reader.GetString(2);
            return new(reader.GetString(0), JsonSerializer.Deserialize<OperationReceipt>(receipt, Json)!,
                request is null ? null : JsonSerializer.Deserialize<OperationRequest>(request, Json), receipt, request);
        }
        internal void AssertHistoryExcludes(params string[] bodies)
        {
            using var connection = Connection(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT receipt,COALESCE(request,'') FROM jobs";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                foreach (var column in new[] { reader.GetString(0), reader.GetString(1) })
                {
                    if (column.Length == 0) continue;
                    using var document = JsonDocument.Parse(column);
                    foreach (var text in JsonStrings(document.RootElement)) foreach (var body in bodies)
                        Check(!text.Contains(body, StringComparison.Ordinal), "registry private fixture data entered durable SQLite history");
                }
            }
        }
        private static IEnumerable<string> JsonStrings(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String) yield return element.GetString()!;
            else if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var text in JsonStrings(property.Value)) yield return text;
                }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) foreach (var text in JsonStrings(item)) yield return text;
        }
        internal void RestoreCapturedRow(StoredRow row)
        {
            using var connection = Connection(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET state=$state,receipt=$receipt,request=$request WHERE jid=$jid AND rid=$rid";
            command.Parameters.AddWithValue("$state", row.State); command.Parameters.AddWithValue("$receipt", row.RawReceipt);
            command.Parameters.AddWithValue("$request", (object?)row.RawRequest ?? DBNull.Value);
            command.Parameters.AddWithValue("$jid", row.Receipt.Jid); command.Parameters.AddWithValue("$rid", row.Receipt.Rid);
            Check(command.ExecuteNonQuery() == 1, "captured running fixture state was not restored exactly once");
        }
        private SqliteConnection Connection()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(State, "agentops.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open(); return connection;
        }
        public void Dispose() => _root.Dispose();
    }

    private static OperationReceipt Execute(RegistryInvocation invocation, RegistryOwnerBinding? binding) =>
        RegistryReceiptAdapter.Execute(invocation, binding, "fixture-jid", "fixture-rid", CancellationToken.None);
    private static OperationRequest Request(string action, string body, string? rid = "fixture-rid") =>
        new("registry", new Dictionary<string, string> { ["op"] = action }, rid, Body: body);

    private static string Body(string action, string valueName = "example", string? generation = null, string? digest = null,
        string base64 = "AQ==", string type = "binary", int view = 64, string hive = "current_user")
    {
        var body = new Dictionary<string, object?>
        {
            ["version"] = 1, ["key"] = new { hive, view, keyPath = Target.KeyPath }
        };
        if (action == "browse") { body["kind"] = "values"; body["limit"] = 2; }
        if (action is "read" or "set" or "delete_value") body["valueName"] = valueName;
        if (action == "create") body["parentGeneration"] = generation ?? ZeroDigest;
        if (action == "delete_empty_key") body["keyGeneration"] = generation ?? ZeroDigest;
        if (action == "set") body["value"] = new { type, base64 };
        if (action is "set" or "delete_value") body["preimage"] = new
        {
            keyGeneration = generation ?? ZeroDigest, valueDigest = action == "delete_value" ? digest ?? ZeroDigest : digest
        };
        return JsonSerializer.Serialize(body);
    }

    private static void Refused(Action action, string description)
    {
        try { action(); }
        catch (RegistryContractException failure)
        {
            Check(failure.Message == "registry_request_invalid" && failure.Field.Length <= 32, description);
            return;
        }
        throw new InvalidOperationException(description);
    }
    private static void Check(bool value, string description)
    { if (!value) throw new InvalidOperationException(description); }

    // Pure in-memory native fixture. It never instantiates the Windows provider,
    // queries an OS identity, opens SQLite, or creates live registry state.
    private sealed class MemoryRegistry : IRegistryNative
    {
        private long _generation = 1;
        private readonly Dictionary<string, RegistryNativeValue> _values = new(StringComparer.OrdinalIgnoreCase);
        internal int Calls { get; private set; }
        internal int Writes { get; private set; }
        internal Action? AfterWrite { get; set; }
        internal Action? BeforeWrite { get; set; }
        internal void Seed(string name) { _values[name] = new(RegistryValueType.Binary, [1]); _generation++; }
        public RegistryNativeKeyInfo? InspectKey(RegistryKeyIdentity key)
        { Calls++; return key == Target ? new(_generation, 0, _values.Count) : null; }
        public RegistryNativeValue? ReadValue(RegistryValueIdentity identity, int maxBytes)
        { Calls++; return _values.TryGetValue(identity.ValueName, out var value) ? new(value.Type, (byte[])value.Data.Clone()) : null; }
        public RegistryBrowseEntry? ReadName(RegistryKeyIdentity key, RegistryBrowseKind kind, int index, int maxNameChars)
        {
            Calls++;
            if (kind == RegistryBrowseKind.Keys) return null;
            var names = _values.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            return index < names.Length ? new(names[index], (uint)_values[names[index]].Type) : null;
        }
        public bool CreateKey(RegistryKeyIdentity key) => throw new RegistryNativeException(RegistryNativeFailure.Unsupported, "fixture_create_unused");
        public void SetValue(RegistryValueIdentity identity, RegistryValueType type, byte[] data)
        { Calls++; BeforeWrite?.Invoke(); _values[identity.ValueName] = new(type, (byte[])data.Clone()); _generation++; Writes++; AfterWrite?.Invoke(); }
        public void DeleteValue(RegistryValueIdentity identity)
        { Calls++; _values.Remove(identity.ValueName); _generation++; Writes++; AfterWrite?.Invoke(); }
        public void DeleteEmptyKey(RegistryKeyIdentity key) => throw new RegistryNativeException(RegistryNativeFailure.Unsupported, "fixture_delete_key_unused");
    }
}
