using System.Security.Principal;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Persistence.File.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;
using Pegasus.Cortex.Product;

internal static class RegisteredFreshStartTests
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string Actor = "os_registered_fresh_fixture";
    private const string MarkerId = "clean-start-preserved";
    private const string MarkerContent = "Preserve exact fixture content Ω\r\nsecond line 😀";
    private sealed record Seed(string Namespace, CortexFileBehaviorRecordDescriptorV2 Descriptor,
        string Content, string PackRoot, string PackId, int PackRevision)
    {
        public string RecordId => Descriptor.RecordId;
        public string Title => Descriptor.Title;
    }
    private sealed record Fixture(CortexProductLayout Layout, CortexProductLayout Fallback);

    // This selector supplies registration values to the existing pure seam. It never
    // calls ResolveCurrent/Load, writes HKLM, starts a host, or selects an installed DB.
    public static async Task<int> RunAsync(string fixtureRoot)
    {
        var passed = 0;
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Registered clean-start acceptance requires Windows.");
            var sourceRoot = FindSourceRoot();
            var admittedParent = Path.Combine(sourceRoot, "artifacts", "validation", "r2-clean-01a1f50b");
            var root = Path.GetFullPath(fixtureRoot);
            Check(Path.IsPathFullyQualified(fixtureRoot) &&
                string.Equals(root, fixtureRoot, StringComparison.OrdinalIgnoreCase) &&
                root.StartsWith(admittedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                root.Length <= 110, "Use a new short canonical child of artifacts/validation/r2-clean-01a1f50b.");
            Check(!Directory.Exists(root) && !File.Exists(root), "The clean-start run root already exists.");
            var sid = WindowsIdentity.GetCurrent().User?.Value ??
                throw new InvalidOperationException("The fixture owner SID is unavailable.");
            // Admit generated paths through the public product boundary before the first write.
            _ = CortexProductLayout.ForIsolatedRoots(Path.Combine(root, "admit-install"), Path.Combine(root, "admit-data"), sid);
            CortexUserStateDirectories.EnsureCurrentUserDirectory(root);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var token = deadline.Token;
            var fresh = CreateFixture(root, "fresh", sid);
            var corpus = Path.Combine(fresh.Layout.InstallRoot, "bin", "semantics", "behavior-packs");
            var seeds = await StageSourceCorpusAsync(sourceRoot, corpus, token);
            Check(seeds.Count is > 0 and <= 256 && seeds.All(seed => seed.Namespace == "knowledge.shared") &&
                seeds.Select(seed => seed.Namespace + "/" + seed.RecordId).Distinct(StringComparer.Ordinal).Count() == seeds.Count,
                "The fixture must use bounded, unique public starter knowledge only.");
            var basicIds = new[]
            {
                "local-interfaces", "deterministic-core", "minimal-context", "fresh-data",
                "durable-context", "scoped-memory", "delivery-pickup", "safe-lifecycle"
            }.Select(suffix => "tenet.pegasus-local-core." + suffix);
            var guideIds = new[]
            {
                "Knowledge.Cerebral.Guide", "Knowledge.Cerebral.Guide.ToolRouting",
                "Knowledge.Cerebral.Guide.FilesImagesArchives", "Knowledge.Cerebral.Guide.ModelCapabilitySelection",
                "Knowledge.Cerebral.Guide.RecoveryCompaction", "Knowledge.Cerebral.Guide.PluginAuthoring",
                "Knowledge.Cerebral.Guide.PersistentEnvironmentsSessions", "Knowledge.Cerebral.Guide.BoundedWork"
            };
            Check(basicIds.Concat(guideIds).All(id => seeds.Count(seed => seed.RecordId == id) == 1),
                "The basic product guidance and canonical public guides must each be present exactly once.");

            await FreshAndReopenAsync(fresh, corpus, seeds, token);
            Pass("service-absent owner startup, actual owner-pipe diagnostic, guidance and preserved reopen", ref passed);
            await RejectLocationsAsync(root, sid);
            Pass("invalid, mismatched and unavailable registration never falls back", ref passed);
            await RejectHostStartupAsync(root, sid, token);
            Pass("required, incomplete, untrusted and unreadable barriers refuse before owner initialization", ref passed);
            await RejectSchemasAsync(root, sid, corpus, token);
            Pass("incompatible schema and metadata shapes fail closed without changing existing files", ref passed);
            await RejectGuidanceAsync(root, fresh, seeds, sid, token);
            Pass("conflicting guidance is atomic and invalid guidance cannot create a DB", ref passed);
            await CancellationCreatesNoDatabaseAsync(root, sid, corpus);
            Pass("pre-cancelled initialization and seeding create no DB", ref passed);
            Console.WriteLine($"METRIC registered-fresh-start passed={passed} total=6 seed-records={seeds.Count} fixture-only=true");
            return 0;
        }
        catch (Exception error)
        {
            var message = error.Message.Replace('\r', ' ').Replace('\n', ' ');
            Console.Error.WriteLine($"FAIL registered-fresh-start passed={passed} cause={error.GetType().Name} detail={message[..Math.Min(message.Length, 300)]}");
            return 1;
        }
    }

    private static Fixture CreateFixture(string root, string name, string sid)
    {
        var caseRoot = Path.Combine(root, name);
        Check(!Directory.Exists(caseRoot) && !File.Exists(caseRoot), "A case directory already exists.");
        var install = Path.Combine(caseRoot, "install");
        var data = Path.Combine(caseRoot, "data");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(data);
        var declared = CortexProductLayout.ForIsolatedRoots(install, data, sid);
        var selected = CortexDataLocation.ApplyRegistration(declared, install, data);
        Check(selected.DataRoot == data && selected.RegisteredDataRoot == data &&
            selected.CortexDatabasePath.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "The explicit registration did not select the isolated data root.");
        var current = CortexCurrentUserLayout.FromProduct(selected);
        Check(current.V2DatabasePath == selected.CortexDatabasePath &&
            current.LegacyDatabasePath == selected.LegacyCortexDatabasePath &&
            current.Root == selected.UserStateDirectory && current.ConfigDirectory == selected.UserConfigDirectory,
            "The normal user-layout projection diverged from the selected registered root.");
        return new(selected, CortexProductLayout.ForIsolatedRoot(install, sid));
    }

    private static async Task FreshAndReopenAsync(Fixture fixture, string corpus,
        IReadOnlyList<Seed> seeds, CancellationToken token)
    {
        var owner = LocalIdentity.Current();
        Check(owner.Session > 0 && !owner.Elevated && owner.Sid == fixture.Layout.OwnerSid,
            "Run owner-startup acceptance from the signed-in ordinary user session.");
        var publicBarrier = Path.Combine(fixture.Layout.DataRoot, "execution-maintenance");
        Check(!Directory.Exists(publicBarrier) && !File.Exists(publicBarrier),
            "The optional service's public state must be genuinely absent in the fresh fixture.");
        var maintenance = MaintenanceModeStore.OpenReader(requireProvisioned: false,
            publicDirectoryResolver: () => publicBarrier);
        var admission = new UserHostAdmission();
        using var fallback = new HeldLegacy(fixture.Fallback.LegacyCortexDatabasePath);
        // The normal tray entry calls this exact helper before creating owner state.
        await using var engine = UserHostStartup.CreateOwnerEngine(fixture.Layout, maintenance, admission);
        Check(File.Exists(Path.Combine(fixture.Layout.UserExecutionDirectory, "agentops.db")) &&
            !Directory.Exists(fixture.Fallback.UserExecutionDirectory),
            "Ordinary execution did not initialize solely under the selected data root.");
        await OwnerPipeDiagnosticAsync(engine, owner, token);
        UserHostStartup.RequireOpen(maintenance);
        CortexCurrentUserLayout.FromProduct(fixture.Layout).EnsureCreated();
        using var legacy = new HeldLegacy(fixture.Layout.LegacyCortexDatabasePath);
        Check(!File.Exists(fixture.Layout.CortexDatabasePath), "The selected DB was not fresh.");
        using (var result = await ImportAsync(corpus, fixture.Layout.CortexDatabasePath, true, token))
            Check(State(result) == "starter-seeded" && result.RootElement.GetProperty("imported").GetInt32() == seeds.Count,
                "The production starter path did not initialize the missing DB.");

        await using (var composition = await CortexMcpLocalComposition.CreateGeneralAsync(
                         fixture.Layout.CortexDatabasePath, null, token))
        {
            var quick = await composition.V2Store.QuickCheckAsync(token);
            Check(quick.Healthy && quick.SchemaVersion == SqliteCortexV2Store.SchemaVersion &&
                quick.CorpusRowsRead == 0, "The new operational schema failed its bounded startup check.");
            Check(composition.ShadowResearchDiagnostics?.State == "not-selected",
                "Clean startup unexpectedly selected optional geometry research.");
            foreach (var seed in seeds)
            {
                var record = await composition.V2Store.ReadHeadAsync(seed.Namespace, "knowledge", seed.RecordId, token);
                Check(record is { Revision: 1 } && record.Title == seed.Title &&
                    record.ContentUtf8.SequenceEqual(Utf8.GetBytes(seed.Content)), "A starter record lost its exact content.");
                using var attributes = JsonDocument.Parse(record!.AttributesJson);
                var metadata = attributes.RootElement;
                var provenance = metadata.GetProperty("provenance").Deserialize<CortexProvenanceV2>(CortexV2Json.Options)!;
                Check(metadata.GetProperty("kind").Deserialize<CortexKnowledgeKindV2>(CortexV2Json.Options) == seed.Descriptor.Kind &&
                    metadata.GetProperty("tier").Deserialize<CortexKnowledgeTierV2>(CortexV2Json.Options) == seed.Descriptor.Tier &&
                    provenance.Source == seed.Descriptor.Source && provenance.SourceId == seed.PackId &&
                    provenance.Evidence == $"pack-revision={seed.PackRevision};source={seed.Descriptor.SourceRelativePath ?? string.Empty}",
                    "A starter record lost its eligibility or portable pack provenance.");
            }
            await CreateMarkerAsync(composition, token);
        }

        using (var automatic = await ImportAsync(corpus, fixture.Layout.CortexDatabasePath, true, token))
            Check(State(automatic) == "existing-store-unchanged", "Automatic seeding altered an existing store.");
        using (var replay = await ImportAsync(corpus, fixture.Layout.CortexDatabasePath, false, token))
            Check(State(replay) == "starter-current" && replay.RootElement.GetProperty("imported").GetInt32() == 0 &&
                replay.RootElement.GetProperty("revised").GetInt32() == 0 &&
                replay.RootElement.GetProperty("unchanged").GetInt32() == seeds.Count, "Guidance replay was not idempotent.");
        await using (var reopened = await CortexMcpLocalComposition.CreateGeneralAsync(
                         fixture.Layout.CortexDatabasePath, null, token))
        {
            var marker = await reopened.V2Store.ReadHeadAsync("project.clean-start", "knowledge", MarkerId, token);
            var counts = await reopened.V2Store.GetCatalogCountersAsync(token);
            Check(marker is { Revision: 1 } && marker.ContentUtf8.SequenceEqual(Utf8.GetBytes(MarkerContent)) &&
                counts.HeadRows == seeds.Count + 1 && counts.RevisionRows == seeds.Count + 1,
                "Reopen or starter replay reset existing records or added revisions.");
            var read = await reopened.Application.CallToolAsync("cortex_read", JsonSerializer.SerializeToElement(new
            {
                @namespace = "project.clean-start", recordType = "knowledge", recordId = MarkerId, revision = 1
            }), token);
            Check(read["isError"]?.GetValue<bool>() == false &&
                read["structuredContent"]?["record"]?["content"]?.GetValue<string>() == MarkerContent,
                "The reopened record is not usable through the normal exact-read operation.");
        }
        legacy.Verify();
        fallback.Verify();
        Check(!File.Exists(fixture.Fallback.CortexDatabasePath), "Startup silently initialized the fallback DB.");
        Check(!Directory.Exists(publicBarrier) && !File.Exists(publicBarrier),
            "Ordinary startup provisioned or cleared the optional privileged barrier.");
    }

    private static async Task OwnerPipeDiagnosticAsync(AgentOpsEngine engine, LocalPeer owner, CancellationToken token)
    {
        var name = "cortex-clean-owner-" + Guid.NewGuid().ToString("N");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        // A unique, owner-authenticated fixture endpoint exposes only this read-only
        // diagnostic. It never contacts the installed service or public host pipes.
        var serving = AuthenticatedPipeHost.RunOperationsAsync(name, owner.Sid,
            (request, peer, ct) => peer.SameIdentity(owner) && request.Op == "diag" && request.Root is null
                ? engine.ExecuteAsync(request, ct)
                : Task.FromResult(new OperationReceipt(false, Guid.NewGuid().ToString("N"),
                    request.Rid ?? Guid.NewGuid().ToString("N"), "failed", request.Op, "auth")), stop.Token);
        try
        {
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await client.ConnectAsync(token);
            var server = LocalIdentity.CaptureExpectedServer(client, owner);
            Check(server.SameIdentity(owner) && server.ProcessId == owner.ProcessId,
                "The fixture owner pipe resolved to another process or identity.");
            await BinaryProtocol.WriteAsync(client, new OperationRequest("diag", new Dictionary<string, string>()), token);
            var result = await BinaryProtocol.ReadAsync<OperationReceipt>(client, token);
            Check(result.Ok, "The freshly initialized ordinary engine did not serve its diagnostic without a service.");
        }
        finally
        {
            stop.Cancel();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static async Task RejectHostStartupAsync(string root, string sid, CancellationToken token)
    {
        foreach (var variant in new[] { "required", "deleted", "incomplete", "untrusted", "unreadable" })
        {
            var fixture = CreateFixture(root, "barrier-" + variant, sid);
            var directory = Path.Combine(fixture.Layout.DataRoot, "execution-maintenance");
            var sentinel = Path.Combine(directory, "required.v1");
            var mode = Path.Combine(directory, "mode.v1.json");
            byte[]? modeBytes = null;
            if (variant != "required") Directory.CreateDirectory(directory);
            if (variant == "deleted")
                await File.WriteAllTextAsync(sentinel, "maintenance/1\n", Utf8, token);
            if (variant is "untrusted" or "unreadable")
            {
                modeBytes = JsonSerializer.SerializeToUtf8Bytes(new MaintenanceModeRecord(1, new string('0', 32),
                    "off", new string('0', 32), new string('0', 32), "", Guid.Empty.ToString("D"), sid, 0, "", null, 1));
                await File.WriteAllBytesAsync(mode, modeBytes, token);
            }
            string[] names = Directory.Exists(directory)
                ? Directory.GetFileSystemEntries(directory).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray()
                : [];
            // An actual exclusive OS handle exercises unreadability without changing
            // machine permissions or pretending to own a protected SYSTEM barrier.
            using var held = variant == "unreadable"
                ? new FileStream(mode, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            var reader = MaintenanceModeStore.OpenReader(requireProvisioned: variant == "required",
                publicDirectoryResolver: () => directory);
            try
            {
                await using var unexpected = UserHostStartup.CreateOwnerEngine(fixture.Layout, reader, new UserHostAdmission());
                throw new InvalidOperationException("An uncertain existing or required barrier admitted owner startup: " + variant);
            }
            catch (AgentOpsException error) when (error.Code == "maintenance_state_unavailable") { }
            Check(!Directory.Exists(fixture.Layout.UserStateDirectory) &&
                !Directory.Exists(fixture.Fallback.UserStateDirectory) && !File.Exists(fixture.Layout.CortexDatabasePath),
                "Refused host startup created owner state or a semantic DB.");
            if (variant == "required")
                Check(!Directory.Exists(directory), "Required missing barrier was provisioned by the reader.");
            else
                Check(Directory.GetFileSystemEntries(directory).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).SequenceEqual(names),
                    "Refused host startup cleared or created barrier entries.");
            if (variant == "deleted")
                Check(await File.ReadAllTextAsync(sentinel, token) == "maintenance/1\n" && !File.Exists(mode),
                    "A deleted mode was silently reset or its required marker changed.");
            if (modeBytes is not null)
            {
                if (held is null)
                    Check((await File.ReadAllBytesAsync(mode, token)).SequenceEqual(modeBytes), "Refusal changed an untrusted barrier.");
                else
                {
                    var observed = new byte[modeBytes.Length];
                    held.ReadExactly(observed);
                    Check(held.Length == modeBytes.Length && observed.SequenceEqual(modeBytes), "Refusal changed an unreadable barrier.");
                }
            }
        }
    }

    private static Task RejectLocationsAsync(string root, string sid)
    {
        var fixture = CreateFixture(root, "locations", sid);
        var layout = fixture.Layout;
        Reject(() => CortexDataLocation.ApplyRegistration(layout, null, layout.DataRoot));
        Reject(() => CortexDataLocation.ApplyRegistration(layout, layout.InstallRoot, ""));
        Reject(() => CortexDataLocation.ApplyRegistration(layout, Path.Combine(root, "other-install"), layout.DataRoot));
        var absent = Path.Combine(root, "unavailable-data");
        var unavailable = CortexProductLayout.ForIsolatedRoots(layout.InstallRoot, absent, sid);
        Reject(() => CortexDataLocation.ApplyRegistration(unavailable, layout.InstallRoot, absent));
        Check(!Directory.Exists(absent) && !Directory.Exists(Path.Combine(layout.InstallRoot, "state")) &&
            !File.Exists(layout.CortexDatabasePath), "Rejected registration created or selected fallback state.");
        return Task.CompletedTask;
    }

    private static async Task RejectSchemasAsync(string root, string sid, string corpus, CancellationToken token)
    {
        foreach (var variant in new[]
                 { "foreign", "corrupt", "generation", "version", "columns", "metadata-columns", "metadata-type", "incomplete" })
        {
            var fixture = CreateFixture(root, variant, sid);
            using var legacy = new HeldLegacy(fixture.Layout.LegacyCortexDatabasePath);
            using var fallback = new HeldLegacy(fixture.Fallback.LegacyCortexDatabasePath);
            var database = fixture.Layout.CortexDatabasePath;
            if (variant == "corrupt")
                await File.WriteAllBytesAsync(database, Utf8.GetBytes("not a SQLite database"), token);
            else if (variant is "foreign" or "version")
                await ExecuteFixtureSqlAsync(database, variant == "foreign"
                    ? "CREATE TABLE foreign_fixture(value TEXT); INSERT INTO foreign_fixture VALUES('preserved');"
                    : """
                      CREATE TABLE cortex_v2_meta(singleton INTEGER PRIMARY KEY, schema_version INTEGER NOT NULL,
                          generation TEXT NOT NULL, created_at_utc TEXT NOT NULL) STRICT;
                      INSERT INTO cortex_v2_meta VALUES(1,999,'pegasus-cortex-sqlite-v2','2026-11-20T00:00:00Z');
                      """, create: true, token);
            else
            {
                await using (var valid = await CortexMcpLocalComposition.CreateGeneralAsync(database, null, token))
                    await CreateMarkerAsync(valid, token);
                var mutation = variant switch
                {
                    "generation" => "UPDATE cortex_v2_meta SET generation='unsupported-fixture-generation';",
                    "columns" => "ALTER TABLE cortex_v2_record_revision RENAME COLUMN content_hash TO incompatible_content_hash;",
                    "metadata-columns" => "ALTER TABLE cortex_v2_meta RENAME COLUMN schema_version TO incompatible_schema_version;",
                    "metadata-type" => """
                        ALTER TABLE cortex_v2_meta RENAME TO fixture_held_meta;
                        CREATE TABLE cortex_v2_meta(singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                            schema_version TEXT NOT NULL, generation TEXT NOT NULL, created_at_utc TEXT NOT NULL) STRICT;
                        INSERT INTO cortex_v2_meta SELECT singleton, schema_version, generation, created_at_utc FROM fixture_held_meta;
                        DROP TABLE fixture_held_meta;
                        """,
                    _ => "DROP TRIGGER cortex_v2_search_head_au;"
                };
                await ExecuteFixtureSqlAsync(database, mutation, create: false, token,
                    preserveSchemaObjects: variant is "columns" or "metadata-columns" or "metadata-type",
                    preserveMarker: true);
            }
            var oldFiles = await ReadDatabaseFilesAsync(database, token);
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                Check(oldFiles[suffix] is null,
                    $"Schema fixture variant={variant} stage=closed-setup file={suffix} still has a companion after construction closed.");
            using (var rejected = await ImportAsync(corpus, database, true, token, success: false))
                Check(State(rejected) == "starter-seed-failed", "An incompatible DB accepted starter import.");
            await AssertDatabaseFilesUnchangedAsync(database, oldFiles, token, variant, "post-import");
            var detail = variant == "version" ? "generation is unsupported" :
                variant.StartsWith("metadata-", StringComparison.Ordinal) ? "cortex_v2_meta" : null;
            await RejectCompositionAsync(database, token, detail);
            await AssertDatabaseFilesUnchangedAsync(database, oldFiles, token, variant, "post-composition");
            // Exact database/file-family equality above also proves the retained
            // marker and foreign sentinel survived. Another SQLite observer here
            // could itself create WAL/SHM after that comparison.
            if (variant == "corrupt")
                Check((await File.ReadAllBytesAsync(database, token)).SequenceEqual(Utf8.GetBytes("not a SQLite database")),
                    "Refusing corrupt input changed its contents.");
            legacy.Verify();
            fallback.Verify();
            Check(!File.Exists(fixture.Fallback.CortexDatabasePath), "Schema refusal opened the fallback DB.");
        }
    }

    private static async Task CreateMarkerAsync(CortexMcpLocalComposition composition, CancellationToken token)
    {
        var write = await composition.Application.CallToolAsync("cortex_knowledge", JsonSerializer.SerializeToElement(new
        {
            @namespace = "project.clean-start", action = "create", recordId = MarkerId,
            idempotencyKey = "clean-start-create", kind = "observation", tier = "verified",
            title = "Preserved fixture", content = MarkerContent, tags = new[] { "fixture" },
            provenance = new { source = "agent", sourceId = "clean-start-fixture" }
        }), token);
        Check(write["isError"]?.GetValue<bool>() == false,
            "The normal knowledge operation did not create the preservation marker.");
    }

    private static async Task<Dictionary<string, byte[]?>> ReadDatabaseFilesAsync(string database, CancellationToken token)
    {
        var snapshot = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var path = database + suffix;
            Check(!Directory.Exists(path), "A fixture database file became a directory.");
            if (!File.Exists(path)) { snapshot.Add(suffix, null); continue; }
            Check(new FileInfo(path).Length <= 2 * 1024 * 1024, "A malformed fixture exceeds the snapshot bound.");
            snapshot.Add(suffix, await File.ReadAllBytesAsync(path, token));
        }
        return snapshot;
    }

    private static async Task AssertDatabaseFilesUnchangedAsync(string database,
        IReadOnlyDictionary<string, byte[]?> before, CancellationToken token, string variant, string stage)
    {
        var after = await ReadDatabaseFilesAsync(database, token);
        foreach (var (suffix, bytes) in before)
        {
            var observed = after[suffix];
            if (bytes is null ? observed is null : observed is not null && bytes.SequenceEqual(observed)) continue;
            var change = bytes is null ? "created" : observed is null ? "removed" : "changed";
            throw new InvalidOperationException(
                $"Schema refusal variant={variant} stage={stage} file={(suffix.Length == 0 ? "db" : suffix)} change={change} " +
                $"before={Describe(bytes)} after={Describe(observed)}.");
        }
        static string Describe(byte[]? bytes) => bytes is null ? "absent" : $"present:{bytes.Length}";
    }

    private static async Task RejectGuidanceAsync(string root, Fixture fixture,
        IReadOnlyList<Seed> seeds, string sid, CancellationToken token)
    {
        var conflictRoot = Path.Combine(root, "conflict-corpus");
        var packRoot = Path.Combine(conflictRoot, "pack");
        Directory.CreateDirectory(Path.Combine(packRoot, "records"));
        var original = seeds[0];
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(original.PackRoot, "behavior-pack.json"), token))!.AsObject();
        var originalDescriptor = manifest["records"]!.AsArray().Single(record =>
            record!["recordId"]!.GetValue<string>() == original.RecordId)!.DeepClone().AsObject();
        originalDescriptor["contentPath"] = "records/conflict.txt";
        var added = new JsonObject
        {
            ["recordId"] = "tenet.clean-start.atomic-candidate", ["kind"] = "tenet", ["tier"] = "explicit",
            ["title"] = "Atomic fixture", ["tags"] = new JsonArray("fixture"),
            ["source"] = "user", ["contentPath"] = "records/new.txt", ["sourceRelativePath"] = "fixture/atomic"
        };
        manifest["records"] = new JsonArray(added, originalDescriptor);
        await File.WriteAllTextAsync(Path.Combine(packRoot, "behavior-pack.json"), manifest.ToJsonString(), Utf8, token);
        await File.WriteAllTextAsync(Path.Combine(packRoot, "records", "conflict.txt"), original.Content + "\nconflicting fixture", Utf8, token);
        await File.WriteAllTextAsync(Path.Combine(packRoot, "records", "new.txt"), "Must roll back with the conflict.", Utf8, token);
        var corpusManifest = new JsonObject
        {
            ["schema"] = "pegasus.cortex.starter-corpus/1.0", ["corpusId"] = "clean-start-conflict",
            ["description"] = "Fixture-only conflict.", ["packs"] = new JsonArray(
                new JsonObject { ["namespace"] = original.Namespace, ["path"] = "pack" })
        };
        await File.WriteAllTextAsync(Path.Combine(conflictRoot, "starter-corpus.json"), corpusManifest.ToJsonString(), Utf8, token);
        using var legacy = new HeldLegacy(fixture.Layout.LegacyCortexDatabasePath, existing: true);
        using (var rejected = await ImportAsync(conflictRoot, fixture.Layout.CortexDatabasePath, false, token, success: false))
            Check(State(rejected) == "starter-seed-failed", "Divergent same-revision guidance was accepted.");
        await using (var reopened = await CortexMcpLocalComposition.CreateGeneralAsync(fixture.Layout.CortexDatabasePath, null, token))
        {
            var existing = await reopened.V2Store.ReadHeadAsync(original.Namespace, "knowledge", original.RecordId, token);
            var absent = await reopened.V2Store.ReadHeadAsync(original.Namespace, "knowledge", "tenet.clean-start.atomic-candidate", token);
            var counts = await reopened.V2Store.GetCatalogCountersAsync(token);
            Check(existing is { Revision: 1 } && existing.ContentUtf8.SequenceEqual(Utf8.GetBytes(original.Content)) &&
                absent is null && counts.HeadRows == seeds.Count + 1 && counts.RevisionRows == seeds.Count + 1,
                "Conflicting guidance partially changed retained state.");
        }
        // Invalid eligibility must be rejected before the importer creates any DB.
        manifest["includesTasks"] = true;
        await File.WriteAllTextAsync(Path.Combine(packRoot, "behavior-pack.json"), manifest.ToJsonString(), Utf8, token);
        var invalid = CreateFixture(root, "invalid-guidance", sid);
        using (var rejected = await ImportAsync(conflictRoot, invalid.Layout.CortexDatabasePath, true, token, success: false))
            Check(State(rejected) == "starter-seed-failed", "Task-bearing starter guidance was accepted.");
        Check(!File.Exists(invalid.Layout.CortexDatabasePath) && !File.Exists(invalid.Fallback.CortexDatabasePath),
            "Invalid guidance initialized a database before validation.");
        legacy.Verify();
    }

    private static async Task CancellationCreatesNoDatabaseAsync(string root, string sid, string corpus)
    {
        var fixture = CreateFixture(root, "cancelled", sid);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await CortexLocalProductCommands.ImportStarterCorpusAsync(corpus, fixture.Layout.CortexDatabasePath,
                true, Actor, TimeProvider.System, cancelled.Token);
            throw new InvalidOperationException("Cancelled starter import continued.");
        }
        catch (OperationCanceledException) { }
        try
        {
            await using var composition = await CortexMcpLocalComposition.CreateGeneralAsync(
                fixture.Layout.CortexDatabasePath, null, cancelled.Token);
            throw new InvalidOperationException("Cancelled composition continued.");
        }
        catch (OperationCanceledException) { }
        Check(!File.Exists(fixture.Layout.CortexDatabasePath) && !File.Exists(fixture.Fallback.CortexDatabasePath),
            "Pre-cancellation created a selected or fallback DB.");
    }

    private static async Task<List<Seed>> StageSourceCorpusAsync(string sourceRoot, string corpus, CancellationToken token)
    {
        await ProductStarterCorpusTests.StageSourceCorpusAsync(sourceRoot, corpus, token);
        var manifest = await CortexFileStarterCorpusManifestV1.ReadAsync(corpus, token);
        var seeds = new List<Seed>();
        foreach (var pack in manifest.Packs)
        {
            var packSource = manifest.ResolvePackRoot(corpus, pack);
            var behavior = await CortexFileBehaviorPackManifestV2.ReadAsync(packSource, token);
            var records = await behavior.LoadRecordsAsync(packSource, token);
            foreach (var record in records)
            {
                seeds.Add(new(pack.Namespace, record.Descriptor, record.Content, packSource, behavior.PackId, behavior.PackRevision));
            }
        }
        return seeds;
    }

    private static async Task<JsonDocument> ImportAsync(string corpus, string database, bool onlyIfEmpty,
        CancellationToken token, bool success = true)
    {
        var result = await CortexLocalProductCommands.ImportStarterCorpusAsync(corpus, database,
            onlyIfEmpty, Actor, TimeProvider.System, token);
        Check(result.ExitCode == (success ? 0 : 1), "Starter import returned an unexpected disposition.");
        var document = JsonDocument.Parse(result.Json);
        try
        {
            Check(document.RootElement.GetProperty("healthy").GetBoolean() == success,
                "Starter import health and exit disagree.");
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static async Task RejectCompositionAsync(string database, CancellationToken token, string? expectedDetail)
    {
        try
        {
            await using var composition = await CortexMcpLocalComposition.CreateGeneralAsync(database, null, token);
            throw new InvalidOperationException("An incompatible DB was admitted.");
        }
        catch (Exception error) when (error is InvalidDataException or SqliteException)
        {
            Check(expectedDetail is null || error.Message.Contains(expectedDetail, StringComparison.Ordinal),
                "The incompatible schema was not rejected at the expected admission boundary.");
        }
    }

    private static async Task ExecuteFixtureSqlAsync(string database, string sql, bool create, CancellationToken token,
        bool preserveSchemaObjects = false, bool preserveMarker = false)
    {
        // Fixture-only construction of deliberately incompatible inputs; never a live path.
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        await connection.OpenAsync(token);
        const string namesQuery = "SELECT group_concat(name, '|') FROM (SELECT name FROM sqlite_schema ORDER BY name);";
        const string markerQuery = "SELECT COUNT(*) FROM cortex_v2_record_head;";
        var objectNames = preserveSchemaObjects ? await ReadFixtureScalarAsync(connection, namesQuery, token) : null;
        if (preserveMarker)
            Check(await ReadFixtureScalarAsync(connection, markerQuery, token) == "1",
                "The valid schema fixture does not contain its preservation marker.");
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
        // Observe schema construction through its existing write connection. A
        // separate read-only WAL connection can leave companions on close and
        // contaminate the exact pre-admission file-family snapshot.
        if (preserveSchemaObjects)
            Check(await ReadFixtureScalarAsync(connection, namesQuery, token) == objectNames,
                "The malformed-shape fixture must retain every original schema object name.");
        if (preserveMarker)
            Check(await ReadFixtureScalarAsync(connection, markerQuery, token) == "1",
                "Constructing an incompatible schema changed its retained marker count.");
    }

    private static async Task<string> ReadFixtureScalarAsync(SqliteConnection connection, string sql, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private sealed class HeldLegacy : IDisposable
    {
        private readonly FileStream _file;
        private static readonly byte[] Sentinel = Utf8.GetBytes("Held legacy fixture; must never be opened or migrated.\r\n");
        public HeldLegacy(string path, bool existing = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _file = new FileStream(path, existing ? FileMode.Open : FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None);
            try
            {
                if (!existing) { _file.Write(Sentinel); _file.Flush(flushToDisk: true); }
                Verify();
            }
            catch { _file.Dispose(); throw; }
        }
        public void Verify()
        {
            Check(_file.Length == Sentinel.Length, "A held legacy fixture changed length.");
            _file.Position = 0;
            var observed = new byte[Sentinel.Length];
            _file.ReadExactly(observed);
            Check(observed.SequenceEqual(Sentinel), "A held legacy fixture changed content.");
        }
        public void Dispose() => _file.Dispose();
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException) { return; }
        throw new InvalidOperationException("Invalid registration was accepted.");
    }
    private static string State(JsonDocument result) => result.RootElement.GetProperty("state").GetString() ?? "";
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string name, ref int passed) { passed++; Console.WriteLine("PASS " + name); }
    private static string FindSourceRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
                if (File.Exists(Path.Combine(current.FullName, "START_HERE.md")) &&
                    File.Exists(Path.Combine(current.FullName, "PegasusCortex.slnx")))
                    return current.FullName;
        throw new DirectoryNotFoundException("The Cortex source root was not found.");
    }
}

