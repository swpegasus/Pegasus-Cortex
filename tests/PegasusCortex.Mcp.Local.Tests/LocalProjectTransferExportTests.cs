using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Local.Configuration;

internal static class LocalProjectTransferExportTests
{
    public static async Task CorrelatedStoreProvenanceAndFailuresAsync()
    {
        await using var context = await LocalTestContext.CreateAsync(
            includeSplitAllowlists: false);
        var registration = context.Registrations.Single();
        var capturedAt = DateTimeOffset.UtcNow;
        const string StoreId = "sqlite-neural-canary";
        const long StoreEpoch = 42;
        LocalCurrentMaterializationTests.WriteProject(
            registration.RootPath,
            currentRevision: 12,
            capturedAt.AddMinutes(-1),
            "transfer-export",
            StoreId,
            StoreEpoch,
            @"checkpoint=D:\Codex Projects\Neural Commander\checkpoint.zip; archive=<D:\Cortex Evidence\snapshot.json>; note=`D:\Temp\receipt.txt`; unix=/opt/pegasus/state/head.json; SHA-256 0123456789abcdef.");

        var destination = Path.Combine(context.Root, "transfer-output");
        Directory.CreateDirectory(destination);
        CortexLocalRegistryAclPolicy.ProtectDirectory(destination);
        const string ValidRunId = "correlated-store-valid";
        var valid = await LocalProcessHarness.RunAsync(
            ExportArguments(context, destination, ValidRunId, capturedAt),
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            valid.ExitCode,
            $"The correlated transfer export failed: {valid.StandardError}");
        var result = JsonNode.Parse(valid.StandardOutput)?.AsObject() ??
            throw new InvalidDataException("The transfer export result is not JSON.");
        LocalAssert.True(
            result["accepted"]!.GetValue<bool>(),
            "The correlated transfer export was not accepted.");
        var outputRoot = Path.Combine(
            destination,
            result["outputRelativePath"]!.GetValue<string>());
        var bundlePath = Path.Combine(outputRoot, "bundle.json");
        var bundle = CortexRetentionTransferBundlePolicy.ReadCanonical(
            await File.ReadAllBytesAsync(bundlePath).ConfigureAwait(false));
        LocalAssert.Equal(
            StoreId,
            bundle.Provenance.SourceProviderId,
            "Transfer provenance did not use the correlated project store id.");
        LocalAssert.Equal(
            StoreEpoch,
            bundle.Provenance.SourceStoreEpoch,
            "Transfer provenance did not use the correlated project store epoch.");
        LocalAssert.Equal(
            registration.ProjectLinkId,
            bundle.Project.ProjectLinkId,
            "Transfer provenance was not bound to the registered project link.");
        var latestSnapshotDocument = bundle.AuthoritativeDocuments
            .Where(item => item.Kind == CortexRetainedDocumentKinds.TaskSnapshot)
            .OrderByDescending(item => item.Revision)
            .First();
        var latestSnapshot = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(
            latestSnapshotDocument.CanonicalUtf8);
        LocalAssert.False(
            latestSnapshot.Tasks.Single().CurrentState.Contains(
                @"D:\Codex Projects",
                StringComparison.Ordinal),
            "The provider-neutral snapshot retained an absolute machine path.");
        LocalAssert.False(
            latestSnapshot.Tasks.Single().CurrentState.Contains(
                @"D:\Cortex Evidence",
                StringComparison.Ordinal) ||
            latestSnapshot.Tasks.Single().CurrentState.Contains(
                @"D:\Temp",
                StringComparison.Ordinal) ||
            latestSnapshot.Tasks.Single().CurrentState.Contains(
                "/opt/pegasus",
                StringComparison.Ordinal),
            "The provider-neutral snapshot retained a delimited absolute machine path.");
        LocalAssert.True(
            latestSnapshot.Tasks.Single().CurrentState.Contains(
                bundle.Project.AuthoritySetHash,
                StringComparison.Ordinal),
            "The portable checkpoint did not retain its authority-set pointer.");
        LocalAssert.True(
            latestSnapshot.Tasks.Single().CurrentState.Contains(
                "SHA-256 0123456789abcdef.",
                StringComparison.Ordinal),
            "The portable checkpoint discarded useful evidence after the local path.");

        var failures = new (string Name, Action WriteMetadata)[]
        {
            (
                "missing-store",
                () => WriteMetadata(
                    registration.RootPath,
                    registration.ProjectLinkId,
                    storeId: null,
                    storeEpoch: null)),
            (
                "invalid-store",
                () => WriteMetadata(
                    registration.RootPath,
                    registration.ProjectLinkId,
                    "invalid store id",
                    storeEpoch: 0)),
            (
                "mismatched-link",
                () => WriteMetadata(
                    registration.RootPath,
                    $"plink_{new string('9', 64)}",
                    StoreId,
                    StoreEpoch))
        };
        foreach (var failure in failures)
        {
            failure.WriteMetadata();
            var before = TreeFingerprint(destination);
            var rejected = await LocalProcessHarness.RunAsync(
                ExportArguments(
                    context,
                    destination,
                    $"correlated-store-{failure.Name}",
                    capturedAt),
                []).ConfigureAwait(false);
            LocalAssert.Equal(
                1,
                rejected.ExitCode,
                $"The {failure.Name} metadata did not fail the transfer export.");
            LocalAssert.True(
                string.IsNullOrWhiteSpace(rejected.StandardOutput),
                $"The {failure.Name} metadata emitted a transfer result.");
            LocalAssert.Equal(
                before,
                TreeFingerprint(destination),
                $"The {failure.Name} metadata created or changed transfer output.");
            LocalAssert.False(
                Directory.EnumerateDirectories(destination)
                    .Any(path => Path.GetFileName(path)
                        .StartsWith(".transfer-", StringComparison.Ordinal)),
                $"The {failure.Name} metadata left a transfer staging directory.");
        }
    }

    private static string[] ExportArguments(
        LocalTestContext context,
        string destination,
        string runId,
        DateTimeOffset capturedAt) =>
    [
        "export-project-transfer",
        "--profile", context.ProfileId,
        "--state-root", Path.GetDirectoryName(context.DatabasePath)!,
        "--project-link", LocalTestContext.PrimaryProjectLinkId,
        "--destination-root", destination,
        "--run-id", runId,
        "--captured-at-utc", capturedAt.ToString("O")
    ];

    private static void WriteMetadata(
        string projectRoot,
        string projectLinkId,
        string? storeId,
        long? storeEpoch)
    {
        var correlation = new JsonObject
        {
            ["projectId"] = LocalTestContext.PrimaryProjectId,
            ["projectLinkId"] = projectLinkId
        };
        if (storeId is not null)
            correlation["storeId"] = storeId;
        if (storeEpoch is not null)
            correlation["storeEpoch"] = storeEpoch.Value;
        var metadata = new JsonObject
        {
            ["project"] = new JsonObject { ["name"] = "Primary Local Test" },
            ["cortexCorrelation"] = correlation
        };
        File.WriteAllText(
            Path.Combine(projectRoot, ".cortex", "meta.json"),
            metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
            new UTF8Encoding(false));
    }

    private static string TreeFingerprint(string root)
    {
        var entries = Directory.EnumerateFileSystemEntries(
                root,
                "*",
                SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Directory.Exists(path)
                ? $"D|{Path.GetRelativePath(root, path).Replace('\\', '/')}"
                : string.Join(
                    '|',
                    "F",
                    Path.GetRelativePath(root, path).Replace('\\', '/'),
                    new FileInfo(path).Length,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
            .ToArray();
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries))));
    }
}
