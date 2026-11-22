# Cortex 2 asset efficiency and verification schedule

This is the source-development schedule for the optional file-backed asset
module. It is not a live installation result, a background optimization timer,
or permission to delete existing data. Cortex uses the normal release coordinate
`2.2026.10.08.05.26.A` for the next candidate; the major change does not itself change MCP, PAO, pipe,
SQLite, or signed product-catalog contracts.

## Current design limits

- Keep passive originals on disk under the configured current-user Cortex asset
  root. Content SHA-256 identifies bytes; an independent immutable reference
  identifies the metadata and selected namespace/asset identity. Preserve older
  exact references when a head changes.
- Import with a fixed 64 KiB buffer. The default asset admission ceiling is
  256 MiB, explicitly configurable up to 2 GiB. This is an admission setting,
  not a requirement to allocate that much memory.
- Retain a checksummed chunk manifest so selected reads validate complete
  affected chunks without repeatedly hashing or loading a large original.
  A byte response is bounded to 128 KiB before wire encoding. A consumer can
  independently verify the full original digest after completing a download.
- Store small INI references, not a second semantic SQLite database. Cap each
  canonical reference at 32 KiB, including escaping overhead. Exact opaque JSON
  parameters are limited to 16 KiB; larger generation recipes are separate
  pinned assets. Neither enters keyword search or default discovery previews.
- Search metadata only: identity, alias, title, meaning, tags, and Unicode text.
  Scan at most 512 heads per page, return at most 16 hits, and keep continuation
  explicit. A capped shard may sort at most 1,024 filenames. Do not enumerate
  and hydrate all objects or all reference bodies into a resident catalog.
- Make namespace selection explicit in the provider, not a client permission.
  Shared/project library access is not restricted by connection defaults; only
  personal resources require a loaded profile. Resource references reveal no arbitrary disk path and
  do not authorize filesystem, execution, or rendering access.
- No asset background worker, resident renderer, subprocess, network fetch,
  listener, recursive cleanup, automatic merge, or implicit publication.
- Missing/damaged exact reads now write a best-effort deduplicated identifier-only
  health log capped at128 entries, with an explicit repairRecorded result. No
  automatic repair, remap, deletion or execution queue is created.

These bounds now pass the focused source lane. Canonical-size, escaping,
generation/head publication, expected stream digest and head identity findings
were corrected. See `ASSETS_V2.md` for exact evidence: raw protocol through the
real server class has fixture adapters, not installed/Cerebral end-to-end proof.

## Next coherent verification pass

Before admitting the candidate, group these checks into one affected Release
build and focused run:

1. Round-trip exact binary bytes, metadata, multiline JSON, Unicode, and an
   optional generation recipe. Exercise content deduplication, updated heads,
   idempotent imports, expected-reference conflicts, and old pinned reads.
2. Search more generated metadata than one page can scan. Confirm continuation,
   generation invalidation, explicit namespaces, keyword meaning/alias matches,
   and small previews with no original bytes or generation parameter bodies.
3. Reject missing or tampered objects, chunk manifests, metadata, over-budget
   canonical references, unsafe paths/reparse components, cross-scope reads,
   interrupted imports, and frozen-pack per-file digest mismatches.
4. Exercise bounded cancellation and lock contention. Concurrent search must
   reject an in-progress publication rather than accepting a cursor built from
   a new generation and an old head.
5. Run real JSON-RPC initialize/resource discovery/search/selected read through
   the candidate host. The tool catalog remains eight semantic tools plus PAO.
   Absence of the optional asset root must leave core continuity available.
6. Validate the frozen 11-key icon corpus, exact catalog/content pins, Unicode
   alternatives, measured sizes, and public licensing references. Package
   verification is distinct from Cerebral native rendering and clipboard proof.

Measure allocation and elapsed time on a bounded generated fixture before
claiming an optimization gain. A passing constant-buffer test is not proof of
zero leaks or a long-running installed acceptance. Keep fixture data temporary;
do not check in bulk catalogs or multiple release trees.

## Follow-up optimization priorities

