using Pegasus.Cortex.Launcher;

var checks = new (string Name, Action Run)[]
{
    ("machine stable root is derived exactly", MachineStableRootIsExact),
    ("launcher must reside directly in stable root", LauncherMustResideInStableRoot),
    ("custom install root is derived from the launcher", CustomInstallRootIsDerivedFromLauncher),
    ("relative and traversing launcher paths fail closed", UnsafeLauncherPathsFailClosed)
};

foreach (var check in checks)
{
    try
    {
        check.Run();
        Console.WriteLine($"PASS {check.Name}");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            $"FAIL {check.Name} :: {exception.GetType().Name}: {exception.Message}");
        return 1;
    }
}

return 0;

static void MachineStableRootIsExact()
{
    const string programFiles = @"C:\Program Files";
    var expected = Path.GetFullPath(@"C:\Program Files\Pegasus\Cortex");
    var actual = CortexStableLauncher.ResolveStableRoot(programFiles);
    AssertEqual(expected, actual);

    AssertThrows<InvalidOperationException>(
        () => CortexStableLauncher.ResolveStableRoot("relative"));
    AssertThrows<InvalidOperationException>(
        () => CortexStableLauncher.ResolveStableRoot(" "));
}

static void LauncherMustResideInStableRoot()
{
    const string programFiles = @"C:\Program Files";
    var launcher = Path.GetFullPath(
        @"C:\Program Files\Pegasus\Cortex\PegasusCortex.exe");
    var expected = Path.GetFullPath(@"C:\Program Files\Pegasus\Cortex");
    var actual = CortexStableLauncher.ResolveStableRootForLauncher(
        programFiles,
        launcher);
    AssertEqual(expected, actual);

    AssertThrows<InvalidOperationException>(() =>
        CortexStableLauncher.ResolveStableRootForLauncher(
            programFiles,
            @"C:\Program Files\Pegasus\Cortex-shadow\PegasusCortex.exe"));
}

static void UnsafeLauncherPathsFailClosed()
{
    const string programFiles = @"C:\Program Files";
    AssertThrows<InvalidOperationException>(() =>
        CortexStableLauncher.ResolveStableRootForLauncher(
            programFiles,
            @"Pegasus\Cortex\PegasusCortex.exe"));
    AssertThrows<InvalidOperationException>(() =>
        CortexStableLauncher.ResolveStableRootForLauncher(
            programFiles,
            @"C:\Program Files\Pegasus\Cortex\..\Other\PegasusCortex.exe"));
}

static void CustomInstallRootIsDerivedFromLauncher()
{
    var launcher = Path.GetFullPath(@"D:\Applications\Pegasus\Cortex\PegasusCortex.exe");
    var actual = CortexStableLauncher.ResolveInstallRootForLauncher(launcher);
    AssertEqual(Path.GetDirectoryName(launcher)!, actual);

    AssertThrows<InvalidOperationException>(() =>
        CortexStableLauncher.ResolveInstallRootForLauncher(@"Pegasus\Cortex\PegasusCortex.exe"));
}

static void AssertEqual(string expected, string actual)
{
    if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(
        $"Expected {typeof(TException).Name}, but no exception was thrown.");
}
