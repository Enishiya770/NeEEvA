[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$Runtime = Join-Path $PSScriptRoot 'runtime'
$Vendor = Join-Path $ProjectRoot 'Server\ARDY\vendor'
New-Item -ItemType Directory -Force -Path $Runtime, $Vendor | Out-Null
$LockPath = Join-Path $PSScriptRoot 'upstream-lock.json'
$Headers = @{ 'User-Agent' = 'NeEEvA-MotionAdapter' }
if (Test-Path -LiteralPath $LockPath) {
    $Lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
} else {
    $Ardy = Invoke-RestMethod 'https://api.github.com/repos/nv-tlabs/ardy/commits/main' -Headers $Headers
    $Llama = Invoke-RestMethod 'https://api.github.com/repos/ggml-org/llama.cpp/commits/b8919' -Headers $Headers
    $Lock = [ordered]@{ ardy = $Ardy.sha; llama_cpp = $Llama.sha; llama_tag = 'b8919' }
    $Lock | ConvertTo-Json | Set-Content -LiteralPath $LockPath -Encoding utf8
}
foreach ($Entry in @(
    @{ Repo='nv-tlabs/ardy'; Sha=$Lock.ardy; Prefix='ardy'; Parent=$Vendor },
    @{ Repo='ggml-org/llama.cpp'; Sha=$Lock.llama_cpp; Prefix='llama.cpp'; Parent=$Runtime }
)) {
    if ($Entry.Sha -notmatch '^[a-f0-9]{40}$') { throw 'Invalid pinned source revision.' }
    $Folder = Join-Path $Entry.Parent ($Entry.Prefix + '-' + $Entry.Sha)
    if (-not (Test-Path -LiteralPath $Folder)) {
        $Archive = Join-Path $Runtime ($Entry.Prefix + '-' + $Entry.Sha + '.zip')
        $Url = 'https://codeload.github.com/' + $Entry.Repo + '/zip/' + $Entry.Sha
        Invoke-WebRequest -Uri $Url -Headers $Headers -OutFile $Archive -UseBasicParsing -TimeoutSec 180
        Expand-Archive -LiteralPath $Archive -DestinationPath $Entry.Parent
        (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash |
            Set-Content -LiteralPath ($Archive + '.sha256') -Encoding ascii
    }
    if (-not (Test-Path -LiteralPath $Folder)) { throw "Source archive did not contain $Folder" }
    Write-Output $Folder
}
