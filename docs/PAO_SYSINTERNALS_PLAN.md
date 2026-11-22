# Optional Sysinternals support for Cortex and Cerebral

## Product decision

Use locally supplied Microsoft Sysinternals utilities as optional diagnostics.
Do not redistribute their Windows binaries in Cortex, Cerebral, starter data,
or installer payloads. Microsoft's [licensing FAQ](https://learn.microsoft.com/en-us/sysinternals/license-faq)
permits use on supported machines but prohibits third-party distribution,
including free distribution. Users obtain their own copies from the
[official suite download page](https://learn.microsoft.com/en-us/sysinternals/downloads/sysinternals-suite).
Keep the supplied EULA with the files; an operator must accept the applicable
[terms](https://learn.microsoft.com/en-us/sysinternals/license-terms) before execution.
Neither extraction nor signature inspection records EULA acceptance.

## Shared local layout

The default external tools location is `%ProgramFiles%\Pegasus\Tools\Sysinternals`,
outside either product's immutable file inventory:

```text
Pegasus\Tools\
  Sysinternals\
    x64\       main suite including x86 and x64 executable variants
    arm64\     ARM64 suite, not added to PATH on x64 Windows
    machine-path.v1.json   ownership of exact PATH entries added locally
```

Add the shared `Tools` directory and the native architecture leaf to machine
PATH. PATH is not recursive. Use the `*64.exe` variants on x64 or `*64a.exe`
on ARM64; never select an executable by an unconstrained PATH lookup for PAO.
An optional feature's settings should reference this external root, not a
developer checkout, Downloads, or an application version directory.

`tools/dependencies/Register-LocalSysinternalsPath.ps1` validates the fixed
shared layout and five native Microsoft-signed executables without launching
them. Its default invocation is a non-mutating plan; `-Apply` requires normal
administrator elevation. It preserves existing machine PATH text/value kind,
adds only missing exact entries, rejects reparse paths, verifies the write,
and records only entries it added. Repeated application does not duplicate
entries or claim preexisting ones. Existing processes may require reopening.
Do not remove these external tools during ordinary Cortex/Cerebral uninstall.
Any later explicit PATH removal must remove only exact entries owned by this
record and preserve preexisting entries and other consumers' dependencies.

## Optional installer workflow

Offer one independently selected external-tools feature with two routes:

1. Open Microsoft's download page using the user's available browser, then
   accept an explicit local ZIP selection and extraction destination.
2. Offer a clearly selected programmatic Microsoft HTTPS download and safe
   extraction when that downloader has been implemented and verified.

Do not assume Chrome is installed or embed a browser for this operation. A
download does not require a browser engine. Keep Microsoft binaries out of
our distributable and do not copy them into product package manifests.
An unselected feature performs no download, probe, extraction, or PATH edit.

The downloader must use the explicitly selected architecture, bounded streaming
and disk budgets, cancellation, safe ZIP paths, no reparse traversal, and clear
partial-failure diagnostics. Inspect PE architecture, publisher signature and
version metadata before any executable probe. Preserve source/license details
and obtain explicit EULA acceptance; never append `-accepteula` silently.
Authenticode trust/revocation checks may contact Windows trust services.
Use native version metadata for normal inventory, not full-tree rehashing.

## Proposed PAO contract

The operations below are a design, not currently callable PAO operations.
Keep CLI, JSON, and MCP on the existing single operation table and dispatcher.
No new resident process, service, or Cortex runtime dependency is needed.

The first checkpoint should provide:

- `sysinternals_status`: required `name` from `handle`, `procdump`, `vmmap`,
  `procmon`, `sigcheck`; return bounded local availability, exact executable
  path, architecture, version, and publisher state without executing it.
  Distinguish `not_configured`, `unverified`, `eula_required`, `available`, and
  `unsupported_architecture`. Missing optional tools never break core work.
- `sysinternals_handles`: exact file path or PID plus process-start identity,
  result limit 1–128 (default 32), timeout 1–30 seconds (default 15), normal
  PAO output/receipt fields. Use the fixed read-only Handle argument set,
  bounded CSV parsing, exact post-filtering, and explicit incomplete/truncated
  status. No forced handle close, process termination, or automatic repair.

The current public registered-tool contract in
`src/Pegasus.AgentOps.Engine/ToolRecipeOperations.cs` and `AgentOpsEngine.cs`
is User-mode only and admission-pinned. It is not a generic System executor.
[Handle requires administrator rights](https://learn.microsoft.com/en-us/sysinternals/downloads/handle),
so privileged use needs a separately reviewed fixed broker adapter, with existing
authorization and exact target checks. Do not weaken admission or add arbitrary
elevated command strings. Preserve the common `ToolCatalog`/`UnifiedToolCatalog`
in `src/pao/Program.cs`; do not invent a second Cerebral-specific tool schema.

## Later diagnostic capabilities

[ProcDump](https://learn.microsoft.com/en-us/sysinternals/downloads/procdump)
should be an explicit bounded one-off capture: selected PID/start identity,
count/size/time budgets, output artifact location, and safe completion or
cancellation. Dumps may contain secrets; never automatically inject their
bodies into Cortex or send them to a model. No debugger registration, WER
submission, or indefinite watcher. Its PID-wide cancellation signal can affect
other ProcDump monitors, so cancellation must not disrupt another owner's capture.

[VMMap](https://learn.microsoft.com/en-us/sysinternals/downloads/vmmap) can
support targeted memory snapshots. [Process Monitor](https://learn.microsoft.com/en-us/sysinternals/downloads/procmon)
needs explicit filtered capture and disk/time limits, not continuous tracing.
[Sigcheck](https://learn.microsoft.com/en-us/sysinternals/downloads/sigcheck)
can inspect signatures and versions; VirusTotal queries/uploads remain off
unless separately authorized. Avoid redundant utilities when a fixed Windows
API already supplies the same evidence.

Return only needed compact results, not empty/null fields or repeated input.
Explicitly distinguish no matches from incomplete access. Keep large diagnostic
artifacts on disk with references and size/sensitivity metadata, not in ordinary
semantic records. Do not ingest logs, dumps, paths, or command output automatically.

## Acceptance and implementation order

First implement local status and its shared CLI/MCP specification. Then add
the fixed read-only Handle adapter and privileged broker admission. Capture
tools and a programmatic downloader follow independently.

Focused fixtures must cover absent files, wrong architecture/publisher, EULA
not accepted, PID reuse, reparse paths, invalid input, truncated output,
incomplete privilege, timeout and cancellation. Verify the same CLI/MCP
contract through the actual candidate executable. PATH acceptance must cover
idempotency and preservation of preexisting entries. Installer acceptance must
prove unselected features cause no unrelated work. Real privileged capture is
a separate explicitly authorized acceptance step, not a fixture pass.

## Local preparation on October 17 2026

Both user-supplied archives were extracted into the layout above, leaving the
ZIPs intact. The main archive contained 164 entries (468,112,920 expanded bytes)
and ARM64 contained 66 entries (112,293,702 bytes). The following executable
versions had valid Microsoft Authenticode signatures in both architecture
folders: Handle 5.0, ProcDump 12.02, VMMap 3.4, Procmon 4.12, Sigcheck 2.93.
Their PE headers matched x64/ARM64 respectively. These are file-validation
results, not tool execution, EULA acceptance, or PAO adapter acceptance.

The elevated PATH operation completed successfully. Readback proved the original
machine PATH text and registry value kind were preserved with exactly the two
planned entries appended, each appearing once. A repeat non-mutating plan found
zero additions; native command lookup resolved all five x64 tools to this shared
folder. The ARM64 directory is absent from machine PATH. No Cortex/Cerebral
binary, service, live tool definition, or installer artifact was changed.
