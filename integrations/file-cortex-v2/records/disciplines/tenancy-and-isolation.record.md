# Tenancy and Isolation

These tenets specialize `CORE-AUTH-*`, `REL-004`, `PERF-*`, `PRIV-*`, `DATA-*`, and `OBS-*` for systems that isolate users, organizations, projects, workspaces, or other security principals.

## TENANT-001 — Propagate trusted tenant context everywhere

**Rule:** The authorized tenant context must constrain primary data, caches, indexes, queues, logs, exports, notifications, background jobs, search, and derived artifacts consistently.

**Practice:** Establish tenant context from trusted identity and policy, carry it through typed boundaries, and include it in every storage key, query, job, and cache scope that can cross tenants.

**Boundary:** A record, path, header, or project identifier supplied by the caller is a lookup input, not authorization by itself.

## TENANT-002 — Fail closed when tenant ownership is ambiguous

**Rule:** Missing, conflicting, or unresolvable tenant ownership must not fall back to a global, default, first, or current tenant.

**Practice:** Reject the operation with bounded diagnostics and preserve enough correlation to investigate without exposing another tenant's existence or content.

**Boundary:** Deliberately public data may use a separate public scope, but it must not be the fallback for failed private authorization.

## TENANT-003 — Isolate derived and asynchronous state

**Rule:** Replicas, search indexes, caches, analytics, temporary files, dead-letter queues, and background work must preserve the same tenant boundary as their source.

**Practice:** Bind tenant identity and source revision to derived records and jobs, validate them again at execution, and prevent shared worker state from leaking between items.

**Boundary:** De-identification or aggregation creates a new dataset only after its re-identification and disclosure risk is reviewed under `PRIV-*`.

## TENANT-004 — Partition resource pressure

**Rule:** One tenant's load, retries, storage, or expensive queries should not exhaust resources promised to unrelated tenants.

**Practice:** Apply tenant-aware quotas, admission, concurrency, rate limits, storage bounds, and observability while preserving system-wide safety reserves.

**Boundary:** Shared capacity may burst across tenants when policy allows it, but overload handling must remain fair and must not weaken isolation.

## TENANT-005 — Make lifecycle operations tenant-complete

**Rule:** Export, retention, deletion, backup, restore, migration, and legal hold must cover every authoritative and derived location owned by the selected tenant scope.

**Practice:** Inventory primary and secondary stores, verify scope before mutation, and record incomplete or delayed cleanup explicitly.

**Boundary:** Shared aggregates and immutable audit may require governed retention or redaction instead of physical deletion; state that limitation truthfully.

## TENANT-006 — Constrain operational access

**Rule:** Support, administration, impersonation, and recovery access across tenant boundaries require explicit purpose, narrow scope, duration, and audit.

**Practice:** Prefer scoped tools and delegated grants over shared superuser credentials, and surface active elevated access to the appropriate operators or users.

**Boundary:** Emergency access remains governed authority, not a hidden bypass, and must be revoked or reconciled after use.
