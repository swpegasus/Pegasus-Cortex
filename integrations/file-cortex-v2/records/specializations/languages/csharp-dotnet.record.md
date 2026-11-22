# C# and .NET Tenets

These tenets specialize `CONC-*`, `DEP-*`, `PORT-*`, and `CORE-FAIL-*` for C# and .NET.

## DOTNET-001 — Preserve asynchronous composition

**Rule:** Return and await tasks, propagate cancellation tokens, and avoid synchronous waits or unowned `async void`.

**Practice:** Keep asynchronous behavior continuous across I/O and service boundaries.

**Boundary:** Event handlers may require `async void`, but must contain and report failure deliberately.

## DOTNET-002 — Make disposal ownership explicit

**Rule:** Dispose only resources the current scope owns and use synchronous or asynchronous disposal according to the resource contract.

**Practice:** Use `using` or `await using` and avoid disposing injected shared instances owned by another lifetime.

**Boundary:** Process-lifetime resources may be released by the owning host during shutdown.

## DOTNET-003 — Use nullable contracts honestly

**Rule:** Enable and respect nullable analysis where practical and validate external data before treating it as non-null.

**Practice:** Avoid the null-forgiving operator merely to silence unresolved uncertainty.

**Boundary:** Interop and legacy APIs may need narrow documented suppression after runtime checks.

## DOTNET-004 — Preserve exception identity

**Rule:** Catch only where policy or context can be added and rethrow without losing the original stack.

**Practice:** Distinguish cancellation, expected domain rejection, transient dependency failure, and programming defects.

**Boundary:** Public boundaries may translate exceptions into stable result contracts while retaining protected diagnostics.

## DOTNET-005 — Respect dependency-injection lifetimes

**Rule:** Do not capture scoped or transient state in longer-lived services or replace explicit dependencies with service location.

**Practice:** Align object lifetime with owned resources, concurrency, and disposal.

**Boundary:** Factories may resolve dynamic dependencies when their lifetime and scope remain explicit.

## DOTNET-006 — Control deferred enumeration

**Rule:** Understand when enumerable and query expressions execute, how often, and under which resource lifetime.

**Practice:** Materialize at ownership boundaries and avoid repeated queries or side effects hidden behind enumeration.

**Boundary:** Streaming enumeration is preferable when it remains single-pass, bounded, and properly disposed.

## DOTNET-007 — Make culture and serialization explicit

**Rule:** .NET parsing, formatting, comparison, and serialization boundaries need explicit `CultureInfo`, `StringComparison`, encoding, and versioned serializer options.

**Practice:** Use invariant formats and ordinal or declared comparisons for machine contracts, user culture for presentation, and the same owned serializer options across supported producers and consumers.

**Boundary:** Default serializer behavior is acceptable only when it is part of the versioned contract.

## DOTNET-008 — Declare target and deployment model

**Rule:** Framework version, runtime identifier, architecture, self-contained or framework-dependent mode, trimming, and native interop affect compatibility.

**Practice:** Test the actual published artifact for each supported deployment model.

**Boundary:** A library may target a broader contract than its consuming applications, but must test its supported targets.
