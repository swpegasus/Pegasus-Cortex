Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# CreateDirectory's managed convenience API succeeds for an existing directory.
# This one native primitive makes extraction ownership create-only atomically.
if (-not ('Pegasus.Cortex.Build.NsisDirectory' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace Pegasus.Cortex.Build {
    public static class NsisDirectory {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
        public static void CreateNew(string path) {
            if (!CreateDirectoryW(path, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "NSIS extraction directory is create-only: " + path);
        }
    }
}
'@
}

function Assert-CortexNsisName {
    param([string]$Name)
    if ([string]::IsNullOrEmpty($Name) -or $Name -in @('.', '..') -or
        $Name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $Name -match '[\x00-\x1f\x7f]' -or $Name.EndsWith('.') -or $Name.EndsWith(' ') -or
        [Text.Encoding]::UTF8.GetByteCount($Name) -gt 255 -or
        $Name -match '^(?i:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)') {
        throw "The NSIS distribution contains an unsafe Windows path segment: '$Name'."
    }
}

function Get-CortexNsisPath {
    param([string]$Path)
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\?\') -or $Path.StartsWith('\\.\')) {
        throw 'NSIS paths must be ordinary fully qualified paths.'
    }
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.Length -gt 240) { throw 'An NSIS path exceeds the 240-character budget.' }
    $pathRoot = [IO.Path]::GetPathRoot($full)
    if ($full.TrimEnd('\', '/') -eq $pathRoot.TrimEnd('\', '/')) { throw 'An NSIS path cannot be a volume root.' }
    foreach ($part in $full.Substring($pathRoot.Length).TrimEnd('\', '/').Split([char]'\')) {
        Assert-CortexNsisName $part
    }
    return $full.TrimEnd('\', '/')
}

function Assert-CortexNsisOrdinaryPath {
    param([string]$Path)
    # Do not recursively enumerate a reparse directory. All existing ancestors
    # are checked, including ancestors outside the final distribution directory.
    $cursor = Get-CortexNsisPath $Path
    while (-not [string]::IsNullOrEmpty($cursor)) {
        try {
            $attributes = [IO.File]::GetAttributes($cursor)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "An NSIS path contains a reparse point: '$cursor'."
            }
        } catch [IO.FileNotFoundException] {
        } catch [IO.DirectoryNotFoundException] {
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Get-CortexNsisRelativeName {
    param([string]$Name)
    if ([string]::IsNullOrEmpty($Name) -or $Name.Length -gt 240 -or $Name.Contains('\') -or $Name.StartsWith('/') -or
        [IO.Path]::IsPathRooted($Name)) { throw 'The NSIS archive contains a rooted or noncanonical entry.' }
    foreach ($part in $Name.Split([char]'/')) { Assert-CortexNsisName $part }
    return $Name
}

function Get-CortexNsisTarget {
    param([string]$Root, [string]$Relative)
    $safe = Get-CortexNsisRelativeName $Relative
    $path = Get-CortexNsisPath (Join-Path $Root $safe.Replace('/', '\'))
    if (-not $path.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'An NSIS entry escaped the selected extraction root.'
    }
    return $path
}

function Get-CortexNsisStreamDigest {
    param([IO.Stream]$Stream, [long]$ExpectedLength)
    $hasher = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $buffer = [byte[]]::new(65536)
        $length = 0L
        while (($read = $Stream.Read($buffer, 0, $buffer.Length)) -ne 0) {
            $length += $read
            if ($length -gt $ExpectedLength) { throw 'An NSIS entry exceeded its declared length.' }
            $hasher.AppendData($buffer, 0, $read)
        }
        if ($length -ne $ExpectedLength) { throw 'An NSIS entry has an unexpected length.' }
        return [Convert]::ToHexString($hasher.GetHashAndReset()).ToLowerInvariant()
    } finally { $hasher.Dispose() }
}

function Assert-CortexNsisArchiveOpen {
    param($Archive)
    if ($null -eq $Archive -or $Archive.PSTypeNames -notcontains 'Pegasus.Cortex.NsisPinnedArchive' -or
        $Archive.Closed -or -not $Archive.Stream.CanRead) { throw 'The pinned NSIS archive is not open.' }
}

function Open-CortexNsisPinnedArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)]$Contract
    )
    $path = Get-CortexNsisPath $ArchivePath
    Assert-CortexNsisOrdinaryPath $path
    $expectedLength = [long]$Contract.archive.length
    $expectedHash = [string]$Contract.archive.sha256
    $distribution = Get-CortexNsisRelativeName ([string]$Contract.distributionRoot)
    if ($distribution.Contains('/') -or $expectedLength -lt 1 -or $expectedLength -gt 64MB -or
        $expectedHash -cnotmatch '^[0-9a-f]{64}$') { throw 'The pinned NSIS archive contract is invalid.' }
    $compilerName = Get-CortexNsisRelativeName ([string]$Contract.compiler.relativePath)
    $compilerLength = [long]$Contract.compiler.length
    $compilerHash = [string]$Contract.compiler.sha256
    if ($compilerLength -lt 1 -or $compilerLength -gt 64MB -or $compilerHash -cnotmatch '^[0-9a-f]{64}$') {
        throw 'The independent NSIS compiler pin is invalid.'
    }
    $stream = $null
    $zip = $null
    try {
        # Windows sharing admission refuses both an existing writable open and
        # subsequent write/delete/replace opens until this exact stream closes.
        $stream = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($stream.Length -ne $expectedLength -or
            (Get-CortexNsisStreamDigest $stream $expectedLength) -cne $expectedHash) {
            throw 'The NSIS archive length or SHA256 does not match its pinned contract.'
        }
        $stream.Position = 0
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        if ($zip.Entries.Count -lt 1 -or $zip.Entries.Count -gt 8192) { throw 'The NSIS archive entry count is outside its bound.' }
        $files = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
        $directories = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
        $explicitNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $expandedLength = 0L
        foreach ($entry in $zip.Entries) {
            $isDirectory = $entry.FullName.EndsWith('/')
            $name = if ($isDirectory) { $entry.FullName.Substring(0, $entry.FullName.Length - 1) } else { $entry.FullName }
            [void](Get-CortexNsisRelativeName $name)
            if (-not $explicitNames.Add($name)) { throw 'The NSIS archive contains duplicate or case-colliding entries.' }
            $unixType = (([long]$entry.ExternalAttributes -shr 16) -band 0xf000)
            if (($entry.ExternalAttributes -band 0x400) -ne 0 -or
                $unixType -notin @(0, 0x4000, 0x8000) -or
                ($unixType -eq 0x4000 -and -not $isDirectory) -or
                ($unixType -eq 0x8000 -and $isDirectory)) { throw 'The NSIS archive contains a reparse or special-file entry.' }
            if ($name -ceq $distribution) {
                if (-not $isDirectory -or $entry.Length -ne 0) { throw 'The canonical NSIS root is not a directory.' }
                continue
            }
            if (-not $name.StartsWith($distribution + '/', [StringComparison]::Ordinal)) {
                throw 'The NSIS archive contains an entry outside its canonical distribution root.'
            }
            $relative = Get-CortexNsisRelativeName $name.Substring($distribution.Length + 1)
            $parts = $relative.Split([char]'/')
            $directoryCount = if ($isDirectory) { $parts.Length } else { $parts.Length - 1 }
            for ($index = 1; $index -le $directoryCount; $index++) {
                $directory = $parts[0..($index - 1)] -join '/'
                if ($files.ContainsKey($directory) -or
                    ($directories.ContainsKey($directory) -and $directories[$directory] -cne $directory)) {
                    throw 'The NSIS archive has a file/directory or case-normalization collision.'
                }
                $directories[$directory] = $directory
                if ($directories.Count -gt 16384) { throw 'The NSIS derived directory inventory exceeds its bound.' }
            }
            if ($isDirectory) {
                if ($entry.Length -ne 0) { throw 'An NSIS directory entry contains bytes.' }
                continue
            }
            if ($directories.ContainsKey($relative) -or $files.ContainsKey($relative)) { throw 'The NSIS archive has a colliding file path.' }
            if ($entry.Length -lt 0 -or $entry.Length -gt 64MB) { throw 'An NSIS file is outside its length bound.' }
            $expandedLength += $entry.Length
            if ($expandedLength -gt 256MB) { throw 'The NSIS expanded distribution exceeds its bound.' }
            $input = $entry.Open()
            try { $digest = Get-CortexNsisStreamDigest $input $entry.Length } finally { $input.Dispose() }
            $files.Add($relative, [pscustomobject]@{ Relative=$relative; Length=[long]$entry.Length; Sha256=$digest; Entry=$entry })
        }
        if (-not $files.ContainsKey($compilerName) -or $files[$compilerName].Length -ne $compilerLength -or
            $files[$compilerName].Sha256 -cne $compilerHash) { throw 'The compiler bytes in the pinned NSIS archive do not match the independent compiler pin.' }
        $result = [pscustomobject]@{ Stream=$stream; Zip=$zip; Files=$files; Directories=$directories;
            ArchivePath=$path; ArchiveSha256=$expectedHash; CompilerRelativePath=$compilerName; Closed=$false }
        $result.PSTypeNames.Insert(0, 'Pegasus.Cortex.NsisPinnedArchive')
        return $result
    } catch {
        if ($null -ne $zip) { $zip.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
        throw
    }
}

function Close-CortexNsisPinnedArchive {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Archive)
    if ($Archive.PSTypeNames -notcontains 'Pegasus.Cortex.NsisPinnedArchive') { throw 'Unknown NSIS archive owner.' }
    if (-not $Archive.Closed) {
        $Archive.Closed = $true
        try { $Archive.Zip.Dispose() } finally { $Archive.Stream.Dispose() }
    }
}

function Assert-CortexNsisCacheExact {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Archive, [Parameter(Mandatory)][string]$Directory)
    Assert-CortexNsisArchiveOpen $Archive
    $root = Get-CortexNsisPath $Directory
    Assert-CortexNsisOrdinaryPath $root
    if (-not [IO.Directory]::Exists($root)) { throw 'The NSIS distribution directory is missing.' }
    $seenFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $seenDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($root)
    while ($pending.Count -ne 0) {
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($pending.Pop())) {
            $attributes = [IO.File]::GetAttributes($path)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The NSIS cache contains a reparse entry.' }
            $relative = [IO.Path]::GetRelativePath($root, $path).Replace('\', '/')
            $canonical = Get-CortexNsisTarget $root $relative
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                if (-not $Archive.Directories.ContainsKey($relative) -or
                    $Archive.Directories[$relative] -cne $relative -or -not $seenDirectories.Add($relative)) {
                    throw "The NSIS cache has an extra or noncanonical directory: '$relative'."
                }
                $pending.Push($canonical)
                continue
            }
            if (-not $Archive.Files.ContainsKey($relative) -or -not $seenFiles.Add($relative)) { throw "The NSIS cache has an extra file: '$relative'." }
            $expected = $Archive.Files[$relative]
            if ($expected.Relative -cne $relative) { throw 'The NSIS cache has a case-normalized file alias.' }
            Assert-CortexNsisOrdinaryPath $canonical
            $input = [IO.FileStream]::new($canonical, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                if ($input.Length -ne $expected.Length -or
                    (Get-CortexNsisStreamDigest $input $expected.Length) -cne $expected.Sha256) {
                    throw "The NSIS cache file does not match the pinned distribution: '$relative'."
                }
            } finally { $input.Dispose() }
        }
    }
    if ($seenFiles.Count -ne $Archive.Files.Count -or $seenDirectories.Count -ne $Archive.Directories.Count) {
        throw 'The NSIS cache is missing an official file or directory.'
    }
}

function Expand-CortexNsisPinnedArchive {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Archive, [Parameter(Mandatory)][string]$Destination)
    Assert-CortexNsisArchiveOpen $Archive
    $root = Get-CortexNsisPath $Destination
    Assert-CortexNsisOrdinaryPath $root
    # Preflight every final generated path before taking ownership or writing.
    foreach ($relative in $Archive.Directories.Keys) { [void](Get-CortexNsisTarget $root $relative) }
    foreach ($relative in $Archive.Files.Keys) { [void](Get-CortexNsisTarget $root $relative) }
    $createdDirectories = [Collections.Generic.List[string]]::new()
    $createdFiles = [Collections.Generic.List[string]]::new()
    try {
        [Pegasus.Cortex.Build.NsisDirectory]::CreateNew($root)
        $createdDirectories.Add($root)
        foreach ($relative in @($Archive.Directories.Keys | Sort-Object { $_.Split([char]'/').Length }, { $_ })) {
            $path = Get-CortexNsisTarget $root $relative
            [Pegasus.Cortex.Build.NsisDirectory]::CreateNew($path)
            $createdDirectories.Add($path)
        }
        foreach ($relative in @($Archive.Files.Keys | Sort-Object)) {
            $expected = $Archive.Files[$relative]
            $path = Get-CortexNsisTarget $root $relative
            $output = [IO.FileStream]::new($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $createdFiles.Add($path)
            try {
                # Release bytes come from this still-held pinned ZIP entry,
                # never from any file in the validated-but-mutable cache tree.
                $input = $expected.Entry.Open()
                try { $input.CopyTo($output) } finally { $input.Dispose() }
                $output.Flush($true)
                $output.Position = 0
                if ($output.Length -ne $expected.Length -or
                    (Get-CortexNsisStreamDigest $output $expected.Length) -cne $expected.Sha256) {
                    throw 'A materialized NSIS file did not retain its admitted bytes.'
                }
            } finally { $output.Dispose() }
        }
        Assert-CortexNsisCacheExact -Archive $Archive -Directory $root
        return $root
    } catch {
        $failure = $_.Exception
        $cleanupFailed = $false
        # Only paths actually created by this invocation are removed, and only
        # empty owned directories. Never recurse into an unknown/reparse tree.
        for ($index = $createdFiles.Count - 1; $index -ge 0; $index--) {
            try { Assert-CortexNsisOrdinaryPath $createdFiles[$index]; [IO.File]::Delete($createdFiles[$index]) }
            catch { $cleanupFailed = $true }
        }
        for ($index = $createdDirectories.Count - 1; $index -ge 0; $index--) {
            try { Assert-CortexNsisOrdinaryPath $createdDirectories[$index]; [IO.Directory]::Delete($createdDirectories[$index], $false) }
            catch { $cleanupFailed = $true }
        }
        if ($cleanupFailed) { throw [IO.IOException]::new("NSIS extraction failed; owned-target cleanup is incomplete at '$root'. No directory was admitted.", $failure) }
        throw
    }
}

Export-ModuleMember -Function Open-CortexNsisPinnedArchive, Close-CortexNsisPinnedArchive, Assert-CortexNsisCacheExact, Expand-CortexNsisPinnedArchive, Assert-CortexNsisOrdinaryPath
