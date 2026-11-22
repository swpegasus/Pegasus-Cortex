using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Continuity;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Knowledge;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Runtime;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

internal sealed record CortexLocalCurrentSnapshotResult(
    string ContractId,
    bool Accepted,
    string Disposition,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    string AuthoritySetHash,
    long CurrentRevision,
    long RetainedRevision,
    string SnapshotHash,
    string ContextSetId,
    string? ProjectBriefDisposition,
    string? ProjectBriefContextSetId,
    string? ProjectionIssueCode,
    string SnapshotCanonicalBase64);

/// <summary>
/// Explicit local producer for the narrow current-recovery chain. It verifies
/// CURRENT.json and its authority files, resolves all scope from the protected
/// registry, appends at most one immutable revision, and repairs the derived
/// brief through the normal guarded local runtime.
/// </summary>
internal static class CortexLocalCurrentSnapshotCommand
{
    private const string ResultContractId =
        "pegasus.cortex.local-current-materialization-result/1.0";
    private static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromHours(1);

    public static async Task<int> RunAsync(string[] args, TextWriter diagnostics)
    {
        CortexLocalCurrentSnapshotArguments options;
        try
        {
            options = CortexLocalCurrentSnapshotArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await WriteDiagnosticAsync(
                diagnostics,
                "cortex_local_current_arguments_invalid",
                exception).ConfigureAwait(false);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var result = await MaterializeAsync(options, cancellation.Token)
                .ConfigureAwait(false);
            var bytes = CortexCanonicalJson.Serialize(result);
            var output = Console.OpenStandardOutput();
            await output.WriteAsync(bytes, cancellation.Token).ConfigureAwait(false);
            await output.WriteAsync("\n"u8.ToArray(), cancellation.Token)
                .ConfigureAwait(false);
            await output.FlushAsync(cancellation.Token).ConfigureAwait(false);
            return result.Accepted ? 0 : 3;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception)
        {
            await WriteDiagnosticAsync(
                diagnostics,
                "cortex_local_current_materialization_failed",
                exception).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static async ValueTask<CortexLocalCurrentSnapshotResult> MaterializeAsync(
        CortexLocalCurrentSnapshotArguments options,
        CancellationToken cancellationToken,
        bool requireVerifiedProjectBrief = false)
    {
        return await MaterializeCoreAsync(
            options,
            currentCandidate: null,
            cancellationToken,
            requireVerifiedProjectBrief).ConfigureAwait(false);
    }

    /// <summary>
    /// Materializes a fully staged local projection without requiring it to be
    /// published as CURRENT.json first. The continuity-refresh command commits
    /// the candidate file only after this retained snapshot and brief succeed.
    /// </summary>
    internal static async ValueTask<CortexLocalCurrentSnapshotResult>
        MaterializeCandidateAsync(
        CortexLocalCurrentSnapshotArguments options,
        CortexCurrentProjection currentCandidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentCandidate);
        return await MaterializeCoreAsync(
            options,
            currentCandidate,
            cancellationToken,
            requireVerifiedProjectBrief: true).ConfigureAwait(false);
    }

    private static async ValueTask<CortexLocalCurrentSnapshotResult> MaterializeCoreAsync(
        CortexLocalCurrentSnapshotArguments options,
        CortexCurrentProjection? currentCandidate,
        CancellationToken cancellationToken,
        bool requireVerifiedProjectBrief)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var composition = await CortexMcpLocalComposition.CreateAsync(
            options.ProfileId,
            options.StatePaths.DatabasePath,
            options.StatePaths.RegistryPath,
            "operator",
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);
        if (!composition.Registry.TryAuthorizeProjectBriefRead(
                options.ProjectLinkId,
                out var registration) ||
            registration is null || !registration.AllowWrite)
        {
            throw new UnauthorizedAccessException(
                "The protected profile does not authorize current-recovery writes.");
        }

        var current = currentCandidate ??
            await CortexCurrentProjectionReader.ReadAsync(
                registration.RootPath,
                options.CapturedAt,
                maximumAge: null,
                cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                current.ProjectLinkId,
                registration.ProjectLinkId,
                StringComparison.Ordinal) ||
            !string.Equals(current.ProjectId, registration.ProjectId, StringComparison.Ordinal) ||
            currentCandidate is not null &&
            (current.GeneratedAt != options.CapturedAt ||
             !string.Equals(
                 CortexProjectBriefService.ComputeAuthoritySetHash(
                     current.SourceRevisions),
                 current.AuthoritySetHash,
                 StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The verified projection does not match its authority or protected registry scope.");
        }

        // Current-recovery chains remain immutable evidence after their hot
        // retention deadline. Use the integrity head view rather than the
        // expiry-filtered hot read, or a valid expired chain would be mistaken
        // for an empty profile and revision one would conflict.
        var head = await FindCurrentRecoveryHeadAsync(
            composition.Store,
            current.ProjectId,
            current.ProjectLinkId,
            current.SourceRevisions.Select(item => item.SourceId).ToArray(),
            cancellationToken).ConfigureAwait(false);
        AgentTaskSnapshotV1 snapshot;
        string disposition;
        if (head is null)
        {
            snapshot = CortexCurrentTaskSnapshotFactory.Create(
                current,
                retainedRevision: 1,
                previousSnapshotHash: null,
                options.CapturedAt,
                Producer(),
                CortexCurrentTaskSnapshotFactory.TaskSetId);
            disposition = CortexRetentionWriteDispositions.Appended;
        }
        else
        {
            var existing = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
                head.CanonicalUtf8);
            if (!AgentTaskSnapshotPolicy.HasValidHash(existing) ||
                !string.Equals(
                    CortexCanonicalJson.Sha256(head.CanonicalUtf8),
                    head.ContentHash,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    existing.ProjectLinkId,
                    registration.ProjectLinkId,
                    StringComparison.Ordinal) ||
                options.CapturedAt < existing.CapturedAt)
            {
                throw new InvalidDataException(
                    "The local current-recovery head failed its integrity or time boundary.");
            }
            var sourceHashes = current.SourceRevisions.ToDictionary(
                item => item.SourceId,
                item => item.Sha256,
                StringComparer.Ordinal);
            var sameAuthority = AgentTaskSnapshotPolicy.SourceHashesMatch(
                existing,
                sourceHashes);
            if (sameAuthority &&
                existing.ExpiresAt - options.CapturedAt > RefreshBeforeExpiry)
            {
                // Re-submit exact bytes so an absent derived brief is repaired
                // without extending or relabeling the immutable source chain.
                snapshot = existing;
                disposition = CortexRetentionWriteDispositions.Idempotent;
            }
            else
            {
                snapshot = CortexCurrentTaskSnapshotFactory.Create(
                    current,
                    existing.SnapshotRevision + 1,
                    existing.SnapshotHash,
                    options.CapturedAt,
                    Producer(),
                    existing.TaskSetId);
                disposition = CortexRetentionWriteDispositions.Appended;
            }
        }

        var write = await composition.Runtime.SubmitTaskSnapshotAsync(
            registration.ProjectLinkId,
            registration.ProjectNamespace,
            snapshot,
            cancellationToken).ConfigureAwait(false) ??
            throw new UnauthorizedAccessException(
                "The local runtime rejected protected current-recovery scope.");
        var accepted = write.Disposition is
            CortexRetentionWriteDispositions.Appended or
            CortexRetentionWriteDispositions.Idempotent;
        if (!accepted)
        {
            disposition = write.Disposition;
        }
        else if (!string.Equals(write.Disposition, disposition, StringComparison.Ordinal))
        {
            // Repository truth wins if a concurrent exact writer completed
            // the same deterministic revision first.
            disposition = write.Disposition;
        }

        if (accepted && requireVerifiedProjectBrief)
        {
            await RequireVerifiedProjectBriefAsync(
                composition,
                registration,
                snapshot,
                current.AuthoritySetHash,
                write,
                cancellationToken).ConfigureAwait(false);
        }

        var canonical = CortexCanonicalJson.Serialize(snapshot);
        return new CortexLocalCurrentSnapshotResult(
            ResultContractId,
            accepted,
            disposition,
            registration.ProjectLinkId,
            registration.ProjectId,
            registration.ProjectNamespace,
            current.AuthoritySetHash,
            current.AuthorityRevision,
            snapshot.SnapshotRevision,
            snapshot.SnapshotHash,
            snapshot.ContextSetId,
            write.ProjectBriefDisposition,
            write.ProjectBriefContextSetId,
            write.ProjectionIssueCode,
            Convert.ToBase64String(canonical));
    }

