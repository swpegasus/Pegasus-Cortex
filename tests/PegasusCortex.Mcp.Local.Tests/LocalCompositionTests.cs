using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Data.Sqlite;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;
using Pegasus.Cortex.Persistence.Sqlite.Storage;

internal static class LocalCompositionTests
{
    public static async Task RegistryAndToolAllowlistsAsync()
    {
        await using (var emptyContext = await LocalTestContext.CreateAsync(
            includePrimary: false,
            includeSplitAllowlists: false))
        await using (var composition = await CortexMcpLocalComposition.CreateAsync(
            emptyContext.ProfileId,
            emptyContext.DatabasePath,
            emptyContext.RegistryPath,
            "stdio",
            TimeProvider.System,
            CancellationToken.None))
        {
            var names = ToolNames(composition);
            LocalAssert.True(
                names.Contains("cortex_discover") &&
                names.Contains("cortex_status") &&
                names.Contains("cortex_search") &&
                names.Contains("cortex_read"),
                "An empty profile did not expose generalized read-only Cortex tools.");
            LocalAssert.False(
                names.Contains("cortex_project_brief") ||
                names.Contains("cortex_read_retained_record") ||
                names.Contains("cortex_submit_task_snapshot") ||
                names.Contains("cortex_submit_work_packet") ||
                names.Contains("cortex_submit_proposal"),
                "An empty profile invented project-scoped read or write authority.");
        }

        await using var readContext = await LocalTestContext.CreateAsync(
            includePrimary: true,
            primaryRead: true,
            primaryWrite: false,
            includeSplitAllowlists: false);
        await using (var composition = await CortexMcpLocalComposition.CreateAsync(
            readContext.ProfileId,
            readContext.DatabasePath,
            readContext.RegistryPath,
            "stdio",
            TimeProvider.System,
            CancellationToken.None))
        {
            var names = ToolNames(composition);
            LocalAssert.True(
                names.Contains("cortex_project_brief") &&
                names.Contains("cortex_read_retained_record"),
                "A read-allowlisted registry did not advertise local read tools.");
            LocalAssert.False(
                names.Contains("cortex_submit_task_snapshot") ||
                names.Contains("cortex_submit_work_packet"),
                "A read-only registry advertised local writer tools.");
        }

        await using var writeContext = await LocalTestContext.CreateAsync(
            includePrimary: true,
            primaryRead: false,
            primaryWrite: true,
            includeSplitAllowlists: false);
        await using (var composition = await CortexMcpLocalComposition.CreateAsync(
            writeContext.ProfileId,
            writeContext.DatabasePath,
            writeContext.RegistryPath,
            "stdio",
            TimeProvider.System,
            CancellationToken.None))
        {
            var names = ToolNames(composition);
            LocalAssert.True(
                names.Contains("cortex_submit_task_snapshot") &&
                names.Contains("cortex_submit_work_packet"),
                "A write-allowlisted registry did not advertise local writer tools.");
            LocalAssert.False(
                names.Contains("cortex_project_brief") ||
                names.Contains("cortex_read_retained_record"),
                "A write-only registry advertised local retained-read tools.");
            LocalAssert.True(
                names.Contains("cortex_search") && names.Contains("cortex_read"),
                "The standalone profile weakened existing Default Library tools.");
        }

        await LocalAssert.ThrowsAsync<InvalidDataException>(
            () => CortexLocalProjectRegistry.LoadAsync(
                readContext.RegistryPath,
                "wrong-profile",
                CancellationToken.None).AsTask(),
            "A registry was accepted for the wrong explicit profile.");

        var document = CortexLocalProjectRegistryFile.Create(
            readContext.ProfileId,
            readContext.Registrations);
        var poisoned = document with { RegistryHash = new string('f', 64) };
        await LocalAssert.ThrowsAsync<InvalidDataException>(
            () => Task.Run(() => CortexLocalProjectRegistryPolicy.Validate(
                poisoned,
                readContext.ProfileId,
                validateRoots: true)),
            "A registry with a poisoned self-hash was accepted.");
        var registered = readContext.Registrations.Single();
        var duplicateProjectId = registered with
        {
            ProjectLinkId = $"plink_{new string('d', 64)}",
            ProjectNamespace = "project.duplicate-id"
        };
        await LocalAssert.ThrowsAsync<InvalidDataException>(
            () => Task.Run(() => CortexLocalProjectRegistryPolicy.Seal(
                document with
                {
                    Projects = [registered, duplicateProjectId]
                })),
            "A profile accepted one repository ProjectId under multiple links or namespaces.");

        await SemanticStartupIntegrityFailsClosedAsync().ConfigureAwait(false);
        await ProvisioningLeavesExistingDirectoryUntouchedAsync()
            .ConfigureAwait(false);
    }

