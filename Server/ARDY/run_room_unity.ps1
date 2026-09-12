[CmdletBinding()]
param(
    [ValidateSet('audit','navigation','integration','actual-room','locomotion','room-dialogue-smoke','room-approach','room-dialogue')][string]$Action = 'audit',
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$Avatar = '',
    [string]$RoomScene = '',
    [string]$RoomStart = '',
    [string]$RoomTarget = '',
    [string]$RoomUser = '',
    [string]$EnvironmentName = '',
    [string]$ExcludedVisualRoots = '',
    [switch]$DisabledVrm,
    [float]$InitialYaw = 37,
    [float]$AvatarScale = .9,
    [switch]$StartIsRoot
)
$ErrorActionPreference = 'Stop'
$isolatedProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime/unity-naturalness-validation'))
if (-not (Test-Path -LiteralPath (Join-Path $isolatedProject 'ProjectSettings/ProjectVersion.txt'))) { throw 'Prepare the isolated validation project first.' }
if (Test-Path -LiteralPath (Join-Path $isolatedProject 'Temp/UnityLockfile')) { throw 'The isolated project is already open.' }
$destination = [IO.Path]::GetFullPath($Output)
if ((Test-Path -LiteralPath $destination) -and @(Get-ChildItem -LiteralPath $destination -Force).Count) { throw 'Use an empty evidence directory.' }
$methods = @{ audit='ArdyRoomTerrainAudit.RunBatch'; navigation='ArdyRoomNavigationRegression.RunBatch'; integration='ArdyRoomIntegrationRegression.RunBatch'; 'actual-room'='ArdyRoomActualRoomRegression.RunBatch'; locomotion='ArdyLocomotionRegression.RunBatch'; 'room-dialogue-smoke'='ArdyRoomDialogueSmokeRegression.RunBatch'; 'room-approach'='ArdyRoomApproachRegression.RunBatch'; 'room-dialogue'='ArdyRoomDialogueIntegrationRegression.RunBatch' }
$arguments = @('-batchmode','-nographics','-projectPath',$isolatedProject,'-executeMethod',$methods[$Action],'-logFile',(Join-Path $destination 'unity.log'),'-ardyRoomNavigationOutput',$destination,'-ardyRoomOutput',$destination,'-ardyLocomotionOutput',$destination)
$arguments += @('-ardyRoomApproachOutput',$destination,'-ardyRoomDialogueOutput',$destination)
if ($RoomUser) { $arguments += @('-ardyRoomUser',$RoomUser) }
if ($Avatar) { $arguments += @('-ardyRoomAvatar',$Avatar) }
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
