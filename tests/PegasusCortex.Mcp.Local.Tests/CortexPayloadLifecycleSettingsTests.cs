using System.Text;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Mcp.Local.Runtime;

internal static class CortexPayloadLifecycleSettingsTests
{
    private static int _checks;
    internal static Task RunAsync()
    {
        _checks = 0;
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cortex-policy-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            ValidateBounds();
            PersistenceAndCas(Path.Combine(root, "valid"));
            PublicationRecovery(Path.Combine(root, "publication"));
            MalformedPreservation(Path.Combine(root, "invalid"));
            PathAdmission(Path.Combine(root, "paths"));
        }
        finally
        {
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Check(root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("cortex-policy-", StringComparison.Ordinal), "owned scratch boundary");
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
                Check((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "owned cleanup entries ordinary");
            Directory.Delete(root, recursive: true);
        }
        Check(!Directory.Exists(root), "owned scratch released");
        Console.WriteLine($"PASS payload lifecycle settings: {_checks} pure and owned-file checks; disabled defaults, closed bounded policy, cloned namespaces, atomic replacement with old read snapshot, revision CAS, bounded partial-publication recovery, malformed preservation and path admission; injected1176/1177 states, fixture ACL checks disabled, no installed settings/runtime action");
        return Task.CompletedTask;
    }

    private static void ValidateBounds()
    {
        var defaults = CortexPayloadLifecycleSettingsStore.DefaultSettings;
        Check(defaults.Revision == 0 && !defaults.Policy.Enabled && defaults.Policy.Namespaces.Count == 0 &&
            defaults.Policy.MinimumArchiveAgeDays == 30 && defaults.Policy.TargetHotMiB == 0 && defaults.Policy.IntervalMinutes == 60 &&
            defaults.Policy.WorkMilliseconds == 1000 && defaults.Policy.MaximumMetadataHeads == 16384, "declared disabled defaults");
        var namespaces = new[] { "project.first" };
        var frozen = CortexPayloadLifecycleSettingsStore.Validate(defaults.Policy with { Enabled = true, Namespaces = namespaces });
        namespaces[0] = "changed";
        Check(frozen.Namespaces[0] == "project.first", "validation snapshots caller namespace list");
        foreach (var invalid in new[]
        {
            defaults.Policy with { Version = 2 }, defaults.Policy with { Enabled = true },
            defaults.Policy with { Namespaces = ["bad value"] }, defaults.Policy with { Namespaces = ["same", "same"] },
            defaults.Policy with { Namespaces = [new string('a', 129)] },
            defaults.Policy with { Namespaces = Enumerable.Range(0, 9).Select(i => "project." + i).ToArray() },
            defaults.Policy with { MinimumArchiveAgeDays = 0 }, defaults.Policy with { MinimumArchiveAgeDays = 36501 },
            defaults.Policy with { TargetHotMiB = -1 }, defaults.Policy with { TargetHotMiB = 16L * 1024 * 1024 + 1 },
            defaults.Policy with { IntervalMinutes = 0 }, defaults.Policy with { IntervalMinutes = 1441 },
            defaults.Policy with { WorkMilliseconds = 49 }, defaults.Policy with { WorkMilliseconds = 10001 },
            defaults.Policy with { MaximumMetadataHeads = 255 }, defaults.Policy with { MaximumMetadataHeads = 65537 }
        }) Throws<ArgumentException>(() => CortexPayloadLifecycleSettingsStore.Validate(invalid), "closed policy range");
        var limits = CortexPayloadLifecycleSettingsStore.Validate(defaults.Policy with
        {
            Enabled = true, Namespaces = Enumerable.Range(0, 8).Select(i => "project." + i).ToArray(),
            MinimumArchiveAgeDays = 36500, TargetHotMiB = 16L * 1024 * 1024, IntervalMinutes = 1440,
            WorkMilliseconds = 10000, MaximumMetadataHeads = 65536
        });
        Check(limits.Namespaces.Count == 8 && limits.TargetHotMiB == 16777216, "upper bounds accepted without silent clamping");
    }

