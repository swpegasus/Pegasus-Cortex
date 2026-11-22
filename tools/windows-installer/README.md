# Shared Pegasus Windows installer tools

Use this kit for another Pegasus product instead of copying Cortex's installer.
It is shipped under `<Cortex install root>\tools\windows-installer`; its sibling
`tools\signing` contains the shared certificate/signing tools. Source locations
and the developer's `C:\CodexProjects` directory are not runtime requirements.
Cortex consumes this same generator through `deploy/x64/windows-installer.json`.

## Product definition and setup adapter

Start with `templates/product.json` and its short adaptation guide (the minimal
`product.example.json` remains compatible). This is the default shared workflow
for new Pegasus Windows installers, not a Cortex-only visual variant.
The bounded `pegasus.windows-installer/1`
definition supplies identity, publisher, default product folder, version and
four package-relative entrypoints. Unknown fields, injected NSIS syntax,
traversal and mutable-state entrypoints are rejected. There are no arbitrary
command strings or shell hooks in the product definition.

The shared generator provides an elevated NSIS **Modern UI 2** wizard with the
traditional InstallShield operating model. A new installation shows only the
relevant program, feature, data, progress, and completion pages. Rerunning setup
for an installed product opens a concise maintenance choice: **Modify optional
features**, **Repair or update program files**, or **Uninstall the program (keep
all databases)**. Internal stage details are available on demand instead of
dominating the normal UI.
It uses NSIS's DPI-aware dialogs and product branding, not a second UI framework.
Windows automatically requests administrator elevation for machine setup.
Recognized upgrade/repair displays a cancel-by-default interruption warning
before core package admission or selected trust changes. The
coordinator reaches the wizard without extracting or executing an inner setup
probe. Core, trust, Codex registration, Codex guidance, and their removals are
separate sections. An unselected section has no command and performs no target
inspection. Silent invocation does not display the warning and constitutes an
unattended upgrade request.
Core installation, upgrade, and repair do not require unrelated Codex windows
to close. Cortex declares `keepRuntimeStopped=true`: its adapter requires the
owned service and product processes already stopped, and refuses loaded images
instead of killing them. Finish affected jobs/applications and use the graceful
Cortex Exit flow before replacement. Setup preserves the stopped state and
existing startup choices; another product must document its own adapter behavior.
An explicitly selected Codex registration or guidance install/removal requires
every `codex` and `chatgpt` process to be closed. The adapter checks this at the
selected feature's mutation boundary; it does not inspect installer ancestry.
An optional immutable `wizardIconPath` supplies a real `.ico` for both wizards;
`iconPath` remains the executable shown in Add/Remove Programs.
It also provides the custom fresh-install path,
registry-based upgrade/repair discovery, visible setup progress, explicit trust
component, Start Menu shortcut, signed uninstaller, Add/Remove Programs fields
and a reusable maintenance setup. Its program default is
`%ProgramFiles%\<publisher>\<product>`.
The generic wizard caches its signed coordinator as
`<InstallRoot>\installer\Setup.exe`; Add/Remove Programs Modify/Repair points to
that coordinator. This is different from Cortex's signed backend declared by
`setupPath=installer/PegasusCortex.Setup.exe`. In adjacent mode the coordinator
resolves its companion product first beside itself, then from the registered
`InstallSource`, then the existing installed root; the selected payload still
must pass admission. Keep the complete source layout available instead of
assuming the cached coordinator contains it. `Uninstall.exe` is the uninstall route. Normal
uninstall never offers database deletion and retains the registered data
location so a clean reinstall can reconnect to the existing store.
Build into the same generated output folder every time; do not create automatic
archive, quarantine, baseline or rollback copies. Version identity belongs in
the signed package and executable metadata.

Each product supplies a signed setup adapter implementing these fixed CLI forms:

