using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Local.Configuration;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

internal sealed record CortexLocalProjectTransferTenantRebindResult(
    string ContractId,
    bool Accepted,
    string Disposition,
    string SourceBundleId,
    string SourceBundleHash,
    string SourceBundleFileSha256,
    string SourceTenantId,
    string TargetTenantId,
    string ProjectLinkId,
    string ProjectId,
    string ProjectNamespace,
    string AuthoritySetHash,
    int AuthoritativeDocumentCount,
    int SourceDerivedBriefEvidenceCount,
    int ReboundDerivedBriefEvidenceCount,
    string BundleId,
    string BundleHash,
    long CanonicalBytes,
    string OutputRelativePath);

/// <summary>
/// Creates a new sealed transfer whose outer tenant scope is rebound while all
/// authoritative documents remain byte-identical. Project link, project ID,
/// namespace, authority hash, revision hashes, and chains cannot change. Briefs
/// are scope-derived, so the new bundle omits them and binds the complete source
/// bundle hash as immutable comparison evidence instead.
/// </summary>
internal static class CortexLocalProjectTransferTenantRebindCommand
{
    private const string ResultContractId =
        "pegasus.cortex.local-project-transfer-tenant-rebind-result/1.0";
    private const string ExportToolId = "pegasus-cortex-tenant-rebind";

