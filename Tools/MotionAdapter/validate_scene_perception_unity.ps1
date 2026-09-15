[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Output,
    [ValidateSet('integration','camera','tv','attention','grounding','object-approach','smooth-stop','object-light')][string]$Action = 'integration',
    [string]$CaptureDirectory = '',
    [switch]$NativeSceneActions,
    [switch]$LiveSceneQwen,
    [switch]$ProductionSceneAction,
    [switch]$ActiveSceneView,
    [switch]$SceneObjects,
    [switch]$LiveObjectReopen,
    [switch]$ObjectMatching,
    [switch]$ObjectApproachNative,
    [switch]$LiveObjectMatching,
    [switch]$MotionReadiness,
    [switch]$DecisionRepair,
    [switch]$DecisionRepairNative,
    [switch]$HalfTurnNative,
    [switch]$ViewPosition,
    [switch]$ViewPositionNative,
    [switch]$ResponseLatency,
    [switch]$FirstAudioRegression,
    [ValidateRange(-1,6)][int]$RecordedFurnitureIndex = -1,
    [ValidateRange(-60,60)][float]$SceneProbeYaw = 0,
    [string]$Project = '',
    [string]$Unity = 'E:/Unity_Data/2022.3.22f1/Editor/Unity.exe'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $Project) { $Project = Join-Path $repository 'Server/ARDY/runtime/unity-naturalness-validation' }
