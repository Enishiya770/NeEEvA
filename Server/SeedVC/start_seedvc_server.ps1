param(
    [string]$LogPath = "",
    [string]$PythonPath = ""
)

$ErrorActionPreference = "Stop"

$seedVcRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent (Split-Path -Parent $seedVcRoot)
$pythonCandidates = @(
    $PythonPath,
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

Set-Location -LiteralPath $seedVcRoot
$env:PYTHONUTF8 = "1"

if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    $resolvedLogPath = [System.IO.Path]::GetFullPath($LogPath)
    $logDirectory = Split-Path -Parent $resolvedLogPath
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    $env:NEEEVA_SEEDVC_LOG = $resolvedLogPath
    if ($pythonExe -eq "py") {
        & $pythonExe -3.10 -u seedvc_server.py --port 9882
    } else {
        & $pythonExe -u seedvc_server.py --port 9882
    }
} elseif ($pythonExe -eq "py") {
    & $pythonExe -3.10 -u seedvc_server.py --port 9882
} else {
    & $pythonExe -u seedvc_server.py --port 9882
}
