using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Contracts.Assets;
using Pegasus.Cortex.Core.Assets;
using Pegasus.Cortex.Persistence.File.Assets;
using Pegasus.Cortex.Persistence.Sqlite.Assets;
using File = System.IO.File;

namespace Pegasus.Cortex.Persistence.Sqlite.Tests;

[SupportedOSPlatform("windows")]
internal static class HybridAssetStorageTests
{
    public static async Task<int> RunAllAsync()
    {
        var cases = new (string Name, Func<Task> Run)[]
        {
            ("default-cutoff-boundaries", DefaultCutoffBoundariesAsync),
            ("custom-cutoff-zero-and-chunks", CustomCutoffAndZeroAsync),
            ("original-bytes-and-pinned-references", OriginalBytesAndPinnedReferencesAsync),
            ("deduplicate-payload-and-preserve-metadata", DeduplicatePayloadAsync),
            ("cas-conflict-preserves-current", CompareAndSwapAsync),
            ("stream-hash-and-length-admission", StreamAdmissionAsync),
            ("cancellation-rolls-back-staging", CancellationRollsBackAsync),
            ("fresh-open-and-create-admission", CreationAndOpenAdmissionAsync),
            ("foreign-schema-is-not-migrated", ForeignSchemaRejectedAsync),
            ("inline-chunk-tamper-is-rejected", InlineChunkTamperAsync),
            ("canonical-reference-tamper-is-rejected", CanonicalReferenceTamperAsync),
            ("large-file-tamper-is-rejected", LargeFileTamperAsync),
            ("namespace-isolation-and-policy-binding", NamespaceAndPolicyAsync),
            ("legacy-provider-and-read-only-wrapper", LegacyAndReadOnlyWrapperAsync),
            ("readonly-metadata-open-does-not-change-files", ReadOnlyOpenPreservesFilesAsync),
            ("sqlite-page-quota-rolls-back", PageQuotaRollbackAsync),
            ("lower-open-write-budget-preserves-read-and-bytes", LowerWriteBudgetAsync),
            ("oversized-corrupt-derived-text-is-rejected", OversizedDerivedTextAsync)
        };
        foreach (var test in cases)
        {
            await test.Run();
            Console.WriteLine("PASS hybrid-storage." + test.Name);
        }
        return cases.Length;
    }

