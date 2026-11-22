# Cortex Synchronization And Legacy Migration v1

> **HISTORICAL AND SUPERSEDED — DO NOT USE FOR CURRENT BUILD, PACKAGING,
> INSTALLATION, OR ARCHITECTURE.** This document preserves a pre-V2 design
> checkpoint. Current authority is `START_HERE.md` and
> `docs/LOCAL_OPERATIONAL_V2.md`. The framework-dependent DLL/profile x64 route
> is retired; current packaging and installation use
> `deploy/x64/New-PegasusCortexV2PackageManifest.ps1` and
> `deploy/x64/Install-PegasusCortexV2X64.ps1`.

Last updated: 2026-07-21

## Status

This document defines the stable v1 boundary for moving Pegasus Cortex from
project-local continuity files toward a shared PostgreSQL-backed service while
retaining a protected offline path. It is an implementation and recovery
contract, not a deployment claim.

The existing `.cortex` schema `0.2` tree remains a readable source of truth for
the current project until a separately approved cutover. Sync v1 does not
rewrite `meta.json`, reinterpret the legacy directory as a database, or grant
permission to delete a source file. The first migration is always copy-first.

## Responsibility Boundaries

| Boundary | Owns | Does not own |
| --- | --- | --- |
| `Pegasus.Cortex.Contracts` | Language-neutral v1 identifiers, bounds, canonical hashes, compatibility, receipts, and migration progress | Filesystem, network, keys, SQL, or authorization providers |
| `Pegasus.Cortex.OfflineJournal` | Protected local queue, immutable source capture, recovery, export, and durable receipt evidence | Server authority, production key custody, source deletion, or cutover |
| `Pegasus.Cortex.Core` | Server-derived namespace authorization, payload policy, repository sequencing, reconciliation, and diagnostics | PostgreSQL driver details or receipt signing keys |
| PostgreSQL persistence | Append-only durable rows, RLS, transaction integrity, idempotency, read-back evidence, and receipt retention | Caller identity derivation, transport authentication, or installer lifecycle |
| Cortex service host | Protected key adapters, PostgreSQL composition, readiness, MCP/API exposure, and per-operation admission | Nexus identity issuance or Pegasus Server installation policy |
| Pegasus Nexus | External ingress, service discovery, identity and policy context, and compatibility routing | Cortex business records or local journal mutation |
| Pegasus Server | Native installation, dependency lifecycle, rollback, service supervision, and host probes | Cortex authorization decisions or record semantics |

## Stable Contract Family

The v1 family uses `schemaVersion: 1`, canonicalization
`pegasus-canonical-json-v1`, and versioned contract identifiers. The base
documents are strict. New optional data must use the bounded extension lane;
unknown required contracts, capabilities, protection suites, proof contracts,
or extensions fail compatibility.

Optional extensions are data only. They cannot grant identity, namespace
authority, acknowledgment, cleanup, cutover, executable behavior, or hash-chain
authority. Implementations must retain the decoded extension payload bytes and
their hashes even when the extension is not understood.

New SQL-first clients do not have to implement the local-store or legacy-file
contracts. Those capabilities are negotiated separately so offline support can
remain independently distributable.

## Identity And Authority

The durable source identity is the tuple:

`tenant + namespace + storeId + storeEpoch + projectId`

`storeEpoch` changes when safe continuation cannot be proven. A store must
never reuse an event sequence or deterministic event identity within an epoch.
Unknown future local schema, a missing committed tail, a hash-chain gap, or a
monotonic-state rollback blocks further writes; it is not repaired by silently
renumbering surviving files.

Tenant and namespace fields carried by an offline event are routing and
conflict-detection hints. On every upload, Core derives the effective tenant,
principal, service identity, policy revision, and namespace grant from the
authenticated server context. Client hints never become grants.

## Traceable Data Flow

1. The producer appends one typed, non-executable intent to an approved local
   store. The store reserves a monotonic sequence before exposing the entry.
2. The payload is bounded, hashed as plaintext, protected by a host-owned key
   provider, hashed again as stored bytes, and committed through an atomic
   same-volume promotion.
3. Export decrypts only a bounded selected range, verifies plaintext length and
   hash, and constructs canonical public events and one canonical bundle. It
   does not move or delete retained storage.
4. Core revalidates hashes, chain continuity, payload contracts, explicit
   target namespace, and server-derived authority before calling persistence.
