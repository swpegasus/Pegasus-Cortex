# Authority, Security, and Integrity

## CORE-AUTH-001 — Guidance never grants authority

**Rule:** Tenets, plans, documentation, memories, and retrieved knowledge guide execution but do not authorize mutations.

**Practice:** Derive authority from the current user request and applicable project rules, and distinguish reading, editing, building, packaging, deployment, and live operation.

**Boundary:** When sources conflict, follow the higher-priority current authority and surface material ambiguity.

## CORE-AUTH-002 — Fail closed at trust boundaries

**Rule:** Missing, invalid, stale, revoked, or ambiguous identity, authorization, trust, or configuration must not become implicit permission.

**Practice:** Validate issuer, audience, scope, revision, expiry, ownership, and requested operation where applicable.

**Boundary:** Degraded read-only behavior is acceptable only when explicitly designed and unable to leak or mutate protected state.

## CORE-AUTH-003 — Use least privilege and narrow elevation

**Rule:** Request only the permissions needed for the smallest operation and duration.

**Practice:** Separate planning from privileged execution, constrain resources, and release elevated context promptly.

**Boundary:** Convenience is not justification for persistent administrator, root, machine-wide, or broad network authority.

## CORE-AUTH-004 — Keep secrets out of ordinary state and diagnostics

**Rule:** Credentials, tokens, private keys, and sensitive payloads must not be embedded in source, normal configuration, logs, prompts, or general knowledge stores.

**Practice:** Use protected references or secret providers, redact diagnostics, and limit secret lifetime and audience.

**Boundary:** Hashing or encoding a secret does not automatically make it safe to persist or disclose.

## CORE-AUTH-005 — Contain destructive operations

**Rule:** Delete, move, overwrite, permission, and ownership operations require exact targets, validated scope, and an understood recovery path.

**Practice:** Resolve paths, enumerate targets, verify containment, preserve necessary state, and prefer reversible staging.

**Boundary:** Never apply recursive destructive operations to an ambiguous, empty, root-level, computed-but-unverified, or redirected target.

## CORE-AUTH-006 — Treat external input and output as bounded

**Rule:** Validate size, shape, encoding, count, time, and resource use at every external boundary.

**Practice:** Apply limits, cancellation, timeouts, safe parsing, and content-aware diagnostics before data reaches core behavior.

**Boundary:** Trusted transport does not make payload semantics trustworthy.

## CORE-AUTH-007 — Preserve provenance and state integrity

**Rule:** Durable decisions and mutations should identify their source, revision, actor or authority, time, and relationship to prior state.

**Practice:** Use append-only history, atomic transitions, digests, concurrency checks, or equivalent integrity controls when consequences justify them.

**Boundary:** Integrity evidence detects or prevents certain changes; it must not be described as rollback resistance or nonrepudiation unless those properties are actually provided.

## CORE-AUTH-008 — Keep irreversible and external mutations explicit

**Rule:** Machine-level changes, publication, deployment, account actions, financial operations, and external messages require explicit scope and confirmation appropriate to their impact.

**Practice:** Present the target, effect, rollback, and evidence before or after execution as required by the governing workflow.

**Boundary:** A successful local build or simulation never implies authorization to perform the external action.
