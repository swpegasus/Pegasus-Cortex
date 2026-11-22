using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.Learning;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Core.Hybrid;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Core.Retention;

namespace Pegasus.Cortex.Mcp.Knowledge;

internal static class CortexMcpContract
{
    public const int MaximumSearchResults = 10;
    public const int MaximumSearchTextCharacters = 512;
    public const int MaximumFilterCharacters = 128;
    public const int MaximumIdentifierCharacters = 128;
    public const int MaximumExcerptCharacters = 2048;
    public const int MaximumResourceRecords = 64;
    public const int MaximumProjectionCharacters = 16;
    public const int MaximumSummaryCharacters = 2048;
    public const int MaximumTitleCharacters = 512;
    public const int MaximumRetainedContentCharacters = 32 * 1024;
    public const int MaximumProposalEvidenceItems =
        CortexProposalEvidencePolicy.MaximumItems;
    public const int MaximumReviewReasonCharacters = 2048;
    public const int MaximumHydratedSearchResults = 3;
    public const int MaximumHydratedContentUtf8Bytes = 64 * 1024;
    public const int MaximumHydratedToolResultBytes = 224 * 1024;
    public const string FullProjection = "full";
    public const string CompactProjection = "compact";
    public const string ExactSearchMode = "exact";
    public const string BroadSearchMode = "broad";

