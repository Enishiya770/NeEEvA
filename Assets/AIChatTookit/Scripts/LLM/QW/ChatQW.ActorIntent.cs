using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using Stopwatch = System.Diagnostics.Stopwatch;

/// <summary>A bounded judge of an actor's draft or a user condition signal.
/// It never authors public speech or submits actions.</summary>
public partial class ChatQW : IActorIntentBackend, IActorIntentReviewBackend
{
    private UnityWebRequest m_ActorIntentRequest;
    private int m_ActorIntentGeneration;

    public bool SupportsActorIntents => m_EnableRoleDecisionFastLane;

    public void PostActorIntent(string context, string draft, string currentUser,
        Action<ActorIntentDecision, string> callback, ConversationTrace trace = null)
    {
        CancelActorIntent();
        if (!SupportsActorIntents)
        {
            callback?.Invoke(null, "actor_intent_disabled");
            return;
        }
        AbortPrewarmIfRunning();
        int generation = m_ActorIntentGeneration;
        string wire = BuildActorIntentRequestJson(context, draft, currentUser, false);
        StartCoroutine(RequestActorIntentPayload(wire, draft ?? "", currentUser ?? "", false,
            generation, callback, null, trace ?? TraceContext?.Child("llm-actor-intent")));
    }

    public void PostActorConditionSignal(string context, string currentUser,
        Action<string, string> callback, ConversationTrace trace = null)
    {
        CancelActorIntent();
        if (!SupportsActorIntents)
        {
            callback?.Invoke(null, "actor_intent_disabled");
            return;
        }
        AbortPrewarmIfRunning();
        int generation = m_ActorIntentGeneration;
        string wire = BuildActorIntentRequestJson(context, "", currentUser, true);
        StartCoroutine(RequestActorIntentPayload(wire, "", currentUser ?? "", true,
            generation, null, callback, trace ?? TraceContext?.Child("llm-actor-condition")));
    }

    public void PostActorIntentReview(string context, string draft, string currentUser,
        Action<ActorIntentReviewDecision, string> callback, ConversationTrace trace = null)
    {
        CancelActorIntent();
        if (!SupportsActorIntents) { callback?.Invoke(null, "actor_intent_disabled"); return; }
        AbortPrewarmIfRunning();
        int generation = m_ActorIntentGeneration;
        string wire = BuildActorIntentReviewRequestJson(context, draft, currentUser);
        StartCoroutine(RequestActorIntentPayload(wire, draft ?? "", currentUser ?? "", false,
            generation, null, null, trace ?? TraceContext?.Child("llm-actor-review"), callback));
    }

    public void CancelActorIntent()
    {
        m_ActorIntentGeneration++;
        var request = m_ActorIntentRequest;
        m_ActorIntentRequest = null;
        if (request == null) return;
        try { if (!request.isDone) request.Abort(); }
        catch (Exception) { /* A disposed or completed request cannot publish. */ }
    }

    private string BuildActorIntentRequestJson(string context, string draft, string currentUser, bool signal) =>
        BuildActorIntentPayload(context, draft, currentUser, signal, false);

    private string BuildActorIntentReviewRequestJson(string context, string draft, string currentUser) =>
        BuildActorIntentPayload(context, draft, currentUser, false, true);

