# CMD Tenets

CMD remains useful for small Windows compatibility and bootstrap scripts. Prefer PowerShell or an application language when work requires structured data, complex control flow, robust diagnostics, or security-sensitive parsing.

These tenets specialize `CLI-001`, `CLI-002`, `CLI-005`, `CLI-006`, `CORE-AUTH-006`, and `CORE-WORK-003` with CMD-specific parsing, path, and exit-code behavior.

## CMD-001 — Keep batch scripts narrowly scoped

**Rule:** Use CMD for concise launch, compatibility, environment, and build-entry tasks.

**Practice:** Move complex parsing, data transformation, networking, or state management into a more suitable language.

**Boundary:** Existing stable batch automation need not be rewritten solely because another language is available.

## CMD-002 — Localize environment changes

**Rule:** Reusable batch scripts should prevent variables and command settings from leaking to the caller.

**Practice:** Use `setlocal` and a deliberate exit path, returning the intended status after cleanup.

**Boundary:** An explicit caller-facing environment change should be documented and separated from ordinary internal variables.

## CMD-003 — Resolve script-relative paths explicitly

**Rule:** Do not assume the caller's current directory is the script directory.

**Practice:** Resolve owned resources from the script location and use `pushd`/`popd` for bounded working-directory changes.

**Boundary:** Use the caller's directory only when it is part of the script's declared interface.

## CMD-004 — Preserve quoting and argument boundaries

**Rule:** Quote path arguments and understand when CMD reparses metacharacters, variables, and nested command lines.

**Practice:** Avoid concatenating untrusted text into `cmd /c`, `call`, parenthesized blocks, or shell operators.

**Boundary:** Quoting alone cannot make arbitrary shell text safe; use a non-shell process API for untrusted arguments.

## CMD-005 — Capture failure immediately

**Rule:** Inspect `ERRORLEVEL` directly after the command whose result matters and return meaningful nonzero codes.

**Practice:** Save the value before another command can replace it and make chaining semantics explicit.

**Boundary:** `if errorlevel N` means greater than or equal to `N`; use an explicit numeric comparison when exact equality matters.

## CMD-006 — Treat delayed expansion as a data transformation

**Rule:** Enable delayed expansion only when block-time variable expansion requires it.

**Practice:** Account for literal exclamation marks and disable delayed expansion while reading data that must preserve them.

**Boundary:** Parenthesized blocks, pipes, and `call` can introduce additional parsing passes; verify rather than guessing.

## CMD-007 — Avoid localized display parsing

**Rule:** Do not depend on human-oriented command output when a stable API, exit code, or machine-readable format exists.

**Practice:** Prefer supported command switches, PowerShell objects, system APIs, or purpose-built tools.

**Boundary:** When parsing is unavoidable, define supported locales and versions and fail clearly on unknown formats.

## CMD-008 — Chain commands deliberately

**Rule:** Use `&&`, `||`, and grouping only when their success and failure propagation matches the intended workflow.

**Practice:** Preserve the first material failure and avoid a later successful cleanup command hiding it.

**Boundary:** Cleanup should still run when required; save the original status and restore it at exit.

## CMD-009 — Keep special-character handling reviewable

**Rule:** Treat `&`, `|`, `<`, `>`, `^`, `%`, `!`, parentheses, and quotes as syntax unless deliberately escaped for the current parsing phase.

**Practice:** Minimize nested parsing and use small helper scripts instead of dense escape sequences.

**Boundary:** A command that works interactively may behave differently in a `.cmd` file because percent expansion and parsing phases differ.
