param(
    [string]$LogPath = "",
    [string]$PythonPath = ""
)

$ErrorActionPreference = "Stop"

$seedVcRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent (Split-Path -Parent $seedVcRoot)
$localPackageRoot = Join-Path $seedVcRoot "python_packages"

# Reuse the already running GPT-SoVITS runtime when possible.  That environment
# commonly contains the fairseq/faiss stack required by the character RVC
# runner, while the lightweight bridge itself can continue using the SVS venv.
$ttsRuntimePython = ""
try {
    $ttsListener = Get-NetTCPConnection -LocalAddress "127.0.0.1" `
        -LocalPort 9880 -State Listen -ErrorAction Stop |
        Select-Object -First 1
    if ($ttsListener) {
        $ttsRuntimePython = (Get-Process -Id $ttsListener.OwningProcess `
            -ErrorAction Stop).Path
    }
} catch {
    $ttsRuntimePython = ""
}

$rvcPythonCandidates = @(
    $env:NEEEVA_RVC_PYTHON,
    $(if ($env:NEEEVA_GPT_SOVITS_ROOT) {
        Join-Path $env:NEEEVA_GPT_SOVITS_ROOT "runtime\python.exe"
    }),
    (Join-Path $projectRoot "GPT-SoVITS\runtime\python.exe"),
    $ttsRuntimePython
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -Unique

foreach ($candidate in $rvcPythonCandidates) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        continue
    }
    try {
        & $candidate -c "import torch, faiss, fairseq, librosa" *> $null
        if ($LASTEXITCODE -eq 0) {
            $env:NEEEVA_RVC_PYTHON = [System.IO.Path]::GetFullPath($candidate)
            break
        }
    } catch {
        continue
    }
}

$pythonCandidates = @(
    $PythonPath,
    $env:NEEEVA_SEEDVC_PYTHON,
    (Join-Path $projectRoot "Server\SVS\.venv\Scripts\python.exe"),
    $env:NEEEVA_PYTHON_EXE,
    $(if ($env:NEEEVA_GPT_SOVITS_ROOT) {
        Join-Path $env:NEEEVA_GPT_SOVITS_ROOT "runtime\python.exe"
    }),
    (Join-Path $projectRoot "GPT-SoVITS\runtime\python.exe")
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

function Test-SeedVcPython {
    param(
        [string]$Candidate,
        [switch]$Launcher
    )
    try {
        $escapedPackages = $localPackageRoot.Replace("'", "''")
        $probe = "import sys; sys.path.insert(0, r'$escapedPackages'); import soundfile, uvicorn, fastapi, torch, torchaudio, yaml, librosa, transformers; from multipart.multipart import parse_options_header"
        if ($Launcher) {
            & $Candidate -3.10 -c $probe *> $null
        } else {
            & $Candidate -c $probe *> $null
        }
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

$pythonExe = $null
foreach ($candidate in $pythonCandidates) {
    if ((Test-Path -LiteralPath $candidate -PathType Leaf) -and
        (Test-SeedVcPython -Candidate $candidate)) {
        $pythonExe = $candidate
        break
    }
}

if (-not $pythonExe -and (Get-Command py -ErrorAction SilentlyContinue) -and
    (Test-SeedVcPython -Candidate "py" -Launcher)) {
    $pythonExe = "py"
}
if (-not $pythonExe) {
    throw "No SeedVC Python with soundfile/uvicorn/fastapi is available. Run install_seedvc.ps1 or install_soulx.ps1."
}

Set-Location -LiteralPath $seedVcRoot
$env:PYTHONUTF8 = "1"

if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    $resolvedLogPath = [System.IO.Path]::GetFullPath($LogPath)
    $logDirectory = Split-Path -Parent $resolvedLogPath
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    $env:NEEEVA_SEEDVC_LOG = $resolvedLogPath
}

if (-not [string]::IsNullOrWhiteSpace($env:NEEEVA_RVC_PYTHON)) {
    Write-Output "[SeedVC] character RVC runtime=$env:NEEEVA_RVC_PYTHON"
}

if ($pythonExe -eq "py") {
    & $pythonExe -3.10 -u seedvc_server.py --port 9882
} else {
    & $pythonExe -u seedvc_server.py --port 9882
}
$serverExitCode = $LASTEXITCODE
if ($serverExitCode -ne 0) {
    exit $serverExitCode
}
