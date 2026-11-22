using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class Program
{
    private static readonly List<object> Facts = [];
    private static int _checks;
    private static void Check(bool ok, string id) { _checks++; if (!ok) throw new InvalidOperationException(id); }
    private static JsonElement Data(OperationReceipt r) => JsonSerializer.SerializeToElement(r.Data);
    private static string S(JsonElement e, string key) => e.GetProperty(key).GetString()!;
    private static string Q(string path) => "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static async Task<int> Main(string[] args)
    {
        if (args is not [var root] || !Path.IsPathFullyQualified(root) ||
            !Path.GetFullPath(root).StartsWith(@"C:\CodexProjects\Pegasus Cortex\artifacts\validation\pterm-81be\", StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(root)) return 2;
        Directory.CreateDirectory(root);
        try
        {
            BufferAndContract();
            await LostReplyAsync();
            await RuntimeAsync(root);
            await HostLifetimeAsync(root);
            var report = new { schema = "pegasus.pao-terminal-focused-results/1", status = "pass", checks = _checks,
                facts = Facts, boundaries = "Source/isolated fixtures only. Real ConPTY and local lost-reply pipe fixture; no installed, remote, elevated or service acceptance." };
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(new { status = "pass", checks = _checks, report = Path.Combine(root, "result.json") }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "failure.json"), JsonSerializer.Serialize(new { status = "failed", checks = _checks, facts = Facts, error = ex.GetType().Name, stage = ex.Message }));
            Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", error = ex.GetType().Name, stage = ex.Message }));
            return 1;
        }
    }

    private static void BufferAndContract()
    {
        var b = new TerminalOutputBuffer(8);
        var bytes = Encoding.UTF8.GetBytes("abcd0123456789");
        b.Append(bytes, bytes.Length);
        var raw = JsonSerializer.SerializeToElement(b.Read(0, 8, true));
        Check(raw.GetProperty("gap").GetBoolean() && S(raw, "base64") == Convert.ToBase64String(bytes[^8..]), "ring-exact-byte-eviction");
        var utf = new TerminalOutputBuffer();
        utf.Append([0xf0, 0x9f], 2);
        Check((long)utf.Read(0, 4, false)["cursor"]! == 0, "incomplete-scalar-held");
        utf.End();
        var eof = utf.Read(0, 4, false);
        Check((long)eof["cursor"]! == 2 && (bool)eof["encodingLoss"]!, "incomplete-eof-advances");
        var complete = new TerminalOutputBuffer();
        complete.Append(Encoding.UTF8.GetBytes("A😀Z"), 6);
        var first = complete.Read(0, 4, false);
        Check((string)first["text"]! == "A" && (long)first["cursor"]! == 1, "scalar-read-boundary");
        Check((string)complete.Read(1, 4, false)["text"]! == "😀", "scalar-resume");
        var sid = new string('a', 32) + ":" + new string('b', 32);
        Check(TerminalContract.Validate(new Dictionary<string,string>{{"op","list"}},null), "list-without-sid");
        Check(!TerminalContract.Validate(new Dictionary<string,string>{{"op","list"},{"sid",sid}},null), "list-rejects-target-selector");
        Check(!TerminalContract.Validate(new Dictionary<string,string>{{"op","list"}},"unexpected"), "list-rejects-body");
        Check(!TerminalContract.Validate(new Dictionary<string,string>{{"op","send"},{"sid",sid}},"bad\ncommand"), "single-line-remembered");
        Check(!TerminalContract.Validate(new Dictionary<string,string>{{"op","read"},{"sid",sid},{"root","ABCD"}},null), "closed-terminal-schema");
        foreach (var op in new[] { "list", "read", "status", "recall", "input_status", "screen" })
            Check(!AgentOpsEngine.IsMutation(new("terminal",new Dictionary<string,string>{{"op",op}})), "read-classification-"+op);
        Check(AgentOpsEngine.IsMutation(new("terminal",new Dictionary<string,string>{{"op","unknown"}})), "unknown-effect-fail-closed");
        var catalog=PaoProgram.UnifiedMcpToolDefinition();
        Check(catalog["inputSchema"]!["properties"]!["op"]!["enum"]!.AsArray().Any(value=>value!.GetValue<string>()=="terminal_list"),
            "list-advertised-through-existing-unified-tool");
        var help=PaoProgram.UnifiedMcpOperationHelp("terminal_list")!;
        Check(help["required"]!.AsArray().Count==0&&help["allowed"]!.AsArray().All(value=>value!.GetValue<string>()!="sid")&&
            help["cli_example"]!.GetValue<string>()=="pao terminal list"&&
            help["mcp_example"]!["op"]!.GetValue<string>()=="terminal_list", "list-help-has-executable-no-sid-examples");
        Facts.Add(new { group = "deterministic-buffer-and-contract", exactBytes = true, utf8EofProgress = true, readClassification = true });
    }

    private static async Task LostReplyAsync()
    {
        var pipeName = "pao-terminal-fixture-" + Guid.NewGuid().ToString("N");
        string? capturedRid = null;
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var receive = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var request = await BinaryProtocol.ReadAsync<OperationRequest>(server);
            capturedRid = request.Rid;
            server.Disconnect(); // Accept the frame, lose the acknowledgement; execute nothing.
        });
        await using var client = new NamedPipeDispatcher(pipeName, expectedServer: LocalIdentity.Current());
        var sid = new string('a',32)+":"+new string('b',32);
        var result = await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new {
            op="terminal_send",sid,text="fixture-only-never-executed" }), client, CancellationToken.None);
        await receive;
        var payload = JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;
        Check(result["structuredContent"] is null && result["isError"]!.GetValue<bool>(), "loss-minimal-mcp-wrapper");
        Check(capturedRid is not null && payload["original_rid"]!.GetValue<string>() == capturedRid &&
            payload["delivery"]!.GetValue<string>() == "unknown", "loss-preserves-client-generated-rid");
        Facts.Add(new { group="real-local-pipe-lost-reply-fixture", requests=1, inputExecuted=false,
            stableRecoveryRid=true, unknownExplicit=true });
    }

    private static async Task RuntimeAsync(string root)
    {
        var state = Path.Combine(root, "engine");
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(work);
        await PowerShell7ToolBootstrap.EnsureAsync(state); // Definition/probe in this fixture only.
        var mode = new Mode();
        var engine = new AgentOpsEngine(state,null,false,null,true,null,maintenanceMode:mode);
        var ownedSids = new HashSet<string>(StringComparer.Ordinal);
        Exception? primaryFailure = null;
        try
        {
        var dispatcher = new EngineDispatcher(engine);
        async Task<OperationReceipt> Call(string op,string? sid=null,string? text=null,string? rid=null,params (string,string)[] extra)
        {
            var fields = new Dictionary<string,string>{{"op",op}};
            if(sid is not null)fields["sid"]=sid;
            foreach(var (k,v) in extra)fields[k]=v;
            var receipt = await engine.ExecuteAsync(new("terminal",fields,Rid:rid??Guid.NewGuid().ToString("N"),Cwd:work,Body:text));
            if (receipt.Ok && op == "connect") ownedSids.Add(S(Data(receipt), "sid"));
            if (receipt.Ok && op == "release" && sid is not null) ownedSids.Remove(sid);
            return receipt;
        }
        async Task<JsonElement> Ok(string op,string? sid=null,string? text=null,string? rid=null,params (string,string)[] extra)
        {
            var receipt=await Call(op,sid,text,rid,extra);
            Check(receipt.Ok,op+":"+(receipt.Err??"ok"));
            return Data(receipt);
        }

        Check((await Ok("list")).GetProperty("sessions").GetArrayLength()==0,"empty-owner-list");
        var invalidListDispatcher=new CountDispatcher();
        var invalidList=await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new {
            op="terminal_list",sid=new string('a',32)+":"+new string('b',32) }),invalidListDispatcher,CancellationToken.None);
        Check(invalidList["isError"]!.GetValue<bool>()&&invalidListDispatcher.Calls==0,"list-extra-target-never-dispatched");
        Check(await PaoProgram.RunCliAsync(["terminal","list","unexpected-sid"],invalidListDispatcher,
            new StringWriter(),new StringWriter(),CancellationToken.None)==2&&invalidListDispatcher.Calls==0,
            "cli-list-extra-positional-never-dispatched");
        var wire=await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new {
            op="terminal_connect",name="live",cwd=work,width=100,height=30 }),dispatcher,CancellationToken.None);
        Check(wire["structuredContent"] is null&&!wire["isError"]!.GetValue<bool>(),"live-mcp-simple");
        var opened=JsonDocument.Parse(wire["content"]![0]!["text"]!.GetValue<string>()).RootElement.Clone();
        var sid=S(opened,"sid");
        ownedSids.Add(sid);
        Check(opened.EnumerateObject().Count()==2&&S(opened,"state")=="connected","connect-payload-only");
        var detail=Data(await engine.ExecuteAsync(new("terminal",new Dictionary<string,string>{{"op","status"},{"sid",sid}},Detail:true)));
        var pid=detail.GetProperty("pid").GetInt32();
        Check(S(detail,"provider")=="windows-conpty"&&!detail.GetProperty("screenSupported").GetBoolean(),"real-provider-and-screen-boundary");
        var inventory=await Ok("list");
        var inventoryRows=inventory.GetProperty("sessions");
        Check(inventory.EnumerateObject().Count()==1&&inventoryRows.GetArrayLength()==1&&
            inventoryRows[0].EnumerateObject().Count()==3&&S(inventoryRows[0],"sid")==sid&&
            S(inventoryRows[0],"name")=="live"&&S(inventoryRows[0],"state")=="connected", "list-only-owned-session-metadata");
        var listWire=await PaoProgram.CallUnifiedMcpToolAsync(JsonSerializer.SerializeToElement(new {
            op="terminal_list",detail=true }),dispatcher,CancellationToken.None);
        var listDetail=JsonDocument.Parse(listWire["content"]![0]!["text"]!.GetValue<string>()).RootElement.GetProperty("sessions")[0];
        Check(listWire["structuredContent"] is null&&!listWire["isError"]!.GetValue<bool>()&&
            listDetail.EnumerateObject().Count()==5&&listDetail.GetProperty("pid").GetInt32()==pid&&
            S(listDetail,"start")==S(detail,"start"), "mcp-list-detail-is-cached-exact-generation");
        var marker=Guid.NewGuid().ToString("N");
        var promptMarker="PROMPT"+marker;
        var prepareCommand="$global:PaoCounter=0; $env:PAO_TERMINAL_VALUE='prepared'; function global:Get-PaoPrepared { 'function-ready' }; function global:prompt { 'PROMPT'+'"+marker+"' }; Set-Location "+Q(work)+"; [Console]::WriteLine(('READY'+'"+marker+"'))";
        var prepared=await Ok("send",sid,prepareCommand,"setup-rid");
        long cursor=0;
        async Task WaitFor(string stage, string expected)
        {
            var accumulated=new StringBuilder();
            var timer=Stopwatch.StartNew();
            var reads=0;var gap=false;var encodingLoss=false;var ended=false;
            while(timer.Elapsed<TimeSpan.FromSeconds(20))
            {
                var part=await Ok("read",sid,null,null,("cursor",cursor.ToString()),("max","65536"));
                reads++;
                cursor=part.GetProperty("cursor").GetInt64();
                gap|=part.TryGetProperty("gap",out var g)&&g.GetBoolean();
                encodingLoss|=part.TryGetProperty("encodingLoss",out var loss)&&loss.GetBoolean();
                ended|=part.TryGetProperty("ended",out var end)&&end.GetBoolean();
                accumulated.Append(S(part,"text"));
                var currentText=accumulated.ToString();
                if(currentText.Contains(expected,StringComparison.Ordinal))
                {
                    if(stage=="sleep-started")
                    {
                        bool? Flag(string prefix)
                        {
                            var yes=currentText.Contains(prefix+"True:"+marker,StringComparison.Ordinal);
                            var no=currentText.Contains(prefix+"False:"+marker,StringComparison.Ordinal);
                            return yes==no?null:yes;
                        }
                        var controlInput=Flag("CTRL_MODE:");
                        var inputRedirected=Flag("INPUT_REDIRECTED:");
                        Facts.Add(new{group="interrupt-input-mode",treatControlCAsInput=controlInput,isInputRedirected=inputRedirected,
                            modeObserved=controlInput.HasValue,inputRedirectedObserved=inputRedirected.HasValue});
                    }
                    return;
                }
                await Task.Delay(75);
            }
            // Only synthetic-marker facts and a digest are retained. Terminal
            // text is never written to the fixture report, including on failure.
            var observed=accumulated.ToString();
            var bytes=Encoding.UTF8.GetBytes(observed);
            object statusMetadata;
            try
            {
                var status=await engine.ExecuteAsync(new("terminal",new Dictionary<string,string>{{"op","status"},{"sid",sid}},Detail:true));
                var data=status.Ok?Data(status):default;
                statusMetadata=new {ok=status.Ok,error=status.Err,
                    state=status.Ok?S(data,"state"):null,
                    pid=status.Ok?data.GetProperty("pid").GetInt32():(int?)null,
                    start=status.Ok?S(data,"start"):null,
                    issue=status.Ok&&data.TryGetProperty("issue",out var issue)?issue.GetString():null};
            }
            catch(Exception ex){statusMetadata=new {errorType=ex.GetType().Name};}
            Facts.Add(new {group="runtime-marker-timeout",stage,expectedMarker=expected,
                elapsedMilliseconds=timer.ElapsedMilliseconds,reads,cursor,gap,encodingLoss,ended,
                outputCharacters=observed.Length,outputUtf8Bytes=bytes.Length,
                outputSha256=Convert.ToHexStringLower(SHA256.HashData(bytes)),
                knownMarkers=new {
                    ready=observed.Contains("READY"+marker,StringComparison.Ordinal),
                    state=observed.Contains("STATE:prepared:"+work+":False",StringComparison.Ordinal),
                    function=observed.Contains("FUNCTION:function-ready",StringComparison.Ordinal),
                    sleep=observed.Contains("SLEEP"+marker,StringComparison.Ordinal),
                    prompt=observed.Contains(promptMarker,StringComparison.Ordinal),
                    interrupted=observed.Contains("INTERRUPTED"+marker,StringComparison.Ordinal),
                    runOne=observed.Contains("RUN_ONE:1",StringComparison.Ordinal),
                    raw=observed.Contains("RAW"+marker,StringComparison.Ordinal),
                    runTwo=observed.Contains("RUN_TWO:2",StringComparison.Ordinal)},status=statusMetadata});
            throw new InvalidOperationException("expected-runtime-marker-not-observed");
        }
        await WaitFor("prepared-ready","READY"+marker);
        await Ok("send",sid,"[Console]::WriteLine(('CAPTURED:'+$env:PEGASUS_AGENTOPS_CAPTURED+':'+'"+marker+"'))");
        await WaitFor("captured-output-fence","CAPTURED:1:"+marker);
        Check(true,"real-conpty-fixed-captured-output-marker");
        await Ok("send",sid,"[Console]::WriteLine(('STATE:'+$env:PAO_TERMINAL_VALUE+':'+(Get-Location).Path+':'+[Console]::IsOutputRedirected))");
        await WaitFor("prepared-state","STATE:prepared:"+work+":False");
        await Ok("disconnect",sid);
        Check(S((await Ok("list")).GetProperty("sessions")[0],"state")=="disconnected", "list-does-not-reconnect-detached-session");
        var duplicate=await Ok("send",sid,prepareCommand,"setup-rid");
        Check(S(duplicate,"input")==S(prepared,"input"),"detached-known-rid-not-reexecuted");
        var blocked=await Call("send",sid,"Write-Output 'must-not-run'");
        Check(!blocked.Ok&&blocked.Err=="terminal_disconnected","detached-new-input-refused");
        await Ok("reconnect",sid);
        var same=Data(await engine.ExecuteAsync(new("terminal",new Dictionary<string,string>{{"op","status"},{"sid",sid}},Detail:true)));
        Check(same.GetProperty("pid").GetInt32()==pid&&S(same,"start")==S(detail,"start"),"same-process-after-detach");
        Facts.Add(new {group="prepared-state",pid,preparedEnvironment="prepared",preparedCwd=work,consoleOutputRedirected=false,sameProcessAfterDetach=true});
        var stdout=new StringWriter();var stderr=new StringWriter();
        Check(await PaoProgram.RunCliAsync(["--json","terminal","status",sid],dispatcher,stdout,stderr,CancellationToken.None)==0,"cli-status-success");
        var cli=JsonDocument.Parse(stdout.ToString()).RootElement;
        Check(cli.EnumerateObject().Count()==1&&S(cli,"state")=="connected","cli-json-minimal");
        var listOut=new StringWriter();var listErr=new StringWriter();
        Check(await PaoProgram.RunCliAsync(["--json","terminal","list"],dispatcher,listOut,listErr,CancellationToken.None)==0&&
            listErr.ToString().Length==0,"cli-list-without-positional-sid");
        var listedCli=JsonDocument.Parse(listOut.ToString()).RootElement;
        Check(listedCli.EnumerateObject().Count()==1&&S(listedCli.GetProperty("sessions")[0],"sid")==sid,
            "cli-list-minimal-payload-no-receipt");

        await Ok("send",sid,"[Console]::WriteLine(('FUNCTION:'+(Get-PaoPrepared)))");
        await WaitFor("preserved-function","FUNCTION:function-ready");
        using(var canceled=new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await engine.ExecuteAsync(new("terminal",new Dictionary<string,string>{{"op","read"},{"sid",sid}}),canceled.Token); Check(false,"read-cancellation-observed"); }
            catch(OperationCanceledException) { Check(true,"read-cancellation-observed"); }
        }
        await Ok("send",sid,"[Console]::WriteLine(('CTRL_MODE:'+[Console]::TreatControlCAsInput+':'+'"+marker+"')); "+
            "[Console]::WriteLine(('INPUT_REDIRECTED:'+[Console]::IsInputRedirected+':'+'"+marker+"')); "+
            "[Console]::WriteLine(('SLEEP'+'"+marker+"')); Start-Sleep -Seconds 15");
        await WaitFor("sleep-started","SLEEP"+marker);
        var interruptReadinessCursor=cursor;
        var interruptTimer=Stopwatch.StartNew();
        var interruptReceipt=await Ok("interrupt",sid);
        var interruptDeliveryMilliseconds=interruptTimer.ElapsedMilliseconds;
        // The earlier diagnostic run reached this pipeline but did not observe
        // the next command's marker. Delivery does not prove prompt readiness.
        // SLEEP consumed all preceding prompt bytes; this fresh read starts beyond
        // that cursor, so an old prompt cannot release the next command.
        await WaitFor("interrupt-prompt-ready",promptMarker);
        var promptCursor=cursor;
        var interruptPromptMilliseconds=interruptTimer.ElapsedMilliseconds;
        Check(promptCursor>interruptReadinessCursor,"interrupt-prompt-is-fresh");
        await Ok("send",sid,"[Console]::WriteLine(('INTERRUPTED'+'"+marker+"'))");
        await WaitFor("interrupt-returned","INTERRUPTED"+marker);
        Facts.Add(new{group="interrupt-observation",delivery=S(interruptReceipt,"delivery"),
            deliveryElapsedMilliseconds=interruptDeliveryMilliseconds,promptElapsedMilliseconds=interruptPromptMilliseconds,
            elapsedMilliseconds=interruptTimer.ElapsedMilliseconds,interruptReadinessCursor,promptCursor,returnedCursor=cursor,
            freshPromptObserved=promptCursor>interruptReadinessCursor});
        Check(interruptTimer.Elapsed<TimeSpan.FromSeconds(8),"interrupt-canceled-running-pipeline");
        Facts.Add(new{group="prepared-function-and-observation-cancellation",functionValue="function-ready",canceledReadPreservedSession=true,interruptPreservedShell=true,
            interruptReadinessCursor,promptCursor,interruptReturnedCursor=cursor,interruptElapsedMilliseconds=interruptTimer.ElapsedMilliseconds,
            freshPromptObserved=true,interruptDeliveryAloneNotTreatedAsReadiness=true});
        var script=Path.Combine(work,"fixture.ps1");
        var ledger=Path.Combine(work,"ledger.txt");
        await File.WriteAllTextAsync(script,"Add-Content -LiteralPath "+Q(ledger)+" -Value 'one'; [Console]::WriteLine(('RUN_ONE:'+$global:PaoCounter))");
        var command="$global:PaoCounter++; & '.\\fixture.ps1'";
        var firstRun=await Ok("send",sid,command,"run-first");
        await WaitFor("first-script","RUN_ONE:1");
        await Ok("input",sid,"[Console]::WriteLine(('RAW'+'"+marker+"'))\r");
        await WaitFor("unremembered-input","RAW"+marker);
        var recalled=await Ok("recall",sid);
        Check(S(recalled,"cid")==S(firstRun,"cid")&&S(recalled,"text")==command,"saved-command-survives-unremembered-input");
        var lastInput=await Ok("input_status",sid);
        Check(!lastInput.TryGetProperty("text",out _)&&!lastInput.TryGetProperty("cid",out _),"raw-input-not-retained");
        await File.WriteAllTextAsync(script,"Add-Content -LiteralPath "+Q(ledger)+" -Value 'two'; [Console]::WriteLine(('RUN_TWO:'+$global:PaoCounter))");
        var repeat=await Ok("repeat",sid,null,"run-repeat",("cid",S(firstRun,"cid")));
        await WaitFor("updated-script-repeat","RUN_TWO:2");
        Check(S(repeat,"input")!=S(firstRun,"input")&&S(repeat,"cid")==S(firstRun,"cid"),"repeat-new-attempt-stored-reference");
        var repeated=await Ok("recall",sid);
        Check(S(repeated,"reRunOf")==S(firstRun,"input")&&S(repeated,"completion")=="unknown","repeat-provenance-not-completion");
        var old=await Ok("input_status",sid,null,null,("original_rid","run-first"));
        Check(S(old,"input")==S(firstRun,"input"),"exact-rid-recovery-after-other-input");
        var before=await File.ReadAllLinesAsync(ledger);
        for(var i=0;i<4;i++)await Ok("read",sid,null,null,("cursor",cursor.ToString()));
        var after=await File.ReadAllLinesAsync(ledger);
        Check(before.SequenceEqual(new[]{"one","two"})&&after.SequenceEqual(before),"updated-file-repeat-and-reads-no-replay");
        Facts.Add(new {group="saved-reference-repeat",ledgerBefore=before,ledgerAfterReads=after,firstInput=S(firstRun,"input"),repeatInput=S(repeat,"input"),storedCommand=S(firstRun,"cid"),reRunOf=S(repeated,"reRunOf")});
        var missing=await Ok("input_status",sid,null,null,("original_rid","not-submitted"));
        Check(S(missing,"state")=="missing","explicit-missing-rid");
        Check((await Call("input_status",sid,null,null,("original_rid","run-first"),("iid",S(firstRun,"input")))).Err=="invalid","ambiguous-recovery-selector-rejected");

        mode.Held=true;
        Check((await Call("send",sid,"Write-Output 'held'")).Err=="maintenance_busy","held-input-refused");
        await Ok("read",sid,null,null,("cursor",cursor.ToString()));
        await Ok("status",sid);
        await Ok("recall",sid);
        Check((await Ok("list")).GetProperty("sessions").GetArrayLength()==1,"list-remains-read-only-while-maintenance-held");
        await Ok("close",sid);
        Check(ProcessAbsent(pid),"closed-original-process-exited");
        Check(S((await Ok("list")).GetProperty("sessions")[0],"state")=="closed", "closed-generation-remains-discoverable-for-release");
        Facts.Add(new {group="close-proof",pid,originalProcessAbsent=true});
        var recovered=await Ok("input_status",sid,null,null,("original_rid","run-first"));
        Check(S(recovered,"input")==S(firstRun,"input"),"closed-input-recovery");
        mode.Held=false;
        var reopenedName=await Ok("connect",null,null,null,("name","live"));
        var replacementSid=S(reopenedName,"sid");
        var reused=(await Ok("list")).GetProperty("sessions").EnumerateArray().ToArray();
        Check(replacementSid!=sid&&reused.Length==2&&reused.All(row=>S(row,"name")=="live")&&
            reused.Any(row=>S(row,"sid")==sid&&S(row,"state")=="closed")&&
            reused.Any(row=>S(row,"sid")==replacementSid&&S(row,"state")=="connected"),
            "reused-name-does-not-hide-retained-original-generation");
        await Ok("release",sid);
        Check((await Ok("list")).GetProperty("sessions").GetArrayLength()==1&&
            S((await Ok("list")).GetProperty("sessions")[0],"sid")==replacementSid,"release-removes-only-selected-generation");
        Check(S(await Ok("connect",null,null,null,("name","live")),"sid")==replacementSid,
            "releasing-old-name-generation-preserves-current-reattachment");
        await Ok("close",replacementSid);await Ok("release",replacementSid);
        Check((await Ok("list")).GetProperty("sessions").GetArrayLength()==0,"released-generations-free-owned-inventory");
        var next=await Ok("connect",null,null,null,("name","after-release"));
        var sid2=S(next,"sid");
        Check(sid2!=sid,"release-allows-new-generation");
        Check((await Call("status",sid)).Err=="terminal_missing","released-handle-never-replays");
        await Ok("close",sid2);
        var jobs=await engine.ExecuteAsync(new("job",new Dictionary<string,string>{{"op","ls"},{"max","100"}}));
        Check(Data(jobs).GetProperty("jobs").GetArrayLength()==0,"terminal-no-durable-job-rows");
        var rejected=await engine.ExecuteAuthenticatedFrozenDirectAsync(new("terminal",new Dictionary<string,string>{{"op","status"},{"sid",sid2}}),allowApplications:true);
        Check(rejected.Err=="user_dispatch_required","direct-frozen-terminal-refused");
        var user=new CountDispatcher();var service=new CountDispatcher();var system=new CountDispatcher();
        await using var routed=new RoutingDispatcher(user,service,system);
        var rootRequest=await routed.DispatchAsync(new("terminal",new Dictionary<string,string>{{"op","status"},{"sid",sid2}},Root:"ABCD"));
        Check(!rootRequest.Ok&&system.Calls==0&&service.Calls==0&&user.Calls==0,"root-terminal-no-dispatch");
        Facts.Add(new { group="real-source-engine-conpty", pid, sameProcessAcrossDetach=true, preparedEnvironment="prepared",
            preparedCwd=work, consoleOutputRedirected=false, ledgerBefore=before,ledgerAfterReads=after,
            distinctRepeatAttempt=true, exactOldRidRecovered=true, closedOriginalExited=true, releaseReusable=true,
            maintenanceRefusal=true, durableJobRows=0, minimalMcp=true,minimalCli=true, genericFrozenDenied=true, rootNeverDispatched=true });
        }
        catch(Exception ex){primaryFailure=ex;throw;}
        finally
        {
            var cleanupComplete=false;
            try{cleanupComplete=await CleanupOwnedTerminalsAsync(engine,ownedSids);}
            catch(Exception ex)
            {
                Facts.Add(new {group="fixture-owned-terminal-cleanup-error",errorType=ex.GetType().Name});
                if(primaryFailure is null)throw;
            }
            if(!cleanupComplete&&primaryFailure is null)
                throw new InvalidOperationException("fixture-owned-terminal-cleanup-incomplete");
        }
    }

    private static async Task<bool> CleanupOwnedTerminalsAsync(AgentOpsEngine engine, HashSet<string> knownOwnedSids)
    {
        var complete=true;var inventoryObserved=false;var disposed=false;
        var outcomes=new List<object>();
        try
        {
            try
            {
                var list=await engine.ExecuteAsync(new("terminal",new Dictionary<string,string>{{"op","list"}}));
                inventoryObserved=list.Ok;
                if(!list.Ok){complete=false;outcomes.Add(new {operation="list",ok=false,error=list.Err});}
                else
                {
                    var sessions=Data(list).GetProperty("sessions");
                    if(sessions.GetArrayLength()>32){complete=false;outcomes.Add(new {operation="list",error="fixture-inventory-bound-exceeded"});}
                    foreach(var session in sessions.EnumerateArray().Take(32))knownOwnedSids.Add(S(session,"sid"));
                }
            }
            catch(Exception ex){complete=false;outcomes.Add(new {operation="list",errorType=ex.GetType().Name});}
            if(knownOwnedSids.Count>32)complete=false;
            foreach(var sid in knownOwnedSids.Order(StringComparer.Ordinal).Take(32))
            {
                // The fresh fixture engine is the only source of these SIDs.
                // No PID/name lookup, input resend, or ownership adoption occurs.
                var closed=false;var released=false;string? closeError=null;string? releaseError=null;
                try
                {
                    var close=await engine.ExecuteAsync(new("terminal",new Dictionary<string,string>{{"op","close"},{"sid",sid}}));
                    closed=close.Ok;closeError=close.Err;
                }
                catch(Exception ex){closeError=ex.GetType().Name;}
                try
                {
                    var release=await engine.ExecuteAsync(new("terminal",new Dictionary<string,string>{{"op","release"},{"sid",sid}}));
                    released=release.Ok;releaseError=release.Err;
                }
                catch(Exception ex){releaseError=ex.GetType().Name;}
                complete&=closed&&released;
                outcomes.Add(new {sid,closed,closeError,released,releaseError});
            }
        }
        finally
        {
            try{await engine.DisposeAsync();disposed=true;}
            catch(Exception ex){complete=false;outcomes.Add(new {operation="dispose",errorType=ex.GetType().Name});}
            Facts.Add(new {group="fixture-owned-terminal-cleanup",inventoryObserved,disposed,complete,outcomes});
        }
        return complete;
    }

    private static async Task HostLifetimeAsync(string root)
    {
        var state=Path.Combine(root,"host-lifetime");
        await PowerShell7ToolBootstrap.EnsureAsync(state);
        var mode=new Mode();
        int pid;string sid;
        await using(var host=new AgentOpsEngine(state,null,false,null,true,null,maintenanceMode:mode))
        {
            var open=await host.ExecuteAsync(new("terminal",new Dictionary<string,string>{{"op","connect"},{"name","owned"}},Cwd:root));
            Check(open.Ok,"host-dispose-fixture-connect");
            sid=S(Data(open),"sid");
            var status=await host.ExecuteAsync(new("terminal",new Dictionary<string,string>{{"op","status"},{"sid",sid}},Detail:true));
            pid=Data(status).GetProperty("pid").GetInt32();
            Check(!ProcessAbsent(pid),"host-owned-shell-started");
        }
        Check(ProcessAbsent(pid),"host-disposal-ends-owned-shell");
        await using(var restarted=new AgentOpsEngine(state,null,false,null,true,null,maintenanceMode:mode))
        {
            var inventory=await restarted.ExecuteAsync(new("terminal",new Dictionary<string,string>{{"op","list"}}));
            Check(inventory.Ok&&Data(inventory).GetProperty("sessions").GetArrayLength()==0,"new-owner-has-no-restored-terminal-generation");
            var old=await restarted.ExecuteAsync(new("terminal",new Dictionary<string,string>{{"op","status"},{"sid",sid}}));
            Check(!old.Ok&&old.Err=="terminal_owner_lost","old-host-handle-never-adopts-or-relaunches-a-shell");
        }
        Facts.Add(new{group="real-owner-host-disposal",pid,ownedProcessAbsent=true});
    }

    private static bool ProcessAbsent(int pid)
    { try { using var p=Process.GetProcessById(pid); return p.HasExited; } catch(ArgumentException){return true;} }
    private sealed class Mode:IMaintenanceModeSource
    {
        internal bool Held;
        public MaintenanceModeRecord? Read()=>Held?new(1,"fixture","active","j","r","s","t","owner",1,"service",null,1):null;
        public bool IsAcknowledged(MaintenanceEffect effect)=>false;
    }
    private sealed class EngineDispatcher(AgentOpsEngine engine):IOperationDispatcher
    {
        public Task<OperationReceipt> DispatchAsync(OperationRequest r,CancellationToken ct=default)=>engine.ExecuteAsync(r,ct);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class CountDispatcher:IOperationDispatcher
    {
        internal int Calls;
        public Task<OperationReceipt> DispatchAsync(OperationRequest r,CancellationToken ct=default)
        { Calls++;return Task.FromResult(new OperationReceipt(true,"j","r","done",r.Op)); }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
