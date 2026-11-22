# Privacy and Data Lifecycle

These tenets extend `CORE-AUTH-004`, `CORE-AUTH-006`, `DATA-002`, `DATA-010`, and `OBS-007`.

## PRIV-001 — Minimize data by purpose

**Rule:** Collect, retain, expose, replicate, and derive only the data required for a declared behavior or obligation.

**Practice:** Identify purpose and owner before adding fields, telemetry, uploads, or long-lived history.

**Boundary:** Future analytical value alone is not sufficient justification for indefinite collection.

## PRIV-002 — Classify data at the boundary

**Rule:** Sensitivity, ownership, tenant, retention, and permitted use should be known when data enters durable or external processing.

**Practice:** Carry classification through storage, logs, exports, backups, and derived outputs.

**Boundary:** Classification labels do not replace access control or encryption.

## PRIV-003 — Keep purpose changes explicit

**Rule:** Reusing data for a materially different purpose requires a new authority, policy, or user decision rather than silent scope expansion.

**Practice:** Review affected consent, disclosure, retention, model use, and downstream copies.

**Boundary:** Closely related maintenance needed to deliver the original purpose may remain within the original scope when policy allows it.

## PRIV-004 — Separate immutable history from revocable access

**Rule:** Preserve provenance and historical integrity while modeling visibility, authorization, retention, and revocation as distinct governed state.

**Practice:** Avoid deleting or rewriting evidence merely to revoke ordinary access.

**Boundary:** Authorized erasure or legal deletion is a separate destructive operation and may require removing content while preserving minimal proof of action.

## PRIV-005 — Make access, export, correction, and deletion operable

**Rule:** Data rights and lifecycle promises require complete paths across primary, derived, cached, archived, and backed-up state.

**Practice:** Define identity verification, scope, exceptions, completion evidence, and delayed propagation.

**Boundary:** Do not claim immediate deletion where immutable backups follow a disclosed expiry process.

## PRIV-006 — Protect test and development data

**Rule:** Nonproduction environments should use synthetic, minimized, or appropriately transformed data unless real data is explicitly authorized and protected.

**Practice:** Keep production credentials and unrestricted copies out of developer fixtures, screenshots, logs, and bug reports.

**Boundary:** Deidentification must be evaluated against reidentification risk and retained linkage.

## PRIV-007 — Keep derived outputs attributable and bounded

**Rule:** Summaries, embeddings, indexes, models, and analytics must carry source lineage, scope, retention, and deletion behavior appropriate to their sensitivity.

**Practice:** Do not assume transformation removes privacy obligations.

**Boundary:** Public aggregate statistics may use a simpler lifecycle only after disclosure risk is genuinely bounded.

## PRIV-008 — Make privacy failure visible without amplifying it

**Rule:** Report unauthorized access, retention failure, or disclosure with enough evidence to contain and correct it while avoiding further spread.

**Practice:** Use protected incident channels, bounded identifiers, access review, and explicit cleanup tracking.

**Boundary:** Ordinary public diagnostics must never reproduce the exposed content.
