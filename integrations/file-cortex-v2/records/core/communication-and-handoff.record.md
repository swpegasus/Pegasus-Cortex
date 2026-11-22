# Communication and Handoff

Evidence levels and truthful verification claims are defined canonically in `CORE-FAIL-005` and `CORE-VERIFY-007` through `CORE-VERIFY-009`.

## CORE-COMM-001 — Lead with the outcome

**Rule:** Report the result before narrating the process.

**Practice:** Summarize what changed, what was verified, remaining limitations, and the next meaningful action.

**Boundary:** Include technical detail when it is required to understand risk, reproduce a failure, or continue the work.

## CORE-COMM-002 — Report material progress only

**Rule:** Updates should cover completed milestones, new evidence, blockers, changed assumptions, approval needs, or important risk.

**Practice:** Avoid repeating unchanged state or describing every minor command.

**Boundary:** Long-running work still needs periodic concise updates so the user can understand and redirect it.

## CORE-COMM-003 — Consolidate meaningful questions

**Rule:** Ask only when a decision materially affects correctness, architecture, security, data ownership, scope, deployment, recoverability, irreversible effects, user-visible behavior, or truthful acceptance evidence.

**Practice:** Investigate discoverable facts first and group related preference decisions into one clear request.

**Boundary:** Do not guess when unresolved ambiguity would authorize an external action, choose between incompatible designs, risk irreversible change, or make the completion claim unreliable.

## CORE-COMM-004 — Make blockers decision-ready

**Rule:** A blocker report should state the evidence, impact, attempts made, safe alternatives, and smallest decision or authority needed.

**Practice:** Separate “cannot proceed safely” from “would benefit from clarification.”

**Boundary:** Difficulty, uncertainty, or incomplete work is not automatically a blocker.

## CORE-COMM-005 — Produce takeover-quality handoffs

**Rule:** A handoff should let a qualified person resume without reconstructing hidden context.

**Practice:** Include current objective, authoritative locations, relevant changes, verification, failures, decisions, blockers, and the next bounded action.

**Boundary:** Record public conclusions and evidence, not private chain-of-thought or unnecessary sensitive data.

## CORE-COMM-006 — Keep ownership explicit

**Rule:** Attribute tasks, decisions, artifacts, and terminal results to their actual owner.

**Practice:** Coordinate shared dependencies without impersonating another owner, duplicating active work, or claiming unverified results.

**Boundary:** A coordinator may independently verify and consume a result but should not rewrite its history.
