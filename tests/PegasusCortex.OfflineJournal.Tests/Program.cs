using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Sync;
using Pegasus.Cortex.OfflineJournal.Contracts;
using Pegasus.Cortex.OfflineJournal.Import;
using Pegasus.Cortex.OfflineJournal.Journal;
using Pegasus.Cortex.OfflineJournal.Paths;
using Pegasus.Cortex.OfflineJournal.Security;

var tests = new (string Name, Func<Task> Run)[]
{
    ("append is chained, idempotent, and conflict-safe", TestAppendIdempotencyAsync),
    ("tampered pending payload is quarantined", TestTamperRecoveryAsync),
    ("bounds reject before read and cancellation cleans staging", TestBoundsAndCancellationAsync),
    ("legacy evidence receipt is durable but does not advance typed sync state", TestReceiptOrderingAsync),
    ("legacy import is copy-first, resumable, and source-preserving", TestLegacyCopyAsync),
    ("approved root is exact and explicit", TestApprovedRootAsync),
    ("production filesystem authority rejects lexical and root drift", TestFileSystemAuthorityGateAsync),
    ("host-detected path swap fails closed before mutation", TestDetectedPathSwapAsync),
    ("legacy source capture requires exact authority approval", TestLegacySourceAuthorityAsync),
    ("boundary durability is reported without lexical overclaim", TestBoundaryDurabilityAsync),
    ("AES-GCM protection is bounded, authenticated, and rotation-safe", TestAesGcmProtectionAsync),
    ("unavailable payload keys preserve retained entries", TestUnavailablePayloadKeyRecoveryAsync)
    ,("tail loss and future state schemas remain fail-closed", TestDurableHighWaterAsync)
    ,("public export is bounded, typed, and authenticated", TestPublicExportAsync)
    ,("typed receipts project uploaded and reconciled separately", TestTypedReceiptProjectionAsync)
    ,("legacy public conversion requires planner proof and typed chunks", TestLegacyProofAndExportAsync)
    ,("legacy paged export is deterministic resumable and source preserving", TestLegacyPagedExportAsync)
    ,("repository V2 cortex tree is rejected as a legacy import without mutation", TestRepositoryV2CortexIsNotLegacySourceAsync)
    ,("legacy export rejects aggregate plaintext before decrypting", TestLegacyExportAggregateBoundAsync)
    ,("legacy capture rejects source namespace drift and alternate leaves", TestLegacySourceNamespaceDriftAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add(test.Name);
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

Console.WriteLine($"RESULT {tests.Length - failures.Count}/{tests.Length} passed");
return failures.Count == 0 ? 0 : 1;

static async Task TestAppendIdempotencyAsync()
{
    var root = CreateTemporaryPath();
    try
    {
        var verifier = new TestReceiptVerifier();
        await using var journal = CreateJournal(root, new TestPayloadProtector(), verifier);
        var request = Request("operation-1", "op.write");
        var first = await AppendTextAsync(journal, request, "first payload");
        var duplicate = await AppendTextAsync(journal, request, "first payload");
        var conflict = await AppendTextAsync(journal, request, "changed payload");
        var second = await AppendTextAsync(journal, Request("operation-2", "op.write"), "second payload");

        Assert.Equal(JournalMutationDisposition.Added, first.Disposition);
        Assert.Equal(JournalMutationDisposition.Existing, duplicate.Disposition);
        Assert.Equal(first.Entry!.EntrySha256, duplicate.Entry!.EntrySha256);
        Assert.Equal(JournalMutationDisposition.Conflict, conflict.Disposition);
        Assert.Equal("offline_journal_idempotency_conflict", conflict.Diagnostic!.Code);
        Assert.Equal(JournalMutationDisposition.Added, second.Disposition);
        Assert.Equal(2L, second.Entry!.Sequence);
        Assert.Equal(first.Entry.EntrySha256, second.Entry.PreviousEntrySha256);

        var pending = await journal.ReadPendingAsync(10, CancellationToken.None);
        Assert.Equal(2, pending.Count);
        var durableState = await journal.ReadStateAsync(CancellationToken.None);
        Assert.Equal(2L, durableState.HighWaterSequence);
        Assert.Equal(second.Entry.EntrySha256, durableState.LastEntrySha256);
        Assert.True(journal.Durability.FileContentFlush && journal.Durability.AtomicSameVolumePromotion);
        Assert.False(journal.Durability.ParentDirectoryFlush);
    }
    finally
    {
        DeleteTemporaryPath(root);
    }
}

static async Task TestTamperRecoveryAsync()
{
    var root = CreateTemporaryPath();
    try
    {
        await using (var journal = CreateJournal(root, new TestPayloadProtector(), new TestReceiptVerifier()))
        {
            var added = await AppendTextAsync(journal, Request("tamper-1", "op.write"), "protected payload");
            Assert.Equal(JournalMutationDisposition.Added, added.Disposition);
        }

        var payloadPath = Directory.EnumerateFiles(Path.Combine(root, "j", "p"), "p.bin", SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(payloadPath);
        bytes[0] ^= 0x5a;
        await File.WriteAllBytesAsync(payloadPath, bytes);

        await using (var recoveredJournal = CreateJournal(root, new TestPayloadProtector(), new TestReceiptVerifier()))
        {
            var recovery = await recoveredJournal.RecoverAsync(CancellationToken.None);
            Assert.False(recovery.Ready);
            Assert.Equal(1, recovery.QuarantinedEntries);
        }
        Assert.Equal(0, Directory.EnumerateDirectories(Path.Combine(root, "j", "p")).Count());
        Assert.Equal(1, Directory.EnumerateDirectories(Path.Combine(root, "j", "q")).Count());
        await using var secondRecovery = CreateJournal(root, new TestPayloadProtector(), new TestReceiptVerifier());
        var persisted = await secondRecovery.RecoverAsync(CancellationToken.None);
        Assert.False(persisted.Ready);
        Assert.True(persisted.Diagnostics.Any(item =>
            item.Code is "offline_journal_high_water_gap" or "offline_journal_quarantine_present"));
        Assert.Equal(
            JournalMutationDisposition.Rejected,
            (await AppendTextAsync(secondRecovery, Request("tamper-reuse", "op.write"), "must not append")).Disposition);
    }
    finally
    {
        DeleteTemporaryPath(root);
    }
}

static async Task TestBoundsAndCancellationAsync()
{
    var root = CreateTemporaryPath();
    try
    {
        var options = new OfflineJournalOptions
        {
            MaxPlaintextBytes = 1024,
            MaxProtectedPayloadBytes = 2048,
            MaxPendingBytes = 128 * 1024,
            MaxRetainedBytes = 256 * 1024,
            MaxReceiptEvidenceBytes = 1024,
            MaxLegacyImportFileBytes = 1024,
            MaxLegacyImportTotalBytes = 2048,
            MaxLegacyManifestBytes = 64 * 1024
        };
        var unread = new CountingReadStream(new byte[1025]);
        await using (var journal = CreateJournal(root, new TestPayloadProtector(), new TestReceiptVerifier(), options))
        {
            var rejected = await journal.AppendAsync(
                Request("oversize-1", "op.write", declaredBytes: 1025),
                unread,
                leaveOpen: true,
                CancellationToken.None);
            Assert.Equal(JournalMutationDisposition.Rejected, rejected.Disposition);
            Assert.Equal(0, unread.ReadCount);
        }

        var cancellationRoot = CreateTemporaryPath();
        try
        {
            await using var cancelingJournal = CreateJournal(
                cancellationRoot,
                new BlockingPayloadProtector(),
                new TestReceiptVerifier(),
                options);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsCanceledAsync(() => cancelingJournal.AppendAsync(
                Request("cancel-1", "op.write", declaredBytes: 7),
                new MemoryStream(Encoding.UTF8.GetBytes("payload"), writable: false),
                leaveOpen: false,
                cancellation.Token).AsTask());
            Assert.Equal(0, Directory.EnumerateDirectories(Path.Combine(cancellationRoot, "j", "s")).Count());
            Assert.Equal(0, Directory.EnumerateDirectories(Path.Combine(cancellationRoot, "j", "p")).Count());
        }
        finally
        {
            DeleteTemporaryPath(cancellationRoot);
        }
    }
    finally
    {
        DeleteTemporaryPath(root);
    }
}

static async Task TestReceiptOrderingAsync()
{
    var root = CreateTemporaryPath();
    try
    {
        var verifier = new TestReceiptVerifier { Accept = false };
        var protector = new TestPayloadProtector();
        await using (var journal = CreateJournal(root, protector, verifier))
        {
            var added = await AppendTextAsync(journal, Request("ack-entry-1", "op.write"), "receipt payload");
            var entry = added.Entry!;
            var evidenceBytes = ReceiptEvidence(entry, "receipt-1");
            var request = ReceiptRequest(entry, "receipt-1", evidenceBytes.Length);

            var rejected = await journal.RecordAcknowledgmentAsync(
                request,
                new MemoryStream(evidenceBytes, writable: false),
                leaveOpen: false,
                CancellationToken.None);
            Assert.Equal(JournalMutationDisposition.Rejected, rejected.Disposition);
            Assert.False(Directory.Exists(Path.Combine(root, "j", "r", entry.EntryId)));
            Assert.Equal(1, (await journal.ReadPendingAsync(10, CancellationToken.None)).Count);

            verifier.Accept = true;
            var accepted = await journal.RecordAcknowledgmentAsync(
                request,
                new MemoryStream(evidenceBytes, writable: false),
                leaveOpen: false,
                CancellationToken.None);
            Assert.Equal(JournalMutationDisposition.Added, accepted.Disposition);
            var receiptRoot = Path.Combine(root, "j", "r", entry.EntryId);
            Assert.True(File.Exists(Path.Combine(receiptRoot, "r.json")));
            Assert.True(File.Exists(Path.Combine(receiptRoot, "e.bin")));
            Assert.SequenceEqual(evidenceBytes, await File.ReadAllBytesAsync(Path.Combine(receiptRoot, "e.bin")));
            Assert.Equal(1, Directory.EnumerateDirectories(Path.Combine(root, "j", "p")).Count());
            Assert.Equal(1, (await journal.ReadPendingAsync(10, CancellationToken.None)).Count);
            Assert.Equal(
                JournalEntrySyncState.Pending,
                (await journal.ReadSyncStatusAsync(10, CancellationToken.None)).Single().State);
        }

        await using var reopened = CreateJournal(root, protector, verifier);
        var recovery = await reopened.RecoverAsync(CancellationToken.None);
        Assert.True(recovery.Ready);
        Assert.Equal(1, recovery.PendingEntries);
        Assert.Equal(1, (await reopened.ReadPendingAsync(10, CancellationToken.None)).Count);
        Assert.Equal(1, Directory.EnumerateDirectories(Path.Combine(root, "j", "p")).Count());
    }
    finally
    {
        DeleteTemporaryPath(root);
    }
}

static async Task TestLegacyCopyAsync()
{
    var journalRoot = CreateTemporaryPath();
    var sourceRoot = CreateTemporaryPath();
    try
    {
        var cortexRoot = Path.Combine(sourceRoot, ".cortex");
        Directory.CreateDirectory(cortexRoot);
        await File.WriteAllTextAsync(Path.Combine(cortexRoot, "STATE.md"), "state\nline two\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(cortexRoot, "meta.json"), "{\"schemaVersion\":\"0.2\"}\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(cortexRoot, "COPY1.md"), "same bytes", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(cortexRoot, "COPY2.md"), "same bytes", new UTF8Encoding(false));
        await File.WriteAllBytesAsync(Path.Combine(cortexRoot, "EMPTY.bin"), []);
        var before = SnapshotFiles(cortexRoot);
        var capturedAt = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        await using var planner = new LegacyCortexImportPlanner(
            journalRoot,
            journalRoot,
            new OfflineJournalIdentity("journal-1", "writer-1", "store-1", "project-1"),
            new TestPayloadProtector());

        var copied = await planner.InventoryAndCopyAsync(
            cortexRoot, cortexRoot, "migration-1", capturedAt, CancellationToken.None);
        var repeated = await planner.InventoryAndCopyAsync(
            cortexRoot, cortexRoot, "migration-1", capturedAt, CancellationToken.None);
        var after = SnapshotFiles(cortexRoot);

        Assert.Equal(LegacyCortexImportDisposition.Copied, copied.Disposition);
        Assert.Equal(LegacyCortexImportDisposition.Existing, repeated.Disposition);
        Assert.Equal(5, copied.Manifest!.Items.Count);
        Assert.Equal(copied.Manifest.ManifestSha256, repeated.Manifest!.ManifestSha256);
        Assert.DictionaryEqual(before, after);
        foreach (var item in copied.Manifest.Items)
        {
            var sourceSnapshot = before[item.RelativePath];
            Assert.Equal(sourceSnapshot.Bytes, item.SourceBytes);
            Assert.Equal(sourceSnapshot.Sha256, item.SourceSha256);
            Assert.True(File.Exists(Path.Combine(journalRoot, "j", "i", "migration-1", item.StoredFileName)));
            if (item.SourceBytes > 0)
                Assert.False(string.Equals(item.SourceSha256, item.StoredSha256, StringComparison.Ordinal));
        }
        var publicManifest = LegacyCortexImportAdapter.ToPublicManifest(
            copied.Verified!,
            new LegacyCortexPublicManifestIdentity("store-1", "epoch-1", "project-1", "source-root-1"));
        Assert.True(CortexSyncContractPolicy.HasValidHash(publicManifest));
        Assert.False(publicManifest.SourceMutationAllowed);
        Assert.Equal("prohibited_until_separate_approval", publicManifest.CleanupState);
        Assert.Equal("not_approved", publicManifest.CutoverState);
        Assert.Equal("state", publicManifest.Files.Single(item => item.SourceRelativePath == "STATE.md").Role);
        Assert.True(publicManifest.Files.All(item => item.LifecycleState == CortexLegacyFileLifecycleIds.Validated));
        Assert.Equal(
            publicManifest.Files.Count,
            publicManifest.Files.SelectMany(item => item.MappedIds).Distinct(StringComparer.Ordinal).Count());
    }
    finally
    {
        DeleteTemporaryPath(journalRoot);
        DeleteTemporaryPath(sourceRoot);
    }
}

static Task TestApprovedRootAsync()
{
    var requested = CreateTemporaryPath();
    var approved = CreateTemporaryPath();
    try
    {
        Assert.Throws<ArgumentException>(() => CreateJournal(
            requested,
            new TestPayloadProtector(),
            new TestReceiptVerifier(),
            approvedRoot: approved));
        return Task.CompletedTask;
    }
    finally
    {
        DeleteTemporaryPath(requested);
        DeleteTemporaryPath(approved);
    }
}

static Task TestFileSystemAuthorityGateAsync()
{
    var requested = CreateTemporaryPath();
    var drifted = CreateTemporaryPath();
    try
    {
        Assert.Throws<NotSupportedException>(() => new OfflineJournal(
            requested,
            requested,
            new OfflineJournalIdentity("journal-1", "writer-1", "store-1", "project-1"),
            new TestPayloadProtector(),
            new TestReceiptVerifier(),
            LexicalTestOnlyJournalFileSystemBoundaryFactory.Instance));

        var factory = new RootDriftBoundaryFactory(drifted);
        Assert.Throws<UnauthorizedAccessException>(() => new OfflineJournal(
            requested,
            requested,
            new OfflineJournalIdentity("journal-1", "writer-1", "store-1", "project-1"),
            new TestPayloadProtector(),
            new TestReceiptVerifier(),
            factory));
        Assert.True(factory.LastBoundary?.Disposed == true, "A rejected host boundary must be disposed.");

        var caseVariant = TogglePathCase(requested);
        Assert.Throws<ArgumentException>(() => new OfflineJournal(
            requested,
            caseVariant,
            new OfflineJournalIdentity("journal-1", "writer-1", "store-1", "project-1"),
            new TestPayloadProtector(),
            new TestReceiptVerifier(),
            new RootDriftBoundaryFactory(requested)));
        var caseBoundary = new RootDriftBoundaryFactory(caseVariant);
        Assert.Throws<UnauthorizedAccessException>(() => new OfflineJournal(
            requested,
            requested,
            new OfflineJournalIdentity("journal-1", "writer-1", "store-1", "project-1"),
            new TestPayloadProtector(),
            new TestReceiptVerifier(),
            caseBoundary));
        Assert.True(caseBoundary.LastBoundary?.Disposed == true);

        var identityMismatch = new RootDriftBoundaryFactory(requested, "approved-root-id", "opened-root-id");
        Assert.Throws<UnauthorizedAccessException>(() => new OfflineJournal(
            requested,
            requested,
            new OfflineJournalIdentity("journal-1", "writer-1", "store-1", "project-1"),
            new TestPayloadProtector(),
            new TestReceiptVerifier(),
            identityMismatch));
        Assert.True(identityMismatch.LastBoundary?.Disposed == true);
        return Task.CompletedTask;
    }
    finally
    {
        DeleteTemporaryPath(requested);
        DeleteTemporaryPath(drifted);
    }
}

static async Task TestLegacySourceAuthorityAsync()
{
    var journalRoot = CreateTemporaryPath();
    var requestedSource = CreateTemporaryPath();
    var approvedSource = CreateTemporaryPath();
    try
    {
        await File.WriteAllTextAsync(
            Path.Combine(requestedSource, "meta.json"),
            "{\"schemaVersion\":\"0.2\"}\n",
            new UTF8Encoding(false));
        var before = SnapshotFiles(requestedSource);
        await using var planner = new LegacyCortexImportPlanner(
            journalRoot,
            journalRoot,
            new OfflineJournalIdentity("journal-1", "writer-1", "store-1", "project-1"),
            new TestPayloadProtector());

        var result = await planner.InventoryAndCopyAsync(
            requestedSource,
            approvedSource,
            "migration-unapproved",
            new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(LegacyCortexImportDisposition.Rejected, result.Disposition);
        Assert.DictionaryEqual(before, SnapshotFiles(requestedSource));
        Assert.Equal(0, Directory.EnumerateDirectories(Path.Combine(journalRoot, "j", "i")).Count());
    }
    finally
    {
        DeleteTemporaryPath(journalRoot);
        DeleteTemporaryPath(requestedSource);
        DeleteTemporaryPath(approvedSource);
    }
}

static async Task TestDetectedPathSwapAsync()
{
    var root = CreateTemporaryPath();
    try
    {
        var factory = new RootDriftBoundaryFactory(root);
        await using var journal = new OfflineJournal(
            root,
            root,
            new OfflineJournalIdentity("journal-1", "writer-1", "store-1", "project-1"),
            new TestPayloadProtector(),
            new TestReceiptVerifier(),
            factory);

        var result = await AppendTextAsync(journal, Request("swap-1", "op.write"), "payload");
        Assert.Equal(JournalMutationDisposition.Rejected, result.Disposition);
        Assert.Equal(0, Directory.EnumerateFileSystemEntries(root).Count());
    }
    finally
    {
        DeleteTemporaryPath(root);
    }
}

static async Task TestBoundaryDurabilityAsync()
{
    var root = CreateTemporaryPath();
    try
    {
        var expected = new JournalDurabilityCapabilities(
            FileContentFlush: true,
            AtomicSameVolumePromotion: true,
            ParentDirectoryFlush: true,
            Guarantee: "simulated production boundary");
        var factory = new RootDriftBoundaryFactory(root, durability: expected);
        await using var journal = new OfflineJournal(
            root,
            root,
            new OfflineJournalIdentity("journal-1", "writer-1", "store-1", "project-1"),
            new TestPayloadProtector(),
            new TestReceiptVerifier(),
            factory);

        Assert.Equal(expected, journal.Durability);
        Assert.True(journal.Durability.ParentDirectoryFlush);
    }
    finally
    {
        DeleteTemporaryPath(root);
    }
}

static async Task TestAesGcmProtectionAsync()
{
    var firstKey = SHA256.HashData(Encoding.UTF8.GetBytes("aes-gcm-journal-key-1"));
    var secondKey = SHA256.HashData(Encoding.UTF8.GetBytes("aes-gcm-journal-key-2"));
    using var provider = new TestPayloadKeyProvider("key-1", firstKey);
    try
    {
        var protector = new AesGcmJournalPayloadProtector(provider, maximumPayloadBytes: 1024);
        var context = ProtectionContext("aad-1");
        var plaintext = Encoding.UTF8.GetBytes("authenticated offline payload");
        using var protectedPayload = new MemoryStream();
        var metadata = await protector.ProtectAsync(
            new MemoryStream(plaintext, writable: false),
            protectedPayload,
            context,
            CancellationToken.None);

        Assert.Equal(AesGcmJournalPayloadProtector.AlgorithmId, metadata.Algorithm);
        Assert.Equal("key-1", metadata.KeyId);
        Assert.Equal(16, metadata.Nonce.Length);
        Assert.Equal(22, metadata.AuthenticationTag.Length);
        var protectedBytes = protectedPayload.ToArray();
        Assert.False(protectedBytes.AsSpan().SequenceEqual(plaintext));
        Assert.True(await protector.VerifyAsync(
            new MemoryStream(protectedBytes, writable: false),
            metadata,
            context,
            CancellationToken.None));

        using var restored = new MemoryStream();
        await protector.UnprotectAsync(
            new MemoryStream(protectedBytes, writable: false),
            restored,
            metadata,
            context,
            CancellationToken.None);
        Assert.SequenceEqual(plaintext, restored.ToArray());

        var wrongContext = ProtectionContext("aad-2");
        Assert.False(await protector.VerifyAsync(
            new MemoryStream(protectedBytes, writable: false),
            metadata,
            wrongContext,
            CancellationToken.None));

        var tampered = protectedBytes.ToArray();
        tampered[0] ^= 0x5a;
        Assert.False(await protector.VerifyAsync(
            new MemoryStream(tampered, writable: false),
            metadata,
            context,
            CancellationToken.None));
        using var rejectedPlaintext = new CountingWriteStream();
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => protector.UnprotectAsync(
            new MemoryStream(tampered, writable: false),
            rejectedPlaintext,
            metadata,
            context,
            CancellationToken.None).AsTask());
        Assert.Equal(0L, rejectedPlaintext.BytesWritten);

        await Assert.ThrowsAsync<InvalidDataException>(() => protector.VerifyAsync(
            new MemoryStream(protectedBytes, writable: false),
            metadata with { Nonce = "not*base64url" },
            context,
            CancellationToken.None).AsTask());

        provider.Rotate("key-2", secondKey);
        Assert.True(await protector.VerifyAsync(
            new MemoryStream(protectedBytes, writable: false),
            metadata,
            context,
            CancellationToken.None));
        provider.Remove("key-1");
        var unavailable = await Assert.ThrowsAsync<JournalPayloadProtectionUnavailableException>(() =>
            protector.VerifyAsync(
                new MemoryStream(protectedBytes, writable: false),
                metadata,
                context,
                CancellationToken.None).AsTask());
        Assert.True(unavailable.Retryable);
        provider.Add("key-1", firstKey);

        var bounded = new AesGcmJournalPayloadProtector(provider, maximumPayloadBytes: 8);
        using var untouchedDestination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => bounded.ProtectAsync(
            new MemoryStream(new byte[9], writable: false),
            untouchedDestination,
            context,
            CancellationToken.None).AsTask());
        Assert.Equal(0L, untouchedDestination.Length);

        using var emptyProtected = new MemoryStream();
        var emptyMetadata = await bounded.ProtectAsync(
            new MemoryStream([], writable: false),
            emptyProtected,
            context,
            CancellationToken.None);
        Assert.Equal(0L, emptyProtected.Length);
        Assert.True(await bounded.VerifyAsync(
            new MemoryStream([], writable: false),
            emptyMetadata,
            context,
            CancellationToken.None));
    }
    finally
    {
        CryptographicOperations.ZeroMemory(firstKey);
        CryptographicOperations.ZeroMemory(secondKey);
    }
}

static async Task TestUnavailablePayloadKeyRecoveryAsync()
{
    var root = CreateTemporaryPath();
    var key = SHA256.HashData(Encoding.UTF8.GetBytes("recovery-key-1"));
    using var provider = new TestPayloadKeyProvider("key-1", key);
    try
    {
        var protector = new AesGcmJournalPayloadProtector(provider, maximumPayloadBytes: 1024);
        await using (var journal = CreateJournal(root, protector, new TestReceiptVerifier()))
        {
            var result = await AppendTextAsync(journal, Request("key-loss-1", "op.write"), "retain me");
            Assert.Equal(JournalMutationDisposition.Added, result.Disposition);
        }

        provider.Remove("key-1");
        await using var recovering = CreateJournal(root, protector, new TestReceiptVerifier());
        var unavailable = await recovering.RecoverAsync(CancellationToken.None);
        Assert.False(unavailable.Ready);
        Assert.Equal(0, unavailable.QuarantinedEntries);
        Assert.True(unavailable.Diagnostics.Any(item =>
            item.Code == "offline_journal_payload_key_unavailable" && item.Retryable));
        Assert.Equal(1, Directory.EnumerateDirectories(Path.Combine(root, "j", "p")).Count());
        Assert.Equal(0, Directory.EnumerateDirectories(Path.Combine(root, "j", "q")).Count());

        provider.Add("key-1", key);
        var restored = await recovering.RecoverAsync(CancellationToken.None);
        Assert.True(restored.Ready);
        Assert.Equal(1, restored.PendingEntries);

        var faultingProtector = new AesGcmJournalPayloadProtector(
            new FaultingPayloadKeyProvider(),
            maximumPayloadBytes: 1024);
        await using var providerFailureRecovery = CreateJournal(
            root,
            faultingProtector,
            new TestReceiptVerifier());
        var providerFailure = await providerFailureRecovery.RecoverAsync(CancellationToken.None);
        Assert.False(providerFailure.Ready);
        Assert.Equal(0, providerFailure.QuarantinedEntries);
        Assert.True(providerFailure.Diagnostics.Any(item =>
            item.Code == "offline_journal_payload_key_provider_unavailable" && item.Retryable));
        Assert.Equal(1, Directory.EnumerateDirectories(Path.Combine(root, "j", "p")).Count());
        Assert.Equal(0, Directory.EnumerateDirectories(Path.Combine(root, "j", "q")).Count());
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
        DeleteTemporaryPath(root);
    }
}

static async Task TestDurableHighWaterAsync()
{
    var tailRoot = CreateTemporaryPath();
    var futureRoot = CreateTemporaryPath();
    var missingRoot = CreateTemporaryPath();
    try
    {
        await using (var journal = CreateJournal(tailRoot, new TestPayloadProtector(), new TestReceiptVerifier()))
        {
            Assert.Equal(JournalMutationDisposition.Added,
                (await AppendTextAsync(journal, Request("tail-1", "op.write"), "one")).Disposition);
            Assert.Equal(JournalMutationDisposition.Added,
                (await AppendTextAsync(journal, Request("tail-2", "op.write"), "two")).Disposition);
        }
        var committed = Directory.EnumerateDirectories(Path.Combine(tailRoot, "j", "p"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Directory.Delete(committed[^1], recursive: true);

        await using (var firstRecovery = CreateJournal(tailRoot, new TestPayloadProtector(), new TestReceiptVerifier()))
        {
            var recovery = await firstRecovery.RecoverAsync(CancellationToken.None);
            Assert.False(recovery.Ready);
            Assert.True(recovery.Diagnostics.Any(item => item.Code == "offline_journal_high_water_gap"));
            var append = await AppendTextAsync(firstRecovery, Request("tail-reuse", "op.write"), "must not append");
            Assert.Equal(JournalMutationDisposition.Rejected, append.Disposition);
        }
        await using (var secondRecovery = CreateJournal(tailRoot, new TestPayloadProtector(), new TestReceiptVerifier()))
        {
            var recovery = await secondRecovery.RecoverAsync(CancellationToken.None);
            Assert.False(recovery.Ready);
            Assert.True(recovery.Diagnostics.Any(item => item.Code == "offline_journal_high_water_gap"));
        }
        Assert.Equal(1, Directory.EnumerateDirectories(Path.Combine(tailRoot, "j", "p")).Count());

        await using (var journal = CreateJournal(futureRoot, new TestPayloadProtector(), new TestReceiptVerifier()))
            await AppendTextAsync(journal, Request("future-1", "op.write"), "payload");
        var latestState = Directory.EnumerateFiles(Path.Combine(futureRoot, "j", "h"), "h.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Last();
        var stateJson = await File.ReadAllTextAsync(latestState, Encoding.UTF8);
        Assert.True(stateJson.Contains("\"schemaVersion\":1", StringComparison.Ordinal));
        await File.WriteAllTextAsync(
            latestState,
            stateJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
            new UTF8Encoding(false));

        await using var future = CreateJournal(futureRoot, new TestPayloadProtector(), new TestReceiptVerifier());
        var futureRecovery = await future.RecoverAsync(CancellationToken.None);
        Assert.False(futureRecovery.Ready);
        Assert.True(futureRecovery.Diagnostics.Any(item => item.Code == "offline_journal_state_invalid"));
        Assert.Equal(
            JournalMutationDisposition.Rejected,
            (await AppendTextAsync(future, Request("future-reuse", "op.write"), "must not append")).Disposition);

        await using (var journal = CreateJournal(missingRoot, new TestPayloadProtector(), new TestReceiptVerifier()))
        {
            await AppendTextAsync(journal, Request("missing-1", "op.write"), "one");
            await AppendTextAsync(journal, Request("missing-2", "op.write"), "two");
        }
        Directory.Delete(Path.Combine(missingRoot, "j", "h"), recursive: true);
        Directory.Delete(
            Directory.EnumerateDirectories(Path.Combine(missingRoot, "j", "p")).Order(StringComparer.Ordinal).Last(),
            recursive: true);
        await using var missing = CreateJournal(missingRoot, new TestPayloadProtector(), new TestReceiptVerifier());
        var missingRecovery = await missing.RecoverAsync(CancellationToken.None);
        Assert.False(missingRecovery.Ready);
        Assert.True(missingRecovery.Diagnostics.Any(item => item.Code == "offline_journal_high_water_missing"));
        Assert.Equal(
            JournalMutationDisposition.Rejected,
            (await AppendTextAsync(missing, Request("missing-reuse", "op.write"), "must not append")).Disposition);
    }
    finally
    {
        DeleteTemporaryPath(tailRoot);
        DeleteTemporaryPath(futureRoot);
        DeleteTemporaryPath(missingRoot);
    }
}

static async Task TestPublicExportAsync()
{
    var root = CreateTemporaryPath();
    try
    {
        var protector = new TestPayloadProtector();
        var options = new OfflineJournalOptions { MaxExportEntries = 1 };
        await using var journal = CreateJournal(root, protector, new TestReceiptVerifier(), options);
        var payload = CortexCanonicalJson.Serialize(new TestPayloadV1(
            1,
            "pegasus.cortex.test-payload/1.0",
            "typed-value"));
        var append = await AppendBytesAsync(journal, Request("export-1", "op.write"), payload);
        Assert.Equal(JournalMutationDisposition.Added, append.Disposition);

        var request = ExportRequest("bundle-1", 1);
        var exported = await journal.ExportPendingAsync(request, CancellationToken.None);
        Assert.True(exported.Accepted);
        var bundle = exported.Bundle!;
        Assert.True(CortexSyncContractPolicy.HasValidHash(bundle));
        Assert.Equal("store-1", bundle.StoreId);
        Assert.Equal("project-1", bundle.ProjectId);
        Assert.Equal("pegasus.cortex.test-payload/1.0", bundle.Events.Single().PayloadContractId);
        Assert.SequenceEqual(payload, DecodeBase64Url(bundle.Events.Single().PayloadBase64Url));

        var overLimit = await journal.ExportPendingAsync(
            ExportRequest("bundle-over-limit", 2),
            CancellationToken.None);
        Assert.False(overLimit.Accepted);
        Assert.Equal("offline_journal_export_invalid", overLimit.Diagnostic!.Code);

        protector.TamperUnprotect = true;
        var tampered = await journal.ExportPendingAsync(
            ExportRequest("bundle-tampered", 1),
            CancellationToken.None);
        Assert.False(tampered.Accepted);
        Assert.Equal("offline_journal_export_failed", tampered.Diagnostic!.Code);
    }
    finally
    {
        DeleteTemporaryPath(root);
    }
}

static async Task TestTypedReceiptProjectionAsync()
{
    var root = CreateTemporaryPath();
    try
    {
        var protector = new TestPayloadProtector();
        var verifier = new TestReceiptVerifier();
        var options = new OfflineJournalOptions { MaxTypedReceipts = 2 };
        CortexOfflineBundleV1 bundle;
        await using (var journal = CreateJournal(root, protector, verifier, options))
        {
            var payload = CortexCanonicalJson.Serialize(new TestPayloadV1(
                1,
                "pegasus.cortex.test-payload/1.0",
                "receipt-value"));
            await AppendBytesAsync(journal, Request("typed-receipt-1", "op.write"), payload);
            bundle = (await journal.ExportPendingAsync(
                ExportRequest("bundle-receipt", 1),
                CancellationToken.None)).Bundle!;

            var uploaded = await journal.RecordTypedAcknowledgmentAsync(
                bundle,
                CreateTypedReceipt(bundle, "receipt-uploaded", reconciled: false),
                CancellationToken.None);
            Assert.Equal(JournalMutationDisposition.Added, uploaded.Disposition);
            Assert.True(uploaded.Uploaded);
            Assert.False(uploaded.Reconciled);
            Assert.Equal(
                JournalEntrySyncState.Uploaded,
                (await journal.ReadSyncStatusAsync(10, CancellationToken.None)).Single().State);
            Assert.Equal(0, (await journal.ReadPendingAsync(10, CancellationToken.None)).Count);
            var uploadedPlan = await journal.PlanRetentionAsync(
                new JournalRetentionPlanRequest(
                    new DateTimeOffset(2026, 7, 14, 11, 0, 1, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
                    10),
                CancellationToken.None);
            Assert.True(uploadedPlan.Accepted);
            Assert.Equal(0, uploadedPlan.Plan!.Candidates.Count);

            var reconciled = await journal.RecordTypedAcknowledgmentAsync(
                bundle,
                CreateTypedReceipt(bundle, "receipt-reconciled", reconciled: true),
                CancellationToken.None);
            Assert.Equal(JournalMutationDisposition.Added, reconciled.Disposition);
            Assert.True(reconciled.Uploaded);
            Assert.True(reconciled.Reconciled);
            Assert.Equal(
                JournalEntrySyncState.Reconciled,
                (await journal.ReadSyncStatusAsync(10, CancellationToken.None)).Single().State);
            var retention = await journal.PlanRetentionAsync(
                new JournalRetentionPlanRequest(
                    new DateTimeOffset(2026, 7, 14, 11, 0, 1, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
                    10),
                CancellationToken.None);
            Assert.True(retention.Accepted);
            Assert.Equal(1, retention.Plan!.Candidates.Count);
            Assert.Equal("receipt-reconciled", retention.Plan.Candidates.Single().ReceiptId);
            Assert.False(retention.Plan.ApplySupported);
            Assert.False(retention.Plan.SourceMutationAllowed);
            Assert.True(retention.Plan.OperatorApprovalRequired);
            Assert.Equal("operator_review_only", retention.Plan.QuarantineDisposition);
            var planPlaceholder = retention.Plan with { PlanHash = OfflineJournalContractIds.EmptySha256 };
            Assert.Equal(
                retention.Plan.PlanHash,
                CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(planPlaceholder)));
            var retentionSchema = await File.ReadAllTextAsync(Path.Combine(
                FindProjectRoot(),
                "schemas",
                "cortex-offline-journal-retention-plan.v1.schema.json"));
            Assert.True(retentionSchema.Contains("\"applySupported\": { \"const\": false }", StringComparison.Ordinal));
            Assert.True(retentionSchema.Contains("\"sourceMutationAllowed\": { \"const\": false }", StringComparison.Ordinal));

            var overQuota = await journal.RecordTypedAcknowledgmentAsync(
                bundle,
                CreateTypedReceipt(bundle, "receipt-over-quota", reconciled: true),
                CancellationToken.None);
            Assert.Equal(JournalMutationDisposition.Rejected, overQuota.Disposition);
            Assert.Equal("offline_journal_typed_receipt_count_exceeded", overQuota.Diagnostic!.Code);
            var existing = await journal.RecordTypedAcknowledgmentAsync(
                bundle,
                CreateTypedReceipt(bundle, "receipt-reconciled", reconciled: true),
                CancellationToken.None);
            Assert.Equal(JournalMutationDisposition.Existing, existing.Disposition);
            Assert.Equal(2, Directory.EnumerateDirectories(Path.Combine(root, "j", "a")).Count());
        }

        await using var reopened = CreateJournal(root, protector, verifier, options);
        var recovery = await reopened.RecoverAsync(CancellationToken.None);
        Assert.True(recovery.Ready);
        Assert.Equal(0, recovery.PendingEntries);
        Assert.Equal(
            JournalEntrySyncState.Reconciled,
            (await reopened.ReadSyncStatusAsync(10, CancellationToken.None)).Single().State);
    }
    finally
    {
        DeleteTemporaryPath(root);
    }
}

static async Task TestLegacyProofAndExportAsync()
{
    var journalRoot = CreateTemporaryPath();
    var sourceProjectRoot = CreateTemporaryPath();
    var sourceRoot = Path.Combine(sourceProjectRoot, ".cortex");
    try
    {
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "meta.json"),
            "{\"schemaVersion\":\"0.2\"}\n",
            new UTF8Encoding(false));
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "EMPTY.bin"), []);
        var large = Enumerable.Range(0, CortexLegacyFileCopyPayloadV1.MaximumChunkBytes + 17)
            .Select(index => (byte)(index % 251))
            .ToArray();
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "LARGE.bin"), large);
        var before = SnapshotFiles(sourceRoot);

        await using var planner = new LegacyCortexImportPlanner(
            journalRoot,
            journalRoot,
            new OfflineJournalIdentity("journal-1", "writer-1", "store-1", "project-1"),
            new TestPayloadProtector());
        var copied = await planner.InventoryAndCopyAsync(
            sourceRoot,
            sourceRoot,
            "migration-proof",
            new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        Assert.True(copied.Verified is not null);
        Assert.Equal(0, typeof(VerifiedLegacyCortexImport).GetConstructors(BindingFlags.Instance | BindingFlags.Public).Length);
        Assert.False(typeof(LegacyCortexImportAdapter).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Any(method => method.GetParameters().FirstOrDefault()?.ParameterType == typeof(LegacyCortexImportManifestV1)));
        Assert.Throws<ArgumentException>(() => LegacyCortexImportAdapter.ToPublicManifest(
            copied.Verified!,
            new LegacyCortexPublicManifestIdentity("different-store", "epoch-1", "project-1", "source-root-1")));

        var identity = new LegacyCortexPublicManifestIdentity(
            "store-1",
            "epoch-1",
            "project-1",
            "source-root-1");
        var publicManifest = LegacyCortexImportAdapter.ToPublicManifest(copied.Verified!, identity);
        Assert.True(publicManifest.Files.All(item => item.LifecycleState == CortexLegacyFileLifecycleIds.Validated));
        var repeated = await planner.InventoryAndCopyAsync(
            sourceRoot,
            sourceRoot,
            "migration-proof",
            new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        var mutableItems = repeated.Manifest!.Items.Select(item => item with { }).ToList();
        var snapshotResult = new LegacyCortexImportResult(
            LegacyCortexImportDisposition.Existing,
            repeated.Manifest with { Items = mutableItems },
            repeated.Verified,
            null);
        var originalSnapshotHash = snapshotResult.Manifest!.Items[0].SourceSha256;
        mutableItems[0] = mutableItems[0] with { SourceSha256 = new string('f', 64) };
        Assert.Equal(originalSnapshotHash, snapshotResult.Manifest.Items[0].SourceSha256);
        var exposedItems = (IList<LegacyCortexImportItemV1>)repeated.Verified!.Manifest.Items;
        Assert.Throws<NotSupportedException>(() => exposedItems[0] = exposedItems[0] with
        {
            SourceSha256 = new string('e', 64)
        });
        Assert.Equal(
            publicManifest.ManifestHash,
            LegacyCortexImportAdapter.ToPublicManifest(repeated.Verified, identity).ManifestHash);
        var exported = await planner.ExportPublicBundlesAsync(
            copied.Verified!,
            new LegacyCortexExportRequest(
                identity,
                "legacy-bundle",
                "tenant-1",
                "default",
                1,
                "server-revision-1",
                CortexSyncContractIds.EmptySha256,
                CortexSyncContractIds.EmptySha256,
                1,
                new DateTimeOffset(2026, 7, 14, 12, 1, 0, TimeSpan.Zero)),
            CancellationToken.None);
        Assert.True(exported.Accepted);
        Assert.True(exported.Bundles.All(CortexSyncContractPolicy.HasValidHash));

        var chunks = exported.Bundles.SelectMany(item => item.Events)
            .Select(item =>
            {
                Assert.Equal(CortexSyncContractIds.LegacyFileCopyPayload, item.PayloadContractId);
                Assert.True(item.PayloadBytes <= CortexSyncContractLimits.MaximumEventPayloadBytes);
                return CortexCanonicalJson.Deserialize<CortexLegacyFileCopyPayloadV1>(DecodeBase64Url(item.PayloadBase64Url));
            })
            .GroupBy(item => item.SourceRelativePath, StringComparer.Ordinal);
        foreach (var file in chunks)
        {
            var ordered = file.OrderBy(item => item.ChunkIndex).ToArray();
            Assert.Equal(1, ordered.Select(item => item.MappingId).Distinct(StringComparer.Ordinal).Count());
            using var reconstructed = new MemoryStream();
            foreach (var chunk in ordered)
                reconstructed.Write(DecodeBase64Url(chunk.ChunkBytesBase64Url));
            Assert.SequenceEqual(File.ReadAllBytes(Path.Combine(sourceRoot, file.Key)), reconstructed.ToArray());
        }
        var metaPath = Path.Combine(sourceRoot, "meta.json");
        var originalMeta = await File.ReadAllBytesAsync(metaPath);
        try
        {
            await File.WriteAllTextAsync(metaPath, "{\"schemaVersion\":\"0.3\"}", new UTF8Encoding(false));
            Assert.Equal(
                LegacyCortexImportDisposition.Rejected,
                (await planner.InventoryAndCopyAsync(
                    sourceRoot,
                    sourceRoot,
                    "migration-unsupported-schema",
                    new DateTimeOffset(2026, 7, 14, 12, 2, 0, TimeSpan.Zero),
                    CancellationToken.None)).Disposition);
            await File.WriteAllTextAsync(
                metaPath,
                "{\"schemaVersion\":\"0.2\",\"schemaVersion\":\"0.2\"}",
                new UTF8Encoding(false));
            Assert.Equal(
                LegacyCortexImportDisposition.Rejected,
                (await planner.InventoryAndCopyAsync(
                    sourceRoot,
                    sourceRoot,
                    "migration-duplicate-schema",
                    new DateTimeOffset(2026, 7, 14, 12, 3, 0, TimeSpan.Zero),
                    CancellationToken.None)).Disposition);
        }
        finally
        {
            await File.WriteAllBytesAsync(metaPath, originalMeta);
        }
        Assert.DictionaryEqual(before, SnapshotFiles(sourceRoot));
    }
    finally
    {
        DeleteTemporaryPath(journalRoot);
        DeleteTemporaryPath(sourceProjectRoot);
    }
}

static async Task TestLegacyPagedExportAsync()
{
    var journalRoot = CreateTemporaryPath();
    var projectRoot = CreateTemporaryPath();
    var sourceRoot = Path.Combine(projectRoot, ".cortex");
    try
    {
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "meta.json"),
            "{\"schemaVersion\":\"0.2\"}",
            new UTF8Encoding(false));
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "EMPTY.bin"), []);
        await WriteRepeatedFileAsync(
            Path.Combine(sourceRoot, "CHUNKED.bin"),
            CortexLegacyFileCopyPayloadV1.MaximumChunkBytes + 17,
            0x43);
        var before = SnapshotFiles(sourceRoot);
        await using var planner = new LegacyCortexImportPlanner(
            journalRoot,
            journalRoot,
            new OfflineJournalIdentity("journal-page", "writer-page", "store-page", "project-page"),
            new TestPayloadProtector());
        var copied = await planner.InventoryAndCopyAsync(
            sourceRoot,
            sourceRoot,
            "migration-page",
            new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        var identity = new LegacyCortexPublicManifestIdentity(
            "store-page", "epoch-page", "project-page", "source-page");
        var initialExport = new LegacyCortexExportRequest(
            identity,
            "legacy-page-bundle",
            "tenant-1",
            "default",
            7,
            null,
            CortexSyncContractIds.EmptySha256,
            CortexSyncContractIds.EmptySha256,
            1,
            new DateTimeOffset(2026, 7, 14, 12, 1, 0, TimeSpan.Zero));

        var first = await planner.ExportPublicPageAsync(
            copied.Verified!,
            new LegacyCortexPageExportRequest(initialExport, null, 2),
            CancellationToken.None);
        var repeated = await planner.ExportPublicPageAsync(
            copied.Verified!,
            new LegacyCortexPageExportRequest(initialExport, null, 2),
            CancellationToken.None);
        Assert.True(first.Accepted);
        Assert.Equal(first.Page!.PageHash, repeated.Page!.PageHash);
        Assert.Equal(2, first.Page.Bundle.Events.Count);
        Assert.False(first.Page.NextCursor.Completed);
        Assert.True(CortexSyncContractPolicy.ValidateLegacyMigrationPage(first.Page).Accepted);
        Assert.True(CortexSyncContractPolicy.HasValidHash(first.Page.SourceInventory));
        Assert.Equal(first.Page.SourceInventory.InventoryHash, first.Page.CurrentCursor.SourceInventoryHash);
        Assert.Equal(first.Page.SourceInventory.Files.Count, first.Page.CurrentCursor.SourceFileCount);
        var firstRecords = first.Page.Bundle.Events
            .Select(item => CortexCanonicalJson.Deserialize<CortexLegacyMigrationRecordV1>(
                DecodeBase64Url(item.PayloadBase64Url)))
            .ToArray();
        Assert.Equal(1, firstRecords.Select(item => item.RecordId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(1, firstRecords.Select(item => item.RevisionId).Distinct(StringComparer.Ordinal).Count());
        Assert.True(firstRecords.All(item => item.NamespaceHint == "default" &&
            item.SupersedesRevisionId is null && !item.Tombstone &&
            item.SourceManifestHash == first.Page.SourceInventory.SourceManifestHash &&
            item.SourceInventoryHash == first.Page.SourceInventory.InventoryHash));

        var resume = first.Page.NextCursor;
        var continuationExport = initialExport with
        {
            PreviousBundleHash = resume.PreviousBundleHash,
            PriorEventHash = resume.PriorEventHash,
            FirstSequence = resume.NextSequence
        };
        var second = await planner.ExportPublicPageAsync(
            copied.Verified!,
            new LegacyCortexPageExportRequest(continuationExport, resume, 2),
            CancellationToken.None);
        Assert.True(second.Accepted);
        Assert.True(second.Page!.NextCursor.Completed);
        Assert.Equal(second.Page.SourceInventory.Files.Count, second.Page.NextCursor.NextFileIndex);
        Assert.Equal(first.Page.SourceInventory.InventoryHash, second.Page.SourceInventory.InventoryHash);
        Assert.Equal(first.Page.Bundle.BundleHash, second.Page.Bundle.PreviousBundleHash);
        Assert.Equal(first.Page.Bundle.Events[^1].EventHash, second.Page.Bundle.PriorEventHash);
        var allRecords = first.Page.Bundle.Events.Concat(second.Page.Bundle.Events)
            .Select(item => CortexCanonicalJson.Deserialize<CortexLegacyMigrationRecordV1>(
                DecodeBase64Url(item.PayloadBase64Url)))
            .ToArray();
        Assert.True(allRecords.Zip(allRecords.Skip(1), (left, right) =>
            right.SourceFileIndex > left.SourceFileIndex ||
            right.SourceFileIndex == left.SourceFileIndex && right.ChunkIndex == left.ChunkIndex + 1).All(value => value));

        var tamperedCursor = new CortexLegacyMigrationCursorV1(
            resume.SchemaVersion,
            resume.ContractId,
            resume.Canonicalization,
            resume.MigrationId,
            resume.SourceManifestHash,
            resume.SourceInventoryHash,
            resume.SourceFileCount,
            resume.StoreId,
            resume.StoreEpoch,
            resume.ProjectId,
            resume.SourceRootId,
            resume.PageNumber,
            resume.NextFileIndex,
            resume.NextChunkIndex,
            resume.NextSequence,
            resume.PreviousBundleHash,
            resume.PriorEventHash,
            resume.Completed,
            resume.ContentTreatment,
            resume.Executable,
            resume.Extensions,
            new string('f', 64));
        var rejected = await planner.ExportPublicPageAsync(
            copied.Verified!,
            new LegacyCortexPageExportRequest(continuationExport, tamperedCursor, 2),
            CancellationToken.None);
        Assert.False(rejected.Accepted);
        Assert.Equal("legacy_cortex_page_cursor_invalid", rejected.Diagnostic!.Code);
        Assert.DictionaryEqual(before, SnapshotFiles(sourceRoot));
    }
    finally
    {
        DeleteTemporaryPath(journalRoot);
        DeleteTemporaryPath(projectRoot);
    }
}

static async Task TestRepositoryV2CortexIsNotLegacySourceAsync()
{
    var sourceRoot = Path.Combine(FindProjectRoot(), ".cortex");
    var journalRoot = CreateTemporaryPath();
    var before = SnapshotFiles(sourceRoot);
    try
    {
        Assert.True(before.ContainsKey("archive/.gitkeep"));
        await using var planner = new LegacyCortexImportPlanner(
            journalRoot,
            journalRoot,
            new OfflineJournalIdentity("journal-smoke", "writer-smoke", "store-smoke", "project-smoke"),
            new TestPayloadProtector());
        var result = await planner.InventoryAndCopyAsync(
            sourceRoot,
            sourceRoot,
            "repository-cortex-smoke",
            new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        Assert.Equal(LegacyCortexImportDisposition.Rejected, result.Disposition);
        Assert.Equal("legacy_cortex_import_failed", result.Diagnostic!.Code);
        Assert.Equal(typeof(InvalidDataException).FullName, result.Diagnostic.Cause);
        Assert.True(result.Manifest is null);
        Assert.True(result.Verified is null);
        Assert.DictionaryEqual(before, SnapshotFiles(sourceRoot));
    }
    finally
    {
        DeleteTemporaryPath(journalRoot);
        Assert.DictionaryEqual(before, SnapshotFiles(sourceRoot));
    }
}

static async Task TestLegacyExportAggregateBoundAsync()
{
    var journalRoot = CreateTemporaryPath();
    var projectRoot = CreateTemporaryPath();
    var sourceRoot = Path.Combine(projectRoot, ".cortex");
    try
    {
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "meta.json"),
            "{\"schemaVersion\":\"0.2\"}",
            new UTF8Encoding(false));
        await WriteRepeatedFileAsync(Path.Combine(sourceRoot, "FIRST.bin"), 9 * 1024 * 1024, 0x31);
        await WriteRepeatedFileAsync(Path.Combine(sourceRoot, "SECOND.bin"), 9 * 1024 * 1024, 0x52);
        var protector = new TestPayloadProtector();
        await using var planner = new LegacyCortexImportPlanner(
            journalRoot,
            journalRoot,
            new OfflineJournalIdentity("journal-bound", "writer-bound", "store-bound", "project-bound"),
            protector);
        var copied = await planner.InventoryAndCopyAsync(
            sourceRoot,
            sourceRoot,
            "migration-export-bound",
            new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        Assert.Equal(LegacyCortexImportDisposition.Copied, copied.Disposition);
        var unprotectCallsBeforeExport = protector.UnprotectCalls;
        var exported = await planner.ExportPublicBundlesAsync(
            copied.Verified!,
            new LegacyCortexExportRequest(
                new LegacyCortexPublicManifestIdentity(
                    "store-bound", "epoch-bound", "project-bound", "source-bound"),
                "legacy-bound",
                "tenant-1",
                "default",
                1,
                null,
                CortexSyncContractIds.EmptySha256,
                CortexSyncContractIds.EmptySha256,
                1,
                new DateTimeOffset(2026, 7, 14, 12, 1, 0, TimeSpan.Zero)),
            CancellationToken.None);
        Assert.False(exported.Accepted);
        Assert.Equal("legacy_cortex_export_limit_exceeded", exported.Diagnostic!.Code);
        Assert.Equal(unprotectCallsBeforeExport, protector.UnprotectCalls);
        Assert.Equal(9L * 1024 * 1024, new FileInfo(Path.Combine(sourceRoot, "FIRST.bin")).Length);
        Assert.Equal(9L * 1024 * 1024, new FileInfo(Path.Combine(sourceRoot, "SECOND.bin")).Length);
    }
    finally
    {
        DeleteTemporaryPath(journalRoot);
        DeleteTemporaryPath(projectRoot);
    }
}

static async Task TestLegacySourceNamespaceDriftAsync()
{
    var journalRoot = CreateTemporaryPath();
    var alternateProject = CreateTemporaryPath();
    var driftProject = CreateTemporaryPath();
    var alternateRoot = Path.Combine(alternateProject, ".CORTEX");
    var driftRoot = Path.Combine(driftProject, ".cortex");
    try
    {
        Directory.CreateDirectory(alternateRoot);
        Directory.CreateDirectory(driftRoot);
        await File.WriteAllTextAsync(
            Path.Combine(alternateRoot, "meta.json"),
            "{\"schemaVersion\":\"0.2\"}",
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(driftRoot, "meta.json"),
            "{\"schemaVersion\":\"0.2\"}",
            new UTF8Encoding(false));

        var protector = new TestPayloadProtector
        {
            AfterFirstProtect = () => File.WriteAllText(
                Path.Combine(driftRoot, "ADDED.md"),
                "appeared during capture",
                new UTF8Encoding(false))
        };
        await using var planner = new LegacyCortexImportPlanner(
            journalRoot,
            journalRoot,
            new OfflineJournalIdentity("journal-drift", "writer-drift", "store-drift", "project-drift"),
            protector);
        var alternate = await planner.InventoryAndCopyAsync(
            alternateRoot,
            alternateRoot,
            "migration-alternate-leaf",
            new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
            CancellationToken.None);
        Assert.Equal(LegacyCortexImportDisposition.Rejected, alternate.Disposition);
        Assert.Equal("legacy_cortex_source_leaf_invalid", alternate.Diagnostic!.Code);

        var drifted = await planner.InventoryAndCopyAsync(
            driftRoot,
            driftRoot,
            "migration-path-drift",
            new DateTimeOffset(2026, 7, 14, 12, 1, 0, TimeSpan.Zero),
            CancellationToken.None);
        Assert.Equal(LegacyCortexImportDisposition.Rejected, drifted.Disposition);
        Assert.True(File.Exists(Path.Combine(driftRoot, "ADDED.md")));
        Assert.Equal(0, Directory.EnumerateDirectories(Path.Combine(journalRoot, "j", "i")).Count());
    }
    finally
    {
        DeleteTemporaryPath(journalRoot);
        DeleteTemporaryPath(alternateProject);
        DeleteTemporaryPath(driftProject);
    }
}

static OfflineJournal CreateJournal(
    string root,
    IJournalPayloadProtector protector,
    IJournalReceiptVerifier verifier,
    OfflineJournalOptions? options = null,
    string? approvedRoot = null) =>
    new(
        root,
        approvedRoot ?? root,
        new OfflineJournalIdentity("journal-1", "writer-1", "store-1", "project-1"),
        protector,
        verifier,
        options);

static JournalExportRequest ExportRequest(string bundleId, int limit) =>
    new(
        bundleId,
        "tenant-1",
        "default",
        1,
        "server-revision-1",
        CortexSyncContractIds.EmptySha256,
        CortexSyncContractIds.EmptySha256,
        new DateTimeOffset(2026, 7, 14, 11, 1, 0, TimeSpan.Zero),
        limit);

static JournalAppendRequest Request(
    string idempotencyKey,
    string operation,
    long? declaredBytes = null) =>
    new(
        idempotencyKey,
        operation,
        "pegasus.cortex.test-payload/1.0",
        new DateTimeOffset(2026, 7, 14, 11, 0, 0, TimeSpan.Zero),
        "server-revision-1",
        new string('a', 64),
        declaredBytes);

static JournalPayloadProtectionContext ProtectionContext(string associatedData) =>
    new(
        "journal-1",
        "writer-1",
        "store-1",
        "project-1",
        1,
        "operation-1",
        "op.write",
        "pegasus.cortex.test-payload/1.0",
        "server-revision-1",
        new string('a', 64),
        OfflineJournalContractIds.EmptySha256,
        Encoding.UTF8.GetBytes(associatedData));

static async Task<JournalAppendResult> AppendTextAsync(
    OfflineJournal journal,
    JournalAppendRequest request,
    string text)
{
    var bytes = Encoding.UTF8.GetBytes(text);
    return await journal.AppendAsync(
        request with { DeclaredPayloadBytes = bytes.Length },
        new MemoryStream(bytes, writable: false),
        leaveOpen: false,
        CancellationToken.None);
}

static async Task<JournalAppendResult> AppendBytesAsync(
    OfflineJournal journal,
    JournalAppendRequest request,
    byte[] bytes) =>
    await journal.AppendAsync(
        request with { DeclaredPayloadBytes = bytes.Length },
        new MemoryStream(bytes, writable: false),
        leaveOpen: false,
        CancellationToken.None);

static CortexSyncReceiptV1 CreateTypedReceipt(
    CortexOfflineBundleV1 bundle,
    string receiptId,
    bool reconciled)
{
    var proof = Encoding.UTF8.GetBytes($"proof:{receiptId}");
    var proofBase64 = EncodeBase64Url(proof);
    var proofHash = CortexCanonicalJson.Sha256(proof);
    var receivedAt = new DateTimeOffset(2026, 7, 14, 11, 2, 0, TimeSpan.Zero);
    var outcomes = bundle.Events.Select(item => new CortexSyncReceiptOutcomeV1(
        item.Sequence,
        item.EventId,
        item.EventHash,
        CortexSyncDispositionIds.Applied,
        true,
        $"entity-{item.Sequence}",
        "server-revision-2",
        null)).ToArray();
    var reconciliation = reconciled
        ? new CortexSyncReceiptReconciliationV1(
            "reconciled",
            outcomes.Length,
            outcomes.Length,
            receivedAt.AddSeconds(2),
            bundle.BundleHash)
        : new CortexSyncReceiptReconciliationV1("pending", outcomes.Length, 0, null, null);
    var placeholderAuthentication = new CortexSyncReceiptAuthenticationV1(
        "pegasus.cortex.receipt-proof-test/1.0",
        "key-1",
        "test-signature",
        CortexSyncContractIds.EmptySha256,
        proofBase64,
        proof.Length,
        proofHash);
    CortexSyncReceiptV1 Create(CortexSyncReceiptAuthenticationV1 authentication, string receiptHash) =>
        new(
            CortexSyncContractIds.SchemaVersion,
            CortexSyncContractIds.SyncReceipt,
            CortexSyncContractIds.Canonicalization,
            receiptId,
            "server-1",
            bundle.StoreId,
            bundle.StoreEpoch,
            bundle.ProjectId,
            bundle.BundleId,
            bundle.BundleHash,
            receivedAt,
            authentication,
            new CortexSyncReceiptDurabilityV1("commit-1", "server-revision-2", receivedAt.AddSeconds(1)),
            outcomes,
            reconciliation,
            [],
            receiptHash);
    var placeholder = Create(placeholderAuthentication, CortexSyncContractIds.EmptySha256);
    var semanticHash = CortexSyncContractPolicy.ComputeReceiptSemanticHash(placeholder);
    return Create(
        new CortexSyncReceiptAuthenticationV1(
            placeholderAuthentication.ProofContractId,
            placeholderAuthentication.KeyId,
            placeholderAuthentication.AlgorithmId,
            semanticHash,
            proofBase64,
            proof.Length,
            proofHash),
        semanticHash);
}

static string EncodeBase64Url(ReadOnlySpan<byte> bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static byte[] DecodeBase64Url(string value)
{
    var normalized = value.Replace('-', '+').Replace('_', '/');
    normalized += new string('=', (4 - normalized.Length % 4) % 4);
    return Convert.FromBase64String(normalized);
}

static string FindProjectRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "PegasusCortex.slnx")))
            return current.FullName;
        current = current.Parent;
    }
    throw new DirectoryNotFoundException("Could not locate the Pegasus Cortex project root.");
}

