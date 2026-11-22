using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.V2;

namespace Pegasus.Cortex.Mcp.V2;

public static class CortexV2McpContract
{
    public static readonly IReadOnlyList<string> ToolNames = Array.AsReadOnly(
    [
        "cortex_status",
        "cortex_namespaces",
        "cortex_start",
        "cortex_search",
        "cortex_read",
        "cortex_task",
        "cortex_knowledge",
        "cortex_finish"
    ]);

    public static JsonArray ListTools() =>
    [
        Tool("cortex_status", "Local status, including optional personal-profile availability when managed.", EmptySchema(), readOnly: true),
        Tool("cortex_namespaces", "List namespaces.", NamespacesSchema(), readOnly: true),
        Tool("cortex_start", "Bounded task brief in explicit spaces. Default compact; detailed adds full preview metadata. Unavailable personal profile omits knowledge.user; reports effective spaces.", StartSchema(), readOnly: true),
        Tool("cortex_search", "Compact search in explicit spaces; knowledge composes geometrically before exact reads. Unavailable personal profile omits knowledge.user; restart stale personal-sensitive cursors.", SearchSchema(), readOnly: true),
        Tool("cortex_read", "Read one or up to eight exact full records.", ReadSchema(), readOnly: true),
        Tool("cortex_task", "Manage tasks.", TaskSchema(), readOnly: false),
        Tool("cortex_knowledge", "Manage knowledge.", KnowledgeSchema(), readOnly: false),
        Tool("cortex_finish", "Commit finish.", FinishSchema(), readOnly: false)
    ];

