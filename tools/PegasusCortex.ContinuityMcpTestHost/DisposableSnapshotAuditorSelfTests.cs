namespace Pegasus.Cortex.ContinuityMcpTestHost;

internal static class DisposableSnapshotAuditorSelfTests
{
    public static int Run()
    {
        ExactNonblankCoveragePasses();
        DuplicateNonblankCoverageFails();
        MissingNonblankCoverageFails();
        UnsafeManifestPathsFail();
        OrphanRecordCountsFail();
        return 5;
    }

    private static void ExactNonblankCoveragePasses()
    {
        const string markdown = "# Heading\n\ncontent\n";
        var chunks = MarkdownContinuityChunker.Chunk(markdown, 128);
        var count = DisposableSnapshotAuditor.CountExactlyOnceCoveredNonblankLines(
            markdown,
            chunks);
        Require(count == 2, "Exact nonblank-line coverage count changed.");
    }

    private static void DuplicateNonblankCoverageFails()
    {
        const string markdown = "# Heading\ncontent";
        var chunks = MarkdownContinuityChunker.Chunk(markdown, 128);
        ExpectFailure(
            () => DisposableSnapshotAuditor.CountExactlyOnceCoveredNonblankLines(
                markdown,
                [chunks[0], chunks[0]]),
            "Duplicate nonblank-line coverage was accepted.");
    }

    private static void MissingNonblankCoverageFails()
    {
        const string markdown = "# Heading\ncontent";
        ExpectFailure(
            () => DisposableSnapshotAuditor.CountExactlyOnceCoveredNonblankLines(
                markdown,
                Array.Empty<MarkdownContinuityChunk>()),
            "Missing nonblank-line coverage was accepted.");
    }

    private static void UnsafeManifestPathsFail()
    {
        ExpectFailure(
            () => DisposableSnapshotAuditor.RequireSafeRelativePath("../snapshot-manifest.json"),
            "Traversal in a manifest relative path was accepted.");
    }

    private static void OrphanRecordCountsFail()
    {
        ExpectFailure(
            () => DisposableSnapshotAuditor.EnsureCompleteRecordConsumption(2, 1),
            "An orphan snapshot record was accepted.");
    }

    private static void ExpectFailure(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
