[CmdletBinding()]
param([ValidateSet('start','status','fetch')][string]$Action = 'status',
      [ValidateRange(1,2000)][int]$Count = 8)
$ErrorActionPreference = 'Stop'
$Pilot = 'D:\NeEEvA\motion-adapter-pilot'
$Local = Join-Path $PSScriptRoot 'runtime\remote-probe'
$ProjectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
function Remote([string]$Script) {
    $Script = '$ErrorActionPreference="Stop"; $ProgressPreference="SilentlyContinue"; ' + $Script
    $Encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    & ssh -o BatchMode=yes -o ConnectTimeout=8 neeeva-5090 powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand $Encoded
    if ($LASTEXITCODE -ne 0) { throw 'Remote probe command failed.' }
}
if ($Action -eq 'start') {
    # All copied paths are experiment artifacts; existing service binaries/configs are untouched.
    Remote ('if (Get-Process qwen_feature_probe -ErrorAction SilentlyContinue) { throw "A probe is running" }; New-Item -ItemType Directory -Force -Path ' + $Pilot + ' | Out-Null')
    Push-Location $ProjectRoot
    try {
        & python -m Tools.MotionAdapter.prepare_probe --dataset (Join-Path $PSScriptRoot 'runtime\prompts.jsonl') --output $Local --count $Count
        if ($LASTEXITCODE -ne 0) { throw 'Preparing probe input failed.' }
    } finally { Pop-Location }
    foreach ($File in @((Join-Path $Local 'prompts.txt'), (Join-Path $PSScriptRoot 'runtime\probe-build\qwen_feature_probe.exe'), (Join-Path $PSScriptRoot 'remote_probe_runner.ps1'))) {
        & scp -q $File ('neeeva-5090:' + $Pilot + '/')
        if ($LASTEXITCODE -ne 0) { throw 'Copying probe artifact failed.' }
    }
    Remote ('& ' + $Pilot + '\remote_probe_runner.ps1')
} elseif ($Action -eq 'status') {
    Remote ('Get-Process qwen_feature_probe -ErrorAction SilentlyContinue | Select-Object Id,CPU,WorkingSet64; if (Test-Path '+$Pilot+'\report.json) { Get-Content '+$Pilot+'\report.json }; if (Test-Path '+$Pilot+'\probe.err.log) { Get-Content '+$Pilot+'\probe.err.log -Tail 16 }; nvidia-smi --query-gpu=memory.used --format=csv,noheader')
} else {
    New-Item -ItemType Directory -Force -Path $Local | Out-Null
    foreach ($Name in @('report.json','provenance.json','features.f32','probe.err.log','export-manifest.json')) {
        & scp -q ('neeeva-5090:' + $Pilot + '/' + $Name) $Local
        if ($LASTEXITCODE -ne 0) { throw "Could not retrieve $Name; inspect status." }
    }
}
