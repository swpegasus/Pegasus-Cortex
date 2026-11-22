using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Tray;
using Pegasus.Cortex.Mcp.Hosting;
using Pegasus.SharedMcp;

internal static class CortexMcpSetupControlTests
{
    internal static async Task RunAsync()
    {
        await FinalizationAsync().ConfigureAwait(false);
        Console.WriteLine("PASS guided MCP finalization: exact certificate/name policy, qualification/trust and fresh-update gates (in-memory certificates/injected proof, no stores/UAC/listener/settings writes)");
        await PageAsync().ConfigureAwait(false);
        await ReviewedRetryPageAsync().ConfigureAwait(false);
        Console.WriteLine("PASS guided MCP page: explicit confirmation, saved/pending selection and disposed-page ownership (unshown controls/injected workflow)");
        await OwnershipWithoutUiLoopAsync().ConfigureAwait(false);
        Console.WriteLine("PASS guided MCP lease cleanup without a running UI context (injected stopped-context fixture)");
    }

    private static async Task FinalizationAsync()
    {
        var now=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var certificates=new CertificateOperations();
        using var ca=certificates.CreateAuthority(new("Owned setup CA",now.AddDays(-1),now.AddDays(50),2048));
        using var leaf=certificates.IssueServer(new("Owned setup leaf",now.AddHours(-1),now.AddDays(10),2048),ca,
            new(["localhost"],["127.0.0.1","::1"]));
        var caPin=ca.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
        var leafPin=leaf.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
        var baseline=CortexMcpSettingsStore.Default;
        var current=baseline;
        var updates=0;var proofs=0;
        var resolver=new Resolver(leaf,ca);
        var request=new CortexMcpSetupRequest(baseline,true,CortexMcpOwner.Service);
        void Update(Func<CortexMcpSettings,CortexMcpSettings> change) { var value=change(current);updates++;current=value; }
        Task<McpTlsQualificationResult> Proof(CortexMcpCertificates _,string name,CancellationToken token)
        { Check(name=="localhost","configured TLS name was changed");proofs++;return Task.FromResult(new McpTlsQualificationResult(true,"fixture")
            { ClientTrustObservation="trusted_by_this_process_system_policy" }); }
        var result=await CortexMcpSetupControl.FinalizeAsync(request,leafPin,caPin,()=>current,resolver,Proof,Update,CancellationToken.None);
        Check(result==0&&proofs==1&&updates==1&&current.Enabled&&current.Owner==CortexMcpOwner.Service&&
            current.ServiceCertificate==new CortexMcpCertificateReference("LocalMachine","My",leafPin)&&
            current.CertificateAuthority==new CortexMcpCertificateReference("LocalMachine","Root",caPin)&&
            current.ApplicationCertificate is null&&current.AllowedPeerCidrs.SequenceEqual(baseline.AllowedPeerCidrs),
            "confirmed setup did not preserve network/opposite-role selection");
        current=baseline;updates=proofs=0;
        result=await CortexMcpSetupControl.FinalizeAsync(request,leafPin,caPin,()=>current,resolver,
            (_,_,_)=>Task.FromResult(new McpTlsQualificationResult(false,"fixture_failed")),Update,CancellationToken.None);
        Check(result==14&&updates==0,"failed loopback proof saved settings");
        result=await CortexMcpSetupControl.FinalizeAsync(request,leafPin,caPin,()=>current,resolver,
            (_,_,_)=>Task.FromResult(new McpTlsQualificationResult(true,"fixture")
                { ClientTrustObservation="selected_ca_not_normally_trusted_here" }),Update,CancellationToken.None);
        Check(result==14&&updates==0,"selected-CA success invented ordinary system trust");
        current=baseline with { Port=49002 };resolver.Calls=0;
        result=await CortexMcpSetupControl.FinalizeAsync(request,leafPin,caPin,()=>current,resolver,Proof,Update,CancellationToken.None);
        Check(result==13&&resolver.Calls==0&&updates==0,"stale baseline opened certificate material");
        current=baseline;
        result=await CortexMcpSetupControl.FinalizeAsync(request,leafPin,caPin,()=>current,resolver,
            (c,n,t)=>{ current=baseline with { Port=49003 };return Proof(c,n,t); },Update,CancellationToken.None);
        Check(result==13&&updates==0&&current.Port==49003,"settings changed during TLS proof were overwritten");
        current=baseline;proofs=0;
        try
        {
            await CortexMcpSetupControl.FinalizeAsync(request,leafPin,new string('c',64),()=>current,resolver,Proof,Update,CancellationToken.None);
            throw new InvalidOperationException("unselected CA was admitted");
        }
        catch(CortexMcpHostingException) { }
        Check(proofs==0&&updates==0,"wrong CA reached proof/publication");
        var badName=baseline with { ServerName="other.example.test" };
        try
        {
            await CortexMcpSetupControl.FinalizeAsync(request with { Baseline=badName },leafPin,caPin,()=>badName,
                resolver,Proof,Update,CancellationToken.None);
            throw new InvalidOperationException("wrong TLS name was admitted");
        }
        catch(CortexMcpHostingException) { }
        Check(proofs==0&&updates==0,"wrong name reached proof/publication");
    }

