[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Output,
    [ValidateSet('controlled','native','objects','matching','result','half-turn','first-audio','first-reply','memory','search','search-native','search-live','unified-device','unified-device-native')][string]$Focus='controlled'
)
$ErrorActionPreference='Stop'
$taskSourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskValidationRoot=[IO.Path]::GetFullPath((Join-Path $taskSourceRoot 'Server/ARDY/runtime/unity-naturalness-validation'))
$taskOutput=[IO.Path]::GetFullPath($Output)
if (-not (Test-Path -LiteralPath (Join-Path $taskValidationRoot 'ProjectSettings/ProjectVersion.txt'))) { throw 'Existing isolated Unity project is required.' }
if ((Test-Path -LiteralPath $taskOutput) -and @(Get-ChildItem -LiteralPath $taskOutput -Force).Count) { throw 'Use a new empty evidence directory.' }
[void](New-Item -ItemType Directory -Path $taskOutput -Force)
$taskLockPath=[IO.Path]::GetFullPath((Join-Path $taskValidationRoot 'Temp/UnityLockfile'))
if (Test-Path -LiteralPath $taskLockPath) {
    $taskUnityOwners=@(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object {
        -not $_.CommandLine -or $_.CommandLine.Replace('/','\').IndexOf($taskValidationRoot,[StringComparison]::OrdinalIgnoreCase) -ge 0
    })
    if ($taskUnityOwners.Count) { throw 'Isolated project is in use.' }
    # Failed batch compilation may leave a stale lock. Verify there is no owner,
    # then preserve only this isolated project's lock in the evidence directory.
    if (-not $taskLockPath.StartsWith($taskValidationRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Lock path escaped the isolated project.' }
    Move-Item -LiteralPath $taskLockPath -Destination (Join-Path $taskOutput 'stale-isolated-UnityLockfile')
}
# This retired planner must not survive from an older isolated-project snapshot.
# Preserve the exact two named files as evidence; never recursively delete a tree.
$taskRetiredPaths=@('Assets/AIChatTookit/Scripts/Chat/ChatSample.SpatialSearchPlanning.cs','Assets/AIChatTookit/Scripts/Chat/ChatSample.SpatialSearchPlanning.cs.meta')
$taskRetired=@(foreach($taskRelative in $taskRetiredPaths) {
    $taskSource=[IO.Path]::GetFullPath((Join-Path $taskSourceRoot $taskRelative))
    if (Test-Path -LiteralPath $taskSource) { throw "Retired planner unexpectedly exists in source: $taskRelative" }
    $taskOld=[IO.Path]::GetFullPath((Join-Path $taskValidationRoot $taskRelative))
    $taskBackup=[IO.Path]::GetFullPath((Join-Path (Join-Path $taskOutput 'retired-source') $taskRelative))
    if (-not $taskOld.StartsWith($taskValidationRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
        -not $taskBackup.StartsWith($taskOutput+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Retired source path escaped its allowed root.' }
    $taskOldHash=$null
    if (Test-Path -LiteralPath $taskOld) {
        $taskOldHash=(Get-FileHash -LiteralPath $taskOld -Algorithm SHA256).Hash
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $taskBackup) -Force)
        Move-Item -LiteralPath $taskOld -Destination $taskBackup
    }
    @{path=$taskRelative;retired=$true;previousIsolatedSha256=$taskOldHash;preserved=($null -ne $taskOldHash)}
})
$taskCodePaths=@(& rg --files (Join-Path $taskSourceRoot 'Assets') -g '*.cs' -g '*.asmdef' -g '*.asmref' -g '*.rsp')
$taskCodePaths += @(& rg --files (Join-Path $taskSourceRoot 'Assets/Editor/Fixtures') -g '*.json')
$taskManifest=@(foreach($taskAbsoluteCode in $taskCodePaths) {
    foreach($taskAbsolute in @($taskAbsoluteCode,($taskAbsoluteCode+'.meta'))) {
        if (-not(Test-Path -LiteralPath $taskAbsolute)) { continue }
        $taskRelative=$taskAbsolute.Substring($taskSourceRoot.Length+1)
        $taskDestination=[IO.Path]::GetFullPath((Join-Path $taskValidationRoot $taskRelative))
        if (-not $taskDestination.StartsWith($taskValidationRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Snapshot path escaped the isolated project.' }
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force)
        Copy-Item -LiteralPath $taskAbsolute -Destination $taskDestination -Force
        @{path=$taskRelative;sha256=(Get-FileHash -LiteralPath $taskAbsolute -Algorithm SHA256).Hash}
    }
})
@{source=$taskSourceRoot;project=$taskValidationRoot;createdUtc=[DateTime]::UtcNow.ToString('o');focus=$Focus;files=$taskManifest;retired=$taskRetired} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOutput 'source-manifest.json') -Encoding utf8
$taskRunArgs=@{Output=(Join-Path $taskOutput 'unity');Action='integration'}
switch($Focus) {
    controlled {$taskRunArgs.SpatialGoalRunner=$true}
    native {$taskRunArgs.SpatialGoalRunnerNative=$true}
    objects {$taskRunArgs.SceneObjects=$true}
    matching {$taskRunArgs.ObjectMatching=$true}
    result {$taskRunArgs.SpatialResult=$true}
    'half-turn' {$taskRunArgs.HalfTurnNative=$true}
    'first-audio' {$taskRunArgs.FirstAudioRegression=$true}
    'first-reply' {$taskRunArgs.FirstReplyRegression=$true}
    memory {$taskRunArgs.SpatialMemory=$true}
    search {$taskRunArgs.SceneSearch=$true}
    'search-native' {$taskRunArgs.SceneSearchNative=$true}
    'search-live' {$taskRunArgs.SceneSearchLive=$true}
    'unified-device' {$taskRunArgs.UnifiedDevice=$true}
    'unified-device-native' {$taskRunArgs.UnifiedDeviceNative=$true}
}
& (Join-Path $PSScriptRoot 'validate_scene_perception_unity.ps1') @taskRunArgs
$taskChanges=@(foreach($taskItem in $taskManifest) {
    $taskCurrentHash=(Get-FileHash -LiteralPath (Join-Path $taskSourceRoot $taskItem.path) -Algorithm SHA256).Hash
    $taskTestedHash=(Get-FileHash -LiteralPath (Join-Path $taskValidationRoot $taskItem.path) -Algorithm SHA256).Hash
    if ($taskCurrentHash -ne $taskItem.sha256 -or $taskTestedHash -ne $taskItem.sha256) { $taskItem.path }
})
$taskRetiredPresent=@(foreach($taskRelative in $taskRetiredPaths) {
    if ((Test-Path -LiteralPath (Join-Path $taskSourceRoot $taskRelative)) -or (Test-Path -LiteralPath (Join-Path $taskValidationRoot $taskRelative))) { $taskRelative }
})
$taskChanges += $taskRetiredPresent
@{passed=$taskChanges.Count -eq 0;fileCount=$taskManifest.Count;changes=$taskChanges;retiredAbsent=($taskRetiredPresent.Count -eq 0);checkedUtc=[DateTime]::UtcNow.ToString('o')} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskOutput 'hash-verification.json') -Encoding utf8
if ($taskChanges.Count) { throw 'Source changed during verification; retain this evidence and run a fresh snapshot.' }
