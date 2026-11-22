using System.Text.Json;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Serialization;

var tests = new List<(string Name, Action Run)>
{
    ("proposal normalization and hash are deterministic", TestDeterministicProposal),
    ("proposal 1.1 metadata is bounded canonical and v1 compatible", TestProposalMetadataV11),
    ("authority and namespace default deny", TestAuthorityDenial),
    ("raw streams secrets and private reasoning are rejected", TestProhibitedContent),
    ("learning use requires compatible rights", TestUsageRights),
    ("review is independent and authority gated", TestReviewGate),
    ("deterministic retention screening separates collective and reserved content", TestRetentionScreening),
    ("collective publication generalizes project coordinates deterministically", TestCollectiveProjection),
    ("proposal ingress redacts bounded secrets before identity and persistence", TestProposalSecretSanitization),
    ("provisional publication is narrow and visibly distinct", TestProvisionalPublication),
    ("publication changes bind deterministic CAS intent to server authority", TestPublicationChange),
    ("publication changes require safe replacements and bounded withdrawals", TestPublicationChangeValidation),
    ("audit assessment and delegated review grants are independent and bounded", TestAuditAndDelegation),
    ("malformed and oversized boundaries fail closed", TestMalformedAndOversized),
    ("cancellation is observed", TestCancellation),
    ("language neutral schemas retain safety bounds", TestSchemas)
};
tests.AddRange(QueryContractSourceTests.All);
tests.AddRange(QueryFingerprintSourceTests.All);
tests.AddRange(SyncContractSourceTests.All);
tests.AddRange(AgentCoordinationContractSourceTests.All);
tests.AddRange(CanonicalTimestampGoldenTests.All);

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add(test.Name);
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"RESULT {tests.Count - failures.Count}/{tests.Count} passed");
return failures.Count == 0 ? 0 : 1;

static void TestDeterministicProposal()
{
    var now = DateTimeOffset.Parse("2026-07-14T05:30:00Z");
    var first = Samples.Proposal(now) with
    {
        Payload = new Dictionary<string, string> { ["zeta"] = "last", ["alpha"] = "first" },
        IntendedUses = ["retrieval", "evaluation"]
    };
    var second = Samples.Proposal(now) with
    {
        Payload = new Dictionary<string, string> { ["alpha"] = "first", ["zeta"] = "last" },
        IntendedUses = ["evaluation", "retrieval"]
    };

    var firstResult = LearningFeedbackPolicy.AcceptProposal(first, Samples.Submitter(), now, CancellationToken.None);
    var secondResult = LearningFeedbackPolicy.AcceptProposal(second, Samples.Submitter(), now, CancellationToken.None);

    Assert.True(firstResult.Accepted);
    Assert.True(secondResult.Accepted);
    Assert.Equal(firstResult.Value!.ProposalHash, secondResult.Value!.ProposalHash);
    Assert.Equal("evaluation,retrieval", string.Join(',', firstResult.Value.IntendedUses));
    Assert.MatchesHash(firstResult.Value.ProposalHash);
    Assert.MatchesHash(firstResult.SubmittedPayloadHash!);
    Assert.Equal(TimeSpan.Zero, firstResult.Value.Screening.DecidedAt.Offset);
    Assert.False(firstResult.Value.IntendedUses is string[]);
    Assert.False(firstResult.Value.Evidence is LearningFeedbackEvidenceV1[]);
    Assert.Throws<NotSupportedException>(() =>
        ((IList<string>)firstResult.Value.IntendedUses)[0] = "fine_tuning");
}

static void TestProposalMetadataV11()
{
    var now = DateTimeOffset.Parse("2026-08-07T18:00:00Z");
    var legacy = LearningFeedbackPolicy.AcceptProposal(
        Samples.Proposal(now),
        Samples.Submitter(),
        now,
        CancellationToken.None);
    Assert.True(legacy.Accepted);
    Assert.Equal(LearningFeedbackContractIds.Proposal, legacy.Value!.ContractId);
    Assert.True(legacy.Value.Taxonomy is null);
    Assert.True(legacy.Value.Keywords is null);
    Assert.True(legacy.Value.Applicability is null);
    using (var legacyCanonical = JsonDocument.Parse(
               CortexCanonicalJson.Serialize(legacy.Value)))
    {
        Assert.False(legacyCanonical.RootElement.TryGetProperty("taxonomy", out _));
        Assert.False(legacyCanonical.RootElement.TryGetProperty("keywords", out _));
        Assert.False(legacyCanonical.RootElement.TryGetProperty("applicability", out _));
    }
    var legacyWireHash = CortexCanonicalJson.Sha256(
        CortexCanonicalJson.Serialize(new
        {
            legacy.Value.SchemaVersion,
            legacy.Value.ContractId,
            legacy.Value.Canonicalization,
            legacy.Value.ProposalId,
            legacy.Value.TenantId,
            legacy.Value.Namespace,
            legacy.Value.FeedbackKind,
            legacy.Value.Source,
            legacy.Value.Summary,
            legacy.Value.Payload,
            legacy.Value.IntendedUses,
            legacy.Value.Evidence,
            legacy.Value.Screening,
            legacy.Value.ReviewState,
            legacy.Value.ContentTreatment,
            legacy.Value.Executable,
            legacy.Value.CreatedAt,
            ProposalHash = new string('0', 64)
        }));
    Assert.Equal(legacyWireHash, legacy.Value.ProposalHash);

    var first = Samples.Proposal(now) with
    {
        ContractId = LearningFeedbackContractIds.ProposalV1_1,
        Taxonomy = "testing",
        Keywords = ["rollback", "bounded recovery"],
        Applicability =
        [
            new LearningFeedbackApplicabilityV1("pegasus-cortex", "persistence"),
            new LearningFeedbackApplicabilityV1("pegasus-coder", "coordination")
        ]
    };
    var reordered = first with
    {
        Keywords = ["bounded recovery", "rollback"],
        Applicability = first.Applicability!.Reverse().ToArray()
    };
    var accepted = LearningFeedbackPolicy.AcceptProposal(
        first,
        Samples.Submitter(),
        now,
        CancellationToken.None);
    var acceptedReordered = LearningFeedbackPolicy.AcceptProposal(
        reordered,
        Samples.Submitter(),
        now,
        CancellationToken.None);
    var changed = LearningFeedbackPolicy.AcceptProposal(
        first with { Keywords = ["rollback", "bounded restore"] },
        Samples.Submitter(),
        now,
        CancellationToken.None);

    Assert.True(accepted.Accepted);
    Assert.True(acceptedReordered.Accepted);
    Assert.True(changed.Accepted);
    Assert.Equal(accepted.Value!.ProposalHash, acceptedReordered.Value!.ProposalHash);
    Assert.False(string.Equals(
        accepted.Value.ProposalHash,
        changed.Value!.ProposalHash,
        StringComparison.Ordinal));
    Assert.Equal("bounded recovery,rollback", string.Join(',', accepted.Value.Keywords!));
    Assert.Equal(
        "pegasus-coder/coordination,pegasus-cortex/persistence",
        string.Join(',', accepted.Value.Applicability!.Select(item =>
            $"{item.ProductId}/{item.ComponentId}")));

    Assert.Code(
        "learning_feedback_metadata_contract_invalid",
        LearningFeedbackPolicy.AcceptProposal(
            Samples.Proposal(now) with { Keywords = ["legacy-metadata"] },
            Samples.Submitter(),
            now,
            CancellationToken.None));
    Assert.Code(
        "learning_feedback_snapshot_limit",
        LearningFeedbackPolicy.AcceptProposal(
            first with
            {
                Keywords = Enumerable.Range(
                        0,
                        LearningFeedbackContractLimits.MaximumProposalKeywords + 1)
                    .Select(index => $"keyword-{index}")
                    .ToArray()
            },
            Samples.Submitter(),
            now,
            CancellationToken.None));
    Assert.Code(
        "learning_feedback_metadata_prohibited",
        LearningFeedbackPolicy.AcceptProposal(
            first with { Keywords = ["api_key=sk-examplevalue12345678901234567890"] },
            Samples.Submitter(),
            now,
            CancellationToken.None));

    var screened = LearningFeedbackContentScreening.Screen(
        "Sanitized title",
        "Sanitized summary",
        "Sanitized content",
        collective: true,
        taxonomy: null,
        keywords: [@"C:\Users\example\private-project"],
        applicability: []);
    Assert.False(screened.Accepted);
    Assert.Contains("absolute_windows_path", string.Join(',', screened.FindingCodes));
}

