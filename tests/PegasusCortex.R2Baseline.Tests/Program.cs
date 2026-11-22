using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Contracts.R2;
using Pegasus.Cortex.Persistence.Sqlite.R2;

// Explicit synthetic-only executable. No default product root, store discovery, reset or recursive cleanup.
if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("UNAVAILABLE: Windows handle-custody acceptance requires Windows.");
    return 2;
}
if (args is not ["--fixture-root", var requestedRoot])
{
    Console.Error.WriteLine("Usage: PegasusCortex.R2Baseline.Tests --fixture-root <new-absolute-local-directory>");
    return 2;
}
if (!Path.IsPathFullyQualified(requestedRoot) || requestedRoot.StartsWith(@"\\", StringComparison.Ordinal) ||
    requestedRoot.StartsWith("//", StringComparison.Ordinal) ||
    Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedRoot)) != requestedRoot || requestedRoot.Length > 150)
{
    Console.Error.WriteLine("Refused: fixture root must be new, canonical, local and bounded.");
    return 2;
}
var fixtureDriveType = new DriveInfo(Path.GetPathRoot(requestedRoot)!).DriveType;
if (fixtureDriveType is DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown ||
    Directory.Exists(requestedRoot) || File.Exists(requestedRoot))
{
    Console.Error.WriteLine("Refused: fixture root must be new and on admitted local storage.");
    return 2;
}
for (var ancestor = new DirectoryInfo(Path.GetDirectoryName(requestedRoot)!); ancestor is not null; ancestor = ancestor.Parent)
    if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new IOException("Synthetic fixtures cannot cross a reparse point.");
Directory.CreateDirectory(requestedRoot);
using var fixtureClaim = new FileStream(Path.Combine(requestedRoot, ".r2-test-owner"),
    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

var cases = new (string Name, Func<Task> Run)[]
{
    ("fresh schema, persisted identity and no semantic population", FreshAsync),
    ("verified reopen retains identity and denies write/delete custody", ReopenAsync),
    ("wrong instance and unsupported identity refuse without writes", WrongIdentityAsync),
    ("changed, hidden-name and populated schemas refuse without writes", SchemaRefusalsAsync),
    ("pre-existing names/nonempty directories are never adopted", ExistingTargetsAsync),
    ("missing reopen never initializes and unsafe paths refuse", InvalidPathsAsync),
    ("stable typed references bind exact store and owner membership", ReferencesAsync),
    ("projectless chat/task references and actual schema membership are independent", ProjectlessMembershipAsync),
    ("pre-canceled calls refuse without storage effects", CancellationAsync),
    ("WAL-mode and unresolved-journal input refuse without side effects", WalRefusalAsync),
    ("concurrent fresh creators publish exactly one generation", ConcurrentCreationAsync),
    ("hard-linked database refuses before SQLite admission", HardLinkAsync),
    ("reparse directory refuses without following it", ReparseAsync)
};
var passed = 0; var skipped = 0; var failed = 0;
foreach (var test in cases)
{
    try { await test.Run(); passed++; Console.WriteLine("PASS " + test.Name); }
    catch (FixtureUnavailable exception) { skipped++; Console.WriteLine("SKIP " + test.Name + ": " + exception.Message); }
    catch (Exception exception) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + exception); }
}
Console.WriteLine($"RESULT passed={passed} skipped={skipped} failed={failed}; evidence=synthetic-sqlite-and-windows-files");
Console.WriteLine("Fixtures retained only beneath the explicitly supplied new root: " + requestedRoot);
return failed != 0 ? 1 : skipped != 0 ? 2 : 0;

string NewRoot(string name)
{
    var path = Path.Combine(requestedRoot, name + "-" + Guid.NewGuid().ToString("N")[..8]);
    if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Synthetic fixture collision.");
    Directory.CreateDirectory(path);
    return path;
}

