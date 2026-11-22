# Security Engineering

These tenets specialize `CORE-AUTH-002` through `CORE-AUTH-008` without replacing those universal authority boundaries.

## SEC-001 — Threat-model changed trust boundaries

**Rule:** Identify assets, actors, entry points, trust transitions, abuse cases, and consequences before choosing controls for material exposure.

**Practice:** Revisit the model when data flow, privilege, deployment, dependency, or external access changes.

**Boundary:** Keep analysis proportional; routine internal refactoring may only require confirming the existing model still applies.

## SEC-002 — Verify grants carried as data

**Rule:** Possessing a token, capability document, signed request, or persisted grant does not prove that it is currently valid for the requested operation.

**Practice:** Apply `CORE-AUTH-001` and verify signature or proof, issuer, audience, subject, scope, resource, revision, expiry, replay protection, and revocation as the grant contract requires.

**Boundary:** A self-contained grant can reduce lookup needs but cannot expand beyond its issuer's authority or ignore current revocation policy.

## SEC-003 — Separate authentication from authorization

**Rule:** Establishing identity does not prove permission for a capability, resource, tenant, or mutation.

**Practice:** Re-evaluate current ownership, scope, policy, and approval at every protected operation.

**Boundary:** Public anonymous capabilities still require abuse and resource controls.

## SEC-004 — Intersect every required grant

**Rule:** Effective authority is the intersection of all controls required by current policy for the exact operation, identity, scope, and resource.

**Practice:** Evaluate the applicable ownership, capability, approval, tenancy, and resource grants independently; no one control substitutes for another required control.

**Boundary:** Administrative recovery paths must be explicit, narrowly scoped, time-bounded, and audited.

## SEC-005 — Never substitute weaker protection silently

**Rule:** When a required protection is unavailable, do not silently substitute a weaker algorithm, transport, identity check, storage mode, or authorization path.

**Practice:** Reject or expose an explicit degraded capability according to policy, and make development bypasses conspicuous, local, nonpersistent, and impossible to activate accidentally in release environments.

**Boundary:** Safe liveness, diagnostics, and repair surfaces may remain available.

## SEC-006 — Construct data for its destination context

**Rule:** Commands, queries, paths, markup, templates, headers, and protocols each require context-appropriate parsing, parameterization, or encoding.

**Practice:** Preserve argument and field boundaries and validate dynamic identifiers through strict owned rules.

**Boundary:** Escaping for one context is not protection in another.

## SEC-007 — Keep the privileged core narrow

**Rule:** Elevated effects belong behind small typed interfaces while planning, UI, parsing, and AI processes remain unprivileged.

**Practice:** Validate exact operations and inputs before crossing the privilege boundary and return bounded outcomes.

**Boundary:** Do not expose unrestricted shells, administrator tokens, or generic package execution as a convenience interface.

## SEC-008 — Use vetted cryptography with an owned key lifecycle

**Rule:** Algorithm selection, canonicalization, key generation, storage, access, rotation, revocation, recovery, and destruction form one design.

**Practice:** Use maintained libraries and established protocols rather than custom cryptographic constructions.

**Boundary:** Hashing, signing, encryption, and password derivation solve different problems and are not interchangeable.

## SEC-009 — Audit decisions without recording protected content

**Rule:** Security audit should identify actor, operation, target, policy or approval, time, revision, and outcome while minimizing payloads.

**Practice:** Protect audit integrity, access, retention, and correlation separately from ordinary diagnostics.

**Boundary:** Audit evidence cannot substitute for enforcing the control itself.

## SEC-010 — Verify controls through rejection paths

**Rule:** Security verification must include unauthorized, stale, malformed, replayed, revoked, and boundary-crossing attempts appropriate to the threat model.

**Practice:** Confirm the operation is rejected without leaking protected data or weakening unrelated availability.

**Boundary:** Do not conduct disruptive or live adversarial testing without explicit authority and containment.
