# Expandable icon packs: Cortex integration checkpoint

Updated: 2026-10-08. Status: installed Cortex 2 asset contract, admitted frozen
baseline and four additional optional public visual modules. File admission,
searchable metadata and selected MCP resource delivery are implemented.
The exact contract/results are in `ASSETS_V2.md` and `VISUAL_LIBRARIES.md`.
Cerebral native consumption and visible rendering/picker acceptance are distinct
from Cortex admission; the earlier design sections below are not runtime proof.

## Ownership and scope

Cerebral owns the reusable licensed pack, its authoring/export UI, and its
optional chat adapter. Its public SDK/DLL baseline does not change. Cortex owns
the pack adapter, bounded discovery/reference resolution, and MCP
mapping in this project. No Cerebral source, live model, installed product,
database, registration, or network listener changes belong to this checkpoint.
Existing runtime/memory work remains separately owned and is preserved.

The producer's frozen pack and pins have been received and copied into the
Cortex source asset lane. The original contract is reused rather than rewritten.
Earlier design examples below are not installed capabilities or guarantees.

## Received source references

Cerebral forwarded these original owner-supplied candidate/reference links:

- OpenMoji: https://openmoji.org/ and
  https://github.com/hfg-gmuend/openmoji
- Fluent Emoji: https://github.com/microsoft/fluentui-emoji
- Svgmoji: https://github.com/svgmoji/svgmoji
- Remix Icon: https://remixicon.com/
- Fluent System Icons: https://github.com/microsoft/fluentui-system-icons

The reported eleven-key embedded Cerebral baseline is Fluent **System Icons**,
not Fluent Emoji. Its exact upstream version/license/provenance and frozen pack
inventory were subsequently frozen by the producer and independently admitted
by Cortex's focused source tests and installed importer; this is not a renderer
test. The earlier reference-only review was followed on 2026-10-08 by pinned
acquisition and optional data-module admission of the four candidate libraries.
Preserve original website URLs in provenance even
when a site redirects to its source repository. A reference link does not mean
the library is admitted, shipped, or approved for every use.

## Current capability (runtime version unchanged by data admission)

- `CortexRecordTypeV2` supports only `task` and `knowledge`. There is no asset,
  attachment, or blob record type. `cortex_search` returns compact previews;
  `cortex_read` selects exact namespace/type/ID/revision coordinates.
- The semantic V2 application still has no asset record type. The unified host
  now composes optional `CortexAssetResourcesV2` through standard resource
  list/template/read methods. Its root-pinned provider returns metadata previews,
  exact metadata, and bounded selected bytes without arbitrary file paths.
- The unified host still exposes eight semantic tools plus `pao`; no new icon
  tool or PAO operation was added. Resource delivery is installed; Cerebral's
  native pipe `op=resource` consumer is independently tested by its owner, not
  inferred from its older tools-only MCP client or Cortex producer checks.
- Local MCP clients can use the fixed PAO `stat`, `sha`, and `cp` operations on
  admitted actual paths. A copy exports an existing file; it does not prove
  licensing, admit a pack, resolve a logical icon key, or create an icon.
- Behavior-pack v2 is for bounded knowledge records, not artwork. It must not
  be overloaded to contain binary assets or every icon catalog entry.

Source anchors:
`src/PegasusCortex.Contracts/V2/CortexV2Contracts.cs`,
`src/PegasusCortex.Mcp/V2/CortexV2McpApplication.cs`,
`src/PegasusCortex.Mcp.Local/Runtime/CortexUnifiedMcpApplication.cs`, and
`src/PegasusCortex.Persistence.File/V2/CortexFileBehaviorPackV2.cs`.

An optional compact pack pointer can already be represented as a knowledge
record. Search and exact-read such a pointer using the current schemas:

```json
{"namespaces":["knowledge.shared"],"text":"icon pack <subject>","limit":5,"maxUtf8Bytes":2048,"format":"compact"}
```

```json
{"namespace":"knowledge.shared","recordType":"knowledge","recordId":"<actual-returned-record-id>","revision":1,"maxUtf8Bytes":4096}
```

These are arguments for `cortex_search` and `cortex_read`, respectively. Use the
actual returned revision, not the illustrative `1`. They return record content,
not image bytes. No pointer records or namespaces were created here. Optional
knowledge pointers are discoverability aids; the independent catalog and files
remain authoritative for assets. Do not mirror the full catalog into knowledge.

## Smallest independent catalog contract

Use an INI/TXT catalog plus asset files initially, matching the producer's
frozen format rather than forcing JSON. Keep human-readable descriptive section
keys, an explicit schema version, UTF-8 text, and literal values; the parser must
reject duplicate/ambiguous keys rather than execute interpolation or silently
rewrite identifiers/paths. Final syntax and filenames belong to the frozen
producer contract. JSON MCP wire messages and existing mandatory plugin
manifests remain contract exceptions, not a reason to change their format.
A second SQLite database is not justified by the current small pack; it can be
an internal later implementation choice without changing logical references.
No binaries, base64 image bodies, or full catalog enter startup briefs or
automatic model context.

