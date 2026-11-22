Set-StrictMode -Version Latest

function Get-CortexBuildProcessOutputTail {
    param([AllowEmptyString()][string]$Text, [int]$MaximumCharacters = 16384)
    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }
    $trimmed = $Text.TrimEnd()
    if ($trimmed.Length -le $MaximumCharacters) { return $trimmed }
    return "...`n" + $trimmed.Substring($trimmed.Length - $MaximumCharacters)
}

function Invoke-CortexBuildProcess {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [ValidateRange(1, 3600)][int]$TimeoutSeconds = 1800
    )

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $commandProcessor = [Environment]::GetEnvironmentVariable('ComSpec')
    if ([string]::IsNullOrWhiteSpace($commandProcessor)) {
        $commandProcessor = Join-Path ([Environment]::SystemDirectory) 'cmd.exe'
    }
    if (-not [string]::IsNullOrWhiteSpace($commandProcessor) -and
        [IO.Path]::IsPathFullyQualified($commandProcessor) -and
        [IO.File]::Exists($commandProcessor)) {
        $start.Environment['ComSpec'] = [IO.Path]::GetFullPath($commandProcessor)
    }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) {
            throw "Process did not start: '$Executable'."
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { $process.Kill() }
            $process.WaitForExit()
            $null = $stdoutTask.GetAwaiter().GetResult()
            $null = $stderrTask.GetAwaiter().GetResult()
            throw "Process timed out after $TimeoutSeconds seconds: '$Executable'."
        }
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $stdoutTask.GetAwaiter().GetResult()
            StandardError = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-CortexBuildPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Boundary)
    $canonical = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $parent = [IO.Path]::GetFullPath($Boundary).TrimEnd('\', '/')
    if (-not $canonical.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build path must be a child of '$parent': '$canonical'."
    }
    $current = [IO.Path]::GetPathRoot($canonical)
    foreach ($part in $canonical.Substring($current.Length).Split('\', [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Build path contains a file or reparse point: '$current'."
            }
        }
    }
    return $canonical
}

function Reset-CortexBuildDirectory {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Boundary)
    $canonical = Assert-CortexBuildPath -Path $Path -Boundary $Boundary
    if (-not (Test-Path -LiteralPath $canonical)) {
        New-Item -ItemType Directory -Path $canonical -ErrorAction Stop | Out-Null
        return
    }
    # Enumerate without following links, then validate every entry before deleting any.
    $pending = [Collections.Generic.Stack[string]]::new()
    $files = [Collections.Generic.List[string]]::new()
    $directories = [Collections.Generic.List[string]]::new()
    $pending.Push($canonical)
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force -ErrorAction Stop) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Build cleanup refuses a reparse point: '$($item.FullName)'."
            }
            if (-not $item.FullName.StartsWith($canonical + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Build cleanup escaped the exact target.'
            }
            if ($item.PSIsContainer) {
                $directories.Add($item.FullName)
                $pending.Push($item.FullName)
            } else {
                $files.Add($item.FullName)
                $probe = [IO.File]::Open($item.FullName, 'Open', 'Read', 'None')
                $probe.Dispose()
            }
        }
    }
    Write-Host "Replacing generated contents: $canonical ($($files.Count) files, $($directories.Count) directories)"
    foreach ($file in $files) { Remove-Item -LiteralPath $file -Force -ErrorAction Stop }
    foreach ($directory in ($directories | Sort-Object Length -Descending)) {
        # No recursive delete: a concurrently introduced child causes a failure.
        [IO.Directory]::Delete($directory, $false)
    }
}

function Enter-CortexBuildWorkspace {
    param([Parameter(Mandatory)][string]$ProjectRoot,[string]$CreateOnlyName="")
    $build = Join-Path $ProjectRoot 'artifacts\build'
    # Retained diagnostic/download fixtures elsewhere in current are not release
    # scratch and may have cleanup restrictions. Reset only this owned build lane.
    if($CreateOnlyName -and $CreateOnlyName -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,95}$'){throw "Invalid create-only build workspace name."}
    $relative=if($CreateOnlyName){$CreateOnlyName}else{'current\release-work'}
    $work = Assert-CortexBuildPath -Path (Join-Path $build $relative) -Boundary $build
    if (-not (Test-Path -LiteralPath $build)) {
        New-Item -ItemType Directory -Path $build -ErrorAction Stop | Out-Null
    }
    # Both builders share the same scratch directory; concurrent builds fail immediately.
    $lockPath = Join-Path $build '.release.lock'
    if ((Test-Path -LiteralPath $lockPath) -and
        ((Get-Item -LiteralPath $lockPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Build lock is a reparse point.'
    }
    $buildLock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        if($CreateOnlyName)
        {
            if(Test-Path -LiteralPath $work){throw "Create-only build workspace already exists: '$work'. Retain it for review."}
            New-Item -ItemType Directory -Path $work -ErrorAction Stop | Out-Null
        }
        else{Reset-CortexBuildDirectory -Path $work -Boundary $build}
        return [pscustomobject]@{ Path = $work; Boundary = $build; Lock = $buildLock; Preserve=$([bool]$CreateOnlyName) }
    } catch { $buildLock.Dispose(); throw }
}

function Exit-CortexBuildWorkspace {
    param([Parameter(Mandatory)]$Workspace)
    try { if(-not ($Workspace.PSObject.Properties['Preserve'] -and $Workspace.Preserve)){Reset-CortexBuildDirectory -Path $Workspace.Path -Boundary $Workspace.Boundary} }
    finally { $Workspace.Lock.Dispose() }
}
