using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pegasus.AgentOps.Engine;

internal readonly record struct OwnerCertificateSecretNativeResult(bool Ok, int Error);
internal interface IOwnerCertificateSecretBackend : IOwnerCertificateSecretReader
{
    OwnerCertificateSecretNativeResult Write(string fixedTarget, ReadOnlySpan<byte> password, Action beforeWrite);
    OwnerCertificateSecretNativeResult Delete(string fixedTarget, Action beforeDelete);
}

// A single pointer-only CREDENTIALW definition serves the original reader and
// local provisioner. No private field is marshaled into a managed string.
[StructLayout(LayoutKind.Sequential)]
internal struct OwnerCertificateCredential
{
    internal uint Flags, Type;
    internal nint TargetName, Comment;
    internal uint LastWrittenLow, LastWrittenHigh, BlobBytes;
    internal nint Blob;
    internal uint Persist, AttributeCount;
    internal nint Attributes, TargetAlias, UserName;
    public override readonly string ToString() => "OwnerCertificateCredential(redacted)";
}

internal interface IOwnerCertificateSecretNativeCalls
{
    bool Read(string fixedTarget, out nint allocation);
    bool Write(ref OwnerCertificateCredential credential, uint flags);
    bool Delete(string fixedTarget, uint type, uint flags);
    int LastError { get; }
    void Free(nint allocation);
    void FreeOwnedBuffer(nint allocation, int bytes);
}

[SupportedOSPlatform("windows")]
internal sealed class OwnerCertificateSecretNative : IOwnerCertificateSecretBackend
{
    private const uint MaximumNativeBlobBytes = 5 * 512;
    private readonly IOwnerCertificateSecretNativeCalls _calls;
    private readonly OwnerPasswordPurpose _purpose;
    internal OwnerCertificateSecretNative() : this(OwnerPasswordPurpose.CertificatePassword, new WindowsCalls()) { }
    internal OwnerCertificateSecretNative(IOwnerCertificateSecretNativeCalls calls) : this(OwnerPasswordPurpose.CertificatePassword, calls) { }
    internal OwnerCertificateSecretNative(OwnerPasswordPurpose purpose) : this(purpose, new WindowsCalls()) { }
    internal OwnerCertificateSecretNative(OwnerPasswordPurpose purpose, IOwnerCertificateSecretNativeCalls calls)
    {
        OwnerPasswordTargets.RequirePurpose(purpose);
        _purpose = purpose;
        _calls = calls ?? throw new ArgumentNullException(nameof(calls));
    }

    public OwnerCertificateSecretRead Read(string fixedTarget)
    {
        OwnerPasswordTargets.RequireFixedTarget(_purpose, fixedTarget);
        // Preserve the resolver's one-read/error/struct contract.
        if (!_calls.Read(fixedTarget, out var allocation))
            return new(0, 1, 0, 0, _calls.LastError);
        if (allocation == 0 || allocation == new nint(-1)) return new(0, 1, 0, 0, 87);
        try
        {
            var value = Marshal.PtrToStructure<OwnerCertificateCredential>(allocation);
            return new(allocation, value.Type, value.Blob, value.BlobBytes, 0);
        }
        catch { _calls.Free(allocation); throw; }
    }

    public void Free(nint allocation) => _calls.Free(allocation);

    public OwnerCertificateSecretNativeResult Write(string fixedTarget, ReadOnlySpan<byte> password, Action beforeWrite)
    {
        OwnerPasswordTargets.RequireFixedTarget(_purpose, fixedTarget);
        ArgumentNullException.ThrowIfNull(beforeWrite);
        if (password.Length is < 2 or > 2048 || password.Length % 2 != 0)
            throw new ArgumentException("certificate_secret_blob_invalid");
        nint target = 0, blob = 0;
        try
        {
            target = Marshal.StringToHGlobalUni(fixedTarget); // Fixed nonsecret reference only.
            blob = Marshal.AllocHGlobal(password.Length);
            for (var index = 0; index < password.Length; index++) Marshal.WriteByte(blob, index, password[index]);
            var credential = new OwnerCertificateCredential
            {
                Type = 1, TargetName = target, Blob = blob, BlobBytes = checked((uint)password.Length),
                // Persist2 means this CURRENT USER's subsequent logons on this
                // computer. It does not make the credential readable by SYSTEM.
                Persist = 2
            };
            beforeWrite();
            var ok = _calls.Write(ref credential, 0); // Explicit upsert; no preserve-blob flag.
            return new(ok, ok ? 0 : _calls.LastError);
        }
        finally
        {
            try
            {
                if (blob != 0)
                {
                    for (var index = 0; index < password.Length; index++) Marshal.WriteByte(blob, index, 0);
                    _calls.FreeOwnedBuffer(blob, password.Length);
                }
            }
            finally { if (target != 0) Marshal.FreeHGlobal(target); }
        }
    }

    public OwnerCertificateSecretNativeResult Delete(string fixedTarget, Action beforeDelete)
    {
        OwnerPasswordTargets.RequireFixedTarget(_purpose, fixedTarget);
        ArgumentNullException.ThrowIfNull(beforeDelete);
        beforeDelete();
        var ok = _calls.Delete(fixedTarget, 1, 0);
        return new(ok, ok ? 0 : _calls.LastError);
    }

    internal static void ClearAndFree(OwnerCertificateSecretRead read, IOwnerCertificateSecretReader reader)
    {
        try
        {
            // Preserve the original bounded cleanup, including rejected blobs.
            if (read.Blob != 0 && read.Blob != new nint(-1) && read.BlobBytes <= MaximumNativeBlobBytes)
                for (var index = 0; index < (int)read.BlobBytes; index++) Marshal.WriteByte(read.Blob, index, 0);
        }
        finally { if (read.Allocation != 0 && read.Allocation != new nint(-1)) reader.Free(read.Allocation); }
    }

    private sealed class WindowsCalls : IOwnerCertificateSecretNativeCalls
    {
        public bool Read(string fixedTarget, out nint allocation) => CredReadW(fixedTarget, 1, 0, out allocation);
        public bool Write(ref OwnerCertificateCredential credential, uint flags) => CredWriteW(ref credential, flags);
        public bool Delete(string fixedTarget, uint type, uint flags) => CredDeleteW(fixedTarget, type, flags);
        public int LastError => Marshal.GetLastWin32Error();
        public void Free(nint allocation) => CredFree(allocation);
        public void FreeOwnedBuffer(nint allocation, int bytes) => Marshal.FreeHGlobal(allocation);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref OwnerCertificateCredential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void CredFree(nint credential);
}
