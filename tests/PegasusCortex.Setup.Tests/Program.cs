using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Product;
using Pegasus.Cortex.Setup;
using System.Security.Principal;
using System.Text;
using System.Security.AccessControl;
using System.Text.Json;

if(args is ["--metadata-read-bounds-only"])
{
    await SignedMetadataReadBoundsTests.RunAsync();
    Console.WriteLine("PASS signed package metadata opened-handle bounds.");
    return 0;
}
if (args is ["--lifecycle-fixture"])
{
    Console.WriteLine("ready");
    Console.Out.Flush();
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

if (args is ["--probe-existing-user-host", var installedRoot])
{
    UserSessionStartupTests.ProbeExistingInstalledReadiness(installedRoot);
    return 0;
}

if (args is ["--user-session-startup-fixture"])
{
    UserSessionStartupTests.Run();
    Console.WriteLine("PASS user_session_startup_focused_fixture (read-only current token and fake policy/JSON fixtures; no installed access or launch)");
    return 0;
}

if (args is ["--execution-preparation-policy-only", var executionEvidence])
{
    VerifyExecutionPreparationPolicy();
    VerifyCapturedExecutionMetadata(executionEvidence);
    Console.WriteLine("PASS Windows TrustedInstaller identity and exact captured ACL policy; no ACL setter or installed operation");
    return 0;
}

if (args is ["--data-parent-only"])
{
    DataRootRelocationTests.VerifyDataParentBoundary();
    return 0;
}

var root = args is ["--execution-preparation-fixture"] or ["--execution-preparation-fixture", _]
    ? Path.Combine(Environment.CurrentDirectory, "build", "q-" + Guid.NewGuid().ToString("N"))
    : Path.Combine(Path.GetTempPath(), "pegasus-cortex-setup-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Exception? primaryFailure = null;
try
{
    if (args is ["--log-retention-only"])
    {
        LogRetentionTests.Run(root);
        return 0;
    }
    if (args is ["--maintenance-safety-only"])
    {
        await MaintenanceSafetyTests.RunAsync(root);
        return 0;
    }
    if (args is ["--data-relocation-only"])
    {
        DataRootRelocationTests.Run(root);
        return 0;
    }
    if (args is ["--state-permissions-only"])
    {
        StatePermissionsTests.Run(root);
        return 0;
    }
    if (args is ["--process-image-only"])
    {
        ProcessImageTests.Run(root);
        return 0;
    }
    if (args is ["--execution-preparation-fixture"] or ["--execution-preparation-fixture", _])
    {
        var fixture = CortexProductLayout.ForIsolatedRoot(
            Path.Combine(root, "p"), "S-1-5-21-1-2-3-1001");
        VerifyExecutionOwnerOnlyPlan(fixture);
        VerifyExecutionPreparationPolicy();
        if (args.Length == 2) VerifyCapturedExecutionMetadata(args[1]);
        VerifyExecutionParentChildAclEffect(root);
        Console.WriteLine("PASS exact-four owner-only plan, synthetic ACL rule admission/rejection, and scratch child ACL preservation");
        return 0;
    }
    // Exercise the existing managed-fragment checks without setup/service lifecycle fixtures.
    if (args is ["--codex-guidance-fixture"])
    {
        await VerifyCodexGuidanceAsync(root);
        Console.WriteLine("Focused Codex guidance fixtures passed (managed boundaries, preservation, creation, migration, replay and malformed/duplicate rejection).");
        return 0;
    }
    if (args is ["--codex-registration-fixture"])
    {
        await VerifyOptionalCodexIntegrationAsync(root);
        Console.WriteLine("Focused Codex registration fixtures passed (shared URL, exact legacy retirement, replay, removal, conflicting routes and byte preservation).");
        return 0;
    }
    var legacyCortex = Path.Combine(root, "legacy-cortex");
    var legacyService = Path.Combine(root, "legacy-service");
    var legacyUser = Path.Combine(root, "legacy-user");
    Directory.CreateDirectory(Path.Combine(legacyCortex, "data"));
    Directory.CreateDirectory(legacyService);
    Directory.CreateDirectory(legacyUser);
    var sourceDatabase = Path.Combine(legacyCortex, "data", "cortex-v2.db");
    using (var connection = new SqliteConnection($"Data Source={sourceDatabase};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version=27; CREATE TABLE retained(id TEXT PRIMARY KEY, body TEXT NOT NULL); INSERT INTO retained VALUES ('r1','line 1' || char(10) || 'Ω');";
        command.ExecuteNonQuery();
    }
    var exactSource = Path.Combine(legacyUser, "workspaces.json");
    await File.WriteAllTextAsync(exactSource, "{\"workspace\":\"C:\\\\CodexProjects\"}\n");

    var layout = CortexProductLayout.ForIsolatedRoot(
        Path.Combine(root, "installed", "Pegasus", "Cortex"),
        "S-1-5-21-100-200-300-1001");
    var plan = CortexStateMigrationPlanV1.Create(
        layout,
        new CortexLegacyStateRootsV1(legacyCortex, legacyService, legacyUser));
    var executor = new CortexStateMigrationExecutor();
    var aclPlan = CortexProductStateProvisioner.Plan(layout);
    Require(aclPlan.Entries.Any(entry => entry.Path == layout.ServiceStateDirectory &&
            entry.Profile == CortexDirectoryAccessProfile.ServicePrivate),
        "The ACL plan omitted the private service state boundary.");
    Require(aclPlan.Entries.Any(entry => entry.Path == layout.UserStateDirectory &&
            entry.OwnerSid == layout.OwnerSid && entry.Profile == CortexDirectoryAccessProfile.ExecutionOwnerPrivate),
        "The ACL plan omitted the SID-owned user state boundary.");
    VerifyExecutionOwnerOnlyPlan(layout);
    VerifyExecutionPreparationPolicy();
    Require(aclPlan.Entries.Any(entry => entry.Path == layout.ToolsDirectory &&
            entry.Profile == CortexDirectoryAccessProfile.ImmutableReadExecute),
        "The ACL plan omitted the immutable product tools boundary.");
    Require(aclPlan.Entries.All(entry =>
        {
            var relative = Path.GetRelativePath(layout.InstallRoot, entry.Path);
            return !Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }),
        "The ACL plan escaped the unified install root.");
    VerifyAclProvisioningWhenElevated(root);
    VerifyServiceRecoveryPolicy();
    VerifyRegistrationCleanupReporting(root);
    VerifyStateRoutingMetadataAccess();
    UserSessionStartupTests.Run();
    InstallerAncestorTests.RunPolicy();
    ObsoleteProgramCleanupTests.Run(root);
    VerifyInstallPlanningAndCommandLine();
    VerifyInstallerStatePolicy();
    VerifySemanticParentPlanAndAdmission(root);
    var first = await executor.ExecuteAsync(plan);
    Require(first.Items.Single(item => item.Role == "cortex-v2").Disposition == CortexStateMigrationDisposition.Migrated,
        "The Cortex database was not migrated on the first pass.");
    Require(first.Items.Single(item => item.Role == "execution-workspaces").Disposition == CortexStateMigrationDisposition.Migrated,
        "The exact workspace file was not migrated on the first pass.");
    Require(await File.ReadAllTextAsync(Path.Combine(layout.UserExecutionDirectory, "workspaces.json")) == await File.ReadAllTextAsync(exactSource),
        "The exact workspace bytes changed during migration.");
    using (var destination = new SqliteConnection($"Data Source={layout.CortexDatabasePath};Mode=ReadOnly;Pooling=False"))
    {
        destination.Open();
        using var command = destination.CreateCommand();
        command.CommandText = "SELECT body FROM retained WHERE id='r1';";
        Require((string?)command.ExecuteScalar() == "line 1\nΩ", "The migrated Cortex row content changed.");
    }

    var second = await executor.ExecuteAsync(plan);
    Require(second.Items.Single(item => item.Role == "cortex-v2").Disposition == CortexStateMigrationDisposition.PreservedExisting,
        "A preexisting admitted Cortex database was not preserved.");
    Require(File.Exists(sourceDatabase), "The original Cortex database was removed.");
    var rejectedStateRemoval = false;
    try
    {
        _ = CortexProductStateRemoval.RemoveAll(layout, "retain-state");
    }
    catch (InvalidOperationException)
    {
        rejectedStateRemoval = true;
    }
    Require(rejectedStateRemoval, "State removal accepted a non-explicit confirmation.");
    var removalLayout = CortexProductLayout.ForIsolatedRoot(
        Path.Combine(root, "state-removal", "Pegasus", "Cortex"),
        "S-1-5-21-100-200-300-1001");
    Directory.CreateDirectory(removalLayout.UserStateDirectory);
    await File.WriteAllTextAsync(Path.Combine(removalLayout.UserStateDirectory, "retained.db"), "fixture");
    var removal = CortexProductStateRemoval.RemoveAll(removalLayout, "remove-all-state");
    Require(removal.Removed && !Directory.Exists(removalLayout.StateDirectory),
        "Explicit isolated state removal did not remove the exact state root.");
    await VerifyCodexGuidanceAsync(root);
    await VerifyOptionalCodexIntegrationAsync(root);
    await VerifyInstallLifecycleAsync(root);
    await VerifyForwardInstallerAsync(root);
    VerifySetupReporter(root);
    Console.WriteLine("Pegasus Cortex setup checks passed (separate locations, immutable-only install, locked database preservation, first-install repair, optional independent Codex integration, explicit migration fixtures)." );
    return 0;
}
catch (Exception failure)
{
    primaryFailure = failure;
    throw;
}
finally
{
    if (Directory.Exists(root))
    {
        if (args is ["--execution-preparation-fixture"] or ["--execution-preparation-fixture", _])
        {
            var scratchParent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "build"));
            var candidate = Path.GetFullPath(root);
            if (!candidate.StartsWith(scratchParent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(candidate).StartsWith("q-", StringComparison.Ordinal) ||
                Path.GetFileName(candidate).Length != 34)
                throw new InvalidOperationException("The focused scratch cleanup path escaped its own build folder.");
        }
        try { Directory.Delete(root, recursive: true); }
        catch (Exception cleanupFailure) when (primaryFailure is not null)
        {
            // Preserve the actual test failure rather than replacing its stack
            // with a secondary cleanup error. Keep diagnostics bounded.
            Console.Error.WriteLine("fixture_cleanup_failed=" + cleanupFailure.GetType().Name);
        }
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void VerifyInstallerStatePolicy()
{
    var now = new DateTimeOffset(2028, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var root = new CortexCertificateWindow(
        CortexProductIdentity.ProdAlphaRootThumbprint,
        new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    var publisher = new CortexCertificateWindow(
        CortexProductIdentity.ProdAlphaPublisherThumbprint,
        root.NotBefore,
        root.NotAfter);
    Require(CortexInstallerState.EvaluateTrust([root], [publisher], false, now) == CortexTrustRecommendation.Current,
        "Current Pegasus trust was incorrectly recommended for replacement.");
    Require(CortexInstallerState.EvaluateTrust([], [], false, now) == CortexTrustRecommendation.Missing,
        "Absent Pegasus trust was not recommended for installation.");
    Require(CortexInstallerState.EvaluateTrust([], [], true, now) == CortexTrustRecommendation.Outdated,
        "Historical Pegasus trust was not recognized as outdated.");

    var nearExpiry = root with { NotAfter = new DateTimeOffset(2028, 6, 1, 0, 0, 0, TimeSpan.Zero) };
    Require(CortexInstallerState.EvaluateTrust([nearExpiry], [publisher with { NotAfter = nearExpiry.NotAfter }], false, now) ==
            CortexTrustRecommendation.Expiring,
        "Trust with less than twenty percent of its original life remaining was not recommended for upgrade.");
    var exactTwentyPercent = root with { NotBefore = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
        NotAfter = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero) };
    var threshold = exactTwentyPercent.NotAfter - TimeSpan.FromTicks(
        (exactTwentyPercent.NotAfter - exactTwentyPercent.NotBefore).Ticks / 5);
    Require(CortexInstallerState.EvaluateTrust([exactTwentyPercent],
            [publisher with { NotBefore = exactTwentyPercent.NotBefore, NotAfter = exactTwentyPercent.NotAfter }], false, threshold) ==
            CortexTrustRecommendation.Current,
        "The exact twenty-percent boundary must not be treated as less than twenty percent.");

    var bits = new CortexInstallerStateV1(true, false, CortexTrustRecommendation.Missing).ExitCode;
    Require(bits == (CortexInstallerStateV1.SuccessMarker | CortexInstallerStateV1.CodexRegisterBit |
                     CortexInstallerStateV1.TrustRecommendedBit | CortexInstallerStateV1.TrustMissingBit),
        "The bounded installer-state exit contract changed.");
}

static void VerifyServiceRecoveryPolicy()
{
    var policy = CortexWindowsProductRegistration.RecoveryPolicyForTest;
    Require(policy.ResetPeriodSeconds == 24*60*60,
        "The service recovery failure counter does not reset after one day.");
    Require(policy.ApplyToNonCrashFailures,
        "The service recovery policy omits reported non-crash failures.");
    Require(policy.Actions.Select(action => action.Type).SequenceEqual([1u,1u,1u]),
        "The service recovery policy contains a non-restart action.");
    Require(policy.Actions.Select(action => action.DelayMilliseconds).SequenceEqual([5_000u,15_000u,60_000u]),
        "The service recovery delays changed from the bounded restart contract.");
}

static void VerifyRegistrationCleanupReporting(string testRoot)
{
    var desired = CortexWindowsProductRegistration.RecoveryPolicyForTest;
    Require((CortexWindowsProductRegistration.RecoveryRestoreAccessForTest(desired) & 0x0010) != 0,
        "Recovery restoration omitted SERVICE_START for restart actions.");
    foreach (var noRestart in new CortexServiceRecoveryPolicy?[]
        { null, desired with { Actions = [] }, desired with { Actions = [new(0, 0)] } })
        Require((CortexWindowsProductRegistration.RecoveryRestoreAccessForTest(noRestart) & 0x0010) == 0,
            "Recovery restoration unnecessarily requested SERVICE_START without restart actions.");

    foreach (var primary in new Exception[]
        {
            new System.ComponentModel.Win32Exception(1067, "primary service startup failure"),
            new InvalidOperationException("primary service stopped failure"),
            new TimeoutException("primary service startup timeout"),
            new IOException("primary registration receipt failure")
        })
    {
        var actions = new List<string>();
        try
        {
            try { throw primary; }
            catch (Exception preserved)
            {
                CortexWindowsProductRegistration.AttemptCleanup(preserved, "restore-service", () =>
                {
                    actions.Add("service");
                    throw new System.ComponentModel.Win32Exception(5, "cleanup private detail must not be retained");
                });
                CortexWindowsProductRegistration.AttemptCleanup(preserved, "restore-product-registration", () => actions.Add("product"));
                CortexWindowsProductRegistration.AttemptCleanup(preserved, "restore-user-run", () =>
                {
                    actions.Add("run"); throw new UnauthorizedAccessException("another cleanup detail");
                });
                CortexWindowsProductRegistration.AttemptCleanup(preserved, "restore-machine-path", () => actions.Add("path"));
                throw;
            }
        }
        catch (Exception observed)
        {
            Require(ReferenceEquals(observed, primary) && observed.StackTrace is not null &&
                    actions.SequenceEqual(["service", "product", "run", "path"]),
                "Cleanup replaced the primary exception or skipped a later existing cleanup action.");
            var failures = CortexWindowsProductRegistration.CleanupFailures(observed);
            Require(failures.Count == 2 && failures[0] is { Stage: "restore-service", Cause: "Win32Exception", Win32: 5 } &&
                    failures[1] is { Stage: "restore-user-run", Cause: "UnauthorizedAccessException", Win32: null },
                "Secondary cleanup diagnostics lost their bounded stage/native-error identity.");
        }
    }
    var reported = new System.ComponentModel.Win32Exception(1067, "primary startup diagnostic");
    var attempts = 0;
    for (var index = 0; index < CortexWindowsProductRegistration.MaximumCleanupFailures + 3; index++)
        CortexWindowsProductRegistration.AttemptCleanup(reported, "restore-service", () =>
        {
            attempts++; throw new System.ComponentModel.Win32Exception(5, "cleanup private detail must not be retained");
        });
    Require(CortexWindowsProductRegistration.CleanupFailures(reported).Count == CortexWindowsProductRegistration.MaximumCleanupFailures &&
            attempts == CortexWindowsProductRegistration.MaximumCleanupFailures + 3,
        "The cleanup diagnostic bound stopped cleanup work or retained unbounded failures.");
    var reporter = CortexSetupReporter.ConsoleOnly().PersistUnder(Path.Combine(testRoot, "registration-cleanup-reporter"));
    reporter.Failure("windows_error", "register-product", null, reported, "Inspect primary failure and cleanup evidence.");
    var failurePath = Directory.EnumerateFiles(Path.GetDirectoryName(reporter.LogPath!)!, "*.failure.json").Single();
    var text = File.ReadAllText(failurePath);
    using var document = System.Text.Json.JsonDocument.Parse(text);
    Require(document.RootElement.GetProperty("diagnostic").GetString() == "primary startup diagnostic" &&
            document.RootElement.GetProperty("cleanup").GetArrayLength() == CortexWindowsProductRegistration.MaximumCleanupFailures &&
            !text.Contains("cleanup private detail", StringComparison.Ordinal),
        "Setup failure reporting replaced the primary diagnostic or disclosed arbitrary cleanup messages.");
}

static void VerifyInstallPlanningAndCommandLine()
{
    Require(CortexProductInstallPlanner.CompareReleaseVersions(
            "1.2026.09.10.12.18.A", "1.2026.09.10.12.17.A") > 0,
        "The install planner did not order a later release after the installed release.");
    Require(CortexProductInstallPlanner.CompareReleaseVersions(
            "1.2026.09.10.12.17.A", "1.2026.09.10.12.17.A") == 0,
        "The install planner did not identify a repair release.");
    Require(CortexProductInstallPlanner.CompareReleaseVersions(
            "1.2026.09.10.12.16.A", "1.2026.09.10.12.17.A") < 0,
        "The install planner did not identify a downgrade.");

    var parsed = CortexSetupCommandLine.ParseInstallPackage(
        ["install-package", @"C:\package", "--install-root", @"D:\Pegasus\Cortex", "--data-root", @"E:\CortexData", "--codex-agents", @"C:\Users\owner\.codex\AGENTS.md"]);
    Require(parsed.PackageRoot == @"C:\package" && parsed.InstallRoot == @"D:\Pegasus\Cortex" &&
            parsed.CodexAgentsPath == @"C:\Users\owner\.codex\AGENTS.md" && parsed.DataRoot == @"E:\CortexData",
        "The setup command line did not preserve explicit install and guidance paths.");
    var legacy = CortexSetupCommandLine.ParseInstallPackage(
        ["install-package", @"C:\package", @"C:\Users\owner\.codex\AGENTS.md"]);
    Require(legacy.InstallRoot is null && legacy.CodexAgentsPath is not null &&
            legacy.CodexRegister is null && legacy.CodexGuidance is null,
        "The setup command line lost legacy guidance-path compatibility.");
    foreach (var register in new[] { false, true })
    foreach (var guidance in new[] { false, true })
    {
        var selection = CortexSetupCommandLine.ParseInstallPackage(
            ["install-package", @"C:\package", "--codex-register", register ? "true" : "false",
             "--codex-guidance", guidance ? "true" : "false", "--codex-home", @"C:\Users\owner\.codex"]);
        Require(selection.CodexRegister == register && selection.CodexGuidance == guidance &&
                selection.CodexHome == @"C:\Users\owner\.codex",
            "The optional Codex selections are not independent or lost their exact home.");
    }
    var actions = CortexSetupCommandLine.ParseInstallPackage(
        ["install-package", @"C:\package", "--codex-register-action", "remove",
         "--codex-guidance-action", "install"]);
    Require(actions.CodexRegisterAction == CortexCodexFeatureAction.Remove &&
            actions.CodexGuidanceAction == CortexCodexFeatureAction.Install,
        "The setup command line lost explicit maintenance feature actions.");
    var registrationFeature = CortexSetupCommandLine.ParseCodexFeature(
        ["configure-codex-feature", @"C:\package", "--install-root", @"C:\Program Files\Pegasus\Cortex",
         "--feature", "registration", "--action", "install", "--codex-home", @"C:\Users\owner\.codex"]);
    Require(registrationFeature.Feature == CortexCodexFeature.Registration &&
            registrationFeature.Action == CortexCodexFeatureAction.Install &&
            registrationFeature.CodexHome == @"C:\Users\owner\.codex" &&
            registrationFeature.CodexAgentsPath is null,
        "The independent Codex registration routine lost its exact typed input.");
    var guidanceFeature = CortexSetupCommandLine.ParseCodexFeature(
        ["configure-codex-feature", @"C:\package", "--install-root", @"C:\Program Files\Pegasus\Cortex",
         "--feature", "guidance", "--action", "remove", "--codex-agents", @"C:\Users\owner\.codex\AGENTS.md"]);
    Require(guidanceFeature.Feature == CortexCodexFeature.Guidance &&
            guidanceFeature.Action == CortexCodexFeatureAction.Remove &&
            guidanceFeature.CodexAgentsPath == @"C:\Users\owner\.codex\AGENTS.md",
        "The independent Codex guidance routine lost its exact typed input.");
    foreach (var invalidFeature in new[]
             {
                 new[] { "configure-codex-feature", @"C:\package", "--install-root", @"C:\Cortex", "--feature", "registration", "--action", "keep" },
                 new[] { "configure-codex-feature", @"C:\package", "--install-root", @"C:\Cortex", "--feature", "other", "--action", "install" },
                 new[] { "configure-codex-feature", @"C:\package", "--feature", "guidance", "--action", "install" },
                 new[] { "configure-codex-feature", @"C:\package", "--install-root", @"C:\Cortex", "--feature", "guidance", "--action", "install", "--action", "remove" }
             })
    {
        try
        {
            _ = CortexSetupCommandLine.ParseCodexFeature(invalidFeature);
            throw new InvalidOperationException("An invalid standalone Codex feature command was accepted.");
        }
        catch (ArgumentException) { }
    }
    foreach (var invalid in new[]
             {
                 new[] { "install-package", @"C:\package", "--data-root", "" },
                 new[] { "install-package", @"C:\package", "--data-root", @"D:\Data", "--data-root", @"E:\Data" },
                 new[] { "install-package", @"C:\package", "--codex-register", "True" },
                 new[] { "install-package", @"C:\package", "--codex-guidance", "1" },
                 new[] { "install-package", @"C:\package", "--codex-register", "false", "--codex-register", "true" },
                 new[] { "install-package", @"C:\package", "--codex-guidance", "false", "--codex-guidance", "true" },
                 new[] { "install-package", @"C:\package", "--codex-home", "" },
                 new[] { "install-package", @"C:\package", "--codex-home", @"C:\One", "--codex-home", @"C:\Two" },
                 new[] { "install-package", @"C:\package", "--codex-register-action", "disable" },
                 new[] { "install-package", @"C:\package", "--codex-register", "true", "--codex-register-action", "install" },
                 new[] { "install-package", @"C:\package", "--codex-register" },
                 new[] { "install-package", @"C:\package", "--codex-other", "true" }
             })
    {
        try
        {
            _ = CortexSetupCommandLine.ParseInstallPackage(invalid);
            throw new InvalidOperationException("An invalid, repeated, or incomplete setup option was accepted.");
        }
        catch (ArgumentException) { }
    }

    var previousOwned = new CortexWindowsRegistrationReceiptV1(
        CortexWindowsProductRegistration.ReceiptContract, 1,
        "1.2026.09.10.12.17.A", CortexProductIdentity.ServiceName,
        @"C:\Program Files\Pegasus\Cortex\versions\execution\1.2026.09.10.12.17.A\service\PegasusCortex.ExecutionService.exe",
        true, @"C:\Program Files\Pegasus\Cortex\bin", true,
        "Pegasus Cortex",
        @"C:\Program Files\Pegasus\Cortex\versions\execution\1.2026.09.10.12.17.A\host\PegasusCortex.Host.exe",
        true, true, null);
    var carried = CortexWindowsProductRegistration.CarryForwardOwnership(
        previousOwned, pathChanged: false, runChanged: true,
        originalRun: "\"C:\\Program Files\\Pegasus\\Cortex\\versions\\execution\\1.2026.09.10.12.17.A\\host\\PegasusCortex.Host.exe\"");
    Require(carried.PathEntryAdded && carried.RunValueCreated && carried.RunValueChanged &&
            carried.PreviousRunValue is null,
        "An upgrade did not carry the original PATH and Run-value ownership forward.");

    const string manualRun = @"C:\Tools\ManualTray.exe";
    var preservedManual = CortexWindowsProductRegistration.CarryForwardOwnership(
        previous: null, pathChanged: false, runChanged: true, originalRun: manualRun);
    Require(!preservedManual.PathEntryAdded && !preservedManual.RunValueCreated &&
            preservedManual.RunValueChanged && preservedManual.PreviousRunValue == manualRun,
        "Registration ownership did not preserve a preexisting manual Run value.");
}

static void VerifySetupReporter(string testRoot)
{
    if (OperatingSystem.IsWindows())
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            Require(CortexSetupReporter.ConsoleOnly().PersistForElevatedRegisteredOperation(testRoot).LogPath is null,
                "Non-elevated user startup unexpectedly required protected installation log access.");
    }
    var state = Path.Combine(testRoot, "reporter");
    var originalOutput = Console.Out;
    var visibleOutput = new StringWriter();
    try
    {
        Console.SetOut(visibleOutput);
        CortexSetupReporter.ConsoleOnly().Stage("verify", "complete", "fixture");
    }
    finally
    {
        Console.SetOut(originalOutput);
    }
    Require(visibleOutput.ToString().Contains("stage=verify status=complete", StringComparison.Ordinal) &&
            !visibleOutput.ToString().Contains("rid=", StringComparison.Ordinal),
        "Normal setup progress still exposes the internal transaction identifier.");
    var reporter = CortexSetupReporter.ConsoleOnly().PersistUnder(state);
    reporter.Stage("preflight", "complete", "mode=repair");
    reporter.Failure("io", "register-product", null, new IOException("fixture failure"), "retry");
    Require(reporter.LogPath is not null && File.Exists(reporter.LogPath),
        "The setup reporter did not persist its bounded stage log.");
    var failure = Directory.EnumerateFiles(Path.Combine(state, "logs"), "*.failure.json").Single();
    Require(File.ReadAllText(failure).Contains("register-product", StringComparison.Ordinal),
        "The setup reporter did not persist its bounded failure stage.");
    var cleanup = new CortexObsoleteProgramCleanupException(
        new CortexObsoleteProgramCleanupReceiptV1(CortexObsoleteProgramCleanup.Contract, 1,
            testRoot, "1.2026.09.14.01.55.A", 2, 1, 0, 0, 0),
        "remove-file", new System.ComponentModel.Win32Exception(32, "fixture locked file"));
    reporter.Failure("io", cleanup.Stage, null, cleanup, "retry");
    using var receipt = System.Text.Json.JsonDocument.Parse(File.ReadAllText(failure));
    var detail = receipt.RootElement.GetProperty("programCleanup");
    Require(detail.GetProperty("stage").GetString() == "remove-file" &&
            detail.GetProperty("cause").GetString() == "Win32Exception" &&
            detail.GetProperty("win32").GetInt32() == 32 && detail.GetProperty("removed").GetInt32() == 2,
        "Setup diagnostics lost the cleanup stage, native cause, or partial-removal count.");
}

static void VerifyExecutionOwnerOnlyPlan(CortexProductLayout layout)
{
    var paths = new[] { layout.UserStateDirectory, layout.UserExecutionDirectory,
        layout.UserLogDirectory, layout.TrayStateDirectory };
    var plan = CortexProductStateProvisioner.Plan(layout);
    Require(paths.All(path => plan.Entries.Count(entry => entry.Path == path &&
                entry.Profile == CortexDirectoryAccessProfile.ExecutionOwnerPrivate) == 1),
        "The four strict host directories were not selected exactly once.");
    foreach (var path in paths)
    {
        var security = CortexProductStateProvisioner.DescribeDirectorySecurity(new(
            path, layout.OwnerSid, CortexDirectoryAccessProfile.ExecutionOwnerPrivate));
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        Require(security.AreAccessRulesProtected && rules.Length == 3 &&
                rules.Select(rule => rule.IdentityReference.Value).Order().SequenceEqual(
                    new[] { layout.OwnerSid, "S-1-5-18", "S-1-5-32-544" }.Order()) &&
                rules.All(rule => rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl),
            "The proposed fresh execution state grants an unexpected principal.");
    }
    Require(plan.Entries.Single(entry => entry.Path == layout.CortexDataDirectory).Profile ==
            CortexDirectoryAccessProfile.UserPrivate,
        "The semantic database sibling profile changed.");
}

static void VerifyExecutionPreparationPolicy()
{
    const string owner = "S-1-5-21-1-2-3-1001";
    const string installer = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
    var full = (int)FileSystemRights.FullControl;
    var read = (int)(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);
    bool Admits(string sid, int mask, bool allow = true, bool inherited = true,
        InheritanceFlags flags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) =>
        CortexExecutionStatePreparation.SupportedInheritedRule(sid, mask, allow, inherited,
            flags, PropagationFlags.None, owner, installer);
    Require(Admits(owner, full) && Admits("S-1-5-18", full) &&
            Admits("S-1-5-32-544", full) && Admits(installer, full, flags: InheritanceFlags.ContainerInherit) &&
            Admits("S-1-15-2-1", read) && Admits("S-1-15-2-2", read),
        "The exact reviewed inherited ACL rule classes were rejected.");
    Require(!Admits("S-1-5-32-545", full) && !Admits(owner, full, inherited: false) &&
            !Admits(owner, full, allow: false) && !Admits(owner, read) &&
            !Admits(installer, full, flags: inherit) && !Admits("S-1-15-2-1", full) &&
            !Admits("S-1-5-80-3097650959-1024610640-97228161-1996064048-742836981", full,
                flags: InheritanceFlags.ContainerInherit),
        "A foreign, explicit, denied, or overbroad ACL rule was admitted.");
}

static void VerifyCapturedExecutionMetadata(string evidencePath)
{
    using var document = JsonDocument.Parse(File.ReadAllBytes(evidencePath));
    var root = document.RootElement;
    Require(root.GetProperty("schema").GetString() == "pegasus.cortex.setup-fixed-four-metadata/1",
        "The selected metadata fixture has another schema.");
    var owner = root.GetProperty("ownerSid").GetString()!;
    var directories = root.GetProperty("directories");
    Require(directories.GetArrayLength() == 4, "The selected metadata fixture is not the fixed four.");
    var service = CortexExecutionStatePreparation.ResolveInheritedInstallerSid();
    Require(service == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
        "The production resolver must identify Windows TrustedInstaller rather than the Cortex service.");
    foreach (var directory in directories.EnumerateArray())
    {
        Require(directory.GetProperty("ownerSid").GetString() == owner &&
                !directory.GetProperty("protected").GetBoolean() &&
                directory.GetProperty("linkTarget").ValueKind == JsonValueKind.Null,
            "The captured directory owner, inheritance, or link state changed.");
        var rules = directory.GetProperty("rules");
        Require(rules.GetArrayLength() == 6, "The captured ACL is not the reviewed six-rule profile.");
        foreach (var rule in rules.EnumerateArray())
        {
            var inheritance = Enum.Parse<InheritanceFlags>(rule.GetProperty("inheritance").GetString()!);
            var propagation = Enum.Parse<PropagationFlags>(rule.GetProperty("propagation").GetString()!);
            Require(CortexExecutionStatePreparation.SupportedInheritedRule(
                rule.GetProperty("sid").GetString()!, rule.GetProperty("mask").GetInt32(),
                rule.GetProperty("type").GetString() == "Allow",
                rule.GetProperty("inherited").GetBoolean(), inheritance, propagation, owner, service),
                "A captured inherited rule is outside the reviewed preparation policy.");
        }
    }
}

static void VerifyExecutionParentChildAclEffect(string testRoot)
{
    if (!OperatingSystem.IsWindows()) return;
    var owner = WindowsIdentity.GetCurrent().User?.Value ??
        throw new InvalidOperationException("Scratch owner missing.");
    var parent = Path.Combine(testRoot, "acl-parent");
    Directory.CreateDirectory(parent);
    var child = Path.Combine(parent, "retained-child.txt");
    File.WriteAllText(child, "scratch retained child");
    var originalChildAcl = new FileInfo(child).GetAccessControl().GetSecurityDescriptorBinaryForm();
    var originalBytes = File.ReadAllBytes(child);
    using (var lockedChild = new FileStream(child, FileMode.Open, FileAccess.Read, FileShare.None))
    using (var pinnedParent = CortexExecutionStatePreparation.OpenPinnedDirectory(parent))
        CortexExecutionStatePreparation.SetOwnerOnlyAcl(pinnedParent, parent, owner, 0);
    var currentParent = new DirectoryInfo(parent).GetAccessControl();
    var rules = currentParent.GetAccessRules(true, true, typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>().ToArray();
    Require(currentParent.AreAccessRulesProtected &&
            currentParent.GetOwner(typeof(SecurityIdentifier))?.Value == owner &&
            rules.Length == 1 && rules[0].IdentityReference.Value == owner &&
            File.ReadAllBytes(child).AsSpan().SequenceEqual(originalBytes) &&
            new FileInfo(child).GetAccessControl().GetSecurityDescriptorBinaryForm()
                .AsSpan().SequenceEqual(originalChildAcl),
        "The exact-directory native ACL setter changed a locked existing child or failed owner-only admission.");
}

static void VerifyAclProvisioningWhenElevated(string testRoot)
{
    if (!OperatingSystem.IsWindows())
        return;

    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        return;

    var ownerSid = identity.User?.Value ?? throw new InvalidOperationException("Current test SID unavailable.");
    var layout = CortexProductLayout.ForIsolatedRoot(
        Path.Combine(testRoot, "acl-provisioning", "Pegasus", "Cortex"), ownerSid);
    CortexProductStateProvisioner.Provision(layout);
    CortexProductStateProvisioner.Provision(layout);
    foreach (var path in new[] { layout.UserStateDirectory, layout.UserExecutionDirectory,
                 layout.UserLogDirectory, layout.TrayStateDirectory })
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        Require(security.AreAccessRulesProtected &&
                security.GetOwner(typeof(SecurityIdentifier))?.Value == ownerSid &&
                rules.Length == 3 && rules.Select(rule => rule.IdentityReference.Value).Order().SequenceEqual(
                    new[] { ownerSid, "S-1-5-18", "S-1-5-32-544" }.Order()) &&
                rules.All(rule => rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl),
            "Fresh execution state omitted ordinary-owner, SYSTEM or Administrator access.");
    }
    var usersBoundary = Path.Combine(layout.StateDirectory, "users");
    var usersRule = new DirectoryInfo(usersBoundary).GetAccessControl()
        .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
        .OfType<System.Security.AccessControl.FileSystemAccessRule>()
        .Single(rule => rule.IdentityReference is SecurityIdentifier sid &&
            sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid));
    Require((usersRule.FileSystemRights & System.Security.AccessControl.FileSystemRights.ReadAttributes) != 0,
        "The shared user-state boundary does not permit no-follow attribute admission.");
    var nestedDirectory = Path.Combine(layout.ServiceStateDirectory, "databases");
    var nestedFile = Path.Combine(nestedDirectory, "router.db");
    Directory.CreateDirectory(nestedDirectory);
    File.WriteAllText(nestedFile, "fixture");
    var legacyUsers = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
    var legacyDirectorySecurity = new DirectoryInfo(nestedDirectory).GetAccessControl();
    legacyDirectorySecurity.AddAccessRule(new FileSystemAccessRule(
        legacyUsers, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.None, AccessControlType.Allow));
    new DirectoryInfo(nestedDirectory).SetAccessControl(legacyDirectorySecurity);
    var legacyFileSecurity = new FileInfo(nestedFile).GetAccessControl();
    legacyFileSecurity.AddAccessRule(new FileSystemAccessRule(
        legacyUsers, FileSystemRights.Read | FileSystemRights.Synchronize,
        AccessControlType.Allow));
    new FileInfo(nestedFile).SetAccessControl(legacyFileSecurity);
    var nestedBytes = File.ReadAllBytes(nestedFile);
    var nestedModified = File.GetLastWriteTimeUtc(nestedFile);
    CortexProductStateProvisioner.SealServiceState(layout);
    Require(Directory.Exists(layout.ServiceStateDirectory),
        "The elevated ACL fixture did not provision the SYSTEM-owned service state directory.");
    Require(Directory.Exists(layout.UserExecutionDirectory),
        "The elevated ACL fixture did not provision the owner-SID execution state directory.");
    Require(new DirectoryInfo(nestedDirectory).GetAccessControl()
            .GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier directoryOwner &&
            directoryOwner.IsWellKnown(WellKnownSidType.LocalSystemSid),
        "The elevated ACL fixture did not seal the migrated service directory to SYSTEM ownership.");
    Require(new FileInfo(nestedFile).GetAccessControl()
            .GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier fileOwner &&
            fileOwner.IsWellKnown(WellKnownSidType.LocalSystemSid),
        "The elevated ACL fixture did not seal the migrated service file to SYSTEM ownership.");
    Require(!new DirectoryInfo(nestedDirectory).GetAccessControl()
                .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Any(rule => rule.IdentityReference == legacyUsers) &&
            !new FileInfo(nestedFile).GetAccessControl()
                .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Any(rule => rule.IdentityReference == legacyUsers) &&
            File.ReadAllBytes(nestedFile).AsSpan().SequenceEqual(nestedBytes) &&
            File.GetLastWriteTimeUtc(nestedFile) == nestedModified,
        "Service-state ACL upgrade changed retained database bytes/timestamp or preserved a legacy Users rule.");

    var semanticParent = Path.Combine(layout.DataRoot, "users", ownerSid, "cortex");
    var legacy = new DirectorySecurity();
    legacy.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
    legacy.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
    foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544", ownerSid })
        legacy.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    new DirectoryInfo(semanticParent).SetAccessControl(legacy);
    var parentSecurity = new DirectoryInfo(semanticParent).GetAccessControl().GetSecurityDescriptorBinaryForm();
    var child = Path.Combine(semanticParent, "retained-child.fixture");
    File.WriteAllText(child, "retained fixture bytes");
    var childSecurity = new FileInfo(child).GetAccessControl().GetSecurityDescriptorBinaryForm();
    var dataSecurity = new DirectoryInfo(layout.CortexDataDirectory).GetAccessControl().GetSecurityDescriptorBinaryForm();
    using (var locked = new FileStream(child, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        CortexProductStateProvisioner.Provision(layout);
    var repaired = new DirectoryInfo(semanticParent).GetAccessControl();
    Require(repaired.GetSecurityDescriptorBinaryForm().AsSpan().SequenceEqual(parentSecurity) &&
            File.ReadAllText(child) == "retained fixture bytes" &&
            new FileInfo(child).GetAccessControl().GetSecurityDescriptorBinaryForm().AsSpan().SequenceEqual(childSecurity) &&
            new DirectoryInfo(layout.CortexDataDirectory).GetAccessControl().GetSecurityDescriptorBinaryForm().AsSpan().SequenceEqual(dataSecurity),
        "Valid existing semantic-parent admission changed its owner, access list or retained children.");
    var repairedBytes = repaired.GetSecurityDescriptorBinaryForm();
    CortexProductStateProvisioner.Provision(layout);
    Require(new DirectoryInfo(semanticParent).GetAccessControl().GetSecurityDescriptorBinaryForm().AsSpan().SequenceEqual(repairedBytes),
        "An owner-correct semantic parent was re-permissioned on repair replay.");
}

static void VerifySemanticParentPlanAndAdmission(string testRoot)
{
    const string sid = "S-1-5-21-100-200-300-1001";
    var legacy = CortexProductLayout.ForIsolatedRoot(Path.Combine(testRoot, "semantic-plan-program"), sid);
    var external = CortexProductLayout.ForIsolatedRoots(Path.Combine(testRoot, "semantic-external-program"),
        Path.Combine(testRoot, "semantic-external-data"), sid);
    foreach (var layout in new[] { legacy, external })
    {
        var parent = Path.Combine(layout.DataRoot, "users", sid, "cortex");
        var entries = CortexProductStateProvisioner.Plan(layout).Entries;
        Require(entries.Count(entry => entry.Path == parent && entry.OwnerSid == sid &&
                    entry.Profile == CortexDirectoryAccessProfile.UserPrivate) == 1 &&
                entries.Select(entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == entries.Count,
            "The exact semantic parent is missing or duplicated for a supported data placement.");
    }
    DirectorySecurity Security(string owner, bool addExpectedUser = true)
    {
        var value = new DirectorySecurity(); value.SetOwner(new SecurityIdentifier(owner));
        foreach (var principal in new[] { "S-1-5-18", "S-1-5-32-544" }.Concat(addExpectedUser ? new[] { sid } : []))
            value.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(principal), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return value;
    }
    Require(CortexProductStateProvisioner.RequireLegacySemanticParentAdmission(Security("S-1-5-32-544"), sid) &&
            CortexProductStateProvisioner.RequireLegacySemanticParentAdmission(Security("S-1-5-18"), sid) &&
            !CortexProductStateProvisioner.RequireLegacySemanticParentAdmission(Security(sid), sid),
        "Semantic-parent admission did not distinguish legacy installer ownership from an unchanged correct owner.");
    var foreignRule = Security("S-1-5-32-544");
    foreignRule.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
    var denied = Security("S-1-5-32-544");
    denied.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.Write, AccessControlType.Deny));
    foreach (var invalid in new[] { Security("S-1-5-21-100-200-300-1002"), Security("S-1-5-32-544", false), foreignRule, denied })
    {
        try
        {
            _ = CortexProductStateProvisioner.RequireLegacySemanticParentAdmission(invalid, sid);
            throw new InvalidOperationException("An unowned or insufficiently bound semantic parent was accepted for repair.");
        }
        catch (UnauthorizedAccessException) { }
    }
}

static async Task VerifyCodexGuidanceAsync(string testRoot)
{
    var sourceRoot = FindSourceRoot();
    var guidanceRoot = Path.Combine(
        sourceRoot, "integrations", "codex", "pegasus-cortex-client", "guidance");
    var cortex = Path.Combine(guidanceRoot, "pegasus-cortex-agents.v2.md");
    var pao = Path.Combine(guidanceRoot, "pegasus-cortex-pao.v1.md");
    var targetDirectory = Path.Combine(testRoot, "codex-guidance");
    Directory.CreateDirectory(targetDirectory);
    var target = Path.Combine(targetDirectory, "AGENTS.md");
    var legacy = string.Join("\r\n", new[]
    {
        "# Local authority", "", "Keep this exact Ω line.", "",
        "## Pegasus AgentOps (PAO)", "",
        "- For safe local operations, use the installed",
        "  `pegasus_agentops` MCP tool `pao` as the first-choice typed layer.",
        "- Legacy details are replaced as one owned section.", "",
        "## Unrelated project rules", "", "Preserve this exact body and order.", ""
    });
    await File.WriteAllTextAsync(target, legacy, new UTF8Encoding(false));
    var first = CortexCodexGuidanceInstaller.Apply(target, cortex, pao);
    var firstBytes = await File.ReadAllBytesAsync(target);
    var firstText = new UTF8Encoding(false, true).GetString(firstBytes);
    Require(
        first.Disposition == "updated" && first.LegacyAgentOpsSectionReplaced &&
        first.Version == 1 &&
        firstText.StartsWith("## Pegasus Cortex continuity\r\n", StringComparison.Ordinal) &&
        firstText.Contains("<!-- PEGASUS:CORTEX:PAO:MANAGED:v1 -->", StringComparison.Ordinal) &&
        firstText.Contains("End of Pegasus Cortex continuity", StringComparison.Ordinal) &&
        !firstText.Contains("PEGASUS:CORTEX:MANAGED", StringComparison.Ordinal) &&
        firstText.Contains("Use the live `pao` tool schema exposed by the configured shared Pegasus Cortex MCP connection.", StringComparison.Ordinal) &&
        !firstText.Contains("`pegasus_cortex`", StringComparison.Ordinal) &&
        !firstText.Contains("`pegasus_agentops` MCP tool", StringComparison.Ordinal) &&
        firstText.Contains("Keep this exact Ω line.\r\n", StringComparison.Ordinal) &&
        firstText.Contains("## Unrelated project rules\r\n\r\nPreserve this exact body and order.", StringComparison.Ordinal),
        "Managed Codex guidance did not replace only the legacy route while preserving unrelated content.");

    var second = CortexCodexGuidanceInstaller.Apply(target, cortex, pao);
    var secondBytes = await File.ReadAllBytesAsync(target);
    Require(
        second.Disposition == "unchanged" &&
        firstBytes.AsSpan().SequenceEqual(secondBytes) &&
        first.ResultSha256 == second.ResultSha256,
        "Managed Codex guidance replay was not byte-idempotent.");

    var createdProfile = Path.Combine(testRoot, "codex-guidance-created", "fixture-user");
    var createdDirectory = Path.Combine(createdProfile, ".codex");
    Directory.CreateDirectory(createdDirectory);
    var createdTarget = CortexCodexGuidanceInstaller.ResolveDefaultTarget(createdProfile);
    var created = CortexCodexGuidanceInstaller.Apply(createdTarget, cortex, pao);
    var createdText = await File.ReadAllTextAsync(createdTarget);
    Require(
        created.Disposition == "created" &&
        createdTarget == Path.Combine(createdProfile, ".codex", "AGENTS.md") &&
        createdText.StartsWith("## Pegasus Cortex continuity", StringComparison.Ordinal) &&
        createdText.Contains("End of Pegasus Cortex continuity", StringComparison.Ordinal) &&
        !createdText.Contains("PEGASUS:CORTEX:MANAGED", StringComparison.Ordinal),
        "Managed Codex guidance did not resolve and create the installing user's global Codex target.");

    var legacyCortexTarget = Path.Combine(targetDirectory, "legacy-cortex", "AGENTS.md");
    Directory.CreateDirectory(Path.GetDirectoryName(legacyCortexTarget)!);
    var legacyCortex = "# Preserved\n\n<!-- PEGASUS:CORTEX:MANAGED:v2 -->\n## Old Cortex\nold body\n<!-- /PEGASUS:CORTEX:MANAGED:v2 -->\n\nKeep this.\n";
    await File.WriteAllTextAsync(legacyCortexTarget, legacyCortex, new UTF8Encoding(false));
    _ = CortexCodexGuidanceInstaller.Apply(legacyCortexTarget, cortex, pao);
    var migratedText = await File.ReadAllTextAsync(legacyCortexTarget);
    Require(
        migratedText.StartsWith("## Pegasus Cortex continuity\n", StringComparison.Ordinal) &&
        migratedText.Contains("End of Pegasus Cortex continuity", StringComparison.Ordinal) &&
        !migratedText.Contains("PEGASUS:CORTEX:MANAGED", StringComparison.Ordinal) &&
        migratedText.Contains("# Preserved\n", StringComparison.Ordinal) &&
        migratedText.Contains("Keep this.\n", StringComparison.Ordinal),
        "Legacy Cortex managed guidance was not replaced by one stable top-of-file block.");

    // Captured managed blocks only: no live Codex files are read by this fixture.
    // The legacy v2 body shares the new heading but has only comment boundaries.
    var observedLegacy = await File.ReadAllTextAsync(Path.Combine(sourceRoot,
        "tests", "PegasusCortex.Setup.Tests", "codex-legacy-managed-v2.fixture.txt"));
    foreach (var (lineEnding, bom) in new[] { ("\n", false), ("\r\n", true) })
    {
        var observedTarget = Path.Combine(targetDirectory, bom ? "observed-bom" : "observed-lf", "AGENTS.md");
        Directory.CreateDirectory(Path.GetDirectoryName(observedTarget)!);
        var observedText = observedLegacy.Replace("\n", lineEnding, StringComparison.Ordinal);
        await File.WriteAllTextAsync(observedTarget, observedText, new UTF8Encoding(bom));
        var migrated = CortexCodexGuidanceInstaller.Apply(observedTarget, cortex, pao);
        var resultBytes = await File.ReadAllBytesAsync(observedTarget);
        var resultText = await File.ReadAllTextAsync(observedTarget);
        var firstHuman = "# Human fixture authority" + lineEnding + "Keep this exact Ω instruction.";
        var middleHuman = "## Unrelated middle" + lineEnding + "Keep this exact middle body.";
        var lastHuman = "## Unrelated ending" + lineEnding + "Keep this exact ending body." + lineEnding;
        Require(migrated.Disposition == "updated" && !migrated.LegacyAgentOpsSectionReplaced &&
            resultBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) == bom &&
            resultText.StartsWith("## Pegasus Cortex continuity" + lineEnding, StringComparison.Ordinal) &&
            !resultText.Contains("PEGASUS:CORTEX:MANAGED:", StringComparison.Ordinal) &&
            resultText.Contains(firstHuman, StringComparison.Ordinal) &&
            resultText.Contains(middleHuman, StringComparison.Ordinal) &&
            resultText.EndsWith(lastHuman, StringComparison.Ordinal) &&
            resultText.IndexOf(firstHuman, StringComparison.Ordinal) < resultText.IndexOf(middleHuman, StringComparison.Ordinal) &&
            resultText.IndexOf(middleHuman, StringComparison.Ordinal) < resultText.IndexOf(lastHuman, StringComparison.Ordinal),
            "Observed legacy Cortex/PAO blocks failed migration or changed unrelated instructions, encoding, or order.");
        var replay = CortexCodexGuidanceInstaller.Apply(observedTarget, cortex, pao);
        var replayBytes = await File.ReadAllBytesAsync(observedTarget);
        Require(replay.Disposition == "unchanged" &&
            resultBytes.AsSpan().SequenceEqual(replayBytes),
            "Observed legacy guidance migration was not byte-idempotent.");
        var removeTarget = Path.Combine(targetDirectory, bom ? "observed-remove-bom" : "observed-remove-lf", "AGENTS.md");
        Directory.CreateDirectory(Path.GetDirectoryName(removeTarget)!);
        await File.WriteAllTextAsync(removeTarget, observedText, new UTF8Encoding(bom));
        _ = CortexCodexGuidanceInstaller.PrepareRemove(removeTarget).Apply();
        var removedBytes = await File.ReadAllBytesAsync(removeTarget);
        var removedText = await File.ReadAllTextAsync(removeTarget);
        Require(removedBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) == bom &&
            !removedText.Contains("PEGASUS:CORTEX:", StringComparison.Ordinal) &&
            removedText.Contains(firstHuman, StringComparison.Ordinal) &&
            removedText.Contains(middleHuman, StringComparison.Ordinal) &&
            removedText.EndsWith(lastHuman, StringComparison.Ordinal),
            "Observed legacy removal failed or changed unrelated instructions or encoding.");
    }

    var observedCortex = observedLegacy[observedLegacy.IndexOf("<!-- PEGASUS:CORTEX:MANAGED:v2 -->", StringComparison.Ordinal)..];
    observedCortex = observedCortex[..(observedCortex.IndexOf("<!-- /PEGASUS:CORTEX:MANAGED:v2 -->", StringComparison.Ordinal) +
        "<!-- /PEGASUS:CORTEX:MANAGED:v2 -->".Length)];
    var currentCortex = await File.ReadAllTextAsync(cortex);
    var rejectedLegacyInputs = new[]
    {
        observedCortex + "\n" + observedCortex,
        observedCortex.Replace("<!-- /PEGASUS:CORTEX:MANAGED:v2 -->", observedCortex + "\n<!-- /PEGASUS:CORTEX:MANAGED:v2 -->", StringComparison.Ordinal),
        observedCortex.Replace("<!-- /PEGASUS:CORTEX:MANAGED:v2 -->", "<!-- /PEGASUS:CORTEX:MANAGED:v1 -->", StringComparison.Ordinal),
        observedCortex.Replace("<!-- /PEGASUS:CORTEX:MANAGED:v2 -->", "", StringComparison.Ordinal),
        observedCortex.Replace("## Pegasus Cortex continuity", "## Pegasus Cortex continuity\n## Pegasus Cortex continuity", StringComparison.Ordinal),
        observedCortex.Replace("<!-- /PEGASUS:CORTEX:MANAGED:v2 -->", "End of Pegasus Cortex continuity\n<!-- /PEGASUS:CORTEX:MANAGED:v2 -->", StringComparison.Ordinal),
        observedCortex + "\n" + currentCortex,
        observedCortex + "\n## Pegasus Cortex continuity\n",
        observedCortex + "\nEnd of Pegasus Cortex continuity\n"
    };
    foreach (var index in Enumerable.Range(0, rejectedLegacyInputs.Length))
    {
        var rejectedTarget = Path.Combine(targetDirectory, "observed-reject-" + index, "AGENTS.md");
        Directory.CreateDirectory(Path.GetDirectoryName(rejectedTarget)!);
        var original = Encoding.UTF8.GetBytes(rejectedLegacyInputs[index]);
        await File.WriteAllBytesAsync(rejectedTarget, original);
        try
        {
            _ = CortexCodexGuidanceInstaller.Apply(rejectedTarget, cortex, pao);
            throw new InvalidOperationException("Malformed, duplicate, or mixed observed legacy guidance was accepted: " + index);
        }
        catch (InvalidDataException)
        {
            var afterRejection = await File.ReadAllBytesAsync(rejectedTarget);
            Require(original.AsSpan().SequenceEqual(afterRejection),
                "Rejected observed legacy guidance changed its preimage.");
        }
        try
        {
            _ = CortexCodexGuidanceInstaller.PrepareRemove(rejectedTarget);
            throw new InvalidOperationException("Malformed, duplicate, or mixed legacy removal was accepted: " + index);
        }
        catch (InvalidDataException)
        {
            var afterRejection = await File.ReadAllBytesAsync(rejectedTarget);
            Require(original.AsSpan().SequenceEqual(afterRejection),
                "Rejected observed legacy removal changed its preimage.");
        }
    }

    var legacyFragment = Path.Combine(targetDirectory, "legacy-as-fragment.md");
    await File.WriteAllTextAsync(legacyFragment, observedCortex, new UTF8Encoding(false));
    var fragmentTarget = Path.Combine(targetDirectory, "legacy-as-fragment", "AGENTS.md");
    Directory.CreateDirectory(Path.GetDirectoryName(fragmentTarget)!);
    const string fragmentTargetOriginal = "# Human authority\nPreserve this body.\n";
    await File.WriteAllTextAsync(fragmentTarget, fragmentTargetOriginal, new UTF8Encoding(false));
    try
    {
        _ = CortexCodexGuidanceInstaller.Apply(fragmentTarget, legacyFragment, pao);
        throw new InvalidOperationException("Legacy comment boundaries were accepted as a new stable fragment.");
    }
    catch (InvalidDataException)
    {
        Require(await File.ReadAllTextAsync(fragmentTarget) == fragmentTargetOriginal,
            "Rejected legacy fragment changed its target.");
    }

    var malformedTarget = Path.Combine(targetDirectory, "malformed", "AGENTS.md");
    Directory.CreateDirectory(Path.GetDirectoryName(malformedTarget)!);
    var malformed = "prefix\n## Pegasus Cortex continuity\nmissing end\n";
    await File.WriteAllTextAsync(malformedTarget, malformed, new UTF8Encoding(false));
    try
    {
        _ = CortexCodexGuidanceInstaller.Apply(malformedTarget, cortex, pao);
        throw new InvalidOperationException("Malformed managed guidance markers were accepted.");
    }
    catch (InvalidDataException)
    {
        Require(
            await File.ReadAllTextAsync(malformedTarget) == malformed,
            "A rejected malformed guidance target changed on disk.");
    }

    var duplicateTarget = Path.Combine(targetDirectory, "duplicate", "AGENTS.md");
    Directory.CreateDirectory(Path.GetDirectoryName(duplicateTarget)!);
    var duplicate = await File.ReadAllTextAsync(cortex) +
        "\n\n<!-- PEGASUS:CORTEX:MANAGED:v1 -->\nold\n<!-- /PEGASUS:CORTEX:MANAGED:v1 -->\n";
    await File.WriteAllTextAsync(duplicateTarget, duplicate, new UTF8Encoding(false));
    try
    {
        _ = CortexCodexGuidanceInstaller.Apply(duplicateTarget, cortex, pao);
        throw new InvalidOperationException("Mixed current and legacy Cortex guidance was accepted.");
    }
    catch (InvalidDataException)
    {
        Require(
            await File.ReadAllTextAsync(duplicateTarget) == duplicate,
            "A rejected duplicate guidance target changed on disk.");
    }
}

static async Task VerifyOptionalCodexIntegrationAsync(string testRoot)
{
    const string owner = "S-1-5-21-100-200-300-1001";
    const string endpoint = CortexCodexMcpRegistration.DefaultSharedEndpoint;
    var source = Path.Combine(FindSourceRoot(), "integrations", "codex", "pegasus-cortex-client", "guidance");
    CortexCodexGuidancePlan Guidance(string path) => CortexCodexGuidanceInstaller.Prepare(path,
        Path.Combine(source, "pegasus-cortex-agents.v2.md"), Path.Combine(source, "pegasus-cortex-pao.v1.md"));
    static void Reject(Action action, string message)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }
    const string originalConfig = "model = \"fixture-model\"\r\n# unrelated comment Ω\r\n" +
        "[plugins.\"unrelated@fixture\"]\r\nenabled = true\r\n\r\n" +
        "[plugins.\"pegasus-cortex-client@pegasus-suite\"]\r\nenabled = true # keep comment\r\n";
    const string originalAgents = "# User authority\r\nKeep unrelated guidance Ω.\r\n";
    foreach (var register in new[] { false, true })
    foreach (var guidance in new[] { false, true })
    {
        var fixture = Path.Combine(testRoot, $"optional-codex-{register}-{guidance}");
        var layout = CortexProductLayout.ForIsolatedRoot(Path.Combine(fixture, "product"), owner);
        var home = Path.Combine(fixture, "codex");
        Directory.CreateDirectory(home);
        var config = Path.Combine(home, "config.toml"); var agents = Path.Combine(home, "AGENTS.md");
        await File.WriteAllTextAsync(config, originalConfig, new UTF8Encoding(false));
        await File.WriteAllTextAsync(agents, originalAgents, new UTF8Encoding(false));
        var plan = CortexCodexIntegration.Prepare(new("fixture", null, null,
            CodexRegister: register, CodexGuidance: guidance, CodexHome: home), layout, Guidance,
            () => throw new InvalidOperationException("An explicit or disabled target resolved the default Codex home."));
        Require(plan.Selection.Register == register && plan.Selection.Guidance == guidance &&
            (plan.Registration is not null) == register && (plan.Guidance is not null) == guidance,
            "Prepared Codex actions do not match independent selections.");
        Require(await File.ReadAllTextAsync(config) == originalConfig && await File.ReadAllTextAsync(agents) == originalAgents,
            "Optional Codex preparation changed user files before lifecycle preflight.");
        var registration = plan.Registration?.Apply(); _ = plan.Guidance?.Apply();
        var resultingConfig = await File.ReadAllTextAsync(config); var resultingAgents = await File.ReadAllTextAsync(agents);
        if (register)
        {
            Require(registration is { Version: 2, RegistrationName: "Cortex", Url: endpoint,
                    LegacyAliasRemoved: false, LegacyPluginDisabled: true, LegacyPluginChanged: true } &&
                resultingConfig.StartsWith(originalConfig.Replace("enabled = true # keep comment", "enabled = false # keep comment", StringComparison.Ordinal), StringComparison.Ordinal) &&
                resultingConfig.Contains("[mcp_servers.Cortex]", StringComparison.Ordinal) &&
                resultingConfig.Contains("url = \"" + endpoint + "\"", StringComparison.Ordinal) &&
                !resultingConfig.Contains("pegasus_cortex", StringComparison.Ordinal) && !resultingConfig.Contains("stdio", StringComparison.Ordinal),
                "Selected registration did not emit only the shared Cortex URL.");
            var replay = CortexCodexMcpRegistration.Prepare(config, layout.InstallRoot).Apply();
            Require(replay is { Disposition: "unchanged", LegacyAliasRemoved: false, LegacyPluginDisabled: true, LegacyPluginChanged: false } &&
                await File.ReadAllTextAsync(config) == resultingConfig, "Shared registration replay rewrote unrelated bytes.");
        }
        else Require(resultingConfig == originalConfig, "Unselected registration changed Codex configuration.");
        Require(guidance ? resultingAgents.StartsWith("## Pegasus Cortex continuity", StringComparison.Ordinal) &&
            resultingAgents.Contains("End of Pegasus Cortex continuity", StringComparison.Ordinal) &&
            resultingAgents.Contains(originalAgents, StringComparison.Ordinal) : resultingAgents == originalAgents,
            "Managed guidance lost its independent selection or unrelated text.");
        CortexCodexIntegration.WriteSelection(layout, plan.Selection with { LegacyPluginDisabled = registration?.LegacyPluginDisabled ?? false });
        if (register && guidance)
        {
            var registrationOnly = CortexCodexIntegration.Prepare(new("fixture", null, null,
                CodexRegisterAction: CortexCodexFeatureAction.Install), layout, Guidance);
            _ = registrationOnly.Registration!.Apply(); CortexCodexIntegration.WriteSelection(layout, registrationOnly.Selection);
            Require(CortexCodexIntegration.ReadPrevious(layout) is { Guidance: true } retained && retained.AgentsPath == agents &&
                await File.ReadAllTextAsync(agents) == resultingAgents && await File.ReadAllTextAsync(config) == resultingConfig,
                "Registration-only action lost independent guidance.");
            var guidanceOnly = CortexCodexIntegration.Prepare(new("fixture", null, null,
                CodexGuidanceAction: CortexCodexFeatureAction.Install), layout, Guidance, sharedEndpoint: "http://not-selected.invalid/");
            _ = guidanceOnly.Guidance!.Apply(); CortexCodexIntegration.WriteSelection(layout, guidanceOnly.Selection);
            Require(CortexCodexIntegration.ReadPrevious(layout) is { Register: true } retainedRegistration &&
                retainedRegistration.Home == home && await File.ReadAllTextAsync(config) == resultingConfig,
                "Guidance-only action inspected registration endpoint or changed configuration.");
            var removeRegistration = CortexCodexIntegration.Prepare(new("fixture", null, null,
                CodexRegisterAction: CortexCodexFeatureAction.Remove), layout, Guidance);
            _ = removeRegistration.RegistrationRemoval!.Apply(); CortexCodexIntegration.WriteSelection(layout, removeRegistration.Selection);
            Require(CortexCodexIntegration.ReadPrevious(layout) is { Register: false, Guidance: true } removed &&
                removed.AgentsPath == agents && await File.ReadAllTextAsync(agents) == resultingAgents,
                "Registration-only removal changed independent guidance.");
            var restore = CortexCodexIntegration.Prepare(new("fixture", null, null,
                CodexRegisterAction: CortexCodexFeatureAction.Install), layout, Guidance);
            _ = restore.Registration!.Apply(); CortexCodexIntegration.WriteSelection(layout, restore.Selection);
            resultingConfig = await File.ReadAllTextAsync(config);
        }
        var beforeRepair = await File.ReadAllTextAsync(config); var beforeAgents = await File.ReadAllTextAsync(agents);
        var preserved = CortexCodexIntegration.Prepare(new("fixture", null, null), layout,
            _ => throw new InvalidOperationException("Ordinary repair prepared guidance."),
            () => throw new InvalidOperationException("Ordinary repair resolved Codex home."), sharedEndpoint: "http://not-selected.invalid/");
        Require(!preserved.RecordSelection && preserved.Registration is null && preserved.Guidance is null &&
            preserved.RegistrationRemoval is null && preserved.GuidanceRemoval is null &&
            await File.ReadAllTextAsync(config) == beforeRepair && await File.ReadAllTextAsync(agents) == beforeAgents &&
            CortexCodexIntegration.ReadPrevious(layout)?.LegacyPluginDisabled == register,
            "Keep/ordinary repair inspected or changed optional Codex features.");
        var selected = CortexCodexIntegration.ReadPrevious(layout)!;
        Reject(() => CortexCodexIntegration.ResolveSelection(new("fixture", null, null), layout,
            selected with { OwnerSid = "S-1-5-21-100-200-300-1002" }, () => home), "Foreign-user selection adopted.");
        Reject(() => CortexCodexIntegration.ResolveSelection(new("fixture", null, null), layout,
            selected with { InstallRoot = Path.Combine(fixture, "foreign") }, () => home), "Foreign-install selection adopted.");
        var remove = CortexCodexIntegration.Prepare(new("fixture", null, null,
            CodexRegisterAction: register ? CortexCodexFeatureAction.Remove : CortexCodexFeatureAction.Keep,
            CodexGuidanceAction: guidance ? CortexCodexFeatureAction.Remove : CortexCodexFeatureAction.Keep),
            layout, Guidance, () => throw new InvalidOperationException("Recorded paths should satisfy removal."));
        _ = remove.RegistrationRemoval?.Apply(); _ = remove.GuidanceRemoval?.Apply();
        if (remove.RecordSelection) CortexCodexIntegration.WriteSelection(layout, remove.Selection);
        Require(!register || !(await File.ReadAllTextAsync(config)).Contains("[mcp_servers.Cortex]", StringComparison.Ordinal),
            "Feature removal retained the managed shared route.");
        Require(!guidance || await File.ReadAllTextAsync(agents) == originalAgents, "Feature removal changed unrelated guidance.");
    }

    var isolated = CortexProductLayout.ForIsolatedRoot(Path.Combine(testRoot, "standalone-product"), owner);
    var absentHome = Path.Combine(testRoot, "absent-codex");
    var off = CortexCodexIntegration.Prepare(new("fixture", null, "not-a-path", CodexRegister: false,
        CodexGuidance: false, CodexHome: "not-a-path"), isolated,
        _ => throw new InvalidOperationException("Disabled guidance was prepared."),
        () => throw new InvalidOperationException("Standalone setup resolved Codex home."), sharedEndpoint: "http://not-selected.invalid/");
    Require(off.Registration is null && off.Guidance is null && !Directory.Exists(absentHome), "Standalone setup accessed Codex.");
    Directory.CreateDirectory(isolated.InstallStateDirectory);
    await File.WriteAllTextAsync(CortexCodexIntegration.ReceiptPath(isolated), "malformed fixture receipt");
    _ = CortexCodexIntegration.Prepare(new("fixture", null, null), isolated,
        _ => throw new InvalidOperationException("Ordinary repair prepared guidance."),
        () => throw new InvalidOperationException("Ordinary repair resolved home."));
    var fresh = CortexCodexIntegration.Prepare(new("fixture", null, null),
        CortexProductLayout.ForIsolatedRoot(Path.Combine(testRoot, "fresh-standalone"), owner),
        _ => throw new InvalidOperationException("Fresh omitted guidance was prepared."),
        () => throw new InvalidOperationException("Fresh omitted registration resolved Codex."));
    Require(!fresh.Selection.Register && !fresh.Selection.Guidance, "Fresh omitted options did not remain standalone.");

    var freshConfig = Path.Combine(absentHome, "config.toml");
    var prepared = CortexCodexMcpRegistration.Prepare(freshConfig, isolated.InstallRoot);
    Require(!Directory.Exists(absentHome), "Registration preparation created an absent home.");
    _ = prepared.Apply();
    var legacy = Path.Combine(isolated.InstallRoot, "PegasusCortex.exe");
    var direct = Path.Combine(isolated.InstallRoot, "bin", "semantics", "PegasusCortex.exe");
    var encodedDirect = System.Text.Json.JsonSerializer.Serialize(direct);
    var encodedLegacy = System.Text.Json.JsonSerializer.Serialize(legacy);
    var encodedUrl = System.Text.Json.JsonSerializer.Serialize(endpoint);
    foreach (var conflict in new[]
    {
        "[mcp_servers.Cortex]\nurl = \"https://localhost:47832/mcp/cortex\"\n",
        "[mcp_servers.Cortex]\ncommand = " + encodedDirect + "\nargs = [\"stdio\"]\n",
        "[mcp_servers.Cortex]\nurl = " + encodedUrl + "\ncommand = " + encodedDirect + "\n",
        "[mcp_servers.Cortex]\nurl = " + encodedUrl + "\nurl = " + encodedUrl + "\n",
        "[mcp_servers.Cortex.env]\nKEY = \"keep\"\n",
        "[mcp_servers.pegasus_cortex]\ncommand = \"foreign.exe\"\nargs = [\"stdio\"]\n",
        "[mcp_servers.pegasus_cortex]\ncommand = " + encodedLegacy + "\nargs = [\"stdio\", \"extra\"]\n",
        "[mcp_servers.pegasus_cortex]\ncommand = " + encodedDirect + "\nargs = [\"stdio\",,]\n",
        "mcp_servers = { Cortex = { url = " + encodedUrl + " } }\n",
        "[plugins.\"pegasus-cortex-client@pegasus-suite\"]\nenabled=true\nenabled=false\n",
        "plugins.\"pegasus-cortex-client@pegasus-suite\" = { enabled = true }\n"
    })
    {
        await File.WriteAllTextAsync(freshConfig, conflict);
        Reject(() => CortexCodexMcpRegistration.Prepare(freshConfig, isolated.InstallRoot), "Conflicting/ambiguous registration accepted.");
        Reject(() => CortexCodexMcpRegistration.PrepareRemove(freshConfig, isolated.InstallRoot), "Conflicting removal adopted foreign coordinates.");
        Require(await File.ReadAllTextAsync(freshConfig) == conflict, "Rejected preparation changed its target.");
    }
    foreach (var conflict in new[]
    {
        "[mcp_servers.unrecognized]\ncommand = " + encodedDirect + "\nargs = [\"stdio\"]\n",
        "[mcp_servers.OtherCortex]\nurl = " + encodedUrl + "\n"
    })
    {
        await File.WriteAllTextAsync(freshConfig, conflict);
        Reject(() => CortexCodexMcpRegistration.Prepare(freshConfig, isolated.InstallRoot), "Unknown Cortex alias duplicated.");
        Require(await File.ReadAllTextAsync(freshConfig) == conflict, "Unknown alias was removed or changed.");
    }
    foreach (var badEndpoint in new[] { "http://localhost:47831/mcp/cortex", "https://example.invalid/mcp/cortex",
        "https://user@localhost:47831/mcp/cortex", endpoint + "?secret=x", endpoint + "#fragment" })
        Reject(() => CortexCodexMcpRegistration.Prepare(freshConfig, isolated.InstallRoot, badEndpoint), "Unsafe endpoint admitted.");
    const string customEndpoint = "https://localhost:47832/mcp/cortex";
    var custom = CortexCodexMcpRegistration.Compose("", isolated.InstallRoot, customEndpoint).Text;
    Require(custom.Contains(customEndpoint, StringComparison.Ordinal) && !custom.Contains(endpoint, StringComparison.Ordinal),
        "Explicit endpoint ignored in favor of factory default.");

    var suffix = "\r\n# keep following comment\r\n[unrelated.after]\r\nvalue = \"keep Ω\"\r\n";
    var retainedConfig = originalConfig.Replace("enabled = true # keep comment", "enabled = false # keep comment", StringComparison.Ordinal);
    var shared = "[mcp_servers.Cortex]\r\nurl  =  " + encodedUrl + " # selected shared route\r\n" +
        "enabled = false # user choice\r\nstartup_timeout_sec = 35\r\ntool_timeout_sec = 47\r\n" +
        "enabled_tools = [\"cortex_start\"]\r\ndisabled_tools = [\"pao\"]\r\nrequired = false\r\n";
    foreach (var dotted in new[] { false, true })
    foreach (var oldCommand in new[] { encodedLegacy, encodedDirect })
    {
        var prefix = dotted ? "mcp_servers.pegasus_cortex." : "";
        var old = (dotted ? "" : "[mcp_servers.pegasus_cortex]\r\n") +
            prefix + "command  =  " + oldCommand + " # exact old route\r\n" +
            (dotted ? "unrelatedRoot = \"keep interleaved Ω\"\r\n" : "") +
            prefix + "args = [\"stdio\",] # retired transport\r\n" + prefix + "enabled = false\r\n";
        var before = (dotted ? old : "") + retainedConfig + (dotted ? "" : old) + shared + suffix;
        await File.WriteAllBytesAsync(freshConfig, [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(before)]);
        var migration = CortexCodexMcpRegistration.Prepare(freshConfig, isolated.InstallRoot);
        var receipt = migration.Apply();
        var afterBytes = await File.ReadAllBytesAsync(freshConfig);
        var after = Encoding.UTF8.GetString(afterBytes.AsSpan(Encoding.UTF8.Preamble.Length));
        Require(afterBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) && receipt is
            { Version: 2, Url: endpoint, LegacyAliasRemoved: true, LegacyPluginDisabled: true, LegacyPluginChanged: false } &&
            after.Contains(shared, StringComparison.Ordinal) && after.Contains(suffix, StringComparison.Ordinal) &&
            after.Contains(retainedConfig, StringComparison.Ordinal) && !after.Contains("mcp_servers.pegasus_cortex", StringComparison.Ordinal) &&
            !after.Contains("command  =", StringComparison.Ordinal) &&
            (!dotted || after.Contains("unrelatedRoot = \"keep interleaved Ω\"", StringComparison.Ordinal)),
            "Exact legacy retirement changed shared settings/BOM/comments/unrelated TOML or retained fallback.");
        Require(CortexCodexMcpRegistration.Prepare(freshConfig, isolated.InstallRoot).Apply().Disposition == "unchanged" &&
            (await File.ReadAllBytesAsync(freshConfig)).AsSpan().SequenceEqual(afterBytes),
            "Shared matching disabled registration was reenabled or rewritten on replay.");
        await File.WriteAllTextAsync(freshConfig, before);
        _ = CortexCodexMcpRegistration.PrepareRemove(freshConfig, isolated.InstallRoot).Apply();
        var removed = await File.ReadAllTextAsync(freshConfig);
        Require(!removed.Contains("mcp_servers.Cortex", StringComparison.Ordinal) && !removed.Contains("mcp_servers.pegasus_cortex", StringComparison.Ordinal) &&
            removed.Contains("enabled = false # keep comment", StringComparison.Ordinal) && removed.Contains(suffix, StringComparison.Ordinal) &&
            (!dotted || removed.Contains("unrelatedRoot = \"keep interleaved Ω\"", StringComparison.Ordinal)),
            "Feature removal changed unrelated content or enabled the legacy plugin.");
    }
    // Migration from a lone legacy alias emits the shared route, never another
    // executable route, regardless of whether the legacy fallback was disabled.
    var loneLegacy = "[mcp_servers.pegasus_cortex]\ncommand=" + encodedDirect + "\nargs=[\"stdio\"]\nenabled=false\n";
    var loneResult = CortexCodexMcpRegistration.Compose(loneLegacy, isolated.InstallRoot);
    Require(loneResult.LegacyAliasRemoved && loneResult.Text.Contains("[mcp_servers.Cortex]", StringComparison.Ordinal) &&
        loneResult.Text.Contains("enabled = true", StringComparison.Ordinal) && !loneResult.Text.Contains("stdio", StringComparison.Ordinal),
        "Legacy-only migration reintroduced a process connection.");

    var dottedShared = "mcp_servers.Cortex.url = " + encodedUrl + "\n" +
        "unrelated = \"keep between settings\"\nmcp_servers.Cortex.enabled = false\nmcp_servers.Cortex.tool_timeout_sec = 55\n";
    Require(CortexCodexMcpRegistration.Compose(dottedShared, isolated.InstallRoot).Text == dottedShared,
        "Matching dotted shared route was reformatted or enabled.");
    var dottedRemoved = CortexCodexMcpRegistration.Remove(dottedShared, isolated.InstallRoot);
    Require(!dottedRemoved.Contains("mcp_servers.Cortex", StringComparison.Ordinal) &&
        dottedRemoved.Contains("unrelated = \"keep between settings\"", StringComparison.Ordinal), "Dotted removal removed intervening TOML.");
    var noFinalNewline = "[plugins.\"pegasus-cortex-client@pegasus-suite\"]";
    var approvedShared = CortexCodexMcpRegistration.Compose("", isolated.InstallRoot).Text +
        "http_headers_helper = " + JsonSerializer.Serialize("\"" + Path.Combine(isolated.InstallRoot, "PegasusCortex.exe") +
            "\" mcp headers --connection " + new string('1',32)) + "\n";
    Require(CortexCodexMcpRegistration.Compose(approvedShared, isolated.InstallRoot).Text == approvedShared,
        "A remembered public-ID helper was removed or executed during registration.");
    var inserted = CortexCodexMcpRegistration.Compose(noFinalNewline, isolated.InstallRoot);
    Require(inserted.Text.StartsWith(noFinalNewline + "\nenabled = false\n", StringComparison.Ordinal),
        "Omitted legacy-plugin enabled value at EOF was not safely disabled.");
    await File.WriteAllTextAsync(freshConfig, inserted.Text);
    var unchanged = CortexCodexMcpRegistration.Prepare(freshConfig, isolated.InstallRoot);
    await File.AppendAllTextAsync(freshConfig, "# concurrent user edit\n");
    try { _ = unchanged.Apply(); throw new InvalidOperationException("Changed integration preimage ignored."); }
    catch (IOException) { }
    Require((await File.ReadAllTextAsync(freshConfig)).EndsWith("# concurrent user edit\n", StringComparison.Ordinal),
        "Concurrent Codex edit overwritten.");

    var shadow = Path.Combine(absentHome, "AGENTS.override.md");
    await File.WriteAllTextAsync(shadow, "# Higher-priority user instructions\n");
    Reject(() => Guidance(Path.Combine(absentHome, "AGENTS.md")), "Shadowed guidance reported as effective.");
    Require(!File.Exists(Path.Combine(absentHome, "AGENTS.md")) &&
        await File.ReadAllTextAsync(shadow) == "# Higher-priority user instructions\n", "Shadow detection changed user files.");
    CortexCodexIntegration.RequireOwnerSid(owner, owner);
    CortexCodexIntegration.RequireOwnerSid("S-1-5-32-544", owner, allowSystemFileOwner: true);
    CortexCodexIntegration.RequireOwnerSid("S-1-5-18", owner, allowSystemFileOwner: true);
    try { CortexCodexIntegration.RequireOwnerSid("S-1-5-21-100-200-300-1002", owner); throw new InvalidOperationException("Foreign target owner accepted."); }
    catch (UnauthorizedAccessException) { }
    try { CortexCodexIntegration.RequireOwnerSid("S-1-5-32-544", owner); throw new InvalidOperationException("Privileged foreign directory owner accepted."); }
    catch (UnauthorizedAccessException) { }
    if (OperatingSystem.IsWindows())
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Creation fixture SID unavailable.");
        var creationTarget = Path.Combine(testRoot, "codex-created-owner", "nested", "config.toml");
        var creation = CortexCodexMcpRegistration.Prepare(creationTarget, isolated.InstallRoot);
        (creation.Update with { CreationOwnerSid = sid }).Apply();
        Require(new FileInfo(creationTarget).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier fileSid &&
            fileSid.Value == sid && new DirectoryInfo(Path.GetDirectoryName(creationTarget)!).GetAccessControl().GetOwner(typeof(SecurityIdentifier))
                is SecurityIdentifier directorySid && directorySid.Value == sid,
            "Created registration file/directory did not bind installing-user ownership.");
    }
}

