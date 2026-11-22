using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Sync;
using Pegasus.Cortex.Mcp.Protocol;

namespace Pegasus.Cortex.Mcp.Integrated.Security;

[Flags]
internal enum CortexLocalCapability
{
    None = 0,
    Read = 1 << 0,
    Correlate = 1 << 1,
    Propose = 1 << 2,
    Review = 1 << 3,
    Audit = 1 << 4,
    DelegateReview = 1 << 5,
    RetainedRead = 1 << 6,
    RetainedWrite = 1 << 7,
    ProjectBrief = 1 << 8,
    Sync = 1 << 9
}

internal sealed class CortexLocalAuthority
{
    private readonly CortexLocalPrincipalPolicy _principal;

    private CortexLocalAuthority(
        string tenantId,
        string endpointId,
        long policyRevision,
        string principalName,
        string subjectId,
        CortexLocalPrincipalPolicy principal)
    {
        TenantId = tenantId;
        EndpointId = endpointId;
        PolicyRevision = policyRevision;
        PrincipalName = principalName;
        SubjectId = subjectId;
        _principal = principal;
    }

    public string TenantId { get; }
    public string EndpointId { get; }
    public long PolicyRevision { get; }
    public string PrincipalName { get; }
    public string SubjectId { get; }
    public CortexLocalCapability Capabilities => _principal.Capabilities;

    public static CortexLocalAuthority Load(string path)
    {
        var json = ProtectedLocalFile.ReadUtf8(path, 64 * 1024);
        var options = new JsonSerializerOptions(McpProtocol.JsonOptions)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        var configuration = JsonSerializer.Deserialize<CortexLocalAuthorityConfiguration>(json, options)
            ?? throw new InvalidOperationException("The Cortex local-authority file is empty.");
        if (configuration.SchemaVersion is not (1 or 2 or 3 or 4) ||
            !ValidIdentifier(configuration.TenantId) ||
            !ValidIdentifier(configuration.EndpointId) ||
            configuration.PolicyRevision < 1 ||
            configuration.Principals is null ||
            configuration.Principals.Count is < 1 or > 16)
        {
            throw new InvalidOperationException("The Cortex local-authority configuration is invalid.");
        }

        var currentPrincipal = Environment.UserName;
        var matches = configuration.Principals
            .Where(item => string.Equals(item.Name, currentPrincipal, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1 || !matches[0].HasAnyCapability())
            throw new UnauthorizedAccessException("The operating-system principal is not granted Cortex access.");
        if (configuration.Principals.Any(item =>
                !ValidIdentifier(item.Name) ||
                configuration.SchemaVersion >= 2 && !ValidIdentifier(item.SubjectId) ||
                item.SubjectId is not null && !ValidIdentifier(item.SubjectId) ||
                item.AutoPublishSafeCollectiveProposals && (!item.Propose || !item.Read) ||
                item.ProjectBrief && !item.RetainedRead ||
                configuration.Principals.Count(candidate =>
                    string.Equals(candidate.Name, item.Name, StringComparison.Ordinal)) != 1))
        {
            throw new InvalidOperationException("The Cortex local-authority principal map is invalid.");
        }

        return new CortexLocalAuthority(
            configuration.TenantId,
            configuration.EndpointId,
            configuration.PolicyRevision,
            currentPrincipal,
            matches[0].SubjectId ?? currentPrincipal,
            matches[0]);
    }

    public bool HasCapability(CortexLocalCapability capability) =>
        (_principal.Capabilities & capability) == capability;

    public bool AutoPublishSafeCollectiveProposals =>
        _principal.AutoPublishSafeCollectiveProposals;

    public CortexProjectLinkAccess ProjectLinkAccess(CortexProjectLinkCapability capability)
    {
        if (capability == CortexProjectLinkCapability.Read && !_principal.CanResolveProjectLinks ||
            capability == CortexProjectLinkCapability.Correlate && !_principal.Correlate)
        {
            throw new UnauthorizedAccessException("The local principal lacks project-correlation access.");
        }
        return new CortexProjectLinkAccess(
            TenantId,
            CortexProjectCorrelationContract.ControlNamespace,
            "local-cortex-policy",
            SubjectId,
            "pegasus-cortex-mcp",
            PolicyRevision,
            capability);
    }

    public CortexQueryAuthority QueryAuthority(string namespaceName)
    {
        if (!_principal.Read)
            throw new UnauthorizedAccessException("The local principal lacks Cortex read access.");
        return new CortexQueryAuthority(
            TenantId,
            "local-cortex-policy",
            SubjectId,
            "pegasus-cortex-mcp",
            PolicyRevision,
            [new CortexQueryNamespaceGrant(
                namespaceName,
                "internal",
                [
                    CortexQueryCapability.Discover,
                    CortexQueryCapability.Status,
                    CortexQueryCapability.Catalog,
                    CortexQueryCapability.Search,
                    CortexQueryCapability.Read
                ])]);
    }

    public LearningFeedbackAuthorityContext LearningAuthority(
        string namespaceName,
        LearningFeedbackNamespaceCapability capability,
        string? screeningDecisionId = null,
        string? screeningDecisionHash = null)
    {
        if (capability == LearningFeedbackNamespaceCapability.Read && !_principal.Read ||
            capability == LearningFeedbackNamespaceCapability.Submit && !_principal.Propose ||
            capability == LearningFeedbackNamespaceCapability.Review && !_principal.Review ||
            capability == LearningFeedbackNamespaceCapability.Audit && !_principal.Audit ||
            capability == LearningFeedbackNamespaceCapability.DelegateReview && !_principal.DelegateReview)
        {
            throw new UnauthorizedAccessException("The local principal lacks the requested learning-feedback capability.");
        }
        if (capability == LearningFeedbackNamespaceCapability.Submit &&
            (!ValidIdentifier(screeningDecisionId) || !ValidSha256(screeningDecisionHash)))
        {
            throw new InvalidOperationException("A trusted local screening decision is required for proposal submission.");
        }

        var decisionHashes = screeningDecisionId is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [screeningDecisionId] = screeningDecisionHash!
            };
        return new LearningFeedbackAuthorityContext(
            TenantId,
            "local-cortex-policy",
            SubjectId,
            "pegasus-cortex-mcp",
            PolicyRevision,
            [new LearningFeedbackNamespaceGrant(
                namespaceName,
                CanSubmit: capability == LearningFeedbackNamespaceCapability.Submit,
                CanReview: capability == LearningFeedbackNamespaceCapability.Review,
                CanRead: capability == LearningFeedbackNamespaceCapability.Read,
                CanAudit: capability == LearningFeedbackNamespaceCapability.Audit,
                CanDelegateReview: capability == LearningFeedbackNamespaceCapability.DelegateReview)],
            screeningDecisionId is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(["local-cortex-screening"], StringComparer.Ordinal),
            decisionHashes);
    }

