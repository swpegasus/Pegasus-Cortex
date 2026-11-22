# User-Facing Quality and Accessibility

These tenets extend `CORE-DELIVERY-001`, `CORE-FAIL-002`, `CORE-AUTH-005`, `PLAY-006`, and `PORT-006`.

## UI-001 — Design around user tasks and state

**Rule:** Interfaces should expose the user's workflow and current outcome rather than mirror internal component boundaries.

**Practice:** Make the next valid action, ownership, affected target, and completion state understandable.

**Boundary:** Administrative and diagnostic interfaces may expose internal structure when their users need it.

## UI-002 — Make asynchronous state visible

**Rule:** Pending, cancellable, succeeded, failed, partial, offline, stale, and recovering states must not collapse into a spinner or false success.

**Practice:** Show progress or bounded status, allow safe cancellation, and preserve the terminal result.

**Boundary:** Very short deterministic actions need not flash distracting transient state.

## UI-003 — Preserve user input on recoverable failure

**Rule:** Validation, network, or transient service failures should not destroy work the user can correct or retry.

**Practice:** Retain values safely, identify affected fields or actions, and offer a clear recovery path.

**Boundary:** Do not retain secrets or prohibited data merely for convenience.

## UI-004 — Make accessibility part of behavior

**Rule:** Keyboard access, focus order, semantics, labels, contrast, text scaling, and assistive output belong in the acceptance criteria.

**Practice:** Prefer native semantic controls and verify representative keyboard and assistive workflows.

**Boundary:** Automated accessibility checks support but do not replace human evaluation where consequence warrants it.

## UI-005 — Do not encode meaning through one sensory channel

**Rule:** Color, sound, motion, position, hover, or shape alone must not carry essential meaning.

**Practice:** Pair cues with text, semantics, patterns, captions, or another independent channel.

**Boundary:** Decorative effects may remain single-channel when no information depends on them.

## UI-006 — Make destructive effects reviewable

**Rule:** Before an irreversible or high-cost action, show the exact target, consequence, scope, and recovery availability.

**Practice:** Prefer undo, soft deletion, preview, or staged confirmation when feasible.

**Boundary:** Repetitive low-risk actions should not be burdened with confirmation dialogs that users learn to ignore.

## UI-007 — Keep interaction responsive and cancellable

**Rule:** Long work must not freeze essential interaction and should expose cancellation or background ownership where safe.

**Practice:** Move blocking work off the UI path, throttle updates, and report final cleanup or partial effects.

**Boundary:** Cancellation is not appropriate after an indivisible irreversible effect has begun; explain the state honestly.

## UI-008 — Design for variable content and environments

**Rule:** Empty states, long values, localization, text expansion, narrow layouts, high scaling, and missing optional media must retain meaning.

**Practice:** Avoid fixed assumptions that truncate controls, hide errors, or change operation scope.

**Boundary:** Specialized fixed-display systems may use constrained layouts when the environment is truly controlled.

## UI-009 — Separate human judgment from automated evidence

**Rule:** Automation can verify mechanics and accessibility properties but cannot by itself prove clarity, comfort, aesthetics, or usability.

**Practice:** Label human observation, automated checks, simulations, and user research at their actual evidence level.

**Boundary:** Human preference is not a substitute for deterministic correctness or accessibility requirements.

## UI-010 — Protect user trust through reversible feedback

**Rule:** Interfaces must not claim completion before durable responsibility is accepted or hide later failure after optimistic feedback.

**Practice:** Distinguish queued, accepted, saved, synchronized, published, and verified outcomes and reconcile optimistic state visibly.

**Boundary:** Optimistic interaction is valid when conflicts and rollback are designed and understandable.
