# Cortex 2 passive assets and searchable icons

Source release: `2.2026.10.07.15.19.A`. Contract: `pegasus.assets/2`.
This optional file-backed module is installed and its bundled icon baseline is
explicitly imported into `knowledge.shared`. Live installed search, exact pinned
metadata and selected original-byte retrieval passed on October 7 local time
(October 8 UTC). Cerebral resource consumption, native rendering, clipboard and cache
acceptance remain its owner's integration work. No listener, service, renderer,
model, image generator or background optimizer was added.

## Storage and identity

Use the registered current-user Cortex state root, with an `assets` sibling to
the semantic data/config directories. Do not substitute a guessed local path or
an old store if the configured binding is unavailable. Normal product upgrades
and uninstall preserve this mutable data root.

- `objects/<content-sha256>`: exact passive originals, deduplicated across this
  owner's namespaces. They have no executable role.
- `objects/<content-sha256>.chunks`: fixed 64 KiB chunk integrity manifest.
- `refs/<metadata-sha256>.ini`: immutable canonical metadata, including namespace,
  stable ID, title, meaning, MIME, measured size, original digest, provenance,
  licenses, pack coordinate and optional parameters/details reference.
- `heads/<namespace-hash>/<shard>/<id-hash>.head`: current metadata reference.
  Namespace marker and generation invalidate search continuations on mutation.
- `scope.ini`: bounded resource-discovery hints; defaults to `knowledge.shared`.
  The historical `assets expose` command adds a catalog entry, not a permission.
  Explicit valid shared/project selections remain readable without that entry.
- `health.txt`: at most 128 deduplicated identifier-only repair events for missing
  or damaged exact references. No bodies, prompts, paths, secrets or raw output.

An asset ID is stable; its content digest identifies bytes, and its reference
digest identifies the complete exact metadata/version. Changes require the
expected current reference. Old pinned metadata and original bytes remain
readable, never silently redirected to a newer head. Equal imports are unchanged.
No asset originals or catalog entries enter the semantic SQLite tables.

## Explicit local ingestion

The installed product command resolves its own protected current-user asset
root. It accepts no database/root override. Library tests use isolated roots.

```text
PegasusCortex.exe assets import --file <absolute-file> --metadata-file <absolute-json> [--expected-ref <metadata-sha256>]
PegasusCortex.exe assets verify-icons --pack-root <absolute-directory> --catalog-sha <sha256> --content-sha <sha256>
PegasusCortex.exe assets import-icons --pack-root <absolute-directory> --namespace <namespace> --catalog-sha <sha256> --content-sha <sha256>
PegasusCortex.exe assets expose --namespace <namespace>
```

The generic metadata input has fixed camelCase fields `namespace`, `id`, `title`,
`meaning`, `mediaType`, and optional `tags`, `alias`, `text`, `license`, `source`,
`pack`, `packVersion`, `parameters`, `detailsRef`. Unknown wrapper fields reject.
`parameters` is **exact JSON text**, up to 16 KiB UTF-8: arbitrary valid keys,
arrays and values are preserved without normalization or interpretation.
Canonical metadata, including escaping, must fit 32 KiB. Null metadata strings,
invalid identifiers/JSON, unsafe paths and over-budget input fail boundedly.
`source`, when supplied, is credential-free HTTPS metadata; it is never fetched.

Larger generation recipes/settings/results are ordinary separately pinned files.
`detailsRef` names an exact metadata reference in the same explicit namespace.
Resolution returns its actual ID and exact read coordinates. Do not inline large
recipes or files into search results, global guidance or startup context.
Never submit secrets as parameters or provenance. Files remain passive even if
their declared MIME is executable, script, HTML or model content; this module
does not invoke them or attest to malware safety.

Import streams 64 KiB at a time. Default file admission is 256 MiB; the library
option can deliberately admit up to 2 GiB without allocating that size. The
production CLI currently uses the default ceiling, not an undocumented override.
Every generated path is bounded to 240 UTF-16 characters; unsafe components,
ADS, traversal and present reparse ancestors reject. Windows ownership/ACL checks
remain the boundary for mutable state, not display names or namespace strings.

## Read-only MCP resource contract

