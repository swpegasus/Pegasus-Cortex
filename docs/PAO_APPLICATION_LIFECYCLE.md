# PAO user application lifecycle v1

Source contract; not evidence that the installed host exposes these operations.
Discover `app_start`, `app_status`, and `app_stop` in the live PAO catalog before
calling them. CLI and MCP use the same operation validation and owner.

## Ownership and permissions

The signed-in user execution host owns each admitted application generation.
It uses the existing User execution level, authenticated same-user/session pipe,
and registered tool admission: exact executable, probe, arguments, environment,
working directory, and configured Hidden or Visible window mode. This is not a
new approval system and does not grant System/elevated execution. Root codes,
raw executables, command strings, and arbitrary environment overrides are not
accepted by these operations. A missing or older user host fails explicitly;
there is no service fallback.

A short request finishing or the root application window exiting does not end
its remaining owned workers. One Windows job retains the helper and descendants
until they finish, the exact generation is stopped, or its owning user host
exits. Closing the dashboard alone does not stop that host. Ordinary `run`
retains its timeout/output contract and process-tree cleanup; it is not a
detached application launch. `run wait=false` changes only job admission
acknowledgement; its timeout, output bounds, and descendant cleanup still
apply. `app_start` has no lifetime `tmo` or `wait` selector and returns no
stdout/stderr stream. Its bounded startup acknowledgement is separate from
the retained application lifetime.

## Typed operations

| Operation | Required | Optional operation fields |
| --- | --- | --- |
| `app_start` | `cfg`, `name` | `arg[]`, `cwd`/`ws`, `aid`, `rid` |
| `app_status` | `name` | none |
| `app_stop` | `aid` | `rid` |
| app_inventory | none | max (1..8), opaque cursor, rid |
| app_release | exact aid, inventory epoch, explicit rid | none |

Common presentation selectors are `detail` and, when advertised by MCP,
`output`. Use `output=agent` for compact agent results. The portable `name`
selects a host-owned slot; returned `aid` identifies one exact generation.
Neither is a caller-supplied PID or a permission grant.

- Starting an unused name creates a generation after normal tool admission.
- Repeating the same name and admitted launch identity without `aid` returns
  its current state, even when terminal. A conflicting launch is rejected.
- To restart an `exited` or `stopped` slot explicitly, supply its previous
  `aid` to `app_start`. A stale or still-active generation is rejected. Reuse
  the same `rid` for a retry of that exact restart, not for another launch.
  This explicit transition can use a newly admitted build or argument vector;
  retry identity must still match the already-created successor.
- `app_stop` targets only the exact owned generation and its remaining tree.
  It never kills by display name or PID alone.
- Host restart does not adopt arbitrary PIDs or replay application starts.
  Capacity is bounded; exhaustion returns an error rather than silently
  evicting a name and relaunching it.

Status contains bounded identifiers and disposition: `name`, `aid`, `state`,
root `pid`/`start` and `exit` when known, and `processes` counting application
processes rather than the isolation helper. `root_exited_workers_running`
distinguishes a closed GUI from its still-running owned workers.
An unconfirmed stop or host-protocol failure stays `stopping` with an issue;
it is not terminal merely because termination was requested. Restart stays
blocked until the exact owned job is observed quiescent. An incomplete-stop
receipt retains bounded generation/state evidence and requires status/recovery.
Owner shutdown closes every owned job even when observation fails, but reports
`app_stop_incomplete` if bounded shutdown cannot be confirmed; closing a handle
is not itself proof that all processes have finished exiting.

V1 does not return stdout/stderr, arguments, environment, or application bodies.
The helper drains output without retaining or ingesting it. Applications that
need a console-output UI must retain their existing output/presentation owner;
do not claim stream parity or start a duplicate worker to obtain it.

## Capacity and recovery

The current source manager admits at most 16 nonterminal application
generations and retains at most 128 generations for the lifetime of its user
host. Stop/exit frees active capacity but does not remove the retained
generation. An explicit restart creates a successor and keeps its predecessor
for identity and replay; repeated restarts can therefore exhaust the retained
bound even when few applications remain active.

