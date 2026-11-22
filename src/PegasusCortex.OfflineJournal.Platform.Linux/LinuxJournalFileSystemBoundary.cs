using Pegasus.Cortex.OfflineJournal.Contracts;
using Pegasus.Cortex.OfflineJournal.Paths;

namespace Pegasus.Cortex.OfflineJournal.Platform.Linux;

internal sealed class LinuxJournalFileSystemBoundary(
    LinuxRootedFileSystemSession session) :
    IJournalFileSystemBoundary,
    IJournalDurableDirectoryPromotion
{
    public JournalFileSystemBoundarySafety Safety =>
        JournalFileSystemBoundarySafety.HandleRelativeNoFollow;

    public JournalDurabilityCapabilities Durability { get; } = new(
        FileContentFlush: true,
        AtomicSameVolumePromotion: true,
        ParentDirectoryFlush: true,
        Guarantee: "Linux file contents support flush-to-disk, directory promotion uses renameat2 RENAME_NOREPLACE within the approved mount, and create/rename/delete mutations fsync their parent directories.");

    public string RootPath => session.RootPath;

    public string AuthorizedRootIdentity => session.RootIdentity;

    public string OpenedRootIdentity => session.RootIdentity;

    public bool DurableDirectoryPromotionSupported => true;

    public void EnsureDirectory(string relativePath) => session.EnsureDirectory(relativePath);

    public void CreateDirectory(string relativePath) => session.CreateDirectory(relativePath);

    public bool DirectoryExists(string relativePath) =>
        session.EntryExists(relativePath, JournalFileSystemEntryKind.Directory, "directory_exists");

    public bool FileExists(string relativePath) =>
        session.EntryExists(relativePath, JournalFileSystemEntryKind.RegularFile, "file_exists");

    public JournalFileSystemEntryKind GetEntryKind(string relativePath) =>
        session.GetEntryKind(relativePath);

    public FileStream AcquireExclusiveLock(string relativePath) =>
        session.AcquireExclusiveLock(relativePath);

    public FileStream CreateNewFile(string relativePath, int bufferSize, bool asynchronous) =>
        session.CreateNewFile(relativePath, bufferSize, asynchronous);

    public FileStream OpenReadFile(
        string relativePath,
        int bufferSize,
        bool asynchronous,
        bool sequentialScan) =>
        session.OpenReadFile(relativePath, bufferSize, asynchronous, sequentialScan);

    public IReadOnlyList<JournalFileSystemEntry> EnumerateDirectory(
        string relativePath,
        int maximumEntries) =>
        session.EnumerateDirectory(relativePath, maximumEntries);

    public void MoveDirectory(string sourceRelativePath, string destinationRelativePath) =>
        session.MoveDirectory(sourceRelativePath, destinationRelativePath);

    public void MoveDirectoryDurably(
        string sourceRelativePath,
        string destinationRelativePath) =>
        session.MoveDirectory(sourceRelativePath, destinationRelativePath);

    public void DeleteFile(string relativePath) => session.DeleteFile(relativePath);

    public void DeleteEmptyDirectory(string relativePath) =>
        session.DeleteEmptyDirectory(relativePath);

    public void Dispose() => session.Dispose();
}
