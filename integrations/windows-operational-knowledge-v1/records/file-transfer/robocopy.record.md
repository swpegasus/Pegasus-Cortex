# Knowledge.Windows.FileTransfer.Robocopy

Object: Knowledge.Windows.FileTransfer.Robocopy
Version: 1.0.0
Scope: substantial Windows directory-tree transfers, including local, network, staging, upgrade, migration, and recoverable copy workflows
Rendered budget: 4096 UTF-8 bytes

## Default decision

Prefer Robocopy for substantial Windows file-tree transfers when resumability, throughput, bounded retries, incremental comparison, timestamps, detailed progress, or a durable log materially improve the operation.

Do not use Robocopy as a substitute for:

- an application-aware backup of a live database;
- an installer or migration engine's semantic decisions;
- source control;
- a single small atomic text/file replacement;
- archive validation or content transformation;
- a product-owned API.

## Safe transfer flow

1. Resolve and validate exact canonical source and destination paths.
2. Confirm the requested operation is copy, update, or synchronization.
3. Enumerate unexpected reparse points before copying and select an explicit link/junction policy.
4. Inspect the destination and run list-only mode when collision or deletion behavior is uncertain.
5. Use copy/merge semantics by default; include empty directories only when required.
6. Bound retry count and wait time so locked or unavailable files fail visibly instead of hanging indefinitely.
7. Preserve data, attributes, and timestamps by default. Copy ACLs, ownership, or audit data only when the task explicitly requires those security semantics.
8. Use restartable transfer and bounded multithreading when compatible with the source, destination, and network.
9. Stream concise progress to the caller and write one task-owned log when durable diagnostics are useful.
10. Inspect the final Robocopy summary and verify the required counts, sizes, required files, and package/application admission evidence.

## Destructive switches

Never use mirror, purge, move, or source-file deletion as a general default. They may remove destination or source content and require explicit task authority, exact target validation, and a clearly stated recovery boundary. A destination containing unrelated material is not an error to “fix” automatically.

## Reparse and metadata boundaries

Do not traverse an unexpected junction, symbolic link, or other reparse point. Decide whether an admitted link is excluded, reproduced as a link, or deliberately followed. Do not casually copy ownership, auditing, or broad ACLs across machines or security domains.

## Exit interpretation

Robocopy uses a bitmask-style exit code. Values below 8 do not indicate a fatal copy failure, but extra files or mismatches represented by those values may still violate a strict acceptance contract and must be inspected. Values 8 or higher indicate at least one copy failure. Never treat process exit code zero as the only success state, and never treat every nonzero code as failure.

## Live and durable evidence

Use Robocopy's native progress/summary output for live status. When a durable log is requested, keep it bounded and task-owned, and avoid logging credentials or sensitive file contents. Use hashes only when integrity risk or a signed package contract requires them; routine trusted-network copies can normally rely on Robocopy's summary plus counts, sizes, timestamps, and required-file checks.
