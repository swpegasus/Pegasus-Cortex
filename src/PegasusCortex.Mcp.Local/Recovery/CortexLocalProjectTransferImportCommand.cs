using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Knowledge;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

internal sealed record CortexLocalProjectTransferImportResult(
    string ContractId,
    bool Accepted,
    string Disposition,
    string ProfileId,
    string BundleId,
    string BundleHash,
    string AuthoritySetHash,
    int AuthoritativeDocumentCount,
    int DerivedBriefEvidenceCount,
    int AppendedCount,
    int ExistingCount,
    string? RegistryHash,
    string? ProjectBriefContextSetId,
    string? MatchingEvidenceContentHash,
    string? DiagnosticCode);

/// <summary>
/// Imports one canonical provider-neutral bundle into a newly created local
/// profile. The bundle supplies passive data only: project identity, local
/// root, grants, OS identity, ACLs, and destination paths all come from the
/// separately validated operator request and current machine.
/// </summary>
internal static class CortexLocalProjectTransferImportCommand
{
    private const string ResultContractId =
        "pegasus.cortex.local-project-transfer-import-result/1.0";

    public static async Task<int> RunAsync(string[] args, TextWriter diagnostics)
    {
        CortexLocalProjectTransferImportArguments options;
        try
        {
            options = CortexLocalProjectTransferImportArguments.Parse(args);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
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

    internal static async ValueTask<CortexLocalProjectTransferImportResult> ImportAsync(
        CortexLocalProjectTransferImportArguments options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        // The exact bundle and its logical scope are verified before even a
        // staging directory is selected. A mismatched bundle therefore cannot
        // create registry, SQLite, journal, or other local state.
        var bundle = await CortexLocalProjectTransferImportPathPolicy
            .ReadCanonicalBundleAsync(options.BundlePath, cancellationToken)
            .ConfigureAwait(false);
        var authorizedScope = new CortexRetentionTransferImportScope(
            options.Project.TenantId,
            options.Project.ProjectLinkId,
            options.Project.ProjectId,
            options.Project.ProjectNamespace);
        if (!Matches(bundle.Project, authorizedScope))
        {
            return Result(
                options.ProfileId,
                bundle,
                CortexRetentionTransferImportDispositions.Rejected,
                accepted: false,
                appendedCount: 0,
                existingCount: 0,
                registryHash: null,
                regeneration: null,
                "transfer_authorized_scope_mismatch");
        }

        var finalRoot = options.DestinationPaths.StateRoot;
        var parent = CortexLocalProjectTransferImportPathPolicy.RequireNewProfileRoot(
            finalRoot,
            options.Project.RootPath);
        var stagingRoot = CortexLocalProjectTransferImportPathPolicy.StagingRoot(
            parent,
            options.ProfileId,
            bundle.BundleHash);
        var stagingPaths = new CortexMcpLocalStatePaths(
            options.ProfileId,
            stagingRoot,
            Path.Combine(stagingRoot, "cortex.db"),
            Path.Combine(stagingRoot, "projects.json"));

        var created = false;
        var promoted = false;
        try
        {
            Directory.CreateDirectory(stagingRoot);
            created = true;
            CortexLocalRegistryAclPolicy.ProtectDirectory(stagingRoot);
            CortexLocalRegistryAclPolicy.RequireProtectedDirectory(stagingRoot);

            // Recreate registry identity from explicit operator inputs. No
            // path, ACL, OS identity, or capability value is copied from the
            // provider-neutral bundle.
            var registration = CortexLocalProjectRegistryFile.CreateRegistration(
                options.Project.TenantId,
                options.Project.ProjectLinkId,
                options.Project.ProjectId,
                options.Project.ProjectNamespace,
                options.Project.RootPath,
                options.Project.AllowRead,
                options.Project.AllowWrite);
            var registryDocument = CortexLocalProjectRegistryFile.Create(
                options.ProfileId,
                [registration]);
            await CortexLocalProjectRegistryFile.WriteNewProtectedAsync(
                stagingPaths.RegistryPath,
                registryDocument,
                cancellationToken).ConfigureAwait(false);

            CortexRetentionTransferImportResult imported;
            CortexRetentionTransferBriefRegenerationResult? regeneration;
            var clock = new FixedTimeProvider(bundle.CapturedAt);
            await using (var composition = await CortexMcpLocalComposition.CreateAsync(
                             options.ProfileId,
                             stagingPaths.DatabasePath,
                             stagingPaths.RegistryPath,
                             "import-project-transfer",
                             clock,
                             cancellationToken).ConfigureAwait(false))
            {
                var regenerator = new CortexLocalTransferBriefRegenerator(
                    composition.Runtime,
                    composition.ProjectBriefStore,
                    registration);
                imported = await new CortexRetentionTransferImportService(
                        composition.Store,
                        regenerator)
                    .ImportAsync(bundle, authorizedScope, cancellationToken)
                    .ConfigureAwait(false);
                regeneration = imported.BriefRegeneration;
                if (!imported.Accepted)
                {
                    return Result(
                        options.ProfileId,
                        bundle,
                        imported.Disposition,
                        accepted: false,
                        imported.AppendedCount,
                        imported.ExistingCount,
                        registryDocument.RegistryHash,
                        regeneration,
                        imported.DiagnosticCode);
                }

                await VerifyImportedAsync(
                    composition,
                    registration,
                    bundle,
                    regeneration!,
                    cancellationToken).ConfigureAwait(false);
            }

            // A second open proves the staged profile is independently
            // recoverable after SQLite has closed and restarted.
            await using (var restarted = await CortexMcpLocalComposition.CreateAsync(
                             options.ProfileId,
                             stagingPaths.DatabasePath,
                             stagingPaths.RegistryPath,
                             "import-project-transfer-verify",
                             clock,
                             cancellationToken).ConfigureAwait(false))
            {
                await VerifyImportedAsync(
                    restarted,
                    registration,
                    bundle,
                    regeneration!,
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(finalRoot) || File.Exists(finalRoot))
                throw new IOException("Transfer import never replaces an existing local profile root.");
            Directory.Move(stagingRoot, finalRoot);
            promoted = true;

            return Result(
                options.ProfileId,
                bundle,
                imported.Disposition,
                accepted: true,
                imported.AppendedCount,
                imported.ExistingCount,
                registryDocument.RegistryHash,
                regeneration,
                diagnosticCode: null);
        }
        finally
        {
            if (created && !promoted && Directory.Exists(stagingRoot))
            {
                CortexLocalRecoveryExportPathPolicy.DeleteVerifiedTree(
                    parent,
                    stagingRoot);
            }
        }
    }

    private static async ValueTask VerifyImportedAsync(
        CortexMcpLocalComposition composition,
        CortexLocalProjectRegistration registration,
        CortexRetentionTransferBundleV1 bundle,
        CortexRetentionTransferBriefRegenerationResult regeneration,
        CancellationToken cancellationToken)
    {
        var integrity = await composition.Store.CheckIntegrityAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!integrity.Healthy)
            throw new InvalidDataException("The staged transfer profile failed SQLite integrity validation.");

        foreach (var expected in bundle.AuthoritativeDocuments)
        {
            var actual = await composition.Store.FindRevisionAsync(
                expected.RepositoryProjectId,
                expected.Kind,
                expected.DocumentId,
                expected.Revision,
                cancellationToken).ConfigureAwait(false);
            if (actual is null || !Exact(expected, actual))
            {
                throw new InvalidDataException(
                    "The staged transfer profile did not preserve an exact retained revision.");
            }
        }

        var brief = await composition.ProjectBriefStore.FindCurrentAsync(
            CreateBriefScope(registration),
            cancellationToken).ConfigureAwait(false);
        if (brief is null ||
            !string.Equals(
                brief.ContextSetId,
                regeneration.ContextSetId,
                StringComparison.Ordinal) ||
            !string.Equals(brief.SourceHash, regeneration.SourceHash, StringComparison.Ordinal) ||
            !string.Equals(
                brief.AuthoritySetHash,
                regeneration.AuthoritySetHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The staged transfer profile did not regenerate the expected current brief.");
        }
    }

    private static CortexProjectBriefReadScope CreateBriefScope(
        CortexLocalProjectRegistration registration)
    {
        var identity = CortexLocalOsIdentity.Capture();
        return new CortexProjectBriefReadScope(
            registration.TenantId,
            registration.ProjectLinkId,
            registration.ProjectId,
            registration.ProjectNamespace,
            "local-os-policy",
            $"os_{identity.Hash}",
            "pegasus-cortex-local",
            PolicyRevision: 1);
    }

    private static bool Exact(
        CortexRetentionTransferDocument expected,
        CortexRetainedDocument actual) =>
        string.Equals(expected.RepositoryProjectId, actual.ProjectId, StringComparison.Ordinal) &&
        string.Equals(expected.Kind, actual.Kind, StringComparison.Ordinal) &&
        string.Equals(expected.DocumentId, actual.DocumentId, StringComparison.Ordinal) &&
        expected.Revision == actual.Revision &&
        string.Equals(expected.PreviousHash, actual.PreviousHash, StringComparison.Ordinal) &&
        string.Equals(expected.IdempotencyKey, actual.IdempotencyKey, StringComparison.Ordinal) &&
        string.Equals(expected.ProducerId, actual.ProducerId, StringComparison.Ordinal) &&
        string.Equals(expected.ContractId, actual.ContractId, StringComparison.Ordinal) &&
        expected.CanonicalUtf8.AsSpan().SequenceEqual(actual.CanonicalUtf8) &&
        string.Equals(expected.ContentHash, actual.ContentHash, StringComparison.Ordinal) &&
        string.Equals(expected.SearchText, actual.SearchText, StringComparison.Ordinal) &&
        expected.CapturedAt == actual.CapturedAt &&
        expected.ExpiresAt == actual.ExpiresAt;

    private static bool Matches(
        CortexRetentionTransferProjectScope project,
        CortexRetentionTransferImportScope authorizedScope) =>
        string.Equals(project.TenantId, authorizedScope.TenantId, StringComparison.Ordinal) &&
        string.Equals(project.ProjectLinkId, authorizedScope.ProjectLinkId, StringComparison.Ordinal) &&
        string.Equals(project.ProjectId, authorizedScope.ProjectId, StringComparison.Ordinal) &&
        string.Equals(
            project.ProjectNamespace,
            authorizedScope.ProjectNamespace,
            StringComparison.Ordinal);

    private static CortexLocalProjectTransferImportResult Result(
        string profileId,
        CortexRetentionTransferBundleV1 bundle,
        string disposition,
        bool accepted,
        int appendedCount,
        int existingCount,
        string? registryHash,
        CortexRetentionTransferBriefRegenerationResult? regeneration,
        string? diagnosticCode) => new(
            ResultContractId,
            accepted,
            disposition,
            profileId,
            bundle.BundleId,
            bundle.BundleHash,
            bundle.Project.AuthoritySetHash,
            bundle.AuthoritativeDocuments.Count,
            bundle.DerivedBriefEvidence.Count,
            appendedCount,
            existingCount,
            registryHash,
            regeneration?.ContextSetId,
            regeneration?.MatchingEvidenceContentHash,
            diagnosticCode);

    private static async ValueTask DiagnosticAsync(
        TextWriter diagnostics,
        string code,
        Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length > 512)
            message = message[..512];
        await diagnostics.WriteLineAsync(
            $"severity=error operation=transfer.import code={code} message=cause={exception.GetType().Name}: {message}")
            .ConfigureAwait(false);
        await diagnostics.FlushAsync().ConfigureAwait(false);
    }

    private sealed class FixedTimeProvider(DateTimeOffset capturedAt) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => capturedAt;
    }
}

/// <summary>
/// Regenerates the derived brief by replaying only the imported authoritative
/// task head through the same local runtime used by MCP writes. Historical
/// brief bytes remain comparison evidence and are never appended as authority.
/// </summary>
internal sealed class CortexLocalTransferBriefRegenerator(
    CortexMcpLocalRuntime runtime,
    Pegasus.Cortex.Persistence.Sqlite.Query.SqliteCortexProjectBriefStore briefStore,
    CortexLocalProjectRegistration registration) :
    ICortexRetentionTransferBriefRegenerator
{
    public async ValueTask<CortexRetentionTransferBriefRegenerationResult>
        RegenerateAsync(
            CortexRetentionTransferProjectScope project,
            AgentTaskSnapshotV1 authoritativeTaskSnapshot,
            IReadOnlyList<CortexRetentionTransferDocument> comparisonEvidence,
            CancellationToken cancellationToken)
    {
        var write = await runtime.SubmitTaskSnapshotAsync(
            registration.ProjectLinkId,
            registration.ProjectNamespace,
            authoritativeTaskSnapshot,
            cancellationToken).ConfigureAwait(false);
        if (write is null ||
            write.Disposition is not (
                CortexRetentionWriteDispositions.Appended or
                CortexRetentionWriteDispositions.Idempotent) ||
            write.ProjectBriefDisposition is not (
                CortexRetentionWriteDispositions.Appended or
                CortexRetentionWriteDispositions.Idempotent or
                CortexProjectBriefMaterializationDispositions.AlreadyAdvanced) ||
            write.ProjectBriefContextSetId is null ||
            write.ProjectionIssueCode is not null)
        {
            return Failed("transfer_local_brief_regeneration_failed");
        }

        var brief = await briefStore.FindCurrentAsync(
            new CortexProjectBriefReadScope(
                registration.TenantId,
                registration.ProjectLinkId,
                registration.ProjectId,
                registration.ProjectNamespace,
                "local-os-policy",
                $"os_{CortexLocalOsIdentity.Capture().Hash}",
                "pegasus-cortex-local",
                PolicyRevision: 1),
            cancellationToken).ConfigureAwait(false);
        if (brief is null ||
            !string.Equals(brief.ContextSetId, write.ProjectBriefContextSetId, StringComparison.Ordinal) ||
            !string.Equals(brief.SourceHash, authoritativeTaskSnapshot.SnapshotHash, StringComparison.Ordinal) ||
            !string.Equals(brief.AuthoritySetHash, project.AuthoritySetHash, StringComparison.Ordinal))
        {
            return Failed("transfer_local_brief_verification_failed");
        }

        var briefBytes = CortexCanonicalJson.Serialize(brief);
        var matchingEvidence = comparisonEvidence.FirstOrDefault(item =>
            item.CanonicalUtf8.AsSpan().SequenceEqual(briefBytes));
        return new CortexRetentionTransferBriefRegenerationResult(
            Accepted: true,
            write.ProjectBriefDisposition,
            brief.ContextSetId,
            brief.SourceHash,
            brief.AuthoritySetHash,
            matchingEvidence?.ContentHash,
            DiagnosticCode: null);
    }

    private static CortexRetentionTransferBriefRegenerationResult Failed(string code) =>
        new(
            Accepted: false,
            "rejected",
            ContextSetId: null,
            SourceHash: null,
            AuthoritySetHash: null,
            MatchingEvidenceContentHash: null,
            DiagnosticCode: code);
}

internal sealed record CortexLocalCanonicalTransferSource(
    CortexRetentionTransferBundleV1 Bundle,
    string FileSha256);

internal static class CortexLocalProjectTransferImportPathPolicy
{
    private const long MaximumBundleBytes =
        CortexRetentionTransferContract.MaximumCanonicalPayloadBytes +
        16L * 1024 * 1024;
    private const int MaximumFinalPathCharacters = 240;

