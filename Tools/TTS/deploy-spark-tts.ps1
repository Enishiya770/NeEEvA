[CmdletBinding()]
param(
    [string]$SourceRoot = 'E:/so-vits/GPT-SoVITS-v2pro-20250604',
    [string]$SshTarget = 'spark_root_001@192.168.114.163',
    [string]$IdentityFile = "$env:USERPROFILE/.ssh/id_ed25519_dgx_spark",
    [string]$RemoteRoot = '/home/spark_root_001/neeeva-voice/tts',
    [string]$BuildProxy = '',
    [switch]$ReusePackage,
    [switch]$TransferOnly
)
$ErrorActionPreference = 'Stop'
$project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$files = Join-Path $project 'Server/TTS'
$package = Join-Path $files 'runtime/package'
if ($RemoteRoot -notmatch '^/home/[A-Za-z0-9_-]+/neeeva-voice/tts$') { throw 'Remote root must be the dedicated neeeva-voice/tts directory.' }
if ($BuildProxy -and $BuildProxy -notmatch '^https?://[A-Za-z0-9._:-]+$') { throw 'BuildProxy must be an HTTP proxy URL without credentials or shell characters.' }
function Invoke-Ssh([string]$Command) {
    & ssh -i $IdentityFile -o BatchMode=yes -o ConnectTimeout=10 $SshTarget $Command
    if ($LASTEXITCODE -ne 0) { throw "Remote operation failed ($LASTEXITCODE)." }
}
if (-not $ReusePackage) {
    & (Join-Path $SourceRoot 'runtime/python.exe') (Join-Path $files 'package_local.py') `
        --source $SourceRoot --reference (Join-Path $project 'Assets/Model/41041.wav') --output $package
    if ($LASTEXITCODE -ne 0) { throw 'Local release packaging failed.' }
}
$manifest = Get-Content -LiteralPath (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json
$release = $manifest.release_sha256
if ($release -notmatch '^[0-9a-f]{64}$') { throw 'Invalid release identity.' }
Invoke-Ssh "if test -e '$RemoteRoot' && ! test -f '$RemoteRoot/.neeeva-tts-owner'; then echo 'Refusing unmanaged existing directory'; exit 2; fi; mkdir -p '$RemoteRoot'; touch '$RemoteRoot/.neeeva-tts-owner'"
$manifestHash = (Get-FileHash -LiteralPath (Join-Path $package 'manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
$present = Invoke-Ssh "if test -f '$RemoteRoot/release/manifest.json'; then sha256sum '$RemoteRoot/release/manifest.json' | cut -d ' ' -f1; else echo absent; fi"
if (($present -join '').Trim() -ne 'absent' -and ($present -join '').Trim() -ne $manifestHash) {
    throw 'A different frozen release already exists. Preserve it and review a versioned migration before replacing it.'
}
foreach ($file in @('Dockerfile.spark','.dockerignore','requirements-spark.txt','requirements-lock.txt','serve.py','verify_release.py','manage.sh')) {
    & scp -i $IdentityFile -o BatchMode=yes (Join-Path $files $file) "${SshTarget}:${RemoteRoot}/$file"
    if ($LASTEXITCODE -ne 0) { throw "Copy failed: $file" }
}
if (($present -join '').Trim() -eq 'absent') {
    Write-Host ("Transferring frozen release: {0:N1} MiB, {1} files" -f ($manifest.total_bytes/1MB), $manifest.file_count)
    & scp -i $IdentityFile -o BatchMode=yes (Join-Path $package 'release.tar') "${SshTarget}:${RemoteRoot}/release.tar"
    if ($LASTEXITCODE -ne 0) { throw 'Release transfer failed; partial archive is not activated.' }
    Invoke-Ssh "mkdir -p '$RemoteRoot/release'; tar -xf '$RemoteRoot/release.tar' -C '$RemoteRoot/release'"
}
Invoke-Ssh "python3 '$RemoteRoot/verify_release.py'"
if (-not $TransferOnly) {
    $prefix = if ($BuildProxy) { "NEEEVA_BUILD_PROXY='$BuildProxy' " } else { '' }
    Invoke-Ssh "${prefix}bash '$RemoteRoot/manage.sh' build"
    & (Join-Path $PSScriptRoot 'manage-spark-tts.ps1') -Action start -SshTarget $SshTarget -IdentityFile $IdentityFile -RemoteRoot $RemoteRoot
}