static void TestAuthorityDenial()
{
    var now = DateTimeOffset.Parse("2026-07-14T05:30:00Z");
    var proposal = Samples.Proposal(now);
    var wrongTenant = LearningFeedbackPolicy.AcceptProposal(
        proposal,
        Samples.Submitter() with { TenantId = "tenant-other" },
        now,
        CancellationToken.None);
    var noGrant = LearningFeedbackPolicy.AcceptProposal(
        proposal,
        Samples.Submitter() with { NamespaceGrants = [] },
        now,
        CancellationToken.None);

    Assert.Code("learning_feedback_authority_mismatch", wrongTenant);
    Assert.Code("learning_feedback_namespace_forbidden", noGrant);
    Assert.MatchesHash(wrongTenant.SubmittedPayloadHash!);
    Assert.MatchesHash(noGrant.SubmittedPayloadHash!);

    var deniedRead = LearningFeedbackPolicy.AuthorizeNamespace(
        Samples.Submitter(),
        proposal.Namespace,
        LearningFeedbackNamespaceCapability.Read,
        CancellationToken.None);
    var allowedRead = LearningFeedbackPolicy.AuthorizeNamespace(
        Samples.Submitter() with
        {
            NamespaceGrants =
            [new LearningFeedbackNamespaceGrant(proposal.Namespace, CanSubmit: true, CanRead: true)]
        },
        proposal.Namespace,
        LearningFeedbackNamespaceCapability.Read,
        CancellationToken.None);
    Assert.Code("learning_feedback_namespace_forbidden", deniedRead);
    Assert.True(allowedRead.Accepted);
    Assert.Equal(proposal.Namespace, allowedRead.Value!.Namespace);
}

static void TestProhibitedContent()
{
    var now = DateTimeOffset.Parse("2026-07-14T05:30:00Z");
    var raw = Samples.Proposal(now) with { Payload = new Dictionary<string, string> { ["rawTranscript"] = "messages" } };
    var reasoning = Samples.Proposal(now) with { Summary = "private reasoning: hidden trace" };
    var secret = Samples.Proposal(now) with { Payload = new Dictionary<string, string> { ["observation"] = "Bearer token-value" } };

    Assert.Code("learning_feedback_payload_invalid", LearningFeedbackPolicy.AcceptProposal(raw, Samples.Submitter(), now, CancellationToken.None));
    Assert.Code("learning_feedback_summary_invalid", LearningFeedbackPolicy.AcceptProposal(reasoning, Samples.Submitter(), now, CancellationToken.None));
    Assert.Code("learning_feedback_payload_invalid", LearningFeedbackPolicy.AcceptProposal(secret, Samples.Submitter(), now, CancellationToken.None));
}

static void TestUsageRights()
{
    var now = DateTimeOffset.Parse("2026-07-14T05:30:00Z");
    var proposal = Samples.Proposal(now) with
    {
        IntendedUses = ["distillation"],
        Screening = Samples.Screening(now) with { UsageRights = "evaluation" }
    };
    Assert.Code(
        "learning_feedback_usage_rights_insufficient",
        LearningFeedbackPolicy.AcceptProposal(proposal, Samples.Submitter(), now, CancellationToken.None));
}

static void TestReviewGate()
{
    var now = DateTimeOffset.Parse("2026-07-14T05:30:00Z");
    var proposal = LearningFeedbackPolicy.AcceptProposal(
        Samples.Proposal(now), Samples.Submitter(), now, CancellationToken.None).Value!;
    var decision = Samples.Decision(proposal, now.AddMinutes(1));

    var selfReview = LearningFeedbackPolicy.AcceptReviewDecision(
        decision,
        proposal,
        Samples.Reviewer() with { SubjectId = proposal.Source.SubjectId },
        now.AddMinutes(1),
        CancellationToken.None);
    var noGrant = LearningFeedbackPolicy.AcceptReviewDecision(
        decision,
        proposal,
        Samples.Reviewer() with { NamespaceGrants = [] },
        now.AddMinutes(1),
        CancellationToken.None);
    var accepted = LearningFeedbackPolicy.AcceptReviewDecision(
        decision,
        proposal,
        Samples.Reviewer(),
        now.AddMinutes(1),
        CancellationToken.None);
    var authorityQualifiedReviewer = Samples.Reviewer() with
    {
        AuthorityId = "review-board",
        SubjectId = proposal.Source.SubjectId
    };
    var authorityQualified = LearningFeedbackPolicy.AcceptReviewDecision(
        decision with
        {
            AuthorityId = authorityQualifiedReviewer.AuthorityId,
            ReviewerSubjectId = authorityQualifiedReviewer.SubjectId
        },
        proposal,
        authorityQualifiedReviewer,
        now.AddMinutes(1),
        CancellationToken.None);

    Assert.Code("learning_feedback_review_independence_required", selfReview);
    Assert.Code("learning_feedback_review_forbidden", noGrant);
    Assert.True(accepted.Accepted);
    Assert.MatchesHash(accepted.Value!.DecisionHash);
    Assert.True(authorityQualified.Accepted);
}

