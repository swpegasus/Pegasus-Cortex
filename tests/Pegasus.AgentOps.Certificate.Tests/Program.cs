using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Certificates;

if (args is ["--server-certificates-only"]) return ServerCertificateTests.Run();
if (args is ["--server-csr-only"]) return ServerCertificateCsrTests.Run();
if (args is ["--machine-server-session-only"])
{
    if (!OperatingSystem.IsWindows()) return 2;
    return MachineServerCertificateSessionTests.Run();
}
if (args.FirstOrDefault() == MachineServerCertificateNativeObservation.Selector)
{
    if (!OperatingSystem.IsWindows() || args is not [MachineServerCertificateNativeObservation.Selector,
        var expectedOwnerSid, var observationDirectory]) return 2;
    return MachineServerCertificateNativeObservation.Run(expectedOwnerSid, observationDirectory);
}
if (args is ["--server-store-owned-only"])
{
    if (!OperatingSystem.IsWindows()) return 2;
    return ServerCertificateTests.RunWindowsOwned();
}
if (args is ["--authenticode-only", var authenticodeFixtureRoot])
    return await WindowsAuthenticodeTests.RunAsync(authenticodeFixtureRoot);
if (args is ["--authenticode-inspect-only", var retainedAuthenticodeFixtureRoot])
    return await WindowsAuthenticodeTests.RunInspectAsync(retainedAuthenticodeFixtureRoot);

