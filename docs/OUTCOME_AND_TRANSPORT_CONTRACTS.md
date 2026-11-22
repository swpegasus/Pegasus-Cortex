# Outcome and secure transport contracts

This slice adopts the language-neutral `agent-outcome.v1`, confidential-envelope,
and certificate-bootstrap schemas embedded in `Pegasus.Shared 1.20260711.153-a`.
`Pegasus.Cortex.Core` is an independent policy and repository boundary; it is not
a runnable service and does not make Cortex a dependency of another Pegasus product.

## Outcome ingestion

The wire outcome is untrusted. Tenant, effective namespace, caller service, policy
revision, and approval/promotion authority come from `OutcomeCallerContext`, which a
future authenticated API or MCP host must construct from validated Nexus-derived or
dedicated external-service policy. A namespace string is never authority by itself.

Accepted rows are immutable. `IOutcomeRepository.AddImmutableAsync` must atomically
reject duplicate IDs and changed supersession preconditions. Supersession stays within
one tenant and namespace and targets an active record. Rejected payloads are not sent
to the repository: callers receive only sanitized diagnostics and a SHA-256 payload
hash suitable for a separately governed diagnostic/audit record.

Stored content is always marked `untrusted_data` and `executable=false`. The policy
rejects credential-shaped fields, private keys and bearer material, raw terminal/chat
dumps, private reasoning fields, and executable-instruction fields. Training export is
only eligible after approval and only for `public` or `internal` sensitivity; export
generation remains a separate approval-gated operation.

## Retention classes

| Class | Server maximum/default | Intended use |
|---|---:|---|
| `ephemeral` | 30 days | Short-lived working context |
| `operational` | 180 days | Final job and operational outcomes |
| `knowledge` | No automatic expiry | Reviewed durable learning |
| `decision` | No automatic expiry | Reviewed architectural/project decisions |
| `rejected-diagnostic` | 90 days | Sanitized rejection metadata, never rejected content |
| `audit` | 730 days | Governed access/write audit evidence |
| `export-artifact` | 30 days | Regenerable, approval-scoped training export artifacts |

A shorter requested future expiry is preserved. Legal hold removes automatic expiry
and is accepted only with server-authorized approval. Archive, policy, and legal-hold
operations still require their own audit and authorization in the future host.

## Communication boundary

Normal Pegasus service traffic requires HTTPS with TLS 1.3 or newer, mutual service
authentication using certificate/trust references, and rotatable keys. Confidential
payloads crossing an intermediary additionally require a recipient-encrypted
`pegasus.confidential-envelope/1.0` envelope. No JWE provider is selected in this
slice, so `FailClosedConfidentialMessageProtector` rejects protected traffic until an
approved provider is injected.

The only plaintext exception is an explicitly opened certificate-bootstrap window on
operating-system loopback. It rejects remote or forwarded requests, exposes only
`status`, `csr`, and `complete`, carries no confidential data, and closes once a usable
certificate exists. Cortex is not a certificate authority; the placeholder issuer
fails closed until Pegasus Server supplies an approved issuer adapter.
