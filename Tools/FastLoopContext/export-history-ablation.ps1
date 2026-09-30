param([Parameter(Mandatory=$true)][string]$ProtocolPath, [Parameter(Mandatory=$true)][string]$OutputPath, [switch]$TypedQuotes)
$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath $ProtocolPath -Raw -Encoding UTF8 | ConvertFrom-Json
$cases = @()
$modes = if ($TypedQuotes) { @('typed-quotes') } else { @('unchanged','speech-receipts-only','latest-assistant-only') }
$ids = if ($TypedQuotes) { @('latest-c19','latest-c21','latest-c24','latest-c24-repair') } else { @('latest-c15','latest-c21','latest-c24') }
foreach ($row in $source.cases | Where-Object { $_.id -in $ids }) {
    foreach ($mode in $modes) {
        $copy = $row | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json
        $turns = $copy.context.recentDialogue.turns
        $lastSpokenTurn = ($turns | Where-Object { @($_.completedAssistantSpeech).Count -gt 0 } | Select-Object -Last 1).turn
        foreach ($turn in $turns) {
            if ($mode -eq 'typed-quotes') {
                $quoted = @($turn.completedAssistantSpeech | ForEach-Object {
                    [ordered]@{quotedWords=$_.text; receipt='speech-delivery-only'; provesAction=$false; provesCause=$false}
                })
                $turn.PSObject.Properties.Remove('completedAssistantSpeech')
                $turn | Add-Member -NotePropertyName earlierRoleQuotes -NotePropertyValue $quoted
            } elseif ($mode -eq 'speech-receipts-only') {
                $turn.completedAssistantSpeech = @($turn.completedAssistantSpeech | ForEach-Object {
                    @{ completed = $true; characters = $_.text.Length; completeChunksOnly = $true }
                })
            } elseif ($mode -eq 'latest-assistant-only' -and $turn.turn -ne $lastSpokenTurn) { $turn.completedAssistantSpeech = @() }
        }
        $copy.id = $row.id + '-' + $mode
        $copy.rubric += ' Controlled history ablation; same current user, capabilities, materials, system and schema.'
        $copy.messages[1].content = [ordered]@{context=$copy.context; current_user=$copy.user} | ConvertTo-Json -Depth 64 -Compress
        $cases += $copy
    }
}
$source.cases = $cases
$source.scope += ' History-only controlled ablation, not a deployed prompt or actual online payload.'
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), ($source | ConvertTo-Json -Depth 64), [Text.UTF8Encoding]::new($false))
