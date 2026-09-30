param(
    [string]$Endpoint = 'http://192.168.114.175:8888/v1/chat/completions',
    [string]$Model = 'qwen3.6-35b-a3b',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('Logs/actor-intent/room-source-live-' + (Get-Date -Format yyyyMMdd-HHmmss)) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[void](New-Item -ItemType Directory -Force -Path $OutputDirectory)
$exportPath = Join-Path $OutputDirectory 'production-cases.json'
& (Join-Path $PSScriptRoot 'Run-Checks.ps1') -ExportModel $exportPath
if ($LASTEXITCODE -ne 0) { throw 'Production factory export or pure checks failed.' }
$export = Get-Content -LiteralPath $exportPath -Raw -Encoding UTF8 | ConvertFrom-Json
$utf8 = New-Object Text.UTF8Encoding($false)
$responses = Join-Path $OutputDirectory 'responses.jsonl'
[IO.File]::WriteAllText($responses, '', $utf8)
foreach ($case in $export.cases) {
    # Matches the real scene's OpenAICompatible branch: compiled production
    # request messages and constrained schema wire format, one final user fence.
    $payload = @{ model=$Model; stream=$false; temperature=0; max_tokens=640; enable_thinking=$false;
        reasoning_effort='none'; chat_template_kwargs=@{enable_thinking=$false; reasoning_effort='none'};
        messages=$case.messages; response_format=$case.response_format }
    $wire = $payload | ConvertTo-Json -Depth 80 -Compress
    [IO.File]::WriteAllText((Join-Path $OutputDirectory ($case.id+'.request.json')), $wire, $utf8)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $result = @{ id=$case.id; raw=''; error=''; elapsedMs=0 }
    try {
        $reply = Invoke-WebRequest -UseBasicParsing -Uri $Endpoint -Method Post -ContentType 'application/json; charset=utf-8' -Headers @{Authorization='Bearer ollama'} -Body $utf8.GetBytes($wire) -TimeoutSec 30
        $body = $reply.RawContentStream.ToArray()
        [IO.File]::WriteAllBytes((Join-Path $OutputDirectory ($case.id+'.response.json')), $body)
        $envelope = $utf8.GetString($body) | ConvertFrom-Json
        $result.raw = [string]$envelope.choices[0].message.content
        $result.finishReason = $envelope.choices[0].finish_reason
        $result.usage = $envelope.usage
    } catch { $result.error = $_.Exception.Message }
    $watch.Stop(); $result.elapsedMs = [Math]::Round($watch.Elapsed.TotalMilliseconds,1)
    [IO.File]::AppendAllText($responses, (($result | ConvertTo-Json -Depth 16 -Compress) + [Environment]::NewLine), $utf8)
    Write-Host ($case.id + ': ' + $result.elapsedMs + ' ms' + $(if ($result.error) {' failed'} else {' received'}))
}
& (Join-Path $PSScriptRoot 'Run-Checks.ps1') -ModelResults $responses
exit $LASTEXITCODE
