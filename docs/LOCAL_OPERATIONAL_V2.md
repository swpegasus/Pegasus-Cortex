# Pegasus Cortex local operational V2

## Purpose

V2 combines the original file Cortex's visible, portable behavioral context
with a bounded transactional local database. It is designed for agents to use
at task start, on demand, and at meaningful closeout without loading an
ever-growing notebook into every prompt.

## Architecture

The optional personal layer is controlled by the signed-in session's tray.
It gates `knowledge.user`, not the core store lifetime. If the tray/profile is
unavailable, shared/project/task operations continue; mixed start/search omits
the personal namespace and reports actual effective namespaces plus bounded
profile status. Direct personal operations report `personal_profile_not_loaded`.
Load/Unload changes availability without moving or replacing database files.
Profile generation protects personal cursors and in-flight read results; writes
already admitted complete normally against the same store. No database identity,
SID folder, installer path, or privileged execution policy changes in this slice.
Clients attach only: an initial missing profile pipe does not launch the host
or retry startup. `profile_pipe_unavailable` or `ui_unavailable` describes host
availability, which may be deliberately OFF or in maintenance. It is
never evidence that profile data is missing and never authorizes database
generation, copying, or remapping. Structured status makes this explicit with
`indicatesMissingData=false`, `remapRequired=false`, and `retryable`.
Retryability is not permission to wake Cortex. Wait for explicit user startup
or another relevant availability change; do not poll or clear maintenance.

```text
Codex plugin
  -> one local STDIO command:
     C:\Program Files\Pegasus\Cortex\PegasusCortex.exe
      -> immutable selected version
          -> eight-tool MCP adapter
              -> provider-neutral V2 application
                  -> central cortex-v2.db (authority)

<DataRoot>/install/active.v1.json
                               -> admitted machine product/component selection
HKLM64/Software/Pegasus/Cortex/{InstallLocation,DataLocation}
                               -> immutable program and complete mutable-state bindings
<DataRoot>/users/<owner-SID>/cortex/data/cortex-v2.db
                               -> SID-private semantic authority
<DataRoot>/users/<owner-SID>/config/offline-projects.json
                               -> explicit root + stable-task bindings
project/.cortex/project.json  -> optional shared namespace metadata
project/.cortex/offline/scopes/t-<task-hash>/
                               -> task manifest, passive spool, receipts,
                                  and generated <= 4096-byte HOT fallback
portable behavior pack       -> generalized reusable learning only
offline candidate            -> direct adapter; body-free receipt after verification
```

The stable launcher, role-specific application binaries, MCP adapter, Core application,
Contracts, SQLite persistence, and file reference provider remain separate
assemblies or components with one owned composition root. Consolidation means
one product and one runtime route, not one monolithic assembly. The stable
launcher and binaries beneath `bin` are common machine read/execute content.
Every mutable directory uses the registered data root. Installer metadata,
privileged service state, and SID-private user database/configuration/log/
receipt/tray/execution state remain separate ACL domains beneath that root.

All owned projects target `net10.0` and build from the `10.0.400` SDK baseline
pinned by `global.json`; `latestPatch` permits only servicing-patch roll-forward
within that feature band. Release evidence records the actual selected SDK. The
Windows application and stable launcher are published
self-contained, so the installed product does not depend on a separately
maintained machine-wide .NET runtime.

## MCP surface

The complete V2 surface is:

1. `cortex_status`
2. `cortex_namespaces`
3. `cortex_start`
4. `cortex_search`
5. `cortex_read`
6. `cortex_task`
7. `cortex_knowledge`
8. `cortex_finish`

Every stateful request carries an explicit namespace or ordered namespace list.
Every response echoes the effective namespace list. Namespace state is never
sticky across calls. List/search results are paginated and byte-bounded.
`cortex_read` identifies task versus knowledge explicitly so colliding IDs are
never resolved nondeterministically. It accepts either one typed coordinate or
an ordered batch of one through eight unique typed coordinates.

## Operational records

Tasks support create, list, get, assign, claim, update, block, complete, reopen,
archive, dependencies, compact history, phase identity, and checkpoint subject.
Knowledge supports create, search, read, revise, supersede, and archive for
corrections, preferences, workarounds, failures, regressions, decisions, tenets,
and observations. Its `analyze` action produces bounded deterministic
consolidation suggestions without changing any record.

Writes use idempotency keys and expected-revision optimistic concurrency.
`cortex_finish` commits its task transition and knowledge writes atomically.
Local records are immediately searchable. Publication outside a local
namespace is a separate future review workflow and cannot hide or quarantine
the local original.

