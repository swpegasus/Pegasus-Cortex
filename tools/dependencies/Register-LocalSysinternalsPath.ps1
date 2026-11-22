#requires -Version 7.0
<#
.SYNOPSIS
Adds the locally supplied, native Sysinternals directory to machine PATH.
.DESCRIPTION
No download, EULA acceptance, tool execution, registration, or service changes.
Without -Apply, validates the fixed shared layout and returns a non-mutating plan.
Run -Apply from an elevated PowerShell 7 session. Existing PATH entries are preserved.
#>
[CmdletBinding()]
param([switch]$Apply)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This helper requires Windows.' }
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$archFolder, $suffix, $machine = switch ($architecture) {
    'X64' { 'x64', '64', 0x8664 }
    'Arm64' { 'arm64', '64a', 0xaa64 }
    default { throw "Unsupported native architecture: $architecture" }
}
$toolsRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Pegasus\Tools'
$suiteRoot = Join-Path $toolsRoot 'Sysinternals'
$nativeRoot = Join-Path $suiteRoot $archFolder
$receiptPath = Join-Path $suiteRoot 'machine-path.v1.json'
$entries = @($toolsRoot, $nativeRoot)

function Assert-PlainPath([string]$Path) {
    $canonical = [IO.Path]::GetFullPath($Path)
    if ($canonical.Length -gt 240 -or $canonical -cne $Path) { throw 'Noncanonical or overlong tools path.' }
    $cursor = $canonical
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse path rejected: $cursor" }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

foreach ($entry in $entries) {
    Assert-PlainPath $entry
    if (-not (Get-Item -LiteralPath $entry).PSIsContainer) { throw 'Expected a tools directory.' }
}
$versions = [ordered]@{}
foreach ($name in @('handle', 'procdump', 'vmmap', 'Procmon', 'sigcheck')) {
    $file = Join-Path $nativeRoot "$name$suffix.exe"
    Assert-PlainPath $file
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
        throw "A valid Microsoft signature is required: $file"
    }
    $stream = [IO.File]::OpenRead($file)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Invalid DOS header.' }
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 64 -or $peOffset -gt ($stream.Length - 6)) { throw 'Invalid PE offset.' }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne $machine) { throw 'Wrong PE architecture.' }
    } finally { $stream.Dispose() }
    $versions["$name$suffix.exe"] = (Get-Item -LiteralPath $file).VersionInfo.FileVersion
}

function Normalize-Entry([string]$Value) {
    [Environment]::ExpandEnvironmentVariables($Value.Trim().Trim('"')).TrimEnd('\')
}
$registryPath = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
if ($Apply -and -not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Apply requires an elevated PowerShell 7 session.' }
$key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($registryPath, [bool]$Apply)
try {
    if ($null -eq $key) { throw 'Machine environment key is unavailable.' }
    $kind = $key.GetValueKind('Path')
    if ($kind -notin @([Microsoft.Win32.RegistryValueKind]::String, [Microsoft.Win32.RegistryValueKind]::ExpandString)) {
        throw 'Machine PATH must be a string or expandable string.'
    }
    $original = $key.GetValue('Path', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    if ($original -isnot [string] -or [string]::IsNullOrWhiteSpace($original)) { throw 'Machine PATH is missing or empty.' }
    $normalized = @($original.Split(';') | ForEach-Object { Normalize-Entry $_ })
    $missing = @($entries | Where-Object { (Normalize-Entry $_) -notin $normalized })
    $owned = @()
    if (Test-Path -LiteralPath $receiptPath) {
        Assert-PlainPath $receiptPath
        if ((Get-Item -LiteralPath $receiptPath).Length -gt 16384) { throw 'Oversized PATH ownership record.' }
        $previous = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        if ($previous.schema -ne 'pegasus.local-sysinternals-path/1' -or $previous.nativeDirectory -cne $nativeRoot -or
            @($previous.ownedEntries).Count -gt 2 -or @($previous.ownedEntries | Where-Object { $_ -notin $entries }).Count) {
            throw 'Invalid PATH ownership record.'
        }
        $owned = @($previous.ownedEntries)
    }
    $result = [ordered]@{
        schema = 'pegasus.local-sysinternals-path/1'
        nativeDirectory = $nativeRoot
        ownedEntries = @(@($owned) + @($(if ($Apply) { $missing })) | Where-Object { $_ } | Select-Object -Unique)
        preexistingEntries = @($entries | Where-Object { $_ -notin $missing -and $_ -notin $owned })
        versions = $versions
        applied = [bool]$Apply
        added = $missing.Count
    }
    if ($Apply) {
        if ($missing.Count) {
            $updated = $original + $(if ($original.EndsWith(';')) { '' } else { ';' }) + ($missing -join ';')
            if ($updated.Length -gt 32767) { throw 'Updated machine PATH exceeds the Windows environment limit.' }
            if ($key.GetValue('Path', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne $original) {
                throw 'Machine PATH changed concurrently; inspect before retrying.'
            }
            $key.SetValue('Path', $updated, $kind)
            if ($key.GetValue('Path', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne $updated) {
                throw 'Machine PATH write did not verify.'
            }
        }
        # Only entries added by this helper are owned; never claim preexisting entries.
        $tempPath = Join-Path $suiteRoot ('.path-' + [guid]::NewGuid().ToString('N') + '.tmp')
        $json = $result | ConvertTo-Json -Depth 5
        $file = [IO.File]::Open($tempPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
            $file.Write($bytes, 0, $bytes.Length)
            $file.Flush($true)
        } finally { $file.Dispose() }
        [IO.File]::Move($tempPath, $receiptPath, $true)
    }
    $result | ConvertTo-Json -Depth 5 -Compress
} finally { if ($key) { $key.Dispose() } }

if ($Apply) {
    # Existing processes keep their inherited environment. Notify desktop listeners;
    # do not restart applications or rebuild PATH with setx.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PegasusLocalToolsEnvironment {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr window, uint message,
        UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}
'@
    $broadcastResult = [UIntPtr]::Zero
    $notified = [PegasusLocalToolsEnvironment]::SendMessageTimeout([IntPtr]0xffff, 0x1a,
        [UIntPtr]::Zero, 'Environment', 2, 3000, [ref]$broadcastResult)
    if ($notified -eq [IntPtr]::Zero) { Write-Warning 'PATH was saved; some existing applications may need restarting to refresh their environment.' }
}
