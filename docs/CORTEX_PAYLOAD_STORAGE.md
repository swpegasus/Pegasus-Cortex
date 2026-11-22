# Cold payload storage and lifecycle protection

The current source adds an opt-in cold payload tier for archived local V2
records. Ordinary store creation stays schema2 and does not migrate or offload
anything automatically. Schema3 retains record heads, immutable revisions,
hash chains and idempotency metadata while allowing verified compressed payloads
to reside beneath the same database's fixed `cold-v1` security boundary.

## Operator boundary

The normal launcher routes `--payload-storage` to a fixed helper in the selected
protected Host image. The helper attaches to the existing ordinary owner and
validates its process generation. It never starts an owner, opens a database,
selects another user or accepts a database path.

```text
PegasusCortex.exe --payload-storage upgrade
PegasusCortex.exe --payload-storage offload --stdin
PegasusCortex.exe --payload-storage rehydrate --stdin
```

Upgrade selects expected schema2. Offload and rehydrate read a closed version1
JSON body from standard input, containing the matching action, expected schema3
and 1–16 exact record coordinates:

```json
{
  "version": 1,
  "action": "offload",
  "expectedSchemaVersion": 3,
  "records": [
    {
      "namespace": "project.example",
      "kind": "task",
      "recordId": "example.archived-task",
      "expectedHeadRevision": 2,
      "expectedHeadHash": "<exact current archived revision hash>"
    }
  ]
}
```

The coordinate and hash above are placeholders. Selection must come from the
actual archived head. The request body is limited to16KiB and rejects unknown or
duplicate fields, paths, callbacks and alternate execution contexts.

The owner handles this fixed control outside ordinary request admission, then
closes and drains its real shared admission and checks the ordinary owner's
deferred Engine work. This avoids waiting for the control request itself. The target is the
actual resident store. Arbitrary process absence, an `--offline` flag or a second
lock is not a substitute for that product gate.
The gate covers supported owner callbacks; it is not an OS-wide exclusion of
arbitrary independently opened SQLite clients.

The original operation, rollback and cleanup remain owned after caller
cancellation. The resident schema is revalidated before ingress reopens. If
admission cannot be re-established, semantic ingress stops and drains before the
shared gate is released; ordinary execution and normal Exit remain separate.
An unresolved drain retains ownership. The client never automatically resubmits
after losing an acknowledgement. Current/last RID correlation is bounded and
process-local, not durable replay protection.

## Data and recovery invariants

- Schema3 upgrade is explicit and transactional. Old schema2 insertion shapes
  cannot silently create tier-aware rows. Compatible installed readers must
  precede any real upgrade.
- A batch selects archived records by exact head revision and hash, with at most
  256 selected revisions and16MiB of charged payload bytes. Bounds refuse the
  batch rather than truncate history. Any nonselected record's reference keeps
  a shared payload hot, including historical references.
- Cold publication is create-only, flushed and reopened for exact verification.
  Its read lease is retained through the database transaction. Only then can
  the hot bytes be replaced by a versioned descriptor. Record and replay
  metadata remain intact.
- Exact reads, replay, inherited content, head listing and doctor share the same
  verified payload accessor. Exact/replay/inherited reads preserve their earlier
  16MiB ceiling; configured head and doctor bounds remain distinct. A missing or
  corrupt cold object is `cold_payload_unavailable`, never an absent record or
  an empty body. New active heads rehydrate verified cold content first.
- Backup hydrates its owned staging snapshot before verification and publication.
  A normal backup remains self-contained and portable. Restore rejects a raw
  snapshot that still depends on external cold files.

Receipts distinguish hot bytes evicted, hot bytes restored and shared payloads
kept hot. They do not claim RAM release, SQLite file shrink or physical disk
reclamation. Rollback may leave an immutable published object; this unit performs
no orphan sweep, cold-object deletion, record deletion or implicit `VACUUM`.
If a publication lease reports cleanup failure after the confirmed transaction,
the original receipt remains available with `CleanupIncomplete=true`. The owner
control reports a partial `payload_cleanup_incomplete` result with the confirmed
tiering receipt, rather than describing it as a canceled/no-effect operation.

## Durable protection

The source store exposes `SetPayloadProtectionAsync` and
`ReadPayloadProtectionAsync` for one exact task/knowledge record or an entire
namespace. Pins and holds cover the whole selected history, including future
revisions. They are separate flags; either prevents offload. They have no automatic
expiry, and releasing them is another explicit CAS-checked immutable revision.
These are library APIs; the existing launcher control has no protection command yet.

Protection is stored as nonsearchable `payload-protection` records using the
existing revision, idempotency, hash, backup and doctor machinery. Task/knowledge
bodies and public record schemas are unchanged. Generic namespace/head/revision
counts include the retained policy records. Policies are read by fixed derived
identity, with closed versioned metadata and exact immutable-hash verification;
malformed, lost-head or nonlatest policy state is unavailable, never unprotected.

Applying protection requires an existing target and hot residency for every
affected semantic revision. If some payload is cold, the operation refuses with
`payload_protection_requires_rehydration`; explicit verified rehydration must come
first. Missing or invalid residency is an error. A failed check rolls back the
new policy and its replay metadata. Rehydration itself remains allowed under a
pin/hold. Both initial offload admission and final eviction recheck protection
inside the same immediate SQLite transaction.
Explicit rehydration accepts an exact validated current active or archived head:
this restores older cold revisions behind a newer active head without changing
its lifecycle, search projection or revision hash. Offload still requires an
archived nonsearchable head. Rehydration is bounded by the same selection limits.

