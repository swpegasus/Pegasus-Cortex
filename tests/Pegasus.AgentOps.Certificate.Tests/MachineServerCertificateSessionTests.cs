using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using Pegasus.AgentOps.Certificates;

[SupportedOSPlatform("windows")]
internal static class MachineServerCertificateSessionTests
{
    internal static int Run()
    {
        try
        {
            var assertions = Verify();
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, fixture = "machine_server_certificate_session", assertions,
                evidence = "in-memory RSA/CSR/certificate checks and injected machine-key/store/cleanup state",
                machineKeys = "not created", stores = "fake only", systemAccess = "not tested", serviceTls = "not tested" }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, fixture = "machine_server_certificate_session",
                type = error.GetType().Name, code = error switch
                {
                    MachineServerCertificateSessionException session => session.Code,
                    CertificateOperationException certificate => certificate.Code,
                    _ => "fixture_failed"
                } }));
            return 1;
        }
    }

    private static int Verify()
    {
        var assertions = 0;
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var operations = new CertificateOperations(new FixedClock(now));
        var specification = new CertificateSpec("Machine session fixture", now.AddMinutes(-1), now.AddDays(1), 2048);
        var names = new ServerCertificateNames(["localhost"], ["127.0.0.1"]);
        using var authority = operations.CreateAuthority(new("Ephemeral session authority", now.AddDays(-1), now.AddDays(7), 2048));
        using var otherAuthority = operations.CreateAuthority(new("Other session authority", now.AddDays(-1), now.AddDays(7), 2048));
        using var key = RSA.Create(2048);
        using var otherKey = RSA.Create(2048);
        var authorityId = CertificateOperations.Inspect(authority).CertificateId;
        var operationId = new Guid("6fd61d95-ef39-4bd6-815f-b0cfa9ae846f");
        Check(MachineServerCertificateNative.MachineKeyFlag == 0x20 &&
            MachineServerCertificateNative.OwnerAndDaclFlags == 0x45, "Native key/security selectors changed.");
        Check(MachineServerCertificateNative.KeyName(operationId) == "Pegasus.Cortex.McpTls.6fd61d95ef394bd6815fb0cfa9ae846f",
            "Operation GUID did not bind the fixed key name.");
        var cleanSecurity = MachineServerCertificatePolicy.SecurityDescriptor();
        MachineServerCertificatePolicy.RequireKey(Observation(cleanSecurity), 2048);
        Check(true, "Canonical machine key descriptor refused.");
        MachineServerCertificatePolicy.RequireKey(Observation(Descriptor(
            "O:BAG:BAD:P(A;;0xd01f01ff;;;SY)(A;;0xd01f01ff;;;BA)")), 2048);
        Check(true, "Exact observed Software-KSP full-control descriptor refused.");
        foreach (var (label, sddl) in new[]
        {
            ("near normalized mask", "O:BAG:BAD:P(A;;0xd01f01ff;;;SY)(A;;0xd01f01fe;;;BA)"),
            ("unknown additional generic bit", "O:BAG:BAD:P(A;;0xd01f01ff;;;SY)(A;;0xf01f01ff;;;BA)"),
            ("normalized mask without GENERIC_ALL", "O:BAG:BAD:P(A;;0xd01f01ff;;;SY)(A;;0xc01f01ff;;;BA)"),
            ("concrete partial mask", "O:BAG:BAD:P(A;;0xd01f01ff;;;SY)(A;;0x001f01ff;;;BA)"),
            ("normalized foreign principal", "O:BAG:BAD:P(A;;0xd01f01ff;;;SY)(A;;0xd01f01ff;;;S-1-5-21-100-200-300-1001)"),
            ("normalized unprotected DACL", "O:BAG:BAD:(A;;0xd01f01ff;;;SY)(A;;0xd01f01ff;;;BA)"),
            ("normalized duplicate principal", "O:BAG:BAD:P(A;;0xd01f01ff;;;SY)(A;;0xd01f01ff;;;SY)"),
            ("normalized inherited ACE", "O:BAG:BAD:P(A;;0xd01f01ff;;;SY)(A;ID;0xd01f01ff;;;BA)")
        })
        {
            try { MachineServerCertificatePolicy.RequireKey(Observation(Descriptor(sddl)), 2048); }
            catch (CertificateOperationException error) when (error.Code == "machine_server_key_security_invalid")
            { Check(true, "Rejected " + label + "."); continue; }
            throw new InvalidOperationException("Accepted " + label + ".");
        }
        foreach (var invalid in new[] { Security("foreign-owner"), Security("unprotected"), Security("foreign-ace"),
            Security("null-dacl"), Security("deny"), Security("weak-grant") })
        {
            try { MachineServerCertificatePolicy.RequireKey(Observation(invalid), 2048); }
            catch (CertificateOperationException)
            { Check(true, "Unsafe descriptor refused."); continue; }
            throw new InvalidOperationException("Unsafe machine-key security was accepted.");
        }

        var valid = new FakeBackend(key);
        using (var session = Create(valid))
        {
            Check(session.Progress is { Phase: "csr_ready", KeyCreationIssued: true, KeyFinalized: true, SecurityObserved: true } &&
                valid.HeldObservations == 1 && valid.ReopenObservations == 1, "CSR preceded held/reopened policy checks.");
            var first = session.GetSigningRequest();
            var saved = (byte[])first.Clone();
            first[0] ^= 1;
            Check(session.GetSigningRequest().AsSpan().SequenceEqual(saved), "CSR output was not a defensive copy.");
            using var leaf = Sign(session);
            var id = CertificateOperations.Inspect(leaf).CertificateId;
            var issuedBeforePublish = false;
            valid.OnPublish = () => issuedBeforePublish = session.Progress.StoreActionIssued;
            var result = session.BindSignedLeaf(leaf.RawData, id, authority.RawData, authorityId, now);
            Check(result is { State: "bound", StoreActionIssued: true, PostconditionObserved: true, OutcomeUnknown: false } &&
                issuedBeforePublish && valid.PublishCalls == 1 && session.Progress.Phase == "bound",
                "Successful binding did not retain issued/postcondition evidence.");
            ExpectSession(() => session.DeleteUnbound(), "machine_server_cleanup_forbidden");
            ExpectSession(() => session.BindSignedLeaf(leaf.RawData, id, authority.RawData, authorityId, now),
                "machine_server_binding_consumed");
            Check(valid.DeleteCalls == 0 && valid.PublishCalls == 1, "Bound key was deleted or publication replayed.");
        }
        Check(valid.DeleteCalls == 0 && valid.DisposeCalls == 1, "Dispose deleted a bound key or failed ownership cleanup.");

        // Every wrong public input is rejected before preparing or entering a store.
        foreach (var invalid in new[] { "key", "ca", "san", "pin", "trailing", "code-purpose" })
        {
            var backend = new FakeBackend(key);
            using var session = Create(backend);
            using var leaf = invalid switch
            {
                "key" => operations.IssueServerFromSigningRequest(specification, authority, names, Csr(otherKey, specification)),
                "san" => operations.IssueServerFromSigningRequest(specification, authority,
                    new ServerCertificateNames(["different.invalid"], []), session.GetSigningRequest()),
                "code-purpose" => operations.IssueCodeSigning(specification, authority),
                _ => Sign(session)
            };
            var der = invalid == "trailing" ? leaf.RawData.Concat(new byte[] { 0 }).ToArray() : leaf.RawData;
            var pin = invalid == "pin" ? new string('0', 64) : CertificateOperations.Inspect(leaf).CertificateId;
            var selected = invalid == "ca" ? otherAuthority : authority;
            ExpectSession(() => session.BindSignedLeaf(der, pin, selected.RawData,
                CertificateOperations.Inspect(selected).CertificateId, now));
            Check(backend.PrepareCalls == 0 && backend.PublishCalls == 0 && !session.Progress.StoreActionIssued,
                "Invalid public material reached store preparation.");
            Check(session.DeleteUnbound().CleanupObserved && backend.DeleteCalls == 1,
                "Known unbound refusal could not clean its exact key.");
        }

        foreach (var failedPhase in new[] { "publish", "read" })
        {
            var backend = new FakeBackend(key) { ThrowAt = failedPhase };
            using var session = Create(backend);
            using var leaf = Sign(session);
            var id = CertificateOperations.Inspect(leaf).CertificateId;
            var failure = ExpectSession(() => session.BindSignedLeaf(leaf.RawData, id, authority.RawData, authorityId, now));
            Check(failure.Progress is { StoreActionIssued: true, OutcomeUnknown: true, CleanupIssued: false },
                "Failed publication/readback lost uncertainty.");
            ExpectSession(() => session.DeleteUnbound(), "machine_server_cleanup_forbidden");
            Check(backend.DeleteCalls == 0 && backend.PublishCalls == 1, "Unknown store outcome deleted or replayed a key.");
        }

        var late = new FakeBackend(key);
        using (var session = Create(late))
        using (var cancellation = new CancellationTokenSource())
        {
            using var leaf = Sign(session);
            late.OnPublish = cancellation.Cancel;
            var bound = session.BindSignedLeaf(leaf.RawData, CertificateOperations.Inspect(leaf).CertificateId,
                authority.RawData, authorityId, now, cancellation.Token);
            Check(cancellation.IsCancellationRequested && bound.PostconditionObserved && !bound.OutcomeUnknown,
                "Late cancellation erased a completed store observation.");
        }
        var canceledBackend = new FakeBackend(key);
        using (var session = Create(canceledBackend))
        using (var canceled = new CancellationTokenSource())
        {
            using var leaf = Sign(session);
            canceled.Cancel();
            try
            {
                session.BindSignedLeaf(leaf.RawData, CertificateOperations.Inspect(leaf).CertificateId,
                    authority.RawData, authorityId, now, canceled.Token);
                throw new InvalidOperationException("Pre-canceled binding ran.");
            }
            catch (OperationCanceledException) { Check(canceledBackend.PublishCalls == 0, "Pre-canceled binding reached store."); }
            Check(session.DeleteUnbound().CleanupObserved, "Pre-store cancellation prevented explicit exact cleanup.");
        }

        foreach (var failurePoint in new[] { "finalize", "held-security", "reopen-security", "export-policy", "csr" })
        {
            var backend = new FakeBackend(key) { ThrowAt = failurePoint };
            var failure = ExpectSession(() => { using var unexpected = Create(backend); });
            Check(failure.Progress is { KeyCreationIssued: true, CleanupIssued: true, CleanupObserved: true, OutcomeUnknown: false } &&
                backend.DeleteCalls == 1 && backend.PublishCalls == 0 && backend.DisposeCalls == 1,
                "Creation refusal did not clean only its original unbound key.");
        }
        var collision = new FakeBackend(key) { ThrowAt = "collision" };
        var conflict = ExpectSession(() => { using var unexpected = Create(collision); }, "machine_server_key_exists");
        Check(conflict.Progress is { KeyCreationIssued: true, CleanupIssued: false, OutcomeUnknown: false } &&
            collision.DeleteCalls == 0 && collision.DisposeCalls == 1, "Collision was adopted, deleted or mislabeled unknown.");
        var unknownCreation = new FakeBackend(key) { ThrowAt = "creation-unknown" };
        var unknown = ExpectSession(() => { using var unexpected = Create(unknownCreation); });
        Check(unknown.Progress is { KeyCreationIssued: true, CleanupIssued: false, OutcomeUnknown: true } &&
            unknownCreation.DeleteCalls == 0, "Unknown creation adopted or deleted an unproven key.");
        var admissionDenied = new FakeBackend(key);
        var denied = ExpectSession(() =>
        {
            using var unexpected = MachineServerCertificateSession.CreateCore(operationId, specification, names,
                () => throw new UnauthorizedAccessException(), admissionDenied);
        });
        Check(denied.Progress is { KeyCreationIssued: false, StoreActionIssued: false, OutcomeUnknown: false } &&
            !admissionDenied.KeyOwned && admissionDenied.DisposeCalls == 1, "Denied admission reached key creation.");
        var prepareFailure = new FakeBackend(key) { ThrowAt = "prepare" };
        using (var session = Create(prepareFailure))
        {
            using var leaf = Sign(session);
            var failure = ExpectSession(() => session.BindSignedLeaf(leaf.RawData,
                CertificateOperations.Inspect(leaf).CertificateId, authority.RawData, authorityId, now));
            Check(!failure.Progress.StoreActionIssued && prepareFailure.PublishCalls == 0 &&
                session.DeleteUnbound().CleanupObserved, "Pre-store preparation failure latched a false store effect.");
        }
        var cleanupFailure = new FakeBackend(key);
        using (var session = Create(cleanupFailure))
        {
            cleanupFailure.ThrowAt = "delete";
            Check(session.DeleteUnbound() is { CleanupIssued: true, CleanupObserved: false, OutcomeUnknown: true },
                "Failed cleanup was reported as clean.");
            ExpectSession(() => session.DeleteUnbound(), "machine_server_cleanup_unavailable");
            Check(cleanupFailure.DeleteCalls == 1, "Unknown cleanup was repeated.");
        }
        var unboundDispose = new FakeBackend(key);
        var disposedSession = Create(unboundDispose);
        disposedSession.Dispose();
        disposedSession.Dispose();
        Check(unboundDispose.DeleteCalls == 0 && unboundDispose.DisposeCalls == 1, "Dispose implicitly deleted an unbound key.");
        try { disposedSession.GetSigningRequest(); throw new InvalidOperationException("Disposed session remained usable."); }
        catch (ObjectDisposedException) { Check(true, "Disposed session refused."); }
        return assertions;

        MachineServerCertificateSession Create(FakeBackend backend) =>
            MachineServerCertificateSession.CreateCore(operationId, specification, names, () => { }, backend);
        X509Certificate2 Sign(MachineServerCertificateSession session) =>
            operations.IssueServerFromSigningRequest(specification, authority, names, session.GetSigningRequest());
        MachineServerCertificateKeyObservation Observation(byte[] descriptor) =>
            new(true, 0, 2048, 3, key.ExportSubjectPublicKeyInfo(), descriptor);
        byte[] Descriptor(string sddl)
        {
            var descriptor = new RawSecurityDescriptor(sddl);
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            return bytes;
        }
        byte[] Security(string variant)
        {
            var descriptor = new RawSecurityDescriptor(cleanSecurity, 0);
            if (variant == "foreign-owner") descriptor.Owner = new SecurityIdentifier("S-1-5-21-100-200-300-1001");
            if (variant == "unprotected") descriptor.SetFlags(descriptor.ControlFlags & ~ControlFlags.DiscretionaryAclProtected);
            if (variant == "null-dacl") descriptor.DiscretionaryAcl = null;
            if (variant is "foreign-ace" or "deny" or "weak-grant")
            {
                var acl = new RawAcl(2, 2);
                acl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x10000000,
                    new SecurityIdentifier("S-1-5-18"), false, null));
                acl.InsertAce(1, new CommonAce(AceFlags.None,
                    variant == "deny" ? AceQualifier.AccessDenied : AceQualifier.AccessAllowed,
                    variant == "weak-grant" ? 1 : 0x10000000,
                    new SecurityIdentifier(variant == "foreign-ace" ? "S-1-5-21-100-200-300-1001" : "S-1-5-32-544"),
                    false, null));
                descriptor.DiscretionaryAcl = acl;
            }
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            return bytes;
        }
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        MachineServerCertificateSessionException ExpectSession(Action action, string? code = null)
        {
            try { action(); }
            catch (MachineServerCertificateSessionException error)
            {
                Check(code is null || error.Code == code, "Unexpected session refusal code.");
                return error;
            }
            throw new InvalidOperationException("Invalid machine session action was accepted.");
        }
    }

    private static byte[] Csr(RSA key, CertificateSpec specification) =>
        new CertificateRequest("CN=" + specification.CommonName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSigningRequest();

    private sealed class FakeBackend(RSA key) : IMachineServerCertificateBackend
    {
        private X509Certificate2? _prepared;
        private byte[]? _published;
        private bool _storeIssued, _disposed;
        internal string? ThrowAt { get; set; }
        internal Action? OnPublish { get; set; }
        internal int HeldObservations, ReopenObservations, PrepareCalls, PublishCalls, DeleteCalls, DisposeCalls;
        public bool KeyOwned { get; private set; }
        public void Create(Guid operationId, int rsaBits, Action beforeCreate)
        {
            beforeCreate();
            if (ThrowAt == "collision") throw new MachineServerCertificateBackendException("machine_server_key_exists");
            if (ThrowAt == "creation-unknown") throw new MachineServerCertificateBackendException("fixture_creation_unknown", outcomeUnknown: true);
            KeyOwned = true;
        }
        public void ConfigureAndFinalize() => Fail("finalize");
        public MachineServerCertificateKeyObservation ObserveKey(bool reopen)
        {
            if (reopen) ReopenObservations++; else HeldObservations++;
            var descriptor = MachineServerCertificatePolicy.SecurityDescriptor();
            if (ThrowAt == (reopen ? "reopen-security" : "held-security"))
            {
                var changed = new RawSecurityDescriptor(descriptor, 0);
                changed.SetFlags(changed.ControlFlags & ~ControlFlags.DiscretionaryAclProtected);
                descriptor = new byte[changed.BinaryLength];
                changed.GetBinaryForm(descriptor, 0);
            }
            return new(true, ThrowAt == "export-policy" ? 1 : 0, key.KeySize, 3, key.ExportSubjectPublicKeyInfo(), descriptor);
        }
        public byte[] CreateSigningRequest(CertificateSpec specification) { Fail("csr"); return Csr(key, specification); }
        public void PrepareBinding(X509Certificate2 publicLeaf, string certificateId)
        { PrepareCalls++; Fail("prepare"); _prepared = publicLeaf.CopyWithPrivateKey(key); }
        public void PublishBinding(Action beforeStore)
        {
            beforeStore();
            _storeIssued = true; PublishCalls++;
            OnPublish?.Invoke();
            _published = _prepared!.RawData;
            Fail("publish");
        }
        public X509Certificate2 ReadBoundCertificate(string certificateId)
        {
            Fail("read");
            using var publicOnly = X509CertificateLoader.LoadCertificate(_published!);
            return publicOnly.CopyWithPrivateKey(key);
        }
        public MachineServerCertificateKeyObservation ObserveBoundKey(X509Certificate2 certificate) => ObserveKey(false);
        public void DeleteUnboundKey()
        {
            if (_storeIssued || !KeyOwned) throw new InvalidOperationException("Fixture cleanup crossed publication.");
            DeleteCalls++; Fail("delete"); KeyOwned = false;
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; DisposeCalls++; _prepared?.Dispose();
        }
        private void Fail(string phase)
        { if (ThrowAt == phase) throw new MachineServerCertificateBackendException("fixture_" + phase); }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
