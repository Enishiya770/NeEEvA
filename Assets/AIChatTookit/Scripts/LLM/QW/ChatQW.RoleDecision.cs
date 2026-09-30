using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using Stopwatch = System.Diagnostics.Stopwatch;

public partial class ChatQW
{
    [Tooltip("Compact RoleDecision JSON route/speech fast lane. Disable to return to the existing XML first-reply route or full formal path.")]
    [SerializeField] private bool m_EnableRoleDecisionFastLane = true;
    [Tooltip("Legacy formal semantic JSON output. Keep off while the fast lane is being validated.")]
    [SerializeField] private bool m_EnableSemanticRoleDecision = false;

    // A private generation/request pair keeps the structured fast lane
    // independent from the formal streaming request and from the ordinary
    // ephemeral draft.  Late responses are discarded before any callback.
    private UnityWebRequest m_RoleDecisionRequest;
    private int m_RoleDecisionGeneration;

    public override bool SupportsRoleDecisionMessages => m_EnableRoleDecisionFastLane;

    /// <summary>
    /// Runs the compact RoleDecision JSON lane on the auxiliary slot.  A true
    /// callback with status <c>tools</c> is a planner handoff proposal and has
    /// an empty legacy payload; only an ordinary <c>ok</c>/<c>ok-silent</c>
    /// result contains compiler output suitable for the normal speech parser.
    /// The caller still owns generation/epoch/consumption and action authority.
    /// </summary>
    public override void PostRoleDecisionMsg(string prompt,
        Action<bool, string, string> callback)
        => PostRoleDecisionMsg(prompt, null, callback, null);

    public override void PostRoleDecisionMsg(string prompt, string imageDataUrl,
        Action<bool, string, string> callback, ConversationTrace trace)
    {
        AbortPrewarmIfRunning();
        CancelRoleDecisionMsg();
        int generation = m_RoleDecisionGeneration;
        trace = trace ?? TraceContext?.Child("llm-role-decision");
        string json = BuildRoleDecisionRequestJson(prompt ?? "", imageDataUrl);
        StartCoroutine(RequestRoleDecisionPayload(json, generation, callback, trace));
    }

    public override void CancelRoleDecisionMsg()
    {
        m_RoleDecisionGeneration++;
        AcceptedRoleDecision = null;
        if (m_RoleDecisionRequest != null)
        {
            try
            {
                if (!m_RoleDecisionRequest.isDone) m_RoleDecisionRequest.Abort();
            }
            catch (Exception) { }
            m_RoleDecisionRequest = null;
        }
    }

    /// <summary>
    /// Build a deliberately small auxiliary request.  The caller supplies the
    /// compact current-turn facts in <paramref name="prompt"/>; no formal
    /// history, skill transcript or tool result is copied here.  This keeps the
    /// fast lane provider-neutral and prevents it from inheriting the 40k-token
    /// formal context that motivated this path.
    /// </summary>
    private string BuildRoleDecisionRequestJson(string prompt, string imageDataUrl)
    {
        var sb = new StringBuilder(Mathf.Max(1024, (prompt ?? "").Length + 2048));
        string persona = BuildRoleDecisionPersona();
        if (string.IsNullOrWhiteSpace(persona)) persona = "（未提供额外角色设定）";
        if (persona.Length > 1400) persona = persona.Substring(0, 1400) + "…";
        string roleSystem =
            BuildRoleDecisionBehaviorRules() + "\n[角色设定短摘要；只用于保持语气和语言，不包含执行授权]\n" + FormalPromptProfile.RolePreferences(persona) +
            "\n[默认回复语言]\n" + (lan ?? "") +
            "\n" + BuildRoleDecisionLanguageRules() +
            "\n[本轮输出：RoleDecision JSON 协议]\n" +
            DecisionCompiler.FastLaneContract +
            "\n[快速首回复补充约束]\n" + DecisionCompiler.FastLaneThoughtGuidance;
        sb.Append("{\"model\":");
        AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false");
        sb.Append(",\"temperature\":0,\"max_tokens\":256");
        sb.Append(",\"response_format\":").Append(DecisionCompiler.FastLaneResponseFormat().ToString(Formatting.None));
        sb.Append(",\"messages\":[");
        AppendMessage(sb, new SendData("system", roleSystem));
        sb.Append(',');
        AppendMessage(sb, new SendData("user", prompt) { imageDataUrl = imageDataUrl });
        sb.Append(']');
        AppendReasoningControls(sb, false);
        AppendSlot(sb, k_SlotAuxiliary);
        sb.Append('}');
        return sb.ToString();
    }

