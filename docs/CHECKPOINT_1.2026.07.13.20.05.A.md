# Pegasus Cortex Checkpoint 1.2026.07.13.20.05.A

Last updated: 2026-07-13

## Purpose

This checkpoint establishes two bounded local foundations:

1. an experimental isolated read-only MCP `2025-11-25` stdio host for local
   Codex testing; and
2. a provider-neutral, approval-gated learning-export library with a strictly
   plan-only command-line interface.

This is takeover documentation for the frozen source baseline. It is not a
production deployment guide, an authenticated service contract, a Codex
registration action, or authority to build, publish, install, or deploy.

## Checkpoint Identity And Evidence State

- Canonical release: `1.2026.07.13.20.05.A`.
- NuGet version mapping: `1.20260713.2005-a`.
- Canonical project root: `D:\Codex Projects\Pegasus Cortex`.
- Rollback snapshot:
  `D:\Codex Projects\_snapshots\collections\Pegasus-Cortex\1.2026.07.13.19.22.A\Pegasus-Cortex-1.2026.07.13.19.22.A.zip`.
- Rollback snapshot SHA-256:
  `467b07efdea5fb70522f236c6a3cfe403673254f5be91ddb29a41c9c1e679c5e`.
- The first snapshot attempt encountered a compression-assembly load failure.
  Recovery succeeded and produced the rollback snapshot identified above. Do
  not discard this failure: future snapshot tooling must surface the underlying
  assembly cause and verify the recovered archive before treating it as a
  rollback point.
- Nexus reported an independent green security review with MCP Release `15/15`
  and learning exports Release `7/7`, with no blocking defect.
- The completed-source snapshot is
  `D:\Codex Projects\_snapshots\collections\Pegasus-Cortex\1.2026.07.13.20.05.A\Pegasus-Cortex-1.2026.07.13.20.05.A.zip`:
  93 entries, 197018 bytes, SHA-256
  `1cd3dcc6112a54a12874f366bdbfc3b719b62bddc0f14826a3a4ce4129d29733`.
- Final local release evidence is recorded at
  `artifacts/release/1.2026.07.13.20.05.A/cortex-mcp-learning-export-release-manifest.json`.
  The manifest and artifacts are unsigned, nondeployable, local-only, and were
  not externally published or deployed.

### Final Verification Evidence

- `PegasusCortex.slnx` includes all 12 projects, including
  `PegasusCortex.LearningExports.DefaultLibrary`.
- One non-incremental full-solution Release build passed with zero warnings and
  zero errors.
- The focused MCP Release build passed; the protocol harness passed `15/15`.
- The focused learning-export Release build passed; its harness passed `7/7`.
- A real child-process MCP smoke from an unrelated working directory negotiated
  `2025-11-25`, returned ready status, completed bounded search, exited zero,
  and wrote no standard error.
- The final framework-dependent managed MCP payload was produced without a
  Windows apphost and passed initialize/status from
  `artifacts/runtime/1.2026.07.13.20.05.A/mcp-portable`.
- `Pegasus.Cortex.LearningExports 1.20260713.2005-a` was packed locally with its
  README, `VERSION.json`, output-manifest schema, and exact dependency
  `Pegasus.Shared [1.20260711.421-a]`.
- CLI `--help` exited zero and reported `operation=plan`,
  `artifactsProduced=false`, `serverAuthorizationGranted=false`,
  `modelTrainingPerformed=false`, and `networkAccess=false`.
- Static boundary scans found no MCP filesystem, database, network, or
  project-local `.cortex` access and no learning-export network, database,
  process, or provider integration.
- Project-local Cortex validation passed with no missing files, placeholders,
  invalid JSON, or recovery-order gaps. Retention remains below the ten-version
  cap, so nothing was pruned.
- No live upload, install, deployment, trust/certificate, server, hardware, or
  production-data mutation occurred.

## Experimental Read-Only MCP Host

### Boundary

The host is a `.NET 10` local-process stdio executable. It implements MCP
revision `2025-11-25` using newline-delimited JSON-RPC and reads only the
verified built-in Default Library loaded into its process.

It has no database, network, production authentication, durable write,
project-local `.cortex` runtime dependency, or live-service access. Its current
security boundary is the local process launcher and operating-system access to
the executable. It must not be exposed over TCP, treated as a shared service,
or represented as the future authenticated production MCP endpoint.

### Tools

- `cortex_discover` returns the fixed capability and limit description.
- `cortex_status` returns deterministic Default Library validation and release
  status.
- `cortex_search` performs bounded lexical and metadata retrieval.
- `cortex_read` reads one exact current record or revision identifier.

Every tool is read-only, non-destructive, idempotent, and closed-world.

### Resources

- `cortex://default-library/status`
- `cortex://default-library/catalog`
- `cortex://default-library/records/{recordId}` for allowlisted current records

The host does not provide production namespaces, user-specific records,
authorization policy, write tools, prompts, subscriptions, or maintenance
operations.

### Bounds And Content Treatment