static void TestRetentionScreening()
{
    const string machineSpecific = "The service is installed at D:\\Pegasus\\Cortex and listens on 10.10.10.15.";
    var collective = LearningFeedbackContentScreening.Screen(
        "Bounded service observation",
        "A machine-specific operational detail.",
        machineSpecific,
        collective: true);
    var reserved = LearningFeedbackContentScreening.Screen(
        "Bounded service observation",
        "A machine-specific operational detail.",
        machineSpecific,
        collective: false);
    var secret = LearningFeedbackContentScreening.Screen(
        "Credential observation",
        "A value that must never be retained.",
        "api_key=sk-examplevalue12345678901234567890",
        collective: false);

    Assert.False(collective.Accepted);
    Assert.Contains("absolute_windows_path", string.Join(',', collective.FindingCodes));
    Assert.Contains("network_address", string.Join(',', collective.FindingCodes));
    Assert.True(reserved.Accepted);
    Assert.False(secret.Accepted);
    Assert.Contains("credential_assignment", string.Join(',', secret.FindingCodes));
}

static void TestCollectiveProjection()
{
    var now = DateTimeOffset.Parse("2026-08-07T20:00:00Z");
    var sourceHash = new string('a', 64);
    var rawContent = string.Join(
        '\n',
        "Neural Commander fixed reusable game pathfinding cache invalidation at " +
        "\"D:\\Codex Projects\\Neural Commander\\src\\Navigation.cs\".",
        "A Linux checkout used /home/alice/work/neural-commander/assets/maps/grid.json.",
        "The runtime used /opt/pegasus/apps/cortex/current/config/secrets.json.",
        "A Windows install used \"D:\\Pegasus\\apps\\cortex\\current\\run.exe\".",
        "User settings were at /home/alice/.config/tool/settings.json.",
        "Windows user settings were at \"C:\\Users\\alice\\AppData\\Roaming\\tool\\settings.json\".",
        "System settings were at /etc/pegasus/cortex.json.",
        "Windows system settings were at \"C:\\ProgramData\\tool\\config.json\".",
        "A generic cache was at /mnt/vol01/cache/routes.bin.",
        "A generic Windows cache was at \"E:\\BuildCache\\game\\routes.bin\".",
        "The relative path src/Navigation.cs remains directly reusable.",
        "Traversal /home/alice/work/neural-commander/../private/key is unsafe.",
        $"hostname=pegasus-ai-arm64 reached 10.10.10.15 and verified {sourceHash}.",
        "The reusable fix is to invalidate cached routes after topology changes.");
    var proposal = Samples.Proposal(now) with
    {
        ContractId = LearningFeedbackContractIds.ProposalV1_1,
        ProposalId = $"proposal_{new string('b', 64)}",
        Namespace = "project:neural-commander",
        FeedbackKind = "success",
        Source = Samples.Proposal(now).Source with
        {
            MachineId = "pegasus-ai-arm64",
            CorrelationId = $"plink_{new string('c', 64)}",
            RecordId = $"record_{new string('d', 64)}",
            RevisionId = $"revision_{new string('e', 64)}",
            ContentHash = CortexCanonicalJson.Sha256(System.Text.Encoding.UTF8.GetBytes(rawContent))
        },
        Summary = "Neural Commander reused pathfinding cache invalidation across game maps.",
        Payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["content"] = rawContent,
            ["retentionRecord"] = "true",
            ["title"] = "Neural Commander pathfinding cache correction"
        },
        Taxonomy = "game-development",
        Keywords = ["Neural Commander pathfinding", "cache invalidation", "grid navigation"],
        Applicability =
        [
            new LearningFeedbackApplicabilityV1("neural-commander", "navigation"),
            new LearningFeedbackApplicabilityV1("game-engine", "pathfinding")
        ],
        ProposalHash = new string('f', 64)
    };
    var proposalBytes = CortexCanonicalJson.Serialize(proposal);

    var projectBoundScreen = LearningFeedbackContentScreening.ScreenProjectBoundProposal(
        proposal.Payload["title"],
        proposal.Summary,
        proposal.Payload["content"],
        collectiveProjectionRequested: true,
        proposal.Taxonomy,
        proposal.Keywords,
        proposal.Applicability);
    var first = LearningFeedbackCollectiveProjectionPolicy.Create(proposal);
    var second = LearningFeedbackCollectiveProjectionPolicy.Create(proposal);

    Assert.True(projectBoundScreen.Accepted);
    Assert.Contains("absolute_windows_path", string.Join(',', projectBoundScreen.FindingCodes));
    Assert.Contains("network_address", string.Join(',', projectBoundScreen.FindingCodes));
    Assert.True(first.Accepted);
    Assert.True(second.Accepted);
    Assert.Equal(
        Convert.ToHexString(CortexCanonicalJson.Serialize(first.Value!)),
        Convert.ToHexString(CortexCanonicalJson.Serialize(second.Value!)));
    Assert.Equal(
        Convert.ToHexString(proposalBytes),
        Convert.ToHexString(CortexCanonicalJson.Serialize(proposal)));
    Assert.Contains("pathfinding", first.Value!.Content);
    Assert.Contains("cache invalidation", first.Value.Content);
    Assert.Contains(LearningFeedbackCollectiveProjectionPolicy.ProjectPlaceholder, first.Value.Content);
    Assert.Contains("[PROJECT_ROOT]/src/Navigation.cs", first.Value.Content);
    Assert.Contains("[PROJECT_ROOT]/assets/maps/grid.json", first.Value.Content);
    Assert.Contains(
        "[INSTALL_ROOT]/apps/cortex/current/config/secrets.json",
        first.Value.Content);
    Assert.Contains("[INSTALL_ROOT]/apps/cortex/current/run.exe", first.Value.Content);
    Assert.Contains("[USER_ROOT]/.config/tool/settings.json", first.Value.Content);
    Assert.Contains("[USER_ROOT]/AppData/Roaming/tool/settings.json", first.Value.Content);
    Assert.Contains("[SYSTEM_CONFIG_ROOT]/pegasus/cortex.json", first.Value.Content);
    Assert.Contains("[SYSTEM_CONFIG_ROOT]/tool/config.json", first.Value.Content);
    Assert.Contains("[FILESYSTEM_ROOT]/mnt/vol01/cache/routes.bin", first.Value.Content);
    Assert.Contains("[FILESYSTEM_ROOT]/BuildCache/game/routes.bin", first.Value.Content);
    Assert.Contains("relative path src/Navigation.cs remains", first.Value.Content);
    Assert.Contains(LearningFeedbackCollectiveProjectionPolicy.ProtectedPathPlaceholder, first.Value.Content);
    Assert.Contains(LearningFeedbackCollectiveProjectionPolicy.HostPlaceholder, first.Value.Content);
    Assert.Contains(LearningFeedbackCollectiveProjectionPolicy.NetworkPlaceholder, first.Value.Content);
    Assert.Contains(LearningFeedbackCollectiveProjectionPolicy.HashPlaceholder, first.Value.Content);
    Assert.False(first.Value.Content.Contains("Neural Commander", StringComparison.OrdinalIgnoreCase));
    Assert.False(first.Value.Content.Contains("D:\\Codex Projects", StringComparison.OrdinalIgnoreCase));
    Assert.False(first.Value.Content.Contains("/home/alice", StringComparison.OrdinalIgnoreCase));
    Assert.False(first.Value.Content.Contains("/opt/pegasus", StringComparison.OrdinalIgnoreCase));
    Assert.False(first.Value.Content.Contains("neural-commander", StringComparison.OrdinalIgnoreCase));
    Assert.False(first.Value.Content.Contains("../", StringComparison.Ordinal));
    Assert.False(first.Value.Content.Contains("pegasus-ai-arm64", StringComparison.OrdinalIgnoreCase));
    Assert.False(first.Value.Content.Contains("10.10.10.15", StringComparison.Ordinal));
    Assert.False(first.Value.Content.Contains(sourceHash, StringComparison.Ordinal));
    Assert.Contains("grid navigation", string.Join(',', first.Value.Keywords));
    Assert.Equal("project", first.Value.Applicability[1].ProductId);

    var unsafeProposal = proposal with
    {
        Payload = new Dictionary<string, string>(proposal.Payload, StringComparer.Ordinal)
        {
            ["content"] = "api_key=sk-examplevalue12345678901234567890"
        }
    };
    var unsafeProjection = LearningFeedbackCollectiveProjectionPolicy.Create(unsafeProposal);
    Assert.False(unsafeProjection.Accepted);
    Assert.Contains("credential_assignment", string.Join(',', unsafeProjection.FindingCodes));
}

