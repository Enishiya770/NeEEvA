<# Managed local ARDY lifecycle. Uses the existing shared Qwen on local 8080. #>
[CmdletBinding()]
param(
    [ValidateSet('start', 'stop', 'status')][string]$Action = 'status',
    [switch]$NoWait,
    [ValidateRange(5, 600)][int]$WaitSeconds = 240
)
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$LogDir = Join-Path $ProjectRoot 'Server\RuntimeLogs'
$Python = Join-Path $ProjectRoot 'Server\ARDY\.venv\Scripts\python.exe'
$RecordPath = Join-Path $LogDir 'ardy.process.json'
$OutLog = Join-Path $LogDir 'ardy.out.log'
$ErrLog = Join-Path $LogDir 'ardy.err.log'
$BaseUrl = 'http://127.0.0.1:8093'
$FeatureUrl = 'http://127.0.0.1:8080'
$ModulePattern = '(?:^|\s)-m\s+Server\.ARDY\.motion_service\.app(?:\s|$)'

function Get-ManagedProcess {
    if (-not (Test-Path -LiteralPath $RecordPath)) { return $null }
    $record = Get-Content -LiteralPath $RecordPath -Raw | ConvertFrom-Json
    $process = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
    if (-not $process) { return $null }
    # A PID file from before a reboot must never identify an unrelated process.
    if ($process.StartTime.ToUniversalTime().Ticks -ne ([datetime]$record.startedUtc).ToUniversalTime().Ticks) { return $null }
    $info = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)" -ErrorAction Stop
    if ($info.CommandLine -notmatch $ModulePattern) { throw 'Recorded ARDY PID has an unexpected command line.' }
    return $process
}

function Get-Listener {
    # Query failures are not evidence that the port is free.
    return Get-NetTCPConnection -State Listen -ErrorAction Stop |
        Where-Object { $_.LocalPort -eq 8093 } | Select-Object -First 1
}

function Assert-QwenFeatures {
    $contract = 'f30f7b62ee39bfe3c930b44e1d0654b291442653c310d715ad6ae3784eee31a0'
    $requestId = 'ardy-startup-' + [guid]::NewGuid().ToString('N')
    $description = 'A person stands still.'
    $body = @{text=$description; request_id=$requestId; feature_contract=$contract} | ConvertTo-Json
    try {
        $feature = Invoke-RestMethod "$FeatureUrl/neeeva/motion-features" -Method Post `
            -ContentType 'application/json' -Body $body -TimeoutSec 20
    } catch {
        throw "Qwen motion features unavailable: $($_.Exception.Message). Start Tools/neeeva_remote_llm.ps1 start -Mode feature first."
    }
    if ($feature.shared_model -ne $true -or $feature.dimension -ne 2048 -or $feature.embedding.Count -ne 2048 -or
        $feature.feature_contract -ne $contract -or $feature.feature_source -ne 'live-qwen' -or
        $feature.model_sha256 -ne '071ee2a008ec51372f990d8efbea92ec9dd0137974110ef68fbfde429c8c6dd4' -or
        $feature.protocol -ne 'qwen-motion-raw-last-v1' -or $feature.pooling -ne 'last_input_token_raw' -or
        $feature.template -ne "Motion description: {text}`nRepresentation:" -or
        $feature.request_id -ne $requestId -or $feature.text -ne $description -or
        $feature.context_tokens -ne 512 -or $feature.chat_slots -ne 3 -or $feature.chat_context_tokens_per_slot -ne 65536) {
        throw 'Qwen motion feature contract does not match the shared model used by the adapter.'
    }
    $norm = 0.0
    foreach ($value in $feature.embedding) {
        $number = [double]$value
        if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) { throw 'Nonfinite Qwen motion features.' }
        $norm += $number * $number
    }
    if ($norm -le 1e-16) { throw 'Empty Qwen motion features.' }
}

function Get-ReadyHealth {
    $health = Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 3
    if ($health.service -ne 'neeeva-ardy-motion' -or $health.ready -ne $true -or
        $health.backend.languageModelsLoaded -ne $false -or
        $health.features.source -ne 'live-qwen' -or $health.features.baseUrl -ne $FeatureUrl) {
        throw 'Port 8093 is not the ready ARDY service with live shared-Qwen features.'
    }
    $capabilities = Invoke-RestMethod "$BaseUrl/v1/locomotion/capabilities" -TimeoutSec 3
    if ($capabilities.ready -ne $true -or $capabilities.schema -ne 1 -or $capabilities.fps -ne 20 -or
        $capabilities.jointNames.Count -ne 27 -or $capabilities.terrain -ne 'single-flat-support-plane' -or
        $capabilities.languageModelsLoaded -ne $false) { throw 'ARDY room locomotion is not ready.' }
    return $health
}

