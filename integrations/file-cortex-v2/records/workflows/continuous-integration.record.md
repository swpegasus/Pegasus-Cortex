# Continuous Integration

These tenets apply `BUILD-*`, `CORE-VERIFY-*`, `DEP-*`, `SEC-*`, and `RELEASE-*` to automated pipelines.

## CI-001 — Treat pipelines as privileged executable code

**Rule:** Workflow definitions, runners, actions, scripts, and credentials require code review and least privilege.

**Practice:** Pin executable third-party components to immutable revisions or digests where supported, treat event type and checked-out ref as trust inputs, and withhold trusted secrets from untrusted contributions.

**Boundary:** Reviewing a mutable tag does not keep its future content reviewed; public read-only validation can run with minimal anonymous authority.

## CI-002 — Reuse canonical repository commands

**Rule:** Automation should invoke the supported build, test, lint, package, and verification entry points rather than maintain divergent logic.

**Practice:** Keep environment setup explicit and make local reproduction possible where practical.

**Boundary:** CI orchestration may add parallelism and artifact handling around the canonical commands.

## CI-003 — Keep jobs deterministic and isolated

**Rule:** Jobs should declare inputs, toolchains, permissions, network needs, working directories, and produced artifacts.

**Practice:** Avoid reliance on mutable runner state or undeclared global tools.

**Boundary:** Purpose-built persistent runners remain valid when their state is explicitly managed and verified.

## CI-004 — Treat caches as optimization, not evidence

**Rule:** Cache keys must cover relevant inputs, clean execution must remain possible, and untrusted jobs must not poison privileged caches.

**Practice:** Distinguish cache hit from verified artifact freshness.

**Boundary:** Content-addressed immutable artifacts may be reused as evidence when identity and provenance are verified.

## CI-005 — Transfer verified artifact identity between jobs

**Rule:** Every downstream job must retrieve the intended artifact by immutable identity and verify its provenance before use.

**Practice:** Apply `RELEASE-001` while carrying digest, target, producer job, provenance, test evidence, and approval through pipeline storage and trust boundaries.

**Boundary:** Independent reproducibility builds are additional comparisons, not substitutes for identifying the promoted artifact.

## CI-006 — Choose matrices by supported risk

**Rule:** Test combinations should reflect supported platforms, runtimes, contracts, and high-risk interactions rather than every conceivable permutation.

**Practice:** Use dependency impact and release commitments to select required and optional jobs.

**Boundary:** Rare safety-critical combinations may justify dedicated coverage despite low usage.

## CI-007 — Cancel only safely superseded work

**Rule:** Replacing obsolete validation is different from interrupting deployment, migration, publication, signing, or other stateful jobs.

**Practice:** Classify job effects and define cancellation, cleanup, and reconciliation per class.

**Boundary:** Emergency containment may cancel stateful work while preserving partial-effect evidence.

## CI-008 — Keep required outcomes honest

**Rule:** Shell chaining, optional jobs, expected-negative checks, and continue-on-error settings must not turn a failed required gate green.

**Practice:** Publish explicit status for required, advisory, skipped, unavailable, and quarantined checks.

**Boundary:** An approved temporary exception needs owner, scope, risk, expiry, and visible release impact.

## CI-009 — Separate untrusted validation from trusted promotion

**Rule:** Artifacts, metadata, logs, caches, and status produced by untrusted contribution jobs must not directly enter signing, deployment, publication, or privileged environments.

**Practice:** Reconstruct from trusted source or verify immutable identity, provenance, policy, and content in a trusted job before promotion.

**Boundary:** Passing untrusted tests is evidence about the tested revision, not authority to expose credentials or perform privileged effects.
