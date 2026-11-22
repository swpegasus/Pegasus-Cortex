using System.Security.Cryptography;
using System.Text;
using Pegasus.AgentOps.Engine;

var tests = new (string Name, Action Run)[]
{
    ("bounded_read", BoundedRead),
    ("encoding_detection_byte_exact", EncodingDetection),
    ("bounded_numbered_line_context", NumberedLineContext),
    ("encoding_inspect_without_content", EncodingInspect),
    ("encoding_convert_atomic_exact_target", EncodingConvert),
    ("write_default_and_preserve", WriteAndPreserve),
    ("write_hash_preconditions_and_publication_race", WriteHashPreconditions),
    ("unknown_codepage_gate", UnknownCodePage),
    ("literal_replace_preconditions", LiteralReplace),
    ("line_range_replace_preserves_context", LineRangeReplace),
    ("strict_single_file_patch", StrictPatch),
    ("patch_path_and_fuzzy_rejection", PatchRejection),
    ("atomic_publish_failure_preserves_original", AtomicFailure),
    ("direct_ads_paths_rejected", AdsPaths),
    ("cas_preimage_change_preserves_newer_content", CasPreimage),
    ("parent_junction_swap_no_follow", ParentSwap)
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
        Console.WriteLine($"FAIL {test.Name} {ex}");
    }
}

Console.WriteLine($"RESULT total={tests.Length} passed={tests.Length - failed} failed={failed}");
return failed == 0 ? 0 : 1;

static void BoundedRead()
{
    using var temp = new TempRoot();
    var path = temp.File("bounded.txt", [0x31, 0x32, 0x33, 0x34]);
    ThrowsCode("invalid", () => ExtendedOperations.Read(path, maxBytes: 3));
    Equal("1234", Value<string>(ExtendedOperations.Read(path, maxBytes: 4), "text"));
}

static void EncodingDetection()
{
    using var temp = new TempRoot();
    var ascii = temp.File("ascii.txt", Encoding.ASCII.GetBytes("can't\r\n"));
    AssertRead(ascii, "ascii", false, "can't\r\n", "\r\n");

    const string unicode = "café Ω ‘quoted’";
    var utf8 = temp.File("utf8.txt", new UTF8Encoding(false, true).GetBytes(unicode));
    AssertRead(utf8, "utf8", false, unicode, null);

    var utf8Bom = temp.File("utf8-bom.txt", WithBom([0xEF, 0xBB, 0xBF], new UTF8Encoding(false, true).GetBytes(unicode)));
    AssertRead(utf8Bom, "utf8", true, unicode, null);

    var utf16Le = temp.File("utf16-le.txt", WithBom([0xFF, 0xFE], new UnicodeEncoding(false, false, true).GetBytes(unicode)));
    AssertRead(utf16Le, "utf16le", true, unicode, null);

    var utf16Be = temp.File("utf16-be.txt", WithBom([0xFE, 0xFF], new UnicodeEncoding(true, false, true).GetBytes(unicode)));
    AssertRead(utf16Be, "utf16be", true, unicode, null);
}

static void NumberedLineContext()
{
    using var temp = new TempRoot();
    var encoding = new UnicodeEncoding(false, false, true);
    var bytes = WithBom([0xFF, 0xFE], encoding.GetBytes("one\r\ntwo Ω\r\nthree\r\nfour\r\n"));
    var path = temp.File("context Ω.txt", bytes);
    var result = ExtendedOperations.ReadLines(path, 2, 2);
    Equal(2, Value<int>(result, "line"));
    Equal(2, Value<int>(result, "count"));
    Equal(4, Value<int>(result, "total"));
    Equal(4, Value<int>(result, "next"));
    Equal("utf16le", Value<string>(result, "enc"));
    Equal(true, Value<bool>(result, "bom"));
    Equal("\r\n", Value<string>(result, "newline"));
    Equal(Sha(bytes), Value<string>(result, "sha"));
    using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(result["entries"]));
    var entries = document.RootElement.EnumerateArray().ToArray();
    Equal(2, entries[0].GetProperty("line").GetInt32());
    Equal("two Ω", entries[0].GetProperty("text").GetString());
    Equal(3, entries[1].GetProperty("line").GetInt32());
    Equal("three", entries[1].GetProperty("text").GetString());
    ThrowsCode("precondition", () => ExtendedOperations.ReadLines(path, 5, 1));
    ThrowsCode("invalid", () => ExtendedOperations.ReadLines(path, 1, 513));
}

