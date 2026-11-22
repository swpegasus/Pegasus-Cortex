# JavaScript and TypeScript Tenets

These tenets specialize `CONC-*`, `DEP-*`, and `CORE-AUTH-*` for JavaScript and TypeScript. Apply `WEB-*` only when browser or HTTP behavior is present.

## JSTS-001 — Validate beyond the type system

**Rule:** Use strict static types, but narrow and validate external JSON, DOM, storage, message, and network values at runtime.

**Practice:** Treat `unknown` as untrusted until checked and avoid unchecked casts that merely suppress uncertainty.

**Boundary:** Values constructed entirely within a trusted typed module may not need repeated validation.

## JSTS-002 — Own promises and cancellation

**Rule:** Await or return promises, observe rejection, and propagate abort signals where the API supports them.

**Practice:** Prevent unhandled work from outliving its component or request without an explicit background owner.

**Boundary:** Deliberate telemetry may be best-effort only when failure and resource bounds are defined.

## JSTS-003 — Declare runtime and module contracts

**Rule:** Browser, worker, server runtime, module system, exports, bundler assumptions, and supported syntax must be explicit.

**Practice:** Avoid incidental interop between ESM, CommonJS, globals, and environment-specific APIs.

**Boundary:** Tool-generated compatibility wrappers remain acceptable when their output is controlled and tested.

## JSTS-004 — Clean up eventful resources

**Rule:** Listeners, timers, observers, subscriptions, streams, object URLs, and workers need explicit lifecycle ownership.

**Practice:** Remove or abort them during component disposal, navigation, cancellation, or replacement.

**Boundary:** Process-lifetime listeners may remain when they are intentionally singleton and bounded.

## JSTS-005 — Treat browser-delivered configuration as public

**Rule:** Secrets and privileged credentials must never be embedded in client bundles, source maps, HTML, or public build variables.

**Practice:** Keep protected operations server-side and expose only safe public configuration.

**Boundary:** Public identifiers and feature presentation flags are not secrets, but still require validation server-side.

## JSTS-006 — Avoid implicit coercion at important boundaries

**Rule:** Numbers, booleans, dates, empty values, and identifiers should be parsed and compared with explicit semantics.

**Practice:** Prefer strict equality and validate range, precision, and missing-value behavior.

**Boundary:** Familiar idiomatic coercion is acceptable inside a tightly scoped expression with no ambiguity.

## JSTS-007 — Do not bypass reactive state ownership

**Rule:** Proxy-, signal-, observer-, and framework-managed state must change through the mechanism that owns dependency tracking and notification.

**Practice:** Avoid mutating raw objects behind reactive wrappers, leaking mutable references across stores, or assuming structural equality captures proxy identity and change semantics.

**Boundary:** Plain local state remains appropriate when no reactive consumer or framework lifecycle depends on observing it.

## JSTS-008 — Verify the emitted artifact

**Rule:** Type checking and source tests do not prove bundling, tree-shaking, assets, environment substitution, or browser/server compatibility.

**Practice:** Test the produced package or bundle in each supported runtime at the appropriate release stage.

**Boundary:** Focused source checks remain efficient during iteration when their evidence level is explicit.
