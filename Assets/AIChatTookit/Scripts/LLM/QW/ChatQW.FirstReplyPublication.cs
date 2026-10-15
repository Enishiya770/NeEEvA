using System;
using System.Collections;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    private IEnumerator ReviewFirstReplyPublication(FirstReplyCompletion candidate, int generation, Action<bool> callback)
    {
        bool Current() => generation == m_StreamRequestGeneration;
        if (!Current()) yield break;
        if (OngoingSpeechMonitoring && SupportsActivityMonitor) {
            // Routing and the public/action syntax boundary already ran. The
            // character may revise ordinary speech during playback; do not add
            // another full words-only approval wait or reject mixed language.
            Debug.Log("[LLM/FirstReplyPublication] mode=ongoing-monitor actionAuthorized=False");
            callback(true); yield break;
        }
        string speech = string.Join("\n", candidate.Speech.Select(part => part.Text));
        string wire = FirstReplyPublicationProtocol.Request(CurrentModelName, speech, m_Backend == BackendType.Local).ToString(Formatting.None);
        var budget = MeasureRoomDecisionBudget(wire, 4096);
        if ((bool?)budget["allowed"] != true) { callback(false); yield break; }
        float started = Time.realtimeSinceStartup;
        using (var request = new UnityWebRequest(url, "POST")) {
            m_ActiveStreamRequest = request;
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(wire));
            request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 8;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            RaiseRequestDiagnostic(wire);
            if (!Current()) {
                if (ReferenceEquals(m_ActiveStreamRequest, request)) m_ActiveStreamRequest = null;
                yield break;
            }
            var operation = request.SendWebRequest();
            while (!operation.isDone && Current() && Time.realtimeSinceStartup - started < 8f) yield return null;
            bool expired = Time.realtimeSinceStartup - started >= 8f;
            if (!operation.isDone) request.Abort();
            if (ReferenceEquals(m_ActiveStreamRequest, request)) m_ActiveStreamRequest = null;
            if (!Current()) yield break;
            JObject result = null;
            bool accepted = !expired && request.result == UnityWebRequest.Result.Success && request.responseCode == 200 &&
                TryReadRoomTaskCompletion(request.downloadHandler.text, out string output, out _) &&
                FirstReplyPublicationProtocol.TryRead(output, speech, out result);
            bool released = accepted && (string)result["kind"] == "conversation" &&
                candidate.Speech.All(part => part.LanguageCode == (string)result["actualLanguage"]);
            Debug.Log("[LLM/FirstReplyPublication] " + new JObject {
                ["generation"] = generation, ["accepted"] = accepted, ["released"] = released,
                ["httpStatus"] = request.responseCode, ["timedOut"] = expired,
                ["claim"] = result, ["elapsedSeconds"] = Time.realtimeSinceStartup - started,
                ["actionAuthorized"] = false,
                ["scope"] = "No speech/history release before this words-only gate. Non-conversation or failure requires existing factual reply review; never execute a candidate promise."
            }.ToString(Formatting.None));
            callback(released);
        }
    }
}
