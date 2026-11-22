using Pegasus.Cortex.Product;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using System.Text.Json;

internal static class ServiceStartupPathTests
{
    internal static async Task ReadLiveDiagnosticsAsync()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var dispatcher=new NamedPipeDispatcher(NamedPipeDispatcher.UserPipeName,
            expectedServer:new LocalPeer("S-1-5-18",0));
        var receipt=await dispatcher.DispatchAsync(new OperationRequest("diag",new Dictionary<string,string>(),
            Guid.NewGuid().ToString("N")),timeout.Token);
        var allowed=new[]{"service","product_version","maintenance_init_feature","maintenance_init_outcome",
            "maintenance_init_stage","maintenance_init_cause"};
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            receipt.Ok,receipt.State,receipt.Err,receipt.Partial,
            Data=receipt.Data?.Where(entry=>allowed.Contains(entry.Key,StringComparer.Ordinal))
                .ToDictionary(entry=>entry.Key,entry=>entry.Value)
        }));
        if(!receipt.Ok||receipt.Partial)throw new InvalidOperationException("The read-only service diagnostic was unavailable.");
    }

    internal static Task RunAsync()
    {
        // Every location and boundary below is synthetic. The production seam's
        // only I/O delegates are replaced; no registry, ACL, user leaf or service
        // is opened, and no engine/runtime is constructed.
        const string installRoot=@"C:\CortexStartupFixture\program";
        const string dataRoot=@"D:\CortexStartupFixture\data";
        var serviceDirectory=Path.Combine(installRoot,"bin","service");
        var expectedHost=Path.Combine(installRoot,"bin","host","PegasusCortex.Host.exe");
        var configuration=new ServiceConfiguration("owner-private-layout-must-not-be-constructed",Path.Combine(dataRoot,"service"));
        var calls=new List<string>();
        var host=ServiceRuntime.ResolveServiceExitHostExecutable(configuration,serviceDirectory,
            root=>
            {
                Check(root==installRoot,"machine binding received a user/data path");
                calls.Add("machine-binding");
                return dataRoot;
            },
            (root,image)=>
            {
                Check(root==installRoot&&image==expectedHost,"protected image admission received a different role");
                calls.Add("protected-host");
            });
        Check(host==expectedHost&&calls.SequenceEqual(["machine-binding","protected-host"]),
            "external data selection changed the fixed Host image or walked an owner layout");

        var legacyData=Path.Combine(installRoot,"state");
        Check(ServiceRuntime.ResolveServiceExitHostExecutable(configuration with { StateRoot=Path.Combine(legacyData,"service") },
            serviceDirectory,_=>legacyData,(_,image)=>Check(image==expectedHost,"legacy data changed the code role"))==expectedHost,
            "legacy machine data binding no longer resolves the same stable Host");

        var protectionCalls=0;
        Throws<InvalidDataException>(()=>ServiceRuntime.ResolveServiceExitHostExecutable(
            configuration with { StateRoot=Path.Combine(legacyData,"service") },serviceDirectory,_=>dataRoot,
            (_,_)=>protectionCalls++));
        Check(protectionCalls==0,"mismatched service state reached protected image admission");

        var mismatch=new InvalidDataException("synthetic registered install mismatch");
        var actualMismatch=Throws<InvalidDataException>(()=>ServiceRuntime.ResolveServiceExitHostExecutable(
            configuration,serviceDirectory,_=>throw mismatch,(_,_)=>protectionCalls++));
        Check(ReferenceEquals(mismatch,actualMismatch)&&protectionCalls==0,
            "machine-binding refusal was replaced, bypassed or followed by code admission");

        // The same production code boundary must remain mandatory, including
        // ownership, no-reparse and missing-image failures. These injected
        // refusals prove propagation, not a live Windows descriptor inspection.
        foreach(var refusal in new Exception[]
        {
            new UnauthorizedAccessException("synthetic unsafe code protection"),
            new IOException("synthetic reparse code path"),
            new FileNotFoundException("synthetic missing Host")
        })
        {
            var observed=Throws<Exception>(()=>ServiceRuntime.ResolveServiceExitHostExecutable(
                configuration,serviceDirectory,_=>dataRoot,(_,_)=>throw refusal));
            Check(ReferenceEquals(refusal,observed),"protected-image refusal was suppressed or rewritten");
        }

        var resolverCalls=0;
        Throws<InvalidDataException>(()=>ServiceRuntime.ResolveServiceExitHostExecutable(configuration,
            Path.Combine(installRoot,"bin","other"),_=>{resolverCalls++;return dataRoot;},(_,_)=>protectionCalls++));
        Throws<InvalidDataException>(()=>ServiceRuntime.ResolveServiceExitHostExecutable(configuration,
            "relative-service",_=>{resolverCalls++;return dataRoot;},(_,_)=>protectionCalls++));
        Check(resolverCalls==0&&protectionCalls==0,"invalid executing role consulted machine state");

        var longRoot=@"C:\"+new string('x',CortexProductLayout.MaximumWindowsPathCharacters);
        Throws<PathTooLongException>(()=>ServiceRuntime.ResolveServiceExitHostExecutable(configuration,
            Path.Combine(longRoot,"bin","service"),_=>dataRoot,(_,_)=>protectionCalls++));
        Check(protectionCalls==0,"over-budget Host path reached code admission");

        foreach(var fixtureHost in new string?[] { null, @"C:\fixture-host.exe" })
        {
            var fixture=configuration with { IsFixture=true,UserHostExecutable=fixtureHost };
            Check(ServiceRuntime.ResolveServiceExitHostExecutable(fixture,"not-a-production-directory",
                _=>throw new Exception("fixture queried registration"),
                (_,_)=>throw new Exception("fixture inspected code protection"))==fixtureHost,
                "fixture Host short circuit changed");
        }

        Check(AgentOpsWindowsWorker.WindowsServiceFailureExitCode(4)==13,
            "configuration failure was reported as an unrelated Win32 error");
        Check(AgentOpsWindowsWorker.WindowsServiceFailureExitCode(8)==1064,
            "unexpected startup failure was reported as Windows memory exhaustion");
        Console.WriteLine("PASS service startup Host identity and failure mapping (deterministic boundary fixtures; no registry/ACL/service/runtime action)");
        return Task.CompletedTask;
    }

    private static T Throws<T>(Action action) where T:Exception
    {
        try { action(); }
        catch(T exception) { return exception; }
        throw new Exception("Expected "+typeof(T).Name+" refusal.");
    }

    private static void Check(bool value,string message)
    {
        if(!value)throw new Exception(message);
    }
}
