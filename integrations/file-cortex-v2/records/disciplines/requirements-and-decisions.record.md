# Requirements and Decisions

These tenets extend `CORE-DELIVERY-001`, `CORE-DELIVERY-003`, `CORE-COMM-004`, and `CORE-FAIL-005` from implementation into requirements and architectural decisions.

## REQ-001 — Separate the need from the proposed solution

**Rule:** State the user, operational, or system need independently from the first implementation idea.

**Practice:** Record the desired outcome, affected actors, current limitation, and evidence of success before selecting technology or structure.

**Boundary:** A mandated public contract, platform constraint, or approved architecture may legitimately constrain the solution space.

## REQ-002 — Make acceptance observable

**Rule:** Express each material requirement as behavior or evidence that can be observed at the appropriate boundary.

**Practice:** Include success, rejection, degraded operation, cancellation, recovery, and important nonfunctional expectations where relevant.

**Boundary:** Not every internal quality needs a user-facing test, but it still needs a reviewable invariant or measurable signal.

## REQ-003 — Record assumptions as assumptions

**Rule:** Unverified conditions that influence design must remain explicit until confirmed or invalidated.

**Practice:** Name the assumption, consequence if false, cheapest useful check, and owner of the decision it affects.

**Boundary:** Do not turn routine language or platform facts into excessive paperwork when they are already established by current evidence.

## REQ-004 — Prioritize by consequence and dependency

**Rule:** Order work using user value, risk, dependency, reversibility, and cost of delay rather than enthusiasm or implementation convenience alone.

**Practice:** Identify the smallest prerequisite chain that unlocks a coherent outcome and separate it from optional breadth.

**Boundary:** Security, integrity, destructive-operation, and irreversible-migration boundaries may outrank near-term feature value.

## REQ-005 — Trace needs to implementation and evidence

**Rule:** Material requirements should be traceable to their owning contract or implementation surface and to verification evidence.

**Practice:** Use stable requirement or decision references where omission or drift would be costly.

**Boundary:** Traceability should remain proportional; lightweight changes do not require a heavyweight requirements database.

## REQ-006 — Treat nonfunctional requirements as behavior

**Rule:** Performance, reliability, accessibility, privacy, operability, and compatibility expectations require explicit scope and evidence.

**Practice:** Define representative workloads, supported environments, failure consequences, or measurable budgets before claiming compliance.

**Boundary:** Avoid arbitrary targets without a user, operational, contractual, or risk basis.

## REQ-007 — Record consequential decisions and reversal conditions

**Rule:** Preserve the context, alternatives, tradeoffs, decision, owner, and conditions that would justify revisiting an expensive or durable choice.

**Practice:** Keep the record concise and link it to the affected contract or system boundary.

**Boundary:** Do not document private reasoning or every routine implementation choice as an architectural decision.

## REQ-008 — Resolve contradictions before implementation

**Rule:** Conflicting requirements, authorities, or source-of-truth claims must be surfaced and reconciled rather than silently averaged.

**Practice:** Present the conflicting evidence, affected behavior, and smallest decision needed from the responsible authority.

**Boundary:** A safe, reversible portion that is independent of the contradiction may continue when its ownership and acceptance remain clear.
