using System.Security.Cryptography;
using System.Text.Json;
using Pegasus.Cortex.DefaultLibrary.Contracts;
using Pegasus.Cortex.DefaultLibrary.Import;
using Pegasus.Cortex.DefaultLibrary.Query;
using Pegasus.Cortex.DefaultLibrary.Seeds;
using Pegasus.Cortex.DefaultLibrary.Serialization;
using Pegasus.Cortex.DefaultLibrary.Validation;
using Pegasus.Shared.Diagnostics;

var reservedIsolationPassed = false;
SearchEvidence? searchEvidence = null;
var tests = new (string Name, Func<Task> Run)[]
{
    ("default taxonomy and evidence coverage", TestTaxonomyAndEvidenceAsync),
    ("four-product baseline preserves and supersedes bounded revisions", TestFourProductBaselineAsync),
    ("curated source and active continuity exclusions", TestCuratedSourceExclusionsAsync),
    ("deterministic seed and import", TestDeterministicImportAsync),
    ("duplicate import is idempotent", TestDuplicateImportAsync),
    ("tampered bundle and record are rejected", TestTamperRejectionAsync),
    ("immutable supersession is enforced", TestSupersessionAsync),
    ("authorization filters before retrieval", async () =>
    {
        await TestAuthorizationFilteringAsync();
        reservedIsolationPassed = true;
    }),
    ("lexical ranking is stable", TestSearchRankingStabilityAsync),
    ("broad lexical mode recovers variants without changing exact mode", TestBroadSearchModeAsync),
    ("exact lexical mode excludes partial-term records while broad includes them", TestExactConjunctionAsync),
    ("broad lexical mode meets the documented 20-query local recall fixture", async () =>
        searchEvidence = await TestBroadNaturalLanguageRecallAsync()),
    ("prohibited content is rejected", TestProhibitedContentAsync),
    ("bundle export round trip is exact", TestRoundTripExportAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception)
    {
        failures.Add(test.Name);
        Console.Error.WriteLine($"FAIL {test.Name}");
    }
}

if (failures.Count != 0)
    return 1;

if (!reservedIsolationPassed || searchEvidence is null)
{
    Console.Error.WriteLine("FAIL search-evidence");
    return 1;
}

try
{
    _ = WriteSeedArtifact();
}
catch (Exception)
{
    Console.Error.WriteLine("FAIL seed-artifact");
    return 1;
}

Console.WriteLine(
    $"METRIC exact-search passed={searchEvidence.Value.ExactPassed} total={searchEvidence.Value.ExactTotal} regressions=0");
Console.WriteLine("METRIC reserved-isolation leakage=0");
Console.WriteLine(
    $"METRIC retrieval-recall hits={searchEvidence.Value.RecallHits} total={searchEvidence.Value.RecallTotal}");
return 0;

static Task TestTaxonomyAndEvidenceAsync()
{
    var bundle = DefaultKnowledgeLibrary.Create();
    using var schema = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(FindProjectRoot(), "schemas", "default-library-bundle.v1.schema.json")));
    Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
    var expectedTaxonomies = new[]
    {
        "suite-topology", "contracts-compatibility", "configuration-security", "workflows",
        "diagnostics-failure-signatures", "recovery-rollback", "verification-procedures",
        "decisions-tradeoffs", "glossary", "cortex-self-description-repair"
    };

    foreach (var taxonomy in expectedTaxonomies)
        Assert.True(bundle.Taxonomies.Any(item => item.Code == taxonomy), $"Missing taxonomy '{taxonomy}'.");

    Assert.True(bundle.Records.Count >= 10, "The v1 seed must include substantive coverage.");
    Assert.True(bundle.Records.SelectMany(record => record.Evidence).Any(e => e.Reference.Contains("pegasus-shared", StringComparison.Ordinal)), "Shared evidence missing.");
    Assert.True(bundle.Records.SelectMany(record => record.Evidence).Any(e => e.Reference.Contains("pegasus-nexus", StringComparison.Ordinal)), "Nexus evidence missing.");
    Assert.True(bundle.Records.SelectMany(record => record.Evidence).Any(e => e.Reference.Contains("pegasus-scatterbrain", StringComparison.Ordinal)), "Scatterbrain evidence missing.");
    Assert.True(bundle.Records.SelectMany(record => record.Evidence).Any(e => e.Reference.Contains("pegasus-cortex", StringComparison.Ordinal)), "Cortex evidence missing.");
    Assert.True(bundle.Records.SelectMany(record => record.Evidence).All(e => !e.Reference.StartsWith("coordination-receipt://", StringComparison.Ordinal)), "Coordination receipt evidence entered the curated seed.");
    Assert.True(bundle.Records.Any(record => record.Freshness.State == "blocked"), "Blocked claims must be explicit.");
    Assert.True(bundle.Records.Any(record => record.Freshness.State == "unverified"), "Unverified claims must be explicit.");
    return Task.CompletedTask;
}