var assertions = 0;
var groups = new List<string>();
var currentStep = "in-memory-authoring";
string? expectedFailureCode = null;
string? observedFailureCode = null;
var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
var operations = new CertificateOperations(new FixedClock(now));
try
{
    using var authority = operations.CreateAuthority(new("PAO isolated authority", now.AddDays(-1), now.AddDays(365), 2048));
    using var signer = operations.IssueCodeSigning(new("PAO isolated signer", now.AddHours(-1), now.AddDays(30), 2048), authority);
    using var another = operations.IssueCodeSigning(new("Another isolated signer", now.AddHours(-1), now.AddDays(30), 2048), authority);
    var authorityInfo = CertificateOperations.Inspect(authority);
    var signerInfo = CertificateOperations.Inspect(signer);
    Check(authorityInfo.IsAuthority && authorityInfo.HasPrivateKey, "authority material");
    Check(!signerInfo.IsAuthority && signerInfo.HasPrivateKey, "issued signer material");
    Check(authorityInfo.CertificateId.Length == 64 && signerInfo.CertificateId != authorityInfo.CertificateId, "stable public identity");
    Check(signer.SerialNumber != another.SerialNumber, "issuance serial uniqueness");
    Check(VerifyCertificateSignature(authority, authority), "independent self-signed X509 signature");
    Check(VerifyCertificateSignature(signer, authority), "independent issuer X509 signature");
    using (var chain = new X509Chain())
    {
        ConfigureOffline(chain, authority, now);
        Check(chain.Build(signer), "independent issued chain");
        Check(chain.ChainElements.Count == 2, "leaf and selected authority");
        Check(chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(authority.RawData), "exact selected anchor");
    }
    groups.Add("actual-rsa-authoring-and-issuance");

    foreach (var format in new[] { PublicCertificateFormat.Der, PublicCertificateFormat.Pem })
    {
        var bytes = CertificateOperations.ExportPublic(authority, format);
        using var restored = format == PublicCertificateFormat.Der
            ? X509CertificateLoader.LoadCertificate(bytes)
            : X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(bytes));
        Check(!restored.HasPrivateKey, "public export excludes private key");
        Check(restored.RawData.AsSpan().SequenceEqual(authority.RawData), "public export exact identity");
        Check(CertificateOperations.Inspect(restored).CertificateId == authorityInfo.CertificateId, "portable public identity");
    }
    Check(Throws(() => CertificateOperations.ExportPublic(authority, (PublicCertificateFormat)99), "public_format_unsupported"), "reject private/unknown format");
    groups.Add("public-only-der-and-pem");

    using var otherAuthority = operations.CreateAuthority(new("Different isolated root", now.AddDays(-1), now.AddDays(365), 2048));
    Check(!VerifyCertificateSignature(signer, otherAuthority), "wrong issuer key rejected");
    using (var chain = new X509Chain())
    {
        ConfigureOffline(chain, otherAuthority, now);
        Check(!chain.Build(signer), "wrong selected authority rejected");
    }
    using (var chain = new X509Chain())
    {
        ConfigureOffline(chain, authority, now.AddDays(31));
        Check(!chain.Build(signer), "expired signer rejected");
    }
    Check(Throws(() => operations.IssueCodeSigning(new("too long", now.AddDays(-1), now.AddDays(366), 2048), authority), "validity_invalid"), "leaf validity bounded");
    Check(Throws(() => operations.IssueCodeSigning(new("outside issuer", now.AddDays(-2), now.AddDays(10), 2048), authority), "issuer_validity_exceeded"), "issuer time envelope enforced");
    using var publicAuthority = X509CertificateLoader.LoadCertificate(authority.RawData);
    Check(Throws(() => operations.IssueCodeSigning(new("public issuer", now, now.AddDays(1), 2048), publicAuthority), "issuer_private_key_required"), "issuance requires issuer key");
    Check(Throws(() => operations.IssueCodeSigning(new("not a CA", now, now.AddDays(1), 2048), signer), "issuer_not_authority"), "leaf cannot issue");
    var later = new CertificateOperations(new FixedClock(now.AddDays(366)));
    Check(Throws(() => later.IssueCodeSigning(new("expired issuer", now.AddDays(366), now.AddDays(367), 2048), authority), "certificate_time_invalid"), "expired issuer rejected");
    Check(Throws(() => operations.CreateAuthority(new("bad\nsubject", now, now.AddDays(1), 2048)), "subject_invalid"), "subject controls rejected");
    Check(Throws(() => operations.CreateAuthority(new("weak key", now, now.AddDays(1), 1024)), "key_algorithm_unsupported"), "weak key rejected");
    Check(Throws(() => operations.CreateAuthority(new("rounding", now.AddTicks(1), now.AddDays(1), 2048)), "validity_invalid"), "silent validity rounding rejected");
    groups.Add("issuer-trust-purpose-and-input-rejection");

    ProtectedExchange(authority);
    OrphanKeyLimit(authority);
    StoreAdmission(authority, signer);
    OfflineChain(authority, signer, otherAuthority);

    // Material is ephemeral; only encrypted PFX bytes enter a task-owned scratch file.
    // All store mutations below use an injected fake, never a Windows trust/key store.
    Console.WriteLine(JsonSerializer.Serialize(new { ok = true, assertions, groups,
        evidence = "isolated .NET X509/PFX and custom-root chain checks; injected admission/store checks",
        revocation = "explicit NoCheck and Offline classifications exercised", aiaDownloads = "disabled",
        persistedSecurityMaterial = "encrypted scratch PFX only; removed in finally", osStoreMutations = "none performed; fake only",
        fileSigning = "This selector covers the adapter contract; use --authenticode-only for isolated native file signing; CMS deferred",
        paoIntegration = "not wired; not tested", installedActivation = "not performed" }));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, assertions, completedGroups = groups,
        failureType = error.GetType().Name,
        step = currentStep,
        failedCheck = error is FixtureAssertionFailure assertionFailure ? assertionFailure.Check : null,
        expectedCode = expectedFailureCode,
        observedCode = observedFailureCode,
        code = error is CertificateOperationException certificateError ? certificateError.Code : "test_failure" }));
    return 1;
}

void Check(bool condition, string label)
{
    assertions++;
    if (!condition) throw new FixtureAssertionFailure(label);
    expectedFailureCode = null;
    observedFailureCode = null;
}

