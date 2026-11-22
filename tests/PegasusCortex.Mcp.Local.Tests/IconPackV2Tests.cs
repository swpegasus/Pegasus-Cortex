using System.Security.Cryptography;
using System.Text;
using Pegasus.Cortex.Persistence.File.Assets;

internal static class IconPackV2Tests
{
    private const string FrozenCatalogSha256 = "04aa6881acc2f65edab2c046409cb87f71e932081fb0ac21bea9f2f526aa0011";
    private const string FrozenContentSha256 = "63f60ac78551b78f4796222a68a323c56267713c94f85f91e1bc3e910a9a427d";
    private const string PassiveSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 20\"><path fill=\"currentColor\" d=\"M0 0h20v20H0z\"/></svg>";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static Task FrozenPackMetadataAsync()
    {
        var root = FindFrozenPackRoot();
        var pack = CortexIconPackV2.Load(root, FrozenCatalogSha256, FrozenContentSha256);
        LocalAssert.True(
            pack.Id == "pegasus.chat-icons" && pack.Version == "1.0.0" && pack.Entries.Count == 11 &&
            pack.NamespacePrefix == "pegasus.chat-icons." && pack.License == "MIT" && pack.Publisher == "Microsoft" &&
            pack.SourceRevision == "cd332ec1f90cda2fad16f86754f8da52f1e9ab30" &&
            pack.CatalogSha256 == FrozenCatalogSha256 && pack.ContentSha256 == FrozenContentSha256,
            "The real frozen producer pack did not preserve its exact pins and identity.");
        var success = pack.Entries.Single(entry => entry.Alias == "status.success");
        var assistant = pack.Entries.Single(entry => entry.Alias == "chat.assistant");
        LocalAssert.True(
            success.Id == "pegasus.chat-icons.status.success" && success.Title == "Succeeded" &&
            success.Meaning == "Completed successfully." && success.Text == "✓" && assistant.Text == "🤖" &&
            pack.Entries.All(entry => entry.Size > 0 && entry.MediaType == "image/svg+xml" &&
                entry.Sha256.Length == 64 && entry.PackId == pack.Id && entry.PackVersion == pack.Version &&
                entry.License == "MIT" && entry.Source.StartsWith("https://github.com/microsoft/", StringComparison.Ordinal)),
            "Searchable meaning, Unicode fallbacks, measured sizes, licensing, or exact asset identity were lost.");
        LocalAssert.True(
            pack.Files.Count == 14 && pack.Files.Count(file => file.Role == "license") == 1 &&
            pack.Files.Count(file => file.Role == "notice") == 1 && pack.Files.Count(file => file.Role == "catalog") == 1 &&
            pack.Files.All(file => File.Exists(pack.GetFilePath(file.FilePath)) && file.Size > 0),
            "The portable admission manifest omitted catalog, required notices, or exact artwork files.");
        ExpectRejected(() => pack.GetFilePath("HANDOFF.txt"), "not in the admitted", "Undeclared adjacent evidence became an asset.");
        ExpectRejected(() => pack.GetFilePath("../catalog.ini"), "not in the admitted", "An undeclared relative path was resolved.");
        return Task.CompletedTask;
    }