- `install-package <package> --install-root <root>`: verify the pinned detached
  signature before admitting content; check product/role/version and all file
  hashes; enforce path, ACL and disk-space rules; apply the adapter's declared
  quiescence policy; replace verified binary files and preserve databases.
  Cortex requires already-stopped components and does not implicitly install,
  enable or start its optional privileged service. Protected state provisioning
  may set the declared ACLs without opening a semantic database engine. Persist
   `InstallLocation` and `ReleaseVersion` in the 64-bit
   `HKLM\Software\<publisher>\<product>` key.
- Optional `--data-root <root>` for a definition with the versioned
  `dataLocation` object described below. Persist the exact canonical
  `DataLocation` in the same machine key; preserve it during repair/upgrade.
- Products whose definition sets `codexGuidance=true` expose four independent
  routines: install/repair registration, install/repair guidance, remove
  registration, and remove guidance. Every routine defaults off. On an
  installed product `/CODEXREGISTER=0` and `/CODEXGUIDANCE=0` select exact
  removals; value `1` selects exact install/repair. Neither integration is a
  runtime prerequisite. Registration replaces only the exact legacy Cortex
  connection. Core installation cannot accept or forward Codex feature work and
  does not require unrelated Codex windows to close. A selected registration or
  guidance install/removal requires Codex to be closed before changing its files.
  Optional `--codex-home <directory>` selects the installing user's Codex home;
  `--codex-agents <AGENTS.md>` overrides only the guidance target. Path overrides
  never enable an action. Preserve unrelated settings and exact managed blocks.
  The compact top block routes agents to live MCP instructions and on-demand
  search/exact reads; setup does not access or modify Codex memories.
- `/TRUST=0|1` remains explicit and defaults off. Selecting it extracts only
  the bounded trust support files and runs that routine before core setup.
  Unattended `/S` setup also keeps trust off unless the caller supplies
  `/TRUST=1`.
- An optional versioned `finishReference` may identify one signed,
  package-relative `.txt` or `.md` overview and a bounded label. The wizard
  presents it as an initially unchecked completion action. It never runs in
  silent mode and is not an acceptance gate or executable hook.
- `unregister-product`: remove only owned service/PATH/startup/registration
   entries and preserve preexisting entries the product did not create. Keep
   the location association needed to recover a retained external database.
- `remove-product-files`: use a validated `uninstall.v1.dat`-style inventory of
  exact relative paths, lengths and digests. Preserve changed/unknown files and
  all mutable state. Keep the admitted setup/trust support available until the
  uninstaller's final cleanup section completes.
- `remove-state remove-all-state` remains an expert adapter operation and is
  never exposed by ordinary uninstall. The Windows uninstaller always retains
  databases and mutable state.
- Optional `prune-program-versions --install-root <root>`, enabled by
  `removeObsoletePrograms=true`: after successful product finalization, remove
  only obsolete inventoried immutable files. Preserve every active component
  version, mutable data, changed/unknown files and reparse/locked targets.
  This is forward-only cleanup, not a backup/rollback lane. It is intended for
  explicit maintenance; Cortex leaves it disabled during normal install and
  upgrade so historical scanning cannot block setup.
  Compact removed/missing obsolete entries out of the uninstall inventory after
  successful cleanup so the ownership file does not accumulate old generations.
- Optional `start-user-session --install-root <root> [--dashboard]`, supported
  by products with `userSessionStartup=true`: admit the exact installed resident
  host, start it with a matching non-elevated interactive user token, then
  verify bounded host/service readiness. It is an explicit user-context
  diagnostic/configuration operation, not an elevated installation step.
  This capability does not itself enable a Windows service or add logon startup.
  Cortex's normal setup keeps runtime stopped and preserves an existing sign-in
  startup selection; PAO/MCP clients attach only and do not demand-start the
  owner. A session-host
  product must register its admitted GUI/WinExe host directly; it must not use
  a console-capable CLI or MCP launcher as the resident Run-key target. The
  elevated wizard never calls this operation and never makes an otherwise
  successful installation depend on manufacturing a medium user token. No
  arbitrary executable, shell hook or argument is accepted.
  Products without this capability do not receive a host or service assumption.

