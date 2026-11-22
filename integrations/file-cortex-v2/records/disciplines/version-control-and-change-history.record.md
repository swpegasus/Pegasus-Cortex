# Version Control and Change History

These tenets apply `CORE-WORK-*`, `CORE-AUTH-007`, `CORE-COMM-005`, `REVIEW-*`, and `MAINT-*` across version-control systems. Git-specific index, worktree, branch, and remote mechanics remain in `GIT-*`.

## VCS-001 — Preserve coherent attributable changes

**Rule:** A recorded change should have one understandable purpose, identify its source and authoring context, and include the contracts, migrations, and verification needed to keep that purpose consistent.

**Practice:** Separate unrelated work while avoiding intermediate records that knowingly misrepresent a buildable, migratable, or reviewable state.

**Boundary:** The appropriate record size depends on the system and workflow; small is useful only while the change remains coherent.

## VCS-002 — Preserve shared history through explicit correction

**Rule:** Once others depend on a published history, correct it through visible successors, reversions, or governed amendment rather than silent destructive rewriting.

**Practice:** Coordinate any exceptional rewrite, identify affected consumers and references, and retain required audit or release provenance.

**Boundary:** An unpublished private history may be reorganized when its owner and workflow permit it.

## VCS-003 — Resolve merges semantically

**Rule:** A conflict-free text merge does not prove that combined behavior, contracts, generated artifacts, migrations, and tests remain correct.

**Practice:** Understand the intent of both changes, reconstruct the desired combined behavior, regenerate derived artifacts, and run focused verification.

**Boundary:** Mechanically choosing one side is safe only when evidence shows the other side is intentionally obsolete.

## VCS-004 — Keep authoritative and derived content intentional

**Rule:** Version source, contracts, migrations, fixtures, lock state, and generated artifacts according to explicit ownership and reproducibility policy.

**Practice:** Ignore caches, local credentials, transient state, and reproducible clutter while retaining derived outputs that are themselves an authoritative interface or release input.

**Boundary:** “Generated” does not mean disposable, and “checked in” does not make content authoritative.

## VCS-005 — Treat names as references, not trust

**Rule:** Branches, tags, labels, and human-readable versions identify references but do not alone prove immutable content, approval, signature, or release status.

**Practice:** Bind sensitive decisions to immutable revision or digest plus the required provenance and policy evidence.

**Boundary:** A protected naming workflow can carry useful policy state, but consumers must still validate the protection they rely on.

## VCS-006 — Make moves and removals traceable

**Rule:** Structural moves, renames, splits, consolidations, and removals should preserve enough attributable history and mapping for consumers and maintainers to follow the change.

**Practice:** Separate large mechanical movement from behavioral edits when practical and record replacement or migration paths for public locations.

**Boundary:** Tool-detected rename history is heuristic; clear change structure and handoff matter more than forcing a similarity score.