static Task TestFourProductBaselineAsync()
{
    var prior = DefaultKnowledgeLibrary.CreatePriorRelease();
    var current = DefaultKnowledgeLibrary.Create();
    var priorBytes = KnowledgeBundleSerializer.Export(prior);
    var priorHash = Convert.ToHexString(SHA256.HashData(priorBytes)).ToLowerInvariant();

    Assert.Equal(16, prior.Records.Count);
    Assert.Equal("b94123ec3ba39d0252546aa06be6f35d96e2f7b90a6bd5cfe99080060e07456b", priorHash);
    Assert.Equal(19, current.Records.Count);
    Assert.Equal(13, current.Records.Count(record =>
        prior.Records.Any(previous => previous.RevisionId == record.RevisionId)));

    foreach (var slug in new[] { "suite-responsibility-map", "shared-contract-release", "observable-verification-ladder" })
    {
        var previous = prior.Records.Single(record => record.Slug == slug);
        var replacement = current.Records.Single(record => record.Slug == slug);
        Assert.SequenceEqual([previous.RevisionId], replacement.Supersession.SupersedesRevisionIds,
            $"The '{slug}' replacement does not supersede the prior immutable revision.");
    }

    Assert.True(current.Records.Any(record => record.Slug == "produced-targets-vs-source-support"), "Platform inventory record missing.");
    Assert.True(current.Records.Any(record => record.Slug == "coder-self-contained-thin-profiles"), "Coder profile record missing.");
    Assert.True(current.Records.Any(record => record.Slug == "server-portable-filesystem-boundary"), "Server filesystem record missing.");
    Assert.Equal(22, current.Records.SelectMany(record => record.Evidence).Select(evidence => evidence.Reference).Distinct(StringComparer.Ordinal).Count());

    var source = current.Records.Single(record => record.Slug == "produced-targets-vs-source-support");
    var unknown = new KnowledgeEvidence(
        "source://pegasus-coder/not-allowlisted.json",
        "application/json",
        new string('0', 64),
        "fixture",
        source.CreatedAt,
        null);
    var changed = source with { Evidence = [.. source.Evidence, unknown] };
    var rejected = DefaultLibrarySourcePolicy.Validate(current with
    {
        Records = [changed, .. current.Records.Where(record => record.RevisionId != source.RevisionId)]
    });
    Assert.False(rejected.Accepted, "An unknown source escaped the closed allowlist.");
    Assert.ContainsCode("default_library_seed_source_forbidden", rejected.Diagnostics);
    return Task.CompletedTask;
}

