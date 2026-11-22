# Typed local registry operations

The source exposes seven operations through the existing PAO entry: `registry_inspect`,
`registry_browse`, `registry_read`, `registry_create`, `registry_set`,
`registry_delete_value`, and `registry_delete_empty_key`. They run in the verified
ordinary signed-in owner host. They do not elevate, start a host, fall back to the
LocalSystem service, or accept root codes, workspaces, working directories or
deferred completion. Windows permissions still determine which exact resource can
be accessed. Adding this contract does not grant a client administrative access.

Use the advertised operation help for its current fields. CLI form is
`pao registry read --stdin`; the body arrives on standard input. The unified MCP
form is `op: registry_read` with the same body as a `text` string. Both accept
optional `detail`; mutations additionally require an explicit `rid`. MCP `output`
is presentation only. Duplicate and unknown fields are refused before execution.

Every body is strict JSON, version 1, at most 96 KiB UTF-8 and depth 12. An exact
key is `{ "hive": "current_user", "view": 64, "keyPath": "Software\\Example" }`.
Supported hives are `current_user`, `local_machine`, and `users`; view must be 32
or 64. Names retain Unicode and whitespace. Default values use an explicit empty
`valueName`; omitting that field is not the default-value shorthand.

An example read body is:

```json
{
  "version": 1,
  "key": { "hive": "current_user", "view": 64, "keyPath": "Software\\Example" },
  "valueName": ""
}
```

| Action | Additional body fields |
| --- | --- |
| `inspect` | None |
| `browse` | `kind`: `keys` or `values`; optional bounded `limit` and returned `cursor` |
| `read` | `valueName` |
| `create` | `parentGeneration` from a fresh exact parent observation |
| `set` | `valueName`, `value: { type, base64 }`, `preimage: { keyGeneration, valueDigest }` |
| `delete_value` | `valueName`, `preimage: { keyGeneration, valueDigest }` |
| `delete_empty_key` | `keyGeneration` |

Generation and digest fields are exact lowercase 64-character hashes returned by
the registry module. A `set` preimage with null `valueDigest` requires an absent
value; deleting a value requires its observed digest. A browse cursor is copied
unchanged from the returned typed cursor, including its numeric kind and generation.
Raw value bytes use Base64; supported types are `none`, `string`, `expand_string`,
`binary`, `dword`, `dword_big_endian`, `multi_string`, and `qword`.

Preimage checks and Windows writes are separate operations, not an atomic
compare-and-swap. A write can report an uncertain outcome if its postcondition
cannot be observed. Inspect that original target and receipt before deliberately
submitting a new request. Never automatically retry a mutation with a fresh RID
or substitute a new preimage after a lost response.

Reads are nondurable snapshots. The original mutation submission receives its
live typed result. Saved history, repeated mutation RIDs, and history waiters
contain only compact outcome and request-identity metadata; target names, values,
generations, cursor bodies, and raw request/result content are not retained there.
The same RID with a changed typed request is refused, including cross-operation
reuse. A restart that loses a private mutation body preserves an unresolved receipt
and its identity; it never reconstructs or executes the request. Pruning retains
the same replay barrier.

`--registry-wiring-only` in AgentOps.Tests covers the pure parser, injected owner
admission, fake registry boundary, isolated real SQLite history/recovery, and
captured CLI/MCP presentation. This is separate from live native registry tests
and installed-product verification. Source changes do not imply that the running
installed PAO already exposes these operations.
