using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Pegasus.AgentOps.Engine;

namespace Pegasus.AgentOps.Zip.Tests;

internal static class Program
{
    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("traversal admission preserves clean destination", TraversalAdmissionPreservesDestination),
            ("UTF8 and final plus staging budgets precede destination mutation", ExtractionPathBudgets),
            ("case-insensitive collision rejected", CaseInsensitiveCollisionRejected),
            ("symlink external attributes rejected", SymlinkAttributesRejected),
            ("expansion ratio bounded", ExpansionRatioBounded),
            ("expanded byte count bounded", ExpandedBytesBounded),
            ("metadata admission completes before content streams", MetadataAdmissionPrecedesContentStreams),
            ("archive inside destination rejected", SourceInsideDestinationRejected),
            ("destination root junction rejected", DestinationRootJunctionRejected),
            ("destination ancestor junction rejected", DestinationAncestorJunctionRejected),
            ("ADS and reserved names rejected", AdsAndReservedNamesRejected),
            ("fail replace and skip use exact destination", ExtractionPoliciesUseExactDestination),
            ("valid create inspect and clean extract", ValidRoundTrip),
            ("replacement preserves prior archive on publish fault", ReplacementPreservesPriorArchive),
            ("archive output is bounded before publication", ArchiveOutputBoundPreservesPriorArchive),
            ("stored CRC mismatch rejected before clean", StoredCrcMismatchPreservesDestination),
            ("direct ADS operation paths rejected", DirectAdsPathsRejected),
            ("clean failure stops before extraction", CleanFailureStopsBeforeExtraction),
            ("clean extraction fault reports partial counters", CleanExtractionFaultReportsCounters),
            ("created directories propagate partial mutation", CreatedDirectoriesPropagatePartialMutation),
            ("clean removes reparse leaf without traversal", CleanReparseLeafDoesNotTraverse),
            ("clean directory identity blocks junction swap", CleanDirectoryIdentityBlocksSwap),
            ("extract publication parent identity blocks junction swap", ExtractPublicationIdentityBlocksSwap),
            ("token overloads preserve physical constructor and pre-entry cancellation", PreEntryCancellation),
            ("admission content cancellation releases original archive", AdmissionCancellation),
            ("source walk and creation write/verification cancellation preserve prior archive", CreationCancellation),
            ("nested clean cancellation retains exact effect counters", CleanCancellationCounters),
            ("extraction staging cancellation preserves existing file", ExtractionStagingCancellation),
            ("publication success wins late cancellation and prior files stay counted", PublicationCancellationBoundaries),
            ("canceled temporary cleanup failure remains explicitly partial", TemporaryCleanupFailureIsPartial),
            ("temporary cleanup failure preserves original non-cancellation cause", TemporaryCleanupPreservesPrimaryError)
        };

        var failed = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"RESULT total={tests.Length} passed={tests.Length - failed} failed={failed}");
        return failed == 0 ? 0 : 1;
    }

    private static void ExtractionPathBudgets()
    {
        using var scope = new TestScope();
        var destination = scope.CreateDirectory("budget-output");
        var marker = Path.Combine(destination, "keep.txt");
        File.WriteAllText(marker, "keep", Encoding.UTF8);
        var temporaryOnlyName = new string('t', 230 - destination.Length - 1);
        var names = new[]
        {
            new string('雪', 86) + "/x.txt", // 258 UTF8 bytes despite only 86 UTF16 characters
            new string('a', 100) + "/" + new string('b', 100) + "/x.txt",
            temporaryOnlyName // final target fits; its actual sibling temp naming shape does not
        };
        for (var index = 0; index < names.Length; index++)
        {
            var archive = scope.PathOf("budget-" + index + ".zip");
            WriteRawArchive(archive, new RawEntry(names[index], Bytes("x")));
            ExpectCode("archive_name", () => new ZipOperations().Extract(archive, destination, "clean"));
            Require(File.ReadAllText(marker) == "keep", "path rejection changed the clean destination");
            Require(Directory.GetFileSystemEntries(destination).Length == 1,
                "path rejection created an entry or staging file");
        }

        var valid = scope.PathOf("utf8-valid.zip");
        var folder = new string('雪', 85); // exactly 255 UTF8 bytes
        WriteRawArchive(valid, new RawEntry(folder + "/x.txt", Bytes("ok")));
        var output = scope.PathOf("utf8-valid");
        new ZipOperations().Extract(valid, output);
        Require(File.ReadAllText(Path.Combine(output, folder, "x.txt")) == "ok",
            "valid UTF8 boundary did not round-trip");
    }

    private static void TraversalAdmissionPreservesDestination()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("bad.zip");
        WriteRawArchive(archive, new RawEntry("../escape.txt", Bytes("escape")));
        var destination = scope.CreateDirectory("destination");
        var marker = Path.Combine(destination, "marker.txt");
        File.WriteAllText(marker, "keep", Encoding.UTF8);

        ExpectCode("archive_traversal", () => new ZipOperations().Extract(archive, destination, "clean"));
        Require(File.Exists(marker), "clean mutated the destination before archive admission");
        Require(!File.Exists(scope.PathOf("escape.txt")), "traversal created an outside file");
    }

    private static void CaseInsensitiveCollisionRejected()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("collision.zip");
        WriteRawArchive(archive,
            new RawEntry("Data.txt", Bytes("one")),
            new RawEntry("data.TXT", Bytes("two")));
        ExpectCode("archive_collision", () => new ZipOperations().Inspect(archive));
    }

    private static void SymlinkAttributesRejected()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("symlink.zip");
        const int unixSymlinkAttributes = unchecked((int)((0xA000u | 0x1FFu) << 16));
        WriteRawArchive(archive, new RawEntry("link", Bytes("outside"), unixSymlinkAttributes));
        ExpectCode("archive_special", () => new ZipOperations().Inspect(archive));

        var device = scope.PathOf("device.zip");
        WriteRawArchive(device, new RawEntry("device", Bytes("outside"), (int)FileAttributes.Device));
        ExpectCode("archive_special", () => new ZipOperations().Inspect(device));
    }

    private static void ExpansionRatioBounded()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("ratio.zip");
        WriteRawArchive(archive, new RawEntry("zeros.bin", new byte[256 * 1024], CompressionLevel.SmallestSize));
        var limits = new ZipLimits(MaxArchiveBytes: 2 * 1024 * 1024, MaxEntries: 10,
            MaxEntryBytes: 512 * 1024, MaxExpandedBytes: 512 * 1024, MaxExpansionRatio: 2);
        ExpectCode("archive_limit", () => new ZipOperations().Inspect(archive, limits));
    }

    private static void ExpandedBytesBounded()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("expanded.zip");
        WriteRawArchive(archive, new RawEntry("data.bin", RandomBytes(4096), CompressionLevel.NoCompression));
        var limits = new ZipLimits(MaxArchiveBytes: 32 * 1024, MaxEntries: 10,
            MaxEntryBytes: 8 * 1024, MaxExpandedBytes: 1024, MaxExpansionRatio: 100);
        ExpectCode("archive_limit", () => new ZipOperations().Inspect(archive, limits));
    }

    private static void MetadataAdmissionPrecedesContentStreams()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("ratio-before-content.zip");
        WriteRawArchive(archive, new RawEntry("zeros.bin", new byte[256 * 1024], CompressionLevel.SmallestSize));
        var limits = new ZipLimits(MaxArchiveBytes: 2 * 1024 * 1024, MaxEntries: 10,
            MaxEntryBytes: 512 * 1024, MaxExpandedBytes: 512 * 1024, MaxExpansionRatio: 2);
        var fault = new CountingFault(ZipFaultPoint.BeforeAdmissionContent);

        ExpectCode("archive_limit", () => new ZipOperations(fault).Inspect(archive, limits));
        Require(fault.Count == 0, "entry content was opened before metadata admission rejected the archive");
    }

    private static void SourceInsideDestinationRejected()
    {
        using var scope = new TestScope();
        var destination = scope.CreateDirectory("destination");
        var marker = Path.Combine(destination, "marker.txt");
        File.WriteAllText(marker, "keep", Encoding.UTF8);
        var archive = Path.Combine(destination, "payload.zip");
        WriteRawArchive(archive, new RawEntry("payload.txt", Bytes("new")));

        ExpectCode("precondition", () => new ZipOperations().Extract(archive, destination, "clean"));
        Require(File.Exists(marker), "source/destination relationship check occurred after cleaning");
        Require(File.Exists(archive), "clean removed its own source archive");
    }

    private static void DestinationRootJunctionRejected()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("payload.zip");
        WriteRawArchive(archive, new RawEntry("payload.txt", Bytes("new")));
        var external = scope.CreateDirectory("external");
        var marker = Path.Combine(external, "marker.txt");
        File.WriteAllText(marker, "outside", Encoding.UTF8);
        var junction = scope.PathOf("destination-link");
        CreateJunction(junction, external);

        ExpectCode("reparse_boundary", () => new ZipOperations().Extract(archive, junction, "replace"));
        Require(File.ReadAllText(marker, Encoding.UTF8) == "outside", "root junction target was modified");
    }

    private static void DestinationAncestorJunctionRejected()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("payload.zip");
        WriteRawArchive(archive, new RawEntry("payload.txt", Bytes("new")));
        var holder = scope.CreateDirectory("holder");
        var external = scope.CreateDirectory("external");
        var marker = Path.Combine(external, "marker.txt");
        File.WriteAllText(marker, "outside", Encoding.UTF8);
        var junction = Path.Combine(holder, "redirect");
        CreateJunction(junction, external);

        ExpectCode("reparse_boundary", () =>
            new ZipOperations().Extract(archive, Path.Combine(junction, "child"), "fail"));
        Require(File.ReadAllText(marker, Encoding.UTF8) == "outside", "ancestor junction target was modified");
        Require(!Directory.Exists(Path.Combine(external, "child")), "directory was created through ancestor junction");
    }

    private static void AdsAndReservedNamesRejected()
    {
        using var scope = new TestScope();
        var ads = scope.PathOf("ads.zip");
        WriteRawArchive(ads, new RawEntry("safe.txt:payload", Bytes("bad")));
        ExpectCode("archive_name", () => new ZipOperations().Inspect(ads));

        var reserved = scope.PathOf("reserved.zip");
        WriteRawArchive(reserved, new RawEntry("folder/CON.txt", Bytes("bad")));
        ExpectCode("archive_name", () => new ZipOperations().Inspect(reserved));

        var superscriptCom = scope.PathOf("superscript-com.zip");
        WriteRawArchive(superscriptCom, new RawEntry("COM\u00B9.txt", Bytes("bad")));
        ExpectCode("archive_name", () => new ZipOperations().Inspect(superscriptCom));

        var superscriptLpt = scope.PathOf("superscript-lpt.zip");
        WriteRawArchive(superscriptLpt, new RawEntry("folder/LPT\u00B2.dat", Bytes("bad")));
        ExpectCode("archive_name", () => new ZipOperations().Inspect(superscriptLpt));

        var normalizedReserved = scope.PathOf("normalized-reserved.zip");
        WriteRawArchive(normalizedReserved, new RawEntry("CON .txt", Bytes("bad")));
        ExpectCode("archive_name", () => new ZipOperations().Inspect(normalizedReserved));
    }

    private static void ValidRoundTrip()
    {
        using var scope = new TestScope();
        var source = scope.CreateDirectory("source");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        var nested = Directory.CreateDirectory(Path.Combine(source, "sp ace's [box] & Ω"));
        var expectedBytes = RandomBytes(32 * 1024);
        File.WriteAllBytes(Path.Combine(nested.FullName, "payload.bin"), expectedBytes);
        File.WriteAllText(Path.Combine(source, "plain.txt"), "hello", new UTF8Encoding(false));

        var archive = scope.PathOf("roundtrip.zip");
        var operations = new ZipOperations();
        var created = operations.Create(source, archive);
        Require(created.Copied == 2 && File.Exists(archive), "create receipt or output is incorrect");
        var inspection = operations.Inspect(archive);
        Require(inspection.Entries.Any(x => x.Name == "empty/" && x.Directory), "empty directory was not archived");
        Require(inspection.Entries.Any(x => x.Name.EndsWith("payload.bin", StringComparison.Ordinal)), "payload entry is absent");

        var destination = scope.CreateDirectory("destination");
        File.WriteAllText(Path.Combine(destination, "old-marker.txt"), "remove", Encoding.UTF8);
        var extracted = operations.Extract(archive, destination, "clean");
        Require(extracted.Deleted == 1 && extracted.Copied == 2 && !extracted.Partial, "clean extraction receipt is incorrect");
        Require(!File.Exists(Path.Combine(destination, "old-marker.txt")), "clean did not remove prior content");
        Require(File.ReadAllBytes(Path.Combine(destination, "sp ace's [box] & Ω", "payload.bin"))
            .SequenceEqual(expectedBytes), "round-tripped bytes differ");
        Require(Directory.Exists(Path.Combine(destination, "empty")), "empty directory was not extracted");
    }

    private static void ExtractionPoliciesUseExactDestination()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("policies.zip");
        WriteRawArchive(archive,
            new RawEntry("existing.txt", Bytes("new-existing")),
            new RawEntry("new.txt", Bytes("new-file")));
        var operations = new ZipOperations();

        var skipDestination = scope.CreateDirectory("skip-target");
        File.WriteAllText(Path.Combine(skipDestination, "existing.txt"), "old", Encoding.UTF8);
        File.WriteAllText(Path.Combine(skipDestination, "unrelated.txt"), "keep", Encoding.UTF8);
        ExpectCode("collision", () => operations.Extract(archive, skipDestination, "fail"));
        var skipped = operations.Extract(archive, skipDestination, "skip");
        Require(skipped.Copied == 1, "skip copied an existing collision");
        Require(File.ReadAllText(Path.Combine(skipDestination, "existing.txt"), Encoding.UTF8) == "old",
            "skip overwrote an existing file");
        Require(File.Exists(Path.Combine(skipDestination, "new.txt")), "skip omitted a missing file");
        Require(File.Exists(Path.Combine(skipDestination, "unrelated.txt")), "skip removed unrelated content");

        var replaceDestination = scope.CreateDirectory("replace-target");
        File.WriteAllText(Path.Combine(replaceDestination, "existing.txt"), "old", Encoding.UTF8);
        File.WriteAllText(Path.Combine(replaceDestination, "unrelated.txt"), "keep", Encoding.UTF8);
        var replaced = operations.Extract(archive, replaceDestination, "replace");
        Require(replaced.Copied == 2, "replace did not publish both archive files");
        Require(File.ReadAllText(Path.Combine(replaceDestination, "existing.txt"), Encoding.UTF8) == "new-existing",
            "replace did not overwrite the collision");
        Require(File.Exists(Path.Combine(replaceDestination, "unrelated.txt")), "replace removed unrelated content");
        Require(!Directory.Exists(Path.Combine(replaceDestination, "policies")),
            "extraction guessed and appended the archive basename");
    }

    private static void ReplacementPreservesPriorArchive()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("existing.zip");
        WriteRawArchive(archive, new RawEntry("old.txt", Bytes("old")));
        var before = SHA256.HashData(File.ReadAllBytes(archive));
        var source = scope.CreateDirectory("source");
        File.WriteAllText(Path.Combine(source, "new.txt"), "new", Encoding.UTF8);
        var operations = new ZipOperations(new ThrowingFault(ZipFaultPoint.BeforePublish));

        ExpectCode("publish_failed", () => operations.Create(source, archive, "replace"));
        var after = SHA256.HashData(File.ReadAllBytes(archive));
        Require(before.SequenceEqual(after), "old archive changed before replacement was publishable");
        Require(!Directory.EnumerateFiles(scope.Root, "*.pao.tmp", SearchOption.TopDirectoryOnly).Any(),
            "failed replacement left its sibling temp behind");
    }

    private static void ArchiveOutputBoundPreservesPriorArchive()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("existing.zip");
        WriteRawArchive(archive, new RawEntry("old.txt", Bytes("old")));
        var before = SHA256.HashData(File.ReadAllBytes(archive));
        var source = scope.CreateDirectory("source");
        File.WriteAllBytes(Path.Combine(source, "random.bin"), RandomBytes(64 * 1024));
        var limits = new ZipLimits(MaxArchiveBytes: 512, MaxEntries: 10,
            MaxEntryBytes: 128 * 1024, MaxExpandedBytes: 128 * 1024, MaxExpansionRatio: 200);

        ExpectCode("archive_limit", () => new ZipOperations().Create(source, archive, "replace", limits));
        Require(SHA256.HashData(File.ReadAllBytes(archive)).SequenceEqual(before),
            "bounded writer changed the prior archive before verification");
        Require(!Directory.EnumerateFiles(scope.Root, "*.pao.tmp", SearchOption.TopDirectoryOnly).Any(),
            "bounded writer left a sibling temporary archive");
    }

    private static void StoredCrcMismatchPreservesDestination()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("crc-mismatch.zip");
        WriteRawArchive(archive, new RawEntry("payload.txt", Bytes("payload")));
        CorruptFirstCentralDirectoryCrc(archive);
        var destination = scope.CreateDirectory("destination");
        var marker = Path.Combine(destination, "marker.txt");
        File.WriteAllText(marker, "keep", Encoding.UTF8);

        ExpectCode("archive_invalid", () => new ZipOperations().Extract(archive, destination, "clean"));
        Require(File.ReadAllText(marker, Encoding.UTF8) == "keep",
            "CRC rejection occurred after clean mutated the destination");
        Require(!File.Exists(Path.Combine(destination, "payload.txt")),
            "CRC-mismatched content was extracted");
    }

    private static void DirectAdsPathsRejected()
    {
        using var scope = new TestScope();
        var source = scope.CreateDirectory("source");
        File.WriteAllText(Path.Combine(source, "payload.txt"), "payload", Encoding.UTF8);
        var archive = scope.PathOf("payload.zip");
        WriteRawArchive(archive, new RawEntry("payload.txt", Bytes("payload")));
        var destination = scope.CreateDirectory("destination");
        var marker = Path.Combine(destination, "marker.txt");
        File.WriteAllText(marker, "keep", Encoding.UTF8);

        ExpectCode("precondition", () => new ZipOperations().Inspect(archive + ":content"));
        ExpectCode("precondition", () => new ZipOperations().Create(source, scope.PathOf("out.zip") + ":content"));
        ExpectCode("precondition", () => new ZipOperations().Extract(archive, destination + ":content", "clean"));
        Require(File.ReadAllText(marker, Encoding.UTF8) == "keep", "ADS destination rejection mutated the base directory");
        Require(!Directory.EnumerateFiles(scope.Root, "*.pao.tmp", SearchOption.TopDirectoryOnly).Any(),
            "ADS path rejection left a sibling temporary file");
    }

    private static void CleanFailureStopsBeforeExtraction()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("payload.zip");
        WriteRawArchive(archive, new RawEntry("payload.txt", Bytes("new")));
        var destination = scope.CreateDirectory("destination");
        var locked = Path.Combine(destination, "locked.txt");
        File.WriteAllText(locked, "locked", Encoding.UTF8);
        using var lockStream = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        File.SetAttributes(locked, File.GetAttributes(locked) | FileAttributes.ReadOnly);

        var failure = ExpectCode("clean_failed", () => new ZipOperations().Extract(archive, destination, "clean"));
        Require(failure.Copied == 0, "clean failure reported extraction work");
        Require(failure.Changed >= 1 && failure.Partial,
            "read-only attribute mutation was not propagated as partial state");
        Require(!File.Exists(Path.Combine(destination, "payload.txt")), "extraction started after clean failed");
    }

    private static void CleanExtractionFaultReportsCounters()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("payload.zip");
        WriteRawArchive(archive, new RawEntry("payload.txt", Bytes("new")));
        var destination = scope.CreateDirectory("destination");
        var marker = Path.Combine(destination, "marker.txt");
        File.WriteAllText(marker, "old", Encoding.UTF8);
        var operations = new ZipOperations(new ThrowingFault(ZipFaultPoint.BeforeExtractEntry));

        var failure = ExpectCode("extract_incomplete", () => operations.Extract(archive, destination, "clean"));
        Require(failure.Deleted == 1 && failure.Copied == 0,
            "partial extraction did not preserve honest clean/extract counters");
        Require(!File.Exists(marker), "injected failure falsely rolled clean back");
        Require(!File.Exists(Path.Combine(destination, "payload.txt")), "fault occurred after publishing the payload");
    }

    private static void CreatedDirectoriesPropagatePartialMutation()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("directories-before-fault.zip");
        WriteRawArchive(archive,
            new RawEntry("empty/", []),
            new RawEntry("payload.txt", Bytes("new")));
        var destination = scope.PathOf("new-destination");
        var fault = new ThrowingFault(
            ZipFaultPoint.BeforeExtractEntry,
            path => path.EndsWith("payload.txt", StringComparison.OrdinalIgnoreCase));

        var failure = ExpectCode("extract_incomplete", () => new ZipOperations(fault).Extract(archive, destination));
        Require(failure.Deleted == 0 && failure.Copied == 0, "directory-only mutation corrupted file counters");
        Require(failure.Changed >= 2 && failure.Partial,
            "created destination root and archive directory were not propagated as partial state");
        Require(Directory.Exists(destination) && Directory.Exists(Path.Combine(destination, "empty")),
            "fault fixture did not leave the expected created directories");
        Require(!File.Exists(Path.Combine(destination, "payload.txt")), "fault occurred after file publication");
    }

    private static void CleanReparseLeafDoesNotTraverse()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("payload.zip");
        WriteRawArchive(archive, new RawEntry("payload.txt", Bytes("new")));
        var destination = scope.CreateDirectory("destination");
        var external = scope.CreateDirectory("external");
        var marker = Path.Combine(external, "marker.txt");
        File.WriteAllText(marker, "outside", Encoding.UTF8);
        var junction = Path.Combine(destination, "link-leaf");
        CreateJunction(junction, external);
        File.WriteAllText(Path.Combine(destination, "old.txt"), "old", Encoding.UTF8);

        var result = new ZipOperations().Extract(archive, destination, "clean");
        Require(result.Deleted == 2, "clean did not count the reparse leaf and old file");
        Require(!Directory.Exists(junction), "clean retained the destination reparse leaf");
        Require(File.ReadAllText(marker, Encoding.UTF8) == "outside", "clean traversed the reparse target");
        Require(File.ReadAllText(Path.Combine(destination, "payload.txt"), Encoding.UTF8) == "new",
            "payload was not extracted after cleaning");
    }

    private static void CleanDirectoryIdentityBlocksSwap()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("payload.zip");
        WriteRawArchive(archive, new RawEntry("payload.txt", Bytes("new")));
        var destination = scope.CreateDirectory("destination");
        var victim = Directory.CreateDirectory(Path.Combine(destination, "victim")).FullName;
        File.WriteAllText(Path.Combine(victim, "old.txt"), "old", Encoding.UTF8);
        var external = scope.CreateDirectory("external");
        var marker = Path.Combine(external, "marker.txt");
        File.WriteAllText(marker, "outside", Encoding.UTF8);
        var fault = new DirectorySwapFault(ZipFaultPoint.BeforeCleanDirectoryEnumeration, victim, external);

        Failure? failure = null;
        try { new ZipOperations(fault).Extract(archive, destination, "clean"); }
        catch (Exception ex) { failure = ToFailure(ex); }

        Require(fault.Attempted, "clean mutation hook did not attempt the directory identity swap");
        Require(fault.Blocked || fault.Swapped, "clean swap fixture reached no terminal state");
        Require(File.ReadAllText(marker, Encoding.UTF8) == "outside", "clean traversed a swapped junction target");
        if (fault.Swapped)
            Require(failure is { Code: "clean_failed" }, "a successful swap was not rejected by identity validation");
        else
            Require(failure is null, $"blocked swap unexpectedly failed clean: {failure?.Code}");
    }

    private static void ExtractPublicationIdentityBlocksSwap()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("payload.zip");
        WriteRawArchive(archive, new RawEntry("nested/payload.txt", Bytes("new")));
        var destination = scope.CreateDirectory("destination");
        var nested = Path.Combine(destination, "nested");
        var external = scope.CreateDirectory("external");
        var marker = Path.Combine(external, "marker.txt");
        File.WriteAllText(marker, "outside", Encoding.UTF8);
        var fault = new ParentSwapFault(nested, external);

        Failure? failure = null;
        try { new ZipOperations(fault).Extract(archive, destination, "replace"); }
        catch (Exception ex) { failure = ToFailure(ex); }

        Require(fault.Attempted, "publication hook did not attempt the parent identity swap");
        Require(fault.Blocked || fault.Swapped, "publication swap fixture reached no terminal state");
        Require(File.ReadAllText(marker, Encoding.UTF8) == "outside", "publication traversed a swapped junction target");
        Require(!File.Exists(Path.Combine(external, "payload.txt")), "publication wrote through the junction target");
        if (fault.Swapped)
            Require(failure is { Code: "extract_incomplete", Partial: true },
                "a successful publication-parent swap was not rejected with honest partial state");
        else
        {
            Require(failure is null, $"blocked publication swap unexpectedly failed extraction: {failure?.Code}");
            Require(File.ReadAllText(Path.Combine(nested, "payload.txt"), Encoding.UTF8) == "new",
                "payload was not published after the blocked swap");
        }
    }

    private static void PreEntryCancellation()
    {
        using var scope = new TestScope();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var operations = new ZipOperations();
        ExpectCanceled(() => operations.Inspect(scope.PathOf("missing.zip"), null, canceled.Token));
        ExpectCanceled(() => operations.Create(scope.PathOf("missing-source"), scope.PathOf("new.zip"), "fail", null, canceled.Token));
        ExpectCanceled(() => operations.Extract(scope.PathOf("missing.zip"), scope.PathOf("new-target"), "clean", null, canceled.Token));
        Require(!Directory.EnumerateFileSystemEntries(scope.Root).Any(), "pre-entry cancel created filesystem state");
        Require(typeof(ZipOperationException).GetConstructor(
            [typeof(string), typeof(long), typeof(long), typeof(long), typeof(Exception)]) is not null,
            "original physical five-argument exception constructor was removed");
        var original = new ZipOperationException("fixture", 1, 2, 3);
        var cleanup = new ZipOperationException("canceled", 0, 0, 0, new OperationCanceledException(), true);
        Require(original.Partial && !original.TemporaryCleanupFailed && cleanup.Partial && cleanup.TemporaryCleanupFailed,
            "old/new partial semantics or exact counters changed");
    }

    private static void AdmissionCancellation()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("content.zip");
        WriteRawArchive(archive, new RawEntry("payload.bin", RandomBytes(512 * 1024)));
        foreach (var inspect in new[] { true, false })
        {
            using var canceled = new CancellationTokenSource();
            var target = scope.CreateDirectory(inspect ? "inspect-target" : "extract-target");
            var marker = Path.Combine(target, "marker"); File.WriteAllText(marker, "keep");
            var observed = 0;
            var operations = new ZipOperations(new CallbackFault((point, path) =>
            {
                if (point != ZipFaultPoint.AfterContentChunk || path != "payload.bin") return;
                observed++; canceled.Cancel();
            }));
            if (inspect) ExpectCanceled(() => operations.Inspect(archive, null, canceled.Token));
            else ExpectCanceled(() => operations.Extract(archive, target, "clean", null, canceled.Token));
            Require(observed == 1 && File.ReadAllText(marker) == "keep" && Directory.GetFiles(target).Length == 1,
                "admission cancel occurred after destination effects or continued reading chunks");
            using var exclusive = new FileStream(archive, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Require(exclusive.Length > 0, "original archive handle not released after admission cancellation");
        }
        NoTemporary(scope.Root);
    }

    private static void CreationCancellation()
    {
        using var scope = new TestScope();
        var source = scope.CreateDirectory("source");
        var payload = Path.Combine(source, "payload.bin"); File.WriteAllBytes(payload, RandomBytes(512 * 1024));
        foreach (var stage in new[] { "walk", "write", "verify" })
        {
            var destination = scope.PathOf(stage + ".zip"); File.WriteAllText(destination, "prior archive");
            using var canceled = new CancellationTokenSource();
            var observed = 0;
            var operations = new ZipOperations(new CallbackFault((point, path) =>
            {
                var match = stage == "walk" ? point == ZipFaultPoint.BeforeSourceEntry :
                    point == ZipFaultPoint.AfterContentChunk && (stage == "write" ? path == payload : path == "payload.bin");
                if (!match) return;
                observed++; canceled.Cancel();
            }));
            ExpectCanceled(() => operations.Create(source, destination, "replace", null, canceled.Token));
            Require(observed == 1 && File.ReadAllText(destination) == "prior archive" && new FileInfo(payload).Length == 512 * 1024,
                stage + " cancellation replaced the prior archive or continued content work");
            NoTemporary(scope.Root);
        }
    }

    private static void CleanCancellationCounters()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("clean.zip"); WriteRawArchive(archive, new RawEntry("new.txt", Bytes("new")));
        var destination = scope.CreateDirectory("destination");
        var nested = Directory.CreateDirectory(Path.Combine(destination, "nested")).FullName;
        File.WriteAllText(Path.Combine(nested, "one"), "old"); File.WriteAllText(Path.Combine(nested, "two"), "old");
        using (var canceled = new CancellationTokenSource())
        {
            var operations = new ZipOperations(new CallbackFault((point, _) =>
            { if (point == ZipFaultPoint.BeforeCleanEntry) canceled.Cancel(); }));
            ExpectCanceled(() => operations.Extract(archive, destination, "clean", null, canceled.Token));
            Require(Directory.GetFiles(nested).Length == 2 && !File.Exists(Path.Combine(destination, "new.txt")),
                "cancellation before first clean leaf caused an effect");
        }
        using (var canceled = new CancellationTokenSource())
        {
            var operations = new ZipOperations(new CallbackFault((point, _) =>
            { if (point == ZipFaultPoint.AfterDestinationChange) canceled.Cancel(); }));
            var result = ExpectCanceledPartial(() => operations.Extract(archive, destination, "clean", null, canceled.Token));
            Require(result.Deleted == 1 && result.Copied == 0 && result.Changed == 1 && !result.TemporaryCleanupFailed &&
                Directory.GetFiles(nested).Length == 1 && !File.Exists(Path.Combine(destination, "new.txt")),
                "nested clean cancellation double-counted or lost completed child work");
        }
        var readonlyDestination = scope.CreateDirectory("readonly-destination");
        var readonlyFile = Path.Combine(readonlyDestination, "readonly"); File.WriteAllText(readonlyFile, "old");
        File.SetAttributes(readonlyFile, File.GetAttributes(readonlyFile) | FileAttributes.ReadOnly);
        using (var canceled = new CancellationTokenSource())
        {
            var operations = new ZipOperations(new CallbackFault((point, _) =>
            { if (point == ZipFaultPoint.AfterDestinationChange) canceled.Cancel(); }));
            var result = ExpectCanceledPartial(() => operations.Extract(archive, readonlyDestination, "clean", null, canceled.Token));
            Require(result.Deleted == 0 && result.Copied == 0 && result.Changed == 1 && File.Exists(readonlyFile) &&
                (File.GetAttributes(readonlyFile) & FileAttributes.ReadOnly) == 0,
                "attribute-only clean effect was omitted or relabeled as a deletion");
        }
        var newDestination = scope.PathOf("new-destination");
        using (var canceled = new CancellationTokenSource())
        {
            var operations = new ZipOperations(new CallbackFault((point, path) =>
            { if (point == ZipFaultPoint.AfterDestinationChange && path == newDestination) canceled.Cancel(); }));
            var result = ExpectCanceledPartial(() => operations.Extract(archive, newDestination, "fail", null, canceled.Token));
            Require(result.Deleted == 0 && result.Copied == 0 && result.Changed == 1 && Directory.Exists(newDestination),
                "new destination directory effect was lost on cancellation");
        }
        using var exclusive = new FileStream(archive, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static void ExtractionStagingCancellation()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("extract.zip"); WriteRawArchive(archive, new RawEntry("payload.bin", RandomBytes(512 * 1024)));
        var destination = scope.CreateDirectory("destination");
        var target = Path.Combine(destination, "payload.bin"); File.WriteAllText(target, "prior");
        foreach (var point in new[] { ZipFaultPoint.AfterContentChunk, ZipFaultPoint.BeforeExtractFilePublish })
        {
            using var canceled = new CancellationTokenSource();
            var observed = 0;
            var operations = new ZipOperations(new CallbackFault((actual, path) =>
            { if (actual == point && path == target) { observed++; canceled.Cancel(); } }));
            ExpectCanceled(() => operations.Extract(archive, destination, "replace", null, canceled.Token));
            Require(observed == 1 && File.ReadAllText(target) == "prior", "staging cancellation replaced destination");
            NoTemporary(scope.Root);
        }
    }

    private static void PublicationCancellationBoundaries()
    {
        using var scope = new TestScope();
        var archive = scope.PathOf("two.zip");
        WriteRawArchive(archive, new RawEntry("one", Bytes("one")), new RawEntry("two", Bytes("two")));
        var destination = scope.CreateDirectory("two-destination");
        using (var canceled = new CancellationTokenSource())
        {
            var operations = new ZipOperations(new CallbackFault((point, _) =>
            { if (point == ZipFaultPoint.AfterPublish) canceled.Cancel(); }));
            var result = ExpectCanceledPartial(() => operations.Extract(archive, destination, "replace", null, canceled.Token));
            Require(result.Deleted == 0 && result.Copied == 1 && result.Changed == 1 && Directory.GetFiles(destination).Length == 1,
                "prior publication must be counted exactly before next-entry cancellation");
        }
        var single = scope.PathOf("single.zip"); WriteRawArchive(single, new RawEntry("one", Bytes("complete")));
        using (var canceled = new CancellationTokenSource())
        {
            var operations = new ZipOperations(new CallbackFault((point, _) =>
            { if (point == ZipFaultPoint.AfterPublish) canceled.Cancel(); }));
            var result = operations.Extract(single, scope.CreateDirectory("single-destination"), "replace", null, canceled.Token);
            Require(canceled.IsCancellationRequested && !result.Partial && result.Copied == 1 && result.Changed == 1,
                "final extraction publication was relabeled canceled");
        }
        var source = scope.CreateDirectory("create-source"); File.WriteAllText(Path.Combine(source, "one"), "complete");
        using (var canceled = new CancellationTokenSource())
        {
            var operations = new ZipOperations(new CallbackFault((point, _) =>
            { if (point == ZipFaultPoint.AfterPublish) canceled.Cancel(); }));
            var result = operations.Create(source, scope.PathOf("complete.zip"), "fail", null, canceled.Token);
            Require(canceled.IsCancellationRequested && !result.Partial && result.Copied == 1 && result.Changed == 1,
                "final archive publication was relabeled canceled");
        }
        NoTemporary(scope.Root);
    }

    private static void TemporaryCleanupFailureIsPartial()
    {
        using var scope = new TestScope();
        var source = scope.CreateDirectory("source"); File.WriteAllText(Path.Combine(source, "one"), "payload");
        var archive = scope.PathOf("existing.zip"); File.WriteAllText(archive, "prior");
        foreach (var create in new[] { true, false })
        {
            var destination = scope.CreateDirectory(create ? "unused" : "extract-destination");
            var validArchive = scope.PathOf(create ? "unused.zip" : "extract-source.zip");
            if (!create) WriteRawArchive(validArchive, new RawEntry("one", Bytes("payload")));
            using var canceled = new CancellationTokenSource();
            FileStream? held = null;
            var operations = new ZipOperations(new CallbackFault((point, path) =>
            {
                if (point != (create ? ZipFaultPoint.BeforePublish : ZipFaultPoint.BeforeExtractFilePublish)) return;
                var temporary = Directory.GetFiles(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".*.pao.tmp");
                Require(temporary.Length == 1, "expected exactly one owned staged temporary");
                held = new FileStream(temporary[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                canceled.Cancel();
            }));
            try
            {
                var result = ExpectCanceledPartial(() =>
                {
                    if (create) operations.Create(source, archive, "replace", null, canceled.Token);
                    else operations.Extract(validArchive, destination, "replace", null, canceled.Token);
                });
                Require(result.TemporaryCleanupFailed && result.Deleted == 0 && result.Copied == 0 && result.Changed == 0 &&
                    result.InnerException is OperationCanceledException && held is not null,
                    "cleanup failure lost cancellation or invented destination counters");
                Require(File.ReadAllText(archive) == "prior" && Directory.GetFiles(destination).All(x => x.EndsWith(".pao.tmp", StringComparison.Ordinal)),
                    "failed temporary cleanup changed a published destination");
            }
            finally { held?.Dispose(); }
        }
    }

    private static void TemporaryCleanupPreservesPrimaryError()
    {
        using var scope = new TestScope();
        var source = scope.CreateDirectory("source"); File.WriteAllText(Path.Combine(source, "one"), "payload");
        var target = scope.PathOf("existing.zip"); File.WriteAllText(target, "prior");
        FileStream? held = null;
        var operations = new ZipOperations(new CallbackFault((point, path) =>
        {
            if (point != ZipFaultPoint.BeforePublish) return;
            var temporary = Directory.GetFiles(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".*.pao.tmp");
            Require(temporary.Length == 1, "expected one staged temp before primary failure");
            held = new FileStream(temporary[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            throw new AgentOpsException("precondition");
        }));
        try
        {
            var failure = ExpectCode("precondition", () => operations.Create(source, target, "replace"));
            Require(failure.Exception is ZipOperationException { TemporaryCleanupFailed: true, Partial: true,
                Deleted: 0, Copied: 0, Changed: 0, InnerException: AgentOpsException { Code: "precondition" } } &&
                File.ReadAllText(target) == "prior", "cleanup failure replaced the original error or prior archive");
        }
        finally { held?.Dispose(); }
    }

    private static void ExpectCanceled(Action operation)
    {
        try { operation(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("expected nonpartial original cancellation");
    }

    private static ZipOperationException ExpectCanceledPartial(Action operation)
    {
        try { operation(); }
        catch (ZipOperationException ex) when (ex.Code == "canceled")
        { Require(ex.Partial, "canceled destination effects were reported nonpartial"); return ex; }
        throw new InvalidOperationException("expected canceled partial ZIP outcome");
    }

    private static void NoTemporary(string root) => Require(
        !Directory.EnumerateFiles(root, "*.pao.tmp", SearchOption.AllDirectories).Any(), "unpublished temporary was retained without cleanup failure");

    private sealed class CallbackFault(Action<ZipFaultPoint, string> callback) : IZipFaultInjector
    { public void OnFaultPoint(ZipFaultPoint point, string path) => callback(point, path); }

    private static void WriteRawArchive(string path, params RawEntry[] entries)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var raw in entries)
        {
            var entry = archive.CreateEntry(raw.Name, raw.Compression);
            if (raw.ExternalAttributes is int attributes) entry.ExternalAttributes = attributes;
            using var output = entry.Open();
            output.Write(raw.Content);
        }
    }

    private static void CorruptFirstCentralDirectoryCrc(string path)
    {
        var bytes = File.ReadAllBytes(path);
        const uint centralHeader = 0x02014B50;
        for (var i = 0; i <= bytes.Length - 46; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) != centralHeader) continue;
            var current = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 16, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 16, 4), current ^ 0x00000001u);
            File.WriteAllBytes(path, bytes);
            return;
        }
        throw new InvalidOperationException("central-directory header was not found");
    }

    private static void CreateJunction(string link, string target)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "New-TestJunction.ps1");
        var configured = Environment.GetEnvironmentVariable("PAO_PWSH_PATH");
        // Use the existing read-only installed PS7 locator, independent of a
        // caller's PATH or a removed Store-package version. This never registers a tool.
        var executable = !string.IsNullOrWhiteSpace(configured) ? configured : PowerShell7ToolBootstrap.ResolveExecutable();
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-LinkPath");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add("-TargetPath");
        start.ArgumentList.Add(target);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("junction setup timed out");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"junction setup failed: {stderr} {stdout}".Trim());
    }

    private static Failure ExpectCode(string code, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            var failure = ToFailure(ex);
            if (failure.Code == code) return failure;
            throw new InvalidOperationException($"expected {code}, received {failure.Code}", ex);
        }
        throw new InvalidOperationException($"expected {code}, operation succeeded");
    }

    private static Failure ToFailure(Exception exception) => exception switch
    {
        ZipOperationException ex => new Failure(ex.Code, ex.Deleted, ex.Copied, ex.Changed, ex.Partial, ex),
        AgentOpsException ex => new Failure(ex.Code, ex.Deleted, ex.Copied, ex.Deleted + ex.Copied,
            ex.Deleted + ex.Copied > 0, ex),
        _ => throw new InvalidOperationException($"unexpected exception {exception.GetType().Name}", exception)
    };

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record RawEntry(
        string Name,
        byte[] Content,
        int? ExternalAttributes = null,
        CompressionLevel Compression = CompressionLevel.Optimal)
    {
        public RawEntry(string name, byte[] content, CompressionLevel compression)
            : this(name, content, null, compression)
        {
        }
    }

    private sealed record Failure(
        string Code,
        long Deleted,
        long Copied,
        long Changed,
        bool Partial,
        Exception Exception);

    private sealed class ThrowingFault : IZipFaultInjector
    {
        private readonly ZipFaultPoint _point;
        private readonly Func<string, bool> _matches;

        public ThrowingFault(ZipFaultPoint point, Func<string, bool>? matches = null)
        {
            _point = point;
            _matches = matches ?? (_ => true);
        }

        public void OnFaultPoint(ZipFaultPoint actual, string path)
        {
            if (actual == _point && _matches(path)) throw new InvalidOperationException("injected ZIP fault");
        }
    }

    private sealed class CountingFault(ZipFaultPoint point) : IZipFaultInjector
    {
        public int Count { get; private set; }

        public void OnFaultPoint(ZipFaultPoint actual, string path)
        {
            _ = path;
            if (actual == point) Count++;
        }
    }

    private sealed class DirectorySwapFault(
        ZipFaultPoint point,
        string directory,
        string external) : IZipFaultInjector
    {
        public bool Attempted { get; private set; }
        public bool Blocked { get; private set; }
        public bool Swapped { get; private set; }

        public void OnFaultPoint(ZipFaultPoint actual, string path)
        {
            if (Attempted || actual != point ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    Path.TrimEndingDirectorySeparator(path),
                    Path.TrimEndingDirectorySeparator(directory))) return;
            Attempted = true;
            var backup = directory + ".swapped";
            try
            {
                Directory.Move(directory, backup);
                CreateJunction(directory, external);
                Swapped = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Blocked = true;
            }
        }
    }

    private sealed class ParentSwapFault(string parent, string external) : IZipFaultInjector
    {
        public bool Attempted { get; private set; }
        public bool Blocked { get; private set; }
        public bool Swapped { get; private set; }

        public void OnFaultPoint(ZipFaultPoint actual, string path)
        {
            if (Attempted || actual != ZipFaultPoint.BeforeExtractFilePublish ||
                !StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(path), parent)) return;
            Attempted = true;
            var backup = parent + ".swapped";
            try
            {
                Directory.Move(parent, backup);
                CreateJunction(parent, external);
                Swapped = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Blocked = true;
            }
        }
    }

    private sealed class TestScope : IDisposable
    {
        public TestScope()
        {
            Root = Path.Combine(Path.GetTempPath(), "Pegasus.AgentOps.Zip.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }
        public string PathOf(string relative) => Path.Combine(Root, relative);

        public string CreateDirectory(string relative)
        {
            var path = PathOf(relative);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            var full = Path.GetFullPath(Root);
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Pegasus.AgentOps.Zip.Tests"));
            Require(StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(parent)) &&
                Guid.TryParseExact(Path.GetFileName(full), "N", out _) &&
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0, "ZIP fixture cleanup boundary changed");
            DeleteTreeNoFollow(full);
        }

        private static void DeleteTreeNoFollow(string path)
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                if ((attrs & FileAttributes.Directory) != 0) Directory.Delete(path); else File.Delete(path);
                return;
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var entryAttrs = File.GetAttributes(entry);
                if ((entryAttrs & FileAttributes.ReparsePoint) != 0)
                {
                    if ((entryAttrs & FileAttributes.Directory) != 0) Directory.Delete(entry); else File.Delete(entry);
                }
                else if ((entryAttrs & FileAttributes.Directory) != 0)
                {
                    DeleteTreeNoFollow(entry);
                }
                else
                {
                    if ((entryAttrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(entry, entryAttrs & ~FileAttributes.ReadOnly);
                    File.Delete(entry);
                }
            }
            Directory.Delete(path);
        }
    }
}
