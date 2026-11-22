using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Mcp.Hosting;
using Pegasus.SharedMcp;

internal static class CortexMcpHostingTests
{
    internal static async Task RunAsync()
    {
        SettingsContract();
        PersistenceFixture();
        CortexMcpSettingsUpdateTests.Run();
        CertificatePins();
        await InactiveOwnersAsync();
        await CallbackDrainAsync();
        Check(CortexMcpHostController.ProductKey=="cortex"&&CortexMcpHostController.EndpointPath=="/mcp/cortex",
            "existing lowercase Cortex URL changed");
        Check(CortexMcpHostController.IsAddressInUse(new IOException("owned fixture",
            new SocketException((int)SocketError.AddressAlreadyInUse))),"wrapped address conflict was not identified");
        Check(!CortexMcpHostController.IsAddressInUse(new UnauthorizedAccessException("fixture"))&&
            !CortexMcpHostController.IsAddressInUse(new SocketException((int)SocketError.AccessDenied)),
            "unrelated listener failure was called another owner");
        Console.WriteLine("PASS shared MCP hosting foundation (pure settings/pins, owned file and deterministic callback fixtures; no listener/store/trust/service/installed action)");
    }

    private static void SettingsContract()
    {
        var defaults=CortexMcpSettingsStore.Default;
        Check(!defaults.Enabled&&defaults.Owner==CortexMcpOwner.Application&&defaults.BindAddress=="127.0.0.1"&&
            defaults.Port==47831&&!defaults.AllowPrivateNetwork&&defaults.AllowedPeerCidrs.SequenceEqual(["127.0.0.0/8","::1/128"]),
            "factory policy is not disabled and loopback-only");
        var bytes=CortexMcpSettingsStore.Serialize(defaults);
        var parsed=CortexMcpSettingsStore.Parse(bytes);
        Check(CortexMcpSettingsStore.Serialize(parsed).SequenceEqual(bytes),"closed settings roundtrip changed");
        var input=defaults with { AllowedPeerCidrs=["127.0.0.1/32"] };
        var snapshot=CortexMcpSettingsStore.Validate(input);
        input.AllowedPeerCidrs[0]="10.0.0.0/8";
        Check(snapshot.AllowedPeerCidrs[0]=="127.0.0.1/32","validation retained a caller-mutable peer array");

        Throws<InvalidDataException>(()=>CortexMcpSettingsStore.Validate(defaults with { Contract="other" }));
        Throws<InvalidDataException>(()=>CortexMcpSettingsStore.Validate(defaults with { Version=2 }));
        Throws<InvalidDataException>(()=>CortexMcpSettingsStore.Validate(defaults with { Owner=(CortexMcpOwner)99 }));
        Throws<InvalidDataException>(()=>CortexMcpSettingsStore.Validate(defaults with { BindAddress="10.2.3.4" }));
        Throws<InvalidDataException>(()=>CortexMcpSettingsStore.Validate(defaults with { AllowedPeerCidrs=["10.0.0.0/8"] }));
        _=CortexMcpSettingsStore.Validate(defaults with { AllowPrivateNetwork=true,BindAddress="10.2.3.4",AllowedPeerCidrs=["10.0.0.0/8","100.64.0.0/10"] });
        foreach(var peer in new[]{"0.0.0.0/0","8.8.8.0/24","10.1.1.1/8","::/0"})
            Throws<InvalidDataException>(()=>CortexMcpSettingsStore.Validate(defaults with { AllowPrivateNetwork=true,AllowedPeerCidrs=[peer] }));
        Throws<InvalidDataException>(()=>CortexMcpSettingsStore.Validate(defaults with { Port=0 }));
        Throws<InvalidDataException>(()=>CortexMcpSettingsStore.Validate(defaults with { BindAddress="0.0.0.0",AllowPrivateNetwork=true }));
        Throws<InvalidDataException>(()=>CortexMcpSettingsStore.Validate(defaults with { ServerName="localhost:47831" }));
        Throws<InvalidDataException>(()=>CortexMcpSettingsStore.Validate(defaults with
        { ApplicationCertificate=Ref("LocalMachine","My") }));
        Throws<InvalidDataException>(()=>CortexMcpSettingsStore.Validate(defaults with
        { ServiceCertificate=Ref("CurrentUser","My") }));
        Throws<InvalidDataException>(()=>CortexMcpSettingsStore.Validate(defaults with
        { CertificateAuthority=Ref("LocalMachine","My") }));
        Throws<InvalidDataException>(()=>CortexMcpSettingsStore.Validate(defaults with
        { ApplicationCertificate=Ref("CurrentUser","My") with { CertificateId=new string('A',64) } }));
        _=CortexMcpSettingsStore.Validate(defaults with { ApplicationCertificate=Ref("CurrentUser","My"),
            ServiceCertificate=Ref("LocalMachine","My"),CertificateAuthority=Ref("LocalMachine","Root") });

        var json=Encoding.UTF8.GetString(bytes);
        Throws<JsonException>(()=>CortexMcpSettingsStore.Parse(Encoding.UTF8.GetBytes(json.Replace("\"version\":1","\"version\":1,\"unknown\":true",StringComparison.Ordinal))));
        Throws<JsonException>(()=>CortexMcpSettingsStore.Parse(Encoding.UTF8.GetBytes(json.Replace("\"version\":1","\"version\":1,\"version\":1",StringComparison.Ordinal))));
        var missing=JsonNode.Parse(bytes)!.AsObject();missing.Remove("enabled");
        Throws<JsonException>(()=>CortexMcpSettingsStore.Parse(Encoding.UTF8.GetBytes(missing.ToJsonString())));
        Throws<JsonException>(()=>CortexMcpSettingsStore.Parse(Encoding.UTF8.GetBytes(json.Replace("\"Application\"","0",StringComparison.Ordinal))));
        Throws<JsonException>(()=>CortexMcpSettingsStore.Parse(Encoding.UTF8.GetBytes(json.Replace("\"Application\"","\"application\"",StringComparison.Ordinal))));
        Throws<InvalidDataException>(()=>CortexMcpSettingsStore.Parse(new byte[16385]));
    }

