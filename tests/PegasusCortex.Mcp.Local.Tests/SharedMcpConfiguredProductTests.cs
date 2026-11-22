using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Hosting;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Mcp.Local.Transport;
using Pegasus.Cortex.Mcp.Protocol;
using Pegasus.Cortex.Product;
using Pegasus.SharedMcp;

internal static class SharedMcpConfiguredProductTests
{
    internal static async Task RunAsync(string explicitPublicDevHostConfigPath)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        var settings=ReadSettings(explicitPublicDevHostConfigPath) with { Port=SelectPort() };
        var root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"cortex-product-tls-"+Guid.NewGuid().ToString("N")));
        CortexProductLayout.RequirePathBudget(root);
        CortexProductLayout.RejectExistingReparseComponents(root);
        Check(!Directory.Exists(root)&&!File.Exists(root),"owned_root_already_exists");
        var database=Path.Combine(root,"cortex-v2.db");
        var admission=new UserHostAdmission();
        var scheduler=new BoundedRequestDispatcher();
        var execution=new RefusedExecution();
        CortexMcpLocalComposition? composition=null;
        CortexMcpHostController? controller=null;
        ProductSession? session=null;
        Exception? primary=null;
        var cleanup=new List<Exception>();
        var ready=false;
        using var startup=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            // No ambient profile/config or installed seed/cache/resident startup.
            composition=await CortexMcpLocalComposition.CreateGeneralAsync(database,TimeProvider.System,startup.Token,
                personalProfileProvider:null,shadowResearch:new CortexShadowConfiguration("off",null));
            var application=new CortexUnifiedMcpApplication(composition.Application,execution);
            var owner=CortexClientApplication.ForOwnerEndpoint(application,admission);
            startup.CancelAfter(TimeSpan.FromSeconds(20));
            controller=await CortexMcpHostController.StartAsync(settings,CortexMcpOwner.Application,
                _=>Task.FromResult<IMcpDispatcher>(new CortexEmbeddedMcpDispatcher(owner,scheduler,admission,TextWriter.Null)),
                startup.Token);
            startup.CancelAfter(Timeout.InfiniteTimeSpan);
            var state=controller.State;
            Check(state.Status==CortexMcpHostStatus.Ready&&state.EffectiveOwner==CortexMcpOwner.Application&&
                state.Endpoint is { Scheme:"https" }&&state.Endpoint.Port==settings.Port&&
                state.Endpoint.AbsolutePath==CortexMcpHostController.EndpointPath,"owned_controller_not_ready");
            ready=true;
            Check(state.TlsQualification is { Success:true,ValidUnderSelectedCa:true,
                ClientTrustObservation:"trusted_by_this_process_system_policy" },"owned_tls_or_system_trust_unqualified");
            session=new ProductSession(state.Endpoint!);
            var initialized=await session.SendAsync(1,"initialize",new()
            {
                ["protocolVersion"]=ProductSession.Protocol,["capabilities"]=new JsonObject(),
                ["clientInfo"]=new JsonObject { ["name"]="owned-product-fixture",["version"]="1" }
            });
            Check(initialized?["protocolVersion"]?.GetValue<string>()==ProductSession.Protocol,
                "owned_protocol_negotiation_failed");
            await session.SendAsync(null,"notifications/initialized",new JsonObject());
            var catalog=await session.SendAsync(2,"tools/list",new JsonObject());
            Check(catalog?["tools"] is JsonArray tools&&tools.Count==9&&
                tools.Any(tool=>tool?["name"]?.GetValue<string>()=="cortex_status")&&
                tools.Any(tool=>tool?["name"]?.GetValue<string>()=="pao"),"actual_product_catalog_invalid");
            var called=await session.SendAsync(3,"tools/call",new JsonObject
            { ["name"]="cortex_status",["arguments"]=new JsonObject() });
            Check(called is not null&&called["isError"]?.GetValue<bool>()==false&&called["structuredContent"] is JsonObject,
                "actual_product_status_failed");
            var status=called!["structuredContent"]!.AsObject();
            Check(status["product"]?.GetValue<string>()=="Pegasus Cortex"&&status["state"]?.GetValue<string>()=="ready"&&
                status["storageKind"]?.GetValue<string>()=="sqlite-v2"&&status["headCount"]?.GetValue<long>()==0&&
                status["revisionCount"]?.GetValue<long>()==0&&status["effectiveNamespaces"] is JsonArray { Count:0 },
                "owned_empty_sqlite_status_invalid");
            Check(execution.Calls==0,"fixture_dispatched_execution");
        }
        catch(Exception error) { primary=error; }
        finally
        {
            // Always close HTTP admission and its actual callbacks before the
            // real composition can release SQLite. No timeout abandons this drain.
            try { if(session is not null)await session.DisposeAsync(); }catch(Exception error) { cleanup.Add(error); }
            try { if(controller is not null)await controller.DisposeAsync(); }catch(Exception error) { cleanup.Add(error); }
            await scheduler.WhenIdleAsync();
            try { Check(admission.ActiveCount==0&&scheduler.Snapshot.Count==0,"owned_callbacks_not_drained"); }
            catch(Exception error) { cleanup.Add(error); }
            try { if(composition is not null)await composition.DisposeAsync(); }catch(Exception error) { cleanup.Add(error); }
            try { await execution.DisposeAsync(); }catch(Exception error) { cleanup.Add(error); }
            try { ReleaseScratch(root,database); }catch(Exception error) { cleanup.Add(error); }
            if(ready)
            {
                try
                {
                    Check(controller!.State.Status==CortexMcpHostStatus.Stopped&&controller.Completion.IsCompletedSuccessfully,
                        "owned_controller_not_stopped");
                    await RequireClosedPortAsync(settings.Port);
                }
                catch(Exception error) { cleanup.Add(error); }
            }
        }
        if(primary is not null)
        {
            if(cleanup.Count>0)throw new AggregateException("owned_product_tls_and_cleanup_failed",new[]{primary}.Concat(cleanup));
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if(cleanup.Count>0)throw new AggregateException("owned_product_tls_cleanup_failed",cleanup);
        Check(ready&&execution.Calls==0&&execution.Disposals==1&&!Directory.Exists(root),"owned_product_fixture_incomplete");
        Console.WriteLine("PASS shared MCP configured product: real default-system-trust TLS/HTTP + actual Cortex composition/owned empty SQLite/status; DELETE, callback drain, DB release, scratch and port cleanup complete; executionCalls=0; no installed/System/Codex/profile/cache/semantic-write acceptance");
    }

    private static CortexMcpSettings ReadSettings(string explicitPath)
    {
        var path=CortexProductLayout.RequireCanonicalAbsoluteDirectory(explicitPath,nameof(explicitPath));
        CortexProductLayout.RejectExistingReparseComponents(path);
        Check(path.Length<=240&&(File.GetAttributes(path)&(FileAttributes.Directory|FileAttributes.ReparsePoint))==0,
            "public_config_path_invalid");
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        Check(input.Length is >0 and <=16384,"public_config_bound");
        var bytes=new byte[checked((int)input.Length)];input.ReadExactly(bytes);
        using var json=JsonDocument.Parse(bytes,new JsonDocumentOptions { MaxDepth=16 });
        Check(json.RootElement.ValueKind==JsonValueKind.Object&&
            json.RootElement.EnumerateObject().Count(p=>p.Name=="https")==1,"public_https_missing_or_duplicate");
        var https=json.RootElement.GetProperty("https");
        Fields(https,"serverName","serverCertificate","certificateAuthority");
        var name=https.GetProperty("serverName").GetString();
        Check(name is not null&&(name.Equals("localhost",StringComparison.OrdinalIgnoreCase)||name=="127.0.0.1"),
            "fixture_requires_loopback_tls_name");
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { PropertyNameCaseInsensitive=false,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };
        CortexMcpCertificateReference Ref(string field)
        {
            var value=https.GetProperty(field);Fields(value,"location","store","certificateId");
            return value.Deserialize<CortexMcpCertificateReference>(options)??throw new InvalidDataException("public_certificate_reference_missing");
        }
        var leaf=Ref("serverCertificate");var ca=Ref("certificateAuthority");
        Check(leaf.Location=="CurrentUser"&&leaf.Store=="My"&&ca.Location=="LocalMachine"&&ca.Store=="Root",
            "public_certificate_scope_invalid");
        return CortexMcpSettingsStore.Validate(CortexMcpSettingsStore.Default with
        { Enabled=true,Owner=CortexMcpOwner.Application,ServerName=name!,ApplicationCertificate=leaf,CertificateAuthority=ca });
        static void Fields(JsonElement value,params string[] expected)=>
            Check(value.ValueKind==JsonValueKind.Object&&value.EnumerateObject().Count()==expected.Length&&
                value.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==expected.Length&&
                value.EnumerateObject().All(p=>expected.Contains(p.Name,StringComparer.Ordinal)),"public_https_fields_invalid");
    }

    private static int SelectPort()
    {
        var reservation=new TcpListener(IPAddress.Loopback,0) { ExclusiveAddressUse=true };
        try { reservation.Start(1);var port=((IPEndPoint)reservation.LocalEndpoint).Port;
            Check(port>0&&port!=47831,"isolated_port_invalid");return port; }
        finally { reservation.Stop(); } // One allocation; a later race fails without retry.
    }
    private static async Task RequireClosedPortAsync(int port)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var probe=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
        try { await probe.ConnectAsync(new IPEndPoint(IPAddress.Loopback,port),deadline.Token); }
        catch(SocketException error)when(error.SocketErrorCode==SocketError.ConnectionRefused) { return; }
        throw new InvalidOperationException("owned_product_port_remains_open");
    }
    private static void ReleaseScratch(string root,string database)
    {
        var parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        Check(root.StartsWith(parent,StringComparison.OrdinalIgnoreCase)&&
            Path.GetFileName(root).StartsWith("cortex-product-tls-",StringComparison.Ordinal),"owned_scratch_boundary_changed");
        if(!Directory.Exists(root))return;
        CortexProductLayout.RejectExistingReparseComponents(root);
        foreach(var file in Directory.EnumerateFileSystemEntries(root))
        {
            Check((File.GetAttributes(file)&(FileAttributes.Directory|FileAttributes.ReparsePoint))==0&&
                (file==database||file==database+"-wal"||file==database+"-shm"),"unexpected_owned_scratch_entry");
            using var exclusive=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.None);
        }
        Directory.Delete(root,recursive:true);
        Check(!Directory.Exists(root),"owned_scratch_remains");
    }
    private static void Check(bool value,string code){if(!value)throw new InvalidOperationException(code);}

    private sealed class RefusedExecution:IOperationDispatcher
    {
        internal int Calls,Disposals;
        public Task<OperationReceipt> DispatchAsync(OperationRequest request,CancellationToken cancellationToken=default)
        { Interlocked.Increment(ref Calls);throw new InvalidOperationException("execution_refused_in_owned_fixture"); }
        public ValueTask DisposeAsync(){Disposals++;return ValueTask.CompletedTask;}
    }

    private sealed class ProductSession(Uri endpoint):IAsyncDisposable
    {
        internal const string Protocol="2025-06-18";
        private readonly HttpClient http=new(new SocketsHttpHandler
        { UseProxy=false,AllowAutoRedirect=false,UseCookies=false,ConnectTimeout=TimeSpan.FromSeconds(20) })
        { Timeout=Timeout.InfiniteTimeSpan };
        private string? session;
        private bool deleteAttempted;
        internal async Task<JsonObject?> SendAsync(int? id,string method,JsonObject parameters)
        {
            var message=new JsonObject { ["jsonrpc"]="2.0",["method"]=method,["params"]=parameters };
            if(id is not null)message["id"]=id.Value;
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response=await SendCoreAsync(HttpMethod.Post,message,deadline.Token);
            if(id is null)
            { Check(response.StatusCode==HttpStatusCode.Accepted,"notification_failed");return null; }
            Check(response.StatusCode==HttpStatusCode.OK,"product_http_request_failed");
            if(session is null&&response.Headers.TryGetValues("Mcp-Session-Id",out var values))
            {
                var ids=values.ToArray();
                Check(ids.Length==1&&ids[0].Length==32&&ids[0].All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f'),"session_header_invalid");
                session=ids[0];
            }
            Check(session is not null,"product_session_missing");
            using var stream=await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer=new MemoryStream();var chunk=new byte[4096];
            while(true)
            {
                var read=await stream.ReadAsync(chunk,deadline.Token);if(read==0)break;
                Check(buffer.Length+read<=65536,"product_response_bound");buffer.Write(chunk,0,read);
            }
            var envelope=JsonNode.Parse(buffer.ToArray(),documentOptions:new JsonDocumentOptions { MaxDepth=32 }) as JsonObject;
            Check(envelope is not null&&envelope["jsonrpc"]?.GetValue<string>()=="2.0"&&
                envelope["id"]?.GetValue<int>()==id&&!envelope.ContainsKey("error")&&envelope["result"] is JsonObject,
                "product_response_mapping_invalid");
            return envelope!["result"]!.AsObject();
        }
        private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method,JsonObject? body,CancellationToken token)
        {
            using var request=new HttpRequestMessage(method,endpoint);
            request.Headers.Accept.ParseAdd("application/json");request.Headers.Accept.ParseAdd("text/event-stream");
            if(session is not null)
            { request.Headers.Add("Mcp-Session-Id",session);request.Headers.Add("MCP-Protocol-Version",Protocol); }
            if(body is not null)request.Content=new StringContent(body.ToJsonString(),Encoding.UTF8,"application/json");
            return await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if(session is not null&&!deleteAttempted)
                {
                    deleteAttempted=true;
                    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    using var response=await SendCoreAsync(HttpMethod.Delete,null,deadline.Token);
                    Check(response.StatusCode==HttpStatusCode.NoContent,"product_session_delete_failed");
                    session=null;
                }
            }
            finally { http.Dispose(); }
        }
    }
}
