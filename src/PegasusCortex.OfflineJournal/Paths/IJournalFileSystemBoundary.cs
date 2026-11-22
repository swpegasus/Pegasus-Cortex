using Pegasus.Cortex.OfflineJournal.Contracts;

namespace Pegasus.Cortex.OfflineJournal.Paths;

/// <summary>
/// Describes the filesystem guarantee supplied by a trusted host adapter. Lexical checks are
/// useful for deterministic tests, but they do not prevent a path component from being swapped
/// after validation. Production journal mutation therefore requires handle-relative, no-follow
/// operations owned by the boundary implementation.
/// </summary>
public enum JournalFileSystemBoundarySafety
{
    LexicalTestOnly = 0,
    HandleRelativeNoFollow = 1,

    /// <summary>
    /// Path-based operations confined to an owner-private local root. This is
    /// suitable only for the standalone single-user trust boundary: it rejects
    /// links/reparse points at each operation but does not defend against a
    /// malicious process already running as the same OS user.
    /// </summary>
    ProtectedLocalSingleUser = 2
}

public enum JournalFileSystemEntryKind
{
    RegularFile,
    Directory
}

/// <summary>
/// One entry returned from a bounded, root-anchored directory enumeration.
/// ExactPath is provenance/display data only; callers must use RelativePath for later boundary
/// operations so an absolute path is never promoted into write authority.
/// </summary>
public sealed record JournalFileSystemEntry(
    string RelativePath,
    string ExactPath,
    JournalFileSystemEntryKind Kind);

/// <summary>
/// Trusted host factory for journal-owned and legacy-source filesystem sessions. The production
/// guarantee is a capability claim: implementations must anchor operations to already-authorized
/// directory handles and reject links/reparse points at the operation itself.
/// </summary>
public interface IJournalFileSystemBoundaryFactory
{
    JournalFileSystemBoundarySafety Safety { get; }

    IJournalFileSystemBoundary OpenJournalRoot(string requestedRoot, string approvedRoot);

    ILegacySourceFileSystemBoundary OpenLegacySourceRoot(
        string requestedRoot,
        string approvedRoot);
}

/// <summary>
/// Root-relative journal operations. A production implementation must perform each operation
/// relative to its authorized directory handle with no-follow semantics; preflight checks alone
/// do not satisfy <see cref="JournalFileSystemBoundarySafety.HandleRelativeNoFollow"/>.
/// </summary>
public interface IJournalFileSystemBoundary : IDisposable
{
    JournalFileSystemBoundarySafety Safety { get; }
    JournalDurabilityCapabilities Durability { get; }
    string RootPath { get; }
    // Opaque stable identities supplied by the trusted host. Production adapters bind these to
    // the approved directory object and the actually opened directory handle respectively.
    string AuthorizedRootIdentity { get; }
    string OpenedRootIdentity { get; }

    void EnsureDirectory(string relativePath);
    void CreateDirectory(string relativePath);
    bool DirectoryExists(string relativePath);
    bool FileExists(string relativePath);
    JournalFileSystemEntryKind GetEntryKind(string relativePath);
    FileStream AcquireExclusiveLock(string relativePath);
    FileStream CreateNewFile(string relativePath, int bufferSize, bool asynchronous);
    FileStream OpenReadFile(string relativePath, int bufferSize, bool asynchronous, bool sequentialScan);
    IReadOnlyList<JournalFileSystemEntry> EnumerateDirectory(string relativePath, int maximumEntries);
    // Must be an atomic same-filesystem rename that fails when the destination exists.
    void MoveDirectory(string sourceRelativePath, string destinationRelativePath);
    void DeleteFile(string relativePath);
    void DeleteEmptyDirectory(string relativePath);
}

/// <summary>
/// Optional narrow capability for a same-filesystem directory promotion that is guaranteed to
/// survive a completed call and power loss. Implementations may use parent-directory fsync or an
/// operating-system write-through move; ordinary atomic rename alone does not satisfy it. A
/// failed call leaves source/destination state uncertain, so callers must reconcile both names.
/// </summary>
public interface IJournalDurableDirectoryPromotion
{
    bool DurableDirectoryPromotionSupported { get; }

    void MoveDirectoryDurably(
        string sourceRelativePath,
        string destinationRelativePath);
}

/// <summary>
/// Read-only, root-relative legacy-source capture. Production implementations must enumerate and
/// open without following links and must keep all operations anchored to the approved source root.
/// </summary>
public interface ILegacySourceFileSystemBoundary : IDisposable
{
    JournalFileSystemBoundarySafety Safety { get; }
    string RootPath { get; }
    string AuthorizedRootIdentity { get; }
    string OpenedRootIdentity { get; }

    IReadOnlyList<JournalFileSystemEntry> EnumerateDirectory(string relativePath, int maximumEntries);
    FileStream OpenReadFile(string relativePath, int bufferSize, bool asynchronous, bool sequentialScan);
}

