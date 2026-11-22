using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.Learning;
using System.Text.Json;

namespace Pegasus.Cortex.Core.Retention;

/// <summary>
/// Product-owned bounds for passive agent-authored documents. The repository
/// retains exact canonical bytes; it never interprets them as execution authority.
/// </summary>
public static class CortexAgentRetentionLimits
{
    public const int MaximumIdentifierCharacters = 128;
    public const int MaximumContractIdCharacters = 256;
    public const int MaximumCanonicalDocumentBytes = 32 * 1024;
    public const int MaximumSearchTextCharacters = 4096;
    public const int MaximumListPageSize = 32;
    public const int MaximumKindHeadPageSize = 100;
    // Matches the standalone store's hard configurable document ceiling. Range
    // readers stream this bound and never materialize the full set in memory.
    public const int MaximumRevisionRangeDocuments = 1_000_000;
}

public static class CortexRetainedDocumentKinds
{
    public const string TaskSnapshot = "task-snapshot";
    public const string WorkPacket = "work-packet";
    public const string WorkPacketHandoffReceipt =
        "work-packet-handoff-receipt";
    public const string WorkPacketAcceptanceReceipt =
        "work-packet-acceptance-receipt";
    public const string ProjectBrief = "project-brief";
    public const string LearningFeedbackProposal =
        "learning-feedback-proposal";
}

/// <summary>
/// Provider-neutral defense-in-depth for every retained document envelope.
/// Rejected text is never copied into diagnostics.
/// </summary>
public static class CortexRetentionContentPolicy
{
    public static void RequireSemanticAccepted(params string?[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Length > 2048) throw new CortexRetentionContentRejectedException(["screening_size"]);
        foreach (var field in fields)
        {
            if (field is null) continue;
            CheckSemanticText(field);
            if (field.AsSpan().TrimStart() is var trimmed && trimmed.Length > 0 && trimmed[0] is '{' or '[')
            {
                JsonDocument? parsed = null;
                try { parsed = JsonDocument.Parse(field, new JsonDocumentOptions { MaxDepth = 32 }); }
                catch (JsonException) { /* Ordinary notes and code need not be JSON. */ }
                using (parsed)
                    if (parsed is not null) RequireSemanticJsonAccepted(parsed.RootElement);
            }
        }
    }

    public static void RequireSemanticJsonAccepted(JsonElement value)
    {
        var remaining = 4096;
        Visit(value, 0);
        void Visit(JsonElement item, int depth)
        {
            if (--remaining < 0 || depth > 32) throw new CortexRetentionContentRejectedException(["screening_size"]);
            if (item.ValueKind == JsonValueKind.String) CheckSemanticText(item.GetString()!);
            else if (item.ValueKind == JsonValueKind.Array)
                foreach (var child in item.EnumerateArray()) Visit(child, depth + 1);
            else if (item.ValueKind == JsonValueKind.Object)
                foreach (var property in item.EnumerateObject())
                {
                    CheckSemanticText(property.Name);
                    if (property.Value.ValueKind == JsonValueKind.String)
                        CheckSemanticText(property.Name + "=" + JsonSerializer.Serialize(property.Value.GetString()));
                    Visit(property.Value, depth + 1);
                }
        }
    }

    private static void CheckSemanticText(string text)
    {
        var result = LearningFeedbackContentScreening.ScreenSemanticText(text);
        if (!result.Accepted) throw new CortexRetentionContentRejectedException(result.FindingCodes);
    }

    public static void RequireAccepted(byte[] canonicalUtf8, string? additionalText = null)
    {
        ArgumentNullException.ThrowIfNull(canonicalUtf8);
        var result = LearningFeedbackContentScreening.Screen(
            "agent coordination retention",
            additionalText ?? "passive exact-byte record",
            Encoding.UTF8.GetString(canonicalUtf8),
            collective: false);
        if (!result.Accepted)
            throw new CortexRetentionContentRejectedException(result.FindingCodes);
    }
}

public static class CortexRetentionWriteDispositions
{
    public const string Appended = "appended";
    public const string Idempotent = "idempotent";
    public const string Conflict = "conflict";
    public const string StaleBase = "stale-base";
}

