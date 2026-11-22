using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Runtime;
using System.Text.Json;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

internal sealed record CortexLocalProjectTransferExportResult(
    string ContractId,
    bool Accepted,
    string Disposition,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    string AuthoritySetHash,
    string BundleId,
    string BundleHash,
    int AuthoritativeDocumentCount,
    int DerivedBriefEvidenceCount,
    long CanonicalBytes,
    string OutputRelativePath);

/// <summary>
/// Produces one self-contained provider-neutral canonical transfer document.
/// It first materializes the verified CURRENT projection, then exports exact
/// complete retained chains. SQLite files, WAL files, registry bytes, paths,
/// ACLs, and OS identity never enter the bundle.
/// </summary>
internal static class CortexLocalProjectTransferExportCommand
{
    private const string ResultContractId =
        "pegasus.cortex.local-project-transfer-export-result/1.0";

    public static async Task<int> RunAsync(string[] args, TextWriter diagnostics)
    {
        CortexLocalProjectTransferExportArguments options;
        try
        {
            options = CortexLocalProjectTransferExportArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await DiagnosticAsync(diagnostics, "cortex_transfer_export_arguments_invalid", exception)
                .ConfigureAwait(false);
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
            var result = await ExportAsync(options, cancellation.Token)
                .ConfigureAwait(false);
            var output = Console.OpenStandardOutput();
            await output.WriteAsync(
                CortexCanonicalJson.Serialize(result),
                cancellation.Token).ConfigureAwait(false);
            await output.WriteAsync("\n"u8.ToArray(), cancellation.Token)
                .ConfigureAwait(false);
            await output.FlushAsync(cancellation.Token).ConfigureAwait(false);
            return result.Accepted ? 0 : 3;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 130;
        }
        catch (CortexRetentionTransferValidationException exception)
        {
            await DiagnosticAsync(diagnostics, exception.Code, exception)
                .ConfigureAwait(false);
            return 3;
        }
        catch (Exception exception)
        {
            await DiagnosticAsync(diagnostics, "cortex_transfer_export_failed", exception)
                .ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static async ValueTask<CortexLocalProjectTransferExportResult> ExportAsync(
        CortexLocalProjectTransferExportArguments options,
        CancellationToken cancellationToken)
    {
        var materialized = await CortexLocalCurrentSnapshotCommand.MaterializeAsync(
            new CortexLocalCurrentSnapshotArguments(
                options.ProfileId,
                options.StatePaths,
                options.ProjectLinkId,
                options.CapturedAt),
            cancellationToken).ConfigureAwait(false);
        if (!materialized.Accepted)
            throw new InvalidDataException("The current-recovery snapshot could not be materialized before export.");

        await using var composition = await CortexMcpLocalComposition.CreateAsync(
            options.ProfileId,
            options.StatePaths.DatabasePath,
            options.StatePaths.RegistryPath,
            "operator",
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);
        if (!composition.Registry.TryAuthorizeRecoveryExport(
                options.ProjectLinkId,
                out var registration) || registration is null)
        {
            throw new UnauthorizedAccessException("The protected profile does not authorize transfer export.");
        }
        var destination = CortexLocalRecoveryExportPathPolicy.RequireDestinationRoot(
            options.DestinationRoot,
            registration.RootPath,
            options.StatePaths.StateRoot);
        var finalRoot = CortexLocalProjectTransferPathPolicy.OutputRoot(destination, options.RunId);
        var stagingRoot = CortexLocalProjectTransferPathPolicy.StagingRoot(destination, options.RunId);
        if (Directory.Exists(finalRoot) || File.Exists(finalRoot))
            throw new IOException("The transfer output already exists.");

        var authoritative = await ReadAuthoritativeAsync(
            composition.Store,
            registration.ProjectId,
            cancellationToken).ConfigureAwait(false);
        var derived = await ReadBriefEvidenceAsync(
            composition.Store,
            registration.ProjectLinkId,
            registration.ProjectNamespace,
            cancellationToken).ConfigureAwait(false);
        var latestTask = authoritative
            .Where(item => item.Kind == CortexRetainedDocumentKinds.TaskSnapshot)
            .OrderByDescending(item => item.Revision)
            .FirstOrDefault() ??
            throw new InvalidDataException("A transfer requires one complete task-snapshot chain.");
        var latestSnapshot = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
            latestTask.CanonicalUtf8);
        var authorityHash = CortexProjectBriefService.ComputeAuthoritySetHash(
            latestSnapshot.SourceRevisions);
        if (!string.Equals(
                authorityHash,
                materialized.AuthoritySetHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The transfer head does not match the materialized project authority.");
        }
        var sourceStore = await ReadSourceStoreAsync(
            registration.RootPath,
            registration.ProjectLinkId,
            cancellationToken).ConfigureAwait(false);

        var bundleId = $"transfer-{CortexCanonicalJson.Sha256(options.RunId)[..32]}";
        var evidenceHashValues = new List<string>
        {
            composition.Registry.Document.RegistryHash,
            latestTask.ContentHash
        };
        evidenceHashValues.AddRange(derived.Select(item => item.ContentHash));
        var evidenceHashes = evidenceHashValues
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var bundle = CortexRetentionTransferBundleFactory.Create(
            bundleId,
            options.CapturedAt,
            new CortexRetentionTransferProjectScope(
                registration.TenantId,
                registration.ProjectLinkId,
                registration.ProjectId,
                registration.ProjectNamespace,
                authorityHash),
            new CortexRetentionTransferProvenance(
                sourceStore.StoreId,
                sourceStore.StoreEpoch,
                "pegasus-cortex-local-export",
                evidenceHashes),
            authoritative,
            derived);
        var canonical = CortexCanonicalJson.Serialize(bundle);

        var created = false;
        try
        {
            Directory.CreateDirectory(stagingRoot);
            created = true;
            CortexLocalRegistryAclPolicy.ProtectDirectory(stagingRoot);
            CortexLocalRegistryAclPolicy.RequireProtectedDirectory(stagingRoot);
            await CortexLocalRecoveryExportPathPolicy.WriteNewProtectedAsync(
                stagingRoot,
                "bundle.json",
                canonical,
                cancellationToken).ConfigureAwait(false);
            Directory.Move(stagingRoot, finalRoot);
            created = false;
            CortexLocalRegistryAclPolicy.RequireProtectedDirectory(finalRoot);
        }
        finally
        {
            if (created && Directory.Exists(stagingRoot))
            {
                CortexLocalRecoveryExportPathPolicy.DeleteVerifiedTree(
                    destination,
                    stagingRoot);
            }
        }

        return new CortexLocalProjectTransferExportResult(
            ResultContractId,
            Accepted: true,
            Disposition: "exported",
            registration.ProjectLinkId,
            registration.ProjectId,
            registration.ProjectNamespace,
            authorityHash,
            bundle.BundleId,
            bundle.BundleHash,
            bundle.AuthoritativeDocuments.Count,
            bundle.DerivedBriefEvidence.Count,
            canonical.Length,
            Path.GetFileName(finalRoot));
    }

    private static async ValueTask<CortexLocalTransferSourceStore> ReadSourceStoreAsync(
        string projectRoot,
        string expectedProjectLinkId,
        CancellationToken cancellationToken)
    {
        var metaPath = Path.Combine(projectRoot, ".cortex", "meta.json");
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(metaPath);
        var info = new FileInfo(metaPath);
        if (!info.Exists || info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.Length is < 2 or > 64 * 1024)
        {
            throw new InvalidDataException("The project correlation metadata is missing or unsafe.");
        }
        var length = info.Length;
        var lastWrite = info.LastWriteTimeUtc;
        var bytes = await File.ReadAllBytesAsync(metaPath, cancellationToken)
            .ConfigureAwait(false);
        info.Refresh();
        if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != lastWrite)
            throw new IOException("The project correlation metadata changed during transfer capture.");

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            var correlation = document.RootElement.GetProperty("cortexCorrelation");
            var projectLinkId = correlation.GetProperty("projectLinkId").GetString();
            var storeId = correlation.GetProperty("storeId").GetString();
            var storeEpoch = correlation.GetProperty("storeEpoch").GetInt64();
            if (!string.Equals(projectLinkId, expectedProjectLinkId, StringComparison.Ordinal) ||
                storeId is not { Length: > 0 and <= 128 } ||
                !char.IsAsciiLetterOrDigit(storeId[0]) ||
                storeId.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')) ||
                storeEpoch < 1)
            {
                throw new InvalidDataException("The project correlation store identity is invalid.");
            }
            return new CortexLocalTransferSourceStore(storeId, storeEpoch);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or KeyNotFoundException or
                FormatException or ArgumentException)
        {
            throw new InvalidDataException(
                "The project correlation metadata cannot supply a valid transfer store identity.",
                exception);
        }
    }

