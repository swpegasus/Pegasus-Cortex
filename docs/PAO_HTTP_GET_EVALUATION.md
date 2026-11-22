# Fixed PAO HTTP GET: contract evaluation

Status: design proposal, 2026-10-22. **Not implemented, advertised, packaged, or
installed.** `http_get` below is a proposed selector, not a callable operation.
The current catalog exposes no fixed HTTP/page-request operation; `net_list`
only inspects local listeners. Existing registered executable availability does
not create that contract.

## Ownership and recommended slice

Implement one bounded `http_get` operation in the existing PAO engine,
catalog/help, and receipt pipeline if this capability is taken forward. Use
the platform .NET HTTP stack. No HTTP executable enrollment, shell command,
new adapter family, service, listener, or third-party production dependency is
needed for that design.

Cerebral retains profile/capability grants, allowed-domain policy, request/effect
ownership, UI, and its current `browser.request` provider. A future PAO operation
does not inherit or override a Cerebral grant. The Browser owner must explicitly
map its policy and accept the narrower network contract before adoption. Keep
the current route explicitly unsupported for PAO until that gate passes.

## Proposed public input

| Field | Proposed contract |
| --- | --- |
| `op` | Literal `http_get`; fixed GET only. |
| `mode` | `request` (default) or `receipt`. Receipt mode is an exact read-only lookup by the existing HTTP RID/fingerprint; it must never admit a job, resolve DNS or send a request, including when absent. |
| `url` | One absolute HTTP(S) URL, at most 4096 characters. Reject userinfo, fragments, control characters, unsupported schemes and nonstandard ports. Do not accept headers, cookies, request bodies or credentials. |
| `origins` | Required bounded set of 1–8 exact normalized HTTP(S) origins. Initial and redirected origins must belong to it. This only restricts a request; it never grants authority. |
| `rid` | Required stable request identity, bound to exact URL, origin set and limits. Reuse returns a known receipt; it does not automatically resend an uncertain request. |
| `tmo` | Execution deadline 1–30 seconds, default 15, shared by DNS, connection, redirects, headers and every body read. Scheduler queue wait remains separate. |
| `max` | Maximum received body bytes 1024–262144, default 65536. Enforce Content-Length when present and streaming reads in every case. |
| `text_max` | Maximum returned UTF-8 text bytes 256–16384, default 12000. Clip only on Unicode scalar boundaries and report text truncation separately from a body-limit failure. |
| `output` | Existing PAO presentation selector; no new receipt family. |

The first slice is synchronous. `wait` is not advertised or accepted: reject
any supplied value and explicitly exclude HTTP from the engine's default
deferred-completion predicate. Receipt mode needs the same exact request fields
for fingerprint comparison; it reports absent/conflict without dispatch. This
HTTP-specific lookup is proposed behavior, not the missing generic RID lookup.

Start with public destinations only. Deny loopback, private, link-local,
unspecified, multicast and other special-use addresses, including mapped IPv6
forms; an origin entry cannot exempt them. Private targets and authenticated
requests are outside this first slice. This is deliberately narrower than the
current Browser provider's explicit numeric local-address grants.

## Transport and effects

Resolve once in the connection callback, bound the answer set, reject any denied
answer, and connect to an admitted numeric address without another DNS lookup.
Keep the original host for TLS/SNI and normal certificate validation. Disable
ambient proxy use, cookies, credentials, authentication, and automatic
decompression. Do not weaken certificate or identity checks.

Disable automatic redirects. Follow at most five manually; revalidate the URL,
origin, DNS answers and address on every
hop, and reject HTTPS downgrade. No caller flag may bypass these checks.
This follows the [OWASP SSRF guidance](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html).

