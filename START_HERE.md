# Start Here

Recovery entry updated: 2026-11-21

Pegasus Cortex is a local operational repository and bounded Windows execution
product.
The active source is this directory. The copy on `N:` is recovery source and
must not be modified. Read this file and `AGENTS.md` before changing the
product.

Optional locally supplied Sysinternals tools, their shared external layout,
licensing boundary, and proposed PAO/Cerebral integration are described in
`docs/PAO_SYSINTERNALS_PLAN.md`. They are not Cortex installer payloads.

## Current recovery entry

Keep source, prepared packages and the installed runtime separate. They can
legitimately be at different revisions while work continues:

- `VERSION.json` and `Directory.Version.props` declare the source release.
  They must agree before the normal build. They do not prove which source was
  included in an already prepared package or which version is installed.
- The normal forward outputs are `artifacts/release/unified` and
  `artifacts/release/installer`. Read their actual signed package manifests,
  catalog and inventory. Do not search timestamped folders for a presumed latest
  build, copy individual DLLs into a running product, or treat source compilation
  as package acceptance. The installer coordinator requires its adjacent signed
  `product` directory.
- The installed roots come from the 64-bit `HKLM\Software\Pegasus\Cortex`
  `InstallLocation` and `DataLocation` values. Verify the actual installed catalog,
  role identity and runtime status when those facts matter. An older acceptance
  report or a source manifest cannot establish the current machine state.
- The normal installer requires owned components already stopped and preserves
  the runtime's stopped state. The ordinary signed-in Host owns user execution
  and semantic storage. MCP, PAO and profile clients attach to it; they do not
  start or repair it. `PegasusCortex.exe --exit` requests graceful owner shutdown
  through the existing admission, active-work and service-exit checks.
- The existing privileged execution service is optional. Core setup does not
  implicitly install, enable or start it. Its protected operations remain distinct
  from ordinary owner execution and explicit same-owner UAC helpers.
- Optional shared HTTPS MCP source is documented in
  [CORTEX_SHARED_MCP.md](docs/CORTEX_SHARED_MCP.md). It is disabled by default;
  loopback is the default network scope. A configured application or existing
  service owns the endpoint until deliberate restart. Machine CA trust alone
  does not grant a service access to a user-private TLS key. Source, fixture,
  real network and installed-client qualification are separate evidence levels.
- The current certificate and password-reference contracts are in
  [PAO_CERTIFICATES.md](docs/PAO_CERTIFICATES.md). Secret entry remains local to the
  ordinary owner; passwords never belong in MCP arguments, job rows or records.
- Current source also declares bounded
  [Windows inventory](docs/PAO_WINDOWS_INVENTORY.md),
  [scheduled tasks](docs/PAO_WINDOWS_TASKS.md),
  [program-registration inventory](docs/PAO_INSTALLED_PROGRAMS.md) and
  [SMB connection profiles](docs/PAO_CONNECTION_PROFILES.md). Check their actual
  qualification and packaged revision before expecting those operations in an
  installed runtime.
- Explicit cold payload storage and its owner-controlled opt-in are documented in
  [CORTEX_PAYLOAD_STORAGE.md](docs/CORTEX_PAYLOAD_STORAGE.md). Compilation and
  isolated fixtures never authorize upgrading an installed database.

Use the recovery steps below with your own exact task binding. Historical
acceptance paragraphs preserve provenance; their version numbers, demand-start
behavior and one-time restart instructions are not current machine-state proof
or permission to change an installation. Preserve unrelated state and obtain
the authorization required by `AGENTS.md` for installed or protected changes.

## Historical implementation and acceptance notes

Keep the evidence coordinates separate:

- The installed baseline recorded on October 8 was `2.2026.10.08.05.26.A`.
- The October 22 preparation coordinate was `2.2026.10.22.16.03.A`
  (`2.20261022.1603-a` NuGet; `2.2026.1022.1603` file version). Version metadata
  identifies the new release being staged for the normal owner build; it does
  not prove that its final signed package or installer is ready.
- The October 13 candidate and earlier focused build/fixture hashes are
  historical qualification evidence. They do not describe the new candidate's
  compiled bytes or prove inclusion of later source changes.
- The source-workspace report
  `artifacts/validation/cortex-guidance-package-current.json` was the report for
  that guidance-package phase. Its release and outputs must match the candidate
  it describes; it is not a substitute for current package verification.
  The report is a source-workspace record, not an assumed shipped file.

