$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Vendor = Join-Path $Root "vendor\rvc"
$VenvPython = Join-Path $Root ".venv\Scripts\python.exe"

if (-not (Test-Path $Vendor)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Vendor) | Out-Null
    git clone --depth 1 https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI.git $Vendor
}

if (-not (Test-Path $VenvPython)) {
    py -3.12 -m venv (Join-Path $Root ".venv")
}

& $VenvPython -m pip install `
    torch==2.7.1+cu118 torchaudio==2.7.1+cu118 `
    --index-url https://mirrors.nju.edu.cn/pytorch/whl/cu118 `
    --extra-index-url https://mirrors.pku.edu.cn/pypi/simple
& $VenvPython -m pip install -r (Join-Path $Vendor "requirments_cu118_py312.txt")
& $VenvPython (Join-Path $Root "setup_rvc_assets.py")

Write-Host "[RVC] environment and official assets are ready"
