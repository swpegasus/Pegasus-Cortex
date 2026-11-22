using System.Text.Json;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Engine;

internal static class JsonFiles
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static T Read<T>(string path, T fallback)
    {
        if (!File.Exists(path)) return fallback;
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options) ?? fallback;
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(value, Options));
        File.Move(tmp, path, true);
    }
}

public sealed class AgentOpsPaths
{
    public AgentOpsPaths(string? stateRoot = null)
    {
        if (stateRoot is null)
        {
            var layout = CortexProductLayout.ResolveExecution();
            CortexUserStateDirectories.EnsureExecution(layout);
            StateRoot = layout.UserExecutionDirectory;
        }
        else
        {
            // Explicit fixture and service-owned roots retain their own policy;
            // SYSTEM state must never acquire signed-in-user directory security.
            StateRoot = Path.GetFullPath(stateRoot);
            Directory.CreateDirectory(StateRoot);
        }
    }

    public string StateRoot { get; }
    public string Workspaces => Path.Combine(StateRoot, "workspaces.json");
    public string Jobs => Path.Combine(StateRoot, "jobs.json");
}

public sealed record Workspace(string Alias, string Path);

public sealed class WorkspaceRegistry
{
    private readonly AgentOpsPaths _paths;
    private readonly WindowsPathContextNormalizer _pathNormalizer;
    private readonly object _gate = new();

    public WorkspaceRegistry(AgentOpsPaths paths,WindowsPathContextNormalizer? pathNormalizer=null){_paths=paths;_pathNormalizer=pathNormalizer??new WindowsPathContextNormalizer();}

    public Workspace Register(string alias, string path)
    {
        ValidateAlias(alias);var normalized = _pathNormalizer.NormalizeAliasRoot(alias,path).Root.CanonicalPath;
        lock (_gate)
        {
            var all = JsonFiles.Read(_paths.Workspaces, new List<Workspace>());
            var existing = all.FindIndex(x => StringComparer.OrdinalIgnoreCase.Equals(x.Alias, alias));
            var workspace = new Workspace(alias, normalized);
            if (existing >= 0) all[existing] = workspace; else all.Add(workspace);
            JsonFiles.Write(_paths.Workspaces, all.OrderBy(x => x.Alias, StringComparer.OrdinalIgnoreCase).ToList());
            return workspace;
        }
    }

    public Workspace? Resolve(string? alias, string cwd)
    {
        var all = List();
        if (!string.IsNullOrWhiteSpace(alias))
            return all.SingleOrDefault(x => StringComparer.OrdinalIgnoreCase.Equals(x.Alias, alias));
        var normalized = _pathNormalizer.Normalize(Path.GetFullPath(cwd)).CanonicalPath;
        return all.Where(x => PathNormalizer.Contains(x.Path, normalized)).OrderByDescending(x => x.Path.Length).FirstOrDefault();
    }

    public IReadOnlyList<Workspace> List()
    {
        lock (_gate) return JsonFiles.Read(_paths.Workspaces, new List<Workspace>());
    }

    private static void ValidateAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias) || alias.Length > 32 || alias.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("invalid_alias");
    }
}

public static class PathNormalizer
{
    public static string NormalizeDirectory(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool Contains(string root, string candidate)
    {
        var rel = Path.GetRelativePath(root, candidate);
        return rel == "." || (!Path.IsPathRooted(rel) && rel != ".." && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    public static void RejectReparseComponents(string root,string candidate)
    {
        root=NormalizeDirectory(root);candidate=Path.GetFullPath(candidate);
        if(!Contains(root,candidate))throw new AgentOpsException("precondition");
        var current=root; RejectIfExistingReparse(current);
        var relative=Path.GetRelativePath(root,candidate);
        if(relative==".")return;
        foreach(var component in relative.Split([Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar],StringSplitOptions.RemoveEmptyEntries))
        { current=Path.Combine(current,component); RejectIfExistingReparse(current); if(!File.Exists(current)&&!Directory.Exists(current))break; }
    }
    public static void RejectAbsoluteReparseComponents(string candidate)
    {
        candidate=Path.GetFullPath(candidate);var root=Path.GetPathRoot(candidate)??throw new AgentOpsException("invalid");var current=root;
        foreach(var component in Path.GetRelativePath(root,candidate).Split([Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar],StringSplitOptions.RemoveEmptyEntries)){current=Path.Combine(current,component);RejectIfExistingReparse(current);if(!File.Exists(current)&&!Directory.Exists(current))break;}
    }
    private static void RejectIfExistingReparse(string path){try{if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new AgentOpsException("reparse_boundary");}catch(Exception ex)when(ex is FileNotFoundException or DirectoryNotFoundException){} }
}
