# UI automation examples

AgentOps exposes a small provider-neutral UI surface for standard Windows
windows and controls. It runs only in the authenticated signed-in user's tray
session. The LocalSystem service never loads the UI provider or automates the
Session 0 desktop.

Use this flow:

1. Check readiness.
2. Resolve exactly one controlled window.
3. Bind its `hwnd`, `pid`, and canonical UTC `start` identity.
4. Inspect the window and intended control.
5. Perform one targeted mutation.
6. Inspect again to verify the application-specific postcondition.
7. Capture the exact window to an admitted PNG only when visual evidence is needed.

Call MCP `{ "op": "help", "name": "ui_find" }` or CLI
`pao help ui_find` for the live operation-specific contract. Every UI operation
except `ui_status` requires `tmo` in seconds from 1 through 30.

The values below identify a task-owned test fixture. Replace them with values
returned by your own `ui_find` and `ui_control` calls; never reuse stale handles
or process identities.

## 1. Check provider readiness

MCP:

```json
{"op":"ui_status"}
```

CLI:

```text
pao ui status
```

A readiness failure is local to UI automation. Follow its bounded remedy; do
not try COM registration, `regsvr32`, or a System/root-code route.

## 2. Resolve one fixture window

MCP:

```json
{"op":"ui_find","title":"AgentOps UI Fixture","match":"exact","tmo":5}
```

CLI:

```text
pao ui find title="AgentOps UI Fixture" match=exact tmo=5
```

An exact match is the default. `match=contains` is available for a bounded
substring match. Optional `window_text` further narrows the search. The call
must resolve exactly one window or fail with `ui_not_found` or `ui_ambiguous`.
On success, retain the returned `hwnd`, positive `pid`, and canonical UTC
`start`; later calls revalidate all three immediately before acting.

The remaining examples use these placeholders:

```text
HWND=0x0000000000012345
PID=4242
START=2026-08-27T20:15:30.1234567Z
CTRL=0x0000000000012350
```

## 3. Inspect a standard control

Resolve a control by class and one-based instance:

```json
{"op":"ui_control","hwnd":"0x0000000000012345","pid":4242,"start":"2026-08-27T20:15:30.1234567Z","control_class":"Edit","instance":1,"tmo":5}
```

```text
pao ui control 0x0000000000012345 4242 2026-08-27T20:15:30.1234567Z control_class=Edit instance=1 tmo=5
```

Alternatively, inspect an already resolved control with exact `ctrl`. The live
result includes its handle, rectangle, and bounded text. Returned text is
scrubbed from durable state; still do not inspect credentials or secret-bearing
controls.

Inspect the window itself with:

```json
{"op":"ui_window","hwnd":"0x0000000000012345","pid":4242,"start":"2026-08-27T20:15:30.1234567Z","tmo":5}
```

The live result includes bounded title, rectangle, and state. The title is not
retained in durable receipts.

## 4. Activate or move the exact window

Activate:

```json
{"op":"ui_activate","hwnd":"0x0000000000012345","pid":4242,"start":"2026-08-27T20:15:30.1234567Z","tmo":5}
```

```text
pao ui activate 0x0000000000012345 4242 2026-08-27T20:15:30.1234567Z tmo=5
```

Move and resize, then verify the final rectangle:

```json
{"op":"ui_move","hwnd":"0x0000000000012345","pid":4242,"start":"2026-08-27T20:15:30.1234567Z","x":120,"y":80,"width":800,"height":600,"tmo":5}
```

```text
pao ui move 0x0000000000012345 4242 2026-08-27T20:15:30.1234567Z 120 80 800 600 tmo=5
```

`x` and `y` are limited to -32768..32768; `width` and `height` are 1..16384.

## 5. Set control text without persistence

MCP supplies bounded `text` in the ephemeral request body:

```json
{"op":"ui_set_text","hwnd":"0x0000000000012345","pid":4242,"start":"2026-08-27T20:15:30.1234567Z","ctrl":"0x0000000000012350","tmo":5,"text":"Fixture value"}
```

CLI reads the value from bounded standard input:

```text
pao ui set-text 0x0000000000012345 4242 2026-08-27T20:15:30.1234567Z 0x0000000000012350 tmo=5 --stdin
```

The operation verifies the text internally and returns only `changed`,
`verified`, and `text_bytes`. It never echoes or persists the supplied text.
Do not put text in positional arguments, logs, feedback, or receipts.

## 6. Focus or click, then inspect

Focus:

```json
{"op":"ui_focus","hwnd":"0x0000000000012345","pid":4242,"start":"2026-08-27T20:15:30.1234567Z","ctrl":"0x0000000000012350","tmo":5}
```

Click once with the left button:

```json
{"op":"ui_click","hwnd":"0x0000000000012345","pid":4242,"start":"2026-08-27T20:15:30.1234567Z","ctrl":"0x0000000000012350","button":"left","count":1,"tmo":5}
```

```text
pao ui click 0x0000000000012345 4242 2026-08-27T20:15:30.1234567Z 0x0000000000012350 button=left count=1 tmo=5
```

`button` is `left`, `right`, or `middle`; `count` is 1..3. Follow the click
with `ui_control` or another application-specific read-only check. Do not
assume the click succeeded merely because it was issued.

## 7. Handle stale identity safely

Capture the exact identity-bound window without returning image bytes through
MCP:

```json
{"op":"ui_capture","hwnd":"0x0000000000012345","pid":4242,"start":"2026-08-27T20:15:30.1234567Z","dst":"captures/window.png","ws":"project","fmt":"png","tmo":5}
```

```text
pao ui capture 0x0000000000012345 4242 2026-08-27T20:15:30.1234567Z captures/window.png ws=project tmo=5
```

The destination must be absent and its ordinary parent must exist. The result
contains the exact destination, PNG dimensions, byte length, and SHA-256. The
image itself stays at the requested path. It may contain sensitive on-screen
content, so do not capture credential or secret-bearing windows.

## 8. Handle stale identity safely

This intentionally wrong start identity must fail without touching the target:

```json
{"op":"ui_activate","hwnd":"0x0000000000012345","pid":4242,"start":"2020-01-01T00:00:00.0000000Z","tmo":5}
```

On a stale handle, exited process, PID reuse, or identity mismatch, resolve the
window again and use the newly returned identity. Never substitute a current
process merely because its PID or title matches.

## 9. Treat uncertain completion as attention-required

If a mutation times out, is canceled after dispatch, loses transport, or
cannot verify its postcondition, inspect the target before deciding what to do.
Do not blindly retry. A recovery/ambiguous result may mean the action happened
even though AgentOps could not prove the final state.

## 10. Dangerous/global automation remains denied

There is no `ui_send` operation:

```json
{"op":"ui_send","text":"not permitted"}
```

The schema rejects it with a remedy to use a supported handle-bound UI
operation. AgentOps also denies raw provider calls, scripts or sequences,
process launching/termination, global keyboard or mouse injection, clipboard
access, OCR, credential handling, and Session 0 automation. Capture is limited
to one exact identity-bound window and an admitted PNG destination.

The current implementation uses a registration-free internal AutoItX provider,
but the public contract and examples are intentionally provider-neutral.
