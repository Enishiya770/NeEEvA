<#
One-click launcher for the selected local or Spark voice services.
Server/RuntimeLogs/voice_routes.json is written by the route switchers;
without it, all three services keep their existing local configuration.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$voiceProjectRoot = Split-Path -Parent $PSScriptRoot

# Match the existing direct-API launcher's portable GPT-SoVITS installation.
# An explicit override takes precedence and is never silently replaced.
if (-not $env:NEEEVA_GPT_SOVITS_ROOT) {
    foreach ($candidate in @(
        (Join-Path $voiceProjectRoot 'GPT-SoVITS'),
        'E:\so-vits\GPT-SoVITS-v2pro-20250604'
    )) {
        $missing = @('runtime\python.exe', 'api_v2.py', 'GPT_SoVITS\configs\tts_infer.yaml') |
            Where-Object { -not (Test-Path -LiteralPath (Join-Path $candidate $_) -PathType Leaf) }
        if (-not $missing) {
            $env:NEEEVA_GPT_SOVITS_ROOT = $candidate
            break
        }
    }
}

try {
    $voiceRoutesPath = Join-Path $voiceProjectRoot 'Server/RuntimeLogs/voice_routes.json'
    $voiceRoutes = if (Test-Path -LiteralPath $voiceRoutesPath) {
        Get-Content -LiteralPath $voiceRoutesPath -Raw | ConvertFrom-Json
    } else { [pscustomobject]@{} }
    if ($voiceRoutes -isnot [pscustomobject]) { throw 'voice_routes.json must contain an object.' }
    $voiceLocal = @()
    $voiceRemote = @()
    foreach ($voiceKey in @('tts','asr','vc')) {
        $voiceRoute = $voiceRoutes.$voiceKey
        if ($null -eq $voiceRoute -or $voiceRoute.mode -eq 'local') {
            $voiceLocal += $voiceKey
            continue
        }
        if ($voiceRoute.mode -ne 'remote') { throw "Unknown route mode for $voiceKey." }
        $voiceEndpoint = $null
        $voiceHealthEndpoint = $null
        if (-not [Uri]::TryCreate([string]$voiceRoute.url,[UriKind]::Absolute,[ref]$voiceEndpoint) -or
            $voiceEndpoint.Scheme -ne 'http' -or $voiceEndpoint.UserInfo -or $voiceEndpoint.Query -or $voiceEndpoint.Fragment -or
            -not [Uri]::TryCreate([string]$voiceRoute.healthUrl,[UriKind]::Absolute,[ref]$voiceHealthEndpoint) -or
            $voiceHealthEndpoint.GetLeftPart([UriPartial]::Authority) -ne $voiceEndpoint.GetLeftPart([UriPartial]::Authority) -or
            $voiceHealthEndpoint.AbsolutePath -ne '/health' -or $voiceHealthEndpoint.Query -or $voiceHealthEndpoint.Fragment) {
            throw "Invalid remote endpoint for $voiceKey. Use its route switcher to repair the configuration."
        }
        $voiceManager = Join-Path $PSScriptRoot ("{0}/manage-spark-{1}.ps1" -f $voiceKey.ToUpperInvariant(),$voiceKey)
        if (-not (Test-Path -LiteralPath $voiceManager -PathType Leaf)) { throw "No Spark service manager is installed for $voiceKey." }
        $voiceRemote += [pscustomobject]@{ Key=$voiceKey; Manager=$voiceManager; HostName=$voiceEndpoint.Host; Url=$voiceRoute.url; Health=$voiceRoute.healthUrl }
    }

    foreach ($voiceService in $voiceRemote) {
        & $voiceService.Manager -Action start -SshTarget ('spark_root_001@'+$voiceService.HostName)
        $voiceRequest = [Net.HttpWebRequest]::Create($voiceService.Health)
        $voiceRequest.Proxy = $null
        $voiceRequest.Timeout = 8000
        $voiceResponse = $voiceRequest.GetResponse()
        try {
            $voiceReader = New-Object IO.StreamReader($voiceResponse.GetResponseStream())
            try { $voiceHealth = $voiceReader.ReadToEnd() | ConvertFrom-Json } finally { $voiceReader.Dispose() }
            if (($voiceHealth.PSObject.Properties['ready'] -and -not $voiceHealth.ready) -or
                ($voiceHealth.PSObject.Properties['ok'] -and -not $voiceHealth.ok)) { throw "Remote $($voiceService.Key) is not ready." }
        } finally { $voiceResponse.Dispose() }
        Write-Host ("[REMOTE] {0}: {1}" -f $voiceService.Key,$voiceService.Url) -ForegroundColor Green
    }

    # An empty -Only means all services in the controller, so skip it entirely
    # when every selected service is remote.
    if ($voiceLocal.Count -gt 0) {
        . (Join-Path $PSScriptRoot 'neeeva_services.ps1') start -Only $voiceLocal -NoWait

        # Also wait for existing listeners that are still loading their models.
        $voicePending = @($Selected | Where-Object { -not (Test-ServiceHealthy -Svc $Services[$_]) })
        Wait-Ready -Keys $voicePending
        Show-Status

        $voiceFailed = @($Selected | Where-Object { -not (Test-ServiceHealthy -Svc $Services[$_]) })
        if ($voiceFailed.Count -gt 0) {
            foreach ($key in $voiceFailed) {
                Write-Host ("[FAILED] {0}: check {1}\{2}.err.log" -f $Services[$key].Name, $LogDir, $key) -ForegroundColor Red
            }
            exit 1
        }
    }
    Write-Host ('Voice services ready. Local: {0}; Spark: {1}.' -f ($voiceLocal -join ', '),(($voiceRemote | ForEach-Object { $_.Key }) -join ', ')) -ForegroundColor Green
    exit 0
} catch {
    Write-Host ("[FAILED] {0}" -f $_.Exception.Message) -ForegroundColor Red
    exit 1
}
