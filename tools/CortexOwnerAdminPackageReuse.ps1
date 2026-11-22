# Dot-sourceable, fixed four-role packaging helper. The caller MUST first verify
# the complete baseline's signed manifest, detached signature, and public signer
# binding with the owning package verifier. Metadata validation here grants no trust.

function Get-CortexReuseProperty {
    param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$Name)
    if ($Value -is [Collections.IDictionary]) {
        $keys = @($Value.Keys | Where-Object { [string]::Equals([string]$_, $Name, [StringComparison]::OrdinalIgnoreCase) })
        if ($keys.Count -ne 1) { throw "CortexReuse: metadata property '$Name' is missing or ambiguous." }
        return ,$Value[$keys[0]]
    }
    $properties = @($Value.PSObject.Properties | Where-Object { [string]::Equals($_.Name, $Name, [StringComparison]::OrdinalIgnoreCase) })
    if ($properties.Count -ne 1) { throw "CortexReuse: metadata property '$Name' is missing or ambiguous." }
    return ,$properties[0].Value
}

function Assert-CortexReuseRelativePath {
    param([Parameter(Mandatory)][string]$Path)
    if ($Path.Length -gt 200 -or $Path.Length -eq 0 -or $Path.Contains('\') -or $Path.StartsWith('/') -or
        $Path -match '[\x00-\x1f\x7f<>:"|?*]') { throw 'CortexReuse: unsafe relative path.' }
    foreach ($part in $Path.Split('/')) {
        if ($part.Length -eq 0 -or $part.Length -gt 120 -or $part -in '.', '..' -or
            $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
            throw 'CortexReuse: unsafe relative path component.'
        }
    }
}

function Get-CortexReuseRole {
    param([Parameter(Mandatory)][string]$Path)
    if ($Path -imatch '^PegasusCortex\.') {
        if ($Path -in 'PegasusCortex.exe', 'PegasusCortex.pdb') { return 'launcher' }
        throw 'CortexReuse: unexpected launcher closure entry.'
    }
    if ($Path -imatch '^installer(?:/|$)') { return 'setup' }
    if ($Path -imatch '^bin/ui(?:/|$)') { return 'ui-host' }
    if ($Path -imatch '^bin/(?:host|service)/Pegasus\.AgentOps\.ProcessHost') {
        if ($Path -imatch '^bin/(?:host|service)/Pegasus\.AgentOps\.ProcessHost\.(?:exe|dll|deps\.json|runtimeconfig\.json|pdb)$') { return 'process-host' }
        throw 'CortexReuse: unexpected process-host closure entry.'
    }
    return ''
}

function Get-CortexReuseRequiredPaths {
    'PegasusCortex.exe'
    'PegasusCortex.pdb'
    'installer/PegasusCortex.Setup.exe'
    foreach ($directory in 'host', 'service') {
        foreach ($suffix in 'exe', 'dll', 'deps.json', 'runtimeconfig.json') { "bin/$directory/Pegasus.AgentOps.ProcessHost.$suffix" }
    }
    foreach ($suffix in 'exe', 'dll', 'deps.json', 'runtimeconfig.json') { "bin/ui/Pegasus.AgentOps.UiHost.$suffix" }
}

function ConvertTo-CortexReuseRows {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Items, [switch]$ManifestFiles)
    $limit = if ($ManifestFiles) { 20000 } else { 10000 }
    if ($Items.Count -eq 0 -or $Items.Count -gt $limit) { throw 'CortexReuse: metadata entry count is out of bounds.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $selected = [Collections.Generic.List[object]]::new()
    [long]$total = 0
    foreach ($item in $Items) {
        if ($null -eq $item) { throw 'CortexReuse: null metadata entry.' }
        $path = Get-CortexReuseProperty $item 'Path'
        $length = Get-CortexReuseProperty $item 'Bytes'
        $sha = Get-CortexReuseProperty $item 'Sha256'
        if ($path -isnot [string] -or $sha -isnot [string] -or $sha -cnotmatch '^[0-9a-f]{64}$' -or
            $length -isnot [byte] -and $length -isnot [short] -and $length -isnot [int] -and $length -isnot [long] -and $length -isnot [uint32]) {
            throw 'CortexReuse: invalid path, byte length, or SHA-256 metadata.'
        }
        Assert-CortexReuseRelativePath $path
        if (-not $seen.Add($path)) { throw 'CortexReuse: case-insensitive duplicate path.' }
        if ([long]$length -lt 0 -or [long]$length -gt 8GB -or [long]$length -gt 32GB - $total) { throw 'CortexReuse: byte metadata is out of bounds.' }
        $total += [long]$length
        $role = Get-CortexReuseRole $path
        if ($ManifestFiles) {
            if (-not $role) { continue }
        } else {
            $suppliedRole = Get-CortexReuseProperty $item 'Role'
            if (-not $role -or $suppliedRole -isnot [string] -or $suppliedRole -cne $role) { throw 'CortexReuse: role does not match the fixed closure.' }
            $names = if ($item -is [Collections.IDictionary]) { @($item.Keys) } else { @($item.PSObject.Properties.Name) }
            if (@($names | Where-Object { $_ -notin 'Role', 'Path', 'Bytes', 'Sha256' }).Count -ne 0) { throw 'CortexReuse: unexpected inventory property.' }
        }
        if ($path -ieq 'installer' -or $path -ieq 'bin/ui') { throw 'CortexReuse: closure directory cannot be a file.' }
        $selected.Add([pscustomobject]@{ Role = $role; Path = $path; Bytes = [long]$length; Sha256 = $sha })
    }
    if ($selected.Count -gt 10000) { throw 'CortexReuse: reused entry count is out of bounds.' }
    $selectedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $selected) { [void]$selectedPaths.Add($row.Path) }
    foreach ($required in Get-CortexReuseRequiredPaths) {
        if (-not $selectedPaths.Contains($required)) { throw "CortexReuse: mandatory closure leaf is missing: $required" }
    }
    $selected | Sort-Object -Property Path
}

