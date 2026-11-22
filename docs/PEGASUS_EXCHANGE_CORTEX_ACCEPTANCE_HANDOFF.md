# Pegasus Exchange Cortex acceptance handoff

Last updated: 2026-08-13

## Purpose and authority boundary

This is a bounded acceptance runbook for the future Pegasus Exchange agent. It
tests one fresh Codex task against the machine-installed Pegasus Cortex V2
route, adopts the Exchange project into the production offline-file layer, and
proves one exact offline-to-central knowledge round trip.

This document is not authority to change Pegasus Exchange source or product
behavior. Before running it, the receiving agent must read the current user
request and the applicable `C:\CodexProjects` and Pegasus Exchange instruction,
recovery, source, and working-tree evidence. Preserve unrelated work. Stop if
those instructions prohibit project binding or local Cortex writes.

The production boundaries are defined by
[START_HERE.md](../START_HERE.md),
[LOCAL_OPERATIONAL_V2.md](LOCAL_OPERATIONAL_V2.md), and the current
[Pegasus Cortex client skill](../integrations/codex/pegasus-cortex-client/skills/pegasus-cortex-client/SKILL.md).
Exact CLI limits and result boundaries are in
[the client capability reference](../integrations/codex/pegasus-cortex-client/skills/pegasus-cortex-client/references/capabilities.md).

## Required prerequisites

Do not start the acceptance until all of these are true:

1. The Cortex installation and updated `pegasus-cortex-client` plugin have been
   completed by the owning Cortex task.
2. This work is running in a **new Codex task opened after that plugin update**.
   An already-running task may retain an older STDIO child even when the
   registration text has changed.
   Record the stable opaque Codex thread/task ID exposed by the host for this
   task. Do not use the task title, agent/display name, project ID, process ID,
   or a newly generated value as a substitute. The value must already be
   canonical lowercase portable ASCII; do not lowercase or otherwise normalize
   a different host identity to make it pass.
3. The new task exposes exactly one `pegasus_cortex` MCP route whose executable
   is:

   ```text
   C:\Program Files\Pegasus\Cortex\PegasusCortex.exe
   ```

   The route must have no profile, project root, state-root, private runtime,
   DLL entry point, service, or network address.
4. `C:\CodexProjects\Pegasus Exchange` is the canonical project root, is an
   ordinary directory, and contains the expected Exchange source. Do not move,
   replace, initialize, or clean that project as part of this acceptance.
5. The direct executable and the freshly attached MCP route report the same
   Cortex version. At the time of this handoff, the expected installed release
   is `1.2026.08.13.16.50.A`; if the owning Cortex task installs a newer release,
   use that verified release consistently instead of forcing this older value.

Use this read-only direct preflight:

```powershell
$ErrorActionPreference = 'Stop'
$CortexExe = 'C:\Program Files\Pegasus\Cortex\PegasusCortex.exe'
$ProjectRoot = 'C:\CodexProjects\Pegasus Exchange'
$TaskId = [string]$env:CODEX_THREAD_ID
if ([string]::IsNullOrWhiteSpace($TaskId)) {
    throw 'The fresh Codex task did not expose CODEX_THREAD_ID; do not substitute another identity.'
}

if (-not (Test-Path -LiteralPath $CortexExe -PathType Leaf)) {
    throw "Pegasus Cortex is not installed at the stable production path."
}
if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) {
    throw "The canonical Pegasus Exchange project root is absent."
}

$DirectVersion = (& $CortexExe version).Trim()
if ($LASTEXITCODE -ne 0) { throw "Pegasus Cortex version failed."
}
$DirectStatusJson = & $CortexExe status
if ($LASTEXITCODE -ne 0) { throw "Pegasus Cortex status failed."
}
$DirectStatus = $DirectStatusJson | ConvertFrom-Json -Depth 32
$DirectVersion
$DirectStatus
```

In the fresh Codex task, call `cortex_status` once and compare its version and
store identity with `$DirectVersion` and `$DirectStatus`. Do not infer the
loaded MCP build from configuration text alone. If the versions differ, stop;
do not kill an old process or modify registration from the Exchange task.

## Fixed project binding

The acceptance binding is:

