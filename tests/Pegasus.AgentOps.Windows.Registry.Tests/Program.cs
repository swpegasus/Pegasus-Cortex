using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Windows.Registry;
using Pegasus.AgentOps.Windows.Registry.Tests;

if (args.Length != 0 && args is not ["--fixtures-only"])
{
    Console.Error.WriteLine("registry_fixture_arguments_invalid");
    return 2;
}

var assertions = 0;
var groups = new List<string>();
try
{
    ValidationAndAdmission();
    NativeNameDecoding();
    RawValuesAndIdentity();
    PagingAndStaleness();
    GuardedMutations();
    UncertainOutcomes();
    Console.WriteLine(JsonSerializer.Serialize(new { ok = true, assertions, groups,
        evidence = "portable unit fixtures using an injected in-memory registry boundary only",
        liveRegistry = "not accessed", windowsNativeAdapter = "not exercised", enginePaoWiring = "not included",
        mutations = "fixture only; non-atomic postcondition observations are not Windows CAS evidence" }));
    return 0;
}
catch (Exception failure)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, assertions, completedGroups = groups,
        failureType = failure.GetType().Name, code = "registry_fixture_failed" }));
    return 1;
}

void Check(bool condition, string description)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(description);
}

RegistryKeyIdentity Key(string path = "Software\\PAO Fixture", RegistryHiveId hive = RegistryHiveId.CurrentUser,
    RegistryViewId view = RegistryViewId.Registry64) => new(hive, view, path);
RegistryValueData Data(RegistryValueType type, byte[] bytes) => new(type, Convert.ToBase64String(bytes));
RegistryValueSnapshot Value(RegistryOperations operations, RegistryValueIdentity identity)
{
    var read = operations.ReadValue(identity);
    Check(read.IsSuccess && read.Value is not null, "fixture read available");
    return read.Value!;
}
RegistryValuePreimage Guard(RegistryValueSnapshot snapshot) => new(snapshot.Key.Generation, snapshot.Digest);
string Generation(RegistryOperations operations, RegistryKeyIdentity key)
{
    var read = operations.InspectKey(key);
    Check(read.IsSuccess && read.Value is not null, "fixture generation available");
    return read.Value!.Generation;
}

