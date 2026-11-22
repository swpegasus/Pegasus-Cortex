using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Integrated.Runtime;
using Pegasus.Cortex.Mcp.Integrated.Security;
using Pegasus.Cortex.Persistence.PostgreSql.Retention;

namespace Pegasus.Cortex.Mcp.Integrated.Recovery;

internal sealed record CortexIntegratedProjectTransferImportResult(
    string ContractId,
    bool Accepted,
    string Disposition,
    string BundleId,
    string BundleHash,
    string BundleFileSha256,
    string TenantId,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    int DocumentCount,
    int AppendedCount,
    int ExistingCount,
    CortexIntegratedTransferBriefReadback? BriefReadback,
    string? DiagnosticCode);

/// <summary>
/// Explicit one-shot PostgreSQL transfer import. This command is selected only
/// by a literal executable argument, is not an MCP method, and uses the normal
/// restricted runtime connection after migrations have been completed.
/// </summary>
internal static class CortexIntegratedProjectTransferImportCommand
{
    private const string ResultContractId =
        "pegasus.cortex.integrated-project-transfer-import-result/1.0";

    public static async Task<int> RunAsync(string[] args, TextWriter diagnostics)
    {
        CortexIntegratedProjectTransferImportArguments options;
        try
        {
            options = CortexIntegratedProjectTransferImportArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await DiagnosticAsync(
                diagnostics,
                "cortex_transfer_import_arguments_invalid",
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
            var result = await ImportAsync(options, cancellation.Token)
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
        catch (UnauthorizedAccessException exception)
        {
            await DiagnosticAsync(
                diagnostics,
                "cortex_transfer_import_not_authorized",
                exception).ConfigureAwait(false);
            return 3;
        }
        catch (Exception exception)
        {
            await DiagnosticAsync(
                diagnostics,
                "cortex_transfer_import_failed",
                exception).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static async ValueTask<CortexIntegratedProjectTransferImportResult> ImportAsync(
        CortexIntegratedProjectTransferImportArguments options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var source = CortexIntegratedProjectTransferBundleReader.Read(
            options.BundlePath,
            options.ExpectedBundleFileSha256);

        var host = await CortexMcpIntegratedHost.TryCreateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!host.Available)
        {
            throw new InvalidOperationException(
                host.IssueCode ?? "cortex_hybrid_not_ready");
        }

        await using var runtime = host.Runtime!;
        var authority = host.Authority!;
        RequireImportCapabilities(authority);
        var postgreSql = runtime.PostgreSqlComposition;
        var links = postgreSql.CreateProjectLinkRepository();
        var link = await AuthorizeProjectAsync(
                options,
                authority,
                links,
                cancellationToken)
            .ConfigureAwait(false);
        var projectScope = new CortexProjectBriefReadScope(
            authority.TenantId,
            link.Metadata.ProjectLinkId,
            link.Metadata.ProjectId,
            link.Metadata.ProjectNamespace,
            "local-cortex-policy",
            authority.SubjectId,
            "pegasus-cortex-transfer-import",
            authority.PolicyRevision);
        var retentionScope = new PostgreSqlCortexRetentionScope(
            authority.TenantId,
            link.Metadata.ProjectNamespace,
            "local-cortex-policy",
            authority.SubjectId,
            "pegasus-cortex-transfer-import",
            authority.PolicyRevision,
            PostgreSqlCortexRetentionCapability.Write);
        var briefs = postgreSql.CreateProjectBriefReadModel();
        var writer = new CortexMcpIntegratedRetentionRuntime(
            postgreSql,
            links,
            briefs,
            authority);
        var regenerator = new CortexIntegratedTransferBriefRegenerator(
            writer,
            briefs,
            projectScope);
        var importer = new CortexRetentionTransferImportService(
            postgreSql.CreateAgentRetentionRepository(retentionScope),
            regenerator);
        var imported = await importer.ImportAsync(
                source.Bundle,
                new CortexRetentionTransferImportScope(
                    authority.TenantId,
                    link.Metadata.ProjectLinkId,
                    link.Metadata.ProjectId,
                    link.Metadata.ProjectNamespace),
                cancellationToken)
            .ConfigureAwait(false);

        if (imported.Accepted &&
            regenerator.LastReadback is not { Verified: true })
        {
            throw new InvalidDataException(
                "The accepted transfer did not produce a verified project-brief readback.");
        }

        return new CortexIntegratedProjectTransferImportResult(
            ResultContractId,
            imported.Accepted,
            imported.Disposition,
            imported.BundleId,
            imported.BundleHash,
            source.FileSha256,
            authority.TenantId,
            link.Metadata.ProjectLinkId,
            link.Metadata.ProjectId,
            link.Metadata.ProjectNamespace,
            imported.DocumentCount,
            imported.AppendedCount,
            imported.ExistingCount,
            regenerator.LastReadback,
            imported.DiagnosticCode);
    }

    internal static async ValueTask<CortexProjectLinkV1> AuthorizeProjectAsync(
        CortexIntegratedProjectTransferImportArguments options,
        CortexLocalAuthority authority,
        ICortexProjectLinkRepository links,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(links);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(options.TenantId, authority.TenantId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The import scope is not server-authorized.");
        var link = await links.FindAsync(
                authority.ProjectLinkAccess(CortexProjectLinkCapability.Read),
                options.ProjectLinkId,
                cancellationToken)
            .ConfigureAwait(false);
        if (link is null ||
            !string.Equals(link.Metadata.EndpointId, authority.EndpointId, StringComparison.Ordinal) ||
            !string.Equals(link.Metadata.ProjectLinkId, options.ProjectLinkId, StringComparison.Ordinal) ||
            !string.Equals(link.Metadata.ProjectId, options.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(link.Metadata.ProjectNamespace, options.ProjectNamespace, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The import scope is not server-authorized.");
        }
        return link;
    }

    private static void RequireImportCapabilities(CortexLocalAuthority authority)
    {
        const CortexLocalCapability required =
            CortexLocalCapability.RetainedRead |
            CortexLocalCapability.RetainedWrite |
            CortexLocalCapability.ProjectBrief;
        if (!authority.HasCapability(required))
        {
            throw new UnauthorizedAccessException(
                "The local principal lacks transfer-import authority.");
        }
    }

    private static async ValueTask DiagnosticAsync(
        TextWriter diagnostics,
        string code,
        Exception exception)
    {
        var cause = exception.GetType().Name;
        await diagnostics.WriteLineAsync(
            $"severity=error operation=transfer.import code={code} message=cause={cause}: transfer import was not completed")
            .ConfigureAwait(false);
        await diagnostics.FlushAsync().ConfigureAwait(false);
    }
}
