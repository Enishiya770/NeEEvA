[CmdletBinding()]
param(
    [string]$SshTarget = 'spark_root_001@192.168.114.163',
    [string]$IdentityFile = "$env:USERPROFILE/.ssh/id_ed25519_dgx_spark",
    [string]$RemoteRoot = '/home/spark_root_001/neeeva-voice/learning',
    [string]$Python = '', [string]$Checkpoint = '', [string]$Labels = '', [string]$Dependencies = '',
    [string]$BuildProxy = '', [switch]$TransferOnly, [switch]$SkipStart
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($SshTarget -notmatch '^[A-Za-z0-9_-]+@[A-Za-z0-9.-]+$') { throw 'Expected user@host without credentials or options.' }
if ($RemoteRoot -notmatch '^/home/[A-Za-z0-9_-]+/neeeva-voice/learning(?:-[A-Za-z0-9_-]+)?$') { throw 'Use a dedicated neeeva-voice/learning directory.' }
if ($BuildProxy -and $BuildProxy -notmatch '^https?://[A-Za-z0-9._:-]+$') { throw 'Build proxy must have no credentials or shell characters.' }
if (!$Python) { $Python = Join-Path $project 'Server/SenseVoice/runtime/.venv-cuda/Scripts/python.exe' }
$package = Join-Path $project 'work/spark-learning-package'
$arguments = @((Join-Path $PSScriptRoot 'package_spark.py'), '--output', $package)
if ($Checkpoint) { $arguments += @('--checkpoint', $Checkpoint) }
if ($Labels) { $arguments += @('--labels', $Labels) }
if ($Dependencies) { $arguments += @('--dependencies', $Dependencies) }
& $Python @arguments
if ($LASTEXITCODE -ne 0) { throw 'Learning release packaging failed.' }
function Invoke-Remote([string]$Command) {
    & ssh -i $IdentityFile -o BatchMode=yes -o ConnectTimeout=10 $SshTarget $Command
    if ($LASTEXITCODE -ne 0) { throw "Remote learning deployment failed ($LASTEXITCODE)." }
}
Invoke-Remote "if test -e '$RemoteRoot' && ! test -f '$RemoteRoot/.neeeva-learning-owner'; then echo 'Refusing unmanaged directory'; exit 2; fi; mkdir -p '$RemoteRoot'; touch '$RemoteRoot/.neeeva-learning-owner'"
$hash = (Get-FileHash -LiteralPath (Join-Path $package 'manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
$present = Invoke-Remote "if test -f '$RemoteRoot/release/manifest.json'; then sha256sum '$RemoteRoot/release/manifest.json' | cut -d ' ' -f1; else echo absent; fi"
if (($present -join '').Trim() -notin @('absent', $hash)) { throw 'A different release exists. Use a new versioned learning directory; existing data was preserved.' }
foreach ($name in @('Dockerfile', '.dockerignore', 'requirements.txt', 'run.py', 'manage.sh')) {
    & scp -i $IdentityFile -o BatchMode=yes (Join-Path $project "Server/SingingLearning/spark/$name") "${SshTarget}:${RemoteRoot}/$name"
    if ($LASTEXITCODE -ne 0) { throw "Transfer failed: $name" }
}
if (($present -join '').Trim() -eq 'absent') {
    & scp -i $IdentityFile -o BatchMode=yes (Join-Path $package 'release.tar') "${SshTarget}:${RemoteRoot}/release.tar"
    if ($LASTEXITCODE -ne 0) { throw 'Archive transfer failed; not activated.' }
    Invoke-Remote "mkdir -p '$RemoteRoot/release'; tar -xf '$RemoteRoot/release.tar' -C '$RemoteRoot/release'"
}
if (!$TransferOnly) {
    $prefix = if ($BuildProxy) { "NEEEVA_BUILD_PROXY='$BuildProxy' " } else { '' }
    Invoke-Remote "${prefix}bash '$RemoteRoot/manage.sh' build"
    Invoke-Remote "bash '$RemoteRoot/manage.sh' verify"
    if (!$SkipStart) { & (Join-Path $PSScriptRoot 'manage-spark-learning.ps1') -Action start -SshTarget $SshTarget -IdentityFile $IdentityFile -RemoteRoot $RemoteRoot }
}
Write-Output 'Learning code/model release transferred. User recordings are never copied by this deployment script.'