5. Persistence atomically records the exact canonical documents and indexed
   projections, every event outcome, the source-head transition, and commit
   evidence. Conflicts and rejections remain durable but do not advance the
   source head.
6. Core reads the committed state back, issues an authenticated receipt, then
   durably records and re-reads that exact receipt before returning success.
7. The offline store validates the receipt proof and semantic hash against the
   exact submitted bundle. `uploaded` and `reconciled` are separate states.
8. A read-only retention plan may list only entries whose typed receipt is
   durable and reconciled. The v1 plan cannot delete entries, quarantine
   artifacts, or legacy sources; applying retention requires a later compact
   tombstone contract and separate approval.

## Offline Storage Rules

- The journal root must be the exact canonical root approved by the host.
- Generated Windows paths must not exceed 240 UTF-16 characters. Internal
  directory and file names remain short and portable.
- Production mutation requires a platform filesystem authority that prevents
  link/reparse traversal at the actual open, create, and promotion operations.
  Lexical containment checks alone are not write authority.
- Existing source roots and journal-owned roots cannot overlap.
- Payload protection is mandatory. The library accepts a replaceable host
  key provider and never invents a production key. Its v1 protector uses
  bounded AES-256-GCM records with an explicit 16-byte tag, authenticates
  metadata as associated data, clears owned key/plaintext buffers, and treats
  unavailable or retired keys as retryable retention states rather than
  quarantine authority.
- Pending and retained quotas are independent. Typed receipts, legacy evidence,
  crash staging, migration pages, and retention-plan candidates have explicit
  recovery-safe bounds. Quarantined artifacts remain operator-review-only and
  are never automatically cleanup-eligible.
- Recovery is fail-closed. It may quarantine only journal-owned artifacts and
  never deletes or modifies the legacy source.
- File-content flush and same-volume atomic promotion are distinct from parent-
  directory power-loss durability. A host must not advertise the latter until
  its platform adapter actually provides it.
- The Linux adapter anchors approved roots and descendants with direct
  no-follow, beneath-root, same-mount syscalls; validates UID, modes, file
  types, and hard-link counts; and fsyncs affected parent directories. It has
  no managed-path fallback. Journal roots require a dedicated service UID,
  mode `0700`, and the journal writer lease around every cooperating mutation.
  Untrusted processes must never share that UID because Linux rename/unlink
  operations remain name-based.

## PostgreSQL Rules

Synchronization data lives in an isolated `cortex_sync` schema so the existing
query/learning readiness contract is not silently broadened. All sync tables
are append-only, forced-RLS, tenant/namespace scoped, and unavailable unless a
server-derived `sync` capability is bound to the transaction.

Every submitted bundle first receives an immutable attempt identity. Exact
retries return the original durable result; accepted bundle/event identities
are unique only among accepted attempts, so a conflicting payload is retained
as distinct evidence rather than being discarded by a uniqueness error. Only
a committed attempt may append the next source head. Conflict diagnostics
distinguish identity collisions from source-head continuation failures.

The durable representation must include bounded canonical bundle, event, and
receipt documents alongside indexed columns. This preserves source provenance
and compatible extension payloads without a schema revision for every additive
field. Indexed projections are checked against the canonical documents at the
application boundary and transaction boundary.

One ingestion transaction must prove all of the following before commit:

- the bundle contains exactly its declared ordered event range;
- every event belongs to the same store epoch, project, and bundle;
- event hashes continue from the exact prior event hash;
- the bundle continues the exact prior bundle and server source head;
- every event has exactly one durable outcome;
- only fully applied or duplicate bundles may advance the source head;
- the new head equals the batch's final sequence and final event hash;
- one terminal reconciliation and one authenticated receipt bind the exact
  bundle, outcomes, commit, and read-back evidence.

The migration existing in source is not enough to advertise sync readiness.
The service separately attests migration checksum, the exact migration-owned
relation/column/constraint/index/trigger/function/owner inventory, forbidden
table and column privileges, forced RLS, and the exact policy contract before
opening sync admission. Admission is checked again for every repository
operation, so a cached adapter cannot outlive a cleared, poisoned, or disposed
readiness epoch. Live repository behavior, signed receipt round-trip, and
tenant isolation must still be proven before publishing the sync capability.

## Legacy `.cortex` Migration

### Source inventory

