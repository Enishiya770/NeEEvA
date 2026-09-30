<#
Starts the services that use the OpenAI-compatible vLLM chat endpoint. LAN
access to 192.168.114.175:8888 is the default; the DGX Spark SSH tunnel is an
explicit fallback. It deliberately does not start the legacy 8080 LLM tunnel
or a local embedding model. The direct-API voice stack keeps GPT-SoVITS,
SenseVoice, and SeedVC/RVC under the same entry point.
# The authenticated ARDY service is exposed only through local port 8093.
#>
[CmdletBinding()]
param(
    [ValidateSet('start', 'stop', 'status')][string]$Action = 'start',
    [switch]$PlanOnly,
    [ValidateSet('1', '2')][string]$Spark = '',
    [ValidateSet('', 'Lan', 'Tunnel')][string]$Transport = '',
    [switch]$InteractiveSsh,
    [string]$ArdyUpstream = ''
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ServiceController = Join-Path $PSScriptRoot 'neeeva_services.ps1'
$ArdyController = Join-Path $PSScriptRoot 'neeeva_remote_ardy.ps1'
$DgxController = Join-Path $PSScriptRoot 'neeeva_dgx_spark.ps1'
$RuntimeLogDir = Join-Path $ProjectRoot 'Server\RuntimeLogs'
$StatePath = Join-Path $RuntimeLogDir 'direct_api_stack.state.json'
$LanChatBaseUrl = 'http://192.168.114.175:8888'
$LanChatUrl = "$LanChatBaseUrl/v1/chat/completions"
$LanModel = 'qwen3.6-35b-a3b'
$ArdyUpstream = if ($ArdyUpstream) { $ArdyUpstream } elseif ($env:NEEEVA_ARDY_UPSTREAM) { $env:NEEEVA_ARDY_UPSTREAM } else { '' }
$ArdyUpstreamRecord = Join-Path $ProjectRoot 'Server\ARDY\runtime\remote-upstream.txt'
if (-not $ArdyUpstream -and (Test-Path -LiteralPath $ArdyUpstreamRecord)) {
    $ArdyUpstream = (Get-Content -LiteralPath $ArdyUpstreamRecord -Raw -ErrorAction SilentlyContinue).Trim()
}
$ArdyArguments = @()

function Read-StackState {
    if (-not (Test-Path -LiteralPath $StatePath)) { return $null }
    try {
        $saved = Get-Content -LiteralPath $StatePath -Raw -ErrorAction Stop | ConvertFrom-Json
        if (-not $saved.schema -or -not $saved.active) { return $null }
        return $saved
    } catch {
        Write-Warning ("Ignoring invalid direct API stack state at {0}: {1}" -f $StatePath, $_.Exception.Message)
        return $null
    }
}

function Save-StackState($Value) {
    New-Item -ItemType Directory -Force -Path $RuntimeLogDir | Out-Null
    $tmp = "$StatePath.tmp.$PID"
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $StatePath -Force
}

function Remove-StackState {
    Remove-Item -LiteralPath $StatePath -Force -ErrorAction SilentlyContinue
}

function Get-Listener([int]$Port) {
    return Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1
}

function Test-Port([int]$Port) {
    return $null -ne (Get-Listener -Port $Port)
}

function Get-UtcNow {
    return (Get-Date).ToUniversalTime().ToString('o')
}

function Set-StateProperty($State, [string]$Name, $Value) {
    if ($State -is [System.Collections.IDictionary]) {
        $State[$Name] = $Value
        return
    }
    if ($null -eq $State.PSObject.Properties[$Name]) {
        $State | Add-Member -MemberType NoteProperty -Name $Name -Value $Value -Force
    } else {
        $State.$Name = $Value
    }
}

$StackState = Read-StackState
# A stop/status invocation with no explicit profile follows the profile that
# start recorded. This prevents the old default (LAN) from stopping a Spark
# SSH tunnel, or vice versa.
if ($StackState -and $Action -in @('start', 'stop', 'status')) {
    if ($Action -in @('stop', 'status')) {
        if ($Transport -and $Transport -ne [string]$StackState.transport) {
            Write-Warning ("Ignoring requested transport {0}; active state records {1}." -f $Transport, $StackState.transport)
        }
        if ($Spark -and $Spark -ne [string]$StackState.spark) {
            Write-Warning ("Ignoring requested Spark {0}; active state records Spark {1}." -f $Spark, $StackState.spark)
        }
        $Transport = [string]$StackState.transport
        $Spark = [string]$StackState.spark
        if ($StackState.ardyUpstream) { $ArdyUpstream = [string]$StackState.ardyUpstream }
    } else {
        if (-not $Transport) { $Transport = [string]$StackState.transport }
        if (-not $Spark) { $Spark = [string]$StackState.spark }
        if (-not $ArdyUpstream -and $StackState.ardyUpstream) { $ArdyUpstream = [string]$StackState.ardyUpstream }
    }
}
if (-not $Transport) { $Transport = 'Lan' }
if (-not $Spark) { $Spark = '1' }
if ($ArdyUpstream) { $ArdyArguments = @('-Upstream', $ArdyUpstream) }
$ProjectGptRoot = Join-Path $ProjectRoot 'GPT-SoVITS'
$KnownGptRoot = 'E:\so-vits\GPT-SoVITS-v2pro-20250604'
$GptCandidates = @()
if ($env:NEEEVA_GPT_SOVITS_ROOT) { $GptCandidates += [IO.Path]::GetFullPath($env:NEEEVA_GPT_SOVITS_ROOT) }
$GptCandidates += $ProjectGptRoot
$GptCandidates += $KnownGptRoot

function Test-GptSovitsRoot([string]$Root) {
    return (Test-Path -LiteralPath (Join-Path $Root 'runtime\python.exe')) -and
        (Test-Path -LiteralPath (Join-Path $Root 'api_v2.py')) -and
        (Test-Path -LiteralPath (Join-Path $Root 'GPT_SoVITS\configs\tts_infer.yaml'))
}

$GptRoot = @($GptCandidates | Where-Object { Test-GptSovitsRoot $_ } | Select-Object -First 1)
if ($GptRoot.Count -gt 0) {
    $GptRoot = [string]$GptRoot[0]
    # Child scripts (including SeedVC) reuse the same portable Python.
    $env:NEEEVA_GPT_SOVITS_ROOT = $GptRoot
} else {
    $GptRoot = if ($env:NEEEVA_GPT_SOVITS_ROOT) { [IO.Path]::GetFullPath($env:NEEEVA_GPT_SOVITS_ROOT) } else { $ProjectGptRoot }
}

function Test-LanChatApi {
    try {
        $models = Invoke-RestMethod "$LanChatBaseUrl/v1/models" -TimeoutSec 5
        $ids = @($models.data | ForEach-Object { [string]$_.id } | Where-Object { $_ })
        if ($ids.Count -eq 0) { throw 'The vLLM /v1/models response contained no model ids.' }
        Write-Host ("  [CHAT] LAN API ready at {0}; models: {1}" -f $LanChatBaseUrl, ($ids -join ', ')) -ForegroundColor Green
        return $true
    } catch {
        Write-Warning ("LAN vLLM is not reachable at {0}: {1}" -f $LanChatBaseUrl, $_.Exception.Message)
        return $false
    }
}

function Set-LanEnvironment {
    [Environment]::SetEnvironmentVariable('NEEEVA_OPENAI_BASE_URL', $LanChatUrl, 'User')
    [Environment]::SetEnvironmentVariable('NEEEVA_OPENAI_MODEL', $LanModel, 'User')
    $env:NEEEVA_OPENAI_BASE_URL = $LanChatUrl
    $env:NEEEVA_OPENAI_MODEL = $LanModel
    Write-Host "  [CHAT] Unity URL override set to $LanChatUrl" -ForegroundColor Cyan
    Write-Host "  [CHAT] Unity model override set to $LanModel" -ForegroundColor Cyan
}

function Test-GptSovitsInstall {
    return Test-GptSovitsRoot $GptRoot
}

function Show-GptSovitsMissing {
    Write-Host ''
    Write-Host '  [MISSING] GPT-SoVITS TTS (text works, but the character cannot speak)' -ForegroundColor Yellow
    Write-Host ("            Checked: {0}" -f $GptRoot) -ForegroundColor DarkGray
    Write-Host '            The repository excludes the roughly 12 GB runtime, models, and private voice.' -ForegroundColor DarkGray
    Write-Host '            Install it in the project GPT-SoVITS directory, or run:' -ForegroundColor DarkGray
    Write-Host '            setx NEEEVA_GPT_SOVITS_ROOT "FULL GPT-SoVITS DIRECTORY"' -ForegroundColor White
}

$ttsInstalled = Test-GptSovitsInstall
# Keep the original voice stack under the same start/stop entry point. The
# service controller reports a missing runtime or model in its own log without
# preventing the other installed services from starting.
$voiceServices = @('tts', 'asr', 'vc')

Write-Host ''
Write-Host "NeEEvA direct-API stack - $Action" -ForegroundColor White
if ($Transport -eq 'Lan') {
    Write-Host "  Chat LLM: direct LAN vLLM through $LanChatBaseUrl/v1" -ForegroundColor Cyan
} else {
    Write-Host '  Chat LLM: DGX Spark through http://127.0.0.1:18080/v1' -ForegroundColor Cyan
}
Write-Host '  Not started: local LLM and bge-m3; the selected chat transport is managed below.' -ForegroundColor DarkGray

if ($PlanOnly) {
    if ($Transport -eq 'Lan') {
        Write-Host '  Transport: LAN (no SSH tunnel)' 
    } else {
        Write-Host ("  DGX Spark: {0} (local API port 18080)" -f $Spark)
    }
    Write-Host ("  Voice services: {0}" -f ($voiceServices -join ', '))
    Write-Host ("  GPT-SoVITS: {0}" -f $(if ($ttsInstalled) { $GptRoot } else { 'not installed' }))
    Write-Host ("  ARDY: authenticated native relay through local 127.0.0.1:8093; upstream: {0}" -f $(if ($ArdyUpstream) { $ArdyUpstream } else { 'not configured (required for start)' }))
    exit 0
}

function Get-VoicePort([string]$Key) {
    switch ($Key) {
        'tts' { return 9880 }
        'asr' { return 9881 }
        'vc'  { return 9882 }
    }
    return $null
}

function Get-RecordedPid($State, [string]$Property, [string]$Key) {
    if ($Key) {
        if ($State.voicePids -and $State.voicePids.$Key) { return [int]$State.voicePids.$Key }
        return $null
    }
    if ($State.$Property) { return [int]$State.$Property }
    return $null
}

function Invoke-StopOwnedStack($State) {
    # Stop only listeners whose PID was recorded after this stack started them.
    # If a listener has changed owners, leave it running and retain state for a
    # later explicit cleanup. This is the guard against killing an unrelated
    # service that reused a port after a crash or manual restart.
    $errors = @()
    $ardyArgs = @()
    if ($State.ardyUpstream) { $ardyArgs = @('-Upstream', [string]$State.ardyUpstream) }

    if ($State.startedArdyRelay -eq $true) {
        $listener = Get-Listener -Port 8093
        $expected = Get-RecordedPid -State $State -Property 'ardyPid'
        if ($listener -and -not $expected) {
            $errors += 'ARDY relay ownership PID is missing; refusing to stop port 8093'
            Write-Warning $errors[-1]
        } elseif ($listener -and $expected -and [int]$listener.OwningProcess -ne $expected) {
            $errors += "ARDY port 8093 is now owned by PID $($listener.OwningProcess), expected $expected"
            Write-Warning $errors[-1]
        } elseif ($listener) {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ArdyController stop @ardyArgs | Out-Host
            $code = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
            if ($code -ne 0) { $errors += "ARDY relay stop failed with exit code $code" }
        }
    }

    $ownedVoice = @($State.ownedVoiceServices | Where-Object { $_ })
    if ($ownedVoice.Count -gt 0) {
        $safeVoice = @()
        foreach ($key in $ownedVoice) {
            $port = Get-VoicePort $key
            $listener = if ($port) { Get-Listener -Port $port } else { $null }
            $expected = Get-RecordedPid -State $State -Property '' -Key $key
            if ($listener -and -not $expected) {
                $errors += "$key ownership PID is missing; refusing to stop port $port"
                Write-Warning $errors[-1]
            } elseif ($listener -and $expected -and [int]$listener.OwningProcess -ne $expected) {
                $errors += "$key port $port is now owned by PID $($listener.OwningProcess), expected $expected"
                Write-Warning $errors[-1]
            } elseif ($listener) {
                $safeVoice += $key
            }
        }
        if ($safeVoice.Count -gt 0) {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ServiceController stop -Only ($safeVoice -join ',') | Out-Host
            $code = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
            if ($code -ne 0) { $errors += "Voice service stop failed with exit code $code" }
        }
    }

    if ($State.startedDgxTunnel -eq $true) {
        $listener = Get-Listener -Port 18080
        $expected = Get-RecordedPid -State $State -Property 'tunnelPid'
        if ($listener -and -not $expected) {
            $errors += 'DGX tunnel ownership PID is missing; refusing to stop port 18080'
            Write-Warning $errors[-1]
        } elseif ($listener -and $expected -and [int]$listener.OwningProcess -ne $expected) {
            $errors += "DGX tunnel port 18080 is now owned by PID $($listener.OwningProcess), expected $expected"
            Write-Warning $errors[-1]
        } elseif ($listener) {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $DgxController stop -Spark ([string]$State.spark) | Out-Host
            $code = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
            if ($code -ne 0) { $errors += "DGX tunnel stop failed with exit code $code" }
        }
    }
    return $errors
}

function Test-StackCleanupComplete($State) {
    if ($State.startedArdyRelay -eq $true -and (Test-Port 8093)) { return $false }
    foreach ($key in @($State.ownedVoiceServices | Where-Object { $_ })) {
        $port = Get-VoicePort $key
        if ($port -and (Test-Port $port)) { return $false }
    }
    if ($State.startedDgxTunnel -eq $true -and (Test-Port 18080)) { return $false }
    return $true
}

if ($Action -eq 'stop') {
    if (-not $StackState) {
        Write-Host '  [STOP] No active direct API stack state; refusing to stop unowned services.' -ForegroundColor Yellow
        exit 0
    }
    Write-Host ("  [STOP] Recorded profile: {0} / Spark {1}" -f $StackState.transport, $StackState.spark) -ForegroundColor Cyan
    $StackState.status = 'stopping'
    Set-StateProperty $StackState 'lastStopAttemptUtc' (Get-UtcNow)
    Save-StackState $StackState
    $stopErrors = @(Invoke-StopOwnedStack $StackState)
    if ($stopErrors.Count -eq 0 -and (Test-StackCleanupComplete $StackState)) {
        Remove-StackState
        Write-Host '  [STOP] Direct API stack ownership cleared.' -ForegroundColor Green
        exit 0
    }
    $StackState.status = 'stop-failed'
    Set-StateProperty $StackState 'stopFailedUtc' (Get-UtcNow)
    Set-StateProperty $StackState 'stopErrors' @($stopErrors)
    Save-StackState $StackState
    Write-Host '  [STOP] Cleanup incomplete; state retained for a safe retry.' -ForegroundColor Red
    exit 1
}

if ($Action -eq 'status') {
    if ($StackState) {
        Write-Host ("  Profile: {0}; Spark {1}; state {2}" -f $StackState.transport, $StackState.spark, $StackState.status) -ForegroundColor Cyan
        Write-Host ("  Owned voice: {0}; tunnel owned: {1}; ARDY relay owned: {2}" -f ((@($StackState.ownedVoiceServices) -join ', ')), $StackState.startedDgxTunnel, $StackState.startedArdyRelay) -ForegroundColor DarkGray
    } else {
        Write-Host '  Profile: no active state (using command-line/default profile)' -ForegroundColor DarkGray
    }
    if ($Transport -eq 'Lan') {
        $dgxExit = if (Test-LanChatApi) { 0 } else { 1 }
    } else {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $DgxController status -Spark $Spark
        $dgxExit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ServiceController status -Only ($voiceServices -join ',')
    $serviceExit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ArdyController status @ArdyArguments
    $ardyExit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    if (-not $ttsInstalled -and -not (Test-Port 9880)) { Show-GptSovitsMissing }
    if ($dgxExit -ne 0) { exit $dgxExit }
    if ($serviceExit -ne 0) { exit $serviceExit }
    if ($ardyExit -ne 0) { exit $ardyExit }
    exit 0
}

# Voice and singing are independent of the remote chat provider. Start every
# locally installed member, recording ownership after each side effect.
if ($StackState -and $StackState.status -in @('active', 'starting', 'stop-failed') -and
    ($StackState.transport -ne $Transport -or [string]$StackState.spark -ne [string]$Spark)) {
    throw "An active direct API stack already uses $($StackState.transport) / Spark $($StackState.spark). Stop it before changing profile."
}
if ($StackState -and $StackState.status -eq 'active' -and
    $StackState.transport -eq $Transport -and [string]$StackState.spark -eq [string]$Spark) {
    if ($ArdyUpstream -and $StackState.ardyUpstream -and $ArdyUpstream.TrimEnd('/') -ne [string]$StackState.ardyUpstream) {
        throw "An active direct API stack already uses ARDY upstream $($StackState.ardyUpstream). Stop it before changing upstream."
    }
    Write-Host '  [START] Direct API stack is already active for this profile; preserving ownership state.' -ForegroundColor DarkGray
    exit 0
}
if ($StackState -and $StackState.status -in @('starting', 'stop-failed', 'cleanup-failed')) {
    throw "Direct API stack state is '$($StackState.status)'. Run stop and resolve cleanup before starting again."
}

$voicePorts = @{ tts = 9880; asr = 9881; vc = 9882 }
$preexistingVoice = @{}
$preexistingVoicePids = @{}
foreach ($key in $voiceServices) {
    $listener = Get-Listener -Port $voicePorts[$key]
    $preexistingVoice[$key] = ($null -ne $listener)
    if ($listener) { $preexistingVoicePids[$key] = [int]$listener.OwningProcess }
}
$preexistingArdy = Get-Listener -Port 8093
$preexistingTunnel = Get-Listener -Port 18080

$newState = [ordered]@{
    schema = 1
    active = $true
    status = 'starting'
    startedUtc = Get-UtcNow
    transport = $Transport
    spark = $Spark
    chatUrl = if ($Transport -eq 'Lan') { $LanChatUrl } else { 'http://127.0.0.1:18080/v1/chat/completions' }
    chatModel = if ($Transport -eq 'Lan') { $LanModel } else { '' }
    ardyUpstream = if ($ArdyUpstream) { $ArdyUpstream } else { '' }
    ownedVoiceServices = @()
    voicePids = @{}
    startedDgxTunnel = $false
    tunnelPid = $null
    startedArdyRelay = $false
    ardyPid = $null
}
Save-StackState $newState

try {
    if ($Transport -eq 'Lan') {
        if (-not (Test-LanChatApi)) { throw 'LAN vLLM is unavailable; no local services were started.' }
        Set-LanEnvironment
    } else {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $DgxController start -Spark $Spark -InteractiveSsh:$InteractiveSsh
        $dgxExit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
        if (-not $preexistingTunnel -and (Test-Port 18080)) {
            $newState.startedDgxTunnel = $true
            $newState.tunnelPid = [int](Get-Listener -Port 18080).OwningProcess
        }
        Save-StackState $newState
        if ($dgxExit -ne 0) { throw "DGX Spark tunnel start failed with exit code $dgxExit" }
    }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ServiceController start -Only ($voiceServices -join ',')
    $serviceExit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    foreach ($key in $voiceServices) {
        $listener = Get-Listener -Port $voicePorts[$key]
        if (-not $preexistingVoice[$key] -and $listener) {
            $newState.ownedVoiceServices += $key
            $newState.voicePids[$key] = [int]$listener.OwningProcess
        }
    }
    Save-StackState $newState
    if ($serviceExit -ne 0) { throw "Voice service start failed with exit code $serviceExit" }
    if (-not $ttsInstalled -and -not (Test-Port 9880)) { Show-GptSovitsMissing }

    Write-Host ''
    Write-Host '  [START] Authenticated remote ARDY on local port 8093...' -ForegroundColor Cyan
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ArdyController start @ArdyArguments
    $ardyExit = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    if (-not $preexistingArdy -and (Test-Port 8093)) {
        $newState.startedArdyRelay = $true
        $newState.ardyPid = [int](Get-Listener -Port 8093).OwningProcess
    }
    Save-StackState $newState
    if ($ardyExit -ne 0) { throw "ARDY relay start failed with exit code $ardyExit" }
    $newState.ardyUpstream = if ($ArdyUpstream) { $ArdyUpstream } else { '' }
    $newState.status = 'active'
    Set-StateProperty $newState 'readyUtc' (Get-UtcNow)
    Save-StackState $newState
    exit 0
} catch {
    Write-Host ("  [START] FAILED: {0}" -f $_.Exception.Message) -ForegroundColor Red
    $cleanupErrors = @(Invoke-StopOwnedStack $newState)
    if ($cleanupErrors.Count -eq 0 -and (Test-StackCleanupComplete $newState)) {
        Remove-StackState
    } else {
        $newState.status = 'cleanup-failed'
        Set-StateProperty $newState 'cleanupFailedUtc' (Get-UtcNow)
        Set-StateProperty $newState 'cleanupErrors' @($cleanupErrors)
        Save-StackState $newState
        Write-Host '  [START] Cleanup incomplete; ownership state retained.' -ForegroundColor Red
    }
    exit 1
}
