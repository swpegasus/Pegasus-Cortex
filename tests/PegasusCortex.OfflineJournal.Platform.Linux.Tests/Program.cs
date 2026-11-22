using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Pegasus.Cortex.OfflineJournal.Paths;
using Pegasus.Cortex.OfflineJournal.Platform.Linux;

if (args.Length > 0 && args[0].StartsWith("--native-child-", StringComparison.Ordinal))
{
    Environment.ExitCode = RunNativeChild(args);
    return;
}

var tests = new List<(string Name, Action Test)>
{
    ("factory advertises production boundary", FactoryAdvertisesProductionBoundary)
};
if (!OperatingSystem.IsLinux())
{
    tests.Add(("unsupported platform rejects journal without path disclosure", UnsupportedPlatformRejectsJournal));
    tests.Add(("unsupported platform rejects legacy source without path disclosure", UnsupportedPlatformRejectsLegacySource));
}
else
{
    Console.WriteLine("SKIP unsupported-platform rejection cases (current platform is Linux)");
}

var passed = 0;
foreach (var (name, test) in tests)
{
    test();
    passed++;
    Console.WriteLine($"PASS {name}");
}

var nativeRoot = Environment.GetEnvironmentVariable("PEGASUS_CORTEX_LINUX_JOURNAL_TEST_ROOT");
if (!OperatingSystem.IsLinux())
{
    Console.WriteLine("SKIP native Linux filesystem cases (current platform is not Linux)");
}
else if (string.IsNullOrEmpty(nativeRoot))
{
    Console.WriteLine("SKIP native Linux filesystem cases (PEGASUS_CORTEX_LINUX_JOURNAL_TEST_ROOT is unset)");
}
else
{
    RunNativeLinuxCases(nativeRoot);
    passed++;
    Console.WriteLine("PASS native Linux openat2/statx/mutation contract");
}

Console.WriteLine($"Linux offline-journal platform tests passed: {passed}/{tests.Count + (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(nativeRoot) ? 1 : 0)}");

static void FactoryAdvertisesProductionBoundary()
{
    Assert.Equal(
        JournalFileSystemBoundarySafety.HandleRelativeNoFollow,
        LinuxJournalFileSystemBoundaryFactory.Instance.Safety);
}

static void UnsupportedPlatformRejectsJournal()
{
    if (OperatingSystem.IsLinux())
        return;
    const string sensitivePath = "C:\\sensitive-journal-root";
    var exception = Assert.Throws<LinuxJournalFileSystemException>(() =>
        LinuxJournalFileSystemBoundaryFactory.Instance.OpenJournalRoot(sensitivePath, sensitivePath));
    Assert.Equal(LinuxJournalIoCause.PlatformUnsupported, exception.Cause);
    Assert.Equal("open_journal_root", exception.Operation);
    Assert.ExceptionGraphDoesNotContain(exception, sensitivePath);
}

static void UnsupportedPlatformRejectsLegacySource()
{
    if (OperatingSystem.IsLinux())
        return;
    const string sensitivePath = "C:\\sensitive-legacy-root";
    var exception = Assert.Throws<LinuxJournalFileSystemException>(() =>
        LinuxJournalFileSystemBoundaryFactory.Instance.OpenLegacySourceRoot(sensitivePath, sensitivePath));
    Assert.Equal(LinuxJournalIoCause.PlatformUnsupported, exception.Cause);
    Assert.Equal("open_legacy_source_root", exception.Operation);
    Assert.ExceptionGraphDoesNotContain(exception, sensitivePath);
}

