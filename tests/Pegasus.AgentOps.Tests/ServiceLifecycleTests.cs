using System.Globalization;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class ServiceLifecycleTests
{
    private const string Service = ApplicationAuthorizationProtocol.CerebralServiceName;
    private const string Start = "2026-09-29T12:34:56.0000000Z";

    public static async Task RunAsync()
    {
        using var temp = new ServiceTestRoot();
        var fake = new FakeServiceLifecycle(new(Service, "running", 4242, Start));
        await using var engine = new AgentOpsEngine(
            temp.Directory("engine"),
            pathNormalizer: null,
            directOnly: true,
            bundledNsis: null,
            enableUserApplications: false,
            systemTools: null,
            serviceLifecycle: fake);

        var noSystem = await engine.ExecuteAuthenticatedFrozenDirectAsync(Request("status", "no-system"));
        Is("auth", noSystem.Err, "authenticated user-host request controlled a service");

        var applicationBroker = new AuthenticatedServiceBroker(
            temp.Directory("application-broker"), LocalIdentity.Current().Sid,
            new RootCodeVerifier(Path.Combine(temp.Path, "application-root.verifier")),
            false, null,
            (request, cancellationToken) => engine.ExecuteAuthenticatedFrozenDirectAsync(request, cancellationToken));
        await using (applicationBroker)
        {
            var applicationDenied = await applicationBroker.HandleApplicationAsync(Request("status", "application-session"), CancellationToken.None);
            Is("auth", applicationDenied.Err, "private Cerebral application session controlled its own service");
            Is(0, fake.Calls, "denied application request reached SCM adapter");
        }

        var owner = LocalIdentity.Current();
        var verifier = new RootCodeVerifier(Path.Combine(temp.Path, "root.verifier"));
        verifier.Enroll("AB12");
        await using var broker = new AuthenticatedServiceBroker(
            temp.Directory("broker"), owner.Sid, verifier, false, null,
            (request, cancellationToken) => engine.ExecuteAuthenticatedFrozenDirectAsync(request, cancellationToken));

        var denied = await broker.HandleSystemAsync(Request("status", "denied", root: "ZZ99"), owner, CancellationToken.None);
        Is("auth", denied.Err, "invalid root code reached service lifecycle");
        Is(0, fake.Calls, "denied request reached SCM adapter");

        var status = await broker.HandleSystemAsync(Request("status", "status", root: "AB12"), owner, CancellationToken.None);
        True(status.Ok, status.Err ?? "service status failed");
        Is("running", Convert.ToString(status.Data!["service_state"], CultureInfo.InvariantCulture), "service status mismatch");
        Is(4242, Integer(status.Data["pid"]), "service pid mismatch");

        var stale = await broker.HandleSystemAsync(Request("stop", "stale", root: "AB12", expect: "running", pid: 9999, start: Start), owner, CancellationToken.None);
        Is("pid_reused", stale.Err, "stale service identity was accepted");
        Is("running", fake.Current.State, "stale identity changed service state");

        var stopped = await broker.HandleSystemAsync(Request("stop", "stop", root: "AB12", expect: "running", pid: 4242, start: Start), owner, CancellationToken.None);
        True(stopped.Ok, stopped.Err ?? "exact service stop failed");
        Is("stopped", fake.Current.State, "service did not stop");

        var started = await broker.HandleSystemAsync(Request("start", "start", root: "AB12", expect: "stopped"), owner, CancellationToken.None);
        True(started.Ok, started.Err ?? "exact service start failed");
        Is("running", fake.Current.State, "service did not start");

        fake.FailRestartAfterStop = true;
        var restartRequest = Request("restart", "restart", expect: "running", pid: fake.Current.ProcessId, start: fake.Current.ProcessStart);
        restartRequest = restartRequest with { Arg = new Dictionary<string, string>(restartRequest.Arg, StringComparer.Ordinal) { ["__execution_context_system"] = "1" } };
        var recovery = await engine.ExecuteAuthenticatedFrozenDirectAsync(restartRequest);
        Is("recovery_required", recovery.Err, "partial restart did not require recovery");
        True(recovery.Partial, "partial restart lost its partial marker");
        Is("stopped", Convert.ToString(recovery.Data!["service_state"], CultureInfo.InvariantCulture), "partial restart did not report current state");

        await VerifyClientSurfacesAsync(temp.Path);
    }

    private static async Task VerifyClientSurfacesAsync(string fixtureRoot)
    {
        await using var capture = new ServiceCaptureDispatcher();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var cli = await PaoProgram.RunCliAsync(
            ["service", "stop", Service, "expect=running", "pid=4242", $"start={Start}", "tmo=30", "root=AB12", "rid=cli-service"],
            capture, stdout, stderr, CancellationToken.None);
        Is(0, cli, $"service CLI rejected typed request: {stderr}");
        Is("service", capture.Last!.Op, "CLI operation mismatch");
        Is("stop", capture.Last.Arg["op"], "CLI action mismatch");
        Is("AB12", capture.Last.Root, "CLI root was not transient request metadata");

        stdout.GetStringBuilder().Clear(); stderr.GetStringBuilder().Clear();
        var wrappedCli = await PaoProgram.RunCliAsync(
            ["service", "status", "\\\"" + Service + "\\\"", "\\\"root=ab12\\\"", "rid=\\\"cli-service-wrapped\\\""],
            capture, stdout, stderr, CancellationToken.None);
        Is(0, wrappedCli, $"service CLI rejected narrowly wrapped root input: {stderr}");
        Is("AB12", capture.Last!.Root, "CLI did not canonicalize the wrapped lowercase root code");
        Is(Service, capture.Last.Arg["service"], "CLI did not normalize a wrapped positional service field");
        Is("cli-service-wrapped", capture.Last.Rid, "CLI did not normalize a wrapped typed scalar field");

        stdout.GetStringBuilder().Clear(); stderr.GetStringBuilder().Clear();
        var leadingWrappedCli = await PaoProgram.RunCliAsync(
            ["\\\"root=ab12\\\"", "service", "status", "\\\"" + Service + "\\\"", "rid=cli-leading-root"],
            capture, stdout, stderr, CancellationToken.None);
        Is(0, leadingWrappedCli, $"service CLI rejected a narrowly wrapped leading root input: {stderr}");
        Is("AB12", capture.Last!.Root, "CLI did not canonicalize a leading wrapped lowercase root code");

        var callsBeforeMalformedCli = capture.Calls;
        stdout.GetStringBuilder().Clear(); stderr.GetStringBuilder().Clear();
        var malformedCli = await PaoProgram.RunCliAsync(
            ["service", "status", Service, "root=prefixAB12suffix", "rid=cli-service-malformed"],
            capture, stdout, stderr, CancellationToken.None);
        Is(2, malformedCli, "CLI accepted root text containing more than the exact code");
        Is(callsBeforeMalformedCli, capture.Calls, "malformed CLI root reached the dispatcher");

        using var validJson = JsonDocument.Parse($$"""{"op":"service_status","service":"{{Service}}","root":"AB12","rid":"mcp-service"}""");
        var mcp = await PaoProgram.CallUnifiedMcpToolAsync(validJson.RootElement, capture, CancellationToken.None);
        Is(callsBeforeMalformedCli + 1, capture.Calls, "MCP rejected typed service status");
        Is("service", capture.Last!.Op, "MCP operation mismatch");
        Is("status", capture.Last.Arg["op"], "MCP action mismatch");

        using var wrappedJson = JsonDocument.Parse($$"""{"op":"service_status","service":" \"{{Service}}\" ","root":" \\\"ab12\\\" ","rid":" \"mcp-service-wrapped\" "}""");
        var wrappedMcp = await PaoProgram.CallUnifiedMcpToolAsync(wrappedJson.RootElement, capture, CancellationToken.None);
        Is(callsBeforeMalformedCli + 2, capture.Calls, "MCP rejected narrowly wrapped root input");
        Is("AB12", capture.Last!.Root, "MCP did not canonicalize the wrapped lowercase root code");
        Is(Service, capture.Last.Arg["service"], "MCP did not normalize a wrapped typed service field");
        Is("mcp-service-wrapped", capture.Last.Rid, "MCP did not normalize a wrapped typed identifier");

        using var missingRootJson = JsonDocument.Parse($$"""{"op":"service_status","service":"{{Service}}"}""");
        var missingRoot = await PaoProgram.CallUnifiedMcpToolAsync(missingRootJson.RootElement, capture, CancellationToken.None);
        Is(callsBeforeMalformedCli + 2, capture.Calls, "MCP accepted service control without root authorization");

        using var arbitraryJson = JsonDocument.Parse("""{"op":"service_status","service":"Spooler","root":"AB12"}""");
        var arbitrary = await PaoProgram.CallUnifiedMcpToolAsync(arbitraryJson.RootElement, capture, CancellationToken.None);
        Is(callsBeforeMalformedCli + 2, capture.Calls, "MCP accepted an arbitrary Windows service");

        using var embeddedJson = JsonDocument.Parse($$"""{"op":"service_status","service":"{{Service}}","root":"prefixAB12suffix"}""");
        var embedded = await PaoProgram.CallUnifiedMcpToolAsync(embeddedJson.RootElement, capture, CancellationToken.None);
        Is(callsBeforeMalformedCli + 2, capture.Calls, "MCP accepted root text containing more than the exact code");

        var literalPattern = " \"keep these content quotes\" ";
        using var scalarAndContentJson = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            op = "search",
            src = " \"" + fixtureRoot + "\" ",
            pattern = literalPattern,
            rid = " \"mcp-search-wrapped\" "
        }));
        var scalarAndContent = await PaoProgram.CallUnifiedMcpToolAsync(scalarAndContentJson.RootElement, capture, CancellationToken.None);
        Is(callsBeforeMalformedCli + 3, capture.Calls, "MCP rejected a wrapped typed path and identifier");
        Is(Path.GetFullPath(fixtureRoot), capture.Last!.Arg["src"], "MCP did not normalize a wrapped typed path");
        Is("mcp-search-wrapped", capture.Last.Rid, "MCP did not normalize a wrapped typed search identifier");
        Is(literalPattern, capture.Last.Arg["pattern"], "MCP altered content-bearing search input");
    }

    private static OperationRequest Request(
        string action,
        string rid,
        string? root = null,
        string? expect = null,
        int? pid = null,
        string? start = null)
    {
        var arg = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["op"] = action,
            ["service"] = Service
        };
        if (action != "status") arg["tmo"] = "30";
        if (expect is not null) arg["expect"] = expect;
        if (pid is not null) arg["pid"] = pid.Value.ToString(CultureInfo.InvariantCulture);
        if (start is not null) arg["start"] = start;
        return new("service", arg, rid, Root: root);
    }

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Is<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}");
    }

    private static int Integer(object? value) => value switch
    {
        JsonElement element when element.ValueKind == JsonValueKind.Number => element.GetInt32(),
        int number => number,
        _ => Convert.ToInt32(value, CultureInfo.InvariantCulture)
    };

    private sealed class FakeServiceLifecycle(WindowsServiceSnapshot initial) : IWindowsServiceLifecycle
    {
        public WindowsServiceSnapshot Current { get; private set; } = initial;
        public int Calls { get; private set; }
        public bool FailRestartAfterStop { get; set; }

        public Task<WindowsServiceLifecycleResult> ExecuteAsync(
            string service,
            WindowsServiceLifecycleAction action,
            string? expectedState,
            int? expectedProcessId,
            string? expectedProcessStart,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var previous = Current;
            if (service != Service)
                return Task.FromResult(new WindowsServiceLifecycleResult(false, action, previous, Current, Error: "service_not_admitted"));
            if (action == WindowsServiceLifecycleAction.Status)
                return Task.FromResult(new WindowsServiceLifecycleResult(true, action, previous, Current));
            if (Current.State != expectedState)
                return Task.FromResult(new WindowsServiceLifecycleResult(false, action, previous, Current, Error: "precondition"));
            if (expectedState == "running" &&
                (Current.ProcessId != expectedProcessId || Current.ProcessStart != expectedProcessStart))
                return Task.FromResult(new WindowsServiceLifecycleResult(false, action, previous, Current, Error: "pid_reused"));

            if (action is WindowsServiceLifecycleAction.Stop or WindowsServiceLifecycleAction.Restart)
                Current = new(Service, "stopped");
            if (action == WindowsServiceLifecycleAction.Restart && FailRestartAfterStop)
                return Task.FromResult(new WindowsServiceLifecycleResult(false, action, previous, Current, true, "recovery_required", true));
            if (action is WindowsServiceLifecycleAction.Start or WindowsServiceLifecycleAction.Restart)
                Current = new(Service, "running", 5252, "2026-09-29T13:00:00.0000000Z");
            return Task.FromResult(new WindowsServiceLifecycleResult(true, action, previous, Current, true));
        }
    }

    private sealed class ServiceCaptureDispatcher : IOperationDispatcher
    {
        public OperationRequest? Last { get; private set; }
        public int Calls { get; private set; }
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = request;
            return Task.FromResult(new OperationReceipt(true, "fixture-jid", request.Rid ?? "fixture-rid", "done", OperationNames.Public(request)));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ServiceTestRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pao-service-test-" + Guid.NewGuid().ToString("N"));
        public ServiceTestRoot() => System.IO.Directory.CreateDirectory(Path);
        public string Directory(string name) => System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Path, name)).FullName;
        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
