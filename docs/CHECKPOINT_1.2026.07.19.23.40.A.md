# Pegasus Cortex Checkpoint 1.2026.07.19.23.40.A

Date: 2026-07-19

Status: accepted local validation baseline; unsigned, nondeployable, non-cutover,
and not externally published. Nexus task `aa73f6438839` authorized only the
contained source, build, focused test, snapshot, package, and continuity work
recorded here.

## Purpose

This checkpoint closes the private-PKI revocation-evidence contract required by
the native PostgreSQL fixture without weakening certificate-chain or hostname
validation. The connection path still forces Npgsql `SslMode=VerifyFull`, the
exact private-CA root, `CheckCertificateRevocation=true`, and
`Trust Server Certificate=false`.

Production certificate issuance, CRL or OCSP publication, rotation,
availability, and revocation remain owned by the authorized platform operator
outside the Cortex runtime. The new CRL responder is test-only, task-local,
literal-loopback, bounded, cancellation-aware, and deterministically disposed.

## Contract And Source

- Profile: `pegasus.cortex.postgresql.private-ca-crl/1`.
- New takeover contract:
  `docs/POSTGRESQL_PRIVATE_PKI_REVOCATION_V1.md`.
- New language-neutral schema:
  `schemas/postgresql-private-pki-revocation.v1.schema.json`.
- The native fixture accepts an owner-only evidence root and one safe manifest
  leaf. It binds public root/server certificate and signed CRL bytes by
  SHA-256, rejects private-key material, validates exact literal IP SANs,
  validates one exact CRL distribution point, verifies the CRL issuer and
  signature, enforces freshness and a seven-day maximum interval, and rejects
  a revoked server serial before network work.
- The fixture responder permits one exact CRL GET path, a 4 KiB header, at most
  64 requests, at most 1 MiB per response, a 100-5000 ms timeout, no proxy,
  no polling, and no persistent service composition.
- The ARM64 bundle now includes the operator guide, schema, and
  `postgresql-rehearsal-v3-requirements.json`. V3 accounts for two distinct
  loopback endpoints: PostgreSQL TLS and CRL HTTP.
- Private test keys remain memory-only and never enter the evidence root,
  archive, manifest, receipt, or continuity.
- Exact dependencies remain `Pegasus.Shared [1.20260711.421-a]` and Npgsql
  `[10.0.3]`; no dependency was installed or upgraded.

## Verification

- One final non-incremental full-solution Release build for `23.40.A` passed
  with 0 warnings and 0 errors.
- The offline native PostgreSQL harness passed `21/21`. Observable cases cover
  fresh unrevoked success and bounded responder cleanup; missing or unreachable
  evidence; stale CRL; invalid CRL signature; revoked server certificate; SAN
  mismatch; chain mismatch; and the existing role, TLS, proof, privilege, path,
  and no-overwrite boundaries.
- A prior combined negative-case run exposed one transient unclassified
  `ArgumentException`. The accepted fixture removes its unnecessary random
  certificate-serial input, assigns each negative case a distinct observable
  test, and the final `21/21` run passed. Five intervening combined runs also
  passed; no failed output was promoted as evidence.
- The evidence builder passed its protected local locked restores, portable
  framework-dependent publishes, ArchiveTool checks, deterministic ZIP
  comparisons, exact source/snapshot comparison, sensitive-name scan, path
  budget, commit re-read, and retention checks.
- Independent archive inspection found all three private-PKI contract files,
  no sensitive-looking entry name, v3 with exactly two endpoints,
  `VerifyFull`, revocation enabled, and
  `cortexRuntimeOwnsResponder=false`.
- Maximum application-bundle relative path is 96 characters. Maximum active
  source/test/tool/doc/schema absolute path remains 153 UTF-16 characters.

## Immutable Source And Artifacts

Final protected source snapshot:

- path:
  `D:\.pegasus-cortex\snapshots\Pegasus-Cortex\1.2026.07.19.23.40.A\Pegasus-Cortex-1.2026.07.19.23.40.A.zip`
- bytes / entries: 849627 / 384
- SHA-256:
  `43748c22429d98fc5dcc145e58cc5185a517f62645b45d923ab3e00d31127d3d`
- snapshot-manifest SHA-256:
  `efa6c7071091f9c6b473519aaeb3ad3143e4506489110d3f43b7abfb0d06e2fe`
- exact verified build inputs: 228
- snapshot retention: exactly 10 verified version roots

Final ARM64 validation evidence under
`D:\.pegasus-cortex\evidence\1.2026.07.19.23.40.A`:

- application ZIP: 2925514 bytes / 58 entries / SHA-256
  `83ab89b46cb90cfeb59e53eea0c33511b7ae3f8fd040d4cdc481c598261a1ceb`
- .NET 10.0.10 Linux ARM64 runtime ZIP: 33967238 bytes / 202 entries /
  SHA-256
  `2efd0cf75d4e5dc9a576362aad84325ffb53cca6593a7c5cc54f71b5c25374f3`
- release manifest: 6858 bytes / SHA-256
  `77f13a9df0ab51dca2f084e1be968dcaf56c3c1741b7f8f03854ce21a113f0a9`
- disposition: `local_unsigned_non_deployable_non_cutover`
- deployment authorized: false
- evidence retention: 9 verified version roots, below the cap of 10

The sealed source snapshot intentionally predates this checkpoint document and
the `.cortex` closeout. Those files are recovery metadata, not hidden build
inputs.

## Recovered Attempts And Retention

- Pre-change rollback snapshot `1.2026.07.19.23.08.A` remains protected with
  278 entries / 787265 bytes / SHA-256
  `5a254370895d055dfcabf6bf1eb99d0c5d62eef7d414bfe1879baabbe503f993`.
- The first shared snapshot invocation for `23.28.A` failed closed in `tar`
  before publication; its retry produced a verified snapshot. A later builder
  preseal detected that the new nested evidence directory inherited instead of
  owning a protected ACL. No `23.28.A` evidence root was published.
- Builder ACL handling was corrected, and `23.33.A` sealed successfully. It is
  preserved as superseded unsigned historical evidence because its combined
  negative test did not isolate the transient setup failure.
- `23.40.A` is the only current checkpoint. Its source makes test serial input
  deterministic and separates every revocation rejection case.
- The builder removed every staging root that it marked and owned. One older
  markerless staging directory remains outside this task's ownership and was
  not deleted; markerless residue is not assumed safe to remove.

## Remote Evidence And Next Gate

The protected prior v2 receipt remains outside the active Cortex product
baseline, 5216 bytes, SHA-256
`0946ead09a6b3ebb733685106e4237f3482fa6f4597f2de6b3b2553a6e5c6d7c`.
It proves that the old environment failed closed with
`cortex_postgresql_private_ca_revocation_unavailable`; it does not prove v3,
PostgreSQL readiness, backup/restore, service readiness, install, or deployment.

The next separately authorized native ARM64 v3 rehearsal must provision both
loopback endpoints, bind the certificate CDP to the exact CRL endpoint, retain
public manifest/CRL hashes and bounded responder evidence, prove final Npgsql
pre-authentication with `VerifyFull` plus revocation, and prove zero process,
listener, credential, and root residue. Signing, trust-store changes,
installation, upload, deployment, live service access, hardware mutation, and
production-data mutation remain separate approval gates.
