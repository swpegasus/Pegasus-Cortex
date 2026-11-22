# Phase A Production Service Plan

> **HISTORICAL AND SUPERSEDED — DO NOT USE FOR CURRENT BUILD, PACKAGING,
> INSTALLATION, OR ARCHITECTURE.** This document preserves a pre-V2 planning
> checkpoint; its framework, host, store, and deployment statements are not the
> current product contract. Use
> [the current installation/build guide](BUILD_FROM_SOURCE.md),
> [shared MCP setup](CORTEX_SHARED_MCP.md), and
> [local operational V2](LOCAL_OPERATIONAL_V2.md). Historical deployment commands
> below are not the maintained release route.

Last updated: 2026-07-13

## Outcome

The smallest production vertical slice should prove one Cortex-owned
application core through two replaceable transports:

1. the existing isolated local stdio MCP adapter for Codex/development;
2. a Nexus-authenticated MCP `2025-11-25` Streamable HTTP ingress that forwards
   bounded signed caller context through a sanctioned local gateway to an
   independent Cortex worker;
3. one durable PostgreSQL namespace with immutable record revisions, lineage,
   lexical search, namespace policy, and RLS defense in depth;
4. one proposal -> review -> immutable publication path; and
5. a bounded status projection plus a Nexus-owned admin/status page showing real
   store, index, migration, gateway, and dependency readiness.

This slice demonstrates retrieval, authorization, durability, review, and
operator visibility without introducing embeddings, provider SDKs, autonomous
training, general thread access, or another always-on public web host.

Phase B implementation has not begun. This document records design and proposed
boundaries only.

## Reconciled Baseline

The frozen checkpoint already provides:

- a `.NET 10` local-process MCP `2025-11-25` adapter with four read-only tools,
  bounded inputs/results, cancellation, allowlisted resources, and no
  filesystem/database/network/live-service access;
- `PegasusCortex.Core` outcome-ingestion policy, immutable repository seams,
  retention/sensitivity/prohibited-content rules, and secure-transport policy;
- deterministic Default Library records, lexical/metadata query behavior,
  explicit supersession, provenance, and closed source/artifact allowlists;
- bounded import/query/cache/retention and disposal behavior in Resource
  Efficiency; and
- provider-neutral deterministic learning-export planning plus a plan-only CLI
  that cannot manufacture build or server authority.

These should be adapted, not duplicated. Project-local `.cortex` continuity is
never read by the product runtime.

## Product And Project Boundaries

### Existing projects retained

- `PegasusCortex.Core` (`net10.0`): application policy and workflows. Refactor
  existing query/ingestion behavior behind the interfaces below.
- `PegasusCortex.Mcp` (`net10.0`): isolated stdio adapter only; it calls the same
  application query service as remote transport.
- `PegasusCortex.DefaultLibrary` (`net8.0`): optional curated data package, not a
  production store.
- `PegasusCortex.ResourceEfficiency` (`net8.0`): bounded provider-neutral
  resource primitives, retained where ownership remains clear.
- `PegasusCortex.LearningExports` (`net8.0`): deterministic provider-neutral
  definitions/manifests/export planning.
- `PegasusCortex.LearningExports.DefaultLibrary` and the plan-only CLI remain
  optional adapters/tools and receive no server or build authority.

### Proposed Cortex projects

- `PegasusCortex.Contracts` (`net8.0`): passive contract types aligned with
  language-neutral JSON Schema/OpenAPI. It contains versions, requests,
  responses, cursors, caller context, diagnostics, record/proposal/review/status
  DTOs, and capability negotiation. It contains no provider or host I/O.
- `PegasusCortex.Persistence.PostgreSql` (`net10.0`): repository
  implementations, migrations, transaction boundaries, RLS session binding,
  lexical index maintenance, and real readiness probes.
- `PegasusCortex.Service` (`net10.0`): independent worker composition root,
  bounded local typed gateway endpoint, queues/backpressure, dependency health,
  and lifecycle. It does not expose an unauthenticated public listener.
- `PegasusCortex.Persistence.Testing` (`net10.0`, test-only): small isolated
  deterministic repository for application/transport conformance. It is not a
  portable production database promise.

Avoid further packages until ownership or independent distribution requires
them. In particular, proposal, review, query, and administration are
application roles inside Core, not separate deployables.

### Required cross-project boundaries

- **Nexus:** owns external HTTPS, issuer/JWKS/certificate validation, service
  registration, quotas, coarse capability scopes, and the remote MCP ingress.
  It forwards a short-lived integrity-protected caller context over a sanctioned
  bounded local transport and renders the management status page from Cortex's
  typed status projection. Nexus contains no Cortex query, review, storage, or
  learning business logic.
