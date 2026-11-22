using Pegasus.Cortex.Mcp.Local.Offline;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Persistence.File.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Contracts.Serialization;
using System.Text.Json;

internal static class OfflineOrchestrationTests
{
    private const string TaskOne = "codex-thread-019c-task-one";
    private const string TaskTwo = "codex-thread-019c-task-two";

    public static async Task CatalogRestartAndIdempotentRegistrationAsync()
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "cortex-offline-catalog-tests",
            Guid.NewGuid().ToString("N")));
        var project = System.IO.Path.Combine(root, "project with spaces");
        var legacyRoot = System.IO.Path.Combine(root, "legacy project");
        var config = System.IO.Path.Combine(root, "config");
        var catalogPath = System.IO.Path.Combine(config, "offline-projects.json");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(legacyRoot);
        Directory.CreateDirectory(config);
        CortexLocalRegistryAclPolicy.ProtectDirectory(project);
        CortexLocalRegistryAclPolicy.ProtectDirectory(legacyRoot);
        try
        {
            var manifest = new CortexFileProjectManifestV2(
                CortexFileProjectManifestV2.CurrentSchema,
                "offline-project",
                "project.offline",
                ["project.offline", "shared.programming"]);
            var legacyRegistration = new LegacyProjectRegistration(
                "legacy-project",
                legacyRoot,
                CortexCanonicalJson.Sha256(System.Text.Encoding.UTF8.GetBytes(legacyRoot)),
                "project.legacy",
                ["project.legacy"]);
            var legacyZero = new LegacyCatalogDocument(
                CortexOfflineProjectCatalog.LegacySchema,
                CortexOfflineProjectCatalog.Canonicalization,
                DateTimeOffset.UnixEpoch,
                [legacyRegistration],
                new string('0', 64));
            var legacy = legacyZero with
            {
                CatalogHash = CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(legacyZero))
            };
            await File.WriteAllBytesAsync(catalogPath, CortexCanonicalJson.Serialize(legacy));

            var catalog = new CortexOfflineProjectCatalog(catalogPath, TimeProvider.System);
            var first = await catalog.RegisterAsync(project, TaskOne, manifest, CancellationToken.None);
            var reopened = new CortexOfflineProjectCatalog(catalogPath, TimeProvider.System);
            var second = await reopened.RegisterAsync(project, TaskOne, manifest, CancellationToken.None);
            var sibling = await reopened.RegisterAsync(project, TaskTwo, manifest, CancellationToken.None);
            var document = await reopened.ReadAsync(CancellationToken.None);
            LocalAssert.True(
                document.Projects.Count == 3 && first.ProjectId == second.ProjectId &&
                sibling.TaskId == TaskTwo && document.Projects.All(item => item.ReadNamespaces.SequenceEqual(
                    item.ProjectId == "legacy-project" ? ["project.legacy"] : manifest.ReadNamespaces,
                    StringComparer.Ordinal)),
                "The task/project catalog did not preserve composite registration or replay idempotency.");
            var legacyRejected = false;
            try
            {
                await reopened.RequireNoLegacyRootAsync(legacyRoot, CancellationToken.None);
            }
            catch (InvalidOperationException exception)
            {
                legacyRejected = exception.Message.Contains("explicit migration", StringComparison.Ordinal);
            }
            LocalAssert.True(
                legacyRejected,
                "A legacy project-level registration was not preserved as an explicit migration conflict.");

            var original = await File.ReadAllBytesAsync(catalogPath);
            var unsupported = System.Text.Encoding.UTF8.GetBytes(
                "{\"schema\":\"future\",\"canonicalization\":\"pegasus-canonical-json-v1\",\"updatedAtUtc\":\"1970-01-01T00:00:00+00:00\",\"projects\":[],\"catalogHash\":\"" +
                new string('0', 64) + "\"}");
            await File.WriteAllBytesAsync(catalogPath, unsupported);
            var rejected = false;
            try
            {
                await reopened.ReadAsync(CancellationToken.None);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            LocalAssert.True(
                rejected && (await File.ReadAllBytesAsync(catalogPath)).SequenceEqual(unsupported),
                "An unknown offline catalog schema was not preserved unchanged and rejected.");
            await File.WriteAllBytesAsync(catalogPath, original);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public static Task DeterministicCliParsingAsync()
    {
        var invocation = CortexOfflineCliArguments.Parse(
        [
            "offline", "capture",
            "--root", @"C:\Projects With Spaces\One",
            "--task-id", TaskOne,
            "--record-id", "record-1",
            "--kind", "correction",
            "--tier", "explicit",
            "--title", "Exact title",
            "--content-file", @"C:\Input Files\body.md",
            "--tag", "one",
            "--tag", "two"
        ]);
        LocalAssert.True(
            invocation.RequireSingle("content-file") == @"C:\Input Files\body.md" &&
            invocation.Many("tag").SequenceEqual(["one", "two"], StringComparer.Ordinal),
            "Offline CLI parsing did not preserve separate values or repeated bounded tags.");
        var migration = CortexOfflineCliArguments.Parse(
        [
            "offline", "migrate-legacy",
            "--root", @"C:\Projects With Spaces\One",
            "--task-id", TaskOne,
            "--project-id", "project-one",
            "--write-namespace", "project.one",
            "--read-namespace", "shared.programming"
        ]);
        LocalAssert.True(
            migration.Command == "migrate-legacy" &&
            migration.RequireSingle("task-id") == TaskOne &&
            migration.Many("read-namespace").SequenceEqual(["shared.programming"], StringComparer.Ordinal),
            "Legacy migration CLI parsing did not retain its exact typed binding arguments.");

        foreach (var invalid in new[]
                 {
                     new[] { "offline", "capture", "--content", "secret" },
                     new[] { "offline", "status", "--root", "one", "--task-id", TaskOne, "--root", "two" },
                     new[] { "offline", "status", "--root", "one" },
                     new[] { "offline", "status", "--root", "one", "--task-id", "Codex-Thread-Uppercase" },
                     new[] { "offline", "unknown" }
                 })
        {
            var rejected = false;
            try
            {
                _ = CortexOfflineCliArguments.Parse(invalid);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
            LocalAssert.True(rejected, "Offline CLI parsing accepted an ambiguous or unsafe shape.");
        }
        return Task.CompletedTask;
    }

    public static async Task ExplicitLegacyMigrationIsScopedAndIdempotentAsync()
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lm",
            Guid.NewGuid().ToString("N")));
        var project = System.IO.Path.Combine(root, "p");
        var sibling = System.IO.Path.Combine(root, "s");
        var layout = TestLayout(System.IO.Path.Combine(root, "l"));
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(sibling);
        CortexLocalRegistryAclPolicy.ProtectDirectory(project);
        CortexLocalRegistryAclPolicy.ProtectDirectory(sibling);
        try
        {
            layout.EnsureCreated();
            var manifest = new CortexFileProjectManifestV2(
                CortexFileProjectManifestV2.CurrentSchema,
                "legacy-project",
                "project.legacy",
                ["project.legacy", "shared.programming"]);
            var siblingManifest = new CortexFileProjectManifestV2(
                CortexFileProjectManifestV2.CurrentSchema,
                "sibling-project",
                "project.sibling",
                ["project.sibling"]);
            var catalogPath = System.IO.Path.Combine(
                layout.ConfigDirectory,
                "offline-projects.json");
            await WriteLegacyCatalogAsync(catalogPath, project, manifest).ConfigureAwait(false);
            var catalog = new CortexOfflineProjectCatalog(catalogPath, TimeProvider.System);
            await using var siblingStore = await CortexOfflineFileStore.InitializeAsync(
                sibling,
                TaskTwo,
                siblingManifest,
                TimeProvider.System,
                CancellationToken.None).ConfigureAwait(false);
            var siblingRegistration = await catalog.RegisterAsync(
                sibling,
                TaskTwo,
                siblingManifest,
                CancellationToken.None).ConfigureAwait(false);
            var siblingManifestPath = System.IO.Path.Combine(siblingStore.ScopeRoot, "manifest.json");
            var siblingManifestBytes = await File.ReadAllBytesAsync(siblingManifestPath).ConfigureAwait(false);

            var cortexRoot = System.IO.Path.Combine(project, ".cortex");
            var legacyHot = System.IO.Path.Combine(cortexRoot, "HOT.json");
            var legacyOffline = System.IO.Path.Combine(cortexRoot, "offline");
            var legacyFrames = System.IO.Path.Combine(legacyOffline, "frames");
            var legacyFrame = System.IO.Path.Combine(legacyFrames, "legacy-frame.json");
            Directory.CreateDirectory(legacyFrames);
            var hotBytes = System.Text.Encoding.UTF8.GetBytes("{\"legacy\":\"hot-exact\"}");
            var frameBytes = System.Text.Encoding.UTF8.GetBytes("{\"legacy\":\"frame-exact\"}");
            await File.WriteAllBytesAsync(legacyHot, hotBytes).ConfigureAwait(false);
            await File.WriteAllBytesAsync(legacyFrame, frameBytes).ConfigureAwait(false);
            var catalogBeforeBlocked = await File.ReadAllBytesAsync(catalogPath).ConfigureAwait(false);

            var blocked = await CortexOfflineProductCommands.RunAsync(
                MigrationArguments(project, TaskOne, manifest),
                layout,
                TimeProvider.System,
                CancellationToken.None).ConfigureAwait(false);
            LocalAssert.True(
                blocked.ExitCode == 1 &&
                blocked.Json.Contains("Legacy project-wide offline state", StringComparison.Ordinal) &&
                (await File.ReadAllBytesAsync(catalogPath)).SequenceEqual(catalogBeforeBlocked) &&
                (await File.ReadAllBytesAsync(legacyHot)).SequenceEqual(hotBytes) &&
                (await File.ReadAllBytesAsync(legacyFrame)).SequenceEqual(frameBytes),
                "Legacy migration did not fail closed while root HOT or unscoped frames remained.");

            // This move represents the separately authorized operator archive;
            // the product migration command never opens or moves legacy bodies.
            var archive = System.IO.Path.Combine(cortexRoot, "a", "v");
            Directory.CreateDirectory(archive);
            var archivedHot = System.IO.Path.Combine(archive, "HOT.json");
            var archivedOffline = System.IO.Path.Combine(archive, "offline");
            File.Move(legacyHot, archivedHot);
            Directory.Move(legacyOffline, archivedOffline);

            // Force a catalog conflict after the exact task scope is created.
            // Restoring the prior catalog then proves the stranded scope has
            // one unambiguous, exact replay path and no inherited legacy data.
            var catalogBeforeLateFailure = await File.ReadAllBytesAsync(catalogPath).ConfigureAwait(false);
            var lateFailure = await CortexOfflineProductCommands.RunAsync(
                MigrationArguments(project, TaskOne, manifest),
                layout,
                TimeProvider.System,
                async cancellationToken =>
                {
                    _ = await catalog.RegisterAsync(
                        sibling,
                        TaskOne,
                        manifest,
                        cancellationToken).ConfigureAwait(false);
                },
                CancellationToken.None).ConfigureAwait(false);
            await using (var replayableStore = await CortexOfflineFileStore.OpenAsync(
                             project,
                             TaskOne,
                             TimeProvider.System,
                             CancellationToken.None).ConfigureAwait(false))
            {
                LocalAssert.True(
                    lateFailure.ExitCode == 1 &&
                    lateFailure.Json.Contains("catalog commit failed", StringComparison.OrdinalIgnoreCase) &&
                    !File.Exists(System.IO.Path.Combine(replayableStore.ScopeRoot, "HOT.json")) &&
                    !Directory.EnumerateFileSystemEntries(
                        System.IO.Path.Combine(replayableStore.ScopeRoot, "frames")).Any() &&
                    (await File.ReadAllBytesAsync(archivedHot)).SequenceEqual(hotBytes) &&
                    (await File.ReadAllBytesAsync(
                        System.IO.Path.Combine(archivedOffline, "frames", "legacy-frame.json")))
                        .SequenceEqual(frameBytes) &&
                    (await File.ReadAllBytesAsync(siblingManifestPath)).SequenceEqual(siblingManifestBytes),
                    "A late catalog conflict imported legacy data, changed a sibling scope, or left no exact replay path.");
            }
            await File.WriteAllBytesAsync(catalogPath, catalogBeforeLateFailure).ConfigureAwait(false);

            var migrated = await CortexOfflineProductCommands.RunAsync(
                MigrationArguments(project, TaskOne, manifest),
                layout,
                TimeProvider.System,
                CancellationToken.None).ConfigureAwait(false);
            using (var migratedJson = JsonDocument.Parse(migrated.Json))
            {
                var value = migratedJson.RootElement;
                LocalAssert.True(
                    migrated.ExitCode == 0 &&
                    value.GetProperty("state").GetString() == "migrated" &&
                    value.GetProperty("legacyRegistrationPreserved").GetBoolean() &&
                    !value.GetProperty("legacyContentImported").GetBoolean() &&
                    !value.GetProperty("legacyTaskAssigned").GetBoolean(),
                    $"Explicit legacy migration failed: {migrated.Json}");
            }
            LocalAssert.True(
                !File.Exists(layout.V2DatabasePath) &&
                (await File.ReadAllBytesAsync(archivedHot)).SequenceEqual(hotBytes) &&
                (await File.ReadAllBytesAsync(
                    System.IO.Path.Combine(archivedOffline, "frames", "legacy-frame.json")))
                    .SequenceEqual(frameBytes),
                "Migration opened central storage or changed the operator-owned legacy archive.");

            var after = await catalog.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            var retainedLegacy = after.Projects.Single(item =>
                item.TaskId == CortexOfflineProjectCatalog.LegacyUnassignedTaskId &&
                string.Equals(item.RootPath, project, PathComparison));
            var taskBinding = after.Projects.Single(item =>
                item.TaskId == TaskOne && string.Equals(item.RootPath, project, PathComparison));
            var retainedSibling = after.Projects.Single(item =>
                item.TaskId == TaskTwo && string.Equals(item.RootPath, sibling, PathComparison));
            LocalAssert.True(
                after.Projects.Count == 3 &&
                SameBinding(retainedLegacy, taskBinding) &&
                RegistrationEquivalent(retainedSibling, siblingRegistration) &&
                (await File.ReadAllBytesAsync(siblingManifestPath)).SequenceEqual(siblingManifestBytes),
                "Migration discarded its legacy evidence or mutated a sibling task/root binding.");

            var status = await CortexOfflineProductCommands.RunAsync(
                ["offline", "status", "--root", project, "--task-id", TaskOne],
                layout,
                TimeProvider.System,
                CancellationToken.None).ConfigureAwait(false);
            using (var statusJson = JsonDocument.Parse(status.Json))
            {
                LocalAssert.True(
                    status.ExitCode == 0 &&
                    statusJson.RootElement.GetProperty("state").GetString() == "ready" &&
                    statusJson.RootElement.GetProperty("frames").GetArrayLength() == 0,
                    "Migrated task status was unavailable or inherited legacy frames.");
            }

            var refresh = await CortexOfflineProductCommands.RunAsync(
                ["offline", "refresh", "--root", project, "--task-id", TaskOne],
                layout,
                TimeProvider.System,
                CancellationToken.None).ConfigureAwait(false);
            await using var migratedStore = await CortexOfflineFileStore.OpenAsync(
                project,
                TaskOne,
                TimeProvider.System,
                CancellationToken.None).ConfigureAwait(false);
            LocalAssert.True(
                refresh.ExitCode == 0 &&
                File.Exists(System.IO.Path.Combine(migratedStore.ScopeRoot, "HOT.json")),
                $"Migrated task refresh failed: {refresh.Json}");

            var catalogAfterMigration = await File.ReadAllBytesAsync(catalogPath).ConfigureAwait(false);
            var scopeManifest = System.IO.Path.Combine(migratedStore.ScopeRoot, "manifest.json");
            var scopeManifestBytes = await File.ReadAllBytesAsync(scopeManifest).ConfigureAwait(false);
            var replay = await CortexOfflineProductCommands.RunAsync(
                MigrationArguments(project, TaskOne, manifest),
                layout,
                TimeProvider.System,
                CancellationToken.None).ConfigureAwait(false);
            using var replayJson = JsonDocument.Parse(replay.Json);
            LocalAssert.True(
                replay.ExitCode == 0 &&
                replayJson.RootElement.GetProperty("state").GetString() == "existing" &&
                (await File.ReadAllBytesAsync(catalogPath)).SequenceEqual(catalogAfterMigration) &&
                (await File.ReadAllBytesAsync(scopeManifest)).SequenceEqual(scopeManifestBytes),
                "Exact legacy migration replay was not idempotent.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public static async Task LegacyMigrationConflictsFailClosedAsync()
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lc",
            Guid.NewGuid().ToString("N")));
        var project = System.IO.Path.Combine(root, "p");
        var conflictingRoot = System.IO.Path.Combine(root, "c");
        var unrelatedRoot = System.IO.Path.Combine(root, "u");
        var linkRoot = System.IO.Path.Combine(root, "x");
        var layout = TestLayout(System.IO.Path.Combine(root, "l"));
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(conflictingRoot);
        Directory.CreateDirectory(unrelatedRoot);
        CortexLocalRegistryAclPolicy.ProtectDirectory(project);
        CortexLocalRegistryAclPolicy.ProtectDirectory(conflictingRoot);
        CortexLocalRegistryAclPolicy.ProtectDirectory(unrelatedRoot);
        try
        {
            layout.EnsureCreated();
            var manifest = new CortexFileProjectManifestV2(
                CortexFileProjectManifestV2.CurrentSchema,
                "legacy-project",
                "project.legacy",
                ["project.legacy", "shared.programming"]);
            var catalogPath = System.IO.Path.Combine(
                layout.ConfigDirectory,
                "offline-projects.json");
            await WriteLegacyCatalogAsync(catalogPath, project, manifest).ConfigureAwait(false);
            var catalog = new CortexOfflineProjectCatalog(catalogPath, TimeProvider.System);
            _ = await catalog.RegisterAsync(
                conflictingRoot,
                TaskOne,
                manifest,
                CancellationToken.None).ConfigureAwait(false);
            var unchanged = await File.ReadAllBytesAsync(catalogPath).ConfigureAwait(false);

            var compositeConflict = await CortexOfflineProductCommands.RunAsync(
                MigrationArguments(project, TaskOne, manifest),
                layout,
                TimeProvider.System,
                CancellationToken.None).ConfigureAwait(false);
            var wrongManifest = manifest with
            {
                WriteNamespace = "project.changed",
                ReadNamespaces = ["project.changed"]
            };
            var bindingConflict = await CortexOfflineProductCommands.RunAsync(
                MigrationArguments(project, TaskTwo, wrongManifest),
                layout,
                TimeProvider.System,
                CancellationToken.None).ConfigureAwait(false);
            var rootMismatch = await CortexOfflineProductCommands.RunAsync(
                MigrationArguments(unrelatedRoot, TaskTwo, manifest),
                layout,
                TimeProvider.System,
                CancellationToken.None).ConfigureAwait(false);
            var nonCanonical = await CortexOfflineProductCommands.RunAsync(
                MigrationArguments(project + System.IO.Path.DirectorySeparatorChar + ".", TaskTwo, manifest),
                layout,
                TimeProvider.System,
                CancellationToken.None).ConfigureAwait(false);

            var reparseRejected = false;
            try
            {
                Directory.CreateSymbolicLink(linkRoot, project);
                var reparse = await CortexOfflineProductCommands.RunAsync(
                    MigrationArguments(linkRoot, TaskTwo, manifest),
                    layout,
                    TimeProvider.System,
                    CancellationToken.None).ConfigureAwait(false);
                reparseRejected = reparse.ExitCode != 0;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                try
                {
                    CortexLocalRegistryPathPolicy.RequireOrdinaryProjectRootEntry(
                        FileAttributes.Directory | FileAttributes.ReparsePoint,
                        linkTarget: null);
                }
                catch (IOException)
                {
                    reparseRejected = true;
                }
            }
            finally
            {
                if (Directory.Exists(linkRoot))
                    Directory.Delete(linkRoot, recursive: false);
            }

            LocalAssert.True(
                compositeConflict.ExitCode == 1 &&
                bindingConflict.ExitCode == 1 &&
                rootMismatch.ExitCode == 1 &&
                nonCanonical.ExitCode == 1 &&
                reparseRejected &&
                (await File.ReadAllBytesAsync(catalogPath)).SequenceEqual(unchanged) &&
                !Directory.Exists(System.IO.Path.Combine(project, ".cortex", "offline", "scopes")) &&
                !File.Exists(layout.V2DatabasePath),
                "A legacy migration conflict, unsafe root, or reparse path changed retained state.");
        }
        finally
        {
            if (Directory.Exists(linkRoot))
                Directory.Delete(linkRoot, recursive: false);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public static async Task LegacyMigrationCapacityPreflightCreatesNoScopeAsync()
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lf",
            Guid.NewGuid().ToString("N")));
        var project = System.IO.Path.Combine(root, "p");
        var layout = TestLayout(System.IO.Path.Combine(root, "l"));
        Directory.CreateDirectory(project);
        CortexLocalRegistryAclPolicy.ProtectDirectory(project);
        try
        {
            layout.EnsureCreated();
            var manifest = new CortexFileProjectManifestV2(
                CortexFileProjectManifestV2.CurrentSchema,
                "legacy-project",
                "project.legacy",
                ["project.legacy"]);
            var registrations = new List<CortexOfflineProjectRegistration>
            {
                new(
                    CortexOfflineProjectCatalog.LegacyUnassignedTaskId,
                    manifest.ProjectId,
                    project,
                    CortexCanonicalJson.Sha256(System.Text.Encoding.UTF8.GetBytes(project)),
                    manifest.WriteNamespace,
                    manifest.ReadNamespaces)
            };
            for (var index = 0; index < CortexOfflineProjectCatalog.MaximumProjects - 1; index++)
            {
                var registeredRoot = System.IO.Path.Combine(root, $"r{index:D4}");
                registrations.Add(new CortexOfflineProjectRegistration(
                    $"capacity-task-{index:D4}",
                    $"capacity-project-{index:D4}",
                    registeredRoot,
                    CortexCanonicalJson.Sha256(System.Text.Encoding.UTF8.GetBytes(registeredRoot)),
                    "project.capacity",
                    ["project.capacity"]));
            }

            var catalogPath = System.IO.Path.Combine(layout.ConfigDirectory, "offline-projects.json");
            await WriteCurrentCatalogAsync(catalogPath, registrations).ConfigureAwait(false);
            var before = await File.ReadAllBytesAsync(catalogPath).ConfigureAwait(false);
            var result = await CortexOfflineProductCommands.RunAsync(
                MigrationArguments(project, TaskOne, manifest),
                layout,
                TimeProvider.System,
                CancellationToken.None).ConfigureAwait(false);

            LocalAssert.True(
                result.ExitCode == 1 &&
                result.Json.Contains("catalog is full", StringComparison.OrdinalIgnoreCase) &&
                (await File.ReadAllBytesAsync(catalogPath)).SequenceEqual(before) &&
                !Directory.Exists(System.IO.Path.Combine(project, ".cortex", "offline", "scopes")) &&
                !File.Exists(layout.V2DatabasePath),
                "A full catalog was not rejected before task-scope initialization.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public static async Task WarmupDoesNotEnumerateTaskScopesAsync()
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "cortex-offline-warmup-tests",
            Guid.NewGuid().ToString("N")));
        var layout = new CortexCurrentUserLayout(
            root,
            System.IO.Path.Combine(root, "data"),
            System.IO.Path.Combine(root, "data", "cortex.db"),
            System.IO.Path.Combine(root, "config"),
            System.IO.Path.Combine(root, "backups"),
            System.IO.Path.Combine(root, "logs"),
            System.IO.Path.Combine(root, "receipts"));
        var good = System.IO.Path.Combine(root, "good");
        var bad = System.IO.Path.Combine(root, "bad");
        try
        {
            layout.EnsureCreated();
            Directory.CreateDirectory(good);
            Directory.CreateDirectory(bad);
            CortexLocalRegistryAclPolicy.ProtectDirectory(good);
            CortexLocalRegistryAclPolicy.ProtectDirectory(bad);
            var goodManifest = new CortexFileProjectManifestV2(
                CortexFileProjectManifestV2.CurrentSchema,
                "good-project",
                "project.good",
                ["project.good"]);
            var badManifest = new CortexFileProjectManifestV2(
                CortexFileProjectManifestV2.CurrentSchema,
                "bad-project",
                "project.bad",
                ["project.bad"]);
            var catalog = new CortexOfflineProjectCatalog(
                System.IO.Path.Combine(layout.ConfigDirectory, "offline-projects.json"));
            await catalog.RegisterAsync(good, TaskOne, goodManifest, CancellationToken.None);
            await catalog.RegisterAsync(bad, TaskTwo, badManifest, CancellationToken.None);

            var result = await CortexOfflineWarmup.RunAsync(
                layout,
                TimeProvider.System,
                CancellationToken.None);
            LocalAssert.True(
                result.Healthy && result.CentralState == "ready" &&
                result.RegisteredProjects == 2 && result.Projects.Count == 0,
                "Ambient warmup enumerated task-scoped project details or failed central-only health.");
            LocalAssert.True(
                File.Exists(result.ReceiptPath) &&
                new FileInfo(result.ReceiptPath).Length <= CortexOfflineWarmup.MaximumReceiptUtf8Bytes,
                "Warmup did not write its bounded receipt.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public static async Task OfflineCaptureSearchReadAndIngestAsync()
    {
        // Real task identifiers exercise the privacy-bounded attribution contract.
        const string TaskOne = "20000000-0000-0000-0000-000000000001";
        const string TaskTwo = "20000000-0000-0000-0000-000000000002";
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "cx-o",
            Guid.NewGuid().ToString("N")));
        var project = System.IO.Path.Combine(root, "project with spaces");
        var layout = new CortexCurrentUserLayout(
            System.IO.Path.Combine(root, "local"),
            System.IO.Path.Combine(root, "local", "data"),
            System.IO.Path.Combine(root, "local", "data", "cortex.db"),
            System.IO.Path.Combine(root, "local", "config"),
            System.IO.Path.Combine(root, "local", "backups"),
            System.IO.Path.Combine(root, "local", "logs"),
            System.IO.Path.Combine(root, "local", "receipts"));
        Directory.CreateDirectory(project);
        CortexLocalRegistryAclPolicy.ProtectDirectory(project);
        var contentPath = System.IO.Path.Combine(root, "exact content.md");
        const string exact = "line one\r\nline two\n\tΩ漢字\u0000end";
        await File.WriteAllTextAsync(
            contentPath,
            exact,
            new System.Text.UTF8Encoding(false));
        try
        {
            var init = await CortexOfflineProductCommands.RunAsync(
                [
                    "offline", "init", "--root", project,
                    "--task-id", TaskOne,
                    "--project-id", "project-one", "--write-namespace", "project.one",
                    "--read-namespace", "shared.programming"
                ],
                layout,
                TimeProvider.System,
                CancellationToken.None);
            LocalAssert.True(init.ExitCode == 0, $"Offline init failed: {init.Json}");

            var capture = await CortexOfflineProductCommands.RunAsync(
                [
                    "offline", "capture", "--root", project,
                    "--task-id", TaskOne,
                    "--record-id", "knowledge-one", "--kind", "correction",
                    "--tier", "explicit", "--title", "Exact Ω correction",
                    "--content-file", contentPath, "--tag", "unicode",
                    "--source", "user", "--source-id", "user-direct",
                    "--evidence", "direct correction"
                ],
                layout,
                TimeProvider.System,
                CancellationToken.None);
            LocalAssert.True(capture.ExitCode == 0, $"Offline capture failed: {capture.Json}");
            using var captureJson = JsonDocument.Parse(capture.Json);
            LocalAssert.True(!captureJson.RootElement.GetProperty("utilizationRecorded").GetBoolean(),
                "Offline capture claimed central observation while the central store was absent.");
            var frameId = captureJson.RootElement.GetProperty("frame").GetProperty("frameId").GetString()!;
            LocalAssert.True(
                !File.Exists(layout.V2DatabasePath),
                "Offline capture opened or created the central SQLite database.");

            var siblingInit = await CortexOfflineProductCommands.RunAsync(
                [
                    "offline", "init", "--root", project,
                    "--task-id", TaskTwo,
                    "--project-id", "project-one", "--write-namespace", "project.one",
                    "--read-namespace", "shared.programming"
                ],
                layout,
                TimeProvider.System,
                CancellationToken.None);
            var siblingStatus = await CortexOfflineProductCommands.RunAsync(
                ["offline", "status", "--root", project, "--task-id", TaskTwo],
                layout,
                TimeProvider.System,
                CancellationToken.None);
            var siblingRead = await CortexOfflineProductCommands.RunAsync(
                ["offline", "read", "--root", project, "--task-id", TaskTwo, "--frame-id", frameId],
                layout,
                TimeProvider.System,
                CancellationToken.None);
            LocalAssert.True(
                siblingInit.ExitCode == 0 && siblingStatus.ExitCode == 0 &&
                !siblingStatus.Json.Contains(frameId, StringComparison.Ordinal) &&
                siblingRead.ExitCode != 0,
                "A sibling task enumerated or read another task scope's pending frame.");

            var search = await CortexOfflineProductCommands.RunAsync(
                ["offline", "search", "--root", project, "--task-id", TaskOne, "--text", "Ω correction"],
                layout,
                TimeProvider.System,
                CancellationToken.None);
            LocalAssert.True(search.ExitCode == 0 && search.Json.Contains(frameId, StringComparison.Ordinal),
                "Offline search did not find the captured frame.");
            var read = await CortexOfflineProductCommands.RunAsync(
                ["offline", "read", "--root", project, "--task-id", TaskOne, "--frame-id", frameId],
                layout,
                TimeProvider.System,
                CancellationToken.None);
            using (var readJson = JsonDocument.Parse(read.Json))
            {
                var retained = readJson.RootElement.GetProperty("pending")
                    .GetProperty("payload").GetProperty("content").GetString();
                LocalAssert.True(read.ExitCode == 0 && retained == exact,
                    "Offline read did not retain exact Unicode/control content.");
            }

            var ingest = await CortexOfflineProductCommands.RunAsync(
                ["offline", "ingest", "--root", project, "--task-id", TaskOne, "--frame-id", frameId],
                layout,
                TimeProvider.System,
                CancellationToken.None);
            LocalAssert.True(ingest.ExitCode == 0, $"Offline ingest failed: {ingest.Json}");
            var receiptRead = await CortexOfflineProductCommands.RunAsync(
                ["offline", "read", "--root", project, "--task-id", TaskOne, "--frame-id", frameId],
                layout,
                TimeProvider.System,
                CancellationToken.None);
            LocalAssert.True(
                receiptRead.ExitCode == 0 &&
                receiptRead.Json.Contains("receipt", StringComparison.OrdinalIgnoreCase) &&
                !receiptRead.Json.Contains(exact, StringComparison.Ordinal),
                "Successful ingest did not replace the candidate with a compact body-free receipt.");
            var replay = await CortexOfflineProductCommands.RunAsync(
                ["offline", "ingest", "--root", project, "--task-id", TaskOne, "--frame-id", frameId],
                layout,
                TimeProvider.System,
                CancellationToken.None);
            LocalAssert.True(replay.ExitCode == 0 && replay.Json.Contains("already-receipt", StringComparison.Ordinal),
                "Offline ingest replay was not idempotent.");

            await using var scoped = await CortexOfflineFileStore.OpenAsync(
                project, TaskOne, TimeProvider.System, CancellationToken.None);
            var refresh = await CortexOfflineProductCommands.RunAsync(
                ["offline", "refresh", "--root", project, "--task-id", TaskOne],
                layout,
                TimeProvider.System,
                CancellationToken.None);
            LocalAssert.True(
                refresh.ExitCode == 0 &&
                refresh.Json.Contains(TaskOne, StringComparison.Ordinal) &&
                File.Exists(System.IO.Path.Combine(scoped.ScopeRoot, "HOT.json")),
                $"Offline refresh failed to materialize HOT: {refresh.Json}");
            await using (var usageStore = new SqliteCortexV2Store(new(layout.V2DatabasePath)))
            {
                var usage = await usageStore.ReadUsageAsync();
                LocalAssert.True(usage.Rows.Any(x => x.Channel == "offline" && x.Operation == "refresh" && x.Calls == 1),
                    "HOT generation was not observed separately from MCP activity.");
                LocalAssert.True(usage.Rows.All(x => x.Channel == "offline") && usage.ObservedTasks == 1,
                    "Offline activity was mislabeled or attributed to the unavailable sibling task.");
            }
            LocalAssert.True(new FileInfo(System.IO.Path.Combine(scoped.ScopeRoot, "HOT.json")).Length <= 4096,
                "Usage attribution enlarged the task HOT payload.");

            var frames = System.IO.Path.Combine(scoped.ScopeRoot, "frames");
            var unknownPath = System.IO.Path.Combine(frames, "future-frame.json");
            var unknownBytes = System.Text.Encoding.UTF8.GetBytes(
                "{\"schema\":\"future.offline-frame/9.0\",\"keep\":\"exact\"}");
            await File.WriteAllBytesAsync(unknownPath, unknownBytes);
            var unknown = await CortexOfflineProductCommands.RunAsync(
                ["offline", "read", "--root", project, "--task-id", TaskOne, "--frame-id", "future-frame"],
                layout,
                TimeProvider.System,
                CancellationToken.None);
            LocalAssert.True(
                unknown.ExitCode == 0 &&
                unknown.Json.Contains("unsupportedOuterSchema", StringComparison.OrdinalIgnoreCase) &&
                (await File.ReadAllBytesAsync(unknownPath)).SequenceEqual(unknownBytes),
                "An unknown offline frame schema was not preserved unchanged and reported unsupported.");

            var conflictContent = System.IO.Path.Combine(root, "conflict.md");
            await File.WriteAllTextAsync(
                conflictContent,
                "offline candidate body",
                new System.Text.UTF8Encoding(false));
            var conflictCapture = await CortexOfflineProductCommands.RunAsync(
                [
                    "offline", "capture", "--root", project,
                    "--task-id", TaskOne,
                    "--record-id", "knowledge-conflict", "--kind", "correction",
                    "--tier", "explicit", "--title", "Offline conflict",
                    "--content-file", conflictContent, "--tag", "conflict"
                ],
                layout,
                TimeProvider.System,
                CancellationToken.None);
            using var conflictJson = JsonDocument.Parse(conflictCapture.Json);
            var conflictFrameId = conflictJson.RootElement.GetProperty("frame")
                .GetProperty("frameId").GetString()!;
            var centralLow = new SqliteCortexV2Store(
                new SqliteCortexV2StoreOptions(layout.V2DatabasePath),
                TimeProvider.System);
            await using (var central = new SqliteCortexV2OperationalStore(centralLow, "test"))
            {
                await centralLow.InitializeAsync(CancellationToken.None);
                var application = new CortexV2Application(
                    central,
                    "test_actor",
                    TimeProvider.System);
                _ = await application.ExecuteKnowledgeAsync(
                    new CortexKnowledgeCommandV2(
                        "project.one",
                        CortexKnowledgeActionV2.Create,
                        "knowledge-conflict",
                        ExpectedRevision: null,
                        "central-preseed-conflict",
                        CortexKnowledgeKindV2.Correction,
                        CortexKnowledgeTierV2.Explicit,
                        "Central conflict",
                        "different central body",
                        ["conflict"],
                        new CortexProvenanceInputV2(
                            CortexProvenanceSourceV2.User,
                            "central-seed",
                            "conflicting record"),
                        ReplacementRecordId: null),
                    CancellationToken.None);
            }
            var conflict = await CortexOfflineProductCommands.RunAsync(
                ["offline", "ingest", "--root", project, "--task-id", TaskOne, "--frame-id", conflictFrameId],
                layout,
                TimeProvider.System,
                CancellationToken.None);
            var retainedConflict = await CortexOfflineProductCommands.RunAsync(
                ["offline", "read", "--root", project, "--task-id", TaskOne, "--frame-id", conflictFrameId],
                layout,
                TimeProvider.System,
                CancellationToken.None);
            LocalAssert.True(
                conflict.ExitCode == 1 &&
                retainedConflict.ExitCode == 0 &&
                retainedConflict.Json.Contains("offline candidate body", StringComparison.Ordinal),
                "A central conflict did not fail while leaving the offline candidate exact and pending.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public static Task CapacityThresholdClassificationAsync()
    {
        const long ceiling = SqliteCortexV2CapacityPolicy.DefaultCeilingBytes;
        var cases = new (long Bytes, string Expected)[]
        {
            (SqliteCortexV2CapacityPolicy.WarningBytes - 1, "normal"),
            (SqliteCortexV2CapacityPolicy.WarningBytes, "warning"),
            (SqliteCortexV2CapacityPolicy.ElevatedBytes, "elevated"),
            (SqliteCortexV2CapacityPolicy.MaintenanceBytes, "maintenance"),
            (ceiling, "ceiling")
        };
        foreach (var value in cases)
        {
            LocalAssert.True(
                SqliteCortexV2CapacityPolicy.Classify(value.Bytes, ceiling) == value.Expected,
                $"Capacity threshold {value.Bytes} did not classify as {value.Expected}.");
        }
        return Task.CompletedTask;
    }

    private sealed record LegacyProjectRegistration(
        string ProjectId,
        string RootPath,
        string RootPathHash,
        string WriteNamespace,
        IReadOnlyList<string> ReadNamespaces);

    private sealed record LegacyCatalogDocument(
        string Schema,
        string Canonicalization,
        DateTimeOffset UpdatedAtUtc,
        IReadOnlyList<LegacyProjectRegistration> Projects,
        string CatalogHash);

    private static CortexCurrentUserLayout TestLayout(string root) => new(
        root,
        System.IO.Path.Combine(root, "data"),
        System.IO.Path.Combine(root, "data", "cortex.db"),
        System.IO.Path.Combine(root, "config"),
        System.IO.Path.Combine(root, "backups"),
        System.IO.Path.Combine(root, "logs"),
        System.IO.Path.Combine(root, "receipts"));

    private static async Task WriteLegacyCatalogAsync(
        string catalogPath,
        string projectRoot,
        CortexFileProjectManifestV2 manifest)
    {
        var legacyRegistration = new LegacyProjectRegistration(
            manifest.ProjectId,
            projectRoot,
            CortexCanonicalJson.Sha256(System.Text.Encoding.UTF8.GetBytes(projectRoot)),
            manifest.WriteNamespace,
            manifest.ReadNamespaces);
        var zero = new LegacyCatalogDocument(
            CortexOfflineProjectCatalog.LegacySchema,
            CortexOfflineProjectCatalog.Canonicalization,
            DateTimeOffset.UnixEpoch,
            [legacyRegistration],
            new string('0', 64));
        var sealedDocument = zero with
        {
            CatalogHash = CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(zero))
        };
        await File.WriteAllBytesAsync(
            catalogPath,
            CortexCanonicalJson.Serialize(sealedDocument)).ConfigureAwait(false);
    }

    private static async Task WriteCurrentCatalogAsync(
        string catalogPath,
        IReadOnlyList<CortexOfflineProjectRegistration> registrations)
    {
        var ordered = registrations
            .OrderBy(item => item.TaskId, StringComparer.Ordinal)
            .ThenBy(item => item.ProjectId, StringComparer.Ordinal)
            .ToArray();
        var zero = new CortexOfflineProjectCatalogDocument(
            CortexOfflineProjectCatalog.CurrentSchema,
            CortexOfflineProjectCatalog.Canonicalization,
            DateTimeOffset.UnixEpoch,
            Array.AsReadOnly(ordered),
            new string('0', 64));
        var sealedDocument = zero with
        {
            CatalogHash = CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(zero))
        };
        await File.WriteAllBytesAsync(
            catalogPath,
            CortexCanonicalJson.Serialize(sealedDocument)).ConfigureAwait(false);
    }

    private static string[] MigrationArguments(
        string root,
        string taskId,
        CortexFileProjectManifestV2 manifest)
    {
        var values = new List<string>
        {
            "offline", "migrate-legacy",
            "--root", root,
            "--task-id", taskId,
            "--project-id", manifest.ProjectId,
            "--write-namespace", manifest.WriteNamespace
        };
        foreach (var ns in manifest.ReadNamespaces.Where(value =>
                     !string.Equals(value, manifest.WriteNamespace, StringComparison.Ordinal)))
        {
            values.Add("--read-namespace");
            values.Add(ns);
        }
        return values.ToArray();
    }

    private static bool SameBinding(
        CortexOfflineProjectRegistration left,
        CortexOfflineProjectRegistration right) =>
        string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal) &&
        string.Equals(left.RootPath, right.RootPath, PathComparison) &&
        string.Equals(left.RootPathHash, right.RootPathHash, StringComparison.Ordinal) &&
        string.Equals(left.WriteNamespace, right.WriteNamespace, StringComparison.Ordinal) &&
        left.ReadNamespaces.SequenceEqual(right.ReadNamespaces, StringComparer.Ordinal);

    private static bool RegistrationEquivalent(
        CortexOfflineProjectRegistration left,
        CortexOfflineProjectRegistration right) =>
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) &&
        SameBinding(left, right);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
