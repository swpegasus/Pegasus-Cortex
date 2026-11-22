using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Mcp.Hosting;
using Pegasus.Cortex.Product;
using Pegasus.SharedMcp;

internal static class CortexMcpConfiguredLoopbackTests
{
    private const string Protocol="2025-06-18";
    private const string EchoValue="owned-loopback-value";
    private const int MaximumResponseBytes=32768;

    internal static async Task RunAsync(string explicitDevHostConfigPath)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Configured controller TLS fixture requires Windows.");
        var settings=ReadPublicConfiguration(explicitDevHostConfigPath) with { Port=SelectOneLoopbackPort() };
        var firstDispatcher=new FixtureDispatcher();
        var secondDispatcher=new FixtureDispatcher();
        var firstFactories=0;
        var secondFactories=0;
        CortexMcpHostController? first=null,second=null;
        HttpClient? http=null;
        string? session=null;
        var deleteAttempted=false;
        var firstReady=false;
        Exception? primary=null;
        var cleanup=new List<Exception>();
        using var lifetime=new CancellationTokenSource();
        using var firstStartup=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using var secondStartup=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            firstStartup.CancelAfter(TimeSpan.FromSeconds(20));
            first=await CortexMcpHostController.StartAsync(settings,CortexMcpOwner.Application,
                _=>{firstFactories++;return Task.FromResult<IMcpDispatcher>(firstDispatcher);},firstStartup.Token);
            firstStartup.CancelAfter(Timeout.InfiniteTimeSpan);
            var state=first.State;
            Check(state.Status==CortexMcpHostStatus.Ready&&state.EffectiveOwner==CortexMcpOwner.Application&&
                !state.PendingRestart&&state.Endpoint is { Scheme:"https" }&&state.Endpoint.Port==settings.Port&&
                state.Endpoint.AbsolutePath==CortexMcpHostController.EndpointPath,"first_controller_not_ready");
            firstReady=true;
            Check(state.TlsQualification is { Success:true,ValidUnderSelectedCa:true,
                ClientTrustObservation:"trusted_by_this_process_system_policy" },"selected_ca_or_system_trust_not_qualified");
            // Deliberately leave SslOptions and every certificate/connect callback untouched.
            // The ordinary Windows trust/name policy must accept the real HTTPS request.
            var handler=new SocketsHttpHandler
            {
                UseProxy=false,AllowAutoRedirect=false,UseCookies=false,ConnectTimeout=TimeSpan.FromSeconds(20)
            };
            http=new HttpClient(handler,disposeHandler:true) { Timeout=Timeout.InfiniteTimeSpan };
            var endpoint=state.Endpoint!;

            var initialized=await SendAsync(http,endpoint,null,HttpMethod.Post,Request("configured-loopback-init","initialize",new()
            {
                ["protocolVersion"]=Protocol,["capabilities"]=new JsonObject(),
                ["clientInfo"]=new JsonObject { ["name"]="controller-loopback-fixture",["version"]="1" }
            }),HttpStatusCode.OK,value=>session=value);
            RequireResult(initialized,JsonValue.Create("configured-loopback-init")!);
            Check(session is { Length:32 }&&session.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f')&&
                initialized!["result"]?["protocolVersion"]?.GetValue<string>()==Protocol,"initialize_session_or_protocol_invalid");
            await SendAsync(http,endpoint,session,HttpMethod.Post,Request(null,"notifications/initialized"),
                HttpStatusCode.Accepted);

            var listed=await SendAsync(http,endpoint,session,HttpMethod.Post,Request(731,"tools/list"),HttpStatusCode.OK);
            RequireCatalog(listed,731);
            var echoed=await SendAsync(http,endpoint,session,HttpMethod.Post,Request("configured-loopback-echo","tools/call",new()
            {
                ["name"]="fixture_echo",["arguments"]=new JsonObject { ["value"]=EchoValue }
            }),HttpStatusCode.OK);
            RequireResult(echoed,JsonValue.Create("configured-loopback-echo")!);
            var echoResult=echoed!["result"]!.AsObject();
            Check(echoResult["isError"]?.GetValue<bool>()==false&&
                echoResult["structuredContent"]?["value"]?.GetValue<string>()==EchoValue,"echo_result_invalid");

