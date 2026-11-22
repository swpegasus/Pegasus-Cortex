using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Opt-in, non-shipped acceptance owner. It never connects to installed PAO state
// or pipes. All application descendants remain owned until the bounded test ends.
internal static class ApplicationCandidateAcceptance
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record Plan(RegisteredToolDefinition Tool, string WorkingDirectory,
        string[] Arguments, int ObserveSeconds = 30);

    internal static async Task<int> RunAsync(string planPath)
    {
        try
        {
            if (!Path.IsPathFullyQualified(planPath)) throw new AgentOpsException("invalid");
            planPath = Path.GetFullPath(planPath);
            PathNormalizer.RejectAbsoluteReparseComponents(planPath);
            using var input = new FileStream(planPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > 64 * 1024) throw new AgentOpsException("invalid");
            var plan = await JsonSerializer.DeserializeAsync<Plan>(input, Json)
                ?? throw new AgentOpsException("invalid");
            if (plan.Tool is null || plan.Arguments is null || plan.ObserveSeconds is < 1 or > 120 ||
                plan.Tool.Mode != ToolExecutionMode.User || !Path.IsPathFullyQualified(plan.WorkingDirectory))
                throw new AgentOpsException("invalid");

            using var fixture = new CandidateFixture();
            var state = fixture.Dir("app-candidate");
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                // Go through normal public registration; caller-supplied probe
                // status cannot replace a successful candidate admission/probe.
                await using var engine = new AgentOpsEngine(state);
                var registration = await engine.ExecuteAsync(new OperationRequest("tool",
                    new Dictionary<string, string> { ["op"] = "add" },
                    Body: JsonSerializer.Serialize(plan.Tool, Json)), cancellation.Token);
                if (!registration.Ok)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, phase = "registration", err = registration.Err }, Json));
                    return registration.ExitCode;
                }

                await using var applications = new UserApplicationManager(new ToolRecipeOperations(state));
                var started = await applications.StartAsync(plan.Tool.Name, "candidate", plan.Arguments,
                    plan.WorkingDirectory, plan.WorkingDirectory, rid: "candidate-start", cancellationToken: cancellation.Token);
                var aid = (string)started["aid"]!;
                Console.WriteLine(JsonSerializer.Serialize(new { phase = "started", data = started }, Json));
                string? previous = null;
                try
                {
                    var until = DateTimeOffset.UtcNow.AddSeconds(plan.ObserveSeconds);
                    while (DateTimeOffset.UtcNow < until)
                    {
                        var status = await applications.StatusAsync("candidate", cancellation.Token);
                        var compact = JsonSerializer.Serialize(status, Json);
                        if (!StringComparer.Ordinal.Equals(previous, compact))
                        {
                            Console.WriteLine(compact);
                            previous = compact;
                        }
                        await Task.Delay(500, cancellation.Token);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                finally
                {
                    var stopped = await applications.StopAsync(aid, CancellationToken.None);
                    Console.WriteLine(JsonSerializer.Serialize(new { phase = "stopped", data = stopped }, Json));
                }
                return 0;
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (OperationCanceledException) { Console.WriteLine("{\"ok\":false,\"err\":\"canceled\"}"); return 5; }
        catch (AgentOpsException ex) { Console.WriteLine(JsonSerializer.Serialize(new { ok = false, err = ex.Code }, Json)); return 3; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { ok = false, err = "candidate_input", type = ex.GetType().Name }, Json));
            return 2;
        }
    }

    private sealed class CandidateFixture : IDisposable
    {
        private readonly TempRoot _root;
        internal CandidateFixture()
        {
            PathNormalizer.RejectAbsoluteReparseComponents(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pao-tests")));
            _root = new TempRoot();
        }
        internal string Dir(string name)
        {
            PathNormalizer.RejectAbsoluteReparseComponents(_root.Path);
            return _root.Dir(name);
        }

        public void Dispose()
        {
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pao-tests"));
            var actual = Path.GetFullPath(_root.Path);
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(actual), expectedParent) ||
                !Guid.TryParseExact(Path.GetFileName(actual), "N", out _)) throw new AgentOpsException("precondition");
            PathNormalizer.RejectAbsoluteReparseComponents(actual);
            var pending = new Stack<string>();
            pending.Push(actual);
            while (pending.TryPop(out var directory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new AgentOpsException("precondition");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
            _root.Dispose();
        }
    }
}