static void RunNativeLinuxCases(string requestedRoot)
{
    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedRoot));
    Assert.Equal(requestedRoot, root);
    Assert.True(Directory.Exists(root));
    Assert.Equal(
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
        File.GetUnixFileMode(root));
    Assert.True(
        RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.X64,
        "Native validation supports Linux ARM64 and x64 only.");
    Console.WriteLine($"INFO native Linux architecture: {RuntimeInformation.ProcessArchitecture}");
    foreach (var descriptor in new[] { 0, 1, 2 })
        RunIsolatedChild($"--native-child-standard-fd-{descriptor}", root);

    var testLeaf = $"linux-boundary-{Guid.NewGuid():N}";
    var testPath = Path.Combine(root, testLeaf);
    var factory = LinuxJournalFileSystemBoundaryFactory.Instance;
    using var boundary = factory.OpenJournalRoot(root, root);
    Assert.True(boundary.Durability.FileContentFlush);
    Assert.True(boundary.Durability.AtomicSameVolumePromotion);
    Assert.True(boundary.Durability.ParentDirectoryFlush);
    var durablePromotion = boundary as IJournalDurableDirectoryPromotion;
    Assert.True(durablePromotion?.DurableDirectoryPromotionSupported == true);
    Assert.Equal(boundary.AuthorizedRootIdentity, boundary.OpenedRootIdentity);

    try
    {
        boundary.CreateDirectory(testLeaf);
        using (var isolatedRoot = factory.OpenJournalRoot(testPath, testPath))
        {
            var invalidRelativePath = "redacted-\ud800";
            var invalidPathException = Assert.Throws<LinuxJournalFileSystemException>(() =>
                isolatedRoot.FileExists(invalidRelativePath));
            Assert.Equal(LinuxJournalIoCause.InvalidRelativePath, invalidPathException.Cause);
            Assert.ExceptionGraphDoesNotContain(
                invalidPathException,
                "redacted-",
                "D800",
                "d800");

            isolatedRoot.CreateDirectory("first");
            Assert.Equal(1, isolatedRoot.EnumerateDirectory(string.Empty, 1).Count);
            isolatedRoot.CreateDirectory("second");
            var refreshed = isolatedRoot.EnumerateDirectory(string.Empty, 2);
            Assert.Equal(2, refreshed.Count);
            Assert.Equal("first", refreshed[0].RelativePath);
            Assert.Equal("second", refreshed[1].RelativePath);

            using (var specialFile = isolatedRoot.CreateNewFile("special.bin", 4096, false))
                specialFile.Flush(flushToDisk: true);
            var specialFilePath = Path.Combine(testPath, "special.bin");
            File.SetUnixFileMode(
                specialFilePath,
                (UnixFileMode)(0x0800 | 0x0180)); // setuid + 0600
            Assert.ThrowsCause(
                LinuxJournalIoCause.PermissionMismatch,
                () => isolatedRoot.FileExists("special.bin"));
            File.SetUnixFileMode(specialFilePath, (UnixFileMode)0x0180);

            isolatedRoot.CreateDirectory("special-dir");
            var specialDirectoryPath = Path.Combine(testPath, "special-dir");
            File.SetUnixFileMode(
                specialDirectoryPath,
                (UnixFileMode)(0x0200 | 0x01c0)); // sticky + 0700
            Assert.ThrowsCause(
                LinuxJournalIoCause.PermissionMismatch,
                () => isolatedRoot.DirectoryExists("special-dir"));
            File.SetUnixFileMode(specialDirectoryPath, (UnixFileMode)0x01c0);
        }

        var work = $"{testLeaf}/work";
        boundary.CreateDirectory(work);
        Assert.True(boundary.DirectoryExists(work));
        Assert.Equal(
            JournalFileSystemEntryKind.Directory,
            boundary.GetEntryKind(work));

        var file = $"{work}/payload.bin";
        var payload = Encoding.UTF8.GetBytes("linux journal boundary");
        using (var output = boundary.CreateNewFile(file, 4096, asynchronous: false))
        {
            output.Write(payload);
            output.Flush(flushToDisk: true);
        }
        Assert.True(boundary.FileExists(file));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(testPath, "work", "payload.bin")));
        using (var input = boundary.OpenReadFile(file, 4096, asynchronous: false, sequentialScan: true))
        using (var memory = new MemoryStream())
        {
            input.CopyTo(memory);
            Assert.SequenceEqual(payload, memory.ToArray());
        }

        var entries = boundary.EnumerateDirectory(work, maximumEntries: 1);
        Assert.Equal(1, entries.Count);
        Assert.Equal(file, entries[0].RelativePath);
        Assert.ThrowsCause(
            LinuxJournalIoCause.EnumerationLimitExceeded,
            () => boundary.EnumerateDirectory(work, maximumEntries: 0));

        var fifoRelativePath = $"{work}/blocking.pipe";
        var fifoExactPath = Path.Combine(testPath, "work", "blocking.pipe");
        LinuxTestNative.CreateFifo(fifoExactPath);
        try
        {
            RunIsolatedChild("--native-child-fifo", root, fifoRelativePath);
        }
        finally
        {
            File.Delete(fifoExactPath);
        }

        var lockPath = $"{work}/writer.lock";
        using (boundary.AcquireExclusiveLock(lockPath))
        {
            Assert.ThrowsCause(
                LinuxJournalIoCause.LockBusy,
                () => boundary.AcquireExclusiveLock(lockPath).Dispose());
        }

        var source = $"{testLeaf}/stage";
        var destination = $"{testLeaf}/pending";
        boundary.CreateDirectory(source);
        durablePromotion!.MoveDirectoryDurably(source, destination);
        Assert.False(boundary.DirectoryExists(source));
        Assert.True(boundary.DirectoryExists(destination));

        var collisionSource = $"{testLeaf}/collision-source";
        var collisionDestination = $"{testLeaf}/collision-destination";
        boundary.CreateDirectory(collisionSource);
        boundary.CreateDirectory(collisionDestination);
        Assert.ThrowsCause(
            LinuxJournalIoCause.AlreadyExists,
            () => durablePromotion.MoveDirectoryDurably(collisionSource, collisionDestination));

        var symlink = Path.Combine(testPath, "work", "link");
        LinuxTestNative.CreateSymbolicLink("payload.bin", symlink);
        try
        {
            Assert.ThrowsCause(
                LinuxJournalIoCause.LinkRejected,
                () => boundary.FileExists($"{work}/link"));
        }
        finally
        {
            File.Delete(symlink);
        }

        var hardLink = Path.Combine(testPath, "work", "hard-link.bin");
        LinuxTestNative.CreateHardLink(Path.Combine(testPath, "work", "payload.bin"), hardLink);
        try
        {
            Assert.ThrowsCause(
                LinuxJournalIoCause.HardLinkRejected,
                () => boundary.FileExists(file));
        }
        finally
        {
            File.Delete(hardLink);
        }

        var legacyRoot = $"{testLeaf}/legacy";
        boundary.CreateDirectory(legacyRoot);
        var legacyFile = $"{legacyRoot}/STATE.md";
        using (var output = boundary.CreateNewFile(legacyFile, 4096, asynchronous: false))
            output.WriteByte(0x41);
        File.SetUnixFileMode(
            Path.Combine(testPath, "legacy", "STATE.md"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var legacyExactRoot = Path.Combine(testPath, "legacy");
        using (var legacy = factory.OpenLegacySourceRoot(legacyExactRoot, legacyExactRoot))
        {
            Assert.Equal(1, legacy.EnumerateDirectory(string.Empty, 1).Count);
            using var input = legacy.OpenReadFile("STATE.md", 4096, false, true);
            Assert.Equal(0x41, input.ReadByte());
        }
    }
    finally
    {
        // This is an explicitly opted-in disposable test subtree; production cleanup continues
        // to use only the handle-relative boundary operations.
        if (Directory.Exists(testPath))
            Directory.Delete(testPath, recursive: true);
    }
}

