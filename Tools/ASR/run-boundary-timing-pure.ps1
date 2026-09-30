param(
    [string]$ProjectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')),
    [string]$Output=''
)
$ErrorActionPreference='Stop'
if (!$Output) { $Output=Join-Path $ProjectRoot 'Logs/asr/boundary-pure' }
[void](New-Item -ItemType Directory -Force $Output)
$sourcePath=Join-Path $ProjectRoot 'Assets/AIChatTookit/Scripts/Chat/RTSpeechHandler.cs'
$evidencePath=Join-Path $ProjectRoot 'Assets/AIChatTookit/Scripts/Chat/TurnBoundaryEvidence.cs'
$recorder=[IO.File]::ReadAllText($sourcePath)
$evidence=[IO.File]::ReadAllText($evidencePath)
function Extract-Block([string]$source,[string]$marker) {
    $start=$source.IndexOf($marker,[StringComparison]::Ordinal)
    if($start -lt 0) { throw "Missing production block: $marker" }
    $opening=$source.IndexOf('{',$start)
    $depth=0
    for($i=$opening;$i -lt $source.Length;$i++) {
        if($source[$i] -eq '{') { $depth++ }
        if($source[$i] -eq '}') { $depth--; if($depth -eq 0) { return $source.Substring($start,$i-$start+1) } }
    }
    throw "Unclosed production block: $marker"
}
$budget=Extract-Block $recorder 'private static bool ShouldStartRemoteBoundaryReview('
$timing=Extract-Block $recorder 'private void ObserveBoundaryRequestDuration('
$normalize=Extract-Block $recorder 'private static string NormalizeSemanticTranscript('
$endpoint=[regex]::Match($recorder,'private static bool CanUsePreparedOrdinaryEndpoint\([^;]+;').Value
if(!$endpoint) { throw 'Missing prepared ordinary endpoint guard' }
$sound=[regex]::Match($recorder,'private static bool HasSoundAdvanced\([^;]+;').Value
if(!$sound) { throw 'Missing sound continuation guard' }
$activity=Extract-Block $evidence 'public sealed class TurnActivityEvidence'
$wrapper=@'
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
public sealed class RecorderBudget {
    public bool m_StalledReviewRemoteAllowed, m_BoundaryRequestTimingRecorded, m_StalledTranscriptReviewRequested, m_BoundaryRemoteTimingArmed;
    public float m_StalledTranscriptReviewRequestTime, m_ObservedBoundarySeconds;
    public int CurrentCaptureSerial => 1;
'@
$wrapper+="`n"+$budget+"`n"+$timing+"`n"+$normalize+"`n"+$sound+"`n"+$endpoint+"`n}`n"+$activity
[IO.File]::WriteAllText((Join-Path $Output 'ExtractedProduction.cs'),$wrapper)
$ref=Get-ChildItem 'C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Ref' -Directory -Filter '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$sdk=Get-ChildItem 'C:/Program Files/dotnet/sdk' -Directory -Filter '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if(!$ref -or !$sdk) { throw '.NET 10 SDK/reference pack is required' }
$compilerArgs=@('-target:exe',('-out:"'+$Output+'/Regression.dll"'))
$compilerArgs+=Get-ChildItem (Join-Path $ref.FullName 'ref/net10.0') -Filter '*.dll' | ForEach-Object { '-r:"'+$_.FullName+'"' }
$compilerArgs+='"'+(Join-Path $Output 'ExtractedProduction.cs')+'"'
$compilerArgs+='"'+(Join-Path $PSScriptRoot 'BoundaryTimingPure.cs')+'"'
[IO.File]::WriteAllLines((Join-Path $Output 'test.rsp'),$compilerArgs)
& dotnet (Join-Path $sdk.FullName 'Roslyn/bincore/csc.dll') ('@'+$Output+'/test.rsp') *> (Join-Path $Output 'compile.log')
if($LASTEXITCODE -ne 0) { Get-Content (Join-Path $Output 'compile.log'); exit $LASTEXITCODE }
[IO.File]::WriteAllText((Join-Path $Output 'Regression.runtimeconfig.json'),'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}')
& dotnet (Join-Path $Output 'Regression.dll') (Join-Path $Output 'result.json') *> (Join-Path $Output 'results.log')
$exitCode=$LASTEXITCODE
$manifest=@{ recorderSha256=(Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash; evidenceSha256=(Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash; extractedAtUtc=[DateTime]::UtcNow.ToString('o'); scope='Exact production methods and evidence class extracted at run time; no Unity, model or microphone.' }
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'source-manifest.json') -Encoding UTF8
Get-Content (Join-Path $Output 'results.log')
exit $exitCode
