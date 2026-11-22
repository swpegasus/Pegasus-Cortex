param(
    [Parameter(Mandatory = $true)]
    [string] $LinkPath,

    [Parameter(Mandatory = $true)]
    [string] $TargetPath
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Junction -Path $LinkPath -Target $TargetPath | Out-Null
