# Cerebral Guide: Multi-Homed Host Networking

Object: Knowledge.Cerebral.Guide.MultiHomedNetworking
Version: 1.0.0
Owner: Pegasus Cerebral
Triggers: a host has multiple Ethernet or wireless adapters, separate Internet and direct-LAN paths, or ambiguous routing
Required capabilities: read-only adapter/route inspection and explicit authorization for any network mutation
Rendered budget: 4096 UTF-8 bytes

## Model the paths first

Treat each adapter as a separate role: Internet/default route, dedicated peer LAN, management, storage, or standby. Record the interface identity, link state, address/prefix, gateway, route metric, DNS role, peer reachability, and expected source address. Similar display names are not stable identity.

## Safe workflow

1. Inspect addresses, routes, neighbor state, DNS configuration, and active connections before changing anything.
2. Ensure local subnets do not overlap unexpectedly and that static addresses are unique. A direct-LAN interface normally has its connected route and no default gateway.
3. Keep one intentional default route unless the design specifies policy routing or failover. Pin management aliases and bulk transfers to the dedicated address when deterministic path selection matters.
4. DNS resolves names; it is not required for a verified literal-address connection. Changing DNS does not repair a layer-2 link, conflicting address, missing connected route, or wrong source selection.
5. Change only the explicitly targeted adapter. Capture the baseline and prepare exact rollback before address, gateway, metric, DHCP, DNS, bridge, bond, VLAN, or Wi-Fi changes.
6. Validate the effective source/destination/interface for local peer traffic and Internet traffic separately. Test both directions when firewall or forwarding policy matters.
7. Reinspect after reconnect, reboot, dock/USB change, or interface rename. Hardware attachment can alter enumeration without changing the intended network contract.

## Boundaries

Do not bridge or bond an Internet adapter with a private direct link merely to “combine speed.” Multipath, failover, routing, and link aggregation require explicit topology support at both ends. Disabling Wi-Fi, changing firewall rules, enabling forwarding, remapping shares, or moving a static address between adapters are separate state changes.

Report observed reachability separately from configured intent. A successful ping does not prove the expected high-speed path, application listener, TLS identity, or authorization.

This guide provides no addresses and authorizes no adapter, route, DNS, firewall, or wireless change.
