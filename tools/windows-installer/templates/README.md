# Default Pegasus Windows installer template

Copy `product.json` into your product's deployment sources and substitute its
identity, versions and admitted package-relative paths. Keep **Pegasus first**
in visible product, window and shortcut names. Do not copy the generated NSIS
implementation: invoke the shared `New-PegasusWindowsInstallerSource.ps1` so
all products inherit the same Modern UI 2 workflow and fixes.

Default pages use a larger, readable, traditional InstallShield-style operating
model and remain streamlined around the action. Fresh install shows Welcome,
an optional license/notices page, Recommended or Custom setup type, relevant
components, program/data locations, a **Ready to proceed** summary, progress with expandable
details, and Finish. An installed product gets one maintenance choice—Modify
optional features, Repair or update program files, or Uninstall while keeping
all databases—followed only by the pages that choice needs.
The signed setup is cached under the install root and registered for Add/Remove
Programs Modify/Repair. The coordinator reaches its wizard without extracting
or executing a hidden inner setup probe. Ordinary repair/update replaces
programs only. Each optional feature has its own explicit section and routine;
an unselected feature performs no inspection, extraction, or mutation.

## Select only capabilities the product implements

- `dataLocation`: remove this object if the adapter does not support a separate
  durable data location. Otherwise implement the existing fixed `--data-root`
  contract and preserve its registry binding and all existing databases.
  The optional `relocateLegacyData` boolean defaults to false. An adapter that
  implements copy-first relocation may opt in: only the exact old
  `InstallLocation/legacyRelativePath` is directed to the default data folder.
  Custom registered roots stay unchanged. The adapter must require quiescence,
  retain originals, refuse an occupied destination, and select the new location
  only after the copy and known path fixups complete. This is a file copy, not
  database conversion; Cortex enables this option for its old `state` folder.
- `wizardIconPath`: optionally add `branding/PegasusExample.ico` and ship that
  actual `.ico` in the immutable signed payload. Omit to use NSIS's standard
  icon. `iconPath` remains the executable used by Add/Remove Programs.
- `noticePath`: identify one package-relative UTF-8 `.txt` installation notice.
  It is embedded as a read-only wizard page before setup choices. Keep complete
  third-party license files in the signed product's `licenses` folder.
- `payloadLayout`: omit it for the traditional single-file embedded package, or
  set `adjacent` to ship a small setup coordinator beside an uncompressed
  `product` directory. The directory is one signed unit and must remain beside
  setup; do not copy only the EXE.
- `codexGuidance`: leave false unless the adapter implements both independent,
  explicitly selected Codex options. Neither integration is required to run.
- `userSessionStartup`: enable only for a product with a resident user host and
  the fixed startup adapter described below. This is not a generic launch hook.
- `keepRuntimeStopped`: optionally set true when the backend requires an already
  stopped product and keeps it stopped after install/repair. Finish/progress
  then describe explicit user startup and preservation of existing sign-in
  choices. This declares backend behavior; it adds no start/stop action or
  login setting. Missing/false preserves existing generated installer behavior.
- `removeObsoletePrograms`: enable when the adapter implements fixed
  `prune-program-versions --install-root <root>`. It runs after durable product finalization,
  removes only inventoried obsolete immutable files, preserves active versions,
  refuses reparse/locked targets, and never visits database/state directories.
  No program rollback copies are created; unknown or changed files are reported.
- `finishReference`: optionally identify one signed package-relative `.txt` or
  `.md` overview with a short label. The shared wizard exposes it as an
  initially unchecked finish-page action. It is a human reference, not an
  acceptance gate, execution hook, or substitute for product-owned guidance.

## Fixed resident-session adapter

When `userSessionStartup=true`, implement:

`<setup.exe> start-user-session --install-root <canonical-root> [--dashboard]`

For the existing default (`keepRuntimeStopped` missing/false), the ordinary
install adapter must already have registered/started the required
automatic service and registered user-logon startup. The shared elevated wizard
does not invoke `start-user-session`; installation success does not depend on a
signed-in user token or UI launch. Resolve only the admitted installed host;
verify owner/session, immutable paths and publisher when the operation is
explicitly invoked from the user context.
When `keepRuntimeStopped=true`, the backend instead preserves existing sign-in
startup settings and requires explicit ordinary-user startup after maintenance.
An optional administrator service is not an ordinary-host prerequisite. The
wizard still does not invoke `start-user-session` or change startup policy.
Launch the user host with a matching **non-elevated interactive user token**,
not the elevated installer's token. Return success only after bounded host and
any service health checks the product actually requires. An optional service's
absence is not an ordinary-host readiness failure. Reject unavailable or mismatched user context
with a bounded nonzero result; do not report successful readiness or silently
switch owners. No arbitrary path, shell command, argument or token is accepted.

For the existing default, the adapter first ensures background execution, then opens only its fixed
configuration view in the same non-elevated session. Configuration is not the
execution lifetime. The host entrypoint supports `--dashboard` for user-invoked
Start Menu shortcuts, while PAO/MCP may demand-start the host without UI.
For the keep-stopped contract, only explicit product launch starts the host;
PAO/MCP clients attach to an already-running host and do not wake it.

## Build and acceptance

From the installed kit (paths may differ on another machine):

```powershell
& '<kit>\New-PegasusWindowsInstallerSource.ps1' `
  -DefinitionPath '<source>\product.json' `
  -ProductDirectory '<admitted-payload>' `
  -OutputSource '<fixed-output>\Setup.nsi'
```

Use PAO's fixed NSIS validate/build operations for development. For signed
releases use the documented admitted compiler/finalize-signing lane in the
parent README. Sign executable bytes before canonical manifests. Packages
carry public certificates only; explicit trust installation precedes setup.

Test your own adapter: fresh custom roots, detected repair/upgrade, database
preservation, explicit trust and opt-in integration, bounded failures, automatic
service/logon registration, demand-started user host, no elevated finish launch, silent mode, exact
owned shortcuts/PATH/registry, uninstall with retained data and reinstall.
Test UI fixtures separately from real elevated installation. A compiled wizard
or another product's passing tests do not certify this adapter.