    private string BuildRoleDecisionPersona()
    {
        if (m_PromptFiles != null)
            foreach (var asset in m_PromptFiles)
                if (asset != null && asset.name == "persona") return asset.text;
        // A custom character layout is preference data, never the complete
        // general tool system or the missing persona's protocol fallback.
        return !HasPromptFiles ? m_Prompt ?? "" : "";
    }

    private string BuildRoleDecisionBehaviorRules()
    {
        var role = new StringBuilder();
        if (m_PromptFiles != null)
            foreach (var asset in m_PromptFiles)
                if (asset != null && asset.name != "persona" && asset.name != "language" && !string.IsNullOrWhiteSpace(asset.text))
                    role.Append("\n[").Append(asset.name).Append("]\n").Append(asset.text);
        // Older callers may supply their only character description as a plain
        // system message. Preserve that prose as preferences, while never
        // resurrecting a stored XML/tool manual after assets were removed.
        if (role.Length == 0 && !HasPromptFiles && string.IsNullOrWhiteSpace(m_Prompt))
        {
            var plainSystem = m_DataList.Find(message => message != null && message.role == "system");
            if (!string.IsNullOrWhiteSpace(plainSystem?.content) && plainSystem.content.IndexOf('<') < 0)
                role.Append("[旧调用方提供的纯文字角色偏好]\n").Append(plainSystem.content);
        }
        return FormalPromptProfile.RolePreferences(role.Length > 0 ? role.ToString() : FormalPromptProfile.CommonRules);
    }

    private string BuildRoleDecisionLanguageRules()
    {
        if (m_PromptFiles != null)
            foreach (var asset in m_PromptFiles)
                if (asset != null && asset.name == "language")
                    return FormalPromptProfile.Fingerprint(asset.text) == FormalPromptProfile.LanguageSha256
                        ? FormalPromptProfile.LanguageRules : FormalPromptProfile.RolePreferences(asset.text);
        return FormalPromptProfile.LanguageRules;
    }

