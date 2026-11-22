using System.Runtime.InteropServices;
using Pegasus.Cortex.Setup;

internal static class RobocopyTransferTests
{
    internal static void Run(string root)
    {
        var empty = new CortexRobocopyTransfer.Capture("", false);
        foreach (var code in new[] { 0, 1, 2, 3 }) CortexRobocopyTransfer.RequireComplete(new(code, empty, empty));
        foreach (var code in new[] { 4, 5, 6, 7, 8, 16, -1 })
        {
            var failure = Throws<IOException>(() => CortexRobocopyTransfer.RequireComplete(new(code, empty, empty)));
            Check(failure.Message.Contains(code is >= 4 and <= 7 ? "mismatched" : "copy failure", StringComparison.Ordinal), "Native failure classification differs");
        }
        var bounded = CortexRobocopyTransfer.DrainAsync(new StringReader(new string('x', 8192) + "tail"), 64).GetAwaiter().GetResult();
        Check(bounded.Truncated && bounded.Text.Length == 64 && bounded.Text.EndsWith("tail", StringComparison.Ordinal), "Native output was not drained into a bounded tail");
        var arguments = CortexRobocopyTransfer.Arguments(@"C:\source", @"C:\destination");
        Check(new[] { "/E", "/COPY:DAT", "/DCOPY:DAT", "/MT:8", "/R:0", "/W:0", "/XF", "/SL", "/SJ" }
            .All(arguments.Contains) && !arguments.Any(value => value is "/MIR" or "/PURGE" or "/MOVE" or "/MOV" or "/REG"), "Bulk copy lost its fixed non-destructive argument policy");
        var special = new[] { "C:\\program path\\tool.exe", "C:\\trailing\\", "embedded\"quote", "", "literal & % Ω" };
        var parsed = CommandLineToArgvW(string.Join(" ", special.Select(CortexRobocopyTransfer.Quote)), out var count);
        if (parsed == IntPtr.Zero) throw new Exception("Native command-line parser unavailable");
        try
        {
            Check(count == special.Length && Enumerable.Range(0, count).All(index =>
                Marshal.PtrToStringUni(Marshal.ReadIntPtr(parsed, index * IntPtr.Size)) == special[index]), "Separate argument quoting changed values");
        }
        finally { _ = LocalFree(parsed); }

        var source = Path.Combine(root, "bulk source Ω &");
        var destination = Path.Combine(root, "bulk target");
        Directory.CreateDirectory(Path.Combine(source, "directory.db"));
        CortexQuiescentDatabaseSnapshot.CreatePrivateDirectory(destination);
        foreach (var name in new[] { "literal & percent%.txt", "extensionless", "ordinary.db.notes", "prefix.sqlite3-shm.tmp", "directory.db\\child.txt" })
            File.WriteAllText(Path.Combine(source, name), name);
        foreach (var name in new[] { "held.db", "held.db-wal", "held.db-shm", "held.db-journal", "UPPER.DB", "UPPER.DB-WAL", "orphan.sqlite3-shm" })
            File.WriteAllText(Path.Combine(source, name), "excluded native DB fixture");
        File.WriteAllText(Path.Combine(destination, "keep-extra.txt"), "retained");
        var copied = CortexRobocopyTransfer.Copy(source, destination, CancellationToken.None);
        CortexRobocopyTransfer.RequireComplete(copied);
        Check((copied.ExitCode & 1) != 0 && File.ReadAllText(Path.Combine(destination, "keep-extra.txt")) == "retained", "Native copied result or extra-file preservation differs");
        foreach (var name in new[] { "literal & percent%.txt", "extensionless", "ordinary.db.notes", "prefix.sqlite3-shm.tmp", "directory.db\\child.txt" })
            Check(File.ReadAllText(Path.Combine(destination, name)) == name && File.Exists(Path.Combine(source, name)), "Native copy lost an ordinary source file");
        Check(!File.Exists(Path.Combine(destination, "held.db")) && !File.Exists(Path.Combine(destination, "held.db-wal")) &&
            !File.Exists(Path.Combine(destination, "UPPER.DB")) && !File.Exists(Path.Combine(destination, "UPPER.DB-WAL")) &&
            !File.Exists(Path.Combine(destination, "orphan.sqlite3-shm")), "Database family escaped native exclusion");

        var lockedSource = Path.Combine(source, "locked.txt"); File.WriteAllText(lockedSource, "retained locked source");
        var lockedTarget = Path.Combine(root, "bulk locked"); CortexQuiescentDatabaseSnapshot.CreatePrivateDirectory(lockedTarget);
        using (var writer = new FileStream(lockedSource, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failed = CortexRobocopyTransfer.Copy(source, lockedTarget, CancellationToken.None);
            Check(failed.ExitCode >= 8, "Native locked-source copy did not report failure");
            Throws<IOException>(() => CortexRobocopyTransfer.RequireComplete(failed));
        }
        Check(File.ReadAllText(lockedSource) == "retained locked source", "Native failure changed its original source");

        var cancelledTarget = Path.Combine(root, "bulk cancelled");
        CortexQuiescentDatabaseSnapshot.CreatePrivateDirectory(cancelledTarget);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); var started = false;
            Throws<OperationCanceledException>(() => CortexRobocopyTransfer.Copy(source, cancelledTarget, cancellation.Token,
                started: _ => started = true));
            Check(!started && !Directory.EnumerateFileSystemEntries(cancelledTarget).Any(), "Pre-cancelled copy launched work");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var failure = Throws<OperationCanceledException>(() => CortexRobocopyTransfer.Copy(source, cancelledTarget, cancellation.Token,
                started: _ => cancellation.Cancel()));
            Check(failure.Data["NativeExitCode"] is int code && code != 259 && failure.Data["ChildStopRequested"] is bool,
                "Cancelled copy did not retain original-child exit evidence");
        }
        var timedTarget = Path.Combine(root, "bulk deadline");
        CortexQuiescentDatabaseSnapshot.CreatePrivateDirectory(timedTarget);
        var timed = Throws<TimeoutException>(() => CortexRobocopyTransfer.Copy(source, timedTarget, CancellationToken.None,
            TimeSpan.FromMilliseconds(1), _ => Thread.Sleep(20)));
        Check(timed.Data["NativeExitCode"] is int timedCode && timedCode != 259, "Deadline did not join its original child");
        Console.WriteLine("PASS fixed System32 Robocopy isolated native copy/exclusions/no-delete/argv and bounded-output/exit/cancellation/deadline checks; no installed state");
    }

    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T failure) { return failure; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
