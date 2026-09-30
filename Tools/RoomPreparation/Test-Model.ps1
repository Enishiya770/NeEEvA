param(
    [string]$Endpoint = 'http://192.168.114.175:8888/v1/chat/completions',
    [string]$Model = 'qwen3.6-35b-a3b',
    [string]$OutputDirectory = 'work/intent-closure-repair-20260926/timing-live'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[void](New-Item -ItemType Directory -Force -Path $OutputDirectory)
$exportPath = Join-Path $OutputDirectory 'production-cases.json'
& (Join-Path $projectRoot 'Tools/ActorIntent/room-source/Run-Checks.ps1') -Output (Join-Path $OutputDirectory 'pure') -ExportModel $exportPath
if ($LASTEXITCODE -ne 0) { throw 'Production factory checks/export failed.' }
$export = Get-Content -LiteralPath $exportPath -Raw -Encoding UTF8 | ConvertFrom-Json
$utf8 = New-Object Text.UTF8Encoding($false)
$rows = @()
foreach ($case in @($export.cases | Where-Object { $_.id -in @('room02', 'room03') })) {
    # Same OpenAICompatible production contract, source factory and full schema;
    # public controlled facts, no persona, no saved scene credential or image.
    $payload = @{model=$Model; stream=$false; temperature=0; max_tokens=640; enable_thinking=$false;
        reasoning_effort='none'; chat_template_kwargs=@{enable_thinking=$false; reasoning_effort='none'};
        messages=$case.messages; response_format=$case.response_format}
    $wire = $payload | ConvertTo-Json -Depth 80 -Compress
    [IO.File]::WriteAllText((Join-Path $OutputDirectory ($case.id+'.request.json')), $wire, $utf8)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $row = [ordered]@{id=$case.id; timeoutSeconds=$export.preparationTimeoutSeconds; elapsedSeconds=0; httpStatus=$null;
        finishReason=$null; intent=$null; origin=$null; evidence=$null; wireComplete=$false; expected=$false; error=$null}
    try {
        $reply = Invoke-WebRequest -UseBasicParsing -Uri $Endpoint -Method Post -ContentType 'application/json; charset=utf-8' -Headers @{Authorization='Bearer ollama'} -Body $utf8.GetBytes($wire) -TimeoutSec $export.preparationTimeoutSeconds
        $body = $reply.RawContentStream.ToArray()
        [IO.File]::WriteAllBytes((Join-Path $OutputDirectory ($case.id+'.response.json')), $body)
        $envelope = $utf8.GetString($body) | ConvertFrom-Json
        $row.httpStatus = [int]$reply.StatusCode
        $message = $envelope.choices[0].message
        $row.finishReason = $envelope.choices[0].finish_reason
        $row.wireComplete = $envelope.choices.Count -eq 1 -and $row.finishReason -eq 'stop' -and $message.role -eq 'assistant' -and
            $message.content -is [string] -and !$message.tool_calls -and !$message.function_call -and !$message.refusal
        if ($row.wireComplete) {
            $decision = $message.content | ConvertFrom-Json
            $row.intent=$decision.intent; $row.origin=$decision.origin; $row.evidence=$decision.evidence
            $row.expected = $decision.intent -in $case.allowedIntents -and $decision.origin -in $case.allowedOrigins -and
                ($decision.intent -eq 'none' -or ($decision.evidence -and $case.input.currentUserText.Contains($decision.evidence)))
        }
    } catch { $row.error = $_.Exception.Message }
    $watch.Stop(); $row.elapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds,6); $rows += [pscustomobject]$row
    Write-Host ($case.id + ': ' + $row.elapsedSeconds + 's, HTTP=' + $row.httpStatus + ', intent=' + $row.intent + ', expected=' + $row.expected)
}
$summary = [ordered]@{passed=($rows.Count -eq 2 -and @($rows | Where-Object { !$_.expected -or !$_.wireComplete -or $_.error }).Count -eq 0);
    count=$rows.Count; results=$rows; checkedAtUtc=[DateTime]::UtcNow.ToString('o');
    scope='Two serial real Spark HTTP calls using compiled production Room contract/schema/current-turn factory and shared 12s budget. PowerShell HTTP transport, not UnityWebRequest; controlled nonvisual current-user facts. No Unity task, no movement, no adoption or audible-latency claim.'}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'http-summary.json'), ($summary | ConvertTo-Json -Depth 20), $utf8)
# Preserve exact wire labels above, then use the runtime's real parser instead
# of letting a test approximation misclassify its documented normalization.
$responseLines = @($rows | ForEach-Object { @{id=$_.id; elapsedMs=$_.elapsedSeconds*1000; error=$_.error} | ConvertTo-Json -Compress })
$responsesPath = Join-Path $OutputDirectory 'responses.jsonl'
[IO.File]::WriteAllLines($responsesPath, $responseLines, $utf8)
& (Join-Path $projectRoot 'Tools/ActorIntent/room-source/Run-Checks.ps1') -Output (Join-Path $OutputDirectory 'pure') -ModelResults $responsesPath -PreparationOnly
exit $LASTEXITCODE
