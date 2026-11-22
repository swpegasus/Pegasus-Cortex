# Brain-Coder Durable Live-Thread Grant Handoff

Last updated: 2026-07-13

## Superseding Decision

Normal product-to-product service calls do not require per-call user approval.
They use authenticated service identities, explicit scopes, server-side policy,
bounded contracts, and audit.

Pegasus Cortex is self-contained. Ingestion processing, storage, indexing,
review, retention, export planning, and retrieval of records already inside the
Cortex security boundary require normal Cortex tenant, namespace, sensitivity,
provenance, policy, and audit authorization, but no per-access thread popup.
Reading an approved Cortex record is not access to its originating live thread.

The only permitted live or external cross-product AI-agent thread interchange
is Brain <-> Coder. That path is implemented outside Cortex and requires an
explicit user grant owned by Nexus. There is one grant mode: durable and
permanent until the user explicitly revokes it. Every other live cross-product
thread route is prohibited rather than made approval-capable. Cortex must not
become a live-thread proxy or a general thread-approval service.

## Boundary Matrix

| Operation | User popup | Authority |
| --- | --- | --- |
| Normal product service API/MCP call | No per-call popup | Service identity, scopes, product/user policy, audit |
| Cortex-local ingest/review/index/retention | No per-access popup | Cortex tenant/namespace policy, provenance, sensitivity, audit |
| Retrieval of an approved Cortex record | No per-access popup | Cortex read authorization and record policy |
| Following Cortex provenance to a live source thread | Prohibited through Cortex | Cortex exposes no live-thread transport |
| Brain <-> Coder live-thread interchange | Required at grant creation or scope expansion | Nexus-owned durable user grant plus service identity/scopes; valid until explicitly revoked |
| Any other cross-product live-thread interchange | Prohibited | No grant type is available |

No product inherits a live-thread exception by calling Cortex. A caller receives
only the authorized immutable Cortex record. Source references are provenance,
not dereference instructions, control handles, or authority.

## Brain-Coder Dialog Requirements

Before Brain or Coder performs a live-thread interchange, the user dialog must
show:

1. requesting product, service, and agent/principal;
2. receiving product and exact target thread or fully resolved bounded target
   set;
3. requested action scopes;
4. human-readable purpose;
5. data scope, including metadata, content, outputs, attachments, or context;
   and
6. a clear statement that the grant remains active until the user explicitly
   revokes it in Nexus.

Any expansion of requester, direction, targets, actions, purpose, or data scope
requires a new decision. Wildcards, unresolved selectors, missing UI, headless
operation, denial, expiry, revocation, or ambiguous scope fail closed with a
structured `approval_required` diagnostic.

Prompts, models, thread content, Cortex records, service tokens, and agents
cannot approve their own transfer.

## Proposed Shared Contracts

Pegasus Shared should own language-neutral schemas and passive net8.0 contract
types specifically for the Brain-Coder route. These serialized contracts are
evidence consumed by policy; they do not grant themselves authority.

### `pegasus.brain-coder-thread-access-request/1.0`

- request ID, version, correlation ID, policy revision, creation, and expiry;
- tenant, user, requesting product/service/agent/instance/machine identifiers;
- direction: `brain-to-coder` or `coder-to-brain`;
- exact source and target thread authority/IDs, or a fully resolved bounded set
  with a deterministic set hash;
- requested action scopes and bounded data scope;
- purpose code and bounded user-visible purpose;
- requested durable-grant revision and acknowledgment that it remains active
  until explicitly revoked;
- canonical request hash.

### `pegasus.brain-coder-thread-access-decision/1.0`

- decision ID and exact request ID/hash;
- state: `approved`, `denied`, or `revoked`;
- approved direction, target set, action scopes, data scope, and purpose;
- approving user and authority references;
- policy revision, decision time, absolute expiry, and correlation ID;
- reason code without copied thread content.

### `pegasus.brain-coder-thread-access-grant/1.0`

- grant and decision IDs;
- user, requesting principal, direction, exact target set, actions, data scope,
  and purpose binding;
- issue time, current policy revision, active/revoked state, revocation
  revision, and optional explicit revocation time;
- integrity metadata and protected-storage reference, never raw key/token data.

### `pegasus.brain-coder-thread-access-audit/1.0`

