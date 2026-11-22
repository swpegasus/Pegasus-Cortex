# Windows Platform Tenets

These tenets specialize `CORE-AUTH-003`, `CORE-FAIL-004`, `CORE-VERIFY-004`, `CORE-WORK-003`, `PORT-*`, `REL-*`, and `CONFIG-*` for Windows ownership, lifecycle, and platform behavior.

## WIN-001 — Distinguish Windows scopes

**Rule:** Process, user, interactive session, service account, machine, and domain scopes are different ownership and security boundaries.

**Practice:** State which identity and scope owns configuration, files, processes, credentials, environment, and UI interaction.

**Boundary:** Administrator access does not make another user's profile or session interchangeable with the current one.

## WIN-002 — Keep the elevated Windows boundary explicit

**Rule:** Cross into elevation through an explicit Windows process or service boundary with a known executable, bounded arguments, and a result channel.

**Practice:** Keep planning, validation, and ordinary processing unprivileged; distinguish same-account UAC consent, alternate-credential elevation, integrity level, service identity, and interactive-session identity before performing the typed elevated effect.

**Boundary:** Do not run an entire application elevated merely because one setup action needs machine authority.

## WIN-003 — Distinguish Windows registration from runtime state

**Rule:** File installation, Registry or service registration, Service Control Manager state, restart-pending state, and post-restart availability are distinct Windows lifecycle facts.

**Practice:** Apply `REL-002` and `RELEASE-007` after verifying the platform-specific registration and restart state required by the component.

**Boundary:** A registered or running Windows component is not thereby ready or functionally correct.

## WIN-004 — Prefer supported APIs and stable identifiers

**Rule:** Use Windows APIs, service interfaces, event sources, and stable identifiers instead of UI automation or localized display text where possible.

**Practice:** Use UI automation only when the UI is the actual boundary being tested or no supported interface exists.

**Boundary:** A stable API may still require version and capability detection.

## WIN-005 — Detect platform capabilities explicitly

**Rule:** Detect OS version, edition, architecture, process architecture, feature availability, and policy rather than assuming the development machine's shape.

**Practice:** Fail clearly or select a supported adapter when a capability is absent.

**Boundary:** Version strings alone may not establish feature availability or policy permission.

## WIN-006 — Keep environment changes scoped

**Rule:** Process-, user-, and machine-level environment changes have different persistence and authority.

**Practice:** Prefer process-local changes for temporary work and record restart or process-reload requirements for persistent changes.

**Boundary:** Changing a registry-backed environment value does not update already running processes automatically.

## WIN-007 — Handle Windows paths deliberately

**Rule:** Distinguish absolute drive paths, drive-relative paths, UNC paths, device paths, and redirected locations.

**Practice:** Reject `C:path` when `C:\path` is required, support UNC deliberately, account for links, junctions, and mount points, and compare names according to the actual filesystem and directory mode.

**Boundary:** Do not infer OS architecture from filesystem layout or current-process redirection.

## WIN-008 — Preserve ACL and ownership semantics

**Rule:** Copy, replacement, installation, and migration must define whether ACLs, ownership, inheritance, alternate streams, and metadata are preserved or recreated.

**Practice:** Verify the resulting access from the intended service or user identity.

**Boundary:** Successful file copying does not prove the consuming identity can safely use the result.

## WIN-009 — Treat locks and sharing violations as real state

**Rule:** A locked file may indicate active ownership, durability work, antivirus scanning, or another legitimate consumer.

**Practice:** Identify the owner, use bounded retries when appropriate, coordinate shutdown, and preserve the original failure.

**Boundary:** Do not force-delete or terminate broadly matched processes merely to free a file.

## WIN-010 — Identify processes precisely

**Rule:** Process names alone are insufficient for sensitive inspection or termination.

**Practice:** Confirm PID, executable path, start time, command line where safe, ownership, parentage, port, or another stable association.

**Boundary:** PID reuse means a previously observed PID must be revalidated before later destructive action.

## WIN-011 — Manage services through service semantics

**Rule:** Use the Service Control Manager or supported service APIs rather than treating a service as an ordinary process.

**Practice:** Preserve startup type, account, dependencies, recovery settings, and prior running state unless changing them is requested; wait for transitions with a timeout and then verify health.

**Boundary:** Killing the backing process may trigger recovery policy and does not constitute a controlled stop.

## WIN-012 — Make restart requirements explicit

**Rule:** Sign-out, service restart, application restart, and machine reboot are distinct outcomes that must not be hidden side effects.

**Practice:** Explain why the restart is required, what state must be preserved, and how success will be verified afterward.

**Boundary:** Do not restart a machine or shared service solely to simplify diagnosis without authorization.

## WIN-013 — Control executable and library resolution

**Rule:** Privileged, installer, and service launches should resolve verified executables and libraries without depending on the current directory, file association, ambient `PATH`, or unsafe DLL search order.

**Practice:** Use explicit executable paths, safe process APIs, controlled working directories, and supported DLL-loading controls; validate any allowed search locations.

**Boundary:** User-facing command discovery may intentionally use `PATH`, but the selected executable still needs identity and trust validation before sensitive use.