static JournalAcknowledgmentRequest ReceiptRequest(
    JournalEntryMetadataV1 entry,
    string receiptId,
    int evidenceBytes) =>
    new(
        receiptId,
        "nexus-authority",
        entry.EntryId,
        entry.Sequence,
        entry.EntrySha256,
        "server-revision-2",
        "test-hmac-sha256",
        "server-key-1",
        new DateTimeOffset(2026, 7, 14, 11, 1, 0, TimeSpan.Zero),
        "accepted",
        evidenceBytes);

static byte[] ReceiptEvidence(JournalEntryMetadataV1 entry, string receiptId) =>
    Encoding.UTF8.GetBytes($"{entry.EntrySha256}:{receiptId}");

static Dictionary<string, (long Bytes, string Sha256)> SnapshotFiles(string root) =>
    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .ToDictionary(
            path => Path.GetRelativePath(root, path).Replace('\\', '/'),
            path =>
            {
                var bytes = File.ReadAllBytes(path);
                return ((long)bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            },
            StringComparer.Ordinal);

static async Task WriteRepeatedFileAsync(string path, int bytes, byte value)
{
    await using var output = new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    var buffer = Enumerable.Repeat(value, 64 * 1024).ToArray();
    var remaining = bytes;
    while (remaining > 0)
    {
        var count = Math.Min(buffer.Length, remaining);
        await output.WriteAsync(buffer.AsMemory(0, count));
        remaining -= count;
    }
}

static string CreateTemporaryPath()
{
    var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"pegasus-cortex-journal-{Guid.NewGuid():N}"));
    Directory.CreateDirectory(path);
    return Path.TrimEndingDirectorySeparator(path);
}

