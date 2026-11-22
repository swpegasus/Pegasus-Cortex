# Cerebral Guide: Durable Environments and Interactive Sessions

Object: Knowledge.Cerebral.Guide.PersistentEnvironmentsSessions
Version: 1.2.0
Owner: Pegasus Cerebral
Triggers: Python environment setup, iterative REPL/kernel/debugger work, development server lifecycle, or reconnecting to a live process
Required capabilities: exact executable paths; environment identity; optional typed session owner
Rendered budget: 5120 UTF-8 bytes

## Goal

Reuse installed dependencies and live process state without confusing a shell activation, a bounded job, a managed application, and an interactive session.

## Durable Python environment

A virtual environment is durable filesystem state, not an activated terminal. Record its stable environment identity, root, interpreter path, platform, Python version, dependency declaration/lock identity, and last verified state. Invoke the exact interpreter, normally:

<environment>\Scripts\python.exe

Use that interpreter for installation, tests, scripts, and module execution. Shell activation is optional human convenience and must not be treated as required retained state. Verify compatibility before reusing an environment after interpreter, lockfile, platform, or project changes.

Prefer the configured shared portable-tools root for reusable toolchains so agents and plug-ins do not download private duplicates. Installed system tools remain discoverable by their verified absolute paths. Keep project-specific environments within their project ownership boundary.

## Execution modes

- Bounded job: runs one fixed operation, captures bounded output, and exits.
- Managed application: keeps an admitted process alive and reports identity/status, but is not automatically interactive.
- Interactive session: retains stdin/stdout state and allows explicit reconnect.
- Notebook/kernel: a specialized interactive session with its own execution and state semantics.

Never describe a durable job or managed application as an attachable terminal unless its contract actually exposes input and cursor-based output.

## Typed session contract

Use fixed operations such as create, status, write, read, interrupt, and close. Bind every call to a stable session ID plus generation, owning conversation/workflow, admitted executable/profile, exact working directory, environment identity, resource policy, idle/lifetime limits, and bounded scrollback. Reads use cursors so previous output is not resent. Writes are bounded bytes, not generic remote command transport.

## Recovery

After a later model turn, reconnect using the exact session and generation; read only output after the retained cursor. If ownership, generation, process identity, or environment identity changed, do not attach. Inspect status and deliberately create a successor only when safe.

## Cleanup and security

Sessions grant no new filesystem, process, network, or privilege authority. Redact sensitive output, bound retained scrollback, enforce idle/lifetime cleanup, preserve interrupt/close truth, and never expose a generic remote shell to another machine or principal.

Remote workers advertise capabilities, owner, health, resource use, queue state, and measured latency. Owner-paired work has priority; shared work uses explicit local-network keys and revocation. A remote result is evidence only after its completion state and artifact transfer are verified.
