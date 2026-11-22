using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Collections.Concurrent;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Cli;

Console.InputEncoding = new System.Text.UTF8Encoding(false);
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
return await PaoProgram.RunAsync(args);

public static class PaoProgram
{
    public const string LegacyProtocolVersion = "2025-11-25";
    private static readonly HashSet<string> SupportedProtocolVersions = new(StringComparer.Ordinal)
    {
        "2024-11-05",
        "2025-03-26",
        "2025-06-18",
        LegacyProtocolVersion,
        "2026-07-28"
    };
    private const string Instructions = "Pegasus AgentOps executes one fixed typed local Windows operation. Send only the selected operation's fields. Use output=agent for result-only success, bounded useful command output, numbered source text, and actionable failure evidence. Accepted or running work returns its jid; inspect it instead of replaying. Full receipts remain available with output=structured or CLI --json; omitted output preserves the legacy structured contract. Terminal state stays in owner-host memory and never reruns on read or reconnect. Discover exact fields, bounds and path/collision rules with pao help <operation>. CLI uses separate key=value arguments. Never put secrets in arguments. Root work requires an explicit 4-character code and the optional privileged service.";
    private const string DeferredCompletionInstructions = " Queueable User operations may use wait=false to return an accepted job, not completed work; omit wait or use true for the synchronous result. Inspect that jid with job_stat, job_wait, or job_log. Explicit same-owner service UAC helpers support wait; Root, app, UI, and other service operations retain their separate completion contracts.";
    private const int MaximumMcpToolCalls = 32;
    private sealed class ActiveMcpCall(CancellationTokenSource cancellation)
    {
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Cancel()
        {
            // A completion may dispose its source after a notification obtained
            // this active entry. A terminal call no longer needs cancellation.
            try { Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
    // UTF-8 JSON transport, never HTML/script embedding. Keep required JSON escapes
    // while avoiding HTML-oriented escapes for ordinary punctuation and Unicode.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var defaultWs = GetOption(args, "--ws");
        await using IOperationDispatcher dispatcher = new RoutingDispatcher();
        if (args.Length > 0 && args[0] == "mcp") return await RunMcpAsync(dispatcher, Console.In, Console.Out, Console.Error, CancellationToken.None, defaultWs, Environment.CurrentDirectory);
        return await RunCliAsync(args, dispatcher, Console.Out, Console.Error, CancellationToken.None,Console.In);
    }

    public static JsonObject UnifiedMcpToolDefinition()
    {
        var node = JsonSerializer.SerializeToNode(UnifiedToolCatalog.All[0], Json);
        return node as JsonObject ??
            throw new InvalidOperationException("The PAO MCP tool definition is malformed.");
    }

    /// <summary>Pure advertised contract metadata for a selected operation.
    /// It never connects, probes availability, constructs an engine or executes work.</summary>
    public static JsonObject? UnifiedMcpOperationHelp(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !UnifiedToolCatalog.TryHelp(operation, out var help)) return null;
        var result = JsonSerializer.SerializeToNode(help, Json) as JsonObject
            ?? throw new InvalidOperationException("The selected PAO help metadata is malformed.");
        var allowed = result["allowed"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        var required = result["required"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        var rootRequired = required.Contains("root", StringComparer.Ordinal);
        var selectedDefinition = UnifiedToolCatalog.OperationDefinition(operation);
        if (operation == "proc_threads") result["description"] = "Observe bounded Windows thread identities for one exact process generation. This read-only operation does not suspend, terminate or control threads.";
        if (result["description"] is null) result["description"] = selectedDefinition?["description"]?.GetValue<string>() ?? operation.Replace('_', ' ');
        result["executionRoute"] = operation is "help" or "admin_validate"
            ? "Local contract discovery or validation; no host execution."
            : operation.StartsWith("certificate_", StringComparison.Ordinal) &&
                CertificateOperationContract.RequiresOwnerElevation(operation["certificate_".Length..])
                ? "Signed-in owner host with one explicit same-owner UAC approval for a fixed LocalMachine certificate action; no service or persistent administrator cache."
            : operation == "job_retry" ? "Optional LocalSystem service with explicit root authorization; original System jobs only."
            : rootRequired ? "Optional LocalSystem service with explicit root authorization."
            : allowed.Contains("root", StringComparer.Ordinal)
                ? "Signed-in owner host; explicit root authorization selects the optional LocalSystem service."
            : operation == "windows_network_resolve"
                ? "Signed-in ordinary owner host; active DNS query with original-work drain, no System/UAC, durable replay or identity fallback."
                : "Signed-in ordinary owner host; no automatic service or elevation fallback.";
        result["availability"] = "unobserved";
        // These are the advertised MCP field definitions. Selector-specific
        // constraints remain alongside them; this is not an alternative validator.
        var advertised = UnifiedMcpToolDefinition()["inputSchema"]!["properties"]!.AsObject();
        var specific = selectedDefinition?["inputSchema"]?["properties"] as JsonObject;
        var properties = new JsonObject();
        foreach (var field in allowed)
            properties[field] = specific?[field]?.DeepClone() ?? advertised[field]?.DeepClone()
                ?? new JsonObject { ["description"] = "See operation-specific constraints." };
        properties["op"] = new JsonObject { ["type"] = "string", ["const"] = operation };
        if (operation == "proc_threads" && properties["max"] is JsonObject threadMaximum)
            threadMaximum["maximum"] = 256;
        if (operation == "windows_network_resolve" && properties["text"] is JsonObject resolutionBody)
            resolutionBody["maxLength"] = WindowsNetworkResolutionContract.MaximumBodyBytes;
        result["inputSchema"] = new JsonObject
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = new JsonArray(required.Prepend("op").Distinct(StringComparer.Ordinal)
                .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["additionalProperties"] = false
        };
        return result;
    }

    public static async ValueTask<JsonObject> CallUnifiedMcpToolAsync(
        JsonElement? arguments,
        IOperationDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? defaultWs = null,
        string? defaultCwd = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var agentOutput = false;
        if (arguments is null ||
            !UnifiedToolCatalog.TryParseArguments(arguments.Value, out var op, out var arg, out var body, out agentOutput))
        {
            return UnifiedToolError(
                "pao_arguments_invalid",
                arguments is null ? "Invalid params: field 'op' is required." : InvalidParamsMessage(arguments.Value), agentOutput);
        }

        OperationRequest request;
        try
        {
            var effectiveWs = arg.GetValueOrDefault("ws") ?? ApplicationWorkspaceDefault(op, arg, defaultWs);
            request = MakeRequest(op, arg, body) with { Ws = effectiveWs };
            if (effectiveWs is not null && !arg.ContainsKey("cwd"))
                request = request with { Cwd = null };
            request = NormalizeClientRequest(
                request,
                defaultCwd ?? Environment.CurrentDirectory);
        }
        catch (Exception exception) when (
            exception is AgentOpsException or ArgumentException or IOException or NotSupportedException)
        {
            return UnifiedToolError(
                "pao_arguments_invalid",
                InvalidParamsMessage(arguments.Value), agentOutput);
        }

        var receipt = op == "help"
            ? HelpReceipt(request)
            : op=="admin"&&request.Arg.GetValueOrDefault("op")=="validate"?OwnerAdminContract.Validate(request):await dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        return McpOutputProjection.FromReceipt(receipt, agentOutput);
    }

    private static JsonObject UnifiedToolError(string code, string message, bool agentOutput = false)
    {
        var structured = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["operation"] = UnifiedToolCatalog.Name
            }
        };
        return McpOutputProjection.FromError(structured, agentOutput);
    }

    public static async Task<int> RunCliAsync(string[] args, IOperationDispatcher dispatcher, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken,TextReader? stdin=null)
    {
        try
        {
            var tokens = args.ToArray();if (tokens.Length == 0) { await stderr.WriteLineAsync("err=invalid usage=pao_<op>"); return 2; }
            var leading = 0; var arg = new Dictionary<string, string>(StringComparer.Ordinal);var jsonOutput=false;
            while(leading<tokens.Length)
            {
                if(tokens[leading]=="--json")
                {
                    if(jsonOutput)throw new ArgumentException("duplicate");
                    jsonOutput=true;leading++;continue;
                }
                var leadingToken=PaoScalarInput.NormalizeCliToken(tokens[leading]);
                if(leadingToken.StartsWith("root=", StringComparison.Ordinal))
                {
                    Add(arg,"root",leadingToken[5..]);leading++;continue;
                }
                break;
            }
            if (leading >= tokens.Length) throw new ArgumentException("invalid");
            var commandTokens = tokens[leading..];
            if (commandTokens.Length == 1 &&
                (IsHelpAlias(commandTokens[0]) || commandTokens[0] == "help"))
            {
                await stdout.WriteLineAsync(CliOverview());
                return 0;
            }
            if (commandTokens.Length == 2 && commandTokens[0] == "help")
            {
                var helpName = ResolveCliHelpName(commandTokens[1..]);
                if (helpName is null || !UnifiedToolCatalog.TryHelp(helpName, out var help))
                    throw new ArgumentException("invalid");
                await stdout.WriteLineAsync(FormatHumanHelp(help));
                return 0;
            }
            if (commandTokens.Length is 2 or 3 && IsHelpAlias(commandTokens[^1]))
            {
                var helpName = ResolveCliHelpName(commandTokens[..^1]);
                if (helpName is null || !UnifiedToolCatalog.TryHelp(helpName, out var help))
                    throw new ArgumentException("invalid");
                await stdout.WriteLineAsync(FormatHumanHelp(help));
                return 0;
            }
            var op = tokens[leading].StartsWith("pao_", StringComparison.Ordinal) ? tokens[leading][4..] : tokens[leading];if(op=="read-lines")op="read_lines";string? body=null;
            if(op=="ux")
            {
                var translated=UnixCompatibilityParser.Parse(tokens[(leading+1)..]);op=translated.Op;tokens=tokens[..leading].Concat(new[]{op}).Concat(translated.Tokens).ToArray();
            }
            if(op=="text")
            {
                var tail=tokens[(leading+1)..];var positional=tail.Where(x=>!IsAssignment(x)).ToArray();if(positional.Length!=4||positional[0]!="replace")throw new ArgumentException("invalid");op="wr";body=positional[3];var assignments=tail.Where(IsAssignment).ToList();assignments.Add("op=replace");assignments.Add("old="+positional[2]);tokens=tokens[..leading].Concat(new[]{"wr",positional[1]}).Concat(assignments).ToArray();
            }
            var readBody=ParseCliArguments(op, tokens[(leading + 1)..], arg);
            if(readBody){if(op is not("wr" or "edit" or "patch" or "root" or "tool" or "recipe" or "do" or "ui" or "admin" or "terminal" or "registry" or "certificate" or "windows_service" or "installed_program" or "connection_profile" or "windows_device" or "windows_network" or "windows_account" or "windows_task")||body is not null)throw new ArgumentException("invalid");body=await ReadBoundedBodyAsync(stdin??Console.In,cancellationToken);}
            if(body is not null)ValidateBody(body);
            if(op=="registry"&&!RegistryToolCatalog.Valid(arg,body,requireBody:true))throw new ArgumentException("invalid");
            if(op=="certificate"&&!CertificateToolCatalog.Valid(arg,body,requireBody:true))throw new ArgumentException("invalid");
            if(op=="windows_service"&&!WindowsServiceToolCatalog.Valid(arg,body,requireBody:true))throw new ArgumentException("invalid");
            if(op=="installed_program"&&!WindowsInstalledProgramToolCatalog.Valid(arg,body,requireBody:true))throw new ArgumentException("invalid");
            if(op=="windows_task"&&!WindowsTaskToolCatalog.Valid(arg,body,requireBody:true))throw new ArgumentException("invalid");
            if(op=="windows_device"&&!WindowsDeviceToolCatalog.Valid(arg,body,requireBody:true))throw new ArgumentException("invalid");
            if(op=="windows_network"&&!WindowsNetworkToolCatalog.Valid(arg,body,requireBody:true))throw new ArgumentException("invalid");
            if(op=="windows_account"&&!WindowsAccountToolCatalog.Valid(arg,body,requireBody:true))throw new ArgumentException("invalid");
            if(op=="connection_profile"&&!ConnectionProfileToolCatalog.Valid(arg,body,requireBody:true))throw new ArgumentException("invalid");
            var request = MakeRequest(op,arg,body);if(arg.ContainsKey("ws")&&!arg.ContainsKey("cwd"))request=request with{Cwd=null};request=NormalizeClientRequest(request,Environment.CurrentDirectory);var receipt = op=="help"?HelpReceipt(request):op=="admin"&&request.Arg.GetValueOrDefault("op")=="validate"?OwnerAdminContract.Validate(request):await dispatcher.DispatchAsync(request, cancellationToken);await stdout.WriteLineAsync((receipt.Op=="terminal"||receipt.Op.StartsWith("terminal_",StringComparison.Ordinal))?TerminalResultProjection.Serialize(receipt):jsonOutput?JsonSerializer.Serialize(receipt,Json):FormatCompact(receipt, request.Quiet));return receipt.ExitCode;
        }
        catch(UnixCompatibilityException ex){await stdout.WriteLineAsync($"ok=0 err={ex.Code} command={Uri.EscapeDataString(ex.Command)} remedy={Uri.EscapeDataString(ex.Remedy)}");return 2;}
        catch(Exception ex)when(ex is ArgumentException or InvalidOperationException){await stderr.WriteLineAsync("err=invalid");return 2;}
    }

    public static async Task<int> RunMcpAsync(IOperationDispatcher dispatcher, TextReader stdin, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken, string? defaultWs = null, string? defaultCwd = null)
    {
        var inflight = new ConcurrentDictionary<string, ActiveMcpCall>(StringComparer.Ordinal);
        using var writeGate = new SemaphoreSlim(1, 1);
        var initializeAccepted = false;
        var operational = false;
        try
        {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try { line = await stdin.ReadLineAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var hasId = root.TryGetProperty("id", out var id);
                var method = root.GetProperty("method").GetString() ?? string.Empty;
                if (!hasId)
                {
                    if (method == "notifications/cancelled" && root.TryGetProperty("params", out var cancelParams) && cancelParams.TryGetProperty("requestId", out var requestId) && inflight.TryGetValue(requestId.GetRawText(), out var active)) active.Cancel();
                    else if (method == "notifications/initialized" && initializeAccepted) operational = true;
                    continue;
                }
                if (method == "tools/call")
                {
                    if (!operational) { await WriteProtocolAsync(stdout, Error(id, -32002, "Server not initialized"), writeGate, cancellationToken); continue; }
                    var cloned = root.Clone(); var key = id.GetRawText();
                    // Only this reader admits calls; worker completions only remove
                    // them. Never wait for a slot here: cancellation notifications
                    // must remain readable while a caller holds every active slot.
                    if (inflight.Count >= MaximumMcpToolCalls)
                    {
                        await WriteProtocolAsync(stdout, Error(id, -32001,
                            $"Server busy: maximum {MaximumMcpToolCalls} active tool requests. Retry after an active request completes."), writeGate, cancellationToken);
                        continue;
                    }
                    var call = new ActiveMcpCall(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
                    if (!inflight.TryAdd(key, call)) { call.Cancellation.Dispose(); continue; }
                    // Insert its completion before starting work. Completed task
                    // objects are not retained until EOF and no assignment race
                    // can reinsert a call that completed synchronously.
                    _ = Task.Run(() => ExecuteToolAsync(cloned, key, call), CancellationToken.None);
                    continue;
                }
                object response = method switch
                {
                    "initialize" when ValidInitialize(root) => Result(id, new { protocolVersion = NegotiatedProtocolVersion(root), capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = "pegasus-agentops", version = AgentOpsProductInfo.ProductVersion }, instructions = Instructions + DeferredCompletionInstructions }),
                    "initialize" => Error(id, -32602, "Invalid initialize params"),
                    "ping" => Result(id, new { }),
                    "tools/list" when operational => Result(id, new { tools = UnifiedToolCatalog.All }),
                    "tools/list" => Error(id, -32002, "Server not initialized"),
                    _ => Error(id, -32601, "Method not found")
                };
                await WriteProtocolAsync(stdout, response, writeGate, cancellationToken);
                if (method == "initialize" && ValidInitialize(root)) initializeAccepted = true;
            }
            catch (JsonException ex)
            {
                await WriteProtocolAsync(stdout,new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "Parse error" } },writeGate,cancellationToken);
                await stderr.WriteLineAsync($"mcp_diag=parse_error chars={Math.Min(ex.Message.Length, 120)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await WriteProtocolAsync(stdout,new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32603, message = "Internal error" } },writeGate,cancellationToken);
                await stderr.WriteLineAsync($"mcp_diag=internal type={ex.GetType().Name}");
            }
        }
        }
        finally
        {
            // EOF, reader failure and host-token cancellation all own the same
            // active-call drain; no completed history is kept for this purpose.
            foreach (var active in inflight.Values) active.Cancel();
            await Task.WhenAll(inflight.Values.Select(active => active.Completion.Task).ToArray());
        }
        return 0;

