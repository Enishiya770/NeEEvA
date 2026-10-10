using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public partial class ChatQW
{
    private const float RoomTaskRecoveryBudgetSeconds = 20f;
    private const int RoomTaskMaximumAttempts = 2;

    // These are transport facts, not a decision or permission to perform an action.
    public static bool TryReadRoomTaskFailureStatus(string status, out JObject facts)
    {
        facts = null;
        if (string.IsNullOrEmpty(status) || !status.StartsWith("room-task-http-", StringComparison.Ordinal)) return false;
        string[] parts = status.Split(';');
        string category = parts[0].Substring("room-task-http-".Length);
        if (!RoomTaskFailureCategories.Contains(category)) category = "unknown";
        int attempts = -1;
        string recovery = "unknown";
        foreach (string part in parts.Skip(1))
        {
            if (part.StartsWith("attempts=", StringComparison.Ordinal) && int.TryParse(part.Substring(9), out int count) && count >= 0 && count <= 2)
                attempts = count;
            if (part.StartsWith("recovery=", StringComparison.Ordinal))
            {
                string value = part.Substring(9);
                if (RoomTaskRecoveryOutcomes.Contains(value)) recovery = value;
            }
        }
        facts = new JObject { ["stage"] = category == "context-budget" && attempts == 0 ? "decision-preflight" : "decision-provider", ["category"] = category,
            ["attempts"] = attempts < 0 ? JValue.CreateNull() : new JValue(attempts),
            ["httpRequestSent"] = attempts < 0 ? JValue.CreateNull() : new JValue(attempts > 0),
            ["recovery"] = recovery, ["retryAttempted"] = attempts < 0 ? JValue.CreateNull() : new JValue(attempts > 1),
            ["actionDispatched"] = false,
            ["scope"] = attempts == 0 ? "Input admission stopped before any HTTP request. No provider refusal, navigation failure, ambiguous user intent or movement is established." :
                "The provider did not return an accepted decision. This is not ambiguous user intent, a navigation failure, or evidence that movement started." };
        return true;
    }

    private static readonly HashSet<string> RoomTaskFailureCategories = new HashSet<string>(StringComparer.Ordinal) {
        "context-limit", "context-budget", "authentication", "rate-limit", "timeout", "server-error", "connection", "invalid-request", "unknown" };
    private static readonly HashSet<string> RoomTaskRecoveryOutcomes = new HashSet<string>(StringComparer.Ordinal) {
        "not-eligible", "context-not-reducible", "exhausted", "disabled-review", "budget-exhausted", "retry-after-exceeds-budget", "preflight-budget" };

    private static string RoomTaskDigest(string value)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
    }

    private static JObject ReadRoomTaskRecoveryJson(string json)
    {
        // Json.NET otherwise promotes ISO-shaped strings to DateTime and can
        // silently normalize capture timestamps when serializing the retry.
        using (var reader = new JsonTextReader(new StringReader(json ?? "")) { DateParseHandling = DateParseHandling.None })
        {
            var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            while (reader.Read())
                if (reader.TokenType != JsonToken.Comment) throw new JsonReaderException("Unexpected trailing content after a request object.");
            return result;
        }
    }

    private static string ReadRoomTaskErrorMessage(string envelope, out string code, out string type)
    {
        code = ""; type = "";
        if (string.IsNullOrWhiteSpace(envelope)) return "";
        // Error bodies may echo an entire input. Only conventional error fields are read.
        if (envelope.Length > 262144) return "[oversized-error-body-omitted]";
        try
        {
            var root = ReadRoomTaskRecoveryJson(envelope);
            var error = root["error"] as JObject ?? root;
            code = error["code"]?.Type == JTokenType.String ? (string)error["code"] : "";
            type = error["type"]?.Type == JTokenType.String ? (string)error["type"] : "";
            if (error["message"]?.Type == JTokenType.String) return (string)error["message"];
            if (root["error"]?.Type == JTokenType.String) return (string)root["error"];
            return "[unrecognized-json-error-body-omitted]";
        }
        catch (JsonException) { return envelope.Substring(0, Math.Min(8192, envelope.Length)); }
    }

    private static string SanitizeRoomTaskError(string value, int limit)
    {
        string result = (value ?? "").Substring(0, Math.Min(8192, (value ?? "").Length));
        result = Regex.Replace(result, @"data:[^\s\""']+", "[redacted-data]", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"(?i)(authorization\s*[:=]?\s*(?:bearer\s+)?|bearer\s+|api[_-]?key\s*[:=]\s*|sk-)[^\s,;]+", "[redacted-secret]");
        result = Regex.Replace(result, @"https?://[^\s\""']+", "[redacted-url]", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"(?is)(?:request\s*body|input\s*text|prompt\s*text|messages|api[_-]?key)\s*[:=].*$", "[redacted-request-echo]");
        result = RedactRoomTaskQuotedValues(result);
        result = Regex.Replace(result, @"[A-Za-z0-9_+/=.-]{48,}", "[redacted-long-value]");
        result = Regex.Replace(result, @"[\r\n\t]+", " ");
        return result.Length <= limit ? result : result.Substring(0, limit) + "[truncated]";
    }

    private static string RedactRoomTaskQuotedValues(string value)
    {
        var safe = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; ++index)
        {
            char quote = value[index];
            // An apostrophe inside a word is not the opening of a quoted payload.
            bool contraction = quote == '\'' && index > 0 && index + 1 < value.Length &&
                char.IsLetterOrDigit(value[index - 1]) && char.IsLetterOrDigit(value[index + 1]);
            if ((quote != '"' && quote != '\'') || contraction) { safe.Append(quote); continue; }
            safe.Append("[redacted-quoted-value]");
            while (++index < value.Length)
            {
                // A quote escaped by a backslash remains inside the same value.
                // A dangling backslash or an absent closing quote consumes the
                // entire remaining tail, including a diagnostic-input size cut.
                if (value[index] == '\\') { ++index; continue; }
                if (value[index] == quote) break;
            }
        }
        return safe.ToString();
    }

    private static JObject BuildRoomTaskHttpFailure(long responseCode, string envelope, string transportError)
    {
        string message = ReadRoomTaskErrorMessage(envelope, out string code, out string type);
        string match = (code + " " + type + " " + message).ToLowerInvariant();
        string category = "unknown";
        if (responseCode == 401 || responseCode == 403) category = "authentication";
        else if ((responseCode == 400 || responseCode == 413 || responseCode == 422) &&
            (Regex.IsMatch(code, @"^(context_length_exceeded|context_window_exceeded|prompt_too_long)$", RegexOptions.IgnoreCase) ||
             Regex.IsMatch(match, @"maximum context length|context (?:window|length|size).{0,80}(?:exceed|too (?:long|large)|limit)|(?:exceed|larger than).{0,80}context|prompt.{0,40}(?:too long|exceeds.{0,20}n_ctx)"))) category = "context-limit";
        else if (responseCode == 429) category = "rate-limit";
        else if (responseCode == 408 || responseCode == 504) category = "timeout";
        else if (responseCode == 500 || responseCode == 502 || responseCode == 503) category = "server-error";
        else if (responseCode == 0) category = (transportError ?? "").IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 ? "timeout" : "connection";
        else if (responseCode >= 400 && responseCode < 500) category = "invalid-request";
        return new JObject { ["category"] = category, ["httpCode"] = responseCode,
            ["errorCode"] = SanitizeRoomTaskError(code, 96), ["errorType"] = SanitizeRoomTaskError(type, 96),
            ["message"] = SanitizeRoomTaskError(message, 768), ["transportError"] = SanitizeRoomTaskError(transportError, 160),
            ["bodyCharacters"] = (envelope ?? "").Length, ["bodySha256"] = RoomTaskDigest(envelope),
            ["scope"] = "Bounded redacted provider error fields. Unknown 400 is not assumed to be context overflow; request contents and credentials are not logged." };
    }

    private static JObject BuildRoomTaskRequestDiagnostic(string json)
    {
        var result = new JObject { ["utf8Bytes"] = Encoding.UTF8.GetByteCount(json ?? ""),
            ["characters"] = (json ?? "").Length, ["sha256"] = RoomTaskDigest(json) };
        try
        {
            var wire = ReadRoomTaskRecoveryJson(json);
            result["schemaSha256"] = wire["response_format"]?["schema"] == null ? JValue.CreateNull() :
                new JValue(RoomTaskDigest(wire["response_format"]["schema"].ToString(Formatting.None)));
            result["imageCount"] = wire.SelectTokens("$..image_url.url").Count();
            result["maxTokens"] = wire["max_tokens"]?.DeepClone();
            var messages = wire["messages"] as JArray;
            result["messageCount"] = messages?.Count ?? 0;
            result["textCharacters"] = messages == null ? 0 : messages.Sum(item => RoomTaskMessageText(item as JObject).Length);
            if (TryFindRoomTaskInput(wire, out _, out var input))
            {
                var lengths = new JObject();
                // Field names are fixed by the producer; never copy arbitrary user keys.
                foreach (string field in RoomTaskDiagnosticFields)
                    if (input[field] != null) lengths[field] = input[field].ToString(Formatting.None).Length;
                result["inputFieldCharacters"] = lengths;
            }
        }
        catch (JsonException) { result["parseable"] = false; }
        return result;
    }

    private static readonly string[] RoomTaskDiagnosticFields = { "currentUserText", "latestUserText", "recentDialogue", "currentFacts",
        "visualObservation", "visualEvidence", "sceneActionFacts", "visibleKnownObjects", "sceneActionPrerequisites", "sceneSpatialMemory",
        "visibleObjectCatalog", "sceneSearch", "turnSweep", "spatialGoal", "verifiedDeviceHistory", "verifiedObservedDeviceHistory",
        "referentSources", "objectTargetReview", "pendingObservationGoal", "decisionContractFacts", "autonomousContext" };

    private static string RoomTaskMessageText(JObject message)
    {
        if (message?["content"]?.Type == JTokenType.String) return (string)message["content"];
        return message?["content"] is JArray parts ? string.Join("\n", parts.OfType<JObject>()
            .Where(part => (string)part["type"] == "text").Select(part => (string)part["text"] ?? "")) : "";
    }

    private static bool TryFindRoomTaskInput(JObject wire, out JToken textToken, out JObject input)
    {
        textToken = null; input = null;
        if (!(wire["messages"] is JArray messages)) return false;
        foreach (var message in messages.OfType<JObject>())
        {
            if ((string)message["role"] != "user") continue;
            var candidates = message["content"] is JArray parts ? parts.OfType<JObject>()
                .Where(part => (string)part["type"] == "text").Select(part => part["text"]) : new[] { message["content"] };
            foreach (var candidate in candidates)
            {
                if (candidate?.Type != JTokenType.String) continue;
                string text = (string)candidate;
                int start = text.IndexOf('{');
                if (start < 0) continue;
                try
                {
                    var parsed = ReadRoomTaskRecoveryJson(text.Substring(start));
                    if (parsed["currentUserText"]?.Type != JTokenType.String || parsed["autonomous"]?.Type != JTokenType.Boolean) continue;
                    textToken = candidate; input = parsed; return true;
                }
                catch (JsonException) { }
            }
        }
        return false;
    }

    // Lossless request-local interning: repeated read-only geometry/provenance is
    // represented once. No array is shortened, no source text/authority removed,
    // and neither current nor historical image attachments are changed.
    private static bool TryCompactRoomTaskRequest(string json, out string compact)
    {
        compact = json;
        try
        {
            var wire = ReadRoomTaskRecoveryJson(json);
            if (!TryFindRoomTaskInput(wire, out var textToken, out var input) ||
                input.Descendants().OfType<JProperty>().Any(property => property.Name == "requestEvidenceTable" ||
                    property.Name == "requestEvidenceRef" || property.Name == "requestEvidenceEncoding")) return false;
            var table = new JObject();
            var seen = new Dictionary<string, List<JProperty>>(StringComparer.Ordinal);
            foreach (var property in input.Descendants().OfType<JProperty>().ToArray())
            {
                string value = property.Value.ToString(Formatting.None);
                bool workingMemoryId = input["workingMemory"] != null && RoomWorkingMemoryInternedIds.Contains(property.Name);
                if (!RoomTaskInternedFields.Contains(property.Name) && !workingMemoryId) continue;
                if (value.Length < (workingMemoryId ? 64 : 256)) continue;
                if (!seen.TryGetValue(value, out var copies)) seen[value] = copies = new List<JProperty>();
                copies.Add(property);
            }
            // Intern enclosing values before smaller child values. Otherwise a
            // shared scope encountered in an earlier branch could be replaced
            // before a later bearing/selfPose is copied, creating nested refs.
            foreach (var copies in seen.OrderByDescending(entry => entry.Key.Length).Select(entry => entry.Value))
            {
                if (copies.Count < 2) continue;
                // A replaced ancestor can detach a child from the request tree.
                var attached = copies.Where(property => ReferenceEquals(property.Root, input)).ToArray();
                if (attached.Length < 2) continue;
                string id = "e" + table.Count.ToString(CultureInfo.InvariantCulture);
                table[id] = attached[0].Value.DeepClone();
                foreach (var property in attached) property.Value = new JObject { ["requestEvidenceRef"] = id };
            }
            bool indexCompacted = CompactRoomWorkingMemoryIndex(input);
            if (table.Count == 0 && !indexCompacted) return false;
            if (table.Descendants().OfType<JProperty>().Any(property => property.Name == "requestEvidenceRef")) return false;
            input["requestEvidenceTable"] = table;
            input["requestEvidenceEncoding"] = "Lossless shared read-only facts: every {requestEvidenceRef:id} means the exact value of input.requestEvidenceTable[id]. Resolve these references before interpreting the containing field. All authority, current geometry, source captures and history remain unchanged. References confer no new visibility or action permission.";
            string original = (string)textToken;
            int start = original.IndexOf('{');
            string reducedText = original.Substring(0, start) + input.ToString(Formatting.None);
            textToken.Replace(new JValue(reducedText));
            string candidate = wire.ToString(Formatting.None);
            int before = Encoding.UTF8.GetByteCount(original), after = Encoding.UTF8.GetByteCount(reducedText);
            // Judge text reduction separately from fixed base64 image bytes, which
            // are not proportional to the vision tokens consumed by the backend.
            if (before - after < 1024 || after > before * .95) return false;
            compact = candidate; return true;
        }
        catch (JsonException) { return false; }
    }

    private static readonly HashSet<string> RoomTaskInternedFields = new HashSet<string>(StringComparer.Ordinal) {
        "selfPose", "authoredHint", "geometryNeighbor", "scope", "relativeToCurrentView", "relativeToCurrentBody", "returnDirection",
        "targetReview", "sceneSearch", "lastAction", "confirmedTarget", "completionEvidence", "boundEntityMemory", "currentBearing" };

    private static readonly HashSet<string> RoomWorkingMemoryInternedIds = new HashSet<string>(StringComparer.Ordinal) {
        "captureId", "sourceCaptureId", "viewCaptureId", "referenceId", "selectionId", "targetMatchCaptureId", "descriptionCaptureId" };

    private static bool TryPlanRoomTaskTransportRetry(bool review, int attempts, bool ownerCurrent,
        float remainingSeconds, string category, string retryAfter, string json, out string nextJson, out float delay, out string stopReason)
    {
        nextJson = json; delay = .5f; stopReason = "not-eligible";
        if (!ownerCurrent) return false;
        if (review) { stopReason = "disabled-review"; return false; }
        if (attempts >= RoomTaskMaximumAttempts) { stopReason = "exhausted"; return false; }
        if (remainingSeconds < 2) { stopReason = "budget-exhausted"; return false; }
        if (!string.IsNullOrWhiteSpace(retryAfter))
        {
            if (!double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ||
                double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds > 2)
            { stopReason = "retry-after-exceeds-budget"; return false; }
            delay = (float)Math.Max(.5, seconds);
        }
        if (remainingSeconds < delay + 1) { stopReason = "budget-exhausted"; return false; }
        if (category == "context-limit")
        {
            if (!TryBudgetRoomDecisionRequest(json, true, out nextJson, out var budget)) { stopReason = "context-not-reducible"; return false; }
            // The admitted recovery must actually reduce the failed text, even if
            // it was already under the estimate (the backend is authoritative).
            if (Encoding.UTF8.GetByteCount(json) - Encoding.UTF8.GetByteCount(nextJson) < 1024)
            { stopReason = "context-not-reducible"; return false; }
            return true;
        }
        return category == "rate-limit" || category == "timeout" || category == "server-error" || category == "connection";
    }
}
