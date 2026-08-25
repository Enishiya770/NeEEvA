<#
Controls the 5090 LLM stack and the SSH port-forward tunnel from the 4090.
Existing Unity endpoints remain 127.0.0.1:8080 and 127.0.0.1:8090.
#>
[CmdletBinding()]
param(
    [ValidateSet('start', 'stop', 'status', 'restart')]
    [string]$Action = 'status',
    [switch]$KeepRemoteRunning
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RuntimeLogDir = Join-Path $ProjectRoot 'Server\RuntimeLogs'
$PidFile = Join-Path $RuntimeLogDir 'remote_llm_tunnel.pid'
$TunnelOut = Join-Path $RuntimeLogDir 'remote_llm_tunnel.out.log'
$TunnelErr = Join-Path $RuntimeLogDir 'remote_llm_tunnel.err.log'
$RemoteScript = 'D:\NeEEvA\services\neeeva_remote_llm.ps1'

New-Item -ItemType Directory -Force -Path $RuntimeLogDir | Out-Null

function Invoke-Remote([string]$RemoteAction) {
    & ssh -o BatchMode=yes neeeva-5090 `
        "powershell -NoProfile -ExecutionPolicy Bypass -File $RemoteScript $RemoteAction"
    if ($LASTEXITCODE -ne 0) { throw "Remote action '$RemoteAction' failed" }
}

function Get-TunnelProcess {
    if (-not (Test-Path -LiteralPath $PidFile)) { return $null }
    $storedPid = (Get-Content -LiteralPath $PidFile -ErrorAction SilentlyContinue | Select-Object -First 1)
    if ($storedPid -notmatch '^\d+$') { return $null }
    $process = Get-Process -Id ([int]$storedPid) -ErrorAction SilentlyContinue
    if ($process -and $process.ProcessName -eq 'ssh') { return $process }
    return $null
}

function Assert-LocalPortsFree {
    foreach ($port in 8080, 8090) {
        $listener = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($listener) {
            throw "Local port $port is already used by PID $($listener.OwningProcess). Stop the local llama service first."
        }
    }
}

function Start-Tunnel {
    $existing = Get-TunnelProcess
    if ($existing) {
        Write-Output "[tunnel] already running, PID $($existing.Id)"
        return
    }
    Assert-LocalPortsFree
    $arguments = @(
        '-N', '-T', '-o', 'BatchMode=yes',
        '-o', 'ExitOnForwardFailure=yes',
        '-o', 'ServerAliveInterval=15',
        '-o', 'ServerAliveCountMax=3',
        '-L', '8080:127.0.0.1:8080',
        '-L', '8090:127.0.0.1:8090',
        'neeeva-5090'
    )
    $process = Start-Process -FilePath 'ssh.exe' -ArgumentList $arguments `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $TunnelOut -RedirectStandardError $TunnelErr
    Start-Sleep -Seconds 2
    if ($process.HasExited) {
        $detail = if (Test-Path $TunnelErr) { Get-Content $TunnelErr -Raw } else { '' }
        throw "SSH tunnel failed to start: $detail"
    }
    Set-Content -LiteralPath $PidFile -Value $process.Id -Encoding ascii
    Write-Output "[tunnel] started PID $($process.Id)"
}

function Stop-Tunnel {
    $process = Get-TunnelProcess
    if ($process) {
        Stop-Process -Id $process.Id -Force
        Write-Output "[tunnel] stopped PID $($process.Id)"
    } else {
        Write-Output '[tunnel] not running'
    }
    Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
}

function Test-Endpoint([int]$Port) {
    try {
        $response = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 3
        return "ready: $($response | ConvertTo-Json -Compress)"
    } catch {
        return "unavailable: $($_.Exception.Message)"
    }
}

function Wait-Endpoint([int]$Port, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $state = Test-Endpoint $Port
        if ($state -like 'ready:*') { return $state }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    return $state
}

function Wait-EndpointOrThrow([string]$Name, [int]$Port, [int]$TimeoutSeconds) {
    $state = Wait-Endpoint $Port $TimeoutSeconds
    Write-Output "[$Name] $state"
    if ($state -notlike 'ready:*') {
        throw "$Name service did not become ready on port $Port"
    }
}

function Start-RemoteStack {
    # Each WMI-launched CUDA process must outlive the short SSH control
    # session. Wait locally, then start the second service only after the LLM
    # has completed its cold load; this also avoids simultaneous VRAM fitting.
    Invoke-Remote start-llm
    Start-Tunnel
    Wait-EndpointOrThrow 'llm' 8080 300
    Invoke-Remote start-embed
    Wait-EndpointOrThrow 'embed' 8090 120
}

switch ($Action) {
    'start' {
        Start-RemoteStack
    }
    'stop' {
        Stop-Tunnel
        if (-not $KeepRemoteRunning) { Invoke-Remote stop }
    }
    'restart' {
        Stop-Tunnel
        Invoke-Remote stop
        Start-RemoteStack
    }
    'status' {
        $tunnel = Get-TunnelProcess
        $tunnelState = if ($tunnel) { "[tunnel] running PID $($tunnel.Id)" } else { '[tunnel] stopped' }
        Write-Output $tunnelState
        Invoke-Remote status
        Write-Output "[llm] $(Test-Endpoint 8080)"
        Write-Output "[embed] $(Test-Endpoint 8090)"
    }
}
