using System;
using System.Reflection;
using UnityEngine;

// Production input outcome/envelope and interruption ownership checks. No
// microphone or remote model; physical voice accuracy is a separate acceptance.
public static class InputTaskRepairRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;
    public static int RunChecks()
    {
        checks = 0;
        var host = new GameObject("Input outcome regression");
        host.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            var sense = host.AddComponent<SenseVoiceSpeechToText>();
            sense.BeginLiveRecordingCandidateSession();
            int capture = sense.InputCaptureSessionSerial;
            const string raw = "不要停，继续跟着我。";
            const string envelope = "[说话人:用户; 类型:owner] [情绪:NEUTRAL] 不要停，继续跟着我。";
            sense.PublishReusedFinalTranscript(capture, raw, envelope);
            Check(sense.MatchesFinalTranscriptCallback(capture, envelope) &&
                sense.MatchesFinalTranscriptCallback(capture, raw), "Bound enriched and raw callbacks retain the same exact capture.");
            Check(!sense.MatchesFinalTranscriptCallback(capture, "[说话人:别人] " + raw) &&
                !sense.MatchesFinalTranscriptCallback(capture, "停下。"), "Arbitrary envelopes and semantic revisions are not normalized into equality.");
            Check(!sense.LastFinalInputFailed, "Successful preview is not a service failure.");
            Publish(sense, capture, "rejected", "http-error");
            Check(sense.LastFinalInputFailed, "A final HTTP failure is explicitly different from no speech.");
            foreach (string reason in new[] { "invalid-response", "request-no-result", "input-error" })
            { Publish(sense, capture, "rejected", reason); Check(sense.LastFinalInputFailed, "Input error: " + reason); }
            foreach (string reason in new[] { "self-speech", "vad-insufficient-speech", "hallucination-rejected", "cancelled", "detached" })
            { Publish(sense, capture, "rejected", reason); Check(!sense.LastFinalInputFailed, "No clarification from a non-service outcome: " + reason); }
            Publish(sense, capture, "evidence-only", "weak-audio-evidence");
            Check(!sense.LastFinalInputFailed, "Retained weak sound is neither an error nor accepted user words.");
            sense.BeginLiveRecordingCandidateSession();
            sense.PublishReusedFinalTranscript(capture, raw, envelope);
            Check(!sense.MatchesFinalTranscriptCallback(capture, envelope) && sense.LastFinalPerceivedText == "",
                "A superseded capture cannot publish or confirm the old envelope.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }

        object fixture = Activator.CreateInstance(typeof(FastLoopIntegrationRegression).GetNestedType("Fixture", BindingFlags.NonPublic), true);
        try
        {
            var chat = (ChatSample)Get(fixture, "Chat");
            var model = (FastLoopIntegrationModel)Get(fixture, "Model");
            Call(fixture, "Arm", "原来的问题。", true);
            var request = (FastLoopIntegrationModel.PendingRequest)Call(fixture, "Dispatch", new object[] { null });
            int generation = (int)Get(chat, "m_FormalResponseGeneration");
            chat.NotifyUserRecordingCandidate();
            Check((int)Get(chat, "m_FormalResponseGeneration") == generation && (bool)Get(chat, "m_FormalResponseInFlight") &&
                !(bool)Get(chat, "m_UserSpeechActiveForAutonomy"), "Weak recording provenance preserves the in-flight answer and does not claim user interruption.");
            request.Answer(new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Language = "zh", Speech = "原问题的回答。" }, "ok");
            Check(((string)Call(fixture, "Queued")).Contains("原问题的回答"), "A weak candidate does not discard a valid pending reply.");

            var sense = chat.gameObject.AddComponent<SenseVoiceSpeechToText>();
            ((ChatSetting)Get(chat, "m_ChatSettings")).m_SpeechToText = sense;
            Set(chat, "m_AutoSend", false);
            Set(chat, "m_InterruptedUserQuestion", "原来的问题。");
            Set(chat, "m_UserSpeechActiveForAutonomy", true);
            Set(chat, "m_WorkReviewOpen", true);
            int oldCalls = model.FormalCalls + model.Pending.Count;
            sense.BeginLiveRecordingCandidateSession();
            Publish(sense, sense.InputCaptureSessionSerial, "rejected", "http-error");
            chat.AcceptText("");
            Check(Get(chat, "m_InterruptedUserQuestion") == null && !(bool)Get(chat, "m_WorkReviewOpen") &&
                (string)Get(chat, "m_WorkStatus") == "waiting_user" && !(bool)Get(chat, "m_UserSpeechActiveForAutonomy"),
                "Failed final input waits for clarification and cannot resume the old user's question.");
            Check(oldCalls == model.FormalCalls + model.Pending.Count && (string)Get(chat, "m_LastUserMsg") == "原来的问题。",
                "Failure neither fabricates accepted user text nor starts a replacement LLM/tool call.");
            Call(fixture, "Arm", "原来的问题。", true);
            var receipt = new SpeechText("刚才那段话没听完整，可以再说一次吗？", "zh", "input-failure", SpeechActionDependency.Independent);
            int receiptGeneration = (int)Get(chat, "m_FormalResponseGeneration");
            int historyBefore = model.m_DataList.Count;
            Check((bool)Call(chat, "CompletePendingInputFailureReceipt", receipt, receiptGeneration) &&
                ((string)Call(fixture, "Queued")).Contains("可以再说一次吗"),
                "A fixed clarification goes through the real speech queue without inventing user text.");
            Check(oldCalls == model.FormalCalls + model.Pending.Count && historyBefore == model.m_DataList.Count &&
                !(bool)Get(chat, "m_FormalResponseInFlight"),
                "Clarification completion requests no model work and fabricates no conversation history.");

        }
        finally { ((IDisposable)fixture).Dispose(); }
        Debug.Log("[InputTaskRepairRegression] passed " + checks + " checks");
        return checks;
    }
    private static void Publish(SenseVoiceSpeechToText sense, int capture, string source, string reason) =>
        Call(sense, "PublishFinalTranscriptOutcome", capture, source, reason, "", "");
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static object Call(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethod(name, Flags).Invoke(target, args); }
        catch (TargetInvocationException e) { throw e.InnerException ?? e; }
    }
    private static void Check(bool condition, string name)
    { checks++; if (!condition) throw new InvalidOperationException(name); }
}
