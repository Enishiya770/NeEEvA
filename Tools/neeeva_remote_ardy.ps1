<#
Starts the authenticated ARDY relay on Unity's existing loopback port.
The upstream must expose the authenticated native motion API (`/health` and
`/v1/motion/...`); pass -Upstream the first time. The non-secret upstream
origin is then persisted for later start/status/stop calls. This controller
never guesses a Spark address and never stores the bearer token in its record.
#>
[CmdletBinding()]
param(
    [ValidateSet('start', 'stop', 'status', 'configure')][string]$Action = 'status',
    [string]$Upstream = ''
)
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$Runtime = Join-Path $ProjectRoot 'Server\ARDY\runtime'
$Logs = Join-Path $ProjectRoot 'Server\RuntimeLogs'
$Credential = Join-Path $Runtime 'remote-token.dpapi'
$UpstreamRecord = Join-Path $Runtime 'remote-upstream.txt'
$Record = Join-Path $Logs 'ardy-remote.process.json'
$OutLog = Join-Path $Logs 'ardy-remote.out.log'
$ErrLog = Join-Path $Logs 'ardy-remote.err.log'
$Proxy = Join-Path $PSScriptRoot 'ardy_remote_proxy.py'
$Local = 'http://127.0.0.1:8093'
$ExplicitUpstream = $Upstream

# Windows PowerShell treats Path/PATH as one key when launching child processes.
$environment = [Environment]::GetEnvironmentVariables('Process')
$pathKeys = @($environment.Keys | Where-Object { $_ -ieq 'Path' })
if ($pathKeys.Count -gt 1) {
    $pathValue = [string]$environment['Path']
    if (-not $pathValue) { $pathValue = [string]$environment[$pathKeys[0]] }
    [Environment]::SetEnvironmentVariable('PATH', $null, 'Process')
    [Environment]::SetEnvironmentVariable('Path', $pathValue, 'Process')
}

function Normalize-Upstream([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $candidate = $Value.Trim().TrimEnd('/')
    $uri = $null
    if (-not [Uri]::TryCreate($candidate, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'http' -or [string]::IsNullOrWhiteSpace($uri.Host) -or
        $uri.AbsolutePath -notin @('', '/') -or $uri.Query -or $uri.Fragment -or $uri.UserInfo) {
        throw "ARDY upstream must be an HTTP origin without a path, query, fragment, or credentials: $Value"
    }
    return $candidate
}

function Read-PersistedUpstream {
    if (-not (Test-Path -LiteralPath $UpstreamRecord)) { return $null }
    $value = (Get-Content -LiteralPath $UpstreamRecord -Raw -ErrorAction Stop).Trim()
    if (-not $value) { return $null }
    return Normalize-Upstream $value
}

function Save-PersistedUpstream([string]$Value) {
    New-Item -ItemType Directory -Force -Path $Runtime | Out-Null
    Set-Content -LiteralPath $UpstreamRecord -Value $Value -Encoding ascii
}

function Get-UpstreamFromCommandLine([string]$CommandLine) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $null }
    $match = [regex]::Match($CommandLine, '--upstream\s+(?:"([^"]+)"|(\S+))')
    if (-not $match.Success) { return $null }
    $value = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
    return Normalize-Upstream $value
}

function Get-RecordedRelay {
    if (-not (Test-Path -LiteralPath $Record)) { return $null }
    try { $saved = Get-Content -LiteralPath $Record -Raw | ConvertFrom-Json }
    catch { throw 'The ARDY relay process record is not valid JSON.' }
    if (-not $saved.pid -or -not $saved.startedUtc) { throw 'The ARDY relay process record is incomplete.' }
    $process = Get-Process -Id ([int]$saved.pid) -ErrorAction SilentlyContinue
    if (-not $process) { return $null }
    if ($process.StartTime.ToUniversalTime().Ticks -ne ([datetime]$saved.startedUtc).ToUniversalTime().Ticks) { return $null }
    $info = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)" -ErrorAction Stop
    if (-not $info -or $info.CommandLine -notlike "*$Proxy*" -or
        $info.CommandLine -notmatch '--port\s+8093(?:\s|$)') {
        throw 'Recorded ARDY relay process does not match its command line.'
    }
    $actual = Get-UpstreamFromCommandLine $info.CommandLine
    if (-not $actual) { throw 'Recorded ARDY relay process has no valid upstream origin.' }
    return [pscustomobject]@{ Process = $process; Upstream = $actual; CommandLine = $info.CommandLine }
}

