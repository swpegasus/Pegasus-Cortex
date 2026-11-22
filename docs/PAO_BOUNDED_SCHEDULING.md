# Cortex V2 bounded user scheduling

Release: `2.2026.10.08.05.26.A`. The authorized core-only installation on
October 8 completed with exit 0 and preserved registered roots and databases.
Fresh installed accepted-job completion, exact RID replay, task recovery,
scheduler health and bounded MCP/asset reads passed. See
`artifacts/validation/v2-installation-current.json`. These bounded checks are not
sustained multi-agent load or proof of all privileged-service failure paths.

## Completion contract

Ordinary queueable PAO mutations accept the optional JSON boolean `wait` (CLI
`wait=false`). Omitted or `true` preserves synchronous completion. `false`
returns an accepted JID/state after durable admission, not a successful result.
Use the returned exact JID with `job_stat`, `job_log`, `job_wait` and
`job_cancel`. JID identifies the durable job; RID identifies the request for
idempotency/reconciliation and is not a substitute for JID. The public surface
currently has no exact read-only RID-to-JID lookup. A known pre-admission
rejection can be corrected under the normal retry contract; an unknown or lost
reply requires reconciliation before any resubmission, even with the same RID.
Agent output says ACCEPTED for queued/running work and omits invented
exit/output/log facts. Root, app, UI, service and read-only selectors do not
accept deferred mode. Schemas/help derive support from the engine predicate.
This includes literal `replace` when live help advertises `wait`: ACCEPTED does
not prove the text preimage, match count, or target publication succeeded.
Inspect its terminal receipt before claiming the edit.

An accepted deferred job belongs to the user engine; client EOF/cancellation
does not cancel it. Explicit exact-job cancellation or engine shutdown controls
execution. Queued cancellation shares the SQLite queued-to-running CAS and
cannot revive a canceled mutation. Running cancellation reports requested until
the actual terminal receipt exists. Never relaunch because a reply was lost.

### Copy cancellation and progress (development source)

The November 21 transfer update connects `cp` to Windows `CopyFileExW` while
retaining its existing `fail`, `replace`, `skip` and `clean` policies. It is a
development-source change until included in a later installed release; the
14.26 shared-MCP handover does not establish this copy behavior.

For a running copy, exact `job_stat` returns the retained receipt in `rec` and a
separate optional RAM-only `progress` snapshot. Counters include completed files
(`cp`), completed deletions (`del`), skipped files, observed transferred bytes,
and current-file byte observations. No whole-tree size scan or reliable remaining
time is implied. `byte_progress_observed=false` means byte progress was not
available; `byte_count_saturated=true` marks the exceptional counter-overflow
case rather than presenting a clamped value as exact. Polling does not persist
chunk history, per-file names or additional job rows.

Explicit `job_cancel` requests cooperative cancellation. The original native
call, scheduler reservation and maintenance effect remain owned until the copy
actually returns. Blocked device or network I/O may delay its next callback;
there is no hard cancellation deadline. Windows `PROGRESS_STOP` is used so an
interrupted destination remains available for inspection. A success already
returned by Windows remains a success even if cancellation arrives afterward.

Failures and cancellations retain completed counters and
`destination_may_have_changed`. Possible destination effects (including a new
directory, attribute change or partial file with zero completed files) produce
`Partial=true` and `attention`; cancellation before any destination effects
produces `canceled` without partial status. `source_deletion_issued=false`
records that this operation did not delete the source; it is not a claim that
another process left the source unchanged. Inspect an interrupted destination
before a deliberate new request. The job is not automatically retried or resumed
after host restart. Archive and move/remove cancellation are separate contracts.

### Built-in ZIP cancellation (development source)

The subsequent ZIP update passes the same owned worker token through built-in
list/test admission, source traversal, compression, content/CRC/SHA validation,
verification, clean traversal and extraction. Existing public library overloads
remain and forward an uncancelled token; explicit trailing-token overloads serve
the Engine. All existing entry/byte/ratio bounds, path admission and pinned
no-follow identity checks remain in force.

Cancellation is checked between synchronous calls/chunks and immediately before
publication. A successful final atomic publication wins a later cancellation
race. Before destination effects, cancellation is nonpartial. After extraction,
clean or directory/attribute changes, the terminal result retains completed
`del`, `cp` and `changed` counters and remains `attention`/partial. Published files
are not automatically removed or retried.

Unpublished temporary cleanup runs without the canceled token while its original
operation is still owned. If cleanup cannot be confirmed, the result includes
`temporary_cleanup_failed=true` and stays partial, including when no final file
was published and `changed=0`. The flag is separate from completed mutation
counters. No automatic cleanup retry, source deletion or restart/resume is implied.
Blocked filesystem, compression and cleanup calls must return before cancellation
can settle; there is no hard cancellation deadline. The external 7-Zip process
contract is separate and unchanged by this built-in ZIP unit.

