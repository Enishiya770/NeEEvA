$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testRoot=Join-Path $projectRoot 'Logs\semantic-ir-regression'
[void](New-Item -ItemType Directory -Force $testRoot)
$chat='Assets\AIChatTookit\Scripts\Chat'
$paths=@((Join-Path $PSScriptRoot 'Regression.cs'))
foreach($name in @('RoleDecision.cs','ConversationTrace.cs')) { $paths += Join-Path (Join-Path $projectRoot $chat) $name }
foreach($name in @('SpeechText.cs','RoleOutputChannels.cs','RoleOutputPublicBoundary.cs')) { $paths += Join-Path (Join-Path $projectRoot $chat) $name }
$compile=($paths | ForEach-Object { '<Compile Include="'+[Security.SecurityElement]::Escape($_)+'" />' }) -join "`n"
$reference=Join-Path $projectRoot 'Assets\AIChatTookit\Plugin\Newtonsoft.Json.dll'
$xml='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>'+$compile+'<Reference Include="Newtonsoft.Json"><HintPath>'+[Security.SecurityElement]::Escape($reference)+'</HintPath></Reference></ItemGroup></Project>'
[IO.File]::WriteAllText((Join-Path $testRoot 'Regression.csproj'),$xml)
$sdk=(& dotnet --version).Trim()
$dotnetRoot=Split-Path (Get-Command dotnet).Source
$refRoot=Get-ChildItem (Join-Path $dotnetRoot 'packs\Microsoft.NETCore.App.Ref') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$argsList=@('-target:exe',('-out:"'+$testRoot+'\Regression.dll"'))
$argsList += Get-ChildItem (Join-Path $refRoot.FullName 'ref\net10.0') -Filter '*.dll' | ForEach-Object { '-r:"'+$_.FullName+'"' }
$argsList += '-r:"'+$reference+'"'
$argsList += $paths | ForEach-Object { '"'+$_+'"' }
[IO.File]::WriteAllLines((Join-Path $testRoot 'test.rsp'),$argsList)
& dotnet (Join-Path $dotnetRoot ('sdk\'+$sdk+'\Roslyn\bincore\csc.dll')) ('@'+$testRoot+'\test.rsp') *> (Join-Path $testRoot 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $testRoot 'compile.log'); exit $LASTEXITCODE }
Copy-Item -LiteralPath $reference -Destination $testRoot -Force
[IO.File]::WriteAllText((Join-Path $testRoot 'Regression.runtimeconfig.json'),'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}')
& dotnet (Join-Path $testRoot 'Regression.dll') *> (Join-Path $testRoot 'results.log')
$code=$LASTEXITCODE
Get-Content (Join-Path $testRoot 'results.log') -Tail 15
exit $code
