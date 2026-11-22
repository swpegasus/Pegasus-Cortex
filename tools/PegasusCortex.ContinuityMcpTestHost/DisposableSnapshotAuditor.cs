using System.Text.Json;
using Pegasus.Cortex.DefaultLibrary.Contracts;

namespace Pegasus.Cortex.ContinuityMcpTestHost;

internal sealed record DisposableSnapshotAuditResult(
    int SourceFileCount,
    int RecordCount,
    int NonblankLineCount,
    IReadOnlyDictionary<string, int> TaskStateRecordCounts,
    IReadOnlyDictionary<string, int> TaskStateItemCounts,
    string BundleSha256,
    string ManifestSha256);

/// <summary>
/// Reproduces the disposable snapshot's complete copy, projection, evidence,
/// and task-state audit without relying on a one-off external script.
/// </summary>
internal static class DisposableSnapshotAuditor
{
    private const int MaximumManifestBytes = 2 * 1024 * 1024;

    public static DisposableSnapshotAuditResult Audit(string value)
    {
        var snapshotRoot = ContinuityMcpTestHostProgram.RequireAbsoluteDirectory(value, "--snapshot");
        var manifestPath = ResolveSnapshotFile(snapshotRoot, "snapshot-manifest.json");
        var manifestBytes = ReadBoundedBytes(manifestPath, MaximumManifestBytes, "Snapshot manifest");
        using var manifest = JsonDocument.Parse(
            manifestBytes,
            new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow });
        var root = RequireKind(manifest.RootElement, JsonValueKind.Object, "Snapshot manifest root");

        RequireInt32(root, "schemaVersion", 1);
        RequireString(root, "mode", "disposable-read-only-test-copy");
        var sourceReleaseVersion = RequireString(root, "sourceReleaseVersion");
        var generatedAt = RequireDateTimeOffset(root, "generatedAt");

        var bundleElement = RequireProperty(root, "bundle", JsonValueKind.Object);
        var bundleRelativePath = RequireSafeRelativePath(RequireString(bundleElement, "relativePath"));
        var bundlePath = ResolveSnapshotFile(snapshotRoot, bundleRelativePath);
        var bundleBytes = ReadBoundedBytes(
            bundlePath,
            ContinuityMcpTestHostProgram.MaximumBundleBytes,
            "Continuity bundle");
        var bundle = ContinuityMcpTestHostProgram.LoadBundle(bundlePath);
        ContinuityMcpTestHostProgram.ValidateBundle(bundle);
        VerifyBundleManifest(bundleElement, bundle, bundleBytes, generatedAt);

        var recordsByEvidence = IndexRecordsByEvidence(bundle.Records);
        var consumedRecordIds = new HashSet<string>(StringComparer.Ordinal);
        var aggregateStateRecordCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var aggregateStateItemCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var nonblankLineCount = 0;

        var sourceFiles = RequireProperty(root, "sourceFiles", JsonValueKind.Array);
        if (sourceFiles.GetArrayLength() != ContinuityMcpTestHostProgram.ContinuityPaths.Length)
            throw new InvalidOperationException("Snapshot manifest does not contain the exact continuity file set.");

