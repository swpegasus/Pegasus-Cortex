using Pegasus.Cortex.Product;
using Pegasus.Cortex.Setup;
using System.Diagnostics;
using System.Security.Principal;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

internal static class UserSessionStartupTests
{
    private const string Owner="S-1-5-21-100-200-300-1001";
    private const string Other="S-1-5-21-100-200-300-1002";
    private const string Release="1.2026.09.14.01.55.A";
    private static readonly CortexStartupToken Medium=new(Owner,2,false,0x2000,true);
    private static readonly CortexStartupHost Host=new(@"C:\fixture\Cortex",Release,Release,
        @"C:\fixture\Cortex\bin\host\PegasusCortex.Host.exe",
        @"C:\fixture\Cortex\PegasusCortex.exe",Owner,
        @"C:\fixture\Cortex\bin\pao.exe",@"C:\fixture\Cortex\bin\service\PegasusCortex.ExecutionService.exe");

    public static void Run()
    {
        SelectedMetadata();
        // Native read-only integration: no token mutation, elevation, child process, or installed-state access.
        var actual=CortexUserSessionStartup.InspectCurrentTokenReadOnly();
        using(var identity=WindowsIdentity.GetCurrent())
        using(var process=Process.GetCurrentProcess())
            Require(actual.Sid==identity.User?.Value&&actual.SessionId==process.SessionId&&actual.Primary&&actual.Integrity>0,
                "Native current-process token inspection differs from the actual process identity.");
        Console.WriteLine("PASS user_session_current_token_read_only (native inspection; no token mutation or launch)");
        Require(CortexUserSessionStartup.MatchesUserSessionRegistration(
                Host.Executable,'"'+Host.Executable+'"',Host.Executable),
            "The admitted GUI host registration was rejected.");
        Require(!CortexUserSessionStartup.MatchesUserSessionRegistration(
                Host.StartupExecutable,'"'+Host.StartupExecutable+'"',Host.Executable)&&
            !CortexUserSessionStartup.MatchesUserSessionRegistration(
                Host.Executable,'"'+Host.StartupExecutable+'"',Host.Executable),
            "The console-capable stable launcher was admitted as automatic user-session startup.");
        Console.WriteLine("PASS user_session_autostart_targets_gui_host (no registry mutation or launch)");
        using(var platform=new FakePlatform())
        {
            var result=CortexUserSessionStartup.StartAdmitted(Host,platform);
            Require(result.Disposition=="started"&&result.ProcessId==101&&result.SessionId==2&&!result.Elevated&&
                result.Service=="optional"&&result.Host=="ready"&&result.DashboardProcessId is null,"Medium startup readiness receipt differs.");
            Require(platform.Events.SequenceEqual(["find","launch-host-current","ready"]),"Medium startup ordering differs.");
            var json=JsonSerializer.Serialize(result);
            Require(!json.Contains("DashboardProcessId",StringComparison.Ordinal),"Absent optional dashboard leaked a null field.");
        }
        using(var platform=new FakePlatform{Caller=Medium with{Elevated=true,Integrity=0x3000},Linked=Medium})
        {
            var result=CortexUserSessionStartup.StartAdmitted(Host,platform,dashboard:true);
            Require(result.DashboardProcessId==102&&!result.Elevated,"Elevated parent produced an elevated receipt.");
            Require(platform.Events.SequenceEqual(["find","launch-host-linked","ready","launch-dashboard-linked"]),
                "Same-user UAC must use its real medium linked token and open settings only after daemon readiness.");
        }
        using(var platform=new FakePlatform{Existing=91,ReadyPid=91})
        {
            var result=CortexUserSessionStartup.StartAdmitted(Host,platform);
            Require(result.Disposition=="already-running"&&result.ProcessId==91&&platform.Events.SequenceEqual(["find","ready"]),
                "An admitted existing daemon was duplicated or not rechecked.");
        }
        using(var platform=new FakePlatform{ReadinessFailure=true})
        {
            var error=Reject(()=>CortexUserSessionStartup.StartAdmitted(Host,platform,dashboard:true));
            Require(error.Message.Contains("stage=host-service-readiness",StringComparison.Ordinal),"Readiness failure omitted its bounded stage.");
            Require(!platform.Events.Any(item=>item.Contains("dashboard",StringComparison.Ordinal)),"Dashboard opened despite failed service/host readiness.");
        }
        using(var platform=new FakePlatform{ReadyPid=91})
        {
            var result=CortexUserSessionStartup.StartAdmitted(Host,platform);
            Require(result.ProcessId==91&&platform.Events.SequenceEqual(["find","launch-host-current","ready"]),
                "Readiness did not resolve the actual existing daemon after a concurrent duplicate launch.");
        }

        // The following policy fixtures invoke no native APIs, launch no process, and change no token or installed store.
        foreach(var invalid in new[]
        {
            Medium with{Sid="S-1-5-18"},Medium with{Sid="S-1-5-19"},Medium with{Sid="S-1-5-20"},
            Medium with{SessionId=0},Medium with{Sid=Other},Medium with{Primary=false},
            Medium with{Integrity=0x1000},Medium with{Integrity=0x3000}
        })
        {
            using var platform=new FakePlatform{Caller=invalid};
            Reject(()=>CortexUserSessionStartup.StartAdmitted(Host,platform));
            Require(platform.Events.Count==0,"Invalid identity reached process discovery or launch.");
        }
        using(var platform=new FakePlatform{Interactive=false})
        {
            Reject(()=>CortexUserSessionStartup.StartAdmitted(Host,platform));
            Require(platform.Events.Count==0,"Disconnected/noninteractive session reached startup.");
        }
        foreach(var invalid in new CortexStartupToken?[]
        {
            null,Medium with{Sid=Other},Medium with{SessionId=3},Medium with{Elevated=true},
            Medium with{Integrity=0x1000},Medium with{Integrity=0x3000},Medium with{Primary=false}
        })
        {
            using var platform=new FakePlatform{Caller=Medium with{Elevated=true,Integrity=0x3000},Linked=invalid};
            Reject(()=>CortexUserSessionStartup.StartAdmitted(Host,platform));
            Require(platform.Events.Count==0,"An invalid linked token reached process discovery or launch.");
        }

        foreach(var scheduler in new[]{"single-owner","ordered-conflicts-bounded"})
        {
            var valid="""
                {"ok":true,"state":"done","op":"diag","data":{"product":"Pegasus Cortex","product_version":"RELEASE","protocol":"1.0","mcp_schema":3,"store":"sqlite-wal","fifo":"SCHEDULER","git_ready":false,"pwsh_ready":false}}
                """.Replace("RELEASE",Release,StringComparison.Ordinal).Replace("SCHEDULER",scheduler,StringComparison.Ordinal);
            var fifo="\"fifo\":\""+scheduler+"\"";
            CortexUserSessionStartup.RequireDiagnostic(valid,Release,0);
            CortexUserSessionStartup.RequireDiagnostic(valid.Replace("\"ok\":true","\"ok\":true,\"err\":null,\"partial\":false",StringComparison.Ordinal),Release,0);
            // A responsive owned engine can have active/pending jobs; readiness is not quiescence.
            CortexUserSessionStartup.RequireDiagnostic(valid.Replace("\"git_ready\":false",
                "\"queue_pending\":2,\"queue_active\":1,\"git_ready\":false",StringComparison.Ordinal),Release,0);
            foreach(var invalid in new[]
            {
                valid.Replace("\"done\"","\"waiting_user\"",StringComparison.Ordinal),
                valid.Replace("\"ok\":true","\"ok\":false",StringComparison.Ordinal),
                valid.Replace("\"diag\"","\"ps\"",StringComparison.Ordinal),
                valid.Replace("Pegasus Cortex","Pegasus AgentOps",StringComparison.Ordinal),
                valid.Replace(Release,"1.2026.08.28.00.00.A",StringComparison.Ordinal),
                valid.Replace("\"ok\":true","\"ok\":true,\"partial\":true",StringComparison.Ordinal),
                valid.Replace("\"ok\":true","\"ok\":true,\"err\":\"unavailable\"",StringComparison.Ordinal),
                valid.Replace("\"ok\":true","\"ok\":false,\"ok\":true",StringComparison.Ordinal),
                valid.Replace("\"protocol\":\"1.0\"","\"protocol\":\"bad\",\"protocol\":\"1.0\"",StringComparison.Ordinal),
                valid.Replace("\"store\":\"sqlite-wal\","+fifo+",","\"service\":\"running\",\"tray\":\"ready\",\"queued\":0,",StringComparison.Ordinal),
                valid.Replace("\"store\":\"sqlite-wal\"","\"store\":\"other\"",StringComparison.Ordinal),
                valid.Replace(fifo,"\"fifo\":\"other\"",StringComparison.Ordinal),
                valid.Replace(fifo,"\"fifo\":\"\"",StringComparison.Ordinal),
                valid.Replace(fifo,"\"fifo\":\""+scheduler.ToUpperInvariant()+"\"",StringComparison.Ordinal),
                valid.Replace(fifo,"\"fifo\":null",StringComparison.Ordinal),
                valid.Replace(fifo,"\"fifo\":true",StringComparison.Ordinal),
                valid.Replace(fifo,"\"fifo\":1",StringComparison.Ordinal),
                valid.Replace(fifo,"\"fifo\":[]",StringComparison.Ordinal),
                valid.Replace(fifo+",",string.Empty,StringComparison.Ordinal),
                valid.Replace(fifo,fifo+",\"fifo\":\""+(scheduler=="single-owner"?"ordered-conflicts-bounded":"single-owner")+"\"",StringComparison.Ordinal),
                valid.Replace("\"git_ready\":false","\"service\":\"running\",\"git_ready\":false",StringComparison.Ordinal),
                "{}", "[]", new string('x',16385)
            })Reject(()=>CortexUserSessionStartup.RequireDiagnostic(invalid,Release,0));
            var protocolMismatch=Reject(()=>CortexUserSessionStartup.RequireDiagnostic(
                valid.Replace("\"protocol\":\"1.0\"","\"protocol\":\"2.0\"",StringComparison.Ordinal),Release,0));
            Require(protocolMismatch is CortexProductContractMismatchException {Component:"execution",Contract:"protocol",Expected:"1.0",Actual:"2.0"},
                "PAO protocol skew did not produce the bounded contract-mismatch diagnostic.");
            var schemaMismatch=Reject(()=>CortexUserSessionStartup.RequireDiagnostic(
                valid.Replace("\"mcp_schema\":3","\"mcp_schema\":4",StringComparison.Ordinal),Release,0));
            Require(schemaMismatch is CortexProductContractMismatchException {Component:"pao",Contract:"schema",Expected:"3",Actual:"4"},
                "PAO schema skew did not produce the bounded contract-mismatch diagnostic.");
            Reject(()=>CortexUserSessionStartup.RequireDiagnostic(valid,Release,8));
            Console.WriteLine("PASS user_session_diagnostic_scheduler_"+scheduler+" (deterministic JSON fixtures; no live diagnostic)");
        }
        Require(CortexProductIdentity.Product=="Pegasus Cortex"&&CortexProductIdentity.ExecutionProtocol=="1.0",
            "Update the expected startup diagnostic contract when the product contract changes.");
        Console.WriteLine("PASS user_session_startup_policy_and_readiness (fake policy/receipt fixtures; no live launch)");
    }

