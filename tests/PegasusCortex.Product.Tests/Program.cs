using Pegasus.Cortex.Product;
using System.Text.Json;

if (args is ["--installed-user-layout"])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The installed user identity check requires Windows.");
    var execution = CortexProductLayout.ResolveExecution();
    try
    {
        _ = File.GetAttributes(execution.ServiceStateDirectory);
        throw new InvalidOperationException("Run this read-only check as the signed-in, non-elevated user whose service-state access is denied.");
    }
    catch (UnauthorizedAccessException) { }
    var semantic = CortexProductLayout.ResolveCurrent();
    if (execution.InstallRoot != semantic.InstallRoot || execution.OwnerSid != semantic.OwnerSid)
        throw new InvalidOperationException("User execution and semantic layouts resolved different product identities.");
    Console.WriteLine("Installed user execution and semantic layouts resolved while privileged service-state access remained denied; no state was created or modified.");
    return 0;
}

var tests = new (string Name, Action Body)[]
{
    ("one_root_layout", OneRootLayout),
    ("user_layout_excludes_privileged_service_probes", UserLayoutExcludesPrivilegedServiceProbes),
    ("separate_data_location", SeparateDataLocation),
    ("registered_data_location", RegisteredDataLocation),
    ("privileged_sid_rejected", PrivilegedSidRejected),
    ("catalog_requires_all_roles", CatalogRequiresAllRoles),
    ("catalog_rejects_state_inventory", CatalogRejectsStateInventory),
    ("catalog_accepts_independent_component_versions", CatalogAcceptsIndependentComponentVersions),
    ("catalog_accepts_legacy_without_modules", CatalogAcceptsLegacyWithoutModules),
    ("catalog_accepts_independent_module_versions", CatalogAcceptsIndependentModuleVersions),
    ("catalog_rejects_duplicate_module_role_ownership", CatalogRejectsDuplicateModuleRoleOwnership),
    ("catalog_reports_missing_module_requirement", CatalogReportsMissingModuleRequirement),
    ("catalog_reports_contract_mismatch", CatalogReportsContractMismatch),
    ("catalog_accepts_only_known_historical_upgrade_omission", CatalogAcceptsOnlyKnownHistoricalUpgradeOmission),
    ("migration_preserves_database_roles", MigrationPreservesDatabaseRoles)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Body();
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.GetType().Name}: {exception.Message}");
    }
}

if (failures.Count != 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine(failure);
    return 1;
}

Console.WriteLine($"Pegasus Cortex unified product checks passed ({tests.Length}/{tests.Length}).");
return 0;

static void OneRootLayout()
{
    var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pcx-product-layout"));
    var layout = CortexProductLayout.ForIsolatedRoot(root, "S-1-5-21-100-200-300-1001");
    layout.RequireRuntimePathsWithinInstallRoot();
    AssertUnder(layout.InstallRoot, layout.ServiceStateDirectory);
    AssertUnder(layout.InstallRoot, layout.UserStateDirectory);
    AssertUnder(layout.InstallRoot, layout.CortexDatabasePath);
    AssertUnder(layout.InstallRoot, layout.NsisCompilerPath);
    if (layout.NsisCompilerPath != Path.Combine(
            root, "tools", "nsis", "3.12", "Bin", "makensis.exe"))
        throw new InvalidOperationException("The product-local NSIS compiler path is not canonical.");
    if (!layout.UserStateDirectory.Contains(layout.OwnerSid, StringComparison.Ordinal))
        throw new InvalidOperationException("The user state path is not SID-scoped.");
}

static void PrivilegedSidRejected()
{
    foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
    {
        try
        {
            _ = CortexProductLayout.ForIsolatedRoot(
                Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pcx-product-layout")),
                sid);
            throw new InvalidOperationException("A privileged user-state owner SID was accepted.");
        }
        catch (ArgumentException)
        {
        }
    }
}

