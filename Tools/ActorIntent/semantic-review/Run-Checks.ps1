param([string]$ExportProtocol, [string]$Output)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$testRoot = if ($Output) { [IO.Path]::GetFullPath($Output) } else { Join-Path $projectRoot 'work\intent-closure-repair-20260926\actor-semantic' }
[void](New-Item -ItemType Directory -Force -Path $testRoot)
$chat = Join-Path $projectRoot 'Assets\AIChatTookit\Scripts\Chat'
$paths = @((Join-Path $PSScriptRoot 'Regression.cs'))
foreach ($name in @('ActorIntentProtocol.cs', 'FastLoopProtocol.cs', 'FastLoopRuntime.cs')) {
    $path = Join-Path $chat $name
    if (-not (Test-Path -LiteralPath $path)) { throw "Production source not found: $path" }
    $paths += $path
}
$reference = Join-Path $projectRoot 'Assets\AIChatTookit\Plugin\Newtonsoft.Json.dll'
$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'A .NET SDK is required.' }
$sdkMajor = [int]($sdk.Split('.')[0])
$framework = 'net' + $sdkMajor + '.0'
$dotnetRoot = Split-Path (Get-Command dotnet).Source
$refRoot = Get-ChildItem (Join-Path $dotnetRoot 'packs\Microsoft.NETCore.App.Ref') -Directory |
    Where-Object { $_.Name -like "$sdkMajor.*" } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $refRoot) { throw "No reference pack for $framework" }
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
if ($ExportProtocol) { $runArgs += @('--export', [IO.Path]::GetFullPath($ExportProtocol)) }
& dotnet @runArgs
exit $LASTEXITCODE