function Show-Ready {
    param($Health)
    Write-Host "[ardy] ready: $BaseUrl (gestures + room locomotion)" -ForegroundColor Green
    Write-Host "[ardy] shared Qwen: $FeatureUrl; adapter: $($Health.backend.adapterMode); no second language model"
}

function Stop-Managed {
    $process = Get-ManagedProcess
    if (-not $process) {
        if (Get-Listener) { throw 'Port 8093 is running outside this launcher; stop its original console instead.' }
        Write-Host '[ardy] already stopped'
        return
    }
    # Windows venv python.exe can own a real Python child. Verify each child's
    # role and birth time before stopping it, then stop the recorded launcher.
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id)" -ErrorAction Stop)
    foreach ($child in $children) {
        # Start-Process may also attach the Windows console host. Windows owns
        # that host's lifetime; only stop the verified Python service below.
        if ($child.Name -ieq 'conhost.exe' -and
            $child.ExecutablePath -ieq (Join-Path $env:SystemRoot 'System32\conhost.exe')) { continue }
        if ($child.CommandLine -notmatch $ModulePattern) { throw 'Unexpected child of ARDY; refusing to stop it.' }
    }
    foreach ($child in @($children | Where-Object { $_.CommandLine -match $ModulePattern })) {
        $live = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
        if ($live -and [math]::Abs(($live.StartTime.ToUniversalTime() - $child.CreationDate.ToUniversalTime()).TotalSeconds) -lt 1) {
            $live | Stop-Process -Force -ErrorAction Stop
        }
    }
    $stillManaged = Get-ManagedProcess
    if ($stillManaged) { $stillManaged | Stop-Process -Force -ErrorAction Stop }
    if (-not $process.WaitForExit(5000)) { throw 'ARDY has not exited yet; retry after it finishes.' }
    if (Get-Listener) { throw 'Port 8093 remains occupied after stopping the recorded ARDY process.' }
    Remove-Item -LiteralPath $RecordPath -ErrorAction SilentlyContinue
    Write-Host '[ardy] stopped'
}

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$mutex = New-Object Threading.Mutex($false, 'Local\NeEEvA-ARDY-8093-start')
$locked = $false
try {
    try { $locked = $mutex.WaitOne(1000) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another ARDY start/stop is in progress; wait for that command to finish.' }
    if ($Action -eq 'stop') {
        Stop-Managed
    } elseif ($Action -eq 'status') {
        $health = Get-ReadyHealth
        # /health only proves ARDY loaded; probe Qwen freshly, not its cached verified flag.
        Assert-QwenFeatures
        Show-Ready $health
    } else {
        Assert-QwenFeatures
        $process = Get-ManagedProcess
        $listener = Get-Listener
        if ($listener) {
            Show-Ready (Get-ReadyHealth)
            Write-Host '[ardy] reused existing service; no duplicate process started'
        } else {
            if (-not $process) {
                if (-not (Test-Path -LiteralPath $Python)) { throw 'Missing ARDY environment; run Server/ARDY/install_core.ps1.' }
                $env:PYTHONUTF8 = '1'
                $env:PYTHONIOENCODING = 'utf-8'
                # Ordinary app startup validates the active adapter release, if present.
                # Do not force a baseline, candidate, test fixture, or language encoder.
                $process = Start-Process -FilePath $Python `
                    -ArgumentList @('-m', 'Server.ARDY.motion_service.app', '--port', '8093', '--feature-url', $FeatureUrl) `
                    -WorkingDirectory $ProjectRoot -WindowStyle Hidden -PassThru `
                    -RedirectStandardOutput $OutLog -RedirectStandardError $ErrLog
                @{pid=$process.Id; startedUtc=$process.StartTime.ToUniversalTime().ToString('o')} |
                    ConvertTo-Json | Set-Content -LiteralPath $RecordPath -Encoding ascii
                Write-Host "[ardy] starting PID $($process.Id); logs: Server/RuntimeLogs/ardy.err.log"
            } else { Write-Host "[ardy] waiting for existing startup PID $($process.Id)" }
            if ($NoWait) {
                Write-Host '[ardy] startup requested; readiness not checked (-NoWait)'
            } else {
                $deadline = (Get-Date).AddSeconds($WaitSeconds)
                $ready = $null
                do {
                    if ($process.HasExited) { throw "ARDY exited during startup. See $ErrLog" }
                    try { $ready = Get-ReadyHealth } catch { $lastError = $_.Exception.Message }
                    if ($ready) { break }
                    Start-Sleep -Seconds 2
                } while ((Get-Date) -lt $deadline)
                if (-not $ready) { throw "ARDY did not become ready: $lastError. See $ErrLog" }
                Show-Ready $ready
            }
        }
    }
} catch {
    Write-Host "[ardy] FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
exit 0
