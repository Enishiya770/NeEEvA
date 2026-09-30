param(
    [string]$BaseUrl = 'http://192.168.114.175:8888',
    [string]$Model = 'qwen3.6-35b-a3b',
    [int]$TimeoutSeconds = 20,
    [string]$OutputDirectory,
    [switch]$ExportOnly,
    [switch]$LatestSession,
    [string]$ProtocolPath
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('Logs\fast-loop-context-model-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[void](New-Item -ItemType Directory -Force -Path $OutputDirectory)
$exportPath = Join-Path $OutputDirectory 'production-protocol.json'
if ($ProtocolPath) { Copy-Item -LiteralPath $ProtocolPath -Destination $exportPath -Force }
elseif ($LatestSession) { & (Join-Path $PSScriptRoot 'run-regression.ps1') -ExportSession $exportPath }
else { & (Join-Path $PSScriptRoot 'run-regression.ps1') -ExportProtocol $exportPath }
if (-not $ProtocolPath -and $LASTEXITCODE -ne 0) { throw 'Production prompt export failed.' }
if ($ExportOnly) { Write-Host "Export only; no network call: $exportPath"; exit 0 }
$export = Get-Content -LiteralPath $exportPath -Raw -Encoding UTF8 | ConvertFrom-Json
$resultPath = Join-Path $OutputDirectory 'responses.jsonl'
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($resultPath, '', $utf8)
$endpoint = $BaseUrl.TrimEnd('/')
if ($endpoint.EndsWith('/v1')) { $endpoint += '/chat/completions' }
elseif (-not $endpoint.EndsWith('/chat/completions')) { $endpoint += '/v1/chat/completions' }
$run = @{ model = $Model; endpoint = $endpoint; startedUtc = [DateTime]::UtcNow.ToString('o'); cases = @($export.cases).Count; note = 'Production prompt/schema; no Unity action. Rubrics require semantic review, not keyword pass/fail.' }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'run.json'), ($run | ConvertTo-Json -Depth 8), $utf8)
foreach ($case in $export.cases) {
    $payload = @{ model = $Model; stream = $false; temperature = 0; max_tokens = 256; enable_thinking = $false; chat_template_kwargs = @{ enable_thinking = $false }; messages = $case.messages; response_format = $export.response_format }
    $wire = $payload | ConvertTo-Json -Depth 64 -Compress
    [IO.File]::WriteAllText((Join-Path $OutputDirectory ($case.id + '.request.json')), $wire, $utf8)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $result = @{ id = $case.id; error = ''; elapsedMs = 0; envelope = ''; raw = '' }
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $endpoint -Method Post -ContentType 'application/json; charset=utf-8' -Body $utf8.GetBytes($wire) -TimeoutSec $TimeoutSeconds
        $watch.Stop()
        # Windows PowerShell may decode .Content as Latin-1 without charset.
        # Preserve the original bytes and decode exactly once as UTF-8.
        $envelope = $utf8.GetString($response.RawContentStream.ToArray())
        $decoded = $envelope | ConvertFrom-Json
        $result.envelope = $envelope
        $result.raw = $decoded.choices[0].message.content
        $result.usage = $decoded.usage
        $result.httpStatus = [int]$response.StatusCode
    } catch { $watch.Stop(); $result.error = $_.Exception.Message }
    $result.elapsedMs = [Math]::Round($watch.Elapsed.TotalMilliseconds, 1)
    [IO.File]::AppendAllText($resultPath, (($result | ConvertTo-Json -Depth 16 -Compress) + [Environment]::NewLine), $utf8)
    Write-Host ($case.id + ': ' + $result.elapsedMs + ' ms' + $(if ($result.error) { ' transport failed' } else { ' response received' }))
}
& (Join-Path $PSScriptRoot 'run-regression.ps1') -ModelResults $resultPath -Cohort $exportPath
exit $LASTEXITCODE
