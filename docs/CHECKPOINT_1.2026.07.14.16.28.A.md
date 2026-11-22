# Pegasus Cortex Checkpoint 1.2026.07.14.16.28.A

Last updated: 2026-07-14

## Outcome

This checkpoint establishes the copy-first synchronization and legacy-migration
baseline for moving project-local Cortex continuity into a future shared
PostgreSQL-backed service without removing the recoverable source. The new
sync family is schema version `1`; the existing project-local `.cortex` schema
`0.2` remains readable and authoritative until a separately verified and
user-approved cutover.

Canonical release: `1.2026.07.14.16.28.A`.

NuGet version: `1.20260714.1628-a`.

The active suite dependency remains exactly
`Pegasus.Shared [1.20260711.421-a]`. Contracts and the offline journal target
`net8.0`; Core and PostgreSQL persistence target `net10.0`. The intended first
server publish remains self-contained `linux-arm64`, with native PostgreSQL as
a separately managed Pegasus Server dependency. Docker is not required.

## Recovery Point

Before schema or persistence edits, a source-only rollback snapshot was created
at
`D:\Codex Projects\_snapshots\collections\Pegasus-Cortex\1.2026.07.14.14.37.A\Pegasus-Cortex-1.2026.07.14.14.37.A.zip`.
The reopened archive contains 250 entries, is 484943 bytes, and has SHA-256
`8ec0c5d8403b2092206958112506c77d03673a3ce1d7f13542da65f31fc5d455`.
Snapshot retention remains bounded to ten product-named versions.

## Stable Sync v1 Boundary

- Six strict language-neutral schemas define capabilities, local store state,
  offline bundles, authenticated receipts, copy-first migration manifests, and
  bounded legacy-file chunk payloads.
- Durable identity binds tenant, namespace, store, store epoch, and project.
  Event and bundle hashes use the existing canonical JSON contract.
- Optional extensions are bounded data only. They cannot grant identity,
  authority, acknowledgment, cleanup, cutover, or executable behavior.
- New SQL-first clients negotiate local/offline capabilities independently;
  they do not have to implement legacy-file migration.

## Protected Offline Journal

- Append reserves a durable hash-chained high-water checkpoint before exposing
  an entry. Missing tails, unknown future state, quarantine, or state rollback
  block later writes instead of reusing sequence or event identity.
- Plaintext is bounded and hashed, protected by a host-owned provider, then
  stored with a second digest. Export unprotects bounded data and rechecks the
  original length and hash before constructing a typed public bundle.
- Only an authenticated public receipt bound to the exact bundle can advance
  `uploaded` or `reconciled`. The older internal evidence receipt cannot alter
  typed sync state.
- Recovery builds bounded entry indexes once. State validation is linear after
  the existing state sort; typed receipt validation is bounded by at most 256
  bundle events. Receipt and staging counts are checked before commit so a
  successful write cannot make the next restart exceed recovery limits.
- Defaults cap typed receipts at 50000, legacy evidence receipts at 50000,
  staging artifacts at 1024, one legacy export at 16777216 plaintext bytes,
  and one export at 256 events. Export holds one bounded plaintext source-file
  buffer at a time.

## Copy-First Legacy Migration

- The v1 planner requires an explicitly approved, exactly spelled `.cortex`
  root. Requested, approved, and opened canonical paths must match ordinally,
  and the trusted filesystem boundary must report the same stable authorized
  and opened root identity.
- Production mutation requires a host-supplied handle-relative, no-follow
  filesystem boundary. The included lexical implementation is internal and
  test-only; it is not production write authority.
- Regular files are inventoried and copied through the host protector. The
  planner repeats the complete path and digest inventory immediately before
  promotion, so additions, removals, renames, or content changes fail the run.
- Verified import proofs are deep immutable snapshots and revalidate their
  manifest hash whenever public evidence is requested. Callers cannot mutate a
  resumable result and reseal fabricated provenance.
- Raw Markdown, JSON, empty files, and unknown roles remain byte-preserved.
  Public upload uses data-only, chunk-aware legacy payloads rather than
  mislabeling source bytes as typed knowledge records.
- The repository smoke copied all 12 current regular `.cortex` files, including
  `archive/.gitkeep`, to a disposable test journal, compared every source hash,
  and removed only the temporary output. No source file was removed or changed
  by the migration implementation.

## Core And PostgreSQL Seam

- Core derives effective authority from the authenticated server context,
  revalidates exact bundle continuity, calls a provider-neutral repository,
  reads the durable commit back, and stores one authenticated canonical
  receipt before returning success.
- Lost-response retries search the exact bundle/commit receipt before a new
  read-back. A regression advances the test clock and proves neither read-back
  nor receipt issuance repeats after the receipt is durable.
- Passive migration `0002_offline_sync.sql` adds isolated forced-RLS sync
  tables for canonical bundles, events, outcomes, source heads, commits, and
  terminal receipts. Deferred parent checks enforce exact cardinality, ordering,
  hash continuation, head equality, and receipt integrity. Composite keys and
  uniqueness prevent late children without per-child quadratic scans.
- Existing migration `0001_initial.sql` remains byte-identical at SHA-256
  `579841cc0844c0af08d0b341e248ad02573e6eb50eef96e8cdc462461c43787c`.
  Sync readiness is explicitly disabled until a real repository and readiness
  adapter attest the new schema.

## Verification

- Independent review found and closed the mutable-proof, path-identity,
  tree-drift, quadratic-recovery, receipt self-brick, oversized-export, and
  lost-response receipt-retry blockers.
- `PegasusCortex.Contracts.Tests`: `27/27` passed.
- `PegasusCortex.OfflineJournal.Tests`: `16/16` passed, including the disposable
  real-source preservation smoke.
- `PegasusCortex.Core.Tests`: `19/19` passed.
- `PegasusCortex.Persistence.PostgreSql.Tests`: `13/13` passed.
- The 23-project Release solution built with zero warnings and zero errors.
- All six sync schemas parse as JSON. Four affected package graphs have exact
  lock files; four packages plus symbols were created and inspected. The
  Contracts package contains all six schemas, and each main package contains
  this checkpoint and the sync design document.
- The final `.cortex` tree contains 12 regular files / 200914 bytes. Its
  canonical relative-path/length/hash inventory SHA-256 is
  `8ffb955a9eb195c5fe555bf1e449ab1e2438e2d0b547eb265ad43f29bd4de35a`.
- Maximum active and complete retained paths are 153 and 232 characters,
  respectively, with no path above the 240-character Windows policy.
- Final evidence is recorded in
  `artifacts/release/1.2026.07.14.16.28.A/cortex-sync-v1-release-manifest.json`.

## Non-Cutover Limits

This is a local unsigned, non-deployable baseline. No production payload key,
production handle-relative filesystem adapter, PostgreSQL sync repository,
live PostgreSQL execution, MCP sync endpoint, Nexus registration, server
installation, upload, trust change, or deployment exists in this checkpoint.
The SQL migration has structural/static coverage but no live PostgreSQL parser,
deferred-trigger, RLS, or receipt round-trip proof yet.

Imports larger than one bounded public export remain protected and
source-preserved but require a future paged export contract. Aggregate retained
directory pruning beyond the explicit receipt/staging/recovery bounds remains a
separate retention component. The initial migration never grants cleanup or
cutover authority. Persistent migration, read-back comparison, backup/restore,
an observation window, and explicit user approval are required before any
legacy source can become cleanup-eligible.
