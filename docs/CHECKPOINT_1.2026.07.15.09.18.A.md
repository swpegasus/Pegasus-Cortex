# Pegasus Cortex Checkpoint 1.2026.07.15.09.18.A

Date: 2026-07-15

Status: accepted local baseline; unsigned, validation-only, non-deployable, and
non-cutover. The first contained external ARM64 PostgreSQL rehearsal failed
closed during dependency acquisition and is not part of this checkpoint's
accepted evidence. A separate package-discovery task is queued.

## Purpose

This checkpoint adds the smallest service-lifecycle and native PostgreSQL
backup/restore verification boundaries needed before a persistent Cortex
service is attempted. It preserves exact `Pegasus.Shared [1.20260711.421-a]`
and the existing product boundaries: Pegasus Server owns installation and
service supervision, Nexus owns external ingress and identity, and an
authorized operator owns native PostgreSQL command provenance.

## Source Changes

- `PegasusCortex.Service` now has a validation-only lifecycle rehearsal. It
  emits a flushed ready marker, handles console cancellation and `SIGTERM`
  without polling, emits a deterministic stop marker, and opens no transport,
  database, key store, or product data.
- The native PostgreSQL fixture now supports a capture proof and a distinct
  `verify-restored-state` workflow. The verifier proves state equivalence only;
  it does not claim that `pg_dump` or `pg_restore` ran.
- Proof files require a canonical existing trusted root plus one bounded safe
  `.json` leaf. The boundary enforces containment, a 240 UTF-16-character final
  path budget, reserved-name/ADS/trailing-normalization denial, symlink/reparse
  denial, Unix root mode `0700`, and proof/temp mode `0600`.
- The ARM64 evidence bundle exposes four fixed entrypoints: Linux native
  filesystem validation, PostgreSQL fixture, isolated MCP stdio, and service
  lifecycle rehearsal.
- The production service plan now specifies a Docker-free Linux ARM64
  application archive, a separately versioned redistributable .NET runtime
  bundle, and an external PostgreSQL dependency selected by Pegasus Server.

## Verification

- Full `Release` solution build: 0 warnings, 0 errors.
- PostgreSQL fixture offline harness: 14/14 passed; live integration remained
  opt-in and skipped locally.
- Service harness: 10/10 passed, including normal lifecycle, shutdown before
  readiness, and bounded output-failure diagnostics.
- Missing proof in restored-state mode failed with configuration exit code 2
  before certificate or network access.
- Independent review passed both prior blockers: the proof path boundary is
  fail-closed, and all names/docs leave native backup/restore provenance with
  the external operator.
- PowerShell evidence-builder AST parse passed; all 12 schema JSON files parsed.
- Maximum current source/test/tool/doc/schema path is 153 UTF-16 characters;
  the complete retained project tree remains within the 240-character policy.

## Immutable Source And Artifacts

Protected source snapshot:

- archive:
  `D:\.pegasus-cortex\snapshots\Pegasus-Cortex\1.2026.07.15.09.18.A\Pegasus-Cortex-1.2026.07.15.09.18.A.zip`
- bytes / entries: 821430 / 378
- SHA-256:
  `4d103498e757f6fb7c2da9256641bd3bedfd278342834550960a39cc956f7101`
- snapshot-manifest SHA-256:
  `40349b34bb4cb0b146f5dba1fca5a30aefbc55031c8f530b114c236f2c14e253`
- protected snapshot retention: exactly 10 verified version roots

ARM64 validation evidence:

- application ZIP: 2897303 bytes / 55 entries / SHA-256
  `738ce3f5c1adbaedf6ffc21a22ec7e5d11ecd2e465035b95573094882b79434f`
- .NET 10.0.10 Linux ARM64 runtime ZIP: 33967235 bytes / 202 entries /
  SHA-256
  `52a6b89d9b097aa10b5f7b62aca2801bdf3c9d3a69b65be160d4fc52713cc231`