    public static Task DependencyClosureAsync()
    {
        var assembly = typeof(CortexMcpLocalComposition).Assembly;
        var forbidden = new[]
        {
            "Npgsql",
            "PegasusCortex.Persistence.PostgreSql",
            "PegasusCortex.Service",
            "PegasusCortex.Mcp.Integrated",
            "System.Net"
        };
        var references = assembly.GetReferencedAssemblies()
            .Select(item => item.Name ?? string.Empty)
            .ToArray();
        foreach (var name in forbidden)
        {
            LocalAssert.False(
                references.Any(reference =>
                    reference.Contains(name, StringComparison.OrdinalIgnoreCase)),
                $"The local entry assembly references forbidden dependency '{name}'.");
        }

        var output = Path.GetDirectoryName(assembly.Location) ??
            throw new InvalidOperationException("The local assembly output is unavailable.");
        var outputNames = Directory.EnumerateFiles(output)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .ToArray();
        LocalAssert.True(
            outputNames.Any(name =>
                string.Equals(
                    name,
                    "PegasusCortex.Persistence.Sqlite.dll",
                    StringComparison.OrdinalIgnoreCase)) &&
            outputNames.Any(name =>
                string.Equals(
                    name,
                    "PegasusCortex.LocalTransport.dll",
                    StringComparison.OrdinalIgnoreCase)),
            "The local output is missing SQLite or LocalTransport.");
        foreach (var name in forbidden)
        {
            LocalAssert.False(
                outputNames.Any(file =>
                    file!.Contains(name, StringComparison.OrdinalIgnoreCase)),
                $"The local output contains forbidden dependency '{name}'.");
        }

        var depsPath = Path.Combine(output, "PegasusCortex.Mcp.Local.deps.json");
        var deps = File.ReadAllText(depsPath);
        foreach (var name in forbidden)
        {
            LocalAssert.False(
                deps.Contains(name, StringComparison.OrdinalIgnoreCase),
                $"The local deps manifest contains forbidden dependency '{name}'.");
        }

        var localSourceRoot = Path.GetFullPath(
            Path.Combine(output, "..", "..", "..", "..", "..", "src",
                "PegasusCortex.Mcp.Local"));
        if (Directory.Exists(localSourceRoot))
        {
            foreach (var file in Directory.EnumerateFiles(
                         localSourceRoot,
                         "*.cs",
                         SearchOption.AllDirectories)
                     .Where(file =>
                         !file.Contains(
                             $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                             StringComparison.OrdinalIgnoreCase) &&
                         !file.Contains(
                             $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                             StringComparison.OrdinalIgnoreCase)))
            {
                var text = File.ReadAllText(file);
                LocalAssert.False(
                    text.Contains("System.Net", StringComparison.Ordinal) ||
                    text.Contains("GetEnvironmentVariable", StringComparison.Ordinal) ||
                    text.Contains("Npgsql", StringComparison.Ordinal) ||
                    text.Contains("PostgreSql", StringComparison.Ordinal) ||
                    text.Contains("PegasusCortex.Service", StringComparison.Ordinal) ||
                    text.Contains("Mcp.Integrated", StringComparison.Ordinal),
                    $"Local source '{file}' contains a forbidden activation/dependency marker.");
            }
        }
        return Task.CompletedTask;
    }

