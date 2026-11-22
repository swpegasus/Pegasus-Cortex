# Build Cortex from source

The maintained Windows product is built from this repository. A Cortex checkout
does not require a neighboring Cerebral checkout, a developer's sibling package
feed, an installed Cortex instance, or access to a live Cortex service.

## Install the prepared product

1. Download the complete Windows x64 archive from
   [the project releases](https://github.com/swpegasus/Pegasus-Cortex/releases).
   A source ZIP and the installer EXE alone are different inputs.
2. Extract the entire archive to one ordinary local folder. Keep its `product`
   directory, public verification material and optional-data sidecars beside
   `PegasusCortexInstaller.exe`. Do not run it from inside the ZIP.
3. For an existing installation, finish active work and use Cortex's graceful
   Exit flow first. Setup requires owned components already stopped; it does
   not kill them to manufacture an update window. Codex may remain open for a
   core-only update, while selected Codex file changes have their own closure check.
4. Run `PegasusCortexInstaller.exe` and review the normal Windows approval and
   wizard choices. Service, publisher trust and Codex integration are separate
   explicit options, not prerequisites silently enabled by core setup.
5. After setup, launch Cortex from its Start Menu shortcut. Setup keeps runtime
   stopped and preserves existing startup choices. Configure the optional shared
   endpoint through [MCP setup](CORTEX_SHARED_MCP.md), then verify a real client
   connection separately from a saved setting.

The coordinator EXE is not a self-contained archive of the payload. Published
application roles include their .NET runtime. An end user does not need the .NET
SDK, a source checkout, global AutoIt, NSIS or release signing private keys.
Local terminals additionally require the registered PowerShell 7 x64 MSI
installation described in the [terminal guide](PAO_LOCAL_TERMINALS.md).

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

Official references: [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0),
[PowerShell 7 MSI](https://learn.microsoft.com/powershell/scripting/install/install-powershell-on-windows),
[Git for Windows](https://git-scm.com/downloads/win), and
[Windows/.NET OS dependencies](https://learn.microsoft.com/dotnet/core/install/windows).
Use the repository's declared SDK and pinned dependency versions. The normal
prepared installer already includes its application runtimes and admitted tools;
these developer prerequisites do not turn into extra end-user requirements.

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

## Compile ordinary source

1. Install the source prerequisites above and open PowerShell 7 as your normal
   Windows user. No administrator session or machine trust change is needed
   merely to restore and compile source.
2. Obtain a normal checkout in a short local path. For example:

```powershell
git clone https://github.com/swpegasus/Pegasus-Cortex.git C:\src\Pegasus-Cortex
Set-Location C:\src\Pegasus-Cortex
dotnet --version
```

   Use `global.json` to check the selected SDK. A fresh checkout needs no
   neighboring Cerebral checkout, `N:` recovery drive or live Cortex database.
3. Restore and build the ordinary Windows Host:

```powershell
dotnet restore .\src\Pegasus.AgentOps.Tray\Pegasus.AgentOps.Tray.csproj --configfile .\NuGet.Config --runtime win-x64
dotnet build .\src\Pegasus.AgentOps.Tray\Pegasus.AgentOps.Tray.csproj --configuration Release --runtime win-x64 --no-restore --disable-build-servers '-m:1' '-nr:false' '-p:UseSharedCompilation=false'
```

The result is a source build, not an installed product or signed installer.
Do not copy its DLLs over a running installation. The complete maintained
solution is `PegasusCortex.slnx`. Entry projects declare
their supported runtime identifiers; retain those declarations when building a
wider graph. An affected source build or a focused fixture pass does not replace
the complete self-contained publish, signed inventory, installer admission or
fresh-machine installation checks.

Do not alter pinned package versions or remove lock-file checks merely to make a
cold restore pass. A restore using a populated global package cache is not proof
that a new computer can acquire every dependency from the declared sources.

## Create a signed installable package

This is separate from ordinary compilation. It runs focused fixtures, publishes
all normal roles, signs immutable files and creates the installable layout. It
does not install Cortex, start a service, register a client or publish a release.
Use a deliberate build window; the normal wrapper reuses its forward output
folders instead of generating automatic rollback copies.

### 1. Prepare the first-builder visual sidecars

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

### 2. Select or create the signing identity

Signing is also part of Cortex's inter-process identity, not just a Windows
download label. Protected cooperating components compare the exact valid
publisher certificate. In particular, the existing Cerebral application route
compares the Cerebral service's publisher with the Cortex execution service's
publisher. Sharing a root CA or using similar certificate subject names does not
satisfy that exact-certificate check.

Build and sign the complete compatible set of cooperating roles with the same
authorized publisher identity. Replacing one component with an unsigned file or
a file signed by a different publisher can correctly prevent communication.
Mixing a self-built Cortex service with an official Cerebral service is not a
supported way to bypass this boundary; use a matching published set or build and
sign the cooperating product components with your own consistent identity.
Standard external MCP clients follow the HTTPS connection contract and do not
need the product publisher's private key.

The prepared release already contains its publisher signatures, signed manifests
and admitted file inventory. A rebuild produces different bytes and must pass the
same admission with your own signatures. Private signing keys are deliberately
absent from the repository. Ordinary compilation for inspection/tests does not
require constructing a signed installer. Signing proves identity and integrity
under the selected policy; it does not prove correctness or grant a local
development certificate universal Windows trust.

Use signing keys you are authorized to use on this builder. If the exact existing
identity is available in your CurrentUser certificate store, select both pins:

```powershell
$identity = & .\tools\signing\New-PegasusProdAlphaSigningIdentity.ps1 `
  -ExistingRootThumbprint '<exact-existing-root-thumbprint>' `
  -ExistingPublisherThumbprint '<exact-existing-publisher-thumbprint>'
```

For a new builder without either identity, deliberately create a new local
development identity instead:

```powershell
$identity = & .\tools\signing\New-PegasusProdAlphaSigningIdentity.ps1 `
  -PublicOutputDirectory .\artifacts\signing-public
```

That output directory must be absent. The script creates non-exportable RSA-3072
keys in `CurrentUser\My`, with the fixed subjects
`CN=Pegasus Local Development Root CA` and
`CN=Pegasus Software Signing Prod Alpha`. It accepts no custom subject arguments.
If either subject already exists, it refuses implicit selection or replacement;
use both exact existing thumbprints. An optional `ValidityYears` is 1–10, default
5. Only public `.cer` files are exported. No machine trust is installed or required
just to sign, and these code-signing certificates are not MCP server certificates.

Keep the keys on the builder. A different builder creates its own authorized
identity; never share a private key, PFX, password, PIN or another machine's
certificate-store state to reproduce a build.

### 3. Run the normal release wrapper

```powershell
& .\tools\Build-PegasusCortexRelease.ps1 `
  -RootThumbprint $identity.RootThumbprint `
  -PublisherThumbprint $identity.PublisherThumbprint `
  -Provider $identity.Provider `
  -DotNetPath 'C:\Program Files\dotnet\dotnet.exe'
```

Use the actual SDK host path if it differs. The wrapper checks that `VERSION.json`
and `Directory.Version.props` agree, restores and runs focused Setup verification,
then invokes `New-PegasusCortexUnifiedProduct.ps1` and
`New-PegasusCortexNsisInstaller.ps1`. It finally runs the maintained Windows
installer-tools, deployment-plan and signing checks. Those publishers own the
normal self-contained `win-x64` restore/publish graph. The product publisher
acquires the exact NSIS archive through `tools/dependencies/Get-NsisBuildTool.ps1`,
verifies its pins and includes its notices. First packaging therefore may need
official NSIS downloads as well as the declared NuGet/runtime-pack sources.

Stop on a failed stage and retain its original result. Do not bypass package,
identity or signing checks or replace a failed output with an older folder.

### 4. Verify the product and outer installer

Use the release version from this exact source, not a version copied from an old
log. Verify both normal role layouts:

```powershell
$release = (Get-Content .\VERSION.json -Raw | ConvertFrom-Json).releaseVersion
$productCheck = & .\tools\signing\Test-PegasusSignedPackage.ps1 `
  -PackageRoot .\artifacts\release\unified `
  -Role product -ReleaseVersion $release
$installerCheck = & .\tools\signing\Test-PegasusSignedPackage.ps1 `
  -PackageRoot .\artifacts\release\installer `
  -Role product-installer -ReleaseVersion $release
foreach ($check in @($productCheck, $installerCheck)) {
  if ($check.RootThumbprint -cne $identity.RootThumbprint -or
      $check.PublisherThumbprint -cne $identity.PublisherThumbprint) {
    throw 'The verified package uses a different signing identity than intended.'
  }
}
```

The verifier admits the detached signature first, then exact file inventory and
hashes, then Authenticode files. The normal publishers retain additional Cortex
catalog/role checks. Keep the canonical license, README, notices, public certificate
bundle and signature containers with the checked output.

### 5. Install or distribute only the checked complete layout

The reusable forward output directories are `artifacts/release/unified` and
`artifacts/release/installer`. Distribute the complete checked installer output,
including its adjacent `product` directory. Keep private keys, PFX files,
passwords and private-key exports out of source and release uploads. The normal
signed installer's public trust bundle is part of its admitted distribution;
it contains no signing private key.
The current Prod Alpha signing setup is documented in
[`tools/windows-installer/README.md`](../tools/windows-installer/README.md#certificates-and-admission).

The existing Prod Alpha root is a local development identity, not a publicly
trusted commercial publisher. A public development package may carry its public
verification certificates; Windows or a client may still show trust warnings.
Recipient trust installation is a separate deliberate choice, never proof of
private-key ownership and never a prerequisite to source compilation. Local
MCP HTTPS setup creates/selects its own target-machine configuration.

Record package verification, actual installed behavior and fresh-machine acceptance
separately. No statement in this guide claims that a new builder or target has
already passed them. The [tools index](../tools/README.md) and
[deployment index](../deploy/README.md) explain retained compatibility names.
The old standalone ARM64/PostgreSQL/SSH-STDIO release path is deferred, not an
alternate Windows packaging route.