The v1 importer accepts only an explicitly approved root with the exact
`.cortex` leaf name. Requested, approved, and opened canonical spelling must
match ordinally, and the trusted boundary must bind the same stable authorized
and opened root identity. The importer inventories regular files beneath that
root. It
records the source platform's path style and case-sensitivity, exact local path
for local recovery, portable relative path for mapping, byte length, SHA-256,
role, and source schema version. Reparse points and links are rejected rather
than followed. Empty regular files are preserved.

Absolute paths are local provenance only. They are never tenant or namespace
authority and should not be exported beyond the trusted migration boundary.

### Copy and mapping

Each source file is reopened read-only, copied through the host protector, and
rehashed after capture. Immediately before promotion, the complete source tree
is re-inventoried and every exact/relative path, length, and hash is compared.
An addition, removal, rename, or content change fails the run. The local
protected copy and its manifest are committed atomically under an immutable run
identifier.

The verified import proof is a deep immutable snapshot. Public conversion
recomputes its local manifest hash, so a caller cannot mutate a resumable result
and seal fabricated provenance.

Known continuity roles such as `meta`, `state`, `tasks`, `worklog`, `sessions`,
`recovery`, `learnings`, `guide`, `rules`, `readme`, and `mcp-tools` are labeled
for later mapping. Unknown regular files are retained as `legacy-file`; they are
not discarded merely because no structured mapper exists.

Raw Markdown and JSON source bytes are not mislabeled as a typed JSON domain
record. Server upload wraps them in a bounded legacy-file payload carrying the
original bytes, role, relative path, length, and hash. Structured conversion
may later produce separate records while the verbatim copy remains available
for audit and recovery.

The compatibility export remains preflighted before any decrypt and is bounded
to 16 MiB of aggregate plaintext and 256 emitted events. The paged v1 path adds
`pegasus.cortex.legacy-migration-cursor/1.0` and
`pegasus.cortex.legacy-migration-page/1.0`. Each page also carries the complete
portable `pegasus.cortex.legacy-migration-inventory/1.0` projection of the exact
public manifest. The projection contains only ordered zero-based file position,
role, portable path, size/hash, stable record/revision identity, and derived
chunk count; it deliberately excludes local absolute paths. Its canonical hash
binds the manifest hash, migration/source scope, capture time, case semantics,
file count, and total bytes. Paged events use
`pegasus.cortex.legacy-migration-record/1.0`, whose stable record ID is derived
from source-root plus relative path and whose immutable revision ID is derived
from the record identity plus source content hash. The record carries the
requested namespace as a non-authoritative hint, exact portable provenance,
source/content hashes, chunk position, optional superseded revision, and an
explicit tombstone flag. Every initial-copy record also binds the source
manifest hash, inventory hash, and zero-based inventory file index. A tombstone
must identify its predecessor and carry no
content; it is never inferred from a missing file. The initial copy has no
predecessor and is not a tombstone.

Each page contains at most 32 events, carries one existing immutable offline
bundle, and binds the exact
migration manifest and inventory hashes, inventory file count, store epoch,
project, source root, sequence, prior event hash, and prior bundle hash. Contract
validation begins at `CurrentCursor`, decodes every event, and walks the exact
inventory file/chunk sequence. It compares role, path, source size/hash, stable
IDs, chunk count/index/offset/bytes, event sequence/hash chain, capture time, and
portable provenance before deriving the only acceptable `NextCursor` position.
Reordering, duplication, skipped files/chunks, and unrelated but internally
valid records fail closed. `NextCursor.Completed` must be false unless the
derived position is exactly `(inventory.Files.Count, 0)` and must be true at
that exact terminus. Cursor, inventory, and page hashes detect accidental or
hostile mutation but grant no identity, namespace, cleanup, or cutover
authority.

The exporter reopens and authenticates the retained protected import before
each page. It decrypts at most one source file at a time, emits only the selected
chunks, clears the owned plaintext buffer, and leaves both the retained copy and
the original `.cortex` tree unchanged. Repeating the same request and cursor is
deterministic. The next cursor is a proposed position only: a caller may persist
and use it after Core returns an authenticated, durable, fully reconciled
receipt for the page bundle. Lost responses retry the same page; uploaded,
conflicted, rejected, or quarantined outcomes do not advance the durable cursor.
Server-side partial outcomes remain explicit per record/chunk. A quarantined or
conflicting record keeps its stable identity, provenance, and diagnostic result
without advancing the page cursor or silently publishing a partial revision.

