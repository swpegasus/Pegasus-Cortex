# Build Cortex from source

The maintained Windows product is built from this repository. A Cortex checkout
does not require a neighboring Cerebral checkout, a developer's sibling package
feed, an installed Cortex instance, or access to a live Cortex service.

## Install the prepared product

For a normal installation, use the complete Windows x64 release archive rather
than building source. Extract the entire archive, keep its `product` directory
next to the installer EXE, and run that installer. The EXE coordinates the
adjacent signed payload; it is not a self-contained archive of that payload.
The published application roles include their .NET runtime. The target computer
does not need the .NET SDK, a source checkout, AutoIt installed globally, NSIS,
or the release signing private keys.

Use the wizard's explicit choices for optional service, trust and client
integration. Publisher trust and the certificates used for MCP HTTPS are
separate configuration concerns. Do not copy another machine's private keys,
certificate-store references or user data to make a new installation work.

## Prerequisites for a source build

- Windows x64 and the .NET SDK selected by [`global.json`](../global.json):
  `10.0.400`, with patch roll-forward enabled.
- PowerShell 7 for the maintained build and packaging scripts.
- Internet access to nuget.org for the existing public package and runtime-pack
  dependencies on the first restore. Their declared versions are unchanged.
- A normal source checkout including the admitted dependencies described below.
  No signing key or trust-store change is needed for an ordinary source build.

Keep the canonical checkout and generated paths within the product's 240-character
path budget. `VERSION.json` and `Directory.Version.props` must describe the same
source release. They do not identify the installed runtime or establish that an
older prepared package contains current source changes.

## Included dependency boundaries

`NuGet.Config` selects the repository-relative `dependencies/packages` feed for
`Pegasus.*` and the official nuget.org v3 source for other packages. The more
specific `Pegasus.*` source mapping keeps those suite packages on the bundled
feed. `Directory.Build.props` uses the same bundled feed by default. No sibling
`_packages` directory is required.

- [`dependencies/packages/PROVENANCE.md`](../dependencies/packages/PROVENANCE.md)
  records the exact `Pegasus.Shared.1.20260711.421-a.nupkg` bytes selected by
  `VERSION.json`. Do not repack it or replace it with an unversioned DLL.
- [`third_party/SharedMcp/PROVENANCE.md`](../third_party/SharedMcp/PROVENANCE.md)
  records the pinned, unchanged neutral MCP core source snapshot. This is a
  temporary source-consumption boundary until an admitted shared-repository
  package exists, not a product-adapter fork. Cortex references this local core
  project. Updating it requires a reviewed shared-owner checkpoint and refreshed
  file hashes; no Cerebral host, adapter, configuration or runtime is included.
- `third_party/autoitx/3.3.18.0/provenance.v1.json` records the three unchanged
  AutoItX libraries copied by the UiHost project. The existing
  [`AutoIt EULA`](../licenses/AutoIt-EULA.txt) permits distribution in whole or
  part with the required notices and EULA. Keep
  [`AgentOps third-party notices`](../licenses/AgentOps-THIRD-PARTY-NOTICES.txt)
  and the EULA with the source and shipped payload. A global AutoIt installation
  is not a build or runtime prerequisite for these admitted libraries.

Only these exact package/binary files are excepted from the general source-control
binary exclusions. Do not add package-cache trees, generated outputs, credentials
or signing material. Maintained `deploy/x64` scripts and its installer definition
are source inputs; release output stays under `artifacts/release`.

Dependency notices and exact archive/text digests are recorded in
[`licenses/DEPENDENCY_PROVENANCE.json`](../licenses/DEPENDENCY_PROVENANCE.json).
The audited 19.02 Windows product uses self-contained .NET, ASP.NET Core and
Windows Forms runtime packs at 10.0.15; this runtime-pack version is separate
from the source-build SDK version. Identical upstream notices are stored once
and mapped to each covered component. Preserve the Windows-specific .NET
Library License terms for the listed native runtime files as well as the MIT
texts and third-party notices; do not describe the entire Windows distribution
as MIT-only. Original NuGet asset digests and subsequently Authenticode-signed
product digests describe different bytes. The [Pegasus Community License](../LICENSE)
covers Cortex and the identified first-party Pegasus.Shared and SharedMcp
components, following the owner's explicit grant. Third-party terms remain
independent; component origin and hash records alone do not grant additional
redistribution rights.

## Restore and build

From the repository root, a focused build of the ordinary Windows host is:

```powershell
dotnet restore .\src\Pegasus.AgentOps.Tray\Pegasus.AgentOps.Tray.csproj --configfile .\NuGet.Config --runtime win-x64
dotnet build .\src\Pegasus.AgentOps.Tray\Pegasus.AgentOps.Tray.csproj --configuration Release --runtime win-x64 --no-restore --disable-build-servers '-m:1' '-nr:false' '-p:UseSharedCompilation=false'
```

The complete maintained solution is `PegasusCortex.slnx`. Entry projects declare
their supported runtime identifiers; retain those declarations when building a
wider graph. An affected source build or a focused fixture pass does not replace
the complete self-contained publish, signed inventory, installer admission or
fresh-machine installation checks.

Do not alter pinned package versions or remove lock-file checks merely to make a
cold restore pass. A restore using a populated global package cache is not proof
that a new computer can acquire every dependency from the declared sources.

## Create a release package

On a first clean builder, prepare the pinned public visual-library sidecars before
the installer publisher. The current complete distribution includes these four
optional install-time data modules; generated archives are not checked into Git.

```powershell
& .\tools\assets\Get-VisualLibraries.ps1
& .\tools\assets\Package-VisualLibraries.ps1
& .\tools\assets\Test-VisualLibraryPackages.ps1
```

These steps acquire the pinned upstream bytes and notices and create
`artifacts/release/assets/visual-library-packages.json` plus its four archives.
On a later build, reuse the existing verified packages; the packaging step is
create-only. This source preparation does not import data into a running Cortex
database. Do not run the separate live import/descriptor publication scripts as
part of a source build. Installing the already complete release archive needs
none of these builder steps or upstream downloads.

Release construction is separate from ordinary compilation. The normal publisher
owns the self-contained `win-x64` restore and publication of every required role:

```powershell
& .\tools\New-PegasusCortexUnifiedProduct.ps1 -RootThumbprint '<existing-root-thumbprint>' -PublisherThumbprint '<existing-publisher-thumbprint>'
& .\tools\New-PegasusCortexNsisInstaller.ps1 -RootThumbprint '<existing-root-thumbprint>' -PublisherThumbprint '<existing-publisher-thumbprint>'
```

Supply the selected existing signing identities with usable private-key handles
on the authorized build machine. The SDK host defaults to
`C:\Program Files\dotnet\dotnet.exe`; `-DotNetPath` on the product publisher can
select another SDK host explicitly. The publisher acquires the exact NSIS archive
through `tools/dependencies/Get-NsisBuildTool.ps1`, verifies its pinned hashes,
and includes the admitted compiler and notices in the product. Packaging may
therefore need the official NSIS download in addition to a NuGet restore.

The reusable forward output directories are `artifacts/release/unified` and
`artifacts/release/installer`. Distribute the complete checked installer output,
including its adjacent `product` directory. Keep private keys, PFX files,
passwords and private-key exports out of source and release uploads. The normal
signed installer's public trust bundle is part of its admitted distribution;
it contains no signing private key.
The current Prod Alpha signing setup is documented in
[`tools/windows-installer/README.md`](../tools/windows-installer/README.md#certificates-and-admission).

The legacy optional ARM64 packaging helper has a separately configured runtime
archive input and is not part of this Windows x64 release path.