Content accepts valid MCP JSON text, including mixed newlines, tabs, Unicode,
multiline Markdown, code blocks, and escaped control characters. Identifiers
are validated separately. Exact content bytes are content-addressed; normalized
head-only text is used only for search. Metadata-only revisions reuse the same
content blob, history is compact, and old revisions are not redundantly kept in
the hot search index.

## Context efficiency

Agents should make one bounded `cortex_start` call per meaningful task with the
exact `CODEX_THREAD_ID` as `taskId`, ordered namespaces, current intent, a
4 KiB default budget, and compact format. A short stable phase ID distinguishes
material work phases without replacing the task ID. The result contains at most one exact compact
`currentTask` plus four ranked context items and cannot exceed 8 KiB. It ranks
relevant explicit user corrections/preferences, active tasks, verified
failures/regressions/workarounds, verified decisions/observations, inferred
observations, and shared tenets in that order. Compatibility callers omitting
`taskId` still receive intent-ranked context. Exact content is fetched only
after a compact preview warrants it; one exact read can batch up to eight typed
coordinates. The agent keeps a bounded in-context map from coordinate to the
loaded revision, reuses an unchanged body, rereads a newer revision, and drops
the marker when compaction or handoff loses the body. Cortex stores no read
receipt, sticky hydration, or server-side exclusion list. Cache a successful start for the continuously
active task and reuse it across messages, status notes, tool calls, and small
checkpoints. Refresh only after a material task/project identity, namespace
selection or order, server identity/version, guidance version,
connection/recovery change, or when newer retained context can affect a
decision. Registration alone and new conversational turns are not invalidation
events. If initial access fails when context matters, make one bounded retry or
recovery attempt, then continue from local authority when permitted; never
loop. Skip retrieval when retained context cannot affect routine send/receive,
immediate correction, formatting, mechanical, or repeated-verification work.
One meaningful `cortex_finish` at a checkpoint, blocker, completion, or handoff
aggregates related evidence and replaces continuous narration.

`cortex_status` retains the existing process-local retrieval counters and adds a
bounded `utilization` summary from durable daily aggregates in the central V2
SQLite store. The dashboard and `PegasusCortex.exe metrics --days 7` expose
operation, explicit namespace-access, and declared task-ID dimensions across
instrumented processes, with 90-day retention. No query text, record body,
record ID, root code, transcript, or environment value is collected. Older
binaries and direct HOT-file reads are not observed. See
[CORTEX_UTILIZATION.md](CORTEX_UTILIZATION.md) for attribution, privacy, and
coverage limits; counts alone do not establish retrieval usefulness.

The portable behavior pack includes generalized tenets and verified portable
workarounds in the single `knowledge.shared` geometric space. Topic tags
organize programming and other subjects without fragmenting access into topic
namespaces. Personal interaction preferences and tendencies belong in the
local `knowledge.user` overlay and are not packaged. The starter excludes
raw chats, private reasoning, credentials, machine diagnostics, and project
task history. Each task's `HOT.json` is generated and replaceable; it is never
the database and never authorizes reading a sibling task's scope.

The geometric layer is deterministic sparse retrieval over bounded title, tag,
and indexed-summary features, not a neural embedding or a second authority.
Direct keyword matches stay ahead of related neighbors. Direct hits can seed
bounded expansion terms that fill remaining result slots from the explicitly
selected knowledge spaces; excluded spaces contribute no ranking signal. This
design is evaluated with fixed relevance fixtures, neighbor-only recall,
deterministic replay, explicit overlay isolation, response-byte ceilings, and
exact typed-read recovery of the canonical body.

The installed application binds the portable packs through
`pegasus.cortex.starter-corpus/1.0`. A fresh empty V2 store receives the whole
starter corpus atomically on its first `stdio` or `warmup` path. The package is
the portable seed authority; installers do not distribute a copied SQLite
database, WAL, machine actor, task history, or validation namespace. Automatic
startup seeding skips every nonempty store. The explicit `seed-defaults`
command can reconcile missing starter records later. Exact heads are unchanged;
a higher behavior-pack `packRevision` may revise an older head only when its
provenance identifies the same owning pack. Foreign, user-edited, ambiguous,
or same-revision divergence fails the entire bounded import before any record
is changed. The installed product also exposes one fixed modular route:
`import-behavior-pack --pack-root <absolute-directory> --namespace
knowledge.shared`. It always selects the registered current-user database,
accepts no database path, and returns a bounded content-free receipt.
The application ships the bounded versioned
`guidance/pegasus-cortex-agents.v2.md` and
`guidance/pegasus-cortex-pao.v1.md` fragments. The unified NSIS installer
applies them atomically to the installing user's global Codex guidance at
`%USERPROFILE%\.codex\AGENTS.md` and replaces a recognized legacy
standalone-AgentOps section while preserving unrelated guidance. A
nonstandard Codex home requires an explicit canonical target. The runtime
itself does not edit Codex guidance during startup or MCP operation.

