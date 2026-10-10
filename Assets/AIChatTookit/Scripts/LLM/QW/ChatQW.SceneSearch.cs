using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    private int m_SceneSearchGeneration;
    private UnityWebRequest m_SceneSearchRequest;
    public override bool SupportsSceneSearchMessages => true;

    public override void PostSceneSearchDecisionMessage(string input, string imageDataUrl, Action<bool, string, string> callback)
    {
        CancelSceneSearchDecisionMessage();
        if (string.IsNullOrEmpty(imageDataUrl) || !imageDataUrl.StartsWith("data:image/", StringComparison.Ordinal))
        { callback?.Invoke(false, "", "scene-search-image-unavailable"); return; }
        StartCoroutine(RequestSceneSearch(BuildSceneSearchRequestJson(input, imageDataUrl), m_SceneSearchGeneration, callback));
    }

    public override void CancelSceneSearchDecisionMessage()
    {
        ++m_SceneSearchGeneration;
        var request = m_SceneSearchRequest; m_SceneSearchRequest = null;
        if (request != null) try { if (!request.isDone) request.Abort(); } catch (Exception) { }
    }

    private string BuildSceneSearchRequestJson(string input, string imageDataUrl)
    {
        string schema = SceneSearchProtocol.SchemaForInput(input);
        var sb = new StringBuilder("{\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":768,\"response_format\":{\"type\":\"json_object\"");
        if (m_Backend == BackendType.Local) sb.Append(",\"schema\":").Append(schema);
        sb.Append("},\"messages\":[");
        AppendMessage(sb, new SendData("system", SceneSearchProtocol.Contract + "\n" + schema)); sb.Append(',');
        AppendMessage(sb, new SendData("user", input ?? "") { imageDataUrl = imageDataUrl });
        sb.Append(']');
        if (m_Backend == BackendType.Local) sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        AppendSlot(sb, k_SlotAuxiliary); sb.Append('}');
        return sb.ToString();
    }

    private IEnumerator RequestSceneSearch(string payload, int generation, Action<bool, string, string> callback)
    {
        if (generation != m_SceneSearchGeneration) yield break;
        float started = Time.realtimeSinceStartup;
        using (var request = new UnityWebRequest(url, "POST"))
        {
            m_SceneSearchRequest = request;
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 12;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            yield return request.SendWebRequest();
            if (ReferenceEquals(m_SceneSearchRequest, request)) m_SceneSearchRequest = null;
            if (generation != m_SceneSearchGeneration) yield break;
            string output = "", status = "scene-search-http-failure";
            bool valid = request.result == UnityWebRequest.Result.Success && request.responseCode == 200 &&
                TryReadRoomTaskCompletion(request.downloadHandler.text, out output, out status) &&
                SceneSearchProtocol.TryDecision(output, out _, out status);
            Debug.Log($"[SceneSearch/Wire] code={request.responseCode} accepted={valid} elapsed={Time.realtimeSinceStartup-started:F3}s status={status}");
            callback?.Invoke(valid, output, valid ? "received" : status);
        }
    }
}
