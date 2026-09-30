<#
Runs the remote API calls shown in the setup reference.

This script does not start a process on either remote host. It verifies that
the services are reachable, submits one ARDY/Gradio feature request, waits for
the event result, lists the vLLM models, and sends a small chat request.

Override the defaults with:
  NEEEVA_REMOTE_ARDY_URL  (default http://192.168.114.163:9550)
  NEEEVA_REMOTE_LLM_URL   (default http://192.168.114.175:8888)
  NEEEVA_REMOTE_LLM_MODEL (default qwen3.6-35b-a3b)
#>
[CmdletBinding()]
param(
    [ValidateSet('start', 'status')][string]$Action = 'start',
    [string]$ArdyBaseUrl,
    [string]$ChatBaseUrl,
    [string]$Model,
    [string]$MotionText = 'A person walks and falls to the ground.',
    [string]$MotionFilename = 'embedding.npy',
    [ValidateRange(1, 120)][int]$RequestTimeoutSec = 15,
    [ValidateRange(1, 900)][int]$EventTimeoutSec = 180,
    [switch]$SkipMotion
)

$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
} catch {
    # Older hosts may not expose a writable console encoding.
}
if ([string]::IsNullOrWhiteSpace($ArdyBaseUrl)) {
    $ArdyBaseUrl = if ($env:NEEEVA_REMOTE_ARDY_URL) { $env:NEEEVA_REMOTE_ARDY_URL } else { 'http://192.168.114.163:9550' }
}
if ([string]::IsNullOrWhiteSpace($ChatBaseUrl)) {
    $ChatBaseUrl = if ($env:NEEEVA_REMOTE_LLM_URL) { $env:NEEEVA_REMOTE_LLM_URL } else { 'http://192.168.114.175:8888' }
}
if ([string]::IsNullOrWhiteSpace($Model)) {
    $Model = if ($env:NEEEVA_REMOTE_LLM_MODEL) { $env:NEEEVA_REMOTE_LLM_MODEL } else { 'qwen3.6-35b-a3b' }
}

$ArdyBaseUrl = $ArdyBaseUrl.TrimEnd('/')
$ChatBaseUrl = $ChatBaseUrl.TrimEnd('/')
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$EventLogDir = Join-Path $ProjectRoot 'Server\RuntimeLogs\remote-api'
New-Item -ItemType Directory -Force -Path $EventLogDir | Out-Null

$failures = New-Object System.Collections.Generic.List[string]

function Write-Check([string]$Name, [string]$Message, [ConsoleColor]$Color = [ConsoleColor]::Green) {
    Write-Host ("[{0}] {1}" -f $Name, $Message) -ForegroundColor $Color
}

function Invoke-HttpGet([string]$Uri, [int]$TimeoutSec) {
    return Invoke-WebRequest -UseBasicParsing -Uri $Uri -Method Get -TimeoutSec $TimeoutSec
}

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    try {
        & $Action
        return $true
    } catch {
        $message = $_.Exception.Message
        $failures.Add($Name)
        Write-Check $Name $message Red
        return $false
    }
}

Write-Host ''
Write-Host ("NeEEvA remote API stack - {0}" -f $Action) -ForegroundColor White
Write-Host ("  ARDY/Gradio: {0}" -f $ArdyBaseUrl) -ForegroundColor Cyan
Write-Host ("  Chat API:    {0}/v1" -f $ChatBaseUrl) -ForegroundColor Cyan
Write-Host ("  Model:       {0}" -f $Model) -ForegroundColor Cyan

if ($Action -eq 'status') {
    $SkipMotion = $true
}

