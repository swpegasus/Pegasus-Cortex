using System.Net;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Core.Outcomes;
using Pegasus.Cortex.Core.Security;
using Pegasus.Cortex.Core.Distribution;
using Pegasus.Shared.Diagnostics;
using Pegasus.Shared.Outcomes;
using Pegasus.Shared.Schemas;
using Pegasus.Shared.Security;

if(args is ["--semantic-admission-only"])
{
    await CortexSemanticAdmissionTests.RunAsync();
    Console.WriteLine("PASS semantic admission fixtures.");
    return 0;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("V2 semantic admission rejects prohibited bodies before effects", CortexSemanticAdmissionTests.RunAsync),
    ("V2 application enforces namespaces provenance budgets and atomic finish", CortexV2ApplicationTests.RunAsync),
    ("V2 optional personal profile gates access and invalidates stale results", CortexPersonalProfileTests.RunAsync),
    ("V2 knowledge geometry is base-first deterministic and body-free", CortexKnowledgeGeometryTests.RunAsync),
    ("shared schema exposes required safety fields", TestSharedSchemaAsync),
    ("Shared 421 mappings expose no runnable Cortex host", TestShared421MappingAsync),
    ("query service delegates accepted operations with exact scopes", QueryServiceTests.AcceptedDelegationAsync),
    ("query service denies invalid or insufficient authority before source access", QueryServiceTests.DenialBeforeSourceAsync),
    ("query service rejects invalid records and enforces structured provenance", QueryServiceTests.SourceIntegrityAsync),
    ("query service bounds provider and authority enumerables", QueryServiceTests.BoundedEnumerationAsync),
    ("query service propagates caller and source cancellation", QueryServiceTests.CancellationAsync),
    ("query service normalizes source rejection and failure diagnostics", QueryServiceTests.SourceDiagnosticsAsync),
    ("project brief returns hash-bound snapshot delta and unchanged views", ProjectBriefTests.SnapshotDeltaAndUnchangedAsync),
    ("project brief fails closed without exact current snapshot authority", ProjectBriefTests.MissingExpiredAndWrongScopeFailClosedAsync),
    ("project brief enforces canonical response budgets for direct consumers", ProjectBriefTests.CanonicalResponseBudgetAsync),
    ("project brief projector uses only reported ordinal and state deterministically", ProjectBriefProjectorTests.DeterministicOrdinalStateProjectionAsync),
    ("project brief projector preserves exact predecessors and fails closed", ProjectBriefProjectorTests.PredecessorAndFailClosedProjectionAsync),
    ("project brief authority hash matches the local projection vector", ProjectBriefProjectorTests.AuthorityHashCompatibilityVectorAsync),
    ("project brief schema matches Core contracts and package metadata", ProjectBriefSchemaTests.ContractParityAsync),
    ("project brief schema text rules match service and projector validation", ProjectBriefSchemaTests.TextValidationParityAsync),
    ("verified CURRENT projection creates an independent retained revision", CurrentProjectionTests.VerifiedProjectionCreatesIndependentRetainedRevisionAsync),
    ("CURRENT authority mismatch and staleness fail closed", CurrentProjectionTests.AuthorityMismatchAndStalenessFailClosedAsync),
    ("project correlation is deterministic public and non-authorizing", ProjectCorrelationTests.DeterministicSafeLinkAsync),
    ("project correlation hash tampering fails closed", ProjectCorrelationTests.CorrelationTamperFailsClosedAsync),
    ("sync application authorizes validates commits reconciles and authenticates receipts", SyncApplicationServiceTests.RunAsync),
    ("sync historical lookup extends rather than mutates the published-head interface", SyncApplicationServiceTests.PublishedRepositoryCompatibilityAsync),
    ("valid outcome is normalized and retained", TestValidIngestionAsync),
    ("namespace authority is server derived", TestNamespaceDenialAsync),
    ("raw terminal and executable content are rejected", TestProhibitedContentAsync),
    ("training eligibility requires approval", TestTrainingEligibilityAsync),
    ("approval state requires server authority", TestApprovalAuthorityAsync),
    ("supersession is immutable and conflict safe", TestSupersessionAsync),
    ("service transport requires TLS mTLS and envelope", TestTransportAsync),
    ("bootstrap is loopback only and closes after issuance", TestBootstrapAsync),
    ("confidential envelopes fail closed", TestConfidentialEnvelopeAsync),
    ("certificate issuer boundary fails closed", TestUnavailableIssuerAsync)
};

