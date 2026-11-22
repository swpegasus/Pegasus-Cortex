using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Mcp.Hosting;
using Pegasus.SharedMcp;

namespace Pegasus.AgentOps.Tray;

/// <summary>Composes the existing owner certificate operations. No CLI, service
/// startup, account switch or independent background server is introduced.</summary>
internal sealed class CortexMcpSetupControl : ICortexMcpSetupController
{
    internal const string HelperFlag = "--shared-mcp-setup";

    public async Task<CortexMcpSetupResult> SetupAsync(CortexMcpSetupRequest request,CancellationToken token)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            if(sid is null or "S-1-5-18" or "S-1-5-19" or "S-1-5-20" ||
                new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                return new(false,"MCP setup requires the ordinary signed-in Cortex host.");
            var installed = CortexMcpSettingsControl.ResolveInstalledHost();
            var scope = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                sid + "\n" + installed.Root.ToUpperInvariant())));
            await using var dispatcher = new RoutingDispatcher();
            var workflow = new CortexMcpSetupWorkflow(scope,CortexMcpSettingsControl.ReadCurrent,
                dispatcher.DispatchAsync,(selection,server,ca,ct)=>CompleteAsync(installed.Host,installed.Root,
                    sid,selection,server,ca,ct));
            return await workflow.SetupAsync(request,token).ConfigureAwait(false);
        }
        catch(OperationCanceledException) { return new(false,"MCP setup was cancelled before the next step."); }
        catch(Exception error) when(IsOperational(error))
        { return new(false,"MCP setup could not be confirmed: " + error.GetType().Name + ". Review the original work before trying again.",RequiresReview:true); }
    }

    private static async Task<CortexMcpSetupResult> CompleteAsync(string host,string root,string sid,
        CortexMcpSetupRequest request,string serverPin,string authorityPin,CancellationToken token)
    {
        var start = new ProcessStartInfo(host)
        { UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(host)! };
        foreach(var arg in new[] { HelperFlag,request.Owner.ToString(),request.Enabled?"enabled":"disabled",
            serverPin,authorityPin,sid,CortexMcpSetupWorkflow.Fingerprint(request.Baseline) })start.ArgumentList.Add(arg);
        token.ThrowIfCancellationRequested();
        try
        {
            using var helper = Process.Start(start);
            if(helper is null)return new(false,"The MCP setup helper did not start.");
            // Page closure/cancellation cannot retract a delivered settings update.
            // Retain the original process until its natural exit, without replay.
            await helper.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if(helper.ExitCode!=0)return new(false,helper.ExitCode switch
            {
                12=>"The selected certificate or private key could not be validated.",
                13=>"Saved MCP settings changed during setup. Refresh and review them.",
                14=>"The local certificate connection check or normal system trust check failed.",
                _=>"MCP setup was not confirmed (helper exit " + helper.ExitCode + "). Review before trying again."
            },RequiresReview:true);
            var expected = CortexMcpSettingsControl.SelectCertificates(request.Baseline,request.Owner,serverPin,authorityPin)
                with { Enabled=request.Enabled,Owner=request.Owner };
            var saved = CortexMcpSettingsStore.ReadForInstallRoot(root);
            return CortexMcpSetupWorkflow.Fingerprint(saved)==CortexMcpSetupWorkflow.Fingerprint(expected)
                ? new(true,TlsQualified:true)
                : new(false,"The helper finished, but the saved selection could not be confirmed. Refresh and review it.",RequiresReview:true);
        }
        catch(Win32Exception error) when(error.NativeErrorCode==1223)
        { return new(false,"Windows elevation was cancelled. Earlier completed certificate steps have been retained.",RequiresReview:true); }
    }

    internal static async Task<int> RunElevatedAsync(string owner,string enabled,string serverPin,string authorityPin,
        string expectedOwnerSid,string expectedSettingsFingerprint)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))return 4;
            if(identity.User?.Value!=expectedOwnerSid || expectedOwnerSid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20")return 12;
            var selection = CortexMcpSettingsControl.ParseSelection(enabled,owner);
            if(!ValidHash(expectedSettingsFingerprint))return 2;
            var installed = CortexMcpSettingsControl.ResolveInstalledHost();
            var baseline = CortexMcpSettingsStore.ReadForInstallRoot(installed.Root);
            if(CortexMcpSetupWorkflow.Fingerprint(baseline)!=expectedSettingsFingerprint)return 13;
            var request = new CortexMcpSetupRequest(baseline,selection.Enabled,selection.Owner);
            return await FinalizeAsync(request,serverPin,authorityPin,CortexMcpSettingsControl.ReadCurrent,
                new CortexMcpCertificateResolver(),QualifyAsync,change=>
                    CortexMcpSettingsStore.UpdateForInstallRoot(installed.Root,change),CancellationToken.None).ConfigureAwait(false);
        }
        catch(Exception error) when(error is ArgumentException or InvalidDataException or System.Text.Json.JsonException)
        { return 2; }
        catch(Exception error) when(error is UnauthorizedAccessException or CortexMcpHostingException or
            Pegasus.AgentOps.Certificates.CertificateOperationException)
        { return 12; }
        catch(Exception error) when(IsOperational(error)) { return 8; }
    }

    // Trusted seams isolate the orchestration policy for owned, store-free tests.
    internal static async Task<int> FinalizeAsync(CortexMcpSetupRequest request,string serverPin,string authorityPin,
        Func<CortexMcpSettings> read,ICortexMcpCertificateResolver resolver,
        Func<CortexMcpCertificates,string,CancellationToken,Task<McpTlsQualificationResult>> qualify,
        Action<Func<CortexMcpSettings,CortexMcpSettings>> update,CancellationToken token)
    {
        var original = CortexMcpSetupWorkflow.Fingerprint(request.Baseline);
        if(CortexMcpSetupWorkflow.Fingerprint(read())!=original)return 13;
        var candidate = CortexMcpSettingsControl.SelectCertificates(request.Baseline,request.Owner,serverPin,authorityPin)
            with { Enabled=request.Enabled,Owner=request.Owner };
        using(var certificates = CortexMcpSettingsControl.ValidateCertificates(candidate,request.Owner,serverPin,resolver))
        {
            var proof = await qualify(certificates,candidate.ServerName,token).ConfigureAwait(false);
            if(!proof.Success || proof.ClientTrustObservation!="trusted_by_this_process_system_policy")return 14;
        }
        token.ThrowIfCancellationRequested();
        try { update(current=>
        {
            // This comparison executes under the same settings-writer handle as
            // read/merge/publication, so another control cannot lose its update.
            if(CortexMcpSetupWorkflow.Fingerprint(current)!=original)
                throw new SettingsChangedException();
            return candidate;
        }); }
        catch(SettingsChangedException) { return 13; }
        return CortexMcpSetupWorkflow.Fingerprint(read())==CortexMcpSetupWorkflow.Fingerprint(candidate)?0:8;
    }

    private sealed class SettingsChangedException : IOException { }

    private static async Task<McpTlsQualificationResult> QualifyAsync(CortexMcpCertificates certificates,string serverName,
        CancellationToken token)
    {
        // A temporary loopback-only certificate proof. Its dispatcher cannot
        // initialize a session or reach a product operation, database or broker.
        await using var dispatcher = new QualificationOnlyDispatcher();
        await using var listener = await SharedMcpServer.StartAsync(new McpServerOptions
        {
            BindAddress=IPAddress.Loopback,Port=0,ServerName=serverName,
            ServerCertificate=certificates.Server,QualificationAuthority=certificates.Authority,
            MaximumSessions=1,MaximumPendingPerSession=1,MaximumPendingTotal=1,MaximumResponseBytes=4096
        },new Dictionary<string,IMcpDispatcher> { ["qualification"]=dispatcher },token).ConfigureAwait(false);
        return listener.LastTlsQualification ?? new(false,"qualification_not_observed");
    }

    private sealed class QualificationOnlyDispatcher : IMcpDispatcher
    {
        public Task<JsonObject> InitializeAsync(CancellationToken token)=>Task.FromException<JsonObject>(new InvalidOperationException("qualification_only"));
        public Task<JsonObject> DispatchAsync(McpRequestContext context,JsonObject request,CancellationToken token)=>
            Task.FromException<JsonObject>(new InvalidOperationException("qualification_only"));
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static bool ValidHash(string hash)=>hash.Length==64&&hash.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsOperational(Exception error)=>error is IOException or UnauthorizedAccessException or Win32Exception or
        InvalidDataException or CryptographicException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException;
}
