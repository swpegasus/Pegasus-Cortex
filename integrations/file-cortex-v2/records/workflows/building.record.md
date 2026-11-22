# Building Software

Artifact freshness, combined checkpoint builds, generated-output boundaries, dependency inputs, release promotion, and diagnostic evidence are defined canonically in `CORE-VERIFY-003`, `CORE-VERIFY-005`, `CORE-VERIFY-008`, `CORE-WORK-002`, `CORE-WORK-007`, `CORE-FAIL-003`, `DEP-*`, and `RELEASE-*`.

## BUILD-001 — Use the canonical build entry point

**Rule:** Build through the repository's supported script, project, workspace, or orchestration command.

**Practice:** Read current instructions and configuration instead of assembling an ad hoc command that skips required generation, validation, or packaging steps.

**Boundary:** A targeted project build is appropriate during iteration when it exercises the changed dependency surface correctly.

## BUILD-002 — Distinguish build lifecycle evidence

**Rule:** Compile, build, publish, package, sign, install, launch, and deploy are different operations and evidence levels.

**Practice:** State exactly which stages ran and which artifact each stage produced or consumed.

**Boundary:** Success at one stage never implies permission or success at the next.

## BUILD-003 — Keep inputs reproducible

**Rule:** Build inputs should be identifiable and recoverable, including source revision or state, dependency locks, toolchain, configuration, target, and architecture.

**Practice:** Avoid unrecorded global dependencies and isolate generated files from authoritative source.

**Boundary:** Full bit-for-bit reproducibility may require additional infrastructure; disclose the achieved level honestly.

## BUILD-004 — Record target identity

**Rule:** A meaningful build result identifies configuration, runtime or platform target, architecture, version, and relevant feature set.

**Practice:** Keep architecture-specific outputs distinguishable and prevent one target from silently overwriting another.

**Boundary:** “Works on my machine” without target identity is not portable build evidence.

## BUILD-005 — Treat release artifacts as controlled outputs

**Rule:** Packaging, signing, SBOM generation, publication, and promotion require explicit release policy and authority.

**Practice:** Bind the artifact to source and dependency evidence and preserve rollback information.

**Boundary:** A developer build must not silently consume production signing credentials or publish to an external destination.
