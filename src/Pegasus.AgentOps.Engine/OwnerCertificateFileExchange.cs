using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Pegasus.AgentOps.Certificates;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Engine;

internal sealed record CertificatePrivateExportResult(string CertificateId, string Destination, long Bytes, string Sha256);
internal interface IOwnerCertificateFileExchange
{
    X509Certificate2 Import(string source, string sha, string passwordReference, string certificateId, CancellationToken token);
    CertificatePrivateExportResult Export(string destination, CertificateKeyReference key, string passwordReference, CancellationToken token);
}
internal sealed class OwnerCertificateFileExchangeException(string code, bool published = false,
    bool outcomeUnknown = false, bool postconditionObserved = false, Exception? inner = null) : Exception(code, inner)
{
    internal string Code { get; } = code;
    internal bool Published { get; } = published;
    internal bool OutcomeUnknown { get; } = outcomeUnknown;
    internal bool PostconditionObserved { get; } = postconditionObserved;
}

// Private payloads stay in admitted files. Only public identities, byte count
// and output hash cross the operation boundary; passwords use opaque OS refs.
[SupportedOSPlatform("windows")]
internal sealed class OwnerCertificateFileExchange : IOwnerCertificateFileExchange
{
    private readonly string _root;
    private readonly SecurityIdentifier _user;
    private readonly OwnerHostIdentity _identity;
    private readonly IOwnerCertificateRepository _keys;
    private readonly ICertificateSecretResolver _secrets;

    internal OwnerCertificateFileExchange(string root, string ownerSid, IOwnerCertificateRepository keys, ICertificateSecretResolver secrets)
    {
        _identity = new(ownerSid, LocalIdentity.Current);
        _user = new(ownerSid);
        var context = new WindowsPathContextNormalizer().Normalize(root);
        if (context.Kind != "local" || !string.Equals(context.CanonicalPath, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new CertificateOperationException("certificate_workspace_invalid");
        _root = context.CanonicalPath;
        WindowsPathContextNormalizer.RevalidateLocalNoFollow(context);
        _keys = keys; _secrets = secrets;
        RequireOwner();
    }

    public X509Certificate2 Import(string source, string sha, string passwordReference, string certificateId, CancellationToken token)
    {
        RequireOwner(); RequirePath(source);
        return CertificateExchange.ImportProtectedPkcs12File(_root, source, sha, passwordReference, _secrets, certificateId, token);
    }

    public CertificatePrivateExportResult Export(string destination, CertificateKeyReference key, string passwordReference, CancellationToken token)
    {
        RequireOwner(); RequirePath(destination); RequireAbsent(destination);
        token.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new CertificateOperationException("certificate_destination_parent_missing");
        var temporary = Path.Combine(parent, ".cx-" + Guid.NewGuid().ToString("N") + ".tmp");
        if (temporary.Length > 240) throw new CertificateOperationException("certificate_destination_path_invalid");
        FileStream? output = null;
        CertificateFileIdentity? identity = null;
        var publicationAttempted = false;
        var published = false;
        try
        {
            RequirePath(temporary);
            output = new FileInfo(temporary).Create(FileMode.CreateNew,
                FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize,
                FileShare.Read, 4096, FileOptions.WriteThrough,
                CortexUserStateDirectories.CreateWindowsUserFileSecurity(_user));
            identity = new(output.SafeFileHandle, temporary);
            var exported = CertificateExchange.ExportProtectedPkcs12(key, _keys, passwordReference, _secrets, output);
            output.Flush(flushToDisk: true);
            identity.RequireAt(temporary);
            var digest = Hash(output, token);
            if (output.Length != exported.Bytes) throw new CertificateOperationException("certificate_export_size_changed");
            RequireOwner(); RequirePath(destination); RequireAbsent(destination);
            token.ThrowIfCancellationRequested();
            publicationAttempted = true;
            identity.PublishAs(destination);
            published = true;
            // A late cancel cannot erase a published private export. Check the
            // original retained file, never reopen a replacement by pathname.
            identity.RequireAt(destination);
            RequirePath(destination);
            CortexUserStateDirectories.RequireWindowsUserStateAccess(output.GetAccessControl(), _user);
            return new(exported.CertificateId, destination, exported.Bytes, digest);
        }
        catch (Exception failure) when (publicationAttempted && failure is not OutOfMemoryException and not StackOverflowException)
        { throw new OwnerCertificateFileExchangeException("certificate_export_publication_unknown", published, true, false, failure); }
        finally
        {
            if (!publicationAttempted && identity is not null)
            {
                try { identity.DeleteOnClose(); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                { /* Retain an unproven artifact; never delete another object by pathname. */ }
            }
            output?.Dispose();
        }
    }

    private void RequireOwner()
    { if (!_identity.IsCurrentOwner()) throw new CertificateOperationException("certificate_owner_denied"); }
    private void RequirePath(string path)
    {
        var context = new WindowsPathContextNormalizer().Normalize(path);
        if (context.Kind != "local" || !string.Equals(context.CanonicalPath, path, StringComparison.OrdinalIgnoreCase) ||
            !PathNormalizer.Contains(_root, path) || string.Equals(_root, path, StringComparison.OrdinalIgnoreCase))
            throw new CertificateOperationException("certificate_file_not_admitted");
        WindowsPathContextNormalizer.RevalidateLocalNoFollow(context, _root);
    }
    private static void RequireAbsent(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        throw new CertificateOperationException("certificate_destination_exists");
    }
    private static string Hash(FileStream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        try
        {
            long offset = 0;
            int read;
            while ((read = RandomAccess.Read(stream.SafeFileHandle, buffer, offset)) != 0)
            { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); offset += read; }
            token.ThrowIfCancellationRequested();
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
