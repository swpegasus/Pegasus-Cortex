using System.Security.Cryptography;
using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.Persistence.PostgreSql.Migrations;

namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

/// <summary>
/// Canonical, non-secret evidence passed around the externally owned backup/restore step.
/// It contains only fixture identifiers and hashes; connection details and trust paths never
/// cross this boundary.
/// </summary>
internal sealed record FixtureBackupRestoreProofV1(
    int SchemaVersion,
    string ContractId,
    string Canonicalization,
    string SourceDatabaseIdentitySha256,
    FixtureBackupRestoreScopeV1 Scope,
    IReadOnlyList<FixtureMigrationProofV1> Migrations,
    IReadOnlyList<FixtureDurableAttemptProofV1> Attempts,
    FixtureReceiptProofV1 Receipt,
    FixtureSourceHeadProofV1 SourceHead,
    string ProofSha256);

internal sealed record FixtureBackupRestoreScopeV1(
    string TenantId,
    string Namespace,
    string StoreId,
    string StoreEpoch,
    string ProjectId,
    long PolicyRevision);

internal sealed record FixtureMigrationProofV1(string MigrationId, string UpSha256);

internal sealed record FixtureDurableAttemptProofV1(
    string BundleId,
    string BundleHash,
    string CommitId,
    string StorageRevision,
    string Disposition,
    int SourceEventCount,
    string DurableStateHash);

internal sealed record FixtureReceiptProofV1(
    string BundleId,
    string BundleHash,
    string CommitId,
    string StorageRevision,
    string ReceiptId,
    string ReceiptHash,
    string CanonicalReceiptSha256);

internal sealed record FixtureSourceHeadProofV1(
    string BaseServerRevision,
    string BundleId,
    string BundleHash,
    string CommitId,
    string StorageRevision,
    long SourceSequence,
    string EventId,
    string EventHash);

internal sealed record FixtureSyncWorkflowEvidence(
    IReadOnlyList<FixtureDurableAttemptProofV1> Attempts,
    FixtureReceiptProofV1 Receipt,
    FixtureSourceHeadProofV1 SourceHead);

internal static class FixtureBackupRestoreProofContract
{
    public const int SchemaVersion = 1;
    public const string ContractId = "pegasus.cortex.postgresql-backup-restore-proof/1.0";
    private const string DatabaseIdentityContractId =
        "pegasus.cortex.postgresql-fixture-database-identity/1.0";

    private static readonly string[] ExpectedBundleIds =
    [
        "bundle-origin",
        "bundle-origin",
        "bundle-stale-origin",
        "bundle-continuation",
        "bundle-branch-a",
        "bundle-branch-b"
    ];

    private static readonly string[] ExpectedDispositions =
    [
        "committed",
        "conflict",
        "conflict",
        "committed"
    ];

    public static FixtureBackupRestoreProofV1 Create(
        string sourceDatabaseName,
        FixtureSyncWorkflowEvidence evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatabaseName);
        ArgumentNullException.ThrowIfNull(evidence);

