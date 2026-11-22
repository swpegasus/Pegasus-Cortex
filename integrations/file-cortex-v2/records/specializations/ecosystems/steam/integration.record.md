# Steam Integration Tenets

These rules cover broadly recurring Steamworks integration boundaries. Feature-specific systems such as Workshop, inventory, trading, matchmaking, anti-cheat, and VR require their own reviewed design and are intentionally excluded.

They specialize `CORE-MOD-001`, `CORE-AUTH-006`, `CORE-AUTH-007`, `CORE-FAIL-006`, `API-*`, `DATA-*`, `NET-*`, and `RELEASE-*` without replacing those general boundaries.

## STEAM-001 — Isolate Steam behind an adapter

**Rule:** Core application behavior should not depend directly on SDK-specific global state or callbacks.

**Practice:** Wrap initialization, identity, achievements, cloud, and other selected capabilities behind focused local contracts.

**Boundary:** Steam-specific user-visible behavior may remain platform-dependent, but its failure must not corrupt unrelated core state.

## STEAM-002 — Use supported SDK and environment identities

**Rule:** Integrate through the current supported Steamworks interfaces and keep development, staging, and production application identities explicit.

**Practice:** Validate the effective application ID and environment during build, launch, package, and release checks.

**Boundary:** Never ship development application-ID configuration or assume the launch environment selects the correct identity automatically.

## STEAM-003 — Treat client availability as runtime state

**Rule:** Steam client unavailable, offline, not logged in, initializing, ready, and shutting down are distinct states.

**Practice:** Define degraded behavior, timeouts, diagnostics, and recovery without blocking unrelated functionality unnecessarily.

**Boundary:** A product that contractually requires Steam may fail closed, but should report the exact missing state.

## STEAM-004 — Own callback lifecycle explicitly

**Rule:** Initialization, callback pumping, asynchronous completion, cancellation, and shutdown require deliberate ownership.

**Practice:** Prevent callbacks after disposal, duplicate subscriptions, orphaned operations, and shutdown races.

**Boundary:** Process exit is not a substitute for orderly persistence and callback cleanup.

## STEAM-005 — Protect authentication material

**Rule:** Authentication tickets, session material, and persistent user identifiers are sensitive inputs.

**Practice:** Transmit tickets only to authorized validation services, bound their lifetime and audience, and redact logs.

**Boundary:** Client-provided identity or achievement claims must not be trusted for competitive or server-authoritative decisions.

## STEAM-006 — Make stats and achievements idempotent

**Rule:** Repeated synchronization must not duplicate, regress, or corrupt progress.

**Practice:** Use stable identifiers, monotonic or explicitly reconciled updates, and observable store/flush results.

**Boundary:** Local cached success is not proof that Steam accepted or persisted the update.

## STEAM-007 — Treat cloud saves as versioned user data

**Rule:** Cloud synchronization requires atomic local writes, version or revision identity, conflict detection, and visible resolution behavior.

**Practice:** Preserve recoverable copies during conflict handling and validate compatibility before replacing local state.

**Boundary:** Timestamp-only “newest wins” behavior is unsafe when clocks, offline edits, or multiple devices can diverge.

## STEAM-008 — Test enabled and degraded paths

**Rule:** When the product supports operation without Steam, verify both Steam-enabled behavior and the declared degraded path.

**Practice:** Separate SDK mock tests from tests using the actual client and intended application environment.

**Boundary:** Do not use production accounts, publish depots, or promote releases merely to prove ordinary integration.

## STEAM-009 — Gate publication separately

**Rule:** Build success does not authorize depot upload, branch promotion, store changes, or release publication.

**Practice:** Make target application, depot, branch, artifact identity, release notes, rollback, and approval explicit.

**Boundary:** Keep credentials and publishing authority outside ordinary developer builds and logs.