function Get-CortexOwnerAdminReuseInventory {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Manifest)
    if ((Get-CortexReuseProperty $Manifest 'schema') -cne 'pegasus.cortex.local-operational-package/1.2' -or
        (Get-CortexReuseProperty $Manifest 'role') -cne 'product') { throw 'CortexReuse: expected the already verified product manifest schema.' }
    $files = Get-CortexReuseProperty $Manifest 'files'
    if ($files -is [string] -or $files -isnot [Collections.IEnumerable]) { throw 'CortexReuse: invalid manifest files collection.' }
    ConvertTo-CortexReuseRows -Items @($files) -ManifestFiles
}

function Assert-CortexReuseNoReparse {
    param([Parameter(Mandatory)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    $volume = [IO.Path]::GetPathRoot($full)
    $current = $volume
    $parts = @($full.Substring($volume.Length).Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries))
    $components = [Collections.Generic.List[string]]::new()
    $components.Add($volume)
    foreach ($part in $parts) { $current = [IO.Path]::Combine($current, $part); $components.Add($current) }
    for ($index = 0; $index -lt $components.Count; $index++) {
        try { $attributes = [IO.File]::GetAttributes($components[$index]) }
        catch [IO.FileNotFoundException] { break }
        catch [IO.DirectoryNotFoundException] { break }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'CortexReuse: reparse path is forbidden.' }
        if ($index -lt $components.Count - 1 -and ($attributes -band [IO.FileAttributes]::Directory) -eq 0) { throw 'CortexReuse: ancestor is not a directory.' }
    }
}