Keep these fields, with optional values omitted rather than empty:

| Level | Required reference metadata |
| --- | --- |
| Pack | schema version, explicit namespace, descriptive stable pack ID, pack version, exact manifest SHA-256, description, license/provenance references |
| Icon | descriptive stable key, title/short description, relative target, MIME/media type, byte length, asset SHA-256, original source URL and license/provenance reference |
| Optional link | actual exact Cortex namespace/record ID/revision or admitted file ID, only when such an object really exists |

Pack identity is namespace + pack ID + version + manifest digest. Icon identity
adds its stable key. The manifest digest is computed over the exact distributed
manifest bytes; do not include a self-referential manifest digest inside those
same bytes. Source URLs are provenance only: never fetch them implicitly.
The actual relative target must refer to the distributed asset, not its
upstream source or a guessed machine path. Do not fabricate a `cortex:` URI,
file ID, record ID, or dedicated tool that the runtime does not advertise.

The namespace scopes discovery and is echoed in results; it is not itself an
authorization grant. Public/shared pack metadata contains no private paths or
user history. Resolve physical roots locally from configured product/data
locations, not from this computer's development directory or from a caller's
arbitrary absolute path.

## Baseline, growth, and old references

1. Ship a read-only baseline catalog/assets with the product. Keep owner-added
   packs and selection metadata in the configured user data area, separate
   from shipped files and the task/knowledge databases.
2. Validate a candidate pack before selecting it. Reject conflicting content
   under an already admitted exact identity; never silently replace bytes.
3. Selecting a newer default affects new unpinned selections only. An exact
   old version/digest/key keeps resolving to its original admitted asset.
4. Preserve user additions, provenance, and explicit choices. Do not merge
   overlapping keys by filename/display label or silently overwrite them.
5. Retain old referenced assets independently of obsolete application binaries.
   Any later collection/removal policy must respect pinned references and
   explicit user authorization; this checkpoint adds no automatic pruning.
6. For an unavailable exact reference, return a bounded missing/changed/invalid
   state, not a different icon disguised as the requested original. The UI may
   display a clearly identified baseline fallback separately.

An export selects one exact icon or pack, validates its target/content, and
copies it with its applicable license/notice/provenance sidecar. A file copy
alone is insufficient where the license requires attribution or notices.
Agents may reuse assets in other projects subject to actual usage rights and
the destination workspace's authority. No claim of unrestricted licensing is
made until the producer's material is reviewed.

## Optional user context-icon preferences

Build stable artwork identity first. Add a small independently versioned user
preference layer that maps a semantic chat meaning to an exact icon reference,
not to copied image bytes or a display name. Example meanings are `chat.go`,
`chat.stop`, `chat.good`, `chat.no-good`, and `chat.good-to-go`. These are
appearance roles, not commands or execution authorization.

The user can browse/search cats, view all matches through bounded pages, and
select one exact icon for `chat.go`. The cat keeps its original descriptive
key/provenance and may still serve other roles. Replacing the appearance of
Go does not rename the cat, modify the shared baseline, or change Go's behavior.
Do not infer a selection from the first/most similar search result.

Store the small role-to-reference map with the configured current user's asset
settings, not in shipped packs, task histories, or the shared catalog. A
versioned INI/TXT settings file is sufficient initially; another SQLite store
is not required. Supported clients can share the user's defaults while explicitly
scoped application/chat overrides remain optional. Clients that do not support
the preference layer continue using the baseline. Never export user choices
in the general starter pack without an explicit personal-settings export.

Selection order is an explicit applicable chat/application override, then the
user's default, then the shipped baseline. Keep the selected version/digest/key
pinned until the user deliberately changes it; a new pack default must not
silently reinterpret a user choice. If an asset is missing or invalid, report
the unavailable selection and display a clearly identified baseline fallback;
do not delete the saved preference merely because a pack is temporarily absent.
An unavailable personal settings layer must not block core Cortex operations.

Keep the original phrase/meaning available as visible or accessible text and
tooltips, especially for Stop, warnings, and negative outcomes. Common-phrase
aliases may decorate chat presentation, but must not rewrite stored messages,
command/script bodies, root codes, grants, approval decisions, or workflow
state. Displaying a Go icon is never an instruction to execute work.

## Cortex implementation boundary after the pack freezes

### Delivery and consumer cache

Cortex is the source catalog and initial/future on-demand asset provider.
Cerebral owns its local consumer cache and rendering; it must not need a full
copy of every pack before it can work. Discovery selects compact descriptors,
then an explicit request obtains the selected asset bytes through an advertised
scoped delivery mechanism, not an inaccessible server-local path. Ordinary
client file roots/grants are not broadened by reference discoverability.

