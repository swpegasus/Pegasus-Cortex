using System.IO.Compression;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class ApplicationAuthorizationTests
{
    public static Task CredentialLifecycleAsync()
    {
        using var temp=new TempRoot();var state=temp.Dir("application-auth");
        var firstPath=Path.Combine(temp.Path,"cerebral-build-one.exe");var secondPath=Path.Combine(temp.Path,"cerebral-build-two.exe");
        File.WriteAllBytes(firstPath,RandomNumberGenerator.GetBytes(127));File.WriteAllBytes(secondPath,RandomNumberGenerator.GetBytes(193));
        var peerOne=new LocalPeer("S-1-5-19",0,firstPath,1001);var peerTwo=new LocalPeer("S-1-5-19",0,secondPath,1002);
        var verifier=new FixturePeerVerifier(firstPath,secondPath);var registry=new ApplicationCredentialRegistry(state);var coordinator=new ApplicationAuthorizationCoordinator(registry,verifier);

        var issued=coordinator.Apply(Request("issue"),peerOne).Result;
        Require(issued.Ok&&issued.State=="issued"&&issued.Generation==1&&issued.Credential is{Length:>40},"issue failed");
        var firstSecret=issued.Credential!;
        Require(!StateContains(state,firstSecret),"plaintext credential persisted");
        var duplicate=coordinator.Apply(Request("issue"),peerOne).Result;Require(!duplicate.Ok&&duplicate.Err=="precondition","duplicate issue admitted");
        var status=coordinator.Apply(Request("status"),peerOne).Result;
        Require(status.Ok&&status.State=="active"&&status.Credential is null&&status.CredentialId==issued.CredentialId,"status leaked or changed identity");
        var connected=coordinator.Apply(Request("connect",firstSecret),peerTwo);
        Require(connected.Connected&&connected.Result.Ok&&connected.Result.SessionId is{Length:>8}&&connected.Result.Credential is null,"same-publisher rebuild did not connect");
        Require(!coordinator.Apply(Request("connect",Mutate(firstSecret)),peerTwo).Connected,"wrong credential connected");

        var rotated=coordinator.Apply(Request("rotate"),peerTwo).Result;
        Require(rotated.Ok&&rotated.State=="rotated"&&rotated.Generation==2&&rotated.Credential is{Length:>40},"rotate failed");
        var secondSecret=rotated.Credential!;Require(firstSecret!=secondSecret,"rotation reused credential");
        Require(!coordinator.Apply(Request("connect",firstSecret),peerTwo).Connected,"old credential survived rotation");
        Require(coordinator.Apply(Request("connect",secondSecret),peerTwo).Connected,"rotated credential rejected");
        Require(!StateContains(state,firstSecret)&&!StateContains(state,secondSecret),"credential leaked to state");

        var revoked=coordinator.Apply(Request("revoke"),peerTwo).Result;Require(revoked.Ok&&revoked.State=="revoked"&&revoked.Credential is null,"revoke failed");
        Require(!coordinator.Apply(Request("connect",secondSecret),peerTwo).Connected,"revoked credential connected");
        var wrongPeer=coordinator.Apply(Request("status"),new LocalPeer("S-1-5-21-1",4,firstPath,1001)).Result;
        Require(!wrongPeer.Ok&&wrongPeer.Err=="application_auth","wrong caller admitted");
        return Task.CompletedTask;
    }

    public static async Task TypedSystemSurfaceAsync()
    {
        using var temp=new TempRoot();var state=temp.Dir("application-broker");var owner=LocalIdentity.Current().Sid;
        using(var pipe=LocalPipeFactory.Create("cortex-application-acl-"+Guid.NewGuid().ToString("N"),"S-1-5-19"))
        {
            var rules=pipe.GetAccessControl().GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
            var localService=rules.Single(rule=>rule.IdentityReference.Value=="S-1-5-19"&&rule.AccessControlType==AccessControlType.Allow);
            Require((localService.PipeAccessRights&PipeAccessRights.ReadWrite)==PipeAccessRights.ReadWrite&&
                (localService.PipeAccessRights&PipeAccessRights.Synchronize)!=0&&
                (localService.PipeAccessRights&PipeAccessRights.CreateNewInstance)==0,
                "application pipe did not grant only client read/write synchronization to LocalService");
            Require(rules.Any(rule=>rule.IdentityReference.Value=="S-1-5-2"&&rule.AccessControlType==AccessControlType.Deny)&&
                rules.Any(rule=>rule.IdentityReference.Value=="S-1-5-7"&&rule.AccessControlType==AccessControlType.Deny),
                "application pipe did not deny network and anonymous identities");
        }

        var systemState=temp.Dir("application-system");
        var fixedSevenZip=await SystemSevenZipToolBinding.TryCreateAsync();
        await using var engine=new AgentOpsEngine(systemState,pathNormalizer:null,directOnly:true,
            bundledNsis:null,enableUserApplications:false,
            systemTools:new AgentOpsSystemToolBindings(fixedSevenZip));
        await using var broker=new AuthenticatedServiceBroker(state,owner,new RootCodeVerifier(Path.Combine(state,"root.verifier")),false,null,(request,ct)=>engine.ExecuteAuthenticatedFrozenDirectAsync(request,ct));
        var destination=Path.Combine(temp.Path,"created-by-application");
        var receipt=await broker.HandleApplicationAsync(new OperationRequest("mkdir",new Dictionary<string,string>{{"dst",destination}},"application-mkdir"),CancellationToken.None);
        Require(receipt.Ok&&receipt.State=="done"&&Directory.Exists(destination),"typed application mutation failed");
        Require(!ReceiptContains(receipt,"credential")&&!ReceiptContains(receipt,"root"),"receipt exposed authorization material");

        var rootField=await broker.HandleApplicationAsync(new OperationRequest("diag",new Dictionary<string,string>(),"root-field",Root:"ABCD"),CancellationToken.None);
        Require(!rootField.Ok&&rootField.Err=="auth","root field admitted");
        var argField=await broker.HandleApplicationAsync(new OperationRequest("diag",new Dictionary<string,string>{{"credential","not-a-real-secret"}},"credential-field"),CancellationToken.None);
        Require(!argField.Ok&&argField.Err=="auth","credential argument admitted");
        var rootAdmin=await broker.HandleApplicationAsync(new OperationRequest("root",new Dictionary<string,string>{{"op","ls"}},"root-admin"),CancellationToken.None);
        Require(!rootAdmin.Ok&&rootAdmin.Err=="auth","root administration admitted");
        var userApplication=await broker.HandleApplicationAsync(new OperationRequest("app",new Dictionary<string,string>{{"op","status"},{"name","fixture"}},"user-application"),CancellationToken.None);
        Require(!userApplication.Ok&&userApplication.Err=="user_dispatch_required","LocalSystem application session exposed signed-in user application lifetime");

        if(fixedSevenZip is not null)
        {
            var source=temp.Dir("application-archive-source");await File.WriteAllTextAsync(Path.Combine(source,"proof.txt"),"Cerebral application pipe");
            var archive=Path.Combine(temp.Path,"application-proof.zip");ZipFile.CreateFromDirectory(source,archive);
            var listed=await broker.HandleApplicationAsync(new OperationRequest("7z",new Dictionary<string,string>{{"op","list"},{"src",archive}},"application-7z-list"),CancellationToken.None);
            Require(listed.Ok&&listed.State=="done","application session 7z_list failed");
            var tested=await broker.HandleApplicationAsync(new OperationRequest("7z",new Dictionary<string,string>{{"op","test"},{"src",archive}},"application-7z-test"),CancellationToken.None);
            Require(tested.Ok&&tested.State=="done","application session 7z_test failed");
            var archiveOutput=Path.Combine(temp.Path,"application-archive-output");
            var extracted=await broker.HandleApplicationAsync(new OperationRequest("7z",new Dictionary<string,string>{{"op","extract"},{"src",archive},{"dst",archiveOutput}},"application-7z-extract"),CancellationToken.None);
            Require(extracted.Ok&&extracted.State=="done"&&await File.ReadAllTextAsync(Path.Combine(archiveOutput,"proof.txt"))=="Cerebral application pipe",
                "application session 7z_extract failed");
        }
    }

    public static async Task MissingSystemSevenZipAsync()
    {
        using var temp=new TempRoot();var state=temp.Dir("application-missing-tool-broker");var owner=LocalIdentity.Current().Sid;
        await using var engine=new AgentOpsEngine(temp.Dir("application-missing-tool-system"),pathNormalizer:null,directOnly:true,
            bundledNsis:null,enableUserApplications:false,
            systemTools:new AgentOpsSystemToolBindings(null));
        await using var broker=new AuthenticatedServiceBroker(state,owner,
            new RootCodeVerifier(Path.Combine(state,"root.verifier")),false,null,
            (request,ct)=>engine.ExecuteAuthenticatedFrozenDirectAsync(request,ct));
        var archive=Path.Combine(temp.Path,"missing-tool.zip");
        await File.WriteAllBytesAsync(archive,[0x50,0x4b,0x05,0x06,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]);
        var receipt=await broker.HandleApplicationAsync(new OperationRequest("7z",
            new Dictionary<string,string>{{"op","list"},{"src",archive}},"application-7z-missing"),CancellationToken.None);
        Require(!receipt.Ok&&receipt.State=="failed"&&receipt.Err=="not_found",
            "missing fixed System 7-Zip binding did not fail closed as not_found");
    }

    private static ApplicationAuthorizationRequest Request(string action,string? credential=null)=>new(ApplicationAuthorizationProtocol.Version,ApplicationAuthorizationProtocol.CerebralApplicationId,action,credential);
    // Change significant key bits, not potentially unused base64 tail bits.
    private static string Mutate(string value)=>(value[0]=='A'?'B':'A')+value[1..];
    private static bool StateContains(string root,string value)=>Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Any(path=>File.ReadAllText(path).Contains(value,StringComparison.Ordinal));
    private static bool ReceiptContains(OperationReceipt receipt,string value)=>System.Text.Json.JsonSerializer.Serialize(receipt).Contains(value,StringComparison.OrdinalIgnoreCase);
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}

    private sealed class FixturePeerVerifier(string firstPath,string secondPath):IApplicationPeerVerifier
    {
        public ApplicationPeerEvidence Verify(LocalPeer peer,string applicationId)
        {
            if(applicationId!=ApplicationAuthorizationProtocol.CerebralApplicationId||peer.Sid!="S-1-5-19"||peer.Session!=0||peer.ProcessId is null||
                peer.ApplicationPath is null||!new[]{firstPath,secondPath}.Contains(peer.ApplicationPath,StringComparer.OrdinalIgnoreCase))throw new AgentOpsException("application_auth");
            return new(applicationId,ApplicationAuthorizationProtocol.CerebralServiceName,peer.ApplicationPath,"00112233445566778899AABBCCDDEEFF00112233");
        }
    }

}
