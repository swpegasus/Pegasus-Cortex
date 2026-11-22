using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class DeferredSubmissionClientTests
{
    // Parser/projection fixtures: no installed queue, executable, database,
    // service, root-code enrollment, or external program is used here.
    internal static async Task RunAsync()
    {
        var receipt = new OperationReceipt(true, "accepted-jid", "accepted-rid", "queued", "run");
        await using var dispatcher = new CaptureDispatcher(() => receipt);
        var originalArguments = new[] { "a b", "value=Ω", "\"quoted\"", "literal\\u0002" };
        var accepted = await CallAsync(new { op = "run", cfg = "fixture", arg = originalArguments, wait = false, output = "agent" });
        Check(dispatcher.Last!.Arg["wait"] == "false" && !dispatcher.Last.Arg.ContainsKey("output"),
            "Deferred completion was not a canonical typed request field.");
        for (var index = 0; index < originalArguments.Length; index++)
            Check(dispatcher.Last.Arg["arg" + index] == originalArguments[index], "Deferred parsing changed a command argument.");
        Check(JsonNode.Parse(Text(accepted))!["state"]!.GetValue<string>() == "queued" &&
            JsonNode.Parse(Text(accepted))!["jid"]!.GetValue<string>() == "accepted-jid" &&
            !accepted.ContainsKey("structuredContent") && !accepted["isError"]!.GetValue<bool>(),
            "Accepted work was presented as completed work.");

        receipt = receipt with { State = "running", Data = new Dictionary<string, object?> { ["seq"] = 12L } };
        var running = await CallAsync(new { op = "run", cfg = "fixture", wait = false, output = "agent" });
        Check(JsonNode.Parse(Text(running))!["state"]!.GetValue<string>() == "running" &&
            JsonNode.Parse(Text(running))!["jid"]!.GetValue<string>() == "accepted-jid" &&
            !Text(running).Contains("log_available", StringComparison.Ordinal) &&
            !Text(running).Contains("exit", StringComparison.Ordinal), "Admission manufactured process result facts.");
        var structured = await CallAsync(new { op = "run", cfg = "fixture", wait = false });
        Check(structured["structuredContent"]!["state"]!.GetValue<string>() == "running" &&
            structured["structuredContent"]!["rid"]!.GetValue<string>() == "accepted-rid", "Legacy receipt lost accepted state or RID.");

        receipt = receipt with { State = "done", Data = null };
        await CallAsync(new { op = "run", cfg = "fixture" });
        Check(!dispatcher.Last!.Arg.ContainsKey("wait"), "Omitted completion mode changed the synchronous request.");
        var completed = await CallAsync(new { op = "run", cfg = "fixture", wait = true, output = "agent" });
        Check(dispatcher.Last!.Arg["wait"] == "true" && Text(completed) == "Completed.",
            "Explicit synchronous completion changed existing success projection.");

        const string exactBody = "objective\r\n\t雪 Ω\n```csharp\nvalue = \"🦄\";\n```\u0002";
        foreach (var arguments in new object[]
        {
            new { op = "dotnet_build", src = "Fixture.csproj", wait = false },
            new { op = "pwsh", src = "fixture.ps1", wait = false },
            new { op = "cp", src = "source.txt", dst = "copy.txt", wait = false },
            new { op = "wr", dst = "body.txt", text = exactBody, wait = false },
            new { op = "encoding_convert", src = "source.txt", dst = "encoded.txt", enc = "utf8", wait = false },
            new { op = "zip_create", src = "source", dst = "fixture.zip", wait = false },
            new { op = "7z_extract", src = "fixture.7z", dst = "expanded", wait = false },
            new { op = "nsis_validate", src = "fixture.nsi", wait = false },
            new { op = "feedback_submit", kind = "performance", area = "fixture", summary = "bounded fixture", wait = false },
            new { op = "ctx_add", name = "fixture", src = Environment.CurrentDirectory, wait = false }
        })
        {
            var count = dispatcher.Count;
            await CallAsync(arguments);
            Check(dispatcher.Count == count + 1 && dispatcher.Last!.Arg["wait"] == "false" &&
                AgentOpsEngine.SupportsDeferredCompletion(dispatcher.Last), "Queueable selector rejected deferred completion: " +
                JsonSerializer.SerializeToElement(arguments).GetProperty("op").GetString());
            if (dispatcher.Last!.Op == "wr") Check(dispatcher.Last.Body == exactBody, "Deferred body did not preserve exact content.");
        }

        foreach (var arguments in new object[]
        {
            new { op = "run", cfg = "fixture", wait = "false" },
            new { op = "run", cfg = "fixture", wait = 0 },
            new { op = "run", cfg = "fixture", wait = (bool?)null },
            new { op = "stat", src = "source.txt", wait = false },
            new { op = "stat", src = "source.txt", wait = true },
            new { op = "app_start", cfg = "fixture", name = "instance", wait = false },
            new { op = "ui_status", wait = false },
            new { op = "service_start", service = "PegasusCerebral", expect = "stopped", tmo = 5, root = "ABCD", wait = false },
            new { op = "mkdir", dst = "root-target", root = "ABCD", wait = false },
            new { op = "job_retry", jid = "fixture-jid", wait = false }
        }) await RejectAsync(JsonSerializer.SerializeToElement(arguments));
        using (var duplicate = JsonDocument.Parse("{\"op\":\"run\",\"cfg\":\"fixture\",\"wait\":false,\"wait\":true}"))
            await RejectAsync(duplicate.RootElement);

        using var cliOutput = new StringWriter();
        using var cliError = new StringWriter();
        Check(await PaoProgram.RunCliAsync(["--json", "run", "fixture", "wait=False"], dispatcher,
            cliOutput, cliError, CancellationToken.None) == 0 && dispatcher.Last!.Arg["wait"] == "false",
            "CLI completion mode did not normalize to the canonical boolean.");
        Check(JsonNode.Parse(cliOutput.ToString())!["state"]!.GetValue<string>() == "done",
            "CLI JSON changed the retained structured receipt.");
        foreach (var arguments in new string[][]
        {
            ["run", "fixture", "wait=0"], ["run", "fixture", "wait=oops"],
            ["run", "fixture", "wait=false", "wait=true"], ["stat", "source.txt", "wait=false"],
            ["mkdir", "root-target", "root=ABCD", "wait=false"]
        })
        {
            var count = dispatcher.Count;
            Check(await PaoProgram.RunCliAsync(arguments, dispatcher, cliOutput, cliError, CancellationToken.None) == 2 &&
                dispatcher.Count == count, "Invalid CLI completion mode reached the dispatcher.");
        }

        var schema = PaoProgram.UnifiedMcpToolDefinition()["inputSchema"]!["properties"]!["wait"]!;
        Check(schema["type"]!.GetValue<string>() == "boolean" && schema["default"]!.GetValue<bool>(),
            "Public schema does not advertise typed synchronous-default completion.");
        var help = await CallAsync(new { op = "help", name = "dotnet_build" });
        var helpData = help["structuredContent"]!["data"]!;
        Check(helpData["allowed"]!.AsArray().Any(value => value!.GetValue<string>() == "wait") &&
            helpData["constraints"]!.GetValue<string>().Contains("not completion", StringComparison.Ordinal),
            "Queueable help omitted the accepted-job contract.");
        var readHelp = await CallAsync(new { op = "help", name = "stat" });
        Check(!readHelp["structuredContent"]!["data"]!["allowed"]!.AsArray()
            .Any(value => value!.GetValue<string>() == "wait"), "Read-only help advertised deferred submission.");

        receipt = receipt with { State = "queued" };
        using var reader = new McpLineReader();
        using var writer = new McpCaptureWriter();
        using var stderr = new StringWriter();
        var server = PaoProgram.RunMcpAsync(dispatcher, reader, writer, stderr, CancellationToken.None);
        reader.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new
        { protocolVersion = "2026-07-28", capabilities = new { }, clientInfo = new { name = "deferred-fixture", version = "1" } } }));
        reader.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized", @params = new { } }));
        reader.Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new
        { name = "pao", arguments = new { op = "run", cfg = "fixture", wait = false, output = "agent" } } }));
        string wire;
        try { wire = await writer.Response.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { reader.Complete(); }
        Check(await server == 0 && string.IsNullOrWhiteSpace(stderr.ToString()) &&
            JsonNode.Parse(Text(JsonNode.Parse(wire)!["result"]!.AsObject()))!["state"]!.GetValue<string>() == "queued" &&
            JsonNode.Parse(Text(JsonNode.Parse(wire)!["result"]!.AsObject()))!["jid"]!.GetValue<string>() == "accepted-jid",
            "Standalone and embedded accepted projections diverged.");

        ValueTask<JsonObject> CallAsync(object arguments) => PaoProgram.CallUnifiedMcpToolAsync(
            JsonSerializer.SerializeToElement(arguments), dispatcher, CancellationToken.None);
        async Task RejectAsync(JsonElement arguments)
        {
            var count = dispatcher.Count;
            var result = await PaoProgram.CallUnifiedMcpToolAsync(arguments, dispatcher, CancellationToken.None);
            Check(dispatcher.Count == count && result["isError"]!.GetValue<bool>(),
                "Invalid or unsupported completion mode reached the dispatcher.");
        }
    }

    internal static async Task McpBoundedCallsAsync()
    {
        // In-process protocol fixtures, not live service/host acceptance. Every
        // response is consumed rather than collected into a second history.
        await using (var dispatcher = new CaptureDispatcher(() =>
            new OperationReceipt(true, "fixture-jid", "fixture-rid", "done", "run")))
        using (var reader = new McpLineReader())
        using (var writer = new ResponseWriter())
        using (var stderr = new StringWriter())
        {
            var server = PaoProgram.RunMcpAsync(dispatcher, reader, writer, stderr, CancellationToken.None);
            try
            {
                await InitializeAsync(reader, writer);
                for (var index = 0; index < 1024; index++)
                {
                    var id = index + 2;
                    SendCall(reader, id);
                    var response = await writer.ReadAsync(id);
                    Check(response.TryGetProperty("result", out _), "Completed calls retained or exhausted an active slot.");
                }
                Check(dispatcher.Count == 1024, "Sequential calls did not dispatch exactly once.");
            }
            finally
            {
                reader.Complete();
                Check(await server.WaitAsync(TimeSpan.FromSeconds(5)) == 0, "Sequential MCP EOF did not finish cleanly.");
            }
            Check(string.IsNullOrWhiteSpace(stderr.ToString()), "Sequential MCP emitted an internal diagnostic.");
        }

        await using (var dispatcher = new GatedDispatcher())
        using (var reader = new McpLineReader())
        using (var writer = new ResponseWriter())
        using (var stderr = new StringWriter())
        {
            var server = PaoProgram.RunMcpAsync(dispatcher, reader, writer, stderr, CancellationToken.None);
            try
            {
                await InitializeAsync(reader, writer);
                for (var index = 0; index < 32; index++) SendCall(reader, index + 100);
                await dispatcher.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                SendCall(reader, 132);
                var rejected = await writer.ReadAsync(132);
                Check(rejected.GetProperty("error").GetProperty("code").GetInt32() == -32001 &&
                    rejected.GetProperty("error").GetProperty("message").GetString()!.Contains("32", StringComparison.Ordinal) &&
                    dispatcher.Count == 32, "Saturation reached the dispatcher or lacked a bounded protocol remedy.");
                Send(reader, new { jsonrpc = "2.0", id = 133, method = "ping", @params = new { } });
                Check((await writer.ReadAsync(133)).TryGetProperty("result", out _), "Saturation blocked non-tool traffic.");
                Send(reader, new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = 100 } });
                await dispatcher.FirstCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Send(reader, new { jsonrpc = "2.0", id = 134, method = "ping", @params = new { } });
                Check((await writer.ReadAsync(134)).TryGetProperty("result", out _), "Cancellation blocked the protocol reader.");
            }
            finally
            {
                reader.Complete();
                Check(await server.WaitAsync(TimeSpan.FromSeconds(5)) == 0, "MCP EOF failed to drain active cancellations.");
            }
            Check(dispatcher.Count == 32 && dispatcher.Canceled == 32 && dispatcher.Active == 0 &&
                string.IsNullOrWhiteSpace(stderr.ToString()), "EOF retained active work or emitted stale canceled results.");
        }

        static async Task InitializeAsync(McpLineReader reader, ResponseWriter writer)
        {
            Send(reader, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new
            { protocolVersion = "2026-07-28", capabilities = new { }, clientInfo = new { name = "bounded-client-fixture", version = "1" } } });
            Check((await writer.ReadAsync(1)).TryGetProperty("result", out _), "Fixture initialization failed.");
            Send(reader, new { jsonrpc = "2.0", method = "notifications/initialized" });
        }
        static void SendCall(McpLineReader reader, int id) => Send(reader, new
        {
            jsonrpc = "2.0", id, method = "tools/call", @params = new
            { name = "pao", arguments = new { op = "run", cfg = "fixture", rid = "bounded-" + id, output = "agent" } }
        });
        static void Send(McpLineReader reader, object request) => reader.Send(JsonSerializer.Serialize(request));
    }

    private static string Text(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CaptureDispatcher(Func<OperationReceipt> receipt) : IOperationDispatcher
    {
        internal OperationRequest? Last { get; private set; }
        internal int Count { get; private set; }
        public Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Last = request;
            Count++;
            return Task.FromResult(receipt());
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ResponseWriter : TextWriter
    {
        private readonly Channel<string> _responses = Channel.CreateBounded<string>(4);
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override Task WriteLineAsync(string? value) => _responses.Writer.WriteAsync(value ?? string.Empty).AsTask();
        internal async Task<JsonElement> ReadAsync(int expectedId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = JsonDocument.Parse(await _responses.Reader.ReadAsync(timeout.Token));
            Check(response.RootElement.GetProperty("id").GetInt32() == expectedId, "Unexpected or stale protocol response.");
            return response.RootElement.Clone();
        }
    }

    private sealed class GatedDispatcher : IOperationDispatcher
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource AllStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FirstCanceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count, _active, _canceled;
        internal int Count => Volatile.Read(ref _count);
        internal int Active => Volatile.Read(ref _active);
        internal int Canceled => Volatile.Read(ref _canceled);
        public async Task<OperationReceipt> DispatchAsync(OperationRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _active);
            if (Interlocked.Increment(ref _count) == 32) AllStarted.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("Gated fixture unexpectedly released without cancellation.");
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _canceled);
                FirstCanceled.TrySetResult();
                throw;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
