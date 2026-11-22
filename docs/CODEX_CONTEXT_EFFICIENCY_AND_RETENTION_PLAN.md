# Cortex Context Efficiency, Retention, And Continuation

Status: local hardening candidate ready for exact-source verification; rollout gates remain closed  
Owner: Token Usage coordinator  
Last updated: 2026-07-31

## Purpose

Pegasus Cortex is a passive retention and retrieval product for public,
agent-authored project state. It keeps exact revision history for:

- task snapshots;
- bounded project briefs;
- agent continuation packets and transfer receipts;
- evidence references and closeouts with reported verification evidence; and
- reusable-lesson proposals.

Cortex does not schedule agents, claim work, approve gates, decide task truth,
or turn stored content into execution authority. Agents and coordinators author
state; the owning control plane decides who may act; Cortex retains and returns
the exact accepted bytes.

The context-efficiency objective is to let a future agent recover through one
bounded brief and, only when necessary, one exact read. Full transcripts and
append-heavy recovery histories stay outside the normal hot path.

## Runtime Profiles

### Standalone local

The standalone profile must run while Nexus, Server, Coder, PostgreSQL, and all
remote services are absent.

- Normal installed storage is a per-user SQLite database under the platform's
  protected local-application state directory.
- Human-readable `.cortex` files remain offline recovery and portability
  material.
- Installed multi-client access uses an ACL-protected Windows named pipe or
  Unix-domain socket.
- A small stdio MCP bridge supports Codex and other stdio-only clients.
- Explicit portable direct-stdio mode remains available without silently
  switching from inherited environment variables.
- The profile opens no TCP/UDP listener, performs no outbound network request,
  and does not run update checks.
- Project access is limited to roots explicitly registered by the local user.
  A deterministic producer may read those roots and submit a validated
  snapshot; Cortex never crawls arbitrary projects or live agent tasks.
- Exact-pinned reusable assemblies, including `Pegasus.Shared`, may be bundled.
  No running Pegasus service is a standalone dependency.
- `CurrentUserOnly` authenticates the operating-system user, not an
  individual same-user agent or process. Local profile names are capability
  namespaces and endpoint selectors, not credentials. Processes that do not
  mutually trust one another require separate OS accounts/protected profiles;
  standalone mode does not add a second token layer without that threat model.

### Integrated

Remote access follows:

`client -> Nexus -> Cortex server -> PostgreSQL`

Nexus is the remote relay/gateway. It terminates network ingress, authenticates
the caller, checks grants and revocation, attaches audit correlation, and
forwards only a bounded Cortex request. Cortex does not trust client-supplied
remote identity, remains a separate process, and owns knowledge validation,
retention, review, and retrieval. The current installed ARM64/PostgreSQL
service remains unchanged and recoverable until a replacement proves
shadow-read and isolated-write parity. Divergent dual writes are prohibited.

## Authority Model

| Concern | Authority |
| --- | --- |
| Task and closeout content | Agent or project coordinator |
| Scheduling, assignment, claims, cancellation | Owning orchestrator |
| User, service, and remote access identity | Nexus in integrated mode |
| Local storage permission | Current OS user and explicit local profile |
| Exact retained bytes and revision chain | Cortex |
| Execution approval | Referenced external project gate |
| Conflict reconciliation | External author/coordinator |

A valid Cortex write grant means only that the caller may retain an assertion.
It does not prove the assertion, assign a task, satisfy a gate, or authorize
execution.

## Versioned Contracts

### `pegasus.agent-task-snapshot/1.0`

The complete active task set is an immutable, canonical JSON snapshot:

- at most 32 tasks and 32 KiB UTF-8;
- stable snapshot, task-set, project, producer, and idempotency identities;
- monotonic revision and exact previous-snapshot hash;
- source revisions, capture/expiry timestamps, and complete coverage;
- ordered task assertions with objective, current state, next action, blockers,
  gate reference, reported owner, dependencies, failures, and evidence; and
- passive/untrusted/no-authority constants.

