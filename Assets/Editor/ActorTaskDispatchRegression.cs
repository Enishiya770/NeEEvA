using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>Production actor callback/receipt gates and real bounded room acceptance; no model or audio service.</summary>
[InitializeOnLoad]
public static class ActorTaskDispatchRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Key = "NeEEvA.ActorTaskDispatch.";
    private const string Floor = "Actor task disposable floor";
    private static int checks;
    private static JObject report;
    private static ChatSample chat;
    private static FastLoopIntegrationModel model;
    private static ArdyRoomDialogueBridge bridge;
    private static IEnumerator scenario;
    private static int lastFrame;

    // Independent edit-mode checks can be included in the root's existing suite.
    public static int RunChecks()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Use edit mode for the controlled callback suite.");
        checks = 0;
        foreach (string language in new[] { "ja", "zh", "en" })
        using (var f = new EditFixture())
        {
            f.Arm();
            var judge = f.Judge("今からあなたのそばへ行くわ。", language);
            Check(f.Queued == "" && f.Model.RoomCalls == 0, "A pending verdict has no public promise or planner call.");
            judge.Answer(TaskDecision("approach", judge.Draft), "ok");
            Check(f.Model.RoomCalls == 0 && f.Model.FormalCalls == 0 && f.Model.m_DataList.Count == 2,
                "An unavailable controller produces one factual reply, with no planner, description or duplicate history.");
            Check(f.Model.m_DataList.Last().content.Contains("code=\"" + language + "\""), "The receipt keeps the draft's selected language.");
            Check(!(bool)Get(f.Chat, "m_FormalResponseInFlight") && f.Queued.Length > 0,
                "A rejected known task closes the formal wait with a bounded receipt.");
            judge.Answer(TaskDecision("approach", judge.Draft), "duplicate");
            Check(f.Model.m_DataList.Count == 2, "A duplicate verdict cannot publish again.");
        }
        using (var f = new EditFixture())
        {
            f.Arm(); f.AttachRoom(); Set(f.Chat, "m_ExplorationAutonomousSuppressed", true);
            var judge = f.Judge("あなたについていくわ。", "ja");
            judge.Answer(TaskDecision("follow", judge.Draft), "ok");
            Check((string)Get(f.Chat, "m_RoomTaskDispatch") == "not-submitted: autonomous-movement-stopped" &&
                string.IsNullOrEmpty(f.Bridge.ActionId), "An actor proposal cannot bypass the user's movement-stop suppression.");
        }
        using (var f = new EditFixture())
        {
            f.Arm(); f.AttachRoom();
            var judge = f.Judge("あなたについていくわ。", "ja");
            int oldGeneration = (int)Get(f.Chat, "m_FormalResponseGeneration");
            Call(f.Chat, "CancelFastLoopRequest", "regression-new-turn");
            judge.Answer(TaskDecision("follow", judge.Draft), "late");
            Check(string.IsNullOrEmpty(f.Bridge.ActionId) && f.Queued == "" && f.Model.m_DataList.Count == 0 &&
                (int)Get(f.Chat, "m_FormalResponseGeneration") > oldGeneration,
                "A delayed verdict from a cancelled turn cannot submit, publish or commit history.");
        }
        using (var f = new EditFixture())
        {
            f.Arm(); f.AttachRoom();
            var judge = f.Judge("あの灯りを見に行くわ。", "ja");
            judge.Answer(new ActorIntentDecision { Domain = "other_action", Kind = "replan", Action = "none",
                Condition = "none", Evidence = judge.Draft, Task = "other", Target = "other" }, "ok");
            Check(f.Model.RoomCalls == 1 && f.Queued == "" && string.IsNullOrEmpty(f.Bridge.ActionId),
                "A concrete object still goes to its existing planner; the current-user compiler cannot appropriate it.");
        }
        using (var f = new EditFixture())
        {
            f.Arm(); f.AttachRoom();
            var judge = f.Judge("あなたについていくわ。", "ja");
            var tts = (FastLoopIntegrationTts)((ChatSetting)Get(f.Chat, "m_ChatSettings")).m_TextToSpeech;
            const string replacement = "今は少し話していよう。";
            tts.Prepared.Last().OnDispose = () => Call(f.Chat, "CompileActorDraft", f.Model,
                "嗯，我们继续聊。", replacement, "ja", (int)Get(f.Chat, "m_FormalResponseGeneration"), true,
                ((FastLoopRuntime)Get(f.Chat, "m_FastLoop")).OutputRevision);
            judge.Answer(TaskDecision("follow", judge.Draft), "ok");
            Check(string.IsNullOrEmpty(f.Bridge.ActionId) && f.Model.ActorPending.Count == 2 &&
                f.Model.ActorPending.Last().Draft == replacement && f.Queued == "",
                "Disposing old prepared audio may synchronously compile a new draft; the old known task cannot submit or cancel it.");
            f.Model.ActorPending.Last().Answer(new ActorIntentDecision { Kind = "none", Domain = "dialogue", Action = "none", Condition = "none", Evidence = "" }, "ok");
            Check(f.Queued == replacement && f.Model.m_DataList.Count == 2, "Only the reentrant replacement retains publication and history ownership.");
        }
        foreach (bool replace in new[] { false, true })
        using (var f = new EditFixture())
        {
            f.Arm(); f.AttachRoom();
            // Explicitly controlled facts, separate from the actual Play acceptance below.
            f.Bridge.userHead = f.Chat.transform;
            Set(f.Bridge, "followUser", f.Bridge.userHead);
            Set(f.Bridge.Controller, "avatar", f.Bridge.avatar);
            Set(f.Bridge, "followActive", true); Set(f.Bridge, "actionId", "controlled-current-follow");
            Set(f.Bridge, "phase", "follow-waiting");
            var judge = f.Judge("あなたについていくわ。", "ja");
            judge.Answer(TaskDecision("follow", judge.Draft), "ok");
            int round = (int)Get(f.Chat, "m_CurrentSpeechRoundId");
            Check((bool)Call(f.Chat, "ValidateActorTaskSpeech", round), "Current controlled follow facts authorize the bounded receipt.");
            if (replace) Set(f.Bridge, "actionId", "replacement-owner"); else Set(f.Bridge, "phase", "failed");
            Check(!(bool)Call(f.Chat, "ValidateActorTaskSpeech", round) && f.Queued == "",
                "A failed or replaced task revokes the queued receipt before playback.");
        }
        foreach (string change in new[] { "user", "avatar", "controller-avatar", "navigation-root", "disconnect", "output-disabled", "action", "active-task" })
        using (var f = new EditFixture())
        {
            f.Arm(); f.AttachRoom();
            var judge = f.Judge("我会跟着你。", "zh");
            if (change == "user") f.Bridge.userHead = f.Chat.transform;
            if (change == "avatar") f.Bridge.avatar = null;
            if (change == "controller-avatar") Set(f.Bridge.Controller, "avatar", f.Bridge.avatar);
            if (change == "navigation-root") Set(f.Bridge.Navigation, "environmentRoot", f.Chat.transform);
            if (change == "disconnect") Set(f.Bridge, "subscribedChat", null);
            if (change == "output-disabled") Set(f.Chat, "m_RoomMotionOutputEnabled", false);
            if (change == "action") Set(f.Bridge, "actionId", "successor-body-owner");
            if (change == "active-task") { Set(f.Bridge, "followActive", true); Set(f.Bridge, "actionId", "successor-follow-owner"); }
            string physicalOwner = f.Bridge.ActionId;
            judge.Answer(TaskDecision("follow", judge.Draft), "ok");
            Check(f.Bridge.ActionId == physicalOwner && f.Queued == "" && f.Model.RoomCalls == 0 &&
                !(bool)Get(f.Chat, "m_FormalResponseInFlight"),
                "A delayed current-user verdict cannot inherit changed physical binding or leave a hanging formal wait: " + change);
        }
        using (var f = new EditFixture())
        {
            f.Arm(); f.AttachRoom(); var judge = f.Judge("我先走过去，再唱刚才那段。", "zh");
            var compound = TaskDecision("other", judge.Draft); compound.Target = "other"; compound.Coverage = "compound";
            judge.Answer(compound, "ok");
            Check(f.Model.RoomCalls == 1 && string.IsNullOrEmpty(f.Bridge.ActionId) && f.Queued == "",
                "Compound current-user meaning cannot be shortened into the atomic controller path, regardless of a model task enum.");
        }
        Directory.CreateDirectory("Logs/fast-loop");
        File.WriteAllText("Logs/fast-loop/actor-task-dispatch.json", new JObject {
            ["passed"] = true, ["checks"] = checks,
            ["scope"] = "Production delayed fake-provider callback, history/language/cancellation and room fallback gates; controlled bindings; no physical movement, HTTP, microphone or audio."
        }.ToString());
        return checks;
    }

    public static void RunBatch()
    {
        try { RunChecks(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(Unwrap(error)); EditorApplication.Exit(1); }
    }

    static ActorTaskDispatchRegression()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) InitializePlay();
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) ExitPlay();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) &&
                !EditorApplication.isPlaying) ExitPlay();
        };
    }

    public static void RunPlayBatch()
    {
        if (!Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Use a closed isolated batch Editor without -quit.");
        foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
            if (!string.IsNullOrEmpty(setup.path) && SceneManager.GetSceneByPath(setup.path).isDirty)
                throw new InvalidOperationException("Refusing to replace a dirty saved scene.");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.name = Floor;
        floor.transform.position = new Vector3(0, -.1f, 0); floor.transform.localScale = new Vector3(10, .2f, 10);
        ArdyRoomGeometryPreparation.Prepare(floor.transform, false);
        SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
        SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 90f);
        EditorApplication.isPlaying = true;
    }

    private static void InitializePlay()
    {
        try
        {
            checks = 0; lastFrame = -1;
            report = new JObject { ["status"] = "running", ["remoteModelRequests"] = 0,
                ["scope"] = "Real imported avatar, temporary flat-floor navigation, production FastLoop/ActorIntent callback and actual room/controller acceptance. Approach is already at its valid destination; follow waits nearby. No generated walking, role semantic accuracy, speaker output or remote service claim." };
            const string asset = "Assets/Model/NeEEvA.vrm";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
            if (prefab == null || prefab.GetComponent<Vrm10Instance>() == null)
                throw new InvalidOperationException("The isolated fixture requires the imported VRM prefab asset: " + asset);
            // Read import rest data from the asset before making any scene copy.
            var reference = ArdyLocomotionAvatarReference.FromImportedPrefab(prefab.GetComponent<Vrm10Instance>(), asset);
            var actor = UnityEngine.Object.Instantiate(prefab);
            var avatar = actor.GetComponent<Vrm10Instance>();
            foreach (var component in actor.GetComponentsInChildren<Behaviour>(true)) component.enabled = false;
            actor.transform.SetPositionAndRotation(new Vector3(0, -reference.groundY, 0), Quaternion.identity);
            var host = new GameObject("Actor task isolated chat"); host.SetActive(false);
            chat = host.AddComponent<ChatSample>(); model = host.AddComponent<FastLoopIntegrationModel>();
            var button = new GameObject("Unused submit", typeof(RectTransform), typeof(Button)); button.transform.SetParent(host.transform);
            var text = new GameObject("Unused text", typeof(RectTransform), typeof(Text)); text.transform.SetParent(host.transform);
            Set(chat, "m_CommitMsgBtn", button.GetComponent<Button>()); Set(chat, "m_TextBack", text.GetComponent<Text>());
            Set(chat, "m_ChatSettings", new ChatSetting { m_ChatModel = model });
            Set(chat, "m_Animator", avatar.GetComponent<Animator>());
            foreach (string name in new[] { "m_AgentRunning", "m_AgentRoundInFlight", "m_EnableNeuralHumSVC", "m_AutoStartHumSVC",
                "m_EnableSingingVoiceSynthesis", "m_AutoStartSVS", "m_EnableScreenVision", "m_ContinuousSceneVision",
                "m_EnableSubtitleTranslation", "m_PersistSubtitleSettings", "m_EnableSceneObjectAwareness", "m_UserSpeechActiveForAutonomy" }) Set(chat, name, false);
            Set(chat, "m_SceneTimePaused", true);
            bridge = host.AddComponent<ArdyRoomDialogueBridge>(); bridge.connectOnStart = false;
            bridge.chat = chat; bridge.avatar = avatar; bridge.importedReference = reference;
            bridge.environmentRoot = GameObject.Find(Floor).transform;
            bridge.userHead = new GameObject("Explicit nearby user").transform; bridge.userHead.position = new Vector3(0, 1.6f, 1f);
            // A mistaken test submission cannot reach a user's remote service.
            bridge.serviceUrl = "http://127.0.0.1:1";
            host.SetActive(true); bridge.Connect();
            Check(bridge.IsConnected && chat.RoomMotionOutputEnabled, "Real room bridge and navigation are connected.");
            scenario = PlayScenario();
        }
        catch (Exception error) { FinishPlay(Unwrap(error)); }
    }

    private static IEnumerator PlayScenario()
    {
        yield return null;
        Arm(chat, "嗯，我们继续聊。", 1001);
        int before = bridge.Controller.OperationRevision;
        var judge = BeginJudge(chat, model, "今からあなたのそばへ行くわ。", "ja", 1001);
        Check(bridge.Controller.OperationRevision == before && Queued(chat) == "", "Private draft alone cannot submit a room operation.");
        yield return null; // The real callback is deliberately delivered on a later frame.
        judge.Answer(TaskDecision("approach", judge.Draft), "ok");
        Check(bridge.Controller.OperationRevision > before && bridge.Phase == "arrived" &&
            bridge.ReadCurrentTargetEvidence().currentlyNearAndFacingUser,
            "The actual controller checks the already-near approach and records arrival without generated motion.");
        Check(string.IsNullOrEmpty(bridge.Controller.LastRequestJson) && model.RoomCalls == 0 && model.FormalCalls == 0,
            "Known approach avoids all planner/description/review and motion-generation requests.");
        Check(((ActorPlanRuntime)Get(chat, "m_ActorPlan")).State == "completed" &&
            ((ActorPlanRuntime)Get(chat, "m_ActorPlan")).RemainingGoal == "" &&
            ((ActorPlanRuntime)Get(chat, "m_ActorPlan")).Results.Count == 1,
            "Program-bound atomic approach closes at its real result with one step and no fabricated remainder or next Room call.");
        report["approachActionId"] = bridge.ActionId; report["approachOperation"] = bridge.Controller.OperationRevision;
        Check(Queued(chat).Contains("近く") && model.m_DataList.Last().content.Contains("code=\"ja\""), "Actual current position receipt retains Japanese.");

        // Crucially keep the same user and target revision: a new follow is not
        // a duplicate of the preceding approach at this target.
        Arm(chat, "嗯。", 1002);
        judge = BeginJudge(chat, model, "これからずっとあなたについていくわ。", "ja", 1002);
        yield return null;
        judge.Answer(TaskDecision("follow", judge.Draft), "ok");
        string follow = bridge.ActiveTaskId;
        Check(bridge.IsFollowing && bridge.Phase == "follow-waiting" && !string.IsNullOrEmpty(follow) &&
            (string)Get(chat, "m_RoomTaskDispatch") == "follow-submitted", "Approach completion at the same target does not suppress a newly adopted follow.");
        Check((bool)Call(chat, "ValidateActorTaskSpeech", 3002), "Accepted waiting follow has a current speech lease.");
        report["followActionId"] = follow; report["followPhase"] = bridge.Phase;
        int history = model.m_DataList.Count, operation = bridge.Controller.OperationRevision;
        judge.Answer(TaskDecision("follow", judge.Draft), "duplicate");
        Check(model.m_DataList.Count == history && bridge.Controller.OperationRevision == operation && bridge.ActiveTaskId == follow,
            "Duplicate asynchronous callbacks do not restart or republish the actual follow.");
        ((FastLoopRuntime)Get(chat, "m_FastLoop")).BeginTurn(); Call(chat, "CancelSceneAction", "new-user-turn");
        Check(bridge.IsFollowing && bridge.ActiveTaskId == follow, "Ordinary dialogue preserves the accepted follow task.");
        Check(!(bool)Call(chat, "ValidateActorTaskSpeech", 3002), "A new output revision revokes the old queued acknowledgement.");

        Arm(chat, "继续。", 1003);
        judge = BeginJudge(chat, model, "あなたについていくわ。", "en", 1003);
        yield return null;
        judge.Answer(TaskDecision("follow", judge.Draft), "ok");
        Check((string)Get(chat, "m_RoomTaskDispatch") == "follow-already-active" && bridge.ActiveTaskId == follow,
            "An already-running follow is acknowledged without starting a second task.");
        Check((bool)Call(chat, "ValidateActorTaskSpeech", 3003), "An idempotent receipt has a current task lease.");
        bridge.StopForTask("fixture-explicit-stop");
        Check(!bridge.IsFollowing && !(bool)Call(chat, "ValidateActorTaskSpeech", 3003) && Queued(chat) == "",
            "Actual stop revokes an unspoken accepted-task acknowledgement.");
        report["sameTargetApproachThenFollow"] = true; report["newDialoguePreservedFollow"] = true;
        report["stopRevokedQueuedReceipt"] = true;

        // A late verdict must not create another real task after cancellation.
        Arm(chat, "稍等。", 1004);
        judge = BeginJudge(chat, model, "あなたについていくわ。", "ja", 1004);
        Call(chat, "CancelFastLoopRequest", "fixture-explicit-cancel");
        yield return null;
        judge.Answer(TaskDecision("follow", judge.Draft), "late");
        Check(!bridge.IsFollowing && bridge.ActionId == "fixture-explicit-stop" && Queued(chat) == "",
            "A cancelled pending actor decision cannot revive the real stopped task.");
        Check(model.RoomCalls == 0 && model.FormalCalls == 0 && string.IsNullOrEmpty(bridge.Controller.LastRequestJson),
            "The complete bounded acceptance fixture needed no room planner, formal model or ARDY generation.");
        // Control is independent of the role draft, and binds to a captured
        // task rather than whichever task happens to exist when a reply arrives.
        model.RoomSupported = true;
        var evidence = bridge.ReadCurrentTargetEvidence(true);
        Check(bridge.FollowForTarget("fixture-follow-stop", evidence.currentTargetRevision, out _), "A real nearby follow starts for explicit control.");
        Arm(chat, "非常棒，可以停下了。", 1005);
        Check((bool)Call(chat, "TryDispatchFastTaskLoop", model, (string)Get(chat, "m_LastUserMsg"), null, 1005, true, null), "Stop decision has a production turn owner.");
        var stop = model.Pending.Last();
        yield return null;
        stop.Answer(new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Language = "zh", Speech = "我会继续跟着你。",
            TaskControl = "stop_movement", ControlTargetId = "fixture-follow-stop", ControlEvidence = "可以停下了" }, "ok");
        Check(bridge.IsFollowing && Queued(chat) == "" && model.ControlPending.Last().CurrentUser == "非常棒，可以停下了。" &&
            !model.ControlPending.Last().Context.Contains("我会继续跟着你"), "A role stop proposal is private until an independent input-only verdict arrives.");
        yield return null;
        model.ControlPending.Last().Answer("stop_follow", "ok");
        Check(!bridge.IsFollowing && string.IsNullOrEmpty(bridge.ActiveTaskId) && bridge.ActionId.StartsWith("fast-stop:"),
            "Independent current-task classification normalizes the role writer's wrong stop subtype and reaches the real controller despite contradictory prose.");
        Check(Queued(chat).Contains("已经停止") && !Queued(chat).Contains("继续跟着") &&
            (bool)Call(chat, "ValidateFastTaskControlSpeech", 3005), "Only the actual stop result supplies the current acknowledgement.");
        string stopId = bridge.ActionId; int stoppedOperation = bridge.Controller.OperationRevision;
        stop.Answer(new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Language = "zh", Speech = "", TaskControl = "stop_follow",
            ControlTargetId = "fixture-follow-stop", ControlEvidence = "可以停下了" }, "duplicate");
        Check(bridge.ActionId == stopId && bridge.Controller.OperationRevision == stoppedOperation, "Duplicate stop result cannot cancel again.");
        evidence = bridge.ReadCurrentTargetEvidence(true);
        Check(bridge.FollowForTarget("fixture-follow-successor", evidence.currentTargetRevision, out _), "A successor task starts under its own owner.");
        Check(!(bool)Call(chat, "ValidateFastTaskControlSpeech", 3005) && Queued(chat) == "",
            "A successor task revokes the unplayed stopped acknowledgement.");
        Arm(chat, "不用跟着我了。", 1006);
        Call(chat, "TryDispatchFastTaskLoop", model, (string)Get(chat, "m_LastUserMsg"), null, 1006, true, null);
        var staleStop = model.Pending.Last();
        bridge.StopForTask("fixture-replace-stop");
        evidence = bridge.ReadCurrentTargetEvidence(true);
        Check(bridge.FollowForTarget("fixture-follow-new-owner", evidence.currentTargetRevision, out _), "A different follow replaces the captured task while the model waits.");
        yield return null;
        int reroutes = model.RoomCalls + model.FormalCalls;
        staleStop.Answer(new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Language = "zh", Speech = "我已经停止了。",
            TaskControl = "stop_follow", ControlTargetId = "fixture-follow-successor", ControlEvidence = "不用跟着我了" }, "late");
        Check(bridge.ActiveTaskId == "fixture-follow-new-owner" && bridge.IsFollowing && Queued(chat) == "" && model.RoomCalls + model.FormalCalls == reroutes &&
            model.Pending.Last().Context.Contains("taskControlRepair") && model.Pending.Last().CurrentUser == (string)Get(chat, "m_LastUserMsg"),
            "Late stop cannot cancel a successor or publish an unsupported success claim; one same-input repair replaces a forced spatial plan.");
        Arm(chat, "不用跟着我了，接下来唱刚才的录音。", 1007);
        var recording = chat.gameObject.AddComponent<FastLoopRecordingRouteModel>();
        ((ChatSetting)Get(chat, "m_ChatSettings")).m_ChatModel = recording;
        Call(chat, "TryDispatchFastTaskLoop", recording, (string)Get(chat, "m_LastUserMsg"), null, 1007, true, null);
        recording.Fast(new FastLoopDecision { Route = "recording_singing", Candidate = "none", Evidence = "", Language = "zh", Speech = "", TaskControl = "stop_follow",
            ControlTargetId = "fixture-follow-new-owner", ControlEvidence = "不用跟着我了" }, "ok");
        Check(bridge.IsFollowing && recording.Stages.Count == 0, "Mixed stop plus singing waits for its independently reviewed control before continuation.");
        recording.Control("stop_follow", "ok");
        Check(!bridge.IsFollowing && recording.RoomCalls == 0 && recording.RoleCalls == 0 &&
            recording.Stages.SequenceEqual(new[] { FormalResponseStage.RecordingSingingDecision }) &&
            recording.StageContext.UserText.Contains("接下来唱刚才的录音"),
            "Actual stop plus recording-singing keeps the remaining goal and skips unrelated planners.");
        report["typedStopAndSinging"] = true;
        ((ChatSetting)Get(chat, "m_ChatSettings")).m_ChatModel = model;
        evidence = bridge.ReadCurrentTargetEvidence(true);
        Check(bridge.FollowForTarget("fixture-keep-following", evidence.currentTargetRevision, out _), "Follow starts for the negative-control case.");
        Arm(chat, "这次不用回唱了，保持跟着我就好。", 1008);
        Set(chat, "m_EnableActorIntentCompiler", false);
        Call(chat, "TryDispatchFastTaskLoop", model, (string)Get(chat, "m_LastUserMsg"), null, 1008, true, null);
        model.Pending.Last().Answer(new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Language = "zh", Speech = "好，我会继续跟着你。",
            TaskControl = "stop_follow", ControlTargetId = "fixture-keep-following", ControlEvidence = "这次不用回唱了" }, "incorrect-role-stop");
        var rejectedControl = model.ControlPending.Last();
        reroutes = model.RoomCalls + model.FormalCalls;
        rejectedControl.Answer("none", "ok");
        rejectedControl.Answer("stop_follow", "ok");
        Check(bridge.IsFollowing && bridge.ActiveTaskId == "fixture-keep-following" && Queued(chat) == "" && model.RoomCalls + model.FormalCalls == reroutes + 1 && CurrentInputWasRouted(),
            "A rejected or duplicate opposite control result never cancels follow or replaces the user's remaining dialogue.");
        Arm(chat, "可以停下了。", 1009);
        Call(chat, "TryDispatchFastTaskLoop", model, (string)Get(chat, "m_LastUserMsg"), null, 1009, true, null);
        model.Pending.Last().Answer(new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Language = "zh", Speech = "停下了。",
            TaskControl = "stop_follow", ControlTargetId = "fixture-keep-following", ControlEvidence = "可以停下了" }, "ok");
        var cancelledControl = model.ControlPending.Last();
        Call(chat, "CancelFastLoopRequest", "cancel-during-control-review");
        cancelledControl.Answer("stop_follow", "ok");
        Check(bridge.IsFollowing && bridge.ActiveTaskId == "fixture-keep-following" && Queued(chat) == "",
            "Cancelling while the independent control model waits revokes both cancellation authority and speech.");
        Arm(chat, "不用跟着我了。", 1010);
        Call(chat, "TryDispatchFastTaskLoop", model, (string)Get(chat, "m_LastUserMsg"), null, 1010, true, null);
        model.Pending.Last().Answer(new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Language = "zh", Speech = "已经停下了。",
            TaskControl = "stop_follow", ControlTargetId = "fixture-keep-following", ControlEvidence = "不用跟着我了" }, "ok");
        reroutes = model.RoomCalls + model.FormalCalls;
        model.ControlPending.Last().Answer("stop_follow", "controlled-http-error");
        Check(bridge.IsFollowing && Queued(chat) == "" && model.RoomCalls + model.FormalCalls == reroutes && model.Pending.Last().Context.Contains("taskControlRepair"),
            "A failed control check does not stop movement or publish an unsupported completion.");
        Arm(chat, "不用跟着我了。", 1011);
        Call(chat, "TryDispatchFastTaskLoop", model, (string)Get(chat, "m_LastUserMsg"), null, 1011, true, null);
        model.Pending.Last().Answer(new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Language = "zh", Speech = "已经停下了。",
            TaskControl = "stop_follow", ControlTargetId = "fixture-keep-following", ControlEvidence = "不用跟着我了" }, "ok");
        var replacedControl = model.ControlPending.Last();
        bridge.StopForTask("fixture-replaced-during-review");
        evidence = bridge.ReadCurrentTargetEvidence(true);
        Check(bridge.FollowForTarget("fixture-review-successor", evidence.currentTargetRevision, out _), "Task replacement occurs during the independent semantic review.");
        yield return null;
        reroutes = model.RoomCalls + model.FormalCalls;
        replacedControl.Answer("stop_follow", "ok");
        Check(bridge.ActiveTaskId == "fixture-review-successor" && bridge.IsFollowing && Queued(chat) == "" && model.RoomCalls + model.FormalCalls == reroutes && model.Pending.Last().Context.Contains("taskControlRepair"),
            "An independently approved late stop still cannot cancel a replacement controller task.");
        report["independentControlGate"] = true;
        var repairScenario = ControlRepairScenario(bridge);
        while (repairScenario.MoveNext()) yield return repairScenario.Current;
        bridge.StopMovement("fixture-pose-start", "Prepare controlled owned trajectory.");
        var ownedClip = SyntheticOwnedClip();
        var ownedPlayer = bridge.Controller.Player;
        ownedPlayer.Play(ownedClip);
        Set(bridge.Controller, "ownedClip", ownedClip);
        Set(bridge.Controller, "ownedPlayerRevision", ownedPlayer.PlaybackRevision);
        Set(bridge, "actionId", "fixture-owned-body"); Set(bridge, "activeAction", true);
        Set(bridge, "phase", "moving"); Set(bridge, "hasControllerOperation", true);
        Set(bridge, "operationRevision", bridge.Controller.OperationRevision);
        Arm(chat, "现在停下。", 1012);
        Call(chat, "TryDispatchFastTaskLoop", model, (string)Get(chat, "m_LastUserMsg"), null, 1012, true, null);
        model.Pending.Last().Answer(new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Language = "zh", Speech = "停下了。",
            TaskControl = "stop_movement", ControlTargetId = "fixture-owned-body", ControlEvidence = "现在停下" }, "ok");
        model.ControlPending.Last().Answer("stop_movement", "ok");
        Check(bridge.Controller.IsMoving && bridge.Controller.IsReturningOwnedPose && !bridge.Controller.IsBusy &&
            string.IsNullOrEmpty(bridge.ActiveTaskId), "Actual smooth stop retains pose return ownership after trajectory cancellation.");
        Check(Queued(chat).Contains("已经停止") && (bool)Call(chat, "ValidateFastTaskControlSpeech", 3012),
            "An actual owned pose return authorizes the same successful stop at publication and playback lease checks.");
        report["actualOwnedPoseStop"] = true;
        ownedPlayer.Stop();
        bridge.Controller.Cancel(); // End this synthetic clip before the independent preparation fixture.

        bool roomChatWasEnabled = chat.enabled;
        chat.enabled = false;
        try { report["roomPreparationChecks"] = ArdyRoomSpeechPreparationRegression.RunChecks(chat, bridge); }
        finally { chat.enabled = roomChatWasEnabled; }

        var visionChecks = FastReplyVisionRecoveryRegression.RunChecks(chat, bridge);
        while (visionChecks.MoveNext()) yield return visionChecks.Current;
        report["fastReplyVisionRecovery"] = FastReplyVisionRecoveryRegression.LastReport;

        report["remoteModelRequests"] = 0; report["motionGenerationRequests"] = 0;
    }

    private static IEnumerator ControlRepairScenario(ArdyRoomDialogueBridge bridge)
    {
        const string feedback = "你做得很好，你一直在跟着我。";
        string runningTask = bridge.ActiveTaskId;
        int roomBefore = model.RoomCalls + model.FormalCalls;
        Arm(chat, feedback, 1101);
        Call(chat, "TryDispatchFastTaskLoop", model, feedback, null, 1101, true, null);
        model.Pending.Last().Answer(new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Language = "zh",
            Speech = "我已经停止跟随了。", TaskControl = "stop_follow", ControlTargetId = runningTask,
            ControlEvidence = "你一直在跟着我" }, "ok");
        var gate = model.ControlPending.Last();
        int pendingBefore = model.Pending.Count;
        gate.Answer("none", "ok");
        gate.Answer("stop_follow", "ok");
        var repair = model.Pending.Last();
        Check(model.Pending.Count == pendingBefore + 1 && repair.CurrentUser == feedback &&
            repair.Context.Contains("taskControlRepair") && repair.Context.Contains("independent-control-review-declined") &&
            !repair.Context.Contains("我已经停止跟随了"), "Denied stop gets exactly one repair with complete input and facts, never the misleading draft.");
        Check(bridge.IsFollowing && bridge.ActiveTaskId == runningTask && Queued(chat) == "" &&
            model.RoomCalls + model.FormalCalls == roomBefore, "Praise repair preserves actual follow and avoids forced Room requests.");
        int actorBefore = model.ActorPending.Count;
        var reply = new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Language = "zh", Speech = "谢谢你的肯定。" };
        repair.Answer(reply, "ok"); repair.Answer(reply, "ok");
        Check(model.ActorPending.Count == actorBefore + 1 && Queued(chat) == "", "Fresh repair is private until ordinary Actor review and duplicates cannot publish.");
        model.ActorPending.Last().Answer(new ActorIntentDecision { Domain = "dialogue", Kind = "none", Action = "none", Condition = "none", Evidence = "" }, "ok");
        Check(Queued(chat).Contains("谢谢你的肯定") && !Queued(chat).Contains("停止") &&
            bridge.IsFollowing && model.RoomCalls + model.FormalCalls == roomBefore, "Non-action feedback can publish without replanning or stopping the running task.");
        yield return null;

        Arm(chat, feedback, 1102);
        Call(chat, "TryDispatchFastTaskLoop", model, feedback, null, 1102, true, null);
        var wrong = new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Language = "zh", Speech = "已经停止了。",
            TaskControl = "stop_follow", ControlTargetId = runningTask, ControlEvidence = "你一直在跟着我" };
        model.Pending.Last().Answer(wrong, "ok"); model.ControlPending.Last().Answer("none", "ok");
        repair = model.Pending.Last(); pendingBefore = model.Pending.Count;
        repair.Answer(wrong, "ok"); repair.Answer(wrong, "ok");
        Check(model.Pending.Count == pendingBefore && bridge.IsFollowing && !Queued(chat).Contains("已经停止") &&
            !Queued(chat).Contains("动作还没有确认完成") && model.RoomCalls + model.FormalCalls == roomBefore,
            "Repeated invalid control repair terminates neutrally, without a second stop or spatial retry.");

        Arm(chat, feedback, 1103);
        Call(chat, "TryDispatchFastTaskLoop", model, feedback, null, 1103, true, null);
        model.Pending.Last().Answer(wrong, "ok"); model.ControlPending.Last().Answer("none", "ok");
        repair = model.Pending.Last(); actorBefore = model.ActorPending.Count;
        Call(chat, "CancelFastLoopRequest", "fixture-control-repair-cancel");
        repair.Answer(reply, "ok");
        Check(Queued(chat) == "" && model.ActorPending.Count == actorBefore && bridge.IsFollowing,
            "Cancellation revokes the pending repair, including late speech and Actor dispatch.");

        Arm(chat, feedback, 1105);
        Call(chat, "TryDispatchFastTaskLoop", model, feedback, null, 1105, true, null);
        model.Pending.Last().Answer(wrong, "ok"); model.ControlPending.Last().Answer("none", "ok");
        repair = model.Pending.Last(); pendingBefore = model.Pending.Count;
        repair.Answer(null, "controlled-transport-failure");
        Check(model.Pending.Count == pendingBefore && bridge.IsFollowing && Queued(chat).Length > 0 &&
            !Queued(chat).Contains("已经停止") && model.RoomCalls + model.FormalCalls == roomBefore,
            "Failed repair transport ends with an interpretation response while the same follow remains running.");

        Arm(chat, feedback, 1106);
        Call(chat, "TryDispatchFastTaskLoop", model, feedback, null, 1106, true, null);
        model.Pending.Last().Answer(wrong, "ok"); model.ControlPending.Last().Answer("none", "ok");
        repair = model.Pending.Last(); actorBefore = model.ActorPending.Count;
        Arm(chat, "先听我说。", 1107);
        Call(chat, "TryDispatchFastTaskLoop", model, "先听我说。", null, 1107, true, null);
        repair.Answer(reply, "ok");
        Check(model.ActorPending.Count == actorBefore && Queued(chat) == "" && bridge.IsFollowing,
            "A successor user turn revokes the previous repair without changing the running physical task.");

        const string mixed = "你一直在跟着我。接下来请看看左边的小灯。";
        Arm(chat, mixed, 1104);
        Call(chat, "TryDispatchFastTaskLoop", model, mixed, null, 1104, true, null);
        model.Pending.Last().Answer(wrong, "ok"); model.ControlPending.Last().Answer("none", "ok");
        repair = model.Pending.Last();
        repair.Answer(new FastLoopDecision { Route = "legacy", Candidate = "none", Evidence = "", Speech = "", Language = "zh" }, "ok");
        Check(model.RoomCalls + model.FormalCalls == roomBefore + 1 && CurrentInputWasRouted() && bridge.IsFollowing,
            "A repaired genuine new goal retains the full mixed input and can enter the ordinary planner without stopping follow.");
        report["declinedControlRepair"] = true;
        var singingChecks = AgentWorkLoopRegression.RunSingingReviewUnavailableChecks();
        while (singingChecks.MoveNext()) yield return singingChecks.Current;
        report["singingReviewUnavailableChecks"] = true;
        var queueChecks = AgentWorkLoopRegression.RunSingingQueueReadinessChecks();
        while (queueChecks.MoveNext()) yield return queueChecks.Current;
        report["singingQueueReadinessChecks"] = true;
    }

    private static void Tick()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        try
        {
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", 0)) throw new TimeoutException("Actor task Play fixture timed out.");
            if (!EditorApplication.isPlaying || scenario == null || lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount;
            if (!scenario.MoveNext()) FinishPlay(null);
        }
        catch (Exception error) { FinishPlay(Unwrap(error)); }
    }

    private static void FinishPlay(Exception error)
    {
        scenario = null;
        if (bridge != null) bridge.StopMovement("cancelled", "isolated-fixture-ended");
        report = report ?? new JObject(); report["status"] = error == null ? "passed" : "failed";
        report["checks"] = checks; report["error"] = error?.ToString();
        Directory.CreateDirectory("Logs/fast-loop"); File.WriteAllText("Logs/fast-loop/actor-task-play.json", report.ToString());
        SessionState.SetBool(Key + "Failed", error != null); SessionState.SetBool(Key + "Finished", true);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else ExitPlay();
    }
    private static void ExitPlay()
    { SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(SessionState.GetBool(Key + "Failed", true) ? 1 : 0); }

    private sealed class EditFixture : IDisposable
    {
        private readonly object fixture;
        public readonly ChatSample Chat;
        public readonly FastLoopIntegrationModel Model;
        public ArdyRoomDialogueBridge Bridge => Chat.GetComponent<ArdyRoomDialogueBridge>();
        public string Queued => ActorTaskDispatchRegression.Queued(Chat);
        public EditFixture()
        {
            var type = typeof(FastLoopIntegrationRegression).GetNestedType("Fixture", BindingFlags.NonPublic);
            fixture = Activator.CreateInstance(type, true); Chat = (ChatSample)Get(fixture, "Chat"); Model = (FastLoopIntegrationModel)Get(fixture, "Model");
        }
        public void Arm() => Call(fixture, "Arm", "嗯，我们继续聊。", false);
        public void AttachRoom()
        {
            Call(fixture, "AttachControlledRoom");
            Call(fixture, "AttachSpeechProvider");
        }
        public FastLoopIntegrationModel.PendingActorIntent Judge(string text, string language) =>
            BeginJudge(Chat, Model, text, language, (int)Get(Chat, "m_FormalResponseGeneration"));
        public void Dispose() => ((IDisposable)fixture).Dispose();
    }

    private static void Arm(ChatSample target, string prompt, int generation)
    {
        Set(target, "m_FormalResponseGeneration", generation); Set(target, "m_WorkEpoch", generation + 1000);
        Set(target, "m_CurrentSpeechRoundId", generation + 2000); Set(target, "m_LastUserMsg", prompt);
        Set(target, "m_FormalTransportDispatchGeneration", -1);
        foreach (string name in new[] { "m_EnableFastTaskLoop", "m_EnableActorIntentCompiler", "m_UseStreaming", "m_IsVoiceMode", "m_RoundInnerCheckDone", "m_RoundRepeatCheckDone" }) Set(target, name, true);
        foreach (string name in new[] { "m_UserSpeechActiveForAutonomy", "m_SceneVisionApplicationPaused", "m_FormalResponseInFlight", "m_StreamComplete", "m_RoundContinue", "m_RoundSilent", "m_RoundIsInner", "m_RoundSilencedForRepeat", "m_RoundRepeatHold", "m_FirstChunkFlushed", "m_HoldSpeechForSongMemoryResult", "m_HoldSpeechForHumBackResult" }) Set(target, name, false);
        Set(target, "m_SentenceBuffer", new SpeechTextBuffer()); Set(target, "m_AssistantHeardText", new StringBuilder());
        Set(target, "m_ChatHistory", new List<string> { prompt });
        foreach (string name in new[] { "m_PendingChunks", "m_PendingClips" }) Set(target, name, Activator.CreateInstance(Get(target, name).GetType()));
    }
    private static FastLoopIntegrationModel.PendingActorIntent BeginJudge(ChatSample target, FastLoopIntegrationModel provider, string text, string language, int generation)
    {
        Set(target, "m_EnableActorIntentCompiler", true);
        Check((bool)Call(target, "TryDispatchFastTaskLoop", provider, (string)Get(target, "m_LastUserMsg"), null, generation, true, null), "Production fast entry owns the turn.");
        provider.Pending.Last().Answer(new FastLoopDecision { Version = FastLoopProtocol.CurrentVersion, Route = "reply", Candidate = "none", Evidence = "", Speech = text, Language = language }, "ok");
        return provider.ActorPending.Last();
    }
    private static ActorIntentDecision TaskDecision(string task, string evidence) => new ActorIntentDecision {
        Domain = "other_action", Kind = "replan", Action = "none", Condition = "none", Evidence = evidence, Task = task, Target = "current_user", TargetIsProgramBound = false
    };
    private static bool CurrentInputWasRouted()
    {
        string input = (string)Get(chat, "m_LastUserMsg");
        return model.LastFormalUser == input || !string.IsNullOrEmpty(model.LastRoomInput) &&
            (string)JObject.Parse(model.LastRoomInput)["currentUserText"] == input;
    }
    private static ArdyLocomotionClip SyntheticOwnedClip()
    {
        const int count = 12;
        var clip = new ArdyLocomotionClip { schema = 1, id = "stop-receipt-fixture", sourceRootHeight = 1,
            frames = new ArdyLocomotionFrame[count], targets = new ArdyLocomotionTarget[count],
            source = new ArdyLocomotionSource { coordinateSystem = "unity-lh-y-up-z-forward" },
            rotationClip = new ArdyMotionClip { schema = 1, id = "stop-receipt-fixture", text = "Synthetic owned trajectory", fps = 20,
                jointNames = (string[])ArdyLiveProtocol.JointNames.Clone(), jointParents = (int[])ArdyLiveProtocol.JointParents.Clone(),
                restGlobalRotations = Enumerable.Repeat(Quaternion.identity, 27).ToArray(), frames = new ArdyMotionFrame[count],
                source = new ArdyMotionSource { coordinateSystem = "unity-lh-y-up-z-forward", rootTranslationApplied = false } } };
        for (int i = 0; i < count; i++) {
            clip.frames[i] = new ArdyLocomotionFrame { rootPosition = new Vector3(0, 1, i * .005f) };
            clip.targets[i] = new ArdyLocomotionTarget { rootPosition = new Vector3(0, 0, i * .005f) };
            clip.rotationClip.frames[i] = new ArdyMotionFrame { globalRotations = Enumerable.Repeat(Quaternion.identity, 27).ToArray() };
        }
        clip.Validate(); return clip;
    }
    private static string Queued(ChatSample target) => string.Concat(((IEnumerable)Get(target, "m_PendingChunks")).Cast<object>().Select(x => (string)Get(x, "Text"))) + Get(target, "m_SentenceBuffer");
    private static object Get(object value, string field) => value.GetType().GetField(field, Flags).GetValue(value);
    private static void Set(object value, string field, object data) => value.GetType().GetField(field, Flags).SetValue(value, data);
    private static object Call(object value, string name, params object[] args) => value.GetType().GetMethods(Flags)
        .Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args);
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
    private static Exception Unwrap(Exception error) => error is TargetInvocationException && error.InnerException != null ? Unwrap(error.InnerException) : error;
}
