using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.DefaultLibrary.Seeds;
using Pegasus.Cortex.LearningExportCli;
using Pegasus.Cortex.LearningExports.Adapters;
using Pegasus.Cortex.LearningExports.Build;
using Pegasus.Cortex.LearningExports.Contracts;
using Pegasus.Cortex.LearningExports.Serialization;
using Pegasus.Cortex.LearningExports.DefaultLibrary;

var tests = new (string Name, Func<Task> Run)[]
{
    ("deterministic plan and leakage-group split", TestDeterministicPlanAsync),
    ("runtime authority and eligibility default deny", TestDefaultDenyAsync),
    ("atomic build, exact hashes, and idempotent rerun", TestAtomicBuildAsync),
    ("failure, limits, and cancellation clean staging", TestCleanupAsync),
    ("input-bundle hashing is canonical, bounded, and cancellation-aware", TestInputBundleHashAsync),
    ("caller-owned input mutation cannot cross the snapshot", TestMutableInputSnapshotAsync),
    ("CLI is advisory plan-only", TestCliSafeDefaultAsync),
    ("language-neutral manifest schema boundary", TestManifestSchemaAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add(test.Name);
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

Console.WriteLine($"RESULT {tests.Length - failures.Count}/{tests.Length} passed");
return failures.Count == 0 ? 0 : 1;

static async Task TestDeterministicPlanAsync()
{
    var fixture = CreateFixture();
    var builder = new LearningDatasetBuilder();
    var first = await builder.ExecuteAsync(
        fixture.Bundle,
        fixture.Definition,
        new LearningDatasetBuildRequest(false, null, fixture.Authority),
        CancellationToken.None);
    var secondBundle = LearningExportFactory.CreateInputBundle(
        fixture.Bundle.GeneratedAt,
        fixture.Bundle.Records.Reverse());
    var second = await builder.ExecuteAsync(
        secondBundle,
        fixture.Definition,
        new LearningDatasetBuildRequest(false, null, fixture.Authority),
        CancellationToken.None);

    Assert.True(first.Accepted && second.Accepted, "Equivalent approved inputs must plan successfully.");
    Assert.Equal(
        Convert.ToHexString(SHA256.HashData(LearningExportJson.Serialize(first.Manifest!))),
        Convert.ToHexString(SHA256.HashData(LearningExportJson.Serialize(second.Manifest!))));
    Assert.Equal(first.Manifest!.ManifestHash, LearningExportFactory.ComputeManifestHash(first.Manifest));
    Assert.Equal(LearningExportContractIds.Canonicalization, first.Manifest.Canonicalization);
    Assert.True(Encoding.UTF8.GetString(LearningExportJson.Serialize(first.Manifest)).StartsWith("{\"artifacts\":", StringComparison.Ordinal),
        "Manifest object members are not using canonical ordinal order.");
    Assert.Equal(first.Manifest.Artifacts.Single().Sha256, second.Manifest!.Artifacts.Single().Sha256);

    var grouped = first.Manifest.Items.Where(item => item.RecordId is "record-a" or "record-b").ToArray();
    Assert.Equal(2, grouped.Length);
    Assert.Equal(grouped[0].Split, grouped[1].Split);
    Assert.Equal(grouped[0].LeakageGroupHash, grouped[1].LeakageGroupHash);
    Assert.False(grouped[0].LeakageGroupHash.Contains("project-one", StringComparison.Ordinal),
        "A raw leakage group entered the manifest.");
    Assert.False(first.Manifest.ModelTrainingPerformed, "Planning claimed a training run.");
    Assert.False(first.Manifest.Executable, "The manifest became executable content.");
    Assert.True(first.Manifest.Items.All(item => item.SourceContentHash.Length == 64),
        "Exact source content hashes were not retained.");

    var canonicalBefore = LearningExportJson.Serialize(first.Manifest);
    var callerOptions = LearningExportJson.CreateSerializerOptions();
    callerOptions.PropertyNamingPolicy = null;
    Assert.Equal(
        Convert.ToHexString(canonicalBefore),
        Convert.ToHexString(LearningExportJson.Serialize(first.Manifest)));

    var knowledge = DefaultKnowledgeLibrary.Create().Records.First();
    var adapted = DefaultLibraryLearningSourceAdapter.FromKnowledgeRecord(
        knowledge,
        CreateReview("decision:adapted-record"),
        "service:pegasus-cortex",
        "artifact:default-library",
        "default-library-group");
    Assert.Equal(knowledge.ContentHash, adapted.SourceContentHash);
    Assert.False(string.Equals(adapted.SourceContentHash, adapted.ReviewEnvelopeHash, StringComparison.Ordinal),
        "The upstream content hash and export review envelope were conflated.");
}

static async Task TestDefaultDenyAsync()
{
    var fixture = CreateFixture();
    var missingAuthority = fixture.Authority with
    {
        CanPlan = false,
        ApprovedDecisionHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Fixture.DatasetDecision] = fixture.Definition.DefinitionHash
        }
    };
    var unauthorized = await new LearningDatasetBuilder().ExecuteAsync(
        fixture.Bundle,
        fixture.Definition,
        new LearningDatasetBuildRequest(false, null, missingAuthority),
        CancellationToken.None);
    Assert.False(unauthorized.Accepted, "Serialized approvals authorized themselves without runtime authority.");
    Assert.ContainsCode("learning_export_authority_denied", unauthorized);
    Assert.ContainsCode("learning_export_record_authority_denied", unauthorized);

    var deniedReview = CreateReview("decision:denied") with { Eligible = false, State = "pending" };
    var denied = LearningExportFactory.CreateRecord(
        "record-denied",
        "revision-denied",
        Fixture.Namespace,
        "raw_chat",
        "service:coder",
        "artifact:denied",
        "project-denied",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["text"] = "authorization: Bearer eyFixture"
        },
        "internal",
        deniedReview,
        Fixture.CreatedAt);
    var bundle = LearningExportFactory.CreateInputBundle(Fixture.CreatedAt, [denied]);
    var definition = CreateDefinition(bundle, [denied], "denied-dataset", "decision:denied-dataset");
    var authority = CreateAuthority([denied], definition);
    var rejected = await new LearningDatasetBuilder().ExecuteAsync(
        bundle,
        definition,
        new LearningDatasetBuildRequest(false, null, authority),
        CancellationToken.None);
    Assert.False(rejected.Accepted, "Ineligible raw chat with credential material was exported.");
    Assert.ContainsCode("learning_export_record_not_approved", rejected);
    Assert.ContainsCode("learning_export_source_kind_denied", rejected);
    Assert.ContainsCode("learning_export_prohibited_content", rejected);
}

