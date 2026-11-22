# Pegasus Cortex

Local continuity, knowledge retrieval and bounded Windows tools for Pegasus and compatible AI clients. A modular foundation for a **Windows R&D environment for AI**: keep useful context close, connect the tools you need, and build workflows on systems you control. Published by Shayne Winter under the [Pegasus Community License 1.0](LICENSE).

[**Download the prepared Windows x64 development release**](https://github.com/swpegasus/Pegasus-Cortex/releases)

[**Cortex website and learning guide**](https://cerebral.shaynewinter.com/cortex/)

The Cortex section of the Cerebral website introduces the product for people who
are new to AI. It explains its evolution, practical workflows and how to get
started. Installation and source-build instructions are also provided below.
For more depth, open [the Cortex handbook](docs/handbook/README.md) or explore
[Cerebral and Cortex together](https://cerebral.shaynewinter.com/together/).

## Companion application

[Pegasus Cerebral](https://cerebral.shaynewinter.com/) brings conversations, a selected model and plugin tools into a Windows workspace. Cortex supplies project continuity and local operations. The modular design lets the workspace grow through selected components and workflows while keeping model execution, retained context and privileged work in their own roles.

The [Cerebral feature guide](https://cerebral.shaynewinter.com/#features) introduces its workspace components and extension model.

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

Created by Shayne Winter, with development assistance from
[OpenAI Codex](https://openai.com/codex/) and [Pegasus Cerebral](https://cerebral.shaynewinter.com/).
See the included license for use and distribution terms.

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

### Connect Codex or another MCP client

**Starting with 03.54, the endpoint URL alone is not enough.** Each client needs
its own remembered connection identity and approval. Existing 22.34 connections
must complete this setup after upgrading; the former root codes are retired.

1. Open **Connections & access → Connections & Full control** in Cortex.
2. Enter a readable connection and application name, then choose **Create local
   connection**. This creates a unique public connection ID and keeps its private
   credential in your Windows user vault.
3. Select the request and choose **Approve ordinary access**, accepting the normal
   Windows approval prompt.
4. Choose **Copy Codex settings** and replace the existing `[mcp_servers.Cortex]`
   table in your Codex configuration. Keep the generated local helper and **your
   own connection ID**; never copy an ID from another user's configuration.
5. Save, reconnect Cortex in Codex, and ask the assistant to check Cortex status.

Approval survives reconnects until you revoke it. Full control is a separate,
optional approval; all chats using the same connection share its permissions.
Use separate connections when access should differ.

Follow [MCP connection setup and upgrade instructions](docs/MCP_CONNECTION_SETUP.md)
for the configuration example, reconnect troubleshooting and other clients.
The [full shared MCP guide](docs/CORTEX_SHARED_MCP.md) covers endpoint ownership,
TLS and trusted-network setup.

## Persistent local terminals

Cortex owns terminal sessions independently of an individual MCP connection.
Closing or reconnecting a client preserves the shell while the ordinary Cortex
Host stays running. Discover retained sessions, reconnect to the selected session,
and explicitly close it when finished. A Cortex Host restart or computer reboot
ends the owned shell; terminal output buffers are not durable transcripts.
See [local terminals](docs/PAO_LOCAL_TERMINALS.md) for supported operations, bounds
and exact lifecycle semantics. Remote terminal providers are deferred.

## Build and release

There are three separate paths: install the prepared archive, compile ordinary
source, or construct a signed installable package. The
[step-by-step guide](https://github.com/swpegasus/Pegasus-Cortex/blob/main/docs/BUILD_FROM_SOURCE.md)
explains their prerequisites and verification. The normal release entry is
`tools/Build-PegasusCortexRelease.ps1`; older standalone package scripts are not
alternate release routes. Signing keys and machine certificates remain external
to this repository. Generated artifacts, databases, private continuity caches
and credentials are excluded from source publication.

For contributors, [Start here](https://github.com/swpegasus/Pegasus-Cortex/blob/main/START_HERE.md)
is the compact current source entry. Its older recovery/qualification history is
preserved separately; a fresh computer needs no recovery drive or prior machine.

The distribution may include public publisher and root certificates used to
verify its signed package. Public certificates and signature containers contain
no signing private keys. Local MCP/TLS private keys and user certificate stores
are created or selected on the target computer through the explicit setup tools;
they are not transferred from the developer's machine in the installer.

This is a development release. Source/fixture qualification, a signed package,
installed verification and a clean-machine test are reported separately; none
should be inferred merely from a version number or from this README.