Allowed states are `planned`, `active`, `blocked`, `held`, `done`, `cancelled`,
and `unknown`.

The same idempotency key and canonical hash is an exact replay. Reusing the key
with different bytes is a conflict. A revision whose expected previous hash is
not the current head is stale. Cortex never performs last-write-wins or merges
competing heads.

### `pegasus.cortex.project-brief/1.1`

The hot brief is at most 8 KiB canonical UTF-8 and contains:

- one current task and at most four additional hot tasks;
- current state, blocker, gate, next action, and checkpoint;
- bounded task/evidence references;
- monotonic state revision and `contextSetId`;
- an `authoritySetHash` that must match the caller's locally verified
  `knownAuthoritySetHash` on every request;
- source hashes, freshness, selection-policy version, and `hasMore`; and
- explicit fallback instructions.

Given a known `contextSetId`, the read returns:

- `unchanged` when the exact set is still current;
- `delta` only when the exact immediate predecessor is retained; or
- `snapshot` otherwise.

Missing, expired, corrupt, incomplete, or mismatched data sets
`fallbackRequired`. A missing or mismatched authority hash returns
`authority_mismatch` without current project/task/source claims. Hashes
establish identity and integrity, not truth, freshness, assignment, or
authority.

### `pegasus.agent-work-packet/1.0`

Start, continuation, and closeout packets are at most 8 KiB. They include:

- work identity, revision chain, packet hash, and idempotency;
- project/context identity and bounded source references;
- objective, current state, constraints, gate, and expected output;
- completed/remaining work, blockers, failed approaches, and next action;
- freshness, cancellation, context budget, and fallback; and
- passive/untrusted/no-authority constants.

Closeout disposition is `solved`, `partial`, `blocked`, or `failed`.
Handoff and acceptance receipts are passive, caller-authored assertions that
name matching exact bytes. Their hashes prove linkage and integrity only; in
standalone mode they do not prove the named receiver observed or accepted the
packet. Integrated identity evidence may be added later through Nexus. A
candidate reusable lesson remains private until sanitized review and authorized
publication.

## Storage

SQLite is the standalone indexed-retention provider.

- WAL journaling, `foreign_keys=ON`, `synchronous=FULL`, bounded busy timeout,
  and an immediate single-writer transaction;
- immutable document revisions and explicit current-head rows;
- producer-scoped idempotency uniqueness;
- append-only audit events;
- rebuildable FTS5 search projection;
- integrity check, backup, restore, and index rebuild;
- canonical local paths, reparse rejection, Windows 240-character budget, and
  owner-only Unix permissions; and
- raw chats, private reasoning, terminal streams, passwords, keys, tokens, and
  credentials are prohibited inputs. Deterministic screening is defense in
  depth, not a DLP classifier or proof that arbitrary unlabeled content is
  clean; producers remain responsible for sanitation and review.

The file-backed and SQLite-backed brief producers must emit byte-identical
canonical briefs. Storage choice affects latency, integrity, and concurrency;
it must not change model token input.

### Hot, warm, and cold context

- **Hot:** compact `START_HERE.md`, `AGENTS.md`, and generated
  `.cortex/CURRENT.json`. `CURRENT.json` is capped at 2 KiB and contains
  only project identity, current task/gate/checkpoint/next action, a monotonic
  local projection revision, an authority-set hash, generation time, and the
  fallback pointer. It is disposable and never authoritative.
- **Warm:** one `cortex_project_brief` response capped at 8 KiB and, only
  when a referenced detail is required, one exact retained-record read.
- **Cold:** completed tasks, dated sessions, worklogs, receipts, and evidence
  remain in hash-sealed dated archives. They do not enter normal model context.

The local authority hash covers `START_HERE.md`, `AGENTS.md`,
`.cortex/STATE.md`, `.cortex/TASKS.md`, and `.cortex/RECOVERY.md`.
Generation rejects linked, missing, changing, malformed, or oversized inputs
and atomically replaces only the optional projection.

