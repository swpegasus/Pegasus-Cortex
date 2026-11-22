using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Windows.Registry;

// Injected registry/native and owner identities only. No live registry, product,
// process, filesystem inventory, credential provider or command execution.
internal static class GeneralInstalledProgramTests
{
    private const string Sid = "S-1-5-21-10-20-30-1001";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static int _assertions;

    internal static Task RunAsync()
    {
        _assertions = 0;
        Contract();
        SourcesAndMetadata();
        OwnerAndLinkRefusal();
        PagingAndChanges();
        PartialAndCancellation();
        Console.WriteLine($"PASS installed-program registrations: {_assertions} injected registry/owner checks; fixed roots/views, closed fields, bounded pages, exact generation/cursor, metadata-only visibility and cancellation; no live inventory or install/uninstall");
        return Task.CompletedTask;
    }

    private static void Contract()
    {
        Check(WindowsInstalledProgramOperationContract.Actions.SequenceEqual(new[] { "list", "read" }), "closed actions");
        Check(WindowsInstalledProgramOperationContract.Sources.SequenceEqual(new[] { "machine32", "machine64", "owner32", "owner64" }), "closed sources");
        foreach (var action in WindowsInstalledProgramOperationContract.Actions)
        {
            var invocation = Parse(action);
            Check(invocation.Action == action && !invocation.IsMutation && invocation.RequestIdentity.Length == 64 &&
                WindowsInstalledProgramOperationContract.IsReadOnly(action), "read-only identity");
        }
        foreach (var source in new[] { "owner", "current_user", "Machine64", "remote", "" })
            Refuse(() => WindowsInstalledProgramOperationContract.Parse("list", Body("list", source)));
        foreach (var key in new[] { "", ".", "..", "x\\y", "x/y", "x\n", new string('x', 256) })
            Refuse(() => WindowsInstalledProgramOperationContract.Parse("read", Body("read", key: key)));
        foreach (var limit in new[] { 0, 65 }) Refuse(() => Parse("list", limit: limit));
        foreach (var extra in new[] { "command", "computer", "hive", "view", "keyPath", "credentials", "filter" })
            Refuse(() => WindowsInstalledProgramOperationContract.Parse("list", "{\"version\":1,\"source\":\"machine64\",\"" + extra + "\":null}"));
        Refuse(() => WindowsInstalledProgramOperationContract.Parse("list", "{\"version\":1,\"version\":1,\"source\":\"machine64\"}"));
        Refuse(() => WindowsInstalledProgramOperationContract.Parse("list", "{\"version\":2,\"source\":\"machine64\"}"));
        Refuse(() => WindowsInstalledProgramOperationContract.Parse("list", "{\"version\":1,\"source\":\"machine64\",\"key\":\"x\"}"));
        Refuse(() => WindowsInstalledProgramOperationContract.Parse("read", "{\"version\":1,\"source\":\"machine64\",\"key\":\"x\",\"generation\":\"ABC\"}"));
        Refuse(() => WindowsInstalledProgramOperationContract.Parse("list", new string(' ', 8193)));
        var request = Request("list");
        foreach (var context in new[] { request with { Root = "root" }, request with { Ws = "workspace" }, request with { Cwd = @"C:\fixture" },
            request with { PathRoots = new Dictionary<string, string> { ["x"] = @"C:\fixture" } } })
            Refuse(() => WindowsInstalledProgramOperationContract.Parse(context));
        Refuse(() => WindowsInstalledProgramOperationContract.Parse(request with { Arg = new Dictionary<string, string> { ["op"] = "list", ["extra"] = "x" } }));
        Refuse(() => WindowsInstalledProgramOperationContract.Parse(request with { Body = null }));
        Refuse(() => WindowsInstalledProgramOperationContract.Parse(request with { Rid = "invalid\n" }));
        var reordered = WindowsInstalledProgramOperationContract.Parse("list", "{\"limit\":32,\"source\":\"machine64\",\"version\":1}");
        Check(reordered.RequestIdentity == Parse("list").RequestIdentity, "JSON ordering changed identity");
    }

