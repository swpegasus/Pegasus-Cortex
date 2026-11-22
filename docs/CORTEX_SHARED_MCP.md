# Cortex shared HTTPS MCP

Release **03.54** replaces URL-only access and root codes with named persistent
connection approvals. When upgrading from 22.34, follow the
[connection setup and migration steps](MCP_CONNECTION_SETUP.md) before reconnecting
your MCP client. The connection ID identifies an approval; the private credential
proves that the client owns that identity.

The optional shared endpoint uses the product-neutral `SharedMcp` library.
Each HTTP session has independent request IDs, cancellation and stream labels;
it shares the existing Cortex application and bounded dispatcher rather than
creating a database or adapter process for each chat. Client labels identify
requests and never select a Windows execution identity.

The source currently consumes the neutral core through the explicit project
reference in `PegasusCortex.Mcp.Hosting.csproj`. No published shared package or
source extraction is implied. Normal self-contained Host and execution-service
publishes must include the ASP.NET runtime closure. `DevHost` and the optional
STDIO bridge are development tools, not additional installed services.

The public direct STDIO startup is retired in current source: explicit `stdio`
and the semantic adapter's empty invocation refuse before initializing client
or database state. The normal product's empty invocation still opens its GUI.
The removed per-chat relay is not a fallback when HTTPS is unavailable. Native
product connections, the reusable MCP protocol implementation and explicit local
profile service/bridge modes remain separate supported paths.

Codex uses one shared `Cortex` HTTPS entry. The generated settings select its
endpoint and local credential helper; a URL alone is not connection approval.
Managed registration can remove the exact legacy `pegasus_cortex` entry, but
never creates or re-enables it. The accompanying plugin supplies skills and
guidance only and declares no second MCP connection.

## First connection and ordinary access

1. In Cortex, open **Connections & access → Connections & Full control**.
   Enter a readable connection name and application name, such as your Codex
   installation. The optional chat label is descriptive.
2. Select **Create local connection**. Cortex creates a random 256-bit credential
   in the current Windows user's Credential Manager and a public enrollment
   request containing its digest and connection ID. Creation does not approve it.
3. Select the request and choose **Approve ordinary access**. Confirm the fixed
   Cortex Windows elevation prompt. Wait for the page to confirm current approval;
   if the outcome is unconfirmed, use **Refresh** before another change.
4. Select the approved row and choose **Copy Codex settings**. Replace the existing
   `[mcp_servers.Cortex]` table in Codex's configuration with the generated table,
   rather than adding a duplicate. It contains the configured HTTPS URL, public
   connection ID and `http_headers_helper` command string, never the bearer.
5. Reload the Codex connection. Its local helper reads the credential from that
   same Windows user's vault and supplies headers directly to the HTTP client.
   Approval persists across reconnects until revoked; reconnecting does not create
   another grant or turn a chat label into identity.

For privileged operations, separately choose **Grant Full control…** and confirm
its red warning and fixed elevation prompt. Full control is off by default.
**Every chat sharing this credential shares its Full control authority.** Use
separate connections when permissions must differ. **Revoke Full control** removes
that extra authority while retaining ordinary approval; **Revoke all access…**
disables the connection, including reconnects. New dispatches recheck current
generation and approval. Already-started effects retain their original ownership.

Protected PAO requests additionally use boolean `system:true` and require the
optional execution service and the actual authenticated ordinary owner Host.
A bare `pao system=true` intent cannot manufacture authority. Registered User tools
keep their ordinary owner token. Cerebral's private application Full control grant
under **Applications** remains separate from MCP connection approvals. Service
controls are under **Settings & support → Administrator service**; approving a
connection does not install, start or enable the service.

## Generate settings or enroll another Windows client

The installed launcher can print the same public Codex table for its configured
HTTPS endpoint:

```powershell
PegasusCortex.exe mcp config --connection <PUBLIC_CONNECTION_ID>
```

The generated helper is one command **string**, not an argument array. Keep the
actual generated launcher path and ID. Do not run `mcp headers` to capture or paste
its output: that command supplies private authorization headers to Codex directly.
The configuration and public request file are the shareable setup material.

On another Windows client, create its credential in that client's own user vault
and write only its public enrollment request to an unused destination:

```powershell
PegasusCortex.exe mcp enroll --name "Office Codex" --application "Codex" --request-file <NEW_PUBLIC_REQUEST.json>
```