Recovery movement is explicit and copy-first. `import-recovery` copies the
fixed `.cortex` recovery allowlist without changing source files or inferring
task truth from Markdown. `export-recovery` emits a bounded, self-hashed,
human-readable export of the current brief and every retained passive
revision. `restore-recovery-export` validates that export without SQLite,
rebuilds a new absent database under the same protected registration scope,
and rematerializes the current brief. Native SQLite backup/restore remains the
forensic exact-database path; readable-export restore intentionally resets the
derived brief lineage while preserving exact passive task, work, receipt, and
proposal envelopes.

## MCP Surface

Read tools:

- `cortex_project_brief`;
- existing bounded `cortex_search`;
- existing exact knowledge-record `cortex_read`;
- separately allowlisted private `cortex_list_proposals`; and
- typed local-retention `cortex_read_retained_record`, which resolves the
  repository coordinates and byte hash carried by `retained://` brief
  references.

Writer tools use a separate local or integrated capability allowlist:

- `cortex_submit_task_snapshot`;
- `cortex_submit_work_packet`; and
- existing sanitized proposal submission.

No Cortex tool assigns, claims, schedules, cancels, completes, prioritizes, or
reopens a task.

## Agent Lifecycle

Startup:

1. Read compact `START_HERE.md` and `AGENTS.md`.
2. Recompute and validate `.cortex/CURRENT.json` against the local authority
   set, then resolve its registered local or Nexus-gated scope.
3. Request the current brief, supplying the verified authority hash and
   optionally the last known context set.
4. Validate the returned snapshot, delta, or unchanged result against its
   revision, freshness, authority hash, source hash, and retained task-head
   binding.
5. Optionally retrieve one exact record. Revalidate the cited project gate
   before acting.

Closeout:

1. Validate the work and evidence.
2. Author and retain a bounded continuation or closeout packet.
3. Submit a revision-checked task snapshot that hash-binds the retained packet
   and any selected evidence.
4. Record handoff/acceptance receipts; when their status must enter the hot
   brief, an agent or coordinator submits the next revision-checked task
   snapshot that references them.
5. Submit a sanitized reusable-lesson candidate when appropriate.
6. Stop or hand off without replaying the transcript.

Packet-first retention is intentional: a task snapshot may reference only exact
records that already exist. Cortex never fabricates or automatically advances a
task snapshot merely because a later packet or receipt arrives.

If Cortex is unavailable or stale, agents use the project-local recovery order.

## Heartbeat v2

Heartbeat remains an orchestrator-side control mechanism:

`deterministic probe -> unchanged | changed | attention-required | failed`

- `unchanged` is at most 512 bytes and performs no model call, worker spawn,
  lease, state write, or inherited-history continuation.
- `changed` coalesces events but may start a fresh coordinator with a bounded
  brief/work packet only after an acknowledgement-backed outbox (or equivalent
  delivery contract) is implemented and enabled. The current reference has no
  dispatcher.
- Scheduler cursor, lease, retry, and metrics state remains outside Cortex
  knowledge.
- Retries use 1, 5, and 15 minutes. Three consecutive failures produce
  `attention-required` and stop automated action.
- Cortex may expose revision data but never wakes or schedules agents.
- Production activation also requires an acknowledgement-backed outbox or an
  equivalent delivery protocol. The isolated producer currently commits its
  changed cursor before a downstream dispatcher acknowledges receipt, so a
  crash in that interval can suppress automatic replay.

## Migration Sequence

1. Preserve and verify a source snapshot.
2. Reconcile the unbuilt July 30 project-brief, heartbeat, and work-packet
   drafts.
3. Separate standalone-local composition from PostgreSQL/service activation.
4. Add SQLite retention and focused provider tests.
5. Add revision-checked task/work-packet ingestion and hot-brief retrieval.
6. Add explicit local IPC plus stdio bridging without TCP.
7. Pilot Nexus and Neural Commander recovery locally.
8. Add Nexus-gated integrated compatibility and PostgreSQL shadow parity.
9. Update system skills/templates only after both pilots pass.

## Preservation And Rollback

The pre-implementation source/state snapshot is:

