using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Engine;

internal static class WindowsSmbConnectionTests
{
    internal static Task<int> RunAsync() => Task.FromResult(!OperatingSystem.IsWindows() ? 77 : RunWindows());

    [SupportedOSPlatform("windows")]
    private static int RunWindows()
    {
        var root = Path.Combine(Path.GetTempPath(), "cortex-smbw-" + Guid.NewGuid().ToString("N"));
        var assertions = 0;
        var ok = false;
        try
        {
            using var windows = WindowsIdentity.GetCurrent();
            var sid = windows.User!.Value;
            var peer = new LocalPeer(sid, 7);
            var current = peer;
            var logon = new WindowsSmbLogonIdentity(42, 3);
            var originalLogon = logon;
            using var calls = new FakeCredentials();
            var certificateNative = new OwnerCertificateSecretNative(calls);
            var smbNative = new OwnerCertificateSecretNative(OwnerPasswordPurpose.SmbPassword, calls);
            var certificateStore = new OwnerCertificateSecretStore(sid, () => current, certificateNative);
            var passwords = new OwnerSmbPasswordReferences(sid, () => current, smbNative);
            var spy = new PasswordSpy(passwords);
            var native = new FakeWNet();
            var provider = new WindowsSmbConnections(sid, () => current, () => logon, spy, native);
            var profiles = new OwnerSmbConnectionProfiles(Path.Combine(root, "profiles"), sid, () => current);
            char[] certificatePassword = ['c', 'e', 'r', 't', '-', 'o', 'n', 'l', 'y'];
            char[] smbPassword = ['s', 'm', 'b', '-', '\u03A9', '-', 'o', 'n', 'l', 'y'];
            try
            {
                Check(certificateStore.Save("shared", certificatePassword).Ok &&
                    passwords.Save("shared", smbPassword).Ok && calls.Count == 2,
                    "Closed purposes shared or overwrote a credential target.");
                Check(calls.LastTarget == "Pegasus.Cortex.Smb.shared" && calls.AllWriteBuffersCleared && calls.AllReadBuffersCleared,
                    "SMB purpose did not use the fixed target/cleared native buffers.");
                using (var certificate = new OwnerCertificateSecrets(sid, () => current, certificateNative).Resolve("shared"))
                    Check(certificate.ToString() == "<protected certificate secret>", "Existing certificate reader API changed.");
                var reads = calls.Reads;
                try { _ = smbNative.Read("Pegasus.Cortex.Certificate.shared"); throw new InvalidOperationException("Wrong purpose reached native."); }
                catch (CertificateOperationException) { Check(calls.Reads == reads, "Wrong purpose performed a native read."); }

                var saved = profiles.Save(new(1, "explicit", "Build share", @"\\server\share", "X:", "explicitRemote", @"DOMAIN\builder", "shared"), 0);
                Check(saved.Ok, "Actual owned profile was not saved.");
                var profile = profiles.Read("explicit").Snapshot!.Profile;
                native.ExpectedPassword = smbPassword;
                var connected = provider.Connect(profile);
                Check(connected is { Ok: true, State: "connected", ActionIssued: true, OutcomeUnknown: false, MappingObserved: true } &&
                    native.Connects == 1 && native.LastProfile == profile && native.PasswordMatched &&
                    calls.LastTarget == "Pegasus.Cortex.Smb.shared",
                    "Saved profile did not reach the actual provider argument/credential path.");
                Check(spy.LastDisposed && calls.PendingAllocations == 0 && calls.AllReadBuffersCleared && calls.AllWriteBuffersCleared,
                    "Completed mapping retained a password lease/native blob.");
                reads = calls.Reads;
                Check(provider.Connect(profile) is { Ok: false, ActionIssued: false, Error: "smb_drive_occupied" } &&
                    native.Connects == 1 && calls.Reads == reads, "Matching occupied map was adopted/replayed or resolved another password.");
                Check(provider.Inspect(profile) is { Ok: true, State: "mapped", MappingObserved: true }, "Exact mapped observation was lost.");
                Check(provider.Disconnect(profile, profile.RemoteShare) is { Ok: true, State: "disconnected", ActionIssued: true } &&
                    native.Disconnects == 1 && native.LastDisconnected == "X:", "Disconnect did not address only the exact local drive.");
                Check(provider.Disconnect(profile, profile.RemoteShare) is { Ok: false, ActionIssued: false, Error: "smb_mapping_changed" } &&
                    native.Disconnects == 1, "Absent map disconnect was replayed.");

                var currentUser = profile with { Id = "current", IdentityMode = "currentUser", RemoteUser = null, PasswordReference = null };
                reads = calls.Reads;
                Check(provider.Connect(currentUser).Ok && calls.Reads == reads && native.LastProfile!.RemoteUser is null &&
                    native.LastPasswordWasEmpty && spy.LastDisposed, "currentUser silently used an explicit remote credential.");
                native.Reset();
                foreach (var occupied in new SmbDriveObservation[] { new(true), new(true, true), new(true, false, @"\\other\share") })
                {
                    native.Observation = occupied;
                    var before = native.Connects;
                    reads = calls.Reads;
                    Check(provider.Connect(profile) is { Ok: false, ActionIssued: false, Error: "smb_drive_occupied" } &&
                        native.Connects == before && calls.Reads == reads, "Occupied/remembered drive resolved a password or dispatched.");
                }
                native.Reset();
                Check(certificateStore.Save("certificate-only", certificatePassword).Ok, "Purpose-isolation fixture provisioning failed.");
                var beforeConnect = native.Connects;
                Check(provider.Connect(profile with { PasswordReference = "certificate-only" }) is
                    { Ok: false, ActionIssued: false, Error: "smb_password_not_found" } && native.Connects == beforeConnect &&
                    calls.LastTarget == "Pegasus.Cortex.Smb.certificate-only", "Missing SMB purpose fell back to certificate/default credentials.");

                foreach (var denied in new[] { peer with { Elevated = true }, peer with { Session = 0 }, peer with { Session = 8 },
                    peer with { Sid = "S-1-5-21-100-200-300-9999" } })
                {
                    current = denied; reads = calls.Reads;
                    var inspections = native.Inspections;
                    Check(provider.Connect(profile) is { Ok: false, Error: "smb_owner_denied", ActionIssued: false } &&
                        calls.Reads == reads && native.Inspections == inspections, "Changed owner/session reached WNet/credentials.");
                }
                current = peer;
                logon = new(43, 3);
                Check(provider.Connect(profile) is { Ok: false, Error: "smb_owner_denied", ActionIssued: false },
                    "Same SID/session but different logon LUID was admitted.");
                logon = originalLogon;
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel(); reads = calls.Reads;
                    beforeConnect = native.Connects;
                    Check(provider.Connect(profile, cancelled.Token) is { Ok: false, State: "cancelled", ActionIssued: false, OutcomeUnknown: false } &&
                        calls.Reads == reads && native.Connects == beforeConnect, "Pre-entry cancellation dispatched credentials/WNet.");
                }
                using (var cancelled = new CancellationTokenSource())
                {
                    spy.AfterResolve = () => cancelled.Cancel(); beforeConnect = native.Connects;
                    Check(provider.Connect(profile, cancelled.Token) is { Ok: false, ActionIssued: false, OutcomeUnknown: false } &&
                        native.Connects == beforeConnect && spy.LastDisposed, "Cancellation during resolution leaked/dispatched.");
                    spy.AfterResolve = null;
                }
                spy.AfterResolve = () => logon = new(44, 3); beforeConnect = native.Connects;
                Check(provider.Connect(profile) is { Ok: false, Error: "smb_owner_denied", ActionIssued: false } &&
                    native.Connects == beforeConnect && spy.LastDisposed, "LUID change at native entry was admitted.");
                spy.AfterResolve = null; logon = originalLogon;

                using (var cancelled = new CancellationTokenSource())
                {
                    native.DuringConnect = () => cancelled.Cancel();
                    var outcome = provider.Connect(profile, cancelled.Token);
                    Check(cancelled.IsCancellationRequested && outcome is { Ok: true, ActionIssued: true, OutcomeUnknown: false } &&
                        spy.LastDisposed, "Late cancellation erased the actual successful mapping observation.");
                }
                native.Reset();
                native.ConnectCode = 1219; // Explicit remote credential conflict; never reconnect/default fallback.
                beforeConnect = native.Connects; var disconnects = native.Disconnects;
                Check(provider.Connect(profile) is { Ok: false, State: "failed", ActionIssued: true, OutcomeUnknown: false, NativeError: 1219 } &&
                    native.Connects == beforeConnect + 1 && native.Disconnects == disconnects && spy.LastDisposed,
                    "Native conflict was retried, fell back, or disconnected another connection.");
                native.Reset();
                native.DuringConnect = () => current = peer with { Elevated = true };
                disconnects = native.Disconnects;
                var unknown = provider.Connect(profile);
                Check(unknown is { Ok: false, State: "unknown", ActionIssued: true, OutcomeUnknown: true } &&
                    native.Disconnects == disconnects && spy.LastDisposed, "Post-entry identity loss invented no effect or cleanup-disconnected.");
                current = peer; native.DuringConnect = null;
                beforeConnect = native.Connects;
                Check(provider.Inspect(profile).MappingObserved && !provider.Connect(profile).Ok && native.Connects == beforeConnect,
                    "Unknown call was automatically replayed/adopted.");
                native.Reset();
                native.ThrowAfterEntry = true;
                disconnects = native.Disconnects;
                Check(provider.Connect(profile) is { Ok: false, ActionIssued: true, OutcomeUnknown: true } &&
                    native.Disconnects == disconnects && spy.LastDisposed, "Thrown post-entry call was retried/cleaned up.");
                native.Reset();

                native.Observation = new(true, false, @"\\other\share");
                disconnects = native.Disconnects;
                Check(provider.Disconnect(profile, profile.RemoteShare) is { Ok: false, ActionIssued: false, Error: "smb_mapping_changed" } &&
                    native.Disconnects == disconnects, "Different remote mapping was disconnected.");
                native.Observation = new(true, true, profile.RemoteShare);
                Check(provider.Disconnect(profile, profile.RemoteShare) is { Ok: false, ActionIssued: false, Error: "smb_drive_occupied" } &&
                    native.Disconnects == disconnects, "Remembered map was adopted for disconnect.");
                native.Observation = new(true, false, profile.RemoteShare);
                native.DisconnectCode = 2401;
                Check(provider.Disconnect(profile, profile.RemoteShare) is
                    { Ok: false, State: "failed", ActionIssued: true, OutcomeUnknown: false, MappingObserved: true, NativeError: 2401 },
                    "Busy/open-file disconnect was forced or lost its terminal failure.");
                native.Reset();
                native.Observation = new(true, false, profile.RemoteShare);
                native.ThrowDuringDisconnect = true; disconnects = native.Disconnects;
                Check(provider.Disconnect(profile, profile.RemoteShare) is { Ok: false, ActionIssued: true, OutcomeUnknown: true } &&
                    native.Disconnects == disconnects + 1, "Unobserved disconnect was retried.");
                native.Reset();
                native.Observation = new(false, Error: 5); reads = calls.Reads; beforeConnect = native.Connects;
                Check(provider.Connect(profile) is { Ok: false, ActionIssued: false, Error: "smb_observation_unavailable" } &&
                    native.Connects == beforeConnect && calls.Reads == reads, "Uncertain occupancy was treated as free.");
                Check(passwords.Remove("shared").Ok && passwords.Inspect("shared") is { Ok: true, State: "missing" } &&
                    certificateStore.Inspect("shared") is { Ok: true, State: "present" },
                    "SMB removal touched the same-named certificate purpose.");
                Check(calls.PendingAllocations == 0 && calls.AllReadBuffersCleared && calls.AllWriteBuffersCleared,
                    "Fixture retained native password allocations.");
                ok = true;
            }
            finally { Array.Clear(certificatePassword); Array.Clear(smbPassword); }
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL SMB provider fixture: " + error.GetType().Name + ": " + error.Message); }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (Exception error) { ok = false; Console.Error.WriteLine("FAIL SMB provider fixture cleanup: " + error.GetType().Name); }
        }
        if (!ok) return 1;
        Console.WriteLine("PASS SMB provider: " + assertions + " stored-profile-to-provider, closed credential purposes, occupancy/remembered/logon, cancellation/unknown/no-auto-replay and buffer teardown assertions (fake WNet/credential provider + owned profile files; no real mapping/authentication)");
        return 0;
        void Check(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
    }

