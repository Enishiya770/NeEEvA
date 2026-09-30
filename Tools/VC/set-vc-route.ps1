[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('local','spark')][string]$Profile,
    [string]$SparkUrl='http://192.168.114.163:9882/convert',
    [string]$SparkReference='/home/spark_root_001/neeeva-voice/vc/release/refs/41041.wav',
    [switch]$CheckOnly
)
$ErrorActionPreference='Stop'
$vcRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$vcScene=Join-Path $vcRoot 'Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity'
$vcRoutesPath=Join-Path $vcRoot 'Server/RuntimeLogs/voice_routes.json'
function Assert-VcEditorClosed {
    $vcEditors=@(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object {
        -not $_.CommandLine -or $_.CommandLine -notmatch '(?i)(?:^|\s)-projectPath(?:\s|$)' -or
        $_.CommandLine.Replace('\','/').IndexOf($vcRoot.Replace('\','/'),[StringComparison]::OrdinalIgnoreCase)-ge 0
    })
    if($vcEditors.Count){throw 'Close this project in Unity before changing its saved voice-conversion route.'}
}
if(-not $CheckOnly){Assert-VcEditorClosed}
$vcUrl='http://127.0.0.1:9882/convert'
$vcReference='Model/41041.wav'
$vcAutostart='1'
if($Profile -eq 'spark') {
    $vcEndpoint=$null
    if(-not [Uri]::TryCreate($SparkUrl,[UriKind]::Absolute,[ref]$vcEndpoint) -or
        $vcEndpoint.Scheme -ne 'http' -or $vcEndpoint.UserInfo -or $vcEndpoint.Query -or $vcEndpoint.Fragment -or
        $vcEndpoint.Port -ne 9882 -or $vcEndpoint.AbsolutePath -ne '/convert') {
        throw 'Expected an HTTP /convert endpoint on managed port 9882, without credentials or query.'
    }
    if($SparkReference -notmatch '^/' -or $SparkReference -match '[\r\n]'){throw 'Reference must be an absolute Linux file path.'}
    $vcUrl=$SparkUrl; $vcReference=$SparkReference; $vcAutostart='0'
}
$vcHealthUrl=([Uri]$vcUrl).GetLeftPart([UriPartial]::Authority)+'/health'
if($Profile -eq 'spark') {
    $vcRequest=[Net.HttpWebRequest]::Create($vcHealthUrl)
    $vcRequest.Proxy=$null; $vcRequest.Timeout=8000; $vcRequest.ReadWriteTimeout=8000
    $vcResponse=$vcRequest.GetResponse()
    try {
        $vcReader=New-Object IO.StreamReader($vcResponse.GetResponseStream())
        try {$vcHealth=$vcReader.ReadToEnd() | ConvertFrom-Json} finally {$vcReader.Dispose()}
        if($vcHealth.ok -ne $true -or $vcHealth.backend -ne 'rvc-character-v2' -or $vcHealth.worker_status -ne 'ready') {
            throw 'Voice-conversion service must report its warmed character RVC backend ready.'
        }
        foreach($vcModel in @('rvc','hubert','rmvpe')) {
            if($vcHealth.models.$vcModel.device -notmatch '^cuda(?::[0-9]+)?$') {
                throw "Expected actual CUDA model parameters for $vcModel."
            }
        }
        $vcManifest=Get-Content -LiteralPath (Join-Path $vcRoot 'Server/VC/manifest.json') -Raw | ConvertFrom-Json
        if(-not $vcManifest.release_sha256 -or $vcHealth.release_sha256 -ne $vcManifest.release_sha256 -or
            $vcHealth.reference_path -ne $vcReference) {throw 'Remote VC release or reference differs from the accepted deployment.'}
    } finally {$vcResponse.Dispose()}
}
$vcOriginalBytes=[IO.File]::ReadAllBytes($vcScene)
$vcOriginal=[IO.File]::ReadAllText($vcScene)
$vcUpdated=$vcOriginal
$vcEdits=@(
    @{Id='1435085656';Guid='1b9e8081040c9c343a1dabf9837b3ee0';Fields=@(
        @{Name='m_HumSVCURL';Value=(ConvertTo-Json -InputObject $vcUrl -Compress)},
        @{Name='m_AutoStartHumSVC';Value=$vcAutostart})},
    @{Id='1167846587';Guid='20745b356ef75fe45a45c9cbecd25ba6';Fields=@(
        @{Name='m_VoiceConversionReferencePath';Value=(ConvertTo-Json -InputObject $vcReference -Compress)})}
)
foreach($vcEdit in $vcEdits) {
    $vcRegex=New-Object Text.RegularExpressions.Regex(('(?ms)^--- !u!114 &'+$vcEdit.Id+'\r?\n.*?(?=^--- |\z)'))
    $vcMatches=$vcRegex.Matches($vcUpdated)
    if($vcMatches.Count-ne 1 -or $vcMatches[0].Value -notmatch ('guid: '+$vcEdit.Guid)){throw 'Expected scene component was not found exactly once.'}
    $vcBlock=$vcMatches[0].Value
    foreach($vcField in $vcEdit.Fields) {
        $vcFieldRegex=New-Object Text.RegularExpressions.Regex(('(?m)^  '+$vcField.Name+':[^\r\n]*'))
        if($vcFieldRegex.Matches($vcBlock).Count-ne 1){throw 'Expected voice-conversion property missing or duplicated.'}
        $vcReplacement='  '+$vcField.Name+': '+$vcField.Value
        $vcBlock=$vcFieldRegex.Replace($vcBlock,[Text.RegularExpressions.MatchEvaluator]{param($m) $vcReplacement})
    }
    $vcUpdated=$vcRegex.Replace($vcUpdated,[Text.RegularExpressions.MatchEvaluator]{param($m) $vcBlock})
}
$vcRoutesExist=Test-Path -LiteralPath $vcRoutesPath -PathType Leaf
$vcRoutesBytes=if($vcRoutesExist){[IO.File]::ReadAllBytes($vcRoutesPath)}else{$null}
$vcRoutes=if($vcRoutesExist){[IO.File]::ReadAllText($vcRoutesPath) | ConvertFrom-Json}else{[pscustomobject]@{}}
if($vcRoutes -isnot [pscustomobject]){throw 'voice_routes.json must contain an object.'}
$vcRoute=[pscustomobject]@{mode=if($Profile-eq 'spark'){'remote'}else{'local'};url=$vcUrl;healthUrl=$vcHealthUrl;referencePath=$vcReference}
$vcRoutes | Add-Member -MemberType NoteProperty -Name vc -Value $vcRoute -Force
$vcUpdatedRoutes=$vcRoutes | ConvertTo-Json -Depth 64
if($CheckOnly){Write-Output "Validated VC route: $Profile -> $vcUrl. No files changed.";return}
Assert-VcEditorClosed
if([IO.File]::ReadAllText($vcScene)-cne $vcOriginal){throw 'Scene changed during route preparation.'}
$vcRoutesExistNow=Test-Path -LiteralPath $vcRoutesPath -PathType Leaf
if($vcRoutesExist-ne $vcRoutesExistNow -or ($vcRoutesExistNow -and
    [Convert]::ToBase64String([IO.File]::ReadAllBytes($vcRoutesPath))-cne [Convert]::ToBase64String($vcRoutesBytes))) {
    throw 'Voice routes changed during preparation; refusing to replace another service route.'
}
$vcBackup=Join-Path $vcRoot ('work/spark-voice-migration-20260926/vc-routes-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $vcBackup -Force | Out-Null
[IO.File]::WriteAllBytes((Join-Path $vcBackup 'scene.before.unity'),$vcOriginalBytes)
if($vcRoutesExist){[IO.File]::WriteAllBytes((Join-Path $vcBackup 'voice_routes.before.json'),$vcRoutesBytes)}
$vcHasBom=$vcOriginalBytes.Length-ge 3 -and $vcOriginalBytes[0]-eq 239 -and $vcOriginalBytes[1]-eq 187 -and $vcOriginalBytes[2]-eq 191
try {
    [IO.File]::WriteAllText($vcScene,$vcUpdated,(New-Object Text.UTF8Encoding($vcHasBom)))
    [IO.File]::WriteAllText($vcRoutesPath,$vcUpdatedRoutes,(New-Object Text.UTF8Encoding($false)))
} catch {
    [IO.File]::WriteAllBytes($vcScene,$vcOriginalBytes)
    if($vcRoutesExist){[IO.File]::WriteAllBytes($vcRoutesPath,$vcRoutesBytes)}
    elseif(Test-Path -LiteralPath $vcRoutesPath -PathType Leaf){Remove-Item -LiteralPath $vcRoutesPath -Force}
    throw
}
Write-Output "VC route applied: $Profile -> $vcUrl"
Write-Output "Route backup: $vcBackup"
Write-Output 'TTS, ASR and disabled SoulX settings were preserved. Use start_voice.cmd for the selected services.'
