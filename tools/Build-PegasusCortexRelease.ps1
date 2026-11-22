[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RootThumbprint,
    [Parameter(Mandatory)][string]$PublisherThumbprint,
    [string]$Provider = 'Microsoft Software Key Storage Provider',
    [string]$DotNetPath = 'C:\Program Files\dotnet\dotnet.exe'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'CortexBuildWorkspace.ps1')
$version = Get-Content -LiteralPath (Join-Path $root 'VERSION.json') -Raw | ConvertFrom-Json
$buildVersion = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Version.props') -Raw)).Project.PropertyGroup
if ($buildVersion.PegasusReleaseVersion -cne $version.releaseVersion -or
    $buildVersion.PegasusNuGetVersion -cne $version.nuGetVersion -or
    $buildVersion.PegasusAssemblyVersion -cne $version.assemblyVersion -or
    $buildVersion.PegasusFileVersion -cne $version.fileVersion) {
    throw 'VERSION.json and Directory.Version.props must agree before building or replacing release artifacts.'
}
$signing = @{ RootThumbprint=$RootThumbprint; PublisherThumbprint=$PublisherThumbprint; Provider=$Provider }
Push-Location $root
try {
    Write-Host 'Release stage: focused Setup build and fixtures'
    # PAO's isolated PowerShell host does not predefine LASTEXITCODE before the
    # first native process. Initialize it so strict mode observes the actual
    # dotnet result rather than failing after a successful first invocation.
    $global:LASTEXITCODE = 0
    # A fresh checkout has no prepared test assets. Own this Windows-x64 restore
    # before the no-restore fixture, just as the product publisher owns its roles.
    $setupRestore = Invoke-CortexBuildProcess -Executable $DotNetPath -Arguments @(
        'restore', 'tests\PegasusCortex.Setup.Tests\PegasusCortex.Setup.Tests.csproj',
        '--runtime', 'win-x64', '--configfile', (Join-Path $root 'NuGet.Config'),
        '--force-evaluate', '--disable-parallel', '-p:Configuration=Release', '-p:NuGetAudit=false') `
        -WorkingDirectory $root -TimeoutSeconds 1800
    if ($setupRestore.ExitCode -ne 0) {
        throw ('Focused setup restore failed. ' +
            (Get-CortexBuildProcessOutputTail -Text ($setupRestore.StandardOutput + "`n" + $setupRestore.StandardError)))
    }
    $focusedSetup = Invoke-CortexBuildProcess -Executable $DotNetPath -Arguments @(
        'run', '--project', 'tests\PegasusCortex.Setup.Tests\PegasusCortex.Setup.Tests.csproj',
        '-c', 'Release', '--runtime', 'win-x64', '--no-restore', '--disable-build-servers', '-p:TreatWarningsAsErrors=true') `
        -WorkingDirectory $root -TimeoutSeconds 1800
    if ($focusedSetup.ExitCode -ne 0) {
        $details = Get-CortexBuildProcessOutputTail -Text ($focusedSetup.StandardOutput + "`n" + $focusedSetup.StandardError)
        if (-not [string]::IsNullOrWhiteSpace($details)) { $details = "`n$details" }
        throw "Focused setup verification failed with code $($focusedSetup.ExitCode).$details"
    }
    & (Join-Path $PSScriptRoot 'New-PegasusCortexUnifiedProduct.ps1') @signing -ProjectRoot $root -DotNetPath $DotNetPath
    & (Join-Path $PSScriptRoot 'New-PegasusCortexNsisInstaller.ps1') @signing -ProjectRoot $root
    & (Join-Path $root 'tests\PegasusCortex.Deployment.Tests\Test-WindowsInstallerTools.ps1')
    & (Join-Path $root 'tests\PegasusCortex.Deployment.Tests\Test-DeploymentPlan.ps1')
    & (Join-Path $root 'tests\PegasusCortex.Deployment.Tests\Test-ProdAlphaSigning.ps1')
    [pscustomobject]@{
        Installer = Join-Path $root 'artifacts\release\installer\PegasusCortexInstaller.exe'
        Product = Join-Path $root 'artifacts\release\unified'
        Scratch = Join-Path $root 'artifacts\build\current'
        Installed = $false
    }
} catch {
    Write-Host ('Release failure: ' + $_.Exception.GetType().Name + '; ' + $_.Exception.Message)
    Write-Host ('Release location: ' + $_.InvocationInfo.PositionMessage)
    throw
} finally { Pop-Location }
