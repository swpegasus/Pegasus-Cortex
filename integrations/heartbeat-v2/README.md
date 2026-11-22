# Heartbeat v2 isolated local producer

This directory contains a disabled-by-default, local-only Heartbeat v2
implementation and an internal disposable recovery-probe brief producer. It is
not connected to the live heartbeat automation, a service, a model, a worker,
or a deployment workflow.

## Isolation contract

`PilotConfig` accepts only the `standalone-local` profile. The runtime rejects:

- inherited parent transcripts;
- model, worker, or product-mutation dispatch;
- environment-driven configuration;
- network access;
- integrated or hybrid adapters; and
- unknown configuration fields.

The producer does not read environment variables and contains no network
client. A poisoned-environment test also replaces `socket.socket` with a
failure sentinel and verifies that output remains byte-for-byte identical.

## Heartbeat states

The classifier emits exactly four statuses:

| Status | Meaning |
| --- | --- |
| `unchanged` | Revision already completed, pilot disabled, or retry not due. |
| `changed` | A new bounded revision was observed. |
| `failed` | The first or second consecutive source/contract probe failed. |
| `attention-required` | Source attention, cursor inconsistency, lease conflict, or the third consecutive failure requires an operator. |

Every `unchanged` response is at most 512 canonical UTF-8 bytes and explicitly
sets lease acquisition, state writing, model dispatch, worker dispatch, and
knowledge writing, and parent-transcript inheritance to `false`. The code
returns `unchanged` before lease acquisition.

Failed stateful probes use the exact bounded retry schedule:

1. 60 seconds;
2. 300 seconds;
3. 900 seconds, with attention latched.

The third consecutive failure persists `attentionLatched=true`. Later runs
return `attention-required` without acquiring a lease or rewriting state until
an operator deliberately repairs or replaces the state file. A successful
probe resets the consecutive-failure counter.

External state must be a direct `.json` child of an explicitly supplied private
state root. The state contains a canonical SHA-256 equality hash, stable
idempotency keys, and a stable lease identity derived from the heartbeat,
idempotency key, and approved state path. The host remains responsible for
making the state root private with suitable filesystem ACLs.

## Disposable recovery-probe brief

The internal probe producer reads the exact recovery order declared in
`recovery_probe_brief.py`, hashes every source byte, binds the result to a
non-revealing root identity, and limits serialized output to 8 KiB. It uses
`pegasus.cortex.recovery-probe-brief/1.0` and is not the product
`pegasus.cortex.project-brief/1.1` contract. Validation recomputes the entire
probe brief and fails closed when:

- an authority file, recovery order, checkpoint, or task changes;
- the brief is stale or unacceptably future-dated;
- it was copied from another project root;
- its approved path changes; or
- a currently inspectable path boundary is a symlink/reparse point.

Generation requires both an explicit timestamp and `--write`; the only allowed
destination is `.cortex/generated/recovery-probe-brief.json` beneath the supplied
project root. No brief was generated in the Pegasus Cortex root as part of this
isolated implementation.

## Local commands

Run from the Pegasus Cortex project root with a local Python 3 installation:

```powershell
$env:PYTHONDONTWRITEBYTECODE = '1'
$env:PYTHONPATH = (Resolve-Path 'integrations\heartbeat-v2').Path
python -m unittest discover -s 'integrations\heartbeat-v2\tests' -v
```

Run the fixed local benchmark. It emits one sanitized aggregate JSON object to
standard output; heartbeat state and bounded recovery briefs are created only
under an automatically removed temporary root.

```powershell
python -m heartbeat_v2.benchmark `
  --pegasus-nexus-root '<approved-pegasus-nexus-root>' `
  --neural-commander-root '<approved-neural-commander-root>'
```

The reproducible fixture performs ten changed/unchanged heartbeat pairs and ten
matched recovery trials per target. Recovery timing alternates which read path
runs first. The benchmark does not flush or manipulate the operating-system
file cache, and brief preparation is excluded from brief-recovery timing; these
limitations are explicit in the aggregate. The checked run is retained as
`benchmark-report.json`, with no raw authority content, absolute paths, task
identifiers, prompt references, or source-hash values.

Classify a supplied observation using the checked-in disabled configuration:

```powershell
python -m heartbeat_v2.cli probe `
  --config 'integrations\heartbeat-v2\config.pilot.json' `
  --observation '<local-observation.json>'
```

Stateful pilot use additionally requires a deliberately enabled pilot
configuration, `--enable-pilot`, `--state-root`, and a direct-child `--state`.
The package never infers these gates.

Generate and validate a brief only after separately authorizing that project
root write:

```powershell
python -m heartbeat_v2.cli recovery-probe-brief-generate `
  --project-root '<approved-project-root>' `
  --generated-at-utc '2026-07-30T12:00:00Z' `
  --write

python -m heartbeat_v2.cli recovery-probe-brief-validate `
  --project-root '<approved-project-root>' `
  --now-utc '2026-07-30T12:01:00Z'
```

## Known platform and integration gaps

- A changed observation is committed to heartbeat state before the bounded report
  is returned to a downstream dispatcher. There is no persisted,
  acknowledgement-backed outbox yet, so a host crash after state commit but
  before downstream receipt can suppress automatic replay of that revision.
  Production integration must add an acknowledged outbox or equivalent
  delivery protocol before activation.
- Python does not expose a portable Windows handle-relative, no-follow atomic
  replace for this use. The code checks reparse ancestry before and after local
  replacement, but a privileged concurrent path-substitution race cannot be
  eliminated by this implementation.
- The equality hash detects accidental or unsealed state changes; it is not an
  authenticity signature against an attacker who can rewrite both content and
  hash.
- ACL creation/verification, scheduler timing, schema-engine validation, and
  monotonic-clock enforcement remain host responsibilities or future
  integration work. Valid hash-sealed expired leases are recovered by atomic
  quarantine. A persistent adjacent OS-locked guard serializes the short live
  lease mutation boundary across processes and is never deleted during normal
  operation; malformed lease or guard records remain fail-closed for operator
  review.
- The symlink rejection test requires Windows symlink privilege. It is skipped
  when the host cannot create the test link; runtime reparse checks still run.
- No ARM64, live automation, service, deployment, publication, or network
  integration validation is included.
