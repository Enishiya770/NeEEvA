[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('local','spark')][string]$Profile,
    [string]$SparkUrl = 'http://192.168.114.163:9881',
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskScene = Join-Path $taskRoot 'Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity'
$taskRoutesPath = Join-Path $taskRoot 'Server/RuntimeLogs/voice_routes.json'

function Assert-AsrProjectClosed {
    $taskEditors = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object {
        -not $_.CommandLine -or
        $_.CommandLine.Replace('\','/').IndexOf($taskRoot.Replace('\','/'), [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $_.CommandLine -notmatch '(?i)(?:^|\s)-projectPath(?:\s|$)'
    })
    if ($taskEditors.Count) { throw 'Close this project in Unity before changing its saved ASR route. An Editor with an unknown project is also treated as open.' }
}

if (-not $CheckOnly) { Assert-AsrProjectClosed }
$taskUrl = 'http://127.0.0.1:9881'
if ($Profile -eq 'spark') {
    $taskEndpoint = $null
    if (-not [Uri]::TryCreate($SparkUrl,[UriKind]::Absolute,[ref]$taskEndpoint) -or
        $taskEndpoint.Scheme -ne 'http' -or $taskEndpoint.UserInfo -or $taskEndpoint.Query -or
        $taskEndpoint.Fragment -or $taskEndpoint.AbsolutePath -ne '/' -or $taskEndpoint.Port -ne 9881) {
        throw 'SparkUrl must be an HTTP origin on port 9881, without a path, credentials or query; the ASR manager owns this fixed port.'
    }
    $taskUrl = $taskEndpoint.GetLeftPart([UriPartial]::Authority)
}
$taskHealthUrl = $taskUrl + '/health'
$taskStreamUrl = $taskUrl.Replace('http://','ws://') + '/stream/asr'
if ($Profile -eq 'spark') {
    $taskRequest = [Net.HttpWebRequest]::Create($taskHealthUrl)
    $taskRequest.Proxy = $null
    $taskRequest.Timeout = 8000
    $taskRequest.ReadWriteTimeout = 8000
    $taskResponse = $taskRequest.GetResponse()
    try {
        $taskReader = New-Object IO.StreamReader($taskResponse.GetResponseStream())
        try { $taskReady = $taskReader.ReadToEnd() | ConvertFrom-Json } finally { $taskReader.Dispose() }
        if ($taskReady -isnot [pscustomobject]) { throw 'Remote ASR health must be an object.' }
        foreach ($taskKey in @('ok','asr_ok','vad_ok','speaker_ok','cuda_available','require_cuda',
                              'streaming_preview','final_asr_worker','singing_analysis','song_search')) {
            if ($taskReady.$taskKey -isnot [bool] -or $taskReady.$taskKey -ne $true) {
                throw "Remote ASR health contract failed: $taskKey."
            }
        }
        foreach ($taskSchema in @(@('stream_activity_schema',2),@('analysis_dispatch_schema',2),
                                  @('turn_segments_schema',1),@('singing_score_schema',1))) {
            if ($taskReady.($taskSchema[0]) -ne $taskSchema[1]) {
                throw "Remote ASR health schema mismatch: $($taskSchema[0])."
            }
        }
        if ($taskReady.asr_device -notmatch '^cuda(?::[0-9]+)?$' -or
            $taskReady.final_speech_window_policy -ne 'speech-context-v1' -or
            $taskReady.external_audio_upload -isnot [bool] -or $taskReady.external_audio_upload -ne $false) {
            throw 'Remote ASR device, final-window policy or audio-upload contract does not match the prepared runtime.'
        }
    } finally { $taskResponse.Dispose() }
}

# Read the scene in memory but identify only this known SenseVoice component.
# Never print a full block or the full scene: other components contain credentials.
$taskOriginalSceneBytes = [IO.File]::ReadAllBytes($taskScene)
$taskOriginalScene = [IO.File]::ReadAllText($taskScene)
$taskHasBom = $taskOriginalSceneBytes.Length -ge 3 -and $taskOriginalSceneBytes[0] -eq 239 -and
              $taskOriginalSceneBytes[1] -eq 187 -and $taskOriginalSceneBytes[2] -eq 191