static string TogglePathCase(string path)
{
    var characters = path.ToCharArray();
    for (var index = 0; index < characters.Length; index++)
    {
        if (!char.IsAsciiLetter(characters[index]))
            continue;
        characters[index] = char.IsUpper(characters[index])
            ? char.ToLowerInvariant(characters[index])
            : char.ToUpperInvariant(characters[index]);
        var changed = new string(characters);
        if (!string.Equals(changed, path, StringComparison.Ordinal))
            return changed;
    }
    throw new InvalidOperationException("The temporary path contains no case-variant character.");
}

static void DeleteTemporaryPath(string path)
{
    if (Directory.Exists(path))
        Directory.Delete(path, recursive: true);
}

sealed class TestPayloadProtector : IJournalPayloadProtector
{
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("offline-journal-test-key"));
    public bool TamperUnprotect { get; set; }
    public Action? AfterFirstProtect { get; init; }
    public int UnprotectCalls { get; private set; }
    private int _protectCalls;

    public async ValueTask<JournalPayloadProtectionMetadata> ProtectAsync(
        Stream plaintext,
        Stream protectedDestination,
        JournalPayloadProtectionContext context,
        CancellationToken cancellationToken)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Key);
        hmac.AppendData(context.AssociatedData.Span);
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            long offset = 0;
            while (true)
            {
                var read = await plaintext.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                    break;
                for (var index = 0; index < read; index++)
                    buffer[index] ^= Key[(int)((offset + index) % Key.Length)];
                await protectedDestination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hmac.AppendData(buffer, 0, read);
                offset += read;
            }
            var metadata = new JournalPayloadProtectionMetadata(
                "test-xor-hmac-sha256",
                "test-key-1",
                "test-nonce-1",
                Convert.ToHexString(hmac.GetHashAndReset()).ToLowerInvariant());
            if (Interlocked.Increment(ref _protectCalls) == 1)
                AfterFirstProtect?.Invoke();
            return metadata;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask<bool> VerifyAsync(
        Stream protectedPayload,
        JournalPayloadProtectionMetadata metadata,
        JournalPayloadProtectionContext context,
        CancellationToken cancellationToken)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Key);
        hmac.AppendData(context.AssociatedData.Span);
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (true)
            {
                var read = await protectedPayload.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                    break;
                hmac.AppendData(buffer, 0, read);
            }
            var expected = Convert.ToHexString(hmac.GetHashAndReset()).ToLowerInvariant();
            return string.Equals(expected, metadata.AuthenticationTag, StringComparison.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask UnprotectAsync(
        Stream protectedPayload,
        Stream plaintextDestination,
        JournalPayloadProtectionMetadata metadata,
        JournalPayloadProtectionContext context,
        CancellationToken cancellationToken)
    {
        UnprotectCalls++;
        using var protectedCopy = new MemoryStream();
        await protectedPayload.CopyToAsync(protectedCopy, cancellationToken);
        var bytes = protectedCopy.ToArray();
        try
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Key);
            hmac.AppendData(context.AssociatedData.Span);
            hmac.AppendData(bytes);
            var expected = Convert.ToHexString(hmac.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(expected, metadata.AuthenticationTag, StringComparison.Ordinal))
                throw new CryptographicException("The test payload authentication tag is invalid.");
            for (var index = 0; index < bytes.Length; index++)
                bytes[index] ^= Key[index % Key.Length];
            if (TamperUnprotect && bytes.Length > 0)
                bytes[0] ^= 0x5a;
            await plaintextDestination.WriteAsync(bytes, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

sealed record TestPayloadV1(
    int SchemaVersion,
    string ContractId,
    string Value);

sealed class TestPayloadKeyProvider : IJournalPayloadKeyProvider, IDisposable
{
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private string _currentKeyId;

    public TestPayloadKeyProvider(string currentKeyId, ReadOnlySpan<byte> key)
    {
        _currentKeyId = currentKeyId;
        Add(currentKeyId, key);
    }

    public ValueTask<JournalPayloadKeyLease> AcquireCurrentKeyAsync(
        CancellationToken cancellationToken) =>
        AcquireKeyAsync(_currentKeyId, cancellationToken);

    public ValueTask<JournalPayloadKeyLease> AcquireKeyAsync(
        string keyId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_keys.TryGetValue(keyId, out var key))
        {
            throw new JournalPayloadProtectionUnavailableException(
                "offline_journal_payload_key_unavailable",
                "The requested test payload key is not available.",
                keyId,
                retryable: true);
        }
        return ValueTask.FromResult(new JournalPayloadKeyLease(keyId, key));
    }

    public void Add(string keyId, ReadOnlySpan<byte> key)
    {
        if (_keys.Remove(keyId, out var replaced))
            CryptographicOperations.ZeroMemory(replaced);
        _keys.Add(keyId, key.ToArray());
    }

    public void Rotate(string keyId, ReadOnlySpan<byte> key)
    {
        Add(keyId, key);
        _currentKeyId = keyId;
    }

    public void Remove(string keyId)
    {
        if (_keys.Remove(keyId, out var removed))
            CryptographicOperations.ZeroMemory(removed);
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values)
            CryptographicOperations.ZeroMemory(key);
        _keys.Clear();
    }
}