static Task TestCuratedSourceExclusionsAsync()
{
    var root = FindProjectRoot();
    var operationTaskIds = ReadExcludedOperationTaskIds(root);
    var bundle = DefaultKnowledgeLibrary.Create();
    var sourceValidation = DefaultLibrarySourcePolicy.Validate(bundle);
    var bundleViolations = DefaultLibraryBaselineScanner.ScanBundle(bundle, operationTaskIds);
    var treeViolations = DefaultLibraryBaselineScanner.ScanActiveTree(root, operationTaskIds);
    Assert.True(sourceValidation.Accepted, "The deterministic seed contains a source outside the explicit allowlist.");
    Assert.Equal(0, bundleViolations.Count);
    Assert.Equal(0, treeViolations.Count);
    Assert.True(bundle.Records.SelectMany(record => record.Evidence).All(evidence =>
        evidence.Reference.StartsWith("source://", StringComparison.Ordinal) ||
        evidence.Reference.StartsWith("schema://", StringComparison.Ordinal) ||
        evidence.Reference.StartsWith("package://", StringComparison.Ordinal)),
        "The curated seed contains a non-allowlisted evidence class.");

    var scratch = Path.Combine(Path.GetTempPath(), $"cortex-curated-scan-{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(Path.Combine(scratch, ".cortex"));
        Directory.CreateDirectory(Path.Combine(scratch, "src"));
        var forbiddenProduct = string.Concat("Pegasus", "AI");
        File.WriteAllText(Path.Combine(scratch, "START_HERE.md"), $"{forbiddenProduct} local recovery evidence");
        File.WriteAllText(Path.Combine(scratch, ".cortex", "STATE.md"), $"{forbiddenProduct} exact local reference");
        File.WriteAllText(Path.Combine(scratch, "src", "LeakedSeed.cs"), $"{forbiddenProduct} shared seed leak");
        var fixtureViolations = DefaultLibraryBaselineScanner.ScanActiveTree(scratch);
        Assert.Equal(1, fixtureViolations.Count);
        Assert.True(
            fixtureViolations[0].StartsWith(Path.Combine("src", "LeakedSeed.cs"), StringComparison.Ordinal),
            "The curated scan either included local recovery authority or missed shared source content.");
    }
    finally
    {
        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
    }
    return Task.CompletedTask;
}

static async Task TestDeterministicImportAsync()
{
    var firstBytes = KnowledgeBundleSerializer.Export(DefaultKnowledgeLibrary.Create());
    var secondBytes = KnowledgeBundleSerializer.Export(DefaultKnowledgeLibrary.Create());
    Assert.SequenceEqual(firstBytes, secondBytes, "Repeated seed creation must be byte deterministic.");

    var firstRepository = new InMemoryKnowledgeRepository();
    var secondRepository = new InMemoryKnowledgeRepository();
    var firstImporter = new KnowledgeBundleImporter(new KnowledgeBundleValidator(), firstRepository);
    var secondImporter = new KnowledgeBundleImporter(new KnowledgeBundleValidator(), secondRepository);
    Assert.True((await firstImporter.ImportAsync(DefaultKnowledgeLibrary.CreatePriorRelease(), Context(), CancellationToken.None)).Accepted,
        "First prerequisite seed import failed.");
    Assert.True((await secondImporter.ImportAsync(DefaultKnowledgeLibrary.CreatePriorRelease(), Context(), CancellationToken.None)).Accepted,
        "Second prerequisite seed import failed.");
    var first = await firstImporter.ImportJsonAsync(firstBytes, Context(), CancellationToken.None);
    var second = await secondImporter.ImportJsonAsync(secondBytes, Context(), CancellationToken.None);

    Assert.True(first.Accepted && second.Accepted, "Deterministic imports must succeed.");
    Assert.Equal(6, first.AddedCount);
    Assert.Equal(13, first.IdempotentCount);
    Assert.SequenceEqual(firstRepository.CurrentRevisionIds, secondRepository.CurrentRevisionIds, "Independent imports must produce identical current revisions.");
}

static async Task TestDuplicateImportAsync()
{
    var repository = new InMemoryKnowledgeRepository();
    var importer = new KnowledgeBundleImporter(new KnowledgeBundleValidator(), repository);
    var bundle = DefaultKnowledgeLibrary.Create();
    Assert.True((await importer.ImportAsync(DefaultKnowledgeLibrary.CreatePriorRelease(), Context(), CancellationToken.None)).Accepted,
        "Prerequisite seed import failed.");
    var first = await importer.ImportAsync(bundle, Context(), CancellationToken.None);
    var duplicate = await importer.ImportAsync(bundle, Context(), CancellationToken.None);

    Assert.True(first.Accepted && duplicate.Accepted, "Initial and duplicate imports should succeed.");
    Assert.Equal(6, first.AddedCount);
    Assert.Equal(13, first.IdempotentCount);
    Assert.Equal(0, duplicate.AddedCount);
    Assert.Equal(bundle.Records.Count, duplicate.IdempotentCount);
    Assert.Equal(22, repository.Count);
}

static Task TestTamperRejectionAsync()
{
    var validator = new KnowledgeBundleValidator();
    var original = DefaultKnowledgeLibrary.Create();
    var changedRecord = original.Records[0] with { Content = original.Records[0].Content + " Altered after hashing." };
    var tampered = original with { Records = [changedRecord, .. original.Records.Skip(1)] };
    var result = validator.Validate(tampered);

    Assert.False(result.Accepted, "Tampered content must fail validation.");
    Assert.ContainsCode("default_library_record_tampered", result.Diagnostics);
    Assert.ContainsCode("default_library_bundle_tampered", result.Diagnostics);
    return Task.CompletedTask;
}

