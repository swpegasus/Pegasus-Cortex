# Refactoring Workflow

This workflow applies `CORE-MOD-*`, `CORE-WORK-006`, `MAINT-005`, and `MAINT-006` to behavior-preserving structural change.

## REFACTOR-001 — Characterize behavior before risky change

**Rule:** Capture the observable behavior, public contracts, persistence, and invariants that must remain stable.

**Practice:** Add focused characterization tests or evidence at the affected boundary before restructuring.

**Boundary:** Clearly incorrect behavior should not be frozen accidentally; decide whether its correction is part of the change.

## REFACTOR-002 — Separate mechanical and behavioral change

**Rule:** Keep renames, moves, extraction, formatting, and behavior changes attributable when practical.

**Practice:** Use bounded steps whose effect can be reviewed and verified independently.

**Boundary:** A small inseparable correction may remain in one patch when splitting would create misleading intermediate states.

## REFACTOR-003 — Move in reversible increments

**Rule:** Preserve a working path while migrating structure, callers, and data in coherent steps.

**Practice:** Verify after meaningful batches and retain rollback until the replacement is proven.

**Boundary:** Avoid permanent dual implementations; transitional duplication needs an owner and removal condition.

## REFACTOR-004 — Migrate callers onto the canonical path

**Rule:** A refactor that consolidates behavior should inventory registrations and consumers, move them to the destination, and prove the obsolete route is no longer selected.

**Practice:** Apply `CORE-MOD-005`, migrate callers in bounded groups, and observe the canonical path before removing compatibility adapters.

**Boundary:** Separate implementations remain valid when their invariants, ownership, or platform behavior differ materially.

## REFACTOR-005 — Define the extraction boundary before moving code

**Rule:** Before extracting code, state the destination responsibility, owned state, dependencies, public surface, and lifecycle that justify the new boundary.

**Practice:** Apply `CORE-MOD-001`, move one coherent responsibility and its tests, and minimize pass-through layers with no policy or abstraction value.

**Boundary:** File size can signal review difficulty, but is not itself a design boundary.

## REFACTOR-006 — Preserve compatibility deliberately

**Rule:** Public names, formats, storage, routes, and behavior require versioned migration or deprecation rather than silent replacement.

**Practice:** Use `API-005` as the canonical deprecation lifecycle, then apply the relevant platform-support and maintenance evidence from `PORT-010` and `MAINT-005`.

**Boundary:** Purely private structure may change freely after confirming no dynamic or external consumers.

## REFACTOR-007 — Judge improvement by clarity and risk

**Rule:** A successful refactor reduces conceptual duplication, ambiguous ownership, unsafe coupling, or change cost while preserving required behavior.

**Practice:** Compare before and after responsibility, surface area, dependencies, tests, and failure handling.

**Boundary:** Fewer lines, classes, or files are not automatically better.
