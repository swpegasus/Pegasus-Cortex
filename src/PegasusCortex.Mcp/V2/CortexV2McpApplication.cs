using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.V2;
using Pegasus.Cortex.Mcp.Protocol;

namespace Pegasus.Cortex.Mcp.V2;

/// <summary>
/// Thin MCP adapter for the eight-operation V2 surface. It holds no namespace,
/// task, cursor, or project state between calls.
/// </summary>
public sealed class CortexV2McpApplication : ICortexMcpProtocolApplication
{
    private readonly CortexV2Application _application;
    private readonly CortexUsageRecorder? _usage;

    public CortexV2McpApplication(CortexV2Application application, CortexUsageRecorder? usage = null)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _usage = usage;
    }

    public JsonArray ListTools() => CortexV2McpContract.ListTools();

    public async ValueTask<JsonObject> CallToolAsync(
        string name,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        JsonObject? result = null;
        try
        {
            result = await CallToolCoreAsync(name, arguments, cancellationToken).ConfigureAwait(false);
            if (name == "cortex_status" && result["isError"]?.GetValue<bool>() == false && _usage is not null)
            {
                var report = await _usage.ReportAsync().ConfigureAwait(false);
                result["structuredContent"]!["utilization"] = JsonSerializer.SerializeToNode(new
                {
                    report.Schema, report.State, report.Days, report.RetentionDays,
                    report.CollectionStartedUtc, report.LastSeenUtc, report.ObservedProcesses, report.ObservedTasks,
                    processLostSamples = _usage.LostSamples,
                    calls = report.Rows.Where(row => row.Dimension == "total").Sum(row => row.Calls),
                    failures = report.Rows.Where(row => row.Dimension == "total").Sum(row => row.Failures),
                    exactRecords = report.Rows.Where(row => row.Dimension == "total").Sum(row => row.ExactRecords),
                    responseBytes = report.Rows.Where(row => row.Dimension == "total").Sum(row => row.ResponseBytes),
                    detail = "dashboard or PegasusCortex.exe metrics --days 7",
                    coverage = "instrumented-processes-only; direct HOT file reads and usefulness are unknown"
                }, CortexV2Json.Options);
            }
            return result;
        }
        finally
        {
            if (_usage is not null)
                await CortexUsageMcpObserver.RecordAsync(_usage, name, arguments, result, started).ConfigureAwait(false);
        }
    }

    private async ValueTask<JsonObject> CallToolCoreAsync(
        string name,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        if (!CortexV2McpContract.ToolNames.Contains(name, StringComparer.Ordinal))
            return Error("cortex_v2_tool_unknown", "The V2 tool is not advertised.", name);
        if (arguments is null || arguments.Value.ValueKind != JsonValueKind.Object ||
            !ContainsUniqueProperties(arguments.Value))
            return Error("cortex_v2_arguments_invalid", "Arguments must be one object with unique properties.", name);
        if (name == "cortex_status" &&
            arguments.Value.EnumerateObject().Any())
        {
            return Error("cortex_v2_arguments_invalid", "This V2 tool accepts no arguments.", name);
        }

        try
        {
            var values = arguments.Value;
            object value = name switch
            {
                "cortex_status" => await _application.StatusAsync(cancellationToken).ConfigureAwait(false),
                "cortex_namespaces" => await _application.ListNamespacesAsync(Deserialize<CortexNamespacesRequestV2>(values), cancellationToken).ConfigureAwait(false),
                "cortex_start" => await _application.StartAsync(Deserialize<CortexStartRequestV2>(values), cancellationToken).ConfigureAwait(false),
                "cortex_search" => await _application.SearchAsync(Deserialize<CortexSearchRequestV2>(values), cancellationToken).ConfigureAwait(false),
                "cortex_read" => values.TryGetProperty("records", out _)
                    ? await _application.ReadBatchAsync(Deserialize<CortexReadBatchRequestV2>(values), cancellationToken).ConfigureAwait(false)
                    : await _application.ReadAsync(Deserialize<CortexReadRequestV2>(values), cancellationToken).ConfigureAwait(false),
                "cortex_task" => await _application.ExecuteTaskAsync(Deserialize<CortexTaskCommandV2>(values), cancellationToken).ConfigureAwait(false),
                "cortex_knowledge" => await _application.ExecuteKnowledgeAsync(Deserialize<CortexKnowledgeCommandV2>(values), cancellationToken).ConfigureAwait(false),
                "cortex_finish" => await _application.FinishAsync(Deserialize<CortexFinishRequestV2>(values), cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("The V2 tool dispatch table is incomplete.")
            };
            // Default only the MCP preview presentation. Exact reads and the
            // underlying Core/CLI contracts keep their full typed result bodies.
            var compact = values.TryGetProperty("format", out var format)
                ? format.ValueKind == JsonValueKind.String &&
                    string.Equals(format.GetString(), "compact", StringComparison.Ordinal)
                : name is "cortex_start" or "cortex_search";
            return Success(value, compact);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or InvalidDataException or
            KeyNotFoundException or JsonException)
        {
            var code = exception switch
            {
                CortexPayloadUnavailableException => CortexPayloadUnavailableException.Code,
                CortexPersonalProfileException personal => personal.Code,
                CortexV2ResponseBudgetExceededException => "cortex_v2_response_too_large",
                _ => "cortex_v2_rejected"
            };
            return Error(code, exception.Message, name);
        }
    }

    public ValueTask<JsonArray> ListResourcesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new JsonArray());
    }

    public ValueTask<JsonArray> ListResourceTemplatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new JsonArray());
    }

    public ValueTask<JsonObject?> ReadResourceAsync(
        string uri,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<JsonObject?>(null);
    }

    private static T Deserialize<T>(JsonElement arguments)
    {
        var value = arguments.Deserialize<T>(CortexV2Json.Options);
        if (value is null)
            throw new JsonException("The V2 argument object was empty.");

        object normalized = value switch
        {
            CortexNamespacesRequestV2 request => request with
            {
                Limit = DefaultWhenOmitted(arguments, "limit", request.Limit, CortexV2Limits.DefaultPageSize),
                MaxUtf8Bytes = DefaultWhenOmitted(arguments, "maxUtf8Bytes", request.MaxUtf8Bytes, CortexV2Limits.DefaultResponseUtf8Bytes)
            },
            CortexStartRequestV2 request => request with
            {
                MaxUtf8Bytes = DefaultWhenOmitted(arguments, "maxUtf8Bytes", request.MaxUtf8Bytes, CortexV2Limits.DefaultStartResponseUtf8Bytes),
                Format = arguments.TryGetProperty("format", out _) ? request.Format : CortexResponseFormatV2.Compact
            },
            CortexSearchRequestV2 request => request with
            {
                Limit = DefaultWhenOmitted(arguments, "limit", request.Limit, CortexV2Limits.DefaultPageSize),
                MaxUtf8Bytes = DefaultWhenOmitted(arguments, "maxUtf8Bytes", request.MaxUtf8Bytes, CortexV2Limits.DefaultResponseUtf8Bytes),
                Format = arguments.TryGetProperty("format", out _) ? request.Format : CortexResponseFormatV2.Compact
            },
            CortexReadRequestV2 request => request with
            {
                MaxUtf8Bytes = DefaultWhenOmitted(arguments, "maxUtf8Bytes", request.MaxUtf8Bytes, CortexV2Limits.MaximumResponseUtf8Bytes)
            },
            CortexReadBatchRequestV2 request => request with
            {
                MaxUtf8Bytes = DefaultWhenOmitted(arguments, "maxUtf8Bytes", request.MaxUtf8Bytes, CortexV2Limits.MaximumResponseUtf8Bytes)
            },
            CortexTaskCommandV2 command => command with
            {
                Limit = DefaultWhenOmitted(arguments, "limit", command.Limit, CortexV2Limits.DefaultPageSize),
                MaxUtf8Bytes = DefaultWhenOmitted(arguments, "maxUtf8Bytes", command.MaxUtf8Bytes, CortexV2Limits.DefaultResponseUtf8Bytes)
            },
            CortexKnowledgeCommandV2 command => command with
            {
                MaxUtf8Bytes = DefaultWhenOmitted(arguments, "maxUtf8Bytes", command.MaxUtf8Bytes, CortexV2Limits.DefaultResponseUtf8Bytes),
                Limit = DefaultWhenOmitted(arguments, "limit", command.Limit, CortexV2Limits.DefaultPageSize)
            },
            CortexFinishRequestV2 request => request with
            {
                MaxUtf8Bytes = DefaultWhenOmitted(arguments, "maxUtf8Bytes", request.MaxUtf8Bytes, CortexV2Limits.MaximumCompactResponseUtf8Bytes)
            },
            _ => value
        };
        return (T)normalized;
    }

    private static int DefaultWhenOmitted(
        JsonElement arguments,
        string propertyName,
        int deserializedValue,
        int defaultValue) =>
        arguments.TryGetProperty(propertyName, out _) ? deserializedValue : defaultValue;

    private static bool ContainsUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || !ContainsUniqueProperties(property.Value))
                    return false;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (!ContainsUniqueProperties(item))
                    return false;
            }
        }
        return true;
    }

    private static JsonObject Success(object value, bool compact)
    {
        var structured = compact ? CompactStructured(value) :
            JsonSerializer.SerializeToNode(value, CortexV2Json.Options) as JsonObject ??
            new JsonObject { ["value"] = JsonSerializer.SerializeToNode(value, CortexV2Json.Options) };
        if (structured["personalProfile"] is null)
            structured.Remove("personalProfile");
        // Compact projections contain summaries only. Never prune exact records,
        // including a batch read that happens to request compact formatting.
        if (compact && value is CortexStartResultV2 or CortexSearchResultV2)
            OmitEmptyCompactOptionals(structured);
        var text = Summary(value);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["structuredContent"] = structured.DeepClone(),
            ["isError"] = false
        };
    }

    private static JsonObject CompactStructured(object value) => value switch
    {
        CortexStartResultV2 start => new JsonObject
        {
            ["schema"] = "cortex.start.compact/1",
            ["ns"] = StringArray(start.EffectiveNamespaces),
            ["mode"] = EnumValue(start.Disposition),
            ["cursor"] = start.ContextCursor,
            ["task"] = start.CurrentTask is null ? null : CompactTask(start.CurrentTask),
            ["items"] = new JsonArray(start.Items.Select(CompactItem).ToArray()),
            ["more"] = start.HasMore,
            ["personalProfile"] = start.PersonalProfile is null ? null : JsonSerializer.SerializeToNode(start.PersonalProfile, CortexV2Json.Options)
        },
        CortexSearchResultV2 search => new JsonObject
        {
            ["schema"] = "cortex.search.compact/1",
            ["ns"] = StringArray(search.EffectiveNamespaces),
            ["items"] = new JsonArray(search.Items.Select(CompactHit).ToArray()),
            ["next"] = search.NextCursor,
            ["more"] = search.HasMore,
            ["personalProfile"] = search.PersonalProfile is null ? null : JsonSerializer.SerializeToNode(search.PersonalProfile, CortexV2Json.Options)
        },
        _ => JsonSerializer.SerializeToNode(value, CortexV2Json.Options) as JsonObject ??
            new JsonObject { ["value"] = JsonSerializer.SerializeToNode(value, CortexV2Json.Options) }
    };

    private static void OmitEmptyCompactOptionals(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array)
                if (child is not null)
                    OmitEmptyCompactOptionals(child);
            return;
        }
        if (node is not JsonObject value)
            return;
        foreach (var property in value.ToArray())
        {
            if (property.Value is not null)
                OmitEmptyCompactOptionals(property.Value);
            if (property.Key is not ("task" or "next" or "phase" or "subject" or
                "kind" or "tier" or "src" or "personalProfile" or "profile" or "generation" or "issueCode"))
                continue;
            if (property.Value is null || property.Value is JsonObject { Count: 0 } ||
                property.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length == 0)
                value.Remove(property.Key);
        }
    }

    private static JsonObject CompactTask(CortexTaskSummaryV2 value) => new()
    {
        ["ns"] = value.Namespace,
        ["id"] = value.TaskId,
        ["rev"] = value.Revision,
        ["status"] = EnumValue(value.Status),
        ["title"] = value.Title,
        ["summary"] = value.Excerpt,
        ["phase"] = value.PhaseId,
        ["subject"] = value.CheckpointSubject
    };

    private static JsonObject CompactItem(CortexContextItemV2 value) => new()
    {
        ["ns"] = value.Namespace,
        ["type"] = value.RecordType,
        ["id"] = value.RecordId,
        ["rev"] = value.Revision,
        ["title"] = value.Title,
        ["summary"] = value.Excerpt,
        ["kind"] = value.Kind is null ? null : EnumValue(value.Kind.Value),
        ["tier"] = value.Tier is null ? null : EnumValue(value.Tier.Value),
        ["src"] = value.Provenance is null ? null : EnumValue(value.Provenance.Source),
        ["score"] = value.Score
    };

    private static JsonObject CompactHit(CortexSearchHitV2 value) => new()
    {
        ["ns"] = value.Namespace,
        ["type"] = EnumValue(value.RecordType),
        ["id"] = value.RecordId,
        ["rev"] = value.Revision,
        ["title"] = value.Title,
        ["summary"] = value.Excerpt,
        ["kind"] = value.Kind is null ? null : EnumValue(value.Kind.Value),
        ["tier"] = value.Tier is null ? null : EnumValue(value.Tier.Value),
        ["src"] = EnumValue(value.Provenance.Source),
        ["score"] = value.Score
    };

    private static JsonArray StringArray(IEnumerable<string> values) =>
        new(values.Select(value => JsonValue.Create(value)).ToArray());

    private static string EnumValue<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static string Summary(object value)
    {
        static string Names(IReadOnlyList<string> values) => string.Join(',', values);
        var summary = value switch
        {
            CortexStatusV2 status =>
                $"state={status.State} namespaces={status.NamespaceCount} heads={status.HeadCount} revisions={status.RevisionCount}",
            CortexNamespacesResultV2 namespaces =>
                $"namespaces={namespaces.Items.Count} hasMore={namespaces.HasMore} nextCursor={namespaces.NextCursor ?? "none"}",
            CortexStartResultV2 start =>
                $"effectiveNamespaces={Names(start.EffectiveNamespaces)} disposition={start.Disposition} currentTask={start.CurrentTask?.TaskId ?? "none"} items={start.Items.Count} hasMore={start.HasMore} cursor={start.ContextCursor}",
            CortexSearchResultV2 search =>
                $"effectiveNamespaces={Names(search.EffectiveNamespaces)} items={search.Items.Count} hasMore={search.HasMore} nextCursor={search.NextCursor ?? "none"}",
            CortexReadResultV2 read =>
                $"effectiveNamespaces={Names(read.EffectiveNamespaces)} recordType={read.RecordType} recordId={read.RecordId} revision={read.Revision}",
            CortexReadBatchResultV2 batch =>
                $"effectiveNamespaces={Names(batch.EffectiveNamespaces)} records={batch.Items.Count}",
            CortexTaskResultV2 task =>
                $"effectiveNamespaces={Names(task.EffectiveNamespaces)} disposition={task.Disposition} taskId={task.Task?.TaskId ?? "none"} revision={task.Task?.Revision.ToString() ?? "none"} status={task.Task?.Status.ToString() ?? "none"} items={task.Items.Count}",
            CortexKnowledgeResultV2 knowledge =>
                $"effectiveNamespaces={Names(knowledge.EffectiveNamespaces)} disposition={knowledge.Disposition} recordId={knowledge.Record?.RecordId ?? "none"} revision={knowledge.Record?.Revision.ToString() ?? "none"} suggestions={knowledge.Analysis?.Suggestions.Count.ToString() ?? "none"}",
            CortexFinishResultV2 finish =>
                $"effectiveNamespaces={Names(finish.EffectiveNamespaces)} disposition={finish.Disposition} taskId={finish.Task.TaskId} revision={finish.Task.Revision} knowledge={finish.Knowledge.Count} cursor={finish.ContextCursor}",
            _ => "cortex_v2 operation completed"
        };
        var personal = value switch
        {
            CortexStatusV2 status => status.PersonalProfile,
            CortexStartResultV2 start => start.PersonalProfile,
            CortexSearchResultV2 search => search.PersonalProfile,
            _ => null
        };
        if (personal is not null)
        {
            var availability = personal.Retryable ? "temporarily_unavailable" : personal.State;
            summary = $"personalProfile={availability} issue={personal.IssueCode ?? "none"} " +
                $"dataMissing={personal.IndicatesMissingData.ToString().ToLowerInvariant()} " +
                $"remapRequired={personal.RemapRequired.ToString().ToLowerInvariant()} " + summary;
        }
        return summary.Length <= 512 ? summary : summary[..512];
    }

    private static JsonObject Error(string code, string message, string operation)
    {
        var structured = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["operation"] = operation
            }
        };
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = structured.ToJsonString(McpProtocol.JsonOptions)
            }),
            ["structuredContent"] = structured.DeepClone(),
            ["isError"] = true
        };
    }
}