Admit a cache entry against exact namespace/pack/version/manifest-digest/key,
asset length/digest/type, and applicable provenance/notices. Reuse the admitted
cached copy on ordinary display without repeated Cortex calls or per-render
file hashing. Ask Cortex for newly selected icons, cache misses, or deliberate
pack updates. Selection/update checks must not silently redirect a pinned
reference. Cache eviction does not erase a saved preference: it makes the next
use an explicit retrieval. Cached assets can support offline display under the
client's applicable access policy; unavailable uncached selections use a
clearly identified baseline fallback without deleting the choice.

Image transport/cache lives outside automatic model context. Models receive
bounded descriptors/references; image bytes enter an LLM only when an explicit
image task requires them. A returned delivery handle/link must correspond to a
real advertised retrieval contract and authorized selected asset. Original
source website URLs remain provenance, not automatically usable download
handles. The installed provider now supplies selected-byte delivery. Consumer
UI/cache/model integration remains separate work, not implied by that provider.

Cerebral's user controls must distinguish Clear icon cache (delete only owned
downloaded cache entries, keep preferences), Refresh selected icons (explicit
refetch without changing pinned selections), and Reset icon preferences
(explicitly restore baseline role choices). Reset must not delete original
packs or other user data. Purging a cache is not a grant, server-side asset
deletion, or instruction to fetch an upstream website. Cerebral owns these
controls and their isolated cleanup implementation.

Frozen producer input received: `pegasus.asset-pack/1`, pack
`pegasus.chat-icons` version `1.0.0`, eleven keys, catalog SHA-256
`04aa6881acc2f65edab2c046409cb87f71e932081fb0ac21bea9f2f526aa0011`.
Read-only hashing confirmed the catalog coordinate. The catalog's `namespace`
value `pegasus.chat-icons.` is an artwork-key prefix, not a Cortex operational
namespace or access grant. The pack adapter must add explicit Cortex discovery
namespace/client scope and measured asset byte lengths/MIME metadata without
rewriting the frozen source catalog. Whole-pack content admission and bounded
delivery now pass source acceptance; consumer cache/purge, rendering and live
integration remain separate stages.

The implemented file-catalog adapter provides bounded read-only discovery/exact
reference mapping at the existing MCP composition boundary. Keep task/knowledge
schemas and SQL migrations unchanged; do not grow the PAO operation catalog
merely to hide an asset provider. Choose the advertised MCP entry point after
matching the producer's contract and the existing tool-count/resource bounds;
the implemented `pegasus-asset://v2/` contract is defined in `ASSETS_V2.md`.

Discovery must be explicit, namespace-scoped, bounded, and pageable; resolution
returns only selected compact descriptors. Export is an explicit effect, never
an implicit result of search. Transport stays on the existing local/STDIO MCP
route; the separate shared-server work can host the same adapter later. LAN
transport is separately authorized work.

Admission validates unique stable keys, supported schema/media types, original
licenses/notices, exact lengths/digests, and portable relative targets. Reuse
current canonical path rules: no traversal, rooted relatives, alternate streams,
reserved Windows names, reparse components, or paths outside the admitted root.
Artwork is passive data. The Cerebral icon producer owns the concrete passive
SVG allowlist/admission and renderer contract and will supply its evidence.
Cortex resolves verified metadata/references; it does not implement a second
renderer, execute SVG/browser content, or fetch external resources. Metadata
admission must not be presented as a Cortex rendering/sanitization guarantee.

## Acceptance gates

- Baseline available with no optional/user pack; growth does not alter startup
  payload size or load all image bytes into the model/host.
- Exact namespace/pack/version/digest/key resolution and bounded search.
- Wrong namespace, unknown version/key, tampering, length mismatch, unsafe path,
  reparse escape, and unsupported format fail explicitly.
- Pack update preserves user additions and old pinned references; fallback is
  visible, never an identity substitution.
- User chooses a cat for Go: exact chosen cat renders, label remains Go, shared
  defaults are unchanged, and no action is executed. Missing-pack fallback,
  personal-settings absence, scoped overrides, and reset-to-baseline preserve
  stored user choices and other users' settings.
- Individual and pack exports retain actual applicable notices/provenance.
- A non-Cerebral MCP client obtains the same descriptors through the advertised
  route, without a Cerebral SDK dependency, URL download, or new listener.

Current evidence includes warning-strict source builds, eight focused checks,
the real frozen 11-icon corpus, generated disk fixtures, and raw JSON-RPC through
the real server class with fixture application/execution adapters. The OS denied
symbolic-link creation, so the actual dangling-reparse probe was skipped.
These are not installed, rendering, clipboard, network or Cerebral end-to-end
acceptance results. The source pack remains data-only and contains no personal
records, generation recipes, model output or signing secrets.