function Resolve-Upstream([switch]$Require) {
    $candidate = $null
    if (-not [string]::IsNullOrWhiteSpace($ExplicitUpstream)) { $candidate = $ExplicitUpstream }
    elseif ($env:NEEEVA_ARDY_UPSTREAM) { $candidate = $env:NEEEVA_ARDY_UPSTREAM }
    else { $candidate = Read-PersistedUpstream }
    if (-not $candidate) {
        $recorded = Get-RecordedRelay
        if ($recorded) { $candidate = $recorded.Upstream }
    }
    if (-not $candidate) {
        if ($Require) { throw 'No ARDY upstream is configured. Pass -Upstream http://<native-ardy-host>:<port> on the first start.' }
        return $null
    }
    $normalized = Normalize-Upstream $candidate
    Save-PersistedUpstream $normalized
    return $normalized
}

function Get-RelayProcess([string]$ExpectedUpstream = '') {
    $recorded = Get-RecordedRelay
    if (-not $recorded) { return $null }
    if ($ExpectedUpstream -and $recorded.Upstream -ne $ExpectedUpstream) {
        throw "The managed ARDY relay points to $($recorded.Upstream), not $ExpectedUpstream. Pass the matching -Upstream or stop it explicitly."
    }
    return $recorded.Process
}

