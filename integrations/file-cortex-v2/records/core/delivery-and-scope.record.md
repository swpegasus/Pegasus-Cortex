# Delivery, Scope, and Triage

Checkpoint evidence and the meaning of a green result are defined canonically in `CORE-VERIFY-007` through `CORE-VERIFY-009`.

## CORE-DELIVERY-001 — Define the observable outcome first

**Rule:** State the user-visible or externally observable result and its acceptance evidence before selecting implementation details.

**Practice:** Identify the real execution path, success state, important failure state, and smallest evidence that would demonstrate completion.

**Boundary:** Compilation, scaffolding, documentation, mocks, or plans are not substitutes for the requested working outcome.

## CORE-DELIVERY-002 — Deliver the smallest coherent vertical slice

**Rule:** Connect enough of the real path to produce a usable, testable result before expanding into adjacent improvements.

**Practice:** Complete one end-to-end behavior, including validation and failure handling, then extend breadth.

**Boundary:** A smaller slice must not omit authorization, integrity, destructive-path containment, or another material boundary required for safe execution.

## CORE-DELIVERY-003 — Triage by demonstrated impact

**Rule:** Classify findings as blockers, material boundary risks, or deferred improvements.

**Practice:** Fix issues that prevent or invalidate the milestone, make success misleading, or create material security, data-loss, integrity, or compatibility risk. Record minor polish and speculative improvements for a bounded later pass.

**Boundary:** Obvious high-impact risks do not require a production incident before being treated as material.

## CORE-DELIVERY-004 — Complete, verify, then clean locally

**Rule:** Work in bounded passes: implement the slice, verify it, repair observed defects, then clean up directly related code.

**Practice:** Batch nonblocking cleanup around a coherent green checkpoint instead of interrupting the active objective repeatedly.

**Boundary:** Cleanup is not permission to redesign unrelated systems or perform broad destructive operations.

## CORE-DELIVERY-005 — Keep deferred work bounded and actionable

**Rule:** A deferred finding must state its component, observed issue, current impact, reason for deferral, and next useful check.

**Practice:** Maintain a short follow-up list and revisit it at an appropriate checkpoint or hardening phase.

**Boundary:** Do not defer known authorization, secret-handling, destructive-path, data-loss, or false-success defects as mere polish.
