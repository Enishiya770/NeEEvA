param(
    [Parameter(Mandatory=$true)][string]$Capture,
    [string]$RunName = 'model-replay-v1',
    [string]$UnityPath = 'E:\Unity_Data\2022.3.22f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$validationRoot = Join-Path $sourceRoot 'Server\ARDY\runtime\unity-naturalness-validation'
$capturePath = (Resolve-Path -LiteralPath $Capture).Path
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName must be a simple directory name.' }
if (Get-Process Unity -ErrorAction SilentlyContinue) { throw 'Wait for the other Unity run before replay.' }
$captured = Get-Content -LiteralPath $capturePath -Raw | ConvertFrom-Json
if ($captured.publicSyntheticInputsOnly -ne $true -or $captured.transportComplete -ne $true) {
    throw 'Replay requires a completed public synthetic model capture.'
}
$runRoot = Join-Path $sourceRoot ('Logs\chat-latency-20260911\' + $RunName)
if (Test-Path -LiteralPath $runRoot) { throw 'Run directory exists; choose a new RunName.' }
[void](New-Item -ItemType Directory -Path $runRoot)

# Reuse the already verified isolated runtime. Do not silently change the runtime
# under the earlier suite manifest just to compile this new replay entry.
$runtimeFiles = @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'Assets\AIChatTookit\Scripts\Chat') -Filter 'ChatSample*.cs')
foreach ($relative in @('Assets\AIChatTookit\Scripts\Chat\SpeechText.cs', 'Assets\AIChatTookit\Scripts\Chat\RoleOutputChannels.cs',
    'Assets\AIChatTookit\Scripts\LLM\QW\ChatQW.cs', 'Assets\AIChatTookit\Scripts\LLM\LLM.cs')) {
    $runtimeFiles += Get-Item -LiteralPath (Join-Path $sourceRoot $relative)
}
$manifest = @($runtimeFiles | ForEach-Object {
    $relative = $_.FullName.Substring($sourceRoot.Length + 1)
    $sourceHash = (Get-FileHash -LiteralPath $_.FullName).Hash
    $testedHash = (Get-FileHash -LiteralPath (Join-Path $validationRoot $relative)).Hash
    if ($sourceHash -ne $testedHash) { throw ('Run the focused suite to synchronize changed runtime first: ' + $relative) }
    @{ path = $relative; sha256 = $sourceHash }
})
foreach ($relative in @('Assets\Editor\ChatLatencyModelReplayRegression.cs', 'Assets\Editor\ChatLatencyModelReplayRegression.cs.meta')) {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $relative) -Destination (Join-Path $validationRoot $relative) -Force
    $manifest += @{ path = $relative; sha256 = (Get-FileHash -LiteralPath (Join-Path $sourceRoot $relative)).Hash }
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'source-manifest.json') -Encoding UTF8
$logPath = Join-Path $runRoot 'replay.log'
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $UnityPath -ArgumentList @('-batchmode', '-nographics',
    '-projectPath', ('"' + $validationRoot + '"'), '-executeMethod', 'ChatLatencyModelReplayRegression.RunBatch',
    '-chatLatencyModelReport', ('"' + $capturePath + '"'), '-logFile', ('"' + $logPath + '"')) -PassThru -WindowStyle Hidden
while (!$process.WaitForExit(30000)) {
    if (([DateTime]::UtcNow - $started).TotalSeconds -gt 180) { Stop-Process -Id $process.Id -Force; throw 'Replay Unity timeout.' }
    Write-Output 'Unity is compiling/running the isolated public replay.'
}
$process.Refresh()
if (!(Test-Path -LiteralPath $logPath)) { throw ('Unity exited before creating its log: ' + $process.ExitCode) }
$reportPath = Join-Path $validationRoot 'Tools\MotionAdapter\reports\chat-latency-model-replay-regression.json'
if (!(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) {
    throw ('Replay did not produce a fresh report: ' + $logPath)
}
Copy-Item -LiteralPath $reportPath -Destination $runRoot
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$unchanged = @($manifest | Where-Object {
    (Get-FileHash -LiteralPath (Join-Path $sourceRoot $_.path)).Hash -ne $_.sha256 -or
    (Get-FileHash -LiteralPath (Join-Path $validationRoot $_.path)).Hash -ne $_.sha256
}).Count -eq 0
$compilerErrors = @(Select-String -LiteralPath $logPath -Pattern 'error CS[0-9]+:')
$passed = $report.passed -eq $true -and $process.ExitCode -eq 0 -and $unchanged -and $compilerErrors.Count -eq 0
@{ passed = $passed; checks = $report.checks; exitCode = $process.ExitCode; sourceUnchanged = $unchanged;
    capture = $capturePath; captureSha256 = (Get-FileHash -LiteralPath $capturePath).Hash;
    scope = 'Offline production replay of previously captured public SSE; no network/model/TTS/tools.' } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'verification.json') -Encoding UTF8
if (!$passed) { throw ('Production replay failed; see ' + $logPath) }
Write-Output ('Production public replay passed: ' + $runRoot)
