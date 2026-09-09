[CmdletBinding()]
param(
    [string]$Endpoint = 'http://127.0.0.1:8080'
)

$ErrorActionPreference = 'Stop'
$markers = @(
    'BASE_MARKER_A',
    'OLD_USER_MARKER_B',
    'OLD_REPLY_MARKER_C',
    'SKILL_MARKER_D',
    'MEMORY_MARKER_E',
    'RECOVERY_MARKER_F',
    'CURRENT_USER_MARKER_G'
)
$payload = @{
    messages = @(
        @{ role = 'system'; content = $markers[0] }
        @{ role = 'user'; content = $markers[1] }
        @{ role = 'assistant'; content = $markers[2] }
        @{ role = 'system'; content = $markers[3] }
        @{ role = 'developer'; content = $markers[4] }
        @{ role = 'system'; content = $markers[5] }
        @{ role = 'user'; content = $markers[6] }
    )
    add_generation_prompt = $true
    chat_template_kwargs = @{ enable_thinking = $false }
} | ConvertTo-Json -Depth 8

$response = Invoke-RestMethod `
    -Uri ($Endpoint.TrimEnd('/') + '/apply-template') `
    -Method Post `
    -ContentType 'application/json' `
    -Body $payload `
    -TimeoutSec 10
$prompt = [string]$response.prompt
foreach ($marker in $markers) {
    if (-not $prompt.Contains($marker)) {
        throw "Rendered chat prompt dropped marker: $marker"
    }
}
for ($index = 1; $index -lt $markers.Count; $index++) {
    if ($prompt.IndexOf($markers[$index], [StringComparison]::Ordinal) -le
        $prompt.IndexOf($markers[$index - 1], [StringComparison]::Ordinal)) {
        throw "Rendered chat prompt changed message order around $($markers[$index])"
    }
}

Write-Output "PASS: all $($markers.Count) role/order markers reached the rendered prompt."