    private static void PersistenceFixture()
    {
        using var temp=new TempRoot();
        var path=Path.Combine(temp.Path,CortexMcpSettingsStore.FileName);
        var checks=0;
        void Boundary(string candidate)
        {
            Check(Path.GetDirectoryName(candidate)==temp.Path,"settings left their exact owned directory");
            checks++;
        }
        var defaults=CortexMcpSettingsStore.Read(path,Boundary);
        Check(!defaults.Enabled&&!File.Exists(path),"missing optional settings wrote a file or enabled the listener");
        var saved=defaults with { Enabled=true,Owner=CortexMcpOwner.Service };
        CortexMcpSettingsStore.Save(path,saved,Boundary,Create);
        Check(CortexMcpSettingsStore.Read(path,Boundary).Owner==CortexMcpOwner.Service&&checks>=5,
            "settings roundtrip lost configured owner or boundary rechecks");
        var original=File.ReadAllBytes(path);
        Throws<UnauthorizedAccessException>(()=>CortexMcpSettingsStore.Save(path,defaults,candidate=>
        {
            Boundary(candidate);
            if(candidate!=path)throw new UnauthorizedAccessException("owned temporary boundary refusal");
        },Create));
        Check(File.ReadAllBytes(path).SequenceEqual(original)&&Directory.GetFiles(temp.Path).Length==1,
            "failed save replaced selected settings or left temporary data");
        Throws<UnauthorizedAccessException>(()=>CortexMcpSettingsStore.Read(path,_=>throw new UnauthorizedAccessException("fixture")));
        Check(File.ReadAllBytes(path).SequenceEqual(original),"refused read altered settings");
        File.WriteAllBytes(path,new byte[16385]);
        Throws<InvalidDataException>(()=>CortexMcpSettingsStore.Read(path,Boundary));
        // Plain fixture creation substitutes only new-file ACL creation; it does not prove machine ACL/elevation behavior.
        static FileStream Create(string candidate)=>new(candidate,FileMode.CreateNew,FileAccess.Write,FileShare.None);
    }

    private static void CertificatePins()
    {
        using var key=RSA.Create(2048);
        var request=new CertificateRequest("CN=owned-fixture",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var server=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddMinutes(5));
        using var ca=X509CertificateLoader.LoadCertificate(server.RawData);
        var pin=Convert.ToHexStringLower(SHA256.HashData(server.RawData));
        var settings=CortexMcpSettingsStore.Default with
        {
            ApplicationCertificate=new("CurrentUser","My",pin),CertificateAuthority=new("LocalMachine","Root",pin)
        };
        using(var borrowed=CortexMcpCertificates.Borrow(server,ca))
        {
            CortexMcpHostController.RequireResolvedCertificatePins(settings,CortexMcpOwner.Application,borrowed);
            Throws<CortexMcpHostingException>(()=>CortexMcpHostController.RequireResolvedCertificatePins(settings with
            { ApplicationCertificate=new("CurrentUser","My",new string('0',64)) },CortexMcpOwner.Application,borrowed));
        }
        Check(server.HasPrivateKey&&ca.RawData.Length>0,"controller disposed borrowed fixture certificates");
        var production=new CortexMcpCertificateResolver();
        var mismatch=Throws<CortexMcpHostingException>(()=>production.Resolve(settings,CortexMcpOwner.Service));
        Check(mismatch.Code=="certificate_owner_mismatch","wrong role was not refused before store resolution");
        // The exact owner mismatch is rejected before a store read; no real store or TLS handshake is exercised here.
    }

