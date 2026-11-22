# Pegasus Cortex Shared 421 alignment

> **HISTORICAL RELEASE RECORD — DO NOT USE FOR CURRENT BUILD, PACKAGING,
> INSTALLATION, OR ARCHITECTURE.** The dependency and target-framework facts
> below describe only the named July 2026 release. Use
> [the current installation/build guide](BUILD_FROM_SOURCE.md),
> [shared MCP setup](CORTEX_SHARED_MCP.md), and
> [local operational V2](LOCAL_OPERATIONAL_V2.md). Old deployment scripts named
> in this historical record are not the maintained release route.

Release `1.2026.07.11.05.04.A` aligns the passive Cortex packages to the exact,
dependency-free `Pegasus.Shared 1.20260711.421-a` baseline. `Pegasus.Cortex.Core`
remains `net10.0`; `Pegasus.Cortex.DefaultLibrary` and
`Pegasus.Cortex.ResourceEfficiency` remain portable `net8.0` libraries.

## Immutable dependency chain

- `Pegasus.Cortex.Core 1.20260711.504-a` -> exact
  `Pegasus.Shared [1.20260711.421-a]`.
- `Pegasus.Cortex.DefaultLibrary 1.20260711.504-a` -> exact
  `Pegasus.Shared [1.20260711.421-a]`.
- `Pegasus.Cortex.ResourceEfficiency 1.20260711.504-a` -> exact
  `Pegasus.Cortex.DefaultLibrary [1.20260711.504-a]` -> exact Shared baseline.

The packages and symbols are unsigned local verification artifacts. They are
not deployable applications, contain no service host or startup entrypoint, and
confer no installation, execution, trust, identity, or namespace authority.

## Interface mappings

`CortexSharedContractMappings` records the current outcome, knowledge revision,
knowledge read, bounded provider stream, and retention-page interfaces by stable
contract/version, source type, package, and verified repository-relative source
path. The mappings are descriptive metadata for a future authenticated host and
never instantiate an adapter.

Shared supplies `deployment-manifest.v1.schema.json` and its bounded evaluation
contracts. Cortex currently has no executable host, so this release intentionally
does not fabricate a product deployment manifest. A future host must create and
sign its own manifest only after its runtime artifacts, SBOM, dependency lock,
signatures, adapters, probes, and supported runtime identifiers exist.

## Curated knowledge boundary

The deterministic Default Library seed remains the exact 16-record release
`1.2026.07.11.03.20.A` with its exact 14-artifact allowlist. Package alignment
does not revise, regenerate, or broaden that knowledge. Coordination receipts,
external operational access, binaries, installers, caches, executable manifests,
and host-management material remain ineligible for seed evidence and package
projection.
