namespace Pegasus.Cortex.Mcp.Knowledge;

/// <summary>
/// Discriminates project-link rejection from a failure in an advertised
/// provider. A valid zero-result page remains a successful result.
/// </summary>
public sealed record CortexProjectSearchResult(
    string Disposition,
    Pegasus.Cortex.Core.Query.CortexKnowledgeSearchPage? Page)
{
    public const string Success = "success";
    public const string LinkRejected = "link-rejected";
    public const string ProviderFailure = "provider-failure";

    public bool IsValid => Disposition switch
    {
        Success => Page is not null,
        LinkRejected or ProviderFailure => Page is null,
        _ => false
    };
}