static async Task TestSupersessionAsync()
{
    var repository = new InMemoryKnowledgeRepository();
    var importer = new KnowledgeBundleImporter(new KnowledgeBundleValidator(), repository);
    var seed = DefaultKnowledgeLibrary.Create();
    Assert.True((await importer.ImportAsync(DefaultKnowledgeLibrary.CreatePriorRelease(), Context(), CancellationToken.None)).Accepted,
        "Prerequisite seed import failed.");
    Assert.True((await importer.ImportAsync(seed, Context(), CancellationToken.None)).Accepted, "Seed import failed.");

    var current = seed.Records[0];
    var replacement = RevisionFrom(current, current.Content + " This reviewed revision adds a bounded clarification.", [current.RevisionId]);
    var replacementBundle = KnowledgeRecordFactory.CreateBundle(
        "1.2026.07.11.03.20.B", "1.20260711.320-b", seed.GeneratedAt.AddMinutes(1), seed.Taxonomies, [replacement]);
    var accepted = await importer.ImportAsync(replacementBundle, Context(), CancellationToken.None);

    var conflictRevision = RevisionFrom(replacement, replacement.Content + " A conflicting change.", []);
    var conflictBundle = KnowledgeRecordFactory.CreateBundle(
        "1.2026.07.11.03.20.C", "1.20260711.320-c", seed.GeneratedAt.AddMinutes(2), seed.Taxonomies, [conflictRevision]);
    var conflict = await importer.ImportAsync(conflictBundle, Context(), CancellationToken.None);

    Assert.True(accepted.Accepted, "Explicit immutable supersession should succeed.");
    Assert.False(conflict.Accepted, "A changed revision without supersession must fail.");
    Assert.ContainsCode("default_library_supersession_conflict", conflict.Diagnostics);
    Assert.Equal(replacement.RevisionId, (await repository.FindCurrentAsync(current.RecordId, CancellationToken.None))!.RevisionId);
}

static async Task TestAuthorizationFilteringAsync()
{
    var repository = await SeedRepositoryAsync();
    var query = new KnowledgeQueryService(repository);
    var request = new KnowledgeSearchRequest("Cortex identity namespace", Limit: 20);

    var wrongNamespace = await query.SearchAsync(
        request,
        new KnowledgeReadScope(new HashSet<string>(["shared:other"], StringComparer.Ordinal), "internal", true, true),
        CancellationToken.None);
    var sensitivityDenied = await query.SearchAsync(
        request,
        new KnowledgeReadScope(new HashSet<string>([DefaultKnowledgeLibrary.DefaultNamespace], StringComparer.Ordinal), "public", true, true),
        CancellationToken.None);
    var allowed = await query.SearchAsync(
        request,
        Scope(includeBlocked: false),
        CancellationToken.None);
    var blockedHidden = await query.SearchAsync(
        new KnowledgeSearchRequest("recipient encryption provider unavailable", Limit: 20),
        Scope(includeBlocked: false),
        CancellationToken.None);
    var blockedVisible = await query.SearchAsync(
        new KnowledgeSearchRequest("recipient encryption provider unavailable", Limit: 20),
        Scope(includeBlocked: true),
        CancellationToken.None);

    Assert.Equal(0, wrongNamespace.Records.Count);
    Assert.Equal(0, sensitivityDenied.Records.Count);
    Assert.True(allowed.Records.Count > 0, "Authorized search should return records.");
    Assert.True(allowed.Records.All(record => !record.Executable && record.ContentTreatment == KnowledgeContractIds.ContentTreatment), "Agent projection safety fields changed.");
    Assert.False(blockedHidden.Records.Any(record => record.Freshness.State == "blocked"), "Blocked results leaked through the default scope.");
    Assert.True(blockedVisible.Records.Any(record => record.Freshness.State == "blocked"), "Explicit blocked-result scope was not honored.");
}

