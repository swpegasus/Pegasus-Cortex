using System.Data;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Learning;
using Pegasus.Cortex.Persistence.PostgreSql.Connections;
using Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;
using Pegasus.Cortex.Persistence.PostgreSql.Learning;

namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

/// <summary>
/// Exercises migration 0007 through the real runtime repository. The migration
/// connection is used only for bounded fixture setup and independent evidence
/// queries; author changes always run as the restricted runtime role.
/// </summary>
internal static class FixturePublicationChangeLiveWorkflow
{
    private const string TenantId = "fixture-publication-tenant";
    private const string Namespace = "project:fixture-publication";
    private const string AuthorAuthorityId = "fixture-publication-authority";
    private const string AuthorSubjectId = "fixture-publication-author";
    private const string AuthorServiceId = "fixture-publication-service";
    private const long PolicyRevision = 29;

    public static async Task RunAsync(
        FixtureConfiguration configuration,
        Action<string> reportPass,
        CancellationToken cancellationToken)
    {
        await using var runtimeDataSource = LivePostgreSqlFixture.BuildDataSource(
            configuration.RuntimeConnectionString,
            configuration.RootCertificatePath,
            "pegasus-cortex-publication-change-live");
        await using var migrationDataSource = LivePostgreSqlFixture.BuildDataSource(
            configuration.MigrationConnectionString,
            configuration.RootCertificatePath,
            "pegasus-cortex-publication-change-evidence");
        var runner = new PostgreSqlTransactionRunner(runtimeDataSource);
        Span<byte> key = stackalloc byte[32];
        key.Fill(0xC7);
        using var authenticator = new TestHmacRowAuthenticator(
            "fixture-publication-key",
            key);
        CryptographicOperations.ZeroMemory(key);
        var repository = new PostgreSqlLearningFeedbackRepository(runner, authenticator);

        var first = await PublishAsync(
            repository,
            "publication-cas",
            "Concurrent publication correction",
            DateTimeOffset.Parse("2026-08-07T18:10:00+00:00"),
            migrationDataSource,
            cancellationToken).ConfigureAwait(false);
        var supersededTarget = await PublishAsync(
            repository,
            "publication-owner",
            "Owner-bound publication correction",
            DateTimeOffset.Parse("2026-08-07T18:20:00+00:00"),
            migrationDataSource,
            cancellationToken).ConfigureAwait(false);
        var replacement = await PublishAsync(
            repository,
            "publication-replacement",
            "Replacement publication correction",
            DateTimeOffset.Parse("2026-08-07T18:30:00+00:00"),
            migrationDataSource,
            cancellationToken).ConfigureAwait(false);

        var authorAccess = Access(
            AuthorAuthorityId,
            AuthorSubjectId,
            AuthorServiceId,
            LearningFeedbackNamespaceCapability.Submit);
        var firstChange = AcceptChange(
            authorAccess,
            first,
            LearningFeedbackPublicationChangeReasonCodes.UnsafeContent,
            "The first correction withdraws guidance that no longer satisfies the fixture.",
            DateTimeOffset.Parse("2026-08-07T19:10:00+00:00"));
        var secondChange = AcceptChange(
            authorAccess,
            first,
            LearningFeedbackPublicationChangeReasonCodes.InvalidContent,
            "The competing correction independently withdraws the same invalid guidance.",
            DateTimeOffset.Parse("2026-08-07T19:10:01+00:00"));

        var concurrent = await Task.WhenAll(
            ApplyCapturedAsync(repository, authorAccess, firstChange, cancellationToken),
            ApplyCapturedAsync(repository, authorAccess, secondChange, cancellationToken))
            .ConfigureAwait(false);
        var applied = concurrent.SingleOrDefault(value =>
            value.Result?.Disposition == LearningFeedbackPublicationChangeWriteDisposition.Applied);
        FixtureAssert.True(
            applied is not null && concurrent.Count(value =>
                value.Result?.Disposition == LearningFeedbackPublicationChangeWriteDisposition.Applied) == 1,
            "Concurrent publication changes did not produce exactly one applied CAS winner.");
        var loser = concurrent.Single(value => !ReferenceEquals(value, applied));
        FixtureAssert.True(
            loser.Result?.Disposition == LearningFeedbackPublicationChangeWriteDisposition.StaleHead ||
            loser.Error is { Retryable: true, SqlState: "40001" },
            "The losing publication change was neither stale nor a retryable serialization loser.");

        var replay = await repository.ApplyPublicationChangeAsync(
            authorAccess,
            applied!.Change,
            cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            replay.Disposition == LearningFeedbackPublicationChangeWriteDisposition.Existing,
            "An exact applied publication-change retry was not idempotent.");

        var staleRetry = await repository.ApplyPublicationChangeAsync(
            authorAccess,
            loser.Change,
            cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            staleRetry.Disposition == LearningFeedbackPublicationChangeWriteDisposition.StaleHead,
            "A fresh retry of the losing publication change did not report the stale head.");

        var identityMismatches = new[]
        {
            ("fixture-intruder-authority", AuthorSubjectId, AuthorServiceId),
            (AuthorAuthorityId, "fixture-intruder-subject", AuthorServiceId),
            (AuthorAuthorityId, AuthorSubjectId, "fixture-intruder-service")
        };
        for (var index = 0; index < identityMismatches.Length; index++)
        {
            var mismatch = identityMismatches[index];
            var intruderAccess = Access(
                mismatch.Item1,
                mismatch.Item2,
                mismatch.Item3,
                LearningFeedbackNamespaceCapability.Submit);
            var intruderChange = AcceptChange(
                intruderAccess,
                supersededTarget,
                LearningFeedbackPublicationChangeReasonCodes.UnsafeContent,
                "A one-field identity mismatch must not change another author's publication.",
                DateTimeOffset.Parse("2026-08-07T19:20:00+00:00").AddSeconds(index));
            var denied = await repository.ApplyPublicationChangeAsync(
                intruderAccess,
                intruderChange,
                cancellationToken).ConfigureAwait(false);
            FixtureAssert.True(
                denied.Disposition == LearningFeedbackPublicationChangeWriteDisposition.NotOwner,
                "Migration 0007 accepted an authority, subject, or service ownership mismatch.");
        }

        var supersedeChange = AcceptChange(
            authorAccess,
            supersededTarget,
            LearningFeedbackPublicationChangeReasonCodes.Corrected,
            "The replacement contains the corrected reusable guidance.",
            DateTimeOffset.Parse("2026-08-07T19:30:00+00:00"),
            LearningFeedbackPublicationChangeActions.Supersede,
            replacement);
        var supersedeResult = await repository.ApplyPublicationChangeAsync(
            authorAccess,
            supersedeChange,
            cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            supersedeResult.Disposition == LearningFeedbackPublicationChangeWriteDisposition.Applied,
            "Migration 0007 did not apply an owner-authorized supersede transition.");

        await VerifyDirectProtectedTableAccessDeniedAsync(runtimeDataSource, cancellationToken)
            .ConfigureAwait(false);
        await VerifyPublicationEvidenceAsync(
            migrationDataSource,
            first,
            applied.Change,
            supersededTarget,
            supersedeChange,
            replacement,
            cancellationToken).ConfigureAwait(false);
        reportPass("migration 0007 exact ownership, withdraw/supersede lineage, protected RLS, idempotency, and concurrent CAS");
    }

