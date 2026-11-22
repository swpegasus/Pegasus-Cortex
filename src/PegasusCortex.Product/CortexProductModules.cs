using System.Text.RegularExpressions;

namespace Pegasus.Cortex.Product;

/// <summary>
/// Describes one selected module in an active Cortex product composition.
/// Component executable versions remain role-specific in the product catalog;
/// this version identifies the independently replaceable module package.
/// </summary>
public sealed record CortexProductModuleV1(
    string ModuleId,
    string Kind,
    string ModuleVersion,
    bool Required,
    string StatePolicy,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Provides,
    IReadOnlyList<string> Requires);

internal static class CortexProductModuleValidation
{
    private const int MaximumModules = 32;
    private const int MaximumRolesPerModule = 8;
    private const int MaximumContractsPerModule = 16;

    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "core", "runtime", "storage", "knowledge", "guidance", "integration", "tooling"
    };

    private static readonly HashSet<string> StatePolicies = new(StringComparer.Ordinal)
    {
        "none", "preserve"
    };

    private static readonly Regex ModuleIdPattern = new(
        "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex ContractPattern = new(
        "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*/[1-9][0-9]*(?:\\.[0-9]+){0,2}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static void Validate(
        IReadOnlyList<CortexProductModuleV1> modules,
        IReadOnlyCollection<string> catalogRoles)
    {
        if (modules.Count is < 1 or > MaximumModules)
            throw InvalidInventory();

        var moduleIds = new HashSet<string>(StringComparer.Ordinal);
        var ownedRoles = new HashSet<string>(StringComparer.Ordinal);
        var providedContracts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var module in modules)
        {
            if (!IsModuleId(module.ModuleId) ||
                !moduleIds.Add(module.ModuleId) ||
                !Kinds.Contains(module.Kind) ||
                !CortexProductIdentity.IsReleaseVersion(module.ModuleVersion) ||
                !StatePolicies.Contains(module.StatePolicy) ||
                module.Roles is null ||
                module.Provides is null ||
                module.Requires is null ||
                module.Roles.Count > MaximumRolesPerModule ||
                module.Provides.Count > MaximumContractsPerModule ||
                module.Requires.Count > MaximumContractsPerModule ||
                module.Roles.Count > 0 && !module.Required)
            {
                throw InvalidInventory();
            }

            var moduleRoles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var role in module.Roles)
            {
                if (!catalogRoles.Contains(role) ||
                    !moduleRoles.Add(role) ||
                    !ownedRoles.Add(role))
                {
                    throw InvalidInventory();
                }
            }

            var moduleProvides = new HashSet<string>(StringComparer.Ordinal);
            foreach (var provided in module.Provides)
            {
                if (!IsContract(provided) ||
                    !moduleProvides.Add(provided) ||
                    !providedContracts.TryAdd(provided, module.ModuleId))
                {
                    throw InvalidInventory();
                }
            }

            var moduleRequires = new HashSet<string>(StringComparer.Ordinal);
            foreach (var required in module.Requires)
            {
                if (!IsContract(required) ||
                    !moduleRequires.Add(required) ||
                    moduleProvides.Contains(required))
                {
                    throw InvalidInventory();
                }
            }
        }

        if (!ownedRoles.SetEquals(catalogRoles))
            throw InvalidInventory();

        foreach (var module in modules)
        {
            foreach (var required in module.Requires)
            {
                if (!providedContracts.ContainsKey(required))
                {
                    throw new CortexProductContractMismatchException(
                        module.ModuleId,
                        "module requirement",
                        required,
                        "missing");
                }
            }
        }
    }

    private static bool IsModuleId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        ModuleIdPattern.IsMatch(value);

    private static bool IsContract(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 96 &&
        ContractPattern.IsMatch(value);

    private static InvalidDataException InvalidInventory() =>
        new("The unified Cortex product module inventory is invalid.");
}
