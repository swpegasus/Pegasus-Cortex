using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Continuity;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Mcp.Local.Configuration;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

/// <summary>
/// Explicit network-independent continuity refresh operator. One-shot performs
/// one cycle; watch remains in this foreground process and delegates every
/// mutation to <see cref="RefreshOnceAsync"/>.
/// </summary>
internal static class CortexLocalContinuityRefreshCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter diagnostics)
    {
        CortexLocalContinuityRefreshArguments options;
        try
        {
            options = CortexLocalContinuityRefreshArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await WriteDiagnosticAsync(
                diagnostics,
                "cortex_local_continuity_refresh_arguments_invalid",
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
            var output = Console.OpenStandardOutput();
            if (options.Mode == CortexLocalContinuityRefreshMode.Once)
            {
                var result = await RefreshOnceAsync(options, cancellation.Token)
                    .ConfigureAwait(false);
                await WriteResultAsync(output, result, cancellation.Token)
                    .ConfigureAwait(false);
                return result.Accepted ? 0 : 3;
            }

            var projectRoot = await ResolveProjectRootAsync(
                options.Current,
                cancellation.Token).ConfigureAwait(false);
            await using var signals =
                new FileSystemCortexLocalContinuityChangeSignalSource(projectRoot);
            await CortexLocalContinuityWatch.RunAsync(
                options,
                projectRoot,
                signals,
                TimeProvider.System,
                (result, token) => WriteResultAsync(output, result, token),
                (exception, _) => WriteDiagnosticAsync(
                    diagnostics,
                    "cortex_local_continuity_watch_cycle_failed",
                    exception),
                cancellation.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception)
        {
            await WriteDiagnosticAsync(
                diagnostics,
                "cortex_local_continuity_refresh_failed",
                exception).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static async ValueTask<CortexLocalCurrentSnapshotResult> RefreshOnceAsync(
        CortexLocalContinuityRefreshArguments options,
        CancellationToken cancellationToken,
        CortexLocalContinuityMaterializer? materializer = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var currentOptions = options.Current;
        var registry = await CortexLocalProjectRegistry.LoadAsync(
            currentOptions.StatePaths.RegistryPath,
            currentOptions.ProfileId,
            cancellationToken).ConfigureAwait(false);
        if (!registry.TryAuthorizeProjectBriefRead(
                currentOptions.ProjectLinkId,
                out var registration) ||
            registration is null || !registration.AllowWrite)
        {
            throw new UnauthorizedAccessException(
                "The protected profile does not authorize continuity refresh writes.");
        }

        var candidate = await CortexLocalCurrentProjectionRefresher.StageAsync(
            registration.RootPath,
            currentOptions.CapturedAt,
            cancellationToken).ConfigureAwait(false);
        materializer ??= static (currentOptions, current, token) =>
            CortexLocalCurrentSnapshotCommand.MaterializeCandidateAsync(
                currentOptions,
                current,
                token);
        var materialized = await materializer(
            currentOptions,
            candidate.Projection,
            cancellationToken).ConfigureAwait(false);
        var retainedBytes = Convert.FromBase64String(
            materialized.SnapshotCanonicalBase64);
        var retained = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(retainedBytes);
        var retainedAuthoritySetHash =
            CortexProjectBriefService.ComputeAuthoritySetHash(retained.SourceRevisions);
        if (!string.Equals(
                candidate.Projection.AuthoritySetHash,
                materialized.AuthoritySetHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.Projection.AuthoritySetHash,
                retainedAuthoritySetHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                retained.ContextSetId,
                $"ctx_{candidate.Projection.AuthoritySetHash}",
                StringComparison.Ordinal) ||
            !materialized.Accepted ||
            materialized.ProjectBriefContextSetId is null ||
            materialized.ProjectionIssueCode is not null)
        {
            throw new InvalidDataException(
                "CURRENT.json, retained snapshot, and project brief authority did not match.");
        }

        // CURRENT.json is the final commit point. A materialization or brief
        // failure above leaves the exact prior file in place.
        var projection = await CortexLocalCurrentProjectionRefresher.CommitAndVerifyAsync(
            candidate,
            cancellationToken).ConfigureAwait(false);
        if (projection.AuthorityRevision != materialized.CurrentRevision)
        {
            throw new InvalidDataException(
                "The committed CURRENT.json revision does not match retained state.");
        }
        return materialized;
    }

    internal static async ValueTask<string> ResolveProjectRootAsync(
        CortexLocalCurrentSnapshotArguments options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var registry = await CortexLocalProjectRegistry.LoadAsync(
            options.StatePaths.RegistryPath,
            options.ProfileId,
            cancellationToken).ConfigureAwait(false);
        if (!registry.TryAuthorizeProjectBriefRead(
                options.ProjectLinkId,
                out var registration) ||
            registration is null || !registration.AllowWrite)
        {
            throw new UnauthorizedAccessException(
                "The protected profile does not authorize continuity refresh writes.");
        }
        return registration.RootPath;
    }

    private static async ValueTask WriteResultAsync(
        Stream output,
        CortexLocalCurrentSnapshotResult result,
        CancellationToken cancellationToken)
    {
        var bytes = CortexCanonicalJson.Serialize(result);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync("\n"u8.ToArray(), cancellationToken)
            .ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
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
            $"severity=error operation=continuity.refresh code={code} message=cause={exception.GetType().Name}: {message}")
            .ConfigureAwait(false);
        await diagnostics.FlushAsync().ConfigureAwait(false);
    }
}

internal delegate ValueTask<CortexLocalCurrentSnapshotResult>
    CortexLocalContinuityMaterializer(
    CortexLocalCurrentSnapshotArguments options,
    CortexCurrentProjection current,
    CancellationToken cancellationToken);
