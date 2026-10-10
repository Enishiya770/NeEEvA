using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    private int m_FirstReplyOwnedGeneration = -1;
    private const float k_FirstReplyReviewSeconds = 6f;
    private FirstReplyUserLease m_FirstReplyUserLease;
    private sealed class FirstReplyUserLease
    {
        internal int Generation;
        internal SendData User;
        internal bool CommitHistory, Settled, HandoffPending;
    }
    public override bool SupportsFirstReplyRouting => m_Backend == BackendType.Local;

    private void OnDisable() => CancelOwnedFirstReply();
    private void OnApplicationPause(bool paused) { if (paused) CancelOwnedFirstReply(); }
    private void CancelOwnedFirstReply()
    {
        if (m_FirstReplyOwnedGeneration < 0 || m_FirstReplyOwnedGeneration != m_StreamRequestGeneration) return;
        m_FirstReplyOwnedGeneration = -1;
        CancelActiveResponse();
    }

    public override void PostFirstReplyStream(string message, Action<SpeechText> onSpeech,
        Action<FirstReplyCompletion> onComplete, string imageDataUrl = null,
        string transientContext = null, bool commitHistory = true)
    {
        if (!SupportsFirstReplyRouting)
        { onComplete?.Invoke(FirstReplyCompletion.Failed("first-reply-provider-unsupported")); return; }
        AbortPrewarmIfRunning();
        CancelEphemeralMsg();
        CancelTurnBoundaryMsg();
        CancelActiveResponse();
        int generation = m_StreamRequestGeneration;
        m_FirstReplyOwnedGeneration = generation;
        // This is a real accepted input, not a listening draft. Its exact object
        // survives cancellation and the asynchronous tool-planning interval. The
        // legacy formal request later claims this exact object, without re-adding it.
        var entry = new SendData("user", message ?? "") { imageDataUrl = imageDataUrl };
        BeginFirstReplyUserLease(entry, generation, commitHistory);
        string context = transientContext ?? RequestContext ?? "";
        RequestContext = "";
        StartCoroutine(RequestFirstReply(entry, generation, context, onSpeech, onComplete, commitHistory));
    }

    private List<SendData> CreateFirstReplyHistory(SendData current, string context)
    {
        var source = new List<SendData>(m_DataList);
        // Legacy providers stored persona in every user message; keep the current
        // question verbatim and supply that same fallback persona as system here.
        if (!HasPromptFiles && !string.IsNullOrWhiteSpace(m_Prompt) && !source.Exists(m => m != null && m.role == "system"))
            source.Insert(0, new SendData("system", "当前为角色的人设设定：" + m_Prompt + " 回复的语言：" + lan));
        if (!source.Contains(current)) source.Add(current);
        string skill = FormalResponseContextPolicy.SkillContext(FormalResponseStage.FirstReplyRouting,
            ActiveSkillContext, SkillCatalogContext);
        return CreateStageRequestHistoryFrom(source, context + "\n" + FirstReplyProtocol.RouteContract +
            "\n" + RoleOutputPublicBoundary.OutputContract, null, skill);
    }

    // Called by production and the compiled transport fixture. It never edits
    // provider history, current image metadata, user input or the supplied facts.
    private string BuildFirstReplyWire(List<SendData> history, string context, bool preparation)
    {
        var wireHistory = new List<SendData>();
        int currentUser = history.FindLastIndex(m => m != null && m.role == "user");
        for (int i = 0; i < history.Count; i++)
        {
            var value = history[i];
            if (value == null) continue;
            // Historical pixels are not a current observation. The route phase
            // carries only an explicitly supplied current image; otherwise scene
            // questions return to the existing fresh-image/tool pipeline.
            bool oldImage = i != currentUser && !string.IsNullOrEmpty(value.imageDataUrl);
            wireHistory.Add(new SendData(value.role, value.content) {
                imageDataUrl = oldImage ? null : value.imageDataUrl,
                imageArchived = value.imageArchived || oldImage });
        }
        int stableAt = 0;
        while (stableAt < wireHistory.Count && wireHistory[stableAt].role == "system") stableAt++;
        wireHistory.Insert(stableAt, new SendData("system", FirstReplyProtocol.RouteContract +
            "\n\n" + RoleOutputPublicBoundary.OutputContract));
        var wire = JObject.Parse(BuildStageRequestJsonForMessages(wireHistory, true,
            context, null, FormalResponseStage.FirstReplyRouting));
        // This opt-in protocol requires the native grammar. Cloud/legacy traffic is unchanged.
        wire["grammar"] = FirstReplyProtocol.Grammar;
        wire["max_tokens"] = 768;
        wire["enable_thinking"] = false;
        wire["chat_template_kwargs"] = new JObject { ["enable_thinking"] = false };
        wire["id_slot"] = preparation ? k_SlotAuxiliary : k_SlotMainConversation;
        return wire.ToString(Formatting.None);
    }

    private IEnumerator RequestFirstReply(SendData current, int generation, string context,
        Action<SpeechText> onSpeech, Action<FirstReplyCompletion> onComplete, bool commitHistory)
    {
        bool Current() => generation == m_StreamRequestGeneration;
        if (!Current()) yield break;
        var userLease = BeginFirstReplyUserLease(current, generation, commitHistory);
        context = CompactScenePromptContext(context, true);
        List<SendData> history = CreateFirstReplyHistory(current, context);
        string wire = BuildFirstReplyWire(history, context, !commitHistory);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timing = new ChatRequestWireTiming(generation, commitHistory ? "first-reply" : "first-reply-preparation");
        // Syntax-valid ordinary output is still only a candidate. Nothing leaves
        // this request (including assistant history) before semantic route review.
        var parser = new FirstReplyStreamParser(Current, null);
        int estimate = EstimateStageRequestTokens(history, context + FirstReplyProtocol.RouteContract +
            RoleOutputPublicBoundary.OutputContract,
            FormalResponseContextPolicy.SkillContext(FormalResponseStage.FirstReplyRouting, ActiveSkillContext, SkillCatalogContext));
        using (var request = new UnityWebRequest(url, "POST"))
        {
            m_ActiveStreamRequest = request;
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(wire));
            var handler = new SSEDownloadHandler(delta => {
                if (!Current()) return;
                timing.MarkContent(clock.Elapsed.TotalSeconds);
                parser.Push(delta);
            });
            handler.OnFirstData = () => { if (Current()) timing.MarkBytes(clock.Elapsed.TotalSeconds); };
            request.downloadHandler = handler;
            request.timeout = 25;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "text/event-stream");
            request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            RaiseRequestDiagnostic(wire);
            // A diagnostic subscriber may cancel; do not send its stale request.
            if (!Current()) { if (ReferenceEquals(m_ActiveStreamRequest, request)) m_ActiveStreamRequest = null; yield break; }
            timing.MarkSent(clock.Elapsed.TotalSeconds, Time.realtimeSinceStartupAsDouble, DateTime.UtcNow.ToString("O"));
            if (m_LogRequestStats) Debug.Log("[LLM/HttpTiming] " + timing.Snapshot("sent", null, null).ToString(Formatting.None));
            yield return request.SendWebRequest();
            timing.MarkComplete(clock.Elapsed.TotalSeconds);
            if (ReferenceEquals(m_ActiveStreamRequest, request)) m_ActiveStreamRequest = null;
            if (!Current()) yield break;
            bool success = request.responseCode == 200 && request.result == UnityWebRequest.Result.Success;
            var completion = parser.Finish(success, handler.FinishReason);
            if (!Current() || completion == null) yield break;
            completion.SpeechDelivered = false;
            var candidateRoute = completion.Disposition;
            RaiseRawResponse(handler.GetFullContent());
            if (!Current()) yield break;
            double reviewSeconds = 0;
            string reviewStatus = "not-required";
            if (completion.Disposition != FirstReplyDisposition.Failed)
            {
                string reviewWire = BuildFirstReplyReviewWire(current, history, context, completion);
                string reviewedRoute = null;
                string requestKind = null;
                string responseDemand = null;
                bool? candidateSufficient = null;
                JObject questionUnderstanding = null;
                double reviewStart = clock.Elapsed.TotalSeconds;
                if (reviewWire == null) reviewStatus = "first-reply-review-input-budget";
                else yield return RequestFirstReplyReview(reviewWire, generation,
                    (route, understanding, kind, sufficient, demand, status) => { reviewedRoute = route; questionUnderstanding = understanding; requestKind = kind; candidateSufficient = sufficient; responseDemand = demand; reviewStatus = status; });
                reviewSeconds = clock.Elapsed.TotalSeconds - reviewStart;
                if (!Current()) yield break;
                completion = FirstReplyProtocol.ResolveReviewedCompletion(completion, reviewedRoute, reviewStatus, questionUnderstanding, requestKind, candidateSufficient, responseDemand);
                if (completion.Disposition == FirstReplyDisposition.Ordinary)
                    foreach (var part in completion.Speech)
                    {
                        if (!Current()) yield break;
                        timing.MarkSpeech(clock.Elapsed.TotalSeconds);
                        onSpeech?.Invoke(part);
                        completion.SpeechDelivered |= !string.IsNullOrEmpty(part.Text);
                    }
            }
            if (!Current()) yield break;
            SettleFirstReplyUserLease(userLease, completion, history);
            if (success)
            {
                int imageTokens = ImageTokenAllowance(history);
                ObservePromptUsage(handler.PromptTokens, estimate - imageTokens, imageTokens > 0);
            }
            if (m_LogRequestStats)
                Debug.Log("[LLM/FirstReply] " + new JObject {
                    ["requestGeneration"] = generation,
                    ["candidateRoute"] = candidateRoute.ToString(),
                    ["route"] = completion.Disposition.ToString(), ["failure"] = completion.Failure,
                    ["requestKind"] = completion.RequestKind,
                    ["responseDemand"] = completion.ResponseDemand,
                    ["candidateSufficient"] = completion.CandidateSufficient,
                    ["finishReason"] = handler.FinishReason,
                    ["responseCharacters"] = handler.GetFullContent().Length,
                    ["maximumResponseTokens"] = 768,
                    ["maximumSpeechSegments"] = FirstReplyProtocol.MaximumSpeechSegments,
                    ["maximumResponseCharacters"] = FirstReplyProtocol.MaximumResponseCharacters,
                    ["preparation"] = !commitHistory, ["publicDeltaDelivered"] = completion.SpeechDelivered,
                    ["speechCharacters"] = completion.SpeechCharacters,
                    ["semanticReviewSeconds"] = reviewSeconds, ["semanticReviewStatus"] = reviewStatus,
                    ["totalThroughReleaseSeconds"] = clock.Elapsed.TotalSeconds,
                    ["estimatedPromptTokens"] = estimate, ["server"] = handler.Performance.Snapshot(),
                    ["http"] = timing.Snapshot("completed", request.responseCode, request.result.ToString()),
                    ["scope"] = "Provider HTTP and validated public deltas, not actual audible PCM or user-stop latency. All valid candidate routes require independent semantic review. Tools/evidence handoffs retain their accepted user, with no public/assistant output. Evidence grants no action permission."
                }.ToString(Formatting.None));
            onComplete?.Invoke(completion);
        }
        if (m_FirstReplyOwnedGeneration == generation) m_FirstReplyOwnedGeneration = -1;
    }

    // A small independent task, without persona, executable skills, old images or
    // the generator's route claim. Preserve the whole latest user and each quoted
    // history entry; oversized mandatory input hands back to the normal pipeline.
    private string BuildFirstReplyReviewWire(SendData current, List<SendData> history,
        string context, FirstReplyCompletion candidate)
    {
        var dialogue = new JArray();
        int characters = 0;
        bool incomplete = false;
        int end = history.IndexOf(current);
        if (end < 0) end = history.Count;
        for (int i = end - 1; i >= 0; --i)
        {
            var row = history[i];
            if (row == null || (row.role != "user" && row.role != "assistant")) continue;
            string text = row.content ?? "";
            if (dialogue.Count >= 6 || characters + text.Length > 4000) { incomplete = true; break; }
            dialogue.Insert(0, new JObject { ["role"] = row.role, ["text"] = text });
            characters += text.Length;
        }
        var speech = new JArray();
        foreach (var part in candidate.Speech)
            speech.Add(new JObject { ["language"] = part.LanguageCode, ["text"] = part.Text });
        // Preserve typed runtime facts, including explicit false and null, rather
        // than nesting a second escaped JSON document inside the review input.
        // Unknown legacy text remains intact; ambiguous duplicate keys are text.
        JToken runtimeContext = new JValue(context ?? "");
        try
        {
            runtimeContext = JObject.Parse(context ?? "", new JsonLoadSettings {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        catch (JsonException) { }
        string input = new JObject {
            ["latestUserText"] = current.content ?? "", ["recentDialogue"] = dialogue,
            ["recentDialogueIncomplete"] = incomplete, ["runtimeContext"] = runtimeContext,
            ["candidateSpeech"] = speech
        }.ToString(Formatting.None);
        if (input.Length > FirstReplyProtocol.MaximumReviewInputCharacters) return null;
        var wire = new StringBuilder("{\"model\":"); AppendJsonString(wire, CurrentModelName);
        wire.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":")
            .Append(FirstReplyProtocol.ReviewMaximumOutputTokens).Append(",\"response_format\":{\"type\":\"json_object\",\"schema\":")
            .Append(FirstReplyProtocol.ReviewSchema).Append("},\"messages\":[");
        AppendMessage(wire, new SendData("system", FirstReplyProtocol.ReviewContract));
        wire.Append(','); AppendMessage(wire, new SendData("user", input)); wire.Append(']');
        wire.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        AppendSlot(wire, k_SlotAuxiliary); wire.Append('}');
        return wire.ToString();
    }

    private IEnumerator RequestFirstReplyReview(string wire, int generation, Action<string, JObject, string, bool?, string, string> callback)
    {
        bool Current() => generation == m_StreamRequestGeneration;
        if (!Current()) yield break;
        float started = Time.realtimeSinceStartup;
        using (var request = new UnityWebRequest(url, "POST"))
        {
            m_ActiveStreamRequest = request;
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(wire));
            request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = (int)k_FirstReplyReviewSeconds;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            RaiseRequestDiagnostic(wire);
            if (!Current()) { if (ReferenceEquals(m_ActiveStreamRequest, request)) m_ActiveStreamRequest = null; yield break; }
            var operation = request.SendWebRequest();
            while (!operation.isDone && Current() && Time.realtimeSinceStartup - started < k_FirstReplyReviewSeconds)
                yield return null;
            bool expired = Time.realtimeSinceStartup - started >= k_FirstReplyReviewSeconds;
            if (!operation.isDone) request.Abort();
            if (ReferenceEquals(m_ActiveStreamRequest, request)) m_ActiveStreamRequest = null;
            if (!Current()) yield break;
            string output = "", route = null, status = expired ? "first-reply-review-timeout" : "first-reply-review-http-failure";
            JObject questionUnderstanding = null;
            string requestKind = null;
            string rawRoute = null;
            string responseDemand = null;
            bool? candidateSufficient = null;
            bool received = !expired && request.responseCode == 200 && request.result == UnityWebRequest.Result.Success &&
                TryReadRoomTaskCompletion(request.downloadHandler.text, out output, out status);
            if (received && FirstReplyProtocol.TryCurrentReview(output, out route, out questionUnderstanding, out requestKind, out candidateSufficient, out responseDemand))
            { status = "reviewed-" + route; rawRoute = (string)JObject.Parse(output)["route"]; }
            else if (received) status = "first-reply-review-invalid-output";
            Debug.Log("[LLM/FirstReplyReview] " + new JObject {
                ["requestGeneration"] = generation, ["route"] = route, ["status"] = status,
                ["requestKind"] = requestKind,
                ["candidateSufficient"] = candidateSufficient, ["rawRoute"] = rawRoute,
                ["responseDemand"] = responseDemand,
                ["elapsed"] = Time.realtimeSinceStartup - started, ["rawResponse"] = output
            }.ToString(Formatting.None));
            callback?.Invoke(route, questionUnderstanding, requestKind, candidateSufficient, responseDemand, status);
        }
    }

    private FirstReplyUserLease BeginFirstReplyUserLease(SendData current, int generation, bool commitHistory)
    {
        if (m_FirstReplyUserLease != null && m_FirstReplyUserLease.Generation == generation &&
            ReferenceEquals(m_FirstReplyUserLease.User, current)) return m_FirstReplyUserLease;
        var lease = new FirstReplyUserLease { Generation = generation, User = current, CommitHistory = commitHistory };
        m_FirstReplyUserLease = lease;
        if (commitHistory) m_DataList.Add(current);
        return lease;
    }

    private void SettleFirstReplyUserLease(FirstReplyUserLease lease, FirstReplyCompletion completion, List<SendData> history)
    {
        if (lease == null || lease.Settled || !ReferenceEquals(lease, m_FirstReplyUserLease) ||
            lease.Generation != m_StreamRequestGeneration || completion == null) return;
        lease.Settled = true;
        if (!lease.CommitHistory) return;
        if (completion.Disposition == FirstReplyDisposition.Ordinary)
        {
            m_DataList.Add(new SendData("assistant", MergeSpokenPrefix(completion.SafeOutput)));
            CommitRequestHistory(history);
            CheckHistory();
            return;
        }
        // Keep the accepted user while the asynchronous planner runs. Removing it
        // here loses that turn if the user interrupts before legacy formal dispatch.
        if (!completion.SpeechDelivered && (completion.Disposition == FirstReplyDisposition.Tools ||
            completion.Disposition == FirstReplyDisposition.Evidence ||
            completion.Disposition == FirstReplyDisposition.Failed)) lease.HandoffPending = true;
    }

    // Called BEFORE the legacy sender cancels its previous provider request. An
    // intervening cancellation/new real turn changes Generation, invalidating the
    // handoff without deleting history. Text equality never establishes ownership.
    private SendData ClaimFirstReplyUserHandoff()
    {
        var lease = m_FirstReplyUserLease;
        if (lease == null || !lease.CommitHistory || !lease.Settled || !lease.HandoffPending ||
            lease.Generation != m_StreamRequestGeneration || !m_DataList.Contains(lease.User)) return null;
        lease.HandoffPending = false;
        return lease.User;
    }
}
