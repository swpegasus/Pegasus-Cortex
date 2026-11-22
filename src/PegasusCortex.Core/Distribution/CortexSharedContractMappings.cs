using Pegasus.Cortex.Core.Outcomes;

namespace Pegasus.Cortex.Core.Distribution;

/// <summary>
/// Source-visible interface mappings for future host/distribution work. These
/// descriptors are documentation and compatibility metadata; they do not load,
/// invoke, install, or grant authority to an adapter.
/// </summary>
public static class CortexSharedContractMappings
{
    public const string DeploymentSchemaName = "deployment-manifest.v1.schema.json";

    public const bool RunnableHostExists = false;

    public static IReadOnlyList<CortexInterfaceMapping> Interfaces { get; } =
    [
        new(
            "pegasus.agent-outcome",
            "1.0",
            typeof(IOutcomeRepository).FullName!,
            "Pegasus.Cortex.Core",
            "src/PegasusCortex.Core/Outcomes/OutcomeIngestion.cs",
            "immutable outcome persistence boundary"),
        new(
            "pegasus.cortex.knowledge-revision",
            "1.0",
            "Pegasus.Cortex.DefaultLibrary.Contracts.IKnowledgeRevisionRepository",
            "Pegasus.Cortex.DefaultLibrary",
            "src/PegasusCortex.DefaultLibrary/Contracts/KnowledgeContracts.cs",
            "atomic knowledge import and supersession boundary"),
        new(
            "pegasus.cortex.knowledge-read",
            "1.0",
            "Pegasus.Cortex.DefaultLibrary.Contracts.IKnowledgeReadRepository",
            "Pegasus.Cortex.DefaultLibrary",
            "src/PegasusCortex.DefaultLibrary/Contracts/KnowledgeContracts.cs",
            "authorization-filtered knowledge read boundary"),
        new(
            "pegasus.cortex.knowledge-stream",
            "1.0",
            "Pegasus.Cortex.ResourceEfficiency.Contracts.IResourceBoundKnowledgeReadRepository",
            "Pegasus.Cortex.ResourceEfficiency",
            "src/PegasusCortex.ResourceEfficiency/Contracts/ResourceEfficiencyContracts.cs",
            "bounded provider-side authorization and streaming boundary"),
        new(
            "pegasus.cortex.retention-page",
            "1.0",
            "Pegasus.Cortex.ResourceEfficiency.Contracts.IResourceRetentionRepository",
            "Pegasus.Cortex.ResourceEfficiency",
            "src/PegasusCortex.ResourceEfficiency/Contracts/ResourceEfficiencyContracts.cs",
            "bounded incremental retention boundary")
    ];
}

public sealed record CortexInterfaceMapping(
    string ContractId,
    string ContractVersion,
    string InterfaceType,
    string PackageId,
    string ReferenceSourcePath,
    string Responsibility);
