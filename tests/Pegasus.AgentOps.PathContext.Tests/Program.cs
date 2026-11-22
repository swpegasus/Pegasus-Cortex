using System.Diagnostics;
using Pegasus.AgentOps.Engine;

var tests = new (string Name, Action Run)[]
{
    ("mapped_drive_root", MappedDriveRoot),
    ("mapped_drive_child_suffix_spaces_unicode", MappedDriveChild),
    ("unavailable_mapping", UnavailableMapping),
    ("unc_normalization_without_share_probe", UncNormalization),
    ("local_path_preserved", LocalPath),
    ("alias_root_normalized_once", AliasRootOnce),
    ("alias_path_safety", AliasSafety),
    ("bounded_and_invalid_syntax", InvalidSyntax),
    ("local_reparse_component_rejected", ReparseRejected),
    ("real_local_drive_readonly", RealLocalDrive),
    ("real_existing_mapping_readonly", RealExistingMapping)
};

var failed = 0;
var skipped = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (SkipTestException ex)
    {
        skipped++;
        Console.WriteLine($"SKIP {test.Name} {ex.Message}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name} {ex}");
    }
}

Console.WriteLine($"RESULT total={tests.Length} passed={tests.Length - failed - skipped} skipped={skipped} failed={failed}");
return failed == 0 ? 0 : 1;