See `docs/CORTEX_CEREBRAL_GUIDANCE_CURRENT.md` for the route matrix, exact-read
rules, qualified source payload hashes, delivery gates, and evidence limits.
Preparing the candidate does not update installed binaries, global Codex
guidance, saved registration, or a consumer. Later runtime adoption and the
matching Modify guidance action remain separately coordinated; the guidance
action requires Codex to be closed. Do not use the unadvertised legacy helper
to bypass those gates.

The following paragraph records the October 8 installed acceptance. The
user-authorized core-only update completed on October 8 with
installer exit 0. The established program/data roots and both current-user V1/V2
database file identities were preserved; one online SQLite backup was created.
The service is automatic/running and fresh MCP demand-started one owner-session
resident host. Exact task recovery/read, deferred accepted-job completion and
RID replay, bounded scheduler health, 25 status/health pairs, selected original
and licensing reads from all four visual libraries, zero stderr and adapter EOF
lifetime passed. After Codex restarted, the native Cortex and PAO wrappers
reconnected: status, exact-task startup, shared search and full typed reads passed
against the same resident host. The earlier Transport closed result followed normal
installer interruption and is no longer the current connection state.
Cerebral checkpointed and quiesced its owned Source generation before update.
No trust, Codex guidance/registration selection, credential or bulk asset-import
operation ran. See `artifacts/validation/v2-installed-readpath-current.json`,
`v2-installed-update-current.json` and `v2-installation-current.json` for actual
evidence. Sustained load and native SVG rendering remain separate acceptance.
Canonical signed outputs remain `artifacts/release/unified` and
`artifacts/release/installer`; no new rollback/version tree was created.
Installation, source builds and signed package verification remain separate
evidence.

The installed update adds deferred ordinary PAO job admission (`wait=false`), bounded
ordered-conflict scheduling, and streamed legacy queue recovery. Unknown external
process effects remain globally serialized; reads/health bypass the mutation queue.
Four optional public visual-library ZIPs and a portable licensing/pin index attach
beside the product, not to core setup. The installed single-module import command
shares one bounded validator with the development helper. See
`docs/PAO_BOUNDED_SCHEDULING.md` and `docs/VISUAL_LIBRARIES.md` for contracts and
source, package, installed and consumer evidence boundaries.

The installed checkpoint wires one resident semantic composition into the
existing signed-in host, with registered API-key + OS-identity admission,
namespace-explicit native Cerebral calls and shared MCP client sessions. API
keys authenticate connections, not namespace permissions; shared/project data
is a local library and personal data follows loaded-profile availability.
Fresh installed MCP and cold-start/EOF lifetime acceptance pass. The host uses
the actual Explorer desktop automation object for independent demand-start;
closing an adapter does not stop the host. A distinct Cerebral Source client
is enrolled; Cerebral reported 13 real Source Worker checks against the installed
native provider, including idempotent write/retry, search/exact Unicode read and
Worker restart/reconnect. Its first inspect used a legacy relay-default test
setting; explicit direct-native selection corrected it without a provider or
security change. Protected metadata was not forwarded after a safety-review
block; Cerebral used independent local discovery. Its installed consumer/legacy credential
are unchanged. See `docs/NATIVE_RESIDENT_V2.md` for enrollment, framing and boundaries.
The test executable also provides an isolated native-only source provider with
a scratch DB/key and unique pipe for Cerebral interoperability; it never uses
installed bindings or state. See `docs/NATIVE_INTEROP_HARNESS.md` for build,
normal-session launch, protected descriptor/key, timeout and clean stop.
The source-only interoperability checkpoint passed an owner native probe and
five resident regressions; Cerebral reported twelve production-client/relay
checks against the same scratch provider. Supported shutdown completed with
zero active sessions and process exit. See
`artifacts/validation/native-interop-current.json`; this does not establish
installed-provider, model, PAO-execution or sustained-load acceptance.