static void VerifyLegacyInstallAdmission(string testRoot, string catalogSource, string owner)
{
    const string completedRelease = "1.2026.08.28.12.33.A";
    var layout = CortexProductLayout.ForIsolatedRoot(Path.Combine(testRoot, "legacy-admission"), owner);
    Directory.CreateDirectory(layout.InstallStateDirectory);
    File.Copy(catalogSource, layout.ActiveCatalogPath);
    var sourceCatalog = CortexProductCatalogV1.ReadExact(catalogSource);
    string Version(string role) => sourceCatalog.Roles.Single(item => item.Role == role).ComponentVersion;
    var currentCatalog = File.ReadAllText(layout.ActiveCatalogPath);
    var historicalCatalog = currentCatalog
        .Replace("\"applicationAuthorization\":1,", "", StringComparison.Ordinal)
        .Replace("bin/semantics/PegasusCortex.exe", $"versions/semantics/{Version("mcp")}/PegasusCortex.exe", StringComparison.Ordinal)
        .Replace("bin/host/PegasusCortex.Host.exe", $"versions/execution/{Version("host")}/host/PegasusCortex.Host.exe", StringComparison.Ordinal)
        .Replace("bin/service/PegasusCortex.ExecutionService.exe", $"versions/execution/{Version("service")}/service/PegasusCortex.ExecutionService.exe", StringComparison.Ordinal)
        .Replace("bin/host/Pegasus.AgentOps.ProcessHost.exe", $"versions/execution/{Version("process-host")}/host/Pegasus.AgentOps.ProcessHost.exe", StringComparison.Ordinal)
        .Replace("bin/ui/Pegasus.AgentOps.UiHost.exe", $"versions/execution/{Version("ui-host")}/ui/Pegasus.AgentOps.UiHost.exe", StringComparison.Ordinal);
    Require(historicalCatalog != currentCatalog,
        "The historical upgrade fixture did not omit the later state schema.");
    File.WriteAllText(layout.ActiveCatalogPath, historicalCatalog);
    var service = Path.Combine(layout.InstallRoot, "versions", "execution", completedRelease, "service", "PegasusCortex.ExecutionService.exe");
    var host = Path.Combine(layout.InstallRoot, "versions", "execution", completedRelease, "host", "PegasusCortex.Host.exe");
    foreach (var path in new[] { service, host })
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "immutable legacy role fixture");
    }
    var receipt = new CortexWindowsRegistrationReceiptV1(CortexWindowsProductRegistration.ReceiptContract, 1,
        completedRelease, CortexProductIdentity.ServiceName, service, true, layout.BinDirectory, true,
        "Pegasus Cortex", host, true, true, null);
    var receiptPath = Path.Combine(layout.InstallStateDirectory, "registration.v1.json");
    void Write(CortexWindowsRegistrationReceiptV1 value) =>
        File.WriteAllText(receiptPath, System.Text.Json.JsonSerializer.Serialize(value));
    Write(receipt);
    var arp = new CortexLegacyArpIdentity(layout.InstallRoot, completedRelease, "Pegasus Cortex Prod Alpha", "Pegasus",
        '"' + Path.Combine(layout.InstallRoot, "Uninstall.exe") + '"');
    var originalReceipt = File.ReadAllBytes(receiptPath);
    var originalCatalog = File.ReadAllBytes(layout.ActiveCatalogPath);
    // The newer active catalog has newer semantic AND execution role slots;
    // the completed registration/ARP remain older after a partial forward copy.
    Require(CortexProductInstallPlanner.CompareReleaseVersions(
        CortexProductCatalogV1.ReadInstalledUpgradeSource(layout.ActiveCatalogPath).ReleaseVersion, completedRelease) > 0,
        "The legacy fixture does not represent the observed partial forward install.");
    CortexProductInstallPlanner.RequireDestinationPolicy(layout, arp);
    Require(CortexProductInstallPlanner.ReadEstablishedDataRoot(layout) is null &&
            layout.DataRoot == Path.Combine(layout.InstallRoot, "state") &&
            File.ReadAllBytes(receiptPath).AsSpan().SequenceEqual(originalReceipt) &&
            File.ReadAllBytes(layout.ActiveCatalogPath).AsSpan().SequenceEqual(originalCatalog),
        "Legacy admission wrote a binding, changed metadata, or redirected retained databases.");
    void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (IOException) { return; }
        throw new InvalidOperationException(message);
    }
    Reject(() => CortexProductInstallPlanner.RequireDestinationPolicy(layout),
        "An occupied fixture was admitted without independent machine ARP identity.");
    foreach (var foreign in new[]
    {
        arp with { InstallRoot = Path.Combine(testRoot, "foreign-product") },
        arp with { ReleaseVersion = "1.2026.08.28.12.32.A" },
        arp with { Publisher = "foreign" },
        arp with { DisplayName = "foreign" },
        arp with { UninstallString = "foreign.exe" }
    }) Reject(() => CortexProductInstallPlanner.RequireDestinationPolicy(layout, foreign),
        "A foreign or inconsistent machine ARP identity admitted an occupied location.");
    foreach (var foreign in new[]
    {
        receipt with { Contract = "foreign" },
        receipt with { ServiceName = "foreign" },
        receipt with { ServiceExecutable = Path.Combine(testRoot, "foreign.exe") },
        receipt with { RunExecutable = service },
        receipt with { PathEntry = Path.Combine(testRoot, "foreign-bin") },
        receipt with { DataRoot = Path.Combine(testRoot, "foreign-data") },
        receipt with { ReleaseVersion = "1.2026.08.28.12.32.A" }
    })
    {
        Write(foreign);
        Reject(() => CortexProductInstallPlanner.RequireDestinationPolicy(layout, arp),
            "A foreign or inconsistent prior registration admitted an occupied location.");
    }
    Write(receipt);
    File.WriteAllText(receiptPath, System.Text.Json.JsonSerializer.Serialize(receipt).Replace(
        "\"Version\":1", "\"Version\":1,\"Version\":1", StringComparison.Ordinal));
    Reject(() => CortexProductInstallPlanner.RequireDestinationPolicy(layout, arp), "Duplicate legacy receipt fields were admitted.");
    Write(receipt);
    var registered = layout with { };
    // Isolate a conflicting registry-derived value without changing the live
    // registry or making the product's internal selector publicly writable.
    typeof(CortexProductLayout).GetProperty(nameof(CortexProductLayout.RegisteredDataRoot))!
        .SetValue(registered, Path.Combine(testRoot, "different-registered-data"));
    Reject(() => CortexProductInstallPlanner.RequireDestinationPolicy(registered, arp),
        "Legacy compatibility bypassed the current machine data binding.");
    var redirected = CortexProductLayout.ForIsolatedRoots(layout.InstallRoot, Path.Combine(testRoot, "redirected-legacy-data"), owner);
    Reject(() => CortexProductInstallPlanner.RequireDestinationPolicy(redirected, arp),
        "Legacy compatibility allowed an external empty database selection.");
    CortexProductInstallPlanner.PreserveLocations(layout);
    Reject(() => CortexProductInstallPlanner.RequireDestinationPolicy(redirected, arp),
        "Legacy compatibility bypassed an established install-location receipt.");
}

