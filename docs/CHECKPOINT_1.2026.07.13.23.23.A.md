# Pegasus Cortex Checkpoint 1.2026.07.13.23.23.A

Last updated: 2026-07-13

## Outcome

This checkpoint implements the bounded Phase B learning-feedback application
slice without starting the production database or service. `Pegasus.Cortex.Core`
now owns proposal submission, independent review, bounded namespace reads, and
status projection behind an implementation-independent repository interface.
`PegasusCortex.Persistence.Testing` supplies a deterministic, concurrency-safe,
non-production implementation for application conformance tests.

Canonical release: `1.2026.07.13.23.23.A`.

NuGet version: `1.20260713.2323-a`.

The active dependency remains exact `Pegasus.Shared [1.20260711.421-a]`.

## Recovery Point

Before material edits, the source-only rollback snapshot was created at
`D:\Codex Projects\_snapshots\collections\Pegasus-Cortex\1.2026.07.13.22.57.A\Pegasus-Cortex-1.2026.07.13.22.57.A.zip`.
It contains 106 files and 800114 uncompressed source bytes. All 106 archive
entries were matched byte-for-byte to their source files with zero hash
mismatches. Archive SHA-256 is
`7566cc5fa863ca0320c780c79898e2fbe8e5ebdc0100d406759827cc93ff532e`.
Generated `bin`, `obj`, `artifacts`, and `.git` content was excluded.

## Application And Repository Boundary

- Commands, queries, results, bounded page/status projections, and metadata-only
  audit receipts are separate from `ILearningFeedbackRepository`.
- Submission calls the existing Contracts policy, then atomically adds an
  immutable proposal. The same proposal ID and canonical proposal hash is an
  idempotent success; the same ID with a different hash is a conflict and never
  overwrites the original.
- Review lookup is authorized from server-derived caller context before storage
  access. A decision must match the exact tenant, namespace, authority-qualified
  reviewer, policy revision, proposal ID, and proposal hash. Only an unreviewed
  proposal can receive its first immutable decision; exact retries are
  idempotent and every different later decision conflicts.
- Read and status queries require an explicit `CanRead` namespace grant. Submit,
  review, and read capabilities are independently default-deny. Serialized
  proposals, decisions, repository rows, and audit metadata create no authority.
- Pagination is deterministic by ordinal proposal ID, exclusive by continuation
  ID, limited to 100 items, cancellation-aware, and defensive against adapters
  that lie about collection count or return cross-scope data.
- Repository values are bounded, deep-copied, scope-checked, and canonical-hash
  verified before the application returns them. The testing repository also
  snapshots writes and reads so caller mutations cannot change stored state.
- Diagnostics and audit metadata contain operation, disposition, bounded IDs,
  hashes, namespace, and server-derived actor metadata. They exclude summaries,
  payload fields, evidence bodies, review reasons, raw conversations, terminal
  streams, credentials, and private reasoning.

## Contract Corrections

- Accepted intended-use and evidence collections now use read-only wrappers,
  closing the prior array-cast mutation path after hashing.
- Reviewer independence compares the authority-qualified principal pair
  `(AuthorityId, SubjectId)` instead of treating a subject identifier as global.
- Namespace grants add an optional explicit `CanRead` capability while retaining
  source compatibility for existing submit/review constructors.
- Diagnostic identifiers are omitted when they exceed their bounded contract,
  preventing malformed caller values from becoming unbounded diagnostic data.

## Verification

- A clean 16-project Release solution build completed with zero warnings and
  zero errors.
- `PegasusCortex.Contracts.Tests`: `8/8` passed.
- `PegasusCortex.LearningFeedback.Tests`: `8/8` passed.
- `PegasusCortex.Core.Tests`: `12/12` passed.
- Focused learning-feedback coverage includes submit/replay/conflict, input and
  output mutation, review independence and immutability, namespace denial,
  cancellation before and during output processing, bounded paging/status,
  concurrent submit/review races, malformed/cross-scope repository values,
  lying collection counts, and audit-content exclusion.
- Independent source review closed with no blocking findings after the adapter
  echo, deep-snapshot, required-ID, count, cancellation, correlation, and checked
  status fixes.

## Trust Boundary And Remaining Work

This slice verifies bounded shape, exact scope, and self-consistent canonical
hashes at repository outputs. It does not yet authenticate durable rows with a
signature or re-run the complete original authority/screening decision against a
malicious repository that can rewrite content and recompute hashes. The
PostgreSQL phase must define transaction isolation, row-level scope defense,
storage integrity/authenticity, migrations and rollback, backup/restore, and
corruption behavior before the repository becomes a production trust boundary.

There is no PostgreSQL provider, migration, publication workflow, authenticated
remote MCP/API transport, Nexus registration, Coder adapter, model provider,
training job, deployment, upload, installation, certificate, trust-store, or
production-data mutation in this checkpoint. `PegasusCortex.Persistence.Testing`
is intentionally non-packable and makes no durability claim. Local package
creation is unsigned recovery/build evidence only and does not authorize
installation or deployment.