        async Task ExecuteToolAsync(JsonElement request, string key, ActiveMcpCall call)
        {
            try
            {
                object? response = null;
                try { response = await CallToolAsync(request.GetProperty("id"), request.GetProperty("params"), dispatcher, call.Cancellation.Token, defaultWs, defaultCwd); }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                {
                    response = Error(request.GetProperty("id"), -32603, "Internal error");
                    await stderr.WriteLineAsync($"mcp_diag=tool type={exception.GetType().Name}");
                }
                if (response is not null) await WriteProtocolAsync(stdout, response, writeGate, CancellationToken.None);
            }
            catch (Exception exception)
            {
                // Observe a failed output stream too; never leave an unobserved
                // fire-and-forget exception or an active slot retained forever.
                try { await stderr.WriteLineAsync($"mcp_diag=output type={exception.GetType().Name}"); } catch { }
            }
            finally
            {
                call.Cancellation.Dispose();
                inflight.TryRemove(key, out _);
                call.Completion.TrySetResult();
            }
        }
    }

    private static bool ValidInitialize(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object) return false;
        return parameters.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(version.GetString()) &&
            parameters.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("clientInfo", out var client) && client.ValueKind == JsonValueKind.Object &&
            client.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
            client.TryGetProperty("version", out var clientVersion) && clientVersion.ValueKind == JsonValueKind.String;
    }

    private static string NegotiatedProtocolVersion(JsonElement root)
    {
        var requested = root.GetProperty("params").GetProperty("protocolVersion").GetString();
        return requested is not null && SupportedProtocolVersions.Contains(requested) ? requested : LegacyProtocolVersion;
    }

    private static async Task WriteProtocolAsync(TextWriter stdout, object response, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { await stdout.WriteLineAsync(JsonSerializer.Serialize(response, Json)); await stdout.FlushAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    private static async Task<object> CallToolAsync(JsonElement id, JsonElement parameters, IOperationDispatcher dispatcher, CancellationToken cancellationToken, string? defaultWs, string? defaultCwd)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String) return Error(id, -32602, "Invalid params");
        var name = nameElement.GetString() ?? string.Empty;
        if (name != UnifiedToolCatalog.Name) return Error(id, -32602, "Unknown tool");
        if (!parameters.TryGetProperty("arguments", out var arguments)) arguments = JsonDocument.Parse("{}").RootElement.Clone();
        if (!UnifiedToolCatalog.TryParseArguments(arguments, out var op, out var arg,out var body, out var agentOutput)) return Error(id, -32602, InvalidParamsMessage(arguments));
        OperationRequest request;try{var effectiveWs=arg.GetValueOrDefault("ws")??ApplicationWorkspaceDefault(op,arg,defaultWs);request=MakeRequest(op,arg,body) with{Ws=effectiveWs};if(effectiveWs is not null&&!arg.ContainsKey("cwd"))request=request with{Cwd=null};request=NormalizeClientRequest(request,defaultCwd??Environment.CurrentDirectory);}
        catch(Exception ex)when(ex is AgentOpsException or ArgumentException or IOException or NotSupportedException){return Error(id,-32602,InvalidParamsMessage(arguments));}
        var receipt = op=="help"?HelpReceipt(request):op=="admin"&&request.Arg.GetValueOrDefault("op")=="validate"?OwnerAdminContract.Validate(request):await dispatcher.DispatchAsync(request, cancellationToken);
        return Result(id, McpOutputProjection.FromReceipt(receipt, agentOutput));
    }

    private static string InvalidParamsMessage(JsonElement arguments)
    {
        if(arguments.ValueKind==JsonValueKind.Object&&arguments.TryGetProperty("output",out var output)&&
            (output.ValueKind!=JsonValueKind.String||output.GetString() is not("agent" or "structured")||arguments.EnumerateObject().Count(property=>property.Name=="output")!=1))
            return "Invalid params: output must be supplied at most once as 'agent' or 'structured'; omit it to retain the legacy structured receipt.";
        if(arguments.ValueKind!=JsonValueKind.Object||!arguments.TryGetProperty("op",out var operation)||operation.ValueKind!=JsonValueKind.String)return "Invalid params: field 'op' must select one advertised operation.";
        var action=operation.GetString()??string.Empty;
        if(action.StartsWith("git_",StringComparison.Ordinal))return "Invalid params: for Git use ws=<registered alias> with cwd omitted, or use one absolute cwd; do not put an alias in cwd.";
        if(action.StartsWith("feedback_",StringComparison.Ordinal))
        {
            var feedbackAllowed=new HashSet<string>(StringComparer.Ordinal){"op","fid","kind","area","summary","notes","status","after","max","rid","detail","output"};
            if(UnifiedToolCatalog.TryGetAllowedFields(action,out var feedbackFields)&&feedbackFields.Contains("wait",StringComparer.Ordinal))feedbackAllowed.Add("wait");
            foreach(var property in arguments.EnumerateObject())if(!feedbackAllowed.Contains(property.Name))return $"Invalid field '{property.Name}' for {action}. Remedy: submit needs kind, area, summary and optional notes; get/set use fid; ls/export accept status, kind, area, after, and max.";
            return $"Invalid params for {action}. Remedy: use the advertised kind/status enums, a single-line summary up to 512 UTF-8 bytes, notes up to 8192 bytes, after >= 0, and max 1..{FeedbackOperations.MaxPageSize}.";
        }
        if(!action.StartsWith("dotnet_",StringComparison.Ordinal))
        {
            if(UnifiedToolCatalog.TryGetAllowedFields(action,out var allowedFields))
            {
                foreach(var property in arguments.EnumerateObject())if(!allowedFields.Contains(property.Name,StringComparer.Ordinal))return $"Invalid field '{property.Name}' for {action}. Remedy: call pao with op=help and name={action} for its exact contract.";
                return $"Invalid params for {action}. Remedy: call pao with op=help and name={action} for exact required fields, types, and bounds.";
            }
            return "Invalid params: select an advertised operation, or call op=help for a known operation.";
        }
        var allowed=new HashSet<string>(StringComparer.Ordinal){"op","src","cfg","ws","cwd","arg","tmo","rid","detail","output","wait"};if(action=="dotnet_publish")allowed.Add("dst");
        foreach(var property in arguments.EnumerateObject())if(!allowed.Contains(property.Name))return $"Invalid field '{property.Name}' for {action}. Remedy: use src=<solution-or-project>, optional cfg, ws or cwd, tmo, arg[], rid, detail, and dst only for dotnet_publish.";
        foreach(var property in arguments.EnumerateObject())
        {
            var valid=property.Name switch
            {
                "op" or "src" or "dst" or "cfg" or "ws" or "cwd" or "rid"=>property.Value.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(property.Value.GetString()),
                "arg"=>property.Value.ValueKind==JsonValueKind.Array&&property.Value.GetArrayLength()<=256&&property.Value.EnumerateArray().All(item=>item.ValueKind==JsonValueKind.String),
                "tmo"=>property.Value.ValueKind==JsonValueKind.Number&&property.Value.TryGetInt32(out var seconds)&&seconds is >=1 and <=3600,
                "detail" or "wait"=>property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _=>true
            };
            if(!valid)return $"Invalid value for '{property.Name}' in {action}. Remedy: src/cfg/ws/cwd/rid are nonempty strings, arg is a string array, tmo is 1..3600 seconds, and detail is boolean.";
        }
        return $"Invalid params for {action}. Remedy: use src=<solution-or-project>, optional cfg, ws or cwd, tmo, arg[], rid, detail, and dst only for dotnet_publish.";
    }

    private static bool IsRootServiceJobObservation(string op, IReadOnlyDictionary<string, string> arg) =>
        op == "job" && arg.ContainsKey("root") && arg.GetValueOrDefault("op") is ("stat" or "wait");

    // Maintenance observes the authenticated host catalog, regardless of the MCP adapter's workspace default.
    private static string? ApplicationWorkspaceDefault(string op, IReadOnlyDictionary<string,string> arg, string? defaultWs)
        => IsRootServiceJobObservation(op,arg)||op is "windows_device" or "windows_network" or "windows_account" or "windows_task"||op=="connection_profile"||op=="installed_program"||op=="windows_service"||op=="registry"||op=="certificate"&&!CertificateOperationContract.IsFileAction(arg.GetValueOrDefault("op"))||op=="proc"&&arg.GetValueOrDefault("op")=="threads"||op=="job"&&arg.GetValueOrDefault("op")=="prune"||op=="terminal"&&arg.GetValueOrDefault("op")!="connect"||op=="admin"||op=="app"&&arg.GetValueOrDefault("op") is "inventory" or "release"?null:defaultWs;

    private static OperationRequest MakeRequest(string op, IReadOnlyDictionary<string, string> arg,string? body=null) => new(op, arg.Where(x=>x.Key is not("ws" or "cwd" or "root" or "quiet" or "detail")).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal),
        arg.GetValueOrDefault("rid") ?? (op=="terminal" ? Guid.NewGuid().ToString("N") : null), arg.GetValueOrDefault("ws"), arg.GetValueOrDefault("cwd") ?? (IsRootServiceJobObservation(op,arg)||op is "windows_device" or "windows_network" or "windows_account" or "windows_task"||op=="connection_profile"||op=="installed_program"||op=="windows_service"||op=="registry"||op=="certificate"&&!CertificateOperationContract.IsFileAction(arg.GetValueOrDefault("op"))||op=="job"&&arg.GetValueOrDefault("op")=="prune" ? null : Environment.CurrentDirectory), arg.GetValueOrDefault("root"),
        Bool(arg, "quiet"), Bool(arg, "detail"),Body:body);

    private static OperationRequest NormalizeClientRequest(OperationRequest request,string baseDirectory)
    {
        var normalizer=new WindowsPathContextNormalizer();var arg=new Dictionary<string,string>(request.Arg,StringComparer.Ordinal);var keys=request.Op switch{"stat" or "ls" or "find" or "search" or "slice" or "count" or "path" or "lines" or "disk" or "rd" or "read_lines" or "sha" or "pwsh"=>new[]{"src"},"nsis" when request.Arg.GetValueOrDefault("op")=="build"=>new[]{"src","dst"},"nsis" when request.Arg.GetValueOrDefault("op")=="validate"=>new[]{"src"},"encoding" when request.Arg.GetValueOrDefault("op")=="inspect"=>new[]{"src"},"encoding"=>new[]{"src","dst"},"diff"=>new[]{"src","dst"},"mkdir" or "wr" or "edit" or "rm"=>new[]{"dst"},"ui" when request.Arg.GetValueOrDefault("op")=="capture"=>new[]{"dst"},"cp" or "mv"=>new[]{"src","dst"},"zip" or "7z" when request.Arg.GetValueOrDefault("op") is "list" or "test"=>new[]{"src"},"zip" or "7z"=>new[]{"src","dst"},_=>Array.Empty<string>()};
        foreach(var key in keys)if(arg.TryGetValue(key,out var value)&&!IsAliasPath(value)&&Path.IsPathFullyQualified(value)){try{arg[key]=normalizer.Normalize(value).CanonicalPath;}catch(AgentOpsException ex)when(ex.Code=="reparse_boundary"){arg[key]=Path.GetFullPath(value);}}
        if(request.Op=="ctx"&&arg.TryGetValue("register",out var registration)){var split=registration.IndexOf(':');if(split>0){var root=registration[(split+1)..];arg["register"]=registration[..(split+1)]+normalizer.Normalize(Path.GetFullPath(root,baseDirectory)).CanonicalPath;}}
        var cwd=request.Cwd is null?null:normalizer.Normalize(Path.GetFullPath(request.Cwd,baseDirectory)).CanonicalPath;return request with{Arg=arg,Cwd=cwd};
    }
    private static bool IsAliasPath(string value){var colon=value.IndexOf(':');return colon>0&&!(colon==1&&value.Length>2&&value[2] is '\\' or '/');}
    private static async Task<string> ReadBoundedBodyAsync(TextReader reader,CancellationToken cancellationToken){var builder=new System.Text.StringBuilder();var buffer=new char[8192];var bytes=0;while(true){var read=await reader.ReadAsync(buffer.AsMemory(),cancellationToken);if(read==0)break;bytes+=System.Text.Encoding.UTF8.GetByteCount(buffer.AsSpan(0,read));if(bytes>OperationRequest.MaxBodyBytes)throw new ArgumentException("body_too_large");builder.Append(buffer,0,read);}return builder.ToString();}

    private static Dictionary<string, string> ParseKeyValues(IEnumerable<string> tokens)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        { var split = token.IndexOf('='); if (split < 1) throw new ArgumentException("invalid"); result[token[..split]] = token[(split + 1)..]; }
        return result;
    }

    private static bool ParseCliArguments(string op, string[] tokens, Dictionary<string, string> result)
    {
        var separator = Array.IndexOf(tokens, "--");
        var regular = separator >= 0 ? tokens[..separator] : tokens;var readBody=regular.Contains("--stdin",StringComparer.Ordinal);regular=regular.Where(x=>x!="--stdin").Select(PaoScalarInput.NormalizeCliToken).ToArray();
        var positional = regular.Where(x => !IsAssignment(x)).Select(x=>x.StartsWith("literal:",StringComparison.Ordinal)?x[8..]:x).ToList();
        foreach (var token in regular.Where(IsAssignment))
        { var split = token.IndexOf('='); Add(result,token[..split],token[(split + 1)..]); }
        var expected=0;
        if(op=="windows_task"&&positional.Count==1&&WindowsTaskOperationContract.IsKnownAction(positional[0]))
        { Add(result,"op",positional[0]); expected=1; }
        else if(op=="windows_device"&&positional.Count==1&&WindowsDeviceOperationContract.IsKnownAction(positional[0]))
        { Add(result,"op",positional[0]); expected=1; }
        else if(op=="windows_account"&&positional.Count==1&&WindowsAccountOperationContract.IsKnownAction(positional[0]))
        { Add(result,"op",positional[0]); expected=1; }
        else if(op=="windows_network"&&positional.Count==1&&WindowsNetworkToolCatalog.IsKnownAction(positional[0]))
        { Add(result,"op",positional[0]); expected=1; }
        else if(op=="connection_profile"&&positional.Count==1&&ConnectionProfileOperationContract.IsKnownAction(positional[0]))
        { Add(result,"op",positional[0]); expected=1; }
        else if(op=="installed_program"&&positional.Count==1&&WindowsInstalledProgramOperationContract.IsKnownAction(positional[0]))
        { Add(result,"op",positional[0]); expected=1; }
        else if(op=="windows_service"&&positional.Count==1&&WindowsServiceOperationContract.IsKnownAction(positional[0]))
        { Add(result,"op",positional[0]); expected=1; }
        else if(op=="certificate"&&positional.Count==1&&CertificateOperationContract.IsKnownAction(positional[0]))
        { Add(result,"op",positional[0]); expected=1; }
        else if(op=="registry"&&positional.Count==1&&RegistryOperationContract.IsKnownAction(positional[0]))
        { Add(result,"op",positional[0]); expected=1; }
        else if(op=="terminal"&&positional.Count==1&&positional[0]=="list")
        { Add(result,"op","list"); expected=1; }
        else if(op=="terminal"&&positional.Count==2&&positional[0]!="list"&&TerminalContract.Operations.Contains(positional[0],StringComparer.Ordinal))
        { Add(result,"op",positional[0]); Add(result,positional[0]=="connect"?"name":"sid",positional[1]); expected=2; }
        else if (op == "ctx" && positional.Count==3 && positional[0] == "add") {Add(result,"register",positional[1]+":"+positional[2]);expected=3;}
        else if(op=="ctx"&&positional.Count==1&&positional[0] is "ls" or "cur"){Add(result,"op",positional[0]);expected=1;}
        else if (op is "cp" or "mv" && positional.Count==2) { Add(result,"src",positional[0]);Add(result,"dst",positional[1]);expected=2; }
        else if (op is "pwsh" or "rd" or "read_lines" or "stat" or "ls" or "sha" or "find" or "search" or "slice" or "count" or "path" or "lines" or "disk" && positional.Count==1){Add(result,"src",positional[0]);expected=1;}
        else if(op=="diff"&&positional.Count==2){Add(result,"src",positional[0]);Add(result,"dst",positional[1]);expected=2;}
        else if(op=="run"&&positional.Count==1){Add(result,"cfg",positional[0]);expected=1;}
        else if(op=="app"&&positional.Count==3&&positional[0]=="start"){Add(result,"op","start");Add(result,"cfg",positional[1]);Add(result,"name",positional[2]);expected=3;}
        else if(op=="app"&&positional.Count==1&&positional[0]=="inventory"){Add(result,"op","inventory");expected=1;}
        else if(op=="app"&&positional.Count==2&&positional[0] is "status" or "stop" or "release"){Add(result,"op",positional[0]);Add(result,positional[0]=="status"?"name":"aid",positional[1]);expected=2;}
        else if(op is "wr" or "rm" && positional.Count==1){Add(result,"dst",positional[0]);expected=1;}
        else if(op=="edit"&&positional.Count==3){Add(result,"dst",positional[0]);if(!int.TryParse(positional[1],System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var first)||!int.TryParse(positional[2],System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var last)||first<1||last<first)throw new ArgumentException("invalid");Add(result,"line",first.ToString(System.Globalization.CultureInfo.InvariantCulture));Add(result,"count",(last-first+1).ToString(System.Globalization.CultureInfo.InvariantCulture));expected=3;}
        else if(op=="patch"&&positional.Count==0)expected=0;
        else if (op == "job" && positional.Count is 1 or 2)
        {
            Add(result,"op",positional[0]);if(positional.Count==2)Add(result,"jid",positional[1]);expected=positional.Count;
        }
        else if(op=="dotnet"&&positional.Count is 1 or 2){Add(result,"op",positional[0]);if(positional.Count==2)Add(result,"src",positional[1]);expected=positional.Count;}
        else if(op=="git"&&positional.Count>=1){Add(result,"op",positional[0]);for(var i=1;i<positional.Count;i++)Add(result,$"arg{i-1}",positional[i]);expected=positional.Count;}
        else if(op=="mkdir"&&positional.Count==1){Add(result,"dst",positional[0]);expected=1;}
        else if(op=="zip"&&positional.Count is 2 or 3){Add(result,"op",positional[0]);Add(result,"src",positional[1]);if(positional.Count==3)Add(result,"dst",positional[2]);expected=positional.Count;}
        else if(op=="proc"&&positional.Count==1&&positional[0] is "ls" or "threads"){Add(result,"op",positional[0]);expected=1;}
        else if(op=="proc"&&positional.Count is 2 or 3){Add(result,"op",positional[0]);Add(result,"pid",positional[1]);if(positional.Count==3)Add(result,"start",positional[2]);expected=positional.Count;}
        else if(op=="service"&&positional.Count==2&&positional[0] is "status" or "start" or "stop" or "restart"){Add(result,"op",positional[0]);Add(result,"service",positional[1]);expected=2;}
        else if(op=="net"&&positional.Count==1&&positional[0]=="list"){Add(result,"op","list");expected=1;}
        else if(op is "root" or "tool" or "recipe"&&positional.Count is 1 or 2){Add(result,"op",positional[0]);if(positional.Count==2)Add(result,op=="root"?"label":"name",positional[1]);expected=positional.Count;}
        else if(op=="admin"&&positional.Count==1){Add(result,"op",positional[0]);expected=1;}
        else if(op=="queue"&&positional.Count==1){Add(result,"op",positional[0]);expected=1;}
        else if(op=="feedback"&&positional.Count==4&&positional[0]=="submit"){Add(result,"op","submit");Add(result,"kind",positional[1]);Add(result,"area",positional[2]);Add(result,"summary",positional[3]);expected=4;}
        else if(op=="feedback"&&positional.Count==1&&positional[0] is "ls" or "export"){Add(result,"op",positional[0]);expected=1;}
        else if(op=="feedback"&&positional.Count==2&&positional[0]=="get"){Add(result,"op","get");Add(result,"fid",positional[1]);expected=2;}
        else if(op=="feedback"&&positional.Count==3&&positional[0]=="set"){Add(result,"op","set");Add(result,"fid",positional[1]);Add(result,"status",positional[2]);expected=3;}
        else if(op=="do"&&positional.Count==1){Add(result,"rec",positional[0]);expected=1;}
        else if(op=="7z"&&positional.Count is 2 or 3){Add(result,"op",positional[0]);Add(result,"src",positional[1]);if(positional.Count==3)Add(result,"dst",positional[2]);expected=positional.Count;}
        else if(op=="nsis"&&positional.Count==1&&positional[0]=="status"){Add(result,"op","status");expected=1;}
        else if(op=="nsis"&&positional.Count is 2 or 3&&positional[0] is "validate" or "build"){Add(result,"op",positional[0]);Add(result,"src",positional[1]);if(positional.Count==3)Add(result,"dst",positional[2]);expected=positional.Count;}
        else if(op=="ui"&&positional.Count==1&&positional[0] is "status" or "find"){Add(result,"op",positional[0]);expected=1;}
        else if(op=="ui"&&positional.Count==4&&positional[0] is "window" or "control" or "activate"){Add(result,"op",positional[0]);Add(result,"hwnd",positional[1]);Add(result,"pid",positional[2]);Add(result,"start",positional[3]);expected=4;}
        else if(op=="ui"&&positional.Count==5&&positional[0] is "focus" or "click" or "set-text"){Add(result,"op",positional[0].Replace('-','_'));Add(result,"hwnd",positional[1]);Add(result,"pid",positional[2]);Add(result,"start",positional[3]);Add(result,"ctrl",positional[4]);expected=5;}
        else if(op=="ui"&&positional.Count==5&&positional[0]=="capture"){Add(result,"op","capture");Add(result,"hwnd",positional[1]);Add(result,"pid",positional[2]);Add(result,"start",positional[3]);Add(result,"dst",positional[4]);expected=5;}
        else if(op=="ui"&&positional.Count==8&&positional[0]=="move"){Add(result,"op","move");Add(result,"hwnd",positional[1]);Add(result,"pid",positional[2]);Add(result,"start",positional[3]);Add(result,"x",positional[4]);Add(result,"y",positional[5]);Add(result,"width",positional[6]);Add(result,"height",positional[7]);expected=8;}
        else if(op=="encoding"&&positional.Count==2&&positional[0]=="inspect"){Add(result,"op","inspect");Add(result,"src",positional[1]);expected=2;}
        else if(op=="encoding"&&positional.Count==3&&positional[0]=="convert"){Add(result,"op","convert");Add(result,"src",positional[1]);Add(result,"dst",positional[2]);expected=3;}
        else if(op=="help"&&positional.Count==1){Add(result,"name",positional[0]);expected=1;}
        else if(positional.Count!=0)throw new ArgumentException("invalid");
        if(expected!=positional.Count)throw new ArgumentException("invalid");
        if (separator >= 0)
        {
            var argv = tokens[(separator + 1)..];
            if (argv.Length > 256) throw new ArgumentException("too_many_args");
            for (var i = 0; i < argv.Length; i++) result[$"arg{i}"] = argv[i];
        }
        var publicName=op=="slice"?"pao_tail":"pao_"+op;if(!ToolCatalog.ValidateDictionary(publicName,result))throw new ArgumentException("invalid");if(op=="wr"&&result.GetValueOrDefault("op","write")=="replace"&&!result.ContainsKey("old"))throw new ArgumentException("invalid");if(op=="edit"&&!readBody)throw new ArgumentException("invalid");if(op=="terminal"&&TerminalContract.HasBody(result.GetValueOrDefault("op")??"")!=readBody)throw new ArgumentException("invalid");if(op=="job"&&result.GetValueOrDefault("op") is not("ls" or "prune")&&!result.ContainsKey("jid"))throw new ArgumentException("invalid");if(op=="root"&&result.GetValueOrDefault("op")=="upsert"&&!readBody)throw new ArgumentException("invalid");if(op is "tool" or "recipe"&&result.GetValueOrDefault("op")=="add"&&!readBody)throw new ArgumentException("invalid");if(op=="admin"&&((result.GetValueOrDefault("op") is "admit" or "run" or "validate" or "reconcile" or "ack" or "end")!=readBody))throw new ArgumentException("invalid");if(op=="ui"&&(result.GetValueOrDefault("op")=="set_text")!=readBody)throw new ArgumentException("invalid");return readBody;
    }

    private static void AdaptTypedArguments(string name,Dictionary<string,string> result)
    {
        if((name is "pao_dotnet" or "pao_git")&&result.Remove("op",out var operation))
        {
            for(var i=255;i>=0;i--)if(result.Remove($"arg{i}",out var value))result[$"arg{i+1}"]=value;
            result["arg0"]=operation;
        }
        if(name=="pao_dotnet"&&result.Remove("cfg",out var configuration))
        {var next=0;while(result.ContainsKey($"arg{next}"))next++;result[$"arg{next}"]="--configuration";result[$"arg{next+1}"]=configuration;}
    }

    private static bool Bool(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value) && (value == "1" || bool.TryParse(value, out var parsed) && parsed);
    private static void Add(Dictionary<string,string> values,string key,string value)
    {
        if(!PaoScalarInput.TryNormalize(key,value,out var normalized)||!values.TryAdd(key,normalized))throw new ArgumentException("invalid");
    }
    private static void ValidateBody(string body){if(System.Text.Encoding.UTF8.GetByteCount(body)>OperationRequest.MaxBodyBytes)throw new ArgumentException("body_too_large");}
    private static bool IsAssignment(string value){if(value.StartsWith("literal:",StringComparison.Ordinal))return false;var split=value.IndexOf('=');return split>0&&PaoScalarInput.IsCliAssignmentField(value[..split]);}
    private static string? GetOption(string[] args, string key)
    {
        var joined = args.FirstOrDefault(x => x.StartsWith(key + "=", StringComparison.Ordinal));
        if (joined is not null) return joined[(key.Length + 1)..];
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    private static object Result(JsonElement id, object result) => new { jsonrpc = "2.0", id = JsonSerializer.Deserialize<object>(id.GetRawText(), Json), result };
    private static object Error(JsonElement id, int code, string message) => new { jsonrpc = "2.0", id = JsonSerializer.Deserialize<object>(id.GetRawText(), Json), error = new { code, message } };

    private static string FormatCompact(OperationReceipt receipt, bool quiet)
    {
        if(receipt.Op=="terminal"||receipt.Op.StartsWith("terminal_",StringComparison.Ordinal)) return TerminalResultProjection.Serialize(receipt);
        if (quiet) return receipt.Ok ? "ok=1" : $"ok=0 err={receipt.Err}";
        // Keep envelope state separate from the owned application's lifecycle state.
        var fields = new List<string> { $"ok={(receipt.Ok ? 1 : 0)}", $"jid={receipt.Jid}", $"rid={receipt.Rid}", $"state={receipt.State}" };if(receipt.Partial)fields.Add("partial=1");
        if (receipt.Err is not null) fields.Add($"err={receipt.Err}");
        if(receipt.Op.StartsWith("app_",StringComparison.Ordinal)&&receipt.Data?.GetValueOrDefault("state") is{} applicationState)fields.Add($"app_state={EscapeValue(applicationState)}");
        if(receipt.Op=="proc_threads"&&receipt.Data?.GetValueOrDefault("result") is{} threadObservation)fields.Add($"result={EscapeValue(threadObservation)}");
        if (receipt.Remedy is not null) fields.Add($"remedy={EscapeValue(receipt.Remedy)}");
        if (receipt.Op.StartsWith("registry_", StringComparison.Ordinal) && receipt.Data is not null)
            foreach (var key in new[] { "registry", "registry_status", "registry_code", "registry_write_invoked", "postcondition_observed", "atomic_compare_and_swap", "native_error" })
                if (receipt.Data.TryGetValue(key, out var value) && value is not null) fields.Add($"{key}={EscapeValue(value)}");
        if (receipt.Op.StartsWith("certificate_", StringComparison.Ordinal) && receipt.Data is not null)
            foreach (var key in new[] { "certificate", "reference", "certificateId", "certificate_status", "certificate_code", "certificate_issued", "published", "private_export_issued", "store_action_issued", "store_scope", "store_kind", "postcondition_observed", "outcome_unknown", "sha_after", "windows_status" })
                if (receipt.Data.TryGetValue(key, out var value) && value is not null) fields.Add($"{key}={EscapeValue(value)}");
        if (receipt.Data is not null) foreach (var key in new[] { "selected", "pruned", "skipped_active", "skipped_invalid", "outcomes_unchanged", "rid_barriers_preserved", "history_pruned", "before", "phase", "epoch", "ordinary_mutations_closed", "blockers", "receipt_sha", "maintenance_phase", "maintenance_closed", "maintenance_epoch", "maintenance_jid", "maintenance_rid", "maintenance_issue", "aid", "issue", "seq", "retry_of", "approval_required", "approval_via", "field", "src", "dst", "pol", "del", "cp", "changed", "skipped", "partial", "sha", "sha_before", "ws", "enc", "enc_before", "bom", "bom_before", "newline", "text", "entries", "matches", "line", "count", "requested", "total", "next", "has_more", "lines", "lines_before", "lines_after", "words", "bytes", "free", "used", "files", "dirs", "processes", "mode", "equal", "expanded_bytes", "tested", "exit", "out", "err_tail", "out_truncated", "err_truncated", "timed_out", "out_tail_truncated", "err_tail_truncated", "listeners", "matched", "truncated", "rec", "jobs", "tray", "queued", "paused", "labels", "codes", "tools", "recipes", "name", "services", "visibility", "bound_reached", "configSha", "previous_configSha", "startup", "previous_startup", "service_code", "phase", "outcome_unknown", "process_identity_available", "service", "service_state", "previous_state", "previous_pid", "previous_start", "version", "path", "probed", "removed", "label", "tool", "action", "fid", "feedback", "fmt", "bundle", "more", "local", "allowed", "required", "constraints", "mcp_example", "cli_example", "cancel_applied", "notes_recorded", "notes_bytes", "product", "product_version", "protocol", "mcp_schema", "manifest", "tools_registered", "recipes_registered", "git_ready", "dotnet_ready", "pwsh_ready", "nsis_ready", "nsis_provider", "available", "provider", "architecture", "interactive_user", "hwnd", "pid", "start", "ctrl", "title", "class_list", "bounds", "action_issued", "verified", "text_bytes", "installer_bytes", "installer_sha", "validated", "log_available" }) if ((!receipt.Op.StartsWith("nsis_",StringComparison.Ordinal)||key is not("out" or "err_tail"))&&receipt.Data.TryGetValue(key, out var value) && value is not null) fields.Add($"{key}={EscapeValue(value)}");
        return string.Join(' ', fields);
    }

    private static bool IsHelpAlias(string value) => value is "/?" or "/help" or "?";

    private static string? ResolveCliHelpName(string[] command)
    {
        if (command.Length is < 1 or > 2) return null;
        var first = command[0].StartsWith("pao_", StringComparison.Ordinal) ? command[0][4..] : command[0];
        first = first.Replace('-', '_');
        if (command.Length == 1)
            return UnifiedToolCatalog.TryHelp(first, out _) ? first : null;
        var second = command[1].Replace('-', '_');
        var combined = first + "_" + second;
        return UnifiedToolCatalog.TryHelp(combined, out _) ? combined : null;
    }

    private static string CliOverview() => string.Join(Environment.NewLine,
        "Pegasus PAO - typed local operations",
        "Usage: pao <command> [arguments] [key=value]",
        "       pao <command> /?",
        "       pao help <operation>",
        "       pao --json <command> ...   (complete machine receipt)",
        "Common: stat, ls, find, search, read-lines, cp, mv, mkdir, git, dotnet, proc, service, app, diag",
        "Examples: pao read-lines src/Program.cs line=1 count=80 ws=project",
        "          pao dotnet build App.slnx cfg=Release ws=project",
        "Help: pao /?   pao read-lines /?   pao dotnet build /?");

    private static string FormatHumanHelp(IReadOnlyDictionary<string,object?> help)
    {
        static string Join(object? value) => value switch
        {
            IEnumerable<string> values => string.Join(", ", values),
            JsonElement { ValueKind: JsonValueKind.Array } json => string.Join(", ", json.EnumerateArray().Select(item => item.GetString())),
            null => "none",
            _ => value.ToString() ?? "none"
        };
        var name = help.GetValueOrDefault("name")?.ToString() ?? "operation";
        return string.Join(Environment.NewLine,
            $"PAO {name}",
            $"Usage: {help.GetValueOrDefault("cli_example")}",
            $"Required: {Join(help.GetValueOrDefault("required"))}",
            $"Allowed: {Join(help.GetValueOrDefault("allowed"))}",
            $"Notes: {help.GetValueOrDefault("constraints")}",
            "Machine output: prefix the command with --json");
    }

    private static OperationReceipt HelpReceipt(OperationRequest request)
    {
        var rid=request.Rid??Guid.NewGuid().ToString("N");
        if(!request.Arg.TryGetValue("name",out var name)||!UnifiedToolCatalog.TryHelp(name,out var data))return new(false,"local",rid,"failed","help","not_found");
        return new(true,"local",rid,"done","help",Data:data);
    }
    private static string EscapeValue(object value){var raw=value switch{string text=>text,JsonElement{ValueKind:JsonValueKind.String} json=>json.GetString()!,JsonElement json=>json.GetRawText(),_=>JsonSerializer.Serialize(value,Json)};raw=BoundUtf8(raw,256*1024);return Uri.EscapeDataString(raw);}
    private static string BoundUtf8(string value,int maxBytes){if(System.Text.Encoding.UTF8.GetByteCount(value)<=maxBytes)return value;var result=new System.Text.StringBuilder();var used=0;foreach(var rune in value.EnumerateRunes()){var count=rune.Utf8SequenceLength;if(used+count>maxBytes)break;result.Append(rune.ToString());used+=count;}return result.ToString();}
}

