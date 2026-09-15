using System;
using System.Collections;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Inactive actual ChatSample hooks with a provider that counts and retains calls; no HTTP/audio.</summary>
public static class PreparedTurnBoundaryHookRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;
    public static int RunOrThrow()
    {
        checks = 0;
        var host = new GameObject("Prepared boundary inactive hook regression"); host.SetActive(false);
        try
        {
            ChatSample chat = host.AddComponent<ChatSample>();
            var model = host.AddComponent<PreparedTurnBoundaryFixtureModel>();
            Set(chat, "m_ChatSettings", new ChatSetting { m_ChatModel = model });
            Set(chat, "m_InputAudioRevision", 7); Set(chat, "m_LogSpeculativeListening", false);
            int delivered = 0; string lastAction = "";
            chat.OnStalledUserTurnDecision += (action, confidence, mode, probability, source, text, audio) =>
            { delivered++; lastAction = action; Check(text == Text, "callback retains reviewed exact transcript"); };
            var evidence = Evidence(); chat.ObservePreparedTurnBoundaryEvidence(evidence);

            // Production defaults to observation only after the real model falsely
            // closed an unfinished correction. Even a fully valid evidence lease must
            // fall through to the existing matured independent review, with no decision
            // callback or speech from preparation itself.
            Check(!(bool)Get(chat, "m_EnablePreparedBoundaryReuse"), "production prepared closure defaults to shadow only");
            Call(chat, "StagePreparedTurnBoundary", Wire(), Text, evidence, 7, evidence.SampledAt);
            chat.ObservePreparedTurnBoundaryEvidence(Mature(evidence));
            object[] shadowArgs = { Text, null };
            bool applied = (bool)typeof(ChatSample).GetMethod("TryConsumePreparedTurnBoundary", Flags).Invoke(chat, shadowArgs);
            Check(!applied && shadowArgs[1] == null && delivered == 0,
                "shadow validation never returns a prepared decision or invokes a callback");
            Call(chat, "StagePreparedTurnBoundary", Wire(), Text, evidence, 7, evidence.SampledAt);
            Check(Request(chat) && model.BoundaryCalls == 1 && delivered == 0 &&
                Get(chat, "m_PreparedBoundary") == null && Get(chat, "m_SpeculativeDraft") == null,
                "default shadow hit consumes candidate but cannot close or speak; independent review proceeds");
            model.Boundary("{\"action\":\"continue\",\"confidence\":0.9,\"mode\":\"speech\",\"source\":\"user\",\"turn_state\":\"open\",\"reason\":\"未完\"}");
            Check(delivered == 1 && lastAction == "continue", "shadow path preserves the independent open decision");
            Call(chat, "CancelTurnBoundaryReview");
            model.BoundaryCalls = 0; delivered = 0; lastAction = "";
            Set(chat, "m_EnablePreparedBoundaryReuse", true); // Explicit isolated experiment only.
            evidence = Evidence(); chat.ObservePreparedTurnBoundaryEvidence(evidence);

            // Exercise the actual ordinary speculative coroutine and its provider callback.
            // The wait is not measured here: manually advance its preparation body only.
            Set(chat, "m_StreamingTranscriptVersion", 1); Set(chat, "m_StreamingTranscript", Text);
            Set(chat, "m_StreamingLatestAudioMs", 1000); Set(chat, "m_LastDraftTranscript", "");
            IEnumerator draft = (IEnumerator)Call(chat, "RequestSpeculativeDraftAfterDelay", 1, Text);
            while (draft.MoveNext()) { }
            Check(model.ListeningCalls == 1 && model.LegacyEphemeralCalls == 0,
                "ordinary draft uses one explicit listening preparation call");
            Check(model.Prompt.Contains("quiet_boundary") && model.Prompt.Contains("没有修改") &&
                model.Prompt.Contains("完整的否定请求也可以闭合"), "actual prompt contains conditional semantic contract");
            model.Prepared(Wire());
            Check(Get(chat, "m_PreparedBoundary") != null && Get(chat, "m_SpeculativeDraft") == null,
                "a valid boundary can be prepared without an audible draft");
            evidence = Mature(evidence); chat.ObservePreparedTurnBoundaryEvidence(evidence);
            Check(Request(chat), "prepared review accepted");
            Check(model.BoundaryCalls == 0 && delivered == 1 && lastAction == "complete",
                "ready lease hits actual callback with zero fresh boundary requests");
            Check(Get(chat, "m_PreparedBoundary") == null, "lease consumed once");

            Check(Request(chat), "consumed lease falls back");
            Check(model.BoundaryCalls == 1 && delivered == 1, "fallback issues one request and waits for its callback");
            model.Boundary("{\"action\":\"continue\",\"confidence\":0.9,\"mode\":\"speech\",\"source\":\"user\",\"turn_state\":\"open\",\"reason\":\"未完\"}");
            Check(delivered == 2 && lastAction == "continue", "fallback semantic continue is preserved");

            foreach (string fault in new[] { "correction", "rollback-to-original", "breath", "singing", "coverage", "expired", "in-flight" })
            {
                Call(chat, "CancelTurnBoundaryReview"); Call(chat, "CancelSpeculativeRequestWithReason", "fixture-reset");
                evidence = Evidence(); chat.ObservePreparedTurnBoundaryEvidence(evidence);
                if (fault != "in-flight") Call(chat, "StagePreparedTurnBoundary", Wire(), Text, evidence, 7, evidence.SampledAt);
                var current = Mature(evidence);
                if (fault == "correction") { current.Transcript = Text + "不，别打开。"; current.TextRevision++; }
                if (fault == "rollback-to-original") current.TextRevision += 2;
                if (fault == "breath") current.ActivityRevision++;
                if (fault == "singing") current.Singing = true;
                if (fault == "coverage") current.CoversQuietTail = false;
                if (fault == "expired")
                {
                    var old = evidence; old.SampledAt -= 4f;
                    PreparedTurnBoundary expired; string reason;
                    Check(PreparedTurnBoundary.TryCreate(Wire(), old, 7, old.SampledAt,
                        old.SampledAt + 1f, out expired, out reason), "expired fixture originally valid");
                    Set(chat, "m_PreparedBoundary", expired);
                }
                if (fault == "in-flight") Set(chat, "m_SpeculativeRequestInFlight", true);
                chat.ObservePreparedTurnBoundaryEvidence(current);
                int calls = model.BoundaryCalls, events = delivered;
                Check(Request(chat), "fallback accepted: " + fault);
                Check(model.BoundaryCalls == calls + 1 && delivered == events,
                    "invalid lease sends exactly one fresh boundary request, no speculative closure: " + fault);
            }
            Call(chat, "CancelTurnBoundaryReview");
            evidence = Evidence(); chat.ObservePreparedTurnBoundaryEvidence(evidence);
            Call(chat, "StagePreparedTurnBoundary", Wire(), Text, evidence, 7, evidence.SampledAt);
            Call(chat, "StagePreparedTurnBoundary", Wire("continue", "open"), Text, evidence, 7, evidence.SampledAt);
            Check(Get(chat, "m_PreparedBoundary") == null, "new open judgment revokes older closed candidate");

            // Provider callbacks can race a matured independent EOU. A cancelled result
            // cannot publish a boundary or draft even when its original transcript matches.
            evidence = Evidence(); evidence.CaptureSerial = 5;
            chat.ObservePreparedTurnBoundaryEvidence(evidence);
            Set(chat, "m_SpeculativeRequestInFlight", false);
            Set(chat, "m_StreamingTranscriptVersion", 20); Set(chat, "m_StreamingTranscript", Text);
            IEnumerator pending = (IEnumerator)Call(chat, "RequestSpeculativeDraftAfterDelay", 20, Text);
            while (pending.MoveNext()) { }
            Check(model.ListeningCalls == 2, "new capture dispatches bounded preparation");
            Action<string> late = model.Prepared;
            Call(chat, "CancelSpeculativeRequestWithReason", "mature-eou");
            late(Wire());
            Check(Get(chat, "m_PreparedBoundary") == null && Get(chat, "m_SpeculativeDraft") == null,
                "cancelled callback cannot publish even a valid exact-text closure");
            checks += PreparedListeningTransportRegression.RunOrThrow();
            return checks;
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private const string Text = "请把小灯打开。";
    private static PreparedTurnBoundaryEvidence Evidence() => new PreparedTurnBoundaryEvidence {
        CaptureSerial = 4, TextRevision = 10, ActivityRevision = 2, AudioMs = 1000,
        Transcript = Text, SampledAt = Time.realtimeSinceStartup, LatestSoundAt = Time.realtimeSinceStartup - 0.5f,
        QuietSeconds = 0.5f, MinimumQuietSeconds = 0.35f, MinimumConfidence = 0.78f,
        Recording = true, AsrHealthy = true, FreshCoverage = true, CoversQuietTail = true,
        TailAvailable = true, TailAge = 0.1f,
    };
    private static PreparedTurnBoundaryEvidence Mature(PreparedTurnBoundaryEvidence source)
    { source.SampledAt = Time.realtimeSinceStartup; source.AudioMs = 2000; source.QuietSeconds = 0.6f; return source; }
    private static string Wire(string action = "complete", string state = "closed") => new JObject {
        ["observed_mode"] = "speech", ["mode_confidence"] = 0.95f, ["draft"] = "",
        ["quiet_boundary"] = new JObject { ["action"] = action, ["confidence"] = 0.95f,
            ["mode"] = "speech", ["source"] = "user", ["turn_state"] = state, ["reason"] = "完整请求" },
    }.ToString();
    private static bool Request(ChatSample chat) => chat.RequestStalledTurnReview(Text,
        1f, 2000, 0.001f, 0.01f, 0.004f, true, 0f, 0f, 0f, 0f, 0f,
        0, "", 0f, 15f, 0.6f, 0.1f, 1000, "quiet", 0);
    private static object Get(object value, string field) => value.GetType().GetField(field, Flags).GetValue(value);
    private static void Set(object value, string field, object data) => value.GetType().GetField(field, Flags).SetValue(value, data);
    private static object Call(object value, string method, params object[] args) => value.GetType().GetMethod(method, Flags).Invoke(value, args);
    private static void Check(bool ok, string label)
    { checks++; if (!ok) throw new InvalidOperationException("Prepared boundary hook: " + label); }
}

public sealed class PreparedTurnBoundaryFixtureModel : LLM
{
    public int ListeningCalls, LegacyEphemeralCalls, BoundaryCalls;
    public string Prompt;
    public Action<string> Prepared, Boundary;
    public override bool SupportsTurnBoundaryMessages => true;
    public override void PostListeningPreparationMsg(string prompt, Action<string> callback)
    { ListeningCalls++; Prompt = prompt; Prepared = callback; }
    public override void PostEphemeralMsg(string prompt, Action<string> callback) => LegacyEphemeralCalls++;
    public override void PostTurnBoundaryMsg(string prompt, Action<string> callback)
    { BoundaryCalls++; Boundary = callback; }
    public override void CancelEphemeralMsg() { }
    public override void CancelTurnBoundaryMsg() { }
}
