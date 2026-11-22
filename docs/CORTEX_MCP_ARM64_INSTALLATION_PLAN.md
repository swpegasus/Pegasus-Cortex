# Historical Linux ARM64 deployment design — deferred

> **ARCHIVED DESIGN, NOT A CURRENT INSTALLATION PROCEDURE.** This document
> summarizes the August 7, 2026 standalone PostgreSQL/SSH-STDIO cutover plan.
> Its deployment tool cluster and direct-adapter route are retired. It does
> not qualify or offer a current Linux ARM64 product. Do not run the former
> prepare, activation, rollback, smoke or SSH-registration procedures.

For the maintained Windows x64 product, use the
[installation/build guide](BUILD_FROM_SOURCE.md) and
[shared MCP setup](CORTEX_SHARED_MCP.md). The current client route is shared
HTTPS under an admitted existing owner; this historical plan is not a remote
fallback, a certificate exception or permission to start another runtime.

## Original scope

The old proposal targeted a fresh, isolated PostgreSQL deployment on one Linux
ARM64 host. It did not import a Windows SQLite database, old machine identity,
credentials or ACLs. Its temporary operator SSH-STDIO exception and trusted
plaintext database-loopback assumptions belonged only to that proposed cutover.
They do not describe the current product's ingress, authentication or TLS policy.

The associated packaging, evidence-bundle and smoke-test tools have been removed
from the maintained release route. This summary retains the useful admission
and evidence questions without leaving copyable commands for the deleted tools.
Older hashes, release numbers, migration numbers and tool inventories cannot be
reassigned to new source or used as current acceptance results.

## Preserved design requirements

Any separately approved future ARM64 implementation would need a fresh design
and its own supported deployment route. The earlier plan identified these checks:

- Bind the source revision, architecture, runtime, package and exact immutable
  inventory together before constructing a release-specific activation plan.
- Establish native runtime execution separately from live PostgreSQL acceptance.
  An offline fixture result does not prove a database connection or migration.
- Keep migration and ordinary runtime roles separate. Prove the intended runtime
  privileges, protected-table denial, tenant/namespace isolation and concurrency
  behavior using a disposable database distinct from deployment data.
- Preserve existing release, database and configuration identities. Reject
  unexpected owners, links, modes, selector changes or package bytes rather than
  repairing or adopting them implicitly.
- Stage and prepare a candidate without selecting it. Activation must validate
  the prepared identity and original selector immediately before publication.
- Keep rollback scoped to the exact previously verified selection. Do not delete
  either database, credentials, configuration or prior evidence as a side effect.
- Give any remote delivery, account, service, network and registration changes
  their own explicit authority and verification. Local Windows qualification is
  not remote or Linux acceptance.

## Evidence limits and current status

The old plan referred to PostgreSQL migration `0007`, a 22-check offline fixture,
16 main tools and four auditor tools. Those were historical expected coordinates,
not current counts, fresh execution results or a supported MCP catalog today.

This document provides no current ARM64 package, deployed server, native hardware
run, PostgreSQL acceptance, remote-client connection or rollback proof. Reopening
that work requires a new bounded product decision, supported source and packaging,
and verification on the actual target. The current Windows release neither
starts nor provisions the deferred ARM64 system.
