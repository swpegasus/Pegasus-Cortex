using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Knowledge;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Storage;

internal static class LocalProposalProcessTests
{
    public static async Task ProposalMetadataV11Async()
    {
        await using var context = await LocalTestContext.CreateAsync();
        var occurredAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToUniversalTime();
        const string title = "Bound recovery before activation";
        const string summary = "Use tested rollback checkpoints for bounded recovery.";
        const string content =
            "Verify the rollback checkpoint and current evidence before activation.";
        var metadataArguments = new JsonObject
        {
            ["taxonomy"] = "testing",
            ["keywords"] = new JsonArray("rollback", "bounded recovery"),
            ["applicability"] = new JsonArray(
                new JsonObject
                {
                    ["productId"] = "pegasus-cortex",
                    ["componentId"] = "persistence"
                },
                new JsonObject
                {
                    ["productId"] = "pegasus-coder",
                    ["componentId"] = "coordination"
                })
        };
        var run = await LocalProcessHarness.RunAsync(
            StdioArguments(context),
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                ProposalCall(
                    2,
                    LocalTestContext.PrimaryProjectLinkId,
                    "proposal-metadata-v11",
                    title,
                    summary,
                    content,
                    occurredAt,
                    metadataArguments: metadataArguments),
                ListProposalCall(
                    3,
                    LocalTestContext.PrimaryProjectLinkId,
                    afterProposalId: null,
                    limit: 1,
                    includeContent: false)
            ]).ConfigureAwait(false);
        LocalAssert.Equal(0, run.ExitCode, $"The metadata proposal process failed: {run.StandardError}");

        var accepted = Structured(run.Responses, 2, expectedError: false);
        AssertPrivatePending(accepted);
        var canonicalMetadata = new CortexProposalMetadataV1(
            "testing",
            Array.AsReadOnly(new[] { "bounded recovery", "rollback" }),
            Array.AsReadOnly(new[]
            {
                new LearningFeedbackApplicabilityV1("pegasus-coder", "coordination"),
                new LearningFeedbackApplicabilityV1("pegasus-cortex", "persistence")
            }));
        var contentHash = CortexCanonicalJson.Sha256(
            System.Text.Encoding.UTF8.GetBytes(content));
        var expectedIdentityHash = CortexCanonicalJson.Sha256(
            CortexCanonicalJson.Serialize(new
            {
                contract = LearningFeedbackContractIds.ProposalV1_1,
                link = LocalTestContext.PrimaryProjectLinkId,
                idempotencyKey = "proposal-metadata-v11",
                feedbackKind = "quality",
                title,
                summary,
                contentHash,
                metadata = canonicalMetadata,
                evidence = Array.Empty<LearningFeedbackEvidenceV1>(),
                occurredAt
            }));
        var proposalId = accepted["proposalId"]!.GetValue<string>();
        var proposalHash = accepted["proposalHash"]!.GetValue<string>();
        LocalAssert.Equal(
            $"proposal_{expectedIdentityHash}",
            proposalId,
            "Proposal metadata did not participate in the canonical idempotency identity.");

        var listed = Structured(run.Responses, 3, expectedError: false);
        var item = listed["items"]!.AsArray().Single()!.AsObject();
        LocalAssert.Equal(
            "pending",
            item["reviewState"]!.GetValue<string>(),
            "Proposal metadata changed the pending review state.");
        LocalAssert.False(item["searchable"]!.GetValue<bool>(),
            "Pending proposal metadata became searchable before publication.");
        LocalAssert.Equal(
            "testing",
            item["taxonomy"]!.GetValue<string>(),
            "Proposal list projection changed the normalized taxonomy.");
        LocalAssert.True(
            item["keywords"]!.AsArray().Select(node => node!.GetValue<string>())
                .SequenceEqual(["bounded recovery", "rollback"], StringComparer.Ordinal),
            "Proposal list projection changed canonical keywords.");
        LocalAssert.Equal(
            2,
            item["applicability"]!.AsArray().Count,
            "Proposal list projection changed applicability coordinates.");