1. **V2 candidate: bounded cross-agent long-job scheduling (user requested
   2026-10-08).** A registered PowerShell visual-library import occupied the
   serialized PAO execution lane. Its MCP caller timed out after 300 seconds
   while execution continued, delaying other agents' short file operations.
   Stopping only the owned import and replaying it outside PAO cleared this
   incident; it did not fix the execution scheduler. Implement both prompt
   accepted-job receipts and scheduling separation: asynchronous replies alone
   leave a single FIFO blocked. Preserve existing synchronous compatibility,
   fixed typed operations, exact job/idempotency identity and bounded
   status/wait/log/cancel. Bound pending work, concurrent long processes,
   buffers and waiters; serialize conflicting mutations by their owned
   resource/workspace rather than holding unrelated reads behind a build or
   import. Disconnect/timeouts must not be reported as cancellation or trigger
   duplicate execution. Cancellation and completion release process handles,
   locks and waiters deterministically. Do not bypass root-code policy,
   authenticated pipe admission or user/System execution boundaries.

   Acceptance: two independent clients; a controlled long job remains active
   while a nonconflicting short stat/search completes before that job ends;
   conflicting mutations retain ordering; retries return the same job; queue
   saturation, cancellation, disconnect and restart/recovery remain bounded.
   Include a nested-call regression: a registered test script that requests a
   bounded PAO health/job snapshot must not deadlock its own enclosing job.
   The candidate implements deferred accepted receipts, bounded ordered-conflict
   scheduling and streamed recovery; see `PAO_BOUNDED_SCHEDULING.md`.
   Unknown external scripts remain globally serialized for mutations, while
   reads/health/job controls bypass the scheduler. Run protocol/PAO acceptance
   drivers outside PAO when they themselves issue/wait for mutations. Installed
   multi-agent acceptance remains separate; no live upgrade occurred here.
   Separate fixture evidence from actual installed concurrent-client proof and
   memory/latency measurements. This item is planned, not implemented or
   installed by the library admission checkpoint.
2. **Execution queue admission and crash recovery:** the installed V2 now reads
   terminal history on demand from SQLite, but the current mutation FIFO is still unbounded
   and recovery hydrates all queued/running requests. Add a deliberate bounded
   admission/back-pressure contract and proportional recovery tests before
   claiming protection against arbitrary queue growth. Do not drop queued work
   or change idempotency merely to reduce memory.
3. **Resident communication:** the installed V2 wires a shared owner/session
   composition with registered API-key/OS admission, MCP adapters and native
   Cerebral calls. Source, installed MCP and Cerebral consumer checks are
   distinct evidence. Complete tray management and actual consumer
   acceptance; see `NATIVE_RESIDENT_V2.md`. No HTTP/network listener is added.
4. **Cerebral consumer:** its owner is replacing its MCP process path with the
   supported native user pipe. Add bounded resource consumption,
   selected icon retrieval/cache, native rendering and clipboard acceptance.
   Clear cache preserves preference mappings; reset preferences is separate.
5. **Catalog scale:** measure paging cost before adding an index. If needed, add
   a rebuildable metadata-only index without making it a second source of truth.
   Disk accounting should distinguish unique original bytes, logical references,
   chunk/reference overhead, and unreferenced objects. Cleanup begins with an
   explicit audit/report, never automatic deletion.

Review these at meaningful release/checkpoint intervals or after measured
regression, not before each small agent turn. Live process/memory evidence,
source tests, package validation, and installed acceptance are separate claims.

## Future generated-image provenance

An image may pin a separate exact recipe containing the reported prompt,
negative prompt, generator/model identity and version, seed, sampler/scheduler,
steps, dimensions, selected input/reference digests, and reported parameters.
Keep unknown fields absent; never invent a seed or generator detail. Store only
non-secret reported provenance, not credentials or private reasoning. Provide
the complete exact namespace/identity/reference coordinates for the recipe,
not merely an unexplained digest.

This supports baseline recall and deliberate editing. A seed and prompt alone
do not guarantee pixel-identical reproduction across model versions, runtimes,
hardware, or nondeterministic implementations. The saved original remains the
exact source of truth; derived images receive their own byte identity and an
explicit parent reference. Image execution/rendering is not part of this asset
storage revision.
