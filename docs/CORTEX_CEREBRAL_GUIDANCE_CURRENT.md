# Cortex–Cerebral guidance and support checkpoint

This is the historical October 22 qualification checkpoint. For the current
shared endpoint, first-time setup and client configuration, use
[CORTEX_SHARED_MCP.md](CORTEX_SHARED_MCP.md) and [START_HERE.md](../START_HERE.md).
The current public STDIO adapter route is retired; old qualification coordinates
below do not establish the installed release or authorize a fallback adapter.

Updated 2026-10-22 for candidate preparation `2.2026.10.22.16.03.A`, Codex task
`01a12d3b-7157-7352-ac1f-a5f549ceb103`, project `pegasus-cortex`, write namespace
`project.pegasus-cortex`, and ordered read namespaces `project.pegasus-cortex`,
`knowledge.shared`. Retained records are reference data and do not grant
operation or deployment authority. Preparing this document does not update an
installed runtime, global Codex guidance, registration, or consumer.

## Source, package, and installed coordinates

| Coordinate | Evidence and limit |
| --- | --- |
| Installed baseline | Last verified release `2.2026.10.08.05.26.A`. The earlier installed delivery check performed no installer, service control, database operation, or live connection. |
| Current preparation | Version metadata identifies release `2.2026.10.22.16.03.A`, NuGet `2.20261022.1603-a`, file version `2.2026.1022.1603`, staged for the normal owner build. These declarations do not prove final package content or signed readiness. |
| Final candidate evidence | The source-workspace record `artifacts/validation/cortex-guidance-package-current.json` must establish the exact release, outputs, inventory, signing and installer checks at their actual evidence levels. Until that report exists and confirms those results, final candidate readiness is unverified. No new binary or manifest hash is asserted here before that evidence exists. |
| Historical qualification | The October 13 package and earlier focused source/development outputs are historical evidence only. Their versions and hashes are not the current preparation's binary or manifest coordinates. |
| Consumer evidence | Cerebral Coordinator previously reported successful admitted Worker native/1 inspection. This is coordinator-reported runtime evidence, not a connection made by this audit or an explanation of earlier unclassified failures. |

The validation reports named here belong to the owning source workspace. They
are not assumed to accompany a shipped document. When reading this handoff from
a package without that workspace, use the package's verified signed inventory
and release/role metadata through normal admission, and obtain the source report
when its build or qualification details matter. This prose is not a readiness
receipt, installed acceptance, or permission to bypass a missing check.

## Qualified source payloads

The earlier focused source qualification established these document payloads.
A final candidate inventory must prove that it contains the same desired bytes:

| Payload | Qualified source coordinate |
| --- | --- |
| Managed Cortex continuity fragment | 1429 UTF-8 bytes; SHA-256 `edf2d42c9fc8bbca927ef057b61762afd323a7f83ce3cf42c053402ce2e5a629` |
| Managed PAO fragment | 1484 UTF-8 bytes; SHA-256 `0a3f4ebe51acf0e7b8428199fc054dc9f9e746b713e02e2032821cb617b86dcf` |
| Embedded legacy guide | Version `2.3.0`, observed `2026-10-22`, review after `2027-01-20`; 9111 UTF-8 bytes; SHA-256 `0e1d4d7eeda59b14ab68e9c34827cfdcaa8ecf5c97b381a46960cca2bdf1b10d` |

Each managed fragment is below its 4096-byte limit and retains its exact managed
boundaries. The embedded guide is below 16 KiB and couples its content to compiled
metadata/hash validation. Updating an AGENTS block cannot change that assembly
resource or create a V2 help resource.

## Route matrix