static void EncodingInspect()
{
    using var temp = new TempRoot();
    var bytes = WithBom([0xEF, 0xBB, 0xBF], new UTF8Encoding(false, true).GetBytes("one\ntwo Ω\n"));
    var path = temp.File("inspect.txt", bytes);
    var result = ExtendedOperations.InspectEncoding(path);
    Check(!result.ContainsKey("text"), "encoding inspection exposed content");
    Equal("utf8", Value<string>(result, "enc"));
    Equal(true, Value<bool>(result, "bom"));
    Equal("\n", Value<string>(result, "newline"));
    Equal(2, Value<int>(result, "lines"));
    Equal(Sha(bytes), Value<string>(result, "sha"));

    var legacy = temp.File("legacy-inspect.txt", [0x63, 0x61, 0x66, 0xE9]);
    ThrowsCode("encoding_required", () => ExtendedOperations.InspectEncoding(legacy));
    Equal("codepage:1252", Value<string>(ExtendedOperations.InspectEncoding(legacy, "codepage:1252"), "enc"));
}

static void EncodingConvert()
{
    using var temp = new TempRoot();
    var sourceBytes = WithBom([0xFF, 0xFE], new UnicodeEncoding(false, false, true).GetBytes("café Ω\r\n"));
    var source = temp.File("source.txt", sourceBytes);
    var destination = Path.Combine(temp.Path, "converted.txt");
    var result = ExtendedOperations.ConvertEncoding(source, destination, "utf8", Sha(sourceBytes));
    Equal("utf16le", Value<string>(result, "enc_before"));
    Equal("utf8", Value<string>(result, "enc"));
    BytesEqual(new UTF8Encoding(false, true).GetBytes("café Ω\r\n"), File.ReadAllBytes(destination));
    BytesEqual(sourceBytes, File.ReadAllBytes(source));

    ThrowsCode("collision", () => ExtendedOperations.ConvertEncoding(source, destination, "utf8"));
    BytesEqual(new UTF8Encoding(false, true).GetBytes("café Ω\r\n"), File.ReadAllBytes(destination));
    ThrowsCode("precondition", () => ExtendedOperations.ConvertEncoding(source, source, "utf8", new string('0', 64)));
    BytesEqual(sourceBytes, File.ReadAllBytes(source));

    ExtendedOperations.ConvertEncoding(source, source, "utf8bom", Sha(sourceBytes));
    var inPlace = File.ReadAllBytes(source);
    Check(inPlace.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "in-place conversion lost requested BOM");
    Equal("café Ω\r\n", new UTF8Encoding(false, true).GetString(inPlace, 3, inPlace.Length - 3));
}

static void WriteAndPreserve()
{
    using var temp = new TempRoot();
    const string unicode = "plain ‘text’ Ω e\u0301";
    var created = Path.Combine(temp.Path, "created.txt");
    ExtendedOperations.Write(created, unicode);
    BytesEqual(new UTF8Encoding(false, true).GetBytes(unicode), File.ReadAllBytes(created));

    var utf16 = new UnicodeEncoding(false, false, true);
    var existing = temp.File("existing.txt", WithBom([0xFF, 0xFE], utf16.GetBytes("old\r\ntext\r\n")));
    ExtendedOperations.Write(existing, "new\ncan't ‘change’\n");
    var preserved = File.ReadAllBytes(existing);
    Check(preserved.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }), "UTF-16LE BOM was not preserved");
    Equal("new\r\ncan't ‘change’\r\n", utf16.GetString(preserved, 2, preserved.Length - 2));

    ExtendedOperations.Write(existing, "explicit\nutf8", "utf8");
    var converted = File.ReadAllBytes(existing);
    Check(!converted.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "explicit UTF-8 unexpectedly retained a BOM");
    BytesEqual(new UTF8Encoding(false, true).GetBytes("explicit\nutf8"), converted);

    ExtendedOperations.Write(existing, "bom restored", "utf8bom");
    var withUtf8Bom = File.ReadAllBytes(existing);
    Check(withUtf8Bom.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "explicit UTF-8 BOM was not emitted");
    Equal("bom restored", new UTF8Encoding(false, true).GetString(withUtf8Bom, 3, withUtf8Bom.Length - 3));

    var utf16Be = new UnicodeEncoding(true, false, true);
    var bigEndian = temp.File("existing-be.txt", WithBom([0xFE, 0xFF], utf16Be.GetBytes("old\ntext\n")));
    ExtendedOperations.Write(bigEndian, "new\r\ntext\r\n");
    var preservedBigEndian = File.ReadAllBytes(bigEndian);
    Check(preservedBigEndian.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }), "UTF-16BE BOM was not preserved");
    Equal("new\ntext\n", utf16Be.GetString(preservedBigEndian, 2, preservedBigEndian.Length - 2));

    var ascii = temp.File("ascii-existing.txt", Encoding.ASCII.GetBytes("old"));
    var before = File.ReadAllBytes(ascii);
    ThrowsCode("encoding_required", () => ExtendedOperations.Write(ascii, "Ω"));
    BytesEqual(before, File.ReadAllBytes(ascii));
}

