# Pegasus Cortex Checkpoint 1.2026.07.21.21.54.B

Date: 2026-07-21

Status: locally verified migration-integrity checkpoint candidate; unsigned,
unpackaged, nondeployable, non-cutover, and pending independent Nexus review
before task `863bcc5241b3` may be acknowledged.

## Purpose

This checkpoint closes four source-level integrity defects found during Nexus
review of the server-authoritative legacy-migration continuation boundary:

1. The stored source-head query, six-field value, and reader now agree on value
   ordinals 0-5 plus `recorded_at` at ordinal 6.
2. Reusing an immutable bundle attempt with absent or different canonical page
   evidence returns permanent, non-retryable
   `cortex_sync_migration_page_conflict` rather than a generic retryable commit
   failure.
3. A byte-identical retry may reuse the original `ReceivedAt` after backward
   wall-clock correction only when the repository proves it read the immutable
   stored attempt.
4. Service admission advertises
   `ICortexLegacyMigrationPageCommitRepository` only when the wrapped
   repository actually supports that optional capability.

The correction is confined to migration commit/continuation. Query, catalog,
search, and record-read paths were not changed, so migration, export, training,
and maintenance work remains outside the hot retrieval path.

## Root And Version

- Physical project root: `D:\Codex Projects\Pegasus Cortex`.
- Compatibility alias: `D:\Codex Porjects\Pegasus Cortex` targets the same
  physical root; no second checkout was edited.
- Canonical version: `1.2026.07.21.21.54.B`.
- NuGet mapping: `1.20260721.2154-b`.
- Assembly version: `1.0.0.0`.
- File version: `1.2026.721.2154`.
- Exact Shared dependency remains `Pegasus.Shared [1.20260711.421-a]`, SHA-256
  `9696db2ccc40e74649233cfbfa34b94a201d5810b71264ce62d1ae33a12a35a6`.

The suite release tool reserved rollback identity `1.2026.07.21.21.54.A`, then
checkpoint identity `1.2026.07.21.21.54.B`. Project version files were updated
with a narrow patch so the existing Shared dependency provenance remained
present.

## Rollback Snapshot

The corrected source candidate was snapshotted before project version mutation
or verification:

- Archive:
  `D:\.pegasus-cortex\snapshots\Pegasus-Cortex\1.2026.07.21.21.54.A\Pegasus-Cortex-1.2026.07.21.21.54.A.zip`
- Entries / bytes: `395` / `904559`.
- Archive SHA-256:
  `6e615ba746865203d6653ac1fc274d8a3b9fa762085ee155f12d686d5e576e9e`.
- Snapshot-manifest SHA-256:
  `532d0dd95f453631ca7c46a29a7ccf7f66fe5efc5484e6b0c6fa62280fa8975b`.
- Exclusions: `.git`, `bin`, `obj`, `node_modules`, `artifacts`, and
  `TestResults`.
- Retention: 10 verified snapshot roots remain. The tool pruned only the oldest
  verified root, `1.2026.07.15.07.00.A`, after the new archive was listed and
  hashed.

This is rollback evidence, not a completed-source snapshot. It intentionally
predates version `21.54.B` and the later test-fixture diagnostic/timestamp
correction described below.

## Verification

Exactly one non-incremental full-solution Release build was run:

```powershell
dotnet build .\PegasusCortex.slnx -c Release --no-restore --nologo --no-incremental
```

Result: all 25 projects built in 12.15 seconds with 0 warnings and 0 errors.

The three proportional harness commands were:

```powershell
dotnet run --project .\tests\PegasusCortex.Core.Tests\PegasusCortex.Core.Tests.csproj -c Release --no-build --no-restore
dotnet run --project .\tests\PegasusCortex.Persistence.PostgreSql.Tests\PegasusCortex.Persistence.PostgreSql.Tests.csproj -c Release --no-build --no-restore
dotnet run --project .\tests\PegasusCortex.Service.Tests\PegasusCortex.Service.Tests.csproj -c Release --no-build --no-restore
```

Final results:

- Core: `19/19`.
- PostgreSQL provider: `16/16`.
- Service: `10/10`.

The first Core run reported `18/19`. The backward-clock retry had passed, but
the valid terminal continuation exposed a fixture-only defect: its constant
receipt persistence timestamp eventually preceded the third page's immutable
read-back time. The test repository now derives `RecordedAt` from
`Reconciliation.ReadBackAt` or `Durability.CommittedAt`, plus one second. A
stage-specific failure helper was retained so future failures identify the
exact migration step and structured diagnostic. The Core test project was
rebuilt twice while isolating and correcting that fixture; both targeted
Release builds had 0 warnings and 0 errors, and the final run passed `19/19`.

