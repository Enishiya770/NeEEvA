# Pure controller-logic tests: imported functions use mocked process snapshots.
# This script never starts, stops or connects to a model server.
$ErrorActionPreference='Stop'
$Root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
function Read-Function([string]$Path,[string]$Name) {
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$tokens,[ref]$errors)
    if($errors) { throw 'PowerShell syntax error' }
    $function=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name},$true)
    if(-not $function) { throw "Missing $Name" }
    return $function.Extent.Text
}
function Assert-Throws([scriptblock]$Code,[string]$Pattern) {
    try { & $Code; throw 'Expected rejection did not occur' } catch {
        if($_.Exception.Message -notmatch $Pattern) { throw }
    }
}
$controller=Join-Path $Root 'Remote\neeeva_remote_llm.ps1'
# Root above resolves to Tools, matching the checked-in remote controller.
Invoke-Expression (Read-Function $controller 'Get-LlmInfo')
Invoke-Expression (Read-Function $controller 'Start-FeatureService')
$FeatureExe='D:\NeEEvA\motion-feature-server\llama-server.exe'
$ServerExe='D:\NeEEvA\llamacpp-b8919-cuda131-sm120\llama-server.exe'
$FeatureLauncher='must-not-run.ps1'
$script:Processes=@()
function Get-CimInstance { return $script:Processes }
$passes=0
if((Get-LlmInfo).mode -ne 'stopped') { throw 'Empty host was not detected.' }; $passes++
$feature=[pscustomobject]@{Name='llama-server.exe';ExecutablePath=$FeatureExe;ProcessId=42;CommandLine='llama-server.exe -m qwen36.gguf --port 8082'}
$legacy=[pscustomobject]@{Name='llama-server.exe';ExecutablePath=$ServerExe;ProcessId=43;CommandLine='llama-server.exe -m qwen36.gguf --port 8080'}
$script:Processes=@($feature)
$info=Get-LlmInfo
if($info.mode -ne 'feature' -or $info.port -ne 8082 -or $info.process_id -ne 42) { throw 'Existing feature endpoint was not preserved.' }; $passes++
if((Start-FeatureService) -notmatch 'reusing') { throw 'Feature mode did not reuse the running instance.' }; $passes++
$script:Processes=@($legacy)
Assert-Throws { Start-FeatureService } 'legacy Qwen service is already running'; $passes++
$script:Processes=@($feature,$legacy)
Assert-Throws { Get-LlmInfo } 'Multiple Qwen processes'; $passes++
$script:Processes=@([pscustomobject]@{Name='qwen_feature_probe.exe';ExecutablePath='D:\elsewhere\qwen_feature_probe.exe';ProcessId=9;CommandLine='probe'})
Assert-Throws { Get-LlmInfo } 'unmanaged Qwen'; $passes++
[ordered]@{passed=$true;checks=$passes;scope='Mocked process snapshots; no process mutations';legacy_feature_switch='refused while legacy active';duplicate_model='refused';existing_feature='reused'} | ConvertTo-Json