static async Task TestInputBundleHashAsync()
{
    var bundle = CreateFixture().Bundle;
    var canonical = bundle with
    {
        BundleId = string.Empty,
        Records = bundle.Records
            .Select(record => record with
            {
                Payload = LearningExportFactory.CanonicalDictionary(record.Payload)
            })
            .OrderBy(record => record.RecordId, StringComparer.Ordinal)
            .ThenBy(record => record.RevisionId, StringComparer.Ordinal)
            .ToArray(),
        BundleHash = new string('0', 64)
    };

    Assert.Equal(
        LearningExportJson.Sha256(LearningExportJson.Serialize(canonical)),
        LearningExportFactory.ComputeBundleHash(bundle, CancellationToken.None));

    var misreportedCount = bundle with
    {
        Records = new MisreportedCountList<LearningRecordRevisionV1>(
            bundle.Records,
            reportedCount: -1)
    };
    Assert.Equal(
        bundle.BundleHash,
        LearningExportFactory.ComputeBundleHash(
            misreportedCount,
            CancellationToken.None));

    var nullRecords = new LearningRecordRevisionV1[] { null! };
    Assert.ThrowsArgument(
        () => LearningExportFactory.CreateInputBundle(
            bundle.GeneratedAt,
            nullRecords,
            CancellationToken.None),
        "records");
    Assert.ThrowsArgument(
        () => LearningExportFactory.ComputeBundleHash(
            bundle with { Records = nullRecords },
            CancellationToken.None),
        "bundle");

    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    Assert.ThrowsCanceled(() =>
        LearningExportFactory.ComputeBundleHash(bundle, canceled.Token));

    using var enumerationCanceled = new CancellationTokenSource();
    Assert.ThrowsCanceled(() =>
        LearningExportFactory.CreateInputBundle(
            bundle.GeneratedAt,
            CancelDuringInputEnumeration(bundle.Records, enumerationCanceled),
            enumerationCanceled.Token));

    var oversizedRecords = Enumerable.Range(0, 3)
        .Select(index => CreateRecord(
            $"oversized-hash-record-{index}",
            $"oversized-hash-revision-{index}",
            $"decision:oversized-hash-{index}",
            "oversized-hash-group",
            new string('x', 30 * 1024)))
        .ToArray();
    var oversizedBundle = LearningExportFactory.CreateInputBundle(
        Fixture.CreatedAt,
        oversizedRecords);
    var oversizedDefinition = CreateDefinition(
        oversizedBundle,
        oversizedRecords,
        "oversized-hash-dataset",
        "decision:oversized-hash-dataset");
    var oversized = await new LearningDatasetBuilder(
            new LearningExportOptions { MaxInputBundleBytes = 64 * 1024 })
        .ExecuteAsync(
            oversizedBundle,
            oversizedDefinition,
            new LearningDatasetBuildRequest(
                false,
                null,
                CreateAuthority(oversizedRecords, oversizedDefinition)),
            CancellationToken.None);
    Assert.False(oversized.Accepted, "An oversized canonical input bundle was accepted.");
    Assert.ContainsCode("learning_export_input_oversize", oversized);
}

