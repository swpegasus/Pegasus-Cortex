# Pegasus Cortex Checkpoint 1.2026.07.13.22.42.A

Last updated: 2026-07-13

## Outcome

This checkpoint starts the first bounded Phase B compatibility slice without
starting the production database or service. It adds a passive `net8.0`
`Pegasus.Cortex.Contracts` package and language-neutral schemas for sanitized,
review-gated learning-feedback proposals and decisions. The contracts support
future retrieval, evaluation, fine-tuning, and distillation preparation; they
do not train, deploy, publish knowledge, contact a provider, or grant runtime
authority.

Canonical release: `1.2026.07.13.22.42.A`.

NuGet version: `1.20260713.2242-a`.

The active dependency remains exact `Pegasus.Shared [1.20260711.421-a]`.

## Recovery Point

Before material edits, a source-only snapshot was created at
`D:\Codex Projects\_snapshots\collections\Pegasus-Cortex\1.2026.07.13.22.22.A\Pegasus-Cortex-1.2026.07.13.22.22.A.zip`.
It contains 142 archive entries and 96 files totaling 248873 bytes. All 96
archived file hashes matched the live pre-change sources. Archive SHA-256 is
`0e267a3bac646e9509a51a564d3ee08a9ba966080f003207023ec2d647839900`.

## Contract Boundary

- `learning-feedback-proposal.v1` carries authority-qualified provenance,
  tenant and namespace, sanitized bounded payload fields, intended uses,
  verified evidence references, trusted screening claims, review state,
  canonicalization, and deterministic hashes.
- `learning-feedback-review-decision.v1` binds an independent reviewer to one
  exact pending proposal and proposal hash. The submitting subject cannot
  approve or reject its own proposal.
- Server-derived authority is separate from serialized feedback. Namespace
  names, screening claims, records, prompts, and model output cannot create a
  grant.
- Payloads and diagnostics exclude credentials, raw transcripts/chat/terminal
  streams, live-thread references, provider credentials, and private chain of
  thought. Accepted values remain explicitly non-executable untrusted data.
- Collections, identifiers, strings, serialized bytes, and authority snapshots
  are bounded before hashing. Cancellation is observed. Safely bounded rejected
  submissions retain a canonical submitted-payload hash; malformed or oversized
  shapes that cannot be hashed safely leave it absent.
- Fine-tuning and distillation intent requires an explicit trusted
  `training_and_evaluation` rights screening. A proposal and review decision are
  still not dataset-export, training-job, deployment, or publication authority.

## Cohesive Reuse

The package owns the provider-neutral Pegasus canonical JSON implementation.
`Pegasus.Cortex.LearningExports` retains its public `LearningExportJson` facade
but delegates to the shared contracts serializer. Existing learning-export
factory behavior was preserved in its own responsibility-focused source file.

## Verification

- `PegasusCortex.Contracts.Tests` Release build: zero warnings and zero errors.
- Contracts behavior harness: `8/8` passed, covering deterministic hashes,
  default-deny authority, prohibited content, intended-use rights, independent
  review, malformed/oversized boundaries, cancellation, and schema/runtime
  parity.
- Directly affected `PegasusCortex.LearningExports.Tests` Release build: zero
  warnings and zero errors.
- Learning-export regression harness: `7/7` passed with canonical identities and
  plan-only behavior preserved.

Final solution, package, package-content, and continuity validation evidence is
recorded in the handoff report for this checkpoint.

## Thread And Product Boundary

The Brain-Coder live-thread exception remains outside Cortex. Nexus owns one
durable user grant that remains permanent until explicitly revoked. Cortex has
no live-thread discovery, dereference, transfer, steering, mutation, approval
registry, or proxy behavior, and no live-thread source was contacted.

The external system-management product remains excluded from the Cortex baseline. No live
service, deployment, upload, installation, certificate, trust-store, provider,
model-training, or production-data action occurred.

## Remaining Work

This checkpoint does not add durable proposal storage, PostgreSQL migrations or
RLS, immutable publication, authenticated remote MCP/API transport, Nexus
gateway registration, Server lifecycle packaging, provider adapters, model
training, or deployment. The next safe product slice is the Cortex application
proposal/review interface plus a small deterministic test repository, followed
by the separately verified PostgreSQL transaction and migration boundary.