# The reference first calls the Gradio job and then reads its event stream.
if (-not $SkipMotion) {
    $script:eventId = $null
    $postOk = Invoke-Step 'ardy-submit' {
        $body = @{ text = $MotionText; filename = $MotionFilename } | ConvertTo-Json -Compress
        $response = Invoke-RestMethod -Uri "$ArdyBaseUrl/gradio_api/call/v2/DemoWrapper" `
            -Method Post -ContentType 'application/json' -Body $body -TimeoutSec $RequestTimeoutSec
        $script:eventId = [string]$response.event_id
        if ([string]::IsNullOrWhiteSpace($script:eventId)) {
            throw 'The Gradio response did not contain event_id.'
        }
        Write-Check 'ardy-submit' ("event_id={0}" -f $script:eventId)
    }

    if ($postOk) {
        Invoke-Step 'ardy-event' {
            $encodedEventId = [Uri]::EscapeDataString($script:eventId)
            $eventUri = "$ArdyBaseUrl/gradio_api/call/v2/DemoWrapper/$encodedEventId"
            $eventResponse = Invoke-HttpGet $eventUri $EventTimeoutSec
            $safeEventId = $script:eventId -replace '[^A-Za-z0-9._-]', '_'
            $eventPath = Join-Path $EventLogDir ("ardy-{0}.response.txt" -f $safeEventId)
            Set-Content -LiteralPath $eventPath -Value $eventResponse.Content -Encoding utf8
            if ([string]::IsNullOrWhiteSpace([string]$eventResponse.Content)) {
                throw 'The Gradio event response was empty.'
            }
            $preview = [string]$eventResponse.Content
            if ($preview.Length -gt 500) { $preview = $preview.Substring(0, 500) + '...' }
            Write-Check 'ardy-event' ("HTTP {0}; response saved to {1}" -f $eventResponse.StatusCode, $eventPath)
            Write-Host $preview -ForegroundColor DarkGray
        } | Out-Null
    }
} else {
    $skipReason = if ($Action -eq 'status') { 'status-only run' } else { '-SkipMotion' }
    Write-Check 'ardy-event' ("skipped ({0})" -f $skipReason) Yellow
}

Invoke-Step 'ardy-root' {
    $response = Invoke-HttpGet "$ArdyBaseUrl/" $RequestTimeoutSec
    Write-Check 'ardy-root' ("HTTP {0}" -f $response.StatusCode)
} | Out-Null

$models = $null
Invoke-Step 'llm-models' {
    $models = Invoke-RestMethod -Uri "$ChatBaseUrl/v1/models" -Method Get -TimeoutSec $RequestTimeoutSec
    $ids = @($models.data | ForEach-Object { [string]$_.id } | Where-Object { $_ })
    if ($ids.Count -eq 0) { throw 'The /v1/models response did not contain any model ids.' }
    Write-Check 'llm-models' ($ids -join ', ')
    if ($ids -notcontains $Model) {
        Write-Check 'llm-models' ("requested model '{0}' was not listed" -f $Model) Yellow
    }
} | Out-Null

Invoke-Step 'llm-chat' {
    # JSON unicode escapes keep this launcher ASCII-safe on Windows PowerShell
    # while preserving the exact Chinese prompt from the reference command.
    $chatBody = '{"model":"' + ($Model -replace '\\', '\\\\' -replace '"', '\\"') + '","messages":[{"role":"user","content":"\u7528\u4e00\u53e5\u8bdd\u4ecb\u7ecd\u4f60\u81ea\u5df1"}],"max_tokens":128,"temperature":0.7}'
    $chatResponse = Invoke-WebRequest -UseBasicParsing -Uri "$ChatBaseUrl/v1/chat/completions" `
        -Method Post -ContentType 'application/json' -Body $chatBody -TimeoutSec $RequestTimeoutSec
    $chatJson = [Text.Encoding]::UTF8.GetString($chatResponse.RawContentStream.ToArray())
    $chat = $chatJson | ConvertFrom-Json
    if ($null -eq $chat.choices -or @($chat.choices).Count -eq 0) {
        throw 'The chat response did not contain choices.'
    }
    $content = [string]$chat.choices[0].message.content
    if ([string]::IsNullOrWhiteSpace($content)) { throw 'The first chat choice was empty.' }
    Write-Check 'llm-chat' $content
} | Out-Null

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host ("FAILED: {0}" -f ($failures -join ', ')) -ForegroundColor Red
    exit 1
}
Write-Host 'All remote API checks passed.' -ForegroundColor Green
exit 0