    public static async Task<int> RunAsync(string[] args, TextWriter diagnostics)
    {
        CortexLocalProjectTransferTenantRebindArguments options;
        try
        {
            options = CortexLocalProjectTransferTenantRebindArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await DiagnosticAsync(
                diagnostics,
                "cortex_transfer_tenant_rebind_arguments_invalid",
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
            var result = await RebindAsync(options, cancellation.Token)
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
            await DiagnosticAsync(
                diagnostics,
                "cortex_transfer_tenant_rebind_failed",
                exception).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static async ValueTask<CortexLocalProjectTransferTenantRebindResult>
        RebindAsync(
            CortexLocalProjectTransferTenantRebindArguments options,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        // Validate the exact source file and every inner contract before any
        // destination or staging path is created.
        var source = await CortexLocalProjectTransferImportPathPolicy
            .ReadCanonicalBundleWithHashAsync(
                options.BundlePath,
                options.ExpectedBundleFileSha256,
                cancellationToken)
            .ConfigureAwait(false);
        RequireAcknowledgedSourceScope(source.Bundle, options);
        if (options.CapturedAt < source.Bundle.CapturedAt)
        {
            throw new CortexRetentionTransferValidationException(
                "transfer_tenant_rebind_capture_time_regressed");
        }

        var evidenceHashes = source.Bundle.Provenance.EvidenceHashes
            .Concat([source.Bundle.BundleHash])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (evidenceHashes.Length > CortexRetentionTransferContract.MaximumEvidenceHashes)
        {
            throw new CortexRetentionTransferValidationException(
                "transfer_tenant_rebind_evidence_capacity_exhausted");
        }

        var identitySeal = CortexCanonicalJson.Sha256(string.Join(
            '\n',
            source.Bundle.BundleHash,
            options.TargetTenantId,
            options.ProjectLinkId,
            options.ProjectId,
            options.ProjectNamespace,
            options.RunId));
        var rebound = CortexRetentionTransferBundlePolicy.Seal(source.Bundle with
        {
            BundleId = $"transfer-tenant-rebind-{identitySeal[..32]}",
            CapturedAt = options.CapturedAt,
            Project = source.Bundle.Project with { TenantId = options.TargetTenantId },
            Provenance = source.Bundle.Provenance with
            {
                ExportToolId = ExportToolId,
                EvidenceHashes = Array.AsReadOnly(evidenceHashes)
            },
            // Derived project briefs embed tenant scope and are not authority.
            // The exact source bundle remains sealed evidence for comparison.
            DerivedBriefEvidence = Array.Empty<CortexRetentionTransferDocument>(),
            BundleHash = CortexRetentionTransferContract.EmptySha256
        });
        RequireAuthoritativeBytesUnchanged(source.Bundle, rebound);
        var canonical = CortexCanonicalJson.Serialize(rebound);

        var destination = CortexLocalProjectTransferTenantRebindPathPolicy
            .RequireDestinationRoot(options.DestinationRoot);
        var finalRoot = CortexLocalProjectTransferPathPolicy.OutputRoot(
            destination,
            options.RunId);
        var stagingRoot = CortexLocalProjectTransferPathPolicy.StagingRoot(
            destination,
            options.RunId);
        if (Directory.Exists(finalRoot) || File.Exists(finalRoot))
            throw new IOException("The tenant-rebound transfer output already exists.");

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

        return new CortexLocalProjectTransferTenantRebindResult(
            ResultContractId,
            Accepted: true,
            Disposition: "tenant-rebound-copy",
            source.Bundle.BundleId,
            source.Bundle.BundleHash,
            source.FileSha256,
            options.SourceTenantId,
            options.TargetTenantId,
            options.ProjectLinkId,
            options.ProjectId,
            options.ProjectNamespace,
            source.Bundle.Project.AuthoritySetHash,
            source.Bundle.AuthoritativeDocuments.Count,
            source.Bundle.DerivedBriefEvidence.Count,
            rebound.DerivedBriefEvidence.Count,
            rebound.BundleId,
            rebound.BundleHash,
            canonical.Length,
            Path.GetFileName(finalRoot));
    }

    private static void RequireAcknowledgedSourceScope(
        CortexRetentionTransferBundleV1 source,
        CortexLocalProjectTransferTenantRebindArguments options)
    {
        if (!string.Equals(source.Project.TenantId, options.SourceTenantId, StringComparison.Ordinal) ||
            !string.Equals(source.Project.ProjectLinkId, options.ProjectLinkId, StringComparison.Ordinal) ||
            !string.Equals(source.Project.ProjectId, options.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(source.Project.ProjectNamespace, options.ProjectNamespace, StringComparison.Ordinal))
        {
            throw new CortexRetentionTransferValidationException(
                "transfer_tenant_rebind_source_scope_mismatch");
        }
    }

    private static void RequireAuthoritativeBytesUnchanged(
        CortexRetentionTransferBundleV1 source,
        CortexRetentionTransferBundleV1 rebound)
    {
        if (source.AuthoritativeDocuments.Count != rebound.AuthoritativeDocuments.Count)
        {
            throw new CortexRetentionTransferValidationException(
                "transfer_tenant_rebind_authoritative_change");
        }
        for (var index = 0; index < source.AuthoritativeDocuments.Count; index++)
        {
            var expected = CortexCanonicalJson.Serialize(source.AuthoritativeDocuments[index]);
            var actual = CortexCanonicalJson.Serialize(rebound.AuthoritativeDocuments[index]);
            if (!expected.AsSpan().SequenceEqual(actual))
            {
                throw new CortexRetentionTransferValidationException(
                    "transfer_tenant_rebind_authoritative_change");
            }
        }
    }

    private static async ValueTask DiagnosticAsync(
        TextWriter diagnostics,
        string code,
        Exception exception)
    {
        await diagnostics.WriteLineAsync(
            $"severity=error operation=transfer.tenant-rebind code={code} message=cause={exception.GetType().Name}: tenant-rebound copy was not created")
            .ConfigureAwait(false);
        await diagnostics.FlushAsync().ConfigureAwait(false);
    }
}

internal static class CortexLocalProjectTransferTenantRebindPathPolicy
{
    private const int MaximumFinalPathCharacters = 240;

    public static string RequireDestinationRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "An existing canonical local tenant-rebind destination is required.");
        }
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!string.Equals(value, canonical, PathComparison) ||
            canonical.Length > MaximumFinalPathCharacters ||
            !Directory.Exists(canonical))
        {
            throw new ArgumentException(
                "The tenant-rebind destination must be an existing canonical directory.");
        }
        CortexLocalRecoveryExportPathPolicy.RequireLocalDrive(canonical);
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(canonical);
        var info = new DirectoryInfo(canonical);
        if (info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "The tenant-rebind destination cannot be a link or reparse point.");
        }
        CortexLocalRegistryAclPolicy.RequireProtectedDirectory(canonical);
        return canonical;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