    private IEnumerator RequestRoleDecisionPayload(string json, int generation,
        Action<bool, string, string> callback, ConversationTrace trace)
    {
        if (generation != m_RoleDecisionGeneration) yield break;
        var clock = Stopwatch.StartNew();
        trace?.Mark("role_decision.send", new JObject {
            ["generation"] = generation,
            ["backend"] = m_Backend.ToString(),
            ["slot"] = m_Backend == BackendType.Local ? (int?)k_SlotAuxiliary : null,
            ["maxTokens"] = 256,
            ["requestBytes"] = Encoding.UTF8.GetByteCount(json),
            ["imageCharacters"] = ImageCharactersInRequest(json),
            ["stream"] = false
        });
        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            m_RoleDecisionRequest = request;
            RaiseRequestDiagnostic(json);
            if (generation != m_RoleDecisionGeneration)
            {
                if (ReferenceEquals(m_RoleDecisionRequest, request)) m_RoleDecisionRequest = null;
                trace?.Mark("role_decision.superseded", new JObject {
                    ["generation"] = generation, ["reason"] = "diagnostic-cancelled"
                });
                yield break;
            }
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = 20;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization",
                string.Format("Bearer {0}", string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            yield return request.SendWebRequest();

            bool current = generation == m_RoleDecisionGeneration;
            trace?.Mark(current ? "role_decision.transport_complete" : "role_decision.superseded",
                new JObject {
                    ["generation"] = generation,
                    ["httpStatus"] = request.responseCode,
                    ["transportResult"] = request.result.ToString(),
                    ["elapsedSeconds"] = clock.Elapsed.TotalSeconds
                });
            if (!current) yield break;
            if (ReferenceEquals(m_RoleDecisionRequest, request)) m_RoleDecisionRequest = null;

            string responseBody = request.downloadHandler?.text ?? "";
            var performance = new CompletionPerformance();
            performance.Observe(responseBody);
            trace?.Mark("role_decision.provider_timing", performance.Snapshot());
            string raw = "", finishReason = "", compiled = "", status = "http_failure", rejectReason = "";
            bool accepted = request.responseCode == 200 &&
                request.result == UnityWebRequest.Result.Success &&
                TryReadRoleDecisionResponse(responseBody,
                    out raw, out finishReason, out compiled, out status, out rejectReason);
            DecisionCompiler.TryParse(raw, out var parsedDecisionForTrace, out _);
            string parsedRoute = parsedDecisionForTrace?.Route ?? "unknown";
            ReadRoleDecisionDiagnostics(raw, out bool containsThought, out int thoughtCharacters,
                out int speechCharacters, out int intentCount, out string intentTypes);
            // This lane intentionally uses a bounded non-stream response so
            // incomplete JSON never reaches a public/action consumer.  The
            // marker therefore means content was received, not first-token
            // arrival; formal streaming retains the true first-token trace.
            if (raw.Length > 0) trace?.Mark("role_decision.content_received", new JObject {
                ["finishReason"] = finishReason,
                ["responseCharacters"] = responseBody.Length,
                ["responseBytes"] = Encoding.UTF8.GetByteCount(responseBody),
                ["contentCharacters"] = raw.Length,
                ["containsThought"] = containsThought,
                ["thoughtCharacters"] = thoughtCharacters,
                ["speechCharacters"] = speechCharacters,
                ["route"] = parsedRoute,
                ["task"] = parsedDecisionForTrace?.Task,
                ["attention"] = parsedDecisionForTrace?.Attention,
                ["schemaVersion"] = parsedDecisionForTrace?.Version,
                ["intentCount"] = intentCount,
                ["intentTypes"] = intentTypes
            });
            trace?.Mark(accepted ? "role_decision.accepted" : "role_decision.rejected",
                new JObject {
                    ["status"] = status,
                    ["finishReason"] = finishReason,
                    ["responseCharacters"] = responseBody.Length,
                    ["responseBytes"] = Encoding.UTF8.GetByteCount(responseBody),
                    ["rawCharacters"] = raw.Length,
                    ["contentCharacters"] = raw.Length,
                    ["containsThought"] = containsThought,
                    ["thoughtCharacters"] = thoughtCharacters,
                    ["speechCharacters"] = speechCharacters,
                    ["route"] = parsedRoute,
                    ["task"] = parsedDecisionForTrace?.Task,
                    ["attention"] = parsedDecisionForTrace?.Attention,
                ["schemaVersion"] = parsedDecisionForTrace?.Version,
                ["intentCount"] = intentCount,
                    ["intentTypes"] = intentTypes,
                    ["compiledCharacters"] = compiled.Length,
                    ["rejectReason"] = rejectReason,
                    ["elapsedSeconds"] = clock.Elapsed.TotalSeconds
                });
            if (m_LogRequestStats)
                Debug.Log("[LLM/RoleDecisionWire] " + new JObject {
                    ["code"] = request.responseCode,
                    ["result"] = request.result.ToString(),
                    ["accepted"] = accepted,
                    ["status"] = status,
                    ["finishReason"] = finishReason,
                    ["rejectReason"] = rejectReason,
                    ["elapsed"] = clock.Elapsed.TotalSeconds,
                    ["maxTokens"] = 256,
                    ["responseCharacters"] = responseBody.Length,
                    ["responseBytes"] = Encoding.UTF8.GetByteCount(responseBody),
                    ["contentCharacters"] = raw.Length,
                    ["containsThought"] = containsThought,
                    ["thoughtCharacters"] = thoughtCharacters,
                    ["speechCharacters"] = speechCharacters,
                    ["route"] = parsedRoute,
                    ["task"] = parsedDecisionForTrace?.Task,
                    ["attention"] = parsedDecisionForTrace?.Attention,
                ["schemaVersion"] = parsedDecisionForTrace?.Version,
                ["intentCount"] = intentCount,
                    ["promptChars"] = promptLengthFromRoleDecisionJson(json),
                    ["requestBytes"] = Encoding.UTF8.GetByteCount(json),
                    ["imageCharacters"] = ImageCharactersInRequest(json),
                    ["slot"] = m_Backend == BackendType.Local ? (int?)k_SlotAuxiliary : null
                }.ToString(Formatting.None));
            AcceptedRoleDecision = accepted && DecisionCompiler.TryParse(raw, out var acceptedDecision, out _)
                ? acceptedDecision : null;
            callback?.Invoke(accepted, accepted ? compiled : "", status);
        }
    }

