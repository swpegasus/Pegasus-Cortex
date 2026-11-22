using Pegasus.Cortex.DefaultLibrary.Contracts;
using Pegasus.Cortex.DefaultLibrary.Serialization;

namespace Pegasus.Cortex.DefaultLibrary.Seeds;

/// <summary>
/// Deterministic, curated v1 seed. Every claim is bounded and tied to a locally verified source hash.
/// </summary>
public static class DefaultKnowledgeLibrary
{
    public const string ReleaseVersion = "1.2026.07.11.13.25.A";
    public const string NuGetVersion = "1.20260711.1325-a";
    public const string PriorReleaseVersion = "1.2026.07.11.03.20.A";
    public const string PriorNuGetVersion = "1.20260711.320-a";
    public const string DefaultNamespace = "shared:pegasus-default-library";

    private static readonly DateTimeOffset PriorObservedAt = DateTimeOffset.Parse("2026-07-11T10:20:00Z");
    private static readonly DateTimeOffset PriorGeneratedAt = DateTimeOffset.Parse("2026-07-11T10:20:00Z");
    private static readonly DateTimeOffset PriorReviewAfter = DateTimeOffset.Parse("2026-10-09T10:20:00Z");
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-07-11T20:25:00Z");
    private static readonly DateTimeOffset GeneratedAt = DateTimeOffset.Parse("2026-07-11T20:25:00Z");
    private static readonly DateTimeOffset ReviewAfter = DateTimeOffset.Parse("2026-10-09T20:25:00Z");

    public static KnowledgeSeedBundleV1 Create()
    {
        var bundle = KnowledgeRecordFactory.CreateBundle(
            ReleaseVersion,
            NuGetVersion,
            GeneratedAt,
            Taxonomies,
            CreateRecords());
        DefaultLibrarySourcePolicy.ThrowIfInvalid(bundle);
        return bundle;
    }

    /// <summary>
    /// Exact immutable prerequisite bundle used to validate additive upgrade behavior.
    /// </summary>
    public static KnowledgeSeedBundleV1 CreatePriorRelease()
    {
        var bundle = KnowledgeRecordFactory.CreateBundle(
            PriorReleaseVersion,
            PriorNuGetVersion,
            PriorGeneratedAt,
            Taxonomies,
            CreatePriorRecords());
        DefaultLibrarySourcePolicy.ThrowIfInvalid(bundle);
        return bundle;
    }

    public static IReadOnlyList<KnowledgeTaxonomyDefinition> Taxonomies { get; } =
    [
        new("configuration-security", "Configuration and security", "Configuration ownership, identity, authorization, transport, secret, and safety boundaries."),
        new("contracts-compatibility", "Contracts and compatibility", "Canonical schemas, package versions, compatibility behavior, and release mappings."),
        new("cortex-self-description-repair", "Cortex self-description and self-repair", "Cortex's own boundaries, health evidence, failure isolation, and safe recovery posture."),
        new("decisions-tradeoffs", "Decisions and tradeoffs", "Explicit architectural decisions, rejected alternatives, costs, and deferred capabilities."),
        new("diagnostics-failure-signatures", "Diagnostics and failure signatures", "Observable failure states, likely causes, retryability, and blocked prerequisites."),
        new("glossary", "Glossary", "Stable suite terms and distinctions that prevent responsibility or identity drift."),
        new("recovery-rollback", "Recovery and rollback", "Snapshot, backup, rollback, corruption recovery, and safe-pause knowledge."),
        new("suite-topology", "Suite topology and responsibilities", "Product ownership, planes, dependencies, and standalone/degraded behavior."),
        new("verification-procedures", "Verification procedures", "Observable checks and evidence required before a claim is considered verified."),
        new("workflows", "Workflows", "Bounded coordination, lifecycle, promotion, and operator workflow semantics.")
    ];