The installed build keeps terminal execution history on disk and releases completed
job/waiter references. Startup hydrates recoverable work only; history summaries
are bounded SQL projections and exact old receipts remain available on demand.
Health/monitor snapshots do not create new durable history, and service health
uses a scalar waiting count. Cancellation cleanup and queued-start arbitration
passed alongside the memory checks (9/9 focused Release checks). Signed product,
installer/template, deployment-plan and signing/tamper acceptance passed. The
installed host passed 100 status plus 100 health calls, clean EOF and independent
lifetime checks. Working set fell from about 3,141 MiB before update to 108 MiB
in the follow-up sample; private bytes fell from about 3,567 MiB to 44 MiB.
This is bounded runtime evidence, not sustained-load proof. Both semantic/legacy
database file identities and the registered data root were preserved. The service
is automatic/running. After Codex restarted, its native Cortex and PAO wrappers
reconnected successfully to the same resident host. A legacy service-process
fixture remains separate unfinished work.
No forced-GC timer, working-set
trimming, memory cap, or history deletion is shipped. See
`artifacts/validation/resident-installed-acceptance-current.json` for current
installed evidence and limits; `execution-memory-current.json` retains the prior
source/package checkpoint and historical baseline.

Cortex 2 adds optional content-addressed passive file assets, searchable icon
metadata, opaque JSON parameters and bounded selected-byte MCP resources. The
source publish includes the frozen 11-icon pack with its two licensing files
and catalog; explicit import does not modify semantic SQLite schemas. Eight
focused checks passed, with the actual dangling-link probe skipped because OS
link creation was unavailable. The installed frozen pack is now explicitly
imported into `knowledge.shared`: 11 icons and three catalog/licensing references.
Live keyword search, pinned metadata, selected original-byte digest and
identical-replay coordinates passed; see
`artifacts/validation/assets-v2-installed-current.json`.
Four additional optional public visual modules have now been acquired, packaged
and explicitly admitted: 34,271 original SVGs from OpenMoji, Fluent Emoji,
Svgmoji and Apache-licensed Remix Icon 4.7.0, with their exact per-family licenses
and notices. Compact descriptors and selected-byte reads use the existing asset
contract; no artwork/catalog is added to semantic SQLite or startup context.
See `docs/VISUAL_LIBRARIES.md` for pins, licensing exclusions, package inventory
and actual validation boundaries. Cerebral's resource consumer, cache and native
rendering remain independently owned acceptance work. Core continuity never
depends on an asset pack. See `docs/ASSETS_V2.md` and
`docs/ASSET_V2_OPTIMIZATION.md`; the next candidate implements prompt
accepted-job receipts and bounded ordered-conflict scheduling for PAO contention.
That fix passed the source/package gates and the bounded installed checks above.

The default shared installer now uses NSIS Modern UI 2 and ships
`tools/windows-installer/templates/product.json` for other products. Cortex
opts into automatic service/user-logon startup and a fixed `start-user-session`
completion step. It verifies the admitted medium-integrity signed-in host and
bounded service/host readiness. The installer creates one Start Menu entry,
`Pegasus Cortex`; configuration and service notifications are available from
that application's notification-area menu.
Automatic user-logon startup targets the stable GUI host at
`bin\host\PegasusCortex.Host.exe` directly. The root `PegasusCortex.exe`
remains the console-capable CLI/MCP/interactive launcher and must not be used as
the resident Run-key target, because doing so creates an unnecessary terminal
for the lifetime of the host.
See `tools/windows-installer/README.md` for the adapter and security contract.
The installer requests UAC elevation and warns before interrupting an existing
installation. Codex must be completely closed; setup checks this before stopping
the Cortex service or replacing program files. Program files use stable root and
`bin` paths. After successful startup, cleanup removes only admitted obsolete
legacy versioned files. It retains all databases/state and reports
changed/unknown material.
The signed candidate and its package/deployment checks passed on September 14.
Its executable manifest requests `requireAdministrator`; double-clicking is
sufficient to request UAC. Source and isolated tests cover non-elevated host
startup, safe interruption and obsolete-file cleanup with inventory compaction.
Setup progress is streamed into the installer details pane with a compact final
receipt; upgrade diagnostics begin before lifecycle interruption so early
backend failures do not collapse into an unexplained wrapper code.
The current package was installed on October 5 with installer exit 0. Fresh
installed MCP checks passed for compact PAO success/failure, ordinary-user
`job_log`, and structured receipt compatibility. The execution service is
automatic/running and the signed-in host was restarted. The registered data
root and both current-user semantic database file identities were preserved;
no trust, Codex registration, guidance selection, or obsolete-file pruning was
performed by this upgrade. See
`artifacts/validation/pao-compact-receipt-current.json` for the acceptance
evidence and remaining boundaries. Existing Codex connections need a restart.

