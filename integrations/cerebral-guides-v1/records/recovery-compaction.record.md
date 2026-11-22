# Cerebral Guide: Recovery and Context Compaction

Object: Knowledge.Cerebral.Guide.RecoveryCompaction
Version: 1.3.0
Owner: Pegasus Cerebral
Triggers: model/context restart, compacted conversation, interrupted workflow, stale capability cache, uncertain mutation, or handoff
Required capabilities: current task identity; authoritative project/runtime evidence; Cortex when available and relevant
Rendered budget: 4096 UTF-8 bytes

## Goal

Resume from a small verified state without replaying uncertain work or loading a complete history.

## Recovery order

1. Re-establish exact task, project, owner, objective, and authority.
2. Read current local instructions and authoritative source/configuration.
3. Inspect the current working/runtime state before trusting an older summary.
4. Use Cortex once with the exact task identity and explicit namespaces when retained context can affect the decision.
5. Treat search hits as previews; exact-read only selected coordinates and cache their revisions with their bodies.
6. Compare durable state with the last checkpoint.
7. Resume from the first unverified transition, not from the beginning.
8. Record a new checkpoint only after meaningful verified progress, blocker, handoff, or completion.

## Mutation safety

If an earlier external or state-changing operation may have completed, inspect its durable state or receipt before retrying. Never assume silence means failure. Reuse an idempotency identity only for the same logical operation and only where the contract supports it.

## Compaction payload

Retain: objective, exact task/project identity, verified current state, material decisions, open blocker or uncertainty, next action, selected record coordinates/revisions, active environment/session identity, and evidence locations. Exclude transcripts, raw logs, repeated checks, secrets, private reasoning, and full guide bodies.

Compact only after the configured threshold is exceeded. Target the configured reduction, normally about 50 percent, then return directly to ordinary processing; do not compact again until usage exceeds the threshold. If one compactor input cannot fit, summarize bounded outline-aligned ranges and merge their checkpoints. Keep original saved messages addressable in durable storage while the active window reads only the recent range plus the adopted checkpoint.

## Cortex behavior

One successful start is enough while task identity, namespace order, server/guidance version, connection state, and decision-relevant retained context remain unchanged. Finish only at a meaningful checkpoint. Cortex unavailability does not create authority to invent replacement records or ignore current local evidence.

## Owner-authorized Source and QA continuation

For an authorized Source restart, pause/drain to a confirmed effect checkpoint before rebuilding in place. Preserve configured engine/SDK pins and saved drafts; confirm the new generation and preserved state before continuing. Unknown effects require reconciliation, not replay.

An owner-review source coordinate is a reference, not source inspection or old build credit. Automatic QA must obtain its own successful exact-root plugin.projects inventory before repair/build/verify/present; older admitted QA can refresh a real catalog and inspect that root. Source builds do not replace an immutable installed plugin. A changed generation needs fresh verify/present and separate owner approval before control tests.

## Failure boundary

When current state conflicts with retained context, current verified source/runtime wins for execution; preserve and report the conflict for correction.
