# Pegasus Cortex Rejected Validation Attempt 1.2026.07.14.19.21.A

Last updated: 2026-07-14

## Outcome

This checkpoint preserves diagnostics from the first local rehearsal and the
first snapshot-bound run of the Docker-free Cortex ARM64 evidence builder. The
restore, publish, deterministic archive, hash, and manifest checks completed,
but independent review later found that every ZIP central-directory entry used
creator system `0` (MS-DOS). Generic Linux extraction may therefore ignore the
stored Unix mode bits and drop required execute permissions.

Canonical version: `1.2026.07.14.19.21.A`.

NuGet version: `1.20260714.1921-a`.

The active suite dependency remained exactly
`Pegasus.Shared [1.20260711.421-a]`. All `19.21.A` artifacts are rejected and
not approved for restore, extraction, installation, deployment, or cutover.
They do not supersede packaged source checkpoint `1.2026.07.14.18.28.A`.

## Preliminary Rehearsal Diagnostics

The temporary preliminary rehearsal produced these exact diagnostic outputs:

| Artifact | Purpose | SHA-256 |
| --- | --- | --- |
| `pegasus-cortex-arm64-evidence-1.2026.07.14.19.21.A.zip` | RID-neutral Cortex validation payload | `46022408a9adb8c5d45d529bde6aa31b6efb75a91f43c9abc7c277c2cad6c757` |
| `pegasus-dotnet-runtime-10.0.10-linux-arm64-1.2026.07.14.19.21.A.zip` | Installer-managed Linux ARM64 .NET runtime dependency | `19156e3ba9a3e3ae53f39203dc7a8619290c308e508d4d14e7e8d00000b7ef2c` |
| `cortex-arm64-evidence-release-manifest.json` | Validation disposition and artifact binding | `7634eadb7ec419da0fcbb535bf275e8f330ef9160284bb54bac78ba0d81da669` |

These hashes identify only the preliminary rehearsal. They are retained for
diagnosis and must never be promoted into release or deployment metadata.

## Rejected Snapshot-Bound Diagnostics

The first snapshot-bound attempt used source snapshot
`D:\.pegasus-cortex\snapshots\Pegasus-Cortex\1.2026.07.14.19.21.A\Pegasus-Cortex-1.2026.07.14.19.21.A.zip`.
It contained 369 entries, was 782306 bytes, and had SHA-256
`0542e853e1abec1aebc0c22cf0445d7ea49a310b72393b36ef9b19cb4f687d28`.
Its snapshot-manifest SHA-256 was
`dec39412f5fbda2d16549df4fc415a2ee0811ed0848b7742c9421bfbf5141151`.

The rejected evidence root was
`D:\.pegasus-cortex\evidence\1.2026.07.14.19.21.A`:

| Artifact | Bytes / entries | Rejected diagnostic SHA-256 |
| --- | --- | --- |
| Cortex application ZIP | 1836002 / 40 | `854c080ef67e903bb9842e56355eac3fe8c2be85c46ba12295e07b6d30a08673` |
| Linux ARM64 runtime ZIP | 33967238 / 202 | `4299e5d75cd7e9cf4270be8456ea0367f7e8e3c72710fa9aa1abc3c2d27d498c` |
| Release manifest | not applicable | `2abe5283094cb1a636e5abe923d790d5d8b2ab5a2160853de2b63a1de0043e21` |

The source binding reported `verifiedBuildInputCount = 215`; owner-only and
no-reparse roots plus checksum verification passed. Those controls do not cure
the creator-system defect. The snapshot and artifact hashes above are retained
strictly as rejected diagnostics, not as an approved recovery or evidence set.

## What The Attempt Exercised

- Restore and publish used the builder's isolated process environment,
  protected offline package feeds, and isolated package cache.
- The application archive intentionally has an empty executable-path set. Its
  verifier still checks the exact evidence entry set, ordinal ordering,
  canonical timestamps, SHA-256 values, regular-file modes, and entry count.
- The Linux ARM64 runtime archive retains its nonempty executable-path mapping
  and corresponding executable-mode verification.
- Both archives passed byte-identical deterministic serialization checks before
  the release manifest bound their hashes, but the verifier did not yet require
  Unix creator system `3` in every central-directory entry.
- The manifest records `validation-only`, `unsigned`, deployment unauthorized,
  and `local_unsigned_non_deployable_non_cutover` disposition.

## Explicit Limits

Both attempts are unsigned and non-deployable. Neither executed Cortex or the
runtime on Linux ARM64 hardware. Neither contacted or executed PostgreSQL,
started a live Cortex service, exercised MCP over a live service boundary,
contacted Nexus, installed dependencies on a managed host, changed trust,
uploaded, deployed, cut over, imported persistent `.cortex` data, or authorized
cleanup.

## Superseding Correction

Accepted local validation checkpoint `1.2026.07.14.22.28.A` adds the
Cortex-local creator-system adapter while keeping Shared pinned unchanged and
supersedes every `19.21.A` artifact claim. See
`CHECKPOINT_1.2026.07.14.22.28.A.md`.

The rejected `19.21.A` preliminary rehearsal and evidence artifacts were
pruned. Its secure source snapshot remains only as rejected diagnostic and
recovery history for reconstructing the failed attempt; it is not approved as
the current build input, validation evidence, installation source, or
deployment source. Preserve every current `.cortex` source file and keep host
QA, signing, installation, and deployment behind separate explicit approval.