static void TestProposalSecretSanitization()
{
    const string providerToken = "sk-abcdefghijklmnopqrstuvwxyz123456";
    const string bearerToken = "abcdefghijklmnopqrstuvwxyz.ABCDEFGHIJKLMNOP";
    const string basicCredential = "dXNlcjpiYXNpYy1zZWNyZXQ=";
    const string apiKeyCredential = "apikey-abcdefghijklmnopqrstuvwxyz";
    const string uriUserInfo = "agent:uri-secret";
    const string jwt =
        "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJwZWdhc3VzIn0.abcdefghijklmnopqrstuvwxyz";
    const string privateKey =
        "-----BEGIN PRIVATE KEY-----\nraw-private-key-material\n-----END PRIVATE KEY-----";
    var evidence = new[]
    {
        new LearningFeedbackEvidenceV1(
            "evidence-1",
            "artifact://verification/result?access_token=raw-evidence-token",
            "application/json",
            new string('a', 64),
            "verified")
    };
    var vectors = new[]
    {
        $"Authorization: Basic {basicCredential}",
        "Authorization: Digest username=\"agent\", realm=\"cortex\", nonce=\"abc123\", uri=\"/mcp\", response=\"digest-secret\"",
        $"Authorization: ApiKey {apiKeyCredential}",
        $"https://{uriUserInfo}@server.example/mcp",
        "password=foo,bar fixed authentication retry"
    };
    for (var vectorIndex = 0; vectorIndex < vectors.Length; vectorIndex++)
    {
        var vectorResult = LearningFeedbackProposalInputSanitizer.Sanitize(
            "Authentication correction",
            "A bounded credential vector was supplied.",
            vectors[vectorIndex],
            null,
            null,
            null,
            []);
        if (!vectorResult.Accepted)
            throw new InvalidOperationException(
                $"Sanitization vector {vectorIndex} failed: {string.Join(',', vectorResult.FindingCodes)}");
    }
    var first = LearningFeedbackProposalInputSanitizer.Sanitize(
        $"Provider api_key={providerToken}",
        $"Authorization used Bearer {bearerToken}",
        string.Join(
            '\n',
            $"JWT {jwt}",
            $"Provider {providerToken}",
            $"Authorization: Basic {basicCredential}",
            "Authorization: Digest username=\"agent\", realm=\"cortex\", nonce=\"abc123\", uri=\"/mcp\", response=\"digest-secret\"",
            $"Authorization: ApiKey {apiKeyCredential}",
            $"https://{uriUserInfo}@server.example/mcp",
            "password=foo,bar fixed authentication retry",
            privateKey,
            "The deployment conclusion remains useful."),
        taxonomy: "continuity",
        keywords: ["deployment", "client_secret=raw-keyword-secret"],
        applicability:
        [
            new LearningFeedbackApplicabilityV1("game-engine", "token=raw-component-token")
        ],
        evidence);
    var second = LearningFeedbackProposalInputSanitizer.Sanitize(
        $"Provider api_key={providerToken}",
        $"Authorization used Bearer {bearerToken}",
        string.Join(
            '\n',
            $"JWT {jwt}",
            $"Provider {providerToken}",
            $"Authorization: Basic {basicCredential}",
            "Authorization: Digest username=\"agent\", realm=\"cortex\", nonce=\"abc123\", uri=\"/mcp\", response=\"digest-secret\"",
            $"Authorization: ApiKey {apiKeyCredential}",
            $"https://{uriUserInfo}@server.example/mcp",
            "password=foo,bar fixed authentication retry",
            privateKey,
            "The deployment conclusion remains useful."),
        taxonomy: "continuity",
        keywords: ["deployment", "client_secret=raw-keyword-secret"],
        applicability:
        [
            new LearningFeedbackApplicabilityV1("game-engine", "token=raw-component-token")
        ],
        evidence);

    if (!first.Accepted)
        throw new InvalidOperationException(
            $"First sanitization failed: {string.Join(',', first.FindingCodes)}");
    if (!second.Accepted)
        throw new InvalidOperationException(
            $"Second sanitization failed: {string.Join(',', second.FindingCodes)}");
    Assert.Equal(
        Convert.ToHexString(CortexCanonicalJson.Serialize(first.Value!)),
        Convert.ToHexString(CortexCanonicalJson.Serialize(second.Value!)));
    var retainedText = System.Text.Encoding.UTF8.GetString(
        CortexCanonicalJson.Serialize(first.Value!));
    Assert.Contains(LearningFeedbackProposalInputSanitizer.TokenPlaceholder, retainedText);
    Assert.Contains(LearningFeedbackProposalInputSanitizer.PrivateKeyPlaceholder, retainedText);
    Assert.Contains("token-redacted", retainedText);
    Assert.False(retainedText.Contains(providerToken, StringComparison.Ordinal));
    Assert.False(retainedText.Contains(bearerToken, StringComparison.Ordinal));
    Assert.False(retainedText.Contains(jwt, StringComparison.Ordinal));
    Assert.False(retainedText.Contains(basicCredential, StringComparison.Ordinal));
    Assert.False(retainedText.Contains("digest-secret", StringComparison.Ordinal));
    Assert.False(retainedText.Contains(apiKeyCredential, StringComparison.Ordinal));
    Assert.False(retainedText.Contains(uriUserInfo, StringComparison.Ordinal));
    Assert.False(retainedText.Contains("raw-private-key-material", StringComparison.Ordinal));
    Assert.False(retainedText.Contains("raw-evidence-token", StringComparison.Ordinal));
    Assert.False(retainedText.Contains("raw-keyword-secret", StringComparison.Ordinal));
    Assert.False(retainedText.Contains("raw-component-token", StringComparison.Ordinal));
    Assert.Contains("deployment conclusion remains useful", retainedText);
    Assert.Contains("private_key_redacted", string.Join(',', first.Value!.RedactionCodes));
    Assert.Contains("provider_token_redacted", string.Join(',', first.Value.RedactionCodes));
    Assert.Contains("authorization_token_redacted", string.Join(',', first.Value.RedactionCodes));
    Assert.Contains("digest_authorization_redacted", string.Join(',', first.Value.RedactionCodes));
    Assert.Contains("uri_userinfo_redacted", string.Join(',', first.Value.RedactionCodes));
    Assert.Contains("credential_token_redacted", string.Join(',', first.FindingCodes));
    Assert.Contains("Authorization: Basic [TOKEN]", retainedText);
    Assert.Contains("Authorization: Digest [TOKEN]", retainedText);
    Assert.Contains("Authorization: ApiKey [TOKEN]", retainedText);
    Assert.Contains("https://[SECRET]@server.example/mcp", retainedText);
    Assert.Contains("password=[SECRET] fixed authentication retry", retainedText);

    var intentPreserved = LearningFeedbackProposalInputSanitizer.Sanitize(
        "Authentication correction",
        "A bounded secret assignment is present.",
        "password=foo fixed authentication retry",
        null,
        null,
        null,
        []);
    Assert.True(intentPreserved.Accepted);
    Assert.Equal(
        "password=[SECRET] fixed authentication retry",
        intentPreserved.Value!.Content);

    var ambiguousDigest = LearningFeedbackProposalInputSanitizer.Sanitize(
        "Ambiguous authorization",
        "A malformed Digest boundary must not be partially retained.",
        "Authorization: Digest username=\"agent\" trailing prose",
        null,
        null,
        null,
        []);
    Assert.False(ambiguousDigest.Accepted);
    Assert.Contains(
        "proposal_input_secret_boundary_unsafe",
        string.Join(',', ambiguousDigest.FindingCodes));

    Assert.True(
        LearningFeedbackProposalInputSanitizer.ScreenStableIdentifier(
            "private-lesson-1").Accepted);
    var secretStableKey =
        LearningFeedbackProposalInputSanitizer.ScreenStableIdentifier(
            providerToken);
    Assert.False(secretStableKey.Accepted);
    Assert.Contains(
        "stable_identifier_secret_material",
        string.Join(',', secretStableKey.FindingCodes));

    var projectSpecific = LearningFeedbackProposalInputSanitizer.Sanitize(
        "Neural Commander navigation fix",
        "Project-specific exact recovery detail.",
        @"The file D:\Codex Projects\Neural Commander\src\Navigation.cs changed.",
        taxonomy: "game-development",
        keywords: ["pathfinding"],
        applicability: [new LearningFeedbackApplicabilityV1("neural-commander", "navigation")],
        evidence: []);
    Assert.True(projectSpecific.Accepted);
    Assert.Equal("Neural Commander navigation fix", projectSpecific.Value!.Title);
    Assert.Equal(
        @"The file D:\Codex Projects\Neural Commander\src\Navigation.cs changed.",
        projectSpecific.Value.Content);
    Assert.Equal(0, projectSpecific.Value.RedactionCodes.Count);

    var incompleteKey = LearningFeedbackProposalInputSanitizer.Sanitize(
        "Unsafe key",
        "The key block is incomplete.",
        "-----BEGIN PRIVATE KEY-----\nunbounded-data",
        null,
        null,
        null,
        []);
    Assert.False(incompleteKey.Accepted);
    Assert.Contains("proposal_input_secret_boundary_unsafe", string.Join(',', incompleteKey.FindingCodes));

    foreach (var hardRejected in new[]
             {
                 "private reasoning: hidden deliberation",
                 "raw terminal output: machine stream",
                 "thread://live-agent-state"
             })
    {
        var result = LearningFeedbackProposalInputSanitizer.Sanitize(
            "Unsafe source",
            "Hard-rejected source material.",
            hardRejected,
            null,
            null,
            null,
            []);
        Assert.False(result.Accepted);
    }
}