static void MappedDriveRoot()
{
    var resolver = new FakeResolver(path =>
    {
        Equal(@"N:\", path);
        return Mapped(@"\\fileserver\CodexProjects\");
    });
    var result = new WindowsPathContextNormalizer(resolver).Normalize(@"n:\");
    Equal(Path.GetFullPath(@"\\fileserver\CodexProjects\"), result.CanonicalPath);
    AssertNetwork(result);
    Equal(1, resolver.Calls.Count);
}

static void MappedDriveChild()
{
    const string expectedInput = @"N:\Folder With Spaces\Ω work\agent's file.bin";
    const string universal = @"\\fileserver\CodexProjects\Folder With Spaces\Ω work\agent's file.bin";
    var resolver = new FakeResolver(path =>
    {
        Equal(expectedInput, path);
        return Mapped(universal);
    });
    var result = new WindowsPathContextNormalizer(resolver).Normalize(@"n:\Folder With Spaces\Ω work\agent's file.bin");
    Equal(Path.GetFullPath(universal), result.CanonicalPath);
    AssertNetwork(result);
}

static void UnavailableMapping()
{
    var resolver = new FakeResolver(_ => new WindowsDriveMappingResolution(
        WindowsDriveMappingState.Unavailable,
        NativeError: 1201));
    ThrowsCode("mapping_unavailable", () => new WindowsPathContextNormalizer(resolver).Normalize(@"N:\offline\file.txt"));
    Equal(1, resolver.Calls.Count);
}

static void UncNormalization()
{
    var resolver = new FakeResolver(_ => throw new InvalidOperationException("UNC path called drive resolver"));
    const string input = @"\\server\share\folder\..\Space Folder\Ω file.txt";
    var result = new WindowsPathContextNormalizer(resolver).Normalize(input);
    Equal(Path.GetFullPath(input), result.CanonicalPath);
    AssertNetwork(result);
    Equal(0, resolver.Calls.Count);
}

static void LocalPath()
{
    using var temp = new TempRoot();
    var path = temp.File("local Ω file.txt", "safe");
    var resolver = new FakeResolver(_ => new WindowsDriveMappingResolution(WindowsDriveMappingState.Local));
    var result = new WindowsPathContextNormalizer(resolver).Normalize(path);
    Equal(UppercaseDrive(Path.GetFullPath(path)), result.CanonicalPath);
    Equal("local", result.Kind);
    Equal("local", result.Atomicity);
    Equal(1, resolver.Calls.Count);
}

static void AliasRootOnce()
{
    const string remoteRoot = @"\\fileserver\CodexProjects";
    var resolver = new FakeResolver(path =>
    {
        Check(path.StartsWith(@"N:\", StringComparison.Ordinal), "unexpected drive path");
        var suffix = path.Length > 3 ? path[3..] : string.Empty;
        return Mapped(suffix.Length == 0 ? remoteRoot : Path.Combine(remoteRoot, suffix));
    });
    var normalizer = new WindowsPathContextNormalizer(resolver);
    var alias = normalizer.NormalizeAliasRoot("share", @"N:\Projects\");
    Equal(1, resolver.Calls.Count);
    Equal(@"\\fileserver\CodexProjects\Projects", alias.Root.CanonicalPath);

    var first = normalizer.ResolveAliasPath(alias, @"Pegasus AgentOps\Ω files\one.bin");
    var second = normalizer.ResolveAliasPath(alias, @"Pegasus Exchange\two.bin");
    Equal(1, resolver.Calls.Count);
    Equal(@"\\fileserver\CodexProjects\Projects\Pegasus AgentOps\Ω files\one.bin", first.CanonicalPath);
    Equal(@"\\fileserver\CodexProjects\Projects\Pegasus Exchange\two.bin", second.CanonicalPath);
    AssertNetwork(first);
    AssertNetwork(second);
}

static void AliasSafety()
{
    using var temp = new TempRoot();
    var resolver = new FakeResolver(_ => new WindowsDriveMappingResolution(WindowsDriveMappingState.Local));
    var normalizer = new WindowsPathContextNormalizer(resolver);
    var alias = normalizer.NormalizeAliasRoot("local_ws", temp.Path);
    var safe = normalizer.ResolveAliasPath(alias, @"folder with spaces\Ω file.txt");
    Check(safe.CanonicalPath.StartsWith(alias.Root.CanonicalPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
        "safe alias child escaped its root");

    ThrowsCode("precondition", () => normalizer.ResolveAliasPath(alias, @"..\outside.txt"));
    ThrowsCode("precondition", () => normalizer.ResolveAliasPath(alias, @"folder\..\outside.txt"));
    ThrowsCode("precondition", () => normalizer.ResolveAliasPath(alias, @"C:\outside.txt"));
    ThrowsCode("precondition", () => normalizer.ResolveAliasPath(alias, @"file.txt:secret"));
    ThrowsCode("precondition", () => normalizer.ResolveAliasPath(alias, @"*.txt"));
    ThrowsCode("invalid", () => normalizer.ResolveAliasPath(alias, string.Empty));
    ThrowsCode("invalid", () => normalizer.NormalizeAliasRoot("bad alias", temp.Path));
}

static void InvalidSyntax()
{
    var normalizer = new WindowsPathContextNormalizer(
        new FakeResolver(_ => new WindowsDriveMappingResolution(WindowsDriveMappingState.Local)));
    ThrowsCode("invalid", () => normalizer.Normalize(string.Empty));
    ThrowsCode("invalid", () => normalizer.Normalize("relative\\file.txt"));
    ThrowsCode("precondition", () => normalizer.Normalize(@"C:\folder\*.txt"));
    ThrowsCode("precondition", () => normalizer.Normalize(@"C:\folder\file.txt:secret"));
    ThrowsCode("invalid", () => normalizer.Normalize(@"\\?\C:\folder\file.txt"));
    ThrowsCode("invalid", () => normalizer.Normalize(new string('x', WindowsPathContextNormalizer.MaximumPathCharacters + 1)));

    var oversizedUniversal = @"\\server\share\" + new string('x', WindowsPathContextNormalizer.MaximumPathCharacters);
    var oversizedResolver = new FakeResolver(_ => Mapped(oversizedUniversal));
    ThrowsCode("invalid", () => new WindowsPathContextNormalizer(oversizedResolver).Normalize(@"N:\file.txt"));
}

static void ReparseRejected()
{
    using var temp = new TempRoot();
    var external = temp.Dir("external");
    temp.File(Path.Combine("external", "safe.txt"), "external-safe");
    var junction = Path.Combine(temp.Path, "junction");
    CreateJunction(junction, external);
    try
    {
        var normalizer = new WindowsPathContextNormalizer(
            new FakeResolver(_ => new WindowsDriveMappingResolution(WindowsDriveMappingState.Local)));
        ThrowsCode("reparse_boundary", () => normalizer.Normalize(Path.Combine(junction, "safe.txt")));
        ThrowsCode("reparse_boundary", () => normalizer.NormalizeAliasRoot("linked", junction));
    }
    finally
    {
        if (Directory.Exists(junction)) Directory.Delete(junction);
    }
}

static void RealLocalDrive()
{
    var result = new WindowsPathContextNormalizer().Normalize(Path.GetTempPath());
    Equal("local", result.Kind);
    Equal("local", result.Atomicity);
    Check(result.CanonicalPath.Length <= WindowsPathContextNormalizer.MaximumPathCharacters, "real local result exceeded bound");
}

static void RealExistingMapping()
{
    DriveInfo? mapped = null;
    foreach (var drive in DriveInfo.GetDrives())
    {
        try
        {
            if (drive.DriveType == DriveType.Network)
            {
                mapped = drive;
                break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    if (mapped is null) throw new SkipTestException("no existing user mapped drive");
    WindowsPathContextResult result;
    try
    {
        result = new WindowsPathContextNormalizer().Normalize(mapped.Name);
    }
    catch (AgentOpsException ex) when (ex.Code == "mapping_unavailable")
    {
        throw new SkipTestException("existing mapped drive is currently unavailable");
    }

    AssertNetwork(result);
    Check(result.CanonicalPath.StartsWith(@"\\", StringComparison.Ordinal), "mapped drive did not resolve to UNC");
}

static WindowsDriveMappingResolution Mapped(string path) =>
    new(WindowsDriveMappingState.Mapped, path);

static void AssertNetwork(WindowsPathContextResult result)
{
    Equal("network", result.Kind);
    Equal("remote_unverified", result.Atomicity);
    Check(result.CanonicalPath.StartsWith(@"\\", StringComparison.Ordinal), "network result was not UNC");
}

static string UppercaseDrive(string path) =>
    path.Length >= 2 && path[1] == ':' ? char.ToUpperInvariant(path[0]) + path[1..] : path;

static void CreateJunction(string path, string target)
{
    var start = new ProcessStartInfo(ResolvePowerShell())
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

    using var process = Process.Start(start) ?? throw new InvalidOperationException("junction process did not start");
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
    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        var candidate = Path.Combine(directory.Trim('"'), "pwsh.exe");
        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
    }
    throw new FileNotFoundException("PowerShell 7 test executable not found on PATH");
}

static void SetMinimumEnvironment(ProcessStartInfo start)
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
        if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value) values[key] = value;
    start.Environment.Clear();
    foreach (var item in values) start.Environment[item.Key] = item.Value;
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

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected={expected} actual={actual}");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakeResolver : IWindowsDriveMappingResolver
{
    private readonly Func<string, WindowsDriveMappingResolution> _resolve;

    public FakeResolver(Func<string, WindowsDriveMappingResolution> resolve) => _resolve = resolve;

    public List<string> Calls { get; } = [];

    public WindowsDriveMappingResolution Resolve(string canonicalDrivePath)
    {
        Calls.Add(canonicalDrivePath);
        return _resolve(canonicalDrivePath);
    }
}

sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pao-path-context-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Dir(string relative) => Directory.CreateDirectory(System.IO.Path.Combine(Path, relative)).FullName;

    public string File(string relative, string text)
    {
        var path = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

sealed class SkipTestException(string message) : Exception(message);
