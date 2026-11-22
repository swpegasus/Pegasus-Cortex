using System.Text;
using System.Text.RegularExpressions;
using System.IO.Enumeration;

namespace Pegasus.AgentOps.Engine;

/// <summary>Windows-native implementations for familiar Unix-shaped developer operations.</summary>
public static class CompatibilityOperations
{
    private const int MaxTextBytes = 512 * 1024;

    public static IReadOnlyDictionary<string, object?> Find(string root, string? pattern, string kind, int max, CancellationToken cancellationToken = default)
    {
        root = Admit(root);
        if (!Directory.Exists(root)) throw new AgentOpsException("not_found");
        if (kind is not ("any" or "file" or "dir")) throw new AgentOpsException("invalid");
        var matches = new List<object>();
        var pending = new Stack<string>(); pending.Push(root); var matched = 0; var truncated = false;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested(); var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                var attributes = File.GetAttributes(entry); var reparse = attributes.HasFlag(FileAttributes.ReparsePoint); var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                if (!reparse && isDirectory) pending.Push(entry);
                if ((kind == "file" && isDirectory) || (kind == "dir" && !isDirectory) || !Matches(Path.GetFileName(entry), pattern)) continue;
                matched++; if (matches.Count < max) matches.Add(new { path = Bound(entry, 2048), type = reparse ? "link" : isDirectory ? "dir" : "file" }); else { truncated = true; return new Dictionary<string, object?> { ["src"] = root, ["entries"] = matches, ["matched"] = matched, ["truncated"] = truncated }; }
            }
        }
        return new Dictionary<string, object?> { ["src"] = root, ["entries"] = matches, ["matched"] = matched, ["truncated"] = truncated };
    }

    public static IReadOnlyDictionary<string, object?> Search(string root, string pattern, bool recur, bool ignoreCase, bool literal, string? include, int max, CancellationToken cancellationToken = default)
    {
        root = Admit(root); if (string.IsNullOrEmpty(pattern)) throw new AgentOpsException("invalid");
        IEnumerable<string> files;
        if (File.Exists(root)) files = [root];
        else if (Directory.Exists(root)) files = EnumerateFiles(root, recur);
        else throw new AgentOpsException("not_found");
        Regex? regex = null;
        if (!literal) try { regex = new Regex(pattern, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromSeconds(1)); } catch (ArgumentException) { throw new AgentOpsException("invalid"); }
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal; var items = new List<object>(); var matched = 0; var truncated = false;
        foreach (var file in files.Where(file => Matches(Path.GetFileName(file), include)).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested(); IReadOnlyDictionary<string, object?> read; try { read = ExtendedOperations.Read(file, null, MaxTextBytes); } catch (AgentOpsException ex) when (ex.Code is "encoding_required" or "invalid") { continue; }
            var text = (string)read["text"]!; var lineNumber = 0;
            using var reader = new StringReader(text); string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lineNumber++; var hit = literal ? line.Contains(pattern, comparison) : regex!.IsMatch(line); if (!hit) continue;
                matched++; if (items.Count < max) items.Add(new { path = Bound(file, 2048), line = lineNumber, text = Bound(line, 2048) }); else { truncated = true; return new Dictionary<string, object?> { ["src"] = root, ["matches"] = items, ["matched"] = matched, ["truncated"] = truncated }; }
            }
        }
        return new Dictionary<string, object?> { ["src"] = root, ["matches"] = items, ["matched"] = matched, ["truncated"] = truncated };
    }

    public static IReadOnlyDictionary<string, object?> Slice(string path, string mode, int count, string? encoding)
    {
        if (mode is not ("head" or "tail") || count is < 1 or > 4096) throw new AgentOpsException("invalid");
        var read = ExtendedOperations.Read(Admit(path), encoding, MaxTextBytes); var text = (string)read["text"]!;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var selected = mode == "head" ? lines.Take(count) : lines.TakeLast(count);
        return new Dictionary<string, object?> { ["src"] = path, ["text"] = string.Join(Environment.NewLine, selected), ["count"] = Math.Min(count, lines.Length), ["enc"] = read["enc"], ["mode"] = mode };
    }

    public static IReadOnlyDictionary<string, object?> Diff(string left, string right, int max)
    {
        left = Admit(left); right = Admit(right); var a = (string)ExtendedOperations.Read(left, null, MaxTextBytes)["text"]!; var b = (string)ExtendedOperations.Read(right, null, MaxTextBytes)["text"]!;
        var al = Lines(a); var bl = Lines(b); var changed = new List<object>(); var total = Math.Max(al.Length, bl.Length); var differences = 0;
        for (var i = 0; i < total; i++) { var av = i < al.Length ? al[i] : null; var bv = i < bl.Length ? bl[i] : null; if (StringComparer.Ordinal.Equals(av, bv)) continue; differences++; if (changed.Count < max) changed.Add(new { line = i + 1, left = Bound(av, 2048), right = Bound(bv, 2048) }); }
        return new Dictionary<string, object?> { ["src"] = left, ["dst"] = right, ["equal"] = differences == 0, ["changed"] = differences, ["entries"] = changed, ["truncated"] = differences > max };
    }

    public static IReadOnlyDictionary<string, object?> Count(string path)
    {
        path = Admit(path); var text = (string)ExtendedOperations.Read(path, null, MaxTextBytes)["text"]!;
        return new Dictionary<string, object?> { ["src"] = path, ["lines"] = Lines(text).Length, ["words"] = Regex.Matches(text, @"\S+", RegexOptions.CultureInvariant).Count, ["bytes"] = new FileInfo(path).Length };
    }

    public static IReadOnlyDictionary<string, object?> TransformLines(string path, string op, bool ignoreCase)
    {
        path = Admit(path);
        var text = (string)ExtendedOperations.Read(path, null, MaxTextBytes)["text"]!;
        var lines = Lines(text);
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = op switch
        {
            "sort" => lines.Order(comparer).ToArray(),
            "uniq" => AdjacentDistinct(lines, comparer).ToArray(),
            _ => throw new AgentOpsException("invalid")
        };
        return new Dictionary<string, object?> { ["src"] = path, ["op"] = op, ["lines"] = result.Length, ["text"] = string.Join(Environment.NewLine, result) };
    }

    public static IReadOnlyDictionary<string, object?> Disk(string path, string op, CancellationToken cancellationToken = default)
    {
        path = Admit(path);
        if (op == "df")
        {
            var root = Path.GetPathRoot(path) ?? throw new AgentOpsException("invalid");
            var drive = new DriveInfo(root);
            return new Dictionary<string, object?> { ["src"] = path, ["bytes"] = drive.TotalSize, ["free"] = drive.AvailableFreeSpace, ["used"] = drive.TotalSize - drive.AvailableFreeSpace };
        }
        if (op != "du") throw new AgentOpsException("invalid");
        if (File.Exists(path)) return new Dictionary<string, object?> { ["src"] = path, ["bytes"] = new FileInfo(path).Length, ["files"] = 1, ["dirs"] = 0 };
        if (!Directory.Exists(path)) throw new AgentOpsException("not_found");
        long bytes = 0; var files = 0; var directories = 1; var pending = new Stack<string>(); pending.Push(path);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (attributes.HasFlag(FileAttributes.Directory)) { directories++; pending.Push(entry); }
                else { files++; bytes = checked(bytes + new FileInfo(entry).Length); }
            }
        }
        return new Dictionary<string, object?> { ["src"] = path, ["bytes"] = bytes, ["files"] = files, ["dirs"] = directories };
    }

    public static IReadOnlyDictionary<string, object?> PathInfo(string path, string op)
    {
        path = Admit(path); return op switch
        {
            "realpath" => new Dictionary<string, object?> { ["path"] = path },
            "basename" => new Dictionary<string, object?> { ["path"] = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) },
            "dirname" => new Dictionary<string, object?> { ["path"] = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) },
            _ => throw new AgentOpsException("invalid")
        };
    }

    private static string Admit(string path) { path = Path.GetFullPath(path); PathNormalizer.RejectAbsoluteReparseComponents(path); return path; }
    private static string[] Lines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    private static IEnumerable<string> AdjacentDistinct(IEnumerable<string> values, StringComparer comparer)
    {
        string? previous = null; var hasPrevious = false;
        foreach (var value in values)
        {
            if (hasPrevious && comparer.Equals(previous, value)) continue;
            yield return value; previous = value; hasPrevious = true;
        }
    }
    private static string? Bound(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
    private static bool Matches(string value, string? pattern) => string.IsNullOrEmpty(pattern) || FileSystemName.MatchesSimpleExpression(pattern, value, ignoreCase: true);
    private static IEnumerable<string> EnumerateFiles(string root, bool recur)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0) { var dir = pending.Pop(); foreach (var entry in Directory.EnumerateFileSystemEntries(dir)) { var attributes = File.GetAttributes(entry); if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue; if (attributes.HasFlag(FileAttributes.Directory)) { if (recur) pending.Push(entry); } else yield return entry; } }
    }
}
