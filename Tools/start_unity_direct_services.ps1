<#
Launch this project with Unity's native proxy explicitly disabled in the child.
Only the new Editor process receives the environment changes. No registry,
scene, remote service, or existing process is modified.
#>
[CmdletBinding()]
param(
    [string]$UnityPath = '',
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$versionFile = Join-Path $projectRoot 'ProjectSettings\ProjectVersion.txt'
$versionLine = Get-Content -LiteralPath $versionFile | Where-Object { $_ -match '^m_EditorVersion:\s*(\S+)\s*$' } | Select-Object -First 1
if (-not $versionLine -or $versionLine -notmatch '^m_EditorVersion:\s*(\S+)\s*$') {
    throw "Cannot read the required Unity version from $versionFile"
}
$editorVersion = $Matches[1]

function Get-ProjectFromCommandLine([string]$CommandLine) {
    if ($CommandLine -match '(?i)(?:^|\s)(?:"-projectPath"|-projectPath)\s+(?:"([^"]+)"|(\S+))') {
        $value = if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
        try { return [IO.Path]::GetFullPath($value).TrimEnd('\', '/') } catch { return $null }
    }
    return $null
}

# Read command lines only to identify this project's process. Never print them:
# unrelated Editor arguments can contain access tokens or other secrets.
$processInspectionAvailable = $true
try { $editors = @(Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" -ErrorAction Stop) }
catch { $editors = @(); $processInspectionAvailable = $false }
$currentEditors = @($editors | Where-Object {
    $openedProject = Get-ProjectFromCommandLine $_.CommandLine
    $isAssetWorker = $_.CommandLine -match '(?i)(?:^|\s)(?:"-assetImportWorker\d*"|-assetImportWorker\d*)(?=\s|$)' -or
        $_.CommandLine -match '(?i)(?:^|\s)(?:"-name"|-name)\s+(?:"AssetImportWorker\d*"|AssetImportWorker\d*)(?=\s|$)'
    -not $isAssetWorker -and $openedProject -and $openedProject.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase)
})

function Resolve-EditorCandidate([string]$Candidate, [bool]$Explicit = $false) {
    if ([string]::IsNullOrWhiteSpace($Candidate)) { return $null }
    $candidatePath = $Candidate.Trim().Trim('"')
    if (Test-Path -LiteralPath $candidatePath -PathType Container) {
        foreach ($relative in @('Editor\Unity.exe', 'Unity.exe')) {
            $nested = Join-Path $candidatePath $relative
            if (Test-Path -LiteralPath $nested -PathType Leaf) { $candidatePath = $nested; break }
        }
    }
    if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) { return $null }
    $item = Get-Item -LiteralPath $candidatePath
    if ($item.Name -ne 'Unity.exe') { return $null }
    $productVersion = $item.VersionInfo.ProductVersion
    if ($productVersion -notmatch ('^' + [regex]::Escape($editorVersion) + '(?:$|[^a-zA-Z0-9])')) {
        if ($Explicit) { throw "This project requires Unity $editorVersion; selected Editor reports $productVersion." }
        return $null
    }
    return $item.FullName
}

function Find-UnityEditor {
    if (-not [string]::IsNullOrWhiteSpace($UnityPath)) {
        $explicitEditor = Resolve-EditorCandidate $UnityPath $true
        if (-not $explicitEditor) { throw 'The supplied -UnityPath does not contain Unity.exe.' }
        return $explicitEditor
    }

    $candidates = New-Object 'System.Collections.Generic.List[string]'
    foreach ($root in @(
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($key in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
            $entry = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
            if ($entry.DisplayName -notlike '*Unity*') { continue }
            if ($entry.InstallLocation) { $candidates.Add([string]$entry.InstallLocation) }
            if ($entry.DisplayIcon) { $candidates.Add(([string]$entry.DisplayIcon -replace ',\s*-?\d+\s*$', '').Trim('"')) }
        }
    }
    foreach ($root in @(
        "HKCU:\SOFTWARE\Unity Technologies\Installer\Unity $editorVersion",
        "HKLM:\SOFTWARE\Unity Technologies\Installer\Unity $editorVersion",
        "HKLM:\SOFTWARE\WOW6432Node\Unity Technologies\Installer\Unity $editorVersion"
    )) {
        if (Test-Path -LiteralPath $root) {
            $entry = Get-ItemProperty -LiteralPath $root -ErrorAction SilentlyContinue
            foreach ($name in @('Location x64', 'Location', 'InstallLocation')) {
                if ($entry.$name) { $candidates.Add([string]$entry.$name) }
            }
        }
    }
    foreach ($candidate in $candidates) {
        $resolved = Resolve-EditorCandidate $candidate
        if ($resolved) { return $resolved }
    }

    $installRoots = @()
    if ($env:ProgramFiles) { $installRoots += Join-Path $env:ProgramFiles 'Unity\Hub\Editor' }
    if (${env:ProgramFiles(x86)}) { $installRoots += Join-Path ${env:ProgramFiles(x86)} 'Unity\Hub\Editor' }
    foreach ($drive in @(Get-PSDrive -PSProvider FileSystem)) {
        foreach ($relative in @('Unity_Data', 'Unity\Hub\Editor', 'UnityHub\Editor', 'Program Files\Unity\Hub\Editor')) {
            $installRoots += Join-Path $drive.Root $relative
        }
    }
    foreach ($root in $installRoots | Select-Object -Unique) {
        $resolved = Resolve-EditorCandidate (Join-Path $root "$editorVersion\Editor\Unity.exe")
        if ($resolved) { return $resolved }
    }
    foreach ($editor in $editors) {
        $resolved = Resolve-EditorCandidate $editor.ExecutablePath
        if ($resolved) { return $resolved }
    }
    throw "Unity $editorVersion was not found. Run again with -UnityPath 'C:\path\to\Editor\Unity.exe'."
}

$resolvedEditor = Find-UnityEditor
$processInfo = New-Object System.Diagnostics.ProcessStartInfo
$processInfo.FileName = $resolvedEditor
$processInfo.Arguments = '-projectPath "' + $projectRoot + '"'
$processInfo.WorkingDirectory = $projectRoot
$processInfo.UseShellExecute = $false
# An explicitly present empty value selects direct connections for Unity's
# native requests on the tested Windows Editor 2022.3.22f1. Ordinary NO_PROXY
# and UNITY_NOPROXY host lists did not bypass its Windows system proxy.
# Keep this in the child environment block: assigning $env:... = '' under
# Windows PowerShell 5.1 deletes the variable and restores the failing path.
$processInfo.EnvironmentVariables['UNITY_PROXYSERVER'] = ''

Write-Host "[unity] version=$editorVersion editor=$resolvedEditor"
Write-Host "[unity] project=$projectRoot"
Write-Host '[unity] child UNITY_PROXYSERVER is explicitly empty: native Unity requests connect directly.'
Write-Host '[unity] This applies to the new Editor process, including its external requests. System proxy settings and other apps are unchanged.'
if ($currentEditors.Count -gt 0) {
    $message = 'This project is already open in Unity (PID ' + (($currentEditors | ForEach-Object { $_.ProcessId }) -join ', ') + '). Save your work, close that Editor, then run this launcher again. No process was stopped.'
    if ($CheckOnly) { Write-Warning $message } else { throw $message }
}
if (-not $processInspectionAvailable) {
    $message = 'Cannot inspect running Unity processes. Save and close this project, then retry in a normal PowerShell session before launching.'
    if ($CheckOnly) { Write-Warning $message } else { throw $message }
}
if ($CheckOnly) {
    Write-Host '[unity] CheckOnly complete; no Editor was opened and no environment or system settings were changed.'
    return
}
$process = [Diagnostics.Process]::Start($processInfo)
Write-Host "[unity] Started PID $($process.Id). Local service connectivity still requires verification inside Unity."
