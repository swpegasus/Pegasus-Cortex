# Failures, Diagnostics, and Evidence

## CORE-FAIL-001 — Diagnose the root cause before proposing a workaround

**Rule:** Identify the actual failing layer and violated expectation instead of bypassing symptoms.

**Practice:** Inspect current source, configuration, inputs, logs, process state, environment, and invoked artifact until the failure is attributable.

**Boundary:** If root-cause correction requires unavailable authority or a material architecture choice, report the evidence and smallest decision needed; do not silently weaken the system.

## CORE-FAIL-002 — Never convert failure into false success

**Rule:** Containment may keep independent work alive, but the failed operation must remain observable as failed, partial, uncertain, blocked, or requiring recovery.

**Practice:** Propagate or persist an honest outcome and preserve enough bounded context for correction.

**Boundary:** Best-effort operations may succeed partially only when partial semantics are explicit and safe.

## CORE-FAIL-003 — Make diagnostics actionable and bounded

**Rule:** Diagnostics should identify the operation, failure category, relevant state, exit or protocol result, and underlying cause without flooding or leaking data.

**Practice:** Use stable error categories, correlation identifiers, structured fields, and truncated output tails where callers or operators depend on them.

**Boundary:** Do not expose credentials, private content, arbitrary paths, or complete untrusted payloads merely for convenience.

## CORE-FAIL-004 — Separate failure domains

**Rule:** Distinguish source defects from stale artifacts, test harnesses, invocation mistakes, environment configuration, dependency outages, expected-negative cases, and integration defects.

**Practice:** Reproduce at the narrowest trustworthy boundary before changing product code.

**Boundary:** Environment or harness failures can still reveal product robustness gaps, but they are not automatically proof of a source defect.

## CORE-FAIL-005 — Calibrate evidence to the exercised boundary

**Rule:** Label evidence as static, unit, fixture, integration, subprocess, packaged, installed-system, staging, or production.

**Practice:** State exactly which artifact, configuration, platform, and path produced the evidence.

**Boundary:** Evidence from a lower or simulated boundary must not be generalized to a higher real boundary without validation.

## CORE-FAIL-006 — Preserve cancellation and cleanup failures

**Rule:** Cancellation, timeout, disposal, rollback, and cleanup are observable outcomes, not invisible background details.

**Practice:** Report incomplete cleanup, await asynchronous disposal where required, and retain recoverable state when completion is uncertain.

**Boundary:** Forced termination is a bounded escalation, not proof that resources, children, locks, or external work were cleaned up.

## CORE-FAIL-007 — Retry only when semantics permit it

**Rule:** Retry transient failures only with bounded attempts, delay, cancellation, and understood idempotency.

**Practice:** Record the final failure and preserve the original causal context.

**Boundary:** Do not retry destructive, financial, externally visible, or non-idempotent operations unless a durable deduplication or reconciliation mechanism exists.