Core validates the page envelope, but it does not reduce the page to a bundle.
Migration submission requires the dedicated atomic page-commit repository
capability; repositories without that capability fail closed. The repository
stores the complete canonical page and its SHA-256 beside the bundle attempt in
the same transaction. Ordinary synchronization continues through its unchanged
bundle-only repository method.

The only cursor that needs no predecessor page is page 1 at exact portable
position `(file 0, chunk 0)`. Its sequence, prior event hash, prior bundle hash,
and base server revision must still exactly continue the repository's current
server head before any bundle row is inserted. The first accepted page
therefore registers the exact migration/inventory at the durable boundary; a
second page-1 attempt for that migration is a conflict unless it is the
byte-identical stored attempt.

For every noninitial page, the repository locks the server scope and requires
the current source leaf to be the preceding stored migration page. That page's
authenticated receipt must already exist in the immutable receipt store with
`reconciliationState=reconciled`. The new `CurrentCursor` must be byte-for-byte
equal to the stored predecessor's `NextCursor`, the complete inventory must be
identical, and the new bundle must use the predecessor receipt's exact storage
revision while continuing its sequence and hashes. A skipped prefix, skipped
middle, stale cursor, restarted migration, or competing fork is rejected before
the new bundle attempt is written.

The scope advisory lock makes validation and conditional bundle insertion one
transactional decision. A concurrent exact retry rereads the original canonical
page/commit and remains idempotent. Competing successors cannot both pass the
same server head. The successful path then reuses the existing read-back,
authenticated receipt, and receipt-persistence coordinator, preserving the
single per-batch/per-record outcome model.

An existing bundle attempt is immutable across both its bundle and migration-
page evidence. Reusing the same exact bundle with an absent or different
canonical page returns `cortex_sync_migration_page_conflict`; this is a
permanent, non-retryable migration conflict and is never normalized into the
generic retryable repository-failure diagnostic. A byte-identical stored page
returns an explicit repository replay result with the original commit and
`ReceivedAt`. Core accepts that stored time even when the host wall clock has
moved backward, because the repository has already proven the immutable
attempt; a new commit result still cannot claim a future `ReceivedAt`.

The Service admission adapter is capability-preserving. Its generic form
implements only `ICortexSyncRepository`; the migration-capable form is selected
only when the inner repository also implements
`ICortexLegacyMigrationPageCommitRepository`. This keeps Core's stable,
permanent capability-absent diagnostic observable instead of advertising an
operation that the inner store cannot prove.

Authored contract/application regressions cover the canonical initial position,
self-hashed noninitial cursors, rejection when only the generic bundle repository
is available, skipped-prefix and skipped-middle pages, a stale fork,
same-bundle altered-page permanent classification, byte-identical retry across
a backward clock correction, and valid three-file continuation. PostgreSQL
source regressions require canonical page persistence, reconciled-receipt
lookup, exact six-field-plus-timestamp stored-head reader shape, production
permanent conflict routing, and append-only migration `0003`. Service regression
source proves that a generic inner repository does not acquire the optional
migration interface. Under explicit `go`, locally verified candidate
`1.2026.07.21.21.54.B` passed one non-incremental 25-project Release solution
build with zero warnings/errors plus Core `19/19`, PostgreSQL `16/16`, and
Service `10/10`. The first Core run exposed and then corrected a fixture-only
fixed receipt timestamp; final counts use immutable read-back/commit-derived
fixture time. Nexus independent review remains required before acknowledgment.

The portable inventory proof is migration-only and is never loaded by normal
query/search/read paths, so this correction adds no retrieval latency. Repeating
the complete bounded inventory on every page is the intentionally simple
fail-closed source candidate. After a production repository can independently
register and validate one inventory by hash, a later compatible optimization may
cache it and accept bounded range proofs/hash references on continuation pages.
That optimization must preserve the same semantic checks and may not trust a
client-only cache claim.

### Retention and quarantine planning

`pegasus.cortex.offline-journal-retention-plan/1.0` is read-only evidence. It
can list a bounded set of spool entries only when recovery is clean, the entry
is older than the requested UTC cutoff, and a persisted typed receipt projects
that exact entry as `reconciled`. `pending` and `uploaded` entries are excluded.
The plan reports the bounded quarantine count and fixes
`quarantineDisposition=operator_review_only`, `applySupported=false`,
`sourceMutationAllowed=false`, and `operatorApprovalRequired=true`.

