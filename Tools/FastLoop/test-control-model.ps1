param(
    [string]$BaseUrl = 'http://192.168.114.175:8888',
    [string]$Model = 'qwen3.6-35b-a3b',
    [string]$DefaultLanguage = 'ja',
    [int]$TimeoutSeconds = 20,
    [string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$ProtocolExport,
    [switch]$SkipEvaluation
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $projectRoot ('Logs\fast-task-control-model-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[void](New-Item -ItemType Directory -Force -Path $OutputDirectory)
$exportPath = Join-Path $OutputDirectory 'production-protocol.json'
if ($ProtocolExport) { Copy-Item -LiteralPath $ProtocolExport -Destination $exportPath -Force }
else {
    & (Join-Path $PSScriptRoot 'run-regression.ps1') -ExportProtocol $exportPath -DefaultLanguage $DefaultLanguage
    if ($LASTEXITCODE -ne 0) { throw 'Deterministic checks or production protocol export failed.' }
}
$export = Get-Content -LiteralPath $exportPath -Raw -Encoding UTF8 | ConvertFrom-Json
$resultPath = Join-Path $OutputDirectory 'responses.jsonl'
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($resultPath, '', $utf8)
$endpoint = $BaseUrl.TrimEnd('/')
if ($endpoint.EndsWith('/v1')) { $endpoint += '/chat/completions' }
elseif (-not $endpoint.EndsWith('/chat/completions')) { $endpoint += '/v1/chat/completions' }
$run = @{ model = $Model; endpoint = $endpoint; startedUtc = [DateTime]::UtcNow.ToString('o'); cases = @($export.cases).Count; defaultLanguage = $DefaultLanguage; note = 'Live semantic evaluation; never executes Unity or controls services.' }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'run.json'), ($run | ConvertTo-Json -Depth 8), $utf8)
foreach ($case in $export.cases) {
    $payload = @{
        model = $Model
        stream = $false
        temperature = 0
        max_tokens = 32
        enable_thinking = $false
        chat_template_kwargs = @{ enable_thinking = $false }
        messages = $case.messages
        response_format = $export.response_format
    }
    $wire = $payload | ConvertTo-Json -Depth 64 -Compress
    [IO.File]::WriteAllText((Join-Path $OutputDirectory ($case.id + '.request.json')), $wire, $utf8)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $result = @{ id = $case.id; raw = ''; error = ''; elapsedMs = 0 }
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $endpoint -Method Post -ContentType 'application/json; charset=utf-8' -Body $utf8.GetBytes($wire) -TimeoutSec $TimeoutSeconds
        $watch.Stop()
        # Windows PowerShell may decode charset-less application/json as Latin-1.
        # Decode original bytes; re-encoding .Content cannot recover damaged text.
        $envelope = [Text.Encoding]::UTF8.GetString($response.RawContentStream.ToArray())
        [IO.File]::WriteAllText((Join-Path $OutputDirectory ($case.id + '.response.json')), $envelope, $utf8)
        $decoded = $envelope | ConvertFrom-Json
        if (-not $decoded.choices -or -not $decoded.choices[0].message) { throw 'Response has no choices[0].message.' }
        $result.raw = [string]$decoded.choices[0].message.content
        $result.envelope = $envelope
        $result.usage = $decoded.usage
        $result.httpStatus = [int]$response.StatusCode
    }
    catch {
        $watch.Stop()
        $result.error = $_.Exception.Message
    }
    $result.elapsedMs = [Math]::Round($watch.Elapsed.TotalMilliseconds, 1)
    [IO.File]::AppendAllText($resultPath, (($result | ConvertTo-Json -Depth 16 -Compress) + [Environment]::NewLine), $utf8)
    Write-Host ($case.id + ': ' + $result.elapsedMs + ' ms' + $(if ($result.error) { ' transport failed' } else { ' response received' }))
}
Write-Host ('Saved live responses: ' + $resultPath)
if ($SkipEvaluation) { exit 0 }
$assembly = Join-Path $projectRoot 'Logs/fast-loop/transport/Regression.dll'
& dotnet $assembly '--evaluate-control' $exportPath $resultPath *> (Join-Path $OutputDirectory 'evaluation.log')
$evaluationCode = $LASTEXITCODE
Get-Content (Join-Path $OutputDirectory 'evaluation.log')
exit $evaluationCode