internal static class UnifiedToolCatalog
{
    public const string Name = "pao";
    private sealed record Route(string Tool, string? SubOp = null, string? Mode = null);
    private static readonly IReadOnlyDictionary<string,Route> Routes = new Dictionary<string,Route>(StringComparer.Ordinal)
    {
        ["ctx_ls"]=new("pao_ctx","ls"), ["ctx_cur"]=new("pao_ctx","cur"), ["ctx_add"]=new("pao_ctx"),
        ["stat"]=new("pao_stat"), ["ls"]=new("pao_ls"), ["find"]=new("pao_find"), ["search"]=new("pao_search"),
        ["head"]=new("pao_tail",Mode:"head"), ["tail"]=new("pao_tail",Mode:"tail"), ["diff"]=new("pao_diff"),
        ["count"]=new("pao_count"), ["realpath"]=new("pao_path","realpath"), ["basename"]=new("pao_path","basename"), ["dirname"]=new("pao_path","dirname"),
        ["sort"]=new("pao_lines","sort"), ["uniq"]=new("pao_lines","uniq"), ["du"]=new("pao_disk","du"), ["df"]=new("pao_disk","df"),
        ["rd"]=new("pao_rd"), ["read_lines"]=new("pao_read_lines"), ["encoding_inspect"]=new("pao_encoding","inspect"), ["encoding_convert"]=new("pao_encoding","convert"),
        ["wr"]=new("pao_wr","write"), ["replace"]=new("pao_wr","replace"), ["edit"]=new("pao_edit"), ["patch"]=new("pao_patch"),
        ["mkdir"]=new("pao_mkdir"), ["cp"]=new("pao_cp"), ["mv"]=new("pao_mv"), ["rm"]=new("pao_rm"), ["sha"]=new("pao_sha"),
        ["zip_create"]=new("pao_zip","create"), ["zip_list"]=new("pao_zip","list"), ["zip_test"]=new("pao_zip","test"), ["zip_extract"]=new("pao_zip","extract"),
        ["7z_list"]=new("pao_7z","list"), ["7z_test"]=new("pao_7z","test"), ["7z_extract"]=new("pao_7z","extract"),
        ["job_prune"]=new("pao_job","prune"),
        ["run"]=new("pao_run"), ["pwsh"]=new("pao_pwsh"),
        ["admin_validate"]=new("pao_admin","validate"), ["admin_admit"]=new("pao_admin","admit"), ["admin_run"]=new("pao_admin","run"), ["admin_stat"]=new("pao_admin","stat"), ["admin_revoke"]=new("pao_admin","revoke"), ["admin_reconcile"]=new("pao_admin","reconcile"),
        ["admin_begin"]=new("pao_admin","begin"), ["admin_end"]=new("pao_admin","end"), ["admin_interval"]=new("pao_admin","interval"), ["admin_ack"]=new("pao_admin","ack"),
        ["terminal_list"]=new("pao_terminal","list"), ["terminal_release"]=new("pao_terminal","release"), ["terminal_input_status"]=new("pao_terminal","input_status"), ["terminal_connect"]=new("pao_terminal","connect"), ["terminal_status"]=new("pao_terminal","status"),
        ["terminal_disconnect"]=new("pao_terminal","disconnect"), ["terminal_reconnect"]=new("pao_terminal","reconnect"),
        ["terminal_close"]=new("pao_terminal","close"), ["terminal_read"]=new("pao_terminal","read"),
        ["terminal_send"]=new("pao_terminal","send"), ["terminal_input"]=new("pao_terminal","input"),
        ["terminal_recall"]=new("pao_terminal","recall"), ["terminal_repeat"]=new("pao_terminal","repeat"),
        ["terminal_resize"]=new("pao_terminal","resize"), ["terminal_interrupt"]=new("pao_terminal","interrupt"), ["terminal_screen"]=new("pao_terminal","screen"),
        ["app_start"]=new("pao_app","start"), ["app_status"]=new("pao_app","status"), ["app_stop"]=new("pao_app","stop"), ["app_inventory"]=new("pao_app","inventory"), ["app_release"]=new("pao_app","release"),
        ["git_status"]=new("pao_git","status"), ["git_diff"]=new("pao_git","diff"), ["git_log"]=new("pao_git","log"), ["git_show"]=new("pao_git","show"), ["git_rev_parse"]=new("pao_git","rev-parse"),
        ["dotnet_build"]=new("pao_dotnet","build"), ["dotnet_test"]=new("pao_dotnet","test"), ["dotnet_restore"]=new("pao_dotnet","restore"), ["dotnet_clean"]=new("pao_dotnet","clean"), ["dotnet_pack"]=new("pao_dotnet","pack"), ["dotnet_publish"]=new("pao_dotnet","publish"),
        ["proc_ls"]=new("pao_proc","ls"), ["proc_inspect"]=new("pao_proc","inspect"), ["proc_wait"]=new("pao_proc","wait"), ["proc_stop"]=new("pao_proc","stop"), ["proc_tree"]=new("pao_proc","tree"), ["proc_threads"]=new("pao_proc","threads"),
        ["service_status"]=new("pao_service","status"), ["service_start"]=new("pao_service","start"), ["service_stop"]=new("pao_service","stop"), ["service_restart"]=new("pao_service","restart"),
        ["net_list"]=new("pao_net","list"), ["job_stat"]=new("pao_job","stat"), ["job_wait"]=new("pao_job","wait"), ["job_ls"]=new("pao_job","ls"), ["job_cancel"]=new("pao_job","cancel"), ["job_retry"]=new("pao_job","retry"), ["job_log"]=new("pao_job","log"),
        ["feedback_submit"]=new("pao_feedback","submit"), ["feedback_ls"]=new("pao_feedback","ls"), ["feedback_get"]=new("pao_feedback","get"), ["feedback_set"]=new("pao_feedback","set"), ["feedback_export"]=new("pao_feedback","export"),
        ["tool_ls"]=new("pao_tool","ls"), ["recipe_ls"]=new("pao_recipe","ls"),
        ["ui_status"]=new("pao_ui","status"), ["ui_find"]=new("pao_ui","find"), ["ui_window"]=new("pao_ui","window"), ["ui_control"]=new("pao_ui","control"),
        ["ui_activate"]=new("pao_ui","activate"), ["ui_move"]=new("pao_ui","move"), ["ui_focus"]=new("pao_ui","focus"), ["ui_click"]=new("pao_ui","click"), ["ui_set_text"]=new("pao_ui","set_text"), ["ui_capture"]=new("pao_ui","capture"),
        ["nsis_status"]=new("pao_nsis","status"), ["nsis_validate"]=new("pao_nsis","validate"), ["nsis_build"]=new("pao_nsis","build"),
        ["do"]=new("pao_do"), ["diag"]=new("pao_diag"), ["help"]=new("pao_help")
    }.Concat(RegistryOperationContract.Actions.Select(action => new KeyValuePair<string,Route>(
        RegistryOperationContract.PublicName(action), new("pao_registry", action))))
        .Concat(CertificateOperationContract.Actions.Select(action => new KeyValuePair<string,Route>(
            CertificateOperationContract.PublicName(action), new("pao_certificate", action))))
        .Concat(WindowsServiceOperationContract.Actions.Select(action => new KeyValuePair<string,Route>(
            WindowsServiceOperationContract.PublicName(action), new("pao_windows_service", action))))
        .Concat(WindowsInstalledProgramOperationContract.Actions.Select(action => new KeyValuePair<string,Route>(
            WindowsInstalledProgramOperationContract.PublicName(action), new("pao_installed_program", action))))
        .Concat(ConnectionProfileOperationContract.Actions.Select(action => new KeyValuePair<string,Route>(
            ConnectionProfileOperationContract.PublicName(action), new("pao_connection_profile", action))))
        .Concat(WindowsTaskOperationContract.Actions.Select(action => new KeyValuePair<string,Route>(
            WindowsTaskOperationContract.PublicName(action), new("pao_windows_task", action))))
        .Concat(WindowsDeviceOperationContract.Actions.Select(action => new KeyValuePair<string,Route>(
            WindowsDeviceOperationContract.PublicName(action), new("pao_windows_device", action))))
        .Concat(WindowsNetworkToolCatalog.Actions.Select(action => new KeyValuePair<string,Route>(
            WindowsNetworkToolCatalog.PublicName(action), new("pao_windows_network", action))))
        .Concat(WindowsAccountOperationContract.Actions.Select(action => new KeyValuePair<string,Route>(
            WindowsAccountOperationContract.PublicName(action), new("pao_windows_account", action))))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    public static bool TryParseArguments(JsonElement arguments, out string operation, out Dictionary<string,string> result, out string? body, out bool agentOutput)
    {
        operation=string.Empty;result=new Dictionary<string,string>(StringComparer.Ordinal);body=null;agentOutput=false;
        if(arguments.ValueKind!=JsonValueKind.Object)return false;
        // Selector ambiguity must be rejected before choosing a route. Otherwise
        // a later duplicate could turn a read-only request into another operation.
        if(arguments.EnumerateObject().Count(property=>property.Name=="op")!=1)return false;
        var outputs=arguments.EnumerateObject().Where(property=>property.Name=="output").ToArray();
        if(outputs.Length>1||outputs.Length==1&&(outputs[0].Value.ValueKind!=JsonValueKind.String||outputs[0].Value.GetString() is not("agent" or "structured")))return false;
        agentOutput=outputs.Length==1&&outputs[0].Value.GetString()=="agent";
        if(!arguments.TryGetProperty("op",out var selector)||selector.ValueKind!=JsonValueKind.String)return false;
        var action=selector.GetString();if(action is null||!Routes.TryGetValue(action,out var route)||!ToolCatalog.Names.TryGetValue(route.Tool,out var resolvedOperation)||resolvedOperation is null)return false;operation=resolvedOperation;
        if((action=="proc_threads"||action.StartsWith("app_",StringComparison.Ordinal)||action.StartsWith("terminal_",StringComparison.Ordinal)||action.StartsWith("registry_",StringComparison.Ordinal)||action.StartsWith("certificate_",StringComparison.Ordinal)||action.StartsWith("windows_service_",StringComparison.Ordinal)||action.StartsWith("installed_program_",StringComparison.Ordinal)||action.StartsWith("connection_profile_",StringComparison.Ordinal)||action.StartsWith("windows_task_",StringComparison.Ordinal)||action.StartsWith("windows_device_",StringComparison.Ordinal)||action.StartsWith("windows_network_",StringComparison.Ordinal)||action.StartsWith("windows_account_",StringComparison.Ordinal)||action is "job_stat" or "job_wait"||arguments.EnumerateObject().Any(property=>property.Name=="wait"))&&arguments.EnumerateObject().GroupBy(property=>property.Name,StringComparer.Ordinal).Any(group=>group.Count()>1))return false;
        var values=new Dictionary<string,JsonElement>(StringComparer.Ordinal);
        // Presentation never enters the operation request, durable job, or idempotency identity.
        foreach(var property in arguments.EnumerateObject())if(property.Name is not("op" or "output"))values[property.Name]=property.Value.Clone();
        if(!TryGetAllowedFields(action,out var publicFields)||arguments.EnumerateObject().Any(property=>!publicFields.Contains(property.Name,StringComparer.Ordinal)))return false;
        if(action=="ctx_add")
        {
            if(!values.Remove("name",out var alias)||alias.ValueKind!=JsonValueKind.String||!values.Remove("src",out var path)||path.ValueKind!=JsonValueKind.String)return false;
            values["register"]=JsonSerializer.SerializeToElement(alias.GetString()+":"+path.GetString());
        }
        if(route.SubOp is not null)values["op"]=JsonSerializer.SerializeToElement(route.SubOp);
        if(route.Mode is not null)values["mode"]=JsonSerializer.SerializeToElement(route.Mode);
        using var document=JsonDocument.Parse(JsonSerializer.Serialize(values));
        return ToolCatalog.TryParseArguments(route.Tool,document.RootElement,out result,out body);
    }