    // Explicitly opt-in live test. It neither starts a host nor admits an installation:
    // current catalog coordinates are used only to inspect the already-running signed endpoint.
    public static void ProbeExistingInstalledReadiness(string installRoot)
    {
        var root=CortexCodexFileUpdate.RequireCanonical(installRoot);
        CortexCodexFileUpdate.RequireDirectory(root,allowMissing:false);
        var catalog=CortexProductCatalogV1.ReadInstalledUpgradeSource(
            Path.Combine(CortexDataLocation.ResolveMachineDataRoot(root),"install","active.v1.json"));
        string Role(string role)
        {
            var entry=catalog.Roles.Single(item=>item.Role==role);
            return Path.Combine(root,entry.RelativePath.Replace('/',Path.DirectorySeparatorChar));
        }
        using var identity=WindowsIdentity.GetCurrent();
        var host=new CortexStartupHost(root,catalog.ReleaseVersion,catalog.Roles.Single(item=>item.Role=="host").ComponentVersion,
            Role("host"),Role("launcher"),identity.User!.Value,
            Role("pao"),Role("service"));
        var publicPublisher=Path.Combine(root,"trust","pegasus-prod-alpha-publisher.cer");
        CortexUserSessionStartup.RequireOwnedFile(root,publicPublisher,16*1024);
        using var publisher=X509CertificateLoader.LoadCertificateFromFile(publicPublisher);
        Require(publisher.Thumbprint==CortexProductIdentity.ProdAlphaPublisherThumbprint&&!publisher.HasPrivateKey,"Wrong installed publisher.");
        foreach(var path in new[]{host.Executable,host.StartupExecutable,host.PaoExecutable,host.ServiceExecutable})
        {
            CortexUserSessionStartup.RequireOwnedFile(root,path,128L*1024*1024);
            CortexSignedProductPackageVerifier.VerifyAuthenticode(path,publisher);
        }
        using var platform=new CortexUserSessionStartup.NativeStartupPlatform();
        var existing=platform.FindExisting(host);
        Require(existing is not null,"No existing matching user host; the read-only probe will not launch one.");
        Require(platform.RequireReady(host)==existing,"The existing user host changed during the probe.");
        Console.WriteLine("PASS installed_existing_host_readiness (signed PE identity, medium pipe server, user-engine diag; optional service not required; excludes full installation metadata admission; no host launch)");
    }

