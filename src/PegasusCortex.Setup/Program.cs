using System.Security.Principal;
using System.Text.Json;
using Pegasus.Cortex.Product;
using Pegasus.Cortex.Setup;

CortexSetupReporter setupReporter = CortexSetupReporter.ConsoleOnly();
try
{
if (args is ["install-execution-service", "--install-root", var serviceInstallRoot])
{
    var receipt = CortexOptionalServiceOperation.Install(serviceInstallRoot);
    Console.WriteLine(JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}

if (args.Length > 0 && args[0] == "start-user-session")
{
    if (args.Length is not (3 or 4) || args[1] != "--install-root" ||
        args.Length == 4 && args[3] != "--dashboard")
        throw new ArgumentException("Use start-user-session --install-root <root> [--dashboard].");
    setupReporter = setupReporter.PersistForElevatedRegisteredOperation(args[2]);
    setupReporter.Stage("user-session", "starting");
    var receipt = CortexUserSessionStartup.Start(args[2], dashboard: args.Length == 4);
    setupReporter.Stage("user-session", "ready");
    Console.WriteLine(JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}

if (args is ["prune-program-versions", "--install-root", var cleanupRoot])
{
    var layout = CortexProductLayout.ResolveCurrent();
    if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cleanupRoot)),
            layout.InstallRoot, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Obsolete program cleanup requires the registered Cortex installation.");
    setupReporter = setupReporter.PersistForElevatedRegisteredOperation(cleanupRoot);
    setupReporter.Stage("obsolete-programs", "checking");
    var receipt = CortexObsoleteProgramCleanup.Remove(layout);
    setupReporter.Stage("obsolete-programs", "complete");
    Console.WriteLine(JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}

if (args is ["layout"])
{
    var layout = CortexProductLayout.ResolveCurrent();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        product = CortexProductIdentity.Product,
        installRoot = layout.InstallRoot,
        dataRoot = layout.DataRoot,
        ownerSid = layout.OwnerSid,
        cortexDatabase = layout.CortexDatabasePath,
        serviceState = layout.ServiceStateDirectory,
        userState = layout.UserStateDirectory
    }));
    return 0;
}

if (args is ["installer-state", "--install-root", var installerRoot])
{
    var ownerSid = WindowsIdentity.GetCurrent().User?.Value ??
        throw new InvalidOperationException("The current Windows user SID is unavailable.");
    var layout = CortexProductLayout.ForInstallRoot(Path.GetFullPath(installerRoot), ownerSid);
    var state = CortexInstallerState.Detect(layout);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        codexRegister = state.CodexRegister,
        codexGuidance = state.CodexGuidance,
        trust = state.TrustRecommendation.ToString().ToLowerInvariant()
    }));
    return state.ExitCode;
}

if (args is ["prepare-execution-state", "--install-root", var preparationRoot,
        "--evidence", var preparationEvidence, "--sha256", var preparationSha])
{
    var ownerSid = WindowsIdentity.GetCurrent().User?.Value ??
        throw new InvalidOperationException("The current Windows user SID is unavailable.");
    var layout = CortexProductLayout.ForInstallRoot(Path.GetFullPath(preparationRoot), ownerSid);
    if (layout.RegisteredDataRoot is null)
        throw new InvalidOperationException("Execution-state preparation requires the registered installation and data location.");
    var receipt = CortexExecutionStatePreparation.Prepare(layout, preparationEvidence, preparationSha);
    Console.WriteLine(JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}

if (args is ["migrate-current-state"])
{
    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var ownerSid = WindowsIdentity.GetCurrent().User?.Value ??
        throw new InvalidOperationException("The current Windows user SID is unavailable.");
    var layout = CortexProductLayout.ResolveCurrent();
    CortexProductStateProvisioner.Provision(layout);
    var plan = CortexStateMigrationPlanV1.Create(
        layout,
        new CortexLegacyStateRootsV1(
            Path.Combine(local, "Pegasus", "Cortex"),
            Path.Combine(common, "Pegasus", "AgentOps"),
            Path.Combine(local, "Pegasus", "AgentOps")));
    var receipt = await new CortexStateMigrationExecutor(privateScratchBase: layout.InstallStateDirectory).ExecuteAsync(plan);
    Console.WriteLine(JsonSerializer.Serialize(receipt));
    return 0;
}

if (args is ["activate-catalog", var candidateCatalog])
{
    var layout = CortexProductLayout.ResolveCurrent();
    CortexProductCatalogActivator.Activate(layout, candidateCatalog);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        contract = CortexProductIdentity.CatalogContract,
        activeCatalog = layout.ActiveCatalogPath
    }));
    return 0;
}

