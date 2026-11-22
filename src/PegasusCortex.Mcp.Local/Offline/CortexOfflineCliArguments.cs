namespace Pegasus.Cortex.Mcp.Local.Offline;

public sealed record CortexOfflineCliInvocation(
    string Command,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Options)
{
    public string RequireSingle(string name)
    {
        if (!Options.TryGetValue(name, out var values) || values.Count != 1)
            throw new ArgumentException($"Option '--{name}' is required exactly once.");
        return values[0];
    }

    public string? OptionalSingle(string name)
    {
        if (!Options.TryGetValue(name, out var values))
            return null;
        if (values.Count != 1)
            throw new ArgumentException($"Option '--{name}' may be supplied at most once.");
        return values[0];
    }

    public IReadOnlyList<string> Many(string name) =>
        Options.TryGetValue(name, out var values) ? values : Array.Empty<string>();
}

public static class CortexOfflineCliArguments
{
    private const int MaximumArguments = 64;
    private const int MaximumArgumentCharacters = 4096;

    private static readonly IReadOnlyDictionary<string, HashSet<string>> Allowed =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["init"] = Set("root", "task-id", "project-id", "write-namespace", "read-namespace"),
            ["migrate-legacy"] = Set(
                "root", "task-id", "project-id", "write-namespace", "read-namespace"),
            ["capture"] = Set(
                "root", "task-id", "record-id", "kind", "tier", "title", "content-file",
                "tag", "source", "source-id", "evidence"),
            ["status"] = Set("root", "task-id"),
            ["search"] = Set("root", "task-id", "text", "limit"),
            ["read"] = Set("root", "task-id", "frame-id"),
            ["ingest"] = Set("root", "task-id", "frame-id"),
            ["refresh"] = Set("root", "task-id")
        };

    private static readonly HashSet<string> Repeatable = Set("read-namespace", "tag");

    public static CortexOfflineCliInvocation Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count is < 2 or > MaximumArguments ||
            !string.Equals(args[0], "offline", StringComparison.Ordinal))
        {
            throw new ArgumentException("An explicit bounded offline command is required.");
        }
        var command = RequireBounded(args[1], "command");
        if (!Allowed.TryGetValue(command, out var allowed))
            throw new ArgumentException($"Offline command '{command}' is unsupported.");
        if ((args.Count - 2) % 2 != 0)
            throw new ArgumentException("Every offline option requires one separate value argument.");

        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 2; index < args.Count; index += 2)
        {
            var token = RequireBounded(args[index], "option");
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length < 3)
                throw new ArgumentException("Offline options must use '--name value'.");
            var name = token[2..];
            if (!allowed.Contains(name))
                throw new ArgumentException($"Option '--{name}' is not valid for offline {command}.");
            var value = RequireBounded(args[index + 1], name);
            if (!options.TryGetValue(name, out var values))
            {
                values = [];
                options.Add(name, values);
            }
            if (values.Count != 0 && !Repeatable.Contains(name))
                throw new ArgumentException($"Option '--{name}' cannot be repeated.");
            values.Add(value);
        }
        if (!options.TryGetValue("task-id", out var taskIds) || taskIds.Count != 1)
            throw new ArgumentException("Option '--task-id' is required exactly once.");
        RequireTaskId(taskIds[0]);
        return new CortexOfflineCliInvocation(
            command,
            options.ToDictionary(
                item => item.Key,
                item => (IReadOnlyList<string>)Array.AsReadOnly(item.Value.ToArray()),
                StringComparer.Ordinal));
    }

    public static void RequireWarmup(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count != 1 || !string.Equals(args[0], "warmup", StringComparison.Ordinal))
            throw new ArgumentException("Warmup accepts no options.");
    }

    private static string RequireBounded(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumArgumentCharacters ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException($"The offline {description} argument is empty, unsafe, or too long.");
        }
        return value;
    }

    public static string RequireTaskId(string value)
    {
        if (value.Length > 128 || !IsLowerAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(character =>
                !IsLowerAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')) ||
            string.Equals(
                value,
                CortexOfflineProjectCatalog.LegacyUnassignedTaskId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A stable canonical lowercase ASCII portable non-legacy task ID is required.",
                nameof(value));
        }
        return value;
    }

    private static bool IsLowerAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static HashSet<string> Set(params string[] values) =>
        new(values, StringComparer.Ordinal);
}
