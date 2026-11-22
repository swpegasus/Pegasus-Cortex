# Deployment inputs and compatibility

For installation, download and extract the complete Windows x64 release archive.
Run its `PegasusCortexInstaller.exe` beside the signed `product` directory.
For source construction, use [the build/signing guide](../docs/BUILD_FROM_SOURCE.md)
and `tools/Build-PegasusCortexRelease.ps1`. Do not choose an old installer from
this directory as a shortcut.

## Required current inputs

- `x64/windows-installer.json` is the current Cortex definition for the shared
  NSIS generator. The publisher supplies current release/file versions from
  `VERSION.json`; historical version literals in the template are not installed
  release evidence. Its explicit Cortex contract keeps runtime stopped, makes
  the privileged service optional and preserves the selected data root.
- `x64/New-PegasusCortexV2PackageManifest.ps1` retains its V2 filename but is
  called by both normal publishers. It creates the inventory for the actual
  signed product or installer role; deleting it would break the normal pipeline.
- `x64/Install-PegasusCortexV2X64.ps1` is the retained fixed signed-package
  compatibility/bootstrap input. It is not the normal interactive installation
  recommendation or a reason to bypass the current Setup adapter.

## Retired entry points

`x64/Install-PegasusCortexX64.ps1` and
`x64/Install-PegasusCortexOperationalX64.ps1` remain explicit refusal stubs for
the old standalone/profile and per-user operational routes. They do not inspect
their supplied package paths, create a private runtime or register a second MCP
connection. Their error points to the unified Setup package.

Linux ARM64 standalone/PostgreSQL/SSH-STDIO deployment is deferred; the former
tool cluster has been removed. Its [historical design summary](../docs/CORTEX_MCP_ARM64_INSTALLATION_PLAN.md)
does not qualify a supported ARM64 installer or a remote connection.

Generated products, installer output, private certificates and machine state
belong outside this source-input directory. Setup and installed verification
are separate from source preparation; they preserve existing databases and
require their own applicable authority.
