param([string]$ProjectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')), [string]$SourceRoot='', [string]$Output='')
$ErrorActionPreference='Stop'
if (!$SourceRoot) { $SourceRoot=$ProjectRoot }
if (!$Output) { $Output=Join-Path ([IO.Path]::GetTempPath()) ('NeEEvA-FormalReply-'+[Guid]::NewGuid().ToString('N')) }
[void](New-Item -ItemType Directory -Force $Output)
$chat='Assets/AIChatTookit/Scripts/Chat'
$paths=@((Join-Path $PSScriptRoot 'Regression.cs'),(Join-Path $PSScriptRoot 'ProjectionRegression.cs'))
foreach ($name in @('RoleDecision.cs','RecordingSingingDecision.cs','RecordingSingingSpeech.cs','RecordingSingingConsistency.cs','RecordingArchiveSnapshot.cs','RecordingSingingPitch.cs','RecordingSingingGoalTransition.cs','RoleDecisionSpeechStream.cs','SpeechText.cs','RoleOutputChannels.cs','RoleOutputPublicBoundary.cs')) {
    $candidate=Join-Path $SourceRoot ($chat+'/'+$name)
    if (!(Test-Path -LiteralPath $candidate)) { $candidate=Join-Path $ProjectRoot ($chat+'/'+$name) }
    $paths+=$candidate
}
$paths+=Join-Path $SourceRoot 'Assets/AIChatTookit/Scripts/LLM/FormalPromptProfile.cs'
$paths+=Join-Path $SourceRoot 'Assets/AIChatTookit/Scripts/LLM/PromptProtocolAssets.cs'
$paths+=Join-Path $SourceRoot 'Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.FormalPrompt.cs'
$stagePath=Join-Path $SourceRoot 'Assets/AIChatTookit/Scripts/LLM/FormalResponseStage.cs'
if(!(Test-Path -LiteralPath $stagePath)) { $stagePath=Join-Path $ProjectRoot 'Assets/AIChatTookit/Scripts/LLM/FormalResponseStage.cs' }
$paths+=$stagePath
$reference=Join-Path $ProjectRoot 'Assets/AIChatTookit/Plugin/Newtonsoft.Json.dll'
$sdkRoot=Join-Path (Split-Path (Get-Command dotnet).Source) 'sdk'
$sdk=Get-ChildItem $sdkRoot -Directory -Filter '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$ref=Get-ChildItem 'C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref' -Directory -Filter '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$argsList=@('-target:exe',('-out:"'+$Output+'/Regression.dll"'))
$argsList+=Get-ChildItem (Join-Path $ref.FullName 'ref/net10.0') -Filter '*.dll' | ForEach-Object { '-r:"'+$_.FullName+'"' }
$argsList+='-r:"'+$reference+'"'
$argsList+=$paths | ForEach-Object { '"'+$_+'"' }
[IO.File]::WriteAllLines((Join-Path $Output 'test.rsp'),$argsList)
& dotnet (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') ('@'+$Output+'/test.rsp') *> (Join-Path $Output 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $Output 'compile.log'); exit $LASTEXITCODE }
Copy-Item -LiteralPath $reference -Destination $Output -Force
[IO.File]::WriteAllText((Join-Path $Output 'Regression.runtimeconfig.json'),'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}')
& dotnet (Join-Path $Output 'Regression.dll') $ProjectRoot $Output *> (Join-Path $Output 'results.log')
$result=$LASTEXITCODE
Get-Content (Join-Path $Output 'results.log')
exit $result