These are development-source semantics until a later coherent installed update;
they are not established by the earlier 14.26 shared-MCP handover.

## Bounds and ordering

The user scheduler retains at most 128 pending and three active mutations with
a combined 16 MiB accounted UTF-8 request-field/payload budget. This is not an
RSS cap. Admission and sequence registration share one short engine lock.
`queue_full` is exit category 3: no new job was admitted; retry the same RID when
capacity is available. Existing RID replay is allowed even when full.

Fixed filesystem/archive/encoding effects reserve canonical source/destination
paths exclusively. Exact and ancestor paths conflict case-insensitively with
separator boundaries. Conflicts with active AND older pending jobs preserve
order; unrelated fixed paths can overlap. Unknown tools, PowerShell, builds,
Git, recipes and policy/configuration mutations remain globally exclusive.
Declared workspace roots are admission boundaries, not process sandboxes.
Do not claim an arbitrary script allows unrelated mutation concurrency.

A globally exclusive reservation lasts until its execution callback finishes.
A controller launched by PAO `run`, registered PowerShell, .NET, Git, or a
recipe can therefore wait on an inner same-engine mutation that cannot start
until that controller ends. `wait=false` changes acknowledgement, not this
reservation or the process lifetime. A descendant execution marker may reject
the nested mutation as `reentrant_mutation` before admission; callers must not
depend on that rejection as a substitute for correct orchestration.

Launch a finite coordinating driver in the ordinary signed-in user context
outside a PAO command reservation, and keep its actual tool operations on PAO.
Do not adopt `app_start` as a general workaround: it owns a separate retained
application lifetime and has independent active/retained capacity limits. See
`PAO_APPLICATION_LIFECYCLE.md`.

Read-only operations, diagnostics and exact job controls bypass the mutation
scheduler. The root/System broker retains its independent serialized queue and
policy. The installed native/1 route still pairs one request/response; this scheduler
acceptance did not introduce implicit response multiplexing. The later
source/candidate native/2 and MCP opt-in contract is documented separately in
`NATIVE-MULTIPLEXING.md` and requires its own installed/consumer acceptance. Choose
nonblocking job_stat when more work must use that same client stream.

The standalone PAO STDIO adapter keeps only active tool calls, not a history of
completed Task objects. Its ceiling is 32 active calls; an excess call receives
protocol error -32001 before dispatch. The reader remains available for ping and
cancellation while saturated. EOF, reader failure and host cancellation drain
active calls before disposing the output gate. This bound is independent of the
durable mutation queue and does not turn an accepted deferred job into canceled
work when its client disconnects.

## Recovery and cleanup

Completed history uses soft age/size cleanup: a completion-triggered pass examines
at most 256 oldest sequence entries, expires known-terminal history older than
30 days, and uses a 64 MiB used-page or 10,000 completed-row trigger to prune
history older than the protected recent 24-hour replay window. Current
queued/running work, active effects and unresolved outcomes are preserved;
unresolved history becomes eligible only after explicit discard. One newest row
preserves the existing sequence high-water mark. These targets never refuse new
submissions, and protected data can exceed them. Cleanup removes history rows,
not operation output or user data; SQLite can reuse freed pages without shrinking
the allocated file. Replay is bounded by retained history: a missing job keeps
`not_found`, proves neither that it never existed nor that its effects did not
occur, and must not trigger automatic reconstruction or retry.

Startup selects one recoverable row at a time
and fills only bounded scheduling capacity, never an entire legacy ledger.
The captured recovery ceiling excludes new admissions. Older disk-only queued
work gets precedence; exact job_wait polls receipts until bounded admission
occurs, without retaining a completion or full request for each backlog row.
Oversized, previously running, application and omitted-ephemeral-body rows
become recovery_required rather than executing or blocking admission forever.
Oversized request JSON is filtered in SQL before deserialization.

Shutdown drains owned callbacks/process cleanup while preserving never-started
durable queued requests for next-start recovery. Ephemeral bodies are not stored;
after a restart their omitted-body marker requires explicit recovery. Completion
and cancellation release resident jobs, waiters, requests, registrations and
reservations. Persistence failure releases waiters with recovery_required error
instead of fabricating a committed terminal result.

## Evidence boundaries

The focused tests use isolated stores, deterministic scheduler gates, OS pipe
fixtures and real registered PowerShell files gated by observable marker files.
They do not change the installed service, roots, trust, databases or registration.
Generated history/backlog fixtures are temporary, not packaged starter knowledge.
See the current release report for final build/test/package results. A legacy
console-service MCP cancellation fixture is a separate known unavailable lane;
its launch failure is not counted as a pass. Sustained installed multi-agent load
and native Cerebral rendering remain separate acceptance work.