    private static IReadOnlyList<KnowledgeRecordV1> CreatePriorRecords() =>
    [
        Record(
            "suite-topology", "suite-responsibility-map", "Pegasus suite responsibility map",
            "Nexus, Server, independent products, Shared, and Cortex have distinct responsibilities.",
            "Pegasus Nexus owns ecosystem identity, policy, discovery, routing, quotas, and audit coordination. Pegasus Server owns approved lifecycle work. Independent products own domain runtime and data. Pegasus Shared is a pinned contract package, not a daemon. Pegasus Cortex owns curated knowledge and retrieval without becoming a startup dependency. External approval-gated test channels are outside the curated baseline and confer no product authority.",
            ["topology", "nexus", "server", "cortex", "shared", "products"],
            [App("pegasus-suite", "topology", null, "verified")],
            [Evidence.SourcePolicy, Evidence.SharedVersion, Evidence.NexusVersion],
            0.99, "current", "The curated source policy and scoped release metadata define the responsibility boundary."),

        Record(
            "contracts-compatibility", "shared-contract-release", "Pegasus Shared contract baseline",
            "The current verified shared contract baseline is canonical release 1.2026.07.11.01.53.A and NuGet 1.20260711.153-a.",
            "Pegasus Shared targets net8.0, embeds language-neutral schemas, and is pinned independently by consumers. Required contract incompatibility fails readiness or registration; optional incompatibility degrades only the integration. Additive unknown fields may be preserved, while unsupported required major versions fail closed. The verified local package SHA-256 is 499773af4b8bd0f4524f59ebe76c58852c659cd0cdfe207f5e0acdf4de24ab07.",
            ["contracts", "compatibility", "schema", "shared", "version"],
            [App("pegasus-shared", "contracts", "1.2026.07.11.01.53.A", "verified")],
            [Evidence.SharedVersion, Evidence.SharedPackage, Evidence.AgentOutcomeSchema],
            1.0, "current", "Release metadata and local immutable package hash were verified."),

        Record(
            "contracts-compatibility", "release-version-mapping", "Canonical release version mapping",
            "Pegasus releases use generation.YYYY.MM.DD.HH.mm.A-D with a deterministic NuGet-safe mapping and ten-version retention.",
            "Canonical release 1.2026.07.11.03.20.A maps to NuGet 1.20260711.320-a, assembly ABI 1.0.0.0, and file version 1.2026.711.320. Same-minute revisions are limited to A through D. Verified release, build, package, and product-named snapshot collections retain ten versions by default.",
            ["release", "version", "nuget", "retention", "snapshot"],
            [App("pegasus-cortex", "default-library", PriorReleaseVersion, "verified")],
            [Evidence.SharedVersion, Evidence.ReleaseVersionSchema, Evidence.SourcePolicy],
            1.0, "current", "Canonical mapping follows the allowlisted Shared release contract."),

        Record(
            "configuration-security", "service-communication-baseline", "Service communication security baseline",
            "Normal service traffic requires TLS 1.3, mutual certificate authentication, rotatable references, and recipient encryption through intermediaries.",
            "HTTPS is mandatory for application traffic. Pegasus service links require mutual TLS certificate and trust references. Confidential payloads crossing Relay or another intermediary require a recipient-bound pegasus.confidential-envelope/1.0 envelope. Inline keys and certificate-error bypasses are forbidden. A bounded plaintext exception exists only for local certificate enrollment before a usable certificate exists.",
            ["tls", "mtls", "relay", "envelope", "certificate"],
            [App("pegasus-suite", "communication-security", "1.2026.07.11.01.53.A", "verified")],
            [Evidence.ConfidentialEnvelopeSchema, Evidence.CertificateBootstrapSchema, Evidence.SourcePolicy],
            0.99, "current", "Shared schemas and architecture define the same fail-closed transport boundary."),

        Record(
            "configuration-security", "identity-and-namespace-authority", "Identity and namespace authority",
            "Nexus owns Pegasus identities; Cortex derives effective namespace access from authenticated server policy.",
            "A namespace name is a filter, never a grant. Cortex authorization combines validated authority and subject, acceptable token and connection state, current policy revision, operation capability, and namespace grants. Invalid or revoked presented authentication is denied even if the owning principal remains enabled. Products reference Nexus identifiers without sharing identity-table writes or copying authentication material.",
            ["identity", "namespace", "authorization", "revocation", "nexus"],
            [App("pegasus-cortex", "authorization", PriorReleaseVersion, "verified"), App("pegasus-nexus", "identity", "1.2026.07.11.02.09.A", "verified")],
            [Evidence.SourcePolicy, Evidence.NexusVersion, Evidence.CortexOutcomePolicy],
            0.98, "current", "The allowlisted Nexus identity boundary and Cortex policy implementation were inspected."),

        Record(
            "workflows", "coordination-task-lifecycle", "Agent coordination task lifecycle",
            "The local file bus is a development coordination adapter with atomic task states and bounded acknowledgments.",
            "Each task has one canonical identifier and progresses through queued, active, then completed or failed, followed by coordinator acknowledgment. Heartbeats report liveness but do not grant authority. Terminal tasks are preserved until acknowledgment, then represented by a bounded receipt. A future authenticated adapter must preserve these transition preconditions without becoming a product runtime database.",
            ["coordination", "queue", "heartbeat", "acknowledgment", "task"],
            [App("pegasus-nexus", "coordination", "1", "verified")],
            [Evidence.AgentCoordinationSchema, Evidence.SourcePolicy],
            0.97, "current", "The allowlisted Shared coordination schema and curated source policy were inspected."),

        Record(
            "contracts-compatibility", "agent-outcome-v1", "Agent outcome v1 safety semantics",
            "Agent outcomes are immutable, evidence-backed, namespace-authorized, non-executable records with explicit retention and supersession.",
            "agent-outcome.v1 preserves source service and agent identifiers, evidence hashes, provenance, confidence, sensitivity, retention, approval, promotion, and supersession. Cortex computes authority and accepted hashes server-side. Rejected content is not persisted; only bounded sanitized diagnostics and a payload hash may be retained. Promotion increases curation status but never turns content into authority.",
            ["outcome", "provenance", "supersession", "promotion", "retention"],
            [App("pegasus-cortex", "outcome-ingestion", PriorReleaseVersion, "verified"), App("pegasus-shared", "agent-outcome.v1", "1", "verified")],
            [Evidence.AgentOutcomeSchema, Evidence.CortexOutcomeContract, Evidence.CortexOutcomePolicy],
            1.0, "current", "Schema, implementation contract, and policy source agree."),

        Record(
            "diagnostics-failure-signatures", "recipient-encryption-provider-unavailable", "Recipient encryption provider unavailable",
            "Recipient-encrypted messaging is intentionally blocked until an approved JWE provider is configured.",
            "Observable code confidential_messaging_provider_unavailable means metadata policy may be valid but no approved payload protector is available. The condition is non-retryable without configuration or implementation change. The safe state is rejection with a sanitized diagnostic; plaintext fallback is not permitted.",
            ["diagnostic", "jwe", "encryption", "blocked", "relay"],
            [App("pegasus-cortex", "confidential-messaging", PriorReleaseVersion, "blocked"), App("pegasus-scatterbrain", "relay", "1.2026.07.11.01.53.A", "blocked")],
            [Evidence.CortexTransportSource, Evidence.ConfidentialEnvelopeSchema],
            0.99, "blocked", "The Cortex boundary and allowlisted Shared envelope schema identify the unavailable provider."),

        Record(
            "diagnostics-failure-signatures", "certificate-bootstrap-issuer-unavailable", "Certificate bootstrap issuer unavailable",
            "Cortex cannot issue service certificates until Pegasus Server provides an approved issuer adapter.",
            "Observable code certificate_bootstrap_issuer_unavailable identifies a deliberate fail-closed boundary. The local bootstrap endpoint may validate loopback status and CSR requests, but Cortex is not a certificate authority. Forwarded, remote, confidential, post-enrollment, or certificate-present requests remain denied.",
            ["diagnostic", "bootstrap", "certificate", "issuer", "blocked"],
            [App("pegasus-cortex", "certificate-bootstrap", PriorReleaseVersion, "blocked")],
            [Evidence.CortexOutcomeContract, Evidence.CertificateBootstrapSchema],
            0.99, "blocked", "The Shared contract and Cortex integration document identify the same missing issuer adapter."),

        Record(
            "recovery-rollback", "snapshot-before-mutation", "Verified snapshot before mutation",
            "Material local changes begin with an immutable product-named snapshot and verified integrity metadata.",
            "Snapshot and package collections use canonical versions, immutable hashes, and bounded retention. Restore remains a separately approved operation and must be followed by integrity and compatibility verification. Snapshot contents are recovery artifacts and are not automatically eligible Default Library evidence.",
            ["snapshot", "recovery", "rollback", "integrity", "retention"],
            [App("pegasus-cortex", "recovery", PriorReleaseVersion, "verified")],
            [Evidence.ReleaseVersionSchema, Evidence.SourcePolicy],
            0.98, "current", "Recovery guidance follows the allowlisted release contract and source policy."),

        Record(
            "verification-procedures", "observable-verification-ladder", "Observable verification ladder",
            "Verification proceeds from contract/hash validation through focused behavior tests and packaging, with live claims kept separate.",
            "A trustworthy local result records exact dependency versions and hashes, validates strict schemas and deterministic serialization, exercises success and denial behavior, builds with warnings visible, and verifies package contents and hashes. A local package is not deployment evidence. Readiness, live identity, certificates, durable storage, backup restore, and target-runtime behavior remain blocked or unverified until separately authorized and observed.",
            ["verification", "tests", "build", "package", "readiness"],
            [App("pegasus-cortex", "verification", PriorReleaseVersion, "verified"), App("pegasus-scatterbrain", "verification", "1.2026.07.11.01.53.A", "verified")],
            [Evidence.SharedPackage, Evidence.NexusVersion, Evidence.ScatterbrainVersion, Evidence.SourcePolicy],
            0.98, "current", "Scoped immutable artifacts support the verification boundary without coordination receipts."),

        Record(
            "decisions-tradeoffs", "lexical-search-before-vectors", "Lexical and metadata retrieval before vectors",
            "The initial retrieval layer uses authorization-safe lexical and metadata ranking before semantic/vector search.",
            "Lexical ranking is deterministic, auditable, portable, and can apply namespace and sensitivity authorization before scoring. It provides a secure baseline for bounded result behavior and stable regression tests. Semantic retrieval remains deferred until provenance, authorization filtering, pagination, audit, and deterministic search behavior are verified.",
            ["search", "lexical", "metadata", "vector", "decision"],
            [App("pegasus-cortex", "retrieval", PriorReleaseVersion, "verified")],
            [Evidence.DefaultQuerySource, Evidence.SourcePolicy],
            0.96, "current", "This is an explicit selected Cortex architecture decision."),

        Record(
            "glossary", "core-suite-terms", "Core Pegasus terminology",
            "Stable terminology distinguishes product, identity, namespace, revision, promotion, readiness, and liveness.",
            "Product means an independently releasable application. Nexus is the identity and discovery control plane. Pegasus Server is lifecycle management. Namespace is a record scope label whose access is policy-derived. Revision is immutable content identity. Promotion is reviewed curation, not execution authority. Liveness means a process responds; readiness means required production dependencies and policy are valid. External test channels are outside the product baseline and confer no authority.",
            ["glossary", "product", "identity", "namespace", "readiness"],
            [App("pegasus-suite", "terminology", null, "verified")],
            [Evidence.SourcePolicy, Evidence.NexusVersion, Evidence.ScatterbrainVersion],
            0.98, "current", "Terms follow the curated source boundary and scoped product metadata."),

        Record(
            "cortex-self-description-repair", "cortex-product-boundary", "Pegasus Cortex product boundary",
            "Cortex is an optional production knowledge service, distinct from project-local continuity and independent of other products' startup.",
            "Cortex owns durable evidence-backed records, namespace policy, retention, retrieval, promotion, audit, and export lineage. Project-local .cortex files remain human-readable development continuity and are not runtime storage. Cortex never executes Node or management operations, never owns Nexus identities, and never lets retrieved text bypass caller policy or approval.",
            ["cortex", "self-description", "boundary", "continuity", "retrieval"],
            [App("pegasus-cortex", "product-boundary", PriorReleaseVersion, "verified")],
            [Evidence.SourcePolicy, Evidence.CortexOutcomeContract],
            0.99, "current", "Cortex continuity and implemented contract documentation agree."),

        Record(
            "cortex-self-description-repair", "cortex-safe-repair-posture", "Cortex safe repair posture",
            "Cortex repair is evidence-first, rollback-aware, and fail-closed when authority or integrity is uncertain.",
            "Diagnosis should preserve operation, caller, namespace, relevant identifiers, severity, retryability, and underlying cause without retaining prohibited payloads. Recovery evidence includes the last verified snapshot or package, contract versions, hashes, migration state, and restore verification. No knowledge record supplies automatic tool arguments or privileged authorization; a separate policy and approval path governs every mutation.",
            ["cortex", "self-repair", "diagnostics", "rollback", "integrity"],
            [App("pegasus-cortex", "self-repair", PriorReleaseVersion, "verified")],
            [Evidence.CortexOutcomeContract, Evidence.ReleaseVersionSchema, Evidence.SourcePolicy],
            0.97, "current", "The repair posture follows explicit Cortex diagnostics, content, and allowlisted recovery boundaries."),

        Record(
            "diagnostics-failure-signatures", "production-readiness-open-gaps", "Production readiness remains unverified",
            "Current local contract packages do not establish a production-ready Cortex, Nexus, or Scatterbrain deployment.",
            "Known open evidence includes durable transactional storage, migrations, backup and restore, approved JWE and certificate providers, protected signing and trust material, authenticated live integration, signed target-runtime packages, local audit, and deployment verification. These are reported as unverified or blocked claims and must not be inferred from successful local compilation or packaging.",
            ["readiness", "unverified", "storage", "deployment", "gaps"],
            [App("pegasus-cortex", "production-readiness", PriorReleaseVersion, "unverified"), App("pegasus-nexus", "production-readiness", "1.2026.07.11.02.09.A", "unverified"), App("pegasus-scatterbrain", "production-readiness", "1.2026.07.11.01.53.A", "unverified")],
            [Evidence.SourcePolicy, Evidence.NexusVersion, Evidence.ScatterbrainVersion],
            0.94, "unverified", "The gaps are documented, but no live deployment verification was authorized or performed.")
    ];

