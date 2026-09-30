<# Start the already-deployed Spark services; never falls back to PC inference. #>
[CmdletBinding()]
param(
    [ValidateSet('start', 'status')][string]$Action = 'start',
    [ValidatePattern('^[A-Za-z0-9.-]+$')][string]$LlmHost = '192.168.114.175',
    [ValidatePattern('^[A-Za-z0-9.-]+$')][string]$ServiceHost = '192.168.114.163',
    [string]$IdentityFile = (Join-Path $env:USERPROFILE '.ssh/id_ed25519_dgx_spark'),
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$shell = Join-Path $PSHOME 'powershell.exe'
if (-not (Test-Path -LiteralPath $shell)) { $shell = (Get-Command pwsh -ErrorAction Stop).Source }
$remote = 'spark_root_001@' + $ServiceHost
$commands = @(
    @{ Name = 'LLM + ARDY + motion features'; Script = 'neeeva_spark_ardy_stack.ps1'; Arguments = @(
        '-Action', $Action, '-ArdySshHost', $ServiceHost, '-LlmSshHost', $LlmHost,
        '-LlmUrl', ('http://{0}:8888' -f $LlmHost), '-SshKeyPath', $IdentityFile, '-SkipDeploy',
        '-AllowIndependentFeatureModel', '-AllowUnverifiedAdapter') },
    @{ Name = 'TTS'; Script = 'TTS/manage-spark-tts.ps1'; Arguments = @('-Action', $Action, '-SshTarget', $remote, '-IdentityFile', $IdentityFile) },
    @{ Name = 'ASR'; Script = 'ASR/manage-spark-asr.ps1'; Arguments = @('-Action', $Action, '-SshTarget', $remote, '-IdentityFile', $IdentityFile) },
    @{ Name = 'Voice conversion'; Script = 'VC/manage-spark-vc.ps1'; Arguments = @('-Action', $Action, '-SshTarget', $remote, '-IdentityFile', $IdentityFile) },
    @{ Name = 'Personal voice learning'; Script = 'SingingSegmentation/manage-spark-learning.ps1'; Arguments = @('-Action', $Action, '-SshTarget', $remote, '-IdentityFile', $IdentityFile) }
)
foreach ($entry in $commands) {
    $path = Join-Path $PSScriptRoot $entry.Script
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing service manager: $path" }
}
if ($CheckOnly) {
    foreach ($entry in $commands) { Write-Output ('[check] {0}: {1}' -f $entry.Name, $entry.Script) }
    Write-Output "LLM=$LlmHost; other services=$ServiceHost. No service or route was changed."
    return
}
if (-not (Test-Path -LiteralPath $IdentityFile -PathType Leaf)) { throw "SSH identity not found: $IdentityFile" }
foreach ($entry in $commands) {
    Write-Host ('[spark] {0}: {1}' -f $Action, $entry.Name)
    $argsForService = $entry.Arguments
    & $shell -NoLogo -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $entry.Script) @argsForService
    if ($LASTEXITCODE -ne 0) { throw ('{0} failed (exit {1}). Check its deployment and SSH configuration.' -f $entry.Name, $LASTEXITCODE) }
}
Write-Host '[spark] Completed. Unity capture, rendering and playback run on the PC.'
