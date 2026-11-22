# Testing

Focused-first selection, compatible combined runs, failure attribution, real-boundary evidence, and coverage reporting are defined canonically in `CORE-VERIFY-001` through `CORE-VERIFY-009` and `CORE-FAIL-004` through `CORE-FAIL-005`. Contract, performance, privacy, and concurrency semantics remain owned by `API-*`, `PERF-*`, `PRIV-*`, and `CONC-*`. This file contains testing-specific applications rather than copies of those principles.

## TEST-001 — Test observable behavior

**Rule:** Assert public outcomes, state transitions, contracts, and important failures instead of incidental implementation structure.

**Practice:** Keep tests resilient to safe refactoring while precise about required behavior.

**Boundary:** Internal algorithm tests are appropriate when the algorithm itself is a material contract or risk boundary.

## TEST-002 — Keep tests deterministic and isolated

**Rule:** Tests should control inputs, time, randomness, environment, dependencies, and cleanup to the degree required by their boundary.

**Practice:** Use unique temporary state, explicit seeds or clocks, bounded waits, and awaited cleanup.

**Boundary:** Real integration tests may include external variability; record it and avoid presenting intermittent success as deterministic proof.

## TEST-003 — Exercise negative and boundary behavior

**Rule:** Important invalid inputs, denied authorization, limits, cancellation, timeouts, partial failures, and recovery paths need observable tests.

**Practice:** Prioritize cases that protect public contracts or material safety boundaries.

**Boundary:** Do not enumerate every theoretical input combination when equivalence classes and property-based checks can cover them efficiently.

## TEST-004 — Treat flaky tests as defects

**Rule:** Nondeterministic tests reduce confidence and should not be normalized through blind retries.

**Practice:** Preserve occurrence evidence, identify timing or state dependencies, and quarantine only through an explicit visible policy.

**Boundary:** A bounded retry may diagnose transient infrastructure but must not convert repeated uncertainty into a clean pass.

## TEST-005 — Preserve diagnostic value

**Rule:** A failed test should identify the scenario, expected behavior, observed behavior, and relevant bounded evidence.

**Practice:** Avoid giant snapshots, shared catch-all tests, and logs that make the causal assertion difficult to find.

**Boundary:** Sensitive data and untrusted payloads still require redaction and truncation.

## TEST-006 — Add contract tests at ownership boundaries

**Rule:** Components that exchange versioned behavior should verify producer and consumer compatibility against the same public contract.

**Practice:** Exercise canonical fixtures, supported versions, unknown fields, stable errors, limits, and primary rejection behavior.

**Boundary:** Contract tests complement rather than replace each component's internal tests and real integration evidence.

## TEST-007 — Use property-based tests for broad invariants

**Rule:** When many inputs share a durable invariant, generate representative variations instead of hand-listing a few examples.

**Practice:** Define valid generators, useful shrinking, deterministic seeds, and explicit invariant assertions.

**Boundary:** Generated coverage does not replace carefully chosen domain examples or external-boundary tests.

## TEST-008 — Fuzz exposed parsers proportionately

**Rule:** Parsers and decoders exposed to complex or untrusted input should be tested for crashes, hangs, resource exhaustion, and invalid acceptance.

**Practice:** Bound execution, retain minimized reproducers, and convert material findings into regression tests.

**Boundary:** Fuzzing live systems or sensitive data requires explicit authorization and containment.

## TEST-009 — Tie performance tests to budgets

**Rule:** Performance tests need a representative workload, environment, artifact, metric, and threshold derived from `PERF-001` and `PERF-002`.

**Practice:** Separate cold start, warm state, throughput, and tail behavior and control unrelated machine noise where feasible.

**Boundary:** A microbenchmark regression is not automatically a user-visible performance failure.

## TEST-010 — Protect test data through its lifecycle

**Rule:** Fixtures, snapshots, recordings, uploaded samples, and generated evidence must follow privacy, ownership, retention, and cleanup rules.

**Practice:** Prefer synthetic or minimized data and keep secrets and production identities out of ordinary tests.

**Boundary:** Authorized real-world corpora require protected storage, access, and deletion behavior.

## TEST-011 — Select tests by dependency impact

**Rule:** Test selection should consider consumers, contracts, data formats, configuration, and shared dependencies—not only edited filenames.

**Practice:** Begin focused, then broaden where the change crosses ownership or compatibility boundaries.

**Boundary:** Dependency analysis is an optimization and must not silently omit required release gates.

## TEST-012 — Create deterministic seams without bypassing production behavior

**Rule:** A test seam should control a nondeterministic dependency while preserving the same validation, authorization, serialization, lifecycle, and public contract used in production.

**Practice:** Inject focused clocks, randomness, transports, stores, or schedulers at owned boundaries and run separate real-adapter tests for behavior the substitute cannot prove.

**Boundary:** A fake that skips the production engine, weakens policy, or returns impossible states tests the fake rather than the claimed product behavior.

## TEST-013 — Develop uncertain behavior in vertical tracer slices

**Rule:** When implementation learning can change the test shape, prove one observable behavior through its public boundary, implement the minimum needed for that behavior, and repeat instead of writing a speculative test batch followed by a separate implementation batch.

**Practice:** Use each red-to-green slice to refine the next behavior, interface, and evidence; refactor only from a passing state and keep each test resilient to internal restructuring.

**Boundary:** A complete up-front conformance suite is appropriate when an external specification or stable compatibility contract already defines the full behavior independently of the implementation.