void ValidationAndAdmission()
{
    var fake = new FixtureRegistry();
    fake.SeedKey(Key());
    var defaultDenied = new RegistryOperations(fake);
    Check(defaultDenied.InspectKey(Key()).Status == RegistryStatus.Denied && fake.Calls == 0, "default deny includes reads");
    var policy = new FixtureAdmission();
    var operations = new RegistryOperations(fake, policy);
    var invalid = new[]
    {
        new RegistryKeyIdentity((RegistryHiveId)99, RegistryViewId.Registry64, "Software"),
        new RegistryKeyIdentity(RegistryHiveId.CurrentUser, (RegistryViewId)0, "Software"),
        Key("\\Software"), Key("Software\\"), Key("Software\\\\Fixture"), Key("Software/Fixture"),
        Key("Software\\..\\Fixture"), Key("Software\\.\\Fixture"), Key("Software\\\0Fixture"),
        Key("Software\\\ud800"), Key(new string('x', RegistryLimits.MaxKeySegmentChars + 1)),
        Key(string.Join('\\', Enumerable.Repeat("a", RegistryLimits.MaxKeyDepth + 1))),
        Key(string.Join('\\', Enumerable.Repeat(new string('a', 40), 30)))
    };
    foreach (var key in invalid) Check(operations.InspectKey(key).Status == RegistryStatus.InvalidRequest, "invalid key rejected");
    Check(operations.ReadValue(new(Key(), "bad\0name")).Status == RegistryStatus.InvalidRequest, "NUL value name rejected");
    Check(operations.ReadValue(new(Key(), new string('n', RegistryLimits.MaxValueNameChars + 1))).Status == RegistryStatus.InvalidRequest,
        "oversized value name rejected");
    Check(operations.Browse(new(Key(), RegistryBrowseKind.Values, 0)).Status == RegistryStatus.InvalidRequest, "zero page rejected");
    Check(operations.Browse(new(Key(), RegistryBrowseKind.Values, RegistryLimits.MaxPageEntries + 1)).Status == RegistryStatus.InvalidRequest,
        "large page rejected");
    var guard = new RegistryValuePreimage(new string('0', 64), null);
    var identity = new RegistryValueIdentity(Key(), "example");
    var malformed = new[]
    {
        new RegistryValueData((RegistryValueType)6, ""), new RegistryValueData(RegistryValueType.Binary, " not-base64 "),
        new RegistryValueData(RegistryValueType.Binary, "AA==\n"), new RegistryValueData(RegistryValueType.DWord, "AA=="),
        new RegistryValueData(RegistryValueType.QWord, ""), Data(RegistryValueType.Binary, new byte[RegistryLimits.MaxValueBytes + 1])
    };
    foreach (var data in malformed)
        Check(operations.SetValue(new(identity, data, guard)).Status == RegistryStatus.InvalidRequest, "invalid payload rejected");
    Check(operations.SetValue(new(identity, Data(RegistryValueType.Binary, []), new("*", null))).Status == RegistryStatus.InvalidRequest,
        "unguarded set rejected");
    Check(operations.DeleteValue(new(identity, guard)).Status == RegistryStatus.InvalidRequest, "delete requires existing digest");
    Check(operations.DeleteEmptyKey(new(Key(""), guard.KeyGeneration)).Status == RegistryStatus.InvalidRequest, "root deletion rejected");
    Check(operations.SetValue(new(new(Key(""), "example"), Data(RegistryValueType.Binary, []), guard)).Status == RegistryStatus.InvalidRequest,
        "root write rejected");
    Check(fake.Calls == 0 && policy.Requests.Count == 0, "all validation precedes policy and native access");

    var denied = new RegistryOperations(fake, new FixtureAdmission(_ => false));
    Check(denied.CreateKey(new(Key("Software\\new"), guard.KeyGeneration)).Status == RegistryStatus.Denied, "create denied");
    Check(denied.SetValue(new(identity, Data(RegistryValueType.Binary, []), guard)).Status == RegistryStatus.Denied, "set denied");
    Check(denied.DeleteValue(new(identity, new(guard.KeyGeneration, new string('1', 64)))).Status == RegistryStatus.Denied, "delete value denied");
    Check(denied.DeleteEmptyKey(new(Key(), guard.KeyGeneration)).Status == RegistryStatus.Denied, "delete key denied");
    var brokenPolicy = new RegistryOperations(fake, new FixtureAdmission(_ => throw new InvalidOperationException("fixture")));
    Check(brokenPolicy.InspectKey(Key()).Status == RegistryStatus.Denied && fake.Calls == 0, "policy failure denies before OS");
    groups.Add("validation-before-admission-and-default-deny");
}

void NativeNameDecoding()
{
    const int maximum = RegistryLimits.MaxValueNameChars;
    Check(RegistryNativeNameDecoder.Decode("allowed", 7, maximum) == "allowed", "exact native name length accepted");
    var unicode = "Ω日本😀";
    Check(RegistryNativeNameDecoder.Decode(unicode, (uint)unicode.Length, maximum) == unicode,
        "Unicode and paired surrogates preserved exactly");
    Check(RegistryNativeNameDecoder.Decode("", 0, maximum) == "", "default empty value name accepted at exact zero length");
    Check(Refused("allowed", 14, RegistryNativeFailure.Unsupported), "truncated embedded-NUL identity refused");
    Check(Refused("allowed", 6, RegistryNativeFailure.Unsupported), "materialized length greater than reported length refused");
    Check(Refused("", 1, RegistryNativeFailure.Unsupported), "empty name with nonzero reported length refused");
    var embeddedNul = "allowed\0hidden";
    Check(Refused(embeddedNul, (uint)embeddedNul.Length, RegistryNativeFailure.Unsupported),
        "embedded NUL refused even when exact length survives marshalling");
    Check(Refused("\ud800", 1, RegistryNativeFailure.Unsupported), "unpaired high surrogate refused");
    Check(Refused("\udc00", 1, RegistryNativeFailure.Unsupported), "unpaired low surrogate refused");
    Check(Refused("a\nb", 3, RegistryNativeFailure.Unsupported), "native control character refused");
    var maximumName = new string('n', maximum);
    Check(RegistryNativeNameDecoder.Decode(maximumName, (uint)maximum, maximum) == maximumName, "exact name bound accepted");
    Check(Refused(new string('n', maximum + 1), (uint)maximum + 1, RegistryNativeFailure.LimitExceeded), "oversized native name refused");
    groups.Add("pure-native-name-length-and-identity-decoding");

    static bool Refused(string materialized, uint length, RegistryNativeFailure expected)
    {
        try { RegistryNativeNameDecoder.Decode(materialized, length, RegistryLimits.MaxValueNameChars); return false; }
        catch (RegistryNativeException failure)
        { return failure.Failure == expected && failure.Code is "native_name_identity_invalid" or "native_name_size_limit"; }
    }
}

