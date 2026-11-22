using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Mcp.Local.Offline;

public sealed record CortexPrivateReadCacheInvocation(
    string Command,
    CortexPrivateReadCacheRequest Request,
    string? ExpectedSnapshotGeneration);

/// <summary>Opt-in passive-cache arguments; the legacy offline grammar is unchanged.</summary>
public static class CortexPrivateReadCacheArguments
{
    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
        { "cache-init", "cache-status", "cache-refresh", "cache-read" };
    private static readonly HashSet<string> Options = new(StringComparer.Ordinal)
        { "root", "task-id", "project-id", "write-namespace", "read-namespace", "expected-generation" };

    public static bool IsCommand(IReadOnlyList<string> args) =>
        args is not null && args.Count >= 2 && args[0] == "offline" && Commands.Contains(args[1]);

    public static CortexPrivateReadCacheInvocation Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!IsCommand(args) || args.Count > 64 || (args.Count - 2) % 2 != 0)
            throw new ArgumentException("An explicit bounded private-cache command with separate option values is required.");
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 2; index < args.Count; index += 2)
        {
            var token = Bounded(args[index]);
            if (!token.StartsWith("--", StringComparison.Ordinal) || !Options.Contains(token[2..]))
                throw new ArgumentException("An unsupported private-cache option was supplied.");
            var name = token[2..];
            if (name == "expected-generation" && args[1] != "cache-refresh")
                throw new ArgumentException("Expected generation is valid only for cache-refresh.");
            if (!values.TryGetValue(name, out var list)) values.Add(name, list = []);
            if (list.Count != 0 && name != "read-namespace")
                throw new ArgumentException("A singleton private-cache option was repeated.");
            list.Add(Bounded(args[index + 1]));
        }

        string One(string name) => values.TryGetValue(name, out var list) && list.Count == 1
            ? list[0] : throw new ArgumentException("Every private-cache binding option is required explicitly.");
        var root = One("root");
        if (!Path.IsPathFullyQualified(root) || !string.Equals(root, Path.GetFullPath(root),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("The content root must be an absolute canonical path.");
        var task = CortexOfflineCliArguments.RequireTaskId(One("task-id"));
        var project = One("project-id");
        var write = One("write-namespace");
        if (!values.TryGetValue("read-namespace", out var reads) || reads.Count < 1 || reads.Count > CortexV2Limits.MaximumNamespaces ||
            reads.Distinct(StringComparer.Ordinal).Count() != reads.Count)
            throw new ArgumentException($"One to {CortexV2Limits.MaximumNamespaces} distinct ordered read namespaces are required.");

        string? expected = null;
        if (args[1] == "cache-refresh")
        {
            var generation = One("expected-generation");
            if (generation != "none")
            {
                if (generation.Length != 64 || generation.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                    throw new ArgumentException("Expected generation must be none or an exact lowercase SHA-256.");
                expected = generation;
            }
        }
        return new(args[1], new(root, task, project, write, Array.AsReadOnly(reads.ToArray())), expected);
    }

    private static string Bounded(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 && !value.Any(char.IsControl)
            ? value : throw new ArgumentException("A private-cache argument is empty, unsafe or exceeds its bound.");
}
