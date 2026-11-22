using System.ComponentModel;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Product;

internal static class CertificatePersistenceTests
{
    private const string CaReference = "fixture-ca", LeafReference = "fixture-leaf";
    private static readonly string[] OwnedReferences = [CaReference, LeafReference, "con", "corrupt", "sealed", "public-only", "public-malformed"];
    private enum Stage { Admission, PassiveMissing, Create, Validation, Collision, PublicRead, ReadOnly, Corrupt, Acl, Reopen, Issue, Export, PrivateFileExchange, Image, Sign, Verify, Complete, Cleanup }

    // Separate invocations/processes are intentional. Stage one leaves ONLY new
    // protected fixture envelopes; stage two never relies on an in-memory key.
    internal static Task<int> CreateAsync(string newOwnedFixtureRoot) => !OperatingSystem.IsWindows()
        ? Task.FromResult(Unavailable("windows_required")) : CreateWindowsAsync(newOwnedFixtureRoot);
    internal static Task<int> ReopenAsync(string sameOwnedFixtureRoot, string expectedCaId, string expectedLeafId) => !OperatingSystem.IsWindows()
        ? Task.FromResult(Unavailable("windows_required")) : ReopenWindowsAsync(sameOwnedFixtureRoot, expectedCaId, expectedLeafId);

