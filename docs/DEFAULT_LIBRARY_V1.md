# Pegasus Cortex Default Library v1

> **HISTORICAL RELEASE RECORD — DO NOT USE FOR CURRENT BUILD, PACKAGING,
> INSTALLATION, OR ARCHITECTURE.** The library, target-framework, and release
> facts below describe only the named July 2026 release. Use
> [the current installation/build guide](BUILD_FROM_SOURCE.md),
> [shared MCP setup](CORTEX_SHARED_MCP.md), and
> [local operational V2](LOCAL_OPERATIONAL_V2.md). Old deployment scripts named
> in this historical record are not the maintained release route.

The Default Library is a curated, portable base of evidence-backed knowledge for
agents and services that need to understand, diagnose, repair, verify, or improve
Pegasus components. It is an independently packaged `net8.0` library and seed
bundle. It is not a service, database, command catalog, or replacement for each
product's local recovery state.

## Release

- Canonical version: `1.2026.07.11.13.25.A`
- NuGet version: `1.20260711.1325-a`
- Shared dependency: exact `Pegasus.Shared [1.20260711.421-a]`
- Default namespace: `shared:pegasus-default-library`
- Verified artifact retention: 10 versions
- Pre-change snapshot: `_snapshots/collections/Pegasus-Cortex/1.2026.07.11.13.25.A`

## Model and safety

Every `KnowledgeRecordV1` is an immutable revision with deterministic record,
revision, and content hashes. Records preserve evidence references and SHA-256,
provenance transformations, product/component/version applicability, confidence,
freshness, sensitivity, retention, approval, promotion, and supersession state.

Knowledge always has `contentTreatment=untrusted_data`, `executable=false`, and
`trainingEligibility=false`. Import rejects credential material, private reasoning,
unbounded transcript/console material, and executable instruction patterns. Retrieval
returns a read-only agent projection with an explicit safety notice; content cannot
grant authorization, approval, namespace access, or tool arguments.

## Taxonomy

The v1 bundle covers:

- suite topology and responsibilities;
- contracts and compatibility;
- configuration and security;
- workflows;
- diagnostics and failure signatures;
- recovery and rollback;
- verification procedures;
- decisions and tradeoffs;
- glossary;
- Cortex self-description and self-repair.

Claims whose prerequisites are missing remain explicitly `blocked`, `unverified`,
`unknown`, or `stale`. The query service excludes those states unless the caller's
server-derived read scope requests them.

## Deterministic bundle lifecycle

`DefaultKnowledgeLibrary.CreatePriorRelease()` reconstructs the byte-identical
16-record `03.20` prerequisite. `DefaultKnowledgeLibrary.Create()` constructs the
19-record `13.25` desired-state bundle: 13 revisions are unchanged, three materially
changed records explicitly supersede their prior revisions, and three records are
additive. `KnowledgeBundleSerializer` performs strict deterministic JSON export and
import. `KnowledgeBundleValidator` verifies the release mapping, contract, taxonomy,
bounds, deterministic IDs, record hashes, bundle hash, evidence hashes,
classification, and prohibited-content policy before repository work occurs.

`KnowledgeBundleImporter` requires a server-derived import context. It treats an
identical revision as idempotent, requires an explicit link to the current revision
for changed content, and sends one atomic immutable import plan to
`IKnowledgeRevisionRepository`. Repository adapters must reject duplicate revisions
and changed current-revision preconditions transactionally.

## Retrieval

`KnowledgeQueryService` accepts an `IKnowledgeReadRepository`, so PostgreSQL, SQLite,
or another store can implement candidate reads without changing policy. The service
applies namespace, sensitivity, freshness, status, safety, taxonomy, product, and
component filters before scoring. Initial ranking is deterministic lexical/metadata
ranking with stable tie-breaking and bounded candidates/results. Vector search is
deliberately deferred until this authorization-safe baseline is preserved.

## Seed evidence

The current 19 records cite 22 narrowly scoped hashed artifacts from the explicit
Pegasus product/shared-contract allowlist. References use portable `source://`,
`schema://`, and `package://` identifiers rather than embedding machine-local paths
or source contents. Coordination receipts, host administration state, binaries,
caches, and broad operational documents are not seed sources. The bundle contains
curated conclusions only; evidence artifacts are not copied into the package.

## Known blocked boundaries

- No approved recipient-encryption/JWE provider is configured for Cortex.
- Pegasus Server has not supplied the production certificate issuer adapter.
- No authenticated MCP/API host or durable Cortex repository exists yet.
- Live identity, deployment, backup/restore, migration, target-runtime, and
  production-readiness behavior has not been verified by this local package.
