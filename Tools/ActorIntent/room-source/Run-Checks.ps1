param(
    [string]$ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')),
    [string]$Output = '',
    [string]$ExportModel,
    [string]$ModelResults,
    [switch]$PreparationOnly
)
$ErrorActionPreference = 'Stop'
if (!$Output) { $Output = Join-Path $ProjectRoot 'Logs/actor-intent/room-source' }
[void](New-Item -ItemType Directory -Path $Output -Force)
$paths = @((Join-Path $PSScriptRoot 'Regression.cs'), (Join-Path $ProjectRoot 'Tools/RoomPreparation/WireRegression.cs'))
foreach ($source in @(
    'Assets/AIChatTookit/Scripts/Chat/RoomTaskProtocol.cs',
    'Assets/AIChatTookit/Scripts/Chat/RoomPreparationBudget.cs',
    'Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.RoomTask.cs'
)) { $paths += Join-Path $ProjectRoot $source }
$reference = Join-Path $ProjectRoot 'Assets/AIChatTookit/Plugin/Newtonsoft.Json.dll'
$sdkRoot = Join-Path (Split-Path (Get-Command dotnet).Source) 'sdk'
$sdk = Get-ChildItem $sdkRoot -Directory -Filter '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$ref = Get-ChildItem 'C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref' -Directory -Filter '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk -or !$ref) { throw 'The transport checks require the .NET 10 SDK and reference pack.' }
$arguments = @('-target:exe', ('-out:"' + $Output + '/Regression.dll"'))
$arguments += Get-ChildItem (Join-Path $ref.FullName 'ref/net10.0') -Filter '*.dll' | ForEach-Object { '-r:"' + $_.FullName + '"' }
$arguments += '-r:"' + $reference + '"'
$arguments += $paths | ForEach-Object { '"' + $_ + '"' }
[IO.File]::WriteAllLines((Join-Path $Output 'test.rsp'), $arguments)
& dotnet (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') ('@' + $Output + '/test.rsp') *> (Join-Path $Output 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $Output 'compile.log'); exit $LASTEXITCODE }
Copy-Item -LiteralPath $reference -Destination $Output -Force
[IO.File]::WriteAllText((Join-Path $Output 'Regression.runtimeconfig.json'), '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}')
$runArgs = @((Join-Path $Output 'Regression.dll'))
if ($ExportModel) { $runArgs += @('--export-model', [IO.Path]::GetFullPath($ExportModel)) }
if ($ModelResults) { $runArgs += @($(if ($PreparationOnly) {'--evaluate-preparation-model'} else {'--evaluate-model'}), [IO.Path]::GetFullPath($ModelResults)) }
& dotnet @runArgs *> (Join-Path $Output 'results.log')
$testExitCode = $LASTEXITCODE
Get-Content (Join-Path $Output 'results.log')
exit $testExitCode