void RawValuesAndIdentity()
{
    var fake = new FixtureRegistry();
    var operations = new RegistryOperations(fake, new FixtureAdmission());
    var payloads = new (RegistryValueType Type, byte[] Bytes)[]
    {
        (RegistryValueType.String, Encoding.Unicode.GetBytes("Pegasus Ω 日本 😀\0")),
        (RegistryValueType.ExpandString, Encoding.Unicode.GetBytes("%FIXTURE_UNEXPANDED%\\Example\0")),
        (RegistryValueType.MultiString, Encoding.Unicode.GetBytes("one\0Ω\0\0")),
        (RegistryValueType.Binary, [0, 1, 0, 255, 128]), (RegistryValueType.None, []),
        (RegistryValueType.DWord, [255, 0, 128, 1]), (RegistryValueType.DWordBigEndian, [1, 128, 0, 255]),
        (RegistryValueType.QWord, [0, 1, 2, 3, 128, 200, 254, 255]),
        // Raw reads preserve even malformed/unterminated existing string data.
        (RegistryValueType.String, [0x41, 0x00, 0xff])
    };
    for (var i = 0; i < payloads.Length; i++)
    {
        var identity = new RegistryValueIdentity(Key(), i == 0 ? "" : $"fixture{i}");
        fake.SeedValue(identity, payloads[i].Type, payloads[i].Bytes);
        var read = Value(operations, identity);
        var raw = read.Value!;
        Check(read.Exists && raw.Type == payloads[i].Type &&
            Convert.FromBase64String(raw.Base64).AsSpan().SequenceEqual(payloads[i].Bytes), "raw read preserved");
        Check(read.Digest is { Length: 64 } && read.Key.Generation.Length == 64, "exact read yields bounded preimage");
        Check(!read.ToString().Contains(raw.Base64, StringComparison.Ordinal) || raw.Base64.Length == 0,
            "value snapshot diagnostic redacts body");
        var destination = new RegistryValueIdentity(Key(), $"roundtrip{i}");
        var missing = Value(operations, destination);
        Check(operations.SetValue(new(destination, Data(payloads[i].Type, payloads[i].Bytes), Guard(missing))).IsSuccess,
            "raw kind fixture set admitted");
        var roundtrip = Value(operations, destination).Value!;
        Check(roundtrip.Type == raw.Type && roundtrip.Base64 == raw.Base64, "raw kind write and read preserve exact bytes");
    }
    var max = Enumerable.Repeat((byte)0x5a, RegistryLimits.MaxValueBytes).ToArray();
    fake.SeedValue(new(Key(), "maximum"), RegistryValueType.Binary, max);
    Check(Convert.FromBase64String(Value(operations, new(Key(), "maximum")).Value!.Base64).Length == max.Length, "bounded maximum exact read");
    fake.SeedValue(new(Key(), "oversized"), RegistryValueType.Binary, new byte[RegistryLimits.MaxValueBytes + 1]);
    Check(operations.ReadValue(new(Key(), "oversized")).Status == RegistryStatus.LimitExceeded, "oversized read fails bounded");
    Check(fake.LargestReadBound == RegistryLimits.MaxValueBytes, "native read has fixed bound");
    var absent = Value(operations, new(Key(), "absent"));
    Check(!absent.Exists && absent.Digest is null, "missing value distinct from empty payload");
    Check(operations.InspectKey(Key("missing")).Status == RegistryStatus.NotFound, "missing key distinct from missing value");

    var digests = new HashSet<string>();
    foreach (var hive in Enum.GetValues<RegistryHiveId>())
    foreach (var view in Enum.GetValues<RegistryViewId>())
    {
        var identity = new RegistryValueIdentity(Key(hive: hive, view: view), "identity");
        fake.SeedValue(identity, RegistryValueType.Binary, [1]);
        Check(digests.Add(Value(operations, identity).Digest!), "hive and view bind digest identity");
    }
    var original = Value(operations, new(Key(), "fixture1"));
    var otherCase = Value(operations, new(Key("software\\pao fixture"), "FIXTURE1"));
    Check(original.Digest == otherCase.Digest && original.Key.Generation == otherCase.Key.Generation, "case insensitive identity remains exact");
    groups.Add("raw-kinds-unicode-default-value-and-explicit-hive-view");
}

