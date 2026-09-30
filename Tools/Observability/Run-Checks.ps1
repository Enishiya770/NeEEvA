# Pure regression runner; requires Windows + .NET 10 SDK. No project writes.
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('NeeevaTraceChecks-'+[Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Force $testRoot)
$chat='Assets\AIChatTookit\Scripts\Chat'
$paths=@((Join-Path $projectRoot 'Tools/SemanticIR/Regression.cs'),(Join-Path $PSScriptRoot 'TraceRegression.cs'),(Join-Path $PSScriptRoot 'DeliveryRegression.cs'),(Join-Path $PSScriptRoot 'ClosureRegression.cs'))
$paths += Join-Path (Join-Path $projectRoot $chat) 'RoleDecision.cs'
foreach($name in @('ConversationTrace.cs','WaveformTrace.cs','RoleOutputChannels.cs','DeliveryDisposition.cs','AuxiliaryReply.cs','ChatSample.Delivery.cs','ReviewedDecisionHandoff.cs','ChatSample.ReviewHandoff.cs','BoundaryDecisionObservation.cs')) {
    $selectedRoot=$projectRoot
    $paths += Join-Path (Join-Path $selectedRoot $chat) $name
}
foreach($name in @('SpeechText.cs','RoleOutputPublicBoundary.cs')) { $paths += Join-Path (Join-Path $projectRoot $chat) $name }
$compile=($paths | ForEach-Object { '<Compile Include="'+[Security.SecurityElement]::Escape($_)+'" />' }) -join "`n"
$reference=Join-Path $projectRoot 'Assets\AIChatTookit\Plugin\Newtonsoft.Json.dll'
$xml='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>'+$compile+'<Reference Include="Newtonsoft.Json"><HintPath>'+[Security.SecurityElement]::Escape($reference)+'</HintPath></Reference></ItemGroup></Project>'
[IO.File]::WriteAllText((Join-Path $testRoot 'Regression.csproj'),$xml)
$refRoot=Get-ChildItem 'C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref' -Directory | Sort-Object Name -Descending | Select-Object -First 1
$argsList=@('-target:exe','-main:DeliveryRegression',('-out:"'+$testRoot+'\Regression.dll"'))
$argsList += Get-ChildItem (Join-Path $refRoot.FullName 'ref\net10.0') -Filter '*.dll' | ForEach-Object { '-r:"'+$_.FullName+'"' }
$argsList += '-r:"'+$reference+'"'
$argsList += $paths | ForEach-Object { '"'+$_+'"' }
[IO.File]::WriteAllLines((Join-Path $testRoot 'test.rsp'),$argsList)
$sdkHome=Join-Path (Split-Path (Get-Command dotnet).Source) 'sdk'
$cscPath=Get-ChildItem $sdkHome -Filter '10.*' -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
& dotnet (Join-Path $cscPath.FullName 'Roslyn/bincore/csc.dll') ('@'+$testRoot+'\test.rsp') *> (Join-Path $testRoot 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $testRoot 'compile.log'); exit $LASTEXITCODE }
Copy-Item -LiteralPath $reference -Destination $testRoot -Force
[IO.File]::WriteAllText((Join-Path $testRoot 'Regression.runtimeconfig.json'),'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}')
& dotnet (Join-Path $testRoot 'Regression.dll') *> (Join-Path $testRoot 'results.log')
$code=$LASTEXITCODE
Get-Content (Join-Path $testRoot 'results.log') -Tail 15
exit $code
