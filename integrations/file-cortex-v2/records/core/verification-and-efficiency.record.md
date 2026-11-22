# Verification and Efficiency

## CORE-VERIFY-001 — Match verification to risk and stage

**Rule:** Verification effort should reflect the consequence of failure, the maturity of the change, and the boundary being claimed.

**Practice:** Use focused checks during development, broaden for shared contracts and integration risk, and reserve exhaustive operational matrices for release readiness.

**Boundary:** Low effort is not justified when changes affect authorization, data integrity, destructive operations, secrets, public compatibility, or recovery.

## CORE-VERIFY-002 — Test changed behavior first

**Rule:** Begin with observable tests covering the modified path, its important failure modes, and known regressions.

**Practice:** Prefer behavior assertions over tests tightly coupled to implementation structure.

**Boundary:** Focused tests do not replace broader validation when a shared dependency or contract can affect distant consumers.

## CORE-VERIFY-003 — Batch compatible verification

**Rule:** When noninterfering changes share the same build or test surface, verify them together in one attributable run.

**Practice:** Plan coherent batches, avoid rebuilding after every trivial edit, and isolate the cause if combined verification fails.

**Boundary:** Do not combine changes when failures would become ambiguous, state would collide, or one change can invalidate another's evidence.

## CORE-VERIFY-004 — Exercise the real boundary eventually

**Rule:** Unit and fixture tests cannot prove process, protocol, service, package, fallback, device, or user-workflow behavior by themselves.

**Practice:** Exercise the most realistic authorized isolated boundary before claiming that integration.

**Boundary:** Real-boundary testing does not imply permission to contact production, mutate live data, or use private accounts.

## CORE-VERIFY-005 — Verify artifact freshness

**Rule:** Confirm that the tested binary, package, generated client, or deployed component was produced from the intended current source and configuration.

**Practice:** Record build configuration, target, architecture, version, digest, or another reliable identity when needed.

**Boundary:** A passing stale artifact is evidence about that artifact, not the current source.

## CORE-VERIFY-006 — Keep failures attributable

**Rule:** A verification result should make clear which behavior failed and at which layer.

**Practice:** Use deterministic setup, isolated state, meaningful test names, bounded parallelism, and separate diagnostics for product and harness failures.

**Boundary:** Faster parallel execution is not an improvement if shared state or nondeterminism makes results untrustworthy.

## CORE-VERIFY-007 — Do not count absence as a pass

**Rule:** Skipped, unavailable, mocked, stubbed, not-run, or unsupported checks must be reported separately from passes.

**Practice:** State coverage limitations and the reason each important boundary was not exercised.

**Boundary:** Expected-negative tests may pass when the intended denial or failure behavior is actually observed.

## CORE-VERIFY-008 — Run one appropriate checkpoint pass

**Rule:** After a coherent change set is locally green, run the agreed broader checkpoint once rather than repeating the entire matrix after every edit.

**Practice:** Include affected shared contracts, formatting or static checks, relevant regressions, and a real execution path according to project policy.

**Boundary:** A failure in the checkpoint reopens the affected work; it must not be hidden by rerunning only passing subsets.

## CORE-VERIFY-009 — Separate developer and release evidence

**Rule:** Developer verification proves a bounded change; release verification additionally covers packaging, installation, upgrade, rollback, recovery, security, and deployment policy.

**Practice:** Use explicit readiness labels and do not promote a checkpoint based solely on a successful build or unit suite.

**Boundary:** The exact release gate belongs to the product or organization, not this general library.

## CORE-VERIFY-010 — Minimize excess and raise recurring waste early

**Rule:** Treat repeated work, growing artifacts, avoidable complexity, and unnecessary reporting as engineering costs. Surface recurring patterns with concrete evidence and a proportionate correction before they become expensive.

**Practice:** Reuse generated output paths; retain extra build copies only for an explicit purpose. Run one coherent set of relevant checks and repeat only after a material change or failure. Read the owning contract before guessing names or rebuilding an existing tool. Cache valid task context and keep progress reports centered on decisions, results, and remaining work.

**Boundary:** Efficiency does not mean bypassing authorization, package signatures, database preservation, or meaningful validation. Correct acknowledged mistakes without treating perfection as a prerequisite for candid discussion.
