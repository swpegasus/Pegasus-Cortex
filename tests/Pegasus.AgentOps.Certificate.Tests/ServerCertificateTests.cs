using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using Pegasus.AgentOps.Certificates;

internal static class ServerCertificateTests
{
    internal static int Run()
    {
        try
        {
            Verify();
            Console.WriteLine("PASS server_certificate_material (ephemeral RSA/SAN/offline custom-root and fake binding; no sockets or OS-store writes)");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, fixture = "server_certificate_material",
                type = error.GetType().Name, code = error is CertificateOperationException operation ? operation.Code : "fixture_failed" }));
            return 1;
        }
    }

    internal static void Verify()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var operations = new CertificateOperations(new FixedClock(now));
        var dns = new[] { "LOCALHOST" };
        var names = new ServerCertificateNames(dns, ["127.0.0.1", "::1"]);
        dns[0] = "changed.localhost";
        Check(names.DnsNames.SequenceEqual(new[] { "localhost" }), "SAN input was not snapshotted.");
        using var authority = operations.CreateAuthority(new("Ephemeral server authority", now.AddDays(-1), now.AddDays(7), 2048));
        using var server = operations.IssueServer(new("Ephemeral HTTPS server", now.AddMinutes(-1), now.AddDays(1), 2048), authority, names);
        var id = CertificateOperations.Inspect(server).CertificateId;
        var observed = ServerCertificateMaterial.Require(server, id, now, privateKey: true);
        Check(observed.DnsNames.SequenceEqual(names.DnsNames) && observed.IpAddresses.SequenceEqual(names.IpAddresses),
            "Issued DNS/IP SANs differ from the bounded request.");
        ServerCertificateMaterial.ProvePrivateKey(server);
        var policy = new CertificateChainRequest(CertificateTrustSelection.CustomRoots, CertificateRevocationSelection.NotChecked,
            now, [authority.RawData]);
        Check(CertificateChainOperations.VerifyServerAuthentication(server, id, policy).Trusted,
            "Server leaf did not validate against its exact offline custom root.");
        Check(!CertificateChainOperations.VerifyCodeSigning(server, id, policy).Trusted, "A server leaf was accepted for code signing.");
        using var signer = operations.IssueCodeSigning(new("Ephemeral code signer", now.AddMinutes(-1), now.AddDays(1), 2048), authority);
        Reject(() => ServerCertificateMaterial.Require(signer, CertificateOperations.Inspect(signer).CertificateId, now, false));
        Check(!CertificateChainOperations.VerifyServerAuthentication(signer, CertificateOperations.Inspect(signer).CertificateId, policy).Trusted,
            "Code-signing purpose was reused as server authentication.");
        using var publicOnly = CertificateExchange.ImportPublic(server.RawData, PublicCertificateFormat.Der);
        Reject(() => ServerCertificateMaterial.Require(publicOnly, id, now, privateKey: true));
        Reject(() => ServerCertificateMaterial.Require(server, new string('0', 64), now, true));
        Reject(() => ServerCertificateMaterial.Require(server, id, now.AddDays(2), true));
        using var different = operations.CreateAuthority(new("Different ephemeral root", now.AddDays(-1), now.AddDays(7), 2048));
        Check(!CertificateChainOperations.VerifyServerAuthentication(server, id, policy with { CustomRootCertificates = [different.RawData] }).Trusted,
            "An unrelated root validated the server leaf.");
        Reject(() => operations.IssueServer(new("Beyond issuer", now.AddMinutes(-1), now.AddDays(8), 2048), authority, names));
        Reject(() => new ServerCertificateNames(["*.localhost"], []));
        Reject(() => new ServerCertificateNames(["localhost", "LOCALHOST"], []));
        Reject(() => new ServerCertificateNames([], ["127.1"]));
        Reject(() => new ServerCertificateNames([], ["fe80::1%1"]));
        Reject(() => new ServerCertificateNames([], []));
        Reject(() => new ServerCertificateNames(Enumerable.Range(0, 17).Select(index => "n" + index + ".localhost"), []));

        var binding = new FakeBinding();
        var first = binding.BindServer(server, id, now);
        var second = binding.BindServer(server, id, now);
        Check(first.State == "bound" && first.KeyPersistenceIssued && first.StoreActionIssued &&
            second.State == "unchanged" && !second.KeyPersistenceIssued && binding.Mutations == 1,
            "Injected fixed-store binding did not preserve unchanged identity.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { binding.BindServer(server, id, now, canceled.Token); throw new InvalidOperationException("Canceled binding ran."); }
        catch (OperationCanceledException) { }
        Check(binding.Calls == 2, "Canceled binding reached its injected effect.");
    }

    // Optional real Windows success-path qualification. Never part of the normal
    // ephemeral suite. An import failure before a key handle is returned remains
    // cleanup-unknown; this fixture never scans or deletes unproven native keys.
    [SupportedOSPlatform("windows")]
    internal static int RunWindowsOwned()
    {
        var guid = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        now = new(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Owner unavailable.");
        if (sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20" ||
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Use the ordinary signed-in owner route.");
        var operations = new CertificateOperations(new FixedClock(now));
        using var authority = operations.CreateAuthority(new("Owned transient CA " + guid, now.AddMinutes(-2), now.AddDays(2), 2048));
        using var server = operations.IssueServer(new("Owned server " + guid, now.AddMinutes(-1), now.AddDays(1), 2048),
            authority, new(["localhost"], ["127.0.0.1", "::1"]));
        var id = CertificateOperations.Inspect(server).CertificateId;
        CngKey? ownedKey = null;
        string? ownedName = null;
        CngProvider? ownedProvider = null;
        var mayHaveEffect = false;
        var success = false;
        var cleanupVerified = false;
        Exception? failure = null;
        try
        {
            var binding = new WindowsCertificateServerBinding(() =>
            {
                using var current = WindowsIdentity.GetCurrent();
                if (current.User?.Value != sid || new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("Owner changed.");
            });
            mayHaveEffect = true;
            var result = binding.BindServer(server, id, now);
            Check(result.State == "bound" && result.KeyPersistenceIssued && result.StoreActionIssued,
                "A fresh GUID-owned leaf did not produce one new binding.");
            using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadOnly);
                using var observed = ReadExact(store, id);
                Check(observed is not null, "The exact bound certificate was not reopened.");
                _ = ServerCertificateMaterial.Require(observed!, id, now, privateKey: true);
                ServerCertificateMaterial.ProvePrivateKey(observed!);
                using var key = observed!.GetRSAPrivateKey();
                using var publicKey = server.GetRSAPublicKey();
                Check(key is RSACng && publicKey is not null &&
                    key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey.ExportSubjectPublicKeyInfo()),
                    "The reopened private key does not match the newly generated owned leaf.");
                var native = ((RSACng)key!).Key;
                Check(!native.IsEphemeral && !native.IsMachineKey && !string.IsNullOrEmpty(native.KeyName),
                    "No exact persisted user CNG key cleanup identity is available.");
                ownedName = native.KeyName;
                ownedProvider = native.Provider ?? throw new InvalidOperationException("Owned key provider unavailable.");
                CngKey? candidate = CngKey.Open(ownedName!, ownedProvider, CngKeyOpenOptions.None);
                try
                {
                    using var reopened = new RSACng(candidate);
                    Check(reopened.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey!.ExportSubjectPublicKeyInfo()),
                        "The exact reopened cleanup handle belongs to a different key.");
                    ownedKey = candidate; // Adopt cleanup ownership only after the public-key proof.
                    candidate = null;
                }
                finally { candidate?.Dispose(); }
            }
            var unchanged = binding.BindServer(server, id, now);
            Check(unchanged.State == "unchanged" && !unchanged.KeyPersistenceIssued && !unchanged.StoreActionIssued,
                "Second explicit bind did not observe the same usable key unchanged.");
            success = true;
        }
        catch (Exception error)
        {
            failure = error;
            if (error is ServerCertificateBindingException binding)
                mayHaveEffect = binding.KeyPersistenceIssued || binding.StoreActionIssued || binding.OutcomeUnknown;
        }
        finally
        {
            if (ownedKey is not null)
            {
                try
                {
                    using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                    store.Open(OpenFlags.OpenExistingOnly | OpenFlags.ReadWrite);
                    using (var selected = ReadExact(store, id) ?? throw new InvalidOperationException("Owned certificate cleanup identity missing."))
                    {
                        Check(selected.RawData.AsSpan().SequenceEqual(server.RawData),
                            "Owned certificate cleanup identity changed.");
                        using var selectedKey = selected!.GetRSAPrivateKey();
                        using var expectedKey = server.GetRSAPublicKey();
                        Check(selectedKey is not null && expectedKey is not null &&
                            selectedKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(expectedKey.ExportSubjectPublicKeyInfo()),
                            "Owned private key cleanup identity changed.");
                        store.Remove(selected);
                    }
                    using (var absent = ReadExact(store, id)) Check(absent is null, "Owned certificate removal was not observed.");
                    ownedKey.Delete();
                    ownedKey.Dispose();
                    ownedKey = null;
                    Check(!CngKey.Exists(ownedName!, ownedProvider!, CngKeyOpenOptions.None),
                        "The exact owned user CNG key still exists after deletion.");
                    cleanupVerified = true;
                }
                catch (Exception error) { failure ??= error; }
                finally { ownedKey?.Dispose(); }
            }
            else cleanupVerified = !mayHaveEffect;
        }
        var ok = success && cleanupVerified && failure is null;
        Console.WriteLine(JsonSerializer.Serialize(new { ok, fixture = "owned_windows_server_binding", certificateId = id,
            store = "CurrentUser/My", keyCleanupVerified = cleanupVerified, cleanupUnknown = !cleanupVerified,
            failureType = failure?.GetType().Name,
            code = failure is ServerCertificateBindingException uncertain ? uncertain.Code : failure is null ? null : "fixture_failed",
            importFailureCleanup = "unknown before an owned key handle is observed; no broad key cleanup",
            trustStoreMutations = false, networkCalls = false }));
        return ok ? 0 : 1;
    }

    [SupportedOSPlatform("windows")]
    private static X509Certificate2? ReadExact(X509Store store, string id)
    {
        var certificates = store.Certificates;
        X509Certificate2? selected = null;
        try
        {
            if (certificates.Count > 4096) throw new InvalidOperationException("Certificate lookup bound.");
            foreach (var certificate in certificates)
            {
                if (CertificateOperations.Inspect(certificate).CertificateId != id) continue;
                Check(selected is null, "Owned certificate identity is ambiguous.");
                selected = certificate;
            }
            return selected;
        }
        catch { selected = null; throw; }
        finally { foreach (var certificate in certificates) if (!ReferenceEquals(selected, certificate)) certificate.Dispose(); }
    }

    private sealed class FakeBinding : ICertificateServerBinding
    {
        private readonly HashSet<string> _bound = new(StringComparer.Ordinal);
        internal int Calls, Mutations;
        public ServerCertificateBindingResult BindServer(X509Certificate2 certificate, string expectedCertificateId,
            DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            _ = ServerCertificateMaterial.Require(certificate, expectedCertificateId, now, true);
            ServerCertificateMaterial.ProvePrivateKey(certificate);
            if (!_bound.Add(expectedCertificateId)) return new("unchanged", expectedCertificateId, false, false, true, false);
            Mutations++;
            return new("bound", expectedCertificateId, true, true, true, false);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (CertificateOperationException) { return; }
        throw new InvalidOperationException("Invalid server certificate input was accepted.");
    }
}
