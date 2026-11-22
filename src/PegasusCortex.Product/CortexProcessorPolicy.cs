using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Product;

public sealed record CortexProcessorSettings(string Contract,int Version,int[] AllowedLogicalProcessors);

public sealed record CortexProcessorPolicyState(CortexProcessorSettings Settings,int[] Available,
    int[] Requested,int[] Effective,bool Supported,bool Applied,string? Issue);

/// <summary>One optional, machine-wide CPU selection for Cortex resident processes.</summary>
public static class CortexProcessorPolicy
{
    public const string Contract="pegasus-cortex-processors";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive=false
    };
    private static CortexProcessorPolicyState? _current;
    public static CortexProcessorSettings Default=>new(Contract,1,[]);
    public static CortexProcessorPolicyState? Current=>Volatile.Read(ref _current) is { } state
        ?state with { Settings=state.Settings with { AllowedLogicalProcessors=state.Settings.AllowedLogicalProcessors.ToArray() },
            Available=state.Available.ToArray(),Requested=state.Requested.ToArray(),Effective=state.Effective.ToArray() }:null;

    public static CortexProcessorSettings Validate(CortexProcessorSettings settings)
    {
        if(settings.Contract!=Contract||settings.Version!=1||settings.AllowedLogicalProcessors is null||
            settings.AllowedLogicalProcessors.Length>64||settings.AllowedLogicalProcessors.Any(value=>value is <0 or >63)||
            settings.AllowedLogicalProcessors.Distinct().Count()!=settings.AllowedLogicalProcessors.Length)
            throw new InvalidDataException("Processor settings require unique logical processor IDs from 0 through 63.");
        return settings with { AllowedLogicalProcessors=settings.AllowedLogicalProcessors.Order().ToArray() };
    }

    public static CortexProcessorSettings ParseSelection(string selection)
    {
        if(selection=="default")return Default;
        if(string.IsNullOrEmpty(selection)||selection.Length>191)throw new InvalidDataException("Select at least one logical processor.");
        var values=selection.Split(',');
        var processors=new int[values.Length];
        for(var index=0;index<values.Length;index++)
            if(!int.TryParse(values[index],System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out processors[index]))
                throw new InvalidDataException("Processor selection must contain comma-separated logical processor IDs.");
        var result=Validate(new(Contract,1,processors));
        if(selection!=FormatSelection(result))throw new InvalidDataException("Processor selection must be sorted and canonical.");
        return result;
    }

    public static string FormatSelection(CortexProcessorSettings settings)
    {
        var validated=Validate(settings);
        return validated.AllowedLogicalProcessors.Length==0?"default":string.Join(',',validated.AllowedLogicalProcessors);
    }

    public static CortexProcessorSettings ReadForInstallRoot(string installRoot)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        return Read(ResolvePolicyPath(installRoot),RequireStoreBoundary);
    }

    // Only the fixed protected installed Host helper calls this after UAC.
    // This method independently checks elevation; it never initializes a store,
    // changes a DACL, starts a service, or selects another data root.
    public static void SaveForInstallRoot(string installRoot,CortexProcessorSettings settings)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        using var identity=WindowsIdentity.GetCurrent();
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Saving machine processor settings requires Windows elevation.");
        Save(ResolvePolicyPath(installRoot),Validate(settings),RequireStoreBoundary);
    }

    private static string ResolvePolicyPath(string installRoot)=>
        CortexMachineSettings.ResolvePath(installRoot,"processors.v1.json");

    internal static CortexProcessorSettings Read(string path,Action<string> requireBoundary)=>
        CortexMachineSettings.Read(path,Default,bytes=>
            Validate(JsonSerializer.Deserialize<CortexProcessorSettings>(bytes,Json)??throw new InvalidDataException("Processor settings are empty.")),
            requireBoundary,4096,"Processor settings");

    internal static void Save(string path,CortexProcessorSettings settings,Action<string> requireBoundary,
        Func<string,FileStream>? createTemporary=null)=>
        CortexMachineSettings.Save(path,Validate(settings),(stream,value)=>JsonSerializer.Serialize(stream,value,Json),
            requireBoundary,".processors-",createTemporary);

    [SupportedOSPlatform("windows")]
    private static void RequireStoreBoundary(string path)=>CortexMachineSettings.RequireStoreBoundary(path,"Processor settings");

    public static CortexProcessorPolicyState ObserveCurrentProcess()=>Observe(new CurrentProcessAffinity());
    private static CortexProcessorPolicyState Observe(IProcessorAffinity adapter)
    {
        var settings=Default;
        try
        {
            var baseline=adapter.Read();
            return new(settings,baseline.Processors,[],baseline.Processors,baseline.GroupCount==1,false,
                baseline.GroupCount==1?"Processor policy has not been applied by this process.":"Multiple processor groups are not supported by this affinity control; the existing processor set is unchanged.");
        }
        catch(Exception error)when(error is not OutOfMemoryException and not StackOverflowException)
        {return new(settings,[],[],[],false,false,"Processor affinity could not be observed: "+error.GetType().Name);}
    }

    // A resource preference must not prevent either resident from starting.
    public static CortexProcessorPolicyState ApplyForInstallRoot(string installRoot)
    {
        var adapter=new CurrentProcessAffinity();
        CortexProcessorPolicyState result;
        var settings=Default;
        var loaded=false;
        try { settings=ReadForInstallRoot(installRoot);loaded=true;result=Apply(settings,adapter); }
        catch(Exception error)when(error is not OutOfMemoryException and not StackOverflowException)
        {result=Observe(adapter) with { Settings=settings,Issue=(loaded?"Processor affinity could not be observed: ":
            "Saved processor policy could not be read: ")+error.GetType().Name,Applied=false };}
        Volatile.Write(ref _current,result);
        return result;
    }

    internal static CortexProcessorPolicyState Apply(CortexProcessorSettings settings,IProcessorAffinity adapter)
    {
        settings=Validate(settings);
        var baseline=adapter.Read();
        var available=baseline.Processors.Distinct().Order().ToArray();
        var requested=settings.AllowedLogicalProcessors.Length==0?available.Where(value=>value!=0).ToArray():settings.AllowedLogicalProcessors;
        if(baseline.GroupCount!=1)
            return new(settings,available,requested,available,false,false,"Multiple processor groups are not supported by this affinity control; the existing processor set is unchanged.");
        var target=requested.Intersect(available).Order().ToArray();
        if(target.Length==0)
            return new(settings,available,requested,available,true,false,"The selection leaves no available processor. The existing usable processor set is unchanged.");
        var unavailable=requested.Except(available).Any();
        try
        {
            var effective=target.SequenceEqual(available)?available:adapter.Apply(target).Distinct().Order().ToArray();
            var applied=effective.SequenceEqual(target);
            return new(settings,available,requested,effective,true,applied,
                !applied?"Windows retained a different processor set.":unavailable?"Some saved processors are unavailable to this process.":null);
        }
        catch(Exception error)when(error is not OutOfMemoryException and not StackOverflowException)
        {
            int[] effective=[];
            try { effective=adapter.Read().Processors; }
            catch(Exception observation)when(observation is not OutOfMemoryException and not StackOverflowException) { }
            return new(settings,available,requested,effective,true,false,"Processor affinity was not confirmed: "+error.GetType().Name);
        }
    }

    internal sealed record AffinityBaseline(int[] Processors,int GroupCount);
    internal interface IProcessorAffinity
    {
        AffinityBaseline Read();
        int[] Apply(int[] processors);
    }
    private sealed class CurrentProcessAffinity:IProcessorAffinity
    {
        public AffinityBaseline Read()
        {
            if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
            var groups=GetActiveProcessorGroupCount();
            if(groups!=1)return new([],groups);
            using var process=Process.GetCurrentProcess();
            var mask=unchecked((ulong)process.ProcessorAffinity.ToInt64());
            var processors=Enumerable.Range(0,IntPtr.Size*8).Where(value=>(mask&(1UL<<value))!=0).ToArray();
            if(processors.Length==0)throw new InvalidOperationException("Windows returned an empty affinity mask.");
            return new(processors,groups);
        }
        public int[] Apply(int[] processors)
        {
            if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
            ulong mask=0;
            foreach(var processor in processors)mask|=1UL<<processor;
            using(var process=Process.GetCurrentProcess())process.ProcessorAffinity=unchecked((nint)(long)mask);
            return Read().Processors;
        }
        [DllImport("kernel32.dll")]private static extern ushort GetActiveProcessorGroupCount();
    }
}
