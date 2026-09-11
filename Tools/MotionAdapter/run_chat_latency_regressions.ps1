param(
    [string]$RunName = 'chat-latency-v1',
    [string]$UnityPath = 'E:\Unity_Data\2022.3.22f1\Editor\Unity.exe',
    [ValidateSet('all', 'focused', 'export-baseline')][string]$Scope = 'all'
)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$validationRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot 'Server\ARDY\runtime\unity-naturalness-validation'))
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunName must be a simple directory name.' }
if (!(Test-Path -LiteralPath $UnityPath)) { throw 'Unity executable was not found.' }
if (!(Test-Path -LiteralPath (Join-Path $validationRoot 'ProjectSettings\ProjectVersion.txt'))) {
    throw 'Prepare the isolated public validation project with sync-from-source.ps1 -IncludeDialogue first.'
}
if (Get-Process Unity -ErrorAction SilentlyContinue) { throw 'Close other Unity runs before taking the validation project lock.' }
$runRoot = Join-Path $sourceRoot ('Logs\chat-latency-20260911\' + $RunName)
if (Test-Path -LiteralPath $runRoot) { throw 'Run directory exists; choose a new RunName to retain prior evidence.' }
[void](New-Item -ItemType Directory -Path $runRoot)

function Copy-RegisteredFile([IO.FileInfo]$File) {
    $relative = $File.FullName.Substring($sourceRoot.Length + 1)
    $destination = [IO.Path]::GetFullPath((Join-Path $validationRoot $relative))
    if (!$destination.StartsWith($validationRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Registered copy escaped the isolated project.'
    }
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force)
    Copy-Item -LiteralPath $File.FullName -Destination $destination -Force
    @{ path = $relative; sha256 = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash }
}

# Baseline exports use the earlier runtime already installed in the isolated public
# project. Only this new test entry is copied; production and prompts remain intact.
# Export mode measures request construction, not a passing regression or real latency.
if ($Scope -eq 'export-baseline') {
    $syncFiles = @(Get-Item -LiteralPath (Join-Path $sourceRoot 'Assets\Editor\ChatLatencyRegression.cs'))
    $meta = Join-Path $sourceRoot 'Assets\Editor\ChatLatencyRegression.cs.meta'
    if (Test-Path -LiteralPath $meta) { $syncFiles += Get-Item -LiteralPath $meta }
} else {
    $syncFiles = @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'Assets\AIChatTookit\Scripts\Chat') -Filter 'ChatSample*.cs*')
    foreach ($filter in @('Agent*Regression.cs*', 'Ardy*Regression.cs*', 'SkillRoutingRegression*.cs*', 'ChatLatency*Regression.cs*', 'SpeechStreamingRegression.cs*')) {
        $syncFiles += Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'Assets\Editor') -Filter $filter
    }
    foreach ($relative in @('Assets\AIChatTookit\Scripts\Chat\RoleOutputChannels.cs',
        'Assets\AIChatTookit\Scripts\Chat\SpeechText.cs',
        'Assets\AIChatTookit\Scripts\TTS&&STT\SenseVoice\SenseVoiceSpeechToText.cs',
        'Assets\AIChatTookit\Scripts\LLM\LLM.cs', 'Assets\AIChatTookit\Scripts\LLM\QW\ChatQW.cs',
        'Assets\AIChatTookit\Prompts\Skills\singing.txt', 'Assets\AIChatTookit\Prompts\behavior.txt')) {
        $syncFiles += Get-Item -LiteralPath (Join-Path $sourceRoot $relative)
    }
}
$manifest = @($syncFiles | Sort-Object -Property FullName -Unique | ForEach-Object { Copy-RegisteredFile $_ })
@{ source = $sourceRoot; validation = $validationRoot; scope = $Scope; files = $manifest } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'source-manifest.json') -Encoding UTF8

# Record tested dependencies too: in export-baseline mode the source tree deliberately
# differs from the isolated runtime, so never label source as that baseline's runtime.
$testedPaths = @(& rg --files (Join-Path $validationRoot 'Assets\AIChatTookit\Scripts') -g '*.cs')
$testedFiles = @($testedPaths | ForEach-Object {
    @{ path = $_.Substring($validationRoot.Length + 1); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
})
$testedFiles | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'tested-runtime-manifest.json') -Encoding UTF8

