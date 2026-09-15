using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>Real room RGB and production Chat dispatch, with a deliberately controlled in-process LLM transport.</summary>
[InitializeOnLoad]
public static class NevaSceneVisionIntegrationRegression
{
    private const string Key = "NeEEvA.SceneVision.Integration.";
    private const string ChatScene = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
    private const string RoomScene = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day.unity";
    private const string AvatarPath = "Assets/Model/NeEEvA.vrm";
    private const string ChatName = "Scene Vision Integration Chat", AvatarName = "Scene Vision Integration Avatar";
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static ChatSample chat;
    private static ArdyRoomDialogueBridge bridge;
    private static NevaSceneVisionIntegrationModel model;
    private static Vrm10Instance avatar;
    private static JObject report;
    private static readonly Stack<IEnumerator> routines = new Stack<IEnumerator>();
    private static int lastFrame, checks;
    private static float started;
    private static string Output => SessionState.GetString(Key + "Output", "");
    private static bool NativeEnabled => Argument("-nevaSceneActionNative", "0") == "1";
    private static bool LiveQwenEnabled => Argument("-nevaSceneActionQwen", "0") == "1";
    private static bool ProductionActionEnabled => Argument("-nevaSceneActionProduction", "0") == "1";
    private static bool ActiveViewEnabled => Argument("-nevaSceneActiveView", "0") == "1";
    private static bool SceneObjectsEnabled => Argument("-nevaSceneObjects", "0") == "1";
    private static bool LiveObjectReopenEnabled => Argument("-nevaSceneObjectReopenQwen", "0") == "1";
    private static bool ObjectMatchingEnabled => Argument("-nevaSceneObjectMatching", "0") == "1";
    private static bool LiveObjectMatchingEnabled => Argument("-nevaSceneObjectMatchingQwen", "0") == "1";
    private static bool ObjectApproachNativeEnabled => Argument("-nevaSceneObjectApproachNative", "0") == "1";
    private static bool MotionReadinessEnabled => Argument("-nevaRoomMotionReadiness", "0") == "1";
    private static bool DecisionRepairEnabled => Argument("-nevaDecisionRepair", "0") == "1";
    private static bool DecisionRepairNativeEnabled => Argument("-nevaDecisionRepairNative", "0") == "1";
    private static bool HalfTurnNativeEnabled => Argument("-nevaHalfTurnNative", "0") == "1";
    private static bool ViewPositionEnabled => Argument("-nevaViewPosition", "0") == "1";
    private static bool ViewPositionNativeEnabled => Argument("-nevaViewPositionNative", "0") == "1";
    private static bool ResponseLatencyEnabled => Argument("-nevaResponseLatency", "0") == "1";
    private static bool FirstAudioRegressionEnabled => Argument("-nevaFirstAudioRegression", "0") == "1";
    private static int RecordedFurnitureIndex => int.Parse(Argument("-nevaRecordedFurnitureIndex", "-1"));
    private static bool UsesNativeService => NativeEnabled || LiveQwenEnabled || ProductionActionEnabled || ActiveViewEnabled || ObjectApproachNativeEnabled || DecisionRepairNativeEnabled || HalfTurnNativeEnabled || ViewPositionNativeEnabled || ResponseLatencyEnabled;