static void UnknownCodePage()
{
    using var temp = new TempRoot();
    var path = temp.File("legacy.txt", [0x63, 0x61, 0x66, 0xE9]);
    ThrowsCode("encoding_required", () => ExtendedOperations.Read(path));
    var read = ExtendedOperations.Read(path, "codepage:1252");
    Equal("café", Value<string>(read, "text"));
    Equal("codepage:1252", Value<string>(read, "enc"));
}

static void WriteHashPreconditions()
{
    using var temp = new TempRoot();
    var original = new UTF8Encoding(false).GetBytes("original 雪\r\n");
    var path = temp.File("write-preimage.txt", original);
    var before = Sha(original);
    ThrowsCode("precondition", () => ExtendedOperations.Write(path, "rejected", requestedEncoding: null, expectedSha: new string('0', 64)));
    BytesEqual(original, File.ReadAllBytes(path));
    var missing = Path.Combine(temp.Path, "missing.txt");
    ThrowsCode("not_found", () => ExtendedOperations.Write(missing, "rejected", requestedEncoding: null, expectedSha: Sha([])));
    Check(!File.Exists(missing), "A missing target was confused with an empty-file preimage.");
    var empty = temp.File("empty.txt", []);
    var written = ExtendedOperations.Write(empty, "new 雪", requestedEncoding: "utf8", expectedSha: Sha([]));
    Equal(Sha([]), Value<string>(written, "sha_before"));
    Equal("new 雪", File.ReadAllText(empty));

    var newer = new UTF8Encoding(false).GetBytes("newer independent 雪\r\n");
    try
    {
        ExtendedOperations.PublicationHookForTests = (point, target) =>
        {
            if (point != TextPublicationHookPoint.BeforePublicationLease) return;
            ExtendedOperations.PublicationHookForTests = null;
            File.WriteAllBytes(target, newer);
        };
        ThrowsCode("precondition", () => ExtendedOperations.Write(path, "stale candidate", requestedEncoding: null, expectedSha: before));
        BytesEqual(newer, File.ReadAllBytes(path));
    }
    finally { ExtendedOperations.PublicationHookForTests = null; }
}

static void LiteralReplace()
{
    using var temp = new TempRoot();
    var encoding = new UTF8Encoding(false, true);
    var original = WithBom([0xEF, 0xBB, 0xBF], encoding.GetBytes("can't\r\ncan't Ω\r\n"));
    var path = temp.File("replace.txt", original);
    var beforeSha = Sha(original);

    var receipt = ExtendedOperations.ReplaceLiteral(path, "can't", "won't ‘ever’", 2, beforeSha);
    Equal(2, Value<int>(receipt, "count"));
    var updated = File.ReadAllBytes(path);
    Check(updated.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "replace lost BOM");
    Equal("won't ‘ever’\r\nwon't ‘ever’ Ω\r\n", encoding.GetString(updated, 3, updated.Length - 3));

    var stable = File.ReadAllBytes(path);
    ThrowsCode("precondition", () => ExtendedOperations.ReplaceLiteral(path, "won't", "x", 1));
    BytesEqual(stable, File.ReadAllBytes(path));
    ThrowsCode("precondition", () => ExtendedOperations.ReplaceLiteral(path, "won't", "x", 2, new string('0', 64)));
    BytesEqual(stable, File.ReadAllBytes(path));
}

