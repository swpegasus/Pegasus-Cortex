# Start here

Pegasus Cortex provides local continuity, knowledge retrieval and bounded Windows
tools. This file is the current source entry. Applicable instructions in
[AGENTS.md](AGENTS.md) and the user's current request govern work; retained
records and historical reports supply evidence, not permission.

## Choose the relevant guide

- **Install the prepared Windows release:** follow
  [Build from source — prepared installation](docs/BUILD_FROM_SOURCE.md#install-the-prepared-product).
  Use the complete archive, not the installer EXE alone.
- **Compile or create a signed installer from source:** follow the separate
  [source build and signing steps](docs/BUILD_FROM_SOURCE.md),
  [tools index](tools/README.md) and [deployment index](deploy/README.md).
- **Connect an AI client:** follow [shared HTTPS MCP setup](docs/CORTEX_SHARED_MCP.md).
  Direct per-chat STDIO startup is retired. The endpoint is optional, disabled
  by default, and loopback-only unless explicitly configured otherwise.
- **Use the product:** start with the [documentation index](docs/README.md).
  [Local terminals](docs/PAO_LOCAL_TERMINALS.md) require PowerShell 7 and survive
  client disconnection only while the same ordinary owner Host remains alive.

## Recover an existing development task

1. Confirm the selected workspace and inspect its working tree, relevant source,
   `VERSION.json` and `Directory.Version.props`. Preserve unrelated work. Source,
   prepared package and installed runtime can be at different revisions.
2. Read the portable binding in `.cortex/project.json` and its explanatory
   `.cortex/README.md`. Follow the applicable exact-task recovery rules; do not
   enumerate or adopt another task's private cache or generated HOT file.
3. When installed Cortex is available and retained context can affect the work,
   use the actual task ID and ordered namespaces for one bounded `cortex_start`.
   Search previews select records; exact typed reads establish selected bodies.
   Reuse that context until a material identity, connection or recovery change.
4. If Cortex is unavailable, continue from applicable local source authority.
   Clients attach to an existing owner; missing tools do not authorize service
   startup, registration repair, a private adapter or direct database reads.
5. Checkpoint only meaningful bounded conclusions. Preserve uncertain work and
   original failures. Never store secrets, private reasoning or raw transcripts.

A fresh computer needs this checkout and the documented prerequisites; it does
not need an `N:` drive, an old source copy, a live database or another machine's
keys. On an existing development machine, any previously designated recovery
copy remains read-only. Do not migrate, rewrite or delete it as part of setup.

## Keep source, packages and installation separate

The normal build entry is `tools/Build-PegasusCortexRelease.ps1`. Its forward
outputs are `artifacts/release/unified` and `artifacts/release/installer`.
Compilation is not package, installation or fresh-machine acceptance. Source-only
cleanup does not update an installed release or any already signed bytes.
Use actual About/role metadata and the package's verified inventory when identity
matters; never select a presumed latest folder by timestamp.

Installed program and data bindings come from the 64-bit
`HKLM\Software\Pegasus\Cortex` `InstallLocation` and `DataLocation` values.
All mutable state belongs below the selected DataRoot, with separate protected
service/install and owner-private user domains. Do not copy individual binaries
into an installation or remap an unavailable database to an empty directory.

Setup requires owned components already stopped and keeps them stopped. Use the
product's graceful Exit flow; `PegasusCortex.exe --exit` retains admission,
active-work and service-exit checks. The privileged service is optional and is
never implicitly installed, enabled or started by core setup. Existing startup
choices and databases are preserved. Installed, trust, service and client changes
retain the explicit authority required by the current instructions.

The complete older dated entry is preserved in
[recovery and qualification history](docs/history/RECOVERY_HISTORY.md).
It is not a source of current approvals, startup behavior or release coordinates.
