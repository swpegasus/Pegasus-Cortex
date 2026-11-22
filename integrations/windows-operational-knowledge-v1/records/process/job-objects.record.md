# Knowledge.Windows.Process.JobObjects

Object: Knowledge.Windows.Process.JobObjects
Version: 1.0.0
Triggers: launching external tools, process-tree ownership, cancellation, resource limits, orphan prevention, persistent sessions
Rendered budget: 4096 UTF-8 bytes

## Ownership

Use a Windows job object when a product must own and observe an external process tree. Create/configure the job before useful child work begins and assign the process as early as the launch contract permits. Keep the job and process handles needed to prevent PID-reuse ambiguity.

## Policy

Choose explicit limits for termination-on-owner-close, active process count, memory/CPU where justified, UI restrictions where compatible, and nested-job behavior. Do not assume every executable can be placed in a job; detect assignment failure and report the boundary.

## Observation

Associate completion notifications before active work when reliable process-tree events are required. Treat event PIDs as observations that can become stale; use retained handles or start identity for destructive actions. A parent exit does not prove all descendants exited.

## Cancellation

Prefer cooperative protocol cancellation or console/control signaling when supported. Escalate to terminating the exact owned job only after the graceful boundary expires. Report whether cancellation occurred while queued, before child creation, during execution, or after an externally visible effect. Preserve partial output/state truth.

## Persistent sessions

A reconnectable terminal or kernel may use a job for process containment, but the job is not the session protocol. Session identity, generation, stdin, cursor-based output, ownership, idle/lifetime limits, and close semantics remain separate.

Reference:
- https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects
