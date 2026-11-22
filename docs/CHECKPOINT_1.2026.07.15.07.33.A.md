# Pegasus Cortex Native ARM64 and MCP Acceptance 1.2026.07.15.07.33.A

Last updated: 2026-07-15

## Outcome

This checkpoint is the first accepted Cortex baseline executed on the native
Linux ARM64 Pegasus AI host. It validates the portable application with the
separately redistributable .NET runtime and closes the Linux descriptor,
ARM64 open-flag, and MCP graceful-cancellation defects found during host QA.

Canonical version: `1.2026.07.15.07.33.A`.

NuGet version: `1.20260715.733-a`.

The dependency remains exactly `Pegasus.Shared [1.20260711.421-a]`. No Shared
package was changed or replaced for this checkpoint.

The result is local, unsigned, validation-only, non-deployable, and
non-cutover. Host execution did not install or register a service and did not
contact PostgreSQL.

## Corrected Boundaries

- Linux open flags are selected at one architecture boundary. ARM64 uses its
  `O_DIRECTORY` and `O_NOFOLLOW` values; x64 retains its existing values.
- Low-numbered native descriptors are normalized with the architecture-
  specific `fcntl` syscall and `F_DUPFD_CLOEXEC`, with the source handle pinned
  for the complete call and close ownership kept explicit.
- Isolated native regressions close standard descriptors `0`, `1`, and `2`,
  then prove that every live journal-root descriptor is at least `3` and that
  disposal returns the root-targeting descriptor set to empty.
- The bounded MCP reader uses a cancellation-aware wait around readers whose
  underlying console or pipe operation may ignore its token. A cancelled read
  with an outstanding buffer owner poisons the reader against reuse, and a
  delayed underlying fault is observed without replacing the cancellation
  outcome.
- The MCP regression fixture deliberately holds an incomplete token-ignoring
  read, verifies bounded cancellation, rejects reuse, then releases and
  observes every fixture task.

The first ARM64 trace exposed x64 flag constants being interpreted as
`O_DIRECT`/`O_LARGEFILE` on ARM64. Later QA exposed that CoreCLR may reopen a
closed standard descriptor and that Linux stdin can remain blocked after
managed cancellation. The final tests assert Cortex ownership and observable
shutdown behavior instead of assumptions about CoreCLR's internal descriptors.

## Secure Source Snapshot

The exact build source is sealed at
`D:\.pegasus-cortex\snapshots\Pegasus-Cortex\1.2026.07.15.07.33.A\Pegasus-Cortex-1.2026.07.15.07.33.A.zip`.

- Archive bytes: 752099.
- Archive file entries: 270.
- Archive SHA-256:
  `fcfc73d4a16ddba60a641ad40579d337c00176c3623440b4b2bb4e2da5cd0c3d`.
- Snapshot-manifest SHA-256:
  `32fd41ff8fec01c941728f80b73c43543f616deb534ed0bc7e8a415ab85707a2`.
- Evidence-builder source binding: 216 of 216 inputs.

The snapshot contains file entries only; directory entries are reconstructed
from the validated relative paths. Build output, artifact, package-cache, and
test-output directories are excluded.

## Retained ARM64 Evidence

Evidence root:
`D:\.pegasus-cortex\evidence\1.2026.07.15.07.33.A`.

| Artifact | Bytes / entries | SHA-256 |
| --- | --- | --- |
| Cortex application ZIP | 1837603 / 40 | `23dea21f41c999e7d0c1451e578a254d802d27c1e5227c5b337520085995c9fa` |
| .NET 10.0.10 Linux ARM64 runtime ZIP | 33967232 / 202 | `721301a19dc3f807ced3a52db7d079ba5f8c038f8fb76422711e001629730413` |
| Release manifest | not applicable | `074549a374ca78e8107782c1140c48c8ba5e482eb19cd5f02721430db21fd053` |
| Retention marker | not applicable | `da9b77341e7feadbc3994fc5dea29787eed86da55d73b31baccbf4439d6417a9` |

The official runtime source archive is exactly bound by SHA-512
`3ea2ac626fdb0f26bb52f02584ae5e68ee88c275e740b34d93918ad380985d0dbfa1632c017658dfddc5fba396114b62748efc11b25897877814a4a4c5022edc`
and by Microsoft's .NET 10 release metadata.

