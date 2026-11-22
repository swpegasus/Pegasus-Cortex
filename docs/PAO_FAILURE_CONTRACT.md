# PAO compact receipt contract v1

This is the source contract for PAO presentation. It is not evidence that an
older installed Cortex exposes the behavior.

`output=structured` and omitted `output` retain the complete machine receipt,
including `jid`, `rid`, operation data, bounded process output, and typed failure
evidence when available. `output=agent` is a presentation projection only; it
does not enter request identity, dispatch, durable jobs, or retry behavior.

MCP and CLI JSON responses use readable UTF-8 (no BOM), not an ANSI code page
or UTF-16 wire format. Ordinary punctuation and supported Unicode are emitted
directly instead of HTML-oriented `\u0027`/`\u003C` escapes. JSON-required quote,
backslash, newline, tab, and control-character escapes remain; parsing recovers
the exact text, including literal escape-looking strings and Windows paths.
This presentation rule does not rewrite input, stored bodies, signed manifests,
or request identity. Consumers must parse JSON and treat returned text as passive
content; HTML views must encode text at rendering, never inject it as markup.

## Agent projection

- Deferred admission returns only `state` and the follow-up `jid` as JSON.
  It proves durable admission, not completion or target publication. Do not
  invent an exit value, output, log, resulting hash, or successful edit.
- Success contains only needed JSON result data, or `Completed.` when there is
  no result body. Routine receipt headers and internal sequence metadata are
  omitted. Small `run`, `pwsh`, recipe and Git stdout is returned as an untrusted
  string, never parsed as typed JSON. It is capped at 4096 UTF-8 bytes without
  splitting Unicode scalars; clipping sets `out_truncated`. Compiler/archive
  logs and stderr stay on demand. `log_available=true` means additional retained
  output was omitted from the compact result, accompanied by the `jid` needed
  to retrieve it. Full receipts are unchanged.
- `read_lines` returns the selected numbered source lines with no routine
  receipt/path/range header. Continuation and total are included only when more
  exists; partial/truncation markers remain explicit. It remains bounded by the
  read contract. An empty result is `No lines.`.
- Failure starts with `FAIL <jid> <stable-code>`, followed by a bounded message,
  any known field/index and measured bound, source-stamped execution and retry
  facts, at most four deduplicated privacy-filtered diagnostics, a next action,
  and truthful detail/log availability.
- RID is omitted from ordinary compact results. It is included when partial or
  reconciliation-required state makes replay identity relevant.
- Rejection before dispatch has no durable receipt and begins with
  `FAIL no-receipt`; it states `execution=not_dispatched`.

Execution values are `not_dispatched`, `started`, `partial`, and `unknown`.
Retry values are `safe`, `after_correction`, `reconcile_first`, and
`not_supported`. These are evidence, not permission to replay an operation.

Registered `run` admission requires `tmo` from 1 through the selected tool's
registered `TimeoutSeconds` (in seconds); `run` defaults to 30 seconds when
omitted. A timeout outside the registered range keeps stable code
`precondition` and exit category 3. Its failure evidence identifies `field=tmo`,
the actual value and registered maximum, `execution=not_dispatched`, and
`retry=after_correction`, with a specific permitted-range correction. The
source is `registered-tool-admission`; no process exit or log is invented.
The structured receipt and persisted `job_stat` retain the same evidence;
agent output shows the bounded field/limit without arguments, environment,
or executable paths. The limit is per registration, not a hardcoded .NET
limit; this diagnostic does not expand it or change execution policy.

Compact failure output is at most 4 KiB. Each diagnostic message is at most 512
UTF-8 bytes. Recognized compiler diagnostics retain only the source leaf,
line/column, code, and redacted message. Full bounded process evidence remains
available only in the structured receipt and, when retained, through
`job_log`.

## Text preimages and guard failures

`wr.sha` binds an existing target's exact byte preimage. A mismatch rejects
before publication; a missing target returns `not_found` without creation.
A matched write adds `sha_before` alongside the existing postimage `sha`,
`dst`, `enc`, `bom`, and `len`. Omitting SHA preserves legacy create-or-overwrite
behavior: `wr` does not provide a create-only/collision-fail policy. Consumers
requiring that policy must use an operation that explicitly implements it.

Text guards retain `precondition`/exit 3 (or `not_found`/exit 3 for a missing
hash-bound write), exact receipt IDs, and the existing failure schema. One
static `failure.diagnostics[].code` distinguishes `preimage_changed`,
`preimage_invalid`, `preimage_missing`, `literal_not_found`,
`literal_not_unique`, or `literal_count_mismatch`. No path, supplied hash,
literal, or body is echoed. `execution=not_dispatched` from
`text-publication-guard` means no target publication was dispatched, not that
the job was never admitted. Detail/log remain `none`/`not_retained`; no process
output or exit is invented. A publication race can also report
`preimage_changed` without replacing newer target bytes.

## Job identity and uncertain replies

An admitted/running job's JID selects exact status, waiting, cancellation, and
logs. Completed agent results retain it only for omitted logs or partial
outcomes. Select structured output when a full completed receipt is needed for
a later audit.
RID is the request's idempotency/reconciliation identity, not an alias accepted
by those JID selectors. The public contract currently has no exact read-only
RID lookup. Keep both when an uncertain outcome may need reconciliation.
Neither `unknown` nor a lost reply authorizes automatic resubmission, including
with the same RID. Do not generate a fresh RID to relaunch an uncertain effect.
Reconcile observed state and receipts through an authorized owner before
deciding whether a retry is safe.

## Job log

`job_log` accepts one exact `jid` and optional `max` from 256 through 65,536
UTF-8 bytes (default 16,384). It returns the target JID and state plus retained
exit/truncation facts and bounded `out`/`err_tail` when present. It never
manufactures output for operations that retained none. The ordinary-user
engine and service broker use the same projection.