    public static bool TryGetAllowedFields(string action,out string[] fields)
    {
        fields=[];
        if(!Routes.TryGetValue(action,out var route))return false;
        if(route.Tool=="pao_terminal") { fields=TerminalToolCatalog.PublicFields(route.SubOp!); return true; }
        if(route.Tool=="pao_registry") { fields=RegistryToolCatalog.Fields.Append("output").ToArray(); return true; }
        if(route.Tool=="pao_certificate") { fields=CertificateToolCatalog.FieldsFor(route.SubOp!).Append("output").ToArray(); return true; }
        if(route.Tool=="pao_windows_service") { fields=WindowsServiceToolCatalog.FieldsFor(route.SubOp!).Append("output").ToArray(); return true; }
        if(route.Tool=="pao_installed_program") { fields=WindowsInstalledProgramToolCatalog.Fields.Append("output").ToArray(); return true; }
        if(route.Tool=="pao_windows_task") { fields=WindowsTaskToolCatalog.FieldsFor(route.SubOp!).Append("output").ToArray(); return true; }
        if(route.Tool=="pao_windows_device") { fields=WindowsDeviceToolCatalog.Fields.Append("output").ToArray(); return true; }
        if(route.Tool=="pao_windows_network") { fields=WindowsNetworkToolCatalog.Fields.Append("output").ToArray(); return true; }
        if(route.Tool=="pao_windows_account") { fields=WindowsAccountToolCatalog.Fields.Append("output").ToArray(); return true; }
        if(route.Tool=="pao_connection_profile") { fields=ConnectionProfileToolCatalog.FieldsFor(route.SubOp!).Append("output").ToArray(); return true; }
        fields=route.Tool switch
        {
            "pao_ctx" when action=="ctx_add"=>["op","name","src","rid","detail"],
            "pao_ctx"=>["op","ws","cwd","rid","detail"],
            "pao_stat" or "pao_sha"=>["op","src","ws","cwd","rid","root","detail"],
            "pao_ls"=>["op","src","max","ws","cwd","rid","root","detail"],
            "pao_find"=>["op","src","pattern","type","max","ws","cwd","rid","detail"],
            "pao_search"=>["op","src","pattern","recur","ignore_case","fmt","include","max","ws","cwd","rid","detail"],
            "pao_tail"=>["op","src","count","enc","ws","cwd","rid","detail"],
            "pao_read_lines"=>["op","src","line","count","enc","ws","cwd","rid","detail"],
            "pao_encoding" when action=="encoding_inspect"=>["op","src","enc","ws","cwd","rid","detail"],
            "pao_encoding"=>["op","src","dst","enc","sha","ws","cwd","rid","detail"],
            "pao_diff"=>["op","src","dst","max","ws","cwd","rid","detail"],
            "pao_count"=>["op","src","ws","cwd","rid","detail"],
            "pao_path"=>["op","src","ws","cwd","rid","detail"],
            "pao_lines"=>["op","src","ignore_case","ws","cwd","rid","detail"],
            "pao_disk"=>["op","src","ws","cwd","rid","detail"],
            "pao_mkdir"=>["op","dst","ws","cwd","rid","root","detail"],
            "pao_cp" or "pao_mv"=>["op","src","dst","pol","ws","cwd","rid","root","detail"],
            "pao_rd"=>["op","src","enc","ws","cwd","rid","root","detail"],
            "pao_wr" when action=="wr"=>["op","dst","sha","enc","text","ws","cwd","rid","detail"],
            "pao_wr"=>["op","dst","old","count","sha","enc","text","ws","cwd","rid","detail"],
            "pao_edit"=>["op","dst","line","count","sha","enc","text","ws","cwd","rid","detail"],
            "pao_patch"=>["op","sha","enc","text","ws","cwd","rid","detail"],
            "pao_rm"=>["op","dst","permanent","ws","cwd","rid","root","detail"],
            "pao_zip" when action is "zip_list" or "zip_test"=>["op","src","ws","cwd","rid","root","detail"],
            "pao_zip" when action=="zip_create"=>["op","src","dst","ws","cwd","rid","root","detail"],
            "pao_zip"=>["op","src","dst","pol","ws","cwd","rid","root","detail"],
            "pao_7z" when action is "7z_list" or "7z_test"=>["op","src","ws","cwd","rid","detail"],
            "pao_7z"=>["op","src","dst","pol","ws","cwd","rid","detail"],
            "pao_admin" when action=="admin_validate"=>["op","text","rid","detail"],
            "pao_admin" when action=="admin_admit"=>["op","text","root","rid","detail"],
            "pao_admin" when action=="admin_run"=>["op","cfg","sha","text","root","rid","tmo","detail"],
            "pao_admin" when action=="admin_reconcile"=>["op","jid","text","root","rid","detail"],
            "pao_admin" when action=="admin_stat"=>["op","jid","root","rid","tmo","detail"],
            "pao_admin" when action=="admin_begin"=>["op","cfg","sha","root","rid","detail"],
            "pao_admin" when action=="admin_end"=>["op","cfg","sha","epoch","text","root","rid","detail"],
            "pao_admin" when action=="admin_ack"=>["op","cfg","sha","text","root","rid","detail"],
            "pao_admin"=>["op","cfg","root","rid","detail"],
            "pao_run"=>["op","cfg","arg","tmo","cwd","rid","detail"],
            "pao_app" when action=="app_start"=>["op","cfg","name","aid","arg","cwd","ws","rid","detail"],
            "pao_app" when action=="app_status"=>["op","name","rid","detail"],
            "pao_app" when action=="app_inventory"=>["op","max","cursor","rid","detail"],
            "pao_app" when action=="app_release"=>["op","aid","epoch","rid","detail"],
            "pao_app"=>["op","aid","rid","detail"],
            "pao_pwsh"=>["op","src","arg","tmo","ws","cwd","rid","detail"],
            "pao_git"=>["op","arg","tmo","ws","cwd","rid","detail"],
            "pao_dotnet" when action=="dotnet_publish"=>["op","src","dst","cfg","arg","tmo","ws","cwd","rid","detail"],
            "pao_dotnet"=>["op","src","cfg","arg","tmo","ws","cwd","rid","detail"],
            "pao_proc" when action=="proc_ls"=>["op","pattern","max","rid","root","detail"],
            "pao_proc" when action=="proc_inspect"=>["op","pid","rid","root","detail"],
            "pao_proc" when action=="proc_tree"=>["op","pid","max","rid","root","detail"],
            "pao_proc" when action=="proc_threads"=>["op","pid","start","max","rid","detail"],
            "pao_proc"=>["op","pid","start","tmo","rid","root","detail"],
            "pao_service" when action is "service_stop" or "service_restart"=>["op","service","expect","pid","start","tmo","root","rid","detail"],
            "pao_service"=>["op","service","expect","tmo","root","rid","detail"],
            "pao_net"=>["op","port","max","rid","root","detail"],
            "pao_job" when action=="job_ls"=>["op","max","state","before","after","rid","detail"],
            "pao_job" when action=="job_prune"=>["op","mode","before","max","rid","detail"],
            "pao_job" when action=="job_log"=>["op","jid","max","rid","detail"],
            "pao_job" when action is "job_retry" or "job_stat" or "job_wait"=>["op","jid","root","rid","detail"],
            "pao_job"=>["op","jid","rid","detail"],
            "pao_feedback" when action=="feedback_submit"=>["op","kind","area","summary","notes","rid","detail"],
            "pao_feedback" when action=="feedback_get"=>["op","fid","rid","detail"],
            "pao_feedback" when action=="feedback_set"=>["op","fid","status","rid","detail"],
            "pao_feedback"=>["op","kind","area","status","after","max","rid","detail"],
            "pao_tool" or "pao_recipe"=>["op","rid","detail"],
            "pao_ui" when action=="ui_status"=>["op","rid","detail"],
            "pao_ui" when action=="ui_find"=>["op","title","window_text","match","tmo","rid","detail"],
            "pao_ui" when action=="ui_move"=>["op","hwnd","pid","start","x","y","width","height","tmo","rid","detail"],
            "pao_ui" when action=="ui_control"=>["op","hwnd","pid","start","ctrl","control_class","instance","tmo","rid","detail"],
            "pao_ui" when action=="ui_click"=>["op","hwnd","pid","start","ctrl","button","count","tmo","rid","detail"],
            "pao_ui" when action=="ui_set_text"=>["op","hwnd","pid","start","ctrl","tmo","text","rid","detail"],
            "pao_ui" when action=="ui_capture"=>["op","hwnd","pid","start","dst","fmt","ws","cwd","tmo","rid","detail"],
            "pao_nsis" when action=="nsis_status"=>["op","rid","detail"],
            "pao_nsis" when action=="nsis_validate"=>["op","src","ws","cwd","tmo","rid","detail"],
            "pao_nsis"=>["op","src","dst","ws","cwd","tmo","rid","detail"],
            "pao_ui"=>["op","hwnd","pid","start","ctrl","tmo","rid","detail"],
            "pao_do"=>["op","rec","vars","cwd","rid","detail"],
            "pao_diag"=>["op","root","rid","detail"],
            "pao_help"=>["op","name","rid","detail"],
            _=>["op","rid","detail"]
        };
        var selectorArg=new Dictionary<string,string>(StringComparer.Ordinal);
        if(route.SubOp is not null)selectorArg["op"]=route.SubOp;
        if(action=="ctx_add")selectorArg["register"]="context-selector";
        if(AgentOpsEngine.SupportsDeferredCompletion(new OperationRequest(ToolCatalog.Names[route.Tool],selectorArg)))
            fields=[..fields,"wait"];
        fields=[..fields,"output"];
        return true;
    }

    internal static JsonObject? OperationDefinition(string action)
    {
        if (!Routes.TryGetValue(action, out var route)) return null;
        foreach (var definition in ToolCatalog.All)
        {
            var node = JsonSerializer.SerializeToNode(definition) as JsonObject;
            if (node?["name"]?.GetValue<string>() == route.Tool)
                return node;
        }
        return null;
    }

    public static bool TryHelp(string action,out IReadOnlyDictionary<string,object?> data)
    {
        data=new Dictionary<string,object?>();
        if(!Routes.TryGetValue(action,out var route)||!TryGetAllowedFields(action,out var fields))return false;
        if(route.Tool=="pao_terminal") { data=TerminalToolCatalog.Help(action,route.SubOp!,fields); return true; }
        if(route.Tool=="pao_registry") { data=RegistryToolCatalog.Help(action,route.SubOp!,fields); return true; }
        if(route.Tool=="pao_certificate") { data=CertificateToolCatalog.Help(action,route.SubOp!,fields); return true; }
        if(route.Tool=="pao_windows_service") { data=WindowsServiceToolCatalog.Help(action,route.SubOp!,fields); return true; }
        if(route.Tool=="pao_installed_program") { data=WindowsInstalledProgramToolCatalog.Help(action,route.SubOp!,fields); return true; }
        if(route.Tool=="pao_windows_task") { data=WindowsTaskToolCatalog.Help(action,route.SubOp!,fields); return true; }
        if(route.Tool=="pao_windows_device") { data=WindowsDeviceToolCatalog.Help(action,route.SubOp!,fields); return true; }
        if(route.Tool=="pao_windows_network") { data=WindowsNetworkToolCatalog.Help(action,route.SubOp!,fields); return true; }
        if(route.Tool=="pao_windows_account") { data=WindowsAccountToolCatalog.Help(action,route.SubOp!,fields); return true; }
        if(route.Tool=="pao_connection_profile") { data=ConnectionProfileToolCatalog.Help(action,route.SubOp!,fields); return true; }
        var required=action switch
        {
            "ctx_add"=>new[]{"name","src"},
            "cp" or "mv" or "diff"=>new[]{"src","dst"},
            "mkdir" or "rm"=>new[]{"dst"},
            "wr"=>new[]{"dst","text"},
            "edit"=>new[]{"dst","line","count","text"},
            "replace"=>new[]{"dst","old","text"},
            "patch"=>new[]{"text"},
            "read_lines"=>new[]{"src"},
            "encoding_inspect"=>new[]{"src"},
            "encoding_convert"=>new[]{"src","dst","enc"},
            "find" or "head" or "tail" or "count" or "realpath" or "basename" or "dirname" or "sort" or "uniq" or "du" or "df" or "rd" or "stat" or "ls" or "sha"=>new[]{"src"},
            "search"=>new[]{"src","pattern"},
            "zip_create" or "zip_extract" or "7z_extract"=>new[]{"src","dst"},
            "zip_list" or "zip_test" or "7z_list" or "7z_test"=>new[]{"src"},
            "admin_validate"=>new[]{"text"},
            "admin_admit"=>new[]{"root","text"},
            "admin_run"=>new[]{"cfg","sha","root","rid","text"},
            "admin_stat"=>new[]{"jid","root"},
            "admin_revoke"=>new[]{"cfg","root"},
            "admin_reconcile"=>new[]{"jid","root","text"},
            "admin_begin"=>new[]{"cfg","sha","root","rid"},
            "admin_end"=>new[]{"cfg","sha","epoch","text","root","rid"},
            "admin_interval"=>new[]{"cfg","root"},
            "admin_ack"=>new[]{"cfg","sha","root","rid","text"},
            "run"=>new[]{"cfg"},
            "app_start"=>new[]{"cfg","name"},
            "app_status"=>new[]{"name"},
            "app_stop"=>new[]{"aid"},
            "app_inventory"=>Array.Empty<string>(),
            "app_release"=>new[]{"aid","epoch","rid"},
            "dotnet_build" or "dotnet_test" or "dotnet_restore" or "dotnet_clean" or "dotnet_pack" or "dotnet_publish"=>new[]{"src"},
            "pwsh"=>new[]{"src"},
            "proc_inspect" or "proc_tree"=>new[]{"pid"},
            "proc_wait" or "proc_stop" or "proc_threads"=>new[]{"pid","start"},
            "service_status"=>new[]{"service","root"},
            "service_start"=>new[]{"service","expect","tmo","root"},
            "service_stop" or "service_restart"=>new[]{"service","expect","pid","start","tmo","root"},
            "job_stat" or "job_wait" or "job_cancel" or "job_retry" or "job_log"=>new[]{"jid"},
            "job_prune"=>new[]{"mode","before"},
            "feedback_submit"=>new[]{"kind","area","summary"},
            "feedback_get"=>new[]{"fid"},
            "feedback_set"=>new[]{"fid","status"},
            "do"=>new[]{"rec"},
            "ui_find"=>new[]{"title","tmo"},
            "ui_window" or "ui_activate"=>new[]{"hwnd","pid","start","tmo"},
            "ui_control"=>new[]{"hwnd","pid","start","tmo"},
            "ui_move"=>new[]{"hwnd","pid","start","x","y","width","height","tmo"},
            "ui_focus" or "ui_click"=>new[]{"hwnd","pid","start","ctrl","tmo"},
            "ui_set_text"=>new[]{"hwnd","pid","start","ctrl","tmo","text"},
            "ui_capture"=>new[]{"hwnd","pid","start","dst","tmo"},
            "nsis_validate"=>new[]{"src"},
            "nsis_build"=>new[]{"src","dst"},
            "help"=>new[]{"name"},
            _=>Array.Empty<string>()
        };
        var constraints=action switch
        {
            "admin_validate"=>"pure strict v1 invocation validation and normalized SHA-256; no broker, root, launch, filesystem mutation or authorization; use returned sha for the exact admin_run request",
            "admin_admit"=>"one elevated-owner setup admits a reusable, revocable task/tool scope; strict v1 JSON in text/--stdin; explicit acknowledgement of unconfined administrator effects; separate from User tool registration; root is transient; at most 7 days",
            "admin_run"=>"strict pinned v1 invocation JSON in text/--stdin; cfg selects reviewed scope; sha is normalized definition SHA-256; rid must equal expectedRid; matching active maintenanceEpoch from admin_begin is required; same owner SID/session/profile with High administrator token; protected staged code; nonsecret argv only; returns original jid/pid/start/exit; tmo1..60 observes only, never kills or replays; root-process status only",
            "admin_stat"=>"exact original jid; root required; tmo1..60 observes only; unknown remains unknown across broker loss; no process termination or automatic retry",
            "admin_reconcile"=>"elevated owner review of exact original job identity plus nonsecret domain-quiescence evidence SHA; original root must be gone; releases only the fence, never kills, replays or invents an exit code",
            "admin_revoke"=>"revoke the exact scope cfg; blocks future launches without killing or replaying existing jobs",
            "admin_begin"=>"explicit exclusive interval within a separately reviewed reusable scope; cfg and sha pin that scope; preserve intended rid across retries; ordinary mutations are fenced while preparing/active/ending/uncertain; returns exact epoch for the strict admin_run invocation; no job forwarding or cancellation",
            "admin_end"=>"strict v1 handback proof in text/--stdin pins begin IDs, epoch, exact native receipts SHA and domain-quiescence evidence SHA; root exit alone is insufficient; hand back the exact original epoch after administrator effects are proven quiescent; preserve intended end rid across retries; ambiguous outcome stays closed; no process termination, original receipt rewrite, or invented exit",
            "admin_interval"=>"read bounded phase/epoch and original interval identity for the exact reviewed cfg; remains diagnostic without admitting work",
            "admin_ack"=>"elevated-owner acknowledgement separately allowed by reviewed scope; strict v1 nonsecret JSON pins exact store/jid/rid/original receipt SHA and quiescence evidence SHA; live/queued effects cannot be acknowledged; original receipt, state and unknown exit remain unchanged",
            "ls"=>"src is required; max is 1..48; choose ws or one admitted cwd; root is only a four-character System code; the client normalizes letter case and one matched quote wrapper",
            "app_start"=>"signed-in user host only; ws requires a registered workspace alias (use ctx_ls or pao ctx ls), not an absolute path; cwd is the working-directory path; cfg is an admitted registered User tool; name is a portable 1..64-character instance key; exact repeated start returns current state; aid permits explicit restart only of the matching terminal generation; same aid/rid retry must match its successor launch identity; no root, service fallback, shell, output tails, or lifetime tmo; startup acknowledgement is bounded to min(10 seconds, registered timeout); 16 active / 128 retained generations per host",
            "app_inventory"=>"read-only current-host capacity and metadata; max1..8, opaque cursor tied to epoch/catalog revision; no args/environment/bodies; unknown observation is not zero; no launch/termination or durable history row",
            "app_release"=>"explicit exact aid, epoch and RID; only terminal, fully retired/quiescent generations; never stops a job; preserves durable receipts and replay; no automatic eviction",
            "app_status"=>"exact current-host instance name only; returns aid/name/state, root pid/start/exit when known and processes excluding the helper; root_exited_workers_running retains descendants; no output tails",
            "app_stop"=>"aid is the exact owned 32-lowercase-hex application generation; terminates only that job and descendants; repeat terminal stop is idempotent; never adopts a PID or uses service/System fallback",
            "find" or "search" or "diff"=>"max is 1..512; search also requires pattern, recur and ignore_case are booleans, and fmt is literal or regex",
            "proc_ls" or "proc_tree" or "net_list"=>"max is 1..1024; proc_ls pattern is an optional case-insensitive name substring up to 256 UTF-8 bytes",
            "proc_threads"=>"read-only ordinary owner host; exact pid and canonical UTC process start are required; max1..256/default64; returns only handle-verified tid/thread-start identities from one bounded system snapshot; scanned includes other processes, snapshotExhausted is enumeration EOF, not a simultaneous or current total; result/scan limits, races and unavailable identities are explicit; no control, elevation, service, deferred execution or durable history",
            "service_status"=>"root-only LocalSystem status for exact service PegasusCerebral; arbitrary service names, executable paths, and command strings are rejected",
            "service_start"=>"root-only LocalSystem start for exact service PegasusCerebral; requires expect=stopped; tmo is 1..120 seconds",
            "service_stop" or "service_restart"=>"root-only LocalSystem lifecycle for exact service PegasusCerebral; requires expect=running plus the exact current pid and canonical UTC start identity; tmo is 1..120 seconds",
            "feedback_ls" or "feedback_export"=>$"after is zero or greater; max is 1..{FeedbackOperations.MaxPageSize}; ls omits notes while get and export include them",
            "feedback_submit"=>$"kind and area are required; summary is one line up to {FeedbackOperations.MaxSummaryBytes} UTF-8 bytes; notes are optional up to {FeedbackOperations.MaxNotesBytes} bytes and are acknowledged without echoing their body",
            "dotnet_build" or "dotnet_test" or "dotnet_restore" or "dotnet_clean" or "dotnet_pack"=>"src is the required admitted solution or project path; cfg is optional except restore does not accept it; tmo is 1..3600 seconds",
            "dotnet_publish"=>"src is required; optional dst is an admitted exact output directory mapped to --output; cfg is optional; tmo is 1..3600 seconds",
            "tool_ls" or "recipe_ls"=>"read-only owner-scoped discovery; definitions are summarized and no secret environment values are returned",
            "job_stat" or "job_wait"=>"ordinary calls observe owner-host jobs; explicit root selects metadata-only observation of known Windows-service mutation JIDs in the System Engine; unknown and foreign-family JIDs are not_found; no body/replay/retry/control or routed-store fallback; wait cancellation ends observation only",
            "job_ls"=>"max is 1..100; optional state, UTC before, and sequence after filter bounded history metadata; child output and detail are omitted",
            "job_prune"=>"owner host only; mode=details compacts old terminal payloads, mode=discard_unresolved explicitly retires old inactive uncertain history; before is a required UTC cutoff, max is 1..256; request identities remain and old commands are never replayed",
            "job_cancel"=>"queued-unstarted cancellation returns ok=true, state=canceled, cancel_applied=true; cancellation after dispatch begins returns recovery attention",
            "job_log"=>"jid selects one retained receipt; max is 256..65536 UTF-8 bytes and defaults to 16384; returns bounded out/err_tail only when retained, plus exit and truncation facts",
            "ui_status"=>"runs through the authenticated signed-in owner host; reports registration-free provider readiness without paths or secrets",
            "ui_find"=>"authenticated signed-in owner session only; title is bounded; match is exact or contains; optional window_text narrows to exactly one result; tmo is 1..30 seconds",
            "ui_window" or "ui_activate" or "ui_focus" or "ui_click" or "ui_set_text"=>"requires exact HWND, positive PID, canonical UTC start, and tmo 1..30; control actions also require ctrl; stale identity fails closed",
            "ui_control"=>"requires exact window identity and either ctrl, or control_class plus optional one-based instance 1..128; tmo is 1..30 seconds",
            "ui_move"=>"requires exact identity; x/y are -32768..32768; width/height are 1..16384; tmo is 1..30 seconds",
            "ui_capture"=>"captures one exact identity-bound window to an absent admitted .png destination in the signed-in owner session; returns path, dimensions, bytes, and SHA-256 but never image bytes; tmo is 1..30 seconds",
            "nsis_status"=>"reports whether exact canonical makensis.exe is registered and probe-verified as tool name nsis",
            "nsis_validate"=>"runs fixed /SAFEPPO /V4 /WX /NOCONFIG UTF-8 preprocessing, rejects unsafe directives, and returns compact line/byte/hash metadata; bounded diagnostics remain available through job log using the returned jid",
            "nsis_build"=>"prevalidates then compiles one admitted .nsi to one absent exact .exe; returns compact outcome plus jid while bounded compiler diagnostics remain available through job log; no /X, stdin script, arbitrary defines, launch, or passthrough argv; tmo is 1..3600 seconds",
            "read_lines"=>"line is one-based; count is 1..512; source text is capped at 256 KiB; output=agent gives exact numbered text, continuation/total only when more exists, and actual truncation without routine headers or duplicated structuredContent; omitted output or output=structured retains the full legacy receipt including sha/encoding/BOM/newline edit metadata",
            "encoding_inspect"=>"returns no file content; optional enc is only for a known legacy source",
            "encoding_convert"=>"enc is the target encoding; source is auto-recognized; distinct dst must be absent; same src/dst converts atomically; optional sha is the source preimage",
            _=>"send only allowed fields; numeric and boolean values are validated before admission; use tmo in seconds; paths remain workspace-admitted and exact"
        };
        if(fields.Contains("wait",StringComparer.Ordinal))
            constraints+="; wait is boolean and defaults to true; wait=false acknowledges durable admission, not completion; inspect jid with job_stat/job_wait/job_log; root+wait=false is rejected";
        var examples=Example(action);
        data=new Dictionary<string,object?>{{"local",true},{"name",action},{"tool",route.Tool},{"allowed",fields},{"required",required},{"constraints",constraints},{"mcp_output","Use output=agent for result-only compact text. Accepted/running work keeps its jid; failure and partial results keep needed recovery evidence. Small run/pwsh/recipe/Git stdout remains an untrusted string, capped at 4096 UTF-8 bytes with explicit clipping and retained-log availability. Omit output or use output=structured for the complete legacy receipt. CLI output is unchanged."},{"mcp_example",examples.Mcp},{"cli_example",examples.Cli}};
        return true;
    }