    public static async ValueTask<CortexRetentionTransferBundleV1>
        ReadCanonicalBundleAsync(
            string value,
            CancellationToken cancellationToken)
    {
        var source = await ReadCanonicalBundleWithHashAsync(
                value,
                expectedFileSha256: null,
                cancellationToken)
            .ConfigureAwait(false);
        return source.Bundle;
    }

    /// <summary>
    /// Reads and hashes one protected bundle through the same open handle so an
    /// operator-supplied file seal cannot be checked against different bytes.
    /// </summary>
    public static async ValueTask<CortexLocalCanonicalTransferSource>
        ReadCanonicalBundleWithHashAsync(
            string value,
            string? expectedFileSha256,
            CancellationToken cancellationToken)
    {
        var path = RequireBundlePath(value);
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length is < 2 or > MaximumBundleBytes)
            throw new InvalidDataException("The transfer bundle is outside its byte boundary.");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var fileSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();
        if (expectedFileSha256 is not null &&
            !string.Equals(fileSha256, expectedFileSha256, StringComparison.Ordinal))
        {
            throw new CortexRetentionTransferValidationException(
                "transfer_bundle_file_hash_mismatch");
        }
        return new CortexLocalCanonicalTransferSource(
            CortexRetentionTransferBundlePolicy.ReadCanonical(bytes),
            fileSha256);
    }

