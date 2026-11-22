# Networking, Distribution, and Messaging

These tenets extend `CORE-FAIL-007`, `CORE-AUTH-002`, `CORE-AUTH-006`, `API-010`, and `CONC-003`.

## NET-001 — Treat the network as partially failing

**Rule:** Requests may be delayed, duplicated, reordered, rejected, partitioned, or completed without a received response.

**Practice:** Design explicit timeout, retry, reconciliation, and uncertain-outcome behavior.

**Boundary:** Local in-process calls need not simulate network failure unless their contract intentionally mirrors a remote boundary.

## NET-002 — Define protocol framing and limits

**Rule:** Versions, encodings, message boundaries, sizes, sequencing, compression, and unknown-field behavior are contractual.

**Practice:** Reject malformed or oversized frames before unbounded allocation or downstream processing.

**Boundary:** A trusted internal transport still needs bounds when faults can exhaust shared resources.

## NET-003 — Authenticate the peer and authorize the operation

**Rule:** Encryption proves neither the intended peer identity nor permission for a requested resource.

**Practice:** Validate trust material, audience, identity, current authorization, and resource ownership at the service boundary.

**Boundary:** Public anonymous operations may omit identity while retaining input, abuse, and resource controls.

## NET-004 — Propagate deadlines across calls

**Rule:** Remote calls should transport or derive remaining deadline budgets without resetting the caller's useful lifetime at each hop.

**Practice:** Convert absolute and relative deadlines carefully across clock domains and allocate bounded connect, handshake, request, response, and cleanup phase budgets.

**Boundary:** Durable asynchronous work requires an explicit accepted handoff rather than inheriting a short request deadline.

## NET-005 — Declare delivery and ordering semantics

**Rule:** At-most-once, at-least-once, deduplicated, ordered, and best-effort delivery impose different producer and consumer duties.

**Practice:** State the unit of ordering, duplicate behavior, acknowledgement point, and recovery process.

**Boundary:** Do not claim global exactly-once behavior from local deduplication or one transactional component.

## NET-006 — Reconcile uncertain side effects before repeating

**Rule:** A lost response may conceal a completed external mutation.

**Practice:** Query by idempotency key, revision, operation identity, or durable receipt before retrying a nontrivial effect.

**Boundary:** Pure reads and proven idempotent operations may follow a simpler bounded retry policy.

## NET-007 — Design authority and conflict handling for replicated state

**Rule:** Distributed copies require declared authority, revisions, freshness, conflict detection, merge, and offline behavior.

**Practice:** Distinguish advisory cache, projection, replica, and multi-writer state.

**Boundary:** Eventual consistency is not permission to leave security or ownership decisions ambiguous.

## NET-008 — Do not infer distributed order from wall time

**Rule:** Wall-clock timestamps from different machines do not establish causal or total order and may not agree at an expiry boundary.

**Practice:** Define the authoritative expiry clock and skew policy, and use logical revisions, sequence numbers, or causal metadata for ordering where required.

**Boundary:** `PORT-003` owns elapsed duration; synchronized clocks remain useful for human correlation and bounded-expiry decisions without becoming an ordering oracle.

## NET-009 — Own connection lifecycle and renegotiation

**Rule:** Address changes, reconnects, half-open connections, credential rotation, capability changes, and shutdown are normal lifecycle states.

**Practice:** Revalidate the peer and negotiated capabilities after reconnection rather than assuming the prior session still applies.

**Boundary:** Stateless one-shot protocols may expose a smaller lifecycle while retaining bounded cleanup.

## NET-010 — Keep acknowledgement coupled to durable responsibility

**Rule:** Acknowledge work only when the receiver has completed it or durably accepted responsibility under the declared delivery contract.

**Practice:** Retain unacknowledged terminal results and bound acknowledged history separately.

**Boundary:** Fire-and-forget telemetry may intentionally accept loss, but its contract must say so.