function Get-CortexReuseRoot {
    param([Parameter(Mandatory)][string]$Root)
    if (-not [OperatingSystem]::IsWindows() -or -not [IO.Path]::IsPathFullyQualified($Root) -or $Root.StartsWith('\\') -or $Root -match '[\x00-\x1f\x7f]') {
        throw 'CortexReuse: a local absolute Windows root is required.'
    }
    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    if ($full.Length -gt 200 -or $full -ieq [IO.Path]::GetPathRoot($full).TrimEnd('\', '/') -or -not [IO.Directory]::Exists($full)) {
        throw 'CortexReuse: root must be an existing non-volume directory.'
    }
    Assert-CortexReuseNoReparse $full
    $full
}

function Get-CortexReusePath {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Relative)
    Assert-CortexReuseRelativePath $Relative
    $full = [IO.Path]::GetFullPath([IO.Path]::Combine($Root, $Relative.Replace('/', '\')))
    if ($full.Length -gt 240 -or -not $full.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'CortexReuse: path escapes or exceeds its exact root.' }
    Assert-CortexReuseNoReparse $full
    $full
}

function Get-CortexReuseClosureFiles {
    param([Parameter(Mandatory)][string]$Root)
    $found = [Collections.Generic.List[string]]::new()
    $stack = [Collections.Generic.Stack[string]]::new()
    $count = 0
    foreach ($entry in [IO.DirectoryInfo]::new($Root).EnumerateFileSystemInfos()) {
        if (++$count -gt 50000) { throw 'CortexReuse: closure scan is out of bounds.' }
        if ($entry.Name.StartsWith('PegasusCortex.', [StringComparison]::OrdinalIgnoreCase)) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'CortexReuse: reparse closure entry.' }
            if (($entry.Attributes -band [IO.FileAttributes]::Directory) -ne 0) { throw 'CortexReuse: unexpected launcher closure directory.' }
            [void](Get-CortexReuseRole $entry.Name); $found.Add($entry.Name)
        }
    }
    foreach ($relative in 'installer', 'bin/ui') {
        $directory = Get-CortexReusePath $Root $relative
        if ([IO.File]::Exists($directory)) { throw 'CortexReuse: closure root is not a directory.' }
        if ([IO.Directory]::Exists($directory)) { $stack.Push($directory) }
    }
    while ($stack.Count -ne 0) {
        foreach ($entry in [IO.DirectoryInfo]::new($stack.Pop()).EnumerateFileSystemInfos()) {
            if (++$count -gt 50000) { throw 'CortexReuse: closure scan is out of bounds.' }
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'CortexReuse: reparse closure entry.' }
            $relative = [IO.Path]::GetRelativePath($Root, $entry.FullName).Replace('\', '/')
            [void](Get-CortexReusePath $Root $relative)
            if (($entry.Attributes -band [IO.FileAttributes]::Directory) -ne 0) { $stack.Push($entry.FullName) }
            else { $found.Add($relative) }
        }
    }
    foreach ($relative in 'bin/host', 'bin/service') {
        $directory = Get-CortexReusePath $Root $relative
        if ([IO.File]::Exists($directory)) { throw 'CortexReuse: process-host parent is not a directory.' }
        if (-not [IO.Directory]::Exists($directory)) { continue }
        foreach ($entry in [IO.DirectoryInfo]::new($directory).EnumerateFileSystemInfos()) {
            if (++$count -gt 50000) { throw 'CortexReuse: closure scan is out of bounds.' }
            if (-not $entry.Name.StartsWith('Pegasus.AgentOps.ProcessHost', [StringComparison]::OrdinalIgnoreCase)) { continue }
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'CortexReuse: reparse closure entry.' }
            if (($entry.Attributes -band [IO.FileAttributes]::Directory) -ne 0) { throw 'CortexReuse: unexpected process-host closure directory.' }
            $path = $relative + '/' + $entry.Name
            [void](Get-CortexReuseRole $path); $found.Add($path)
        }
    }
    $found
}