    private sealed class PasswordSpy(IOwnerSmbPasswordResolver inner) : IOwnerSmbPasswordResolver
    {
        private OwnerPasswordLease? _last;
        internal Action? AfterResolve;
        internal bool LastDisposed
        {
            get
            {
                if (_last is null) return true;
                try { _ = _last.Value.Length; return false; }
                catch (ObjectDisposedException) { return true; }
            }
        }
        public OwnerPasswordLease Resolve(string reference)
        {
            var value = inner.Resolve(reference); _last = value;
            try { AfterResolve?.Invoke(); return value; }
            catch { value.Dispose(); throw; }
        }
    }
    private sealed class FakeWNet : IWindowsSmbNative
    {
        internal SmbDriveObservation Observation = new(false);
        internal int Connects, Disconnects, Inspections, ConnectCode, DisconnectCode;
        internal bool ThrowAfterEntry, ThrowDuringDisconnect, PasswordMatched, LastPasswordWasEmpty;
        internal char[] ExpectedPassword = [];
        internal SmbConnectionProfile? LastProfile;
        internal string? LastDisconnected;
        internal Action? DuringConnect;
        public SmbDriveObservation InspectDrive(string localDrive) { Inspections++; return Observation; }
        public int Connect(SmbConnectionProfile profile, ReadOnlySpan<char> password, Action beforeEntry)
        {
            beforeEntry(); Connects++; LastProfile = profile; LastPasswordWasEmpty = password.IsEmpty;
            PasswordMatched = password.SequenceEqual(ExpectedPassword);
            if (ConnectCode == 0) Observation = new(true, false, profile.RemoteShare);
            DuringConnect?.Invoke();
            if (ThrowAfterEntry) throw new InvalidOperationException("owned_native_return_failure");
            return ConnectCode;
        }
        public int Disconnect(string localDrive, Action beforeEntry)
        {
            beforeEntry(); Disconnects++; LastDisconnected = localDrive;
            if (DisconnectCode == 0) Observation = new(false);
            if (ThrowDuringDisconnect) throw new InvalidOperationException("owned_disconnect_return_failure");
            return DisconnectCode;
        }
        internal void Reset()
        {
            Observation = new(false); ConnectCode = DisconnectCode = 0;
            ThrowAfterEntry = ThrowDuringDisconnect = false; DuringConnect = null;
        }
    }

