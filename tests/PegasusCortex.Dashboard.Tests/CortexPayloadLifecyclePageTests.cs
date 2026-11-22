using System.Text.Json;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Tray;
using Pegasus.Cortex.Mcp.Local.Runtime;

internal static class CortexPayloadLifecyclePageTests
{
    private static int _checks;
    private const string CleanupIssue = "payload_policy_cleanup_required";
    private const string ReviewIssue = "payload_policy_review_required";
    internal static int Run()
    {
        try
        {
            _checks = 0;
            PolicyAndOwner();
            UncertainAndMissing();
            ReviewedPolicySave();
            BusyAndDisposed();
            ClientProtocolAsync().GetAwaiter().GetResult();
            Console.WriteLine($"PASS payload lifecycle page/client: {_checks} injected unshown-control/connection checks; disabled defaults, observed schema/activity, explicit prepare, reviewed cleanup CAS, proof flags, unknown metrics, original reply identity and no replay; no windows shown or native/config/database/network action");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL payload lifecycle page/client: " + error); return 1; }
    }

    private static CortexPayloadLifecycleStatus Status(int schema = 2) => new(CortexPayloadLifecycleClient.StatusSchema,
        CortexPayloadLifecycleSettingsStore.DefaultSettings, true, schema, false, schema == 2 ? "requires_explicit_upgrade" : "disabled");

    private static void PolicyAndOwner()
    {
        var client = new Client(); var confirms = 0; var confirm = false; var themes = 0;
        using var page = new CortexPayloadLifecyclePage(client, _ => themes++, () => { confirms++; return confirm; }, autoLoad: false);
        Check(client.Reads == 0 && client.Saves == 0 && client.Runs == 0 && themes == 1 && page.Parent is null,
            "construction is passive and theme injected");
        Check(Find<Label>(page, "Observed storage owner").Text.Contains("not been checked", StringComparison.Ordinal) &&
            !Find<Button>(page, "Save storage policy").Enabled, "unobserved owner is not ready/default-saved");
        page.RefreshAsync().GetAwaiter().GetResult();
        Check(client.Reads == 1 && !Find<CheckBox>(page, "Enable automatic payload rotation").Checked &&
            Find<NumericUpDown>(page, "Minimum archive age days").Value == 30 &&
            Find<NumericUpDown>(page, "Soft hot storage target MiB").Value == 0 &&
            Find<Button>(page, "Prepare cold storage").Enabled && !Find<Button>(page, "Run storage pass").Enabled,
            "observed disabled defaults and explicit schema2 preparation");
        Find<CheckBox>(page, "Enable automatic payload rotation").Checked = true;
        Find<TextBox>(page, "Payload namespaces").Text = "project.fixture\r\nknowledge.shared";
        Find<NumericUpDown>(page, "Cooperative time budget milliseconds").Value = 1500;
        Find<NumericUpDown>(page, "Metadata work per pass").Value = 4096;
        Check(client.Saves == 0 && Find<Button>(page, "Save storage policy").Enabled, "editing does not persist");
        page.SaveAsync().GetAwaiter().GetResult();
        Check(client.Saves == 1 && client.Expected == 0 && client.LastPolicy is { Enabled: true, WorkMilliseconds: 1500, MaximumMetadataHeads: 4096 } &&
            client.LastPolicy.Namespaces.SequenceEqual(["project.fixture", "knowledge.shared"]), "explicit save sends exact full policy and CAS");
        Check(Find<Label>(page, "Observed storage owner").Text.Contains("preparation is required", StringComparison.Ordinal) &&
            !Find<Button>(page, "Run storage pass").Enabled, "saved enabled policy does not invent prepared schema");
        page.PrepareAsync().GetAwaiter().GetResult();
        Check(confirms == 1 && client.Upgrades == 0, "declined prepare does not call owner");
        confirm = true; page.PrepareAsync().GetAwaiter().GetResult();
        Check(client.Upgrades == 1 && Find<Button>(page, "Run storage pass").Enabled && !Find<Button>(page, "Prepare cold storage").Enabled,
            "explicit confirmed preparation reflects actual schema3");
        client.Current = client.Current with { State = "deferred_budget", Namespace = "project.fixture", HotBytesEvicted = null,
            SelectedRecords = null, TargetSatisfied = null, MetadataObservations = 4096, ImmutableObjectsMayRemain = true,
            LastFinishedUtc = DateTimeOffset.Parse("2026-11-21T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture) };
        page.RunOnceAsync().GetAwaiter().GetResult();
        var last = Find<Label>(page, "Last storage pass").Text;
        Check(client.Runs == 1 && last.Split("unavailable", StringSplitOptions.None).Length - 1 == 3 &&
            last.Contains("immutable staging may remain", StringComparison.Ordinal) && last.Contains("UTC", StringComparison.Ordinal),
            "deferred unknown metrics remain unavailable with local-zone time and immutable staging guidance");
        client.Current = client.Current with { PassRunning = true, State = "running", Saved = client.Current.Saved! with
            { Policy = client.Current.Saved!.Policy with { Enabled = false } } };
        page.RefreshAsync().GetAwaiter().GetResult();
        Check(Find<Label>(page, "Observed storage owner").Text.Contains("still stopping", StringComparison.Ordinal) &&
            !Find<Button>(page, "Run storage pass").Enabled, "disabled configured policy does not claim active pass stopped");
    }

    private static void UncertainAndMissing()
    {
        var client = new Client { Current = Status(3) };
        using var page = new CortexPayloadLifecyclePage(client, _ => { }, () => true, autoLoad: false);
        page.RefreshAsync().GetAwaiter().GetResult();
        Find<CheckBox>(page, "Enable automatic payload rotation").Checked = true;
        Find<TextBox>(page, "Payload namespaces").Text = "project.fixture";
        client.UncertainSave = true;
        page.SaveAsync().GetAwaiter().GetResult();
        page.SaveAsync().GetAwaiter().GetResult();
        Check(client.Saves == 1 && !Find<Button>(page, "Save storage policy").Enabled &&
            Find<Label>(page, "Storage page status").Text.Contains("not confirmed", StringComparison.Ordinal), "unknown save blocks another mutation/no optimistic saved state");
        client.UncertainSave = false; page.RefreshAsync().GetAwaiter().GetResult();
        client.Current = client.Current with { Saved = null, OwnerBound = false, StoreSchemaVersion = null, State = "policy_unavailable", Issue = "payload_policy_invalid" };
        page.RefreshAsync().GetAwaiter().GetResult();
        Check(!Find<CheckBox>(page, "Enable automatic payload rotation").Enabled && !Find<Button>(page, "Save storage policy").Enabled &&
            Find<Label>(page, "Saved storage policy").Text.Contains("unavailable", StringComparison.Ordinal), "missing policy is not replaced with editable defaults");
        client.Current = Status(); page.RefreshAsync().GetAwaiter().GetResult();
        client.UncertainUpgrade = true;
        page.PrepareAsync().GetAwaiter().GetResult(); page.PrepareAsync().GetAwaiter().GetResult();
        Check(client.Upgrades == 1 && !Find<Button>(page, "Prepare cold storage").Enabled,
            "unconfirmed prepare cannot be repeated before explicit refresh");
    }

    private static void ReviewedPolicySave()
    {
        var policy = CortexPayloadLifecycleSettingsStore.DefaultSettings.Policy with { Enabled = true, Namespaces = ["project.fixture"] };
        foreach (var issue in new[] { CleanupIssue, ReviewIssue })
        {
            var client = new Client { ReadState = "refused", Current = Status(3) with
                { Saved = new(7, policy), State = "paused", Issue = issue } };
            using var page = new CortexPayloadLifecyclePage(client, _ => { }, autoLoad: false);
            Check(client.Reads == 0 && client.Saves == 0, "review construction does not read or write");
            page.RefreshAsync().GetAwaiter().GetResult();
            var save = Find<Button>(page, "Save storage policy");
            Check(client.Reads == 1 && client.Saves == 0 && save.Enabled &&
                save.Text == (issue == CleanupIssue ? "Finish cleanup and save policy" : "Review and save policy") &&
                !Find<Button>(page, "Run storage pass").Enabled,
                "explicit refresh admits a verified reviewed policy without silently saving or running");
            page.RunOnceAsync().GetAwaiter().GetResult();
            Check(client.Runs == 0, "reviewed paused policy cannot run through the direct action seam");
            page.SaveAsync().GetAwaiter().GetResult();
            Check(client.Saves == 1 && client.Expected == 7 && client.LastPolicy is { } sent &&
                CortexPayloadLifecycleClient.SamePolicy(sent, policy) && client.Current.Saved?.Revision == 8 &&
                save.Text == "Save policy" && !save.Enabled && Find<Button>(page, "Run storage pass").Enabled,
                "one explicit unchanged reviewed policy save uses actual CAS then clears confirmed pause");
            page.SaveAsync().GetAwaiter().GetResult();
            Check(client.Saves == 1, "confirmed healthy unchanged policy never creates another save");
        }

        var partial = new Client { Current = Status(3) with { Saved = new(3, policy), State = "waiting" }, SaveCleanupIncomplete = true };
        using (var page = new CortexPayloadLifecyclePage(partial, _ => { }, autoLoad: false))
        {
            page.RefreshAsync().GetAwaiter().GetResult();
            page.SaveAsync().GetAwaiter().GetResult();
            Check(partial.Saves == 0, "normal unchanged policy has no redundant direct save");
            Find<NumericUpDown>(page, "Minimum archive age days").Value = 31;
            page.SaveAsync().GetAwaiter().GetResult();
            page.SaveAsync().GetAwaiter().GetResult();
            Check(partial.Saves == 1 && !Find<Button>(page, "Save storage policy").Enabled &&
                Find<Label>(page, "Storage page status").Text.Contains("follow-up cleanup", StringComparison.Ordinal) &&
                !Find<Button>(page, "Run storage pass").Enabled,
                "confirmed partial cleanup requires refresh and never automatically resends");
            page.RefreshAsync().GetAwaiter().GetResult();
            Check(partial.Saves == 1 && Find<Button>(page, "Save storage policy").Enabled,
                "only the refreshed known cleanup snapshot enables a fresh explicit save");
            partial.SaveCleanupIncomplete = false;
            page.SaveAsync().GetAwaiter().GetResult();
            Check(partial.Saves == 2 && partial.Expected == 4 && partial.Current.Saved?.Revision == 5,
                "cleanup follow-up is a new CAS save at the observed next revision");
        }

        var recovery = new Client { Current = Status(3) with
            { Saved = null, State = "paused", Issue = "payload_policy_recovery_required" } };
        using (var page = new CortexPayloadLifecyclePage(recovery, _ => { }, autoLoad: false))
        {
            page.RefreshAsync().GetAwaiter().GetResult();
            page.SaveAsync().GetAwaiter().GetResult(); page.RunOnceAsync().GetAwaiter().GetResult();
            Check(recovery.Saves == 0 && recovery.Runs == 0 && !Find<Button>(page, "Save storage policy").Enabled &&
                !Find<CheckBox>(page, "Enable automatic payload rotation").Enabled && !Find<Button>(page, "Run storage pass").Enabled,
                "ambiguous recovery offers no fabricated policy or save/run choice");
        }
        var paused = new Client { Current = Status(3) with { Saved = new(2, policy), State = "paused", Issue = "owner_paused" } };
        using (var page = new CortexPayloadLifecyclePage(paused, _ => { }, autoLoad: false))
        {
            page.RefreshAsync().GetAwaiter().GetResult(); page.RunOnceAsync().GetAwaiter().GetResult();
            Check(paused.Runs == 0 && !Find<Button>(page, "Run storage pass").Enabled,
                "paused owner cannot run even outside recognized policy review issues");
        }
        foreach (var issue in new[] { CleanupIssue, ReviewIssue })
        {
            var unverified = new Connection { Change = receipt => PublicationReply(receipt, issue, null, false, false,
                partial: false, status: Status(3) with { Saved = new(2, policy) }, refused: false) };
            var client = new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(unverified));
            using var page = new CortexPayloadLifecyclePage(client, _ => { }, autoLoad: false);
            page.RefreshAsync().GetAwaiter().GetResult();
            page.SaveAsync().GetAwaiter().GetResult(); page.RunOnceAsync().GetAwaiter().GetResult();
            Check(unverified.Actions.SequenceEqual(["lifecycle_status"]) && unverified.Sends == 1 &&
                !Find<Button>(page, "Save storage policy").Enabled && !Find<Button>(page, "Run storage pass").Enabled,
                "a retained cleanup/review snapshot without proof remains unavailable in the actual client/page flow");
        }
    }

    private static void BusyAndDisposed()
    {
        var client = new Client { PendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var page = new CortexPayloadLifecyclePage(client, _ => { }, autoLoad: false);
        var context = SynchronizationContext.Current;
        // Controls may install a WinForms context during construction. This
        // fixture has no message loop; clear it only after that construction.
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var pending = page.RefreshAsync();
            page.RefreshAsync().GetAwaiter().GetResult(); page.SaveAsync().GetAwaiter().GetResult();
            Check(!pending.IsCompleted && client.Reads == 1 && client.Saves == 0 && !Find<Button>(page, "Refresh storage status").Enabled,
                "busy request cannot overlap another UI action");
            page.Dispose(); page.Dispose();
            Check(client.LastToken.IsCancellationRequested && !pending.IsCompleted, "disposed page cancels observation but retains original client task");
            client.PendingRead.SetResult(new("done", Status: client.Current));
            pending.GetAwaiter().GetResult();
            Check(page.IsDisposed && client.Reads == 1 && client.Saves == 0, "late result after disposal cannot reload or replay");
        }
        finally
        {
            client.PendingRead.TrySetResult(new("done", Status: client.Current)); page.Dispose();
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private static async Task ClientProtocolAsync()
    {
        var connection = new Connection();
        var connects = 0;
        var client = new CortexPayloadLifecycleClient(_ => { connects++; return Task.FromResult<ICortexHostExitConnection?>(connection); });
        var result = await client.ReadAsync(default);
        Check(result.State == "done" && result.Status?.Schema == CortexPayloadLifecycleClient.StatusSchema &&
            connection.Sends == 1 && connection.Disposals == 1 && connection.Actions.SequenceEqual(["lifecycle_status"]), "one exact attached owner status call");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); var before = connection.Sends;
            Check((await client.ReadAsync(cancel.Token)).State == "invalid" && connection.Sends == before && connects == 1, "canceled-before-submit does not connect/send");
        }
        foreach (var change in new Func<OperationReceipt, OperationReceipt>[]
        {
            receipt => receipt with { Rid = Guid.NewGuid().ToString("N") }, receipt => receipt with { Op = "wrong" },
            receipt => receipt with { Jid = "" }, receipt => receipt with { State = "running" },
            receipt => receipt with { Data = new Dictionary<string, object?>() },
            receipt => receipt with { Data = new Dictionary<string, object?> { ["lifecycle"] = new
                { schema = CortexPayloadLifecycleClient.StatusSchema, saved = Status().Saved, ownerBound = true, storeSchemaVersion = 3, state = "idle" } } }
        })
        {
            var bad = new Connection { Change = change };
            var invalid = await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(bad)).ReadAsync(default);
            Check(invalid.State == "unknown" && bad.Sends == 1 && bad.Disposals == 1, "unverified status reply refuses without replay");
        }
        var lost = new Connection { FailSend = true };
        Check((await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(lost)).RunOnceAsync(default)).State == "unknown" &&
            lost.Sends == 1 && lost.Disposals == 1, "lost original mutation acknowledgement never resends");
        var partial = new Connection { Change = receipt => receipt with { Ok = false, State = "recovery_required", Partial = true, Err = "recovery_required" } };
        Check((await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(partial)).UpgradeAsync(default)).State == "unknown" &&
            partial.Actions.SequenceEqual(["upgrade"]), "partial upgrade never sends another upgrade or status");
        var prepare = new Connection();
        var prepared = await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(prepare)).UpgradeAsync(default);
        Check(prepared.State == "done" && prepared.Status is { OwnerBound: true, StoreSchemaVersion: 3 } &&
            prepare.Actions.SequenceEqual(["upgrade", "lifecycle_status"]) && prepare.Disposals == 2,
            "verified preparation gets exactly one subsequent status read");
        foreach (var observed in new[]
        {
            Status(2), Status(3) with { StoreSchemaVersion = null }, Status(3) with { OwnerBound = false }
        })
        {
            var unverified = new Connection
            {
                Change = receipt => receipt.Data?.ContainsKey("lifecycle") == true
                    ? receipt with { Data = new Dictionary<string, object?> { ["lifecycle"] = observed } } : receipt
            };
            var preparation = await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(unverified)).UpgradeAsync(default);
            Check(preparation.State == "prepared_status_unavailable" && preparation.Status is { } retainedStatus &&
                retainedStatus.StoreSchemaVersion == observed.StoreSchemaVersion && retainedStatus.OwnerBound == observed.OwnerBound &&
                unverified.Sends == 2 && unverified.Disposals == 2 && unverified.Actions.SequenceEqual(["upgrade", "lifecycle_status"]),
                "successful header with schema2/null/unbound status is retained but cannot confirm preparation or resend upgrade");
        }
        var policy = CortexPayloadLifecycleSettingsStore.DefaultSettings.Policy with { Enabled = true, Namespaces = ["project.fixture"] };
        var saved = new Connection();
        var saveResult = await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(saved)).SaveAsync(policy, 0, default);
        Check(saveResult.State == "done" && saveResult.Status?.Saved?.Revision == 1 && saved.Actions.SequenceEqual(["lifecycle_save"]), "save confirms exact requested policy revision");
        var mismatched = new Connection { Change = receipt => receipt with { Data = new Dictionary<string, object?> { ["lifecycle"] = Status() } } };
        Check((await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(mismatched)).SaveAsync(policy, 0, default)).State == "unknown" &&
            mismatched.Sends == 1, "save cannot accept old or different policy snapshot");
        var cleanup = new Connection { FailDispose = true };
        var retained = await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(cleanup)).SaveAsync(policy, 0, default);
        Check(retained.State == "done_cleanup_incomplete" && retained.Status?.Saved?.Revision == 1 && cleanup.Sends == 1,
            "confirmed save remains retained when only local connection cleanup fails");
        await PublicationProtocolAsync(policy);
    }

    private static async Task PublicationProtocolAsync(CortexPayloadLifecyclePolicy policy)
    {
        var confirmed = new Connection { Change = receipt => PublicationReply(receipt, CleanupIssue, true, true, false, partial: true) };
        var saved = await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(confirmed)).SaveAsync(policy, 0, default);
        Check(saved.State == "done_cleanup_incomplete" && saved.Error == CleanupIssue &&
            saved.Status is { Saved.Revision: 1, Issue: CleanupIssue } && confirmed.Sends == 1 && confirmed.Disposals == 1 &&
            confirmed.Actions.SequenceEqual(["lifecycle_save"]),
            "partial cleanup confirms a new save only with explicit applied/selected/known-outcome evidence");
        var wire = new Connection { Change = receipt =>
        {
            var response = PublicationReply(receipt, CleanupIssue, true, true, false, partial: true);
            return response with { Data = response.Data!.ToDictionary(pair => pair.Key,
                pair => (object?)JsonSerializer.SerializeToElement(pair.Value), StringComparer.Ordinal) };
        } };
        Check((await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(wire)).SaveAsync(policy, 0, default)).State == "done_cleanup_incomplete" &&
            wire.Sends == 1, "deserialized JSON boolean proof values follow the same strict known-save rule");

        foreach (var flags in new (bool? Applied, bool? Verified, bool? Unknown)[]
        {
            (false, true, false), (null, true, false), (true, false, false), (true, null, false),
            (true, true, true), (true, true, null)
        })
        {
            var uncertain = new Connection { Change = receipt => PublicationReply(receipt, CleanupIssue,
                flags.Applied, flags.Verified, flags.Unknown, partial: true) };
            var result = await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(uncertain)).SaveAsync(policy, 0, default);
            Check(result.State == "unknown" && uncertain.Sends == 1 && uncertain.Disposals == 1 &&
                uncertain.Actions.SequenceEqual(["lifecycle_save"]),
                "missing or false cleanup save proof cannot confirm or replay the save");
        }
        var unapplied = new Connection { Change = receipt => PublicationReply(receipt, CleanupIssue, false, true, false, partial: false) };
        Check((await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(unapplied)).SaveAsync(policy, 0, default)).State == "refused" &&
            unapplied.Sends == 1, "known non-applied save refusal is never upgraded to successful save");
        foreach (var actual in new[]
        {
            new CortexPayloadLifecycleSettings(2, policy),
            new CortexPayloadLifecycleSettings(1, policy with { MinimumArchiveAgeDays = 31 })
        })
        {
            var mismatch = new Connection { Change = receipt => PublicationReply(receipt, CleanupIssue, true, true, false, partial: true,
                status: Status(3) with { Saved = actual }) };
            var result = await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(mismatch)).SaveAsync(policy, 0, default);
            Check(result.State == "unknown" && result.Error == "saved_policy_unverified" && mismatch.Sends == 1 && mismatch.Disposals == 1,
                "server cleanup evidence cannot substitute for exact new revision and requested policy equality");
        }

        foreach (var issue in new[] { CleanupIssue, ReviewIssue })
        {
            var actual = Status(3) with { Saved = new(7, policy) };
            foreach (var refused in new[] { false, true })
            {
                var known = new Connection { Change = receipt => PublicationReply(receipt, issue, null, true, false,
                    partial: false, status: actual, refused: refused) };
                var result = await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(known)).ReadAsync(default);
                Check(result.State == (refused ? "refused" : "done") && result.Status?.Saved?.Revision == 7 && known.Sends == 1,
                    "review status with verified selected snapshot and explicit known outcome remains available for fresh review");
            }
            foreach (var flags in new (bool? Verified, bool? Unknown)[]
            {
                (null, false), (false, false), (true, null), (true, true)
            })
            {
                var uncertain = new Connection { Change = receipt => PublicationReply(receipt, issue, null,
                    flags.Verified, flags.Unknown, partial: false, status: actual, refused: false) };
                var result = await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(uncertain)).ReadAsync(default);
                Check(result.State == "unknown" && result.Error == "selected_policy_snapshot_unverified" &&
                    result.Status?.Saved?.Revision == 7 && uncertain.Sends == 1,
                    "cleanup/review status without both explicit proof flags cannot make the retained snapshot actionable");
            }
            var empty = new Connection { Change = receipt => PublicationReply(receipt, issue, null, true, false,
                partial: false, status: actual with { Saved = null }, refused: false) };
            Check((await new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(empty)).ReadAsync(default)).State == "unknown" &&
                empty.Sends == 1, "review proof without an actual selected policy cannot fabricate one");
        }
        var original = new Connection { FailSend = true };
        var recoveryClient = new CortexPayloadLifecycleClient(_ => Task.FromResult<ICortexHostExitConnection?>(original));
        var unknownSave = await recoveryClient.SaveAsync(policy, 0, default);
        var unknownId = unknownSave.RequestId;
        original.FailSend = false;
        original.Change = receipt => PublicationReply(receipt, ReviewIssue, null, true, false, partial: false,
            status: Status(3) with { Saved = new(7, policy) }, refused: false);
        var reviewed = await recoveryClient.ReadAsync(default);
        original.Change = receipt => receipt with { Data = new Dictionary<string, object?> { ["lifecycle"] = Status(3) with
            { Saved = new(8, policy), State = "waiting" } } };
        var fresh = await recoveryClient.SaveAsync(policy, reviewed.Status!.Saved!.Revision, default);
        Check(unknownSave.State == "unknown" && unknownSave.RequestId == unknownId && unknownId is not null &&
            reviewed.State == "done" && fresh.State == "done" && original.RequestIds.Distinct(StringComparer.Ordinal).Count() == 3 &&
            original.Actions.SequenceEqual(["lifecycle_save", "lifecycle_status", "lifecycle_save"]) && original.Sends == 3 &&
            original.SaveRevisions.SequenceEqual([0L, 7L]),
            "explicit review and new CAS save retain the original unknown RID and use separate new requests without replay");
    }

    private static OperationReceipt PublicationReply(OperationReceipt receipt, string issue, bool? applied, bool? verified,
        bool? unknown, bool partial, CortexPayloadLifecycleStatus? status = null, bool refused = true)
    {
        var data = receipt.Data!.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        status ??= JsonSerializer.SerializeToElement(data["lifecycle"]).Deserialize<CortexPayloadLifecycleStatus>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        data["lifecycle"] = status with { State = "paused", Issue = issue };
        if (applied is { } wasApplied) data["policy_save_applied"] = wasApplied;
        if (verified is { } wasVerified) data["selected_policy_verified"] = wasVerified;
        if (unknown is { } isUnknown) data["outcome_unknown"] = isUnknown;
        return receipt with { Ok = !refused, State = refused ? "failed" : "done", Err = refused ? issue : null,
            Partial = partial, Data = data };
    }

    private sealed class Client : ICortexPayloadLifecycleClient
    {
        internal CortexPayloadLifecycleStatus Current = Status();
        internal int Reads, Saves, Runs, Upgrades;
        internal long Expected;
        internal CortexPayloadLifecyclePolicy? LastPolicy;
        internal bool UncertainSave, UncertainUpgrade, SaveCleanupIncomplete;
        internal string ReadState = "done";
        internal TaskCompletionSource<CortexPayloadLifecycleClientResult>? PendingRead;
        internal CancellationToken LastToken;
        public Task<CortexPayloadLifecycleClientResult> ReadAsync(CancellationToken token)
        { Reads++; LastToken = token; return PendingRead?.Task ?? Task.FromResult(new CortexPayloadLifecycleClientResult(ReadState, Status: Current)); }
        public Task<CortexPayloadLifecycleClientResult> SaveAsync(CortexPayloadLifecyclePolicy policy, long expectedRevision, CancellationToken token)
        {
            Saves++; Expected = expectedRevision; LastPolicy = policy;
            if (UncertainSave) return Task.FromResult(new CortexPayloadLifecycleClientResult("unknown", "reply_unavailable"));
            var reviewed = Current.Issue is CleanupIssue or ReviewIssue;
            Current = Current with { Saved = new(expectedRevision + 1, policy) };
            if (SaveCleanupIncomplete)
            {
                Current = Current with { State = "paused", Issue = CleanupIssue };
                return Task.FromResult(new CortexPayloadLifecycleClientResult("done_cleanup_incomplete", CleanupIssue, Current));
            }
            if (reviewed) Current = Current with { State = policy.Enabled ? "waiting" : "disabled", Issue = null };
            return Task.FromResult(new CortexPayloadLifecycleClientResult("done", Status: Current));
        }
        public Task<CortexPayloadLifecycleClientResult> RunOnceAsync(CancellationToken token)
        { Runs++; return Task.FromResult(new CortexPayloadLifecycleClientResult("done", Status: Current)); }
        public Task<CortexPayloadLifecycleClientResult> UpgradeAsync(CancellationToken token)
        {
            Upgrades++;
            if (UncertainUpgrade) return Task.FromResult(new CortexPayloadLifecycleClientResult("unknown", "reply_unavailable"));
            Current = Current with { StoreSchemaVersion = 3, State = "idle" };
            return Task.FromResult(new CortexPayloadLifecycleClientResult("done", Status: Current));
        }
    }
    private sealed class Connection : ICortexHostExitConnection
    {
        internal int Sends, Disposals;
        internal readonly List<string> Actions = [];
        internal readonly List<string> RequestIds = [];
        internal readonly List<long> SaveRevisions = [];
        internal Func<OperationReceipt, OperationReceipt>? Change;
        internal bool FailSend, FailDispose;
        public uint ProcessId => 42;
        public long CreationFileTime => 123;
        public Task<OperationReceipt> SendAsync(OperationRequest request, CancellationToken token)
        {
            Sends++;
            Check(request.Op == CortexPayloadTieringControl.Operation && request.Arg.Count == 0 &&
                request.Root is null && request.Cwd is null && request.Ws is null && request.PathRoots is null &&
                Guid.TryParseExact(request.Rid, "N", out _), "client uses only fixed owner context and exact RID");
            using var json = JsonDocument.Parse(request.Body!);
            var action = json.RootElement.GetProperty("action").GetString()!; Actions.Add(action); RequestIds.Add(request.Rid!);
            if (action == "lifecycle_save") SaveRevisions.Add(json.RootElement.GetProperty("expectedPolicyRevision").GetInt64());
            if (FailSend) throw new IOException("synthetic lost reply");
            var status = Status(3);
            if (action == "lifecycle_save") status = status with { Saved = new(1,
                JsonSerializer.Deserialize<CortexPayloadLifecyclePolicy>(json.RootElement.GetProperty("policy"), new JsonSerializerOptions(JsonSerializerDefaults.Web))!) };
            var receipt = new OperationReceipt(true, Guid.NewGuid().ToString("N"), request.Rid!, "done", request.Op,
                Data: action == "upgrade" ? null : new Dictionary<string, object?> { ["lifecycle"] = status });
            return Task.FromResult(Change?.Invoke(receipt) ?? receipt);
        }
        public Task<bool> WaitForStopAsync(TimeSpan timeout, CancellationToken token) => throw new InvalidOperationException("Lifecycle client must not stop an owner.");
        public ValueTask DisposeAsync()
        { Disposals++; return FailDispose ? ValueTask.FromException(new IOException("synthetic cleanup failed")) : ValueTask.CompletedTask; }
    }
    private static T Find<T>(Control control, string name) where T : Control
    {
        foreach (Control child in control.Controls)
        {
            if (child is T value && child.AccessibleName == name) return value;
            var nested = FindOrNull<T>(child, name); if (nested is not null) return nested;
        }
        throw new InvalidOperationException("Missing control " + name);
    }
    private static T? FindOrNull<T>(Control control, string name) where T : Control
    {
        if (control is T value && control.AccessibleName == name) return value;
        foreach (Control child in control.Controls) { var found = FindOrNull<T>(child, name); if (found is not null) return found; }
        return null;
    }
    private static void Check(bool value, string name) { _checks++; if (!value) throw new InvalidOperationException("Lifecycle page fixture: " + name); }
}