The standard `resources/list` advertises small namespace discovery entry points;
`resources/templates/list` advertises these four implemented templates:

```text
pegasus-asset://v2/search/{namespace}{?q,limit,after}
cortex://{assetId}
pegasus-asset://v2/reference/{namespace}{?ref}
cortex://{assetId}?part=bytes{&offset,length}
```

`assetId` is the globally unique pinned metadata reference: it binds namespace,
record identity, metadata and original-content digest without revealing any
disk path. `cortex://<assetId>` retrieves metadata; `?part=bytes` retrieves the
selected original range. It resolves one exact reference file, not a catalog
scan. IDs select data, never commands or arbitrary files, and are not credentials.
Existing `pegasus-asset://v2/record/...` and `bytes/...` coordinates remain
readable compatibility aliases; new previews emit `cortex://` references.

Call `resources/read` with one URI. URI authorities are reference identities,
not network hosts; no connection is opened. Namespace and provider-root scope
are validated on every request. Unknown fields, malformed/dot-segment paths,
mismatched IDs/references fail explicitly. The discovery list is not an access
allowlist: clients can select valid shared/project namespaces that are not
listed. Personal assets require the loaded profile and are withheld if its
generation changes during retrieval. This remains owner-local library access,
not remote-client namespace authorization.

Search matches all supplied whitespace-separated terms, case-insensitively,
against ID/alias/title/meaning/tags/Unicode text. It does not search file bytes,
generation recipes or opaque parameters. Current heads only are searched.
Default page: four previews; maximum: sixteen. At most 512 metadata heads are
scanned per request; each filename shard is capped at 1,024. Follow `nextUri`
even when a page contains zero hits. Ordering is deterministic by sharded ID hash,
not semantic/vector ranking. Cursor binds namespace, exact query, limit and
generation. Changed query/namespace/limit or a committed mutation invalidates it.

A preview contains ID, exact metadata reference, label/meaning (at most 160
Unicode scalars), MIME, size, optional alias/Unicode fallback, and one exact
metadata URI. It contains no artwork, parameters, unrelated history or empty
optional fields. A shortened meaning carries `preview=true`; exact metadata
retains the original. Exact metadata additionally exposes byte digest, bytes URI,
provenance/licensing, pack/version and supplied parameters/details coordinate.

Selected bytes use standard MCP blob content. Default range is 64 KiB, maximum
128 KiB; offset must be a multiple of 65,536. A small icon is returned whole.
Large originals are requested in ranges; complete affected chunks and the
pinned chunk manifest are verified, without rereading the entire original for
every range. Response metadata echoes namespace/ID/reference, offset, length,
total size, selected-byte SHA-256 and expected full-content SHA-256. The consumer
must assemble and verify the full original before its own content admission.
Search never downloads artwork or causes rendering/execution/publication.

The tool catalog remains eight semantic tools plus PAO. Asset discovery does not
add a general file-access operation, broad AllowedRoots, or FullAccess. A broken
optional exposure configuration disables assets with a bounded startup diagnostic
while core continuity remains available. Retrieval failures do not substitute
another file; bounded repair logging is best effort and reports `repairRecorded`.

## Frozen baseline and user choices

Producer contract stays `pegasus.asset-pack/1`, pack `pegasus.chat-icons` version
`1.0.0`. Eleven Microsoft Fluent System Icons include status and chat roles,
Unicode alternatives and public source/license metadata. Core inventory is
14 files: catalog, two licensing files and eleven SVGs. Exact pins:

```text
catalog SHA256 04aa6881acc2f65edab2c046409cb87f71e932081fb0ac21bea9f2f526aa0011
content SHA256 63f60ac78551b78f4796222a68a323c56267713c94f85f91e1bc3e910a9a427d
```

The source contains the producer's wider evidence/docs tree; publish includes
only the 14 declared files. Admission requires both pins, strict INI metadata,
exact lengths/digests, original licenses/notices and the producer-compatible
passive SVG allowlist. No SVG renderer was added. URL/license provenance is not
a new independent upstream-license audit.