    private static void SourcesAndMetadata()
    {
        foreach (var source in WindowsInstalledProgramOperationContract.Sources)
        {
            var fixture = new Fixture();
            var item = fixture.Native.Add(source, "Fixture App 日本");
            item.Values["DisplayName"] = Text("Synthetic program 日本");
            item.Values["DisplayVersion"] = Text("%FIXTURE_VERSION%", RegistryValueType.ExpandString);
            item.Values["Publisher"] = Text("Synthetic publisher");
            item.Values["InstallDate"] = Text("20261121");
            var size = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(size, uint.MaxValue);
            item.Values["EstimatedSize"] = new(RegistryValueType.DWord, size);
            item.Values["UninstallString"] = Text("fixture-command-must-not-be-read");
            item.Values["QuietUninstallString"] = Text("fixture-secret-marker-must-not-be-read");
            item.Values["InstallLocation"] = Text(@"C:\fixture-not-probed");
            var result = fixture.Operations.Execute(Parse("list", source));
            Check(result.Ok && !result.Partial && !result.HasMore && result.Scanned == 1, "fixed-source list");
            var observed = result.Programs.Single();
            Check(observed is { Name: "Synthetic program 日本", Version: "%FIXTURE_VERSION%", Publisher: "Synthetic publisher", InstallDate: "20261121", EstimatedSizeKb: 4294967295 } &&
                observed.Source == source && observed.Key == "Fixture App 日本", "metadata projection or literal expansion");
            var (hive, view) = source switch
            {
                "machine32" => (RegistryHiveId.LocalMachine, RegistryViewId.Registry32),
                "machine64" => (RegistryHiveId.LocalMachine, RegistryViewId.Registry64),
                "owner32" => (RegistryHiveId.CurrentUser, RegistryViewId.Registry32),
                "owner64" => (RegistryHiveId.CurrentUser, RegistryViewId.Registry64),
                _ => throw new InvalidOperationException("unexpected fixture source")
            };
            var root = new RegistryKeyIdentity(hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            Check(fixture.Native.Requests.All(query => query.Hive == root.Hive && query.View == root.View &&
                (query.KeyPath == root.KeyPath || query.KeyPath == root.KeyPath + "\\Fixture App 日本")), "registry target escaped fixed source");
            Check(fixture.Native.ValueNames.Distinct().Order().SequenceEqual(new[] { "DisplayName", "DisplayVersion", "EstimatedSize", "InstallDate", "Publisher" }.Order()) &&
                fixture.Native.Writes == 0, "nonmetadata field or write reached native boundary");
            Check(!JsonSerializer.Serialize(result).Contains("must-not-be-read", StringComparison.Ordinal), "command content entered output");
            var read = fixture.Operations.Execute(Parse("read", source, key: observed.Key, generation: observed.Generation));
            Check(read.Ok && read.Programs.Single().Generation == observed.Generation, "exact selected registration read");
        }
        var empty = new Fixture();
        Check(empty.Operations.Execute(Parse("list")).Code == "source_absent", "missing source was not explicit");
        empty.Native.Add("machine64", "Only a registration");
        var registration = empty.Operations.Execute(Parse("list")).Programs.Single();
        Check(registration.Name is null && registration.Version is null, "missing optional values were invented or registration hidden");
    }

    private static void OwnerAndLinkRefusal()
    {
        var fixture = new Fixture();
        foreach (var source in WindowsInstalledProgramOperationContract.Sources) fixture.Native.Add(source, "Fixture");
        var ordinary = fixture.Peer;
        foreach (var denied in new[] { ordinary with { Elevated = true }, ordinary with { Session = 0 }, ordinary with { Session = 8 },
            ordinary with { Sid = "S-1-5-18" }, ordinary with { Sid = "S-1-5-21-10-20-30-1002" } })
        {
            fixture.Peer = denied;
            foreach (var source in WindowsInstalledProgramOperationContract.Sources)
            {
                var calls = fixture.Native.Requests.Count;
                var result = fixture.Operations.Execute(Parse("list", source));
                Check(!result.Ok && result.Code == "owner_denied" && result.Programs.Count == 0 && fixture.Native.Requests.Count == calls,
                    "nonowner/elevated/System context read a source");
            }
        }
        Check(new WindowsInstalledProgramOperations(null).Execute(Parse("list")).Code == "owner_denied", "missing binding admitted");
        fixture.Peer = ordinary;
        var root = WindowsInstalledProgramOperationContract.RootForSource("machine64");
        fixture.Native.Links.Add(root);
        var linkedRoot = fixture.Operations.Execute(Parse("list"));
        Check(!linkedRoot.Ok && linkedRoot.Code == "registry_unsupported" && linkedRoot.Programs.Count == 0 && fixture.Native.ValueNames.Count == 0,
            "registry-link refusal was bypassed at root");
        fixture.Native.Links.Clear();
        fixture.Native.Links.Add(root with { KeyPath = root.KeyPath + "\\Fixture" });
        var linkedChild = fixture.Operations.Execute(Parse("list"));
        Check(linkedChild.Ok && linkedChild.Partial && linkedChild.Programs.Single().Issues.Single().Code == "registry_link_not_supported" &&
            fixture.Native.ValueNames.Count == 0, "linked registration was followed or silently omitted");
        fixture.Native.Links.Clear();
        fixture.Native.Failure = new(RegistryNativeFailure.AccessDenied, "injected_arbitrary_diagnostic_must_not_escape");
        var deniedRoot = fixture.Operations.Execute(Parse("list"));
        Check(!deniedRoot.Ok && deniedRoot.Code == "access" && !JsonSerializer.Serialize(deniedRoot).Contains("injected_", StringComparison.Ordinal), "access denial became absence/raw diagnostic");
        fixture.Native.Failure = new(RegistryNativeFailure.Unsupported, "registry64_view_unavailable");
        var unsupported = fixture.Operations.Execute(Parse("list"));
        Check(!unsupported.Ok && unsupported.Code == "registry_unsupported" && unsupported.Programs.Count == 0,
            "unavailable registry view became an empty success");
    }

    private static void PagingAndChanges()
    {
        var fixture = new Fixture();
        var root = fixture.Native.EnsureRoot("machine64");
        root.VirtualCount = 1_000_000;
        var first = fixture.Operations.Execute(Parse("list", limit: 64));
        Check(first.Ok && first.Scanned == 64 && first.Programs.Count == 64 && first.HasMore && first.Continuation?.NextIndex == 64,
            "large source was not bounded and continued");
        Check(fixture.Native.NameReads == 65 && fixture.Native.ValueNames.Count == 64 * 5, "enumeration/read work exceeded page plus one lookahead");
        var next = fixture.Operations.Execute(Parse("list", limit: 2, cursor: first.Continuation));
        Check(next.Ok && next.Programs[0].Key == "p000064" && next.Continuation?.NextIndex == 66, "continuation did not resume exact source position");
        Refuse(() => Parse("list", "owner64", cursor: first.Continuation));
        Refuse(() => Parse("list", cursor: first.Continuation! with { NextIndex = 0 }));
        Refuse(() => Parse("list", cursor: first.Continuation! with { Version = 2 }));
        root.WriteTime++;
        var stale = fixture.Operations.Execute(Parse("list", cursor: first.Continuation));
        Check(!stale.Ok && stale.Code == "precondition" && stale.Programs.Count == 0 && stale.Continuation is null, "stale cursor returned a page");

        var changed = new Fixture();
        var child = changed.Native.Add("machine64", "Fixture");
        child.Values["DisplayName"] = Text("Fixture");
        var read = changed.Operations.Execute(Parse("read", key: "Fixture"));
        child.WriteTime++;
        var valueReads = changed.Native.ValueNames.Count;
        var staleRead = changed.Operations.Execute(Parse("read", key: "Fixture", generation: read.Programs.Single().Generation));
        Check(!staleRead.Ok && staleRead.Code == "precondition" && changed.Native.ValueNames.Count == valueReads, "generation precondition read fields");
        changed.Native.Hook = (stage, _) =>
        {
            if (stage != "value") return;
            changed.Native.Hook = null;
            changed.Native.EnsureRoot("machine64").WriteTime++;
        };
        var changedRoot = changed.Operations.Execute(Parse("list"));
        Check(!changedRoot.Ok && changedRoot.Code == "precondition" && changedRoot.Programs.Count == 0 && changedRoot.Continuation is null,
            "root changed during metadata observation but returned continuation");
    }

    private static void PartialAndCancellation()
    {
        var fixture = new Fixture();
        var item = fixture.Native.Add("machine64", "Fixture");
        item.Values["DisplayName"] = Text(new string('x', 513));
        item.Values["DisplayVersion"] = Text("1.0");
        item.Values["Publisher"] = new(RegistryValueType.Binary, [1, 2]);
        item.Values["InstallDate"] = new(RegistryValueType.String, [0x61]);
        var observed = fixture.Operations.Execute(Parse("read", key: "Fixture"));
        Check(observed.Ok && observed.Partial && observed.Programs.Single().Version == "1.0" &&
            observed.Programs.Single().Issues.Select(issue => issue.Code).SequenceEqual(new[] { "field_limit", "unsupported_type", "invalid_text" }),
            "invalid metadata was truncated/coerced or its visibility hidden");
        fixture.Native.Hook = (stage, _) =>
        {
            if (stage != "value") return;
            fixture.Native.Hook = null; item.WriteTime++;
        };
        var changed = fixture.Operations.Execute(Parse("read", key: "Fixture"));
        Check(!changed.Ok && changed.Code == "precondition" && changed.Programs.Count == 0, "cross-generation values were blended");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var calls = fixture.Native.Requests.Count;
        Canceled(() => fixture.Operations.Execute(Parse("list"), canceled.Token));
        Check(fixture.Native.Requests.Count == calls, "already canceled request touched native boundary");
        using var during = new CancellationTokenSource();
        fixture.Native.Hook = (stage, _) => { if (stage == "value") during.Cancel(); };
        Canceled(() => fixture.Operations.Execute(Parse("list"), during.Token));
        fixture.Native.Hook = (stage, _) => { if (stage == "value") fixture.Peer = fixture.Peer with { Elevated = true }; };
        var lost = fixture.Operations.Execute(Parse("list"));
        Check(!lost.Ok && lost.Code == "owner_denied" && lost.Programs.Count == 0 && lost.Continuation is null, "observed owner loss returned metadata");
        Check(fixture.Native.Writes == 0, "inventory invoked mutation");
    }

    private static WindowsInstalledProgramInvocation Parse(string action, string source = "machine64", int limit = 32,
        string key = "Fixture", string? generation = null, RegistryBrowseCursor? cursor = null) =>
        WindowsInstalledProgramOperationContract.Parse(action, Body(action, source, limit, key, generation, cursor));
    private static string Body(string action, string source = "machine64", int limit = 32, string key = "Fixture",
        string? generation = null, RegistryBrowseCursor? cursor = null) => action == "list"
        ? JsonSerializer.Serialize(new { version = 1, source, limit, cursor }, WebJson)
        : generation is null ? JsonSerializer.Serialize(new { version = 1, source, key }, WebJson) : JsonSerializer.Serialize(new { version = 1, source, key, generation }, WebJson);
    private static OperationRequest Request(string action) => new("installed_program", new Dictionary<string, string> { ["op"] = action }, "fixture-rid", Body: Body(action));
    private static RegistryNativeValue Text(string value, RegistryValueType type = RegistryValueType.String) => new(type, Encoding.Unicode.GetBytes(value + "\0"));
    private static void Refuse(Action action)
    { try { action(); } catch (WindowsInstalledProgramContractException) { _assertions++; return; } throw new InvalidOperationException("invalid installed-program contract accepted"); }
    private static void Canceled(Action action)
    { try { action(); } catch (OperationCanceledException) { _assertions++; return; } throw new InvalidOperationException("inventory cancellation not propagated"); }
    private static void Check(bool condition, string code) { if (!condition) throw new InvalidOperationException(code); _assertions++; }

    internal static RegistryOwnerBinding CreateEngineFixtureBinding()
    {
        var fixture = new Fixture();
        fixture.Native.Add("machine64", "fixture-registration").Values["DisplayName"] = Text("Fixture registration");
        return new RegistryOwnerBinding(Sid, () => fixture.Peer, admission => new RegistryOperations(fixture.Native, admission));
    }

    private sealed class Fixture
    {
        internal LocalPeer Peer = new(Sid, 7, Elevated: false);
        internal readonly Native Native = new();
        internal readonly WindowsInstalledProgramOperations Operations;
        internal Fixture() => Operations = new(new RegistryOwnerBinding(Sid, () => Peer, admission => new RegistryOperations(Native, admission)));
    }
    private sealed class Node
    {
        internal long WriteTime = 1;
        internal int? VirtualCount;
        internal readonly List<string> Children = new();
        internal readonly Dictionary<string, RegistryNativeValue> Values = new(StringComparer.Ordinal);
    }
    private sealed class Native : IRegistryNative
    {
        private readonly Dictionary<RegistryKeyIdentity, Node> _nodes = new();
        private readonly Node _virtualChild = new();
        internal readonly List<RegistryKeyIdentity> Requests = new();
        internal readonly List<string> ValueNames = new();
        internal readonly HashSet<RegistryKeyIdentity> Links = new();
        internal int Writes, NameReads;
        internal RegistryNativeException? Failure;
        internal Action<string, RegistryKeyIdentity>? Hook;
        internal Node EnsureRoot(string source)
        {
            var key = WindowsInstalledProgramOperationContract.RootForSource(source);
            if (!_nodes.TryGetValue(key, out var root)) _nodes[key] = root = new();
            return root;
        }
        internal Node Add(string source, string name)
        {
            var root = EnsureRoot(source); root.Children.Add(name); root.WriteTime++;
            var key = WindowsInstalledProgramOperationContract.RootForSource(source);
            return _nodes[key with { KeyPath = key.KeyPath + "\\" + name }] = new();
        }
        private Node? Find(RegistryKeyIdentity key)
        {
            Requests.Add(key);
            if (Failure is not null) throw Failure;
            if (Links.Any(link => link.Hive == key.Hive && link.View == key.View &&
                (key.KeyPath == link.KeyPath || key.KeyPath.StartsWith(link.KeyPath + "\\", StringComparison.Ordinal))))
                throw new RegistryNativeException(RegistryNativeFailure.Unsupported, "registry_link_not_supported");
            if (_nodes.TryGetValue(key, out var value)) return value;
            var separator = key.KeyPath.LastIndexOf('\\');
            if (separator > 0 && _nodes.TryGetValue(key with { KeyPath = key.KeyPath[..separator] }, out var root) && root.VirtualCount is not null)
                return _virtualChild;
            return null;
        }
        public RegistryNativeKeyInfo? InspectKey(RegistryKeyIdentity key)
        { Hook?.Invoke("inspect", key); var node = Find(key); return node is null ? null : new(node.WriteTime, node.VirtualCount ?? node.Children.Count, node.Values.Count); }
        public RegistryNativeValue? ReadValue(RegistryValueIdentity identity, int maxBytes)
        {
            Hook?.Invoke("value", identity.Key);
            ValueNames.Add(identity.ValueName);
            var node = Find(identity.Key) ?? throw new RegistryNativeException(RegistryNativeFailure.NotFound, "key_not_found");
            if (!node.Values.TryGetValue(identity.ValueName, out var value)) return null;
            if (value.Data.Length > maxBytes) throw new RegistryNativeException(RegistryNativeFailure.LimitExceeded, "native_value_size_limit");
            return value;
        }
        public RegistryBrowseEntry? ReadName(RegistryKeyIdentity key, RegistryBrowseKind kind, int index, int maxNameChars)
        {
            Hook?.Invoke("name", key); NameReads++;
            if (kind != RegistryBrowseKind.Keys || maxNameChars != RegistryLimits.MaxKeySegmentChars) throw new InvalidOperationException("unexpected enumeration");
            var node = Find(key) ?? throw new RegistryNativeException(RegistryNativeFailure.NotFound, "key_not_found");
            return index >= (node.VirtualCount ?? node.Children.Count) ? null : new(node.VirtualCount is null ? node.Children[index] : "p" + index.ToString("D6"), null);
        }
        public bool CreateKey(RegistryKeyIdentity key) { Writes++; throw new InvalidOperationException("write forbidden"); }
        public void SetValue(RegistryValueIdentity identity, RegistryValueType type, byte[] data) { Writes++; throw new InvalidOperationException("write forbidden"); }
        public void DeleteValue(RegistryValueIdentity identity) { Writes++; throw new InvalidOperationException("write forbidden"); }
        public void DeleteEmptyKey(RegistryKeyIdentity key) { Writes++; throw new InvalidOperationException("write forbidden"); }
    }
}
