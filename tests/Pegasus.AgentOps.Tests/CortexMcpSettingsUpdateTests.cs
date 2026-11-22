using Pegasus.Cortex.Mcp.Hosting;

internal static class CortexMcpSettingsUpdateTests
{
    internal static void Run()
    {
        var root=Directory.CreateTempSubdirectory("cortex-mcp-update-").FullName;
        var path=Path.Combine(root,"shared-mcp.v1.json");
        FileStream Temporary(string p)=>new(p,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        try
        {
            CortexMcpSettingsStore.Save(path,CortexMcpSettingsStore.Default,_=>{},Temporary);
            CortexMcpSettingsStore.Update(path,current=>current with { Port=49001 },_=>{},Temporary);
            CortexMcpSettingsStore.Update(path,current=>current with { Enabled=true },_=>{},Temporary);
            var read=CortexMcpSettingsStore.Read(path,_=>{});
            Check(read.Port==49001&&read.Enabled,"serialized merge replaced an unrelated setting");
            Check(!File.Exists(Path.Combine(root,".shared-mcp.write.lock")),"writer coordination leaf was retained after close");
            var before=File.ReadAllBytes(path);
            try { CortexMcpSettingsStore.Update(path,_=>throw new InvalidDataException("fixture conflict"),_=>{},Temporary);
                throw new InvalidOperationException("conflicting update was accepted"); }
            catch(InvalidDataException) { }
            Check(File.ReadAllBytes(path).SequenceEqual(before),"failed update changed saved settings");
            var calls=0;
            using(var competing=new FileStream(Path.Combine(root,".shared-mcp.write.lock"),FileMode.CreateNew,
                FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose))
            {
                try { CortexMcpSettingsStore.Update(path,value=>{calls++;return value with { Enabled=false };},_=>{},Temporary);
                    throw new InvalidOperationException("competing writer was admitted"); }
                catch(IOException) { }
                Check(calls==0&&File.ReadAllBytes(path).SequenceEqual(before),"busy writer executed or changed saved settings");
            }
            CortexMcpSettingsStore.Update(path,current=>current with { Enabled=false },_=>{},Temporary);
            Check(!CortexMcpSettingsStore.Read(path,_=>{}).Enabled,"settled writer handle kept the next update blocked");
        }
        finally
        {
            var full=Path.GetFullPath(root);
            if(Path.GetDirectoryName(full)!=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(full).StartsWith("cortex-mcp-update-",StringComparison.Ordinal) ||
                (File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("fixture_cleanup_boundary_invalid");
            Directory.Delete(full,recursive:true);
        }
        Console.WriteLine("PASS shared MCP settings update: latest-value merge, conflict preservation, native exclusive writer admission and deterministic lock cleanup (owned files; boundary injected, no protected settings)");
    }
    private static void Check(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
}
