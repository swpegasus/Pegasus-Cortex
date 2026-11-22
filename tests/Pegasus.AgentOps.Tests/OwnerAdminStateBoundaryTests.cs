using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.AgentOps.Engine;

internal static class OwnerAdminStateBoundaryTests
{
    internal static Task RunAsync()
    {
        var shared=Security((FileSystemRights)278);
        Preserve(shared,()=>AdmitStateAncestor(shared));
        RejectPreserving(shared,()=>WindowsOwnerAdminLauncher.RequireTrustedAcl(shared,directory:true,ancestor:true));
        RejectPreserving(shared,()=>WindowsOwnerAdminLauncher.RequireTrustedAcl(shared,directory:true));
        // The data context cannot weaken either a target or a file merely by
        // being supplied incorrectly: only a directory ANCESTOR uses it.
        RejectPreserving(shared,()=>WindowsOwnerAdminLauncher.RequireTrustedAcl(shared,directory:true,stateDataAncestor:true));
        RejectPreserving(shared,()=>WindowsOwnerAdminLauncher.RequireTrustedAcl(shared,directory:false,ancestor:true,stateDataAncestor:true));

        foreach(var rights in new[]
        {
            FileSystemRights.CreateFiles|FileSystemRights.CreateDirectories,
            FileSystemRights.WriteAttributes|FileSystemRights.WriteExtendedAttributes,
            FileSystemRights.ReadAndExecute,
            (FileSystemRights)0x40000000u // GenericWrite is sibling/attribute access, not child replacement.
        })
        {
            var descriptor=Security(rights);
            Preserve(descriptor,()=>AdmitStateAncestor(descriptor));
        }
        foreach(var rights in new[]
        {
            FileSystemRights.Delete,FileSystemRights.DeleteSubdirectoriesAndFiles,
            FileSystemRights.ChangePermissions,FileSystemRights.TakeOwnership,
            (FileSystemRights)0x10000000u // GenericAll must never hide replacement access.
        })
        {
            var descriptor=Security(rights);
            RejectPreserving(descriptor,()=>AdmitStateAncestor(descriptor));
        }
        foreach(var rights in new[]{FileSystemRights.WriteAttributes,FileSystemRights.WriteExtendedAttributes,
            FileSystemRights.CreateFiles,FileSystemRights.CreateDirectories,(FileSystemRights)0x40000000u,(FileSystemRights)0x10000000u})
        {
            var descriptor=Security(rights);
            RejectPreserving(descriptor,()=>WindowsOwnerAdminLauncher.RequireTrustedAcl(descriptor,directory:true));
            if((rights&(FileSystemRights.CreateFiles|FileSystemRights.CreateDirectories))==0)
                RejectPreserving(descriptor,()=>WindowsOwnerAdminLauncher.RequireTrustedAcl(descriptor,directory:true,ancestor:true));
        }

        var foreignOwner=Security(FileSystemRights.Read);
        foreignOwner.SetOwner(new SecurityIdentifier("S-1-5-32-545"));
        RejectPreserving(foreignOwner,()=>AdmitStateAncestor(foreignOwner));
        var noDacl=new DirectorySecurity();
        var raw=new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent,
            new SecurityIdentifier("S-1-5-32-544"),new SecurityIdentifier("S-1-5-32-544"),null,null);
        var rawBytes=new byte[raw.BinaryLength];raw.GetBinaryForm(rawBytes,0);
        noDacl.SetSecurityDescriptorBinaryForm(rawBytes);
        RejectPreserving(noDacl,()=>AdmitStateAncestor(noDacl));
        RejectPreserving(noDacl,()=>WindowsOwnerAdminLauncher.RequireTrustedAcl(noDacl,directory:true,ancestor:true));

        // Read only the two already-public shared parents. Do not enumerate
        // Cortex state, call ProtectState, create a directory or open a DB/key.
        var programData=Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if(string.IsNullOrWhiteSpace(programData))throw new Exception("ProgramData is unavailable.");
        foreach(var path in new[]{programData,Path.Combine(programData,"Pegasus")})
        {
            var directory=new DirectoryInfo(path);
            if(!directory.Exists||(directory.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new Exception("The explicit shared data parent is unavailable or redirected.");
            var before=directory.GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner);
            var bytes=before.GetSecurityDescriptorBinaryForm();
            Preserve(before,()=>AdmitStateAncestor(before));
            Check(bytes.SequenceEqual(directory.GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner).GetSecurityDescriptorBinaryForm()),
                "Read-only shared-parent admission changed its descriptor.");
        }
        Console.WriteLine("PASS owner-admin data ancestor boundary (pure descriptors plus read-only public parent metadata; no protection/state/DB/key/service/runtime action)");
        return Task.CompletedTask;
    }

    private static void AdmitStateAncestor(DirectorySecurity security)=>
        WindowsOwnerAdminLauncher.RequireTrustedAcl(security,directory:true,ancestor:true,stateDataAncestor:true);

    private static DirectorySecurity Security(FileSystemRights users)
    {
        // Raw ACE masks keep generic-rights vectors intact instead of relying
        // on FileSystemAccessRule's public enum validation/normalization.
        var owner=new SecurityIdentifier("S-1-5-32-544");
        var acl=new RawAcl(GenericAcl.AclRevision,3);
        acl.InsertAce(0,new CommonAce(AceFlags.None,AceQualifier.AccessAllowed,(int)FileSystemRights.FullControl,new SecurityIdentifier("S-1-5-18"),false,null));
        acl.InsertAce(1,new CommonAce(AceFlags.None,AceQualifier.AccessAllowed,(int)FileSystemRights.FullControl,owner,false,null));
        acl.InsertAce(2,new CommonAce(AceFlags.None,AceQualifier.AccessAllowed,unchecked((int)users),new SecurityIdentifier("S-1-5-32-545"),false,null));
        var raw=new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent|ControlFlags.DiscretionaryAclProtected,owner,owner,null,acl);
        var bytes=new byte[raw.BinaryLength];raw.GetBinaryForm(bytes,0);
        var security=new DirectorySecurity();security.SetSecurityDescriptorBinaryForm(bytes);
        return security;
    }
    private static void Preserve(DirectorySecurity security,Action check)
    {
        var before=security.GetSecurityDescriptorBinaryForm();check();
        Check(before.SequenceEqual(security.GetSecurityDescriptorBinaryForm()),"Descriptor admission changed its input.");
    }
    private static void RejectPreserving(DirectorySecurity security,Action check)
    {
        var before=security.GetSecurityDescriptorBinaryForm();
        try{check();throw new Exception("Unsafe boundary was admitted.");}
        catch(AgentOpsException error)when(error.Code=="admin_protected_input_required"){}
        Check(before.SequenceEqual(security.GetSecurityDescriptorBinaryForm()),"Descriptor refusal changed its input.");
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
}
