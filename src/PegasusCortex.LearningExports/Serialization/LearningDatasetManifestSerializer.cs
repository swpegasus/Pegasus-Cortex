using System.Text.Encodings.Web;
using System.Text.Json;
using Pegasus.Cortex.LearningExports.Contracts;
using Pegasus.Cortex.LearningExports.IO;

namespace Pegasus.Cortex.LearningExports.Serialization;

internal sealed record CanonicalManifestWriteResult(string Sha256, long Bytes);

/// <summary>
/// Streams the canonical manifest so record count does not become a second full-size allocation.
/// </summary>
internal static class LearningDatasetManifestSerializer
{
    public static CanonicalManifestWriteResult ComputeHash(
        LearningDatasetManifestV1 manifest,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var bounded = new BoundedHashWriteStream(
            Stream.Null,
            maximumBytes,
            "manifest",
            leaveOpen: true);
        WriteCanonical(bounded, manifest, cancellationToken);
        return new CanonicalManifestWriteResult(bounded.CompleteHash(), bounded.BytesWritten);
    }

    public static CanonicalManifestWriteResult WriteCanonicalAndHash(
        Stream destination,
        LearningDatasetManifestV1 manifest,
        long maximumBytes,
        bool durableFlush,
        CancellationToken cancellationToken)
    {
        using var bounded = new BoundedHashWriteStream(
            destination,
            maximumBytes,
            "manifest",
            leaveOpen: true);
        WriteCanonical(bounded, manifest, cancellationToken);
        if (durableFlush)
            bounded.FlushToDisk();
        else
            bounded.Flush();
        return new CanonicalManifestWriteResult(bounded.CompleteHash(), bounded.BytesWritten);
    }

    private static void WriteCanonical(
        Stream destination,
        LearningDatasetManifestV1 manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
            SkipValidation = false
        });

        writer.WriteStartObject();
        WriteArray(writer, "artifacts", manifest.Artifacts, cancellationToken);
        WriteValue(writer, "buildId", manifest.BuildId);
        WriteValue(writer, "canonicalization", manifest.Canonicalization);
        WriteValue(writer, "contentTreatment", manifest.ContentTreatment);
        WriteValue(writer, "contractId", manifest.ContractId);
        WriteValue(writer, "createdAt", manifest.CreatedAt);
        WriteValue(writer, "datasetId", manifest.DatasetId);
        WriteValue(writer, "definitionHash", manifest.DefinitionHash);
        WriteValue(writer, "definitionRevisionId", manifest.DefinitionRevisionId);
        WriteValue(writer, "executable", manifest.Executable);
        WriteValue(writer, "exportApproval", manifest.ExportApproval);
        WriteArray(writer, "intendedUses", manifest.IntendedUses, cancellationToken);
        WriteArray(writer, "items", manifest.Items, cancellationToken);
        WriteValue(writer, "manifestHash", manifest.ManifestHash);
        WriteValue(writer, "modelTrainingPerformed", manifest.ModelTrainingPerformed);
        WriteValue(writer, "schemaVersion", manifest.SchemaVersion);
        WriteValue(writer, "sourceBundleHash", manifest.SourceBundleHash);
        WriteValue(writer, "sourceBundleId", manifest.SourceBundleId);
        WriteValue(writer, "splitPolicy", manifest.SplitPolicy);
        WriteArray(writer, "splits", manifest.Splits, cancellationToken);
        WriteArray(writer, "transformations", manifest.Transformations, cancellationToken);
        writer.WriteEndObject();
        cancellationToken.ThrowIfCancellationRequested();
        writer.Flush();
    }

    private static void WriteArray<T>(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<T> values,
        CancellationToken cancellationToken)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteRawValue(LearningExportJson.Serialize(value), skipInputValidation: false);
        }
        writer.WriteEndArray();
    }

    private static void WriteValue<T>(Utf8JsonWriter writer, string propertyName, T value)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteRawValue(LearningExportJson.Serialize(value), skipInputValidation: false);
    }
}