function Get-Listener {
    return Get-NetTCPConnection -LocalPort 8093 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Assert-RelayOwner($Process, [string]$ExpectedUpstream) {
    $listener = Get-Listener
    if (-not $listener -or $listener.OwningProcess -ne $Process.Id -or $listener.LocalAddress -ne '127.0.0.1') {
        throw 'Port 8093 is not owned by the recorded loopback ARDY relay.'
    }
    $identity = Invoke-RestMethod "$Local/_relay/health" -TimeoutSec 3
    if ($identity.service -ne 'neeeva-ardy-remote-relay' -or $identity.upstream -ne $ExpectedUpstream) {
        throw 'Port 8093 is not the configured ARDY relay.'
    }
}

function Get-RemoteHealth([switch]$RequireVerified) {
    $health = Invoke-RestMethod "$Local/health" -TimeoutSec 8
    if ($health.service -ne 'neeeva-ardy-motion' -or $health.ready -ne $true -or
        $health.backend.ready -ne $true -or $health.backend.newFrames -ne 40 -or
        $health.backend.historyFrames -ne 16 -or $health.backend.languageModelsLoaded -ne $false -or
        $health.features.source -ne 'live-qwen') {
        throw 'Remote ARDY health contract does not match Unity.'
    }
    if ($RequireVerified -and $health.features.verified -ne $true) {
        throw 'Remote ARDY features are not verified.'
    }
    return $health
}

function Show-Health($Health) {
    Write-Host "[ardy-remote] ready at $Local; adapter $($Health.backend.adapter)" -ForegroundColor Green
    if ($Health.features.verified -eq $true) {
        Write-Host '[ardy-remote] shared Qwen motion features verified.' -ForegroundColor Green
    } else {
        Write-Host '[ardy-remote] native ARDY is ready; features will be verified on the first real motion request.' -ForegroundColor Yellow
    }
    if ($Health.backend.approvedForRuntime -ne $true) {
        Write-Host '[ardy-remote] Adapter is not approved for runtime; motion quality remains experimental.' -ForegroundColor Yellow
    }
}

function Find-Python {
    $candidates = @()
    if ($env:NEEEVA_GPT_SOVITS_ROOT) { $candidates += (Join-Path $env:NEEEVA_GPT_SOVITS_ROOT 'runtime\python.exe') }
    $candidates += (Join-Path $ProjectRoot 'GPT-SoVITS\runtime\python.exe')
    $candidates += 'E:\so-vits\GPT-SoVITS-v2pro-20250604\runtime\python.exe'
    foreach ($candidate in $candidates) { if (Test-Path -LiteralPath $candidate) { return $candidate } }
    $command = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw 'Python is required for the ARDY relay; install GPT-SoVITS or provide python.exe on PATH.'
}

function Get-Token {
    if (-not (Test-Path -LiteralPath $Credential)) {
        throw 'ARDY token is not configured. Run Tools\neeeva_remote_ardy.ps1 configure once.'
    }
    $secure = (Get-Content -LiteralPath $Credential -Raw).Trim() | ConvertTo-SecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    if ($token -notmatch '^[A-Za-z0-9._~-]{16,512}$') { throw 'ARDY token file is invalid.' }
    return $token
}

try {
    if ($Action -eq 'configure') {
        New-Item -ItemType Directory -Path $Runtime -Force | Out-Null
        $secure = Read-Host 'ARDY Bearer token' -AsSecureString
        $secure | ConvertFrom-SecureString | Set-Content -LiteralPath $Credential -Encoding ascii
        Write-Host '[ardy-remote] Token encrypted for the current Windows user.'
        exit 0
    }

    $Upstream = Resolve-Upstream -Require:($Action -eq 'start')
    $process = Get-RelayProcess $Upstream
    if ($Action -eq 'stop') {
        if ($process) {
            Assert-RelayOwner $process $Upstream
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            if (-not $process.WaitForExit(5000)) { throw 'ARDY relay did not stop.' }
            Remove-Item -LiteralPath $Record -ErrorAction SilentlyContinue
            Write-Host '[ardy-remote] stopped'
        } else { Write-Host '[ardy-remote] no managed relay is running' }
        exit 0
    }
    if ($Action -eq 'status') {
        if (-not $process) {
            if ($Upstream) { Write-Host "[ardy-remote] stopped; upstream $Upstream" }
            else { Write-Host '[ardy-remote] stopped; no upstream configured' }
            exit 0
        }
        Assert-RelayOwner $process $Upstream
        $health = Get-RemoteHealth
        Show-Health $health
        exit 0
    }

    $started = $false
    if (-not $process) {
        if (Get-Listener) { throw 'Port 8093 is already in use by another process; no service was replaced.' }
        $python = Find-Python
        $token = Get-Token
        New-Item -ItemType Directory -Force -Path $Logs | Out-Null
        [Environment]::SetEnvironmentVariable('NEEEVA_ARDY_BEARER_TOKEN', $token, 'Process')
        try {
            $process = Start-Process -FilePath $python -ArgumentList @('-u', $Proxy, '--upstream', $Upstream, '--port', '8093') `
                -WorkingDirectory $ProjectRoot -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput $OutLog -RedirectStandardError $ErrLog
        } finally {
            [Environment]::SetEnvironmentVariable('NEEEVA_ARDY_BEARER_TOKEN', $null, 'Process')
            $token = $null
        }
        @{pid=$process.Id; startedUtc=$process.StartTime.ToUniversalTime().ToString('o'); upstream=$Upstream} |
            ConvertTo-Json | Set-Content -LiteralPath $Record -Encoding ascii
        $started = $true
    }
    Save-PersistedUpstream $Upstream
    $deadline = (Get-Date).AddSeconds(30)
    do {
        if ($process.HasExited) { throw "ARDY relay exited during startup. See $ErrLog" }
        try {
            Assert-RelayOwner $process $Upstream
            $health = Get-RemoteHealth
            if ($health.features.verified -eq $true) {
                Show-Health $health
                exit 0
            }
            if ($health.backend.ready -eq $true) { break }
        } catch { $lastError = $_.Exception.Message }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    Assert-RelayOwner $process $Upstream
    $health = Get-RemoteHealth
    Show-Health $health
    exit 0
} catch {
    Write-Host "[ardy-remote] FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($started) {
        $ours = Get-RelayProcess $Upstream
        if ($ours) { Stop-Process -Id $ours.Id -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $Record -ErrorAction SilentlyContinue
    }
    exit 1
}