        var placeholder = new FixtureBackupRestoreProofV1(
            SchemaVersion,
            ContractId,
            CortexSyncContractIds.Canonicalization,
            DatabaseIdentitySha256(sourceDatabaseName),
            new FixtureBackupRestoreScopeV1(
                FixtureSyncContractFactory.TenantId,
                FixtureSyncContractFactory.Namespace,
                FixtureSyncContractFactory.StoreId,
                FixtureSyncContractFactory.StoreEpoch,
                FixtureSyncContractFactory.ProjectId,
                FixtureSyncContractFactory.PolicyRevision),
            PostgreSqlMigrationCatalog.All
                .Select(value => new FixtureMigrationProofV1(value.Id, value.Sha256))
                .ToArray(),
            evidence.Attempts.ToArray(),
            evidence.Receipt,
            evidence.SourceHead,
            CortexSyncContractIds.EmptySha256);
        var proof = placeholder with
        {
            ProofSha256 = CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(placeholder))
        };
        EnsureValid(proof);
        return proof;
    }

    public static void EnsureValidForRestore(
        FixtureBackupRestoreProofV1 proof,
        string restoredDatabaseName)
    {
        EnsureValid(proof);
        if (string.Equals(
            proof.SourceDatabaseIdentitySha256,
            DatabaseIdentitySha256(restoredDatabaseName),
            StringComparison.Ordinal))
        {
            throw Failure("backup_restore_database_not_distinct");
        }
    }

    public static void EnsureValid(FixtureBackupRestoreProofV1? proof)
    {
        if (proof is null ||
            proof.SchemaVersion != SchemaVersion ||
            !string.Equals(proof.ContractId, ContractId, StringComparison.Ordinal) ||
            !string.Equals(
                proof.Canonicalization,
                CortexSyncContractIds.Canonicalization,
                StringComparison.Ordinal) ||
            !ValidHash(proof.SourceDatabaseIdentitySha256) ||
            !ValidScope(proof.Scope) ||
            !ValidMigrations(proof.Migrations) ||
            !ValidAttempts(proof.Attempts) ||
            !ValidReceipt(proof.Receipt, proof.Attempts) ||
            !ValidSourceHead(proof.SourceHead, proof.Attempts) ||
            !ValidHash(proof.ProofSha256))
        {
            throw Failure("backup_restore_proof_invalid");
        }

        var placeholder = proof with { ProofSha256 = CortexSyncContractIds.EmptySha256 };
        var expectedHash = CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(placeholder));
        if (!string.Equals(expectedHash, proof.ProofSha256, StringComparison.Ordinal))
            throw Failure("backup_restore_proof_hash_mismatch");
    }

    internal static string DatabaseIdentitySha256(string databaseName) =>
        CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(new FixtureDatabaseIdentityV1(
            SchemaVersion,
            DatabaseIdentityContractId,
            databaseName)));

    private static bool ValidScope(FixtureBackupRestoreScopeV1? value) =>
        value is not null &&
        string.Equals(value.TenantId, FixtureSyncContractFactory.TenantId, StringComparison.Ordinal) &&
        string.Equals(value.Namespace, FixtureSyncContractFactory.Namespace, StringComparison.Ordinal) &&
        string.Equals(value.StoreId, FixtureSyncContractFactory.StoreId, StringComparison.Ordinal) &&
        string.Equals(value.StoreEpoch, FixtureSyncContractFactory.StoreEpoch, StringComparison.Ordinal) &&
        string.Equals(value.ProjectId, FixtureSyncContractFactory.ProjectId, StringComparison.Ordinal) &&
        value.PolicyRevision == FixtureSyncContractFactory.PolicyRevision;

    private static bool ValidMigrations(IReadOnlyList<FixtureMigrationProofV1>? values)
    {
        if (values is null || values.Count is < 1 or > 1024)
            return false;
        var expected = PostgreSqlMigrationCatalog.All
            .Select(value => new FixtureMigrationProofV1(value.Id, value.Sha256));
        return values.SequenceEqual(expected);
    }

    private static bool ValidAttempts(IReadOnlyList<FixtureDurableAttemptProofV1>? values)
    {
        if (values is null || values.Count != ExpectedBundleIds.Length)
            return false;

        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (value is null ||
                !string.Equals(value.BundleId, ExpectedBundleIds[index], StringComparison.Ordinal) ||
                !ValidHash(value.BundleHash) ||
                !ValidPrefixedHash(value.CommitId, "cm_") ||
                !ValidPrefixedHash(value.StorageRevision, "sr_") ||
                !ValidHash(value.DurableStateHash) ||
                value.SourceEventCount != 1)
            {
                return false;
            }
            if (index < ExpectedDispositions.Length &&
                !string.Equals(value.Disposition, ExpectedDispositions[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        var branchDispositions = values.Skip(4).Select(value => value.Disposition).ToArray();
        return branchDispositions.Count(value => value == "committed") == 1 &&
            branchDispositions.Count(value => value == "conflict") == 1 &&
            values.Select(value => (value.BundleId, value.BundleHash)).Distinct().Count() == values.Count;
    }

    private static bool ValidReceipt(
        FixtureReceiptProofV1? value,
        IReadOnlyList<FixtureDurableAttemptProofV1> attempts)
    {
        if (value is null)
            return false;
        var origin = attempts[0];
        return string.Equals(value.BundleId, origin.BundleId, StringComparison.Ordinal) &&
            string.Equals(value.BundleHash, origin.BundleHash, StringComparison.Ordinal) &&
            string.Equals(value.CommitId, origin.CommitId, StringComparison.Ordinal) &&
            string.Equals(value.StorageRevision, origin.StorageRevision, StringComparison.Ordinal) &&
            ValidIdentifier(value.ReceiptId, 128) &&
            ValidHash(value.ReceiptHash) &&
            ValidHash(value.CanonicalReceiptSha256);
    }

    private static bool ValidSourceHead(
        FixtureSourceHeadProofV1? value,
        IReadOnlyList<FixtureDurableAttemptProofV1> attempts)
    {
        if (value is null)
            return false;
        var continuation = attempts[3];
        var winner = attempts.Skip(4).SingleOrDefault(attempt => attempt.Disposition == "committed");
        return winner is not null &&
            string.Equals(value.BaseServerRevision, continuation.StorageRevision, StringComparison.Ordinal) &&
            string.Equals(value.BundleId, winner.BundleId, StringComparison.Ordinal) &&
            string.Equals(value.BundleHash, winner.BundleHash, StringComparison.Ordinal) &&
            string.Equals(value.CommitId, winner.CommitId, StringComparison.Ordinal) &&
            string.Equals(value.StorageRevision, winner.StorageRevision, StringComparison.Ordinal) &&
            value.SourceSequence == 3 &&
            string.Equals(
                value.EventId,
                winner.BundleId == "bundle-branch-a" ? "event-branch-a" : "event-branch-b",
                StringComparison.Ordinal) &&
            ValidHash(value.EventHash);
    }

    private static bool ValidIdentifier(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool ValidPrefixedHash(string? value, string prefix) =>
        value is not null && value.StartsWith(prefix, StringComparison.Ordinal) &&
        ValidHash(value[prefix.Length..]);

    private static bool ValidHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static FixtureConfigurationException Failure(string code) => new(code);

    private sealed record FixtureDatabaseIdentityV1(
        int SchemaVersion,
        string ContractId,
        string DatabaseName);
}

/// <summary>
/// Lexically validated proof location. Callers supply one trusted existing root and one
/// portable leaf; no API in the fixture accepts an arbitrary final path.
/// </summary>
internal sealed record FixtureBackupRestoreProofLocation
{
    private static readonly HashSet<string> ReservedWindowsNames = new(
        [
            "con", "prn", "aux", "nul",
            "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
            "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
        ],
        StringComparer.OrdinalIgnoreCase);

    private FixtureBackupRestoreProofLocation(string rootPath, string fileName, string fullPath)
    {
        RootPath = rootPath;
        FileName = fileName;
        FullPath = fullPath;
    }

    public string RootPath { get; }
    public string FileName { get; }
    public string FullPath { get; }

    public static FixtureBackupRestoreProofLocation Parse(string rootPath, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        try
        {
            if (!Path.IsPathFullyQualified(rootPath) ||
                OperatingSystem.IsWindows() && rootPath.StartsWith("\\\\", StringComparison.Ordinal))
            {
                throw Failure("backup_restore_proof_root_invalid");
            }

            var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(rootPath, canonicalRoot, comparison) || !ValidRootSegments(canonicalRoot))
                throw Failure("backup_restore_proof_root_not_canonical");
            if (!ValidLeaf(fileName))
                throw Failure("backup_restore_proof_file_invalid");

            var fullPath = Path.GetFullPath(Path.Combine(canonicalRoot, fileName));
            var temporaryPath = Path.GetFullPath(Path.Combine(canonicalRoot, ".ctxpf_000000000000.tmp"));
            var rootPrefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
                ? canonicalRoot
                : canonicalRoot + Path.DirectorySeparatorChar;
            if (fullPath.Length > FixtureBackupRestoreProofFile.MaximumPathCharacters ||
                temporaryPath.Length > FixtureBackupRestoreProofFile.MaximumPathCharacters ||
                !fullPath.StartsWith(rootPrefix, comparison) ||
                !string.Equals(Path.GetDirectoryName(fullPath), canonicalRoot, comparison))
            {
                throw Failure("backup_restore_proof_path_invalid");
            }
            return new FixtureBackupRestoreProofLocation(canonicalRoot, fileName, fullPath);
        }
        catch (FixtureConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new FixtureConfigurationException("backup_restore_proof_path_invalid", exception);
        }
    }

    private static bool ValidLeaf(string value)
    {
        if (value.Length is < 6 or > FixtureBackupRestoreProofFile.MaximumLeafCharacters ||
            Encoding.UTF8.GetByteCount(value) > FixtureBackupRestoreProofFile.MaximumLeafUtf8Bytes ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) ||
            !value.EndsWith(".json", StringComparison.Ordinal) ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')))
        {
            return false;
        }

        var baseName = value.Split('.', 2)[0];
        return !ReservedWindowsNames.Contains(baseName) &&
            !value.Contains(':') &&
            !value.EndsWith(' ') &&
            !value.EndsWith('.');
    }

    private static bool ValidRootSegments(string rootPath)
    {
        if (!OperatingSystem.IsWindows())
            return true;
        var volumeRoot = Path.GetPathRoot(rootPath);
        if (string.IsNullOrEmpty(volumeRoot) || volumeRoot.StartsWith("\\\\", StringComparison.Ordinal))
            return false;
        var relative = rootPath[volumeRoot.Length..];
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var baseName = segment.Split('.', 2)[0];
            if (segment.Contains(':') ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                ReservedWindowsNames.Contains(baseName))
            {
                return false;
            }
        }
        return true;
    }

    private static FixtureConfigurationException Failure(string code) => new(code);
}

/// <summary>
/// Bounded file handoff. Publishing uses a same-directory temporary file followed by an
/// atomic, no-overwrite rename so an interrupted capture is never accepted as a proof.
/// </summary>
internal static class FixtureBackupRestoreProofFile
{
    public const int MaximumPathCharacters = 240;
    public const int MaximumLeafCharacters = 64;
    public const int MaximumLeafUtf8Bytes = 255;
    public const int MaximumProofBytes = 64 * 1024;
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void ValidateCaptureTarget(FixtureBackupRestoreProofLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        ValidateTrustedRoot(location);
        if (EntryExistsOrIsLink(location.FullPath))
            throw Failure("backup_restore_proof_exists");
    }

    public static FixtureBackupRestoreProofV1 Read(FixtureBackupRestoreProofLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        ValidateExistingProofFile(location);
        try
        {
            using var stream = new FileStream(
                location.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length is < 2 or > MaximumProofBytes)
                throw Failure("backup_restore_proof_size_invalid");

            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            var proof = CortexCanonicalJson.Deserialize<FixtureBackupRestoreProofV1>(bytes);
            FixtureBackupRestoreProofContract.EnsureValid(proof);
            if (!CortexCanonicalJson.Serialize(proof).AsSpan().SequenceEqual(bytes))
                throw Failure("backup_restore_proof_not_canonical");
            return proof;
        }
        catch (FixtureConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new FixtureConfigurationException("backup_restore_proof_unavailable", exception);
        }
        catch (Exception exception) when (
            exception is System.Text.Json.JsonException or NotSupportedException or OverflowException)
        {
            throw new FixtureConfigurationException("backup_restore_proof_invalid", exception);
        }
    }

    public static void WriteNew(
        FixtureBackupRestoreProofLocation location,
        FixtureBackupRestoreProofV1 proof)
    {
        ArgumentNullException.ThrowIfNull(location);
        FixtureBackupRestoreProofContract.EnsureValid(proof);
        var bytes = CortexCanonicalJson.Serialize(proof);
        if (bytes.Length is < 2 or > MaximumProofBytes)
            throw Failure("backup_restore_proof_size_invalid");
        ValidateCaptureTarget(location);

        var temporaryPath = Path.Combine(
            location.RootPath,
            ".ctxpf_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant() + ".tmp");
        Exception? writeFailure = null;
        var ownsTemporaryPath = false;
        try
        {
            using (var stream = OpenPrivateNewFile(temporaryPath))
            {
                ownsTemporaryPath = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, location.FullPath, overwrite: false);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(location.FullPath, PrivateFileMode);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            writeFailure = exception;
        }

        try
        {
            if (ownsTemporaryPath && File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        catch (Exception cleanupFailure) when (
            cleanupFailure is IOException or UnauthorizedAccessException)
        {
            throw new FixtureConfigurationException(
                "backup_restore_proof_cleanup_failed",
                writeFailure is null
                    ? cleanupFailure
                    : new AggregateException(writeFailure, cleanupFailure));
        }

        if (writeFailure is not null)
        {
            throw new FixtureConfigurationException(
                EntryExistsOrIsLink(location.FullPath)
                    ? "backup_restore_proof_exists"
                    : "backup_restore_proof_write_failed",
                writeFailure);
        }
    }

    private static void ValidateTrustedRoot(FixtureBackupRestoreProofLocation location)
    {
        try
        {
            var root = new DirectoryInfo(location.RootPath);
            root.Refresh();
            if (!root.Exists ||
                !root.Attributes.HasFlag(FileAttributes.Directory) ||
                root.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                root.LinkTarget is not null)
            {
                throw Failure("backup_restore_proof_root_unsafe");
            }

            for (DirectoryInfo? current = root; current is not null; current = current.Parent)
            {
                current.Refresh();
                if (!current.Exists ||
                    current.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    current.LinkTarget is not null)
                {
                    throw Failure("backup_restore_proof_ancestor_unsafe");
                }
            }

            if (!OperatingSystem.IsWindows() &&
                File.GetUnixFileMode(location.RootPath) != PrivateDirectoryMode)
            {
                throw Failure("backup_restore_proof_root_permissions_invalid");
            }
        }
        catch (FixtureConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new FixtureConfigurationException("backup_restore_proof_root_unavailable", exception);
        }
    }

    private static void ValidateExistingProofFile(FixtureBackupRestoreProofLocation location)
    {
        ValidateTrustedRoot(location);
        try
        {
            var file = new FileInfo(location.FullPath);
            file.Refresh();
            if (file.LinkTarget is not null)
                throw Failure("backup_restore_proof_file_unsafe");
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(location.FullPath);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new FixtureConfigurationException("backup_restore_proof_unavailable", exception);
            }
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw Failure("backup_restore_proof_file_unsafe");
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.Device))
            {
                throw Failure("backup_restore_proof_file_not_regular");
            }
            if (!OperatingSystem.IsWindows() &&
                File.GetUnixFileMode(location.FullPath) != PrivateFileMode)
            {
                throw Failure("backup_restore_proof_file_permissions_invalid");
            }
        }
        catch (FixtureConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FixtureConfigurationException("backup_restore_proof_unavailable", exception);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new FixtureConfigurationException("backup_restore_proof_file_unavailable", exception);
        }
    }

    private static FileStream OpenPrivateNewFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = PrivateFileMode;
        return new FileStream(path, options);
    }

    private static bool EntryExistsOrIsLink(string path)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null)
            return true;
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static FixtureConfigurationException Failure(string code) => new(code);
}
