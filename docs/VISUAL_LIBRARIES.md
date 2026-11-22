# Optional visual libraries

Prepared 2026-10-08 for the installed Cortex 2 passive asset contract. These are
data modules, not another runtime, semantic database, renderer or model tool.
Installed admission and consumer acceptance are recorded separately below.

## Sources and redistribution

| Collection | Pinned source | Selected SVGs | Original bytes | Artwork license |
| --- | --- | ---: | ---: | --- |
| OpenMoji | `aeb8bb3a59e2de39c754ac79180c8131c906acea` | 9,130 | 23,173,638 | CC-BY-SA-4.0 |
| Fluent Emoji | `1ffb34c752ecf5d402f04cfb4b392c77f57c54bc` | 7,885 | 155,552,178 | MIT |
| Svgmoji | `fb4bf0c2e6188c0cf70642d2931e92b9b7b0ae65` | 14,121 | 85,467,224 | Underlying artwork licenses, not wrapper MIT |
| Remix Icon 4.7.0 | `4ef50878f17accbed89ef2691bdc357562ed0a17` | 3,135 | 1,876,768 | Apache-2.0 |

Source repositories: [OpenMoji](https://github.com/hfg-gmuend/openmoji),
[Fluent Emoji](https://github.com/microsoft/fluentui-emoji),
[Svgmoji](https://github.com/svgmoji/svgmoji), and
[Remix Icon](https://github.com/Remix-Design/RemixIcon/tree/v4.7.0).
The separately bundled eleven-icon baseline is Fluent **System Icons**, not
Fluent Emoji, and its frozen producer/parser is unchanged.

Licenses and attribution accompany the data. Fluent Emoji is
[MIT](https://raw.githubusercontent.com/microsoft/fluentui-emoji/1ffb34c752ecf5d402f04cfb4b392c77f57c54bc/LICENSE).
OpenMoji's [FAQ](https://raw.githubusercontent.com/hfg-gmuend/openmoji/aeb8bb3a59e2de39c754ac79180c8131c906acea/FAQ.md)
and packaged CC-BY-SA-4.0 license explain attribution and ShareAlike for
adaptations. Preserve that artwork's license rather than relabeling the collection
as MIT or treating it as permission for trademarks/endorsement.

Svgmoji aggregates Noto/Blobmoji (Apache-2.0 image resources), OpenMoji
(CC-BY-SA-4.0) and Twemoji graphics (CC-BY-4.0). The MIT wrapper is not an
umbrella license for the art. The module contains exact license copies and
separate notices for all four families. Historical Noto image licensing is pinned
to [v2.038](https://raw.githubusercontent.com/googlefonts/noto-emoji/v2.038/LICENSE),
not the newer font-oriented root license. Noto/Blob regional/subdivision flag
variants with separate rights were conservatively omitted: 522 entries.
No fonts, giant sprite sheets, PNG/3D variants or unrelated repository files
are selected. No new artwork edits were made; Svgmoji's prior upstream SVG
optimizations remain part of its provenance.

Remix's current 2026 license restricts competing/standalone icon-library
distribution. This module uses only the verified
[Apache-2.0 v4.7.0 release](https://raw.githubusercontent.com/Remix-Design/RemixIcon/v4.7.0/License).
Never substitute newer Remix bytes under the older license. Brand symbols do
not grant trademark rights.

## Acquisition, packaging and admission

Source-controlled tools under `tools/assets`:

1. `Get-VisualLibraries.ps1` streams fixed HTTPS commit archives and selects
   original individual SVG bytes. It rejects selected archive links, bounds each
   SVG to 2 MiB, parses only root dimensions with external XML/DTD disabled,
   and emits exact licensing files plus a streaming `assets.jsonl` manifest.
2. The next product build provides the installed command
   `PegasusCortex.exe assets import-visual-library --manifest <absolute assets.jsonl>
   --sha256 <pin>`. It resolves the protected registered current-user asset root,
   not a caller-selected database/state path. `CortexVisualLibraryV2` owns the
   shared streaming validator/importer; the transient development helper is
   only a small wrapper and is never distributed as another installed product.
3. `Import-VisualLibraries.ps1 -Library <selected names> -Executable <exact installed
   PegasusCortex.exe> -PackRoot <absolute extracted module parent>` runs the product
   command for only those explicit selections. It has no implicit all-four default.
   Whole-manifest pins bind the declared paths, content hashes, metadata,
   versions and licensing. Preflight rejects conflicting/foreign heads before
   writes; license entries precede art and become exact `detailsRef` links.
   Each asset commit is atomic; a multi-file module is not a single transaction.
   Completed identical heads are reused. Interrupted imports can be replayed;
   replay is not automatic original repair. Exact byte reads still verify
   existing stored bytes/chunks.
4. `Package-VisualLibraries.ps1` creates four optional data-only ZIP modules
   after verifying every declared file digest. It includes only `assets.jsonl`,
   `acquisition.json`, declared SVGs and licenses/notices. It does not package
   task receipts, user preferences, private data, executables or signing keys.

Canonical modules: `artifacts/release/assets/{openmoji,fluent-emoji,svgmoji,remix-icon}.zip`.
`visual-library-packages.json` gives their exact byte counts and digest pins.
Prepared manifests and current acquisition/admission receipts are under
`artifacts/release/assets/libraries/<source>`. No extra rollback generations.
These modules are not newly installed feature selections in the main NSIS
installer. `Attach-VisualLibraryModules.ps1` adds the unchanged ZIPs beside the
companion product under `optional-data/` before the outer installer inventory is
signed. Its `visual-library-modules.v1.json` uses only relative ZIP filenames,
independent upstream module versions, exact package/manifest pins, sizes/counts,
and the bounded complete list of declared licensing files, IDs and digests.
It contains no this-machine paths, private records or executable module.
Core setup does not copy, extract, inspect or admit these optional archives.
They remain redistributable sidecars, not newly selected wizard features.
Explicitly extract only a chosen archive with a bounded admitted archive tool,
then call the installed import command with its exact manifest pin. This owner-
local data operation is outside the elevated machine installer and never opens
or changes semantic SQLite. Completing the machine installation alone does not
claim the artwork has been imported. Existing installed assets are left intact.

Long one-time imports must not occupy the serialized PAO lane. During this
checkpoint a synchronous registered PowerShell import delayed other agents'
file operations and timed out at the MCP boundary after 300 seconds while the
process continued. Only the exact task-owned helper/script were stopped and
resumed outside that lane. The host/service/credentials were unchanged; a
small PAO stat then succeeded. A lost tool reply means effects are unknown,
not canceled. Inspect exact state before any replay. The bounded-scheduling
implementation subsequently added deferred admission and conflict scheduling;
see `PAO_BOUNDED_SCHEDULING.md` for its source and installed evidence. Reads/health
stay responsive, but arbitrary import scripts retain a global mutation reservation.
Package preparation alone does not change installed capabilities; discover the
actual wait field in the live tools before using it.

## Identity, overlap and memory

Namespace: `knowledge.shared`. Pack IDs: `visual.openmoji`,
`visual.fluent-emoji`, `visual.svgmoji`, `visual.remix-icon`. Pack version is the
exact pinned upstream revision. Stable icon keys include family/style, readable
meaning, and a short original-path digest to distinguish variants/collisions.
Exact byte objects deduplicate by SHA-256; metadata references remain separate
when sources/styles/licenses differ. Similar-looking art is not auto-merged.

Metadata retains meaning, Unicode when known, variant, original path, source
revision, upstream metadata and SVG dimensions. Unknown Fluent skin-variant
Unicode is omitted, not falsely assigned its unmodified base glyph. Generation
recipes and model parameters may remain opaque valid JSON in future assets;
this corpus is existing vector art, not generated-image reproducibility evidence.

No catalog or originals are loaded into semantic SQLite/startup briefs. Import
streams one bounded entry/file at a time; resident retrieval remains bounded.
Search previews select exact coordinates; only selected bytes are delivered.
User preferences/alias choices belong to the loaded personal layer, never a
redistributed library. Purging a Cerebral cache must not delete Cortex originals.

## Cerebral consumer contract

The existing native `op=resource` path delegates to the same reader as MCP;
no extra MCP process/tool or model-facing raw resource gateway is required.

```json
{"id":"request-id","op":"resource","uri":"cortex://<exact metadata ref>"}
```

Search: `pegasus-asset://v2/search/knowledge.shared?q=<terms>&limit=4`
(maximum 16). A page scans at most 512 heads; follow `nextUri` even if a page
has no hits. A changed namespace generation makes old continuations stale;
do not spin during an active import. Search is a bounded scan, not a vector
or inverted keyword index.

Exact metadata: `cortex://<ref>`. Exact bytes: the same URI with `?part=bytes`,
default 65,536 bytes, maximum requested length 131,072, offset multiple 65,536.
Validate chunk digests and assembled full content SHA-256 before caching.
`detailsUri` selects the actual linked license metadata; its `bytesUri` returns
the license text. Namespace/identity/digest are echoed and remain exact.
Keep absent, corrupt, denied, stale and unsupported-format states distinct.

`detailsUri` is one document, not a promise to include every required NOTICE or
attribution. The frozen baseline does not set an aggregate artwork detailsRef.
There is currently no provider-owned per-asset export resource or typed list of
all required licensing documents. Complete module ZIPs include all declared
license/NOTICE bodies. Selected-artwork export must independently include the
actual required documents, not just SPDX labels or readable reference strings;
until it does, disable redistribution-oriented export. Exact current coordinates
were supplied to Cerebral (two documents per baseline/OpenMoji/Fluent/Remix and
ten for Svgmoji), without claiming a new runtime capability.
The portable sidecar index now also declares this complete bounded document
inventory with passive IDs/digest pins. These are not runtime reference URIs:
consumers must resolve/validate admitted exact coordinates separately. This
index does not silently turn `detailsRef` into an aggregate export contract.

`renderProfile=passive-svg-original` is not sanitation or renderer acceptance.
The eleven-icon MIT/20px/monochrome baseline consumer cannot silently admit
larger color/gradient/stroke SVGs. Cerebral owns an independently bounded safe
renderer or rasterization/cache adapter, chat picker, selected insertion,
accessible labels/Unicode meaning, attribution display and cache purge.
Never expose arbitrary SVG/HTML execution or hydrate this catalog into models.

## Validation status

Acquisition and explicit installed-store admission completed for all four pinned
repositories. The existing fourteen-reference baseline is preserved; current
inventory has 34,305 immutable references. Four compact catalog descriptors
replayed with identical coordinates. No runtime installation or semantic schema
change occurred. Only public passive artwork/licensing data was admitted.

Warning-strict helper Release build passed with zero warnings/errors. Seven
isolated fixture checks passed: initial admission, exact replay, linked license,
wrong-pin rejection, tampered original rejection, traversal rejection and
foreign-head conflict before writes. These are fixture tests, not installed
consumer or renderer evidence.

All four real ZIP modules passed independent decompression/inventory checks:
every one of the 34,287 declared artwork/license files matched its digest;
manifest pins, exact inventory and passive-data-only boundaries matched. Total
compressed size is 80,627,689 bytes (76.89 MiB). Evidence:
`artifacts/validation/visual-library-packages-current.json`.

Raw JSON-RPC through installed `PegasusCortex.exe` passed exact sample metadata,
original bytes and full digest, and exact linked license bytes for each of the
four modules. A bounded Fluent Emoji keyword query returned four previews in
one page, measured 29 ms. Twenty-five status/health pairs passed, stderr was
empty, the adapter exited zero, and resident PID 33804 survived unchanged.
Working set measured 141,389,824 then 149,127,168 bytes; private bytes measured
64,446,464 then 71,655,424. These are bounded samples, not sustained-load or leak
proof. Evidence: `artifacts/validation/visual-libraries-installed-current.json`.

The larger combined baseline-walk driver did not complete in the diagnostic
window and was stopped only by its exact owned process identity; it is not a
pass. The focused new-module lane above completed in about two seconds. Do not
repeat whole-catalog walks as a startup/test default merely to reread known
baseline coordinates. The earlier installed baseline proof remains separate.

Cerebral reported an actual Source/native canary pass after imports became idle:
exact baseline/OpenMoji metadata, default bytes/full hashes, range clamp, bounded
keyword search and invalid range rejection. Receipt:
`3c6e06d7a6f245e2bc7ac3e7dce54b15`. Its earlier search during imports returned
`asset_busy`, not an auth failure. This is peer-reported native consumer evidence,
not Cortex-owned UI/renderer/clipboard or installed Cerebral acceptance. Larger
SVG rendering and the visible optional picker remain separate consumer gates.
Its subsequent consumer-library lane passed exact baseline/OpenMoji/Fluent
metadata/full hashes, owned temporary-cache roundtrip and bounded search through
the actual Source pipe (receipt `765ade78254b4f43a1c77d955f31d774`), with zero
model calls/clipboard/Source writes. Its export review correctly rejected
reference-only licensing as an incomplete redistribution bundle.

A streaming read-only inventory counted 31,040 distinct original objects,
243,399,131 original bytes, 2,078,115 additional chunk-manifest bytes,
47,481,673 reference bytes and 2,195,608 head/generation bytes. These are logical
file lengths, not NTFS allocated-cluster size. Exact-byte deduplication saves
space without merging different source/license metadata. No catalog was loaded
into resident memory or semantic SQLite to obtain these counts.

Only owned diagnostic drivers were stopped; the shared host/service and other
agents' processes were preserved. This historical data checkpoint did not
implement the PAO contention correction. The subsequent accepted-job receipts,
bounded scheduling and nested-call coverage are recorded separately in
`PAO_BOUNDED_SCHEDULING.md`. A validated disposable-cache
cleanup was denied before execution; upstream download caches and small fixture
trees remain, with no alternate deletion attempt or user database deletion.