    static NevaSceneVisionIntegrationRegression()
    {
        EditorApplication.update += Update;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
                try { Initialize(); } catch (Exception error) { Finish(error); }
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) Exit();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) && !EditorApplication.isPlaying) Exit();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Only run in the isolated validation project, in graphics-enabled batch mode without -quit.");
        try
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Play already active.");
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
                if (!string.IsNullOrEmpty(setup.path) && SceneManager.GetSceneByPath(setup.path).isDirty)
                    throw new InvalidOperationException("Refusing to replace a modified validation scene.");
            string output = Path.GetFullPath(Argument("-nevaSceneVisionOutput", "Logs/scene-vision-integration"));
            Directory.CreateDirectory(output); SessionState.SetString(Key + "Output", output);
            report = new JObject { ["status"] = "preparing", ["phase"] = "load-actual-chat-configuration", ["chatScene"] = ChatScene,
                ["requiredPromptResources"] = new JArray("Assets/AIChatTookit/Prompts/persona.txt", "Assets/AIChatTookit/Prompts/language.txt",
                    "Assets/AIChatTookit/Prompts/behavior.txt", "Assets/AIChatTookit/Prompts/Skills/singing.txt"),
                ["resourceRequirement"] = "Copy the actual prompt assets and their original .meta GUIDs to the isolated project. Do not replace missing serialized references with fixture prompts." };
            // Read actual serialized avatar placement in Edit mode. No scene Start/Play is run or scene saved.
            Scene original = EditorSceneManager.OpenScene(ChatScene, OpenSceneMode.Single);
            var originalChat = original.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<ChatSample>(true)).Single();
            var originalAnimator = (Animator)Get(originalChat, "m_Animator");
            Vrm10Instance reference = originalChat.GetComponent<ArdyRoomDialogueBridge>()?.avatar;
            if (reference == null && originalAnimator != null) reference = originalAnimator.GetComponentInParent<Vrm10Instance>();
            if (reference == null) throw new InvalidOperationException("The actual Chat scene must explicitly bind its VRM through the room bridge or Animator.");
            Vector3 position = reference.transform.position, scale = reference.transform.lossyScale;
            Quaternion rotation = reference.transform.rotation;
            var initial = new JObject { ["status"] = "running", ["unityVersion"] = Application.unityVersion,
                ["scope"] = "Copied real SailingMoon Day room and imported NeEEvA VRM at the actual Chat scene's serialized placement. Baseline cases use real head-view GPU RGB and production Chat dispatch with controlled model callbacks. Explicitly enabled nativeEvidence, productionActionEvidence or liveQwenEvidence fields describe additional native ARDY or actual shared-Qwen requests separately.",
                ["limitations"] = "Static environment replay, not a live desktop/TV feed or full user dialogue. No ASR or spoken TTS. Controlled decisions do not prove Qwen understanding; only explicitly enabled liveQwenEvidence represents actual model decisions. Native camera orientation/self-occlusion/cancellation checks also have a separate camera regression.",
                ["chatScene"] = ChatScene, ["chatSceneSha256"] = Hash(ChatScene), ["roomScene"] = RoomScene,
                ["roomSceneSha256"] = Hash(RoomScene), ["avatarAsset"] = AvatarPath,
                ["avatarPlacement"] = JObject.Parse(JsonUtility.ToJson(new Placement { position = position, rotation = rotation, scale = scale })),
                ["cases"] = new JArray(), ["wireRequests"] = new JArray() };
            var settings = (ChatSetting)Get(originalChat, "m_ChatSettings");
            var configured = settings?.m_ChatModel as ChatQW;
            if (configured == null) throw new InvalidOperationException("Actual Chat scene does not bind a ChatQW provider.");
            if (ResponseLatencyEnabled)
            {
                var configuredTts = settings.m_TextToSpeech as GPTSoVITSFASTAPI;
                if (configuredTts == null) throw new InvalidOperationException("Actual Chat scene does not bind the streaming TTS provider.");
                var ttsConfiguration = new JObject();
                for (Type t = configuredTts.GetType(); t != typeof(MonoBehaviour); t = t.BaseType)
                    foreach (var field in t.GetFields(Flags | BindingFlags.DeclaredOnly))
                        if (!field.IsStatic && (field.IsPublic || field.IsDefined(typeof(SerializeField), true)) &&
                            (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(string)))
                            ttsConfiguration[field.Name] = JToken.FromObject(field.GetValue(configuredTts) ?? "");
                initial["actualTtsConfiguration"] = ttsConfiguration;
            }
            foreach (var property in report.Properties()) if (initial[property.Name] == null) initial[property.Name] = property.Value.DeepClone();
            report = initial;
            if (RecordedFurnitureIndex >= 0)
            {
                if (!ObjectApproachNativeEnabled || RecordedFurnitureIndex > 6) throw new InvalidOperationException("Recorded fixture requires explicit native approach.");
                // user-editor.log:118528 records actualRoot.y=-.72. The separate
                // avatarGround.y=-.69721955 includes the imported sole offset.
                if (RecordedFurnitureIndex == 6) position = new Vector3(-4.1764088f, -.72f, -3.9058204f);
                initial["recordedFixture"] = new JObject { ["regionIndex"] = RecordedFurnitureIndex,
                    ["actualFixtureRoot"] = JObject.Parse(JsonUtility.ToJson(position)),
                    ["scope"] = "Replay recorded target region with actor placed before Play; no teleport during action. Original serialized placement retained separately." };
            }
            var configuration = new JObject();
            initial["actualRoleConfiguration"] = configuration;
            foreach (string field in new[] { "m_Backend", "m_LocalModelName", "m_EnableThinking", "m_KeepRecentImages", "m_MaxPromptTokens", "m_EphemeralMaxTokens", "m_EphemeralHistoryMessages" })
                configuration[field] = JToken.FromObject(Get(configured, field));
            foreach (string field in new[] { "m_PromptFiles", "m_SkillFiles" })
                configuration[field] = new JArray(((TextAsset[])Get(configured, field) ?? Array.Empty<TextAsset>()).Select(asset => {
                    string path = AssetDatabase.GetAssetPath(asset);
                    if (asset == null || string.IsNullOrEmpty(path)) throw new InvalidOperationException("Unresolved actual prompt/skill asset: " + field);
                    return new JObject { ["path"] = path, ["sha256"] = Hash(path) };
                }));
            initial["phase"] = "prepare-real-room-play-fixture";
            Scene room = EditorSceneManager.OpenScene(RoomScene, OpenSceneMode.Single);
            var roots = room.GetRootGameObjects();
            foreach (var root in roots)
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true)) if (behaviour != null) behaviour.enabled = false;
            var environment = new GameObject("Scene Vision Integration Environment");
            foreach (var root in roots) root.transform.SetParent(environment.transform, true);
            ArdyRoomGeometryPreparation.Prepare(environment.transform, false);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            if (prefab == null) throw new InvalidOperationException("Missing imported avatar.");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab); instance.name = AvatarName;
            instance.transform.SetPositionAndRotation(position, rotation); instance.transform.localScale = scale;
            instance.GetComponent<Vrm10Instance>().enabled = false;
            foreach (var animator in instance.GetComponentsInChildren<Animator>(true))
            { animator.enabled = false; animator.runtimeAnimatorController = null; animator.applyRootMotion = false; }
            var host = new GameObject(ChatName); host.SetActive(false);
            var sample = host.AddComponent<ChatSample>(); sample.enabled = false;
            var button = new GameObject("Commit", typeof(RectTransform), typeof(Button)).GetComponent<Button>();
            var text = new GameObject("Output", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
            button.transform.SetParent(host.transform, false); text.transform.SetParent(host.transform, false);
            Set(sample, "m_CommitMsgBtn", button); Set(sample, "m_TextBack", text);
            var outputAudio = host.AddComponent<AudioSource>();
            // Match the actual room scene. Production Awake must normalize this
            // empty startup source before installing the AEC audio filter.
            outputAudio.playOnAwake = true; outputAudio.loop = false;
            Set(sample, "m_AudioSource", outputAudio);
            // Editor-assembly MonoBehaviours are runtime-only fixture components. Do not
            // serialize them into the temporary scene across the Play domain reload.
            Set(sample, "m_ChatSettings", new ChatSetting());
            Set(sample, "m_Animator", instance.GetComponent<Animator>());
            Set(sample, "m_SceneVisionAvatar", instance.GetComponent<Vrm10Instance>());
            // Baseline pixel/occlusion cases deliberately retain the original strict path.
            // Object-awareness is enabled only in its explicit additional suite below.
            Set(sample, "m_EnableSceneObjectAwareness", false);
            Set(sample, "m_EnableSceneObjectTargetMatching", false);
            foreach (string field in new[] { "m_AutoStartHumSVC", "m_AutoStartSVS", "m_EnableLatencyFiller", "m_EnableSubtitleTranslation", "m_PersistSubtitleSettings" }) Set(sample, field, false);
            var roomBridge = host.AddComponent<ArdyRoomDialogueBridge>(); roomBridge.connectOnStart = false;
            roomBridge.chat = sample; roomBridge.avatar = instance.GetComponent<Vrm10Instance>();
            roomBridge.environmentRoot = environment.transform; roomBridge.serviceUrl = UsesNativeService ? "http://127.0.0.1:8093" : "http://127.0.0.1:1";
            roomBridge.importedReference = ArdyLocomotionAvatarReference.FromImportedPrefab(prefab.GetComponent<Vrm10Instance>(), AvatarPath);
            roomBridge.userHead = new GameObject("Explicit geometry-only user head").transform;
            roomBridge.userHead.position = position + rotation * Vector3.forward * 2f + Vector3.up * 1.6f;
            roomBridge.excludedVisualRoots = environment.GetComponentsInChildren<Transform>(true).Where(x => x.name == "水面").ToArray();
            var eyes = instance.AddComponent<NevaEyeTracking>(); eyes.Configure(instance.GetComponent<Vrm10Instance>());
            var camera = host.AddComponent<NevaSceneVisionCapture>(); camera.Configure(instance.GetComponent<Vrm10Instance>(), eyes);
            Set(sample, "m_SceneVisionCapture", camera);
            host.SetActive(true);
            SessionState.SetString(Key + "Report", initial.ToString());
            SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + (UsesNativeService ? 285f : 180f));
            EditorApplication.isPlaying = true;
        }
        catch (Exception error)
        {
            Debug.LogException(error); SessionState.SetBool(Key + "Active", false);
            if (!string.IsNullOrEmpty(Output))
            {
                if (report == null) report = new JObject();
                report["status"] = "failed"; report["error"] = error.ToString();
                Directory.CreateDirectory(Output); File.WriteAllText(Path.Combine(Output, "report.json"), report.ToString() + "\n");
            }
            EditorApplication.Exit(1);
        }
    }

    [Serializable] private sealed class Placement { public Vector3 position, scale; public Quaternion rotation; }

    private static void Initialize()
    {
        report = JObject.Parse(SessionState.GetString(Key + "Report", "{}")); checks = 0;
        Check(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null && SystemInfo.supportsAsyncGPUReadback,
            "A real graphics device with async readback is required; Null graphics cannot prove camera capture.");
        report["graphicsDevice"] = SystemInfo.graphicsDeviceName;
        chat = GameObject.Find(ChatName).GetComponent<ChatSample>();
        report["initialBeforeHelpersOrResetGuards"] = GuardSnapshot();
        var initialAudio = (AudioSource)Get(chat, "m_AudioSource");
        Check(initialAudio != null && initialAudio.clip == null && !initialAudio.playOnAwake && !chat.IsVoiceOutputPlaying,
            "Production Awake did not make the actual-scene empty Play On Awake source idle before any fixture reset.");
        model = chat.gameObject.AddComponent<NevaSceneVisionIntegrationModel>();
        bridge = chat.GetComponent<ArdyRoomDialogueBridge>(); avatar = GameObject.Find(AvatarName).GetComponent<Vrm10Instance>();
        model.Initialize(Output);
        Set(chat, "m_ChatSettings", new ChatSetting { m_ChatModel = model, m_TextToSpeech = chat.gameObject.AddComponent<TTS>() });
        bridge.Connect();
        report["followLifetimeChecks"] = ArdyRoomFollowRegression.RunChecks(chat, bridge);
        report["transportChecks"] = SceneVisionTransportRegression.RunChecks();
        report["roomDecisionTransportChecks"] = RoomDecisionTransportRegression.RunChecks();
        report["timeChecks"] = SceneTimeRegression.RunChecks();
        report["activeViewProtocolChecks"] = SceneActiveViewRegression.RunProtocolChecks();
        report["sceneObjectProtocolChecks"] = SceneObjectProtocolRegression.RunChecks();
        report["viewPositionProtocolChecks"] = SceneViewPositionProtocolRegression.RunChecks();
        report["viewPositionGeometryChecks"] = ArdyViewPositionRegression.RunChecks();
        report["sceneFactCompactionChecks"] = SceneFactCompactionRegression.RunChecks();
        ChatLatencyTransportRegression.RunInteractive();
        report["chatLatencyTransportPassed"] = true;
        report["publicBoundaryChecks"] = RoleOutputPublicBoundaryRegression.RunOrThrow();
        report["preparedTurnBoundaryChecks"] = PreparedTurnBoundaryRegression.RunOrThrow();
        report["preparedTurnBoundaryHookChecks"] = PreparedTurnBoundaryHookRegression.RunOrThrow();
        FirstReplyTransportRegression.RunChecks(Output);
        report["firstReplyTransportEvidence"] = FirstReplyTransportRegression.LastReport;
        report["serviceDeadlineDiagnosticChecks"] = ArdyRoomStreamDiagnosticsRegression.RunServiceErrorChecks();
        // Controlled callbacks below are deliberately exercised faster than real inference.
        // The production limiter is tested above and restored before the real Qwen case.
        Set(chat, "m_AutonomyReviewsPerMinute", 1000); Set(chat, "m_AutonomyReviewMinimumSpacing", 0f);
        Call(chat, "BeginSceneTimeSession");
        bool originalBusy = bridge.Controller.IsBusy;
        int originalOperation = bridge.Controller.OperationRevision;
        report["stopPreparationChecks"] = SceneVisionStopPreparationRegression.RunChecks(chat, bridge);
        Check(bridge.Controller.IsBusy == originalBusy && bridge.Controller.OperationRevision == originalOperation && !bridge.ReservesBody,
            "Stop-preparation regression leaked a staged controller/body state into the scene integration fixture.");
        chat.enabled = true;
        Set(chat, "m_AgentRunning", true); Set(chat, "m_AgentEyesOpen", true); Set(chat, "m_EnableScreenVision", true);
        Set(chat, "m_UseStreaming", true); Set(chat, "m_IsVoiceMode", true);
        Set(chat, "m_ChatHistory", new List<string>());
        started = Time.realtimeSinceStartup; lastFrame = -1; routines.Clear(); routines.Push(Scenario());
    }

    private static IEnumerator Scenario()
    {
        yield return null; // Allow ordinary Start with the no-network base TTS and disabled service-autostart flags.
        Check(!(bool)Get(chat, "m_AutoStartHumSVC") && !(bool)Get(chat, "m_AutoStartSVS"), "Service autostart was not disabled.");
        report["afterStartBeforeFixtureResetGuards"] = GuardSnapshot();
        Check(!((AudioSource)Get(chat, "m_AudioSource")).playOnAwake && !chat.IsVoiceOutputPlaying,
            "Ordinary Start reactivated the normalized empty startup source before the first autonomy probe.");
        ResetRound();
        Set(chat, "m_WorkReviewOpen", false); Set(chat, "m_WorkNoProgress", false);
        Set(chat, "m_UserTurnAwaitingReplySince", -1f); Set(chat, "m_LastUserMsg", ""); Set(chat, "m_LastUserEvidence", "");
        Set(chat, "m_AgentGracefulShutdownPending", false);
        Set(chat, "m_MonologueBackstopTurns", 40); Set(chat, "m_MaxConsecutiveAITurns", 8);
        Check(!chat.IsVoiceOutputPlaying, "The isolated silent fixture AudioSource is not physically idle.");
        int operation = bridge.Controller.OperationRevision;
        Vector3 root = avatar.transform.position;

        if (FirstAudioRegressionEnabled)
        {
            string recorded = Environment.GetEnvironmentVariable("NEEEVA_RECORDED_REVIEW_LOG");
            if (!string.IsNullOrEmpty(recorded))
                report["recordedPublicBoundaryChecks"] = RoleOutputPublicBoundaryRegression.RunRecordedPlayOrThrow(recorded);
            AgentSingingGoalRegression.RunInteractive();
            report["persistentSingingGoalPassed"] = true;
            yield return ChatLatencyTransportRegression.RunPublicBoundaryHttpChecks(Output);
            report["publicBoundaryHttpEvidence"] = ChatLatencyTransportRegression.PublicBoundaryHttpReport;
            report["sourceReferenceCopyChecks"] = FirstAudioReferenceCapture.RunSyntheticChecks(Output);
            yield return FormalAudioHandoffRegression.RunChecks(chat, Output);
            report["formalAudioHandoffEvidence"] = FormalAudioHandoffRegression.LastReport;
            yield return FirstReplyFailureAudioRegression.RunChecks(chat, bridge, Output);
            report["firstReplyFailureAudioEvidence"] = FirstReplyFailureAudioRegression.LastReport;
            yield return FirstReplyChatIntegrationRegression.RunChecks(chat, bridge, Output);
            report["firstReplyChatEvidence"] = FirstReplyChatIntegrationRegression.LastReport;
            if (Environment.GetEnvironmentVariable("NEEEVA_LISTENING_LIVE") == "1")
            {
                yield return PreparedListeningLiveRegression.RunChecks((JObject)report["actualRoleConfiguration"], Output);
                report["liveListeningEvidence"] = PreparedListeningLiveRegression.LastReport;
                Check((bool)PreparedListeningLiveRegression.LastReport["passed"], "Live listening semantic boundary cases failed; see retained report.");
            }
            Finish(null); yield break;
        }

        if (RecordedFurnitureIndex >= 0 || DecisionRepairNativeEnabled || HalfTurnNativeEnabled || ViewPositionEnabled || ViewPositionNativeEnabled || ResponseLatencyEnabled)
        {
            var recordedIdle = avatar.GetComponent<Animator>();
            recordedIdle.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/AIChatTookit/Animation/Animator Controller.controller");
            Check(recordedIdle.runtimeAnimatorController != null, "Actual idle controller required for recorded fixture.");
            recordedIdle.applyRootMotion = false; recordedIdle.enabled = true; recordedIdle.Rebind(); recordedIdle.Update(0f);
            Set(chat, "m_EnableSceneObjectAwareness", true); Set(chat, "m_EnableSceneObjectTargetMatching", false);
            yield return WaitFrames(3);
            Call(chat, "EnsureSceneVisionCapture");
            Check(chat.SceneVisionCapture != null, "Recorded fixture uses the actual avatar scene camera.");
            chat.SceneVisionCapture.SetObserving(true);
            if (ResponseLatencyEnabled)
            {
                yield return SceneDecisionRepairRegression.RunNativeChecks(chat, bridge, model, Output);
                report["responseSetupApproachEvidence"] = SceneDecisionRepairRegression.LastReport;
                yield return SceneResponseLatencyRegression.RunChecks(chat, bridge, model,
                    (JObject)report["actualRoleConfiguration"], (JObject)report["actualTtsConfiguration"], Output);
                report["responseLatencyEvidence"] = SceneResponseLatencyRegression.LastReport;
                Check((string)SceneResponseLatencyRegression.LastReport["status"] == "completed", "Response latency run retained failed or missing-audio cases; see response-latency/report.json.");
            }
            else if (ViewPositionNativeEnabled)
            {
                // Reach the established original-room observation station by real
                // forward movement first. No root relocation creates test space.
                yield return SceneDecisionRepairRegression.RunNativeChecks(chat, bridge, model, Output);
                report["viewPositionSetupApproachEvidence"] = SceneDecisionRepairRegression.LastReport;
                yield return ArdyViewPositionRegression.RunNativeChecks(chat, bridge, Output);
                report["viewPositionNativeEvidence"] = ArdyViewPositionRegression.LastReport;
            }
            else if (ViewPositionEnabled)
            {
                yield return SceneViewPositionIntegrationRegression.RunChecks(chat, bridge, model, Output);
                report["viewPositionEvidence"] = SceneViewPositionIntegrationRegression.LastReport;
            }
            else if (DecisionRepairNativeEnabled)
            {
                yield return SceneDecisionRepairRegression.RunNativeChecks(chat, bridge, model, Output);
                report["decisionRepairNativeEvidence"] = SceneDecisionRepairRegression.LastReport;
            }
            else if (HalfTurnNativeEnabled)
            {
                yield return SceneOrientHalfTurnRegression.RunChecks(chat, bridge, Output);
                report["halfTurnNativeEvidence"] = SceneOrientHalfTurnRegression.LastReport;
            }
            else
            {
                yield return SceneObjectApproachRegression.RunRecordedLowFurnitureChecks(chat, bridge, model, Output, RecordedFurnitureIndex);
                report["recordedFurnitureEvidence"] = SceneObjectApproachRegression.LastReport;
            }
            Finish(null); yield break;
        }

        // Full production path: BeginAutonomyIntentProbe requests a scene capture itself.
        report["beforeProbeGuards"] = GuardSnapshot();
        Call(chat, "BeginAutonomyIntentProbe", "scene-integration-positive");
        yield return Wait(() => model.Probes.Count == 1, "Actual camera → autonomy consumer");
        var probe = model.Probes.Last();
        SceneVisionObservation observed = chat.SceneVisionCapture.LatestObservation;
        Check(observed.HasImage && probe.Image == observed.ImageDataUrl && probe.Input.Contains(observed.CaptureId),
            "Autonomy did not receive actual capture pixels and its matching captureId.");
        var sampleTime = JObject.Parse(chat.SceneTimeContextJson)["samples"].Last;
        Check((string)sampleTime["captureId"] == observed.CaptureId &&
            Vector3.Distance(sampleTime["viewDirectionWorld"].ToObject<Vector3>(), observed.CameraRotation * Vector3.forward) < .0001f,
            "Recent sample time and direction are measured from that actual image, not a proposed orientation.");
        SaveCapture(observed);
        ExportActualRoleRequest(probe);
        report["beforeProceedGuards"] = GuardSnapshot(probe.ProbeCallback);
        probe.Complete(Proceed);
        report["afterProceedGuards"] = GuardSnapshot(probe.ProbeCallback);
        Check(model.Decisions.Count == 1, "Autonomy completion did not reach the actual room decision through StartStreaming.");
        var decision = model.Decisions.Last();
        Check(decision.Image == probe.Image && decision.Input.Contains(observed.CaptureId), "Room decision lost the autonomy capture binding.");
        decision.Complete(true, None, "controlled-none");
        Check(model.Speech.Count == 1 && model.Speech.Last().Image == probe.Image && model.Speech.Last().Context.Contains(observed.CaptureId),
            "Formal speech did not receive the same capture and current observation context.");
        Check(WireImages(probe.Wire).Single() == observed.ImageDataUrl && WireImages(decision.Wire).Single() == observed.ImageDataUrl &&
            WireImages(model.Speech.Last().Wire).Single() == observed.ImageDataUrl, "Actual production serializers changed or omitted the image.");
        Case("actual-camera-autonomy-room-formal-same-capture");

        ResetRound(); int beforeFormal = model.Speech.Count, beforeDecisions = model.Decisions.Count;
        Call(chat, "BeginAutonomyIntentProbe", "scene-integration-late-cancel");
        yield return Wait(() => model.Probes.Count == 2, "Second capture");
        var late = model.Probes.Last();
        Call(chat, "CancelAutonomyIntentProbe"); late.Complete(Proceed);
        Check(model.Speech.Count == beforeFormal && model.Decisions.Count == beforeDecisions,
            "Cancelled late autonomy completion revived a formal or room task.");
        Case("cancelled-autonomy-completion-cannot-dispatch");

        ResetRound(); int callbacks = 0;
        Call(chat, "RequestSceneObservation", "cancel-capture", new Action<SceneVisionObservation>(_ => callbacks++));
        Call(chat, "ResetSceneVision", "integration-cancel", false);
        yield return WaitFrames(4);
        Check(callbacks == 0 && !chat.SceneVisionCapture.LatestObservation.HasImage, "Cancelled pending image entered the Chat callback or current preview.");
        Case("pending-camera-cancel-epoch");

        ResetRound(); int beforeProbes = model.Probes.Count;
        Call(chat, "BeginAutonomyIntentProbe", "scene-integration-expired");
        yield return Wait(() => model.Probes.Count == beforeProbes + 1, "Expiring capture");
        var expired = model.Probes.Last();
        Set(chat, "m_SceneObservationMaxAgeSeconds", .001f); yield return null;
        beforeDecisions = model.Decisions.Count; expired.Complete(Proceed);
        Check(model.Decisions.Count == beforeDecisions && !(bool)Get(chat, "m_FormalResponseInFlight"),
            "An expired autonomy image authorized a new room/formal action.");
        Set(chat, "m_SceneObservationMaxAgeSeconds", 12f);
        Case("expired-autonomy-capture-does-not-authorize-new-task");

        ResetRound(); Set(chat, "m_AgentCurrentRoundIsTick", false);
        Call(chat, "RecordAcceptedMotionUserTurn", "请走到我身边。");
        // Use a fresh real capture and let only the controlled room decision age before it returns.
        SceneVisionObservation roomObservation = null;
        Call(chat, "RequestSceneObservation", "room-expiry", new Action<SceneVisionObservation>(x => roomObservation = x));
        yield return Wait(() => roomObservation != null, "Room expiry capture");
        Call(chat, "StartStreaming", "请走到我身边。", false, null, false, false, false, null, false, roomObservation, true);
        var staleRoom = model.Decisions.Last(); int staleCount = model.Decisions.Count;
        Set(chat, "m_SceneObservationMaxAgeSeconds", .001f); yield return null;
        staleRoom.Complete(true, "{\"intent\":\"approach\",\"origin\":\"user\",\"evidence\":\"请走到我身边。\"}", "controlled-expired-approach");
        Check(bridge.Controller.OperationRevision == operation && !bridge.ReservesBody, "Expired room action was dispatched before a fresh decision.");
        Set(chat, "m_SceneObservationMaxAgeSeconds", 12f);
        yield return Wait(() => model.Decisions.Count == staleCount + 1, "Bounded room refresh");
        var refreshed = model.Decisions.Last();
        Check(refreshed.Image != staleRoom.Image || !refreshed.Input.Contains(roomObservation.CaptureId), "Room refresh reused the obsolete capture identity.");
        refreshed.Complete(true, None, "controlled-none-after-refresh");
        Check(bridge.Controller.OperationRevision == operation, "Fresh none decision unexpectedly generated motion.");
        Case("expired-room-approach-requires-fresh-image-and-new-decision");

        // Cancel through the actual formal-start lifecycle while GPU readback is pending.
        foreach (bool disableComponent in new[] { true, false })
        {
            ResetRound(); beforeDecisions = model.Decisions.Count; beforeFormal = model.Speech.Count;
            Call(chat, "StartStreaming", "请描述此刻看到的场景。", false, null, false, false, false, null, false, null, false);
            Check((bool)Get(chat, "m_FormalResponseInFlight") && !(bool)Get(chat, "m_StreamComplete"),
                "Formal-start fixture did not reach the pending-capture state.");
            if (disableComponent) chat.enabled = false; else chat.StopAgentLoop();
            yield return WaitFrames(4);
            Check(!(bool)Get(chat, "m_FormalResponseInFlight") && (bool)Get(chat, "m_StreamComplete") &&
                !(bool)Get(chat, "m_AgentRoundInFlight"), "Pending formal capture cancellation left the conversation permanently busy.");
            Check(model.Decisions.Count == beforeDecisions && model.Speech.Count == beforeFormal,
                "Late pending formal capture was delivered after disable/StopAgentLoop.");
            chat.enabled = true;
            Case(disableComponent ? "formal-gpu-pending-disable-clears-busy-state" : "formal-gpu-pending-stop-agent-clears-busy-state");
        }

        foreach (bool disableComponent in new[] { true, false })
        {
            ResetRound(); Call(chat, "RecordAcceptedMotionUserTurn", "请走到我身边。");
            SceneVisionObservation initialRoomView = null;
            Call(chat, "RequestSceneObservation", "room-refresh-lifecycle", new Action<SceneVisionObservation>(x => initialRoomView = x));
            yield return Wait(() => initialRoomView != null, "Initial view before cancelled room refresh");
            Call(chat, "StartStreaming", "请走到我身边。", false, null, false, false, false, null, false, initialRoomView, true);
            var pending = model.Decisions.Last();
            Set(chat, "m_SceneObservationMaxAgeSeconds", .001f); yield return null;
            beforeDecisions = model.Decisions.Count; beforeFormal = model.Speech.Count;
            pending.Complete(true, "{\"intent\":\"approach\",\"origin\":\"user\",\"evidence\":\"请走到我身边。\"}", "controlled-refresh-pending");
            Check((int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == (int)Get(chat, "m_FormalResponseGeneration") &&
                (bool)Get(chat, "m_FormalResponseInFlight"), "Room refresh did not reserve the pending formal-capture owner.");
            if (disableComponent) chat.enabled = false; else chat.StopAgentLoop();
            yield return WaitFrames(4);
            Check(!(bool)Get(chat, "m_FormalResponseInFlight") && (bool)Get(chat, "m_StreamComplete") &&
                !(bool)Get(chat, "m_AgentRoundInFlight") && (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1,
                "Cancelled pending room refresh left formal ownership busy.");
            Check(model.Decisions.Count == beforeDecisions && model.Speech.Count == beforeFormal && bridge.Controller.OperationRevision == operation,
                "Late room-refresh capture revived a planner, formal reply or movement after cancellation.");
            Set(chat, "m_SceneObservationMaxAgeSeconds", 12f); chat.enabled = true;
            Case(disableComponent ? "room-refresh-pending-disable-clears-owner" : "room-refresh-pending-stop-agent-clears-owner");
        }

        foreach (bool stopping in new[] { false, true })
        {
            ResetRound(); Call(chat, "RecordAcceptedMotionUserTurn", stopping ? "请停下。" : "请描述此刻的场景。");
            SceneVisionObservation pending = null;
            Call(chat, "RequestSceneObservation", "close-during-room", new Action<SceneVisionObservation>(x => pending = x));
            yield return Wait(() => pending != null, "Capture before closed room reply");
            Call(chat, "StartStreaming", stopping ? "请停下。" : "请描述此刻的场景。", false, null, false, false, false, null, false, pending, true);
            var pendingDecision = model.Decisions.Last();
            Set(chat, "m_EnableScreenVision", false);
            pendingDecision.Complete(true, stopping ? "{\"intent\":\"stop-moving\",\"origin\":\"user\",\"evidence\":\"请停下。\"}" : None, "controlled-close-before-result");
            var closedReply = model.Speech.Last();
            Check(string.IsNullOrEmpty(closedReply.Image) && !WireImages(closedReply.Wire).Any(),
                "A room result arriving after vision was disabled still attached old pixels.");
            Check(closedReply.Context.Contains("本次没有可用图像附件") &&
                (Call(chat, "GetFormalSceneObservation", (int)Get(chat, "m_FormalResponseGeneration")) as SceneVisionObservation)?.HasImage != true,
                "Closed reply has no current observation; historical sample times must not become current pixels.");
            Check(!bridge.Controller.IsBusy && !bridge.ReservesBody, "No-op/stop room completion unexpectedly acquired the body.");
            operation = bridge.Controller.OperationRevision; // A real explicit stop may revoke an otherwise idle controller revision.
            Case(stopping ? "room-stop-after-vision-disabled-has-no-image" : "room-none-after-vision-disabled-has-no-image");
        }

        ResetRound(); Set(chat, "m_AgentEyesOpen", false); beforeProbes = model.Probes.Count;
        Call(chat, "BeginAutonomyIntentProbe", "scene-integration-closed-eyes");
        Check(model.Probes.Count == beforeProbes + 1 && string.IsNullOrEmpty(model.Probes.Last().Image), "Closed eyes still supplied a new or cached image.");
        var textProbe = model.Probes.Last(); textProbe.Complete(Proceed);
        var textDecision = model.Decisions.Last(); textDecision.Complete(true, None, "controlled-text-only");
        var textSpeech = model.Speech.Last();
        Check(!WireImages(textProbe.Wire).Any() && !WireImages(textDecision.Wire).Any() && !WireImages(textSpeech.Wire).Any(),
            "Text-only autonomy/room/formal wire inherited image pixels from a preceding round.");
        Check(string.IsNullOrEmpty(textSpeech.Image) && textSpeech.Context.Contains("本次没有可用图像附件") &&
            (Call(chat, "GetFormalSceneObservation", (int)Get(chat, "m_FormalResponseGeneration")) as SceneVisionObservation)?.HasImage != true,
            "Closed-eye time history retains no executable current observation or image attachment.");
        Case("closed-eyes-text-only-has-no-inherited-image-or-current-capture");
        Check(bridge.Controller.OperationRevision == operation && Vector3.Distance(root, avatar.transform.position) < .0001f && !bridge.Controller.IsBusy,
            "Perception regression unexpectedly obtained body ownership, sent native generation or moved the character.");
        yield return SceneActionIntegrationRegression.RunChecks(chat, bridge, model, Output);
        report["sceneActionChecks"] = SceneActionIntegrationRegression.LastReport;
        ResetRound();
        ArdyRoomObjectApproachRegression.RunRuntimeChecks(bridge, Output);
        yield return RoomSpeechFactScopeRegression.RunChecks(chat, bridge, model, Output);
        report["roomSpeechScopeChecks"] = RoomSpeechFactScopeRegression.LastReport;
        yield return RoomSpeechViewPreparationRegression.RunChecks(chat, bridge, model, Output);
        report["roomSpeechViewPreparationChecks"] = RoomSpeechViewPreparationRegression.LastReport;
        report["nativeRequests"] = 0; report["modelHttpRequests"] = 0;
        if (SceneObjectsEnabled)
        {
            ResetRound();
            yield return SceneObjectIntegrationRegression.RunChecks(chat, bridge, model, Output);
            report["sceneObjectEvidence"] = SceneObjectIntegrationRegression.LastReport;
            yield return SceneObjectReferenceRegression.RunChecks(chat, bridge, Output);
            report["sceneObjectReferenceEvidence"] = SceneObjectReferenceRegression.LastReport;
            if (LiveObjectReopenEnabled)
            {
                yield return SceneObjectReferenceQwenRegression.RunChecks(chat, bridge, model, Output);
                report["sceneObjectReferenceQwenEvidence"] = SceneObjectReferenceQwenRegression.LastReport;
            }
            yield return WorkSceneFeedbackRegression.RunChecks(chat, bridge, model, Output);
            report["workSceneFeedbackChecks"] = WorkSceneFeedbackRegression.LastReport;
            yield return Wait(() => {
                var attention = (NevaSceneAttention)Get(chat, "m_SceneAttention");
                return attention == null || !attention.IsBusy;
            }, "Object interaction fixture releases actual attention before independent GPU panels");
            yield return SceneObjectPerceptionRegression.RunChecks(chat.SceneVisionCapture, Output);
            report["sceneObjectPerceptionEvidence"] = SceneObjectPerceptionRegression.LastReport;
        }
        // Only the inherited strict-baseline cases own the disabled-awareness setting.
        // Native/current-role branches must exercise the newly shipped production behavior.
        Set(chat, "m_EnableSceneObjectAwareness", true);
        if (ObjectMatchingEnabled)
        {
            ResetRound();
            SceneObjectMatchTransportRegression.RunChecks();
            report["sceneObjectMatchTransport"] = SceneObjectMatchTransportRegression.LastReport;
            if (LiveObjectMatchingEnabled) yield return SceneObjectMatchingRegression.RunLiveChecks(chat, bridge, model, Output);
            else yield return SceneObjectMatchingRegression.RunChecks(chat, bridge, model, Output);
            report["sceneObjectMatchingEvidence"] = SceneObjectMatchingRegression.LastReport;
            if (LiveObjectMatchingEnabled) report["modelHttpRequests"] = 1;
            yield return SceneObjectApproachRegression.RunGuardChecks(chat, bridge, Output);
            report["sceneObjectApproachGuards"] = SceneObjectApproachRegression.LastReport;
            yield return SceneObjectCandidateViewRegression.RunChecks(chat.SceneVisionCapture, Output);
            report["sceneObjectCandidates"] = SceneObjectCandidateViewRegression.LastReport;
            Set(chat, "m_EnableSceneObjectTargetMatching", false);
        }
        if (DecisionRepairEnabled)
        {
            ResetRound();
            yield return SceneDecisionRepairRegression.RunChecks(chat, bridge, model, Output);
            report["decisionRepairEvidence"] = SceneDecisionRepairRegression.LastReport;
        }
        if (MotionReadinessEnabled)
        {
            ResetRound();
            yield return RoomMotionReadinessRegression.RunChecks(chat, bridge, Output);
            report["motionReadinessEvidence"] = RoomMotionReadinessRegression.LastReport;
        }
        if (UsesNativeService)
        {
            ResetRound();
            var idle = avatar.GetComponent<Animator>();
            idle.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/AIChatTookit/Animation/Animator Controller.controller");
            Check(idle.runtimeAnimatorController != null, "Actual idle controller required for native return-to-idle validation.");
            idle.applyRootMotion = false; idle.enabled = true; idle.Rebind(); idle.Update(0f);
            yield return WaitFrames(3);
            chat.SceneVisionCapture.SetObserving(true);
            if (ObjectApproachNativeEnabled)
            {
                yield return NevaLocomotionFocusRegression.RunChecks(Output);
                report["locomotionFocusGuards"] = NevaLocomotionFocusRegression.LastReport;
                yield return SceneObjectApproachRegression.RunNativeChecks(chat, bridge, model, Output);
                report["sceneObjectApproachNative"] = SceneObjectApproachRegression.LastReport;
                report.Remove("nativeRequests");
            }
            if (NativeEnabled)
            {
                yield return SceneActionLocomotionRegression.RunChecks(chat, bridge, Path.Combine(Output, "native"));
                report["nativeEvidence"] = "native/native-object-actions.json";
                report.Remove("nativeRequests");
            }
            if (LiveQwenEnabled)
            {
                Set(chat, "m_AutonomyReviewsPerMinute", 6); Set(chat, "m_AutonomyReviewMinimumSpacing", 2f);
                Call(chat, "BeginSceneTimeSession");
                yield return OrientAdditionalLiveView();
                yield return SceneActionQwenRegression.RunChecks(chat, bridge, model, Output);
                report["liveQwenEvidence"] = SceneActionQwenRegression.LastReport;
                report.Remove("modelHttpRequests"); report.Remove("nativeRequests");
            }
            if (ProductionActionEnabled)
            {
                yield return SceneActionProductionApproachRegression.RunChecks(chat, bridge, model, Output);
                report["productionActionEvidence"] = "scene-action-production/report.json";
                report.Remove("nativeRequests");
            }
            if (ActiveViewEnabled)
            {
                yield return SceneActiveViewRegression.RunChecks(chat, bridge, model, Output);
                report["activeViewEvidence"] = SceneActiveViewRegression.LastReport;
                report.Remove("nativeRequests");
            }
        }
        ChatLatencyTransportRegression.RunInteractive();
        report["chatLatencyTransportChecks"] = "passed; production serializer, stage budget, cancellation and exact fact sharing";
        report["sceneReviewQueuePureChecks"] = SceneReviewQueueRegression.RunPureChecks();
        yield return SceneReviewQueueRegression.RunChecks(chat, bridge, Output);
        report["sceneReviewQueueChecks"] = SceneReviewQueueRegression.LastReport;
        report["wireRequests"] = JArray.FromObject(model.Wires.Select(x => new { kind = x.Kind, filename = x.File, images = WireImages(x.Wire).Count() }));
        Finish(null);
    }

    private static IEnumerator OrientAdditionalLiveView()
    {
        float yaw = float.Parse(Argument("-nevaSceneProbeYaw", "0"), System.Globalization.CultureInfo.InvariantCulture);
        if (Mathf.Abs(yaw) < .01f) yield break;
        Check(Mathf.Abs(yaw) <= 60f && !bridge.ReservesBody && !bridge.Controller.IsBusy,
            "Additional view preparation requires bounded native turning from an idle validated station.");
        Vector3 before = avatar.transform.position;
        Vector3 approximateGround = before + Vector3.up * bridge.importedReference.groundY * avatar.transform.lossyScale.y;
        Check(bridge.Navigation.TryProjectGround(approximateGround, out var ground, out string reason), "Initial orientation support: " + reason);
        Vector3 facing = Quaternion.Euler(0f, yaw, 0f) * avatar.transform.forward;
        var orientation = new JObject { ["scope"] = "Explicit fixture setup for a second observation direction using an actual native in-place body turn. This turn is not selected by Qwen, and no desired object or intention is supplied to Qwen afterward.",
            ["requestedYaw"] = yaw, ["initialRoot"] = JObject.Parse(JsonUtility.ToJson(before)) };
        report["additionalObservationSetup"] = orientation;
        bridge.Controller.MoveTo(ground, ground + facing * 2f);
        int operation = bridge.Controller.OperationRevision;
        float deadline = Time.realtimeSinceStartup + 35f;
        while (bridge.Controller.ExecutionState != ArdyRoomExecutionState.Arrived &&
            bridge.Controller.ExecutionState != ArdyRoomExecutionState.Failed &&
            bridge.Controller.ExecutionState != ArdyRoomExecutionState.Blocked &&
            bridge.Controller.ExecutionState != ArdyRoomExecutionState.Cancelled && Time.realtimeSinceStartup < deadline) yield return null;
        Check(bridge.Controller.OperationRevision == operation && bridge.Controller.ExecutionState == ArdyRoomExecutionState.Arrived,
            "Additional observation orientation did not actually complete: " + bridge.Controller.Status);
        deadline = Time.realtimeSinceStartup + 5f;
        while (bridge.Controller.Player != null && bridge.Controller.Player.HasPoseOwnership && Time.realtimeSinceStartup < deadline) yield return null;
        float drift = Vector3.Distance(before, avatar.transform.position);
        Check((bridge.Controller.Player == null || !bridge.Controller.Player.HasPoseOwnership) &&
            avatar.GetComponent<Animator>().enabled && drift < .15f, "Native observation turn did not release to idle at the original station.");
        orientation["actualRootDisplacementMetres"] = drift;
        orientation["actualFacingErrorDegrees"] = Vector3.Angle(avatar.transform.forward, facing);
        orientation["executionState"] = bridge.Controller.ExecutionState.ToString();
        orientation["modelSelectedThisTurn"] = false;
        yield return WaitFrames(3);
    }

    private const string Proceed = "{\"proceed\":true,\"work_status\":\"none\",\"intent\":\"观察刚捕获的房间画面\",\"novelty\":\"新的实际场景图像\",\"wait_seconds\":45}";
    private const string None = "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}";

    private static void ResetRound()
    {
        Call(chat, "CancelAutonomyIntentProbe"); Call(chat, "InvalidateFormalResponse", "scene-vision-fixture-next");
        chat.StopAllCoroutines();
        Call(chat, "ClearScheduledAgentWake");
        foreach (string field in new[] { "m_AudioSource", "m_HumStreamSecondaryAudioSource" })
        {
            var source = (AudioSource)Get(chat, field);
            if (source == null) continue;
            source.playOnAwake = false; source.Stop(); source.clip = null;
        }
        Set(chat, "m_PendingTickCo", null); Set(chat, "m_AgentRoundInFlight", false); Set(chat, "m_FormalResponseInFlight", false);
        Set(chat, "m_AgentCurrentRoundIsTick", false); Set(chat, "m_UserSpeechActiveForAutonomy", false);
        Set(chat, "m_StreamComplete", true); Set(chat, "m_ConsecutiveAITurns", 0);
        Set(chat, "m_AgentRunning", true); Set(chat, "m_AgentEyesOpen", true); Set(chat, "m_EnableScreenVision", true);
    }

    private static IEnumerator Wait(Func<bool> complete, string label)
    {
        float deadline = Time.realtimeSinceStartup + 8f;
        while (!complete()) { if (Time.realtimeSinceStartup > deadline) throw new TimeoutException(label); yield return null; }
    }
    private static IEnumerator WaitFrames(int count) { while (count-- > 0) yield return null; }
    private static void SaveCapture(SceneVisionObservation observation)
    {
        File.WriteAllText(Path.Combine(Output, "observation.json"), observation.MetadataJson + "\n");
        byte[] bytes = Convert.FromBase64String(observation.ImageDataUrl.Substring(observation.ImageDataUrl.IndexOf(',') + 1));
        File.WriteAllBytes(Path.Combine(Output, "capture.jpg"), bytes);
        report["captureId"] = observation.CaptureId; report["imageSha256"] = Digest(bytes); report["imageBytes"] = bytes.Length;
        report["capturePreparationMilliseconds"] = chat.SceneVisionCapture.PreparationMilliseconds;
        report["capturePreparationScope"] = "One-time synchronous self-view mesh preparation; GPU readback is asynchronous but the complete capture pipeline is not wholly asynchronous.";
        Check(bytes.Length > 1024, "Camera produced no substantial JPEG payload.");
    }
    private static JObject GuardSnapshot(Delegate callback = null)
    {
        var result = new JObject { ["enabled"] = chat.enabled, ["active"] = chat.isActiveAndEnabled,
            ["IsAISpeaking"] = chat.IsAISpeaking, ["SceneEyesOpen"] = chat.SceneEyesOpen };
        foreach (string name in new[] { "m_AgentRunning", "m_AgentGracefulShutdownPending", "m_AgentRoundInFlight", "m_FormalResponseInFlight",
            "m_AutonomyProbeGeneration", "m_WorkEpoch", "m_WorkReviewOpen", "m_WorkNoProgress", "m_UserSpeechActiveForAutonomy",
            "m_AutonomyProbeInFlight", "m_AutonomyProbeMustRemainPrivate", "m_ConsecutiveAITurns", "m_MonologueBackstopTurns", "m_MaxConsecutiveAITurns" })
            result[name] = JToken.FromObject(Get(chat, name));
        var voice = typeof(ChatSample).GetProperty("IsVoiceOutputPlaying", Flags);
        if (voice != null) result["IsVoiceOutputPlaying"] = JToken.FromObject(voice.GetValue(chat));
        foreach (string field in new[] { "m_AudioSource", "m_HumStreamSecondaryAudioSource" })
        {
            var source = (AudioSource)Get(chat, field);
            result[field] = source == null ? JValue.CreateNull() : (JToken)new JObject {
                ["name"] = source.name, ["isPlaying"] = source.isPlaying, ["playOnAwake"] = source.playOnAwake,
                ["enabled"] = source.enabled, ["clip"] = source.clip == null ? null : source.clip.name,
                ["echoFilterAttached"] = source.GetComponent<PlaybackEchoReferenceTap>() != null };
        }
        object urge = Get(chat, "m_Urge");
        if (urge != null) result["urge"] = JObject.Parse(JsonUtility.ToJson(urge));
        if (callback?.Target != null)
        {
            var captured = new JObject();
            foreach (var field in callback.Target.GetType().GetFields(Flags))
                if (field.FieldType == typeof(bool) || field.FieldType == typeof(int) || field.FieldType == typeof(string))
                    captured[field.Name] = field.GetValue(callback.Target) == null ? JValue.CreateNull() : JToken.FromObject(field.GetValue(callback.Target));
            result["callbackCaptured"] = captured;
        }
        return result;
    }
    private static void ExportActualRoleRequest(NevaSceneVisionIntegrationModel.Request probe)
    {
        var host = new GameObject("Inactive actual-role vision serializer"); host.SetActive(false);
        try
        {
            var real = host.AddComponent<ChatQW>();
            var config = (JObject)report["actualRoleConfiguration"];
            foreach (var item in config.Properties())
            {
                FieldInfo field = typeof(ChatQW).GetField(item.Name, Flags);
                object value = item.Name == "m_PromptFiles" || item.Name == "m_SkillFiles"
                    ? ((JArray)item.Value).Select(x => AssetDatabase.LoadAssetAtPath<TextAsset>((string)x["path"])).ToArray()
                    : item.Value.ToObject(field.FieldType);
                field.SetValue(real, value);
            }
            real.InitSystemMessage();
            real.SkillCatalogContext = model.SkillCatalogContext; real.SetActiveSkills();
            string json = (string)Call(real, "BuildAutonomyIntentRequestJsonWithImage", probe.Input, probe.Image);
            Check(real.HasPromptFiles && real.m_DataList.Any(x => x.role == "system" && !string.IsNullOrWhiteSpace(x.content)),
                "Real-role export did not load actual prompt assets.");
            Check(WireImages(json).Single() == probe.Image, "Real-role export lost the same actual camera attachment.");
            File.WriteAllText(Path.Combine(Output, "real-role-autonomy-request.json"), json + "\n");
            report["realRoleAutonomyExport"] = new JObject { ["file"] = "real-role-autonomy-request.json", ["sha256"] = Digest(System.Text.Encoding.UTF8.GetBytes(json)),
                ["captureId"] = (string)report["captureId"], ["systemMessages"] = real.m_DataList.Count(x => x.role == "system"),
                ["scope"] = "Production autonomy request serializer, actual main-scene PromptFiles/SkillFiles and generation settings, same captured RGB and actual ChatSample probe text. Empty isolated dialogue history; exported only, not submitted." };
        }
        finally { Object.DestroyImmediate(host); }
    }
    private static IEnumerable<string> WireImages(string json) => JObject.Parse(json).SelectTokens("$..image_url.url").Select(x => (string)x);
    private static void Case(string name) => ((JArray)report["cases"]).Add(name);
    private static void Update()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", 0)) { Finish(new TimeoutException("Scene integration deadline")); return; }
        if (!EditorApplication.isPlaying || routines.Count == 0 || lastFrame == Time.frameCount) return;
        lastFrame = Time.frameCount;
        try
        {
            while (routines.Count > 0)
            {
                IEnumerator current = routines.Peek();
                if (!current.MoveNext()) { if (routines.Count > 0) routines.Pop(); continue; }
                if (current.Current is IEnumerator nested) { routines.Push(nested); continue; }
                break;
            }
        }
        catch (Exception error) { Finish(error); }
    }
    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        routines.Clear(); if (report == null) report = JObject.Parse(SessionState.GetString(Key + "Report", "{}"));
        report["status"] = error == null ? "passed" : "failed"; report["checks"] = checks;
        report["elapsedSeconds"] = Time.realtimeSinceStartup - started;
        if (error != null) { report["error"] = error.ToString(); Debug.LogException(error); }
        File.WriteAllText(Path.Combine(Output, "report.json"), report.ToString() + "\n");
        SessionState.SetBool(Key + "Passed", error == null); SessionState.SetBool(Key + "Finished", true);
        if (chat != null) { chat.StopAllCoroutines(); chat.enabled = false; }
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else Exit();
    }
    private static void Exit() { bool passed = SessionState.GetBool(Key + "Passed", false); SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(passed ? 0 : 1); }
    private static string Argument(string key, string fallback) { string[] args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    public static object Call(object target, string name, params object[] args)
    {
        Type type = target.GetType(); MethodInfo method = null;
        while (type != null && method == null) { method = type.GetMethods(Flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length); type = type.BaseType; }
        if (method == null) throw new MissingMethodException(target.GetType().Name, name + "/" + args.Length);
        try { return method.Invoke(target, args); } catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
    private static string Hash(string path) => Digest(File.ReadAllBytes(path));
    private static string Digest(byte[] data) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
}

/// <summary>Only transport callbacks are controlled; inputs originate in the actual ChatSample chain.</summary>
public sealed class NevaSceneVisionIntegrationModel : LLM
{
    public new sealed class Request
    {
        public string Kind, Input, Context, Image, Wire, File;
        public Action<string> ProbeCallback;
        public Action<bool, string, string> RoomCallback;
        public Action<SpeechText> SpeechCallback;
        public Action<string> SpeechCompleteCallback;
        public bool RecordAssistantHistory;
        public void Complete(string json) => ProbeCallback(json);
        public void Complete(bool ok, string json, string status) => RoomCallback(ok, json, status);
    }
    public readonly List<Request> Probes = new List<Request>(), Decisions = new List<Request>(), Speech = new List<Request>(), Wires = new List<Request>(), Matches = new List<Request>();
    private ChatQW serializer;
    private string output;
    public void Initialize(string path)
    {
        output = path; var host = new GameObject("Dormant production Qwen serializer"); host.SetActive(false);
        serializer = host.AddComponent<ChatQW>(); serializer.m_Backend = ChatQW.BackendType.Local;
        serializer.m_DataList = m_DataList;
    }
    public override bool SupportsVisualDecisionMessages => true;
    public override bool SupportsRoomTaskMessages => true;
    public override bool SupportsSceneObjectMatchMessages => true;
    public override void PostSceneObjectMatchMessage(string input, string imageDataUrl, Action<bool,string,string> callback)
    {
        var request = new Request { Kind="object-match", Input=input, Image=imageDataUrl, RoomCallback=callback };
        request.Wire = (string)NevaSceneVisionIntegrationRegression.Call(serializer, "BuildSceneObjectMatchRequestJson", input, imageDataUrl);
        Matches.Add(request); Save(request);
    }
    public override void PostEphemeralMsg(string prompt, Action<string> callback) => PostEphemeralMsg(prompt, null, callback);
    public override void PostAutonomyIntentMsg(string prompt, string imageDataUrl, Action<string> callback)
    {
        serializer.ActiveSkillContext = ActiveSkillContext;
        var request = new Request { Kind = "autonomy", Input = prompt, Image = imageDataUrl, ProbeCallback = callback };
        request.Wire = (string)NevaSceneVisionIntegrationRegression.Call(serializer, "BuildAutonomyIntentRequestJsonWithImage", prompt, imageDataUrl);
        Probes.Add(request); Save(request);
    }
    public override void PostEphemeralMsg(string prompt, string imageDataUrl, Action<string> callback)
    {
        serializer.ActiveSkillContext = ActiveSkillContext;
        var request = new Request { Kind = "autonomy", Input = prompt, Image = imageDataUrl, ProbeCallback = callback };
        request.Wire = (string)NevaSceneVisionIntegrationRegression.Call(serializer, "BuildEphemeralRequestJsonWithImage", prompt, imageDataUrl, 1, false);
        Probes.Add(request); Save(request);
    }
    public override void PostWorkReviewMsg(string prompt, string imageDataUrl, Action<string> callback) => PostEphemeralMsg(prompt, imageDataUrl, callback);
    public override void PostRoomTaskMessage(string input, bool review, Action<bool, string, string> callback) => PostRoomTaskMessage(input, null, review, callback);
    public override void PostRoomTaskMessage(string input, string imageDataUrl, bool review, Action<bool, string, string> callback)
    {
        var request = new Request { Kind = review ? "room-review" : "room", Input = input, Image = imageDataUrl, RoomCallback = callback };
        request.Wire = (string)NevaSceneVisionIntegrationRegression.Call(serializer, "BuildRoomTaskRequestJsonWithImage", input, imageDataUrl, review);
        Decisions.Add(request); Save(request);
    }
    public override void PostSpeechStream(string message, Action<SpeechText> onSpeech, Action<string> onComplete, string imageDataUrl = null, bool recordAssistantHistory = true)
        => PostSpeechStreamForStage(message, onSpeech, onComplete, imageDataUrl, recordAssistantHistory, FormalResponseStage.General);
    public override void PostSpeechStreamForStage(string message, Action<SpeechText> onSpeech, Action<string> onComplete,
        string imageDataUrl, bool recordAssistantHistory, FormalResponseStage stage)
    {
        m_DataList.Add(new SendData("user", message) { imageDataUrl = imageDataUrl });
        var request = new Request { Kind = "formal", Input = message, Context = RequestContext, Image = imageDataUrl };
        serializer.ActiveSkillContext = ActiveSkillContext;
        serializer.SkillCatalogContext = SkillCatalogContext;
        request.Wire = (string)NevaSceneVisionIntegrationRegression.Call(serializer, "BuildStageRequestJsonForMessages", m_DataList, true, RequestContext, null, stage);
        Speech.Add(request); Save(request);
        // No speech completion is delivered: lifecycle fences are exercised without synthesizing any audio.
    }
    public override void PostSpeechFeedbackStream(string context, string feedback, Action<SpeechText> onSpeech,
        Action<string> onComplete, string imageDataUrl, bool recordAssistantHistory)
    {
        var request = new Request { Kind = "work-feedback", Input = feedback, Context = context, Image = imageDataUrl,
            RecordAssistantHistory = recordAssistantHistory, SpeechCallback = onSpeech };
        request.SpeechCompleteCallback = full => {
            if (recordAssistantHistory) m_DataList.Add(new SendData("assistant", full));
            onComplete(full);
        };
        var wireHistory = NevaSceneVisionIntegrationRegression.Call(serializer, "CreateObservationRequestHistory", m_DataList, imageDataUrl);
        request.Wire = (string)NevaSceneVisionIntegrationRegression.Call(serializer, "BuildRequestJsonForMessagesWithFeedback", wireHistory, true, context, feedback);
        Speech.Add(request); Save(request);
    }
    private void Save(Request request)
    {
        request.File = "wire-" + Wires.Count.ToString("D2") + "-" + request.Kind + ".json";
        File.WriteAllText(Path.Combine(output, request.File), request.Wire + "\n"); Wires.Add(request);
    }
    // Late handles remain usable so production request generations, not the fake, must reject them.
    public override void CancelActiveResponse() { }
    public override void CancelEphemeralMsg() { }
}
