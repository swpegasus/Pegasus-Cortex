using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Engine;

internal static class OwnerCertificateSecretStoreTests
{
    private const string OwnerSid = "S-1-5-21-100-200-300-1001";

    internal static Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult(77);
        return Task.FromResult(RunWindows());
    }

    [SupportedOSPlatform("windows")]
    private static int RunWindows()
    {
        var assertions = 0;
        try
        {
            var peer = new LocalPeer(OwnerSid, 7, Elevated: false);
            var current = peer;
            using var calls = new FakeCalls();
            var native = new OwnerCertificateSecretNative(calls);
            var store = new OwnerCertificateSecretStore(OwnerSid, () => current, native);
            var resolver = new OwnerCertificateSecrets(OwnerSid, () => current, native);
            char[] password = ['f', 'i', 'x', 't', 'u', 'r', 'e', '\u03A9'];
            try
            {
                foreach (var invalid in new[] { "", "A", ".a", "../x", "a/b", "a:b", "a b", new string('a', 65) })
                {
                    Check(!store.Inspect(invalid).Ok && !store.Save(invalid, password).Ok && !store.Remove(invalid).Ok,
                        "Invalid references reached provisioning.");
                }
                Check(calls.Reads == 0 && calls.Writes == 0 && calls.Deletes == 0, "Invalid references reached native calls.");
                foreach (var invalid in new[] { Array.Empty<char>(), new[] { '\0' }, new[] { '\uD800' },
                    new[] { '\uDC00' }, Enumerable.Repeat('x', 1025).ToArray() })
                {
                    try
                    {
                        var refused = store.Save("invalid-password", invalid);
                        Check(!refused.Ok && !refused.ActionIssued && refused.Error == "certificate_secret_blob_invalid",
                            "Invalid password encoding/size reached native write.");
                    }
                    finally { Array.Clear(invalid); }
                }
                Check(calls.Writes == 0, "Invalid passwords wrote a credential.");
                foreach (var denied in new[] { peer with { Sid = "S-1-5-21-100-200-300-1002" },
                    peer with { Elevated = true }, peer with { Session = 0 }, peer with { Session = 8 } })
                {
                    current = denied;
                    Check(store.Save("denied", password) is { Ok: false, ActionIssued: false, OutcomeUnknown: false,
                            Error: "certificate_secret_owner_denied" } && !store.Inspect("denied").Ok && !store.Remove("denied").Ok,
                        "Changed identity reached credentials.");
                }
                current = peer;
                Check(calls.Reads == 0 && calls.Writes == 0 && calls.Deletes == 0, "Identity refusal made a native call.");
                using (var entryCalls = new FakeCalls())
                {
                    var samples = 0;
                    var entryStore = new OwnerCertificateSecretStore(OwnerSid,
                        () => ++samples >= 3 ? peer with { Elevated = true } : peer,
                        new OwnerCertificateSecretNative(entryCalls));
                    Check(entryStore.Save("entry-denied", password) is { Ok: false, ActionIssued: false, OutcomeUnknown: false,
                            Error: "certificate_secret_owner_denied" } && entryCalls.Writes == 0 &&
                        entryCalls.WriteFrees == 1 && entryCalls.AllWriteBlobsCleared,
                        "Identity change at native entry wrote or retained a secret buffer.");
                }

                Check(store.Inspect("consumer") is { Ok: true, State: "missing", ActionIssued: false }, "Missing reference status changed.");
                var saved = store.Save("consumer", password);
                Check(saved is { Ok: true, State: "saved", ActionIssued: true, OutcomeUnknown: false } &&
                    calls.Writes == 1 && calls.LastType == 1 && calls.LastPersist == 2 && calls.LastFlags == 0 &&
                    calls.LastTarget == OwnerCertificateSecretTarget.Prefix + "consumer",
                    "Save did not issue the fixed CurrentUser Generic Credential upsert.");
                Check(store.Inspect("consumer") is { Ok: true, State: "present" }, "Saved reference was not observed.");
                Check(ResultFields(saved).SetEquals(new[] { "Ok", "State", "Error", "ActionIssued", "OutcomeUnknown" }),
                    "Result exposed more than UI status metadata.");
                using (var resolved = resolver.Resolve("consumer"))
                    Check(true, "Existing resolver could not consume provisioned bytes.");

                // The existing PKCS12 consumer invokes the original resolver twice.
                // Only this ephemeral certificate and an in-memory stream are used.
                var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
                var operations = new CertificateOperations(new FixedClock(now));
                using var authority = operations.CreateAuthority(new("Ephemeral credential consumer", now.AddDays(-1), now.AddDays(1), 2048));
                var id = CertificateOperations.Inspect(authority).CertificateId;
                using (var stream = new MemoryStream())
                {
                    try
                    {
                        var before = calls.Reads;
                        var exported = CertificateExchange.ExportProtectedPkcs12(new("fixture-key", id),
                            new FixtureKeys(authority), "consumer", resolver, stream);
                        stream.Position = 0;
                        using var imported = CertificateExchange.ImportProtectedPkcs12(stream, "consumer", resolver, id);
                        Check(exported.CertificateId == id && imported.HasPrivateKey &&
                            CertificateOperations.Inspect(imported).CertificateId == id && calls.Reads == before + 2,
                            "Provisioned reference did not reach existing PKCS12 export/import readers.");
                    }
                    finally
                    {
                        if (stream.TryGetBuffer(out var owned)) CryptographicOperations.ZeroMemory(owned.AsSpan());
                    }
                }
                char[] replacement = ['n', 'e', 'w', '\uD83D', '\uDE00'];
                try
                {
                    var before = calls.Writes;
                    Check(store.Save("consumer", replacement).Ok && calls.Writes == before + 1 && calls.Count == 1,
                        "Save did not replace the same exact target.");
                    using var replaced = resolver.Resolve("consumer");
                }
                finally { Array.Clear(replacement); }
                foreach (var length in new[] { 1, 1024 })
                {
                    var boundary = Enumerable.Repeat('b', length).ToArray();
                    try
                    {
                        Check(store.Save("boundary", boundary).Ok, "Valid password boundary was refused.");
                        using var resolved = resolver.Resolve("boundary");
                    }
                    finally { Array.Clear(boundary); }
                }
                // Presence intentionally does not certify strict reader usability.
                calls.Put("malformed", [0]);
                Check(store.Inspect("malformed") is { Ok: true, State: "present" } &&
                    Refused(() => { using var secret = resolver.Resolve("malformed"); }, "certificate_secret_blob_invalid"),
                    "Presence inspection claimed or altered password usability.");

                var beforeWrites = calls.Writes;
                calls.OnWrite = () => current = peer with { Elevated = true };
                var changed = store.Save("changed", password);
                Check(changed is { Ok: false, State: "unknown", ActionIssued: true, OutcomeUnknown: true,
                    Error: "certificate_secret_owner_denied" } && calls.Writes == beforeWrites + 1,
                    "Identity change after write lost uncertainty or retried.");
                calls.OnWrite = null; current = peer;
                Check(store.Inspect("changed").Ok, "Explicit later inspection could not observe a changed-write target.");
                calls.FailWrite = true;
                beforeWrites = calls.Writes;
                Check(store.Save("native-failure", password) is { Ok: false, State: "unknown", ActionIssued: true, OutcomeUnknown: true } &&
                    calls.Writes == beforeWrites + 1, "Failed native write was misreported or retried.");
                calls.FailWrite = false;
                calls.ThrowAfterWrite = true;
                Check(store.Save("thrown-write", password) is { Ok: false, ActionIssued: true, OutcomeUnknown: true },
                    "Lost post-write return was reported as clean.");
                calls.ThrowAfterWrite = false;
                calls.ThrowWriteCode = "injected_private_looking_value";
                beforeWrites = calls.Writes;
                var foreignCode = store.Save("foreign-error", password);
                Check(foreignCode is { Ok: false, ActionIssued: true, OutcomeUnknown: true,
                        Error: "certificate_secret_unavailable" } && calls.Writes == beforeWrites + 1 &&
                    !JsonSerializer.Serialize(foreignCode).Contains("injected_private_looking_value", StringComparison.Ordinal),
                    "Foreign exception code escaped metadata or caused a replay.");
                calls.ThrowWriteCode = null;
                calls.FailRead = true;
                Check(store.Save("read-failure", password) is { Ok: false, ActionIssued: true, OutcomeUnknown: true },
                    "Failed readback certified a write.");
                calls.FailRead = false;
                calls.OnRead = () => calls.Put("mismatch", [1, 0]);
                Check(store.Save("mismatch", password) is { Ok: false, OutcomeUnknown: true, Error: "certificate_secret_write_unobserved" },
                    "Mismatched readback certified the written value.");
                calls.OnRead = null;
                calls.ThrowOwnedFree = true;
                Check(store.Save("write-cleanup", password) is { Ok: false, ActionIssued: true, OutcomeUnknown: true },
                    "Native write-buffer cleanup failure was hidden.");
                calls.ThrowOwnedFree = false;
                calls.ThrowReadFree = true;
                Check(store.Save("read-cleanup", password) is { Ok: false, ActionIssued: true, OutcomeUnknown: true,
                    Error: "certificate_secret_cleanup_failed" }, "Read-buffer cleanup failure was hidden.");
                calls.ThrowReadFree = false;
                calls.AfterRead = () => current = peer with { Session = 8 };
                Check(!store.Inspect("consumer").Ok, "Identity change during inspection disclosed status.");
                calls.AfterRead = null; current = peer;

                var beforeDeletes = calls.Deletes;
                Check(store.Remove("consumer") is { Ok: true, State: "removed", ActionIssued: true, OutcomeUnknown: false } &&
                    calls.Deletes == beforeDeletes + 1 && store.Inspect("consumer").State == "missing" &&
                    Refused(() => { using var secret = resolver.Resolve("consumer"); }, "certificate_secret_not_found"),
                    "Remove did not affect the exact original reader target.");
                Check(store.Remove("consumer") is { Ok: true, State: "absent", OutcomeUnknown: false },
                    "Already-absent deletion was not observed truthfully.");
                Check(store.Save("remove-change", password).Ok, "Delete fixture setup failed.");
                calls.OnDelete = () => current = peer with { Sid = "S-1-5-21-100-200-300-1002" };
                Check(store.Remove("remove-change") is { Ok: false, State: "unknown", ActionIssued: true, OutcomeUnknown: true },
                    "Identity change after delete lost uncertainty.");
                calls.OnDelete = null; current = peer;
                Check(store.Save("remove-recreated", password).Ok, "Recreation fixture setup failed.");
                calls.OnRead = () => calls.Put("remove-recreated", [1, 0]);
                Check(store.Remove("remove-recreated") is { Ok: false, OutcomeUnknown: true },
                    "Concurrent recreation was reported as absent.");
                calls.OnRead = null;
                Check(calls.AllReadBlobsCleared && calls.AllWriteBlobsCleared && calls.PendingAllocations == 0 &&
                    calls.ReadFrees > 0 && calls.WriteFrees == calls.Writes,
                    "Native buffers were not cleared before release.");
            }
            finally { Array.Clear(password); }

        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, fixture = "owner_certificate_secret_store",
                assertions, type = error.GetType().Name,
                code = error is CertificateOperationException certificate ? certificate.Code : "fixture_failed" }));
            return 1;
        }
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, fixture = "owner_certificate_secret_store", assertions,
            evidence = "injected native credential calls and owned memory; real in-memory PKCS12 consumer",
            osCredentialCalls = "none", files = "none", database = "none", ui = "not tested" }));
        return 0;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
    }

    private static HashSet<string> ResultFields(OwnerCertificateSecretResult result)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        return json.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    }
    private static bool Refused(Action action, string code)
    {
        try { action(); return false; }
        catch (CertificateOperationException error) { return error.Code == code && error.InnerException is null; }
    }

    [SupportedOSPlatform("windows")]
    private sealed class FakeCalls : IOwnerCertificateSecretNativeCalls, IDisposable
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<nint, (nint Blob, int Length)> _allocations = [];
        internal int Reads, Writes, Deletes, ReadFrees, WriteFrees;
        internal bool AllReadBlobsCleared = true, AllWriteBlobsCleared = true;
        internal bool FailWrite, FailRead, ThrowAfterWrite, ThrowReadFree, ThrowOwnedFree;
        internal Action? OnWrite, OnRead, AfterRead, OnDelete;
        internal string? LastTarget, ThrowWriteCode;
        internal uint LastType, LastPersist, LastFlags;
        internal int Count => _values.Count;
        internal int PendingAllocations => _allocations.Count;
        public int LastError { get; private set; }
        public bool Read(string fixedTarget, out nint allocation)
        {
            Reads++; LastTarget = fixedTarget; OnRead?.Invoke();
            allocation = 0;
            if (FailRead) { LastError = 5; return false; }
            if (!_values.TryGetValue(fixedTarget, out var value)) { LastError = 1168; return false; }
            var blob = Marshal.AllocHGlobal(value.Length);
            allocation = Marshal.AllocHGlobal(Marshal.SizeOf<OwnerCertificateCredential>());
            Marshal.Copy(value, 0, blob, value.Length);
            Marshal.StructureToPtr(new OwnerCertificateCredential { Type = 1, Blob = blob,
                BlobBytes = checked((uint)value.Length) }, allocation, false);
            _allocations.Add(allocation, (blob, value.Length));
            LastError = 0; AfterRead?.Invoke(); return true;
        }
        public bool Write(ref OwnerCertificateCredential credential, uint flags)
        {
            Writes++;
            LastTarget = Marshal.PtrToStringUni(credential.TargetName);
            LastType = credential.Type; LastPersist = credential.Persist; LastFlags = flags;
            if (ThrowWriteCode is { } code) throw new CertificateOperationException(code);
            if (credential.Flags != 0 || credential.Comment != 0 || credential.UserName != 0 ||
                credential.AttributeCount != 0 || credential.Attributes != 0 || credential.TargetAlias != 0)
                throw new InvalidOperationException("Unexpected native credential field.");
            if (FailWrite) { LastError = 5; return false; }
            var bytes = new byte[checked((int)credential.BlobBytes)];
            Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
            if (_values.Remove(LastTarget!, out var old)) CryptographicOperations.ZeroMemory(old);
            _values.Add(LastTarget!, bytes);
            LastError = 0; OnWrite?.Invoke();
            if (ThrowAfterWrite) throw new InvalidOperationException("injected_write_return_failure");
            return true;
        }
        public bool Delete(string fixedTarget, uint type, uint flags)
        {
            Deletes++; LastTarget = fixedTarget;
            if (type != 1 || flags != 0) throw new InvalidOperationException("Unexpected delete selector.");
            if (!_values.Remove(fixedTarget, out var value)) { LastError = 1168; return false; }
            CryptographicOperations.ZeroMemory(value);
            LastError = 0; OnDelete?.Invoke(); return true;
        }
        public void Free(nint allocation)
        {
            var value = _allocations[allocation]; _allocations.Remove(allocation);
            for (var index = 0; index < value.Length; index++)
                AllReadBlobsCleared &= Marshal.ReadByte(value.Blob, index) == 0;
            ReadFrees++;
            Marshal.FreeHGlobal(value.Blob); Marshal.FreeHGlobal(allocation);
            if (ThrowReadFree) throw new InvalidOperationException("injected_read_release_failure");
        }
        public void FreeOwnedBuffer(nint allocation, int bytes)
        {
            for (var index = 0; index < bytes; index++)
                AllWriteBlobsCleared &= Marshal.ReadByte(allocation, index) == 0;
            WriteFrees++; Marshal.FreeHGlobal(allocation);
            if (ThrowOwnedFree) throw new InvalidOperationException("injected_write_release_failure");
        }
        internal void Put(string reference, byte[] bytes)
        {
            var target = OwnerCertificateSecretTarget.For(reference);
            if (_values.Remove(target, out var old)) CryptographicOperations.ZeroMemory(old);
            _values.Add(target, (byte[])bytes.Clone());
            CryptographicOperations.ZeroMemory(bytes);
        }
        public void Dispose()
        {
            foreach (var value in _values.Values) CryptographicOperations.ZeroMemory(value);
            _values.Clear();
            foreach (var allocation in _allocations)
            {
                for (var index = 0; index < allocation.Value.Length; index++) Marshal.WriteByte(allocation.Value.Blob, index, 0);
                Marshal.FreeHGlobal(allocation.Value.Blob); Marshal.FreeHGlobal(allocation.Key);
            }
            _allocations.Clear();
        }
    }

    private sealed class FixtureKeys(X509Certificate2 certificate) : ICertificateKeyResolver
    {
        public X509Certificate2 Resolve(CertificateKeyReference reference)
        {
            if (CertificateOperations.Inspect(certificate).CertificateId != reference.ExpectedCertificateId)
                throw new CertificateOperationException("fixture_key_mismatch");
            using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
            using var rsa = certificate.GetRSAPrivateKey() ?? throw new CertificateOperationException("fixture_key_missing");
            return publicOnly.CopyWithPrivateKey(rsa);
        }
    }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