        await AssertExactStoredProposalAsync(
            context,
            proposalId,
            proposalHash,
            title,
            summary,
            content,
            expectedEvidenceCount: 0,
            expectedRetainedDocuments: 1,
            expectedContractId: LearningFeedbackContractIds.ProposalV1_1,
            expectedTaxonomy: "testing",
            expectedKeywords: ["bounded recovery", "rollback"],
            expectedApplicability:
            [
                new LearningFeedbackApplicabilityV1("pegasus-coder", "coordination"),
                new LearningFeedbackApplicabilityV1("pegasus-cortex", "persistence")
            ]).ConfigureAwait(false);
    }

    public static async Task PrivatePendingProposalRetentionAsync()
    {
        await using var context = await LocalTestContext.CreateAsync();
        var occurredAt = DateTimeOffset.UtcNow
            .AddMinutes(-1)
            .ToUniversalTime();
        const string IdempotencyKey = "private-lesson-1";
        const string Title = "Bound coordinator context";
        const string Summary =
            "Use bounded continuation packets for long-running coordination.";
        const string Content =
            "Resume from a verified closeout and exact evidence references.";

        var firstRun = await LocalProcessHarness.RunAsync(
            StdioArguments(context),
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                LocalMcpProtocol.ToolsList(2),
                ProposalCall(
                    3,
                    LocalTestContext.PrimaryProjectLinkId,
                    IdempotencyKey,
                    Title,
                    Summary,
                    Content,
                    occurredAt),
                ProposalCall(
                    4,
                    LocalTestContext.PrimaryProjectLinkId,
                    IdempotencyKey,
                    Title,
                    Summary,
                    Content,
                    occurredAt),
                ProposalCall(
                    5,
                    LocalTestContext.PrimaryProjectLinkId,
                    IdempotencyKey,
                    Title,
                    Summary,
                    "Different content must conflict for the same key.",
                    occurredAt),
                ProposalCall(
                    6,
                    LocalTestContext.PrimaryProjectLinkId,
                    "private-lesson-screening",
                    Title,
                    Summary,
                    "Authorization: Basic dXNlcjpiYXNpYy1zZWNyZXQ= fixed authentication retry",
                    occurredAt),
                ProposalCall(
                    7,
                    LocalTestContext.ReadOnlyProjectLinkId,
                    "private-lesson-read-only",
                    Title,
                    Summary,
                    Content,
                    occurredAt),
                ProposalCall(
                    8,
                    LocalTestContext.PrimaryProjectLinkId,
                    "private-lesson-machine-path",
                    Title,
                    Summary,
                    @"The source was C:\Users\example\private-project.",
                    occurredAt),
                ProposalCall(
                    9,
                    LocalTestContext.PrimaryProjectLinkId,
                    "private-lesson-evidence",
                    Title,
                    Summary,
                    Content,
                    occurredAt,
                    EvidenceReferences()),
                ProposalCall(
                    10,
                    LocalTestContext.PrimaryProjectLinkId,
                    "private-lesson-bad-hash",
                    Title,
                    Summary,
                    Content,
                    occurredAt,
                    EvidenceReferences(
                        sha256:
                            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")),
                ProposalCall(
                    11,
                    LocalTestContext.PrimaryProjectLinkId,
                    "private-lesson-too-much-evidence",
                    Title,
                    Summary,
                    Content,
                    occurredAt,
                    OversizedEvidenceReferences()),
                ProposalCall(
                    12,
                    LocalTestContext.PrimaryProjectLinkId,
                    "sk-abcdefghijklmnopqrstuvwxyz123456",
                    Title,
                    Summary,
                    Content,
                    occurredAt)
            ]).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            firstRun.ExitCode,
            $"The private-proposal process failed: {firstRun.StandardError}");

        var tools = LocalMcpProtocol.ResponseById(firstRun.Responses, 2)
            ["result"]!.AsObject()["tools"]!.AsArray()
            .Select(item => item!.AsObject()["name"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        LocalAssert.True(
            tools.Contains("cortex_submit_proposal"),
            "A write-allowlisted local registry did not advertise proposal submission.");
        LocalAssert.True(
            tools.Contains("cortex_list_proposals"),
            "A read-allowlisted local registry did not advertise private proposal retrieval.");
        foreach (var prohibitedTool in new[]
                 {
                     "cortex_review_proposal",
                     "cortex_submit_audit",
                     "cortex_manage_review_grant",
                     "cortex_correlate_project"
                 })
        {
            LocalAssert.False(
                tools.Contains(prohibitedTool),
                $"The standalone private-proposal profile advertised '{prohibitedTool}'.");
        }
        LocalAssert.False(
            tools.Any(name =>
                name.Contains("publish", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("generalize", StringComparison.OrdinalIgnoreCase)),
            "The standalone private-proposal profile advertised publication or Generalize.");

        var accepted = Structured(firstRun.Responses, 3, expectedError: false);
        LocalAssert.Equal(
            "accepted",
            accepted["disposition"]!.GetValue<string>(),
            "The first private proposal was not accepted.");
        AssertPrivatePending(accepted);
        var proposalId = accepted["proposalId"]!.GetValue<string>();
        var proposalHash = accepted["proposalHash"]!.GetValue<string>();
        var legacyIdentityHash = CortexCanonicalJson.Sha256(
            CortexCanonicalJson.Serialize(new
            {
                link = LocalTestContext.PrimaryProjectLinkId,
                idempotencyKey = IdempotencyKey,
                feedbackKind = "quality",
                title = Title,
                summary = Summary,
                contentHash = CortexCanonicalJson.Sha256(
                    System.Text.Encoding.UTF8.GetBytes(Content)),
                evidence = Array.Empty<LearningFeedbackEvidenceV1>(),
                occurredAt
            }));
        LocalAssert.Equal(
            $"proposal_{legacyIdentityHash}",
            proposalId,
            "A metadata-omitted v1 request changed its legacy proposal identity.");

        var replay = Structured(firstRun.Responses, 4, expectedError: false);
        LocalAssert.Equal(
            "idempotent",
            replay["disposition"]!.GetValue<string>(),
            "An exact same-process proposal replay was not idempotent.");
        LocalAssert.Equal(
            proposalId,
            replay["proposalId"]!.GetValue<string>(),
            "An exact replay changed the proposal identity.");
        LocalAssert.Equal(
            proposalHash,
            replay["proposalHash"]!.GetValue<string>(),
            "An exact replay changed the proposal hash.");
        AssertPrivatePending(replay);

        LocalAssert.Equal(
            "learning_feedback_proposal_id_conflict",
            LocalMcpProtocol.DiagnosticCode(
                Structured(firstRun.Responses, 5, expectedError: true)),
            "A changed proposal reused an idempotency key without conflict.");
        var redacted = Structured(firstRun.Responses, 6, expectedError: false);
        LocalAssert.Equal(
            "accepted",
            redacted["disposition"]!.GetValue<string>(),
            "A detectable credential was not redacted before private retention.");
        AssertPrivatePending(redacted);
        var redactedProposalId = redacted["proposalId"]!.GetValue<string>();
        var redactedProposalHash = redacted["proposalHash"]!.GetValue<string>();
        LocalAssert.Equal(
            "cortex_project_link_not_available",
            LocalMcpProtocol.DiagnosticCode(
                Structured(firstRun.Responses, 7, expectedError: true)),
            "A read-only registry entry acquired proposal-write authority.");
        var machineSpecific = Structured(firstRun.Responses, 8, expectedError: false);
        LocalAssert.Equal(
            "accepted",
            machineSpecific["disposition"]!.GetValue<string>(),
            "A bounded project path was rejected from exact project-bound retention.");
        AssertPrivatePending(machineSpecific);
        var machineSpecificProposalId =
            machineSpecific["proposalId"]!.GetValue<string>();
        var machineSpecificProposalHash =
            machineSpecific["proposalHash"]!.GetValue<string>();
        var evidenced = Structured(firstRun.Responses, 9, expectedError: false);
        LocalAssert.Equal(
            "accepted",
            evidenced["disposition"]!.GetValue<string>(),
            "A bounded verified evidence reference was not retained.");
        var evidencedProposalId =
            evidenced["proposalId"]!.GetValue<string>();
        var evidencedProposalHash =
            evidenced["proposalHash"]!.GetValue<string>();
        LocalAssert.Equal(
            "cortex_proposal_arguments_invalid",
            LocalMcpProtocol.DiagnosticCode(
                Structured(firstRun.Responses, 10, expectedError: true)),
            "A non-lowercase evidence hash passed MCP admission.");
        LocalAssert.Equal(
            "cortex_proposal_arguments_invalid",
            LocalMcpProtocol.DiagnosticCode(
                Structured(firstRun.Responses, 11, expectedError: true)),
            "An oversized evidence-reference array passed MCP admission.");
        LocalAssert.Equal(
            "cortex_retention_screening_rejected",
            LocalMcpProtocol.DiagnosticCode(
                Structured(firstRun.Responses, 12, expectedError: true)),
            "A token-bearing idempotency key reached proposal identity or persistence.");

        await AssertExactStoredProposalAsync(
            context,
            proposalId,
            proposalHash,
            Title,
            Summary,
            Content,
            expectedEvidenceCount: 0,
            expectedRetainedDocuments: 4).ConfigureAwait(false);
        await AssertExactStoredProposalAsync(
            context,
            redactedProposalId,
            redactedProposalHash,
            Title,
            Summary,
            "Authorization: Basic [TOKEN] fixed authentication retry",
            expectedEvidenceCount: 0,
            expectedRetainedDocuments: 4).ConfigureAwait(false);
        await AssertExactStoredProposalAsync(
            context,
            machineSpecificProposalId,
            machineSpecificProposalHash,
            Title,
            Summary,
            @"The source was C:\Users\example\private-project.",
            expectedEvidenceCount: 0,
            expectedRetainedDocuments: 4).ConfigureAwait(false);
        await AssertExactStoredProposalAsync(
            context,
            evidencedProposalId,
            evidencedProposalHash,
            Title,
            Summary,
            Content,
            expectedEvidenceCount: 1,
            expectedRetainedDocuments: 4).ConfigureAwait(false);

        // A fresh process and SQLite connection must return the same immutable
        // pending record without creating a review or publication decision.
        var restart = await LocalProcessHarness.RunAsync(
            StdioArguments(context),
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                ProposalCall(
                    2,
                    LocalTestContext.PrimaryProjectLinkId,
                    IdempotencyKey,
                    Title,
                    Summary,
                    Content,
                    occurredAt),
                ListProposalCall(
                    3,
                    LocalTestContext.PrimaryProjectLinkId,
                    afterProposalId: null,
                    limit: 1,
                    includeContent: true)
            ]).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            restart.ExitCode,
            $"The proposal restart process failed: {restart.StandardError}");
        var restartedReplay =
            Structured(restart.Responses, 2, expectedError: false);
        LocalAssert.Equal(
            "idempotent",
            restartedReplay["disposition"]!.GetValue<string>(),
            "A restart did not preserve exact proposal idempotency.");
        LocalAssert.Equal(
            proposalId,
            restartedReplay["proposalId"]!.GetValue<string>(),
            "A restart changed the retained proposal identity.");
        LocalAssert.Equal(
            proposalHash,
            restartedReplay["proposalHash"]!.GetValue<string>(),
            "A restart changed the retained proposal hash.");
        AssertPrivatePending(restartedReplay);
        var firstPage = Structured(
            restart.Responses,
            3,
            expectedError: false);
        LocalAssert.Equal(
            1,
            firstPage["count"]!.GetValue<int>(),
            "The private restart query did not honor its page size.");
        var firstPageItem = firstPage["items"]!.AsArray()[0]!.AsObject();
        var firstPageId =
            firstPageItem["proposalId"]!.GetValue<string>();
        LocalAssert.True(
            firstPageItem["proposalHash"] is not null,
            "A private list page omitted proposal integrity identity.");
        var nextAfter = firstPage["nextAfterProposalId"]?
            .GetValue<string>();
        LocalAssert.True(
            nextAfter is not null,
            "A four-proposal private result did not return a restart-safe cursor.");

        var secondPageRun = await LocalProcessHarness.RunAsync(
            StdioArguments(context),
            [
                LocalMcpProtocol.Initialize(),
                LocalMcpProtocol.Initialized(),
                ListProposalCall(
                    2,
                    LocalTestContext.PrimaryProjectLinkId,
                    nextAfter,
                    limit: 3,
                    includeContent: true),
                ListProposalCall(
                    3,
                    LocalTestContext.ReadOnlyProjectLinkId,
                    afterProposalId: null,
                    limit: 3,
                    includeContent: true),
                ListProposalCall(
                    4,
                    $"plink_{new string('f', 64)}",
                    afterProposalId: null,
                    limit: 10,
                    includeContent: false)
            ]).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            secondPageRun.ExitCode,
            $"The proposal pagination process failed: {secondPageRun.StandardError}");
        var secondPage = Structured(
            secondPageRun.Responses,
            2,
            expectedError: false);
        LocalAssert.Equal(
            3,
            secondPage["count"]!.GetValue<int>(),
            "The private second page did not contain the remaining three proposals.");
        var secondPageItems = secondPage["items"]!.AsArray()
            .Select(item => item!.AsObject())
            .ToArray();
        LocalAssert.True(
            secondPageItems.All(item => item["proposalHash"] is not null),
            "A private second page omitted proposal integrity identity.");
        var allItems = new[] { firstPageItem }
            .Concat(secondPageItems)
            .ToArray();
        LocalAssert.True(
            allItems
                .Select(item => item["proposalId"]!.GetValue<string>())
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(
                [
                    proposalId,
                    redactedProposalId,
                    machineSpecificProposalId,
                    evidencedProposalId
                ]),
            "Restart pagination did not return the exact project-scoped proposal set.");
        var evidencedItem = allItems.Single(item => string.Equals(
            item["proposalId"]!.GetValue<string>(),
            evidencedProposalId,
            StringComparison.Ordinal));
        LocalAssert.Equal(
            1,
            evidencedItem["evidence"]!.AsArray().Count,
            "Hydrated private retrieval omitted retained evidence coordinates.");
        LocalAssert.Equal(
            "caller-reported-reference-metadata",
            evidencedItem["evidence"]!.AsArray()[0]!.AsObject()
                ["verificationSemantics"]!.GetValue<string>(),
            "Private retrieval overstated caller-reported evidence verification.");
        LocalAssert.Equal(
            0,
            Structured(secondPageRun.Responses, 3, expectedError: false)
                ["count"]!.GetValue<int>(),
            "Private proposal retrieval crossed into another registered project.");
        LocalAssert.Equal(
            "cortex_project_link_not_available",
            LocalMcpProtocol.DiagnosticCode(
                Structured(secondPageRun.Responses, 4, expectedError: true)),
            "An unregistered project link acquired private proposal-read authority.");
        await AssertExactStoredProposalAsync(
            context,
            proposalId,
            proposalHash,
            Title,
            Summary,
            Content,
            expectedEvidenceCount: 0,
            expectedRetainedDocuments: 4).ConfigureAwait(false);
    }

    private static async Task AssertExactStoredProposalAsync(
        LocalTestContext context,
        string proposalId,
        string proposalHash,
        string title,
        string summary,
        string content,
        int expectedEvidenceCount,
        long expectedRetainedDocuments,
        string expectedContractId = LearningFeedbackContractIds.Proposal,
        string? expectedTaxonomy = null,
        IReadOnlyList<string>? expectedKeywords = null,
        IReadOnlyList<LearningFeedbackApplicabilityV1>? expectedApplicability = null)
    {
        await using var store = new SqliteCortexRetentionStore(
            new SqliteCortexStoreOptions(context.DatabasePath));
        var retained = await store.FindRevisionAsync(
            LocalTestContext.PrimaryProjectId,
            CortexRetainedDocumentKinds.LearningFeedbackProposal,
            proposalId,
            revision: 1,
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.True(
            retained is not null,
            "The accepted private proposal was not readable from SQLite.");
        LocalAssert.True(
            retained!.CanonicalUtf8.Length <=
                CortexAgentRetentionLimits.MaximumCanonicalDocumentBytes,
            "The retained private proposal exceeded the generic byte boundary.");
        LocalAssert.Equal(
            expectedContractId,
            retained.ContractId,
            "The retained private proposal changed contract identity.");
        LocalAssert.Equal(
            CortexCanonicalJson.Sha256(retained.CanonicalUtf8),
            retained.ContentHash,
            "The retained private proposal failed exact content hashing.");

        var proposal =
            CortexCanonicalJson.Deserialize<LearningFeedbackProposalV1>(
                retained.CanonicalUtf8);
        LocalAssert.True(
            CortexCanonicalJson.Serialize(proposal)
                .AsSpan().SequenceEqual(retained.CanonicalUtf8),
            "The retained proposal is not exact canonical LearningFeedbackProposalV1 bytes.");
        LocalAssert.Equal(
            proposalId,
            proposal.ProposalId,
            "SQLite readback changed the proposal identity.");
        LocalAssert.Equal(
            proposalHash,
            proposal.ProposalHash,
            "SQLite readback changed the proposal hash.");
        LocalAssert.Equal(
            "pending",
            proposal.ReviewState,
            "The standalone proposal was promoted out of pending state.");
        LocalAssert.False(
            proposal.Executable,
            "The standalone proposal became executable authority.");
        LocalAssert.Equal(
            LocalTestContext.PrimaryNamespace,
            proposal.Namespace,
            "The proposal namespace did not come from the protected registry.");
        LocalAssert.Equal(
            LocalTestContext.TenantId,
            proposal.TenantId,
            "The proposal tenant did not come from the protected registry.");
        LocalAssert.Equal(
            LocalTestContext.PrimaryProjectLinkId,
            proposal.Source.CorrelationId,
            "The proposal project link did not come from the protected registry.");
        LocalAssert.True(
            proposal.Source.AgentId is null,
            "The generic local transport asserted an unauthenticated client-agent identity.");
        LocalAssert.True(
            proposal.Source.RecordId.StartsWith(
                "inline-content_",
                StringComparison.Ordinal) &&
            proposal.Source.RevisionId.StartsWith(
                "inline-content-revision_",
                StringComparison.Ordinal),
            "Inline proposal content was mislabeled as an external or retained source record.");
        LocalAssert.Equal(
            "inline-submitted-content",
            proposal.Payload["sourceDisposition"],
            "The proposal did not disclose its inline source disposition.");
        LocalAssert.Equal(
            expectedEvidenceCount,
            proposal.Evidence.Count,
            "Exact readback changed the proposal evidence set.");
        if (expectedEvidenceCount != 0)
        {
            LocalAssert.Equal(
                "verified",
                proposal.Evidence[0].Verification,
                "The caller-reported evidence verification marker changed.");
            LocalAssert.Equal(
                new string('0', 64),
                proposal.Evidence[0].Sha256,
                "The exact caller-supplied evidence hash changed.");
        }
        LocalAssert.Equal(
            title,
            proposal.Payload["title"],
            "Exact readback changed the sanitized title.");
        LocalAssert.Equal(
            content,
            proposal.Payload["content"],
            "Exact readback changed the sanitized content.");
        LocalAssert.Equal(
            summary,
            proposal.Summary,
            "Exact readback changed the sanitized summary.");
        LocalAssert.Equal(
            expectedTaxonomy ?? string.Empty,
            proposal.Taxonomy ?? string.Empty,
            "Exact readback changed proposal taxonomy.");
        LocalAssert.True(
            (proposal.Keywords ?? []).SequenceEqual(
                expectedKeywords ?? [],
                StringComparer.Ordinal),
            "Exact readback changed proposal keywords.");
        LocalAssert.True(
            (proposal.Applicability ?? []).SequenceEqual(
                expectedApplicability ?? []),
            "Exact readback changed proposal applicability.");
        var integrity = await store.CheckIntegrityAsync(
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.True(
            integrity.Healthy,
            "Private proposal retention did not remain semantically healthy.");
        LocalAssert.Equal(
            expectedRetainedDocuments,
            integrity.RetainedDocuments,
            "A replay, conflict, screening rejection, or restart changed the expected retained-document count.");
        LocalAssert.Equal(
            expectedRetainedDocuments,
            integrity.AuditEvents,
            "Private proposal retention did not keep one append audit receipt per accepted proposal.");
        var searchHits = await store.SearchHeadsAsync(
            LocalTestContext.PrimaryProjectId,
            "bounded continuation",
            limit: 10,
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            searchHits.Count,
            "A private pending proposal leaked into generic retained search.");
    }

    private static void AssertPrivatePending(JsonObject result)
    {
        LocalAssert.Equal(
            "pending",
            result["reviewState"]!.GetValue<string>(),
            "A standalone proposal did not remain pending.");
        LocalAssert.False(
            result["searchable"]!.GetValue<bool>(),
            "A private pending proposal was made searchable.");
        LocalAssert.False(
            result["deferredReviewRequired"]!.GetValue<bool>(),
            "The local-only path claimed a provisional-publication review.");
        LocalAssert.True(
            result["decisionId"] is null,
            "The local-only path created a review or publication decision.");
        LocalAssert.True(
            result["publicationConfidence"] is null,
            "The local-only path assigned publication confidence.");
    }

    private static JsonObject Structured(
        IReadOnlyList<JsonObject> responses,
        int id,
        bool expectedError)
    {
        var response = LocalMcpProtocol.ResponseById(responses, id);
        var envelope = LocalMcpProtocol.ToolEnvelope(response);
        LocalAssert.Equal(
            expectedError,
            envelope["isError"]?.GetValue<bool>() ?? false,
            $"The MCP error state for response {id} was unexpected: {envelope}");
        return LocalMcpProtocol.Structured(response);
    }

    private static string ProposalCall(
        int id,
        string projectLinkId,
        string idempotencyKey,
        string title,
        string summary,
        string content,
        DateTimeOffset occurredAt,
        JsonArray? evidenceReferences = null,
        JsonObject? metadataArguments = null)
    {
        var arguments = new JsonObject
        {
            ["projectLinkId"] = projectLinkId,
            ["idempotencyKey"] = idempotencyKey,
            ["feedbackKind"] = "quality",
            ["title"] = title,
            ["summary"] = summary,
            ["content"] = content,
            ["occurredAtUtc"] = occurredAt.ToString("O")
        };
        if (evidenceReferences is not null)
            arguments["evidenceReferences"] = evidenceReferences;
        if (metadataArguments is not null)
        {
            foreach (var property in metadataArguments)
                arguments[property.Key] = property.Value?.DeepClone();
        }
        return
        LocalMcpProtocol.ToolCall(
            id,
            "cortex_submit_proposal",
            arguments);
    }

    private static string ListProposalCall(
        int id,
        string projectLinkId,
        string? afterProposalId,
        int limit,
        bool includeContent)
    {
        var arguments = new JsonObject
        {
            ["projectLinkId"] = projectLinkId,
            ["limit"] = limit,
            ["includeContent"] = includeContent
        };
        if (afterProposalId is not null)
            arguments["afterProposalId"] = afterProposalId;
        return LocalMcpProtocol.ToolCall(
            id,
            "cortex_list_proposals",
            arguments);
    }

    private static JsonArray EvidenceReferences(
        string? sha256 = null) =>
        new(
            new JsonObject
            {
                ["evidenceId"] = "evidence-a",
                ["reference"] = "artifact://validation/evidence-a",
                ["mediaType"] = "application/json",
                ["sha256"] = sha256 ?? new string('0', 64),
                // This is a caller-reported verification assertion; Cortex
                // retains it exactly and does not resolve the artifact here.
                ["verification"] = "verified"
            });

    private static JsonArray OversizedEvidenceReferences()
    {
        var result = new JsonArray();
        for (var index = 0;
             index <= CortexProposalEvidencePolicy.MaximumItems;
             index++)
        {
            result.Add(new JsonObject
            {
                ["evidenceId"] = $"evidence-{index}",
                ["reference"] = $"artifact://validation/evidence-{index}",
                ["mediaType"] = "application/json",
                ["sha256"] = new string('0', 64),
                ["verification"] = "verified"
            });
        }
        return result;
    }

    private static string[] StdioArguments(LocalTestContext context) =>
    [
        "stdio",
        "--profile",
        context.ProfileId,
        "--state-root",
        Path.GetDirectoryName(context.DatabasePath)!
    ];
}