void PagingAndStaleness()
{
    var fake = new FixtureRegistry();
    var operations = new RegistryOperations(fake, new FixtureAdmission());
    fake.SeedKey(Key());
    for (var i = 0; i < 5; i++) fake.SeedValue(new(Key(), $"value{i}"), RegistryValueType.Binary, [1]);
    var first = operations.Browse(new(Key(), RegistryBrowseKind.Values, 2));
    var firstPage = first.Value!;
    Check(first.IsSuccess && firstPage.Entries.Count == 2 && firstPage.HasMore && firstPage.Continuation is not null,
        "first bounded page");
    Check(fake.NamesRead == 3, "one page plus one lookahead only");
    var second = operations.Browse(new(Key(), RegistryBrowseKind.Values, 2, firstPage.Continuation));
    var secondPage = second.Value!;
    var third = operations.Browse(new(Key(), RegistryBrowseKind.Values, 2, second.Value!.Continuation));
    var thirdPage = third.Value!;
    Check(second.IsSuccess && third.IsSuccess && thirdPage.Entries.Count == 1 && !thirdPage.HasMore && thirdPage.Continuation is null,
        "continuation completes without phantom more");
    Check(firstPage.Entries.Concat(secondPage.Entries).Concat(thirdPage.Entries).Select(entry => entry.Name).Distinct().Count() == 5,
        "stable pages have no duplicates");
    var cursor = firstPage.Continuation!;
    var before = fake.Calls;
    Check(operations.Browse(new(Key(view: RegistryViewId.Registry32), RegistryBrowseKind.Values, 2, cursor)).Status == RegistryStatus.InvalidRequest,
        "cursor view substitution rejected");
    Check(operations.Browse(new(Key(), RegistryBrowseKind.Keys, 2, cursor)).Status == RegistryStatus.InvalidRequest,
        "cursor kind substitution rejected");
    Check(operations.Browse(new(Key(), RegistryBrowseKind.Values, 2, cursor with { NextIndex = -1 })).Status == RegistryStatus.InvalidRequest,
        "negative cursor rejected");
    Check(fake.Calls == before, "cursor validation precedes native access");
    fake.Touch(Key());
    Check(operations.Browse(new(Key(), RegistryBrowseKind.Values, 2, cursor)).Status == RegistryStatus.Stale, "changed generation rejects old cursor");
    var changed = false;
    fake.BeforeCall = operation => { if (operation == "name" && !changed) { changed = true; fake.Touch(Key()); } };
    Check(operations.Browse(new(Key(), RegistryBrowseKind.Values, 2)).Status == RegistryStatus.Stale, "concurrent page changes discard page");
    fake.BeforeCall = null;
    fake.SeedKey(Key("Software\\PAO Fixture\\child"));
    var keys = operations.Browse(new(Key(), RegistryBrowseKind.Keys, 2));
    var keyPage = keys.Value!;
    Check(keys.IsSuccess && keyPage.Entries.Single().Name == "child" && keyPage.Entries[0].NativeValueType is null,
        "key browse does not return values or recurse");

    var budgetFake = new FixtureRegistry();
    budgetFake.SeedKey(Key());
    for (var i = 0; i < 40; i++) budgetFake.SeedValue(new(Key(), i.ToString("D3") + new string('n', 509)), RegistryValueType.Binary, []);
    var budget = new RegistryOperations(budgetFake, new FixtureAdmission()).Browse(new(Key(), RegistryBrowseKind.Values, 128));
    var budgetPage = budget.Value!;
    Check(budget.IsSuccess && budgetPage.Entries.Count == 32 && budgetPage.HasMore &&
        budgetPage.Entries.Sum(entry => entry.Name.Length) == RegistryLimits.MaxPageNameChars, "aggregate page name budget");
    fake.SeedValue(new(Key(), new string('n', RegistryLimits.MaxValueNameChars + 1)), RegistryValueType.Binary, []);
    Check(operations.Browse(new(Key(), RegistryBrowseKind.Values, 128)).Status == RegistryStatus.LimitExceeded,
        "unsupported long existing name is explicit bounded failure");
    groups.Add("bounded-page-lookahead-budget-and-stale-continuations");
}