For session-host products, Start Menu contains the declared stable product
shortcut. Configuration opens from that normal user-context shortcut after the
installer exits. Silent and interactive setup both avoid launching user UI.
Closing configuration must not terminate the resident execution host.

Report flushed, bounded stage lines to stdout, a compact final receipt, and a
nonzero exit on failure. The GUI streams those lines into the NSIS details pane
without parsing their content. Do not emit unbounded or bulk single-line JSON;
the shared NSIS 3.12 transport is intentionally limited to bounded progress and
receipt lines. Keep a persistent bounded setup log/failure receipt. Launch
failure maps to code `8`; actual backend exit codes are preserved. The wait has
no outer inactivity timeout, so adapters must bound their own waits. The setup
adapter is hosted without a separate blank child console.
Cortex's elevated startup and obsolete-file cleanup persist their stage and
failure details under `<DataRoot>\install\logs`, selected through the registered
data location. `<InstallRoot>\state` applies only to a retained legacy data-root
binding, not the new default. Cleanup failures
include stage, underlying exception type, native error when available, and
partial counters. Ordinary non-elevated startup requires no protected log write.
Development updates build forward; retry the corrected package in the same
root after partial failure. Never claim success or erase user state to recover.
Use transaction cleanup for security-sensitive registration/trust changes.
Database access, initialization, and schema evolution belong to the product
runtime, not to this GUI. Cortex setup does not open the semantic engine, seed
records, convert schemas or replace retained database contents. The explicit
copy-first relocation below may copy quiescent database file families while
retaining originals; that is distinct from database conversion. Product-specific service semantics stay in the adapter;
the shared GUI does not assume all products use Cortex's database or service.
The adapter must implement genuine admission; the JSON definition is not proof.

## Optional separate data location

The optional `dataLocation` definition uses schema
`pegasus.windows-installer.data-location/1`, `defaultBase=commonApplicationData`,
and validated relative `relativePath` and `legacyRelativePath` values. Cortex
declares `relativePath=Pegasus/Cortex` and `legacyRelativePath=state`; products
without this object retain the existing program-only location flow.

For a fresh Cortex install, the GUI offers a separate durable local data root,
defaulting to `%ProgramData%\Pegasus\Cortex`. Both fresh roots must be empty
dedicated folders (or absent). Recognized repair/upgrade preserves a custom
registered data location. Cortex's `relocateLegacyData=true` allows only the
exact old install-local `state` root to be copied to the default durable data
folder: require quiescence and an empty destination, retain the originals, and
select the new binding only after copy and known path fixups succeed. This does
not authorize moving arbitrary user directories or converting a database.
Reinstall preserves the selected data binding. An invalid, missing or unavailable
registered root is refused rather than remapped to an empty store.

Cortex places all mutable state under `DataRoot`: protected install metadata,
service state, and SID-private user database, backup, execution, configuration,
log, receipt, cache, and tray directories. Programs remain common machine
read/execute content under `InstallRoot`. Ordinary uninstall retains every
database, mutable state directory, and its path association. The expert
`remove-all-state` adapter requires an explicit confirmation and is confined to
the exact registered `DataRoot`; the Windows uninstaller never exposes it.

The optional `payloadLayout` field is either `embedded` (the backward-compatible
default) or `adjacent`. Adjacent mode produces a small signed coordinator beside
an uncompressed `product` directory. It performs no whole-product startup
decompression; every selected routine uses the same signed companion root.

The Cortex package records this source contract in `installer-contract.v7.json`.
This documentation update is not live installation or migration verification.

## PAO and release construction