The initial parallel harness launch returned only the nonzero Core result, so
PostgreSQL and Service were rerun once to obtain independently captured final
evidence. No broader contract, journal, MCP, native, integration, package,
stress, consumer, or visual matrix was run.

## Bound Source And Binary Evidence

| Item | SHA-256 |
|---|---|
| `CortexLegacyMigrationPageRepository.cs` | `e2764981ef27a659c7ff2f410724fa1359bcfc3ddae6b532c3719cc2d5463c85` |
| `CortexSyncApplicationService.cs` | `5afd531b452082a705c7a27c1c41d99901bf829fd86c180d00f7b739de54f4cd` |
| `PostgreSqlCortexSyncRepository.cs` | `c5252487de0faf4c2fb5a6e4798cfd606b130c55aea8b91c618d7322ec71db10` |
| `PostgreSqlCortexSyncRepository.Read.cs` | `dd03bdde31ad836b4f043cf2c1b3693c22c734506fab51bfc13e35dbf7cacc01` |
| `CortexPostgreSqlAdmissionDecorators.cs` | `2ea90d7ad09ad17b48d3702d42197fdf545158b68ddeb28c1d597e94da73bfc0` |
| `CortexPostgreSqlComposition.cs` | `b84f4fb3c3bbb4902a6815837008c7d8cd2b274d8e57daf6ece174a2dca90a44` |
| Core test source | `1176c1e626518a01a879802af0638712c27ba954d741c9c9d31e97996673a357` |
| PostgreSQL test source | `07194463ac328104d1f5426cdcb4e7188c7979e55278b1609013e02de819cd5b` |
| Service test source | `3c22ff31df6378d883a1bc7e7f964b626e5a9b2a7b3925dc63c616db6ab4fffb` |
| Core DLL | `03a0d31ca2b5c09c00b1d0b6991109d0d97505a8b572c4cf4f1be4ce56c7b381` |
| PostgreSQL DLL | `f337aa873cb3e9cc211e90341c372d69ca3097fff5640e391a4138177e682a29` |
| Service DLL | `6d5436bdf358fd19a689d7d2e8cedd83c60756a8905965b37d2b34f2f6a67365` |

All three product DLLs report product version `1.2026.07.21.21.54.B` and file
version `1.2026.721.2154`.

## Completed-Source Snapshot

After the final Core fixture correction and continuity update, the canonical
versioned source candidate was captured separately from rollback snapshot A:

- Archive:
  `D:\.pegasus-cortex\snapshots\Pegasus-Cortex\1.2026.07.21.21.54.B\Pegasus-Cortex-1.2026.07.21.21.54.B.zip`
- Entries / bytes: `396` / `909482`.
- Archive SHA-256:
  `cbe0d77180f5466b30f5b2da691c5f920cf09e93fe22a2e9c24a572d40c1882c`.
- Snapshot-manifest SHA-256:
  `f304a4851529d331d6698baa4fbc201ae96d60285d0e2daa143218b914fee455`.
- Exclusions: `.git`, `bin`, `obj`, `node_modules`, `artifacts`, and
  `TestResults`; archive inspection found zero matching entries.
- Bound content: archive `VERSION.json` reports `1.2026.07.21.21.54.B`; the
  final Core fixture source SHA-256 is
  `1176c1e626518a01a879802af0638712c27ba954d741c9c9d31e97996673a357`;
  required source, docs, tests, and `.cortex` continuity entries are present.
- Retention: 10 verified roots remain. Rollback `21.54.A` is preserved; only
  oldest verified root `1.2026.07.15.07.11.A` was pruned.
- Final static continuity checks: version JSON/props agree; no merge markers or
  invalid JSON exist; Cortex validation reports no missing files or recovery-
  order gaps. Its 14 placeholder matches are known policy/generic-type notation.
  Active source/docs maximum path length is 153 with zero over 240.

The immutable archive necessarily predates the current external lines that bind
its own hash. Audit it with its adjacent generated manifest plus this current
checkpoint document; do not attempt to rewrite the snapshot.

## Limits And Next Gate

- No live or native PostgreSQL operation, migration execution, database change,
  legacy/offline-data mutation, deletion, package, upload, deployment, install,
  trust, service, hardware, MCP, or external publication occurred.
- The Nexus bus terminal for task `863bcc5241b3` was not changed or
  acknowledged. The active and inbox queues were empty at recovery.
- No package or release manifest was produced; `bin/` output is local build
  evidence only.
- PostgreSQL coverage is source-shape/provider-harness evidence, not native
  database execution. Backward-clock coverage uses the deterministic repository
  stub and does not assert host clock mutation.
- Nexus must independently inspect the correction and evidence before deciding
  whether to acknowledge the preserved terminal result.
