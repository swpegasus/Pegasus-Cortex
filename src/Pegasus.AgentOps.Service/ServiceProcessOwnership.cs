using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

internal interface IServiceProcessTokenOwner
{
    string UserSid { get; }
    string OwnerSid { get; }
    void SetSystemOwner();
}

internal static class ServiceProcessOwnership
{
    private const string SystemSid="S-1-5-18";
    private const string AdministratorsSid="S-1-5-32-544";

    internal static bool EnsureCurrentProcess()
    {
        using var token=new CurrentProcessToken();
        return Ensure(token);
    }

    internal static bool Ensure(IServiceProcessTokenOwner token)
    {
        // The service creates JSON replacement files, SQLite sidecars and lock
        // files through several libraries. Correct the LocalSystem process's
        // creation default once; do not rewrite state or alter user-host tokens.
        if(token.UserSid!=SystemSid)return false;
        var owner=token.OwnerSid;
        if(owner==SystemSid)return false;
        if(owner!=AdministratorsSid)throw new ServiceConfigurationException("service_process_owner_invalid");
        token.SetSystemOwner();
        if(token.UserSid!=SystemSid||token.OwnerSid!=SystemSid)
            throw new ServiceConfigurationException("service_process_owner_invalid");
        return true;
    }

    private sealed class CurrentProcessToken: IServiceProcessTokenOwner,IDisposable
    {
        private const uint TokenQuery=0x0008;
        private const uint TokenAdjustDefault=0x0080;
        private const int TokenUser=1;
        private const int TokenOwner=4;
        private readonly SafeAccessTokenHandle _token;

        internal CurrentProcessToken()
        {
            if(!OpenProcessToken(GetCurrentProcess(),TokenQuery,out _token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public string UserSid=>ReadSid(_token,TokenUser);
        public string OwnerSid=>ReadSid(_token,TokenOwner);

        public void SetSystemOwner()
        {
            if(!OpenProcessToken(GetCurrentProcess(),TokenQuery|TokenAdjustDefault,out var writable))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using(writable)
            {
                if(ReadSid(writable,TokenUser)!=SystemSid||ReadSid(writable,TokenOwner)!=AdministratorsSid)
                    throw new ServiceConfigurationException("service_process_owner_invalid");
                var system=new SecurityIdentifier(SystemSid);
                var sidBytes=new byte[system.BinaryLength];
                system.GetBinaryForm(sidBytes,0);
                var sid=Marshal.AllocHGlobal(sidBytes.Length);
                var owner=Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Marshal.Copy(sidBytes,0,sid,sidBytes.Length);
                    Marshal.WriteIntPtr(owner,sid);
                    if(!SetTokenInformation(writable,TokenOwner,owner,IntPtr.Size))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally{Marshal.FreeHGlobal(owner);Marshal.FreeHGlobal(sid);}
            }
        }

        private static string ReadSid(SafeAccessTokenHandle token,int informationClass)
        {
            _=GetTokenInformation(token,informationClass,IntPtr.Zero,0,out var bytes);
            if(Marshal.GetLastWin32Error()!=122||bytes<IntPtr.Size||bytes>4096)
                throw new ServiceConfigurationException("service_process_owner_invalid");
            var buffer=Marshal.AllocHGlobal(bytes);
            try
            {
                if(!GetTokenInformation(token,informationClass,buffer,bytes,out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            }
            finally{Marshal.FreeHGlobal(buffer);}
        }

        public void Dispose()=>_token.Dispose();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll",SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process,uint access,out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll",SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(SafeAccessTokenHandle token,int informationClass,IntPtr information,int length,out int returnedLength);
        [DllImport("advapi32.dll",SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetTokenInformation(SafeAccessTokenHandle token,int informationClass,IntPtr information,int length);
    }
}
