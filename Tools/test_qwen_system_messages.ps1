param([switch]$Live)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Get-Content (Join-Path $root 'Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.cs') -Raw
$begin = $source.IndexOf('    internal static string NormalizeLeadingSystemMessages')
$end = $source.IndexOf('    void Awake()', $begin)
$method = $source.Substring($begin, $end-$begin).Replace('internal static', 'public static')
Add-Type -TypeDefinition ('using System; using System.Linq; using System.Text; public class QwenWireTest {'+$method+'}') -CompilerOptions '/nowarn:1701' -ReferencedAssemblies @([Newtonsoft.Json.Linq.JObject].Assembly.Location, 'System.Linq', 'System.Linq.Expressions', 'System.Runtime', 'System.Collections', 'System.ComponentModel.TypeConverter', 'System.ComponentModel.Primitives', 'System.ObjectModel')
$body = '{"model":"qwen38-27b-unsloth-nvfp4","stream":false,"max_tokens":16,"reasoning_effort":"none","chat_template_kwargs":{"enable_thinking":false,"reasoning_effort":"none"},"messages":[{"role":"system","content":"Answer briefly."},{"role":"user","content":"Say A."},{"role":"assistant","content":"A"},{"role":"system","content":"Current test context."},{"role":"user","content":"Say B."},{"role":"system","content":"The test action failed."}]}'
$wire = [QwenWireTest]::NormalizeLeadingSystemMessages($body)
$o = $wire | ConvertFrom-Json
if (($o.messages.role -join ',') -ne 'system,user,assistant,user') { throw 'History order changed' }
if ($o.messages[0].content -notmatch 'Answer briefly.[\s\S]*Current test context.[\s\S]*The test action failed.') { throw 'System context lost' }
if ([QwenWireTest]::NormalizeLeadingSystemMessages($wire) -ne $wire) { throw 'Not idempotent' }
$imageBody = '{"messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"test-image"}},{"type":"text","text":"C"}]},{"role":"system","content":"context"}]}'
$image = [QwenWireTest]::NormalizeLeadingSystemMessages($imageBody) | ConvertFrom-Json
if ($image.messages[1].content[0].image_url.url -ne 'test-image' -or $image.messages[1].content[1].text -ne 'C') { throw 'Image or text changed' }
foreach ($unchanged in @('{"messages":[]}', '{"messages":[{"role":"user","content":"A"}]}', '{"messages":[{"role":"system","content":"S"},{"role":"user","content":"A"}]}')) {
    if ([QwenWireTest]::NormalizeLeadingSystemMessages($unchanged) -ne $unchanged) { throw 'Already compatible request changed' }
}
Write-Output 'PASS: production C# compiled; context, history, images, idempotence and compatible requests verified.'
if ($Live) {
    # Only synthetic text is sent; scene access supplies credentials and endpoint.
    $scene = Get-Content (Join-Path $root 'Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity') -Raw
    $key = [regex]::Match($scene, '(?m)^  api_key: (.+)$').Groups[1].Value.Trim()
    $url = [regex]::Match($scene, '(?m)^  m_OpenAICompatibleUrl: (.+)$').Groups[1].Value.Trim()
    $model = [regex]::Match($scene, '(?m)^  m_OpenAICompatibleModelName: (.+)$').Groups[1].Value.Trim()
    $request = $wire | ConvertFrom-Json
    $request.model = $model
    $wire = $request | ConvertTo-Json -Depth 20 -Compress
    $reply = Invoke-RestMethod -Uri $url -Method Post -Headers @{Authorization=('Bearer '+$key)} -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($wire)) -TimeoutSec 45
    if ([string]::IsNullOrWhiteSpace($reply.choices[0].message.content)) { throw 'No content returned' }
    Write-Output ('PASS: normalized multi-turn request returned content: '+$reply.choices[0].message.content)
}
