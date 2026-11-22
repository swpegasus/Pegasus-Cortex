using Pegasus.Cortex.Mcp.Local.Offline;
using Pegasus.Cortex.Contracts.V2;

internal static class PrivateReadCacheArgumentsTests
{
    internal static string[] Arguments(string command, string root, string? expectedGeneration = null)
    {
        var values = new List<string>
        {
            "offline", command,
            "--root", Path.GetFullPath(root),
            "--task-id", "codex-private-cache-fixture",
            "--project-id", "private-cache-fixture",
            "--write-namespace", "project.private-cache-fixture",
            "--read-namespace", "project.private-cache-fixture",
            "--read-namespace", "knowledge.shared"
        };
        if (expectedGeneration is not null) values.AddRange(["--expected-generation", expectedGeneration]);
        return values.ToArray();
    }

    public static Task StrictGrammarAndLegacyIsolationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "private-cache-argument-fixture");
        foreach (var verb in new[] { "cache-init", "cache-status", "cache-refresh", "cache-read" })
        {
            var args = Arguments(verb, root, verb == "cache-refresh" ? "none" : null);
            var parsed = CortexPrivateReadCacheArguments.Parse(args);
            Check(parsed.Command == verb && parsed.Request.ReadNamespaces.SequenceEqual(
                new[] { "project.private-cache-fixture", "knowledge.shared" }), "Ordered explicit binding was not preserved.");
            Check(parsed.ExpectedSnapshotGeneration is null, "The initial generation was invented.");
            Reject(() => CortexOfflineCliArguments.Parse(args));
        }
        var generation = new string('a', 64);
        Check(CortexPrivateReadCacheArguments.Parse(Arguments("cache-refresh", root, generation))
            .ExpectedSnapshotGeneration == generation, "The exact expected snapshot generation changed.");
        Check(!CortexPrivateReadCacheArguments.IsCommand(new[] { "offline", "refresh" }), "A legacy verb was intercepted.");
        Check(CortexOfflineCliArguments.Parse(new[] { "offline", "refresh", "--root", root, "--task-id", "fixture" })
            .Command == "refresh", "Legacy grammar changed.");

        var valid = Arguments("cache-read", root);
        foreach (var option in new[] { "cache-path", "profile", "owner-sid", "core-store", "content-file", "frame-id" })
            Reject(() => CortexPrivateReadCacheArguments.Parse(valid.Concat(new[] { "--" + option, "forbidden" }).ToArray()));
        Reject(() => CortexPrivateReadCacheArguments.Parse(valid.Concat(new[] { "--task-id", "other" }).ToArray()));
        Reject(() => CortexPrivateReadCacheArguments.Parse(valid.Concat(new[] { "--read-namespace", "knowledge.shared" }).ToArray()));
        Reject(() => CortexPrivateReadCacheArguments.Parse(valid.Concat(new[] { "--expected-generation", "none" }).ToArray()));
        Reject(() => CortexPrivateReadCacheArguments.Parse(Arguments("cache-refresh", root)));
        Reject(() => CortexPrivateReadCacheArguments.Parse(Arguments("cache-refresh", root, new string('A', 64))));
        Reject(() => CortexPrivateReadCacheArguments.Parse(Arguments("cache-refresh", root, new string('a', 63))));
        Reject(() => CortexPrivateReadCacheArguments.Parse(valid.Take(valid.Length - 1).ToArray()));
        Reject(() => CortexPrivateReadCacheArguments.Parse(valid.Where(value => value is not "--project-id" and not "private-cache-fixture").ToArray()));
        var relative = (string[])valid.Clone(); relative[3] = "relative-project";
        Reject(() => CortexPrivateReadCacheArguments.Parse(relative));
        var unsafeTask = (string[])valid.Clone(); unsafeTask[5] = "Task\nIdentity";
        Reject(() => CortexPrivateReadCacheArguments.Parse(unsafeTask));
        var maximumNamespaces = valid.Take(valid.Length - 4).ToList();
        for (var index = 0; index < CortexV2Limits.MaximumNamespaces; index++)
            maximumNamespaces.AddRange(["--read-namespace", index == 0 ? "project.private-cache-fixture" : "knowledge.fixture" + index]);
        Check(CortexPrivateReadCacheArguments.Parse(maximumNamespaces).Request.ReadNamespaces.Count == CortexV2Limits.MaximumNamespaces,
            "The public namespace limit was not admitted.");
        maximumNamespaces.AddRange(["--read-namespace", "knowledge.overflow"]);
        Reject(() => CortexPrivateReadCacheArguments.Parse(maximumNamespaces));
        return Task.CompletedTask;
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("An invalid or foreign argument was accepted.");
    }
}