static string FindSourceRoot()
{
    foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
    {
        var current = new DirectoryInfo(Path.GetFullPath(start));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "VERSION.json")) &&
                File.Exists(Path.Combine(current.FullName, "PegasusCortex.slnx")))
                return current.FullName;
            current = current.Parent;
        }
    }
    throw new DirectoryNotFoundException("The Pegasus Cortex source root was not found.");
}

static void VerifyStateRoutingMetadataAccess()
{
    var security = CortexProductStateProvisioner.DescribeDirectorySecurity(new(
        @"C:\fixture\state", "S-1-5-32-544", CortexDirectoryAccessProfile.StateTraverse));
    var users = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>().Single(rule => rule.IdentityReference.Value == "S-1-5-32-545");
    Require((users.FileSystemRights & FileSystemRights.ReadPermissions) != 0 &&
        (users.FileSystemRights & FileSystemRights.ListDirectory) != 0 &&
        users.InheritanceFlags == InheritanceFlags.None &&
        users.PropagationFlags == PropagationFlags.None &&
        (users.FileSystemRights & (FileSystemRights.CreateFiles |
            FileSystemRights.CreateDirectories | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) == 0,
        "State routing metadata must permit container listing without inherited access or mutation rights.");
}

static async Task VerifyInstallLifecycleAsync(string testRoot)
{
    var lifecycleOrder = new List<string>();
    var coreOnly = CortexProductInstallLifecycle.PrepareCore(
        () => { lifecycleOrder.Add("service"); return true; },
        () => { lifecycleOrder.Add("processes"); return 2; });
    Require(lifecycleOrder.SequenceEqual(["service", "processes"]) &&
            coreOnly.ServiceStopped && coreOnly.ProductProcessesStopped == 2,
        "Core installation did not drain only owned Cortex components in order.");
    CortexProductInstallLifecycle.RequireCodexClosed(["explorer", "pwsh"]);
    foreach (var processName in new[] { "codex", "CoDeX", "chatgpt", "ChatGPT" })
    {
        try
        {
            CortexProductInstallLifecycle.RequireCodexClosed(["explorer", processName]);
            throw new InvalidOperationException("A running Codex feature client bypassed the Codex-file mutation prerequisite.");
        }
        catch (CortexCodexRunningException exception)
        {
            Require(exception.Message.Contains("1 related process", StringComparison.Ordinal),
                "The Codex feature prerequisite lost its bounded process count.");
        }
    }

    var installRoot = Path.Combine(testRoot, "lifecycle", "Pegasus", "Cortex");
    var fixtureDirectory = Path.Combine(installRoot, "versions", "fixture");
    Directory.CreateDirectory(fixtureDirectory);
    foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.TopDirectoryOnly))
        File.Copy(source, Path.Combine(fixtureDirectory, Path.GetFileName(source)), overwrite: false);

    var fixture = Path.Combine(fixtureDirectory, "PegasusCortex.Setup.Tests.exe");
    var start = new System.Diagnostics.ProcessStartInfo
    {
        FileName = fixture,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    start.ArgumentList.Add("--lifecycle-fixture");
    using var process = System.Diagnostics.Process.Start(start) ??
        throw new InvalidOperationException("The lifecycle fixture did not start.");
    try
    {
        var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Require(ready == "ready", "The lifecycle fixture did not enter its bounded wait state.");
        var stopped = CortexProductInstallLifecycle.StopProductProcessesForTest(installRoot);
        Require(stopped == 1, "The lifecycle drain did not stop exactly the admitted product fixture.");
        Require(process.WaitForExit(10_000), "The lifecycle fixture remained alive after the drain.");
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
    }
}

