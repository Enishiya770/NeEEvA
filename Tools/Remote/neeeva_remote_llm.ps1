<#
NeEEvA 5090-side llama.cpp service controller.

This file is copied to D:\NeEEvA\services\neeeva_remote_llm.ps1 on the
inference machine. Both servers intentionally bind to loopback; the 4090
reaches them through an SSH local-forward tunnel.
#>
[CmdletBinding()]
param(
    [ValidateSet('start', 'start-llm', 'start-llm-feature', 'llm-info', 'start-embed', 'stop', 'status', 'restart', 'restart-llm')]
    [string]$Action = 'status'
)

$ErrorActionPreference = 'Stop'
# Keep the known-good b8919 server/API behavior, but use the official CUDA 13.1
# package that includes native Blackwell (SM120) kernels for the RTX 5090.
$LlamaRoot = 'D:\NeEEvA\llamacpp-b8919-cuda131-sm120'
$LogRoot = 'D:\NeEEvA\logs'
$ServerExe = Join-Path $LlamaRoot 'llama-server.exe'
$ChatTemplate = 'D:\NeEEvA\services\qwen36_chat_template.jinja'
$FeatureExe = 'D:\NeEEvA\motion-feature-server\llama-server.exe'
$FeatureLauncher = 'D:\NeEEvA\motion-feature-server\remote_server.ps1'

