param(
    [string]$UnityPath = 'D:/工程应用/Unity/Unity Hub/2022.3.22f1/Editor/Unity.exe'
)

$ErrorActionPreference = 'Stop'
$taskProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not (Test-Path -LiteralPath $UnityPath)) { throw "Unity executable not found: $UnityPath" }
$taskEditors = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object {
    $_.CommandLine -and $_.CommandLine.Replace('\', '/').IndexOf($taskProjectRoot.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase) -ge 0
})
if ($taskEditors.Count -gt 0) { throw 'This project is already open in Unity. Save and close that Editor before running the trial; no process was stopped.' }
New-Item -ItemType Directory -Path (Join-Path $taskProjectRoot 'Logs/fast-loop') -Force | Out-Null
$taskProcessInfo = New-Object System.Diagnostics.ProcessStartInfo
$taskProcessInfo.FileName = $UnityPath
$taskProcessInfo.Arguments = '-batchmode -force-d3d11 -projectPath "' + $taskProjectRoot + '" -executeMethod MainSceneFastLoopLiveRegression.RunBatch -logFile Logs/main-scene-fast-loop-live.log'
$taskProcessInfo.WorkingDirectory = $taskProjectRoot
$taskProcessInfo.UseShellExecute = $false
$taskProcessInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$taskProcessInfo.CreateNoWindow = $true
# Unity 2022 Windows uses the system proxy unless this key EXISTS and is empty.
# A PowerShell 5.1 $env: assignment of '' deletes it, so set the child block directly.
$taskProcessInfo.EnvironmentVariables['UNITY_PROXYSERVER'] = ''
$taskProcess = [Diagnostics.Process]::Start($taskProcessInfo)
[pscustomobject]@{
    ProcessId = $taskProcess.Id
    Log = Join-Path $taskProjectRoot 'Logs/main-scene-fast-loop-live.log'
    Report = Join-Path $taskProjectRoot 'Logs/fast-loop/main-scene-live.json'
    Scope = 'Real main scene and services; muted AudioSource progress; no microphone or saved test state.'
}