static void LineRangeReplace()
{
    using var temp = new TempRoot();
    var encoding = new UnicodeEncoding(false, false, true);
    var original = WithBom([0xFF, 0xFE], encoding.GetBytes("one\r\ntwo\r\nthree\r\nfour\r\n"));
    var path = temp.File("lines Ω.txt", original);
    var receipt = ExtendedOperations.ReplaceLines(path, 2, 2, "TWO Ω\nTHREE edited", Sha(original));
    Equal(2, Value<int>(receipt, "line"));
    Equal(2, Value<int>(receipt, "count"));
    Equal(4, Value<int>(receipt, "lines_before"));
    Equal(4, Value<int>(receipt, "lines_after"));
    var updated = File.ReadAllBytes(path);
    Check(updated.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }), "line edit lost UTF-16LE BOM");
    Equal("one\r\nTWO Ω\r\nTHREE edited\r\nfour\r\n", encoding.GetString(updated, 2, updated.Length - 2));

    var stable = File.ReadAllBytes(path);
    ThrowsCode("precondition", () => ExtendedOperations.ReplaceLines(path, 5, 1, "outside"));
    BytesEqual(stable, File.ReadAllBytes(path));
    ThrowsCode("precondition", () => ExtendedOperations.ReplaceLines(path, 1, 1, "changed", new string('0', 64)));
    BytesEqual(stable, File.ReadAllBytes(path));
}

static void StrictPatch()
{
    using var temp = new TempRoot();
    var workspace = temp.Dir("workspace");
    var folder = Directory.CreateDirectory(Path.Combine(workspace, "folder")).FullName;
    var path = Path.Combine(folder, "agent's Ω.txt");
    var encoding = new UTF8Encoding(false, true);
    File.WriteAllBytes(path, WithBom([0xEF, 0xBB, 0xBF], encoding.GetBytes("one\r\ntwo\r\nthree\r\n")));
    var patch = """
        --- a/folder/agent's Ω.txt
        +++ b/folder/agent's Ω.txt
        @@ -1,3 +1,3 @@
         one
        -two
        +TWO ‘exact’
         three
        """;

    var receipt = ExtendedOperations.ApplyUnifiedPatch(workspace, patch);
    Equal(1, Value<int>(receipt, "hunks"));
    Equal(false, Value<bool>(receipt, "partial"));
    var updated = File.ReadAllBytes(path);
    Check(updated.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "patch lost BOM");
    Equal("one\r\nTWO ‘exact’\r\nthree\r\n", encoding.GetString(updated, 3, updated.Length - 3));

    var insertion = """
        --- a/folder/agent's Ω.txt
        +++ b/folder/agent's Ω.txt
        @@ -1,0 +2,1 @@
        +inserted
        """;
    ExtendedOperations.ApplyUnifiedPatch(workspace, insertion);
    var inserted = File.ReadAllBytes(path);
    Equal("one\r\ninserted\r\nTWO ‘exact’\r\nthree\r\n", encoding.GetString(inserted, 3, inserted.Length - 3));
}

static void PatchRejection()
{
    using var temp = new TempRoot();
    var workspace = temp.Dir("workspace");
    var path = Path.Combine(workspace, "file.txt");
    File.WriteAllText(path, "one\r\ntwo\r\nthree\r\n", new UTF8Encoding(false));
    var original = File.ReadAllBytes(path);

    var absolute = """
        --- C:\outside.txt
        +++ C:\outside.txt
        @@ -1 +1 @@
        -old
        +new
        """;
    ThrowsCode("precondition", () => ExtendedOperations.ApplyUnifiedPatch(workspace, absolute));
    BytesEqual(original, File.ReadAllBytes(path));

    var traversal = """
        --- a/../outside.txt
        +++ b/../outside.txt
        @@ -1 +1 @@
        -old
        +new
        """;
    ThrowsCode("precondition", () => ExtendedOperations.ApplyUnifiedPatch(workspace, traversal));
    BytesEqual(original, File.ReadAllBytes(path));

    var alternateStream = """
        --- a/file.txt:secret
        +++ b/file.txt:secret
        @@ -1 +1 @@
        -old
        +new
        """;
    ThrowsCode("precondition", () => ExtendedOperations.ApplyUnifiedPatch(workspace, alternateStream));
    BytesEqual(original, File.ReadAllBytes(path));

    var fuzzy = """
        --- a/file.txt
        +++ b/file.txt
        @@ -2,2 +2,2 @@
         one
        -two
        +changed
        """;
    ThrowsCode("precondition", () => ExtendedOperations.ApplyUnifiedPatch(workspace, fuzzy));
    BytesEqual(original, File.ReadAllBytes(path));

    var lateMismatch = """
        --- a/file.txt
        +++ b/file.txt
        @@ -1 +1 @@
        -one
        +ONE
        @@ -3 +3 @@
        -not-three
        +THREE
        """;
    ThrowsCode("precondition", () => ExtendedOperations.ApplyUnifiedPatch(workspace, lateMismatch));
    BytesEqual(original, File.ReadAllBytes(path));

    var multiFile = """
        --- a/file.txt
        +++ b/file.txt
        @@ -1 +1 @@
        -one
        +ONE
        --- a/second.txt
        +++ b/second.txt
        @@ -1 +1 @@
        -x
        +y
        """;
    ThrowsCode("invalid", () => ExtendedOperations.ApplyUnifiedPatch(workspace, multiFile));
    BytesEqual(original, File.ReadAllBytes(path));
}

