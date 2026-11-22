using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Pegasus.AgentOps.Engine;

if (args.Length > 0 && args[0].StartsWith("--helper-", StringComparison.Ordinal))
    return await RunHelperAsync(args);

var tests = new (string Name, Func<Task> Run)[]
{
    ("typed_argument_vector_and_valid_listing", TypedArgumentVectorAsync),
    ("unsafe_archive_names_rejected", UnsafeNamesAsync),
    ("UTF8_and_final_plus_staging_path_budgets", ExtractionPathBudgetsAsync),
    ("links_special_entries_and_encryption_rejected", SpecialEntriesAsync),
    ("case_and_shape_collisions_rejected", CollisionsAsync),
    ("listing_and_expansion_bounds_enforced", BoundsAsync),
    ("integrity_test_precedes_clean", IntegrityBeforeCleanAsync),
    ("clean_streams_verified_files_and_keeps_root", CleanExtractAsync),
    ("fail_replace_and_skip_are_exact", PoliciesAsync),
    ("crc_and_length_failures_are_honest", VerificationFailuresAsync),
    ("clean_copy_failure_reports_partial", CleanPartialAsync),
    ("archive_and_executable_are_held", InputsHeldAsync),
    ("input_paths_cannot_be_destination_descendants", InputRelationshipAsync),
    ("bounded_process_failures_are_closed", BoundedFailuresAsync),
    ("real_process_output_timeout_and_cancel_are_bounded", RealProcessBoundsAsync),
    ("real_7zip_probe_when_installed", RealSevenZipProbeAsync)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name} {ex}");
    }
}

Console.WriteLine($"RESULT total={tests.Length} passed={tests.Length - failed} failed={failed}");
return failed == 0 ? 0 : 1;

static async Task<int> RunHelperAsync(string[] helperArgs)
{
    if (helperArgs[0] == "--helper-spam")
    {
        var count = int.Parse(helperArgs[1], CultureInfo.InvariantCulture);
        var bytes = Enumerable.Repeat((byte)'x', count).ToArray();
        await Console.OpenStandardOutput().WriteAsync(bytes);
        await Console.OpenStandardError().WriteAsync(bytes);
        return 0;
    }
    if (helperArgs[0] == "--helper-sleep")
    {
        await Task.Delay(int.Parse(helperArgs[1], CultureInfo.InvariantCulture));
        return 0;
    }
    return 2;
}

static async Task TypedArgumentVectorAsync()
{
    using var scope = new TestScope();
    var executable = scope.File("7z exact.exe", [0x4d, 0x5a]);
    var archive = scope.File("archive 'brackets[] & 雪.7z", [1, 2, 3, 4]);
    var content = Bytes("payload");
    var runner = new FakeRunner(Listing(
        DirectoryEntry("folder"),
        FileEntry("folder/file '[] & 雪.txt", content, packed: 4)));

    var result = await new SevenZipOperations(runner).ListAsync(executable, archive);
    Equal(2, result.Entries);
    Equal((long)content.Length, result.ExpandedBytes);
    Equal(1, runner.Requests.Count);
    var request = runner.Requests[0];
    Equal(executable, request.ExecutablePath);
    SequenceEqual(
        ["l", "-slt", "-ba", "-bd", "-bb0", "-sccUTF-8", "--", archive],
        request.Arguments);
    Check(request.Arguments[^1].Contains("'brackets[] & 雪", StringComparison.Ordinal), "difficult path was altered");
}