Development builds replace `artifacts\release\unified` and
`artifacts\release\installer` in place. Use only `artifacts\build\current`
for build scratch and clear it after each build. Do not automatically create
versioned output directories, rollback packages, quarantines, or new baselines.
Setup keeps verified forward replacements on failure and reports repair needed;
existing databases are preserved. Snapshots or baselines are created only when
explicitly requested.

- one machine application at
  `C:\Program Files\Pegasus\Cortex\PegasusCortex.exe` launches stable
  role-specific binaries beneath `bin`;
- all owned source, application, library, and test projects target `net10.0`
  and build with the SDK baseline selected by `global.json` (`10.0.400`, with
  servicing-patch roll-forward); the Windows
  product publish is self-contained;
- one general local MCP surface exposes eight semantic tools plus the unified
  `pao` execution tool; the newer source STDIO adapter shares the resident
  owner/session host rather than opening a database per client. Native clients
  use a separate typed wire adapter into that same semantic composition;
- every stateful call supplies and receives explicit effective namespaces;
- the runtime gates only the optional `knowledge.user` layer on tray profile
  availability; shared/project/task operations keep working without it. The
  tray exposes Load/Unload personal preferences and bounded transition logs.
  A missing personal-profile pipe or signed-in host is a retryable availability
  state, never evidence that the database is absent and never permission to
  generate, copy, seed, or remap one. Existing database locations and files are
  preserved;
- the installer offers independent optional Codex MCP registration and
  managed prompt integration. Both are off by default in the GUI; neither is
  required for standalone Cortex or Cerebral. Selected registration replaces
  only the exact old Cortex plugin connection, avoiding duplicate servers.
  Selected guidance places the compact Cortex managed block at the top of
  `AGENTS.md`; it routes agents to live MCP guidance and on-demand shared-tenet
  search without accessing Codex memories;
- the completion page offers an unchecked **View included programming guidance**
  reference. It opens a concise signed overview of the packaged starter corpus;
  it does not display or inject the full corpus, add an acceptance gate, or
  change an existing database;
- a fresh `cortex-v2.db` is separate from every V1 or profile database;
- tasks and typed knowledge are authoritative within Cortex's local
  operational scope and become immediately searchable;
- current source packages a versioned 77-record starter corpus in the single
  `knowledge.shared` geometric space. The first `stdio` or sign-in
  `warmup` initialization atomically seeds an empty V2 store; an existing
  nonempty store is never implicitly reconciled or overwritten;
  The corpus composes four independently versioned behavior packs: general
  knowledge, generalized programming tenets, Cerebral guides, and Windows
  operational knowledge. The latter two can be distributed as detached pack
  folders without changing record identities or database semantics;
  Cerebral guides source pack revision 8 retains exact-root QA inspection,
  immutable generation approval and checkpointed Source recovery. It adds
  source-verified invoke postconditions, distinct Reload/reopen persistence
  tests, presentation routing and frozen-descriptor compatibility reminders.
  PluginAuthoring is 1.6.0, RecoveryCompaction 1.3.0, ToolRouting 1.5.0 and
  the catalog 1.7.0. The three changed canonical heads were explicitly revised
  in `knowledge.shared`: catalog revision 5, PluginAuthoring revision 5 and
  ToolRouting revision 4; RecoveryCompaction remains revision 3.
  Exact changed bodies and pack-8 provenance were read back.
  These were three guarded existing-head revisions, not an atomic whole-pack
  import; no project-namespace copies or unrelated guide revisions were created.
  Cerebral confirmed prior pack-7 native reads; pack-8 native confirmation is
  separate and pending. Unfinished bootstrap/continuation fixes are not asserted.
  Installed immutable pack revision 5 already contains the 77-record corpus;
  the older statement that it shipped 76 records was stale. Package updates
  never silently reconcile retained guide heads. These data-only changes add
  no scheduler, runtime operation, registration or grant; source, installed
  package generation and explicitly admitted record revisions are distinct.
- a bounded file spool and generated `HOT.json` projection are isolated by the
  stable Codex task identity under each project. They retain the useful
  resilience and visibility of the original file Cortex without making project
  files the central mutable database or sharing pending state between tasks;
- `offline migrate-legacy` preserves an exact historical project registration
  while adding one explicit task/root binding. It never imports or assigns a
  legacy root projection or unscoped frame body;
