using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Recovery;
using Pegasus.Cortex.Mcp.Local.Runtime;

internal static class LocalProjectTransferTenantRebindTests
{
    public static async Task ExactAuthoritativeCopyAndFailuresAsync()
    {
        await using var context = await LocalTestContext.CreateAsync(
            includeSplitAllowlists: false);
        var registration = context.Registrations.Single();
        var sourceCapturedAt = DateTimeOffset.UtcNow;
        LocalCurrentMaterializationTests.WriteProject(
            registration.RootPath,
            currentRevision: 12,
            sourceCapturedAt.AddMinutes(-1),
            "tenant-rebind",
            "sqlite-tenant-rebind-test",
            storeEpoch: 7,
            "Portable checkpoint evidence only.");

        var sourceRoot = Path.Combine(context.Root, "tenant-rebind-source");
        Directory.CreateDirectory(sourceRoot);
        CortexLocalRegistryAclPolicy.ProtectDirectory(sourceRoot);
        var sourceExport = await LocalProcessHarness.RunAsync(
            ExportArguments(context, sourceRoot, sourceCapturedAt),
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            sourceExport.ExitCode,
            $"The tenant-rebind source export failed: {sourceExport.StandardError}");
        var sourceResult = JsonNode.Parse(sourceExport.StandardOutput)?.AsObject() ??
            throw new InvalidDataException("The source export result is not JSON.");
        var sourceBundlePath = Path.Combine(
            sourceRoot,
            sourceResult["outputRelativePath"]!.GetValue<string>(),
            "bundle.json");
        var sourceBytes = await File.ReadAllBytesAsync(sourceBundlePath).ConfigureAwait(false);
        var sourceFileSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes))
            .ToLowerInvariant();
        var sourceBundle = CortexRetentionTransferBundlePolicy.ReadCanonical(sourceBytes);
        LocalAssert.True(
            sourceBundle.DerivedBriefEvidence.Count > 0,
            "The source fixture did not contain scope-bound brief evidence.");

        // Prove that ordinary import still rejects the original tenant scope.
        // Tenant rebinding is an explicit operator copy, never an implicit
        // relaxation of the default four-field import boundary.
        var targetRegistration = registration with { TenantId = "pegasus-local" };
        const string rejectedProfile = "tenant-rebind-unrebound-target";
        var rejectedRoot = Path.Combine(context.Root, rejectedProfile);
        var rejected = await CortexLocalProjectTransferImportCommand.ImportAsync(
            CortexLocalProjectTransferImportArguments.Parse(
                ImportArguments(
                    rejectedProfile,
                    rejectedRoot,
                    sourceBundlePath,
                    targetRegistration)),
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.False(
            rejected.Accepted,
            "An unrebound source bundle crossed the target tenant boundary.");
        LocalAssert.True(
            string.Equals(
                "transfer_authorized_scope_mismatch",
                rejected.DiagnosticCode,
                StringComparison.Ordinal),
            "The unrebound target rejection did not use the stable scope diagnostic.");
        LocalAssert.False(
            Directory.Exists(rejectedRoot) || File.Exists(rejectedRoot),
            "A rejected unrebound target import created profile state.");

        var destination = Path.Combine(context.Root, "tenant-rebind-output");
        Directory.CreateDirectory(destination);
        CortexLocalRegistryAclPolicy.ProtectDirectory(destination);
        var reboundCapturedAt = sourceCapturedAt.AddSeconds(1);
        var rebound = await LocalProcessHarness.RunAsync(
            RebindArguments(
                sourceBundlePath,
                sourceFileSha256,
                destination,
                "tenant-rebind-valid",
                reboundCapturedAt),
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            rebound.ExitCode,
            $"The tenant-rebind copy failed: {rebound.StandardError}");
        var result = JsonNode.Parse(rebound.StandardOutput)?.AsObject() ??
            throw new InvalidDataException("The tenant-rebind result is not JSON.");
        LocalAssert.True(
            result["accepted"]!.GetValue<bool>(),
            "The tenant-rebind copy was not accepted.");
        LocalAssert.Equal(
            sourceFileSha256,
            result["sourceBundleFileSha256"]!.GetValue<string>(),
            "The result did not retain the exact source file seal.");

        var reboundPath = Path.Combine(
            destination,
            result["outputRelativePath"]!.GetValue<string>(),
            "bundle.json");
        var reboundBundle = CortexRetentionTransferBundlePolicy.ReadCanonical(
            await File.ReadAllBytesAsync(reboundPath).ConfigureAwait(false));
        LocalAssert.Equal(
            "pegasus-local",
            reboundBundle.Project.TenantId,
            "Only the target tenant should change in the rebound scope.");
        LocalAssert.Equal(
            sourceBundle.Project.ProjectLinkId,
            reboundBundle.Project.ProjectLinkId,
            "Tenant rebinding changed the project link.");
        LocalAssert.Equal(
            sourceBundle.Project.ProjectId,
            reboundBundle.Project.ProjectId,
            "Tenant rebinding changed the project ID.");
        LocalAssert.Equal(
            sourceBundle.Project.ProjectNamespace,
            reboundBundle.Project.ProjectNamespace,
            "Tenant rebinding changed the namespace.");
        LocalAssert.Equal(
            sourceBundle.Project.AuthoritySetHash,
            reboundBundle.Project.AuthoritySetHash,
            "Tenant rebinding changed the authority-set hash.");
        LocalAssert.Equal(
            0,
            reboundBundle.DerivedBriefEvidence.Count,
            "Tenant-bound derived briefs were copied into the new scope.");
        LocalAssert.True(
            reboundBundle.Provenance.EvidenceHashes.Contains(
                sourceBundle.BundleHash,
                StringComparer.Ordinal),
            "The rebound provenance did not bind the complete source bundle.");
        LocalAssert.Equal(
            "pegasus-cortex-tenant-rebind",
            reboundBundle.Provenance.ExportToolId,
            "The rebound provenance did not identify the migration tool.");
        AssertAuthoritativeDocumentsExact(sourceBundle, reboundBundle);

        // Import the rebound copy into a new target-tenant profile and verify
        // that the brief is regenerated from the authoritative head. No
        // source-tenant derived brief may be installed as authority.
        const string targetProfile = "tenant-rebind-target";
        var targetRoot = Path.Combine(context.Root, targetProfile);
        var imported = await CortexLocalProjectTransferImportCommand.ImportAsync(
            CortexLocalProjectTransferImportArguments.Parse(
                ImportArguments(
                    targetProfile,
                    targetRoot,
                    reboundPath,
                    targetRegistration)),
            CancellationToken.None).ConfigureAwait(false);
        LocalAssert.True(imported.Accepted, "The tenant-rebound copy was not importable.");
        LocalAssert.Equal(
            0,
            imported.DerivedBriefEvidenceCount,
            "The target import reported source-tenant brief evidence.");
        LocalAssert.Equal(
            reboundBundle.AuthoritativeDocuments.Count,
            imported.AppendedCount,
            "The target import did not preserve every authoritative document.");
        await using (var target = await CortexMcpLocalComposition.CreateAsync(
                         targetProfile,
                         Path.Combine(targetRoot, "cortex.db"),
                         Path.Combine(targetRoot, "projects.json"),
                         "operator-test",
                         TimeProvider.System,
                         CancellationToken.None).ConfigureAwait(false))
        {
            var brief = await target.ProjectBriefStore.FindCurrentAsync(
                BriefScope(targetRegistration),
                CancellationToken.None).ConfigureAwait(false);
            LocalAssert.True(
                brief is not null &&
                brief.TenantId == targetRegistration.TenantId &&
                brief.ProjectLinkId == targetRegistration.ProjectLinkId &&
                brief.ProjectId == targetRegistration.ProjectId &&
                brief.ProjectNamespace == targetRegistration.ProjectNamespace &&
                brief.AuthoritySetHash == reboundBundle.Project.AuthoritySetHash &&
                brief.ContextSetId == imported.ProjectBriefContextSetId,
                "The target profile did not regenerate a correctly scoped project brief.");
        }
        var sourceBytesAfter = await File.ReadAllBytesAsync(sourceBundlePath)
            .ConfigureAwait(false);
        LocalAssert.True(
            sourceBytes.AsSpan().SequenceEqual(sourceBytesAfter),
            "The source bundle changed during copy-first tenant rebinding.");

        var beforeRejected = Directory.EnumerateFileSystemEntries(destination).Count();
        var wrongScope = await LocalProcessHarness.RunAsync(
            RebindArguments(
                sourceBundlePath,
                sourceFileSha256,
                destination,
                "tenant-rebind-wrong-scope",
                reboundCapturedAt,
                sourceTenantId: "wrong-tenant"),
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            3,
            wrongScope.ExitCode,
            "An unacknowledged source tenant did not fail closed.");
        LocalAssert.True(
            wrongScope.StandardError.Contains(
                "transfer_tenant_rebind_source_scope_mismatch",
                StringComparison.Ordinal),
            "The source-scope rejection did not emit its stable diagnostic.");
        LocalAssert.Equal(
            beforeRejected,
            Directory.EnumerateFileSystemEntries(destination).Count(),
            "A rejected source scope created output.");

        var wrongHash = await LocalProcessHarness.RunAsync(
            RebindArguments(
                sourceBundlePath,
                new string('f', 64),
                destination,
                "tenant-rebind-wrong-hash",
                reboundCapturedAt),
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            3,
            wrongHash.ExitCode,
            "A wrong source file seal did not fail closed.");
        LocalAssert.True(
            wrongHash.StandardError.Contains(
                "transfer_bundle_file_hash_mismatch",
                StringComparison.Ordinal),
            "The file-seal rejection did not emit its stable diagnostic.");
        LocalAssert.Equal(
            beforeRejected,
            Directory.EnumerateFileSystemEntries(destination).Count(),
            "A rejected source file seal created output.");

        var sameTenant = await LocalProcessHarness.RunAsync(
            RebindArguments(
                sourceBundlePath,
                sourceFileSha256,
                destination,
                "tenant-rebind-same-tenant",
                reboundCapturedAt,
                targetTenantId: LocalTestContext.TenantId),
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            2,
            sameTenant.ExitCode,
            "A no-op tenant rebind was accepted.");
        LocalAssert.Equal(
            beforeRejected,
            Directory.EnumerateFileSystemEntries(destination).Count(),
            "A rejected no-op tenant rebind created output.");
    }

    private static void AssertAuthoritativeDocumentsExact(
        CortexRetentionTransferBundleV1 source,
        CortexRetentionTransferBundleV1 rebound)
    {
        LocalAssert.Equal(
            source.AuthoritativeDocuments.Count,
            rebound.AuthoritativeDocuments.Count,
            "Tenant rebinding changed the authoritative document count.");
        for (var index = 0; index < source.AuthoritativeDocuments.Count; index++)
        {
            var expected = CortexCanonicalJson.Serialize(source.AuthoritativeDocuments[index]);
            var actual = CortexCanonicalJson.Serialize(rebound.AuthoritativeDocuments[index]);
            LocalAssert.True(
                expected.AsSpan().SequenceEqual(actual),
                $"Tenant rebinding changed authoritative document {index}.");
        }
    }

    private static string[] ExportArguments(
        LocalTestContext context,
        string destination,
        DateTimeOffset capturedAt) =>
    [
        "export-project-transfer",
        "--profile", context.ProfileId,
        "--state-root", Path.GetDirectoryName(context.DatabasePath)!,
        "--project-link", LocalTestContext.PrimaryProjectLinkId,
        "--destination-root", destination,
        "--run-id", "tenant-rebind-source",
        "--captured-at-utc", capturedAt.ToString("O")
    ];

    private static string[] RebindArguments(
        string bundlePath,
        string expectedSha256,
        string destination,
        string runId,
        DateTimeOffset capturedAt,
        string sourceTenantId = LocalTestContext.TenantId,
        string targetTenantId = "pegasus-local") =>
    [
        CortexLocalProjectTransferTenantRebindArguments.Command,
        "--bundle", bundlePath,
        "--expected-sha256", expectedSha256,
        "--source-tenant", sourceTenantId,
        "--target-tenant", targetTenantId,
        "--project-link", LocalTestContext.PrimaryProjectLinkId,
        "--project-id", LocalTestContext.PrimaryProjectId,
        "--namespace", LocalTestContext.PrimaryNamespace,
        "--destination-root", destination,
        "--run-id", runId,
        "--captured-at-utc", capturedAt.ToString("O")
    ];

    private static string[] ImportArguments(
        string profile,
        string stateRoot,
        string bundlePath,
        CortexLocalProjectRegistration registration) =>
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
        "--allow-write", "true"
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
}
