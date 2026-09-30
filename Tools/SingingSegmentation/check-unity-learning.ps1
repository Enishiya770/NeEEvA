param([string]$Output = '', [string]$UnityData = 'D:/工程应用/Unity/Unity Hub/2022.3.22f1/Editor/Data')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$Output) { $Output = Join-Path $projectRoot ('work/personal-singing-learning-20260929/compile-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')) }
$response = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Library/Bee/artifacts') -Filter 'Assembly-CSharp.rsp' -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (!$response) { throw 'Open this project in Unity once to generate its real assembly references.' }
if (!(Test-Path -LiteralPath (Join-Path $UnityData 'DotNetSdkRoslyn/csc.dll'))) { throw 'Supply the installed Unity Editor Data directory.' }
[void](New-Item -ItemType Directory -Path $Output -Force)
$lines = @([IO.File]::ReadAllLines($response.FullName) | Where-Object { $_ -notmatch '^-(out|refout):' })
$existingSources = @{}
foreach ($line in $lines) { $existingSources[$line.Trim('"').Replace('\','/')] = $true }
$newSources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Assets/AIChatTookit/Scripts') -Recurse -File -Filter '*.cs' | Where-Object { $_.Name -match 'PersonalSingingLearning|CompanionPresentation[.](SingingLearning|Learning)' }
foreach ($sourceFile in $newSources) {
    $relative = [IO.Path]::GetRelativePath($projectRoot,$sourceFile.FullName).Replace('\','/')
    if (!$existingSources.ContainsKey($relative)) { $lines += '"' + $relative + '"' }
}
$lines += '-out:"' + (Join-Path $Output 'Assembly-CSharp.dll') + '"'
$lines += '-refout:"' + (Join-Path $Output 'Assembly-CSharp.ref.dll') + '"'
[IO.File]::WriteAllLines((Join-Path $Output 'compile.rsp'), $lines)
Push-Location $projectRoot
try {
    & (Join-Path $UnityData 'NetCoreRuntime/dotnet.exe') (Join-Path $UnityData 'DotNetSdkRoslyn/csc.dll') ('@' + (Join-Path $Output 'compile.rsp')) *> (Join-Path $Output 'compile.log')
    $result = $LASTEXITCODE
} finally { Pop-Location }
Get-Content -LiteralPath (Join-Path $Output 'compile.log')
Write-Output ('Compiler output: ' + $Output)
exit $result
