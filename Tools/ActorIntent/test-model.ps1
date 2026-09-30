param(
    [string]$BaseUrl = 'http://384.168.114.175:8888',
    [string]$Model = 'qwen3.6-35b-a3b',
    [int]$TimeoutSeconds = 20,
    [string]$OutputDirectory,
    [string]$ProtocolExport,
    [ValidateSet('draft','signal')][string[]]$Scope
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $projectRoot ('Logs\actor-intent-model-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[void](New-Item -ItemType Directory -Force -Path $OutputDirectory)
$exportPath = Join-Path $OutputDirectory 'production-protocol.json'
if ($ProtocolExport) {
    $candidatePath = [IO.Path]::GetFullPath($ProtocolExport)
    if ($candidatePath -eq $exportPath) { throw 'Candidate export must be separate from the result directory.' }
    [IO.File]::WriteAllBytes($exportPath, [IO.File]::ReadAllBytes($candidatePath))
} else {
    & (Join-Path $PSScriptRoot 'run-regression.ps1') -ExportProtocol $exportPath
    if ($LASTEXITCODE -ne 0) { throw 'Deterministic checks or production protocol export failed.' }
}
$export = Get-Content -LiteralPath $exportPath -Raw -Encoding UTF8 | ConvertFrom-Json
$resultPath = Join-Path $OutputDirectory 'responses.jsonl'
$utf8 = New-Object Text.UTF8Encoding($false)
if ($Scope -and $Scope.Count) {
    $export.cases = @($export.cases | Where-Object { $Scope -contains $_.scope })
    if (-not $export.cases.Count) { throw 'No cases remain in the selected scope.' }
    [IO.File]::WriteAllText($exportPath, ($export | ConvertTo-Json -Depth 64), $utf8)
}
[IO.File]::WriteAllText($resultPath, '', $utf8)
$endpoint = $BaseUrl.TrimEnd('/')
if ($endpoint.EndsWith('/v1')) { $endpoint += '/chat/completions' }
elseif (-not $endpoint.EndsWith('/chat/completions')) { $endpoint += '/v1/chat/completions' }
$run = @{ model = $Model; endpoint = $endpoint; startedUtc = [DateTime]::UtcNow.ToString('o'); cases = @($export.cases).Count; maxTokens = 384; timeoutSeconds = $TimeoutSeconds; protocolSource = $(if ($ProtocolExport) { $candidatePath } else { 'compiled-production-factory' }); note = 'Live role-draft and accepted-user-event semantic evaluation. Does not execute Unity or configure services. Explicit candidate exports are not production validation until landed unchanged.' }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'run.json'), ($run | ConvertTo-Json -Depth 8), $utf8)
foreach ($case in $export.cases) {
    $payload = @{
        model = $Model
        stream = $false
        temperature = 0
        max_tokens = 384
        enable_thinking = $false
        chat_template_kwargs = @{ enable_thinking = $false }
        messages = $case.messages
        response_format = $case.response_format
    }
    $wire = $payload | ConvertTo-Json -Depth 64 -Compress
    [IO.File]::WriteAllText((Join-Path $OutputDirectory ($case.id + '.request.json')), $wire, $utf8)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $result = @{ id = $case.id; scope = $case.scope; raw = ''; envelope = ''; error = ''; elapsedMs = 0 }
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $endpoint -Method Post -ContentType 'application/json; charset=utf-8' -Headers @{ Authorization = 'Bearer ollama' } -Body $utf8.GetBytes($wire) -TimeoutSec $TimeoutSeconds
        $watch.Stop()
        # Windows PowerShell may decode .Content as Latin-1 when JSON lacks a
        # charset. Preserve and decode the original bytes as UTF-8, exactly once.
        $responseBytes = $response.RawContentStream.ToArray()
        [IO.File]::WriteAllBytes((Join-Path $OutputDirectory ($case.id + '.response.json')), $responseBytes)
        $result.envelope = $utf8.GetString($responseBytes)
        $decoded = $result.envelope | ConvertFrom-Json
        if ($decoded.choices -and $decoded.choices[0].message) { $result.raw = [string]$decoded.choices[0].message.content }
        $result.usage = $decoded.usage
        $result.httpStatus = [int]$response.StatusCode
    }
    catch {
        $watch.Stop()
        $result.error = $_.Exception.Message
    }
    $result.elapsedMs = [Math]::Round($watch.Elapsed.TotalMilliseconds, 1)
    [IO.File]::AppendAllText($resultPath, (($result | ConvertTo-Json -Depth 16 -Compress) + [Environment]::NewLine), $utf8)
    Write-Host ($case.id + ' [' + $case.scope + ']: ' + $result.elapsedMs + ' ms' + $(if ($result.error) { ' transport failed' } else { ' response received' }))
}
Write-Host ('Saved live responses: ' + $resultPath)
& (Join-Path $PSScriptRoot 'run-regression.ps1') -ModelResults $resultPath
exit $LASTEXITCODE