    public static Task RejectUnsafeAndTamperedPackAsync()
    {
        using var workspace = new FixtureWorkspace();
        var valid = workspace.Create("valid");
        _ = valid.Load();
        ExpectRejected(() => CortexIconPackV2.Load(valid.Root, new string('a', 64), valid.ContentSha256),
            "catalog does not match", "A wrong catalog pin was accepted.");
        ExpectRejected(() => CortexIconPackV2.Load(valid.Root, valid.CatalogSha256, new string('a', 64)),
            "canonical SHA-256", "A wrong canonical content pin was accepted.");
        ExpectRejected(() => CortexIconPackV2.Load(valid.Root, "", valid.ContentSha256),
            "exact SHA-256", "Unpinned icon admission was accepted.");

        var tamper = workspace.Create("tamper");
        File.WriteAllText(Path.Combine(tamper.Root, "svg", "fixture.svg"), PassiveSvg + " ", Utf8);
        ExpectRejected(() => tamper.Load(), "declared SHA-256", "Changed artwork bytes were accepted.");

        var changedAfterAdmission = workspace.Create("changed-after");
        var admitted = changedAfterAdmission.Load();
        File.WriteAllText(Path.Combine(changedAfterAdmission.Root, "Licensing", "NOTICE.txt"), "Changed notice.", Utf8);
        ExpectRejected(() => admitted.GetFilePath("Licensing/NOTICE.txt"), "changed before import",
            "A changed notice was returned from a previously admitted pack.");

        foreach (var unsafePath in new[] { "svg/../fixture.svg", "svg/CON.svg", "svg/fixture.svg:stream", "svg/fixture.svg.", "svg\\fixture.svg" })
        {
            var fixture = workspace.Create("path-" + workspace.NextId());
            fixture.RewriteCatalog(text => text.Replace("file=svg/fixture.svg", "file=" + unsafePath, StringComparison.Ordinal));
            ExpectRejected(() => fixture.Load(), null, "An unsafe icon path was admitted: " + unsafePath);
        }

        foreach (var svg in new[]
        {
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>bad</script><path d=\"M0 0\"/></svg>",
            "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"bad()\"><path d=\"M0 0\"/></svg>",
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><path fill=\"url(https://invalid.test/a)\" d=\"M0 0\"/></svg>",
            "<!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///not-a-resource'>]><svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0\"/></svg>",
            "<?unsafe external?><svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0\"/></svg>",
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"><path d=\"M0 0\"/></svg>"
        })
        {
            var fixture = workspace.Create("active-" + workspace.NextId(), svg);
            ExpectRejected(() => fixture.Load(), null, "Pinned active SVG content bypassed passive admission.");
        }

        var missing = workspace.Create("missing");
        File.Delete(Path.Combine(missing.Root, "Licensing", "LICENSE.txt"));
        ExpectRejected(() => missing.Load(), null, "A missing licensing file was accepted.");
        var oversized = workspace.Create("oversized", new string(' ', CortexIconPackV2.MaximumAssetBytes + 1));
        ExpectRejected(() => oversized.Load(), "byte bound", "Oversized artwork was hydrated.");
        var unknown = workspace.Create("unknown");
        unknown.RewriteCatalog(text => text.Replace("viewport=20", "viewport=20\nunknown=1", StringComparison.Ordinal));
        ExpectRejected(() => unknown.Load(), "Unexpected icon catalog fields", "Unknown catalog fields were accepted.");
        var duplicate = workspace.Create("duplicate");
        duplicate.RewriteCatalog(text => text.Replace("viewport=20", "viewport=20\nviewport=20", StringComparison.Ordinal));
        ExpectRejected(() => duplicate.Load(), "duplicate icon catalog property", "Duplicate catalog fields were accepted.");
        var foreign = workspace.Create("foreign");
        foreign.RewriteCatalog(text => text.Replace("[fixture.icons.status.0]", "[other.icons.status.0]", StringComparison.Ordinal));
        ExpectRejected(() => foreign.Load(), "declared artwork namespace", "An ID escaped its artwork namespace.");
        var credentials = workspace.Create("credential-url");
        credentials.RewriteCatalog(text => text.Replace("upstream=https://example.test/icons", "upstream=https://name:secret@example.test/icons", StringComparison.Ordinal));
        ExpectRejected(() => credentials.Load(), "credential-free", "Credential-bearing source metadata was admitted.");
        var invalidUtf8 = workspace.Create("invalid-utf8");
        File.WriteAllBytes(Path.Combine(invalidUtf8.Root, "catalog.ini"), [0xff]);
        invalidUtf8.RefreshPins();
        ExpectRejected(() => invalidUtf8.Load(), null, "Invalid UTF-8 was silently repaired.");
        return Task.CompletedTask;
    }

