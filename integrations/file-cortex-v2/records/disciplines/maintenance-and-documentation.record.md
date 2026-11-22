# Maintenance and Documentation

These tenets extend `CORE-COMM-005`, `CORE-WORK-006`, `CORE-MOD-008`, and the dedicated debugging, refactoring, and review workflows.

## MAINT-001 — Assign maintenance ownership

**Rule:** Public contracts, dependencies, data stores, operations, and documentation need an owner responsible for compatibility and retirement decisions.

**Practice:** Make escalation and review boundaries discoverable without encoding transient personnel details into the contract.

**Boundary:** Shared ownership still needs one decision process rather than ambiguous collective responsibility.

## MAINT-002 — Comment intent and constraints

**Rule:** Comments should explain non-obvious purpose, invariant, tradeoff, ownership, or failure behavior rather than restate syntax.

**Practice:** Keep comments near the code they constrain and update or remove them with behavior changes.

**Boundary:** Prefer clear names and structure over comments that compensate for avoidable complexity.

## MAINT-003 — Document public behavior and operations

**Rule:** Documentation should cover supported setup, contracts, configuration, workflows, failure states, recovery, and handoff information appropriate to its audience.

**Practice:** Name the source of truth and keep examples versioned or verified where feasible.

**Boundary:** Documentation depth and timing should match contract stability, operational consequence, audience, and maintenance ownership; do not generate large documents that will immediately become unaudited duplicate authority.

## MAINT-004 — Do not use documentation as runtime evidence

**Rule:** A guide or design states intent and procedure but does not prove the current build, service, migration, or deployment works.

**Practice:** Link documentation claims to current tests, artifacts, probes, or explicitly unverified status.

**Boundary:** Documentation review can prove completeness and internal consistency of the document itself.

## MAINT-005 — Retire compatibility mechanisms deliberately

**Rule:** Aliases, shims, adapters, duplicated documentation, and transitional support introduced by `API-005` need an owner, observable usage, and an executable removal condition.

**Practice:** Keep the canonical replacement prominent, test the compatibility path only for its supported window, and remove duplicate authority after the retirement gate is met.

**Boundary:** Long-lived compatibility may be a valid product commitment when it has explicit ownership and tests.

## MAINT-006 — Search ownership before removing code

**Rule:** Apparent dead code must be checked for configuration, reflection, registration, serialization, migrations, scripts, plugins, and external consumers.

**Practice:** Remove callers and compatibility deliberately and verify the intended supported surface.

**Boundary:** Generated or provably unreachable internal code may use a narrower removal check.

## MAINT-007 — Preserve coherent attributable change history

**Rule:** Material maintenance should leave enough change records, decision context, compatibility notes, and verification evidence for another maintainer to understand and reverse it when feasible.

**Practice:** Group related changes coherently, link superseding decisions, and preserve prior behavior or migration evidence according to `VCS-*` and `CORE-COMM-005`.

**Boundary:** This does not require recording private reasoning or every keystroke; preserve the public basis needed to review and continue the work.
