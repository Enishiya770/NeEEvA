<#
Starts and stops the local SSH tunnel to one of the DGX Spark OpenAI-compatible
model APIs. The Unity scene uses the stable local endpoint on port 18080.
#>
[CmdletBinding()]
param(
    [ValidateSet('start', 'stop', 'status')][string]$Action = 'status',
    [ValidateSet('1', '2')][string]$Spark = '1',
    [switch]$InteractiveSsh
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RuntimeLogDir = Join-Path $ProjectRoot 'Server\RuntimeLogs'
$PidFile = Join-Path $RuntimeLogDir 'dgx_spark_tunnel.pid'
$TunnelOut = Join-Path $RuntimeLogDir 'dgx_spark_tunnel.out.log'
$TunnelErr = Join-Path $RuntimeLogDir 'dgx_spark_tunnel.err.log'
$LocalPort = 18080
$LocalChatUrl = "http://127.0.0.1:$LocalPort/v1/chat/completions"
$DefaultSshKey = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.ssh\id_ed25519_dgx_spark'

$targets = @{
    '1' = [pscustomobject]@{ Alias = 'dgx-spark-1'; RemotePort = 8888; Model = 'qwen3.6-35b-a3b' }
    '2' = [pscustomobject]@{ Alias = 'dgx-spark-2'; RemotePort = 8891; Model = '/home/spark_root_001/models/qwen36-gguf/Qwen3.6-35B-A3B-UD-IQ4_NL_XL.gguf' }
}
$target = $targets[$Spark]

New-Item -ItemType Directory -Force -Path $RuntimeLogDir | Out-Null

function Get-TunnelProcess {
    if (-not (Test-Path -LiteralPath $PidFile)) { return $null }
    $storedPid = Get-Content -LiteralPath $PidFile -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($storedPid -notmatch '^\d+$') { return $null }
    $process = Get-Process -Id ([int]$storedPid) -ErrorAction SilentlyContinue
    if (-not $process -or $process.ProcessName -ne 'ssh') { return $null }
    try { $info = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)" -ErrorAction Stop }
    catch { return $process }
    if ($info.CommandLine -match [regex]::Escape("127.0.0.1:${LocalPort}:127.0.0.1:$($target.RemotePort)") -and
        $info.CommandLine -match [regex]::Escape($target.Alias)) { return $process }
    return $null
}

function Test-LocalPort([int]$Port) {
    return $null -ne (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1)
}

function Test-ModelApi {
    try {
        $response = Invoke-RestMethod "http://127.0.0.1:$LocalPort/v1/models" -TimeoutSec 3
        return $null -ne $response
    } catch { return $false }
}

function Set-ModelEnvironment {
    [Environment]::SetEnvironmentVariable('NEEEVA_OPENAI_MODEL', $target.Model, 'User')
    [Environment]::SetEnvironmentVariable('NEEEVA_OPENAI_BASE_URL', $LocalChatUrl, 'User')
    $env:NEEEVA_OPENAI_MODEL = $target.Model
    $env:NEEEVA_OPENAI_BASE_URL = $LocalChatUrl
    Write-Output "[dgx-spark-$Spark] Unity model override set to $($target.Model)"
    Write-Output "[dgx-spark-$Spark] Unity URL override set to $LocalChatUrl"
}

function Start-Tunnel {
    $existing = Get-TunnelProcess
    if ($existing) {
        Write-Output "[dgx-spark-$Spark] tunnel already running, PID $($existing.Id)"
        return
    }
    if (Test-LocalPort $LocalPort) {
        throw "Local port $LocalPort is already used by another process."
    }

    Write-Output "[dgx-spark-$Spark] forwarding 127.0.0.1:$LocalPort -> $($target.Alias):127.0.0.1:$($target.RemotePort)"
    $sshMode = if ($InteractiveSsh) { 'interactive' } else { 'batch' }
    Write-Output "[dgx-spark] SSH mode: $sshMode"
    $arguments = @(
        '-N', '-T',
        '-o', 'ConnectTimeout=10',
        '-o', 'ExitOnForwardFailure=yes',
        '-o', 'ServerAliveInterval=15',
        '-o', 'ServerAliveCountMax=3'
    )
    if (-not $InteractiveSsh) {
        $arguments += @('-o', 'BatchMode=yes')
    }
    if (Test-Path -LiteralPath $DefaultSshKey) {
        $arguments += @('-i', $DefaultSshKey, '-o', 'IdentitiesOnly=yes')
        Write-Output "[dgx-spark] using SSH key $DefaultSshKey"
    } else {
        Write-Warning "SSH key not found at $DefaultSshKey; relying on the SSH config/agent."
    }
    $arguments += @(
        '-L', "127.0.0.1:$LocalPort`:127.0.0.1:$($target.RemotePort)",
        $target.Alias
    )
    # Run as an independent hidden console process so closing the launcher does
    # not send the inherited console's shutdown signal to the SSH tunnel.
    if ($InteractiveSsh) {
        # The key supplied for the DGX hosts is passphrase-protected. Keep the
        # SSH console visible so the user can enter the passphrase when an
        # agent has not already loaded the key.
        $process = Start-Process -FilePath 'ssh.exe' -ArgumentList $arguments -WindowStyle Normal -PassThru
    } else {
        $process = Start-Process -FilePath 'ssh.exe' -ArgumentList $arguments -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput $TunnelOut -RedirectStandardError $TunnelErr
    }
    Set-Content -LiteralPath $PidFile -Value $process.Id -Encoding ascii

    $deadline = (Get-Date).AddSeconds(90)
    do {
        if ($process.HasExited) {
            $process.Refresh()
            $exitCode = $process.ExitCode
            $detail = if (-not $InteractiveSsh -and (Test-Path $TunnelErr)) {
                Get-Content -LiteralPath $TunnelErr -Raw
            } else { '' }
            throw "SSH tunnel '$($target.Alias)' exited with code $exitCode. $detail"
        }
        if (Test-ModelApi) {
            Write-Output "[dgx-spark-$Spark] model API ready at http://127.0.0.1:$LocalPort/v1"
            return
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    throw "DGX Spark model API did not become ready on local port $LocalPort."
}

function Stop-Tunnel {
    $process = Get-TunnelProcess
    if ($process) {
        Stop-Process -Id $process.Id -Force
        Write-Output "[dgx-spark-$Spark] tunnel stopped, PID $($process.Id)"
    } else {
        Write-Output '[dgx-spark] tunnel not running for the selected target'
    }
    Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
}

function Show-Status {
    $process = Get-TunnelProcess
    if ($process) {
        $api = if (Test-ModelApi) { 'ready' } else { 'unavailable' }
        Write-Output "[dgx-spark-$Spark] tunnel PID $($process.Id); API $api; model $($target.Model)"
    } else {
        Write-Output "[dgx-spark-$Spark] tunnel stopped; model $($target.Model)"
    }
}

switch ($Action) {
    'start' { Start-Tunnel; Set-ModelEnvironment }
    'stop' { Stop-Tunnel }
    'status' { Show-Status }
}
