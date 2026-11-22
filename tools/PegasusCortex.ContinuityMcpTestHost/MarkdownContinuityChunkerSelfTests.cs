namespace Pegasus.Cortex.ContinuityMcpTestHost;

internal static class MarkdownContinuityChunkerSelfTests
{
    public static int Run()
    {
        HeadingAndFenceBoundaries();
        LongSectionSplitsDeterministically();
        OversizedLineFailsClosed();
        return 3;
    }

    private static void HeadingAndFenceBoundaries()
    {
        const string markdown =
            "\n" +
            "\n" +
            "# Root\n" +
            "root text\n" +
            "\n" +
            "## First\n" +
            "first text\n" +
            "```text\n" +
            "# fenced lookalike\n" +
            "```\n" +
            "\n" +
            "## Second\n" +
            "second text";

        var chunks = MarkdownContinuityChunker.Chunk(markdown, 3000);
        Require(chunks.Count == 3, "Heading test did not create exactly three semantic chunks.");
        Require(
            chunks[0].LineStart == 3 && chunks[0].LineEnd == 4 &&
            chunks[0].HeadingTitle == "Root",
            "Root heading line span was not exact.");
        Require(
            chunks[1].LineStart == 6 && chunks[1].LineEnd == 10 &&
            chunks[1].HeadingTitle == "First" &&
            chunks[1].Content.Contains("# fenced lookalike", StringComparison.Ordinal),
            "Fenced heading lookalike changed section boundaries.");
        Require(
            chunks[2].LineStart == 12 && chunks[2].LineEnd == 13 &&
            chunks[2].HeadingTitle == "Second",
            "Second heading line span was not exact.");
    }

    private static void LongSectionSplitsDeterministically()
    {
        var markdown = string.Join(
            '\n',
            "## Large",
            new string('a', 1200),
            new string('b', 1200),
            new string('c', 1200));
        var first = MarkdownContinuityChunker.Chunk(markdown, 3000);
        var second = MarkdownContinuityChunker.Chunk(markdown, 3000);

        Require(first.SequenceEqual(second), "Chunking was not deterministic.");
        Require(first.Count == 2, "Long section did not split into two bounded chunks.");
        Require(
            first[0].LineStart == 1 && first[0].LineEnd == 3 &&
            first[1].LineStart == 4 && first[1].LineEnd == 4,
            "Long-section line spans were not exact and contiguous.");
        Require(
            first.All(chunk => chunk.Content.Length <= 3000) &&
            first.Select(chunk => chunk.HeadingIdentity).Distinct(StringComparer.Ordinal).Count() == 1,
            "Long-section chunks lost the bound or heading identity.");

        var lines = markdown.Split('\n');
        foreach (var chunk in first)
        {
            var exactSlice = string.Join(
                '\n',
                lines[(chunk.LineStart - 1)..chunk.LineEnd]);
            Require(
                chunk.Content == exactSlice,
                "A chunk was not the exact normalized source-line slice it advertises.");
        }
    }

    private static void OversizedLineFailsClosed()
    {
        var markdown = $"## Huge\n{new string('x', 3001)}";
        try
        {
            _ = MarkdownContinuityChunker.Chunk(markdown, 3000);
        }
        catch (InvalidOperationException exception)
            when (exception.Message.Contains("Markdown line 2", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException("An oversized Markdown line did not fail closed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
