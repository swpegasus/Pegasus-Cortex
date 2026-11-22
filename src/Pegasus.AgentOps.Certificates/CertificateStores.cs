using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;

namespace Pegasus.AgentOps.Certificates;

public enum CertificateStoreScope { CurrentUser, LocalMachine }
public enum CertificateStoreKind { Personal, IntermediateAuthority, Root, TrustedPublisher, TrustedPeople }
public enum CertificateStoreAction { Inventory, AddPublic, InstallTrust, RemovePublic }
public sealed record CertificateStoreTarget(CertificateStoreScope Scope, CertificateStoreKind Store);
public sealed record CertificateStorePage(IReadOnlyList<CertificateSummary> Items, string? NextAfterCertificateId);
public sealed record CertificateStoreMutation(string State, string CertificateId, bool ActionIssued);

public interface ICertificateStoreAdmission
{
    /// <summary>
    /// The execution owner must verify actual identity/elevation and authorize this exact target/action.
    /// This library never selects another token, elevates, displays UAC, or substitutes a store scope.
    /// </summary>
    void Authorize(CertificateStoreTarget target, CertificateStoreAction action);
}

/// <summary>Injectable OS boundary. Public-only writes must never import or remove persisted private keys.</summary>
public interface ICertificateStoreBackend
{
    IReadOnlyList<CertificateSummary> Inventory(CertificateStoreTarget target);
    CertificateStoreMutation AddPublic(CertificateStoreTarget target, byte[] certificateDer);
    CertificateStoreMutation RemovePublic(CertificateStoreTarget target, string certificateId);
}

public sealed class CertificateStoreOperations
{
    private readonly ICertificateStoreBackend _backend;
    private readonly ICertificateStoreAdmission _admission;
    public CertificateStoreOperations(ICertificateStoreBackend backend, ICertificateStoreAdmission admission)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
    }

    public CertificateStorePage Inventory(CertificateStoreTarget target, int limit = 32, string? afterCertificateId = null)
    {
        Validate(target);
        if (limit is < 1 or > 128) throw new CertificateOperationException("store_page_bound");
        if (afterCertificateId is not null) CertificateMaterial.RequireId(afterCertificateId);
        _admission.Authorize(target, CertificateStoreAction.Inventory);
        var source = _backend.Inventory(target);
        if (source.Count > 4096) throw new CertificateOperationException("certificate_store_bound");
        var values = source.GroupBy(value => value.CertificateId, StringComparer.Ordinal)
            .Select(group => group.First() with { HasPrivateKey = group.Any(value => value.HasPrivateKey) })
            .OrderBy(value => value.CertificateId, StringComparer.Ordinal)
            .Where(value => afterCertificateId is null || StringComparer.Ordinal.Compare(value.CertificateId, afterCertificateId) > 0)
            .Take(limit + 1).ToArray();
        return new(values.Take(limit).ToArray(), values.Length > limit ? values[limit - 1].CertificateId : null);
    }

    public CertificateStoreMutation AddPublic(CertificateStoreTarget target, ReadOnlySpan<byte> certificateDer) =>
        Add(target, certificateDer, installTrust: false);

    /// <summary>Explicit trust action, distinct from certificate creation/import and ordinary store insertion.</summary>
    public CertificateStoreMutation InstallTrust(CertificateStoreTarget target, ReadOnlySpan<byte> certificateDer) =>
        Add(target, certificateDer, installTrust: true);

    public CertificateStoreMutation RemovePublic(CertificateStoreTarget target, string certificateId)
    {
        Validate(target); CertificateMaterial.RequireId(certificateId);
        _admission.Authorize(target, CertificateStoreAction.RemovePublic);
        return _backend.RemovePublic(target, certificateId);
    }

    private CertificateStoreMutation Add(CertificateStoreTarget target, ReadOnlySpan<byte> der, bool installTrust)
    {
        Validate(target);
        var isTrustTarget = target.Store is CertificateStoreKind.Root or CertificateStoreKind.TrustedPublisher or CertificateStoreKind.TrustedPeople;
        if (isTrustTarget != installTrust)
            throw new CertificateOperationException(isTrustTarget ? "explicit_trust_action_required" : "trust_target_required");
        using var certificate = CertificateExchange.ImportPublic(der, PublicCertificateFormat.Der);
        if ((target.Store is CertificateStoreKind.Root or CertificateStoreKind.IntermediateAuthority) && !CertificateOperations.Inspect(certificate).IsAuthority)
            throw new CertificateOperationException("authority_certificate_required");
        _admission.Authorize(target, installTrust ? CertificateStoreAction.InstallTrust : CertificateStoreAction.AddPublic);
        return _backend.AddPublic(target, certificate.RawData);
    }

    private static void Validate(CertificateStoreTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!Enum.IsDefined(target.Scope) || !Enum.IsDefined(target.Store)) throw new CertificateOperationException("store_target_invalid");
    }
}