static async Task ExtractionPathBudgetsAsync()
{
    using var scope = new TestScope();
    var executable = scope.File("7z.exe", [0x4d, 0x5a]);
    var archive = scope.File("data.7z", [1]);
    var destination = scope.Directory("budget-output");
    var marker = scope.File(Path.Combine("budget-output", "keep.txt"), Bytes("keep"));
    var parentAt215 = new string('d', 215 - destination.Length - 1);
    foreach (var path in new[]
    {
        new string('雪', 86) + "/x.txt",
        new string('a', 100) + "/" + new string('b', 100) + "/x.txt",
        parentAt215 + "/x" // final target fits; the fixed .pao-7z-GUID.tmp sibling exceeds 240
    })
    {
        var runner = new FakeRunner(Listing(FileEntry(path, Bytes("x"), 1)));
        await ThrowsCodeAsync("archive_name", () =>
            new SevenZipOperations(runner).ExtractAsync(executable, archive, destination, "clean"));
        Equal("keep", File.ReadAllText(marker));
        Equal(1, System.IO.Directory.GetFileSystemEntries(destination).Length);
        SequenceEqual(["l"], runner.Calls);
    }

    var folder = new string('雪', 85);
    var relative = folder + "/x.txt";
    var payload = Bytes("ok");
    var validRunner = new FakeRunner(Listing(FileEntry(relative, payload, 1)),
        new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [folder + Path.DirectorySeparatorChar + "x.txt"] = payload
        });
    var output = scope.Directory("utf8-valid");
    var result = await new SevenZipOperations(validRunner).ExtractAsync(executable, archive, output, "clean");
    Check(result.Ok && !result.Partial, result.Error ?? "valid UTF8 boundary failed");
    Equal("ok", File.ReadAllText(Path.Combine(output, folder, "x.txt")));
}

static async Task UnsafeNamesAsync()
{
    using var scope = new TestScope();
    var executable = scope.File("7z.exe", [0x4d, 0x5a]);
    var archive = scope.File("data.7z", [1]);
    var payload = Bytes("x");
    foreach (var path in new[]
    {
        "../escape.txt", "/absolute.txt", "C:/absolute.txt", "\\\\server\\share\\x.txt",
        "folder/name:stream", "folder/CON.txt", "folder/COM¹.log", "folder/LPT²",
        "folder/trailing. ", "folder/*.txt", "folder//empty.txt"
    })
    {
        var runner = new FakeRunner(Listing(FileEntry(path, payload, 1)));
        await ThrowsAnyCodeAsync(["archive_name", "archive_traversal"],
            () => new SevenZipOperations(runner).ListAsync(executable, archive));
    }
}

static async Task SpecialEntriesAsync()
{
    using var scope = new TestScope();
    var executable = scope.File("7z.exe", [0x4d, 0x5a]);
    var archive = scope.File("data.7z", [1]);
    var payload = Bytes("x");
    var cases = new[]
    {
        Entry("Path = link\nFolder = -\nSize = 1\nPacked Size = 1\nAttributes = A_ lrwxrwxrwx\nCRC = 8CDC1683\nEncrypted = -"),
        Entry("Path = device\nFolder = -\nSize = 1\nPacked Size = 1\nAttributes = A_ brw-rw-rw-\nCRC = 8CDC1683\nEncrypted = -"),
        Entry("Path = link\nFolder = -\nSize = 1\nPacked Size = 1\nAttributes = A\nCRC = 8CDC1683\nEncrypted = -\nSymbolic Link = target"),
        Entry("Path = secret\nFolder = -\nSize = 1\nPacked Size = 1\nAttributes = A\nCRC = 8CDC1683\nEncrypted = +"),
        Entry("Path = solid\nFolder = -\nSize = 1\nPacked Size = \nAttributes = A\nCRC = 8CDC1683\nEncrypted = -")
    };
    foreach (var listing in cases)
    {
        var runner = new FakeRunner(listing);
        await ThrowsAnyCodeAsync(["archive_special", "archive_encrypted", "archive_listing"],
            () => new SevenZipOperations(runner).ListAsync(executable, archive));
    }
    _ = payload;
}

static async Task CollisionsAsync()
{
    using var scope = new TestScope();
    var executable = scope.File("7z.exe", [0x4d, 0x5a]);
    var archive = scope.File("data.7z", [1]);
    var payload = Bytes("x");
    var listings = new[]
    {
        Listing(FileEntry("A.txt", payload, 1), FileEntry("a.TXT", payload, 1)),
        Listing(FileEntry("node", payload, 1), FileEntry("node/child", payload, 1)),
        Listing(DirectoryEntry("node"), FileEntry("NODE", payload, 1)),
        Listing(DirectoryEntry("dup"), DirectoryEntry("DUP"))
    };
    foreach (var listing in listings)
        await ThrowsCodeAsync("archive_collision", () =>
            new SevenZipOperations(new FakeRunner(listing)).ListAsync(executable, archive));

    var implicitThenExplicit = await new SevenZipOperations(new FakeRunner(Listing(
        FileEntry("folder/child.txt", payload, 1), DirectoryEntry("folder"))))
        .ListAsync(executable, archive);
    Equal(2, implicitThenExplicit.Entries);
}