    private static async Task<PublishedHead> PublishAsync(
        PostgreSqlLearningFeedbackRepository repository,
        string suffix,
        string title,
        DateTimeOffset occurredAt,
        NpgsqlDataSource migrationDataSource,
        CancellationToken cancellationToken)
    {
        var authority = AuthorAuthority();
        var proposal = AcceptedProposal(authority, suffix, title, occurredAt);
        var submit = await repository.AddProposalAsync(
            Access(
                authority.AuthorityId,
                authority.SubjectId,
                authority.ServiceId,
                LearningFeedbackNamespaceCapability.Submit),
            proposal,
            cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            submit.Disposition == LearningFeedbackProposalWriteDisposition.Added,
            "The native publication fixture could not add its proposal.");

        var reviewerAccess = Access(
            "fixture-review-authority",
            "fixture-reviewer",
            "fixture-review-service",
            LearningFeedbackNamespaceCapability.Review);
        var decision = Decision(proposal, reviewerAccess, occurredAt.AddMinutes(1));
        var review = await repository.AddDecisionIfPendingAsync(
            reviewerAccess,
            decision,
            cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            review.Disposition == LearningFeedbackDecisionWriteDisposition.Added,
            "The native publication fixture could not publish its approved proposal.");

        return await ReadPublishedHeadAsync(
            migrationDataSource,
            title,
            cancellationToken).ConfigureAwait(false);
    }

