using System.Text.Json;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Persistence.Testing.Learning;

internal static class LearningFeedbackApplicationTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("submission is idempotent conflict safe and immutable", SubmissionIdempotencyAsync),
        ("proposal 1.1 metadata is immutable and identity bound", ProposalMetadataV11Async),
        ("review is independent pending only and immutable", ReviewIndependenceAsync),
        ("namespace authority defaults to deny", NamespaceDenialAsync),
        ("repository scope is server derived", RepositoryScopeAsync),
        ("cancellation propagates through every operation", CancellationAsync),
        ("paging and status are bounded and deterministic", PagingAndStatusAsync),
        ("concurrent submission and review transitions are atomic", ConcurrencyAsync),
        ("repository adapter violations fail closed", RepositoryBoundaryAsync),
        ("audit metadata and diagnostics exclude raw content", AuditPrivacyAsync)
    ];

    private static async Task SubmissionIdempotencyAsync()
    {
        var repository = new InMemoryLearningFeedbackRepository();
        var service = Service(repository);
        var submitted = LearningFeedbackSamples.Proposal();

        var first = await service.SubmitAsync(
            new SubmitLearningFeedbackCommand(submitted, LearningFeedbackSamples.Submitter()),
            CancellationToken.None);
        ((Dictionary<string, string>)submitted.Payload)["observation"] = "mutated-after-submit";
        var replay = await service.SubmitAsync(
            new SubmitLearningFeedbackCommand(
                LearningFeedbackSamples.Proposal(),
                LearningFeedbackSamples.Submitter()),
            CancellationToken.None);
        var conflict = await service.SubmitAsync(
            new SubmitLearningFeedbackCommand(
                LearningFeedbackSamples.Proposal(summary: "A different sanitized correction."),
                LearningFeedbackSamples.Submitter()),
            CancellationToken.None);

        TestAssert.True(first.Succeeded);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Accepted, first.Disposition);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Idempotent, replay.Disposition);
        TestAssert.Equal(first.Proposal!.ProposalHash, replay.Proposal!.ProposalHash);
        TestAssert.Hash(first.Proposal.ProposalHash);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Conflict, conflict.Disposition);
        TestAssert.Code("learning_feedback_proposal_id_conflict", conflict.Diagnostics);
        TestAssert.Equal(1, repository.Count);

        var stored = await repository.FindAsync(
            LearningFeedbackSamples.Access(
                LearningFeedbackSamples.Reader(),
                LearningFeedbackNamespaceCapability.Read),
            "proposal-1",
            CancellationToken.None);
        TestAssert.Equal(
            "SENSITIVE-PAYLOAD bounded observation.",
            stored!.Proposal.Payload["observation"]);
        TestAssert.Equal(first.Proposal.Source, stored.Proposal.Source);
        TestAssert.Equal(first.Proposal.ProposalHash, stored.Proposal.ProposalHash);
        TestAssert.False(first.Proposal.IntendedUses is string[]);
        TestAssert.False(first.Proposal.Evidence is LearningFeedbackEvidenceV1[]);
        TestAssert.Throws<NotSupportedException>(() =>
            ((IList<string>)first.Proposal.IntendedUses)[0] = "fine_tuning");
        TestAssert.Throws<NotSupportedException>(() =>
            ((IList<LearningFeedbackEvidenceV1>)first.Proposal.Evidence)[0] =
                first.Proposal.Evidence[0] with { Verification = "changed" });
    }

    private static async Task ProposalMetadataV11Async()
    {
        var repository = new InMemoryLearningFeedbackRepository();
        var service = Service(repository);
        var keywords = new List<string> { "rollback", "bounded recovery" };
        var applicability = new List<LearningFeedbackApplicabilityV1>
        {
            new("pegasus-cortex", "persistence"),
            new("pegasus-coder", "coordination")
        };
        var submitted = LearningFeedbackSamples.Proposal("proposal-metadata") with
        {
            ContractId = LearningFeedbackContractIds.ProposalV1_1,
            Taxonomy = "testing",
            Keywords = keywords,
            Applicability = applicability
        };
        var first = await service.SubmitAsync(
            new SubmitLearningFeedbackCommand(
                submitted,
                LearningFeedbackSamples.Submitter()),
            CancellationToken.None);
        keywords[0] = "changed-after-submit";
        applicability[0] = new LearningFeedbackApplicabilityV1("changed", "changed");

        var replay = await service.SubmitAsync(
            new SubmitLearningFeedbackCommand(
                LearningFeedbackSamples.Proposal("proposal-metadata") with
                {
                    ContractId = LearningFeedbackContractIds.ProposalV1_1,
                    Taxonomy = "testing",
                    Keywords = ["bounded recovery", "rollback"],
                    Applicability =
                    [
                        new("pegasus-coder", "coordination"),
                        new("pegasus-cortex", "persistence")
                    ]
                },
                LearningFeedbackSamples.Submitter()),
            CancellationToken.None);
        var conflict = await service.SubmitAsync(
            new SubmitLearningFeedbackCommand(
                LearningFeedbackSamples.Proposal("proposal-metadata") with
                {
                    ContractId = LearningFeedbackContractIds.ProposalV1_1,
                    Taxonomy = "testing",
                    Keywords = ["different"],
                    Applicability = []
                },
                LearningFeedbackSamples.Submitter()),
            CancellationToken.None);

        TestAssert.Equal(LearningFeedbackMutationDisposition.Accepted, first.Disposition);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Idempotent, replay.Disposition);
        TestAssert.Equal(first.Proposal!.ProposalHash, replay.Proposal!.ProposalHash);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Conflict, conflict.Disposition);
        TestAssert.Equal("bounded recovery,rollback", string.Join(',', first.Proposal.Keywords!));
        TestAssert.Equal(
            "pegasus-coder/coordination,pegasus-cortex/persistence",
            string.Join(',', first.Proposal.Applicability!.Select(item =>
                $"{item.ProductId}/{item.ComponentId}")));
        TestAssert.False(first.Proposal.Keywords is string[]);
        TestAssert.False(first.Proposal.Applicability is LearningFeedbackApplicabilityV1[]);
        TestAssert.Throws<NotSupportedException>(() =>
            ((IList<string>)first.Proposal.Keywords!)[0] = "changed");
    }

    private static async Task ReviewIndependenceAsync()
    {
        var repository = new InMemoryLearningFeedbackRepository();
        var service = Service(repository);
        var submission = await SubmitAsync(service, "proposal-review");
        var proposal = submission.Proposal!;

        var selfReviewer = LearningFeedbackSamples.Reviewer(
            authorityId: "nexus",
            subjectId: "submitter-1");
        var selfDecision = LearningFeedbackSamples.Decision(
            proposal,
            selfReviewer,
            "review-self");
        var denied = await service.ReviewAsync(
            new ReviewLearningFeedbackCommand(selfDecision, selfReviewer),
            CancellationToken.None);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Rejected, denied.Disposition);
        TestAssert.Code("learning_feedback_review_independence_required", denied.Diagnostics);

        // Subject identifiers are scoped by authority; this is a different server-derived principal.
        var authorityQualifiedReviewer = LearningFeedbackSamples.Reviewer(
            authorityId: "review-board",
            subjectId: "submitter-1");
        var decision = LearningFeedbackSamples.Decision(
            proposal,
            authorityQualifiedReviewer,
            "review-authority-qualified");
        var accepted = await service.ReviewAsync(
            new ReviewLearningFeedbackCommand(decision, authorityQualifiedReviewer),
            CancellationToken.None);
        var replay = await service.ReviewAsync(
            new ReviewLearningFeedbackCommand(decision, authorityQualifiedReviewer),
            CancellationToken.None);

        var anotherReviewer = LearningFeedbackSamples.Reviewer(subjectId: "reviewer-2");
        var replacement = LearningFeedbackSamples.Decision(
            proposal,
            anotherReviewer,
            "review-replacement",
            "rejected");
        var conflict = await service.ReviewAsync(
            new ReviewLearningFeedbackCommand(replacement, anotherReviewer),
            CancellationToken.None);

        TestAssert.Equal(LearningFeedbackMutationDisposition.Accepted, accepted.Disposition);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Idempotent, replay.Disposition);
        TestAssert.Equal(accepted.Decision!.DecisionHash, replay.Decision!.DecisionHash);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Conflict, conflict.Disposition);
        TestAssert.Code("learning_feedback_already_reviewed", conflict.Diagnostics);
        TestAssert.Equal(proposal.Source.CorrelationId, conflict.Audit!.CorrelationId);

        var stored = await repository.FindAsync(
            LearningFeedbackSamples.Access(
                authorityQualifiedReviewer,
                LearningFeedbackNamespaceCapability.Review),
            proposal.ProposalId,
            CancellationToken.None);
        TestAssert.Equal(accepted.Decision.DecisionHash, stored!.Decision!.DecisionHash);
        TestAssert.Equal("approved", stored.ReviewState);
    }

    private static async Task NamespaceDenialAsync()
    {
        var repository = new InMemoryLearningFeedbackRepository();
        var service = Service(repository);
        var deniedSubmit = await service.SubmitAsync(
            new SubmitLearningFeedbackCommand(
                LearningFeedbackSamples.Proposal(),
                LearningFeedbackSamples.NoGrants()),
            CancellationToken.None);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Rejected, deniedSubmit.Disposition);
        TestAssert.Code("learning_feedback_namespace_forbidden", deniedSubmit.Diagnostics);
        TestAssert.Equal(0, repository.Count);

        var submission = await service.SubmitAsync(
            new SubmitLearningFeedbackCommand(
                LearningFeedbackSamples.Proposal(),
                LearningFeedbackSamples.Submitter(canRead: false)),
            CancellationToken.None);
        TestAssert.True(submission.Succeeded);

        var deniedRead = await service.ListAsync(
            new ListLearningFeedbackQuery(
                "project:project-1",
                null,
                10,
                LearningFeedbackSamples.Submitter(canRead: false)),
            CancellationToken.None);
        TestAssert.False(deniedRead.Accepted);
        TestAssert.Code("learning_feedback_namespace_forbidden", deniedRead.Diagnostics);

        var readOnly = LearningFeedbackSamples.Reader();
        var deniedReview = await service.ReviewAsync(
            new ReviewLearningFeedbackCommand(
                LearningFeedbackSamples.Decision(submission.Proposal!, readOnly),
                readOnly),
            CancellationToken.None);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Rejected, deniedReview.Disposition);
        TestAssert.Code("learning_feedback_namespace_forbidden", deniedReview.Diagnostics);

        var crossNamespace = await service.ListAsync(
            new ListLearningFeedbackQuery(
                "project:other",
                null,
                10,
                LearningFeedbackSamples.Reader()),
            CancellationToken.None);
        TestAssert.False(crossNamespace.Accepted);
        TestAssert.Code("learning_feedback_namespace_forbidden", crossNamespace.Diagnostics);
    }

    private static async Task RepositoryScopeAsync()
    {
        var repository = new InMemoryLearningFeedbackRepository();
        var proposal = LearningFeedbackSamples.AcceptedProposal("proposal-authority-scope");
        var submitAccess = LearningFeedbackSamples.Access(
            LearningFeedbackSamples.Submitter(),
            LearningFeedbackNamespaceCapability.Submit);
        var otherSubmitAccess = LearningFeedbackSamples.Access(
            LearningFeedbackSamples.Submitter(namespaceName: "project:other"),
            LearningFeedbackNamespaceCapability.Submit,
            "project:other");

        await TestAssert.ThrowsAsync<ArgumentException>(() =>
            repository.AddProposalAsync(
                otherSubmitAccess,
                proposal,
                CancellationToken.None).AsTask());

        var write = await repository.AddProposalAsync(
            submitAccess,
            proposal,
            CancellationToken.None);
        TestAssert.Equal(LearningFeedbackProposalWriteDisposition.Added, write.Disposition);

        var authorized = await repository.FindAsync(
            LearningFeedbackSamples.Access(
                LearningFeedbackSamples.Reader(),
                LearningFeedbackNamespaceCapability.Read),
            proposal.ProposalId,
            CancellationToken.None);
        var crossScope = await repository.FindAsync(
            LearningFeedbackSamples.Access(
                LearningFeedbackSamples.Reader("project:other"),
                LearningFeedbackNamespaceCapability.Read,
                "project:other"),
            proposal.ProposalId,
            CancellationToken.None);

        TestAssert.True(authorized is not null);
        TestAssert.Equal<LearningFeedbackSnapshot?>(null, crossScope);
    }

    private static async Task CancellationAsync()
    {
        var repository = new InMemoryLearningFeedbackRepository();
        var service = Service(repository);
        var submitted = await SubmitAsync(service, "proposal-cancel");
        using var source = new CancellationTokenSource();
        source.Cancel();

        await TestAssert.ThrowsAsync<OperationCanceledException>(() =>
            service.SubmitAsync(
                new SubmitLearningFeedbackCommand(
                    LearningFeedbackSamples.Proposal("proposal-cancel-submit"),
                    LearningFeedbackSamples.Submitter()),
                source.Token).AsTask());
        await TestAssert.ThrowsAsync<OperationCanceledException>(() =>
            service.ReviewAsync(
                new ReviewLearningFeedbackCommand(
                    LearningFeedbackSamples.Decision(
                        submitted.Proposal!,
                        LearningFeedbackSamples.Reviewer()),
                    LearningFeedbackSamples.Reviewer()),
                source.Token).AsTask());
        await TestAssert.ThrowsAsync<OperationCanceledException>(() =>
            service.ListAsync(
                new ListLearningFeedbackQuery(
                    "project:project-1",
                    null,
                    10,
                    LearningFeedbackSamples.Reader()),
                source.Token).AsTask());
        await TestAssert.ThrowsAsync<OperationCanceledException>(() =>
            service.GetStatusAsync(
                new GetLearningFeedbackStatusQuery(
                    "project:project-1",
                    LearningFeedbackSamples.Reader()),
                source.Token).AsTask());

        using var duringBoundary = new CancellationTokenSource();
        var cancelingRepository = new FaultyLearningFeedbackRepository(
            FaultyRepositoryMode.MutablePage,
            submitted.Proposal!,
            duringBoundary);
        await TestAssert.ThrowsAsync<OperationCanceledException>(() =>
            Service(cancelingRepository).ListAsync(
                new ListLearningFeedbackQuery(
                    "project:project-1",
                    null,
                    10,
                    LearningFeedbackSamples.Reader()),
                duringBoundary.Token).AsTask());
    }

    private static async Task PagingAndStatusAsync()
    {
        var repository = new InMemoryLearningFeedbackRepository();
        var service = Service(repository);
        var ids = new[] { "proposal-03", "proposal-01", "proposal-05", "proposal-02", "proposal-04" };
        var accepted = new Dictionary<string, LearningFeedbackProposalV1>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var result = await SubmitAsync(service, id);
            accepted.Add(id, result.Proposal!);
        }

        var first = await service.ListAsync(
            new ListLearningFeedbackQuery(
                "project:project-1",
                null,
                2,
                LearningFeedbackSamples.Reader()),
            CancellationToken.None);
        var second = await service.ListAsync(
            new ListLearningFeedbackQuery(
                "project:project-1",
                first.Page!.NextAfterProposalId,
                2,
                LearningFeedbackSamples.Reader()),
            CancellationToken.None);
        var third = await service.ListAsync(
            new ListLearningFeedbackQuery(
                "project:project-1",
                second.Page!.NextAfterProposalId,
                2,
                LearningFeedbackSamples.Reader()),
            CancellationToken.None);

        TestAssert.SequenceEqual(
            new[] { "proposal-01", "proposal-02" },
            first.Page.Items.Select(item => item.Proposal.ProposalId));
        TestAssert.Equal("proposal-02", first.Page.NextAfterProposalId);
        TestAssert.SequenceEqual(
            new[] { "proposal-03", "proposal-04" },
            second.Page.Items.Select(item => item.Proposal.ProposalId));
        TestAssert.SequenceEqual(
            new[] { "proposal-05" },
            third.Page!.Items.Select(item => item.Proposal.ProposalId));
        TestAssert.Equal<string?>(null, third.Page.NextAfterProposalId);

        var invalid = await service.ListAsync(
            new ListLearningFeedbackQuery(
                "project:project-1",
                null,
                LearningFeedbackApplicationLimits.MaximumPageSize + 1,
                LearningFeedbackSamples.Reader()),
            CancellationToken.None);
        TestAssert.False(invalid.Accepted);
        TestAssert.Code("learning_feedback_page_request_invalid", invalid.Diagnostics);

        await ReviewAsync(service, accepted["proposal-01"], "review-approved", "approved", "reviewer-a");
        await ReviewAsync(service, accepted["proposal-02"], "review-rejected", "rejected", "reviewer-b");
        var status = await service.GetStatusAsync(
            new GetLearningFeedbackStatusQuery(
                "project:project-1",
                LearningFeedbackSamples.Reader()),
            CancellationToken.None);
        TestAssert.True(status.Accepted);
        TestAssert.Equal(5L, status.Status!.Total);
        TestAssert.Equal(3L, status.Status.Pending);
        TestAssert.Equal(1L, status.Status.Approved);
        TestAssert.Equal(1L, status.Status.Rejected);
    }

    private static async Task ConcurrencyAsync()
    {
        var repository = new InMemoryLearningFeedbackRepository();
        var service = Service(repository);
        var firstProposal = LearningFeedbackSamples.Proposal(
            "proposal-submit-race",
            summary: "First concurrent sanitized value.");
        var secondProposal = LearningFeedbackSamples.Proposal(
            "proposal-submit-race",
            summary: "Second concurrent sanitized value.");
        var submissionRace = await Task.WhenAll(
            Task.Run(async () => await service.SubmitAsync(
                new SubmitLearningFeedbackCommand(firstProposal, LearningFeedbackSamples.Submitter()),
                CancellationToken.None)),
            Task.Run(async () => await service.SubmitAsync(
                new SubmitLearningFeedbackCommand(secondProposal, LearningFeedbackSamples.Submitter()),
                CancellationToken.None)));

        TestAssert.Equal(1, submissionRace.Count(result => result.Disposition == LearningFeedbackMutationDisposition.Accepted));
        TestAssert.Equal(1, submissionRace.Count(result => result.Disposition == LearningFeedbackMutationDisposition.Conflict));

        var submitted = await SubmitAsync(service, "proposal-review-race");
        var reviewerA = LearningFeedbackSamples.Reviewer(subjectId: "reviewer-race-a");
        var reviewerB = LearningFeedbackSamples.Reviewer(subjectId: "reviewer-race-b");
        var reviewRace = await Task.WhenAll(
            Task.Run(async () => await service.ReviewAsync(
                new ReviewLearningFeedbackCommand(
                    LearningFeedbackSamples.Decision(submitted.Proposal!, reviewerA, "review-race-a", "approved"),
                    reviewerA),
                CancellationToken.None)),
            Task.Run(async () => await service.ReviewAsync(
                new ReviewLearningFeedbackCommand(
                    LearningFeedbackSamples.Decision(submitted.Proposal!, reviewerB, "review-race-b", "rejected"),
                    reviewerB),
                CancellationToken.None)));

        TestAssert.Equal(1, reviewRace.Count(result => result.Disposition == LearningFeedbackMutationDisposition.Accepted));
        TestAssert.Equal(1, reviewRace.Count(result => result.Disposition == LearningFeedbackMutationDisposition.Conflict));
        var stored = await repository.FindAsync(
            LearningFeedbackSamples.Access(
                reviewerA,
                LearningFeedbackNamespaceCapability.Review),
            "proposal-review-race",
            CancellationToken.None);
        TestAssert.True(stored!.Decision is not null);
        TestAssert.Equal(
            reviewRace.Single(result => result.Succeeded).Decision!.DecisionHash,
            stored.Decision!.DecisionHash);
    }

    private static async Task RepositoryBoundaryAsync()
    {
        var accepted = LearningFeedbackSamples.AcceptedProposal("proposal-boundary");

        var lookupRepository = new InMemoryLearningFeedbackRepository();
        var lookupService = Service(lookupRepository);
        var lookupSubmission = await SubmitAsync(lookupService, "proposal-lookup");
        var lookupReviewer = LearningFeedbackSamples.Reviewer();
        var invalidLookup = await lookupService.ReviewAsync(
            new ReviewLearningFeedbackCommand(
                LearningFeedbackSamples.Decision(lookupSubmission.Proposal!, lookupReviewer) with
                {
                    ProposalId = null!
                },
                lookupReviewer),
            CancellationToken.None);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Rejected, invalidLookup.Disposition);
        TestAssert.Code("learning_feedback_review_lookup_invalid", invalidLookup.Diagnostics);

        var corruptWrite = Service(new FaultyLearningFeedbackRepository(
            FaultyRepositoryMode.CorruptProposalWrite,
            accepted));
        var write = await corruptWrite.SubmitAsync(
            new SubmitLearningFeedbackCommand(
                LearningFeedbackSamples.Proposal("proposal-boundary"),
                LearningFeedbackSamples.Submitter()),
            CancellationToken.None);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Rejected, write.Disposition);
        TestAssert.Code("learning_feedback_repository_result_invalid", write.Diagnostics);

        var malformedFind = Service(new FaultyLearningFeedbackRepository(
            FaultyRepositoryMode.MalformedFind,
            accepted));
        var reviewer = LearningFeedbackSamples.Reviewer();
        var review = await malformedFind.ReviewAsync(
            new ReviewLearningFeedbackCommand(
                LearningFeedbackSamples.Decision(accepted, reviewer),
                reviewer),
            CancellationToken.None);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Rejected, review.Disposition);
        TestAssert.Code("learning_feedback_repository_integrity_invalid", review.Diagnostics);

        var corruptReview = Service(new FaultyLearningFeedbackRepository(
            FaultyRepositoryMode.CorruptReviewWrite,
            accepted));
        var corruptReviewResult = await corruptReview.ReviewAsync(
            new ReviewLearningFeedbackCommand(
                LearningFeedbackSamples.Decision(accepted, reviewer),
                reviewer),
            CancellationToken.None);
        TestAssert.Equal(LearningFeedbackMutationDisposition.Rejected, corruptReviewResult.Disposition);
        TestAssert.Code("learning_feedback_repository_result_invalid", corruptReviewResult.Diagnostics);

        var mutableRepository = new FaultyLearningFeedbackRepository(
            FaultyRepositoryMode.MutablePage,
            accepted);
        var pageResult = await Service(mutableRepository).ListAsync(
            new ListLearningFeedbackQuery(
                "project:project-1",
                null,
                10,
                LearningFeedbackSamples.Reader()),
            CancellationToken.None);
        TestAssert.True(pageResult.Accepted);
        mutableRepository.ClearMutablePage();
        TestAssert.Equal(1, pageResult.Page!.Items.Count);
        TestAssert.Equal("proposal-boundary", pageResult.Page.Items[0].Proposal.ProposalId);

        var lyingCount = await Service(new LyingCountLearningFeedbackRepository(
                new LearningFeedbackSnapshot(accepted, null),
                new LearningFeedbackSnapshot(
                    LearningFeedbackSamples.AcceptedProposal("proposal-boundary-2"),
                    null)))
            .ListAsync(
                new ListLearningFeedbackQuery(
                    "project:project-1",
                    null,
                    1,
                    LearningFeedbackSamples.Reader()),
                CancellationToken.None);
        TestAssert.False(lyingCount.Accepted);
        TestAssert.Code("learning_feedback_repository_page_invalid", lyingCount.Diagnostics);
    }

    private static async Task AuditPrivacyAsync()
    {
        var repository = new InMemoryLearningFeedbackRepository();
        var service = Service(repository);
        var submission = await SubmitAsync(service, "proposal-audit");
        var reviewer = LearningFeedbackSamples.Reviewer();
        var review = await service.ReviewAsync(
            new ReviewLearningFeedbackCommand(
                LearningFeedbackSamples.Decision(submission.Proposal!, reviewer),
                reviewer),
            CancellationToken.None);
        var conflict = await service.SubmitAsync(
            new SubmitLearningFeedbackCommand(
                LearningFeedbackSamples.Proposal(
                    "proposal-audit",
                    summary: "SENSITIVE-CONFLICT-SUMMARY"),
                LearningFeedbackSamples.Submitter()),
            CancellationToken.None);

        var serialized = JsonSerializer.Serialize(new
        {
            SubmissionAudit = submission.Audit,
            ReviewAudit = review.Audit,
            ConflictAudit = conflict.Audit,
            conflict.Diagnostics
        });
        TestAssert.Contains("proposal-audit", serialized);
        TestAssert.DoesNotContain("SENSITIVE-SUMMARY", serialized);
        TestAssert.DoesNotContain("SENSITIVE-PAYLOAD", serialized);
        TestAssert.DoesNotContain("SENSITIVE-REASON", serialized);
        TestAssert.DoesNotContain("SENSITIVE-CONFLICT-SUMMARY", serialized);
        TestAssert.DoesNotContain("artifact://verification", serialized);
        TestAssert.Equal(
            submission.Proposal!.Source.CorrelationId,
            review.Audit!.CorrelationId);
    }

    private static LearningFeedbackApplicationService Service(ILearningFeedbackRepository repository) =>
        new(repository, new FixedTimeProvider(LearningFeedbackSamples.Now));

    private static async Task<LearningFeedbackSubmissionResult> SubmitAsync(
        LearningFeedbackApplicationService service,
        string proposalId)
    {
        var result = await service.SubmitAsync(
            new SubmitLearningFeedbackCommand(
                LearningFeedbackSamples.Proposal(proposalId),
                LearningFeedbackSamples.Submitter()),
            CancellationToken.None);
        TestAssert.True(result.Succeeded);
        return result;
    }

    private static async Task ReviewAsync(
        LearningFeedbackApplicationService service,
        LearningFeedbackProposalV1 proposal,
        string decisionId,
        string state,
        string reviewerSubject)
    {
        var reviewer = LearningFeedbackSamples.Reviewer(subjectId: reviewerSubject);
        var result = await service.ReviewAsync(
            new ReviewLearningFeedbackCommand(
                LearningFeedbackSamples.Decision(proposal, reviewer, decisionId, state),
                reviewer),
            CancellationToken.None);
        TestAssert.True(result.Succeeded);
    }
}
