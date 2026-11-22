using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Persistence.File.V2;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Mcp.Local.Offline;

public sealed record CortexPrivateReadCacheRequest(string ContentRoot, string TaskId, string ProjectId,
    string WriteNamespace, IReadOnlyList<string> ReadNamespaces);
public sealed record CortexPrivateReadCacheRootIdentity(string CanonicalPath, string VolumeId,
    string FileId, string OwnerSid);
public sealed record CortexPrivateReadCacheOwnerIdentity(string OwnerSid, string CallerId);
public sealed record CortexPrivateReadCacheBinding(string Schema, CortexPrivateReadCacheRequest Request,
    CortexPrivateReadCacheOwnerIdentity Owner, CortexPrivateReadCacheRootIdentity Root,
    string NamespaceBindingSha256, string BindingGeneration, string BindingSha256);
public sealed record CortexPrivateReadCacheSnapshot(string Schema, string State, bool Authoritative,
    string? CoreStoreIdentity, bool LoadedRevisionProof, string BindingGeneration, string BindingSha256,
    string Generation, long Sequence, DateTimeOffset ObservedUtc, string Content, bool Truncated)
{
    public string? PreviousGeneration { get; init; }
}
public sealed record CortexPrivateReadCacheResult(string State, string BindingSha256,
    string BindingGeneration, string? SnapshotGeneration, CortexPrivateReadCacheSnapshot? Snapshot);

public sealed class CortexPrivateReadCacheException : Exception
{
    public CortexPrivateReadCacheException(string code, bool isSecurityError, Exception? inner = null)
        : base(code, inner) { Code = code; IsSecurityError = isSecurityError; }
    public string Code { get; }
    public bool IsSecurityError { get; }
}

