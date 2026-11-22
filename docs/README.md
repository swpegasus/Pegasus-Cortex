# Cortex documentation

Start with the maintained guides below. A document's historical version, hash,
process identity or test result does not identify the currently installed build.
Use the installed About window, the actual connection and the signed release
inventory for that build's identity and evidence.

For an introduction before the contracts, open [the Cortex handbook](handbook/README.md).
It explains the file-based origins, context and geometric retrieval, the tool
hierarchy, and a local-first Cerebral/Cortex case study with smaller-model and
data-boundary tradeoffs. The [visual product guide](https://cerebral.shaynewinter.com/cortex/)
offers the same starting points as an interactive site.

## Installation, connection and everyday use

- [Install or build Cortex](BUILD_FROM_SOURCE.md): the complete Windows x64
  installer archive, source prerequisites and admitted dependencies.
- [Shared MCP connection](CORTEX_SHARED_MCP.md): first-time HTTPS setup,
  loopback defaults, Application or existing Service ownership, certificates and
  restart behavior. Per-chat direct STDIO startup is retired.
- [MCP client setup and upgrades](MCP_CONNECTION_SETUP.md): create your own
  connection ID, approve access, save the Codex helper and reconnect. Required
  when upgrading an older URL-only connection to 03.54.
- [Local continuity and knowledge](LOCAL_OPERATIONAL_V2.md): task/knowledge
  contracts, exact reads, starter guidance and explicit offline bindings.
- [Local terminals](PAO_LOCAL_TERMINALS.md): PowerShell 7 prerequisite,
  discovery and reconnect, bounded input/output and owner-Host lifetime limits.
- [Storage and lifecycle protection](CORTEX_PAYLOAD_STORAGE.md): optional cold
  payloads, protection, rotation, rehydration and their source/verification limits.
- [Statistics](CORTEX_UTILIZATION.md): recorded operations, byte/time units and
  the limits of aggregate observations.
- [Branding artwork and kit](BRANDING_ARTWORK.md): source artwork, GUI watermark,
  icon families and the maintained kit exporter.
- [Repository and licensing](repository-setup.md) and the [operative license](../LICENSE).

The installed MCP catalog and operation-specific help describe the available
contract. Examples use placeholders, not reusable live window/process identities,
credentials, or permission to perform the illustrated action.

## Local tools

The `PAO_*.md` guides cover specific typed capabilities and their execution
boundaries. Common entry points are [failure and receipt handling](PAO_FAILURE_CONTRACT.md),
[jobs and scheduling](PAO_BOUNDED_SCHEDULING.md),
[applications](PAO_APPLICATION_LIFECYCLE.md),
[Windows services](PAO_WINDOWS_SERVICES.md), [tasks](PAO_WINDOWS_TASKS.md),
[certificates](PAO_CERTIFICATES.md), and [UI automation](execution/UI_AUTOMATION.md).
[UI examples](../examples/ui-automation/README.md) illustrate the current fixed
handle-bound contract. A visible tray window is not required for ordinary
owner-Host operations. Machine controls retain their explicit service or UAC
admission; installing Cortex does not automatically enable those operations.

[Assets](ASSETS_V2.md) and [optional visual libraries](VISUAL_LIBRARIES.md)
describe separately licensed passive data. Importing artwork, rendering it and
exporting a complete redistribution bundle are different capabilities.

## Reference integrations and historical material

- [Recovery history](history/RECOVERY_HISTORY.md): the preserved dated entry
  formerly in `START_HERE.md`; current recovery starts at the repository root.
- [File-provider reference pack](../integrations/file-cortex-v2/README.md): an
  isolated portable reference implementation, not the installed live database.
- [Heartbeat experiment](../integrations/heartbeat-v2/README.md): disabled,
  standalone-local research, not a product service or startup task.
- `CHECKPOINT_*` documents, `CORTEX_R2_*` diagnostics, named acceptance handoffs
  and dated qualification sections preserve evidence from the stated work.
  Their old roots, versions and commands are not current setup instructions.
- [Cortex–Cerebral checkpoint](CORTEX_CEREBRAL_GUIDANCE_CURRENT.md), despite its
  historical filename, explicitly records an October qualification checkpoint.
  Use the shared MCP guide above for the current connection route.
- `*_PLAN.md`, the [HTTP GET evaluation](PAO_HTTP_GET_EVALUATION.md), and
  earlier service/default-library/sync design records describe their own
  proposed or superseded scope. A proposal is not an advertised operation.
- [Historical Linux ARM64 plan](CORTEX_MCP_ARM64_INSTALLATION_PLAN.md): deferred
  standalone PostgreSQL/SSH-STDIO design, with the old runnable procedure retired.
  It does not qualify an ARM64 product or the current Windows connection.

Historical files remain at stable paths so existing citations continue to work.
Do not use an old direct adapter, installer script, private runtime or database
path as a fallback for the maintained product. The starter records beneath
`integrations/` retain their content and attribution independently of this index.