static async Task BoundsAsync()
{
    using var scope = new TestScope();
    var executable = scope.File("7z.exe", [0x4d, 0x5a]);
    var archive = scope.File("data.7z", new byte[10]);
    var payload = Bytes("abcd");

    await ThrowsCodeAsync("archive_limit", () =>
        new SevenZipOperations(new FakeRunner(Listing(FileEntry("a", payload, 1), FileEntry("b", payload, 1))))
            .ListAsync(executable, archive, new SevenZipLimits(MaxEntries: 1)));

    await ThrowsCodeAsync("archive_limit", () =>
        new SevenZipOperations(new FakeRunner(Listing(FileEntry("a", new byte[100], 1))))
            .ListAsync(executable, archive, new SevenZipLimits(MaxExpansionRatio: 10)));

    await ThrowsCodeAsync("archive_limit", () =>
        new SevenZipOperations(new FakeRunner(Listing(FileEntry("a", payload, 1))))
            .ListAsync(executable, archive, new SevenZipLimits(MaxEntryBytes: 3)));

    await ThrowsCodeAsync("archive_limit", () =>
        new SevenZipOperations(new FakeRunner(Listing(FileEntry("a", payload, 1))))
            .ListAsync(executable, archive, new SevenZipLimits(MaxExpandedBytes: 3)));

    var truncated = new FakeRunner(Listing(FileEntry("a", payload, 1))) { ListStdoutTruncated = true };
    await ThrowsCodeAsync("tool_output_limit", () =>
        new SevenZipOperations(truncated).ListAsync(executable, archive));
}

static async Task IntegrityBeforeCleanAsync()
{
    using var scope = new TestScope();
    var executable = scope.File("7z.exe", [0x4d, 0x5a]);
    var archive = scope.File("data.7z", [1]);
    var destination = scope.Directory("destination");
    var old = scope.File(Path.Combine("destination", "old.txt"), Bytes("keep"));
    var data = Bytes("new");
    var runner = new FakeRunner(Listing(FileEntry("new.txt", data, 2))) { TestExitCode = 2 };

    var result = await new SevenZipOperations(runner).ExtractAsync(executable, archive, destination, "clean");
    Check(!result.Ok && result.Error == "archive_invalid" && !result.Partial, "failed integrity result was dishonest");
    Equal("keep", File.ReadAllText(old));
    SequenceEqual(["l", "t"], runner.Calls);
}

static async Task CleanExtractAsync()
{
    using var scope = new TestScope();
    var executable = scope.File("7z.exe", [0x4d, 0x5a]);
    var archive = scope.File("data.7z", [1, 2, 3]);
    var destination = scope.Directory("destination");
    var oldDirectory = scope.Directory(Path.Combine("destination", "old"));
    var oldFile = scope.File(Path.Combine("destination", "old", "readonly.txt"), Bytes("old"));
    File.SetAttributes(oldFile, File.GetAttributes(oldFile) | FileAttributes.ReadOnly);
    var outside = scope.Directory("outside");
    var marker = scope.File(Path.Combine("outside", "marker.txt"), Bytes("outside"));
    var link = Path.Combine(destination, "outside-link");
    Directory.CreateDirectory(link);
    Junction.Create(link, outside);

    var one = Bytes("one");
    var two = Bytes("二 two");
    var runner = new FakeRunner(Listing(
        DirectoryEntry("new"),
        FileEntry("new/one.txt", one, 2),
        FileEntry("top.txt", two, 3)),
        new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["new\\one.txt"] = one,
            ["top.txt"] = two
        });

    var rootCreation = Directory.GetCreationTimeUtc(destination);
    var result = await new SevenZipOperations(runner).ExtractAsync(executable, archive, destination, "clean");
    Check(result.Ok && !result.Partial, result.Error ?? "clean failed");
    Equal(3L, result.Deleted); // old file, old directory, and reparse leaf
    Equal(2L, result.Copied);
    Equal(6L, result.Changed); // 3 deletes + new directory + 2 publishes
    Equal("one", File.ReadAllText(Path.Combine(destination, "new", "one.txt")));
    Equal(Encoding.UTF8.GetString(two), File.ReadAllText(Path.Combine(destination, "top.txt")));
    Equal("outside", File.ReadAllText(marker));
    Check(!Directory.Exists(link), "clean retained the reparse leaf");
    Equal(rootCreation, Directory.GetCreationTimeUtc(destination));
    SequenceEqual(["l", "t", "x:new\\one.txt", "x:top.txt"], runner.Calls);
    _ = oldDirectory;
}