static async Task TestSearchRankingStabilityAsync()
{
    var repository = await SeedRepositoryAsync();
    repository.ReverseOutputOnAlternateReads = true;
    var query = new KnowledgeQueryService(repository);
    var request = new KnowledgeSearchRequest("Cortex", Limit: 10);

    var first = await query.SearchAsync(request, Scope(includeBlocked: true), CancellationToken.None);
    var second = await query.SearchAsync(request, Scope(includeBlocked: true), CancellationToken.None);

    Assert.True(first.Records.Count > 1, "Ranking stability needs multiple matching records.");
    Assert.SequenceEqual(
        first.Records.Select(record => record.RevisionId),
        second.Records.Select(record => record.RevisionId),
        "Repository order must not affect lexical ranking.");
}

static async Task TestBroadSearchModeAsync()
{
    var repository = await SeedRepositoryAsync();
    var query = new KnowledgeQueryService(repository);
    var exact = await query.SearchAsync(
        new KnowledgeSearchRequest("snapshots", Limit: 10, Mode: KnowledgeSearchMode.Exact),
        Scope(includeBlocked: true),
        CancellationToken.None);
    var broad = await query.SearchAsync(
        new KnowledgeSearchRequest("snapshots", Limit: 10, Mode: KnowledgeSearchMode.Broad),
        Scope(includeBlocked: true),
        CancellationToken.None);

    Assert.Equal(0, exact.Records.Count);
    Assert.True(
        broad.Records.Any(record => record.Title == "Verified snapshot before mutation"),
        "Broad mode did not recover the singular snapshot record from a plural query.");
    Assert.True(broad.Records.All(record =>
        !record.Executable && record.ContentTreatment == KnowledgeContractIds.ContentTreatment),
        "Broad mode bypassed the passive-content safety projection.");

    var invalid = await query.SearchAsync(
        new KnowledgeSearchRequest("snapshot", Mode: (KnowledgeSearchMode)99),
        Scope(includeBlocked: true),
        CancellationToken.None);
    Assert.False(invalid.Accepted, "An unsupported broad-search mode was accepted.");
}

static async Task TestExactConjunctionAsync()
{
    var (repository, target, fixture) = await SeedParityFixtureAsync();
    var query = new KnowledgeQueryService(repository);
    var exact = await query.SearchAsync(
        new KnowledgeSearchRequest(
            fixture.PartialTermQuery,
            Limit: 3,
            Mode: KnowledgeSearchMode.Exact),
        Scope(includeBlocked: true),
        CancellationToken.None);
    var broad = await query.SearchAsync(
        new KnowledgeSearchRequest(
            fixture.PartialTermQuery,
            Limit: 3,
            Mode: KnowledgeSearchMode.Broad),
        Scope(includeBlocked: true),
        CancellationToken.None);

    Assert.True(exact.Accepted, "Exact parity-fixture search was rejected.");
    Assert.False(
        exact.Records.Any(record => record.RecordId == target.RecordId),
        "Exact search returned a record that matched only part of the query.");
    Assert.True(broad.Accepted, "Broad parity-fixture search was rejected.");
    Assert.True(
        broad.Records.Any(record => record.RecordId == target.RecordId),
        "Broad search did not include the partial-term target in its top three.");
}

