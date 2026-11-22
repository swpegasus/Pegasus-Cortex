# Private Cortex pipe multiplexing

This source contract is additive and requires explicit negotiation. Installed
hosts, Codex registration/cache and Cerebral consumers are not changed by this
patch. Native/1 remains the default and retains one active callback/FIFO replies
per connection. Binary framing, Windows owner/session checks, registered client
lease checks, semantic tool contracts/store schema and the shared24 native+MCP
connection limit remain in place.

## Native/2

Use the existing native endpoint and hello with
`contract="pegasus.cortex.native/2"`. Verify the exact echoed contract, owner,
product/version/client identity and ready state before sending data. An explicit
version rejection must be surfaced; do not replay an uncertain mutation while
trying another transport. Registration still uses transport `native`.

Each normal request has the native/1 fields plus required `streamId`, a1–64
character string. Its connection-wide `id` remains a1–64 character string.
The dispatcher preserves FIFO execution within each stream; independent streams
can overlap. Use the same stream for dependent create/read or ordered mutations.
The caller owns dependency grouping; request IDs are correlation only. Responses
can finish out of order. One connection writer emits a complete frame at a time.
Duplicate pending IDs terminate the session before a second operation executes.

Example independent requests:

```json
{"id":"read-a","streamId":"a","op":"resource","uri":"cortex://..."}
{"id":"read-b","streamId":"b","op":"resource","uri":"cortex://..."}
```

Native/2 cancellation is a reader-path control frame:

```json
{"id":"cancel-1","op":"cancel","targetId":"read-a"}
```

The target is scoped to that connection. The control response acknowledges
`cancellation_requested`; it does not establish rollback or completion. The
target's terminal result is decisive. Unknown targets are harmless and do not
affect another connection. Revoked control callers close without injecting an
out-of-band error frame into an active writer.

## MCP edge

Legacy initialization retains ordered execution/replies. Clients can opt in with
`capabilities.experimental["pegasus.cortex/multiplex"]=true` in initialize. The
server advertises that experimental capability; the initialize response is
flushed before the initialized notification can advance the lifecycle. In this
mode each request is independent by default. Optional
`params._meta["pegasus.cortex/streamId"]` (string1–64 characters) groups ordered
operations. String and numeric IDs stay distinct. Standard
`notifications/cancelled` targets the connection's typed request ID.

The relay validates JSON-RPC response shape and pending ID before forwarding any
bytes. It reserves the ID through the complete output write. EOF drains at most
10seconds, then closes with an incomplete/Unknown diagnostic. It never stops the
independent resident host or automatically retries a request.

## Bounds and uncertainty

Initial policy:4 active callbacks globally,32 pending requests/replies per
connection,96 admitted callbacks overall and8MiB admitted UTF-8 payload bytes.
These are conservative policy limits, not a measured hardware capacity. Scheduling
is round-robin across connections, then streams. Opening more streams does not
purchase priority over another client. Malformed/oversized error replies count
against the response limit. Scheduler and writer tracking retain only live work.

Request deadlines begin at admission (300seconds), independently of native idle
reads (300seconds). Cancellation before callback dispatch reports NotDispatched.
After dispatch, cancellation/deadline/exception reports Unknown unless an actual
result already won. A mutation can commit before its reply is lost; transport
success alone does not prove a semantic disposition. No automatic replay,
replacement idempotency key or PAO-receipt settlement is introduced.

A noncooperative callback still owns its worker, payload budget and shared
dependencies until it actually settles. Finite connection drain does not permit
disposing the store/execution owner underneath it. Domain shutdown can therefore
remain waiting for such a callback; this limitation is deliberate and visible.

## Codex process profile and consumer seam

Official Codex MCP support is local STDIO and Streamable HTTP. A pipe CLI skill
would avoid a persistent adapter, but no admitted native CLI client exists in
this slice and another product's credential cannot substitute for one. A new HTTP
listener or DLL/native injection is outside this work. The source plugin uses
the existing managed semantics executable directly with `stdio`: one adapter
process per Codex connection plus the shared resident host. It verifies the
active strict catalog's exact role path and component version before connecting.
General CLI commands retain the stable launcher. An installer still owns root
substitution, registration and plugin update/admission; none was exercised here.

Setup now prepares an explicitly selected registration change against the
verified package and installed catalog's matching MCP role. It migrates only the
same installation's exact stable-launcher `stdio` route to the selected
`bin/semantics/PegasusCortex.exe` route, and recognizes both managed routes for
removal. Unrecognized aliases, foreign paths, alternate roles and extra arguments
remain conflicts. Migration preserves unrelated TOML, allowed transport options,
inline comments, BOM/newline form and the disabled legacy plugin. A selected
feature retains the other feature's recorded target; ordinary Keep does not
resolve or modify optional integration files. Focused disposable-file fixtures
exercise these behaviors. Installed feature activation remains a separate owner
operation requiring the existing Codex close/lifecycle gate.


The minimal future Cerebral consumer seam is private: explicitly choose native/2
at hello, supply dependency stream IDs, retain one outbound frame writer, keep a
bounded dictionary of pending IDs and one reader that routes replies to those
callers. Support scoped cancel and Unknown without replay. The frozen SDKs and
current native/1 consumer remain unchanged pending owner review/adoption.