$Services = [ordered]@{
    llm = @{
        Name = 'qwen3.6 multimodal LLM'
        Port = 8080
        ModelFiles = @('qwen36.gguf', 'mmproj-Q8_0.gguf')
        Args = @(
            '-m', 'qwen36.gguf', '--mmproj', 'mmproj-Q8_0.gguf',
            '--host', '127.0.0.1', '--port', '8080',
            # The GGUF template only renders the first one or two system/developer
            # messages. NeEEvA deliberately places per-turn Skill, memory and
            # evidence blocks immediately before the current user message, so use
            # the checked-in compatible template that preserves those roles.
            '--jinja', '--chat-template-file', $ChatTemplate,
            # 49152 (24576/slot) was sized for the old 4090 where the LLM shared
            # the GPU with the whole voice stack. On the dedicated 5090 that ceiling
            # was costing us memory, not VRAM: 2026-08-25 a 42-turn session trimmed
            # history 22 times and dropped 61 messages, and the character forgot
            # three songs she had saved herself minutes earlier.
            # This model is cheap to extend: only 10 of 40 layers keep a KV cache
            # (hybrid attention), so 20 KiB/token -> 131072 costs 2560 MiB total,
            # up 1600 MiB from 960. Recurrent state (126 MiB) scales with seqs,
            # not context, and n_ctx_train is 262144 so no rope scaling is needed.
            # Three independent caches: main=0, auxiliary=1, turn boundary=2.
            # Keep 65536 tokens per slot (do not shrink conversation memory).
            # +1280 MiB KV vs two slots; 2026-09-03 measured 8476 MiB free before change.
            '-c', '196608', '--parallel', '3',
            '--slot-prompt-similarity', '0.8', '-ngl', '99',
            '--flash-attn', 'on'
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

function Get-LlmInfo {
    $processes = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'qwen_feature_probe.exe' -or
        ($_.Name -like 'llama*.exe' -and $_.CommandLine -match 'qwen36|Qwen3.6')
    })
    if ($processes.Count -gt 1) { throw 'Multiple Qwen processes detected; no automatic process changes are allowed.' }
    if (-not $processes.Count) { return [pscustomobject]@{mode='stopped';port=8080;process_id=$null} }
    $process = $processes[0]
    $mode = if ($process.ExecutablePath -ieq $FeatureExe) { 'feature' } elseif ($process.ExecutablePath -ieq $ServerExe) { 'legacy' } else { 'unknown' }
    $port = $null
    if ($process.CommandLine -match '--port\s+"?(\d+)') { $port = [int]$Matches[1] }
    if ($mode -eq 'unknown' -or $port -notin 8080,8082) { throw 'An unmanaged Qwen process is running; refusing another model load.' }
    return [pscustomobject]@{mode=$mode;port=$port;process_id=$process.ProcessId;executable=$process.ExecutablePath}
}

function Start-FeatureService {
    $info = Get-LlmInfo
    if ($info.mode -eq 'feature') {
        Write-Output "[llm] reusing shared-model feature server PID $($info.process_id), remote port $($info.port)"
        return
    }
    if ($info.mode -ne 'stopped') {
        throw 'The legacy Qwen service is already running. Feature mode does not stop or replace it; perform an explicit idle maintenance switch first.'
    }
    if (-not (Test-Path -LiteralPath $FeatureLauncher)) { throw 'Stage the verified shared-model server before selecting feature mode.' }
    & $FeatureLauncher -Action start -Port 8080
}

function Assert-ServiceFiles($Service) {
    if (-not (Test-Path -LiteralPath $ServerExe)) {
        throw "Missing llama-server: $ServerExe"
    }
    foreach ($name in $Service.ModelFiles) {
        $path = Join-Path $LlamaRoot $name
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing model: $path" }
    }
    if ($Service.Name -eq 'qwen3.6 multimodal LLM' -and
        -not (Test-Path -LiteralPath $ChatTemplate)) {
        throw "Missing chat template: $ChatTemplate"
    }
}

function Start-ServiceProcess([string]$Key, $Service) {
    # Both launchers use this cross-session mutex. A competing startup must
    # become visible in CIM before the lock is released; otherwise two scripts
    # could both observe an idle GPU while a feature startup hashes its model.
    $startMutex = $null
    if ($Key -eq 'llm') {
        $startMutex = New-Object Threading.Mutex($false, 'Global\NeEEvA-Qwen-model-start')
        if (-not $startMutex.WaitOne(30000)) { $startMutex.Dispose(); throw 'Another Qwen startup is in progress; retry after it finishes.' }
    }
    try {
    if ($Key -eq 'llm') {
        $info = Get-LlmInfo
        if ($info.mode -ne 'stopped') {
            Write-Output "[llm] reusing $($info.mode) Qwen PID $($info.process_id), remote port $($info.port); no second model loaded"
            return
        }
    }
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
    if ($Key -eq 'llm') {
        $deadline = (Get-Date).AddSeconds(8)
        do {
            if ((Get-LlmInfo).mode -ne 'stopped') { break }
            Start-Sleep -Milliseconds 100
        } while ((Get-Date) -lt $deadline)
        if ((Get-LlmInfo).mode -eq 'stopped') { throw 'The Qwen launcher did not create its server process; inspect the service logs.' }
    }
    } finally {
        if ($startMutex) { $startMutex.ReleaseMutex(); $startMutex.Dispose() }
    }
}

function Stop-ServiceProcess([string]$Key, $Service) {
    if ($Key -eq 'llm') {
        $info = Get-LlmInfo
        if ($info.mode -eq 'feature') { & $FeatureLauncher -Action stop; return }
    }
    $processId = Get-PortPid $Service.Port
    if (-not $processId) {
        Write-Output "[$Key] not running"
        return
    }
    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $processId"
    if ($process.ExecutablePath -ine $ServerExe) {
        throw "Refusing to stop unexpected process on port $($Service.Port): $processId ($($process.ExecutablePath))"
    }
    $launcherInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.ParentProcessId)"
    $launcher = $null
    if ($launcherInfo.Name -ieq 'cmd.exe' -and $launcherInfo.CommandLine.Contains($ServerExe)) {
        $launcher = Get-Process -Id $launcherInfo.ProcessId -ErrorAction SilentlyContinue
    }
    Stop-Process -Id $processId -Force
    # cmd owns the redirected log handles. A restart racing its exit can fail
    # before llama-server even starts, without adding anything to llm.err.log.
    if ($launcher) {
        try {
            if (-not $launcher.WaitForExit(5000)) { throw 'Old LLM launcher has not released its log handles yet; retry start-llm shortly.' }
        } finally { $launcher.Dispose() }
    }
    Write-Output "[$Key] stopped PID $processId"
}

function Show-Status {
    $info = Get-LlmInfo
    Write-Output "[qwen-mode] $($info.mode), actual remote port $($info.port), PID $($info.process_id)"
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
    'start-llm-feature' { Start-FeatureService }
    'llm-info' { Get-LlmInfo | ConvertTo-Json -Compress }
    'start-embed' { Start-ServiceProcess 'embed' $Services.embed }
    'restart-llm' {
        $info = Get-LlmInfo
        if ($info.mode -ne 'stopped') {
            $slots = Invoke-RestMethod "http://127.0.0.1:$($info.port)/slots" -TimeoutSec 5
            if ($slots.Count -eq 0 -or @($slots | Where-Object { $_.is_processing }).Count -gt 0) {
                throw 'LLM has active requests or no verifiable idle slots; retry after the conversation finishes.'
            }
        }
        Stop-ServiceProcess 'llm' $Services.llm
        Start-Sleep -Seconds 1
        if ($info.mode -eq 'feature') { Start-FeatureService } else { Start-ServiceProcess 'llm' $Services.llm }
        Show-Status
    }
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
