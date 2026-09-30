[CmdletBinding()]
param(
 [string]$SshTarget='spark_root_001@192.168.114.163',
 [string]$IdentityFile="$env:USERPROFILE/.ssh/id_ed25519_dgx_spark",
 [string]$RemoteRoot='/home/spark_root_001/neeeva-voice/vc',
 [string]$BuildProxy='',
 [switch]$TransferOnly,
 [switch]$SkipStart
)
$ErrorActionPreference='Stop'
$project=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$files=Join-Path $project 'Server/VC'
$package=Join-Path $files 'runtime/package'
if($RemoteRoot -notmatch '^/home/[A-Za-z0-9_-]+/neeeva-voice/vc$'){throw 'Invalid dedicated VC path'}
if($BuildProxy -and $BuildProxy -notmatch '^https?://[A-Za-z0-9._:-]+$'){throw 'Invalid install-only proxy URL'}
function Invoke-Remote([string]$Command){
 & ssh -i $IdentityFile -o BatchMode=yes -o ConnectTimeout=10 $SshTarget $Command
 if($LASTEXITCODE -ne 0){throw 'Remote VC deployment operation failed'}
}
$manifestHash=(Get-FileHash -LiteralPath (Join-Path $package 'manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
Invoke-Remote "if test -e '$RemoteRoot' && ! test -f '$RemoteRoot/.neeeva-vc-owner'; then echo 'Refusing unmanaged existing directory'; exit 2; fi; mkdir -p '$RemoteRoot'; touch '$RemoteRoot/.neeeva-vc-owner'"
$present=Invoke-Remote "if test -f '$RemoteRoot/release/manifest.json'; then sha256sum '$RemoteRoot/release/manifest.json' | cut -d ' ' -f1; else echo absent; fi"
if(($present -join '').Trim() -notin @('absent',$manifestHash)){throw 'Different frozen VC release exists; review versioned replacement first'}
foreach($file in @('Dockerfile.spark','.dockerignore','requirements-spark.txt','serve.py','manage.sh','verify_release.py')){
 & scp -i $IdentityFile -o BatchMode=yes (Join-Path $files $file) "${SshTarget}:${RemoteRoot}/$file"
 if($LASTEXITCODE -ne 0){throw "Copy failed: $file"}
}
if(($present -join '').Trim() -eq 'absent'){
 & scp -i $IdentityFile -o BatchMode=yes (Join-Path $package 'release.tar') "${SshTarget}:${RemoteRoot}/release.tar"
 if($LASTEXITCODE -ne 0){throw 'VC release transfer failed'}
 Invoke-Remote "mkdir -p '$RemoteRoot/release'; tar -xf '$RemoteRoot/release.tar' -C '$RemoteRoot/release'"
}
Invoke-Remote "python3 '$RemoteRoot/verify_release.py'"
if(-not $TransferOnly){
 $prefix=if($BuildProxy){"NEEEVA_BUILD_PROXY='$BuildProxy' "}else{''}
 Invoke-Remote "${prefix}bash '$RemoteRoot/manage.sh' build"
 if(-not $SkipStart){& (Join-Path $PSScriptRoot 'manage-spark-vc.ps1') -Action start -SshTarget $SshTarget -IdentityFile $IdentityFile -RemoteRoot $RemoteRoot}
}
