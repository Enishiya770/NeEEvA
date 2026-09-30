[CmdletBinding()]
param(
    [ValidateSet('status','start','stop','health','logs','verify')][string]$Action = 'status',
    [string]$SshTarget = 'spark_root_001@192.168.114.163',
    [string]$IdentityFile = "$env:USERPROFILE/.ssh/id_ed25519_dgx_spark",
    [string]$RemoteRoot = '/home/spark_root_001/neeeva-voice/learning',
    [ValidateSet('cpu','cuda')][string]$Device = 'cpu',
    [ValidateRange(1,4)][int]$CpuThreads = 2,
    [ValidateRange(1024,65535)][int]$Port = 9884
)
$ErrorActionPreference = 'Stop'
if ($SshTarget -notmatch '^[A-Za-z0-9_-]+@(?<host>[A-Za-z0-9.-]+)$') { throw 'Expected user@host without credentials or options.' }
$learningHost = $Matches['host']
if ($RemoteRoot -notmatch '^/home/[A-Za-z0-9_-]+/neeeva-voice/learning(?:-[A-Za-z0-9_-]+)?$') { throw 'Invalid learning deployment directory.' }
& ssh -i $IdentityFile -o BatchMode=yes -o ConnectTimeout=10 $SshTarget "SINGING_LEARNING_PORT='$Port' SINGING_LEARNING_DEVICE='$Device' SINGING_LEARNING_TORCH_THREADS='$CpuThreads' bash '$RemoteRoot/manage.sh' '$Action'"
if ($LASTEXITCODE -ne 0) { throw "Learning service $Action failed ($LASTEXITCODE)." }
if ($Action -eq 'start') {
    for ($attempt = 0; $attempt -lt 45; $attempt++) {
        try {
            $result = & curl.exe --noproxy '*' --fail --silent --connect-timeout 2 --max-time 4 "http://${learningHost}:$Port/health" 2>$null
            if ($LASTEXITCODE -eq 0) {
                $health = ($result -join '') | ConvertFrom-Json
                if ($health.ok -eq $true -and $health.service -eq 'singing-learning') { Write-Output "Learning ready: http://${learningHost}:$Port"; return }
            }
        } catch { }
        Start-Sleep -Seconds 1
    }
    throw 'Learning did not become healthy; inspect its dedicated container logs.'
}