An exact existing launch/restart replay can return its known state before new
admission. A new generation at capacity fails rather than evicting history or
starting an untracked process. The source diagnostic distinguishes the
exhausted bound and reports non-dispatch; this is source evidence, not a claim
that the October 8 installed host has that newer diagnostic.

The installed October 8 baseline exposes neither an application inventory nor
a release/purge operation. The source candidate adds the following closed
owner-local operations; they are unavailable until a matching normal product
upgrade and installed acceptance. Do not infer support from new CLI help
against an older resident host.

app_inventory (CLI: pao app inventory max=8 cursor=...) is a nondurable read
outside mutation scheduling and start-capacity admission. It reports schema
pegasus.pao.application-inventory/1, a per-host epoch, catalog revision,
observation time, exact retained count/bound, known active/terminal counts,
unknown-observation count, nullable active total, and retained-first admission
classification. It emits at most eight generation rows with aid/name/state,
root PID/start, known process/exit data and releasable; no invocation args,
environment, source bodies or descendant member list. An opaque cursor is
bound to epoch, catalog membership revision and last aid. Start/release
invalidates it; lifecycle observations may change between pages. Unknown
observation is never a fabricated zero or available capacity. Inventory does
not launch, terminate, or drive helper cleanup, and works while starts are
saturated.

app_release (CLI: pao app release AID epoch=E rid=R) removes exactly one fully
retired terminal generation from retained manager metadata. It never calls
stop or requests termination. exited/stopped alone is insufficient:
asynchronous helper/handle retirement must have succeeded. Running,
surviving-worker, retiring and unknown generations remain owned. The exact
name map is removed only if it still points to that aid; predecessors cannot
erase successors. A fresh deliberate name reuse receives a fresh aid.
No wildcard, bulk, automatic eviction, host reset or new release authority is
introduced.

Release commits a durable receipt with its canonical request identity.
Exact release-RID replay returns that receipt without repeating reclamation;
the CLI's incidental working directory is excluded from release identity,
and MCP workspace defaults do not scope either maintenance operation.
Changed aid/epoch under a used RID fails closed, including a previously
failed RID. Old start/restart replay cannot recreate a released generation.
A released predecessor reference fails closed instead of indexing a missing
successor. No unbounded in-memory tombstone or replay cache is added; normal
SQLite receipt history is preserved. A release also checks the host epoch;
inventory coordinates and name labels never confer authority to release an
unknown generation.

Owner host exit still ends all remaining owned jobs. Host restart is not a
per-name cleanup API and requires separate operational authority. Stop and
release are distinct: stop affects a live exact job; release only reclaims
already-retired metadata. This candidate adds no arbitrary-descendant
membership attestation to app_status or inventory.

Source verification uses focused fake-handle capacity/unknown/retirement/CAS
and typed-projection cases plus an isolated Windows process/helper and
SQLite-RID replay case. Those tests are not installed-route acceptance.
Runtime adoption requires a coherent signed product/installer built by the
owner, coordination of all current owned applications before the user host
ends, preserved registrations/state/databases and durable receipts, and a
separately authorized normal upgrade. No DLL hot swap or live host restart is
implied by source preparation. After authorized adoption, verify read-only
inventory first, then only a known task-owned inert terminal generation's
exact release/replay; never retire unrelated or current Cerebral generations.

## Verification boundary

Use task-owned temporary registration/state and the candidate helper for source
tests. Do not mix a new CLI with an old installed host and infer support from
CLI discovery alone. Installing/selecting the candidate, changing the live
tray, service, PATH, or registry remains a separate machine-level operation.

For an explicitly authorized real-application source acceptance, the non-shipped
test executable accepts `--app-candidate <absolute-plan.json>`. Its bounded plan
contains `tool` (the normal registered-tool definition, including a safe version
probe), `workingDirectory`, `arguments` (a string array), and `observeSeconds`
(1..120, default 30). The harness creates isolated temporary registration/state,
uses the candidate manager/helper, reports changed compact status only, then
stops the exact generation and disposes its job. It does not use the installed
tray, its registrations, or its pipe. Close the application's root GUI during
the observation interval to check retained worker lifetime. This is manager/job
acceptance, not installed-route or consumer-MCP end-to-end evidence.