    private static (string Mcp,string Cli) Example(string action)=>action switch
    {
        "proc_threads"=>("{\"op\":\"proc_threads\",\"pid\":42,\"start\":\"2026-11-20T18:14:00.0000000Z\",\"max\":64,\"output\":\"agent\"}","pao proc threads pid=42 start=2026-11-20T18:14:00.0000000Z max=64"),
        "app_start"=>("{\"op\":\"app_start\",\"cfg\":\"integrator\",\"name\":\"dev-integrator\",\"ws\":\"project\",\"cwd\":\"C:\\\\work\\\\app\",\"output\":\"agent\"}","pao app start integrator dev-integrator ws=project cwd=C:\\work\\app -- --development"),
        "app_status"=>("{\"op\":\"app_status\",\"name\":\"dev-integrator\",\"output\":\"agent\"}","pao app status dev-integrator"),
        "app_inventory"=>("{\"op\":\"app_inventory\",\"max\":8,\"output\":\"agent\"}","pao app inventory max=8"),
        "app_release"=>("{\"op\":\"app_release\",\"aid\":\"0123456789abcdef0123456789abcdef\",\"epoch\":\"fedcba9876543210fedcba9876543210\",\"rid\":\"release-exact-generation\",\"output\":\"agent\"}","pao app release 0123456789abcdef0123456789abcdef epoch=fedcba9876543210fedcba9876543210 rid=release-exact-generation"),
        "app_stop"=>("{\"op\":\"app_stop\",\"aid\":\"0123456789abcdef0123456789abcdef\",\"output\":\"agent\"}","pao app stop 0123456789abcdef0123456789abcdef"),
        "read_lines"=>("{\"op\":\"read_lines\",\"src\":\"src/Program.cs\",\"line\":120,\"count\":80,\"ws\":\"project\",\"output\":\"agent\"}","pao read-lines src/Program.cs line=120 count=80 ws=project"),
        "encoding_inspect"=>("{\"op\":\"encoding_inspect\",\"src\":\"data.txt\",\"ws\":\"project\"}","pao encoding inspect data.txt ws=project"),
        "encoding_convert"=>("{\"op\":\"encoding_convert\",\"src\":\"data.txt\",\"dst\":\"data.utf8.txt\",\"enc\":\"utf8\",\"ws\":\"project\"}","pao encoding convert data.txt data.utf8.txt enc=utf8 ws=project"),
        "git_status"=>("{\"op\":\"git_status\",\"ws\":\"project\"}","pao git status ws=project"),
        "dotnet_build"=>("{\"op\":\"dotnet_build\",\"src\":\"App.slnx\",\"cfg\":\"Release\",\"ws\":\"project\",\"wait\":false}","pao dotnet build src=App.slnx cfg=Release ws=project wait=false"),
        "dotnet_publish"=>("{\"op\":\"dotnet_publish\",\"src\":\"App.csproj\",\"dst\":\"artifacts/publish\",\"cfg\":\"Release\",\"ws\":\"project\"}","pao dotnet publish src=App.csproj dst=artifacts/publish cfg=Release ws=project"),
        "ls"=>("{\"op\":\"ls\",\"src\":\".\",\"ws\":\"project\",\"max\":48}","pao ls . ws=project max=48"),
        "search"=>("{\"op\":\"search\",\"src\":\".\",\"pattern\":\"TODO\",\"recur\":true,\"fmt\":\"literal\",\"ws\":\"project\",\"max\":200}","pao search . pattern=TODO recur=true fmt=literal ws=project max=200"),
        "job_stat" or "job_wait" => (JsonSerializer.Serialize(new { op = action, jid = new string('0', 32) }), $"pao job {action[4..]} {new string('0', 32)}"),
        "job_ls"=>("{\"op\":\"job_ls\",\"max\":50}","pao job ls max=50"),
        "job_prune"=>("{\"op\":\"job_prune\",\"mode\":\"details\",\"before\":\"2020-01-01T00:00:00Z\",\"max\":128}","pao job prune mode=details before=2020-01-01T00:00:00Z max=128"),
        "job_log"=>("{\"op\":\"job_log\",\"jid\":\"0123456789abcdef0123456789abcdef\",\"max\":16384,\"output\":\"agent\"}","pao job log 0123456789abcdef0123456789abcdef max=16384"),
        "service_status"=>("{\"op\":\"service_status\",\"service\":\"PegasusCerebral\",\"root\":\"ABCD\"}","pao service status PegasusCerebral root=ABCD"),
        "service_stop"=>("{\"op\":\"service_stop\",\"service\":\"PegasusCerebral\",\"expect\":\"running\",\"pid\":4242,\"start\":\"2026-09-29T12:34:56.0000000Z\",\"root\":\"ABCD\"}","pao service stop PegasusCerebral expect=running pid=4242 start=2026-09-29T12:34:56.0000000Z root=ABCD"),
        "feedback_submit"=>("{\"op\":\"feedback_submit\",\"kind\":\"bug\",\"area\":\"mcp-schema\",\"summary\":\"Reject message needs the invalid field\"}","pao feedback submit bug mcp-schema \"Reject message needs the invalid field\""),
        "feedback_ls"=>("{\"op\":\"feedback_ls\",\"status\":\"open\",\"max\":25}","pao feedback ls status=open max=25"),
        "diag"=>("{\"op\":\"diag\",\"detail\":true}","pao diag detail=true"),
        "tool_ls"=>("{\"op\":\"tool_ls\"}","pao tool ls"),
        "recipe_ls"=>("{\"op\":\"recipe_ls\"}","pao recipe ls"),
        "ui_status"=>("{\"op\":\"ui_status\"}","pao ui status"),
        "ui_find"=>("{\"op\":\"ui_find\",\"title\":\"AgentOps UI Fixture\",\"match\":\"exact\",\"tmo\":5}","pao ui find title=\"AgentOps UI Fixture\" match=exact tmo=5"),
        "ui_set_text"=>("{\"op\":\"ui_set_text\",\"hwnd\":\"0x0000000000012345\",\"pid\":4242,\"start\":\"2026-08-27T20:15:30.1234567Z\",\"ctrl\":\"0x0000000000012350\",\"tmo\":5,\"text\":\"Fixture value\"}","pao ui set-text HWND PID START CTRL tmo=5 --stdin"),
        "ui_capture"=>("{\"op\":\"ui_capture\",\"hwnd\":\"0x0000000000012345\",\"pid\":4242,\"start\":\"2026-08-27T20:15:30.1234567Z\",\"dst\":\"captures/window.png\",\"ws\":\"project\",\"tmo\":5}","pao ui capture HWND PID START captures/window.png ws=project tmo=5"),
        "nsis_validate"=>("{\"op\":\"nsis_validate\",\"src\":\"setup/AgentOps.nsi\",\"ws\":\"project\",\"tmo\":60}","pao nsis validate setup/AgentOps.nsi ws=project tmo=60"),
        "nsis_build"=>("{\"op\":\"nsis_build\",\"src\":\"setup/AgentOps.nsi\",\"dst\":\"artifacts/AgentOps-Setup.exe\",\"ws\":\"project\",\"tmo\":120}","pao nsis build setup/AgentOps.nsi artifacts/AgentOps-Setup.exe ws=project tmo=120"),
        _=>($"{{\"op\":\"{action}\"}}",$"pao help {action}")
    };

    private static object S(string description)=>new{type="string",description};
    private static object B(string description)=>new{type="boolean",description};
    private static object N(int minimum,int maximum)=>new{type="integer",minimum,maximum};
    public static readonly object[] All =
    [
        new
        {
            name=Name,
            description="Perform one fixed typed Pegasus AgentOps operation. Use output=agent for requested results without routine headers; accepted/running work includes its jid, failures keep recovery evidence, and read_lines returns numbered text. Omit output or use output=structured for the legacy machine receipt. Irrelevant fields and generic command strings are rejected; terminal_send/input expressly address an owned local terminal.",
            inputSchema=new
            {
                type="object",
                properties=new
                {
                    op=new{type="string",@enum=Routes.Keys.ToArray(),description="fixed operation selector"},
                    epoch=S("exact application inventory host epoch or original exclusive maintenance interval epoch for admin_end"),cursor=new{type=new[]{"string","integer"},description="opaque application inventory cursor (string), or terminal absolute byte cursor"},sid=S("exact owner-host local terminal handle"),cid=S("stored terminal command reference for intentional new-attempt repeat"),iid=S("exact terminal input id; input_status defaults to last input"),original_rid=S("original submitted RID for exact input_status recovery after a lost reply"),
                    aid=new{type="string",pattern="^[a-f0-9]{32}$",description="exact owned application generation; required for app_stop and optional expected terminal generation for explicit app_start restart"},
                    output=new{type="string",@enum=new[]{"agent","structured"},@default="structured",description="Use agent for normal agent output without duplicate structuredContent. Omitted or structured preserves the complete legacy receipt. MCP presentation only; CLI is unchanged."},
                    src=S("source or input path; for .NET use the solution or project path"),dst=S("exact destination or target path"),pol=new{type="string",@enum=new[]{"fail","replace","skip","clean"}},
                    cfg=S("registered tool or build configuration"),ws=S("registered workspace alias, not an absolute path; use ctx_ls to list aliases; for app_start put the working-directory path in cwd; for Git selects its root and must not be combined with cwd; for .NET selects the build root"),cwd=S("working directory; for Git use one absolute path without ws"),enc=S("text encoding"),tmo=new{type="integer",minimum=1,maximum=3600,description="seconds"},
                    arg=new{type="array",items=new{type="string"},maxItems=256,description="separate argv entries for registered execution"},
                    rec=S("registered one-action recipe"),vars=S("bounded JSON variable object for do"),jid=S("job id"),rid=S("idempotency id"),sha=S("expected preimage SHA-256"),
                    fmt=new{type="string",@enum=new[]{"literal","regex","png","text","base64"},description="operation-specific format; search uses literal|regex and ui_capture uses png"},max=N(1,65536),root=new{type="string",minLength=4,maxLength=16,description="exact four ASCII letters or digits; the client trims surrounding whitespace, accepts one matched quote or escaped-quote wrapper, and uppercases letters before strict service validation"},
                    wait=new{type="boolean",@default=true,description="Queueable User operations only: false returns accepted jid/state before completion; inspect job_stat/job_wait/job_log. Omitted or true waits synchronously. False is not supported with root; app, UI, service, and read-only operations accept no wait field."},
                    detail=B("include bounded detail"),recur=B("recurse"),permanent=B("permanent exact delete"),old=S("exact literal to replace"),line=N(1,int.MaxValue),count=N(0,4096),
                    port=N(1,65535),pid=N(1,int.MaxValue),start=S("canonical UTC process start identity"),service=new{type="string",@enum=new[]{ApplicationAuthorizationProtocol.CerebralServiceName},description="exact admitted Windows service identity"},expect=new{type="string",@enum=new[]{"running","stopped"},description="required current service state precondition"},pattern=S("search or name pattern"),type=new{type="string",@enum=new[]{"any","file","dir"}},
                    ignore_case=B("case-insensitive comparison"),include=S("simple included file pattern"),text=new{type="string",maxLength=OperationRequest.MaxBodyBytes},name=S("workspace alias for ctx_add or advertised operation name for help"),
                    fid=new{type="string",pattern="^F[0-9A-Fa-f]{16}$"},kind=new{type="string",@enum=new[]{"bug","feature","improvement","docs","usability","performance","other"}},area=new{type="string",pattern="^[A-Za-z0-9._-]{1,64}$"},
                    summary=new{type="string",maxLength=FeedbackOperations.MaxSummaryBytes},notes=new{type="string",maxLength=FeedbackOperations.MaxNotesBytes},status=new{type="string",@enum=new[]{"open","planned","deferred","done","declined"}},after=N(0,int.MaxValue),
                    state=S("job history state or unresolved"),before=S("job history UTC cutoff ending in Z"),mode=S("operation-specific mode; job_prune uses details or discard_unresolved"),
                    title=new{type="string",maxLength=4096},window_text=new{type="string",maxLength=4096},match=new{type="string",@enum=new[]{"exact","contains"}},hwnd=new{type="string",pattern="^0x[0-9A-Fa-f]{1,16}$"},ctrl=new{type="string",pattern="^0x[0-9A-Fa-f]{1,16}$"},control_class=new{type="string",maxLength=256},instance=N(1,128),x=N(-32768,32768),y=N(-32768,32768),width=N(1,16384),height=N(1,16384),button=new{type="string",@enum=new[]{"left","right","middle"}}
                },
                required=new[]{"op"},additionalProperties=false
            },
            annotations=new{readOnlyHint=false,destructiveHint=true,idempotentHint=false,openWorldHint=false}
        }
    ];
}

