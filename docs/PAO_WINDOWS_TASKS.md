# Local scheduled tasks

The development source exposes `windows_task_list`, `read`, `instances`, `prepare`,
`run`, `stop`, `enable`, `disable`, `remove`, `create` and `update` through the
existing PAO CLI and MCP catalog. Discover the running catalog before using it:
source compilation does not install these operations into an older Cortex.

Each operation takes a `text` string containing closed version 1 JSON. CLI input
uses `pao windows_task <action> --stdin`; pass JSON on standard input, and pass
each CLI option as a separate argument. MCP uses `op: windows_task_<action>`.
Paths such as `\Pegasus\Example` name Scheduler objects, not filesystem roots.
No caller supplies a remote machine, COM class/member, task XML, security
descriptor, account, password, principal or run level.

`list` reads one existing folder, with `kind: tasks|folders`, `max: 1..64`, and
an optional exact `after` path. It scans at most 4,096 entries and returns a
bounded page. Paging observes a fresh live collection; it is not a snapshot.
`read` and `instances` take an exact `task` path. Reads return safe metadata and
opaque definition/security fingerprints, never action arguments or raw XML/SDDL.
Instance visibility depends on the caller's security context. An empty instance
list is not proof that no task is running at a higher context. Scheduler local
times retain the system zone and DST ambiguity; unknown offsets remain unknown.

Existing-task changes require a stable `rid`, exact `task`, and the fresh
`taskSha` from `read`. `stop` also requires the exact canonical `instance` GUID
and stops only that instance. `run` supplies no parameters, user/session
override or flexible flags; it checks enabled/demand-start eligibility and
reports an acknowledged request or observed instance, not completed external
work. Enabling a task permits future triggers. Disabling or removing its
registration does not stop current instances. These preimages are cooperative
checks immediately before the native call, not an atomic Windows compare-and-swap.

Ordinary owner changes require the admitted signed-in owner and an owner
InteractiveToken/least-privilege task. Explicit `root` selects the existing
authenticated System service route for inspection/control of existing tasks.
System cannot prepare, create or update task definitions through this family.
Access denial never triggers an identity change. Explicit `elevate: uac` selects
one same-owner administrator helper for exact `read`, `instances`, `run`, `stop`,
`enable`, `disable` or `remove`. It cannot be combined with `root` and cannot
select task listing, preparation or authoring. Each elevated request needs a RID,
even a read; `wait: false` observes its durable helper job without waiting for
completion. Ordinary reads without this selector remain ephemeral.

The installed Host helper validates the same owner's High process and exact
parent generation before redeeming its one-use task payload. Certificate
capabilities cannot redeem task payloads or vice versa. The ordinary owner records
payload issuance before release and holds the original job until the exact child
exits and entered callbacks drain. Native task action issuance is a separate fact:
a read can issue a helper payload while keeping `action_issued: false`. Missing
native evidence after payload release leaves the outcome unknown and omits the
native action-issued field. Declined or unavailable UAC never falls back to another
identity, System service or an ordinary task action. The helper starts before
normal Host initialization, so it creates no second repository, tray or MCP host.

Authoring resolves an existing registered User tool. `prepare` takes `tool`,
separate nonsecret `argv`, `environmentPolicy: scheduler`, and the registered
working-directory policy's explicit `workspace` alias or absolute `cwd` where
needed. It returns `toolVersion` and `toolSha`; both pins are required by
`create`/`update`. Create also requires `task` and `schedule`; update additionally
requires `taskSha`. The folder must already exist. Schedule `kind` is
`on_demand`, `once`, or `daily`; timed schedules require canonical UTC
`startUtc`, and daily requires `daysInterval: 1..365`. Registration is create-only
or update-only, fixed to the owner, with one executable action and initially
disabled. Enabling is a separate deliberate change.

Tool image/hash admission and its open admission lease cover registration and
immediate readback. Later Task Scheduler execution is independent of Cortex:
it does not retain Cortex's per-run image check, registered environment
replacement, output capture, timeout or child lifetime. The explicit Scheduler
environment policy acknowledges that difference. Percent expansion and
`$(Arg...)` substitution are refused in literal paths and arguments.

Mutations support `wait: false`, returning the admitted JID while the original
worker continues. The original fixed COM worker and its references remain owned
until actual return and cleanup, including after cancellation. Native statuses
remain HRESULTs, including positive warnings. An issued action with an unknown
outcome stays `recovery_required`; there is no automatic replay or compensating
stop/delete. Retained receipts omit task paths, definitions, principals and argv,
keeping only replay identity and bounded effect/uncertainty facts. Reads create
no durable jobs. System `job_stat`/`job_wait` observe only admitted service/task
control JIDs under the same authorized root; canceling observation leaves the
original work alone.

Keep the original authority route, RID and JID when resolving an uncertain result.
Owner and System engines have separate durable stores. Switching authority is a
new admission, not a cross-store retry or a way to discover the original outcome.

Before releasing a helper payload, the owner records its authenticated original
PID and canonical UTC start time with the uncertainty barrier. Recovery preserves
that generation pair for later inspection. These are retained observations, not
authority to reopen, adopt or terminate a process. A missing completion stays
uncertain; no replacement helper or automatic task replay follows a restart.
