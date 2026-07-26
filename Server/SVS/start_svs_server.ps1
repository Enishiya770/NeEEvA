param(
    [string]$LogPath = "",
    [string]$PythonPath = ""
)

$ErrorActionPreference = "Stop"
$svsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent (Split-Path -Parent $svsRoot)
$pythonCandidates = @(
    $PythonPath,
    $env:NEEEVA_SVS_PYTHON,
    (Join-Path $svsRoot ".venv\Scripts\python.exe"),
    $env:NEEEVA_PYTHON_EXE,
    $(if ($env:NEEEVA_GPT_SOVITS_ROOT) {
        Join-Path $env:NEEEVA_GPT_SOVITS_ROOT "runtime\python.exe"
    }),
    (Join-Path $projectRoot "GPT-SoVITS\runtime\python.exe")
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

function Test-SVSPython {
    param(
        [string]$Candidate,
        [switch]$Launcher
    )
    try {
        if ($Launcher) {
            & $Candidate -3.10 -c "import soundfile, uvicorn, fastapi; from multipart.multipart import parse_options_header" *> $null
        } else {
            & $Candidate -c "import soundfile, uvicorn, fastapi; from multipart.multipart import parse_options_header" *> $null
        }
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

$pythonExe = $null
foreach ($candidate in $pythonCandidates) {
    if ((Test-Path -LiteralPath $candidate -PathType Leaf) -and
        (Test-SVSPython -Candidate $candidate)) {
        $pythonExe = $candidate
        break
    }
}
if (-not $pythonExe -and (Get-Command py -ErrorAction SilentlyContinue) -and
    (Test-SVSPython -Candidate "py" -Launcher)) {
    $pythonExe = "py"
}
if (-not $pythonExe) {
    throw "No compatible Python was found. Run install_soulx.ps1 or set NEEEVA_SVS_PYTHON."
}

Set-Location -LiteralPath $svsRoot
$env:PYTHONUTF8 = "1"
if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    $resolvedLogPath = [System.IO.Path]::GetFullPath($LogPath)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolvedLogPath) |
        Out-Null
    $env:NEEEVA_SVS_LOG = $resolvedLogPath
}
if ($pythonExe -eq "py") {
    & $pythonExe -3.10 -u svs_server.py --port 9883
} else {
    & $pythonExe -u svs_server.py --port 9883
}
$serverExitCode = $LASTEXITCODE
if ($serverExitCode -ne 0) {
    exit $serverExitCode
}
