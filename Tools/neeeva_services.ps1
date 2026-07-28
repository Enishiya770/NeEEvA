<#
NeEEvA 本地服务总控。

项目根目录的双击入口：
    start_all.cmd     全部六个服务（LLM 走本地时用）
    start_cloud.cmd   除本地 LLM 外全部（LLM 走云端 DashScope 时用）
    start_chat.cmd    仅对话链路 llm/embed/tts/asr（不含歌声）
    start_llm.cmd     仅本地 LLM，可独立起停
    stop_all.cmd / status.cmd

命令行：
    .\Tools\neeeva_services.ps1              # 启动全部
    .\Tools\neeeva_services.ps1 status        # 只看状态
    .\Tools\neeeva_services.ps1 stop          # 全部停止
    .\Tools\neeeva_services.ps1 restart
    .\Tools\neeeva_services.ps1 stop -Only llm             # 只停本地 LLM
    .\Tools\neeeva_services.ps1 start -Only embed,tts,asr  # 按需组合

设计要点：
  * 幂等——端口已在监听的服务直接跳过，不会重复拉起
  * 全部后台启动，日志写到 Server\RuntimeLogs\（已 gitignore）
  * 停止按端口对应的 PID 精确 kill；不用进程名，避免误杀共用 llama-server.exe
    的 LLM 与嵌入服务
  * llama.cpp 目录可用环境变量 NEEEVA_LLAMACPP_ROOT 覆盖，默认 E:\llamacpp
#>
[CmdletBinding()]
param(
    [ValidateSet('start', 'stop', 'status', 'restart')]
    [string]$Action = 'start',

    # 只操作这些服务（键名见下方 $Services），留空表示全部
    [string[]]$Only = @(),

    # 启动后不等待健康检查，立即返回
    [switch]$NoWait
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$LogDir = Join-Path $ProjectRoot 'Server\RuntimeLogs'
$LlamaRoot = if ($env:NEEEVA_LLAMACPP_ROOT) { $env:NEEEVA_LLAMACPP_ROOT } else { 'E:\llamacpp' }

# 追加给 llama-server 的额外参数（空格分隔）。用途：给歌声服务腾显存。
# qwen3.6 是 MoE，用 --n-cpu-moe N 把 N 层专家权重放内存最划算：
#     setx NEEEVA_LLM_EXTRA_ARGS "--n-cpu-moe 12"
# 24GB 卡上六个服务全开时，LLM 约占 20GB，SeedVC 会因空闲不足 1500MiB 退回 CPU。
$LlmExtraArgs = @()
if ($env:NEEEVA_LLM_EXTRA_ARGS) {
    $LlmExtraArgs = $env:NEEEVA_LLM_EXTRA_ARGS -split '\s+' | Where-Object { $_ }
}

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

# ---------------------------------------------------------------- 服务定义
# 全部统一用 Start-Process 直接拉起目标进程，不经 cmd/bat 中转：
# 用 `& cmd.exe ... *>> log` 调用会自后台化的 .bat 时，PowerShell 会一直等到
# 输出流关闭（而不只是进程退出），被孙进程继承的句柄挂住 → 总控卡死。
$Services = [ordered]@{
    llm = @{
        Name = '本地 LLM qwen3.6'; Port = 8080; Health = '/health'; WaitSec = 240
        File = Join-Path $LlamaRoot 'llama-server.exe'
        Args = @('-m', 'qwen36.gguf', '--mmproj', 'mmproj-Q8_0.gguf',
                 '--host', '127.0.0.1', '--port', '8080',
                 '-c', '16384', '--parallel', '1', '-ngl', '99',
                 '--jinja', '--flash-attn', 'on') + $LlmExtraArgs
        WorkDir = $LlamaRoot
        Note = 'GPU 大户（约 20GB 显存）'
    }
    embed = @{
        Name = '嵌入 bge-m3'; Port = 8090; Health = '/health'; WaitSec = 120
        File = Join-Path $LlamaRoot 'llama-server.exe'
        Args = @('-m', 'bge-m3-Q8_0.gguf', '--embeddings',
                 '--host', '127.0.0.1', '--port', '8090', '-ngl', '99')
        WorkDir = $LlamaRoot
        Note = '情境记忆召回用；不开则退回提及扫描'
    }
    tts = @{
        Name = 'GPT-SoVITS TTS'; Port = 9880; Health = $null; WaitSec = 240
        File = Join-Path $ProjectRoot 'GPT-SoVITS\runtime\python.exe'
        Args = @('api_v2.py', '-a', '127.0.0.1', '-p', '9880', '-c', 'GPT_SoVITS/configs/tts_infer.yaml')
        WorkDir = Join-Path $ProjectRoot 'GPT-SoVITS'
        Note = 'Antoneva 音色'
    }
    asr = @{
        Name = 'SenseVoice ASR/歌唱感知'; Port = 9881; Health = '/health'; WaitSec = 240
        File = 'python'
        Args = @('sensevoice_server.py')
        WorkDir = Join-Path $ProjectRoot 'Server\SenseVoice'
        Note = '首次启动会自动下载模型'
    }
    vc = @{
        Name = 'SeedVC/RVC 歌声转换'; Port = 9882; Health = '/health'; WaitSec = 240
        File = 'powershell.exe'
        Args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'start_seedvc_server.ps1')
        WorkDir = Join-Path $ProjectRoot 'Server\SeedVC'
        Note = '有角色模型时走 rvc-character-v2'
    }
    svs = @{
        Name = 'SoulX 歌声合成'; Port = 9883; Health = '/health'; WaitSec = 300
        File = 'powershell.exe'
        Args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'start_svs_server.ps1')
        WorkDir = Join-Path $ProjectRoot 'Server\SVS'
        Note = '中英粤官方 / 日语为实验性适配'
    }
}

