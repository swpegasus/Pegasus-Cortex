# Native PostgreSQL fixture

This fixture uses an externally provisioned PostgreSQL database on a literal loopback IP.
Pegasus Server or the authorized operator owns database and role creation, native backup and
restore commands, operation receipts, and database cleanup. The fixture never invokes
`pg_dump` or `pg_restore`, and restored-state equivalence is not evidence that either command
ran successfully.

## Common environment

- `PEGASUS_CORTEX_PG_INTEGRATION=1`
- `PEGASUS_CORTEX_TLS_POLICY=verify-full|trusted-loopback`
- `PEGASUS_CORTEX_PG_MIGRATION_CONNECTION=<migration-owner connection>`
- `PEGASUS_CORTEX_PG_RUNTIME_CONNECTION=<restricted-runtime connection>`

Both connections must use literal loopback addresses, one fresh `ctx_it_` database, and
distinct roles. `PEGASUS_CORTEX_PG_PREFLIGHT_ONLY=1` cannot be combined with either proof
mode. The normal live run applies the complete migration ledger through `0007`, reconciles
the production runtime privileges, and verifies the migration-0007 publication-change
tables, exact recorded/installed RLS policy clauses, readiness parity, one-field
authority/subject/service owner denials, withdrawal and supersede/replacement lineage,
idempotency, and CAS concurrency through the real PostgreSQL repository.

### Trusted loopback

`trusted-loopback` is the small isolated-host profile used when Cortex and PostgreSQL share
the same protected ARM host. Both connection strings must be plaintext (`SSL Mode=Disable`),
must use literal loopback IPs, and must not contain a root certificate or trust bypass.
Revocation evidence variables are prohibited in this mode. The fixture does not weaken the
loopback-only, fresh-database, separate-role, forced-RLS, or no-`BYPASSRLS` requirements.

### Private CA verify-full

`verify-full` is the default for compatibility. It additionally requires:

- `PEGASUS_CORTEX_PG_REVOCATION_EVIDENCE_ROOT=<owner-only public PKI evidence directory>`
- `PEGASUS_CORTEX_PG_REVOCATION_MANIFEST_FILE=<safe v1 manifest leaf>`

Both connections must use `VerifyFull` and the same absolute private-CA root.

The versioned revocation profile is `pegasus.cortex.postgresql.private-ca-crl/1`. The evidence
root contains only the public root certificate, public server certificate, signed CRL, and
manifest. The server certificate must cover both literal connection IPs and embed exactly the
manifest's loopback CRL distribution point. That endpoint uses a different port from both
PostgreSQL connections. The fixture verifies chain, SAN, hashes, CRL signature, freshness,
and non-revoked status before starting its bounded test-only responder. Npgsql still uses
`VerifyFull` and `CheckCertificateRevocation=true`; the fixture provides no trust or
revocation bypass.

See `docs/POSTGRESQL_PRIVATE_PKI_REVOCATION_V1.md` and
`schemas/postgresql-private-pki-revocation.v1.schema.json` for the takeover contract. The
external production operator owns long-running CRL or OCSP publication and rotation; the
contained fixture responder is not a product runtime component.

Capture adds:

- `PEGASUS_CORTEX_PG_BACKUP_RESTORE_MODE=capture`
- `PEGASUS_CORTEX_PG_BACKUP_RESTORE_PROOF_ROOT=<existing trusted stage>`
- `PEGASUS_CORTEX_PG_BACKUP_RESTORE_PROOF_FILE=<lowercase-safe-name.json>`

After capture exits, the external owner quiesces the source, performs the native backup and
restore into a distinct fresh `ctx_it_` database, and retains its own operation receipt.
Restored-state equivalence uses the target connections and changes only the mode:

- `PEGASUS_CORTEX_PG_BACKUP_RESTORE_MODE=verify-restored-state`

## Proof stage boundary

The proof root must already exist, be a canonical absolute local path, and contain no reparse
or symbolic-link component. On Unix its mode must be exactly `0700`. The leaf is one segment,
at most 64 UTF-16 characters and 255 UTF-8 bytes, uses only lowercase ASCII letters, digits,
dot, underscore, and hyphen, and ends in `.json`. Windows reserved names, ADS syntax, trailing
normalization, path escape, and final paths above 240 UTF-16 characters are rejected.

Capture publishes a canonical proof through a private same-root temporary file and atomic
no-overwrite rename. New Unix proof and temporary files use mode `0600`. Verification accepts
only a private regular file and performs all proof and filesystem checks before certificate or
network work.

The externally owned `0700` stage excludes hostile same-UID mutation from this fixture's threat
model. The checks narrow accidental and cross-identity exposure; they do not claim immunity to
a cooperating or compromised process running as the same Unix user between validation and I/O.
