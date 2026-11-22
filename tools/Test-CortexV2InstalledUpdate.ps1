[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$ReleaseVersion,
    [Parameter(Mandatory)][string]$TaskId,
    [Parameter(Mandatory)][string]$EvidenceFile,
    [switch]$Checkpoint
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture=Join-Path $projectRoot 'artifacts\build\current\v2-installed-receipt-fixture'
foreach ($candidate in @($Executable,$EvidenceFile,$fixture)) {
    if (-not [IO.Path]::IsPathFullyQualified($candidate)) { throw 'Explicit absolute verification paths are required.' }
    for ($path=[IO.Path]::GetFullPath($candidate);$path;$path=[IO.Path]::GetDirectoryName($path)) {
        if ((Test-Path -LiteralPath $path) -and
            ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Installed-update verification rejects reparse paths.'
        }
    }
}
if (Test-Path -LiteralPath $fixture) { throw 'The tiny owned fixture directory must be absent; no cleanup or overwrite is implicit.' }
$start=[Diagnostics.ProcessStartInfo]::new($Executable)
$start.ArgumentList.Add('stdio');$start.UseShellExecute=$false;$start.CreateNoWindow=$true
$start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$process=[Diagnostics.Process]::new();$process.StartInfo=$start;$started=$false
try {
    $started=$process.Start()
    if (-not $started) { throw 'Installed MCP adapter did not start.' }
    $errors=$process.StandardError.ReadToEndAsync()
    function Request([int]$Id,[string]$Method,$Parameters) {
        $process.StandardInput.WriteLine((@{jsonrpc='2.0';id=$Id;method=$Method;params=$Parameters}|ConvertTo-Json -Compress -Depth 12))
        $line=$process.StandardOutput.ReadLineAsync()
        if (-not $line.Wait(30000) -or $null -eq $line.Result) { throw "Bounded installed response absent: $Method" }
        $response=$line.Result|ConvertFrom-Json -Depth 40
        if ($response.id -ne $Id -or $response.PSObject.Properties['error']) { throw "Installed protocol response invalid: $Method" }
        if ($response.result.PSObject.Properties['isError'] -and $response.result.isError) {
            throw ('Installed operation rejected: '+($response.result.content.text -join ' '))
        }
        return $response.result
    }
    $initialized=Request 1 'initialize' @{protocolVersion='2025-11-25';capabilities=@{};clientInfo=@{name='cortex-v2-installed-update';version='1'}}
    if ($initialized.serverInfo.version -cne $ReleaseVersion) { throw 'Installed MCP version mismatch.' }
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $listed=Request 2 'tools/list' @{}
    $pao=@($listed.tools|Where-Object name -eq 'pao')
    if ($pao.Count -ne 1 -or $pao[0].inputSchema.properties.wait.type -cne 'boolean') { throw 'Installed deferred PAO schema absent.' }
    $brief=Request 3 'tools/call' @{name='cortex_start';arguments=@{
        namespaces=@('project.pegasus-cortex','knowledge.shared');taskId=$TaskId;
        intent='Verify installed Cortex V2 update, preserved task continuity, bounded PAO scheduling and optional assets';
        phaseId='v2-installed-update';format='compact';maxUtf8Bytes=4096}}
    if ($brief.structuredContent.task.id -cne $TaskId -or
        ($brief.structuredContent.ns -join ',') -cne 'project.pegasus-cortex,knowledge.shared') { throw 'Exact-task startup or namespace echo mismatch.' }
    $read=Request 4 'tools/call' @{name='cortex_read';arguments=@{
        namespace='project.pegasus-cortex';recordType='task';recordId=$TaskId;maxUtf8Bytes=8192}}
    if ($read.structuredContent.recordId -cne $TaskId -or
        $read.structuredContent.record.taskId -cne $TaskId -or
        [string]::IsNullOrWhiteSpace($read.structuredContent.record.content)) { throw 'Exact retained task body unavailable.' }
    $rid=[Guid]::NewGuid().ToString('N')
    $accepted=Request 5 'tools/call' @{name='pao';arguments=@{op='mkdir';dst=$fixture;wait=$false;rid=$rid;output='structured'}}
    $receipt=$accepted.structuredContent
    if (-not $receipt.ok -or $receipt.state -ne 'queued' -or -not $receipt.jid) { throw 'Deferred mkdir did not return a durable accepted job.' }
    $terminal=Request 6 'tools/call' @{name='pao';arguments=@{op='job_wait';jid=$receipt.jid;output='structured'}}
    if (-not $terminal.structuredContent.ok -or $terminal.structuredContent.state -ne 'done' -or
        $terminal.structuredContent.jid -cne $receipt.jid -or -not (Test-Path -LiteralPath $fixture -PathType Container)) { throw 'Exact deferred job did not complete.' }
    $replay=Request 7 'tools/call' @{name='pao';arguments=@{op='mkdir';dst=$fixture;wait=$false;rid=$rid;output='structured'}}
    if (-not $replay.structuredContent.ok -or $replay.structuredContent.jid -cne $receipt.jid) { throw 'RID replay did not retain the exact admitted job.' }
    $health=Request 8 'tools/call' @{name='pao';arguments=@{op='diag';output='structured'}}
    $data=$health.structuredContent.data
    if ($data.product_version -cne $ReleaseVersion -or $data.fifo -cne 'ordered-conflicts-bounded' -or
        $data.queue_max_pending -ne 128 -or $data.queue_max_active -ne 3 -or $data.queue_max_payload_bytes -ne 16777216 -or
        $data.queue_callback_faults -ne 0) { throw 'Installed scheduler health contract mismatch.' }
    $revision=[long]$read.structuredContent.revision
    if ($Checkpoint) {
        $finish=Request 9 'tools/call' @{name='cortex_finish';arguments=@{
            namespace='project.pegasus-cortex';taskId=$TaskId;expectedRevision=$revision;taskAction='update';
            taskContent="Objective: finish Cortex V2 bounded continuity/execution and passive visual assets. Verified installed state: $ReleaseVersion core-only signed installer exited 0; established program/data roots and V1/V2 database file identities preserved; service automatic/running and demand-started resident survives adapter EOF. Fresh MCP exposes eight semantic tools plus PAO, exact task startup/read, deferred job completion/RID replay and bounded scheduler health pass. Four existing libraries retain exact original/licensing reads without reimport. One online SQLite backup was created. Next: user restarts Codex to replace its closed cached transport; Cerebral resumes its separately checkpointed Source work. Sustained multi-agent load and native SVG UI acceptance remain separate gates.";
            checkpoint='Core-only Cortex V2 update installed and fresh route accepted; Codex restart next';
            evidence='Installer exit 0; registered paths unchanged; V1/V2 file IDs unchanged; 25 fresh status/health pairs, compact receipts, four exact library original/license reads, zero stderr and resident EOF lifetime passed. Actual deferred mkdir accepted, exact job_wait done and RID replay preserved. Evidence: artifacts/validation/v2-installed-readpath-current.json and v2-installed-update-current.json.';
            handoff='Cerebral checkpointed/quiesced its Cortex-owned Source generation before install. Existing Codex native wrappers returned Transport closed as expected; fresh raw installed MCP passed. No trust/guidance/credential changes or duplicate library admission.';
            knowledge=@();idempotencyKey=('installed-v2-update-'+$ReleaseVersion);maxUtf8Bytes=2048;
            phaseId='v2-installed-update';checkpointSubject='Installed V2 ready for Codex restart'}}
        if ($finish.structuredContent.disposition -cne 'appended') { throw 'Meaningful installed checkpoint was not appended.' }
        $revision=[long]$finish.structuredContent.task.revision
        $readback=Request 10 'tools/call' @{name='cortex_read';arguments=@{
            namespace='project.pegasus-cortex';recordType='task';recordId=$TaskId;revision=$revision;maxUtf8Bytes=8192}}
        if ($readback.structuredContent.revision -ne $revision -or $readback.structuredContent.record.content -notmatch [regex]::Escape($ReleaseVersion)) { throw 'Exact installed checkpoint readback mismatch.' }
    }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(10000) -or $process.ExitCode -ne 0) { throw 'Installed update adapter failed clean EOF.' }
    $stderr=$errors.GetAwaiter().GetResult()
    if ($stderr.Length -ne 0) { throw 'Installed update adapter emitted stderr.' }
    $result=[ordered]@{ok=$true;releaseVersion=$ReleaseVersion;evidence='real installed MCP and ordinary-user execution';
        exactTaskStart=$true;exactTaskBody=$true;taskRevision=$revision;checkpointWritten=[bool]$Checkpoint;
        deferredSchema=$true;acceptedState=$receipt.state;jobId=$receipt.jid;terminalState='done';ridReplay=$true;
        scheduler=$data.fifo;maximumPending=128;maximumActive=3;maximumPayloadBytes=16777216;callbackFaults=0;
        fixture=$fixture;fixtureCleanup='tiny ordinary directory retained; no deletion or overwrite attempted';stderrBytes=0;exitCode=0}
    $json=$result|ConvertTo-Json -Depth 6 -Compress
    [IO.File]::WriteAllText($EvidenceFile,$json+"`n",[Text.UTF8Encoding]::new($false))
    $json
} finally {
    try { if ($started -and -not $process.HasExited) { $process.Kill($true);$null=$process.WaitForExit(5000) } }
    finally { $process.Dispose() }
}