static void TestProvisionalPublication()
{
    Assert.True(
        LearningFeedbackPublicationPolicy.IsProvisionalAutoPublicationEligible(
            "quality",
            collectiveLibrary: true));
    Assert.True(
        LearningFeedbackPublicationPolicy.IsProvisionalAutoPublicationEligible(
            "success",
            collectiveLibrary: true));
    Assert.False(
        LearningFeedbackPublicationPolicy.IsProvisionalAutoPublicationEligible(
            "correction",
            collectiveLibrary: true));
    Assert.False(
        LearningFeedbackPublicationPolicy.IsProvisionalAutoPublicationEligible(
            "success",
            collectiveLibrary: false));

    var now = DateTimeOffset.Parse("2026-07-28T12:00:00Z");
    var proposal = LearningFeedbackPolicy.AcceptProposal(
        Samples.Proposal(now),
        Samples.Submitter(),
        now,
        CancellationToken.None).Value!;
    var provisional = Samples.Decision(proposal, now.AddSeconds(1)) with
    {
        AuthorityId = LearningFeedbackPublicationPolicy.ProvisionalAuthorityId,
        ReviewerSubjectId = LearningFeedbackPublicationPolicy.ProvisionalReviewerSubjectId,
        Reason = LearningFeedbackPublicationPolicy.ProvisionalReason
    };
    Assert.True(LearningFeedbackPublicationPolicy.IsProvisionalDecision(provisional));
    Assert.False(
        LearningFeedbackPublicationPolicy.IsProvisionalDecision(
            provisional with { Reason = "ordinary review" }));
}

