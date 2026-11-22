using Pegasus.Cortex.Persistence.PostgreSql.Tests;

var tests = new (string Name, Func<Task> Run)[]
{
    ("embedded migrations are ordered bounded hashed and security complete", PostgreSqlProviderContractTests.MigrationCatalogAsync),
    ("legacy migration cursor authority is durable append-only and attested", PostgreSqlProviderContractTests.LegacyMigrationCursorAuthorityMigrationAsync),
    ("hybrid project correlation and promotion are immutable and scoped", PostgreSqlProviderContractTests.HybridProjectRetentionMigrationAsync),
    ("audit delegation migration and repository remain append-only scoped and atomic", PostgreSqlProviderContractTests.AuditDelegationMigrationAsync),
    ("learning proposal metadata projects into searchable retained fields", PostgreSqlProviderContractTests.LearningMetadataPromotionAsync),
    ("author-owned publication changes are immutable owner-scoped and CAS-bound", PostgreSqlProviderContractTests.AuthorOwnedPublicationChangeMigrationAsync),
    ("publication-change service is content-safe and preserves exact idempotency", PostgreSqlProviderContractTests.AuthorOwnedPublicationChangeServiceAsync),
    ("agent retention migration and adapters are append-only authenticated and scoped", PostgreSqlAgentRetentionTests.MigrationAndAdapterBoundaryAsync),
    ("agent retention projections authenticate before visibility order and search", PostgreSqlAgentRetentionTests.AuthenticatedProjectionTamperDenialAsync),
    ("offline sync migration is attempt-first idempotent scoped and non-destructive", PostgreSqlProviderContractTests.OfflineSyncMigrationAsync),
    ("sync canonical identities and durable hashes are deterministic", PostgreSqlSyncRepositoryTests.CanonicalIdentityAsync),
    ("sync repository is append-only and readiness is evidence complete", PostgreSqlSyncRepositoryTests.AppendOnlyAndReadinessBoundaryAsync),
    ("sync exact published lookup binds scope bundle and reconciled receipt", PostgreSqlSyncRepositoryTests.ExactPublishedLookupScopeAsync),
    ("migration runner uses a transaction advisory lock and ordered ledger write", PostgreSqlProviderContractTests.MigrationRunnerAsync),
    ("migration history accepts only an exact local prefix", PostgreSqlProviderContractTests.MigrationHistoryAsync),
    ("trusted query and learning scopes map to bounded transaction context", PostgreSqlProviderContractTests.TransactionContextMappingAsync),
    ("transaction context SQL is clean checked parameterized and local", PostgreSqlProviderContractTests.TransactionContextSqlAsync),
    ("transaction runner commits only after clean bind and operation", PostgreSqlProviderContractTests.TransactionCommitSequenceAsync),
    ("transaction runner rolls back cancellation without poisoning", PostgreSqlProviderContractTests.TransactionCancellationAsync),
    ("transaction runner poisons contaminated pooled state", PostgreSqlProviderContractTests.TransactionContaminationAsync),
    ("stored row codec authenticates canonical scope-bound payloads", PostgreSqlRowCodecTests.RoundTripAndTamperDenialAsync),
    ("stored row codec preserves sealed legacy project links byte canonically", PostgreSqlRowCodecTests.LegacyProjectLinkPayloadRemainsCanonicalAsync),
    ("recovery policy is plan-only and evidence-complete", PostgreSqlProviderContractTests.RecoveryPolicyAsync),
    ("query and readiness SQL remain store-bound and allocation-bounded", PostgreSqlProviderContractTests.QueryAndReadinessSqlAsync),
    ("PostgreSQL lexical modes match the shared provider golden corpus", PostgreSqlLexicalParityTests.SharedGoldenCorpusAsync),
    ("PostgreSQL broad candidates resist fifty-row false-prefix crowd-out", PostgreSqlLexicalParityTests.BroadCandidateCrowdOutAsync),
    ("provider project and resolved assets remain driver independent", PostgreSqlProviderContractTests.DependencyBoundaryAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run().ConfigureAwait(false);
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
