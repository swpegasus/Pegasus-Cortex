using Pegasus.Cortex.Product;
using Pegasus.Cortex.Setup;
using System.Text;

internal static class LogRetentionTests
{
    internal static void Run(string root)
    {
        var now = DateTimeOffset.UtcNow;
        var own = Path.Combine(root, "log-retention");
        Directory.CreateDirectory(own);
        Rotation(own, now);
        ReporterClosureAndRename(own);
        AgeAndExcludedFamilies(own, now);
        CountAndSize(own, now);
        Console.WriteLine("PASS diagnostic_log_retention (pure policy and owned files; no installed cleanup)");
    }

    private static void Rotation(string root, DateTimeOffset now)
    {
        var today = now.UtcDateTime.Date;
        Check(CortexLogRotation.ShouldRotate(1, today.AddDays(-1), 64, now), "Prior UTC day did not rotate.");
        Check(!CortexLogRotation.ShouldRotate(64, today, 64, now), "Same-day threshold rotated early.");
        Check(CortexLogRotation.ShouldRotate(65, today, 64, now), "Size rollover was lost.");
        Check(!CortexLogRotation.ShouldRotate(1, today.AddDays(1), 64, now), "Future timestamp caused repeated rollover.");
        var directory = Path.Combine(root, "two-slots");
        Directory.CreateDirectory(directory);
        var current = Path.Combine(directory, "host.log");
        var previous = Path.Combine(directory, "host.previous.log");
        const string original = "2026-01-01T00:00:00.0000000+00:00 event=owned_fixture\n";
        File.WriteAllText(current, original);
        File.SetLastWriteTimeUtc(current, today.AddDays(-1));
        Check(CortexLogRotation.RotateIfNeeded(current, previous, 256 * 1024, now) &&
            File.ReadAllText(previous) == original && !File.Exists(current), "Day rollover altered stored UTC bytes.");
        File.WriteAllText(current, "today\n");
        File.SetLastWriteTimeUtc(current, today);
        Check(!CortexLogRotation.RotateIfNeeded(current, previous, 256 * 1024, now), "Same-day small log rolled again.");
        File.WriteAllText(current, new string('x', 65));
        File.SetLastWriteTimeUtc(current, today);
        Check(CortexLogRotation.RotateIfNeeded(current, previous, 64, now) &&
            new FileInfo(previous).Length == 65 && Directory.GetFiles(directory).Length == 1,
            "Size rollover did not reuse the two existing slots.");
    }

