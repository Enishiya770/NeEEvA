[CmdletBinding(DefaultParameterSetName = 'Automatic')]
param(
    [int]$Port = 8093,
    [string]$FeatureUrl = 'http://127.0.0.1:8080',
    [string]$ExpectedModelSha256 = '',
    [switch]$TestFixture,
    [Parameter(Mandatory = $true, ParameterSetName = 'Baseline')]
    [switch]$BaselineAdapter,
    [Parameter(Mandatory = $true, ParameterSetName = 'Release')]
    [ValidateNotNullOrEmpty()]
    [string]$AdapterRelease,
    [Parameter(Mandatory = $true, ParameterSetName = 'Selection')]
    [ValidateNotNullOrEmpty()]
    [string]$AdapterSelection
)
$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$Python = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $Python)) { throw 'Run Server/ARDY/install_core.ps1 first.' }
$Arguments = @('-m', 'Server.ARDY.motion_service.app', '--port', $Port, '--feature-url', $FeatureUrl)
if ($ExpectedModelSha256) { $Arguments += @('--expected-model-sha256', $ExpectedModelSha256) }
if ($TestFixture) { $Arguments += '--test-fixture' }
if ($BaselineAdapter) { $Arguments += '--baseline-adapter' }
if ($AdapterRelease) { $Arguments += @('--adapter-release', $AdapterRelease) }
if ($AdapterSelection) { $Arguments += @('--adapter-selection', $AdapterSelection) }
Push-Location -LiteralPath $ProjectRoot
try {
    & $Python @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'ARDY motion service exited unsuccessfully.' }
} finally { Pop-Location }