    public static string RequireNewProfileRoot(
        string destinationStateRoot,
        string projectRoot)
    {
        var finalRoot = CortexLocalRegistryPathPolicy.ValidateStateRootPath(
            destinationStateRoot,
            requireExisting: false,
            allowAbsent: true);
        if (Directory.Exists(finalRoot) || File.Exists(finalRoot))
            throw new IOException("Transfer import never replaces an existing local profile root.");
        var parent = Path.GetDirectoryName(finalRoot) ??
            throw new ArgumentException("The transfer destination has no parent.");
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException(
                "The transfer destination parent must already exist.");
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(parent);
        CortexLocalRecoveryExportPathPolicy.RequireLocalDrive(parent);
        if (PathsOverlap(finalRoot, projectRoot))
        {
            throw new ArgumentException(
                "The new local profile cannot overlap the registered project root.");
        }
        return parent;
    }

    public static string StagingRoot(
        string parent,
        string profileId,
        string bundleHash)
    {
        var path = Path.Combine(
            parent,
            $".cortex-import-{CortexCanonicalJson.Sha256(profileId)[..12]}-{bundleHash[..12]}-{Guid.NewGuid():N}");
        if (path.Length > MaximumFinalPathCharacters)
            throw new ArgumentException("The transfer staging path exceeds 240 characters.");
        path = CortexLocalRegistryPathPolicy.ValidateStateRootPath(
            path,
            requireExisting: false,
            allowAbsent: true);
        RequireChild(parent, path);
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException("The transfer staging path is already occupied.");
        return path;
    }

