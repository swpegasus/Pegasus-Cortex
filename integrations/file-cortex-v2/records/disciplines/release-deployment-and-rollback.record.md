# Release, Deployment, and Rollback

These tenets extend `BUILD-002`, `BUILD-005`, `CORE-VERIFY-009`, `CORE-AUTH-008`, and `CORE-WORK-008`.

## RELEASE-001 — Promote one identified artifact per target

**Rule:** Build once per target, identify the output, and promote that artifact without rebuilding source between approval and promotion.

**Practice:** Bind version, digest, target, provenance, and evidence to every artifact; provenance-link and separately verify any controlled signature, package, or platform wrapper derived from it.

**Boundary:** Environment-specific configuration should be supplied separately. A derived wrapper is a new identified artifact, not proof that its bytes equal the unwrapped input.

## RELEASE-002 — Bind release provenance

**Rule:** Source revision, dependencies, toolchain, build inputs, signatures, inventory, configuration schema, and evidence belong to the release record.

**Practice:** Make it possible to identify what is running and reproduce or withdraw it.

**Boundary:** Reproducibility may be bounded by protected inputs, but those inputs still need owned recovery.

## RELEASE-003 — Separate release configuration from application bytes

**Rule:** Deployment should supply validated environment policy and secret references without modifying the verified artifact.

**Practice:** Version configuration schemas and capture the non-sensitive effective revision used.

**Boundary:** Platform signing or packaging may wrap the artifact when the wrapper is itself controlled and identified.

## RELEASE-004 — Prove upgrade and rollback compatibility

**Rule:** Schema, protocol, configuration, storage, and mixed-version behavior are release gates when old and new components can coexist.

**Practice:** Test supported upgrade paths and define the last safe rollback point.

**Boundary:** An approved offline replacement may use a simpler one-version transition with explicit downtime and backup.

## RELEASE-005 — Roll out in bounded stages

**Rule:** Increase exposure progressively when failure consequence or uncertainty justifies it.

**Practice:** Use representative staging, canaries or cohorts, health budgets, pause criteria, and explicit promotion decisions.

**Boundary:** Small isolated deployments may use one stage when rollback is rapid and consequence is low.

## RELEASE-006 — Make deployment reconcilable

**Rule:** Repeated execution should discover current state and converge safely rather than duplicate effects or assume a blank target.

**Practice:** Use operation identity, expected versions, idempotent steps, and explicit handling for partial prior execution.

**Boundary:** One-time irreversible migrations require a governed state machine rather than pretending to be idempotent.

## RELEASE-007 — Verify semantics after deployment

**Rule:** Installer success, process existence, or open ports do not prove the deployed user or system behavior.

**Practice:** Exercise a bounded real workflow, its primary rejection path, identity, persistence, and readiness as appropriate.

**Boundary:** Production probes must avoid destructive or privacy-sensitive effects unless explicitly authorized.

## RELEASE-008 — Roll back with evidence

**Rule:** Preserve the prior artifact, configuration, trust material, and data compatibility needed to restore known behavior.

**Practice:** Verify restored semantics and record any data or capability that cannot move backward.

**Boundary:** Forward repair may be safer than rollback after incompatible durable changes; choose explicitly.

## RELEASE-009 — Support revocation and withdrawal

**Rule:** Vulnerable, compromised, invalid, or superseded artifacts must be identifiable and preventable from future promotion.

**Practice:** Maintain signing trust, revocation, repository status, inventory, and removal procedures.

**Boundary:** Preserve required audit evidence even when executable distribution is withdrawn.

## RELEASE-010 — Keep privileged execution separate from planning

**Rule:** When release automation performs privileged or external mutation, unprivileged decisions and plans should feed a narrow deterministic executor that validates exact approved effects.

**Practice:** Give the executor least privilege, typed operations, bounded diagnostics, and rollback-aware state.

**Boundary:** A low-risk local release need not introduce a planner/executor architecture, but build agents, user interfaces, and AI planners must not receive unrestricted production credentials.

## RELEASE-011 — Distribute public trust, never signing secrets

**Rule:** Packages and installers may carry the exact public certificate chain and signer identity needed for verification, but must never contain private keys, signing secrets, PINs, passwords, or exportable key material.

**Practice:** Select signing keys by exact thumbprint and provider, prefer non-exportable keys, scan package inventories for private-key-shaped content, and keep any operator-owned recovery material outside source and artifact roots.

**Boundary:** Public certificates and their digests are release evidence, not permission to broaden machine trust.

## RELEASE-012 — Sign executable bytes before sealing the manifest

**Rule:** Complete executable signing before generating the canonical content manifest, then sign that exact manifest and verify its detached signature before admitting any listed content.

**Practice:** Use the order publish, sign executable bytes, inventory and hash signed bytes, sign the canonical manifest, verify signer and chain, then verify content hashes. Any later byte change requires a new manifest and signature.

**Boundary:** A content digest alone does not establish publisher identity, and a valid signature over a stale or differently scoped manifest does not admit a package.

## RELEASE-013 — Make trust installation explicit and reversible

**Rule:** Installing publisher or root trust is a separately consented privileged operation with pinned identities, scoped consumers, ownership evidence, and bounded removal.

**Practice:** Validate subject, thumbprint, validity, key usage, EKU, and chain before mutation; preserve matching preexisting trust; make repeated installation idempotent; and remove only trust installed by the product after every dependent component is gone.

**Boundary:** Ordinary install, repair, startup, and package verification must not silently add or broaden machine trust.