static void AtomicFailure()
{
    using var temp = new TempRoot();
    var path = temp.File("locked.txt", Encoding.UTF8.GetBytes("original"));
    var original = File.ReadAllBytes(path);
    using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        ThrowsAny<IOException, UnauthorizedAccessException>(() => ExtendedOperations.Write(path, "replacement"));
    }

    BytesEqual(original, File.ReadAllBytes(path));
    Check(!Directory.EnumerateFiles(temp.Path, ".locked.txt.pao-*.tmp").Any(), "failed atomic write retained a temp file");
}

static void AdsPaths()
{
    using var temp = new TempRoot();
    var path = temp.File("ads.txt", Encoding.UTF8.GetBytes("safe"));
    var ads = path + ":stream";
    ThrowsCode("precondition", () => ExtendedOperations.Read(ads));
    ThrowsCode("precondition", () => ExtendedOperations.Write(ads, "unsafe"));
    ThrowsCode("precondition", () => ExtendedOperations.ReplaceLiteral(ads, "safe", "unsafe", 1));
    Equal("safe", File.ReadAllText(path));
}

static void CasPreimage()
{
    using var temp = new TempRoot();
    var original = new UTF8Encoding(false).GetBytes("old old\r\n");
    var path = temp.File("identity.txt", original);
    var displaced = path + ".displaced";
    try
    {
        ExtendedOperations.PublicationHookForTests = (point, target) =>
        {
            if (point != TextPublicationHookPoint.AfterPublicationLease) return;
            ExtendedOperations.PublicationHookForTests = null;
            File.Move(target, displaced);
            File.WriteAllBytes(target, original);
        };
        ThrowsCode("precondition", () => ExtendedOperations.ReplaceLiteral(path, "old", "new", 2));
        BytesEqual(original, File.ReadAllBytes(path));
        BytesEqual(original, File.ReadAllBytes(displaced));
    }
    finally
    {
        ExtendedOperations.PublicationHookForTests = null;
    }

    var workspace = temp.Dir("cas-workspace");
    var patchTarget = Path.Combine(workspace, "patch.txt");
    File.WriteAllText(patchTarget, "one\r\ntwo\r\n", new UTF8Encoding(false));
    const string patch = """
        --- a/patch.txt
        +++ b/patch.txt
        @@ -1,2 +1,2 @@
         one
        -two
        +TWO
        """;
    try
    {
        ExtendedOperations.PublicationHookForTests = (point, target) =>
        {
            if (point != TextPublicationHookPoint.AfterPublicationLease) return;
            ExtendedOperations.PublicationHookForTests = null;
            File.WriteAllText(target, "newer\r\ncontent\r\n", new UTF8Encoding(false));
        };
        ThrowsCode("precondition", () => ExtendedOperations.ApplyUnifiedPatch(workspace, patch));
        Equal("newer\r\ncontent\r\n", File.ReadAllText(patchTarget));
    }
    finally
    {
        ExtendedOperations.PublicationHookForTests = null;
    }
}

