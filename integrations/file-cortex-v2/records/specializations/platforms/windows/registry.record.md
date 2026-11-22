# Windows Registry Tenets

These tenets specialize `CORE-AUTH-003`, `CORE-AUTH-004`, `CORE-AUTH-007`, `CORE-WORK-005`, `DATA-*`, and `CONFIG-*` for Registry ownership and durable configuration.

## REG-001 — Use the Registry for appropriate Windows state

**Rule:** Store configuration and discovery data in the Registry only when Windows integration or the consuming application expects it.

**Practice:** Prefer ordinary configuration or durable data stores for large, portable, transactional, or user-authored data.

**Boundary:** The Registry is not a general document store, secret vault, or database replacement.

## REG-002 — Choose the hive by ownership

**Rule:** User-specific configuration belongs to the intended user context; machine-wide configuration requires explicit machine authority.

**Practice:** State the hive, effective token and profile identity, and whether policy, service, or interactive application code consumes the value; distinguish same-account UAC consent from alternate-credential elevation.

**Boundary:** Elevation alone does not necessarily change `HKCU`; alternate credentials or a service context can. Verify the effective identity rather than inferring it from an elevated window.

## REG-003 — Select the registry view explicitly

**Rule:** Select the 32-bit or 64-bit registry view required by the consuming component's declared storage contract through supported APIs.

**Practice:** Use process and OS architecture as evidence, but choose the view explicitly instead of hard-coding `Wow6432Node`; either process architecture can access either view through the appropriate API.

**Boundary:** OS architecture and current-process architecture do not by themselves determine which view owns a value.

## REG-004 — Preserve value types

**Rule:** String, expandable string, integer, multi-string, binary, and other registry types have distinct semantics.

**Practice:** Read and write the type expected by the consumer and preserve the prior type for rollback.

**Boundary:** Textual similarity does not make two registry types interchangeable.

## REG-005 — Modify only owned values

**Rule:** Change the narrowest key or value owned by the application or requested integration.

**Practice:** Avoid replacing or recursively deleting shared parent keys; make writes idempotent and re-read before overwriting concurrent state.

**Boundary:** A key's location beneath an application name does not prove exclusive ownership.

## REG-006 — Record exact rollback state

**Rule:** When rollback matters, capture the prior existence, value, type, registry view, and relevant permissions before mutation.

**Practice:** Distinguish restoring an absent value from restoring an empty value.

**Boundary:** Registry exports can contain unrelated or sensitive data; keep backups narrowly scoped and protected.

## REG-007 — Respect policy-managed state

**Rule:** Group Policy and management systems own policy-backed values and may restore them automatically.

**Practice:** Detect or document policy ownership and change the authoritative policy source when authorized.

**Boundary:** A successful direct write is not evidence that policy allows or will retain the change.

## REG-008 — Keep credentials out of ordinary values

**Rule:** Do not store plaintext passwords, tokens, private keys, or equivalent secrets in normal registry values.

**Practice:** Use an approved protected credential mechanism and persist only a bounded reference when needed.

**Boundary:** ACL restrictions alone may be insufficient for high-value long-lived secrets.

## REG-009 — Verify the consumer, not only the write

**Rule:** A successful registry operation proves only that the value was written.

**Practice:** Verify the intended application's behavior and state any refresh, restart, sign-out, or reboot requirement.

**Boundary:** Do not restart automatically unless the task authorizes it.