- Cortex now owns the imported AgentOps execution subsystem: one automatic
  privileged service, one signed-in user execution host with a supervised tray
  UI, transient isolation helpers, and the product-local `pao.exe`. Ordinary
  owner-context work goes directly to the authenticated user host and continues
  when the privileged service or tray UI is unavailable. Root/System work goes
  only to the service and never requires tray approval or tray availability.
  Local authenticated, versioned named pipes preserve the Windows identity and
  privilege boundaries; there is no network listener or firewall change;
- the execution service also exposes one private Cerebral application-session
  pipe. It binds the live SCM service PID, LocalService/session-zero identity,
  canonical non-reparse executable path, and a dynamically matching valid
  Authenticode Code Signing publisher on the live Cortex and Cerebral service
  executables. A separately rotated 256-bit credential is issued only across
  that authenticated pipe, stored by Cortex only as a salted verifier, and is
  never part of PAO/MCP/CLI requests or receipts. Persistent authenticated
  sessions may use the existing fixed PAO surface through the service FIFO;
  attended four-character root codes remain unchanged. See
  `docs/CEREBRAL_APPLICATION_AUTHORIZATION.md`;
- the Cortex dashboard includes a read-only Service notifications tab. It
  independently reports the user host, Windows service state, authenticated
  service-pipe health, and a bounded recent view of Cortex-specific Application
  and System warnings/errors without retaining or ingesting event messages;
- `pao.exe` exposes the same fixed operation catalog, validation, dispatcher,
  receipt, and error taxonomy through compact CLI, `--json`, and `pao.exe mcp`.
  Its client boundary tolerates surrounding whitespace and one matched quote or
  escaped-quote wrapper on typed scalar inputs, then validates the canonical
  value. Root codes are reduced to exactly four ASCII letters or digits and
  uppercased before strict service validation. Command arguments, file/script
  bodies, replacement text, search patterns, summaries, notes, titles, and
  other content-bearing fields are never rewritten by this normalization.
  Callers should still send canonical separated arguments rather than depend on
  recovery from malformed quoting.
  The signed catalog records the bundle release and each role's actual
  component version separately. Semantics, host, service, PAO, UI/process
  helpers, launcher, and setup may roll forward independently when their
  declared protocol and schema contracts remain compatible. Contract skew
  fails explicitly as `contract_mismatch`; version difference alone is not a
  failure. New catalogs also declare the selected product modules, their
  independent module versions, owned executable roles, exact provided/required
  contracts, and whether their state must be preserved. Legacy catalogs that
  predate the module inventory remain readable. The current local composition
  deliberately selects `storage.sqlite` as the required operational database
  module; the local semantic runtime is still concretely wired to SQLite, so it
  cannot honestly be removed or replaced yet. PostgreSQL and future database
  providers remain separate optional add-on candidates until a validated
  provider-loader boundary is implemented. `knowledge.shared` and
  `guidance.codex` are declared data-only modules, establishing their update
  boundary without claiming that this checkpoint physically packages them
  separately.
  The product installs only the one stable PAO client directory on machine
  `PATH`; PAO is not a second product, service, updater, state root, or Codex
  registration;
- the runtime includes User-only `app_start`/`app_status`/`app_stop` for registered
  applications with host-owned jobs, explicit generation restart, and compact
  status. Root-window exit can retain owned workers; unresolved termination
  cannot admit a successor. Ordinary `run` retains bounded output and cleanup.
  A PAO-owned process tree is marked while it occupies the single mutation
  worker. A descendant mutation fails before admission with
  `reentrant_mutation`; programs that synchronously call PAO mutations must be
  launched directly in the ordinary user session while their inner operations
  remain typed PAO calls. Nested reads remain available.
  See `docs/PAO_APPLICATION_LIFECYCLE.md`. Agent-mode responses use the bounded
  receipt-first contract in `docs/PAO_FAILURE_CONTRACT.md`;
- attended root-authorized `service_status`/`service_start`/`service_stop`/
  `service_restart` operations control only the exact `PegasusCerebral`
  Windows service through the System broker. Stop/restart require the observed
  PID and process-start identity; no arbitrary service, executable, command,
  or Cortex-service target is accepted. The private Cerebral application
  session cannot use this route;
- the admitted NSIS 3.12 compiler and AutoItX/UI support are immutable product
  tools. The agent-facing NSIS adapter remains fixed and bounded; it never
  accepts generic commands or searches `PATH`;
