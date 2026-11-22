# Knowledge.Windows.Service.UserSessionBoundary

Object: Knowledge.Windows.Service.UserSessionBoundary
Version: 1.0.0
Triggers: Windows service, tray/UI host, startup, privilege broker, service health, service-to-user communication
Rendered budget: 5120 UTF-8 bytes

## Separation

Windows services run in session 0 and must not own ordinary interactive UI. Put privileged, machine-lifetime, queue, and policy work in the service. Put tray UI, user preferences, attended interaction, and interactive automation in a signed-in user process. Connect them through a local authenticated, versioned, typed IPC contract.

## SCM lifecycle

Install, start, stop, query, and delete services through the Service Control Manager or supported service APIs. Respect pending states, checkpoints, and wait hints with bounded timeouts. A service process existing is not equivalent to SERVICE_RUNNING or functional readiness. Killing the backing process is not a controlled service stop and may trigger recovery policy.

## Startup

Select startup by responsibility:

- service auto-start for required machine-lifetime work;
- per-user logon startup for the tray/user host;
- explicit application launch for optional interactive work.

A service should not launch UI directly onto the interactive desktop. A user host failure must not silently grant the service interactive-user authority; service failure must not corrupt independent per-user semantic state.

## Health

Report SCM state, authenticated pipe health, component version/identity, queue state, and bounded recent failures separately. Avoid one aggregate “running” flag that hides which boundary failed.

## Update and repair

Stop only owned components, wait for confirmed transitions, preserve prior running/startup configuration unless intentionally changed, replace immutable binaries transactionally, then restore and verify each required role.

References:
- https://learn.microsoft.com/en-us/windows/win32/services/interactive-services
- https://learn.microsoft.com/en-us/windows/win32/services/service-status-transitions