void ProtectedExchange(X509Certificate2 certificate)
{
    currentStep = "protected-exchange";
    var id = CertificateOperations.Inspect(certificate).CertificateId;
    var reference = new CertificateKeyReference("isolated-key", id);
    var keys = new FixtureKeys(certificate);
    var secrets = new FixtureSecrets("isolated nonproduction password");
    var scratch = Path.Combine(Path.GetTempPath(), "pao-cert-test-" + Guid.NewGuid().ToString("N"));
    if (Directory.Exists(scratch)) throw new InvalidOperationException("scratch collision");
    Directory.CreateDirectory(scratch);
    var path = Path.Combine(scratch, "protected.pfx");
    try
    {
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var result = CertificateExchange.ExportProtectedPkcs12(reference, keys, "password-ref", secrets, output);
            Check(result.CertificateId == id && result.Format == "pkcs12-protected" && result.Bytes > 0, "protected export metadata");
        }
        using (var input = File.OpenRead(path))
        using (var restored = CertificateExchange.ImportProtectedPkcs12(input, "password-ref", secrets, id))
            Check(restored.HasPrivateKey && restored.RawData.AsSpan().SequenceEqual(certificate.RawData), "protected ephemeral PFX roundtrip");
        using (var input = File.OpenRead(path))
            Check(Throws(() => CertificateExchange.ImportProtectedPkcs12(input, "password-ref", new FixtureSecrets("wrong isolated password"), id), "pkcs12_import_invalid"), "wrong password rejected");
        using (var input = File.OpenRead(path))
            Check(Throws(() => CertificateExchange.ImportProtectedPkcs12(input, "password-ref", secrets, new string('0', 64)), "certificate_identity_mismatch"), "PFX cannot substitute a different key identity");
        using var rejectedOutput = new MemoryStream();
        Check(Throws(() => CertificateExchange.ExportProtectedPkcs12(reference with { ExpectedCertificateId = new string('0', 64) }, keys,
            "password-ref", secrets, rejectedOutput), "certificate_identity_mismatch") && rejectedOutput.Length == 0, "key reference pinned before output");
        using var brokenOutput = new PartialWriteStream();
        var writeError = Capture(() => CertificateExchange.ExportProtectedPkcs12(reference, keys, "password-ref", secrets, brokenOutput));
        Check(writeError.Code == "certificate_export_output_unknown" && brokenOutput.ObservedBytes > 0 &&
            ReferenceEquals(writeError.InnerException, brokenOutput.Failure), "partial IO output preserves uncertainty and cause");
        using var cryptoOutput = new PartialWriteStream(new CryptographicException("isolated post-prefix failure"));
        writeError = Capture(() => CertificateExchange.ExportProtectedPkcs12(reference, keys, "password-ref", secrets, cryptoOutput));
        Check(writeError.Code == "certificate_export_output_unknown" && cryptoOutput.ObservedBytes > 0 &&
            ReferenceEquals(writeError.InnerException, cryptoOutput.Failure), "post-prefix cryptographic failure is not generation failure");
        var pem = CertificateOperations.ExportPublic(certificate, PublicCertificateFormat.Pem);
        var repeated = pem.Concat(pem).ToArray();
        currentStep = "public-import-repeated-pem-rejection";
        Check(Throws(() => CertificateExchange.ImportPublic(repeated, PublicCertificateFormat.Pem), "public_certificate_only"), "multiple PEM blocks rejected");
        currentStep = "public-import-private-pem-rejection";
        Check(Throws(() => CertificateExchange.ImportPublic(Encoding.UTF8.GetBytes("-----BEGIN PRIVATE KEY-----\nAA==\n-----END PRIVATE KEY-----"), PublicCertificateFormat.Pem),
            "public_certificate_only"), "public import refuses private key material");
        currentStep = "secret-lease-metadata";
        using var lease = new CertificateSecret("nonproduction value");
        string? serializedLease = null;
        var serializationRejected = false;
        try { serializedLease = JsonSerializer.Serialize(lease); }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException)
        {
            // Ref-like secret accessors may refuse metadata generation. That is a fail-closed result.
            serializationRejected = true;
        }
        // Keep assertions outside the catch: a failing Check must never be swallowed as serializer rejection.
        Check(serializationRejected || serializedLease == "{}", "secret lease serialization is empty or explicitly rejected");
        Check(!lease.ToString().Contains("nonproduction", StringComparison.Ordinal), "secret lease display excludes material");
        Check(Throws(() => new CertificateSecret("isolated\0invalid"), "secret_invalid"), "NUL password rejected before standard export API");
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
        Directory.Delete(scratch, recursive: false);
    }
    Check(!Directory.Exists(scratch), "owned encrypted scratch removed");
    groups.Add("protected-pfx-stream-and-isolated-file-roundtrip");
}