    private static async Task InactiveOwnersAsync()
    {
        var resolver=new CountingResolver();
        var factories=0;
        Task<IMcpDispatcher> Factory(CancellationToken _)
        { factories++;throw new Exception("inactive factory must never run"); }
        await using(var disabled=await CortexMcpHostController.StartAsync(CortexMcpSettingsStore.Default,
            CortexMcpOwner.Application,Factory,CancellationToken.None,resolver))
        {
            Check(disabled.State.Status==CortexMcpHostStatus.Disabled&&disabled.State.EffectiveOwner is null&&
                disabled.State.Endpoint is null&&disabled.Completion.IsCompleted,"disabled controller claimed a listener");
        }
        await using(var wrong=await CortexMcpHostController.StartAsync(CortexMcpSettingsStore.Default with
            { Enabled=true,Owner=CortexMcpOwner.Service },CortexMcpOwner.Application,Factory,CancellationToken.None,resolver))
        {
            Check(wrong.State.Status==CortexMcpHostStatus.ConfiguredForOtherOwner&&wrong.State.EffectiveOwner is null&&
                wrong.State.Endpoint is null,"wrong owner claimed another listener");
        }
        Check(factories==0&&resolver.Calls==0,"inactive owner constructed a dispatcher or resolved private certificates");
        await using(var unavailable=await CortexMcpHostController.StartAsync(CortexMcpSettingsStore.Default with
            { Enabled=true },CortexMcpOwner.Application,Factory,CancellationToken.None,resolver))
        {
            Check(unavailable.State.Status==CortexMcpHostStatus.Unavailable&&!unavailable.State.PendingRestart&&
                unavailable.State.Issue=="fixture_missing_certificate","missing TLS reference did not remain explicitly unavailable");
        }
        Check(factories==0&&resolver.Calls==1,"TLS failure reached dispatcher construction");
        using var cancelled=new CancellationTokenSource();
        cancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(()=>CortexMcpHostController.StartAsync(CortexMcpSettingsStore.Default with
            { Enabled=true },CortexMcpOwner.Application,Factory,cancelled.Token,resolver));
        Check(factories==0&&resolver.Calls==1,"already-cancelled startup reached certificate or dispatcher construction");
    }

    private static async Task CallbackDrainAsync()
    {
        var owned=new PendingDispatcher();
        var guarded=new CortexMcpHostController.CallbackDispatcher(owned);
        var initialize=guarded.InitializeAsync(CancellationToken.None);
        var dispatch=guarded.DispatchAsync(new("cortex","owned",1,"fixture","1"),new JsonObject(),CancellationToken.None);
        try
        {
            await Task.WhenAll(owned.InitializeEntered.Task,owned.DispatchEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            guarded.BeginClose();
            var disposal=guarded.DisposeAsync().AsTask();
            Check(!disposal.IsCompleted&&owned.Disposals==0&&owned.InitializeCancellation.IsCancellationRequested&&
                owned.DispatchCancellation.IsCancellationRequested,"closing disposed live callback state or failed cancellation");
            await ThrowsAsync<ObjectDisposedException>(()=>guarded.InitializeAsync(CancellationToken.None));
            await ThrowsAsync<ObjectDisposedException>(()=>guarded.DispatchAsync(new("cortex","owned",1,null,"2"),new JsonObject(),CancellationToken.None));
            owned.InitializeRelease.SetResult(new JsonObject());
            await initialize;
            Check(!disposal.IsCompleted&&owned.Disposals==0,"completed initialize hid a still-live dispatch");
            owned.DispatchRelease.SetResult(new JsonObject());
            await dispatch;
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            await guarded.DisposeAsync();
            Check(owned.Disposals==1&&guarded.WhenIdle.IsCompleted,"dispatcher was not drained/disposed exactly once");
        }
        finally
        {
            // Even a failed fixture assertion releases its owned deterministic callbacks.
            owned.InitializeRelease.TrySetResult(new JsonObject());
            owned.DispatchRelease.TrySetResult(new JsonObject());
            await Task.WhenAll(initialize,dispatch);
            await guarded.DisposeAsync();
        }
    }

    private static CortexMcpCertificateReference Ref(string location,string store)=>new(location,store,new string('a',64));
    private static T Throws<T>(Action action)where T:Exception
    { try { action(); }catch(T error){return error;}throw new Exception("Expected "+typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action)where T:Exception
    { try { await action(); }catch(T){return;}throw new Exception("Expected "+typeof(T).Name); }
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}

    private sealed class CountingResolver:ICortexMcpCertificateResolver
    {
        internal int Calls;
        public CortexMcpCertificates Resolve(CortexMcpSettings settings,CortexMcpOwner owner)
        { Calls++;throw new CortexMcpHostingException("fixture_missing_certificate"); }
    }

    private sealed class PendingDispatcher:IMcpDispatcher
    {
        internal readonly TaskCompletionSource InitializeEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DispatchEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<JsonObject> InitializeRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<JsonObject> DispatchRelease=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken InitializeCancellation,DispatchCancellation;
        internal int Disposals;
        public Task<JsonObject> InitializeAsync(CancellationToken token)
        { InitializeCancellation=token;InitializeEntered.TrySetResult();return InitializeRelease.Task; }
        public Task<JsonObject> DispatchAsync(McpRequestContext context,JsonObject request,CancellationToken token)
        { DispatchCancellation=token;DispatchEntered.TrySetResult();return DispatchRelease.Task; }
        public ValueTask DisposeAsync(){Disposals++;return ValueTask.CompletedTask;}
    }
}
