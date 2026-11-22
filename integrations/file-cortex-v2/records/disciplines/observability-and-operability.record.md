# Observability and Operability

These tenets extend `CORE-FAIL-003`, `CORE-FAIL-005`, `CORE-AUTH-004`, `ADD-006`, and the lifecycle semantics in `REL-001` through `REL-003`.

## OBS-001 — Give logs, metrics, traces, health, and audit distinct jobs

**Rule:** Events explain occurrences, metrics aggregate behavior, traces connect causality, health exposes capability, and audit records governed decisions.

**Practice:** Choose the signal that answers the operational question instead of emitting the same payload everywhere.

**Boundary:** A small application may implement fewer signal systems while preserving the semantic distinctions.

## OBS-002 — Carry safe causal identity across boundaries

**Rule:** Operations need stable correlation, actor, revision, and component identifiers sufficient to connect bounded evidence.

**Practice:** Propagate correlation through asynchronous and remote work without forwarding raw private payloads.

**Boundary:** Correlation identifiers must not themselves expose secrets or become authorization tokens.

## OBS-003 — Record state transitions and public outcomes

**Rule:** Operational evidence should show accepted, started, progressed, degraded, cancelled, failed, recovered, and completed transitions where they matter.

**Practice:** Persist bounded status, cause category, timing, and result rather than private reasoning or entire inputs.

**Boundary:** High-frequency internal transitions may be sampled or aggregated when individual records have no diagnostic or audit value.

## OBS-004 — Keep capability signals truthful

**Rule:** Health and readiness instrumentation must reflect the distinct states defined by `REL-002` and the dependencies required for the advertised capability.

**Practice:** Expose degraded and unavailable guarantees rather than returning a generic green response because the process is running.

**Boundary:** Optional dependencies should affect only the capabilities that actually require them.

## OBS-005 — Make alerts actionable

**Rule:** An alert should identify violated expectation, likely user or system impact, responsible owner, and first useful diagnostic.

**Practice:** Alert on sustained symptoms or exhausted budgets and link to bounded investigation or recovery steps.

**Boundary:** Avoid paging on raw events that require no action or are already automatically contained.

## OBS-006 — Bound cardinality, payload, and retention

**Rule:** Labels, event bodies, traces, diagnostic tails, and retention must not become a privacy leak, outage source, or uncontrolled cost.

**Practice:** Use stable low-cardinality dimensions, truncate safely, sample intentionally, and define storage limits.

**Boundary:** High-cardinality forensic evidence may live in a separately protected, explicitly bounded store.

## OBS-007 — Protect diagnostic content

**Rule:** Telemetry must minimize secrets, personal data, source content, commands, tokens, and other protected values.

**Practice:** Prefer identifiers, categories, digests, lengths, safe summaries, and redaction at the emission boundary.

**Boundary:** Authorized forensic capture requires separate access, retention, and disclosure controls.

## OBS-008 — Keep instrumentation behavior-neutral

**Rule:** Telemetry failure or slowness must not silently change protected application semantics.

**Practice:** Bound exporters and buffers, isolate failure, and declare when audit durability is a prerequisite for accepting an operation.

**Boundary:** Security or compliance operations may correctly fail closed when required audit evidence cannot be recorded.

## OBS-009 — Identify evidence freshness and scope

**Rule:** Operational views should reveal when, where, and against which revision or workload their evidence was observed.

**Practice:** Distinguish current probe, cached result, aggregate history, simulated fixture, and unverified configuration.

**Boundary:** Human summaries may omit low-level metadata when the underlying evidence remains reachable and unambiguous.