if (args.Contains("--personal-profile-only", StringComparer.Ordinal))
{
    tests = tests.Where(test => test.Run == CortexPersonalProfileTests.RunAsync).ToArray();
    if (tests.Length != 1)
        throw new InvalidOperationException("The personal-profile selector must identify exactly one Core fixture.");
}

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
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"RESULT {tests.Length - failures.Count}/{tests.Length} passed");
return failures.Count == 0 ? 0 : 1;

static Task TestSharedSchemaAsync()
{
    var schema = ContractSchemaCatalog.Read("agent-outcome.v1.schema.json");
    Assert.Contains("\"executable\"", schema);
    Assert.Contains("\"trainingEligibility\"", schema);
    Assert.Contains("\"contentTreatment\"", schema);
    Assert.Contains("\"supersession\"", schema);
    Assert.Contains("\"retention\"", schema);
    return Task.CompletedTask;
}

static Task TestShared421MappingAsync()
{
    var root = FindProjectRoot();
    Assert.True(ContractSchemaCatalog.Names.Contains(
        CortexSharedContractMappings.DeploymentSchemaName,
        StringComparer.Ordinal));
    var schema = ContractSchemaCatalog.Read(CortexSharedContractMappings.DeploymentSchemaName);
    Assert.Contains("pegasus.deployment-manifest", schema);
    Assert.False(CortexSharedContractMappings.RunnableHostExists);
    Assert.Equal(5, CortexSharedContractMappings.Interfaces.Count);
    Assert.True(CortexSharedContractMappings.Interfaces.Any(mapping =>
        mapping.InterfaceType == typeof(IOutcomeRepository).FullName));
    Assert.True(CortexSharedContractMappings.Interfaces.All(mapping =>
        !string.IsNullOrWhiteSpace(mapping.ContractId) &&
        !string.IsNullOrWhiteSpace(mapping.ContractVersion) &&
        !string.IsNullOrWhiteSpace(mapping.InterfaceType) &&
        !string.IsNullOrWhiteSpace(mapping.PackageId) &&
        !string.IsNullOrWhiteSpace(mapping.ReferenceSourcePath)));
    Assert.True(CortexSharedContractMappings.Interfaces.All(mapping =>
        File.Exists(Path.Combine(
            root,
            mapping.ReferenceSourcePath.Replace('/', Path.DirectorySeparatorChar)))));
    return Task.CompletedTask;
}

static string FindProjectRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PegasusCortex.slnx")))
        directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Pegasus Cortex project root.");
}

static async Task TestValidIngestionAsync()
{
    var repository = new InMemoryOutcomeRepository();
    var now = DateTimeOffset.Parse("2026-07-11T16:00:00Z");
    var service = new OutcomeIngestionService(repository, new FixedTimeProvider(now));
    var outcome = Samples.Outcome("outcome-1", now, retentionClass: "operational");

    var result = await service.IngestAsync(outcome, Samples.Caller(), CancellationToken.None);

    Assert.True(result.Accepted);
    Assert.Equal("project:project-1", result.Outcome!.Namespace);
    Assert.Equal("untrusted_data", result.Outcome.ContentTreatment);
    Assert.False(result.Outcome.Executable);
    Assert.Equal(now.AddDays(180), result.Outcome.Retention.RetainUntil);
    Assert.MatchesLowerHash(result.Outcome.ContentHash);
    Assert.MatchesLowerHash(result.SubmittedPayloadHash);
    Assert.Equal(1, repository.Count);
}

static async Task TestNamespaceDenialAsync()
{
    var now = DateTimeOffset.Parse("2026-07-11T16:00:00Z");
    var service = new OutcomeIngestionService(new InMemoryOutcomeRepository(), new FixedTimeProvider(now));
    var outcome = Samples.Outcome("outcome-denied", now) with { Namespace = "user:someone-else" };

    var result = await service.IngestAsync(outcome, Samples.Caller(), CancellationToken.None);

    Assert.False(result.Accepted);
    Assert.Equal("outcome_namespace_forbidden", result.Diagnostic!.Code);
    Assert.MatchesLowerHash(result.SubmittedPayloadHash);
}