    private static IReadOnlyList<KnowledgeRecordV1> CreateRecords()
    {
        var prior = CreatePriorRecords();
        var replacedSlugs = new HashSet<string>(StringComparer.Ordinal)
        {
            "suite-responsibility-map",
            "shared-contract-release",
            "observable-verification-ladder"
        };

        return
        [
            .. prior.Where(record => !replacedSlugs.Contains(record.Slug)),

            SupersedingRecord(
                prior,
                "suite-topology", "suite-responsibility-map", "Pegasus suite responsibility map",
                "Nexus selects package profiles, Coder produces evidence, Server owns approved lifecycle, and Cortex curates bounded knowledge.",
                "Nexus selects the self-contained default or an explicitly requested thin/fallback profile. Coder produces application and runtime artifacts and validates their evidence. Pegasus Server owns separately approved acquisition, hash and signature verification, reference counting, rollback, and final cleanup. Shared remains a passive contract/tooling source. Cortex stores compact non-executable conclusions and evidence references. Compatibility readiness is never deployment authorization.",
                ["topology", "nexus", "server", "coder", "cortex", "shared"],
                [App("pegasus-nexus", "profile-selection", "1.2026.07.11.13.13.A", "verified"), App("pegasus-server", "dependency-lifecycle", "1.2026.07.11.13.07.A", "verified"), App("pegasus-coder", "distribution", "1.2026.07.11.11.52.A", "verified"), App("pegasus-cortex", "curation", ReleaseVersion, "verified")],
                [Evidence.NexusReleaseManifest, Evidence.ServerReleaseManifest, Evidence.CoderSelfContainedManifest, Evidence.CoderThinManifest, Evidence.SourcePolicy1325],
                0.99, "current", "The bounded release manifests and focused source policy agree on product ownership."),

            SupersedingRecord(
                prior,
                "contracts-compatibility", "shared-contract-release", "Pegasus Shared contract baseline",
                "The exact Shared NuGet baseline remains dependency-free 1.20260711.421-a while source tooling adds proportional verification and safer deterministic archives.",
                "Pegasus Shared remains the unchanged net8.0 package 1.20260711.421-a with SHA-256 9696db2ccc40e74649233cfbfa34b94a201d5810b71264ce62d1ae33a12a35a6 and zero external NuGet dependencies. The later source policy requires proportional verification. The release-tools source adds OS-aware, reparse-safe, deterministic streaming ZIP behavior. Those source updates do not claim or imply a newer Shared binary package.",
                ["shared", "contracts", "verification", "archive", "dependency"],
                [App("pegasus-shared", "contracts", "1.2026.07.11.04.21.A", "verified"), App("pegasus-shared", "release-tooling-source", null, "verified")],
                [Evidence.SharedPackage421, Evidence.SharedVerificationPolicy, Evidence.SharedReleaseTools, Evidence.SourcePolicy1325],
                1.0, "current", "The exact package hash and compact source hashes were independently verified."),

            SupersedingRecord(
                prior,
                "verification-procedures", "observable-verification-ladder", "Proportional observable verification",
                "After a stable baseline, verification targets changed behavior and its primary failure path without repeating unchanged matrices.",
                "Test the changed public contract and its most important failure path. Run one clean build when compiled source changes. Add package, consumer, schema, smoke, stress, cross-runtime, or visual checks only when that surface changed or a concrete failure expands risk. Record what was not rerun and retain the prior evidence for unchanged surfaces.",
                ["verification", "proportional", "failure-path", "build", "evidence"],
                [App("pegasus-shared", "verification-policy", null, "verified"), App("pegasus-cortex", "default-library", ReleaseVersion, "verified")],
                [Evidence.SharedVerificationPolicy, Evidence.SourcePolicy1325],
                1.0, "current", "The allowlisted verification policy directly defines the focused contract."),

            CurrentRecord(
                "contracts-compatibility", "produced-targets-vs-source-support", "Produced targets versus source support",
                "Supported source targets and produced release inventory are separate evidence dimensions.",
                "Nexus source supports win-x64, linux-x64, and linux-arm64, while its current produced executable inventory contains linux-arm64 only. Coder source supports the same three targets, while its current produced executable inventory contains win-x64 only. Server, Cortex, and Shared artifacts in this baseline are architecture-neutral libraries and must not be relabeled as ARM64 binaries. Future runnable Server and Cortex hosts target linux-arm64, but no such host artifact is claimed here.",
                ["runtime", "platform", "inventory", "source-support", "architecture-neutral"],
                [App("pegasus-nexus", "release-inventory", "1.2026.07.11.13.13.A", "verified"), App("pegasus-coder", "release-inventory", "1.2026.07.11.11.52.A", "verified"), App("pegasus-server", "libraries", "1.2026.07.11.13.07.A", "verified"), App("pegasus-cortex", "libraries", ReleaseVersion, "verified")],
                [Evidence.NexusReleaseManifest, Evidence.ServerReleaseManifest, Evidence.CoderSelfContainedManifest, Evidence.SourcePolicy1325],
                0.99, "current", "The release manifests explicitly separate supported source targets from produced inventory."),

            CurrentRecord(
                "decisions-tradeoffs", "coder-self-contained-thin-profiles", "Coder self-contained and thin profiles",
                "Coder keeps self-contained as the default and exposes thin plus managed runtime only through explicit selection and fallback policy.",
                "The current win-x64 self-contained artifact is 271367474 bytes with SHA-256 96caadcc26eb6a2ebef39e7189667c05b3016e36826fab24074aa615945c846b. The thin artifact is 5118788 bytes with SHA-256 bd839861122ef1aabb9ea4eca821521960f451fffcaa68c1e81e1a1111789400, and its .NET 10.0.9 runtime bundle is 86322969 bytes with SHA-256 9668f63a4e0594359d5fc191d3e664f6b04b9deacd95336f1a8e927e819ed58e. Thin plus runtime saves 179925717 bytes, or 66.3 percent. All remain unsigned and nondeployable.",
                ["coder", "self-contained", "thin", "runtime", "storage"],
                [App("pegasus-coder", "self-contained-profile", "1.2026.07.11.11.52.A", "verified"), App("pegasus-coder", "thin-profile", "1.2026.07.11.11.52.A", "verified")],
                [Evidence.CoderSelfContainedManifest, Evidence.CoderThinManifest, Evidence.SourcePolicy1325],
                1.0, "current", "The two allowlisted deployment manifests contain the exact profile and artifact metadata."),

            CurrentRecord(
                "suite-topology", "server-portable-filesystem-boundary", "Server portable filesystem and lifecycle boundary",
                "Pegasus Server currently ships architecture-neutral net8 libraries with an OS-aware filesystem boundary, not a runnable host.",
                "Release 1.2026.07.11.13.07.A contains six package and symbol files whose libraries have no runtimes entries. FileSystemPathBoundary centralizes OS-aware rooted-path, containment, and reparse handling, while build roots remain project-relative or configurable. Server owns approved dependency acquisition, verification, reference counting, rollback, and final cleanup. No Server ARM64 binary exists in this release; a future runnable host targets linux-arm64.",
                ["server", "filesystem", "portable", "lifecycle", "library"],
                [App("pegasus-server", "filesystem-boundary", "1.2026.07.11.13.07.A", "verified"), App("pegasus-server", "dependency-lifecycle", "1.2026.07.11.13.07.A", "verified")],
                [Evidence.ServerReleaseManifest, Evidence.ServerFileSystemBoundary, Evidence.SourcePolicy1325],
                0.99, "current", "The architecture-neutral release manifest and focused source hash support the bounded claim.")
        ];
    }