    private static void ReporterClosureAndRename(string root)
    {
        var console = CortexSetupReporter.ConsoleOnly();
        console.Close();
        Check(console.LogPath is null, "Console-only closure invented persistence.");
        var original = Path.Combine(root, "reporter-stage");
        var final = Path.Combine(root, "reporter-final");
        var reporter = CortexSetupReporter.ConsoleOnly().PersistUnder(original);
        reporter.Stage("fixture", "started");
        var firstBytes = File.ReadAllBytes(reporter.LogPath!);
        var detached = reporter.WithoutPersistence();
        detached.Stage("fixture", "copying");
        Check(firstBytes.SequenceEqual(File.ReadAllBytes(reporter.LogPath!)), "Detached reporter wrote to the old state name.");
        Directory.Move(original, final);
        var selected = detached.PersistUnder(final);
        Check(selected.Rid == reporter.Rid && firstBytes.SequenceEqual(File.ReadAllBytes(selected.LogPath!)),
            "Stage-to-final logging lost its RID or original lines.");
        selected.Failure("fixture", "owned", null, new IOException("synthetic diagnostic"), "fixture only");
        var diagnostic = Path.Combine(final, "logs", $"setup-{selected.Rid}.failure.json");
        Check(File.Exists(diagnostic), "The matching diagnostic failure member was not written.");
        selected.Close();
        var closed = File.ReadAllBytes(selected.LogPath!);
        Check(Encoding.UTF8.GetString(closed).Contains("stage=setup-log status=closed rid=" + selected.Rid,
            StringComparison.Ordinal), "Logger closure marker was not written.");
        selected.Close();
        reporter.Stage("fixture", "after-close");
        Check(closed.SequenceEqual(File.ReadAllBytes(selected.LogPath!)), "A shared closed logger appended again.");

        var busy = CortexSetupReporter.ConsoleOnly().PersistUnder(Path.Combine(root, "close-busy"));
        busy.Stage("fixture", "started");
        using (var held = new FileStream(busy.LogPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            busy.Close(); // Best effort: a log conflict never changes the caller's outcome.
        Check(!File.ReadAllText(busy.LogPath!).Contains("status=closed", StringComparison.Ordinal),
            "A failed close invented a persisted closure marker.");
    }

    private static void AgeAndExcludedFamilies(string root, DateTimeOffset now)
    {
        var directory = Path.Combine(root, "age");
        Directory.CreateDirectory(directory);
        var oldClosed = Family(directory, 1, now.AddDays(-31), closed: true);
        var oldUnclosed = Family(directory, 2, now.AddDays(-31), closed: false);
        var current = Family(directory, 3, now.AddDays(-31), closed: true);
        var active = Family(directory, 4, now.AddDays(-31), closed: true);
        var recentUnknown = Family(directory, 5, now, closed: false);
        var unrelated = Path.Combine(directory, "execution-recovery.v1.json");
        File.WriteAllText(unrelated, "retained recovery metadata");
        var nested = Path.Combine(directory, "nested");
        Directory.CreateDirectory(nested);
        var nestedLog = Family(nested, 6, now.AddDays(-31), closed: true);
        using (var held = new FileStream(active, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            Check(CortexSetupLogRetention.Prune(directory, Rid(3), now) == 2,
                "Age pruning did not remove only stale inactive diagnostic families.");
            Check(!File.Exists(oldClosed) && !File.Exists(oldUnclosed) &&
                !File.Exists(Path.ChangeExtension(oldClosed, ".failure.json")) &&
                !File.Exists(Path.ChangeExtension(oldUnclosed, ".failure.json")), "A diagnostic family was only partly pruned.");
            Check(File.Exists(current) && File.Exists(active) && File.Exists(recentUnknown) &&
                File.ReadAllText(unrelated) == "retained recovery metadata" && File.Exists(nestedLog),
                "Retention touched current/open/recent-unknown/nested/recovery files.");
        }
        Check(CortexSetupLogRetention.TryRid("setup-" + Rid(8) + ".log", out _) &&
            !CortexSetupLogRetention.TryRid("setup-not-a-guid.log", out _) &&
            !CortexSetupLogRetention.TryRid("setup-" + Rid(8) + ".failure.json", out _),
            "Diagnostic filename admission broadened beyond the exact GUID log family.");
    }

    private static void CountAndSize(string root, DateTimeOffset now)
    {
        var countRoot = Path.Combine(root, "count");
        Directory.CreateDirectory(countRoot);
        for (var index = 0; index < 35; index++)
            Family(countRoot, 100 + index, now.AddMinutes(-100 + index), closed: true);
        var unknown = Family(countRoot, 200, now, closed: false);
        Check(CortexSetupLogRetention.Prune(countRoot, Rid(999), now) == 3 &&
            Directory.GetFiles(countRoot, "setup-*.log").Length == 33 && File.Exists(unknown),
            "Count retention did not keep 32 closed families plus the recent unclosed family.");
        Check(!File.Exists(Path.Combine(countRoot, "setup-" + Rid(100) + ".log")) &&
            File.Exists(Path.Combine(countRoot, "setup-" + Rid(134) + ".log")), "Count retention did not remove oldest families first.");

        var sizeRoot = Path.Combine(root, "size");
        Directory.CreateDirectory(sizeRoot);
        var oldest = Family(sizeRoot, 300, now.AddHours(-2), closed: true, padding: 9 * 1024 * 1024);
        var newest = Family(sizeRoot, 301, now.AddHours(-1), closed: true, padding: 9 * 1024 * 1024);
        Check(CortexSetupLogRetention.Prune(sizeRoot, Rid(999), now) == 1 &&
            !File.Exists(oldest) && File.Exists(newest), "Size retention did not trim oldest closed diagnostics.");
    }

    private static string Family(string directory, int index, DateTimeOffset utc, bool closed, long padding = 0)
    {
        var rid = Rid(index);
        var path = Path.Combine(directory, $"setup-{rid}.log");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(padding);
        File.AppendAllText(path, "\n" + (closed
            ? $"stage=setup-log status=closed rid={rid} utc={utc:O}"
            : $"stage=fixture status=started rid={rid}") + "\n", new UTF8Encoding(false));
        var failure = Path.Combine(directory, $"setup-{rid}.failure.json");
        File.WriteAllText(failure, "{\"contract\":\"pegasus-cortex-setup-failure\",\"ok\":false}\n");
        File.SetLastWriteTimeUtc(path, utc.UtcDateTime);
        File.SetLastWriteTimeUtc(failure, utc.UtcDateTime);
        return path;
    }

    private static string Rid(int index) => index.ToString("x32");
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
