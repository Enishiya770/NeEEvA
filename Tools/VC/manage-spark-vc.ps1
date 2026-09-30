[CmdletBinding()]
param(
 [ValidateSet('start','status','stop','logs')][string]$Action='status',
 [string]$SshTarget='spark_root_001@192.168.114.163',
 [string]$IdentityFile="$env:USERPROFILE/.ssh/id_ed25519_dgx_spark",
 [string]$RemoteRoot='/home/spark_root_001/neeeva-voice/vc',
 [int]$WaitSeconds=360
)
$ErrorActionPreference='Stop'
if($RemoteRoot -notmatch '^/home/[A-Za-z0-9_-]+/neeeva-voice/vc$'){throw 'Invalid dedicated VC path'}
& ssh -i $IdentityFile -o BatchMode=yes -o ConnectTimeout=10 $SshTarget "bash '$RemoteRoot/manage.sh' $Action"
if($LASTEXITCODE -ne 0){throw "Remote VC $Action failed"}
if($Action -eq 'start'){
 $deadline=[DateTime]::UtcNow.AddSeconds($WaitSeconds)
 $manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../Server/VC/manifest.json') -Raw | ConvertFrom-Json
 do{
  try{
   $raw=& ssh -i $IdentityFile -o BatchMode=yes -o ConnectTimeout=5 $SshTarget 'curl -sf --max-time 5 http://127.0.0.1:9882/health' 2>$null
   $h=($raw -join '') | ConvertFrom-Json
  }catch{ $h=$null }
   if($h -and $h.ok -and $h.backend -eq 'rvc-character-v2' -and $h.release_sha256 -eq $manifest.release_sha256){
    if($h.worker_status -ne 'ready'){
     & ssh -i $IdentityFile -o BatchMode=yes -o ConnectTimeout=5 $SshTarget 'curl -sf --max-time 320 -X POST http://127.0.0.1:9882/warmup' | Out-Null
     if($LASTEXITCODE -ne 0){throw 'VC model warmup failed'}
    }else{
     $models=@($h.models.rvc,$h.models.hubert,$h.models.rmvpe)
     if(@($models | Where-Object {$null -eq $_ -or $_.device -notlike 'cuda*'}).Count -eq 0){Write-Host 'VC ready: actual RVC/HuBERT/RMVPE parameters are on CUDA.';return}
     throw 'VC worker is ready but actual CUDA model validation failed'
    }
   }
  Start-Sleep -Seconds 2
 }while([DateTime]::UtcNow -lt $deadline)
 throw 'VC health timeout; use -Action logs'
}