    private sealed class FakeCredentials : IOwnerCertificateSecretNativeCalls, IDisposable
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<nint, (nint Blob, int Bytes)> _reads = new();
        internal int Reads;
        internal string? LastTarget;
        internal bool AllReadBuffersCleared = true, AllWriteBuffersCleared = true;
        internal int Count => _values.Count;
        internal int PendingAllocations => _reads.Count;
        public int LastError { get; private set; }
        public bool Read(string target, out nint allocation)
        {
            Reads++; LastTarget = target; allocation = 0;
            if (!_values.TryGetValue(target, out var bytes)) { LastError = 1168; return false; }
            var blob = Marshal.AllocHGlobal(bytes.Length);
            allocation = Marshal.AllocHGlobal(Marshal.SizeOf<OwnerCertificateCredential>());
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            Marshal.StructureToPtr(new OwnerCertificateCredential { Type = 1, Blob = blob, BlobBytes = checked((uint)bytes.Length) }, allocation, false);
            _reads.Add(allocation, (blob, bytes.Length)); LastError = 0; return true;
        }
        public bool Write(ref OwnerCertificateCredential credential, uint flags)
        {
            LastTarget = Marshal.PtrToStringUni(credential.TargetName)!;
            if (credential.Type != 1 || credential.Persist != 2 || flags != 0) throw new InvalidOperationException("Unexpected fixed credential fields.");
            var bytes = new byte[checked((int)credential.BlobBytes)];
            Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
            if (_values.Remove(LastTarget, out var old)) CryptographicOperations.ZeroMemory(old);
            _values.Add(LastTarget, bytes); LastError = 0; return true;
        }
        public bool Delete(string target, uint type, uint flags)
        {
            LastTarget = target;
            if (type != 1 || flags != 0) throw new InvalidOperationException("Unexpected fixed delete fields.");
            if (!_values.Remove(target, out var bytes)) { LastError = 1168; return false; }
            CryptographicOperations.ZeroMemory(bytes); LastError = 0; return true;
        }
        public void Free(nint allocation)
        {
            var value = _reads[allocation]; _reads.Remove(allocation);
            for (var index = 0; index < value.Bytes; index++) AllReadBuffersCleared &= Marshal.ReadByte(value.Blob, index) == 0;
            Marshal.FreeHGlobal(value.Blob); Marshal.FreeHGlobal(allocation);
        }
        public void FreeOwnedBuffer(nint allocation, int bytes)
        {
            for (var index = 0; index < bytes; index++) AllWriteBuffersCleared &= Marshal.ReadByte(allocation, index) == 0;
            Marshal.FreeHGlobal(allocation);
        }
        public void Dispose()
        {
            foreach (var bytes in _values.Values) CryptographicOperations.ZeroMemory(bytes);
            _values.Clear();
            foreach (var allocation in _reads)
            {
                for (var index = 0; index < allocation.Value.Bytes; index++) Marshal.WriteByte(allocation.Value.Blob, index, 0);
                Marshal.FreeHGlobal(allocation.Value.Blob); Marshal.FreeHGlobal(allocation.Key);
            }
            _reads.Clear();
        }
    }
}
