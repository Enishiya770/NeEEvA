[CmdletBinding()]
param([ValidateSet('start','run','status','stop')][string]$Action='status',
      [ValidateSet(8080,8082)][int]$Port=8082)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$Stage='D:\NeEEvA\motion-feature-server'
$Runtime='D:\NeEEvA\llamacpp-b8919-cuda131-sm120'
$Exe=Join-Path $Stage 'llama-server.exe'
$Model=Join-Path $Runtime 'qwen36.gguf'
$PidFile=Join-Path $Stage 'server.pid'
$ExpectedModel='071ee2a008ec51372f990d8efbea92ec9dd0137974110ef68fbfde429c8c6dd4'
function Get-FeatureProcess {
    $allQwen=@(Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'qwen_feature_probe.exe' -or ($_.Name -like 'llama*.exe' -and $_.CommandLine -match 'qwen36|Qwen3.6') })
    if($allQwen.Count -gt 1) { throw 'Multiple Qwen processes detected; refusing automatic changes.' }
    $existing=@($allQwen | Where-Object { $_.ExecutablePath -ieq $Exe })
    if($existing.Count -gt 1) { throw 'Multiple feature servers detected; refusing automatic changes.' }
    if($existing.Count -eq 1) {
        if($existing[0].CommandLine -notmatch '--port\s+(8080|8082)(?:\s|$)') { throw 'Unexpected feature server port.' }
        return [pscustomobject]@{process_id=$existing[0].ProcessId;port=[int]$Matches[1]}
    }
    return $null
}
function Assert-NoQwen {
    $active=@(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'qwen_feature_probe.exe' -or
        ($_.Name -like 'llama*.exe' -and $_.CommandLine -match 'qwen36|Qwen3.6')
    })
    if ($active.Count) { throw 'Another Qwen process is loaded; refusing a second model. Existing services are not stopped.' }
    if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { throw "Port $Port already occupied." }
}
if ($Action -eq 'run') {
    $startMutex=New-Object Threading.Mutex($false, 'Global\NeEEvA-Qwen-model-start')
    if(-not $startMutex.WaitOne(30000)) { $startMutex.Dispose(); throw 'Another Qwen startup is in progress.' }
    try {
    Assert-NoQwen
    $actual=(Get-FileHash -LiteralPath $Model -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $ExpectedModel) { throw 'Qwen model differs from adapter training provenance.' }
    $runtimeHashes=[ordered]@{}
    foreach($file in Get-ChildItem -LiteralPath $Stage -File | Where-Object { $_.Extension -in '.exe','.dll' }) {
        $runtimeHashes[$file.Name]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    [ordered]@{schema=1;model_sha256=$actual;model_path=$Model;model_size=(Get-Item -LiteralPath $Model).Length;runtime_files=$runtimeHashes;llama_cpp_revision='dc80c5252a6d49301726e5987cc1fa006b69d93e';chat_context_tokens=196608;chat_slots=3;motion_context_tokens=512;port=$Port;started_at=(Get-Date).ToString('o')} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Stage 'provenance.json') -Encoding utf8
    $env:NEEEVA_MOTION_FEATURES='1'
    $env:NEEEVA_MOTION_MODEL_SHA256=$actual
    $serverArguments=@('-m',$Model,'--mmproj',(Join-Path $Runtime 'mmproj-Q8_0.gguf'),'--host','127.0.0.1','--port',"$Port",'--jinja','--chat-template-file','D:\NeEEvA\services\qwen36_chat_template.jinja','-c','196608','--parallel','3','--slot-prompt-similarity','0.8','-ngl','99','--flash-attn','on')
    Assert-NoQwen
    $proc=Start-Process -FilePath $Exe -WorkingDirectory $Stage -ArgumentList $serverArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $Stage 'server.out.log') -RedirectStandardError (Join-Path $Stage 'server.err.log')
    Set-Content -LiteralPath $PidFile -Value $proc.Id -Encoding ascii
    } catch {
        $_ | Out-String | Set-Content -LiteralPath (Join-Path $Stage 'startup.failure.log') -Encoding utf8
        throw
    } finally { $startMutex.ReleaseMutex(); $startMutex.Dispose() }
    $proc.WaitForExit()
    Set-Content -LiteralPath (Join-Path $Stage 'exit-code.txt') -Value $proc.ExitCode -Encoding ascii
    exit $proc.ExitCode
} elseif ($Action -eq 'start') {
    $existing=Get-FeatureProcess
    if($existing) { [ordered]@{process_id=$existing.process_id;port=$existing.port;status='reused_existing_single_model'} | ConvertTo-Json; exit }
    Assert-NoQwen
    if (-not (Test-Path -LiteralPath $Exe)) { throw 'Stage the isolated server first.' }
    $command='powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File D:\NeEEvA\motion-feature-server\remote_server.ps1 -Action run -Port '+$Port
    $result=Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine=$command;CurrentDirectory=$Stage}
    if ($result.ReturnValue -ne 0) { throw 'Failed to launch isolated server.' }
    [ordered]@{launcher_pid=$result.ProcessId;port=$Port;status='starting_after_model_hash_verification'} | ConvertTo-Json
} elseif ($Action -eq 'status') {
    $existing=Get-FeatureProcess
    if($existing) { $Port=$existing.port }
    if (Test-Path -LiteralPath $PidFile) {
        $serverProcessId=[int](Get-Content -LiteralPath $PidFile -Raw)
        Get-Process -Id $serverProcessId -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,WorkingSet64 | ConvertTo-Json
    }
    try { Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 4 | ConvertTo-Json } catch { Write-Output 'health=unavailable_or_loading' }
    if(Test-Path (Join-Path $Stage 'server.err.log')) { Get-Content (Join-Path $Stage 'server.err.log') -Tail 10 }
} else {
    if(-not(Test-Path -LiteralPath $PidFile)) { Write-Output 'No isolated process recorded.'; exit }
    $serverProcessId=[int](Get-Content -LiteralPath $PidFile -Raw)
    $proc=Get-CimInstance Win32_Process -Filter "ProcessId=$serverProcessId"
    if(-not $proc) { Write-Output 'Isolated process already stopped.'; exit }
    if($proc.ExecutablePath -ine $Exe -or $proc.CommandLine -notmatch '--port\s+(8080|8082)(?:\s|$)') { throw 'Refusing to stop process outside managed feature server path/port.' }
    Stop-Process -Id $serverProcessId
    $stopping=Get-Process -Id $serverProcessId -ErrorAction SilentlyContinue
    if($stopping -and -not $stopping.WaitForExit(5000)) { throw 'Feature server has not exited yet; retry start after it exits.' }
    Write-Output "Stopped isolated feature server PID $serverProcessId."
}