void GuardedMutations()
{
    var fake = new FixtureRegistry();
    var policy = new FixtureAdmission(request => request.Key.KeyPath.StartsWith("Software\\PAO Fixture", StringComparison.OrdinalIgnoreCase));
    var operations = new RegistryOperations(fake, policy);
    fake.SeedKey(Key());
    var identity = new RegistryValueIdentity(Key(), "example");
    var missing = Value(operations, identity);
    var set = operations.SetValue(new(identity, Data(RegistryValueType.String, Encoding.Unicode.GetBytes("Ω\0")), Guard(missing)));
    Check(set.IsSuccess && set.Value is { WriteInvoked: true, PostconditionObserved: true, AtomicCompareAndSwap: false },
        "guarded fixture set observes postcondition without CAS claim");
    Check(operations.SetValue(new(identity, Data(RegistryValueType.Binary, [2]), Guard(missing))).Status == RegistryStatus.PreconditionFailed && fake.Writes == 1,
        "old preimage refuses second write");
    var current = Value(operations, identity);
    var wrongDigest = Guard(current) with { ValueDigest = new string('0', 64) };
    Check(operations.DeleteValue(new(identity, wrongDigest)).Status == RegistryStatus.PreconditionFailed && fake.Writes == 1,
        "changed value digest refuses deletion");
    var deletion = operations.DeleteValue(new(identity, Guard(current)));
    Check(deletion.IsSuccess && !Value(operations, identity).Exists, "exact guarded value deletion");

    var child = Key("Software\\PAO Fixture\\child");
    var create = operations.CreateKey(new(child, Generation(operations, Key())));
    Check(create.IsSuccess && fake.Writes == 3, "create one child under existing guarded parent");
    Check(policy.Requests.Last(request => request.Operation == RegistryOperation.CreateKey).Parent == Key(), "create policy sees exact parent");
    Check(operations.CreateKey(new(child, Generation(operations, Key()))).Status == RegistryStatus.PreconditionFailed,
        "existing key not reconfigured");
    var writesBeforeMissingParent = fake.Writes;
    Check(operations.CreateKey(new(Key("Software\\PAO Fixture\\missing-parent\\child"), new string('0', 64))).Status == RegistryStatus.NotFound &&
        fake.Writes == writesBeforeMissingParent, "create does not implicitly create a parent chain");
    fake.SeedValue(new(child, "nonempty"), RegistryValueType.Binary, []);
    Check(operations.DeleteEmptyKey(new(child, Generation(operations, child))).Status == RegistryStatus.PreconditionFailed, "nonempty key refused");
    var data = Value(operations, new(child, "nonempty"));
    Check(operations.DeleteValue(new(new(child, "nonempty"), Guard(data))).IsSuccess, "remove exact fixture value");
    var childGeneration = Generation(operations, child);
    fake.Touch(child);
    Check(operations.DeleteEmptyKey(new(child, childGeneration)).Status == RegistryStatus.PreconditionFailed, "changed key generation refuses deletion");
    Check(operations.DeleteEmptyKey(new(child, Generation(operations, child))).IsSuccess &&
        operations.InspectKey(child).Status == RegistryStatus.NotFound, "empty direct key deleted and absence observed");
    Check(operations.SetValue(new(new(Key("Software\\outside"), "example"), Data(RegistryValueType.Binary, []), Guard(missing))).Status == RegistryStatus.Denied,
        "injected policy scope stays authoritative");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var beforeCalls = fake.Calls;
    Check(operations.SetValue(new(identity, Data(RegistryValueType.Binary, []), Guard(missing)), cancellation.Token).Status == RegistryStatus.Cancelled &&
        beforeCalls == fake.Calls, "cancelled mutation never starts OS access");
    groups.Add("guarded-create-set-delete-and-no-recursion");
}

