[CmdletBinding()]
param([int]$Epochs = 100)
$ErrorActionPreference = 'Stop'
if ($Epochs -lt 1) { throw 'Epochs must be positive.' }
$ProjectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$Python = Join-Path $ProjectRoot 'Server\ARDY\.venv\Scripts\python.exe'
$Runtime = Join-Path $PSScriptRoot 'runtime'
if (-not (Test-Path -LiteralPath (Join-Path $Runtime 'teacher-2000.npz'))) {
    throw 'Export the real teacher features before training.'
}
Push-Location $ProjectRoot
try {
    foreach ($Architecture in @('linear','mlp')) {
        foreach ($Seed in @(0,1)) {
            Write-Output ("Training $Architecture with seed $Seed")
            $Output = Join-Path $Runtime ($Architecture + '-seed' + $Seed + '.pt')
            & $Python -m Tools.MotionAdapter.train --dataset (Join-Path $Runtime 'prompts.jsonl') `
                --qwen (Join-Path $Runtime 'qwen-2000.npz') --teacher (Join-Path $Runtime 'teacher-2000.npz') `
                --architecture $Architecture --seed $Seed --epochs $Epochs --output $Output
            if ($LASTEXITCODE -ne 0) { throw "Training failed for $Architecture seed $Seed" }
        }
    }
    & $Python -m Tools.MotionAdapter.summarize_training --directory $Runtime --output (Join-Path $PSScriptRoot 'reports\adapter-pilot.json')
    if ($LASTEXITCODE -ne 0) { throw 'Training comparison failed.' }
} finally { Pop-Location }
