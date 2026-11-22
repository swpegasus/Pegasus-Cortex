# Reliability, Backup, and Recovery

These tenets extend `CORE-FAIL-002`, `CORE-FAIL-006`, `CORE-FAIL-007`, `CORE-WORK-005`, and `CORE-WORK-008`.

## REL-001 — Define lifecycle and recovery states

**Rule:** Startup, initializing, ready, degraded, draining, failed, recovering, and stopped should be explicit when behavior differs by state.

**Practice:** Define allowed transitions, terminal outcomes, and what callers may safely do in each state.

**Boundary:** Small stateless operations need not expose a service lifecycle they do not have.

## REL-002 — Separate liveness, readiness, health, and correctness

**Rule:** Process existence, ability to accept work, dependency condition, and semantic correctness are different claims.

**Practice:** Verify and report each signal against the capability it actually proves.

**Boundary:** A compact status may summarize them only when detailed causes remain accessible.

## REL-003 — Preserve valid state while surfacing degradation

**Rule:** When refresh or optional dependency failure occurs, retain the latest validated state only if continued use is safe and visibly stale or degraded.

**Practice:** Record source revision, age, unavailable guarantee, and recovery attempt.

**Boundary:** Authorization, destructive actions, or high-integrity decisions may need to fail closed instead of using stale state.

## REL-004 — Isolate faults by ownership boundary

**Rule:** Failure of one request, worker, tenant, plugin, optional dependency, or data item should not corrupt unrelated work.

**Practice:** Separate state, quotas, cancellation, error handling, and recovery domains.

**Boundary:** Shared foundational failure may legitimately affect multiple capabilities, but its blast radius should remain explicit.

## REL-005 — Define recovery objectives proportionately

**Rule:** Acceptable data loss, recovery time, service degradation, and dependency order should match business and safety consequence.

**Practice:** Use those objectives to choose backup frequency, redundancy, runbooks, and rehearsal depth.

**Boundary:** Do not invent strict objectives without the authority and resources to meet them.

## REL-006 — Make recovery resumable and idempotent

**Rule:** Interrupted recovery must restart or reconcile without compounding damage or discarding causal evidence.

**Practice:** Persist checkpoints, operation identity, expected revision, and completed effects where needed.

**Boundary:** A fully atomic recovery step may restart from its unchanged prior state.

## REL-007 — Rehearse restoration and rollback

**Rule:** A backup, documented recovery path, or rollback procedure is not proven until exercised through the real consumer at an authorized isolated boundary.

**Practice:** Verify data, permissions, configuration, dependencies, startup, and representative behavior after recovery.

**Boundary:** Production drills should avoid unnecessary live risk and may use equivalent isolated targets.

## REL-008 — Preserve history through amendment

**Rule:** Correct durable audit or lifecycle records with explicit supersession, reconciliation, or successor entries rather than rewriting evidence.

**Practice:** Link old and corrected outcomes while preserving who changed what and why.

**Boundary:** Ordinary mutable user content may follow its own edit model when audit history is not required.

## REL-009 — Distinguish tamper evidence from rollback resistance

**Rule:** Hash chains and append-only logs can reveal alteration but do not prevent restoration of an older complete history.

**Practice:** Protect a trusted head, monotonic counter, external witness, or equivalent anchor independently when rollback resistance matters.

**Boundary:** Do not add physical trust mechanisms when detection alone satisfies the threat model.

## REL-010 — Stabilize first, then learn from failure

**Rule:** During an incident, contain harm and preserve evidence before performing broad root-cause or redesign work.

**Practice:** Assign ownership, communicate impact, use reversible mitigation, then produce bounded corrective actions with verification.

**Boundary:** Emergency containment does not authorize permanent workarounds or suppression of the real cause.