static void ParentSwap()
{
    using var temp = new TempRoot();
    var workspace = temp.Dir("swap-workspace");
    var parent = Directory.CreateDirectory(Path.Combine(workspace, "parent")).FullName;
    var originalTarget = Path.Combine(parent, "file.txt");
    File.WriteAllText(originalTarget, "original", new UTF8Encoding(false));
    var displacedParent = Path.Combine(workspace, "parent-original");
    var external = temp.Dir("external");
    var externalTarget = Path.Combine(external, "file.txt");
    File.WriteAllText(externalTarget, "external-safe", new UTF8Encoding(false));
    var junctionCreated = false;
    var swapBlocked = false;
    AgentOpsException? publicationFailure = null;
    try
    {
        ExtendedOperations.PublicationHookForTests = (point, _) =>
        {
            if (point != TextPublicationHookPoint.AfterPublicationLease) return;
            ExtendedOperations.PublicationHookForTests = null;
            try
            {
                Directory.Move(parent, displacedParent);
                CreateJunction(parent, external);
                junctionCreated = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                swapBlocked = true;
            }
        };
        try
        {
            ExtendedOperations.ReplaceLiteral(originalTarget, "original", "replaced", 1);
        }
        catch (AgentOpsException ex)
        {
            publicationFailure = ex;
        }

        Equal("external-safe", File.ReadAllText(externalTarget));
        if (swapBlocked)
        {
            Check(publicationFailure is null, $"publication failed after the parent swap was blocked; code={publicationFailure?.Code}; parent_moved={Directory.Exists(displacedParent)}");
            Equal("replaced", File.ReadAllText(originalTarget));
        }
        else
        {
            Check(junctionCreated, "parent move unexpectedly succeeded without creating the test junction");
            Equal("precondition", publicationFailure?.Code);
            Equal("original", File.ReadAllText(Path.Combine(displacedParent, "file.txt")));
        }
    }
    finally
    {
        ExtendedOperations.PublicationHookForTests = null;
        if (junctionCreated && Directory.Exists(parent)) Directory.Delete(parent);
        if (Directory.Exists(displacedParent) && !Directory.Exists(parent)) Directory.Move(displacedParent, parent);
    }
}

static void CreateJunction(string path, string target)
{
    var start = new System.Diagnostics.ProcessStartInfo(ResolvePowerShell())
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    SetMinimumEnvironment(start);
    start.ArgumentList.Add("-NoProfile");
    start.ArgumentList.Add("-NonInteractive");
    start.ArgumentList.Add("-File");
    start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "New-TestJunction.ps1"));
    start.ArgumentList.Add(path);
    start.ArgumentList.Add(target);
    using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("junction process did not start");
    process.WaitForExit(30_000);
    if (!process.HasExited)
    {
        process.Kill(entireProcessTree: true);
        throw new InvalidOperationException("junction process timed out");
    }
    var error = process.StandardError.ReadToEnd();
    if (process.ExitCode != 0) throw new InvalidOperationException($"junction creation failed: {error}");
}

static string ResolvePowerShell()
{
    // Registered dotnet runs intentionally have a bounded PATH. Reuse the
    // product's canonical machine PowerShell discovery for this Windows fixture.
    return PowerShell7ToolBootstrap.ResolveExecutable();
}

static void SetMinimumEnvironment(System.Diagnostics.ProcessStartInfo start)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
        if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value) values[key] = value;
    start.Environment.Clear();
    foreach (var item in values) start.Environment[item.Key] = item.Value;
}

static void AssertRead(string path, string enc, bool bom, string text, string? newline)
{
    var result = ExtendedOperations.Read(path);
    Equal(enc, Value<string>(result, "enc"));
    Equal(bom, Value<bool>(result, "bom"));
    Equal(text, Value<string>(result, "text"));
    Equal(newline, result["newline"] as string);
}

static T Value<T>(IReadOnlyDictionary<string, object?> data, string key)
{
    if (!data.TryGetValue(key, out var value) || value is not T typed)
        throw new InvalidOperationException($"{key} was not {typeof(T).Name}");
    return typed;
}

static byte[] WithBom(byte[] bom, byte[] content)
{
    var bytes = new byte[bom.Length + content.Length];
    bom.CopyTo(bytes, 0);
    content.CopyTo(bytes, bom.Length);
    return bytes;
}

static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static void BytesEqual(byte[] expected, byte[] actual)
{
    if (!expected.AsSpan().SequenceEqual(actual))
        throw new InvalidOperationException($"byte mismatch expected={Convert.ToHexString(expected)} actual={Convert.ToHexString(actual)}");
}

static void ThrowsCode(string code, Action action)
{
    try
    {
        action();
        throw new InvalidOperationException($"expected {code}");
    }
    catch (AgentOpsException ex) when (ex.Code == code)
    {
    }
}

static void ThrowsAny<TFirst, TSecond>(Action action)
    where TFirst : Exception
    where TSecond : Exception
{
    try
    {
        action();
        throw new InvalidOperationException($"expected {typeof(TFirst).Name} or {typeof(TSecond).Name}");
    }
    catch (Exception ex) when (ex is TFirst or TSecond)
    {
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected={expected} actual={actual}");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pao-text-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Dir(string name) => Directory.CreateDirectory(System.IO.Path.Combine(Path, name)).FullName;

    public string File(string name, byte[] bytes)
    {
        var path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
