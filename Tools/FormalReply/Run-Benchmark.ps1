param([string]$ProjectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')), [string]$SourceRoot='', [string]$Output='')
$ErrorActionPreference='Stop'
if (!$SourceRoot) { $SourceRoot=$ProjectRoot }
if (!$Output) { $Output=Join-Path ([IO.Path]::GetTempPath()) ('NeEEvA-FormalBenchmark-'+[Guid]::NewGuid().ToString('N')) }
& (Join-Path $PSScriptRoot 'Run-Checks.ps1') -ProjectRoot $ProjectRoot -SourceRoot $SourceRoot -Output $Output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$argsList=Get-Content (Join-Path $Output 'test.rsp') | ForEach-Object {
    if($_ -match '^-out:') {'-out:"'+$Output+'/Benchmark.dll"'}else{$_}
}
$argsList+='-main:FormalReplyBenchmark'
$argsList+='"'+(Join-Path $PSScriptRoot 'Benchmark.cs')+'"'
[IO.File]::WriteAllLines((Join-Path $Output 'benchmark.rsp'),$argsList)
$sdkRoot=Join-Path (Split-Path (Get-Command dotnet).Source) 'sdk'
$sdk=Get-ChildItem $sdkRoot -Directory -Filter '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
& dotnet (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') ('@'+$Output+'/benchmark.rsp') *> (Join-Path $Output 'benchmark-compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $Output 'benchmark-compile.log'); exit $LASTEXITCODE }
Copy-Item -LiteralPath (Join-Path $Output 'Regression.runtimeconfig.json') -Destination (Join-Path $Output 'Benchmark.runtimeconfig.json') -Force
& dotnet (Join-Path $Output 'Benchmark.dll') (Join-Path $Output 'profiles.json') $Output $ProjectRoot
exit $LASTEXITCODE