    private static KnowledgeRecordV1 SupersedingRecord(
        IReadOnlyList<KnowledgeRecordV1> prior,
        string taxonomy,
        string slug,
        string title,
        string summary,
        string content,
        IEnumerable<string> keywords,
        IEnumerable<KnowledgeApplicability> applicability,
        IEnumerable<KnowledgeEvidence> evidence,
        double confidence,
        string freshness,
        string freshnessBasis) =>
        CurrentRecord(
            taxonomy, slug, title, summary, content, keywords, applicability, evidence,
            confidence, freshness, freshnessBasis,
            [prior.Single(record => record.Taxonomy == taxonomy && record.Slug == slug).RevisionId]);

    private static KnowledgeRecordV1 CurrentRecord(
        string taxonomy,
        string slug,
        string title,
        string summary,
        string content,
        IEnumerable<string> keywords,
        IEnumerable<KnowledgeApplicability> applicability,
        IEnumerable<KnowledgeEvidence> evidence,
        double confidence,
        string freshness,
        string freshnessBasis,
        IEnumerable<string>? supersedesRevisionIds = null) =>
        KnowledgeRecordFactory.Create(
            DefaultNamespace,
            taxonomy,
            slug,
            title,
            summary,
            content,
            keywords,
            applicability,
            evidence,
            new KnowledgeProvenance(
                KnowledgeContractIds.KnowledgeRecord,
                evidence.Select(item => item.Reference).ToArray(),
                ["bounded-summary", "evidence-hash-captured", "non-executable-normalization", "supersession-linked"]),
            new KnowledgeConfidence(confidence, freshnessBasis),
            new KnowledgeFreshness(freshness, ObservedAt, ReviewAfter, freshnessBasis),
            "internal",
            new KnowledgeRetention("knowledge", null, false),
            new KnowledgeApproval("approved", 1, "pegasus-nexus:curated-default-library-policy-v1", ObservedAt),
            new KnowledgePromotion("seeded", null, DefaultNamespace),
            supersedesRevisionIds,
            ObservedAt);