- **Pegasus Server:** later owns install/uninstall, worker lifecycle,
  certificate/bootstrap, backup/restore, retention maintenance, Linux ARM64
  packaging, host ACLs, and filesystem/archive path enforcement.
- **Coder and external MCP clients:** consume public MCP/contracts. Coder
  integration remains deferred and may later submit bounded proposals under its
  own principal/correlation/lineage.
- **Brain:** no Cortex live-thread connector is created. The only permitted live
  cross-product thread interchange is Brain <-> Coder through their separately
  owned flow: one durable user grant stored by Nexus and valid until explicitly
  revoked. Normal service calls use identity/scopes/audit and do not require
  per-call popups. Cortex never reads, writes, or proxies that grant.

The external system-management product remains a separately approved test-
access path only. Its machine inventory, operations, tasks, services, ports,
credentials, playbooks, and executable artifacts do not enter Cortex product
knowledge, seed data, contracts, packages, or deployment authority.

## Application Interfaces

Keep product-owned interfaces small, cancellation-aware, and independent from
transport and storage:

- `ICortexQueryService`: discover capabilities, health/status, list authorized
  namespaces/policies, bounded search, exact record read, lineage, and project
  brief reads.
- `ICortexIngestionService`: validate and capture immutable candidate source,
  provenance, rights, sensitivity, redaction result, and sanitized rejection
  evidence. It creates proposals, never trusted published knowledge directly.
- `ICortexReviewService`: approve/reject a proposal, publish an immutable
  revision, and create explicit supersession/tombstone relationships.
- `ICortexAdministrationService`: plan retention/cleanup/compaction, inspect
  store/index/readiness, and apply only separately scoped approved maintenance.
- `ICortexLearningService`: define and plan deterministic eligible-record
  datasets and export manifests. It cannot train, deploy, or grant build
  authority.
- Future optional `ILearningJobProvider`, registry, and evaluator interfaces
  live outside the MCP process and are contract-only until export, lineage,
  evaluation, promotion, and rollback are reliable.

Repository interfaces should be role-focused rather than one transport-shaped
store: record/revision and lineage persistence, proposal/review persistence,
namespace-policy reads, lexical index access, audit append, and unit-of-work
coordination. PostgreSQL specifics remain in the provider project.

## Versioned Contract Model

JSON Schema/OpenAPI are the language-neutral source of truth. The net8 contract
assembly mirrors rather than replaces them. Version independently:

- service API and MCP tool/resource contract;
- record/revision/provenance/evidence schemas;
- caller authority and diagnostic envelopes;
- cursor format and search/index generation;
- proposal/review/publication events; and
- dataset definition, manifest, evaluation, and promotion contracts.

MCP initialization and capability negotiation are deterministic. Support
bounded tool/resource pagination, opaque signed or integrity-protected cursors,
deadlines/cancellation, `listChanged` or version capability notification, and
structured actionable diagnostics. Temporary checkpoint aliases may remain for
one compatibility window, but canonical names are `cortex_discover`,
`cortex_health`, `cortex_search`, `cortex_get_record`, and
`cortex_get_lineage`. Read tools default on. Proposal, mutation, maintenance,
and administration default off unless explicit current scopes grant them.

Every returned record is labeled untrusted, non-executable knowledge and
includes revision, namespace, provenance, sensitivity, confidence, status, and
bounded evidence/lineage information appropriate to caller policy.

## Storage Slice

Use PostgreSQL first. SQLite remains a later isolated portable option after the
repository and migration contract stabilizes.

The first schema needs:

- tenant and namespace policy references;
- logical record identity and immutable revision rows;
- provenance, evidence references, rights, sensitivity, retention, publication
  state, hashes, and timestamps;
- explicit supersedes/superseded-by/tombstone graph;
- immutable candidate proposal and review decision rows;
- append-only audit events and sanitized rejected-write diagnostics; and
- rebuildable lexical index generation/state.

Begin with one tenant and one durable
`shared:pegasus-default-library` namespace, while requiring `tenantId` at every
storage/security boundary. Application authorization is authoritative; RLS is
defense in depth and binds each transaction to verified tenant, principal, and
authorized namespace context. Do not share Nexus identity tables or mutable
databases.

Use revisioning and explicit supersession; never silently overwrite. Enforce
idempotency through source operation/job/event ID plus canonical content hash
and deterministic conflict diagnostics.

## Authentication And Remote MCP

Nexus issues or validates short-lived audience-bound credentials for
`pegasus.cortex`, including issuer/key rotation, token instance/family,
delegation grant, connection, principal, and policy revision. Rejection or
revocation at any layer denies access even when the underlying user remains
valid. User sessions and user-owned API delegations are independent children of
the durable principal, so rotating a session does not silently revoke a
separate valid delegation.

