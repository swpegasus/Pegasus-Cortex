# Linux Platform Tenets

These tenets specialize `PORT-*`, `CLI-*`, `CORE-AUTH-003`, `CORE-WORK-003`, and `CORE-FAIL-006` for Linux behavior.

## LINUX-001 — Name the intended interpreter

**Rule:** Scripts must declare and use the shell or interpreter whose syntax and behavior they require.

**Practice:** Use an appropriate shebang, supported line endings, executable permission, and declared runtime dependencies.

**Boundary:** A script invoked explicitly through its interpreter need not rely on executable-bit or shebang behavior.

## LINUX-002 — Preserve user, group, mode, and umask semantics

**Rule:** File and process access should use the narrow owner, group, mode, ACL, and creation mask required by the behavior.

**Practice:** Repair ownership causes rather than applying broad permissions such as world-writable access.

**Boundary:** Shared public content may intentionally permit broad read access while protecting mutation.

## LINUX-003 — Treat privilege escalation as a narrow operation

**Rule:** Treat the effective user, login identity, `sudo` policy, preserved environment, home directory, and service account as separate Linux execution inputs.

**Practice:** Run the smallest necessary command with a known executable and bounded environment, and make file ownership and credential lookup follow the intended effective identity.

**Boundary:** Elevation does not make the caller's home, environment, agent, or credential files appropriate for the elevated identity; a dedicated privileged service may own repeated effects behind a narrow typed interface.

## LINUX-004 — Manage signals and process groups deliberately

**Rule:** Graceful termination, deadlines, child ownership, process groups, and forced kill are distinct lifecycle concerns.

**Practice:** Forward intended signals, reap children, bound shutdown, and report incomplete termination.

**Boundary:** Daemonization and supervisors may change parent-child relationships; use the actual service model.

## LINUX-005 — Treat service context as explicit

**Rule:** A service's account, environment, working directory, limits, filesystem access, and readiness differ from an interactive shell.

**Practice:** Use the platform service manager and verify the real capability after start or reload.

**Boundary:** A foreground development process is not service-installation evidence.

## LINUX-006 — Respect Linux path and mount semantics

**Rule:** Case, symlinks, bind mounts, namespaces, ownership, executable bits, and mount boundaries are functional behavior.

**Practice:** Resolve and contain mutation targets at the actual I/O boundary and avoid crossing mounts unintentionally.

**Boundary:** Lexical normalization alone cannot prove the resolved destination under writable links.

## LINUX-007 — Use standard configuration and state locations deliberately

**Rule:** System configuration, user configuration, cache, runtime state, logs, and durable application data should follow their intended ownership and lifecycle.

**Practice:** Respect platform directory conventions and environment overrides without mixing cache with authoritative state.

**Boundary:** Self-contained portable applications may use an explicit owned root when that behavior is documented.

## LINUX-008 — Account for distribution and tool variation

**Rule:** Package names, service defaults, library versions, command options, and filesystem layout vary across supported distributions.

**Practice:** Declare the supported matrix and feature-detect capabilities instead of parsing one distribution's display output.

**Boundary:** A single-distribution product may optimize for its declared target and reject unsupported environments clearly.

## LINUX-009 — Respect distribution-owned files

**Rule:** Do not manually overwrite or delete package-managed binaries, libraries, service units, or configuration files as though they were application-owned.

**Practice:** Use the package manager or supported drop-in and override mechanisms, and keep locally installed artifacts under an explicitly owned prefix.

**Boundary:** Emergency repair may temporarily replace a distribution-owned file only with explicit authority, captured prior state, and a plan to reconcile package ownership.