static async Task PoliciesAsync()
{
    using var scope = new TestScope();
    var executable = scope.File("7z.exe", [0x4d, 0x5a]);
    var archive = scope.File("data.7z", [1]);
    var existing = Bytes("new-existing");
    var missing = Bytes("missing");
    var listing = Listing(FileEntry("same.txt", existing, 2), FileEntry("missing.txt", missing, 2));

    var failRoot = scope.Directory("fail");
    await ThrowsCodeAsync("collision", () =>
        new SevenZipOperations(new FakeRunner(listing)).ExtractAsync(executable, archive, failRoot, "fail"));

    var skipRoot = scope.Directory("skip");
    scope.File(Path.Combine("skip", "same.txt"), Bytes("old"));
    var skipRunner = new FakeRunner(listing, new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["same.txt"] = existing,
        ["missing.txt"] = missing
    });
    var skipped = await new SevenZipOperations(skipRunner).ExtractAsync(executable, archive, skipRoot, "skip");
    Check(skipped.Ok, skipped.Error ?? "skip failed");
    Equal(1L, skipped.Copied);
    Equal(1L, skipped.Skipped);
    Equal("old", File.ReadAllText(Path.Combine(skipRoot, "same.txt")));
    Equal("missing", File.ReadAllText(Path.Combine(skipRoot, "missing.txt")));
    SequenceEqual(["l", "t", "x:missing.txt"], skipRunner.Calls);

    var replaceRoot = scope.Directory("replace");
    scope.File(Path.Combine("replace", "same.txt"), Bytes("old"));
    var replaceRunner = new FakeRunner(listing, skipRunner.Content);
    var replaced = await new SevenZipOperations(replaceRunner).ExtractAsync(executable, archive, replaceRoot, "replace");
    Check(replaced.Ok && replaced.Copied == 2, replaced.Error ?? "replace failed");
    Equal("new-existing", File.ReadAllText(Path.Combine(replaceRoot, "same.txt")));
}

static async Task VerificationFailuresAsync()
{
    using var scope = new TestScope();
    var executable = scope.File("7z.exe", [0x4d, 0x5a]);
    var archive = scope.File("data.7z", [1]);
    var first = Bytes("first");
    var declaredSecond = Bytes("second");
    var corruptSecond = Bytes("SECOND");
    var listing = Listing(FileEntry("first.txt", first, 3), FileEntry("second.txt", declaredSecond, 3));
    var root = Path.Combine(scope.Root, "output");
    var runner = new FakeRunner(listing, new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["first.txt"] = first,
        ["second.txt"] = corruptSecond
    });
    var result = await new SevenZipOperations(runner).ExtractAsync(executable, archive, root, "replace");
    Check(!result.Ok && result.Error == "archive_crc" && result.Partial, "CRC failure was not reported honestly");
    Equal(1L, result.Copied);
    Equal(2L, result.Changed); // destination root and first publish
    Check(File.Exists(Path.Combine(root, "first.txt")), "verified first file was lost");
    Check(!File.Exists(Path.Combine(root, "second.txt")), "bad CRC file was published");
    Check(!Directory.EnumerateFiles(root, ".pao-7z-*.tmp").Any(), "temporary file leaked");

    var lengthRoot = Path.Combine(scope.Root, "length-output");
    var shortRunner = new FakeRunner(Listing(FileEntry("short.txt", declaredSecond, 3)),
        new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["short.txt"] = Bytes("x") });
    var shortResult = await new SevenZipOperations(shortRunner).ExtractAsync(executable, archive, lengthRoot, "replace");
    Check(!shortResult.Ok && shortResult.Error == "archive_length", "length mismatch was not rejected");
    Check(!File.Exists(Path.Combine(lengthRoot, "short.txt")), "short file was published");
}

