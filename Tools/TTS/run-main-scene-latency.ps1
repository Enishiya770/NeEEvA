param(
    [ValidateSet('default', 'baseline', 'fps60', 'network-probe')][string]$Mode = 'default',
    [switch]$NetworkProbe,
    [string]$TtsUrl = '',
    [string]$ReferenceAudioPath = '',
    [switch]$Extended,
    [ValidatePattern('^[a-zA-Z0-9_-]*$')][string]$ReportName = '',
    [string]$UnityPath = 'D:/工程应用/Unity/Unity Hub/2022.3.22f1/Editor/Unity.exe'
)
$ErrorActionPreference = 'Stop'
$taskProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not (Test-Path -LiteralPath $UnityPath)) { throw "Unity executable not found: $UnityPath" }
$taskEditors = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object {
    $_.CommandLine -and $_.CommandLine.Replace('\', '/').IndexOf($taskProjectRoot.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase) -ge 0
})
if ($taskEditors.Count -gt 0) { throw 'Save and close this project in Unity before running the separate latency trial.' }
New-Item -ItemType Directory -Path (Join-Path $taskProjectRoot 'Logs/tts-latency') -Force | Out-Null
$taskProcessInfo = New-Object System.Diagnostics.ProcessStartInfo
$taskUseNetworkProbe = $NetworkProbe.IsPresent -or $Mode -eq 'network-probe'
$taskFrameMode = if ($Mode -eq 'network-probe' -or $Mode -eq 'baseline') { 'default' } else { $Mode }
$taskVariant = if ($taskUseNetworkProbe) {
    if ($taskFrameMode -eq 'default') { 'network-probe' } else { $taskFrameMode + '-network-probe' }
} else { $taskFrameMode }
if ($ReportName) { $taskVariant = $ReportName }
$taskProcessInfo.FileName = $UnityPath
$taskProcessInfo.Arguments = '-batchmode -force-d3d11 -projectPath "' + $taskProjectRoot + '" -executeMethod TtsMainSceneLatencyRegression.RunBatch -logFile Logs/tts-latency/main-scene-' + $taskVariant + '.log'
$taskProcessInfo.WorkingDirectory = $taskProjectRoot
$taskProcessInfo.UseShellExecute = $false
$taskProcessInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$taskProcessInfo.CreateNoWindow = $true
$taskProcessInfo.EnvironmentVariables['UNITY_PROXYSERVER'] = ''
$taskProcessInfo.EnvironmentVariables['NEEEVA_TTS_MAIN_SCENE_MODE'] = $taskFrameMode
$taskProcessInfo.EnvironmentVariables['NEEEVA_TTS_NETWORK_PROBE'] = if ($taskUseNetworkProbe) { '1' } else { '0' }
$taskProcessInfo.EnvironmentVariables['NEEEVA_TTS_TRIAL_URL'] = $TtsUrl
$taskProcessInfo.EnvironmentVariables['NEEEVA_TTS_TRIAL_REFERENCE'] = $ReferenceAudioPath
$taskProcessInfo.EnvironmentVariables['NEEEVA_TTS_TRIAL_EXTENDED'] = if ($Extended) { '1' } else { '0' }
$taskProcessInfo.EnvironmentVariables['NEEEVA_TTS_TRIAL_REPORT'] = $taskVariant
$taskProcess = [Diagnostics.Process]::Start($taskProcessInfo)
[pscustomobject]@{ ProcessId = $taskProcess.Id; Mode = $taskFrameMode; NetworkProbe = $taskUseNetworkProbe;
    Log = Join-Path $taskProjectRoot ('Logs/tts-latency/main-scene-' + $taskVariant + '.log');
    Report = Join-Path $taskProjectRoot ('Logs/tts/main-scene-latency-' + $taskVariant + '.json') }
