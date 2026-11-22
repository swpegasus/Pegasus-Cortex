# Cerebral Guide: Failure Evidence and Safe Retry

Object: Knowledge.Cerebral.Guide.FailureEvidenceRetry
Version: 1.0.0
Owner: Pegasus Cerebral
Triggers: a build, deployment, transfer, remote command, container, or provisioning operation fails, times out, disconnects, or becomes uncertain
Required capabilities: bounded diagnostics, exact operation identity, and read-only reconciliation of owned state
Rendered budget: 4096 UTF-8 bytes

## Preserve an honest terminal state

Classify the result as failed, partial, cancelled, timed out, uncertain, or succeeded at a named evidence level. Do not convert containment into success. Capture the exact source/input identities, operation or container ID, start/end times, exit status, failing stage, bounded error, resource observations, and cleanup result.

Use rolling logs with explicit size/file limits. Hash retained receipts and logs when integrity matters. Keep secrets, signed URLs, private payloads, raw transcripts, and unbounded terminal streams out of durable evidence.

## Reconcile before retry

1. Check whether the exact remote process, build record, container, transfer, promotion, listener, or external mutation is still active or completed.
2. Confirm cancellation against the actual owned operation. Killing a client is not proof that a daemon, child, remote job, or external effect stopped.
3. Inspect only task-owned objects. Do not globally prune Docker/BuildKit, delete caches, reset repositories, kill unrelated processes, or overwrite ambiguous destinations to make the retry clean.
4. Determine whether the failed step committed a reusable artifact. A failed Docker `RUN` is not a final image/layer; files created within it are transient unless an explicit supported output was committed and admitted.
5. Fix the root cause and issue a new source/lock/profile or operation identity when behavior or inputs changed. Reuse an idempotency key only under its declared reconciliation semantics.
6. Re-run the smallest early preflight that would catch the prior failure before repeating expensive work.

## Evidence boundaries

Static inspection, unit tests, resolver dry runs, successful compilation, native import, GPU kernels, model inference, service health, and end-to-end client acceptance are different evidence. Report only what ran on the exact artifact and platform.

When safe recovery requires unsupported daemon internals, credential use, destructive cleanup, or a material architecture choice, stop with the smallest required decision. Preserved evidence is preferable to an unreviewable workaround.

This guide authorizes no retry, cleanup, process termination, rollback, or external mutation.
