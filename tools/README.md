# Cortex source tools

The maintained Windows x64 release entry is
[Build-PegasusCortexRelease.ps1](Build-PegasusCortexRelease.ps1). Follow the
[step-by-step build and signing guide](../docs/BUILD_FROM_SOURCE.md) rather than
choosing an older package script by its filename. None of these scripts runs
merely because this directory is present.

## Normal pipeline

1. Prepare and verify the pinned optional visual-library sidecars on a first
   clean builder using `assets/Get-VisualLibraries.ps1`,
   `assets/Package-VisualLibraries.ps1` and `assets/Test-VisualLibraryPackages.ps1`.
   This acquires public passive data and notices; it does not import a live store.
2. Select an authorized existing signing identity, or deliberately create a new
   builder-local identity with `signing/New-PegasusProdAlphaSigningIdentity.ps1`.
   It uses fixed subjects and non-exportable CurrentUser keys, not shared keys.
3. Run `Build-PegasusCortexRelease.ps1` with its exact root/publisher thumbprints.
   The wrapper performs focused Setup verification, calls the unified product
   publisher and NSIS installer publisher, then checks the deployment/signing
   contracts. It does not install or start Cortex.
4. Verify both `product` and `product-installer` with
   `signing/Test-PegasusSignedPackage.ps1`, using the source release version and
   checking the returned signing identities against the intended builder identity.

The normal forward outputs are `artifacts/release/unified` and
`artifacts/release/installer`. The installer EXE needs its adjacent signed
`product` folder and sidecars. Do not distribute the EXE alone or mistake a
source build for a verified installation.

## Supporting groups

- `New-PegasusCortexUnifiedProduct.ps1` and
  `New-PegasusCortexNsisInstaller.ps1` are the two normal publisher stages.
  The wrapper coordinates them; invoke a lower-level stage only for a deliberate
  bounded workflow with its actual parameters and verification.
- `windows-installer/` contains the shared declarative wizard generator and
  [adapter contract](windows-installer/README.md), not another product installer.
- `signing/` contains package signing, public-certificate export and verification.
  Trust installation is a separate explicit operation; signing does not require
  changing machine trust. Never add private keys, PFX files or passwords to source.
- `dependencies/` acquires the pinned NSIS build tool. Sysinternals remains an
  optional externally supplied tool, not an included application prerequisite.
- `assets/` retains the first-builder sidecar acquisition/package tools as well
  as separate explicit live-import tools. A source build does not call live import.
- `diagnostics/`, the native interop harness, benchmarks and continuity test
  hosts support bounded developer investigation. Their presence is not a reason
  to run a workload, scan retained data or change an installed product.

## Compatibility and historical helpers

Some old standalone x64 package/Codex-bundle names remain as bounded refusal
stubs, including `New-CortexMcpX64Package.ps1` and
`New-CortexMcpX64CodexBundle.ps1`. They return a retired-route error without
creating a package or registration. They are not an alternate build path.
The [deployment index](../deploy/README.md) distinguishes required legacy-named
manifest inputs from compatibility installers. Archived ARM64 procedures and
old one-off repair/smoke tools do not form part of the normal release pipeline.

Keep generated outputs and local diagnostic artifacts out of source control.
Do not move or delete an apparently old input until its references have been
checked: some project package-readme files intentionally use historical docs.