    private static async ValueTask<IReadOnlyList<CortexRetainedDocument>>
        ReadAuthoritativeAsync(
            ICortexAgentRetentionRepository repository,
            string projectId,
            CancellationToken cancellationToken)
    {
        var results = new List<CortexRetainedDocument>();
        foreach (var kind in CortexRetentionTransferBundlePolicy.AuthoritativeKinds)
        {
            foreach (var head in await ReadHeadsAsync(
                         repository,
                         projectId,
                         kind,
                         cancellationToken).ConfigureAwait(false))
            {
                await foreach (var revision in repository.ReadRevisionRangeAsync(
                                   projectId,
                                   kind,
                                   head.DocumentId,
                                   1,
                                   head.Revision,
                                   cancellationToken).ConfigureAwait(false))
                {
                    results.Add(revision.Snapshot());
                }
            }
        }
        return results.AsReadOnly();
    }

    private static async ValueTask<IReadOnlyList<CortexRetainedDocument>>
        ReadBriefEvidenceAsync(
            ICortexAgentRetentionRepository repository,
            string projectLinkId,
            string projectNamespace,
            CancellationToken cancellationToken)
    {
        var heads = await ReadHeadsAsync(
            repository,
            projectLinkId,
            CortexRetainedDocumentKinds.ProjectBrief,
            cancellationToken).ConfigureAwait(false);
        var head = heads.SingleOrDefault(item =>
            string.Equals(item.DocumentId, projectNamespace, StringComparison.Ordinal));
        if (head is null)
            return [];
        var results = new List<CortexRetainedDocument>();
        await foreach (var revision in repository.ReadRevisionRangeAsync(
                           projectLinkId,
                           CortexRetainedDocumentKinds.ProjectBrief,
                           projectNamespace,
                           1,
                           head.Revision,
                           cancellationToken).ConfigureAwait(false))
        {
            results.Add(revision.Snapshot());
        }
        return results.AsReadOnly();
    }

