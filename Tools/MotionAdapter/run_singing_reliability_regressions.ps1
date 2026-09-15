param(
    [Parameter(Mandatory=$true)][string]$RunName,
    [string]$UnityPath = 'E:/Unity_Data/2022.3.22f1/Editor/Unity.exe',
    [string]$Entry = 'SingingReliabilityBatch.RunBatch'
)
$ErrorActionPreference = 'Stop'
if ($RunName -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Use a simple, new run name.' }
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$validationRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot 'Server/ARDY/runtime/unity-naturalness-validation'))
$runRoot = Join-Path $sourceRoot ('Server/ARDY/runtime/singing-reliability-fix-20260915/' + $RunName)
if (!(Test-Path -LiteralPath (Join-Path $validationRoot 'ProjectSettings/ProjectVersion.txt'))) { throw 'Missing isolated validation project.' }
if (Test-Path -LiteralPath $runRoot) { throw 'Keep earlier evidence; select a new run name.' }
$lockPath = Join-Path $validationRoot 'Temp/UnityLockfile'
if (Test-Path -LiteralPath $lockPath) {
    try { $stream = [IO.File]::Open($lockPath, 'Open', 'ReadWrite', 'None'); $stream.Dispose() }
    catch { throw 'The isolated project is in use. The user Editor is not affected.' }
}
[void](New-Item -ItemType Directory -Path $runRoot)

