param(
    [string]$RunName = 'final-v1',
    [string]$UnityPath = 'E:\Unity_Data\2022.3.22f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$validationRoot = Join-Path $sourceRoot 'Server\ARDY\runtime\unity-naturalness-validation'
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName must be a simple directory name.' }
if (!(Test-Path -LiteralPath (Join-Path $validationRoot 'ProjectSettings\ProjectVersion.txt'))) {
    throw 'Prepare the isolated public validation project with sync-from-source.ps1 -IncludeDialogue first.'
}
if (Get-Process Unity -ErrorAction SilentlyContinue) { throw 'Close other Unity runs before taking the validation project lock.' }
$runRoot = Join-Path $sourceRoot ('Logs\agent-work-loop-20260910\' + $RunName)
if (Test-Path -LiteralPath $runRoot) { throw 'Run directory already exists; keep prior evidence and choose a new RunName.' }
[void](New-Item -ItemType Directory -Path $runRoot)

# Only registered production scripts, public skill text and test entry points are copied.
# The previously prepared isolated project has no private persona, history or chat scene.
$syncFiles = @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'Assets\AIChatTookit\Scripts\Chat') -Filter 'ChatSample*.cs*') +
    @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'Assets\Editor') -Filter 'Agent*Regression.cs*') +
    @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'Assets\Editor') -Filter 'SkillRoutingRegression*.cs*')
$extraPaths = @('Assets\AIChatTookit\Scripts\Chat\RoleOutputChannels.cs',
    'Assets\AIChatTookit\Scripts\TTS&&STT\SenseVoice\SenseVoiceSpeechToText.cs',
    'Assets\AIChatTookit\Scripts\LLM\LLM.cs', 'Assets\AIChatTookit\Scripts\LLM\QW\ChatQW.cs',
    'Assets\AIChatTookit\Prompts\Skills\singing.txt', 'Assets\AIChatTookit\Prompts\behavior.txt')
$syncFiles += $extraPaths | ForEach-Object { Get-Item -LiteralPath (Join-Path $sourceRoot $_) }
$manifest = foreach ($file in $syncFiles) {
    $relative = $file.FullName.Substring($sourceRoot.Length + 1)
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $validationRoot $relative) -Force
    @{ path = $relative; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
@{ source = $sourceRoot; validation = $validationRoot; files = @($manifest) } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'source-manifest.json') -Encoding UTF8

$suites = @(
    @{ name='loop'; entry='AgentWorkLoopRegression'; report='agent-work-loop-regression.json' },
    @{ name='goal'; entry='AgentSingingGoalRegression'; report='agent-singing-goal-regression.json' },
    @{ name='transport'; entry='AgentWorkReviewTransportRegression'; report='agent-work-review-transport-regression.json' },
    @{ name='http'; entry='AgentWorkReviewHttpRegression'; report='agent-work-review-http-regression.json' },
    @{ name='compatibility'; entry='AgentSelfInspectionRegression'; report='agent-self-inspection-regression.json' },
    @{ name='playmode'; entry='AgentSelfInspectionPlayModeRegression'; report='agent-self-inspection-playmode.json' }
)
$results = @()
foreach ($suite in $suites) {
    $logPath = Join-Path $runRoot ($suite.name + '.log')
    $started = [DateTime]::UtcNow
    Write-Output ('Starting ' + $suite.entry)
    $process = Start-Process -FilePath $UnityPath -ArgumentList @('-batchmode', '-nographics',
        '-projectPath', ('"' + $validationRoot + '"'), '-executeMethod', ($suite.entry + '.RunBatch'),
        '-logFile', ('"' + $logPath + '"')) -PassThru -WindowStyle Hidden
    if (!$process.WaitForExit(180000)) {
        Stop-Process -Id $process.Id -Force
        throw ($suite.entry + ' exceeded its isolated test timeout.')
    }
    $reportPath = Join-Path $validationRoot ('Tools\MotionAdapter\reports\' + $suite.report)
    if (!(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) {
        throw ($suite.entry + ' did not produce a fresh report. See ' + $logPath)
    }
    Copy-Item -LiteralPath $reportPath -Destination (Join-Path $runRoot $suite.report)
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    $compilerErrors = @(Select-String -LiteralPath $logPath -Pattern 'error CS[0-9]+:')
    $passed = $report.passed -eq $true -and $process.ExitCode -eq 0 -and $compilerErrors.Count -eq 0
    $results += @{ suite=$suite.entry; passed=$passed; checks=$report.checks; exitCode=$process.ExitCode; report=$suite.report }
    $results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'suite-results.json') -Encoding UTF8
    Write-Output ($suite.entry + ' passed=' + $passed + ' checks=' + $report.checks)
    if (!$passed) { throw ($suite.entry + ' failed. See ' + $logPath) }
    if ($suite.name -eq 'goal') {
        Copy-Item -LiteralPath (Join-Path $validationRoot 'Tools\MotionAdapter\reports\agent-singing-goal-public-model-cases.json') -Destination $runRoot
    }
}
$checks = foreach ($file in $manifest) {
    @{ path=$file.path; sourceMatches=((Get-FileHash -LiteralPath (Join-Path $sourceRoot $file.path)).Hash -eq $file.sha256);
        testedCopyMatches=((Get-FileHash -LiteralPath (Join-Path $validationRoot $file.path)).Hash -eq $file.sha256) }
}
$sourceMatches = @($checks | Where-Object { !$_.sourceMatches -or !$_.testedCopyMatches }).Count -eq 0
@{ passed=$sourceMatches; checkedAtUtc=[DateTime]::UtcNow.ToString('o'); suites=$results; files=@($checks);
    scope='Isolated Unity, public synthetic inputs and actual production code. Tool completions simulated except real loopback HTTP. No private scene, microphone or physical speaker validation.' } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'verification.json') -Encoding UTF8
if (!$sourceMatches) { throw 'Source changed during verification; inspect the saved manifest.' }
Write-Output ('All isolated suites passed with unchanged source: ' + $runRoot)