Pack import validates all frozen files and preflights existing heads, then commits
each asset atomically with expected-reference concurrency. It is **not** one
cross-file transaction: later I/O/cancellation/conflict may leave completed files.
Failure reports `committed` and `partial`; partial exit category is 7. Replay is
idempotent. Foreign heads, malformed versions, downgrade and same-version content
or metadata edits reject rather than being adopted/overwritten.

Baseline packages contain no personal records/preferences or generated artwork.
New versioned packs can add icons; old exact references remain usable. User role
choices are separate from stable artwork identities. Cerebral owns picker,
AI-to-chat structured icon use, selected-only cache, native rendering/clipboard,
Clear cache, Refresh selected icons, and explicit Reset preferences. It currently
requires separate resource-consumer acceptance; the producer checks here do not
establish its retrieval, cache or rendering integration. Default good/
go/stop labels do not authorize an action; cat-for-Go remains a user choice.

For later image generation, retain the reported exact prompt, model/version,
seed when supplied, settings, inputs and outcomes in parameters or a pinned recipe.
Do not infer missing facts. The original content digest is the exact baseline;
prompt/seed alone do not guarantee identical pixels across providers/versions.

## Evidence and remaining gates

Installed `2.2026.10.07.15.19.A` verified and imported the frozen 14-file pack:
11 icons plus catalog, license and notice, totaling 20,752 original bytes.
Raw JSON-RPC through the installed executable returned all 14 references,
matched `status.success`, preserved its pinned upstream URL/license metadata,
and returned the exact 711-byte SVG with matching metadata/content digests.
Identical replay preserved the count and all exact ID/reference pairs. The same
resident host survived adapter EOF; the final sample was approximately 121 MiB
working set and 52 MiB private memory. No installation, broad scan, personal-data
import, network download or configuration change occurred during this import.
See `artifacts/validation/assets-v2-installed-current.json`. Rerun the bounded
read-only lane with `tools/Test-PegasusCortexInstalledMcp.ps1 -AssetChecks
-RequireResidentLifetime`; this checks the explicitly imported baseline and does
not ingest files or render artwork. Consumer integration and sustained-load
acceptance are not established by this result.

Warning-strict affected Release builds passed, followed by eight combined checks.
The fixture exercised 521 generated heads, a 16 MiB original, a 70,000-byte exact
range, 64 repeated reads, 512-head scan continuation and the real frozen pack.
After forced collection the latest repeated-read fixture retained 4,152 fewer managed
bytes than its before sample. This is bounded fixture evidence, not a production
leak guarantee. The actual dangling-link probe was skipped because OS link
creation was unavailable. Raw JSON-RPC uses the real server class with fixture
application/execution adapters; it is not installed/Cerebral end-to-end evidence.

See `ASSET_V2_OPTIMIZATION.md` for outstanding FIFO/recovery bounds, resident MCP
transport acceptance, consumer integration, measured metadata index needs and
explicit disk-accounting/cleanup. No installation, old-state migration, signing
trust change, live process restart or network deployment occurred in this slice.

The portable passive icon payload is `artifacts/release/assets/pegasus.chat-icons.assets.zip`:
10,632 archive bytes and 20,752 expanded bytes across the 14 declared files.
Its SHA-256 is `7ea010e5cb13ecb7e7f94b0d9c271bfa518e8d0691cc8c51dc5a786b9500018c`.
It is a data add-on, not a signed complete Cortex installer. The bounded evidence
receipt is `artifacts/validation/assets-v2-current.json`.

## Optional visual libraries, 2026-10-08

The original fourteen-reference baseline is preserved. Four optional data-only
modules now add 34,271 individual SVGs, sixteen exact license/notice files and
four compact pack descriptors in `knowledge.shared`. They reuse the installed
resource/store contract, not a new semantic schema or resident renderer. Exact
bytes deduplicate while distinct source/style/license metadata stays distinct.
See `VISUAL_LIBRARIES.md` and `artifacts/release/assets/visual-library-packages.json`
for the pinned contents, per-artwork rights, conservative flag exclusions and
validation evidence. Larger color/gradient/stroke originals are passive data;
the frozen eleven-icon renderer is not their admission authority. Native
Cerebral consumption and visible picker/rendering are separate acceptance gates.
