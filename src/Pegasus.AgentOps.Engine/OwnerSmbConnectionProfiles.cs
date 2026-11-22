using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Engine;

/// <summary>
/// One current nonsecret document per profile, including compact tombstones.
/// Private per-profile lock files serialize cooperating store writers; expected
/// revision is optimistic concurrency under that lock, not OS CAS against an
/// arbitrary same-owner editor. Atomic publication never restores a mapping.
/// Construction/Read/List do not create directories, locks or repair ACLs.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class OwnerSmbConnectionProfiles
{
    private const int MaximumDocumentBytes = 8192, MaximumListedProfiles = 512;
    private const string Prefix = "smb-", Suffix = ".v1.json";
    private readonly string _root, _ownerSid;
    private readonly SecurityIdentifier _user;
    private readonly OwnerHostIdentity _owner;
    private readonly WindowsPathContextResult _rootContext;
    private readonly object _gate = new();
    private sealed record Document(int Version, string OwnerSid, string Id, long Revision, bool Deleted, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] SmbConnectionProfile? Profile);

    internal OwnerSmbConnectionProfiles(string root, string ownerSid) : this(root, ownerSid, LocalIdentity.Current) { }
    internal OwnerSmbConnectionProfiles(string root, string ownerSid, Func<LocalPeer> current)
    {
        _user = new SecurityIdentifier(ownerSid);
        if (_user.Value != ownerSid) throw Error("smb_owner_denied");
        _ownerSid = ownerSid; _owner = new(ownerSid, current);
        RequireOwner();
        _rootContext = new WindowsPathContextNormalizer().Normalize(root);
        _root = Path.TrimEndingDirectorySeparator(_rootContext.CanonicalPath);
        if (_rootContext.Kind != "local" || _root.Length + 43 > 240 ||
            !StringComparer.OrdinalIgnoreCase.Equals(_root, Path.TrimEndingDirectorySeparator(root)))
            throw Error("smb_profile_path_invalid");
        PathNormalizer.RejectAbsoluteReparseComponents(_root);
        if (File.Exists(_root)) throw Error("smb_profile_path_invalid");
        _ = RequireRoot(false);
    }

    internal SmbProfileStoreResult Read(string id)
    {
        lock (_gate)
        {
            try
            {
                SmbConnectionProfile.RequireReference(id); RequireOwner();
                if (!RequireRoot(false)) return new(true, "missing");
                using var lease = ReadDocument(EntryPath(id), id, allowMissing: true);
                RequireOwner();
                return Result(lease);
            }
            catch (Exception error) when (Recoverable(error)) { return Failed(error, false); }
        }
    }

    internal SmbProfileListResult List(string? afterId = null, int limit = 32)
    {
        lock (_gate)
        {
            try
            {
                if (afterId is not null) SmbConnectionProfile.RequireReference(afterId);
                if (limit is < 1 or > 64) throw Error("smb_profile_invalid");
                RequireOwner();
                if (!RequireRoot(false)) return new(true, "ready", []);
                var ids = new List<string>();
                foreach (var path in Directory.EnumerateFileSystemEntries(_root, Prefix + "*" + Suffix))
                {
                    if (ids.Count == MaximumListedProfiles) throw Error("smb_profile_listing_bound");
                    var name = Path.GetFileName(path);
                    var id = name[Prefix.Length..^Suffix.Length];
                    SmbConnectionProfile.RequireReference(id);
                    if (name != Prefix + id + Suffix) throw Error("smb_profile_invalid");
                    ids.Add(id);
                }
                ids.Sort(StringComparer.Ordinal);
                var selected = ids.Where(id => afterId is null || StringComparer.Ordinal.Compare(id, afterId) > 0).Take(limit + 1).ToArray();
                var rows = new List<SmbConnectionProfileSnapshot>();
                foreach (var id in selected.Take(limit))
                {
                    using var lease = ReadDocument(EntryPath(id), id, allowMissing: false);
                    if (!lease!.Value.Deleted) rows.Add(Snapshot(lease));
                }
                RequireRoot(false); RequireOwner();
                // Cursor advances over tombstones too. No claim of a point-in-time
                // multi-profile snapshot or completeness above the explicit bound.
                return new(true, "ready", rows, selected.Length > limit ? selected[limit - 1] : null);
            }
            catch (Exception error) when (Recoverable(error)) { return new(false, "unavailable", [], Error: Code(error)); }
        }
    }

    internal SmbProfileStoreResult Save(SmbConnectionProfile profile, long expectedRevision, CancellationToken token = default)
    {
        try { SmbConnectionProfile.Validate(profile); return Change(profile.Id, profile, expectedRevision, token); }
        catch (Exception error) when (Recoverable(error)) { return Failed(error, false); }
    }
    internal SmbProfileStoreResult Remove(string id, long expectedRevision, CancellationToken token = default) =>
        Change(id, null, expectedRevision, token);

    private SmbProfileStoreResult Change(string id, SmbConnectionProfile? profile, long expectedRevision, CancellationToken token)
    {
        lock (_gate)
        {
            var issued = false;
            FileStream? staging = null;
            CertificateFileIdentity? stagingIdentity = null;
            try
            {
                SmbConnectionProfile.RequireReference(id);
                if (expectedRevision < 0 || (profile is null && expectedRevision == 0)) throw Error("smb_profile_revision_conflict");
                RequireOwner(); token.ThrowIfCancellationRequested();
                RequireRoot(true);
                using var heldLock = AcquireLock(id);
                token.ThrowIfCancellationRequested();
                var target = EntryPath(id);
                using var before = ReadDocument(target, id, allowMissing: true);
                var revision = before?.Value.Revision ?? 0;
                if (revision != expectedRevision) throw Error("smb_profile_revision_conflict");
                before?.RequireWritable(_user);
                if (profile is null && before is null) throw Error("smb_profile_revision_conflict");
                var next = checked(revision + 1);
                var document = new Document(1, _ownerSid, id, next, profile is null, profile);
                var encoded = JsonSerializer.SerializeToUtf8Bytes(document, SmbConnectionProfile.Json);
                if (encoded.Length > MaximumDocumentBytes) throw Error("smb_profile_bound");
                var temporary = Path.Combine(_root, ".smb-" + Guid.NewGuid().ToString("N") + ".tmp");
                staging = new FileInfo(temporary).Create(FileMode.CreateNew,
                    FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize,
                    FileShare.Read, 4096, FileOptions.WriteThrough, CortexUserStateDirectories.CreateWindowsUserFileSecurity(_user));
                stagingIdentity = new(staging.SafeFileHandle, temporary);
                staging.Write(encoded); staging.Flush(true);
                stagingIdentity.RequireAt(temporary);
                CortexUserStateDirectories.RequireWindowsUserStateAccess(staging.GetAccessControl(), _user, CortexUserStateAccess.Modify);
                var verify = new byte[encoded.Length];
                staging.Position = 0; staging.ReadExactly(verify);
                if (!verify.AsSpan().SequenceEqual(encoded) || staging.Length != encoded.Length) throw Error("smb_profile_changed");
                RequireRoot(true); heldLock.RequireIdentity(); before?.RequireIdentity();
                PathNormalizer.RejectAbsoluteReparseComponents(target);
                if (before is null && (File.Exists(target) || Directory.Exists(target))) throw Error("smb_profile_revision_conflict");
                RequireOwner(); token.ThrowIfCancellationRequested();
                // Legacy FileRenameInfo cannot replace an open destination.
                // Exact preimage checks are complete; close it while retaining
                // the cooperative lock. This is deliberately not OS atomic CAS.
                before?.Dispose();
                // ActionIssued refers to publication, not temporary/lock creation.
                issued = true;
                stagingIdentity.PublishAs(target, replaceExisting: before is not null);
                staging.Dispose(); staging = null;
                using var observed = ReadDocument(target, id, allowMissing: false);
                if (!observed!.Bytes.AsSpan().SequenceEqual(encoded)) throw Error("smb_profile_changed");
                RequireOwner(); // Late cancellation cannot erase an observed publication.
                return Result(observed) with { State = profile is null ? "removed" : "saved", ActionIssued = true };
            }
            catch (Exception error) when (Recoverable(error)) { return Failed(error, issued); }
            finally
            {
                if (!issued && stagingIdentity is not null)
                {
                    try { stagingIdentity.DeleteOnClose(); }
                    catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { /* Retain only the unproven owned temporary. */ }
                }
                try { staging?.Dispose(); }
                catch (Exception cleanup) when (Recoverable(cleanup)) { /* Preserve the already classified failure; never retry publication. */ }
            }
        }
    }

    private FileLease AcquireLock(string id)
    {
        var path = Path.Combine(_root, ".smb-" + id + ".lock");
        PathNormalizer.RejectAbsoluteReparseComponents(path);
        FileStream? created = null;
        try
        {
            // Create-only with a private descriptor; no truncating pathname open.
            created = new FileInfo(path).Create(FileMode.CreateNew,
                FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize,
                FileShare.None, 4096, FileOptions.None, CortexUserStateDirectories.CreateWindowsUserFileSecurity(_user));
        }
        catch (IOException) when (File.Exists(path)) { }
        return new FileLease(path, _user, writable: true, maximum: 0, created: created);
    }

    private DocumentLease? ReadDocument(string path, string id, bool allowMissing)
    {
        RequireOwner(); PathNormalizer.RejectAbsoluteReparseComponents(path);
        FileLease lease;
        try { lease = new FileLease(path, _user, writable: false, MaximumDocumentBytes); }
        catch (FileNotFoundException) when (allowMissing) { return null; }
        try
        {
            if (lease.Stream.Length < 1) throw Error("smb_profile_invalid");
            var bytes = new byte[checked((int)lease.Stream.Length)];
            lease.Stream.ReadExactly(bytes); lease.RequireIdentity();
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 6 });
            var value = json.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw Error("smb_profile_invalid");
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            string[] fields = ["version", "ownerSid", "id", "revision", "deleted", "profile"];
            foreach (var property in value.EnumerateObject())
                if (!fields.Contains(property.Name, StringComparer.Ordinal) || !values.TryAdd(property.Name, property.Value)) throw Error("smb_profile_invalid");
            if (values.Count != fields.Length || !values["version"].TryGetInt32(out var version) || version != 1 ||
                values["ownerSid"].GetString() != _ownerSid || values["id"].GetString() != id ||
                !values["revision"].TryGetInt64(out var revision) || revision < 1 ||
                values["deleted"].ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Error("smb_profile_invalid");
            var deleted = values["deleted"].GetBoolean();
            var profile = deleted ? null : SmbConnectionProfile.Parse(values["profile"]);
            if (deleted ? values["profile"].ValueKind != JsonValueKind.Null : profile!.Id != id) throw Error("smb_profile_invalid");
            lease.RequireIdentity(); RequireOwner();
            return new(lease, new(1, _ownerSid, id, revision, deleted, profile), bytes);
        }
        catch { lease.Dispose(); throw; }
    }

    private bool RequireRoot(bool create)
    {
        RequireOwner(); WindowsPathContextNormalizer.RevalidateLocalNoFollow(_rootContext);
        FileAttributes? attributes = null;
        try { attributes = File.GetAttributes(_root); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        if (attributes.HasValue && (attributes.Value & FileAttributes.Directory) == 0) throw Error("smb_profile_path_invalid");
        if (!attributes.HasValue)
        {
            if (!create) return false; // Only an actual not-found observation.
            CortexUserStateDirectories.EnsureCurrentUserDirectory(_root);
        }
        WindowsPathContextNormalizer.RevalidateLocalNoFollow(_rootContext);
        CortexUserStateDirectories.RequireWindowsUserStateAccess(new DirectoryInfo(_root).GetAccessControl(), _user,
            create ? CortexUserStateAccess.Modify : CortexUserStateAccess.Read);
        RequireOwner(); return true;
    }
    private string EntryPath(string id)
    {
        var path = Path.Combine(_root, Prefix + id + Suffix);
        if (path.Length > 240) throw Error("smb_profile_path_invalid");
        return path;
    }
    private void RequireOwner() { if (!_owner.IsCurrentOwner()) throw Error("smb_owner_denied"); }
    private static SmbConnectionProfileSnapshot Snapshot(DocumentLease lease) =>
        new(lease.Value.Profile!, lease.Value.Revision, Convert.ToHexStringLower(SHA256.HashData(lease.Bytes)));
    private static SmbProfileStoreResult Result(DocumentLease? lease) => lease is null ? new(true, "missing") :
        new(true, lease.Value.Deleted ? "removed" : "present", Snapshot: lease.Value.Deleted ? null : Snapshot(lease), Revision: lease.Value.Revision);
    private static bool Recoverable(Exception error) => error is not OutOfMemoryException and not StackOverflowException;
    private static SmbConnectionException Error(string code) => new(code);
    private static string Code(Exception error)
    {
        var code = error is OperationCanceledException ? "smb_cancelled" : error is SmbConnectionException smb ? smb.Code : "smb_profile_unavailable";
        return code is "smb_profile_invalid" or "smb_profile_path_invalid" or "smb_profile_revision_conflict" or "smb_profile_bound" or
            "smb_profile_listing_bound" or "smb_profile_changed" or "smb_profile_missing" or "smb_owner_denied" or "smb_cancelled"
            ? code : "smb_profile_unavailable";
    }
    private static SmbProfileStoreResult Failed(Exception error, bool issued) =>
        new(false, issued ? "unknown" : "failed", Code(error), ActionIssued: issued, OutcomeUnknown: issued,
            NativeError: error is Win32Exception native ? native.NativeErrorCode :
                error is IOException { InnerException: Win32Exception inner } ? inner.NativeErrorCode : null);

    private sealed class DocumentLease(FileLease file, Document value, byte[] bytes) : IDisposable
    {
        internal Document Value { get; } = value;
        internal byte[] Bytes { get; } = bytes;
        internal void RequireIdentity() => file.RequireIdentity();
        internal void RequireWritable(SecurityIdentifier user) => CortexUserStateDirectories.RequireWindowsUserStateAccess(file.Stream.GetAccessControl(), user, CortexUserStateAccess.Modify);
        public void Dispose() => file.Dispose();
    }
    private sealed class FileLease : IDisposable
    {
        private readonly string _path;
        private readonly CertificateFileIdentity _identity;
        internal FileStream Stream { get; }
        internal FileLease(string path, SecurityIdentifier user, bool writable, int maximum, FileStream? created = null)
        {
            _path = path;
            var stream = created;
            SafeFileHandle? handle = null;
            try
            {
                PathNormalizer.RejectAbsoluteReparseComponents(path);
                if (stream is null)
                {
                    handle = CreateFileW(path, writable ? 0xC0000000u : 0x80000000u, writable ? 0u : 5u, 0, 3, 0x00200000u, 0);
                    if (handle.IsInvalid)
                    {
                        var code = Marshal.GetLastWin32Error();
                        if (code == 2) throw new FileNotFoundException("smb_profile_missing");
                        throw new IOException("smb_profile_open_failed", new Win32Exception(code));
                    }
                    stream = new FileStream(handle, writable ? FileAccess.ReadWrite : FileAccess.Read);
                }
                _identity = new CertificateFileIdentity(stream.SafeFileHandle, path);
                if (stream.Length < 0 || stream.Length > maximum) throw Error("smb_profile_bound");
                CortexUserStateDirectories.RequireWindowsUserStateAccess(stream.GetAccessControl(), user,
                    writable ? CortexUserStateAccess.Modify : CortexUserStateAccess.Read);
                Stream = stream; RequireIdentity();
            }
            catch { stream?.Dispose(); handle?.Dispose(); throw; }
        }
        internal void RequireIdentity() => _identity.RequireAt(_path);
        public void Dispose() => Stream.Dispose();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
}