    private static async Task DefaultCutoffBoundariesAsync()
    {
        using var fixture = new HybridAssetFixture();
        using var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot,
            new CortexHybridAssetOptionsV1());
        const int cutoff = 2 * 1024 * 1024;
        foreach (var size in new[] { cutoff - 1, cutoff, cutoff + 1 })
        {
            var bytes = HybridAssetFixture.Bytes(size, size % 17);
            var asset = await ImportAsync(store, bytes, HybridAssetFixture.Spec("boundary-" + size));
            HybridAssetFixture.Require(asset.Size == size && asset.Sha256 == HybridAssetFixture.Hash(bytes), "Original size/hash changed.");
            HybridAssetFixture.Require(fixture.Scalar("SELECT tier FROM asset_objects WHERE sha=$sha", ("$sha", asset.Sha256)) == (size <= cutoff ? 0 : 1), "Cutoff tier selection differs.");
            if (size <= cutoff)
                HybridAssetFixture.Require(!Directory.Exists(fixture.LargeRoot), "An inline-only import created the large-payload directory.");
            await AssertBytesAsync(store, asset, bytes);
        }
        HybridAssetFixture.Require(fixture.Scalar("SELECT COALESCE(MAX(length(payload)),0) FROM asset_chunks") <= 65536, "SQLite payload chunks exceed 64 KiB.");
        HybridAssetFixture.Require(fixture.Scalar("SELECT COUNT(*) FROM asset_chunks WHERE payload IS NULL") > 0, "File-tier chunk metadata is missing.");
    }

    private static async Task CustomCutoffAndZeroAsync()
    {
        foreach (var cutoff in new long[] { 0, 65536 })
        {
            using var fixture = new HybridAssetFixture();
            using var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot,
                new CortexHybridAssetOptionsV1(InlineCutoffBytes: cutoff, MaximumAssetBytes: 262144));
            foreach (var size in (cutoff == 0 ? new[] { 0, 1 } : new[] { 0, 65535, 65536, 65537 }))
            {
                var bytes = HybridAssetFixture.Bytes(size, 3);
                var asset = await ImportAsync(store, bytes, HybridAssetFixture.Spec("custom-" + size));
                HybridAssetFixture.Require(fixture.Scalar("SELECT tier FROM asset_objects WHERE sha=$sha", ("$sha", asset.Sha256)) == (size <= cutoff ? 0 : 1), "Custom cutoff is not inclusive.");
                await AssertBytesAsync(store, asset, bytes);
            }
            HybridAssetFixture.Require(fixture.Scalar("SELECT COUNT(*) FROM asset_import_chunks") == 0, "Committed imports retained staging rows.");
        }
    }

    private static async Task OriginalBytesAndPinnedReferencesAsync()
    {
        using var fixture = new HybridAssetFixture();
        var options = new CortexHybridAssetOptionsV1(InlineCutoffBytes: 65536, MaximumAssetBytes: 262144);
        CortexAssetV2 first;
        CortexAssetV2 second;
        var firstBytes = HybridAssetFixture.Bytes(65537, 11);
        var secondBytes = HybridAssetFixture.Bytes(4096, 12);
        using (var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot, options))
        {
            var spec = HybridAssetFixture.Spec("immutable") with { Parameters = "{\"z\":2, \"a\":1}" };
            first = await ImportAsync(store, firstBytes, spec);
            second = await ImportAsync(store, secondBytes, spec with { Title = "updated title" }, first.Ref);
            HybridAssetFixture.Require(first.Ref != second.Ref, "A changed original did not create a new reference.");
            HybridAssetFixture.Require(store.Current(spec.Namespace, spec.Id)?.Ref == second.Ref, "The current head did not advance.");
        }
        using (var reopened = CortexSqliteAssetStoreV1.OpenExisting(fixture.Database, fixture.LargeRoot, options))
        {
            HybridAssetFixture.Require(reopened.ReadReference(first.Metadata.Namespace, first.Ref) == first, "The original pinned reference changed after reopen.");
            HybridAssetFixture.Require(reopened.ReadReference(second.Metadata.Namespace, second.Ref) == second, "The new pinned reference changed after reopen.");
            HybridAssetFixture.Require(first.Metadata.Parameters == "{\"z\":2, \"a\":1}", "Opaque metadata text changed.");
            await AssertBytesAsync(reopened, first, firstBytes);
            await AssertBytesAsync(reopened, second, secondBytes);
        }
    }

    private static async Task DeduplicatePayloadAsync()
    {
        using var fixture = new HybridAssetFixture();
        using var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot,
            new CortexHybridAssetOptionsV1(InlineCutoffBytes: 16, MaximumAssetBytes: 262144));
        var bytes = HybridAssetFixture.Bytes(65537, 7);
        var first = await ImportAsync(store, bytes, HybridAssetFixture.Spec("dedup-a"));
        var second = await ImportAsync(store, bytes, HybridAssetFixture.Spec("dedup-b") with { Title = "a different title" });
        var third = await store.ImportExistingObjectAsync(first.Sha256, HybridAssetFixture.Spec("dedup-c"));
        HybridAssetFixture.Require(first.Sha256 == second.Sha256 && third.Sha256 == first.Sha256, "Deduplication changed original hashes.");
        HybridAssetFixture.Require(new[] { first.Ref, second.Ref, third.Ref }.Distinct().Count() == 3, "Distinct metadata lost its references.");
        HybridAssetFixture.Require(fixture.Scalar("SELECT COUNT(*) FROM asset_objects") == 1, "Identical originals created multiple object rows.");
        HybridAssetFixture.Require(fixture.Scalar("SELECT COUNT(*) FROM asset_refs") == 3, "Metadata references were not all retained.");
        HybridAssetFixture.Require(Directory.GetFiles(fixture.LargeRoot).Length == 1, "Identical originals created extra payload/index files.");
        HybridAssetFixture.Require(global::System.IO.File.Exists(Path.Combine(fixture.LargeRoot, first.Sha256)), "The payload filename is not its original SHA-256.");
        await AssertBytesAsync(store, third, bytes);
    }

    private static async Task CompareAndSwapAsync()
    {
        using var fixture = new HybridAssetFixture();
        using var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot,
            new CortexHybridAssetOptionsV1(InlineCutoffBytes: 32, MaximumAssetBytes: 262144));
        var spec = HybridAssetFixture.Spec("cas");
        var first = await ImportAsync(store, HybridAssetFixture.Bytes(16, 1), spec);
        var secondBytes = HybridAssetFixture.Bytes(65537, 2);
        var second = await ImportAsync(store, secondBytes, spec, first.Ref);
        await HybridAssetFixture.ExpectAssetAsync(async () => { await ImportAsync(store, HybridAssetFixture.Bytes(65538, 3), spec, first.Ref); }, "asset_revision_conflict");
        HybridAssetFixture.Require(store.Current(spec.Namespace, spec.Id)?.Ref == second.Ref, "A stale CAS replaced the current head.");
        HybridAssetFixture.Require(fixture.Scalar("SELECT COUNT(*) FROM asset_import_chunks") == 0, "A rejected CAS retained staging rows.");
        await AssertBytesAsync(store, second, secondBytes);
    }

    private static async Task StreamAdmissionAsync()
    {
        using var fixture = new HybridAssetFixture();
        using var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot,
            new CortexHybridAssetOptionsV1(InlineCutoffBytes: 65536, MaximumAssetBytes: 131072));
        var bytes = HybridAssetFixture.Bytes(65537, 6);
        await using (var source = new HybridNonSeekableStream(bytes))
        {
            var asset = await store.ImportStreamAsync(source, bytes.Length, HybridAssetFixture.Spec("nonseek"), expectedContentSha256: HybridAssetFixture.Hash(bytes));
            await AssertBytesAsync(store, asset, bytes);
        }
        await HybridAssetFixture.ExpectAssetAsync(async () =>
        {
            await using var source = new HybridNonSeekableStream(bytes);
            await store.ImportStreamAsync(source, bytes.Length, HybridAssetFixture.Spec("wrong-hash"), expectedContentSha256: new string('0', 64));
        });
        foreach (var length in new long[] { bytes.Length - 1, bytes.Length + 1 })
        {
            await HybridAssetFixture.ExpectAssetAsync(async () =>
            {
                await using var source = new HybridNonSeekableStream(bytes);
                await store.ImportStreamAsync(source, length, HybridAssetFixture.Spec("wrong-length"));
            });
        }
        await HybridAssetFixture.ExpectAssetAsync(async () =>
        {
            await using var source = new HybridNonSeekableStream(HybridAssetFixture.Bytes(131073, 5));
            await store.ImportStreamAsync(source, 131073, HybridAssetFixture.Spec("oversized"));
        });
        foreach (var id in new[] { "wrong-hash", "wrong-length", "oversized" })
            HybridAssetFixture.Require(store.Current("test.hybrid", id) is null, "A rejected stream published a head.");
        HybridAssetFixture.Require(fixture.Scalar("SELECT COUNT(*) FROM asset_import_chunks") == 0, "A rejected stream retained staging rows.");
    }

    private static async Task CancellationRollsBackAsync()
    {
        foreach (var cutoff in new long[] { 0, 262144 })
        {
            using var fixture = new HybridAssetFixture();
            using var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot,
                new CortexHybridAssetOptionsV1(InlineCutoffBytes: cutoff, MaximumAssetBytes: 262144));
            using var cancellation = new CancellationTokenSource();
            await using var source = new HybridNonSeekableStream(HybridAssetFixture.Bytes(131073, 8), cancellation.Cancel);
            try
            {
                await store.ImportStreamAsync(source, 131073, HybridAssetFixture.Spec("cancelled"), cancellationToken: cancellation.Token);
                throw new InvalidOperationException("A cancelled import completed.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            HybridAssetFixture.Require(store.Current("test.hybrid", "cancelled") is null, "Cancellation published a head.");
            HybridAssetFixture.Require(fixture.Scalar("SELECT COUNT(*) FROM asset_objects") == 0, "Cancellation committed an object.");
            HybridAssetFixture.Require(fixture.Scalar("SELECT COUNT(*) FROM asset_import_chunks") == 0, "Cancellation retained staged chunks.");
            HybridAssetFixture.Require(!Directory.Exists(fixture.LargeRoot) || Directory.GetFiles(fixture.LargeRoot).Length == 0, "Cancellation retained a payload or staging file.");
        }
    }

    private static Task CreationAndOpenAdmissionAsync()
    {
        using var fixture = new HybridAssetFixture();
        var options = new CortexHybridAssetOptionsV1();
        HybridAssetFixture.ExpectAsset(() => { using var unused = CortexSqliteAssetStoreV1.OpenExisting(fixture.Database, fixture.LargeRoot, options); }, "asset_store_missing");
        HybridAssetFixture.Require(!global::System.IO.File.Exists(fixture.Database) && !Directory.Exists(fixture.LargeRoot), "OpenExisting created storage.");
        var missingParent = Path.Combine(fixture.Root, "absent-parent");
        HybridAssetFixture.ExpectAsset(() => { using var unused = CortexSqliteAssetStoreV1.CreateNew(Path.Combine(missingParent, "assets.db"), Path.Combine(missingParent, "payloads"), options); });
        HybridAssetFixture.Require(!Directory.Exists(missingParent), "CreateNew created its required private parent.");
        using (var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot, options)) { }
        HybridAssetFixture.ExpectAsset(() => { using var unused = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot, options); }, "asset_store_exists");
        foreach (var invalid in new[] { new CortexHybridAssetOptionsV1(InlineCutoffBytes: -1), new CortexHybridAssetOptionsV1(InlineCutoffBytes: 17, MaximumAssetBytes: 16), new CortexHybridAssetOptionsV1(InlineCutoffBytes: 0, MaximumAssetBytes: 0) })
            HybridAssetFixture.ExpectAsset(() => { using var unused = CortexSqliteAssetStoreV1.CreateNew(Path.Combine(fixture.Root, "invalid.db"), fixture.LargeRoot, invalid); }, "asset_configuration_invalid");
        HybridAssetFixture.Require(!global::System.IO.File.Exists(Path.Combine(fixture.Root, "invalid.db")), "Invalid options created a database.");
        return Task.CompletedTask;
    }

    private static Task ForeignSchemaRejectedAsync()
    {
        foreach (var journal in new[] { "DELETE", "WAL" })
        {
            using var fixture = new HybridAssetFixture();
            fixture.Execute("PRAGMA journal_mode=" + journal + "; CREATE TABLE foreign_fixture(value TEXT NOT NULL); INSERT INTO foreign_fixture VALUES ('synthetic');", create: true);
            var before = fixture.SnapshotFiles();
            HybridAssetFixture.ExpectAsset(() => { using var unused = CortexSqliteAssetStoreV1.OpenExisting(fixture.Database, fixture.LargeRoot, new CortexHybridAssetOptionsV1()); });
            HybridAssetFixture.Require(before.SequenceEqual(fixture.SnapshotFiles()), "Foreign schema/WAL-header refusal changed bytes or created files.");
            HybridAssetFixture.Require(fixture.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'asset_%'") == 0, "A foreign schema was initialized or migrated.");
            HybridAssetFixture.Require(!Directory.Exists(fixture.LargeRoot), "Foreign-schema refusal created a payload directory.");
        }
        return Task.CompletedTask;
    }

    private static async Task InlineChunkTamperAsync()
    {
        using var fixture = new HybridAssetFixture();
        var options = new CortexHybridAssetOptionsV1(InlineCutoffBytes: 131072, MaximumAssetBytes: 262144);
        CortexAssetV2 asset;
        var bytes = HybridAssetFixture.Bytes(65537, 10);
        using (var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot, options))
            asset = await ImportAsync(store, bytes, HybridAssetFixture.Spec("tamper-inline"));
        var changed = bytes[..65536]; changed[100] ^= 0x55;
        fixture.Execute("UPDATE asset_chunks SET payload=$payload WHERE sha=$sha AND ordinal=0", parameters: new[] { ("$payload", (object)changed), ("$sha", (object)asset.Sha256) });
        await HybridAssetFixture.ExpectAssetAsync(async () =>
        {
            using var store = CortexSqliteAssetStoreV1.OpenExisting(fixture.Database, fixture.LargeRoot, options);
            await store.ReadBytesAsync(asset.Metadata.Namespace, asset.Metadata.Id, asset.Ref, 0, 8);
        }, "asset_integrity_failed");
    }

    private static async Task CanonicalReferenceTamperAsync()
    {
        using var fixture = new HybridAssetFixture();
        var options = new CortexHybridAssetOptionsV1();
        CortexAssetV2 asset;
        using (var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot, options))
            asset = await ImportAsync(store, HybridAssetFixture.Bytes(31, 4), HybridAssetFixture.Spec("tamper-ref"));
        fixture.Execute("UPDATE asset_refs SET canonical=$value WHERE ref=$ref", parameters: new[] { ("$value", (object)"{}"), ("$ref", (object)asset.Ref) });
        HybridAssetFixture.ExpectAsset(() =>
        {
            using var store = CortexSqliteAssetStoreV1.OpenExisting(fixture.Database, fixture.LargeRoot, options);
            _ = store.ReadReference(asset.Metadata.Namespace, asset.Ref);
        }, "asset_metadata_integrity_failed");
    }

    private static async Task LargeFileTamperAsync()
    {
        using var fixture = new HybridAssetFixture();
        var options = new CortexHybridAssetOptionsV1(InlineCutoffBytes: 0, MaximumAssetBytes: 262144);
        CortexAssetV2 asset;
        var bytes = HybridAssetFixture.Bytes(65537, 13);
        using (var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot, options))
            asset = await ImportAsync(store, bytes, HybridAssetFixture.Spec("tamper-file"));
        bytes[100] ^= 0x33;
        global::System.IO.File.WriteAllBytes(Path.Combine(fixture.LargeRoot, asset.Sha256), bytes);
        await HybridAssetFixture.ExpectAssetAsync(async () =>
        {
            using var store = CortexSqliteAssetStoreV1.OpenExisting(fixture.Database, fixture.LargeRoot, options);
            await store.ReadBytesAsync(asset.Metadata.Namespace, asset.Metadata.Id, asset.Ref, 0, 8);
        }, "asset_integrity_failed");
    }

    private static async Task LegacyAndReadOnlyWrapperAsync()
    {
        using var fixture = new HybridAssetFixture();
        using var sql = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot,
            new CortexHybridAssetOptionsV1(InlineCutoffBytes: 64, MaximumAssetBytes: 262144));
        var legacy = new CortexAssetStoreV2(Path.Combine(fixture.Root, "legacy-fixture"), 262144);
        var bytes = HybridAssetFixture.Bytes(65537, 15);
        var source = Path.Combine(fixture.Root, "synthetic-original.bin");
        global::System.IO.File.WriteAllBytes(source, bytes);
        var spec = HybridAssetFixture.Spec("cross-provider");
        var legacyAsset = await legacy.ImportAsync(source, spec, expectedContentSha256: HybridAssetFixture.Hash(bytes));
        var sqlAsset = await ImportAsync(sql, bytes, spec);
        HybridAssetFixture.Require(legacyAsset == sqlAsset, "Physical provider selection changed assets/2 identity or metadata.");
        var readOnly = new CortexReadOnlyAssetStoreV1(legacy);
        HybridAssetFixture.Require(readOnly.ReadReference(spec.Namespace, legacyAsset.Ref) == legacyAsset, "The real legacy reference did not pass through its read-only wrapper.");
        await AssertBytesAsync(readOnly, legacyAsset, bytes);
        await HybridAssetFixture.ExpectAssetAsync(async () => { await readOnly.ImportAsync(source, spec); }, "asset_read_only");
        await HybridAssetFixture.ExpectAssetAsync(() => readOnly.ExposeAsync("other.scope"), "asset_read_only");
        HybridAssetFixture.Require(!readOnly.RecordIssue(spec.Namespace, spec.Id, legacyAsset.Ref, "fixture_failure"), "The read-only wrapper recorded an issue.");
        HybridAssetFixture.Require(legacy.Current(spec.Namespace, spec.Id)?.Ref == legacyAsset.Ref, "A refused write changed legacy current state.");
    }

    private static async Task ReadOnlyOpenPreservesFilesAsync()
    {
        using var fixture = new HybridAssetFixture();
        var options = new CortexHybridAssetOptionsV1();
        CortexAssetV2 asset;
        using (var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot, options))
            asset = await ImportAsync(store, HybridAssetFixture.Bytes(32, 4), HybridAssetFixture.Spec("readonly-open"));
        var before = fixture.SnapshotFiles();
        using (var store = CortexSqliteAssetStoreV1.OpenExisting(fixture.Database, fixture.LargeRoot, options))
        {
            HybridAssetFixture.Require(store.ReadReference(asset.Metadata.Namespace, asset.Ref) == asset, "Metadata changed after readonly open.");
            HybridAssetFixture.Require(store.Current(asset.Metadata.Namespace, asset.Metadata.Id)?.Ref == asset.Ref, "Current changed after readonly open.");
            _ = store.Search(asset.Metadata.Namespace, "fixture");
            _ = store.ExposedNamespaces();
        }
        HybridAssetFixture.Require(before.SequenceEqual(fixture.SnapshotFiles()), "Readonly open/metadata reads changed database bytes or created auxiliary files.");
    }

    private static async Task PageQuotaRollbackAsync()
    {
        using var fixture = new HybridAssetFixture();
        using var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot,
            new CortexHybridAssetOptionsV1(InlineCutoffBytes: 2097152, MaximumAssetBytes: 2097152, MaximumDatabasePages: 256));
        var original = await ImportAsync(store, HybridAssetFixture.Bytes(16, 2), HybridAssetFixture.Spec("quota-existing"));
        await HybridAssetFixture.ExpectAssetAsync(async () =>
        {
            await ImportAsync(store, HybridAssetFixture.Bytes(2097152, 3), HybridAssetFixture.Spec("quota-oversized"));
        });
        HybridAssetFixture.Require(store.Current("test.hybrid", "quota-oversized") is null, "A database-full import published a head.");
        HybridAssetFixture.Require(store.Current("test.hybrid", "quota-existing")?.Ref == original.Ref, "A database-full import damaged the existing head.");
        HybridAssetFixture.Require(fixture.Scalar("SELECT COUNT(*) FROM asset_import_chunks") == 0, "A database-full import retained staged chunks.");
        HybridAssetFixture.Require(fixture.Scalar("SELECT COUNT(*) FROM asset_objects") == 1, "A database-full import committed a partial object.");
    }

    private static async Task LowerWriteBudgetAsync()
    {
        using var fixture = new HybridAssetFixture();
        var options = new CortexHybridAssetOptionsV1(InlineCutoffBytes: 2097152, MaximumAssetBytes: 2097152, MaximumDatabasePages: 2048);
        CortexAssetV2 original;
        using (var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot, options))
            original = await ImportAsync(store, HybridAssetFixture.Bytes(2097152, 17), HybridAssetFixture.Spec("lower-budget-original"));
        HybridAssetFixture.Require(fixture.Scalar("PRAGMA page_count") > 256, "The write-budget fixture did not exceed its proposed lower ceiling.");
        var before = fixture.SnapshotFiles();
        using (var store = CortexSqliteAssetStoreV1.OpenExisting(fixture.Database, fixture.LargeRoot, options with { MaximumDatabasePages = 256 }))
        {
            HybridAssetFixture.Require(store.ReadReference(original.Metadata.Namespace, original.Ref) == original, "A lower write ceiling blocked valid reads.");
            await HybridAssetFixture.ExpectAssetAsync(async () =>
            {
                await ImportAsync(store, HybridAssetFixture.Bytes(1, 1), HybridAssetFixture.Spec("lower-budget-rejected"));
            }, "asset_capacity_reached");
            HybridAssetFixture.Require(store.Current(original.Metadata.Namespace, original.Metadata.Id)?.Ref == original.Ref &&
                store.Current("test.hybrid", "lower-budget-rejected") is null, "A rejected lower-budget write changed current heads.");
        }
        HybridAssetFixture.Require(before.SequenceEqual(fixture.SnapshotFiles()), "A lower-budget write refusal changed database bytes or files.");
    }

    private static async Task OversizedDerivedTextAsync()
    {
        foreach (var field in new[] { "namespace-generation", "object-file-name", "head-reference", "canonical", "chunk-hash" })
        {
            using var fixture = new HybridAssetFixture();
            var options = new CortexHybridAssetOptionsV1(InlineCutoffBytes: 0, MaximumAssetBytes: 262144);
            CortexAssetV2 asset;
            using (var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot, options))
                asset = await ImportAsync(store, HybridAssetFixture.Bytes(1024, 18), HybridAssetFixture.Spec("oversized-text"));
            var assignment = field switch
            {
                "namespace-generation" => "UPDATE asset_namespaces SET generation=$text WHERE ns='test.hybrid';",
                "object-file-name" => "UPDATE asset_objects SET file_name=$text;",
                "head-reference" => "UPDATE asset_heads SET ref=$text;",
                "canonical" => "UPDATE asset_refs SET canonical=$text;",
                _ => "UPDATE asset_chunks SET hash=$text;"
            };
            // Only a newly constructed disposable database is deliberately corrupted.
            fixture.Execute("PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON; " + assignment,
                parameters: new[] { ("$text", (object)new string('f', 128 * 1024)) });
            var corrupted = fixture.SnapshotFiles();
            await HybridAssetFixture.ExpectAssetAsync(async () =>
            {
                using var store = CortexSqliteAssetStoreV1.OpenExisting(fixture.Database, fixture.LargeRoot, options);
                if (field == "namespace-generation") _ = store.Search("test.hybrid", "fixture");
                else if (field == "head-reference") _ = store.Current("test.hybrid", asset.Metadata.Id);
                else if (field == "canonical") _ = store.ReadReference("test.hybrid", asset.Ref);
                else _ = await store.ReadBytesAsync("test.hybrid", asset.Metadata.Id, asset.Ref, 0, 8);
            });
            HybridAssetFixture.Require(corrupted.SequenceEqual(fixture.SnapshotFiles()), "A corrupt-text read refusal changed storage bytes or files.");
        }
    }

    private static async Task NamespaceAndPolicyAsync()
    {
        using var fixture = new HybridAssetFixture();
        var options = new CortexHybridAssetOptionsV1(InlineCutoffBytes: 64, MaximumAssetBytes: 262144);
        CortexAssetV2 asset;
        using (var store = CortexSqliteAssetStoreV1.CreateNew(fixture.Database, fixture.LargeRoot, options))
        {
            asset = await ImportAsync(store, HybridAssetFixture.Bytes(65, 1), HybridAssetFixture.Spec("scoped"));
            HybridAssetFixture.Require(store.Current("other.scope", asset.Metadata.Id) is null, "A current head crossed namespaces.");
            HybridAssetFixture.ExpectAsset(() => _ = store.ReadReference("other.scope", asset.Ref));
            HybridAssetFixture.Require(store.Search("other.scope", "fixture").Items.Count == 0, "Search crossed namespaces.");
        }
        HybridAssetFixture.ExpectAsset(() => { using var unused = CortexSqliteAssetStoreV1.OpenExisting(fixture.Database, fixture.LargeRoot, options with { InlineCutoffBytes = 65 }); }, "asset_policy_mismatch");
        using var reopened = CortexSqliteAssetStoreV1.OpenExisting(fixture.Database, fixture.LargeRoot, options);
        HybridAssetFixture.Require(reopened.Current(asset.Metadata.Namespace, asset.Metadata.Id)?.Ref == asset.Ref, "A refused policy change damaged the head.");
    }

    private static async Task<CortexAssetV2> ImportAsync(ICortexAssetStreamStoreV1 store, byte[] bytes, CortexAssetSpecV2 spec, string? expectedRef = null)
    {
        await using var input = new MemoryStream(bytes, writable: false);
        return await store.ImportStreamAsync(input, bytes.LongLength, spec, expectedRef, expectedContentSha256: HybridAssetFixture.Hash(bytes));
    }

    private static async Task AssertBytesAsync(ICortexAssetStoreV2 store, CortexAssetV2 asset, byte[] expected)
    {
        if (expected.Length == 0)
        {
            var empty = await store.ReadBytesAsync(asset.Metadata.Namespace, asset.Metadata.Id, asset.Ref, 0, 1);
            HybridAssetFixture.Require(empty.Bytes.Length == 0 && empty.TotalSize == 0 && empty.ContentSha256 == HybridAssetFixture.Hash(expected), "The empty original did not round-trip.");
            return;
        }
        for (var offset = 0; offset < expected.Length; offset += 65536)
        {
            var length = Math.Min(65536, expected.Length - offset);
            var result = await store.ReadBytesAsync(asset.Metadata.Namespace, asset.Metadata.Id, asset.Ref, offset, length);
            HybridAssetFixture.Require(result.Bytes.AsSpan().SequenceEqual(expected.AsSpan(offset, length)), "Original range bytes changed.");
            HybridAssetFixture.Require(result.Offset == offset && result.TotalSize == expected.Length && result.ContentSha256 == asset.Sha256 && result.Sha256 == HybridAssetFixture.Hash(result.Bytes), "Range provenance differs.");
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class HybridAssetFixture : IDisposable
{
    public string Root { get; }
    public string Database => Path.Combine(Root, "assets.db");
    public string LargeRoot => Path.Combine(Root, "payloads");

    public HybridAssetFixture()
    {
        Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cortex-hybrid-fixture-" + Guid.NewGuid().ToString("N")));
        if (Directory.Exists(Root) || global::System.IO.File.Exists(Root)) throw new InvalidOperationException("A new fixture root is required.");
        var owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No fixture owner SID.");
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).Create(security);
    }

    public void Dispose()
    {
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(Root).StartsWith("cortex-hybrid-fixture-", StringComparison.Ordinal) || Path.GetFileName(Root).Length != "cortex-hybrid-fixture-".Length + 32)
            throw new InvalidOperationException("Fixture cleanup escaped its exact new temporary root.");
        if (Directory.Exists(Root))
        {
            if ((global::System.IO.File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture root became a reparse point.");
            Directory.Delete(Root, recursive: true);
        }
    }

    public string[] SnapshotFiles() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(Root, path) + ":" + Hash(global::System.IO.File.ReadAllBytes(path))).ToArray();

    public long Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open(create: false);
        using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var pair in parameters) command.Parameters.AddWithValue(pair.Name, pair.Value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Execute(string sql, bool create = false, (string Name, object Value)[]? parameters = null)
    {
        using var connection = Open(create);
        using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var pair in parameters ?? Array.Empty<(string, object)>()) command.Parameters.AddWithValue(pair.Name, pair.Value);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open(bool create)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        connection.Open(); return connection;
    }

    public static CortexAssetSpecV2 Spec(string id) => new("test.hybrid", id, "fixture title", "synthetic fixture", "application/octet-stream", License: "CC0-1.0", Parameters: "{\"fixture\":true}");
    public static byte[] Bytes(int length, int salt) { var bytes = new byte[length]; for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i * 31 + salt) & 255); return bytes; }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static void ExpectAsset(Action action, string? code = null)
    {
        try { action(); }
        catch (CortexAssetException failure) when (code is null || failure.Code == code) { return; }
        throw new InvalidOperationException("Expected typed asset refusal" + (code is null ? "." : ": " + code));
    }
    public static async Task ExpectAssetAsync(Func<Task> action, string? code = null)
    {
        try { await action(); }
        catch (CortexAssetException failure) when (code is null || failure.Code == code) { return; }
        throw new InvalidOperationException("Expected typed asset refusal" + (code is null ? "." : ": " + code));
    }
}

internal sealed class HybridNonSeekableStream(byte[] bytes, Action? afterFirstRead = null) : Stream
{
    private readonly MemoryStream inner = new(bytes, writable: false);
    private bool notified;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) { var result = inner.Read(buffer, offset, Math.Min(count, 16384)); Notify(result); return result; }
    public override int Read(Span<byte> buffer) { var result = inner.Read(buffer[..Math.Min(buffer.Length, 16384)]); Notify(result); return result; }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer, offset, count)); }
    private void Notify(int count) { if (count > 0 && !notified) { notified = true; afterFirstRead?.Invoke(); } }
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