/// <summary>
/// One validated document append. Callers must provide canonical JSON and its
/// exact hash; storage verifies both before opening a write transaction.
/// </summary>
public sealed record CortexRetentionAppendRequest(
    string ProjectId,
    string Kind,
    string DocumentId,
    long Revision,
    string? ExpectedPreviousHash,
    string IdempotencyKey,
    string ProducerId,
    string ContractId,
    byte[] CanonicalUtf8,
    string ContentHash,
    string SearchText,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt)
{
    public CortexRetentionAppendRequest Validate()
    {
        RequireIdentifier(ProjectId, nameof(ProjectId));
        RequireIdentifier(Kind, nameof(Kind));
        RequireIdentifier(DocumentId, nameof(DocumentId));
        RequireIdentifier(IdempotencyKey, nameof(IdempotencyKey));
        RequireIdentifier(ProducerId, nameof(ProducerId));
        if (string.IsNullOrWhiteSpace(ContractId) ||
            ContractId.Length > CortexAgentRetentionLimits.MaximumContractIdCharacters ||
            ContractId.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded contract identifier is required.", nameof(ContractId));
        }
        if (Revision < 1)
            throw new ArgumentOutOfRangeException(nameof(Revision));
        if (Revision == 1 && ExpectedPreviousHash is not null ||
            Revision > 1 && !IsHash(ExpectedPreviousHash))
        {
            throw new ArgumentException(
                "The expected previous hash must be absent only for revision one.",
                nameof(ExpectedPreviousHash));
        }
        if (CanonicalUtf8 is null ||
            CanonicalUtf8.Length is < 2 or > CortexAgentRetentionLimits.MaximumCanonicalDocumentBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(CanonicalUtf8));
        }
        if (!IsHash(ContentHash) ||
            !string.Equals(
                ContentHash,
                CortexCanonicalJson.Sha256(CanonicalUtf8),
                StringComparison.Ordinal))
        {
            throw new ArgumentException("The canonical document hash is invalid.", nameof(ContentHash));
        }
        RequireCanonicalContractJson(CanonicalUtf8);
        if (SearchText is null ||
            SearchText.Length > CortexAgentRetentionLimits.MaximumSearchTextCharacters ||
            SearchText.Any(character => char.IsControl(character) &&
                character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException("The search projection is not bounded text.", nameof(SearchText));
        }
        CortexRetentionContentPolicy.RequireAccepted(CanonicalUtf8, SearchText);
        RequireUtc(CapturedAt, nameof(CapturedAt));
        RequireUtc(ExpiresAt, nameof(ExpiresAt));
        if (ExpiresAt <= CapturedAt)
            throw new ArgumentException("The retained document expiry must follow capture time.", nameof(ExpiresAt));
        return this with { CanonicalUtf8 = CanonicalUtf8.ToArray() };
    }

    private static void RequireIdentifier(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > CortexAgentRetentionLimits.MaximumIdentifierCharacters ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Skip(1).Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or ':' or '-')))
        {
            throw new ArgumentException("A bounded portable identifier is required.", parameterName);
        }
    }

    private static bool IsHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("A UTC timestamp is required.", parameterName);
    }

    private static void RequireCanonicalContractJson(byte[] canonicalUtf8)
    {
        try
        {
            using var document = JsonDocument.Parse(canonicalUtf8, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !ContainsOnlyContractJson(document.RootElement) ||
                !CortexCanonicalJson.Serialize(document.RootElement)
                    .AsSpan().SequenceEqual(canonicalUtf8))
            {
                throw new ArgumentException(
                    "The retained document must use exact canonical contract JSON.",
                    nameof(CanonicalUtf8));
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "The retained document is not valid canonical JSON.",
                nameof(CanonicalUtf8),
                exception);
        }
    }

    private static bool ContainsOnlyContractJson(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                return value.EnumerateObject().All(property =>
                    names.Add(property.Name) &&
                    ContainsOnlyContractJson(property.Value));
            case JsonValueKind.Array:
                return value.EnumerateArray().All(ContainsOnlyContractJson);
            case JsonValueKind.Number:
                return value.TryGetInt64(out _) && IsCanonicalInteger(value.GetRawText());
            case JsonValueKind.String:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            default:
                return false;
        }
    }

    private static bool IsCanonicalInteger(string raw)
    {
        var index = raw.Length > 0 && raw[0] == '-' ? 1 : 0;
        if (index == raw.Length)
            return false;
        if (raw[index] == '0')
            return index == 0 && raw.Length == 1;
        if (raw[index] is < '1' or > '9')
            return false;
        for (index++; index < raw.Length; index++)
        {
            if (raw[index] is < '0' or > '9')
                return false;
        }
        return true;
    }
}

public sealed record CortexRetainedDocument(
    string ProjectId,
    string Kind,
    string DocumentId,
    long Revision,
    string? PreviousHash,
    string IdempotencyKey,
    string ProducerId,
    string ContractId,
    byte[] CanonicalUtf8,
    string ContentHash,
    string SearchText,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt)
{
    public CortexRetainedDocument Snapshot() => this with { CanonicalUtf8 = CanonicalUtf8.ToArray() };
}

