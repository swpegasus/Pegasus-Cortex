using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Recovery;
using Pegasus.Cortex.Mcp.Local.Runtime;

internal static class LocalProjectTransferImportTests
{
    public static async Task CreateOnlyRoundTripAndFailClosedAsync()
    {
        await using var context = await LocalTestContext.CreateAsync(
            includeSplitAllowlists: false);
        var registration = context.Registrations.Single();
        var cortexRoot = Path.Combine(registration.RootPath, ".cortex");
        Directory.CreateDirectory(cortexRoot);
        var authorityPath = Path.Combine(cortexRoot, "STATE.md");
        var authorityBytes = "project-local authority remains the source of truth\n"u8.ToArray();
        await File.WriteAllBytesAsync(authorityPath, authorityBytes).ConfigureAwait(false);
        var authorityTimestamp = File.GetLastWriteTimeUtc(authorityPath);

        var first = context.TaskSnapshot(
            projectLinkId: registration.ProjectLinkId,
            revision: 1,
            stateSuffix: "-transfer-one");
        var second = context.TaskSnapshot(
            projectLinkId: registration.ProjectLinkId,
            revision: 2,
            previousHash: first.SnapshotHash,
            stateSuffix: "-transfer-two");
        var bundle = await CreateBundleAsync(
            context,
            registration,
            first,
            second).ConfigureAwait(false);
        var bundleRoot = Path.Combine(context.Root, "sealed-transfer");
        Directory.CreateDirectory(bundleRoot);
        CortexLocalRegistryAclPolicy.ProtectDirectory(bundleRoot);
        await CortexLocalRecoveryExportPathPolicy.WriteNewProtectedAsync(
            bundleRoot,
            "bundle.json",
            CortexCanonicalJson.Serialize(bundle),
            CancellationToken.None).ConfigureAwait(false);
        var bundlePath = Path.Combine(bundleRoot, "bundle.json");

        const string targetProfile = "transfer-import-target";
        var targetRoot = Path.Combine(context.Root, targetProfile);
        var importArguments = ImportArguments(
            targetProfile,
            targetRoot,
            bundlePath,
            registration);
        var options = CortexLocalProjectTransferImportArguments.Parse(importArguments);
        var process = await LocalProcessHarness.RunAsync(importArguments, [])
            .ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            process.ExitCode,
            $"The transfer import command failed: {process.StandardError}");
        var imported = JsonNode.Parse(process.StandardOutput)?.AsObject() ??
            throw new InvalidDataException("The transfer import result is not JSON.");

        LocalAssert.True(
            imported["accepted"]!.GetValue<bool>(),
            "The valid sealed transfer was rejected.");
        LocalAssert.Equal(
            CortexRetentionTransferImportDispositions.Imported,
            imported["disposition"]!.GetValue<string>(),
            "The new target did not report an imported disposition.");
        LocalAssert.Equal(
            bundle.AuthoritativeDocuments.Count,
            imported["appendedCount"]!.GetValue<int>(),
            "The import did not append every authoritative revision.");
        LocalAssert.Equal(
            bundle.DerivedBriefEvidence.Count,
            imported["derivedBriefEvidenceCount"]!.GetValue<int>(),
            "The import result lost comparison-evidence accounting.");
        LocalAssert.True(
            Directory.Exists(targetRoot) &&
            File.Exists(Path.Combine(targetRoot, "cortex.db")) &&
            File.Exists(Path.Combine(targetRoot, "projects.json")),
            "The verified staging profile was not atomically promoted.");
        LocalAssert.True(
            File.ReadAllBytes(authorityPath).AsSpan().SequenceEqual(authorityBytes) &&
            File.GetLastWriteTimeUtc(authorityPath) == authorityTimestamp,
            "Transfer import changed project-local .cortex authority.");
        AssertNoStagingDirectories(context.Root);

        await using (var target = await CortexMcpLocalComposition.CreateAsync(
                         targetProfile,
                         Path.Combine(targetRoot, "cortex.db"),
                         Path.Combine(targetRoot, "projects.json"),
                         "operator-test",
                         TimeProvider.System,
                         CancellationToken.None).ConfigureAwait(false))
        {
            foreach (var expected in bundle.AuthoritativeDocuments)
            {
                var actual = await target.Store.FindRevisionAsync(
                    expected.RepositoryProjectId,
                    expected.Kind,
                    expected.DocumentId,
                    expected.Revision,
                    CancellationToken.None).ConfigureAwait(false);
                LocalAssert.True(
                    actual is not null && Exact(expected, actual),
                    "The promoted profile changed a retained revision or hash.");
            }
            var currentBrief = await target.ProjectBriefStore.FindCurrentAsync(
                BriefScope(registration),
                CancellationToken.None).ConfigureAwait(false);
            LocalAssert.True(
                currentBrief is not null &&
                currentBrief.SourceHash == second.SnapshotHash &&
                currentBrief.AuthoritySetHash == bundle.Project.AuthoritySetHash &&
                currentBrief.ContextSetId ==
                    imported["projectBriefContextSetId"]!.GetValue<string>(),
                "The promoted profile did not regenerate the brief from the imported head.");
        }