static async Task<SearchEvidence> TestBroadNaturalLanguageRecallAsync()
{
    // This reproduces the predeclared 20-query acceptance set against a
    // sanitized local form of the published context-retention lesson. It is a
    // provider-neutral regression fixture, not proof against the live ARM64
    // PostgreSQL corpus, which remains separately gated.
    var (repository, target, fixture) = await SeedParityFixtureAsync();
    var queries = fixture.NaturalLanguageQueries;
    var misses = new List<string>();
    foreach (var text in queries)
    {
        var result = await new KnowledgeQueryService(repository).SearchAsync(
            new KnowledgeSearchRequest(text, Limit: 3, Mode: KnowledgeSearchMode.Broad),
            Scope(includeBlocked: true),
            CancellationToken.None);
        Assert.True(result.Accepted, $"Broad retrieval rejected documented query: {text}");
        if (!result.Records.Any(record => record.RecordId == target.RecordId))
            misses.Add(text);
    }

    Assert.True(
        queries.Count - misses.Count >= fixture.MinimumBroadRecallAt3,
        $"Local broad Recall@3 was {queries.Count - misses.Count}/{queries.Count}; misses: {string.Join(" | ", misses)}");
    // Explicit exact and omitted-mode requests must remain byte-order
    // compatible even after broad mode is added.
    const string exactText = "project brief continuation packet";
    var implicitExact = await new KnowledgeQueryService(repository).SearchAsync(
        new KnowledgeSearchRequest(exactText, Limit: 10),
        Scope(includeBlocked: true),
        CancellationToken.None);
    var explicitExact = await new KnowledgeQueryService(repository).SearchAsync(
        new KnowledgeSearchRequest(exactText, Limit: 10, Mode: KnowledgeSearchMode.Exact),
        Scope(includeBlocked: true),
        CancellationToken.None);
    Assert.SequenceEqual(
        implicitExact.Records.Select(record => record.RevisionId),
        explicitExact.Records.Select(record => record.RevisionId),
        "Adding broad mode changed default exact-search ordering.");

    // Preserve the five exact-conjunctive diagnostics used to distinguish a
    // publication/index failure from broad natural-language recall quality.
    var exactQueries = fixture.ExactQueries;
    var exactPassed = 0;
    foreach (var text in exactQueries)
    {
        var result = await new KnowledgeQueryService(repository).SearchAsync(
            new KnowledgeSearchRequest(text, Limit: 3, Mode: KnowledgeSearchMode.Exact),
            Scope(includeBlocked: true),
            CancellationToken.None);
        Assert.True(result.Accepted, $"Exact retrieval rejected diagnostic query: {text}");
        if (result.Records.Any(record => record.RecordId == target.RecordId))
            exactPassed++;
    }

    Assert.Equal(exactQueries.Count, exactPassed);
    return new SearchEvidence(
        exactPassed,
        exactQueries.Count,
        queries.Count - misses.Count,
        queries.Count);
}

static Task TestProhibitedContentAsync()
{
    var seed = DefaultKnowledgeLibrary.Create();
    var source = seed.Records[0];
    var prohibited = RevisionFrom(source, "A prohibited fixture contains password=not-a-real-secret.", []);
    var bundle = KnowledgeRecordFactory.CreateBundle(
        "1.2026.07.11.03.20.D", "1.20260711.320-d", seed.GeneratedAt.AddMinutes(3), seed.Taxonomies, [prohibited]);
    var result = new KnowledgeBundleValidator().Validate(bundle);

    Assert.False(result.Accepted, "Prohibited content must fail validation even when hashes are valid.");
    Assert.ContainsCode("default_library_prohibited_content", result.Diagnostics);
    return Task.CompletedTask;
}

static Task TestRoundTripExportAsync()
{
    var original = DefaultKnowledgeLibrary.Create();
    var first = KnowledgeBundleSerializer.Export(original);
    var imported = KnowledgeBundleSerializer.Deserialize(first);
    var second = KnowledgeBundleSerializer.Export(imported);
    var validation = new KnowledgeBundleValidator().Validate(imported);

    Assert.True(validation.Accepted, "Round-tripped bundle must validate.");
    Assert.SequenceEqual(first, second, "Bundle export/import/export must be byte exact.");
    Assert.Equal(original.BundleHash, imported.BundleHash);
    return Task.CompletedTask;
}

static KnowledgeRecordV1 RevisionFrom(
    KnowledgeRecordV1 source,
    string content,
    IReadOnlyList<string> supersedes) =>
    KnowledgeRecordFactory.Create(
        source.Namespace,
        source.Taxonomy,
        source.Slug,
        source.Title,
        source.Summary,
        content,
        source.Keywords,
        source.Applicability,
        source.Evidence,
        source.Provenance,
        source.Confidence,
        source.Freshness,
        source.Sensitivity,
        source.Retention,
        source.Approval,
        source.Promotion,
        supersedes,
        source.CreatedAt.AddMinutes(1));

static KnowledgeImportContext Context() => new(
    "pegasus-nexus", "test-subject", 1, CanImport: true, CanApprove: true,
    new HashSet<string>([DefaultKnowledgeLibrary.DefaultNamespace], StringComparer.Ordinal));

static KnowledgeReadScope Scope(bool includeBlocked) => new(
    new HashSet<string>([DefaultKnowledgeLibrary.DefaultNamespace], StringComparer.Ordinal),
    "internal",
    IncludeStale: true,
    IncludeBlockedOrUnverified: includeBlocked);

