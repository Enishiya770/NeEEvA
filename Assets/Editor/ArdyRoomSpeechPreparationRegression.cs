using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;

// Runs synchronously inside the existing connected room fixture. Only task
// ownership is staged; no ARDY request, microphone, Qwen call or TTS is launched.
public static class ArdyRoomSpeechPreparationRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;

    public static int RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge)
    {
        checks = 0;
        Check(Application.isPlaying && chat != null && bridge != null && bridge.IsConnected && !chat.enabled,
            "Use the dormant ChatSample in the connected Play-mode room fixture.");
        string failure = null;
        try
        {
            using (var f = new Fixture(chat, bridge))
            {
                var p = f.Begin("停一下。"); int operation = f.CancelRevision;
                p.Deliver("stop-moving");
                Check(f.Bridge.ReservesBody && f.CancelRevision == operation, "Prepared semantics alone cannot stop.");
                f.Final("full-preview", "停一下。", "停一下。");
                string stopped = f.Bridge.ActionId;
                Check(!f.Bridge.ReservesBody && f.Bridge.Phase == "stopped" && f.CancelRevision == operation + 1,
                    "Matching terminal ASR commits the actual controller cancellation.");
                p.Deliver("stop-moving"); f.Final("full-preview", "停一下。", "停一下。");
                Check(f.Bridge.ActionId == stopped && f.CancelRevision == operation + 1,
                    "Duplicate semantic/final callbacks cannot stop twice.");
                int decisions = f.Model.Decisions;
                f.Formal("停一下。");
                Check(f.Model.Decisions == decisions && f.Model.SpeechRequests == 1 && f.Bridge.ActionId == stopped &&
                    f.CancelRevision == operation + 1 && (string)Get(chat, "m_RoomTaskActionId") == stopped,
                    "Accepted ASR user-turn handoff reuses prepared result in formal response without a second stop.");

                p = f.Begin("停一下。"); operation = f.CancelRevision;
                f.Final("closed-stream-recovery", "停一下。", "停一下。");
                Check(f.Bridge.ReservesBody && f.CancelRevision == operation, "Final confirmation still waits for semantic stop decision.");
                p.Deliver("stop-moving");
                Check(!f.Bridge.ReservesBody && f.CancelRevision == operation + 1,
                    "Matching same-capture recovery and later semantics commit once.");

                foreach (string text in new[] { "不要停，继续走。", "他说‘停一下’，不是叫你停。", "今天心情怎么样？" })
                {
                    p = f.Begin(text); operation = f.CancelRevision;
                    p.Deliver("none"); f.Final("full-preview", text, text);
                    Check(f.Bridge.ReservesBody && f.CancelRevision == operation, "Non-stop semantics preserve movement: " + text);
                }
                foreach (string source in new[] { "rejected", "pending", "unknown" })
                {
                    p = f.Begin("停一下。"); operation = f.CancelRevision;
                    f.Final(source, "", ""); p.Deliver("stop-moving");
                    Check(f.Bridge.ReservesBody && f.CancelRevision == operation, "Rejected/empty/self ASR invalidates pending semantics: " + source);
                }
                p = f.Begin("停一下。"); operation = f.CancelRevision;
                f.Final("full-preview", "不要停。", "不要停。"); p.Deliver("stop-moving");
                Check(f.Bridge.ReservesBody && f.CancelRevision == operation, "Final negation revision invalidates prepared stop.");

                p = f.Begin("停一下。"); operation = f.CancelRevision;
                f.Sense.BeginLiveRecordingCandidateSession(); f.Final("full-preview", "停一下。", "停一下。"); p.Deliver("stop-moving");
                Check(f.Bridge.ReservesBody && f.CancelRevision == operation, "Different capture serial cannot confirm old stop.");

                p = f.Begin("停一下。"); operation = f.CancelRevision;
                f.Final("full-preview", "停一下。", "停一下。");
                chat.NotifyUserStartedSpeaking(); p.Deliver("stop-moving");
                Check(f.Bridge.ReservesBody && f.CancelRevision == operation, "New microphone recording invalidates pending stop but preserves the room task.");

                p = f.Begin("停一下。"); operation = f.CancelRevision;
                f.Final("full-preview", "停一下。", "停一下。");
                Call(chat, "SetCurrentUserInput", "别停，继续走吧。", "别停，继续走吧。");
                Call(chat, "RecordAcceptedMotionUserTurn", "别停，继续走吧。"); p.Deliver("stop-moving");
                Check(f.Bridge.ReservesBody && f.CancelRevision == operation, "Typed new user turn also invalidates the old prepared stop.");

                p = f.Begin("停一下。"); operation = f.CancelRevision;
                f.Final("full-preview", "停一下。", "停一下。");
                int oldOperation = bridge.Controller.OperationRevision;
                Set(bridge.Controller, "<OperationRevision>k__BackingField", oldOperation + 1);
                p.Deliver("stop-moving");
                Check(f.Bridge.ReservesBody && f.CancelRevision == operation &&
                    ((string)Get(Get(chat, "m_RoomSpeechPreparation"), "stopDispatch")).StartsWith("not-submitted", StringComparison.Ordinal),
                    "A new execution operation rejects the old prepared stop.");
                Set(bridge.Controller, "<OperationRevision>k__BackingField", oldOperation);

                p = f.Begin("停一下。"); operation = f.CancelRevision;
                f.Final("full-preview", "停一下。", "停一下。");
                Set(bridge, "actionId", "replacement-task"); p.Deliver("stop-moving");
                Check(f.Bridge.ReservesBody && f.CancelRevision == operation && bridge.ActiveTaskId == "replacement-task",
                    "Replacement task identity cannot be stopped by an old recording.");

                Check(f.Model.Decisions == decisions && f.Model.SpeechRequests == 1,
                    "Closed evidence never starts an ordinary reply or another room decision by itself.");

                p = f.Begin("停一下。"); operation = f.CancelRevision;
                f.Final("full-preview", "停一下。", "停一下。"); f.Formal("停一下。");
                var waiting = Get(chat, "m_RoomSpeechPreparation");
                Check(waiting != null && Get(waiting, "continuation") != null && (bool)Get(chat, "m_FormalResponseInFlight"),
                    "Real accepted-input work epoch and ledger migration retain pending preparation.");
                Set(chat, "m_AgentRoundInFlight", true); Set(chat, "m_StreamComplete", false);
                decisions = f.Model.Decisions; f.Model.CancelRoomPreparationMessage();
                Call(chat, "CheckRoomPreparationWait", waiting, (float)Get(waiting, "deadline") + .01f);
                Check(f.Model.Decisions == decisions + 1 && (bool)Get(chat, "m_FormalResponseInFlight") &&
                    Get(chat, "m_RoomSpeechPreparation") == null, "Missing provider callback gets one bounded fresh formal decision.");
                Check((bool)Get(chat, "m_AgentRoundInFlight") && !(bool)Get(chat, "m_StreamComplete"),
                    "Valid fallback preserves the agent reply and stream completion state.");
                p.Deliver("stop-moving"); Call(chat, "CheckRoomPreparationWait", waiting, (float)Get(waiting, "deadline") + 1f);
                Check(f.Model.Decisions == decisions + 1 && f.CancelRevision == operation && f.Bridge.ReservesBody,
                    "Timed-out preparation never stops late or starts a second fallback.");

                p = f.Begin("停一下。"); operation = f.CancelRevision;
                f.Final("full-preview", "停一下。", "停一下。"); f.Formal("停一下。");
                waiting = Get(chat, "m_RoomSpeechPreparation"); decisions = f.Model.Decisions;
                var replacement = chat.gameObject.AddComponent<ArdyRoomSpeechPreparationFakeLLM>();
                try
                {
                    ((ChatSetting)Get(chat, "m_ChatSettings")).m_ChatModel = replacement;
                    Call(chat, "CheckRoomPreparationWait", waiting, Time.realtimeSinceStartup);
                    p.Deliver("stop-moving");
                    Check(Get(chat, "m_RoomSpeechPreparation") == null && !(bool)Get(chat, "m_FormalResponseInFlight") &&
                        replacement.Decisions == 0 && replacement.Cancellations == 0 && f.Model.Decisions == decisions &&
                        f.Bridge.ReservesBody && f.CancelRevision == operation,
                        "Provider replacement releases old formal wait without querying/cancelling the replacement provider or stopping.");
                }
                finally
                {
                    ((ChatSetting)Get(chat, "m_ChatSettings")).m_ChatModel = f.Model;
                    UnityEngine.Object.DestroyImmediate(replacement);
                }

                p = f.Begin("停一下。"); operation = f.CancelRevision;
                f.Final("full-preview", "停一下。", "停一下。"); f.Formal("停一下。");
                waiting = Get(chat, "m_RoomSpeechPreparation");
                var otherSpeech = chat.gameObject.AddComponent<SenseVoiceSpeechToText>();
                try
                {
                    otherSpeech.BeginLiveRecordingCandidateSession();
                    ((ChatSetting)Get(chat, "m_ChatSettings")).m_SpeechToText = otherSpeech;
                    Call(chat, "CheckRoomPreparationWait", waiting, Time.realtimeSinceStartup); p.Deliver("stop-moving");
                    Check(Get(chat, "m_RoomSpeechPreparation") == null && !(bool)Get(chat, "m_FormalResponseInFlight") &&
                        f.Bridge.ReservesBody && f.CancelRevision == operation,
                        "Replacing ASR instance invalidates capture identity even when its counter restarts.");
                }
                finally
                {
                    ((ChatSetting)Get(chat, "m_ChatSettings")).m_SpeechToText = f.Sense;
                    UnityEngine.Object.DestroyImmediate(otherSpeech);
                }

                p = f.Begin("停一下。"); operation = f.CancelRevision;
                f.Final("full-preview", "停一下。", "停一下。"); f.Formal("停一下。");
                waiting = Get(chat, "m_RoomSpeechPreparation");
                Set(chat, "m_AutoSend", false); Call(chat, "CheckRoomPreparationWait", waiting, Time.realtimeSinceStartup);
                Set(chat, "m_AutoSend", true); p.Deliver("stop-moving");
                Check(Get(chat, "m_RoomSpeechPreparation") == null && !(bool)Get(chat, "m_FormalResponseInFlight") &&
                    f.Bridge.ReservesBody && f.CancelRevision == operation,
                    "Turning AutoSend off releases the wait and re-enabling does not revive a stale stop.");

                p = f.Begin("停一下。");
                f.Final("full-preview", "停一下。", "停一下。"); f.Formal("停一下。");
                string disabledTask = bridge.ActionId; decisions = f.Model.Decisions;
                chat.enabled = false; chat.enabled = true; p.Deliver("stop-moving");
                Check(Get(chat, "m_RoomSpeechPreparation") == null && !(bool)Get(chat, "m_FormalResponseInFlight") &&
                    bridge.ActionId == disabledTask && f.Model.Decisions == decisions,
                    "Disable clears the pending formal wait; re-enable cannot execute late preparation.");
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            string path = Path.GetFullPath("Tools/MotionAdapter/reports/room-speech-preparation-regression.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(new {
                passed = failure == null, checks, failure, checkedAtUtc = DateTime.UtcNow.ToString("o"),
                scope = "Connected room and actual ChatSample closed-evidence, final confirmation, formal reuse and controller cancellation; controlled LLM callbacks and staged in-memory task ownership. No microphone, Qwen semantics, ARDY motion generation or audible latency claim."
            }, Formatting.Indented) + "\n");
        }
        return checks;
    }

    private sealed class Fixture : IDisposable
    {
        public readonly ChatSample Chat;
        public readonly ArdyRoomDialogueBridge Bridge;
        public readonly ArdyRoomSpeechPreparationFakeLLM Model;
        public readonly SenseVoiceSpeechToText Sense;
        private readonly TTS tts;
        private readonly Dictionary<string, object> chatSaved = new Dictionary<string, object>(), bridgeSaved = new Dictionary<string, object>();
        private readonly bool savedEnabled;
        private int task;
        public int CancelRevision => (int)Get(Bridge.Controller, "revision");
        public Fixture(ChatSample chat, ArdyRoomDialogueBridge bridge)
        {
            Chat = chat; Bridge = bridge; savedEnabled = chat.enabled;
            Model = chat.gameObject.AddComponent<ArdyRoomSpeechPreparationFakeLLM>();
            Sense = chat.gameObject.AddComponent<SenseVoiceSpeechToText>();
            tts = chat.gameObject.AddComponent<TTS>();
            foreach (string field in new[] { "m_ChatSettings", "m_ChatHistory", "m_UseStreaming", "m_IsVoiceMode", "m_AutoSend",
                "m_AgentRunning", "m_AgentCurrentRoundIsTick", "m_LogStreamTimings", "m_LogAgentLoop", "m_LogRawLLMOutput",
                "m_EnableSubtitleTranslation", "m_SystemNoticeMode", "m_LastUserTurnTime", "m_UserSpeechActiveForAutonomy" })
                chatSaved[field] = Get(Chat, field);
            foreach (string field in new[] { "activeAction", "actionId", "phase", "reason", "lastAttemptActionId", "lastAttemptPhase",
                "lastAttemptReason", "lastAttemptTime", "followActive", "followNeedsNewTarget", "followUser", "hasControllerOperation", "operationRevision" })
                bridgeSaved[field] = Get(Bridge, field);
            Set(Chat, "m_ChatSettings", new ChatSetting { m_ChatModel = Model, m_TextToSpeech = tts, m_SpeechToText = Sense });
            Set(Chat, "m_ChatHistory", new List<string>());
            foreach (string field in new[] { "m_UseStreaming", "m_IsVoiceMode", "m_AutoSend" }) Set(Chat, field, true);
            foreach (string field in new[] { "m_AgentRunning", "m_AgentCurrentRoundIsTick", "m_LogStreamTimings", "m_LogAgentLoop",
                "m_LogRawLLMOutput", "m_EnableSubtitleTranslation", "m_UserSpeechActiveForAutonomy" }) Set(Chat, field, false);
            Set(Chat, "m_SystemNoticeMode", SystemNoticeMode.Hidden);
            chat.enabled = true;
        }
        public ArdyRoomSpeechPreparationFakeLLM.Pending Begin(string text)
        {
            Call(Chat, "CancelClosedRoomPreparation", "regression-new-case");
            Bridge.StopMovement("fixture-reset", "Reset synchronous preparation fixture.");
            ClearSpeech();
            Set(Chat, "m_UserSpeechActiveForAutonomy", false);
            Set(Chat, "m_LastUserTurnTime", Time.realtimeSinceStartup + .01f);
            Call(Chat, "RecordAcceptedMotionUserTurn", text);
            Sense.BeginLiveRecordingCandidateSession();
            Set(Bridge, "activeAction", true); Set(Bridge, "actionId", "prepared-fixture:" + (++task));
            Set(Bridge, "phase", "moving"); Set(Bridge, "followActive", false);
            Set(Bridge.Controller, "<IsBusy>k__BackingField", true);
            float now = Time.realtimeSinceStartup;
            var evidence = new ClosedSpeechEvidence { CaptureSessionSerial = Sense.InputCaptureSessionSerial, Revision = 1,
                AudioMs = 1450, SubmittedAudioMs = 1800, CapturedAt = now - 1.8f, ObservedAt = now - .15f,
                ClosedAt = now, StableForSeconds = .8f, Text = text, NormalizedText = ClosedSpeechEvidence.Normalize(text), CloseReason = "llm-take_turn" };
            Sense.RetainClosedSpeechEvidence(evidence);
            int before = Model.Preparations.Count; Chat.AcceptClosedSpeechEvidence(evidence);
            Check(Model.Preparations.Count == before + 1, "Actual closed-evidence entry submits one semantic preparation.");
            Chat.AcceptClosedSpeechEvidence(evidence);
            Check(Model.Preparations.Count == before + 1, "Duplicate same-capture evidence is suppressed.");
            return Model.Preparations[before];
        }
        public void Final(string source, string raw, string delivered)
        {
            if (source == "closed-stream-recovery")
            {
                object[] args = { Sense.InputCaptureSessionSerial, true, "hallucination-rejected", "", null, raw, true, 480 };
                Check((bool)Call(Sense, "TryRecoverClosedTranscript", args), "Provider double-evidence recovery fixture admitted.");
                delivered = (string)args[4];
            }
            Call(Sense, "PublishFinalTranscriptOutcome", Sense.InputCaptureSessionSerial, source,
                source == "rejected" ? "self-speech" : "", raw);
            Call(Chat, "ConfirmClosedRoomSpeechFinal", delivered);
        }
        public void Formal(string text)
        {
            // Exactly the production SendDataInternal order: work reset first,
            // accepted user ledger second. A direct Record-only test misses it.
            Call(Chat, "SetCurrentUserInput", text, text);
            Call(Chat, "RecordAcceptedMotionUserTurn", text);
            Set(Chat, "m_LastUserTurnTime", Time.realtimeSinceStartup + .01f);
            int generation = (int)Call(Chat, "BeginFormalResponseGeneration");
            Call(Chat, "DispatchFormalStream", text, null, generation, true, "Public synthetic speech preparation regression", false);
        }
        private void ClearSpeech()
        {
            ((SpeechTextBuffer)Get(Chat, "m_SentenceBuffer")).Length = 0;
            Call(Get(Chat, "m_PendingChunks"), "Clear"); Call(Get(Chat, "m_PendingClips"), "Clear");
            Set(Chat, "m_StreamComplete", true); Set(Chat, "m_FormalResponseInFlight", false);
        }
        public void Dispose()
        {
            Call(Chat, "CancelClosedRoomPreparation", "regression-finished");
            Bridge.StopMovement("fixture-reset", "Completed preparation fixture."); ClearSpeech();
            Chat.enabled = savedEnabled;
            foreach (var item in chatSaved) Set(Chat, item.Key, item.Value);
            foreach (var item in bridgeSaved) Set(Bridge, item.Key, item.Value);
            Bridge.InvalidateCurrentTargetAssessment();
            UnityEngine.Object.DestroyImmediate(Model); UnityEngine.Object.DestroyImmediate(Sense); UnityEngine.Object.DestroyImmediate(tts);
        }
    }
    private static object Get(object target, string name)
    {
        var field = target.GetType().GetField(name, Flags) ?? throw new MissingFieldException(target.GetType().Name, name);
        return field.GetValue(target);
    }
    private static void Set(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, Flags) ?? throw new MissingFieldException(target.GetType().Name, name);
        field.SetValue(target, value);
    }
    private static object Call(object target, string name, params object[] args)
    {
        var method = target.GetType().GetMethod(name, Flags) ?? throw new MissingMethodException(target.GetType().Name, name);
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static void Check(bool ok, string name) { checks++; if (!ok) throw new InvalidOperationException(name); }
}

