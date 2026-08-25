<#
Adds the dedicated 4090 <-> 5090 direct-link address to the physical Marvell
adapter. It does not remove DHCP, gateways, or existing addresses.
#>
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
        $_.InterfaceDescription -like '*Marvell AQtion*'
    } | Select-Object -First 1

    if (-not $adapter) { throw 'Marvell AQtion physical adapter was not found.' }

    $address = Get-NetIPAddress -InterfaceIndex $adapter.ifIndex `
        -AddressFamily IPv4 -IPAddress '192.168.50.2' -ErrorAction SilentlyContinue
    if (-not $address) {
        New-NetIPAddress -InterfaceIndex $adapter.ifIndex `
            -IPAddress '192.168.50.2' -PrefixLength 24 | Out-Null
    }

    $ruleName = 'NeEEvA SSH direct from 4090'
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort 22 -RemoteAddress '192.168.50.1' -Profile Any | Out-Null

    Write-Host ''
    Write-Host '5090 direct-link configuration succeeded.' -ForegroundColor Green
    Write-Host "Adapter: $($adapter.InterfaceDescription)"
    Write-Host 'Address: 192.168.50.2/24'
    Write-Host 'SSH allowed from: 192.168.50.1'
    Write-Host ''
    Write-Host 'You may now reconnect the cable directly between the two computers.'
} catch {
    Write-Host ''
    Write-Host "Configuration failed: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ''
Read-Host 'Press Enter to close'
