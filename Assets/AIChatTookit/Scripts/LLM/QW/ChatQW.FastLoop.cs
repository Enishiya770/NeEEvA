using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using Stopwatch = System.Diagnostics.Stopwatch;

/// <summary>Bounded phase-one decisions. This transport proposes a decision;
/// the caller owns task state, speech delivery and execution authority.</summary>
public partial class ChatQW : IFastLoopDecisionBackend, IFastLoopHistoryBackend, IFastLoopRecoveryHistoryBackend
{
    private UnityWebRequest m_FastLoopRequest;
    private int m_FastLoopGeneration;
    private FirstReplyUserLease m_FastLoopHistoryLease;

    public bool ResumeFastLoopHistory(SendData acceptedUser, string freshImageUrl)
    {
        if (acceptedUser == null || acceptedUser.role != "user" || !m_DataList.Contains(acceptedUser)) return false;
        // Reject a newer accepted user even when its text happens to be identical.
        for (int i = m_DataList.Count - 1; i >= 0; --i)
            if (m_DataList[i]?.role == "user")
            { if (!ReferenceEquals(m_DataList[i], acceptedUser)) return false; break; }
        m_FastLoopHistoryLease = new FirstReplyUserLease {
            User = acceptedUser, Generation = m_StreamRequestGeneration,
            CommitHistory = true, Settled = true, HandoffPending = true
        };
        m_FirstReplyUserLease = m_FastLoopHistoryLease;
        return true;
    }

    public bool SupportsFastLoopDecisions => m_EnableRoleDecisionFastLane;

    public void BeginFastLoopHistory(string prompt, string imageUrl, bool commitHistory)
    {
        // The exact accepted object can be claimed by the legacy sender. Its
        // identity, not text equality, distinguishes repeated identical turns.
        var accepted = new SendData("user", prompt ?? "") { imageDataUrl = imageUrl };
        m_FastLoopHistoryLease = BeginFirstReplyUserLease(accepted, m_StreamRequestGeneration, commitHistory);
        m_FastLoopHistoryLease.Settled = true;
        m_FastLoopHistoryLease.HandoffPending = commitHistory;
    }

    public void CompleteFastLoopHistory()
    {
        // Completing in the new loop retains the accepted user and only closes
        // its handoff. Cancellation never deletes an already accepted input.
        var lease = m_FastLoopHistoryLease;
        m_FastLoopHistoryLease = null;
        if (lease != null && ReferenceEquals(lease, m_FirstReplyUserLease) &&
            lease.Generation == m_StreamRequestGeneration) lease.HandoffPending = false;
    }

    public void PostFastLoopDecision(string context, string currentUser,
        Action<FastLoopDecision, string> callback, ConversationTrace trace = null, string imageDataUrl = null)
    {
        CancelFastLoopDecision();
        if (!SupportsFastLoopDecisions)
        {
            callback?.Invoke(null, "fast_loop_disabled");
            return;
        }
        AbortPrewarmIfRunning();
        int generation = m_FastLoopGeneration;
        trace = trace ?? TraceContext?.Child("llm-fast-loop");
        string wire = BuildFastLoopRequestJson(context ?? "", currentUser ?? "", imageDataUrl);
        if (TryPostSingingSessionPair(context ?? "", currentUser ?? "", wire, generation, callback, trace,
            !string.IsNullOrEmpty(imageDataUrl))) return;
        StartCoroutine(RequestFastLoopPayload(wire, currentUser ?? "", generation, callback, trace,
            !string.IsNullOrEmpty(imageDataUrl), FastLoopRequestPolicy.FromContext(SingingSessionEventProtocol.RoleContext(context))));
    }

    public void CancelFastLoopDecision()
    {
        m_FastLoopGeneration++;
        CancelSingingSessionEvent();
        var request = m_FastLoopRequest;
        m_FastLoopRequest = null;
        if (request == null) return;
        try { if (!request.isDone) request.Abort(); }
        catch (Exception) { /* A disposed or completed request has no authority. */ }
    }