    private static KnowledgeRecordV1 Record(
        string taxonomy,
        string slug,
        string title,
        string summary,
        string content,
        IEnumerable<string> keywords,
        IEnumerable<KnowledgeApplicability> applicability,
        IEnumerable<KnowledgeEvidence> evidence,
        double confidence,
        string freshness,
        string freshnessBasis) =>
        KnowledgeRecordFactory.Create(
            DefaultNamespace,
            taxonomy,
            slug,
            title,
            summary,
            content,
            keywords,
            applicability,
            evidence,
            new KnowledgeProvenance(
                KnowledgeContractIds.KnowledgeRecord,
                evidence.Select(item => item.Reference).ToArray(),
                ["bounded-summary", "evidence-hash-captured", "non-executable-normalization"]),
            new KnowledgeConfidence(confidence, freshnessBasis),
            new KnowledgeFreshness(freshness, PriorObservedAt, PriorReviewAfter, freshnessBasis),
            "internal",
            new KnowledgeRetention("knowledge", null, false),
            new KnowledgeApproval("approved", 1, "pegasus-nexus:curated-default-library-policy-v1", PriorObservedAt),
            new KnowledgePromotion("seeded", null, DefaultNamespace),
            supersedesRevisionIds: null,
            createdAt: PriorObservedAt);

