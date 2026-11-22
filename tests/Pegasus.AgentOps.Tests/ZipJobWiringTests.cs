using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

// Actual owned ZIP files, Engine/scheduler and SQLite. An internal fault point
// holds the original call at a deterministic boundary; no network/product data.
internal static class ZipJobWiringTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal static async Task RunAsync()
    {
        await BeforePublicationAsync();
        await PartialExtractionAsync();
        await TemporaryCleanupFailureAsync();
        await DisposalJoinsOriginalAsync();
        Console.WriteLine("PASS ZIP job wiring: deferred client cancellation, explicit job cancel, original-call Exit/disposal ownership, exact partial extraction, residual-temp evidence and RID replay (owned ZIP/SQLite/fault fixture)");
    }

    private static async Task BeforePublicationAsync()
    {
        await using var fixture=new Fixture(extract:false);
        using var caller=new CancellationTokenSource();
        var accepted=await fixture.Engine.ExecuteAsync(fixture.Request(),caller.Token);
        await fixture.Fault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        Check(!File.Exists(fixture.Destination),"unpublished archive reached final destination");
        var observed=await StatAsync(fixture.Engine,accepted.Jid);
        Check(observed.State=="running","deferred caller cancellation retired the original ZIP worker");
        var canceled=await fixture.Engine.ExecuteAsync(Job("cancel",accepted.Jid));
        Check(canceled.Ok&&JsonSerializer.SerializeToElement(canceled.Data,Json).GetProperty("cancel_requested").GetBoolean(),
            "exact ZIP job cancellation was not requested");
        var waiting=fixture.Engine.ExecuteAsync(Job("wait",accepted.Jid));
        using(fixture.Admission.TryClose()??throw new InvalidOperationException("admission fence failed"))
        {
            Check(!waiting.IsCompleted&&await fixture.Engine.UserExitBlockerAsync(CancellationToken.None)=="work_active",
                "canceled ZIP released original callback/Exit ownership prematurely");
            fixture.Fault.Release();
            var result=await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!result.Ok&&result.State=="canceled"&&result.Err=="canceled"&&!result.Partial,
                "cancellation before publication fabricated a destination effect");
        }
        Check(!File.Exists(fixture.Destination)&&!fixture.Temporaries().Any(),"canceled ZIP left unpublished output");
        Check((await fixture.Engine.ExecuteAsync(fixture.Request())).Jid==accepted.Jid&&fixture.Fault.Entries==1,
            "same RID executed canceled ZIP creation again");
        Check(File.ReadAllText(Path.Combine(fixture.SourceDirectory,"one.txt"))=="owned source one",
            "ZIP cancellation changed source data");
    }

    private static async Task PartialExtractionAsync()
    {
        await using var fixture=new Fixture(extract:true);
        var accepted=await fixture.Engine.ExecuteAsync(fixture.Request());
        await fixture.Fault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Engine.ExecuteAsync(Job("cancel",accepted.Jid));
        var waiting=fixture.Engine.ExecuteAsync(Job("wait",accepted.Jid));
        Check(!waiting.IsCompleted,"partial ZIP extraction finished beneath held original");
        fixture.Fault.Release();
        var result=await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        var data=JsonSerializer.SerializeToElement(result.Data,Json);
        Check(!result.Ok&&result.State=="attention"&&result.Partial&&result.Err=="canceled"&&
            data.GetProperty("cp").GetInt64()==1&&data.GetProperty("changed").GetInt64()==1&&
            data.GetProperty("del").GetInt64()==0&&Directory.GetFiles(fixture.Destination).Length==1,
            "partial ZIP extraction lost committed counters or ran a later entry");
        Check((await fixture.Engine.ExecuteAsync(fixture.Request())).Jid==accepted.Jid&&fixture.Fault.Entries==2,
            "partial extraction was automatically replayed");
    }

    private static async Task TemporaryCleanupFailureAsync()
    {
        await using var fixture=new Fixture(extract:false,lockTemporary:true);
        var accepted=await fixture.Engine.ExecuteAsync(fixture.Request());
        await fixture.Fault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Engine.ExecuteAsync(Job("cancel",accepted.Jid));
        var waiting=fixture.Engine.ExecuteAsync(Job("wait",accepted.Jid));
        fixture.Fault.Release();
        var result=await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        var data=JsonSerializer.SerializeToElement(result.Data,Json);
        Check(!result.Ok&&result.Partial&&result.State=="attention"&&result.Err=="canceled"&&
            data.GetProperty("temporary_cleanup_failed").GetBoolean()&&
            data.GetProperty("changed").GetInt64()==0&&data.GetProperty("cp").GetInt64()==0&&
            fixture.Temporaries().Count()==1&&!File.Exists(fixture.Destination),
            "residual staging file was hidden or counted as a published archive");
    }

    private static async Task DisposalJoinsOriginalAsync()
    {
        await using var fixture=new Fixture(extract:false);
        var accepted=await fixture.Engine.ExecuteAsync(fixture.Request());
        await fixture.Fault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal=fixture.StopEngineAsync();
        Check(!disposal.IsCompleted,"Engine disposal abandoned the owned ZIP callback");
        fixture.Fault.Release();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        using var db=fixture.OpenDatabase();using var command=db.CreateCommand();
        command.CommandText="SELECT receipt FROM jobs WHERE jid=$jid";command.Parameters.AddWithValue("$jid",accepted.Jid);
        var result=JsonSerializer.Deserialize<OperationReceipt>((string)command.ExecuteScalar()!,Json)!;
        Check(!result.Ok&&result.State=="canceled"&&!result.Partial&&!File.Exists(fixture.Destination)&&!fixture.Temporaries().Any(),
            "shutdown lost ZIP cancellation or staging cleanup result");
    }

    private static OperationRequest Job(string action,string jid)=>new("job",new Dictionary<string,string>
        { ["op"]=action,["jid"]=jid });
    private static async Task<OperationReceipt> StatAsync(AgentOpsEngine engine,string jid)
    {
        var result=await engine.ExecuteAsync(Job("stat",jid));
        Check(result.Ok,"job observation failed");
        return JsonSerializer.SerializeToElement(result.Data,Json).GetProperty("rec").Deserialize<OperationReceipt>(Json)!;
    }

    private sealed class HeldFault(bool extract,Func<IEnumerable<string>> temporaries,bool lockTemporary):IZipFaultInjector,IDisposable
    {
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private FileStream? _held;
        internal int Entries;
        internal void Release()=>_release.TrySetResult();
        public void OnFaultPoint(ZipFaultPoint point,string path)
        {
            if(point!=(extract?ZipFaultPoint.BeforeExtractEntry:ZipFaultPoint.BeforePublish))return;
            var entry=Interlocked.Increment(ref Entries);
            if(extract&&entry!=2)return;
            if(lockTemporary)_held=new FileStream(temporaries().Single(),FileMode.Open,FileAccess.Read,FileShare.Read);
            Entered.TrySetResult();
            _release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        public void Dispose() { Release();Interlocked.Exchange(ref _held,null)?.Dispose(); }
    }

    private sealed class Mode:IMaintenanceModeSource
    {
        public MaintenanceModeRecord? Read()=>null;
        public bool IsAcknowledged(MaintenanceEffect effect)=>false;
    }
    private sealed class Fixture:IAsyncDisposable
    {
        internal readonly string Root=Directory.CreateTempSubdirectory("cortex-zip-wiring-").FullName;
        internal readonly UserHostAdmission Admission=new();
        internal readonly string SourceDirectory;
        internal readonly string Source;
        internal readonly string Destination;
        internal readonly HeldFault Fault;
        internal readonly AgentOpsEngine Engine;
        private readonly bool _extract;
        private Task? _disposal;
        internal Fixture(bool extract,bool lockTemporary=false)
        {
            _extract=extract;
            SourceDirectory=Directory.CreateDirectory(Path.Combine(Root,"source")).FullName;
            File.WriteAllText(Path.Combine(SourceDirectory,"one.txt"),"owned source one");
            File.WriteAllText(Path.Combine(SourceDirectory,"two.txt"),"owned source two");
            if(extract)
            {
                Source=Path.Combine(Root,"input.zip");new ZipOperations().Create(SourceDirectory,Source);
                Destination=Directory.CreateDirectory(Path.Combine(Root,"destination")).FullName;
            }
            else { Source=SourceDirectory;Destination=Path.Combine(Root,"output.zip"); }
            Fault=new(extract,Temporaries,lockTemporary);
            Engine=new(Path.Combine(Root,"state"),null,false,null,false,null,maintenanceMode:new Mode(),
                userHostAdmission:Admission,zipOperations:new ZipOperations(Fault));
        }
        internal IEnumerable<string> Temporaries()=>Directory.EnumerateFiles(Root,".output.zip.*.pao.tmp",SearchOption.TopDirectoryOnly);
        internal OperationRequest Request()=>new("zip",new Dictionary<string,string>
        { ["op"]=_extract?"extract":"create",["src"]=Source,["dst"]=Destination,["pol"]=_extract?"replace":"fail",["wait"]="false" },"owned-zip-rid");
        internal Task StopEngineAsync()=>_disposal??=Engine.DisposeAsync().AsTask();
        internal SqliteConnection OpenDatabase()
        {
            var db=new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource=Path.Combine(Root,"state","agentops.db"),Mode=SqliteOpenMode.ReadWrite,Pooling=false }.ToString());
            db.Open();return db;
        }
        public async ValueTask DisposeAsync()
        {
            Fault.Release();await StopEngineAsync();Fault.Dispose();
            var root=Path.GetFullPath(Root);
            var boundary=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))+Path.DirectorySeparatorChar;
            if(!root.StartsWith(boundary,StringComparison.OrdinalIgnoreCase)||
                !Path.GetFileName(root).StartsWith("cortex-zip-wiring-",StringComparison.Ordinal)||
                (File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new IOException("fixture_cleanup_boundary_invalid");
            Directory.Delete(root,recursive:true);
        }
    }
    private static void Check(bool value,string reason) { if(!value)throw new InvalidOperationException(reason); }
}