    private string BuildFastLoopRequestJson(string context, string currentUser, string imageDataUrl = null)
    {
        context = SingingSessionEventProtocol.RoleContext(context);
        // Never inherit BuildRoleDecisionPersona's full-prompt fallback: custom
        // layouts may include the old behavior/tool protocol there.
        string persona = "";
        if (m_PromptFiles != null)
            foreach (var asset in m_PromptFiles)
                if (asset != null && asset.name == "persona")
                {
                    persona = BuildRoleDecisionPersona().Replace("<", "‹").Replace(">", "›");
                    break;
                }
        if (!HasPromptFiles)
            persona = BuildRoleDecisionPersona().Replace("<", "‹").Replace(">", "›");
        string system = BuildRoleDecisionBehaviorRules() + "\n" + FastLoopPrompt.System(persona, BuildRoleDecisionLanguageRules(), lan) + FastLoopPrompt.RecoveryRules(context);
        bool singingSession = FastLoopRequestPolicy.FromContext(context).SingingSession != null;
        if (singingSession) system += "\n" + SingingSessionProtocol.Contract;
        string input = FastLoopPrompt.User(context, currentUser);
        var sb = new StringBuilder(Mathf.Max(2048, system.Length + input.Length + 1024));
        sb.Append("{\"model\":");
        AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":").Append(singingSession ? 384 : 256);
        sb.Append(",\"response_format\":").Append(FastLoopProtocol.ResponseFormatForContext(context).ToString(Formatting.None));
        sb.Append(",\"messages\":[");
        AppendMessage(sb, new SendData("system", system));
        sb.Append(',');
        AppendMessage(sb, new SendData("user", input) { imageDataUrl = imageDataUrl });
        sb.Append(']');
        AppendReasoningControls(sb, false);
        AppendSlot(sb, k_SlotAuxiliary);
        sb.Append('}');
        return sb.ToString();
    }

    private IEnumerator RequestFastLoopPayload(string wire, string currentUser, int generation,
        Action<FastLoopDecision, string> callback, ConversationTrace trace, bool imageSent, FastLoopRequestPolicy policy)
    {
        if (generation != m_FastLoopGeneration) yield break;
        var clock = Stopwatch.StartNew();
        JObject listeningAttachment = IncrementalListeningCandidate.DescribeWireAttachment(wire, k_AttachedImageNote);
        trace?.Mark("fast_loop.send", new JObject {
            ["generation"] = generation, ["backend"] = m_Backend.ToString(),
            ["slot"] = m_Backend == BackendType.Local ? (int?)k_SlotAuxiliary : null,
            ["requestBytes"] = Encoding.UTF8.GetByteCount(wire), ["maxTokens"] = policy?.SingingSession != null ? 384 : 256,
            ["stream"] = false, ["imageSent"] = imageSent
        });
        if (generation != m_FastLoopGeneration) yield break;
        using (var request = new UnityWebRequest(url, "POST"))
        {
            m_FastLoopRequest = request;
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(wire));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = 20;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            RaiseRequestDiagnostic(wire);
            // A diagnostic subscriber may cancel or start a newer generation.
            if (generation != m_FastLoopGeneration)
            {
                if (ReferenceEquals(m_FastLoopRequest, request)) m_FastLoopRequest = null;
                yield break;
            }
            // Building a context is not an attachment receipt. Record the immutable
            // wire identity only after the HTTP operation actually starts, including
            // primary, recovery and repair requests using this common sender.
            var operation = request.SendWebRequest();
            var attachment = (JObject)listeningAttachment.DeepClone();
            attachment["generation"] = generation;
            attachment["requestAttached"] = (string)attachment["status"] == "present";
            trace?.Sample((bool)attachment["requestAttached"] ? "listening.request_attached" : "listening.request_without_candidate", attachment);
            yield return operation;
            if (ReferenceEquals(m_FastLoopRequest, request)) m_FastLoopRequest = null;
            bool current = generation == m_FastLoopGeneration;
            trace?.Mark(current ? "fast_loop.transport_complete" : "fast_loop.superseded", new JObject {
                ["generation"] = generation, ["httpStatus"] = request.responseCode,
                ["transportResult"] = request.result.ToString(), ["elapsedSeconds"] = clock.Elapsed.TotalSeconds
            });
            if ((string)listeningAttachment["status"] == "present")
            {
                var receipt = (JObject)listeningAttachment.DeepClone();
                receipt["generation"] = generation; receipt["requestAttached"] = true;
                receipt["requestOutcome"] = current ? "transport-completed" : "superseded";
                receipt["httpStatus"] = request.responseCode;
                receipt["transportResult"] = request.result.ToString();
                receipt["elapsedSeconds"] = clock.Elapsed.TotalSeconds;
                // Attachment and a successful answer do not prove semantic use.
                receipt["modelUse"] = "unknown";
                trace?.Sample("listening.request_result", receipt);
            }
            if (!current || generation != m_FastLoopGeneration) yield break;

            string body = request.downloadHandler?.text ?? "";
            string finishReason = "", reason = "http_failure";
            FastLoopDecision decision = null;
            bool accepted = request.responseCode == 200 && request.result == UnityWebRequest.Result.Success &&
                FastLoopProtocol.TryReadBoundResponse(body, currentUser, policy, out decision, out finishReason, out reason);
            var performance = new CompletionPerformance();
            performance.Observe(body);
            var diagnostic = new JObject {
                ["generation"] = generation, ["accepted"] = accepted,
                ["reason"] = reason, ["finishReason"] = finishReason,
                ["route"] = accepted ? decision.Route : null,
                ["candidate"] = accepted ? decision.Candidate : null,
                ["taskControl"] = accepted ? decision.TaskControl : null,
                ["controlTargetId"] = accepted ? decision.ControlTargetId : null,
                ["controlEvidenceCharacters"] = accepted ? decision.ControlEvidence.Length : 0,
                ["speechCharacters"] = accepted ? (decision.Speech ?? "").Length : 0,
                ["imageSent"] = imageSent,
                ["responseBytes"] = Encoding.UTF8.GetByteCount(body),
                ["elapsedSeconds"] = clock.Elapsed.TotalSeconds, ["server"] = performance.Snapshot()
            };
            // No response body, API credentials or user evidence is logged.
            trace?.Mark(accepted ? "fast_loop.accepted" : "fast_loop.rejected", diagnostic);
            if (m_LogRequestStats) Debug.Log("[LLM/FastLoopWire] " + diagnostic.ToString(Formatting.None));
            if (generation != m_FastLoopGeneration) yield break;
            callback?.Invoke(accepted ? decision : null, reason);
        }
    }

    private static bool TryReadFastLoopResponse(string envelope, string currentUser,
        out FastLoopDecision decision, out string finishReason, out string reason) =>
        FastLoopProtocol.TryReadResponse(envelope, currentUser, out decision, out finishReason, out reason);
}
