using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Engine;

internal static class CertificateSecretTests
{
    private const string OwnerSid = "S-1-5-21-1-2-3-1001";
    private enum Stage { Validation, Identity, Decode, NativeError, Cleanup, Complete }

    internal static Task<int> RunAsync()
    {
        if (OperatingSystem.IsWindows()) return RunWindowsAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { ok = false, available = false, code = "windows_required", evidence = "unavailable is not a pass" }));
        return Task.FromResult(77);
    }

    [SupportedOSPlatform("windows")]
    private static Task<int> RunWindowsAsync()
    {
        var assertions = 0;
        var stage = Stage.Validation;
        string? assertion = null;
        void Check(bool value, string label) { assertions++; if (!value) { assertion = label; throw new InvalidOperationException("certificate_secret_assertion_failed"); } }
        try
        {
            var peer = new LocalPeer(OwnerSid, 7, Elevated: false);
            using (var reader = new FakeReader(Blob(['x'])))
            {
                var resolver = new OwnerCertificateSecrets(OwnerSid, () => peer, reader);
                foreach (var invalid in new[] { "", "A", ".a", "../x", "a/b", "a:b", "a b", new string('a', 65) })
                    Check(Refused(() => { using var secret = resolver.Resolve(invalid); }, "certificate_secret_reference_invalid"), "invalid reference refused");
                Check(reader.Reads == 0 && reader.Frees == 0, "invalid references never reach reader");
            }
            stage = Stage.Identity;
            foreach (var denied in new[] { peer with { Sid = "S-1-5-21-1-2-3-1002" }, peer with { Elevated = true }, peer with { Session = 0 } })
            {
                using var reader = new FakeReader(Blob(['x']));
                var current = peer;
                var resolver = new OwnerCertificateSecrets(OwnerSid, () => current, reader);
                current = denied;
                Check(Refused(() => { using var secret = resolver.Resolve("password-ref"); }, "certificate_secret_owner_denied") && reader.Reads == 0,
                    "changed owner elevation or session refused before native read");
            }
            using (var reader = new FakeReader(Blob(['x'])))
            {
                var current = peer;
                var resolver = new OwnerCertificateSecrets(OwnerSid, () => current, reader);
                reader.AfterRead = () => current = peer with { Session = 8 };
                Check(Refused(() => { using var secret = resolver.Resolve("password-ref"); }, "certificate_secret_owner_denied") &&
                    reader.Reads == 1 && reader.Frees == 1 && reader.ClearedBeforeFree,
                    "identity change during read refuses secret and clears returned allocation");
            }
            using (var reader = new FakeReader(Blob(['x'])))
            {
                var failSample = false;
                var resolver = new OwnerCertificateSecrets(OwnerSid, () => failSample ? throw new InvalidOperationException("sampler_unavailable") : peer, reader);
                failSample = true;
                Check(Refused(() => { using var secret = resolver.Resolve("password-ref"); }, "certificate_secret_owner_denied") && reader.Reads == 0,
                    "identity observation failure never reaches native read");
            }
            stage = Stage.Decode;
            foreach (var valid in new[] { Blob(['x']), Blob(['p', '\u00e4', '\ud83d', '\ude00']), RepeatedBlob(1024) })
            {
                using var reader = new FakeReader(valid);
                var resolver = new OwnerCertificateSecrets(OwnerSid, () => peer, reader);
                using var secret = resolver.Resolve("password-ref");
                Check(secret.ToString() == "<protected certificate secret>" && reader.Reads == 1 && reader.Frees == 1 && reader.ClearedBeforeFree,
                    "valid strict UTF16LE returns an owned redacted lease and clears native bytes first");
                Check(reader.Target == "Pegasus.Cortex.Certificate.password-ref", "only fixed prefix and exact lowercase reference reach native read");
                var rejectedSerialization = false;
                string? serialized = null;
                try { serialized = JsonSerializer.Serialize(secret); }
                catch (Exception error) when (error is NotSupportedException or InvalidOperationException) { rejectedSerialization = true; }
                Check(rejectedSerialization || serialized == "{}", "secret contents cannot enter ordinary JSON diagnostics");
            }
            foreach (var invalid in new[] { Array.Empty<byte>(), new byte[] { 0x41 }, new byte[] { 0x41, 0, 0x42 },
                Blob(['x', '\0']), new byte[] { 0, 0xd8 }, new byte[] { 0, 0xdc }, RepeatedBlob(1025), RepeatedBlob(1280) })
            {
                using var reader = new FakeReader(invalid);
                var resolver = new OwnerCertificateSecrets(OwnerSid, () => peer, reader);
                Check(Refused(() => { using var secret = resolver.Resolve("password-ref"); }, "certificate_secret_blob_invalid") &&
                    reader.Reads == 1 && reader.Frees == 1 && reader.ClearedBeforeFree,
                    "empty odd NUL malformed surrogate and oversized native blobs refuse with cleanup");
            }
            using (var reader = new FakeReader(Blob(['x'])) { Type = 2 })
            {
                var resolver = new OwnerCertificateSecrets(OwnerSid, () => peer, reader);
                Check(Refused(() => { using var secret = resolver.Resolve("password-ref"); }, "certificate_secret_blob_invalid") && reader.ClearedBeforeFree,
                    "non-Generic credential refused without leaving native bytes");
            }
            using (var reader = new FakeReader(Blob(['x'])) { ReturnNullBlob = true })
            {
                var resolver = new OwnerCertificateSecrets(OwnerSid, () => peer, reader);
                Check(Refused(() => { using var secret = resolver.Resolve("password-ref"); }, "certificate_secret_blob_invalid") && reader.Frees == 1,
                    "null blob with positive length refused and allocation freed");
            }
            stage = Stage.NativeError;
            foreach (var errorCode in new[] { 1168, 5 })
            {
                using var reader = new FakeReader(Blob(['x'])) { Error = errorCode };
                var resolver = new OwnerCertificateSecrets(OwnerSid, () => peer, reader);
                Check(Refused(() => { using var secret = resolver.Resolve("password-ref"); },
                    errorCode == 1168 ? "certificate_secret_not_found" : "certificate_secret_unavailable") && reader.ClearedBeforeFree && reader.Frees == 1,
                    "native errors expose only fixed codes and clean any returned owned buffer");
            }
            using (var reader = new FakeReader(Blob(['x'])) { ThrowRead = true })
            {
                var resolver = new OwnerCertificateSecrets(OwnerSid, () => peer, reader);
                Check(Refused(() => { using var secret = resolver.Resolve("password-ref"); }, "certificate_secret_unavailable"),
                    "reader failure does not expose its diagnostic body");
            }
            stage = Stage.Cleanup;
            using (var reader = new FakeReader(Blob(['x'])) { ThrowFree = true })
            {
                var resolver = new OwnerCertificateSecrets(OwnerSid, () => peer, reader);
                Check(Refused(() => { using var secret = resolver.Resolve("password-ref"); }, "certificate_secret_cleanup_failed") &&
                    reader.ClearedBeforeFree && reader.Frees == 1, "cleanup failure never hands out a secret lease");
            }
            stage = Stage.Complete;
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, assertions, stage = stage.ToString(),
                evidence = "injected identity and allocated native-buffer fixtures only; no CredRead, credential store, key, file or OS mutation calls" }));
            return Task.FromResult(0);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, assertions, stage = stage.ToString(), assertion,
                failureType = error.GetType().Name, outerHResult = error.HResult,
                code = error is CertificateOperationException certificate ? certificate.Code : "certificate_secret_fixture_failed" }));
            return Task.FromResult(1);
        }
    }

    private static bool Refused(Action action, string code)
    {
        try { action(); return false; }
        catch (CertificateOperationException error) { return error.Code == code && error.Message == code && error.InnerException is null; }
    }
    private static byte[] Blob(char[] characters)
    {
        try { return Encoding.Unicode.GetBytes(characters); }
        finally { Array.Clear(characters); }
    }
    private static byte[] RepeatedBlob(int count) => Blob(Enumerable.Repeat('x', count).ToArray());

    private sealed class FakeReader : IOwnerCertificateSecretReader, IDisposable
    {
        private readonly nint _allocation, _blob;
        private readonly int _length;
        private bool _freed;
        internal int Reads, Frees;
        internal bool ClearedBeforeFree;
        internal string? Target;
        internal uint Type = 1;
        internal int Error;
        internal bool ReturnNullBlob, ThrowRead, ThrowFree;
        internal Action? AfterRead;
        internal FakeReader(byte[] ownedBytes)
        {
            _allocation = Marshal.AllocHGlobal(1);
            _length = ownedBytes.Length;
            _blob = _length == 0 ? 0 : Marshal.AllocHGlobal(_length);
            try { if (_length != 0) Marshal.Copy(ownedBytes, 0, _blob, _length); }
            finally { CryptographicOperations.ZeroMemory(ownedBytes); }
        }
        public OwnerCertificateSecretRead Read(string fixedTarget)
        {
            Reads++; Target = fixedTarget;
            if (ThrowRead) throw new InvalidOperationException("injected_reader_failure");
            AfterRead?.Invoke();
            return new(_allocation, Type, ReturnNullBlob ? 0 : _blob, checked((uint)_length), Error);
        }
        public void Free(nint allocation)
        {
            if (allocation != _allocation || _freed) throw new InvalidOperationException("invalid_fixture_release");
            Frees++;
            ClearedBeforeFree = true;
            for (var index = 0; index < _length; index++) ClearedBeforeFree &= Marshal.ReadByte(_blob, index) == 0;
            // A malformed seam may withhold its blob pointer. The fixture still
            // clears every allocation it owns, after recording resolver behavior.
            for (var index = 0; index < _length; index++) Marshal.WriteByte(_blob, index, 0);
            Release();
            if (ThrowFree) throw new InvalidOperationException("injected_release_failure");
        }
        public void Dispose()
        {
            if (_freed) return;
            for (var index = 0; index < _length; index++) Marshal.WriteByte(_blob, index, 0);
            Release();
        }
        private void Release()
        {
            if (_blob != 0) Marshal.FreeHGlobal(_blob);
            Marshal.FreeHGlobal(_allocation);
            _freed = true;
        }
    }
}