        var beforeReplay = TreeFingerprint(targetRoot);
        await ThrowsAsync<IOException>(
            () => CortexLocalProjectTransferImportCommand.ImportAsync(
                    options,
                    CancellationToken.None)
                .AsTask(),
            "An exact replay replaced an existing profile.").ConfigureAwait(false);
        LocalAssert.Equal(
            beforeReplay,
            TreeFingerprint(targetRoot),
            "A rejected exact replay changed the existing profile.");

        const string mismatchProfile = "transfer-import-mismatch";
        var mismatchRoot = Path.Combine(context.Root, mismatchProfile);
        var mismatchRegistration = registration with { TenantId = "tenant-mismatch" };
        var mismatch = await CortexLocalProjectTransferImportCommand.ImportAsync(
            CortexLocalProjectTransferImportArguments.Parse(
                ImportArguments(
                    mismatchProfile,
                    mismatchRoot,
                    bundlePath,
                    mismatchRegistration)),
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.False(mismatch.Accepted, "A mismatched authorized scope was accepted.");
        LocalAssert.True(
            string.Equals(
                "transfer_authorized_scope_mismatch",
                mismatch.DiagnosticCode,
                StringComparison.Ordinal),
            "The mismatch did not return the stable pre-state diagnostic.");
        LocalAssert.False(
            Directory.Exists(mismatchRoot) || File.Exists(mismatchRoot),
            "A scope mismatch created local profile state.");
        AssertNoStagingDirectories(context.Root);

        const string corruptProfile = "transfer-import-corrupt";
        var corruptRoot = Path.Combine(context.Root, corruptProfile);
        var corruptBundleRoot = Path.Combine(context.Root, "corrupt-transfer");
        Directory.CreateDirectory(corruptBundleRoot);
        CortexLocalRegistryAclPolicy.ProtectDirectory(corruptBundleRoot);
        var corruptBytes = CortexCanonicalJson.Serialize(bundle);
        corruptBytes[^2] ^= 1;
        await CortexLocalRecoveryExportPathPolicy.WriteNewProtectedAsync(
            corruptBundleRoot,
            "bundle.json",
            corruptBytes,
            CancellationToken.None).ConfigureAwait(false);
        await ThrowsAsync<CortexRetentionTransferValidationException>(
            () => CortexLocalProjectTransferImportCommand.ImportAsync(
                    CortexLocalProjectTransferImportArguments.Parse(
                        ImportArguments(
                            corruptProfile,
                            corruptRoot,
                            Path.Combine(corruptBundleRoot, "bundle.json"),
                            registration)),
                    CancellationToken.None)
                .AsTask(),
            "A corrupt transfer bundle passed the canonical seal boundary.")
            .ConfigureAwait(false);
        LocalAssert.False(
            Directory.Exists(corruptRoot) || File.Exists(corruptRoot),
            "A corrupt bundle created local profile state.");
        AssertNoStagingDirectories(context.Root);

        AssertThrows<ArgumentException>(
            () => CortexLocalProjectTransferImportArguments.Parse(
                ImportArguments(
                    "transfer-import-read-only",
                    Path.Combine(context.Root, "transfer-import-read-only"),
                    bundlePath,
                    registration,
                    allowWrite: false)),
            "Transfer import accepted grants that cannot regenerate and verify a brief.");
    }