static async Task TestProhibitedContentAsync()
{
    var now = DateTimeOffset.Parse("2026-07-11T16:00:00Z");
    var repository = new InMemoryOutcomeRepository();
    var service = new OutcomeIngestionService(repository, new FixedTimeProvider(now));
    var rawDump = Samples.Outcome("outcome-dump", now) with
    {
        Result = Samples.Json("""{"rawTerminal":"full shell history"}""")
    };
    var executable = Samples.Outcome("outcome-exec", now) with { Executable = true };

    var rawResult = await service.IngestAsync(rawDump, Samples.Caller(), CancellationToken.None);
    var execResult = await service.IngestAsync(executable, Samples.Caller(), CancellationToken.None);

    Assert.Equal("outcome_prohibited_content", rawResult.Diagnostic!.Code);
    Assert.Equal("outcome_executable_forbidden", execResult.Diagnostic!.Code);
    Assert.Equal(0, repository.Count);
}

static async Task TestTrainingEligibilityAsync()
{
    var now = DateTimeOffset.Parse("2026-07-11T16:00:00Z");
    var service = new OutcomeIngestionService(new InMemoryOutcomeRepository(), new FixedTimeProvider(now));
    var outcome = Samples.Outcome("outcome-training", now) with { TrainingEligibility = true };

    var result = await service.IngestAsync(outcome, Samples.Caller(), CancellationToken.None);

    Assert.False(result.Accepted);
    Assert.Equal("outcome_training_eligibility_forbidden", result.Diagnostic!.Code);
}

static async Task TestApprovalAuthorityAsync()
{
    var now = DateTimeOffset.Parse("2026-07-11T16:00:00Z");
    var service = new OutcomeIngestionService(new InMemoryOutcomeRepository(), new FixedTimeProvider(now));
    var outcome = Samples.Outcome("outcome-self-approved", now) with
    {
        Approval = new AgentOutcomeApproval("approved", 7, "user-1", now)
    };

    var result = await service.IngestAsync(outcome, Samples.Caller(), CancellationToken.None);

    Assert.False(result.Accepted);
    Assert.Equal("outcome_approval_forbidden", result.Diagnostic!.Code);
}

static async Task TestSupersessionAsync()
{
    var now = DateTimeOffset.Parse("2026-07-11T16:00:00Z");
    var repository = new InMemoryOutcomeRepository();
    var service = new OutcomeIngestionService(repository, new FixedTimeProvider(now));
    var first = await service.IngestAsync(Samples.Outcome("outcome-old", now), Samples.Caller(), CancellationToken.None);
    var replacement = Samples.Outcome("outcome-new", now.AddMinutes(1)) with
    {
        Supersession = new AgentOutcomeSupersession(["outcome-old"])
    };
    var accepted = await service.IngestAsync(replacement, Samples.Caller(), CancellationToken.None);
    var conflict = await service.IngestAsync(
        Samples.Outcome("outcome-other", now.AddMinutes(2)) with
        {
            Supersession = new AgentOutcomeSupersession(["outcome-old"])
        },
        Samples.Caller(),
        CancellationToken.None);

    Assert.True(first.Accepted);
    Assert.True(accepted.Accepted);
    Assert.False(conflict.Accepted);
    Assert.Equal("outcome_supersession_conflict", conflict.Diagnostic!.Code);
    Assert.Equal(2, repository.Count);
}

static Task TestTransportAsync()
{
    var descriptor = CortexTransportSecurity.CreateServiceDescriptor(
        "os-cert:cortex/current",
        "os-trust:nexus/services",
        intermediaryCarriesConfidentialPayload: true);
    var secure = CortexTransportSecurity.EvaluateServiceEndpoint(
        new Uri("https://cortex.internal.example/mcp"), descriptor, true);
    var plaintext = CortexTransportSecurity.EvaluateServiceEndpoint(
        new Uri("http://cortex.internal.example/mcp"), descriptor, true);
    var noEnvelope = CortexTransportSecurity.EvaluateServiceEndpoint(
        new Uri("https://cortex.internal.example/mcp"),
        descriptor with { RecipientEncryptionRequired = false, EnvelopeContract = null },
        true);

    Assert.True(secure.Allowed);
    Assert.False(plaintext.Allowed);
    Assert.ContainsCode("communication_plaintext_forbidden", plaintext.Diagnostics);
    Assert.False(noEnvelope.Allowed);
    Assert.ContainsCode("communication_recipient_encryption_required", noEnvelope.Diagnostics);
    return Task.CompletedTask;
}

