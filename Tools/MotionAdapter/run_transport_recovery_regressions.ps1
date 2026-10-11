[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Output,
    [ValidateSet('transport','feedback','room','cognition','speech-view','xr-lifecycle','device-sequence')][string]$Focus='transport'
)
$ErrorActionPreference='Stop'
$taskSourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskValidationRoot=[IO.Path]::GetFullPath((Join-Path $taskSourceRoot 'Server/ARDY/runtime/unity-naturalness-validation'))
$taskOutput=[IO.Path]::GetFullPath($Output)
if (-not (Test-Path -LiteralPath (Join-Path $taskValidationRoot 'ProjectSettings/ProjectVersion.txt'))) { throw 'Existing isolated project required.' }
if ((Test-Path -LiteralPath $taskOutput) -and @(Get-ChildItem -LiteralPath $taskOutput -Force).Count) { throw 'Use a fresh evidence directory.' }
[void](New-Item -ItemType Directory -Path $taskOutput -Force)
$taskOwners=@(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object {
    -not $_.CommandLine -or $_.CommandLine.Replace('/','\').IndexOf($taskValidationRoot,[StringComparison]::OrdinalIgnoreCase) -ge 0
})
if ($taskOwners.Count) { throw 'Isolated project is in use.' }
$taskLock=[IO.Path]::GetFullPath((Join-Path $taskValidationRoot 'Temp/UnityLockfile'))
if (-not $taskLock.StartsWith($taskValidationRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid lock path.' }
if (Test-Path -LiteralPath $taskLock) { Move-Item -LiteralPath $taskLock -Destination (Join-Path $taskOutput 'stale-UnityLockfile') }
foreach($taskRetired in @('Assets/AIChatTookit/Scripts/Chat/ChatSample.SpatialSearchPlanning.cs','Assets/AIChatTookit/Scripts/Chat/ChatSample.SpatialSearchPlanning.cs.meta')) {
    if (Test-Path -LiteralPath (Join-Path $taskSourceRoot $taskRetired)) { throw 'Retired planner returned in source.' }
    if (Test-Path -LiteralPath (Join-Path $taskValidationRoot $taskRetired)) { throw 'Isolated project contains retired planner; preserve and remove it before this run.' }
}
$taskCodePaths=@(& rg --files (Join-Path $taskSourceRoot 'Assets') -g '*.cs' -g '*.asmdef' -g '*.asmref' -g '*.rsp')
$taskCodePaths+=@(& rg --files (Join-Path $taskSourceRoot 'Assets/AIChatTookit/Prompts') -g '*.txt')
$taskCodePaths+=@(& rg --files (Join-Path $taskSourceRoot 'Assets/Editor/Fixtures') -g '*.json')
$taskManifest=@(foreach($taskCode in $taskCodePaths) {
    foreach($taskAbsolute in @($taskCode,($taskCode+'.meta'))) {
        if (-not(Test-Path -LiteralPath $taskAbsolute)) { continue }
        $taskRelative=$taskAbsolute.Substring($taskSourceRoot.Length+1)
        $taskDestination=[IO.Path]::GetFullPath((Join-Path $taskValidationRoot $taskRelative))
        if (-not $taskDestination.StartsWith($taskValidationRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Snapshot escaped project.' }
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force)
        Copy-Item -LiteralPath $taskAbsolute -Destination $taskDestination -Force
        @{path=$taskRelative;sha256=(Get-FileHash -LiteralPath $taskAbsolute -Algorithm SHA256).Hash}
    }
})
@{source=$taskSourceRoot;project=$taskValidationRoot;createdUtc=[DateTime]::UtcNow.ToString('o');focus=$Focus;files=$taskManifest} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOutput 'source-manifest.json') -Encoding utf8
$taskMethod=if($Focus -eq 'transport'){'FormalStreamRecoveryRegression.RunBatch'}elseif($Focus -eq 'feedback'){'FeedbackStageHttpRegression.RunBatch'}elseif($Focus -eq 'xr-lifecycle'){'ReloadSafeTrackedDeviceGraphicRaycasterRegression.RunBatch'}else{'NevaSceneVisionIntegrationRegression.RunBatch'}
$taskArguments=@('-batchmode','-projectPath',$taskValidationRoot,'-executeMethod',$taskMethod,'-logFile',(Join-Path $taskOutput 'unity.log'))
if($Focus -eq 'transport') { $taskArguments+=@('-nographics','-formalStreamRecoveryOutput',$taskOutput) }
elseif($Focus -eq 'feedback') { $taskArguments+=@('-nographics','-feedbackStageOutput',$taskOutput) }
elseif($Focus -eq 'xr-lifecycle') { $taskArguments+=@('-nevaXRReloadOutput',$taskOutput) }
elseif($Focus -eq 'cognition') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-roomCognitionRegression','1') }
elseif($Focus -eq 'speech-view') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-roomSpeechViewRegression','1') }
elseif($Focus -eq 'device-sequence') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-nevaUnifiedDevice','1') }
else { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-roomDecisionFailureRegression','1') }
$taskArgumentsQuoted=@($taskArguments | ForEach-Object { '"'+$_+'"' })
$taskProcess=Start-Process -FilePath 'E:/Unity_Data/2022.3.22f1/Editor/Unity.exe' -ArgumentList $taskArgumentsQuoted -PassThru -WindowStyle Hidden
$taskTimedOut=-not $taskProcess.WaitForExit(300000)
if($taskTimedOut) { Stop-Process -Id $taskProcess.Id -ErrorAction SilentlyContinue }
$taskChanges=@(foreach($taskItem in $taskManifest) {
    $taskCurrentHash=(Get-FileHash -LiteralPath (Join-Path $taskSourceRoot $taskItem.path) -Algorithm SHA256).Hash
    $taskTestedHash=(Get-FileHash -LiteralPath (Join-Path $taskValidationRoot $taskItem.path) -Algorithm SHA256).Hash
    if($taskCurrentHash -ne $taskItem.sha256 -or $taskTestedHash -ne $taskItem.sha256) { $taskItem.path }
})
@{passed=($taskChanges.Count -eq 0);fileCount=$taskManifest.Count;changes=$taskChanges;timedOut=$taskTimedOut;exitCode=$taskProcess.ExitCode;checkedUtc=[DateTime]::UtcNow.ToString('o')} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskOutput 'hash-verification.json') -Encoding utf8
if($taskTimedOut) { throw 'Isolated regression timed out.' }
if($taskChanges.Count) { throw 'Source changed during verification; rerun a fresh snapshot.' }
if($taskProcess.ExitCode -ne 0) { throw "Unity exited with $($taskProcess.ExitCode). Inspect this run's log." }
Write-Output "Completed $Focus validation: $taskOutput"