## Verification

Local focused verification:

- MCP Release build: zero warnings and zero errors.
- MCP observable protocol harness: `25/25`.
- Sealed evidence builder: exit code `0`; exact source, package, runtime,
  archive, mode, checksum, ELF, path, retention, and serialization checks
  passed.

Contained non-root gx10 verification:

- Both ZIPs passed exact size/hash, CRC, creator-system, collision, path, file-
  type, and mode preflight before extraction.
- Application modes were exactly 40 regular files at `0644`. Runtime modes
  were exactly 186 regular files at `0644` and 16 at `0755`.
- Internal bundle checksums passed `38/38` for the application and `200/200`
  for the runtime.
- The private runtime reported .NET `10.0.10`; `dotnet`, `libhostfxr.so`, and
  `libcoreclr.so` were verified as little-endian 64-bit AArch64 ELF files.
- The complete Linux native harness passed `2/2`, including isolated standard-
  descriptor cases `0`, `1`, and `2`; maximum resident set was 44516 KiB.
- Tokenized `strace` proved the journal-root `openat2` returned descriptor `0`
  with exactly `O_RDONLY`, `O_NOCTTY`, `O_NONBLOCK`, `O_NOFOLLOW`,
  `O_CLOEXEC`, and `O_DIRECTORY`, plus `RESOLVE_NO_MAGICLINKS` and
  `RESOLVE_NO_SYMLINKS`. Cortex normalized ownership and the child exited `0`.
- The normal MCP session returned all 10 expected responses, performed bounded
  search/read and URI-denial checks, exited `0` at EOF, emitted no diagnostic,
  and opened no socket descriptor; maximum resident set was 57356 KiB.
- A 131073-byte line produced one bounded oversize diagnostic, drained through
  LF, accepted the following ping, and exited `0`; maximum resident set was
  56504 KiB.
- With stdin intentionally left open, `SIGINT` produced no additional protocol
  output or diagnostic and exited `0` in 0.007265 seconds; maximum resident set
  was 54964 KiB.
- TCP listener state was unchanged. There were no surviving task processes,
  open task socket descriptors, or journal files. The exact owner-only task
  root was then removed and its absence verified.

Sanitized host evidence is
the `sanitized-result.json` file beneath the protected `1.2026.07.15.07.33.A`
host-evidence root,
SHA-256
`ac2a671571c8acc81a42f07ff31abb12d429d192b6304b99f1b4fca3fe3a7dd3`.
The native-validation Nexus bus task was completed and acknowledged.

The first normal-session validator iteration used the superseded field name
`excerpt`; it was corrected to the current `contentExcerpt` contract and all
MCP sessions were restarted and rerun. No product code changed between those
two host validator iterations.

## Recovery and Promotion Rules

- `1.2026.07.15.07.13.A` is diagnostic only because its MCP process did not
  exit after `SIGINT` while stdin remained open.
- `1.2026.07.15.07.28.A` is an intermediate immutable rollback snapshot made
  before final cancellation-invariant review. It has no accepted release
  bundle or host claim.
- Use only the `07.33.A` snapshot and hashes above for this accepted source and
  ARM64 validation baseline.
- These checkpoint and continuity edits postdate the sealed source snapshot
  and are not among its 216 bound build inputs.
- Markerless staging residue `2c3945` remains untouched because it is not
  proven to belong to this completed build while its compiler may be active.

## Remaining Gates

1. Provide the host-owned journal key/protector, dedicated service identity,
   installation layout, and cooperating writer lock before service deployment.
2. Run the explicit native PostgreSQL migration, RLS, receipt, retry, restart,
   readiness, and backup/restore fixture under separate authority.
3. Add the sanctioned Nexus registration/ingress and signed caller context
   without moving Cortex storage or business logic into Nexus.
4. Build and verify signing, installer, upgrade, rollback, service-lifecycle,
   and uninstall paths before any deployment claim.

No PostgreSQL schema, account, certificate, trust store, firewall, listener,
service registration, package installation, upload, deployment, or cutover was
changed by this checkpoint.