static IEnumerable<LearningRecordRevisionV1> CancelDuringInputEnumeration(
    IReadOnlyList<LearningRecordRevisionV1> records,
    CancellationTokenSource cancellation)
{
    yield return records[0];
    cancellation.Cancel();
    yield return records[1];
}

static async Task TestAtomicBuildAsync()
{
    var fixture = CreateFixture();
    var root = CreateTemporaryRoot();
    try
    {
        var builder = new LearningDatasetBuilder();
        var request = new LearningDatasetBuildRequest(
            true,
            root,
            fixture.Authority with { CanBuild = true, ApprovedOutputRoot = root });
        var first = await builder.ExecuteAsync(
            fixture.Bundle,
            fixture.Definition,
            request,
            CancellationToken.None);
        Assert.True(first.Accepted && first.Published, "Approved build did not publish.");
        if (OperatingSystem.IsWindows())
            Assert.ContainsCode("learning_export_windows_acl_host_asserted", first);
        Assert.True(Directory.Exists(first.ArtifactDirectory!), "Published directory is missing.");
        Assert.Equal(2, Directory.EnumerateFiles(first.ArtifactDirectory!).Count());

        var manifestPath = Path.Combine(first.ArtifactDirectory!, "learning-dataset-manifest.v1.json");
        var artifactPath = Path.Combine(first.ArtifactDirectory!, "learning-dataset.v1.jsonl");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var artifactBytes = await File.ReadAllBytesAsync(artifactPath);
        var stored = LearningExportJson.Deserialize<LearningDatasetManifestV1>(manifestBytes);
        Assert.Equal(
            Convert.ToHexString(LearningExportJson.Serialize(stored)),
            Convert.ToHexString(manifestBytes));
        Assert.Equal(stored.ManifestHash, LearningExportFactory.ComputeManifestHash(stored));
        Assert.Equal(stored.Artifacts.Single().Sha256, Sha256(artifactBytes));
        Assert.Equal(stored.Artifacts.Single().Bytes, (long)artifactBytes.Length);
        Assert.Equal(stored.Items.Count, Encoding.UTF8.GetString(artifactBytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        var firstLine = JsonDocument.Parse(Encoding.UTF8.GetString(artifactBytes).Split('\n')[0]).RootElement;
        Assert.True(firstLine.GetProperty("payload").TryGetProperty("text", out _),
            "The canonical adapter dropped the reviewed payload.");
        Assert.False(firstLine.GetProperty("payload").TryGetProperty("prompt", out _),
            "The adapter invented a supervised prompt field.");
        Assert.False(firstLine.GetProperty("payload").TryGetProperty("answer", out _),
            "The adapter invented a supervised answer field.");

        var beforeManifest = Sha256(manifestBytes);
        var beforeArtifact = Sha256(artifactBytes);
        var second = await builder.ExecuteAsync(
            fixture.Bundle,
            fixture.Definition,
            request,
            CancellationToken.None);
        Assert.True(second.Accepted && second.Published, "Idempotent rebuild failed.");
        Assert.ContainsCode("learning_export_already_published", second);
        Assert.Equal(beforeManifest, Sha256(await File.ReadAllBytesAsync(manifestPath)));
        Assert.Equal(beforeArtifact, Sha256(await File.ReadAllBytesAsync(artifactPath)));
        Assert.False(Directory.Exists(Path.Combine(root, ".staging")), "Staging was not pruned.");
    }
    finally
    {
        DeleteTemporaryRoot(root);
    }
}

static async Task TestCleanupAsync()
{
    var fixture = CreateFixture("failing-fixture", "1");
    var failureRoot = CreateTemporaryRoot();
    try
    {
        var builder = new LearningDatasetBuilder(adapters: [new FailingAdapter()]);
        var result = await builder.ExecuteAsync(
            fixture.Bundle,
            fixture.Definition,
            new LearningDatasetBuildRequest(
                true,
                failureRoot,
                fixture.Authority with { CanBuild = true, ApprovedOutputRoot = failureRoot }),
            CancellationToken.None);
        Assert.False(result.Accepted, "A partial adapter failure published artifacts.");
        Assert.ContainsCode("learning_export_adapter_failed", result);
        AssertNoPublishedFiles(failureRoot);
    }
    finally
    {
        DeleteTemporaryRoot(failureRoot);
    }

    var oversizeFixture = CreateFixture("oversize-fixture", "1");
    var oversizeRoot = CreateTemporaryRoot();
    try
    {
        var smallBuilder = new LearningDatasetBuilder(
            new LearningExportOptions
            {
                MaxPayloadCharacters = 4096,
                MaxRecordBytes = 32 * 1024
            },
            adapters: [new OversizeAdapter()]);
        var result = await smallBuilder.ExecuteAsync(
            oversizeFixture.Bundle,
            oversizeFixture.Definition,
            new LearningDatasetBuildRequest(
                true,
                oversizeRoot,
                oversizeFixture.Authority with { CanBuild = true, ApprovedOutputRoot = oversizeRoot }),
            CancellationToken.None);
        Assert.False(result.Accepted, "An oversized encoded record was published.");
        Assert.ContainsCode("learning_export_record_oversize", result);
        AssertNoPublishedFiles(oversizeRoot);
    }
    finally
    {
        DeleteTemporaryRoot(oversizeRoot);
    }

    var canceledFixture = CreateFixture("canceling-fixture", "1");
    var cancellationRoot = CreateTemporaryRoot();
    try
    {
        var builder = new LearningDatasetBuilder(adapters: [new CancelingAdapter()]);
        var result = await builder.ExecuteAsync(
            canceledFixture.Bundle,
            canceledFixture.Definition,
            new LearningDatasetBuildRequest(
                true,
                cancellationRoot,
                canceledFixture.Authority with { CanBuild = true, ApprovedOutputRoot = cancellationRoot }),
            CancellationToken.None);
        Assert.False(result.Accepted, "A dependency-owned cancellation was reported as caller cancellation.");
        Assert.ContainsCode("learning_export_adapter_failed", result);
        AssertNoPublishedFiles(cancellationRoot);
    }
    finally
    {
        DeleteTemporaryRoot(cancellationRoot);
    }

    var emptyFixture = CreateFixture("empty-fixture", "1");
    var empty = await new LearningDatasetBuilder(adapters: [new EmptyAdapter()]).ExecuteAsync(
        emptyFixture.Bundle,
        emptyFixture.Definition,
        new LearningDatasetBuildRequest(false, null, emptyFixture.Authority),
        CancellationToken.None);
    Assert.False(empty.Accepted, "A zero-byte adapter output produced an accepted manifest.");
    Assert.ContainsCode("learning_export_adapter_empty_output", empty);

    var callerCanceledFixture = CreateFixture("blocking-fixture", "1");
    var callerCancellationRoot = CreateTemporaryRoot();
    try
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var builder = new LearningDatasetBuilder(adapters: [new BlockingAdapter()]);
        await Assert.ThrowsCanceledAsync(() => builder.ExecuteAsync(
            callerCanceledFixture.Bundle,
            callerCanceledFixture.Definition,
            new LearningDatasetBuildRequest(
                true,
                callerCancellationRoot,
                callerCanceledFixture.Authority with
                {
                    CanBuild = true,
                    ApprovedOutputRoot = callerCancellationRoot
                }),
            cancellation.Token).AsTask());
        AssertNoPublishedFiles(callerCancellationRoot);
    }
    finally
    {
        DeleteTemporaryRoot(callerCancellationRoot);
    }
}

static async Task TestMutableInputSnapshotAsync()
{
    const string originalText = "Reviewed immutable source text.";
    var created = CreateRecord(
        "record-mutable",
        "revision-mutable",
        "decision:record-mutable",
        "project-mutable",
        originalText);
    var callerPayload = new Dictionary<string, string>(created.Payload, StringComparer.Ordinal);
    var record = created with
    {
        Payload = callerPayload,
        SourceContentHash = new string('0', 64),
        ReviewEnvelopeHash = new string('0', 64)
    };
    record = record with { SourceContentHash = LearningExportFactory.ComputeSourceContentHash(record) };
    record = record with { ReviewEnvelopeHash = LearningExportFactory.ComputeReviewEnvelopeHash(record) };
    var bundle = LearningExportFactory.CreateInputBundle(Fixture.CreatedAt, [record]);
    var definition = CreateDefinition(
        bundle,
        [record],
        "mutable-fixture",
        "decision:mutable-fixture",
        "mutating-fixture",
        "1");
    var authority = CreateAuthority([record], definition);
    var root = CreateTemporaryRoot();
    try
    {
        var result = await new LearningDatasetBuilder(
            adapters: [new MutatingAdapter(callerPayload)]).ExecuteAsync(
                bundle,
                definition,
                new LearningDatasetBuildRequest(
                    true,
                    root,
                    authority with { CanBuild = true, ApprovedOutputRoot = root }),
                CancellationToken.None);

        Assert.True(result.Accepted && result.Published, "Snapshot-isolated build did not publish.");
        Assert.Equal("tampered-after-snapshot", callerPayload["text"]);
        var artifactPath = Path.Combine(result.ArtifactDirectory!, "mutating.jsonl");
        using var item = JsonDocument.Parse((await File.ReadAllLinesAsync(artifactPath)).Single());
        Assert.Equal(originalText, item.RootElement.GetProperty("payload").GetProperty("text").GetString());
    }
    finally
    {
        DeleteTemporaryRoot(root);
    }
}

static async Task TestCliSafeDefaultAsync()
{
    var fixture = CreateFixture();
    var root = CreateTemporaryRoot();
    try
    {
        var bundlePath = Path.Combine(root, "input.json");
        var definitionPath = Path.Combine(root, "definition.json");
        await File.WriteAllBytesAsync(bundlePath, LearningExportJson.Serialize(fixture.Bundle));
        await File.WriteAllBytesAsync(definitionPath, LearningExportJson.Serialize(fixture.Definition));
        var arguments = CreateCliArguments(fixture, bundlePath, definitionPath).ToArray();
        var planOutput = new StringWriter();
        var planExit = await LearningExportCliApplication.RunAsync(
            arguments,
            planOutput,
            CancellationToken.None);
        Assert.Equal(0, planExit);
        using (var response = JsonDocument.Parse(planOutput.ToString()))
        {
            Assert.Equal("plan", response.RootElement.GetProperty("operation").GetString());
            Assert.True(response.RootElement.GetProperty("accepted").GetBoolean(), "CLI plan was rejected.");
            Assert.False(response.RootElement.GetProperty("published").GetBoolean(), "CLI planned operation published.");
            Assert.False(response.RootElement.GetProperty("networkAccess").GetBoolean(), "CLI claimed network access.");
            Assert.False(response.RootElement.GetProperty("serverAuthorizationGranted").GetBoolean(),
                "CLI advisory validation claimed trusted server authorization.");
        }

        var buildOutput = new StringWriter();
        var buildExit = await LearningExportCliApplication.RunAsync(
            [.. arguments, "--build"],
            buildOutput,
            CancellationToken.None);
        Assert.Equal(2, buildExit);
        using var buildResponse = JsonDocument.Parse(buildOutput.ToString());
        Assert.Equal("validate", buildResponse.RootElement.GetProperty("operation").GetString());
        Assert.False(buildResponse.RootElement.GetProperty("published").GetBoolean(), "Rejected CLI build published.");
        Assert.Equal(
            "learning_export_cli_usage_invalid",
            buildResponse.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }
    finally
    {
        DeleteTemporaryRoot(root);
    }
}

static Task TestManifestSchemaAsync()
{
    var path = Path.Combine(FindProjectRoot(), "schemas", "learning-dataset-manifest.v1.schema.json");
    using var schema = JsonDocument.Parse(File.ReadAllBytes(path));
    Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
    Assert.Equal(
        LearningExportContractIds.DatasetManifest,
        schema.RootElement.GetProperty("properties").GetProperty("contractId").GetProperty("const").GetString());
    Assert.Equal(
        LearningExportContractIds.Canonicalization,
        schema.RootElement.GetProperty("properties").GetProperty("canonicalization").GetProperty("const").GetString());
    Assert.False(
        schema.RootElement.GetProperty("properties").GetProperty("modelTrainingPerformed").GetProperty("const").GetBoolean(),
        "The manifest schema permits a training claim.");
    var itemProperties = schema.RootElement.GetProperty("$defs").GetProperty("item").GetProperty("properties");
    Assert.True(itemProperties.TryGetProperty("sourceContentHash", out _), "The schema omits pinned source hashes.");
    Assert.True(itemProperties.TryGetProperty("reviewEnvelopeHash", out _), "The schema omits pinned review envelopes.");
    Assert.True(itemProperties.TryGetProperty("redactionDecision", out _), "The schema omits redaction decisions.");
    return Task.CompletedTask;
}

static Fixture CreateFixture(
    string adapterId = LearningExportContractIds.CanonicalJsonlAdapter,
    string adapterVersion = LearningExportContractIds.CanonicalJsonlAdapterVersion)
{
    var first = CreateRecord("record-a", "revision-a", "decision:record-a", "project-one", " First reviewed text.\r\n");
    var second = CreateRecord("record-b", "revision-b", "decision:record-b", "project-one", "Second reviewed text.   ");
    var third = CreateRecord("record-c", "revision-c", "decision:record-c", "project-two", new string('x', 180));
    var records = new[] { third, first, second };
    var bundle = LearningExportFactory.CreateInputBundle(Fixture.CreatedAt, records);
    var definition = CreateDefinition(bundle, records, "fixture-dataset", Fixture.DatasetDecision, adapterId, adapterVersion);
    return new Fixture(bundle, definition, CreateAuthority(records, definition));
}

static LearningRecordRevisionV1 CreateRecord(
    string recordId,
    string revisionId,
    string decisionId,
    string leakageGroup,
    string text) =>
    LearningExportFactory.CreateRecord(
        recordId,
        revisionId,
        Fixture.Namespace,
        "reviewed_knowledge",
        "service:pegasus-cortex",
        $"artifact:{recordId}",
        leakageGroup,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["text"] = text,
            ["title"] = $"Title for {recordId}"
        },
        "internal",
        CreateReview(decisionId),
        Fixture.CreatedAt);

static LearningExportReviewV1 CreateReview(string decisionId) => new(
    true,
    "approved",
    "training_and_evaluation",
    "not_required",
    "passed",
    "passed",
    "passed",
    "passed",
    Fixture.PolicyRevision,
    Fixture.AuthorityId,
    decisionId,
    "operator:reviewer",
    Fixture.CreatedAt);

static LearningDatasetDefinitionV1 CreateDefinition(
    LearningInputBundleV1 bundle,
    IReadOnlyList<LearningRecordRevisionV1> records,
    string datasetId,
    string decisionId,
    string adapterId = LearningExportContractIds.CanonicalJsonlAdapter,
    string adapterVersion = LearningExportContractIds.CanonicalJsonlAdapterVersion) =>
    LearningExportFactory.CreateDefinition(
        datasetId,
        "Fixture dataset",
        bundle.BundleId,
        records.Select(record => new LearningDatasetSelectionV1(
            record.RecordId,
            record.RevisionId,
            record.SourceContentHash,
            record.ReviewEnvelopeHash)),
        [Fixture.Namespace],
        ["evaluation", "retrieval"],
        new LearningSplitPolicyV1(
            LearningExportContractIds.StableSplitAlgorithm,
            "fixture-split-salt-v1",
            8000,
            1000,
            1000),
        [
            new LearningTransformationSpecV1(
                "normalize-line-endings",
                "1",
                new Dictionary<string, string>(StringComparer.Ordinal)),
            new LearningTransformationSpecV1(
                "trim-values",
                "1",
                new Dictionary<string, string>(StringComparer.Ordinal))
        ],
        adapterId,
        adapterVersion,
        Fixture.CreatedAt,
        new LearningDefinitionApprovalV1(
            "approved",
            Fixture.PolicyRevision,
            Fixture.AuthorityId,
            decisionId,
            "operator:export-approver",
            Fixture.CreatedAt));

static LearningExportAuthorityContext CreateAuthority(
    IReadOnlyList<LearningRecordRevisionV1> records,
    LearningDatasetDefinitionV1 definition) =>
    new(
        "operator:builder",
        Fixture.PolicyRevision,
        true,
        false,
        new HashSet<string>(StringComparer.Ordinal) { Fixture.AuthorityId },
        records.ToDictionary(
            record => record.Review.ApprovalDecisionId,
            record => record.ReviewEnvelopeHash,
            StringComparer.Ordinal)
            .Append(new KeyValuePair<string, string>(definition.Approval.DecisionId, definition.DefinitionHash))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal) { Fixture.Namespace },
        ApprovedOutputRoot: null);