/// <summary>
/// Exact Windows stores only. The caller must wrap this boundary with CertificateStoreOperations and an
/// admitted execution policy. Merely constructing this object never opens a store or establishes trust.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCertificateStoreBackend : ICertificateStoreBackend
{
    public IReadOnlyList<CertificateSummary> Inventory(CertificateStoreTarget target)
    {
        using var store = Open(target, writable: false);
        var certificates = store.Certificates;
        try
        {
            if (certificates.Count > 4096) throw new CertificateOperationException("certificate_store_bound");
            return certificates.Cast<X509Certificate2>().Select(CertificateOperations.Inspect).ToArray();
        }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    public CertificateStoreMutation AddPublic(CertificateStoreTarget target, byte[] certificateDer)
    {
        using var certificate = CertificateExchange.ImportPublic(certificateDer, PublicCertificateFormat.Der);
        var id = CertificateMaterial.Id(certificate);
        using var store = Open(target, writable: true);
        if (Read(store, id) is { } existing) { existing.Dispose(); return new("unchanged", id, false); }
        try
        {
            store.Add(certificate);
            using var observed = Read(store, id);
            if (observed is null) throw new CertificateOperationException("certificate_store_outcome_unknown");
            return new("added", id, true);
        }
        catch (Exception error) when (error is CryptographicException or CertificateOperationException or UnauthorizedAccessException)
        { throw new CertificateOperationException("certificate_store_outcome_unknown"); }
    }

    public CertificateStoreMutation RemovePublic(CertificateStoreTarget target, string certificateId)
    {
        CertificateMaterial.RequireId(certificateId);
        using var store = Open(target, writable: true);
        using var certificate = Read(store, certificateId);
        if (certificate is null) return new("absent", certificateId, false);
        if (certificate.HasPrivateKey) throw new CertificateOperationException("private_key_store_removal_unsupported");
        try
        {
            store.Remove(certificate);
            using var observed = Read(store, certificateId);
            if (observed is not null) throw new CertificateOperationException("certificate_store_outcome_unknown");
            return new("removed", certificateId, true);
        }
        catch (Exception error) when (error is CryptographicException or CertificateOperationException or UnauthorizedAccessException)
        { throw new CertificateOperationException("certificate_store_outcome_unknown"); }
    }

    private static X509Certificate2? Read(X509Store store, string id)
    {
        var certificates = store.Certificates;
        X509Certificate2? selected = null;
        try
        {
            if (certificates.Count > 4096) throw new CertificateOperationException("certificate_store_bound");
            foreach (var certificate in certificates)
            {
                if (CertificateMaterial.Id(certificate) != id) continue;
                if (selected is not null) throw new CertificateOperationException("certificate_identity_ambiguous");
                selected = certificate;
            }
            return selected;
        }
        catch { selected = null; throw; }
        finally { foreach (var certificate in certificates) if (!ReferenceEquals(certificate, selected)) certificate.Dispose(); }
    }

    private static X509Store Open(CertificateStoreTarget target, bool writable)
    {
        ArgumentNullException.ThrowIfNull(target);
        var location = target.Scope switch
        {
            CertificateStoreScope.CurrentUser => StoreLocation.CurrentUser,
            CertificateStoreScope.LocalMachine => StoreLocation.LocalMachine,
            _ => throw new CertificateOperationException("store_target_invalid")
        };
        var name = target.Store switch
        {
            CertificateStoreKind.Personal => StoreName.My,
            CertificateStoreKind.IntermediateAuthority => StoreName.CertificateAuthority,
            CertificateStoreKind.Root => StoreName.Root,
            CertificateStoreKind.TrustedPublisher => StoreName.TrustedPublisher,
            CertificateStoreKind.TrustedPeople => StoreName.TrustedPeople,
            _ => throw new CertificateOperationException("store_target_invalid")
        };
        var store = new X509Store(name, location);
        try { store.Open(OpenFlags.OpenExistingOnly | (writable ? OpenFlags.ReadWrite : OpenFlags.ReadOnly)); return store; }
        catch (Exception error) when (error is CryptographicException or UnauthorizedAccessException)
        { store.Dispose(); throw new CertificateOperationException("certificate_store_unavailable"); }
    }
}
