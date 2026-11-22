# Code Review Workflow

This workflow applies the library's authority, contract, evidence, and maintenance tenets to reviewing changes.

## REVIEW-001 — Establish intent and scope first

**Rule:** Review against the requested outcome, applicable instructions, ownership, and acceptance evidence rather than personal style preference.

**Practice:** Identify changed behavior, affected boundaries, and explicit exclusions before assessing implementation details.

**Boundary:** A review should still surface material risks outside the stated happy path when the change creates them.

## REVIEW-002 — Inspect high-consequence boundaries first

**Rule:** Prioritize authorization, secrets, destructive paths, persistence, migrations, concurrency, external effects, and public compatibility before cosmetic concerns.

**Practice:** Trace untrusted input to effect and durable state to recovery.

**Boundary:** Low-risk changes may need only a focused subset of this review.

## REVIEW-003 — Keep the diff attributable

**Rule:** Unrelated formatting, generated output, dependency churn, and opportunistic cleanup should not obscure the behavior under review.

**Practice:** Ask for separation when mixed changes materially increase risk or hide causality.

**Boundary:** Tiny adjacent cleanup is acceptable when it improves the changed path and remains easy to verify.

## REVIEW-004 — Review failure, cancellation, and cleanup

**Rule:** Evaluate rejection, partial completion, retries, concurrency, cancellation, disposal, and recovery—not only successful control flow.

**Practice:** Check that terminal states remain honest and resources are released under each important outcome.

**Boundary:** Review depth should follow demonstrated reach and consequence.

## REVIEW-005 — Evaluate tests as evidence

**Rule:** Confirm that tests exercise the changed observable behavior and important failure boundary with current artifacts.

**Practice:** Note gaps in integration, compatibility, persistence, security, or release evidence at their actual level.

**Boundary:** More tests are not automatically better when they duplicate coverage or create ambiguous failures.

## REVIEW-006 — Make feedback actionable and prioritized

**Rule:** Each finding should state the defect or risk, evidence, consequence, and smallest useful correction.

**Practice:** Distinguish blocker, required correction, suggestion, and question.

**Boundary:** Avoid prescribing one implementation when several satisfy the protected property.

## REVIEW-007 — Verify assumptions before asserting defects

**Rule:** Inspect relevant source, contract, configuration, and call paths before reporting a finding as fact.

**Practice:** Label uncertainty and request evidence when the behavior cannot be established locally.

**Boundary:** Obvious dangerous patterns may be flagged promptly while deeper verification continues.

## REVIEW-008 — Preserve ownership during correction

**Rule:** Review does not authorize overwriting unrelated work, expanding scope, or taking another component's decision authority.

**Practice:** Direct findings to the owner and separate cross-component dependencies from local corrections.

**Boundary:** A reviewer may implement fixes when the user or workflow explicitly grants that scope.
