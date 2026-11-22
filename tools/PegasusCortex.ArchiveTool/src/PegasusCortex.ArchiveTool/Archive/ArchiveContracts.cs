using System.Formats.Tar;

namespace Pegasus.Cortex.ArchiveTool.Archive;

/// <summary>
/// Describes one hash-pinned archive extraction into a new direct child of a trusted root.
/// The destination name is intentionally a single portable segment so the authority boundary
/// remains narrow and cleanup never owns an unrelated parent directory.
/// </summary>
public sealed record ArchiveExtractionRequest(
    string ArchivePath,
    string ExpectedSha256,
    string DestinationRoot,
    string DestinationName,
    string InventoryPath,
    ArchiveSafetyLimits? Limits = null);

/// <summary>
/// Fail-closed archive limits. Callers may tighten these defaults, but the extractor rejects
/// values above the hard policy ceilings defined here.
/// </summary>
public sealed record ArchiveSafetyLimits
{
    public const long HardMaximumCompressedBytes = 200L * 1024 * 1024;
    public const long HardMaximumTarStreamBytes = 640L * 1024 * 1024;
    public const long HardMaximumFileBytes = 128L * 1024 * 1024;
    public const long HardMaximumTotalFileBytes = 512L * 1024 * 1024;
    public const int HardMaximumEntries = 10_000;
    public const double HardMaximumExpansionRatio = 100d;
    public const int HardMaximumCanonicalPathUtf16 = 240;
    public const int HardMaximumRelativePathUtf8 = 4_096;
    public const int HardMaximumSegmentUtf8 = 255;
    public const int HardMaximumSegments = 64;

    public long MaximumCompressedBytes { get; init; } = HardMaximumCompressedBytes;

    public long MaximumTarStreamBytes { get; init; } = HardMaximumTarStreamBytes;

    public long MaximumFileBytes { get; init; } = HardMaximumFileBytes;

    public long MaximumTotalFileBytes { get; init; } = HardMaximumTotalFileBytes;

    public int MaximumEntries { get; init; } = HardMaximumEntries;

    public double MaximumExpansionRatio { get; init; } = HardMaximumExpansionRatio;

    public int MaximumCanonicalPathUtf16 { get; init; } = HardMaximumCanonicalPathUtf16;

    public int MaximumRelativePathUtf8 { get; init; } = HardMaximumRelativePathUtf8;

    public int MaximumSegmentUtf8 { get; init; } = HardMaximumSegmentUtf8;

    public int MaximumSegments { get; init; } = HardMaximumSegments;
}

/// <summary>
/// Summarizes a committed extraction. Absolute paths are returned to the caller but are not
/// serialized into the deterministic inventory.
/// </summary>
public sealed record ArchiveExtractionResult(
    string DestinationPath,
    string InventoryPath,
    string ArchiveSha256,
    long CompressedBytes,
    long TarStreamBytes,
    long TotalFileBytes,
    int ArchiveEntryCount,
    int MaterializedEntryCount);

/// <summary>
/// A stable, machine-readable failure. Unsafe archive-controlled names are never copied into
/// the public message or CLI diagnostics.
/// </summary>
public sealed class ArchiveBoundaryException : Exception
{
    internal ArchiveBoundaryException(
        string code,
        string message,
        Exception? innerException = null,
        int? entryIndex = null,
        string? primaryCode = null,
        IReadOnlyList<Exception>? cleanupFailures = null)
        : base(message)
    {
        Code = code;
        EntryIndex = entryIndex;
        PrimaryCode = primaryCode;
        CleanupFailureCount = cleanupFailures?.Count ?? 0;
    }

    public string Code { get; }

    public int? EntryIndex { get; }

    public string? PrimaryCode { get; }

    public int CleanupFailureCount { get; }
}

internal enum MaterializedEntryKind
{
    Directory,
    File
}

internal sealed record ArchiveEntryRecord(
    int Index,
    string RawName,
    string RelativePath,
    MaterializedEntryKind? Kind,
    TarEntryType EntryType,
    TarEntryFormat Format,
    long Length,
    UnixFileMode Mode,
    string? Sha256,
    bool IsArchiveRoot);

internal sealed record MaterializedEntryRecord(
    string RelativePath,
    MaterializedEntryKind Kind,
    long Length,
    UnixFileMode Mode,
    string? Sha256);

internal sealed record ArchivePreflight(
    string ArchiveSha256,
    long CompressedBytes,
    long TarStreamBytes,
    long TotalFileBytes,
    IReadOnlyList<ArchiveEntryRecord> ArchiveEntries,
    IReadOnlyList<MaterializedEntryRecord> MaterializedEntries);

/// <summary>
/// Internal fault points keep cleanup and no-overwrite behavior executable without weakening
/// the production CLI surface.
/// </summary>
internal sealed record ArchiveExtractionHooks(
    Action<int, string>? BeforeMaterializeEntry = null,
    Action<string>? BeforeCleanupDelete = null,
    Action? AfterDestinationCommit = null,
    Action? AfterInventoryCommit = null);
