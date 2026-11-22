namespace Pegasus.Cortex.OfflineJournal.Platform.Linux;

/// <summary>
/// Stable failure categories emitted by the Linux filesystem boundary. Categories retain the
/// failed operation and native cause without embedding journal paths or record content.
/// </summary>
public enum LinuxJournalIoCause
{
    PlatformUnsupported,
    SystemCallUnavailable,
    InvalidRelativePath,
    NotFound,
    AlreadyExists,
    AccessDenied,
    LinkRejected,
    CrossDevice,
    InvalidEntryType,
    OwnershipMismatch,
    PermissionMismatch,
    HardLinkRejected,
    EnumerationLimitExceeded,
    LockBusy,
    DirectoryNotEmpty,
    ResourceLimit,
    InvalidArgument,
    IoFailure,
    Unknown
}

/// <summary>
/// Actionable, path-redacted error returned by a native Linux journal operation.
/// </summary>
public sealed class LinuxJournalFileSystemException : IOException
{
    internal LinuxJournalFileSystemException(
        string operation,
        LinuxJournalIoCause cause,
        int? nativeErrorCode = null,
        Exception? innerException = null)
        : base(BuildMessage(operation, cause, nativeErrorCode), innerException)
    {
        Operation = operation;
        Cause = cause;
        NativeErrorCode = nativeErrorCode;
    }

    public string Operation { get; }

    public LinuxJournalIoCause Cause { get; }

    public int? NativeErrorCode { get; }

    internal static LinuxJournalFileSystemException FromErrno(string operation, int errno) =>
        new(operation, MapErrno(operation, errno), errno);

    internal static LinuxJournalFileSystemException Unavailable(string operation, Exception exception) =>
        new(operation, LinuxJournalIoCause.SystemCallUnavailable, innerException: exception);

    private static string BuildMessage(string operation, LinuxJournalIoCause cause, int? nativeErrorCode) =>
        nativeErrorCode is null
            ? $"Linux journal operation '{operation}' failed with cause '{cause}'."
            : $"Linux journal operation '{operation}' failed with cause '{cause}' (errno {nativeErrorCode.Value}).";

    private static LinuxJournalIoCause MapErrno(string operation, int errno) => errno switch
    {
        1 or 13 => LinuxJournalIoCause.AccessDenied,
        2 => LinuxJournalIoCause.NotFound,
        11 when operation == "acquire_lock" => LinuxJournalIoCause.LockBusy,
        11 => LinuxJournalIoCause.IoFailure,
        17 => LinuxJournalIoCause.AlreadyExists,
        18 => LinuxJournalIoCause.CrossDevice,
        20 or 21 => LinuxJournalIoCause.InvalidEntryType,
        22 => LinuxJournalIoCause.InvalidArgument,
        23 or 24 or 37 => LinuxJournalIoCause.ResourceLimit,
        36 => LinuxJournalIoCause.InvalidRelativePath,
        38 or 95 => LinuxJournalIoCause.SystemCallUnavailable,
        39 => LinuxJournalIoCause.DirectoryNotEmpty,
        40 => LinuxJournalIoCause.LinkRejected,
        5 or 28 or 30 => LinuxJournalIoCause.IoFailure,
        _ => LinuxJournalIoCause.Unknown
    };
}