sealed class FaultingPayloadKeyProvider : IJournalPayloadKeyProvider
{
    public ValueTask<JournalPayloadKeyLease> AcquireCurrentKeyAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new IOException("Simulated host credential source failure.");
    }

    public ValueTask<JournalPayloadKeyLease> AcquireKeyAsync(
        string keyId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new IOException("Simulated host credential source failure.");
    }
}

sealed class BlockingPayloadProtector : IJournalPayloadProtector
{
    public async ValueTask<JournalPayloadProtectionMetadata> ProtectAsync(
        Stream plaintext,
        Stream protectedDestination,
        JournalPayloadProtectionContext context,
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new UnreachableException();
    }

    public ValueTask<bool> VerifyAsync(
        Stream protectedPayload,
        JournalPayloadProtectionMetadata metadata,
        JournalPayloadProtectionContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);

    public ValueTask UnprotectAsync(
        Stream protectedPayload,
        Stream plaintextDestination,
        JournalPayloadProtectionMetadata metadata,
        JournalPayloadProtectionContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new UnreachableException());
}

sealed class TestReceiptVerifier : IJournalReceiptVerifier
{
    public bool Accept { get; set; } = true;

    public async ValueTask<bool> VerifyAsync(
        Stream serverEvidence,
        JournalAcknowledgmentRequest acknowledgment,
        JournalEntryMetadataV1 pendingEntry,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await serverEvidence.CopyToAsync(memory, cancellationToken);
        var expected = Encoding.UTF8.GetBytes($"{pendingEntry.EntrySha256}:{acknowledgment.ReceiptId}");
        return Accept && memory.ToArray().AsSpan().SequenceEqual(expected);
    }

