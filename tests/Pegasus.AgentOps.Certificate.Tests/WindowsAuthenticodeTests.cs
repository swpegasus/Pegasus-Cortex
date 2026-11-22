using System.Reflection;
using System.ComponentModel;
using System.Reflection.PortableExecutable;
using System.Formats.Asn1;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Pegasus.AgentOps.Certificates;

internal static class WindowsAuthenticodeTests
{
    private enum Stage
    {
        Admission, FixtureCreate, PureVectors, EphemeralCertificates,
        PeInspect, PeAdmissionChecks, PeSign, PeVerify, PeOfflineTrust, PeTamper, PeTamperVerify,
        Ps1Inspect, Ps1AdmissionChecks, Ps1Sign, Ps1Verify, Ps1OfflineTrust, Ps1Tamper, Ps1TamperVerify,
        Complete, Cleanup
    }
    private sealed record UnsignedInspection(string Format, string Stage, string Signature,
        int? WindowsStatus, int TrustCallsDelta);
    private sealed record OfflineTrustObservation(string Format, string Stage, string Signature,
        string Trust, string Revocation, string? CertificateId, int? WindowsStatus);
    private enum InspectOnlyStage { Admission, PureVectors, Lease, PeCertificateTable, NativeInspect, Complete }
    private sealed record PeCertificateTable(string Presence, int? FileOffset, int? Size);
    // 77 is explicitly unavailable, never a pass. All signing targets are NEW
    // copies/material in the caller's absent owned root. Nothing here executes them.
    internal static Task<int> RunAsync(string newOwnedFixtureRoot)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(Unavailable("windows_digest_signing_unavailable"));
        return RunWindowsAsync(newOwnedFixtureRoot);
    }

    // Diagnostic entry for an already retained attempt. It does not recreate
    // keys, assert integrity, call trust, or replay any signing effect.
    internal static Task<int> RunInspectAsync(string retainedOwnedFixtureRoot)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult(Unavailable("windows_inspection_unavailable"));
        return RunInspectWindowsAsync(retainedOwnedFixtureRoot);
    }

    [SupportedOSPlatform("windows")]
    private static Task<int> RunInspectWindowsAsync(string retainedOwnedFixtureRoot)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null || identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return Task.FromResult(Unavailable("ordinary_user_required"));
        string root;
        try { root = AuthenticodeFileLease.Canonical(retainedOwnedFixtureRoot, directory: true); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            InspectFailure(null, null, InspectOnlyStage.Admission, AuthenticodeNative.InspectionStage.NotStarted, null, error);
            return Task.FromResult(1);
        }
        var pureAssertions = 0;
        string? failedPureVector = null;
        try
        {
            void CheckPure(bool condition, string label)
            {
                pureAssertions++;
                if (condition) return;
                failedPureVector = label;
                throw new InvalidOperationException("authenticode_pure_vector_failed");
            }
            PePaddingVectors(CheckPure);
            Console.WriteLine(JsonSerializer.Serialize(new { observation = "pe_padding_vectors", assertions = pureAssertions,
                evidence = "pure in-memory normalization checks; no file or signing effects" }));
            var beforePs1 = pureAssertions;
            Ps1StructureVectors(CheckPure);
            Console.WriteLine(JsonSerializer.Serialize(new { observation = "ps1_structure_vectors", assertions = pureAssertions - beforePs1,
                evidence = "pure in-memory encoding/marker checks; no file or signing effects" }));
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            InspectFailure(root, null, InspectOnlyStage.PureVectors, AuthenticodeNative.InspectionStage.NotStarted,
                null, error, failedPureVector);
            return Task.FromResult(1);
        }
        var failures = 0;
        // Exact owned leaf names only: no directory scan, writes, or cleanup.
        foreach (var leaf in new[] { "owned.exe", "owned.ps1" })
        {
            var stage = InspectOnlyStage.Lease;
            var nativeStage = AuthenticodeNative.InspectionStage.NotStarted;
            PeCertificateTable? table = null;
            try
            {
                using var file = new AuthenticodeFileLease(root, Path.Combine(root, leaf), writable: false);
                stage = InspectOnlyStage.PeCertificateTable;
                table = ReadPeCertificateTable(file, leaf == "owned.exe");
                stage = InspectOnlyStage.NativeInspect;
                var result = AuthenticodeNative.Inspect(file, expectedCertificateId: null, out nativeStage);
                stage = InspectOnlyStage.Complete;
                Console.WriteLine(JsonSerializer.Serialize(new { mode = "read_only_inspection", inspectionCompleted = true,
                    ownedRoot = root, file = leaf, stage = stage.ToString(), nativeStage = nativeStage.ToString(),
                    signature = result.Signature, windowsStatus = result.WindowsStatus, peCertificateTable = table,
                    evidence = "diagnostic observation; no integrity assertion or trust evaluation", disposition = "retained; no cleanup" }));
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                failures++;
                InspectFailure(root, leaf, stage, nativeStage, table, error);
            }
        }
        return Task.FromResult(failures == 0 ? 0 : 1);
    }

    private static PeCertificateTable ReadPeCertificateTable(AuthenticodeFileLease file, bool isPe)
    {
        if (!isPe) return new("not_applicable", null, null);
        using var borrowed = new SafeFileHandle(file.Handle.DangerousGetHandle(), ownsHandle: false);
        using var input = new FileStream(borrowed, FileAccess.Read);
        input.Position = 0;
        using var image = new PEReader(input);
        if (image.PEHeaders.PEHeader is not { } header) return new("missing_optional_header", null, null);
        var table = header.CertificateTableDirectory;
        var presence = table.RelativeVirtualAddress == 0 && table.Size == 0 ? "absent"
            : table.RelativeVirtualAddress > 0 && table.Size > 0 && (long)table.RelativeVirtualAddress + table.Size <= input.Length
                ? "present" : "malformed_range";
        return new(presence, table.RelativeVirtualAddress, table.Size);
    }

    private static void InspectFailure(string? root, string? leaf, InspectOnlyStage stage,
        AuthenticodeNative.InspectionStage nativeStage, PeCertificateTable? table, Exception error, string? assertion = null)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { mode = "read_only_inspection", inspectionCompleted = false,
            ownedRoot = root, file = leaf, stage = stage.ToString(), nativeStage = nativeStage.ToString(),
            peCertificateTable = table, assertion, failureType = error.GetType().Name, outerHResult = error.HResult,
            directInnerWin32Code = (error.InnerException as Win32Exception)?.NativeErrorCode,
            code = error is CertificateOperationException certificate ? certificate.Code : "authenticode_inspection_failed",
            disposition = "retained; no cleanup" }));
    }

    [SupportedOSPlatform("windows")]
    private static Task<int> RunWindowsAsync(string newOwnedFixtureRoot)
    {
        if (!AuthenticodeNative.SigningAvailable)
            return Task.FromResult(Unavailable("windows_digest_signing_unavailable"));
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null || identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return Task.FromResult(Unavailable("ordinary_user_required"));
        var root = AuthenticodeFileLease.Canonical(newOwnedFixtureRoot, directory: true);
        if (Directory.Exists(root) || File.Exists(root)) return Task.FromResult(Unavailable("authenticode_fixture_root_occupied"));
        var files = new List<string>();
        var inspections = new List<UnsignedInspection>(2);
        var offlineTrust = new List<OfflineTrustObservation>(2);
        var assertions = 0;
        var stage = Stage.FixtureCreate;
        var fullSuccess = false;
        string? failedAssertion = null;
        string? expectedSignerCertificateId = null;
        void Check(bool condition, string label)
        {
            assertions++;
            if (condition) return;
            failedAssertion = label.Length <= 160 && label.All(character => character is >= ' ' and <= '~')
                ? label : "assertion_label_unavailable";
            throw new InvalidOperationException("authenticode_assertion_failed");
        }
        try
        {
            var security = new DirectorySecurity();
            security.SetOwner(identity.User); security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { identity.User, new SecurityIdentifier("S-1-5-18"), new SecurityIdentifier("S-1-5-32-544") })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).Create(security);
            stage = Stage.PureVectors;
            CompletionPolicy(Check);
            IndirectDataVectors(Check);
            PePaddingVectors(Check);
            Ps1StructureVectors(Check);
            stage = Stage.EphemeralCertificates;
            var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var operations = new CertificateOperations();
            using var authority = operations.CreateAuthority(new("Authenticode scratch root", now.AddMinutes(-1), now.AddDays(2), 2048));
            using var signer = operations.IssueCodeSigning(new("Authenticode scratch leaf", now.AddMinutes(-1), now.AddDays(1), 2048), authority);
            var id = CertificateOperations.Inspect(signer).CertificateId;
            expectedSignerCertificateId = id;
            var reference = new CertificateKeyReference("scratch-authenticode", id);
            var resolver = new ScratchKeys(signer);
            var trustCalls = 0;
            var adapter = new WindowsAuthenticodeFileAdapter(identity.User.Value, root, resolver, (_, _) =>
            {
                trustCalls++;
                return new("not_evaluated", "not_evaluated", "not_checked", null, null);
            });
            stage = Stage.PeInspect;
            var apphost = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
            if (!File.Exists(apphost)) return Task.FromResult(Unavailable("own_unsigned_test_apphost_unavailable", root, stage, assertions));
            // Inspect only our newly built test image, never an installed publisher.
            using (var image = File.OpenRead(apphost))
            using (var pe = new PEReader(image))
                if (pe.PEHeaders.PEHeader?.CertificateTableDirectory.Size != 0)
                    return Task.FromResult(Unavailable("own_test_apphost_is_not_unsigned", root, stage, assertions));
            var pePath = Path.Combine(root, "owned.exe"); files.Add(pePath); File.Copy(apphost, pePath, overwrite: false);
            stage = Stage.Ps1Inspect;
            var script = Path.Combine(root, "owned.ps1"); files.Add(script);
            File.WriteAllText(script, "# isolated signing fixture; never executed\r\n'authenticode fixture'\r\n", new UnicodeEncoding(false, true));
            foreach (var path in new[] { pePath, script })
            {
                var isPe = path == pePath;
                stage = isPe ? Stage.PeInspect : Stage.Ps1Inspect;
                var beforeTrust = trustCalls;
                var before = Hash(path);
                var unsigned = adapter.Verify(new(path, before, id));
                var inspection = new UnsignedInspection(isPe ? "PE" : "PS1", stage.ToString(),
                    unsigned.Signature, unsigned.WindowsStatus, trustCalls - beforeTrust);
                inspections.Add(inspection);
                Console.WriteLine(JsonSerializer.Serialize(new { observation = "unsigned_inspection", inspection }));
                if (unsigned.Signature == "unsupported") return Task.FromResult(Unavailable(isPe ? "pe_sip_unavailable" : "ps1_sip_unavailable", root, stage, assertions));
                Check(unsigned.Signature == "not_signed" && trustCalls == beforeTrust, "unsigned inspection does not invoke trust");
                stage = isPe ? Stage.PeAdmissionChecks : Stage.Ps1AdmissionChecks;
                Check(Attempt(() => adapter.Sign(new(path, new string('0', 64), reference)), "authenticode_preimage_changed", false), "wrong preimage refused before issue");
                var wrongSigningPin = false;
                try { _ = adapter.Sign(new(path, before, reference with { ExpectedCertificateId = new string('0', 64) })); }
                catch (CertificateOperationException error) when (error.Code == "certificate_identity_mismatch") { wrongSigningPin = true; }
                Check(wrongSigningPin, "wrong signing certificate pin refused before issue");
                Check(Hash(path) == before, "wrong preimage preserves fixture");
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    Check(Attempt(() => adapter.Sign(new(path, before, reference), canceled.Token), "authenticode_canceled_before_issue", false), "canceled before issue");
                }
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var refused = false;
                    try { _ = adapter.Sign(new(path, before, reference)); } catch (IOException) { refused = true; }
                    Check(refused, "existing writer lock refused without weakening sharing");
                }
                Check(Hash(path) == before, "lock and cancellation preserved fixture");
                stage = isPe ? Stage.PeSign : Stage.Ps1Sign;
                var signed = adapter.Sign(new(path, before, reference));
                Check(signed.State == "signed" && signed.CertificateId == id && !signed.Timestamped && signed.ContentSha256 == Hash(path), "actual native signing postcondition");
                stage = isPe ? Stage.PeVerify : Stage.Ps1Verify;
                var verified = adapter.Verify(new(path, signed.ContentSha256, id));
                Check(verified.Signature == "valid" && verified.CertificateId == id && verified.Trust == "not_evaluated" && verified.Revocation == "not_checked",
                    "actual embedded message and SIP integrity without system trust");
                stage = isPe ? Stage.PeOfflineTrust : Stage.Ps1OfflineTrust;
                var beforeOfflineTrust = trustCalls;
                var publicAdapter = new WindowsAuthenticodeFileAdapter(identity.User.Value, root, resolver);
                var offline = publicAdapter.Verify(new(path, signed.ContentSha256, id));
                var observation = new OfflineTrustObservation(isPe ? "PE" : "PS1", stage.ToString(),
                    offline.Signature, offline.Trust, offline.Revocation, offline.CertificateId, offline.WindowsStatus);
                offlineTrust.Add(observation);
                Console.WriteLine(JsonSerializer.Serialize(new { observation = "offline_windows_trust", result = observation }));
                var expectedTrustStatus = offline.WindowsStatus is { } windowsStatus &&
                    (unchecked((uint)windowsStatus) is 0x800b0109 or 0x800b010a);
                Check(offline.Signature == "valid" && offline.CertificateId == id && offline.Revocation == "not_checked" &&
                    offline.Trust == "untrusted" && expectedTrustStatus && trustCalls == beforeOfflineTrust,
                    "public offline Windows trust preserves integrity and rejects uninstalled ephemeral authority");
                stage = isPe ? Stage.PeVerify : Stage.Ps1Verify;
                var wrongPin = adapter.Verify(new(path, signed.ContentSha256, new string('0', 64)));
                Check(wrongPin.Signature == "identity_mismatch", "wrong expected signer rejected");
                Check(Attempt(() => adapter.Sign(new(path, signed.ContentSha256, reference)), "authenticode_already_signed", false), "existing signature never overwritten");
                Check(Hash(path) == signed.ContentSha256, "existing signed bytes preserved");
                stage = isPe ? Stage.PeTamper : Stage.Ps1Tamper;
                Tamper(path);
                // Supply the new outer preimage so this exercises Authenticode's
                // signature-covered digest, not only the caller's SHA256 guard.
                stage = isPe ? Stage.PeTamperVerify : Stage.Ps1TamperVerify;
                var tampered = adapter.Verify(new(path, Hash(path), id));
                Check(tampered.Signature == "invalid", "covered-byte tamper rejected by SIP integrity");
            }
            stage = Stage.Complete;
            fullSuccess = true;
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, assertions, stage = stage.ToString(),
                inspections, offlineTrust, expectedSignerCertificateId,
                evidence = "actual Windows Authenticode/SIP signing, integrity and offline Windows trust of NEW copied PE and nonexecuted PS1 fixtures",
                injectedTrust = "not_evaluated; injected explicitly", revocation = "not_checked",
                signingCertificateSelection = "newly generated ephemeral test certificates; no existing signing certificate selected",
                signingStore = "temporary memory store", osInternalStoreReads = "not traced here",
                cleanup = "exact owned names only after full success; any cleanup failure reported separately",
                ephemeralKeysOnly = true, installedFiles = "not modified", noTargetsExecuted = true }));
            return Task.FromResult(0);
        }
        catch (Exception error)
        {
            fullSuccess = false;
            Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, assertions, stage = stage.ToString(),
                inspections, offlineTrust, expectedSignerCertificateId,
                assertion = failedAssertion, failureType = error.GetType().Name, outerHResult = error.HResult,
                directInnerWin32Code = (error.InnerException as Win32Exception)?.NativeErrorCode,
                code = error is AuthenticodeAttemptException attempt ? attempt.Code : error is CertificateOperationException certificate ? certificate.Code : "authenticode_fixture_failed",
                issued = error is AuthenticodeAttemptException { Issued: true }, unknown = error is AuthenticodeAttemptException { OutcomeUnknown: true },
                windowsStatus = error is AuthenticodeAttemptException native ? native.WindowsStatus : null,
                ownedRoot = root, disposition = "retained for diagnosis; no failure cleanup" }));
            return Task.FromResult(1);
        }
        finally
        {
            // An issued/unknown attempt must remain inspectable. Only a complete
            // successful fixture permits exact, nonrecursive diagnostic cleanup.
            if (fullSuccess)
            {
                stage = Stage.Cleanup;
                try
                {
                    foreach (var path in files) if (File.Exists(path)) File.Delete(path);
                    Directory.Delete(root, recursive: false);
                }
                catch (Exception cleanup) when (cleanup is not OutOfMemoryException and not StackOverflowException)
                {
                    // Preserve the primary native result even if diagnostic
                    // cleanup is incomplete; report the retained root separately.
                    Console.Error.WriteLine(JsonSerializer.Serialize(new { stage = stage.ToString(), cleanup = "incomplete",
                        primaryResultPreserved = true, ownedRoot = root, outerHResult = cleanup.HResult,
                        directInnerWin32Code = (cleanup.InnerException as Win32Exception)?.NativeErrorCode }));
                }
            }
        }
    }

    private static int Unavailable(string reason, string? ownedRoot = null, Stage stage = Stage.Admission, int assertions = 0)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok = false, available = false, reason, stage = stage.ToString(), assertions,
            ownedRoot, disposition = ownedRoot is null ? "not created" : "retained for diagnosis; no unavailable cleanup",
            evidence = "not executed/unavailable, not a pass" }));
        return 77;
    }
    [SupportedOSPlatform("windows")]
    private static void CompletionPolicy(Action<bool, string> check)
    {
        var id = new string('0', 64);
        var postimage = new string('1', 64);
        foreach (var status in new[] { 0, unchecked((int)0x80004004) })
        foreach (var observed in new[] { false, true })
        foreach (var canceled in new[] { false, true })
        {
            try
            {
                var result = WindowsAuthenticodeFileAdapter.CompleteSignAttempt(status, observed, canceled, id, postimage);
                check(status == 0 && observed && result is { State: "signed", Timestamped: false } &&
                    result.CertificateId == id && result.ContentSha256 == postimage,
                    "observed native success remains signed despite late cancellation");
            }
            catch (AuthenticodeAttemptException failure)
            {
                check(status != 0 || !observed, "an observed native success never becomes a cancellation failure");
                check(failure.Issued && failure.OutcomeUnknown == !observed && failure.PostconditionObserved == observed && failure.WindowsStatus == status,
                    "completion projection preserves issued, observed and unknown as separate facts");
                check(!observed || failure.Code == "authenticode_native_error_postcondition_observed",
                    "observed native failure remains a failure with a known signed effect");
            }
        }
    }
    private static void IndirectDataVectors(Action<bool, string> check)
    {
        var valid = IndirectVector(nested: true, digestLength: 32);
        check(AuthenticodeNative.SupportedIndirect(valid), "standard nested SHA256 DigestInfo accepted");
        check(!AuthenticodeNative.SupportedIndirect(IndirectVector(nested: false, digestLength: 32)), "flattened DigestInfo rejected");
        check(!AuthenticodeNative.SupportedIndirect(IndirectVector(nested: true, digestLength: 31)), "wrong SHA256 digest length rejected");
        check(!AuthenticodeNative.SupportedIndirect(valid.AsMemory(0, valid.Length - 1)), "truncated indirect data rejected");
        check(!AuthenticodeNative.SupportedIndirect(valid.Concat(new byte[] { 0 }).ToArray()), "trailing indirect data rejected");
    }
    private static void PePaddingVectors(Action<bool, string> check)
    {
        var aligned = PaddingMessage(4);
        check(AuthenticodeNative.TryNormalizePeCertificateMessage(aligned, aligned.Length, out var plain) && plain.SequenceEqual(aligned),
            "unaltered ASN message accepted without padding");
        var unaligned = PaddingMessage(0);
        check(AuthenticodeNative.TryNormalizePeCertificateMessage(unaligned, unaligned.Length, out var unpadded) && unpadded.SequenceEqual(unaligned),
            "unaligned ASN message preserved when dwLength excludes storage padding");
        for (var padding = 1; padding <= 7; padding++)
        {
            var encoded = PaddingMessage((8 - padding - 4 + 8) % 8);
            var payload = encoded.Concat(new byte[padding]).ToArray();
            check(AuthenticodeNative.TryNormalizePeCertificateMessage(payload, payload.Length, out var normalized) && normalized.SequenceEqual(encoded),
                "each one-through-seven-byte aligned zero padding accepted");
        }
        var nonzero = PaddingMessage(3).Concat(new byte[] { 1 }).ToArray();
        check(!AuthenticodeNative.TryNormalizePeCertificateMessage(nonzero, nonzero.Length, out _), "nonzero PE padding rejected");
        var excess = aligned.Concat(new byte[8]).ToArray();
        check(!AuthenticodeNative.TryNormalizePeCertificateMessage(excess, excess.Length, out _), "eight trailing zero bytes rejected");
        var extraAsn = PaddingMessage(2).Concat(new byte[] { 5, 0 }).ToArray();
        check(!AuthenticodeNative.TryNormalizePeCertificateMessage(extraAsn, extraAsn.Length, out _), "extra ASN value rejected");
        var misaligned = aligned.Concat(new byte[1]).ToArray();
        check(!AuthenticodeNative.TryNormalizePeCertificateMessage(misaligned, misaligned.Length, out _), "zero suffix without required alignment rejected");
        check(!AuthenticodeNative.TryNormalizePeCertificateMessage(aligned, aligned.Length + 1, out _), "SIP and PE payload length mismatch rejected");
        check(!AuthenticodeNative.TryNormalizePeCertificateMessage([0x30, 0x82, 0x01], 3, out _), "truncated ASN envelope rejected");
        check(!AuthenticodeNative.TryNormalizePeCertificateMessage([0x04, 0], 2, out _), "nonsequence envelope rejected");
        check(!AuthenticodeNative.TryNormalizePeCertificateMessage([], 0, out _), "empty PE message rejected");
    }

    private static void Ps1StructureVectors(Action<bool, string> check)
    {
        const string begin = "# SIG # Begin signature block", end = "# SIG # End signature block";
        var block = $"\r\n{begin}\r\n# AA==\r\n{end}\r\n";
        var absent = AuthenticodeNative.Ps1SignatureStructure.Absent;
        var present = AuthenticodeNative.Ps1SignatureStructure.Present;
        var malformed = AuthenticodeNative.Ps1SignatureStructure.Malformed;
        var unknown = AuthenticodeNative.Ps1SignatureStructure.UnrecognizedEncoding;
        void Expect(string text, AuthenticodeNative.Ps1SignatureStructure expected, string label)
        {
            foreach (var unicode in new[] { false, true })
                check(AuthenticodeNative.InspectPs1Structure(Ps1Bytes(text, unicode)) == expected,
                    (unicode ? "UTF16LE " : "ASCII ") + label);
        }
        Expect("", absent, "empty script has structural absence");
        Expect("# ordinary script\r\n'fixture'\r\n", absent, "ordinary script has structural absence");
        Expect(block, present, "complete block is present and never unsigned");
        Expect(begin + "\r\n", malformed, "orphan begin marker refused");
        Expect(end + "\r\n", malformed, "orphan end marker refused");
        Expect(end + "\r\n" + begin + "\r\n", malformed, "reversed markers refused");
        Expect(block + block, malformed, "multiple blocks refused");
        Expect(block.Replace("Begin", "begin", StringComparison.Ordinal), malformed, "case-variant signature-looking marker refused");
        Expect("# SIG # incomplete\r\n", malformed, "malformed signature-looking prefix refused");
        Expect("'" + begin + "'\r\n" + end + "\r\n", malformed, "embedded markers refused");
        Expect(block + "'trailing script'\r\n", malformed, "script after signature block refused");
        check(AuthenticodeNative.InspectPs1Structure(Ps1Bytes("# Unicode \u00e9\r\n", true)) == absent,
            "strict BOM UTF16LE non-ASCII text recognized");
        check(AuthenticodeNative.InspectPs1Structure(Ps1Bytes("'a'\0", false)) == unknown,
            "NUL bytes do not prove ASCII absence or infer BOMless Unicode");
        check(AuthenticodeNative.InspectPs1Structure(Ps1Bytes("'a'\0", true)) == malformed,
            "NUL in recognized UTF16LE refused");
        check(AuthenticodeNative.InspectPs1Structure([0xff, 0xfe, 0x41]) == malformed, "odd UTF16LE refused");
        check(AuthenticodeNative.InspectPs1Structure([0xff, 0xfe, 0x00, 0xd8]) == malformed, "unpaired UTF16LE surrogate refused");
        check(AuthenticodeNative.InspectPs1Structure(Encoding.UTF8.GetBytes("# \u00e9\r\n")) == unknown,
            "BOMless non-ASCII gets no unsigned shortcut");
        check(AuthenticodeNative.InspectPs1Structure(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(block)).ToArray()) == unknown,
            "UTF8 BOM signed text retains existing SIP route");
        check(AuthenticodeNative.InspectPs1Structure([0xfe, 0xff, 0x00, 0x41]) == unknown,
            "UTF16BE retains existing SIP route");
        check(AuthenticodeNative.InspectPs1Structure([0xff, 0xfe, 0, 0, 0x41, 0, 0, 0]) == unknown,
            "UTF32LE is not mistaken for UTF16LE");
        check(AuthenticodeNative.InspectPs1Structure([0, 0, 0xfe, 0xff, 0, 0, 0, 0x41]) == unknown,
            "UTF32BE retains existing SIP route");
    }

    private static byte[] Ps1Bytes(string text, bool unicode) => unicode
        ? Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray()
        : Encoding.ASCII.GetBytes(text);

    private static byte[] PaddingMessage(int octets)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence()) writer.WriteOctetString(new byte[octets]);
        return writer.Encode();
    }

    private static byte[] IndirectVector(bool nested, int digestLength)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            // SpcAttributeTypeAndOptionalValue followed by DigestInfo, whose
            // AlgorithmIdentifier is itself a sequence, not its parent container.
            using (writer.PushSequence()) writer.WriteObjectIdentifier("1.3.6.1.4.1.311.2.1.15");
            if (nested)
            {
                using (writer.PushSequence()) { Algorithm(writer); writer.WriteOctetString(new byte[digestLength]); }
            }
            else { Algorithm(writer); writer.WriteOctetString(new byte[digestLength]); }
        }
        return writer.Encode();

        static void Algorithm(AsnWriter value)
        { using (value.PushSequence()) { value.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1"); value.WriteNull(); } }
    }
    private static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static bool Attempt(Action action, string code, bool issued)
    {
        try { action(); return false; }
        catch (AuthenticodeAttemptException failure) { return failure.Code == code && failure.Issued == issued && !failure.OutcomeUnknown; }
    }
    private static void Tamper(string path)
    {
        if (Path.GetExtension(path).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            // Change a covered character in the initial comment, preserving size,
            // encoding and the complete native signature text block.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            stream.Position = 6; var value = stream.ReadByte(); stream.Position = 6; stream.WriteByte((byte)(value ^ 1)); stream.Flush(true); return;
        }
        int offset;
        using (var input = File.OpenRead(path))
        using (var pe = new PEReader(input))
            offset = pe.PEHeaders.SectionHeaders.First(section => section.SizeOfRawData > 0).PointerToRawData;
        using var output = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        output.Position = offset; var original = output.ReadByte(); if (original < 0) throw new IOException("fixture_section_unavailable");
        output.Position = offset; output.WriteByte((byte)(original ^ 1)); output.Flush(true);
    }
    private sealed class ScratchKeys(X509Certificate2 signer) : ICertificateKeyResolver
    {
        public X509Certificate2 Resolve(CertificateKeyReference reference)
        {
            if (reference.Reference != "scratch-authenticode") throw new CertificateOperationException("fixture_key_missing");
            using var publicOnly = X509CertificateLoader.LoadCertificate(signer.RawData);
            using var key = signer.GetRSAPrivateKey() ?? throw new InvalidOperationException("fixture_key_missing");
            return publicOnly.CopyWithPrivateKey(key);
        }
    }
}