static void UserLayoutExcludesPrivilegedServiceProbes()
{
    var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pcx-private-service-layout"));
    var layout = CortexProductLayout.ForIsolatedRoot(root, "S-1-5-21-100-200-300-1001");
    var probed = new List<string>();
    void DeniedService(string path)
    {
        probed.Add(path);
        if (path == layout.ServiceStateDirectory)
            throw new UnauthorizedAccessException("Fixture models the existing SYSTEM-only service directory.");
    }
    layout.RequireExecutionPathsWithinInstallRoot(DeniedService);
    if (probed.Contains(layout.ServiceStateDirectory) || !probed.Contains(layout.UserExecutionDirectory) ||
        !probed.Contains(layout.ActiveCatalogPath) || !probed.Contains(layout.BinDirectory))
        throw new InvalidOperationException("User layout probes crossed into service state or omitted its own path checks.");
    try
    {
        layout.RequireServiceStatePath(DeniedService);
        throw new InvalidOperationException("Explicit service validation ignored denied service-state access.");
    }
    catch (UnauthorizedAccessException) { }
    try
    {
        layout.RequireServiceStatePath(_ => throw new IOException("Fixture reparse point."));
        throw new InvalidOperationException("Explicit service validation ignored a reparse rejection.");
    }
    catch (IOException) { }
    foreach (var invalid in new[] { Path.Combine(root, "other-service"), Path.Combine(Path.GetDirectoryName(root)!, "foreign-service") })
    {
        try
        {
            (layout with { ServiceStateDirectory = invalid }).RequireExecutionPathsWithinInstallRoot(_ => { });
            throw new InvalidOperationException("User-domain validation relaxed the service path's exact canonical containment.");
        }
        catch (InvalidDataException) { }
    }
}

static void SeparateDataLocation()
{
    var root = Path.Combine(Path.GetTempPath(), "pcx-data-layout");
    var program = Path.Combine(root, "program");
    var data = Path.Combine(root, "data");
    var first = CortexProductLayout.ForIsolatedRoots(program, data, "S-1-5-21-100-200-300-1001");
    var second = CortexProductLayout.ForIsolatedRoots(program, data, "S-1-5-21-100-200-300-1002");
    first.RequireRuntimePathsWithinDeclaredRoots();
    AssertUnder(data, first.CortexDatabasePath);
    AssertUnder(data, first.LegacyCortexDatabasePath);
    AssertUnder(data, first.UserBackupDirectory);
    AssertUnder(data, first.ServiceStateDirectory);
    AssertUnder(data, first.UserExecutionDirectory);
    AssertUnder(data, first.UserConfigDirectory);
    AssertUnder(data, first.UserLogDirectory);
    AssertUnder(data, first.InstallStateDirectory);
    AssertUnder(data, first.ActiveCatalogPath);
    AssertUnder(program, first.NsisCompilerPath);
    if (first.InstallRoot != second.InstallRoot || first.CortexDatabasePath == second.CortexDatabasePath)
        throw new InvalidOperationException("Common binaries did not retain distinct Windows-user databases.");
    foreach (var invalid in new[] { program, Path.Combine(program, "bin"), root })
    {
        try
        {
            _ = CortexProductLayout.ForIsolatedRoots(program, invalid, first.OwnerSid);
            throw new InvalidOperationException("An overlapping data location was accepted.");
        }
        catch (ArgumentException) { }
    }
}