void OrphanKeyLimit(X509Certificate2 certificate)
{
    currentStep = "orphan-key-loader-admission";
    const string password = "isolated orphan-key password";
    var encoded = OrphanKeyFixture.Create(certificate, password);
    try
    {
        var baseline = X509CertificateLoader.LoadPkcs12Collection(encoded, password,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        try
        {
            Check(baseline.Count == 1 && baseline.Cast<X509Certificate2>().Count(value => value.HasPrivateKey) == 1,
                "valid standard PFX orphan fixture hides a second key from certificate counting");
        }
        finally { foreach (var value in baseline) value.Dispose(); }
        using var input = new CapturingReadStream(encoded);
        var error = Capture(() => CertificateExchange.ImportProtectedPkcs12(input, "password-ref", new FixtureSecrets(password),
            CertificateOperations.Inspect(certificate).CertificateId));
        Check(error.Code == "pkcs12_import_invalid" && error.InnerException is Pkcs12LoadLimitExceededException,
            "loader rejects the orphan key bag before certificate selection");
        Check(input.ObservedBuffer is { } observed && observed.All(value => value == 0), "fixed read backing storage cleared on rejection");
    }
    finally { CryptographicOperations.ZeroMemory(encoded); }
    groups.Add("actual-standard-pfx-orphan-key-loader-limit-and-buffer-clearing");
}

void StoreAdmission(X509Certificate2 authority, X509Certificate2 leaf)
{
    currentStep = "fake-store-admission";
    var backend = new FixtureStores();
    var admission = new FixtureAdmission();
    var stores = new CertificateStoreOperations(backend, admission);
    var user = new CertificateStoreTarget(CertificateStoreScope.CurrentUser, CertificateStoreKind.Personal);
    var machine = user with { Scope = CertificateStoreScope.LocalMachine };
    var trust = user with { Store = CertificateStoreKind.Root };
    var authorityId = CertificateOperations.Inspect(authority).CertificateId;
    var leafId = CertificateOperations.Inspect(leaf).CertificateId;
    Check(stores.AddPublic(user, authority.RawData).State == "added", "explicit public insertion");
    Check(stores.AddPublic(user, authority.RawData).State == "unchanged", "same public insertion idempotent");
    Check(stores.Inventory(machine).Items.Count == 0, "scope never substitutes user for machine");
    Check(Throws(() => stores.AddPublic(trust, authority.RawData), "explicit_trust_action_required"), "ordinary insertion cannot establish trust");
    Check(Throws(() => stores.InstallTrust(trust, leaf.RawData), "authority_certificate_required"), "root store requires authority certificate");
    admission.Deny = true;
    var before = backend.Calls;
    Check(Throws(() => stores.InstallTrust(trust, authority.RawData), "fixture_denied") && backend.Calls == before, "denial precedes OS boundary");
    admission.Deny = false;
    Check(stores.InstallTrust(trust, authority.RawData).State == "added" && admission.LastAction == CertificateStoreAction.InstallTrust,
        "trust insertion explicitly admitted");
    stores.AddPublic(user, leaf.RawData);
    var first = stores.Inventory(user, limit: 1);
    var second = stores.Inventory(user, limit: 1, afterCertificateId: first.NextAfterCertificateId);
    Check(first.Items.Count == 1 && second.Items.Count == 1 && first.Items[0].CertificateId != second.Items[0].CertificateId && second.NextAfterCertificateId is null,
        "bounded inventory continuation without duplicate coordinates");
    Check(stores.RemovePublic(user, leafId).State == "removed" && stores.RemovePublic(user, leafId).State == "absent", "exact public removal");
    Check(stores.Inventory(trust).Items.Single().CertificateId == authorityId, "removal preserves another store");
    groups.Add("fake-store-explicit-scope-trust-admission-and-pagination");
}

void OfflineChain(X509Certificate2 authority, X509Certificate2 leaf, X509Certificate2 wrongAuthority)
{
    currentStep = "offline-chain-classification";
    var id = CertificateOperations.Inspect(leaf).CertificateId;
    var policy = new CertificateChainRequest(CertificateTrustSelection.CustomRoots, CertificateRevocationSelection.NotChecked,
        now, [authority.RawData]);
    var good = CertificateChainOperations.VerifyCodeSigning(leaf, id, policy);
    Check(good.Trusted && good.TrustSelection == "CustomRoots" && good.Revocation == "not_checked", "custom root trust distinct from system trust and revocation");
    Check(!CertificateChainOperations.VerifyCodeSigning(leaf, id, policy with { CustomRootCertificates = [wrongAuthority.RawData] }).Trusted,
        "wrong root not trusted");
    Check(!CertificateChainOperations.VerifyCodeSigning(leaf, id, policy with { VerificationTime = now.AddDays(31) }).Trusted,
        "expired signing certificate is not trusted");
    Check(Throws(() => CertificateChainOperations.VerifyCodeSigning(leaf, id, policy with { Trust = CertificateTrustSelection.System }), "chain_policy_invalid"),
        "system/custom root mixing rejected");
    var offline = CertificateChainOperations.VerifyCodeSigning(leaf, id, policy with { Revocation = CertificateRevocationSelection.Offline });
    Check(offline.Revocation is "offline_checked" or "offline_indeterminate", "actual offline eligible chain classification");
    Check(CertificateChainOperations.DescribeRevocation(CertificateRevocationSelection.Offline, eligibleCertificates: 0, indeterminate: false) == "not_checked",
        "one-element ExcludeRoot chain cannot claim a revocation check");
    Check(CertificateChainOperations.DescribeRevocation(CertificateRevocationSelection.Offline, eligibleCertificates: 1, indeterminate: true) == "offline_indeterminate",
        "offline uncertainty remains explicit");
    groups.Add("actual-offline-chain-policy-and-truthful-revocation");
}
bool Throws(Action action, string expected)
{
    expectedFailureCode = SafeCode(expected);
    try { action(); observedFailureCode = "no_failure"; return false; }
    catch (CertificateOperationException error) { observedFailureCode = SafeCode(error.Code); return error.Code == expected; }
}
static string SafeCode(string value) => value.Length is > 0 and <= 64 &&
    value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.') ? value : "unclassified";
static CertificateOperationException Capture(Action action)
{
    try { action(); }
    catch (CertificateOperationException error) { return error; }
    throw new InvalidOperationException("Expected an operation failure.");
}
static bool VerifyCertificateSignature(X509Certificate2 certificate, X509Certificate2 issuer)
{
    // Independently decode the signed X509 envelope; do not call authoring helpers.
    var reader = new AsnReader(certificate.RawData, AsnEncodingRules.DER);
    var envelope = reader.ReadSequence();
    var tbs = envelope.ReadEncodedValue().ToArray();
    var algorithm = envelope.ReadSequence();
    if (algorithm.ReadObjectIdentifier() != "1.2.840.113549.1.1.11") return false;
    if (algorithm.HasData) algorithm.ReadNull();
    algorithm.ThrowIfNotEmpty();
    var signature = envelope.ReadBitString(out var unusedBits).ToArray();
    envelope.ThrowIfNotEmpty(); reader.ThrowIfNotEmpty();
    using var key = issuer.GetRSAPublicKey();
    return unusedBits == 0 && key is not null &&
        key.VerifyData(tbs, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
}
static void ConfigureOffline(X509Chain chain, X509Certificate2 root, DateTimeOffset instant)
{
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(root);
    chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.3"));
    chain.ChainPolicy.VerificationTime = instant.UtcDateTime;
    chain.ChainPolicy.DisableCertificateDownloads = true;
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
}
sealed class FixedClock(DateTimeOffset instant) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => instant;
}

sealed class FixtureAssertionFailure(string check) : Exception("A named certificate fixture assertion failed.")
{
    // Labels are fixed source literals, never certificate/private values or exception messages.
    public string Check { get; } = check;
}

sealed class FixtureSecrets(string password) : ICertificateSecretResolver
{
    public CertificateSecret Resolve(string reference)
    {
        if (reference != "password-ref") throw new CertificateOperationException("fixture_secret_missing");
        return new(password);
    }
}

sealed class FixtureKeys(X509Certificate2 certificate) : ICertificateKeyResolver
{
    public X509Certificate2 Resolve(CertificateKeyReference reference)
    {
        if (reference.Reference != "isolated-key") throw new CertificateOperationException("fixture_key_missing");
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        using var key = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("fixture has no key");
        return publicOnly.CopyWithPrivateKey(key);
    }
}

sealed class FixtureAdmission : ICertificateStoreAdmission
{
    public bool Deny { get; set; }
    public CertificateStoreAction LastAction { get; private set; }
    public void Authorize(CertificateStoreTarget target, CertificateStoreAction action)
    {
        LastAction = action;
        if (Deny) throw new CertificateOperationException("fixture_denied");
    }
}

sealed class FixtureStores : ICertificateStoreBackend
{
    private readonly Dictionary<CertificateStoreTarget, Dictionary<string, CertificateSummary>> _stores = [];
    public int Calls { get; private set; }
    private Dictionary<string, CertificateSummary> Store(CertificateStoreTarget target)
    {
        if (!_stores.TryGetValue(target, out var store)) _stores[target] = store = [];
        return store;
    }
    public IReadOnlyList<CertificateSummary> Inventory(CertificateStoreTarget target) { Calls++; return Store(target).Values.ToArray(); }
    public CertificateStoreMutation AddPublic(CertificateStoreTarget target, byte[] der)
    {
        Calls++;
        using var certificate = X509CertificateLoader.LoadCertificate(der);
        var info = CertificateOperations.Inspect(certificate);
        var added = Store(target).TryAdd(info.CertificateId, info);
        return new(added ? "added" : "unchanged", info.CertificateId, added);
    }
    public CertificateStoreMutation RemovePublic(CertificateStoreTarget target, string id)
    {
        Calls++;
        var removed = Store(target).Remove(id);
        return new(removed ? "removed" : "absent", id, removed);
    }
}

sealed class PartialWriteStream(Exception? failure = null) : Stream
{
    public Exception Failure { get; } = failure ?? new IOException("isolated partial output");
    public int ObservedBytes { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => ObservedBytes;
    public override long Position { get => ObservedBytes; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count)
    {
        ObservedBytes += Math.Min(3, count);
        throw Failure;
    }
}

sealed class CapturingReadStream(byte[] input) : MemoryStream(input, writable: false)
{
    public byte[]? ObservedBuffer { get; private set; }
    public override int Read(byte[] buffer, int offset, int count)
    {
        ObservedBuffer = buffer;
        return base.Read(buffer, offset, count);
    }
}
