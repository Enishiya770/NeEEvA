using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>Real device readback and public-speech gates; controlled completed visual provenance, no network or speaker.</summary>
[InitializeOnLoad]
public static class DeviceActionReceiptRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Key = "NeEEvA.DeviceReceiptRegression.";
    private static int checks;

    public static int RunChecks()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Edit checks require edit mode.");
        checks = 0;
        using (var f = new Fixture(false))
        {
            f.Prepare("ja", "performed", false, false);
            Check(f.Receipt() == null, "A declared performed phase without actual bound-device readback cannot create a completion receipt.");
            foreach (string phase in new[] { "preparing", "state-applied", "state-applied-unconfirmed", "observed" })
            {
                Set(Get(f.Chat, "m_SceneActionFact"), "phase", phase);
                Check(f.Receipt() == null, "An intermediate or unconfirmed state cannot become a device completion receipt: " + phase);
            }
            Check((bool)Call(f.Chat, "ValidateActorTaskSpeech", f.Round), "No device lease leaves unrelated speech authority unchanged.");
        }
        WriteReport("device-receipt-edit.json", null, false);
        return checks;
    }

    public static int RunPlayChecks()
    {
        if (!Application.isPlaying) throw new InvalidOperationException("Actual device binding requires Play mode.");
        checks = 0;
        foreach (string language in new[] { "zh", "ja", "en" })
        foreach (bool on in new[] { false, true })
        using (var f = new Fixture(true))
        {
            f.Prepare(language, "performed", on);
            var before = f.Device.ReadState();
            Check(before.registered && before.on == on && before.lightEnabled == on,
                "Actual owned material and Light already match the requested state before acknowledgement.");
            Check(f.DeliverActor(), "A bound actor proposal uses its production planned-device receipt.");
            string expected = on ? language == "ja" ? "点けた" : language == "zh" ? "打开了" : "switched that lamp on"
                : language == "ja" ? "消した" : language == "zh" ? "关掉了" : "switched that lamp off";
            Check(f.Queued.Contains(expected) && f.Model.m_DataList.Last().content.Contains("code=\"" + language + "\""),
                "Device completion is factual and retains the chosen actor language, never future permission.");
            Check(f.Model.RoomCalls == 0 && f.Model.FormalCalls == 0 && !(bool)Get(f.Chat, "m_FormalResponseInFlight"),
                "The completed device bypasses additional planning, description and review and closes its reply waiter.");
            int history = f.Model.m_DataList.Count;
            Check(!f.DeliverActor() && f.Model.m_DataList.Count == history, "A duplicate completion callback cannot publish a second receipt.");
            Check(f.Validate(), "An unchanged real target and device state authorize queued speech.");
            Check(f.Device.ReadState().on == on, "Receipt generation never reapplies or delays the device operation.");
        }
        using (var f = new Fixture(true))
        {
            f.Prepare("ja", "already-satisfied", true);
            Check(f.DeliverActor() && f.Queued.Contains("切り替えてはいない"),
                "An already-satisfied target explicitly avoids claiming a newly performed switch.");
        }
        foreach (string invalidation in new[] { "opposite-power", "external-light", "replaced-result", "wrong-target", "new-generation", "new-output", "scene-cancel", "disabled-device" })
        using (var f = new Fixture(true))
        {
            f.Prepare("ja", "performed", false); Check(f.DeliverActor() && f.Validate(), "Current result is queued before invalidation.");
            switch (invalidation)
            {
                case "opposite-power": Check(f.Device.TrySetPower("newer-user-operation", true, out _, out _), "A later real device action changes power."); break;
                case "external-light": f.Device.DeviceLight.enabled = true; break;
                case "replaced-result": ((JObject)Get(f.Chat, "m_SceneObjectStateResult"))["actionId"] = "different-action"; break;
                case "wrong-target": Set(Get(f.Chat, "m_SceneActionFact"), "entityId", "different-entity"); break;
                case "new-generation": Set(f.Chat, "m_FormalResponseGeneration", f.Generation + 1); break;
                case "new-output": ((FastLoopRuntime)Get(f.Chat, "m_FastLoop")).RevokeOutput(); break;
                case "scene-cancel": Call(f.Chat, "CancelSceneAction", "device-receipt-test-cancel"); break;
                case "disabled-device": f.Device.enabled = false; break;
            }
            Check(!f.Validate() && f.Queued.Length == 0, "A stale queued device statement is revoked by the public playback gate: " + invalidation);
        }
        foreach (string invalid in new[] { "action", "entity", "partial", "pending-image", "wrong-power" })
        using (var f = new Fixture(true))
        {
            f.Prepare("en", "performed", false);
            var result = (JObject)Get(f.Chat, "m_SceneObjectStateResult");
            if (invalid == "action") result["actionId"] = "wrong";
            if (invalid == "entity") result["entityId"] = "wrong";
            if (invalid == "partial") result["partial"] = true;
            if (invalid == "pending-image") result["freshVisualObservation"] = false;
            if (invalid == "wrong-power") result["on"] = true;
            Check(f.Receipt() == null && !f.DeliverActor() && f.Queued == "",
                "A mismatched or incomplete result cannot take the completion shortcut: " + invalid);
        }
        using (var f = new Fixture(true))
        {
            f.Prepare("en", "performed", false);
            Set(f.Chat, "m_SceneTimePaused", false);
            var direct = new RoomTaskProtocol.Decision { intent = "set-object-state", origin = "user", replyMode = "receipt", replyLanguage = "en" };
            Check((bool)Call(f.Chat, "TryDeliverRoomActionReceipt", direct, f.Generation, true) && f.Queued.Contains("switched that lamp off"),
                "The existing explicit user-receipt path also receives a short actual device fact.");
            Check(f.Device.TrySetPower("later-on", true, out _, out _) && !f.Validate() && f.Queued == "",
                "User-origin device receipts share the same live playback fence.");
        }
        WriteReport("device-receipt-play.json", null, true);
        return checks;
    }

    static DeviceActionReceiptRegression()
    {
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) EditorApplication.delayCall += ExecutePlay;
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) Exit();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) && !EditorApplication.isPlaying) Exit();
        };
    }
    public static void RunBatch()
    {
        try { RunChecks(); EditorApplication.Exit(0); }
        catch (Exception error) { WriteReport("device-receipt-edit.json", Unwrap(error), false); EditorApplication.Exit(1); }
    }
    public static void RunPlayBatch()
    {
        if (!Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Use an isolated closed batch Editor without -quit.");
        foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
            if (!string.IsNullOrEmpty(setup.path) && SceneManager.GetSceneByPath(setup.path).isDirty)
                throw new InvalidOperationException("Refusing to replace a dirty saved scene.");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
        EditorApplication.isPlaying = true;
    }
    private static void ExecutePlay()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        Exception failure = null;
        try { RunPlayChecks(); } catch (Exception error) { failure = Unwrap(error); WriteReport("device-receipt-play.json", failure, true); Debug.LogException(failure); }
        SessionState.SetBool(Key + "Failed", failure != null); SessionState.SetBool(Key + "Finished", true);
        EditorApplication.isPlaying = false;
    }
    private static void Exit() { SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(SessionState.GetBool(Key + "Failed", true) ? 1 : 0); }

    private sealed class Fixture : IDisposable
    {
        private readonly GameObject host, environment;
        private readonly Material material;
        private readonly UnityEngine.AI.NavMeshData navigationData;
        public readonly ChatSample Chat;
        public readonly FastLoopIntegrationModel Model;
        public readonly ArdyRoomDialogueBridge Bridge;
        public readonly SceneObjectLightSwitch Device;
        public readonly int Generation = 8001, Round = 8003;
        private RoomTaskProtocol.Decision decision;
        public string Queued => string.Concat(((IEnumerable)Get(Chat, "m_PendingChunks")).Cast<object>().Select(x => (string)Get(x, "Text"))) + Get(Chat, "m_SentenceBuffer");
        public Fixture(bool bindDevice)
        {
            host = new GameObject("Device receipt disposable chat"); host.SetActive(false);
            Chat = host.AddComponent<ChatSample>(); Model = host.AddComponent<FastLoopIntegrationModel>();
            var button = new GameObject("Unused submit", typeof(RectTransform), typeof(Button)); button.transform.SetParent(host.transform);
            var text = new GameObject("Unused text", typeof(RectTransform), typeof(Text)); text.transform.SetParent(host.transform);
            Set(Chat, "m_CommitMsgBtn", button.GetComponent<Button>()); Set(Chat, "m_TextBack", text.GetComponent<Text>());
            Set(Chat, "m_ChatSettings", new ChatSetting { m_ChatModel = Model, m_TextToSpeech = host.AddComponent<FastLoopIntegrationTts>() });
            foreach (string field in new[] { "m_AgentRunning", "m_AgentRoundInFlight", "m_EnableNeuralHumSVC", "m_AutoStartHumSVC", "m_EnableSingingVoiceSynthesis", "m_AutoStartSVS", "m_EnableScreenVision", "m_ContinuousSceneVision", "m_EnableSubtitleTranslation", "m_PersistSubtitleSettings", "m_UserSpeechActiveForAutonomy", "m_LogAgentLoop", "m_LogRawLLMOutput" }) Set(Chat, field, false);
            Set(Chat, "m_SceneTimePaused", true);
            var navigation = host.AddComponent<ArdyRoomNavigation>(); navigation.enabled = false;
            navigationData = new UnityEngine.AI.NavMeshData(); Set(navigation, "data", navigationData);
            Set(navigation, "dataInstance", UnityEngine.AI.NavMesh.AddNavMeshData(navigationData));
            var controller = host.AddComponent<ArdyRoomLocomotionController>(); controller.enabled = false;
            Bridge = host.AddComponent<ArdyRoomDialogueBridge>(); Bridge.connectOnStart = false; Bridge.enabled = false;
            Bridge.chat = Chat; Bridge.avatar = host.AddComponent<UniVRM10.Vrm10Instance>(); Bridge.avatar.enabled = false;
            Set(Bridge, "navigation", navigation); Set(Bridge, "controller", controller); Set(Bridge, "subscribedChat", Chat);
            environment = new GameObject("Device receipt disposable environment"); Bridge.environmentRoot = environment.transform;
            var lamp = new GameObject("Synthetic registered lamp"); lamp.transform.SetParent(environment.transform);
            lamp.AddComponent<SceneObjectIdentity>().Configure(SceneObjectLampCatalog.Label, SceneObjectLampCatalog.AuthoredKey);
            var glow = GameObject.CreatePrimitive(PrimitiveType.Cube); glow.transform.SetParent(lamp.transform);
            material = new Material(Shader.Find("Standard")); material.EnableKeyword("_EMISSION"); material.SetColor("_EmissionColor", Color.white);
            glow.GetComponent<Renderer>().sharedMaterial = material;
            var light = new GameObject("Synthetic Light").AddComponent<Light>(); light.transform.SetParent(lamp.transform); light.enabled = true;
            Device = lamp.AddComponent<SceneObjectLightSwitch>();
            host.SetActive(true);
            if (bindDevice) Check(Device.TryBind(lamp.transform, glow.GetComponent<Renderer>(), light, out string reason), "Synthetic runtime device binds through its production whitelist: " + reason);
            Set(Chat, "m_MotionOutputEnabled", true); Set(Chat, "m_RoomMotionOutputEnabled", true);
            Set(Chat, "m_EnableSceneActions", true); Set(Chat, "m_EnableSceneObjectAwareness", true);
            Check(Bridge.IsConnected, "Controlled room binding is connected; no avatar movement or scene observation is claimed.");
        }
        public void Prepare(string language, string phase, bool on, bool applyDevice = true)
        {
            string action = "device-receipt-action", source = "role-device-proposal";
            Set(Chat, "m_FormalResponseGeneration", Generation); Set(Chat, "m_WorkEpoch", 8002); Set(Chat, "m_CurrentSpeechRoundId", Round);
            Set(Chat, "m_FormalTransportDispatchGeneration", -1); Set(Chat, "m_LastUserMsg", "その灯りを操作して。");
            foreach (string field in new[] { "m_EnableFastTaskLoop", "m_UseStreaming", "m_IsVoiceMode", "m_RoundInnerCheckDone", "m_RoundRepeatCheckDone" }) Set(Chat, field, true);
            Set(Chat, "m_SentenceBuffer", new SpeechTextBuffer()); Set(Chat, "m_AssistantHeardText", new StringBuilder());
            Set(Chat, "m_ChatHistory", new List<string> { "その灯りを操作して。" });
            Set(Chat, "m_RoomTaskGeneration", Generation); Set(Chat, "m_RoomTaskIntent", "set-object-state");
            Set(Chat, "m_RoomTaskActionId", action); Set(Chat, "m_RoomTaskDispatch", phase);
            Set(Chat, "m_SceneActionActive", false);
            JObject result = null;
            if (applyDevice)
            {
                if (phase == "performed" && on) Check(Device.TrySetPower("initial-opposite-state", false, out _, out _), "Prepare an actual prior off state.");
                Check(Device.TrySetPower(action, on, out var applied, out string why), "The real device operation succeeds before its controlled visual completion: " + why);
                result = JObject.Parse(JsonUtility.ToJson(applied));
                result["freshVisualObservation"] = true; result["resultCaptureId"] = "controlled-result-frame";
            }
            var factType = typeof(ChatSample).GetNestedType("SceneActionFact", BindingFlags.NonPublic);
            var fact = JsonUtility.FromJson(new JObject { ["actionId"] = action, ["intent"] = "set-object-state", ["phase"] = phase,
                ["entityId"] = Device.EntityId, ["objectConfirmed"] = true, ["semanticTargetConfirmed"] = true,
                ["requestedState"] = on ? "on" : "off", ["stateChanged"] = phase == "performed",
                ["confirmedCaptureId"] = "controlled-source-frame", ["resultCaptureId"] = "controlled-result-frame" }.ToString(), factType);
            Set(Chat, "m_SceneActionFact", fact); Set(Chat, "m_SceneResultDevice", Device);
            Set(Chat, "m_SceneResultEnvironment", environment.transform); Set(Chat, "m_SceneObjectEnvironment", environment.transform);
            Set(Chat, "m_SceneResultAvatar", Bridge.avatar); Set(Chat, "m_SceneObjectStateResult", result);
            var runtime = (FastLoopRuntime)Get(Chat, "m_FastLoop");
            var turnType = typeof(ChatSample).GetNestedType("FastLoopTurnContext", BindingFlags.NonPublic);
            var turn = Activator.CreateInstance(turnType, Flags, null, new object[] { Model, "その灯りを操作して。", null, null, Generation, 8002, Round, runtime.OutputRevision, true }, null);
            Set(turn, "HandedOff", true); Set(turn, "ActorProposal", new JObject {
                ["source"] = "unpublished-current-role-draft", ["executed"] = false, ["published"] = false,
                ["draftId"] = source, ["draftRevision"] = 1, ["text"] = "あの灯りを操作するわ。", ["purpose"] = "plan" });
            Set(Chat, "m_FastLoopTurn", turn); Call(Chat, "RememberActorTaskLanguage", Generation, language);
            var proposal = RoomTaskProtocol.ReadActorProposal((JToken)Get(turn, "ActorProposal"));
            var wireDecision = new JObject { ["intent"] = "set-object-state", ["origin"] = "actor_draft",
                ["actorDraftId"] = source, ["actorDraftRevision"] = 1, ["evidence"] = "あの灯りを操作するわ。",
                ["entityTarget"] = new JObject { ["captureId"] = "controlled-source-frame", ["entityId"] = Device.EntityId },
                ["objectState"] = on ? "on" : "off" };
            Check(RoomTaskProtocol.TryDecision(wireDecision.ToString(), "その灯りを操作して。", false,
                proposal, out decision, out string parseReason), "Actor device receipt source is bound by the production parser: " + parseReason);
        }
        public string Receipt() => (string)Call(Chat, "CurrentRoomActionReceipt", "ja");
        public bool DeliverActor() => (bool)Call(Chat, "TryDeliverActorPlannedTaskReceipt", decision, Generation);
        public bool Validate() => (bool)Call(Chat, "ValidateFastHeadSpeech", Round);
        public void Dispose()
        {
            if (Device != null && Device.IsBound) Device.RestoreOriginal(out _);
            UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(environment);
            UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(navigationData);
        }
    }
    private static void WriteReport(string name, Exception error, bool play)
    {
        Directory.CreateDirectory("Logs/control-async");
        File.WriteAllText(Path.Combine("Logs/control-async", name), new JObject { ["passed"] = error == null, ["checks"] = checks,
            ["playMode"] = play, ["networkRequests"] = 0, ["error"] = error?.ToString(),
            ["scope"] = "Production device readback, receipt generation, language/history and public playback gate. Play checks use a synthetic registered lamp with actual material/Light mutations. Scene completion, identity and image provenance are controlled fixtures; no real pixel recognition, navigation, model semantics, TTS or speaker validation." }.ToString());
    }
    private static object Get(object value, string field) => value.GetType().GetField(field, Flags).GetValue(value);
    private static void Set(object value, string field, object data) => value.GetType().GetField(field, Flags).SetValue(value, data);
    private static object Call(object value, string name, params object[] args) => value.GetType().GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args);
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
    private static Exception Unwrap(Exception error) => error is TargetInvocationException && error.InnerException != null ? Unwrap(error.InnerException) : error;
}