static void TestPublicationChange()
{
    var now = DateTimeOffset.Parse("2026-08-07T19:00:00Z");
    var access = PublicationChangeAccess();
    var request = PublicationChangeRequest(now);

    var first = LearningFeedbackPublicationChangePolicy.Accept(
        request,
        access,
        now,
        CancellationToken.None);
    var second = LearningFeedbackPublicationChangePolicy.Accept(
        request,
        access,
        now,
        CancellationToken.None);
    var changedGeneration = LearningFeedbackPublicationChangePolicy.Accept(
        request with { ExpectedHeadGeneration = 4 },
        access,
        now,
        CancellationToken.None);
    var changedSubject = LearningFeedbackPublicationChangePolicy.Accept(
        request,
        access with { SubjectId = "producer-2" },
        now,
        CancellationToken.None);

    Assert.True(first.Accepted);
    Assert.True(second.Accepted);
    Assert.True(changedGeneration.Accepted);
    Assert.True(changedSubject.Accepted);
    Assert.Equal(first.Value!.ChangeHash, second.Value!.ChangeHash);
    Assert.Equal(first.Value.ChangeId, second.Value.ChangeId);
    Assert.False(first.Value.ChangeHash == changedGeneration.Value!.ChangeHash);
    Assert.False(first.Value.ChangeHash == changedSubject.Value!.ChangeHash);
    Assert.Equal($"pchg_{first.Value.ChangeHash}", first.Value.ChangeId);
    Assert.MatchesHash(first.Value.ChangeHash);
    Assert.True(LearningFeedbackPublicationChangePolicy.HasValidIdentity(first.Value));
    Assert.False(LearningFeedbackPublicationChangePolicy.HasValidIdentity(
        first.Value with { Reason = "Tampered after acceptance." }));
    Assert.Equal(
        first.Value.ChangeHash,
        LearningFeedbackPublicationChangePolicy.ComputeChangeHash(first.Value));
    Assert.Equal(access.TenantId, first.Value.TenantId);
    Assert.Equal(access.Namespace, first.Value.Namespace);
    Assert.Equal(access.AuthorityId, first.Value.AuthorityId);
    Assert.Equal(access.SubjectId, first.Value.SubjectId);
    Assert.Equal(access.ServiceId, first.Value.ServiceId);
    Assert.Equal(access.PolicyRevision, first.Value.PolicyRevision);
    Assert.Equal(TimeSpan.Zero, first.Value.OccurredAtUtc.Offset);
    Assert.MatchesHash(first.SubmittedPayloadHash!);

    using var callerShape = JsonDocument.Parse(
        CortexCanonicalJson.Serialize(request));
    Assert.False(callerShape.RootElement.TryGetProperty("tenantId", out _));
    Assert.False(callerShape.RootElement.TryGetProperty("authorityId", out _));
    Assert.False(callerShape.RootElement.TryGetProperty("subjectId", out _));
    Assert.False(callerShape.RootElement.TryGetProperty("serviceId", out _));
    Assert.False(callerShape.RootElement.TryGetProperty("changeHash", out _));
}

static void TestPublicationChangeValidation()
{
    var now = DateTimeOffset.Parse("2026-08-07T19:00:00Z");
    var access = PublicationChangeAccess();
    var request = PublicationChangeRequest(now);

    Assert.Code(
        "learning_feedback_publication_change_replacement_required",
        LearningFeedbackPublicationChangePolicy.Accept(
            request with { Replacement = null },
            access,
            now,
            CancellationToken.None));
    Assert.Code(
        "learning_feedback_publication_change_replacement_required",
        LearningFeedbackPublicationChangePolicy.Accept(
            request with
            {
                Action = LearningFeedbackPublicationChangeActions.Withdraw,
                Replacement = null
            },
            access,
            now,
            CancellationToken.None));

    var unsafeWithdrawal = LearningFeedbackPublicationChangePolicy.Accept(
        request with
        {
            Action = LearningFeedbackPublicationChangeActions.Withdraw,
            Replacement = null,
            ReasonCode = LearningFeedbackPublicationChangeReasonCodes.UnsafeContent,
            Reason = "The record contains unsafe material and has no safe replacement."
        },
        access,
        now,
        CancellationToken.None);
    var invalidWithdrawal = LearningFeedbackPublicationChangePolicy.Accept(
        request with
        {
            Action = LearningFeedbackPublicationChangeActions.Withdraw,
            Replacement = null,
            ReasonCode = LearningFeedbackPublicationChangeReasonCodes.InvalidContent,
            Reason = "The conclusion is invalid and no corrected record exists yet."
        },
        access,
        now,
        CancellationToken.None);
    Assert.True(unsafeWithdrawal.Accepted);
    Assert.True(invalidWithdrawal.Accepted);

    Assert.Code(
        "learning_feedback_publication_change_target_invalid",
        LearningFeedbackPublicationChangePolicy.Accept(
            request with { TargetRecordId = "record-1" },
            access,
            now,
            CancellationToken.None));
    Assert.Code(
        "learning_feedback_publication_change_replacement_invalid",
        LearningFeedbackPublicationChangePolicy.Accept(
            request with
            {
                Replacement = request.Replacement! with
                {
                    RecordId = request.TargetRecordId
                }
            },
            access,
            now,
            CancellationToken.None));
    Assert.Code(
        "learning_feedback_publication_change_timestamp_invalid",
        LearningFeedbackPublicationChangePolicy.Accept(
            request with
            {
                OccurredAtUtc = now.ToOffset(TimeSpan.FromHours(-7))
            },
            access,
            now,
            CancellationToken.None));
    Assert.Code(
        "learning_feedback_publication_change_hashing_boundary_exceeded",
        LearningFeedbackPublicationChangePolicy.Accept(
            request with
            {
                Reason = new string(
                    'x',
                    LearningFeedbackPublicationChangeLimits.MaximumReasonCharacters + 1)
            },
            access,
            now,
            CancellationToken.None));
    Assert.Code(
        "learning_feedback_publication_change_hashing_boundary_exceeded",
        LearningFeedbackPublicationChangePolicy.Accept(
            request with { Reason = null! },
            access,
            now,
            CancellationToken.None));
    Assert.Code(
        "learning_feedback_publication_change_reason_invalid",
        LearningFeedbackPublicationChangePolicy.Accept(
            request with { Reason = "password=do-not-store-this" },
            access,
            now,
            CancellationToken.None));
    Assert.Code(
        "learning_feedback_publication_change_authority_invalid",
        LearningFeedbackPublicationChangePolicy.Accept(
            request,
            access with { Capability = LearningFeedbackNamespaceCapability.Review },
            now,
            CancellationToken.None));

    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    Assert.Throws<OperationCanceledException>(() =>
        LearningFeedbackPublicationChangePolicy.Accept(
            request,
            access,
            now,
            cancellation.Token));
}

static LearningFeedbackNamespaceAccess PublicationChangeAccess() => new(
    "tenant-1",
    "collective",
    "nexus",
    "producer-1",
    "pegasus-coder",
    17,
    LearningFeedbackNamespaceCapability.Submit);