static async Task VerifyForwardInstallerAsync(string testRoot)
{
    const string release = "1.2026.08.28.12.34.A";
    var source = Path.Combine(testRoot, "verified-package");
    Directory.CreateDirectory(source);
    var rolePaths = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["mcp"] = "bin/semantics/PegasusCortex.exe",
        ["host"] = "bin/host/PegasusCortex.Host.exe",
        ["service"] = "bin/service/PegasusCortex.ExecutionService.exe",
        ["pao"] = "bin/pao.exe",
        ["process-host"] = "bin/host/Pegasus.AgentOps.ProcessHost.exe",
        ["ui-host"] = "bin/ui/Pegasus.AgentOps.UiHost.exe",
        ["launcher"] = "PegasusCortex.exe",
        ["setup"] = "installer/PegasusCortex.Setup.exe"
    };
    foreach (var item in rolePaths)
    {
        var path = Path.Combine(source, item.Value.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, item.Key + "-v1");
    }
    var catalogRelative = $"manifests/releases/{release}/product-catalog.v1.json";
    var catalogPath = Path.Combine(source, catalogRelative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
    var components = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["mcp"]="semantics", ["host"]="execution", ["service"]="execution", ["pao"]="execution",
        ["process-host"]="execution", ["ui-host"]="execution", ["launcher"]="launcher", ["setup"]="installer"
    };
    var componentVersions = rolePaths.Keys.ToDictionary(key => key, _ => release, StringComparer.Ordinal);
    componentVersions["host"] = "1.2026.08.28.12.31.A";
    componentVersions["service"] = "1.2026.08.28.12.32.A";
    componentVersions["pao"] = "1.2026.08.28.12.33.A";
    var modules = new object[]
    {
        new { moduleId="core.bootstrap", kind="core", moduleVersion=release, @required=true, statePolicy="none", roles=new[]{"launcher","setup"}, provides=new[]{"product.catalog/1"}, requires=Array.Empty<string>() },
        new { moduleId="semantics.local", kind="runtime", moduleVersion="1.2026.08.28.12.30.A", @required=true, statePolicy="none", roles=new[]{"mcp"}, provides=new[]{"cortex.mcp/2"}, requires=new[]{"storage.sqlite/2"} },
        new { moduleId="execution.local", kind="runtime", moduleVersion="1.2026.08.28.12.31.A", @required=true, statePolicy="preserve", roles=new[]{"host","service","process-host","ui-host"}, provides=new[]{"execution.local/1"}, requires=Array.Empty<string>() },
        new { moduleId="pao.client", kind="tooling", moduleVersion="1.2026.08.28.12.33.A", @required=true, statePolicy="none", roles=new[]{"pao"}, provides=new[]{"pao/3"}, requires=new[]{"execution.local/1"} },
        new { moduleId="storage.sqlite", kind="storage", moduleVersion=release, @required=true, statePolicy="preserve", roles=Array.Empty<string>(), provides=new[]{"storage.sqlite/2"}, requires=Array.Empty<string>() }
    };
    await File.WriteAllTextAsync(catalogPath, System.Text.Json.JsonSerializer.Serialize(new
    {
        contract="pegasus-cortex-product-catalog", version=1, product="Pegasus Cortex",
        releaseVersion=release, channel="Prod Alpha", executionProtocol="1.0", cortexMcpSchema=2, paoSchema=3,
        stateSchemas=new{install=1,semantic=2,execution=1,applicationAuthorization=1,userExecution=1,tray=1},
        roles=rolePaths.Select(item=>new{role=item.Key,component=components[item.Key],componentVersion=componentVersions[item.Key],relativePath=item.Value}),
        modules
    }) + "\n");
    var manifest = Path.Combine(source, "package-manifest.json");
    var signature = Path.Combine(source, "package-manifest.p7s");
    await File.WriteAllTextAsync(manifest, "fixture manifest");
    await File.WriteAllTextAsync(signature, "fixture signature");
    var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
        .Where(path => path != manifest && path != signature)
        .Select(path =>
        {
            using var stream = File.OpenRead(path);
            return new CortexVerifiedPackageFileV1(
                Path.GetRelativePath(source, path).Replace(Path.DirectorySeparatorChar, '/'),
                new FileInfo(path).Length,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)));
        }).ToArray();
    var owner = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ??
        throw new InvalidOperationException("Current test SID unavailable.");
    var layout = CortexProductLayout.ForIsolatedRoots(
        Path.Combine(testRoot, "transaction-install"), Path.Combine(testRoot, "transaction-data"), owner);
    var catalog = CortexProductCatalogV1.ReadExact(catalogPath);
    Require(catalog.Roles.Where(role => role.Role is "host" or "service" or "pao")
            .All(role => role.ComponentVersion != catalog.ReleaseVersion),
        "The forward installer fixture did not retain independent execution-module versions.");
    Require(catalog.Modules is not null &&
            catalog.Modules.Single(module => module.ModuleId == "semantics.local").ModuleVersion != catalog.ReleaseVersion &&
            catalog.Modules.Single(module => module.ModuleId == "pao.client").ModuleVersion != catalog.ReleaseVersion,
        "The forward installer fixture did not retain independent catalog-module versions.");
    var sourceLayout = CortexProductLayout.ForIsolatedRoots(
        source, Path.Combine(testRoot, "source-data"), owner);
    var expectedUserHost = Path.Combine(
        sourceLayout.InstallRoot,
        rolePaths["host"].Replace('/', Path.DirectorySeparatorChar));
    Require(string.Equals(
            CortexWindowsProductRegistration.ResolveUserSessionExecutable(sourceLayout, catalog),
            expectedUserHost,
            StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(expectedUserHost,
            Path.Combine(sourceLayout.InstallRoot, rolePaths["launcher"]),
            StringComparison.OrdinalIgnoreCase),
        "Automatic user-session registration did not select the stable GUI host instead of the console-capable launcher.");
    var package = new CortexVerifiedProductPackageV1(
        source, release, catalogPath, manifest, signature,
        CortexProductIdentity.ProdAlphaPublisherThumbprint,
        CortexProductIdentity.ProdAlphaRootThumbprint,
        files);
    var historicalLayout = CortexProductLayout.ForIsolatedRoots(
        Path.Combine(testRoot, "historical-program"), Path.Combine(testRoot, "historical-data"), owner);
    Directory.CreateDirectory(historicalLayout.InstallStateDirectory);
    CortexProductInstallPlanner.PreserveLocations(historicalLayout);
    var historicalSource = await File.ReadAllTextAsync(catalogPath);
    var historicalRelease = "1.2026.08.28.12.33.A";
    var historicalActive = historicalSource
        .Replace(release, historicalRelease, StringComparison.Ordinal)
        .Replace("\"applicationAuthorization\":1,", "", StringComparison.Ordinal);
    Require(historicalActive != historicalSource &&
            !historicalActive.Contains("applicationAuthorization", StringComparison.Ordinal),
        "The pre-application-authorization upgrade fixture was not created.");
    await File.WriteAllTextAsync(historicalLayout.ActiveCatalogPath, historicalActive);
    var historicalPlan = CortexProductInstallPlanner.Create(package, historicalLayout);
    Require(historicalPlan.Mode == CortexInstallMode.Upgrade &&
            historicalPlan.InstalledVersion == historicalRelease &&
            historicalPlan.CandidateVersion == release && historicalPlan.StatePreserved,
        "A recognized historical catalog did not produce a database-preserving upgrade plan.");
    foreach (var invalidPath in new[] { "state/users/owner/cortex/data/cortex-v2.db", "State/service/config.json", "bin/seed.sqlite", "bin/cortex.db-wal" })
    {
        var rejectedLayout = CortexProductLayout.ForIsolatedRoots(
            Path.Combine(testRoot, "rejected-payload-program"), Path.Combine(testRoot, "rejected-payload-data"), owner);
        try
        {
            await CortexProductInstaller.InstallVerifiedFilesForTestAsync(package with
            {
                Files = [.. files, new CortexVerifiedPackageFileV1(invalidPath, 1, new string('0', 64))]
            }, rejectedLayout);
            throw new InvalidOperationException("A mutable database/state package payload was accepted.");
        }
        catch (InvalidDataException) { }
        Require(!Directory.Exists(rejectedLayout.InstallRoot) && !Directory.Exists(rejectedLayout.DataRoot),
            "Mutable package admission changed destination roots before rejection.");
    }
    var copiedCatalog = CortexProductLayout.ForIsolatedRoot(Path.Combine(testRoot, "copied-catalog"), owner);
    Directory.CreateDirectory(copiedCatalog.InstallStateDirectory);
    File.Copy(catalogPath, copiedCatalog.ActiveCatalogPath);
    try
    {
        await CortexProductInstaller.InstallVerifiedFilesForTestAsync(package, copiedCatalog);
        throw new InvalidOperationException("A copied catalog without installation binding adopted an occupied folder.");
    }
    catch (InvalidDataException) { }
    VerifyLegacyInstallAdmission(testRoot, catalogPath, owner);
    var interrupted = CortexProductLayout.ForIsolatedRoots(
        Path.Combine(testRoot, "interrupted-program"), Path.Combine(testRoot, "interrupted-data"), owner);
    try
    {
        await CortexProductInstaller.InstallVerifiedFilesForTestAsync(package with
        {
            Files = files.Select(file => file with { Sha256 = new string('0', 64) }).ToArray()
        }, interrupted);
        throw new InvalidOperationException("Invalid first-install digest accepted.");
    }
    catch (CortexProductInstallStageException exception) when (exception.Stage == "install-file") { }
    Require(!File.Exists(interrupted.ActiveCatalogPath) &&
            CortexProductInstallPlanner.ReadEstablishedDataRoot(interrupted) == interrupted.DataRoot,
        "Interrupted first install lost its data-location binding.");
    var resumed = await CortexProductInstaller.InstallVerifiedFilesForTestAsync(package, interrupted);
    Require(resumed.Migration is null && !File.Exists(interrupted.CortexDatabasePath),
        "First-install repair created or migrated databases.");
    var relocated = CortexProductLayout.ForIsolatedRoots(interrupted.InstallRoot,
        Path.Combine(testRoot, "not-the-established-data"), owner);
    typeof(CortexProductLayout).GetProperty(nameof(CortexProductLayout.RegisteredDataRoot))!
        .SetValue(relocated, interrupted.DataRoot);
    try
    {
        await CortexProductInstaller.InstallVerifiedFilesForTestAsync(package, relocated);
        throw new InvalidOperationException("Repair accepted a changed data root.");
    }
    catch (InvalidDataException) { }
    foreach (var occupiedRole in new[] { "program", "data" })
    {
        var occupied = CortexProductLayout.ForIsolatedRoots(
            Path.Combine(testRoot, "occupied-" + occupiedRole, "program"),
            Path.Combine(testRoot, "occupied-" + occupiedRole, "data"), owner);
        var target = occupiedRole == "program" ? occupied.InstallRoot : occupied.DataRoot;
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "existing.db");
        await File.WriteAllTextAsync(sentinel, "do not adopt");
        try
        {
            await CortexProductInstaller.InstallVerifiedFilesForTestAsync(package, occupied);
            throw new InvalidOperationException("A fresh install accepted an occupied " + occupiedRole + " folder.");
        }
        catch (IOException) { }
        Require(await File.ReadAllTextAsync(sentinel) == "do not adopt" &&
                !Directory.Exists(occupied.BinDirectory), "Rejected destination was changed.");
    }
    var first = await CortexProductInstaller.InstallVerifiedFilesForTestAsync(package, layout);
    Require(first.CreatedFiles >= files.Length, "The first package transaction did not create its admitted files.");
    Require(first.Migration is null && !first.DatabaseFilesTouched && first.DataRoot == layout.DataRoot &&
            !File.Exists(layout.CortexDatabasePath), "Fresh installation created or migrated a database.");
    var dataFiles = new[] { layout.CortexDatabasePath, layout.CortexDatabasePath + "-wal", layout.CortexDatabasePath + "-shm" };
    foreach (var path in dataFiles) await File.WriteAllTextAsync(path, "existing exact Ω data: " + Path.GetFileName(path));
    var originalData = dataFiles.Select(path => (Path: path, Bytes: File.ReadAllBytes(path), Modified: File.GetLastWriteTimeUtc(path))).ToArray();
    var heldData = dataFiles.Select(path => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)).ToArray();
    try
    {
        var lockedDataUpgrade = await CortexProductInstaller.InstallVerifiedFilesForTestAsync(package, layout);
        Require(!lockedDataUpgrade.DatabaseFilesTouched && lockedDataUpgrade.Migration is null,
            "A program upgrade interacted with exclusively locked database files.");
    }
    finally { foreach (var stream in heldData) stream.Dispose(); }
    foreach (var original in originalData)
        Require(File.ReadAllBytes(original.Path).AsSpan().SequenceEqual(original.Bytes) &&
                File.GetLastWriteTimeUtc(original.Path) == original.Modified,
            "Program upgrade changed database bytes or modification time.");
    var second = await CortexProductInstaller.InstallVerifiedFilesForTestAsync(package, layout);
    Require(second.PreservedFiles >= files.Length, "The second package transaction was not idempotent.");

    var sourcePao = Path.Combine(source, "bin", "pao.exe");
    var installedPao = Path.Combine(layout.InstallRoot, "bin", "pao.exe");
    await File.WriteAllTextAsync(sourcePao, "pao-v2");
    var replacement = package with
    {
        Files = files.Select(file => file.RelativePath == "bin/pao.exe"
            ? Evidence(source, sourcePao)
            : file).ToArray()
    };
    await using (var held = new FileStream(
                     installedPao, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try
        {
            await CortexProductInstaller.InstallVerifiedFilesForTestAsync(replacement, layout);
            throw new InvalidOperationException("A locked installed file was unexpectedly replaced.");
        }
        catch (CortexProductInstallStageException exception)
        {
            Require(exception.Stage == "install-file", "A locked file failure lost its bounded install stage.");
            Require(exception.Target == "bin/pao.exe", "A locked file failure lost its package-relative target.");
            Require(exception.InnerException is IOException,
                "A locked file failure did not preserve the underlying sharing violation.");
        }
    }
    Require(await File.ReadAllTextAsync(installedPao) == "pao-v1",
        "A blocked replacement did not preserve the original installed file.");

    var changed = files.Select(file => file.RelativePath == "bin/pao.exe"
        ? Evidence(source, sourcePao)
        : file.RelativePath == rolePaths["ui-host"]
            ? file with { Sha256 = new string('0', 64) }
            : file).ToArray();
    var rejected = package with { Files = changed };
    try
    {
        await CortexProductInstaller.InstallVerifiedFilesForTestAsync(rejected, layout);
        throw new InvalidOperationException("A mid-transaction digest failure was accepted.");
    }
    catch (CortexProductInstallStageException exception)
    {
        Require(exception.Stage == "install-file", "A digest failure lost its bounded install stage.");
        Require(exception.Target == rolePaths["ui-host"], "A digest failure lost its package-relative target.");
        Require(exception.InnerException is InvalidDataException,
            "A digest failure did not preserve the underlying integrity failure.");
    }
    Require(await File.ReadAllTextAsync(installedPao) == "pao-v2",
        "A later digest failure discarded an already verified replacement.");
    Require(!Directory.EnumerateDirectories(layout.InstallStateDirectory).Any(path =>
        Path.GetFileName(path).StartsWith(".rollback-", StringComparison.Ordinal)),
        "Forward installation created a rollback directory.");

    var activeBeforeCallbackFailure = await File.ReadAllBytesAsync(layout.ActiveCatalogPath);
    try
    {
        await CortexProductInstaller.InstallVerifiedFilesWithActivationForTestAsync(
            replacement,
            layout,
            _ => throw new InvalidOperationException("fixture registration failure"));
        throw new InvalidOperationException("A failed post-activation registration callback was accepted.");
    }
    catch (InvalidOperationException exception) when (exception.Message == "fixture registration failure")
    {
    }
    Require(await File.ReadAllTextAsync(installedPao) == "pao-v2",
        "A registration failure discarded the forward replacement.");
    var activeAfterCallbackFailure = await File.ReadAllBytesAsync(layout.ActiveCatalogPath);
    Require(activeBeforeCallbackFailure.AsSpan().SequenceEqual(activeAfterCallbackFailure),
        "An equal-version registration failure damaged the active catalog.");

    var repaired = await CortexProductInstaller.InstallVerifiedFilesForTestAsync(replacement, layout);
    Require(repaired.PreservedFiles >= replacement.Files.Count,
        "Retry did not reuse already completed, verified replacements.");
    Require(!Directory.EnumerateFiles(layout.InstallRoot, "*.tmp", SearchOption.AllDirectories).Any(),
        "Forward repair retained temporary file copies.");

    // Exercise a real immutable replacement, not only an idempotent repair,
    // while all three database transport files deny even read access.
    await File.WriteAllTextAsync(sourcePao, "pao-v3");
    replacement = replacement with
    {
        Files = replacement.Files.Select(file => file.RelativePath == "bin/pao.exe"
            ? Evidence(source, sourcePao) : file).ToArray()
    };
    heldData = dataFiles.Select(path => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)).ToArray();
    try
    {
        var programOnly = await CortexProductInstaller.InstallVerifiedFilesForTestAsync(replacement, layout);
        Require(programOnly.ReplacedFiles == 1 && !programOnly.DatabaseFilesTouched,
            "Program replacement failed while databases were exclusively locked.");
    }
    finally { foreach (var stream in heldData) stream.Dispose(); }
    foreach (var original in originalData)
        Require(File.ReadAllBytes(original.Path).AsSpan().SequenceEqual(original.Bytes) &&
                File.GetLastWriteTimeUtc(original.Path) == original.Modified,
            "Immutable replacement changed a database, WAL, or SHM file.");

    var inventory = CortexUninstallInventory.Write(replacement, layout);
    Require(inventory.Files.Count == package.Files.Count + 2,
        "The uninstall inventory did not include package files plus installed manifest evidence.");
    var stateSentinel = Path.Combine(layout.UserStateDirectory, "retained.db");
    Directory.CreateDirectory(layout.UserStateDirectory);
    await File.WriteAllTextAsync(stateSentinel, "retain");
    var changedInstalledFile = Path.Combine(layout.InstallRoot,
        rolePaths["host"].Replace('/', Path.DirectorySeparatorChar));
    await File.AppendAllTextAsync(changedInstalledFile, "-changed");
    var removal = CortexUninstallInventory.RemoveImmutableFiles(layout);
    Require(removal.RemovedFiles > 0 && removal.PreservedChangedFiles == 1,
        "Inventory removal did not remove exact files while preserving changed content.");
    Require(!File.Exists(installedPao) && File.Exists(changedInstalledFile) && File.Exists(stateSentinel),
        "Inventory removal did not preserve the mutable state or changed-file boundary.");
    foreach (var original in originalData)
        Require(File.ReadAllBytes(original.Path).AsSpan().SequenceEqual(original.Bytes),
            "Program uninstall modified an external database.");

    static CortexVerifiedPackageFileV1 Evidence(string root, string path)
    {
        using var stream = File.OpenRead(path);
        return new CortexVerifiedPackageFileV1(
            Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
            new FileInfo(path).Length,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)));
    }
}