static Task TestBootstrapAsync()
{
    var availability = new MutableCertificateAvailability();
    var window = new CortexBootstrapWindow(availability);
    Assert.True(window.TryOpen());

    var local = CortexTransportSecurity.EvaluateBootstrapRequest(
        IPAddress.Loopback, false, false, window.IsOpen, "csr", false);
    var remote = CortexTransportSecurity.EvaluateBootstrapRequest(
        IPAddress.Parse("192.0.2.10"), false, false, window.IsOpen, "csr", false);
    var forwarded = CortexTransportSecurity.EvaluateBootstrapRequest(
        IPAddress.Loopback, true, false, window.IsOpen, "csr", false);
    var confidential = CortexTransportSecurity.EvaluateBootstrapRequest(
        IPAddress.Loopback, false, false, window.IsOpen, "csr", true);

    Assert.True(local.Allowed);
    Assert.False(remote.Allowed);
    Assert.False(forwarded.Allowed);
    Assert.False(confidential.Allowed);

    availability.HasUsableCertificate = true;
    window.CertificateIssued();
    Assert.False(window.IsOpen);
    Assert.False(window.TryOpen());
    return Task.CompletedTask;
}

static async Task TestConfidentialEnvelopeAsync()
{
    var now = DateTimeOffset.Parse("2026-07-11T16:00:00Z");
    var reader = new CortexConfidentialOutcomeReader(new FailClosedConfidentialMessageProtector());
    var envelope = Samples.Envelope(now);

    var unavailable = await reader.ReadAsync(envelope, "pegasus-cortex", 4, now, CancellationToken.None);
    var wrongRecipient = await reader.ReadAsync(envelope, "different-service", 4, now, CancellationToken.None);
    var staleKey = await reader.ReadAsync(envelope, "pegasus-cortex", 5, now, CancellationToken.None);

    Assert.Equal("confidential_messaging_provider_unavailable", unavailable.Diagnostic!.Code);
    Assert.Equal("confidential_envelope_wrong_recipient", wrongRecipient.Diagnostic!.Code);
    Assert.Equal("confidential_envelope_key_stale", staleKey.Diagnostic!.Code);
}

static async Task TestUnavailableIssuerAsync()
{
    var issuer = new CortexUnavailableCertificateBootstrapIssuer();
    var result = await issuer.RequestAsync(
        new CertificateBootstrapRequest(
            1, "pegasus.certificate-bootstrap", "pegasus-cortex", "instance-1", "machine-1",
            "sanitized-csr-placeholder", "correlation-1"),
        CancellationToken.None);

    Assert.False(result.Accepted);
    Assert.Equal("issuer-unavailable", result.State);
    Assert.Equal("certificate_bootstrap_issuer_unavailable", result.Diagnostic!.Code);
}

static class Samples
{
    public static OutcomeCallerContext Caller() => new(
        "tenant-1", "nexus", "user-1", "pegasus-coder", "connection-1", 7,
        [
            new OutcomeNamespaceGrant("project:project-1", CanWrite: true),
            new OutcomeNamespaceGrant("shared:verified", CanWrite: false, CanProposePromotion: true)
        ]);

