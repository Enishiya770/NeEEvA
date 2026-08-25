<# Adds the dedicated 4090 <-> 5090 address without changing Wi-Fi. #>
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdministrator) {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath)
    )
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments
    exit
}

try {
    $adapter = Get-NetAdapter | Where-Object {
        $_.InterfaceDescription -like '*Intel*I226-V*'
    } | Select-Object -First 1
    if (-not $adapter) { throw 'Intel I226-V physical adapter was not found.' }

    $address = Get-NetIPAddress -InterfaceIndex $adapter.ifIndex `
        -AddressFamily IPv4 -IPAddress '192.168.50.1' -ErrorAction SilentlyContinue
    if (-not $address) {
        New-NetIPAddress -InterfaceIndex $adapter.ifIndex `
            -IPAddress '192.168.50.1' -PrefixLength 24 | Out-Null
    }

    Write-Host ''
    Write-Host '4090 direct-link configuration succeeded.' -ForegroundColor Green
    Write-Host "Adapter: $($adapter.InterfaceDescription)"
    Write-Host 'Address: 192.168.50.1/24'
    Write-Host ''
    Write-Host 'Wi-Fi, gateway, and DNS were not changed.'
} catch {
    Write-Host ''
    Write-Host "Configuration failed: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ''
Read-Host 'Press Enter to close'