Use response-header streaming, a 16 KiB response-header budget, and one linked
execution deadline through body completion. `ResponseHeadersRead` does not make
`HttpClient.Timeout` cover later body reads; those reads require the same bounded
token explicitly. See [Microsoft's completion contract](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0)
and [handler controls](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler?view=net-10.0).

Allow only bounded text/HTML/JSON/XML representations with explicit supported
decoding. Reject attachments, unsupported encodings and compressed bodies. A
received over-limit body is not a complete successful page. Authentication
challenges return a bounded refusal; never retry with ambient credentials.

Network GET is an observable external effect. Admit it through existing durable
job/idempotency machinery, not the read-only queue bypass. Initially retain the
existing conservative reservation policy; do not change scheduler concurrency
to add this operation. Queued rejection/cancellation is NotDispatched only when
no exchange started. A sent request with no complete result is Unknown or
Partial as supported by the failure schema. Preserve that receipt and prohibit
automatic resubmission, even with the same RID. Recovery never replays an
omitted ephemeral request.

This requires an admission change: current ordinary job replay is RID-based,
without comparing the request fingerprint. For HTTP, require the existing RID's
operation and a versioned canonical fingerprint to match before returning any
known receipt. Include the exact input URL, normalized origin set, limits and
policy version. Reject divergent and cross-operation RID collisions; an old
HTTP receipt without a verifiable fingerprint requires reconciliation. Hashing
for privacy alone does not provide this identity check. Preserve compatibility
for unrelated operations while making collisions with HTTP fail closed.

Synchronous caller cancellation applies to the active execution token; a sent
request that cannot complete keeps uncertain/partial evidence. The initial
contract exposes no accepted deferred HTTP job or deferred body retrieval.
Adding deferred mode later requires an explicit compatible result-delivery
contract and tests, rather than inheriting it accidentally from scheduling.

Distinguish operation replay from transport behavior. Add no operation-level
retry loop. DNS address connection attempts, each explicit redirect, and each
HTTP send invocation must be bounded and reported where observed. The platform
handler can reconnect or internally retry; without measured runtime evidence,
report its wire retry count as unknown, never as one. A consumer requiring an
at-most-once wire exchange must not adopt this route until that stronger
transport guarantee is implemented and qualified. Current no-replay receipts
only prevent another operation dispatch; they do not attest wire-attempt count.

Return known HTTP status, bounded untrusted source text, actual byte counts,
encoding, redirect count and explicit truncation/disposition. A completed HTTP
exchange can have a non-success HTTP status; do not confuse transport completion
with a successful page. No JavaScript, browser state or rendering is implied.

URL queries/paths and response text can be sensitive. The proposed first slice
uses metadata-only durable receipts. Persist the request fingerprint and safe
result metadata, with no raw URL, origin set, body or text in request JSON,
terminal `Data`, errors or logs. Use the omitted-body/recovery boundary for the
raw request. Current PAO persists complete terminal receipts, so this requires
an explicit ephemeral result projection before exposure; simply putting text
in ordinary `Data` would violate this design.

Keep returned text in an owner-bound in-memory result cache keyed by exact JID
and fingerprint, at most 64 bodies/1 MiB UTF-8 total, with a five-minute maximum
lifetime measured monotonically. Capacity may evict a body earlier; it must not
evict or clear the durable request outcome. Synchronous results and an exact
`mode=receipt` read can include text only while present. After expiry,
eviction, host loss or loss of the cached body, return known metadata with
`bodyAvailable=false`; never refetch to reconstruct it. `job_stat`/`job_log`
remain metadata-only. Dynamic body availability must not be stored as a
permanent `true` flag in a durable receipt. Cache insertion, result projection,
failure cleanup and owner isolation need explicit implementation/tests.

A lost client response can leave the server's body cache intact. Authorized
reconciliation uses `mode=receipt`, which returns cached text if still present,
or truthful metadata/body absence without refetching. The server must not claim
it observed response delivery, and an absent RID in receipt mode is never
permission to switch to request mode automatically.

Never write page bodies into semantic Cortex knowledge automatically. These
privacy, identity and ephemeral-result requirements are implementation gates;
they are not capabilities of the current ledger or a settled shipped contract.

## Bounded qualification required before exposure

1. Schema/help tests: exact fields, unknown-field rejection, limits, GET-only,
   no root/System, credentials, headers, shell inputs, deferred completion or
   unsupported selectors.
2. Deterministic URL/origin/address tests: alternate IP forms, mixed allowed and
   denied DNS results, mapped addresses, IDNs, nonstandard ports, rebinding and
   pinning. Verify TLS host identity is retained.
3. Deterministic redirect tests: each hop rechecked, outside-origin/private hop,
   downgrade, cycles and the five-hop ceiling; no hidden automatic follow.
4. Bounded stream tests: declared/undeclared oversized bodies, compression,
   unsupported media/charset, invalid text, scalar-safe clipping, and a body
   that stalls after prompt headers. Verify deadline and cancellation release
   every stream/socket and reservation.
5. Receipt tests: pre-dispatch vs sent uncertainty, identical-RID retained
   outcome, divergent/cross-operation input conflict, no operation retry,
   transport repeat-attempt evidence, restart recovery, body-cache expiry/
   eviction/lost results, exhausted queue/result retention and privacy across
   structured/agent/log projections. GET idempotence alone is not evidence that
   resending is safe. Internal transport attempts must never be reported as
   exactly once unless the qualified runtime supplies that evidence.
6. Isolated local HTTP/TLS executable integration only after deterministic
   checks. Test injection may provide a loopback endpoint without adding a
   production private-address bypass. Internet/provider and Browser end-to-end
   acceptance remain separate, authorized evidence levels.

None of these proposed HTTP tests has been implemented or executed by this
evaluation. The completed checks in `CORTEX_CEREBRAL_GUIDANCE_CURRENT.md` cover
guidance composition and the existing embedded-resource contract only.
