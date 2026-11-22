using System.Runtime.InteropServices;
using Pegasus.Cortex.OfflineJournal.Paths;

namespace Pegasus.Cortex.OfflineJournal.Platform.Linux;

/// <summary>
/// Creates Linux sessions only when the requested and approved roots are the same pre-existing
/// canonical path. Platform and syscall failures remain fail-closed; no managed path fallback is
/// available from this package. Journal mutation is intended for a dedicated service UID that
/// exclusively owns the 0700 root; every cooperating writer must retain the journal's exclusive
/// writer lease for the complete mutation. Linux rename and unlink remain name-based operations,
/// so the host must not run untrusted processes under that service UID.
/// </summary>
public sealed class LinuxJournalFileSystemBoundaryFactory : IJournalFileSystemBoundaryFactory
{
    public static LinuxJournalFileSystemBoundaryFactory Instance { get; } = new();

    public JournalFileSystemBoundarySafety Safety =>
        JournalFileSystemBoundarySafety.HandleRelativeNoFollow;

    public IJournalFileSystemBoundary OpenJournalRoot(string requestedRoot, string approvedRoot)
    {
        EnsureSupportedPlatform("open_journal_root");
        var canonicalRoot = AuthorizeCanonicalRoot(requestedRoot, approvedRoot, "open_journal_root");
        return new LinuxJournalFileSystemBoundary(
            LinuxRootedFileSystemSession.Open(
                canonicalRoot,
                LinuxRootEntryPolicy.JournalOwned,
                "open_journal_root"));
    }

    public ILegacySourceFileSystemBoundary OpenLegacySourceRoot(
        string requestedRoot,
        string approvedRoot)
    {
        EnsureSupportedPlatform("open_legacy_source_root");
        var canonicalRoot = AuthorizeCanonicalRoot(
            requestedRoot,
            approvedRoot,
            "open_legacy_source_root");
        return new LinuxLegacySourceFileSystemBoundary(
            LinuxRootedFileSystemSession.Open(
                canonicalRoot,
                LinuxRootEntryPolicy.ReadOnlyLegacySource,
                "open_legacy_source_root"));
    }

    private static void EnsureSupportedPlatform(string operation)
    {
        if (!OperatingSystem.IsLinux() ||
            RuntimeInformation.ProcessArchitecture is not (Architecture.Arm64 or Architecture.X64))
        {
            throw new LinuxJournalFileSystemException(
                operation,
                LinuxJournalIoCause.PlatformUnsupported);
        }
    }

    private static string AuthorizeCanonicalRoot(
        string requestedRoot,
        string approvedRoot,
        string operation)
    {
        if (string.IsNullOrWhiteSpace(requestedRoot) ||
            string.IsNullOrWhiteSpace(approvedRoot) ||
            requestedRoot.Any(char.IsControl) ||
            approvedRoot.Any(char.IsControl) ||
            !requestedRoot.StartsWith("/", StringComparison.Ordinal) ||
            !approvedRoot.StartsWith("/", StringComparison.Ordinal) ||
            requestedRoot.StartsWith("//", StringComparison.Ordinal) ||
            approvedRoot.StartsWith("//", StringComparison.Ordinal))
        {
            throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.InvalidArgument);
        }

        string requested;
        string approved;
        try
        {
            requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedRoot));
            approved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(approvedRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            _ = exception;
            // Path exceptions may include the submitted root. Preserve the stable category while
            // deliberately omitting the path-derived exception graph.
            throw new LinuxJournalFileSystemException(
                operation,
                LinuxJournalIoCause.InvalidArgument);
        }

        if (requested == "/" ||
            !string.Equals(requestedRoot, requested, StringComparison.Ordinal) ||
            !string.Equals(approvedRoot, approved, StringComparison.Ordinal) ||
            !string.Equals(requested, approved, StringComparison.Ordinal))
        {
            throw new LinuxJournalFileSystemException(operation, LinuxJournalIoCause.InvalidArgument);
        }
        return requested;
    }
}