    public static async Task BoundedMetadataAndCancellationAsync()
    {
        using var workspace = new FixtureWorkspace();
        var maximum = workspace.Create("maximum", iconCount: CortexIconPackV2.MaximumIcons);
        var pack = maximum.Load();
        LocalAssert.True(
            pack.Entries.Count == CortexIconPackV2.MaximumIcons && pack.Files.Count == 4 &&
            pack.Entries.Select(entry => entry.Id).SequenceEqual(
                pack.Entries.Select(entry => entry.Id).Order(StringComparer.Ordinal)) &&
            !typeof(CortexIconPackV2).GetProperties().Any(property => property.PropertyType == typeof(byte[])),
            "The pack reader retained artwork, duplicated shared files, or lost bounded deterministic metadata.");
        var over = workspace.Create("over", iconCount: CortexIconPackV2.MaximumIcons + 1);
        ExpectRejected(() => over.Load(), "excessive icon catalog sections", "More than the bounded icon count was admitted.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try
        {
            _ = CortexIconPackV2.Load(maximum.Root, maximum.CatalogSha256, maximum.ContentSha256, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        LocalAssert.True(cancelled, "Icon admission ignored cancellation.");
        if (!await TryRejectDanglingReparsePathsAsync())
            Console.WriteLine("SKIP assets reparse probe: symbolic-link creation is unavailable.");
    }

    public static Task<bool> TryRejectDanglingReparsePathsAsync()
    {
        using var workspace = new FixtureWorkspace();
        var fixture = workspace.Create("dangling-link");
        var link = Path.GetFullPath(Path.Combine(fixture.Root, "svg", "fixture.svg"));
        var missingTarget = Path.GetFullPath(Path.Combine(fixture.Root, "missing.svg"));
        var prefix = Path.GetFullPath(fixture.Root) + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(link) || !Path.IsPathFullyQualified(missingTarget) ||
            !link.StartsWith(prefix, StringComparison.Ordinal) || !missingTarget.StartsWith(prefix, StringComparison.Ordinal) ||
            (File.GetAttributes(link) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("The generated link probe targets are not exact owned fixture files.");
        var created = false;
        try
        {
            File.Delete(link);
            try
            {
                File.CreateSymbolicLink(link, missingTarget);
                created = true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException ||
                exception is IOException && (exception.HResult & 0xffff) is 5 or 50 or 1314)
            {
                return Task.FromResult(false);
            }
            LocalAssert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0,
                "The OS did not create the expected dangling link probe.");
            ExpectRejected(() => fixture.Load(), "reparse point", "Dangling artwork bypassed icon path admission.");
            return Task.FromResult(true);
        }
        finally
        {
            if (created)
            {
                if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
                    throw new IOException("The exact fixture link changed before cleanup.");
                File.Delete(link); // Unlink this exact task-owned entry; never open or traverse its target.
            }
        }
    }

    private static string FindFrozenPackRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "PegasusCortex.slnx")))
                continue;
            var packaged = Path.Combine(directory.FullName, "assets", "icons", "chat-v1");
            if (File.Exists(Path.Combine(packaged, "catalog.ini")))
                return packaged;
            var producer = Path.Combine(directory.Parent!.FullName, "Pegasus Cerebral", "assets", "icons", "chat-v1");
            if (File.Exists(Path.Combine(producer, "catalog.ini")))
                return producer;
        }
        throw new DirectoryNotFoundException("The exact frozen icon source fixture is not available.");
    }

    private static void ExpectRejected(Action action, string? messagePart, string failure)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or KeyNotFoundException)
        {
            LocalAssert.True(messagePart is null || exception.Message.Contains(messagePart, StringComparison.Ordinal),
                failure + " Wrong rejection: " + exception.Message);
            return;
        }
        throw new InvalidOperationException(failure);
    }

    private sealed class FixtureWorkspace : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cx-icons-" + Guid.NewGuid().ToString("N"));
        private int counter;
        public string NextId() => (++counter).ToString(System.Globalization.CultureInfo.InvariantCulture);

        public Fixture Create(string name, string svg = PassiveSvg, int iconCount = 1) => new(Path.Combine(root, name), svg, iconCount);

        public void Dispose()
        {
            if (!Directory.Exists(root))
                return;
            // Only this generated fixture root is owned here; no link fixtures are created or traversed.
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Fixture cleanup found an unexpected reparse entry.");
            ValidateCleanupTree(root);
            Directory.Delete(root, recursive: true);
        }

        private static void ValidateCleanupTree(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Fixture cleanup found an unexpected reparse entry.");
                if ((attributes & FileAttributes.Directory) != 0)
                    ValidateCleanupTree(path);
            }
        }
    }

    private sealed class Fixture
    {
        public Fixture(string root, string svg, int iconCount)
        {
            Root = root;
            Directory.CreateDirectory(Path.Combine(root, "svg"));
            Directory.CreateDirectory(Path.Combine(root, "Licensing"));
            File.WriteAllText(Path.Combine(root, "svg", "fixture.svg"), svg, Utf8);
            File.WriteAllText(Path.Combine(root, "Licensing", "LICENSE.txt"), "MIT fixture license.\n", Utf8);
            File.WriteAllText(Path.Combine(root, "Licensing", "NOTICE.txt"), "Fixture notice.\n", Utf8);
            var svgSha256 = Digest(Utf8.GetBytes(svg));
            var catalog = new StringBuilder("[pack]\ncontract=pegasus.asset-pack/1\nid=fixture.icons\nversion=1.0.0\nnamespace=fixture.icons.\nlicense=MIT\nlicense-file=Licensing/LICENSE.txt\nnotice-file=Licensing/NOTICE.txt\npublisher=Fixture\nupstream=https://example.test/icons\nrevision=pinned-fixture\nviewport=20\n");
            for (var index = 0; index < iconCount; index++)
                catalog.Append($"\n[fixture.icons.status.{index}]\nalias=status.{index}\nlabel=Success {index}\ndescription=Completed successfully.\nfile=svg/fixture.svg\nsha256={svgSha256}\ntext=✓\nsource=https://example.test/icons/fixture.svg\n");
            File.WriteAllText(Path.Combine(root, "catalog.ini"), catalog.ToString(), Utf8);
            RefreshPins();
        }

        public string Root { get; }
        public string CatalogSha256 { get; private set; } = "";
        public string ContentSha256 { get; private set; } = "";
        public CortexIconPackV2 Load() => CortexIconPackV2.Load(Root, CatalogSha256, ContentSha256);
        public void RewriteCatalog(Func<string, string> update)
        {
            var path = Path.Combine(Root, "catalog.ini");
            File.WriteAllText(path, update(File.ReadAllText(path, Utf8)), Utf8);
            RefreshPins();
        }
        public void RefreshPins()
        {
            var paths = new[] { "catalog.ini", "Licensing/LICENSE.txt", "Licensing/NOTICE.txt", "svg/fixture.svg" };
            var hashes = paths.Order(StringComparer.Ordinal)
                .Select(path => (Path: path, Sha256: Digest(File.ReadAllBytes(Path.Combine(Root, path))))).ToArray();
            CatalogSha256 = hashes.Single(item => item.Path == "catalog.ini").Sha256;
            ContentSha256 = Digest(Utf8.GetBytes(string.Concat(hashes.Select(item => item.Path + "\0" + item.Sha256 + "\n"))));
        }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
