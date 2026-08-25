<#
NeEEvA 5090-side llama.cpp service controller.

This file is copied to D:\NeEEvA\services\neeeva_remote_llm.ps1 on the
inference machine. Both servers intentionally bind to loopback; the 4090
reaches them through an SSH local-forward tunnel.
#>
[CmdletBinding()]
param(
    [ValidateSet('start', 'start-llm', 'start-embed', 'stop', 'status', 'restart')]
    [string]$Action = 'status'
)

$ErrorActionPreference = 'Stop'
# Keep the known-good b8919 server/API behavior, but use the official CUDA 13.1
# package that includes native Blackwell (SM120) kernels for the RTX 5090.
$LlamaRoot = 'D:\NeEEvA\llamacpp-b8919-cuda131-sm120'
$LogRoot = 'D:\NeEEvA\logs'
$ServerExe = Join-Path $LlamaRoot 'llama-server.exe'

$Services = [ordered]@{
    llm = @{
        Name = 'qwen3.6 multimodal LLM'
        Port = 8080
        ModelFiles = @('qwen36.gguf', 'mmproj-Q8_0.gguf')
        Args = @(
            '-m', 'qwen36.gguf', '--mmproj', 'mmproj-Q8_0.gguf',
            '--host', '127.0.0.1', '--port', '8080',
            '-c', '49152', '--parallel', '2',
            '--slot-prompt-similarity', '0.8', '-ngl', '99',
            '--jinja', '--flash-attn', 'on'
        )
    }
    embed = @{
        Name = 'bge-m3 embeddings'
        Port = 8090
        ModelFiles = @('bge-m3-Q8_0.gguf')
        Args = @(
            '-m', 'bge-m3-Q8_0.gguf', '--embeddings',
            '--host', '127.0.0.1', '--port', '8090', '-ngl', '99'
        )
    }
}

New-Item -ItemType Directory -Force -Path $LogRoot | Out-Null

function Get-PortPid([int]$Port) {
    $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($listener) { return $listener.OwningProcess }
    return $null
}

function Assert-ServiceFiles($Service) {
    if (-not (Test-Path -LiteralPath $ServerExe)) {
        throw "Missing llama-server: $ServerExe"
    }
    foreach ($name in $Service.ModelFiles) {
        $path = Join-Path $LlamaRoot $name
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing model: $path" }
    }
}

function Start-ServiceProcess([string]$Key, $Service) {
    $existing = Get-PortPid $Service.Port
    if ($existing) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId = $existing"
        if ($process.ExecutablePath -ieq $ServerExe) {
            Write-Output "[$Key] already listening on $($Service.Port), PID $existing"
            return
        }
        throw "Port $($Service.Port) is occupied by PID $existing ($($process.ExecutablePath)); expected $ServerExe"
    }
    Assert-ServiceFiles $Service
    $stdout = Join-Path $LogRoot "$Key.out.log"
    $stderr = Join-Path $LogRoot "$Key.err.log"
    # OpenSSH for Windows places direct children in the session job and kills
    # them when SSH disconnects. Win32_Process.Create runs via the WMI service,
    # so the model server survives the control connection closing.
    $argumentText = $Service.Args -join ' '
    $commandLine = 'cmd.exe /d /c ""{0}" {1} 1>>"{2}" 2>>"{3}""' -f `
        $ServerExe, $argumentText, $stdout, $stderr
    $result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create `
        -Arguments @{ CommandLine = $commandLine; CurrentDirectory = $LlamaRoot }
    if ($result.ReturnValue -ne 0) {
        throw "Win32_Process.Create failed for $Key, code $($result.ReturnValue)"
    }
    Write-Output "[$Key] started PID $($result.ProcessId), port $($Service.Port)"
}

function Stop-ServiceProcess([string]$Key, $Service) {
    $processId = Get-PortPid $Service.Port
    if (-not $processId) {
        Write-Output "[$Key] not running"
        return
    }
    Stop-Process -Id $processId -Force
    Write-Output "[$Key] stopped PID $processId"
}

function Show-Status {
    foreach ($entry in $Services.GetEnumerator()) {
        $processId = Get-PortPid $entry.Value.Port
        $state = if ($processId) { "listening PID $processId" } else { 'stopped' }
        Write-Output "[$($entry.Key)] $state on 127.0.0.1:$($entry.Value.Port)"
    }
    & nvidia-smi --query-gpu=name,memory.used,memory.total,memory.free `
        --format=csv,noheader
}

switch ($Action) {
    'start' {
        foreach ($entry in $Services.GetEnumerator()) {
            Start-ServiceProcess $entry.Key $entry.Value
        }
        Show-Status
    }
    'start-llm' { Start-ServiceProcess 'llm' $Services.llm }
    'start-embed' { Start-ServiceProcess 'embed' $Services.embed }
    'stop' {
        foreach ($entry in @($Services.GetEnumerator())[-1..-($Services.Count)]) {
            Stop-ServiceProcess $entry.Key $entry.Value
        }
        Start-Sleep -Seconds 1
        Show-Status
    }
    'restart' {
        foreach ($entry in @($Services.GetEnumerator())[-1..-($Services.Count)]) {
            Stop-ServiceProcess $entry.Key $entry.Value
        }
        Start-Sleep -Seconds 1
        foreach ($entry in $Services.GetEnumerator()) {
            Start-ServiceProcess $entry.Key $entry.Value
        }
        Show-Status
    }
    'status' { Show-Status }
}
