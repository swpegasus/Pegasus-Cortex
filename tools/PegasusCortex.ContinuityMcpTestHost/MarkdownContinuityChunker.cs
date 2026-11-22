using System.Security.Cryptography;
using System.Text;

namespace Pegasus.Cortex.ContinuityMcpTestHost;

internal sealed record MarkdownContinuityChunk(
    string Content,
    int LineStart,
    int LineEnd,
    string HeadingIdentity,
    string HeadingTitle,
    int HeadingLevel);

internal static class MarkdownContinuityChunker
{
    private const int MaximumHeadingSlugCharacters = 32;
    private const int MaximumHeadingTitleCharacters = 72;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IReadOnlyList<MarkdownContinuityChunk> Chunk(
        string text,
        int maximumCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (maximumCharacters < 128)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        if (text.Contains('\r', StringComparison.Ordinal))
            throw new InvalidOperationException("Markdown chunking requires normalized LF line endings.");

        var lines = text.Split('\n');
        var sections = FindSections(lines);
        var chunks = new List<MarkdownContinuityChunk>();
        foreach (var section in sections)
            AddSectionChunks(lines, section, maximumCharacters, chunks);

        if (chunks.Count == 0)
            throw new InvalidOperationException("A continuity file produced no bounded content.");
        return chunks;
    }

    private static IReadOnlyList<MarkdownSection> FindSections(string[] lines)
    {
        var headings = new List<MarkdownHeading>();
        var inFence = false;
        var fenceMarker = '\0';
        var fenceLength = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            if (TryReadFence(lines[index], out var marker, out var markerLength, out var canClose))
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

            if (!inFence &&
                TryReadHeading(lines[index], out var level, out var title))
            {
                headings.Add(new MarkdownHeading(
                    index,
                    level,
                    DisplayHeading(title),
                    HeadingIdentity(level, title)));
            }
        }

        if (headings.Count == 0)
        {
            return
            [
                new MarkdownSection(
                    0,
                    lines.Length,
                    0,
                    "Document",
                    "document")
            ];
        }

        var sections = new List<MarkdownSection>(headings.Count + 1);
        if (headings[0].LineIndex != 0)
        {
            sections.Add(new MarkdownSection(
                0,
                headings[0].LineIndex,
                0,
                "Document preamble",
                "document-preamble"));
        }

