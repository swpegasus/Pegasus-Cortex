# Local Windows service control

The fixed `windows_service` family supports `list`, `read`, `start`, `stop`,
`restart` and `startup`. These are local Win32 service operations. They do not
take a remote machine, binary command line, credentials, arbitrary SCM control,
driver operation or dependency cascade.

Ordinary `list` and `read` use the existing signed-in owner Host. Exact `read`
and the four controls can explicitly select one of two independent routes:

- `root`: the existing authorized LocalSystem execution service.
- `elevate: "uac"`: one fixed same-owner Windows elevation helper, admitted and
  owned by the ordinary Host. It does not require the optional System service.

Root and elevation cannot be combined. Neither route is an automatic fallback.
Elevated listing is unavailable. A different account supplied to a Windows UAC
prompt is refused by the owner/session check. The helper does not establish a
persistent administrator session.

The unified MCP operation selects `windows_service_<action>` and supplies the
version 1 JSON in `text`. The CLI takes `windows_service <action> --stdin` with
the same body on standard input. `elevate=uac` is a separate CLI argument.
For example, the body for an exact elevated read is:

```json
{"version":1,"service":"ExampleService"}
```

Every elevated call requires a stable `rid`, including read. Controls also need
the existing configuration preimage and expected state. Stop and restart pin
the observed target service PID and canonical UTC start time. Startup accepts
`manual`, `automatic`, `automatic_delayed` or `disabled`; it does not start or
stop the target. Native control timeouts are 1–120 seconds, default 30.

`wait=false` is available only on the explicit UAC route. It returns the same
admitted JID while the original helper continues. `job_stat` and `job_wait`
without Root observe the owning user job; an observer cancellation does not
abandon, cancel or replace that helper. The existing Root observation path
remains System-store-only and never falls back to a User/UAC JID.

An elevated read owns a durable helper/effect job even though its SCM operation
is read-only. Before releasing the one-use payload, the parent persists the
original helper PID and start time, route, request identity and uncertainty
barrier. Helper generation is kept separate from target service PID/start.
These retained observations do not authorize adopting or terminating a process.

The existing shared elevation registry bounds all certificate, machine-key,
task and service helper purposes together. Each request is pinned to the
original High child, same owner/session and protected installed image. Private
completion traffic remains admitted while the public Host gate is closing so
the original work can drain. A completion report alone does not release the
helper lifetime before original-child exit.

Request and completion envelopes reject unknown or duplicate fields, incorrect
target/preimage/RID/JID, and malformed outcome combinations. The first malformed
completion is consumed; there is no second report or effect retry. Missing
native evidence after payload release remains `recovery_required`, with no
fabricated `action_issued:false`. A valid report survives a lost final ACK.

An already-desired startup setting can succeed with `phase:"unchanged"` and no
issued write. Changing delayed-start configuration can require two native
writes. Failure after the first write remains issued and uncertain; there is no
transaction or implicit rollback. The held SCM handle and preconditions are
cooperative checks, not an atomic compare-and-swap or universal generation lease.

The Cortex execution service itself retains its dedicated maintenance/Exit
controls; generic mutation is refused through both the provider and native open
path. Its exact status can still be read.

The focused `--windows-service-elevation-only` selectors use synthetic child,
identity, transport and SCM outcomes, plus captured frontends and an owned
temporary SQLite store. They do not invoke UAC or change Windows services.
Compilation and these fixtures do not establish actual elevated execution or
installed-product compatibility.
