using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    public IEnumerator ReviewTranscriptComparison(JObject packet, Func<bool> current, Action<bool> complete)
    {
        if (!current()) yield break;
        var input = new JObject { ["streamText"] = packet.SelectToken("stream.text")?.DeepClone(),
            ["fullText"] = packet.SelectToken("full.text")?.DeepClone(),
            ["streamSource"] = packet.SelectToken("stream.source")?.DeepClone(),
            ["fullSource"] = packet.SelectToken("full.source")?.DeepClone(),
            ["provenance"] = packet["provenance"]?.DeepClone() };
        var format = new JObject { ["type"] = "json_object" };
        if (m_Backend == BackendType.Local) format["schema"] = JObject.Parse(TranscriptDisagreementPolicy.ReviewSchema);
        var wire = new JObject { ["model"] = CurrentModelName, ["stream"] = false, ["temperature"] = 0,
            ["enable_thinking"] = false, ["max_tokens"] = 384, ["response_format"] = format,
            ["messages"] = new JArray(new JObject { ["role"] = "system", ["content"] = TranscriptDisagreementPolicy.ReviewContract },
                new JObject { ["role"] = "user", ["content"] = input.ToString(Formatting.None) }) };
        if (m_Backend == BackendType.Local) wire["chat_template_kwargs"] = new JObject { ["enable_thinking"] = false };
        var slot = new StringBuilder(); AppendSlot(slot, k_SlotAuxiliary);
        if (slot.Length > 0) foreach (var p in JObject.Parse("{" + slot.ToString().TrimStart(',') + "}").Properties()) wire[p.Name] = p.Value;
        string json = wire.ToString(Formatting.None);
        if ((bool?)MeasureRoomDecisionBudget(json, 4096)["allowed"] != true) { complete(false); yield break; }
        float started = Time.realtimeSinceStartup;
        using (var request = new UnityWebRequest(url, "POST")) {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 3;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            var operation = request.SendWebRequest();
            while (!operation.isDone && current() && Time.realtimeSinceStartup - started < 3f) yield return null;
            if (!current()) { request.Abort(); yield break; }
            if (!operation.isDone) request.Abort();
            string output = "", status = "http-failure";
            bool equivalent = request.result == UnityWebRequest.Result.Success &&
                TryReadRoomTaskCompletion(request.downloadHandler.text, out output, out status) &&
                TranscriptDisagreementPolicy.Equivalent(packet, output);
            Debug.Log("[ASR/ComparisonReview] " + new JObject { ["capture"] = packet["capture"],
                ["equivalent"] = equivalent, ["status"] = status, ["httpStatus"] = request.responseCode,
                ["elapsedSeconds"] = Time.realtimeSinceStartup - started, ["attempts"] = 1,
                ["response"] = output, ["sceneFactsIncluded"] = false, ["actionAuthorized"] = false }.ToString(Formatting.None));
            complete(equivalent);
        }
    }
}