public sealed class ArdyRoomSpeechPreparationFakeLLM : LLM
{
    public sealed class Pending
    {
        public string Input;
        public Action<bool, string, string> Callback;
        public void Deliver(string intent)
        {
            string text = (string)Newtonsoft.Json.Linq.JObject.Parse(Input)["currentUserText"];
            Callback(true, JsonConvert.SerializeObject(new { intent, origin = intent == "none" ? "none" : "user", evidence = intent == "none" ? "" : text }), "controlled-preparation");
        }
    }
    public readonly List<Pending> Preparations = new List<Pending>();
    public int Decisions, SpeechRequests, Cancellations;
    public override bool SupportsRoomTaskMessages => true;
    public override bool SupportsRoomPreparationMessages => true;
    public override void PostRoomPreparationMessage(string input, Action<bool, string, string> callback)
        => Preparations.Add(new Pending { Input = input, Callback = callback });
    public override void CancelRoomPreparationMessage() => Cancellations++;
    public override void PostRoomTaskMessage(string input, bool reviewSpeech, Action<bool, string, string> callback) => Decisions++;
    public override void PostSpeechStream(string input, Action<SpeechText> onSpeech, Action<string> onComplete,
        string imageDataUrl = null, bool recordAssistantHistory = true) => SpeechRequests++;
    public override void CancelActiveResponse() { }
}
