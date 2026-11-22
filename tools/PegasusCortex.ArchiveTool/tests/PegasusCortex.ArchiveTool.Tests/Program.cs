using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Pegasus.Cortex.ArchiveTool.Archive;
using Pegasus.Cortex.ArchiveTool.Cli;

return ArchiveToolTests.RunAll();

internal static class ArchiveToolTests
{
    private static readonly (string Name, Action Body)[] Cases =
    [
        ("valid archive is deterministic", ValidArchiveIsDeterministic),
        ("GNU archive is accepted", GnuArchiveIsAccepted),
        ("CLI emits stable result JSON", CliEmitsStableResultJson),
        ("traversal is rejected before mutation", TraversalIsRejected),
        ("symbolic link is rejected before mutation", SymbolicLinkIsRejected),
        ("duplicate path is rejected", DuplicatePathIsRejected),
        ("portable case collision is rejected", CaseCollisionIsRejected),
        ("special mode is rejected", SpecialModeIsRejected),
        ("file-size budget is enforced", FileSizeBudgetIsEnforced),
        ("staging path budget is enforced", StagingPathBudgetIsEnforced),
        ("existing destination is never overwritten", ExistingDestinationIsNeverOverwritten),
        ("existing inventory is never overwritten", ExistingInventoryIsNeverOverwritten),
        ("extraction failure cleans owned staging", ExtractionFailureCleansStaging),
        ("cleanup failure is explicit and sanitized", CleanupFailureIsExplicit),
        ("failure after destination commit rolls back", FailureAfterDestinationCommitRollsBack),
        ("failure after inventory commit rolls back", FailureAfterInventoryCommitRollsBack),
        ("PAX ambiguity is rejected", PaxAmbiguityIsRejected),
        ("reserved and ADS paths are rejected", ReservedAndAdsPathsAreRejected),
        ("non-NFC path is rejected", NonNfcPathIsRejected),
        ("concatenated gzip payload is rejected", ConcatenatedGZipPayloadIsRejected),
        ("empty concatenated gzip member is rejected", EmptyConcatenatedGZipMemberIsRejected),
        ("single-block TAR terminator is rejected", SingleBlockTarTerminatorIsRejected),
        ("compressed trailing junk is rejected", CompressedTrailingJunkIsRejected)
    ];

    internal static int RunAll()
    {
        var failures = new List<string>();
        foreach (var test in Cases)
        {
            try
            {
                test.Body();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                failures.Add(test.Name);
                Console.Error.WriteLine($"FAIL {test.Name}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        Console.WriteLine($"SUMMARY passed={Cases.Length - failures.Count} failed={failures.Count}");
        return failures.Count == 0 ? 0 : 1;
    }

    private static void ValidArchiveIsDeterministic()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(
            DirectoryEntry("./bin/"),
            FileEntry("./bin/tool", "hello", 0x1ed),
            FileEntry("./NOTICE", "notice", 0x1a4));

        var first = StructuredArchiveExtractor.Extract(scope.Request(archive, "runtime-a", "inventory-a.json"));
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        ArchiveExtractionResult second;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            second = StructuredArchiveExtractor.Extract(scope.Request(archive, "runtime-b", "inventory-b.json"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        Assert(File.ReadAllText(Path.Combine(first.DestinationPath, "bin", "tool")) == "hello", "valid file content differs");
        Assert(File.ReadAllBytes(first.InventoryPath).SequenceEqual(File.ReadAllBytes(second.InventoryPath)), "inventory is not deterministic");
        using var document = JsonDocument.Parse(File.ReadAllBytes(first.InventoryPath));
        var root = document.RootElement;
        Assert(root.GetProperty("schemaVersion").GetString() == "pegasus.cortex.archive-inventory/1", "inventory schema differs");
        Assert(root.GetProperty("archiveEntryCount").GetInt32() == 3, "archive entry count differs");
        Assert(root.GetProperty("materializedEntryCount").GetInt32() == 3, "materialized count differs");
    }

    private static void GnuArchiveIsAccepted()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(
            TarEntryFormat.Gnu,
            GnuDirectoryEntry("./bin/"),
            GnuFileEntry("./bin/tool", "hello", 0x1ed));
        var result = StructuredArchiveExtractor.Extract(scope.Request(archive));
        Assert(File.ReadAllText(Path.Combine(result.DestinationPath, "bin", "tool")) == "hello", "GNU content differs");
    }

    private static void CliEmitsStableResultJson()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(FileEntry("./file.txt", "content", 0x1a4));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = CliApplication.Run(
        [
            "extract",
            "--archive", archive,
            "--sha256", HashFile(archive),
            "--destination-root", scope.Root,
            "--destination-name", "runtime",
            "--inventory", Path.Combine(scope.Root, "inventory.json")
        ], output, error);

        Assert(exitCode == 0, "CLI did not succeed");
        Assert(error.ToString().Length == 0, "CLI wrote an unexpected error");
        using var document = JsonDocument.Parse(output.ToString());
        Assert(document.RootElement.GetProperty("status").GetString() == "succeeded", "CLI result status differs");
        Assert(document.RootElement.GetProperty("archiveEntryCount").GetInt32() == 1, "CLI result count differs");
    }

    private static void TraversalIsRejected()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(FileEntry("../escape", "x", 0x1a4));
        ExpectFailure("entry_path_invalid", () => StructuredArchiveExtractor.Extract(scope.Request(archive)));
        AssertNoDestinationOrStaging(scope);
    }

    private static void SymbolicLinkIsRejected()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(LinkEntry("./link", "target"));
        ExpectFailure("entry_type_unsupported", () => StructuredArchiveExtractor.Extract(scope.Request(archive)));
        AssertNoDestinationOrStaging(scope);
    }

