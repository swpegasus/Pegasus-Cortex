using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Internal;
using Pegasus.AgentOps.Protocol;

internal static partial class ApplicationCapacityTests
{
    private const string Configuration = "capacity-fixture";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task ActiveAsync()
    {
        using var fixture = new TempRoot();
        var tools = await ToolsAsync(fixture);
        var handles = new List<CapacityHandle>();
        await using var manager = Manager(tools, handles);
        var admitted = new List<IReadOnlyDictionary<string, object?>>();
        for (var i = 0; i < UserApplicationManager.MaxActiveApplications; i++)
            admitted.Add(await manager.StartAsync(Configuration, "active-" + i, ["private-value"], fixture.Path));

        var failure = await RejectAsync("app_capacity", () =>
            manager.StartAsync(Configuration, "rejected-active", ["private-value"], fixture.Path));
        AssertCapacity(failure, "active", 16, 16);
        Check(handles.Count == 16, "Active-capacity rejection invoked the launch callback.");
        await RejectAsync("not_found", () => manager.StatusAsync("rejected-active"));
        var replay = await manager.StartAsync(Configuration, "active-0", ["private-value"], fixture.Path);
        Check(Aid(replay) == Aid(admitted[0]) && handles.Count == 16,
            "A full manager stopped replaying its existing exact generation.");

        await manager.StopAsync(Aid(admitted[0]));
        Check(handles[0].TerminationRequests == 1 && handles.Skip(1).All(handle => handle.TerminationRequests == 0),
            "An exact stop affected unrelated generations.");
        await manager.StartAsync(Configuration, "replacement", ["private-value"], fixture.Path);
        Check(handles.Count == 17, "Releasing active capacity did not admit exactly one new generation.");
    }

    internal static async Task RetainedAsync()
    {
        using var fixture = new TempRoot();
        var tools = await ToolsAsync(fixture);
        var handles = new List<CapacityHandle>();
        await using var manager = Manager(tools, handles);
        string? previous = null;
        for (var i = 0; i < UserApplicationManager.MaxRetainedApplications; i++)
        {
            var current = await manager.StartAsync(Configuration, "one-slot", ["private-value"], fixture.Path,
                expectedAid: previous, rid: "generation-" + i);
            previous = Aid(current);
            await manager.StopAsync(previous);
        }

        var restart = await RejectAsync("app_capacity", () =>
            manager.StartAsync(Configuration, "one-slot", ["private-value"], fixture.Path,
                expectedAid: previous, rid: "rejected-successor"));
        AssertCapacity(restart, "retained", 128, 128);
        var fresh = await RejectAsync("app_capacity", () =>
            manager.StartAsync(Configuration, "rejected-retained", ["private-value"], fixture.Path));
        AssertCapacity(fresh, "retained", 128, 128);
        Check(handles.Count == 128, "Retained-capacity rejection invoked the launch callback.");
        await RejectAsync("not_found", () => manager.StatusAsync("rejected-retained"));

        var replay = await manager.StartAsync(Configuration, "one-slot", ["private-value"], fixture.Path);
        Check(Aid(replay) == previous && handles.Count == 128,
            "Terminal replay created a generation or required free capacity.");
        await manager.StopAsync(previous!);
        var stillFull = await RejectAsync("app_capacity", () =>
            manager.StartAsync(Configuration, "after-terminal-stop", ["private-value"], fixture.Path));
        AssertCapacity(stillFull, "retained", 128, 128);
        Check(handles.Count == 128 && handles.All(handle => handle.TerminationRequests == 1),
            "A terminal stop evicted history or repeated process termination.");
    }

    internal static async Task RetainedBeforeObservationAsync()
    {
        using var fixture = new TempRoot();
        var tools = await ToolsAsync(fixture);
        var handles = new List<CapacityHandle>();
        await using var manager = Manager(tools, handles);
        for (var i = 0; i < UserApplicationManager.MaxRetainedApplications; i++)
        {
            var current = await manager.StartAsync(Configuration, "priority-" + i, [], fixture.Path);
            if (i < UserApplicationManager.MaxRetainedApplications - UserApplicationManager.MaxActiveApplications)
                await manager.StopAsync(Aid(current));
        }

        // Match the existing retained-first short circuit even when unrelated
        // active accounting becomes unavailable. Cleanup restores observation.
        foreach (var handle in handles) handle.ObservationUnavailable = true;
        try
        {
            var failure = await RejectAsync("app_capacity", () =>
                manager.StartAsync(Configuration, "retained-priority", [], fixture.Path));
            AssertCapacity(failure, "retained", 128, 128);
            Check(handles.Count == 128, "Retained-first rejection invoked the launch callback.");
        }
        finally
        {
            foreach (var handle in handles) handle.ObservationUnavailable = false;
        }
    }

