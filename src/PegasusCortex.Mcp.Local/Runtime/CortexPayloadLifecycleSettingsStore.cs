using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Mcp.Local.Runtime;

internal sealed class CortexPayloadLifecycleSettingsPublicationException : IOException
{
    internal string Code { get; }
    internal CortexPayloadLifecycleSettings? ConfirmedSettings { get; }
    internal CortexPayloadLifecycleSettingsPublicationException(string code,
        CortexPayloadLifecycleSettings? confirmedSettings = null, Exception? innerException = null)
        : base(code, innerException)
    { Code = code; ConfirmedSettings = confirmedSettings; }
}

/// <summary>One owner-private policy file. The directory is supplied by the
/// actual owner composition, never by a wire request or a namespace label.</summary>
internal sealed class CortexPayloadLifecycleSettingsStore : ICortexPayloadLifecycleSettingsStore
{
    internal const string FileName = "payload-lifecycle.v1.json";
    internal const string Schema = "pegasus.cortex.payload-lifecycle-settings/1";
    internal const string PendingFileName = ".payload-lifecycle.pending.json";
    internal const string PreviousFileName = ".payload-lifecycle.previous.json";
    internal const int MaximumBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    private sealed record Document(string Schema, long Revision, CortexPayloadLifecyclePolicy Policy,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PredecessorSha256 = null);
    private sealed record Entry(CortexPayloadLifecycleSettings Settings, byte[] Bytes, string? PredecessorSha256);
    private readonly string _directory, _path, _lockPath, _pendingPath, _previousPath;
    private readonly bool _protect;
    private readonly Action<string, string, string> _replaceFile;