- Maximum request: 128 Ki characters.
- Maximum response: 256 KiB.
- Maximum search results: 10.
- Maximum exposed record resources: 64.
- Retrieved content remains `untrusted_data`, non-executable, and unable to
  grant authority or provide privileged tool arguments.
- Stale, blocked, unknown, or unverified records require explicit read opt-in.

### Local Codex Use

Portable MCP execution requires the .NET 10 runtime. The approved local Codex
configuration now contains an enabled, non-required `pegasus_cortex`
registration restricted to the four read-only tools. The TOML parsed
successfully and the existing `node_repl` environment remained intact. The
current Codex process has not hot-loaded the registration; Codex must be
restarted before it is expected to be available.

The production authenticated MCP service contract remains future work. It will
require server-side namespace authorization, authenticated identities,
audience-bound tokens, revocation checks, audit, and the production transport
contract; none of those controls may be inferred from this isolated host.

## Provider-Neutral Learning Exports

### Boundary

The learning-export library defines immutable input, dataset-definition,
selection, transformation, split, manifest, artifact, and diagnostic contracts.
Its canonical JSONL adapter is provider-neutral and does not invent a trainer's
prompt/answer schema. Manifests preserve selected record and revision IDs,
content hashes, review-envelope hashes, transformation identity, deterministic
split lineage, approvals, output hashes, usage rights, sensitivity, and
redaction decisions.

The library prepares datasets; it does not run model training. All submitted
and exported text remains untrusted data.

### Authority Model

Serialized record reviews and dataset approvals do not authorize themselves.
The runtime host must supply current authority that independently permits
planning or building, binds approved decision hashes, restricts namespaces, and
authorizes an output root.

The CLI is strictly plan-only. It reads explicit input files, validates and
plans in memory, writes a structured result to standard output, and always
uses `CanBuild: false`, `PublishArtifacts: false`, and no approved output root.
It never grants server authorization and cannot turn supplied approval text
into build authority.

Build authority must be host-injected and signed or equivalently integrity-
protected by the future trusted host. It must be inaccessible to Codex,
Pegasus Coder, MCP clients, submitted records, dataset definitions, and model
output. Coder integration is explicitly deferred.

### Local Publication Boundary

The library can publish only when a trusted host supplies build authority and a
matching approved output root. Publication uses private same-volume staging,
bounded writes, deterministic hashes, and an atomic directory move.

On Windows, private ACL creation and enforcement are host responsibilities.
Portable library code can validate the selected path boundary but cannot prove
that a Windows ACL is private or correctly inherited.

Two follow-ups remain open:

- prune crash-orphaned staging directories through a bounded, host-owned
  maintenance operation; and
- make canonical input-bundle hashing cancellation-aware and streaming so a
  large bundle does not require an uninterrupted whole-buffer hash pass.

## Ecosystem And Scope Boundaries

- Project-local `.cortex` is development continuity only and must not become a
  runtime input, database, or release payload.
- The external system-management product is excluded from the Cortex product baseline. It
  is only an external, separately approved test-access path and grants no
  product, build, identity, deployment, or operational authority.
- Pegasus Coder integration is deferred. No Coder adapter, registration,
  credential, or authority path is part of this checkpoint.
- The experimental stdio host is not the production authenticated MCP/API
  service and must not be used as evidence that production namespace or access
  controls exist.
- This documentation update does not modify Codex configuration, deployment,
  live services, production data, identity providers, trust stores,
  certificates, or server environments. The completed local Codex registration
  described above preceded this documentation update and remains restart-bound.

## Next Authorized Production Direction

Phase A planning selected one Cortex-owned application core behind replaceable
transports, PostgreSQL as the first durable store, and retrieval-first learning.
The production remote MCP endpoint should use a sanctioned Nexus
gateway/registration boundary while Cortex remains an independent product
runtime. Current Nexus code has no verified product-extension seam and
explicitly is not a reverse proxy or plugin-execution gateway; therefore Cortex
business logic must not be imported into Nexus merely to share its web
endpoint. The smallest slice is an independent Cortex worker and PostgreSQL
namespace behind an authenticated local gateway contract, the existing stdio
adapter over the same query service, a bounded Nexus status projection, and a
proposal-to-review-to-published-record workflow. Phase B implementation has not
begun and requires a separate explicit authorization, including any
cross-project Nexus change.

## Safe Resume

1. Read `START_HERE.md`, `AGENTS.md`, and `.cortex/` in the documented recovery
   order.
2. Confirm the source-freeze handoff still applies before any implementation or
   verification action.
3. Preserve the rollback snapshot and its recorded hash.
4. Keep MCP and learning-export verification proportional to changed observable
   behavior; do not rerun unchanged Core, Default Library, or Resource
   Efficiency matrices without a changed risk surface.
5. Use the final local release manifest identified above for checkpoint
   evidence; do not represent the unsigned local output as published or
   deployable.
6. Require separate approval before build, test, package, registration,
   publication, deployment, live-service contact, or configuration mutation.
