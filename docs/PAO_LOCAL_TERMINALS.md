# Local PAO terminals (source contract v1)

This additive terminal family runs in the existing authenticated signed-in user
execution host. It uses a real Windows ConPTY with PowerShell 7. The source
implementation and isolated tests do not install or activate it in an existing
Cortex installation. Existing finite PAO operations retain their contracts.

## Lifetime and authority

A terminal is one original live shell and its owned Windows job. Its working
directory, environment, variables and functions survive client requests,
disconnect and reconnect while that owner host remains alive. Closing an MCP
adapter or cancelling an output observation does not terminate the shell.

The source provider requires an unelevated interactive user context. Root
arguments, the privileged broker, service/frozen direct execution and System
fallback cannot create or operate a terminal. There is no alternate-user,
administrator, remote, credential-vault or transfer implementation in this
family. No new registration, service or dependency is needed.

A shell working directory is not filesystem confinement. These operations
deliberately submit interactive shell text in the authenticated user's existing
authority. They do not confer the immutable-script, exact-path or collision
guarantees of typed filesystem or finite registered-script operations. Those
existing operations keep their validation and security boundaries.

The PowerShell profile is fixed: no user startup profile, no PSReadLine disk
history, and basic console line input. This is an interactive shell, not
app_start (which discards output) or a sequence of new noninteractive scripts.

Each returned sid binds the owner-host epoch and exact terminal generation.
A different host epoch returns terminal_owner_lost. Reconnect never launches a
replacement shell, restores a prepared environment or resubmits input.
Natural shell exit and explicit close retire the owned process tree.
Host shutdown ends its owned terminals; there is no restart persistence.

New opens and input/control effects require ordinary maintenance admission.
A live terminal remains visible as an active maintenance effect for its whole
native lifetime, without occupying the global mutation scheduler. Reads,
status, history inspection and exact cleanup remain available while fenced.
Unknown selectors remain mutations. The generic reentrant-mutation guard is
unchanged for effects, while the enumerated read-only terminal selectors are
read-only.

## Fixed operations

CLI uses `pao terminal list` without a session selector. Other actions use
`pao terminal ACTION` followed by NAME for connect or SID otherwise.
Additional fields use key=value. send and input receive text through standard
input with --stdin; command text is never an argv argument. send accepts a
single nonsecret line without a trailing newline and adds the shell Return.
input forwards its supplied bytes as UTF-8 without adding Return.

MCP uses the existing single pao tool with terminal_ACTION as its op. Ask pao
help terminal_ACTION for that leaf's fields; no whole catalog is returned in
an ordinary terminal result.

| Operation | Required input | Behavior |
| --- | --- | --- |
| terminal_list | none; optional detail | List the current owner's at most 32 retained generations as sid/name/state. Detail adds cached PID/start only. Includes closed generations until release, even after a name is reused. Never reads output/history or attaches/starts a shell. |
| terminal_connect | name; optional cwd, ws, width, height | Open the fixed PowerShell provider or reattach to the same live named identity. A conflicting live name fails. |
| terminal_status | sid; optional detail | Read actual connection/lifetime state. Requested detail includes cached PID/start, provider and supported lifetime/screen facts. |
| terminal_disconnect | sid | Detach the logical local attachment and refuse new input; keep the original shell and buffer. |
| terminal_reconnect | sid | Reattach to that same generation; never submit input or create a shell. |
| terminal_read | sid; optional cursor, max, fmt | Retrieve an absolute byte range from retained RAM without consuming or executing anything. |
| terminal_send | sid, text; optional rid | Remember and submit a NONSECRET single-line command as a new input attempt. |
| terminal_input | sid, text; optional rid | Forward interactive input without retaining its text as a saved command. This is not a protected credential-entry API. |
| terminal_recall | sid | Retrieve the last saved command, including its current attempt metadata. Unremembered input and interrupts do not replace the saved-command pointer. |
| terminal_input_status | sid; optional iid or original_rid | Read the last input's metadata or one exact original attempt. Explicit missing targets return missing; no history returns empty. The two target selectors are mutually exclusive. |
| terminal_repeat | sid, cid; optional rid | Submit the exact retained command as a NEW intentional attempt in the current prepared session. |
| terminal_resize | sid, width, height | Resize the owned ConPTY. |
| terminal_interrupt | sid; optional rid | Send Ctrl+C to this terminal's input channel. This is a delivery acknowledgement, not command-completion proof. |
| terminal_close | sid | Terminate only the exact owned generation, await confirmed native lifetime/cleanup, then acknowledge closed. A timeout remains incomplete. |
| terminal_release | sid | Forget RAM history for a confirmed inactive generation and reclaim capacity. Active or unconfirmed work cannot be released. |
| terminal_screen | sid | Explicitly report terminal_screen_unsupported. This provider has no terminal-emulator viewport/history projection; a transcript tail is not advertised as a screen. |

Widths are 1..512 cells and heights 1..200; defaults are 100x30. No desktop
window is opened. In a local provider, disconnect means attachment detach,
not a remote network transport operation. Future remote transport controls
must not silently inherit local work-survival guarantees.

