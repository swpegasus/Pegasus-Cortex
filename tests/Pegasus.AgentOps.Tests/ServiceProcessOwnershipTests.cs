using System.Security.Principal;

internal static class ServiceProcessOwnershipTests
{
    internal static Task RunAsync()
    {
        var user=new FakeToken("S-1-5-21-100-200-300-400","S-1-5-32-544");
        Check(!ServiceProcessOwnership.Ensure(user)&&user.Writes==0&&user.OwnerReads==0,
            "An ordinary user's process token was selected for mutation.");
        var already=new FakeToken("S-1-5-18","S-1-5-18");
        Check(!ServiceProcessOwnership.Ensure(already)&&already.Writes==0,"A SYSTEM-owned token was rewritten.");
        var inherited=new FakeToken("S-1-5-18","S-1-5-32-544");
        Check(ServiceProcessOwnership.Ensure(inherited)&&inherited.Writes==1&&inherited.Owner=="S-1-5-18",
            "The LocalSystem default owner was not selected and read back.");
        Check(!ServiceProcessOwnership.Ensure(inherited)&&inherited.Writes==1,"Owner selection was not idempotent.");
        var foreign=new FakeToken("S-1-5-18","S-1-1-0");
        Reject(()=>ServiceProcessOwnership.Ensure(foreign));
        Check(foreign.Writes==0,"An unexpected existing token owner was silently replaced.");
        var unconfirmed=new FakeToken("S-1-5-18","S-1-5-32-544"){KeepOwner=true};
        Reject(()=>ServiceProcessOwnership.Ensure(unconfirmed));
        Check(unconfirmed.Writes==1,"Failed owner readback did not remain a bounded single attempt.");

        using var actual=WindowsIdentity.GetCurrent();
        Check(actual.User?.IsWellKnown(WellKnownSidType.LocalSystemSid)!=true,
            "The nonmutating native guard fixture must run as an ordinary user, not SYSTEM.");
        var userBefore=actual.User?.Value;
        var ownerBefore=actual.Owner?.Value;
        Check(!ServiceProcessOwnership.EnsureCurrentProcess(),"Native current-user guard attempted a service-token change.");
        using var after=WindowsIdentity.GetCurrent();
        Check(after.User?.Value==userBefore&&after.Owner?.Value==ownerBefore,
            "Native guard changed the ordinary user's token identity or owner.");
        return Task.CompletedTask;
    }

    private static void Reject(Action action)
    {
        try{action();}
        catch(ServiceConfigurationException ex)when(ex.Code=="service_process_owner_invalid"){return;}
        throw new InvalidOperationException("An invalid process owner transition was accepted.");
    }

    private static void Check(bool value,string message)
    {
        if(!value)throw new InvalidOperationException(message);
    }

    private sealed class FakeToken(string user,string owner):IServiceProcessTokenOwner
    {
        internal string Owner{get;private set;}=owner;
        internal int Writes{get;private set;}
        internal int OwnerReads{get;private set;}
        internal bool KeepOwner{get;init;}
        public string UserSid=>user;
        public string OwnerSid{get{OwnerReads++;return Owner;}}
        public void SetSystemOwner(){Writes++;if(!KeepOwner)Owner="S-1-5-18";}
    }
}
