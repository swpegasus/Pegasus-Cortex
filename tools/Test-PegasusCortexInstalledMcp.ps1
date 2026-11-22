[CmdletBinding()]
param(
    [string]$Executable = (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Pegasus\Cortex\PegasusCortex.exe'),
    [switch]$CompactReceiptChecks,
    [switch]$AssetChecks,
    [switch]$VisualLibraryChecks,
    [switch]$RequireResidentLifetime,
    [ValidateRange(0, 200)][int]$ReadOnlyIterations = 0,
    [string]$EvidenceFile
)
$ErrorActionPreference = 'Stop'
$item = Get-Item -LiteralPath $Executable -Force
if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Expected an ordinary Cortex executable.' }
$start = [Diagnostics.ProcessStartInfo]::new($item.FullName)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add('stdio')
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
try {
    if (-not $process.Start()) { throw 'Cortex MCP process did not start.' }
    $errors = $process.StandardError.ReadToEndAsync()
    function Invoke-McpRequest([int]$Id, [string]$Method, $Parameters) {
        $process.StandardInput.WriteLine((@{jsonrpc='2.0';id=$Id;method=$Method;params=$Parameters} | ConvertTo-Json -Compress -Depth 12))
        $line = $process.StandardOutput.ReadLineAsync()
        if (-not $line.Wait(30000)) { throw "MCP response timeout: $Method" }
        if ($null -eq $line.Result) {
            $failure = if ($errors.Wait(1000)) { $errors.Result } else { 'stderr still open' }
            throw "MCP EOF during ${Method}: $($failure.Substring(0, [Math]::Min(1800, $failure.Length)))"
        }
        $response = $line.Result | ConvertFrom-Json -Depth 30
        if ($response.id -ne $Id -or $null -ne $response.error) { throw "MCP protocol error: $Method" }
        return $response.result
    }
    $initialized = Invoke-McpRequest 1 'initialize' @{protocolVersion='2025-11-25';capabilities=@{};clientInfo=@{name='cortex-installed-acceptance';version='1'}}
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $listed = Invoke-McpRequest 2 'tools/list' @{}
    $status = Invoke-McpRequest 3 'tools/call' @{name='cortex_status';arguments=@{}}
    $help = Invoke-McpRequest 4 'tools/call' @{name='pao';arguments=@{op='help';name='app_status';output='agent'}}
    $diag = Invoke-McpRequest 5 'tools/call' @{name='pao';arguments=@{op='diag';output='agent'}}
    if ($status.isError -or $help.isError -or $diag.isError) { throw 'A bounded installed read operation failed.' }
    if ($null -ne $help.structuredContent -or $null -ne $diag.structuredContent) { throw 'Agent output duplicated structuredContent.' }
    $pao = @($listed.tools | Where-Object name -eq 'pao')
    if ($pao.Count -ne 1) { throw 'Expected exactly one unified PAO tool.' }
    $operations = @($pao[0].inputSchema.properties.op.enum)
    foreach ($operation in @('app_start','app_status','app_stop','nsis_status','nsis_validate','nsis_build')) {
        if ($operation -notin $operations) { throw "Missing installed operation: $operation" }
    }
    $assetEvidence = $null
    $visualEvidence = @()
    if ($VisualLibraryChecks) {
        # Exact admitted receipt coordinates avoid scanning/hydrating a catalog
        # merely to prove selected bytes. One keyword search separately exercises discovery.
        function Read-VisualJson($Result) {
            $texts=@($Result.contents | Where-Object { $null -ne $_.text })
            if ($texts.Count -ne 1) { throw 'Expected a bounded asset metadata response.' }
            $data=$texts[0].text|ConvertFrom-Json -Depth 32
            if ($data.contract -ne 'pegasus.assets/2' -or $data.ok -eq $false) { throw 'Asset response failed.' }
            return $data
        }
        $libraryRoot=Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release\assets\libraries'
        $requestId=10000
        foreach ($library in @('openmoji','fluent-emoji','svgmoji','remix-icon')) {
            $receipt=Get-Content -LiteralPath (Join-Path $libraryRoot ($library+'\admission.json')) -Raw|ConvertFrom-Json
            $sample=$receipt.sample
            $metadata=Read-VisualJson (Invoke-McpRequest (++$requestId) 'resources/read' @{uri=$sample.uri})
            if ($metadata.id -ne $sample.id -or $metadata.ref -ne $sample.ref -or $metadata.sha256 -ne $sample.sha256 -or
                $metadata.pack -ne $receipt.pack -or $metadata.packVersion -ne $receipt.version -or
                $metadata.license -ne $sample.license -or $metadata.detailsUri -ne $sample.licenseUri -or $metadata.namespace -ne 'knowledge.shared') { throw 'Visual identity/licensing link mismatch.' }
            $parameters=$metadata.parameters|ConvertFrom-Json -Depth 24
            if ($parameters.artworkModifiedByCortex -ne $false -or $parameters.renderProfile -ne 'passive-svg-original' -or
                $parameters.upstreamRevision -ne $receipt.version -or -not $parameters.originalPath) { throw 'Visual provenance lost.' }
            $original=Invoke-McpRequest (++$requestId) 'resources/read' @{uri=($sample.bytesUri+'&length=131072')}
            $blob=@($original.contents | Where-Object { $null -ne $_.blob })
            if ($blob.Count -ne 1) { throw 'Original artwork unavailable.' }
            $bytes=[Convert]::FromBase64String($blob[0].blob)
            $sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
            if (-not $original._meta.complete -or $bytes.Length -ne $sample.size -or $sha -ne $sample.sha256) { throw 'Original artwork digest mismatch.' }
            $license=Read-VisualJson (Invoke-McpRequest (++$requestId) 'resources/read' @{uri=$sample.licenseUri})
            $licenseBytes=Invoke-McpRequest (++$requestId) 'resources/read' @{uri=$license.bytesUri}
            $licenseBody=[Convert]::FromBase64String(@($licenseBytes.contents|Where-Object {$null -ne $_.blob})[0].blob)
            if ($license.license -ne $sample.license -or -not $licenseBytes._meta.complete -or
                [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($licenseBody)).ToLowerInvariant() -ne $license.sha256) { throw 'Linked license bytes invalid.' }
            $visualEvidence+=@{pack=$receipt.pack;id=$sample.id;ref=$sample.ref;bytes=$bytes.Length;license=$sample.license;originalDigestVerified=$true;linkedLicenseVerified=$true}
        }
        $searchUri='pegasus-asset://v2/search/knowledge.shared?q=visual.fluent-emoji&limit=4'
        $pages=0;$timer=[Diagnostics.Stopwatch]::StartNew()
        do {
            $search=Read-VisualJson (Invoke-McpRequest (++$requestId) 'resources/read' @{uri=$searchUri})
            if ($search.namespace -ne 'knowledge.shared' -or $search.items.Count -gt 4 -or $search.scanned -gt 512) { throw 'Discovery lost its bounds.' }
            $searchUri=$search.nextUri;$pages++
            if ($pages -gt 128) { throw 'Visual search scan exceeded its explicit bound.' }
        } while ($search.items.Count -eq 0 -and $searchUri)
        $timer.Stop()
        if ($search.items.Count -eq 0 -or @($search.items|Where-Object {$_.id -notlike 'visual.fluent-emoji.*'}).Count -ne 0) { throw 'Visual-library keyword discovery failed.' }
        $visualEvidence+=@{keywordSearch=$true;pages=$pages;returned=$search.items.Count;elapsedMs=$timer.ElapsedMilliseconds}
    }
    if ($AssetChecks) {
        # Read the explicitly imported baseline only. This check never imports,
        # renders, executes, repairs, or scans arbitrary user files.
        function Read-AssetJson($Result) {
            $texts = @($Result.contents | Where-Object { $null -ne $_.text })
            if ($texts.Count -ne 1) { throw 'Expected one bounded asset JSON response.' }
            $data = $texts[0].text | ConvertFrom-Json -Depth 30
            if ($data.contract -ne 'pegasus.assets/2' -or $data.ok -eq $false) {
                throw 'Installed asset resource failed its contract.'
            }
            return $data
        }
        # Other optional packs may now coexist. Follow bounded pages until the
        # baseline identities are found; never require an otherwise empty store.
        $baselineUri='pegasus-asset://v2/search/knowledge.shared?q=pegasus.chat-icons&limit=16'
        $baselineItems=@();$pageNumber=0
        do {
            $catalog=Read-AssetJson (Invoke-McpRequest (2000 + $pageNumber) 'resources/read' @{uri=$baselineUri})
            if ($catalog.namespace -ne 'knowledge.shared') { throw 'Asset namespace echo mismatch.' }
            $baselineItems+=@($catalog.items)
            $baselineUri=$catalog.nextUri
            $pageNumber++
            if ($pageNumber -gt 128) { throw 'Baseline search exceeded its explicit scan bound.' }
        } while ($baselineUri -and $baselineItems.Count -lt 14)
        $expectedIds = @('status.success','status.error','status.warning','status.working',
            'status.info','status.cancelled','status.awaiting','chat.message','chat.assistant',
            'chat.tool','chat.attachment') | ForEach-Object { 'pegasus.chat-icons.' + $_ }
        $icons = @($baselineItems | Where-Object mimeType -eq 'image/svg+xml')
        if ($baselineItems.Count -ne 14 -or $icons.Count -ne 11 -or
            @(Compare-Object $expectedIds @($icons.id)).Count -ne 0) {
            throw 'Installed shared asset catalog did not contain the exact baseline.'
        }
        $selected=@($baselineItems | Where-Object id -eq 'pegasus.chat-icons.status.success')[0]
        if ($selected.id -ne 'pegasus.chat-icons.status.success') {
            throw 'Installed icon metadata search did not select the expected identity.'
        }
        $metadata = Read-AssetJson (Invoke-McpRequest 22 'resources/read' @{uri=$selected.uri})
        if ($metadata.namespace -ne 'knowledge.shared' -or $metadata.id -ne $selected.id -or
            $metadata.ref -ne $selected.ref -or $metadata.pack -ne 'pegasus.chat-icons' -or
            $metadata.packVersion -ne '1.0.0' -or $metadata.license -ne 'MIT' -or
            $metadata.source -notlike 'https://github.com/microsoft/fluentui-system-icons/blob/cd332ec1f90cda2fad16f86754f8da52f1e9ab30/*') {
            throw 'Pinned metadata lost identity or source/licensing provenance.'
        }
        $original = Invoke-McpRequest 23 'resources/read' @{uri=$metadata.bytesUri}
        $blobs = @($original.contents | Where-Object { $null -ne $_.blob })
        if ($blobs.Count -ne 1) { throw 'Expected one selected original-byte response.' }
        $bytes = [Convert]::FromBase64String($blobs[0].blob)
        $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        if (-not $original._meta.complete -or $original._meta.namespace -ne 'knowledge.shared' -or
            $original._meta.id -ne $selected.id -or $original._meta.ref -ne $selected.ref -or
            $bytes.Length -ne $metadata.size -or $digest -ne $metadata.sha256 -or
            $digest -ne $original._meta.contentSha256 -or $digest -ne $original._meta.sha256) {
            throw 'Selected original bytes did not match their pinned metadata and digest.'
        }
        $assetEvidence = @{namespace='knowledge.shared'; references=14; icons=11;
            keywordSearch=$true; pinnedMetadata=$true; originalDigestVerified=$true;
            selectedId=$selected.id; selectedRef=$selected.ref; selectedBytes=$bytes.Length;
            coordinates=@($baselineItems | Sort-Object id | ForEach-Object { @{id=$_.id; ref=$_.ref} })}
    }
    if ($CompactReceiptChecks) {
        $diagText = $diag.content.text -join "`n"
        if ($diagText -notmatch '^OK ([0-9a-f]{32})\r?\n') { throw 'Installed diag did not use the compact receipt-first success contract.' }
        # Health/monitor snapshots intentionally do not create durable history.
        # Use one harmless durable filesystem read to exercise exact job-log access.
        $stat = Invoke-McpRequest 6 'tools/call' @{name='pao';arguments=@{op='stat';src=$item.FullName;output='agent'}}
        $statText = $stat.content.text -join "`n"
        if ($stat.isError -or $statText -notmatch '^OK ([0-9a-f]{32})\r?\n') { throw 'Installed stat did not produce its bounded receipt.' }
        $targetJid = $Matches[1]
        $log = Invoke-McpRequest 7 'tools/call' @{name='pao';arguments=@{op='job_log';jid=$targetJid;max=256;output='agent'}}
        $logLines = ($log.content.text -join "`n") -split '\r?\n', 2
        if ($log.isError -or $logLines[0] -notmatch '^OK [0-9a-f]{32}$' -or $null -ne $log.structuredContent) {
            throw 'The installed ordinary-user job_log route failed.'
        }
        $logData = $logLines[1] | ConvertFrom-Json
        if ($logData.jid -ne $targetJid -or $logData.state -ne 'done' -or
            $null -ne $logData.out -or $null -ne $logData.err_tail) {
            throw 'Installed job_log lost the exact target or manufactured output.'
        }
        $rejected = Invoke-McpRequest 8 'tools/call' @{name='pao';arguments=@{op='diag';src='unused';output='agent'}}
        $rejectedText = $rejected.content.text -join "`n"
        if (-not $rejected.isError -or $rejectedText -notmatch '^FAIL no-receipt ' -or
            -not $rejectedText.Contains('execution=not_dispatched') -or $null -ne $rejected.structuredContent) {
            throw 'Installed pre-admission error did not use the compact failure contract.'
        }
        # This intentionally exceeds the registered argument bound, so no external process is launched.
        $bounded = Invoke-McpRequest 9 'tools/call' @{name='pao';arguments=@{
            op='run';cfg='dotnet';arg=@(('x' * 16385));cwd=(Split-Path -Parent $PSScriptRoot);tmo=30;output='agent'
        }}
        $boundedText = $bounded.content.text -join "`n"
        if (-not $bounded.isError -or $boundedText -notmatch '^FAIL [0-9a-f]{32} invalid' -or
            -not $boundedText.Contains('field=arg[0] actual=16385 max=16384 unit=utf8_bytes') -or
            -not $boundedText.Contains('execution=not_dispatched') -or
            [Text.Encoding]::UTF8.GetByteCount($boundedText) -gt 4096 -or $null -ne $bounded.structuredContent) {
            throw 'Installed registered-argument validation lost bounded typed failure evidence.'
        }
        $structured = Invoke-McpRequest 10 'tools/call' @{name='pao';arguments=@{op='diag';output='structured'}}
        if ($structured.isError -or -not $structured.structuredContent.ok -or
            $null -eq $structured.structuredContent.jid -or $null -eq $structured.structuredContent.rid) {
            throw 'Installed structured receipt compatibility failed.'
        }
    }
    $memorySamples = @()
    $resident = $null
    if ($RequireResidentLifetime) {
        $expectedHost = Join-Path (Split-Path -Parent $item.FullName) 'bin\host\PegasusCortex.Host.exe'
        $hosts = @(Get-Process -Name 'PegasusCortex.Host' -ErrorAction SilentlyContinue | Where-Object {
            $_.Path -eq $expectedHost -and $_.SessionId -eq (Get-Process -Id $PID).SessionId
        })
        if ($hosts.Count -ne 1) { throw 'Expected exactly one installed resident host in the current session.' }
        $resident = $hosts[0]
        $residentStart = $resident.StartTime.ToUniversalTime()
        $memorySamples += @{iterations=0; privateBytes=$resident.PrivateMemorySize64; workingSetBytes=$resident.WorkingSet64}
    }
    for ($iteration = 1; $iteration -le $ReadOnlyIterations; $iteration++) {
        $read = Invoke-McpRequest (100 + $iteration * 2) 'tools/call' @{name='cortex_status';arguments=@{}}
        $health = Invoke-McpRequest (101 + $iteration * 2) 'tools/call' @{name='pao';arguments=@{op='diag';output='agent'}}
        if ($read.isError -or $health.isError) { throw 'A repeated bounded read/health operation failed.' }
        if ($resident -and ($iteration % 25 -eq 0 -or $iteration -eq $ReadOnlyIterations)) {
            $resident.Refresh()
            $memorySamples += @{iterations=$iteration; privateBytes=$resident.PrivateMemorySize64; workingSetBytes=$resident.WorkingSet64}
        }
    }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(10000)) { throw 'Cortex MCP did not exit after EOF.' }
    if ($process.ExitCode -ne 0) { throw 'Cortex MCP exit failed.' }
    if ($RequireResidentLifetime) {
        # STDIO EOF must release only this adapter, never its shared resident host.
        # Recheck the exact PID/start/image, not a replacement process with the same name.
        $resident.Refresh()
        if ($resident.HasExited -or $resident.StartTime.ToUniversalTime() -ne $residentStart -or $resident.Path -ne $expectedHost) {
            throw 'The shared resident host exited with its transient MCP adapter.'
        }
    }
    $receipt = [pscustomobject]@{
        ok=$true; version=$initialized.serverInfo.version; tools=@($listed.tools.name)
        appAndNsisOperations=$true; statusRead=$true; agentContentOnly=$true
        diagUtf8Bytes=[Text.Encoding]::UTF8.GetByteCount(($diag.content.text -join "`n"))
        compactReceiptChecks=[bool]$CompactReceiptChecks
        stderrUtf8Bytes=[Text.Encoding]::UTF8.GetByteCount($errors.GetAwaiter().GetResult()); exitCode=$process.ExitCode
        residentLifetimeChecked=[bool]$RequireResidentLifetime; readOnlyIterations=$ReadOnlyIterations
        residentPid=if ($resident) {$resident.Id} else {$null}; memorySamples=$memorySamples
        assets=$assetEvidence
        visualLibraries=$visualEvidence
    } | ConvertTo-Json -Compress -Depth 4
    if ($EvidenceFile) {
        if (-not [IO.Path]::IsPathFullyQualified($EvidenceFile)) { throw 'Evidence path must be absolute.' }
        for ($path = [IO.Path]::GetFullPath($EvidenceFile); $path; $path = [IO.Path]::GetDirectoryName($path)) {
            if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Evidence reparse path rejected.' }
        }
        [IO.File]::WriteAllText($EvidenceFile, $receipt, [Text.UTF8Encoding]::new($false))
    }
    $receipt
} finally {
    if ($process.Id -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }
    $process.Dispose()
    if ($resident) { $resident.Dispose() }
}
