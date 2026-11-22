# Windows device and network inventory

The `windows_device` and `windows_network` inventory leaves provide selected
local observations through the existing ordinary signed-in owner Host. They use
fixed Windows APIs. No shell command, hardware change, configuration write,
remote computer, alternate account or elevation fallback is part of these reads.
The separate explicit `windows_network resolve` action performs active DNS I/O
under the distinct contract described below.

Both families accept `list` and `read`, a closed version1 JSON `text` body,
optional RID and optional `detail`. Workspace, working directory, Root,
deferred execution and arbitrary provider selectors are refused. CLI bodies
use stdin. Reads do not create durable jobs or replay an unrelated operation
whose RID happens to match. Owner shutdown admission and cancellation still
apply, and observed owner loss discards the returned payload.

## Devices and registered driver metadata

```text
pao windows_device list --stdin
{"version":1,"scope":"present","limit":32}

pao windows_device read --stdin
{"version":1,"instanceId":"exact-instance-id-from-list"}
```

List scope is `present` by default, or explicitly `all` to include registered
nonpresent devices. Limit is1–64, default32. Continue with the returned exact
`afterInstanceId`. Each request opens a fresh local device information set and
seeks that anchor; it does not retain a hidden inventory session. An absent
anchor fails its precondition. Topology changes can invalidate the anchor or
cause changes between pages; the response is a live observation, not a global
snapshot or a stable ordinal directory.

The selected fields cover the device instance and class, display metadata,
manufacturer, service, Windows status/problem information and associated
registered driver INF/version/date/provider metadata. Property reads use
fixed typed keys and bounded buffers through
[SetupDiGetDevicePropertyW](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdevicepropertyw).
Missing, unsupported, oversized,
inaccessible or changed properties retain item-level visibility issues.
Detected removal or identity change does not produce a mixed metadata item.

Device instance IDs are opaque identifiers, not filesystem paths. The
per-call enumeration ceiling and total page-byte budget are explicit bounds;
a byte-limited page can return fewer items than its requested limit and still
have a continuation. An unidentifiable continuation boundary is reported,
not guessed. Requests never enable, disable, eject, install or update a device,
activate arbitrary COM objects, execute an INF or inspect a driver binary.

Driver fields describe the package associated with a device registration.
They are not a complete inventory of staged driver packages or non-PnP kernel
drivers, and do not prove a binary is loaded, signed, trusted or healthy.
General driver administration and selected COM interfaces remain separate
capabilities.

## Adapters, routes and connections

```text
pao windows_network list --stdin
{"version":1,"view":"adapters","limit":32}

pao windows_network read --stdin
{"version":1,"view":"adapters","key":"exact-row-key-from-list"}
```

Select exactly `adapters`, `routes` or `connections`. List limit is1–64,
default32. Continue using returned `afterKey`; `read` reobserves the selected
row key. Keys identify normalized observed coordinates. They are not snapshot
generations, account permissions, process-generation identities or proof that
a previously observed connection still exists. Each request reobserves local
state, so changes can remove rows or change a later page.

Identical rows sharing coordinates are represented once with their occurrence
count. If metadata within a coordinate group conflicts, only fields common to
every member are returned, with an explicit partial issue. A first arbitrary
member is not used to claim conflicting facts for the whole group.

- Adapters include selected interface metadata, unicast addresses/prefixes,
  configured DNS-server addresses and gateways. Reading those settings does
  not resolve a name, send a packet or prove reachability.
- Routes include actual IPv4 and IPv6 route-table observations. Routing
  metadata is distinct from a successful connectivity test.
- Connections contain IPv4/IPv6 TCP connection rows and explicitly labeled
  UDP endpoint rows with observed owner PIDs. UDP has no invented remote peer
  or connection state. A PID alone is not authority for a process action.

Caller-buffer APIs have bounded allocation and growth attempts. The standard
`GetIpForwardTable2` route API allocates its table inside Windows before the
caller can inspect the count. Its returned table must be released with
`FreeMibTable`, as specified by the
[Windows route-table API contract](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getipforwardtable2).
That API therefore has no caller-enforced
preallocation ceiling. The provider admits at most its fixed row limit,
copies/traverses only admitted rows, and releases the original OS allocation
on every path. It does not retry through a different route source. This
limitation remains in the coverage metadata and selected operation help;
the implementation does not claim a hard peak-memory cap for that OS call.

Source/table/row/property/page limits, missing source coverage and partial
results remain explicit. Ordinary replies contain requested rows and necessary
continuation/issues. `detail` adds observation time, source coverage and other
diagnostic metadata. Configured DNS, routes and local endpoints never imply
remote authentication, successful traffic or permission to change anything.
Network diagnostics, administration, shares/resources and account management
remain separate work.

## Explicit DNS resolution

```text
pao windows_network resolve --stdin
{"version":1,"name":"example.com","recordType":"A","timeoutMs":5000}
```

This action accepts one ASCII DNS name and either `A` or `AAAA`. Names use bounded
DNS labels and are normalized to absolute names. IP literals, URLs, wildcards,
Unicode, unknown or duplicate fields are refused. `timeoutMs` is100–30000,
default5000. The envelope remains `op/text/rid/detail`; callers cannot select a
server, interface, resolver provider, flags, identity, elevation or workspace.

The ordinary owner issues one `DnsQueryEx` invocation using the configured
Windows resolver and fixed unicast wire-only/cache-bypass options. It does not
use the hosts file, local-name fallback or application-generated follow-up
queries. Windows may perform its own resolver behavior; API entry does not prove
a packet count, reachability, remote authentication or general network health.
Returned address, family and TTL observations concern only the selected record
type. Record traversal and the complete result have explicit bounds. Windows
allocates its record list before admission, so no caller-enforced native
preallocation ceiling is claimed.

At most two original queries are admitted by the provider; additional work is
refused as busy. A deadline or cancellation requests `DnsCancelQuery`, which
does not wait for completion. The original callback, buffers, admission slot
and live Engine effect stay owned until the query and any entered cancellation
call settle. Thus the timeout bounds when cancellation is requested, not when
Windows must return. There is no detached query, automatic replay or fallback.
Owner loss discards the payload after cleanup; shutdown joins the actual work.
[Microsoft query contract](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnsqueryex),
[cancellation contract](https://learn.microsoft.com/en-us/windows/win32/api/windns/nf-windns-dnscancelquery).

Resolution receipts are ephemeral and do not create durable job rows or reuse
an unrelated RID. Names and answers are not retained as job request bodies.
This capability does not change adapter, DNS-server, route or firewall settings.
DNS configuration administration and other active diagnostics remain separate
requirements.

## Qualification

These additions are source work ahead of the prepared signed06.34 candidate.
Injected native/ABI tests, owned-SQLite routing tests, actual local read-only
Windows API checks, installed execution and visible UI are separate evidence
tiers. A catalog declaration, fake fixture or successful source build does
not establish installed or hardware/network integration acceptance.