static async Task CleanPartialAsync()
{
    using var scope = new TestScope();
    var executable = scope.File("7z.exe", [0x4d, 0x5a]);
    var archive = scope.File("data.7z", [1]);
    var root = scope.Directory("output");
    scope.File(Path.Combine("output", "old.txt"), Bytes("old"));
    var declared = Bytes("declared");
    var runner = new FakeRunner(Listing(FileEntry("new.txt", declared, 2)),
        new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["new.txt"] = Bytes("wrong!!!") });
    var result = await new SevenZipOperations(runner).ExtractAsync(executable, archive, root, "clean");
    Check(!result.Ok && result.Error == "copy_incomplete" && result.Partial, "clean failure was not mapped to copy_incomplete");
    Equal(1L, result.Deleted);
    Equal(0L, result.Copied);
    Equal(1L, result.Changed);
    Check(!File.Exists(Path.Combine(root, "old.txt")), "clean did not happen before failed copy");
}

static async Task InputsHeldAsync()
{
    using var scope = new TestScope();
    var executable = scope.File("7z.exe", [0x4d, 0x5a]);
    var archive = scope.File("data.7z", [1, 2, 3]);
    var payload = Bytes("x");
    var observed = 0;
    var runner = new FakeRunner(Listing(FileEntry("x", payload, 1)))
    {
        OnRun = () =>
        {
            Throws<IOException>(() => File.Open(executable, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
            Throws<IOException>(() => File.Open(archive, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
            observed++;
        }
    };
    _ = await new SevenZipOperations(runner).TestAsync(executable, archive);
    Equal(2, observed);
}

static async Task BoundedFailuresAsync()
{
    using var scope = new TestScope();
    var executable = scope.File("7z.exe", [0x4d, 0x5a]);
    var archive = scope.File("data.7z", [1]);
    var payload = Bytes("x");
    var timed = new FakeRunner(Listing(FileEntry("x", payload, 1))) { TestTimedOut = true };
    var test = await new SevenZipOperations(timed).TestAsync(executable, archive);
    Check(!test.Ok && test.Error == "timeout", "test timeout was not surfaced");

    var stderr = new FakeRunner(Listing(FileEntry("x", payload, 1))) { TestStderrTruncated = true };
    var limited = await new SevenZipOperations(stderr).TestAsync(executable, archive);
    Check(!limited.Ok && limited.Error == "tool_output_limit", "diagnostic truncation was not fail closed");
}

static async Task InputRelationshipAsync()
{
    using var scope = new TestScope();
    var destination = scope.Directory("destination");
    var executable = scope.File("7z.exe", [0x4d, 0x5a]);
    var archive = scope.File(Path.Combine("destination", "data.7z"), [1]);
    var old = scope.File(Path.Combine("destination", "old.txt"), Bytes("preserved"));
    var payload = Bytes("new");
    var runner = new FakeRunner(Listing(FileEntry("new.txt", payload, 1)));

    await ThrowsCodeAsync("precondition", () =>
        new SevenZipOperations(runner).ExtractAsync(executable, archive, destination, "clean"));
    Equal("preserved", File.ReadAllText(old));
    Equal(0, runner.Requests.Count);
}

static async Task RealProcessBoundsAsync()
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("current process path is unavailable");
    var prefix = Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
        ? new[] { Assembly.GetExecutingAssembly().Location }
        : Array.Empty<string>();
    var runner = new WindowsSevenZipProcessRunner();
    var bounded = await runner.RunTextAsync(new SevenZipProcessRequest(
        executable,
        [.. prefix, "--helper-spam", "200000"],
        10,
        97,
        89));
    Check(bounded.ExitCode == 0 && bounded.StdoutTruncated && bounded.StderrTruncated, "real output was not bounded");
    Equal(97, bounded.Stdout.Length);
    Equal(89, Encoding.UTF8.GetByteCount(bounded.Stderr));
    Equal(200000L, bounded.StdoutBytes);

    var timed = await runner.RunTextAsync(new SevenZipProcessRequest(
        executable,
        [.. prefix, "--helper-sleep", "5000"],
        1,
        1024,
        1024));
    Check(timed.TimedOut && timed.ExitCode != 0, "real timeout did not terminate the child");

    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    await ThrowsAsync<OperationCanceledException>(() => runner.RunTextAsync(new SevenZipProcessRequest(
        executable,
        [.. prefix, "--helper-sleep", "5000"],
        10,
        1024,
        1024), cancellation.Token));
}

static async Task RealSevenZipProbeAsync()
{
    var executable = FindSevenZip();
    if (executable is null)
    {
        Console.WriteLine("PROBE real_7zip unavailable");
        return;
    }

    using var scope = new TestScope();
    var archive = Path.Combine(scope.Root, "real '[] & 雪.zip");
    var expected = Bytes("real process payload 雪");
    using (var stream = new FileStream(archive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
    {
        var entry = zip.CreateEntry("folder/file '[] & 雪.txt", CompressionLevel.Optimal);
        await using var output = entry.Open();
        await output.WriteAsync(expected);
    }

    var operations = new SevenZipOperations();
    var listed = await operations.ListAsync(executable, archive);
    Equal(1, listed.Entries);
    var tested = await operations.TestAsync(executable, archive);
    Check(tested.Ok, $"real test failed: {tested.Error}");
    var destination = Path.Combine(scope.Root, "real output");
    var extracted = await operations.ExtractAsync(executable, archive, destination, "replace");
    Check(extracted.Ok, $"real extract failed: {extracted.Error}");
    SequenceEqual(expected, await File.ReadAllBytesAsync(Path.Combine(destination, "folder", "file '[] & 雪.txt")));
    Console.WriteLine($"PROBE real_7zip executable={executable}");
}

static string? FindSevenZip()
{
    var candidates = new List<string>
    {
        @"C:\Program Files\7-Zip\7z.exe",
        @"C:\Program Files (x86)\7-Zip\7z.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "7-Zip", "7z.exe")
    };
    var path = Environment.GetEnvironmentVariable("PATH");
    if (!string.IsNullOrEmpty(path))
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(value => Path.Combine(value, "7z.exe")));
    return candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
}

static string Listing(params string[] entries) => string.Join("\n\n", entries) + "\n";
static string Entry(string text) => text + "\n";

static string DirectoryEntry(string path)
{
    var canonical = path.Replace('/', '\\').TrimEnd('\\') + "\\";
    return $"Path = {canonical}\nFolder = +\nSize = 0\nPacked Size = 0\nAttributes = D\nEncrypted = -";
}

static string FileEntry(string path, byte[] content, long packed)
{
    var canonical = path.Replace('/', '\\');
    return $"Path = {canonical}\nFolder = -\nSize = {content.Length.ToString(CultureInfo.InvariantCulture)}\nPacked Size = {packed.ToString(CultureInfo.InvariantCulture)}\nAttributes = A\nCRC = {Crc32(content)}\nEncrypted = -";
}

static string Crc32(ReadOnlySpan<byte> bytes)
{
    uint crc = uint.MaxValue;
    foreach (var value in bytes)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
    }
    return (~crc).ToString("X8", CultureInfo.InvariantCulture);
}

static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

static async Task ThrowsCodeAsync(string code, Func<Task> action)
{
    try { await action(); }
    catch (AgentOpsException ex) when (ex.Code == code) { return; }
    throw new InvalidOperationException($"Expected {code}.");
}

static async Task ThrowsAnyCodeAsync(IReadOnlyCollection<string> codes, Func<Task> action)
{
    try { await action(); }
    catch (AgentOpsException ex) when (codes.Contains(ex.Code, StringComparer.Ordinal)) { return; }
    throw new InvalidOperationException($"Expected one of: {string.Join(",", codes)}.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected [{expected}], actual [{actual}].");
}

static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException($"Sequences differ. Expected [{string.Join(",", expected)}], actual [{string.Join(",", actual)}].");
}

sealed class FakeRunner : ISevenZipProcessRunner
{
    public FakeRunner(string listing, IReadOnlyDictionary<string, byte[]>? content = null)
    {
        Listing = listing;
        Content = content ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);
    }

    public string Listing { get; }
    public IReadOnlyDictionary<string, byte[]> Content { get; }
    public List<SevenZipProcessRequest> Requests { get; } = [];
    public List<string> Calls { get; } = [];
    public int TestExitCode { get; init; }
    public bool TestTimedOut { get; init; }
    public bool TestStderrTruncated { get; init; }
    public bool ListStdoutTruncated { get; init; }
    public Action? OnRun { get; init; }

    public Task<SevenZipProcessResult> RunTextAsync(SevenZipProcessRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        OnRun?.Invoke();
        if (request.Arguments[0] == "l")
        {
            Calls.Add("l");
            var bytes = Encoding.UTF8.GetBytes(Listing);
            return Task.FromResult(new SevenZipProcessResult(0, bytes, string.Empty, bytes.Length,
                ListStdoutTruncated, false, false));
        }
        if (request.Arguments[0] == "t")
        {
            Calls.Add("t");
            return Task.FromResult(new SevenZipProcessResult(TestExitCode, [], string.Empty, 0,
                false, TestStderrTruncated, TestTimedOut));
        }
        throw new InvalidOperationException("Unexpected text request.");
    }

    public async Task<SevenZipProcessResult> RunToStreamAsync(
        SevenZipProcessRequest request,
        Stream stdoutDestination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        OnRun?.Invoke();
        if (request.Arguments[0] != "x") throw new InvalidOperationException("unexpected binary request");
        var entry = request.Arguments[^1];
        Calls.Add("x:" + entry);
        if (!Content.TryGetValue(entry, out var bytes)) throw new InvalidOperationException($"No fake content for {entry}.");
        var permitted = (int)Math.Min(bytes.Length, request.MaxStdoutBytes);
        if (permitted > 0) await stdoutDestination.WriteAsync(bytes.AsMemory(0, permitted), cancellationToken);
        return new(0, [], string.Empty, bytes.Length, bytes.Length > request.MaxStdoutBytes, false, false);
    }
}

sealed class TestScope : IDisposable
{
    public TestScope()
    {
        Root = Path.Combine(Path.GetTempPath(), "PegasusAgentOps-SevenZip-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string relative, byte[] content)
    {
        var path = Path.IsPathFullyQualified(relative) ? relative : Path.Combine(Root, relative);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    public string Directory(string relative)
    {
        var path = Path.IsPathFullyQualified(relative) ? relative : Path.Combine(Root, relative);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(Root)) DeleteTreeNoFollow(Root);
    }

    private static void DeleteTreeNoFollow(string path)
    {
        var attributes = System.IO.File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0) System.IO.Directory.Delete(path);
            else System.IO.File.Delete(path);
            return;
        }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            if ((attributes & FileAttributes.ReadOnly) != 0) System.IO.File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            System.IO.File.Delete(path);
            return;
        }
        foreach (var child in System.IO.Directory.EnumerateFileSystemEntries(path)) DeleteTreeNoFollow(child);
        System.IO.Directory.Delete(path);
    }
}