static IEnumerable<string> CreateCliArguments(
    Fixture fixture,
    string bundlePath,
    string definitionPath)
{
    yield return "--bundle";
    yield return bundlePath;
    yield return "--definition";
    yield return definitionPath;
    yield return "--subject";
    yield return fixture.Authority.SubjectId;
    yield return "--policy-revision";
    yield return fixture.Authority.PolicyRevision.ToString();
    foreach (var authority in fixture.Authority.TrustedApprovalAuthorities)
    {
        yield return "--authority";
        yield return authority;
    }
    foreach (var decision in fixture.Authority.ApprovedDecisionHashes)
    {
        yield return "--decision";
        yield return $"{decision.Key}={decision.Value}";
    }
    foreach (var @namespace in fixture.Authority.ExportableNamespaces)
    {
        yield return "--namespace";
        yield return @namespace;
    }
}

static string FindProjectRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "schemas", "learning-dataset-manifest.v1.schema.json")))
            return directory.FullName;
    }
    throw new DirectoryNotFoundException("Could not find the Pegasus Cortex project root.");
}

static string CreateTemporaryRoot()
{
    var path = Path.Combine(Path.GetTempPath(), "pegasus-cortex-learning-export-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void DeleteTemporaryRoot(string path)
{
    var full = Path.GetFullPath(path);
    var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pegasus-cortex-learning-export-tests")) +
        Path.DirectorySeparatorChar;
    if (!full.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing to delete outside the test-owned temporary root.");
    if (Directory.Exists(full))
        Directory.Delete(full, recursive: true);
}

static void AssertNoPublishedFiles(string root)
{
    Assert.False(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(),
        "A failed operation left a partial file.");
    Assert.False(Directory.Exists(Path.Combine(root, ".staging")),
        "A failed operation left its staging root.");
}

static string Sha256(ReadOnlySpan<byte> bytes) =>
    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

sealed record Fixture(
    LearningInputBundleV1 Bundle,
    LearningDatasetDefinitionV1 Definition,
    LearningExportAuthorityContext Authority)
{
    public const long PolicyRevision = 7;
    public const string AuthorityId = "authority:nexus-learning-export";
    public const string DatasetDecision = "decision:fixture-dataset";
    public const string Namespace = "project:learning-export-fixture";
    public static readonly DateTimeOffset CreatedAt = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
}

sealed class MisreportedCountList<T>(
    IReadOnlyList<T> values,
    int reportedCount) : IReadOnlyList<T>
{
    public int Count => reportedCount;
    public T this[int index] => values[index];

    public IEnumerator<T> GetEnumerator() => values.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

sealed class FailingAdapter : ILearningDatasetAdapter
{
    public string Id => "failing-fixture";
    public string Version => "1";
    public string RelativeArtifactPath => "failing.jsonl";
    public string MediaType => "application/x-ndjson";

    public async ValueTask WriteItemAsync(
        Stream destination,
        LearningDatasetItemV1 item,
        CancellationToken cancellationToken)
    {
        await destination.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
        throw new IOException("fixture detail must not enter diagnostics");
    }
}

sealed class CancelingAdapter : ILearningDatasetAdapter
{
    public string Id => "canceling-fixture";
    public string Version => "1";
    public string RelativeArtifactPath => "canceling.jsonl";
    public string MediaType => "application/x-ndjson";

    public async ValueTask WriteItemAsync(
        Stream destination,
        LearningDatasetItemV1 item,
        CancellationToken cancellationToken)
    {
        await destination.WriteAsync(new byte[] { 1 }, cancellationToken);
        throw new OperationCanceledException("fixture cancellation", cancellationToken);
    }
}

sealed class EmptyAdapter : ILearningDatasetAdapter
{
    public string Id => "empty-fixture";
    public string Version => "1";
    public string RelativeArtifactPath => "empty.jsonl";
    public string MediaType => "application/x-ndjson";

    public ValueTask WriteItemAsync(
        Stream destination,
        LearningDatasetItemV1 item,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

sealed class OversizeAdapter : ILearningDatasetAdapter
{
    public string Id => "oversize-fixture";
    public string Version => "1";
    public string RelativeArtifactPath => "oversize.jsonl";
    public string MediaType => "application/x-ndjson";

    public ValueTask WriteItemAsync(
        Stream destination,
        LearningDatasetItemV1 item,
        CancellationToken cancellationToken) =>
        destination.WriteAsync(new byte[(32 * 1024) + 1], cancellationToken);
}

sealed class BlockingAdapter : ILearningDatasetAdapter
{
    public string Id => "blocking-fixture";
    public string Version => "1";
    public string RelativeArtifactPath => "blocking.jsonl";
    public string MediaType => "application/x-ndjson";

    public async ValueTask WriteItemAsync(
        Stream destination,
        LearningDatasetItemV1 item,
        CancellationToken cancellationToken) =>
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
}

sealed class MutatingAdapter(IDictionary<string, string> callerPayload) : ILearningDatasetAdapter
{
    private readonly CanonicalJsonlDatasetAdapter _inner = new();

    public string Id => "mutating-fixture";
    public string Version => "1";
    public string RelativeArtifactPath => "mutating.jsonl";
    public string MediaType => "application/x-ndjson";

    public ValueTask WriteItemAsync(
        Stream destination,
        LearningDatasetItemV1 item,
        CancellationToken cancellationToken)
    {
        callerPayload["text"] = "tampered-after-snapshot";
        return _inner.WriteItemAsync(destination, item, cancellationToken);
    }
}

static class Assert
{
    public static void True(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    public static void False(bool value, string message) => True(!value, message);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }

    public static void ContainsCode(string code, LearningDatasetBuildResult result)
    {
        if (!result.Diagnostics.Any(diagnostic => diagnostic.Code == code))
        {
            throw new InvalidOperationException(
                $"Expected diagnostic '{code}', received [{string.Join(", ", result.Diagnostics.Select(item => item.Code))}].");
        }
    }

    public static void ThrowsArgument(Action action, string parameterName)
    {
        try
        {
            action();
        }
        catch (ArgumentException exception) when (
            string.Equals(exception.ParamName, parameterName, StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException(
            $"Expected an argument failure for parameter '{parameterName}' was not observed.");
    }

    public static async Task ThrowsCanceledAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("Expected cancellation was not observed.");
    }

    public static void ThrowsCanceled(Action action)
    {
        try
        {
            action();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("Expected cancellation was not observed.");
    }
}