if (args is ["validate-catalog", var catalogPath])
{
    var catalog = CortexProductCatalogV1.ReadExact(Path.GetFullPath(catalogPath));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        catalog.Contract,
        catalog.Version,
        catalog.Product,
        catalog.ReleaseVersion,
        catalog.Channel,
        roles = catalog.Roles.Count
    }));
    return 0;
}

if (args is ["verify-package", var packageRoot])
{
    var package = CortexSignedProductPackageVerifier.Verify(Path.GetFullPath(packageRoot));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        package.ReleaseVersion,
        package.PublisherThumbprint,
        package.RootThumbprint,
        files = package.Files.Count
    }));
    return 0;
}

if (args.Length is 2 or 3 && string.Equals(args[0], "install-codex-guidance", StringComparison.Ordinal))
{
    var guidancePackageRoot = args[1];
    var agentsPath = args.Length == 3
        ? args[2]
        : CortexCodexGuidanceInstaller.ResolveDefaultTarget();
    CortexVerifiedProductPackageV1 package;
    try
    {
        package = CortexSignedProductPackageVerifier.Verify(Path.GetFullPath(guidancePackageRoot));
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        throw new CortexProductInstallStageException("verify-package", null, exception);
    }
    var catalog = CortexProductCatalogV1.ReadExact(package.CatalogPath);
    var receipt = CortexCodexGuidanceInstaller.ApplyFromVerifiedPackage(package, catalog, agentsPath);
    Console.WriteLine(JsonSerializer.Serialize(receipt));
    return 0;
}

if (args.Length >= 2 && string.Equals(args[0], "configure-codex-feature", StringComparison.Ordinal))
{
    var request = CortexSetupCommandLine.ParseCodexFeature(args);
    setupReporter = setupReporter.PersistForElevatedRegisteredOperation(request.InstallRoot);
    setupReporter.Stage("codex-feature", "started", $"feature={request.Feature}; action={request.Action}");
    var receipt = CortexCodexFeatureOperation.Execute(request);
    setupReporter.Stage("codex-feature", "complete", $"feature={receipt.Feature}; action={receipt.Action}; disposition={receipt.Disposition}");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        receipt.Feature,
        receipt.Action,
        receipt.Disposition,
        receipt.Installed
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}

