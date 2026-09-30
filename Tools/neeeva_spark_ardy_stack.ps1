<##
.SYNOPSIS
    Start, stop, and inspect the Spark LLM/native-ARDY stack.

.DESCRIPTION
    The chat model is reused from the Spark OpenAI-compatible endpoint. The
    native ARDY service is launched over SSH on the selected Spark and bound
    to the private switch (0.0.0.0), so Unity can call it directly. This
    script never treats the legacy Gradio service as ARDY and never kills an
    unrecorded process. A Spark that already owns the LLM is reused; an
    optional explicit remote LLM command can be supplied when the model must
    be started by this stack.
#>
[CmdletBinding()]
param(
    [ValidateSet('start', 'stop', 'status')][string]$Action = 'status',
    [ValidateSet('1', '2')][string]$Spark = '1',
    [string]$ArdySshHost = '192.168.114.163',
    [ValidateRange(1, 65535)][int]$ArdySshPort = 22,
    [string]$LlmSshHost = '192.168.114.175',
    [ValidateRange(1, 65535)][int]$LlmSshPort = 22,
    [string]$SshKeyPath = '',
    [string]$ArdyHost = '',
    [ValidateRange(1, 65535)][int]$ArdyPort = 8093,
    [string]$FeatureUrl = 'http://127.0.0.1:8890',
    [string]$LlmUrl = '',
    [string]$LlmModelName = 'qwen3.6-35b-a3b',
    [string]$RemoteRoot = '/home/spark_root_001/ardy-qwen36',
    [string]$RemotePython = '/home/spark_root_001/ardy-qwen36/venv/bin/python',
    [string]$RemoteCoreDir = '',
    [string]$LlmServiceName = 'qwen36-vllm.service',
    [string[]]$FeatureServiceName = @('llama-motion.service', 'neeeva-motion-features.service'),
    [string]$RemoteLlmStartCommand = '',
    [string]$RemoteLlmStopCommand = '',
    [switch]$AllowIndependentFeatureModel,
    [switch]$AllowUnverifiedAdapter,
    [switch]$ManageFeatureServices = $true,
    [switch]$StopLlm,
    [switch]$SkipDeploy,
    [switch]$NoWait
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$RuntimeLogDir = Join-Path $ProjectRoot 'Server\RuntimeLogs'
$StatePath = Join-Path $RuntimeLogDir 'spark_ardy_stack.state.json'
$SshAlias = "spark_root_001@$ArdySshHost"
if ([string]::IsNullOrWhiteSpace($ArdyHost)) { $ArdyHost = $ArdySshHost }
if ([string]::IsNullOrWhiteSpace($LlmUrl)) {
    $LlmUrl = if ($env:NEEEVA_SPARK_LLM_URL) { $env:NEEEVA_SPARK_LLM_URL } else { 'http://192.168.114.175:8888' }
}
if ([string]::IsNullOrWhiteSpace($RemoteCoreDir)) {
    $RemoteCoreDir = "$RemoteRoot/checkpoints/ARDY-Core-RP-20FPS-Horizon40"
}
if ([string]::IsNullOrWhiteSpace($SshKeyPath)) {
    $SshKeyPath = Join-Path $env:USERPROFILE '.ssh\id_ed25519_dgx_spark'
}
$LlmUrl = $LlmUrl.TrimEnd('/')
$ArdyUrl = "http://$ArdyHost`:$ArdyPort"

New-Item -ItemType Directory -Force -Path $RuntimeLogDir | Out-Null

function Assert-SafeOrigin([string]$Value, [string]$Name) {
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'http' -or [string]::IsNullOrWhiteSpace($uri.Host) -or
        $uri.AbsolutePath -notin @('', '/') -or $uri.Query -or $uri.Fragment -or $uri.UserInfo) {
        throw "$Name must be an HTTP origin without a path, query, fragment, or credentials: $Value"
    }
}

