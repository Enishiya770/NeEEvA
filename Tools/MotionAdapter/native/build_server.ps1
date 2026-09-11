[CmdletBinding()]
param([switch]$SkipOverlay)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
if (-not $SkipOverlay) {
    & python (Join-Path $PSScriptRoot 'apply_overlay.py')
    if ($LASTEXITCODE -ne 0) { throw 'Source overlay failed.' }
}
$Source = Join-Path $Root 'runtime\server-feature-src'
$Build = Join-Path $Root 'runtime\server-feature-build'
$Cmake = Join-Path $Root 'runtime\build-tools\cmake\data\bin\cmake.exe'
if (-not (Test-Path -LiteralPath $Cmake)) { throw 'Install isolated cmake first; see README.' }
New-Item -ItemType Directory -Force -Path $Build | Out-Null
& $Cmake -S $Source -B $Build -G 'Visual Studio 17 2022' -A x64 -DBUILD_SHARED_LIBS=ON -DGGML_BACKEND_DL=ON -DGGML_NATIVE=OFF -DGGML_CUDA=OFF -DLLAMA_OPENSSL=OFF -DLLAMA_BUILD_TESTS=OFF -DLLAMA_BUILD_EXAMPLES=OFF -DLLAMA_BUILD_WEBUI=OFF -DLLAMA_BUILD_TOOLS=ON
if ($LASTEXITCODE -ne 0) { throw 'Server CMake configure failed.' }
& $Cmake --build $Build --config Release --target llama-server --parallel 8
if ($LASTEXITCODE -ne 0) { throw 'Server compilation failed.' }
Write-Output (Join-Path $Build 'bin\Release\llama-server.exe')
