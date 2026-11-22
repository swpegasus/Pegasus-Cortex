# Cerebral Guide: TLS Certificates and Private Ingress

Object: Knowledge.Cerebral.Guide.TlsCertificatesIngress
Version: 1.0.0
Owner: Pegasus Cerebral
Triggers: expose a private HTTPS service, select or rotate certificates, configure ingress, or prepare a monitor
Required capabilities: exact endpoint inventory, certificate inspection, authorized ingress configuration, and an owned trust path
Rendered budget: 4096 UTF-8 bytes

## Certificate contract

1. Identify every intended client-visible hostname and IP before requesting a certificate. Put names in DNS SANs and literal addresses in IP SANs; a common name is not a substitute.
2. Verify leaf purpose, public-key algorithm, signature chain, issuer, serial, validity window, key usage, extended key usage, SANs, and independently obtained fingerprints.
3. Establish the complete trust and revocation model: CA distribution, intermediate chain, CRL or OCSP availability, offline/unknown policy, rotation overlap, revocation response, and owner. Encryption without peer-name validation is not authenticated TLS.
4. Keep the private key outside images, source, logs, prompts, model inputs, and ordinary artifacts. Bind it read-only to the smallest ingress identity with explicit ownership and rotation permissions.

## Private ingress

- Bind only verified private addresses and intended ports. Do not use wildcard IPv4/IPv6, public tunnels, automatic router publication, or an extra plaintext listener by default.
- Keep TLS termination and application authorization separate. A valid certificate does not grant API, operator, tenant, or data access.
- Trust proxy headers only from the exact local ingress. Enforce source/interface policy in the real host/container forwarding path, including IPv6.
- Serve a credential-free minimal health endpoint only when policy permits; it must not load a model, mutate state, or reveal protected details.
- Record the exact certificate fingerprint, chain, endpoint, image/service revision, and effective listener. Recheck them after rotation or restart.

## Client and monitor trust

Prefer a product-scoped CA bundle or explicitly pinned trust contract over a machine-global trust-store change. A monitor must probe the actual service port, validate the same endpoint identity, and follow the selected revocation policy. If no existing CA/revocation facility satisfies the contract, stop for a concrete PKI/policy decision rather than weakening verification.

This guide authorizes no certificate issuance, CA creation, trust-store mutation, listener, DNS record, or service deployment.
