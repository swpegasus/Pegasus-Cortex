using System.Text;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Recovery;
using Pegasus.Cortex.OfflineJournal.Contracts;
using Pegasus.Cortex.OfflineJournal.Import;

internal static class LocalRecoveryImportTests
{
    public static async Task ExplicitCopyFirstImportAsync()
    {
        await using var context = await LocalTestContext.CreateAsync();
        var registration = context.Registrations.Single(item =>
            string.Equals(
                item.ProjectLinkId,
                LocalTestContext.PrimaryProjectLinkId,
                StringComparison.Ordinal));
        var sourceRoot = Path.GetFullPath(
            Path.Combine(registration.RootPath, ".cortex"));
        Directory.CreateDirectory(sourceRoot);
        var sourceFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["README.md"] = Encoding.UTF8.GetBytes(
                "# Recovery\n\nHuman-readable local recovery material.\n"),
            ["STATE.md"] = Encoding.UTF8.GetBytes(
                "# State\n\nCurrent state is retained as public conclusions.\n"),
            ["meta.json"] = Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":\"0.2\"}\n")
        };
        foreach (var item in sourceFiles)
            await File.WriteAllBytesAsync(
                Path.Combine(sourceRoot, item.Key),
                item.Value).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "mcp-tools.json"),
            "Bearer ignored-unapproved-value",
            new UTF8Encoding(false)).ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(sourceRoot, "archive"));
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "archive", "old.md"),
            "This archive is outside the fixed recovery set.",
            new UTF8Encoding(false)).ConfigureAwait(false);

        var original = sourceFiles.Keys.ToDictionary(
            name => name,
            name => new SourceSnapshot(
                File.ReadAllBytes(Path.Combine(sourceRoot, name)),
                File.GetLastWriteTimeUtc(Path.Combine(sourceRoot, name))),
            StringComparer.Ordinal);
        var destination = Path.GetFullPath(
            Path.Combine(context.Root, "recovery-import"));
        CreateProtectedDirectory(destination);
        const string RunId = "recovery-run-1";
        var capturedAt = DateTimeOffset.UtcNow
            .AddSeconds(-1)
            .ToUniversalTime();

        var first = await RunImportAsync(
            context,
            LocalTestContext.PrimaryProjectLinkId,
            destination,
            RunId,
            capturedAt).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            first.ExitCode,
            $"Explicit recovery import failed: {first.StandardError}");
        var firstResult = ParseResult(first.StandardOutput);
        LocalAssert.True(
            firstResult["accepted"]!.GetValue<bool>(),
            "The explicit approved recovery import was not accepted.");
        LocalAssert.Equal(
            "copied",
            firstResult["disposition"]!.GetValue<string>(),
            "The first recovery import did not report a new copy.");
        LocalAssert.False(
            firstResult["sourceMutationAllowed"]!.GetValue<bool>(),
            "Recovery import claimed authority to mutate source files.");
        LocalAssert.Equal(
            "omitted-unsafe",
            firstResult["typedConversion"]!.GetValue<string>(),
            "Recovery import inferred typed task authority from Markdown.");
        LocalAssert.True(
            firstResult["fallbackRequired"]!.GetValue<bool>(),
            "Recovery import did not preserve file-based fallback semantics.");

        var manifestPath = Path.Combine(
            destination,
            firstResult["manifestRelativePath"]!.GetValue<string>()
                .Replace('/', Path.DirectorySeparatorChar));
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath)
            .ConfigureAwait(false);
        var manifest =
            CortexCanonicalJson.Deserialize<LegacyCortexImportManifestV1>(
                manifestBytes);
        LocalAssert.True(
            CortexCanonicalJson.Serialize(manifest)
                .AsSpan().SequenceEqual(manifestBytes),
            "The retained recovery manifest is not exact canonical JSON.");
        LocalAssert.True(
            manifest.Items.Select(item => item.RelativePath)
                .SequenceEqual(
                    sourceFiles.Keys.OrderBy(name => name, StringComparer.Ordinal),
                    StringComparer.Ordinal),
            "Recovery import copied a file outside the approved fixed set.");
        for (var index = 0; index < manifest.Items.Count; index++)
        {
            var item = manifest.Items[index];
            var retained = await File.ReadAllBytesAsync(
                Path.Combine(
                    Path.GetDirectoryName(manifestPath)!,
                    item.StoredFileName)).ConfigureAwait(false);
            LocalAssert.True(
                retained.AsSpan().SequenceEqual(sourceFiles[item.RelativePath]),
                "The exact-copy recovery payload changed source bytes.");
        }
        AssertSourceUnchanged(sourceRoot, original);

        var replay = await RunImportAsync(
            context,
            LocalTestContext.PrimaryProjectLinkId,
            destination,
            RunId,
            capturedAt).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            replay.ExitCode,
            $"Exact recovery replay failed: {replay.StandardError}");
        LocalAssert.Equal(
            "existing",
            ParseResult(replay.StandardOutput)["disposition"]!
                .GetValue<string>(),
            "An exact recovery replay was not idempotent.");
        AssertSourceUnchanged(sourceRoot, original);

        var unauthorized = await RunImportAsync(
            context,
            $"plink_{new string('f', 64)}",
            destination,
            "recovery-unauthorized",
            capturedAt).ConfigureAwait(false);
        LocalAssert.Equal(
            3,
            unauthorized.ExitCode,
            "An unregistered project link acquired recovery-import authority.");
        LocalAssert.Equal(
            "cortex_local_recovery_import_forbidden",
            ParseResult(unauthorized.StandardOutput)["diagnosticCode"]!
                .GetValue<string>(),
            "Unauthorized recovery import returned the wrong bounded diagnostic.");

        var stateRoot = Path.GetDirectoryName(context.DatabasePath)!;
        var stateOverlap = await RunImportAsync(
            context,
            LocalTestContext.PrimaryProjectLinkId,
            stateRoot,
            "recovery-state-overlap",
            capturedAt).ConfigureAwait(false);
        LocalAssert.Equal(
            1,
            stateOverlap.ExitCode,
            "Recovery import accepted the dedicated local state tree as its destination.");
        LocalAssert.False(
            Directory.Exists(Path.Combine(stateRoot, "j")),
            "A rejected state-overlap import changed the dedicated state layout.");

        var statePath = Path.Combine(sourceRoot, "STATE.md");
        var stateOriginal = original["STATE.md"];
        await File.WriteAllTextAsync(
            statePath,
            "Bearer abcdefghijklmnop",
            new UTF8Encoding(false)).ConfigureAwait(false);
        var screened = await RunImportAsync(
            context,
            LocalTestContext.PrimaryProjectLinkId,
            destination,
            "recovery-secret",
            capturedAt).ConfigureAwait(false);
        LocalAssert.Equal(
            3,
            screened.ExitCode,
            "A prohibited credential-like recovery source was committed.");
        LocalAssert.False(
            Directory.Exists(
                Path.Combine(destination, "j", "i", "recovery-secret")),
            "Rejected recovery content reached its final destination.");
        Restore(statePath, stateOriginal);

        await File.WriteAllBytesAsync(
            statePath,
            Enumerable.Repeat((byte)'x', 1025).ToArray()).ConfigureAwait(false);
        var registry = await CortexLocalProjectRegistry.LoadAsync(
            context.RegistryPath,
            context.ProfileId,
            CancellationToken.None).ConfigureAwait(false);
        var boundedDestination = Path.GetFullPath(
            Path.Combine(context.Root, "recovery-bounded"));
        CreateProtectedDirectory(boundedDestination);
        var boundedProducer = new CortexLocalRecoveryImportProducer(
            registry,
            context.ProfileId,
            stateRoot,
            options: new OfflineJournalOptions
            {
                MaxLegacyImportFileBytes = 1024
            });
        var oversized = await boundedProducer.ImportAsync(
            LocalTestContext.PrimaryProjectLinkId,
            boundedDestination,
            "recovery-oversized",
            capturedAt,
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.False(
            oversized.Accepted,
            "An oversized approved recovery file passed the configured bound.");
        LocalAssert.Equal(
            "legacy_cortex_import_limit_exceeded",
            oversized.DiagnosticCode!,
            "Oversized recovery content returned the wrong bounded diagnostic.");
        Restore(statePath, stateOriginal);

        await File.WriteAllTextAsync(
            statePath,
            "Changed by the test to simulate a stale source.",
            new UTF8Encoding(false)).ConfigureAwait(false);
        var stale = await RunImportAsync(
            context,
            LocalTestContext.PrimaryProjectLinkId,
            destination,
            RunId,
            capturedAt).ConfigureAwait(false);
        LocalAssert.Equal(
            3,
            stale.ExitCode,
            "A changed source was accepted as an exact run replay.");
        LocalAssert.Equal(
            "conflict",
            ParseResult(stale.StandardOutput)["disposition"]!
                .GetValue<string>(),
            "A stale source did not report a visible run conflict.");
        Restore(statePath, stateOriginal);

        await AssertReparseRejectedWhenSupportedAsync(
            context,
            sourceRoot,
            destination,
            capturedAt).ConfigureAwait(false);

        var importDirectoriesBefore = Directory
            .EnumerateDirectories(Path.Combine(destination, "j", "i"))
            .Count();
        var normalStartup = await LocalProcessHarness.RunAsync(
            StdioArguments(context),
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized()
            ]).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            normalStartup.ExitCode,
            $"Normal local startup failed: {normalStartup.StandardError}");
        LocalAssert.Equal(
            importDirectoriesBefore,
            Directory.EnumerateDirectories(
                    Path.Combine(destination, "j", "i"))
                .Count(),
            "Normal local startup invoked the one-shot recovery importer.");

        var protectedItem = manifest.Items[0];
        var protectedPath = Path.Combine(
            Path.GetDirectoryName(manifestPath)!,
            protectedItem.StoredFileName);
        var tamperedBytes = await File.ReadAllBytesAsync(protectedPath)
            .ConfigureAwait(false);
        tamperedBytes[0] ^= 1;
        await File.WriteAllBytesAsync(protectedPath, tamperedBytes)
            .ConfigureAwait(false);
        var tampered = await RunImportAsync(
            context,
            LocalTestContext.PrimaryProjectLinkId,
            destination,
            RunId,
            capturedAt).ConfigureAwait(false);
        LocalAssert.Equal(
            3,
            tampered.ExitCode,
            "A tampered retained recovery copy passed exact replay.");
        LocalAssert.False(
            ParseResult(tampered.StandardOutput)["accepted"]!.GetValue<bool>(),
            "A tampered retained copy was reported as accepted.");
    }

    private static async Task AssertReparseRejectedWhenSupportedAsync(
        LocalTestContext context,
        string sourceRoot,
        string destination,
        DateTimeOffset capturedAt)
    {
        var target = Path.Combine(context.Root, "reparse-target.md");
        var link = Path.Combine(sourceRoot, "RULES.md");
        await File.WriteAllTextAsync(
            target,
            "A link target must never be traversed.",
            new UTF8Encoding(false)).ConfigureAwait(false);
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                PlatformNotSupportedException)
        {
            Console.WriteLine(
                "SKIP recovery reparse probe: symbolic-link creation is unavailable.");
            return;
        }

        try
        {
            var result = await RunImportAsync(
                context,
                LocalTestContext.PrimaryProjectLinkId,
                destination,
                "recovery-reparse",
                capturedAt).ConfigureAwait(false);
            LocalAssert.Equal(
                3,
                result.ExitCode,
                "A recovery import traversed a source reparse point.");
            LocalAssert.False(
                Directory.Exists(
                    Path.Combine(destination, "j", "i", "recovery-reparse")),
                "A reparse-backed recovery source reached final storage.");
        }
        finally
        {
            File.Delete(link);
        }
    }

    private static async Task<LocalProcessResult> RunImportAsync(
        LocalTestContext context,
        string projectLinkId,
        string destination,
        string runId,
        DateTimeOffset capturedAt) =>
        await LocalProcessHarness.RunAsync(
            [
                "import-recovery",
                "--profile",
                context.ProfileId,
                "--state-root",
                Path.GetDirectoryName(context.DatabasePath)!,
                "--project-link",
                projectLinkId,
                "--destination-root",
                destination,
                "--run-id",
                runId,
                "--captured-at-utc",
                capturedAt.ToString("O")
            ],
            []).ConfigureAwait(false);

    private static JsonObject ParseResult(string output) =>
        JsonNode.Parse(output)?.AsObject() ??
        throw new InvalidDataException(
            "The recovery command did not emit one JSON result.");

    private static void CreateProtectedDirectory(string path)
    {
        Directory.CreateDirectory(path);
        CortexLocalRegistryAclPolicy.ProtectDirectory(path);
        CortexLocalRegistryAclPolicy.RequireProtectedDirectory(path);
    }

    private static void AssertSourceUnchanged(
        string sourceRoot,
        IReadOnlyDictionary<string, SourceSnapshot> expected)
    {
        foreach (var item in expected)
        {
            var path = Path.Combine(sourceRoot, item.Key);
            LocalAssert.True(
                File.ReadAllBytes(path).AsSpan().SequenceEqual(item.Value.Bytes),
                $"Recovery import changed source bytes for {item.Key}.");
            LocalAssert.Equal(
                item.Value.LastWriteUtc,
                File.GetLastWriteTimeUtc(path),
                $"Recovery import changed the source timestamp for {item.Key}.");
        }
    }

    private static void Restore(string path, SourceSnapshot snapshot)
    {
        File.WriteAllBytes(path, snapshot.Bytes);
        File.SetLastWriteTimeUtc(path, snapshot.LastWriteUtc);
    }

    private static string[] StdioArguments(LocalTestContext context) =>
    [
        "stdio",
        "--profile",
        context.ProfileId,
        "--state-root",
        Path.GetDirectoryName(context.DatabasePath)!
    ];

    private sealed record SourceSnapshot(byte[] Bytes, DateTime LastWriteUtc);
}