static void RegisteredDataLocation()
{
    var root = Path.Combine(Path.GetTempPath(), "pcx-location-" + Guid.NewGuid().ToString("N"));
    var program = Path.Combine(root, "program");
    var data = Path.Combine(root, "data");
    Directory.CreateDirectory(data);
    try
    {
        var layout = CortexProductLayout.ForIsolatedRoots(program, data, "S-1-5-21-100-200-300-1001");
        var selected = CortexDataLocation.ApplyRegistration(layout, program, data);
        if (selected.DataRoot != data || Directory.Exists(selected.CortexDataDirectory))
            throw new InvalidOperationException("Registration resolution created state or lost the selected root.");
        var legacy = CortexDataLocation.ApplyRegistration(layout, program, null);
        if (legacy.DataRoot != Path.Combine(program, "state"))
            throw new InvalidOperationException("Missing legacy DataLocation changed the existing placement.");
        foreach (var registration in new[] { (Install: (string?)null, Data: data), (Install: Path.Combine(root, "other"), Data: data) })
        {
            try
            {
                _ = CortexDataLocation.ApplyRegistration(layout, registration.Install, registration.Data);
                throw new InvalidOperationException("An unbound or mismatched registration was accepted.");
            }
            catch (InvalidDataException) { }
        }
        Directory.Delete(data);
        try
        {
            _ = CortexDataLocation.ApplyRegistration(layout, program, data);
            throw new InvalidOperationException("Unavailable registered data silently fell back or was created.");
        }
        catch (IOException) { }
        if (Directory.Exists(data))
            throw new InvalidOperationException("Unavailable data root was silently recreated.");
    }
    finally
    {
        if (Directory.Exists(data)) Directory.Delete(data);
        Directory.Delete(root);
    }
}

static void CatalogRequiresAllRoles()
{
    try
    {
        _ = CortexProductCatalogV1.Create(
            "1.2026.08.28.12.00.A",
            cortexMcpSchema: 2,
            semanticStateSchema: 2,
            executionStateSchema: 1,
            [new CortexProductRoleV1("mcp", "semantics", "1.2026.08.28.12.00.A", "bin/semantics/PegasusCortex.exe")]);
        throw new InvalidOperationException("An incomplete product catalog was accepted.");
    }
    catch (InvalidDataException)
    {
    }
}

static void CatalogRejectsStateInventory()
{
    var roles = Roles();
    roles[0] = new CortexProductRoleV1(
        "mcp", "semantics", "1.2026.08.28.12.00.A", "state/users/db.sqlite");
    try
    {
        _ = CortexProductCatalogV1.Create(
            "1.2026.08.28.12.00.A", 2, 2, 1, roles);
        throw new InvalidOperationException("Mutable state was accepted as immutable package content.");
    }
    catch (InvalidDataException)
    {
    }
}

static void CatalogAcceptsIndependentComponentVersions()
{
    var roles = Roles();
    roles[1] = roles[1] with { ComponentVersion = "1.2026.08.28.12.01.A" };
    roles[2] = roles[2] with { ComponentVersion = "1.2026.08.28.12.02.A" };
    roles[3] = roles[3] with { ComponentVersion = "1.2026.08.28.12.03.A" };
    var catalog = CortexProductCatalogV1.Create(
        "1.2026.08.28.12.04.A", 2, 2, 1, roles);
    if (catalog.Roles.Single(role => role.Role == "host").ComponentVersion == catalog.ReleaseVersion ||
        catalog.Roles.Single(role => role.Role == "service").ComponentVersion == catalog.ReleaseVersion ||
        catalog.Roles.Single(role => role.Role == "pao").ComponentVersion == catalog.ReleaseVersion)
    {
        throw new InvalidOperationException("Independent component versions were collapsed to the bundle release.");
    }
}

