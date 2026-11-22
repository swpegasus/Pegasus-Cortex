using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.AgentOps.Engine;

internal static class ServiceTraceSecurityTests
{
    internal static Task RunAsync()
    {
        using var temp=new TempRoot();
        var root=temp.Dir("trace-state");
        var sid=LocalIdentity.Current().Sid;
        ServiceStateSecurity.RestrictFixture(root);
        var path=Path.Combine(root,"startup.log");
        ServiceStartupTrace.Write(root,"fixture","first");
        Check(File.Exists(path),"Private startup trace was not created.");
        RequireOwner(path,sid);
        Check(new FileInfo(path).GetAccessControl().AreAccessRulesProtected,"New trace lacks its private default ACL.");
        ServiceStateSecurity.Validate(root,sid);

        var inherited=new FileInfo(path).GetAccessControl();
        inherited.SetAccessRuleProtection(false,true);
        new FileInfo(path).SetAccessControl(inherited);
        var inheritedBefore=new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm();
        ServiceStartupTrace.Write(root,"fixture","second");
        RequireOwner(path,sid);
        Check(inheritedBefore.SequenceEqual(new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm()),
            "Safe inherited trace permissions were normalized.");
        Check(File.ReadAllText(path).Contains("detail=second",StringComparison.Ordinal),"Private inherited trace was not admitted.");
        ServiceStateSecurity.Validate(root,sid);

        File.WriteAllText(path,new string('x',64*1024+1));
        ServiceStartupTrace.Write(root,"fixture","rotation");
        RequireOwner(path,sid);
        RequireOwner(Path.Combine(root,"startup.previous.log"),sid);
        ServiceStateSecurity.Validate(root,sid);

        var dayBody=File.ReadAllBytes(path);
        var dayAcl=new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm();
        File.SetLastWriteTimeUtc(path,DateTime.UtcNow.Date.AddDays(-1).AddHours(12));
        ServiceStartupTrace.Write(root,"fixture","day_rotation");
        var previous=Path.Combine(root,"startup.previous.log");
        Check(dayBody.SequenceEqual(File.ReadAllBytes(previous)),
            "Day rotation changed a stored UTC diagnostic line.");
        Check(dayAcl.SequenceEqual(new FileInfo(previous).GetAccessControl().GetSecurityDescriptorBinaryForm()),
            "Day rotation changed the retained trace permissions.");
        RequireOwner(path,sid);
        RequireOwner(previous,sid);
        Check(File.ReadAllText(path).Contains("stage=fixture detail=day_rotation",StringComparison.Ordinal),
            "Day rotation did not append the new diagnostic.");
        ServiceStateSecurity.Validate(root,sid);

        var privateAdmin=ServiceStateSecurity.TraceFileSecurity(sid);
        privateAdmin.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
        ServiceStateSecurity.RequireAdoptableTraceSecurity(privateAdmin,sid);
        ServiceStateSecurity.ValidateSecurity(privateAdmin,sid,false,root,path);
        var foreignOwner=ServiceStateSecurity.TraceFileSecurity(sid);
        foreignOwner.SetOwner(new SecurityIdentifier(WellKnownSidType.WorldSid,null));
        Reject(()=>ServiceStateSecurity.RequireAdoptableTraceSecurity(foreignOwner,sid));
        Reject(()=>ServiceStateSecurity.ValidateSecurity(foreignOwner,sid,false,root,path));

        var unsafeSecurity=new FileInfo(path).GetAccessControl();
        unsafeSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid,null),
            FileSystemRights.Read,AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(unsafeSecurity);
        var beforeAcl=new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm();
        var beforeBody=File.ReadAllBytes(path);
        ServiceStartupTrace.Write(root,"fixture","must-not-write");
        Check(beforeAcl.SequenceEqual(new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm())&&
            beforeBody.SequenceEqual(File.ReadAllBytes(path)),"Unsafe trace ACL was normalized or its content was changed.");
        Reject(()=>ServiceStateSecurity.Validate(root,sid));

        var unrelated=Path.Combine(root,"service.v1.json");
        Reject(()=>ServiceStateSecurity.SealExistingTraceFile(root,unrelated,sid));
        Check(!File.Exists(unrelated),"Trace handling created unrelated configuration.");
        var missing=Path.Combine(temp.Path,"not-created");
        ServiceStartupTrace.Write(missing,"fixture","missing-root");
        ServiceStartupTrace.Write(null,"fixture","no-root");
        Check(!Directory.Exists(missing),"Tracing invented an unprovisioned state directory.");
        var workspaceRoot=temp.Dir("workspace-state");
        ServiceStateSecurity.RestrictFixture(workspaceRoot);
        var workspacePath=Path.Combine(workspaceRoot,"workspaces.json");
        File.WriteAllText(workspacePath,"[]");
        var workspaceSecurity=ServiceStateSecurity.TraceFileSecurity(sid);
        new FileInfo(workspacePath).SetAccessControl(workspaceSecurity);
        var workspaceBefore=File.ReadAllBytes(workspacePath);
        var workspaceAcl=new FileInfo(workspacePath).GetAccessControl().GetSecurityDescriptorBinaryForm();
        Check(!ServiceStateSecurity.RepairLegacyWorkspaceOwner(workspaceRoot,sid)&&
            workspaceBefore.SequenceEqual(File.ReadAllBytes(workspacePath))&&
            workspaceAcl.SequenceEqual(new FileInfo(workspacePath).GetAccessControl().GetSecurityDescriptorBinaryForm()),
            "Already owned workspace metadata or body was changed.");
        workspaceSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid,null),
            FileSystemRights.Read,AccessControlType.Allow));
        new FileInfo(workspacePath).SetAccessControl(workspaceSecurity);
        var unsafeWorkspaceAcl=new FileInfo(workspacePath).GetAccessControl().GetSecurityDescriptorBinaryForm();
        Reject(()=>ServiceStateSecurity.RepairLegacyWorkspaceOwner(workspaceRoot,sid));
        Check(workspaceBefore.SequenceEqual(File.ReadAllBytes(workspacePath))&&
            unsafeWorkspaceAcl.SequenceEqual(new FileInfo(workspacePath).GetAccessControl().GetSecurityDescriptorBinaryForm()),
            "Unsafe workspace state was changed during rejected ownership admission.");
        return Task.CompletedTask;
    }

    private static void RequireOwner(string path,string sid)
    {
        var security=new FileInfo(path).GetAccessControl();
        Check(security.GetOwner(typeof(SecurityIdentifier))?.Value==sid,
            "Trace file did not retain service ownership.");
        ServiceStateSecurity.RequireAdoptableTraceSecurity(security,sid);
    }

    private static void Reject(Action action)
    {
        try{action();}
        catch(ServiceConfigurationException ex)when(ex.Code=="service_state_acl_invalid"){return;}
        throw new InvalidOperationException("Invalid trace ownership or access was accepted.");
    }

    private static void Check(bool value,string message)
    {
        if(!value)throw new InvalidOperationException(message);
    }
}