Normal product-to-product service calls do not require per-call user approval.
They require authenticated service identity, least-privilege scopes, current
policy, namespace authorization, quotas, and audit. External organizations use
explicitly trusted OIDC authorities or approved service connections; open
dynamic registration is disabled.

Encrypted transport is mandatory for non-loopback access. Private CA or pinned
self-signed certificates may be supported with rotation. An unauthenticated
loopback bootstrap, if later approved, must be explicit, visible, disableable,
and impossible on non-loopback interfaces.

Architecture conflict: current Nexus has no verified product-extension seam and
its gateway is deliberately not a reverse proxy or plugin-execution host.
Therefore remote MCP requires a separately authorized Nexus gateway/
registration contract. Independent product runtimes are mandatory; importing
Cortex business logic into Nexus simply to share its web endpoint is rejected.

## Packaging And Deployment Targets

- Publish passive language-neutral schemas and exact-pinned net8.0 contracts as
  the compatibility boundary.
- Keep the local stdio MCP as a framework-dependent portable `.NET 10` payload
  without a Windows apphost; local clients require an installed .NET 10 runtime.
- Package the production worker first as a signed `linux-arm64` redistributable
  application with an independently versioned runtime dependency bundle and
  external PostgreSQL. Pegasus Server owns installation and lifecycle once that
  contract is authorized; Cortex does not require Docker or another container
  runtime. Do not create an x64 Cortex product matrix.
- Keep Default Library, PostgreSQL provider, embeddings, trainers, and external
  provider adapters optional and independently installable where practical.
- Ship migrations, compatibility manifests, SBOM/license/source/hash metadata,
  backup/restore instructions, and rollback evidence with deployable releases.

No artifact described by this planning checkpoint is signed, deployable, or
authorized for upload, installation, registration, or live execution.

## Governed Publication And Retrieval

The first write path is a proposal, not a trusted write:

1. capture immutable source plus correlation, service, agent, instance, machine,
   operation/job/event, and authority-qualified user identifiers;
2. enforce request/schema/size/time bounds;
3. classify and redact secrets, credentials, private reasoning, raw streams,
   and disallowed PII before durable payload persistence;
4. attach usage rights/licensing and sensitivity/retention metadata;
5. preserve sanitized rejection cause and payload hash when rejected;
6. require current human or explicit policy review;
7. publish an immutable revision and index it transactionally or through an
   idempotent outbox; and
8. supersede rather than overwrite.

Retrieval makes approved knowledge immediately available without changing model
weights. Lexical/metadata retrieval is the deterministic fallback. Embeddings
and reranking remain later adapters with versioned model identity, rebuildable
indexes, bounded caches, cancellation, and no authorization bypass.

Cortex records are self-contained within the Cortex security boundary. Reading
an approved record is not access to its originating live thread. Provenance is
not a dereference instruction or control handle, and Cortex exposes no live-
thread discovery, refresh, steering, mutation, cancellation, or transfer proxy.

## Learning And Training Boundary

Training remains outside the Cortex MCP and production query process. Cortex
may prepare a deterministic, lineage-complete dataset and signed/hashed manifest
from revisions that are explicitly training-eligible and approved. Build
authority is independently host-injected, integrity-protected, and inaccessible
to Codex, Coder, MCP, records, definitions, prompts, and model output.

The later promotion loop is: approved dataset definition -> deterministic
export -> signed job request -> isolated trainer/provider -> immutable artifact
registry -> benchmark/security/privacy/regression evaluation -> explicit
promotion approval -> canary -> rollback pointer. Record exact base model,
tokenizer, tool schema, prompt, dataset manifest/hash, seed, hyperparameters,
runtime/hardware, outputs, metrics, evaluator versions, approver, and predecessor.
No autonomous training, deployment, or self-modifying source is allowed.

## Filesystem And Archive Boundary

Use the 240 UTF-16-character default for final canonical absolute Windows paths
unless the owning product defines a stricter rule. Validate approved root plus
relative path before I/O; reject traversal, rooted-relative/ADS syntax, reserved
or trailing-normalized names, UTF-8 segments above the default 255-byte limit,
reparse/symlink escape, and over-budget results. Do not rely on long-path mode.
Use short stable folders and bounded IDs/hashes, preserving human names in
metadata.

