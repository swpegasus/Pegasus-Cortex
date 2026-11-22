using System.IO.Compression;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class ApplicationAuthorizationTests
{
    public static async Task CredentialLifecycleAsync()
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
        await ConnectedSessionRevocationAsync();
    }

    private static async Task ConnectedSessionRevocationAsync()
    {
        using var temp=new TempRoot();var state=temp.Dir("application-session-revocation");
        var firstPath=Path.Combine(temp.Path,"cerebral-one.exe");var secondPath=Path.Combine(temp.Path,"cerebral-two.exe");
        var peer=new LocalPeer("S-1-5-19",0,firstPath,1001);
        var registry=new ApplicationCredentialRegistry(state);
        var coordinator=new ApplicationAuthorizationCoordinator(registry,new FixturePeerVerifier(firstPath,secondPath));
        var first=coordinator.Apply(Request("issue"),peer).Result;
        Require(first.Ok&&first.Credential is not null,"session fixture issue failed");
        var firstSecret=first.Credential!;
        var connected=coordinator.Apply(Request("connect",firstSecret),peer);
        Require(connected.Lease is { } lease&&lease.ApplicationId==ApplicationAuthorizationProtocol.CerebralApplicationId&&
            lease.CredentialId==first.CredentialId&&lease.Generation==first.Generation,"connection lease did not pin its authenticated tuple");
        Require(!System.Text.Json.JsonSerializer.Serialize(connected).Contains(firstSecret,StringComparison.Ordinal),"connection retained credential text");

        var executions=0;
        var pending=new TaskCompletionSource<OperationReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<OperationReceipt> Execute(OperationRequest request,CancellationToken token)
        {
            executions++;
            return request.Rid=="already-admitted"?pending.Task:Task.FromResult(Success(request));
        }
        var original=coordinator.OpenSession(connected,Execute);
        var before=await original.DispatchAsync(Operation("before-rotation"),CancellationToken.None);
        Require(before.Ok&&executions==1,"current application generation did not dispatch");
        var admitted=original.DispatchAsync(Operation("already-admitted"),CancellationToken.None);
        Require(executions==2&&!admitted.IsCompleted,"pending fixture did not enter captured execution");

        var rotated=coordinator.Apply(Request("rotate"),peer).Result;
        Require(rotated.Ok&&rotated.Credential is not null&&rotated.Generation==2,"session fixture rotate failed");
        var secondSecret=rotated.Credential!;
        var denied=await original.DispatchAsync(Operation("old-after-rotation"),CancellationToken.None);
        Require(!denied.Ok&&denied.Err=="application_auth"&&executions==2,"rotation allowed an old open session to execute");
        pending.SetResult(Success(Operation("already-admitted")));
        Require((await admitted).Ok&&executions==2,"rotation rewrote or replayed already-admitted work");
        var current=coordinator.OpenSession(coordinator.Apply(Request("connect",secondSecret),peer),Execute);
        Require((await current.DispatchAsync(Operation("new-generation"),CancellationToken.None)).Ok&&executions==3,
            "current rotated generation did not dispatch");

        Require(coordinator.Apply(Request("revoke"),peer).Result.State=="revoked","session fixture revoke failed");
        var revoked=await current.DispatchAsync(Operation("old-after-revoke"),CancellationToken.None);
        Require(!revoked.Ok&&revoked.Err=="application_auth"&&executions==3,"revocation allowed an already-open session to execute");
        var reissued=coordinator.Apply(Request("issue"),peer).Result;
        Require(reissued.Ok&&reissued.Credential is not null&&reissued.Generation==3,"explicit reissue failed");
        Require(!(await current.DispatchAsync(Operation("old-after-reissue"),CancellationToken.None)).Ok&&executions==3,
            "reissue silently renewed an old connection");
        var newConnection=coordinator.Apply(Request("connect",reissued.Credential!),peer);
        var renewed=coordinator.OpenSession(newConnection,Execute);
        Require((await renewed.DispatchAsync(Operation("explicit-new-connection"),CancellationToken.None)).Ok&&executions==4,
            "explicitly reconnected current generation failed");

        var currentLease=newConnection.Lease!;
        var wrongCredentialId=currentLease.CredentialId[..4]+(currentLease.CredentialId[4]=='0'?'1':'0')+currentLease.CredentialId[5..];
        foreach(var invalid in new[]{currentLease with{ApplicationId="other-application"},currentLease with{CredentialId=wrongCredentialId},
            currentLease with{Generation=currentLease.Generation+1}})
        {
            var invalidSession=new ApplicationAuthorizedSession(registry,invalid,Execute);
            var result=await invalidSession.DispatchAsync(Operation("invalid-lease"),CancellationToken.None);
            Require(!result.Ok&&result.Err=="application_auth"&&executions==4,"invalid session tuple reached captured execution");
            Require(!ReceiptContains(result,firstSecret)&&!ReceiptContains(result,secondSecret)&&!ReceiptContains(result,reissued.Credential!),
                "session refusal exposed a credential");
        }
        var otherApplication=coordinator.Apply(new(ApplicationAuthorizationProtocol.Version,"other-application","connect",reissued.Credential),peer);
        Require(!otherApplication.Connected&&otherApplication.Lease is null,"other application obtained a connection lease");
        var rejected=coordinator.Apply(Request("connect",Mutate(reissued.Credential!)),peer);
        try{_ = coordinator.OpenSession(rejected,Execute);throw new InvalidOperationException("failed connection opened a session");}
        catch(AgentOpsException error){Require(error.Code=="application_auth"&&executions==4,"failed connection disclosed details or executed");}

        using var canceled=new CancellationTokenSource();canceled.Cancel();
        try{await renewed.DispatchAsync(Operation("canceled-before-admission"),canceled.Token);throw new InvalidOperationException("canceled dispatch ran");}
        catch(OperationCanceledException){Require(executions==4,"canceled operation reached captured execution");}
        Require(!StateContains(state,firstSecret)&&!StateContains(state,secondSecret)&&!StateContains(state,reissued.Credential!),
            "session lifecycle persisted credential text");
        Require(!ReceiptContains(denied,firstSecret)&&!ReceiptContains(revoked,secondSecret),"revoked session receipt exposed credential text");

        var registryPath=Path.Combine(state,"application-authorizations.v1.json");
        var missingCredentialId=System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(registryPath))!.AsObject();
        missingCredentialId["entries"]![0]!["credentialId"]=null;
        var corruptExecutions=0;
        var corruptSession=coordinator.OpenSession(newConnection,(request,_)=>
        {
            corruptExecutions++;
            return Task.FromResult(Success(request));
        });
        foreach(var corruptShape in new[]{"{\"version\":1,\"entries\":null}","{\"version\":1,\"entries\":[null]}",missingCredentialId.ToJsonString()})
        {
            await File.WriteAllTextAsync(registryPath,corruptShape);
            var result=await corruptSession.DispatchAsync(Operation("corrupt-authority-shape"),CancellationToken.None);
            Require(!result.Ok&&result.State=="failed"&&result.Err=="application_auth"&&result.Data is null&&corruptExecutions==0,
                "corrupt registry shape escaped bounded authorization refusal or reached captured execution");
            Require(!ReceiptContains(result,firstSecret)&&!ReceiptContains(result,secondSecret)&&!ReceiptContains(result,reissued.Credential!),
                "corrupt registry refusal exposed a credential");
        }
        await File.WriteAllTextAsync(registryPath,"{not-json");
        var unreadable=await renewed.DispatchAsync(Operation("unreadable-current-authorization"),CancellationToken.None);
        Require(!unreadable.Ok&&unreadable.Err=="application_auth"&&executions==4,"unreadable authority allowed cached session execution");

        static OperationRequest Operation(string rid)=>new("diag",new Dictionary<string,string>(),rid);
        static OperationReceipt Success(OperationRequest request)=>new(true,Guid.NewGuid().ToString("N"),request.Rid!,"done",request.Op);
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
