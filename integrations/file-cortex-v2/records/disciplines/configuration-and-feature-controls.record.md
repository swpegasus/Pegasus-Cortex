# Configuration and Feature Controls

These tenets extend `CORE-AUTH-002`, `CORE-AUTH-004`, `CORE-MOD-008`, and `BUILD-003`.

## CONFIG-001 — Define configuration precedence

**Rule:** Defaults, files, environment, command line, remote policy, and runtime overrides need an explicit precedence order.

**Practice:** Expose the source of each effective non-sensitive value and keep precedence consistent across entry points.

**Boundary:** A single-source application may omit unused layers rather than emulate a complex hierarchy.

## CONFIG-002 — Validate typed configuration before use

**Rule:** Reject malformed, contradictory, unsupported, or unsafe values at a clear activation boundary.

**Practice:** Parse into a typed candidate, validate the complete set, and return actionable field-level diagnostics.

**Boundary:** Unknown keys may be preserved only when forward compatibility is intentional and visible.

## CONFIG-003 — Distinguish required, optional, and derived values

**Rule:** Missing critical configuration must fail closed while optional behavior degrades explicitly and derived values remain reproducible.

**Practice:** Avoid silently substituting developer defaults for missing production policy.

**Boundary:** Safe diagnostic and repair surfaces may remain available when protected functionality is disabled.

## CONFIG-004 — Reload complete revisions atomically

**Rule:** Validate a full candidate configuration before replacing the active snapshot.

**Practice:** Preserve the latest valid revision on failure and expose stale or degraded state when continued use is safe.

**Boundary:** Security, authorization, or destructive operations may need to stop rather than rely on stale policy.

## CONFIG-005 — Store secret references rather than secret values

**Rule:** Ordinary configuration should point to an owned secret provider with defined resolution, rotation, expiry, and revocation.

**Practice:** Redact secret values from effective-configuration views, errors, logs, and generated artifacts.

**Boundary:** A bootstrap secret may require protected local storage, but it still needs explicit ownership and rotation.

## CONFIG-006 — Surface unknown and deprecated keys

**Rule:** Misspelled, ignored, shadowed, and deprecated settings must not look successfully applied.

**Practice:** Warn or fail according to consequence and provide the replacement and retirement timeline.

**Boundary:** Extensible namespaced payloads may permit unknown keys inside explicitly owned extension areas.

## CONFIG-007 — Treat feature flags as temporary control state

**Rule:** Every feature control needs an owner, purpose, scope, default, rollout plan, observability, expiry, and removal condition.

**Practice:** Keep flag evaluation deterministic and prevent stale flags from becoming permanent architecture.

**Boundary:** Long-lived user preferences or licensed capabilities are configuration or policy, not temporary rollout flags.

## CONFIG-008 — Separate activation from readiness

**Rule:** Enabling a setting does not prove that its provider, credentials, transport, trust material, or dependencies are ready.

**Practice:** Verify each required capability before accepting operations that depend on it.

**Boundary:** Configuration can accurately describe intent even while readiness reports failure.

## CONFIG-009 — Make configuration mutation auditable and reversible

**Rule:** Material runtime or machine configuration changes require current-state capture, actor, scope, validated candidate, outcome, and rollback behavior.

**Practice:** Apply the smallest owned change rather than rewriting unrelated configuration.

**Boundary:** Ephemeral process-local test overrides may use lighter evidence when they cannot escape the test scope.
