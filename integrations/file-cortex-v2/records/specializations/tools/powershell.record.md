# PowerShell Tenets

These tenets specialize `CLI-001`, `CLI-002`, `CLI-006` through `CLI-008`, `PORT-005`, `CORE-WORK-003`, and `CORE-VERIFY-003` for PowerShell's object pipeline, streams, parsing, and native-process boundaries.

## PS-001 — Treat pipeline values as objects

**Rule:** Use properties, types, and structured serialization instead of parsing formatted display output.

**Practice:** Select or filter object properties before formatting; reserve `Format-*` cmdlets for final human presentation.

**Boundary:** Parse text only when the upstream program provides no stable structured interface, and validate the format explicitly.

## PS-002 — Use literal paths for supplied targets

**Rule:** Prefer `-LiteralPath` when a path is already known and wildcard expansion is not intended.

**Practice:** Resolve and validate paths with .NET or PowerShell path APIs before mutation.

**Boundary:** Use wildcard-aware parameters only when pattern matching is an explicit feature.

## PS-003 — Preserve native argument boundaries

**Rule:** Invoke native executables with separate, deliberate arguments rather than constructing an executable command string.

**Practice:** Use the call operator or process APIs with a known executable, validated arguments, working directory, environment, and timeout.

**Boundary:** Avoid `Invoke-Expression`; never interpolate untrusted text into a reparsed command line.

## PS-004 — Distinguish PowerShell and native failures

**Rule:** PowerShell errors and native process exit codes require separate handling.

**Practice:** Use terminating behavior at important PowerShell boundaries, catch expected exceptions, and inspect `$LASTEXITCODE` immediately after relevant native commands.

**Boundary:** `$?` alone is not durable diagnostic evidence for a multi-command operation.

## PS-005 — Use output streams intentionally

**Rule:** Keep machine-readable output separate from diagnostics and human commentary.

**Practice:** Return objects on the success stream and use error, warning, information, verbose, or debug streams according to meaning.

**Boundary:** Do not mix decorative text into output that another command or process must parse.

## PS-006 — Make encoding explicit across boundaries

**Rule:** Specify text encoding when files cross PowerShell versions, operating systems, build tools, or external processes.

**Practice:** Select an encoding supported by every consumer and verify round-trip behavior for non-ASCII content.

**Boundary:** Do not assume the shell's console encoding, default file encoding, and child-process encoding are identical.

## PS-007 — Scope temporary state

**Rule:** Location, environment, preferences, modules, and credentials changed for one operation should not leak into later work.

**Practice:** Use bounded scopes, `Push-Location`/`Pop-Location`, saved prior values, and `try/finally` restoration.

**Boundary:** Permanent user or machine configuration changes require explicit intent and rollback information.

## PS-008 — Design destructive functions for review

**Rule:** Destructive PowerShell operations should be idempotent where practical and expose their intended targets before acting.

**Practice:** Use `SupportsShouldProcess`, `-WhatIf`, confirmation, exact target enumeration, and content-safe diagnostics for reusable tools.

**Boundary:** Confirmation prompts do not replace path validation, containment, or authority.

## PS-009 — Prefer reusable scripts over interactive shorthand

**Rule:** Saved automation should use explicit cmdlet names, named parameters, validation, functions, and clear exit behavior.

**Practice:** Use aliases and abbreviated syntax only for disposable interactive work.

**Boundary:** Conciseness is valuable only while behavior remains reviewable and portable across intended PowerShell versions.

## PS-010 — Quote interpolation boundaries deliberately

**Rule:** Choose single quotes, double quotes, here-strings, format operations, or argument arrays according to whether expansion is intended.

**Practice:** Pass multiline source or data through one deliberately literal, non-reparsed input channel when interpolation is not required.

**Boundary:** Never solve quoting difficulty by adding an extra shell or evaluation layer without understanding its parsing rules.

## PS-011 — Avoid accidental quadratic collection work

**Rule:** Choose streaming, pipeline output, or mutable collection types for large or repeated accumulation.

**Practice:** Avoid repeatedly rebuilding large arrays with `+=` inside loops when a list, direct output, or producer pipeline is clearer.

**Boundary:** Optimize measured or obviously scaling paths; do not obscure small scripts for theoretical micro-performance.
