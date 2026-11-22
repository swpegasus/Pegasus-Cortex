using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Core.Query;

namespace Pegasus.Cortex.Mcp.Knowledge;

/// <summary>
/// Loads the portable file-continuity guide from an embedded, hash-bound source.
/// The guide is intentionally independent from any project's live .cortex files:
/// project files remain authoritative and the MCP host performs no filesystem I/O.
/// </summary>
internal static class CortexContinuityGuide
{
    public const string Version = "2.3.0";
    public const string ObservedAt = "2026-10-22";
    public const string ReviewAfter = "2027-01-20";
    public const string ExpectedContentSha256 =
        "0e1d4d7eeda59b14ab68e9c34827cfdcaa8ecf5c97b381a46960cca2bdf1b10d";
    private const int MaximumGuideUtf8Bytes = 16 * 1024;
    private const string EmbeddedResourceName =
        "Pegasus.Cortex.Mcp.Knowledge.continuity-guide.md";

    private static readonly Lazy<GuidePayload> Payload = new(
        LoadAndValidate,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Verify() => _ = Payload.Value;

    public static JsonObject BuildDocument()
    {
        var payload = Payload.Value;
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["guideVersion"] = Version,
            ["title"] = "Pegasus Cortex project continuity guide",
            ["content"] = payload.Content,
            ["source"] = new JsonObject
            {
                ["kind"] = "embedded-versioned-markdown",
                ["sha256"] = payload.Sha256,
                ["utf8Bytes"] = payload.Utf8Bytes
            },
            ["freshness"] = new JsonObject
            {
                ["state"] = "current",
                ["observedAt"] = ObservedAt,
                ["reviewAfter"] = ReviewAfter,
                ["basis"] =
                    "Explicit namespace-local operational tasks and knowledge, expected-revision concurrency, immediate local search, current-user provenance, separate optional collective publication, and no implicit execution or external authority."
            },
            ["authority"] = new JsonObject
            {
                ["projectFilesRemainAuthoritative"] = true,
                ["grantsOperationalAuthority"] = false,
                ["projectContinuityRuntime"] = false
            },
            ["contentTreatment"] = CortexQueryPolicy.ContentTreatment,
            ["executable"] = false,
            ["safetyNotice"] = CortexQueryPolicy.SafetyNotice
        };
    }

    private static GuidePayload LoadAndValidate()
    {
        using var stream = typeof(CortexContinuityGuide).Assembly
            .GetManifestResourceStream(EmbeddedResourceName) ??
            throw new InvalidOperationException(
                "The embedded Cortex continuity guide is unavailable.");
        if (stream.Length <= 0 || stream.Length > MaximumGuideUtf8Bytes)
        {
            throw new InvalidOperationException(
                "The embedded Cortex continuity guide violates its byte boundary.");
        }

        using var memory = new MemoryStream((int)stream.Length);
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        if (bytes.Length >= 3 &&
            bytes[0] == 0xef &&
            bytes[1] == 0xbb &&
            bytes[2] == 0xbf)
            throw new InvalidOperationException("The embedded Cortex continuity guide contains a UTF-8 BOM.");

        string content;
        try
        {
            content = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException(
                "The embedded Cortex continuity guide is not strict UTF-8.",
                exception);
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(sha256, ExpectedContentSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The embedded Cortex continuity guide does not match its accepted SHA-256.");
        }

        return new GuidePayload(content, sha256, bytes.Length);
    }

    private sealed record GuidePayload(string Content, string Sha256, int Utf8Bytes);
}
