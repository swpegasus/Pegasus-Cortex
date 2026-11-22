# Architecture and Dependency Direction

These tenets apply `CORE-MOD-*`, `CORE-AUTH-*`, `CORE-FAIL-*`, and `MODEL-*` to relationships among components. They describe ownership and change direction without prescribing one architectural style.

## ARCH-001 — Declare component ownership and dependency direction

**Rule:** Each component should have an owned responsibility, public boundary, and intentional dependency direction that does not bypass another component's authority or stability contract.

**Practice:** Map who owns each fact and effect, which interfaces may be called, and where cycles, shared storage, callbacks, or service location could bypass the intended direction.

**Boundary:** A dependency cycle is acceptable only when the participating components intentionally form one ownership boundary; drawing separate boxes does not make them independent.

## ARCH-002 — Protect stable policy from volatile mechanisms

**Rule:** Domain rules and policy that change for business reasons should not depend directly on infrastructure, UI, transport, storage, vendor, or deployment details that change for different reasons.

**Practice:** Put volatile mechanisms behind behavior-focused boundaries and keep translation at the edge while applying `CORE-MOD-002` and `CORE-MOD-009`.

**Boundary:** Do not add pass-through layers that merely rename a framework API without protecting policy, ownership, testability, or replacement.

## ARCH-003 — Use one owned model with purpose-built projections

**Rule:** Several consumers may need different views of one fact, but those projections must derive from the same authoritative model and retain source revision and ownership.

**Practice:** Build read models, caches, indexes, summaries, and presentation shapes for their query needs without allowing them to become competing writers.

**Boundary:** A projection can become a new authoritative fact only through an explicit ownership and lifecycle decision, not because it is convenient to mutate.

## ARCH-004 — Make cross-boundary failure semantics part of the design

**Rule:** A component boundary must define unavailable, rejected, partial, delayed, stale, and uncertain outcomes that its consumers can handle.

**Practice:** Align timeouts, retries, idempotency, reconciliation, observability, and fallback with the owner of the effect rather than hiding failure behind a generic success-shaped adapter.

**Boundary:** In-process modules can use simpler mechanics, but crossing a boundary never erases the underlying failure or ownership model.
