# Adding Features

Use `REQ-*` for accepted behavior, `API-*` and `DATA-*` for changed contracts and state, `OBS-*` for operability, `CORE-VERIFY-003` for batching, and `CORE-DELIVERY-004` for final verification and cleanup rather than duplicating those rules here.

## ADD-001 — Turn accepted behavior into one owned slice

**Rule:** Translate established requirements and acceptance evidence into the smallest coherent implementation slice with explicit component ownership.

**Practice:** Map the relevant `REQ-*` items to entry point, contract, state, failure boundary, and verification.

**Boundary:** Do not invent detailed future requirements solely to make the first design more general.

## ADD-002 — Trace the existing execution path

**Rule:** Understand current entry points, contracts, state, dependencies, and tests before selecting where to add behavior.

**Practice:** Search for analogous capabilities and stable extension points, then confirm their actual semantics.

**Boundary:** Similar names or types do not prove that an existing path has compatible ownership or guarantees.

## ADD-003 — Map the feature onto the canonical path

**Rule:** After tracing the existing path, identify the exact owned extension point, adapters, registrations, and consumers that must change for the feature.

**Practice:** Apply `CORE-MOD-005`, preserve established validation, authorization, persistence, cancellation, and diagnostics, and add a new seam only when the current owner cannot safely represent the requirement.

**Boundary:** When the existing path is demonstrably unsuitable, define the replacement, migration, compatibility window, and rollback explicitly.

## ADD-004 — Include the complete failure boundary

**Rule:** A feature slice includes validation, authorization, cancellation, cleanup, and honest failure behavior needed for its real path.

**Practice:** Define how partial work, retries, timeouts, unavailable dependencies, and rejected inputs become observable.

**Boundary:** Do not broaden into exhaustive resilience scenarios unrelated to the current risk and acceptance criteria.

## ADD-005 — Plan contract evolution with the feature

**Rule:** Identify affected APIs, protocols, events, schemas, configuration, and stored data before committing the implementation shape.

**Practice:** Apply `API-*`, `DATA-*`, `CONFIG-*`, and `PORT-010` to define versioning, coexistence, migration, and retirement work in the same slice.

**Boundary:** Internal implementation changes need no new public version when externally observable behavior remains compatible.

## ADD-006 — Connect behavior to canonical operational signals

**Rule:** New behavior should reuse the owned logs, metrics, traces, health, status, and audit channels appropriate to its public outcomes.

**Practice:** Map important transitions to `OBS-*` and add only the minimum new signal needed to operate the feature safely.

**Boundary:** Observability is not permission to log secrets, private inputs, complete prompts, or arbitrary content.