`D:\Codex Projects\_recovery\cortex\pre-context-20260730-122134`

- copied files: 361;
- copied bytes: 4,593,628;
- excluded generated/heavy directories: `artifacts`, `bin`, `obj`, `.git`,
  and `.vs`;
- verification: zero missing or mismatched files; and
- manifest SHA-256:
  `c7a21b3237ed2e34807cde9e0ed13b4a0d50b18dec66264b674559f2527a95cc`.

The current installed ARM64 selector, database, service, packages, credentials,
and evidence are outside this implementation's mutation scope and remained
unchanged. No installed standalone state was created. Disposable test state
passed native SQLite backup/restore, readable export, functional rebuild, and
file-recovery checks.

## Implemented Local Source Candidate

The 2026-07-30 working tree now contains a local source candidate for:

- strict passive contracts and schemas for task snapshots, work packets,
  handoff receipts, and acceptance receipts;
- revision-checked immutable retention, canonical byte validation, prohibited-
  content screening, bounded search projection, and exact reads;
- a provider-neutral `pegasus.cortex.project-brief/1.1` projector and read
  service with immediate-predecessor delta semantics;
- SQLite retention with WAL, foreign keys, full synchronization, bounded writer
  waiting, immutable heads/revisions/audit, lexical indexing, integrity checks,
  backup/restore, ACL/path checks, quotas, and fail-closed schema verification;
- a file-backed brief store whose accepted canonical output is required to be
  byte-identical to SQLite;
- one stable task-set head per project, atomic stale-base/idempotency handling,
  exact retained locators, and read-time binding between the derived brief and
  the sole retained task head;
- bounded evidence-bearing proposal submission plus separately allowlisted,
  restart-safe private proposal listing without generic-search exposure;
- explicit copy-first `.cortex` import, self-hashed readable recovery export,
  SQLite-independent export verification, and functional export-to-new-
  database rebuild with truthful cleanup-required reporting;
- explicit provider-neutral, standalone-local, and integrated MCP
  compositions;
- standalone `service`, `bridge`, and explicit portable `stdio` modes, with
  create-new/no-overwrite project-registry provisioning and separate read/write
  allowlists;
- a current-user named-pipe transport with bounded framing, deadlines, request
  budgets, session isolation, and no network-stack dependency; and
- disabled-by-default Heartbeat v2 plus an internal
  `pegasus.cortex.recovery-probe-brief/1.0` benchmark producer. The internal
  probe is not the product project-brief contract.

The July 30 drafts were explicitly reconciled rather than silently accepted.
The Heartbeat `project_brief.py` draft and test were superseded by the clearly
named internal `recovery_probe_brief.py` contract. The former MCP `Hybrid`
host/runtime/query-source and local-security files moved into the explicit
`PegasusCortex.Mcp.Integrated` composition, with their security tests, while
provider-neutral MCP and the new standalone-local composition remain separate.
The pre-change snapshot retains every superseded file.

This was the July 30 unversioned working-tree source candidate. `VERSION.json`
remains `1.2026.07.28.05.50.A`; no new release/checkpoint was allocated. The
selected ARM64 release remains `1.2026.07.27.13.20.A`. X64 was frozen at that
checkpoint and was explicitly reactivated for isolated local testing on July
31 under the narrower boundary recorded below.

## Acceptance

The implementation is accepted only after:

- standalone read/write/restart recovery succeeds with all Pegasus services
  and PostgreSQL unavailable;
- no standalone TCP/UDP listener, outbound connection, remote update check, or
  inherited-environment hybrid activation is observed;
- task replay, conflict, stale-head, concurrent-writer, corruption, disk-full,
  migration, ACL, backup/restore, and index rebuild cases pass;
- task/current-state/gate/checkpoint/revision/hash recovery is 100% correct;
- briefs and continuation packets remain at most 8 KiB and full task snapshots
  at most 32 KiB;
- normal recovery uses no more than two calls;
- twenty matched cold recoveries and twenty heartbeat cycles include at least
  ten unchanged cycles;
