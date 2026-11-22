# Canonical Path Budget Policy Handoff

Last updated: 2026-07-13

## Decision

No stricter existing Pegasus Cortex absolute-path policy was found during the
current source inspection. Cortex therefore adopts the suite default: on
Windows, a generated final canonical absolute path must not exceed 240 UTF-16
code units. The limit deliberately reserves headroom below `MAX_PATH`, even
when the host enables long paths. Long-path support is not a normal-operation
dependency and does not relax this product limit.

This is an architecture and implementation handoff only. The current turn did
not change runtime source, tests, schemas, artifacts, packages, or host
configuration and did not build or execute the product.

## Scope

The policy covers every path generated or materialized by Cortex or one of its
owned optional modules, including:

- product/submodule source layouts and generated source/state;
- runtime state, indexes, caches, evidence attachments, and temporary files;
- ingestion, review, learning, export, and maintenance jobs;
- learning-export staging and final artifacts;
- package, snapshot, backup, restore, and migration outputs; and
- archive entries evaluated against their actual extraction destination.

Project-local `.cortex` remains development continuity and is not a product
runtime store. It still follows the same safe-path rule when Cortex tooling
creates or maintains it.

## Required Pre-I/O Validation

One product-owned path-policy service should validate an approved canonical
root and an untrusted or generated relative path before any directory creation,
open, write, move, replacement, extraction, or publication:

1. Resolve and normalize the approved root once through the trusted host. The
   root must be fully qualified and permitted by product policy.
2. Reject empty or ambiguous relative paths where a leaf is required, rooted or
   drive-relative input, UNC/device syntax unless explicitly supported by a
   stricter host policy, `.` or `..` traversal, control characters, NUL, and
   alternate data stream syntax.
3. Validate every segment before combination. Reject Windows reserved device
   names, trailing spaces or dots, normalization changes that could alter the
   effective name, excessive platform length, and UTF-8 encoded size above 255
   bytes unless an owning provider sets a stricter limit. Segment checks apply
   to Unicode names as encoded, not only to ASCII character counts.
4. Combine root and relative path, canonicalize the result, confirm containment
   beneath the approved root with the platform-appropriate comparison, and
   calculate the final absolute Windows length in UTF-16 code units.
5. Reject a result longer than 240, or longer than an existing stricter product
   limit. Include separator and extension characters in the calculation.
6. Inspect each existing ancestor for reparse points or symbolic links and
   reject any route that can escape the approved root. Creation code must avoid
   check-then-use gaps and revalidate the final destination when the platform
   permits it.
7. Perform the operation only after all applicable validations pass. Failure is
   atomic: no partial destination, staged output, or extracted subset remains.

The same service should return a typed validated-path value so downstream code
cannot accidentally reopen the original unchecked string. Authorization to use
a root remains separate from path syntax validation.

## Generated Layout

Generated directory components should be short, stable, and bounded. Prefer a
layout such as `st/{job-id}`, `ex/{dataset-id}/{build-id}`, or `ix/{shard-id}`
under the approved product root. IDs should be fixed-width opaque identifiers
or bounded hashes. Human-readable dataset, project, record, model, product, and
version descriptions belong in metadata and manifests, not repeated in nested
directory names.

Do not silently truncate a user-selected filename or path because truncation
can create collisions and break provenance. If an authorized import/export
workflow can safely copy external content into owned storage, map the external
path to a bounded internal staging ID and preserve the original display value
only in protected metadata. Otherwise reject it cleanly before producing any
output.

## Archive And Package Preflight

Archive validation must finish before extraction. For every entry, evaluate the
canonical destination formed from the actual extraction root and entry name.
Reject the whole archive on traversal or zip-slip, rooted entries, alternate
data streams, reserved/trailing-normalized segments, duplicate or case-folded
collisions, symlink/reparse entries that can escape, segment byte overflow,
final-path budget overflow, entry-count overflow, or configured expanded-size
overflow. Directory entries do not exempt their descendants from evaluation.

Extract only into a new bounded staging directory after the complete preflight
succeeds, then publish atomically. Cancellation or failure removes only staging
owned by the current operation. Crash-orphan cleanup remains a bounded,
host-owned follow-up and must not recursively delete unverified paths.

## Diagnostics

Path failures use the shared structured diagnostic envelope. Suggested codes
are `cortex_path_invalid`, `cortex_path_escape`,
`cortex_path_budget_exceeded`, `cortex_path_segment_too_large`, and
`cortex_archive_entry_rejected`. Context should include:

- operation and correlation identifier;
- approved root identifier or sanitized root;
- sanitized relative path or bounded path hash;
- calculated final UTF-16 length and effective limit;
- failing segment index or archive entry index when useful;
- stable cause and retryability; and
- underlying exception type/message only after sensitivity-safe shaping.

Do not echo credentials, record content, private path segments, archive payload
content, or other sensitive values. A caller-facing diagnostic may use a path
hash while protected audit retains the minimum authorized detail required for
recovery.

## Current Cortex Gaps

The frozen checkpoint already contains partial defenses:

- `LocalOutputRootPolicy` canonicalizes a fully qualified local root, rejects
  controls/UNC/filesystem roots/nonlocal drives, and requires requested and
  approved roots to match.
- `AtomicArtifactPublisher` checks reparse points, same-volume staging, portable
  segments, Windows reserved basenames, trailing dots/spaces, and traversal.
- `LearningExportCliApplication` canonicalizes input paths, checks regular
  non-reparse files and parents, and applies bounded input sizes.