$projectPath = [IO.Path]::GetFullPath($Project)
if ([IO.Path]::GetFileName($projectPath.TrimEnd('\','/')) -ne 'unity-naturalness-validation' -or
    -not (Test-Path -LiteralPath (Join-Path $projectPath 'ProjectSettings/ProjectVersion.txt'))) {
    throw 'Only the existing isolated unity-naturalness-validation project is supported. Sync its sources first.'
}
if (Test-Path -LiteralPath (Join-Path $projectPath 'Temp/UnityLockfile')) { throw 'The isolated validation project is already open.' }
$destination = [IO.Path]::GetFullPath($Output)
if ((Test-Path -LiteralPath $destination) -and @(Get-ChildItem -LiteralPath $destination -Force).Count) {
    throw 'Use an empty evidence directory.'
}
$methods = @{ integration='NevaSceneVisionIntegrationRegression.RunBatch'; camera='NevaSceneVisionCaptureRegression.RunBatch'; tv='NevaDesktopTvVisibilityRegression.RunBatch'; attention='NevaSceneAttentionRegression.RunBatch'; grounding='SceneVisionGroundingRegression.RunBatch'; 'object-approach'='ArdyRoomObjectApproachRegression.RunBatch'; 'smooth-stop'='ArdyLocomotionSmoothStopRegression.RunBatch'; 'object-light'='SceneObjectLightRegression.RunBatch' }
# A Null graphics device cannot prove Camera.Render/ReadPixels worked. Keep graphics enabled,
# while hiding only the launched validation window. This never opens or saves the user's scene.
$arguments = @('-batchmode','-projectPath',$projectPath,'-executeMethod',$methods[$Action],
    '-logFile',(Join-Path $destination 'unity.log'),'-nevaSceneVisionOutput',$destination)
if ($NativeSceneActions) { if ($Action -ne 'integration' -or $ProductionSceneAction) { throw 'Native scene actions require a separate integration run.' }; $arguments += @('-nevaSceneActionNative','1') }
if ($LiveSceneQwen) {
    if ($Action -ne 'integration' -or $NativeSceneActions -or $ProductionSceneAction) { throw 'Live Qwen requires a separate integration run.' }
    $arguments += @('-nevaSceneActionQwen','1')
}
if ($ProductionSceneAction) {
    if ($Action -ne 'integration') { throw 'Production scene action requires integration.' }
    $arguments += @('-nevaSceneActionProduction','1')
}
if ($ActiveSceneView) {
    if ($Action -ne 'integration' -or $NativeSceneActions -or $LiveSceneQwen -or $ProductionSceneAction) { throw 'Active view requires a separate integration run.' }
    $arguments += @('-nevaSceneActiveView','1')
}
if ($SceneObjects) {
    if ($Action -ne 'integration' -or $NativeSceneActions -or $LiveSceneQwen -or $ProductionSceneAction -or $ActiveSceneView) { throw 'Scene objects require a separate integration run.' }
    $arguments += @('-nevaSceneObjects','1')
}
if ($LiveObjectReopen) {
    if (-not $SceneObjects -or $Action -ne 'integration') { throw 'Live object reopening requires the explicit scene-object integration run.' }
    $arguments += @('-nevaSceneObjectReopenQwen','1')
}
if ($ObjectMatching -or $ObjectApproachNative) {
    if ($Action -ne 'integration' -or $NativeSceneActions -or $LiveSceneQwen -or $ProductionSceneAction -or $ActiveSceneView -or $LiveObjectReopen) { throw 'Object matching/approach need their own explicit integration run.' }
    if ($ObjectMatching) { $arguments += @('-nevaSceneObjectMatching','1') }
    if ($ObjectApproachNative) { $arguments += @('-nevaSceneObjectApproachNative','1') }
}
if ($LiveObjectMatching) {
    if (-not $ObjectMatching) { throw 'Live object matching requires explicit ObjectMatching integration.' }
    $arguments += @('-nevaSceneObjectMatchingQwen','1')
}
if ($MotionReadiness) {
    if ($Action -ne 'integration') { throw 'Motion readiness requires integration.' }
    $arguments += @('-nevaRoomMotionReadiness','1')
}
if ($DecisionRepair -or $DecisionRepairNative -or $HalfTurnNative) {
    if ($Action -ne 'integration') { throw 'Decision repair / half turn requires integration.' }
    if ($DecisionRepairNative -and $HalfTurnNative) { throw 'Run the two native fixtures separately.' }
    if ($DecisionRepair) { $arguments += @('-nevaDecisionRepair','1') }
    if ($DecisionRepairNative) { $arguments += @('-nevaDecisionRepairNative','1') }
    if ($HalfTurnNative) { $arguments += @('-nevaHalfTurnNative','1') }
}
if ($RecordedFurnitureIndex -ge 0) {
    if (-not $ObjectApproachNative -or $Action -ne 'integration') { throw 'Recorded furniture requires explicit native object approach.' }
    $arguments += @('-nevaRecordedFurnitureIndex',[string]$RecordedFurnitureIndex)
}
if ($ViewPosition -or $ViewPositionNative) {
    if ($Action -ne 'integration' -or ($ViewPosition -and $ViewPositionNative) -or $DecisionRepairNative -or $HalfTurnNative -or $ObjectApproachNative -or $LiveSceneQwen -or $ProductionSceneAction) { throw 'Run each view-position fixture as a separate integration run.' }
    if ($ViewPosition) { $arguments += @('-nevaViewPosition','1') }
    if ($ViewPositionNative) { $arguments += @('-nevaViewPositionNative','1') }
}
if ($ResponseLatency) {
    if ($Action -ne 'integration' -or $ViewPosition -or $ViewPositionNative -or $DecisionRepairNative -or $HalfTurnNative -or $ObjectApproachNative -or $LiveSceneQwen -or $ProductionSceneAction) { throw 'Response latency requires its own integration run.' }
    $arguments += @('-nevaResponseLatency','1')
}
if ($FirstAudioRegression) {
    if ($Action -ne 'integration' -or $ResponseLatency) { throw 'First-audio controlled regressions require a separate integration run.' }
    $arguments += @('-nevaFirstAudioRegression','1')
}
if ($SceneProbeYaw -ne 0) {
    if (-not $LiveSceneQwen) { throw 'A separate native observation orientation requires the explicit live Qwen run.' }
    $arguments += @('-nevaSceneProbeYaw',$SceneProbeYaw.ToString([Globalization.CultureInfo]::InvariantCulture))
}
if ($Action -eq 'tv') { $arguments += @('-nevaTvVisibilityOutput',$destination) }
if ($Action -eq 'attention') { $arguments += @('-nevaSceneAttentionOutput',$destination) }
if ($Action -eq 'object-approach') { $arguments += @('-ardyRoomObjectApproachOutput',$destination) }
if ($Action -eq 'object-light') { $arguments += @('-sceneObjectLightOutput',$destination) }
if ($Action -eq 'smooth-stop') { $arguments += '-quit'; $env:ARDY_SMOOTH_STOP_OUTPUT = Join-Path $destination 'report.json' }
if ($CaptureDirectory) { $arguments += @('-nevaSceneVisionCaptureDirectory',[IO.Path]::GetFullPath($CaptureDirectory)) }
$quoted = $arguments | ForEach-Object { if ($_ -match '"') { throw 'Unexpected quote in argument.' }; '"' + $_ + '"' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $Unity -ArgumentList $quoted -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $destination 'stdout.txt') -RedirectStandardError (Join-Path $destination 'stderr.txt')
# Retain the process handle so a fast exit still has a readable ExitCode on Windows PowerShell.
$null = $process.Handle
$process.Id | Set-Content -LiteralPath (Join-Path $destination 'process-id.txt')
$timedOut = $false
while (-not $process.WaitForExit(1000)) {
    if (([DateTime]::UtcNow - $started).TotalSeconds -gt 300) {
        $timedOut = $true
        $process.Kill()
        $process.WaitForExit()
        break
    }
}
$result = [ordered]@{ action=$Action; exitCode=$process.ExitCode; timedOut=$timedOut;
    graphicsEnabled=$true; seconds=([DateTime]::UtcNow-$started).TotalSeconds;
    isolatedProject=$projectPath; startedUtc=$started.ToString('o'); executeMethod=$methods[$Action] }
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'process-result.json') -Encoding utf8
$result | ConvertTo-Json -Compress | Write-Output
if ($timedOut -or $process.ExitCode -ne 0) { throw "Scene vision $Action did not pass. See $destination" }
if (Select-String -LiteralPath (Join-Path $destination 'unity.log') -Pattern 'error CS[0-9]{4}' -Quiet) {
    throw "C# compilation errors invalidate this run. See $destination"
}
