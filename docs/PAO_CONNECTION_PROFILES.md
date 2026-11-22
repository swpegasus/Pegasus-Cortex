# Owner-local SMB connection profiles

The `connection_profile` PAO family manages nonsecret, versioned SMB drive
profiles for the normal signed-in Cortex owner. The fixed actions are `list`,
`read`, `save`, `remove`, `inspect`, `connect` and `disconnect`. Neither the
privileged service nor an elevated or alternate user is a fallback execution
route. Workspace, working-directory and Root selectors are not accepted.

Profiles contain an ID, label, exact `\\server\share`, drive letter and identity
mode. `currentUser` supplies no alternate credentials. `explicitRemote` requires
a remote account name and an SMB password reference; it never falls back to the
current account when that reference is unavailable. A profile cannot contain a
password. Certificate passwords and SMB passwords use separate Windows
Credential Manager target families, even if their reference names are equal.
The local password page uses a shared mutable capture/clear implementation;
raw passwords do not pass through PAO, request bodies or job history.

Submit the closed version1 JSON body as the MCP operation's `text` string or
through CLI stdin. Mutations require a unique RID. For example:

```text
pao connection_profile save --stdin rid=my-explicit-save
{"version":1,"kind":"smb-drive","expectedRevision":0,"profile":{"version":1,"kind":"smb-drive","id":"team-share","label":"Team share","remoteShare":"\\\\fileserver\\team","localDrive":"Z:","identityMode":"currentUser"}}

pao connection_profile read --stdin
{"version":1,"kind":"smb-drive","id":"team-share"}
```

`save` requires the observed `expectedRevision`: zero means the ID has never
existed. An update or restoration requires its current positive revision.
`remove` requires that revision and retains a compact tombstone with the next
revision. It does not disconnect a drive or delete the password reference.
The owner-private store uses a cooperative writer lock, retained-file checks and
atomic publication. It does not claim an operating-system compare-and-swap
against arbitrary same-owner file editors.

`list` accepts `limit`1–64 (default32) and the returned `afterId`. Its cursor
advances over scanned tombstones; a page can contain no active profiles and
still have a continuation. A bounded directory scan that cannot establish a
complete result reports `listing_bound` instead of pretending the list ended.
`read` distinguishes never-existing and removed profiles, including the
revision needed for deliberate restoration.

`inspect`, `connect` and `disconnect` require the exact saved ID, revision and
lowercase SHA256 `contentHash` returned by a read. Those pins are checked before
obtaining a connection provider or resolving a password. `disconnect` also
requires `expectedRemoteShare` equal to the saved share. A changed profile,
occupied device or remembered mapping is refused. These operations do not
adopt another connection, force-close open files, save credentials, prompt for
credentials or disconnect by remote name.

Connect uses the fixed Windows LANMAN provider and a temporary drive mapping.
The original owner SID, Windows session and logon identity must remain valid.
A synchronous native mapping call cannot be safely canceled after entry: the
Engine retains its worker and active-effect ownership until the actual call
returns. Observer cancellation does not retry, compensate, disconnect or
invent success. A verified late terminal result remains the actual outcome;
an unobserved issued effect reports recovery required. A refusal before native
entry retains `action_issued=false` and `outcome_unknown=false`.

A mapping observation proves a local drive/share association. It does not
prove remote authentication, password validity or share-content access.
Temporary Windows mappings can outlive Cortex within the same Windows logon;
Cortex does not disconnect them automatically on exit.

Read-only observations are ephemeral. Durable mutation requests retain their
operation selector, RID and omitted-body marker; receipts retain compact scalar
outcome flags and a normalized request-identity hash. Profile contents, remote
accounts, endpoints and credential references are excluded from execution
history. Repeating the same RID and exact request returns its existing receipt;
changed coordinates are refused. Recovery never replays a lost private body.
An uncertain result requires explicit observation and a new deliberate action.

Connections & access presents the password and profile controls as lazy pages.
Opening a page does not read credentials, load profiles, inspect a drive or
connect. Loading, refreshing, saving, removing and mapping are explicit actions;
mapping and removal require confirmation. Closing a page does not cancel or
release an already-submitted mutation before its real completion.

This source unit is ahead of the signed06.34 candidate. Fake native and owned
file/SQLite fixtures, real Windows credentials, real SMB networking, visible UI
and installed execution are separate evidence tiers; source presence and a
catalog entry do not qualify those live behaviors.
