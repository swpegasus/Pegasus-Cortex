using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Persistence.File.V2;

internal static class PersonalProfileOfflineTests
{
    private const string TaskId = "personal-offline-fixture-task";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-13T12:00:00Z");

    public static async Task EmptyPersonalHotPreservesBoundStateAsync()
    {
        foreach (var personalNamespace in new[] { "knowledge.user", "user.preferences" })
        {
            var name = "cortex-hot-" + Guid.NewGuid().ToString("N");
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), name));
            Directory.CreateDirectory(root);
            try
            {
                var manifest = new CortexFileProjectManifestV2(
                    CortexFileProjectManifestV2.CurrentSchema, "personal-fixture",
                    personalNamespace, [personalNamespace], HotMaximumUtf8Bytes: 4096);
                await using var store = await CortexOfflineFileStore.InitializeAsync(root, TaskId, manifest);
                var capture = await store.CaptureAsync(new CortexOfflineKnowledgeCreatePayloadV1(
                    CortexOfflineSchemas.KnowledgeCreateV1, personalNamespace, "pending-personal",
                    CortexKnowledgeKindV2.Preference, CortexKnowledgeTierV2.Explicit,
                    "Pending personal fixture", "Uncommitted personal fixture body.", [],
                    CortexProvenanceSourceV2.User, "test-fixture", "Deterministic file fixture.", Now));
                var previousItem = new CortexContextItemV2(personalNamespace, "knowledge", "previous-personal", 1,
                    "Previous personal context", "Prior personal preview.", CortexKnowledgeKindV2.Preference,
                    CortexKnowledgeTierV2.Explicit, null, 1);
                await store.MergeHotAsync(new("loaded-context", [personalNamespace], [previousItem]));

                var hotPath = Path.Combine(store.ScopeRoot, "HOT.json");
                var framePath = Path.Combine(store.ScopeRoot, "frames", capture.Frame.FrameId + ".json");
                var before = SnapshotBoundFiles(root, hotPath);
                using (var previous = JsonDocument.Parse(await File.ReadAllBytesAsync(hotPath)))
                {
                    LocalAssert.Equal(2, previous.RootElement.GetProperty("items").GetArrayLength(),
                        "Fixture did not contain both previous and pending personal context.");
                }

                CortexOfflineHotRefreshResult result;
                // An empty available set must not hydrate pending frames, even when one is locked.
                await using (var lockedFrame = new FileStream(framePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    result = await store.MergeHotAsync(new("unloaded-context", [], [], false));

                var hotBytes = await File.ReadAllBytesAsync(hotPath);
                using var hot = JsonDocument.Parse(hotBytes);
                var value = hot.RootElement;
                LocalAssert.True(hotBytes.Length <= 4096 && result.Utf8Bytes == hotBytes.Length,
                    "Empty personal HOT escaped its 4 KiB budget.");
                LocalAssert.True(value.GetProperty("generated").GetBoolean() &&
                    !value.GetProperty("authoritative").GetBoolean(), "Empty HOT lost its generated fallback markers.");
                LocalAssert.Equal(TaskId, value.GetProperty("taskId").GetString() ??
                    throw new InvalidDataException("HOT taskId is null."), "HOT crossed its exact task binding.");
                LocalAssert.Equal("personal-fixture", value.GetProperty("projectId").GetString() ??
                    throw new InvalidDataException("HOT projectId is null."), "HOT crossed its project binding.");
                LocalAssert.Equal("unloaded-context", value.GetProperty("contextCursor").GetString() ??
                    throw new InvalidDataException("HOT contextCursor is null."), "HOT retained its old cursor.");
                LocalAssert.Equal(0, value.GetProperty("effectiveNamespaces").GetArrayLength(), "HOT overstated available namespaces.");
                LocalAssert.Equal(0, value.GetProperty("items").GetArrayLength(), "Old or pending personal context survived unload.");
                LocalAssert.True(!value.GetProperty("hasMore").GetBoolean() && result.ItemCount == 0 && !result.HasMore,
                    "Empty personal HOT falsely advertised remaining context.");
                RequireSameBoundFiles(before, SnapshotBoundFiles(root, hotPath));

                await RequireRejectedAsync(() => store.MergeHotAsync(
                    new("invalid-empty", [], [previousItem], false)).AsTask());
                await RequireRejectedAsync(() => store.MergeHotAsync(
                    new("invalid-more", [], [], true)).AsTask());
                await RequireRejectedAsync(() => store.MergeHotAsync(
                    new("invalid-item", [personalNamespace], [previousItem with { Namespace = "project.outside" }], false)).AsTask());
                var afterRejectedBytes = await File.ReadAllBytesAsync(hotPath);
                LocalAssert.True(hotBytes.SequenceEqual(afterRejectedBytes),
                    "Rejected HOT refresh changed the valid empty projection.");
                RequireSameBoundFiles(before, SnapshotBoundFiles(root, hotPath));
                LocalAssert.Equal(personalNamespace, store.Manifest.WriteNamespace, "Refresh changed the writable namespace.");
                LocalAssert.True(store.Manifest.ReadNamespaces.SequenceEqual([personalNamespace]),
                    "Refresh weakened the retained read namespace binding.");
            }
            finally
            {
                var directory = new DirectoryInfo(root);
                if (directory.Exists && directory.LinkTarget is null &&
                    (directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
                    directory.Name == name && directory.Parent?.FullName == Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
                {
                    RequireNoReparseChildren(directory);
                    directory.Delete(recursive: true);
                }
            }
        }
    }

    private static Dictionary<string, byte[]> SnapshotBoundFiles(string root, string hotPath) =>
        Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, hotPath, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void RequireSameBoundFiles(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        LocalAssert.Equal(before.Count, after.Count, "Refresh added or removed manifest/frame files.");
        foreach (var file in before)
            LocalAssert.True(after.TryGetValue(file.Key, out var bytes) && file.Value.SequenceEqual(bytes),
                "Refresh changed a retained manifest or pending frame.");
    }

    private static async Task RequireRejectedAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("Invalid HOT namespace/item metadata was accepted.");
    }

    private static void RequireNoReparseChildren(DirectoryInfo directory)
    {
        foreach (var child in directory.EnumerateFileSystemInfos())
        {
            if (child.LinkTarget is not null || (child.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing cleanup of a reparse point in the exact test fixture.");
            if (child is DirectoryInfo childDirectory)
                RequireNoReparseChildren(childDirectory);
        }
    }
}