static class Junction
{
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const uint SetReparsePoint = 0x000900A4;
    private const uint MountPointTag = 0xA0000003;

    public static void Create(string link, string target)
    {
        var printName = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var substituteName = @"\??\" + printName;
        var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(printName);
        var pathBytes = new byte[substituteBytes.Length + 2 + printBytes.Length + 2];
        substituteBytes.CopyTo(pathBytes, 0);
        printBytes.CopyTo(pathBytes, substituteBytes.Length + 2);

        var buffer = new byte[16 + pathBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), MountPointTag);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), checked((ushort)(8 + pathBytes.Length)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)substituteBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), checked((ushort)(substituteBytes.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), checked((ushort)printBytes.Length));
        pathBytes.CopyTo(buffer, 16);

        using var handle = NativeMethods.CreateFile(
            link,
            GenericWrite,
            ShareReadWriteDelete,
            0,
            OpenExisting,
            OpenReparsePoint | BackupSemantics,
            0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "open junction fixture");
        if (!NativeMethods.DeviceIoControl(
                handle,
                SetReparsePoint,
                buffer,
                checked((uint)buffer.Length),
                0,
                0,
                out _,
                0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "set junction fixture");
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            nint securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            nint templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            byte[] input,
            uint inputLength,
            nint output,
            uint outputLength,
            out uint bytesReturned,
            nint overlapped);
    }
}