static async Task<InMemoryKnowledgeRepository> SeedRepositoryAsync()
{
    var repository = new InMemoryKnowledgeRepository();
    var importer = new KnowledgeBundleImporter(new KnowledgeBundleValidator(), repository);
    var prior = await importer.ImportAsync(DefaultKnowledgeLibrary.CreatePriorRelease(), Context(), CancellationToken.None);
    Assert.True(prior.Accepted, "Prior test repository seed failed.");
    var result = await importer.ImportAsync(DefaultKnowledgeLibrary.Create(), Context(), CancellationToken.None);
    Assert.True(result.Accepted, "Test repository seed failed.");
    return repository;
}

static async Task<(InMemoryKnowledgeRepository Repository, KnowledgeRecordV1 Target, LexicalParityFixture Fixture)>
    SeedParityFixtureAsync()
{
    var fixture = LoadLexicalParityFixture();
    var repository = await SeedRepositoryAsync();
    var seed = DefaultKnowledgeLibrary.Create();
    var template = seed.Records.First(record => record.Taxonomy == fixture.Target.Taxonomy);
    var target = KnowledgeRecordFactory.Create(
        template.Namespace,
        fixture.Target.Taxonomy,
        "bounded-agent-context-recovery",
        fixture.Target.Title,
        fixture.Target.Summary,
        fixture.Target.Content,
        fixture.Target.Keywords,
        template.Applicability,
        template.Evidence,
        template.Provenance,
        template.Confidence,
        template.Freshness,
        template.Sensitivity,
        template.Retention,
        template.Approval,
        template.Promotion,
        supersedesRevisionIds: null,
        template.CreatedAt.AddMinutes(5));
    var fixtureBundle = KnowledgeRecordFactory.CreateBundle(
        "1.2026.07.31.17.50.A",
        "1.20260731.1750-a",
        seed.GeneratedAt.AddMinutes(5),
        seed.Taxonomies,
        [target]);
    var imported = await new KnowledgeBundleImporter(new KnowledgeBundleValidator(), repository)
        .ImportAsync(fixtureBundle, Context(), CancellationToken.None);
    Assert.True(imported.Accepted, "The shared lexical parity fixture failed validation or import.");
    return (repository, target, fixture);
}

static LexicalParityFixture LoadLexicalParityFixture()
{
    var path = Path.Combine(
        FindProjectRoot(),
        "tests",
        "fixtures",
        "lexical-search-provider-parity.v1.json");
    var fixture = JsonSerializer.Deserialize<LexicalParityFixture>(
        File.ReadAllBytes(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("The shared lexical parity fixture is unavailable.");
    if (fixture.SchemaVersion != 1 || fixture.NaturalLanguageQueries.Count != 20 ||
        fixture.ExactQueries.Count == 0 || fixture.MinimumBroadRecallAt3 is < 1 or > 20)
    {
        throw new InvalidOperationException("The shared lexical parity fixture is malformed or unbounded.");
    }
    return fixture;
}

static (string Path, string Hash) WriteSeedArtifact()
{
    var root = FindProjectRoot();
    var directory = Path.Combine(root, "artifacts", "seeds", DefaultKnowledgeLibrary.ReleaseVersion);
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "pegasus-cortex-default-library.v1.json");
    var bytes = KnowledgeBundleSerializer.Export(DefaultKnowledgeLibrary.Create());
    File.WriteAllBytes(path, bytes);
    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    return (path, hash);
}

static string FindProjectRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PegasusCortex.slnx")))
        directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Pegasus Cortex project root.");
}

static IReadOnlyList<string> ReadExcludedOperationTaskIds(string projectRoot)
{
    var suiteRoot = Directory.GetParent(projectRoot)?.FullName
        ?? throw new DirectoryNotFoundException("Could not locate the Pegasus suite root.");
    var agentId = string.Concat("pegasus", "ai", "-operations");
    var agentRoot = Path.Combine(suiteRoot, "Pegasus Nexus", "coordination", "agents", agentId);
    if (!Directory.Exists(agentRoot))
        return [];

    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var path in Directory.EnumerateFiles(agentRoot, "*.json", SearchOption.AllDirectories))
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        CollectTaskIds(document.RootElement, ids);
    }
    return ids.ToArray();
}