    private string BuildActorIntentPayload(string context, string draft, string currentUser, bool signal, bool review)
    {
        // The judge receives only its own contract and explicit data. It must not
        // inherit the actor persona, behavior manual, language or tool prompts.
        string system = review ? ActorIntentProtocol.ReviewSystemPrompt : signal ? ActorIntentProtocol.SignalContract : ActorIntentProtocol.SystemPrompt;
        string user = ActorIntentProtocol.BuildUser(context ?? "", signal ? "" : draft ?? "", currentUser ?? "")
            .ToString(Formatting.None);
        var format = review ? ActorIntentProtocol.ReviewResponseFormat() : signal ? ActorIntentProtocol.SignalResponseFormat() : ActorIntentProtocol.ResponseFormat();
        var sb = new StringBuilder(Mathf.Max(2048, system.Length + user.Length + 1024));
        sb.Append("{\"model\":");
        AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":384");
        sb.Append(",\"response_format\":").Append(format.ToString(Formatting.None));
        sb.Append(",\"messages\":[");
        AppendMessage(sb, new SendData("system", system));
        sb.Append(',');
        AppendMessage(sb, new SendData("user", user));
        sb.Append(']');
        AppendReasoningControls(sb, false);
        AppendSlot(sb, k_SlotAuxiliary);
        sb.Append('}');
        return sb.ToString();
    }

    private IEnumerator RequestActorIntentPayload(string wire, string draft, string currentUser, bool signal,
        int generation, Action<ActorIntentDecision, string> onDecision,
        Action<string, string> onSignal, ConversationTrace trace, Action<ActorIntentReviewDecision, string> onReview = null)
    {
        if (generation != m_ActorIntentGeneration) yield break;
        bool review = onReview != null;
        string requestKind = review ? "semantic-review" : signal ? "condition" : "intent";
        var clock = Stopwatch.StartNew();
        trace?.Mark("actor_intent.send", new JObject {
            ["kind"] = requestKind, ["generation"] = generation, ["backend"] = m_Backend.ToString(),
            ["slot"] = m_Backend == BackendType.Local ? (int?)k_SlotAuxiliary : null,
            ["requestBytes"] = Encoding.UTF8.GetByteCount(wire), ["maxTokens"] = 384, ["stream"] = false
        });
        using (var request = new UnityWebRequest(url, "POST"))
        {
            m_ActorIntentRequest = request;
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(wire));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = 20;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            RaiseRequestDiagnostic(wire);
            // Observers may synchronously cancel or replace this request.
            if (generation != m_ActorIntentGeneration)
            {
                if (ReferenceEquals(m_ActorIntentRequest, request)) m_ActorIntentRequest = null;
                yield break;
            }
            yield return request.SendWebRequest();
            if (ReferenceEquals(m_ActorIntentRequest, request)) m_ActorIntentRequest = null;
            bool current = generation == m_ActorIntentGeneration;
            trace?.Mark(current ? "actor_intent.transport_complete" : "actor_intent.superseded", new JObject {
                ["kind"] = requestKind, ["generation"] = generation,
                ["httpStatus"] = request.responseCode, ["transportResult"] = request.result.ToString(),
                ["elapsedSeconds"] = clock.Elapsed.TotalSeconds
            });
            if (!current || generation != m_ActorIntentGeneration) yield break;

            string body = request.downloadHandler?.text ?? "";
            string raw = "", finishReason = "", reason = "http_failure", signalValue = null;
            ActorIntentDecision decision = null;
            ActorIntentReviewDecision reviewed = null;
            bool accepted = request.responseCode == 200 && request.result == UnityWebRequest.Result.Success &&
                TryReadActorIntentEnvelope(body, out raw, out finishReason, out reason);
            if (accepted)
                accepted = review ? ActorIntentProtocol.TryParseReview(raw, draft, out reviewed, out reason) : signal
                    ? ActorIntentProtocol.TryParseSignal(raw, currentUser, out signalValue, out reason)
                    : ActorIntentProtocol.TryCompile(raw, draft, out decision, out reason);
            if (accepted) reason = "ok";
            var performance = new CompletionPerformance();
            if (body.Length <= 65536) performance.Observe(body);
            var diagnostic = new JObject {
                ["kind"] = requestKind, ["generation"] = generation, ["accepted"] = accepted,
                ["reason"] = reason, ["finishReason"] = finishReason,
                ["protocolVersion"] = review ? ActorIntentProtocol.ReviewVersion : signal ? ActorIntentProtocol.SignalVersion : ActorIntentProtocol.IntentVersion,
                ["reviewStatus"] = reviewed?.Status,
                ["reviewTrigger"] = reviewed?.Trigger,
                ["reviewTask"] = reviewed?.Task,
                ["reviewCoverage"] = reviewed?.Coverage,
                ["domain"] = decision?.Domain,
                ["requiresFullReview"] = decision?.RequiresFullReview == true,
                ["reviewReason"] = decision?.ReviewReason,
                ["intent"] = decision?.Kind,
                ["action"] = decision?.Action,
                ["condition"] = decision?.Condition,
                ["task"] = decision?.Task,
                ["target"] = decision?.Target,
                ["signal"] = accepted && signal ? signalValue : null,
                ["responseBytes"] = Encoding.UTF8.GetByteCount(body),
                ["elapsedSeconds"] = clock.Elapsed.TotalSeconds, ["server"] = performance.Snapshot()
            };
            // Log only bounded verdicts and timing, never credentials, evidence,
            // draft text or the provider response body.
            trace?.Mark(accepted ? "actor_intent.accepted" : "actor_intent.rejected", diagnostic);
            if (m_LogRequestStats) Debug.Log("[LLM/ActorIntentWire] " + diagnostic.ToString(Formatting.None));
            if (generation != m_ActorIntentGeneration) yield break;
            if (review) onReview(accepted ? reviewed : null, reason);
            else if (signal) onSignal?.Invoke(accepted ? signalValue : null, reason);
            else onDecision?.Invoke(accepted ? decision : null, reason);
        }
    }

    private static bool TryReadActorIntentEnvelope(string envelope,
        out string raw, out string finishReason, out string reason)
    {
        raw = "";
        finishReason = "";
        reason = "invalid_envelope";
        if (string.IsNullOrWhiteSpace(envelope) || envelope.Length > 65536)
        {
            reason = "response_size_invalid";
            return false;
        }
        try
        {
            var response = JObject.Parse(envelope, new JsonLoadSettings {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            });
            if (!(response["choices"] is JArray choices) || choices.Count != 1 ||
                !(choices[0] is JObject choice) || !(choice["message"] is JObject message))
                return false;
            var finish = choice["finish_reason"];
            if (finish?.Type != JTokenType.String)
            {
                reason = "missing_finish";
                return false;
            }
            string value = (string)finish;
            finishReason = value == "stop" || value == "length" || value == "tool_calls" ||
                value == "content_filter" ? value : "other";
            if (value != "stop")
            {
                reason = "finish_not_stop";
                return false;
            }
            if (message["content"]?.Type != JTokenType.String)
            {
                reason = "missing_message_content";
                return false;
            }
            if (message["role"] != null && (message["role"].Type != JTokenType.String ||
                (string)message["role"] != "assistant"))
            {
                reason = "unexpected_message_role";
                return false;
            }
            var tools = message["tool_calls"];
            var function = message["function_call"];
            var refusal = message["refusal"];
            if (tools != null && tools.Type != JTokenType.Null && !(tools is JArray emptyTools && emptyTools.Count == 0) ||
                function != null && function.Type != JTokenType.Null ||
                refusal != null && refusal.Type != JTokenType.Null &&
                    !(refusal.Type == JTokenType.String && string.IsNullOrEmpty((string)refusal)))
            {
                reason = "unexpected_message_channel";
                return false;
            }
            raw = (string)message["content"];
            reason = "ok";
            return true;
        }
        catch (JsonException) { reason = "invalid_json_envelope"; }
        catch (ArgumentException) { reason = "invalid_envelope_argument"; }
        catch (InvalidOperationException) { reason = "invalid_envelope_operation"; }
        return false;
    }
}