# ---------------------------------------------------------------- 工具函数
function Get-PortPid {
    param([int]$Port)
    $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($conn) { return @($conn.OwningProcess)[0] }
    return $null
}

function Test-PortUp {
    param([int]$Port)
    return $null -ne (Get-PortPid -Port $Port)
}

function Test-ServiceHealthy {
    param($Svc)
    if (-not (Test-PortUp -Port $Svc.Port)) { return $false }
    # 没有 health 端点的服务（GPT-SoVITS）以端口监听为准：
    # api_v2 是先加载模型再起 uvicorn，端口开了就等于就绪
    if (-not $Svc.Health) { return $true }
    try {
        $null = Invoke-RestMethod ("http://127.0.0.1:{0}{1}" -f $Svc.Port, $Svc.Health) -TimeoutSec 3
        return $true
    } catch {
        return $false
    }
}

function Get-VramFreeMiB {
    try {
        $out = & nvidia-smi --query-gpu=memory.free --format=csv,noheader,nounits 2>$null
        if ($LASTEXITCODE -eq 0 -and $out) { return [int](($out | Select-Object -First 1).Trim()) }
    } catch { }
    return $null
}

function Show-Vram {
    try {
        $out = & nvidia-smi --query-gpu=memory.used,memory.total --format=csv,noheader,nounits 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $out) { return }
        $p = ($out | Select-Object -First 1) -split ',\s*'
        $used = [int]$p[0].Trim(); $total = [int]$p[1].Trim(); $free = $total - $used
        Write-Host ''
        Write-Host ("  显存 {0} / {1} MiB（空闲 {2} MiB）" -f $used, $total, $free) -ForegroundColor DarkGray
        # SeedVC 的 cuda_min_free_mib 默认 1500，低于它会退回 CPU（歌声转换明显变慢）
        if ($free -lt 1500) {
            Write-Host '  ⚠ 显存吃紧：歌声转换(9882)会退回 CPU。想让它用 GPU 可给 LLM 留出余量：' -ForegroundColor Yellow
            Write-Host '     setx NEEEVA_LLM_EXTRA_ARGS "--n-cpu-moe 12"   然后 restart' -ForegroundColor DarkGray
            Write-Host '     或用 start_chat.cmd 只起对话链路（不含歌声服务）' -ForegroundColor DarkGray
        }
    } catch { }
}

