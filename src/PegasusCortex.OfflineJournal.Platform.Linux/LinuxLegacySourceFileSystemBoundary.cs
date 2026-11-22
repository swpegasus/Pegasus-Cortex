using Pegasus.Cortex.OfflineJournal.Paths;

namespace Pegasus.Cortex.OfflineJournal.Platform.Linux;

internal sealed class LinuxLegacySourceFileSystemBoundary(
    LinuxRootedFileSystemSession session) : ILegacySourceFileSystemBoundary
{
    public JournalFileSystemBoundarySafety Safety =>
        JournalFileSystemBoundarySafety.HandleRelativeNoFollow;

    public string RootPath => session.RootPath;

    public string AuthorizedRootIdentity => session.RootIdentity;

    public string OpenedRootIdentity => session.RootIdentity;

    public IReadOnlyList<JournalFileSystemEntry> EnumerateDirectory(
        string relativePath,
        int maximumEntries) =>
        session.EnumerateDirectory(relativePath, maximumEntries);

    public FileStream OpenReadFile(
        string relativePath,
        int bufferSize,
        bool asynchronous,
        bool sequentialScan) =>
        session.OpenReadFile(relativePath, bufferSize, asynchronous, sequentialScan);

    public void Dispose() => session.Dispose();
}
