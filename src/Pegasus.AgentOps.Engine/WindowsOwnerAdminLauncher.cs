using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Engine;

/// <summary>Headless owner-session launch; never uses the service token as the child token.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsOwnerAdminLauncher(string stateRoot) : IOwnerAdminLauncher
{
    private const uint QueryToken = 0x0008, PrimaryTokenRights = 0x000b;
    private const string SystemSid = "S-1-5-18", AdministratorsSid = "S-1-5-32-544";
    private static readonly HashSet<string> TrustedWriters = new(StringComparer.Ordinal)
    {
        SystemSid, AdministratorsSid,
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" // TrustedInstaller
    };

    public bool IsElevatedOwner(LocalPeer peer)
        => InspectElevatedPeer(peer, static _ => true);

    // Query the token and creation identity on the SAME limited-query handle.
    // The original launcher retains its own handle; the coordinator compares
    // this observation with that exact generation before releasing a payload.
    internal static CertificateElevationProcessIdentity? CaptureElevatedPeerIdentity(LocalPeer peer)
    {
        if (peer.ApplicationPath is null) return null;
        CertificateElevationProcessIdentity? result = null;
        try
        {
            _ = InspectElevatedPeer(peer, process =>
            {
                if (!GetProcessTimes(process, out var created, out _, out _, out _)) return false;
                result = new(peer.ProcessId!.Value,
                    DateTime.FromFileTimeUtc(created).ToString("O", System.Globalization.CultureInfo.InvariantCulture), peer.ApplicationPath);
                return true;
            });
            return result;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return null; }
    }

    private static bool InspectElevatedPeer(LocalPeer peer, Func<SafeProcessHandle, bool> inspect)
    {
        if (peer.ProcessId is not { } pid || peer.Session <= 0) return false;
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, QueryToken, out var token)) return false;
        using (token) return Matches(token, peer.Sid, peer.Session, requireElevated: true) && inspect(process);
    }

    public void ValidateProtectedInputs(OwnerAdminDefinition definition)
    {
        // Medium-token code must not be able to replace privileged inputs after
        // a broker crash closes its read leases. Stage revisions with the fixed,
        // root-authorized copy operation into an already protected task directory.
        RequireProtectedFile(definition.ExecutablePath);
        RequireProtectedFile(definition.ClientApplicationPath);
        if (definition.ScriptPath is not null) RequireProtectedFile(definition.ScriptPath);
        // Declared data remains under its owner's existing ACL. Hash/read leases
        // bind it while observed; loss of those leases is an unknown outcome,
        // never proof of completion or permission to rerun. Code/config inputs
        // must stay protected even if the broker dies.
        foreach (var input in definition.Inputs.Where(input => input.Kind == "code")) RequireProtectedFile(input.Path);
        RequireProtectedDirectory(definition.WorkingDirectory);
        RequireProtectedDirectory(Path.GetDirectoryName(definition.ExecutablePath)!);
    }

    internal static IDisposable ProtectState(string path)
    {
        using var current = WindowsIdentity.GetCurrent();
        if (current.User?.Value != SystemSid || System.Diagnostics.Process.GetCurrentProcess().SessionId != 0)
            throw new AgentOpsException("admin_service_identity_required");
        PathNormalizer.RejectAbsoluteReparseComponents(Path.GetDirectoryName(path)!);
        if (!Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(new SecurityIdentifier(SystemSid));
            foreach (var sid in new[] { SystemSid, AdministratorsSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
        }
        RequireProtectedDirectory(path, ownerAdminStateRoot: path);
        var handle = CreateFile(path, 0x80000000, 3, 0, 3, 0x02200000, 0); // no FILE_SHARE_DELETE; open reparse point itself
        if (handle.IsInvalid) { var error=Marshal.GetLastWin32Error(); handle.Dispose(); throw Failure("admin_state_unavailable","open_state_directory",error); }
        return handle;
    }

    private static void RequireProtectedFile(string path)
    {
        PathNormalizer.RejectAbsoluteReparseComponents(path);
        var file = new FileInfo(path);
        if (!file.Exists) throw new AgentOpsException("not_found");
        RequireTrustedAcl(file.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), directory: false);
        RequireProtectedDirectory(file.DirectoryName!);
    }

    private static void RequireProtectedDirectory(string path, string? ownerAdminStateRoot = null)
    {
        PathNormalizer.RejectAbsoluteReparseComponents(path);
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) throw new AgentOpsException("not_found");
        var stateRoot = ownerAdminStateRoot is null ? null : Path.GetFullPath(ownerAdminStateRoot);
        if (stateRoot is not null && !WithinStateRoot(stateRoot, directory.FullName))
            throw new AgentOpsException("admin_protected_input_required");
        RequireTrustedAcl(directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), directory: true);
        // An untrusted ancestor with DELETE_CHILD can replace an otherwise
        // protected directory. Creation rights on an existing ancestor alone
        // do not permit replacing its protected child.
        for (var parent = directory.Parent; parent is not null; parent = parent.Parent)
        {
            // State and its fixed runs/RID/temp descendants stay strictly
            // private. Only ancestors ABOVE that known private root use data
            // replacement admission, where creating a sibling or changing an
            // ancestor's attributes cannot rewrite our existing protected child.
            var privateState = stateRoot is not null && WithinStateRoot(stateRoot, parent.FullName);
            RequireTrustedAcl(parent.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner),
                directory: true, ancestor: !privateState, stateDataAncestor: stateRoot is not null && !privateState);
        }
    }

    private static bool WithinStateRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static void RequireTrustedAcl(FileSystemSecurity security, bool directory, bool ancestor = false,
        bool stateDataAncestor = false)
    {
        if (new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null ||
            !TrustedWriters.Contains(security.GetOwner(typeof(SecurityIdentifier))?.Value ?? string.Empty))
            throw new AgentOpsException("admin_protected_input_required");
        var dataAncestor = directory && ancestor && stateDataAncestor;
        var writes = FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.Delete;
        if (!dataAncestor) writes |= FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;
        if (directory) writes |= FileSystemRights.DeleteSubdirectoriesAndFiles;
        if (!ancestor) writes |= FileSystemRights.WriteData | FileSystemRights.AppendData;
        var forbidden = (uint)writes | 0x10000000u; // GenericAll includes replacement/ownership authority.
        if (!dataAncestor) forbidden |= 0x40000000u; // GenericWrite stays forbidden on code/private targets.
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            if ((unchecked((uint)rule.FileSystemRights) & forbidden) != 0 && !TrustedWriters.Contains(rule.IdentityReference.Value))
                throw new AgentOpsException("admin_protected_input_required");
        }
    }

    public IOwnerAdminProcess CreateSuspended(OwnerAdminDefinition definition)
    {
        using var current = WindowsIdentity.GetCurrent();
        if (current.User?.Value != SystemSid || System.Diagnostics.Process.GetCurrentProcess().SessionId != 0)
            throw new AgentOpsException("admin_service_identity_required");
        if (!WTSQueryUserToken(checked((uint)definition.OwnerSession), out var sessionToken)) throw Failure("admin_owner_token_unavailable","wts_owner_token");
        using (sessionToken)
        {
            if (!Matches(sessionToken, definition.OwnerSid, definition.OwnerSession, requireElevated: false))
                throw new AgentOpsException("admin_owner_token_mismatch");
            SafeAccessTokenHandle? linked = null;
            try
            {
                var selected = sessionToken;
                if (!IsElevated(sessionToken))
                {
                    var buffer = ReadToken(sessionToken, 19); // TokenLinkedToken
                    try { linked = new SafeAccessTokenHandle(Marshal.ReadIntPtr(buffer)); }
                    finally { Marshal.FreeHGlobal(buffer); }
                    selected = linked;
                }
                if (!Matches(selected, definition.OwnerSid, definition.OwnerSession, requireElevated: true))
                    throw new AgentOpsException("admin_owner_token_unavailable");
                if (!DuplicateTokenEx(selected, PrimaryTokenRights, 0, 2, 1, out var primary))
                    throw Failure("admin_owner_token_unavailable","duplicate_owner_primary");
                using (primary)
                {
                    if (!Matches(primary, definition.OwnerSid, definition.OwnerSession, requireElevated: true))
                        throw new AgentOpsException("admin_owner_token_mismatch");
                    RequireProfile(primary, definition);
                    var privateStateRoot = Path.Combine(Path.GetFullPath(stateRoot), "owner-admin");
                    var temp = Path.Combine(privateStateRoot, "runs", definition.ExpectedRid, "temp");
                    if (temp.Length > 240) throw new AgentOpsException("precondition");
                    Directory.CreateDirectory(temp);
                    RequireProtectedDirectory(temp, ownerAdminStateRoot: privateStateRoot);
                    var environment = EnvironmentBlock(primary, definition, temp);
                    try
                    {
                        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1, ShowWindow = 0 };
                        var command = new StringBuilder(CommandLine(definition.ExecutablePath, OwnerAdminExecution.Arguments(definition)));
                        // CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW.
                        // No inherited handles, standard streams, desktop, shell or System environment.
                        if (!CreateProcessAsUser(primary, definition.ExecutablePath, command, 0, 0, false,
                                0x08000404, environment, definition.WorkingDirectory, ref startup, out var info))
                            throw Failure("admin_launch_failed");
                        var process = new NativeProcess(info);
                        try
                        {
                            using var child = OpenProcess(0x1000, false, info.ProcessId);
                            if (child.IsInvalid || !OpenProcessToken(child, QueryToken, out var childToken))
                                throw Failure("admin_owner_token_mismatch");
                            using (childToken)
                                if (!Matches(childToken, definition.OwnerSid, definition.OwnerSession, requireElevated: true))
                                    throw new AgentOpsException("admin_owner_token_mismatch");
                            return process;
                        }
                        catch
                        {
                            // The primary thread is still suspended: no reviewed program
                            // instruction has run. Clean up only this failed pre-start child.
                            process.AbortBeforeResume(); process.Dispose(); throw;
                        }
                    }
                    finally { Marshal.FreeHGlobal(environment); }
                }
            }
            finally { linked?.Dispose(); }
        }
    }

    private static void RequireProfile(SafeAccessTokenHandle token, OwnerAdminDefinition definition)
    {
        using var loaded = Registry.Users.OpenSubKey(definition.OwnerSid, writable: false);
        var size = 1024u; var path = new StringBuilder((int)size);
        if(loaded is null)throw new AgentOpsException("admin_owner_profile_unavailable");
        if(!GetUserProfileDirectory(token,path,ref size))throw Failure("admin_owner_profile_unavailable","get_owner_profile");
        if (
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path.ToString()), definition.OwnerProfile))
            throw new AgentOpsException("admin_owner_profile_unavailable");
        PathNormalizer.RejectAbsoluteReparseComponents(definition.OwnerProfile);
    }

    private static nint EnvironmentBlock(SafeAccessTokenHandle token, OwnerAdminDefinition definition, string temp)
    {
        if (!CreateEnvironmentBlock(out var block, token, false)) throw Failure("admin_owner_profile_unavailable");
        try
        {
            var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var position = block; ;)
            {
                var entry = Marshal.PtrToStringUni(position)!;
                if (entry.Length == 0) break;
                var split = entry.IndexOf('=');
                if (split > 0 && new[] { "USERPROFILE", "USERNAME", "USERDOMAIN", "APPDATA", "LOCALAPPDATA" }.Contains(entry[..split], StringComparer.OrdinalIgnoreCase))
                    owner[entry[..split]] = entry[(split + 1)..];
                position += (entry.Length + 1) * 2;
            }
            if (!owner.TryGetValue("USERPROFILE", out var profile) ||
                !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(profile), definition.OwnerProfile))
                throw new AgentOpsException("admin_owner_profile_unavailable");
            var folders=OwnerFolders(owner);
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var executableDirectory = Path.GetDirectoryName(definition.ExecutablePath)!;
            // Deliberately constructed runtime environment. Neither System's process
            // environment nor arbitrary owner variables (possibly secrets) are copied.
            var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemRoot"] = windows, ["WINDIR"] = windows,
                ["PATH"] = string.Join(';', executableDirectory, Path.Combine(windows, "System32"), windows),
                ["PATHEXT"] = AgentOpsEngine.NativeExecutableExtensions,
                ["USERPROFILE"] = definition.OwnerProfile,
                ["APPDATA"] = folders.AppData,
                ["LOCALAPPDATA"] = folders.LocalAppData,
                ["TEMP"] = temp, ["TMP"] = temp, ["PROCESSOR_ARCHITECTURE"] = "AMD64",
                ["ProgramFiles"] = programFiles, ["ProgramW6432"] = programFiles,
                ["ProgramFiles(x86)"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                ["PSModulePath"] = string.Join(';', Path.Combine(programFiles, "PowerShell", "Modules"),
                    Path.Combine(executableDirectory, "Modules"), Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "Modules"))
            };
            AgentOpsExecutionContext.MarkMutationChildEnvironment(values);
            foreach (var name in new[] { "USERNAME", "USERDOMAIN" }) if (owner.TryGetValue(name, out var value)) values[name] = value;
            return Marshal.StringToHGlobalUni(string.Join('\0', values.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
        }
        finally { DestroyEnvironmentBlock(block); }
    }

    internal static (string AppData,string LocalAppData) OwnerFolders(IReadOnlyDictionary<string,string> owner)
    {
        string Folder(string name)
        {
            if(!owner.TryGetValue(name,out var value)||!Path.IsPathFullyQualified(value)||value.StartsWith("\\\\",StringComparison.Ordinal)||value.Length>240||!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(value),value))
                throw new AgentOpsException("admin_owner_profile_unavailable");
            _=new WindowsPathContextNormalizer().Normalize(value);
            return value;
        }
        return (Folder("APPDATA"),Folder("LOCALAPPDATA"));
    }
    public bool IsOriginalProcessAlive(int pid,string start)
    {
        using var process=OpenProcess(0x101000,false,checked((uint)pid));
        if(process.IsInvalid){if(Marshal.GetLastWin32Error()==87)return false;throw Failure("admin_outcome_unknown");}
        if(!GetProcessTimes(process,out var created,out _,out _,out _))throw Failure("admin_outcome_unknown");
        if(DateTime.FromFileTimeUtc(created).ToString("O",System.Globalization.CultureInfo.InvariantCulture)!=start)return false;
        var state=WaitForSingleObject(process,0);
        if(state==0)return false;if(state==0x102)return true;throw Failure("admin_outcome_unknown");
    }
    private static bool Matches(SafeAccessTokenHandle token, string sid, int session, bool requireElevated)
    {
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        var sessionInfo = ReadToken(token, 12); // TokenSessionId
        try
        {
            return identity.User?.Value == sid && Marshal.ReadInt32(sessionInfo) == session &&
                (!requireElevated || IsElevated(token) && HasEnabledAdministrator(identity, token));
        }
        finally { Marshal.FreeHGlobal(sessionInfo); }
    }
    private static bool HasEnabledAdministrator(WindowsIdentity identity, SafeAccessTokenHandle token)
    {
        // IsInRole duplicates a primary token before checking membership, which
        // cannot work on this deliberately query-only peer handle. Groups reads
        // enabled SIDs and excludes disabled/deny-only/logon groups. Refuse a
        // restricted token rather than ignoring its additional SID constraints.
        var restricted = ReadToken(token, 11); // TokenRestrictedSids
        try
        {
            return Marshal.ReadInt32(restricted) == 0 &&
                identity.Groups?.Contains(new SecurityIdentifier(AdministratorsSid)) == true;
        }
        finally { Marshal.FreeHGlobal(restricted); }
    }
    private static bool IsElevated(SafeAccessTokenHandle token)
    {
        var elevation = ReadToken(token, 20);
        var integrity = ReadToken(token, 25);
        try
        {
            var sid = Marshal.ReadIntPtr(integrity);
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            var level = Marshal.ReadInt32(GetSidSubAuthority(sid, checked((uint)(count - 1))));
            return Marshal.ReadInt32(elevation) != 0 && level is >= 0x3000 and < 0x4000;
        }
        finally { Marshal.FreeHGlobal(elevation); Marshal.FreeHGlobal(integrity); }
    }
    private static nint ReadToken(SafeAccessTokenHandle token, int kind)
    {
        GetTokenInformation(token, kind, 0, 0, out var size);
        if (size is < sizeof(int) or > 65536) throw Failure("admin_owner_token_unavailable");
        var buffer = Marshal.AllocHGlobal(size);
        if (GetTokenInformation(token, kind, buffer, size, out var returned) && returned >= sizeof(int) && returned <= size) return buffer;
        var error=Marshal.GetLastWin32Error();
        Marshal.FreeHGlobal(buffer); throw Failure("admin_owner_token_unavailable","get_token_information",error);
    }

    internal static string CommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var command = string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteArgument));
        if (command.Length >= 32767 || command.Contains('\0')) throw new AgentOpsException("invalid");
        return command;
    }

    // Task Scheduler's ExecAction receives only the argument tail. Both native
    // launch paths share the same Windows argument quoting and closed length bound.
    internal static string Arguments(IReadOnlyList<string> arguments)
    {
        var command = string.Join(' ', arguments.Select(QuoteArgument));
        if (command.Length >= 32767 || command.Contains('\0')) throw new AgentOpsException("invalid");
        return command;
    }

    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1);
            else result.Append('\\', slashes);
            slashes = 0; result.Append(character);
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private sealed class NativeProcess : IOwnerAdminProcess
    {
        private readonly SafeProcessHandle _process;
        private readonly SafeWaitHandle _thread;
        private bool _resumed;
        public int Pid { get; }
        public string Start { get; }
        internal NativeProcess(ProcessInformation info)
        {
            _process = new SafeProcessHandle(info.Process, true);
            _thread = new SafeWaitHandle(info.Thread, true);
            Pid = checked((int)info.ProcessId);
            if (!GetProcessTimes(_process, out var created, out _, out _, out _))
            {
                var error=Marshal.GetLastWin32Error();
                TerminateProcess(_process, 1); _thread.Dispose(); _process.Dispose();
                throw Failure("admin_launch_failed","get_process_times",error);
            }
            Start = DateTime.FromFileTimeUtc(created).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }
        public void Resume()
        {
            if (_resumed || ResumeThread(_thread) == uint.MaxValue) throw Failure("admin_launch_failed");
            _resumed = true;
        }
        public bool TryGetExit(out int exit)
        {
            exit = 0;
            var state = WaitForSingleObject(_process, 0);
            if (state == 0x102) return false;
            if (state != 0 || !GetExitCodeProcess(_process, out var code)) throw Failure("admin_outcome_unknown");
            exit = unchecked((int)code); return true;
        }
        internal void AbortBeforeResume() { if (!_resumed) TerminateProcess(_process, 1); }
        public void Dispose() { _thread.Dispose(); _process.Dispose(); } // Deliberately no kill-on-close job.
    }

    private static AgentOpsException Failure(string code,[System.Runtime.CompilerServices.CallerMemberName]string phase="",int? nativeError=null)
    {
        var failure=new AgentOpsException(code,inner:new Win32Exception(nativeError??Marshal.GetLastWin32Error()));
        failure.Data["native_phase"]=phase.ToLowerInvariant();return failure;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved; public string? Desktop; public string? Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize; public nint ReservedPointer, Stdin, Stdout, Stderr;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, nint buffer, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, nint attributes, int impersonation, int type, out SafeAccessTokenHandle primary);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string application, StringBuilder command,
        nint processSecurity, nint threadSecurity, bool inherit, uint flags, nint environment, string cwd, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserProfileDirectory(SafeAccessTokenHandle token, StringBuilder path, ref uint size);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out nint environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(nint environment);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
}