static void CatalogAcceptsLegacyWithoutModules()
{
    var catalog = CortexProductCatalogV1.Create(
        "1.2026.08.28.12.00.A", 2, 2, 1, Roles());
    if (catalog.Modules is not null)
        throw new InvalidOperationException("A legacy catalog acquired an implicit module inventory.");

    var root = Path.Combine(Path.GetTempPath(), "pcx-legacy-module-catalog-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var path = Path.Combine(root, "catalog.json");
        var json = JsonSerializer.Serialize(catalog, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var withoutModules = document.RootElement.EnumerateObject()
            .Where(property => property.Name != "modules")
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        File.WriteAllText(path, JsonSerializer.Serialize(withoutModules));
        if (CortexProductCatalogV1.ReadExact(path).Modules is not null)
            throw new InvalidOperationException("An omitted legacy module inventory did not remain omitted.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void CatalogAcceptsIndependentModuleVersions()
{
    var modules = Modules();
    modules[1] = modules[1] with { ModuleVersion = "1.2026.08.28.12.01.A" };
    modules[3] = modules[3] with { ModuleVersion = "1.2026.08.28.12.02.A" };
    var catalog = CortexProductCatalogV1.Create(
        "1.2026.08.28.12.04.A", 2, 2, 1, Roles(), modules);
    if (catalog.Modules is null ||
        catalog.Modules.Single(module => module.ModuleId == "semantics.local").ModuleVersion == catalog.ReleaseVersion ||
        catalog.Modules.Single(module => module.ModuleId == "pao.client").ModuleVersion == catalog.ReleaseVersion)
    {
        throw new InvalidOperationException("Independent module versions were collapsed to the product release.");
    }
}

static void CatalogRejectsDuplicateModuleRoleOwnership()
{
    var modules = Modules().ToList();
    modules.Add(new CortexProductModuleV1(
        "integration.invalid", "integration", "1.2026.08.28.12.00.A", true, "none",
        ["mcp"], ["integration.invalid/1"], []));
    RejectInvalidModuleInventory(
        () => CortexProductCatalogV1.Create(
            "1.2026.08.28.12.00.A", 2, 2, 1, Roles(), modules),
        "Two modules were allowed to own the same executable role.");
}

static void CatalogReportsMissingModuleRequirement()
{
    var modules = Modules()
        .Where(module => module.ModuleId != "storage.sqlite")
        .ToArray();
    try
    {
        _ = CortexProductCatalogV1.Create(
            "1.2026.08.28.12.00.A", 2, 2, 1, Roles(), modules);
    }
    catch (CortexProductContractMismatchException exception)
    {
        if (exception.Component == "semantics.local" &&
            exception.Contract == "module requirement" &&
            exception.Expected == "storage.sqlite/2" &&
            exception.Actual == "missing")
        {
            return;
        }
        throw new InvalidOperationException("The catalog returned incorrect module-mismatch details.", exception);
    }
    throw new InvalidOperationException("A catalog with a missing storage provider was accepted.");
}

static void RejectInvalidModuleInventory(Func<CortexProductCatalogV1> action, string message)
{
    try { _ = action(); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException(message);
}

static void CatalogReportsContractMismatch()
{
    var current = CortexProductCatalogV1.Create(
        "1.2026.08.28.12.00.A", 2, 2, 1, Roles());
    RequireMismatch(
        current with { ExecutionProtocol = "2.0" },
        component: "execution", contract: "protocol", expected: "1.0", actual: "2.0");
    RequireMismatch(
        current with { PaoSchema = 4 },
        component: "pao", contract: "schema", expected: "3", actual: "4");

    static void RequireMismatch(
        CortexProductCatalogV1 catalog,
        string component,
        string contract,
        string expected,
        string actual)
    {
        try { _ = catalog.Validate(); }
        catch (CortexProductContractMismatchException exception)
        {
            if (exception.Component == component && exception.Contract == contract &&
                exception.Expected == expected && exception.Actual == actual)
                return;
            throw new InvalidOperationException("The catalog returned incorrect bounded contract-mismatch details.", exception);
        }
        throw new InvalidOperationException("An incompatible catalog contract was accepted.");
    }
}

static void CatalogAcceptsOnlyKnownHistoricalUpgradeOmission()
{
    var root = Path.Combine(Path.GetTempPath(), "pcx-historical-catalog-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var current = CortexProductCatalogV1.Create(
            "1.2026.09.14.01.55.A", 2, 2, 1, Roles());
        var path = Path.Combine(root, "active.v1.json");
        var json = JsonSerializer.Serialize(current, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var historical = json.Replace("\"applicationAuthorization\":1,", "", StringComparison.Ordinal);
        if (historical == json)
            throw new InvalidOperationException("The historical catalog fixture did not omit the intended schema field.");
        File.WriteAllText(path, historical);

        Reject(() => CortexProductCatalogV1.ReadExact(path),
            "Strict candidate/runtime validation accepted the historical omission.");
        var compatible = CortexProductCatalogV1.ReadInstalledUpgradeSource(path);
        if (compatible.ReleaseVersion != current.ReleaseVersion ||
            compatible.StateSchemas.ApplicationAuthorization != 0)
        {
            throw new InvalidOperationException("The historical upgrade reader changed catalog identity.");
        }

        File.WriteAllText(path, json.Replace(
            "\"applicationAuthorization\":1", "\"applicationAuthorization\":0", StringComparison.Ordinal));
        Reject(() => CortexProductCatalogV1.ReadInstalledUpgradeSource(path),
            "An explicit invalid application-authorization schema was treated as a historical omission.");

        static void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException(message);
        }
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void MigrationPreservesDatabaseRoles()
{
    var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pcx-product-migration"));
    var layout = CortexProductLayout.ForIsolatedRoot(root, "S-1-5-21-100-200-300-1001");
    var plan = CortexStateMigrationPlanV1.Create(
        layout,
        new CortexLegacyStateRootsV1(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "legacy-cortex")),
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "legacy-agentops-service")),
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "legacy-agentops-user"))));
    var databaseRoles = plan.Items
        .Where(item => item.Strategy == CortexStateMigrationStrategy.SqliteOnlineBackup)
        .Select(item => item.Role)
        .ToHashSet(StringComparer.Ordinal);
    foreach (var required in new[] { "cortex-v2", "execution-jobs", "execution-router", "execution-feedback" })
    {
        if (!databaseRoles.Contains(required))
            throw new InvalidOperationException($"The state plan omitted database role '{required}'.");
    }
    foreach (var item in plan.Items)
        AssertUnder(layout.InstallRoot, item.DestinationPath);
}

