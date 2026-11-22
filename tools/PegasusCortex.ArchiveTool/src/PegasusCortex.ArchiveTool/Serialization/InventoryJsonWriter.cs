using System.Text.Encodings.Web;
using System.Text.Json;
using Pegasus.Cortex.ArchiveTool.Archive;

namespace Pegasus.Cortex.ArchiveTool.Serialization;

internal static class InventoryJsonWriter
{
    internal const string SchemaVersion = "pegasus.cortex.archive-inventory/1";

    internal static void WriteCreateNew(
        FileStream stream,
        ArchivePreflight preflight,
        IReadOnlyList<MaterializedEntryRecord> entries)
    {
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.Default,
                       Indented = false,
                       SkipValidation = false
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", SchemaVersion);
            writer.WriteString("archiveSha256", preflight.ArchiveSha256);
            writer.WriteNumber("compressedBytes", preflight.CompressedBytes);
            writer.WriteNumber("tarStreamBytes", preflight.TarStreamBytes);
            writer.WriteNumber("totalFileBytes", preflight.TotalFileBytes);
            writer.WriteNumber("archiveEntryCount", preflight.ArchiveEntries.Count);
            writer.WriteNumber("materializedEntryCount", entries.Count);
            writer.WritePropertyName("entries");
            writer.WriteStartArray();
            foreach (var entry in entries.OrderBy(candidate => candidate.RelativePath, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("path", entry.RelativePath);
                writer.WriteString(
                    "kind",
                    entry.Kind == MaterializedEntryKind.Directory ? "directory" : "file");
                writer.WriteNumber("sizeBytes", entry.Length);
                if (entry.Sha256 is null)
                    writer.WriteNull("sha256");
                else
                    writer.WriteString("sha256", entry.Sha256);
                writer.WriteString("mode", FormatMode(entry.Mode));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    private static string FormatMode(UnixFileMode mode) =>
        Convert.ToString((int)mode, 8).PadLeft(4, '0');
}
