param(
    [string]$Python = '',
    [string]$Checkpoint = '',
    [string]$Labels = '',
    [string]$Dependencies = '',
    [string]$DataDirectory = '',
    [ValidateSet('cpu','cuda')][string]$Device = 'cpu',
    [ValidateRange(1,16)][int]$CpuThreads = 2,
    [ValidateRange(1024,65535)][int]$Port = 9884
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$developmentAssets = Join-Path $projectRoot 'work/singing-subsystem-20260929/segmentation'
if (!$Python) { $Python = Join-Path $projectRoot 'Server/SenseVoice/runtime/.venv-cuda/Scripts/python.exe' }
if (!$Checkpoint) { $Checkpoint = Join-Path $developmentAssets 'models/Cnn14_DecisionLevelMax_mAP=0.385.pth' }
if (!$Labels) { $Labels = Join-Path $developmentAssets 'models/class_labels_indices.csv' }
if (!$Dependencies) { $Dependencies = Join-Path $developmentAssets 'deps' }
if (!$DataDirectory) { $DataDirectory = Join-Path $projectRoot 'Server/SingingLearning/data' }
foreach ($file in @($Python,$Checkpoint,$Labels,(Join-Path $Dependencies 'panns_inference/models.py'))) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing runtime asset: $file. Supply the matching parameter; this launcher does not download or install dependencies." }
}
$healthUrl = 'http://127.0.0.1:' + $Port + '/health'
try { $existing = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 2 } catch { $existing = $null }
if ($existing -and $existing.service -eq 'singing-learning' -and $existing.schema -eq 1) {
    Write-Output ('Personal singing learning is already running at http://127.0.0.1:' + $Port)
    exit 0
}
if ($existing) { throw 'A different service already uses this port. Select another port.' }
[void](New-Item -ItemType Directory -Path $DataDirectory -Force)
$runDirectory = Join-Path $DataDirectory ('logs/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
[void](New-Item -ItemType Directory -Path $runDirectory -Force)
$env:SINGING_LEARNING_DATA = [IO.Path]::GetFullPath($DataDirectory)
$env:SINGING_LEARNING_CHECKPOINT = [IO.Path]::GetFullPath($Checkpoint)
$env:SINGING_LEARNING_LABELS = [IO.Path]::GetFullPath($Labels)
$env:SINGING_LEARNING_DEPS = [IO.Path]::GetFullPath($Dependencies)
$env:SINGING_LEARNING_DEVICE = $Device
$env:SINGING_LEARNING_TORCH_THREADS = [string]$CpuThreads
$env:SINGING_LEARNING_HOST = '127.0.0.1'
$env:SINGING_LEARNING_PORT = [string]$Port
$learningProcess = Start-Process -FilePath $Python -ArgumentList @('-m','Server.SingingLearning') -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runDirectory 'stdout.log') -RedirectStandardError (Join-Path $runDirectory 'stderr.log')
$metadata = @{ pid=$learningProcess.Id; url=('http://127.0.0.1:'+$Port); device=$Device; cpu_threads=$CpuThreads; data=$env:SINGING_LEARNING_DATA; checkpoint=$env:SINGING_LEARNING_CHECKPOINT; started_utc=[DateTime]::UtcNow.ToString('o') }
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'process.json') -Encoding utf8
for ($attempt=0; $attempt -lt 75; $attempt++) {
    if ($learningProcess.HasExited) { throw ('Learning service exited. See ' + (Join-Path $runDirectory 'stderr.log')) }
    try {
        $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 1
        if ($health.service -eq 'singing-learning' -and $health.ok) {
            Write-Output ('Personal singing learning ready: http://127.0.0.1:' + $Port)
            Write-Output ('Original audio and confirmed labels: ' + $env:SINGING_LEARNING_DATA)
            Write-Output ('Worker: ' + $Device + '; ' + $CpuThreads + ' CPU threads. Models load on first analysis.')
            Write-Output ('Process record and logs: ' + $runDirectory)
            exit 0
        }
    } catch { }
    Start-Sleep -Milliseconds 200
}
throw ('Service has not become healthy. Inspect ' + $runDirectory + '; the launcher has not killed any process.')
