using System.Text;
using Pegasus.Cortex.Product;

internal static class ProcessorPolicyTests
{
    internal static Task RunAsync()
    {
        var normal=new FakeAffinity([0,1,2,3]);
        var defaults=CortexProcessorPolicy.Apply(CortexProcessorPolicy.Default,normal);
        Check(defaults.Applied&&defaults.Effective.SequenceEqual([1,2,3])&&normal.ApplyCalls==1,
            "default did not exclude CPU0 while retaining all other available processors");
        var explicitState=CortexProcessorPolicy.Apply(Settings(0,2),new FakeAffinity([0,1,2,3]));
        Check(explicitState.Applied&&explicitState.Effective.SequenceEqual([0,2]),"explicit CPU0 selection was refused");
        var single=new FakeAffinity([0]);
        var singleState=CortexProcessorPolicy.Apply(CortexProcessorPolicy.Default,single);
        Check(!singleState.Applied&&singleState.Effective.SequenceEqual([0])&&single.ApplyCalls==0&&singleState.Issue is not null,
            "single-processor default selected an unusable empty mask");
        var groups=new FakeAffinity([0,1,64],2);
        var groupState=CortexProcessorPolicy.Apply(CortexProcessorPolicy.Default,groups);
        Check(!groupState.Supported&&!groupState.Applied&&groupState.Effective.SequenceEqual([0,1,64])&&groups.ApplyCalls==0,
            "multiple groups were silently truncated or changed");
        var unavailable=CortexProcessorPolicy.Apply(Settings(1,5),new FakeAffinity([0,1,2]));
        Check(unavailable.Applied&&unavailable.Effective.SequenceEqual([1])&&unavailable.Issue is not null,
            "unavailable explicit IDs were not reported alongside the applied intersection");
        var none=new FakeAffinity([0,1]);
        var noneState=CortexProcessorPolicy.Apply(Settings(4),none);
        Check(!noneState.Applied&&noneState.Effective.SequenceEqual([0,1])&&none.ApplyCalls==0,
            "unavailable-only selection changed the usable baseline");
        var failed=new FakeAffinity([0,1,2]) { FailApply=true };
        var failedState=CortexProcessorPolicy.Apply(CortexProcessorPolicy.Default,failed);
        Check(!failedState.Applied&&failedState.Effective.SequenceEqual([0,1,2])&&failedState.Issue is not null,
            "an apply error was reported as success or lost the observed effective set");
        var different=new FakeAffinity([0,1,2]) { Retained=[1] };
        Check(!CortexProcessorPolicy.Apply(CortexProcessorPolicy.Default,different).Applied,"different OS readback was claimed applied");

        Check(CortexProcessorPolicy.FormatSelection(Settings(2,0))=="0,2"&&
            CortexProcessorPolicy.ParseSelection("0,2").AllowedLogicalProcessors.SequenceEqual([0,2])&&
            CortexProcessorPolicy.ParseSelection("default").AllowedLogicalProcessors.Length==0,"canonical selection formatting changed");
        foreach(var invalid in new[]{"","0,0","2,0","-1","64","01","0, 1","default,0"})
            Throws<InvalidDataException>(()=>CortexProcessorPolicy.ParseSelection(invalid));
        Throws<InvalidDataException>(()=>CortexProcessorPolicy.Validate(Settings(1) with { Contract="other" }));
        Throws<InvalidDataException>(()=>CortexProcessorPolicy.Validate(Settings(1) with { Version=2 }));
        PersistenceFixture();
        Console.WriteLine("PASS Cortex processor policy (fake affinity adapter and owned file fixture; no real affinity/elevation/service/runtime change)");
        return Task.CompletedTask;
    }

    private static void PersistenceFixture()
    {
        using var temp=new TempRoot();
        var path=Path.Combine(temp.Path,"processors.v1.json");
        var checks=0;
        void Boundary(string candidate)
        {
            Check(Path.GetDirectoryName(candidate)==temp.Path,"persistence escaped its owned fixture directory");
            checks++;
        }
        Check(CortexProcessorPolicy.Read(path,Boundary).AllowedLogicalProcessors.Length==0,"missing optional file lost factory default");
        CortexProcessorPolicy.Save(path,Settings(0,2),Boundary,
            candidate=>new FileStream(candidate,FileMode.CreateNew,FileAccess.Write,FileShare.None));
        Check(CortexProcessorPolicy.Read(path,Boundary).AllowedLogicalProcessors.SequenceEqual([0,2])&&checks>=5,
            "atomic format roundtrip or boundary rechecks were omitted");
        Check(Directory.GetFiles(temp.Path).Length==1,"successful settings save retained scratch files");
        var before=File.ReadAllBytes(path);
        Throws<UnauthorizedAccessException>(()=>CortexProcessorPolicy.Save(path,Settings(1),_=>throw new UnauthorizedAccessException("fixture")));
        Check(File.ReadAllBytes(path).SequenceEqual(before)&&Directory.GetFiles(temp.Path).Length==1,"boundary refusal altered an existing file");
        File.WriteAllText(path,"{\"contract\":\"pegasus-cortex-processors\",\"version\":1,\"allowedLogicalProcessors\":[0],\"unknown\":true}",new UTF8Encoding(false));
        Throws<System.Text.Json.JsonException>(()=>CortexProcessorPolicy.Read(path,Boundary));
        File.WriteAllText(path,new string('x',4097),Encoding.UTF8);
        Throws<InvalidDataException>(()=>CortexProcessorPolicy.Read(path,Boundary));
        // No machine permissions are asserted here: the fixture save injects
        // ordinary file creation instead of the elevated production descriptor.
    }

    private static CortexProcessorSettings Settings(params int[] values)=>new(CortexProcessorPolicy.Contract,1,values);
    private static T Throws<T>(Action action)where T:Exception
    {
        try { action(); }catch(T error){return error;}
        throw new Exception("Expected "+typeof(T).Name);
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}

    private sealed class FakeAffinity(int[] processors,int groups=1):CortexProcessorPolicy.IProcessorAffinity
    {
        private int[] _effective=processors.ToArray();
        public int ApplyCalls { get; private set; }
        public bool FailApply { get; init; }
        public int[]? Retained { get; init; }
        public CortexProcessorPolicy.AffinityBaseline Read()=>new(_effective.ToArray(),groups);
        public int[] Apply(int[] target)
        {
            ApplyCalls++;
            if(FailApply)throw new InvalidOperationException("fixture apply failed");
            _effective=(Retained??target).ToArray();
            return _effective.ToArray();
        }
    }
}