    [SupportedOSPlatform("windows")]
    private static Task<int> CreateWindowsAsync(string suppliedRoot)
    {
        string? root = null, caId = null, leafId = null;
        var stage = Stage.Admission;
        var assertions = 0;
        string? assertion = null;
        void Check(bool value, string label) { assertions++; if (!value) { assertion = label; throw new InvalidOperationException("certificate_persistence_assertion_failed"); } }
        try
        {
            using var identity = OrdinaryIdentity();
            var sid = identity.User!.Value;
            root = Canonical(suppliedRoot);
            if (Directory.Exists(root) || File.Exists(root)) return Task.FromResult(Unavailable("fixture_root_occupied", root));
            CortexUserStateDirectories.EnsureCurrentUserDirectory(root);
            var vault = Path.Combine(root, "certificates");
            stage = Stage.PassiveMissing;
            var repository = new OwnerCertificateRepository(vault, sid);
            Check(!Directory.Exists(vault), "construction does not create missing vault");
            Check(Refused(() => repository.Inspect(CaReference)), "inspect refuses missing vault");
            Check(Refused(() => { using var unexpected = repository.Resolve(new(CaReference, new string('0', 64))); }), "resolve refuses missing vault");
            Check(!Directory.Exists(vault), "reads do not initialize missing vault");
            Check(Refused(() => new OwnerCertificateRepository(vault, "S-1-5-18")), "System owner refused without creating vault");
            stage = Stage.Create;
            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var operations = new CertificateOperations();
            using var ca = operations.CreateAuthority(new("Persistent fixture authority", now.AddMinutes(-1), now.AddDays(2), 2048));
            using var leaf = operations.IssueCodeSigning(new("Persistent fixture leaf", now.AddMinutes(-1), now.AddDays(1), 2048), ca);
            var authority = repository.Publish(CaReference, ca, new string('a', 64));
            var issued = repository.Publish(LeafReference, leaf, new string('b', 64));
            caId = authority.CertificateId; leafId = issued.CertificateId;
            Check(ca.HasPrivateKey && leaf.HasPrivateKey, "publication retains caller certificate ownership");
            Check(authority.Version == 1 && authority.OwnerSid == sid && authority.Reference == CaReference &&
                authority.RequestIdentity == new string('a', 64) && authority.ProtectedMaterialPresent,
                "published public entry binds exact owner reference and request identity");
            Check(issued.CertificateId == CertificateOperations.Inspect(leaf).CertificateId, "published leaf exact identity");
            stage = Stage.Validation;
            foreach (var invalid in new[] { "", "A", ".first", "../escape", "a/b", "a:b", "a ", new string('a', 65) })
                Check(Refused(() => repository.Publish(invalid, ca, new string('a', 64))), "invalid reference refused before publication");
            Check(Refused(() => repository.Publish("invalid-identity", ca, "bad")), "invalid request identity refused");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                var canceledBefore = false;
                try { repository.Publish("canceled", ca, new string('a', 64), canceled.Token); }
                catch (OperationCanceledException) { canceledBefore = true; }
                Check(canceledBefore && !File.Exists(EntryFile(vault, "canceled")), "cancellation before issue creates no entry");
            }
            repository.Publish("con", ca, new string('c', 64));
            Check(repository.Inspect("con", caId).CertificateId == caId, "fixed filename prefix permits reserved-looking opaque reference");
            stage = Stage.Collision;
            var originalHash = Hash(EntryFile(vault, CaReference));
            Check(Refused(() => repository.Publish(CaReference, leaf, new string('d', 64))), "existing reference never replaced or adopted");
            Check(Hash(EntryFile(vault, CaReference)) == originalHash, "collision preserves original encrypted bytes");
            Check(Refused(() => repository.Inspect(LeafReference, new string('0', 64))) &&
                Refused(() => { using var unexpected = repository.Resolve(new(LeafReference, new string('0', 64))); }), "wrong expected pin refused on both read paths");
            stage = Stage.PublicRead;
            var publicEntry = repository.Inspect(LeafReference, leafId);
            using (var publicOnly = CertificateExchange.ImportPublic(publicEntry.PublicDer, PublicCertificateFormat.Der))
            {
                Check(!publicOnly.HasPrivateKey && CertificateOperations.Inspect(publicOnly).CertificateId == leafId,
                    "public entry DER contains exact public certificate only");
                var publishedPublic = repository.Publish("public-only", publicOnly, new string('f', 64));
                Check(!publishedPublic.ProtectedMaterialPresent && publishedPublic.CertificateId == leafId &&
                    publishedPublic.PublicDer.AsSpan().SequenceEqual(publicEntry.PublicDer), "public-only publication preserves exact public identity without key material");
                Check(!repository.Inspect("public-only", leafId).ProtectedMaterialPresent &&
                    Refused(() => { using var unexpected = repository.Resolve(new("public-only", leafId)); }), "public-only inspect is passive and key resolution refuses");
                using var publicEnvelope = JsonDocument.Parse(File.ReadAllBytes(EntryFile(vault, "public-only")));
                Check(publicEnvelope.RootElement.GetProperty("protectedPfx").ValueKind == JsonValueKind.Null &&
                    publicEnvelope.RootElement.GetProperty("protectedPassword").ValueKind == JsonValueKind.Null, "public-only protected fields are both explicit JSON null");
                var publicHash = Hash(EntryFile(vault, "public-only"));
                Check(Refused(() => repository.Publish("public-only", leaf, new string('a', 64))) &&
                    Hash(EntryFile(vault, "public-only")) == publicHash, "private publication cannot silently reset an existing public-only reference");
                repository.Publish("public-malformed", publicOnly, new string('f', 64));
                var malformedPath = EntryFile(vault, "public-malformed");
                var malformed = JsonNode.Parse(File.ReadAllBytes(malformedPath))!.AsObject();
                malformed["protectedPfx"] = Convert.ToBase64String(new byte[] { 1 });
                File.WriteAllText(malformedPath, malformed.ToJsonString());
                Check(Refused(() => repository.Inspect("public-malformed")), "mixed null and protected material refused rather than adopted");
            }
            stage = Stage.ReadOnly;
            var leafFile = EntryFile(vault, LeafReference);
            var attributes = File.GetAttributes(leafFile);
            const AccessControlSections dataSecurity = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;
            var descriptor = new FileInfo(leafFile).GetAccessControl().GetSecurityDescriptorSddlForm(dataSecurity);
            try
            {
                File.SetAttributes(leafFile, attributes | FileAttributes.ReadOnly);
                var readOnlyHash = Hash(leafFile);
                Check(repository.Inspect(LeafReference, leafId).ProtectedMaterialPresent, "read-only envelope inspection remains passive");
                using (var reopened = repository.Resolve(new(LeafReference, leafId)))
                    Check(reopened.HasPrivateKey, "read-only envelope reopens protected key");
                Check(Hash(leafFile) == readOnlyHash && (File.GetAttributes(leafFile) & FileAttributes.ReadOnly) != 0 &&
                    new FileInfo(leafFile).GetAccessControl().GetSecurityDescriptorSddlForm(dataSecurity) == descriptor,
                    "reads preserve bytes attributes and existing security without resealing");
            }
            finally { File.SetAttributes(leafFile, attributes); }
            stage = Stage.Corrupt;
            repository.Publish("corrupt", ca, new string('d', 64));
            File.WriteAllText(EntryFile(vault, "corrupt"), "{}");
            Check(Refused(() => repository.Inspect("corrupt")), "malformed envelope refused");
            repository.Publish("sealed", ca, new string('e', 64));
            var sealedPath = EntryFile(vault, "sealed");
            var encryptedNode = JsonNode.Parse(File.ReadAllBytes(sealedPath))!.AsObject();
            encryptedNode["protectedPassword"] = Convert.ToBase64String(new byte[128]);
            File.WriteAllText(sealedPath, encryptedNode.ToJsonString());
            Check(repository.Inspect("sealed", caId).ProtectedMaterialPresent, "inspect reports material presence without asserting decryptability");
            Check(Refused(() => { using var unexpected = repository.Resolve(new("sealed", caId)); }), "unavailable protected password cannot resolve a key");
            stage = Stage.Acl;
            var unsafeRoot = Path.Combine(root, "unsafe");
            var unsafeAcl = CortexUserStateDirectories.CreateWindowsUserDirectorySecurity(identity.User);
            unsafeAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(unsafeRoot).Create(unsafeAcl);
            var originalAcl = new DirectoryInfo(unsafeRoot).GetAccessControl().GetSecurityDescriptorSddlForm(dataSecurity);
            Check(Refused(() => new OwnerCertificateRepository(unsafeRoot, sid)), "unrelated principal grant refused");
            Check(new DirectoryInfo(unsafeRoot).GetAccessControl().GetSecurityDescriptorSddlForm(dataSecurity) == originalAcl,
                "admission does not repair unsafe existing ACL");
            stage = Stage.Complete;
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, phase = "create", assertions, stage = stage.ToString(),
                ownedRoot = root, expectedCaId = caId, expectedLeafId = leafId, processId = Environment.ProcessId,
                evidence = "new owner-private protected envelopes persisted and validated; separate-process reopening still required",
                existingCertificateSelected = false, osStoreMutations = "none", disposition = "retained for explicit reopen phase" }));
            return Task.FromResult(0);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { Failure("create", root, caId, leafId, stage, assertions, assertion, error); return Task.FromResult(1); }
    }

    [SupportedOSPlatform("windows")]
    private static Task<int> ReopenWindowsAsync(string suppliedRoot, string expectedCaId, string expectedLeafId)
    {
        string? root = null;
        var stage = Stage.Admission;
        var assertions = 0;
        string? assertion = null;
        var complete = false;
        void Check(bool value, string label) { assertions++; if (!value) { assertion = label; throw new InvalidOperationException("certificate_persistence_assertion_failed"); } }
        try
        {
            using var identity = OrdinaryIdentity();
            root = Canonical(suppliedRoot);
            var vault = Path.Combine(root, "certificates");
            var repository = new OwnerCertificateRepository(vault, identity.User!.Value);
            stage = Stage.Reopen;
            var caEntry = repository.Inspect(CaReference, expectedCaId);
            var leafEntry = repository.Inspect(LeafReference, expectedLeafId);
            var publicEntry = repository.Inspect("public-only", expectedLeafId);
            Check(!publicEntry.ProtectedMaterialPresent && publicEntry.PublicDer.AsSpan().SequenceEqual(leafEntry.PublicDer),
                "new process reopens exact public-only entry with material presence false");
            Check(Refused(() => { using var unexpected = repository.Resolve(new("public-only", expectedLeafId)); }),
                "new process cannot resolve a public-only entry as a private key");
            using var ca = repository.Resolve(new(CaReference, expectedCaId));
            using var leaf = repository.Resolve(new(LeafReference, expectedLeafId));
            Check(ca.HasPrivateKey && leaf.HasPrivateKey, "new process reopens both persisted private keys");
            Check(CertificateOperations.Inspect(ca).CertificateId == expectedCaId &&
                CertificateOperations.Inspect(leaf).CertificateId == expectedLeafId, "reopened keys retain exact supplied public identities");
            stage = Stage.Issue;
            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            using var newLeaf = new CertificateOperations().IssueCodeSigning(new("Reopened issuer fixture", now.AddMinutes(-1), now.AddHours(1), 2048), ca);
            Check(newLeaf.HasPrivateKey && !CertificateOperations.Inspect(newLeaf).IsAuthority, "reopened CA actually issues a fresh leaf");
            stage = Stage.Export;
            foreach (var entry in new[] { caEntry, leafEntry, publicEntry })
            {
                using var publicOnly = CertificateExchange.ImportPublic(entry.PublicDer, PublicCertificateFormat.Der);
                var der = CertificateOperations.ExportPublic(publicOnly, PublicCertificateFormat.Der);
                var pem = CertificateOperations.ExportPublic(publicOnly, PublicCertificateFormat.Pem);
                using var roundTrip = CertificateExchange.ImportPublic(pem, PublicCertificateFormat.Pem);
                Check(!publicOnly.HasPrivateKey && der.AsSpan().SequenceEqual(entry.PublicDer) &&
                    CertificateOperations.Inspect(roundTrip).CertificateId == entry.CertificateId, "persisted public DER and PEM export exact public identity");
            }
            stage = Stage.PrivateFileExchange;
            var passwords = new FixturePasswordResolver();
            var exchange = new OwnerCertificateFileExchange(root, identity.User.Value, repository, passwords);
            var pfxPath = Path.Combine(root, "owned.pfx");
            var privateExport = exchange.Export(pfxPath, new(LeafReference, expectedLeafId), "fixture-password", CancellationToken.None);
            Check(privateExport.CertificateId == expectedLeafId && privateExport.Destination == pfxPath &&
                privateExport.Bytes > 0 && privateExport.Bytes <= 1024 * 1024 && new FileInfo(pfxPath).Length == privateExport.Bytes &&
                privateExport.Sha256 == Hash(pfxPath), "owned private export reports exact destination bytes and content hash");
            using (var imported = exchange.Import(pfxPath, privateExport.Sha256, "fixture-password", expectedLeafId, CancellationToken.None))
                Check(imported.HasPrivateKey && CertificateOperations.Inspect(imported).CertificateId == expectedLeafId,
                    "held-file protected PFX import reopens exact ephemeral persisted-leaf key");
            var beforeRefusal = passwords.Calls;
            Check(ExchangeRefused(() => { using var unexpected = exchange.Import(pfxPath, new string('0', 64), "fixture-password", expectedLeafId, CancellationToken.None); },
                "certificate_preimage_changed") && passwords.Calls == beforeRefusal,
                "wrong protected-file preimage refuses before password resolution");
            Check(ExchangeRefused(() => exchange.Export(pfxPath, new(LeafReference, expectedLeafId), "fixture-password", CancellationToken.None),
                "certificate_destination_exists") && passwords.Calls == beforeRefusal && Hash(pfxPath) == privateExport.Sha256 &&
                new FileInfo(pfxPath).Length == privateExport.Bytes, "occupied private destination refuses before password resolution and preserves exact bytes");
            var rejectedPath = Path.Combine(root, "rejected.pfx");
            var rejectedPassword = false;
            try { exchange.Export(rejectedPath, new(LeafReference, expectedLeafId), "fixture-password-invalid", CancellationToken.None); }
            catch (InvalidOperationException error) when (error.Message == "fixture_password_reference_invalid" && error.InnerException is null)
            { rejectedPassword = true; }
            Check(rejectedPassword && !File.Exists(rejectedPath) &&
                !Directory.EnumerateFiles(root, ".cx-*.tmp", SearchOption.TopDirectoryOnly).Any(),
                "failed password resolution preserves exact error and removes held staging file without publishing a destination");
            stage = Stage.Image;
            var apphost = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
            if (!File.Exists(apphost)) return Task.FromResult(Unavailable("own_unsigned_test_image_missing", root));
            using (var stream = File.OpenRead(apphost))
            using (var image = new PEReader(stream))
                if (image.PEHeaders.PEHeader?.CertificateTableDirectory.Size != 0)
                    return Task.FromResult(Unavailable("own_test_image_not_unsigned", root));
            var target = Path.Combine(root, "owned.exe");
            File.Copy(apphost, target, overwrite: false);
            var adapter = new WindowsAuthenticodeFileAdapter(identity.User.Value, root, repository);
            stage = Stage.Sign;
            var signed = adapter.Sign(new(target, Hash(target), new(LeafReference, expectedLeafId)));
            Check(signed.State == "signed" && signed.CertificateId == expectedLeafId && signed.ContentSha256 == Hash(target),
                "actual signing resolves persisted leaf rather than a fixture memory resolver");
            stage = Stage.Verify;
            var verified = adapter.Verify(new(target, signed.ContentSha256, expectedLeafId));
            Check(verified.Signature == "valid" && verified.CertificateId == expectedLeafId &&
                verified.Trust == "untrusted" && verified.Revocation == "not_checked", "public adapter verifies exact persisted signer with uninstalled authority");
            stage = Stage.Complete;
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, phase = "reopen", assertions, stage = stage.ToString(), ownedRoot = root,
                expectedCaId, expectedLeafId, processId = Environment.ProcessId, signature = verified.Signature,
                trust = verified.Trust, revocation = verified.Revocation, windowsStatus = verified.WindowsStatus,
                evidence = "separate-process protected-key and public-only reopen, CA issuance, public export, owned protected PFX file exchange and actual new owned PE sign/verify; target never executed",
                osStoreMutations = "none", cleanup = "exact owned names only after success" }));
            complete = true;
            return Task.FromResult(0);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { Failure("reopen", root, expectedCaId, expectedLeafId, stage, assertions, assertion, error); return Task.FromResult(1); }
        finally
        {
            if (complete && root is not null)
            {
                stage = Stage.Cleanup;
                try
                {
                    var vault = Path.Combine(root, "certificates");
                    foreach (var reference in OwnedReferences) File.Delete(EntryFile(vault, reference));
                    File.Delete(Path.Combine(root, "owned.exe"));
                    File.Delete(Path.Combine(root, "owned.pfx"));
                    Directory.Delete(Path.Combine(root, "unsafe"), recursive: false);
                    Directory.Delete(vault, recursive: false);
                    Directory.Delete(root, recursive: false);
                }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                {
                    Console.Error.WriteLine(JsonSerializer.Serialize(new { stage = stage.ToString(), cleanup = "incomplete", ownedRoot = root,
                        primaryResultPreserved = true, failureType = error.GetType().Name, outerHResult = error.HResult }));
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static WindowsIdentity OrdinaryIdentity()
    {
        var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null || identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        { identity.Dispose(); throw new UnauthorizedAccessException("ordinary_owner_required"); }
        return identity;
    }
    [SupportedOSPlatform("windows")]
    private static string Canonical(string value)
    {
        var context = new WindowsPathContextNormalizer().Normalize(value);
        if (context.Kind != "local" || context.CanonicalPath.Length > 100 ||
            !string.Equals(Path.TrimEndingDirectorySeparator(context.CanonicalPath), Path.TrimEndingDirectorySeparator(value), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("fixture_root_invalid");
        return Path.TrimEndingDirectorySeparator(context.CanonicalPath);
    }
    private static string EntryFile(string vault, string reference) => Path.Combine(vault, "certificate-" + reference + ".v1.json");
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static bool Refused(Action action)
    {
        try { action(); return false; }
        catch (Exception error) when (error is OwnerCertificateRepositoryException or UnauthorizedAccessException or ArgumentException or IOException) { return true; }
    }
    private static bool ExchangeRefused(Action action, string expectedCode)
    {
        try { action(); return false; }
        catch (CertificateOperationException error) when (error.Code == expectedCode) { return true; }
    }
    private sealed class FixturePasswordResolver : ICertificateSecretResolver
    {
        internal int Calls { get; private set; }
        public CertificateSecret Resolve(string reference)
        {
            if (reference != "fixture-password") throw new InvalidOperationException("fixture_password_reference_invalid");
            Calls++;
            Span<char> password = stackalloc char[] { 'f', 'i', 'x', 't', 'u', 'r', 'e', '-', 'p', 'f', 'x', '-', '7', '2', '9', '1' };
            try { return new CertificateSecret(password); }
            finally { password.Clear(); }
        }
    }
    private static int Unavailable(string code, string? root = null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok = false, available = false, code, ownedRoot = root,
            evidence = "unavailable is not a pass", disposition = root is null ? "not created" : "retained; no cleanup" }));
        return 77;
    }
    [SupportedOSPlatform("windows")]
    private static void Failure(string phase, string? root, string? caId, string? leafId, Stage stage, int assertions, string? assertion, Exception error)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, phase, stage = stage.ToString(), assertions, assertion,
            ownedRoot = root, expectedCaId = PublicId(caId), expectedLeafId = PublicId(leafId), failureType = error.GetType().Name, outerHResult = error.HResult,
            directInnerWin32Code = (error.InnerException as Win32Exception)?.NativeErrorCode,
            code = error is OwnerCertificateRepositoryException repository ? repository.Code : error is OwnerCertificateFileExchangeException exchange ? exchange.Code : error is AuthenticodeAttemptException native ? native.Code :
                error is CertificateOperationException certificate ? certificate.Code : "certificate_persistence_failed",
            windowsStatus = error is AuthenticodeAttemptException signing ? signing.WindowsStatus : null,
            published = error is OwnerCertificateRepositoryException { Published: true } || error is OwnerCertificateFileExchangeException { Published: true },
            unknown = error is OwnerCertificateRepositoryException { OutcomeUnknown: true } || error is OwnerCertificateFileExchangeException { OutcomeUnknown: true } || error is AuthenticodeAttemptException { OutcomeUnknown: true },
            signIssued = error is AuthenticodeAttemptException { Issued: true }, disposition = "retained; no failure cleanup" }));
    }
    private static string? PublicId(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ? value : null;
}
