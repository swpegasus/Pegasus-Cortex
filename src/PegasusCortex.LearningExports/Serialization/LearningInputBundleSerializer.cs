using System.Text.Encodings.Web;
using System.Text.Json;
using Pegasus.Cortex.LearningExports.Contracts;
using Pegasus.Cortex.LearningExports.IO;

namespace Pegasus.Cortex.LearningExports.Serialization;

/// <summary>
/// Streams canonical input-bundle JSON through the incremental hash boundary.
/// Each record is canonicalized independently so bundle size does not create a
/// second full-bundle byte allocation.
/// </summary>
internal static class LearningInputBundleSerializer
{
    public static string ComputeHash(
        LearningInputBundleV1 bundle,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        cancellationToken.ThrowIfCancellationRequested();

        using var hashing = new BoundedHashWriteStream(
            Stream.Null,
            maximumBytes,
            "input-bundle",
            leaveOpen: true);
        using (var writer = new Utf8JsonWriter(hashing, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
            SkipValidation = false
        }))
        {
            writer.WriteStartObject();
            WriteValue(writer, "bundleHash", bundle.BundleHash);
            WriteValue(writer, "bundleId", bundle.BundleId);
            WriteValue(writer, "contentTreatment", bundle.ContentTreatment);
            WriteValue(writer, "contractId", bundle.ContractId);
            WriteValue(writer, "executable", bundle.Executable);
            WriteValue(writer, "generatedAt", bundle.GeneratedAt);
            WriteRecords(writer, bundle.Records, cancellationToken);
            WriteValue(writer, "schemaVersion", bundle.SchemaVersion);
            writer.WriteEndObject();

            cancellationToken.ThrowIfCancellationRequested();
            writer.Flush();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return hashing.CompleteHash();
    }

    private static void WriteRecords(
        Utf8JsonWriter writer,
        IReadOnlyList<LearningRecordRevisionV1> records,
        CancellationToken cancellationToken)
    {
        writer.WritePropertyName("records");
        writer.WriteStartArray();
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteRawValue(
                LearningExportJson.Serialize(record),
                skipInputValidation: false);
        }
        writer.WriteEndArray();
    }

    private static void WriteValue<T>(
        Utf8JsonWriter writer,
        string propertyName,
        T value)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteRawValue(
            LearningExportJson.Serialize(value),
            skipInputValidation: false);
    }
}
