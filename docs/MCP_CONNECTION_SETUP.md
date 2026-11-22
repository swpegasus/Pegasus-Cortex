# Connect an MCP client to Cortex

Cortex 03.54 remembers approved **connections**. Each connection has a readable
name, a unique public ID and a private credential. Its approval survives client
disconnects and reconnects until you revoke it.

The connection ID selects the approval. It is not a password and cannot grant
access by itself. A chat ID or application label is descriptive; every chat
using the same credential shares that connection's permissions.

## 1. Prepare the local endpoint

Install the complete prepared release, then open Cortex. For a new installation,
use **Settings & support → MCP → Set up and save MCP** to configure HTTPS and
select Application ownership or your installed Cortex execution service. Restart
the selected owner after saving. The default address is
`https://localhost:47831/mcp/cortex`; keep your configured address if it differs.

An existing installation keeps its endpoint settings and certificates. Upgrading
from 22.34 still requires the connection enrollment below: saving the URL or
clicking Reconnect alone does not create an approval. The old four-character root
codes are no longer accepted.

## 2. Create and approve your connection

1. Open **Connections & access → Connections & Full control**.
2. Enter a name you will recognize later, such as **Office Codex**, and the
   application name **Codex**. An optional chat label helps identify its use.
3. Choose **Create local connection**. Cortex stores a random credential in the
   current Windows user's Credential Manager and retains a public enrollment
   request. Creation alone leaves access unapproved.
4. Select that request and choose **Approve ordinary access**. Accept Cortex's
   normal Windows elevation prompt and wait for confirmed approval.
5. Select the approved connection and choose **Copy Codex settings**.

Create the connection on the computer and Windows user account that will run the
client. Use a separate connection for another client or a different permission
boundary. Do not copy the author's connection ID, another user's vault, or a
private credential from a screenshot or example.

## 3. Save the generated Codex settings

Open Codex's configuration file, normally `%USERPROFILE%\.codex\config.toml`.
Replace the existing `[mcp_servers.Cortex]` table with the generated table;
do not add a duplicate. For a default installation it has this shape:

```toml
[mcp_servers.Cortex]
url = "https://localhost:47831/mcp/cortex"
enabled = true
http_headers_helper = '"C:\Program Files\Pegasus\Cortex\PegasusCortex.exe" mcp headers --connection <YOUR_CONNECTION_ID>'
```

**Replace `<YOUR_CONNECTION_ID>` with your own generated ID.** Prefer the copied
settings so the install path, ID and endpoint are exact. The helper is one command
string, not a JSON array. The configuration contains the public ID; the helper
supplies private authorization headers directly to the client from the Windows
vault. Do not run the header helper to paste or save its output.

The installed launcher can also print the public settings for a known ID:

```powershell
& 'C:\Program Files\Pegasus\Cortex\PegasusCortex.exe' mcp config --connection '<YOUR_CONNECTION_ID>'
```

Remove obsolete Cortex bearer/OAuth/header overrides when replacing its table;
do not mix credential sources. Leave unrelated MCP entries unchanged.

## 4. Reconnect and check

Save the configuration and reconnect the **Cortex** MCP server in Codex. If Codex
shows an **Authentication expired** banner, use its **Reconnect** button after
saving the generated helper setting. If your client has not reloaded its server
configuration, reload it or restart that client.

Ask the assistant to check Cortex status. A successful call should report the
installed version and `ready`; discovering a saved URL alone is not a connection
check. Subsequent reconnects reuse the same identity without another approval.

| What you see | What to check |
| --- | --- |
| Authentication expired / HTTP 401 | The helper is saved under the correct Cortex table, its ID matches an enabled approval, and the client runs as the Windows user who created the credential. |
| Certificate or hostname error | Complete the HTTPS certificate/trust setup for the configured endpoint; retain normal TLS validation. |
| Connection refused or Host unavailable | Start the deliberately selected owner and ordinary Cortex Host, then reconnect. |
| Ordinary tools work but protected work is refused | Full control is a separate approval and may be off or revoked. |

## 5. Optional Full control and revocation

Ordinary access is enough for normal continuity and user tools. For protected
operations, select the connection, choose **Grant Full control…**, read its red
warning and confirm the fixed Windows prompt. Full control is off by default.
Protected execution also requires the optional Cortex execution service.

**All chats sharing this credential share its Full control approval.** A chat
label does not isolate permissions. Create separate connections where required.

- **Revoke Full control** blocks new protected work while retaining ordinary access.
- **Revoke all access…** disables that connection, including open sessions and
  future reconnects. Reapproval is explicit.
- **Copy connection ID** gives you the full public ID for identifying and revoking
  the right entry. The readable name helps you recognize it.

Revocation prevents new dispatches; it does not undo an operation already started.
Cerebral's private application Full control grant is a separate approval.

## Other clients and another computer

A compatible Streamable HTTP MCP client needs the same approved identity and a
way to supply its authorization header privately. Codex's local
`http_headers_helper` setting is client-specific; it is not an OAuth login flow.
Never place a raw credential in public configuration, documentation or a chat.

For an allowed LAN/VPN client, create the local credential on that client and
transfer only its public enrollment request for approval on the server. Keep the
helper local and change the public URL to the configured server address. Network
inclusion, server trust and connection approval are separate requirements.
See [the complete shared MCP guide](https://github.com/swpegasus/Pegasus-Cortex/blob/main/docs/CORTEX_SHARED_MCP.md)
for public-request enrollment commands, endpoint ownership, TLS and network options.