internal static class PaoScalarInput
{
    private static readonly string[] Wrappers = ["\\\"", "\\'", "\"", "'"];
    private static readonly HashSet<string> ContentFields = new(StringComparer.Ordinal)
    {
        "old", "pattern", "include", "summary", "notes", "title", "window_text", "text", "vars"
    };
    private static readonly HashSet<string> CliAssignmentFields = new(StringComparer.Ordinal)
    {
        "src", "dst", "aid", "epoch", "cursor", "jid", "pol", "cfg", "ws", "cwd", "enc", "tmo", "rid", "sha", "max", "root", "detail", "quiet", "wait", "elevate",
        "recur", "permanent", "allow_root_target", "follow_links", "overwrite", "op", "old", "line", "count", "port", "pid", "start",
        "name", "service", "expect", "label", "rec", "pattern", "type", "mode", "ignore_case", "include", "fmt", "fid", "kind", "area",
        "summary", "notes", "status", "after", "before", "state", "title", "window_text", "match", "hwnd", "ctrl", "control_class", "instance", "x", "y",
        "width", "height", "button"
    };
    // Scalar normalization also serves MCP/body selectors which are not CLI
    // assignments. Keep that exact extra vocabulary separate from CLI syntax.
    private static readonly HashSet<string> AssignmentFields = new(
        CliAssignmentFields.Concat(["sid", "cid", "iid", "original_rid", "text", "vars"]), StringComparer.Ordinal);
    internal static bool IsCliAssignmentField(string field) => CliAssignmentFields.Contains(field);

    public static string NormalizeCliToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > OperationRequest.MaxBodyBytes) return token;
        var candidate = Unwrap(token.Trim());
        var split = candidate.IndexOf('=');
        if (split < 1 || !AssignmentFields.Contains(candidate[..split])) return token;
        return TryNormalize(candidate[..split], candidate[(split + 1)..], out var value)
            ? candidate[..split] + "=" + value
            : token;
    }

    public static bool TryNormalize(string field, string? input, out string value)
    {
        value = string.Empty;
        if (input is null || input.Length > OperationRequest.MaxBodyBytes || input.IndexOf('\0') >= 0) return false;
        if (ContentFields.Contains(field) || field.StartsWith("arg", StringComparison.Ordinal) &&
            int.TryParse(field.AsSpan(3), out var argumentIndex) && argumentIndex is >= 0 and < 256)
        { value = input; return true; }
        var candidate = Unwrap(input.Trim());
        if (field == "wait")
        {
            if (!bool.TryParse(candidate, out var wait)) return false;
            value = wait ? "true" : "false";
            return true;
        }
        if (field == "root")
        {
            if (candidate.Length != 4 || candidate.Any(character => !IsAsciiAlphaNumeric(character))) return false;
            value = candidate.ToUpperInvariant();
            return true;
        }
        if (candidate.IndexOfAny(['\r', '\n']) >= 0) return false;
        value = candidate;
        return true;
    }

    private static string Unwrap(string value)
    {
        foreach (var wrapper in Wrappers)
            if (value.Length >= wrapper.Length * 2 && value.StartsWith(wrapper, StringComparison.Ordinal) &&
                value.EndsWith(wrapper, StringComparison.Ordinal))
                return value[wrapper.Length..^wrapper.Length].Trim();
        return value;
    }

    private static bool IsAsciiAlphaNumeric(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}

