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
    'PEGASUS_CORTEX_LEGACY_X64_ROUTE_RETIRED: The standalone profile installer was removed. Use the normal unified Setup package. The existing Install-PegasusCortexX64.cmd compatibility entry targets Install-PegasusCortexV2X64.ps1; it does not recreate the retired connector. No inputs were inspected or changes made.')