$taskSceneEncoding = New-Object Text.UTF8Encoding($taskHasBom)
$taskBlockRegex = New-Object Text.RegularExpressions.Regex('(?ms)^--- !u!114 &501756907\r?\n.*?(?=^--- |\z)')
$taskBlocks = $taskBlockRegex.Matches($taskOriginalScene)
if ($taskBlocks.Count -ne 1 -or $taskBlocks[0].Value -notmatch 'guid: e74eb068227898240b54f1ba047b8f2c') {
    throw 'Expected main-scene SenseVoice component was not found exactly once.'
}
if ([regex]::Matches($taskOriginalScene,'(?m)^\s+m_SpeechToText: \{fileID: 501756907\}\s*$').Count -ne 1 -or
    [regex]::Matches($taskOriginalScene,'(?m)^\s+m_NeuralVadClient: \{fileID: 501756907\}\s*$').Count -ne 1) {
    throw 'Main chat and realtime VAD must reference the same expected SenseVoice component.'
}
$taskBlock = $taskBlocks[0].Value
# Empty WS override deliberately restores BuildStreamingPreviewURL's derivation
# from m_ServerSetting, so final HTTP and partial WS cannot retain different hosts.
foreach ($taskField in @(@('m_ServerSetting',$taskUrl),@('m_StreamingPreviewURL',''))) {
    $taskFieldRegex = New-Object Text.RegularExpressions.Regex(('(?m)^  '+$taskField[0]+':[^\r\n]*'))
    if ($taskFieldRegex.Matches($taskBlock).Count -ne 1) { throw 'Expected SenseVoice property missing or duplicated.' }
    $taskReplacement = '  '+$taskField[0]+': '+(ConvertTo-Json -InputObject ([string]$taskField[1]) -Compress)
    $taskBlock = $taskFieldRegex.Replace($taskBlock,[Text.RegularExpressions.MatchEvaluator]{param($m) $taskReplacement})
}
$taskUpdatedScene = $taskBlockRegex.Replace($taskOriginalScene,[Text.RegularExpressions.MatchEvaluator]{param($m) $taskBlock})
$taskOriginalRoutesBytes = $null
$taskOriginalRoutesExists = Test-Path -LiteralPath $taskRoutesPath -PathType Leaf
if ($taskOriginalRoutesExists) { $taskOriginalRoutesBytes = [IO.File]::ReadAllBytes($taskRoutesPath) }
$taskRoutes = if ($taskOriginalRoutesExists) { [IO.File]::ReadAllText($taskRoutesPath) | ConvertFrom-Json } else { [pscustomobject]@{} }
if ($taskRoutes -isnot [pscustomobject]) { throw 'voice_routes.json must contain an object.' }
$taskRoute = [pscustomobject]@{
    mode = if ($Profile -eq 'spark') { 'remote' } else { 'local' }
    url = $taskUrl
    healthUrl = $taskHealthUrl
    streamUrl = $taskStreamUrl
}
$taskRoutes | Add-Member -MemberType NoteProperty -Name asr -Value $taskRoute -Force
$taskUpdatedRoutes = $taskRoutes | ConvertTo-Json -Depth 64
if ($CheckOnly) {
    Write-Output "Validated ASR route: $Profile -> $taskUrl; WS -> $taskStreamUrl. No files changed."
    return
}

# Recheck after health/network work, before touching scene or routing files.
Assert-AsrProjectClosed
if ([IO.File]::ReadAllText($taskScene) -cne $taskOriginalScene) { throw 'Scene changed during route preparation; retry from its new state.' }
$taskRoutesNow = $null
$taskRoutesNowExists = Test-Path -LiteralPath $taskRoutesPath -PathType Leaf
if ($taskRoutesNowExists) { $taskRoutesNow = [IO.File]::ReadAllBytes($taskRoutesPath) }
if ($taskOriginalRoutesExists -ne $taskRoutesNowExists -or
    ($taskRoutesNowExists -and [Convert]::ToBase64String($taskRoutesNow) -cne [Convert]::ToBase64String($taskOriginalRoutesBytes))) {
    throw 'Voice routes changed during preparation; retry rather than replacing another service route.'
}
$taskBackup = Join-Path $taskRoot ('work/spark-voice-migration-20260926/asr-routes-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $taskBackup -Force | Out-Null
[IO.File]::WriteAllBytes((Join-Path $taskBackup 'scene.before.unity'),$taskOriginalSceneBytes)
if ($null -ne $taskOriginalRoutesBytes) {
    [IO.File]::WriteAllBytes((Join-Path $taskBackup 'voice_routes.before.json'),$taskOriginalRoutesBytes)
}
try {
    [IO.File]::WriteAllText($taskScene,$taskUpdatedScene,$taskSceneEncoding)
    [IO.File]::WriteAllText($taskRoutesPath,$taskUpdatedRoutes,(New-Object Text.UTF8Encoding($false)))
} catch {
    [IO.File]::WriteAllBytes($taskScene,$taskOriginalSceneBytes)
    if ($null -ne $taskOriginalRoutesBytes) { [IO.File]::WriteAllBytes($taskRoutesPath,$taskOriginalRoutesBytes) }
    elseif (Test-Path -LiteralPath $taskRoutesPath -PathType Leaf) { Remove-Item -LiteralPath $taskRoutesPath -Force }
    throw
}
Write-Output "ASR route applied: $Profile -> $taskUrl; WS derives from the same origin."
Write-Output "Route backup: $taskBackup"
Write-Output 'TTS, voice conversion, SVS and their reference paths were preserved. Use start_voice.cmd for the selected services.'
