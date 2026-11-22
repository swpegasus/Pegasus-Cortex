# Concurrency and Resource Lifecycle

These tenets extend `CORE-FAIL-006`, `CORE-FAIL-007`, `CORE-AUTH-006`, and `TEST-002`.

## CONC-001 — Give shared state one invariant owner

**Rule:** Synchronization should follow clear ownership of shared state rather than being scattered among callers.

**Practice:** Centralize compound transitions and expose operations that preserve the invariant.

**Boundary:** Immutable data can be shared freely when its referenced dependencies are also safe.

## CONC-002 — Own every asynchronous operation

**Rule:** Every task must be awaited, supervised by a scope, or durably handed off to an explicit owner.

**Practice:** Observe completion, failure, cancellation, and cleanup; avoid untracked fire-and-forget work.

**Boundary:** Deliberate background services are valid when their lifetime and failure reporting are owned.

## CONC-003 — Propagate cancellation and deadlines

**Rule:** Cancellation and remaining useful lifetime should flow through participating layers and dependencies.

**Practice:** Distinguish caller cancellation, timeout, shutdown, and dependency failure in terminal outcomes.

**Boundary:** Durable work may outlive the initiating request only after an explicit ownership handoff.

## CONC-004 — Bound concurrency and queue growth

**Rule:** Threads, tasks, workers, queues, fan-out, and pending requests require explicit capacity and backpressure.

**Practice:** Define admission, fairness, overload behavior, and what is rejected, delayed, or shed.

**Boundary:** A naturally fixed small set may rely on its structural bound when that bound is enforced.

## CONC-005 — Do not block asynchronous execution paths

**Rule:** Avoid synchronous waits and blocking I/O on event-loop, request, or limited worker threads.

**Practice:** Compose asynchronous operations through their native awaitable model and isolate unavoidable blocking work.

**Boundary:** Simple synchronous programs need not become asynchronous without a concurrency or responsiveness need.

## CONC-006 — Keep locks away from unbounded work

**Rule:** Do not hold shared locks across external I/O, callbacks, user code, or indefinite waits.

**Practice:** Use a consistent acquisition order, minimize the protected section, and copy safe work outside the lock where practical.

**Boundary:** An atomic storage transaction may intentionally hold locks through bounded database work required by its invariant.

## CONC-007 — Resolve terminal races exactly once

**Rule:** Each observer or operation handle must resolve success, fault, timeout, cancellation, shutdown, acceptance, or uncertainty through one explicit terminal-state transition.

**Practice:** Make duplicate completion attempts harmless, preserve the winning cause and known effects, and reconcile remote or durable work that may remain active after a local timeout or cancellation.

**Boundary:** Terminating a caller's handle does not prove termination of underlying work; accepted, uncertain, reconciling, or durably handed-off work may have a separate lifecycle.

## CONC-008 — Make shutdown ordered and observable

**Rule:** Stop intake, drain or cancel owned work, flush required state, release resources, and report residual failure in a defined order.

**Practice:** Bound each phase and preserve evidence when graceful shutdown cannot complete.

**Boundary:** Emergency termination remains a last-resort containment action, not proof of clean shutdown.

## CONC-009 — Test invariants instead of lucky timing

**Rule:** Concurrency tests should control scheduling or assert invariants rather than depend on arbitrary sleeps.

**Practice:** Use barriers, test clocks, deterministic schedulers, fault injection, and repeated stress checks where appropriate.

**Boundary:** Stress success alone does not prove absence of races; combine it with ownership and synchronization review.