    private static JsonObject Tool(
        string name,
        string description,
        JsonObject schema,
        bool readOnly) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema,
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = readOnly,
            ["destructiveHint"] = false,
            ["idempotentHint"] = true,
            ["openWorldHint"] = false
        }
    };

    private static JsonObject EmptySchema() => ObjectSchema(new JsonObject(), []);

    private static JsonObject NamespacesSchema() => ObjectSchema(new JsonObject
    {
        ["afterNamespace"] = Identifier(),
        ["limit"] = Integer(1, CortexV2Limits.MaximumPageSize, CortexV2Limits.DefaultPageSize),
        ["maxUtf8Bytes"] = CompactBudget()
    }, []);

    private static JsonObject StartSchema() => ObjectSchema(new JsonObject
    {
        ["namespaces"] = NamespaceArray(),
        ["intent"] = String(CortexV2Limits.MaximumIntentCharacters),
        ["taskId"] = Identifier(),
        ["phaseId"] = Identifier(),
        ["knownContextCursor"] = String(CortexV2Limits.MaximumContextCursorCharacters),
        ["maxUtf8Bytes"] = StartBudget(),
        ["format"] = PreviewFormat()
    }, ["namespaces", "intent"]);

    private static JsonObject SearchSchema() => ObjectSchema(new JsonObject
    {
        ["namespaces"] = NamespaceArray(),
        ["text"] = String(CortexV2Limits.MaximumContentCharacters),
        ["kinds"] = EnumArray<CortexKnowledgeKindV2>(8),
        ["afterCursor"] = String(CortexV2Limits.MaximumContextCursorCharacters),
        ["limit"] = Integer(1, CortexV2Limits.MaximumPageSize, CortexV2Limits.DefaultPageSize),
        ["maxUtf8Bytes"] = CompactBudget(),
        ["format"] = PreviewFormat()
    }, ["namespaces", "text"]);

    private static JsonObject ReadSchema()
    {
        var schema = ObjectSchema(new JsonObject
        {
            ["namespace"] = Namespace(),
            ["recordType"] = Enum<CortexRecordTypeV2>(),
            ["recordId"] = Identifier(),
            ["revision"] = Integer(1, long.MaxValue),
            ["records"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["maxItems"] = CortexV2Limits.MaximumBatchReadRecords,
                ["uniqueItems"] = true,
                ["items"] = RecordCoordinateSchema()
            },
            ["maxUtf8Bytes"] = ExactBudget()
        }, []);
        schema["oneOf"] = new JsonArray(
            new JsonObject
            {
                ["required"] = new JsonArray("namespace", "recordType", "recordId")
            },
            new JsonObject
            {
                ["required"] = new JsonArray("records")
            });
        return schema;
    }

    private static JsonObject RecordCoordinateSchema() => ObjectSchema(new JsonObject
    {
        ["namespace"] = Namespace(),
        ["recordType"] = Enum<CortexRecordTypeV2>(),
        ["recordId"] = Identifier(),
        ["revision"] = Integer(1, long.MaxValue)
    }, ["namespace", "recordType", "recordId"]);

    private static JsonObject TaskSchema() => ObjectSchema(new JsonObject
    {
        ["namespace"] = Namespace(),
        ["action"] = Enum<CortexTaskActionV2>(),
        ["taskId"] = Identifier(),
        ["expectedRevision"] = Integer(1, long.MaxValue),
        ["idempotencyKey"] = Identifier(),
        ["title"] = String(CortexV2Limits.MaximumTitleCharacters),
        ["content"] = String(CortexV2Limits.MaximumContentCharacters, 0),
        ["assignee"] = DeclaredParticipant(),
        ["claimedBy"] = DeclaredParticipant(),
        ["reason"] = String(CortexV2Limits.MaximumReasonCharacters),
        ["dependencyId"] = Identifier(),
        ["dependencies"] = IdentifierArray(CortexV2Limits.MaximumDependencies),
        ["firstRevision"] = Integer(1, long.MaxValue),
        ["lastRevision"] = Integer(1, long.MaxValue),
        ["afterTaskId"] = Identifier(),
        ["limit"] = Integer(1, CortexV2Limits.MaximumPageSize, CortexV2Limits.DefaultPageSize),
        ["maxUtf8Bytes"] = CompactBudget(),
        ["phaseId"] = Identifier(),
        ["checkpointSubject"] = String(CortexV2Limits.MaximumCheckpointSubjectCharacters)
    }, ["namespace", "action"]);

    private static JsonObject KnowledgeSchema() => ObjectSchema(new JsonObject
    {
        ["namespace"] = Namespace(),
        ["action"] = Enum<CortexKnowledgeActionV2>(),
        ["recordId"] = Identifier(),
        ["expectedRevision"] = Integer(1, long.MaxValue),
        ["idempotencyKey"] = Identifier(),
        ["kind"] = Enum<CortexKnowledgeKindV2>(),
        ["tier"] = Enum<CortexKnowledgeTierV2>(),
        ["title"] = String(CortexV2Limits.MaximumTitleCharacters),
        ["content"] = String(CortexV2Limits.MaximumContentCharacters, 0),
        ["tags"] = IdentifierArray(CortexV2Limits.MaximumTags),
        ["provenance"] = ProvenanceSchema(),
        ["replacementRecordId"] = Identifier(),
        ["afterRecordId"] = Identifier(),
        ["limit"] = Integer(1, CortexV2Limits.MaximumConsolidationHeads, CortexV2Limits.DefaultPageSize),
        ["maxUtf8Bytes"] = CompactBudget()
    }, ["namespace", "action"]);

    private static JsonObject FinishSchema() => ObjectSchema(new JsonObject
    {
        ["namespace"] = Namespace(),
        ["taskId"] = Identifier(),
        ["expectedRevision"] = Integer(1, long.MaxValue),
        ["taskAction"] = Enum<CortexFinishTaskActionV2>(),
        ["taskTitle"] = String(CortexV2Limits.MaximumTitleCharacters),
        ["taskContent"] = String(CortexV2Limits.MaximumContentCharacters, 0),
        ["blockedReason"] = String(CortexV2Limits.MaximumReasonCharacters),
        ["checkpoint"] = String(CortexV2Limits.MaximumCheckpointCharacters),
        ["evidence"] = String(CortexV2Limits.MaximumEvidenceCharacters),
        ["handoff"] = String(CortexV2Limits.MaximumHandoffCharacters),
        ["knowledge"] = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = CortexV2Limits.MaximumKnowledgeWritesPerFinish,
            ["items"] = FinishKnowledgeSchema()
        },
        ["idempotencyKey"] = Identifier(),
        ["maxUtf8Bytes"] = CompactBudget(CortexV2Limits.MaximumCompactResponseUtf8Bytes),
        ["phaseId"] = Identifier(),
        ["checkpointSubject"] = String(CortexV2Limits.MaximumCheckpointSubjectCharacters)
    },
    [
        "namespace", "taskId", "expectedRevision", "taskAction",
        "checkpoint", "evidence", "knowledge", "idempotencyKey"
    ]);

    private static JsonObject FinishKnowledgeSchema() => ObjectSchema(new JsonObject
    {
        ["recordId"] = Identifier(),
        ["expectedRevision"] = Integer(1, long.MaxValue),
        ["kind"] = Enum<CortexKnowledgeKindV2>(),
        ["tier"] = Enum<CortexKnowledgeTierV2>(),
        ["title"] = String(CortexV2Limits.MaximumTitleCharacters),
        ["content"] = String(CortexV2Limits.MaximumContentCharacters, 0),
        ["tags"] = IdentifierArray(CortexV2Limits.MaximumTags),
        ["provenance"] = ProvenanceSchema(),
        ["idempotencyKey"] = Identifier()
    }, ["recordId", "kind", "tier", "title", "content", "tags", "provenance", "idempotencyKey"]);

    private static JsonObject ProvenanceSchema() => ObjectSchema(new JsonObject
    {
        ["source"] = DeclaredSource(),
        ["sourceId"] = DeclaredParticipant(),
        ["evidence"] = String(CortexV2Limits.MaximumEvidenceCharacters)
    }, ["source"]);

    private static JsonObject DeclaredParticipant()
    {
        var value = Identifier();
        value["description"] = "Caller-declared label; host source separate.";
        return value;
    }

    private static JsonObject DeclaredSource()
    {
        var value = Enum<CortexProvenanceSourceV2>();
        value["description"] = "Caller-declared category; host source separate.";
        return value;
    }

    private static JsonObject ObjectSchema(JsonObject properties, IEnumerable<string> required) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = new JsonArray(required.Select(value => JsonValue.Create(value)).ToArray()),
        ["additionalProperties"] = false
    };

    private static JsonObject String(int maximum, int minimum = 1) => new()
    {
        ["type"] = "string",
        ["minLength"] = minimum,
        ["maxLength"] = maximum
    };

    private static JsonObject Identifier() => String(CortexV2Limits.MaximumIdentifierCharacters);

    private static JsonObject Namespace() => String(CortexV2Limits.MaximumNamespaceCharacters);

    private static JsonObject IdentifierArray(int maximum) => new()
    {
        ["type"] = "array",
        ["maxItems"] = maximum,
        ["uniqueItems"] = true,
        ["items"] = Identifier()
    };

    private static JsonObject NamespaceArray() => new()
    {
        ["type"] = "array",
        ["minItems"] = 1,
        ["maxItems"] = CortexV2Limits.MaximumNamespaces,
        ["uniqueItems"] = true,
        ["items"] = Namespace()
    };

    private static JsonObject Integer(long minimum, long maximum, int? defaultValue = null)
    {
        var value = new JsonObject
        {
            ["type"] = "integer",
            ["minimum"] = minimum,
            ["maximum"] = maximum
        };
        if (defaultValue is not null)
            value["default"] = defaultValue.Value;
        return value;
    }

    private static JsonObject CompactBudget(
        int defaultValue = CortexV2Limits.DefaultResponseUtf8Bytes) =>
        Integer(
            CortexV2Limits.MinimumResponseUtf8Bytes,
            CortexV2Limits.MaximumCompactResponseUtf8Bytes,
            defaultValue);

    private static JsonObject StartBudget() =>
        Integer(
            CortexV2Limits.MinimumResponseUtf8Bytes,
            CortexV2Limits.MaximumStartResponseUtf8Bytes,
            CortexV2Limits.DefaultStartResponseUtf8Bytes);

    private static JsonObject ExactBudget() =>
        Integer(
            CortexV2Limits.MinimumResponseUtf8Bytes,
            CortexV2Limits.MaximumResponseUtf8Bytes,
            CortexV2Limits.MaximumResponseUtf8Bytes);

    private static JsonObject PreviewFormat()
    {
        var value = Enum<CortexResponseFormatV2>();
        value["default"] = "compact";
        return value;
    }

    private static JsonObject Enum<T>() where T : struct, Enum => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(
            System.Enum.GetNames<T>()
                .Select(name => JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(name)))
                .ToArray())
    };

    private static JsonObject EnumArray<T>(int maximum) where T : struct, Enum => new()
    {
        ["type"] = "array",
        ["maxItems"] = maximum,
        ["uniqueItems"] = true,
        ["items"] = Enum<T>()
    };
}