# Existing isolated dependencies remain in place. No private scene or persona is copied.
$files = @()
foreach ($directory in @('Assets/AIChatTookit/Scripts/Chat','Assets/AIChatTookit/Scripts/LLM')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $sourceRoot $directory) -Recurse -File |
        Where-Object { $_.Name -like '*.cs' -or $_.Name -like '*.cs.meta' }
}
foreach ($filter in @('Agent*Regression.cs*','SkillRoutingRegression*.cs*','RoleOutput*Regression.cs*','Singing*Regression.cs*','FirstReply*Regression.cs*')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'Assets/Editor') -Filter $filter -File
}
foreach ($relative in @('Assets/AIChatTookit/Prompts/behavior.txt','Assets/AIChatTookit/Prompts/Skills/singing.txt')) {
    $files += Get-Item -LiteralPath (Join-Path $sourceRoot $relative)
}
$manifest = @($files | Sort-Object FullName -Unique | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($sourceRoot, $_.FullName)
    $destination = [IO.Path]::GetFullPath((Join-Path $validationRoot $relative))
    if (!$destination.StartsWith($validationRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Copy escaped isolated project.' }
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force)
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    @{ path=$relative; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'source-manifest.json') -Encoding utf8

$runner = @'
using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
public static class SingingReliabilityBatch {
    public static void ExportFormalBatch() {
        try {
            SingingOutputContractRegression.ExportPublicFormalCasesOrThrow(Environment.GetEnvironmentVariable("NEEEVA_SINGING_RELIABILITY_OUTPUT"));
            EditorApplication.Exit(0);
        } catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }
    public static void ValidateFormalBatch() {
        try {
            var report = SingingOutputContractRegression.ValidatePublicFormalResponses(
                Environment.GetEnvironmentVariable("NEEEVA_SINGING_FORMAL_CASES"),
                Environment.GetEnvironmentVariable("NEEEVA_SINGING_FORMAL_RESPONSES"),
                Environment.GetEnvironmentVariable("NEEEVA_SINGING_RELIABILITY_OUTPUT"));
            EditorApplication.Exit((bool)report["passed"] ? 0 : 1);
        } catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }
    public static void RunBatch() {
        var rows = new JArray();
        bool passed = true;
        foreach (var item in new[] {
            "RoleOutputPublicBoundaryRegression.RunOrThrow", "SingingOutputContractRegression.RunRecordedOrThrow",
            "SingingOutputContractRegression.RunPromptAuditOrThrow",
            "AgentSpeechPhaseRegression.RunOrThrow", "AgentSingingGoalRegression.RunInteractive",
            "AgentWorkReviewTransportRegression.RunInteractive", "AgentSingingPolicyRegression.RunProtocolChecks",
            "SkillRoutingRegression.RunInteractive", "FirstReplyProtocolRegression.RunChecks"
        }) {
            var row = new JObject { ["suite"] = item }; rows.Add(row);
            try {
                int dot = item.LastIndexOf('.'); Type type = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                    type = assembly.GetType(item.Substring(0,dot)); if (type != null) break;
                }
                if (type == null) throw new MissingMemberException(item);
                var method = type.GetMethod(item.Substring(dot+1), BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static);
                if (method == null) throw new MissingMethodException(item);
                string source = Environment.GetEnvironmentVariable("NEEEVA_SINGING_RELIABILITY_SOURCE");
                string output = Environment.GetEnvironmentVariable("NEEEVA_SINGING_RELIABILITY_OUTPUT");
                object[] arguments = null;
                if (item.EndsWith(".RunRecordedOrThrow")) arguments = new object[] { Path.Combine(source, "Server/ARDY/runtime/singing-reliability-20260915/Editor.log") };
                if (item.EndsWith(".RunPromptAuditOrThrow")) arguments = new object[] {
                    Path.Combine(source, "Server/ARDY/runtime/singing-reliability-fix-20260915/baseline/Assets/AIChatTookit/Prompts/behavior.txt"),
                    Path.Combine(output, "prompt-audit") };
                var result = method.Invoke(null, arguments);
                row["passed"] = true;
                if (result is JObject report) { row["result"] = report; if (report["checks"] != null) row["checks"] = report["checks"].DeepClone(); }
                else if (result is int count) row["checks"] = count;
                else {
                    var field = type.GetField("checks", BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);
                    if (field != null && field.FieldType == typeof(int)) row["checks"] = (int)field.GetValue(null);
                }
            } catch (Exception error) {
                passed = false; row["passed"] = false;
                row["error"] = (error.InnerException ?? error).ToString();
                Debug.LogError(row.ToString());
            }
        }
        string path = Path.Combine(Environment.GetEnvironmentVariable("NEEEVA_SINGING_RELIABILITY_OUTPUT"), "combined.json");
        File.WriteAllText(path,new JObject { ["passed"] = passed, ["suites"] = rows,
            ["scope"] = "Isolated Unity production code, controlled semantic replies and audio facts; no model, microphone or audible output." }.ToString());
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
'@
[IO.File]::WriteAllText((Join-Path $validationRoot 'Assets/Editor/SingingReliabilityBatch.cs'), $runner)
$env:NEEEVA_SINGING_RELIABILITY_OUTPUT = $runRoot
$env:NEEEVA_POLICY_REGRESSION_OUTPUT = $runRoot
$env:NEEEVA_SINGING_RELIABILITY_SOURCE = $sourceRoot
$started = [DateTime]::UtcNow
$logPath = Join-Path $runRoot 'unity.log'
$process = Start-Process -FilePath $UnityPath -ArgumentList @('-batchmode','-nographics','-projectPath',('"'+$validationRoot+'"'),'-executeMethod',$Entry,'-logFile',('"'+$logPath+'"')) -PassThru -WindowStyle Hidden
while (!$process.WaitForExit(10000)) {
    if (([DateTime]::UtcNow - $started).TotalSeconds -gt 240) { Stop-Process -Id $process.Id; throw 'Isolated regression timed out.' }
    Write-Output ('Isolated Unity running ' + $Entry)
}
$process.Refresh()
$fresh = @(Get-ChildItem -LiteralPath (Join-Path $validationRoot 'Tools/MotionAdapter/reports') -File | Where-Object { $_.LastWriteTimeUtc -ge $started })
foreach ($report in $fresh) { Copy-Item -LiteralPath $report.FullName -Destination $runRoot }
$unchanged = @($manifest | Where-Object {
    (Get-FileHash -LiteralPath (Join-Path $sourceRoot $_.path)).Hash -ne $_.sha256 -or
    (Get-FileHash -LiteralPath (Join-Path $validationRoot $_.path)).Hash -ne $_.sha256
}).Count -eq 0
$compileErrors = @(Select-String -LiteralPath $logPath -Pattern 'error CS[0-9]+:')
$result = @{ exitCode=$process.ExitCode; sourceUnchanged=$unchanged; compileErrors=$compileErrors.Count; entry=$Entry; elapsedSeconds=([DateTime]::UtcNow-$started).TotalSeconds; reports=@($fresh.Name) }
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'verification.json') -Encoding utf8
$result | ConvertTo-Json -Depth 5
if ($process.ExitCode -ne 0 -or !$unchanged -or $compileErrors.Count -gt 0) { throw ('Validation failed: ' + $runRoot) }