        for (var index = 0; index < headings.Count; index++)
        {
            var heading = headings[index];
            sections.Add(new MarkdownSection(
                heading.LineIndex,
                index + 1 < headings.Count ? headings[index + 1].LineIndex : lines.Length,
                heading.Level,
                heading.Title,
                heading.Identity));
        }
        return sections;
    }

    private static void AddSectionChunks(
        string[] lines,
        MarkdownSection section,
        int maximumCharacters,
        ICollection<MarkdownContinuityChunk> chunks)
    {
        var sectionStart = section.LineStart;
        var sectionEnd = section.LineEndExclusive - 1;
        while (sectionStart <= sectionEnd && string.IsNullOrWhiteSpace(lines[sectionStart]))
            sectionStart++;
        while (sectionEnd >= sectionStart && string.IsNullOrWhiteSpace(lines[sectionEnd]))
            sectionEnd--;
        if (sectionStart > sectionEnd)
            return;

        var cursor = sectionStart;
        while (cursor <= sectionEnd)
        {
            while (cursor <= sectionEnd && string.IsNullOrWhiteSpace(lines[cursor]))
                cursor++;
            if (cursor > sectionEnd)
                break;
            if (lines[cursor].Length > maximumCharacters)
                throw OversizedLine(cursor, maximumCharacters);

            var candidateEnd = cursor;
            var characterCount = lines[cursor].Length;
            var lastBlankLine = -1;
            for (var index = cursor + 1; index <= sectionEnd; index++)
            {
                if (lines[index].Length > maximumCharacters)
                    throw OversizedLine(index, maximumCharacters);
                var nextLength = checked(characterCount + 1 + lines[index].Length);
                if (nextLength > maximumCharacters)
                    break;
                characterCount = nextLength;
                candidateEnd = index;
                if (string.IsNullOrWhiteSpace(lines[index]))
                    lastBlankLine = index;
            }

            var chunkEnd = candidateEnd;
            if (candidateEnd < sectionEnd &&
                lastBlankLine > cursor &&
                RangeLength(lines, cursor, lastBlankLine - 1) >= maximumCharacters / 2)
            {
                chunkEnd = lastBlankLine - 1;
            }
            while (chunkEnd > cursor && string.IsNullOrWhiteSpace(lines[chunkEnd]))
                chunkEnd--;

            var content = string.Join('\n', lines[cursor..(chunkEnd + 1)]);
            if (content.Length == 0 || content.Length > maximumCharacters)
                throw new InvalidOperationException("Markdown chunking violated its bounded content contract.");
            chunks.Add(new MarkdownContinuityChunk(
                content,
                cursor + 1,
                chunkEnd + 1,
                section.HeadingIdentity,
                section.HeadingTitle,
                section.HeadingLevel));
            cursor = candidateEnd == chunkEnd ? chunkEnd + 1 : lastBlankLine + 1;
        }
    }

    private static int RangeLength(string[] lines, int start, int end)
    {
        var length = 0;
        for (var index = start; index <= end; index++)
            length = checked(length + lines[index].Length + (index == start ? 0 : 1));
        return length;
    }

    private static InvalidOperationException OversizedLine(int zeroBasedLine, int maximumCharacters) =>
        new(
            $"Markdown line {zeroBasedLine + 1} exceeds the {maximumCharacters}-character chunk boundary.");

    private static bool TryReadHeading(
        string line,
        out int level,
        out string title)
    {
        level = 0;
        title = string.Empty;
        var offset = 0;
        while (offset < line.Length && offset < 3 && line[offset] == ' ')
            offset++;
        while (offset + level < line.Length && level < 6 && line[offset + level] == '#')
            level++;
        if (level == 0)
            return false;

        var contentStart = offset + level;
        if (contentStart < line.Length && line[contentStart] is not (' ' or '\t'))
            return false;
        title = contentStart == line.Length ? string.Empty : line[contentStart..].Trim();
        if (title.Length != 0)
        {
            var lastContent = title.Length - 1;
            while (lastContent >= 0 && title[lastContent] == '#')
                lastContent--;
            if (lastContent >= 0 && char.IsWhiteSpace(title[lastContent]))
                title = title[..(lastContent + 1)].TrimEnd();
        }
        return true;
    }

    internal static bool TryReadFence(
        string line,
        out char marker,
        out int length,
        out bool canClose)
    {
        marker = '\0';
        length = 0;
        canClose = false;
        var offset = 0;
        while (offset < line.Length && offset < 3 && line[offset] == ' ')
            offset++;
        if (offset >= line.Length || line[offset] is not ('`' or '~'))
            return false;

        marker = line[offset];
        while (offset + length < line.Length && line[offset + length] == marker)
            length++;
        if (length < 3)
            return false;
        canClose = line[(offset + length)..].All(char.IsWhiteSpace);
        return true;
    }

    private static string DisplayHeading(string title)
    {
        var normalized = string.Join(
            ' ',
            title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
            return "Untitled heading";
        if (normalized.Length <= MaximumHeadingTitleCharacters)
            return normalized;
        return $"{normalized[..(MaximumHeadingTitleCharacters - 13)]}-{ShortHash(normalized)}";
    }

    private static string HeadingIdentity(int level, string title)
    {
        var builder = new StringBuilder(Math.Min(title.Length, MaximumHeadingSlugCharacters));
        var lastWasDash = false;
        foreach (var character in title.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (builder.Length == MaximumHeadingSlugCharacters)
                    break;
                builder.Append(character);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length != 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0
            ? $"h{level}-{ShortHash(title)}"
            : $"h{level}-{slug}-{ShortHash(title)}";
    }

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(value)))
            .ToLowerInvariant()[..12];

    private sealed record MarkdownHeading(
        int LineIndex,
        int Level,
        string Title,
        string Identity);

    private sealed record MarkdownSection(
        int LineStart,
        int LineEndExclusive,
        int HeadingLevel,
        string HeadingTitle,
        string HeadingIdentity);
}