$suites = @(
    @{ name='chat-latency'; entry='ChatLatencyRegression'; report='chat-latency-regression.json' },
    @{ name='speech-streaming'; entry='AgentSpeechPhaseRegression'; report='agent-speech-phase-regression.json' },
    @{ name='lifecycle'; entry='ChatLatencyLifecycleRegression'; report='chat-latency-lifecycle-regression.json' },
    @{ name='latency-transport'; entry='ChatLatencyTransportRegression'; report='chat-latency-transport-regression.json' },
    @{ name='skill-routing'; entry='SkillRoutingRegression'; marker='[SkillRoutingRegression] all cases passed' }
)
if ($Scope -eq 'export-baseline') { $suites = @(@{ name='public-baseline'; entry='ChatLatencyRegression'; method='RunExportBatch'; report='chat-latency-regression.json' }) }
if ($Scope -eq 'all') {
    $suites += @(
        @{ name='work-loop'; entry='AgentWorkLoopRegression'; report='agent-work-loop-regression.json' },
        @{ name='singing-goal'; entry='AgentSingingGoalRegression'; report='agent-singing-goal-regression.json' },
        @{ name='work-review-transport'; entry='AgentWorkReviewTransportRegression'; report='agent-work-review-transport-regression.json' },
        @{ name='work-review-http'; entry='AgentWorkReviewHttpRegression'; report='agent-work-review-http-regression.json' },
        @{ name='self-inspection'; entry='AgentSelfInspectionRegression'; report='agent-self-inspection-regression.json' },
        @{ name='self-inspection-playmode'; entry='AgentSelfInspectionPlayModeRegression'; report='agent-self-inspection-playmode.json' }
    )
}
$results = @()
foreach ($suite in $suites) {
    $logPath = Join-Path $runRoot ($suite.name + '.log')
    $started = [DateTime]::UtcNow
    $method = if ($suite.method) { $suite.method } else { 'RunBatch' }
    Write-Output ('Starting ' + $suite.entry + '.' + $method)
    $unityArguments = @('-batchmode', '-nographics',
        '-projectPath', ('"' + $validationRoot + '"'), '-executeMethod', ($suite.entry + '.' + $method),
        '-logFile', ('"' + $logPath + '"'))
    $process = Start-Process -FilePath $UnityPath -ArgumentList $unityArguments -PassThru -WindowStyle Hidden
    while (!$process.WaitForExit(30000)) {
        if (([DateTime]::UtcNow - $started).TotalSeconds -gt 180) {
            Stop-Process -Id $process.Id -Force
            throw ($suite.entry + ' exceeded its isolated test timeout.')
        }
        Write-Output ('Still running ' + $suite.entry)
    }
    $process.Refresh()
    @{ executable = $UnityPath; arguments = $unityArguments; processId = $process.Id; exitCode = $process.ExitCode;
        elapsedSeconds = ([DateTime]::UtcNow - $started).TotalSeconds; logCreated = (Test-Path -LiteralPath $logPath) } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot ($suite.name + '-launch.json')) -Encoding UTF8
    if (!(Test-Path -LiteralPath $logPath)) {
        throw ($suite.entry + ' exited before creating its Unity log; exitCode=' + $process.ExitCode +
            '. Inspect the launch JSON. Check Unity initialization / license and sandbox write access; no test result exists.')
    }
    $compilerErrors = @(Select-String -LiteralPath $logPath -Pattern 'error CS[0-9]+:')
    $passed = $process.ExitCode -eq 0 -and $compilerErrors.Count -eq 0
    $checks = $null
    if ($suite.report) {
        $reportPath = Join-Path $validationRoot ('Tools\MotionAdapter\reports\' + $suite.report)
        if (!(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTimeUtc -lt $started) {
            throw ($suite.entry + ' did not produce a fresh report. See ' + $logPath)
        }
        Copy-Item -LiteralPath $reportPath -Destination (Join-Path $runRoot $suite.report)
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $verdict = if ($Scope -eq 'export-baseline') { $report.exportOnly -eq $true -and $report.exportSucceeded -eq $true } else { $report.passed -eq $true }
        $passed = $passed -and $verdict; $checks = $report.checks
    } else {
        $passed = $passed -and [bool](Select-String -LiteralPath $logPath -SimpleMatch -Pattern $suite.marker)
    }
    $results += @{ suite = $suite.entry; passed = $passed; checks = $checks; exitCode = $process.ExitCode; report = $suite.report }
    $results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'suite-results.json') -Encoding UTF8
    Write-Output ($suite.entry + ' passed=' + $passed + ' checks=' + $checks)
    if (!$passed) { throw ($suite.entry + ' failed. See ' + $logPath) }
    if ($suite.entry -eq 'ChatLatencyRegression') {
        Copy-Item -LiteralPath (Join-Path $validationRoot 'Tools\MotionAdapter\reports\chat-latency-public-requests.json') -Destination $runRoot
    }
    if ($suite.entry -eq 'AgentSingingGoalRegression') {
        Copy-Item -LiteralPath (Join-Path $validationRoot 'Tools\MotionAdapter\reports\agent-singing-goal-public-model-cases.json') -Destination $runRoot
    }
}
$unchanged = @($manifest | ForEach-Object {
    @{ path = $_.path; sourceMatches = ((Get-FileHash -LiteralPath (Join-Path $sourceRoot $_.path)).Hash -eq $_.sha256);
        testedCopyMatches = ((Get-FileHash -LiteralPath (Join-Path $validationRoot $_.path)).Hash -eq $_.sha256) }
})
$allMatch = @($unchanged | Where-Object { !$_.sourceMatches -or !$_.testedCopyMatches }).Count -eq 0
@{ passed = $allMatch -and $Scope -ne 'export-baseline'; exportOnly = $Scope -eq 'export-baseline'; completed = $allMatch;
    checkedAtUtc = [DateTime]::UtcNow.ToString('o'); suites = $results; files = $unchanged;
    scope = 'Isolated public synthetic inputs. Actual routing, final request assembly and first-chunk admission; scripted model/TTS inputs. No private scene, microphone, remote model, TTS output or real user-latency measurement.' } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'verification.json') -Encoding UTF8
if (!$allMatch) { throw 'Copied source changed during verification; inspect the saved manifests.' }
Write-Output ('Isolated run completed: ' + $runRoot)