- system-installed PowerShell is resolved from its machine installation path.
  Routine servicing follows the installed executable without per-call binary
  hashing or manual hash re-registration. Custom pinned tools, admitted scripts,
  and signed Cortex package content retain their separate integrity policies;
- the signed NSIS installer verifies the detached product signature and exact
  inventory before installation, preserves the registered database location
  without opening, copying, seeding, or migrating databases, and keeps Prod
  Alpha trust as a separate explicit optional operation.

For Codex-driven local Windows developer operations, follow the inherited
PAO-first policy. Use direct Cortex MCP tools for semantic records and PAO for
supporting local operating-system and installed-CLI work. AgentOps is now an
  internal Cortex execution subsystem, while its service-owned root policy and
  privileged queue, per-user execution state, and semantic state retain separate
  ACL boundaries.

## Recovery order

Installed acceptance on 2026-09-13: the signed NSIS installer completed with
exit 0 in 23.1 seconds. Windows service restart, authenticated PAO queue status,
and raw installed MCP initialize/list/status/PAO read checks passed. Add/Remove
Programs and the signed uninstaller identify `1.2026.09.13.18.40.A`. Existing
program/data bindings and database security descriptors were preserved. Both
optional Codex integrations were selected on this machine; unrelated config
and unmanaged guidance were unchanged. Existing Codex processes need one
restart to load the new direct route. No Windows restart was required by this
acceptance. See `artifacts/validation/installed-codex-options-acceptance.json`.

1. Read `START_HERE.md` and `AGENTS.md`.
2. Inspect current source, tests, `VERSION.json`, and the actual working tree.
3. If `.cortex/project.json` exists, read its ordered namespace binding.
4. Treat a task scope's generated `HOT.json` only as a small,
   non-authoritative fallback. Obtain the scope through the installed offline
   command using the exact stable task ID; never derive or enumerate sibling
   scope paths. A root-level `.cortex/HOT.json` is legacy project-wide state
   and is never silently assigned to a task.
5. When the installed local MCP route is available, call `cortex_start` once
   with explicit ordered namespaces, the current intent, exact
   `CODEX_THREAD_ID` as `taskId`, a 4 KiB budget, compact format, and an
   optional stable phase ID. Create the exact task if `currentTask` is absent.
   Cache and reuse the verified context across ordinary
   turns. Refresh only after a material task, project, namespace order, server,
   guidance, connection, or recovery change, or when newer retained context can
   affect a decision. If initial access fails and context matters, make one
   bounded retry or recovery attempt; do not loop. Search results are previews;
   exact-read selected typed coordinates only, batching at most eight. Keep a
   bounded loaded coordinate/revision map in agent context and discard its
   marker whenever the exact body is lost. Cortex stores no read receipt or
   sticky hydration state.
6. Use `cortex_finish` once for a meaningful checkpoint, block, completion, or
   handoff, aggregating related evidence rather than recording every small
   event. Skip Cortex for immediate corrections, routine send/receive,
   formatting or mechanical work, repeated verification, and any case where
   retained context cannot affect the decision. Store bounded public
   conclusions and evidence, never raw transcripts, credentials, or private
   reasoning.

The pre-V2 `.cortex` tree is historical file-based Cortex/continuity evidence.
Do not bulk-convert, delete, or silently treat it as current runtime authority.
The permanent historical fallback material remains available through
`.cortex/README.md` and `.cortex/RECOVERY.md`.

## Data and deployment boundaries

- The 64-bit `HKLM\Software\Pegasus\Cortex` key binds canonical `REG_SZ`
  `InstallLocation` and `DataLocation` values. A fresh install defaults to
  `%ProgramFiles%\Pegasus\Cortex` for programs and independently to
  `%ProgramData%\Pegasus\Cortex` for all mutable product state. Upgrade/repair preserves
  custom registered locations. The normal installer's admitted relocation of
  the exact legacy `<InstallRoot>\state` default copies raw state to the separate
  data root with originals retained and registry selection last. It does not
  convert or seed databases; ordinary same-location updates preserve them.
- Stable application: `<InstallRoot>\PegasusCortex.exe`
- Immutable program roles: `<InstallRoot>\bin\semantics`, `bin\host`,
  `bin\service`, and `bin\ui`; the typed CLI is `<InstallRoot>\bin\pao.exe`.
