# Performance, Capacity, and Caching

These tenets extend `CORE-AUTH-006`, `CORE-VERIFY-001`, `PLAY-008`, and `ARM64-008`.

## PERF-001 — Set measurable performance budgets

**Rule:** Define representative latency, throughput, startup, memory, CPU, storage, and network expectations before optimizing or certifying performance.

**Practice:** Tie budgets to user workflows, operational capacity, supported hardware, and consequence.

**Boundary:** Early prototypes may begin with coarse budgets, but should not claim production capacity from them.

## PERF-002 — Measure representative workloads

**Rule:** Performance evidence must use realistic data shapes, concurrency, environments, and operation mixes.

**Practice:** Record input scale, configuration, artifact, warm-up, resource limits, and measurement method.

**Boundary:** Microbenchmarks remain useful for isolated hot paths when their limited scope is explicit.

## PERF-003 — Profile before optimizing

**Rule:** Locate the actual bottleneck and its cause before selecting a performance change.

**Practice:** Measure CPU, allocation, I/O, locks, network round trips, query plans, and wait time at the relevant boundary.

**Boundary:** Obvious algorithmic explosions or unbounded allocation can be corrected before full profiling when evidence is already decisive.

## PERF-004 — Bound every accumulating resource

**Rule:** Queues, caches, histories, retries, buffers, batches, result sets, connections, and fan-out all need enforceable limits.

**Practice:** Define capacity, eviction or rejection, visibility, and recovery before exhaustion.

**Boundary:** Structurally fixed collections may rely on the enforced fixed bound.

## PERF-005 — Define overload behavior

**Rule:** Systems should apply backpressure, admission control, shedding, prioritization, or explicit degradation before resource collapse.

**Practice:** Preserve high-value and safety-critical work and report rejected or delayed work honestly.

**Boundary:** Do not shed authorization, integrity, or required persistence merely to keep a superficial health signal green.

## PERF-006 — Measure distributions and lifecycle phases

**Rule:** Averages alone hide cold start, warm-up, steady state, stalls, and tail latency.

**Practice:** Report percentiles or distributions and separate phases whose causes and user effects differ.

**Boundary:** Tiny deterministic operations may need only a simple bounded measurement.

## PERF-007 — Give caches an owner and invalidation contract

**Rule:** Every cache needs defined keys, scope, authoritative fallback, freshness, expiry, capacity, eviction, and failure behavior.

**Practice:** Include all security, tenant, locale, version, and input dimensions that affect correctness in the key or scope.

**Boundary:** Immutable content-addressed data may not require time-based expiry, but still needs storage bounds.

## PERF-008 — Bound authorization caches without widening grants

**Rule:** An authorization cache may reduce lookup cost only within the grant's policy, scope, revision, and expiry; it must not issue, widen, renew, or permanently preserve authority.

**Practice:** Apply `SEC-002`, key cached decisions by every material policy input, and define revocation, outage, clock-skew, stale-use, and renewal behavior explicitly.

**Boundary:** An independently authoritative offline system requires its own delegated policy and audit, not an accidental cache.

## PERF-009 — Optimize algorithms and data movement first

**Rule:** Prefer fewer operations, allocations, copies, queries, and round trips before micro-optimizing syntax.

**Practice:** Examine complexity, batching, streaming, locality, and avoidable repeated work.

**Boundary:** Keep changes understandable and verify that the optimized path preserves semantics.

## PERF-010 — Never hide correctness loss as optimization

**Rule:** Relaxed validation, durability, precision, ordering, privacy, or security must be an explicit contract and decision.

**Practice:** Measure the gain, state the lost guarantee, constrain scope, and provide safe fallback or recovery.

**Boundary:** Approximate behavior is valid when users and dependent systems knowingly accept its error bounds.