async Task<Fixture> CreatedAsync(string name)
{
    var root = NewRoot(name);
    await using var store = await SqliteFreshSemanticStore.CreateNewAsync(root);
    return new(root, store.DatabasePath, store.Baseline);
}

async Task FreshAsync()
{
    var root = NewRoot("fresh");
    await using var store = await SqliteFreshSemanticStore.CreateNewAsync(root);
    var snapshot = await store.InspectAsync();
    Check(snapshot.IsEmpty && snapshot.Baseline == store.Baseline, "Fresh schema contains unexpected identities.");
    Check(store.Baseline.Contract == CortexSemanticBaselineV1.ContractName &&
        store.Baseline.SchemaVersion == 1 && store.Baseline.StoreInstanceId != Guid.Empty, "Missing persisted baseline identity.");
    Check(Path.GetFileName(store.DatabasePath) == "cortex-r2.db", "The fresh store selected another filename.");
    Check(Entries(root).SequenceEqual(["cortex-r2.db"]), "Fresh publication leaked staging/claim/sidecar files.");
}

async Task ReopenAsync()
{
    var fixture = await CreatedAsync("reopen");
    var original = Hash(fixture.Path);
    await using (var store = await SqliteFreshSemanticStore.OpenVerifiedAsync(fixture.Path, fixture.Baseline))
    {
        Check((await store.InspectAsync()).Baseline == fixture.Baseline, "Reopen replaced the store instance identity.");
        Reject<IOException>(() => { using var writer = new FileStream(fixture.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        Reject<IOException>(() => File.Delete(fixture.Path));
        Reject<IOException>(() => File.Move(fixture.Path, fixture.Path + ".moved"));
    }
    Check(Hash(fixture.Path) == original && Entries(fixture.Root).SequenceEqual(["cortex-r2.db"]),
        "Read-only admission changed database bytes or created files.");
}

async Task WrongIdentityAsync()
{
    var fixture = await CreatedAsync("wrong-instance");
    var before = Hash(fixture.Path);
    await RejectStoreAsync<InvalidDataException>(() => SqliteFreshSemanticStore.OpenVerifiedAsync(
        fixture.Path, fixture.Baseline with { StoreInstanceId = Guid.NewGuid() }));
    await RejectStoreAsync<ArgumentException>(() => SqliteFreshSemanticStore.OpenVerifiedAsync(
        fixture.Path, fixture.Baseline with { SchemaVersion = 99 }));
    await RejectStoreAsync<InvalidDataException>(() => SqliteFreshSemanticStore.OpenVerifiedAsync(
        fixture.Path, fixture.Baseline with { SchemaSha256 = new string('0', 64) }));
    Check(Hash(fixture.Path) == before, "An identity refusal wrote to the file.");
}

async Task SchemaRefusalsAsync()
{
    foreach (var (name, sql) in new[]
    {
        ("version", "PRAGMA user_version=99;"),
        ("generation", "UPDATE cortex_r2_meta SET schema_generation='unsupported';"),
        ("column", "ALTER TABLE cortex_r2_projects ADD COLUMN unexpected TEXT;"),
        ("hidden-name", "CREATE TABLE sqliteX_payload (content TEXT);"),
        ("extra-index", "CREATE INDEX extra_index ON cortex_r2_projects(project_id);"),
        ("populated", "INSERT INTO cortex_r2_projects(project_id) VALUES ('synthetic-project');")
    })
    {
        var fixture = await CreatedAsync(name);
        await MutateFixtureAsync(fixture, sql);
        var before = Hash(fixture.Path);
        var entries = Entries(fixture.Root);
        await RejectStoreAsync<InvalidDataException>(() =>
            SqliteFreshSemanticStore.OpenVerifiedAsync(fixture.Path, fixture.Baseline));
        Check(Hash(fixture.Path) == before && Entries(fixture.Root).SequenceEqual(entries),
            "An unsupported-schema refusal changed bytes or sidecars.");
    }
}

async Task ExistingTargetsAsync()
{
    foreach (var name in new[] { "cortex.db", "cortex-v2.db", "cortex-r2.db", "keep.txt" })
    {
        var root = NewRoot("sentinel");
        var path = Path.Combine(root, name);
        var bytes = System.Text.Encoding.UTF8.GetBytes("SYNTHETIC ONLY - existing bytes must remain exact\r\n");
        await File.WriteAllBytesAsync(path, bytes);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await RejectStoreAsync<Exception>(() => SqliteFreshSemanticStore.CreateNewAsync(root),
                exception => exception is ArgumentException or IOException);
        }
        Check(File.ReadAllBytes(path).SequenceEqual(bytes) && Entries(root).SequenceEqual([name]),
            "Fresh creation adopted, read through, changed or removed a held sentinel.");
    }
    var fixture = await CreatedAsync("repeat");
    var before = Hash(fixture.Path);
    await RejectStoreAsync<ArgumentException>(() => SqliteFreshSemanticStore.CreateNewAsync(fixture.Root));
    Check(Hash(fixture.Path) == before, "Repeat creation replaced a completed generation.");
}

async Task InvalidPathsAsync()
{
    var fixture = await CreatedAsync("path-reference");
    var empty = NewRoot("missing");
    await RejectStoreAsync<ArgumentException>(() => SqliteFreshSemanticStore.OpenVerifiedAsync(
        Path.Combine(empty, "cortex-r2.db"), fixture.Baseline));
    Check(Entries(empty).Length == 0, "A missing reopen initialized a database.");
    await RejectStoreAsync<ArgumentException>(() => SqliteFreshSemanticStore.OpenVerifiedAsync(
        Path.Combine(empty, "cortex-v2.db"), fixture.Baseline));
    await RejectStoreAsync<ArgumentException>(() => SqliteFreshSemanticStore.CreateNewAsync("relative"));
    // These values must be rejected by string/local-path admission, never contacted as network roots.
    await RejectStoreAsync<ArgumentException>(() => SqliteFreshSemanticStore.CreateNewAsync(@"\\invalid.example\share\root"));
    await RejectStoreAsync<ArgumentException>(() => SqliteFreshSemanticStore.OpenVerifiedAsync(
        @"\\invalid.example\share\cortex-r2.db", fixture.Baseline));
    await RejectStoreAsync<ArgumentException>(() => SqliteFreshSemanticStore.CreateNewAsync(empty + "."));
    Check(Entries(empty).Length == 0, "An invalid-path call created files.");
}

async Task ReferencesAsync()
{
    var first = await CreatedAsync("references-a");
    var second = await CreatedAsync("references-b");
    Check(first.Baseline.StoreInstanceId != second.Baseline.StoreInstanceId, "Separate stores aliased instance identities.");
    var owner = new CortexSemanticOwnerV1("project-exact", "chat-exact", "task-exact");
    var reference = new CortexSemanticRecordReferenceV1(first.Baseline.StoreInstanceId,
        "project.synthetic", CortexSemanticRecordKindV1.Task, "task-exact", 1, owner);
    reference.RequireMatches(first.Baseline, owner);
    Check(reference.RecordId == "task-exact" && reference.Owner.ProjectId == "project-exact",
        "A caller-provided stable ID changed.");
    Reject<InvalidOperationException>(() => reference.RequireMatches(second.Baseline, owner));
    Reject<InvalidOperationException>(() => reference.RequireMatches(first.Baseline, owner with { ChatId = "another-chat" }));
    Reject<ArgumentException>(() => (reference with { RecordId = "another-task" }).Validate());
    Reject<ArgumentException>(() => (reference with { Revision = 0 }).Validate());
    Reject<ArgumentException>(() => new CortexSemanticOwnerV1(null, "invalid chat").Validate());
    Reject<ArgumentException>(() => (reference with { Namespace = "not a stable ID" }).Validate());
}

async Task ProjectlessMembershipAsync()
{
    var fixture = await CreatedAsync("projectless");
    var other = await CreatedAsync("projectless-other-store");
    var owner = new CortexSemanticOwnerV1(null, "chat-projectless", "task-projectless");
    var reference = new CortexSemanticRecordReferenceV1(fixture.Baseline.StoreInstanceId,
        "chat.synthetic", CortexSemanticRecordKindV1.Task, "task-projectless", 1, owner);
    reference.RequireMatches(fixture.Baseline, owner);
    Check(reference.Owner.ProjectId is null && reference.Owner.ChatId == "chat-projectless" &&
        reference.Owner.TaskId == reference.RecordId, "Projectless reference invented or replaced an identity.");
    var independentTask = new CortexSemanticOwnerV1(null, null, "task-standalone");
    (reference with { RecordId = "task-standalone", Owner = independentTask })
        .RequireMatches(fixture.Baseline, independentTask);
    Reject<InvalidOperationException>(() => reference.RequireMatches(other.Baseline, owner));
    Reject<InvalidOperationException>(() =>
        reference.RequireMatches(fixture.Baseline, owner with { ChatId = "another-chat" }));
    Reject<ArgumentException>(() => (reference with { RecordId = "another-task" }).Validate());
    Reject<ArgumentException>(() => (owner with { TaskId = "invalid task" }).Validate());
    Reject<ArgumentException>(() => (owner with { ProjectId = "invalid project" }).Validate());

    // Fixture-only SQL proves the real fresh DDL permits independent optional membership.
    // This adds no public semantic writer; ordinary phase-one reopen still requires empty tables.
    await using var connection = await OpenFixtureAsync(fixture);
    using (var insert = connection.CreateCommand())
    {
        insert.CommandText = """
            INSERT INTO cortex_r2_chats(chat_id, project_id) VALUES ('chat-projectless', NULL);
            INSERT INTO cortex_r2_tasks(task_id, project_id, chat_id)
                VALUES ('task-projectless', NULL, 'chat-projectless'), ('task-standalone', NULL, NULL);
            INSERT INTO cortex_r2_record_identities
                (namespace, record_kind, record_id, project_id, chat_id, task_id)
            VALUES
                ('chat.synthetic', 'task', 'task-projectless', NULL, 'chat-projectless', 'task-projectless'),
                ('task.synthetic', 'task', 'task-standalone', NULL, NULL, 'task-standalone'),
                ('knowledge.shared', 'knowledge', 'fact-projectless', NULL, 'chat-projectless', 'task-projectless');
            """;
        await insert.ExecuteNonQueryAsync();
    }
    using (var inspect = connection.CreateCommand())
    {
        inspect.CommandText = """
            SELECT
                (SELECT count(*) FROM cortex_r2_projects),
                (SELECT count(*) FROM cortex_r2_chats WHERE project_id IS NULL AND chat_id='chat-projectless'),
                (SELECT count(*) FROM cortex_r2_tasks WHERE project_id IS NULL),
                (SELECT count(*) FROM cortex_r2_tasks WHERE task_id='task-standalone' AND chat_id IS NULL),
                (SELECT count(*) FROM cortex_r2_record_identities WHERE project_id IS NULL);
            """;
        using var reader = await inspect.ExecuteReaderAsync();
        Check(await reader.ReadAsync() && reader.GetInt64(0) == 0 && reader.GetInt64(1) == 1 &&
            reader.GetInt64(2) == 2 && reader.GetInt64(3) == 1 && reader.GetInt64(4) == 3,
            "The actual schema required a project or lost projectless membership.");
    }
    foreach (var invalid in new[]
    {
        "INSERT INTO cortex_r2_tasks(task_id, chat_id) VALUES ('bad-chat-task', 'missing-chat');",
        "INSERT INTO cortex_r2_record_identities(namespace, record_kind, record_id, chat_id) VALUES ('chat.synthetic', 'knowledge', 'bad-chat-ref', 'missing-chat');",
        "INSERT INTO cortex_r2_record_identities(namespace, record_kind, record_id, task_id) VALUES ('chat.synthetic', 'knowledge', 'bad-task-ref', 'missing-task');",
        "INSERT INTO cortex_r2_record_identities(namespace, record_kind, record_id, task_id) VALUES ('chat.synthetic', 'task', 'wrong-task-id', 'task-projectless');"
    })
    {
        using var rejected = connection.CreateCommand();
        rejected.CommandText = invalid;
        try { await rejected.ExecuteNonQueryAsync(); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19) { continue; }
        throw new InvalidOperationException("The schema accepted a missing membership target or mismatched task identity.");
    }
}

async Task CancellationAsync()
{
    var root = NewRoot("cancel-create");
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    await RejectStoreAsync<OperationCanceledException>(() => SqliteFreshSemanticStore.CreateNewAsync(root, canceled.Token));
    Check(Entries(root).Length == 0, "Pre-publication cancellation created files.");
    var fixture = await CreatedAsync("cancel-reopen");
    var before = Hash(fixture.Path);
    await RejectStoreAsync<OperationCanceledException>(() =>
        SqliteFreshSemanticStore.OpenVerifiedAsync(fixture.Path, fixture.Baseline, canceled.Token));
    Check(Hash(fixture.Path) == before, "Canceled reopen wrote to the file.");
}

async Task WalRefusalAsync()
{
    var fixture = await CreatedAsync("wal-mode");
    await MutateFixtureAsync(fixture, "PRAGMA journal_mode=WAL;");
    var header = File.ReadAllBytes(fixture.Path);
    Check(header[18] == 2 && header[19] == 2, "Fixture did not actually persist WAL mode.");
    Check(Entries(fixture.Root).SequenceEqual(["cortex-r2.db"]),
        "WAL fixture must start with no retained sidecars after its setup connection closes.");
    var before = Hash(fixture.Path);
    await RejectStoreAsync<InvalidDataException>(() =>
        SqliteFreshSemanticStore.OpenVerifiedAsync(fixture.Path, fixture.Baseline));
    Check(Hash(fixture.Path) == before && Entries(fixture.Root).SequenceEqual(["cortex-r2.db"]),
        "WAL refusal created WAL/SHM or changed the synthetic database.");

    var journalFixture = await CreatedAsync("unresolved-journal");
    var journal = journalFixture.Path + "-journal";
    var evidence = System.Text.Encoding.UTF8.GetBytes("synthetic unresolved journal; preserve exact bytes");
    await File.WriteAllBytesAsync(journal, evidence);
    var databaseBefore = Hash(journalFixture.Path);
    await RejectStoreAsync<IOException>(() =>
        SqliteFreshSemanticStore.OpenVerifiedAsync(journalFixture.Path, journalFixture.Baseline));
    Check(Hash(journalFixture.Path) == databaseBefore && File.ReadAllBytes(journal).SequenceEqual(evidence),
        "Journal refusal repaired or deleted evidence.");
}

async Task ConcurrentCreationAsync()
{
    var root = NewRoot("concurrent");
    async Task<(SqliteFreshSemanticStore? Store, Exception? Failure)> AttemptAsync()
    {
        try { return (await SqliteFreshSemanticStore.CreateNewAsync(root), null); }
        catch (Exception error) when (error is ArgumentException or IOException or Win32Exception) { return (null, error); }
    }
    var outcomes = await Task.WhenAll(Task.Run(AttemptAsync), Task.Run(AttemptAsync));
    var winners = outcomes.Where(item => item.Store is not null).ToArray();
    try
    {
        Check(winners.Length == 1 && outcomes.Count(item => item.Failure is not null) == 1,
            "Concurrent creation did not publish exactly one generation.");
        Check(Entries(root).SequenceEqual(["cortex-r2.db"]), "Concurrent creation leaked or adopted staging.");
        Check((await winners[0].Store!.InspectAsync()).IsEmpty, "Concurrent winner is not a valid fresh baseline.");
    }
    finally
    {
        foreach (var winner in winners) await winner.Store!.DisposeAsync();
    }
}

async Task HardLinkAsync()
{
    var fixture = await CreatedAsync("hardlink-original");
    var linkedRoot = NewRoot("hardlink-target");
    var linkedPath = Path.Combine(linkedRoot, "cortex-r2.db");
    if (!FixtureNative.CreateHardLink(linkedPath, fixture.Path, IntPtr.Zero))
    {
        var code = Marshal.GetLastWin32Error();
        if (code is 1 or 50) throw new FixtureUnavailable("Filesystem hard links unavailable; code=" + code);
        throw new Win32Exception(code, "Synthetic hard-link fixture creation failed.");
    }
    var before = Hash(linkedPath);
    await RejectStoreAsync<IOException>(() =>
        SqliteFreshSemanticStore.OpenVerifiedAsync(linkedPath, fixture.Baseline));
    Check(Hash(linkedPath) == before && Hash(fixture.Path) == before, "Hard-link refusal changed either name.");
}

async Task ReparseAsync()
{
    var parent = NewRoot("reparse-parent");
    var target = NewRoot("reparse-target");
    var link = Path.Combine(parent, "link");
    FixtureNative.CreateDirectoryJunction(requestedRoot, link, target);
    Check((File.GetAttributes(link) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) ==
        (FileAttributes.Directory | FileAttributes.ReparsePoint), "Junction fixture is not a real reparse directory.");
    var resolved = Directory.ResolveLinkTarget(link, returnFinalTarget: false);
    Check(resolved is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved.FullName)), target,
            StringComparison.OrdinalIgnoreCase), "Junction did not resolve directly to its exact owned target.");
    Check(Entries(target).Length == 0, "Junction fixture target was not initially empty.");
    Console.WriteLine("INFO reparse fixture=directory-junction, tag=0xA0000003; symbolic-link-specific behavior unqualified.");
    await RejectStoreAsync<IOException>(() => SqliteFreshSemanticStore.CreateNewAsync(link));
    Check(Entries(target).Length == 0, "Reparse rejection followed the target and wrote files.");
}

