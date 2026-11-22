using System.Text.Json;
using System.Text;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Mcp.Local.Configuration;
using Pegasus.Cortex.Mcp.Local.Runtime;
using Pegasus.Cortex.Persistence.File.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;

namespace Pegasus.Cortex.Mcp.Local.Offline;

public static partial class CortexOfflineProductCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask<(int ExitCode, string Json)> WarmupAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await CortexOfflineWarmup.RunAsync(
                CortexCurrentUserLayout.Resolve(),
                TimeProvider.System,
                cancellationToken).ConfigureAwait(false);
            return (result.Healthy ? 0 : 1, JsonSerializer.Serialize(result, JsonOptions));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure("warmup-failed", exception);
        }
    }

    public static ValueTask<(int ExitCode, string Json)> RunAsync(
        IReadOnlyList<string> args,
        CortexCurrentUserLayout? layout = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(args, layout, timeProvider, beforeLegacyCatalogCommit: null, cancellationToken);

    internal static async ValueTask<(int ExitCode, string Json)> RunAsync(
        IReadOnlyList<string> args,
        CortexCurrentUserLayout? layout,
        TimeProvider? timeProvider,
        Func<CancellationToken, ValueTask>? beforeLegacyCatalogCommit,
        CancellationToken cancellationToken)
    {
        if (CortexPrivateReadCacheArguments.IsCommand(args))
            return await CortexPrivateReadCacheCommands.RunAsync(args, layout, timeProvider, cancellationToken).ConfigureAwait(false);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await RunCoreAsync(args, layout, timeProvider, beforeLegacyCatalogCommit, cancellationToken)
            .ConfigureAwait(false);
        // Diagnostics are best-effort when the central database is offline. Do not
        // create another store, replay spool bodies, or make a successful capture fail.
        try
        {
            var invocation = CortexOfflineCliArguments.Parse(args);
            layout ??= CortexCurrentUserLayout.Resolve();
            await using var metricsStore = new SqliteCortexV2Store(new(layout.V2DatabasePath), timeProvider);
            var recorder = new CortexUsageRecorder(metricsStore, "offline", invocation.OptionalSingle("task-id"));
            using var document = JsonDocument.Parse(result.Json);
            var value = document.RootElement;
            var spaces = Array.Empty<string>();
            if (result.ExitCode == 0 &&
                (value.TryGetProperty("effectiveNamespaces", out var ns) || value.TryGetProperty("readNamespaces", out ns)) &&
                ns.ValueKind == JsonValueKind.Array)
                spaces = ns.EnumerateArray().Select(x => CortexKnowledgeSpaces.Canonicalize(x.GetString()!)).Distinct().ToArray();
            var items = value.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0;
            var exact = invocation.Command == "read" && value.TryGetProperty("pending", out _) ? 1 : 0;
            await recorder.RecordAsync(invocation.Command, spaces, result.ExitCode != 0,
                Math.Max(items, exact), exact, Encoding.UTF8.GetByteCount(result.Json), started).ConfigureAwait(false);
            var response = System.Text.Json.Nodes.JsonNode.Parse(result.Json)!.AsObject();
            response["utilizationRecorded"] = recorder.LostSamples == 0;
            return (result.ExitCode, response.ToJsonString(JsonOptions));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return result;
        }
    }

    private static async ValueTask<(int ExitCode, string Json)> RunCoreAsync(
        IReadOnlyList<string> args,
        CortexCurrentUserLayout? layout,
        TimeProvider? timeProvider,
        Func<CancellationToken, ValueTask>? beforeLegacyCatalogCommit,
        CancellationToken cancellationToken)
    {
        try
        {
            var invocation = CortexOfflineCliArguments.Parse(args);
            layout ??= CortexCurrentUserLayout.Resolve();
            timeProvider ??= TimeProvider.System;
            return invocation.Command switch
            {
                "init" => await InitAsync(invocation, layout, timeProvider, cancellationToken)
                    .ConfigureAwait(false),
                "migrate-legacy" => await MigrateLegacyAsync(
                        invocation,
                        layout,
                        timeProvider,
                        beforeLegacyCatalogCommit,
                        cancellationToken)
                    .ConfigureAwait(false),
                "capture" => await CaptureAsync(invocation, layout, timeProvider, cancellationToken)
                    .ConfigureAwait(false),
                "status" => await StatusAsync(invocation, layout, timeProvider, cancellationToken)
                    .ConfigureAwait(false),
                "search" => await SearchAsync(invocation, layout, timeProvider, cancellationToken)
                    .ConfigureAwait(false),
                "read" => await ReadAsync(invocation, layout, timeProvider, cancellationToken)
                    .ConfigureAwait(false),
                "ingest" => await IngestAsync(invocation, layout, timeProvider, cancellationToken)
                    .ConfigureAwait(false),
                "refresh" => await RefreshAsync(invocation, layout, timeProvider, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new ArgumentException("The offline command is unsupported.")
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure("offline-command-failed", exception);
        }
    }

    private static async ValueTask<(int, string)> InitAsync(
        CortexOfflineCliInvocation invocation,
        CortexCurrentUserLayout layout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var root = CanonicalProjectRoot(invocation.RequireSingle("root"));
        var taskId = TaskId(invocation);
        var manifest = RequestedManifest(invocation);
        layout.EnsureCreated();
        var catalog = Catalog(layout, timeProvider);
        await catalog.RequireNoLegacyRootAsync(root, cancellationToken).ConfigureAwait(false);
        await using var store = await CortexOfflineFileStore.InitializeAsync(
            root,
            taskId,
            manifest,
            timeProvider,
            cancellationToken).ConfigureAwait(false);
        var registration = await catalog.RegisterAsync(root, taskId, store.Manifest, cancellationToken)
            .ConfigureAwait(false);
        return Success(new
        {
            healthy = true,
            state = "registered",
            project = registration,
            catalog = catalog.Path,
            network = false,
            service = false
        });
    }

    private static async ValueTask<(int, string)> MigrateLegacyAsync(
        CortexOfflineCliInvocation invocation,
        CortexCurrentUserLayout layout,
        TimeProvider timeProvider,
        Func<CancellationToken, ValueTask>? beforeCatalogCommit,
        CancellationToken cancellationToken)
    {
        var root = CanonicalProjectRoot(invocation.RequireSingle("root"));
        var taskId = TaskId(invocation);
        var manifest = RequestedManifest(invocation);
        var catalog = Catalog(layout, timeProvider);

        // Validate the exact historical binding before creating any task
        // scope. InitializeAsync then rejects root-level HOT/unscoped frames,
        // requiring the operator to archive them explicitly first.
        _ = await catalog.RequireLegacyMigrationAsync(
                root,
                taskId,
                manifest,
                cancellationToken)
            .ConfigureAwait(false);
        await using var store = await CortexOfflineFileStore.InitializeAsync(
            root,
            taskId,
            manifest,
            timeProvider,
            cancellationToken).ConfigureAwait(false);
        CortexOfflineLegacyMigrationResult migration;
        try
        {
            if (beforeCatalogCommit is not null)
                await beforeCatalogCommit(cancellationToken).ConfigureAwait(false);
            migration = await catalog.MigrateLegacyAsync(
                    root,
                    taskId,
                    store.Manifest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "The catalog commit failed after the exact task scope was initialized. No legacy content was assigned; after resolving the catalog failure, replay the same canonical root, task ID, and binding. Divergent replay fails closed.",
                exception);
        }
        return Success(new
        {
            healthy = true,
            state = migration.Disposition,
            taskId,
            projectId = store.Manifest.ProjectId,
            writeNamespace = store.Manifest.WriteNamespace,
            readNamespaces = store.Manifest.ReadNamespaces,
            legacyRegistrationPreserved = migration.LegacyRegistrationPreserved,
            legacyContentImported = false,
            legacyTaskAssigned = false,
            network = false,
            service = false
        });
    }

    private static async ValueTask<(int, string)> CaptureAsync(
        CortexOfflineCliInvocation invocation,
        CortexCurrentUserLayout layout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var root = await RegisteredRootAsync(invocation, layout, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        var taskId = TaskId(invocation);
        await using var store = await CortexOfflineFileStore.OpenAsync(
            root,
            taskId,
            timeProvider,
            cancellationToken).ConfigureAwait(false);
        var content = await ReadExactContentAsync(
            invocation.RequireSingle("content-file"),
            cancellationToken).ConfigureAwait(false);
        var source = ParseEnum<CortexProvenanceSourceV2>(
            invocation.OptionalSingle("source") ?? "user",
            "source");
        var payload = new CortexOfflineKnowledgeCreatePayloadV1(
            CortexOfflineSchemas.KnowledgeCreateV1,
            store.Manifest.WriteNamespace,
            invocation.RequireSingle("record-id"),
            ParseEnum<CortexKnowledgeKindV2>(invocation.RequireSingle("kind"), "kind"),
            ParseEnum<CortexKnowledgeTierV2>(invocation.RequireSingle("tier"), "tier"),
            invocation.RequireSingle("title"),
            content,
            invocation.Many("tag"),
            source,
            invocation.OptionalSingle("source-id") ?? "offline-capture",
            invocation.OptionalSingle("evidence") ?? string.Empty,
            timeProvider.GetUtcNow().ToUniversalTime());
        var result = await store.CaptureAsync(payload, cancellationToken).ConfigureAwait(false);
        return Success(new
        {
            healthy = true,
            state = result.Status.ToString(),
            taskId,
            effectiveNamespaces = new[] { store.Manifest.WriteNamespace },
            frame = result.Frame,
            network = false,
            service = false
        });
    }

    private static async ValueTask<(int, string)> StatusAsync(
        CortexOfflineCliInvocation invocation,
        CortexCurrentUserLayout layout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var root = await RegisteredRootAsync(invocation, layout, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        var taskId = TaskId(invocation);
        await using var store = await CortexOfflineFileStore.OpenAsync(root, taskId, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        var page = await store.ListAsync(new CortexOfflineListRequest(Limit: 32), cancellationToken)
            .ConfigureAwait(false);
        return Success(new
        {
            healthy = true,
            state = "ready",
            taskId,
            projectId = store.Manifest.ProjectId,
            root,
            writeNamespace = store.Manifest.WriteNamespace,
            readNamespaces = store.Manifest.ReadNamespaces,
            frames = page.Items,
            page.HasMore,
            page.NextCursor,
            network = false,
            service = false
        });
    }

    private static async ValueTask<(int, string)> SearchAsync(
        CortexOfflineCliInvocation invocation,
        CortexCurrentUserLayout layout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var root = await RegisteredRootAsync(invocation, layout, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        var taskId = TaskId(invocation);
        await using var store = await CortexOfflineFileStore.OpenAsync(root, taskId, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        var limit = ParseLimit(invocation.OptionalSingle("limit"));
        var page = await store.SearchAsync(
            new CortexOfflineSearchRequest(invocation.RequireSingle("text"), Limit: limit),
            cancellationToken).ConfigureAwait(false);
        return Success(new
        {
            healthy = true,
            taskId,
            effectiveNamespaces = store.Manifest.ReadNamespaces,
            items = page.Items,
            page.HasMore,
            page.NextCursor
        });
    }

    private static async ValueTask<(int, string)> ReadAsync(
        CortexOfflineCliInvocation invocation,
        CortexCurrentUserLayout layout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var root = await RegisteredRootAsync(invocation, layout, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        var taskId = TaskId(invocation);
        await using var store = await CortexOfflineFileStore.OpenAsync(root, taskId, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        var frameId = invocation.RequireSingle("frame-id");
        var inspection = await store.InspectAsync(frameId, cancellationToken).ConfigureAwait(false);
        if (inspection.Frame.Status != CortexOfflineFrameStatus.Pending)
        {
            return Success(new
            {
                healthy = inspection.IntegrityVerified,
                state = inspection.Frame.Status.ToString(),
                taskId,
                effectiveNamespaces = store.Manifest.ReadNamespaces,
                inspection
            });
        }
        var pending = await store.ReadPendingAsync(frameId, cancellationToken).ConfigureAwait(false);
        return Success(new
        {
            healthy = true,
            taskId,
            effectiveNamespaces = new[] { pending.Payload.Namespace },
            pending
        });
    }

    private static async ValueTask<(int, string)> IngestAsync(
        CortexOfflineCliInvocation invocation,
        CortexCurrentUserLayout layout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var root = await RegisteredRootAsync(invocation, layout, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        var taskId = TaskId(invocation);
        await using var offline = await CortexOfflineFileStore.OpenAsync(root, taskId, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        var requestedFrame = invocation.OptionalSingle("frame-id");
        var frameIds = requestedFrame is not null
            ? new[] { requestedFrame }
            : (await offline.ListAsync(
                    new CortexOfflineListRequest(Status: CortexOfflineFrameStatus.Pending, Limit: 32),
                    cancellationToken).ConfigureAwait(false))
                .Items.Select(item => item.FrameId).ToArray();

        layout.EnsureCreated();
        var lowLevel = new SqliteCortexV2Store(
            new SqliteCortexV2StoreOptions(layout.V2DatabasePath),
            timeProvider);
        await using var operational = new SqliteCortexV2OperationalStore(
            lowLevel,
            CortexLocalProductCommands.Version);
        await lowLevel.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var identity = CortexLocalOsIdentity.Capture();
        var application = new CortexV2Application(operational, $"os_{identity.Hash}", timeProvider,
            new CortexPersonalProfileClient(layout.LogDirectory));
        var results = new List<object>(frameIds.Length);
        var failed = false;
        foreach (var frameId in frameIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var inspection = await offline.InspectAsync(frameId, cancellationToken)
                    .ConfigureAwait(false);
                if (inspection.Frame.Status == CortexOfflineFrameStatus.Receipt)
                {
                    results.Add(new { frameId, state = "already-receipt" });
                    continue;
                }
                var adaptation = await offline.AdaptPendingAsync(frameId, cancellationToken)
                    .ConfigureAwait(false);
                if (adaptation.Candidate is not { } candidate)
                    throw new InvalidDataException(adaptation.Diagnostic ?? "The candidate payload is unsupported.");

                CortexKnowledgeV2? exact = null;
                try
                {
                    var read = await application.ReadAsync(
                        new CortexReadRequestV2(
                            candidate.Command.Namespace,
                            CortexRecordTypeV2.Knowledge,
                            candidate.Command.RecordId!,
                            Revision: null),
                        cancellationToken).ConfigureAwait(false);
                    exact = read.Record as CortexKnowledgeV2;
                }
                catch (KeyNotFoundException)
                {
                    // Only a genuinely absent exact head permits create.
                }

                var disposition = "existing";
                if (exact is null)
                {
                    var write = await application.ExecuteKnowledgeAsync(
                        candidate.Command,
                        cancellationToken).ConfigureAwait(false);
                    if (write.Record is null)
                        throw new InvalidOperationException(
                            $"Central create did not commit: {write.Disposition}.");
                    disposition = write.Disposition;
                    var read = await application.ReadAsync(
                        new CortexReadRequestV2(
                            candidate.Command.Namespace,
                            CortexRecordTypeV2.Knowledge,
                            candidate.Command.RecordId!,
                            write.Record.Revision),
                        cancellationToken).ConfigureAwait(false);
                    exact = read.Record as CortexKnowledgeV2 ??
                        throw new InvalidDataException("Central exact readback was not typed knowledge.");
                }
                if (exact.State != CortexKnowledgeStateV2.Active)
                {
                    throw new InvalidOperationException(
                        "The central record exists but is not active; the offline candidate remains pending.");
                }
                var verified = CortexOfflineReceiptVerifier.Verify(
                    candidate,
                    exact,
                    disposition,
                    timeProvider.GetUtcNow().ToUniversalTime());
                var replaced = await offline.ReplaceWithReceiptAsync(
                    frameId,
                    verified,
                    cancellationToken).ConfigureAwait(false);
                results.Add(new
                {
                    frameId,
                    state = replaced.Status.ToString(),
                    recordId = exact.RecordId,
                    exact.Revision
                });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed = true;
                results.Add(new
                {
                    frameId,
                    state = "conflict",
                    error = BoundedError(exception)
                });
            }
        }
        return (failed ? 1 : 0, JsonSerializer.Serialize(new
        {
            healthy = !failed,
            state = failed ? "partial-or-conflict" : "complete",
            taskId,
            projectId = offline.Manifest.ProjectId,
            effectiveNamespaces = new[] { offline.Manifest.WriteNamespace },
            items = results,
            network = false,
            service = false
        }, JsonOptions));
    }

    private static async ValueTask<(int, string)> RefreshAsync(
        CortexOfflineCliInvocation invocation,
        CortexCurrentUserLayout layout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var root = await RegisteredRootAsync(invocation, layout, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        var taskId = TaskId(invocation);
        await using var offline = await CortexOfflineFileStore.OpenAsync(root, taskId, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        layout.EnsureCreated();
        var lowLevel = new SqliteCortexV2Store(
            new SqliteCortexV2StoreOptions(layout.V2DatabasePath),
            timeProvider);
        await using var operational = new SqliteCortexV2OperationalStore(
            lowLevel,
            CortexLocalProductCommands.Version);
        await lowLevel.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var identity = CortexLocalOsIdentity.Capture();
        var application = new CortexV2Application(operational, $"os_{identity.Hash}", timeProvider,
            new CortexPersonalProfileClient(layout.LogDirectory));
        var central = await application.StartAsync(
            new CortexStartRequestV2(
                offline.Manifest.ReadNamespaces,
                "refresh bounded project Cortex projection",
                KnownContextCursor: null,
                TaskId: taskId),
            cancellationToken).ConfigureAwait(false);
        var hotItems = new List<CortexContextItemV2>(central.Items.Count + 1);
        if (central.CurrentTask is not null)
        {
            hotItems.Add(new CortexContextItemV2(
                ProjectNamespace(central.CurrentTask.Namespace),
                "task",
                central.CurrentTask.TaskId,
                central.CurrentTask.Revision,
                central.CurrentTask.Title,
                central.CurrentTask.Excerpt,
                Kind: null,
                Tier: null,
                Provenance: null,
                Score: double.MaxValue));
        }
        hotItems.AddRange(central.Items.Select(value => value with
        {
            Namespace = ProjectNamespace(value.Namespace)
        }));
        var projectedNamespaces = central.EffectiveNamespaces
            .Select(ProjectNamespace)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var result = await offline.MergeHotAsync(
            new CortexOfflineHotRefreshRequest(
                central.ContextCursor,
                projectedNamespaces,
                hotItems,
                central.HasMore),
            cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.SerializeToNode(new
        {
            healthy = true,
            taskId,
            projectId = offline.Manifest.ProjectId,
            effectiveNamespaces = projectedNamespaces,
            projection = result,
            network = false,
            service = false
        }, JsonOptions)!.AsObject();
        if (central.PersonalProfile is not null)
            response["personalProfile"] = JsonSerializer.SerializeToNode(central.PersonalProfile, CortexV2Json.Options);
        return Success(response);

        string ProjectNamespace(string value) =>
            offline.Manifest.ReadNamespaces.FirstOrDefault(candidate =>
                string.Equals(
                    CortexKnowledgeSpaces.Canonicalize(candidate),
                    value,
                    StringComparison.Ordinal)) ?? value;
    }

    private static CortexOfflineProjectCatalog Catalog(
        CortexCurrentUserLayout layout,
        TimeProvider timeProvider) => new(
        Path.Combine(layout.ConfigDirectory, "offline-projects.json"),
        timeProvider);

    private static async ValueTask<string> RegisteredRootAsync(
        CortexOfflineCliInvocation invocation,
        CortexCurrentUserLayout layout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var root = CanonicalProjectRoot(invocation.RequireSingle("root"));
        _ = await Catalog(layout, timeProvider).RequireRegisteredRootAsync(
                root,
                TaskId(invocation),
                cancellationToken)
            .ConfigureAwait(false);
        return root;
    }

    private static string TaskId(CortexOfflineCliInvocation invocation)
        => CortexOfflineCliArguments.RequireTaskId(invocation.RequireSingle("task-id"));

    private static CortexFileProjectManifestV2 RequestedManifest(
        CortexOfflineCliInvocation invocation)
    {
        var writeNamespace = invocation.RequireSingle("write-namespace");
        var readNamespaces = invocation.Many("read-namespace").Prepend(writeNamespace)
            .Distinct(StringComparer.Ordinal).ToArray();
        return new CortexFileProjectManifestV2(
            CortexFileProjectManifestV2.CurrentSchema,
            invocation.RequireSingle("project-id"),
            writeNamespace,
            readNamespaces);
    }

    private static string CanonicalProjectRoot(string value)
    {
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException("The offline project root must be an absolute canonical path.", nameof(value));
        var canonical = Path.GetFullPath(value);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(value, canonical, comparison))
            throw new ArgumentException("The offline project root must already be canonical.", nameof(value));
        return CortexLocalRegistryPathPolicy.ValidateProjectRoot(canonical);
    }

    private static async ValueTask<string> ReadExactContentAsync(
        string value,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException("The content file path must be absolute.", nameof(value));
        var path = Path.GetFullPath(value);
        CortexLocalRegistryPathPolicy.RequireSafeExistingAncestors(
            Path.GetDirectoryName(path) ?? throw new ArgumentException("The content file has no parent."));
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.Length is < 1 or > CortexOfflineLimits.MaximumPayloadUtf8Bytes)
        {
            throw new IOException("The content file is missing, unsafe, or exceeds the byte bound.");
        }
        var beforeLength = info.Length;
        var beforeWrite = info.LastWriteTimeUtc;
        byte[] bytes;
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         4096,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        info.Refresh();
        if (!info.Exists || info.Length != beforeLength || info.LastWriteTimeUtc != beforeWrite)
            throw new IOException("The content file changed while it was being captured.");
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The content file is not strict UTF-8.", exception);
        }
    }

    private static T ParseEnum<T>(string value, string name) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ArgumentException($"The offline {name} value is unsupported.", name);

    private static int ParseLimit(string? value) => value is null
        ? CortexOfflineLimits.MaximumPageSize
        : int.TryParse(value, out var parsed) && parsed is >= 1 and <= CortexOfflineLimits.MaximumPageSize
            ? parsed
            : throw new ArgumentOutOfRangeException(nameof(value));

    private static (int, string) Success(object value) =>
        (0, JsonSerializer.Serialize(value, JsonOptions));

    private static string BoundedError(Exception exception)
    {
        var value = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 256 ? value : value[..256];
    }

    internal static (int ExitCode, string Json) Failure(string state, Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length > 512)
            message = message[..512];
        return (1, JsonSerializer.Serialize(new
        {
            healthy = false,
            state,
            error = message,
            network = false,
            service = false
        }, JsonOptions));
    }
}