    public static IReadOnlySet<string> NoArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal);

    public static IReadOnlySet<string> SearchArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "text",
            "taxonomy",
            "productId",
            "componentId",
            "limit",
            "mode",
            "projection",
            "includeContent",
            "includeStale",
            "includeBlockedOrUnverified",
            "projectLinkId"
        };

    public static IReadOnlySet<string> ReadArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "id",
            "projection",
            "includeStale",
            "includeBlockedOrUnverified",
            "projectLinkId",
            "revisionId"
        };

    public static IReadOnlySet<string> ProjectBriefArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "projectLinkId",
            "knownContextSetId",
            "knownAuthoritySetHash",
            "maxUtf8Bytes"
        };

    public static IReadOnlySet<string> SubmitRetainedAssertionArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "projectLinkId",
            "projectNamespace",
            "assertion"
        };

    public static IReadOnlySet<string> SubmitRetainedReceiptArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "projectLinkId",
            "projectId",
            "projectNamespace",
            "assertion"
        };

    public static IReadOnlySet<string> ReadRetainedAssertionArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "projectLinkId",
            "projectId",
            "projectNamespace",
            "kind",
            "documentId",
            "revision",
            "contentHash"
        };

    public static IReadOnlySet<string> CorrelateArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "operation",
            "projectId",
            "storeId",
            "storeEpoch",
            "contractVersion",
            "libraryAccess",
            "correlationHash"
        };

    public static IReadOnlySet<string> SubmitProposalArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "projectLinkId",
            "idempotencyKey",
            "feedbackKind",
            "title",
            "summary",
            "content",
            "taxonomy",
            "keywords",
            "applicability",
            "evidenceReferences",
            "occurredAtUtc"
        };

    public static IReadOnlySet<string> ProposalApplicabilityArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "productId",
            "componentId"
        };

    public static IReadOnlySet<string> ProposalEvidenceArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "evidenceId",
            "reference",
            "mediaType",
            "sha256",
            "verification"
        };

    public static IReadOnlySet<string> ListProposalArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "projectLinkId",
            "afterProposalId",
            "limit",
            "includeContent"
        };

    public static IReadOnlySet<string> ReviewProposalArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "projectLinkId",
            "proposalId",
            "state",
            "reason",
            "reviewedAtUtc",
            "userConfirmed"
        };

    public static IReadOnlySet<string> PublicationChangeArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "projectLinkId",
            "action",
            "targetRecordId",
            "expectedHeadRevisionId",
            "expectedHeadGeneration",
            "replacement",
            "reasonCode",
            "reason",
            "occurredAtUtc"
        };

    public static IReadOnlySet<string> PublicationReplacementArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "recordId",
            "revisionId",
            "contentHash"
        };

    public static IReadOnlySet<string> SubmitAuditArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "projectLinkId",
            "proposalId",
            "recommendation",
            "rationale",
            "reviewGrantId",
            "assessedAtUtc"
        };

    public static IReadOnlySet<string> ManageReviewGrantArguments { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "operation",
            "projectLinkId",
            "reviewGrantId",
            "delegateAuthorityId",
            "delegateSubjectId",
            "feedbackKinds",
            "maximumUses",
            "expiresAtUtc",
            "occurredAtUtc",
            "reason",
            "userConfirmed"
        };

    public static JsonArray ListTools(CortexMcpRuntimeComposition? runtime = null)
    {
        var tools = new JsonArray(
        ToolDefinition(
            "cortex_discover",
            "Discover Cortex capabilities",
            "Returns this authenticated principal's capabilities and bounds.",
            EmptyObjectSchema()),
        ToolDefinition(
            "cortex_status",
            "Read Cortex status",
            "Returns deterministic bundle health.",
            EmptyObjectSchema()));
        if (runtime is null || runtime.HasCapability(CortexMcpRuntimeCapability.Read))
        {
            tools.Add(ToolDefinition(
                "cortex_search",
                "Search Cortex knowledge",
                "Bounded lexical and metadata search. Exact conjunctive search is the wire default; use mode broad for normal natural-language questions. Without projectLinkId it searches the Default Library plus any advertised collective provider. Supplying projectLinkId requires features.projectLinkedSearch; reserved libraries require their exact authorized link. For exact content in one call, use projection compact, includeContent true, and limit at most 3. Other freshness states require opt-in.",
                SearchSchema()));
            tools.Add(ToolDefinition(
                "cortex_read",
                "Read one Cortex record",
                "Reads one exact record by known ID or when search omitted content. Other freshness states require opt-in.",
                ReadSchema()));
        }
        if (runtime?.CanUseProjectBrief == true)
        {
            tools.Add(ToolDefinition(
                "cortex_project_brief",
                "Read one project brief",
                "Returns one passive, hash-bound current-task snapshot, immediate-predecessor delta, or unchanged marker for an exact authorized projectLinkId. Every request must pass the verified local authority-set probe as knownAuthoritySetHash; missing or mismatched authority requires file fallback. It never crawls files, claims tasks, schedules work, or grants approval; use cortex_read_retained_record for retained:// locators and cortex_read for knowledge-record references.",
                ProjectBriefSchema()));
        }
        if (runtime?.RetainedAssertionReader is not null &&
            runtime.HasCapability(CortexMcpRuntimeCapability.RetainedRead))
        {
            tools.Add(ToolDefinition(
                "cortex_read_retained_record",
                "Read one exact retained assertion",
                "Reads one exact hash-matched retained task snapshot, work packet, transfer receipt, or project brief revision for a separately read-allowlisted local project. Returned assertions are passive, untrusted, and non-authoritative.",
                ReadRetainedAssertionSchema()));
        }
        if (runtime?.RetainedAssertionWriter is not null &&
            runtime.HasCapability(CortexMcpRuntimeCapability.RetainedWrite))
        {
            tools.Add(ToolDefinition(
                "cortex_submit_task_snapshot",
                "Retain one task snapshot",
                $"Validates and append-attempts one typed {AgentTaskSnapshotContractIds.Snapshot} assertion for a separately write-allowlisted local project. It never schedules, claims, approves, or executes work.",
                SubmitRetainedAssertionSchema(
                    AgentTaskSnapshotContractIds.Snapshot,
                    AgentTaskSnapshotContractLimits.MaximumSerializedSnapshotBytes),
                readOnly: false));
            tools.Add(ToolDefinition(
                "cortex_submit_work_packet",
                "Retain one work packet",
                $"Validates and append-attempts one typed {AgentWorkPacketContractIds.Packet} assertion for a separately write-allowlisted local project. It never hands off, claims, approves, or executes work.",
                SubmitRetainedAssertionSchema(
                    AgentWorkPacketContractIds.Packet,
                    AgentWorkPacketContractLimits.MaximumSerializedPacketBytes),
                readOnly: false));
            tools.Add(ToolDefinition(
                "cortex_submit_work_packet_handoff_receipt",
                "Retain one work-packet handoff receipt",
                $"Validates and append-attempts one typed {AgentWorkPacketContractIds.HandoffReceipt} passive caller assertion about exact retained packet bytes offered to an intended owner. Its hash proves linkage, not delivery, and grants no assignment, claim, approval, scheduling, or execution authority.",
                SubmitRetainedReceiptSchema(
                    AgentWorkPacketContractIds.HandoffReceipt),
                readOnly: false));
            tools.Add(ToolDefinition(
                "cortex_submit_work_packet_acceptance_receipt",
                "Retain one work-packet acceptance receipt",
                $"Validates and append-attempts one typed {AgentWorkPacketContractIds.AcceptanceReceipt} passive caller assertion naming acceptance of an exact retained handoff. Its hash proves linkage, not authenticated observation, and grants no assignment, claim, approval, scheduling, or execution authority.",
                SubmitRetainedReceiptSchema(
                    AgentWorkPacketContractIds.AcceptanceReceipt),
                readOnly: false));
        }
        if (runtime?.ProjectCorrelation is not null &&
            runtime.HasCapability(CortexMcpRuntimeCapability.Correlate))
        {
            tools.Add(ToolDefinition(
                "cortex_correlate_project",
                "Correlate a project with Cortex",
                "Prepares or confirms one idempotent, non-authorizing project link for hybrid local-file and retained MCP knowledge.",
                CorrelateSchema(),
                readOnly: false));
        }
        if (runtime?.ProposalSubmission is not null &&
            runtime.HasCapability(CortexMcpRuntimeCapability.Propose))
        {
            tools.Add(ToolDefinition(
                "cortex_submit_proposal",
                "Submit bounded project knowledge",
                "Stores one bounded project-scoped proposal. When server policy explicitly opts in, Cortex deterministically derives a generalized collective projection of safe quality/success content, publishes it at reduced provisional confidence, and flags it for later independent review. Detectable secrets are replaced before storage; private reasoning fails closed; all other proposals remain pending.",
                SubmitProposalSchema(),
                readOnly: false));
        }
        if (runtime?.ProposalQuery is not null &&
            (runtime.HasCapability(CortexMcpRuntimeCapability.Read) ||
             runtime.HasCapability(CortexMcpRuntimeCapability.Review) ||
             runtime.HasCapability(CortexMcpRuntimeCapability.Audit)))
        {
            tools.Add(ToolDefinition(
                "cortex_list_proposals",
                "List retained-knowledge proposals",
                "Returns bounded proposal metadata, latest audit state, and optionally exact sanitized content for one correlated project.",
                ListProposalSchema()));
        }
        if (runtime?.PublicationChange is not null &&
            runtime.HasCapability(CortexMcpRuntimeCapability.Propose))
        {
            tools.Add(ToolDefinition(
                "cortex_change_publication",
                "Supersede or withdraw authored knowledge",
                "Appends one author-owned supersede or withdraw event for an exact published record. The authenticated authority-qualified subject must own the original publication and must supply the expected head revision/generation. Normal search then excludes the old head; immutable history remains. This notebook operation grants no task or execution authority.",
                PublicationChangeSchema(),
                readOnly: false));
        }
        if (runtime?.ProposalReview is not null &&
            runtime.HasCapability(CortexMcpRuntimeCapability.Review))
        {
            tools.Add(ToolDefinition(
                "cortex_review_proposal",
                "Review a retained-knowledge proposal",
                "Approves or rejects one proposal after explicit user review. Approval atomically makes the sanitized record searchable.",
                ReviewProposalSchema(),
                readOnly: false));
        }
        if (runtime?.ProposalAudit is not null &&
            runtime.HasCapability(CortexMcpRuntimeCapability.Audit))
        {
            tools.Add(ToolDefinition(
                "cortex_submit_audit",
                "Submit an independent audit assessment",
                "Appends one advisory assessment. A safe collective success or quality proposal is auto-approved only when a matching active bounded review grant is atomically consumed; rejection remains pending for human review.",
                SubmitAuditSchema(),
                readOnly: false));
        }
        if (runtime?.ReviewGrant is not null &&
            runtime.HasCapability(CortexMcpRuntimeCapability.DelegateReview))
        {
            tools.Add(ToolDefinition(
                "cortex_manage_review_grant",
                "Manage a bounded audit review grant",
                "Issues or revokes a collective-only, time-limited, use-limited grant for safe success and quality proposals after explicit user confirmation.",
                ManageReviewGrantSchema(),
                readOnly: false));
        }
        foreach (var tool in CortexOperationalMcpTools.ListTools(runtime))
            tools.Add(tool);
        return tools;
    }

    public static JsonObject ResourceDefinition(string uri, string name, string description) => new()
    {
        ["uri"] = uri,
        ["name"] = name,
        ["description"] = description,
        ["mimeType"] = "application/json"
    };

    public static JsonObject ResourceTemplateDefinition(
        string uriTemplate,
        string name,
        string description) => new()
    {
        ["uriTemplate"] = uriTemplate,
        ["name"] = name,
        ["description"] = description,
        ["mimeType"] = "application/json"
    };

    private static JsonObject ToolDefinition(
        string name,
        string title,
        string description,
        JsonObject inputSchema,
        bool readOnly = true) => new()
    {
        ["name"] = name,
        ["title"] = title,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
        ["annotations"] = new JsonObject
        {
            ["title"] = title,
            ["readOnlyHint"] = readOnly,
            ["destructiveHint"] = false,
            ["idempotentHint"] = true,
            ["openWorldHint"] = false
        }
    };

    private static JsonObject EmptyObjectSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false
    };

    private static JsonObject SearchSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["text"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = MaximumSearchTextCharacters },
            ["taxonomy"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaximumFilterCharacters },
            ["productId"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaximumFilterCharacters },
            ["componentId"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaximumFilterCharacters },
            ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaximumSearchResults, ["default"] = 5 },
            ["mode"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(ExactSearchMode, BroadSearchMode),
                ["default"] = ExactSearchMode,
                ["description"] = "Exact preserves conjunctive matching; broad opts into minimum-match lexical recall."
            },
            ["projection"] = ProjectionSchema(),
            ["includeContent"] = new JsonObject
            {
                ["type"] = "boolean",
                ["default"] = false,
                ["description"] =
                    $"With projection '{CompactProjection}' and limit at most {MaximumHydratedSearchResults}, returns exact content in this response to avoid follow-up reads."
            },
            ["includeStale"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
            ["includeBlockedOrUnverified"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
            ["projectLinkId"] = IdentifierSchema("plink")
        },
        ["required"] = new JsonArray("text"),
        ["additionalProperties"] = false
    };

    private static JsonObject ReadSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["id"] = new JsonObject
            {
                ["type"] = "string",
                ["maxLength"] = MaximumIdentifierCharacters,
                ["pattern"] = "^(krec|krev)_[0-9a-f]{64}$"
            },
            ["projection"] = ProjectionSchema(),
            ["includeStale"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
            ["includeBlockedOrUnverified"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
            ["projectLinkId"] = IdentifierSchema("plink"),
            ["revisionId"] = KnowledgeRevisionSchema()
        },
        ["required"] = new JsonArray("id"),
        ["additionalProperties"] = false
    };

    private static JsonObject ProjectionSchema() => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(FullProjection, CompactProjection),
        ["default"] = FullProjection
    };

    private static JsonObject ProjectBriefSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["projectLinkId"] = IdentifierSchema("plink"),
            ["knownContextSetId"] = IdentifierSchema("ctx"),
            ["knownAuthoritySetHash"] = Sha256Schema(),
            ["maxUtf8Bytes"] = new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = CortexProjectBriefContract.MinimumMaximumUtf8Bytes,
                ["maximum"] = CortexProjectBriefContract.MaximumUtf8Bytes,
                ["default"] = CortexProjectBriefContract.DefaultMaximumUtf8Bytes
            }
        },
        ["required"] = new JsonArray("projectLinkId", "knownAuthoritySetHash"),
        ["additionalProperties"] = false
    };

    private static JsonObject SubmitRetainedAssertionSchema(
        string contractId,
        int maximumCanonicalBytes) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["projectLinkId"] = IdentifierSchema("plink"),
            ["projectNamespace"] = IdentifierSchema(),
            ["assertion"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] =
                    $"One typed {contractId} object whose canonical form is at most {maximumCanonicalBytes} bytes."
            }
        },
        ["required"] = new JsonArray(
            "projectLinkId",
            "projectNamespace",
            "assertion"),
        ["additionalProperties"] = false
    };

    private static JsonObject SubmitRetainedReceiptSchema(string contractId) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["projectLinkId"] = IdentifierSchema("plink"),
            ["projectId"] = IdentifierSchema(),
            ["projectNamespace"] = IdentifierSchema(),
            ["assertion"] = new JsonObject
            {
                ["type"] = "object",
                ["description"] =
                    $"One typed {contractId} object whose canonical form is at most {AgentWorkPacketContractLimits.MaximumSerializedPacketBytes} bytes."
            }
        },
        ["required"] = new JsonArray(
            "projectLinkId",
            "projectId",
            "projectNamespace",
            "assertion"),
        ["additionalProperties"] = false
    };

    private static JsonObject ReadRetainedAssertionSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["projectLinkId"] = IdentifierSchema("plink"),
            ["projectId"] = IdentifierSchema(),
            ["projectNamespace"] = IdentifierSchema(),
            ["kind"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    CortexRetainedDocumentKinds.TaskSnapshot,
                    CortexRetainedDocumentKinds.WorkPacket,
                    CortexRetainedDocumentKinds.WorkPacketHandoffReceipt,
                    CortexRetainedDocumentKinds.WorkPacketAcceptanceReceipt,
                    CortexRetainedDocumentKinds.ProjectBrief)
            },
            ["documentId"] = IdentifierSchema(),
            ["revision"] = new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = 1,
                ["maximum"] = 9_007_199_254_740_991
            },
            ["contentHash"] = Sha256Schema()
        },
        ["required"] = new JsonArray(
            "projectLinkId",
            "projectId",
            "projectNamespace",
            "kind",
            "documentId",
            "revision",
            "contentHash"),
        ["additionalProperties"] = false
    };

    private static JsonObject CorrelateSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["operation"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("prepare", "confirm")
            },
            ["projectId"] = IdentifierSchema(),
            ["storeId"] = IdentifierSchema(),
            ["storeEpoch"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
            ["contractVersion"] = IdentifierSchema(),
            ["libraryAccess"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    CortexProjectCorrelationContract.CollectiveLibraryAccess,
                    CortexProjectCorrelationContract.ReservedLibraryAccess),
                ["default"] = CortexProjectCorrelationContract.CollectiveLibraryAccess,
                ["description"] =
                    "Collective libraries participate in general search. Reserved libraries require their exact projectLinkId."
            },
            ["correlationHash"] = Sha256Schema()
        },
        ["required"] = new JsonArray(
            "operation",
            "projectId",
            "storeId",
            "storeEpoch",
            "contractVersion"),
        ["additionalProperties"] = false
    };

    private static JsonObject SubmitProposalSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["projectLinkId"] = IdentifierSchema("plink"),
            ["idempotencyKey"] = IdentifierSchema(),
            ["feedbackKind"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    "correction",
                    "failure",
                    "preference",
                    "quality",
                    "regression",
                    "safety",
                    "success")
            },
            ["title"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 1,
                ["maxLength"] = MaximumTitleCharacters
            },
            ["summary"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 1,
                ["maxLength"] = MaximumSummaryCharacters
            },
            ["content"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 1,
                ["maxLength"] = MaximumRetainedContentCharacters
            },
            ["taxonomy"] = IdentifierSchema(),
            ["keywords"] = new JsonObject
            {
                ["type"] = "array",
                ["maxItems"] =
                    LearningFeedbackContractLimits.MaximumProposalKeywords,
                ["uniqueItems"] = true,
                ["items"] = new JsonObject
                {
                    ["type"] = "string",
                    ["minLength"] = 1,
                    ["maxLength"] = LearningFeedbackContractLimits
                        .MaximumProposalKeywordCharacters
                },
                ["description"] =
                    "Optional bounded search terms included in proposal identity and retained knowledge."
            },
            ["applicability"] = new JsonObject
            {
                ["type"] = "array",
                ["maxItems"] = LearningFeedbackContractLimits
                    .MaximumProposalApplicabilityItems,
                ["uniqueItems"] = true,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["productId"] = IdentifierSchema(),
                        ["componentId"] = IdentifierSchema()
                    },
                    ["required"] = new JsonArray("productId", "componentId"),
                    ["additionalProperties"] = false
                },
                ["description"] =
                    "Optional bounded product/component coordinates included in proposal identity and retained knowledge."
            },
            ["evidenceReferences"] = new JsonObject
            {
                ["type"] = "array",
                ["maxItems"] = MaximumProposalEvidenceItems,
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["evidenceId"] = IdentifierSchema(),
                        ["reference"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["minLength"] = 1,
                            ["maxLength"] =
                                LearningFeedbackContractLimits
                                    .MaximumEvidenceReferenceCharacters
                        },
                        ["mediaType"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["minLength"] = 1,
                            ["maxLength"] = MaximumIdentifierCharacters
                        },
                        ["sha256"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["pattern"] = "^[0-9a-f]{64}$"
                        },
                        ["verification"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("verified")
                        }
                    },
                    ["required"] = new JsonArray(
                        "evidenceId",
                        "reference",
                        "mediaType",
                        "sha256",
                        "verification"),
                    ["additionalProperties"] = false
                },
                ["description"] =
                    "Optional bounded caller-reported verified evidence references. Cortex retains these coordinates and hashes but does not resolve or prove the referenced artifact in this operation."
            },
            ["occurredAtUtc"] = UtcTimestampSchema()
        },
        ["required"] = new JsonArray(
            "projectLinkId",
            "idempotencyKey",
            "feedbackKind",
            "title",
            "summary",
            "content",
            "occurredAtUtc"),
        ["additionalProperties"] = false
    };

    private static JsonObject ListProposalSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["projectLinkId"] = IdentifierSchema("plink"),
            ["afterProposalId"] = IdentifierSchema(),
            ["limit"] = new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = 1,
                ["maximum"] = 100,
                ["default"] = 25
            },
            ["includeContent"] = new JsonObject
            {
                ["type"] = "boolean",
                ["default"] = false,
                ["description"] =
                    $"Returns exact sanitized title and content. When true, limit is at most {MaximumHydratedSearchResults}."
            }
        },
        ["required"] = new JsonArray("projectLinkId"),
        ["additionalProperties"] = false
    };

    private static JsonObject ReviewProposalSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["projectLinkId"] = IdentifierSchema("plink"),
            ["proposalId"] = IdentifierSchema("proposal"),
            ["state"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("approved", "rejected")
            },
            ["reason"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 1,
                ["maxLength"] = MaximumReviewReasonCharacters
            },
            ["reviewedAtUtc"] = UtcTimestampSchema(),
            ["userConfirmed"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Must be true only after explicit user review of this proposal."
            }
        },
        ["required"] = new JsonArray(
            "projectLinkId",
            "proposalId",
            "state",
            "reason",
            "reviewedAtUtc",
            "userConfirmed"),
        ["additionalProperties"] = false
    };

    private static JsonObject PublicationChangeSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["projectLinkId"] = IdentifierSchema("plink"),
            ["action"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    LearningFeedbackPublicationChangeActions.Supersede,
                    LearningFeedbackPublicationChangeActions.Withdraw)
            },
            ["targetRecordId"] = IdentifierSchema("krec"),
            ["expectedHeadRevisionId"] = KnowledgeRevisionSchema(),
            ["expectedHeadGeneration"] = new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = 1,
                ["maximum"] = 1,
                ["description"] =
                    "Publication-change v1 operates only on an active generation-one published head. A successful change retires that head and preserves its immutable history."
            },
            ["replacement"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["recordId"] = IdentifierSchema("krec"),
                    ["revisionId"] = KnowledgeRevisionSchema(),
                    ["contentHash"] = Sha256Schema()
                },
                ["required"] = new JsonArray(
                    "recordId",
                    "revisionId",
                    "contentHash"),
                ["additionalProperties"] = false
            },
            ["reasonCode"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    LearningFeedbackPublicationChangeReasonCodes.Corrected,
                    LearningFeedbackPublicationChangeReasonCodes.Obsolete,
                    LearningFeedbackPublicationChangeReasonCodes.Duplicate,
                    LearningFeedbackPublicationChangeReasonCodes.UnsafeContent,
                    LearningFeedbackPublicationChangeReasonCodes.InvalidContent)
            },
            ["reason"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 1,
                ["maxLength"] =
                    LearningFeedbackPublicationChangeLimits.MaximumReasonCharacters
            },
            ["occurredAtUtc"] = UtcTimestampSchema()
        },
        ["required"] = new JsonArray(
            "projectLinkId",
            "action",
            "targetRecordId",
            "expectedHeadRevisionId",
            "expectedHeadGeneration",
            "reasonCode",
            "reason",
            "occurredAtUtc"),
        ["additionalProperties"] = false
    };

    private static JsonObject SubmitAuditSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["projectLinkId"] = IdentifierSchema("plink"),
            ["proposalId"] = IdentifierSchema("proposal"),
            ["recommendation"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("approve", "reject", "human_review")
            },
            ["rationale"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 1,
                ["maxLength"] = MaximumReviewReasonCharacters
            },
            ["reviewGrantId"] = IdentifierSchema("grant"),
            ["assessedAtUtc"] = UtcTimestampSchema()
        },
        ["required"] = new JsonArray(
            "projectLinkId",
            "proposalId",
            "recommendation",
            "rationale",
            "assessedAtUtc"),
        ["additionalProperties"] = false
    };

    private static JsonObject ManageReviewGrantSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["operation"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("issue", "revoke")
            },
            ["projectLinkId"] = IdentifierSchema("plink"),
            ["reviewGrantId"] = IdentifierSchema("grant"),
            ["delegateAuthorityId"] = IdentifierSchema(),
            ["delegateSubjectId"] = IdentifierSchema(),
            ["feedbackKinds"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["maxItems"] = 2,
                ["uniqueItems"] = true,
                ["items"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("quality", "success")
                }
            },
            ["maximumUses"] = new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = 1,
                ["maximum"] = 1000
            },
            ["expiresAtUtc"] = UtcTimestampSchema(),
            ["occurredAtUtc"] = UtcTimestampSchema(),
            ["reason"] = new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 1,
                ["maxLength"] = MaximumReviewReasonCharacters
            },
            ["userConfirmed"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Must be true only after explicit user confirmation."
            }
        },
        ["required"] = new JsonArray(
            "operation",
            "projectLinkId",
            "reviewGrantId",
            "occurredAtUtc",
            "userConfirmed"),
        ["additionalProperties"] = false
    };

    private static JsonObject IdentifierSchema(string? prefix = null) => new()
    {
        ["type"] = "string",
        ["minLength"] = 1,
        ["maxLength"] = MaximumIdentifierCharacters,
        ["pattern"] = prefix is null
            ? "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$"
            : $"^{prefix}_[0-9a-f]{{64}}$"
    };

    private static JsonObject KnowledgeRevisionSchema() => new()
    {
        ["type"] = "string",
        ["pattern"] = "^krev_[0-9a-f]{64}$"
    };

    private static JsonObject Sha256Schema() => new()
    {
        ["type"] = "string",
        ["pattern"] = "^[0-9a-f]{64}$"
    };

    private static JsonObject UtcTimestampSchema() => new()
    {
        ["type"] = "string",
        ["maxLength"] = 64,
        ["description"] = "Stable ISO-8601 UTC timestamp used for idempotent retries."
    };
}