internal static class ToolCatalog
{
    private static object Tool(string name, string description, object properties, string[]? required = null) => new { name, description, inputSchema = new { type = "object", properties, required = required ?? Array.Empty<string>(), additionalProperties = false } };
    private static object S(string description) => new { type = "string", description };
    private static object B(string description) => new { type = "boolean", description };
    private static object R() => new { type = "string", minLength = 4, maxLength = 16, description = "exact four ASCII letters or digits; the client normalizes case and one matched quote wrapper before strict service validation" };
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["pao_installed_program"]="installed_program",
        ["pao_windows_task"]="windows_task",
        ["pao_windows_device"]="windows_device",
        ["pao_windows_network"]="windows_network",
        ["pao_windows_account"]="windows_account",
        ["pao_connection_profile"]="connection_profile",
        ["pao_windows_service"]="windows_service", ["pao_certificate"]="certificate", ["pao_registry"]="registry", ["pao_ctx"]="ctx", ["pao_stat"]="stat", ["pao_ls"]="ls", ["pao_find"]="find", ["pao_search"]="search", ["pao_tail"]="slice", ["pao_read_lines"]="read_lines", ["pao_encoding"]="encoding", ["pao_diff"]="diff", ["pao_count"]="count", ["pao_path"]="path", ["pao_lines"]="lines", ["pao_disk"]="disk", ["pao_rd"]="rd", ["pao_wr"]="wr", ["pao_edit"]="edit", ["pao_patch"]="patch", ["pao_mkdir"]="mkdir", ["pao_cp"]="cp", ["pao_mv"]="mv", ["pao_rm"]="rm", ["pao_sha"]="sha", ["pao_zip"]="zip", ["pao_7z"]="7z", ["pao_app"]="app", ["pao_terminal"]="terminal", ["pao_admin"]="admin", ["pao_run"]="run", ["pao_pwsh"]="pwsh", ["pao_git"]="git", ["pao_dotnet"]="dotnet", ["pao_proc"]="proc", ["pao_service"]="service", ["pao_net"]="net", ["pao_job"]="job", ["pao_feedback"]="feedback", ["pao_do"]="do", ["pao_ui"]="ui", ["pao_nsis"]="nsis", ["pao_diag"]="diag", ["pao_help"]="help", ["pao_root"]="root", ["pao_tool"]="tool", ["pao_recipe"]="recipe", ["pao_queue"]="queue"
    };

    public static bool TryParseArguments(string name, JsonElement arguments, out Dictionary<string, string> result,out string? body)
    {
        result = new Dictionary<string, string>(StringComparer.Ordinal);body=null;
        if(name=="pao_terminal") return TerminalToolCatalog.Parse(arguments,out result,out body);
        if(name=="pao_registry") return RegistryToolCatalog.Parse(arguments,out result,out body);
        if(name=="pao_certificate") return CertificateToolCatalog.Parse(arguments,out result,out body);
        if(name=="pao_windows_service") return WindowsServiceToolCatalog.Parse(arguments,out result,out body);
        if(name=="pao_installed_program") return WindowsInstalledProgramToolCatalog.Parse(arguments,out result,out body);
        if(name=="pao_windows_task") return WindowsTaskToolCatalog.Parse(arguments,out result,out body);
        if(name=="pao_windows_device") return WindowsDeviceToolCatalog.Parse(arguments,out result,out body);
        if(name=="pao_windows_network") return WindowsNetworkToolCatalog.Parse(arguments,out result,out body);
        if(name=="pao_windows_account") return WindowsAccountToolCatalog.Parse(arguments,out result,out body);
        if(name=="pao_connection_profile") return ConnectionProfileToolCatalog.Parse(arguments,out result,out body);
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var allowed = name switch
        {
            "pao_ctx" => new[] { "op", "register", "ws", "cwd", "rid" },
            "pao_stat" or "pao_sha" => new[] { "src", "ws", "cwd", "rid", "root" },
            "pao_ls" => new[] { "src", "max", "ws", "cwd", "rid", "root" },
            "pao_find" => new[] { "src","pattern","type","max","ws","cwd","rid" },
            "pao_search" => new[] { "src","pattern","recur","ignore_case","fmt","include","max","ws","cwd","rid" },
            "pao_tail" => new[] { "src","mode","count","enc","ws","cwd","rid" },
            "pao_read_lines" => new[] { "src","line","count","enc","ws","cwd","rid" },
            "pao_encoding" => new[] { "op","src","dst","enc","sha","ws","cwd","rid" },
            "pao_diff" => new[] { "src","dst","max","ws","cwd","rid" },
            "pao_count" => new[] { "src","ws","cwd","rid" },
            "pao_path" => new[] { "src","op","ws","cwd","rid" },
            "pao_lines" => new[] { "src","op","ignore_case","ws","cwd","rid" },
            "pao_disk" => new[] { "src","op","ws","cwd","rid" },
            "pao_mkdir" => new[] { "dst", "ws", "cwd", "rid", "root" },
            "pao_cp" => new[] { "src", "dst", "pol", "ws", "cwd", "rid", "root" },
            "pao_wr" => new[] { "op","dst","old","count","sha","enc","text","ws","cwd","rid" },
            "pao_edit" => new[] { "dst","line","count","sha","enc","text","ws","cwd","rid" },
            "pao_patch" => new[] { "sha","enc","text","ws","cwd","rid" },
            "pao_mv" => new[] { "src","dst","pol","ws","cwd","rid","root" },
            "pao_rm" => new[] { "dst","permanent","ws","cwd","rid","root" },
            "pao_zip" => new[] { "op","src","dst","pol","ws","cwd","rid","root" },
            "pao_pwsh" => new[] { "src", "arg", "tmo", "ws", "cwd", "rid" },
            "pao_job" => new[] { "op", "jid", "max", "mode", "before", "state", "after", "rid", "root" },
            "pao_feedback" => new[] { "op", "fid", "kind", "area", "summary", "notes", "status", "after", "max", "rid" },
            "pao_diag" => new[] { "detail", "rid", "root" },
            "pao_rd" => new[] { "src","enc","ws","cwd","rid","root" },
            "pao_admin" => new[] { "op","cfg","sha","jid","epoch","tmo","root","rid","text" },
            "pao_run" => new[] { "cfg","arg","tmo","cwd","rid" },
            "pao_app" => new[] { "op","cfg","name","aid","arg","cwd","ws","rid","epoch","cursor","max" },
            "pao_git" => new[] { "op","arg","tmo","ws","cwd","rid" },
            "pao_dotnet" => new[] { "op","src","dst","cfg","arg","tmo","ws","cwd","rid" },
            "pao_proc" => new[] { "op","pid","start","tmo","max","pattern","rid","root" },
            "pao_service" => new[] { "op","service","expect","pid","start","tmo","rid","root" },
            "pao_net" => new[] { "op","port","max","rid","root" },
            "pao_7z" => new[] { "op","src","dst","pol","ws","cwd","rid" },
            "pao_do" => new[] { "rec","vars","cwd","rid" },
            "pao_help" => new[] { "name","rid" },
            "pao_tool" or "pao_recipe" => new[] { "op","name","rid" },
            "pao_ui" => new[] { "op","title","window_text","match","hwnd","pid","start","ctrl","control_class","instance","x","y","width","height","button","count","tmo","text","dst","fmt","ws","cwd","rid" },
            "pao_nsis" => new[] { "op","src","dst","tmo","ws","cwd","rid" },
            _ => Array.Empty<string>()
        };
        var required = name switch { "pao_stat" or "pao_ls" or "pao_find" or "pao_search" or "pao_tail" or "pao_read_lines" or "pao_count" or "pao_path" or "pao_lines" or "pao_disk" or "pao_sha" or "pao_pwsh" => new[] { "src" }, "pao_diff"=>new[]{"src","dst"}, "pao_run"=>new[]{"cfg"}, "pao_mkdir" or "pao_wr" or "pao_rm" => new[] { "dst" }, "pao_edit"=>new[]{"dst","line","count"}, "pao_cp" or "pao_mv" => new[] { "src", "dst" },"pao_encoding"=>new[]{"op","src"},"pao_help"=>new[]{"name"},"pao_zip"=>new[]{"op","src"},"pao_dotnet"=>new[]{"op","src"},"pao_proc" or "pao_service" or "pao_git" or "pao_net" or "pao_feedback" or "pao_tool" or "pao_recipe" or "pao_nsis"=>new[]{"op"}, _ => Array.Empty<string>() };
        foreach (var property in arguments.EnumerateObject())
        {
            // The unified MCP schema exposes detail as a common receipt option.
            // Keep operation-specific fields closed while accepting that common field consistently.
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) && property.Name is not ("detail" or "wait")) return false;
            if(property.Name is "text" or "vars")
            {if(property.Value.ValueKind!=JsonValueKind.String)return false;body=property.Value.GetString()!;if(System.Text.Encoding.UTF8.GetByteCount(body)>OperationRequest.MaxBodyBytes)return false;}
            else if (property.Name == "arg")
            {
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() > 256) return false;
                var i = 0; foreach (var value in property.Value.EnumerateArray()) { if (value.ValueKind != JsonValueKind.String) return false; result[$"arg{i++}"] = value.GetString()!; }
            }
            else if (property.Name is "max" or "tmo" or "line" or "count" or "port" or "pid" or "after" or "instance" or "x" or "y" or "width" or "height")
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var number)) return false;
                if(!ValidNumber(name,property.Name,number))return false;
                result[property.Name] = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (property.Name == "wait")
            { if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false; result[property.Name] = property.Value.GetBoolean() ? "true" : "false"; }
            else if (property.Name is "detail" or "overwrite" or "permanent" or "recur" or "ignore_case")
            { if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false; result[property.Name] = property.Value.GetBoolean().ToString(); }
            else if (property.Name == "root")
            {
                if (property.Value.ValueKind != JsonValueKind.String ||
                    !PaoScalarInput.TryNormalize(property.Name, property.Value.GetString(), out var normalizedRoot)) return false;
                result[property.Name] = normalizedRoot;
            }
            else
            {
                if (property.Value.ValueKind != JsonValueKind.String ||
                    !PaoScalarInput.TryNormalize(property.Name, property.Value.GetString(), out var value)) return false;
                result[property.Name] = value;
            }
        }
        foreach (var requiredName in required)
            if (!result.TryGetValue(requiredName, out var requiredValue) || string.IsNullOrWhiteSpace(requiredValue)) return false;
        if (result.TryGetValue("pol", out var pol) && pol is not ("fail" or "replace" or "skip" or "clean")) return false;
        if(name=="pao_job"&&!ValidJob(result))return false;
        if(name=="pao_job"&&result.ContainsKey("root")&&result.GetValueOrDefault("op") is not ("retry" or "stat" or "wait"))return false;
        if (name=="pao_git"&&result.TryGetValue("op",out var gitOp)&&gitOp is not ("status" or "diff" or "log" or "show" or "rev-parse")) return false;
        if(name=="pao_git"&&(result.ContainsKey("ws")&&result.ContainsKey("cwd")||result.TryGetValue("cwd",out var gitCwd)&&IsAliasPath(gitCwd)))return false;
        if (name=="pao_dotnet"&&result.TryGetValue("op",out var dotnetOp)&&dotnetOp is not ("build" or "test" or "restore" or "clean" or "pack" or "publish")) return false;
        if(name=="pao_dotnet"&&result.ContainsKey("dst")&&result.GetValueOrDefault("op")!="publish")return false;
        if(name=="pao_wr"&&result.GetValueOrDefault("op","write") is not("write" or "replace"))return false;
        if(name=="pao_find"&&result.GetValueOrDefault("type","any") is not("any" or "file" or "dir"))return false;
        if(name=="pao_search"&&(!result.ContainsKey("pattern")||result.GetValueOrDefault("fmt","literal") is not("literal" or "regex")))return false;
        if(name=="pao_tail"&&result.GetValueOrDefault("mode","tail") is not("head" or "tail"))return false;
        if(name=="pao_encoding"&&(result.GetValueOrDefault("op") is not("inspect" or "convert")||result.GetValueOrDefault("op")=="convert"&&(!result.ContainsKey("dst")||!result.ContainsKey("enc"))))return false;
        if(name=="pao_path"&&result.GetValueOrDefault("op") is not("realpath" or "basename" or "dirname"))return false;
        if(name=="pao_lines"&&result.GetValueOrDefault("op") is not("sort" or "uniq"))return false;
        if(name=="pao_disk"&&result.GetValueOrDefault("op") is not("du" or "df"))return false;
        if(name=="pao_wr"&&body is null||name=="pao_edit"&&body is null||name=="pao_patch"&&body is null)return false;
        if(name=="pao_zip"&&result.GetValueOrDefault("op") is not("create" or "list" or "test" or "extract"))return false;
        if(name=="pao_zip"&&result.GetValueOrDefault("op") is not("list" or "test")&&!result.ContainsKey("dst"))return false;
        if(name=="pao_proc"&&result.GetValueOrDefault("op") is not("ls" or "inspect" or "wait" or "stop" or "tree" or "threads"))return false;
        if(name=="pao_proc"&&result.GetValueOrDefault("op")!="ls"&&!result.ContainsKey("pid"))return false;
        if(name=="pao_proc"&&(result.GetValueOrDefault("op") is "wait" or "stop")&&!result.ContainsKey("start"))return false;
        if(name=="pao_proc"&&!ValidProcessThreads(result))return false;
        if(name=="pao_proc"&&result.TryGetValue("pattern",out var processPattern)&&(result.GetValueOrDefault("op")!="ls"||string.IsNullOrWhiteSpace(processPattern)||System.Text.Encoding.UTF8.GetByteCount(processPattern)>256))return false;
        if(name=="pao_service"&&!ValidService(result))return false;
        if(name=="pao_net"&&result.GetValueOrDefault("op")!="list")return false;
        if(name=="pao_7z"&&(result.GetValueOrDefault("op") is not("list" or "test" or "extract")||result.GetValueOrDefault("op")=="extract"&&!result.ContainsKey("dst")))return false;
        if(name=="pao_do"&&!result.ContainsKey("rec"))return false;
        if(name=="pao_help"&&(!result.TryGetValue("name",out var described)||string.IsNullOrWhiteSpace(described)))return false;
        if(name=="pao_feedback"&&!ValidFeedback(result))return false;
        if(name=="pao_wr"&&result.GetValueOrDefault("op","write")=="replace"&&!result.ContainsKey("old"))return false;
        if(name=="pao_ui"&&!ValidUi(result,body))return false;
        if(name=="pao_nsis"&&!ValidNsis(result))return false;
        if(name=="pao_admin"&&!ValidAdmin(result,body))return false;
        if(name=="pao_app"&&!ValidApplication(result))return false;
        if(result.TryGetValue("root",out var root)&&(!SystemRootTools.Contains(name)||!System.Text.RegularExpressions.Regex.IsMatch(root,"^[A-Z0-9]{4}$",System.Text.RegularExpressions.RegexOptions.CultureInvariant)))return false;
        return ValidCompletionMode(name,result);
    }
    public static bool ValidateDictionary(string name,IReadOnlyDictionary<string,string> values)
    {
        if(name=="pao_terminal") return TerminalToolCatalog.Valid(values,null,requireBody:false);
        if(name=="pao_registry") return RegistryToolCatalog.Valid(values,null,requireBody:false);
        if(name=="pao_certificate") return CertificateToolCatalog.Valid(values,null,requireBody:false);
        if(name=="pao_windows_service") return WindowsServiceToolCatalog.Valid(values,null,requireBody:false);
        if(name=="pao_installed_program") return WindowsInstalledProgramToolCatalog.Valid(values,null,requireBody:false);
        if(name=="pao_windows_task") return WindowsTaskToolCatalog.Valid(values,null,requireBody:false);
        if(name=="pao_windows_device") return WindowsDeviceToolCatalog.Valid(values,null,requireBody:false);
        if(name=="pao_windows_network") return WindowsNetworkToolCatalog.Valid(values,null,requireBody:false);
        if(name=="pao_windows_account") return WindowsAccountToolCatalog.Valid(values,null,requireBody:false);
        if(name=="pao_connection_profile") return ConnectionProfileToolCatalog.Valid(values,null,requireBody:false);
        if(!Names.ContainsKey(name))return false;var common=new[]{"rid","ws","cwd","quiet","detail","wait"};
        var allowed=name switch
        {
            "pao_ctx"=>new[]{"op","register"},
            "pao_stat" or "pao_ls" or "pao_rd" or "pao_sha"=>new[]{"src","max","enc"},
            "pao_find"=>new[]{"src","pattern","type","max"},
            "pao_search"=>new[]{"src","pattern","recur","ignore_case","fmt","include","max"},
            "pao_tail"=>new[]{"src","mode","count","enc"},
            "pao_read_lines"=>new[]{"src","line","count","enc"},
            "pao_encoding"=>new[]{"op","src","dst","enc","sha"},
            "pao_diff"=>new[]{"src","dst","max"},
            "pao_count"=>new[]{"src"},
            "pao_path"=>new[]{"src","op"},
            "pao_lines"=>new[]{"src","op","ignore_case"},
            "pao_disk"=>new[]{"src","op"},
            "pao_mkdir"=>new[]{"dst"},
            "pao_cp" or "pao_mv"=>new[]{"src","dst","pol"},
            "pao_wr"=>new[]{"op","dst","old","count","sha","enc"},
            "pao_edit"=>new[]{"dst","line","count","sha","enc"},
            "pao_patch"=>new[]{"sha","enc"},
            "pao_rm"=>new[]{"dst","permanent"},
            "pao_zip"=>new[]{"op","src","dst","pol"},
            "pao_job"=>new[]{"op","jid","max","mode","before","state","after"},
            "pao_feedback"=>new[]{"op","fid","kind","area","summary","notes","status","after","max"},
            "pao_proc"=>new[]{"op","pid","start","tmo","max","pattern"},
            "pao_service"=>new[]{"op","service","expect","pid","start","tmo"},
            "pao_net"=>new[]{"op","port","max"},
            "pao_diag"=>new[]{"detail","root"},
            "pao_pwsh"=>new[]{"src","tmo"},
            "pao_admin"=>new[]{"op","cfg","sha","jid","epoch","tmo"},
            "pao_run"=>new[]{"cfg","tmo"},
            "pao_app"=>new[]{"op","cfg","name","aid","epoch","cursor","max"},
            "pao_git"=>new[]{"op","tmo"},
            "pao_dotnet"=>new[]{"op","src","dst","cfg","tmo"},
            "pao_7z"=>new[]{"op","src","dst","pol"},
            "pao_do"=>new[]{"rec"},
            "pao_help"=>new[]{"name"},
            "pao_root"=>new[]{"op","label"},
            "pao_tool" or "pao_recipe"=>new[]{"op","name"},
            "pao_queue"=>new[]{"op"},
            "pao_ui"=>new[]{"op","title","window_text","match","hwnd","pid","start","ctrl","control_class","instance","x","y","width","height","button","count","tmo","dst","fmt"},
            "pao_nsis"=>new[]{"op","src","dst","tmo"},
            _=>Array.Empty<string>()
        };
        if(!values.Keys.All(x=>common.Contains(x,StringComparer.Ordinal)||allowed.Contains(x,StringComparer.Ordinal)||x=="root"&&SystemRootTools.Contains(name)||x.StartsWith("arg",StringComparison.Ordinal)&&int.TryParse(x.AsSpan(3),out var index)&&index is >=0 and <256))return false;
        foreach(var key in new[]{"tmo","max","line","count","port","pid","after"})if(!ValidOptionalInteger(name,key,values))return false;
        foreach(var key in new[]{"quiet","detail","permanent","recur","ignore_case","overwrite"})if(values.TryGetValue(key,out var raw)&&raw is not("0" or "1" or "true" or "false" or "True" or "False"))return false;
        if(values.TryGetValue("root",out var root)&&(!SystemRootTools.Contains(name)||!System.Text.RegularExpressions.Regex.IsMatch(root,"^[A-Z0-9]{4}$",System.Text.RegularExpressions.RegexOptions.CultureInvariant)))return false;
        if(name=="pao_job"&&values.ContainsKey("root")&&values.GetValueOrDefault("op") is not ("retry" or "stat" or "wait"))return false;
        if(name=="pao_net"&&values.GetValueOrDefault("op")!="list")return false;
        if(name=="pao_find"&&values.GetValueOrDefault("type","any") is not("any" or "file" or "dir"))return false;
        if(name=="pao_search"&&(!values.ContainsKey("pattern")||values.GetValueOrDefault("fmt","literal") is not("literal" or "regex")))return false;
        if(name=="pao_tail"&&values.GetValueOrDefault("mode","tail") is not("head" or "tail"))return false;
        if(name=="pao_encoding"&&(values.GetValueOrDefault("op") is not("inspect" or "convert")||values.GetValueOrDefault("op")=="convert"&&(!values.ContainsKey("dst")||!values.ContainsKey("enc"))))return false;
        if(name=="pao_path"&&values.GetValueOrDefault("op") is not("realpath" or "basename" or "dirname"))return false;
        if(name=="pao_lines"&&values.GetValueOrDefault("op") is not("sort" or "uniq"))return false;
        if(name=="pao_disk"&&values.GetValueOrDefault("op") is not("du" or "df"))return false;
        if(name=="pao_proc"&&(values.GetValueOrDefault("op") is not("ls" or "inspect" or "wait" or "stop" or "tree" or "threads")||values.GetValueOrDefault("op")!="ls"&&!values.ContainsKey("pid")||(values.GetValueOrDefault("op") is "wait" or "stop")&&!values.ContainsKey("start")))return false;
        if(name=="pao_proc"&&!ValidProcessThreads(values))return false;
        if(name=="pao_proc"&&values.TryGetValue("pattern",out var processPattern)&&(values.GetValueOrDefault("op")!="ls"||string.IsNullOrWhiteSpace(processPattern)||System.Text.Encoding.UTF8.GetByteCount(processPattern)>256))return false;
        if(name=="pao_service"&&!ValidService(values))return false;
        if(name=="pao_git"&&!values.ContainsKey("op")||name=="pao_dotnet"&&(!values.ContainsKey("op")||!values.ContainsKey("src")))return false;
        if(name=="pao_dotnet"&&(values.GetValueOrDefault("op") is not("build" or "test" or "restore" or "clean" or "pack" or "publish")||values.ContainsKey("dst")&&values.GetValueOrDefault("op")!="publish"))return false;
        if(name=="pao_git"&&(values.ContainsKey("ws")&&values.ContainsKey("cwd")||values.TryGetValue("cwd",out var gitCwd)&&IsAliasPath(gitCwd)))return false;
        if(name=="pao_job"&&!ValidJob(values))return false;
        if(name=="pao_7z"&&(values.GetValueOrDefault("op") is not("list" or "test" or "extract")||values.GetValueOrDefault("op")=="extract"&&!values.ContainsKey("dst")))return false;
        if(name=="pao_do"&&!values.ContainsKey("rec"))return false;
        if(name=="pao_help"&&(!values.TryGetValue("name",out var described)||string.IsNullOrWhiteSpace(described)))return false;
        if(name=="pao_root"&&values.GetValueOrDefault("op") is not("ls" or "upsert" or "rm"))return false;
        if(name is "pao_tool" or "pao_recipe"&&values.GetValueOrDefault("op") is not("ls" or "add" or "rm"))return false;
        if(name=="pao_queue"&&values.GetValueOrDefault("op") is not("stat" or "pause" or "resume"))return false;
        if(name=="pao_feedback"&&!ValidFeedback(values))return false;
        if(name=="pao_ui"&&!ValidUi(values,values.GetValueOrDefault("op")=="set_text"?"present":null))return false;
        if(name=="pao_nsis"&&!ValidNsis(values))return false;
        if(name=="pao_admin"&&!ValidAdmin(values,values.GetValueOrDefault("op") is "admit" or "run" or "validate" or "reconcile" or "ack" or "end"?"present":null))return false;
        if(name=="pao_app"&&!ValidApplication(values))return false;
        return ValidCompletionMode(name,values);
    }
    private static bool ValidAdmin(IReadOnlyDictionary<string,string> values,string? body)
    {
        var op=values.GetValueOrDefault("op");
        if((op!="validate"&&!values.ContainsKey("root"))||values.Keys.Any(key=>key is "ws" or "cwd" or "wait"||key.StartsWith("arg",StringComparison.Ordinal)))return false;
        if(op is not("validate" or "admit" or "run" or "stat" or "revoke" or "reconcile" or "begin" or "end" or "interval" or "ack")||
            (op is "admit" or "run" or "validate" or "reconcile" or "ack" or "end")!=(body is not null))return false;
        var allowed=op switch
        {
            "validate"=>new[]{"op","rid","detail","quiet"},
            "admit"=>new[]{"op","root","rid","detail","quiet"},
            "run"=>new[]{"op","cfg","sha","root","rid","tmo","detail","quiet"},
            "stat"=>new[]{"op","jid","root","rid","tmo","detail","quiet"},
            "reconcile"=>new[]{"op","jid","root","rid","detail","quiet"},
            "begin" or "ack"=>new[]{"op","cfg","sha","root","rid","detail","quiet"},
            "end"=>new[]{"op","cfg","sha","epoch","root","rid","detail","quiet"},
            _=>new[]{"op","cfg","root","rid","detail","quiet"}
        };
        if(values.Keys.Any(key=>!allowed.Contains(key,StringComparer.Ordinal)))return false;
        if(op is "run" or "begin" or "end" or "ack"&&
            (!values.TryGetValue("rid",out var rid)||!System.Text.RegularExpressions.Regex.IsMatch(rid,"^[a-f0-9]{32}$")||
             !values.TryGetValue("sha",out var sha)||!System.Text.RegularExpressions.Regex.IsMatch(sha,"^[a-f0-9]{64}$")))return false;
        if(op=="end"&&(!values.TryGetValue("epoch",out var epoch)||!System.Text.RegularExpressions.Regex.IsMatch(epoch,"^[a-f0-9]{32}$")))return false;
        var key=op is "stat" or "reconcile"?"jid":"cfg";
        return op is "admit" or "validate"||values.TryGetValue(key,out var id)&&System.Text.RegularExpressions.Regex.IsMatch(id,"^[a-f0-9]{32}$");
    }
    private static bool ValidCompletionMode(string name,IReadOnlyDictionary<string,string> values)
    {
        if(!values.TryGetValue("wait",out var wait))return true;
        if(wait is not("true" or "false")||!Names.TryGetValue(name,out var operation))return false;
        // Submission semantics come from the execution engine, not a second
        // catalog that can drift from the queue/admission boundary.
        return AgentOpsEngine.SupportsDeferredCompletion(new OperationRequest(operation,values))&&
            (wait!="false"||!values.ContainsKey("root"));
    }
    private static bool ValidJob(IReadOnlyDictionary<string,string> values)
    {
        var action=values.GetValueOrDefault("op");
        if(action is not("stat" or "wait" or "ls" or "cancel" or "retry" or "log" or "prune"))return false;
        if(action is not("ls" or "prune")&&!values.ContainsKey("jid"))return false;
        if(values.ContainsKey("root")&&action is not ("retry" or "stat" or "wait"))return false;
        if(values.ContainsKey("root")&&action is "stat" or "wait")
        {
            if(values.Keys.Any(key=>key is not ("op" or "jid" or "root" or "rid" or "quiet" or "detail"))||
                !values.TryGetValue("jid",out var systemJid)||systemJid.Length!=32||
                !systemJid.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f'))return false;
        }
        if(values.TryGetValue("max",out var raw)&&(!int.TryParse(raw,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var maximum)||
            (action=="ls"?maximum is <1 or >100:action=="log"?maximum is <256 or >65536:action=="prune"?maximum is <1 or >256:true)))return false;
        if(values.ContainsKey("state")&&action!="ls"||values.ContainsKey("after")&&action!="ls"||values.ContainsKey("before")&&action is not("ls" or "prune")||values.ContainsKey("mode")&&action!="prune")return false;
        if(values.TryGetValue("state",out var state)&&state is not("unresolved" or "attention" or "recovery_required" or "done" or "failed" or "canceled" or "cancelled" or "queued" or "running"))return false;
        if(values.TryGetValue("after",out var after)&&(!long.TryParse(after,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var cursor)||cursor<0))return false;
        if(values.TryGetValue("before",out var before)&&(!before.EndsWith('Z')||!DateTimeOffset.TryParse(before,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal,out _)))return false;
        return action!="prune"||values.GetValueOrDefault("mode") is "details" or "discard_unresolved"&&values.ContainsKey("before")&&!values.ContainsKey("jid")&&!values.ContainsKey("ws")&&!values.ContainsKey("cwd")&&!values.ContainsKey("wait");
    }
    private static readonly HashSet<string> SystemRootTools=new(StringComparer.Ordinal){"pao_diag","pao_stat","pao_ls","pao_rd","pao_mkdir","pao_cp","pao_mv","pao_rm","pao_sha","pao_zip","pao_proc","pao_service","pao_net","pao_job","pao_admin"};
    private static bool IsAliasPath(string value){var colon=value.IndexOf(':');return colon>0&&!(colon==1&&value.Length>2&&value[2] is '\\' or '/');}
    private static bool ValidNumber(string name,string key,int value)=>key switch{"tmo" when name=="pao_admin"=>value is >=1 and <=60,"tmo" when name=="pao_ui"=>value is >=1 and <=30,"tmo" when name=="pao_service"=>value is >=1 and <=120,"tmo"=>value is >=1 and <=3600,"line" when name is "pao_edit" or "pao_read_lines"=>value>=1,"count" when name=="pao_ui"=>value is >=1 and <=3,"count" when name=="pao_edit"=>value is >=1 and <=4096,"count" when name=="pao_read_lines"=>value is >=1 and <=512,"count" when name=="pao_tail"=>value is >=1 and <=4096,"count"=>value>=0,"port"=>value is >=1 and <=65535,"pid"=>value>=1,"instance" when name=="pao_ui"=>value is >=1 and <=128,"x" or "y" when name=="pao_ui"=>value is >=-32768 and <=32768,"width" or "height" when name=="pao_ui"=>value is >=1 and <=16384,"after" when name=="pao_feedback"=>value>=0,"max" when name=="pao_ls"=>value is >=1 and <=48,"max" when name is "pao_find" or "pao_search" or "pao_diff"=>value is >=1 and <=512,"max" when name is "pao_proc" or "pao_net"=>value is >=1 and <=1024,"max" when name=="pao_feedback"=>value is >=1 and <=FeedbackOperations.MaxPageSize,"max" when name=="pao_job"=>value is >=1 and <=65536,"max"=>value is >=1 and <=4096,_=>true};
    private static bool ValidUi(IReadOnlyDictionary<string,string> values,string? body)
    {
        var op=values.GetValueOrDefault("op");if(op is not("status" or "find" or "window" or "control" or "activate" or "move" or "focus" or "click" or "set_text" or "capture"))return false;
        if(op=="status")return values.Keys.All(key=>key is "op" or "rid" or "detail" or "quiet");
        if(!values.ContainsKey("tmo"))return false;
        if(op=="find")return values.TryGetValue("title",out var title)&&!string.IsNullOrWhiteSpace(title)&&System.Text.Encoding.UTF8.GetByteCount(title)<=4096&&values.GetValueOrDefault("match","exact") is "exact" or "contains"&&(!values.TryGetValue("window_text",out var windowText)||System.Text.Encoding.UTF8.GetByteCount(windowText)<=4096);
        if(!ValidHandle(values.GetValueOrDefault("hwnd"))||!values.TryGetValue("pid",out var pidRaw)||!int.TryParse(pidRaw,out var pid)||pid<1||!values.TryGetValue("start",out var start)||!DateTimeOffset.TryParseExact(start,"O",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal|System.Globalization.DateTimeStyles.AdjustToUniversal,out _))return false;
        if(op=="control")return values.TryGetValue("ctrl",out var existing)?ValidHandle(existing):values.TryGetValue("control_class",out var controlClass)&&!string.IsNullOrWhiteSpace(controlClass)&&System.Text.Encoding.UTF8.GetByteCount(controlClass)<=256;
        if(op=="move")return values.ContainsKey("x")&&values.ContainsKey("y")&&values.ContainsKey("width")&&values.ContainsKey("height");
        if(op=="capture")return values.TryGetValue("dst",out var destination)&&!string.IsNullOrWhiteSpace(destination)&&values.GetValueOrDefault("fmt","png")=="png";
        if((op is "focus" or "click" or "set_text")&&!ValidHandle(values.GetValueOrDefault("ctrl")))return false;
        if(op=="click"&&values.GetValueOrDefault("button","left") is not("left" or "right" or "middle"))return false;
        return op!="set_text"||body is not null;
    }
    private static bool ValidApplication(IReadOnlyDictionary<string,string> values)
    {
        var op=values.GetValueOrDefault("op");
        if(op is not("start" or "status" or "stop" or "inventory" or "release"))return false;
        var allowed=op switch
        {
            "start"=>new[]{"op","cfg","name","aid","cwd","ws","rid","quiet","detail"},
            "status"=>new[]{"op","name","rid","quiet","detail"},
            "inventory"=>new[]{"op","max","cursor","rid","quiet","detail"},
            "release"=>new[]{"op","aid","epoch","rid","quiet","detail"},
            _=>new[]{"op","aid","rid","quiet","detail"}
        };
        if(values.Keys.Any(key=>!allowed.Contains(key,StringComparer.Ordinal)&&!(op=="start"&&key.StartsWith("arg",StringComparison.Ordinal)&&int.TryParse(key.AsSpan(3),out var index)&&index is >=0 and <256)))return false;
        if(op=="start"&&(!values.TryGetValue("cfg",out var cfg)||string.IsNullOrWhiteSpace(cfg)))return false;
        if(op is "start" or "status"&&(!values.TryGetValue("name",out var name)||name.Any(char.IsControl)||!System.Text.RegularExpressions.Regex.IsMatch(name,"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$",System.Text.RegularExpressions.RegexOptions.CultureInvariant)))return false;
        if(op is "stop" or "release"&&!values.ContainsKey("aid"))return false;
        if(op=="release"&&(!values.TryGetValue("epoch",out var epoch)||
            !System.Text.RegularExpressions.Regex.IsMatch(epoch,"^[a-f0-9]{32}$",System.Text.RegularExpressions.RegexOptions.CultureInvariant)||
            !values.TryGetValue("rid",out var releaseRid)||string.IsNullOrWhiteSpace(releaseRid)))return false;
        if(op=="inventory"&&(values.TryGetValue("max",out var maximum)&&
            (!int.TryParse(maximum,out var page)||page is <1 or >8)||
            values.TryGetValue("cursor",out var cursor)&&(cursor.Length>128||cursor.Any(char.IsControl))))return false;
        if(values.TryGetValue("aid",out var aid)&&(aid.Any(char.IsControl)||!System.Text.RegularExpressions.Regex.IsMatch(aid,"^[a-f0-9]{32}$",System.Text.RegularExpressions.RegexOptions.CultureInvariant)))return false;
        if(op=="start"&&values.Keys.Count(key=>key.StartsWith("arg",StringComparison.Ordinal))!=Enumerable.Range(0,256).TakeWhile(index=>values.ContainsKey("arg"+index)).Count())return false;
        return !values.TryGetValue("rid",out var rid)||!string.IsNullOrWhiteSpace(rid)&&rid.Length<=128&&!rid.Any(char.IsControl);
    }

    private static bool ValidProcessThreads(IReadOnlyDictionary<string,string> values)
    {
        if (values.GetValueOrDefault("op") != "threads") return true;
        var allowed = new[] { "op", "pid", "start", "max", "rid", "detail", "output", "quiet", "cwd" };
        return values.Keys.All(key => allowed.Contains(key, StringComparer.Ordinal)) &&
            values.TryGetValue("pid", out var pid) && int.TryParse(pid, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0 &&
            values.TryGetValue("start", out var start) && ProcessOperations.IsCanonicalStartIdentity(start) &&
            (!values.TryGetValue("max", out var max) || int.TryParse(max, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var maximum) && maximum is >= 1 and <= 256);
    }

    private static bool ValidService(IReadOnlyDictionary<string,string> values)
    {
        var op=values.GetValueOrDefault("op");
        if(op is not("status" or "start" or "stop" or "restart")||
            values.GetValueOrDefault("service")!=ApplicationAuthorizationProtocol.CerebralServiceName||
            !values.ContainsKey("root"))return false;
        if(op=="status")return !values.ContainsKey("expect")&&!values.ContainsKey("pid")&&!values.ContainsKey("start")&&!values.ContainsKey("tmo");
        if(!values.ContainsKey("tmo"))return false;
        if(op=="start")return values.GetValueOrDefault("expect")=="stopped"&&!values.ContainsKey("pid")&&!values.ContainsKey("start");
        return values.GetValueOrDefault("expect")=="running"&&values.TryGetValue("pid",out var rawPid)&&int.TryParse(rawPid,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var pid)&&pid>0&&values.TryGetValue("start",out var start)&&DateTimeOffset.TryParseExact(start,"O",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal|System.Globalization.DateTimeStyles.AdjustToUniversal,out _);
    }

    private static bool ValidNsis(IReadOnlyDictionary<string,string> values)
    {
        var op=values.GetValueOrDefault("op");
        if(op=="status")return !values.ContainsKey("src")&&!values.ContainsKey("dst")&&!values.ContainsKey("tmo");
        if(op=="validate")return values.ContainsKey("src")&&!values.ContainsKey("dst");
        return op=="build"&&values.ContainsKey("src")&&values.ContainsKey("dst");
    }
    private static bool ValidHandle(string? value)=>value is not null&&System.Text.RegularExpressions.Regex.IsMatch(value,"^0x[0-9A-Fa-f]{1,16}$",System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static bool ValidFeedback(IReadOnlyDictionary<string,string> values)
    {
        var op=values.GetValueOrDefault("op");if(op is not("submit" or "ls" or "get" or "set" or "export"))return false;
        if(op=="submit"&&(!values.TryGetValue("kind",out var kind)||!values.TryGetValue("area",out var area)||!values.TryGetValue("summary",out var summary)||!ValidFeedbackKind(kind)||!ValidFeedbackArea(area)||string.IsNullOrWhiteSpace(summary)||summary.Contains('\r')||summary.Contains('\n')||System.Text.Encoding.UTF8.GetByteCount(summary)>FeedbackOperations.MaxSummaryBytes))return false;
        if(op is "get" or "set"&&(!values.TryGetValue("fid",out var fid)||!System.Text.RegularExpressions.Regex.IsMatch(fid,"^F[0-9A-Fa-f]{16}$",System.Text.RegularExpressions.RegexOptions.CultureInvariant)))return false;
        if(op=="set"&&(!values.TryGetValue("status",out var setStatus)||!ValidFeedbackStatus(setStatus)))return false;
        if(values.TryGetValue("kind",out var filterKind)&&!ValidFeedbackKind(filterKind)||values.TryGetValue("area",out var filterArea)&&!ValidFeedbackArea(filterArea)||values.TryGetValue("status",out var filterStatus)&&!ValidFeedbackStatus(filterStatus)||values.TryGetValue("notes",out var notes)&&System.Text.Encoding.UTF8.GetByteCount(notes)>FeedbackOperations.MaxNotesBytes)return false;
        var allowed=op switch{"submit"=>new[]{"op","kind","area","summary","notes","rid","detail","quiet"},"get"=>new[]{"op","fid","rid","detail","quiet"},"set"=>new[]{"op","fid","status","rid","detail","quiet"},_=>new[]{"op","kind","area","status","after","max","rid","detail","quiet"}};
        if(op is "submit" or "set")allowed=[..allowed,"wait"];
        return values.Keys.All(key=>allowed.Contains(key,StringComparer.Ordinal));
    }
    private static bool ValidFeedbackKind(string value)=>value is "bug" or "feature" or "improvement" or "docs" or "usability" or "performance" or "other";
    private static bool ValidFeedbackStatus(string value)=>value is "open" or "planned" or "deferred" or "done" or "declined";
    private static bool ValidFeedbackArea(string value)=>value.Length is >=1 and <=64&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '-' or '_');
    private static bool ValidOptionalInteger(string name,string key,IReadOnlyDictionary<string,string> values)=>!values.TryGetValue(key,out var raw)||int.TryParse(raw,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var number)&&ValidNumber(name,key,number);
    public static readonly object[] All =
    {
        RegistryToolCatalog.Definition(),
        CertificateToolCatalog.Definition(),
        WindowsServiceToolCatalog.Definition(),
        WindowsInstalledProgramToolCatalog.Definition(),
        WindowsTaskToolCatalog.Definition(),
        WindowsDeviceToolCatalog.Definition(),
        WindowsNetworkToolCatalog.Definition(),
        WindowsAccountToolCatalog.Definition(),
        ConnectionProfileToolCatalog.Definition(),
        Tool("pao_ctx", "List, resolve, or register a project workspace alias.", new { op=new{type="string",@enum=new[]{"ls","cur"}}, register=S("alias:path registration"), ws=S("workspace alias"), cwd=S("working directory"), rid=S("idempotency receipt id") }),
        Tool("pao_stat", "Read file or directory metadata.", new { src=S("exact source path"), ws=S("workspace alias"), cwd=S("caller working directory"), root=R(), rid=S("receipt id") }, ["src"]),
        Tool("pao_ls", "List a directory with a bounded maximum.", new { src=S("directory path"), max=new { type="integer", minimum=1, maximum=48 }, ws=S("workspace alias"), cwd=S("caller working directory"), root=R(), rid=S("receipt id") }, ["src"]),
        Tool("pao_find", "Recursively find files or directories with a Windows-native simple name pattern.", new { src=S("search root"), pattern=S("simple name pattern such as *.cs"), type=new{type="string",@enum=new[]{"any","file","dir"}}, max=new{type="integer",minimum=1,maximum=512}, ws=S("workspace"), cwd=S("cwd"), rid=S("rid") }, ["src"]),
        Tool("pao_search", "Search recognized text files without a shell or external grep.", new { src=S("file or directory"), pattern=S("literal or regular expression"), recur=B("recurse"), ignore_case=B("case-insensitive"), fmt=new{type="string",@enum=new[]{"literal","regex"}}, include=S("simple file pattern"), max=new{type="integer",minimum=1,maximum=512}, ws=S("workspace"), cwd=S("cwd"), rid=S("rid") }, ["src","pattern"]),
        Tool("pao_tail", "Read a bounded head or tail of a recognized text file.", new { src=S("file"), mode=new{type="string",@enum=new[]{"head","tail"}}, count=new{type="integer",minimum=1,maximum=4096}, enc=S("encoding"), ws=S("workspace"), cwd=S("cwd"), rid=S("rid") }, ["src"]),
        Tool("pao_read_lines", "Read bounded numbered line context with full-file hash and encoding metadata.", new { src=S("file"), line=new{type="integer",minimum=1}, count=new{type="integer",minimum=1,maximum=512}, enc=S("known source encoding"), ws=S("workspace"), cwd=S("cwd"), rid=S("rid") }, ["src"]),
        Tool("pao_encoding", "Inspect encoding without content, or atomically convert recognized text to an explicit target encoding.", new { op=new{type="string",@enum=new[]{"inspect","convert"}}, src=S("source"), dst=S("exact conversion destination"), enc=S("known source encoding for inspect; required target encoding for convert"), sha=S("expected source SHA-256 for convert"), ws=S("workspace"), cwd=S("cwd"), rid=S("rid") }, ["op","src"]),
        Tool("pao_diff", "Compare two bounded recognized text files line by line.", new { src=S("left file"), dst=S("right file"), max=new{type="integer",minimum=1,maximum=512}, ws=S("workspace"), cwd=S("cwd"), rid=S("rid") }, ["src","dst"]),
        Tool("pao_count", "Count lines, words, and bytes in a recognized text file.", new { src=S("file"), ws=S("workspace"), cwd=S("cwd"), rid=S("rid") }, ["src"]),
        Tool("pao_path", "Resolve realpath, basename, or dirname without invoking a shell.", new { src=S("path"), op=new{type="string",@enum=new[]{"realpath","basename","dirname"}}, ws=S("workspace"), cwd=S("cwd"), rid=S("rid") }, ["src","op"]),
        Tool("pao_lines", "Sort lines or remove adjacent duplicate lines without invoking a shell.", new { src=S("recognized text file"), op=new{type="string",@enum=new[]{"sort","uniq"}}, ignore_case=B("case-insensitive comparison"), ws=S("workspace"), cwd=S("cwd"), rid=S("rid") }, ["src","op"]),
        Tool("pao_disk", "Measure file-tree bytes or report local volume capacity without invoking a shell.", new { src=S("file or directory"), op=new{type="string",@enum=new[]{"du","df"}}, ws=S("workspace"), cwd=S("cwd"), rid=S("rid") }, ["src","op"]),
        Tool("pao_mkdir", "Create a directory through the durable mutation FIFO.", new { dst=S("exact destination path"), ws=S("workspace alias"), cwd=S("caller working directory"), root=R(), rid=S("idempotency id") }, ["dst"]),
        Tool("pao_cp", "Copy to the exact destination with fail, replace, skip, or destructive clean policy.", new { src=S("exact source"), dst=S("exact destination"), pol=new { type="string", @enum=new[]{"fail","replace","skip","clean"} }, ws=S("workspace alias"), cwd=S("caller working directory"), root=R(), rid=S("idempotency id") }, ["src","dst"]),
        Tool("pao_sha", "Compute SHA-256 for a file.", new { src=S("file path"), ws=S("workspace alias"), cwd=S("caller working directory"), root=R(), rid=S("receipt id") }, ["src"]),
        Tool("pao_pwsh", "Run one existing ps1 with PowerShell 7 and separately typed arguments.", new { src=S("ps1 path"), arg=new { type="array", items=new { type="string" }, maxItems=256 }, tmo=new { type="integer", minimum=1, maximum=3600 }, ws=S("workspace alias"), cwd=S("working directory"), rid=S("idempotency id") }, ["src"]),
        Tool("pao_job", "List bounded job metadata, inspect, wait, cancel, explicitly retry, prune old payloads, or read a bounded log tail. Explicit root on stat/wait observes only existing Windows-service mutation JIDs in the System Engine; no replay or control. Pruning uses the ordinary owner host and preserves request identities.", new { op=new{type="string",@enum=new[]{"stat","wait","ls","cancel","retry","log","prune"}}, jid=S("job id; omitted for ls and prune"), max=new{type="integer",minimum=1,maximum=65536,description="ls count is 1..100; prune batch is 1..256; log tail bytes are 256..65536"}, mode=S("prune uses details or discard_unresolved"), before=S("required UTC cutoff for prune; optional for ls"), state=S("ls state or unresolved"), after=new{type="integer",minimum=0,maximum=int.MaxValue}, root=R(), rid=S("idempotency id; retry uses it for the fresh job") },["op"]),
        Tool("pao_diag", "Return bounded local diagnostics; root selects the fixed System diagnostic action.", new { detail=B("include bounded detail"), root=R(), rid=S("receipt id") })
        ,Tool("pao_rd","Read recognized text without changing bytes.",new {src=S("source"),enc=S("encoding"),ws=S("workspace"),cwd=S("cwd"),root=R(),rid=S("rid")},["src"])
        ,Tool("pao_wr","Atomically write text or replace an exact literal while preserving recognized encoding.",new {op=new{type="string",@enum=new[]{"write","replace"}},dst=S("target"),old=S("literal required for replace"),count=new{type="integer",minimum=0},sha=S("expected preimage sha"),enc=S("encoding"),text=new{type="string",maxLength=OperationRequest.MaxBodyBytes},ws=S("workspace"),cwd=S("cwd"),rid=S("rid")},["dst","text"])
        ,Tool("pao_edit","Atomically replace a one-based inclusive line range after validating the complete text file.",new {dst=S("target"),line=new{type="integer",minimum=1},count=new{type="integer",minimum=1,maximum=4096},sha=S("expected preimage sha"),enc=S("encoding"),text=new{type="string",maxLength=OperationRequest.MaxBodyBytes},ws=S("workspace"),cwd=S("cwd"),rid=S("rid")},["dst","line","count","text"])
        ,Tool("pao_patch","Apply one preimage-exact unified patch atomically.",new {sha=S("expected preimage sha"),enc=S("encoding"),text=new{type="string",maxLength=OperationRequest.MaxBodyBytes},ws=S("workspace"),cwd=S("cwd"),rid=S("rid")},["text"])
        ,Tool("pao_mv","Move an exact source to an exact destination.",new {src=S("source"),dst=S("destination"),pol=new{type="string",@enum=new[]{"fail","replace"}},ws=S("workspace"),cwd=S("cwd"),root=R(),rid=S("rid")},["src","dst"])
        ,Tool("pao_rm","Quarantine by default or permanently delete an exact admitted target.",new {dst=S("target"),permanent=B("permanent delete"),ws=S("workspace"),cwd=S("cwd"),root=R(),rid=S("rid")},["dst"])
        ,Tool("pao_zip","Create, list, test, or safely extract ZIP archives.",new {op=new{type="string",@enum=new[]{"create","list","test","extract"}},src=S("source"),dst=S("destination for create/extract"),pol=new{type="string",@enum=new[]{"fail","replace","skip","clean"}},ws=S("workspace"),cwd=S("cwd"),root=R(),rid=S("rid")},["op","src"])
        ,Tool("pao_admin","Begin/end an exclusive maintenance interval and execute within a separately reviewed reusable owner-admin scope; observation never kills or replays.",new {op=new{type="string",@enum=new[]{"validate","admit","run","stat","revoke","reconcile","begin","end","interval","ack"}},cfg=S("reviewed scope id"),sha=S("exact normalized invocation hash"),jid=S("original job id"),epoch=S("exact original maintenance interval epoch for end"),text=S("strict nonsecret JSON scope, invocation, reconciliation or acknowledgement"),root=R(),rid=S("exact invocation id"),tmo=new{type="integer",minimum=1,maximum=60}},["op"])
        ,Tool("pao_run","Run a registered tool with exact argv.",new {cfg=S("registered tool name"),arg=new{type="array",items=new{type="string"},maxItems=256},tmo=new{type="integer",minimum=1,maximum=3600},cwd=S("cwd"),rid=S("rid")},["cfg"])
        ,Tool("pao_git","Run a typed Git operation with additional argv.",new {op=new{type="string",@enum=new[]{"status","diff","log","show","rev-parse"}},arg=new{type="array",items=new{type="string"},maxItems=256},tmo=new{type="integer",minimum=1,maximum=3600},ws=S("workspace alias selecting the repository root; omit cwd"),cwd=S("absolute working directory; omit ws"),rid=S("rid")},["op"])
        ,Tool("pao_dotnet","Run a typed registered .NET operation.",new {op=new{type="string",@enum=new[]{"build","test","restore","clean","pack","publish"}},src=S("required solution or project path passed immediately after the operation"),dst=S("publish-only exact output directory mapped to --output"),cfg=S("configuration passed as --configuration"),arg=new{type="array",items=new{type="string"},maxItems=256},tmo=new{type="integer",minimum=1,maximum=3600,description="seconds"},ws=S("workspace alias selecting the build root"),cwd=S("working directory"),rid=S("rid")},["op","src"])
        ,Tool("pao_proc","List processes, inspect, wait, stop, enumerate a process tree, or observe thread identities. Thread listing is read-only and owner-host only.",new {op=new{type="string",@enum=new[]{"ls","inspect","wait","stop","tree","threads"}},pid=new{type="integer",minimum=1},start=S("canonical UTC process start identity required for wait/stop/threads"),tmo=new{type="integer",minimum=1,maximum=3600},max=new{type="integer",minimum=1,maximum=1024},pattern=S("case-insensitive process-name substring for ls"),root=R(),rid=S("rid")},["op"])
        ,Tool("pao_service","Control only the exact admitted Pegasus Cerebral Windows service through the root-authorized System broker.",new {op=new{type="string",@enum=new[]{"status","start","stop","restart"}},service=new{type="string",@enum=new[]{ApplicationAuthorizationProtocol.CerebralServiceName}},expect=new{type="string",@enum=new[]{"running","stopped"}},pid=new{type="integer",minimum=1},start=S("canonical UTC process start identity required for stop/restart"),tmo=new{type="integer",minimum=1,maximum=120},root=R(),rid=S("rid")},["op","service","root"])
        ,Tool("pao_net","List bounded local TCP and UDP listeners.",new {op=new{type="string",@enum=new[]{"list"}},port=new{type="integer",minimum=1,maximum=65535},max=new{type="integer",minimum=1,maximum=1024},root=R(),rid=S("rid")},["op"])
        ,Tool("pao_7z","List, test, or safely extract through the registered 7-Zip adapter.",new {op=new{type="string",@enum=new[]{"list","test","extract"}},src=S("archive"),dst=S("extract destination"),pol=new{type="string",@enum=new[]{"fail","replace","skip","clean"}},ws=S("workspace"),cwd=S("cwd"),rid=S("rid")},["op","src"])
        ,Tool("pao_do","Run one registered typed recipe.",new {rec=S("recipe name"),vars=S("bounded JSON object of typed variable values"),cwd=S("cwd"),rid=S("rid")},["rec"])
        ,Tool("pao_feedback","Submit, review, filter, or export durable local AgentOps feedback.",new {op=new{type="string",@enum=new[]{"submit","ls","get","set","export"}},fid=S("feedback id"),kind=new{type="string",@enum=new[]{"bug","feature","improvement","docs","usability","performance","other"}},area=S("component area"),summary=S("single-line summary up to 512 UTF-8 bytes"),notes=S("optional detail up to 8192 UTF-8 bytes; never include secrets"),status=new{type="string",@enum=new[]{"open","planned","deferred","done","declined"}},after=new{type="integer",minimum=0},max=new{type="integer",minimum=1,maximum=FeedbackOperations.MaxPageSize},rid=S("rid")},["op"])
        ,Tool("pao_help","Return the exact bounded public contract for one unified operation without contacting the service.",new {name=S("advertised unified operation name"),rid=S("optional local correlation id")},["name"])
    };
}


internal static class TerminalToolCatalog
{
    internal static string[] PublicFields(string op) => [..TerminalContract.Fields(op),
        ..(TerminalContract.HasBody(op) ? new[] { "text" } : Array.Empty<string>()), "detail", "output"];

    internal static bool Valid(IReadOnlyDictionary<string,string> values, string? body, bool requireBody = true) =>
        TerminalContract.Validate(values.Where(p=>p.Key is not("detail" or "quiet")).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal),body,requireBody);

    internal static bool Parse(JsonElement args,out Dictionary<string,string> values,out string? body)
    {
        values=new(StringComparer.Ordinal);body=null;
        if(args.ValueKind!=JsonValueKind.Object||args.EnumerateObject().GroupBy(p=>p.Name,StringComparer.Ordinal).Any(g=>g.Count()!=1)||
           !args.TryGetProperty("op",out var selector)||selector.ValueKind!=JsonValueKind.String) return false;
        var op=selector.GetString()!;
        if(!TerminalContract.Operations.Contains(op,StringComparer.Ordinal)) return false;
        var allowed=PublicFields(op);
        foreach(var p in args.EnumerateObject())
        {
            if(!allowed.Contains(p.Name,StringComparer.Ordinal))return false;
            if(p.Name=="text"){if(p.Value.ValueKind!=JsonValueKind.String)return false;body=p.Value.GetString();continue;}
            if(p.Name=="detail"){if(p.Value.ValueKind is not(JsonValueKind.True or JsonValueKind.False))return false;values[p.Name]=p.Value.GetBoolean().ToString();continue;}
            if(p.Value.ValueKind==JsonValueKind.Number&&p.Name is "cursor" or "max" or "width" or "height")
            {if(!p.Value.TryGetInt64(out var number))return false;values[p.Name]=number.ToString(System.Globalization.CultureInfo.InvariantCulture);}
            else {if(p.Value.ValueKind!=JsonValueKind.String)return false;values[p.Name]=p.Value.GetString()!;}
        }
        return Valid(values,body);
    }

    internal static IReadOnlyDictionary<string,object?> Help(string action,string op,string[] fields)
    {
        var required=op=="list"?Array.Empty<string>():op=="connect"?new[]{"name"}:op is "send" or "input"?new[]{"sid","text"}:
            op=="repeat"?new[]{"sid","cid"}:op=="resize"?new[]{"sid","width","height"}:new[]{"sid"};
        var help=new Dictionary<string,object?>{
            ["name"]=action,["allowed"]=fields,["required"]=required,
            ["constraints"]="Local signed-in unelevated owner host only. Real Windows ConPTY; PowerShell7 profile. Host-lifetime RAM: 8 active/32 retained sessions, 1MiB output/session, 64 saved NONSECRET commands/256 attempts. list returns only bounded owned sid/name/state metadata; detail adds cached PID/start. No disk transcript or job recovery; Host restart/reboot ends these shells. Read max4..65536 bytes(default4096); raw base64 preserves bytes. Closed/exited generations can be explicitly release-d to reclaim capacity; active/unknown generations cannot. Screen projection unsupported. send remembers nonsecret single-line text; input is unremembered; CLI send/input use --stdin. Reads/reconnect never send input. repeat cid creates a new attempt. Input delivered is not completion. cwd is not shell confinement. Root/elevation/remote unavailable.",
            ["mcp_output"]="Requested payload only, including default/structured mode; no routine receipt or preheader.",
            ["cli_example"]="pao terminal "+op+(op=="list"?"":op=="connect"?" NAME cwd=C:\\work":" SID")+(TerminalContract.HasBody(op)?" --stdin":"")
        };
        if(op=="list")help["mcp_example"]=new{op=action};
        return help;
    }
}