Transfer that public digest request to the server and use **Import public request…**,
then approve it there. Keep the credential on the originating client. Generate
that client's helper settings with its own installed launcher, then set only the
public table's URL to the server's explicitly allowed HTTPS endpoint. Keep its
local helper path and connection ID. Do not copy another user's vault or assume
the server's helper can read the client's credential. Client trust in the server's
CA and the server's allowed network remain separate setup requirements.

Codex documents `http_headers_helper` for HTTP MCP connections made from the local
execution environment. It is not supported for STDIO or remote-executor MCP.
See the official [MCP guide](https://learn.chatgpt.com/docs/extend/mcp) and
[configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference).
The helper is invoked locally even when the explicitly configured Cortex endpoint
is on an admitted LAN/VPN host. Existing explicit bearer/OAuth configuration can
take precedence over helper authorization; replace the old Cortex table with the
generated one instead of mixing credential sources.

TLS and peer-network admission remain required. Default binding is loopback only;
LAN/VPN inclusion is explicit and does not grant ordinary access or Full control.
A different client must also trust the selected server CA and use its valid HTTPS
name. The server certificate proves the endpoint, not a chat's authority.

## Results and requested detail

Normal PAO MCP requests return the useful result without repeating the submitted
command, request ID or routine receipt metadata. Queued work retains its job ID;
failures, partial results, uncertainty and truncation retain actionable evidence.
For full machine receipts choose `output: "structured"`; `detail: true` also
requests retained command/receipt details during selected-job inspection. These
presentation choices do not change execution, persisted receipts or CLI JSON.

`cortex_start` and `cortex_search` default to compact previews. Explicit
`format: "detailed"` retains the full preview shape. Namespace, revision,
provenance and continuation information needed to use the result remain available.
Exact reads retain the requested full record bodies, and file reads and command
execution retain useful content rather than replacing it with a success receipt.

These defaults describe current source; an older prepared release keeps the
presentation contract it was built with.

## Configuration and ownership

The protected setting is `<selected DataRoot>/install/shared-mcp.v1.json`.
A missing file selects disabled Application ownership, IPv4 loopback port47831,
server name `localhost`, and loopback peers only. LAN/VPN binding and peer CIDRs
require explicit private-network opt-in. Configuration does not change Windows
firewall rules or create startup entries.

`Application` ownership attaches the listener to the existing ordinary Cortex
Host and its actual semantic runtime. `Service` ownership attaches HTTPS to the
existing LocalSystem execution service. Service requests use its existing
authenticated, generation-pinned route to the registered ordinary owner Host;
the service does not open a user database or start an ordinary Host. If that
Host is unavailable, semantic requests fail explicitly without replay.

Settings are read once at startup. A stopped configured service does not permit
an application takeover. Changing owners requires deliberately stopping the
previous owner and restarting the selected owner. An occupied port is reported
as unavailable with restart pending; Cortex never adopts or kills its occupant.
Shutdown closes all ingress before joining actual product callbacks and
disposing their composition. An incomplete callback retains its ownership.

The lazy MCP page under Settings & support edits Enabled and Owner. Its
settings-only action rereads current protected settings and preserves network and
certificate fields. The development source also has an explicit guided setup
action described below. Saving does not apply a running listener change. The
page distinguishes saved configuration from unobserved runtime status.

## Certificates

References contain a Windows location, store and exact lowercase SHA256 of public
DER. Application private leaves use CurrentUser/My; service private leaves use
LocalMachine/My. Service CA references use LocalMachine/Root. Machine-wide CA
trust does not grant the service access to a CurrentUser private key.

Use the explicit PAO certificate operations described in
[PAO_CERTIFICATES.md](PAO_CERTIFICATES.md) to author and bind material.
`issue_machine_server` keeps the machine key nonexportable in Windows while the
ordinary owner signs only its public CSR. Trust installation remains a separate
operation. The fixed `--shared-mcp-certificates` Host setup mode selects existing
role-specific leaf and machine Root references after checking the expected
owner SID, name, selected-CA chain and signing capability; it neither issues
material nor enables the endpoint. Its exact argument order is owner
(`Application` or `Service`), leaf SHA256, authority SHA256, expected owner SID.

At listener startup, the controller performs real selected-CA HTTPS loopback
qualification with the chosen certificate before product dispatch opens. The
ordinary system-trust observation is separate. This proves local certificate
use, not LAN/VPN reachability or trust inside another client's certificate stack.

## Verification boundaries

The source has focused protocol, ownership, shutdown, settings, key-session and
helper fixtures. A configured network fixture also passed with the real Windows
certificate references, default-system-trust HTTPS, the actual Cortex
composition and a new empty owned SQLite database; no PAO execution occurred.
Its listener, callbacks and owned database were released before success.

A separate 150-second read-only shared-bridge workload completed22715 requests
across11 agents with no workload errors. One other agent failed initialization
and was excluded without retry. This is shared HTTPS/installed-backend evidence,
not deployment proof for the new embedded code.

An earlier Codex adoption check used the shared development bridge and removed
the old direct registration. That historical bridge qualification was distinct
from the later installed embedded handover below; the bridge and its adapter
processes were stopped during that handover.

The installed development handover was qualified on 2026-11-21 with release
`2.2026.11.21.14.26.A`. The existing execution service owned the IPv4-loopback
listener and served the new nonexportable machine certificate under the existing
trusted CA. A real TLS 1.3 handshake used default system trust and localhost-name
validation. The actual Codex shared connection returned the installed version,
recovered its existing exact task revision, and completed twelve status requests
with up to four concurrent calls. An owned local terminal also preserved its
process generation and variable state through disconnect/reconnect, then closed
and released. This is installed service/TLS/client evidence, not LAN/VPN,
reboot, remote-terminal or long-duration load qualification.

The first machine-key attempt correctly refused a Software-KSP permission
readback and confirmed unbound-key cleanup before any certificate-store action.
The validator was corrected to accept the exact observed full-control encoding
for the same SYSTEM/Administrators principals while retaining the protected DACL
and every other key-policy check. The successful new request used a fresh RID;
the original failure was not replayed or relabeled.

## First-time setup

After configuration, the listener follows the selected existing owner's normal
startup and shutdown. Installed 14.26 has the settings-only page; later development
source adds **Set up and save MCP** inside the ordinary running Host. This does
not establish that a later package or installed GUI contains the new workflow.

The deliberate setup action explains system-wide CA trust before continuing.
It composes the existing owner certificate operations, same-owner UAC helpers and
protected settings controls. Complete configured references are reused without
issuing a certificate or opening a CA private key. A fresh setup uses versioned,
deterministic create-only references and RIDs. Application ownership issues a user
leaf and binds it to CurrentUser/My; Service ownership creates a nonexportable
LocalMachine/My leaf through the existing machine-key operation. Public CA trust
uses LocalMachine/Root. No private export, signing-store scan or substitute
execution identity is introduced.

A configured CA without the selected role's leaf needs an explicitly supplied
matching issuer reference. A public CA pin cannot select its private signer.
Failed, partial, unknown, pruned or mismatched child results stop the workflow;
the original known JID remains available for review. No new RID is minted to
repeat uncertain work. Existing material is not removed as rollback.
After Refresh, an exact canceled service-issuance or CA-trust step can offer a
distinct **Review and retry canceled step** action. It requires a verified original
receipt proving no administrator payload, publication or store effect occurred.
The reviewed step alone receives a deterministic successor identity linked to its
original; prior successful steps keep their identities. Reopening setup reaches
the same retained successor, including an unknown outcome. At most eight reviewed
cancellations are accepted. Unknown, partial, pruned or contradictory evidence
never creates a new attempt. This is explicit reviewed recovery, not an automatic
retry or automatic renewal.

The final fixed helper checks the original owner, selected pins/name/chain and
private-key usability, then performs real HTTPS qualification on a temporary
loopback-only ephemeral port. Its dispatcher cannot initialize a product session
or execute an operation. The selected-CA result and ordinary local system-trust
observation must both pass. The listener is disposed before settings publication.
This proves key use by that elevated owner; Service/System and actual client
readiness still depend on their later startup and connection checks.

Settings updates merge under one protected exclusive writer handle. Guided setup
compares the original full settings fingerprint inside that update before
publication, preserving concurrent changes instead of overwriting them. A busy
writer or changed selection requires review; there is no automatic save retry.
The entire original workflow retains owner admission and active-work ownership
through page closure and natural helper completion.

Setup saves for the next deliberate restart. It does not install, enable, start
or restart a service, change startup entries/firewall/network scope, hot-transfer
the endpoint, or claim that Codex has reconnected. Automatic certificate renewal
is still outstanding. The settings-only action remains available and does not
silently create keys or install trust for an incomplete selection.

Focused development checks passed for the workflow's injected operation receipts,
in-memory certificate validation and injected TLS result, unshown page behavior,
owner/maintenance lifetime using isolated SQLite, and owned-file settings update
serialization. Two test-harness failures (fractional-second synthetic certificate
times and a console fixture waiting on an unrun WinForms context) were corrected;
their failed original runs remain failures. These checks perform no machine trust
installation, actual setup UAC, configured TLS listener or installed GUI action.