`New-PegasusWindowsInstallerSource.ps1 -DefinitionPath <json> -ProductDirectory
<admitted payload> -OutputSource <fixed output.nsi>` generates a source file
using `${PAO_OUT}`. `pao nsis validate` and `pao nsis build` can validate/compile
that development source. Discover their exact arguments with `pao help`.
The fixed PAO adapter intentionally rejects compile-time signing/finalize hooks.

For a release, additionally supply `-OutputExecutable <fixed setup.exe>` and
the exact `-PublisherThumbprint`. Compile through the admitted product NSIS
compiler: the generator uses the existing fixed signer for `!uninstfinalize`
so the embedded uninstaller is signed before the outer installer. This is the
same release path Cortex uses. Sign the outer executable after compilation.
An unsigned development compiler result is not a releasable installer.

## Certificates and admission

The existing Pegasus Prod Alpha identity is an RSA-3072/SHA-256 local root and
code-signing leaf. A public development distribution can carry its public
verification certificates, while Windows/client trust and reputation remain
separate: a local development root is not a publicly trusted commercial publisher.
No signing private key belongs in a source checkout or distribution.

1. Use `..\signing\New-PegasusProdAlphaSigningIdentity.ps1` only when creating
   an identity is explicitly authorized. It creates non-exportable keys in
   `CurrentUser\My`. Prefer selecting the existing identity with both exact
   `-ExistingRootThumbprint` and `-ExistingPublisherThumbprint`; never select
   solely by a friendly name. Its root and publisher subjects are fixed; it has
   no subject customization arguments. Optional `PublicOutputDirectory` is
   create-only and exports public certificates only. No machine trust
   installation occurs here or is required just to sign.
2. Publish the product into its reusable package folder. Export only public
   trust with `Export-PegasusProdAlphaTrustBundle.ps1`. Never copy a PFX, private
   key, PIN, password, token or signing secret into a package or log.
3. Run `Sign-PegasusArtifactTree.ps1 -IncludePowerShell` against the package,
   selecting the exact publisher thumbprint and key provider. Sign every
   shipped EXE/DLL and executable PowerShell file before hashing it.
4. `New-PegasusSignedManifest.ps1` creates and signs the product-neutral
   `pegasus.windows-package/1` manifest. Supply package root, product ID,
   release version, role (`product` or `product-installer`), root/publisher
   thumbprints and provider. It binds signed bytes, exact product/role/version,
   certificate identity and public-trust digests using the existing CMS signer.
5. `Test-PegasusSignedPackage.ps1 -ProductId <id> -Role <role> -ReleaseVersion
   <version>` validates that generic package. Cortex retains its own established
   package schema and additional role/catalog checks. An adapter must pin its
   expected product, root and publisher independently of the incoming package.
6. The shared explicit trust operation installs the root and publisher only
   after certificate-window, chain, key-usage/EKU and identity checks. Use
   `Invoke-PegasusProdAlphaTrust.ps1 -Action Install -PackageRoot <package>
   -ComponentId <pegasus.product> -PlanOnly` for a read-only plan; actual trust
   changes require explicit elevation and consent. Machine-wide ownership is
   reference-counted in `HKLM\Software\Pegasus\Trust\ProdAlpha\<root thumbprint>`
   across product IDs. Preexisting trust is preserved. Uninstall cannot remove
   shared trust while another product remains registered as a consumer.

No network timestamp is used in Prod Alpha. Inspect certificate validity for
each release; a previously successful build does not establish current validity.
Never regenerate a manifest after signing without repeating signature creation
and verification. Distribute public trust only.

## Acceptance for another product

Verify fresh install, same-version repair, upgrade, downgrade handling, custom
root, state preservation, restart failure, visible errors, uninstall inventory,
Add/Remove Programs and reinstallation. Test certificate rejection, tamper,
wrong product/role/version, missing/extra files and shared-trust dependencies.
Use isolated fixtures for destructive paths; report real elevated installation
separately. Cortex's passing install tests do not certify another product's
adapter. No other product has been installed by adding this shared kit.