/// <summary>
/// Owner-private passive read snapshots. Content-root identity and physical cache
/// storage are separate. This class has no journal, import, receipt or replay path.
/// The default context is the actual owner-local CLI; injected identity readers
/// are trusted host composition/fixture seams and are never selected by argv.
/// </summary>
public sealed class CortexPrivateReadCache
{
    public const string BindingSchema = "pegasus.cortex.private-read-cache-binding/1";
    public const string SnapshotSchema = "pegasus.cortex.private-read-cache-snapshot/1";
    public const int MaximumSnapshotUtf8Bytes = 4096;
    private const int MaximumBindingUtf8Bytes = 32 * 1024;
    private static readonly string EmptySha = new('0', 64);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16
    };
    private readonly string _privateParent;
    private readonly TimeProvider _clock;
    private readonly Func<string, CortexPrivateReadCacheRootIdentity> _rootIdentity;
    private readonly Func<CortexPrivateReadCacheOwnerIdentity> _ownerIdentity;

    public CortexPrivateReadCache(string privateParentDirectory, TimeProvider? clock = null,
        Func<string, CortexPrivateReadCacheRootIdentity>? rootIdentityReader = null,
        Func<CortexPrivateReadCacheOwnerIdentity>? ownerIdentityReader = null)
    {
        _privateParent = CanonicalPath(privateParentDirectory);
        _clock = clock ?? TimeProvider.System;
        _rootIdentity = rootIdentityReader ?? ReadWindowsRootIdentity;
        _ownerIdentity = ownerIdentityReader ?? CurrentOwner;
    }

    public async ValueTask<CortexPrivateReadCacheBinding> BindAsync(CortexPrivateReadCacheRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Freeze the caller's list before any asynchronous shared-file read.
        var frozen = FreezeRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        var owner = RequireOwner(_ownerIdentity());
        ValidatePrivateParentIfPresent(owner.OwnerSid, CortexUserStateAccess.Read);
        var first = RequireRoot(_rootIdentity(frozen.ContentRoot));
        Require(string.Equals(first.CanonicalPath, frozen.ContentRoot, StringComparison.OrdinalIgnoreCase),
            "cache_content_path_changed");
        frozen = frozen with { ContentRoot = first.CanonicalPath };
        await CompareSharedBindingAsync(frozen, cancellationToken).ConfigureAwait(false);
        var second = RequireRoot(_rootIdentity(frozen.ContentRoot));
        Require(first == second, "cache_content_identity_changed");
        var namespaces = Hash("namespace-binding/1", new
        { frozen.ProjectId, frozen.WriteNamespace, frozen.ReadNamespaces });
        var generation = Hash("private-cache-binding-generation/1", new
        { Owner = owner, Root = first, frozen.TaskId, NamespaceBindingSha256 = namespaces });
        var binding = new CortexPrivateReadCacheBinding(BindingSchema, frozen, owner, first,
            namespaces, generation, EmptySha);
        binding = binding with { BindingSha256 = BindingHash(binding) };
        _ = Paths(binding); // Validate the complete path budget before any creation.
        return binding;
    }

    public async ValueTask<CortexPrivateReadCacheResult> InitAsync(CortexPrivateReadCacheBinding binding,
        CancellationToken cancellationToken = default)
    {
        binding = FreezeBinding(binding);
        await RequireCurrentBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        RequireExistingPrivateParent(binding.Owner.OwnerSid);
        var paths = Paths(binding);
        CreatePrivateDirectory(paths.Base, binding.Owner.OwnerSid);
        CreatePrivateDirectory(paths.Scope, binding.Owner.OwnerSid);
        ValidateLeaseEntry(paths, binding.Owner.OwnerSid);
        await using var lease = await AcquireLeaseAsync(paths, binding.Owner.OwnerSid, cancellationToken).ConfigureAwait(false);
        await RequireCurrentBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        ValidatePrivateScope(paths, binding.Owner.OwnerSid);
        if (File.Exists(paths.Binding))
            return await ReadStateAsync(binding, paths, cancellationToken).ConfigureAwait(false);
        RequireUnoccupiedUnboundScope(paths);
        await WriteAtomicAsync(paths.Binding, Serialize(binding), createOnly: true, binding.Owner.OwnerSid,
            cancellationToken).ConfigureAwait(false);
        return Result("initialized", binding, null);
    }

    public ValueTask<CortexPrivateReadCacheResult> StatusAsync(CortexPrivateReadCacheBinding binding,
        CancellationToken cancellationToken = default) => ReadPublicAsync(binding, cancellationToken);

    public ValueTask<CortexPrivateReadCacheResult> ReadAsync(CortexPrivateReadCacheBinding binding,
        CancellationToken cancellationToken = default) => ReadPublicAsync(binding, cancellationToken);

    public async ValueTask<CortexPrivateReadCacheResult> RefreshAsync(CortexPrivateReadCacheBinding binding,
        string? expectedSnapshotGeneration, Func<CancellationToken, ValueTask<string>> freshReader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(freshReader);
        binding = FreezeBinding(binding);
        Require(expectedSnapshotGeneration is null || IsSha(expectedSnapshotGeneration), "cache_generation_invalid");
        await RequireCurrentBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        var paths = Paths(binding);
        var before = await ReadStateAsync(binding, paths, cancellationToken).ConfigureAwait(false);
        Require(before.State != "missing", "cache_not_initialized");
        Require(before.SnapshotGeneration == expectedSnapshotGeneration, "cache_generation_conflict");

        // Refuse a provably unwritable destination before requesting fresh
        // content. These observations do not replace the later race rechecks.
        ValidatePrivateScope(paths, binding.Owner.OwnerSid);
        ValidateLeaseEntry(paths, binding.Owner.OwnerSid);
        RejectReparseAncestry(paths.Hot);
        Require(!Directory.Exists(paths.Hot), "cache_private_file_invalid");
        if (File.Exists(paths.Hot)) RequirePrivateEntry(paths.Hot, false, binding.Owner.OwnerSid);

        // There is deliberately no cursor argument or cached-source shortcut.
        var content = await freshReader(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (content is null || content.Length > MaximumSnapshotUtf8Bytes ||
                Utf8.GetByteCount(content) > MaximumSnapshotUtf8Bytes)
                throw new CortexPrivateReadCacheException("cache_source_preview_invalid", false);
        }
        catch (EncoderFallbackException error)
        { throw new CortexPrivateReadCacheException("cache_source_preview_invalid", false, error); }
        await RequireCurrentBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        ValidatePrivateScope(paths, binding.Owner.OwnerSid);
        ValidateLeaseEntry(paths, binding.Owner.OwnerSid);
        await using var lease = await AcquireLeaseAsync(paths, binding.Owner.OwnerSid, cancellationToken).ConfigureAwait(false);
        await RequireCurrentBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        var retained = await ReadStateAsync(binding, paths, cancellationToken).ConfigureAwait(false);
        Require(retained.State != "missing" && retained.SnapshotGeneration == expectedSnapshotGeneration,
            "cache_generation_conflict");
        var snapshot = CreateSnapshot(binding, retained.Snapshot, content);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteAtomicAsync(paths.Hot, Serialize(snapshot), createOnly: false, binding.Owner.OwnerSid,
            cancellationToken).ConfigureAwait(false);
        return Result("snapshot", binding, snapshot);
    }

    private async ValueTask<CortexPrivateReadCacheResult> ReadPublicAsync(CortexPrivateReadCacheBinding binding,
        CancellationToken token)
    {
        binding = FreezeBinding(binding);
        await RequireCurrentBindingAsync(binding, token).ConfigureAwait(false);
        var paths = Paths(binding);
        ValidatePrivateScope(paths, binding.Owner.OwnerSid, CortexUserStateAccess.Read);
        var busy = IsLeaseBusy(paths, binding.Owner.OwnerSid);
        if (busy && !File.Exists(paths.Binding)) return Result("busy", binding, null);
        var result = await ReadStateAsync(binding, paths, token).ConfigureAwait(false);
        return busy ? result with { State = "busy" } : result;
    }

    private async ValueTask RequireCurrentBindingAsync(CortexPrivateReadCacheBinding binding, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(binding);
        Require(binding.Schema == BindingSchema && IsSha(binding.BindingSha256) &&
            BindingHash(binding) == binding.BindingSha256, "cache_binding_invalid");
        var current = await BindAsync(binding.Request, token).ConfigureAwait(false);
        Require(current.BindingSha256 == binding.BindingSha256, "cache_binding_identity_mismatch");
    }

    private async ValueTask<CortexPrivateReadCacheResult> ReadStateAsync(CortexPrivateReadCacheBinding binding,
        CachePaths paths, CancellationToken token)
    {
        if (!Directory.Exists(_privateParent)) return Result("missing", binding, null);
        ValidatePrivateScope(paths, binding.Owner.OwnerSid, CortexUserStateAccess.Read);
        if (!Directory.Exists(paths.Scope)) return Result("missing", binding, null);
        if (!File.Exists(paths.Binding))
        {
            Require(!Directory.Exists(paths.Binding) && !File.Exists(paths.Hot) && !Directory.Exists(paths.Hot),
                "cache_partial_binding_conflict");
            RequireUnoccupiedUnboundScope(paths);
            return Result("missing", binding, null);
        }
        var stored = Deserialize<CortexPrivateReadCacheBinding>(
            await ReadExactAsync(paths.Binding, MaximumBindingUtf8Bytes, binding.Owner.OwnerSid, token).ConfigureAwait(false));
        Require(stored.Schema == BindingSchema && BindingHash(stored) == stored.BindingSha256 &&
            stored.BindingSha256 == binding.BindingSha256, "cache_binding_identity_mismatch");
        if (!File.Exists(paths.Hot))
        {
            Require(!Directory.Exists(paths.Hot), "cache_snapshot_invalid");
            return Result("initialized", binding, null);
        }
        var snapshot = Deserialize<CortexPrivateReadCacheSnapshot>(
            await ReadExactAsync(paths.Hot, MaximumSnapshotUtf8Bytes, binding.Owner.OwnerSid, token).ConfigureAwait(false));
        Require(snapshot.Schema == SnapshotSchema && snapshot.State == "passive-stale" &&
            !snapshot.Authoritative && snapshot.CoreStoreIdentity is null && !snapshot.LoadedRevisionProof &&
            snapshot.BindingGeneration == binding.BindingGeneration && snapshot.BindingSha256 == binding.BindingSha256 &&
            snapshot.Sequence > 0 && snapshot.ObservedUtc.Offset == TimeSpan.Zero && snapshot.Content is not null &&
            (snapshot.Sequence == 1 ? snapshot.PreviousGeneration is null : IsSha(snapshot.PreviousGeneration)) &&
            IsSha(snapshot.Generation) && SnapshotHash(snapshot) == snapshot.Generation,
            "cache_snapshot_invalid");
        return Result("snapshot", binding, snapshot);
    }

    private CortexPrivateReadCacheSnapshot CreateSnapshot(CortexPrivateReadCacheBinding binding,
        CortexPrivateReadCacheSnapshot? previous, string content)
    {
        var snapshot = new CortexPrivateReadCacheSnapshot(SnapshotSchema, "passive-stale", false, null, false,
            binding.BindingGeneration, binding.BindingSha256, EmptySha,
            checked((previous?.Sequence ?? 0) + 1), _clock.GetUtcNow().ToUniversalTime(), content, false)
        { PreviousGeneration = previous?.Generation };
        if (Serialize(snapshot).Length > MaximumSnapshotUtf8Bytes)
        {
            var boundaries = new List<int> { 0 };
            var position = 0;
            foreach (var rune in content.EnumerateRunes()) { position += rune.Utf16SequenceLength; boundaries.Add(position); }
            var low = 0;
            var high = boundaries.Count - 1;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                var candidate = snapshot with { Content = content[..boundaries[middle]], Truncated = true };
                if (Serialize(candidate).Length <= MaximumSnapshotUtf8Bytes) low = middle;
                else high = middle - 1;
            }
            snapshot = snapshot with { Content = content[..boundaries[low]], Truncated = true };
        }
        snapshot = snapshot with { Generation = SnapshotHash(snapshot) };
        Require(Serialize(snapshot).Length <= MaximumSnapshotUtf8Bytes, "cache_snapshot_bound");
        return snapshot;
    }

    private static CortexPrivateReadCacheRequest FreezeRequest(CortexPrivateReadCacheRequest request)
    {
        Require(request.ReadNamespaces is not null, "cache_binding_invalid");
        Require(request.ReadNamespaces.Count is > 0 and <= 16, "cache_binding_invalid");
        var reads = request.ReadNamespaces.ToArray();
        Require(IsIdentifier(request.TaskId, 128) && request.TaskId == request.TaskId.ToLowerInvariant(),
            "cache_task_identity_invalid");
        _ = new CortexFileProjectManifestV2(CortexFileProjectManifestV2.CurrentSchema,
            request.ProjectId, request.WriteNamespace, Array.AsReadOnly(reads)).Validate();
        return request with { ContentRoot = CanonicalPath(request.ContentRoot), ReadNamespaces = Array.AsReadOnly(reads) };
    }

    private static CortexPrivateReadCacheBinding FreezeBinding(CortexPrivateReadCacheBinding binding)
    {
        Require(binding is not null && binding.Request is not null, "cache_binding_invalid");
        // Public records can be reconstructed by a trusted host with a mutable
        // IReadOnlyList implementation. Snapshot it before crossing any await.
        return binding with { Request = FreezeRequest(binding.Request) };
    }

    private static async ValueTask CompareSharedBindingAsync(CortexPrivateReadCacheRequest request, CancellationToken token)
    {
        var path = Path.Combine(request.ContentRoot, ".cortex", "project.json");
        RejectReparseAncestry(path);
        Require(!Directory.Exists(path), "cache_shared_binding_invalid");
        if (!File.Exists(path)) return; // Explicit owner request, never absent-file self-authorization.
        var shared = await CortexFileProjectManifestV2.ReadAsync(request.ContentRoot, token).ConfigureAwait(false);
        Require(shared.ProjectId == request.ProjectId && shared.WriteNamespace == request.WriteNamespace &&
            shared.ReadNamespaces.SequenceEqual(request.ReadNamespaces, StringComparer.Ordinal),
            "cache_shared_binding_mismatch");
    }

    private CachePaths Paths(CortexPrivateReadCacheBinding binding)
    {
        // Do not put FID/namespaces into this key: a replacement/remap must collide
        // with and fail the immutable binding, not silently select an empty cache.
        var key = Hash("private-cache-path/1", new
        {
            binding.Owner, Root = binding.Root.CanonicalPath.ToUpperInvariant(), binding.Request.TaskId
        })[..32];
        var root = CanonicalPath(Path.Combine(_privateParent, "rc"));
        var scope = CanonicalPath(Path.Combine(root, "t-" + key));
        return new(root, scope, CanonicalPath(Path.Combine(scope, "binding.v1.json")),
            CanonicalPath(Path.Combine(scope, "HOT.json")), CanonicalPath(Path.Combine(scope, ".lock")));
    }

    private void ValidatePrivateParentIfPresent(string owner,
        CortexUserStateAccess requiredAccess = CortexUserStateAccess.Modify)
    {
        RejectReparseAncestry(_privateParent);
        Require(!File.Exists(_privateParent), "cache_private_parent_invalid");
        if (Directory.Exists(_privateParent)) RequirePrivateEntry(_privateParent, true, owner, requiredAccess);
    }

    private void RequireExistingPrivateParent(string owner)
    {
        ValidatePrivateParentIfPresent(owner);
        if (!Directory.Exists(_privateParent)) throw new CortexPrivateReadCacheException("cache_unavailable", false);
    }

    private void ValidatePrivateScope(CachePaths paths, string owner,
        CortexUserStateAccess requiredAccess = CortexUserStateAccess.Modify)
    {
        ValidatePrivateParentIfPresent(owner, requiredAccess);
        foreach (var path in new[] { paths.Base, paths.Scope })
        {
            RejectReparseAncestry(path);
            Require(!File.Exists(path), "cache_private_directory_invalid");
            if (Directory.Exists(path)) RequirePrivateEntry(path, true, owner, requiredAccess);
        }
    }

    private static void ValidateLeaseEntry(CachePaths paths, string owner,
        CortexUserStateAccess requiredAccess = CortexUserStateAccess.Modify)
    {
        RejectReparseAncestry(paths.Lock);
        Require(!Directory.Exists(paths.Lock), "cache_private_file_invalid");
        if (File.Exists(paths.Lock))
        {
            RequirePrivateEntry(paths.Lock, false, owner, requiredAccess);
            Require(new FileInfo(paths.Lock).Length == 0, "cache_lock_invalid");
        }
    }

    private static void RequireUnoccupiedUnboundScope(CachePaths paths)
    {
        // This is a bounded names-only examination of this exact scope, never
        // sibling task discovery. An orphan or unknown file requires review.
        foreach (var entry in Directory.EnumerateFileSystemEntries(paths.Scope))
            Require(string.Equals(entry, paths.Lock, StringComparison.Ordinal), "cache_unbound_scope_occupied");
    }

    private static void CreatePrivateDirectory(string path, string owner)
    {
        var parent = Path.GetDirectoryName(path)!;
        RequirePrivateEntry(parent, true, owner);
        RejectReparseAncestry(path);
        Require(!File.Exists(path), "cache_private_directory_invalid");
        if (!Directory.Exists(path))
        {
            if (!OperatingSystem.IsWindows()) throw Security("cache_platform_unsupported");
            var sid = new SecurityIdentifier(owner);
            var security = CortexUserStateDirectories.CreateWindowsUserDirectorySecurity(sid);
            try { new DirectoryInfo(path).Create(security); }
            catch (UnauthorizedAccessException error)
            { throw new CortexPrivateReadCacheException("cache_private_access_rejected", true, error); }
            catch (IOException error)
            { throw new CortexPrivateReadCacheException("cache_unavailable", false, error); }
        }
        RequirePrivateEntry(path, true, owner);
    }

    private static void RequirePrivateEntry(string path, bool directory, string owner,
        CortexUserStateAccess requiredAccess = CortexUserStateAccess.Modify)
    {
        if (!OperatingSystem.IsWindows()) throw Security("cache_platform_unsupported");
        RejectReparseAncestry(path);
        try
        {
            FileSystemSecurity security = directory
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            try
            {
                // A safe inherited descriptor is admitted as-is. Scope entry
                // points separately validate the private parent/base/scope;
                // walking above that boundary would reject browseable containers.
                CortexUserStateDirectories.RequireWindowsUserStateAccess(security, new SecurityIdentifier(owner), requiredAccess);
            }
            catch (UnauthorizedAccessException error)
            { throw new CortexPrivateReadCacheException("cache_private_acl_invalid", true, error); }
        }
        catch (CortexPrivateReadCacheException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw new CortexPrivateReadCacheException("cache_private_acl_unverifiable", true, error); }
    }

    private static async ValueTask<byte[]> ReadExactAsync(string path, int limit, string owner, CancellationToken token)
    {
        RequirePrivateEntry(path, false, owner, CortexUserStateAccess.Read);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            Require(stream.Length is > 0 && stream.Length <= limit, "cache_invalid_data");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            _ = Utf8.GetString(bytes);
            return bytes;
        }
        catch (CortexPrivateReadCacheException) { throw; }
        catch (DecoderFallbackException error) { throw new CortexPrivateReadCacheException("cache_invalid_data", true, error); }
        catch (UnauthorizedAccessException error)
        { throw new CortexPrivateReadCacheException("cache_private_access_rejected", true, error); }
        catch (IOException error)
        { throw new CortexPrivateReadCacheException("cache_unavailable", false, error); }
    }

    private static async ValueTask WriteAtomicAsync(string target, byte[] bytes, bool createOnly, string owner,
        CancellationToken token)
    {
        var parent = Path.GetDirectoryName(target)!;
        RequirePrivateEntry(parent, true, owner);
        RejectReparseAncestry(target);
        Require(!Directory.Exists(target), "cache_private_file_invalid");
        if (File.Exists(target)) RequirePrivateEntry(target, false, owner);
        var temporary = CanonicalPath(Path.Combine(parent, ".tmp-" + Guid.NewGuid().ToString("N")));
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
            }
            CortexLocalRegistryAclPolicy.ProtectFile(temporary);
            RequirePrivateEntry(temporary, false, owner);
            RejectReparseAncestry(target);
            if (File.Exists(target)) RequirePrivateEntry(target, false, owner);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: !createOnly);
        }
        catch (CortexPrivateReadCacheException) { throw; }
        catch (UnauthorizedAccessException error)
        { throw new CortexPrivateReadCacheException("cache_private_access_rejected", true, error); }
        catch (IOException error)
        { throw new CortexPrivateReadCacheException("cache_unavailable", false, error); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async ValueTask<FileStream> AcquireLeaseAsync(CachePaths paths, string owner, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), _clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                ValidatePrivateScope(paths, owner);
                ValidateLeaseEntry(paths, owner);
                try
                {
                    // The known zero-length lock remains in the private scope.
                    // Open the entry itself, never a reparse target.
                    var handle = OpenCacheControl(paths.Lock, 0xc0000000, 0, 4, 0x40200080);
                    try { return new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true); }
                    catch { handle.Dispose(); throw; }
                }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
                { await Task.Delay(10, timeout.Token).ConfigureAwait(false); }
                catch (UnauthorizedAccessException error)
                { throw new CortexPrivateReadCacheException("cache_private_access_rejected", true, error); }
                catch (IOException error)
                { throw new CortexPrivateReadCacheException("cache_unavailable", false, error); }
            }
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new CortexPrivateReadCacheException("cache_busy_timeout", false, error); }
    }

    private static bool IsLeaseBusy(CachePaths paths, string owner)
    {
        ValidateLeaseEntry(paths, owner, CortexUserStateAccess.Read);
        if (!File.Exists(paths.Lock)) return false;
        try { using var handle = OpenCacheControl(paths.Lock, 0x80000000, 7, 3, 0x00200080); return false; }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return true; }
        catch (FileNotFoundException) { return false; }
    }

    private static SafeFileHandle OpenCacheControl(string path, uint access, uint share, uint creation, uint flags)
    {
        var handle = CreateFileW(path, access, share, IntPtr.Zero, creation, flags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is 32 or 33) throw new IOException("cache_busy", unchecked((int)(0x80070000u | (uint)error)));
            if (error is 2 or 3) throw new FileNotFoundException("cache_control_missing");
            if (error == 5) throw new CortexPrivateReadCacheException("cache_private_access_rejected", true, new Win32Exception(error));
            throw new CortexPrivateReadCacheException("cache_unavailable", false, new Win32Exception(error));
        }
        try
        {
            Require(GetFileAttributeTagInformation(handle, 9, out var attributes, 8) &&
                (attributes.FileAttributes & (uint)FileAttributes.ReparsePoint) == 0, "cache_reparse_rejected");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static CortexPrivateReadCacheOwnerIdentity RequireOwner(CortexPrivateReadCacheOwnerIdentity owner)
    {
        Require(owner is not null && IsIdentifier(owner.CallerId, 128) && !string.IsNullOrEmpty(owner.OwnerSid),
            "cache_owner_identity_invalid");
        if (!OperatingSystem.IsWindows()) throw Security("cache_platform_unsupported");
        using var current = WindowsIdentity.GetCurrent();
        Require(current.User is not null && current.User.Value == owner.OwnerSid && !current.IsSystem &&
            !current.User.IsWellKnown(WellKnownSidType.LocalServiceSid) &&
            !current.User.IsWellKnown(WellKnownSidType.NetworkServiceSid), "cache_owner_identity_mismatch");
        return owner;
    }

    private static CortexPrivateReadCacheOwnerIdentity CurrentOwner()
    {
        if (!OperatingSystem.IsWindows()) throw Security("cache_platform_unsupported");
        using var identity = WindowsIdentity.GetCurrent();
        return new(identity.User?.Value ?? throw Security("cache_owner_identity_invalid"), "owner-local-cli");
    }

    private static CortexPrivateReadCacheRootIdentity RequireRoot(CortexPrivateReadCacheRootIdentity root)
    {
        Require(root is not null && IsHex(root.VolumeId, 16) && IsHex(root.FileId, 32) &&
            !string.IsNullOrEmpty(root.OwnerSid), "cache_content_identity_invalid");
        var canonical = CanonicalPath(root.CanonicalPath);
        Require(Directory.Exists(canonical), "cache_content_identity_invalid");
        RejectReparseAncestry(canonical);
        return root with { CanonicalPath = canonical };
    }

    private static CortexPrivateReadCacheRootIdentity ReadWindowsRootIdentity(string path)
    {
        if (!OperatingSystem.IsWindows()) throw Security("cache_platform_unsupported");
        path = CanonicalPath(path);
        Require(Directory.Exists(path), "cache_content_identity_invalid");
        RejectReparseAncestry(path);
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 18, out var information, 24))
            throw new CortexPrivateReadCacheException("cache_content_identity_unverifiable", true,
                new Win32Exception(Marshal.GetLastWin32Error()));
        var final = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 0);
        Require(length > 0 && length < final.Capacity, "cache_content_identity_unverifiable");
        var canonical = final.ToString();
        Require(canonical.StartsWith("\\\\?\\", StringComparison.Ordinal) &&
            !canonical.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase), "cache_content_identity_invalid");
        canonical = CanonicalPath(canonical[4..]);
        Require(string.Equals(canonical, path, StringComparison.OrdinalIgnoreCase), "cache_content_path_changed");
        RejectReparseAncestry(path);
        RejectReparseAncestry(canonical);
        var owner = new DirectoryInfo(canonical).GetAccessControl(AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return new(canonical, information.VolumeSerialNumber.ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
            information.FileIdLow.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) +
            information.FileIdHigh.ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
            owner?.Value ?? throw Security("cache_content_identity_unverifiable"));
    }

    private static string CanonicalPath(string path)
    {
        Require(!string.IsNullOrWhiteSpace(path) && !path.Any(char.IsControl) && Path.IsPathFullyQualified(path) &&
            !path.StartsWith("\\\\", StringComparison.Ordinal) && !path.StartsWith("//", StringComparison.Ordinal),
            "cache_path_invalid");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Require(full.Length <= 240 && full != Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), "cache_path_invalid");
        var relative = full[Path.GetPathRoot(full)!.Length..];
        Require(relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).All(segment =>
            segment.Length > 0 && segment is not ("." or "..") && !segment.Contains(':') &&
            !segment.EndsWith('.') && !segment.EndsWith(' ') && Utf8.GetByteCount(segment) <= 255), "cache_path_invalid");
        return full;
    }

    private static void RejectReparseAncestry(string path)
    {
        for (var candidate = path; !string.IsNullOrEmpty(candidate); candidate = Path.GetDirectoryName(candidate))
        {
            try
            {
                Require((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) == 0, "cache_reparse_rejected");
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                // Exists follows a link and can hide a dangling reparse entry.
                Require(new FileInfo(candidate).LinkTarget is null && new DirectoryInfo(candidate).LinkTarget is null,
                    "cache_reparse_rejected");
            }
        }
    }

    private static T Deserialize<T>(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicates(document.RootElement);
            return document.RootElement.Deserialize<T>(Json) ?? throw Security("cache_invalid_data");
        }
        catch (JsonException error) { throw new CortexPrivateReadCacheException("cache_invalid_data", true, error); }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { Require(names.Add(property.Name), "cache_invalid_data"); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    private static string Hash<T>(string domain, T value)
    {
        using var document = JsonDocument.Parse(Serialize(value));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Utf8.GetBytes(domain + "\n"));
        hash.AppendData(CortexCanonicalJson.Serialize(document.RootElement));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static string BindingHash(CortexPrivateReadCacheBinding binding) =>
        Hash("private-cache-binding-manifest/1", binding with { BindingSha256 = EmptySha });
    private static string SnapshotHash(CortexPrivateReadCacheSnapshot snapshot) =>
        Hash("private-cache-snapshot/1", snapshot with { Generation = EmptySha });
    private static bool IsIdentifier(string? text, int maximum) => text is { Length: > 0 } && text.Length <= maximum &&
        char.IsAsciiLetterOrDigit(text[0]) && text.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    private static bool IsHex(string? text, int length) => text?.Length == length && text.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsSha(string? text) => IsHex(text, 64);
    private static CortexPrivateReadCacheException Security(string code) => new(code, true);
    private static void Require([DoesNotReturnIf(false)] bool condition, string code) { if (!condition) throw Security(code); }
    private static CortexPrivateReadCacheResult Result(string state, CortexPrivateReadCacheBinding binding,
        CortexPrivateReadCacheSnapshot? snapshot) => new(state, binding.BindingSha256, binding.BindingGeneration,
            snapshot?.Generation, snapshot);
    private sealed record CachePaths(string Base, string Scope, string Binding, string Hot, string Lock);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation { public ulong VolumeSerialNumber; public ulong FileIdLow; public ulong FileIdHigh; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation { public uint FileAttributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr attributes,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileIdInformation information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileAttributeTagInformation(SafeFileHandle handle, int informationClass,
        out FileAttributeTagInformation information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
