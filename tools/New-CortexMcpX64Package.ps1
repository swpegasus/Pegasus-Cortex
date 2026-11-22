# Retired compatibility entry point. No source, package or output is inspected.
[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [string]$OutputRoot = '',
    [string]$DotNetPath = 'C:\Program Files\dotnet\dotnet.exe',
    [string]$PythonPath = 'python.exe',
    [switch]$InspectSourceSet
)

throw [InvalidOperationException]::new(
    'PEGASUS_CORTEX_LEGACY_X64_ROUTE_RETIRED: The framework-dependent DLL/profile package route was removed. Use tools/Build-PegasusCortexRelease.ps1 and the normal unified Setup package; clients attach to the existing shared Pegasus Cortex MCP owner. No inputs were inspected or changes made.')
