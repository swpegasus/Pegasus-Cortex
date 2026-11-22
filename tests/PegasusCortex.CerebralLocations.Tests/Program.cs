using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Product;

internal static class Program
{
    // This is a fixture identity, never a registered production client ID.
    private const string Client = "fixture-cerebral";
    private const string Generation = "d724b082369d43a5a77671540c99c940";

    private static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("UNAVAILABLE location fixture requires Windows metadata APIs; no cases executed.");
            return 2;
        }
        var tests = new (string Name, Action Body)[]
        {
            ("unit.absent-map-preserves-live-and-autoattach", LiveDefault),
            ("unit.map-roundtrip-and-explicit-client", MapRoundtrip),
            ("unit.strict-json-admission", StrictJson),
            ("unit.missing-selected-root-never-falls-back", MissingSelectedRoot),
            ("unit.nonlive-isolation-contract", NonliveIsolation),
            ("metadata.lexical-overlap-and-boundaries", LexicalOverlap),
            ("unit.mocked-physical-alias-is-rejected", MockedPhysicalAlias),
            ("metadata.test-and-starter-are-disjoint", SeparateRoots),
            ("metadata.unsafe-paths-fail-closed", UnsafePaths),
            ("metadata.existing-and-missing-ancestor-identity", PhysicalIdentity),
            ("metadata.synthetic-hardlink-is-rejected", HardlinkAdmission),
            ("metadata.starter-requires-fresh-destination", FreshStarter),
            ("fixture-sqlite.new-test-store-preserves-synthetic-live", SqliteIsolation)
        };
        var passed = 0;
        var failed = 0;
        var skipped = 0;
        foreach (var test in tests)
        {
            try { test.Body(); passed++; Console.WriteLine("PASS " + test.Name); }
            catch (UnsupportedFixture error) { skipped++; Console.WriteLine("SKIP " + test.Name + " " + error.Message); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + test.Name + " " + error.GetType().Name + " " + error.Message); }
        }
        Console.WriteLine($"RESULT total={tests.Length} passed={passed} failed={failed} skipped={skipped}; new synthetic fixtures only; no registry/profile/host acceptance.");
        return failed == 0 && skipped == 0 ? 0 : 1;
    }

    private static void LiveDefault()
    {
        using var f = new Fixture();
        var selected = CortexCerebralDataLocations.Resolve(f.Live, null, Client,
            _ => throw new InvalidOperationException("Unexpected alias probe for absent map."),
            _ => throw new InvalidOperationException("Unexpected file probe for absent map."));
        Check(selected.DataRoot == f.Live.DataRoot && selected.DatabasePath == f.Live.CortexDatabasePath, "Live selection moved.");
        Check(selected.ClientId == Client && selected.Generation is null && selected.AllowsLiveProfileAutoAttach &&
            selected.AllowsSharedResidentEndpoint && !selected.RequiresIsolatedComposition, "Live auto-attach contract changed.");
        Throws<InvalidOperationException>(selected.RequireIsolatedComposition);
        Check(!Directory.Exists(f.Live.CortexDataDirectory), "Resolution created semantic state.");
    }

    private static void MapRoundtrip()
    {
        using var f = new Fixture();
        var map = f.Map(CortexCerebralDataMode.Test);
        var text = CortexCerebralDataLocations.PrepareRegistration(f.Live, map, Client);
        Check(CortexCerebralDataLocations.Parse(text) == map, "Map did not roundtrip exactly.");
        Rejects(() => CortexCerebralDataLocations.Resolve(f.Live, map, "different-fixture-client"));
        foreach (var invalid in new[] { "", "../fixture", ".fixture", new string('x', 64), "fixture\\child" })
            Rejects(() => CortexCerebralDataLocations.Resolve(f.Live, null, invalid));
    }

    private static void StrictJson()
    {
        using var f = new Fixture();
        var text = CortexCerebralDataLocations.PrepareRegistration(f.Live, f.Map(CortexCerebralDataMode.Test), Client);
        foreach (var invalid in new[]
        {
            text.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            text.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            text.Replace("\"version\":1", "\"version\":1,\"profileBody\":\"forbidden-fixture-field\"", StringComparison.Ordinal),
            text.Replace("\"mode\":\"test\"", "\"mode\":\"unknown\"", StringComparison.Ordinal),
            text.Replace(Generation, "not-a-generation", StringComparison.Ordinal),
            text.Replace("\"version\":1", "\"version\":\"1\"", StringComparison.Ordinal),
            "[]", "{}", new string(' ', CortexCerebralDataLocations.MaximumMapUtf8Bytes + 1)
        }) Rejects(() => CortexCerebralDataLocations.Parse(invalid));
    }

    private static void MissingSelectedRoot()
    {
        using var f = new Fixture();
        foreach (var map in new[]
        {
            f.Map(CortexCerebralDataMode.Test) with { TestDataLocation = null },
            f.Map(CortexCerebralDataMode.Starter) with { StarterDataLocation = null }
        }) Rejects(() => CortexCerebralDataLocations.Resolve(f.Live, map, Client));
        foreach (var required in new[] { CortexCerebralDataMode.Test, CortexCerebralDataMode.Starter })
        {
            Rejects(() => CortexCerebralDataLocations.ResolveExpected(f.Live, null, Client, required));
            Rejects(() => CortexCerebralDataLocations.ResolveExpected(f.Live, f.Map(CortexCerebralDataMode.Live), Client, required));
        }
        Check(CortexCerebralDataLocations.ResolveExpected(f.Live, null, Client, CortexCerebralDataMode.Live).DatabasePath ==
            f.Live.CortexDatabasePath, "Explicit live default changed.");
        Check(!Directory.Exists(f.Test) && !Directory.Exists(f.Starter), "Rejected map created state.");
    }

    private static void NonliveIsolation()
    {
        using var f = new Fixture();
        foreach (var mode in new[] { CortexCerebralDataMode.Test, CortexCerebralDataMode.Starter })
        {
            var selected = CortexCerebralDataLocations.Resolve(f.Live, f.Map(mode), Client);
            selected.RequireIsolatedComposition();
            Check(!selected.AllowsSharedResidentEndpoint && !selected.AllowsLiveProfileAutoAttach, "Nonlive selection permitted live access.");
            Check(selected.DataRoot == (mode == CortexCerebralDataMode.Test ? f.Test : f.Starter) &&
                selected.DatabasePath == Path.Combine(selected.DataRoot, "cortex-v2.db"), "Nonlive path mapping is wrong.");
            Check(selected.RequiresFreshStarterDestination == (mode == CortexCerebralDataMode.Starter), "Starter gate is wrong.");
        }
        Check(!Directory.Exists(f.Test) && !Directory.Exists(f.Starter), "Selecting a mode created a store.");
    }

    private static void LexicalOverlap()
    {
        using var f = new Fixture();
        foreach (var root in new[] { f.Live.DataRoot, f.Live.DataRoot.ToUpperInvariant(), Path.Combine(f.Live.DataRoot, "child"), f.Root, f.Live.InstallRoot })
            Rejects(() => CortexCerebralDataLocations.Resolve(f.Live, f.Map(CortexCerebralDataMode.Test) with { TestDataLocation = root }, Client));
        var sibling = f.Live.DataRoot + "-sibling";
        var selected = CortexCerebralDataLocations.Resolve(f.Live, f.Map(CortexCerebralDataMode.Test) with { TestDataLocation = sibling }, Client);
        Check(selected.DataRoot == sibling, "A safe sibling prefix was rejected.");
    }

    private static void MockedPhysicalAlias()
    {
        using var f = new Fixture();
        string Alias(string path) => path == f.Test ? f.Live.DataRoot : path;
        Rejects(() => CortexCerebralDataLocations.Resolve(f.Live, f.Map(CortexCerebralDataMode.Test), Client, Alias, _ => { }));
        Rejects(() => CortexCerebralDataLocations.Resolve(f.Live, f.Map(CortexCerebralDataMode.Live), Client, Alias, _ => { }));
        // A failed identity lookup must remain failure; it cannot select live.
        Throws<IOException>(() => CortexCerebralDataLocations.Resolve(f.Live, f.Map(CortexCerebralDataMode.Test), Client,
            _ => throw new IOException("Synthetic metadata failure."), _ => { }));
    }

    private static void SeparateRoots()
    {
        using var f = new Fixture();
        foreach (var root in new[] { f.Test, Path.Combine(f.Test, "child"), f.Root })
            Rejects(() => CortexCerebralDataLocations.Resolve(f.Live, f.Map(CortexCerebralDataMode.Test) with { StarterDataLocation = root }, Client));
        var selected = CortexCerebralDataLocations.Resolve(f.Live, f.Map(CortexCerebralDataMode.Starter), Client);
        Check(selected.DataRoot == f.Starter, "Distinct starter root was not selected.");
    }

    private static void UnsafePaths()
    {
        using var f = new Fixture();
        foreach (var root in new[]
        {
            "relative", Path.GetPathRoot(f.Root)!, Path.Combine(f.Root, "unsafe."), Path.Combine(f.Root, "CON"),
            Path.Combine(f.Root, "item:stream"), Path.Combine(f.Root, "missing", "..", "test"),
            Path.Combine(f.Root, new string('x', 241)), @"\\localhost\C$\fixture"
        }) Rejects(() => CortexCerebralDataLocations.Resolve(f.Live, f.Map(CortexCerebralDataMode.Test) with { TestDataLocation = root }, Client));
    }

    private static void PhysicalIdentity()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.Test);
        var physical = CortexLocationPathIdentity.PhysicalPath(f.Test);
        var missing = CortexLocationPathIdentity.PhysicalPath(Path.Combine(f.Test, "missing", "tail"));
        Check(physical.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase), "Metadata identity lacks volume GUID.");
        Check(missing == Path.Combine(physical, "missing", "tail"), "Missing tail lost its physical ancestor or order.");
        Check(!Directory.Exists(Path.Combine(f.Test, "missing")), "Physical lookup created the missing tail.");
        var regularFile = Path.Combine(f.Test, "synthetic.bin");
        File.WriteAllText(regularFile, "synthetic metadata fixture");
        CortexLocationPathIdentity.RequireSingleLinkFile(regularFile);
        Rejects(() => CortexLocationPathIdentity.CanonicalRoot(regularFile));
        Rejects(() => CortexLocationPathIdentity.PhysicalPath(Path.Combine(regularFile, "child")));
    }

    private static void HardlinkAdmission()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.Test);
        var source = Path.Combine(f.Root, "synthetic-source.db");
        var alias = Path.Combine(f.Test, "cortex-v2.db");
        File.WriteAllText(source, "synthetic hardlink bytes, not a retained database");
        if (!CreateHardLinkW(alias, source, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 1 or 50) throw new UnsupportedFixture("filesystem hardlinks unsupported, win32=" + error);
            throw new Win32Exception(error);
        }
        Rejects(() => CortexCerebralDataLocations.Resolve(f.Live, f.Map(CortexCerebralDataMode.Test), Client));
        Check(File.ReadAllText(source) == "synthetic hardlink bytes, not a retained database", "Admission changed hardlink bytes.");
    }

    private static void FreshStarter()
    {
        using var f = new Fixture();
        var selected = CortexCerebralDataLocations.Resolve(f.Live, f.Map(CortexCerebralDataMode.Starter), Client);
        CortexCerebralDataLocations.RequireFreshStarterDestination(selected);
        Check(!Directory.Exists(f.Starter), "Starter gate created a destination.");
        Directory.CreateDirectory(f.Starter);
        CortexCerebralDataLocations.RequireFreshStarterDestination(selected);
        var marker = Path.Combine(f.Starter, "synthetic-existing.txt");
        File.WriteAllText(marker, "keep this synthetic fixture");
        Rejects(() => CortexCerebralDataLocations.RequireFreshStarterDestination(selected));
        Check(File.ReadAllText(marker) == "keep this synthetic fixture", "Starter gate changed existing state.");
    }

    private static void SqliteIsolation()
    {
        using var f = new Fixture();
        SQLitePCL.Batteries_V2.Init();
        Directory.CreateDirectory(f.Live.CortexDataDirectory);
        var nativeVersion = CreateSyntheticDatabase(f.Live.CortexDatabasePath, "synthetic-live");
        Console.WriteLine("Native SQLite fixture version: " + nativeVersion);
        var liveBytes = File.ReadAllBytes(f.Live.CortexDatabasePath);
        var selected = CortexCerebralDataLocations.Resolve(f.Live, f.Map(CortexCerebralDataMode.Test), Client);
        Directory.CreateDirectory(selected.DataRoot);
        CreateSyntheticDatabase(selected.DatabasePath, "synthetic-test");
        Check(ReadSyntheticValue(selected.DatabasePath) == "synthetic-test", "New test store did not receive its fixture value.");
        Check(ReadSyntheticValue(f.Live.CortexDatabasePath) == "synthetic-live" &&
            liveBytes.SequenceEqual(File.ReadAllBytes(f.Live.CortexDatabasePath)), "Test selection mutated the synthetic live store.");
        Check(f.Live.CortexDatabasePath != selected.DatabasePath && !selected.AllowsLiveProfileAutoAttach, "SQLite fixture crossed into live selection.");
    }

    private static string CreateSyntheticDatabase(string path, string value)
    {
        if (File.Exists(path)) throw new InvalidOperationException("Synthetic database already exists.");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; CREATE TABLE synthetic(value TEXT NOT NULL); INSERT INTO synthetic(value) VALUES($value);";
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
        return connection.ServerVersion;
    }

    private static string ReadSyntheticValue(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM synthetic;";
        return (string)command.ExecuteScalar()!;
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void Rejects(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException) { return; }
        throw new InvalidOperationException("Unsafe fixture input was accepted.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class UnsupportedFixture(string message) : Exception(message);

    private sealed class Fixture : IDisposable
    {
        private const string Prefix = "cortex-location-fixture-";
        public string Root { get; }
        public string Test => Path.Combine(Root, "test");
        public string Starter => Path.Combine(Root, "starter");
        public CortexProductLayout Live { get; }

        public Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
            if (Directory.Exists(Root) || File.Exists(Root)) throw new IOException("Fixture root already exists.");
            Directory.CreateDirectory(Root);
            try { Live = CortexProductLayout.ForIsolatedRoots(Path.Combine(Root, "program"), Path.Combine(Root, "live"), "S-1-5-21-100-200-300-1001"); }
            catch { Directory.Delete(Root); throw; }
        }

        public CortexCerebralLocationMapV1 Map(CortexCerebralDataMode mode) =>
            new(1, Client, Generation, mode, Test, Starter);

        public void Dispose()
        {
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var leaf = Path.GetFileName(Root);
            Check(Path.GetDirectoryName(Root) == parent && leaf.StartsWith(Prefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(leaf[Prefix.Length..], "N", out _), "Fixture cleanup escaped its exact temporary root.");
            var pending = new Stack<string>();
            pending.Push(Root);
            while (pending.TryPop(out var directory))
            {
                Check((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0, "Fixture cleanup encountered a reparse point.");
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    Check((attributes & FileAttributes.ReparsePoint) == 0, "Fixture cleanup encountered a reparse point.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
            Directory.Delete(Root, recursive: true);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, nint security);
}
