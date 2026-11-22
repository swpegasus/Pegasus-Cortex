namespace Pegasus.Cortex.ContinuityMcpTestHost;

internal static class MarkdownTaskStateClassifierSelfTests
{
    public static int Run()
    {
        KnownSectionsAndContinuationChunks();
        FencedLookalikesAreIgnored();
        UnknownSectionsRemainUnclassified();
        ContradictoryMarkersFailClosed();
        return 4;
    }

    private static void KnownSectionsAndContinuationChunks()
    {
        var markdown = string.Join(
            '\n',
            "# Tasks",
            "## Active",
            "- [ ] First active item",
            new string('a', 90),
            new string('b', 90),
            "## Blocked",
            "- [ ] Blocked item",
            "  blocked continuation",
            "## Done",
            "- [x] Completed item");
        var chunks = MarkdownContinuityChunker.Chunk(markdown, 128);
        var projections = MarkdownTaskStateClassifier.Classify(
            ".cortex/TASKS.md",
            markdown,
            chunks);

        var active = projections
            .Where((projection, index) =>
                chunks[index].HeadingTitle == "Active" && projection is not null)
            .Select(projection => projection!)
            .ToArray();
        Require(active.Length >= 2, "Active continuation chunks did not retain task state.");
        Require(active.All(item => item.State == "active"), "Active state was not deterministic.");
        Require(active.Sum(item => item.TaskItemCount) == 1, "Active marker count changed.");
        Require(active.All(item => item.SectionTaskItemCount == 1), "Active section count changed.");
        Require(active.Any(item => item.TaskItemCount == 0), "Continuation-only active chunk was not covered.");

        var blocked = SingleProjection(chunks, projections, "Blocked");
        Require(
            blocked.State == "blocked" &&
            blocked.ComponentId == "project-tasks-blocked",
            "Blocked task metadata was not exact.");
        var done = SingleProjection(chunks, projections, "Done");
        Require(
            done.State == "done" &&
            done.Keyword == "task-state-done" &&
            done.TaskItemCount == 1,
            "Done task metadata was not exact.");
    }

    private static void FencedLookalikesAreIgnored()
    {
        const string markdown =
            "# Tasks\n" +
            "## Active\n" +
            "```text\n" +
            "- [x] fenced lookalike\n" +
            "```\n" +
            "- [ ] real active item";
        var chunks = MarkdownContinuityChunker.Chunk(markdown, 3000);
        var projections = MarkdownTaskStateClassifier.Classify(
            ".cortex/TASKS.md",
            markdown,
            chunks);
        var active = SingleProjection(chunks, projections, "Active");
        Require(
            active.TaskItemCount == 1 && active.SectionTaskItemCount == 1,
            "A fenced task-marker lookalike changed the active count.");
    }

    private static void UnknownSectionsRemainUnclassified()
    {
        const string markdown =
            "# Tasks\n" +
            "## Later\n" +
            "- [ ] deferred item";
        var chunks = MarkdownContinuityChunker.Chunk(markdown, 3000);
        var projections = MarkdownTaskStateClassifier.Classify(
            ".cortex/TASKS.md",
            markdown,
            chunks);
        Require(
            projections.All(item => item is null),
            "An unknown task section received lifecycle metadata.");

        var otherFile = MarkdownTaskStateClassifier.Classify(
            ".cortex/STATE.md",
            markdown,
            chunks);
        Require(
            otherFile.All(item => item is null),
            "A non-task continuity file received task lifecycle metadata.");
    }

    private static void ContradictoryMarkersFailClosed()
    {
        const string markdown =
            "# Tasks\n" +
            "## Done\n" +
            "- [ ] incomplete item";
        var chunks = MarkdownContinuityChunker.Chunk(markdown, 3000);
        try
        {
            _ = MarkdownTaskStateClassifier.Classify(
                ".cortex/TASKS.md",
                markdown,
                chunks);
        }
        catch (InvalidOperationException exception)
            when (exception.Message.Contains("contradicts", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            "A contradictory task marker did not fail closed.");
    }

    private static ContinuityTaskStateProjection SingleProjection(
        IReadOnlyList<MarkdownContinuityChunk> chunks,
        IReadOnlyList<ContinuityTaskStateProjection?> projections,
        string heading) =>
        projections
            .Where((projection, index) =>
                chunks[index].HeadingTitle == heading && projection is not null)
            .Select(projection => projection!)
            .Single();

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
