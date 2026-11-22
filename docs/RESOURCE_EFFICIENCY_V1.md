# Pegasus Cortex resource efficiency v1

Resource efficiency is a separately owned policy package around the stable
Default Library contracts. It does not change knowledge record, seed taxonomy,
authorization, or non-executable content semantics.

## Default limits

| Resource | Default bound |
|---|---:|
| Import body | 1 MiB |
| Records per bundle | 500 |
| Evidence references per record | 16 |
| Search page | 20 records |
| Ranked result window | 50 records |
| Ranking candidates | 500 records |
| Concurrent queries / queued queries | 4 / 16 |
| Concurrent imports / queued imports | 2 / 4 |
| Cache entries / estimated bytes | 128 / 16 MiB |
| Retention batch | 100 records |
| Buffered metric events | 1,024 |

All options are validated and capped. Providers may select smaller values but
cannot silently exceed the contract.

## Ownership

- `ResourceEfficientKnowledgeImporter` owns its concurrency gate and disposes an
  input stream unless the caller explicitly retains ownership. Seekable or
  declared oversize inputs are rejected before parsing or reading. Other inputs
  are incrementally parsed through a stream that permits at most one boundary
  probe byte beyond the configured cap.
- `ResourceEfficientKnowledgeQueryService` owns its gate and async enumerator.
  The repository receives server-derived namespace and sensitivity scope before
  it streams candidates. The service enforces the cap again, then uses the stable
  Default Library ranking and bounded continuation window.
- `BoundedResourceCache` owns every value passed to `SetAsync`. Replaced,
  rejected, evicted, and terminal values are deterministically disposed.
- Repository/provider implementations are caller-owned. Their interfaces expose
  bounded streaming or page operations, so adapters can own connections and
  transactions according to their native lifecycle.
- Retention uses ordered delta pages and continuation identifiers. It does not
  rebuild or materialize the complete record corpus.

No timers, background loops, or idle polling are created by this package.

## Backpressure and cancellation

Concurrency gates accept a fixed number of active and queued operations. A full
queue returns a retryable diagnostic without starting repository work. Caller
cancellation propagates through gate waits, streaming enumeration, JSON parsing,
repository operations, and ranking. Terminal paths release leases and dispose
owned streams/enumerators.

## Metrics

The package emits content-safe numeric events only:

- memory samples;
- import/query latency;
- queue depth;
- cache entries, estimated bytes, and evictions;
- cleanup failures.

Metrics contain operation and outcome labels but no query text, record content,
identity, evidence payload, or secret material. The provided diagnostic buffer
is fixed-size and evicts its oldest events.

## Verification boundary

Observable tests cover pre-read oversize rejection, query backpressure,
cancellation cleanup, bounded paging with authorization pushed to the provider,
LRU eviction and value disposal, atomic failed-import rollback, terminal resource
release, bounded retention, and metric eviction. No live database, API, MCP,
identity, certificate, deployment, or service behavior is claimed.

External test access may exist, but it is outside Cortex scope, grants no
authority, and requires separate approval.
