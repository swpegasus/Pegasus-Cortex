using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Synthetic native captures and identities only. No Windows account, token,
// WTS, NetAPI, credential, registry, filesystem or administrative operation.
internal static class WindowsAccountTests
{
    private const string Sid = "S-1-5-21-10-20-30-1001";
    private const int Session = 7;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static int _assertions;
    internal static WindowsAccountOperations CreateEngineFixtureOperations(string ownerSid, Func<LocalPeer> current) =>
        new(ownerSid, current, new FakeNative(current));

    internal static Task RunAsync()
    {
        _assertions = 0;
        Contracts();
        InventoryAndPaging();
        PrivacyShapesAndGroups();
        OwnerCancellationAndBounds();
        CoverageAndFailures();
        Console.WriteLine("PASS Windows account inventory: " + _assertions +
            " pure/injected parser, local owner, five closed views, group/SID coordinates, duplicate refusal, reobserved key pages, privacy fields, incomplete visibility and resource/output bounds; no real account/session/native call");
        return Task.CompletedTask;
    }

    private static void Contracts()
    {
        Check(WindowsAccountOperationContract.Actions.SequenceEqual(new[] { "list", "read" }) &&
            WindowsAccountOperationContract.Views.SequenceEqual(new[] { "users", "groups", "members", "sessions", "current" }), "closed family");
        foreach (var view in WindowsAccountOperationContract.Views)
        {
            var group = view == "members" ? "Administrators" : null;
            var list = Parse("list", view, group);
            var read = Parse("read", view, group, key: new string('a', 64));
            Check(!list.IsMutation && !read.IsMutation && list.Limit == 32 && list.RequestIdentity.Length == 64, "readonly identity/defaults");
            var body = JsonNode.Parse(Body("list", view, group))!.AsObject();
            var reverse = new JsonObject();
            foreach (var pair in body.Reverse()) reverse.Add(pair.Key, pair.Value?.DeepClone());
            Check(list.RequestIdentity == WindowsAccountOperationContract.Parse("list", reverse.ToJsonString()).RequestIdentity, "property order identity");
            foreach (var field in new[] { "server", "computer", "identity", "password", "token", "provider", "elevate", "command", "root" })
            {
                var altered = JsonNode.Parse(Body("list", view, group))!.AsObject(); altered[field] = null;
                Refuse(() => WindowsAccountOperationContract.Parse("list", altered.ToJsonString()));
            }
            Refuse(() => WindowsAccountOperationContract.Parse("list",
                Body("list", view, group).Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal)));
            Refuse(() => WindowsAccountOperationContract.Parse(Request("list", view, group) with { Root = "AB12" }));
            Refuse(() => WindowsAccountOperationContract.Parse(Request("list", view, group) with { Ws = "fixture" }));
            Refuse(() => WindowsAccountOperationContract.Parse(Request("list", view, group) with { Cwd = @"C:\fixture" }));
            Refuse(() => WindowsAccountOperationContract.Parse(Request("list", view, group) with
                { Arg = new Dictionary<string, string> { ["op"] = "list", ["wait"] = "false" } }));
        }
        foreach (var field in new[] { "version", "limit" })
        foreach (var value in new JsonNode?[] { null, JsonValue.Create("1"), JsonValue.Create(true), JsonValue.Create(1.5) })
        {
            var root = JsonNode.Parse(Body("list", "users"))!.AsObject(); root[field] = value?.DeepClone();
            Refuse(() => WindowsAccountOperationContract.Parse("list", root.ToJsonString()));
        }
        foreach (var limit in new[] { 0, -1, 65 }) Refuse(() => Parse("list", "users", limit: limit));
        Refuse(() => Parse("read", "users", key: "not-a-key"));
        Refuse(() => Parse("list", "users", after: new string('A', 64)));
        Refuse(() => Parse("list", "remote"));
        foreach (var group in new[] { "", " ", ".", "..", @"domain\Admins", @"\\server\Admins", "x*y", "x\0", new string('x', 257), " leading" })
            Refuse(() => Parse("list", "members", group));
        Refuse(() => Parse("list", "members"));
        Refuse(() => Parse("list", "users", "Administrators"));
        Refuse(() => WindowsAccountOperationContract.Parse("list", "{\"version\":1,\"view\":\"users\",\"group\":null}"));
        Refuse(() => WindowsAccountOperationContract.Parse("list", new string(' ', 4097)));
        Refuse(() => WindowsAccountOperationContract.Parse(Request("list", "users") with
            { Rid = "one", Arg = new Dictionary<string, string> { ["op"] = "list", ["rid"] = "two" } }));
    }

    private static void InventoryAndPaging()
    {
        var native = new FakeNative(Current) { Count = 101 };
        var operations = new WindowsAccountOperations(Sid, Current, native);
        Check(operations.IsCurrentOwner() && native.Calls == 0, "constructor passive");
        foreach (var view in WindowsAccountOperationContract.Views)
        {
            var result = operations.Execute(Parse("list", view, view == "members" ? "Administrators" : null, limit: 3));
            Check(result.Ok && result.Items.Count == (view == "current" ? 1 : 3) &&
                result.Items.All(item => item.Kind == WindowsAccountOperations.Kind(view)), "real view reaches fake consumer");
        }
        native.Count = 7;
        var first = operations.Execute(Parse("list", "users", limit: 2));
        Check(first.Ok && first.Items.Count == 2 && first.HasMore && first.NextKey == first.Items[^1].Key &&
            StringComparer.Ordinal.Compare(first.Items[0].Key, first.Items[1].Key) < 0, "sorted opaque first page");
        var second = operations.Execute(Parse("list", "users", limit: 2, after: first.NextKey));
        Check(second.Ok && second.Items.Count == 2 && !second.Items.Any(row => first.Items.Any(old => old.Key == row.Key)), "forward key page no duplicate");
        var key = first.Items[0].Key;
        var oldName = first.Items[0].Fields["name"];
        native.NameSuffix = "-new";
        var exact = operations.Execute(Parse("read", "users", key: key));
        Check(exact.Ok && exact.Items.Single().Key == key && !Equals(exact.Items[0].Fields["name"], oldName) &&
            native.Calls >= 8 && !exact.Coverage.Snapshot, "fresh read not retained snapshot");
        native.Rows = [User("first", "coordinate"), User("first", "coordinate")];
        Check(operations.Execute(Parse("list", "users")).Code == "account_duplicate_coordinates", "identical duplicate refused");
        native.Rows = [User("first", "coordinate"), User("changed", "coordinate")];
        Check(operations.Execute(Parse("list", "users")).Code == "account_duplicate_coordinates", "conflicting duplicate refused");
        native.Rows = [];
        var empty = operations.Execute(Parse("list", "users"));
        Check(empty.Ok && empty.Items.Count == 0 && !empty.HasMore && empty.NextKey is null, "complete empty observation");
        var notFound = operations.Execute(Parse("read", "users", key: new string('0', 64)));
        Check(!notFound.Ok && notFound.Code == "not_found", "complete missing key");
        native.Partial = true;
        var incomplete = operations.Execute(Parse("read", "users", key: new string('0', 64)));
        Check(!incomplete.Ok && incomplete.Code == "account_visibility_incomplete" && incomplete.Partial, "incomplete missing not absence proof");
    }

    private static void PrivacyShapesAndGroups()
    {
        var native = new FakeNative(Current);
        var operations = new WindowsAccountOperations(Sid, Current, native);
        var members = operations.Execute(Parse("list", "members", "administrators"));
        Check(members.Ok && native.Group == "administrators" &&
            members.Items[0].Fields["group"] as string == "ADMINISTRATORS", "exact group case-insensitive not trimmed");
        native.Rows = [WindowsAccountOperations.Item("group_member", "member", new() { ["group"] = "Other", ["sid"] = Sid })];
        Check(operations.Execute(Parse("list", "members", "Administrators")).Code == "account_invalid_native_data", "cross-group coordinates refused");
        foreach (var forbidden in new[] { "password", "token", "claims", "fullName", "comment", "rawFlags", "environment" })
        {
            var fields = new Dictionary<string, object?> { ["name"] = "fixture", [forbidden] = "fixture-private-marker" };
            native.Rows = [WindowsAccountOperations.Item("user", "bad-field", fields)];
            var refused = operations.Execute(Parse("list", "users"));
            Check(!refused.Ok && refused.Code == "account_invalid_native_data" && refused.Items.Count == 0 &&
                !JsonSerializer.Serialize(refused, Json).Contains("fixture-private-marker", StringComparison.Ordinal), "unrelated field cannot reach output");
        }
        native.Rows = [WindowsAccountOperations.Item("user", "wrong-type", new() { ["name"] = 123 })];
        Check(operations.Execute(Parse("list", "users")).Code == "account_invalid_native_data", "wrong scalar type");
        native.Rows = [WindowsAccountOperations.Item("user", "bad-utf", new() { ["name"] = "\ud800" })];
        Check(operations.Execute(Parse("list", "users")).Code == "account_invalid_native_data", "malformed Unicode rejected");
        native.Rows = [WindowsAccountOperations.Item("user", "too-long", new() { ["name"] = new string('x', 257) })];
        Check(operations.Execute(Parse("list", "users")).Code == "account_invalid_native_data", "per-field size bound");
        native.Rows = [WindowsAccountOperations.Item("current_identity", "other-current", new()
            { ["sid"] = "S-1-5-21-10-20-30-9999", ["sessionId"] = (uint)Session, ["elevated"] = false })];
        Check(operations.Execute(Parse("list", "current")).Code == "account_invalid_native_data", "current identity cannot describe another owner");
        native.Rows = [WindowsAccountOperations.Item("windows_session", "listener", new()
            { ["sessionId"] = 60000u, ["state"] = "listen" })];
        Check(operations.Execute(Parse("list", "sessions")).Code == "account_invalid_native_data", "listener not interactive slot");
        foreach (var sid in new[] { "S-1-5", Sid, "S-1-281474976710655-4294967295" }) Check(WindowsAccountOperations.IsSid(sid), "bounded canonical SID");
        foreach (var sid in new[] { "", "s-1-5-1", "S-2-5-1", "S-1-05-1", "S-1-5-01", "S-1-5-4294967296", "S-1-281474976710656-1" })
            Check(!WindowsAccountOperations.IsSid(sid), "invalid SID refused");
    }

    private static void OwnerCancellationAndBounds()
    {
        var peer = Current();
        var native = new FakeNative(() => peer);
        var operations = new WindowsAccountOperations(Sid, () => peer, native);
        native.OnCapture = () => peer = peer with { Session = Session + 1 };
        var denied = operations.Execute(Parse("list", "users"));
        Check(!denied.Ok && denied.Code == "owner_denied" && denied.Items.Count == 0, "owner loss after native discards payload");
        peer = Current(); native.OnCapture = null;
        var elevated = new WindowsAccountOperations(Sid, () => new(Sid, Session, Elevated: true), native);
        var before = native.Calls;
        Check(elevated.Execute(Parse("list", "users")).Code == "owner_denied" && native.Calls == before, "no elevated fallback");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { operations.Execute(Parse("list", "users"), cancellation.Token); throw new InvalidOperationException("Expected cancellation."); }
        catch (OperationCanceledException) { Check(native.Calls == before, "cancellation before capture"); }
        using var during = new CancellationTokenSource();
        native.OnCapture = during.Cancel;
        try { operations.Execute(Parse("list", "users"), during.Token); throw new InvalidOperationException("Expected cancellation."); }
        catch (OperationCanceledException) { _assertions++; }
        native.OnCapture = null; native.Count = 4097;
        Check(operations.Execute(Parse("list", "users")).Code == "account_limit", "4096-row managed bound");
        native.Count = 64; native.WorstCase = true;
        var page = operations.Execute(Parse("list", "sessions", limit: 64));
        Check(page.Ok && page.Items.Count == 64 &&
            JsonSerializer.SerializeToUtf8Bytes(page, Json).Length <= WindowsAccountOperations.MaximumPageBytes, "worst legal metadata within full page budget");
        native.Count = 1; native.WorstCase = false;
    }

    private static void CoverageAndFailures()
    {
        foreach (var view in WindowsAccountOperationContract.Views)
        {
            var coverage = WindowsAccountOperations.Coverage(view);
            Check(!coverage.Snapshot && !coverage.NativePreallocationCapped && coverage.MaximumNativeRows == 4096 &&
                coverage.PreferredNativePageBytes == (view is "users" or "groups" ? 8192 : (int?)null), "honest native allocation coverage");
        }
        Check(WindowsAccountOperations.Coverage("members").Limitations.Contains("windows_allocates_entire_membership_before_count_admission") &&
            WindowsAccountOperations.Coverage("sessions").Limitations.Contains("windows_allocates_wts_array_before_count_admission"), "whole-result allocation not hidden");
        var native = new FakeNative(Current)
        {
            Failure = "account_domain_controller_refused",
            Issues = [new("local_role", "account_domain_controller_refused", 5, "dsrole")]
        };
        var operations = new WindowsAccountOperations(Sid, Current, native);
        var refused = operations.Execute(Parse("list", "users"));
        Check(!refused.Ok && refused.Code == "account_domain_controller_refused" && refused.Issues.Single().NativeError == 5 &&
            refused.Items.Count == 0, "actual source error/domain preserved");
        native.Failure = null; native.Issues = [new("session_user", "account_unavailable", 5, "win32")]; native.Partial = true;
        var partial = operations.Execute(Parse("list", "sessions"));
        Check(partial.Ok && partial.Partial && partial.Issues[0].NativeDomain == "win32" && partial.Items.Count == 1, "optional source field issue retained");
        native.Issues = [new("session_user", "fixture-private-marker", 5, "win32")];
        var unknown = operations.Execute(Parse("list", "sessions"));
        Check(unknown.Code == "account_invalid_native_data" && unknown.Issues.Count == 0 &&
            !JsonSerializer.Serialize(unknown, Json).Contains("fixture-private-marker", StringComparison.Ordinal), "unknown provider text not echoed");
        native.Issues = []; native.Partial = false; native.BadCoverage = true;
        Check(operations.Execute(Parse("list", "users")).Code == "account_invalid_native_data", "false snapshot/hardcap assertion refused");
    }

    private static LocalPeer Current() => new(Sid, Session);
    private static WindowsAccountItem User(string name, string identity) => WindowsAccountOperations.Item("user", identity,
        new() { ["name"] = name, ["rid"] = 1001u, ["disabled"] = false });
    private static OperationRequest Request(string action, string view, string? group = null) =>
        new("windows_account", new Dictionary<string, string> { ["op"] = action }, Body: Body(action, view, group));
    private static WindowsAccountInvocation Parse(string action, string view, string? group = null, int? limit = null, string? after = null, string? key = null) =>
        WindowsAccountOperationContract.Parse(action, Body(action, view, group, limit, after, key));
    private static string Body(string action, string view, string? group = null, int? limit = null, string? after = null, string? key = null)
    {
        var root = new JsonObject { ["version"] = 1, ["view"] = view };
        if (group is not null) root["group"] = group;
        if (limit is not null) root["limit"] = limit.Value;
        if (after is not null) root["afterKey"] = after;
        if (action == "read") root["key"] = key;
        return root.ToJsonString();
    }
    private static void Refuse(Action action)
    {
        try { action(); throw new InvalidOperationException("Expected closed account request refusal."); }
        catch (WindowsAccountContractException) { _assertions++; }
    }
    private static void Check(bool condition, string message)
    { _assertions++; if (!condition) throw new InvalidOperationException("Windows account fixture: " + message); }

    private sealed class FakeNative(Func<LocalPeer> current) : IWindowsAccountNative
    {
        internal int Calls, Count = 1;
        internal string? Group, Failure;
        internal string NameSuffix = "";
        internal bool Partial, WorstCase, BadCoverage;
        internal Action? OnCapture;
        internal IReadOnlyList<WindowsAccountItem>? Rows;
        internal IReadOnlyList<WindowsAccountIssue> Issues = Array.Empty<WindowsAccountIssue>();
        public WindowsAccountCapture Capture(string view, string? group, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; Group = group;
            var rows = Rows ?? (view == "current" ? new[] { CurrentRow() } :
                Enumerable.Range(0, Count).Select(index => Row(view, group, index)).ToArray());
            OnCapture?.Invoke();
            var coverage = WindowsAccountOperations.Coverage(view);
            if (BadCoverage) coverage = coverage with { Snapshot = true, NativePreallocationCapped = true };
            return new(Failure is null, Failure ?? "observed", Failure is null ? rows : Array.Empty<WindowsAccountItem>(), Partial, coverage, Issues);
        }
        private WindowsAccountItem CurrentRow()
        {
            var peer = current();
            return WindowsAccountOperations.Item("current_identity", new { peer.Sid, peer.Session }, new()
                { ["sid"] = peer.Sid, ["sessionId"] = (uint)peer.Session, ["elevated"] = peer.Elevated });
        }
        private WindowsAccountItem Row(string view, string? group, int index) => view switch
        {
            "users" => User("fixture" + index + NameSuffix, "user" + index),
            "groups" => WindowsAccountOperations.Item("local_group", index, new() { ["name"] = "group" + index }),
            "members" => WindowsAccountOperations.Item("group_member", new { group, index }, new()
                { ["group"] = group!.ToUpperInvariant(), ["sid"] = "S-1-5-21-10-20-30-" + (1001 + index) }),
            _ => WindowsAccountOperations.Item("windows_session", index, WorstCase
                ? new() { ["sessionId"] = (uint)(index + 1), ["state"] = "active", ["stationName"] = new string('界', 128),
                    ["userName"] = new string('界', 256), ["domain"] = new string('界', 256) }
                : new() { ["sessionId"] = (uint)(index + 1), ["state"] = "disconnected" })
        };
    }
}
