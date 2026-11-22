# Data, Persistence, and Migrations

These tenets extend `CORE-WORK-005`, `CORE-WORK-008`, `CORE-AUTH-007`, `CORE-MOD-008`, and `MODEL-007`.

## DATA-001 — Define one authoritative mutation model per durable fact

**Rule:** Each durable dataset or fact needs a clear authority boundary, mutation model, and conflict policy.

**Practice:** Prefer one owning writer when it meets the requirement; otherwise govern multi-writer, replicated, or merge-based mutation explicitly and expose versioned projections, events, or APIs to consumers.

**Boundary:** Storage access alone does not grant authority, and multiple authorities must never compete without defined convergence or rejection semantics.

## DATA-002 — Define data lifecycle with the schema

**Rule:** Durable data requires an owner, purpose, authority, sensitivity, retention, deletion, backup, and recovery policy.

**Practice:** Treat lifecycle behavior as part of the data contract rather than an operational afterthought.

**Boundary:** Temporary derived data may use a simpler policy when loss and disclosure consequences are bounded.

## DATA-003 — Validate on write and on read

**Rule:** Reject invalid new state and detect corrupted, unsupported, or inconsistent existing state before using it.

**Practice:** Preserve safe prior state, quarantine or report invalid records, and avoid silently normalizing evidence away.

**Boundary:** Approved forward-compatible unknown fields may be preserved without being interpreted.

## DATA-004 — Align transaction boundaries with invariants

**Rule:** A transaction should protect the complete invariant that must change atomically, not an arbitrary method or repository call.

**Practice:** Keep transactions bounded, minimize external work inside them, and define isolation according to concurrency risk.

**Boundary:** Cross-system workflows may require durable state machines, idempotency, and reconciliation instead of one transaction.

## DATA-005 — Prevent lost updates deliberately

**Rule:** Concurrent writers require an explicit policy such as expected revision, compare-and-swap, locking, merge, or single-writer serialization.

**Practice:** Return conflict as a distinct outcome and preserve enough context for reconciliation.

**Boundary:** Last-write-wins is acceptable only when loss is an intentional and documented semantic.

## DATA-006 — Design the failed commit

**Rule:** Preserve the last valid durable state until its replacement is complete, validated, and visible under the intended atomicity model.

**Practice:** Define behavior for crashes before, during, and after persistence, including cleanup of incomplete candidates.

**Boundary:** Append-only systems may preserve both attempts and use an explicit trusted head rather than replacement.

## DATA-007 — Make migrations resumable and attributable

**Rule:** A migration must record version, progress, outcome, failure, and safe restart behavior.

**Practice:** Use deterministic steps, checkpoints where needed, and explicit handling for partial application.

**Boundary:** Tiny atomic schema changes may not need per-record progress, but still require version and outcome evidence.

## DATA-008 — Preserve mixed-version compatibility deliberately

**Rule:** Upgrades and rollbacks must define which old and new readers and writers may coexist.

**Practice:** Prefer expand, migrate, observe, and contract sequencing when deployments overlap.

**Boundary:** A coordinated offline migration can use a narrower compatibility window when downtime is explicitly accepted.

## DATA-009 — Validate restored data semantics

**Rule:** A restored dataset must preserve the schema, relationships, permissions, confidentiality, and domain invariants required by its consumers.

**Practice:** Apply `REL-007`, then check representative reads and writes, ownership, references, encryption or secret bindings, and migration compatibility through the real consumer.

**Boundary:** Storage-level integrity is useful evidence but does not prove application-level meaning or access behavior.

## DATA-010 — Make retention and destructive cleanup observable

**Rule:** Archival, expiry, compaction, deletion, legal hold, and cleanup failure need explicit outcomes and ownership.

**Practice:** Protect active, unacknowledged, referenced, or otherwise ineligible records from retention pruning.

**Boundary:** Legal or user-authorized erasure may override ordinary retention, but must follow its governed verification path.
