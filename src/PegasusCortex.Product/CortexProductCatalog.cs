using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pegasus.Cortex.Product;

public sealed class CortexProductContractMismatchException : IOException
{
    public CortexProductContractMismatchException(
        string component,
        string contract,
        string expected,
        string actual)
        : base($"Cortex component '{component}' has incompatible {contract}: expected '{expected}', actual '{actual}'.")
    {
        Component = component;
        Contract = contract;
        Expected = expected;
        Actual = actual;
    }

    public string Component { get; }
    public string Contract { get; }
    public string Expected { get; }
    public string Actual { get; }
}

public sealed record CortexProductRoleV1(
    string Role,
    string Component,
    string ComponentVersion,
    string RelativePath);

public sealed record CortexProductStateSchemasV1(
    int Install,
    int Semantic,
    int Execution,
    int ApplicationAuthorization,
    int UserExecution,
    int Tray);

public sealed record CortexProductCatalogV1(
    string Contract,
    int Version,
    string Product,
    string ReleaseVersion,
    string Channel,
    string ExecutionProtocol,
    int CortexMcpSchema,
    int PaoSchema,
    CortexProductStateSchemasV1 StateSchemas,
    IReadOnlyList<CortexProductRoleV1> Roles,
    IReadOnlyList<CortexProductModuleV1>? Modules = null)
{
    private static readonly string[] RequiredRoles =
    [
        "mcp", "host", "service", "pao", "process-host", "ui-host", "launcher", "setup"
    ];

    private static readonly IReadOnlyDictionary<string, string> RoleComponents =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mcp"] = "semantics",
            ["host"] = "execution",
            ["service"] = "execution",
            ["pao"] = "execution",
            ["process-host"] = "execution",
            ["ui-host"] = "execution",
            ["launcher"] = "launcher",
            ["setup"] = "installer"
        };

    public static readonly IReadOnlyDictionary<string, string> StableRolePaths =
        new Dictionary<string, string>(StringComparer.Ordinal)
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

    public static CortexProductCatalogV1 Create(
        string releaseVersion,
        int cortexMcpSchema,
        int semanticStateSchema,
        int executionStateSchema,
        IReadOnlyList<CortexProductRoleV1> roles,
        IReadOnlyList<CortexProductModuleV1>? modules = null) =>
        new CortexProductCatalogV1(
            CortexProductIdentity.CatalogContract,
            CortexProductIdentity.CatalogVersion,
            CortexProductIdentity.Product,
            releaseVersion,
            CortexProductIdentity.Channel,
            CortexProductIdentity.ExecutionProtocol,
            cortexMcpSchema,
            CortexProductIdentity.PaoSchema,
            new CortexProductStateSchemasV1(
                Install: 1,
                Semantic: semanticStateSchema,
                Execution: executionStateSchema,
                ApplicationAuthorization: 1,
                UserExecution: executionStateSchema,
                Tray: 1),
            roles,
            modules).Validate();

    public CortexProductCatalogV1 Validate() =>
        ValidateCore(
            allowLegacyApplicationAuthorizationOmission: false,
            allowLegacyVersionedRolePaths: false);

    private CortexProductCatalogV1 ValidateCore(
        bool allowLegacyApplicationAuthorizationOmission,
        bool allowLegacyVersionedRolePaths)
    {
        if (ExecutionProtocol != CortexProductIdentity.ExecutionProtocol)
        {
            throw new CortexProductContractMismatchException(
                "execution",
                "protocol",
                CortexProductIdentity.ExecutionProtocol,
                ExecutionProtocol);
        }
        if (PaoSchema != CortexProductIdentity.PaoSchema)
        {
            throw new CortexProductContractMismatchException(
                "pao",
                "schema",
                CortexProductIdentity.PaoSchema.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PaoSchema.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var applicationAuthorizationIsValid = StateSchemas.ApplicationAuthorization >= 1 ||
            allowLegacyApplicationAuthorizationOmission && StateSchemas.ApplicationAuthorization == 0;
        if (Contract != CortexProductIdentity.CatalogContract ||
            Version != CortexProductIdentity.CatalogVersion ||
            Product != CortexProductIdentity.Product ||
            Channel != CortexProductIdentity.Channel ||
            !CortexProductIdentity.IsReleaseVersion(ReleaseVersion) ||
            CortexMcpSchema < 1 ||
            StateSchemas.Install < 1 ||
            StateSchemas.Semantic < 1 ||
            StateSchemas.Execution < 1 ||
            !applicationAuthorizationIsValid ||
            StateSchemas.UserExecution < 1 ||
            StateSchemas.Tray < 1)
        {
            throw new InvalidDataException("The unified Cortex product catalog identity is invalid.");
        }

        var coordinates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in Roles)
        {
            if (string.IsNullOrWhiteSpace(role.Role) ||
                !coordinates.Add(role.Role) ||
                !RoleComponents.TryGetValue(role.Role, out var expectedComponent) ||
                !string.Equals(role.Component, expectedComponent, StringComparison.Ordinal) ||
                !CortexProductIdentity.IsReleaseVersion(role.ComponentVersion) ||
                !IsSafeImmutableRelativePath(role.RelativePath) ||
                !HasAdmittedRolePath(role, allowLegacyVersionedRolePaths))
            {
                throw new InvalidDataException("The unified Cortex product role inventory is invalid.");
            }
        }
        foreach (var required in RequiredRoles)
        {
            if (!coordinates.Contains(required))
                throw new InvalidDataException($"The unified Cortex product catalog is missing role '{required}'.");
        }
        if (Modules is not null)
            CortexProductModuleValidation.Validate(Modules, coordinates);
        return this;
    }

    public static CortexProductCatalogV1 ReadExact(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Deserialize(bytes).Validate();
    }

    /// <summary>
    /// Reads an already-installed catalog only while planning an upgrade. The
    /// first unified catalog did not declare the application-authorization
    /// state schema. Its exact omission is accepted here so setup can replace
    /// it with the current strict catalog; candidate packages and runtime
    /// selectors continue to require every current schema declaration.
    /// </summary>
    public static CortexProductCatalogV1 ReadInstalledUpgradeSource(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var value = Deserialize(bytes);
        if (value.StateSchemas.ApplicationAuthorization >= 1)
            return value.ValidateCore(
                allowLegacyApplicationAuthorizationOmission: false,
                allowLegacyVersionedRolePaths: true);
        if (value.StateSchemas.ApplicationAuthorization != 0 ||
            !HasLegacyApplicationAuthorizationOmission(bytes))
        {
            throw new InvalidDataException("The unified Cortex product catalog identity is invalid.");
        }
        return value.ValidateCore(
            allowLegacyApplicationAuthorizationOmission: true,
            allowLegacyVersionedRolePaths: true);
    }

    private static CortexProductCatalogV1 Deserialize(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize(bytes, ProductCatalogJsonContext.Default.CortexProductCatalogV1) ??
        throw new InvalidDataException("The unified Cortex product catalog is malformed.");

    private static bool HasLegacyApplicationAuthorizationOmission(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return false;
        var schemaContainers = document.RootElement.EnumerateObject()
            .Where(property => property.NameEquals("stateSchemas"))
            .ToArray();
        if (schemaContainers.Length != 1 ||
            schemaContainers[0].Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        return !schemaContainers[0].Value.EnumerateObject()
            .Any(property => property.NameEquals("applicationAuthorization"));
    }

    private static bool IsSafeImmutableRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value.Contains(':') ||
            value.Contains('\\') ||
            value.StartsWith('/') ||
            value.EndsWith('/') ||
            value.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            return false;
        }
        return !value.StartsWith("state/", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, "state", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAdmittedRolePath(
        CortexProductRoleV1 role,
        bool allowLegacyVersionedRolePaths)
    {
        if (StableRolePaths.TryGetValue(role.Role, out var stable) &&
            string.Equals(role.RelativePath, stable, StringComparison.Ordinal))
        {
            return true;
        }
        if (!allowLegacyVersionedRolePaths)
            return false;

        var legacy = role.Role switch
        {
            "mcp" => $"versions/semantics/{role.ComponentVersion}/PegasusCortex.exe",
            "host" => $"versions/execution/{role.ComponentVersion}/host/PegasusCortex.Host.exe",
            "service" => $"versions/execution/{role.ComponentVersion}/service/PegasusCortex.ExecutionService.exe",
            "pao" => "bin/pao.exe",
            "process-host" => $"versions/execution/{role.ComponentVersion}/host/Pegasus.AgentOps.ProcessHost.exe",
            "ui-host" => $"versions/execution/{role.ComponentVersion}/ui/Pegasus.AgentOps.UiHost.exe",
            "launcher" => "PegasusCortex.exe",
            "setup" => "installer/PegasusCortex.Setup.exe",
            _ => string.Empty
        };
        return string.Equals(role.RelativePath, legacy, StringComparison.Ordinal);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CortexProductCatalogV1))]
[JsonSerializable(typeof(CortexProductModuleV1))]
internal sealed partial class ProductCatalogJsonContext : JsonSerializerContext;