- median uncached input falls at least 70% with no correctness loss;
- end-to-end recovery is at least 15% faster than matched file-only recovery;
- every unchanged heartbeat produces zero model/worker/lease/knowledge-write
  activity; and
- remote access without Nexus fails closed.

### Historical July 30 Verification Evidence

The July 30 source state passed:

- locked dependency restore across the solution;
- one 38-project Release build with zero warnings and zero errors;
- all 19 executable project harnesses with 283/283 reported checks passing;
- 36 Heartbeat v2 Python tests with 35 passing and one Windows
  symlink-privilege skip;
- deterministic task/work lineage corruption, stale head, concurrent writer,
  crash rollback, backup/restore, readable export/rebuild, restart, private
  proposal isolation, and post-promotion cleanup-failure cases;
- an independent final audit of export and restore safety; and
- static isolation checks: 38 solution projects, 58 valid JSON files, zero
  conflict-marker files, a 153-character maximum source path, no generated
  project brief in this checkout, and no network, PostgreSQL, integrated-host,
  service, or inherited-environment activation dependency in the standalone
  runtime source/output.

Five additional .NET cases were explicitly skipped: two Windows symlink probes
in local import/export, one file-provider symlink probe, one Linux-only native
filesystem group, and opt-in live PostgreSQL. They are not represented as
passes. The standalone output contains the pinned `Pegasus.Shared` runtime
assembly with SHA-256
`2ee5184d228ce7f90517aa83f218d5b4a75610742d6f70b230562a0cd9b5b91a`;
the pinned `1.20260711.421-a` package SHA-256 is
`9696db2ccc40e74649233cfbfa34b94a201d5810b71264ce62d1ae33a12a35a6`.

The July 31 hardening candidate has separate preliminary mutable-tree evidence:
a warning/error-free 38-project build, all 19 executable harnesses exiting zero,
SQLite 19/19 under a fixed `TimeProvider`, provider-neutral broad retrieval
20/20 with exact 5/5 and zero reserved leakage, eight concurrent clients
without starvation/timeouts, manifest tooling 38 passes/1 catalogued skip, hot
projection 6 passes/1 catalogued skip, and disabled Heartbeat 35 passes/1
catalogued skip. None of this is the accepted exact-source aggregate until the
candidate is sealed, extracted, rerun, and bound to one immutable manifest.

### Preliminary Pilot Evidence

The checked Heartbeat v2 report contains 20 cycles: 10 `unchanged` and 10
`changed`. Every unchanged response was at most 329 bytes and recorded zero
model, worker, lease, state-write, knowledge-write, transcript-inheritance,
network, service, automation-mutation, and target-project-write activity.

The same report contains 20 matched recovery trials, 10 each for Pegasus Nexus
and Neural Commander. The bounded internal probe payloads were 3,033 versus
360,117 bytes and 3,007 versus 681,344 bytes. Median measured retrieval was
1.54875 versus 50.51875 ms and 1.6127 versus 52.5286 ms. These are preliminary
payload/latency proxies: brief preparation was excluded, the operating-system
cache was not controlled, and the Pegasus Nexus checkpoint field was not
measurable in its 10 trials. This report therefore does not by itself satisfy
every product-brief acceptance criterion.

### Remaining Gated Or Unverified Work

- Integrated PostgreSQL task/work-packet/project-brief persistence, Nexus
  routing, shadow-read parity, and isolated-write parity are not implemented or
  live-verified.
- No installed standalone service, ARM64 standalone execution, Unix UID
  isolation, second-Windows-user denial, or literal second-Windows-session file
  lock proof has run.
- True filesystem exhaustion remains untested; the local provider instead has
  deterministic SQLite-full rollback and process-termination rollback
  coverage.
- Future SQLite schema migration is not implemented; unsupported versions fail
  closed.
- Readable-export restore requires the same protected local registration, an
  absent destination database, and a closed/checkpointed export source. It
  rebuilds current state and deliberately reports
  `HistoricalProjectBriefChainRestored=false`; native SQLite backup is required
  for forensic preservation of the exact derived-brief history.