    private static void PersistenceAndCas(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, CortexPayloadLifecycleSettingsStore.FileName);
        var store = new CortexPayloadLifecycleSettingsStore(root, protect: false);
        Check(store.Read().Revision == 0 && Directory.GetFileSystemEntries(root).Length == 0, "absent read does not provision/write defaults");
        var policy = CortexPayloadLifecycleSettingsStore.DefaultSettings.Policy with { Enabled = true, Namespaces = ["project.first", "knowledge.shared"] };
        var first = store.Save(policy, 0);
        Check(first.Revision == 1 && File.Exists(path), "first atomic save revision1");
        var reopened = new CortexPayloadLifecycleSettingsStore(root, protect: false).Read();
        Check(reopened.Revision == 1 && reopened.Policy.Enabled && reopened.Policy.Namespaces.SequenceEqual(policy.Namespaces), "saved policy survives fresh reader");
        var original = File.ReadAllBytes(path);
        Throws<InvalidOperationException>(() => store.Save(policy with { Enabled = false }, 0), "stale CAS refused");
        Check(File.ReadAllBytes(path).SequenceEqual(original), "stale save leaves exact policy bytes unchanged");
        var lockPath = Path.Combine(root, ".payload-lifecycle.lock");
        using (var occupied = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            Throws<IOException>(() => store.Save(policy with { Enabled = false }, 1), "occupied writer lock refuses once");
            Check(File.ReadAllBytes(path).SequenceEqual(original), "busy save has no policy publication");
        }
        File.Delete(lockPath);
        // Existing read handles retain their snapshot while atomic replacement
        // publishes another valid revision for future readers.
        using (var old = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            var second = store.Save(policy with { Enabled = false, MinimumArchiveAgeDays = 40 }, 1);
            var oldBytes = new byte[checked((int)old.Length)]; old.ReadExactly(oldBytes);
            Check(oldBytes.SequenceEqual(original) && second.Revision == 2 && !store.Read().Policy.Enabled, "atomic replacement preserves old reader and new revision");
        }
        Check(Directory.GetFiles(root).Length == 1 && !File.Exists(lockPath), "no retained staging or lock files after success");
    }

    private static void MalformedPreservation(string root)
    {
        Directory.CreateDirectory(root);
        var store = new CortexPayloadLifecycleSettingsStore(root, protect: false);
        var path = Path.Combine(root, CortexPayloadLifecycleSettingsStore.FileName);
        store.Save(CortexPayloadLifecycleSettingsStore.DefaultSettings.Policy, 0);
        var original = File.ReadAllText(path);
        var values = new List<string>
        {
            "{}", original.Replace("\"revision\":1", "\"revision\":1,\"revision\":2", StringComparison.Ordinal),
            original.Replace("\"enabled\":false", "\"enabled\":false,\"enabled\":true", StringComparison.Ordinal),
            original.Replace("\"schema\":", "\"unknown\":0,\"schema\":", StringComparison.Ordinal),
            original.Replace("\"workMilliseconds\":1000", "\"workMilliseconds\":10001", StringComparison.Ordinal),
            original.Replace("\"revision\":1", "\"revision\":0", StringComparison.Ordinal),
            new string('x', CortexPayloadLifecycleSettingsStore.MaximumBytes + 1), "\"not an object\""
        };
        var missing = JsonNode.Parse(original)!.AsObject(); missing["policy"]!.AsObject().Remove("minimumArchiveAgeDays"); values.Add(missing.ToJsonString());
        foreach (var malformed in values)
        {
            File.WriteAllText(path, malformed, new UTF8Encoding(false));
            var before = File.ReadAllBytes(path);
            Throws<InvalidDataException>(() => store.Read(), "malformed policy explicit error");
            Throws<InvalidDataException>(() => store.Save(CortexPayloadLifecycleSettingsStore.DefaultSettings.Policy, 0), "save never resets unreadable/malformed policy");
            Check(File.ReadAllBytes(path).SequenceEqual(before), "invalid source preserved exactly");
        }
    }

    private static void PublicationRecovery(string root)
    {
        foreach (var errorCode in new[] { 1176, 1177 })
        {
            var directory = Path.Combine(root, "error-" + errorCode); Directory.CreateDirectory(directory);
            var selected = Path.Combine(directory, CortexPayloadLifecycleSettingsStore.FileName);
            var pending = Path.Combine(directory, CortexPayloadLifecycleSettingsStore.PendingFileName);
            var previous = Path.Combine(directory, CortexPayloadLifecycleSettingsStore.PreviousFileName);
            var initial = new CortexPayloadLifecycleSettingsStore(directory, protect: false);
            initial.Save(CortexPayloadLifecycleSettingsStore.DefaultSettings.Policy, 0);
            var original = File.ReadAllBytes(selected);
            var replacements = 0;
            var interrupted = new CortexPayloadLifecycleSettingsStore(directory, protect: false, replaceFile: (candidate, main, backup) =>
            {
                replacements++;
                // Model the documented names left by the failed replace. These
                // are actual moves of this fixture's own old file, not a product
                // retry or an alternative publication algorithm.
                if (errorCode == 1177) File.Move(main, backup, overwrite: false);
                throw new IOException("synthetic partial replacement", new System.ComponentModel.Win32Exception(errorCode));
            });
            var requested = CortexPayloadLifecycleSettingsStore.DefaultSettings.Policy with
                { Enabled = true, Namespaces = ["project.fixture"] };
            var failed = Publication(() => interrupted.Save(requested, 1), "payload_policy_recovery_required");
            Check(failed.ConfirmedSettings is null && replacements == 1 && File.Exists(pending), "failed replacement preserves bounded pending candidate");
            Check(JsonNode.Parse(File.ReadAllText(pending))!["revision"]!.GetValue<long>() == 2,
                "the retained candidate is the requested revision rather than an empty marker");
            if (errorCode == 1176)
                Check(File.ReadAllBytes(selected).SequenceEqual(original) && !File.Exists(previous), "1176 retains original selected name and bytes");
            else Check(!File.Exists(selected) && File.ReadAllBytes(previous).SequenceEqual(original),
                "1177 retains the previous bytes even when selected name is absent");
            Check(Publication(() => interrupted.Read(), "payload_policy_recovery_required").ConfirmedSettings is null,
                "partial publication is never revision-zero or a confirmed policy read");
            Publication(() => interrupted.Save(requested, errorCode == 1177 ? 0 : 1), "payload_policy_recovery_required");
            Check(replacements == 1 && Directory.GetFiles(directory).Length == 2 && File.Exists(pending),
                "subsequent save neither replays publication nor deletes recovery evidence");
        }

        var cleanupDirectory = Path.Combine(root, "cleanup"); Directory.CreateDirectory(cleanupDirectory);
        var cleanupPath = Path.Combine(cleanupDirectory, CortexPayloadLifecycleSettingsStore.FileName);
        var cleanupPrevious = Path.Combine(cleanupDirectory, CortexPayloadLifecycleSettingsStore.PreviousFileName);
        var normal = new CortexPayloadLifecycleSettingsStore(cleanupDirectory, protect: false);
        normal.Save(CortexPayloadLifecycleSettingsStore.DefaultSettings.Policy, 0);
        var oldBytes = File.ReadAllBytes(cleanupPath);
        var cleanupFault = new CortexPayloadLifecycleSettingsStore(cleanupDirectory, protect: false, replaceFile: (candidate, main, backup) =>
        {
            File.Replace(candidate, main, backup, ignoreMetadataErrors: false);
            File.SetAttributes(backup, File.GetAttributes(backup) | FileAttributes.ReadOnly);
        });
        var enabled = CortexPayloadLifecycleSettingsStore.DefaultSettings.Policy with { Enabled = true, Namespaces = ["project.fixture"] };
        try
        {
            var known = Publication(() => cleanupFault.Save(enabled, 1), "payload_policy_cleanup_required");
            Check(known.ConfirmedSettings is { Revision: 2, Policy.Enabled: true } &&
                File.ReadAllBytes(cleanupPrevious).SequenceEqual(oldBytes), "cleanup failure retains verified selected policy and exact predecessor");
            var observed = Publication(() => normal.Read(), "payload_policy_cleanup_required");
            Check(observed.ConfirmedSettings is { Revision: 2 }, "linked actual selected bytes remain inspectable through typed cleanup issue");
            Throws<InvalidOperationException>(() => normal.Save(enabled, 1), "stale explicit save cannot clean a successor it has not observed");
            Check(File.Exists(cleanupPrevious), "stale CAS preserves previous recovery entry");
        }
        finally
        {
            if (File.Exists(cleanupPrevious)) File.SetAttributes(cleanupPrevious, FileAttributes.Normal);
        }
        var afterReview = normal.Save(enabled with { MinimumArchiveAgeDays = 40 }, 2);
        Check(afterReview.Revision == 3 && normal.Read().Policy.MinimumArchiveAgeDays == 40 &&
            Directory.GetFiles(cleanupDirectory).Length == 1,
            "fresh explicit CAS save cleans only a verified predecessor and creates a new policy revision");

        var pristine = Path.Combine(root, "pristine-pending"); Directory.CreateDirectory(pristine);
        var pristinePending = Path.Combine(pristine, CortexPayloadLifecycleSettingsStore.PendingFileName);
        File.WriteAllText(pristinePending, "incomplete candidate", new UTF8Encoding(false));
        var stopped = new CortexPayloadLifecycleSettingsStore(pristine, protect: false);
        Publication(() => stopped.Read(), "payload_policy_recovery_required");
        Publication(() => stopped.Save(CortexPayloadLifecycleSettingsStore.DefaultSettings.Policy, 0), "payload_policy_recovery_required");
        Check(!File.Exists(Path.Combine(pristine, CortexPayloadLifecycleSettingsStore.FileName)) &&
            File.ReadAllText(pristinePending) == "incomplete candidate", "orphan candidate is not default state or cleanup permission");

        var legacyDirectory = Path.Combine(root, "unlinked-previous"); Directory.CreateDirectory(legacyDirectory);
        var legacyStore = new CortexPayloadLifecycleSettingsStore(legacyDirectory, protect: false);
        legacyStore.Save(CortexPayloadLifecycleSettingsStore.DefaultSettings.Policy, 0);
        var legacyPath = Path.Combine(legacyDirectory, CortexPayloadLifecycleSettingsStore.FileName);
        var legacyPrevious = Path.Combine(legacyDirectory, CortexPayloadLifecycleSettingsStore.PreviousFileName);
        var legacyFirst = File.ReadAllBytes(legacyPath);
        var unlinked = JsonNode.Parse(Encoding.UTF8.GetString(legacyFirst))!.AsObject(); unlinked["revision"] = 2;
        File.WriteAllText(legacyPath, unlinked.ToJsonString(), new UTF8Encoding(false));
        Check(legacyStore.Read().Revision == 2, "valid legacy document stays readable without inventing a predecessor link");
        File.WriteAllBytes(legacyPrevious, legacyFirst);
        Check(Publication(() => legacyStore.Read(), "payload_policy_recovery_required").ConfirmedSettings is null,
            "unlinked previous file is not treated as a verified predecessor");
        Publication(() => legacyStore.Save(enabled, 2), "payload_policy_recovery_required");
        Check(File.ReadAllBytes(legacyPrevious).SequenceEqual(legacyFirst), "unclassifiable predecessor is retained without implicit rollback");
    }

    private static void PathAdmission(string root)
    {
        Directory.CreateDirectory(root);
        Throws<ArgumentException>(() => new CortexPayloadLifecycleSettingsStore(Path.Combine(root, "..", "other"), protect: false), "noncanonical root refused");
        Throws<PathTooLongException>(() => new CortexPayloadLifecycleSettingsStore(Path.Combine(root, new string('a', 210)), protect: false), "over-budget configuration root refused");
        var boundaryRoot = Path.Combine(root, new string('a', 239 - root.Length - 1));
        Throws<ArgumentException>(() => new CortexPayloadLifecycleSettingsStore(boundaryRoot, protect: false), "generated paths obey240 budget");
        Throws<DirectoryNotFoundException>(() => new CortexPayloadLifecycleSettingsStore(Path.Combine(root, "absent"), protect: false).Read(), "missing directory is not missing policy");
        var occupied = Path.Combine(root, "occupied"); Directory.CreateDirectory(occupied);
        Directory.CreateDirectory(Path.Combine(occupied, CortexPayloadLifecycleSettingsStore.FileName));
        Throws<IOException>(() => new CortexPayloadLifecycleSettingsStore(occupied, protect: false), "directory at policy file refused");
    }

    private static void Check(bool value, string name) { _checks++; if (!value) throw new InvalidOperationException("Lifecycle settings fixture: " + name); }
    private static void Throws<T>(Action action, string name) where T : Exception
    { try { action(); } catch (T) { Check(true, name); return; } throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + name); }
    private static CortexPayloadLifecycleSettingsPublicationException Publication(Action action, string code)
    {
        try { action(); }
        catch (CortexPayloadLifecycleSettingsPublicationException error) when (error.Code == code)
        { Check(true, code); return error; }
        throw new InvalidOperationException("Expected publication issue: " + code);
    }
}
