using System.Text.Json;
using Pegasus.Cortex.Contracts.Serialization;

namespace Pegasus.Cortex.LearningExports.Serialization;

/// <summary>
/// Backward-compatible learning-export facade over the shared Cortex canonical JSON contract.
/// </summary>
public static class LearningExportJson
{
    public static JsonSerializerOptions CreateSerializerOptions() =>
        CortexCanonicalJson.CreateSerializerOptions();

    public static byte[] Serialize<T>(T value) =>
        CortexCanonicalJson.Serialize(value);

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json) =>
        CortexCanonicalJson.Deserialize<T>(utf8Json);

    public static ValueTask<T> DeserializeAsync<T>(
        Stream input,
        CancellationToken cancellationToken) =>
        CortexCanonicalJson.DeserializeAsync<T>(input, cancellationToken);

    public static string Sha256(ReadOnlySpan<byte> bytes) =>
        CortexCanonicalJson.Sha256(bytes);

    public static string Sha256(string value) =>
        CortexCanonicalJson.Sha256(value);
}
