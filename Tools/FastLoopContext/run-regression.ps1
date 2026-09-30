param([string]$ExportProtocol, [string]$ModelResults, [string]$Cohort, [string]$ExportSession)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testRoot = Join-Path $projectRoot 'Logs\fast-loop-context-regression'
[void](New-Item -ItemType Directory -Force -Path $testRoot)
$chat = Join-Path $projectRoot 'Assets\AIChatTookit\Scripts\Chat'
$paths = @((Join-Path $PSScriptRoot 'Regression.cs'), (Join-Path $chat 'FastLoopPrompt.cs'), (Join-Path $chat 'FastLoopProtocol.cs'), (Join-Path $projectRoot 'Assets\AIChatTookit\Scripts\LLM\FormalPromptProfile.cs'))
$reference = Join-Path $projectRoot 'Assets\AIChatTookit\Plugin\Newtonsoft.Json.dll'
$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'A .NET SDK is required.' }
$sdkMajor = [int]($sdk.Split('.')[0])
$framework = 'net' + $sdkMajor + '.0'
$dotnetRoot = Split-Path (Get-Command dotnet).Source
$refRoot = Get-ChildItem (Join-Path $dotnetRoot 'packs\Microsoft.NETCore.App.Ref') -Directory | Where-Object { $_.Name -like "$sdkMajor.*" } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$argsList = @('-target:exe', '-nologo', ('-out:"' + $testRoot + '\Regression.dll"'))
$argsList += Get-ChildItem (Join-Path $refRoot.FullName "ref\$framework") -Filter '*.dll' | ForEach-Object { '-r:"' + $_.FullName + '"' }
$argsList += '-r:"' + $reference + '"'
$argsList += $paths | ForEach-Object { '"' + $_ + '"' }
[IO.File]::WriteAllLines((Join-Path $testRoot 'test.rsp'), $argsList)
& dotnet (Join-Path $dotnetRoot ('sdk\' + $sdk + '\Roslyn\bincore\csc.dll')) ('@' + $testRoot + '\test.rsp') *> (Join-Path $testRoot 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $testRoot 'compile.log'); exit $LASTEXITCODE }
Copy-Item -LiteralPath $reference -Destination $testRoot -Force
$runtime = @{ runtimeOptions = @{ tfm = $framework; framework = @{ name = 'Microsoft.NETCore.App'; version = "$sdkMajor.0.0" } } }
[IO.File]::WriteAllText((Join-Path $testRoot 'Regression.runtimeconfig.json'), ($runtime | ConvertTo-Json -Depth 8))
$runArgs = @((Join-Path $testRoot 'Regression.dll'))
if ($ExportProtocol) { $runArgs += @('--export', [IO.Path]::GetFullPath($ExportProtocol), '--root', $projectRoot, '--fixtures', (Join-Path $PSScriptRoot 'cases.jsonl')) }
if ($ExportSession) { $runArgs += @('--export-session', [IO.Path]::GetFullPath($ExportSession), '--root', $projectRoot) }
if ($ModelResults) {
    if (-not $Cohort) { $Cohort = Join-Path (Split-Path $ModelResults) 'production-protocol.json' }
    $runArgs += @('--results', [IO.Path]::GetFullPath($ModelResults), '--cohort', [IO.Path]::GetFullPath($Cohort))
}
& dotnet @runArgs *> (Join-Path $testRoot 'results.log')
$code = $LASTEXITCODE
Get-Content (Join-Path $testRoot 'results.log')
exit $code
