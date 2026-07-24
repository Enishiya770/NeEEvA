$ErrorActionPreference = "Stop"

$seedVcRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent (Split-Path -Parent $seedVcRoot)
$vendorRoot = Join-Path $seedVcRoot "vendor\seed-vc"
$packageRoot = Join-Path $seedVcRoot "python_packages"
$pythonCandidates = @(
    $env:NEEEVA_PYTHON_EXE,
    $(if ($env:NEEEVA_GPT_SOVITS_ROOT) {
        Join-Path $env:NEEEVA_GPT_SOVITS_ROOT "runtime\python.exe"
    }),
    (Join-Path $projectRoot "GPT-SoVITS\runtime\python.exe")
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$pythonExe = $pythonCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1

if (-not $pythonExe -and (Get-Command py -ErrorAction SilentlyContinue)) {
    $pythonExe = "py"
}
if (-not $pythonExe) {
    throw "No compatible Python was found. Install Python 3.10/3.11, set NEEEVA_PYTHON_EXE, or set NEEEVA_GPT_SOVITS_ROOT."
}

if (-not (Test-Path -LiteralPath $vendorRoot)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $vendorRoot) | Out-Null
    git clone --depth 1 https://github.com/Plachtaa/seed-vc.git $vendorRoot
}

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
if ($pythonExe -eq "py") {
    & $pythonExe -3.10 -m pip install --target $packageRoot munch==4.0.0 python-multipart==0.0.20
    & $pythonExe -3.10 -m pip install --no-deps --target $packageRoot descript-audio-codec==1.0.0
} else {
    & $pythonExe -m pip install --target $packageRoot munch==4.0.0 python-multipart==0.0.20
    & $pythonExe -m pip install --no-deps --target $packageRoot descript-audio-codec==1.0.0
}
Write-Host "Seed-VC bridge installed. Start it with .\start_seedvc_server.ps1"
