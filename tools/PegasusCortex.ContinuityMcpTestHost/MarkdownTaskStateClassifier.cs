namespace Pegasus.Cortex.ContinuityMcpTestHost;

internal sealed record ContinuityTaskStateProjection(
    string State,
    string Keyword,
    string ComponentId,
    int TaskItemCount,
    int SectionTaskItemCount);

/// <summary>
/// Adds bounded lifecycle metadata only to real task-list sections. Markdown
/// content and line spans remain untouched; the result is projection metadata.
/// </summary>
internal static class MarkdownTaskStateClassifier
{
    private const string TasksPath = ".cortex/TASKS.md";

    public static IReadOnlyList<ContinuityTaskStateProjection?> Classify(
        string relativePath,
        string text,
        IReadOnlyList<MarkdownContinuityChunk> chunks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentNullException.ThrowIfNull(chunks);

        var projections = new ContinuityTaskStateProjection?[chunks.Count];
        if (!string.Equals(relativePath.Replace('\\', '/'), TasksPath, StringComparison.Ordinal))
            return projections;
        if (text.Contains('\r', StringComparison.Ordinal))
            throw new InvalidOperationException("Task-state classification requires normalized LF line endings.");

        var localUnchecked = new int[chunks.Count];
        var localCompleted = new int[chunks.Count];
        CountTaskMarkers(text, chunks, localUnchecked, localCompleted);

        // A long task item can span more than one bounded chunk. Classify each
        // contiguous chunk run under the same real lifecycle heading so its
        // continuation-only chunks retain the section's state.
        var runStart = 0;
        while (runStart < chunks.Count)
        {
            var runEnd = runStart + 1;
            while (runEnd < chunks.Count &&
                   string.Equals(
                       chunks[runStart].HeadingIdentity,
                       chunks[runEnd].HeadingIdentity,
                       StringComparison.Ordinal))
            {
                runEnd++;
            }

            if (TryMapState(chunks[runStart], out var state, out var completedExpected))
            {
                var uncheckedCount = 0;
                var completedCount = 0;
                for (var index = runStart; index < runEnd; index++)
                {
                    uncheckedCount = checked(uncheckedCount + localUnchecked[index]);
                    completedCount = checked(completedCount + localCompleted[index]);
                }

                var sectionTaskCount = checked(uncheckedCount + completedCount);
                if (sectionTaskCount != 0)
                {
                    if ((completedExpected && uncheckedCount != 0) ||
                        (!completedExpected && completedCount != 0))
                    {
                        throw new InvalidOperationException(
                            "A task marker contradicts its public lifecycle section.");
                    }

                    for (var index = runStart; index < runEnd; index++)
                    {
                        projections[index] = new ContinuityTaskStateProjection(
                            state!,
                            $"task-state-{state}",
                            $"project-tasks-{state}",
                            checked(localUnchecked[index] + localCompleted[index]),
                            sectionTaskCount);
                    }
                }
            }

            runStart = runEnd;
        }

        return projections;
    }

    private static void CountTaskMarkers(
        string text,
        IReadOnlyList<MarkdownContinuityChunk> chunks,
        int[] localUnchecked,
        int[] localCompleted)
    {
        var lines = text.Split('\n');
        var inFence = false;
        var fenceMarker = '\0';
        var fenceLength = 0;
        var chunkIndex = 0;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (MarkdownContinuityChunker.TryReadFence(
                    lines[lineIndex],
                    out var marker,
                    out var markerLength,
                    out var canClose))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceMarker = marker;
                    fenceLength = markerLength;
                }
                else if (canClose && marker == fenceMarker && markerLength >= fenceLength)
                {
                    inFence = false;
                }
                continue;
            }

            if (inFence || !TryReadTaskMarker(lines[lineIndex], out var completed))
                continue;

            var oneBasedLine = lineIndex + 1;
            while (chunkIndex < chunks.Count && chunks[chunkIndex].LineEnd < oneBasedLine)
                chunkIndex++;
            if (chunkIndex == chunks.Count ||
                chunks[chunkIndex].LineStart > oneBasedLine)
            {
                throw new InvalidOperationException(
                    "A public task marker was not covered by an exact continuity chunk.");
            }

            if (completed)
                localCompleted[chunkIndex] = checked(localCompleted[chunkIndex] + 1);
            else
                localUnchecked[chunkIndex] = checked(localUnchecked[chunkIndex] + 1);
        }
    }

    private static bool TryMapState(
        MarkdownContinuityChunk chunk,
        out string? state,
        out bool completedExpected)
    {
        state = null;
        completedExpected = false;
        if (chunk.HeadingLevel != 2)
            return false;

        if (string.Equals(chunk.HeadingTitle, "Active", StringComparison.OrdinalIgnoreCase))
        {
            state = "active";
            return true;
        }
        if (string.Equals(chunk.HeadingTitle, "Blocked", StringComparison.OrdinalIgnoreCase))
        {
            state = "blocked";
            return true;
        }
        if (string.Equals(chunk.HeadingTitle, "Done", StringComparison.OrdinalIgnoreCase))
        {
            state = "done";
            completedExpected = true;
            return true;
        }
        return false;
    }

    private static bool TryReadTaskMarker(string line, out bool completed)
    {
        completed = false;
        var offset = 0;
        while (offset < line.Length && offset < 3 && line[offset] == ' ')
            offset++;
        if (offset + 5 > line.Length ||
            line[offset] is not ('-' or '*' or '+') ||
            line[offset + 1] is not (' ' or '\t') ||
            line[offset + 2] != '[' ||
            line[offset + 4] != ']')
        {
            return false;
        }

        var marker = line[offset + 3];
        if (marker is not (' ' or 'x' or 'X'))
            return false;
        if (offset + 5 < line.Length && !char.IsWhiteSpace(line[offset + 5]))
            return false;

        completed = marker is 'x' or 'X';
        return true;
    }
}
