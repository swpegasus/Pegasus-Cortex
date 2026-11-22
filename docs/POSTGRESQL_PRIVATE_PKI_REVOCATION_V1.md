# PostgreSQL private-PKI revocation evidence v1

This contract closes the native PostgreSQL fixture's private-CA revocation-evidence gap
without weakening `VerifyFull` or `CheckCertificateRevocation=true`. It is a contained test
contract. Production certificate issuance, CRL or OCSP publication, availability, rotation,
and revocation remain responsibilities of the authorized platform operator outside the
Pegasus Cortex runtime.

The v1 profile is `pegasus.cortex.postgresql.private-ca-crl/1`. It uses one signed, fresh CRL
at a literal loopback HTTP distribution point. OCSP is deferred. The test-only responder is
created by the PostgreSQL fixture, serves exactly the manifest-bound public CRL, and is never
composed into the product service or installed as a persistent host process.

## Evidence inputs

The operator supplies two additional environment values:

- `PEGASUS_CORTEX_PG_REVOCATION_EVIDENCE_ROOT=<canonical owner-only directory>`
- `PEGASUS_CORTEX_PG_REVOCATION_MANIFEST_FILE=<safe single-segment .json leaf>`

The evidence root contains only four public inputs: root CA certificate, PostgreSQL server
certificate, signed CRL, and the v1 JSON manifest. Private keys, credentials, connection
strings, bearer material, and responder executables are prohibited. The root must already
exist, be canonical, remain at or below the 240 UTF-16 path budget, contain no reparse or
symbolic-link ancestor, and use mode `0700` on Unix. Each public evidence file uses mode `0600`
on Unix and is read with a fixed byte ceiling.

The manifest is governed by
`schemas/postgresql-private-pki-revocation.v1.schema.json`. Its lowercase SHA-256 values bind
all three public PKI files. File references are safe single-segment leaves. The distribution
point must be literal loopback HTTP, must end with the CRL leaf, and must use a port distinct
from both PostgreSQL endpoints.

## Validation and failure behavior

Before the first database connection, the fixture verifies:

- the root is a public CA certificate with certificate-signing and CRL-signing usage;
- the server certificate chains only to that exact root without certificate downloads;
- every configured literal PostgreSQL IP is present as an IP SAN;
- the server certificate has exactly one CRL distribution point and it exactly matches the
  manifest endpoint;
- the CRL issuer and signature match the root, its update interval is current and bounded,
  and the server certificate serial is not revoked;
- the bounded responder can bind the declared loopback endpoint and serve an exact-byte
  self-probe before database work begins.

Missing, unreachable, stale, malformed, hash-mismatched, invalid-signature, or revoked
evidence fails closed with a structured fixture diagnostic. Chain and SAN mismatch continue
to fail. The connection path still forces Npgsql `VerifyFull`, the exact root certificate,
and `CheckCertificateRevocation=true`; there is no fallback to `Trust Server Certificate`,
`Require`, or a global revocation bypass.

The responder uses asynchronous accept rather than polling, a four-connection backlog, a
4 KiB request-header limit, manifest-bounded request count and response size, bounded
per-request timeouts, cancellation, and deterministic disposal. It exposes no secrets and
serves no path other than the exact CRL distribution point.

## Native ARM64 rehearsal v3

The ARM64 evidence bundle carries this document, the JSON schema, and a machine-readable v3
requirements file. The external rehearsal must account for two different loopback endpoints:
PostgreSQL TLS and CRL retrieval. Its receipt must identify both ports, record that the server
certificate contains the exact IP SANs and CRL distribution point, preserve the public
manifest and CRL hashes, show the responder's bounded ownership and cleanup, and prove that
the final Npgsql connection used `VerifyFull` with revocation enabled.

The v3 environment may create a task-local CA and responder outside Cortex solely for the
contained rehearsal. It must not place private keys, database credentials, or trust-store
changes in the bundle or receipt. Success does not authorize application lifecycle, install,
deployment, certificate trust, hardware mutation, or production-data access.
