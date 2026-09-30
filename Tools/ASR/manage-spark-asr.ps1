[CmdletBinding()]
param(
    [ValidateSet('status','start','stop','health')][string]$Action = 'status',
    [string]$SshTarget = 'spark_root_001@192.168.114.163',
    [string]$IdentityFile = (Join-Path $env:USERPROFILE '.ssh\id_ed25519_dgx_spark'),
    [string]$RemoteRoot = '/home/spark_root_001/neeeva-voice/asr',
    [ValidateRange(5,900)][int]$WaitSeconds = 240
)
$ErrorActionPreference = 'Stop'
if ($SshTarget -notmatch '^(?<user>[a-zA-Z0-9_-]+)@(?<host>[a-zA-Z0-9.-]+)$') {
    throw 'SshTarget must be a user@host target without options or credentials.'
}
$taskUser = $Matches['user']
$taskHostName = $Matches['host']
$taskManager = Join-Path $PSScriptRoot 'manage_spark.ps1'
# manage.sh start reuses only a recorded process whose argv matches this ASR root.
# It refuses to replace an unrelated process already listening on port 9881.
& $taskManager -Action $Action -SshHost $taskHostName -SshUser $taskUser -SshKeyPath $IdentityFile -RemoteRoot $RemoteRoot
if ($LASTEXITCODE -ne 0) { throw "Remote ASR $Action failed ($LASTEXITCODE)." }
if ($Action -ne 'start') { return }

$taskHealthUrl = "http://${taskHostName}:9881/health"
$taskDeadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
do {
    try {
        $taskHealthJson = & curl.exe --noproxy '*' --connect-timeout 3 --max-time 5 --fail --silent $taskHealthUrl 2>$null
        if ($LASTEXITCODE -eq 0) {
            $taskHealth = ($taskHealthJson -join '') | ConvertFrom-Json
            if ($taskHealth.ok -eq $true -and $taskHealth.asr_ok -eq $true -and
                $taskHealth.vad_ok -eq $true -and $taskHealth.speaker_ok -eq $true -and
                $taskHealth.cuda_available -eq $true -and $taskHealth.require_cuda -eq $true -and
                $taskHealth.asr_device -match '^cuda(?::[0-9]+)?$' -and
                $taskHealth.streaming_preview -eq $true -and $taskHealth.stream_activity_schema -eq 2 -and
                $taskHealth.analysis_dispatch_schema -eq 2 -and
                $taskHealth.final_speech_window_policy -eq 'speech-context-v1') {
                Write-Host "ASR ready: http://${taskHostName}:9881, $($taskHealth.asr_device), torch=$($taskHealth.torch_version)"
                return
            }
        }
    } catch { }
    if ([DateTime]::UtcNow -lt $taskDeadline) { Start-Sleep -Seconds 2 }
} while ([DateTime]::UtcNow -lt $taskDeadline)
throw "Remote ASR did not satisfy its CUDA/streaming health contract within ${WaitSeconds}s. Inspect $RemoteRoot/.runtime/asr.err.log. No local service or routing was changed."