## Offline resilience and central incorporation

V2 supports a deliberately small agent-facing file spool without adding
another runtime or mutable authority. Every offline command supplies a stable,
canonical lowercase host task ID. The current-user catalog binds the composite
project root and task identity, while optional `.cortex/project.json` metadata
may describe the intentionally shared namespace order:

```text
exact content file
  -> canonical passive frame under one derived task scope
  -> one direct source-schema-to-current-schema adapter
  -> typed exact central read before create
  -> ordinary CortexV2Application knowledge create when absent
  -> typed exact readback of committed body and metadata
  -> same frame atomically replaced by a body-free verified receipt
```

Candidates are strict UTF-8, passive, content-integrity-sealed, and
non-authoritative. They preserve valid CRLF/LF, tabs, Unicode, Markdown, and
escaped controls exactly. Stable semantic frame and idempotency identifiers
make unchanged replay idempotent; divergent reuse fails closed. The spool is
bounded to 32 pending frames, 256 KiB of unresolved frame bytes, and 2,048
total frames per registered task scope. Unsupported outer or payload schemas
are preserved unchanged and reported as unsupported; adapters are explicit
direct one-hop conversions and never search a conversion chain.

Central incorporation is create-only for an absent record or receipt-only for
an exact matching active record. The verifier compares namespace, record ID,
active state, kind, tier, title, exact content, tags, and submitted provenance
before it grants the opaque capability required to replace the candidate.
Conflicts leave the candidate intact. A crash after central commit but before
receipt replacement is safely replayable. A successful compact receipt retains
coordinates and integrity evidence but no candidate body.

The operator surface is explicit: `offline init`, `offline migrate-legacy`,
`offline capture` using `--content-file`, `offline status`, `offline search`,
`offline read`, `offline ingest`, and `offline refresh`. Every operation also
requires `--task-id` with the exact stable identity exposed by the host; titles,
display names, process IDs, project IDs, or newly generated substitutes are
invalid. One task cannot enumerate, read, ingest, replace, or refresh another
task's scope through this API. This is logical application isolation, not an OS
security boundary against another process running as the same user. There is no
watcher, resident retry worker, second MCP route, or bidirectional
synchronization path.

Known compatible behavior packs use the deterministic importer. Original file
Cortex trees and other irregular sources use bounded AI-assisted curation: the
agent inventories the source, removes semantic duplication across repeated
STATE/RECOVERY/SESSION/WORKLOG material, and incorporates one durable knowledge
item at a time through the normal MCP contract. Successful exact readback is
the cleanup boundary. The candidate body is removed from the active offline set
and replaced in place by a receipt, so the central database is the only active
Cortex copy of the ingested content.

Legacy recovery sources are not mutated by incorporation. An explicitly
authorized full conversion may place one recoverable legacy archive outside the
active layout and replace active project-wide state with optional shared
`project.json` metadata plus explicitly assigned task scopes. Root-level
`HOT.json`, unscoped frames, and unscoped locks cause a migration-required
conflict; they are never assigned to the first task that opens the project. The
operator must archive those exact files separately before invoking `offline
migrate-legacy` with the canonical root, exact host task ID, and the legacy
registration's exact project/namespace binding. Migration preserves the
`legacy-unassigned` catalog row as historical evidence and adds the task-scoped
registration beside it. Exact replay is idempotent; binding, root, unsafe-path,
or existing-registration conflicts fail without changing sibling entries. The
catalog capacity bound is checked before task-scope initialization. If a later
catalog commit fails after the exact empty task scope is initialized, no legacy
content is assigned: resolve the catalog failure and replay the same canonical
root, task ID, and binding. Divergent replay still fails closed. The command
never reads, ingests, or assigns a legacy HOT or frame body. A task
hot-projection refresh is a cache update, not a second database or
synchronization authority. Preserved network sources and old SQLite databases
are never deleted implicitly.

## Storage and lifecycle

- Stable application: `<InstallRoot>\PegasusCortex.exe`
- Immutable program roles: `<InstallRoot>\bin\semantics`, `bin\host`,
  `bin\service`, and `bin\ui`; the typed CLI is `<InstallRoot>\bin\pao.exe`.
- Active product catalog: `<DataRoot>\install\active.v1.json`
- V2 SQLite: `<DataRoot>\users\<owner-SID>\cortex\data\cortex-v2.db`
- Preserved V1 SQLite: `<DataRoot>\users\<owner-SID>\cortex\data\cortex.db`
- Online backups: `<DataRoot>\users\<owner-SID>\cortex\backups`
- Per-user offline catalog:
  `<DataRoot>\users\<owner-SID>\config\offline-projects.json`