Those checks do not yet form one pre-I/O policy for the 240-character final
path budget, alternate data streams, UTF-8 segment budgets, every generated
destination, or archive extraction. Current staging also nests dataset ID,
build ID, and a GUID; the implementation checkpoint should measure it and
shorten components if needed. The isolated MCP host has no filesystem,
database, network, or `.cortex` access and needs no path change for this rule.

## Measurement And Completed Historical Remediation

A 2026-07-13 inventory measured path length using .NET string length, which
counts UTF-16 code units on Windows. Before remediation:

- the active tree maximum is 217 at
  `artifacts/verification/nuget-cache-default-1325/microsoft.aspnetcore.app.ref/8.0.28/analyzers/dotnet/cs/zh-Hans/Microsoft.Extensions.Configuration.Binder.SourceGeneration.resources.dll`;
- the active tree has zero paths above 240;
- the full retained tree maximum is 261; and
- 13 paths exceed 240, all beneath the superseded historical
  `artifacts/_superseded/Pegasus.Cortex.CleanPrerelease/1.2026.07.11.03.06.A/verification/resource-package-cache-306`
  evidence.

The historical evidence predates this rule and is not in the active feed.
Authorized remediation created `artifacts/_superseded/e/306a/cache-files.json`,
`cache.zip`, and `evidence.json`. The inventory covers all 37 files, 20
directories, 757527 bytes, and ten pre-remediation reference files. Every ZIP
entry and a bounded recovery extraction matched the original SHA-256 hashes;
package bytes were not changed. The verified bundle was copied to
`D:/Codex Projects/_snapshots/collections/Pegasus-Cortex/pathfix-306a-20260713`.

Evidence hashes are:

- inventory: `43599ca9c2c70afcebd08828211fc864a78fb087618f8aeae8c7056e97ce0bc2`;
- ZIP bundle: `69218fe4a477f72aa626918fbf0feab160b21aa82456aba4b28fca52a530e9ce`;
- verification receipt: `0b9184a3c0ae9009aad04f5adee3344e1e67b9c5537bce1e8b19a0b68ae05109`;
  and
- final supersession record: `566ff146acdece3c286c2093baf867c8425758d3113c0c159493b4e5b43ede6a`.

After the supersession record bound those hashes, 37/37 source hashes were
rechecked and the redundant original cache was removed. The full project and
artifacts maximum is now 232 with zero paths above 240. Active artifacts remain
217 and source remains 133. The short evidence and external recovery copies
match. New operations must not reproduce the prior nested layout.

## Ownership Handoff

- **Pegasus Shared:** verified unsigned local release
  `1.2026.07.13.21.48.A` / `1.20260713.2148-a` now supplies
  `GeneratedPathBoundary`, `GeneratedPathPolicy`, and `GeneratedPathId` for
  the generic 240 UTF-16 full-path and 255-byte UTF-8 segment rules, canonical
  containment, unsafe-name/archive rejection, reparse preflight, bounded IDs,
  and sanitized diagnostics. Reported package SHA-256 is
  `17c39711d9bcaf8e362a5bd375b07c2ce5a2b1eb22a33d800dffdd56d2020c5d`;
  symbols SHA-256 is
  `ae288c81b957eb9260d79c570c12f4dec957f273d2cc1ec531ea9a9d69f15f2b`.
  The contract is preflight-only and does not perform host I/O or grant root
  authority.
- **Pegasus Cortex:** keep the exact Shared `421-a` pin until an explicitly
  authorized owner-controlled affected build adopts `2148-a`. At adoption,
  prefer the Shared generic policy over near-duplicate generic logic, retain
  genuinely Cortex-specific adapters, and use the result for all Cortex-owned
  state, export, staging, attachment, job, cache, index, package, and archive
  paths. Hostile writable roots still require platform no-follow or handle-
  relative write semantics; lexical preflight cannot close creation races by
  itself.
- **Pegasus Server:** enforce the same or a stricter rule for install roots,
  lifecycle state, snapshots, backups, packages, staging, and archive
  extraction. It owns host path/ACL provisioning and rollback cleanup.
- **Pegasus Nexus:** use short stable product state and registration paths and
  carry bounded diagnostics; it does not validate or create Cortex-owned
  runtime paths through the remote MCP gateway.

No cross-project source edit, package adoption, build, or deployment is
authorized by this handoff.

## Proportional Phase B Verification

When implementation is explicitly authorized, add focused observable tests for
the affected path generator and its primary failure path:

- final canonical absolute path at exactly 240 UTF-16 code units is accepted;
- the otherwise identical path at 241 is rejected before I/O;
- deep nesting exceeds the budget without leaving partial directories;
- Unicode and multibyte UTF-8 segments enforce both final UTF-16 and segment
  byte limits deterministically, including 255-byte acceptance and 256-byte
  rejection when no stricter provider limit applies;
- reserved device names, trailing dot/space, ADS syntax, rooted-relative input,
  and traversal are rejected;
- existing reparse/symlink ancestors and a creation-race-safe escape case fail
  closed;
- a long but allowed install root reduces the remaining relative-path budget;
- archive preflight rejects zip-slip, collision, reparse/symlink, expansion,
  and over-budget entries before extraction;
- safe external-path mapping uses a bounded internal ID without losing metadata
  lineage, while an unmappable path fails without output; and
- cancellation leaves no current-operation staging and returns a bounded
  diagnostic.

For each affected layout, verification should report the maximum observed final
canonical generated path and the effective limit. Run one clean affected build
only if compiled source changes; do not repeat unchanged consumer/package or
stress matrices unless the changed risk warrants them.
