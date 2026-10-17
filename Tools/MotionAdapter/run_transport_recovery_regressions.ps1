[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Output,
    [ValidateSet('transport','feedback','room','cognition','speech-view','xr-lifecycle','device-sequence','work-loop','approach','preparation','approach-plan','private-review','quality-recovery','quality-speech','conversation-repair','stream-delay','arrival','stream-pure','control-recovery','follow-handoff','activity-monitor','activity-runtime','activity-handoff','activity-evidence','activity-continuity','task-handoff','spatial-recall','compound-delivery','lifecycle-grounding','routing-delivery','semantic-playback','clarification-delivery','result-autonomy','runtime-failure','activity-origin','budget-delivery','semantic-window','playback-evidence','claim-playback','audio-source')][string]$Focus='transport',
    [string]$LifecycleInput='',
    [string]$QualitySpeechInput='',
    [string]$ConversationRepairInput='',
    [string]$LocomotionClip=''
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
$taskMethod=if($Focus -eq 'transport'){'FormalStreamRecoveryRegression.RunBatch'}elseif($Focus -eq 'feedback'){'FeedbackStageHttpRegression.RunBatch'}elseif($Focus -eq 'xr-lifecycle'){'ReloadSafeTrackedDeviceGraphicRaycasterRegression.RunBatch'}elseif($Focus -eq 'work-loop'){'AgentWorkLoopRegression.RunBatch'}elseif($Focus -eq 'approach'){'ArdyRoomDialogueIntegrationRegression.RunBatch'}elseif($Focus -eq 'approach-plan'){'ArdyRoomApproachRegression.RunBatch'}elseif($Focus -eq 'quality-recovery'){'ArdyRoomStreamDelayRegression.RunBatch'}else{'NevaSceneVisionIntegrationRegression.RunBatch'}
$taskArguments=@('-batchmode','-projectPath',$taskValidationRoot,'-executeMethod',$taskMethod,'-logFile',(Join-Path $taskOutput 'unity.log'))
if($Focus -eq 'lifecycle-grounding') { $taskArguments+=@('-lifecycleGroundingRuntime','1') }
if($Focus -eq 'activity-monitor') {
    $taskArguments=@('-batchmode','-nographics','-projectPath',$taskValidationRoot,'-executeMethod','ActivityMonitorRegression.RunBatch',
        '-logFile',(Join-Path $taskOutput 'unity.log'),'-activityMonitorOutput',$taskOutput)
}
if($Focus -eq 'preparation') {
    $taskArguments=@('-batchmode','-projectPath',$taskValidationRoot,'-executeMethod','ArdyRoomDialogueIntegrationRegression.RunBatch','-logFile',(Join-Path $taskOutput 'unity.log'),'-ardyRoomDialogueOutput',$taskOutput,'-ardyPreparationOnly')
}
if($Focus -eq 'stream-delay') {
    $taskArguments=@('-batchmode','-projectPath',$taskValidationRoot,'-executeMethod','ArdyRoomStreamDelayRegression.RunBatch','-logFile',(Join-Path $taskOutput 'unity.log'),'-ardyRoomOutput',$taskOutput)
}
if($Focus -eq 'stream-pure') {
    $taskArguments=@('-batchmode','-nographics','-projectPath',$taskValidationRoot,'-executeMethod','ArdyRoomStreamPureRegression.RunBatch','-logFile',(Join-Path $taskOutput 'unity.log'),'-ardyRoomOutput',$taskOutput)
}
if($Focus -eq 'control-recovery') {
    $taskArguments=@('-batchmode','-projectPath',$taskValidationRoot,'-executeMethod','ArdyRoomStreamDelayRegression.RunBatch','-logFile',(Join-Path $taskOutput 'unity.log'),'-ardyRoomOutput',$taskOutput,'-ardyControlRecoveryOnly')
}
if($Focus -eq 'follow-handoff') {
    $taskArguments=@('-batchmode','-projectPath',$taskValidationRoot,'-executeMethod','ArdyRoomStreamDelayRegression.RunBatch','-logFile',(Join-Path $taskOutput 'unity.log'),'-ardyRoomOutput',$taskOutput,'-ardyFollowHandoffOnly')
}
if($Focus -eq 'arrival') {
    if(-not(Test-Path -LiteralPath $LocomotionClip -PathType Leaf)) { throw 'Existing real locomotion response required.' }
    $taskArguments=@('-batchmode','-projectPath',$taskValidationRoot,'-executeMethod','ArdyRoomPoseRecorderRegression.RunBatch','-logFile',(Join-Path $taskOutput 'unity.log'),'-ardyRoomOutput',$taskOutput,'-ardyLocomotionClip',[IO.Path]::GetFullPath($LocomotionClip))
}
if($Focus -eq 'conversation-repair') {
    if (-not (Test-Path -LiteralPath $ConversationRepairInput -PathType Leaf)) { throw 'Recorded conversation fixture required.' }
    $taskArguments=@('-batchmode','-projectPath',$taskValidationRoot,'-executeMethod','FormalStreamRecoveryRegression.RunBatch','-logFile',(Join-Path $taskOutput 'unity.log'),'-formalStreamRecoveryOutput',$taskOutput,'-conversationRepairInput',[IO.Path]::GetFullPath($ConversationRepairInput))
}
if($Focus -eq 'transport') { $taskArguments+=@('-nographics','-formalStreamRecoveryOutput',$taskOutput) }
elseif($Focus -eq 'work-loop') { $taskArguments+=@('-nographics','-workNextIntentOnly') }
elseif($Focus -eq 'approach-plan') { $taskArguments+=@('-nographics','-ardyRoomApproachOutput',$taskOutput) }
elseif($Focus -eq 'approach') { $taskArguments+=@('-ardyRoomDialogueOutput',$taskOutput,'-ardyApproachRetargetOnly') }
elseif($Focus -eq 'feedback') { $taskArguments+=@('-nographics','-feedbackStageOutput',$taskOutput) }
elseif($Focus -eq 'xr-lifecycle') { $taskArguments+=@('-nevaXRReloadOutput',$taskOutput) }
elseif($Focus -eq 'private-review') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-privateReviewRegression','1') }
elseif($Focus -eq 'quality-recovery') { $taskArguments+=@('-ardyRoomOutput',$taskOutput,'-ardyQualityRecoveryOnly') }
elseif($Focus -eq 'quality-speech') {
    if (-not (Test-Path -LiteralPath (Join-Path $QualitySpeechInput 'latest-quality-follow-failure/review-input.json'))) { throw 'Explicit recorded speech input is required.' }
    $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-qualitySpeechInput',[IO.Path]::GetFullPath($QualitySpeechInput))
}
elseif($Focus -eq 'activity-handoff') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-activityInterventionRuntime','1') }
elseif($Focus -eq 'activity-evidence') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-activityEvidenceRuntime','1') }
elseif($Focus -eq 'activity-continuity') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-activityContinuityRuntime','1') }
elseif($Focus -eq 'spatial-recall') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-spatialRecallRuntime','1') }
elseif($Focus -eq 'compound-delivery') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-compoundDeliveryRuntime','1') }
elseif($Focus -eq 'lifecycle-grounding') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput) }
elseif($Focus -eq 'result-autonomy') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-resultAutonomyRuntime','1') }
elseif($Focus -eq 'budget-delivery') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-budgetDeliveryRuntime','1') }
elseif($Focus -eq 'audio-source') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-audioSourceRuntime','1') }
elseif($Focus -eq 'claim-playback') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-claimPlaybackRuntime','1') }
elseif($Focus -eq 'playback-evidence') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-playbackEvidenceRuntime','1') }
elseif($Focus -eq 'semantic-window') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-semanticWindowRuntime','1') }
elseif($Focus -eq 'activity-origin') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-activityOriginRuntime','1') }
elseif($Focus -eq 'runtime-failure') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-runtimeFailureRuntime','1') }
elseif($Focus -eq 'clarification-delivery') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-clarificationDeliveryRuntime','1') }
elseif($Focus -eq 'semantic-playback') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-semanticPlaybackRuntime','1') }
elseif($Focus -eq 'routing-delivery') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-routingDeliveryRuntime','1') }
elseif($Focus -eq 'task-handoff') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-taskHandoffRuntime','1') }
elseif($Focus -eq 'activity-runtime') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-activityMonitorRuntime','1') }
elseif($Focus -eq 'conversation-repair' -or $Focus -eq 'preparation' -or $Focus -eq 'activity-monitor') { }
elseif($Focus -eq 'stream-delay' -or $Focus -eq 'arrival' -or $Focus -eq 'follow-handoff' -or $Focus -eq 'control-recovery' -or $Focus -eq 'stream-pure') { }
elseif($Focus -eq 'cognition') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-roomCognitionRegression','1') }
elseif($Focus -eq 'speech-view') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-roomSpeechViewRegression','1') }
elseif($Focus -eq 'device-sequence') { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-nevaUnifiedDevice','1') }
else { $taskArguments+=@('-nevaSceneVisionOutput',$taskOutput,'-roomDecisionFailureRegression','1') }
if ($LifecycleInput) {
    if ($Focus -ne 'transport' -or -not (Test-Path -LiteralPath $LifecycleInput -PathType Leaf)) { throw 'LifecycleInput requires transport focus and an existing explicit local-model case file.' }
    $taskArguments+=@('-attentionLifecycleInput',[IO.Path]::GetFullPath($LifecycleInput))
}
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
if($Focus -eq 'work-loop') { Copy-Item -LiteralPath (Join-Path $taskValidationRoot 'Tools/MotionAdapter/reports/agent-work-loop-regression.json') -Destination $taskOutput }
if($Focus -eq 'work-loop') { Copy-Item -LiteralPath (Join-Path $taskValidationRoot 'Tools/MotionAdapter/reports/work-next-intent-model-cases.json') -Destination $taskOutput }
if($taskProcess.ExitCode -ne 0) { throw "Unity exited with $($taskProcess.ExitCode). Inspect this run's log." }
if(Select-String -LiteralPath (Join-Path $taskOutput 'unity.log') -Pattern 'error CS\d+|Scripts have compiler errors' -Quiet) { throw 'Unity compilation failed even though its process returned zero.' }
if($Focus -eq 'activity-monitor' -and -not(Test-Path -LiteralPath (Join-Path $taskOutput 'regression.json'))) { throw 'Activity regression did not execute.' }
Write-Output "Completed $Focus validation: $taskOutput"
