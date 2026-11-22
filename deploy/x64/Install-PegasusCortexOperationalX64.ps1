# Retired compatibility entry point. No package, profile or registration is created.
[CmdletBinding()]
param(
    [string]$PublishRoot = '',
    [string]$ReleaseVersion = '',
    [string]$InstallRoot = '',
    [string]$DataRoot = '',
    [string]$CodexConfig = '',
    [switch]$ReplaceRegistration
)

throw [InvalidOperationException]::new(
    'PEGASUS_CORTEX_LEGACY_X64_ROUTE_RETIRED: The historical per-user operational installer was removed. Use the normal unified Setup package. Source releases are built with tools/Build-PegasusCortexRelease.ps1. No inputs were inspected or changes made.')
