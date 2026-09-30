[CmdletBinding()]
param(
    [ValidateSet('status','upload','setup','start','stop','health')][string]$Action = 'status',
    [string]$SshHost = '192.168.114.163',
    [string]$SshUser = 'spark_root_001',
    [string]$RemoteRoot = '/home/spark_root_001/neeeva-voice/asr',
    [string]$RemoteWheelhouse = '',
    [string]$InstallProxy = '',
    [string]$Bundle = '',
    [string]$SshKeyPath = (Join-Path $env:USERPROFILE '.ssh\id_ed25519_dgx_spark')
)
$ErrorActionPreference = 'Stop'
if ($SshHost -notmatch '^[a-zA-Z0-9.-]+$' -or $SshUser -notmatch '^[a-zA-Z0-9_-]+$' -or
    $RemoteRoot -notmatch '^/home/[a-zA-Z0-9_-]+/neeeva-voice/asr$') { throw 'Unsafe remote target.' }
$taskTarget = "$SshUser@$SshHost"
$taskSshArgs = @('-o','BatchMode=yes','-o','ConnectTimeout=8','-o','StrictHostKeyChecking=yes','-o','IdentitiesOnly=yes','-i',$SshKeyPath)
function Invoke-AsrSsh([string]$Script) {
    $taskEncoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Script))
    & ssh.exe @taskSshArgs $taskTarget "echo $taskEncoded | base64 -d | bash"
    if ($LASTEXITCODE -ne 0) { throw "ASR remote $Action failed (exit $LASTEXITCODE)." }
}
if ($Action -eq 'health') {
    & curl.exe --noproxy '*' --connect-timeout 3 --max-time 10 --fail --silent --show-error "http://${SshHost}:9881/health"
    if ($LASTEXITCODE -ne 0) { throw 'Remote ASR health unavailable.' }
    exit 0
}
if ($Action -eq 'upload') {
    if (-not (Test-Path -LiteralPath $Bundle -PathType Leaf)) { throw 'Provide an existing explicit --bundle archive.' }
    $taskId = [Guid]::NewGuid().ToString('N')
    $taskStage = "/home/$SshUser/neeeva-voice/.asr-stage-$taskId"
    Invoke-AsrSsh "set -eu`ntest ! -e '$RemoteRoot'`nmkdir -m 700 '$taskStage'"
    & scp.exe @taskSshArgs (Resolve-Path -LiteralPath $Bundle).Path "${taskTarget}:$taskStage/asr.tar"
    if ($LASTEXITCODE -ne 0) { throw 'ASR bundle upload failed.' }
    & scp.exe @taskSshArgs (Join-Path $PSScriptRoot 'extract_bundle.py') "${taskTarget}:$taskStage/extract_bundle.py"
    if ($LASTEXITCODE -ne 0) { throw 'ASR extractor upload failed.' }
    Invoke-AsrSsh "set -eu`npython3 '$taskStage/extract_bundle.py' '$taskStage/asr.tar' '$RemoteRoot'`nrm -- '$taskStage/asr.tar' '$taskStage/extract_bundle.py'`nrmdir '$taskStage'"
    $taskStatePath = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'Server/RuntimeLogs/spark_asr.state.json'
    [pscustomobject]@{ schema=1; stage='uploaded-not-started'; host=$SshHost; root=$RemoteRoot; url="http://${SshHost}:9881"; updatedUtc=[DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath $taskStatePath -Encoding UTF8
    exit 0
}
if ($Action -eq 'setup') {
    if ($RemoteWheelhouse -and $RemoteWheelhouse -notmatch '^/[a-zA-Z0-9_./-]+$') { throw 'Unsafe wheelhouse path.' }
    $taskInstallEnv = "PIP_CACHE_DIR='$RemoteRoot/.pip-cache' "
    if ($InstallProxy) {
        if ($InstallProxy -notmatch '^http://[a-zA-Z0-9.-]+:[0-9]+/?$') { throw 'Expected an HTTP proxy origin without credentials.' }
        $taskInstallEnv += "HTTP_PROXY='$InstallProxy' HTTPS_PROXY='$InstallProxy' "
    }
    Invoke-AsrSsh "${taskInstallEnv}bash '$RemoteRoot/spark/setup_spark.sh' '$RemoteRoot' '$RemoteWheelhouse'"
} else {
    Invoke-AsrSsh "bash '$RemoteRoot/spark/manage.sh' '$Action' '$RemoteRoot'"
}
