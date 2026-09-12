[CmdletBinding()]
param(
    [ValidateSet('regression','analyze','playmode')][string]$Action = 'regression',
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$Clip = '',
    [switch]$Render
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$isolatedProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime/unity-naturalness-validation'))
$expectedPrefix = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'runtime')) + [IO.Path]::DirectorySeparatorChar
if (-not $isolatedProject.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected isolated Unity project' }
if (-not (Test-Path -LiteralPath (Join-Path $isolatedProject 'ProjectSettings/ProjectVersion.txt'))) { throw 'Prepare the isolated validation project first.' }
$destination = [IO.Path]::GetFullPath($Output)
if ((Test-Path -LiteralPath $destination) -and @(Get-ChildItem -LiteralPath $destination -Force).Count) { throw 'Use an empty output directory; prior evidence is preserved.' }
$methods = @{ regression='ArdyLocomotionRegression.RunBatch'; analyze='ArdyLocomotionRegression.AnalyzeBatch'; playmode='ArdyLocomotionPlayModeRegression.RunBatch' }
$arguments = @('-batchmode','-projectPath', $isolatedProject, '-executeMethod', $methods[$Action], '-logFile', (Join-Path $destination 'unity.log'), '-ardyLocomotionOutput', $destination)
if (-not $Render) { $arguments += '-nographics' }
if ($Action -ne 'regression') {
    if (-not $Clip -or -not (Test-Path -LiteralPath $Clip)) { throw 'A generated locomotion JSON is required.' }
    $arguments += @('-ardyLocomotionClip', [IO.Path]::GetFullPath($Clip))
}
if ($Render) { $arguments += '-ardyLocomotionRender' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
# Quote every path passed through Start-Process; do not execute a built shell string.
$quoted = $arguments | ForEach-Object { if ($_ -match '"') { throw 'Unexpected quote in Unity argument' }; '"' + $_ + '"' }
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath 'E:/Unity_Data/2022.3.22f1/Editor/Unity.exe' -ArgumentList $quoted -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $destination 'stdout.txt') -RedirectStandardError (Join-Path $destination 'stderr.txt')
while (-not $process.WaitForExit(1000)) {
    if (([DateTime]::UtcNow - $started).TotalSeconds -gt 420) {
        $process.Kill()
        throw 'The isolated Unity process exceeded 420 seconds; only this launched process was stopped.'
    }
}
$result = [ordered]@{ action=$Action; exitCode=$process.ExitCode; seconds=([DateTime]::UtcNow-$started).TotalSeconds; isolatedProject=$isolatedProject; startedUtc=$started.ToString('o') }
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'process-result.json') -Encoding utf8
$result | ConvertTo-Json -Compress | Write-Output
if ($process.ExitCode -ne 0) { throw "Isolated locomotion Unity $Action failed. See $destination" }
