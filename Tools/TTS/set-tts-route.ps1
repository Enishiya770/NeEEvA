[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('local','spark')][string]$Profile,
    [string]$SparkUrl = 'http://192.168.114.163:9880/tts',
    [string]$SparkReference = '/home/spark_root_001/neeeva-voice/tts/release/refs/41041.wav',
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskScene = Join-Path $taskRoot 'Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity'
$taskRoutesPath = Join-Path $taskRoot 'Server/RuntimeLogs/voice_routes.json'
$taskEditors = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object {
    $_.CommandLine -and $_.CommandLine.Replace('\','/').IndexOf($taskRoot.Replace('\','/'), [StringComparison]::OrdinalIgnoreCase) -ge 0
})
if ($taskEditors.Count -and -not $CheckOnly) { throw 'Close this project in Unity before changing its saved service route.' }
$taskUrl = 'http://127.0.0.1:9880/tts'
$taskReference = 'Model/41041.wav'
$taskHealth = $null
if ($Profile -eq 'spark') {
    $taskEndpoint = $null
    if (-not [Uri]::TryCreate($SparkUrl,[UriKind]::Absolute,[ref]$taskEndpoint) -or
        $taskEndpoint.Scheme -ne 'http' -or $taskEndpoint.UserInfo -or $taskEndpoint.Query -or
        $taskEndpoint.Fragment -or $taskEndpoint.AbsolutePath -ne '/tts' -or $taskEndpoint.Port -ne 9880) { throw 'Spark URL must be an HTTP /tts endpoint on managed port 9880, without credentials or query.' }
    if ($SparkReference -notmatch '^/' -or $SparkReference -match '[\r\n]') { throw 'Spark reference must be an absolute Linux file path.' }
    $taskUrl = $SparkUrl
    $taskReference = $SparkReference
    $taskHealth = $taskEndpoint.GetLeftPart([UriPartial]::Authority) + '/health'
    $taskRequest = [Net.HttpWebRequest]::Create($taskHealth)
    $taskRequest.Proxy=$null; $taskRequest.Timeout=8000
    $taskResponse=$taskRequest.GetResponse()
    try {
        $taskReader=New-Object IO.StreamReader($taskResponse.GetResponseStream())
        try { $taskReady=$taskReader.ReadToEnd() | ConvertFrom-Json } finally {$taskReader.Dispose()}
        if ($taskReady.ok -ne $true -or $taskReady.ready -ne $true -or
            $taskReady.cuda_available -ne $true -or $taskReady.precision -ne 'float32') {
            throw 'Remote TTS must report ready CUDA FP32 inference.'
        }
        $taskManifestPath=Join-Path $taskRoot 'Server/TTS/manifest.json'
        $taskManifest=Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json
        if (-not $taskManifest.release_sha256 -or $taskReady.release_sha256 -ne $taskManifest.release_sha256) {
            throw 'Remote TTS does not match the locally accepted release manifest.'
        }
        if ($taskReady.reference_path -ne $taskReference) { throw 'Reference path differs from the accepted remote service.' }
    } finally {$taskResponse.Dispose()}
}
$taskOriginalScene=[IO.File]::ReadAllText($taskScene)
$taskBlockRegex=New-Object Text.RegularExpressions.Regex('(?ms)^--- !u!114 &1167846587\r?\n.*?(?=^--- |\z)')
$taskBlocks=$taskBlockRegex.Matches($taskOriginalScene)
if ($taskBlocks.Count -ne 1 -or $taskBlocks[0].Value -notmatch 'guid: 20745b356ef75fe45a45c9cbecd25ba6') { throw 'Expected main-scene TTS component was not found exactly once.' }
$taskBlock=$taskBlocks[0].Value
foreach ($taskField in @(@('m_PostURL',$taskUrl),@('m_ReferWavPath',$taskReference))) {
    $taskFieldRegex=New-Object Text.RegularExpressions.Regex(('(?m)^  '+$taskField[0]+':[^\r\n]*'))
    if ($taskFieldRegex.Matches($taskBlock).Count -ne 1) { throw 'Expected TTS property missing or duplicated.' }
    $taskReplacement='  '+$taskField[0]+': '+(ConvertTo-Json -InputObject ([string]$taskField[1]) -Compress)
    $taskBlock=$taskFieldRegex.Replace($taskBlock,[Text.RegularExpressions.MatchEvaluator]{param($m) $taskReplacement})
}
$taskUpdatedScene=$taskBlockRegex.Replace($taskOriginalScene,[Text.RegularExpressions.MatchEvaluator]{param($m) $taskBlock})
$taskOriginalRoutes=if(Test-Path -LiteralPath $taskRoutesPath){[IO.File]::ReadAllText($taskRoutesPath)}else{$null}
$taskRoutes=if($null-ne $taskOriginalRoutes){$taskOriginalRoutes | ConvertFrom-Json}else{[pscustomobject]@{}}
if ($taskRoutes -isnot [pscustomobject]) { throw 'voice_routes.json must contain an object.' }
$taskRoute=[pscustomobject]@{mode=if($Profile-eq 'spark'){'remote'}else{'local'};url=$taskUrl;healthUrl=$taskHealth;referencePath=$taskReference}
$taskRoutes | Add-Member -MemberType NoteProperty -Name tts -Value $taskRoute -Force
$taskUpdatedRoutes=$taskRoutes | ConvertTo-Json -Depth 64
if ($CheckOnly) { Write-Output "Validated TTS route: $Profile -> $taskUrl. No files changed."; return }
$taskBackup=Join-Path $taskRoot ('work/spark-voice-migration-20260926/routes-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $taskBackup -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $taskBackup 'scene.before.unity'),$taskOriginalScene)
if ($null-ne $taskOriginalRoutes) {[IO.File]::WriteAllText((Join-Path $taskBackup 'voice_routes.before.json'),$taskOriginalRoutes)}
try {
    [IO.File]::WriteAllText($taskScene,$taskUpdatedScene,(New-Object Text.UTF8Encoding($false)))
    [IO.File]::WriteAllText($taskRoutesPath,$taskUpdatedRoutes,(New-Object Text.UTF8Encoding($false)))
} catch {
    [IO.File]::WriteAllText($taskScene,$taskOriginalScene,(New-Object Text.UTF8Encoding($false)))
    if ($null-ne $taskOriginalRoutes) {[IO.File]::WriteAllText($taskRoutesPath,$taskOriginalRoutes,(New-Object Text.UTF8Encoding($false)))}
    elseif (Test-Path -LiteralPath $taskRoutesPath -PathType Leaf) { Remove-Item -LiteralPath $taskRoutesPath -Force }
    throw
}
Write-Output "TTS route applied: $Profile -> $taskUrl"
Write-Output "Route backup: $taskBackup"
Write-Output 'Use start_voice.cmd to start the selected voice services; voice-conversion reference is unchanged.'
