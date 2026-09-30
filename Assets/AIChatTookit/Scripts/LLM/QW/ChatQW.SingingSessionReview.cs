using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public partial class ChatQW
{
    // The short review still failed a wrong-source negative control against the
    // real model. Keep complete independent review in production. This flag is
    // deliberately not serialized into scenes; offline fixtures may opt in.
    [NonSerialized] internal bool EnableExperimentalSingingSessionReview = false;

    private bool TryBuildSingingSessionReviewRequest(string prompt, out string wire)
    {
        wire = null;
        if (!EnableExperimentalSingingSessionReview) return false;
        if (m_Backend == BackendType.Cloud) return false; // Keep legacy cloud transport compatibility.
        if (!(prompt ?? "").StartsWith(RecordingSingingDecisionCompiler.ReviewContextMarker, StringComparison.Ordinal)) return false;
        JObject facts;
        try { facts = JObject.Parse(prompt.Substring(RecordingSingingDecisionCompiler.ReviewContextMarker.Length)); }
        catch (JsonException) { return false; }
        if (!SingingSessionReview.TryCreate(facts, out var packet)) return false;
        var sb = new StringBuilder(4096);
        sb.Append("{\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":256,\"response_format\":");
        var format = new JObject { ["type"] = "json_schema", ["json_schema"] = new JObject {
            ["name"] = SingingSessionReview.SchemaName, ["strict"] = true, ["schema"] = SingingSessionReview.Schema() } };
        sb.Append(format.ToString(Formatting.None)).Append(",\"messages\":[");
        AppendMessage(sb, new SendData("system", SingingSessionReview.Contract)); sb.Append(',');
        AppendMessage(sb, new SendData("user", packet.ToString(Formatting.None)));
        sb.Append(']'); AppendReasoningControls(sb, false); AppendSlot(sb, k_SlotAuxiliary); sb.Append('}');
        wire = sb.ToString(); return true;
    }

    private static bool IsSingingSessionReviewRequest(string wire)
    {
        try { return (string)JObject.Parse(wire)["response_format"]?["json_schema"]?["name"] == SingingSessionReview.SchemaName; }
        catch (JsonException) { return false; }
    }

    private static bool TryReadSingingSessionReviewResponse(string wire, string envelope, out string output,
        out string finishReason, out bool validJson, out bool validSchema, out string status)
    {
        output = ""; finishReason = ""; validJson = validSchema = false; status = "invalid_envelope";
        try
        {
            var settings = new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error };
            var request = JObject.Parse(wire, settings);
            var messages = request["messages"] as JArray;
            if ((string)request["response_format"]?["json_schema"]?["name"] != SingingSessionReview.SchemaName || messages?.Count != 2) return false;
            var packet = JObject.Parse((string)messages[1]["content"], settings);
            var response = JObject.Parse(envelope, settings);
            var choices = response["choices"] as JArray;
            var choice = choices?.Count > 0 ? choices[0] : null;
            if (choice?["message"]?["content"]?.Type != JTokenType.String) return false;
            string raw = (string)choice["message"]["content"];
            finishReason = (string)choice["finish_reason"] ?? "";
            if (finishReason != "stop") { status = finishReason == "length" ? "truncated" : "incomplete_finish"; return false; }
            JObject.Parse(raw, settings); validJson = true;
            if (!SingingSessionReview.TryNormalize(packet, raw, out var normalized, out status)) return false;
            validSchema = IsValidWorkReviewObject(normalized);
            if (!validSchema) { status = "invalid_schema"; return false; }
            output = normalized.ToString(Formatting.None); status = "ok"; return true;
        }
        catch (Exception e) when (e is JsonException || e is ArgumentException || e is InvalidOperationException) { return false; }
    }
}