static LearningFeedbackPublicationChangeRequestV1 PublicationChangeRequest(
    DateTimeOffset now) => new(
    1,
    LearningFeedbackContractIds.PublicationChange,
    LearningFeedbackContractIds.Canonicalization,
    LearningFeedbackPublicationChangeActions.Supersede,
    $"krec_{new string('a', 64)}",
    $"krev_{new string('b', 64)}",
    3,
    new LearningFeedbackPublicationReplacementV1(
        $"krec_{new string('c', 64)}",
        $"krev_{new string('d', 64)}",
        new string('e', 64)),
    LearningFeedbackPublicationChangeReasonCodes.Corrected,
    "A verified replacement supersedes the original record.",
    now);

static void TestAuditAndDelegation()
{
    var now = DateTimeOffset.Parse("2026-07-27T18:00:00Z");
    var proposal = LearningFeedbackPolicy.AcceptProposal(
        Samples.Proposal(now) with { ProposalId = $"proposal_{new string('d', 64)}" },
        Samples.Submitter(),
        now,
        CancellationToken.None).Value!;
    var auditor = Samples.Reviewer() with
    {
        SubjectId = "auditor-1",
        NamespaceGrants =
        [
            new LearningFeedbackNamespaceGrant(
                proposal.Namespace,
                CanSubmit: false,
                CanAudit: true)
        ]
    };
    var submittedAssessment = new LearningFeedbackAuditAssessmentV1(
        1,
        LearningFeedbackContractIds.AuditAssessment,
        LearningFeedbackContractIds.Canonicalization,
        $"assessment_{new string('e', 64)}",
        proposal.ProposalId,
        proposal.ProposalHash,
        proposal.TenantId,
        proposal.Namespace,
        "approve",
        auditor.AuthorityId,
        auditor.SubjectId,
        auditor.PolicyRevision,
        "The bounded evidence supports general retention.",
        $"grant_{new string('f', 64)}",
        0,
        now.AddMinutes(1),
        new string('0', 64));
    var assessment = LearningFeedbackAuditPolicy.AcceptAssessment(
        submittedAssessment,
        proposal,
        auditor,
        now.AddMinutes(1),
        CancellationToken.None);
    var selfAuditor = Samples.Submitter() with
    {
        NamespaceGrants =
        [
            new LearningFeedbackNamespaceGrant(
                proposal.Namespace,
                CanSubmit: false,
                CanAudit: true)
        ]
    };
    var selfAssessment = LearningFeedbackAuditPolicy.AcceptAssessment(
        submittedAssessment with
        {
            AuthorityId = selfAuditor.AuthorityId,
            AuditorSubjectId = selfAuditor.SubjectId
        },
        proposal,
        selfAuditor,
        now.AddMinutes(1),
        CancellationToken.None);

    var issuer = Samples.Submitter() with
    {
        SubjectId = "review-grant-issuer",
        NamespaceGrants =
        [
            new LearningFeedbackNamespaceGrant(
                "collective",
                CanSubmit: false,
                CanDelegateReview: true)
        ]
    };
    var submittedGrant = new LearningFeedbackReviewGrantV1(
        1,
        LearningFeedbackContractIds.ReviewGrant,
        LearningFeedbackContractIds.Canonicalization,
        $"grant_{new string('f', 64)}",
        issuer.TenantId,
        "collective",
        issuer.AuthorityId,
        issuer.SubjectId,
        auditor.AuthorityId,
        auditor.SubjectId,
        issuer.PolicyRevision,
        ["success", "quality"],
        CollectiveOnly: true,
        MaximumUses: 25,
        ExpiresAt: now.AddDays(7),
        CreatedAt: now,
        GrantHash: new string('0', 64));
    var grant = LearningFeedbackAuditPolicy.AcceptGrant(
        submittedGrant,
        issuer,
        now,
        CancellationToken.None);
    var unsafeGrant = LearningFeedbackAuditPolicy.AcceptGrant(
        submittedGrant with { FeedbackKinds = ["correction"] },
        issuer,
        now,
        CancellationToken.None);

    Assert.True(assessment.Accepted);
    Assert.Equal(0.60, assessment.Value!.RetentionConfidence);
    Assert.MatchesHash(assessment.Value.AssessmentHash);
    Assert.Code("learning_feedback_audit_independence_required", selfAssessment);
    Assert.True(grant.Accepted);
    Assert.Equal("quality,success", string.Join(',', grant.Value!.FeedbackKinds));
    Assert.MatchesHash(grant.Value.GrantHash);
    Assert.Code("learning_feedback_review_grant_invalid", unsafeGrant);
}

static void TestMalformedAndOversized()
{
    var now = DateTimeOffset.Parse("2026-07-14T05:30:00Z");
    var missingShape = Samples.Proposal(now) with { Payload = null! };
    var tooManyFields = Samples.Proposal(now) with
    {
        Payload = Enumerable.Range(0, LearningFeedbackContractLimits.MaximumPayloadFields + 1)
            .ToDictionary(index => $"field-{index}", index => index.ToString())
    };
    var hugeSummary = Samples.Proposal(now) with
    {
        Summary = new string('x', LearningFeedbackContractLimits.MaximumSummaryCharacters + 1)
    };
    var hugeOptionalId = Samples.Proposal(now) with
    {
        Source = Samples.Proposal(now).Source with
        {
            MachineId = new string('m', LearningFeedbackContractLimits.MaximumIdentifierCharacters + 1)
        }
    };

    Assert.Code("learning_feedback_shape_invalid", LearningFeedbackPolicy.AcceptProposal(missingShape, Samples.Submitter(), now, CancellationToken.None));
    Assert.Code("learning_feedback_snapshot_limit", LearningFeedbackPolicy.AcceptProposal(tooManyFields, Samples.Submitter(), now, CancellationToken.None));
    Assert.Code("learning_feedback_hashing_boundary_exceeded", LearningFeedbackPolicy.AcceptProposal(hugeSummary, Samples.Submitter(), now, CancellationToken.None));
    var oversizedOptional = LearningFeedbackPolicy.AcceptProposal(hugeOptionalId, Samples.Submitter(), now, CancellationToken.None);
    Assert.Code("learning_feedback_hashing_boundary_exceeded", oversizedOptional);
    Assert.True(oversizedOptional.SubmittedPayloadHash is null);
}

static void TestCancellation()
{
    using var source = new CancellationTokenSource();
    source.Cancel();
    Assert.Throws<OperationCanceledException>(() => LearningFeedbackPolicy.AcceptProposal(
        Samples.Proposal(DateTimeOffset.UtcNow),
        Samples.Submitter(),
        DateTimeOffset.UtcNow,
        source.Token));
}

