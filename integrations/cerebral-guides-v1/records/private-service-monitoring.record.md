# Cerebral Guide: Private Service Health and Monitoring

Object: Knowledge.Cerebral.Guide.PrivateServiceMonitoring
Version: 1.0.0
Owner: Pegasus Cerebral
Triggers: expose a private portal/API health endpoint, enroll uptime monitoring, or define down/recovery behavior
Required capabilities: deployed immutable service identity, verified private HTTPS endpoint, and an authorized monitor
Rendered budget: 4096 UTF-8 bytes

## Health contract

Define a small credential-free health endpoint only when it is safe to reveal. It should report API/control-plane availability without loading a model, starting expensive work, mutating state, or exposing secrets, paths, versions, queues, tenants, or diagnostic dumps. Distinguish process running, listener reachable, TLS valid, API available, runtime installed, hardware qualified, model loaded, and generation ready.

Use stable status codes and a bounded versioned response. A monitor may require an exact body/schema check, but it must tolerate only explicitly documented fields and limits. Health success is not authentication, authorization, inference, or artifact-quality evidence.

## Enrollment workflow

1. Wait until the immutable image/service and endpoint are actually deployed; never register a planned or preview URL as live.
2. Provide the exact HTTPS address/port/path, allowed monitor source addresses, expected status/body, timeout/interval, maintenance policy, and owner.
3. Give the monitor a product-scoped CA bundle or independently verified chain/fingerprint contract. Ensure SANs, issuer, validity, revocation, port, and source route match the real endpoint. Do not disable verification or add a plaintext fallback.
4. Probe from each intended network path. Verify no public route, wildcard listener, unintended IPv6 exposure, tunnel, or router mapping exists.
5. Emit bounded down and recovery state with debounce, last success/failure, and actionable cause. Planned maintenance should suppress escalation without falsifying service availability.
6. Revalidate after certificate rotation, image/service change, address change, restart, or monitor upgrade.

## Separation of concerns

Monitoring credentials, if any, are separate from portal user credentials and model/provisioning secrets. An uptime monitor observes; it must not restart services, clear queues, load models, accept licenses, alter trust, or perform recovery unless a separately authorized automation contract exists.

This guide authorizes no listener, CA/trust change, DNS record, enrollment, notification, service activation, or public exposure.
