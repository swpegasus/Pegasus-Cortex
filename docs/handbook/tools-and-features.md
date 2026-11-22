# Tool and feature map

Cortex has two complementary jobs: retain useful project context and provide
predictable ways to work with local resources. The installed catalog is the
detailed operation reference. This map explains why the groups exist and when
they are useful, without asking a newcomer to learn every command first.

```text
Cortex
  Continuity
    Tasks and checkpoints
    Knowledge and provenance
    Project / shared / personal spaces
  Retrieval
    Small start brief
    Search previews -> exact selected reads
    Deterministic knowledge geometry
  PAO local operations
    Files / text / hashes / archives
    Developer programs / scripts / Git / .NET / recipes
    Processes / applications / network observations
    Persistent local terminals
    Windows administration
    UI automation
      Standard windows and controls
      AutoItX provider
  Operations and connection
    Jobs / diagnostics / feedback / usage
    Settings / work queue / operation browser
    Shared HTTPS MCP / authenticated native connection
```

An advertised operation can still be unavailable because its provider,
prerequisite or authorized execution route is missing. Discovery and readiness
are separate from permission to act.

## Continuity and retrieval

Tasks preserve what work is being done and where it stopped. Knowledge preserves
useful corrections and decisions. Briefs, previews and exact reads let the
client select what belongs in its immediate working context. See
[context and retrieval](context-and-retrieval.md) for the full example.

Use this group when returning to a project, recovering after context compaction,
or sharing project state across conversations. It does not turn a retained
statement into automatic truth or permission.

## Files text hashes and archives

These operations inspect directories and files, return bounded text, search,
handle supported encodings, make explicit edits, copy or move material, compute
hashes and work with supported archives. Exact paths, collision policies and
bounded results make a request easier to understand and recover than an
unstructured sequence of shell commands.

For example, a file inspection request might be:

```json
{
  "op": "stat",
  "src": "C:\\Projects\\Example\\README.md",
  "cwd": "C:\\Projects\\Example",
  "output": "agent"
}
```

The path is illustrative. Use a real admitted path and the installed
operation-specific help. A returned file size describes that observation; it
does not prove the file's future contents or authorize an edit.

Compact results aim to return useful information without routine receipt
boilerplate. Accepted/running work still needs a job identifier; failures,
partial results and uncertain effects still need recovery information. Explicit
full structured output is available for machine consumers. See the
[failure contract](../PAO_FAILURE_CONTRACT.md) and the prepared build's own help
because source changes can be newer than a download.

## Developer programs and recipes

Registered tools, PowerShell scripts, Git and .NET operations support bounded
development work. A recipe can describe an admitted repeatable workflow.
Arguments are supplied separately and should stay tied to the selected project.

Use finite typed operations for a build, a known script or a repository check.
They offer stronger explicit-path and invocation contracts than arbitrary shell
text. A successful process exit is evidence about that process; the result of
the application being built may need its own test.

## Processes applications and observations

Process metadata, trees, threads, waits and deliberately requested stops support
diagnosis and application lifecycle work. Network observations help establish
what is listening. Application controls track a selected owned application
rather than treating any matching name as the same process forever.

An exact process generation matters: Windows can reuse a process identifier.
Never treat an old PID or a familiar window title as enough evidence to stop or
control a replacement process.

## Persistent local terminals

A terminal keeps a live shell, its working directory and shell state across
separate client requests. Cursor-based output reads return new output instead
of requiring the entire transcript again. This is useful for interactive or
multi-stage work where recreating the environment is costly.

The shell runs in the ordinary signed-in owner context. Disconnect/reconnect
does not recreate it or resend a command. Host exit, restart or reboot ends it.
Output buffers are bounded and the terminal is not a permanent disk transcript.
Recall and intentional repeat are different actions.

Terminal shell text has the shell's own semantics and risks; it is not equivalent
to finite typed operations with exact-path and registered-script guarantees.
Read [the terminal guide](../PAO_LOCAL_TERMINALS.md) before automating input.
Integrated remote terminal providers remain a separate capability; an operator
using an external SSH program inside a local shell does not establish one.

## Windows administration

Fixed contracts cover supported registry, certificate, service, scheduled-task,
installed-program, device, network, account and connection-profile operations.
They are useful when a workflow needs a specific machine observation or change.

Ordinary work uses the user Host. Protected operations retain their own service
or fixed UAC admission. Availability is not authorization, and an access denial
is not a reason to silently reroute ordinary work as LocalSystem.

Certificate tools and MCP connection setup have different responsibilities.
Certificate tools create or inspect appropriate material through their defined
routes; the connection's TLS check establishes whether the selected server
certificate works for that endpoint. Neither a public CA certificate nor a
server certificate is a publisher private signing key. See
[certificates](../PAO_CERTIFICATES.md) and [shared MCP](../CORTEX_SHARED_MCP.md).

## UI automation and AutoItX

When an application offers a suitable API or documented unattended mode, prefer
it. For a standard Windows program without that route, PAO provides a small set
of typed window and control actions. AutoItX is the admitted product-local
provider behind those actions, loaded without COM registration.

The useful sequence is:

1. Check provider readiness with `ui_status`.
2. Resolve exactly one window with `ui_find`.
3. Retain its actual window handle, PID and process-start identity.
4. Inspect a standard control and retain the actual control handle.
5. Request the intended action and inspect its verified or uncertain outcome.

The fixed surface can inspect, activate and move windows; inspect and focus
controls; click or set appropriate nonsecret text; and capture the exact window
to an admitted PNG file. A capture result returns metadata and a hash, not an
automatic upload of the pixels.

This is useful for a developer utility or installer that cannot be driven more
directly. It is not arbitrary AutoIt scripting: the public contract does not
expose general macros, global keyboard/mouse injection, OCR, clipboard access,
credential entry, remote desktops or Session 0 automation. Custom-rendered
controls may not support the requested action. It runs in the signed-in
interactive session, not on the LocalSystem desktop.

For exact fields and synthetic examples, see [UI automation](../execution/UI_AUTOMATION.md)
and [UI requests](../../examples/ui-automation/README.md). Always resolve live
identities; never copy fixture handles or process identifiers into real actions.

## Jobs feedback and the human interface

The work queue and job operations distinguish accepted, running, done, failed,
partial and uncertain outcomes. Inspect an existing job before retrying work
whose response was lost. Cancellation requests do not prove that an effect was
undone. Logs, diagnostics and feedback help investigate an actual failure;
usage aggregates show recorded operations rather than whether an answer was
useful.

The read-only operation browser helps a person discover categories and their
advertised contracts. Settings and statistics make configuration and observed
activity easier to inspect. A displayed configuration is not proof that a
listener is running or a provider is ready.

Start with the [documentation index](../README.md), then use the installed
catalog and `pao help` for the operation and build you are actually using.