- A rare failure to remove the protected staging child after database promotion
  returns `Accepted=false`, `cleanup-required`, a stable diagnostic, and a
  manual recovery instruction. Normal startup remains fail-closed until that
  exact empty staging child is inspected and removed.
- Recovering across consecutive retained task revisions that could not produce
  a bounded brief streams and validates the entire unresolved source range.
  Memory and connections remain bounded, but repeated unrepresentable
  submissions can make cumulative validation work quadratic until a persisted
  validated-chain checkpoint or explicit fallback brief is added.
- The file brief writer serializes cooperating provider processes through a
  stable lock file. Project-directory ACLs remain the security boundary, and a
  privileged/noncooperating path substitution race cannot be eliminated by the
  current path-based APIs.
- Content screening recognizes bounded prohibited patterns but cannot prove
  arbitrary text contains no unlabeled secret, transcript, or private
  reasoning. Producer sanitation, least-privilege write grants, and review
  remain required.
- Heartbeat v2 remains disconnected from the live scheduler and paused; its
  acknowledgement-backed outbox is not implemented.
- Windows symlink rejection paths execute in production code, but four local
  probes could not create links under the current user token. Unix-domain
  socket, Unix UID isolation, and network-mounted filesystem behavior remain
  platform-gated.
- The checked recovery benchmark is an internal payload/latency proxy. OS cache
  state was uncontrolled, brief preparation was excluded, and the Nexus
  checkpoint criterion was not measurable, so it does not authorize
  system-wide rollout.
- Within the July 30 source candidate, no package, upload, deployment,
  publication, Generalize promotion, or system-wide skill/template rollout
  occurred. The separately authorized July 31 single-lesson publication is
  recorded below.

## Retention Review Submission

On 2026-07-31, the user authorized one sanitized collective `quality`
submission and later authorized its independent review, publication, and
bounded retrieval/leakage validation. The accepted proposal is `Bound
long-lived coordinator context and fork inheritance`:

- proposal ID:
  `proposal_8e9752951e1bc8f67a2c29d14a91b1683bca8bbc4938b6274a555d16194e6877`;
- proposal hash:
  `f7c75951cd7c31c2f2c2177e81b6798431a15560cda83955818a4fd17d886072`;
- independent assessment ID:
  `assessment_0816643a8a99f292b58846e6d2fab3001c375533ddcc1cf76b59334129e6d4e5`;
- assessment hash:
  `82aa46d043e13e3b6ed11867e6107be8a2bf6a1b50c7b86d1078a72e0f170eff`;
- one-use independent-review grant ID:
  `grant_f2d35116404d15ed72dbc6fb53703fdd9f34dcd47e8a7de734c883f603a18524`;
- terminal auditor assessment ID:
  `assessment_47adffc589eff38953edeab8b6c0cecd89b3c7fa4af73de878480803b69d989c`;
- consumed grant-use ID:
  `use_a9ba70ee6611e95d0826df34a28f68361965ce37f4e8c4e11727d0f24de85896`;
- terminal decision ID:
  `decision_01a03df60145c1caaab873e0b26fd07260b162e1b974a8602eaecaa906645789`;
  and
- published record/revision:
  `krec_f7c75951cd7c31c2f2c2177e81b6798431a15560cda83955818a4fd17d886072`
  / `krev_4db7fbceb8082edba514ee8fa10c9e3c54f256df066fa99a7b67a9a672cd8caf`.

The first assessment remained advisory because no grant accompanied it. A
direct review attempt by the submitting identity then failed closed with
`learning_feedback_review_independence_required`. The separately authorized
`codex-auditor` identity consumed the bounded one-use grant and produced the
terminal `auto_approved` decision. The proposal is now approved and
searchable.

The published record contains generalized practices, sanitized pilot ratios
and limitations, and explicit non-authority/non-deployment language. It
excludes raw conversations, prompts, account totals, private paths, task
identifiers, and executable instructions. The defined exact-read leakage scan
found no Windows/UNC/Unix path, receipt identifier in content, conversation
marker, credential assignment, account total, or private lineage name. This
bounded pattern/category scan is not a universal DLP proof.