    public static AgentOutcomeV1 Outcome(
        string outcomeId,
        DateTimeOffset createdAt,
        string retentionClass = "operational") =>
        new(
            SchemaVersion: 1,
            OutcomeId: outcomeId,
            TenantId: "tenant-1",
            Namespace: "project:project-1",
            RecordKind: "agent-outcome",
            OutcomeKind: "verified-solution",
            RecordStatus: "active",
            Result: Json("""{"conclusion":"The bounded operation completed successfully."}"""),
            PublicSummary: "A verified, non-executable outcome.",
            Source: new AgentOutcomeSource(
                "pegasus-coder", "coder-agent-1", "machine-1", "operation-1", "task-1", "correlation-1", createdAt),
            Evidence: [new AgentOutcomeEvidence(
                "artifact://verification/1", "application/json", new string('a', 64), "verified")],
            Provenance: new AgentOutcomeProvenance("pegasus.agent-outcome/1.0", [], ["redacted", "summarized"]),
            Sensitivity: "internal",
            Confidence: new AgentOutcomeConfidence(0.95, "observable verification"),
            Retention: new AgentOutcomeRetention(retentionClass, null, false),
            Supersession: new AgentOutcomeSupersession([]),
            Approval: new AgentOutcomeApproval("pending", 7, null, null),
            Promotion: new AgentOutcomePromotion("none", null, null),
            TrainingEligibility: false,
            ContentTreatment: "untrusted_data",
            Executable: false,
            CreatedAt: createdAt,
            ContentHash: new string('0', 64));

    public static ConfidentialEnvelopeV1 Envelope(DateTimeOffset now) => new(
        1, "pegasus.confidential-envelope", "envelope-1", "pegasus-coder", "pegasus-cortex",
        "cortex-key-4", 4, "ECDH-ES+A256KW", "A256GCM", CortexTransportSecurity.OutcomeContentType,
        "protected-payload", now.AddMinutes(-1), now.AddMinutes(4), "correlation-1");

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

sealed class MutableCertificateAvailability : ICertificateAvailability
{
    public bool HasUsableCertificate { get; set; }
}

sealed class InMemoryOutcomeRepository : IOutcomeRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentOutcomeV1> _outcomes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _superseded = new(StringComparer.Ordinal);

    public int Count { get { lock (_gate) return _outcomes.Count; } }

    public ValueTask<StoredOutcome?> FindAsync(string tenantId, string outcomeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_outcomes.TryGetValue(Key(tenantId, outcomeId), out var outcome))
                return ValueTask.FromResult<StoredOutcome?>(null);

            var readView = _superseded.Contains(Key(tenantId, outcomeId))
                ? outcome with { RecordStatus = "superseded" }
                : outcome;
            return ValueTask.FromResult<StoredOutcome?>(new StoredOutcome(readView, outcome.CreatedAt));
        }
    }

    public ValueTask<OutcomeRepositoryWriteResult> AddImmutableAsync(
        AgentOutcomeV1 outcome,
        IReadOnlyList<string> expectedActiveSupersededOutcomeIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = Key(outcome.TenantId, outcome.OutcomeId);
            if (_outcomes.ContainsKey(key))
                return ValueTask.FromResult(Reject("outcome_duplicate", "The outcome identifier already exists."));

            foreach (var id in expectedActiveSupersededOutcomeIds)
            {
                var oldKey = Key(outcome.TenantId, id);
                if (!_outcomes.ContainsKey(oldKey) || _superseded.Contains(oldKey))
                    return ValueTask.FromResult(Reject("outcome_supersession_conflict", "A supersession precondition changed."));
            }

            _outcomes.Add(key, outcome);
            foreach (var id in expectedActiveSupersededOutcomeIds)
                _superseded.Add(Key(outcome.TenantId, id));

            return ValueTask.FromResult(new OutcomeRepositoryWriteResult(true, null));
        }
    }

    private static string Key(string tenantId, string outcomeId) => $"{tenantId}\n{outcomeId}";
    private static OutcomeRepositoryWriteResult Reject(string code, string message) => new(
        false,
        new PegasusDiagnostic(code, message, DiagnosticSeverity.Error, "test.repository.add"));
}

static class Assert
{
    public static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    public static void False(bool value) => True(!value);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected text to contain '{expected}'.");
    }

    public static void ContainsCode(string code, IReadOnlyList<PegasusDiagnostic> diagnostics)
    {
        if (!diagnostics.Any(d => string.Equals(d.Code, code, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Expected diagnostic '{code}'.");
    }

    public static void MatchesLowerHash(string value)
    {
        if (value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidOperationException("Expected a lowercase SHA-256 hash.");
    }
}