Archive/package flows preflight every entry against the actual extraction
destination before creating output and reject zip-slip, collisions, unsafe link
entries, expansion bounds, and final-path overflow atomically. See
`PATH_BUDGET_POLICY_HANDOFF.md`. Current learning-export code has partial checks;
unified enforcement and maximum-observed-path measurement are Phase B tasks.
The pre-remediation inventory found a 217-character active-tree maximum and no
active path above 240, plus 13 pre-policy superseded paths up to 261. Artifact-
only remediation preserved all 37 file hashes in a verified short bundle and
external recovery copy, then removed the redundant long cache. Full-tree maximum
is now 232 with zero violations; the old layout must not be recreated.

## Efficiency And Observability

- Prefer notifications/events over polling.
- Bound paging, serialization, queues, concurrency, caches, request/output size,
  deadlines, and rate limits.
- Stream canonical hashes and export I/O; do not raise current bundle limits
  until hashing is cancellation-aware.
- Use connection pooling and incremental lexical-index updates.
- Release resources on cancellation/disconnect and apply deterministic
  backpressure/eviction/disposal.
- Add bounded crash-orphan staging discovery/pruning owned by the host.
- Emit privacy-safe metrics/traces and diagnostics with operation, severity,
  code, relevant IDs, cause, retryability, and correlation across Nexus,
  service/agent/machine, ingestion, review, export, training, and promotion.
- Drive health/readiness from real store, index, migration, and gateway probes.

## Proportional Verification

After explicit Phase B authorization:

1. Validate schema examples and contract version/capability negotiation.
2. Test authorized local stdio and remote gateway calls through the same query
   service, plus wrong/missing/revoked authority and cross-namespace denial.
3. Verify one proposal through rejection and one through review/publication,
   including provenance, idempotency, supersession, and audit.
4. Test bounded search/pagination, malformed/expired cursor, cancellation,
   duplicate concurrent submission, and store/index readiness failure.
5. Exercise PostgreSQL migration forward/rollback and a small backup/restore of
   the one-namespace slice.
6. Add exact-limit path/archive tests from the path-policy handoff and report
   maximum observed generated path.
7. Run one clean affected Release build because compiled source changes. Add
   only the package/consumer/transport checks directly changed by the slice; do
   not repeat unchanged broad matrices or stress passes.

## Snapshot And Recovery Gate

Before Phase B edits, create and verify one immutable source snapshot outside
the project tree, record entry count/size/SHA-256, and confirm the current
checkpoint evidence remains recoverable. Do not include `.cortex` as product
runtime or package content. Before accepting the slice, verify migration
rollback, store backup/restore, index rebuild, failed-publication recovery,
staging cleanup, and the ability to run local read-only stdio against the
restored state. Retain 5-10 verified releases/snapshots and never prune the only
known-good rollback point.
The snapshot destination itself must pass the path budget. The legacy
superseded package-cache bytes now live in the verified bounded-ID `e/306a`
bundle and external recovery collection; do not recreate the nested live tree.

## Assumptions And Material Decisions

Selected assumptions:

- PostgreSQL remains the first production store and RLS is defense in depth.
- Nexus owns identity and external HTTPS; Cortex remains independently
  deployable and owns all knowledge behavior/storage.
- the first namespace is the reviewed Default Library and contains no private
  user data;
- lexical retrieval is sufficient to prove the first slice;
- the existing isolated MCP aliases remain for one bounded compatibility
  window; and
- provider-specific training and embeddings are optional later features.

No user choice blocks documentation. Before remote MCP implementation, however,
Nexus must approve one sanctioned extension/registration and local gateway seam.
If none exists after verification, stop for an architecture decision rather
than embedding Cortex into Nexus or creating an unapproved public listener.
Production retention values, deployment secrets, certificate issuer, and
external identity allowlist also require environment-specific approval before
live deployment, not before local Phase B construction.

## Follow-On Phases

- **Phase B:** contracts/schemas, Core application interfaces, test repository,
  PostgreSQL migrations/RLS, independent worker/local gateway, shared stdio
  query path, proposal/review/publication, readiness, and focused verification.
- **Phase C:** authorized Nexus Streamable HTTP ingress, OAuth metadata,
  federation/service connections, quotas/rate limits, and remote conformance.
- **Phase D:** Pegasus Server lifecycle, certificates/bootstrap, backup/restore,
  retention maintenance, signed Linux ARM64 OCI/package output, and corruption
  recovery.
- **Phase E:** embeddings/reranking and rebuildable index generations behind
  provider-neutral interfaces.
- **Phase F:** host-authorized learning-job registry/providers/evaluators,
  immutable model registry, promotion/canary/rollback. No autonomous training or
  deployment.

Coder, Brain, Scatterbrain, EQ, Vision, and other adapters enter only after the
core proposal/read contracts and product-specific authority are approved. No
adapter may turn retrieved content into executable authority or bypass normal
service identity and namespace policy.