static int RunNativeChild(string[] arguments)
{
    try
    {
        var root = arguments[1];
        const string standardDescriptorPrefix = "--native-child-standard-fd-";
        if (arguments[0].StartsWith(standardDescriptorPrefix, StringComparison.Ordinal) &&
            int.TryParse(arguments[0].AsSpan(standardDescriptorPrefix.Length), out var descriptor) &&
            descriptor is >= 0 and <= 2)
        {
            LinuxTestNative.CloseDescriptor(descriptor);

            // CoreCLR may reoccupy a closed standard descriptor before the next managed call.
            // The observable contract is that Cortex never takes ownership of that descriptor.
            var baselineRootDescriptors = LinuxTestNative.FindDescriptorsForTarget(root);
            Assert.Equal(0, baselineRootDescriptors.Count);
            using (var boundary = LinuxJournalFileSystemBoundaryFactory.Instance.OpenJournalRoot(root, root))
            {
                Assert.True(boundary.DirectoryExists(string.Empty));
                var rootDescriptors = LinuxTestNative.FindDescriptorsForTarget(root);
                Assert.True(
                    rootDescriptors.Count >= 1 && rootDescriptors.All(value => value >= 3),
                    $"Cortex assigned the journal root to standard descriptor {descriptor}.");
            }
            Assert.Equal(0, LinuxTestNative.FindDescriptorsForTarget(root).Count);
            return 0;
        }

        if (arguments[0] == "--native-child-fifo")
        {
            var relativePath = arguments[2];
            using var boundary = LinuxJournalFileSystemBoundaryFactory.Instance.OpenJournalRoot(root, root);
            Assert.ThrowsCause(
                LinuxJournalIoCause.InvalidEntryType,
                () => boundary.OpenReadFile(relativePath, 4096, false, true).Dispose());
            Assert.ThrowsCause(
                LinuxJournalIoCause.InvalidEntryType,
                () => boundary.AcquireExclusiveLock(relativePath).Dispose());
            return 0;
        }

        return 2;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            exception is LinuxJournalFileSystemException linux
                ? $"Native child failed: {linux.Operation}/{linux.Cause}."
                : $"Native child failed: {exception.GetType().Name}.");
        return 1;
    }
}