function Bash-Quote([string]$Value) {
    if ($Value -match "['`r`n]") { throw 'Remote paths and origins must not contain single quotes or newlines.' }
    return "'$Value'"
}

function Invoke-NativeText([string]$FilePath, [string[]]$ArgumentList, [string]$FailureMessage) {
    # Resolve before relaxing error handling, so a missing executable still fails.
    $application = Get-Command -Name $FilePath -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $previousPreference = $ErrorActionPreference
    # PowerShell 7 callers may enable exit-code errors; this helper checks them itself.
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        # Windows PowerShell 5.1 turns redirected native stderr into ErrorRecords.
        # A warning must not terminate the call before we can inspect its exit code.
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = $null
        $output = @(& $application.Source @ArgumentList 2>&1)
        $exitCode = $global:LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    $output = @($output | ForEach-Object { $_.ToString() })
    if ($null -eq $exitCode) {
        throw "$FailureMessage (process did not return an exit code): $($output -join [Environment]::NewLine)"
    }
    if ($exitCode -ne 0) {
        throw "$FailureMessage (exit ${exitCode}): $($output -join [Environment]::NewLine)"
    }
    return $output
}

function Invoke-SshText([string]$Command, [string]$SshHost = $ArdySshHost, [int]$Port = $ArdySshPort) {
    # Windows PowerShell 5.1 can strip embedded quotes from native arguments.
    # Send only base64 in the command argument and let Bash decode the UTF-8 script.
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Command.Replace("`r`n", "`n")))
    $remoteCommand = "echo $encodedCommand | base64 -d | bash"
    $sshArgs = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10', '-o', 'ServerAliveInterval=10',
        '-o', 'ServerAliveCountMax=2', '-o', 'IdentitiesOnly=yes', '-i', $SshKeyPath,
        '-p', [string]$Port, "spark_root_001@$SshHost", $remoteCommand)
    Invoke-NativeText 'ssh.exe' $sshArgs "SSH command on spark_root_001@$SshHost failed"
}

function Invoke-Scp([string]$LocalPath, [string]$RemotePath, [string]$SshHost = $ArdySshHost, [int]$Port = $ArdySshPort) {
    $scpArgs = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10', '-o', 'IdentitiesOnly=yes',
        '-i', $SshKeyPath, '-P', [string]$Port, $LocalPath, "spark_root_001@${SshHost}:$RemotePath")
    Invoke-NativeText 'scp.exe' $scpArgs "SCP to spark_root_001@$SshHost failed" | ForEach-Object { Write-Host "[scp] $_" }
}

function Get-HttpJson([string]$Url, [int]$TimeoutSec = 5) {
    try { return Invoke-RestMethod -Uri $Url -TimeoutSec $TimeoutSec }
    catch { return $null }
}

function Get-LlmStatus {
    $models = Get-HttpJson "$LlmUrl/v1/models"
    if (-not $models) {
        return [pscustomobject]@{ ready = $false; url = $LlmUrl; models = @() }
    }
    $ids = @($models.data | ForEach-Object { [string]$_.id } | Where-Object { $_ })
    $ready = if ([string]::IsNullOrWhiteSpace($LlmModelName)) { $ids.Count -gt 0 } else { $ids -contains $LlmModelName }
    return [pscustomobject]@{ ready = $ready; url = $LlmUrl; models = $ids; expectedModel = $LlmModelName }
}

function Get-ArdyStatus {
    $health = Get-HttpJson "$ArdyUrl/health" 8
    if (-not $health) {
        return [pscustomobject]@{ ready = $false; url = $ArdyUrl; health = $null }
    }
    $ready = $health.service -eq 'neeeva-ardy-motion' -and $health.ready -eq $true -and
        $health.backend.ready -eq $true -and $health.backend.newFrames -eq 40 -and
        $health.backend.historyFrames -eq 16
    return [pscustomobject]@{ ready = $ready; url = $ArdyUrl; health = $health }
}

function Save-State([hashtable]$State) {
    $State | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $StatePath -Encoding utf8
}