The predeclared natural-language retrieval set returned the lesson in the top
three for only 5 of 20 queries (`25%`), below the required `95%`; this rollout
gate failed and is not waived. A separate five-query exact-conjunctive
diagnostic returned it in the top three for 5 of 5 queries, proving publication
and indexing while isolating the recall limitation. The observed miss pattern
is consistent with the current PostgreSQL source's
`plainto_tsquery('simple', ...)`: every surviving query token must be present
before source reservation can preserve a matching project result. Long,
unweighted record text is a secondary likely contributor to low cover-density
scores. These are source-supported explanations, not ablation proof. A
versioned OR/minimum-match or semantic mode, field weighting, and source-fair
fusion before global truncation are candidate improvements; none was
implemented or deployed in this publication turn.

The 20-query natural-language acceptance set and observed target ranks were:

| Query | Target rank |
| --- | ---: |
| reduce long running agent context without replaying transcripts | no match |
| continue solved work from verified closeout evidence | no match |
| revision probe before spawning coordinator worker | no match |
| new workers should not inherit full conversation history | no match |
| agent task snapshots are passive assertions not authority | no match |
| project brief must bind to current task head | 3 |
| delta only from exact predecessor otherwise snapshot | 3 |
| Nexus remote gateway for Cortex integrated access | no match |
| standalone Cortex local no network listener | 3 |
| reduce uncached input for cold agent recovery | no match |
| handoff receipt proves bytes not receiver acceptance | no match |
| revision checked immutable task state conflicts | no match |
| bounded revision gaps validation work | no match |
| project brief and work packet context budget | no match |
| heartbeat acknowledgement outbox delivery gap | no match |
| retention storage does not assign or approve tasks | no match |
| one brief retrieval and one exact read startup | 3 |
| readable recovery export versus exact database backup | no match |
| deterministic unchanged heartbeat no model call | 3 |
| resume agent work from bounded continuation packet | no match |

Matching target scores ranged from `0.0008421053` to `0.0159999996`; source
reservation placed every match at rank 3. The separate exact-title smoke query
also returned the target at rank 3 with score `0.0800000012`. The diagnostic
queries `transcript replay coordinator history`, `project brief continuation
packet`, `immutable revision checked task snapshots`, `unchanged model worker
transcript`, and `readable export exact database backup` returned the target at
ranks 2, 3, 3, 3, and 3 respectively.

## Current Implementation Boundary

The July 30 source candidate is preserved, but its `283/283` result is
historical: a July 31 rerun exposed one expired fixed-time SQLite fixture.
Context-retention hardening is now active under local source/build/test/evidence
authority. On 2026-07-31 the user additionally reactivated isolated local x64
packaging, Codex registration, and testing from the exact accepted source. Live
Nexus changes, installed or remote ARM64 changes, heartbeat activation, upload,
publication, and system-wide skill/template rollout remain gated. Retrieval
changes are limited to an opt-in versioned broad mode; exact behavior must
remain the default and unchanged.

The hardening candidate reproduces the documented 20-query set against a
sanitized local provider-neutral fixture through the real `broad` query path.
That fixture must reach at least 19/20 in the top three, preserve byte-for-byte
default/explicit exact ordering, and run alongside the existing reserved-
namespace isolation coverage. It is a deterministic regression for retrieval
behavior, not evidence about the installed ARM64 PostgreSQL corpus.

Acceptance evidence is source-first: create a deterministic no-overwrite source
bundle, verify it, extract it into a new disposable directory, and run the full
build and harness matrix from that extracted directory. The final manifest
binds the exact bundle, SDK/runtime inventory, bounded structured JSON evidence,
command summaries, explicit skips, and every required gate. Raw prompts,
responses, terminal streams, transcripts, credentials, and private reasoning
are prohibited evidence. A missing, blocked, skipped, or failed required gate
must leave aggregate acceptance false.
