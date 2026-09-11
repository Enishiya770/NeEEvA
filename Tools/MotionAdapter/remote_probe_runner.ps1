$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Pilot = 'D:\NeEEvA\motion-adapter-pilot'
$Runtime = 'D:\NeEEvA\llamacpp-b8919-cuda131-sm120'
$Active = Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'qwen_feature_probe.exe' -or
    ($_.Name -eq 'llama-server.exe' -and $_.CommandLine -match 'qwen36')
}
if ($Active) { throw 'Qwen is already loaded or a probe is running; refusing a second model load.' }
$OldReport = Join-Path $Pilot 'report.json'
if (Test-Path -LiteralPath $OldReport) { Remove-Item -LiteralPath $OldReport }
$Model = Join-Path $Runtime 'qwen36.gguf'
$PromptPath = Join-Path $Pilot 'prompts.txt'
$PromptHash = (Get-FileHash -LiteralPath $PromptPath -Algorithm SHA256).Hash
$Provenance = [ordered]@{
    model = 'Qwen3.6-35B-A3B'
    model_sha256 = (Get-FileHash -LiteralPath $Model -Algorithm SHA256).Hash
    llama_dll_sha256 = (Get-FileHash -LiteralPath (Join-Path $Runtime 'llama.dll') -Algorithm SHA256).Hash
    runtime = 'b8919-cuda131-sm120'
    pooling = 'last_input_token_raw'
    template = "Motion description: {text}`nRepresentation:"
    input_context = '512 tokens; reset recurrent/KV state per prompt'
}
$Provenance | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Pilot 'provenance.json') -Encoding utf8
$Arguments = @($Runtime, $Model, (Join-Path $Pilot 'prompts.txt'),
               (Join-Path $Pilot 'features.f32'), (Join-Path $Pilot 'report.json'))
$Process = Start-Process -FilePath (Join-Path $Pilot 'qwen_feature_probe.exe') -ArgumentList $Arguments `
    -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput (Join-Path $Pilot 'probe.out.log') `
    -RedirectStandardError (Join-Path $Pilot 'probe.err.log')
Set-Content -LiteralPath (Join-Path $Pilot 'probe.pid') -Value $Process.Id -Encoding ascii
Write-Output ('probe_pid=' + $Process.Id)
# -Wait keeps SSH alive and ensures PowerShell captures the native exit code.
Set-Content -LiteralPath (Join-Path $Pilot 'exit-code.txt') -Value $Process.ExitCode -Encoding ascii
if ($Process.ExitCode -ne 0) { throw ('Native probe exited with code ' + $Process.ExitCode) }
if ($PromptHash -ne (Get-FileHash -LiteralPath $PromptPath -Algorithm SHA256).Hash) {
    throw 'Prompt input changed during export.'
}
[ordered]@{
    schema = 1
    prompts_sha256 = $PromptHash
    features_sha256 = (Get-FileHash -LiteralPath (Join-Path $Pilot 'features.f32') -Algorithm SHA256).Hash
    report_sha256 = (Get-FileHash -LiteralPath (Join-Path $Pilot 'report.json') -Algorithm SHA256).Hash
    provenance_sha256 = (Get-FileHash -LiteralPath (Join-Path $Pilot 'provenance.json') -Algorithm SHA256).Hash
    probe_exe_sha256 = (Get-FileHash -LiteralPath (Join-Path $Pilot 'qwen_feature_probe.exe') -Algorithm SHA256).Hash
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Pilot 'export-manifest.json') -Encoding utf8
