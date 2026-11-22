# Interfaces, Contracts, and Serialization

These tenets extend `CORE-MOD-004`, `CORE-MOD-008`, `CORE-AUTH-006`, `CORE-FAIL-003`, and `CORE-FAIL-007`.

## API-001 — Model owned capabilities, not transport mechanics

**Rule:** Public interfaces should describe domain operations and outcomes while HTTP, RPC, queues, files, and databases remain replaceable adapters.

**Practice:** Keep transport headers, paths, credentials, and vendor types out of the domain contract unless they are the domain itself.

**Boundary:** A protocol library may intentionally expose protocol mechanics because those mechanics are its owned behavior.

## API-002 — Define complete operation semantics

**Rule:** A contract must define success, rejection, side effects, partial completion, cancellation, ordering, ownership, and retry behavior where those states matter.

**Practice:** Make ambiguous outcomes and uncertain completion explicit rather than forcing callers to infer them from messages.

**Boundary:** Keep the surface proportional; omit states that cannot occur under the contract.

## API-003 — Bound collections, streams, and batches

**Rule:** Operations that can return or accept growing data require explicit limits, ordering, pagination or continuation, and termination behavior.

**Practice:** Define maximum item and byte counts, stable ordering, cursor scope, and behavior when the underlying set changes.

**Boundary:** Small fixed-cardinality collections may use a simpler complete response.

## API-004 — Version behavioral boundaries

**Rule:** Evolve meaning, validation, ownership, compatibility, and wire representation according to the contract's declared evolution policy.

**Practice:** Define additive-change rules, supported version windows, unknown-field and unknown-version behavior, and migration or negotiation paths.

**Boundary:** Require a new incompatible version when supported consumers cannot safely interpret the change; compatible additive evolution and internal refactoring need not create unnecessary versions.

## API-005 — Add before removing

**Rule:** Introduce compatible replacements before retiring old behavior, then remove compatibility only after adoption and rollback are proven.

**Practice:** Publish mappings, deprecation signals, usage evidence, and an explicit retirement condition.

**Boundary:** Dangerous or compromised behavior may require accelerated removal with a separately approved mitigation.

## API-006 — Keep errors stable, safe, and useful

**Rule:** Machine-readable error categories and fields should remain stable while human diagnostics stay bounded and non-sensitive.

**Practice:** Separate category, operation, retryability, correlation, and safe detail from localized display text.

**Boundary:** Internal stack details belong only in appropriately protected diagnostics, not public payloads.

## API-007 — Make resource lifetime explicit

**Rule:** Contracts for streams, sessions, handles, subscriptions, leases, and jobs must define creation, ownership, renewal, closure, expiry, and recovery.

**Practice:** State which side is responsible for cleanup and what happens after disconnection or process loss.

**Boundary:** Process-scoped resources with no external lifetime may rely on normal language ownership when that behavior is unambiguous.

## API-008 — Canonicalize bytes that are compared or trusted

**Rule:** Signed, hashed, deduplicated, cached, or equality-compared payloads need one canonical encoding.

**Practice:** Specify field ordering, whitespace, number and timestamp forms, Unicode handling, null behavior, and version.

**Boundary:** Parsed semantic comparison is sufficient when byte identity has no security, cache, or interoperability consequence.

## API-009 — Validate both producer and consumer compatibility

**Rule:** Producer success alone does not prove that consumers interpret the same contract correctly.

**Practice:** Use shared fixtures, schema checks, contract tests, or compatibility matrices across supported versions.

**Boundary:** Avoid one test package becoming the contract authority when the published specification says otherwise.

## API-010 — Separate correlation, revision, and idempotency

**Rule:** Diagnostic correlation, optimistic concurrency, and retry deduplication solve different problems and require distinct identifiers.

**Practice:** Use correlation to trace, expected revisions to reject stale writes, and idempotency keys to make equivalent retries deterministic.

**Boundary:** These mechanisms do not make arbitrary external side effects globally exactly once.