    private static int promptLengthFromRoleDecisionJson(string json)
    {
        try
        {
            var root = JObject.Parse(json);
            var messages = root["messages"] as JArray;
            if (messages == null || messages.Count == 0) return 0;
            var content = messages[messages.Count - 1]["content"];
            if (content?.Type == JTokenType.String) return ((string)content ?? "").Length;
            int length = 0;
            if (content is JArray parts)
                foreach (var part in parts) if ((string)part["type"] == "text") length += ((string)part["text"] ?? "").Length;
            return length;
        }
        catch (Exception) { return 0; }
    }

    private static int ImageCharactersInRequest(string json)
    {
        if (string.IsNullOrEmpty(json)) return 0;
        int start = json.IndexOf("data:image/", StringComparison.Ordinal);
        if (start < 0) return 0;
        int end = json.IndexOf('"', start);
        return end > start ? end - start : 0;
    }

    private static bool TryReadRoleDecisionResponse(string envelope,
        out string raw, out string finishReason, out string compiled, out string status, out string rejectReason)
    {
        raw = ""; finishReason = ""; compiled = ""; status = "invalid_envelope"; rejectReason = "";
        try
        {
            var settings = new JsonLoadSettings {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            };
            var response = JObject.Parse(envelope, settings);
            var choices = response["choices"] as JArray;
            var choice = choices != null && choices.Count > 0 ? choices[0] : null;
            if (choice == null)
            {
                rejectReason = "missing_message_content";
                return false;
            }
            finishReason = (string)choice["finish_reason"] ?? "";
            if (choice["message"]?["content"]?.Type != JTokenType.String)
            {
                rejectReason = "missing_message_content";
                return false;
            }
            raw = (string)choice["message"]["content"] ?? "";
            if (finishReason != "stop")
            {
                status = string.IsNullOrEmpty(finishReason) ? "missing_finish" : "finish_" + finishReason;
                rejectReason = status;
                return false;
            }
            status = "invalid_role_decision";
            if (!DecisionCompiler.TryParse(raw, out var decision, out var parseError))
            {
                // A known route with an accidental public sentence is still safe
                // to hand to the existing planner. Drop that sentence; unknown
                // or extended route objects remain rejected.
                if (TryGetSafeRouteHint(raw))
                {
                    status = "tools";
                    rejectReason = "safe_route_hint_after_parse_rejection";
                    return true;
                }
                rejectReason = string.IsNullOrEmpty(parseError) ? "parse_rejected" : parseError;
                return false;
            }
            if (decision.HasExecutionIntent)
            {
                // A route proposal is deliberately returned without executable
                // tags. The caller hands the original user turn to the legacy
                // planner, which remains the only action authority.
                status = "tools";
                compiled = "";
                return true;
            }
            if (!DecisionCompiler.TryCompile(raw, out compiled, out var compileError) || string.IsNullOrEmpty(compiled))
            {
                rejectReason = string.IsNullOrEmpty(compileError) ? "compile_rejected" : compileError;
                return false;
            }
            status = string.IsNullOrWhiteSpace(decision.Speech) ? "ok-silent" : "ok";
            return true;
        }
        catch (JsonException) { rejectReason = "invalid_json_envelope"; return false; }
        catch (ArgumentException) { rejectReason = "argument_error"; return false; }
        catch (InvalidOperationException) { rejectReason = "invalid_operation"; return false; }
    }