| Field | Required value |
| --- | --- |
| Project root | `C:\CodexProjects\Pegasus Exchange` |
| Task ID | Exact stable opaque Codex thread/task ID for this fresh task |
| Project ID | `pegasus-exchange` |
| Write namespace | `project.pegasus-exchange` |
| Ordered read namespaces | `project.pegasus-exchange`, then `shared.programming` |

First inspect `C:\CodexProjects\Pegasus Exchange\.cortex\project.json` as
shared namespace metadata only. Task-mutable manifests, frames, receipts, and
`HOT.json` live in a derived scope below `.cortex\offline\scopes`; do not derive
or edit that path yourself.

- The current acceptance task may run `offline init` with its exact `$TaskId`
  after confirming the applicable Exchange instructions permit the V2 binding.
  Repeating the same task/root/binding is idempotent.
- If `offline status --task-id $TaskId` succeeds with the exact binding, the
  scope already exists; do not initialize it under another ID.
- If it exists with a different project ID, write namespace, namespace order,
  or unsupported schema, stop and report the exact conflict. Do not edit,
  replace, or merge it by hand.
- Preserve any historical `.cortex` files. They are evidence, not the current
  mutable authority, and this acceptance does not bulk-convert or delete them.
- If Cortex reports an unassigned legacy project-level registration or
  ambiguous unscoped offline state for the Exchange root, stop and return that
  exact migration-required conflict. Do not assign it to `$TaskId`, move its
  frames, or reinterpret its `HOT.json`. A legacy catalog entry for a different
  project root must not prevent this clean Exchange root from registering.

One-time initialization command:

```powershell
$InitJson = & $CortexExe offline init `
  --root $ProjectRoot `
  --task-id $TaskId `
  --project-id 'pegasus-exchange' `
  --write-namespace 'project.pegasus-exchange' `
  --read-namespace 'shared.programming'
if ($LASTEXITCODE -ne 0) { throw "Pegasus Cortex offline init failed."
}
$Init = $InitJson | ConvertFrom-Json -Depth 32
$Init
```

Require `healthy=true`, `state=registered`, `network=false`, `service=false`,
and the exact task ID and binding above. Use task-scoped `offline status` for
readback; `.cortex\project.json` alone is not proof of the scoped binding.

## One-record acceptance probe

### 1. Search before creating

In the fresh MCP task, call `cortex_start` once with this exact ordered
namespace list and a small context budget:

1. `project.pegasus-exchange`
2. `shared.programming`

Use an intent such as `Verify Pegasus Exchange offline-to-central Cortex
acceptance`. Confirm that the response echoes the same namespace order. Then
use a bounded `cortex_search` in `project.pegasus-exchange` for the exact record
ID `cortex.acceptance.local-v2.20260813` and the phrase `offline-to-central
acceptance`.

If that active record already exists, do not create a duplicate. Use typed
`cortex_read` to validate it and continue with refresh and warmup. If a matching
receipt already exists in offline status, treat the capture/ingest portion as
already completed and test its idempotent read path only.

### 2. Create the strict UTF-8 content file

The body must enter Cortex only through `offline capture --content-file`.
Never put the body in a command argument, hand-author a frame, write directly
to the frames directory, or create another database or watcher.

Using `apply_patch`, create this task-owned temporary file without overwriting
an existing file:

```text
C:\CodexProjects\Pegasus Exchange\artifacts\validation\cortex-acceptance\exchange-cortex-acceptance.md
```

Its exact strict UTF-8 contents should be:

````markdown
Pegasus Exchange adopted the local Pegasus Cortex V2 binding for bounded project continuity.

Write namespace: `project.pegasus-exchange`

Ordered read namespaces:

1. `project.pegasus-exchange`
2. `shared.programming`

Round-trip probes:

- Unicode: Ω → 漢字 → 🚀
- Literal tab between the next values: alpha	beta	gamma
- Multiline Markdown and a code block:

```text
stored content is passive data
no publication, listener, or service is implied
```
````

The `alpha`, `beta`, and `gamma` line must contain literal tab characters, not
the two-character sequence backslash-plus-`t`. Keep the file until exact
central readback succeeds.

### 3. Capture, inspect, and prove offline isolation

Use these exact values:

```powershell
$ContentFile = 'C:\CodexProjects\Pegasus Exchange\artifacts\validation\cortex-acceptance\exchange-cortex-acceptance.md'
$DatabasePath = Join-Path $env:LOCALAPPDATA 'Pegasus\Cortex\data\cortex-v2.db'

