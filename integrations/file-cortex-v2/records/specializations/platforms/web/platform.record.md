# Web Platform Tenets

These tenets specialize `SEC-*`, `API-*`, `NET-*`, `UI-*`, and `CORE-AUTH-006` for browser and HTTP boundaries.

## WEB-001 — Treat every request as untrusted

**Rule:** Validate route, query, headers, content type, encoding, body shape, size, and multiplicity before business logic.

**Practice:** Reject ambiguity, smuggling, unsupported media, and oversized input at the earliest owned boundary.

**Boundary:** Internal requests remain untrusted when a compromised or faulty peer could reach the service.

## WEB-002 — Authorize the requested resource server-side

**Rule:** Authentication and client-side visibility do not prove permission for an operation or object.

**Practice:** Validate current scope, tenant, ownership, and indirect references for every protected request.

**Boundary:** Public resources may omit identity while retaining rate and abuse controls.

## WEB-003 — Preserve HTTP semantics

**Rule:** Methods, status codes, caching, conditional requests, redirects, and idempotency should truthfully describe behavior.

**Practice:** Use safe retry only where operation semantics support it and return stable machine-readable errors.

**Boundary:** Compatibility constraints may require legacy behavior, but it should remain explicit and tested.

## WEB-004 — Encode output for its destination

**Rule:** HTML text, attributes, URLs, JavaScript, CSS, and headers require different safe construction rules.

**Practice:** Use framework-safe APIs, avoid raw markup injection, and apply content security policy as defense in depth.

**Boundary:** Sanitization is appropriate only for intentionally supported rich content and must match the renderer.

## WEB-005 — Protect ambient credentials from cross-site requests

**Rule:** Cookie-authenticated state changes require deliberate cross-site request protections.

**Practice:** Use appropriate same-site policy, anti-forgery mechanisms, origin checks, and safe method semantics.

**Boundary:** CORS and authentication alone do not prevent cross-site request forgery.

## WEB-006 — Treat CORS as browser sharing policy

**Rule:** Cross-origin configuration controls browser exposure, not service authorization.

**Practice:** Allow only required origins, methods, headers, and credential behavior and validate authorization independently.

**Boundary:** Non-browser clients are not constrained by CORS.

## WEB-007 — Protect session and token material

**Rule:** Session identifiers and tokens need secure transport, storage, expiry, rotation, revocation, and replay-aware handling.

**Practice:** Keep them out of URLs, browser-readable storage when avoidable, logs, analytics, and error pages.

**Boundary:** Token format does not remove the need for server-side policy and revocation decisions.

## WEB-008 — Trust proxy metadata narrowly

**Rule:** Forwarded host, scheme, address, and certificate metadata are trustworthy only from configured proxy hops.

**Practice:** Bound the trusted chain before using forwarded values for security, routing, rate limits, or generated URLs.

**Boundary:** Direct connections should ignore spoofable forwarding headers.

## WEB-009 — Contain uploads and generated downloads

**Rule:** Bound upload and download size and count, generate server-owned storage names, validate actual content, and keep untrusted files outside executable paths.

**Practice:** Define quarantine, scanning, retention, authorization, cleanup, safe `Content-Disposition` filenames, content type, cache policy, and prevention of internal-path disclosure.

**Boundary:** File extensions and client-declared media types are advisory, not validation.

## WEB-010 — Constrain server-side outbound requests

**Rule:** Validate destinations and redirects, restrict reachable networks, and bound time and response size for user-influenced outbound requests.

**Practice:** Resolve and revalidate addresses according to policy and prevent access to local metadata or administrative endpoints.

**Boundary:** Approved fixed integrations may use a narrow allowlist rather than general destination parsing.

## WEB-011 — Define canonical origin and secure transport

**Rule:** Establish the trusted scheme, host, and port used for identity, redirects, cookies, links, and origin checks, and require secure transport wherever confidentiality or authenticated identity matters.

**Practice:** Coordinate HTTPS redirects, HSTS, certificate identity, and externally visible origin with the trusted-proxy boundary in `WEB-008`.

**Boundary:** Development-only plaintext endpoints must be explicitly isolated and must not teach production clients to accept insecure downgrade.

## WEB-012 — Classify private-response caching

**Rule:** Authenticated, tenant-specific, personalized, or sensitive responses require deliberate cache-control and variation semantics.

**Practice:** Prevent shared intermediaries from serving one principal's content to another and include every representation-changing input in the cache key or variation contract.

**Boundary:** Public immutable content may be cached broadly when authorization, invalidation, and content identity support it.
