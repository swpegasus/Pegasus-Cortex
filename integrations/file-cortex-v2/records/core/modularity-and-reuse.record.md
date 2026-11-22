# Modularity and Reuse

## CORE-MOD-001 — Organize by responsibility and ownership

**Rule:** Group code around coherent roles such as networking, persistence, identity, lifecycle, presentation, diagnostics, and platform integration.

**Practice:** Keep each module's inputs, state changes, dependencies, and outputs understandable without traversing unrelated concerns.

**Boundary:** Do not split code merely to reduce file size; split when responsibilities, ownership, change cadence, or failure boundaries differ.

## CORE-MOD-002 — Reuse behavior only when semantics match

**Rule:** Share an abstraction when callers require equivalent validation, durability, authorization, serialization, and failure behavior.

**Practice:** Route equivalent operations through one tested engine or contract rather than convenience bypasses.

**Boundary:** Similar syntax is not sufficient reason to merge behavior with different ownership or invariants.

## CORE-MOD-003 — Prefer cohesive configurable abstractions

**Rule:** Replace near-duplicate functions or classes with one clearly named abstraction using bounded, typed options when the underlying behavior is the same.

**Practice:** Represent variations such as units, formats, modes, or strategies as validated values or implementations behind a stable interface.

**Boundary:** Do not create a generic utility with flags for unrelated behavior merely to remove a few repeated lines.

## CORE-MOD-004 — Keep public surfaces small and purposeful

**Rule:** Public operations should be typed, bounded, versionable, and aligned with actual use cases.

**Practice:** Expose focused capabilities and keep implementation details private.

**Boundary:** Avoid generic command, path, query, or arbitrary-execution escape hatches unless arbitrary execution is the explicit product purpose and is strongly contained.

## CORE-MOD-005 — Extend existing paths before creating parallel ones

**Rule:** Trace the current execution path and reuse its stable extension points before adding a replacement tree, service, state machine, or data model.

**Practice:** Repair or extend the canonical implementation when compatible rather than avoiding the work through duplication.

**Boundary:** A replacement is justified when the existing boundary is demonstrably unsafe, incompatible, or unable to meet the requirement; migration and rollback must then be explicit.

## CORE-MOD-006 — Abstract demonstrated variability

**Rule:** Introduce frameworks and extension layers for observed shared behavior or important protected boundaries, not hypothetical future variants.

**Practice:** Start with the smallest stable seam that supports the current requirement and known near-term consumers.

**Boundary:** Security, platform isolation, persistence, and external-provider boundaries may justify an interface before multiple implementations exist.

## CORE-MOD-007 — Prefer trusted libraries with local boundaries

**Rule:** Use established libraries for complex, well-solved problems when they improve correctness and maintainability.

**Practice:** Evaluate security, licensing, support, platform coverage, size, update policy, and failure behavior; isolate material dependencies behind local interfaces.

**Boundary:** Do not add dependencies for trivial behavior or allow library-specific types to spread through unrelated core code.

## CORE-MOD-008 — Version shared contracts deliberately

**Rule:** Changes to persisted data, public APIs, protocols, or cross-component contracts require explicit compatibility semantics.

**Practice:** Define version negotiation, migration, unknown-field behavior, deprecation, and rollback before changing consumers.

**Boundary:** Internal refactors that do not alter observable behavior should not create unnecessary public versions.

## CORE-MOD-009 — Make dependencies and side effects explicit

**Rule:** Behavior should receive its required dependencies and expose material effects through owned boundaries instead of reaching into hidden global or ambient state.

**Practice:** Pass values or focused interfaces for configuration, time, randomness, persistence, transport, filesystem, network, and process effects; return typed outcomes and keep mutable ownership visible.

**Boundary:** Immutable constants and genuinely process-wide platform services may remain global when ownership and lifetime are unambiguous. Do not introduce a dependency-injection framework merely for style.