            // One real occupied-port attempt; no probe/adoption/kill/retry or alternative port.
            secondStartup.CancelAfter(TimeSpan.FromSeconds(20));
            second=await CortexMcpHostController.StartAsync(settings,CortexMcpOwner.Application,
                _=>{secondFactories++;return Task.FromResult<IMcpDispatcher>(secondDispatcher);},secondStartup.Token);
            secondStartup.CancelAfter(Timeout.InfiniteTimeSpan);
            var conflict=second.State;
            Check(conflict.Status==CortexMcpHostStatus.Unavailable&&conflict.Issue=="address_in_use"&&
                conflict.PendingRestart&&conflict.EffectiveOwner is null&&conflict.Endpoint is null,
                "second_controller_did_not_refuse_occupied_port");
            var afterConflict=await SendAsync(http,endpoint,session,HttpMethod.Post,Request(732,"tools/list"),HttpStatusCode.OK);
            RequireCatalog(afterConflict,732);
            Check(first.State.Status==CortexMcpHostStatus.Ready&&first.State.EffectiveOwner==CortexMcpOwner.Application,
                "conflict_disturbed_original_owner");
            Check(firstDispatcher.Initializations==1&&firstDispatcher.CatalogCalls==2&&firstDispatcher.EchoCalls==1&&
                secondDispatcher.Initializations==0&&secondDispatcher.CatalogCalls==0&&secondDispatcher.EchoCalls==0,
                "unexpected_dispatch_or_replay");