    public ValueTask<bool> VerifyAsync(
        CortexSyncReceiptV1 receipt,
        CortexOfflineBundleV1 bundle,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Accept);
}

sealed class RootDriftBoundaryFactory(
    string returnedRoot,
    string authorizedRootIdentity = "test-root-1",
    string openedRootIdentity = "test-root-1",
    JournalDurabilityCapabilities? durability = null) : IJournalFileSystemBoundaryFactory
{
    public JournalFileSystemBoundarySafety Safety => JournalFileSystemBoundarySafety.HandleRelativeNoFollow;
    public RootDriftBoundary? LastBoundary { get; private set; }

    public IJournalFileSystemBoundary OpenJournalRoot(string requestedRoot, string approvedRoot)
    {
        LastBoundary = new RootDriftBoundary(
            returnedRoot,
            authorizedRootIdentity,
            openedRootIdentity,
            durability ?? new JournalDurabilityCapabilities(
                FileContentFlush: true,
                AtomicSameVolumePromotion: true,
                ParentDirectoryFlush: false,
                Guarantee: "simulated handle-relative boundary"));
        return LastBoundary;
    }

    public ILegacySourceFileSystemBoundary OpenLegacySourceRoot(string requestedRoot, string approvedRoot) =>
        throw new UnreachableException();
}