async Task MutateFixtureAsync(Fixture fixture, string sql)
{
    await using var connection = await OpenFixtureAsync(fixture);
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}

async Task<SqliteConnection> OpenFixtureAsync(Fixture fixture)
{
    var prefix = requestedRoot + Path.DirectorySeparatorChar;
    if (!fixture.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Raw fixture access escaped the newly created synthetic root.");
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = fixture.Path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5
    }.ToString());
    try
    {
        await connection.OpenAsync();
        using var admission = connection.CreateCommand();
        admission.CommandText = "PRAGMA foreign_keys=ON;";
        await admission.ExecuteNonQueryAsync();
        return connection;
    }
    catch { await connection.DisposeAsync(); throw; }
}

static string[] Entries(string root) => Directory.EnumerateFileSystemEntries(root)
    .Select(Path.GetFileName).Select(name => name!).Order(StringComparer.Ordinal).ToArray();
static string Hash(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static void Reject<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected refusal: " + typeof(T).Name);
}
static async Task RejectStoreAsync<T>(Func<Task<SqliteFreshSemanticStore>> action,
    Func<Exception, bool>? qualifies = null) where T : Exception
{
    try { await using var unexpected = await action(); }
    catch (T error) when (qualifies is null || qualifies(error)) { return; }
    throw new InvalidOperationException("Expected store refusal: " + typeof(T).Name);
}

