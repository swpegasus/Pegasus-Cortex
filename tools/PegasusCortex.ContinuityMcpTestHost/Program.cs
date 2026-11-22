using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.DefaultLibrary.Contracts;
using Pegasus.Cortex.DefaultLibrary.Serialization;
using Pegasus.Cortex.DefaultLibrary.Validation;
using Pegasus.Cortex.ContinuityMcpTestHost;
using Pegasus.Cortex.Mcp.Knowledge;
using Pegasus.Cortex.Mcp.Protocol;

return await ContinuityMcpTestHostProgram.RunAsync(args).ConfigureAwait(false);

internal static class ContinuityMcpTestHostProgram
{
    internal const string TestNamespace = "local:pegasus-continuity-test";
    internal const string Taxonomy = "project-continuity";
    internal const string ProducerContract = "pegasus.cortex.continuity-test-copy/1.0";
    internal const int MaximumBundleBytes = 8 * 1024 * 1024;
    internal const int MaximumChunkCharacters = 3000;
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static readonly string[] ContinuityPaths =
    [
        "START_HERE.md",
        ".cortex/README.md",
        ".cortex/STATE.md",
        ".cortex/WORKLOG.md",
        ".cortex/LEARNINGS.md",
        ".cortex/SESSIONS.md",
        ".cortex/TASKS.md",
        ".cortex/RECOVERY.md",
        ".cortex/GUIDE.md",
        ".cortex/RULES.md"
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "snapshot" => CreateSnapshot(ParseTwoOptions(args, "--source", "--output")),
                "verify" => VerifyBundle(ParseOneOption(args, "--bundle")),
                "audit" => AuditSnapshot(ParseOneOption(args, "--snapshot")),
                "serve" => await ServeAsync(ParseOneOption(args, "--bundle")).ConfigureAwait(false),
                "self-test" => RunChunkingSelfTests(),
                _ => Usage()
            };
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(JsonSerializer.Serialize(new
            {
                level = "error",
                operation = "continuity-mcp-test-host",
                code = "continuity_test_host_failed",
                message = exception.Message
            })).ConfigureAwait(false);
            return 1;
        }
    }

    private static int CreateSnapshot(IReadOnlyDictionary<string, string> options)
    {
        var sourceRoot = RequireAbsoluteDirectory(options["--source"], "--source");
        var outputRoot = RequireAbsoluteNewDirectory(options["--output"], "--output");
        EnsureSnapshotDestination(sourceRoot, outputRoot);
        EnsurePathBudget(outputRoot);

        var version = ReadVersion(sourceRoot);
        var filesRoot = Path.Combine(outputRoot, "files");
        Directory.CreateDirectory(filesRoot);

        var records = new List<KnowledgeRecordV1>();
        var fileManifest = new List<object>();
        foreach (var relativePath in ContinuityPaths)
        {
            var sourcePath = ResolveContainedFile(sourceRoot, relativePath);
            EnsureNoReparsePoint(sourcePath);
            var bytes = File.ReadAllBytes(sourcePath);
            var copiedPath = ResolveContainedFile(filesRoot, relativePath, requireExisting: false);
            EnsurePathBudget(copiedPath);
            Directory.CreateDirectory(Path.GetDirectoryName(copiedPath)!);
            File.WriteAllBytes(copiedPath, bytes);

            var sourceHash = Sha256(bytes);
            var copiedHash = Sha256(File.ReadAllBytes(copiedPath));
            if (!string.Equals(sourceHash, copiedHash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Snapshot verification failed for '{relativePath}'.");

            var observedAt = File.GetLastWriteTimeUtc(sourcePath);
            var text = CreateSafeProjection(DecodeMarkdown(bytes, relativePath));
            var chunks = MarkdownContinuityChunker.Chunk(text, MaximumChunkCharacters);
            var taskStates = MarkdownTaskStateClassifier.Classify(relativePath, text, chunks);
            for (var index = 0; index < chunks.Count; index++)
            {
                var chunk = chunks[index];
                var taskState = taskStates[index];
                var chunkNumber = index + 1;
                var evidenceReference = BuildEvidenceReference(relativePath, chunk, chunkNumber);
                var projectedContent = NormalizeProjectedContent(chunk.Content);
                var chunkHash = Sha256(StrictUtf8.GetBytes(projectedContent));
                var fileName = Path.GetFileName(relativePath);
                var keywords = new List<string>
                {
                    "continuity",
                    "pegasus-cortex",
                    fileName.ToLowerInvariant(),
                    Slug(relativePath),
                    chunk.HeadingIdentity
                };
                if (taskState is not null)
                {
                    keywords.Add("task-state");
                    keywords.Add(taskState.Keyword);
                }
                var transformations = new List<string>
                {
                    "markdown-heading-line-span-v1",
                    $"source-lines-{chunk.LineStart:D6}-{chunk.LineEnd:D6}",
                    $"heading-{chunk.HeadingIdentity}",
                    "copied-without-source-mutation",
                    "descriptive-prohibited-marker-normalized",
                    "outer-whitespace-trimmed",
                    "utf8-normalized"
                };
                if (taskState is not null)
                {
                    transformations.Add("markdown-task-section-state-v1");
                    transformations.Add(taskState.Keyword);
                }
                records.Add(KnowledgeRecordFactory.Create(
                    TestNamespace,
                    Taxonomy,
                    $"{Slug(relativePath)}-{chunk.HeadingIdentity}-lines-{chunk.LineStart:D6}-{chunk.LineEnd:D6}-part-{chunkNumber:D4}",
                    $"Continuity: {fileName} - {chunk.HeadingTitle} (lines {chunk.LineStart}-{chunk.LineEnd})",
                    taskState is null
                        ? $"Bounded normalized lines {chunk.LineStart}-{chunk.LineEnd} under {chunk.HeadingTitle} from the hash-verified public continuity copy of {relativePath}."
                        : $"Public task state {taskState.State}; bounded normalized lines {chunk.LineStart}-{chunk.LineEnd} under {chunk.HeadingTitle} from the hash-verified public continuity copy of {relativePath}.",
                    projectedContent,
                    keywords,
                    [new KnowledgeApplicability(
                        "pegasus-cortex",
                        taskState?.ComponentId ?? "project-continuity",
                        null,
                        null,
                        "verified")],
                    [new KnowledgeEvidence(
                        evidenceReference,
                        "text/markdown",
                        chunkHash,
                        "sha256-verified-line-projection",
                        observedAt,
                        version.ReleaseVersion)],
                    new KnowledgeProvenance(
                        ProducerContract,
                        [evidenceReference],
                        transformations),
                    new KnowledgeConfidence(1, "Deterministic normalized line projection from a SHA-256-verified public continuity copy."),
                    new KnowledgeFreshness("current", observedAt, null, "Current at disposable snapshot creation."),
                    "internal",
                    new KnowledgeRetention("ephemeral", null, LegalHold: false),
                    new KnowledgeApproval("approved", 1, "direct-user-test-authorization", observedAt),
                    new KnowledgePromotion("none", null, null),
                    supersedesRevisionIds: null,
                    createdAt: observedAt));
            }

            fileManifest.Add(new
            {
                relativePath,
                byteLength = bytes.LongLength,
                sha256 = sourceHash,
                observedAt,
                lineCount = text.Count(character => character == '\n') + 1,
                chunkCount = chunks.Count,
                chunking = "markdown-heading-line-span-v1",
                chunks = chunks.Select((chunk, index) =>
                {
                    var projectedContent = NormalizeProjectedContent(chunk.Content);
                    return new
                    {
                        number = index + 1,
                        chunk.LineStart,
                        chunk.LineEnd,
                        chunk.HeadingIdentity,
                        chunk.HeadingTitle,
                        taskState = taskStates[index]?.State,
                        taskStateComponentId = taskStates[index]?.ComponentId,
                        taskItemCount = taskStates[index]?.TaskItemCount ?? 0,
                        taskSectionItemCount = taskStates[index]?.SectionTaskItemCount ?? 0,
                        sourceSliceCharacterCount = chunk.Content.Length,
                        characterCount = projectedContent.Length,
                        sha256 = Sha256(StrictUtf8.GetBytes(projectedContent))
                    };
                }).ToArray(),
                taskStateRecordCounts = taskStates
                    .Where(item => item is not null)
                    .GroupBy(item => item!.State, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                taskStateItemCounts = taskStates
                    .Where(item => item is not null)
                    .GroupBy(item => item!.State, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Sum(item => item!.TaskItemCount),
                        StringComparer.Ordinal)
            });
        }

        var generatedAt = DateTimeOffset.UtcNow;
        var bundleVersion = TestBundleVersion(generatedAt);
        var bundle = KnowledgeRecordFactory.CreateBundle(
            bundleVersion.ReleaseVersion,
            bundleVersion.NuGetVersion,
            generatedAt,
            [new KnowledgeTaxonomyDefinition(
                Taxonomy,
                "Project continuity",
                "Disposable, read-only copy of public Pegasus Cortex development continuity.")],
            records);
        ValidateBundle(bundle);

        var bundlePath = Path.Combine(outputRoot, "continuity-bundle.json");
        File.WriteAllBytes(bundlePath, KnowledgeBundleSerializer.Export(bundle));
        var bundleBytes = File.ReadAllBytes(bundlePath);
        if (bundleBytes.LongLength > MaximumBundleBytes)
            throw new InvalidOperationException("Generated continuity bundle exceeds the test-host byte limit.");

        var manifestPath = Path.Combine(outputRoot, "snapshot-manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                mode = "disposable-read-only-test-copy",
                sourceRoot,
                sourceReleaseVersion = version.ReleaseVersion,
                generatedAt,
                sourceFiles = fileManifest,
                bundle = new
                {
                    relativePath = Path.GetRelativePath(outputRoot, bundlePath).Replace('\\', '/'),
                    bundle.ReleaseVersion,
                    bundle.NuGetVersion,
                    bundle.BundleId,
                    bundle.BundleHash,
                    recordCount = bundle.Records.Count,
                    byteLength = bundleBytes.LongLength,
                    sha256 = Sha256(bundleBytes)
                },
                boundaries = new
                {
                    sourceContinuityMutated = false,
                    exactSourceCopiesPreserved = true,
                    descriptiveProhibitedMarkerNormalizedInProjection = true,
                    productionRuntimeDependency = false,
                    networkAccess = false,
                    databaseAccess = false,
                    writableMcpTools = false
                }
            }, new JsonSerializerOptions { WriteIndented = true }),
            StrictUtf8);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "created",
            outputRoot,
            bundlePath,
            manifestPath,
            recordCount = bundle.Records.Count,
            bundle.BundleHash,
            snapshotManifestSha256 = Sha256(File.ReadAllBytes(manifestPath))
        }));
        return 0;
    }

    private static int VerifyBundle(string bundlePath)
    {
        var bundle = LoadBundle(bundlePath);
        ValidateBundle(bundle);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "verified",
            bundlePath = Path.GetFullPath(bundlePath),
            bundle.BundleId,
            bundle.BundleHash,
            recordCount = bundle.Records.Count,
            taxonomyCount = bundle.Taxonomies.Count,
            namespaces = bundle.Records.Select(record => record.Namespace)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
        }));
        return 0;
    }

    private static int AuditSnapshot(string snapshotRoot)
    {
        var result = DisposableSnapshotAuditor.Audit(snapshotRoot);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "audited",
            snapshotRoot = Path.GetFullPath(snapshotRoot),
            result.SourceFileCount,
            result.RecordCount,
            result.NonblankLineCount,
            result.TaskStateRecordCounts,
            result.TaskStateItemCounts,
            result.BundleSha256,
            result.ManifestSha256
        }));
        return 0;
    }

    private static async Task<int> ServeAsync(string bundlePath)
    {
        var bundle = LoadBundle(bundlePath);
        ValidateBundle(bundle);
        var source = DefaultLibraryQuerySource.CreateVerifiedBundle(bundle);
        var service = new CortexQueryService(
            source,
            new CortexQueryServiceOptions(
                TestNamespace,
                "disposable-continuity-copy-read-only",
                "sha256-verified-at-startup"));
        var authority = new CortexQueryAuthority(
            "local",
            "pegasus-cortex-continuity-test",
            "local-stdio-test",
            "pegasus-cortex-continuity-test",
            policyRevision: 1,
            [new CortexQueryNamespaceGrant(
                TestNamespace,
                "internal",
                [
                    CortexQueryCapability.Discover,
                    CortexQueryCapability.Status,
                    CortexQueryCapability.Catalog,
                    CortexQueryCapability.Search,
                    CortexQueryCapability.Read
                ],
                allowStale: false,
                allowBlockedOrUnverified: false)]);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var application = await CortexMcpApplication.CreateAsync(
                service,
                source,
                authority,
                TestNamespace,
                cancellation.Token).ConfigureAwait(false);
            using var input = new StreamReader(
                Console.OpenStandardInput(),
                StrictUtf8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: false);
            await using var output = new StreamWriter(
                Console.OpenStandardOutput(),
                StrictUtf8,
                bufferSize: 4096,
                leaveOpen: false)
            {
                AutoFlush = false,
                NewLine = "\n"
            };
            await using var diagnostics = new StreamWriter(
                Console.OpenStandardError(),
                StrictUtf8,
                bufferSize: 1024,
                leaveOpen: false)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            var server = new McpStdioServer(application, input, output, diagnostics);
            await server.RunAsync(cancellation.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static KnowledgeSeedBundleV1 LoadBundle(string value)
    {
        var path = Path.GetFullPath(value);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidOperationException("--bundle must identify an existing absolute file.");
        EnsurePathBudget(path);
        EnsureNoReparsePoint(path);
        var length = new FileInfo(path).Length;
        if (length is <= 0 or > MaximumBundleBytes)
            throw new InvalidOperationException("Continuity bundle size is outside the test-host boundary.");
        return KnowledgeBundleSerializer.Deserialize(File.ReadAllBytes(path));
    }

    internal static void ValidateBundle(KnowledgeSeedBundleV1 bundle)
    {
        var validation = new KnowledgeBundleValidator().Validate(bundle);
        if (!validation.Accepted)
        {
            var codes = string.Join(
                ",",
                validation.Diagnostics.Select(item => item.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            throw new InvalidOperationException($"Continuity test bundle validation failed: {codes}");
        }
        if (bundle.Records.Any(record => !string.Equals(record.Namespace, TestNamespace, StringComparison.Ordinal)))
            throw new InvalidOperationException("Continuity test bundle contains a record outside its isolated namespace.");
        _ = DefaultLibraryQuerySource.CreateVerifiedBundle(bundle);
    }

    private static (string ReleaseVersion, string NuGetVersion) TestBundleVersion(DateTimeOffset generatedAt)
    {
        var local = generatedAt.ToLocalTime();
        // The shared release mapping renders hour/minute as an integer, so
        // morning values intentionally omit the canonical form's leading zero.
        var hourMinute = (local.Hour * 100) + local.Minute;
        return (
            $"1.{local:yyyy.MM.dd.HH.mm}.D",
            $"1.{local:yyyyMMdd}.{hourMinute}-d");
    }

    private static (string ReleaseVersion, string NuGetVersion) ReadVersion(string sourceRoot)
    {
        var path = ResolveContainedFile(sourceRoot, "VERSION.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        return (
            root.GetProperty("releaseVersion").GetString()
                ?? throw new InvalidOperationException("VERSION.json has no releaseVersion."),
            root.GetProperty("nuGetVersion").GetString()
                ?? throw new InvalidOperationException("VERSION.json has no nuGetVersion."));
    }

    private static int RunChunkingSelfTests()
    {
        var checkCount = checked(
            MarkdownContinuityChunkerSelfTests.Run() +
            MarkdownTaskStateClassifierSelfTests.Run() +
            DisposableSnapshotAuditorSelfTests.Run());
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "passed",
            operation = "continuity-projection-audit-self-test",
            checkCount
        }));
        return 0;
    }

    internal static string DecodeMarkdown(byte[] bytes, string relativePath)
    {
        var text = StrictUtf8.GetString(bytes);
        if (text.Length > 0 && text[0] == '\ufeff')
            text = text[1..];
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"Continuity file '{relativePath}' is empty.");
        return text;
    }

    internal static string CreateSafeProjection(string text) =>
        // This literal is frequently used to prohibit unsafe history capture.
        // The immutable copied file retains its exact bytes and hash; only the
        // untrusted MCP projection uses neutral terminology so validation does
        // not confuse the policy statement with the prohibited content itself.
        text.Replace("raw chat transcript", "verbatim conversation history", StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeProjectedContent(string content)
    {
        var normalized = content.Trim();
        if (normalized.Length == 0)
            throw new InvalidOperationException("A source line span produced no normalized projection content.");
        return normalized;
    }

    internal static string Slug(string relativePath)
    {
        var builder = new StringBuilder(relativePath.Length);
        var lastWasDash = false;
        foreach (var character in relativePath.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }
        return builder.ToString().Trim('-');
    }

    internal static string RequireAbsoluteDirectory(string value, string option)
    {
        var path = Path.GetFullPath(value);
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new InvalidOperationException($"{option} must identify an existing absolute directory.");
        EnsurePathBudget(path);
        EnsureNoReparsePoint(path);
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string RequireAbsoluteNewDirectory(string value, string option)
    {
        var path = Path.GetFullPath(value);
        if (!Path.IsPathFullyQualified(path) || File.Exists(path) || Directory.Exists(path))
            throw new InvalidOperationException($"{option} must identify a new absolute directory.");
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void EnsureSnapshotDestination(string sourceRoot, string outputRoot)
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(sourceRoot, "artifacts", "validation"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Disposable snapshots must stay beneath the product's artifacts/validation root.");
    }

    internal static string ResolveContainedFile(
        string root,
        string relativePath,
        bool requireExisting = true)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Split('/', '\\').Any(segment => segment is "" or "." or ".."))
            throw new InvalidOperationException("A continuity path is not a safe relative path.");
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A continuity path escaped its allowed root.");
        if (requireExisting && !File.Exists(candidate))
            throw new InvalidOperationException($"Required continuity file is missing: {relativePath}");
        return candidate;
    }

    internal static void EnsureNoReparsePoint(string path)
    {
        var item = File.Exists(path)
            ? (FileSystemInfo)new FileInfo(path)
            : new DirectoryInfo(path);
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Reparse-point paths are not accepted: {path}");
    }

    internal static void EnsurePathBudget(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Length > 240)
            throw new InvalidOperationException("A generated canonical path exceeds 240 UTF-16 characters.");
        foreach (var segment in fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (StrictUtf8.GetByteCount(segment) > 240)
                throw new InvalidOperationException("A generated path segment exceeds 240 UTF-8 bytes.");
        }
    }

    internal static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string BuildEvidenceReference(
        string relativePath,
        MarkdownContinuityChunk chunk,
        int chunkNumber) =>
        $"source://pegasus-cortex/{relativePath.Replace('\\', '/')}" +
        $"?lineStart={chunk.LineStart:D6}&lineEnd={chunk.LineEnd:D6}" +
        $"&heading={chunk.HeadingIdentity}&part={chunkNumber:D4}";

    private static IReadOnlyDictionary<string, string> ParseTwoOptions(
        string[] args,
        string first,
        string second)
    {
        if (args.Length != 5 || args[1] != first || args[3] != second)
            throw new InvalidOperationException($"Expected: snapshot {first} <absolute-path> {second} <absolute-path>");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [first] = args[2],
            [second] = args[4]
        };
    }

    private static string ParseOneOption(string[] args, string option)
    {
        if (args.Length != 3 || args[1] != option)
            throw new InvalidOperationException($"Expected: {args.FirstOrDefault()} {option} <absolute-path>");
        return args[2];
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: snapshot --source <absolute-project-root> --output <new-artifact-dir> | " +
            "verify --bundle <absolute-bundle-path> | audit --snapshot <absolute-snapshot-root> | " +
            "serve --bundle <absolute-bundle-path> | self-test");
        return 2;
    }
}