            deleteAttempted=true;
            await SendAsync(http,endpoint,session,HttpMethod.Delete,null,HttpStatusCode.NoContent);
            session=null;
        }
        catch(Exception error) { primary=error; }
        finally
        {
            // A prior successful DELETE is never replayed. On earlier failure,
            // make at most one control cleanup attempt before stopping the listener.
            if(http is not null&&first?.State.Endpoint is { } endpoint&&session is not null&&!deleteAttempted)
            {
                deleteAttempted=true;
                try { await SendAsync(http,endpoint,session,HttpMethod.Delete,null,HttpStatusCode.NoContent); }
                catch(Exception error) { cleanup.Add(error); }
            }
            try { http?.Dispose(); }catch(Exception error) { cleanup.Add(error); }
            try { if(second is not null)await second.DisposeAsync(); }catch(Exception error) { cleanup.Add(error); }
            try { if(first is not null)await first.DisposeAsync(); }catch(Exception error) { cleanup.Add(error); }
            try { lifetime.Cancel(); }catch(Exception error) { cleanup.Add(error); }
        }

        // All clients/sessions/controllers have closed before these checks or PASS.
        if(firstReady)
        {
            try
            {
                Check(first!.State.Status==CortexMcpHostStatus.Stopped&&first.Completion.IsCompletedSuccessfully&&
                    first.State.EffectiveOwner is null&&first.State.Endpoint is null,"first_controller_did_not_stop");
                await RequireNoListenerAsync(settings.Port);
            }
            catch(Exception error) { cleanup.Add(error); }
        }
        try
        {
            Check(firstFactories<=1&&secondFactories<=1&&firstDispatcher.Disposals==firstFactories&&
                secondDispatcher.Disposals==secondFactories,"owned_dispatcher_disposal_not_exact_once");
        }
        catch(Exception error) { cleanup.Add(error); }
        if(primary is not null)
        {
            if(cleanup.Count>0)throw new AggregateException("configured_loopback_failed_and_cleanup_failed",
                new[]{primary}.Concat(cleanup));
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if(cleanup.Count>0)throw new AggregateException("configured_loopback_cleanup_failed",cleanup);
        Check(firstReady&&firstFactories==1,"controller_fixture_did_not_run");
        Console.WriteLine("PASS configured shared MCP controller: real Windows store read + selected-CA TLS qualification + default-system-trust HTTPS initialize/initialized/catalog/echo/DELETE; occupied-port refusal/original-owner continuity/exact disposal/listener closed; injected fixture dispatcher, no product DB/service/installed settings/trust writes");
    }

    private static CortexMcpSettings ReadPublicConfiguration(string supplied)
    {
        // Reuse product canonical/no-reparse/path-budget admission; no other config
        // fields, backend paths, referenced files or product state are consumed.
        var path=CortexProductLayout.RequireCanonicalAbsoluteDirectory(supplied,nameof(supplied));
        CortexProductLayout.RejectExistingReparseComponents(path);
        Check(path.Length<=240&&(File.GetAttributes(path)&(FileAttributes.Directory|FileAttributes.ReparsePoint))==0,
            "explicit_public_config_path_invalid");
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        Check(stream.Length is >0 and <=16384,"explicit_public_config_size_invalid");
        var bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);
        using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions { MaxDepth=16 });
        var root=document.RootElement;
        Check(root.ValueKind==JsonValueKind.Object&&root.EnumerateObject().Count(p=>p.Name=="https")==1,
            "explicit_https_config_missing_or_duplicate");
        var https=root.GetProperty("https");
        RequireFields(https,"serverName","serverCertificate","certificateAuthority");
        var name=RequireString(https,"serverName");
        // No DNS-selected external destination and no custom ConnectCallback.
        // The selected configuration must name this fixture's IPv4 loopback.
        Check(name.Equals("localhost",StringComparison.OrdinalIgnoreCase)||name=="127.0.0.1",
            "configured_fixture_requires_localhost_or_ipv4_loopback_name");
        var server=ReadReference(https.GetProperty("serverCertificate"));
        var ca=ReadReference(https.GetProperty("certificateAuthority"));
        Check(server.Location=="CurrentUser"&&server.Store=="My"&&ca.Location=="LocalMachine"&&ca.Store=="Root",
            "configured_fixture_certificate_scope_invalid");
        return CortexMcpSettingsStore.Validate(CortexMcpSettingsStore.Default with
        {
            Enabled=true,Owner=CortexMcpOwner.Application,ServerName=name,
            ApplicationCertificate=server,CertificateAuthority=ca
        });
    }

    private static CortexMcpCertificateReference ReadReference(JsonElement value)
    {
        RequireFields(value,"location","store","certificateId");
        return new(RequireString(value,"location"),RequireString(value,"store"),RequireString(value,"certificateId"));
    }
    private static void RequireFields(JsonElement value,params string[] fields)
    {
        Check(value.ValueKind==JsonValueKind.Object&&value.EnumerateObject().Count()==fields.Length&&
            value.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==fields.Length&&
            value.EnumerateObject().All(p=>fields.Contains(p.Name,StringComparer.Ordinal)),"public_https_fields_invalid");
    }
    private static string RequireString(JsonElement value,string name)
    {
        Check(value.TryGetProperty(name,out var property)&&property.ValueKind==JsonValueKind.String,
            "public_https_string_invalid");
        return property.GetString()!;
    }

    private static int SelectOneLoopbackPort()
    {
        var reservation=new TcpListener(IPAddress.Loopback,0) { ExclusiveAddressUse=true };
        try
        {
            reservation.Start(1);
            var port=((IPEndPoint)reservation.LocalEndpoint).Port;
            Check(port is >0 and <=65535&&port!=47831,"isolated_ephemeral_port_invalid");
            return port;
        }
        finally { reservation.Stop(); }
    }

    private static async Task RequireNoListenerAsync(int port)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var probe=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
        try { await probe.ConnectAsync(new IPEndPoint(IPAddress.Loopback,port),deadline.Token); }
        catch(SocketException error)when(error.SocketErrorCode==SocketError.ConnectionRefused) { return; }
        throw new InvalidOperationException("isolated_listener_remains_after_cleanup");
    }

    private static async Task<JsonObject?> SendAsync(HttpClient http,Uri endpoint,string? session,HttpMethod method,
        JsonObject? body,HttpStatusCode expected,Action<string>? initializedSession=null)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request=new HttpRequestMessage(method,endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if(session is not null)
        {
            request.Headers.Add("Mcp-Session-Id",session);
            request.Headers.Add("MCP-Protocol-Version",Protocol);
        }
        if(body is not null)request.Content=new StringContent(body.ToJsonString(),Encoding.UTF8,"application/json");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
        Check(response.StatusCode==expected,"unexpected_http_status_"+(int)response.StatusCode);
        if(initializedSession is not null)
        {
            Check(response.Headers.TryGetValues("Mcp-Session-Id",out var values),"initialize_session_header_missing");
            var ids=values!.ToArray();
            Check(ids.Length==1&&ids[0].Length==32&&ids[0].All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f'),
                "initialize_session_header_invalid");
            initializedSession(ids[0]);
        }
        Check(response.Content.Headers.ContentLength is null or <=MaximumResponseBytes,"fixture_response_too_large");
        using var input=await response.Content.ReadAsStreamAsync(deadline.Token);
        using var content=new MemoryStream();
        var buffer=new byte[4096];
        while(true)
        {
            var read=await input.ReadAsync(buffer,deadline.Token);
            if(read==0)break;
            Check(content.Length+read<=MaximumResponseBytes,"fixture_response_too_large");
            content.Write(buffer,0,read);
        }
        if(expected is HttpStatusCode.Accepted or HttpStatusCode.NoContent)
        { Check(content.Length==0,"control_response_was_not_empty");return null; }
        Check(response.Content.Headers.ContentType?.MediaType=="application/json"&&content.Length>0,
            "fixture_json_response_missing");
        return JsonNode.Parse(content.ToArray(),documentOptions:new JsonDocumentOptions { MaxDepth=16 }) as JsonObject??
            throw new InvalidDataException("fixture_json_response_invalid");
    }

    private static JsonObject Request(JsonNode? id,string method,JsonObject? parameters=null)
    {
        var value=new JsonObject { ["jsonrpc"]="2.0",["method"]=method,["params"]=parameters??new JsonObject() };
        if(id is not null)value["id"]=id;
        return value;
    }
    private static void RequireResult(JsonObject? response,JsonNode id)=>
        Check(response is not null&&response["jsonrpc"]?.GetValue<string>()=="2.0"&&response["id"]?.ToJsonString()==id.ToJsonString()&&
            !response.ContainsKey("error")&&response["result"] is JsonObject,"typed_result_mapping_invalid");
    private static void RequireCatalog(JsonObject? response,int id)
    {
        RequireResult(response,JsonValue.Create(id)!);
        Check(response!["result"]?["tools"] is JsonArray tools&&tools.Count==1&&
            tools[0]?["name"]?.GetValue<string>()=="fixture_echo","fixture_catalog_invalid");
    }
    private static void Check(bool condition,string code){if(!condition)throw new InvalidOperationException(code);}

    private sealed class FixtureDispatcher:IMcpDispatcher
    {
        private int initializations,catalogCalls,echoCalls,disposals;
        internal int Initializations=>Volatile.Read(ref initializations);
        internal int CatalogCalls=>Volatile.Read(ref catalogCalls);
        internal int EchoCalls=>Volatile.Read(ref echoCalls);
        internal int Disposals=>Volatile.Read(ref disposals);
        public Task<JsonObject> InitializeAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(Disposals==0,"initialize_after_disposal");
            Interlocked.Increment(ref initializations);
            return Task.FromResult(new JsonObject
            {
                ["protocolVersion"]=Protocol,["capabilities"]=new JsonObject { ["tools"]=new JsonObject() },
                ["serverInfo"]=new JsonObject { ["name"]="owned-controller-fixture",["version"]="1" }
            });
        }
        public Task<JsonObject> DispatchAsync(McpRequestContext context,JsonObject request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(Disposals==0&&context.Product=="cortex"&&context.StreamId>0&&
                context.RequestId==request["id"]?.ToJsonString(),"fixture_context_mapping_invalid");
            JsonObject result;
            switch(request["method"]?.GetValue<string>())
            {
                case "tools/list":
                    Interlocked.Increment(ref catalogCalls);
                    result=new JsonObject
                    {
                        ["tools"]=new JsonArray(new JsonObject
                        {
                            ["name"]="fixture_echo",["description"]="Owned loopback fixture only.",
                            ["inputSchema"]=new JsonObject
                            {
                                ["type"]="object",["properties"]=new JsonObject { ["value"]=new JsonObject { ["type"]="string" } },
                                ["required"]=new JsonArray("value"),["additionalProperties"]=false
                            }
                        })
                    };
                    break;
                case "tools/call":
                    Check(request["params"]?["name"]?.GetValue<string>()=="fixture_echo"&&
                        request["params"]?["arguments"] is JsonObject arguments&&arguments.Count==1&&
                        arguments["value"]?.GetValue<string>()==EchoValue,"fixture_echo_arguments_invalid");
                    Interlocked.Increment(ref echoCalls);
                    result=new JsonObject { ["isError"]=false,["structuredContent"]=new JsonObject { ["value"]=EchoValue } };
                    break;
                default: throw new InvalidOperationException("unexpected_fixture_method");
            }
            return Task.FromResult(new JsonObject { ["jsonrpc"]="2.0",["id"]=request["id"]?.DeepClone(),["result"]=result });
        }
        public ValueTask DisposeAsync()
        {
            Check(Interlocked.Increment(ref disposals)==1,"fixture_dispatcher_disposed_twice");
            return ValueTask.CompletedTask;
        }
    }
}
