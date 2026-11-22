using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Tray;

// Unshown controls, synthetic password input and injected dispatch only. No
// credential store, profile filesystem, network mapping or Windows provider.
internal static class CortexConnectionProfilesPageTests
{
    private static readonly SmbConnectionProfile Profile = new(1, "fixture-share", "Fixture share", @"\\fixture-server\share", "Z:", "currentUser");
    private static readonly SmbConnectionProfileSnapshot Snapshot = new(Profile, 1, new string('a', 64));
    private static int _assertions;

    internal static Task RunAsync()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("profile_fixture_requires_sta");
        var previous = SynchronizationContext.Current;
        var autoInstall = WindowsFormsSynchronizationContext.AutoInstall;
        using (var context = new StaFixtureContext())
        {
            try
            {
                // The synchronous STA test entry has no Windows message loop.
                // Pump only this fixture's async continuations on that same STA;
                // native controls must not replace the fixture context midway.
                WindowsFormsSynchronizationContext.AutoInstall = false;
                SynchronizationContext.SetSynchronizationContext(context);
                var elapsed = Stopwatch.StartNew();
                context.Complete(RunCoreAsync(), elapsed, TimeSpan.FromSeconds(20));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                WindowsFormsSynchronizationContext.AutoInstall = autoInstall;
            }
        }
        Console.WriteLine($"PASS connection profile UI: {_assertions} unshown/injected checks; closed request/RID, two fixed password purposes sharing native capture/clear, nonsecret profile edits, pinned confirmation, uncertainty and callback disposal; no credential/store/network provider");
        return Task.CompletedTask;
    }

    private static async Task RunCoreAsync()
    {
        _assertions = 0;
        await AdapterEnvelopeAsync();
        SharedSmbPasswordEntry();
        await ProfileEditsAsync();
        await PinnedMappingAndUncertaintyAsync();
        await ResponseAndTombstoneAsync();
        await SelectionAndDisposalAsync();
    }

    private static async Task AdapterEnvelopeAsync()
    {
        foreach (var action in ConnectionProfileOperationContract.Actions)
        {
            var capture = new Capture();
            var adapter = new CortexConnectionProfileController(() => capture);
            var rid = "fixture-" + action;
            await adapter.ExecuteAsync(action, Body(action), rid, CancellationToken.None);
            Check(capture.Last is { Op: "connection_profile", Root: null, Ws: null, Cwd: null, PathRoots: null } request &&
                request.Arg.Count == 1 && request.Arg["op"] == action && request.Rid == rid && request.Body == Body(action) && capture.Disposals == 1,
                "default adapter changed closed envelope/RID or failed dispatcher disposal");
        }
        var starts = 0;
        var invalid = new CortexConnectionProfileController(() => { starts++; return new Capture(); });
        await Refused(() => invalid.ExecuteAsync("save", Body("save"), null, CancellationToken.None));
        await Refused(() => invalid.ExecuteAsync("list", "{\"version\":1,\"kind\":\"smb-drive\",\"password\":\"synthetic\"}", "fixture", CancellationToken.None));
        await Refused(() => invalid.ExecuteAsync("execute", Body("list"), "fixture", CancellationToken.None));
        Check(starts == 0, "invalid metadata/password/unknown action constructed a transport");
    }

    private static void SharedSmbPasswordEntry()
    {
        var active = 0; var controller = new Password(() => active);
        IDisposable Enter() { active++; return new Lease(() => active--); }
        using (var page = new CortexCertificatePasswordsPage(Enter, controller, () => true, _ => { }, OwnerPasswordPurpose.SmbPassword))
        {
            var reference = Find<TextBox>(page, "SMB password reference");
            var password = Find<TextBox>(page, "SMB password value");
            Check(controller.Calls == 0 && password.UseSystemPasswordChar && password.MaxLength == 1024, "SMB purpose opened store or lost shared masked field");
            reference.Text = "fixture-smb-password";
            password.Text = "synthetic-p\u00e4\U0001f600"; // Synthetic fixture only.
            page.SavePassword();
            Check(controller.Saves == 1 && active == 0 && password.TextLength == 0 && controller.Reference == "fixture-smb-password",
                "SMB purpose did not use shared exact mutable capture/owner/clear path");
        }
        using (var standalone = new CortexCertificatePasswordsPage(controller: controller, theme: _ => { }, purpose: OwnerPasswordPurpose.SmbPassword))
        {
            Find<TextBox>(standalone, "SMB password reference").Text = "fixture-smb-password";
            standalone.CheckReference(); standalone.SavePassword(); standalone.RemovePassword();
            Check(controller.Calls == 1 && !Find<Button>(standalone, "Check password reference").Enabled, "standalone SMB purpose gained secret access");
        }
        try { using var wrong = new CortexCertificatePasswordsPage(controller: controller, theme: _ => { }, purpose: (OwnerPasswordPurpose)999); }
        catch (ArgumentOutOfRangeException) { _assertions++; return; }
        throw new InvalidOperationException("arbitrary password purpose accepted");
    }

    private static async Task ProfileEditsAsync()
    {
        var fake = new Fake(); var confirm = false;
        using var page = new CortexConnectionProfilesPage(fake, _ => confirm, _ => { });
        Check(fake.Calls.Count == 0, "profile construction dispatched a probe");
        await page.LoadAsync();
        Check(fake.Calls.Count == 1 && Find<TextBox>(page, "Profile ID").Text == Profile.Id &&
            !Find<TextBox>(page, "Profile ID").Enabled && !Find<TextBox>(page, "Remote SMB user").Enabled,
            "explicit page load lost selected revision or current-user field policy");
        Find<TextBox>(page, "Profile label").Text = "Edited fixture";
        await page.SaveAsync();
        using (var saved = JsonDocument.Parse(fake.Calls[^1].Body))
        {
            var body = saved.RootElement;
            Check(body.GetProperty("expectedRevision").GetInt64() == 1 && body.GetProperty("profile").GetProperty("label").GetString() == "Edited fixture" &&
                !body.GetProperty("profile").TryGetProperty("passwordReference", out _) && !body.GetProperty("profile").TryGetProperty("remoteUser", out _),
                "current-user save invented credentials or lost expected revision");
        }
        Find<ComboBox>(page, "Connection identity").SelectedIndex = 1;
        Find<TextBox>(page, "Remote SMB user").Text = @"FIXTURE\remote-user";
        Find<TextBox>(page, "SMB password reference").Text = "fixture-smb-password";
        await page.SaveAsync();
        using (var saved = JsonDocument.Parse(fake.Calls[^1].Body))
        {
            var profile = saved.RootElement.GetProperty("profile");
            Check(profile.GetProperty("identityMode").GetString() == "explicitRemote" &&
                profile.GetProperty("remoteUser").GetString() == @"FIXTURE\remote-user" &&
                profile.GetProperty("passwordReference").GetString() == "fixture-smb-password" && !profile.TryGetProperty("password", out _),
                "explicit remote profile did not remain nonsecret reference-only");
        }
        var before = fake.Calls.Count; await page.RemoveAsync();
        Check(fake.Calls.Count == before, "canceled removal dispatched");
        confirm = true; await page.RemoveAsync();
        using (var removed = JsonDocument.Parse(fake.Calls[^1].Body))
            Check(fake.Calls[^1].Action == "remove" && removed.RootElement.GetProperty("expectedRevision").GetInt64() == 3 &&
                !Find<Button>(page, "Remove profile").Enabled && Status(page).Contains("not changed", StringComparison.Ordinal),
                "confirmed removal was not exact revision or implied mapping/password removal");
        Check(fake.Calls.Select(call => call.Rid).Distinct().Count() == fake.Calls.Count && fake.Calls.All(call => call.Rid is { Length: 32 }),
            "distinct UI actions reused or lost request IDs");
    }

    private static async Task PinnedMappingAndUncertaintyAsync()
    {
        var fake = new Fake();
        using var page = new CortexConnectionProfilesPage(fake, _ => true, _ => { });
        await page.LoadAsync(); await page.MapAsync("disconnect");
        using (var request = JsonDocument.Parse(fake.Calls[^1].Body))
            Check(request.RootElement.GetProperty("revision").GetInt64() == 1 &&
                request.RootElement.GetProperty("contentHash").GetString() == Snapshot.ContentHash &&
                request.RootElement.GetProperty("expectedRemoteShare").GetString() == Profile.RemoteShare,
                "disconnect lost saved profile/hash/exact expected share");
        fake.Handler = call => Task.FromResult(Reply(call, false, "attention", new Dictionary<string, object?>
            { ["outcome_unknown"] = true, ["action_issued"] = true }, partial: true));
        await page.MapAsync("connect"); var issued = fake.Calls.Count;
        await page.MapAsync("connect"); await page.SaveAsync(); await page.RemoveAsync();
        Check(fake.Calls.Count == issued && !Find<Button>(page, "Save profile").Enabled && !Find<Button>(page, "Connect drive").Enabled,
            "unconfirmed mapping permitted mutation/retry");
        fake.Handler = null; await page.InspectAsync();
        Check(Find<Button>(page, "Connect drive").Enabled && Status(page).Contains("earlier action remains unconfirmed", StringComparison.Ordinal),
            "explicit mapping observation certified past action or did not allow a deliberate new action");
        fake.Handler = call => Task.FromResult(Reply(call, false, "attention", new Dictionary<string, object?> { ["outcome_unknown"] = true }, partial: true));
        await page.SaveAsync(); issued = fake.Calls.Count;
        await page.SaveAsync(); Check(fake.Calls.Count == issued, "unconfirmed save retried");
        fake.Handler = null; await page.RefreshAsync();
        Check(Find<Button>(page, "Save profile").Enabled && Status(page).Contains("earlier change remains unconfirmed", StringComparison.Ordinal),
            "explicit profile read falsely certified prior save");
    }

    private static async Task SelectionAndDisposalAsync()
    {
        var second = new SmbConnectionProfileSnapshot(Profile with { Id = "second-share", Label = "Second fixture", LocalDrive = "Y:" }, 2, new string('b', 64));
        var fake = new Fake { Profiles = [Snapshot, second] };
        using (var page = new CortexConnectionProfilesPage(fake, _ => true, _ => { }))
        {
            await page.LoadAsync();
            var held = new TaskCompletionSource<OperationReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.Handler = _ => held.Task;
            var pending = page.RefreshAsync(); var call = fake.Calls[^1];
            Find<ListBox>(page, "Saved connection profiles").SelectedIndex = 1;
            held.SetResult(Reply(call, true, "done", new Dictionary<string, object?> { ["snapshot"] = Snapshot }));
            await pending;
            Check(Find<TextBox>(page, "Profile ID").Text == second.Profile.Id, "superseded read overwrote selected profile");
        }
        fake = new Fake { Profiles = [Snapshot, second] };
        CortexConnectionProfilesPage? retargeted = null;
        using (retargeted = new CortexConnectionProfilesPage(fake, _ =>
        {
            Find<ListBox>(retargeted!, "Saved connection profiles").SelectedIndex = 1;
            return true;
        }, _ => { }))
        {
            await retargeted.LoadAsync(); var before = fake.Calls.Count;
            await retargeted.MapAsync("connect");
            Check(fake.Calls.Count == before, "confirmation for one snapshot connected another selection");
        }
        fake = new Fake();
        var disposed = new CortexConnectionProfilesPage(fake, _ => true, _ => { });
        var read = new TaskCompletionSource<OperationReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Handler = _ => read.Task;
        var observed = disposed.LoadAsync(); var readCall = fake.Calls[^1];
        disposed.Dispose(); disposed.Dispose();
        Check(readCall.Token.IsCancellationRequested && !observed.IsCompleted, "dispose did not cancel reads or abandoned active callback");
        read.SetResult(Reply(readCall, true, "done", new Dictionary<string, object?> { ["profiles"] = new[] { Snapshot } }));
        await observed;
        Check(disposed.IsDisposed && fake.Calls.Count == 1, "disposed read updated/restarted work");
        fake = new Fake();
        disposed = new CortexConnectionProfilesPage(fake, _ => true, _ => { });
        await disposed.LoadAsync();
        var mutation = new TaskCompletionSource<OperationReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Handler = _ => mutation.Task;
        var saved = disposed.SaveAsync(); var saveCall = fake.Calls[^1];
        disposed.Dispose();
        Check(!saveCall.Token.CanBeCanceled && !saved.IsCompleted, "page close canceled or abandoned delivered mutation");
        mutation.SetResult(Reply(saveCall, true, "done", new Dictionary<string, object?> { ["snapshot"] = Snapshot with { Revision = 2 } }));
        await saved;
        Check(fake.Calls.Count == 2, "disposal retried or compensated a mutation");
    }

    private static async Task ResponseAndTombstoneAsync()
    {
        var fake = new Fake();
        using (var page = new CortexConnectionProfilesPage(fake, _ => true, _ => { }))
        {
            await page.LoadAsync();
            fake.Handler = call => Task.FromResult(Reply(call, true, "done", new Dictionary<string, object?>
                { ["snapshot"] = Snapshot with { Profile = Profile with { Label = "must-not-apply" } } }) with { Rid = "different-rid" });
            await page.RefreshAsync();
            Check(Find<TextBox>(page, "Profile label").Text == Profile.Label, "mismatched RID applied profile data");
            fake.Handler = call => Task.FromResult(Reply(call, true, "done", new Dictionary<string, object?>
                { ["snapshot"] = Snapshot with { Profile = Profile with { Id = "wrong-id" } } }));
            await page.RefreshAsync();
            Check(Find<TextBox>(page, "Profile ID").Text == Profile.Id, "mismatched snapshot ID applied");
            fake.Handler = call => Task.FromResult(Reply(call, true, "done", new Dictionary<string, object?>
                { ["snapshot"] = Snapshot }, partial: true));
            await page.RefreshAsync();
            Check(Status(page).Contains("not confirmed", StringComparison.Ordinal), "partial observation became a confirmed refresh");
        }
        fake = new Fake();
        using (var page = new CortexConnectionProfilesPage(fake, _ => true, _ => { }))
        {
            fake.Handler = call => Task.FromResult(Reply(call, true, "done", new Dictionary<string, object?>
                { ["profiles"] = Array.Empty<SmbConnectionProfileSnapshot>(), ["nextId"] = "tombstone-position" }));
            await page.LoadAsync();
            Check(Find<Button>(page, "Next page").Enabled, "empty tombstone page lost continuation");
            fake.Handler = call => Task.FromResult(Reply(call, true, "done", new Dictionary<string, object?> { ["profiles"] = Array.Empty<SmbConnectionProfileSnapshot>() }));
            await page.LoadAsync(next: true);
            using (var request = JsonDocument.Parse(fake.Calls[^1].Body))
                Check(request.RootElement.GetProperty("afterId").GetString() == "tombstone-position", "tombstone continuation not preserved");
            Find<TextBox>(page, "Profile ID").Text = Profile.Id;
            fake.Handler = call => Task.FromResult(Reply(call, true, "done", new Dictionary<string, object?> { ["state"] = "removed", ["revision"] = 4L }));
            await page.RefreshAsync();
            Check(Find<Label>(page, "Profile revision").Text.Contains("4", StringComparison.Ordinal) && !Find<TextBox>(page, "Profile ID").Enabled &&
                !Find<Button>(page, "Remove profile").Enabled, "removed profile revision was reset or became removable again");
        }
    }

    private static string Body(string action)
    {
        var value = new JsonObject { ["version"] = 1, ["kind"] = "smb-drive" };
        if (action == "list") value["limit"] = 1;
        else if (action == "save") { value["profile"] = JsonSerializer.SerializeToNode(Profile, SmbConnectionProfile.Json); value["expectedRevision"] = 0; }
        else
        {
            value["id"] = Profile.Id;
            if (action == "remove") value["expectedRevision"] = 1;
            else if (action != "read") { value["revision"] = 1; value["contentHash"] = Snapshot.ContentHash; }
            if (action == "disconnect") value["expectedRemoteShare"] = Profile.RemoteShare;
        }
        return value.ToJsonString(SmbConnectionProfile.Json);
    }
    private sealed record Call(string Action, string Body, string? Rid, CancellationToken Token);
    private sealed class Fake : ICortexConnectionProfileController
    {
        internal readonly List<Call> Calls = new();
        internal SmbConnectionProfileSnapshot[] Profiles = [Snapshot];
        internal Func<Call, Task<OperationReceipt>>? Handler;
        public Task<OperationReceipt> ExecuteAsync(string action, string body, string? rid, CancellationToken token)
        {
            var call = new Call(action, body, rid, token); Calls.Add(call);
            _ = ConnectionProfileOperationContract.Parse(new("connection_profile", new Dictionary<string, string> { ["op"] = action }, rid, Body: body));
            if (Handler is not null) return Handler(call);
            using var json = JsonDocument.Parse(body);
            var fields = new Dictionary<string, object?>();
            if (action == "list") fields["profiles"] = Profiles;
            else if (action == "read") fields["snapshot"] = Profiles.First(item => item.Profile.Id == json.RootElement.GetProperty("id").GetString());
            else if (action == "save")
            {
                var profile = SmbConnectionProfile.Parse(json.RootElement.GetProperty("profile"));
                var snapshot = new SmbConnectionProfileSnapshot(profile, json.RootElement.GetProperty("expectedRevision").GetInt64() + 1, new string('c', 64));
                Profiles = [snapshot]; fields["snapshot"] = snapshot;
            }
            else if (action == "remove") { fields["state"] = "removed"; fields["revision"] = json.RootElement.GetProperty("expectedRevision").GetInt64() + 1; Profiles = []; }
            else { fields["state"] = action == "inspect" ? "mapped" : action == "connect" ? "connected" : "disconnected"; fields["mapping_observed"] = action != "disconnect"; fields["action_issued"] = action != "inspect"; }
            return Task.FromResult(Reply(call, true, "done", fields));
        }
    }
    private static OperationReceipt Reply(Call call, bool ok, string state, IReadOnlyDictionary<string, object?> data, bool partial = false) =>
        new(ok, "fixture-jid", call.Rid!, state, ConnectionProfileOperationContract.PublicName(call.Action), ok ? null : "fixture_unavailable", data, Partial: partial);
    private sealed class Capture : IOperationDispatcher
    {
        internal OperationRequest? Last; internal int Disposals;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default)
        { Last = request; return Task.FromResult(new OperationReceipt(true, "fixture-jid", request.Rid!, "done", OperationNames.Public(request))); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Password(Func<int> active) : ICortexCertificatePasswordController
    {
        internal int Calls, Saves; internal string? Reference;
        public OwnerCertificateSecretResult Inspect(string reference) { Calls++; return new(true, "present"); }
        public OwnerCertificateSecretResult Save(string reference, ReadOnlySpan<char> password)
        { Check(active() == 1 && password.SequenceEqual("synthetic-p\u00e4\U0001f600"), "SMB shared capture lost exact buffer/lease"); Calls++; Saves++; Reference = reference; return new(true, "saved", ActionIssued: true); }
        public OwnerCertificateSecretResult Remove(string reference) { Calls++; return new(true, "removed", ActionIssued: true); }
    }
    private sealed class Lease(Action close) : IDisposable
    { private Action? _close = close; public void Dispose() => Interlocked.Exchange(ref _close, null)?.Invoke(); }
    private sealed class StaFixtureContext : SynchronizationContext, IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _callbacks = new(256);
        private Exception? _postFailure;
        private int _closed;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (Volatile.Read(ref _closed) != 0) return;
            try
            {
                if (!_callbacks.TryAdd((callback, state)))
                    Interlocked.CompareExchange(ref _postFailure, new InvalidOperationException("profile_fixture_callback_bound"), null);
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _closed) != 0) { }
        }
        internal void Complete(Task task, Stopwatch elapsed, TimeSpan budget)
        {
            while (!task.IsCompleted)
            {
                if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("profile_fixture_left_sta");
                if (Volatile.Read(ref _postFailure) is { } failure) throw failure;
                var remaining = budget - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("profile_fixture_async_deadline");
                if (_callbacks.TryTake(out var next, Math.Min(50, Math.Max(1, (int)remaining.TotalMilliseconds))))
                    next.Callback(next.State);
            }
            if (Volatile.Read(ref _postFailure) is { } error) throw error;
            task.GetAwaiter().GetResult();
            if (elapsed.Elapsed > budget) throw new TimeoutException("profile_fixture_async_deadline");
        }
        public void Dispose() { Volatile.Write(ref _closed, 1); _callbacks.Dispose(); }
    }
    private static async Task Refused(Func<Task> action)
    { try { await action(); } catch (ConnectionProfileContractException) { _assertions++; return; } throw new InvalidOperationException("invalid profile adapter envelope accepted"); }
    private static string Status(Control root) => Find<Label>(root, "Connection profile status").Text;
    private static T Find<T>(Control root, string name) where T : Control
    {
        foreach (Control child in root.Controls)
        { if (child is T found && child.AccessibleName == name) return found; var nested = FindOrNull<T>(child, name); if (nested is not null) return nested; }
        throw new InvalidOperationException("fixture_control_missing");
    }
    private static T? FindOrNull<T>(Control root, string name) where T : Control
    { foreach (Control child in root.Controls) { if (child is T found && child.AccessibleName == name) return found; if (FindOrNull<T>(child, name) is { } nested) return nested; } return null; }
    private static void Check(bool value, string code) { if (!value) throw new InvalidOperationException(code); _assertions++; }
}
