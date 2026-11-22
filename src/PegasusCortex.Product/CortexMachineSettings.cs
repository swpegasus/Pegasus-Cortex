using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Pegasus.Cortex.Product;

/// <summary>Protected, optional machine metadata in the already-selected data root. Never provisions or repairs permissions.</summary>
internal static class CortexMachineSettings
{
    internal static string ResolvePath(string installRoot,string fileName)
    {
        var root=CortexDataLocation.ResolveMachineDataRoot(installRoot);
        var path=Path.Combine(root,"install",fileName);
        CortexProductLayout.RequirePathBudget(path);
        CortexProductLayout.RejectExistingReparseComponents(path);
        return path;
    }

    internal static T Read<T>(string path,T factoryDefault,Func<byte[],T> deserialize,
        Action<string> requireBoundary,int maximumBytes,string subject)
    {
        requireBoundary(path);
        FileAttributes attributes;
        try { attributes=File.GetAttributes(path); }
        catch(FileNotFoundException) { return factoryDefault; }
        if((attributes&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)
            throw new InvalidDataException(subject+" are not an ordinary file.");
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length<1||stream.Length>maximumBytes)throw new InvalidDataException(subject+" exceed their bounded file format.");
        var bytes=new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return deserialize(bytes);
    }

    internal static void Save<T>(string path,T settings,Action<Stream,T> serialize,
        Action<string> requireBoundary,string temporaryPrefix,Func<string,FileStream>? createTemporary=null)
    {
        requireBoundary(path);
        var temporary=Path.Combine(Path.GetDirectoryName(path)!,temporaryPrefix+Guid.NewGuid().ToString("N")+".tmp");
        CortexProductLayout.RequirePathBudget(temporary);
        try
        {
            using(var stream=(createTemporary??CreateTemporary)(temporary))
            {
                serialize(stream,settings);
                stream.Flush(true);
            }
            requireBoundary(temporary);
            requireBoundary(path);
            File.Move(temporary,path,overwrite:true);
        }
        finally { if(File.Exists(temporary))File.Delete(temporary); }
    }

    // The existing selected install-metadata directory is Admin/SYSTEM writable
    // and ordinarily readable. Existing metadata is validated, never resealed.
    [SupportedOSPlatform("windows")]
    internal static void RequireStoreBoundary(string path,string subject)
    {
        CortexProductLayout.RejectExistingReparseComponents(path);
        var parent=Directory.GetParent(path)??throw new InvalidDataException(subject+" have no directory.");
        var dataRoot=parent.Parent??throw new InvalidDataException(subject+" have no data root.");
        if(!parent.Exists||!dataRoot.Exists)throw new DirectoryNotFoundException("The selected Cortex install metadata is unavailable.");
        RequireMachineSecurity(parent.GetAccessControl(),subject);
        RequireMachineSecurity(dataRoot.GetAccessControl(),subject);
        try
        {
            var attributes=File.GetAttributes(path);
            if((attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)
                throw new InvalidDataException(subject+" are not an ordinary file.");
            RequireMachineSecurity(new FileInfo(path).GetAccessControl(),subject);
        }
        catch(FileNotFoundException) { /* Missing optional settings select the factory policy. */ }
    }

    [SupportedOSPlatform("windows")]
    private static void RequireMachineSecurity(FileSystemSecurity security,string subject)
    {
        var raw=new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(),0);
        if(raw.DiscretionaryAcl is null||raw.Owner?.Value is not("S-1-5-18" or "S-1-5-32-544"))
            throw new UnauthorizedAccessException(subject+" are not protected machine metadata.");
        const FileSystemRights writes=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|
            FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
        var forbidden=(uint)writes|0x10000000u|0x40000000u;
        foreach(FileSystemAccessRule rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow&&(unchecked((uint)rule.FileSystemRights)&forbidden)!=0&&
                rule.IdentityReference.Value is not("S-1-5-18" or "S-1-5-32-544"))
                throw new UnauthorizedAccessException(subject+" are writable by an untrusted principal.");
    }

    private static FileStream CreateTemporary(string path)
    {
        // Only the new temporary file receives this descriptor.
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        var security=new FileSecurity();
        security.SetAccessRuleProtection(true,false);
        security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach(var sid in new[]{"S-1-5-18","S-1-5-32-544"})
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),FileSystemRights.FullControl,AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"),FileSystemRights.Read,AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew,FileSystemRights.Write|FileSystemRights.ReadPermissions|FileSystemRights.Synchronize,
            FileShare.None,4096,FileOptions.None,security);
    }
}
