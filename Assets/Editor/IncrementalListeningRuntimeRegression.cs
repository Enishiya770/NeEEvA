using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Actual preparation callback to accepted-input Fast context; no live scene, HTTP or task execution.</summary>
public static class IncrementalListeningRuntimeRegression
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    public static int RunOrThrow()
    {
        int checks = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new InvalidOperationException("Incremental runtime: " + label); }
        var host = new GameObject("Inactive incremental listening regression"); host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>(); var model = host.AddComponent<ListeningThoughtFixtureModel>();
            var sense = host.AddComponent<SenseVoiceSpeechToText>();
            Set(chat, "m_ChatSettings", new ChatSetting { m_ChatModel = model, m_SpeechToText = sense });
            Set(chat, "m_InputAudioRevision", 4); Set(chat, "m_LogSpeculativeListening", false);
            Set(chat, "m_SingingGoalTurn", 1); Set(chat, "m_LogAgentLoop", false);
            const string partial = "桌上的灯", final = "桌上的灯，不，先别管灯。";
            // Reuse the retained-PCM test setup; production acceptance and projection below remain actual methods.
            typeof(FastLoopContextRegression).GetMethod("Stage", Flags).Invoke(null, new object[] { sense, final, null, 2.4f });
            Call(chat, "RetainCompletedRecordingEvidence", sense);
            Set(chat, "m_StreamingTranscriptVersion", 1); Set(chat, "m_StreamingTranscript", partial);
            Set(chat, "m_StreamingLatestAudioMs", 1100);
            var source = new PreparedTurnBoundaryEvidence {
                CaptureSerial = sense.InputCaptureSessionSerial, Transcript = partial, Recording = true,
                AsrHealthy = true, AudioMs = 1100, TextRevision = 1, SampledAt = Time.realtimeSinceStartup };
            chat.ObservePreparedTurnBoundaryEvidence(source);
            IEnumerator prepare = (IEnumerator)Call(chat, "RequestSpeculativeDraftAfterDelay", 1, partial);
            while (prepare.MoveNext()) { }
            var wire = IncrementalListeningRegression.Wire(partial);
            wire["inner_reaction"] = "少し気になるな。続きを聞こう。";
            // The runtime must resist an old backend returning prose despite the new empty-draft schema.
            wire["draft"] = "我已经把灯处理好了。";
            model.Prepared(wire.ToString());
            Check(model.ListeningCalls == 1 && model.EphemeralCalls == 0, "single preparation wire, no additional model call");
            Check(Get(chat, "m_IncrementalListeningCandidate") != null && Get(chat, "m_SpeculativeDraft") == null &&
                !(bool)Get(chat, "m_SingingBridgeTtsInFlight") && ((ICollection)Get(chat, "m_PendingChunks")).Count == 0,
                "real callback stores an evidenced intent without legacy draft, TTS or speech side effects");
            Check((string)Get(chat, "m_ListeningThoughtText") == (string)wire["inner_reaction"], "current tentative natural thought is preserved");
            Set(chat, "m_IncrementalSingingSourceCandidate", Get(chat, "m_IncrementalListeningCandidate"));
            var staleMaterials = (IList)Activator.CreateInstance(typeof(ChatSample).GetField("m_IncrementalSingingSources", Flags).FieldType);
            staleMaterials.Add(null); Set(chat, "m_IncrementalSingingSources", staleMaterials);
            model.Prepared("{\"inner_reaction\":\"旧版返回\"}");
            Check(Get(chat, "m_IncrementalListeningCandidate") == null &&
                Get(chat, "m_IncrementalSingingSourceCandidate") == null &&
                (Get(chat, "m_IncrementalSingingSources") == null ||
                 ((ICollection)Get(chat, "m_IncrementalSingingSources")).Count == 0),
                "old or invalid provider JSON clears prior hypothesis and material cache together");
            model.Prepared(wire.ToString());
            Call(chat, "ReconcileListeningThought", final);
            Check((string)Get(chat, "m_ListeningThoughtText") == "", "appended cancellation withdraws bubble immediately");
            Call(chat, "CancelSpeculativeRequestWithReason", "final-input-accepted");
            Set(chat, "m_LastUserMsg", final); Set(chat, "m_LastUserEvidence", "[accepted evidence] " + final);
            Call(chat, "ResetAcceptedRecordingInput"); Call(chat, "BindAcceptedRecordingInput", true);
            Set(chat, "m_FormalResponseGeneration", 7); Set(chat, "m_WorkEpoch", 9);
            Call(chat, "FastLoopCurrentUserText", "[accepted evidence] " + final);
            JObject facts = Facts(chat);
            Check(facts["listeningPreparation"] is JObject && (bool)facts["listeningPreparation"]["finalMismatch"] &&
                (string)facts["listeningPreparation"]["source"]["text"] == partial,
                "actual accepted capture gets the bounded earlier hypothesis marked different from final");
            Check(!(bool)facts["listeningPreparation"]["executionAuthorized"] && facts["listeningPreparation"]["inner_reaction"] == null &&
                ((ICollection)Get(chat, "m_PendingChunks")).Count == 0, "context never turns partial meaning into output or an action");
            var observation = (JObject)Get(chat, "m_IncrementalListeningContextObservation");
            Check((string)observation["phase"] == "context-ready" && !(bool)observation["requestAttached"] &&
                (string)observation["modelUse"] == "unknown", "building formal facts is not a send or adoption receipt");
            // Exercise the actual production builder with and without its image note.
            var qwen = host.AddComponent<ChatQW>(); qwen.m_LogRequestStats = false;
            Set(qwen, "url", "http://127.0.0.1:1/fixture-must-not-send");
            string context = facts.ToString();
            string wireText = (string)Call(qwen, "BuildFastLoopRequestJson", context, final, null);
            var receipt = IncrementalListeningCandidate.DescribeWireAttachment(wireText);
            Check((string)receipt["status"] == "present" && (bool)receipt["finalMismatch"] &&
                (int)receipt["capture"] == source.CaptureSerial, "real text builder preserves exact prepared candidate identity");
            string visualWire = (string)Call(qwen, "BuildFastLoopRequestJson", context, final, "data:image/png;base64,FIXTURE");
            string note = (string)typeof(ChatQW).GetField("k_AttachedImageNote", Flags).GetRawConstantValue();
            Check((string)IncrementalListeningCandidate.DescribeWireAttachment(visualWire, note)["status"] == "present",
                "real multimodal wire prefix does not hide preparation from attachment diagnostics");
            var traces = new List<JObject>();
            var trace = new ConversationTrace("incremental-attachment-fixture", value => traces.Add(JObject.Parse(value)));
            int diagnosticCount = 0;
            Action<string> cancelBeforeSend = _ => { diagnosticCount++; qwen.CancelFastLoopDecision(); };
            qwen.OnRequestDiagnostic += cancelBeforeSend;
            var send = (IEnumerator)Call(qwen, "RequestFastLoopPayload", wireText, final,
                (int)Get(qwen, "m_FastLoopGeneration"), new Action<FastLoopDecision, string>((_, __) =>
                    { throw new InvalidOperationException("Cancelled preparation wire must not return a decision"); }),
                trace, false, FastLoopRequestPolicy.FromContext(context));
            try { Check(!send.MoveNext(), "diagnostic cancellation prevents all HTTP dispatch"); }
            finally { (send as IDisposable)?.Dispose(); qwen.OnRequestDiagnostic -= cancelBeforeSend; }
            Check(diagnosticCount == 1 && traces.TrueForAll(t => (string)t["event"] != "listening.request_attached" &&
                (string)t["event"] != "listening.request_result"), "cancelled-before-send wire is never labelled attached or used");
            Check(!(bool)Get(chat, "m_EnablePreparedBoundaryReuse"), "observability leaves experimental EOU reuse disabled");
            Set(chat, "m_InputAudioRevision", 5);
            Check(Facts(chat)["listeningPreparation"].Type == JTokenType.Null, "new input generation cannot reuse old preparation");
            Check((string)((JObject)Get(chat, "m_IncrementalListeningContextObservation"))["reason"] == "input-revision-changed",
                "formal omission reports changed accepted input generation");
            Set(chat, "m_InputAudioRevision", 4); Set(chat, "m_CurrentUserInputHasAudioEvidence", false);
            Call(chat, "FastLoopCurrentUserText", "[accepted evidence] " + final);
            Check(Facts(chat)["listeningPreparation"].Type == JTokenType.Null, "typed same words cannot borrow a microphone hypothesis");
            Check((string)((JObject)Get(chat, "m_IncrementalListeningContextObservation"))["reason"] == "no-accepted-audio",
                "typed-input omission is distinguished from model rejection");
            Set(chat, "m_CurrentUserInputHasAudioEvidence", true); Call(chat, "FastLoopCurrentUserText", "[accepted evidence] " + final);
            ((ChatSetting)Get(chat, "m_ChatSettings")).m_SpeechToText = host.AddComponent<SenseVoiceSpeechToText>();
            Check(Facts(chat)["listeningPreparation"].Type == JTokenType.Null, "provider ownership fences capture-number collisions");
            Check((string)((JObject)Get(chat, "m_IncrementalListeningContextObservation"))["reason"] == "provider-changed",
                "provider change is observable without relaxing the accepted-input lease");
            Call(chat, "InvalidateListeningThoughtPresentation");
            Check(Get(chat, "m_IncrementalListeningCandidate") == null, "disabled owner cannot revive an earlier preparation");
            return checks;
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }
    private static JObject Facts(ChatSample chat) { var facts = new JObject(); Call(chat, "EnrichFastLoopContext", facts); return facts; }
    private static object Get(object value, string field) => value.GetType().GetField(field, Flags).GetValue(value);
    private static void Set(object value, string field, object data) => value.GetType().GetField(field, Flags).SetValue(value, data);
    private static object Call(object value, string method, params object[] args) => value.GetType().GetMethod(method, Flags).Invoke(value, args);
}