function Assert-CortexReuseClosureSet {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][object[]]$Rows, [switch]$AllowMissing)
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $Rows) { [void]$expected.Add($row.Path) }
    $observed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @(Get-CortexReuseClosureFiles $Root)) {
        if (-not $observed.Add($path) -or -not $expected.Contains($path)) { throw "CortexReuse: extra or duplicate closure file: $path" }
    }
    if (-not $AllowMissing -and $observed.Count -ne $expected.Count) { throw 'CortexReuse: missing closure file.' }
}

function Get-CortexReuseStreamHash {
    param([Parameter(Mandatory)][IO.Stream]$Stream)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { ([Convert]::ToHexString($algorithm.ComputeHash($Stream))).ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}

function Copy-CortexOwnerAdminReusedRoles {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BaselineRoot, [Parameter(Mandatory)][string]$ProductRoot, [Parameter(Mandatory)][object[]]$Inventory)
    $rows = @(ConvertTo-CortexReuseRows -Items $Inventory)
    $sourceRoot = Get-CortexReuseRoot $BaselineRoot
    $targetRoot = Get-CortexReuseRoot $ProductRoot
    if ($sourceRoot.Equals($targetRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $sourceRoot.StartsWith($targetRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $targetRoot.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'CortexReuse: baseline/product roots overlap.' }
    Assert-CortexReuseClosureSet -Root $sourceRoot -Rows $rows
    Assert-CortexReuseClosureSet -Root $targetRoot -Rows $rows -AllowMissing
    foreach ($row in $rows) {
        [void](Get-CortexReusePath $sourceRoot $row.Path)
        $destination = Get-CortexReusePath $targetRoot $row.Path
        if ([IO.File]::Exists($destination) -or [IO.Directory]::Exists($destination)) { throw "CortexReuse: create-only destination collision: $($row.Path)" }
    }
    [long]$copiedBytes = 0
    foreach ($row in $rows) {
        $source = Get-CortexReusePath $sourceRoot $row.Path
        $destination = Get-CortexReusePath $targetRoot $row.Path
        # FileShare.Read denies concurrent source writes and replacement for the
        # complete pre-copy verification, copy, and target verification interval.
        $read = [IO.FileStream]::new($source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            if ($read.Length -ne $row.Bytes -or (Get-CortexReuseStreamHash $read) -cne $row.Sha256) { throw "CortexReuse: source bytes changed: $($row.Path)" }
            $read.Position = 0
            $parent = [IO.Path]::GetDirectoryName($destination)
            [void][IO.Directory]::CreateDirectory($parent)
            [void](Get-CortexReusePath $targetRoot $row.Path)
            $write = [IO.FileStream]::new($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            try {
                $read.CopyTo($write); $write.Flush($true); $write.Position = 0
                if ($write.Length -ne $row.Bytes -or (Get-CortexReuseStreamHash $write) -cne $row.Sha256) { throw "CortexReuse: copied target bytes mismatch: $($row.Path)" }
            } finally { $write.Dispose() }
        } finally { $read.Dispose() }
        $copiedBytes += $row.Bytes
    }
    [pscustomobject]@{ ReusedFiles = $rows.Count; ReusedBytes = $copiedBytes; Roles = @('launcher', 'setup', 'process-host', 'ui-host') }
}

function Assert-CortexOwnerAdminReusedRoles {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProductRoot, [Parameter(Mandatory)][object[]]$Inventory)
    $rows = @(ConvertTo-CortexReuseRows -Items $Inventory)
    $root = Get-CortexReuseRoot $ProductRoot
    Assert-CortexReuseClosureSet -Root $root -Rows $rows
    foreach ($row in $rows) {
        $path = Get-CortexReusePath $root $row.Path
        $read = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            if ($read.Length -ne $row.Bytes -or (Get-CortexReuseStreamHash $read) -cne $row.Sha256) { throw "CortexReuse: reused target bytes changed: $($row.Path)" }
        } finally { $read.Dispose() }
    }
    [pscustomobject]@{ VerifiedFiles = $rows.Count; Roles = @('launcher', 'setup', 'process-host', 'ui-host'); ExactUnchangedClosure = $true }
}
