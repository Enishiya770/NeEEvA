$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$Lock = Get-Content -LiteralPath (Join-Path $ProjectRoot 'Tools\MotionAdapter\upstream-lock.json') -Raw | ConvertFrom-Json
$Source = Join-Path $PSScriptRoot ('vendor\ardy-' + $Lock.ardy)
if (-not (Test-Path -LiteralPath $Source)) { throw 'Run Tools/MotionAdapter/bootstrap_sources.ps1 first.' }
$EnvRoot = Join-Path $PSScriptRoot '.venv'
$Python = Join-Path $EnvRoot 'Scripts\python.exe'
if (-not (Test-Path -LiteralPath $Python)) {
    # Reuse this host's working CUDA torch read-only; pip changes are confined to this venv.
    & python -m venv --system-site-packages $EnvRoot
    if ($LASTEXITCODE -ne 0) { throw 'Creating the ARDY environment failed.' }
}
$Runtime = Join-Path $PSScriptRoot 'runtime'
New-Item -ItemType Directory -Force -Path $Runtime | Out-Null
$env:NEEEVA_ARDY_SOURCE = $Source
$env:NEEEVA_ARDY_REQUIREMENTS = Join-Path $Runtime 'requirements-core.txt'
& $Python -c "import os,tomllib,pathlib; d=tomllib.loads((pathlib.Path(os.environ['NEEEVA_ARDY_SOURCE'])/'pyproject.toml').read_text(encoding='utf-8')); pathlib.Path(os.environ['NEEEVA_ARDY_REQUIREMENTS']).write_text('\n'.join(d['project']['dependencies']),encoding='utf-8')"
if ($LASTEXITCODE -ne 0) { throw 'Reading pinned requirements failed.' }
& $Python -m pip install -r $env:NEEEVA_ARDY_REQUIREMENTS
if ($LASTEXITCODE -ne 0) { throw 'Installing core requirements failed.' }
# Python inference only. The optional C++ motion-correction extension is not built here.
& $Python -c "import sysconfig,pathlib,os; pathlib.Path(sysconfig.get_path('purelib'),'neeeva_ardy_source.pth').write_text(os.environ['NEEEVA_ARDY_SOURCE']+'\n')"
& $Python -c "import ardy,torch,numpy; from ardy.model import load_model; print('core imports OK',torch.__version__,numpy.__version__)"
if ($LASTEXITCODE -ne 0) { throw 'ARDY core import check failed.' }
& $Python -m pip freeze | Set-Content -LiteralPath (Join-Path $Runtime 'environment-freeze.txt') -Encoding utf8
