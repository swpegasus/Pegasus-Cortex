using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Contracts.Serialization;

internal static class CanonicalTimestampGoldenTests
{
    public static IReadOnlyList<(string Name, Action Run)> All { get; } =
    [
        ("C# reseals UTC Z timestamps as cross-language canonical +00:00 bytes", ResealsUtcTimestamp)
    ];

    private static void ResealsUtcTimestamp()
    {
        var path = Path.Combine(
            QueryContractSourceTests.FindProjectRoot(),
            "schemas",
            "cortex-canonical-timestamp-v1.vectors.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        Require(root.GetProperty("schemaVersion").GetInt32() == 1 &&
                root.GetProperty("contractId").GetString() ==
                    "pegasus.cortex.canonical-timestamp-vectors/1.0",
            "The canonical timestamp vector header is invalid.");

        var vectors = root.GetProperty("vectors").EnumerateArray().ToArray();
        Require(vectors.Length == 1,
            "The checked-in canonical timestamp vector inventory changed without test coverage.");
        var vector = vectors[0];
        Require(vector.GetProperty("id").GetString() == "utc-z-to-explicit-offset",
            "The canonical timestamp vector ID changed.");

        var inputJson = vector.GetProperty("inputJson").GetString()
            ?? throw new InvalidOperationException("The canonical timestamp input is missing.");
        Require(inputJson.Contains(".123456Z\"", StringComparison.Ordinal),
            "The cross-language test must begin with a Z-form UTC timestamp.");
        var value = CortexCanonicalJson.Deserialize<CanonicalTimestampVectorPayload>(
            Encoding.UTF8.GetBytes(inputJson));
        Require(value.CapturedAt.Offset == TimeSpan.Zero,
            "The Z-form input did not deserialize as UTC.");

        var actual = CortexCanonicalJson.Serialize(value);
        var expectedText = vector.GetProperty("canonicalJson").GetString()
            ?? throw new InvalidOperationException("The expected canonical JSON is missing.");
        var expectedBytes = Convert.FromBase64String(
            vector.GetProperty("canonicalUtf8Base64").GetString()
            ?? throw new InvalidOperationException("The expected canonical bytes are missing."));
        var expectedHash = vector.GetProperty("sha256").GetString()
            ?? throw new InvalidOperationException("The expected canonical SHA-256 is missing.");

        Require(actual.AsSpan().SequenceEqual(expectedBytes),
            "C# canonical timestamp bytes differ from the shared cross-language vector.");
        Require(Encoding.UTF8.GetString(actual) == expectedText,
            "C# canonical timestamp text differs from the shared cross-language vector.");
        Require(expectedText.Contains("+00:00", StringComparison.Ordinal) &&
                !expectedText.Contains(".123456Z\"", StringComparison.Ordinal),
            "The canonical timestamp must use the explicit +00:00 offset.");
        Require(CortexCanonicalJson.Sha256(actual) == expectedHash,
            "C# canonical timestamp SHA-256 differs from the shared vector.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record CanonicalTimestampVectorPayload(
        int SchemaVersion,
        string ContractId,
        long Revision,
        DateTimeOffset CapturedAt);
}