| Need | Supported route | Boundary |
| --- | --- | --- |
| Codex continuity | Installed `pegasus_cortex` semantic tools, explicit namespace/task binding | Missing tools in one session do not prove a failed resident or bad registration. |
| PAO local operations | Live fixed typed `pao`; installed `bin/pao.exe` where supported | Availability does not grant machine/service authority or a semantic record-read command. |
| Cerebral semantic calls | Authenticated native/1 pipe from the admitted signed-in Worker | Verify canonical server/client identities and actual hello. Do not create a private MCP runtime. |
| Current MCP client | Shared HTTPS endpoint configured by the owning product; default loopback `https://localhost:47831/mcp/cortex` | No per-chat STDIO adapter. Follow current certificate and selected-owner setup; a saved registration does not prove a live connection. |
| Offline continuity | Existing authorized task-scoped binding and supported `offline` commands | Frame reads/projections cannot replace exact central task or arbitrary knowledge reads. Do not derive sibling task scopes. |
| Missing semantic tools | Applicable local project recovery and bounded admitted task cache | No guessed CLI/pipe, private-body proxy, raw database read, registration repair, or claimed central write. |

For an exact selected task revision, `cortex_read` arguments are:

```json
{"namespace":"project.example","recordType":"task","recordId":"<exact-task-id>","revision":87}
```

Revision 87 illustrates the shape; use the selected record's real coordinates.
Omit `revision` for its current head. For knowledge use `recordType=knowledge`
and its exact record ID. Batch form is a separate shape:

```json
{"records":[{"namespace":"project.example","recordType":"task","recordId":"<exact-task-id>","revision":87},{"namespace":"knowledge.shared","recordType":"knowledge","recordId":"<selected-record-id>"}],"maxUtf8Bytes":65536}
```