        for (var index = 0; index < sourceFiles.GetArrayLength(); index++)
        {
            var fileElement = RequireKind(
                sourceFiles[index],
                JsonValueKind.Object,
                $"sourceFiles[{index}]");
            var relativePath = RequireSafeRelativePath(RequireString(fileElement, "relativePath"));
            var expectedPath = ContinuityMcpTestHostProgram.ContinuityPaths[index];
            if (!string.Equals(relativePath, expectedPath, StringComparison.Ordinal))
                throw new InvalidOperationException("Snapshot manifest continuity files are missing, reordered, or unexpected.");

            var copiedPath = ResolveSnapshotFile(snapshotRoot, $"files/{relativePath}");
            var copiedBytes = File.ReadAllBytes(copiedPath);
            RequireInt64(fileElement, "byteLength", copiedBytes.LongLength);
            RequireString(fileElement, "sha256", ContinuityMcpTestHostProgram.Sha256(copiedBytes));

            var projectedText = ContinuityMcpTestHostProgram.CreateSafeProjection(
                ContinuityMcpTestHostProgram.DecodeMarkdown(copiedBytes, relativePath));
            RequireInt32(fileElement, "lineCount", projectedText.Count(character => character == '\n') + 1);
            RequireString(fileElement, "chunking", "markdown-heading-line-span-v1");

            var chunks = MarkdownContinuityChunker.Chunk(
                projectedText,
                ContinuityMcpTestHostProgram.MaximumChunkCharacters);
            var taskStates = MarkdownTaskStateClassifier.Classify(relativePath, projectedText, chunks);
            var manifestChunks = RequireProperty(fileElement, "chunks", JsonValueKind.Array);
            RequireInt32(fileElement, "chunkCount", chunks.Count);
            if (manifestChunks.GetArrayLength() != chunks.Count)
                throw new InvalidOperationException($"Snapshot chunk count disagrees for '{relativePath}'.");

            var fileStateRecordCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var fileStateItemCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                var chunk = chunks[chunkIndex];
                var taskState = taskStates[chunkIndex];
                var chunkNumber = chunkIndex + 1;
                var chunkElement = RequireKind(
                    manifestChunks[chunkIndex],
                    JsonValueKind.Object,
                    $"{relativePath} chunk {chunkNumber}");
                VerifyChunkManifest(chunkElement, chunk, taskState, chunkNumber);

                var evidenceReference = ContinuityMcpTestHostProgram.BuildEvidenceReference(
                    relativePath,
                    chunk,
                    chunkNumber);
                if (!recordsByEvidence.TryGetValue(evidenceReference, out var record))
                    throw new InvalidOperationException($"Snapshot has no record for '{relativePath}' chunk {chunkNumber}.");
                if (!consumedRecordIds.Add(record.RecordId))
                    throw new InvalidOperationException("A snapshot record was consumed by more than one source chunk.");
                VerifyRecord(
                    record,
                    relativePath,
                    sourceReleaseVersion,
                    RequireDateTimeOffset(fileElement, "observedAt"),
                    chunk,
                    taskState,
                    chunkNumber,
                    evidenceReference);

                if (taskState is not null)
                {
                    Increment(fileStateRecordCounts, taskState.State, 1);
                    Increment(fileStateItemCounts, taskState.State, taskState.TaskItemCount);
                    Increment(aggregateStateRecordCounts, taskState.State, 1);
                    Increment(aggregateStateItemCounts, taskState.State, taskState.TaskItemCount);
                }
            }

