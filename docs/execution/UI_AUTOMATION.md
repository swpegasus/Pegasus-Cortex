# UI automation contract

Pegasus AgentOps provides bounded, provider-neutral UI automation for standard
Windows windows and controls. It is intended for deterministic developer and
installer interactions that cannot use a native API or silent installer mode.
Prefer a native API, a documented unattended installer switch, or a typed
setup adapter whenever one is available.

## Execution boundary

- UI operations execute only through the authenticated signed-in user's tray.
- The LocalSystem service does not load the UI provider and cannot automate the
  Session 0 desktop.
- `ui_find` returns an exact target identity: `hwnd`, positive `pid`, and
  canonical UTC process `start`.
- Every later operation requires and revalidates that complete identity.
- Control mutations also require an exact resolved `ctrl` handle.
- Every operation except `ui_status` requires a 1..30 second timeout.
- The fixed UI helper first enters a Cortex-owned kill-on-close job. When
  Windows rejects that nested assignment because the helper already inherited
  an operating-system job, Cortex accepts only that verified inherited-job
  case and uses exact helper-process-tree termination for cleanup. General
  process and registered-tool execution remains fail-closed and does not use
  this fallback.

The internal provider is the admitted product-local AutoItX payload, loaded
registration-free. `ui_status` reports the exact runtime provider version and
architecture. That implementation detail does not change the public contract
and does not authorize raw AutoItX exports or COM registration.

For installer and repair acceptance,
`PegasusCortex.Host.exe --ui-provider-status-once` performs one bounded
provider probe, emits the typed JSON response, and exits without registering a
tray or service route. It is a diagnostic of the packaged host/provider pair,
not an alternate automation interface.

## Fixed operations

| Operation | Purpose | Additional input |
| --- | --- | --- |
| `ui_status` | Provider readiness, version, architecture | None |
| `ui_find` | Resolve exactly one window | `title`; optional `window_text`, `match=exact|contains`; `tmo` |
| `ui_window` | Read bounded title, rectangle, and state | `hwnd`, `pid`, `start`, `tmo` |
| `ui_control` | Resolve or inspect a standard control | identity plus exact `ctrl`, or `control_class` and optional `instance=1..128`; `tmo` |
| `ui_activate` | Activate an exact window | identity, `tmo` |
| `ui_move` | Move/resize and verify the rectangle | identity, `x`, `y`, `width`, `height`, `tmo` |
| `ui_focus` | Focus and verify an exact control | identity, `ctrl`, `tmo` |
| `ui_click` | Click an exact control | identity, `ctrl`; optional `button`, `count`; `tmo` |
| `ui_set_text` | Set and verify bounded control text | identity, `ctrl`, ephemeral body text, `tmo` |
| `ui_capture` | Capture the exact identity-bound window to PNG | identity, exact absent admitted `dst`; optional `fmt=png`; `tmo` |

Use `pao help OPERATION` for the authoritative current required/allowed fields,
bounds, and examples. `x` and `y` are -32768..32768; width and height are
1..16384. Click buttons are `left`, `right`, or `middle`, and count is 1..3.

## Sensitive and durable data

Window and control selectors, returned titles, and returned control text are
available only in the bounded live response and are scrubbed from durable job
state. `ui_set_text` accepts MCP `text` or CLI `--stdin` through the ephemeral
body channel. Durable requests, receipts, diagnostics, and logs contain only
non-content metadata such as byte count and verification state.

`ui_capture` writes image content directly to an admitted user path and returns
only `dst`, `fmt`, dimensions, byte length, and SHA-256. Image bytes never cross
MCP or enter durable receipts. Captures may contain sensitive on-screen data;
choose and protect the destination accordingly.

Do not use UI automation for credentials or secret-bearing controls. The
nonpersistence boundary reduces accidental retention; it is not a credential
automation feature.

## Failures and remedies

- `ui_unavailable`: the user-session provider cannot be loaded or probed. Keep
  the tray running and repair the product payload; do not register COM.
- `ui_not_found`: no window/control matched. Reinspect the visible fixture and
  use bounded exact selectors.
- `ui_ambiguous`: more than one window matched. Add exact title/text criteria.
- `stale_target`: the handle, PID, or start identity changed. Resolve again;
  never substitute by PID or title alone.
- `access`: integrity/session ownership prevents safe interaction. Run the
  intended target in the same signed-in user context; do not route through
  LocalSystem.
- `ui_unsupported`: the control is custom-rendered or does not expose the
  requested standard operation. Use the application's native API or supported
  unattended mode.
- `timeout` or `canceled`: inspect the live target before deciding whether to
  retry.
- `postcondition_failed` or `recovery_required`: the action may have occurred,
  but its final state could not be proven. Do not blindly repeat it.

## Explicitly unavailable

AgentOps does not expose a raw provider pass-through, scripts, macros,
multi-step sequences, provider process-launch APIs, RunAs, process killing,
global keyboard/mouse injection, clipboard access, OCR, credential entry, or
remote/Session 0 UI automation. Screen capture is limited to the exact
identity-bound window and an admitted PNG destination.

See `examples/ui-automation/README.md` for MCP and CLI requests following the
safe flow from readiness through postcondition verification.
