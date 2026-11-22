using Pegasus.Cortex.Core.Query;

namespace Pegasus.Cortex.Persistence.PostgreSql.Retention;

/// <summary>
/// Server-derived capability for one PostgreSQL passive-retention adapter.
/// Write includes the bounded reads required to validate append continuity.
/// </summary>
public enum PostgreSqlCortexRetentionCapability
{
    Read,
    Write
}

/// <summary>
/// Exact tenant and namespace authority captured before repository composition.
/// Request content cannot widen this scope.
/// </summary>
public sealed record PostgreSqlCortexRetentionScope(
    string TenantId,
    string Namespace,
    string AuthorityId,
    string SubjectId,
    string ServiceId,
    long PolicyRevision,
    PostgreSqlCortexRetentionCapability Capability)
{
    public PostgreSqlCortexRetentionScope Validate()
    {
        RequireIdentifier(TenantId, 128, nameof(TenantId));
        RequireIdentifier(Namespace, 256, nameof(Namespace));
        RequireIdentifier(AuthorityId, 128, nameof(AuthorityId));
        RequireIdentifier(SubjectId, 128, nameof(SubjectId));
        RequireIdentifier(ServiceId, 128, nameof(ServiceId));
        if (PolicyRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(PolicyRevision));
        if (!Enum.IsDefined(Capability))
            throw new ArgumentOutOfRangeException(nameof(Capability));
        return this with { };
    }

    public static PostgreSqlCortexRetentionScope FromProjectBrief(
        CortexProjectBriefReadScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new PostgreSqlCortexRetentionScope(
            scope.TenantId,
            scope.ProjectNamespace,
            scope.AuthorityId,
            scope.SubjectId,
            scope.ServiceId,
            scope.PolicyRevision,
            PostgreSqlCortexRetentionCapability.Read).Validate();
    }

    private static void RequireIdentifier(
        string value,
        int maximumCharacters,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumCharacters ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or ':' or '-')))
        {
            throw new ArgumentException(
                "A bounded portable PostgreSQL retention scope identifier is required.",
                parameterName);
        }
    }
}
