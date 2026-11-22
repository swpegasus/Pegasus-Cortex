# Dependencies and Supply Chain

These tenets extend `CORE-MOD-007`, `CORE-AUTH-007`, `BUILD-003`, and `BUILD-005`.

## DEP-001 — Define the dependency exit strategy

**Rule:** A material dependency decision must identify the capability it owns, the switching cost it creates, and how the product can update, isolate, replace, or remove it.

**Practice:** Apply `CORE-MOD-007`, record transitive and operational coupling, and keep owned contracts from unnecessarily exposing dependency-specific types.

**Boundary:** Deep ecosystem integration may make replacement intentionally expensive; the cost should be visible rather than discovered during an incident or migration.

## DEP-002 — Control package identity and source

**Rule:** Use exact package coordinates and approved sources, and defend against mutable substitution and dependency confusion.

**Practice:** Preserve registry, publisher, signature or integrity, and resolved version evidence where the ecosystem supports it.

**Boundary:** Local development packages must be visibly distinct from releasable dependencies.

## DEP-003 — Keep manifest, lock, and toolchain consistent

**Rule:** Use the repository's selected package manager and preserve authoritative lock or resolution state when supported.

**Practice:** Avoid hidden global packages and undeclared tools required to reproduce the build.

**Boundary:** Applications and reusable libraries may adopt different version-range policies for legitimate ecosystem reasons.

## DEP-004 — Review transitive impact

**Rule:** Evaluate the resolved dependency graph, native components, licenses, install scripts, network behavior, and runtime footprint rather than only the direct API.

**Practice:** Keep optional integrations isolated so their failure and removal do not corrupt unrelated behavior.

**Boundary:** Review depth should match privilege, exposure, update channel, and consequence.

## DEP-005 — Make updates attributable

**Rule:** Update a coherent dependency set with known compatibility reasons and focused consumer verification.

**Practice:** Review release notes, changed transitive packages, lockfile diff, runtime targets, and rollback path.

**Boundary:** Automated low-risk updates remain useful when policy, verification, and promotion gates are explicit.

## DEP-006 — Triage vulnerability reports in context

**Rule:** Confirm the resolved version, reachable functionality, exploit conditions, exposure, and supported correction before deciding urgency.

**Practice:** Do not ignore applicable advisories or apply unrelated breaking upgrades blindly.

**Boundary:** Active exploitation or critical exposed paths may justify an emergency mitigation before complete upgrade work.

## DEP-007 — Preserve license and release provenance

**Rule:** Release evidence should identify included dependencies, versions, licenses, sources, and integrity information proportionate to distribution risk.

**Practice:** Generate or retain an inventory through the same controlled build that produced the artifact.

**Boundary:** An inventory is evidence, not proof that every dependency is safe or legally suitable.

## DEP-008 — Keep acquisition reproducible and replaceable

**Rule:** A compatible build must not depend on one developer cache, vanished mutable URL, or undocumented private installation.

**Practice:** Pin justified inputs, mirror according to policy, and isolate vendor APIs behind owned boundaries when replacement risk matters.

**Boundary:** Protected private sources are valid when access, retention, and disaster recovery are deliberately owned.
