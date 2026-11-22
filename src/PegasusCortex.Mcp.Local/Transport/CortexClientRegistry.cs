using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Mcp.Local.Transport;

/// <summary>Suggested namespace selections, never data-access permissions.</summary>
public sealed record CortexClientDefaults(string[] ReadNamespaces, string[] WriteNamespaces, bool Pao = false);
internal sealed record CortexClientEntry(string ClientId, string KeyId, long Generation, string Application,
    string Transport, CortexClientDefaults Defaults, bool ManagedAdapter, DateTimeOffset ExpiresUtc, bool Revoked,
    byte[] Salt, byte[] Verifier);
internal sealed record CortexClientLease(string ClientId, string KeyId, long Generation);
internal sealed record CortexClientCredential(string ClientId, string KeyId, string ApiKey);

/// <summary>Owner-local application credentials; never semantic records, package inputs or job receipts.</summary>
internal sealed partial class CortexClientRegistry
{
    private sealed record Store(int Version, List<CortexClientEntry> Clients);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };
    private readonly string _directory;
    private readonly string _path;
    private readonly string _lockPath;
    private readonly LocalPeer _owner;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const int MaximumRegistryBytes = 128 * 1024;

    public CortexClientRegistry(string directory, LocalPeer owner, TimeProvider? clock = null)
    {
        _directory = CortexProductLayout.RequireCanonicalAbsoluteDirectory(directory, nameof(directory));
        _path = Path.Combine(_directory, "clients.v1.json");
        _lockPath = Path.Combine(_directory, "clients.lock");
        _owner = owner;
        _clock = clock ?? TimeProvider.System;
        CortexUserStateDirectories.EnsureCurrentUserDirectory(_directory);
    }

    // Registration authenticates a local application, not its namespace access.
    // Only the managed adapter forwards attended privileged PAO requests; their
    // authorization still belongs to the existing System broker, never this key.
    internal CortexClientCredential Register(string clientId, string application, string transport,
        CortexClientDefaults defaults, DateTimeOffset expiresUtc, bool rotate = false, bool managedAdapter = false)
    {
        ValidateIdentity(clientId, application, transport, defaults);
        if (expiresUtc <= _clock.GetUtcNow() || expiresUtc > _clock.GetUtcNow().AddYears(1))
            throw new InvalidDataException("client_expiry_invalid");
        using var held = Lock();
        var store = Read();
        var old = store.Clients.SingleOrDefault(entry => entry.ClientId == clientId);
        if (old is not null && !rotate) throw new InvalidDataException("client_exists");
        if (old is null && store.Clients.Count >= 32) throw new InvalidDataException("client_limit");
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var salt = RandomNumberGenerator.GetBytes(32);
            var keyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var verifier = Hash(salt, secret);
            var entry = new CortexClientEntry(clientId, keyId, (old?.Generation ?? 0) + 1,
                Path.GetFullPath(application), transport, defaults, managedAdapter, expiresUtc, false, salt, verifier);
            store.Clients.RemoveAll(value => value.ClientId == clientId);
            store.Clients.Add(entry);
            Write(store);
            return new(clientId, keyId, Convert.ToHexStringLower(secret));
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal CortexClientLease Authenticate(string clientId, string keyId, string apiKey, string transport, LocalPeer peer)
    {
        if (!peer.SameIdentity(_owner) || apiKey.Length != 64 || !Hex().IsMatch(apiKey))
            throw new UnauthorizedAccessException("client_auth_failed");
        using var held = Lock();
        var entry = Read().Clients.SingleOrDefault(value => value.ClientId == clientId);
        if (entry is null || !Active(entry) || entry.KeyId != keyId || entry.Transport != transport ||
            !SamePath(entry.Application, peer.ApplicationPath)) throw new UnauthorizedAccessException("client_auth_failed");
        var supplied = Convert.FromHexString(apiKey);
        try
        {
            var candidate = Hash(entry.Salt, supplied);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(candidate, entry.Verifier))
                    throw new UnauthorizedAccessException("client_auth_failed");
            }
            finally { CryptographicOperations.ZeroMemory(candidate); }
        }
        finally { CryptographicOperations.ZeroMemory(supplied); }
        return new(entry.ClientId, entry.KeyId, entry.Generation);
    }

    internal CortexClientEntry RequireActive(CortexClientLease lease)
    {
        using var held = Lock();
        var entry = Read().Clients.SingleOrDefault(value => value.ClientId == lease.ClientId);
        if (entry is null || !Active(entry) || entry.KeyId != lease.KeyId || entry.Generation != lease.Generation)
            throw new UnauthorizedAccessException("client_revoked_or_expired");
        return entry;
    }

    internal void Revoke(string clientId)
    {
        using var held = Lock();
        var store = Read();
        var index = store.Clients.FindIndex(entry => entry.ClientId == clientId);
        if (index < 0) return;
        store.Clients[index] = store.Clients[index] with { Revoked = true };
        Write(store);
    }

    internal object[] List()
    {
        using var held = Lock();
        return Read().Clients.Select(entry => (object)new { entry.ClientId, entry.KeyId, entry.Generation,
            entry.Application, entry.Transport, entry.Defaults, entry.ExpiresUtc,
            state = entry.Revoked ? "revoked" : Active(entry) ? "active" : "expired" }).ToArray();
    }

    internal CortexClientCredential ManagedCodexCredential(string application)
    {
        using var held = Lock();
        var keyFile = Path.Combine(_directory, "codex.key");
        var existing = Read().Clients.SingleOrDefault(entry => entry.ClientId == "codex");
        if (existing is not null)
        {
            // Revoked, expired, tampered or missing credentials never auto-reissue.
            if (!Active(existing) || !existing.ManagedAdapter || existing.Transport != "mcp" || !SamePath(existing.Application, application))
                throw new UnauthorizedAccessException("client_auth_failed");
            var saved = ReadProtectedCredential(keyFile);
            if (saved.ClientId != existing.ClientId || saved.KeyId != existing.KeyId)
                throw new UnauthorizedAccessException("client_auth_failed");
            return saved;
        }
        // Lock is intentionally released before Register takes the same cross-process lease.
        held.Dispose();
        if (File.Exists(keyFile)) throw new InvalidDataException("client_key_orphaned");
        var credential = Register("codex", application, "mcp", new([], [], true), _clock.GetUtcNow().AddYears(1), managedAdapter: true);
        SaveProtectedCredential(keyFile, credential);
        return credential;
    }

    internal static void SaveProtectedCredential(string path, CortexClientCredential credential, bool replace = false)
    {
        CortexProductLayout.RejectExistingReparseComponents(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(credential, Json);
        try
        {
            var protectedBytes = CortexUserSecretProtection.Protect(bytes);
            var scratch = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(protectedBytes); file.Flush(true); }
                CortexProductLayout.RejectExistingReparseComponents(path);
                File.Move(scratch, path, replace);
            }
            finally { if (File.Exists(scratch)) File.Delete(scratch); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static CortexClientCredential ReadProtectedCredential(string path)
    {
        CortexProductLayout.RejectExistingReparseComponents(path);
        if (new FileInfo(path).Length is < 1 or > 4096) throw new InvalidDataException("client_key_invalid");
        var bytes = CortexUserSecretProtection.Unprotect(File.ReadAllBytes(path));
        try { return JsonSerializer.Deserialize<CortexClientCredential>(bytes, Json) ?? throw new InvalidDataException("client_key_invalid"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private bool Active(CortexClientEntry entry) => !entry.Revoked && entry.ExpiresUtc > _clock.GetUtcNow();
    private static byte[] Hash(byte[] salt, byte[] key)
    {
        // Keys are random 256-bit values, not human passwords. A salted SHA-256
        // verifier avoids password-KDF cost on every reconnect without retaining keys.
        var bytes = new byte[salt.Length + key.Length];
        salt.CopyTo(bytes, 0); key.CopyTo(bytes, salt.Length);
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private IDisposable Lock()
    {
        CortexProductLayout.RejectExistingReparseComponents(_directory);
        CortexProductLayout.RejectExistingReparseComponents(_lockPath);
        if (!_gate.Wait(TimeSpan.FromSeconds(1))) throw new IOException("client_registry_busy");
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try { return new RegistryLock(new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), _gate); }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && started.Elapsed < TimeSpan.FromSeconds(1))
                { Thread.Sleep(10); } // Only owner enrollment from another process contends here; bounded, no spin loop.
            }
        }
        catch { _gate.Release(); throw; }
    }

    private sealed class RegistryLock(FileStream file, SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            file.Dispose(); gate.Release();
        }
    }

    private Store Read()
    {
        CortexProductLayout.RejectExistingReparseComponents(_path);
        if (!File.Exists(_path)) return new(1, []);
        if (new FileInfo(_path).Length > MaximumRegistryBytes) throw new InvalidDataException("client_registry_invalid");
        var result = JsonSerializer.Deserialize<Store>(File.ReadAllBytes(_path), Json) ?? throw new InvalidDataException("client_registry_invalid");
        if (result.Version != 1 || result.Clients.Count > 32 || result.Clients.DistinctBy(entry => entry.ClientId).Count() != result.Clients.Count)
            throw new InvalidDataException("client_registry_invalid");
        foreach (var entry in result.Clients)
        {
            ValidateIdentity(entry.ClientId, entry.Application, entry.Transport, entry.Defaults);
            if (entry.Generation < 1 || entry.Salt.Length != 32 || entry.Verifier.Length != 32 || entry.KeyId.Length != 32 || !Hex().IsMatch(entry.KeyId) ||
                entry.ManagedAdapter && (entry.ClientId != "codex" || entry.Transport != "mcp")) throw new InvalidDataException("client_registry_invalid");
        }
        return result;
    }

    private void Write(Store value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumRegistryBytes) throw new InvalidDataException("client_registry_invalid");
        var scratch = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(bytes); file.Flush(true); }
            CortexProductLayout.RejectExistingReparseComponents(_path);
            File.Move(scratch, _path, true);
        }
        finally { if (File.Exists(scratch)) File.Delete(scratch); }
    }

    private static void ValidateIdentity(string clientId, string application, string transport, CortexClientDefaults defaults)
    {
        if (clientId is null || application is null || defaults is null || defaults.ReadNamespaces is null || defaults.WriteNamespaces is null ||
            !Name().IsMatch(clientId) || transport is not ("mcp" or "native") || !Path.IsPathFullyQualified(application) ||
            application != Path.GetFullPath(application) || application.Length > 240 || defaults.ReadNamespaces.Length > 32 || defaults.WriteNamespaces.Length > 32 ||
            defaults.ReadNamespaces.Concat(defaults.WriteNamespaces).Any(ns => ns is null || !Name().IsMatch(ns)) ||
            defaults.ReadNamespaces.Distinct().Count() != defaults.ReadNamespaces.Length || defaults.WriteNamespaces.Distinct().Count() != defaults.WriteNamespaces.Length)
            throw new InvalidDataException("client_registration_invalid");
        CortexProductLayout.RejectExistingReparseComponents(application);
    }

    private static bool SamePath(string expected, string? actual) => actual is not null &&
        StringComparer.OrdinalIgnoreCase.Equals(expected, Path.GetFullPath(actual));
    internal static void RequireClientId(string value)
    {
        if (value is null || !Name().IsMatch(value)) throw new ArgumentException("client_id_invalid");
    }
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,62}$", RegexOptions.CultureInvariant)] private static partial Regex Name();
    [GeneratedRegex("^[0-9a-f]+$", RegexOptions.CultureInvariant)] private static partial Regex Hex();
}
