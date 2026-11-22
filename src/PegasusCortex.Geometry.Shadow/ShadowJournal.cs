using System.Text.Json;

namespace Pegasus.Cortex.Geometry.Shadow;

/// <summary>Single-writer, generation-bound ring of metadata records. No body/query/SQL fields.
/// The dedicated root is explicit, never derived from an operational DB or installed root.</summary>
internal sealed class ShadowJournal : IAsyncDisposable
{
    private readonly string _root;
    private readonly CortexShadowProfile _profile;
    private readonly CortexShadowOptions _options;
    private readonly FileStream _lock;
    private int _next;
    public long Bytes { get; private set; }
    public long MetadataBytes => new[] { "binding.json", "status.json", "frontier.json" }
        .Select(name => Path.Combine(_root, name)).Where(File.Exists).Sum(path => new FileInfo(path).Length);
    public long Writes { get; private set; }
    public long Evictions { get; private set; }

    public ShadowJournal(string root, CortexShadowProfile profile, CortexShadowOptions options)
    {
        _root = Path.GetFullPath(root);
        _profile = profile; _options = options;
        if (!Path.IsPathFullyQualified(root) || root != _root || root.StartsWith(@"\\") ||
            root.Length > 200 || root.Any(char.IsControl))
            throw new ArgumentException("An explicit canonical local research root is required.");
        foreach (var segment in root[(Path.GetPathRoot(root)?.Length ?? 0)..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.EndsWith(' ') || segment.EndsWith('.') || segment.Contains(':') ||
                System.Text.Encoding.UTF8.GetByteCount(segment) > 255 ||
                System.Text.RegularExpressions.Regex.IsMatch(segment, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new ArgumentException("Research root contains an unsafe path segment.");
        }
        for (var dir = new DirectoryInfo(_root); dir is not null; dir = dir.Parent)
            if (dir.LinkTarget is not null || dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Research root cannot cross a link.");
        Directory.CreateDirectory(_root);
        var binding = Path.Combine(_root, "binding.json");
        if (!File.Exists(binding) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new IOException("A research root must be empty or exactly bound to this profile.");
        Check(binding); Check(Path.Combine(_root, "writer.lock"));
        _lock = new(Path.Combine(_root, "writer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            var expected = ShadowPolicies.Binding(_root, profile, options);
            if (File.Exists(binding))
            {
                if (new FileInfo(binding).Length > 4096 || File.ReadAllText(binding) != expected)
                    throw new IOException("Research generation binding mismatch.");
            }
            else
            {
                using var file = new FileStream(binding, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(System.Text.Encoding.UTF8.GetBytes(expected));
            }
            foreach (var path in Directory.EnumerateFileSystemEntries(_root))
            {
                var name = Path.GetFileName(path);
                if (name is "binding.json" or "writer.lock") continue;
                if (name is "status.json" or "frontier.json")
                { ValidateMetadata(path, name == "status.json" ? 16384 : 1024 * 1024); continue; }
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^n[0-9]{3}\.json$") ||
                    !int.TryParse(name.AsSpan(1, 3), out var slot) || slot >= options.JournalSlots)
                    throw new IOException("Unrecognized research artifact; preserve it for recovery.");
                Check(path);
                if (new FileInfo(path).Length > 32768) throw new IOException("Oversized research artifact.");
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                if (doc.RootElement.GetProperty("Generation").GetGuid() != profile.Generation)
                    throw new IOException("Foreign research artifact generation.");
            }
            _next = Enumerable.Range(0, options.JournalSlots).OrderBy(i =>
                File.Exists(Path.Combine(_root, $"n{i:000}.json"))
                    ? File.GetLastWriteTimeUtc(Path.Combine(_root, $"n{i:000}.json")) : DateTime.MinValue).First();
            Measure();
        }
        catch { _lock.Dispose(); throw; }
    }

    private static void Check(string path)
    {
        var info = new FileInfo(path);
        if (Directory.Exists(path) || info.LinkTarget is not null ||
            info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Research entries must be ordinary files.");
    }

    public async ValueTask WriteAsync(object value, DateTimeOffset now, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 32768) throw new IOException("Research observation exceeds 32KiB.");
        var slot = _next;
        _next = (_next + 1) % _options.JournalSlots;
        var path = Path.Combine(_root, $"n{slot:000}.json");
        var temp = Path.Combine(_root, "pending.json");
        Check(path); Check(temp);
        if (File.Exists(path)) ValidateOwned(path);
        var existed = File.Exists(path);
        var created = false;
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.Asynchronous))
            {
                created = true;
                await file.WriteAsync(bytes, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
            Writes++; if (existed) Evictions++; Measure();
        }
        finally
        {
            // Only this generation's just-created scratch file is removable.
            if (created && File.Exists(temp)) File.Delete(temp);
        }
    }

    public void Prune(DateTimeOffset now)
    {
        var paths = Directory.EnumerateFiles(_root, "n???.json").ToArray();
        foreach (var path in paths) ValidateOwned(path); // Validate all before deleting any.
        foreach (var path in paths)
        {
            if (new FileInfo(path).LastWriteTimeUtc >= now.UtcDateTime.AddSeconds(-_options.RetentionSeconds))
                continue;
            File.Delete(path); Evictions++;
        }
        Measure();
    }

    private void ValidateMetadata(string path, int maximum)
    {
        Check(path);
        if (new FileInfo(path).Length > maximum) throw new IOException("Oversized research checkpoint.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var expectedSchema = Path.GetFileName(path) == "status.json" ? "cortex.shadow.summary/1" : "cortex.shadow.frontier/1";
        if (doc.RootElement.GetProperty("Generation").GetGuid() != _profile.Generation ||
            doc.RootElement.GetProperty("Schema").GetString() != expectedSchema)
            throw new IOException("Foreign research checkpoint generation or schema.");
    }

    internal CortexShadowStudySummary? ReadSummary()
    {
        var path = Path.Combine(_root, "status.json");
        if (!File.Exists(path)) return null;
        ValidateMetadata(path, 16384);
        var result = JsonSerializer.Deserialize<CortexShadowStudySummary>(File.ReadAllBytes(path))!;
        if (result.Schema != "cortex.shadow.summary/1" ||
            result.Status.Generation != _profile.Generation ||
            result.Status.StopwatchFrequency != System.Diagnostics.Stopwatch.Frequency ||
            result.Status.Counters.Values.Any(x => x < 0))
            throw new IOException("Invalid research summary.");
        return result;
    }

    internal ShadowFrontierSnapshot? ReadFrontier()
    {
        var path = Path.Combine(_root, "frontier.json");
        if (!File.Exists(path)) return null;
        ValidateMetadata(path, 1024 * 1024);
        var result = JsonSerializer.Deserialize<ShadowFrontierSnapshot>(File.ReadAllBytes(path))!;
        if (result.Schema != "cortex.shadow.frontier/1" || result.Epoch < 0 || result.Entries.Count > 2048)
            throw new IOException("Invalid research reference checkpoint.");
        return result;
    }

    internal async ValueTask SaveSummaryAsync(CortexShadowStudySummary summary,
        ShadowFrontierSnapshot? frontier, CancellationToken token)
    {
        if (frontier is not null)
            await WriteMetadataAsync("frontier.json", frontier, 1024 * 1024, token).ConfigureAwait(false);
        await WriteMetadataAsync("status.json", summary, 16384, token).ConfigureAwait(false);
    }

    private async ValueTask WriteMetadataAsync(string name, object value, int maximum, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > maximum) throw new IOException("Research checkpoint exceeds its fixed budget.");
        var path = Path.Combine(_root, name); var temp = Path.Combine(_root, name + ".pending");
        Check(path); Check(temp);
        if (File.Exists(path)) ValidateMetadata(path, maximum);
        var created = false;
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            { created = true; await file.WriteAsync(bytes, token).ConfigureAwait(false); }
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        finally { if (created && File.Exists(temp)) File.Delete(temp); }
    }

    private void ValidateOwned(string path)
    {
        Check(path);
        var name = Path.GetFileName(path);
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^n[0-9]{3}\.json$") ||
            !int.TryParse(name.AsSpan(1, 3), out var slot) || slot >= _options.JournalSlots ||
            new FileInfo(path).Length > 32768)
            throw new IOException("Unexpected research entry; preserve it.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var schema = doc.RootElement.GetProperty("Schema").GetString();
        if (doc.RootElement.GetProperty("Generation").GetGuid() != _profile.Generation ||
            schema is not ("cortex.shadow.observation/1" or "cortex.shadow.feedback/1"))
            throw new IOException("Foreign research generation or schema; preserve it.");
    }

    private void Measure() => Bytes = Directory.EnumerateFiles(_root, "n???.json")
        .Sum(path => new FileInfo(path).Length);
    public ValueTask DisposeAsync() => _lock.DisposeAsync();
}
