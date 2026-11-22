using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

// This callback is exposed only on the private, authenticated service route.
internal static class MaintenanceProbeProtocol
{
    internal const string Operation="__maintenance_probe";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=8};
    internal static OperationReceipt Handle(AgentOpsEngine engine,MaintenanceHostIdentity host,OperationRequest request)
    {
        try
        {
            if(request.Op!=Operation||request.Root is not null||request.PathRoots is not null||request.Arg.Count!=0||request.Body is null||System.Text.Encoding.UTF8.GetByteCount(request.Body)>2048)throw new AgentOpsException("invalid");
            using var document=JsonDocument.Parse(request.Body,new JsonDocumentOptions{MaxDepth=8});
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var property in document.RootElement.EnumerateObject())if(!names.Add(property.Name))throw new AgentOpsException("invalid");
            var probe=document.RootElement.Deserialize<MaintenanceProbeRequest>(Json)??throw new AgentOpsException("invalid");
            if(probe.Version!=1||!Regex.IsMatch(probe.Epoch,"^[a-f0-9]{32}$",RegexOptions.CultureInvariant)||probe.Action is not("snapshot" or "effect")||probe.After<0||
                probe.Action=="snapshot"&&probe.Jid is not null||probe.Action=="effect"&&(probe.After!=0||probe.Jid is null||!Regex.IsMatch(probe.Jid,"^[a-f0-9]{32}$",RegexOptions.CultureInvariant)))throw new AgentOpsException("invalid");
            var result=engine.ProbeMaintenance(probe,host);
            return new(true,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"done",Operation,Data:new Dictionary<string,object?>{{"maintenance",result}});
        }
        catch(AgentOpsException ex){return Failure(request,ex.Code);}
        catch(Exception ex)when(ex is JsonException or InvalidOperationException or ArgumentException){return Failure(request,"invalid");}
    }
    private static OperationReceipt Failure(OperationRequest request,string error)=>new(false,Guid.NewGuid().ToString("N"),request.Rid??Guid.NewGuid().ToString("N"),"failed",Operation,error);
}