    private static async ValueTask<IReadOnlyList<CortexRetainedDocument>> ReadHeadsAsync(
        ICortexAgentRetentionRepository repository,
        string projectId,
        string kind,
        CancellationToken cancellationToken)
    {
        var results = new List<CortexRetainedDocument>();
        string? cursor = null;
        do
        {
            var page = await repository.ListAllHeadsByKindAsync(
                projectId,
                kind,
                cursor,
                CortexAgentRetentionLimits.MaximumKindHeadPageSize,
                cancellationToken).ConfigureAwait(false);
            results.AddRange(page.Items.Select(item => item.Snapshot()));
            if (page.NextAfterDocumentId is not null &&
                (page.Items.Count == 0 ||
                 string.Equals(cursor, page.NextAfterDocumentId, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("The retained-head cursor did not advance.");
            }
            cursor = page.NextAfterDocumentId;
        }
        while (cursor is not null);
        return results.AsReadOnly();
    }

    private static async ValueTask DiagnosticAsync(
        TextWriter diagnostics,
        string code,
        Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length > 512)
            message = message[..512];
        await diagnostics.WriteLineAsync(
            $"severity=error operation=transfer.export code={code} message=cause={exception.GetType().Name}: {message}")
            .ConfigureAwait(false);
        await diagnostics.FlushAsync().ConfigureAwait(false);
    }
}

internal sealed record CortexLocalTransferSourceStore(
    string StoreId,
    long StoreEpoch);

internal static class CortexLocalProjectTransferPathPolicy
{
    public static string OutputRoot(string destinationRoot, string runId)
    {
        var path = Path.Combine(
            destinationRoot,
            $"transfer-{CortexCanonicalJson.Sha256(runId)[..24]}");
        CortexLocalRecoveryExportPathPolicy.RequireOutputPath(destinationRoot, path);
        return path;
    }

    public static string StagingRoot(string destinationRoot, string runId)
    {
        var path = Path.Combine(
            destinationRoot,
            $".transfer-{CortexCanonicalJson.Sha256(runId)[..16]}-{Guid.NewGuid():N}");
        CortexLocalRecoveryExportPathPolicy.RequireOutputPath(destinationRoot, path);
        return path;
    }
}