    private static string RequireBundlePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("An exact local transfer bundle path is required.");
        }
        var path = Path.GetFullPath(value);
        if (!string.Equals(path, value, PathComparison) ||
            path.Length > MaximumFinalPathCharacters ||
            !string.Equals(Path.GetFileName(path), "bundle.json", StringComparison.Ordinal) ||
            !File.Exists(path) ||
            Directory.Exists(path))
        {
            throw new ArgumentException(
                "The transfer bundle must be an existing canonical bundle.json file.");
        }
        CortexLocalRecoveryExportPathPolicy.RequireLocalDrive(path);
        var parent = Path.GetDirectoryName(path) ??
            throw new ArgumentException("The transfer bundle has no parent.");
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(parent);
        var info = new FileInfo(path);
        if (info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The transfer bundle cannot be a link or reparse point.");
        }
        CortexLocalRegistryAclPolicy.RequireProtectedFileAndParent(path, CortexUserStateAccess.Read);
        return path;
    }

    private static void RequireChild(string parent, string path)
    {
        var canonicalParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var canonical = Path.GetFullPath(path);
        if (!canonical.StartsWith(
                canonicalParent + Path.DirectorySeparatorChar,
                PathComparison))
        {
            throw new ArgumentException("The transfer staging path escapes its parent.");
        }
    }

    private static bool PathsOverlap(string left, string right)
    {
        var canonicalLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var canonicalRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(canonicalLeft, canonicalRight, PathComparison) ||
            canonicalLeft.StartsWith(
                canonicalRight + Path.DirectorySeparatorChar,
                PathComparison) ||
            canonicalRight.StartsWith(
                canonicalLeft + Path.DirectorySeparatorChar,
                PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
