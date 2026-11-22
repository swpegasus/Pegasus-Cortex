# Cortex utilization and execution dashboard

## Open the dashboard

The existing signed-in user host supports `PegasusCortex.Host.exe --dashboard`.
This opens one transient window, not a second tray, queue, service, or broker
registration. The resident tray also opens the dashboard on double-click and
offers **Open Cortex** in its menu. Queue controls remain available separately.
The source dashboard places connection approvals under **Connections & access →
Connections & Full control**; discover the installed build before relying on a
new control.

For a development build, `tools/New-CortexDashboardShortcut.ps1` creates a
create-only `Cortex Dashboard.lnk` in an explicitly selected existing directory.
It requires the exact host executable and validates ordinary path ancestors.
It does not install, register startup, change PATH, or replace the live host.
Build output and this shortcut are development candidates, not signed releases.
An installed shortcut must target the admitted host for the selected release;
the candidate does not change current installer ownership or registration.

## What is measured

Daily numeric aggregates (`cortex.utilization/1`) live in additive
`cortex_usage_*_v1` tables in the existing per-user `cortex-v2.db`. No new state
root, namespace, semantic record/revision, listener, watcher or service is
created. Existing semantic schema generation remains unchanged. Normal SQLite
backup/restore includes the optional aggregate tables.

The dashboard has operation, namespace-access, and observed-task tabs, with
7/30/90-day UTC windows and manual refresh. `PegasusCortex.exe metrics --days N`
returns the same read-only report for N=1..90. `cortex_status` includes a small
seven-day summary; this is not a new MCP tool. The status response describes
previously completed samples; its own sample commits after that snapshot.

* Operations: successful/failed calls, returned result items, exact full records,
  serialized MCP response bytes, mean/max operation duration and last seen time.
  Durations exclude the final metric write. Response bytes are not token usage.
  MCP lifecycle handshakes, protocol parse errors before tool dispatch, PAO
  execution, and legacy profile routes are not counted as Cortex tool activity.
* Namespace access: one access for each effective namespace echoed by a
  successful operation. A multi-namespace call is one total call but several
  namespace accesses. Namespace dimensions deliberately do not duplicate
  whole-response bytes, results or duration. Rejected inputs have global
  failure counts, not an invented effective namespace.
* Observed task IDs: canonical GUIDs supplied explicitly on `cortex_start`,
  `cortex_task`, `cortex_finish`, or task-scoped offline commands. Search/read
  lack a caller task ID and are not attributed by guessing. No sticky task
  association or process environment is used. IDs are client declarations,
  not authenticated agent identities. Recorder sessions count observed process
  sessions, not unique agents, users, pinned tasks, or concurrently active tasks.
* Offline: instrumented init/migration/capture/status/search/read/ingest/refresh
  commands attempt a separate `offline` channel sample only if the central DB
  already exists and is reachable. `utilizationRecorded` exposes success or
  loss on parsed command responses. Failed parse/extraction and cancellation
  before a response can be unobserved. Ambient warmup is not instrumented.
  HOT refresh means generation, not consumption. Ordinary editor/agent reads of
  `HOT.json` are **unknown**; no watcher or hidden file-access surveillance exists.

The collector records no query, title, body, record coordinate, raw output,
transcript, legacy root code, credential, application/folder restriction, or environment
value. Only fixed operation labels, explicit namespace names, canonical task
GUIDs, random session IDs, timestamps and numeric aggregates are retained.
These diagnostic tables are machine-local and never starter-corpus inputs.

## Retention, costs and failure behavior

Aggregates retain 90 UTC days. Pruning touches metric tables only. Each daily
aggregate/process table is capped at 100,000 rows; admission refuses samples
before exceeding that bound. Reports return at most 512 detail rows, totals
first, with an explicit truncation flag. Totals are independent from namespace
and task detail; never sum the three dimensions together.

Writes use the central WAL database, an atomic transaction and the same page
ceiling as semantic writes. There is no background buffer requiring shutdown
flush. Collection is best effort with a one-second SQLite lock timeout and a
two-second cancellation budget. A collection failure never turns a committed
semantic write into a retryable failure. Current-process lost samples are
reported through `cortex_status`; missed samples are not durably reconstructed.
Read-only dashboard/CLI reports do not initialize, migrate, prune, seed or write
to the database. `not-collected` and `unavailable` are not zero-use claims.

All reports are lower bounds: older binaries, offline periods, failures of the
collector and unobservable file reads can be absent. The first local 50-sample
fixture measured roughly 8 ms median added collection time; this is local test
evidence, not a production latency guarantee or an effectiveness benchmark.

## Connection approvals and Full control

Current source replaces the four-character root-code list/editor with persistent
connection approvals. Open **Connections & access → Connections & Full control**
to review a connection's readable name, reported application/chat labels and full
public IDs. Labels help identify the connection; they do not authenticate a chat
or make separate chats into separate permissions.

Ordinary access and **Full control** are separate explicit decisions. Full
control is off by default. **Every chat sharing a connection credential shares
its Full control authority.** Use separate connections when their permissions
must differ. The fixed owner-elevation control writes protected machine approval
metadata; credentials stay in owner credential custody, outside semantic records
and request arguments. Revocation and credential-generation changes are checked
on new dispatches, including work that was admitted to a queue but has not yet
started its protected callback. Already-started effects retain their original
ownership and are not undone by revocation.

Protected PAO operations additionally require `system:true`, an admitted
operation and the optional LocalSystem execution service. `system:false` is
ordinary intent where the operation permits that field; merely supplying the
boolean never grants authority. Registered User tools still run as the ordinary
owner. Reviewed owner-admin scopes keep their separate executable, input,
owner/session, expiry and declared-effect checks. Scope declarations do not
confine arbitrary administrator code.

The Cerebral private application Full control session remains independently
managed under **Applications**. It is not an MCP connection approval. These
source changes neither modify a live grant nor establish installed acceptance.

## Interpreting adoption and usefulness

Do not infer an adoption percentage from a sidebar screenshot. Pinned tasks may
be idle, reused across phases, or working without retained context being useful.
Once a new collector is installed, compare namespace access and exact reads over
a defined window, explicitly excluding old clients and unknown HOT consumption.
Task-ID counts show some participation, not every search/read attributable to
each agent. A low measured count warrants checking guidance and client versions
before concluding agents ignored Cortex.

Retrieval usefulness needs a separate bounded relevance evaluation: sample
search intent and selected results with consent, judge precision@1/@3/@5,
irrelevant-hit rejection, stale/wrong-namespace hits and exact-read success.
This release does not silently record those bodies, infer whether returned
records were injected, or treat more calls as better behavior. Preserve the
one-start-per-continuously-active-task guidance; do not manufacture utilization
by adding redundant load/save calls.