Use 1–8 unique typed coordinates; do not mix batch and top-level single fields
or send `format`. The exact-read budget is 512..262144 UTF-8 bytes, default
262144. A body that does not fit fails rather than being shortened; raise the
budget within the bound or reduce the batch. Single output is
`structuredContent.record`; batch bodies are `structuredContent.items[n].record`
in request order. Verify echoed namespaces, IDs, revisions, and the full body.
`cortex_task get` and startup/search summaries do not hydrate full content.
The [capability reference](../integrations/codex/pegasus-cortex-client/skills/pegasus-cortex-client/references/capabilities.md#exact-reads-and-unavailable-tools)
is included at the same relative path in source and package for further client
rules and limits.

If semantic tools are absent, discover/retry once when useful and continue from
applicable local authority when allowed. `offline read --frame-id` reads an
offline frame and `offline refresh` creates a bounded projection; neither is a
central exact-read CLI. An already authorized coordinator with healthy tools may
relay a bounded exact typed read with coordinates, revision and reported
provenance. Keep that evidence distinct from a direct local read. Without an
admitted route, state the missing context and pending central checkpoint; do
not fabricate a finish or manually edit generated HOT.

## Corrected operational guidance

- A PAO command controller retains its global reservation while awaiting work.
  Run finite coordinating drivers in the ordinary signed-in user session
  outside that reservation, with actual tools still on PAO. `wait=false` does
  not release the reservation or detach the process lifetime.
- ACCEPTED is admission, including deferred literal replacement where
  advertised. JID controls the exact job; RID is a distinct idempotency identity.
  Unknown/lost replies require reconciliation before resubmission, even with
  the same RID. No public exact read-only RID lookup is currently exposed.
- `app_start` has independent lifetime and capacity: source bounds are 16
  nonterminal and 128 retained generations. Stops do not purge retained
  generations. V1 has no all-generation inventory or release/purge operation;
  host restart affects every owned application and needs separate authority.
- Native failures must retain a safe public classification when available.
  `client_auth_failed` aggregates several checks and does not prove a bad key.
  An identical Worker executable at a different canonical path does not inherit
  the admitted application's enrollment. Preserve guards and credentials.

Detailed contracts remain in [bounded scheduling](PAO_BOUNDED_SCHEDULING.md),
[application lifecycle](PAO_APPLICATION_LIFECYCLE.md),
[receipt presentation](PAO_FAILURE_CONTRACT.md), and
[native/1 communication](NATIVE_RESIDENT_V2.md).

## Normal package and guidance adoption

The normal owner preparation scripts in the source checkout are
`tools/New-PegasusCortexUnifiedProduct.ps1` followed by
`tools/New-PegasusCortexNsisInstaller.ps1`, using the admitted signing identity
and canonical `artifacts/release/unified` and `artifacts/release/installer`
outputs. Preparation and qualification must produce the final source-workspace
report before readiness is claimed. A matching version string or a successful
earlier fixture does not establish a newly signed payload.

Later adoption is separately coordinated. The new semantic DLL must be admitted
through the normal new-release package/runtime path to serve embedded guide
2.3.0. Preserve the registered program/data roots, databases, user records,
connections, security, and unrelated state. Source preparation does not itself
authorize installation or a host/service restart.

After that adoption, the installer's **Install or repair Cortex guidance in
Codex** feature, selected with `/CODEXGUIDANCE=1`, enters Modify mode and skips
core installation. Its separate-argument adapter form is:

```text
PegasusCortex.Setup.exe configure-codex-feature <verified-package-root>
  --install-root <registered-install-root>
  --feature guidance --action install
  --codex-home <installing-owner-Codex-home>
  --codex-agents <exact-AGENTS.md>
```

The selected package must contain the desired fragments and match the installed
active catalog release. Codex/chatgpt must be closed before guidance changes.
The operation checks the installing owner, safe exact target, write ACLs, no
active `AGENTS.override.md`, strict UTF-8/bounds/managed boundaries, and exact
preimage before atomic replacement. Registration remains Keep, unrelated
guidance/BOM/newline form is preserved, and the selection receipt is updated.
Omit registration switches: `/CODEXREGISTER=0` means removal, not preservation.
Leave trust unselected. Do not use a relabeled old package, loose source
substitution, or the unadvertised `install-codex-guidance` helper to bypass the
matching-release or Codex-close gates.

## Support and remaining work

The V2 semantic catalog has eight tools. PAO and asset resources, when present,
belong to the resident composition; V2 itself exposes no guidance/help resource.
The legacy `cortex://default-library/guide` is not a V2 fallback. Updating that
embedded guide changes neither the V2 catalog nor resource surface.

Existing support uses `cortex_status` and the documented
`PegasusCortex.exe metrics --days N` (1..90) report. Process retrieval counters,
durable utilization, capacity, and native consumer readiness are distinct.
Utilization reports instrumented calls only, not HOT reads, usefulness, token
cost, or authenticated agent identity. See [utilization](CORTEX_UTILIZATION.md).

The following remain implementation/coordination decisions, not features
delivered by this guidance preparation:

- A public bounded central task/knowledge exact-read CLI or equivalent admitted
  client, if product owners choose to support disconnected tool sessions.
- Action-specific semantic help or a deliberately versioned V2 support resource;
  do not add undeclared actions to the current eight-tool contract.
- An exact read-only RID lookup and application inventory/release semantics,
  with ownership, replay, retention, and failure behavior designed first.
- A first-party native SDK distribution and consumer adoption plan. The
  [native/2 multiplexing contract](NATIVE-MULTIPLEXING.md) does not update the
  frozen SDKs or installed native/1 consumer automatically. Verify the final
  candidate report separately from installed/consumer acceptance.
- Distinct queue/admission/start/finish timing evidence, if required. An
  overwritten update timestamp does not prove historical phase durations.
- Optional future Browser-to-PAO HTTP integration remains unsupported by the
  current fixed PAO catalog: no HTTP/page-request schema is exposed; `net_list`
  only inspects listeners. Cerebral's existing `browser.request` provider owns
  its bounded HTTP GET contract separately. The [fixed GET evaluation](PAO_HTTP_GET_EVALUATION.md)
  proposes one engine operation with explicit request/receipt, privacy and
  address boundaries; its proposed schema and HTTP test plan are not
  implemented. No registration, shell recipe or provider network probe is part
  of this guidance delivery. Cerebral Coordinator reported 48 Browser page HTTP
  checks; those are owner-reported, not Cortex-run acceptance.

## Historical qualification and continuity

Before this new release preparation, the source documentation pass used exact
schema cross-checks, strict UTF-8/managed boundaries, JSON examples, local links,
independent reviews, and byte-preimage-protected publication. Focused setup and
MCP builds passed with zero warnings/errors. Existing scratch guidance fixtures
passed via `--codex-guidance-fixture`; the in-memory
`ResourcesAndUriSafetyAsync` fixture passed 1/1 via `--guidance-only`. An initial
MCP build had failed with stale cached restore/runtime metadata (`NU1004`);
normal locked restore and the affected rebuild succeeded without lock-file
changes or weakened validation. These are earlier source qualification results,
not fresh results for the release metadata bump or final package.

Historical development hashes were semantic DLL
`050500c7241b68194443a5ba82183bf04c1f1235efe010980d4fef036f5907c2` and setup
adapter `4198237f5ee2f19d6861381dc654a8e5f8d4f1c4b5db0c919409ad8328cfd185`.
The historical October 13 package declared `2.2026.10.13.15.10.A`; its manifest
hash was `d30a942faae1059c2b06df9dafa6d782946043e87bb968eca509e9083f23a456`.
It and the installed October 8 payload held guide 2.2.0 and the older 586/935-byte
fragments at the earlier delivery check. None of those compiled/manifest hashes
identifies the new candidate.

Detailed historical receipts remain in the source-workspace records
`artifacts/validation/cortex-guidance-current.json` and
`artifacts/validation/cortex-guidance-delivery-current.json`. They need not be
present beside this shipped document. The new
`artifacts/validation/cortex-guidance-package-current.json` must state the actual
new build, package/signature and installer results, plus any skipped checks.
Installed runtime, native/model, provider, sustained-load and consumer acceptance
must not be inferred from static review, fixture passes, or package admission.

At qualification, the Codex session exposed no `pegasus_cortex` semantic tools;
saved registration was configuration evidence only. No central task write or
`cortex_finish` was claimed. Central checkpointing remains pending a healthy
authorized semantic route. Keep local handoff evidence separate from central
state and do not edit generated HOT to simulate completion.

## Legacy managed-block migration correction

Candidate 2.2026.10.22.16.03.A also corrects setup admission for the observed
managed-v2 continuity block. That legacy comment-delimited body uses the same
Pegasus Cortex continuity heading as the current stable block but has no
End of Pegasus Cortex continuity line. Earlier validation counted that legacy
heading as an incomplete current block before admitting legacy migration.
The earlier fixture used an Old Cortex heading and missed this preimage shape.

The corrected validator first admits exactly one complete legacy span, rejects
duplicate internal headings or any nested stable end boundary, and excludes
only that span when validating current boundaries. Malformed, duplicate, and
mixed legacy/current blocks remain rejected; fragment admission is unchanged.
The existing composer then replaces only its owned sections.

Focused scratch fixtures reproduced the prior rejection, then passed the
correction: captured managed blocks with synthetic human instructions in
LF/no-BOM and CRLF/BOM forms, migration, byte-idempotent replay, removal,
nine malformed/duplicate/mixed inputs through install and removal admission,
unchanged rejected targets, and rejection of legacy syntax as a new fragment.
These are fixture results, not a live Codex guidance update.

Both desired fragments differ from the installed files and active global
managed bodies even after ignoring wrapper/newline differences. A guidance
update therefore remains real pending work after normal matching-release core
adoption and the explicit Codex closure boundary; no reset/removal is needed.
The embedded guide and desired fragment content hashes above are unchanged.

The source installer README now distinguishes core interruption from selected
Codex-feature closure. The prior signed 2.2026.10.22.14.58.A product, installer,
and validation record were explicitly retained under
artifacts/release/before-guidance-migration for this correction. Their old
parser and README must not be mistaken for the new candidate. Normal core
Update still waits for coordinated consumer/job quiescence; installation and
host/service restart were not part of this source/fixture correction.
