using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using Stopwatch = System.Diagnostics.Stopwatch;

public partial class ChatQW : IFastTaskControlBackend
{
    public void PostFastTaskControlDecision(string context, string currentUser,
        Action<string, string> callback, ConversationTrace trace = null)
    {
        CancelFastLoopDecision();
        if (!SupportsFastLoopDecisions) { callback?.Invoke(null, "fast_loop_disabled"); return; }
        AbortPrewarmIfRunning();
        int generation = m_FastLoopGeneration;
        trace = trace ?? TraceContext?.Child("llm-fast-task-control");
        string wire = BuildFastTaskControlRequestJson(context, currentUser);
        StartCoroutine(RequestFastTaskControlPayload(wire, generation, callback, trace));
    }

    private string BuildFastTaskControlRequestJson(string context, string currentUser)
    {
        var sb = new StringBuilder(2048);
        sb.Append("{\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":32");
        sb.Append(",\"response_format\":").Append(FastTaskControlProtocol.ResponseFormat().ToString(Formatting.None));
        sb.Append(",\"messages\":[");
        AppendMessage(sb, new SendData("system", FastTaskControlProtocol.SystemPrompt));
        sb.Append(',');
        AppendMessage(sb, new SendData("user", FastTaskControlProtocol.BuildUser(context, currentUser).ToString(Formatting.None)));
        sb.Append(']'); AppendReasoningControls(sb, false); AppendSlot(sb, k_SlotAuxiliary); sb.Append('}');
        return sb.ToString();
    }

    private IEnumerator RequestFastTaskControlPayload(string wire, int generation,
        Action<string, string> callback, ConversationTrace trace)
    {
        if (generation != m_FastLoopGeneration) yield break;
        var clock = Stopwatch.StartNew();
        trace?.Mark("fast_task_control.send", new JObject { ["generation"] = generation,
            ["requestBytes"] = Encoding.UTF8.GetByteCount(wire), ["maxTokens"] = 32 });
        // A synchronous trace sink may already have created a newer request.
        if (generation != m_FastLoopGeneration) yield break;
        using (var request = new UnityWebRequest(url, "POST")) {
            m_FastLoopRequest = request;
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(wire));
            request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 20;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            RaiseRequestDiagnostic(wire);
            if (generation != m_FastLoopGeneration) {
                if (ReferenceEquals(m_FastLoopRequest, request)) m_FastLoopRequest = null;
                yield break;
            }
            yield return request.SendWebRequest();
            if (ReferenceEquals(m_FastLoopRequest, request)) m_FastLoopRequest = null;
            bool current = generation == m_FastLoopGeneration;
            trace?.Mark(current ? "fast_task_control.transport_complete" : "fast_task_control.superseded", new JObject {
                ["generation"] = generation, ["httpStatus"] = request.responseCode,
                ["transportResult"] = request.result.ToString(), ["elapsedSeconds"] = clock.Elapsed.TotalSeconds });
            if (!current || generation != m_FastLoopGeneration) yield break;
            string body = request.downloadHandler?.text ?? "";
            string control = null, finish = "", reason = "http_failure";
            bool accepted = request.responseCode == 200 && request.result == UnityWebRequest.Result.Success &&
                FastTaskControlProtocol.TryReadResponse(body, out control, out finish, out reason);
            trace?.Mark(accepted ? "fast_task_control.accepted" : "fast_task_control.rejected", new JObject {
                ["generation"] = generation, ["accepted"] = accepted, ["control"] = accepted ? control : null,
                ["reason"] = reason, ["finishReason"] = finish, ["elapsedSeconds"] = clock.Elapsed.TotalSeconds });
            if (generation != m_FastLoopGeneration) yield break;
            callback?.Invoke(accepted ? control : null, reason);
        }
    }
}
