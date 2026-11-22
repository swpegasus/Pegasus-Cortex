# Pegasus Cortex V2 provider benchmark

This tool performs a reproducible, local-only comparison of the isolated File
V2 reference provider and the SQLite V2 operational provider. It creates new
state only under the explicitly supplied `artifacts/validation` output root.
It never opens an installed or active Cortex database.

The A/B corpus is the current portable generalized programming-tenet pack.
The benchmark admits compatible pack growth while requiring unique record IDs
and four stable semantic relevance anchors.
The workload measures first reopen/status or quick-check, warm startup/search/
typed-read/metadata-write p50 and p95, serialized response bytes, and retained
storage bytes. A separate SQLite database receives one content revision plus
10,000 metadata-only revisions; public storage counters report revision/blob,
search-chunk/head, and FTS/head amplification.

The run also fails closed on three deterministic relevance anchors for both
providers: persistence/migrations must appear in the bounded startup context,
workspace path safety must lead its search, and interfaces/contracts/
serialization must rank ahead of portability/time/encoding for the
serialization query. This keeps the gate meaningful as compatible packs add
more specific failures and workarounds above a general migration tenet.

The operating-system filesystem cache is not flushed. “First reopen” therefore
means a fresh provider object after seed/dispose, not a laboratory cold-disk
measurement. Run other applications idle and compare repeated reports rather
than treating one sub-millisecond observation as universal hardware truth.

From the repository root, with the SDK selected by `global.json`:

```powershell
$sdk = 'C:\Program Files\dotnet\dotnet.exe'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$output = Join-Path (Resolve-Path .\artifacts\validation) "cortex-v2-provider-benchmark-$stamp"
& $sdk run --project .\tools\PegasusCortex.V2.Benchmarks -- `
  --pack (Resolve-Path .\integrations\file-cortex-v2) `
  --output $output `
  --iterations 100 `
  --writes 50 `
  --scale-revisions 10000
```

The tool writes `benchmark.json`, `benchmark.md`, and disposable provider state
beneath `state/` in that new output directory.
