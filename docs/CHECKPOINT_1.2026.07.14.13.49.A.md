# Pegasus Cortex Checkpoint 1.2026.07.14.13.49.A

Last updated: 2026-07-14

## Outcome

This checkpoint adds the first exact-driver PostgreSQL service composition
without coupling the portable persistence provider to Npgsql and without using
Docker. The service boundary owns driver configuration, TLS policy, connection
pool limits, short-lived migrations, stateful readiness admission, and
secret-free diagnostics. A separate opt-in fixture can exercise an externally
owned native PostgreSQL endpoint, but no database was contacted in this
checkpoint.

Canonical release: `1.2026.07.14.13.49.A`.

NuGet version: `1.20260714.1349-a`.

The active Pegasus dependency remains exact
`Pegasus.Shared [1.20260711.421-a]`. The new external driver dependency is exact
`Npgsql [10.0.3]` and is confined to the Service and integration-test surfaces.

## Recovery Point

Before material edits, the source-only rollback snapshot was created at
`D:\Codex Projects\_snapshots\collections\Pegasus-Cortex\1.2026.07.14.13.02.A\Pegasus-Cortex-1.2026.07.14.13.02.A.zip`.
It contains 218 entries and 1583833 uncompressed source bytes. The 439781-byte
archive and its manifest were independently reopened; all source hashes and
lengths matched. Archive SHA-256 is
`fe088bbef59df56dd823c186ee3327b7953d98f8ce14ebd9f83fa958c9d7b33f`.
Snapshot retention remains bounded to ten product-named versions.

## Service Composition

- `PegasusCortex.Service` is a net10 executable composition boundary. Its
  static status command reports that the contract is available but runtime
  configuration is absent; it does not invent database credentials, a row-
  authentication key, or production readiness.
- The portable `PegasusCortex.Persistence.PostgreSql` project still depends on
  BCL `DbDataSource` rather than Npgsql. This preserves a small replaceable
  provider contract for other hosts and platforms.
- Runtime and migration data sources use stable names and separate PostgreSQL
  roles. The runtime pool defaults to minimum zero, maximum eight, bounded to
  1-32, with idle pruning and a one-hour connection lifetime. Migration uses a
  separately disposed non-pooled data source with one connection and a bounded
  command timeout.
- Normal operation requires `VerifyFull`, an existing bounded PEM trust anchor,
  certificate revocation checking, and no trust-server-certificate bypass.
  Detailed errors, parameter logging, persisted security information, ambient
  enlistment, multiplexing, and GSS encryption are disabled at this boundary.
- The trust-anchor reader limits input to 1 MiB, parses UTF-8 PEM certificates,
  rejects private-key material, disposes parsed certificates, and clears the
  mutable input buffer. Configuration exceptions preserve stable operation and
  cause-type diagnostics without retaining secret-bearing provider messages.
- Explicit `LocalLoopbackBootstrap` accepts only literal `127.0.0.1` or `::1`,
  removes TLS/client-key settings, and can never open normal admission. The
  reserved `Pinned` mode fails closed until a complete pin lifecycle exists.
- A new composition starts `AwaitingReadiness`. Query and learning adapter
  creation is denied until the stateful probe returns a real `Ready` result.
  An overlap invalidates the earlier probe and is rejected. Cancellation,
  failure, `Degraded`, bootstrap, a poisoned data source, and disposal all keep
  admission closed.
- The host must provide `ICortexRowAuthenticator` from protected key custody.
  The service creates no test key or fallback authenticator for production.

## Native Integration Fixture

The integration executable always runs seven offline boundary checks. Live
work requires the exact `PEGASUS_CORTEX_PG_INTEGRATION=1` opt-in plus separate
migration/runtime connection strings. Both endpoints must use literal loopback
IP addresses, one `ctx_it_` database, distinct roles, `VerifyFull`, and the same
canonical fully qualified CA path.

When explicitly enabled against a freshly provisioned external database, the
fixture is designed to verify two concurrent Service-owned migrators, exactly
one migration owner, sequential idempotency, the exact migration ledger,
runtime TLS and restricted-role observations, and fail-closed readiness for the
newly migrated but unseeded store. It never creates or deletes the database and
requires a new pristine fixture database for a repeat run.

## Redistributable And Installation Boundary

Docker and Testcontainers are not part of the source, tests, or intended
deployment. The preferred release shape is a self-contained Cortex
`linux-arm64` application, with native PostgreSQL represented separately as an
installer-managed dependency bundle. Pegasus Server owns approved acquisition,
signature/hash/SBOM/license verification, reference counting, rollback, probe,
and uninstall cleanup. Nexus selects the compatible profile; neither Cortex
nor Pegasus Shared downloads or installs the database.

An already installed compatible PostgreSQL service may be reused only after the
same version, TLS, role, migration, RLS, and readiness probes pass. A future
native bundle must use the existing Pegasus deployment-manifest dependency
contract and include exact per-RID evidence before it can be called
redistributable or deployable. This checkpoint downloaded or installed no
PostgreSQL server runtime and produced no application deployment package.

The local dependency feed contains the exact Npgsql package and its two exact
Microsoft abstractions. Three new lock files retain package content hashes.
PostgreSQL/MIT license texts, Microsoft third-party notices, and a source/hash/
commit provenance record are copied into the Service output.

## Verification

- The 21-project Release solution build completed with zero warnings and zero
  errors.
- `PegasusCortex.Service.Tests`: `7/7` passed, including redaction, bounded TLS
  defaults, loopback bootstrap, readiness admission, poison/disposal behavior,
  a 32-factory lifecycle race, and the configuration-free executable boundary.
- `PegasusCortex.Persistence.PostgreSql.IntegrationTests`: `7/7` offline checks
  passed. Live work was skipped by the absent exact opt-in, so no database or
  network connection was opened.
- `PegasusCortex.Persistence.PostgreSql.Tests`: `12/12` passed, preserving the
  driver-independent provider contract.
- JSON/XML parsing, exact package and notice hashes, output dependency/license
  presence, Npgsql confinement, Docker/Testcontainers absence, and the static
  composition-status smoke passed.
- Maximum active source path was 153 characters. The complete retained tree,
  including historical artifacts, measured 232 characters; both remain below
  the 240-character Windows policy.
- Independent review found no checkpoint blocker after the credential-
  retention, readiness-admission, and native-fixture contract corrections.

## Known Limits And Next Gate

The following bounded items are deliberately not claimed complete:

- Runtime composition and migration-session creation still accept one combined
  secret-bearing options object, although neither stores it after construction.
  Split runtime and migration option contracts before separate-process
  deployment so each process receives only its own credential.
- Adapter factories check admission at creation time. A previously returned
  adapter is not revoked by a later readiness probe, so the future request host
  must enforce admission per scheduled operation or add an admission-aware
  wrapper before it caches adapters.
- Six older package-consuming projects have exact-looking package pins but do
  not yet use lock files. The new Npgsql slice itself is exact and locked.
- The native fixture proves a pristine migrated store remains `NotReady`; a
  positive live `Ready`/`Accepting` path awaits the deterministic authenticated
  Default Library import and index-generation workflow.

There is no live PostgreSQL server proof, role/grant bootstrap executor,
authenticated publication writer, durable seed, positive readiness proof,
query-plan measurement, backup/restore drill, signed ARM64 artifact, deployment,
upload, installation, certificate/trust-store mutation, Nexus registration, or
production-data mutation in this checkpoint. Local evidence is unsigned and
non-deployable.