    /// <summary>
    /// Produces the authenticated submit grant used to maintain a publication
    /// that was already accepted into the knowledge store. Proposal submission
    /// deliberately continues to require a trusted screening decision through
    /// <see cref="LearningAuthority"/>; this narrower helper does not bypass
    /// that ingress boundary or grant task authority.
    /// </summary>
    public LearningFeedbackAuthorityContext PublicationChangeAuthority(
        string namespaceName)
    {
        if (!_principal.Propose)
        {
            throw new UnauthorizedAccessException(
                "The local principal lacks authored-publication maintenance access.");
        }

        return new LearningFeedbackAuthorityContext(
            TenantId,
            "local-cortex-policy",
            SubjectId,
            "pegasus-cortex-mcp",
            PolicyRevision,
            [new LearningFeedbackNamespaceGrant(namespaceName, CanSubmit: true, CanReview: false)],
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Produces a server-derived synchronization authority for one exact target
    /// namespace. Bundle hints never widen this grant. The integrated runtime is
    /// still reachable only through the Nexus-owned gateway.
    /// </summary>
    public CortexSyncAuthorityContext SyncAuthority(
        string namespaceName,
        IEnumerable<string> allowedPayloadContractIds)
    {
        if (!_principal.Sync)
            throw new UnauthorizedAccessException("The local principal lacks Cortex synchronization access.");
        if (!ValidIdentifier(namespaceName))
            throw new ArgumentException("The synchronization namespace is invalid.", nameof(namespaceName));
        return new CortexSyncAuthorityContext(
            TenantId,
            "local-cortex-policy",
            SubjectId,
            "pegasus-cortex-sync",
            PolicyRevision,
            [new CortexSyncNamespaceGrant(namespaceName, CanSync: true)],
            allowedPayloadContractIds);
    }

    /// <summary>
    /// Produces a distinct server-policy reviewer only for an explicitly opted-in
    /// proposing principal. This is not the caller's review identity and is never
    /// exposed as a user-granted capability.
    /// </summary>
    public LearningFeedbackAuthorityContext ProvisionalReviewAuthority(string namespaceName)
    {
        if (!_principal.Propose || !_principal.AutoPublishSafeCollectiveProposals)
        {
            throw new UnauthorizedAccessException(
                "The local principal is not opted into provisional collective publication.");
        }

        return new LearningFeedbackAuthorityContext(
            TenantId,
            LearningFeedbackPublicationPolicy.ProvisionalAuthorityId,
            LearningFeedbackPublicationPolicy.ProvisionalReviewerSubjectId,
            "pegasus-cortex-mcp",
            PolicyRevision,
            [new LearningFeedbackNamespaceGrant(namespaceName, CanSubmit: false, CanReview: true)],
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static bool ValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool ValidSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed record CortexLocalAuthorityConfiguration(
    int SchemaVersion,
    string TenantId,
    string EndpointId,
    long PolicyRevision,
    IReadOnlyList<CortexLocalPrincipalPolicy> Principals);

internal sealed record CortexLocalPrincipalPolicy(
    string Name,
    bool Read,
    bool Correlate,
    bool Propose,
    bool Review,
    bool Audit = false,
    bool DelegateReview = false,
    string? SubjectId = null,
    bool AutoPublishSafeCollectiveProposals = false,
    bool RetainedRead = false,
    bool RetainedWrite = false,
    bool ProjectBrief = false,
    bool Sync = false)
{
    public CortexLocalCapability Capabilities =>
        (Read ? CortexLocalCapability.Read : CortexLocalCapability.None) |
        (Correlate ? CortexLocalCapability.Correlate : CortexLocalCapability.None) |
        (Propose ? CortexLocalCapability.Propose : CortexLocalCapability.None) |
        (Review ? CortexLocalCapability.Review : CortexLocalCapability.None) |
        (Audit ? CortexLocalCapability.Audit : CortexLocalCapability.None) |
        (DelegateReview ? CortexLocalCapability.DelegateReview : CortexLocalCapability.None) |
        (RetainedRead ? CortexLocalCapability.RetainedRead : CortexLocalCapability.None) |
        (RetainedWrite ? CortexLocalCapability.RetainedWrite : CortexLocalCapability.None) |
        (ProjectBrief ? CortexLocalCapability.ProjectBrief : CortexLocalCapability.None) |
        (Sync ? CortexLocalCapability.Sync : CortexLocalCapability.None);

    public bool CanResolveProjectLinks => Capabilities != CortexLocalCapability.None;

    public bool HasAnyCapability() => Capabilities != CortexLocalCapability.None;
}
