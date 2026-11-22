# Workspace, Artifacts, and Path Safety

## CORE-WORK-001 — Maintain one canonical location per responsibility

**Rule:** Source, tests, documentation, mutable state, staged packages, installed binaries, and backups should each have an obvious owned location.

**Practice:** Repair or migrate the canonical location instead of creating parallel replacement trees to avoid understanding existing state. Default development builds to a reusable output folder and bounded scratch space; create archives, rollback packages, or baselines only for an explicit retention purpose.

**Boundary:** Multiple variants are appropriate when they have explicit platform, configuration, release, or rollback identities.

## CORE-WORK-002 — Distinguish source, durable state, and generated output

**Rule:** Treat reproducible build output differently from source, configuration, user data, credentials, databases, and audit history.

**Practice:** Mark generated directories, keep them isolated, and define which state must survive cleanup or rebuild.

**Boundary:** An unfamiliar or ambiguous directory is not disposable merely because it contains binaries or temporary-looking names.

## CORE-WORK-003 — Use path APIs and prove containment

**Rule:** Join, normalize, compare, and relativize paths with platform-aware APIs rather than string concatenation or prefix checks.

**Practice:** Resolve the approved root and candidate target, reject ambiguity, and prove the target remains within scope.

**Boundary:** Case, alternate separators, traversal segments, drive-relative paths, links, mount points, and canonicalization can invalidate naive string checks.

## CORE-WORK-004 — Inspect redirects before recursive mutation

**Rule:** Symbolic links, junctions, reparse points, mounts, and aliases can redirect operations outside an approved tree.

**Practice:** Enumerate exact targets and choose an explicit follow, reject, or preserve policy before recursive delete, move, copy, permission, or ownership changes.

**Boundary:** Do not use lossy text or quoting to reconstruct destructive targets across shells or processes. Structured transfer is acceptable only when identity and containment are preserved and revalidated at the mutation boundary.

## CORE-WORK-005 — Replace important files crash-safely

**Rule:** An important file replacement should expose either the prior valid file or the complete validated replacement after interruption.

**Practice:** Write a same-filesystem candidate, validate it, flush file and directory state when durability requires it, and replace atomically with concurrency and backup behavior proportionate to risk.

**Boundary:** Rename alone does not guarantee durability, concurrency safety, or cross-filesystem atomicity; `DATA-006` owns general durable commit semantics beyond files.

## CORE-WORK-006 — Preserve unrelated work

**Rule:** Existing modifications and untracked files belong to the user or another workstream unless proven otherwise.

**Practice:** Inspect status, keep edits focused, avoid broad formatting, and reconcile overlapping ownership before changing the same area.

**Boundary:** Do not use destructive reset, checkout, clean, or bulk replacement to obtain a convenient baseline without explicit authority.

## CORE-WORK-007 — Clean generated output by exact boundary

**Rule:** Reproducible artifacts may be removed when their exact path and generated nature are verified.

**Practice:** Prefer tool-native clean operations or bounded deletion within the verified build-output root, then rebuild when needed.

**Boundary:** This tenet does not authorize deleting the workspace root, caches containing unique evidence, staged releases, user data, or ambiguous parents.

## CORE-WORK-008 — Make workspace migrations copy-first and reversible

**Rule:** Preserve the authoritative filesystem or workspace source and a rollback path until the destination is verified through the consuming application.

**Practice:** Inventory, copy, validate identity and behavior, switch references, observe, and remove old state only after explicit completion criteria.

**Boundary:** File counts and hashes can support migration evidence but do not prove application-level readability or semantic compatibility; schema and durable-state migration belong to `DATA-007`, `DATA-008`, and `REL-007`.
