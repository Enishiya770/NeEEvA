param([string]$ProjectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')), [string]$SourceRoot='', [string]$Output='')
$ErrorActionPreference='Stop'
if (!$SourceRoot) { $SourceRoot=$ProjectRoot }
if (!$Output) { $Output=Join-Path ([IO.Path]::GetTempPath()) ('NeEEvA-DecisionClosure-'+[Guid]::NewGuid().ToString('N')) }
[void](New-Item -ItemType Directory -Force $Output)
$paths=@((Join-Path $PSScriptRoot 'Regression.cs'))
$paths+=Join-Path $ProjectRoot 'Assets/Editor/PttRoleDecisionRegression.cs'
foreach($name in @('RoleDecision.cs','RecordingSingingDecision.cs','RecordingSingingSpeech.cs','RecordingSingingConsistency.cs','RecordingArchiveSnapshot.cs','RecordingSingingPitch.cs','RecordingSingingGoalTransition.cs','SpeechText.cs','RoleOutputChannels.cs','RoleOutputPublicBoundary.cs')) {
    $path=Join-Path $SourceRoot ('Assets/AIChatTookit/Scripts/Chat/'+$name)
    if(!(Test-Path -LiteralPath $path)) { $path=Join-Path $ProjectRoot ('Assets/AIChatTookit/Scripts/Chat/'+$name) }
    $paths+=$path
}
$reference=Join-Path $ProjectRoot 'Assets/AIChatTookit/Plugin/Newtonsoft.Json.dll'
$paths+=Join-Path $SourceRoot 'Assets/AIChatTookit/Scripts/LLM/ProviderTimingInterval.cs'
$ref=Get-ChildItem 'C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref' -Directory -Filter '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$sdk=Get-ChildItem 'C:/Program Files/dotnet/sdk' -Directory -Filter '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$argsList=@('-target:exe',('-out:"'+$Output+'/Regression.dll"'))
$argsList+=Get-ChildItem (Join-Path $ref.FullName 'ref/net10.0') -Filter '*.dll' | ForEach-Object { '-r:"'+$_.FullName+'"' }
$argsList+='-r:"'+$reference+'"'
$argsList+=$paths | ForEach-Object { '"'+$_+'"' }
[IO.File]::WriteAllLines((Join-Path $Output 'test.rsp'),$argsList)
& dotnet (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') ('@'+$Output+'/test.rsp') *> (Join-Path $Output 'compile.log')
if($LASTEXITCODE -ne 0) { Get-Content (Join-Path $Output 'compile.log'); exit $LASTEXITCODE }
Copy-Item -LiteralPath $reference -Destination $Output -Force
[IO.File]::WriteAllText((Join-Path $Output 'Regression.runtimeconfig.json'),'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}')
& dotnet (Join-Path $Output 'Regression.dll') *> (Join-Path $Output 'results.log')
$result=$LASTEXITCODE
Get-Content (Join-Path $Output 'results.log')
exit $result