static void CollectTaskIds(JsonElement element, ISet<string> ids)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("task_id") && property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString() is { Length: > 0 } id)
                ids.Add(id);
            else
                CollectTaskIds(property.Value, ids);
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in element.EnumerateArray())
            CollectTaskIds(item, ids);
    }
}

sealed class InMemoryKnowledgeRepository : IKnowledgeRevisionRepository, IKnowledgeReadRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, KnowledgeRecordV1> _revisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _currentByRecord = new(StringComparer.Ordinal);
    private bool _reverseNext;

    public int Count { get { lock (_gate) return _revisions.Count; } }
    public bool ReverseOutputOnAlternateReads { get; set; }
    public IReadOnlyList<string> CurrentRevisionIds
    {
        get { lock (_gate) return _currentByRecord.Values.Order(StringComparer.Ordinal).ToArray(); }
    }

    public ValueTask<KnowledgeRecordV1?> FindRevisionAsync(string revisionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult(_revisions.GetValueOrDefault(revisionId));
    }

    public ValueTask<KnowledgeRecordV1?> FindCurrentAsync(string recordId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _currentByRecord.TryGetValue(recordId, out var revisionId)
                    ? _revisions[revisionId]
                    : null);
        }
    }

    public ValueTask<KnowledgeRepositoryImportResult> ImportImmutableAsync(
        IReadOnlyList<KnowledgeImportItem> items,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var item in items)
            {
                if (_revisions.ContainsKey(item.Record.RevisionId))
                    return ValueTask.FromResult(Reject("test_repository_duplicate", "The revision already exists."));
                _currentByRecord.TryGetValue(item.Record.RecordId, out var current);
                if (!string.Equals(current, item.ExpectedCurrentRevisionId, StringComparison.Ordinal))
                    return ValueTask.FromResult(Reject("test_repository_precondition_failed", "The expected current revision changed."));
            }

            foreach (var item in items)
            {
                _revisions.Add(item.Record.RevisionId, item.Record);
                _currentByRecord[item.Record.RecordId] = item.Record.RevisionId;
            }

            return ValueTask.FromResult(new KnowledgeRepositoryImportResult(true, items.Count, null));
        }
    }

    public ValueTask<IReadOnlyList<KnowledgeRecordV1>> ReadCandidatesAsync(
        KnowledgeRepositoryQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IEnumerable<KnowledgeRecordV1> records = _currentByRecord.Values.Select(id => _revisions[id]);
            records = records.Where(record =>
                (string.IsNullOrWhiteSpace(query.Taxonomy) || record.Taxonomy == query.Taxonomy) &&
                record.Applicability.Any(item =>
                    (string.IsNullOrWhiteSpace(query.ProductId) || item.ProductId == query.ProductId) &&
                    (string.IsNullOrWhiteSpace(query.ComponentId) || item.ComponentId == query.ComponentId)));

            var result = records.Take(query.MaximumCandidates).ToArray();
            if (ReverseOutputOnAlternateReads && _reverseNext)
                Array.Reverse(result);
            _reverseNext = !_reverseNext;
            return ValueTask.FromResult<IReadOnlyList<KnowledgeRecordV1>>(result);
        }
    }

    private static KnowledgeRepositoryImportResult Reject(string code, string message) => new(
        false,
        0,
        new PegasusDiagnostic(code, message, DiagnosticSeverity.Error, "test.default-library.repository"));
}

static class Assert
{
    public static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void False(bool value, string message) => True(!value, message);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException(message);
    }

    public static void ContainsCode(string code, IReadOnlyList<PegasusDiagnostic> diagnostics)
    {
        if (!diagnostics.Any(diagnostic => diagnostic.Code == code))
            throw new InvalidOperationException($"Expected diagnostic '{code}'.");
    }
}

readonly record struct SearchEvidence(
    int ExactPassed,
    int ExactTotal,
    int RecallHits,
    int RecallTotal);

sealed record LexicalParityTarget(
    string Taxonomy,
    string Title,
    string Summary,
    string Content,
    IReadOnlyList<string> Keywords);

sealed record LexicalParityFixture(
    int SchemaVersion,
    LexicalParityTarget Target,
    string PartialTermQuery,
    IReadOnlyList<string> ExactQueries,
    IReadOnlyList<string> NaturalLanguageQueries,
    int MinimumBroadRecallAt3);
