using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace Pegasus.Cortex.Product;

/// <summary>
/// Windows user-vault custody for one client's random MCP credential. Public enrollment
/// requests contain only its digest. Headers are emitted solely to the requesting client
/// helper's stdout, never to Cortex records, diagnostics, command arguments or config.
/// </summary>
public static class CortexMcpClientCredentials
{
    private const string Prefix = "Pegasus/Cortex/MCP/";
    private const uint Generic = 1;
    private const uint LocalMachinePersistence = 2; // Credential Manager remains scoped to the current Windows user.

    public static CortexMcpConnectionRequest Create(string name, string application, string? chatLabel = null)
    {
        RequireOrdinaryOwner();
        var id = Guid.NewGuid().ToString("N");
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var request = CortexMcpConnections.Validate(new CortexMcpConnectionRequest(CortexMcpConnections.RequestSchema, id,
                Convert.ToHexString(SHA256.HashData(secret)).ToLowerInvariant(), name, application, chatLabel));
            var pin = GCHandle.Alloc(secret, GCHandleType.Pinned);
            try
            {
                var credential = new Credential
                {
                    Type = Generic, TargetName = Prefix + id, CredentialBlobSize = 32,
                    CredentialBlob = pin.AddrOfPinnedObject(), Persist = LocalMachinePersistence,
                    UserName = "Cortex MCP connection"
                };
                if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { pin.Free(); }
            return request;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public static void WritePublicRequest(string path, CortexMcpConnectionRequest request)
    {
        request = CortexMcpConnections.Validate(request);
        path = Path.GetFullPath(path);
        CortexProductLayout.RequirePathBudget(path);
        CortexProductLayout.RejectExistingReparseComponents(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, request, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        stream.Flush(true);
    }

    /// <summary>Called only by the explicit local client header-helper command.</summary>
    public static string ReadHeaderJson(string connectionId)
    {
        if(Environment.GetEnvironmentVariable("PEGASUS_AGENTOPS_CAPTURED")=="1")
            throw new UnauthorizedAccessException("Client credential headers cannot be captured through PAO or a terminal.");
        RequireOrdinaryOwner();
        if (!CortexMcpConnections.IsId(connectionId)) throw new InvalidDataException("Invalid connection ID.");
        if (!CredRead(Prefix + connectionId, Generic, 0, out var native))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        byte[]? secret = null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(native);
            if (credential.Type != Generic || credential.TargetName != Prefix + connectionId ||
                credential.CredentialBlobSize != 32 || credential.CredentialBlob == 0)
                throw new InvalidDataException("MCP client credential is unavailable.");
            secret = new byte[32];
            Marshal.Copy(credential.CredentialBlob, secret, 0, 32);
            return JsonSerializer.Serialize(new Dictionary<string, string>
                { ["Authorization"] = "Bearer " + connectionId + "." + CortexMcpConnections.Base64Url(secret) });
        }
        finally
        {
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
            CredFree(native);
        }
    }

    private static void RequireOrdinaryOwner()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value is not { } sid || sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20" ||
            System.Diagnostics.Process.GetCurrentProcess().SessionId <= 0 ||
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("MCP client credentials belong to the ordinary signed-in user.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint credential);
}
