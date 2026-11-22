using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Persistence.File.V2;

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "Usage: ReferenceSeeder <behavior-pack-root> <isolated-reference-instance-root>");
    return 2;
}

var packRoot = Path.GetFullPath(args[0]);
var instanceRoot = Path.GetFullPath(args[1]);
if (string.Equals(packRoot, instanceRoot, StringComparison.OrdinalIgnoreCase) ||
    instanceRoot.StartsWith(packRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase) is false)
{
    Console.Error.WriteLine(
        "The isolated reference instance must be a child of the selected behavior-pack root.");
    return 2;
}

Directory.CreateDirectory(instanceRoot);
var manifestPath = Path.Combine(instanceRoot, ".cortex", "project.json");
if (!System.IO.File.Exists(manifestPath))
{
    var manifest = new CortexFileProjectManifestV2(
        CortexFileProjectManifestV2.CurrentSchema,
        "file-v2-reference",
        "knowledge.shared",
        ["knowledge.shared"],
        4096,
        16,
        16 * 1024);
    await manifest.WriteAsync(instanceRoot).ConfigureAwait(false);
}

var store = await FileCortexV2Store.OpenAsync(instanceRoot).ConfigureAwait(false);
var result = await store.ImportBehaviorPackAsync(
    packRoot,
    "knowledge.shared",
    "reference-seeder").ConfigureAwait(false);
await store.RefreshHotAsync().ConfigureAwait(false);
var status = await store.StatusAsync(CancellationToken.None).ConfigureAwait(false);
var application = new CortexV2Application(store, "reference-seeder");
var pathSafety = await application.SearchAsync(new CortexSearchRequestV2(
    ["knowledge.shared"],
    "path safety links traversal source preservation",
    [CortexKnowledgeKindV2.Tenet],
    null,
    5), CancellationToken.None).ConfigureAwait(false);
var serialization = await application.SearchAsync(new CortexSearchRequestV2(
    ["knowledge.shared"],
    "serialization Unicode controls portable encoding",
    [CortexKnowledgeKindV2.Tenet],
    null,
    5), CancellationToken.None).ConfigureAwait(false);
var startup = await application.StartAsync(new CortexStartRequestV2(
    ["knowledge.shared"],
    "implement a safe portable persistence migration",
    null), CancellationToken.None).ConfigureAwait(false);
Console.WriteLine(JsonSerializer.Serialize(new
{
    result.PackId,
    result.Namespace,
    result.Imported,
    result.Unchanged,
    result.ContextCursor,
    status.HeadCount,
    status.RevisionCount,
    hotUtf8Bytes = new FileInfo(Path.Combine(instanceRoot, ".cortex", "HOT.json")).Length,
    pathSafety = pathSafety.Items.Select(value => value.RecordId).ToArray(),
    serialization = serialization.Items.Select(value => value.RecordId).ToArray(),
    startup = startup.Items.Take(5).Select(value => value.RecordId).ToArray()
}, CortexV2Json.Options));
return 0;
