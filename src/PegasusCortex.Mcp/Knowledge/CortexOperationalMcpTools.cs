using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Core.Operations;
using Pegasus.Cortex.Core.Retention;
using Pegasus.Cortex.Mcp.Protocol;

namespace Pegasus.Cortex.Mcp.Knowledge;

internal static class CortexOperationalMcpTools
{
    private static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "cortex_task_create", "cortex_task_list", "cortex_task_get",
        "cortex_task_update", "cortex_task_assign", "cortex_task_claim",
        "cortex_task_block", "cortex_task_complete", "cortex_task_reopen",
        "cortex_task_archive", "cortex_task_dependency_add",
        "cortex_task_dependency_remove", "cortex_task_history",
        "cortex_knowledge_create", "cortex_knowledge_search",
        "cortex_knowledge_read", "cortex_knowledge_revise",
        "cortex_knowledge_supersede", "cortex_knowledge_archive"
    };

    public static bool IsOperationalTool(string name) => Names.Contains(name);

    public static IEnumerable<JsonObject> ListTools(CortexMcpRuntimeComposition? runtime)
    {
        if (runtime?.Operations is null)
            yield break;
        if (runtime.HasCapability(CortexMcpRuntimeCapability.Tasks))
        {
            yield return Definition("cortex_task_create", "Create task", "Creates one namespace-local operational task with idempotency and revision one.", TaskCreateSchema(), false);
            yield return Definition("cortex_task_list", "List tasks", "Lists bounded current task heads in the explicit namespace.", TaskListSchema(), true);
            yield return Definition("cortex_task_get", "Read task", "Reads one current task head from the explicit namespace.", TaskGetSchema(), true);
            foreach (var name in new[] { "update", "assign", "claim", "block", "complete", "reopen", "archive" })
                yield return Definition($"cortex_task_{name}", $"{char.ToUpperInvariant(name[0])}{name[1..]} task", $"Applies the {name} transition using an expected revision and idempotency key.", TaskMutationSchema(name), false);
            yield return Definition("cortex_task_dependency_add", "Add task dependency", "Adds one dependency using expected-revision concurrency.", TaskDependencySchema(), false);
            yield return Definition("cortex_task_dependency_remove", "Remove task dependency", "Removes one dependency using expected-revision concurrency.", TaskDependencySchema(), false);
            yield return Definition("cortex_task_history", "Read task history", "Reads a bounded immutable task revision range.", TaskHistorySchema(), true);
        }
        if (runtime.HasCapability(CortexMcpRuntimeCapability.KnowledgeWrite))
        {
            yield return Definition("cortex_knowledge_create", "Create knowledge", "Creates an immediately searchable namespace-local knowledge record.", KnowledgeCreateSchema(), false);
            yield return Definition("cortex_knowledge_search", "Search local knowledge", "Searches current namespace-local knowledge heads using the normalized index.", KnowledgeSearchSchema(), true);
            yield return Definition("cortex_knowledge_read", "Read local knowledge", "Reads one current namespace-local knowledge record.", KnowledgeReadSchema(), true);
            yield return Definition("cortex_knowledge_revise", "Revise local knowledge", "Appends a knowledge revision using expected-revision concurrency.", KnowledgeMutationSchema("revise"), false);
            yield return Definition("cortex_knowledge_supersede", "Supersede local knowledge", "Marks one local record superseded by another record while preserving history.", KnowledgeMutationSchema("supersede"), false);
            yield return Definition("cortex_knowledge_archive", "Archive local knowledge", "Archives one local record while preserving history.", KnowledgeMutationSchema("archive"), false);
        }
    }

    public static async ValueTask<JsonObject> CallAsync(
        ICortexOperationalRuntime runtime,
        string name,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            if (arguments is null || arguments.Value.ValueKind != JsonValueKind.Object ||
                !ToolArgumentReader.ContainsUniqueObjectProperties(arguments.Value))
                return Error("cortex_operational_arguments_invalid", "Tool arguments must be one JSON object with unique properties.", name);
            if (!ToolArgumentReader.Validate(arguments, AllowedArguments(name), out var validationError))
                return Error("cortex_operational_arguments_invalid", validationError!, name);
            var values = arguments.Value;
            return name switch
            {
                "cortex_task_create" => await CreateTaskAsync(runtime, values, cancellationToken).ConfigureAwait(false),
                "cortex_task_list" => await ListTasksAsync(runtime, values, cancellationToken).ConfigureAwait(false),
                "cortex_task_get" => await GetTaskAsync(runtime, values, cancellationToken).ConfigureAwait(false),
                "cortex_task_history" => await TaskHistoryAsync(runtime, values, cancellationToken).ConfigureAwait(false),
                "cortex_task_dependency_add" => await MutateTaskAsync(runtime, values, "add-dependency", cancellationToken).ConfigureAwait(false),
                "cortex_task_dependency_remove" => await MutateTaskAsync(runtime, values, "remove-dependency", cancellationToken).ConfigureAwait(false),
                "cortex_task_update" => await MutateTaskAsync(runtime, values, "update", cancellationToken).ConfigureAwait(false),
                "cortex_task_assign" => await MutateTaskAsync(runtime, values, "assign", cancellationToken).ConfigureAwait(false),
                "cortex_task_claim" => await MutateTaskAsync(runtime, values, "claim", cancellationToken).ConfigureAwait(false),
                "cortex_task_block" => await MutateTaskAsync(runtime, values, "block", cancellationToken).ConfigureAwait(false),
                "cortex_task_complete" => await MutateTaskAsync(runtime, values, "complete", cancellationToken).ConfigureAwait(false),
                "cortex_task_reopen" => await MutateTaskAsync(runtime, values, "reopen", cancellationToken).ConfigureAwait(false),
                "cortex_task_archive" => await MutateTaskAsync(runtime, values, "archive", cancellationToken).ConfigureAwait(false),
                "cortex_knowledge_create" => await CreateKnowledgeAsync(runtime, values, cancellationToken).ConfigureAwait(false),
                "cortex_knowledge_search" => await SearchKnowledgeAsync(runtime, values, cancellationToken).ConfigureAwait(false),
                "cortex_knowledge_read" => await ReadKnowledgeAsync(runtime, values, cancellationToken).ConfigureAwait(false),
                "cortex_knowledge_revise" => await MutateKnowledgeAsync(runtime, values, "revise", cancellationToken).ConfigureAwait(false),
                "cortex_knowledge_supersede" => await MutateKnowledgeAsync(runtime, values, "supersede", cancellationToken).ConfigureAwait(false),
                "cortex_knowledge_archive" => await MutateKnowledgeAsync(runtime, values, "archive", cancellationToken).ConfigureAwait(false),
                _ => Error("cortex_tool_unknown", "The operational tool is unavailable.", name)
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or JsonException)
        {
            return Error("cortex_operational_rejected", exception.Message, name);
        }
    }

    private static async ValueTask<JsonObject> CreateTaskAsync(ICortexOperationalRuntime runtime, JsonElement values, CancellationToken token)
    {
        if (!Common(values, "taskId", out var ns, out var id, out var error) ||
            !ToolArgumentReader.RequiredString(values, "title", CortexOperationalLimits.MaximumTitleCharacters, out var title, out error) ||
            !ToolArgumentReader.RequiredContentString(values, "content", CortexOperationalLimits.MaximumContentCharacters, out var content, out error) ||
            !ToolArgumentReader.OptionalString(values, "assignee", CortexOperationalLimits.MaximumIdentifierCharacters, out var assignee, out error) ||
            !ToolArgumentReader.OptionalStringArray(values, "dependencies", CortexOperationalLimits.MaximumDependencies, CortexOperationalLimits.MaximumIdentifierCharacters, out var dependencies, out error) ||
            !Idempotency(values, out var key, out error))
            return Error("cortex_task_arguments_invalid", error!, "cortex_task_create");
        return Success(await runtime.CreateTaskAsync(ns!, id!, title!, content!, assignee, dependencies ?? [], key!, token).ConfigureAwait(false));
    }

    private static async ValueTask<JsonObject> ListTasksAsync(ICortexOperationalRuntime runtime, JsonElement values, CancellationToken token)
    {
        if (!Namespace(values, out var ns, out var error) ||
            !ToolArgumentReader.OptionalString(values, "afterTaskId", CortexOperationalLimits.MaximumIdentifierCharacters, out var after, out error) ||
            !ToolArgumentReader.OptionalInt32(values, "limit", 10, 1, CortexOperationalLimits.MaximumPageSize, out var limit, out error))
            return Error("cortex_task_arguments_invalid", error!, "cortex_task_list");
        return Success(await runtime.ListTasksAsync(ns!, after, limit, token).ConfigureAwait(false));
    }

    private static async ValueTask<JsonObject> GetTaskAsync(ICortexOperationalRuntime runtime, JsonElement values, CancellationToken token)
    {
        if (!Common(values, "taskId", out var ns, out var id, out var error))
            return Error("cortex_task_arguments_invalid", error!, "cortex_task_get");
        var task = await runtime.ReadTaskAsync(ns!, id!, token).ConfigureAwait(false);
        return task is null ? Error("cortex_task_not_found", "The task does not exist in the requested namespace.", "cortex_task_get") : Success(task);
    }

    private static async ValueTask<JsonObject> MutateTaskAsync(ICortexOperationalRuntime runtime, JsonElement values, string operation, CancellationToken token)
    {
        if (!Common(values, "taskId", out var ns, out var id, out var error) ||
            !ToolArgumentReader.RequiredInt64(values, "expectedRevision", 1, long.MaxValue, out var revision, out error) ||
            !Idempotency(values, out var key, out error) ||
            !ToolArgumentReader.OptionalString(values, "title", CortexOperationalLimits.MaximumTitleCharacters, out var title, out error) ||
            !ToolArgumentReader.OptionalContentString(values, "content", CortexOperationalLimits.MaximumContentCharacters, out var content, out error) ||
            !ToolArgumentReader.OptionalString(values, "assignee", CortexOperationalLimits.MaximumIdentifierCharacters, out var assignee, out error) ||
            !ToolArgumentReader.OptionalString(values, "claimedBy", CortexOperationalLimits.MaximumIdentifierCharacters, out var claimedBy, out error) ||
            !ToolArgumentReader.OptionalString(values, "reason", CortexOperationalLimits.MaximumReasonCharacters, out var reason, out error) ||
            !ToolArgumentReader.OptionalString(values, "dependencyId", CortexOperationalLimits.MaximumIdentifierCharacters, out var dependency, out error))
            return Error("cortex_task_arguments_invalid", error!, $"cortex_task_{operation}");
        return Success(await runtime.MutateTaskAsync(ns!, id!, revision, operation, title, content, assignee, claimedBy, reason, dependency, key!, token).ConfigureAwait(false));
    }

    private static async ValueTask<JsonObject> TaskHistoryAsync(ICortexOperationalRuntime runtime, JsonElement values, CancellationToken token)
    {
        if (!Common(values, "taskId", out var ns, out var id, out var error) ||
            !ToolArgumentReader.RequiredInt64(values, "firstRevision", 1, long.MaxValue, out var first, out error) ||
            !ToolArgumentReader.RequiredInt64(values, "lastRevision", 1, long.MaxValue, out var last, out error))
            return Error("cortex_task_arguments_invalid", error!, "cortex_task_history");
        var items = await runtime.ReadTaskHistoryAsync(ns!, id!, first, last, token).ConfigureAwait(false);
        return Success(new { Namespace = ns, Items = items });
    }

    private static async ValueTask<JsonObject> CreateKnowledgeAsync(ICortexOperationalRuntime runtime, JsonElement values, CancellationToken token)
    {
        if (!Common(values, "recordId", out var ns, out var id, out var error) ||
            !ToolArgumentReader.RequiredString(values, "title", CortexOperationalLimits.MaximumTitleCharacters, out var title, out error) ||
            !ToolArgumentReader.RequiredContentString(values, "content", CortexOperationalLimits.MaximumContentCharacters, out var content, out error) ||
            !ToolArgumentReader.OptionalStringArray(values, "tags", CortexOperationalLimits.MaximumTags, CortexOperationalLimits.MaximumIdentifierCharacters, out var tags, out error) ||
            !Idempotency(values, out var key, out error))
            return Error("cortex_knowledge_arguments_invalid", error!, "cortex_knowledge_create");
        return Success(await runtime.CreateKnowledgeAsync(ns!, id!, title!, content!, tags ?? [], key!, token).ConfigureAwait(false));
    }

    private static async ValueTask<JsonObject> SearchKnowledgeAsync(ICortexOperationalRuntime runtime, JsonElement values, CancellationToken token)
    {
        if (!Namespace(values, out var ns, out var error) ||
            !ToolArgumentReader.RequiredContentString(values, "text", CortexAgentRetentionLimits.MaximumSearchTextCharacters, out var text, out error) ||
            string.IsNullOrWhiteSpace(text) ||
            !ToolArgumentReader.OptionalInt32(values, "limit", 10, 1, CortexOperationalLimits.MaximumPageSize, out var limit, out error))
            return Error("cortex_knowledge_arguments_invalid", error ?? "Search text is required.", "cortex_knowledge_search");
        var items = await runtime.SearchKnowledgeAsync(ns!, text!, limit, token).ConfigureAwait(false);
        return Success(new { Namespace = ns, Items = items });
    }

    private static async ValueTask<JsonObject> ReadKnowledgeAsync(ICortexOperationalRuntime runtime, JsonElement values, CancellationToken token)
    {
        if (!Common(values, "recordId", out var ns, out var id, out var error))
            return Error("cortex_knowledge_arguments_invalid", error!, "cortex_knowledge_read");
        var record = await runtime.ReadKnowledgeAsync(ns!, id!, token).ConfigureAwait(false);
        return record is null ? Error("cortex_knowledge_not_found", "The record does not exist in the requested namespace.", "cortex_knowledge_read") : Success(record);
    }

    private static async ValueTask<JsonObject> MutateKnowledgeAsync(ICortexOperationalRuntime runtime, JsonElement values, string operation, CancellationToken token)
    {
        if (!Common(values, "recordId", out var ns, out var id, out var error) ||
            !ToolArgumentReader.RequiredInt64(values, "expectedRevision", 1, long.MaxValue, out var revision, out error) ||
            !Idempotency(values, out var key, out error) ||
            !ToolArgumentReader.OptionalString(values, "title", CortexOperationalLimits.MaximumTitleCharacters, out var title, out error) ||
            !ToolArgumentReader.OptionalContentString(values, "content", CortexOperationalLimits.MaximumContentCharacters, out var content, out error) ||
            !ToolArgumentReader.OptionalStringArray(values, "tags", CortexOperationalLimits.MaximumTags, CortexOperationalLimits.MaximumIdentifierCharacters, out var tags, out error) ||
            !ToolArgumentReader.OptionalString(values, "replacementRecordId", CortexOperationalLimits.MaximumIdentifierCharacters, out var replacement, out error))
            return Error("cortex_knowledge_arguments_invalid", error!, $"cortex_knowledge_{operation}");
        return Success(await runtime.MutateKnowledgeAsync(ns!, id!, revision, operation, title, content, tags, replacement, key!, token).ConfigureAwait(false));
    }

    private static bool Namespace(JsonElement values, out string? ns, out string? error) =>
        ToolArgumentReader.RequiredString(values, "namespace", CortexOperationalLimits.MaximumNamespaceCharacters, out ns, out error);

    private static bool Common(JsonElement values, string idName, out string? ns, out string? id, out string? error)
    {
        id = null;
        return Namespace(values, out ns, out error) &&
            ToolArgumentReader.RequiredString(values, idName, CortexOperationalLimits.MaximumIdentifierCharacters, out id, out error);
    }

    private static bool Idempotency(JsonElement values, out string? key, out string? error) =>
        ToolArgumentReader.RequiredString(values, "idempotencyKey", CortexOperationalLimits.MaximumIdentifierCharacters, out key, out error);

    private static IReadOnlySet<string> AllowedArguments(string name) => name switch
    {
        "cortex_task_create" => Set("namespace", "taskId", "title", "content", "assignee", "dependencies", "idempotencyKey"),
        "cortex_task_list" => Set("namespace", "afterTaskId", "limit"),
        "cortex_task_get" => Set("namespace", "taskId"),
        "cortex_task_history" => Set("namespace", "taskId", "firstRevision", "lastRevision"),
        "cortex_task_update" => Set("namespace", "taskId", "expectedRevision", "idempotencyKey", "title", "content"),
        "cortex_task_assign" => Set("namespace", "taskId", "expectedRevision", "idempotencyKey", "assignee"),
        "cortex_task_claim" => Set("namespace", "taskId", "expectedRevision", "idempotencyKey", "claimedBy"),
        "cortex_task_block" => Set("namespace", "taskId", "expectedRevision", "idempotencyKey", "reason"),
        "cortex_task_complete" or "cortex_task_reopen" or "cortex_task_archive" =>
            Set("namespace", "taskId", "expectedRevision", "idempotencyKey"),
        "cortex_task_dependency_add" or "cortex_task_dependency_remove" =>
            Set("namespace", "taskId", "expectedRevision", "idempotencyKey", "dependencyId"),
        "cortex_knowledge_create" => Set("namespace", "recordId", "title", "content", "tags", "idempotencyKey"),
        "cortex_knowledge_search" => Set("namespace", "text", "limit"),
        "cortex_knowledge_read" => Set("namespace", "recordId"),
        "cortex_knowledge_revise" => Set("namespace", "recordId", "expectedRevision", "idempotencyKey", "title", "content", "tags"),
        "cortex_knowledge_supersede" => Set("namespace", "recordId", "expectedRevision", "idempotencyKey", "replacementRecordId"),
        "cortex_knowledge_archive" => Set("namespace", "recordId", "expectedRevision", "idempotencyKey"),
        _ => Set()
    };

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);

    private static JsonObject Success(object value)
    {
        var structured = JsonSerializer.SerializeToNode(value, McpProtocol.JsonOptions) as JsonObject ??
            new JsonObject { ["value"] = JsonSerializer.SerializeToNode(value, McpProtocol.JsonOptions) };
        var text = structured.ToJsonString(McpProtocol.JsonOptions);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["structuredContent"] = structured.DeepClone(),
            ["isError"] = false
        };
    }

    private static JsonObject Error(string code, string message, string operation)
    {
        var structured = new JsonObject
        {
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["operation"] = operation }
        };
        var text = structured.ToJsonString(McpProtocol.JsonOptions);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["structuredContent"] = structured.DeepClone(),
            ["isError"] = true
        };
    }

    private static JsonObject Definition(string name, string title, string description, JsonObject schema, bool readOnly) => new()
    {
        ["name"] = name,
        ["title"] = title,
        ["description"] = description,
        ["inputSchema"] = schema,
        ["annotations"] = new JsonObject
        {
            ["title"] = title,
            ["readOnlyHint"] = readOnly,
            ["destructiveHint"] = false,
            ["idempotentHint"] = true,
            ["openWorldHint"] = false
        }
    };

    private static JsonObject Schema(string[] required, params (string Name, JsonNode Definition)[] properties)
    {
        var values = new JsonObject();
        values["namespace"] = StringSchema(CortexOperationalLimits.MaximumNamespaceCharacters);
        foreach (var property in properties)
            values[property.Name] = property.Definition;
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = values,
            ["required"] = new JsonArray(
                required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["additionalProperties"] = false
        };
    }

    private static JsonObject StringSchema(int maximum, int minimum = 1) => new() { ["type"] = "string", ["minLength"] = minimum, ["maxLength"] = maximum };
    private static JsonObject RevisionSchema() => new() { ["type"] = "integer", ["minimum"] = 1 };
    private static JsonObject StringArraySchema(int maximumItems) => new() { ["type"] = "array", ["maxItems"] = maximumItems, ["uniqueItems"] = true, ["items"] = StringSchema(CortexOperationalLimits.MaximumIdentifierCharacters) };
    private static JsonObject TaskCreateSchema() => Schema(["namespace", "taskId", "title", "content", "idempotencyKey"], ("taskId", StringSchema(128)), ("title", StringSchema(512)), ("content", StringSchema(CortexOperationalLimits.MaximumContentCharacters, 0)), ("assignee", StringSchema(128)), ("dependencies", StringArraySchema(CortexOperationalLimits.MaximumDependencies)), ("idempotencyKey", StringSchema(128)));
    private static JsonObject TaskListSchema() => Schema(["namespace"], ("afterTaskId", StringSchema(128)), ("limit", new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = CortexOperationalLimits.MaximumPageSize }));
    private static JsonObject TaskGetSchema() => Schema(["namespace", "taskId"], ("taskId", StringSchema(128)));
    private static JsonObject TaskMutationSchema(string operation)
    {
        var common = new (string Name, JsonNode Definition)[]
        {
            ("taskId", StringSchema(128)),
            ("expectedRevision", RevisionSchema()),
            ("idempotencyKey", StringSchema(128))
        };
        var specific = operation switch
        {
            "update" => new[] { ("title", (JsonNode)StringSchema(512)), ("content", (JsonNode)StringSchema(CortexOperationalLimits.MaximumContentCharacters, 0)) },
            "assign" => new[] { ("assignee", (JsonNode)StringSchema(128)) },
            "claim" => new[] { ("claimedBy", (JsonNode)StringSchema(128)) },
            "block" => new[] { ("reason", (JsonNode)StringSchema(CortexOperationalLimits.MaximumReasonCharacters)) },
            _ => Array.Empty<(string Name, JsonNode Definition)>()
        };
        var required = operation switch
        {
            "assign" => new[] { "namespace", "taskId", "expectedRevision", "idempotencyKey", "assignee" },
            "claim" => new[] { "namespace", "taskId", "expectedRevision", "idempotencyKey", "claimedBy" },
            "block" => new[] { "namespace", "taskId", "expectedRevision", "idempotencyKey", "reason" },
            _ => new[] { "namespace", "taskId", "expectedRevision", "idempotencyKey" }
        };
        return Schema(required, common.Concat(specific).ToArray());
    }
    private static JsonObject TaskDependencySchema() => Schema(["namespace", "taskId", "expectedRevision", "idempotencyKey", "dependencyId"], ("taskId", StringSchema(128)), ("expectedRevision", RevisionSchema()), ("idempotencyKey", StringSchema(128)), ("dependencyId", StringSchema(128)));
    private static JsonObject TaskHistorySchema() => Schema(["namespace", "taskId", "firstRevision", "lastRevision"], ("taskId", StringSchema(128)), ("firstRevision", RevisionSchema()), ("lastRevision", RevisionSchema()));
    private static JsonObject KnowledgeCreateSchema() => Schema(["namespace", "recordId", "title", "content", "idempotencyKey"], ("recordId", StringSchema(128)), ("title", StringSchema(512)), ("content", StringSchema(CortexOperationalLimits.MaximumContentCharacters, 0)), ("tags", StringArraySchema(CortexOperationalLimits.MaximumTags)), ("idempotencyKey", StringSchema(128)));
    private static JsonObject KnowledgeSearchSchema() => Schema(["namespace", "text"], ("text", StringSchema(CortexAgentRetentionLimits.MaximumSearchTextCharacters)), ("limit", new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = CortexOperationalLimits.MaximumPageSize }));
    private static JsonObject KnowledgeReadSchema() => Schema(["namespace", "recordId"], ("recordId", StringSchema(128)));
    private static JsonObject KnowledgeMutationSchema(string operation)
    {
        var common = new (string Name, JsonNode Definition)[]
        {
            ("recordId", StringSchema(128)),
            ("expectedRevision", RevisionSchema()),
            ("idempotencyKey", StringSchema(128))
        };
        var specific = operation switch
        {
            "revise" => new[] { ("title", (JsonNode)StringSchema(512)), ("content", (JsonNode)StringSchema(CortexOperationalLimits.MaximumContentCharacters, 0)), ("tags", (JsonNode)StringArraySchema(CortexOperationalLimits.MaximumTags)) },
            "supersede" => new[] { ("replacementRecordId", (JsonNode)StringSchema(128)) },
            _ => Array.Empty<(string Name, JsonNode Definition)>()
        };
        var required = operation == "supersede"
            ? new[] { "namespace", "recordId", "expectedRevision", "idempotencyKey", "replacementRecordId" }
            : new[] { "namespace", "recordId", "expectedRevision", "idempotencyKey" };
        return Schema(required, common.Concat(specific).ToArray());
    }
}