    private static void DuplicatePathIsRejected()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(
            FileEntry("./same", "one", 0x1a4),
            FileEntry("same", "two", 0x1a4));
        ExpectFailure("path_collision", () => StructuredArchiveExtractor.Extract(scope.Request(archive)));
        AssertNoDestinationOrStaging(scope);
    }

    private static void CaseCollisionIsRejected()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(
            FileEntry("./A.txt", "one", 0x1a4),
            FileEntry("./a.txt", "two", 0x1a4));
        ExpectFailure("path_collision", () => StructuredArchiveExtractor.Extract(scope.Request(archive)));
        AssertNoDestinationOrStaging(scope);
    }

    private static void SpecialModeIsRejected()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(FileEntry("./tool", "x", 0x9ed)); // POSIX 04755.
        ExpectFailure("entry_mode_unsafe", () => StructuredArchiveExtractor.Extract(scope.Request(archive)));
        AssertNoDestinationOrStaging(scope);
    }

    private static void FileSizeBudgetIsEnforced()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(FileEntry("./large", "1234", 0x1a4));
        var limits = new ArchiveSafetyLimits { MaximumFileBytes = 3 };
        ExpectFailure(
            "file_size_over_budget",
            () => StructuredArchiveExtractor.Extract(scope.Request(archive, limits: limits)));
        AssertNoDestinationOrStaging(scope);
    }

    private static void StagingPathBudgetIsEnforced()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(FileEntry("./file", "x", 0x1a4));
        var finalFilePath = Path.Combine(scope.Root, "r", "file");
        var limits = new ArchiveSafetyLimits { MaximumCanonicalPathUtf16 = finalFilePath.Length };
        ExpectFailure(
            "destination_unsafe",
            () => StructuredArchiveExtractor.Extract(scope.Request(archive, "r", "i", limits)));
        Assert(!Directory.EnumerateDirectories(scope.Root, ".pct-*").Any(), "over-budget staging was not cleaned");
    }

    private static void ExistingDestinationIsNeverOverwritten()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(FileEntry("./file", "new", 0x1a4));
        var destination = Path.Combine(scope.Root, "runtime");
        Directory.CreateDirectory(destination);
        var sentinel = Path.Combine(destination, "sentinel");
        File.WriteAllText(sentinel, "keep");

        ExpectFailure("destination_exists", () => StructuredArchiveExtractor.Extract(scope.Request(archive)));
        Assert(File.ReadAllText(sentinel) == "keep", "existing destination was modified");
    }

    private static void ExistingInventoryIsNeverOverwritten()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(FileEntry("./file", "new", 0x1a4));
        var inventory = Path.Combine(scope.Root, "inventory.json");
        File.WriteAllText(inventory, "keep");

        ExpectFailure(
            "inventory_exists",
            () => StructuredArchiveExtractor.Extract(scope.Request(archive, inventoryName: "inventory.json")));
        Assert(File.ReadAllText(inventory) == "keep", "existing inventory was modified");
        Assert(!Directory.Exists(Path.Combine(scope.Root, "runtime")), "destination was created before inventory preflight");
    }

    private static void ExtractionFailureCleansStaging()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(
            FileEntry("./one", "1", 0x1a4),
            FileEntry("./two", "2", 0x1a4));
        var hooks = new ArchiveExtractionHooks(
            BeforeMaterializeEntry: (index, _) =>
            {
                if (index == 1)
                    throw new InvalidOperationException("injected materialization failure");
            });

        ExpectFailure(
            "extraction_failed",
            () => StructuredArchiveExtractor.Extract(scope.Request(archive), hooks));
        AssertNoDestinationOrStaging(scope);
    }

    private static void CleanupFailureIsExplicit()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(
            FileEntry("./one", "1", 0x1a4),
            FileEntry("./two", "2", 0x1a4));
        var cleanupInjected = false;
        var hooks = new ArchiveExtractionHooks(
            BeforeMaterializeEntry: (index, _) =>
            {
                if (index == 1)
                    throw new InvalidOperationException("injected materialization failure");
            },
            BeforeCleanupDelete: path =>
            {
                if (!cleanupInjected && Path.GetFileName(path) == "one")
                {
                    cleanupInjected = true;
                    throw new IOException("injected path-bearing cleanup failure");
                }
            });

        var exception = ExpectFailure(
            "cleanup_failed",
            () => StructuredArchiveExtractor.Extract(scope.Request(archive), hooks));
        Assert(exception.PrimaryCode == "extraction_failed", "cleanup error lost primary cause");
        Assert(exception.CleanupFailureCount > 0, "cleanup error omitted failure count");
        Assert(exception.InnerException is null, "cleanup error exposed an exception graph");

        // Test-owned cleanup is outside the injected production cleanup hook.
        foreach (var directory in Directory.EnumerateDirectories(scope.Root, ".pct-*"))
            Directory.Delete(directory, recursive: true);
    }

    private static void FailureAfterDestinationCommitRollsBack()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(FileEntry("./one", "1", 0x1a4));
        var hooks = new ArchiveExtractionHooks(
            AfterDestinationCommit: () => throw new InvalidOperationException("injected post-destination failure"));
        ExpectFailure("extraction_failed", () => StructuredArchiveExtractor.Extract(scope.Request(archive), hooks));
        AssertNoDestinationOrStaging(scope);
    }

    private static void FailureAfterInventoryCommitRollsBack()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(FileEntry("./one", "1", 0x1a4));
        var hooks = new ArchiveExtractionHooks(
            AfterInventoryCommit: () => throw new InvalidOperationException("injected post-inventory failure"));
        ExpectFailure("extraction_failed", () => StructuredArchiveExtractor.Extract(scope.Request(archive), hooks));
        AssertNoDestinationOrStaging(scope);
    }

    private static void PaxAmbiguityIsRejected()
    {
        using var scope = TestScope.Create();
        var entry = new PaxTarEntry(TarEntryType.RegularFile, "./file", [new("comment", "ambiguous")])
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes("x")),
            Mode = (UnixFileMode)0x1a4,
            ModificationTime = DateTimeOffset.UnixEpoch
        };
        var archive = scope.CreateArchive(entry);
        ExpectFailure("pax_ambiguous", () => StructuredArchiveExtractor.Extract(scope.Request(archive)));
        AssertNoDestinationOrStaging(scope);
    }

    private static void ReservedAndAdsPathsAreRejected()
    {
        using var reservedScope = TestScope.Create();
        var reserved = reservedScope.CreateArchive(FileEntry("./con.txt", "x", 0x1a4));
        ExpectFailure("entry_path_invalid", () => StructuredArchiveExtractor.Extract(reservedScope.Request(reserved)));

        using var adsScope = TestScope.Create();
        var ads = adsScope.CreateArchive(FileEntry("./file:stream", "x", 0x1a4));
        ExpectFailure("entry_path_invalid", () => StructuredArchiveExtractor.Extract(adsScope.Request(ads)));
    }

    private static void NonNfcPathIsRejected()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(FileEntry("./e\u0301.txt", "x", 0x1a4));
        ExpectFailure("entry_path_invalid", () => StructuredArchiveExtractor.Extract(scope.Request(archive)));
        AssertNoDestinationOrStaging(scope);
    }

    private static void ConcatenatedGZipPayloadIsRejected()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(FileEntry("./file", "x", 0x1a4));
        using (var output = new FileStream(archive, FileMode.Append, FileAccess.Write, FileShare.None))
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: false))
            gzip.Write(Encoding.ASCII.GetBytes("not-tar-padding"));

        ExpectFailure("archive_malformed", () => StructuredArchiveExtractor.Extract(scope.Request(archive)));
        AssertNoDestinationOrStaging(scope);
    }

    private static void EmptyConcatenatedGZipMemberIsRejected()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(FileEntry("./file", "x", 0x1a4));
        using (var output = new FileStream(archive, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            // Canonical empty gzip member: header, empty DEFLATE block, CRC32=0, ISIZE=0.
            output.Write([
                0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03,
                0x03, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00
            ]);
        }

        ExpectFailure("archive_malformed", () => StructuredArchiveExtractor.Extract(scope.Request(archive)));
        AssertNoDestinationOrStaging(scope);
    }

    private static void SingleBlockTarTerminatorIsRejected()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(FileEntry("./file", "x", 0x1a4));
        using var tarBytes = new MemoryStream();
        using (var input = File.OpenRead(archive))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: false))
            gzip.CopyTo(tarBytes);
        Assert(tarBytes.Length >= 1024, "test TAR is unexpectedly short");
        tarBytes.SetLength(tarBytes.Length - 512);
        tarBytes.Position = 0;
        using (var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: false))
            tarBytes.CopyTo(gzip);

        ExpectFailure("archive_malformed", () => StructuredArchiveExtractor.Extract(scope.Request(archive)));
        AssertNoDestinationOrStaging(scope);
    }

    private static void CompressedTrailingJunkIsRejected()
    {
        using var scope = TestScope.Create();
        var archive = scope.CreateArchive(FileEntry("./file", "x", 0x1a4));
        using (var output = new FileStream(archive, FileMode.Append, FileAccess.Write, FileShare.None))
            output.Write([0xde, 0xad, 0xbe, 0xef]);

        ExpectFailure("archive_malformed", () => StructuredArchiveExtractor.Extract(scope.Request(archive)));
        AssertNoDestinationOrStaging(scope);
    }

    private static TarEntry DirectoryEntry(string path) => new UstarTarEntry(TarEntryType.Directory, path)
    {
        Mode = (UnixFileMode)0x1ed,
        ModificationTime = DateTimeOffset.UnixEpoch
    };

    private static TarEntry FileEntry(string path, string content, int mode) => new UstarTarEntry(
        TarEntryType.RegularFile,
        path)
    {
        DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
        Mode = (UnixFileMode)mode,
        ModificationTime = DateTimeOffset.UnixEpoch
    };

    private static TarEntry LinkEntry(string path, string target) => new UstarTarEntry(
        TarEntryType.SymbolicLink,
        path)
    {
        LinkName = target,
        Mode = (UnixFileMode)0x1ff,
        ModificationTime = DateTimeOffset.UnixEpoch
    };

    private static TarEntry GnuDirectoryEntry(string path) => new GnuTarEntry(TarEntryType.Directory, path)
    {
        Mode = (UnixFileMode)0x1ed,
        ModificationTime = DateTimeOffset.UnixEpoch
    };

    private static TarEntry GnuFileEntry(string path, string content, int mode) => new GnuTarEntry(
        TarEntryType.RegularFile,
        path)
    {
        DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
        Mode = (UnixFileMode)mode,
        ModificationTime = DateTimeOffset.UnixEpoch
    };

    private static ArchiveBoundaryException ExpectFailure(string code, Action action)
    {
        try
        {
            action();
        }
        catch (ArchiveBoundaryException exception)
        {
            Assert(exception.Code == code, $"expected failure '{code}', received '{exception.Code}'");
            return exception;
        }
        throw new InvalidOperationException($"expected failure '{code}', but operation succeeded");
    }

    private static void AssertNoDestinationOrStaging(TestScope scope)
    {
        Assert(!Directory.Exists(Path.Combine(scope.Root, "runtime")), "destination exists after failure");
        Assert(!Directory.EnumerateDirectories(scope.Root, ".pct-*").Any(), "staging exists after failure");
        Assert(!File.Exists(Path.Combine(scope.Root, "inventory.json")), "inventory exists after failure");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class TestScope : IDisposable
    {
        private TestScope(string root) => Root = root;

        internal string Root { get; }

        internal static TestScope Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"pct-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new TestScope(root);
        }

        internal string CreateArchive(params TarEntry[] entries)
            => CreateArchive(TarEntryFormat.Ustar, entries);

        internal string CreateArchive(TarEntryFormat format, params TarEntry[] entries)
        {
            var path = Path.Combine(Root, $"archive-{Guid.NewGuid():N}.tar.gz");
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: false);
            using var writer = new TarWriter(gzip, format, leaveOpen: false);
            foreach (var entry in entries)
                writer.WriteEntry(entry);
            return path;
        }

        internal ArchiveExtractionRequest Request(
            string archive,
            string destinationName = "runtime",
            string inventoryName = "inventory.json",
            ArchiveSafetyLimits? limits = null) => new(
                archive,
                HashFile(archive),
                Root,
                destinationName,
                Path.Combine(Root, inventoryName),
                limits);

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