sealed record Fixture(string Root, string Path, CortexSemanticBaselineV1 Baseline);
sealed class FixtureUnavailable(string message) : Exception(message) { }
static class FixtureNative
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateHardLink(string newName, string existingName, IntPtr security);

    // The product guard is tag-independent. Use an actual mount-point reparse tag;
    // symbolic-link privilege is neither requested nor changed by this fixture.
    internal static void CreateDirectoryJunction(string fixtureRoot, string link, string target)
    {
        var prefix = fixtureRoot + Path.DirectorySeparatorChar;
        foreach (var path in new[] { link, target })
        {
            if (path.Length > 240 || Path.GetFullPath(path) != path ||
                !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Junction fixture escaped its explicit new fixture root.");
        }
        if (link.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            link.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(link + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetPathRoot(link), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Junction fixture requires separate local link and target directories.");
        RequireOrdinaryAncestors(target);
        RequireOrdinaryAncestors(Path.GetDirectoryName(link)!);
        if (Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException("Junction fixture target is not empty.");
        // CreateDirectoryW refuses a pre-existing link name; no entry is adopted.
        if (!CreateDirectory(link, IntPtr.Zero))
            throw JunctionFailure("fresh directory creation", Marshal.GetLastWin32Error());

        const uint mountPointTag = 0xA0000003;
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(mountPointTag);
            writer.Write(checked((ushort)(12 + substitute.Length + print.Length)));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(checked((ushort)substitute.Length));
            writer.Write(checked((ushort)(substitute.Length + 2)));
            writer.Write(checked((ushort)print.Length));
            writer.Write(substitute); writer.Write((ushort)0);
            writer.Write(print); writer.Write((ushort)0);
        }
        var bytes = buffer.ToArray();
        if (bytes.Length > 16_384)
            throw new IOException("Junction fixture exceeds the reparse-buffer bound.");
        // BACKUP_SEMANTICS | OPEN_REPARSE_POINT opens the directory object itself.
        using var handle = OpenDirectory(link, 0xC0000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw JunctionFailure("directory custody", Marshal.GetLastWin32Error());
        if (!ReparseControl(handle, 0x000900A4, bytes, bytes.Length, null, 0, out _, IntPtr.Zero))
        {
            var code = Marshal.GetLastWin32Error();
            throw JunctionFailure("reparse creation", code);
        }
        var observed = new byte[16_384];
        if (!ReparseControl(handle, 0x000900A8, null, 0, observed, observed.Length, out var returned, IntPtr.Zero))
            throw JunctionFailure("tag verification", Marshal.GetLastWin32Error());
        if (returned < bytes.Length || BitConverter.ToUInt32(observed, 0) != mountPointTag ||
            BitConverter.ToUInt16(observed, 4) != bytes.Length - 8)
            throw new IOException("Fixture is not a verified mount-point reparse buffer.");
        var substituteOffset = BitConverter.ToUInt16(observed, 8);
        var substituteLength = BitConverter.ToUInt16(observed, 10);
        var printOffset = BitConverter.ToUInt16(observed, 12);
        var printLength = BitConverter.ToUInt16(observed, 14);
        if ((substituteOffset | substituteLength | printOffset | printLength) % 2 != 0 ||
            16 + substituteOffset + substituteLength > bytes.Length || 16 + printOffset + printLength > bytes.Length ||
            !string.Equals(Encoding.Unicode.GetString(observed, 16 + substituteOffset, substituteLength),
                @"\??\" + target, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Encoding.Unicode.GetString(observed, 16 + printOffset, printLength),
                target, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Junction fixture target did not match its exact owned directory.");
    }

    private static Exception JunctionFailure(string stage, int code) =>
        code is 1 or 5 or 50 or 1314
            ? new FixtureUnavailable("Directory junction fixture unavailable at " + stage + "; code=" + code)
            : new Win32Exception(code, "Synthetic junction fixture failed at " + stage + ".");

    private static void RequireOrdinaryAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Junction fixture ancestry must be ordinary and already exist.");
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr security);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReparseControl(SafeFileHandle handle, uint code,
        [In] byte[]? input, int inputLength, [Out] byte[]? output, int outputLength,
        out int returned, IntPtr overlapped);
}
