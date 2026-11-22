# Pegasus Cortex Accepted Local Validation Evidence 1.2026.07.14.22.28.A

Last updated: 2026-07-14

## Outcome

This checkpoint records the accepted local, snapshot-bound validation evidence
for the corrected Cortex ARM64 archive flow. It supersedes every artifact claim
from rejected version `1.2026.07.14.19.21.A`.

Canonical version: `1.2026.07.14.22.28.A`.

NuGet version: `1.20260714.2228-a`.

The active dependency remains exactly
`Pegasus.Shared [1.20260711.421-a]`. Shared is unchanged; the narrow correction
is owned by the Cortex evidence builder.

## Rejected Predecessor

The first snapshot-bound `19.21.A` archives stored Unix mode bits but marked
their ZIP central-directory entries with creator system `0` (MS-DOS). Generic
Linux extraction may ignore those mode bits and remove executable permissions.
All preliminary and snapshot-bound `19.21.A` hashes are rejected diagnostics
only, as recorded in `CHECKPOINT_1.2026.07.14.19.21.A.md`.

## Cortex-Local Creator-System Adapter

Builder SHA-256:
`468130f3a9639713e5982d318a99afac2d69164d0a637e2f589f669ecbe05339`.

The builder now wraps the pinned Shared deterministic ZIP writer with one
Cortex-owned creator-system policy:

- It opens the archive exclusively and bounds the non-ZIP64 end-of-central-
  directory and central-directory ranges before mutation.
- It rejects empty, multi-disk, ZIP64, trailing-data, malformed-signature,
  unsupported creator-system, and out-of-bounds entry layouts.
- It accepts only creator systems `0` and `3`, normalizes every supported entry
  to Unix creator system `3`, flushes the file, and revalidates every entry.
- Both deterministic archive creation and evidence verification require the
  normalized entry count to equal the expected archive/evidence count.
- Existing exact entry, order, timestamp, content-hash, and Unix mode checks
  remain in force. Runtime executable mappings remain nonempty; the application
  archive intentionally retains its empty executable mapping.

This local adapter is intentionally narrower than changing Shared during an
evidence correction. A future Shared owner may expose an equivalent versioned
contract through a separately reviewed and adopted package.

## Secure Source Snapshot

The exact build source is sealed at
`D:\.pegasus-cortex\snapshots\Pegasus-Cortex\1.2026.07.14.22.28.A\Pegasus-Cortex-1.2026.07.14.22.28.A.zip`.

- Archive bytes: 787942.
- Archive entries: 370.
- Archive SHA-256:
  `368a48e4f46f7e6517b01a16b2ba402f74f6f033eb1b0953c5804d5ec776e1d5`.
- Snapshot-manifest SHA-256:
  `b97176152a92840873cd5a67bb0b101f1bca6a3a00fa2208ae8d5c90d761cd40`.
- Source binding: 216 of 216 verified build inputs, including builder SHA-256
  `468130f3a9639713e5982d318a99afac2d69164d0a637e2f589f669ecbe05339`.

The snapshot and final evidence roots are owner-only and no-reparse. Snapshot
inventory, checksum, containment, and source-binding verification passed.

## Final Retained Evidence

Final evidence root:
`D:\.pegasus-cortex\evidence\1.2026.07.14.22.28.A`.

| Artifact | Bytes / entries | SHA-256 |
| --- | --- | --- |
| Cortex application ZIP | 1836036 / 40 | `ba22583e33a5af4fab4b5dc7a2db44d1b7fcc6f5c76f38707afe85b112112031` |
| Linux ARM64 runtime ZIP | 33967241 / 202 | `4409d25c3cda80af863a4ddd393f891dc3970eaa7da201e1880fb60f933b7b1d` |
| Release manifest | not applicable | `cd6febb19dad2f79b1b3e7a23fe08e80d8231e9c007d37c660c80e52fc76996b` |
| Retention marker | not applicable | `af6356c8343d159776ad2bc407966b049c792a10f28a876457eb1a30a1d2fdf4` |

## Independent Acceptance Gate

- Windows PowerShell 5.1 parsing reports zero errors.
- Focused creator-system normalization, rejection, and validation probes pass.
- The builder file matches the SHA-256 recorded above.
- Every one of the 40 application and 202 runtime central-directory entries has
  Unix creator system `3`.
- Application modes are exactly 40 regular files at `0644`. Runtime modes are
  exactly 186 regular files at `0644` and 16 executable files at `0755`.
- Independent `bsdtar` extraction reports the executable files as
  `rwxr-xr-x`.
- Archive checksums, paths, ELF AArch64 identity, owner-only ACLs, no-reparse
  boundaries, retention state, and staging cleanup all pass.

The earlier `19.21.A` rehearsal and evidence artifacts were pruned. Its secure
snapshot remains only as rejected diagnostic/recovery history.

These checkpoint and continuity edits were written after the secure source
snapshot and are not inputs to the already sealed artifacts. The 216-input
binding above is the authoritative build-input set.

## Remaining Host Gates

1. Keep native Linux ARM64 execution and service lifecycle QA separately
   authorized; archive extraction and ELF identity checks are not execution.
2. Keep live PostgreSQL, live Cortex service/MCP, signing, installation, trust,
   upload, deployment, cutover, persistent migration, and cleanup unperformed
   until separately approved.
3. Leave markerless compiler residue `2c3945` untouched while an unrelated
   `VBCSCompiler` process remains active; prune only after separate ownership
   and inactivity proof.

This checkpoint is accepted local validation evidence. It remains unsigned,
validation-only, non-deployable, and non-cutover. No Cortex or .NET runtime was
executed on ARM64 hardware, and no live PostgreSQL, install, upload, or
deployment action occurred.