    private static void ReadRoleDecisionDiagnostics(string raw, out bool containsThought,
        out int thoughtCharacters, out int speechCharacters, out int intentCount, out string intentTypes)
    {
        containsThought = ContainsJsonPropertyMarker(raw, "thought");
        thoughtCharacters = -1;
        speechCharacters = -1;
        intentCount = -1;
        intentTypes = "";
        try
        {
            var value = JObject.Parse(raw ?? "");
            var thought = value["thought"];
            containsThought = thought != null;
            if (thought?.Type == JTokenType.String) thoughtCharacters = ((string)thought ?? "").Length;
            if (value["speech"] is JObject speech && speech["text"]?.Type == JTokenType.String)
                speechCharacters = ((string)speech["text"] ?? "").Length;
            if (value["intents"] is JArray intents)
            {
                intentCount = intents.Count;
                var types = new System.Collections.Generic.List<string>();
                foreach (var item in intents)
                    if (item is JObject intent && intent["type"]?.Type == JTokenType.String)
                        types.Add((string)intent["type"] ?? "");
                intentTypes = string.Join(",", types);
            }
        }
        catch (JsonException) { }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    private static bool ContainsJsonPropertyMarker(string json, string propertyName)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName)) return false;
        string marker = "\"" + propertyName + "\"";
        int searchFrom = 0;
        while (searchFrom < json.Length)
        {
            int at = json.IndexOf(marker, searchFrom, StringComparison.Ordinal);
            if (at < 0) return false;
            int colon = at + marker.Length;
            while (colon < json.Length && char.IsWhiteSpace(json[colon])) colon++;
            if (colon < json.Length && json[colon] == ':') return true;
            searchFrom = at + marker.Length;
        }
        return false;
    }

    private static bool TryGetSafeRouteHint(string raw)
    {
        try
        {
            var value = JObject.Parse(raw ?? "");
            if (value["version"]?.Type != JTokenType.Integer || (int)value["version"] != 1 ||
                !HasOnlyRoleDecisionFields(value) || !(value["speech"] is JObject speech) ||
                speech["text"]?.Type != JTokenType.String || speech["language"]?.Type != JTokenType.String)
                return false;
            var intents = value["intents"] as JArray;
            if (intents == null || intents.Count != 1 || !(intents[0] is JObject item) ||
                item.Count != 1 || item["type"]?.Type != JTokenType.String)
                return false;
            string type = (string)item["type"];
            if (string.Equals(type, "room.request/tools", StringComparison.OrdinalIgnoreCase))
                type = "room.request";
            return type == "room.request" || type == "singing.request" ||
                type == "memory.request" || type == "tool.request";
        }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool HasOnlyRoleDecisionFields(JObject value)
    {
        foreach (var property in value.Properties())
        {
            if (property.Name != "version" && property.Name != "thought" &&
                property.Name != "speech" && property.Name != "attention" &&
                property.Name != "observation" && property.Name != "intents") return false;
        }
        return value.Count == 4 || value.Count == 5 || value.Count == 6;
    }

    private string BuildSemanticDecisionWire(string json)
    {
        var wire = JObject.Parse(json);
        if (wire["messages"] is JArray messages)
            messages.Add(new JObject { ["role"] = "system", ["content"] = DecisionCompiler.Contract });
        // Prompt + strict local validation work with both cloud and local providers;
        // no unsupported server-specific response_format is assumed.
        return wire.ToString(Formatting.None);
    }
}
