[CmdletBinding()]
param([ValidateSet('stage','controllers','start','status','stop')][string]$Action='status',
      [ValidateSet(8080,8082)][int]$Port=8082)
$ErrorActionPreference='Stop'
$Stage='D:\NeEEvA\motion-feature-server'
$Root=Split-Path $PSScriptRoot -Parent
function Remote([string]$Script) {
    $Script='$ErrorActionPreference="Stop"; $ProgressPreference="SilentlyContinue"; '+$Script
    $Encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    & ssh -o BatchMode=yes -o ConnectTimeout=8 neeeva-5090 powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand $Encoded
    if($LASTEXITCODE -ne 0) { throw 'Remote command failed.' }
}
function Stage-Controllers {
    Remote '$original="D:\NeEEvA\services\neeeva_remote_llm.ps1"; $backup="D:\NeEEvA\services\neeeva_remote_llm.before-feature.ps1"; if((Test-Path -LiteralPath $original) -and -not(Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $original -Destination $backup }'
    & scp -q (Join-Path $PSScriptRoot 'remote_server.ps1') ('neeeva-5090:'+$Stage+'/')
    if($LASTEXITCODE -ne 0) { throw 'Copy launcher failed.' }
    & scp -q (Join-Path (Split-Path $Root -Parent) 'Remote\neeeva_remote_llm.ps1') 'neeeva-5090:D:/NeEEvA/services/neeeva_remote_llm.ps1'
    if($LASTEXITCODE -ne 0) { throw 'Copy remote controller failed.' }
}
if($Action -eq 'stage') {
    Remote 'if(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -ieq "D:\NeEEvA\motion-feature-server\llama-server.exe" }) { throw "Stop the feature server before staging binary updates" }; New-Item -ItemType Directory -Force D:\NeEEvA\motion-feature-server | Out-Null; Get-ChildItem D:\NeEEvA\llamacpp-b8919-cuda131-sm120 -Filter *.dll -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination D:\NeEEvA\motion-feature-server -Force }'
    # Use the official pinned DLLs unchanged, including common and vision.
    # Only server-context/server HTTP logic lives in the rebuilt executable.
    foreach($file in @('llama-server.exe')) {
        & scp -q (Join-Path $Root ('runtime\server-feature-build\bin\Release\'+$file)) ('neeeva-5090:'+$Stage+'/')
        if($LASTEXITCODE -ne 0) { throw "Copy failed: $file" }
    }
    Stage-Controllers
    Remote 'Get-FileHash D:\NeEEvA\motion-feature-server\llama-server.exe -Algorithm SHA256 | Select-Object Hash | ConvertTo-Json'
} elseif ($Action -eq 'controllers') {
    Stage-Controllers
} else {
    Remote ('& '+$Stage+'\remote_server.ps1 -Action '+$Action+' -Port '+$Port)
}