static void TestSchemas()
{
    var root = FindProjectRoot();
    using var proposal = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", "learning-feedback-proposal.v1.schema.json")));
    using var proposalV11 = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", "learning-feedback-proposal.v1.1.schema.json")));
    using var decision = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", "learning-feedback-review-decision.v1.schema.json")));
    using var publicationChange = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", "learning-feedback-publication-change.v1.schema.json")));
    using var assessment = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", "learning-feedback-audit-assessment.v1.schema.json")));
    using var grant = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schemas", "learning-feedback-review-grant.v1.schema.json")));

    Assert.False(proposal.RootElement.GetProperty("additionalProperties").GetBoolean());
    Assert.Equal(32, proposal.RootElement.GetProperty("properties").GetProperty("payload").GetProperty("maxProperties").GetInt32());
    Assert.Equal(4, proposal.RootElement.GetProperty("properties").GetProperty("intendedUses").GetProperty("maxItems").GetInt32());
    Assert.Contains("distillation", proposal.RootElement.GetRawText());
    Assert.Contains("untrusted_data", proposal.RootElement.GetRawText());
    Assert.Contains("pending", proposal.RootElement.GetRawText());
    Assert.False(proposalV11.RootElement.GetProperty("additionalProperties").GetBoolean());
    Assert.Equal(
        LearningFeedbackContractLimits.MaximumProposalKeywords,
        proposalV11.RootElement.GetProperty("properties").GetProperty("keywords")
            .GetProperty("maxItems").GetInt32());
    Assert.Equal(
        LearningFeedbackContractLimits.MaximumProposalApplicabilityItems,
        proposalV11.RootElement.GetProperty("properties").GetProperty("applicability")
            .GetProperty("maxItems").GetInt32());
    Assert.Contains(LearningFeedbackContractIds.ProposalV1_1, proposalV11.RootElement.GetRawText());
    Assert.False(decision.RootElement.GetProperty("additionalProperties").GetBoolean());
    Assert.Contains("approved", decision.RootElement.GetRawText());
    Assert.Contains("rejected", decision.RootElement.GetRawText());
    Assert.False(publicationChange.RootElement.GetProperty("additionalProperties").GetBoolean());
    Assert.Contains(LearningFeedbackContractIds.PublicationChange, publicationChange.RootElement.GetRawText());
    Assert.Contains("supersede", publicationChange.RootElement.GetRawText());
    Assert.Contains("withdraw", publicationChange.RootElement.GetRawText());
    Assert.Equal(
        LearningFeedbackPublicationChangeLimits.MaximumReasonCharacters,
        publicationChange.RootElement.GetProperty("properties").GetProperty("reason")
            .GetProperty("maxLength").GetInt32());
    Assert.False(assessment.RootElement.GetProperty("additionalProperties").GetBoolean());
    Assert.Contains("human_review", assessment.RootElement.GetRawText());
    Assert.Contains("0.6", assessment.RootElement.GetRawText());
    Assert.False(grant.RootElement.GetProperty("additionalProperties").GetBoolean());
    Assert.Equal(1000, grant.RootElement.GetProperty("properties").GetProperty("maximumUses").GetProperty("maximum").GetInt32());
    Assert.Contains("collective", grant.RootElement.GetRawText());

    var now = DateTimeOffset.Parse("2026-07-14T05:30:00Z");
    Assert.Code(
        "learning_feedback_payload_invalid",
        LearningFeedbackPolicy.AcceptProposal(
            Samples.Proposal(now) with { Payload = new Dictionary<string, string>() },
            Samples.Submitter(), now, CancellationToken.None));
    Assert.Code(
        "learning_feedback_identity_invalid",
        LearningFeedbackPolicy.AcceptProposal(
            Samples.Proposal(now) with { ProposalId = "-proposal" },
            Samples.Submitter(), now, CancellationToken.None));
    Assert.Code(
        "learning_feedback_authority_invalid",
        LearningFeedbackPolicy.AcceptProposal(
            Samples.Proposal(now),
            Samples.Submitter() with { PolicyRevision = 0 }, now, CancellationToken.None));
}

static string FindProjectRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PegasusCortex.slnx")))
        directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the Pegasus Cortex project root.");
}

static class Samples
{
    private static readonly string ScreeningHash = new('b', 64);

    public static LearningFeedbackAuthorityContext Submitter() => new(
        "tenant-1",
        "nexus",
        "submitter-1",
        "pegasus-coder",
        7,
        [new LearningFeedbackNamespaceGrant("project:project-1", CanSubmit: true)],
        new HashSet<string>(StringComparer.Ordinal) { "cortex-screening" },
        new Dictionary<string, string>(StringComparer.Ordinal) { ["screening-1"] = ScreeningHash });

    public static LearningFeedbackAuthorityContext Reviewer() => Submitter() with
    {
        SubjectId = "reviewer-1",
        NamespaceGrants = [new LearningFeedbackNamespaceGrant("project:project-1", CanSubmit: false, CanReview: true)]
    };

    public static LearningFeedbackScreeningV1 Screening(DateTimeOffset now) => new(
        "sanitized", "passed", "passed", "passed", "passed", "training_and_evaluation",
        7, "cortex-screening", "screening-1", ScreeningHash, now.ToOffset(TimeSpan.FromHours(-7)));

    public static LearningFeedbackProposalV1 Proposal(DateTimeOffset now) => new(
        1,
        LearningFeedbackContractIds.Proposal,
        LearningFeedbackContractIds.Canonicalization,
        "proposal-1",
        "tenant-1",
        "project:project-1",
        "correction",
        new LearningFeedbackSourceV1(
            "nexus", "submitter-1", "pegasus-coder", "coder-agent-1", "instance-1", "machine-1",
            "operation-1", "task-1", "correlation-1", "record-1", "revision-1", new string('a', 64), now),
        "A sanitized observation with an externally verifiable correction.",
        new Dictionary<string, string> { ["observation"] = "The bounded output differed from the expected contract." },
        ["retrieval", "evaluation"],
        [new LearningFeedbackEvidenceV1("evidence-1", "artifact://verification/1", "application/json", new string('c', 64), "verified")],
        Screening(now),
        "pending",
        LearningFeedbackContractIds.ContentTreatment,
        false,
        now,
        new string('0', 64));

    public static LearningFeedbackReviewDecisionV1 Decision(LearningFeedbackProposalV1 proposal, DateTimeOffset now) => new(
        1,
        LearningFeedbackContractIds.ReviewDecision,
        LearningFeedbackContractIds.Canonicalization,
        "review-1",
        proposal.ProposalId,
        proposal.ProposalHash,
        proposal.TenantId,
        proposal.Namespace,
        "approved",
        "nexus",
        "reviewer-1",
        7,
        "The screened correction is supported by verified evidence.",
        now,
        new string('0', 64));
}

static class Assert
{
    public static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    public static void False(bool value)
    {
        if (value) throw new InvalidOperationException("Expected false.");
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected text to contain '{expected}'.");
    }

    public static void MatchesHash(string value)
    {
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidOperationException($"'{value}' is not a lowercase SHA-256 value.");
    }

    public static void Code<T>(string expected, CortexContractResult<T> result) where T : class
    {
        False(result.Accepted);
        Equal(expected, result.Diagnostics.Single().Code);
    }

    public static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
