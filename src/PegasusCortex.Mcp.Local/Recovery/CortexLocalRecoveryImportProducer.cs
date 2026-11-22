using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.OfflineJournal.Contracts;
using Pegasus.Cortex.OfflineJournal.Import;
using Pegasus.Cortex.OfflineJournal.Paths;
using Pegasus.Cortex.OfflineJournal.Security;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

internal sealed record CortexLocalRecoveryImportResult(
    string ContractId,
    bool Accepted,
    string Disposition,
    string ProjectLinkId,
    string? ProjectId,
    string? ProjectNamespace,
    string RunId,
    string? ManifestSha256,
    string? ManifestRelativePath,
    int FileCount,
    long SourceBytes,
    bool SourceMutationAllowed,
    string TypedConversion,
    bool FallbackRequired,
    string FallbackInstructions,
    string? DiagnosticCode);

/// <summary>
/// Explicit standalone producer for one registry-authorized project's fixed
/// .cortex recovery set. It retains exact bytes and the verified copy-first
/// manifest; it does not interpret Markdown, infer task state, or mutate source.
/// </summary>
internal sealed class CortexLocalRecoveryImportProducer
{
    public const string ResultContractId =
        "pegasus.cortex.local-recovery-import-result/1.0";
    public const string ManifestRelativePathPrefix = "j/i/";
    public const string ManifestFileName = "i.json";

    private readonly CortexLocalProjectRegistry _registry;
    private readonly string _profileId;
    private readonly string _stateRoot;
    private readonly IJournalFileSystemBoundaryFactory _fileSystemBoundaryFactory;
    private readonly OfflineJournalOptions _options;

    public CortexLocalRecoveryImportProducer(
        CortexLocalProjectRegistry registry,
        string profileId,
        string stateRoot,
        IJournalFileSystemBoundaryFactory? fileSystemBoundaryFactory = null,
        OfflineJournalOptions? options = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        CortexLocalProjectRegistryPolicy.RequireProfile(profileId);
        if (!string.Equals(
                registry.Document.ProfileId,
                profileId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The recovery producer profile does not match the protected registry.",
                nameof(profileId));
        }

        _profileId = profileId;
        _stateRoot = CortexLocalRegistryPathPolicy.ValidateStateRootPath(
            stateRoot,
            requireExisting: true,
            allowAbsent: false);
        _fileSystemBoundaryFactory =
            fileSystemBoundaryFactory ??
            ProtectedLocalJournalFileSystemBoundaryFactory.Instance;
        _options = options ?? new OfflineJournalOptions();
        _options.Validate();
        if (_options.MaxLegacyImportFileBytes >
            ExactCopyJournalPayloadProtector.MaximumSupportedPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The exact-copy recovery producer supports at most 16 MiB per approved source file.");
        }
    }

    public async ValueTask<CortexLocalRecoveryImportResult> ImportAsync(
        string projectLinkId,
        string destinationRoot,
        string runId,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_registry.TryAuthorizeRecoveryImport(
                projectLinkId,
                out var registration))
        {
            return Rejected(
                projectLinkId,
                runId,
                "cortex_local_recovery_import_forbidden");
        }

        var canonicalDestination = RequireExplicitDestination(
            destinationRoot,
            registration!.RootPath);
        var sourceRoot = Path.GetFullPath(
            Path.Combine(registration.RootPath, ".cortex"));
        var scopeHash = CortexCanonicalJson.Sha256(
            $"{_profileId}\n{registration.ProjectLinkId}\n{registration.RootPathHash}");
        var profileHash = CortexCanonicalJson.Sha256(_profileId);
        var identity = new OfflineJournalIdentity(
            $"recovery-{scopeHash[..32]}",
            "local-recovery-producer",
            $"local-{profileHash[..32]}",
            registration.ProjectId);
        var protector = new ExactCopyJournalPayloadProtector(
            checked((int)_options.MaxLegacyImportFileBytes));

        await using var planner = new LegacyCortexImportPlanner(
            canonicalDestination,
            canonicalDestination,
            identity,
            protector,
            _fileSystemBoundaryFactory,
            _options);
        var retained = await planner.InventoryAndCopyRecoverySetAsync(
            sourceRoot,
            sourceRoot,
            runId,
            capturedAt,
            cancellationToken).ConfigureAwait(false);
        var accepted = retained.Disposition is
            LegacyCortexImportDisposition.Copied or
            LegacyCortexImportDisposition.Existing;
        var manifest = retained.Manifest;
        return new CortexLocalRecoveryImportResult(
            ResultContractId,
            accepted,
            retained.Disposition.ToString().ToLowerInvariant(),
            registration.ProjectLinkId,
            registration.ProjectId,
            registration.ProjectNamespace,
            runId,
            manifest?.ManifestSha256,
            manifest is null
                ? null
                : $"{ManifestRelativePathPrefix}{runId}/{ManifestFileName}",
            manifest?.Items.Count ?? 0,
            manifest?.SourceBytes ?? 0,
            SourceMutationAllowed: false,
            TypedConversion: "omitted-unsafe",
            FallbackRequired: true,
            FallbackInstructions:
                "Read the retained manifest and exact copied recovery files; task state was not inferred or converted.",
            retained.Diagnostic?.Code);
    }

    private string RequireExplicitDestination(
        string destinationRoot,
        string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot) ||
            destinationRoot.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(destinationRoot))
        {
            throw new ArgumentException(
                "An existing canonical absolute recovery destination is required.",
                nameof(destinationRoot));
        }

        var canonical = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(destinationRoot));
        if (!string.Equals(destinationRoot, canonical, StringComparison.Ordinal) ||
            !Directory.Exists(canonical))
        {
            throw new ArgumentException(
                "The recovery destination must be an existing canonical directory.",
                nameof(destinationRoot));
        }
        CortexLocalRecoveryExportPathPolicy.RequireLocalDrive(canonical);

        // Keep recovery storage separate from project authority and from the
        // entire dedicated state tree. The local host rejects extra state-root
        // entries, so even an explicitly selected child would break restart.
        if (PathsOverlap(canonical, projectRoot) ||
            PathsOverlap(canonical, _stateRoot))
        {
            throw new ArgumentException(
                "The recovery destination cannot overlap the registered project or local state tree.",
                nameof(destinationRoot));
        }
        return canonical;
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

    private static CortexLocalRecoveryImportResult Rejected(
        string projectLinkId,
        string runId,
        string diagnosticCode) =>
        new(
            ResultContractId,
            Accepted: false,
            Disposition: "rejected",
            projectLinkId,
            ProjectId: null,
            ProjectNamespace: null,
            runId,
            ManifestSha256: null,
            ManifestRelativePath: null,
            FileCount: 0,
            SourceBytes: 0,
            SourceMutationAllowed: false,
            TypedConversion: "omitted-unsafe",
            FallbackRequired: true,
            FallbackInstructions:
                "Use the registered project's authoritative local .cortex recovery files.",
            diagnosticCode);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