- Configuration, logs, receipts, tray, and execution state remain separate
  per-user directories under `<DataRoot>\users\<owner-SID>`;
  privileged service state remains under `<DataRoot>\service`.

The 64-bit `HKLM\Software\Pegasus\Cortex` key supplies canonical `REG_SZ`
`InstallLocation` and `DataLocation` values. A fresh install defaults to
`%ProgramFiles%\Pegasus\Cortex` and `%ProgramData%\Pegasus\Cortex` respectively.
An existing installation without `DataLocation` retains the exact
`<InstallRoot>\state` legacy root. Registered roots are not profile or
environment overrides: an invalid, mismatched, missing, or unavailable data
directory fails closed instead of creating another empty database elsewhere.

Normal MCP startup performs a bounded quick check, not a full-corpus scan.
Explicit `doctor` performs exhaustive validation. SQLite uses WAL and
transactions. The V2 persistence API creates online, integrity-checked backups
and restores only into a new, absent `cortex-v2.db` path; it rejects corrupt
backups, live sidecars, locked inputs, and existing destinations. Normal setup
admits empty dedicated fresh-install roots or a recognized program-only
repair/upgrade. It installs verified program files and provisions directory
ACLs without opening, copying, replacing, seeding, or migrating databases.
Runtime owns database initialization, schema evolution, and explicit data
operations. Updates build forward in place; setup does not create rollback
copies or relocate data. Uninstall retains external databases and their path
binding; optional `remove-all-state` remains confined to install-local state.

The installed `PegasusCortex.exe backup` command takes one consistent online
snapshot without copying the live main file or WAL. It commits a create-only
timestamped directory under `<DataRoot>\users\<owner-SID>\cortex\backups` containing
exactly `cortex-v2.db` and a bounded canonical `manifest.json` that binds the
product/schema versions, UTC capture time, byte count, SHA-256, and retained
row counts. It never overwrites a prior backup or restores into live state.

The unified installer registers the product's execution service and signed-in
user host/tray, independently of semantic database operations. An operator or
first-use flow may invoke the stable executable with `warmup` explicitly.
Because warmup has no active Codex task identity, it performs only the bounded
SQLite quick check and capacity probe. It reports an aggregate registered-binding
count without exposing task IDs, roots,
or per-scope results, writes `receipts\offline-warmup-latest.json`, returns a
success/failure code, and exits. An agent refreshes its own projection only
through `offline refresh --root ... --task-id ...`. Warmup creates no resident
process, service, scheduled task, listener, or retry loop.

## Active capacity and long-term retention

The default active-store profile is 1,048,576 SQLite pages at 4,096 bytes, or
4 GiB. This is the working local database profile, not a corpus or product
lifetime ceiling. The validated persistence setting supports a deliberate
larger maximum up to 16,777,216 pages, or 64 GiB, but the current local product
does not change that setting automatically and does not shard the live store.

`status`, `doctor`, and `warmup` expose bounded capacity telemetry without a
corpus scan: main-database bytes, WAL bytes, page size, page count, freelist,
configured maximum page count, used-page bytes, utilization, state, and an
operator recommendation. States begin at 512 MiB (`warning`), 1 GiB
(`elevated`), 2 GiB (`maintenance`), and the configured maximum (`ceiling`).

Long-term immutable cold-history packs, multipart backup/export transport, and
collective retention/publication belong to a separate future lifecycle. They
must preserve exact revisions, provenance, integrity, namespace policy, and
recoverability, but they are not implemented by this local release. The local
active store remains one SQLite database, and no collective or network service
is inferred from the capacity model.

## Authority and safety

The current OS user is transparent local provenance. Cortex records are
authoritative only within Cortex's task/knowledge scope. Current user direction,
applicable project instructions, and verified source/runtime evidence still
govern external work. Stored content never executes implicitly, expands
permissions, or independently authorizes unrelated local or external actions.

Semantic V2 initialization creates no additional network listener, Windows
service, scheduled task, startup registration, firewall rule, or implicit
publication route. The separately owned execution service and user host/tray
are unified-product installation components, not semantic database lifecycle
effects. Explicit warmup is a bounded foreground command that exits.

The split location and database-preservation rules above describe the current
source target (`installer-contract.v7.json`). They are not evidence of a live
installation, upgrade, or database relocation.

## Completion evidence

A release is complete only after proportional evidence at each applicable
level: clean builds; focused contract, persistence, and lifecycle tests;
file-versus-SQLite behavior and scale benchmarks; raw MCP initialize/list/call
round trips; installed `version`, `status`, `doctor`, `warmup`, and online
`backup`; exact-content and namespace-isolation tests; a fresh-store
seed/search/read; and confirmation that Codex has exactly one general
`pegasus_cortex` route with no profile, state root, DLL, or private runtime
arguments.