static void RunIsolatedChild(params string[] arguments)
{
    var processPath = Environment.ProcessPath ??
        throw new InvalidOperationException("The test host process path is unavailable.");
    var start = new ProcessStartInfo
    {
        FileName = processPath,
        UseShellExecute = false
    };
    if (string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase))
    {
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    }
    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);

    using var process = Process.Start(start) ??
        throw new InvalidOperationException("The isolated native test process could not be started.");
    if (!process.WaitForExit(milliseconds: 5_000))
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
        throw new TimeoutException("The isolated native filesystem regression exceeded five seconds.");
    }
    if (process.ExitCode != 0)
        throw new InvalidOperationException(
            $"The isolated native test '{arguments[0]}' exited with code {process.ExitCode}.");
}

static class LinuxTestNative
{
    private const int BadFileDescriptor = 9;

    public static void CloseDescriptor(int fileDescriptor)
    {
        if (Close(fileDescriptor) == 0)
            return;
        var errno = Marshal.GetLastPInvokeError();
        if (errno != BadFileDescriptor)
            throw new IOException($"Native descriptor close failed with errno {errno}.");
    }

    public static IReadOnlyList<int> FindDescriptorsForTarget(string exactTarget)
    {
        var matches = new List<int>();
        var descriptorPaths = Directory.GetFileSystemEntries("/proc/self/fd");
        foreach (var path in descriptorPaths)
        {
            if (!int.TryParse(Path.GetFileName(path), out var fileDescriptor))
                continue;

            string? linkTarget;
            try
            {
                linkTarget = new FileInfo(path).LinkTarget;
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                // Enumerating /proc/self/fd uses a temporary descriptor that may disappear before
                // its link is read. It cannot be the already-open journal root descriptor.
                continue;
            }

            if (string.Equals(linkTarget, exactTarget, StringComparison.Ordinal))
                matches.Add(fileDescriptor);
        }

        matches.Sort();
        return matches;
    }

    public static void CreateFifo(string path)
    {
        if (MakeFifo(path, 0x0180) != 0)
            throw new IOException($"Native FIFO setup failed with errno {Marshal.GetLastPInvokeError()}.");
    }

    public static void CreateSymbolicLink(string target, string linkPath)
    {
        if (Symlink(target, linkPath) != 0)
            throw new IOException($"Native symlink setup failed with errno {Marshal.GetLastPInvokeError()}.");
    }

    public static void CreateHardLink(string existingPath, string newPath)
    {
        if (Link(existingPath, newPath) != 0)
            throw new IOException($"Native hard-link setup failed with errno {Marshal.GetLastPInvokeError()}.");
    }

    [DllImport("libc", EntryPoint = "symlink", SetLastError = true)]
    private static extern int Symlink(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string target,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string linkPath);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fileDescriptor);

}

static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
            throw new InvalidOperationException(message ?? "Expected true.");
    }

    public static void False(bool condition, string? message = null) =>
        True(!condition, message ?? "Expected false.");

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

    public static void ExceptionGraphDoesNotContain(Exception exception, params string[] forbiddenValues)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var rendered = current.ToString();
            foreach (var forbidden in forbiddenValues)
            {
                False(
                    rendered.Contains(forbidden, StringComparison.Ordinal),
                    "The exception graph disclosed a path-derived value.");
            }
        }
    }

    public static TException Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    public static void ThrowsCause(LinuxJournalIoCause expected, Action action)
    {
        var exception = Throws<LinuxJournalFileSystemException>(action);
        Equal(expected, exception.Cause);
    }
}