- Product tools: `<InstallRoot>\tools`
- Service state: `<DataRoot>\service`
- V2 data: `<DataRoot>\users\<owner-SID>\cortex\data\cortex-v2.db`
- Preserved V1 data: `<DataRoot>\users\<owner-SID>\cortex\data\cortex.db`
- `DataRoot` owns every mutable directory: install metadata, service state,
  per-user config, databases, backups, logs, receipts, tray state, and execution
  state. Program binaries and tools remain common machine read/execute content
  under `InstallRoot`; service and user state retain separate ACL boundaries.
- An invalid, mismatched, missing, or unavailable registered data directory
  fails closed. Runtime does not switch to another directory or create an
  unrelated empty store; the fresh-install ProgramData default is not a
  runtime fallback.
- `PegasusCortex.exe backup` creates a timestamped, consolidated online SQLite
  snapshot and canonical manifest under
  `<DataRoot>\users\<owner-SID>\cortex\backups`. Do not copy a live
  `cortex-v2.db` and its WAL as an ad hoc transport substitute.
- `PegasusCortex.exe seed-defaults` explicitly reconciles the packaged starter
  corpus into an existing store. Exact active matches are unchanged. A higher
  `packRevision` may atomically revise an older head owned by the same pack;
  foreign, user-edited, ambiguous, or same-revision divergence rejects the
  whole import before any record changes.
- `PegasusCortex.exe import-behavior-pack --pack-root <absolute-directory>
  --namespace knowledge.shared` admits exactly one portable behavior-pack v2
  module into the registered current-user store. It exposes no database-path
  argument and returns a bounded JSON receipt with imported, revised,
  unchanged, and total counts.
- A one-time predecessor adoption adds `--adoption-manifest <absolute-file>`.
  The bounded manifest must bind the exact pack generation, prior source ID,
  provenance source, record IDs, and expected revisions. It preserves prior
  revisions and does not relax normal foreign-head conflict rejection.
- The default active SQLite profile is 4 GiB. Capacity telemetry reports the
  main database, WAL, page allocation, configured maximum, utilization, and
  warning state. The validated persistence option permits a deliberate larger
  active profile, but this release does not expand, shard, or archive the
  database automatically.
- Legacy databases are retained for an optional, separately reviewed
  translation. Startup never migrates or deletes them.
- Fresh installation accepts empty dedicated program/data roots; recognized
  repair/upgrade replaces admitted program files without adopting arbitrary
  populated folders. Normal setup creates directories and applies ACLs, but
  never opens, copies, replaces, seeds, or migrates database contents. The
  runtime owns database initialization, schema evolution, and explicit data
  operations.
- The InstallShield-style wizard is a small signed coordinator beside the
  uncompressed signed `product` directory. It opens without extracting or
  executing a hidden inner setup/state probe. Its traditional page order is
  Welcome, license/notices, Recommended or Custom setup type, relevant
  components, program/data locations, Ready summary, progress with expandable
  details, and Finish. An existing product receives Modify, Repair/Update, or
  Uninstall instead. Core, trust, Codex registration, Codex guidance, and
  their removals have separate routines. Every optional routine starts
  inactive and an unselected feature performs no inspection, extraction, or
  mutation. `/TRUST=1`, `/CODEXREGISTER=1`, and `/CODEXGUIDANCE=1` select exact
  routines; zero selects an exact Codex removal only for an installed product.
- The signed setup is retained as `<InstallRoot>\installer\Setup.exe`; rerunning
  it and Add/Remove Programs expose traditional Modify, Repair, and Uninstall
  maintenance. Uninstall retains every database, mutable state, and the data
  path binding; it exposes no database-deletion option. The expert
  `remove-all-state` adapter is not part of ordinary uninstall. Trust uninstall is independently
  reference-counted and removes only certificates Pegasus installed when no
  installed component still depends on them.

These location and preservation rules describe the current source contract
(`installer-contract.v7.json`), not a live installed acceptance result. No live
installation or database relocation was performed for this documentation update.

The active-store size is an operational profile, not a lifetime or collective
knowledge limit. Long-term cold history, multipart transport, and collective
retention/publication are separate future boundaries. This local release does
not implement them and creates no network route or collective service.

## Verification standard

Distinguish source inspection, compilation, unit tests, integration tests,
benchmarks, raw protocol tests, and live installed evidence. A generated
projection, fixture, or documentation claim is not runtime proof.

The V2 architecture and acceptance gates are recorded in
`docs/LOCAL_OPERATIONAL_V2.md`. Windows x64 build and installation details are
in `deploy/x64/README.md`.
