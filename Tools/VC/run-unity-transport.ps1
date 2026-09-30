[CmdletBinding()]
param(
    [string]$VcUrl = 'http://192.168.114.163:9882/convert',
    [string]$ReferencePath = '/home/spark_root_001/neeeva-voice/vc/release/refs/41041.wav',
    [string]$InputWav = 'work/spark-voice-migration-20260926/asr-validation-ja.wav',
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$ReportName = 'spark',
    [string]$UnityPath = 'D:/工程应用/Unity/Unity Hub/2022.3.22f1/Editor/Unity.exe'
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) { throw "Unity not found: $UnityPath" }
$taskEndpoint=$null
if (-not [Uri]::TryCreate($VcUrl,[UriKind]::Absolute,[ref]$taskEndpoint) -or
    $taskEndpoint.Scheme -notin @('http','https') -or $taskEndpoint.UserInfo -or $taskEndpoint.Query -or
    $taskEndpoint.Fragment -or $taskEndpoint.AbsolutePath -ne '/convert') { throw 'VcUrl must be an HTTP(S) /convert endpoint.' }
if ([string]::IsNullOrWhiteSpace($ReferencePath)) { throw 'ReferencePath is required on the selected server.' }
$taskInput=if([IO.Path]::IsPathRooted($InputWav)){[IO.Path]::GetFullPath($InputWav)}else{[IO.Path]::GetFullPath((Join-Path $taskRoot $InputWav))}
if (-not (Test-Path -LiteralPath $taskInput -PathType Leaf)) { throw "Input WAV not found: $taskInput" }
$taskEditors=@(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object {
    $_.CommandLine -and $_.CommandLine.Replace('\','/').IndexOf($taskRoot.Replace('\','/'),[StringComparison]::OrdinalIgnoreCase) -ge 0
})
if ($taskEditors.Count) { throw 'Close this project in Unity before its isolated VC transport trial.' }
New-Item -ItemType Directory -Path (Join-Path $taskRoot 'Logs/vc') -Force | Out-Null
$taskInfo=New-Object Diagnostics.ProcessStartInfo
$taskInfo.FileName=$UnityPath
# Graphics/audio initialization is retained, though the unsaved scene has no camera.
$taskInfo.Arguments='-batchmode -force-d3d11 -projectPath "'+$taskRoot+'" -executeMethod VcSparkTransportRegression.RunBatch -logFile Logs/vc/unity-transport-'+$ReportName+'.log'
$taskInfo.WorkingDirectory=$taskRoot; $taskInfo.UseShellExecute=$false
$taskInfo.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden; $taskInfo.CreateNoWindow=$true
$taskInfo.EnvironmentVariables['UNITY_PROXYSERVER']=''
$taskInfo.EnvironmentVariables['NEEEVA_VC_TRIAL_URL']=$VcUrl
$taskInfo.EnvironmentVariables['NEEEVA_VC_TRIAL_REFERENCE']=$ReferencePath
$taskInfo.EnvironmentVariables['NEEEVA_VC_TRIAL_WAV']=$taskInput
$taskInfo.EnvironmentVariables['NEEEVA_VC_TRIAL_REPORT']=$ReportName
$taskProcess=[Diagnostics.Process]::Start($taskInfo)
[pscustomobject]@{ProcessId=$taskProcess.Id;Endpoint=$VcUrl;InputWav=$taskInput;
    Log=Join-Path $taskRoot ('Logs/vc/unity-transport-'+$ReportName+'.log');
    Report=Join-Path $taskRoot ('Logs/vc/unity-transport-'+$ReportName+'.json');
    Scope='Real Unity WAV download/decode/muted playback and transport cancellation; not autonomous singing or singing-quality evaluation.'}