sealed class RootDriftBoundary(
    string rootPath,
    string authorizedRootIdentity,
    string openedRootIdentity,
    JournalDurabilityCapabilities durability) : IJournalFileSystemBoundary
{
    public JournalFileSystemBoundarySafety Safety => JournalFileSystemBoundarySafety.HandleRelativeNoFollow;
    public JournalDurabilityCapabilities Durability => durability;
    public string RootPath => rootPath;
    public string AuthorizedRootIdentity => authorizedRootIdentity;
    public string OpenedRootIdentity => openedRootIdentity;
    public bool Disposed { get; private set; }

    public void EnsureDirectory(string relativePath) => throw DetectedSwap();
    public void CreateDirectory(string relativePath) => throw DetectedSwap();
    public bool DirectoryExists(string relativePath) => throw DetectedSwap();
    public bool FileExists(string relativePath) => throw DetectedSwap();
    public JournalFileSystemEntryKind GetEntryKind(string relativePath) => throw DetectedSwap();
    public FileStream AcquireExclusiveLock(string relativePath) => throw DetectedSwap();
    public FileStream CreateNewFile(string relativePath, int bufferSize, bool asynchronous) => throw DetectedSwap();
    public FileStream OpenReadFile(string relativePath, int bufferSize, bool asynchronous, bool sequentialScan) =>
        throw DetectedSwap();
    public IReadOnlyList<JournalFileSystemEntry> EnumerateDirectory(string relativePath, int maximumEntries) =>
        throw DetectedSwap();
    public void MoveDirectory(string sourceRelativePath, string destinationRelativePath) => throw DetectedSwap();
    public void DeleteFile(string relativePath) => throw DetectedSwap();
    public void DeleteEmptyDirectory(string relativePath) => throw DetectedSwap();
    public void Dispose() => Disposed = true;

    private static IOException DetectedSwap() => new("The host boundary detected a simulated path-component swap.");
}