    private static HashSet<string> ToolNames(CortexMcpLocalComposition composition) =>
        composition.Application.ListTools()
            .Select(item => item!.AsObject()["name"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

    private static async Task SemanticStartupIntegrityFailsClosedAsync()
    {
        await using var context = await LocalTestContext.CreateAsync(
            includePrimary: true,
            primaryRead: true,
            primaryWrite: true,
            includeSplitAllowlists: false);
        var first = context.TaskSnapshot(projectLinkId: context.Registrations[0].ProjectLinkId);
        var second = context.TaskSnapshot(
            projectLinkId: context.Registrations[0].ProjectLinkId,
            revision: 2,
            previousHash: first.SnapshotHash);
        string firstContentHash;
        await using (var store = new SqliteCortexRetentionStore(
            new SqliteCortexStoreOptions(context.DatabasePath)))
        {
            var retention = new CortexAgentRetentionApplicationService(store);
            var firstResult = await retention.SubmitTaskSnapshotAsync(
                first,
                CancellationToken.None);
            var secondResult = await retention.SubmitTaskSnapshotAsync(
                second,
                CancellationToken.None);
            LocalAssert.Equal(
                CortexRetentionWriteDispositions.Appended,
                firstResult.Disposition,
                "The first integrity fixture revision was not appended.");
            LocalAssert.Equal(
                CortexRetentionWriteDispositions.Appended,
                secondResult.Disposition,
                "The second integrity fixture revision was not appended.");
            firstContentHash = firstResult.Document!.ContentHash;
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = context.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE retained_head
                   SET revision = 1,
                       content_hash = $hash
                 WHERE project_id = $project
                   AND kind = 'task-snapshot'
                   AND document_id = $document;
                """;
            command.Parameters.AddWithValue("$hash", firstContentHash);
            command.Parameters.AddWithValue("$project", LocalTestContext.PrimaryProjectId);
            command.Parameters.AddWithValue("$document", first.TaskSetId);
            LocalAssert.Equal(
                1,
                await command.ExecuteNonQueryAsync(),
                "The semantic-integrity fixture did not rewind one head.");
        }

        await LocalAssert.ThrowsAsync<InvalidDataException>(
            async () =>
            {
                await using var ignored =
                    await CortexMcpLocalComposition.CreateAsync(
                        context.ProfileId,
                        context.DatabasePath,
                        context.RegistryPath,
                        "stdio",
                        TimeProvider.System,
                        CancellationToken.None);
            },
            "The local composition advertised tools over a semantically corrupted store.");
    }

    private static async Task ProvisioningLeavesExistingDirectoryUntouchedAsync()
    {
        var parent = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            $"pcx-existing-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(parent);
        var profile = $"occupied-{Guid.NewGuid():N}"[..32];
        var stateRoot = Path.Combine(parent, profile);
        Directory.CreateDirectory(stateRoot);
        var markerPath = Path.Combine(stateRoot, "owner-data.txt");
        var marker = new byte[] { 1, 3, 5, 7, 9 };
        await File.WriteAllBytesAsync(markerPath, marker).ConfigureAwait(false);
        var securityBefore = CaptureDirectorySecurity(stateRoot);
        try
        {
            await LocalAssert.ThrowsAsync<IOException>(
                () => CortexLocalProjectRegistryProvisioner.CreateNewPortableAsync(
                    profile,
                    stateRoot,
                    [],
                    CancellationToken.None).AsTask(),
                "Create-new provisioning overwrote an existing state root.");
            var markerAfter = await File.ReadAllBytesAsync(markerPath)
                .ConfigureAwait(false);
            LocalAssert.True(
                marker.SequenceEqual(markerAfter),
                "Failed provisioning changed an existing state-root file.");
            LocalAssert.Equal(
                securityBefore,
                CaptureDirectorySecurity(stateRoot),
                "Failed provisioning changed an existing state-root ACL or mode.");
        }
        finally
        {
            if (Directory.Exists(parent))
                Directory.Delete(parent, recursive: true);
        }
    }

    private static string CaptureDirectorySecurity(string path) =>
        OperatingSystem.IsWindows()
            ? CaptureWindowsDirectorySecurity(path)
            : File.GetUnixFileMode(path).ToString();

    [SupportedOSPlatform("windows")]
    private static string CaptureWindowsDirectorySecurity(string path)
    {
        const AccessControlSections Sections =
            AccessControlSections.Access |
            AccessControlSections.Owner |
            AccessControlSections.Group;
        return new DirectoryInfo(path)
            .GetAccessControl(Sections)
            .GetSecurityDescriptorSddlForm(Sections);
    }
}
