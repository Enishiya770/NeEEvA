using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    private int m_RoomTaskRequestGeneration;
    private UnityWebRequest m_RoomTaskRequest;
    public override bool SupportsRoomTaskMessages => true;

    public override void PostRoomTaskMessage(string input, bool reviewSpeech, Action<bool, string, string> callback)
    {
        CancelRoomTaskMessage();
        int generation = m_RoomTaskRequestGeneration;
        StartCoroutine(RequestRoomTask(input ?? "", reviewSpeech, generation, callback));
    }

    private void CancelRoomTaskMessage()
    {
        ++m_RoomTaskRequestGeneration;
        if (m_RoomTaskRequest == null) return;
        try { if (!m_RoomTaskRequest.isDone) m_RoomTaskRequest.Abort(); } catch (Exception) { }
        m_RoomTaskRequest = null;
    }

    private IEnumerator RequestRoomTask(string input, bool review, int generation, Action<bool, string, string> callback)
    {
        float start = Time.realtimeSinceStartup;
        using (var request = new UnityWebRequest(url, "POST"))
        {
            m_RoomTaskRequest = request;
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(BuildRoomTaskRequestJson(input, review)));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = 12;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            yield return request.SendWebRequest();
            if (ReferenceEquals(m_RoomTaskRequest, request)) m_RoomTaskRequest = null;
            PublishRoomTaskCompletion(generation, request.result == UnityWebRequest.Result.Success,
                request.responseCode, request.downloadHandler.text, review, start, callback);
        }
    }

    private string BuildRoomTaskRequestJson(string input, bool review)
    {
        string schema = review ? RoomTaskProtocol.ReviewSchema : RoomTaskProtocol.DecisionSchema;
        string contract = review ? RoomTaskProtocol.ReviewContract : RoomTaskProtocol.DecisionContract;
        var sb = new StringBuilder("{\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":240");
        sb.Append(",\"response_format\":{\"type\":\"json_object\"");
        if (m_Backend == BackendType.Local) sb.Append(",\"schema\":").Append(schema);
        sb.Append("},\"messages\":[");
        AppendMessage(sb, new SendData("system", contract + "\n" + schema)); sb.Append(',');
        AppendMessage(sb, new SendData("user", input ?? "")); sb.Append(']');
        if (m_Backend == BackendType.Local) sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        AppendSlot(sb, k_SlotAuxiliary); sb.Append('}');
        return sb.ToString();
    }

    private static bool TryReadRoomTaskCompletion(string envelope, out string output, out string status)
    {
        output = ""; status = "room-task-invalid-envelope";
        try
        {
            JObject body = JObject.Parse(envelope ?? "", new JsonLoadSettings {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            var choices = body["choices"] as JArray;
            if (choices == null || choices.Count != 1 || !(choices[0] is JObject choice)) return false;
            if (choice["finish_reason"]?.Type != JTokenType.String || (string)choice["finish_reason"] != "stop")
            { status = "room-task-incomplete-output"; return false; }
            var message = choice["message"] as JObject;
            if (message == null || (string)message["role"] != "assistant" ||
                message["content"]?.Type != JTokenType.String ||
                (message["tool_calls"] != null && message["tool_calls"].Type != JTokenType.Null) ||
                (message["function_call"] != null && message["function_call"].Type != JTokenType.Null) ||
                (message["refusal"] != null && message["refusal"].Type != JTokenType.Null &&
                    (message["refusal"].Type != JTokenType.String || !string.IsNullOrEmpty((string)message["refusal"]))))
                return false;
            string content = (string)message["content"];
            if (string.IsNullOrWhiteSpace(content)) { status = "room-task-empty-output"; return false; }
            output = content; status = "received"; return true;
        }
        catch (Exception) { return false; }
    }

    private void PublishRoomTaskCompletion(int generation, bool transportSuccess, long responseCode,
        string envelope, bool review, float startedAt, Action<bool, string, string> callback)
    {
        // Cancellation invalidates both successful and failed late completions.
        // Auxiliary decisions/reviews never become accepted chat history.
        if (generation != m_RoomTaskRequestGeneration) return;
        bool ok = false; string output = "", status = "room-task-http-failure";
        if (transportSuccess && responseCode == 200)
            ok = TryReadRoomTaskCompletion(envelope, out output, out status);
        Debug.Log($"[Room/TaskWire] kind={(review ? "speech-review" : "decision")} code={responseCode} accepted={ok} elapsed={Time.realtimeSinceStartup-startedAt:F3}s status={status}");
        callback?.Invoke(ok, output, status);
    }
}