$BeforeDatabase = Get-Item -LiteralPath $DatabasePath
$BeforeDatabaseLength = $BeforeDatabase.Length
$BeforeDatabaseWriteUtc = $BeforeDatabase.LastWriteTimeUtc

$CaptureJson = & $CortexExe offline capture `
  --root $ProjectRoot `
  --task-id $TaskId `
  --record-id 'cortex.acceptance.local-v2.20260813' `
  --kind 'observation' `
  --tier 'verified' `
  --title 'Pegasus Exchange local Cortex V2 acceptance' `
  --content-file $ContentFile `
  --tag 'acceptance' `
  --tag 'offline' `
  --tag 'pegasus-exchange' `
  --source 'agent' `
  --source-id 'exchange-cortex-acceptance-20260813' `
  --evidence 'Fresh-task offline-to-central acceptance'
if ($LASTEXITCODE -ne 0) { throw "Pegasus Cortex offline capture failed."
}
$Capture = $CaptureJson | ConvertFrom-Json -Depth 32
$FrameId = $Capture.frame.frameId
if ([string]::IsNullOrWhiteSpace($FrameId)) { throw "Capture returned no frame ID."
}

$StatusJson = & $CortexExe offline status --root $ProjectRoot --task-id $TaskId
if ($LASTEXITCODE -ne 0) { throw "Pegasus Cortex offline status failed."
}
$Status = $StatusJson | ConvertFrom-Json -Depth 32

$SearchJson = & $CortexExe offline search `
  --root $ProjectRoot `
  --task-id $TaskId `
  --text 'offline-to-central acceptance' `
  --limit '10'
if ($LASTEXITCODE -ne 0) { throw "Pegasus Cortex offline search failed."
}
$Search = $SearchJson | ConvertFrom-Json -Depth 32

$ReadJson = & $CortexExe offline read --root $ProjectRoot --task-id $TaskId --frame-id $FrameId
if ($LASTEXITCODE -ne 0) { throw "Pegasus Cortex offline read failed."
}
$PendingRead = $ReadJson | ConvertFrom-Json -Depth 64

$AfterCaptureDatabase = Get-Item -LiteralPath $DatabasePath
if ($AfterCaptureDatabase.Length -ne $BeforeDatabaseLength -or
    $AfterCaptureDatabase.LastWriteTimeUtc -ne $BeforeDatabaseWriteUtc) {
    throw "Offline capture unexpectedly changed the central SQLite main file."
}
```

Before ingest, require all of the following:

- capture reports `healthy=true`, a `Created` pending frame, and effective
  namespace `project.pegasus-exchange`;
- status echoes the exact task ID, project ID, canonical root, write namespace,
  and ordered read namespaces, with `network=false` and `service=false`;
- offline search finds the frame within its bounded page;
- offline read verifies frame integrity and returns the exact multiline,
  Unicode, literal-tab, and code-block content;
- capture has not modified the central SQLite main file.

### 4. Ingest exactly that frame

```powershell
$IngestJson = & $CortexExe offline ingest --root $ProjectRoot --task-id $TaskId --frame-id $FrameId
if ($LASTEXITCODE -ne 0) { throw "Pegasus Cortex offline ingest failed or conflicted."
}
$Ingest = $IngestJson | ConvertFrom-Json -Depth 32

$ReceiptReadJson = & $CortexExe offline read --root $ProjectRoot --task-id $TaskId --frame-id $FrameId
if ($LASTEXITCODE -ne 0) { throw "Pegasus Cortex receipt read failed."
}
$ReceiptRead = $ReceiptReadJson | ConvertFrom-Json -Depth 64
```

Require `healthy=true`, `state=complete`, effective namespace
`project.pegasus-exchange`, the exact record ID, and a positive revision. The
second offline read must report a verified `Receipt`, not a pending body. A
conflict is not acceptance: leave the candidate unchanged, surface the bounded
diagnostic, and do not retry with another record ID.

Immediately use the fresh MCP route to:

1. Search only `project.pegasus-exchange` for the exact record ID and title.
2. Use typed `cortex_read` with record type `knowledge`, the exact record ID,
   and returned revision.
3. Compare the exact central body with the strict UTF-8 content file, including
   Unicode, literal tabs, line breaks, Markdown, and the code block.