if (args.Length >= 2 && string.Equals(args[0], "install-package", StringComparison.Ordinal))
{
    var options = CortexSetupCommandLine.ParseInstallPackage(args);
    if (options.CodexRegister is not null || options.CodexGuidance is not null ||
        options.CodexRegisterAction is not null || options.CodexGuidanceAction is not null ||
        options.CodexHome is not null || options.CodexAgentsPath is not null)
        throw new ArgumentException(
            "Core product installation cannot configure optional Codex features. Use one explicit configure-codex-feature operation per selected feature.");
    var admittedPackageRoot = options.PackageRoot;
    CortexVerifiedProductPackageV1 package;
    try
    {
        setupReporter.Stage("verify-package", "started");
        package = CortexSignedProductPackageVerifier.Verify(Path.GetFullPath(admittedPackageRoot));
        setupReporter.Stage("verify-package", "complete", $"release={package.ReleaseVersion}; files={package.Files.Count}");
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        throw new CortexProductInstallStageException("verify-package", null, exception);
    }

    CortexProductLayout layout;
    CortexDataRootRelocation? relocation = null;
    try
    {
        layout = options.InstallRoot is null
            ? CortexProductLayout.ResolveCurrent()
            : CortexProductLayout.ForInstallRoot(
                Path.GetFullPath(options.InstallRoot),
                WindowsIdentity.GetCurrent().User?.Value ??
                    throw new InvalidOperationException("The current Windows user SID is unavailable."));
        var establishedDataRoot = CortexProductInstallPlanner.ReadEstablishedDataRoot(layout);
        if (establishedDataRoot is not null)
            layout = layout.WithDataRoot(establishedDataRoot);
        var requestedDataRoot = options.DataRoot ??
            (File.Exists(layout.ActiveCatalogPath) || establishedDataRoot is not null ? layout.DataRoot : CortexDataLocation.DefaultDataRoot());
        if (requestedDataRoot is not null)
        {
            var selected = layout.WithDataRoot(requestedDataRoot);
            if ((File.Exists(layout.ActiveCatalogPath) || establishedDataRoot is not null) && !string.Equals(
                    selected.DataRoot, layout.DataRoot, StringComparison.OrdinalIgnoreCase))
                relocation = CortexDataRootRelocation.Plan(layout, selected.DataRoot);
            else layout = selected;
        }
        // Upgrades already have an admitted, administrator-owned install log
        // boundary. Begin persisting before lifecycle preflight so a failure
        // that occurs before file provisioning is not reduced to an opaque
        // wrapper exit code. Fresh installs begin persistence after the state
        // boundary is provisioned below.
        if (relocation is null && Directory.Exists(layout.InstallStateDirectory))
            setupReporter = setupReporter.PersistForElevatedRegisteredOperation(layout.InstallRoot);
        setupReporter.Stage("resolve-layout", "complete", layout.InstallRoot);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        throw new CortexProductInstallStageException("resolve-layout", null, exception);
    }
    CortexProductInstallPlanV1 plan;
    try
    {
        setupReporter.Stage("preflight", "started");
        plan = CortexProductInstallPlanner.Create(package, layout);
        CortexWindowsProductRegistration.RequireExistingProductLocations(layout);
        // Reject an unowned legacy intermediate before stopping installed components.
        CortexProductStateProvisioner.ValidateLegacySemanticParent(layout, requireWritable: relocation is null);
        setupReporter.Stage("preflight", "complete", $"mode={plan.Mode}; required={plan.RequiredFreeBytes}; available={plan.AvailableFreeBytes}");
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        throw new CortexProductInstallStageException("preflight", layout.InstallRoot, exception);
    }
    CortexProductInstallLifecycleReceiptV1 lifecycle;
    try
    {
        setupReporter.Stage("prepare-lifecycle", "started");
        lifecycle = CortexProductInstallLifecycle.Prepare(layout);
        setupReporter.Stage("prepare-lifecycle", "complete", $"serviceStopped={lifecycle.ServiceStopped}; processesStopped={lifecycle.ProductProcessesStopped}");
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        throw new CortexProductInstallStageException("prepare-lifecycle", null, exception);
    }

    using var userHostAdmission = CortexProductInstallLifecycle.HoldUserHostAdmission(layout);
    CortexProductInstallReceiptV1 install;
    CortexWindowsRegistrationReceiptV1? registration = null;
    CortexUninstallInventoryV1? inventory = null;
    var installStarted = false;
    try
    {
        if (relocation is not null)
        {
            setupReporter.Stage("copy-state", "started", "raw files; originals retained; no database conversion");
            try { relocation.Prepare(); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { throw new CortexProductInstallStageException("copy-state", relocation.Staging.DataRoot, exception); }
            layout = relocation.InstallLayout;
            plan = CortexProductInstallPlanner.Create(package, layout, relocation);
            setupReporter.Stage("copy-state", "complete", $"files={relocation.CopiedFiles}; bytes={relocation.CopiedBytes}");
        }
        setupReporter.Stage("install-product", "started", $"mode={plan.Mode}");
        void RegisterInstalled(CortexProductCatalogV1 catalog)
            {
                try
                {
                    setupReporter.Stage("write-uninstall-inventory", "started");
                    inventory = CortexUninstallInventory.Write(package, layout);
                    setupReporter.Stage("write-uninstall-inventory", "complete", $"files={inventory.Files.Count}");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new CortexProductInstallStageException("write-uninstall-inventory", null, exception);
                }
                try
                {
                    if (relocation is not null)
                    {
                        setupReporter = setupReporter.WithoutPersistence();
                        layout = relocation.Publish();
                        setupReporter = setupReporter.PersistUnder(layout.InstallStateDirectory);
                    }
                    setupReporter.Stage("register-product", "started");
                    if (relocation is null) registration = CortexWindowsProductRegistration.Apply(layout, catalog);
                    else relocation.CommitSelection(target =>
                        registration = CortexWindowsProductRegistration.ApplyRelocated(target, catalog, relocation));
                    if (registration is null)
                        throw new InvalidOperationException("Product registration did not return a result.");
                    setupReporter.Stage("register-product", "complete", registration.ServiceExecutable);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new CortexProductInstallStageException("register-product", null, exception);
                }
            }
        void ReportProvisioned(CortexProductLayout provisioned)
            {
                setupReporter = setupReporter.PersistUnder(provisioned.InstallStateDirectory);
                setupReporter.Stage("provision-state", "complete", $"dataLocation={provisioned.DataRoot}; databaseFilesTouched=false");
            }
        installStarted = true;
        install = relocation is null
            ? await CortexProductInstaller.InstallVerifiedFilesAndRegisterAsync(package, layout, null,
                RegisterInstalled, CancellationToken.None, ReportProvisioned)
            : await CortexProductInstaller.InstallRelocatedFilesAndRegisterAsync(package, relocation,
                RegisterInstalled, CancellationToken.None, ReportProvisioned);
        if (relocation is not null)
            install = install with { DataRoot = layout.DataRoot, ActiveCatalogPath = layout.ActiveCatalogPath };
        setupReporter.Stage("install-product", "complete", $"created={install.CreatedFiles}; replaced={install.ReplacedFiles}; preserved={install.PreservedFiles}");
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        setupReporter.Stage(installStarted ? "install-product" : "copy-state", installStarted ? "repair-required" : "stopped",
            installStarted
                ? "Program installation stopped; any completed replacements retained. Databases preserved."
                : "Copy stopped; originals retained; data location not selected.");
        if (exception is CortexProductInstallStageException)
            throw;
        throw new CortexProductInstallStageException(installStarted ? "install-product" : "copy-state", null, exception);
    }

    if (registration is null || inventory is null)
        throw new CortexProductInstallStageException(
            "register-product", null, new InvalidOperationException("The install transaction did not produce registration evidence."));
    setupReporter.Stage("setup", "complete", $"mode={plan.Mode}; release={package.ReleaseVersion}");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        mode = plan.Mode.ToString().ToLowerInvariant(),
        release = package.ReleaseVersion,
        created = install.CreatedFiles,
        replaced = install.ReplacedFiles,
        preserved = install.PreservedFiles,
        serviceStopped = lifecycle.ServiceStopped,
        runtimeKeptStopped = lifecycle.RuntimeKeptStopped,
        processesStopped = lifecycle.ProductProcessesStopped,
        service = registration.ServiceName,
        inventoryFiles = inventory.Files.Count,
        dataRelocated = relocation is not null,
        originalDataRetained = relocation is not null ? true : (bool?)null
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
    return 0;
}

if (args is ["remove-product-files"])
{
    var layout = CortexProductLayout.ResolveCurrent();
    Console.WriteLine(JsonSerializer.Serialize(CortexUninstallInventory.RemoveImmutableFiles(layout)));
    return 0;
}

if (args is ["unregister-product"])
{
    var layout = CortexProductLayout.ResolveCurrent();
    var catalog = CortexProductCatalogV1.ReadExact(layout.ActiveCatalogPath);
    var receipt = CortexWindowsProductRegistration.Remove(layout, catalog);
    Console.WriteLine(JsonSerializer.Serialize(receipt));
    return 0;
}

if (args is ["remove-state", var explicitConfirmation])
{
    var receipt = CortexProductStateRemoval.RemoveAll(
        CortexProductLayout.ResolveCurrent(), explicitConfirmation);
    Console.WriteLine(JsonSerializer.Serialize(receipt));
    return 0;
}

Console.Error.WriteLine("Usage: PegasusCortex.Setup.exe [layout|installer-state --install-root <root>|migrate-current-state|activate-catalog <catalog.json>|validate-catalog <catalog.json>|verify-package <package-root>|install-package <package-root> [--install-root <root>] [--data-root <root>]|configure-codex-feature <package-root> --install-root <root> --feature registration|guidance --action install|remove [--codex-home <home>] [--codex-agents <AGENTS.md>]|start-user-session --install-root <root> [--dashboard]|prune-program-versions --install-root <root>|unregister-product|remove-product-files|remove-state remove-all-state]");
return 2;
}
catch (Exception exception)
{
    var stageException = exception as CortexProductInstallStageException;
    var cause = stageException?.InnerException ?? exception;
    var cleanupFailure = cause as CortexObsoleteProgramCleanupException;
    var failureStage = stageException?.Stage ?? cleanupFailure?.Stage ?? cause.Data["CortexStartupStage"] as string;
    var (error, exitCode, remedy) = cause switch
    {
        CortexCodexRunningException =>
            ("prerequisite", 3, "Close every Codex window completely, then run the signed installer again. Cortex was not interrupted."),
        CortexProductContractMismatchException mismatch =>
            ("contract_mismatch", 3, $"Install a signed {mismatch.Component} module compatible with {mismatch.Contract} '{mismatch.Expected}', then retry."),
        UnauthorizedAccessException or System.Security.SecurityException =>
            ("access", 4, "Run the signed setup from an elevated administrator process and verify the admitted Program Files ACL boundary."),
        ArgumentException or InvalidDataException or FormatException =>
            ("invalid", 3, "Verify the exact signed package, catalog, command shape, and canonical local paths, then retry."),
        IOException =>
            ("io", 8, "Close conflicting product processes if identified, verify the admitted filesystem boundary, and retry the same signed package."),
        System.ComponentModel.Win32Exception =>
            ("windows_error", 8, "Inspect the Windows service, registry, privilege, and ACL diagnostic for the fixed setup operation."),
        _ =>
            ("internal", 8, "Inspect the bounded setup diagnostic and Windows Application log before retrying; no trust change is implied.")
    };
    try { setupReporter.Failure(error, failureStage, stageException?.Target, cause, remedy); }
    catch { }
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        ok = false,
        rid = setupReporter.Rid,
        op = args.FirstOrDefault() ?? "",
        err = error,
        cause = cause.GetType().Name,
        stage = failureStage,
        target = stageException?.Target,
        diagnostic = cause.Message.Replace('\r', ' ').Replace('\n', ' ').Trim() is var message && message.Length <= 512 ? message : message[..512],
        log = setupReporter.LogPath,
        programCleanup = cause is CortexObsoleteProgramCleanupException obsolete ? new
        {
            cause = obsolete.InnerException?.GetType().Name,
            win32 = (obsolete.InnerException as System.ComponentModel.Win32Exception)?.NativeErrorCode,
            removed = obsolete.Receipt.RemovedFiles,
            missing = obsolete.Receipt.MissingFiles,
            preservedChanged = obsolete.Receipt.PreservedChangedFiles,
            preservedUnknown = obsolete.Receipt.PreservedUnknownEntries,
            databaseFilesTouched = false,
            backupCreated = false
        } : null,
        contractMismatch = cause is CortexProductContractMismatchException contractFailure ? new
        {
            contractFailure.Component,
            contractFailure.Contract,
            contractFailure.Expected,
            contractFailure.Actual
        } : null,
        remedy
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
    return exitCode;
}
finally
{
    // Logger closure is diagnostic only and cannot change the original exit.
    try { setupReporter.Close(); } catch { }
}
