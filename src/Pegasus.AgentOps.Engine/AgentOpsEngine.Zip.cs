using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    // The internal constructor can supply only the owned fixture implementation.
    // Requests cannot select fault callbacks, another provider or native flags.
    private readonly ZipOperations _zipOperations;

    private IReadOnlyDictionary<string,object?> Zip(OperationRequest request,CancellationToken token)
    {
        var action=request.Arg.GetValueOrDefault("op","list");
        var source=Required(request,"src");
        try
        {
            if(action is "list" or "test")
            {
                var value=_zipOperations.Inspect(source,null,token);
                return new Dictionary<string,object?>
                {
                    ["src"]=source,["archive_bytes"]=value.ArchiveBytes,["expanded_bytes"]=value.ExpandedBytes,
                    ["compressed_bytes"]=value.CompressedBytes,["entries"]=action=="list"?value.Entries.Take(48).ToArray():null,
                    ["count"]=value.Entries.Count,["truncated"]=value.Entries.Count>48,["tested"]=action=="test"
                };
            }
            var destination=Required(request,"dst");
            var policy=request.Arg.GetValueOrDefault("pol","fail");
            var result=action switch
            {
                "create"=>_zipOperations.Create(source,destination,policy,null,token),
                "extract"=>_zipOperations.Extract(source,destination,policy,null,token),
                _=>throw new AgentOpsException("invalid")
            };
            return new Dictionary<string,object?>
            {
                ["src"]=source,["dst"]=destination,["pol"]=policy,["del"]=result.Deleted,["cp"]=result.Copied,
                ["changed"]=result.Changed,["partial"]=result.Partial,["archive_bytes"]=result.ArchiveBytes,
                ["expanded_bytes"]=result.ExpandedBytes,["entries"]=result.Entries
            };
        }
        catch(ZipOperationException error)
        {
            // A retained staging file is partial even with zero committed entries.
            // Never replace that evidence with a generic untouched cancellation.
            if(error.Code=="canceled"&&!error.Partial)throw new OperationCanceledException(token);
            var data=new Dictionary<string,object?>
            {
                ["src"]=source,["del"]=error.Deleted,["cp"]=error.Copied,["changed"]=error.Changed
            };
            if(request.Arg.TryGetValue("dst",out var destination))data["dst"]=destination;
            if(error.TemporaryCleanupFailed)data["temporary_cleanup_failed"]=true;
            throw new OperationResultException(error.Code,data,error.Partial);
        }
    }
}