internal static class JournalFileSystemBoundaryGate
{
    public static (JournalPathPolicy Paths, IJournalFileSystemBoundary Boundary) OpenJournal(
        IJournalFileSystemBoundaryFactory factory,
        string requestedRoot,
        string approvedRoot,
        bool allowLexicalTestOnly)
    {
        ArgumentNullException.ThrowIfNull(factory);
        RequireSafety(factory.Safety, allowLexicalTestOnly, nameof(factory));
        RequireExactRequestedAndApprovedRoots(requestedRoot, approvedRoot);
        var boundary = factory.OpenJournalRoot(requestedRoot, approvedRoot)
            ?? throw new InvalidOperationException("The filesystem-boundary factory returned no journal session.");
        try
        {
            RequireSafety(boundary.Safety, allowLexicalTestOnly, nameof(boundary));
            if (boundary.Safety != factory.Safety)
                throw new InvalidOperationException("The filesystem factory and opened boundary disagree about their safety guarantee.");
            RequireExactAuthorizedRoot(
                requestedRoot,
                approvedRoot,
                boundary.RootPath,
                boundary.AuthorizedRootIdentity,
                boundary.OpenedRootIdentity);
            return (JournalPathPolicy.FromBoundaryRoot(boundary.RootPath), boundary);
        }
        catch
        {
            boundary.Dispose();
            throw;
        }
    }

    public static ILegacySourceFileSystemBoundary OpenLegacySource(
        IJournalFileSystemBoundaryFactory factory,
        string requestedRoot,
        string approvedRoot,
        bool allowLexicalTestOnly)
    {
        ArgumentNullException.ThrowIfNull(factory);
        RequireSafety(factory.Safety, allowLexicalTestOnly, nameof(factory));
        RequireExactRequestedAndApprovedRoots(requestedRoot, approvedRoot);
        var boundary = factory.OpenLegacySourceRoot(requestedRoot, approvedRoot)
            ?? throw new InvalidOperationException("The filesystem-boundary factory returned no legacy-source session.");
        try
        {
            RequireSafety(boundary.Safety, allowLexicalTestOnly, nameof(boundary));
            if (boundary.Safety != factory.Safety)
                throw new InvalidOperationException("The filesystem factory and legacy-source boundary disagree about their safety guarantee.");
            RequireExactAuthorizedRoot(
                requestedRoot,
                approvedRoot,
                boundary.RootPath,
                boundary.AuthorizedRootIdentity,
                boundary.OpenedRootIdentity);
            return boundary;
        }
        catch
        {
            boundary.Dispose();
            throw;
        }
    }

    private static void RequireSafety(
        JournalFileSystemBoundarySafety safety,
        bool allowLexicalTestOnly,
        string parameterName)
    {
        if (safety is
            JournalFileSystemBoundarySafety.HandleRelativeNoFollow or
            JournalFileSystemBoundarySafety.ProtectedLocalSingleUser)
            return;
        if (allowLexicalTestOnly && safety == JournalFileSystemBoundarySafety.LexicalTestOnly)
            return;
        throw new NotSupportedException(
            $"{parameterName} does not provide handle-relative, no-follow filesystem authority. " +
            "The lexical implementation is available only to the friend test assembly.");
    }

    private static void RequireExactAuthorizedRoot(
        string requestedRoot,
        string approvedRoot,
        string boundaryRoot,
        string authorizedRootIdentity,
        string openedRootIdentity)
    {
        RequireExactRequestedAndApprovedRoots(requestedRoot, approvedRoot);
        if (string.IsNullOrWhiteSpace(boundaryRoot) || boundaryRoot.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(boundaryRoot))
        {
            throw new ArgumentException("Requested, approved, and boundary roots must be fully-qualified canonical paths.");
        }

        var requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedRoot));
        var opened = Path.TrimEndingDirectorySeparator(Path.GetFullPath(boundaryRoot));
        if (!ValidRootIdentity(authorizedRootIdentity) || !ValidRootIdentity(openedRootIdentity) ||
            !string.Equals(authorizedRootIdentity, openedRootIdentity, StringComparison.Ordinal) ||
            !string.Equals(boundaryRoot, opened, StringComparison.Ordinal) ||
            !string.Equals(requested, opened, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "The filesystem boundary did not open the exact requested and authority-approved canonical root.");
        }
    }

    private static void RequireExactRequestedAndApprovedRoots(string requestedRoot, string approvedRoot)
    {
        if (string.IsNullOrWhiteSpace(requestedRoot) || string.IsNullOrWhiteSpace(approvedRoot) ||
            requestedRoot.Any(char.IsControl) || approvedRoot.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(requestedRoot) || !Path.IsPathFullyQualified(approvedRoot))
        {
            throw new ArgumentException("Requested and approved roots must be fully-qualified canonical paths.");
        }
        var requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedRoot));
        var approved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(approvedRoot));
        if (!string.Equals(requestedRoot, requested, StringComparison.Ordinal) ||
            !string.Equals(approvedRoot, approved, StringComparison.Ordinal) ||
            !string.Equals(requested, approved, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The requested and authority-approved roots must be the same canonical path.");
        }
    }

    private static bool ValidRootIdentity(string? value) =>
        value is { Length: > 0 and <= 512 } &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);
}