sealed class CountingWriteStream : MemoryStream
{
    public long BytesWritten { get; private set; }

    public override void Write(byte[] buffer, int offset, int count)
    {
        BytesWritten += count;
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        BytesWritten += buffer.Length;
        base.Write(buffer);
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        BytesWritten += buffer.Length;
        await base.WriteAsync(buffer, cancellationToken);
    }
}

sealed class CountingReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
{
    public int ReadCount { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ReadCount++;
        return base.Read(buffer, offset, count);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return base.ReadAsync(buffer, cancellationToken);
    }
}

static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
            throw new InvalidOperationException(message ?? "Expected true.");
    }

    public static void False(bool condition, string? message = null) => True(!condition, message ?? "Expected false.");

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    public static void SequenceEqual(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new InvalidOperationException("Byte sequences differ.");
    }

    public static void DictionaryEqual(
        IReadOnlyDictionary<string, (long Bytes, string Sha256)> expected,
        IReadOnlyDictionary<string, (long Bytes, string Sha256)> actual)
    {
        Equal(expected.Count, actual.Count);
        foreach (var item in expected)
        {
            True(actual.TryGetValue(item.Key, out var value), $"Missing '{item.Key}'.");
            Equal(item.Value, value);
        }
    }

    public static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    public static async Task ThrowsCanceledAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("Expected cancellation.");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
