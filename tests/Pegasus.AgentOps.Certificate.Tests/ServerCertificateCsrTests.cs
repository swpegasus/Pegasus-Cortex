using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Pegasus.AgentOps.Certificates;

internal static class ServerCertificateCsrTests
{
    internal static int Run()
    {
        try
        {
            var assertions = Verify();
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, fixture = "server_certificate_csr", assertions,
                evidence = "in-memory RSA/PKCS10/public-leaf and offline selected-CA checks",
                stores = "none", files = "none", privateExport = "none", serviceTls = "not tested" }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, fixture = "server_certificate_csr",
                type = error.GetType().Name,
                code = error is CertificateOperationException operation ? operation.Code : "fixture_failed" }));
            return 1;
        }
    }

    internal static int Verify()
    {
        var assertions = 0;
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var operations = new CertificateOperations(new FixedClock(now));
        var spec = new CertificateSpec("Admitted server subject", now.AddMinutes(-1), now.AddDays(1), 2048);
        var names = new ServerCertificateNames(["localhost"], ["127.0.0.1", "::1"]);
        using var authority = operations.CreateAuthority(new("Ephemeral CSR authority", now.AddDays(-1), now.AddDays(7), 2048));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Untrusted CSR subject", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var csr = request.CreateSigningRequest();
        using var issued = operations.IssueServerFromSigningRequest(spec, authority, names, csr);
        var id = CertificateOperations.Inspect(issued).CertificateId;
        var observedNames = ServerCertificateMaterial.Require(issued, id, now, privateKey: false);
        Check(!issued.HasPrivateKey && issued.GetRSAPrivateKey() is null, "CSR signing returned a private key.");
        Check(issued.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()),
            "Issued public key differs from the signature-validated CSR.");
        Check(!issued.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(authority.PublicKey.ExportSubjectPublicKeyInfo()),
            "Issuer key appeared as the subject key.");
        Check(issued.GetNameInfo(X509NameType.SimpleName, false) == spec.CommonName, "CSR subject overrode admitted subject.");
        Check(observedNames.DnsNames.SequenceEqual(names.DnsNames) && observedNames.IpAddresses.SequenceEqual(names.IpAddresses),
            "CSR signing changed admitted SANs.");
        var policy = new CertificateChainRequest(CertificateTrustSelection.CustomRoots, CertificateRevocationSelection.NotChecked,
            now, [authority.RawData]);
        Check(CertificateChainOperations.VerifyServerAuthentication(issued, id, policy).Trusted,
            "CSR leaf did not validate under the exact offline authority.");
        Check(!CertificateChainOperations.VerifyCodeSigning(issued, id, policy).Trusted, "CSR leaf acquired code-signing purpose.");
        // This test-only association proves the returned public leaf fits exactly
        // the request key; the operation itself never receives or returns that key.
        using (var matching = issued.CopyWithPrivateKey(key))
        {
            ServerCertificateMaterial.ProvePrivateKey(matching);
            Check(matching.HasPrivateKey && !issued.HasPrivateKey, "Public-only return changed during caller association.");
        }
        using (var second = operations.IssueServerFromSigningRequest(spec, authority, names, csr))
            Check(second.SerialNumber != issued.SerialNumber, "CSR issuance reused a serial.");

        var tampered = (byte[])csr.Clone();
        tampered[^1] ^= 1;
        Reject(() => operations.IssueServerFromSigningRequest(spec, authority, names, tampered), "server_csr_invalid");
        var trailing = new byte[csr.Length + 1];
        csr.CopyTo(trailing, 0);
        Reject(() => operations.IssueServerFromSigningRequest(spec, authority, names, trailing), "server_csr_invalid");
        Reject(() => operations.IssueServerFromSigningRequest(spec, authority, names, []), "server_csr_bound");
        Reject(() => operations.IssueServerFromSigningRequest(spec, authority, names, new byte[16 * 1024 + 1]), "server_csr_bound");
        Reject(() => operations.IssueServerFromSigningRequest(spec with { RsaBits = 3072 }, authority, names, csr),
            "server_csr_key_size_mismatch");

        var sha384 = new CertificateRequest("CN=Other signature hash", key, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1)
            .CreateSigningRequest();
        Reject(() => operations.IssueServerFromSigningRequest(spec, authority, names, sha384),
            "server_csr_signature_algorithm_unsupported");
        var pss = new CertificateRequest("CN=Other RSA padding", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)
            .CreateSigningRequest();
        Reject(() => operations.IssueServerFromSigningRequest(spec, authority, names, pss),
            "server_csr_signature_algorithm_unsupported");
        using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var elliptic = new CertificateRequest("CN=Other key algorithm", ec, HashAlgorithmName.SHA256).CreateSigningRequest();
            Reject(() => operations.IssueServerFromSigningRequest(spec, authority, names, elliptic),
                "server_csr_signature_algorithm_unsupported");
        }
        using (var weak = RSA.Create(1024))
        {
            var weakRequest = new CertificateRequest("CN=Weak request key", weak, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSigningRequest();
            Reject(() => operations.IssueServerFromSigningRequest(spec, authority, names, weakRequest), "rsa_key_required");
        }

        var requestedPolicy = new CertificateRequest("CN=Untrusted requested authority", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        requestedPolicy.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        requestedPolicy.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        requestedPolicy.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.3") }, true));
        var unwantedNames = new SubjectAlternativeNameBuilder();
        unwantedNames.AddDnsName("untrusted.invalid");
        requestedPolicy.CertificateExtensions.Add(unwantedNames.Build());
        requestedPolicy.CertificateExtensions.Add(new X509Extension("1.2.3.4", new byte[] { 0x05, 0x00 }, true));
        using (var isolated = operations.IssueServerFromSigningRequest(spec, authority, names, requestedPolicy.CreateSigningRequest()))
        {
            var isolatedId = CertificateOperations.Inspect(isolated).CertificateId;
            var selected = ServerCertificateMaterial.Require(isolated, isolatedId, now, privateKey: false);
            Check(!CertificateOperations.Inspect(isolated).IsAuthority, "Requested CA flag escaped CSR isolation.");
            Check(selected.DnsNames.SequenceEqual(names.DnsNames) && selected.IpAddresses.SequenceEqual(names.IpAddresses),
                "Requested SAN escaped CSR isolation.");
            Check(isolated.GetNameInfo(X509NameType.SimpleName, false) == spec.CommonName, "Requested subject escaped CSR isolation.");
            Check(isolated.Extensions.Count == 5 && !isolated.Extensions.Cast<X509Extension>().Any(extension => extension.Oid?.Value == "1.2.3.4"),
                "Requested arbitrary extension escaped CSR isolation.");
            var usage = isolated.Extensions.OfType<X509KeyUsageExtension>().Single();
            Check(usage.KeyUsages == (X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment),
                "Requested key usage escaped CSR isolation.");
            var purposes = isolated.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages;
            Check(purposes.Count == 1 && purposes[0].Value == "1.3.6.1.5.5.7.3.1",
                "Requested EKU escaped CSR isolation.");
        }
        using (var publicIssuer = X509CertificateLoader.LoadCertificate(authority.RawData))
            Reject(() => operations.IssueServerFromSigningRequest(spec, publicIssuer, names, csr), "issuer_private_key_required");
        Reject(() => operations.IssueServerFromSigningRequest(spec with { NotAfter = now.AddDays(8) }, authority, names, csr),
            "issuer_validity_exceeded");

        using (var localServer = operations.IssueServer(spec, authority, names))
        {
            ServerCertificateMaterial.ProvePrivateKey(localServer);
            Check(localServer.HasPrivateKey, "Existing IssueServer private-key contract changed.");
        }
        using (var codeSigner = operations.IssueCodeSigning(spec, authority))
        {
            Check(codeSigner.HasPrivateKey && CertificateChainOperations.VerifyCodeSigning(codeSigner,
                CertificateOperations.Inspect(codeSigner).CertificateId, policy).Trusted,
                "Existing code-signing issuance contract changed.");
        }
        return assertions;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        void Reject(Func<X509Certificate2> action, string expectedCode)
        {
            try { using var unexpected = action(); }
            catch (CertificateOperationException error)
            {
                Check(error.Code == expectedCode, "CSR refusal code differs from its admitted boundary.");
                return;
            }
            throw new InvalidOperationException("Invalid CSR signing input was accepted.");
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