    private static void SelectedMetadata()
    {
        var fixture=Path.Combine(Path.GetTempPath(),"cortex-startup-"+Guid.NewGuid().ToString("N"));
        var program=Path.Combine(fixture,"program");
        var selected=Path.Combine(fixture,"data");
        var legacy=Path.Combine(program,"state");
        Directory.CreateDirectory(Path.Combine(selected,"install"));
        Directory.CreateDirectory(Path.Combine(legacy,"install"));
        try
        {
            var roles=CortexProductCatalogV1.StableRolePaths.Select(entry=>new CortexProductRoleV1(entry.Key,
                entry.Key switch { "mcp"=>"semantics","launcher"=>"launcher","setup"=>"installer",_=>"execution" },
                Release,entry.Value)).ToArray();
            var catalog=CortexProductCatalogV1.Create(Release,1,1,1,roles);
            var registration=new CortexWindowsRegistrationReceiptV1(CortexWindowsProductRegistration.ReceiptContract,1,
                Release,CortexProductIdentity.ServiceName,Path.Combine(program,"bin","service","PegasusCortex.ExecutionService.exe"),
                false,Path.Combine(program,"bin"),false,"Pegasus Cortex",Path.Combine(program,"bin","host","PegasusCortex.Host.exe"),
                false,false,null,selected);
            Write(selected,catalog,registration);
            Write(legacy,catalog with { ReleaseVersion="1.2026.08.28.00.00.A" },registration with { DataRoot=legacy });
            var before=Directory.GetFiles(fixture,"*",SearchOption.AllDirectories)
                .ToDictionary(path=>path,File.ReadAllBytes,StringComparer.OrdinalIgnoreCase);
            var observed=new List<string>();
            var metadata=CortexUserSessionStartup.ReadInstalledMetadata(selected,AdmitFixtureFile);
            Require(metadata.Catalog.ReleaseVersion==Release&&metadata.Registration==registration&&observed.Count==2,
                "Startup read a stale legacy catalog or required optional Codex integration.");
            Require(before.All(entry=>entry.Value.SequenceEqual(File.ReadAllBytes(entry.Key)))&&
                Directory.GetFiles(fixture,"*",SearchOption.AllDirectories).Length==before.Count,
                "Startup metadata read changed its fixture.");
            File.Delete(Path.Combine(selected,"install","active.v1.json"));
            Reject(()=>CortexUserSessionStartup.ReadInstalledMetadata(selected,AdmitFixtureFile));
            Write(selected,catalog,registration with { DataRoot=legacy });
            Reject(()=>CortexUserSessionStartup.ReadInstalledMetadata(selected,AdmitFixtureFile));
            var legacyMetadata=CortexUserSessionStartup.ReadInstalledMetadata(legacy,(root,path,maximum)=>
            {
                Require(root==legacy&&Path.GetDirectoryName(path)==Path.Combine(legacy,"install"),"Legacy binding was rebased.");
            });
            Require(legacyMetadata.Registration.DataRoot==legacy,"An explicit legacy data binding was rejected.");
            Console.WriteLine("PASS user_session_selected_metadata (owned file fixtures; injected ACL boundary; no registry, installed admission or launch)");

            void AdmitFixtureFile(string root,string path,long maximum)
            {
                Require(root==selected&&Path.GetDirectoryName(path)==Path.Combine(selected,"install")&&maximum>0,
                    "Startup metadata escaped the selected data boundary.");
                observed.Add(path);
            }
            static void Write(string root,CortexProductCatalogV1 catalog,CortexWindowsRegistrationReceiptV1 registration)
            {
                File.WriteAllText(Path.Combine(root,"install","active.v1.json"),
                    JsonSerializer.Serialize(catalog,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                File.WriteAllText(Path.Combine(root,"install","registration.v1.json"),JsonSerializer.Serialize(registration));
            }
        }
        finally { Directory.Delete(fixture,recursive:true); }
    }

    private static Exception Reject(Action action)
    {
        try{action();}
        catch(Exception exception)when(exception is UnauthorizedAccessException or InvalidOperationException or InvalidDataException or CortexProductContractMismatchException or JsonException){return exception;}
        throw new InvalidOperationException("Invalid user-session startup input was accepted.");
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}

    private sealed class FakePlatform:ICortexUserSessionStartupPlatform
    {
        public CortexStartupToken Caller{get;init;}=Medium;
        public CortexStartupToken? Linked{get;init;}
        public bool Interactive{get;init;}=true;
        public int? Existing{get;init;}
        public int ReadyPid{get;init;}=101;
        public bool ReadinessFailure{get;init;}
        public List<string> Events{get;}=[];
        public int? FindExisting(CortexStartupHost host){Require(host==Host,"Changed fixed target.");Events.Add("find");return Existing;}
        public int Launch(CortexStartupHost host,bool linked,bool dashboard)
        {
            Require(host==Host,"Changed fixed target.");Events.Add("launch-"+(dashboard?"dashboard":"host")+"-"+(linked?"linked":"current"));
            return dashboard?102:101;
        }
        public int RequireReady(CortexStartupHost host)
        {
            Require(host==Host,"Changed fixed target.");Events.Add("ready");
            if(ReadinessFailure)throw new InvalidOperationException("Fake service/host readiness failure.");
            return ReadyPid;
        }
        public void Dispose(){}
    }
}
