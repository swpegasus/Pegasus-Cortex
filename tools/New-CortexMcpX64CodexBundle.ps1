# Retired compatibility entry point. No package or registration is created.
[CmdletBinding()]
param(
    [string]$ProductZip = '',
    [string]$RuntimeZip = '',
    [string]$OutputRoot = '',
    [switch]$InspectContract
)

throw [InvalidOperationException]::new(
    'PEGASUS_CORTEX_LEGACY_X64_ROUTE_RETIRED: The standalone Codex bundle route was removed. Use tools/New-PegasusCortexUnifiedProduct.ps1 and the normal unified Setup package; clients attach to the existing shared Pegasus Cortex MCP owner. No inputs were inspected or changes made.')