    private static LearningFeedbackProposalV1 AcceptedProposal(
        LearningFeedbackAuthorityContext authority,
        string suffix,
        string title,
        DateTimeOffset occurredAt)
    {
        var content = $"Fixture guidance for {suffix} remains bounded and independently verifiable.";
        var screeningHash = new string('b', 64);
        var submitted = new LearningFeedbackProposalV1(
            1,
            LearningFeedbackContractIds.ProposalV1_1,
            LearningFeedbackContractIds.Canonicalization,
            $"proposal-{suffix}",
            authority.TenantId,
            Namespace,
            "quality",
            new LearningFeedbackSourceV1(
                authority.AuthorityId,
                authority.SubjectId,
                authority.ServiceId,
                "fixture-agent",
                null,
                "fixture-endpoint",
                "native-publication-fixture",
                null,
                $"fixture-link-{suffix}",
                $"fixture-source-{suffix}",
                $"fixture-source-revision-{suffix}",
                CortexCanonicalJson.Sha256(Encoding.UTF8.GetBytes(content)),
                occurredAt),
            $"Native PostgreSQL publication-change fixture for {suffix}.",
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["content"] = content,
                ["retentionRecord"] = "true",
                ["sourceDisposition"] = "inline-submitted-content",
                ["title"] = title
            },
            ["postgresql", "publication-change", suffix],
            [],
            new LearningFeedbackScreeningV1(
                "sanitized",
                "passed",
                "passed",
                "passed",
                "passed",
                "integration",
                authority.PolicyRevision,
                "fixture-screening",
                "fixture-screening-decision",
                screeningHash,
                occurredAt),
            "pending",
            LearningFeedbackContractIds.ContentTreatment,
            false,
            occurredAt,
            new string('0', 64),
            "testing",
            ["postgresql", "publication change", suffix],
            [new LearningFeedbackApplicabilityV1("pegasus-cortex", "postgresql")]);
        var accepted = LearningFeedbackPolicy.AcceptProposal(
            submitted,
            authority,
            occurredAt,
            CancellationToken.None);
        FixtureAssert.True(accepted.Accepted, "The native publication proposal was rejected.");
        return accepted.Value!;
    }

    private static LearningFeedbackReviewDecisionV1 Decision(
        LearningFeedbackProposalV1 proposal,
        LearningFeedbackNamespaceAccess reviewer,
        DateTimeOffset occurredAt)
    {
        var submitted = new LearningFeedbackReviewDecisionV1(
            1,
            LearningFeedbackContractIds.ReviewDecision,
            LearningFeedbackContractIds.Canonicalization,
            $"decision-{proposal.ProposalId}",
            proposal.ProposalId,
            proposal.ProposalHash,
            proposal.TenantId,
            proposal.Namespace,
            "approved",
            reviewer.AuthorityId,
            reviewer.SubjectId,
            reviewer.PolicyRevision,
            "Native fixture publication acceptance.",
            occurredAt,
            new string('0', 64));
        return submitted with
        {
            DecisionHash = LearningFeedbackPolicy.ComputeDecisionHash(submitted)
        };
    }

    private static LearningFeedbackPublicationChangeV1 AcceptChange(
        LearningFeedbackNamespaceAccess access,
        PublishedHead head,
        string reasonCode,
        string reason,
        DateTimeOffset occurredAt,
        string action = LearningFeedbackPublicationChangeActions.Withdraw,
        PublishedHead? replacement = null)
    {
        var request = new LearningFeedbackPublicationChangeRequestV1(
            1,
            LearningFeedbackContractIds.PublicationChange,
            LearningFeedbackContractIds.Canonicalization,
            action,
            head.RecordId,
            head.RevisionId,
            head.Generation,
            replacement is null
                ? null
                : new LearningFeedbackPublicationReplacementV1(
                    replacement.RecordId,
                    replacement.RevisionId,
                    replacement.ContentHash),
            reasonCode,
            reason,
            occurredAt);
        var accepted = LearningFeedbackPublicationChangePolicy.Accept(
            request,
            access,
            occurredAt,
            CancellationToken.None);
        FixtureAssert.True(accepted.Accepted, "The native publication change was rejected.");
        return accepted.Value!;
    }

    private static async Task<CapturedChange> ApplyCapturedAsync(
        PostgreSqlLearningFeedbackRepository repository,
        LearningFeedbackNamespaceAccess access,
        LearningFeedbackPublicationChangeV1 change,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await repository.ApplyPublicationChangeAsync(
                access,
                change,
                cancellationToken).ConfigureAwait(false);
            return new CapturedChange(change, result, null);
        }
        catch (PostgreSqlPersistenceException exception)
        {
            return new CapturedChange(change, null, exception);
        }
    }

    private static async Task<PublishedHead> ReadPublishedHeadAsync(
        NpgsqlDataSource dataSource,
        string title,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT head_value.record_id, head_value.current_revision_id,
                   head_value.generation, revision_value.content_hash
            FROM cortex_data.knowledge_head head_value
            JOIN cortex_data.knowledge_revision revision_value
              ON revision_value.tenant_id = head_value.tenant_id
             AND revision_value.namespace_name = head_value.namespace_name
             AND revision_value.record_id = head_value.record_id
             AND revision_value.revision_id = head_value.current_revision_id
            WHERE head_value.tenant_id = @tenant_id
              AND head_value.namespace_name = @namespace
              AND revision_value.title = @title;
            """,
            connection);
        command.Parameters.AddWithValue("tenant_id", TenantId);
        command.Parameters.AddWithValue("namespace", Namespace);
        command.Parameters.AddWithValue("title", title);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "The native publication fixture could not resolve its promoted head.");
        var result = new PublishedHead(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3));
        FixtureAssert.True(
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "The native publication fixture resolved more than one promoted head.");
        return result;
    }

    private static async Task VerifyDirectProtectedTableAccessDeniedAsync(
        NpgsqlDataSource runtimeDataSource,
        CancellationToken cancellationToken)
    {
        await using var connection = await runtimeDataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM cortex_data.knowledge_publication_change;",
            connection);
        try
        {
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            throw new FixtureAssertionException(
                "The runtime role directly read a protected publication-change table.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
        }
    }

    private static async Task VerifyPublicationEvidenceAsync(
        NpgsqlDataSource dataSource,
        PublishedHead withdrawn,
        LearningFeedbackPublicationChangeV1 withdrawal,
        PublishedHead superseded,
        LearningFeedbackPublicationChangeV1 supersede,
        PublishedHead replacement,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM cortex_data.knowledge_publication_origin
                 WHERE tenant_id = @tenant_id AND namespace_name = @namespace) = 3,
                (SELECT count(*) FROM cortex_data.knowledge_publication_change
                 WHERE tenant_id = @tenant_id AND namespace_name = @namespace) = 2,
                (SELECT count(*) FROM cortex_data.knowledge_publication_change
                 WHERE tenant_id = @tenant_id AND namespace_name = @namespace
                   AND target_record_id = @withdrawn_record_id) = 1,
                (SELECT generation FROM cortex_data.knowledge_head
                 WHERE tenant_id = @tenant_id AND namespace_name = @namespace
                   AND record_id = @withdrawn_record_id) = @withdrawn_generation + 1,
                EXISTS
                (
                    SELECT 1 FROM cortex_data.knowledge_revision
                    WHERE tenant_id = @tenant_id AND namespace_name = @namespace
                      AND record_id = @withdrawn_record_id
                      AND revision_id = @withdrawn_revision_id
                      AND record_status = 'active'
                ),
                EXISTS
                (
                    SELECT 1 FROM cortex_data.knowledge_revision
                    WHERE tenant_id = @tenant_id AND namespace_name = @namespace
                      AND record_id = @withdrawn_record_id
                      AND revision_id = 'krev_' || @withdrawal_hash
                      AND record_status = 'tombstone'
                ),
                EXISTS
                (
                    SELECT 1 FROM cortex_data.knowledge_link
                    WHERE tenant_id = @tenant_id AND namespace_name = @namespace
                      AND record_id = @withdrawn_record_id
                      AND revision_id = 'krev_' || @withdrawal_hash
                      AND link_kind = 'tombstones'
                      AND target_record_id = @withdrawn_record_id
                      AND target_revision_id = @withdrawn_revision_id
                ),
                (SELECT count(*) FROM cortex_data.knowledge_publication_change
                 WHERE tenant_id = @tenant_id AND namespace_name = @namespace
                   AND target_record_id = @superseded_record_id) = 1,
                (SELECT generation FROM cortex_data.knowledge_head
                 WHERE tenant_id = @tenant_id AND namespace_name = @namespace
                   AND record_id = @superseded_record_id) = @superseded_generation + 1,
                EXISTS
                (
                    SELECT 1 FROM cortex_data.knowledge_revision
                    WHERE tenant_id = @tenant_id AND namespace_name = @namespace
                      AND record_id = @superseded_record_id
                      AND revision_id = @superseded_revision_id
                      AND record_status = 'active'
                ),
                EXISTS
                (
                    SELECT 1 FROM cortex_data.knowledge_revision
                    WHERE tenant_id = @tenant_id AND namespace_name = @namespace
                      AND record_id = @superseded_record_id
                      AND revision_id = 'krev_' || @supersede_hash
                      AND record_status = 'superseded'
                ),
                EXISTS
                (
                    SELECT 1 FROM cortex_data.knowledge_link
                    WHERE tenant_id = @tenant_id AND namespace_name = @namespace
                      AND record_id = @superseded_record_id
                      AND revision_id = 'krev_' || @supersede_hash
                      AND link_kind = 'tombstones'
                      AND target_record_id = @superseded_record_id
                      AND target_revision_id = @superseded_revision_id
                ),
                EXISTS
                (
                    SELECT 1 FROM cortex_data.knowledge_link
                    WHERE tenant_id = @tenant_id AND namespace_name = @namespace
                      AND record_id = @replacement_record_id
                      AND revision_id = @replacement_revision_id
                      AND link_kind = 'supersedes'
                      AND target_record_id = @superseded_record_id
                      AND target_revision_id = @superseded_revision_id
                      AND target_content_hash = @superseded_content_hash
                ),
                EXISTS
                (
                    SELECT 1 FROM cortex_data.knowledge_head
                    WHERE tenant_id = @tenant_id AND namespace_name = @namespace
                      AND record_id = @replacement_record_id
                      AND current_revision_id = @replacement_revision_id
                      AND generation = @replacement_generation
                );
            """,
            connection);
        command.Parameters.AddWithValue("tenant_id", TenantId);
        command.Parameters.AddWithValue("namespace", Namespace);
        command.Parameters.AddWithValue("withdrawn_record_id", withdrawn.RecordId);
        command.Parameters.AddWithValue("withdrawn_generation", withdrawn.Generation);
        command.Parameters.AddWithValue("withdrawn_revision_id", withdrawn.RevisionId);
        command.Parameters.AddWithValue("withdrawal_hash", withdrawal.ChangeHash);
        command.Parameters.AddWithValue("superseded_record_id", superseded.RecordId);
        command.Parameters.AddWithValue("superseded_generation", superseded.Generation);
        command.Parameters.AddWithValue("superseded_revision_id", superseded.RevisionId);
        command.Parameters.AddWithValue("superseded_content_hash", superseded.ContentHash);
        command.Parameters.AddWithValue("supersede_hash", supersede.ChangeHash);
        command.Parameters.AddWithValue("replacement_record_id", replacement.RecordId);
        command.Parameters.AddWithValue("replacement_revision_id", replacement.RevisionId);
        command.Parameters.AddWithValue("replacement_generation", replacement.Generation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        FixtureAssert.True(
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
            Enumerable.Range(0, 14).All(reader.GetBoolean) &&
            !await reader.ReadAsync(cancellationToken).ConfigureAwait(false),
            "The native publication-change evidence did not preserve the exact immutable/head contract.");
    }

    private static LearningFeedbackAuthorityContext AuthorAuthority() => new(
        TenantId,
        AuthorAuthorityId,
        AuthorSubjectId,
        AuthorServiceId,
        PolicyRevision,
        [new LearningFeedbackNamespaceGrant(Namespace, CanSubmit: true)],
        new HashSet<string>(["fixture-screening"], StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fixture-screening-decision"] = new string('b', 64)
        });

    private static LearningFeedbackNamespaceAccess Access(
        string authorityId,
        string subjectId,
        string serviceId,
        LearningFeedbackNamespaceCapability capability) => new(
            TenantId,
            Namespace,
            authorityId,
            subjectId,
            serviceId,
            PolicyRevision,
            capability);

    private sealed record PublishedHead(
        string RecordId,
        string RevisionId,
        long Generation,
        string ContentHash);

    private sealed record CapturedChange(
        LearningFeedbackPublicationChangeV1 Change,
        LearningFeedbackPublicationChangeWriteResult? Result,
        PostgreSqlPersistenceException? Error);
}