function Start-One {
    param([string]$Key, $Svc)

    if (Test-PortUp -Port $Svc.Port) {
        Write-Host ("  [跳过] {0,-24} {1} 已在监听" -f $Svc.Name, $Svc.Port) -ForegroundColor DarkGray
        return $false
    }
    if (-not (Test-Path -LiteralPath $Svc.WorkDir)) {
        Write-Host ("  [失败] {0,-24} 目录不存在: {1}" -f $Svc.Name, $Svc.WorkDir) -ForegroundColor Red
        return $false
    }
    # 'python' 之类走 PATH 的命令不做存在性检查，其余检查文件
    if ($Svc.File -match '[\\/]' -and -not (Test-Path -LiteralPath $Svc.File)) {
        Write-Host ("  [失败] {0,-24} 找不到: {1}" -f $Svc.Name, $Svc.File) -ForegroundColor Red
        return $false
    }

    $out = Join-Path $LogDir ("{0}.out.log" -f $Key)
    $err = Join-Path $LogDir ("{0}.err.log" -f $Key)

    try {
        $proc = Start-Process -FilePath $Svc.File -ArgumentList $Svc.Args `
            -WorkingDirectory $Svc.WorkDir `
            -RedirectStandardOutput $out -RedirectStandardError $err `
            -WindowStyle Hidden -PassThru
        Write-Host ("  [启动] {0,-24} 端口 {1}  PID {2}" -f $Svc.Name, $Svc.Port, $proc.Id) -ForegroundColor Cyan
        return $true
    } catch {
        Write-Host ("  [失败] {0,-24} {1}" -f $Svc.Name, $_.Exception.Message) -ForegroundColor Red
        return $false
    }
}

function Stop-One {
    param([string]$Key, $Svc)
    $procId = Get-PortPid -Port $Svc.Port
    if (-not $procId) {
        Write-Host ("  [跳过] {0,-24} 未在运行" -f $Svc.Name) -ForegroundColor DarkGray
        return
    }
    try {
        Stop-Process -Id $procId -Force -ErrorAction Stop
        Write-Host ("  [停止] {0,-24} PID {1}" -f $Svc.Name, $procId) -ForegroundColor Yellow
    } catch {
        Write-Host ("  [失败] {0,-24} 无法停止 PID {1}: {2}" -f $Svc.Name, $procId, $_.Exception.Message) -ForegroundColor Red
    }
}

function Show-Status {
    Write-Host ''
    Write-Host ('{0,-6} {1,-26} {2,-6} {3}' -f '键', '服务', '端口', '状态')
    Write-Host ('-' * 62)
    foreach ($key in $Selected) {
        $svc = $Services[$key]
        $up = Test-PortUp -Port $svc.Port
        if (-not $up) {
            $state = '○ 未启动'; $color = 'DarkGray'
        } elseif (Test-ServiceHealthy -Svc $svc) {
            $state = '● 就绪'; $color = 'Green'
        } else {
            $state = '◐ 启动中'; $color = 'Yellow'
        }
        Write-Host ('{0,-6} {1,-26} {2,-6} ' -f $key, $svc.Name, $svc.Port) -NoNewline
        Write-Host $state -ForegroundColor $color
    }
    Show-Vram
    Write-Host ''
}

function Wait-Ready {
    param([string[]]$Keys)
    if ($Keys.Count -eq 0) { return }
    $maxWait = 0
    foreach ($k in $Keys) { if ($Services[$k].WaitSec -gt $maxWait) { $maxWait = $Services[$k].WaitSec } }
    $deadline = (Get-Date).AddSeconds($maxWait)
    $pending = [System.Collections.ArrayList]@($Keys)

    Write-Host ''
    Write-Host "  等待就绪（模型加载中，最长 $maxWait 秒）..." -ForegroundColor DarkGray
    while ($pending.Count -gt 0 -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        foreach ($k in @($pending)) {
            if (Test-ServiceHealthy -Svc $Services[$k]) {
                Write-Host ("  [就绪] {0}" -f $Services[$k].Name) -ForegroundColor Green
                $pending.Remove($k)
            }
        }
    }
    foreach ($k in $pending) {
        Write-Host ("  [超时] {0} 仍未就绪，看日志: Server\RuntimeLogs\{1}.err.log" -f $Services[$k].Name, $k) -ForegroundColor Red
    }
}

# ---------------------------------------------------------------- 主流程
# 经 powershell.exe -File 传入时，-Only a,b,c 会整个到达为一个字符串，
# 不会被拆成数组——这里统一再切一次逗号，两种调用形式都能用
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$Selected = if ($Only.Count -gt 0) {
    $bad = $Only | Where-Object { -not $Services.Contains($_) }
    if ($bad) { throw ("未知服务键: {0}。可用: {1}" -f ($bad -join ','), ($Services.Keys -join ',')) }
    $Only
} else {
    @($Services.Keys)
}

Write-Host ''
Write-Host "NeEEvA 服务总控 — $Action" -ForegroundColor White

switch ($Action) {
    'status' {
        Show-Status
    }
    'stop' {
        Write-Host ''
        foreach ($key in ($Selected | Sort-Object -Descending)) { Stop-One -Key $key -Svc $Services[$key] }
        Start-Sleep -Seconds 2
        Show-Status
    }
    default {
        if ($Action -eq 'restart') {
            Write-Host ''
            foreach ($key in ($Selected | Sort-Object -Descending)) { Stop-One -Key $key -Svc $Services[$key] }
            Start-Sleep -Seconds 3
        }
        Write-Host ''
        $started = @()
        foreach ($key in $Selected) {
            # 顺序有意为之：先起显存大户 LLM，其余服务多为按需占用显存
            if (Start-One -Key $key -Svc $Services[$key]) { $started += $key }
        }
        if (-not $NoWait) { Wait-Ready -Keys $started }
        Show-Status
        Write-Host '  提示: 停止用 stop_all.cmd（按端口精确停止，不会误杀其他 llama-server）' -ForegroundColor DarkGray
        Write-Host ''
    }
}