function Read-State {
    if (-not (Test-Path -LiteralPath $StatePath)) { return $null }
    try { return Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json }
    catch { throw "Invalid stack state file: $StatePath" }
}

function Get-RemoteDeployCommand([string]$RemoteArchive, [string]$Vendor, [string]$VendorName) {
    # Fail at the actual deployment error instead of returning the final rm status.
    # Use the C locale for readable tar diagnostics on Windows PowerShell 5.1.
    $command = @'
set -e
LC_ALL=C tar -xzf {ARCHIVE} -C {ROOT}
chmod +x {ROOT}/Server/ARDY/start_motion_service.sh
if test -d {VENDOR}; then
  ln -sfn {VENDOR} {ROOT}/Server/ARDY/vendor/{VENDOR_NAME}
fi
rm -f {ARCHIVE}
'@
    return $command.Replace('{ARCHIVE}', (Bash-Quote $RemoteArchive)).Replace('{ROOT}', (Bash-Quote $RemoteRoot)).Replace('{VENDOR}', (Bash-Quote $Vendor)).Replace('{VENDOR_NAME}', (Bash-Quote $VendorName))
}

function Deploy-Source {
    $stage = Join-Path $env:TEMP ("neeeva-ardy-stage-" + [guid]::NewGuid().ToString('N'))
    $archive = Join-Path $env:TEMP ("neeeva-ardy-source-" + [guid]::NewGuid().ToString('N') + '.tar.gz')
    try {
        New-Item -ItemType Directory -Force -Path (Join-Path $stage 'Server\ARDY'), (Join-Path $stage 'Tools\MotionAdapter') | Out-Null
        Get-ChildItem (Join-Path $ProjectRoot 'Server\ARDY') -File -Filter '*.py' | Copy-Item -Destination (Join-Path $stage 'Server\ARDY')
        Copy-Item (Join-Path $ProjectRoot 'Server\ARDY\motion_service') -Destination (Join-Path $stage 'Server\ARDY') -Recurse
        Copy-Item (Join-Path $ProjectRoot 'Server\ARDY\start_motion_service.sh') -Destination (Join-Path $stage 'Server\ARDY')
        foreach ($name in @('__init__.py', 'adapter.py', 'export_unity.py', 'upstream-lock.json')) {
            $source = Join-Path $ProjectRoot "Tools\MotionAdapter\$name"
            if (Test-Path -LiteralPath $source) { Copy-Item $source -Destination (Join-Path $stage 'Tools\MotionAdapter') }
        }
        $report = Join-Path $ProjectRoot 'Tools\MotionAdapter\reports\unity-preview-generation.json'
        if (Test-Path -LiteralPath $report) {
            New-Item -ItemType Directory -Force -Path (Join-Path $stage 'Tools\MotionAdapter\reports') | Out-Null
            Copy-Item $report -Destination (Join-Path $stage 'Tools\MotionAdapter\reports')
        }
        $adapter = Join-Path $ProjectRoot 'Tools\MotionAdapter\runtime\mlp-seed1.pt'
        if (Test-Path -LiteralPath $adapter) {
            New-Item -ItemType Directory -Force -Path (Join-Path $stage 'Tools\MotionAdapter\runtime') | Out-Null
            Copy-Item $adapter -Destination (Join-Path $stage 'Tools\MotionAdapter\runtime')
        } else {
            Write-Warning '本机没有 mlp-seed1.pt；上传后仍需确认 Spark 的 runtime/adapter 或 active release 已存在。'
        }
        $localInit = Join-Path $ProjectRoot 'Tools\MotionAdapter\__init__.py'
        if (Test-Path -LiteralPath $localInit) { Copy-Item $localInit -Destination (Join-Path $stage 'Tools\MotionAdapter') }

        Invoke-NativeText 'tar.exe' @('-czf', $archive, '-C', $stage, 'Server', 'Tools') '创建 native ARDY 源码包失败。' | ForEach-Object { Write-Host "[tar] $_" }
        $remoteArchive = "$RemoteRoot/.runtime/neeeva-ardy-source.tar.gz"
        Invoke-SshText ("mkdir -p {0}/.runtime {0}/Server/ARDY/vendor && rm -f {1}" -f (Bash-Quote $RemoteRoot), (Bash-Quote $remoteArchive)) | ForEach-Object { Write-Host "[deploy] $_" }
        Invoke-Scp $archive $remoteArchive
        $vendorName = "ardy-$((Get-Content (Join-Path $ProjectRoot 'Tools\MotionAdapter\upstream-lock.json') -Raw | ConvertFrom-Json).ardy)"
        $vendor = "$RemoteRoot/vendor/$vendorName"
        Invoke-SshText (Get-RemoteDeployCommand $remoteArchive $vendor $vendorName) | ForEach-Object { Write-Host "[deploy] $_" }
        Write-Host "[deploy] native ARDY source uploaded to ${SshAlias}:$RemoteRoot" -ForegroundColor Green
    } finally {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-RemoteLlmStart {
    $active = Invoke-SshText ("sudo -n systemctl is-active --quiet {0} && echo active || echo inactive" -f (Bash-Quote $LlmServiceName)) $LlmSshHost $LlmSshPort
    if (($active -join '').Trim() -eq 'active') { return $false }
    if (-not [string]::IsNullOrWhiteSpace($RemoteLlmStartCommand)) {
        Write-Host '[llm] API 不可用，执行显式配置的远程启动命令。' -ForegroundColor Yellow
        Invoke-SshText $RemoteLlmStartCommand $LlmSshHost $LlmSshPort | ForEach-Object { Write-Host "[llm] $_" }
    } else {
        Write-Host "[llm] 启动 systemd 服务 $LlmServiceName。" -ForegroundColor Yellow
        Invoke-SshText ("sudo -n systemctl start {0}" -f (Bash-Quote $LlmServiceName)) $LlmSshHost $LlmSshPort | ForEach-Object { Write-Host "[llm] $_" }
    }
    return $true
}

function Ensure-FeatureServices {
    if (-not $ManageFeatureServices) { return @() }
    $health = Get-HttpJson "$FeatureUrl/health" 3
    if ($health -and $health.protocol -eq 'qwen-motion-raw-last-v1') { return @() }
    $started = @()
    foreach ($service in $FeatureServiceName) {
        $active = Invoke-SshText ("sudo -n systemctl is-active --quiet {0} && echo active || echo inactive" -f (Bash-Quote $service))
        if (($active -join '').Trim() -ne 'active') {
            Invoke-SshText ("sudo -n systemctl start {0}" -f (Bash-Quote $service)) | Out-Null
            $started += $service
        }
    }
    return $started
}

function Start-RemoteArdy {
    $remoteRuntime = "$RemoteRoot/.runtime"
    $pidFile = "$remoteRuntime/ardy.pid"
    $outLog = "$remoteRuntime/ardy.out.log"
    $errLog = "$remoteRuntime/ardy.err.log"
    $allow = if ($AllowIndependentFeatureModel) { ' --allow-independent-feature-model' } else { '' }
    $adapter = if ($AllowUnverifiedAdapter) { " --adapter-path $(Bash-Quote "$RemoteRoot/runtime/mlp-seed1.pt") --allow-unverified-adapter" } else { '' }
    $coreEnv = "NEEEVA_ARDY_CORE_DIR=$(Bash-Quote $RemoteCoreDir) "
    $feature = Bash-Quote $FeatureUrl
    $root = Bash-Quote $RemoteRoot
    $startScript = Bash-Quote "$RemoteRoot/Server/ARDY/start_motion_service.sh"
    $pythonEnv = if ([string]::IsNullOrWhiteSpace($RemotePython)) { '' } else { "NEEEVA_ARDY_PYTHON=$(Bash-Quote $RemotePython) " }
    $lines = @(
        'set -eu',
        'mkdir -p {RUNTIME}',
        'if [ -f {PID} ]; then',
        '  p="$(cat {PID})"',
        '  line=',
        '  case "$p" in',
        '    ""|*[!0-9]*|0|1) ;;',
        '    *) if kill -0 "$p" 2>/dev/null; then',
        '         line="$(tr ''\0'' '' '' 2>/dev/null < /proc/$p/cmdline || true)"',
        '       fi ;;',
        '  esac',
        '  case "$line " in',
        '    *Server.ARDY.motion_service.app\ *--port\ {PORT}\ *) ;;',
        '    *) rm -f {PID}; echo cleared-stale-pid ;;',
        '  esac',
        'fi',
        'if [ ! -f {PID} ]; then',
        '  test -x {SCRIPT} || { echo missing-start-motion-service >&2; exit 3; }',
        '  nohup env {PYTHON_ENV}{CORE_ENV}NEEEVA_ARDY_ROOT={ROOT} {SCRIPT} --host 0.0.0.0 --port {PORT} --feature-url {FEATURE}{ALLOW}{ADAPTER} >{OUT} 2>{ERR} </dev/null &',
        '  echo $! >{PID}',
        '  echo started',
        'else',
        '  echo reused',
        'fi'
    )
    $cmd = $lines -join "`n"
    $cmd = $cmd.Replace('{RUNTIME}', (Bash-Quote $remoteRuntime)).Replace('{PID}', (Bash-Quote $pidFile))
    $cmd = $cmd.Replace('{SCRIPT}', $startScript).Replace('{ROOT}', $root).Replace('{PORT}', [string]$ArdyPort)
    $cmd = $cmd.Replace('{FEATURE}', $feature).Replace('{ALLOW}', $allow).Replace('{ADAPTER}', $adapter).Replace('{PYTHON_ENV}', $pythonEnv).Replace('{CORE_ENV}', $coreEnv)
    $cmd = $cmd.Replace('{OUT}', (Bash-Quote $outLog)).Replace('{ERR}', (Bash-Quote $errLog))
    Invoke-SshText $cmd | ForEach-Object { Write-Host "[ardy] $_" }
}

function Assert-RemoteArdyRunning([switch]$IncludeLogs) {
    $lines = @(
        'set -eu',
        'p="$(cat {PID} 2>/dev/null || true)"',
        'line=',
        'case "$p" in',
        '  ""|*[!0-9]*|0|1) ;;',
        '  *) if kill -0 "$p" 2>/dev/null; then',
        '       line="$(tr ''\0'' '' '' 2>/dev/null < /proc/$p/cmdline || true)"',
        '     fi ;;',
        'esac',
        'case "$line " in',
        '  *Server.ARDY.motion_service.app\ *--port\ {PORT}\ *) running=yes ;;',
        '  *) running=no ;;',
        'esac',
        'if [ "$running" != yes ] || [ "{LOGS}" = yes ]; then',
        '  echo "ARDY process: pid=$p running=$running"',
        '  echo "Recent ARDY stderr:"',
        '  tail -n 40 {ERR} 2>/dev/null || true',
        'fi',
        '[ "$running" = yes ] || exit 3'
    )
    $cmd = ($lines -join "`n").Replace('{PID}', (Bash-Quote "$RemoteRoot/.runtime/ardy.pid"))
    $cmd = $cmd.Replace('{PORT}', [string]$ArdyPort).Replace('{ERR}', (Bash-Quote "$RemoteRoot/.runtime/ardy.err.log"))
    $cmd = $cmd.Replace('{LOGS}', $(if ($IncludeLogs) { 'yes' } else { 'no' }))
    Invoke-SshText $cmd | ForEach-Object { Write-Host "[ardy] $_" }
}

function Stop-RemoteArdy {
    $pidFile = "$RemoteRoot/.runtime/ardy.pid"
    $lines = @(
        'set -eu',
        'p=',
        'if [ -f {PID} ]; then p="$(cat {PID})"; fi',
        'if [ -n "$p" ] && kill -0 "$p" 2>/dev/null; then',
        '  line="$(tr ''\0'' '' '' 2>/dev/null < /proc/$p/cmdline || true)"',
        '  case "$line " in',
        '    *Server.ARDY.motion_service.app\ *--port\ {PORT}\ *)',
        '      kill "$p"',
        '      for i in 1 2 3 4 5 6 7 8 9 10; do kill -0 "$p" 2>/dev/null || break; sleep 1; done',
        '      kill -0 "$p" 2>/dev/null && kill -KILL "$p" || true',
        '      ;;',
        '    *) echo refusing-to-stop-unrecognised-PID >&2; exit 4 ;;',
        '  esac',
        'fi',
        'rm -f {PID}',
        'echo stopped'
    )
    $cmd = $lines -join "`n"
    $cmd = $cmd.Replace('{PID}', (Bash-Quote $pidFile)).Replace('{PORT}', [string]$ArdyPort)
    Invoke-SshText $cmd | ForEach-Object { Write-Host "[ardy] $_" }
}

Assert-SafeOrigin $LlmUrl 'LlmUrl'
Assert-SafeOrigin $ArdyUrl 'ArdyUrl'

try {
    if ($Action -eq 'status') {
        $llm = Get-LlmStatus
        $ardy = Get-ArdyStatus
        Write-Host "[llm] $($llm.url) ready=$($llm.ready) models=$($llm.models -join ', ')" -ForegroundColor $(if ($llm.ready) { 'Green' } else { 'Yellow' })
        if ($ardy.health) {
            $features = $ardy.health.features
            $shared = if ($null -ne $features.sharedModel) { [string]$features.sharedModel } else { 'pending-first-request' }
            Write-Host "[ardy] $($ardy.url) ready=$($ardy.ready) feature=$($features.source) shared=$shared" -ForegroundColor $(if ($ardy.ready) { 'Green' } else { 'Yellow' })
        } else { Write-Host "[ardy] $($ardy.url) unavailable" -ForegroundColor Yellow }
        exit ([int](-not ($llm.ready -and $ardy.ready)))
    }

    if ($Action -eq 'stop') {
        $state = Read-State
        if ($state -and $state.remoteRoot) {
            if ($state.ardySshHost) { $ArdySshHost = [string]$state.ardySshHost }
            if ($state.ardySshPort) { $ArdySshPort = [int]$state.ardySshPort }
            if ($state.llmSshHost) { $LlmSshHost = [string]$state.llmSshHost }
            if ($state.llmSshPort) { $LlmSshPort = [int]$state.llmSshPort }
            if ($state.llmServiceName) { $LlmServiceName = [string]$state.llmServiceName }
            if ($state.remoteRoot) { $RemoteRoot = [string]$state.remoteRoot }
            if ($state.ardyPort) { $ArdyPort = [int]$state.ardyPort }
            Stop-RemoteArdy
            if ($StopLlm -or $state.llmStartedByStack) {
                if (-not [string]::IsNullOrWhiteSpace($RemoteLlmStopCommand)) {
                    Invoke-SshText $RemoteLlmStopCommand $LlmSshHost $LlmSshPort | ForEach-Object { Write-Host "[llm] $_" }
                } else {
                    Invoke-SshText ("sudo -n systemctl stop {0}" -f (Bash-Quote $LlmServiceName)) $LlmSshHost $LlmSshPort | ForEach-Object { Write-Host "[llm] $_" }
                }
            }
            if ($state.featureServicesStartedByStack) {
                foreach ($service in @($state.featureServicesStartedByStack)) {
                    Invoke-SshText ("sudo -n systemctl stop {0}" -f (Bash-Quote $service)) $ArdySshHost $ArdySshPort | ForEach-Object { Write-Host "[feature] $_" }
                }
            }
            Remove-Item -LiteralPath $StatePath -Force -ErrorAction SilentlyContinue
        } else {
            Write-Host '[stack] 没有本机记录的 Spark ARDY 进程；为避免误杀外部服务，未执行 ARDY kill。' -ForegroundColor Yellow
            if ($StopLlm) {
                if (-not [string]::IsNullOrWhiteSpace($RemoteLlmStopCommand)) {
                    Invoke-SshText $RemoteLlmStopCommand $LlmSshHost $LlmSshPort | ForEach-Object { Write-Host "[llm] $_" }
                } else {
                    Invoke-SshText ("sudo -n systemctl stop {0}" -f (Bash-Quote $LlmServiceName)) $LlmSshHost $LlmSshPort | ForEach-Object { Write-Host "[llm] $_" }
                }
            }
        }
        exit 0
    }

    $llm = Get-LlmStatus
    $llmStartedByStack = $false
    if (-not $llm.ready) {
        $llmStartedByStack = Invoke-RemoteLlmStart
        if (-not $NoWait) {
            # The 35B NVFP4 container loads five safetensor shards and can take
            # several minutes after systemd reports the container as started.
            $deadline = (Get-Date).AddSeconds(420)
            do { Start-Sleep -Seconds 2; $llm = Get-LlmStatus; if ($llm.ready) { break } } while ((Get-Date) -lt $deadline)
        }
    }
    if (-not $llm.ready) { throw "Spark LLM 未就绪或模型不匹配：$LlmUrl/v1/models（需要 $LlmModelName）。已有远程进程不会被脚本臆测或替换；需要 -RemoteLlmStartCommand。" }
    Write-Host "[llm] ready at $LlmUrl; models=$($llm.models -join ', ')" -ForegroundColor Green
    $featureStartedByStack = Ensure-FeatureServices

    if (-not $SkipDeploy) { Deploy-Source }
    $state = [ordered]@{
        schema = 1; startedUtc = [DateTime]::UtcNow.ToString('o'); status = 'starting'
        spark = $Spark; ardySshHost = $ArdySshHost; ardySshPort = $ArdySshPort; llmSshHost = $LlmSshHost; llmSshPort = $LlmSshPort
        sshAlias = $SshAlias; remoteRoot = $RemoteRoot; ardyUrl = $ArdyUrl
        remoteCoreDir = $RemoteCoreDir; ardyPort = $ArdyPort; featureUrl = $FeatureUrl; llmUrl = $LlmUrl; llmModelName = $LlmModelName
        llmServiceName = $LlmServiceName; llmStartedByStack = $llmStartedByStack; featureServicesStartedByStack = @($featureStartedByStack)
        allowIndependentFeatureModel = [bool]$AllowIndependentFeatureModel; allowUnverifiedAdapter = [bool]$AllowUnverifiedAdapter
    }
    Save-State $state
    Start-RemoteArdy
    if (-not $NoWait) {
        Write-Host "[ardy] waiting for $ArdyUrl/health ..." -ForegroundColor Yellow
        $deadline = (Get-Date).AddSeconds(180)
        do {
            Start-Sleep -Seconds 2
            $ardy = Get-ArdyStatus
            if ($ardy.ready) { break }
            Assert-RemoteArdyRunning
        } while ((Get-Date) -lt $deadline)
        if (-not $ardy.ready) {
            Assert-RemoteArdyRunning -IncludeLogs
            throw "native ARDY 未就绪：$ArdyUrl/health。请查看 Spark $RemoteRoot/.runtime/ardy.err.log。"
        }
    }
    $state.status = if ($NoWait) { 'starting' } else { 'ready' }
    Save-State $state
    Write-Host "[ardy] native endpoint ready at $ArdyUrl" -ForegroundColor Green
    Write-Host '[unity] 请使用上述 ARDY URL；场景中的 m_ArdyServiceUrl 应与它一致。' -ForegroundColor Cyan
} catch {
    if ($Action -eq 'start' -and $state) {
        $state.status = 'failed'
        $state.error = $_.Exception.Message
        Save-State $state
    }
    Write-Host "[stack] FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