    private static KnowledgeApplicability App(
        string productId,
        string componentId,
        string? minimumVersion,
        string state) => new(productId, componentId, minimumVersion, null, state);

    private static class Evidence
    {
        public static readonly KnowledgeEvidence SharedVersion = Source("source://pegasus-shared/VERSION.json", "94f35960bc10e647a9d34d378a8c6361b0aca5d104bf5ec85025fe271dbcb7d9", "1.2026.07.11.01.53.A", "application/json");
        public static readonly KnowledgeEvidence SharedPackage = Source("package://pegasus-shared/1.20260711.153-a", "499773af4b8bd0f4524f59ebe76c58852c659cd0cdfe207f5e0acdf4de24ab07", "1.20260711.153-a", "application/vnd.nuget.package");
        public static readonly KnowledgeEvidence AgentOutcomeSchema = Source("schema://pegasus-shared/agent-outcome.v1", "b6bc3fea55c96b94eec206b052cf5eea544c8718fcf2eedc16cdd9d9b76052f9", "1", "application/schema+json");
        public static readonly KnowledgeEvidence AgentCoordinationSchema = Source("schema://pegasus-shared/agent-coordination.v1", "6ff9191d5409040f7de956e2551bf6e6d86235d3dc526c7b6c44c74ea00f17d1", "1", "application/schema+json");
        public static readonly KnowledgeEvidence ReleaseVersionSchema = Source("schema://pegasus-shared/release-version.v1", "f43272e6f25f70f53756fda60aea1b16efe40456397d0010d3d170c8311f0c62", "1", "application/schema+json");
        public static readonly KnowledgeEvidence ConfidentialEnvelopeSchema = Source("schema://pegasus-shared/confidential-envelope.v1", "58838bd8f7edd255cc4452406d6d83683cd1efae3106706f9283fa579d346263", "1", "application/schema+json");
        public static readonly KnowledgeEvidence CertificateBootstrapSchema = Source("schema://pegasus-shared/certificate-bootstrap.v1", "b31d5fe2d85b57af5449daf732e01dae63bd1c322b03db04417a7c144caf03c2", "1", "application/schema+json");
        public static readonly KnowledgeEvidence NexusVersion = Source("source://pegasus-nexus/VERSION.json", "8a55fc120a5d6f6332e28b79b77e362761d841990caa0bbcb2e8f0cb02439725", "1.2026.07.11.02.09.A", "application/json");
        public static readonly KnowledgeEvidence ScatterbrainVersion = Source("source://pegasus-scatterbrain/VERSION.json", "95fda5c751af742b0d5206a73ea46581c8d872cbaaac359358ffdad5e3f997d4", "1.2026.07.11.01.53.A", "application/json");
        public static readonly KnowledgeEvidence CortexOutcomeContract = Source("source://pegasus-cortex/docs/OUTCOME_AND_TRANSPORT_CONTRACTS.md", "cb7ec4f36c808fdb4ff4bdd3d025df119668bf29e5b8bb79d36c487a61a21bf4", "0.1.0-alpha.1");
        public static readonly KnowledgeEvidence CortexOutcomePolicy = Source("source://pegasus-cortex/src/PegasusCortex.Core/Outcomes/AgentOutcomePolicy.cs", "a49428d0499743b9e0c43320bcbdd01ba143ee47c0d75e24f7079d77fb65343c", "0.1.0-alpha.1", "text/x-csharp");
        public static readonly KnowledgeEvidence CortexTransportSource = Source("source://pegasus-cortex/src/PegasusCortex.Core/Security/CortexTransportSecurity.cs", "0c43dfa5a6848bbbf15f22373a825e55bb4bd906ee743038ddbc8e54bb5a4b06", "0.1.0-alpha.1", "text/x-csharp");
        public static readonly KnowledgeEvidence SourcePolicy = Source("source://pegasus-cortex/docs/DEFAULT_LIBRARY_SOURCE_POLICY.md", "0a56e1848b37516f796125c925e5dc56aeb8ffa9028ef87fa71fe04daafa2cd8", PriorReleaseVersion);
        public static readonly KnowledgeEvidence DefaultQuerySource = Source("source://pegasus-cortex/src/PegasusCortex.DefaultLibrary/Query/KnowledgeQueryService.cs", "8d95320a7e0686573292a641bd982b644bd247776d1a3857886c466090723add", "1.20260711.230-a", "text/x-csharp");
        public static readonly KnowledgeEvidence SharedPackage421 = CurrentSource("package://pegasus-shared/1.20260711.421-a", "9696db2ccc40e74649233cfbfa34b94a201d5810b71264ce62d1ae33a12a35a6", "1.20260711.421-a", "application/vnd.nuget.package");
        public static readonly KnowledgeEvidence SharedVerificationPolicy = CurrentSource("source://pegasus-shared/docs/VERIFICATION_POLICY.md", "4d39327365870792d36490ee97c4544ef0deebad71029f82d4a5bf6e291e15d6", null);
        public static readonly KnowledgeEvidence SharedReleaseTools = CurrentSource("source://pegasus-shared/tools/Pegasus.ReleaseTools.psm1", "43559735b56d86c83cae408ccff4d7824da6a30650a5d39b274fe9890ce7d115", null, "text/x-powershell");
        public static readonly KnowledgeEvidence NexusReleaseManifest = CurrentSource("source://pegasus-nexus/releases/1.2026.07.11.13.13.A/release-manifest.json", "81c002f78489b14c30f5d8863ad030f1f82995eb1ad84b8058e7cfd77e344b6e", "1.2026.07.11.13.13.A", "application/json");
        public static readonly KnowledgeEvidence ServerReleaseManifest = CurrentSource("source://pegasus-server/releases/1.2026.07.11.13.07.A/manifest.json", "f4d9938f969abe21c39a64bbe9fd324618052b01c01c5467ee2279dd2c0d6d89", "1.2026.07.11.13.07.A", "application/json");
        public static readonly KnowledgeEvidence ServerFileSystemBoundary = CurrentSource("source://pegasus-server/src/PegasusServer.Infrastructure/FileSystem/FileSystemPathBoundary.cs", "f04e10b7f7cbeaf6013837284235d90b5654c1e6c15ab34844c929ea9e6c70b2", "1.2026.07.11.13.07.A", "text/x-csharp");
        public static readonly KnowledgeEvidence CoderSelfContainedManifest = CurrentSource("source://pegasus-coder/deployment/pegasus-coder.deployment-manifest.json", "52e0ff1afbd1b8588e2b7a2893dcdbea813a4a942553b811ad430f194ed0950c", "1.2026.07.11.11.52.A", "application/json");
        public static readonly KnowledgeEvidence CoderThinManifest = CurrentSource("source://pegasus-coder/deployment/pegasus-coder.thin.deployment-manifest.json", "d160eae1b74dc61c559cc54a64fcf17cd98942d68e9964b965131a5323a2db65", "1.2026.07.11.11.52.A", "application/json");
        public static readonly KnowledgeEvidence SourcePolicy1325 = CurrentSource("source://pegasus-cortex/docs/DEFAULT_LIBRARY_SOURCE_POLICY_1325.md", "d8b333cbe3f50098014c5ba7342655a9354d9749f4ae81a1c4576396424c1e34", ReleaseVersion);

        private static KnowledgeEvidence Source(string reference, string hash, string? version, string mediaType = "text/markdown") =>
            new(reference, mediaType, hash, "inspected-and-hashed", PriorObservedAt, version);

        private static KnowledgeEvidence CurrentSource(string reference, string hash, string? version, string mediaType = "text/markdown") =>
            new(reference, mediaType, hash, "inspected-and-hashed", ObservedAt, version);

    }
}