void UncertainOutcomes()
{
    foreach (var mode in new[] { "failure-after-write", "post-read-failure", "postcondition-replaced", "cancel-after-write" })
    {
        var fake = new FixtureRegistry();
        fake.SeedKey(Key());
        var operations = new RegistryOperations(fake, new FixtureAdmission());
        var identity = new RegistryValueIdentity(Key(), "example");
        var guard = Guard(Value(operations, identity));
        using var cancellation = new CancellationTokenSource();
        fake.AfterWrite = _ =>
        {
            switch (mode)
            {
                case "failure-after-write": throw new RegistryNativeException(RegistryNativeFailure.Failed, "fixture_lost_reply", 123);
                case "post-read-failure": fake.BeforeCall = _ => throw new RegistryNativeException(RegistryNativeFailure.AccessDenied, "fixture_postcheck_denied", 5); break;
                case "postcondition-replaced": fake.SeedValue(identity, RegistryValueType.Binary, [3]); break;
                case "cancel-after-write": cancellation.Cancel(); break;
            }
        };
        var result = operations.SetValue(new(identity, Data(RegistryValueType.Binary, [2]), guard), cancellation.Token);
        Check(result.Status == RegistryStatus.Uncertain && result.Value is { WriteInvoked: true, PostconditionObserved: false, AtomicCompareAndSwap: false },
            "uncertain consumed write reported honestly");
        Check(fake.Writes == 1, "uncertain write never retried");
    }

    var conflictFake = new FixtureRegistry();
    conflictFake.SeedKey(Key());
    var conflictOperations = new RegistryOperations(conflictFake, new FixtureAdmission());
    var child = Key("Software\\PAO Fixture\\racing");
    var generation = Generation(conflictOperations, Key());
    conflictFake.BeforeCall = operation => { if (operation == "create") conflictFake.SeedKey(child); };
    var raced = conflictOperations.CreateKey(new(child, generation));
    Check(raced.Status == RegistryStatus.Uncertain && conflictFake.Writes == 0 && raced.Value!.WriteInvoked,
        "create losing race does not attribute another writer's key");

    var changedFake = new FixtureRegistry();
    changedFake.SeedValue(new(Key(), "example"), RegistryValueType.Binary, [1]);
    var changedOperations = new RegistryOperations(changedFake, new FixtureAdmission());
    var identityChanged = new RegistryValueIdentity(Key(), "example");
    var preimage = Guard(Value(changedOperations, identityChanged));
    changedFake.BeforeCall = operation => { if (operation == "read") changedFake.Touch(Key()); };
    var changed = changedOperations.SetValue(new(identityChanged, Data(RegistryValueType.Binary, [2]), preimage));
    Check(changed.Status == RegistryStatus.Stale && !changed.Value!.WriteInvoked && changedFake.Writes == 0,
        "change during preimage read refuses mutation");
    groups.Add("changed-preimages-and-uncertain-consumed-write-outcomes");
}
