# Python Tenets

These tenets specialize `DEP-*`, `CONC-*`, `CLI-*`, and `CORE-FAIL-*` for Python code.

## PY-001 — Bind tools to the intended interpreter

**Rule:** Use an isolated environment and invoke package or tool modules through the selected Python interpreter.

**Practice:** Keep interpreter version, environment, package manager, and lock or resolved dependencies consistent.

**Boundary:** System Python may be appropriate for distribution-owned scripts that follow the platform package policy.

## PY-002 — Keep imports deterministic

**Rule:** Avoid material work, network calls, process changes, and ad hoc path mutation at import time.

**Practice:** Expose explicit entry points and package metadata and keep initialization callable and testable.

**Boundary:** Small immutable constant construction at import time is appropriate.

## PY-003 — Use context managers for owned resources

**Rule:** Files, locks, database resources, temporary objects, and other scoped ownership should use context-management semantics.

**Practice:** Ensure cleanup occurs across success, exception, cancellation, and early return.

**Boundary:** Long-lived application resources may be owned by an explicit service lifecycle instead.

## PY-004 — Catch expected exceptions precisely

**Rule:** Catch at a layer that can add policy, recovery, or useful context and preserve the causal exception chain.

**Practice:** Avoid broad catches that hide cancellation, process-exit signals, programming errors, or partial effects.

**Boundary:** A top-level containment boundary may catch broadly while still recording honest failure and releasing resources.

## PY-005 — Treat type hints as static evidence

**Rule:** Use annotations to clarify public contracts, but validate untrusted runtime data because annotations do not enforce payload shape.

**Practice:** Narrow dynamic values before converting them into domain types.

**Boundary:** Internal exploratory code may use lighter typing when its boundary and lifetime are small.

## PY-006 — Avoid shared mutable defaults and globals

**Rule:** Create mutable defaults per call and give module-level mutable state explicit ownership and synchronization.

**Practice:** Prefer immutable defaults, factories, and dependency injection over hidden singleton mutation.

**Boundary:** Deliberate process-wide caches may be global when their bounds, locking, and reset behavior are owned.

## PY-007 — Keep async and blocking boundaries explicit

**Rule:** Await owned coroutines, propagate cancellation, and isolate blocking libraries from event-loop execution.

**Practice:** Use structured task ownership and avoid creating unobserved tasks.

**Boundary:** Synchronous code should remain synchronous when concurrency provides no benefit.
