param([Parameter(Mandatory = $true)][int] $Seconds)
$ErrorActionPreference = 'Stop'
Start-Sleep -Seconds $Seconds
Write-Output 'done'
