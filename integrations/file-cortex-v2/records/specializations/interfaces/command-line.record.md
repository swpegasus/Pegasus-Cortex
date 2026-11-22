# Command-Line Interface Tenets

These tenets specialize `CORE-FAIL-002`, `CORE-FAIL-003`, `CORE-WORK-003`, `SEC-006`, and `PORT-*` for command-line programs and portable shell automation; PowerShell- and CMD-specific deltas remain in their own files.

## CLI-001 — Preserve the command-line contract

**Rule:** Standard output carries requested data, standard error carries diagnostics, and exit status truthfully represents the outcome.

**Practice:** Keep human formatting optional when callers need stable structured output.

**Boundary:** Interactive programs may share a terminal while still offering a machine-readable noninteractive mode when automation is supported.

## CLI-002 — Preserve argument boundaries

**Rule:** Invoke programs with argument vectors rather than reconstructed shell strings.

**Practice:** Avoid `eval`, reparsing shells, and interpolation of untrusted text into executable command lines.

**Boundary:** Explicit shell language is valid when the shell expression itself is trusted source and inputs remain separately quoted or passed.

## CLI-003 — Handle arbitrary filenames

**Rule:** Do not assume paths lack spaces, newlines, leading hyphens, Unicode, glob characters, or reserved bytes.

**Practice:** Use path APIs, `--` where supported, structured output, or null-delimited transfer for enumeration.

**Boundary:** A validated constrained namespace may document and enforce narrower filenames.

## CLI-004 — Make automation predictably noninteractive

**Rule:** Unattended execution must not hang on a prompt or silently accept a destructive default.

**Practice:** Select interactive mode explicitly and require supplied confirmation or fail with an actionable result in automation.

**Boundary:** Human-first tools may remain interactive when they do not advertise unattended operation.

## CLI-005 — Control environment-dependent inputs

**Rule:** Locale, timezone, encoding, working directory, environment variables, shell, and required executable capabilities must be explicit where they affect behavior.

**Practice:** Feature-detect rather than assuming one vendor's command options or display format.

**Boundary:** A deliberately fixed runtime can validate one known environment at startup.

## CLI-006 — Preserve failure through pipelines and chaining

**Rule:** Later successful commands must not hide an earlier required failure.

**Practice:** Inspect pipeline semantics, native exit codes, and the exact command whose outcome is being tested.

**Boundary:** Best-effort multi-item workflows may continue while collecting an explicit partial result.

## CLI-007 — Restore terminal state on signal-driven exit

**Rule:** A command-line program that changes terminal modes or installs signal handling must define how normal exit, cancellation, and supported signals restore usable terminal state.

**Practice:** Keep original terminal settings, use scoped handlers, preserve the material exit status, and make repeated restoration harmless.

**Boundary:** `CONC-008` and `CORE-FAIL-006` own general resource shutdown; a forceful process kill may prevent in-process restoration, so avoid leaving the terminal dependent on it when an external recovery mechanism is practical.

## CLI-008 — Keep text and binary modes explicit

**Rule:** Commands must distinguish text encoding, line endings, byte streams, and terminal formatting at each boundary.

**Practice:** Disable colors or progress control codes in machine output unless explicitly requested.

**Boundary:** Terminal-only presentation may use rich formatting when it cannot contaminate redirected data.
