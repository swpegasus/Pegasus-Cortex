# Pegasus Cortex client capabilities

## Contents

- [Production route and authority](#production-route-and-authority)
- [Pegasus AgentOps interoperability](#pegasus-agentops-interoperability)
- [Central MCP surface](#central-mcp-surface)
- [Exact reads and unavailable tools](#exact-reads-and-unavailable-tools)
- [Support and evidence limits](#support-and-evidence-limits)
- [Precedence and learning](#precedence-and-learning)
- [Installed offline CLI](#installed-offline-cli)
- [Offline frame and incorporation contract](#offline-frame-and-incorporation-contract)
- [Capacity and retention](#capacity-and-retention)
- [Behavior packs and legacy fallback](#behavior-packs-and-legacy-fallback)

## Production route and authority

Use the configured shared Pegasus Cortex MCP endpoint, loopback by default,
owned by the existing admitted Cortex application/service lifecycle. Chats attach
independent sessions to that owner instead of launching per-chat adapters.
Inspect the actual registered URL, selected owner and live contract when
diagnosing a consumer. Configuration, listener readiness and actual Codex
connection are separate observations.

This source plugin provides skills and guidance only. It has no MCP server
declaration, URL registration, launcher or per-chat bootstrap. Editing it does
not activate an installed plugin or change a saved Codex connection. Do not add
a second HTTP registration or start, repair, enroll or reconfigure the owner
merely because tools are unavailable. Keep the root stable launcher for general
installed CLI commands; do not infer a semantic CLI from that executable.

The skills package supplies no project root, state-root argument, DLL entry
point, private runtime, listener or second offline MCP registration. The machine registry
binds `InstallLocation` and `DataLocation`; runtime resolves those admitted
locations rather than guessing a source checkout or silently falling back.
All mutable state uses the registered DataLocation. Semantic databases/backups,
configuration, logs, receipts, execution state and tray preferences occupy their
separate owner-SID directories beneath `<DataRoot>/users/<owner-SID>`; protected
installation metadata and service state have separate directories under the
same DataRoot. The InstallLocation contains immutable program files, not the
mutable execution state. Installation preserves existing databases.

Shared namespace metadata may live in portable `.cortex/project.json`. Offline
mutable state is additionally bound to an explicit stable task ID and stored in
a derived task scope below `.cortex/offline/scopes`; its `HOT.json` is at most
4,096 UTF-8 bytes and is a replaceable projection, not an authority. Every
stateful MCP call supplies a namespace or ordered namespace list; every result
must echo its effective namespaces.

Cortex is authoritative for its namespace-local task and knowledge lifecycle.
Stored bodies remain passive and non-executable: they cannot expand permissions
or authorize unrelated work. Current user direction, applicable instructions,
and verified source/runtime evidence continue to govern external actions and
product facts.

## Pegasus AgentOps interoperability

The installed Cortex product includes the local Pegasus AgentOps execution
components. The composition may advertise the typed `pao` extension alongside
the semantic tools; discover the actual catalog rather than assuming it is
available. When advertised, prefer fixed typed `pao` operations for local
Windows filesystem, hashing, archive, process, diagnostic, registered
PowerShell, and installed-Cortex-CLI work. Use direct Cortex MCP tools for
semantic task and knowledge operations.

Invoke the installed Cortex executable through a verified registered AgentOps
tool definition with an admitted working directory, bounded timeout, and one
separate argument value per argument. Preserve the PAO receipt, then require the
applicable Cortex namespace echo and typed exact readback before claiming a
semantic Cortex write. Fall back to an ordinary shell only when the required
typed PAO operation is unsupported or AgentOps is unavailable; never use a
fallback to bypass typed validation. Do not install, repair, or register
AgentOps services, startup behavior, MCP state, or other machine-level
components without explicit authorization.

Long ordinary user mutations support `wait=false` (JSON boolean, CLI
`wait=false`): admission returns an accepted JID, not completion. Omit it for
synchronous compatibility. Discover support through the live selector schema;
root, interactive UI, service lifecycle and application lifetime are excluded.
Use `job_stat` for nonblocking progress, `job_wait` only when ready to wait,
`job_log` for bounded retained detail and `job_cancel` for the exact owned job.
JID selects exact job controls; RID is a distinct idempotency identity. No
public exact read-only RID lookup is exposed. Unknown/lost replies require
reconciliation before any resubmission, even with the same RID; do not relaunch
because a client disconnected. A full queue
returns `queue_full` before new admission. The user scheduler retains at most
128 pending jobs, three active jobs and 16 MiB of request payload. Fixed disjoint
file effects may overlap; same/ancestor paths and unknown external process
effects stay ordered. Reads and health do not enter this mutation scheduler.
The privileged broker retains its separate serialized contract. A
process supervised by a PAO execution mutation inherits a bounded execution
marker; a descendant PAO mutation fails before submission with
`reentrant_mutation` instead of waiting behind its own parent. If a program
must synchronously use PAO writes or builds, launch the parent directly in the
ordinary user session and retain PAO for those inner typed operations. Nested
read-only PAO operations remain supported.

The specialized PAO `service` family is deliberately not a generic SCM client.
`service_status`, `service_start`, `service_stop`, and
`service_restart` admit only the exact `PegasusCerebral` service through the
root-authorized LocalSystem broker. Status provides bounded state plus the
running PID/start identity. Stop and restart require that exact identity;
start requires `expect=stopped`; mutation timeouts are 1-120 seconds. No caller
can supply an executable, command line, machine, or different service name,
and Cortex's own service is outside this route. Root material is stripped
before durable queueing and never belongs in receipts or Cortex records.

The separate `windows_service_<action>` family supports bounded local service
inspection and control with exact target/preimage checks. Its explicit Root and
same-owner UAC routes are independent; neither is an automatic fallback, and
startup configuration does not start or stop a service. Use current selected
operation help for the exact body and route. Ordinary owner-Host operations do
not require the tray window to stay open. Local `terminal_list` and the other
`terminal_*` operations retain shells only for that owner's lifetime; an MCP
disconnect can be reattached, while Host restart or reboot does not restore a
shell. PowerShell 7 is the provisioned terminal prerequisite.

The current fixed PAO catalog has no HTTP/page-request or download operation.
Its `net_list` selector inspects local listeners; it does not fetch a URL.
There is no supported PAO HTTP schema/version to select in this source
contract. A consumer-owned HTTP provider is a separate integration, not an
implicit PAO route. Do not register a new HTTP client, invent an operation, or
substitute a shell recipe under the current audit scope.

## Central MCP surface

The V2 semantic MCP surface has eight tools. A resident composition may also
advertise optional user PAO; discover that extension separately:

| Tool | Use | Result boundary |
| --- | --- | --- |
| `cortex_status` | Product, version, store, bounded health, process retrieval counters, and optional durable utilization summary | Metrics are content-free; instrumented coverage only |
| `cortex_namespaces` | Bounded, paginated namespace inventory | Namespace names are not grants |
| `cortex_start` | Exact current task plus ranked task-start projection | Optional exact task/phase ID, compact format, ordered explicit namespaces, intent-bound cursor, 4 KiB default/8 KiB maximum |
| `cortex_search` | Bounded task and knowledge search | Compact typed previews; paginated |
| `cortex_read` | Exact task or knowledge revision | One typed coordinate, or an ordered batch of 1-8 unique coordinates |
| `cortex_task` | Full namespace-local task lifecycle | Compact summary/receipt; body via `cortex_read` |
| `cortex_knowledge` | Typed local learning lifecycle and read-only consolidation analysis | Compact receipt; body via `cortex_read`; analysis never mutates |
| `cortex_finish` | Atomic task transition plus learning closeout | Compact task/knowledge receipts |

Search without a knowledge-kind filter may return tasks and knowledge. A kind
filter searches knowledge only. Archived tasks and archived or superseded
knowledge are not active search hits. Writes require stable idempotency keys and
expected-revision concurrency where applicable. Content supports valid MCP JSON
text including CRLF/LF, tabs, Unicode, multiline Markdown, code blocks, and
escaped controls; identifiers are restricted separately.

At the beginning of a meaningful Codex task, call `cortex_start` once with the
exact `CODEX_THREAD_ID` as optional `taskId`, the current intent, ordered
namespaces, a 4,096-byte budget, and `format=compact`. Add a short stable
`phaseId` when one Codex task enters a materially different work phase. The result contains at most one summary for the exact
`currentTask` identity plus four compact ranked items. If the current task is absent,
create it with the exact task ID. Compatibility callers may omit `taskId` and
still receive intent-ranked context. Exact replay may return `unchanged`; a
changed normalized intent, task ID, namespace order, selection version, or
namespace revision produces a new cursor. Full record bodies are never part of
startup context and remain available through typed `cortex_read`.

Compact startup and search items are previews. Exact-read only the coordinates
that can affect the decision; a single `cortex_read` request may batch one
through eight unique task or knowledge coordinates and returns their full
bodies in request order. The agent keeps a bounded in-context hydration map
from `namespace + recordType + recordId` to the loaded revision. Reuse a body
when the preview revision is unchanged, reread when it is newer, and mark a
revision only after the exact typed read succeeds. If context compaction or a
handoff loses the body, discard the marker. Cortex deliberately stores no read
receipt, sticky hydration state, or search exclusion list.

Cache a successful start for the continuously active task and reuse it across
incremental messages, status or note updates, tool calls, and small checkpoints.
Registration and a new conversational turn do not require another load/save
cycle. Refresh only after a material task or project identity, namespace
selection/order, server identity/version, guidance version,
connection/recovery change, or when newer retained context can affect a
decision. If initial access fails when retained context matters, make one
bounded retry or recovery attempt, never a loop, then continue from applicable
local authority when permitted. Skip retrieval for immediate corrections,
routine send/receive, formatting or mechanical work, repeated verification, or
whenever retained context cannot affect the decision. Use `cortex_finish` only
at a meaningful checkpoint, blocker, completion, or handoff and aggregate
related evidence.

## Exact reads and unavailable tools

`cortex_task get` returns a compact task summary/excerpt. To obtain the full
body, select a task from startup/search (or an already verified exact task
binding) and call `cortex_read`, not `cortex_task get` repeatedly. Arguments for
one selected task revision are:

```json
{"namespace":"project.example","recordType":"task","recordId":"<exact-task-id>","revision":87}
```

Use the actual returned revision; 87 is an example. Omit `revision` to request
the current head. For knowledge, use `recordType=knowledge` and its exact record
ID. Batch form is a different argument shape, not a mixture with single-record
fields:

```json
{"records":[{"namespace":"project.example","recordType":"task","recordId":"<exact-task-id>","revision":87},{"namespace":"knowledge.shared","recordType":"knowledge","recordId":"<selected-record-id>"}],"maxUtf8Bytes":65536}
```

There must be 1-8 unique typed coordinates. Do not send `format`; the published
read schema does not advertise it. The source exact-read budget is
512..262144 UTF-8 bytes (default 262144). A body that does not fit fails rather
than being shortened; raise the explicit budget within the bound or reduce
the batch. Single output is `structuredContent.record`; batch bodies are
`structuredContent.items[n].record`, in request order. Check echoed namespaces,
IDs, revisions, and the full body.

If the current session exposes no semantic tools, discover/retry once when
relevant and continue from applicable local project authority when allowed.
An installed PAO CLI and saved MCP configuration are not a central task-read
route. Current product CLI dispatch has no public generic task/knowledge
exact-read command. `offline read --frame-id` reads the current task's offline
frame, while `offline refresh` produces a bounded projection; neither replaces
`cortex_read`. Do not guess pipe/CLI operations, open the central database, or
repair registration/services to retrieve context.

An already authorized coordinator with a healthy semantic connection can relay
a bounded exact read with its namespace, type, ID, revision, and provenance.
Keep coordinator-reported evidence distinct from a direct local read. If no
such route is available, state the specific missing context and pending
central checkpoint; do not fabricate a `cortex_finish` or edit generated HOT.

## Support and evidence limits

Use the live `cortex_status` schema for semantic health. It separates
process-local retrieval counters from an optional durable utilization summary;
the existing `PegasusCortex.exe metrics --days N` report supports 1..90 days.
Capacity/store status, utilization aggregates, and consumer readiness answer
different questions. CLI status is not an authenticated native hello. There is
no V2 guidance/help resource or dedicated support-bundle operation in the
current semantic surface; do not invent a guide URI or help action.

Utilization covers instrumented processes/calls only. Response bytes are not
token counts; task IDs are caller declarations, not authenticated agent
identities. Direct HOT-file consumption, usefulness, and earlier uninstrumented
activity remain unknown. Opening a dashboard or running an operator diagnostic
must remain within current authority; status availability is not permission to
initialize, migrate, repair, restart, or collect record bodies.

Source/static inspection, fixture or unit tests, real pipe integration,
package/signature verification, installed runtime, and consumer acceptance are
separate evidence levels. Native/2/MCP multiplexing must be observed on the
configured live connection; source support alone does not prove consumer adoption.
Installed legacy guidance resources likewise do not create a V2 resource route.

## Precedence and learning

Use this retrieval precedence:

1. Current user direction, applicable instructions, and verified source/runtime
   evidence.
2. The exact current task, when supplied.
3. Relevant explicit user corrections and preferences.
4. Relevant blocked, claimed, assigned, or open tasks.
5. Verified failures, regressions, and workarounds.
6. Verified decisions and observations.
7. Inferred observations.
8. Shared tenets.

Learning tiers are `explicit`, `verified`, and `inferred`. Use `source=user`
only for actual user-supplied material, `source=imported` for curated legacy or
package material, and `source=agent` for agent-authored findings. Report
conflicts; never silently let a lower-precedence record override current
evidence.

Local ingest is immediate within the explicit namespace. Collective publication
is a different lifecycle and does not gate, hide, quarantine, or rewrite the
local original.

Use task `phaseId` and `checkpointSubject` to distinguish meaningful phases and
checkpoints within a stable Codex task ID. Use `cortex_knowledge analyze` for a
bounded deterministic list of exact-duplicate or title-review consolidation
suggestions. Analysis is read-only and content-free; a reviewed consolidation
still uses ordinary explicit expected-revision lifecycle operations.

Use `project.<id>` for project-dependent tasks and evidence,
`knowledge.shared` for generalized cross-agent knowledge, and `knowledge.user`
for this user's durable interaction style, corrections, and tendencies.
Programming and other subjects use tags inside the one shared geometric space.
Other agents gain access only by explicitly including a space; there is no
sticky global namespace, and excluded spaces cannot influence ranking through
the geometric layer. Compact search/start results remain body-free previews;
use the returned type, record ID, and revision for an exact read. The old
`shared.general`, `shared.programming`, and `user.preferences` names are
accepted only as one-hop compatibility inputs and echo canonical names.

## Optional personal profile

The tray selects the signed-in user's optional personal context. Core shared,
project, and task operations do not depend on tray availability. A fixed local
read-only version-1 pipe reports only state, display label, generation, and an
issue code; it neither transfers records nor selects arbitrary database paths.
The current slice preserves the existing database and its directory layout.

The client attaches only to the already-running signed-in host. A missing pipe
does not launch it or trigger a startup retry. Cortex may intentionally be OFF
or in maintenance. A `profile_pipe_unavailable` or `ui_unavailable` result is
an availability condition,
not evidence that profile data is absent. Profile status explicitly reports
`indicatesMissingData=false`, `remapRequired=false`, and whether the state is
retryable. Never generate, copy, seed, or remap a database in response to these
availability codes.
Do not wake Cortex, repair registration, change service startup, clear a barrier,
or loop reconnects to make tools reappear. Use applicable bounded local files
when allowed. After explicit user startup, a fresh relevant read can observe
the new profile generation; old cached personal bodies/cursors stay invalid.

`cortex_status` independently reports `personalProfile`. Mixed start/search
requests omit unavailable `knowledge.user` (including its compatibility alias)
and echo actual effective namespaces. Direct personal reads/writes fail with
`personal_profile_not_loaded`; mixed exact-read batches fail before any hydration.
A personal-generation change invalidates personal-sensitive cursors and suppresses
stale in-flight read results. Writes admitted while loaded finish their original
transaction and return normal receipts; unload affects subsequent admission.

When notified of a profile change, agents discard cached personal context and
refresh only when needed, without discarding project/shared continuity. The server
cannot erase a body already delivered to a client. Do not use old personal HOT
items to circumvent an explicitly unloaded profile. Tray recovery works with
the existing MCP process. Transition-only `personal-profile.log` diagnostics
retain at most a current file and one previous file, without record bodies.

## Installed offline CLI

Use the stable installed executable directly:

```powershell
$CortexExe = "$env:ProgramFiles\Pegasus\Cortex\PegasusCortex.exe"
$ProjectRoot = 'C:\absolute\canonical\project'
$TaskId = [string]$env:CODEX_THREAD_ID
if ([string]::IsNullOrWhiteSpace($TaskId)) {
    throw 'The host did not expose CODEX_THREAD_ID; use central MCP and skip offline file Cortex.'
}
```

Use the exact host-provided stable ID for the fresh Codex task. In Codex, prefer
the exact `CODEX_THREAD_ID` environment value when present. It must remain
stable across command invocations and must not be a display name, task title,
project ID, process ID, or caller-generated replacement. If no stable task ID
is available, fail closed for offline commands. It must already be canonical
lowercase portable ASCII; Cortex rejects uppercase instead of silently
normalizing or merging identities.

Initialize an explicitly authorized project once. The write namespace is also
included in the ordered read set; repeat `--read-namespace` for additional
namespaces:

```powershell
& $CortexExe offline init `
  --root $ProjectRoot `
  --task-id $TaskId `
  --project-id 'project-short-id' `
  --write-namespace 'project.example' `
  --read-namespace 'knowledge.shared'
```

For a root that reports an unassigned legacy project-level registration,
archive the exact root `.cortex/HOT.json` and complete unscoped
`.cortex/offline` directory first. Use PAO exact-path move/copy operations with
durable receipts and hash verification; never enumerate or derive task scope
directories. Then preserve the legacy catalog evidence while adding the exact
current task binding:

```powershell
& $CortexExe offline migrate-legacy `
  --root $ProjectRoot `
  --task-id $TaskId `
  --project-id 'project-short-id' `
  --write-namespace 'project.example' `
  --read-namespace 'knowledge.shared'
```

The migration command requires the legacy catalog binding to match those
project and namespace values exactly. It keeps the `legacy-unassigned` entry,
adds only the requested task/root entry, and never reads, ingests, or assigns a
legacy HOT or frame body. Root-level legacy files, noncanonical or reparse
paths, binding conflicts, and sibling task/root conflicts fail closed. Exact
replay returns the existing binding without rewriting the catalog. Catalog
capacity is preflighted before task-scope initialization. If the catalog commit
later fails after an exact empty scope was initialized, resolve the catalog
failure and replay only the same canonical root, task ID, and binding; no
legacy content was assigned, and divergent replay remains rejected.

Capture one strict UTF-8 content file. The CLI preserves its exact valid text;
do not put the body in a command argument:

```powershell
& $CortexExe offline capture `
  --root $ProjectRoot `
  --task-id $TaskId `
  --record-id 'correction.example.20260813' `
  --kind 'correction' `
  --tier 'explicit' `
  --title 'Bounded durable correction' `
  --content-file 'C:\absolute\candidate.md' `
  --tag 'offline' `
  --source 'user' `
  --source-id 'user-correction-20260813' `
  --evidence 'Direct user correction'
```

Inspect and reconcile:

```powershell
& $CortexExe offline status  --root $ProjectRoot --task-id $TaskId
& $CortexExe offline search  --root $ProjectRoot --task-id $TaskId --text 'durable correction' --limit '10'
& $CortexExe offline read    --root $ProjectRoot --task-id $TaskId --frame-id 'of_...'
& $CortexExe offline ingest  --root $ProjectRoot --task-id $TaskId --frame-id 'of_...'
& $CortexExe offline refresh --root $ProjectRoot --task-id $TaskId
& $CortexExe warmup
& $CortexExe backup
```

Omit `--frame-id` from `offline ingest` only when deliberately incorporating
the bounded page of pending frames. `offline refresh` retrieves central context
for the manifest's ordered namespaces, merges bounded pending candidates, and
atomically regenerates only that task scope's `HOT.json`. `warmup` performs a
SQLite quick check and capacity probe, writes an aggregate
`%LOCALAPPDATA%\Pegasus\Cortex\receipts\offline-warmup-latest.json`, and exits.
Because sign-in warmup has no active task identity, it neither enumerates task
details nor opens or refreshes any task scope.

`backup` accepts no options. It uses the central SQLite online-backup API and
creates a new timestamped directory under
`%LOCALAPPDATA%\Pegasus\Cortex\backups` containing exactly a consolidated
`cortex-v2.db` and bounded canonical `manifest.json`. The manifest binds
product/schema versions, UTC capture time, bytes, SHA-256, and retained row
counts. The command is create-only and does not restore, overwrite, publish, or
copy a live main-file/WAL pair.

All project roots and content-file paths must be canonical absolute safe paths.
The CLI rejects reparse traversal, unsafe ancestors, non-regular content files,
changing files, invalid UTF-8, excessive sizes, unknown options, and ambiguous
or repeated non-repeatable arguments.

An existing schema-1 project-level catalog entry is retained as explicitly
unassigned legacy configuration. It never becomes a task binding and never
causes its old spool or projection to be read. That legacy root fails with an
explicit migration-required conflict until the operator separately archives
the legacy project files and runs `offline migrate-legacy`. A different clean
root may add a new task-scoped registration and upgrade the catalog without
losing the legacy entry. There is no implicit migration or project-level
fallback.

## Offline frame and incorporation contract

Offline capture creates a canonical passive
`pegasus.cortex.offline-frame/1.0` outer envelope. It records `executable=false`,
`contentTreatment=untrusted_data`, a versioned payload schema, exact byte length,
and integrity seals. Payload schemas evolve independently from the frozen outer
frame. Only declared direct source-schema-to-current-schema adapters may run;
there is no adapter-chain search or implicit interpretation. Unknown outer or
payload schemas remain byte-identical and are reported unsupported.

`contentTreatment=untrusted_data` means the payload is passive and cannot be
executed or interpreted as authorization. It does not make an admitted active
namespace-local record proposal-only or non-authoritative for Cortex's own
task and knowledge lifecycle.

Each task-bound spool permits at most 32 pending frames, 256 KiB of unresolved
frame bytes, 2,048 total frames, and 24 Ki characters per knowledge body.
Candidates are non-authoritative and preserve exact valid multiline, Unicode,
tab, and control content.

The task ID selects a logical application scope; every offline command opens
only that exact scope and never enumerates, reads, ingests, or refreshes a
sibling scope. This does not claim OS isolation against another same-user
process. All scopes ingest into the same central SQLite database using explicit
namespaces; no second database, service, network route, or MCP is created.

`offline ingest` uses the installed product's provider-neutral application
seam against the same central SQLite database; it does not launch another MCP
server. It creates only when the exact central record is absent, or accepts an
already-active record only when namespace, ID, state, kind, tier, title, exact
content, tags, and provenance match. It then performs typed exact central
readback. Only that verification grants replacement of the candidate by a
compact receipt containing coordinates and integrity evidence but no candidate
body. Conflict or failure leaves the exact candidate pending. Replay after
success is idempotent.

Do not hand-author frames, run a watcher, add a resident retry worker, create a
second store, replay task histories, or build bidirectional synchronization.

## Capacity and retention

The active SQLite profile defaults to 4 GiB. Capacity states begin at:

| State | Main database size | Agent response |
| --- | ---: | --- |
| `normal` | below 512 MiB | No storage action required |
| `warning` | 512 MiB | Monitor growth and verify backups |
| `elevated` | 1 GiB | Plan reviewed retention maintenance |
| `maintenance` | 2 GiB | Perform reviewed maintenance before the ceiling |
| `ceiling` | configured maximum | Expect writes to fail; stop and surface the condition |

`status`, `doctor`, and `warmup` expose page, WAL, freelist, utilization, state,
and recommendation telemetry without a corpus scan. The active profile is a
working-set bound, not a product-lifetime or collective-corpus limit. Do not
auto-delete, auto-shard, or change the configured maximum.

The current source also has explicit optional local cold-payload, protection,
rotation and paged-rehydration controls. Those owner controls are distinct from
the eight semantic MCP tools and do not imply automatic migration, deletion or
that a particular installed owner has enabled them. Cross-machine cold-history
packs, multipart exports and collective publication remain separate lifecycles.
Use the release's storage guide and actual owner status for the admitted policy.

## Behavior packs and legacy fallback

A portable behavior pack may contain generalized tenets, explicit corrections
and preferences, and verified workarounds. Exclude raw chats, private reasoning,
credentials, machine diagnostics, and project task history by default. Use a
declared compatible package's deterministic importer rather than converting it
through offline prose.

Product packages bind their portable packs through the versioned starter-corpus
manifest. During admitted owner semantic initialization (including the existing
sign-in warmup route), Cortex seeds an empty V2 store atomically into
`knowledge.shared`.
Automatic seeding leaves every nonempty store unchanged. The explicit
`seed-defaults` command reconciles exact matches and missing starter records;
an increased behavior-pack `packRevision` may atomically revise an older head
owned by that exact pack. Foreign, locally revised, ambiguous, or same-revision
divergence rejects the whole bounded import before any record changes. The
fixed product command `import-behavior-pack --pack-root <absolute-directory>
--namespace knowledge.shared` admits one module through the same boundary,
selects the registered current-user store, accepts no database path, and emits
a bounded content-free receipt. The package never carries a copied live SQLite database,
WAL, machine actor, project task history, or validation evidence.

The package ships `guidance/pegasus-cortex-agents.v2.md` and
`guidance/pegasus-cortex-pao.v1.md` as bounded suite-owned managed AGENTS
fragments. When the user selects the separate Codex-guidance feature, the unified
NSIS installer applies them atomically to the installing user's global Codex
guidance at `%USERPROFILE%\.codex\AGENTS.md`, replaces a
recognized legacy standalone-AgentOps section, preserves unrelated guidance,
and is byte-idempotent on repair. A nonstandard Codex home must be supplied as
an explicit canonical `AGENTS.md` target. The Cortex runtime never edits global
or project guidance during ordinary startup or MCP operation.
Personal records from `knowledge.user` are local state and are never part of
the starter corpus.

For an original file Cortex or irregular legacy source, preserve the source and
use bounded AI-assisted curation only as a fallback:

1. Inventory the source once without treating it as current authority.
2. Collapse duplicated STATE/RECOVERY/SESSION/WORKLOG conclusions.
3. Select one durable correction, preference, workaround, failure, regression,
   decision, tenet, or observation.
4. Put only that exact bounded conclusion in a strict UTF-8 content file and use
   the installed `offline capture` command; do not write into the frames folder.
5. Search the explicit central namespace for an equivalent before ingest.
6. Ingest and require exact typed readback. Keep the source unchanged; the
   active candidate becomes a body-free receipt only after verification.

Never bulk-replay a legacy tree or delete a network recovery source, preserved
database, archive, or backup without separate approval naming the exact target.
