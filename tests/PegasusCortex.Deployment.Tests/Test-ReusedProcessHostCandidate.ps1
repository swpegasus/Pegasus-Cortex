[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectRoot,
    [Parameter(Mandatory)][string]$ProductPackageRoot,
    [Parameter(Mandatory)][string]$BaselinePackageRoot,
    [Parameter(Mandatory)][string]$ExpectedCandidateVersion,
    [Parameter(Mandatory)][string]$FixtureRoot,
    [string]$DotNetPath='C:\Program Files\dotnet\dotnet.exe'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $ProjectRoot 'tools\CortexBuildWorkspace.ps1')
. (Join-Path $ProjectRoot 'tools\CortexOwnerAdminPackageReuse.ps1')
$product=Assert-CortexBuildPath -Path $ProductPackageRoot -Boundary (Join-Path $ProjectRoot 'artifacts\release')
$fixture=Assert-CortexBuildPath -Path $FixtureRoot -Boundary (Join-Path $ProjectRoot 'artifacts\build')
if(Test-Path -LiteralPath $fixture){throw 'The integration fixture root is create-only.'}
$verify=& (Join-Path $ProjectRoot 'tools\signing\Test-PegasusSignedPackage.ps1') -PackageRoot $product -Role product -ReleaseVersion $ExpectedCandidateVersion
if(-not$verify.Valid){throw 'The candidate package was not verified.'}
$manifestPath=Join-Path $BaselinePackageRoot 'package-manifest.json'
if((Get-FileHash -LiteralPath $manifestPath).Hash.ToLowerInvariant()-cne'0a06fa71cc6ce736c55bd9c559c0ff489a17b7e29abfc9230c244e94ef1cfbce'){throw 'The original baseline changed.'}
$inventory=@(Get-CortexOwnerAdminReuseInventory -Manifest (Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json -Depth 40))
Assert-CortexOwnerAdminReusedRoles -ProductRoot $product -Inventory $inventory
[void][IO.Directory]::CreateDirectory($fixture)
$probeName='CortexReuseArgvProbe-'+[Guid]::NewGuid().ToString('N')
foreach($role in @('host','service'))
{
    $roleRoot=Join-Path $product ('bin\'+$role)
    $buildRoot=Join-Path $fixture $role
    $build=Invoke-CortexBuildProcess -Executable $DotNetPath -WorkingDirectory $PSScriptRoot -Arguments @('build',(Join-Path $PSScriptRoot 'ProcessHostReuseProbe\ProcessHostReuseProbe.csproj'),'--configuration','Release','--output',$buildRoot,('-p:EngineAssemblyPath='+(Join-Path $roleRoot 'Pegasus.AgentOps.Engine.dll')),('-p:AssemblyName='+$probeName)) -TimeoutSeconds 120
    if($build.ExitCode-ne 0){throw ('Private reuse driver build failed: '+(Get-CortexBuildProcessOutputTail ($build.StandardOutput+"`n"+$build.StandardError)))}
    $owned=@()
    foreach($suffix in @('.dll','.deps.json','.runtimeconfig.json'))
    {
        $src=Join-Path $buildRoot ($probeName+$suffix);$dst=Join-Path $roleRoot ($probeName+$suffix)
        if(Test-Path -LiteralPath $dst){throw 'Private driver collision; no candidate file may be overwritten.'}
        $input=[IO.File]::OpenRead($src);try{$output=[IO.FileStream]::new($dst,'CreateNew','Write','None');try{$input.CopyTo($output)}finally{$output.Dispose()}}finally{$input.Dispose()}
        $owned+=@{path=$dst;sha=(Get-FileHash -LiteralPath $dst).Hash.ToLowerInvariant()}
    }
    $helper=$inventory|Where-Object Path -eq ('bin/'+$role+'/Pegasus.AgentOps.ProcessHost.exe')
    $report=Join-Path $fixture ($role+'-result.json')
    $run=Invoke-CortexBuildProcess -Executable $DotNetPath -WorkingDirectory $fixture -Arguments @((Join-Path $roleRoot ($probeName+'.dll')),$fixture,$helper.Sha256,$ExpectedCandidateVersion,$report) -TimeoutSeconds 30
    if($run.ExitCode-ne 0){throw ('Reused ProcessHost candidate probe failed; private driver files retained: '+(Get-CortexBuildProcessOutputTail ($run.StandardOutput+"`n"+$run.StandardError)))}
    # Only known successful temporary fixture files are removed. Ambiguous or
    # failed work above remains for review; no role file is ever replaced.
    foreach($file in $owned)
    {if((Get-FileHash -LiteralPath $file.path).Hash.ToLowerInvariant()-cne$file.sha){throw 'Temporary driver changed; retained for review.'};[IO.File]::Delete($file.path)}
}
Assert-CortexOwnerAdminReusedRoles -ProductRoot $product -Inventory $inventory
& (Join-Path $ProjectRoot 'tools\signing\Test-PegasusSignedPackage.ps1') -PackageRoot $product -Role product -ReleaseVersion $ExpectedCandidateVersion
[ordered]@{passed=2;tier='actual ordinary-user execution; both reused candidate ProcessHost closures';fixture=$fixture;administratorExecution=$false;installedChanges=$false}|ConvertTo-Json -Compress