public sealed record CortexRetentionAppendResult(
    string Disposition,
    CortexRetainedDocument? Document,
    string? CurrentHeadHash);

public sealed record CortexRetentionSearchHit(
    string ProjectId,
    string Kind,
    string DocumentId,
    long Revision,
    string ContentHash,
    string SearchText,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Bounded exact head page for a single project and passive document kind.
/// This is not full-text search and does not make excluded kinds searchable.
/// </summary>
public sealed record CortexRetentionHeadPage(
    IReadOnlyList<CortexRetainedDocument> Items,
    string? NextAfterDocumentId);

/// <summary>
/// Passive append/read port. Implementations reject concurrent divergent heads
/// instead of selecting a winner.
/// </summary>
public interface ICortexAgentRetentionRepository
{
    ValueTask<CortexRetentionAppendResult> AppendAsync(
        CortexRetentionAppendRequest request,
        CancellationToken cancellationToken);

    ValueTask<CortexRetainedDocument?> FindHeadAsync(
        string projectId,
        string kind,
        string documentId,
        CancellationToken cancellationToken);

    ValueTask<CortexRetainedDocument?> FindRevisionAsync(
        string projectId,
        string kind,
        string documentId,
        long revision,
        CancellationToken cancellationToken);

    ValueTask<CortexRetentionHeadPage> ListHeadsByKindAsync(
        string projectId,
        string kind,
        string? afterDocumentId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists retained heads regardless of expiry for integrity and identity
    /// decisions. Hot proposal/search reads use the expiry-filtered method.
    /// </summary>
    ValueTask<CortexRetentionHeadPage> ListAllHeadsByKindAsync(
        string projectId,
        string kind,
        string? afterDocumentId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streams one ordered, bounded revision range. Missing revisions are not
    /// synthesized; consumers remain responsible for exact adjacency checks.
    /// </summary>
    IAsyncEnumerable<CortexRetainedDocument> ReadRevisionRangeAsync(
        string projectId,
        string kind,
        string documentId,
        long firstRevision,
        long lastRevision,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<CortexRetentionSearchHit>> SearchHeadsAsync(
        string projectId,
        string text,
        int limit,
        CancellationToken cancellationToken);

    async ValueTask<IReadOnlyList<CortexRetentionSearchHit>> SearchHeadsByKindAsync(
        string projectId,
        string kind,
        string text,
        int limit,
        CancellationToken cancellationToken)
    {
        var values = await SearchHeadsAsync(
            projectId,
            text,
            CortexAgentRetentionLimits.MaximumListPageSize,
            cancellationToken).ConfigureAwait(false);
        return values
            .Where(value => string.Equals(value.Kind, kind, StringComparison.Ordinal))
            .Take(limit)
            .ToArray();
    }
}

public sealed record CortexLocalStoreIntegrity(
    bool Healthy,
    string Result,
    long RetainedDocuments,
    long AuditEvents);

public interface ICortexLocalStoreMaintenance
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    ValueTask<CortexLocalStoreIntegrity> CheckIntegrityAsync(CancellationToken cancellationToken);

    ValueTask RebuildSearchIndexAsync(CancellationToken cancellationToken);

    ValueTask BackupAsync(string destinationPath, CancellationToken cancellationToken);
}

public sealed class CortexRetentionQuotaExceededException(string message) :
    InvalidOperationException(message);

/// <summary>
/// Indicates that a well-formed typed assertion does not extend its exact
/// retained typed predecessor. No repository write is attempted.
/// </summary>
public sealed class CortexRetentionRevisionRejectedException() :
    ArgumentException("The typed revision does not extend its exact retained predecessor.");

/// <summary>
/// Rejects prohibited retained content without returning the rejected bytes.
/// Finding codes are deterministic, bounded, and safe to expose to a producer.
/// </summary>
public sealed class CortexRetentionContentRejectedException : ArgumentException
{
    public CortexRetentionContentRejectedException(IEnumerable<string> findingCodes)
        : base("Retained content failed passive-data screening.")
    {
        ArgumentNullException.ThrowIfNull(findingCodes);
        var values = findingCodes
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (values.Length is < 1 or > 16 ||
            values.Any(value =>
                value.Length is < 1 or > 64 ||
                !char.IsAsciiLetter(value[0]) ||
                value.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character != '_')))
        {
            throw new ArgumentException(
                "One to sixteen bounded screening finding codes are required.",
                nameof(findingCodes));
        }
        FindingCodes = Array.AsReadOnly(values);
    }

    public IReadOnlyList<string> FindingCodes { get; }
}