    internal static async Task ProjectionAsync()
    {
        foreach (var resource in new[] { "active", "retained" })
        {
            var maximum = resource == "active" ? 16 : 128;
            var evidence = FailureEvidence.ApplicationCapacity(resource, maximum, maximum);
            var receipt = new OperationReceipt(false, "capacity-jid", "capacity-rid", "failed", "app_start",
                "app_capacity", Partial: false, Failure: evidence);
            var dispatcher = new RecordingDispatcher(_ => receipt);
            var reply = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new
            {
                op = "app_start", cfg = Configuration, name = "private-name", arg = new[] { "private-value" },
                output = "agent"
            }), dispatcher, CancellationToken.None);
            var text = reply["content"]![0]!["text"]!.GetValue<string>();
            Check(receipt.ExitCode == 8 && reply["isError"]!.GetValue<bool>() &&
                !reply.ContainsKey("structuredContent") &&
                text.Contains("execution=not_dispatched source=application-capacity-admission", StringComparison.Ordinal) &&
                text.Contains("field=" + evidence.Field!.Name, StringComparison.Ordinal) &&
                text.Contains("actual=" + maximum + " max=" + maximum, StringComparison.Ordinal) &&
                text.Contains("diag[1]=application_" + resource + "_capacity", StringComparison.Ordinal) &&
                !text.Contains("private-name", StringComparison.Ordinal) &&
                !text.Contains("private-value", StringComparison.Ordinal) &&
                Encoding.UTF8.GetByteCount(text) <= 4096,
                "Compact capacity evidence lost the stable failure, rejected bound, or privacy.");
            if (resource == "retained")
            {
                var next = evidence.Next ?? throw new InvalidOperationException("Missing retained-capacity next action.");
                Check(next.Contains("app_inventory", StringComparison.Ordinal) &&
                    next.Contains("app_release", StringComparison.Ordinal) &&
                    next.Contains("epoch", StringComparison.Ordinal) && next.Contains("RID", StringComparison.Ordinal) &&
                    !next.Contains("end of this user host", StringComparison.Ordinal) &&
                    text.Contains("next=" + next, StringComparison.Ordinal),
                    "Retained-capacity agent guidance still required shutdown or lost exact release preconditions.");
                var structured = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new
                    { op = "app_start", cfg = Configuration, name = "private-name", output = "structured" }), dispatcher, CancellationToken.None);
                Check(structured["structuredContent"]!["failure"]!["next"]!.GetValue<string>() == next,
                    "Structured retained-capacity failure guidance diverged from the terminal-release contract.");
            }
        }
        Console.WriteLine("EVIDENCE deterministic fake application handles and mocked MCP dispatch only; no process launch, installed-host access, or live recovery.");
    }

    private static UserApplicationManager Manager(ToolRecipeOperations tools, List<CapacityHandle> handles) =>
        new(tools, async (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new MemoryStream();
            await new ApplicationHostFrame(ApplicationHostFrame.RootExited, 0, 0, 0).WriteAsync(stream);
            stream.Position = 0;
            var handle = new CapacityHandle(stream);
            handles.Add(handle);
            return new ApplicationProcessHost(handle, 1000 + handles.Count, "2026-10-18T00:00:00.0000000Z");
        });

    private static async Task<ToolRecipeOperations> ToolsAsync(TempRoot fixture)
    {
        var executable = Path.Combine(fixture.Path, "inert-capacity-fixture.exe");
        await File.WriteAllTextAsync(executable, "Inert fixture; never executed.", new UTF8Encoding(false));
        var tools = new ToolRecipeOperations(fixture.Dir("capacity-state"), new ProbeRunner());
        await tools.Tools.RegisterAsync(new RegisteredToolDefinition(Configuration, 1, executable,
            VersionProbe: new ToolProbeDefinition(["--version"], RequiredText: "fixture")));
        return tools;
    }

    private static async Task<AgentOpsException> RejectAsync(string code,
        Func<Task<IReadOnlyDictionary<string, object?>>> action)
    {
        try { await action(); }
        catch (AgentOpsException exception)
        {
            Check(exception.Code == code, "Expected " + code + " but received " + exception.Code + ".");
            return exception;
        }
        throw new InvalidOperationException("Expected " + code + ".");
    }

    private static void AssertCapacity(AgentOpsException exception, string resource, int actual, int maximum)
    {
        var failure = exception.Failure;
        Check(exception.Code == "app_capacity" && failure is not null &&
            failure.Execution == new OperationFailureFact(OperationExecutionState.NotDispatched, "application-capacity-admission") &&
            failure.Retry == new OperationFailureFact(OperationRetryDisposition.AfterCorrection, "application-capacity-admission") &&
            failure.Field == new OperationFailureField(resource == "active" ? "active_applications" : "retained_generations") &&
            failure.Bound == new OperationFailureBound(actual, maximum, resource == "active" ? "applications" : "generations") &&
            failure.Diagnostics?.Single().Code == "application_" + resource + "_capacity" &&
            failure.DetailAvailability == "none" && failure.LogAvailability == "not_retained",
            "Capacity failure lost its exhausted resource, measured bound, or no-dispatch evidence.");
        var text = JsonSerializer.Serialize(failure, Json);
        Check(!text.Contains("private-value", StringComparison.Ordinal),
            "Capacity failure exposed application arguments.");
    }

    private static string Aid(IReadOnlyDictionary<string, object?> value) => value["aid"]!.ToString()!;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ProbeRunner : IToolInvocationRunner
    {
        public Task<ProcessResult> RunAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProcessResult(0, "fixture", string.Empty, false, false, false));
        }
    }

    private sealed class CapacityHandle(Stream responses) : IApplicationHostHandle
    {
        private int _active = 2;
        private volatile bool _helperExited;
        internal bool ObservationUnavailable { get; set; }
        internal TaskCompletionSource? RetirementGate { get; set; }
        internal void FinishWorkers() => Volatile.Write(ref _active, 1);
        internal int TerminationRequests { get; private set; }
        public int ActiveProcesses => ObservationUnavailable
            ? throw new AgentOpsException("app_observation_incomplete") : Volatile.Read(ref _active);
        public bool HelperHasExited => _helperExited;
        public Stream Responses { get; } = responses;
        public bool RequestTermination()
        {
            TerminationRequests++;
            _helperExited = true;
            Volatile.Write(ref _active, 0);
            return true;
        }
        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(_helperExited, "A fake application was retired before exact termination.");
            return RetirementGate?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
        }
        public void Dispose() => Responses.Dispose();
    }
}