- event, request, decision, and grant references;
- requester, direction, and target-set references;
- attempted action, outcome, time, policy revision, and correlation ID;
- structured diagnostic code and retryability;
- no thread title, prompt, content, output, attachment, or copied context.

The existing `CoordinationApprovalSnapshot` is insufficient because its single
scope string does not bind a user, requesting product/agent, direction, exact
thread set, data scope, decision, durable grant revision, protection, or
revocation state.

## Grant Semantics

- **Durable permanent-until-revoked:** one narrowly keyed grant survives
  application and user-session restarts. Nexus binds it to the authority-
  qualified user, requester, Brain-Coder direction, exact target set, actions,
  data scope, purpose, and policy revision. Nexus stores it encrypted and
  integrity-protected, shows it in settings, audits each use, and supports
  immediate explicit user revocation.
- Scope expansion, a different requester, direction, target set, action, data
  scope, purpose, or authority requires a new user decision. A retry that stays
  inside the exact active grant does not create another grant.
- Logout or ordinary session rotation does not revoke the durable grant.
  Explicit user revocation, user disablement, loss of underlying thread/service
  authorization, invalid integrity state, or a superseding policy denial does.

The approval is conjunctive with ordinary Brain and Coder service identity,
scope, ownership, and thread authorization. A valid user grant never rescues an
invalid, revoked, expired, wrong-audience, or unauthorized service call.

## Diagnostics

Use shared `PegasusDiagnostic` with code `approval_required`, operation,
severity, retryability, correlation ID, requester, direction, target-set
reference, requested action scopes, data scope, and approval-request ID. Do not
copy thread content into diagnostics.

Retryability is true only when an authenticated interactive confirmation flow
is available. Headless or missing-UI denial remains `approval_required` but is
non-retryable until an authorized interactive channel exists. A non-Brain-Coder
route fails with `cross_product_thread_interchange_forbidden`, not with an
approval request.

## Nexus Handoff

Nexus should own the Brain-Coder confirmation UI and grant registry:

- authority-qualified user/requester/target binding;
- one encrypted, integrity-protected durable grant mode with explicit
  revocation and revisioned scope;
- visible settings, audit, and revocation;
- denial for any product pair other than Brain-Coder;
- denial when UI is missing, scope is ambiguous, or policy state is stale; and
- conformance fixtures for denial, scope expansion, revocation, session
  rotation, concurrent use, and prohibited third-product routes.

Normal product connectors remain governed by service identity/scopes/audit and
do not call this dialog for each request.

## Cortex Handoff

Cortex requires no runtime thread-approval subsystem. It must:

- continue applying normal Cortex authorization to records inside its boundary;
- preserve source provenance without automatically dereferencing live threads;
- expose no live-thread discovery, import-from-live-thread, refresh, steering,
  cancellation, mutation, or transfer proxy;
- reject record content, MCP arguments, prompts, or evidence that attempt to
  induce live-thread control; and
- ensure other products cannot use Cortex as an intermediary for Brain-Coder or
  any other live thread transfer.

The current isolated MCP host already has no network or live-service access and
only four read-only Default Library tools. No Cortex runtime source change is
required for this policy checkpoint. Any future proposal to add live-thread
behavior conflicts with this decision and requires a new explicit user
architecture decision.

## Current Nexus Session Instruction

The user's current queue-and-return instruction is session authorization only
for Nexus to collect results from the bounded set of agents already queued when
the instruction was issued. It is an orchestration authorization for the
current user session, not a reusable Brain-Coder product grant and not a
precedent for per-call service approval.

It does not authorize discovery of additional threads or agents, new messages,
steering, cancellation, thread-state changes, later queued work, permanent grant
storage, or reuse by Cortex or another product. Audit preserves approval and
access metadata without copying thread content.

## Proportional Future Verification

When Shared/Nexus/Brain/Coder implementation is explicitly authorized, verify:

- normal authenticated service calls do not produce per-call approval dialogs;
- the Brain-Coder dialog contains every required field;
- durable protection, explicit revocation, session-survival, and exact scope
  binding;
- missing-UI, headless, stale-policy, denial, and ambiguity failure paths;
- third-product live-thread routes are prohibited rather than approval-capable;
- audit excludes thread content; and
- Cortex record retrieval works through normal namespace policy while Cortex
  exposes no live-thread transport or proxy.
