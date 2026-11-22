# Domain Modeling, Types, and Identifiers

These tenets specialize `CORE-MOD-001` through `CORE-MOD-006` for representing meaning in code and data.

## MODEL-001 — Represent one domain fact once

**Rule:** Keep one authoritative representation of a fact and derive alternate displays, units, summaries, or projections from it.

**Practice:** Centralize conversions and transformations instead of maintaining parallel fields or classes that can drift.

**Boundary:** Separate representations are appropriate when ownership, lifecycle, precision, security, or consistency requirements genuinely differ.

## MODEL-002 — Separate canonical values from presentation forms

**Rule:** A domain value should have one canonical meaning while units, localized text, display formats, and alternate views remain explicit conversions or projections.

**Practice:** Preserve precision and invariants in the value type, convert units deliberately, and format only at presentation or protocol boundaries.

**Boundary:** Values with different invariants or meanings remain different types even when their current representation looks identical.

## MODEL-003 — Make units and precision explicit

**Rule:** Values with units, scale, currency, precision, or coordinate systems must carry that meaning at their boundary.

**Practice:** Use named types, parameters, or metadata and convert in one reviewed location.

**Boundary:** Primitive values remain reasonable inside a tightly scoped implementation where the unit cannot be confused.

## MODEL-004 — Separate stable identity from display metadata

**Rule:** Ownership and references should use stable opaque identifiers rather than mutable names, labels, paths, or positions.

**Practice:** Treat names and labels as changeable presentation data and preserve explicit mapping when identities migrate.

**Boundary:** Human-readable natural keys may be authoritative only when their uniqueness and immutability are genuine domain rules.

## MODEL-005 — Distinguish absent, unknown, invalid, and empty

**Rule:** Different state meanings must not collapse into one null, zero, empty string, or default value.

**Practice:** Use explicit variants, validation results, or status fields when callers must respond differently.

**Boundary:** Avoid elaborate sum types when the distinction has no behavioral consequence.

## MODEL-006 — Put invariants near the owning type

**Rule:** Construction and mutation should preserve the rules that make a value valid rather than relying on every caller to remember them.

**Practice:** Validate at creation, expose purposeful operations, and avoid public setters that permit impossible combinations.

**Boundary:** External payload models may first capture untrusted shape, but conversion into the domain model must validate it.

## MODEL-007 — Keep derived state visibly derived

**Rule:** Cached summaries, indexes, projections, and denormalized values must identify their source revision and rebuild or reconciliation behavior.

**Practice:** Prevent derived state from silently becoming a second authority.

**Boundary:** A projection may become authoritative only through an explicit ownership transfer and migration.

## MODEL-008 — Prefer explicit state machines for lifecycle behavior

**Rule:** When valid actions depend on lifecycle state, model allowed states and transitions rather than scattering booleans and conditionals.

**Practice:** Define terminal states, invalid transitions, recovery transitions, and observable outcomes in one owned model.

**Boundary:** Simple independent flags are appropriate when they do not encode a shared lifecycle.

## MODEL-009 — Name by responsibility and effect

**Rule:** Names should reveal the owned concept, observable effect, and important unit or scope rather than implementation history.

**Practice:** Use consistent domain vocabulary and reserve ambiguous words such as manager, helper, data, or process for cases where they are genuinely precise.

**Boundary:** Follow established public vocabulary when changing a name would create more compatibility cost than clarity.
