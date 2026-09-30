param([string]$Output='')
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$Output) { $Output=Join-Path $taskRoot 'work/runtime-convergence-20260927/input-isolation-pure' }
$Output=[IO.Path]::GetFullPath($Output)
[void][IO.Directory]::CreateDirectory($Output)
$taskFiles=@(
    (Join-Path $taskRoot 'Assets/AIChatTookit/Scripts/Chat/RecordingContinuationIsolation.cs'),
    (Join-Path $taskRoot 'Assets/Editor/RecordingContinuationIsolationRegression.cs')
)
$taskRunner=@'
using System;
using System.IO;
using System.Text.Json;
public static class Runner {
    public static int Main(string[] args) {
        try { int n=RecordingContinuationIsolationRegression.RunChecks();
            string json=JsonSerializer.Serialize(new { passed=true, checks=n, scope="Production capture lineage and exact PCM cropping; no microphone, remote ASR or acoustic accuracy claim" });
            File.WriteAllText(args[0],json); Console.WriteLine(json); return 0;
        } catch(Exception e) { File.WriteAllText(args[0],JsonSerializer.Serialize(new { passed=false, error=e.ToString() })); Console.WriteLine(e); return 1; }
    }
}
'@
[IO.File]::WriteAllText((Join-Path $Output 'Runner.cs'),$taskRunner)
$taskSdk=(& dotnet --version).Trim()
$taskMajor=[int]($taskSdk.Split('.')[0])
$taskDotnet=Split-Path (Get-Command dotnet).Source
$taskFramework='net'+$taskMajor+'.0'
$taskRef=Get-ChildItem (Join-Path $taskDotnet 'packs/Microsoft.NETCore.App.Ref') -Directory |
    Where-Object { $_.Name -like "$taskMajor.*" } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$taskArgs=@('-target:exe','-nologo',('-out:"'+$Output+'/Regression.dll"'))
$taskArgs+=Get-ChildItem (Join-Path $taskRef.FullName ('ref/'+$taskFramework)) -Filter '*.dll' | ForEach-Object { '-r:"'+$_.FullName+'"' }
$taskArgs+=$taskFiles | ForEach-Object { '"'+$_+'"' }
$taskArgs+='"'+(Join-Path $Output 'Runner.cs')+'"'
[IO.File]::WriteAllLines((Join-Path $Output 'test.rsp'),$taskArgs)
& dotnet (Join-Path $taskDotnet ('sdk/'+$taskSdk+'/Roslyn/bincore/csc.dll')) ('@'+$Output+'/test.rsp') *> (Join-Path $Output 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $Output 'compile.log'); exit $LASTEXITCODE }
$taskRuntime=@{runtimeOptions=@{tfm=$taskFramework;framework=@{name='Microsoft.NETCore.App';version="$taskMajor.0.0"}}}
[IO.File]::WriteAllText((Join-Path $Output 'Regression.runtimeconfig.json'),($taskRuntime | ConvertTo-Json -Depth 5))
& dotnet (Join-Path $Output 'Regression.dll') (Join-Path $Output 'result.json') *> (Join-Path $Output 'results.log')
$taskExit=$LASTEXITCODE
$taskFiles | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'source-hashes.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $Output 'results.log')
exit $taskExit