For histories that have grown beyond that whole-record selection limit, the
source library also exposes `RehydratePayloadHistoryAsync`. It takes one exact
current head coordinate and an optional `(revision, revisionHash)` cursor. Each
committed page visits at most50 historical rows and restores at most16MiB of
charged distinct cold payloads. Fixed head/cursor/descriptor proofs and the
existing promotion helper fit within the batch metadata allowance; ordinary
store-schema admission is separate. `InspectedRevisions` reports the history,
cursor and two head-proof rows, rather than pretending these are all unique
historical revisions or all underlying metadata reads.

The current head is checked before and after each page. Historical immutable
metadata and local hash-chain continuity are verified. A cursor identifies an
actual immutable revision of that same record; the query uses a real revision
keyset and does not reread the earlier history. Byte exhaustion stops before the
next unprocessed payload and returns the last committed cursor. A single item
larger than the per-page ceiling refuses explicitly. Repeats are safe, prior
committed pages remain intact after a later failure, and source/replay metadata
is never deleted. `ReachedHead` means range exhaustion only: a caller can choose
a later valid cursor, so successful pinning still requires the protection setter's
independent check that the entire affected history is actually hot.

## Age and soft-size batches

The source store exposes `PlanPayloadRotationAsync` and
`RotateArchivedPayloadsAsync`. They require the existing schema3 opt-in, an explicit
namespace, a UTC archive cutoff no later than the store clock, and a nonnegative
soft target for hot compressed bytes. A zero target considers all age-eligible
records; a positive target avoids starting a batch when the namespace is already
within that target. Active/protected/shared data can leave the target unmet. This
is not an admission quota and never permits deletion of records or cold objects.

Eligibility requires archived state, no searchable head, and archive/update plus
server-stamped revision times at or before the cutoff. Backdated client metadata
cannot age a newly written revision. Each candidate page considers at most256 heads
using a kind/ID keyset cursor, selecting at most16 records within the existing
256-revision/16MiB executor bounds. An over-bound record is reported separately;
history is never truncated merely to fit a batch. Hot-byte observations count
unique compressed blobs within the namespace, not SQLite file length.

Reference checks interpret only existing typed same-namespace task dependencies
and knowledge supersession IDs. They do not inspect bodies or interpret opaque
provenance/evidence strings. Incoming references from nonselected heads protect
their selected targets; unresolved outgoing references protect their source.
Protection propagates through the selected graph when a candidate is removed.
A closed set of selected archived records may rotate together. Metadata, exact
references, replay information and verified cold payloads remain available.

The reference proof streams current metadata and verifies current-head hashes,
content identity and latest-head state. Its memory is bounded to selected records
and their bounded typed references. It scans the namespace, so the256 candidate
page is **not** a total work or latency bound. Cancellation is checked while
streaming. Before eviction, the executor repeats age/reference/protection checks
under its real immediate transaction; a new reference/pin or changed head refuses
the original plan without resubmission. Use `RotateArchivedPayloadsAsync` for this
workflow; serializing a reviewed selection into the separate explicit offload
command does not preserve the in-process rotation guard.

The rotation result preserves a confirmed commit if a later metrics observation
fails: observed bytes/target satisfaction are unavailable and the issue is explicit.
No automatic retry, physical-space reclamation or fixed-duration claim is implied.

The admitted owner rotation entry can additionally attach a metadata-work allowance
and cooperative time budget to a pass. These count admitted metadata observations
and signal cancellation; they do not count SQLite's internal visited pages or
guarantee an OS I/O deadline. The budget is shared across planning, reference proof,
selected-history charging and final eviction validation. A budget exhaustion returns
only after the original read/transaction/rollback/lease cleanup has returned. An
uncommitted pass is `deferred_budget`, with unavailable counters/target state kept
null and no cursor advanced. Create-only immutable cold objects may remain after
rollback; that possibility is explicit, not described as physical cleanup success.
A confirmed commit stays confirmed if a later observation consumes the remaining
allowance. The owner must preserve that distinction when scheduling later passes.

## Owner policy and recovery

The Storage page uses the existing ordinary owner and its closed control route.
The policy defaults to disabled and requires explicit namespaces. Preparing cold
storage is a separate deliberate format upgrade; enabling a policy never silently
upgrades an existing database. Once enabled and prepared, the owner runs one bounded
namespace batch per cadence, using the same admission and active-work checks as
manual maintenance. Shutdown cancels and joins the actual owned pass before its
composition is disposed. A busy owner defers work instead of starting another queue.

The owner-private policy uses revision-checked saves. A save publishes a flushed
candidate through strict file replacement, retaining bounded pending/previous
evidence if publication or cleanup is uncertain. It does not replace a missing or
ambiguous selection with defaults. A verified selected policy with incomplete
cleanup is shown separately from an unknown outcome; automation stays paused.
After Refresh, the operator can make a fresh explicit revision-checked cleanup or
review save. The original request is never automatically replayed. Recovery files
without a proven selected/predecessor relationship require operator review.

The metadata allowance and cooperative time budget limit admitted pass work.
They are not a hard disk-operation deadline or a quota on retained records.
Scope retirement, permanent pruning and physical-space reclamation remain separate
work; cold payload rotation preserves record history and immutable payload objects.

## Qualification

Source builds, owned SQLite/file fixtures, injected control/lifetime fixtures,
real operator execution, power-loss durability and installed migration are
separate evidence levels. No existing machine database is upgraded by compiling
or testing this source. The protection, rotation, history rehydration, bounded owner
policy and Storage page are source changes awaiting their matching release and
installed qualification. The installed 14.26 baseline does not contain this owner
policy. Existing databases and settings are preserved; this document is not proof
that an installed database has been upgraded or that automatic rotation is enabled.
