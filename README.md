# Pegasus Cortex

Local continuity, knowledge retrieval and bounded Windows tools for Pegasus and compatible AI clients. Published by Shayne Winter under the [Pegasus Community License 1.0](LICENSE).

[**Download the prepared Windows x64 development release**](https://github.com/swpegasus/Pegasus-Cortex/releases)

[**Cortex website and learning guide**](https://cerebral.shaynewinter.com/cortex/)

The Cortex section of the Cerebral website introduces the product for people who
are new to AI. It explains its evolution, practical workflows and how to get
started. Installation and source-build instructions are also provided below.

## Companion application

[Pegasus Cerebral](https://github.com/swpegasus/Pegasus-Cerebral) is the Windows AI workspace, model orchestrator and extensible plugin host.

The [Cerebral repository catalog](https://github.com/swpegasus/Pegasus-Cerebral#plugin-repositories) links the plugin repositories and records the current shared-library layout.

## License and use

Personal use is free. Organizations with less than **US$10 million in combined
annual group revenue** can also use Cortex commercially under the community
license. At or above that threshold, a separately signed paid agreement is
required. The terms permit source and precompiled distribution, modifications,
plugins and integrations. They do not claim ownership of your data or outputs.
Read the [complete license](LICENSE) for eligibility and distribution conditions.

This is source-available community software with a business-size restriction,
rather than an OSI open-source license. Bundled third-party code, runtime files,
tools and artwork retain their own terms in [licenses](licenses/).
For author information or commercial licensing, visit [Shayne Winter](https://shaynewinter.com/).

## Shared foundations

The planned Pegasus Shared repository groups reusable diagnostics, common schemas/compatibility contracts and MCP transport as separate versioned libraries. Each product keeps its own control adapter, tool registry, state, authorization and release.

The neutral MCP core is distributed as a pinned source snapshot for reproducible
Cortex builds. Product adapters, state and control remain product-owned.

## Install on a new computer

Use Windows x64. Local persistent terminals require an installed **PowerShell 7
x64 MSI installation**; use [Microsoft's installation guide](https://learn.microsoft.com/powershell/scripting/install/install-powershell-on-windows).
Cortex discovers that registered installation when its ordinary Host starts.
It does not silently substitute Windows PowerShell 5 or Command Prompt. If you
install PowerShell after Cortex is running, deliberately restart the Cortex Host.

The prepared installer includes the application's .NET runtime, AutoItX libraries
and product-local NSIS tools. You do not need a compiler, the .NET SDK, a global
AutoIt installation or a separate NSIS installation just to run the product.
Git, a developer SDK and other external tools are needed only for the operations
that use them; Cortex's tool catalog reports what is configured locally.

### Prerequisites and official references

| Component | What a user needs | Official reference |
| --- | --- | --- |
| Windows x64 | Use a current supported Windows installation. Cortex's checks on this machine do not qualify every OS in .NET's wider support list. | [Microsoft Windows/.NET requirements](https://learn.microsoft.com/dotnet/core/install/windows) |
| .NET 10, Windows Forms and ASP.NET Core runtime | Included in the prepared Cortex application roles; no separate global runtime installation is needed for Cortex. | [.NET 10 information and downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) |
| SQLite | Included through the pinned SQLitePCLRaw libraries; no database-server installation is required. | [SQLite](https://www.sqlite.org/) |
| AutoItX 3.3.18.0 | Included for the Windows UI tools; no global AutoIt installation is required. | [AutoIt / AutoItX](https://www.autoitscript.com/site/autoit/) |
| NSIS 3.12 | Included product-local tools; the maintained builder acquires the pinned version. Users need no separate NSIS installation. | [NSIS documentation](https://nsis.sourceforge.io/Docs/) |
| PowerShell 7 x64 MSI | Install separately for persistent terminals and registered PowerShell tools, then restart the Cortex Host. | [Microsoft installation guide](https://learn.microsoft.com/powershell/scripting/install/install-powershell-on-windows) |
| Git for Windows | Needed only for Git operations or cloning source. | [Official Git for Windows download](https://git-scm.com/downloads/win) |
| .NET SDK | Needed for source builds and SDK operations, not for running the prepared product. Match [global.json](https://github.com/swpegasus/Pegasus-Cortex/blob/main/global.json), currently SDK 10.0.400 with its declared roll-forward policy. | [.NET 10 SDK downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) |
| Visual C++ / Windows system prerequisites | Conditional on the Windows version or additional native tools; this is not a blanket extra Cortex installer requirement. Follow Microsoft's OS-specific dependency guidance. | [Microsoft Visual C++ redistributable reference](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist) |

The bundled components retain their own [licenses and provenance](licenses/).
Additional project tools may have their own prerequisites; check their registered
availability before using the corresponding operation. Bundled versions are
part of the checked product inventory, rather than instructions to replace them
with unrelated downloads.

Normal Windows approval is required for installation and selected machine-wide
settings, service or trust changes. Configure a compatible Streamable HTTP MCP
client when you want to connect an AI assistant. Internet access is needed to
download the release and any separately chosen tools; the default local Cortex
connection stays on this computer.

Use the complete Windows x64 development installer archive from the repository's
releases. Extract the entire archive and keep its adjacent `product` directory;
the coordinator executable alone is not the installation. Run
`PegasusCortexInstaller.exe` and follow its normal Windows elevation and trust
prompts. The installer preserves an existing selected data location and its
records. No developer SDK or Cerebral checkout is required on the target computer.

Open Cortex. For MCP clients, use **Settings & support → MCP → Set up and save MCP**
to provision the selected local endpoint through the product's certificate
controls. The shared endpoint is disabled until deliberately configured. Select
Application ownership, or an installed local execution service. After setup,
deliberately restart the selected owner before connecting, even when the owner
choice did not change; enabling MCP also takes effect at startup. Loopback is the
default, and private-network access requires explicit configuration.

Configure a compatible Streamable HTTP client with the readable connection name
**Cortex** and the default URL `https://localhost:47831/mcp/cortex`. Verify status
through that connection. A valid server certificate, machine trust and an
available ordinary user Host are distinct requirements. Details and evidence
limits are in [the shared MCP guide](docs/CORTEX_SHARED_MCP.md).

## Persistent local terminals

Cortex owns terminal sessions independently of an individual MCP connection.
Closing or reconnecting a client preserves the shell while the ordinary Cortex
Host stays running. Discover retained sessions, reconnect to the selected session,
and explicitly close it when finished. A Cortex Host restart or computer reboot
ends the owned shell; terminal output buffers are not durable transcripts.
See [local terminals](docs/PAO_LOCAL_TERMINALS.md) for supported operations, bounds
and exact lifecycle semantics. Remote terminal providers are deferred.

## Build and release

See [Build from source](https://github.com/swpegasus/Pegasus-Cortex/blob/main/docs/BUILD_FROM_SOURCE.md) for the declared Windows SDK,
pinned dependencies and normal build/release route. Signing keys and machine
certificates remain external to this repository. Generated artifacts, databases,
private continuity caches and credentials are excluded from source publication.

The distribution may include public publisher and root certificates used to
verify its signed package. Public certificates and signature containers contain
no signing private keys. Local MCP/TLS private keys and user certificate stores
are created or selected on the target computer through the explicit setup tools;
they are not transferred from the developer's machine in the installer.

This is a development release. Source/fixture qualification, a signed package,
installed verification and a clean-machine test are reported separately; none
should be inferred merely from a version number or from this README.