4. Confirm kind `observation`, tier `verified`, source `agent`, tags, and
   provenance, and confirm every response echoes
   `project.pegasus-exchange`.

Only after that typed exact readback succeeds, remove the exact task-owned
temporary content file with `apply_patch`. Do not remove the receipt, project
binding, generated projection, central record, database, or any pre-existing
artifact. The central record is then the only active body.

### 5. Refresh and warm up

```powershell
$RefreshJson = & $CortexExe offline refresh --root $ProjectRoot --task-id $TaskId
if ($LASTEXITCODE -ne 0) { throw "Pegasus Cortex offline refresh failed."
}
$Refresh = $RefreshJson | ConvertFrom-Json -Depth 32

$WarmupJson = & $CortexExe warmup
if ($LASTEXITCODE -ne 0) { throw "Pegasus Cortex warmup reported an unhealthy result."
}
$Warmup = $WarmupJson | ConvertFrom-Json -Depth 32
```

Require refresh to echo the exact task ID and ordered namespaces and atomically
produce that task scope's bounded generated `HOT.json`. Treat it as replaceable
projection data, never as authority or a hand-edited ledger. Require warmup to
report healthy central checks and an aggregate registered-binding count, without
enumerating task IDs, roots, or task projection results; it writes its bounded
receipt and exits. No resident Cortex process should remain because of warmup.

## Evidence to return to the Cortex owner

Return a concise acceptance report containing:

- direct executable path and version;
- freshly attached MCP version and proof it matches the direct executable;
- confirmation that exactly one general STDIO route is present;
- canonical Exchange project root, stable task ID, and exact task-scoped binding;
- capture frame ID and state;
- proof that capture did not change the central SQLite main file;
- offline status/search/read results at their actual evidence level;
- ingest disposition, central record ID, revision, and body-free receipt state;
- immediate central MCP search and typed exact-read result;
- exact-content round-trip result for Unicode, literal tabs, newlines, Markdown,
  and code block;
- refresh result and generated-projection boundary;
- warmup central health, aggregate binding result, and proof it exited;
- every failed, skipped, or unavailable check stated as such.

Do not store raw terminal output, a raw chat transcript, credentials, private
reasoning, or unbounded diagnostics in Cortex or the handoff report.

## Backup, failure, and rollback boundaries

Before ingest, the operator may create a recoverable active-store snapshot with
the exact no-option command:

```powershell
& $CortexExe backup
```

The valid result is a new timestamped directory below
`%LOCALAPPDATA%\Pegasus\Cortex\backups` containing exactly a consolidated
`cortex-v2.db` and canonical `manifest.json`. This is an online SQLite backup.
Never copy the live `cortex-v2.db` plus WAL/SHM files as an ad hoc backup or
transport.

Failure behavior is deliberately conservative:

- capture failure or a pre-ingest stop leaves central data unchanged;
- ingest validation, unsupported-schema, or divergent-record failure leaves
  the exact candidate pending;
- successful ingest replaces only the candidate frame with a body-free receipt;
- installation rollback selects a preserved immutable Cortex version but does
  not delete or rewind V2 data, Exchange bindings, old databases, or backups;
- restoring a backup, changing the selected immutable version, archiving the
  acceptance record, removing a project binding, or deleting any database,
  receipt, candidate, legacy file, or backup requires a separately authorized
  operator action. The Exchange acceptance agent must stop and return the
  evidence instead of attempting those actions.

## Explicit exclusions

This acceptance does **not**:

- publish local content to a collective namespace or external system;
- create or assume a publication/review gate for local ingest;
- add a network listener, service, firewall rule, scheduled resident worker, or
  watcher;
- create a second MCP registration, mutable store, profile, or sticky namespace;
- migrate, replay, deduplicate, or delete historical Exchange `.cortex` files;
- modify Pegasus Exchange product source or deploy Pegasus Exchange;
- grant stored content permission to execute or authorize unrelated actions.

The MCP server is local STDIO and starts on demand with the fresh Codex task.
`warmup` is a bounded sign-in/preflight command that exits; it is not a
pre-warmed resident service.

Task scoping is logical API isolation: offline commands cannot enumerate, read,
ingest, or refresh another task's scope when given this task's ID. It is not an
OS security boundary against another process running as the same user. All
tasks retain one shared, namespace-addressed central SQLite database and the
same single STDIO MCP route; no service, network listener, or second database is
introduced.