## Minimal results and recovery

Terminal CLI (including --json) and MCP return the same requested JSON payload,
apart from MCP's required content wrapper. Terminal output does not add a
routine receipt, input echo, schema/version, hash, catalog or diagnostics.
Existing nonterminal formatting is unchanged.

Examples of payload shapes, not actual execution results:

    {"sid":"HOST_EPOCH:GENERATION","state":"connected"}
    {"state":"disconnected"}
    {"cursor":412,"text":"Build succeeded."}
    {"input":"INPUT_ID","delivery":"delivered","cid":"SAVED_COMMAND_ID"}

The example sid/IDs above are placeholders; real handles are opaque exact
values. Use returned cursor values directly. Input delivered means the pipe
write completed; it does not mean the shell ran or completed a command.
Generic ConPTY output or a prompt does not prove a per-command exit code.
Requested recall/input status therefore reports completion unknown.

Clients establish a RID before sending terminal requests, including before
the first named-pipe frame. An exact repeated delivery RID returns the
original attempt; different payload bytes under that RID fail. A transport
failure after submission exposes unknown delivery and original_rid when
needed. Query terminal_input_status with that original_rid even if the reply
containing iid/cid was lost or subsequent input has occurred. Reconciliation
also works after disconnect/close and never reopens or executes the session.

Input delivery runs independently of output draining. At most one write is
pending per terminal. The caller observes it for at most two seconds;
observation timeout/cancellation leaves the original host-owned write
tracked, never cancels or resends an uncertain suffix. Inspect the original
attempt. Delivery errors remain unknown rather than invented as not-sent or
completed. A new input cannot pass a pending write.

RecallLast and RunAgain are separate. terminal_recall only reads.
terminal_repeat takes the saved cid, not regenerated command text. It
allocates a new input attempt and RID, links reRunOf to the previous attempt
and preserves earlier delivery records. Files referenced by an interactive
command are read in their current state; no old file is restored. A deliberate
new attempt is not reconciliation of an uncertain old one.

## Bounded memory and bytes

There are at most eight active and 32 retained generations per owner host.
Each has a 1 MiB output ring, at most 64 saved nonsecret commands and 256
delivery attempts. Capacity errors are explicit. Close and release inactive
generations to reclaim session capacity. Use `terminal_list` after reconnecting
or losing a client-side SID to discover actual current-owner generations; the
returned SID still has to be used for exact close/release. This list is owner
local and host-lifetime only, not a persisted inventory restored after reboot.
Saved-command/attempt capacity does
not silently discard RID identities and then execute an old retry as new.

No raw terminal stream, command history, interactive input or terminal response
is written to the generic job database, its recovery queue, continuity
records or disk logs by these operations. The terminal route bypasses durable
Claim/Finish/SqliteJobStore entirely. Input text is carried in the existing
nonserialized Body. The bounded RAM buffer lasts only for this owner host.
Do not put secrets in remembered commands. Unremembered input does not promise
that an arbitrary program will not echo it; credential handling is outside
this phase.

Cursors count original output bytes. max is 4..65536 (default 4096) bytes of
retained output. fmt=text returns UTF-8 text and does not split a trailing
scalar while more output may arrive. At EOF an incomplete scalar advances
with explicit encodingLoss rather than leaving a stuck cursor. Invalid
UTF-8 similarly reports encodingLoss; fmt=base64 retrieves exact retained
bytes. After eviction, gap and earliest are explicit; reads cannot recover
discarded bytes. more and ended are included when material. The stream can
contain VT control sequences; there is no claimed rendered-screen parser.

## Implementation and qualification

Production ownership is in Engine/LocalTerminalManager.cs,
ConPtyTerminalProcess.cs, TerminalOutputBuffer.cs and
AgentOpsEngine.Terminals.cs, with narrow protocol, dispatch, maintenance and
CLI/MCP integration. The real ConPTY child is suspended until exact Windows
job ownership is established. Explicit null standard handles prevent a
redirected parent's handles from escaping the pseudoconsole. Input and output
are serviced separately; output remains drained during ClosePseudoConsole.
PID/start are cached before process-handle disposal, and close awaits the
same lifetime task used by ownership and release.

Microsoft's ConPTY lifecycle documentation and its maintainer discussion of
redirected parent handles informed these boundaries:

- https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session
- https://github.com/microsoft/terminal/discussions/15814

The focused console driver is tests/Pegasus.AgentOps.Terminal.Tests. It uses
only isolated source/test roots and emits sanitized fixture facts, not
terminal transcripts. It separates deterministic buffer/contract checks,
a real local named-pipe lost-reply fixture, CLI/MCP projection checks, real
ConPTY prepared state/rerun/cleanup checks and owner-host disposal. Qualified
reports and original failed fixtures are retained under the task-owned
artifacts/validation/pterm-81be leaf. These are source-runtime facts, not an
installed rollout, provider benchmark, remote test or elevated-session test.