    private static async ValueTask<CortexRetentionTransferBundleV1> CreateBundleAsync(
        LocalTestContext context,
        CortexLocalProjectRegistration registration,
        Pegasus.Cortex.Contracts.WorkPackets.AgentTaskSnapshotV1 first,
        Pegasus.Cortex.Contracts.WorkPackets.AgentTaskSnapshotV1 second)
    {
        var authoritative = new List<CortexRetainedDocument>();
        var derived = new List<CortexRetainedDocument>();
        await using var source = await CortexMcpLocalComposition.CreateAsync(
            context.ProfileId,
            context.DatabasePath,
            context.RegistryPath,
            "operator-test",
            TimeProvider.System,
            CancellationToken.None).ConfigureAwait(false);
        var firstWrite = await source.Runtime.SubmitTaskSnapshotAsync(
            registration.ProjectLinkId,
            registration.ProjectNamespace,
            first,
            CancellationToken.None).ConfigureAwait(false);
        var secondWrite = await source.Runtime.SubmitTaskSnapshotAsync(
            registration.ProjectLinkId,
            registration.ProjectNamespace,
            second,
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.True(
            firstWrite?.ProjectionIssueCode is null &&
            secondWrite?.ProjectionIssueCode is null,
            "The source fixture did not materialize both task revisions.");

        await foreach (var document in source.Store.ReadRevisionRangeAsync(
                           registration.ProjectId,
                           CortexRetainedDocumentKinds.TaskSnapshot,
                           first.TaskSetId,
                           1,
                           2,
                           CancellationToken.None).ConfigureAwait(false))
        {
            authoritative.Add(document.Snapshot());
        }
        var briefHead = await source.Store.FindHeadAsync(
            registration.ProjectLinkId,
            CortexRetainedDocumentKinds.ProjectBrief,
            registration.ProjectNamespace,
            CancellationToken.None).ConfigureAwait(false) ??
            throw new InvalidDataException("The source fixture has no derived brief head.");
        await foreach (var document in source.Store.ReadRevisionRangeAsync(
                           registration.ProjectLinkId,
                           CortexRetainedDocumentKinds.ProjectBrief,
                           registration.ProjectNamespace,
                           1,
                           briefHead.Revision,
                           CancellationToken.None).ConfigureAwait(false))
        {
            derived.Add(document.Snapshot());
        }

        return CortexRetentionTransferBundleFactory.Create(
            "transfer-local-operator-test",
            second.CapturedAt.AddSeconds(1),
            new CortexRetentionTransferProjectScope(
                registration.TenantId,
                registration.ProjectLinkId,
                registration.ProjectId,
                registration.ProjectNamespace,
                CortexProjectBriefService.ComputeAuthoritySetHash(
                    second.SourceRevisions)),
            new CortexRetentionTransferProvenance(
                "sqlite-local-test",
                SourceStoreEpoch: 7,
                "pegasus-cortex-local-import-test",
                [new string('e', 64), new string('f', 64)]),
            authoritative,
            derived);
    }

    private static string[] ImportArguments(
        string profile,
        string stateRoot,
        string bundlePath,
        CortexLocalProjectRegistration registration,
        bool allowWrite = true) =>
    [
        "import-project-transfer",
        "--profile", profile,
        "--destination-state-root", stateRoot,
        "--bundle-path", bundlePath,
        "--tenant-id", registration.TenantId,
        "--project-link-id", registration.ProjectLinkId,
        "--project-id", registration.ProjectId,
        "--project-namespace", registration.ProjectNamespace,
        "--project-root", registration.RootPath,
        "--allow-read", "true",
        "--allow-write", allowWrite ? "true" : "false"
    ];

    private static CortexProjectBriefReadScope BriefScope(
        CortexLocalProjectRegistration registration) => new(
            registration.TenantId,
            registration.ProjectLinkId,
            registration.ProjectId,
            registration.ProjectNamespace,
            "local-os-policy",
            $"os_{CortexLocalOsIdentity.Capture().Hash}",
            "pegasus-cortex-local",
            PolicyRevision: 1);

    private static bool Exact(
        CortexRetentionTransferDocument expected,
        CortexRetainedDocument actual) =>
        expected.RepositoryProjectId == actual.ProjectId &&
        expected.Kind == actual.Kind &&
        expected.DocumentId == actual.DocumentId &&
        expected.Revision == actual.Revision &&
        expected.PreviousHash == actual.PreviousHash &&
        expected.IdempotencyKey == actual.IdempotencyKey &&
        expected.ProducerId == actual.ProducerId &&
        expected.ContractId == actual.ContractId &&
        expected.CanonicalUtf8.AsSpan().SequenceEqual(actual.CanonicalUtf8) &&
        expected.ContentHash == actual.ContentHash &&
        expected.SearchText == actual.SearchText &&
        expected.CapturedAt == actual.CapturedAt &&
        expected.ExpiresAt == actual.ExpiresAt;

    private static string TreeFingerprint(string root)
    {
        var records = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => string.Join(
                '|',
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                new FileInfo(path).Length,
                File.GetLastWriteTimeUtc(path).Ticks,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
            .ToArray();
        return Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', records))));
    }

    private static void AssertNoStagingDirectories(string root)
    {
        LocalAssert.False(
            Directory.EnumerateDirectories(root)
                .Any(path => Path.GetFileName(path)
                    .StartsWith(".cortex-import-", StringComparison.Ordinal)),
            "Transfer import left a staging profile behind.");
    }

    private static void AssertThrows<T>(Action action, string message)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string message)
        where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