            VerifyStateMap(
                RequireProperty(fileElement, "taskStateRecordCounts", JsonValueKind.Object),
                fileStateRecordCounts,
                $"{relativePath} task-state record counts");
            VerifyStateMap(
                RequireProperty(fileElement, "taskStateItemCounts", JsonValueKind.Object),
                fileStateItemCounts,
                $"{relativePath} task-state item counts");
            nonblankLineCount = checked(
                nonblankLineCount +
                CountExactlyOnceCoveredNonblankLines(projectedText, chunks));
        }

        EnsureCompleteRecordConsumption(bundle.Records.Count, consumedRecordIds.Count);
        VerifyBoundaries(RequireProperty(root, "boundaries", JsonValueKind.Object));

        return new DisposableSnapshotAuditResult(
            ContinuityMcpTestHostProgram.ContinuityPaths.Length,
            bundle.Records.Count,
            nonblankLineCount,
            SortedCopy(aggregateStateRecordCounts),
            SortedCopy(aggregateStateItemCounts),
            ContinuityMcpTestHostProgram.Sha256(bundleBytes),
            ContinuityMcpTestHostProgram.Sha256(manifestBytes));
    }

    internal static int CountExactlyOnceCoveredNonblankLines(
        string text,
        IReadOnlyList<MarkdownContinuityChunk> chunks)
    {
        var lines = text.Split('\n');
        var coverage = new int[lines.Length];
        foreach (var chunk in chunks)
        {
            if (chunk.LineStart < 1 || chunk.LineEnd < chunk.LineStart || chunk.LineEnd > lines.Length)
                throw new InvalidOperationException("A chunk line span is outside its copied source.");
            for (var lineNumber = chunk.LineStart; lineNumber <= chunk.LineEnd; lineNumber++)
            {
                if (!string.IsNullOrWhiteSpace(lines[lineNumber - 1]))
                    coverage[lineNumber - 1] = checked(coverage[lineNumber - 1] + 1);
            }
        }

        var nonblankLineCount = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
                continue;
            nonblankLineCount++;
            if (coverage[index] != 1)
                throw new InvalidOperationException(
                    $"A nonblank copied source line has coverage {coverage[index]} instead of exactly one.");
        }
        return nonblankLineCount;
    }

    internal static string RequireSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.IndexOf('\0') >= 0 ||
            Path.IsPathRooted(value) ||
            value.Split('/', '\\').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidOperationException("Snapshot manifest contains an unsafe relative path.");
        }
        return value.Replace('\\', '/');
    }

    internal static void EnsureCompleteRecordConsumption(int recordCount, int consumedCount)
    {
        if (recordCount != consumedCount)
            throw new InvalidOperationException("Snapshot contains an orphan or missing continuity record.");
    }

    private static void VerifyBundleManifest(
        JsonElement element,
        KnowledgeSeedBundleV1 bundle,
        byte[] bundleBytes,
        DateTimeOffset generatedAt)
    {
        RequireString(element, "ReleaseVersion", bundle.ReleaseVersion);
        RequireString(element, "NuGetVersion", bundle.NuGetVersion);
        RequireString(element, "BundleId", bundle.BundleId);
        RequireString(element, "BundleHash", bundle.BundleHash);
        RequireInt32(element, "recordCount", bundle.Records.Count);
        RequireInt64(element, "byteLength", bundleBytes.LongLength);
        RequireString(element, "sha256", ContinuityMcpTestHostProgram.Sha256(bundleBytes));
        if (bundle.GeneratedAt != generatedAt)
            throw new InvalidOperationException("Snapshot manifest and bundle generation times disagree.");
    }

    private static Dictionary<string, KnowledgeRecordV1> IndexRecordsByEvidence(
        IReadOnlyList<KnowledgeRecordV1> records)
    {
        var recordIds = new HashSet<string>(StringComparer.Ordinal);
        var revisionIds = new HashSet<string>(StringComparer.Ordinal);
        var byEvidence = new Dictionary<string, KnowledgeRecordV1>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (!recordIds.Add(record.RecordId) || !revisionIds.Add(record.RevisionId))
                throw new InvalidOperationException("Snapshot contains a duplicate record or revision identity.");
            if (record.Evidence.Count != 1 || !byEvidence.TryAdd(record.Evidence[0].Reference, record))
                throw new InvalidOperationException("Snapshot evidence references are not unique one-to-one mappings.");
        }
        return byEvidence;
    }

    private static void VerifyChunkManifest(
        JsonElement element,
        MarkdownContinuityChunk chunk,
        ContinuityTaskStateProjection? taskState,
        int chunkNumber)
    {
        RequireInt32(element, "number", chunkNumber);
        RequireInt32(element, "LineStart", chunk.LineStart);
        RequireInt32(element, "LineEnd", chunk.LineEnd);
        RequireString(element, "HeadingIdentity", chunk.HeadingIdentity);
        RequireString(element, "HeadingTitle", chunk.HeadingTitle);
        RequireNullableString(element, "taskState", taskState?.State);
        RequireNullableString(element, "taskStateComponentId", taskState?.ComponentId);
        RequireInt32(element, "taskItemCount", taskState?.TaskItemCount ?? 0);
        RequireInt32(element, "taskSectionItemCount", taskState?.SectionTaskItemCount ?? 0);

        var projectedContent = ContinuityMcpTestHostProgram.NormalizeProjectedContent(chunk.Content);
        RequireInt32(element, "sourceSliceCharacterCount", chunk.Content.Length);
        RequireInt32(element, "characterCount", projectedContent.Length);
        RequireString(
            element,
            "sha256",
            ContinuityMcpTestHostProgram.Sha256(
                ContinuityMcpTestHostProgram.StrictUtf8.GetBytes(projectedContent)));
    }

    private static void VerifyRecord(
        KnowledgeRecordV1 record,
        string relativePath,
        string sourceReleaseVersion,
        DateTimeOffset observedAt,
        MarkdownContinuityChunk chunk,
        ContinuityTaskStateProjection? taskState,
        int chunkNumber,
        string evidenceReference)
    {
        var projectedContent = ContinuityMcpTestHostProgram.NormalizeProjectedContent(chunk.Content);
        var projectedHash = ContinuityMcpTestHostProgram.Sha256(
            ContinuityMcpTestHostProgram.StrictUtf8.GetBytes(projectedContent));
        var fileName = Path.GetFileName(relativePath);
        var expectedSlug =
            $"{ContinuityMcpTestHostProgram.Slug(relativePath)}-{chunk.HeadingIdentity}" +
            $"-lines-{chunk.LineStart:D6}-{chunk.LineEnd:D6}-part-{chunkNumber:D4}";
        var expectedSummary = taskState is null
            ? $"Bounded normalized lines {chunk.LineStart}-{chunk.LineEnd} under {chunk.HeadingTitle} from the hash-verified public continuity copy of {relativePath}."
            : $"Public task state {taskState.State}; bounded normalized lines {chunk.LineStart}-{chunk.LineEnd} under {chunk.HeadingTitle} from the hash-verified public continuity copy of {relativePath}.";
        var expectedKeywords = new HashSet<string>(
        [
            "continuity",
            "pegasus-cortex",
            fileName.ToLowerInvariant(),
            ContinuityMcpTestHostProgram.Slug(relativePath),
            chunk.HeadingIdentity
        ], StringComparer.Ordinal);
        var expectedTransformations = new HashSet<string>(
        [
            "markdown-heading-line-span-v1",
            $"source-lines-{chunk.LineStart:D6}-{chunk.LineEnd:D6}",
            $"heading-{chunk.HeadingIdentity}",
            "copied-without-source-mutation",
            "descriptive-prohibited-marker-normalized",
            "outer-whitespace-trimmed",
            "utf8-normalized"
        ], StringComparer.Ordinal);
        if (taskState is not null)
        {
            expectedKeywords.Add("task-state");
            expectedKeywords.Add(taskState.Keyword);
            expectedTransformations.Add("markdown-task-section-state-v1");
            expectedTransformations.Add(taskState.Keyword);
        }

        RequireEqual(record.Namespace, ContinuityMcpTestHostProgram.TestNamespace, "record namespace");
        RequireEqual(record.Taxonomy, ContinuityMcpTestHostProgram.Taxonomy, "record taxonomy");
        RequireEqual(record.Slug, expectedSlug, "record slug");
        RequireEqual(
            record.Title,
            $"Continuity: {fileName} - {chunk.HeadingTitle} (lines {chunk.LineStart}-{chunk.LineEnd})",
            "record title");
        RequireEqual(record.Summary, expectedSummary, "record summary");
        RequireEqual(record.Content, projectedContent, "record content");
        if (!expectedKeywords.SetEquals(record.Keywords))
            throw new InvalidOperationException("Snapshot record keywords disagree with copied source structure.");
        if (record.Applicability.Count != 1)
            throw new InvalidOperationException("Snapshot record applicability is not singular.");
        var applicability = record.Applicability[0];
        RequireEqual(applicability.ProductId, "pegasus-cortex", "record applicability product");
        RequireEqual(
            applicability.ComponentId,
            taskState?.ComponentId ?? "project-continuity",
            "record applicability component");
        RequireEqual(applicability.State, "verified", "record applicability state");

        var evidence = record.Evidence.Single();
        RequireEqual(evidence.Reference, evidenceReference, "record evidence reference");
        RequireEqual(evidence.MediaType, "text/markdown", "record evidence media type");
        RequireEqual(evidence.Sha256, projectedHash, "record evidence hash");
        RequireEqual(evidence.Verification, "sha256-verified-line-projection", "record evidence verification");
        RequireEqual(evidence.SourceVersion, sourceReleaseVersion, "record evidence source version");
        if (evidence.ObservedAt != observedAt)
            throw new InvalidOperationException("Snapshot record observed time disagrees with its copied file manifest.");

        RequireEqual(
            record.Provenance.ProducerContract,
            ContinuityMcpTestHostProgram.ProducerContract,
            "record producer contract");
        if (record.Provenance.SourceRecordIds.Count != 1 ||
            !string.Equals(record.Provenance.SourceRecordIds[0], evidenceReference, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Snapshot record provenance does not bind its exact evidence reference.");
        }
        if (!expectedTransformations.SetEquals(record.Provenance.Transformations))
            throw new InvalidOperationException("Snapshot record transformations disagree with its projection.");
    }

    private static int Count(JsonElement element, string name) =>
        RequireProperty(element, name, JsonValueKind.Number).GetInt32();

    private static void VerifyStateMap(
        JsonElement element,
        IReadOnlyDictionary<string, int> expected,
        string description)
    {
        var actual = element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.GetInt32(),
            StringComparer.Ordinal);
        if (actual.Count != expected.Count ||
            expected.Any(item => !actual.TryGetValue(item.Key, out var value) || value != item.Value))
        {
            throw new InvalidOperationException($"Snapshot manifest {description} disagree.");
        }
    }

    private static void VerifyBoundaries(JsonElement element)
    {
        RequireBoolean(element, "sourceContinuityMutated", false);
        RequireBoolean(element, "exactSourceCopiesPreserved", true);
        RequireBoolean(element, "descriptiveProhibitedMarkerNormalizedInProjection", true);
        RequireBoolean(element, "productionRuntimeDependency", false);
        RequireBoolean(element, "networkAccess", false);
        RequireBoolean(element, "databaseAccess", false);
        RequireBoolean(element, "writableMcpTools", false);
    }

    private static string ResolveSnapshotFile(string snapshotRoot, string relativePath)
    {
        var safeRelativePath = RequireSafeRelativePath(relativePath);
        var path = ContinuityMcpTestHostProgram.ResolveContainedFile(snapshotRoot, safeRelativePath);
        EnsurePathChainHasNoReparsePoint(snapshotRoot, path);
        ContinuityMcpTestHostProgram.EnsurePathBudget(path);
        return path;
    }

    private static void EnsurePathChainHasNoReparsePoint(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        ContinuityMcpTestHostProgram.EnsureNoReparsePoint(current);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            ContinuityMcpTestHostProgram.EnsureNoReparsePoint(current);
        }
    }

    private static byte[] ReadBoundedBytes(string path, int maximumBytes, string description)
    {
        var length = new FileInfo(path).Length;
        if (length is <= 0 || length > maximumBytes)
            throw new InvalidOperationException($"{description} size is outside the audit boundary.");
        return File.ReadAllBytes(path);
    }

    private static JsonElement RequireProperty(JsonElement element, string name, JsonValueKind kind)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new InvalidOperationException($"Snapshot manifest property '{name}' is missing or invalid.");
        return value;
    }

    private static JsonElement RequireKind(JsonElement element, JsonValueKind kind, string description)
    {
        if (element.ValueKind != kind)
            throw new InvalidOperationException($"{description} is not a {kind}.");
        return element;
    }

    private static string RequireString(JsonElement element, string name)
    {
        var value = RequireProperty(element, name, JsonValueKind.String).GetString();
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException($"Snapshot manifest property '{name}' is empty.");
        return value;
    }

    private static void RequireString(JsonElement element, string name, string expected) =>
        RequireEqual(RequireString(element, name), expected, $"manifest property '{name}'");

    private static void RequireNullableString(JsonElement element, string name, string? expected)
    {
        if (!element.TryGetProperty(name, out var value))
            throw new InvalidOperationException($"Snapshot manifest property '{name}' is missing.");
        var actual = value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new InvalidOperationException($"Snapshot manifest property '{name}' is invalid.")
        };
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Snapshot manifest property '{name}' disagrees.");
    }

    private static DateTimeOffset RequireDateTimeOffset(JsonElement element, string name)
    {
        var value = RequireString(element, name);
        if (!DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var result))
        {
            throw new InvalidOperationException($"Snapshot manifest property '{name}' is not a round-trip timestamp.");
        }
        return result;
    }

    private static void RequireInt32(JsonElement element, string name, int expected)
    {
        if (Count(element, name) != expected)
            throw new InvalidOperationException($"Snapshot manifest property '{name}' disagrees.");
    }

    private static void RequireInt64(JsonElement element, string name, long expected)
    {
        var value = RequireProperty(element, name, JsonValueKind.Number);
        if (!value.TryGetInt64(out var actual) || actual != expected)
            throw new InvalidOperationException($"Snapshot manifest property '{name}' disagrees.");
    }

    private static void RequireBoolean(JsonElement element, string name, bool expected)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            value.GetBoolean() != expected)
        {
            throw new InvalidOperationException($"Snapshot manifest boundary '{name}' disagrees.");
        }
    }

    private static void RequireEqual(string? actual, string? expected, string description)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Snapshot {description} disagrees.");
    }

    private static void Increment(IDictionary<string, int> values, string key, int amount)
    {
        values.TryGetValue(key, out var current);
        values[key] = checked(current + amount);
    }

    private static IReadOnlyDictionary<string, int> SortedCopy(IReadOnlyDictionary<string, int> values) =>
        values.OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
}
