[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageRoot,

    [Parameter(Mandatory = $true)]
    [string] $ReportPath,

    [string] $SetupPath,
    [string] $InstallRoot,
    [string] $DataRoot,
    [string] $CodexHome,
    [switch] $CodexRegister,
    [switch] $CodexGuidance
)

$ErrorActionPreference = 'Stop'

$package = [System.IO.Path]::GetFullPath($PackageRoot)
$report = [System.IO.Path]::GetFullPath($ReportPath)
$setup = if ([string]::IsNullOrWhiteSpace($SetupPath)) {
    [System.IO.Path]::Combine($package, 'installer', 'PegasusCortex.Setup.exe')
} else {
    [System.IO.Path]::GetFullPath($SetupPath)
}
if (-not [System.IO.File]::Exists($setup)) {
    throw "The admitted package does not contain installer/PegasusCortex.Setup.exe."
}

$reportParent = [System.IO.Path]::GetDirectoryName($report)
if ([string]::IsNullOrWhiteSpace($reportParent)) {
    throw 'The diagnostic report path has no parent directory.'
}
[System.IO.Directory]::CreateDirectory($reportParent) | Out-Null

$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = $setup
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add('install-package')
$start.ArgumentList.Add($package)
foreach ($option in @(@('--install-root', $InstallRoot), @('--data-root', $DataRoot), @('--codex-home', $CodexHome))) {
    if (-not [string]::IsNullOrWhiteSpace($option[1])) {
        $start.ArgumentList.Add($option[0])
        $start.ArgumentList.Add($option[1])
    }
}
$start.ArgumentList.Add('--codex-register')
$start.ArgumentList.Add($CodexRegister.IsPresent.ToString().ToLowerInvariant())
$start.ArgumentList.Add('--codex-guidance')
$start.ArgumentList.Add($CodexGuidance.IsPresent.ToString().ToLowerInvariant())

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $start
try {
    if (-not $process.Start()) {
        throw 'The signed setup process did not start.'
    }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($stdout.Length -gt 16384) { $stdout = $stdout.Substring(0, 16384) }
    if ($stderr.Length -gt 16384) { $stderr = $stderr.Substring(0, 16384) }
    $result = [ordered]@{
        contract = 'pegasus-cortex-setup-diagnostic'
        version = 1
        setupSha256 = [Convert]::ToHexStringLower(
            [System.Security.Cryptography.SHA256]::HashData(
                [System.IO.File]::ReadAllBytes($setup)))
        exitCode = $process.ExitCode
        stdout = $stdout
        stderr = $stderr
    }
    $json = $result | ConvertTo-Json -Compress -Depth 4
    [System.IO.File]::WriteAllText(
        $report,
        ($json + [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))
    exit $process.ExitCode
}
finally {
    $process.Dispose()
}
