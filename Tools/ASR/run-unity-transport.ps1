[CmdletBinding()]
param(
    [string]$AsrUrl = 'http://192.168.114.163:9881',
    [string]$InputWav = 'work/spark-voice-migration-20260926/asr-validation-ja.wav',
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$ReportName = 'spark',
    [string]$UnityPath = 'D:/工程应用/Unity/Unity Hub/2022.3.22f1/Editor/Unity.exe'
)
$ErrorActionPreference = 'Stop'
$taskProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) { throw "Unity executable not found: $UnityPath" }
$taskEndpoint = $null
if (-not [Uri]::TryCreate($AsrUrl,[UriKind]::Absolute,[ref]$taskEndpoint) -or
    $taskEndpoint.Scheme -notin @('http','https') -or $taskEndpoint.UserInfo -or
    $taskEndpoint.Query -or $taskEndpoint.Fragment -or $taskEndpoint.AbsolutePath -ne '/') {
    throw 'AsrUrl must be an HTTP(S) origin without path, credentials, query or fragment.'
}
$taskInput = if ([IO.Path]::IsPathRooted($InputWav)) { [IO.Path]::GetFullPath($InputWav) } else {
    [IO.Path]::GetFullPath((Join-Path $taskProjectRoot $InputWav))
}
if (-not (Test-Path -LiteralPath $taskInput -PathType Leaf)) { throw "Synthetic input WAV not found: $taskInput" }
$taskEditors = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object {
    $_.CommandLine -and $_.CommandLine.Replace('\','/').IndexOf($taskProjectRoot.Replace('\','/'),[StringComparison]::OrdinalIgnoreCase) -ge 0
})
if ($taskEditors.Count) { throw 'Save and close this project in Unity before the isolated ASR network trial.' }
New-Item -ItemType Directory -Path (Join-Path $taskProjectRoot 'Logs/asr') -Force | Out-Null
$taskProcessInfo = New-Object Diagnostics.ProcessStartInfo
$taskProcessInfo.FileName = $UnityPath
$taskProcessInfo.Arguments = '-batchmode -nographics -projectPath "' + $taskProjectRoot + '" -executeMethod AsrSparkTransportRegression.RunBatch -logFile Logs/asr/unity-transport-' + $ReportName + '.log'
$taskProcessInfo.WorkingDirectory = $taskProjectRoot
$taskProcessInfo.UseShellExecute = $false
$taskProcessInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$taskProcessInfo.CreateNoWindow = $true
$taskProcessInfo.EnvironmentVariables['UNITY_PROXYSERVER'] = ''
$taskProcessInfo.EnvironmentVariables['NEEEVA_ASR_TRIAL_URL'] = $AsrUrl.TrimEnd('/')
$taskProcessInfo.EnvironmentVariables['NEEEVA_ASR_TRIAL_WAV'] = $taskInput
$taskProcessInfo.EnvironmentVariables['NEEEVA_ASR_TRIAL_REPORT'] = $ReportName
$taskProcess = [Diagnostics.Process]::Start($taskProcessInfo)
[pscustomobject]@{ ProcessId=$taskProcess.Id; Endpoint=$AsrUrl; InputWav=$taskInput;
    Log=Join-Path $taskProjectRoot ('Logs/asr/unity-transport-'+$ReportName+'.log');
    Report=Join-Path $taskProjectRoot ('Logs/asr/unity-transport-'+$ReportName+'.json');
    Scope='Real Unity HTTP/WS with synthetic paced PCM; no microphone, scene save, memory or speaker enrollment.' }
