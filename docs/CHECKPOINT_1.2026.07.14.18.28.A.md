# Pegasus Cortex Checkpoint 1.2026.07.14.18.28.A

Last updated: 2026-07-14

## Outcome

This checkpoint advances the copy-first Cortex migration baseline from passive
contracts to executable, fail-closed storage seams while preserving every
legacy `.cortex` file. It adds mandatory bounded payload protection, a separate
Linux native filesystem adapter, an attempt-first PostgreSQL sync repository,
exact sync-schema readiness attestation, and per-operation Service admission.

Canonical release: `1.2026.07.14.18.28.A`.

NuGet version: `1.20260714.1828-a`.

The existing project-local schema remains `0.2` and the sync family remains
schema version `1`; neither contract required a breaking revision. The active
suite dependency remains exactly `Pegasus.Shared [1.20260711.421-a]`.

## Recovery Point

Before this slice, a source-only rollback snapshot was created at
`D:\Codex Projects\_snapshots\collections\Pegasus-Cortex\1.2026.07.14.17.30.A\Pegasus-Cortex-1.2026.07.14.17.30.A.zip`.
The reopened archive contains 310 entries, is 631238 bytes, and has SHA-256
`415ebbd6b3c6a919b440d3ccea6485a91bab58ef07566ba94f76ed791f6a9dfb`.
Snapshot and package retention remain bounded to ten product-named versions.

No source file was deleted relative to that snapshot. The schema-`0.2`
`.cortex` tree remains the rollback authority until a later persistent import,
read-back comparison, observation window, and explicit user-approved cutover.

## Protected Offline Journal

- `Pegasus.Cortex.OfflineJournal` now includes a host-owned payload-key
  provider and bounded AES-256-GCM v1 protection with a 12-byte nonce, explicit
  16-byte tag, canonical associated data, and a 16 MiB payload ceiling.
- Authentication completes before plaintext is exposed. Owned key, plaintext,
  and associated-data buffers are cleared. Retired/missing keys and provider or
  cryptographic availability failures remain typed, retryable retention states;
  they do not authorize quarantine or deletion.
- Journal writes continue to reserve monotonic hash-chained state before
  exposing an entry. Export, receipt, staging, count, and memory bounds remain
  closed under restart.

## Linux Filesystem Boundary

- New package `Pegasus.Cortex.OfflineJournal.Platform.Linux` targets `net8.0`
  and contains no database, container, or platform-emulation dependency.
- Direct `openat2`, `statx`, `renameat2`, `unlinkat`, `fsync`, `flock`, and
  directory-enumeration calls anchor operations below the approved root,
  reject links/cross-mount traversal/special files/hard links/unsafe modes, and
  use atomic no-replace promotion plus parent-directory fsync.
- Descriptor ownership, fd-zero handling, nonblocking FIFO/device defense,
  exact reopened-object checks, bounded enumeration, and path-redacted errors
  were independently reviewed and corrected.
- Operational security requires one dedicated service UID that exclusively
  owns the `0700` journal root and retains the journal writer lock around every
  cooperating mutation. Linux rename/unlink remain name-based; untrusted
  processes must not run under that UID.

The source contains ARM64 and x64 syscall mappings. Only the portable Windows
harness ran here; native ARM64/x64 execution and crash-injection durability are
still required on the intended Linux host.

## Attempt-First PostgreSQL Sync

- Migration `0002_offline_sync.sql` retains six append-only forced-RLS journal
  tables. Every submission receives an immutable attempt identity, so rejected
  or conflicting bundles remain durable evidence. Only committed attempts can
  advance a source head.
- Exact retries return the original stored commit. Identity collisions and
  source-head continuation failures retain distinct bounded diagnostic codes.
- Deferred integrity checks bind canonical batch, entries, outcomes, source
  head, receipt, and duplicated commit provenance. Repository read-back repeats
  the provenance comparison and recomputes durable hashes before returning.
- Migration-owned policy and schema-object contracts bind the exact sync
  relations, columns, constraints, indexes, triggers, functions, and owners.
  Readiness also denies table/column mutation privileges and PostgreSQL 17
  `MAINTAIN`, verifies forced RLS and exact policy expressions, and remains
  closed on any mismatch.
- Service composition exposes separate base and sync readiness. Query,
  learning, and sync adapters re-check the current admission epoch for every
  operation, including adapters cached before readiness is cleared, poisoned,
  or disposed.

Migration `0001_initial.sql` remains byte-identical at SHA-256
`579841cc0844c0af08d0b341e248ad02573e6eb50eef96e8cdc462461c43787c`.

## Verification

- Independent security, Linux-native, and PostgreSQL reviews were completed;
  concrete findings were repaired before checkpointing.
- `PegasusCortex.OfflineJournal.Tests`: `19/19` passed.
- `PegasusCortex.OfflineJournal.Platform.Linux.Tests`: `3/3` portable cases
  passed; native cases correctly skipped on Windows.
- `PegasusCortex.Core.Tests`: `19/19` passed.
- `PegasusCortex.Persistence.PostgreSql.Tests`: `15/15` passed.
- `PegasusCortex.Service.Tests`: `7/7` passed.
- PostgreSQL integration fixture: `8/8` offline configuration/privilege cases
  passed; live database cases correctly skipped without exact opt-in.
- All 12 repository JSON schemas parsed successfully. Final solution build,
  package inspection, path inventory, and artifact hashes are recorded in the
  release manifest beside this checkpoint.

## Platform And Distribution

Contracts, OfflineJournal, and the Linux adapter target `net8.0`; Core,
PostgreSQL persistence, and Service align with Nexus on `net10.0`. These
libraries remain architecture-neutral where possible. The intended first
server profile remains `linux-arm64`; no broad platform matrix or Coder x64
build was needed for this Cortex-only checkpoint.

Five local NuGet packages plus symbols form the redistributable library seam.
Native PostgreSQL remains a separately versioned Pegasus Server-managed
dependency. Docker is not required or used.

## Non-Cutover Limits

This checkpoint is local, unsigned, non-deployable, and non-cutover. It has no
host credential/key-custody adapter, live PostgreSQL parser/RLS/trigger/retry
proof, Linux ARM64 runtime proof, SQL-backed MCP/API worker, Nexus registration,
installer, upload, trust change, persistent legacy import, cleanup, or
deployment. The integration fixture intentionally requires an externally
provisioned loopback database, distinct migration/runtime roles, and verified
private-CA TLS before it performs network I/O.

No current `.cortex` source may be removed or rewritten based on this release.
The offline copy path remains available for service outages and later replay;
cleanup stays prohibited until durable server read-back, backup/restore, an
observation window, and explicit user approval are separately evidenced.
