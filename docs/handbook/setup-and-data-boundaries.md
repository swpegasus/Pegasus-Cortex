# Setup and data boundaries

There are two sensible ways to get Cortex: use a prepared release, or build a
complete signed release from source. A source checkout is useful for reading
and development, but it is not the same thing as an admitted Windows product.

## Using a prepared release

1. Read the release's availability, qualification and license notes. The current
   public offering is a development prerelease, so use its explicit release
   link rather than assuming a stable `latest` download.
2. Download the complete installer archive from the
   [official releases](https://github.com/swpegasus/Pegasus-Cortex/releases).
   Keep its installer and payload together. A copied executable by itself does
   not establish a complete installable product.
3. Check the published archive hash and follow its verification and installation
   instructions. Use the normal installation flow to select the program and
   mutable data locations. Do not move active database files by hand.
4. Install the prerequisites for the workflows you actually want. The prepared
   self-contained product includes its required .NET runtime components and
   admitted bundled dependencies. PowerShell 7 x64 MSI is a separate prerequisite
   for the supported terminal/PowerShell workflow. Git and the .NET SDK are
   needed for relevant development operations, not every ordinary context query.
5. Start the intended owner and configure the shared MCP endpoint if needed.
   It is disabled by default and loopback is the default network boundary.
   A saved setting alone does not prove that a listener or client is ready.
6. Verify the actual client connection using a harmless operation, then test the
   specific workflow you intend to use. Keep ordinary and privileged readiness
   separate.

Use the maintained [installation and source guide](../BUILD_FROM_SOURCE.md)
for exact prerequisites, official vendor links, commands and package layout.
This introductory sequence does not replace the release-specific instructions.

## Building your own release

A publisher signature is part of the product's communication and admission
design, not merely a nicer Windows publisher label. Some cooperating components
require the expected same publisher certificate before they communicate.
Building or replacing one component with a different signing identity can break
that relationship even when the executable is otherwise valid.

1. Install the source guide's SDK and build prerequisites.
2. Create your own appropriate signing identity using the maintained signing
   tooling, and protect its private key. The author's private release keys are
   deliberately not in the repository or downloads.
3. Build the complete compatible role set through the maintained release entry
   point. Sign the cooperating components with the required same publisher
   identity. Do not combine arbitrary binaries from unrelated publishers or
   generations.
4. Use the product and installer verification tools before installing or
   distributing the result. A compiler success alone does not qualify the
   signed package or its runtime communication.
5. Follow the relevant trust and installation steps for your own deployment.
   Do not use an unsigned development output as an unexplained replacement in
   an existing protected installation.

Code-signing identity and HTTPS server identity have different purposes. The
former authenticates the product's signed code and package relationships. The
latter authenticates the selected MCP server name and TLS connection. They may
be rooted in an intentionally managed certificate system, but one should not
be substituted for the other.

The [build guide](../BUILD_FROM_SOURCE.md), [release tools](../../tools/README.md)
and [shared MCP guide](../CORTEX_SHARED_MCP.md) provide the exact maintained
procedure. A prepared archive avoids building and signing the author's release
again; it does not bypass that release's verification or deployment controls.

## Choosing the runtime and connection boundary

The ordinary signed-in user Host owns local semantic work and user operations.
The optional privileged execution service has a separate role. The shared HTTPS
listener belongs to the configured Application or existing Service owner;
service gateway mode preserves the authenticated ordinary-owner route for
semantic work instead of opening a second user database as LocalSystem.

Owner changes require a deliberate restart. An application should not take over
the port because another configured owner happens to be stopped. Closing a
window can also differ from actually exiting its resident runtime.

Loopback permits clients on the local machine. Explicit private-network and
allowed-peer configuration can support an intended trusted LAN/VPN deployment.
It does not make Cortex suitable as an anonymous public Internet service. TLS
validation, application identity, machine policy and any privileged approval
remain distinct controls.

Remembered connection approval is separate development work. Do not assume
that a friendly client name, chat ID, source address or transport session ID
establishes a protected identity. Consult the prepared release's actual controls
before enabling privileged work.

## What stays where

| Material | Normal responsibility | What to consider |
| --- | --- | --- |
| Product binaries and resources | Replaceable signed installation | Update as a compatible admitted set |
| Cortex project/task/knowledge records | Selected local mutable data root and owner | Access, backups, retention and content classification |
| Personal preferences | Separately controlled local user layer | Availability and privacy; not public starter material |
| Cerebral conversations and runtime state | Cerebral's configured local data locations | Separate lifecycle and backup policy |
| Model files | Selected inference deployment | License, provenance and hardware requirements |
| Terminal shell and output buffers | Live ordinary Host, bounded memory | Host stop/reboot ends the shell; no permanent transcript promise |
| Captured images or exported files | Explicitly selected destination | May contain sensitive content; review before sharing |
| Optional hosted provider request | Selected external provider | Material sent follows that configured route and its terms |

Keeping the default store local is helpful, but it does not automatically
encrypt every artifact, redact every secret, isolate tenants or control every
optional plugin's network use.

## Preparing an isolated deployment

Provision the selected model and all required dependencies before disconnecting
the environment. Select local inference rather than a hosted provider or remote
Worker. Leave unnecessary network tools and plugins disabled. Use loopback
unless another controlled machine needs access. Review the operating system's
network, account and storage controls as well as the application's settings.

For compliance-sensitive work, document the actual data path, permitted users,
retention and deletion policy, backup/recovery procedure, enabled components,
model licenses and update process. Validate that deployment against your own
requirements. Local-first architecture supports such a plan; it is not a
compliance certificate or a guarantee that no configuration can send data out.

## License and public material

The [operative Pegasus Community License](../../LICENSE) governs the first-party
components it covers. Personal and qualifying group use below US$10 million
combined annual group revenue is permitted under those terms; at or above that
threshold a separate paid agreement is required. Read the complete license for
the actual scope and definitions. Source availability is not an OSI open-source
license claim. Separately licensed dependencies keep their own terms.

The public source and prepared release are intended to contain tools and
portable guidance, not the operator's private database, personal profile or
private signing keys. Public certificate material can be distributed for its
intended role; possession of a public certificate does not include its private
key or grant unrelated authority.

Return to the [handbook](README.md) or the [Together case study](together.md).
