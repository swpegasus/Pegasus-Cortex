using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class JobSubmissionProjectionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Pure projection/privacy checks followed by one actual engine/owned SQLite
    // lifecycle. No child, installed database, tool lookup or executable launch.
    internal static async Task RunAsync()
    {
        SafeTypedFieldsAndPrivacy();
        DeterministicBoundsAndLabels();
        ReceiptPreservation();
        await FreshReopenReplayAndPruningAsync();
    }

    private static void SafeTypedFieldsAndPrivacy()
    {
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "submission-pure"));
        var secretMarkers = new[] { "PRIVATE_POSITIONAL_TOKEN", "PRIVATE_STDIN", "PRIVATE_ENV", "PRIVATE_BODY", "PRIVATE_PATTERN", "PRIVATE_UNKNOWN" };
        var request = new OperationRequest("run", new Dictionary<string, string>
        {
            ["cfg"] = "registered-dotnet", ["tmo"] = "300", ["wait"] = "false",
            ["arg"] = "[\"" + secretMarkers[0] + "\"]", ["arg0"] = secretMarkers[0], ["stdin"] = secretMarkers[1],
            ["env.PRIVATE"] = secretMarkers[2], ["pattern"] = secretMarkers[4], ["unknown"] = secretMarkers[5]
        }, Cwd: directory, Body: secretMarkers[3]);
        var projection = Project(request);
        Check(projection.GetProperty("schema").GetString() == "pegasus.cortex.job-submission/1" &&
            projection.GetProperty("state").GetString() == "redacted" &&
            projection.GetProperty("operation").GetString() == "run" &&
            projection.GetProperty("command").GetString() == "pao run" &&
            projection.GetProperty("toolReference").GetString() == "registered-dotnet", "fixed schema and tool reference");
        var arguments = Strings(projection, "arguments");
        Check(arguments.SequenceEqual(new[] { "cfg=registered-dotnet", "cwd=" + directory, "tmo=300", "wait=false" }),
            "separate deterministic typed arguments");
        Check(Strings(projection, "omitted").SequenceEqual(new[] { "arg", "arg0", "body", "env.PRIVATE", "pattern", "stdin", "unknown" }) &&
            !projection.GetProperty("truncated").GetBoolean(), "omitted names contain no values");
        var serialized = projection.GetRawText();
        Check(secretMarkers.All(marker => !serialized.Contains(marker, StringComparison.Ordinal)), "all opaque and secret values omitted");
        Check(JobSubmissionProjection.Create(request with { Root = "PRIVATE_ROOT" }) is null, "presented root credential omits view entirely");

        foreach (var family in new[] { "registry", "certificate", "app", "terminal", "ui", "admin", "root", "rootcode", "application_access", "unknown-family" })
            Check(JobSubmissionProjection.Create(new(family, new Dictionary<string, string> { ["op"] = "private", ["src"] = directory }, Body: "PRIVATE_BODY")) is null,
                "protected or unknown family omitted");
        var write = Project(new("wr", new Dictionary<string, string> { ["dst"] = directory, ["old"] = "PRIVATE_OLD", ["text"] = "PRIVATE_TEXT" }, Body: "PRIVATE_WRITE"));
        Check(Strings(write, "arguments").SequenceEqual(new[] { "dst=" + directory }) &&
            Strings(write, "omitted").SequenceEqual(new[] { "body", "old", "text" }) && !write.GetRawText().Contains("PRIVATE_", StringComparison.Ordinal),
            "write and replacement content are never submission metadata");
        var powerShell = Project(new("pwsh", new Dictionary<string, string> { ["src"] = directory, ["arg"] = "PRIVATE_PWSH_ARGUMENT", ["arg0"] = "PRIVATE_PWSH_ARGUMENT" }));
        Check(Strings(powerShell, "arguments").SequenceEqual(new[] { "src=" + directory }) && !powerShell.GetRawText().Contains("PRIVATE_", StringComparison.Ordinal),
            "registered PowerShell arguments remain opaque");
    }

    private static void DeterministicBoundsAndLabels()
    {
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "submission-pure"));
        var ordered = new Dictionary<string, string> { ["dst"] = directory, ["pol"] = "fail", ["max"] = "10", ["free"] = "PRIVATE_FREE" };
        var reversed = ordered.Reverse().ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
        Check(Project(new("cp", ordered)).GetRawText() == Project(new("cp", reversed)).GetRawText(), "argument order does not change metadata");
        var clean = Project(new("mkdir", new Dictionary<string, string> { ["dst"] = directory }));
        Check(clean.GetProperty("state").GetString() == "available" && Strings(clean, "omitted").Length == 0 &&
            clean.GetProperty("toolReference").ValueKind == JsonValueKind.Null, "complete allowlisted view is available");

        var longPath = Path.GetPathRoot(directory)! + new string('\u754c', 225);
        var crowded = new Dictionary<string, string>
        {
            ["src"] = longPath, ["dst"] = longPath, ["cwd"] = longPath, ["workspace_root"] = longPath, ["cfg"] = longPath,
            ["stdin"] = "PRIVATE_LONG_STDIN", ["arg"] = "PRIVATE_LONG_ARGUMENT", ["bad key\nPRIVATE_LABEL"] = "PRIVATE_VALUE"
        };
        for (var index = 0; index < 100; index++) crowded["unknown_" + index.ToString("D3")] = "PRIVATE_VALUE";
        var bounded = Project(new("run", crowded));
        Check(Encoding.UTF8.GetByteCount(bounded.GetRawText()) <= JobSubmissionProjection.MaximumUtf8Bytes &&
            bounded.GetProperty("truncated").GetBoolean() && bounded.GetProperty("state").GetString() == "redacted" &&
            Strings(bounded, "omitted").Length <= 32 && !bounded.GetRawText().Contains("PRIVATE_", StringComparison.Ordinal),
            "UTF8 escaped payload and omission names are bounded");
        Check(Strings(bounded, "arguments").All(argument => crowded.Any(field => argument == field.Key + "=" + field.Value)),
            "bounds drop complete fields rather than invent partial paths");
        var malformed = Project(new("run", new Dictionary<string, string>
        {
            ["cfg"] = "valid-tool", ["cwd"] = directory + "\nPRIVATE_CONTROL", ["src"] = "relative/PRIVATE_PATH", ["wait"] = "PRIVATE_BOOL",
            ["tmo"] = "PRIVATE_NUMBER", ["pol"] = "PRIVATE_POLICY", ["ws"] = "bad\ud800", ["sha"] = "PRIVATE_HASH"
        }));
        Check(Strings(malformed, "arguments").SequenceEqual(new[] { "cfg=valid-tool" }) &&
            !malformed.GetRawText().Contains("PRIVATE_", StringComparison.Ordinal), "typed value shapes fail closed without copying rejected values");
        var unsafeSelector = Project(new("git", new Dictionary<string, string> { ["op"] = "PRIVATE_SELECTOR" }));
        Check(unsafeSelector.GetProperty("operation").GetString() == "git" &&
            Strings(unsafeSelector, "omitted").SequenceEqual(new[] { "op" }) && !unsafeSelector.GetRawText().Contains("PRIVATE_SELECTOR", StringComparison.Ordinal),
            "unknown operation selector cannot become a command label");
    }

    private static void ReceiptPreservation()
    {
        var request = new OperationRequest("run", new Dictionary<string, string> { ["cfg"] = "registered-tool", ["arg"] = "PRIVATE_ARGUMENT" });
        var data = new Dictionary<string, object?> { ["seq"] = 7L, ["exit"] = 17, ["out"] = "existing result", ["timed_out"] = false };
        var original = new OperationReceipt(false, "jid", "rid", "failed", "run", "external_failed", data, Partial: true);
        var attached = JobSubmissionProjection.Attach(original, request);
        var attachedData = attached.Data ?? throw new InvalidOperationException("submission_fixture_data_missing");
        Check(original.Data!.Count == 4 && attachedData.Count == 5 && data.All(field => Equals(attachedData[field.Key], field.Value)) &&
            attached.Jid == original.Jid && attached.Rid == original.Rid && attached.State == original.State && attached.Err == original.Err && attached.Partial,
            "attachment clones data and preserves result/outcome fields");
        var pruned = original with { Data = new Dictionary<string, object?> { ["history_pruned"] = true, ["seq"] = 7L } };
        Check(ReferenceEquals(JobSubmissionProjection.Attach(pruned, request), pruned), "pruned receipt is never backfilled");
        foreach (var family in new[] { "registry", "certificate", "app", "terminal", "ui", "admin", "root" })
            Check(ReferenceEquals(JobSubmissionProjection.Attach(original, request with { Op = family }), original), "private receipt result remains untouched");
    }

    private static async Task FreshReopenReplayAndPruningAsync()
    {
        using var fixture = new TempRoot();
        var state = fixture.Dir("state");
        var work = fixture.Dir("work");
        var destination = Path.Combine(work, "made");
        var rid = Guid.NewGuid().ToString("N");
        var request = new OperationRequest("mkdir", new Dictionary<string, string> { ["dst"] = destination, ["wait"] = "false" }, rid, Cwd: work);
        OperationReceipt completed;
        string initialProjection;
        await using (var engine = Engine(state))
        {
            var accepted = await engine.ExecuteAsync(request);
            Check(accepted.Ok && accepted.State == "queued", "actual initial queued receipt");
            initialProjection = Submission(accepted).GetRawText();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            completed = await engine.ExecuteAsync(new("job", new Dictionary<string, string> { ["op"] = "wait", ["jid"] = accepted.Jid }), deadline.Token);
            var completedData = completed.Data ?? throw new InvalidOperationException("submission_fixture_data_missing");
            Check(completed.Ok && completed.State == "done" && Directory.Exists(destination) &&
                Submission(completed).GetRawText() == initialProjection && completedData.ContainsKey("seq") &&
                completedData.GetValueOrDefault("dst")?.ToString() == destination, "actual final receipt preserves input and result fields");
        }
        using (var store = new SqliteJobStore(state))
        {
            var saved = store.FindByRid(rid)!;
            Check(saved.Request is null && saved.Receipt.Jid == completed.Jid && Submission(saved.Receipt).GetRawText() == initialProjection,
                "durable final request remains null");
            var legacy = store.Claim(new(true, "legacy", "legacy-rid", "done", "mkdir"),
                new("mkdir", new Dictionary<string, string> { ["dst"] = destination }), true);
            store.Update(legacy with { Request = null });
        }
        await using (var reopened = Engine(state))
        {
            var mustNotRun = Path.Combine(work, "replay-must-not-run");
            var replay = await reopened.ExecuteAsync(request with { Arg = new Dictionary<string, string> { ["dst"] = mustNotRun, ["wait"] = "false" } });
            Check(replay.Jid == completed.Jid && !Directory.Exists(mustNotRun) && Submission(replay).GetRawText() == initialProjection,
                "RID replay neither executes nor rewrites the original submission");
            var status = await reopened.ExecuteAsync(new("job", new Dictionary<string, string> { ["op"] = "stat", ["jid"] = completed.Jid }));
            var returned = (OperationReceipt)status.Data!["rec"]!;
            Check(Submission(returned).GetRawText() == initialProjection, "existing job_stat exposes the future receipt metadata");
            var oldStatus = await reopened.ExecuteAsync(new("job", new Dictionary<string, string> { ["op"] = "stat", ["jid"] = "legacy" }));
            Check(!((OperationReceipt)oldStatus.Data!["rec"]!).Data!.ContainsKey("submission"), "completed legacy input is not reconstructed");
        }
        using (var store = new SqliteJobStore(state))
        {
            var now = DateTimeOffset.UtcNow;
            var pruned = store.PruneHistory(now.AddMinutes(1), 16, false, (_, _, _) => false, now);
            Check(pruned.Pruned >= 1, "owned completed receipt pruned through existing policy");
            var saved = store.FindByRid(rid)!;
            Check(saved.Request is null && AgentOpsEngine.IsHistoryPruned(saved.Receipt) && !saved.Receipt.Data!.ContainsKey("submission"),
                "existing pruning removes submission payload and keeps request cleared");
        }
        await using (var reopened = Engine(state))
        {
            var replay = await reopened.ExecuteAsync(request);
            Check(replay.Jid == completed.Jid && AgentOpsEngine.IsHistoryPruned(replay) && !replay.Data!.ContainsKey("submission"),
                "pruned replay barrier does not resurrect submission metadata");
        }
    }

    private static AgentOpsEngine Engine(string root) => new(root, null, false, null, false, null,
        maintenanceMode: new OpenMode());
    private sealed class OpenMode : IMaintenanceModeSource
    {
        public MaintenanceModeRecord? Read() => null;
        public bool IsAcknowledged(MaintenanceEffect effect) => false;
    }
    private static JsonElement Project(OperationRequest request) => JobSubmissionProjection.Create(request)
        ?? throw new InvalidOperationException("submission_projection_missing");
    private static JsonElement Submission(OperationReceipt receipt) => JsonSerializer.SerializeToElement(receipt.Data!["submission"], Json);
    private static string[] Strings(JsonElement projection, string property) => projection.GetProperty(property).EnumerateArray().Select(value => value.GetString()!).ToArray();
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("job_submission_fixture: " + label);
    }
}