static CortexProductRoleV1[] Roles() =>
[
    new("mcp", "semantics", "1.2026.08.28.12.00.A", "bin/semantics/PegasusCortex.exe"),
    new("host", "execution", "1.2026.08.28.12.00.A", "bin/host/PegasusCortex.Host.exe"),
    new("service", "execution", "1.2026.08.28.12.00.A", "bin/service/PegasusCortex.ExecutionService.exe"),
    new("pao", "execution", "1.2026.08.28.12.00.A", "bin/pao.exe"),
    new("process-host", "execution", "1.2026.08.28.12.00.A", "bin/host/Pegasus.AgentOps.ProcessHost.exe"),
    new("ui-host", "execution", "1.2026.08.28.12.00.A", "bin/ui/Pegasus.AgentOps.UiHost.exe"),
    new("launcher", "launcher", "1.2026.08.28.12.00.A", "PegasusCortex.exe"),
    new("setup", "installer", "1.2026.08.28.12.00.A", "installer/PegasusCortex.Setup.exe")
];

static CortexProductModuleV1[] Modules() =>
[
    new("core.bootstrap", "core", "1.2026.08.28.12.00.A", true, "none",
        ["launcher", "setup"], ["product.catalog/1"], []),
    new("semantics.local", "runtime", "1.2026.08.28.12.00.A", true, "none",
        ["mcp"], ["cortex.mcp/2"], ["storage.sqlite/2"]),
    new("execution.local", "runtime", "1.2026.08.28.12.00.A", true, "preserve",
        ["host", "service", "process-host", "ui-host"], ["execution.local/1"], []),
    new("pao.client", "tooling", "1.2026.08.28.12.00.A", true, "none",
        ["pao"], ["pao/3"], ["execution.local/1"]),
    new("storage.sqlite", "storage", "1.2026.08.28.12.00.A", true, "preserve",
        [], ["storage.sqlite/2"], []),
    new("knowledge.shared", "knowledge", "1.2026.08.28.12.00.A", false, "preserve",
        [], ["knowledge.shared/1"], []),
    new("guidance.codex", "guidance", "1.2026.08.28.12.00.A", false, "none",
        [], ["guidance.codex/2"], [])
];

static void AssertUnder(string root, string path)
{
    var relative = Path.GetRelativePath(root, path);
    if (Path.IsPathRooted(relative) || relative == ".." ||
        relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Path escaped the product root: {path}");
    }
}