No delete/apply API is exposed in this slice. Pending entry directories form a
monotonic recovery hash chain, so removing them without a compact authenticated
tombstone/checkpoint would make restart validation incomplete. A future apply
contract must first preserve the chain head, idempotency evidence, receipt
binding, and rollback metadata, then receive separate authorization. It must
never treat acknowledgment as permission to mutate the legacy source.

### Progress states

The public migration manifest is an immutable hash-chained series. Per-file
progress may advance only through the supported lifecycle:

`discovered -> copied -> validated -> uploaded -> read_back -> reconciled`

`conflict` and `rejected` are terminal evidence states. A new manifest revision
cannot change source provenance, remove earlier evidence, regress lifecycle, or
silently replace a terminal result.

### Cutover gate

Migration completion requires all of the following evidence:

1. Final source inventory and protected-copy hashes match.
2. Every intended file has a durable authenticated server receipt.
3. Server read-back matches event count, hashes, mapped record IDs, and source
   head.
4. Namespace queries return the expected logical records and provenance.
5. Backup/restore reproduces the same authenticated head.
6. The legacy source remains readable during a defined observation window.
7. The user explicitly approves cutover under a separately versioned plan.

Even after cutover, deletion is a separate retention decision. The v1 manifest
always records `cleanupProhibited: true` and `cutoverApproved: false`; a future
component may supersede that only through a new reviewed contract and explicit
authority.

## Failure And Diagnostic Contract

Failures carry a stable code, severity, operation, bounded relevant identifiers,
retry guidance, and underlying cause type without leaking payloads, credentials,
absolute source paths, or provider exception text. Cancellation requested by
the caller is propagated; provider-originated cancellation is treated as a
failure with diagnostic context.

Partial failure is visible. A durable commit with failed read-back, a verified
receipt with a conflict outcome, or an uploaded but unreconciled bundle is not
reported as success and never makes local data cleanup-eligible.

## Platform And Distribution

Contracts and the offline journal target `net8.0` so Coder and other clients can
consume them on supported platforms. Core, PostgreSQL persistence, and the
service remain aligned with the Nexus server baseline on `net10.0`. The source
is platform-neutral; the first server publish profile remains self-contained
`linux-arm64`. Cortex does not require Docker. Native PostgreSQL is a separate
installer-managed dependency owned by Pegasus Server.

The Linux filesystem adapter is a separate `net8.0` package so portable
contracts and offline behavior remain usable on Windows without Linux native
calls. It contains no database or container dependency. Its syscall source is
ARM64/x64 aware, but a release is not deployable until the ARM64 native tests
run under the intended dedicated service identity and filesystem.

The active shared dependency remains exactly
`Pegasus.Shared [1.20260711.421-a]`. A newer shared path-preflight package is not
adopted until an affected owner-controlled build verifies compatibility; in any
case lexical preflight does not replace platform safe-write authority.

## Current Non-Cutover Limits

- No live PostgreSQL migration or behavioral SQL verification has occurred;
  syntax, deferred constraints, RLS isolation, retries, restart continuation,
  and receipt round-trips still require the explicit native fixture.
- The payload protector and Linux handle-relative adapter exist, but no host
  credential/key-custody adapter is configured and Linux ARM64 native execution
  has not been proven.
- The PostgreSQL synchronization repository is composed behind fail-closed
  Service admission, but no SQL-backed MCP/API worker or Nexus registration
  advertises synchronization.
- No persistent legacy copy has been created and no `.cortex` source has been
  removed or rewritten by the migration implementation.
- The paged migration, ordered portable inventory correction, Core delegation,
  and retention-plan source are locally verified but are not a packaged,
  snapshotted, or Nexus-acknowledged checkpoint. After explicit `go`, one
  non-incremental Release solution build passed with zero warnings/errors;
  Contracts passed `28/28`, OfflineJournal `20/20`, and Core `19/19`, with the
  harnesses executed against the built binaries using `--no-build`.
- No installation, upload, trust change, cutover, cleanup, or deployment has
  occurred.

These limits are intentional. They preserve a recoverable migration path while
the v1 contracts and local/server boundaries are validated independently.
