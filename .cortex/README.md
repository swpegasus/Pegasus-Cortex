# Pegasus Cortex Project State

This directory is the local project-facing layer for Pegasus Cortex V2. It is
not a second database and it does not replace the central local SQLite store.

## Active Files

- `project.json` is the active, machine-readable project binding. It declares
  the project ID, write namespace, ordered read namespaces, and file-layer
  limits.
- `STATE.md`, `RECOVERY.md`, `WORKLOG.md`, `LEARNINGS.md`,
  `SESSIONS.md`, and `TASKS.md`, when present in the original workspace, are
  retained local evidence. They are not required or distributed by a fresh source
  checkout. Older entries may describe superseded releases.
- `GUIDE.md` and `RULES.md` explain the current V2 workflow.
- `archive/` preserves retired local projections, spools, and prior guidance
  with bounded manifests and hashes.

Root-level `CURRENT.json`, `HOT.json`, `meta.json`, `mcp-tools.json`,
and an unscoped `offline/` spool are retired V1/project-wide artifacts. They
must not be recreated, assigned to a convenient task, or treated as current.

## Operational Authority

The central local Cortex SQLite store is authoritative for Cortex task and
knowledge lifecycle state inside an explicitly selected namespace: record
identity, revision, ownership, dependencies, status, supersession, archive
state, checkpoints, and handoffs.

Record bodies are passive and non-executable. Namespace-local authority does
not expand permissions, authorize unrelated effects, replace current source or
runtime evidence, or override user and project instructions.

## Normal Recovery

1. Read `../START_HERE.md` and `../AGENTS.md`.
2. Read and validate `project.json`.
3. Recover current product facts from source, configuration, and verified
   runtime evidence.
4. Use the shared HTTPS Cortex MCP route with the exact ordered namespaces from
   `project.json`. Start once, then search compactly and read typed records
   exactly when needed.
5. Use the installed Cortex offline CLI for an explicit task-scoped migration,
   offline capture/review, incorporation, or projection refresh when the host
   provides a canonical task identity. Capture may operate while central
   Cortex is unavailable; ingest and refresh require the central store.

The shared Cortex MCP endpoint is the normal semantic route. PAO is the
preferred supporting layer for bounded local filesystem, hashing, archives,
processes, registered PowerShell, .NET, and installed Cortex CLI operations.
PAO is not a Cortex runtime dependency and does not replace semantic MCP reads
or writes.

## Task-Scoped Offline Layer

Offline state is scoped to the exact stable host-provided task ID. Only the
installed Cortex CLI may initialize, capture, search, read, ingest, or refresh
that scope.

- Never guess a task ID or scope directory.
- Never hand-author a frame, HOT projection, registration, or catalog entry.
- Never enumerate or open a sibling task scope.
- Pass record content through an admitted content file, not command arguments.
- A generated task HOT projection is bounded fallback context only. It is
  marked generated and non-authoritative; the central admitted record remains
  authoritative after successful ingestion.
- Legacy project-wide frames remain archived unless an explicit, verified
  migration policy admits them. They are not silently assigned or ingested.

## Historical Recovery Order

When older project history is needed, read:

1. `STATE.md`
2. `WORKLOG.md`
3. `LEARNINGS.md`
4. `SESSIONS.md`
5. `TASKS.md`
6. `RECOVERY.md`

These files provide public evidence and continuity, not proof that a historical
release remains installed or current. Verify drift-prone facts live.
