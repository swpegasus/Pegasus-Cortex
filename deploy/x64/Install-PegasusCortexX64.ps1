# Retired compatibility entry point. No profile, package or registration is created.
[CmdletBinding()]
param(
    [string]$ProfileId = 'standalone',
    [string]$RegistrationName = '',
    [string]$InstallBase = '',
    [string]$CodexExecutable = '',
    [switch]$ReplaceExistingRegistration,
    [switch]$NonInteractive
)

throw [InvalidOperationException]::new(
    'PEGASUS_CORTEX_LEGACY_X64_ROUTE_RETIRED: The standalone profile installer was removed. Use the normal unified Setup package. Source releases are built with tools/Build-PegasusCortexRelease.ps1; clients attach to the existing shared Pegasus Cortex MCP owner. No inputs were inspected or changes made.')
