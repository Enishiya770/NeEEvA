using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    // This record is never consumed as speech, a boundary decision or model history.
    private void LogListeningWire(string kind, string id, int generation, string status,
        double seconds, UnityWebRequest response, string requestJson, string responseJson)
    {
        if (!m_LogRequestStats) return;
        var record = new JObject {
            ["requestKind"] = kind, ["requestId"] = id, ["generation"] = generation,
            ["status"] = status, ["seconds"] = Math.Round(seconds, 4),
            ["code"] = response == null ? (long?)null : response.responseCode,
            ["result"] = response?.result.ToString()
        };
        try
        {
            var request = JObject.Parse(requestJson);
            record["maxTokens"] = request["max_tokens"]?.DeepClone();
            record["messageCount"] = (request["messages"] as JArray)?.Count;
            record["schemaBound"] = request["response_format"]?["schema"] != null;
            if (!string.IsNullOrEmpty(responseJson))
            {
                var wire = JObject.Parse(responseJson);
                var choice = (wire["choices"] as JArray)?[0];
                record["finishReason"] = choice?["finish_reason"]?.DeepClone();
                record["usage"] = wire["usage"]?.DeepClone();
                string content = (string)choice?["message"]?["content"] ?? "";
                record["contentCharacters"] = content.Length;
                record["content"] = content.Length > 2048 ? content.Substring(0, 2048) : content;
            }
        }
        catch (JsonException) { record["diagnosticParse"] = "invalid-json"; }
        catch (ArgumentException) { record["diagnosticParse"] = "invalid-envelope"; }
        Debug.Log("[Listening/Wire] " + record.ToString(Formatting.None));
    }
}
