[CmdletBinding()]
param(
    [ValidateSet('audit','navigation','integration','actual-room','locomotion','room-dialogue-smoke','room-approach','room-dialogue','room-segment','locomotion-stop','room-stream','room-stream-pure','room-chat-stream','room-chat-lifecycle','room-stream-delay','room-boundary','room-boundary-runtime','room-boundary-ownership','room-pose-recorder','room-lowtable-geometry','room-lowtable-stream','room-braking-export','room-braking-linear')][string]$Action = 'audit',
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$Avatar = '',
    [string]$BrakingCases = '',
    [string]$RecordedSeams = '',
    [int]$BrakingBranchOffset = 0,
    [string]$LocomotionClip = '',
    [string]$RoomScene = '',
    [string]$RoomStart = '',
    [string]$RoomTarget = '',
    [string]$RoomUser = '',
    [string]$EnvironmentName = '',
    [string]$ExcludedVisualRoots = '',
    [switch]$DisabledVrm,
    [float]$InitialYaw = 37,
    [float]$AvatarScale = .9,
    [switch]$ExtendedRuntime,
    [switch]$ContinuityRuntime,
    [switch]$StartIsRoot
)
$ErrorActionPreference = 'Stop'
$isolatedProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime/unity-naturalness-validation'))
if (-not (Test-Path -LiteralPath (Join-Path $isolatedProject 'ProjectSettings/ProjectVersion.txt'))) { throw 'Prepare the isolated validation project first.' }
if (Test-Path -LiteralPath (Join-Path $isolatedProject 'Temp/UnityLockfile')) { throw 'The isolated project is already open.' }
$destination = [IO.Path]::GetFullPath($Output)
if ((Test-Path -LiteralPath $destination) -and @(Get-ChildItem -LiteralPath $destination -Force).Count) { throw 'Use an empty evidence directory.' }
$methods = @{ audit='ArdyRoomTerrainAudit.RunBatch'; navigation='ArdyRoomNavigationRegression.RunBatch'; integration='ArdyRoomIntegrationRegression.RunBatch'; 'actual-room'='ArdyRoomActualRoomRegression.RunBatch'; locomotion='ArdyLocomotionRegression.RunBatch'; 'room-dialogue-smoke'='ArdyRoomDialogueSmokeRegression.RunBatch'; 'room-approach'='ArdyRoomApproachRegression.RunBatch'; 'room-dialogue'='ArdyRoomDialogueIntegrationRegression.RunBatch'; 'room-segment'='ArdyRoomSegmentRegression.RunBatch'; 'locomotion-stop'='ArdyLocomotionSmoothStopRegression.RunBatch' }
$methods['room-stream'] = 'ArdyRoomStreamRuntimeRegression.RunBatch'
$methods['room-stream-pure'] = 'ArdyRoomStreamPureRegression.RunBatch'
$methods['room-chat-stream'] = 'ArdyRoomChatStreamRuntimeRegression.RunBatch'
$methods['room-chat-lifecycle'] = 'ArdyRoomChatStreamRuntimeRegression.RunLifecycleBatch'
$methods['room-stream-delay'] = 'ArdyRoomStreamDelayRegression.RunBatch'
$methods['room-boundary'] = 'ArdyRoomBoundaryRecoveryRegression.RunBatch'
$methods['room-boundary-runtime'] = 'ArdyRoomBoundaryRecoveryRuntimeRegression.RunBatch'
$methods['room-boundary-ownership'] = 'ArdyRoomBoundaryRecoveryRuntimeRegression.RunOwnershipBatch'
$methods['room-pose-recorder'] = 'ArdyRoomPoseRecorderRegression.RunBatch'
$methods['room-lowtable-geometry'] = 'ArdyRoomLowTableStreamRegression.RunGeometryBatch'
$methods['room-lowtable-stream'] = 'ArdyRoomLowTableStreamRegression.RunBatch'
$methods['room-braking-export'] = 'ArdyRoomBrakingTargetExport.RunBatch'
$methods['room-braking-linear'] = 'ArdyRoomBrakingTargetExport.RunLinearBatch'
$arguments = @('-batchmode','-nographics','-projectPath',$isolatedProject,'-executeMethod',$methods[$Action],'-logFile',(Join-Path $destination 'unity.log'),'-ardyRoomNavigationOutput',$destination,'-ardyRoomOutput',$destination,'-ardyLocomotionOutput',$destination)
$arguments += @('-ardyRoomApproachOutput',$destination,'-ardyRoomDialogueOutput',$destination)
if ($Action -eq 'locomotion-stop') {
    $arguments += '-quit'
    $env:ARDY_SMOOTH_STOP_OUTPUT = Join-Path $destination 'smooth-stop.json'
}
if ($ExtendedRuntime) { $arguments += @('-ardyRoomExtended','true') }
if ($ContinuityRuntime) {
    if ($Action -ne 'room-dialogue' -or $ExtendedRuntime -or $RoomScene) { throw 'ContinuityRuntime uses the isolated flat room-dialogue fixture only.' }
    $arguments += @('-ardyRoomContinuity','true')
}
if ($RoomUser) { $arguments += @('-ardyRoomUser',$RoomUser) }
if ($BrakingCases) { $arguments += @('-ardyBrakingCases',[IO.Path]::GetFullPath($BrakingCases),'-ardyBrakingBranchOffset',$BrakingBranchOffset.ToString([Globalization.CultureInfo]::InvariantCulture)) }
if ($RecordedSeams) { $arguments += @('-ardyRoomRecordedSeams',[IO.Path]::GetFullPath($RecordedSeams)) }
if ($Avatar) { $arguments += @('-ardyRoomAvatar',$Avatar) }
if ($LocomotionClip) { $arguments += @('-ardyLocomotionClip',[IO.Path]::GetFullPath($LocomotionClip)) }
if ($DisabledVrm) {
    if ($Action -notin @('integration','room-dialogue')) { throw 'DisabledVrm is only supported for integration.' }
    $arguments += @('-ardyRoomDisabledVrm','true')
}
if ($Action -in @('integration','room-dialogue')) {
    $arguments += @('-ardyRoomInitialYaw',$InitialYaw.ToString([Globalization.CultureInfo]::InvariantCulture),
                   '-ardyRoomAvatarScale',$AvatarScale.ToString([Globalization.CultureInfo]::InvariantCulture))
    if ($StartIsRoot) { $arguments += @('-ardyRoomStartIsRoot','true') }
}
if ($RoomScene) {
    if ($Action -notin @('integration','room-dialogue') -or -not $RoomStart -or ($Action -eq 'integration' -and -not $RoomTarget) -or ($Action -eq 'room-dialogue' -and -not $RoomUser) -or $RoomScene -like '*_Chat*') { throw 'Room integration requires a pure environment scene and explicit start/target.' }
    $arguments += @('-ardyRoomScene',$RoomScene,'-ardyRoomStart',$RoomStart,'-ardyRoomTarget',$RoomTarget)
}
if ($EnvironmentName) { $arguments += @('-ardyRoomEnvironmentName',$EnvironmentName) }
if ($ExcludedVisualRoots) { $arguments += @('-ardyRoomExcludedVisualRoots',$ExcludedVisualRoots) }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$env:ARDY_ROOM_AUDIT_OUTPUT = $destination
$env:ARDY_ROOM_ACTUAL_OUTPUT = $destination
if ($ExcludedVisualRoots) { $env:ARDY_ROOM_ACTUAL_NON_SOLID_NAMES = $ExcludedVisualRoots }
$quoted = $arguments | ForEach-Object { if ($_ -match '"') { throw 'Unexpected quote in Unity argument' }; '"' + $_ + '"' }
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath 'E:/Unity_Data/2022.3.22f1/Editor/Unity.exe' -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $destination 'stdout.txt') -RedirectStandardError (Join-Path $destination 'stderr.txt')
$process.Id | Set-Content -LiteralPath (Join-Path $destination 'process-id.txt')
while (-not $process.WaitForExit(1000)) {
    if (([DateTime]::UtcNow - $started).TotalSeconds -gt 420) {
        $process.Kill()
        throw 'Isolated room validation timed out; stopped only this launched process.'
    }
}
$result = [ordered]@{ action=$Action; exitCode=$process.ExitCode; seconds=([DateTime]::UtcNow-$started).TotalSeconds; isolatedProject=$isolatedProject; startedUtc=$started.ToString('o') }
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'process-result.json') -Encoding utf8
$result | ConvertTo-Json -Compress | Write-Output
if ($process.ExitCode -ne 0) { throw "Room Unity $Action failed. See $destination" }
if (Select-String -LiteralPath (Join-Path $destination 'unity.log') -Pattern 'error CS[0-9]{4}' -Quiet) {
    throw "Unity reported a C# compilation error; this run is not a clean validation even if an entry point exited zero. See $destination"
}