- release manifest SHA-256:
  `7a10a60d942126a4ed91f507a786bca9fd8226b4928f6cb090e793e664e911fd`
- exact verified build inputs: 222
- disposition: `local_unsigned_non_deployable_non_cutover`
- deployment authorized: false

These checkpoint and Cortex continuity edits postdate the immutable source
snapshot and are documentation only; they are not hidden build inputs.

## Remote Rehearsal Status

The contained Nexus rehearsal task, correlation
`cortex-1.20260715.918-a-arm64-rehearsal`, was claimed by the existing
external operations agent and failed closed before application or PostgreSQL
execution. Exact Ubuntu ARM64 pins for `postgresql-16`,
`postgresql-client-16`, and `libpq5` at `16.13-0ubuntu0.24.04.1` returned HTTP
404. The separately pinned `libllvm17t64 1:17.0.6-9ubuntu1` downloaded and
matched SHA-256
`aa1c6daed97b7579ccc2779b43b53adc16ac5efb187f5379bb1b8f897c742b3e`.
The agent did not substitute packages.

Application/runtime hashes and archive preflight passed before the failure. No
Cortex entrypoint, PostgreSQL process, listener, certificate, credential, role,
database, dump, restore, restart, or synthetic-data operation ran. The exact
remote task root was removed; process/listener counts were zero and credential
cleanup was reported. The protected canonical receipt is
recorded beneath the protected `1.2026.07.15.09.18.A` remote-evidence root,
4001 bytes, SHA-256
`e711e7a3e8fe5d7506681f45dcc72ac87e5607bd11d84acbe5f390ee03e6868d`.
The receipt and owner-only ACL were independently checked, and the failed task
was acknowledged through the Nexus bus.

Current official signed Noble ARM64 metadata independently identifies the
three PostgreSQL packages at `16.14-0ubuntu0.24.04.1`; it no longer lists
`16.13`, and the old pool objects return 404. This is candidate repository
evidence only. Host-specific compatible dependency closure and extracted-tree
`ldd` proof remain assigned to the separate package-discovery task. That task
is queued to the existing Operations identity and grants no application run,
install, or deployment authority.

| Candidate package | Exact version | Bytes | SHA-256 |
|---|---|---:|---|
| `postgresql-16` | `16.14-0ubuntu0.24.04.1` | 15345580 | `411d695c730e27403d8ca2bb47790b85b243cf2395adf4c1e0e0bd5467a2f597` |
| `postgresql-client-16` | `16.14-0ubuntu0.24.04.1` | 1291948 | `0021a3d7b3934205662f644f718a606c696a951e55adcccd7f33fb4128873b18` |
| `libpq5` | `16.14-0ubuntu0.24.04.1` | 144566 | `a7bf34f910180de364da8e9aa1ede194d32540fd0fc7465d128adb243c6ee7d2` |
| `libllvm17t64` | `1:17.0.6-9ubuntu1` | 25034872 | `aa1c6daed97b7579ccc2779b43b53adc16ac5efb187f5379bb1b8f897c742b3e` |

## Recovery And Next Gate

Use the protected `09.18.A` source snapshot for rollback. Do not treat the
application/runtime archives as installable releases because they are unsigned
and `deploymentAuthorized=false`.

Next, let the existing external operations thread claim the package-discovery
task. Accept its proposed immutable lock only after checking signed
metadata, exact package/control identities and hashes, host-compatible
dependency closure, extracted-tree `ldd`, unchanged system apt state, receipt
integrity, and zero residue. Only then queue one corrected contained rehearsal;
do not reuse or silently rewrite the failed task. The corrected rehearsal must
still prove distinct database OIDs, native dump/restore provenance,
restored-state equivalence, service/MCP/native markers, secret cleanup, and
zero residue. A persistent service install still requires a healthy supervisor,
dedicated identity, host-owned keys, signed artifacts, rollback/uninstall proof,
and separate deployment authority.