    internal CortexPayloadLifecycleSettingsStore(string configDirectory, bool protect = true,
        Action<string, string, string>? replaceFile = null)
    {
        _directory = CortexProductLayout.RequireCanonicalAbsoluteDirectory(configDirectory, nameof(configDirectory));
        if (_directory.StartsWith("\\\\", StringComparison.Ordinal) ||
            Path.TrimEndingDirectorySeparator(_directory) == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(_directory)!))
            throw new ArgumentException("A local owner configuration directory is required.", nameof(configDirectory));
        _path = Path.Combine(_directory, FileName);
        _lockPath = Path.Combine(_directory, ".payload-lifecycle.lock");
        _pendingPath = Path.Combine(_directory, PendingFileName);
        _previousPath = Path.Combine(_directory, PreviousFileName);
        _protect = protect;
        // The optional primitive is a trusted fixture seam, never a wire field.
        _replaceFile = replaceFile ?? ((pending, selected, previous) =>
            File.Replace(pending, selected, previous, ignoreMetadataErrors: false));
        foreach (var path in new[] { _path, _lockPath, _pendingPath, _previousPath }) RequirePath(path);
    }

    internal static CortexPayloadLifecycleSettings DefaultSettings => new(0,
        new(1, false, Array.Empty<string>()));

    internal static CortexPayloadLifecyclePolicy Validate(CortexPayloadLifecyclePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Version != 1 || policy.Namespaces is null || policy.Namespaces.Count > 8 ||
            policy.Enabled && policy.Namespaces.Count == 0 ||
            policy.MinimumArchiveAgeDays is < 1 or > 36500 || policy.TargetHotMiB is < 0 or > 16L * 1024 * 1024 ||
            policy.IntervalMinutes is < 1 or > 1440 || policy.WorkMilliseconds is < 50 or > 10000 ||
            policy.MaximumMetadataHeads is < 256 or > 65536)
            throw new ArgumentException("payload_policy_invalid");
        var namespaces = policy.Namespaces.ToArray();
        if (namespaces.Length > 8 || policy.Enabled && namespaces.Length == 0 ||
            namespaces.Any(value => !Identifier(value)) || namespaces.Distinct(StringComparer.Ordinal).Count() != namespaces.Length)
            throw new ArgumentException("payload_policy_invalid");
        return policy with { Namespaces = Array.AsReadOnly(namespaces) };
    }

    private static bool Identifier(string? value) => value is { Length: > 0 and <= 128 } &&
        char.IsAsciiLetterOrDigit(value[0]) && value.Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');

    public CortexPayloadLifecycleSettings Read()
    {
        RequireDirectory(CortexUserStateAccess.Read);
        // A pristine read creates no lock or policy entry. If a lock already
        // exists, a read lease excludes its writer until this snapshot settles.
        using var observation = ObserveExistingLock();
        RequireNoRecoveryEntries();
        var entry = ReadCurrentCore();
        if (observation is null)
        {
            // A writer could have started after our first absent-lock check.
            // Never reinterpret its partial rename as a revision-zero policy.
            using var finalObservation = ObserveExistingLock();
            RequireNoRecoveryEntries();
        }
        return entry?.Settings ?? DefaultSettings;
    }

    private Entry? ReadCurrentCore()
    {
        RequirePath(_path);
        try { return ReadEntry(_path); }
        catch (FileNotFoundException) { return null; }
    }

    private Entry ReadEntry(string path)
    {
        RequirePath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length is < 1 or > MaximumBytes) throw new InvalidDataException("payload_policy_invalid");
        if (_protect) CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(path, CortexUserStateAccess.Read);
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("payload_policy_changed_during_read");
        return Parse(bytes);
    }

    private FileStream? ObserveExistingLock()
    {
        RequirePath(_lockPath);
        try
        {
            if (_protect && EntryExists(_lockPath))
                CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(_lockPath, CortexUserStateAccess.Read);
            return new FileStream(_lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException) { return null; }
        catch (IOException error) { throw new IOException("payload_policy_busy", error); }
    }

    public CortexPayloadLifecycleSettings Save(CortexPayloadLifecyclePolicy policy, long expectedRevision)
    {
        var frozen = Validate(policy);
        if (expectedRevision < 0 || expectedRevision == long.MaxValue) throw new ArgumentException("payload_policy_revision_invalid");
        RequireDirectory(CortexUserStateAccess.Modify);
        RequirePath(_lockPath);
        CortexPayloadLifecycleSettings? confirmed = null;
        var publicationEntered = false;
        try
        {
            if (_protect && EntryExists(_lockPath)) CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(_lockPath);
            // This is one writer lock, not another runtime/store authority. No
            // sharing retry, old-file deletion or ACL-merge bypass is performed.
            using var lease = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            if (_protect) CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(_lockPath);
            if (EntryExists(_pendingPath)) throw Recovery();
            var current = ReadCurrentCore();
            if ((current?.Settings.Revision ?? 0) != expectedRevision) throw new InvalidOperationException("payload_policy_changed");
            if (EntryExists(_previousPath))
            {
                if (!VerifiedPredecessor(current)) throw Recovery();
                // This is an explicit later CAS save after fresh verification,
                // not an automatic rollback or replay of the interrupted save.
                try { DeleteVerifiedPrevious(); }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                { throw Cleanup(current!.Settings, error); }
            }
            var result = new CortexPayloadLifecycleSettings(checked(expectedRevision + 1), frozen);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(Schema, result.Revision, frozen,
                current is null ? null : Hash(current.Bytes)), Json);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("payload_policy_too_large");
            RequirePath(_pendingPath);
            using (var file = new FileStream(_pendingPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            RequirePath(_pendingPath);
            if (_protect) CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(_pendingPath);
            RequireDirectory(CortexUserStateAccess.Modify);
            RequirePath(_path); RequirePath(_previousPath);
            if (current is not null && _protect) CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(_path);
            publicationEntered = true;
            if (current is null) File.Move(_pendingPath, _path, overwrite: false);
            else _replaceFile(_pendingPath, _path, _previousPath);
            // Only exact selected bytes plus normal private ACL admission make
            // this publication confirmed. A header or vanished source is not proof.
            var actual = ReadEntry(_path);
            if (!actual.Bytes.SequenceEqual(bytes)) throw new InvalidDataException("payload_policy_publication_unverified");
            confirmed = result;
            if (EntryExists(_pendingPath)) throw new IOException("payload_policy_pending_retained");
            if (EntryExists(_previousPath))
            {
                if (!VerifiedPredecessor(actual)) throw new InvalidDataException("payload_policy_predecessor_unverified");
                DeleteVerifiedPrevious();
            }
            return result;
        }
        catch (CortexPayloadLifecycleSettingsPublicationException) { throw; }
        catch (InvalidOperationException error) when (!publicationEntered && error.Message == "payload_policy_changed") { throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (confirmed is not null) throw Cleanup(confirmed, error);
            // ReplaceFile has documented partial rename states. Pending/previous
            // are bounded recovery evidence, never unconditional finally cleanup.
            if (publicationEntered || RecoveryEntriesMayExist()) throw Recovery(error);
            throw;
        }
    }

    private void RequireNoRecoveryEntries()
    {
        if (EntryExists(_pendingPath)) throw Recovery();
        if (!EntryExists(_previousPath)) return;
        Entry? current = null;
        try { current = ReadCurrentCore(); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { throw Recovery(error); }
        if (VerifiedPredecessor(current)) throw Cleanup(current!.Settings);
        throw Recovery();
    }

    private bool VerifiedPredecessor(Entry? current)
    {
        if (current?.PredecessorSha256 is null || current.Settings.Revision <= 1) return false;
        try
        {
            var previous = ReadEntry(_previousPath);
            return previous.Settings.Revision == current.Settings.Revision - 1 && Hash(previous.Bytes) == current.PredecessorSha256;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return false; }
    }

    private void DeleteVerifiedPrevious()
    {
        RequirePath(_previousPath);
        if (_protect) CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(_previousPath);
        File.Delete(_previousPath);
    }

    private bool RecoveryEntriesMayExist()
    {
        try { return EntryExists(_pendingPath) || EntryExists(_previousPath); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return true; }
    }
    private static CortexPayloadLifecycleSettingsPublicationException Recovery(Exception? error = null) =>
        new("payload_policy_recovery_required", innerException: error);
    private static CortexPayloadLifecycleSettingsPublicationException Cleanup(CortexPayloadLifecycleSettings settings, Exception? error = null) =>
        new("payload_policy_cleanup_required", settings, error);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Entry Parse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            UniqueNames(document.RootElement);
            var root = document.RootElement;
            RequireFields(root, root.TryGetProperty("predecessorSha256", out _) ? ["schema", "revision", "policy", "predecessorSha256"] : ["schema", "revision", "policy"]);
            if (root.GetProperty("schema").ValueKind != JsonValueKind.String || root.GetProperty("schema").GetString() != Schema ||
                !root.GetProperty("revision").TryGetInt64(out var revision) || revision < 1)
                throw new JsonException();
            string? predecessor = null;
            if (root.TryGetProperty("predecessorSha256", out var link))
            {
                if (revision <= 1 || link.ValueKind != JsonValueKind.String || (predecessor = link.GetString()) is not { Length: 64 } ||
                    predecessor.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new JsonException();
            }
            var policy = root.GetProperty("policy");
            RequireFields(policy, ["version", "enabled", "namespaces", "minimumArchiveAgeDays", "targetHotMiB", "intervalMinutes", "workMilliseconds", "maximumMetadataHeads"]);
            if (policy.GetProperty("enabled").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                policy.GetProperty("namespaces").ValueKind != JsonValueKind.Array) throw new JsonException();
            var value = policy.Deserialize<CortexPayloadLifecyclePolicy>(Json) ?? throw new JsonException();
            return new(new(revision, Validate(value)), bytes, predecessor);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or OverflowException)
        { throw new InvalidDataException("payload_policy_invalid", error); }
    }

    private static void RequireFields(JsonElement value, string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length ||
            value.EnumerateObject().Any(property => !names.Contains(property.Name, StringComparer.Ordinal))) throw new JsonException();
    }
    private static void UniqueNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!seen.Add(property.Name)) throw new JsonException(); UniqueNames(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueNames(item);
    }
    private void RequireDirectory(CortexUserStateAccess access)
    {
        RequirePath(_directory);
        if (!Directory.Exists(_directory)) throw new DirectoryNotFoundException("payload_policy_directory_unavailable");
        if (_protect) CortexLocalRegistryAclPolicy.RequireProtectedDirectory(_directory, access);
    }
    private static void RequirePath(string path)
    {
        if (path.Length > 240) throw new ArgumentException("payload_policy_path_too_long");
        CortexProductLayout.RejectExistingReparseComponents(path);
        if (Directory.Exists(path) && Path.GetFileName(path) is FileName or ".payload-lifecycle.lock" or PendingFileName or PreviousFileName)
            throw new IOException("payload_policy_entry_is_directory");
    }
    private static bool EntryExists(string path)
    {
        RequirePath(path);
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