    private static async Task PageAsync()
    {
        var saved=CortexMcpSettingsStore.Default;
        var calls=0;var active=0;
        var setup=new Setup(async request=>
        {
            calls++;Check(active==1,"setup did not retain owner admission");
            await Task.CompletedTask;
            saved=request.Baseline with { Enabled=request.Enabled,Owner=request.Owner,
                CertificateAuthority=new("LocalMachine","Root",new string('c',64)),
                ServiceCertificate=new("LocalMachine","My",new string('b',64)) };
            return new(true,TlsQualified:true);
        });
        using(var unavailable=new CortexMcpSettingsPage(()=>saved,new Settings(),_=>{},setupController:setup))
            Check(!Find<Button>(unavailable,"Set up shared MCP").Enabled,"standalone page admitted setup without an owner lease");
        using(var declined=new CortexMcpSettingsPage(()=>saved,new Settings(),_=>{},setup,()=>new Lease(()=>active--),()=>false))
        { await declined.SetupSelectionAsync();Check(calls==0&&active==0,"declined setup issued work"); }
        using(var page=new CortexMcpSettingsPage(()=>saved,new Settings(),_=>{},setup,
            ()=>{active++;return new Lease(()=>active--);},()=>true))
        {
            Find<CheckBox>(page,"Enable shared HTTPS MCP").Checked=true;
            Find<ComboBox>(page,"Configured MCP owner").SelectedIndex=1;
            await page.SetupSelectionAsync();
            Check(calls==1&&active==0&&saved.Enabled&&saved.Owner==CortexMcpOwner.Service&&
                Find<Label>(page,"MCP settings status").Text.Contains("Restart pending",StringComparison.Ordinal),
                "confirmed setup was not shown as saved/pending restart");
        }
        var pending=new TaskCompletionSource<CortexMcpSetupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pagePending=new CortexMcpSettingsPage(()=>saved,new Settings(),_=>{},new Setup(_=>pending.Task),
            ()=>{active++;return new Lease(()=>active--);},()=>true);
        // Construct controls first: WinForms can install its context during construction.
        // Only the disposed continuation runs without a message loop in this fixture.
        var oldContext=SynchronizationContext.Current;
        Task work;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            work=pagePending.SetupSelectionAsync();
        }
        finally { SynchronizationContext.SetSynchronizationContext(oldContext); }
        pagePending.Dispose();
        Check(active==1&&!work.IsCompleted,"page disposal released an unfinished original setup");
        pending.SetResult(new(false,"fixture unconfirmed",RequiresReview:true));
        await work.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Check(active==0,"settled setup retained an owner lease");
    }
    private static async Task ReviewedRetryPageAsync()
    {
        var saved = CortexMcpSettingsStore.Default;
        var active = 0; var calls = 0; var confirmations = 0; var approve = false;
        var step = new CortexMcpSetupRetry("issue_machine_server",new string('a',32),"original-step",new string('b',64));
        var setup = new Setup(request =>
        {
            calls++;
            Check(active == 1,"reviewed retry lost owner admission");
            if(calls == 1)
                return Task.FromResult(new CortexMcpSetupResult(false,"Canceled before effect",step.JobId,RequiresReview:true,Retry:step));
            Check(request.Owner == CortexMcpOwner.Service && request.Enabled &&
                request.ReviewedCanceledJobIds is { Count:1 } && request.ReviewedCanceledJobIds[0] == step.JobId,
                "reviewed retry changed selection or did not retain the exact reviewed canceled job");
            saved = request.Baseline with { Enabled=request.Enabled,Owner=request.Owner,
                CertificateAuthority=new("LocalMachine","Root",new string('c',64)),
                ServiceCertificate=new("LocalMachine","My",new string('d',64)) };
            return Task.FromResult(new CortexMcpSetupResult(true,TlsQualified:true));
        });
        using(var page = new CortexMcpSettingsPage(()=>saved,new Settings(),_=>{},setup,
            ()=>{active++;return new Lease(()=>active--);},()=>true,review=>
            { confirmations++;Check(review == step,"retry confirmation displayed a different original");return approve; }))
        {
            Find<CheckBox>(page,"Enable shared HTTPS MCP").Checked=true;
            Find<ComboBox>(page,"Configured MCP owner").SelectedIndex=1;
            await page.SetupSelectionAsync();
            Check(calls==1&&active==0&&!Find<Button>(page,"Review and retry canceled MCP step").Enabled,
                "canceled setup allowed retry without Refresh");
            await page.RetrySetupSelectionAsync();
            Check(calls==1&&confirmations==0,"retry bypassed required Refresh");
            page.Reload();
            Check(Find<Button>(page,"Review and retry canceled MCP step").Enabled&&
                Find<ComboBox>(page,"Configured MCP owner").SelectedIndex==1&&
                Find<CheckBox>(page,"Enable shared HTTPS MCP").Checked,
                "Refresh lost the reviewed setup selection or failed to expose explicit retry");
            await page.RetrySetupSelectionAsync();
            Check(calls==1&&confirmations==1&&active==0,"declined retry submitted work");
            approve=true;
            await page.RetrySetupSelectionAsync();
            Check(calls==2&&confirmations==2&&active==0&&saved.Enabled&&saved.Owner==CortexMcpOwner.Service&&
                !Find<Button>(page,"Review and retry canceled MCP step").Enabled,
                "approved retry failed to settle or left stale retry admission");
        }
        saved=CortexMcpSettingsStore.Default;
        using(var changed = new CortexMcpSettingsPage(()=>saved,new Settings(),_=>{},
            new Setup(_=>Task.FromResult(new CortexMcpSetupResult(false,"Canceled",step.JobId,RequiresReview:true,Retry:step))),
            ()=>new Lease(()=>{}),()=>true,_=>throw new InvalidOperationException("stale retry reached confirmation")))
        {
            await changed.SetupSelectionAsync();
            saved=saved with { Port=saved.Port+1 };
            changed.Reload();
            Check(!Find<Button>(changed,"Review and retry canceled MCP step").Enabled,
                "changed baseline retained a canceled-step retry");
            await changed.RetrySetupSelectionAsync();
        }
        using(var unknown = new CortexMcpSettingsPage(()=>saved,new Settings(),_=>{},
            new Setup(_=>Task.FromResult(new CortexMcpSetupResult(false,"Unknown original",RequiresReview:true))),
            ()=>new Lease(()=>{}),()=>true,_=>throw new InvalidOperationException("unknown setup offered retry")))
        {
            await unknown.SetupSelectionAsync();unknown.Reload();
            Check(!Find<Button>(unknown,"Review and retry canceled MCP step").Enabled,"unknown original exposed reviewed retry");
        }
    }

    private sealed class Resolver(X509Certificate2 leaf,X509Certificate2 ca):ICortexMcpCertificateResolver
    {
        internal int Calls;
        public CortexMcpCertificates Resolve(CortexMcpSettings settings,CortexMcpOwner owner)
        { Calls++;return CortexMcpCertificates.Borrow(leaf,ca); }
    }
    private sealed class Setup(Func<CortexMcpSetupRequest,Task<CortexMcpSetupResult>> run):ICortexMcpSetupController
    { public Task<CortexMcpSetupResult> SetupAsync(CortexMcpSetupRequest request,CancellationToken token)=>run(request); }
    private sealed class Settings:ICortexMcpSettingsController
    { public Task<CortexMcpSettingsSaveResult> SaveAsync(CortexMcpSettings settings,CancellationToken token)=>
        throw new InvalidOperationException("setup used the ordinary settings-only control"); }
    private sealed class Lease(Action close):IDisposable
    { private Action? _close=close;public void Dispose()=>Interlocked.Exchange(ref _close,null)?.Invoke(); }
    private static async Task OwnershipWithoutUiLoopAsync()
    {
        var pending=new TaskCompletionSource<CortexMcpSetupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original=SynchronizationContext.Current;
        var unavailable=new UnavailableUiContext();
        Task<CortexMcpSetupResult> work;
        try
        {
            SynchronizationContext.SetSynchronizationContext(unavailable);
            work=CortexMcpSettingsPage.RunOwnedSetupAsync(()=>new Lease(()=>released.TrySetResult()),
                new Setup(_=>pending.Task),new(CortexMcpSettingsStore.Default,true,CortexMcpOwner.Application));
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        pending.SetResult(new(false,"fixture settled"));
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await work.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Check(unavailable.Posts==0,"releasing setup ownership depended on the old UI loop");
    }
    private sealed class UnavailableUiContext:SynchronizationContext
    { internal int Posts;public override void Post(SendOrPostCallback callback,object? state)=>Interlocked.Increment(ref Posts); }
    private static T Find<T>(Control parent,string name) where T:Control
    {
        foreach(Control child in parent.Controls)
        { if(child is T found&&found.AccessibleName==name)return found;
            try { return Find<T>(child,name); }catch(KeyNotFoundException) { } }
        throw new KeyNotFoundException(name);
    }
    private static void Check(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
}
