# Playing and Interactive Testing

This file covers broadly applicable human and automated evaluation of games, simulations, media tools, and other interactive software.

These tenets specialize `CORE-VERIFY-004`, `CORE-VERIFY-005`, `CORE-VERIFY-006`, `CORE-FAIL-005`, `UI-*`, and `PERF-*` for interactive runtime evidence.

## PLAY-001 — Use the actual user-launched artifact

**Rule:** Interactive validation should identify and launch the executable or package users are expected to run.

**Practice:** Record the artifact, version or digest, configuration, architecture, platform, and input mode.

**Boundary:** Launching a development host, editor preview, or parent bootstrapper may not test the shipped application process.

## PLAY-002 — Separate interactive subsystems

**Rule:** Launch, rendering, input, audio, networking, persistence, performance, and shutdown are separate evidence areas.

**Practice:** Record which were observed and which were outside the session.

**Boundary:** A visible first frame does not prove responsiveness, persistence, network correctness, or clean exit.

## PLAY-003 — Use representative workflows

**Rule:** Test the actions users repeatedly perform and at least one meaningful completion or recovery path.

**Practice:** Include setup, normal interaction, error feedback, state persistence, and exit appropriate to the product.

**Boundary:** Avoid exhaustive exploratory play when the current goal is a focused regression check.

## PLAY-004 — Isolate mutable user state

**Rule:** Interactive tests that can change profiles, saves, settings, inventories, cloud state, or accounts should use isolated test identities and recoverable data.

**Practice:** Capture the starting state, avoid real user data, and clean up or preserve failures for diagnosis.

**Boundary:** Do not delete or reset existing user state merely to obtain a clean test environment.

## PLAY-005 — Exercise interruption and lifecycle

**Rule:** Interactive software should handle focus loss, pause, resume, cancellation, device change, window close, and orderly shutdown according to its contract.

**Practice:** Verify that resources, saves, processes, ports, and child operations reach an understood final state.

**Boundary:** Forced termination is useful failure testing but not proof of graceful shutdown.

## PLAY-006 — Record the conditions of human observation

**Rule:** Qualitative interactive findings must identify the observer role, scenario, input method, display or audio conditions, and relevant accessibility setup.

**Practice:** Apply `UI-009` to distinguish automated evidence, engineer observation, representative user evaluation, and formal acceptance.

**Boundary:** One observer's impression is useful evidence but not a universal user conclusion.

## PLAY-007 — Capture evidence narrowly

**Rule:** Preserve enough logs, screenshots, recordings, traces, and reproduction steps to diagnose failures without collecting unnecessary private data.

**Practice:** Correlate evidence with the exact artifact and session and trim unrelated content.

**Boundary:** Reference media, generated previews, or editor output are not automatically runtime assets or package evidence.

## PLAY-008 — Observe representative performance

**Rule:** When performance is part of interactive acceptance, execute the representative scenario and environment defined by `PERF-002` and observe the lifecycle phases defined by `PERF-006`.

**Practice:** Record resolution, workload, device class, runtime mode, warm-up, frame pacing, latency, memory, CPU or GPU behavior, and the interval relevant to the claim.

**Boundary:** A single average metric can hide stalls, warm-up, thermal limits, and tail latency; choose metrics proportionate to the claim.
