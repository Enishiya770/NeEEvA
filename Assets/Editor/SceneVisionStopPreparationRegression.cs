using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Connected room stop control with staged task ownership and controlled model callbacks. No camera/model/ARDY requests.</summary>
public static class SceneVisionStopPreparationRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;

    public static int RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge)
    {
        checks = 0; string failure = null;
        Check(Application.isPlaying && chat != null && bridge != null && bridge.IsConnected && !chat.enabled,
            "Use the dormant ChatSample in the connected Play-mode room fixture.");
        try
        {
            using (var f = new Fixture(chat, bridge))
            {
                var pending = f.Begin(); int revision = f.CancelRevision;
                int initialRequests = f.Model.PendingRequests.Count;
                Call(chat, "BeginSceneVisionStopPreparation", f.Generation);
                Check(f.Model.PendingRequests.Count == initialRequests, "Repeated same-generation entry launched a duplicate preparation.");
                object camera = Get(chat, "m_SceneVisionCapture");
                var smallInput = JObject.Parse(pending.Input);
                Check(smallInput.Count == 3 && (string)smallInput["roomControlReview"] == RoomControlReviewProtocol.BodyStop &&
                    (string)smallInput["currentUserText"] == "停一下。" &&
                    (string)smallInput["activeBody"]["actionId"] == bridge.ActiveTaskId && !pending.Input.Contains("data:image"),
                    "Stop preparation retains exact current words and body identity without scene/history payloads.");
                pending.Deliver("stop-moving");
                Check(!bridge.ReservesBody && bridge.Phase == "stopped" && f.CancelRevision == revision + 1 &&
                    ReferenceEquals(camera, Get(chat, "m_SceneVisionCapture")),
                    "Exact-task stop did not complete without any camera request/completion.");
                object[] result = { f.Generation, null, null };
                Check((bool)Call(chat, "TryGetSceneVisionPreparedStop", result) && (string)result[1] == bridge.ActionId &&
                    (string)result[2] == "stop-submitted", "Formal planner cannot reuse the already committed stop identity.");
                string id = bridge.ActionId;
                pending.Deliver("stop-moving");
                result = new object[] { f.Generation, null, null };
                Check((bool)Call(chat, "TryGetSceneVisionPreparedStop", result) && bridge.ActionId == id && f.CancelRevision == revision + 1,
                    "Duplicate callback or identity read submitted a second cancellation.");

                foreach (string intent in new[] { "none", "follow", "approach", "inspect" })
                {
                    pending = f.Begin(); revision = f.CancelRevision; pending.Deliver(intent);
                    Check(bridge.ReservesBody && f.CancelRevision == revision, "Non-stop preparation executed early: " + intent);
                }
                foreach (string json in new[] {
                    "{\"intent\":\"stop-moving\",\"origin\":\"autonomous\",\"evidence\":\"停一下。\"}",
                    "{\"intent\":\"stop-moving\",\"origin\":\"user\",\"evidence\":\"别的用户原话\"}",
                    "{\"intent\":\"stop-moving\",\"origin\":\"user\",\"evidence\":\"停一下。\",\"extra\":true}", "{}", "not json" })
                {
                    pending = f.Begin(); revision = f.CancelRevision; pending.Callback(true, json, "controlled-malformed");
                    Check(bridge.ReservesBody && f.CancelRevision == revision, "Invalid stop schema/source obtained body authority.");
                }

                pending = f.Begin(); revision = f.CancelRevision;
                Call(chat, "BeginFormalResponseGeneration"); pending.Deliver("stop-moving");
                Check(bridge.ReservesBody && f.CancelRevision == revision, "Old formal generation stopped a current task.");

                pending = f.Begin(); revision = f.CancelRevision;
                Call(chat, "SetCurrentUserInput", "继续走，不要停。", "继续走，不要停。");
                Call(chat, "RecordAcceptedMotionUserTurn", "继续走，不要停。"); pending.Deliver("stop-moving");
                Check(bridge.ReservesBody && f.CancelRevision == revision, "New accepted user turn failed to invalidate the earlier stop.");

                pending = f.Begin(); revision = f.CancelRevision;
                Set(chat, "m_UserSpeechActiveForAutonomy", true); pending.Deliver("stop-moving");
                Check(bridge.ReservesBody && f.CancelRevision == revision, "New user speech allowed late prior stop semantics.");

                pending = f.Begin(); revision = f.CancelRevision;
                int operation = bridge.Controller.OperationRevision;
                Set(bridge.Controller, "<OperationRevision>k__BackingField", operation + 1); pending.Deliver("stop-moving");
                Check(bridge.ReservesBody && f.CancelRevision == revision && !f.HasPreparedStop(), "Changed operation was stopped by a stale result.");
                Set(bridge.Controller, "<OperationRevision>k__BackingField", operation);

                f.Prepare(); Set(bridge, "followActive", true); Set(bridge, "hasControllerOperation", true);
                Set(bridge, "operationRevision", bridge.Controller.OperationRevision);
                Call(chat, "BeginSceneVisionStopPreparation", f.Generation);
                pending = f.Model.PendingRequests[f.Model.PendingRequests.Count - 1];
                int followOperation = bridge.Controller.OperationRevision;
                Set(bridge.Controller, "<OperationRevision>k__BackingField", followOperation + 1);
                Set(bridge, "operationRevision", followOperation + 1);
                pending.Deliver("stop-moving");
                Check(!bridge.IsFollowing && bridge.Phase == "stopped" && f.HasPreparedStop(),
                    "A new leg of the same owned follow does not invalidate the user's stop; unrelated task replacements remain fenced.");

                pending = f.Begin(); revision = f.CancelRevision;
                Set(bridge, "actionId", "replacement-task"); pending.Deliver("stop-moving");
                Check(bridge.ReservesBody && f.CancelRevision == revision && bridge.ActiveTaskId == "replacement-task",
                    "Replacement task was stopped by a stale result.");

                pending = f.Begin(); revision = f.CancelRevision; int cancels = f.Model.Cancellations;
                object state = Get(chat, "m_SceneVisionStopPreparation");
                Call(chat, "CheckSceneVisionStopDeadline", state, (float)Get(state, "deadline") + .001f);
                Check(f.Model.Cancellations == cancels + 1 && Get(chat, "m_SceneVisionStopPreparation") == null,
                    "Bounded timeout failed to release its own preparation transport.");
                pending.Deliver("stop-moving");
                Check(bridge.ReservesBody && f.CancelRevision == revision, "Timed-out stop revived after the deadline.");

                pending = f.Begin(); revision = f.CancelRevision; cancels = f.Model.Cancellations;
                object closed = f.ClosedPreparation();
                Set(chat, "m_RoomSpeechPreparation", closed);
                Call(chat, "CancelSceneVisionStopPreparation"); pending.Deliver("stop-moving");
                Check(f.Model.Cancellations == cancels && ReferenceEquals(Get(chat, "m_RoomSpeechPreparation"), closed) &&
                    bridge.ReservesBody && f.CancelRevision == revision, "Old visual preparation cancelled the new closed-speech slot owner.");
                int before = f.Model.PendingRequests.Count;
                Call(chat, "BeginSceneVisionStopPreparation", f.Generation);
                Check(f.Model.PendingRequests.Count == before && f.Model.Cancellations == cancels &&
                    ReferenceEquals(Get(chat, "m_RoomSpeechPreparation"), closed), "Existing closed-speech preparation was replaced by visual stop preparation.");
                Set(chat, "m_RoomSpeechPreparation", null);

                foreach (string guard in new[] { "autonomous", "closed-eyes", "idle", "unsupported" })
                {
                    f.Prepare(); before = f.Model.PendingRequests.Count;
                    if (guard == "autonomous") Set(chat, "m_AgentCurrentRoundIsTick", true);
                    if (guard == "closed-eyes") Set(chat, "m_AgentEyesOpen", false);
                    if (guard == "idle") { Set(bridge, "activeAction", false); Set(bridge, "followActive", false); }
                    if (guard == "unsupported") f.Model.SupportPreparation = false;
                    Call(chat, "BeginSceneVisionStopPreparation", f.Generation);
                    Check(f.Model.PendingRequests.Count == before && Get(chat, "m_SceneVisionStopPreparation") == null,
                        "Stop preparation bypassed its scope guard: " + guard);
                }
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            string path = Path.GetFullPath("Server/ARDY/runtime/scene-vision-stop-preparation-regression.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(new { passed = failure == null, checks, failure,
                checkedAtUtc = DateTime.UtcNow.ToString("o"),
                scope = "Actual ChatSample preparation API and connected room controller cancellation. Controlled semantic callbacks, staged task ownership; no camera, model, ARDY or TTS requests. Does not measure semantic or audible stop latency."
            }, Formatting.Indented) + "\n");
        }
        return checks;
    }

    private sealed class Fixture : IDisposable
    {
        public readonly ChatSample Chat;
        public readonly ArdyRoomDialogueBridge Bridge;
        public readonly SceneVisionStopPreparationFakeLLM Model;
        private readonly TTS tts;
        private readonly Dictionary<string, object> chatSaved = new Dictionary<string, object>(), bridgeSaved = new Dictionary<string, object>();
        private readonly Dictionary<string, object> controllerSaved = new Dictionary<string, object>();
        private readonly bool savedEnabled;
        private int task;
        public int Generation;
        public int CancelRevision => (int)Get(Bridge.Controller, "revision");
        public Fixture(ChatSample chat, ArdyRoomDialogueBridge bridge)
        {
            Chat = chat; Bridge = bridge; savedEnabled = chat.enabled;
            Model = chat.gameObject.AddComponent<SceneVisionStopPreparationFakeLLM>(); tts = chat.gameObject.AddComponent<TTS>();
            foreach (string field in new[] { "m_ChatSettings", "m_UseStreaming", "m_IsVoiceMode", "m_AutoSend", "m_AgentRunning",
                "m_AgentCurrentRoundIsTick", "m_AgentEyesOpen", "m_EnableScreenVision", "m_LogStreamTimings", "m_LogAgentLoop",
                "m_EnableSubtitleTranslation", "m_SystemNoticeMode", "m_UserSpeechActiveForAutonomy" }) chatSaved[field] = Get(chat, field);
            foreach (string field in new[] { "activeAction", "actionId", "phase", "reason", "lastAttemptActionId", "lastAttemptPhase",
                "lastAttemptReason", "lastAttemptTime", "followActive", "followNeedsNewTarget", "followUser", "hasControllerOperation", "operationRevision" })
                bridgeSaved[field] = Get(bridge, field);
            // These two fields are explicitly staged by this synchronous helper. In
            // particular a later case may set IsBusy while ExecutionState still says
            // Cancelled; the bridge correctly avoids a second terminal cancellation,
            // so production StopMovement is not responsible for undoing our fake flag.
            foreach (string field in new[] { "<IsBusy>k__BackingField", "<OperationRevision>k__BackingField" })
                controllerSaved[field] = Get(bridge.Controller, field);
            Set(chat, "m_ChatSettings", new ChatSetting { m_ChatModel = Model, m_TextToSpeech = tts });
            foreach (string field in new[] { "m_UseStreaming", "m_IsVoiceMode", "m_AutoSend", "m_AgentRunning", "m_AgentEyesOpen", "m_EnableScreenVision" }) Set(chat, field, true);
            foreach (string field in new[] { "m_AgentCurrentRoundIsTick", "m_UserSpeechActiveForAutonomy", "m_LogStreamTimings", "m_LogAgentLoop", "m_EnableSubtitleTranslation" }) Set(chat, field, false);
            Set(chat, "m_SystemNoticeMode", SystemNoticeMode.Hidden);
            chat.enabled = true;
        }
        public void Prepare()
        {
            Call(Chat, "CancelSceneVisionStopPreparation"); Call(Chat, "CancelClosedRoomPreparation", "vision-stop-fixture");
            Bridge.StopMovement("fixture-reset", "Reset synchronous visual stop fixture.");
            Set(Chat, "m_UserSpeechActiveForAutonomy", false); Set(Chat, "m_AgentCurrentRoundIsTick", false); Set(Chat, "m_AgentEyesOpen", true);
            Model.SupportPreparation = true;
            Call(Chat, "RecordAcceptedMotionUserTurn", "停一下。");
            Generation = (int)Call(Chat, "BeginFormalResponseGeneration");
            Set(Bridge, "activeAction", true); Set(Bridge, "actionId", "vision-stop-fixture:" + (++task));
            Set(Bridge, "phase", "moving"); Set(Bridge, "followActive", false);
            Set(Bridge.Controller, "<IsBusy>k__BackingField", true);
        }
        public SceneVisionStopPreparationFakeLLM.Pending Begin()
        {
            Prepare(); int before = Model.PendingRequests.Count;
            Call(Chat, "BeginSceneVisionStopPreparation", Generation);
            Check(Model.PendingRequests.Count == before + 1, "Actual visual stop entry did not submit one pure-text preparation.");
            return Model.PendingRequests[before];
        }
        public bool HasPreparedStop()
        { object[] args = { Generation, null, null }; return (bool)Call(Chat, "TryGetSceneVisionPreparedStop", args); }
        public object ClosedPreparation()
        {
            // Presence alone must reserve the channel, even before the recording's
            // remaining freshness/confirmation fields have been populated.
            var type = typeof(ChatSample).GetNestedType("RoomSpeechPreparation", BindingFlags.NonPublic);
            object closed = Activator.CreateInstance(type); Set(closed, "provider", Model); return closed;
        }
        public void Dispose()
        {
            try
            {
                Call(Chat, "CancelSceneVisionStopPreparation"); Call(Chat, "CancelClosedRoomPreparation", "vision-stop-fixture-finished");
                Bridge.StopMovement("fixture-reset", "Completed visual stop fixture.");
            }
            finally
            {
                Chat.enabled = savedEnabled;
                foreach (var entry in chatSaved) Set(Chat, entry.Key, entry.Value);
                foreach (var entry in bridgeSaved) Set(Bridge, entry.Key, entry.Value);
                foreach (var entry in controllerSaved) Set(Bridge.Controller, entry.Key, entry.Value);
                Bridge.InvalidateCurrentTargetAssessment();
                UnityEngine.Object.DestroyImmediate(Model); UnityEngine.Object.DestroyImmediate(tts);
            }
        }
    }

    private static object Get(object target, string field) => target.GetType().GetField(field, Flags).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Flags).SetValue(target, value);
    private static object Call(object target, string method, params object[] args)
    {
        try { return target.GetType().GetMethod(method, Flags).Invoke(target, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static void Check(bool condition, string error) { checks++; if (!condition) throw new InvalidOperationException(error); }
}

public sealed class SceneVisionStopPreparationFakeLLM : LLM
{
    public sealed class Pending
    {
        public string Input;
        public Action<bool, string, string> Callback;
        public void Deliver(string intent)
        {
            string text = (string)JObject.Parse(Input)["currentUserText"];
            Callback(true, JsonConvert.SerializeObject(new { intent, origin = intent == "none" ? "none" : "user",
                evidence = intent == "none" ? "" : text }), "controlled-pure-text-preparation");
        }
    }
    public readonly List<Pending> PendingRequests = new List<Pending>();
    public int Cancellations;
    public bool SupportPreparation = true;
    public override bool SupportsRoomTaskMessages => true;
    public override bool SupportsRoomPreparationMessages => SupportPreparation;
    public override void PostRoomPreparationMessage(string input, Action<bool, string, string> callback)
        => PendingRequests.Add(new Pending { Input = input, Callback = callback });
    public override void CancelRoomPreparationMessage() => Cancellations++;
    public override void CancelActiveResponse() { }
}
