using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

public sealed class CortexSetupReporter
{
    private sealed class RunState
    {
        internal readonly object Gate = new();
        internal bool Closed;
    }
    private readonly RunState _state;
    private readonly string? _logPath;

    private CortexSetupReporter(string rid, string? logPath, RunState? state = null)
    {
        Rid = rid;
        _logPath = logPath;
        _state = state ?? new RunState();
    }

    public string Rid { get; }
    public string? LogPath => _logPath;

    // Moving the state tree must not leave a writer pointing at its old name.
    public CortexSetupReporter WithoutPersistence() => new(Rid, null, _state);

    public static CortexSetupReporter ConsoleOnly() =>
        new(Guid.NewGuid().ToString("N"), null);

    public CortexSetupReporter PersistUnder(string installStateDirectory)
    {
        var directory = Path.Combine(installStateDirectory, "logs");
        Directory.CreateDirectory(directory);
        CortexSetupLogRetention.Prune(directory, Rid, DateTimeOffset.UtcNow);
        return new CortexSetupReporter(Rid, Path.Combine(directory, $"setup-{Rid}.log"), _state);
    }

    internal CortexSetupReporter PersistForElevatedRegisteredOperation(string installRoot)
    {
        if (!OperatingSystem.IsWindows()) return this;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return this; // Ordinary user startup never requires protected log writes.
        var root = CortexCodexFileUpdate.RequireCanonical(installRoot);
        var ownerSid = identity.User?.Value ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        // Setup commonly executes from an admitted package beside, rather than
        // beneath, the installed product. Resolve the registered layout from
        // the explicit target root; deriving it from AppContext would mistake
        // a complete candidate package for the active installation.
        var layout = CortexProductLayout.ForInstallRoot(root, ownerSid);
        if (!string.Equals(root, layout.InstallRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Setup logging requires the registered Cortex installation.");
        CortexCodexFileUpdate.RequireDirectory(layout.InstallStateDirectory, allowMissing: false);
        CortexProductStateProvisioner.RequireInstallReceiptBoundary(layout);
        var logs = Path.Combine(layout.InstallStateDirectory, "logs");
        CortexCodexFileUpdate.RequireDirectory(logs, allowMissing: true);
        if (Directory.Exists(logs))
        {
            var security = new DirectoryInfo(logs).GetAccessControl();
            if (security.GetOwner(typeof(SecurityIdentifier))?.Value is not ("S-1-5-18" or "S-1-5-32-544"))
                throw new UnauthorizedAccessException("The setup log directory is not administrator-owned.");
            const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete |
                FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0 &&
                    rule.IdentityReference.Value is not ("S-1-5-18" or "S-1-5-32-544"))
                    throw new UnauthorizedAccessException("The setup log directory is writable by an unowned principal.");
        }
        return PersistUnder(layout.InstallStateDirectory);
    }

    public void Stage(string stage, string status, string? detail = null)
    {
        var safeStage = Token(stage);
        var safeStatus = Token(status);
        var safeDetail = detail is null ? null : OneLine(detail, 384);
        var line = $"stage={safeStage} status={safeStatus}" +
            (safeDetail is null ? string.Empty : $" detail={Uri.EscapeDataString(safeDetail)}");
        var persistedLine = $"{line} rid={Rid}";
        lock (_state.Gate)
        {
            Console.WriteLine(line);
            Console.Out.Flush();
            if (_logPath is not null && !_state.Closed)
                File.AppendAllText(_logPath, persistedLine + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    public void Failure(
        string error,
        string? stage,
        string? target,
        Exception cause,
        string remedy)
    {
        Stage(stage ?? "setup", "failed", $"{error}: {cause.GetType().Name}");
        var cleanup = CortexWindowsProductRegistration.CleanupFailures(cause);
        foreach (var failure in cleanup)
            Stage("registration-cleanup", "failed", $"step={failure.Stage}; cause={failure.Cause}; win32={failure.Win32}");
        var obsolete = cause as CortexObsoleteProgramCleanupException;
        var nativeError = (obsolete?.InnerException as System.ComponentModel.Win32Exception)?.NativeErrorCode;
        if (obsolete is not null)
            Stage(obsolete.Stage, "failed", $"cause={obsolete.InnerException?.GetType().Name}; win32={nativeError}; removed={obsolete.Receipt.RemovedFiles}");
        if (_logPath is null)
            return;
        var receipt = Path.Combine(Path.GetDirectoryName(_logPath)!, $"setup-{Rid}.failure.json");
        var payload = JsonSerializer.Serialize(new
        {
            contract = "pegasus-cortex-setup-failure",
            version = 1,
            rid = Rid,
            ok = false,
            err = error,
            stage,
            target,
            cause = cause.GetType().Name,
            diagnostic = OneLine(cause.Message, 512),
            cleanup,
            programCleanup = obsolete is null ? null : new
            {
                stage = obsolete.Stage,
                cause = obsolete.InnerException?.GetType().Name,
                win32 = nativeError,
                removed = obsolete.Receipt.RemovedFiles,
                missing = obsolete.Receipt.MissingFiles,
                preservedChanged = obsolete.Receipt.PreservedChangedFiles,
                preservedUnknown = obsolete.Receipt.PreservedUnknownEntries
            },
            remedy,
            utc = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions
        { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }) + "\n";
        lock (_state.Gate)
            if (!_state.Closed) File.WriteAllText(receipt, payload, new UTF8Encoding(false));
    }

    // This closes only the logger, never certifies an operation or external effect.
    // Each append is still closed immediately; relocation retains no open handle.
    public void Close()
    {
        try
        {
            lock (_state.Gate)
            {
                if (_state.Closed) return;
                _state.Closed = true;
                if (_logPath is not null && File.Exists(_logPath))
                    File.AppendAllText(_logPath,
                        CortexSetupLogRetention.ClosedLine(Rid, DateTimeOffset.UtcNow) + Environment.NewLine,
                        new UTF8Encoding(false));
            }
        }
        catch { /* Diagnostic retention must never change setup's exit or failure. */ }
    }

    private static string Token(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("A setup progress token is invalid.", nameof(value));
        return value;
    }

    private static string OneLine(string value, int maximum) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim() is var single && single.Length <= maximum
            ? single
            : single[..maximum];
}

// Only the diagnostic setup-<GUID>.log/.failure.json family is eligible.
// No recursion, source-state scan, hard quota, or recovery-record deletion.
internal static class CortexSetupLogRetention
{
    internal const int MaximumFamilies = 32;
    internal const long MaximumBytes = 16 * 1024 * 1024;
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);
    private sealed record Family(string Rid, string Log, string Failure, long Bytes, DateTime UpdatedUtc, bool Closed);

    internal static string ClosedLine(string rid, DateTimeOffset now) =>
        $"stage=setup-log status=closed rid={rid} utc={now:O}";

    internal static bool TryRid(string name, out string rid)
    {
        rid = "";
        if (name.Length != 42 || !name.StartsWith("setup-", StringComparison.Ordinal) ||
            !name.EndsWith(".log", StringComparison.Ordinal)) return false;
        var value = name.Substring(6, 32);
        if (!Guid.TryParseExact(value, "N", out _)) return false;
        rid = value;
        return true;
    }

    internal static int Prune(string directory, string currentRid, DateTimeOffset now)
    {
        var removed = 0;
        try
        {
            directory = CortexCodexFileUpdate.RequireCanonical(directory);
            CortexCodexFileUpdate.RequireDirectory(directory, allowMissing: false);
            var cutoff = now.UtcDateTime - MaximumAge;
            var candidates = new List<Family>();
            foreach (var log in Directory.EnumerateFiles(directory, "setup-*.log", SearchOption.TopDirectoryOnly))
            {
                if (!TryRid(Path.GetFileName(log), out var rid) ||
                    string.Equals(rid, currentRid, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var logFile = OpenOrdinary(log);
                    var failure = Path.Combine(directory, $"setup-{rid}.failure.json");
                    using var failureFile = OpenOptional(failure);
                    var family = Describe(rid, log, failure, logFile, failureFile);
                    // Legacy/interrupted logs are expendable by age only, never
                    // inferred closed for count/size retention.
                    if (family.Closed || family.UpdatedUtc < cutoff) candidates.Add(family);
                }
                catch (Exception error) when (Skippable(error)) { }
            }

            var closed = candidates.Count(item => item.Closed);
            var bytes = candidates.Where(item => item.Closed).Sum(item => item.Bytes);
            foreach (var family in candidates.OrderBy(item => item.UpdatedUtc).ThenBy(item => item.Rid, StringComparer.Ordinal))
            {
                if (family.UpdatedUtc >= cutoff &&
                    (!family.Closed || closed <= MaximumFamilies && bytes <= MaximumBytes)) continue;
                if (!TryDelete(family)) continue;
                removed++;
                if (family.Closed) { closed--; bytes -= family.Bytes; }
            }
        }
        catch (Exception error) when (Skippable(error)) { }
        return removed;
    }

    private static bool TryDelete(Family expected)
    {
        try
        {
            // Sharing permits deletion by this cleanup while excluding any
            // currently open reader/writer and preventing a new writer.
            using var log = OpenOrdinary(expected.Log);
            using var failure = OpenOptional(expected.Failure);
            var current = Describe(expected.Rid, expected.Log, expected.Failure, log, failure);
            if (current != expected) return false;
            if (failure is not null) File.Delete(expected.Failure);
            File.Delete(expected.Log);
            return true;
        }
        catch (Exception error) when (Skippable(error)) { return false; }
    }

    private static Family Describe(string rid, string path, string failurePath, FileStream log, FileStream? failure)
    {
        var updated = File.GetLastWriteTimeUtc(path);
        if (failure is not null)
        {
            var failureTime = File.GetLastWriteTimeUtc(failurePath);
            if (failureTime > updated) updated = failureTime;
        }
        var closed = ClosedAt(log, rid);
        if (closed is { } instant && instant.UtcDateTime > updated) updated = instant.UtcDateTime;
        return new(rid, path, failurePath, checked(log.Length + (failure?.Length ?? 0)), updated, closed is not null);
    }

    private static DateTimeOffset? ClosedAt(FileStream stream, string rid)
    {
        var count = (int)Math.Min(1024, stream.Length);
        var buffer = new byte[count];
        stream.Position = stream.Length - count;
        stream.ReadExactly(buffer);
        var tail = Encoding.UTF8.GetString(buffer).TrimEnd('\r', '\n');
        var last = tail[(tail.LastIndexOf('\n') + 1)..].TrimEnd('\r');
        var prefix = $"stage=setup-log status=closed rid={rid} utc=";
        return last.StartsWith(prefix, StringComparison.Ordinal) &&
            DateTimeOffset.TryParseExact(last[prefix.Length..], "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp) && timestamp.Offset == TimeSpan.Zero
            ? timestamp : null;
    }

    private static FileStream? OpenOptional(string path)
    {
        try { return OpenOrdinary(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static FileStream OpenOrdinary(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("A setup diagnostic member is not an ordinary file.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
    }

    private static bool Skippable(Exception error) => error is IOException or UnauthorizedAccessException or
        ArgumentException or InvalidDataException or System.Security.SecurityException;
}
