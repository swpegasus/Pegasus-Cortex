# Portability, Time, and Encoding

These tenets generalize recurring cross-platform concerns and are specialized further by `ARM64-*`, `WIN-*`, and `LINUX-*` guidance.

## PORT-001 — Detect capabilities rather than infer them

**Rule:** Query the operating system, runtime, architecture, filesystem, device, and dependency capabilities that behavior actually requires.

**Practice:** Choose supported paths explicitly and report degraded or unavailable capabilities.

**Boundary:** A deliberately single-platform product may validate one declared environment instead of carrying unused fallbacks.

## PORT-002 — Isolate platform behavior behind owned adapters

**Rule:** Keep platform APIs, native libraries, paths, devices, and service managers out of portable domain logic.

**Practice:** Define a small behavior-focused interface and test portable logic independently.

**Boundary:** Avoid an abstraction that merely renames one call without creating a useful ownership boundary.

## PORT-003 — Use monotonic time for durations

**Rule:** Measure elapsed time and deadlines with a monotonic clock rather than a wall clock that can jump.

**Practice:** Keep start, duration, and timeout units explicit.

**Boundary:** Persisted human timestamps and shared lease expiry still require an authoritative wall-time source.

## PORT-004 — Make time zones and calendar semantics explicit

**Rule:** Store instants, local calendar values, offsets, zones, and recurrence rules as different concepts.

**Practice:** Convert for display at the edge and preserve the intended zone when future calendar interpretation matters.

**Boundary:** UTC alone does not encode a user's recurring local schedule.

## PORT-005 — Make text encoding explicit at boundaries

**Rule:** Files, streams, protocols, databases, and subprocesses must agree on encoding and invalid-sequence behavior.

**Practice:** Prefer Unicode encodings with explicit decoding, normalization policy where needed, and round-trip tests.

**Boundary:** Do not normalize identifiers, signatures, or user content unless the contract explicitly requires it.

## PORT-006 — Treat locale as behavior, not decoration

**Rule:** Parsing, formatting, sorting, casing, decimal separators, and messages must use the intended culture or invariant rules explicitly.

**Practice:** Keep machine formats invariant and localize user-facing presentation at the boundary.

**Boundary:** Linguistic collation and identifier comparison require different rules.

## PORT-007 — Preserve numeric and binary semantics

**Rule:** Width, signedness, precision, overflow, byte order, alignment, and floating-point behavior must be explicit across languages and machines.

**Practice:** Use fixed-width or decimal representations where the contract requires them and validate conversion ranges.

**Boundary:** Internal calculations may use native types when no boundary or precision ambiguity exists.

## PORT-008 — Treat filesystem semantics as platform behavior

**Rule:** Case, separators, reserved names, links, permissions, locking, mount boundaries, and maximum component lengths vary and affect correctness.

**Practice:** Use path APIs, declare supported semantics, and test on representative filesystems.

**Boundary:** Do not emulate every platform when the supported matrix is intentionally narrow.

## PORT-009 — Separate buildability, emulation, and native runtime evidence

**Rule:** Cross-compilation or emulation does not prove native packaging, loading, performance, devices, or installation.

**Practice:** Label evidence by architecture, OS, runtime, and execution mode.

**Boundary:** Emulated evidence remains valuable when native hardware is unavailable and the limitation is explicit.

## PORT-010 — Govern the supported-platform matrix

**Rule:** Supported platforms, architectures, runtimes, filesystems, and deployment modes need a declared matrix with introduction, deprecation, and removal behavior.

**Practice:** Apply `API-005`, then use capability detection, compatibility tests, migration guidance, usage evidence, and vendor-support status before changing the matrix.

**Boundary:** Security or vendor support loss may shorten the window with an explicit decision and mitigation.

## PORT-011 — Choose randomness by purpose

**Rule:** Cryptographic unpredictability, statistically appropriate simulation, and reproducible seeded behavior are different requirements and need different random sources.

**Practice:** Use a cryptographically secure generator for secrets and security tokens, a suitable statistical generator for modeled randomness, and an explicit recorded seed for reproducible tests or simulations.

**Boundary:** A deterministic seed is evidence for replay, not security; a cryptographic generator does not by itself make a simulation statistically valid.
