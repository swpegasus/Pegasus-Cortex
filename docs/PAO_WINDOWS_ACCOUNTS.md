# Local Windows account and session observations

`windows_account_list` and `windows_account_read` are the fixed PAO observations
for Windows OS accounts, local groups and Windows sessions. These observations
are separate from Cortex clients, Nexus identities, and terminal-manager sessions.
They do not grant permissions or select another execution identity.

The route uses the existing ordinary signed-in owner Host. It rejects System,
UAC, remote-machine, alternate-credential, workspace, working-directory, path-root
and asynchronous-job selectors. It does not create durable job-history rows.
Reads reobserve Windows; an existing durable RID does not replay unrelated history.

The unified MCP operation takes `op`, `text`, optional `rid`, and optional boolean
`detail`. `text` is a closed version 1 JSON object, at most 4096 UTF-8 bytes.
Unknown and duplicate properties are rejected. The CLI equivalent is
`pao windows_account list --stdin` or `pao windows_account read --stdin`, with
the JSON body supplied on standard input.

For example, list a small page of users:

```json
{"version":1,"view":"users","limit":8}
```

The views are:

| View | Observation |
| --- | --- |
| `users` | Local normal Windows user-account metadata. |
| `groups` | Local Windows group names. |
| `members` | Member SIDs of one exact local group, without SID-to-name resolution. |
| `sessions` | Local Windows session observations, subject to the caller's query permissions. |
| `current` | Current Host identity metadata, not a transferable token or authorization result. |

`members` requires `group`; all other views reject it. No default group or
translated English group name is assumed. A request for a particular group is:

```json
{"version":1,"view":"members","group":"Example local group","limit":8}
```

List uses `limit` from 1 through 64 (default 32), and the returned `afterKey`
for another page. Read requires one exact returned `key` in the same view and
group scope. Keys identify observed coordinates; they are not stable account or
session generations. Pages are fresh observations, not an atomic snapshot.
An incomplete capture cannot prove that a missing key does not exist.

Only verified local workstation or member-server SAM roles are accepted for
users, groups and membership. Domain controllers and an unproven local role are
refused, so a local API call cannot silently become an Active Directory inventory.
Member SIDs can refer to domain principals; resolving them against a directory is
outside this route. Windows' level-20 user-information buffer includes full-name
and comment pointers; Cortex never follows or returns them. Home/profile paths,
passwords, credentials, raw tokens and client addresses are not requested.

Managed copying admits at most 4096 rows and a page at most 256 KiB. User/group
enumeration uses a bounded number of native calls and preferred-size buffers.
Those preferred sizes are not hard limits on Windows' allocations. Membership
uses one whole-result native call, and Windows session enumeration also allocates
its native result before count admission; these limitations are exposed in
coverage metadata. An oversized or invalid native result is refused rather than
silently dropping rows or advancing past an omitted page tail.

The complete membership call avoids abandoning a normal paged enumeration:
Microsoft requires an `ERROR_MORE_DATA` membership enumeration to be continued
through success to avoid an RPC connection leak. All acquired buffers are freed
on success, failure and cancellation. Synchronous Windows calls are joined;
cancellation is checked around calls rather than forcibly aborting them.
See [NetLocalGroupGetMembers](https://learn.microsoft.com/en-us/windows/win32/api/lmaccess/nf-lmaccess-netlocalgroupgetmembers)
and [NetLocalGroupEnum](https://learn.microsoft.com/en-us/windows/win32/api/lmaccess/nf-lmaccess-netlocalgroupenum).

Native access errors retain their numeric source and explicit coverage state.
There is no implicit elevation, fallback identity, account creation/deletion,
membership change, password operation, session logoff or disconnect. Those
administrative capabilities are separate unfinished R2 work.

The focused development selector is `--windows-accounts-only` in
`Pegasus.AgentOps.Tests`. It combines synthetic contract/provider/native-buffer
fixtures and captured CLI/MCP routing with an owned temporary SQLite database.
It does not enumerate actual accounts or sessions, invoke UAC, or change the OS.
Passing that selector is not installed or native Windows acceptance.
