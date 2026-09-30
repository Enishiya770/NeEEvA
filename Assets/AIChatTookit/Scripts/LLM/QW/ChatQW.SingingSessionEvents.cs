using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;
using Stopwatch = System.Diagnostics.Stopwatch;

public partial class ChatQW
{
    private UnityWebRequest m_SingingSessionEventRequest;
    private int m_SingingSessionEventGeneration;
    private const int SingingSessionEventTimeoutSeconds = 5;

    private void CancelSingingSessionEvent()
    {
        m_SingingSessionEventGeneration++;
        var request = m_SingingSessionEventRequest; m_SingingSessionEventRequest = null;
        if (request == null) return;
        try { if (!request.isDone) request.Abort(); }
        catch (Exception) { /* A finished/disposed read-only leaf has no authority. */ }
    }

    private string BuildSingingSessionEventRequestJson(string context, string currentUser)
    {
        if (m_Backend != BackendType.OpenAICompatible ||
            !SingingSessionEventProtocol.TryContext(context, out var facts, out var session)) return null;
        var sb = new StringBuilder(4096); sb.Append("{\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":256,\"response_format\":")
            .Append(SingingSessionEventProtocol.ResponseFormat(session).ToString(Formatting.None)).Append(",\"messages\":[");
        AppendMessage(sb, new SendData("system", SingingSessionEventProtocol.Contract)); sb.Append(',');
        AppendMessage(sb, new SendData("user", SingingSessionEventProtocol.Input(facts, session, currentUser).ToString(Formatting.None)));
        sb.Append(']'); AppendReasoningControls(sb, false); sb.Append('}');
        // The event leaf has its own request/generation and no Ephemeral or local
        // slot. Only OpenAICompatible is advertised for this parallel transport.
        return sb.ToString();
    }

    private bool TryPostSingingSessionPair(string context, string currentUser, string roleWire, int generation,
        Action<FastLoopDecision, string> callback, ConversationTrace trace, bool imageSent)
    {
        string eventWire = BuildSingingSessionEventRequestJson(context, currentUser);
        if (eventWire == null || !SingingSessionEventProtocol.TryContext(context, out _, out var session)) return false;
        int eventGeneration = m_SingingSessionEventGeneration;
        var frozenSession = (JObject)session.DeepClone();
        bool Current() => generation == m_FastLoopGeneration;
        var pair = new SingingSessionDecisionPair(Current, callback, CancelSingingSessionEvent,
            (name, fields) => trace?.Mark(name, fields));
        StartCoroutine(RequestFastLoopPayload(roleWire, currentUser, generation, pair.Role, trace, imageSent,
            FastLoopRequestPolicy.FromContext(SingingSessionEventProtocol.RoleContext(context))));
        if (Current() && eventGeneration == m_SingingSessionEventGeneration)
            StartCoroutine(RequestSingingSessionEvent(eventWire, currentUser, frozenSession, eventGeneration,
                generation, pair.Event, trace?.Child("singing-session-event")));
        return true;
    }

    private IEnumerator RequestSingingSessionEvent(string wire, string currentUser, JObject session,
        int generation, int fastGeneration, Action<SingingSessionUpdate, string> callback, ConversationTrace trace)
    {
        bool Current() => generation == m_SingingSessionEventGeneration && fastGeneration == m_FastLoopGeneration;
        if (!Current()) yield break;
        var clock = Stopwatch.StartNew();
        using (var request = new UnityWebRequest(url, "POST"))
        {
            m_SingingSessionEventRequest = request;
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(wire));
            request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = SingingSessionEventTimeoutSeconds;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            RaiseRequestDiagnostic(wire);
            if (!Current())
            { if (ReferenceEquals(m_SingingSessionEventRequest, request)) m_SingingSessionEventRequest = null; yield break; }
            trace?.Mark("singing_session.event_send", new JObject { ["generation"] = generation,
                ["fastGeneration"] = fastGeneration, ["requestBytes"] = Encoding.UTF8.GetByteCount(wire),
                ["timeoutSeconds"] = SingingSessionEventTimeoutSeconds, ["scope"] = "Read-only event classification, no role text or audio operation." });
            if (!Current())
            { if (ReferenceEquals(m_SingingSessionEventRequest, request)) m_SingingSessionEventRequest = null; yield break; }
            UnityWebRequestAsyncOperation operation = null;
            try { operation = request.SendWebRequest(); }
            catch (Exception) { /* A failed optional event leaf must not strand ordinary speech. */ }
            if (operation == null)
            {
                if (ReferenceEquals(m_SingingSessionEventRequest, request)) m_SingingSessionEventRequest = null;
                if (Current()) callback?.Invoke(null, "event-send-failed");
                yield break;
            }
            yield return operation;
            if (ReferenceEquals(m_SingingSessionEventRequest, request)) m_SingingSessionEventRequest = null;
            if (!Current()) yield break;
            SingingSessionUpdate update = null; string status = "event-transport-failed";
            if (request.result == UnityWebRequest.Result.Success && request.responseCode == 200)
                SingingSessionEventProtocol.TryReadResponse(request.downloadHandler.text, session, currentUser, out update, out status);
            trace?.Mark("singing_session.event_complete", new JObject { ["generation"] = generation,
                ["fastGeneration"] = fastGeneration, ["status"] = status, ["httpStatus"] = request.responseCode,
                ["operation"] = update?.Operation, ["take"] = update?.Take,
                ["elapsedSeconds"] = clock.Elapsed.TotalSeconds });
            if (Current()) callback?.Invoke(update, status);
        }
    }
}