    private static async ValueTask RequireVerifiedProjectBriefAsync(
        CortexMcpLocalComposition composition,
        CortexLocalProjectRegistration registration,
        AgentTaskSnapshotV1 snapshot,
        string authoritySetHash,
        CortexRetainedAssertionWriteResult write,
        CancellationToken cancellationToken)
    {
        var identity = CortexLocalOsIdentity.Capture();
        var scope = new CortexProjectBriefReadScope(
            registration.TenantId,
            registration.ProjectLinkId,
            registration.ProjectId,
            registration.ProjectNamespace,
            "local-os-policy",
            $"os_{identity.Hash}",
            "pegasus-cortex-local",
            PolicyRevision: 1);
        var brief = await composition.ProjectBriefStore.FindCurrentAsync(
            scope,
            cancellationToken).ConfigureAwait(false);
        var retainedAuthoritySetHash =
            CortexProjectBriefService.ComputeAuthoritySetHash(snapshot.SourceRevisions);
        if (!string.Equals(
                authoritySetHash,
                retainedAuthoritySetHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The SQLite retained snapshot authority does not match CURRENT.json.");
        }
        if (write.ProjectionIssueCode is not null ||
            write.ProjectBriefDisposition is null ||
            write.ProjectBriefContextSetId is null)
        {
            throw new InvalidDataException(
                "The SQLite project-brief materializer did not report a complete projection.");
        }
        if (brief is null)
            throw new InvalidDataException("The SQLite project brief did not read back.");
        if (!string.Equals(
                brief.AuthoritySetHash,
                authoritySetHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The SQLite project brief authority does not match CURRENT.json.");
        }
        if (!string.Equals(brief.ProjectLinkId, registration.ProjectLinkId, StringComparison.Ordinal) ||
            !string.Equals(brief.ProjectId, registration.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(
                brief.ProjectNamespace,
                registration.ProjectNamespace,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The SQLite project brief does not match protected registry scope.");
        }
        if (brief.StateRevision != snapshot.SnapshotRevision ||
            !string.Equals(brief.SourceHash, snapshot.SnapshotHash, StringComparison.Ordinal) ||
            !string.Equals(
                write.ProjectBriefContextSetId,
                brief.ContextSetId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The SQLite project brief does not match the retained snapshot head.");
        }
    }

    private static AgentWorkOwnerV1 Producer()
    {
        var identity = CortexLocalOsIdentity.Capture();
        return new AgentWorkOwnerV1(
            "local-os-policy",
            $"os_{identity.Hash}",
            "pegasus-cortex-sync-client");
    }

    private static async ValueTask<CortexRetainedDocument?> FindCurrentRecoveryHeadAsync(
        ICortexAgentRetentionRepository repository,
        string projectId,
        string projectLinkId,
        IReadOnlyCollection<string> authoritySourceIds,
        CancellationToken cancellationToken)
    {
        var candidates = new List<CortexRetainedDocument>();
        string? after = null;
        do
        {
            var page = await repository.ListAllHeadsByKindAsync(
                projectId,
                CortexRetainedDocumentKinds.TaskSnapshot,
                after,
                CortexAgentRetentionLimits.MaximumKindHeadPageSize,
                cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                var snapshot = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
                    item.CanonicalUtf8);
                if (!AgentTaskSnapshotPolicy.HasValidHash(snapshot) ||
                    !string.Equals(
                        CortexCanonicalJson.Sha256(item.CanonicalUtf8),
                        item.ContentHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "A retained task head failed its immutable integrity boundary.");
                }
                var sourceIds = snapshot.SourceRevisions
                    .Select(source => source.SourceId)
                    .ToHashSet(StringComparer.Ordinal);
                if (string.Equals(snapshot.ProjectLinkId, projectLinkId, StringComparison.Ordinal) &&
                    string.Equals(snapshot.ProjectId, projectId, StringComparison.Ordinal) &&
                    string.Equals(snapshot.Mode, AgentCoordinationModeIds.Standalone, StringComparison.Ordinal) &&
                    snapshot.Tasks.Count == 1 &&
                    sourceIds.SetEquals(authoritySourceIds))
                {
                    candidates.Add(item.Snapshot());
                }
            }
            after = page.NextAfterDocumentId;
        }
        while (after is not null);
        return candidates.Count switch
        {
            0 => null,
            1 => candidates[0],
            _ => throw new InvalidDataException(
                "More than one retained chain claims the narrow current-recovery authority set.")
        };
    }

    private static async ValueTask WriteDiagnosticAsync(
        TextWriter diagnostics,
        string code,
        Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length > 512)
            message = message[..512];
        await diagnostics.WriteLineAsync(
            $"severity=error operation=current.materialize code={code} message=cause={exception.GetType().Name}: {message}")
            .ConfigureAwait(false);
        await diagnostics.FlushAsync().ConfigureAwait(false);
    }
}
