[CmdletBinding()]
param(
    [ValidateSet('status','start','stop','logs')][string]$Action = 'status',
    [string]$SshTarget = 'spark_root_001@192.168.114.163',
    [string]$IdentityFile = "$env:USERPROFILE/.ssh/id_ed25519_dgx_spark",
    [string]$RemoteRoot = '/home/spark_root_001/neeeva-voice/tts',
    [int]$WaitSeconds = 240
)
$ErrorActionPreference = 'Stop'
if ($RemoteRoot -notmatch '^/[A-Za-z0-9_./-]+$') { throw 'Unsupported remote path.' }
& ssh -i $IdentityFile -o BatchMode=yes -o ConnectTimeout=10 $SshTarget "bash '$RemoteRoot/manage.sh' $Action"
if ($LASTEXITCODE -ne 0) { throw "Remote TTS $Action failed ($LASTEXITCODE)." }
if ($Action -eq 'start') {
    $hostName = ($SshTarget -split '@')[-1]
    $manifestPath = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'Server/TTS/manifest.json'
    $expectedRelease = (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).release_sha256
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    do {
        try {
            $healthJson = & ssh -i $IdentityFile -o BatchMode=yes -o ConnectTimeout=5 $SshTarget 'curl --silent --fail --max-time 5 http://127.0.0.1:9880/health' 2>$null
            $health = ($healthJson -join '') | ConvertFrom-Json
            if ($health.ok -and $health.ready -and $health.cuda_available -and $health.precision -eq 'float32' -and $health.release_sha256 -eq $expectedRelease) {
                Write-Host "TTS ready: http://${hostName}:9880, $($health.model_version), $($health.precision), release=$($health.release_sha256)"
                return
            }
        } catch { }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Remote TTS did not become ready before the deadline. Use -Action logs; no other service was stopped.'
}
