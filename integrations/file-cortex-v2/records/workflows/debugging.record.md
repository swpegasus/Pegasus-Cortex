# Debugging Workflow

This workflow applies `CORE-FAIL-001` through `CORE-FAIL-005`, `CORE-VERIFY-005`, and `OBS-*` to fault investigation.

## DEBUG-001 — Reproduce against the authoritative artifact

**Rule:** Establish the narrowest trustworthy failure using the source, build, configuration, data, and launch path actually under investigation.

**Practice:** Record exact artifact identity, inputs, environment, expected result, and observed result.

**Boundary:** If reproduction is unsafe or unavailable, label the investigation as evidence-based rather than reproduced.

## DEBUG-002 — Locate the failing boundary before editing

**Rule:** Determine whether the defect belongs to source, build artifact, invocation, configuration, data, dependency, environment, or external integration.

**Practice:** Inspect the transition where expected state first diverges from observed state.

**Boundary:** A symptom may span layers, but the investigation should still identify the earliest demonstrated break.

## DEBUG-003 — Test one causal hypothesis at a time

**Rule:** Each diagnostic change should distinguish a specific plausible cause from alternatives.

**Practice:** Predict the observation, make the smallest reversible probe, and retain contradictory evidence.

**Boundary:** Parallel independent probes are efficient when their outputs remain attributable.

## DEBUG-004 — Minimize without erasing the cause

**Rule:** Reduce inputs, components, timing, or environment while preserving the failure and its important boundary.

**Practice:** Confirm the minimized case still represents the original defect before basing a fix on it.

**Boundary:** Synthetic minimization may miss scale, concurrency, permission, or deployment causes; retain the original reproduction.

## DEBUG-005 — Preserve bounded diagnostic evidence

**Rule:** Capture relevant logs, traces, dumps, exit codes, revisions, and timing before cleanup or repeated attempts overwrite them.

**Practice:** Redact protected data and keep enough context to reproduce the causal sequence.

**Boundary:** Do not collect broad sensitive state merely because it might be useful.

## DEBUG-006 — Compare with a known-good boundary

**Rule:** When possible, isolate the first differing change, version, input, or environment between working and failing behavior.

**Practice:** Use history bisection, controlled configuration comparison, or component substitution without mutating the only evidence copy.

**Boundary:** Correlation with a change is not proof; verify the mechanism.

## DEBUG-007 — Fix the root and preserve a regression check

**Rule:** Correct the owning invariant or boundary and add the smallest durable verification that would have caught the defect.

**Practice:** Confirm both the original failure and relevant neighboring behavior after the fix.

**Boundary:** A temporary workaround requires explicit approval, risk, removal condition, and separate root-cause follow-up.

## DEBUG-008 — Remove diagnostic side effects

**Rule:** Temporary logging, toggles, bypasses, test data, relaxed limits, and instrumentation must be removed or intentionally productized after investigation.

**Practice:** Review the final diff and runtime configuration for residual diagnostic behavior.

**Boundary:** Retained diagnostics must follow normal privacy, performance, configuration, and maintenance ownership.

## DEBUG-009 — Build and sharpen the feedback loop first

**Rule:** Before broad code changes on a hard, intermittent, or performance defect, establish the fastest trustworthy pass/fail loop that exercises the reported symptom at the relevant boundary.

**Practice:** Prefer a focused test, fixture-driven CLI or API invocation, captured-trace replay, headless interaction, differential comparison, bisection harness, or bounded stress loop; then improve its speed, specificity, and determinism by controlling setup, time, randomness, filesystem, network, and unrelated initialization.

**Boundary:** Urgent containment or an unsafe production-only incident may require evidence-based diagnosis before a local loop exists. State that limitation, preserve the original evidence, and do not let a synthetic loop replace final verification against the authoritative scenario.
