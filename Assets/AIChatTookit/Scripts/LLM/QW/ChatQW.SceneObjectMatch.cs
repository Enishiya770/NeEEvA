using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    private int m_SceneObjectMatchGeneration;
    private UnityWebRequest m_SceneObjectMatchRequest;
    public override bool SupportsSceneObjectMatchMessages => true;

    public override void PostSceneObjectMatchMessage(string input, string imageDataUrl,
        Action<bool, string, string> callback)
    {
        CancelSceneObjectMatchMessage();
        if (string.IsNullOrWhiteSpace(imageDataUrl))
        {
            callback?.Invoke(false, "", "scene-object-match-current-image-required");
            return;
        }
        int generation = m_SceneObjectMatchGeneration;
        // Snapshot only this request's facts/image, never shared prompt or historical image state.
        string json = BuildSceneObjectMatchRequestJson(input ?? "", imageDataUrl);
        StartCoroutine(RequestSceneObjectMatchPayload(json, generation, callback));
    }

    public override void CancelSceneObjectMatchMessage()
    {
        ++m_SceneObjectMatchGeneration;
        if (m_SceneObjectMatchRequest == null) return;
        try { if (!m_SceneObjectMatchRequest.isDone) m_SceneObjectMatchRequest.Abort(); } catch (Exception) { }
        m_SceneObjectMatchRequest = null;
    }

    private string BuildSceneObjectMatchRequestJson(string input, string imageDataUrl)
    {
        var sb = new StringBuilder("{\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":240");
        sb.Append(",\"response_format\":{\"type\":\"json_object\"");
        if (m_Backend == BackendType.Local) sb.Append(",\"schema\":").Append(RoomTaskProtocol.ObjectMatchSchema);
        sb.Append("},\"messages\":[");
        AppendMessage(sb, new SendData("system", RoomTaskProtocol.ObjectMatchContract + "\n" + RoomTaskProtocol.ObjectMatchSchema));
        sb.Append(',');
        AppendMessage(sb, new SendData("user", input ?? "") { imageDataUrl = imageDataUrl });
        sb.Append(']');
        if (m_Backend == BackendType.Local) sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        AppendSlot(sb, k_SlotAuxiliary); sb.Append('}');
        return sb.ToString();
    }

    private IEnumerator RequestSceneObjectMatchPayload(string json, int generation,
        Action<bool, string, string> callback)
    {
        if (generation != m_SceneObjectMatchGeneration) yield break;
        float started = Time.realtimeSinceStartup;
        using (var request = new UnityWebRequest(url, "POST"))
        {
            m_SceneObjectMatchRequest = request;
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 12; request.redirectLimit = 0;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            yield return request.SendWebRequest();
            if (ReferenceEquals(m_SceneObjectMatchRequest, request)) m_SceneObjectMatchRequest = null;
            PublishSceneObjectMatchCompletion(generation, request.result == UnityWebRequest.Result.Success,
                request.responseCode, request.downloadHandler.text, started, callback);
        }
    }

    private void PublishSceneObjectMatchCompletion(int generation, bool transportSuccess, long responseCode,
        string envelope, float started, Action<bool, string, string> callback)
    {
        // Cancellation cannot turn late success/failure into a semantic decision or revive its action.
        if (generation != m_SceneObjectMatchGeneration) return;
        bool ok = false; string output = "", status = "scene-object-match-http-failure";
        if (transportSuccess && responseCode == 200)
        {
            if (envelope != null && envelope.Length <= 200000)
                ok = TryReadRoomTaskCompletion(envelope, out output, out status);
            else status = "scene-object-match-invalid-envelope-size";
        }
        Debug.Log($"[Vision/ObjectMatchWire] code={responseCode} accepted={ok} elapsed={Time.realtimeSinceStartup-started:F3}s status={status}");
        // Offered-candidate membership and current scene ownership are checked by the scene caller.
        callback?.Invoke(ok, output, status);
    }
}
