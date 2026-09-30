using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NeEEvA.Perception;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>Real imported head/eye rig and production coroutine in an unsaved isolated Play scene.</summary>
[InitializeOnLoad]
public static class FastLoopHeadPlayRegression
{
    private const string Key = "NeEEvA.FastLoopHeadPlay.";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [Serializable] private sealed class Report
    {
        public string status = "running", error, avatarAsset;
        public string scope = "Actual imported VRM head/eyes, ChatSample.TryDispatchFastTaskLoop reply through actor-intent compilation to physical head execution, scoped attention release and deterministic waiting/count-event runtime in an unsaved isolated Play scene. Provider callbacks and condition signals are supplied fixtures; fake backend call counts are distinct from network requests. No model semantic accuracy, network, microphone, ASR, TTS, song or whole-body motion test.";
        public int checks, backendRequests, fakeFastDecisionRequests, fakeActorIntentRequests, networkRequests;
        public bool startingSurvivedDialogue, holdingSurvivedDialogue, renewedBeyondLease, sameTaskCorrection,
            staleReleaseRejected, smoothReturnObserved, noPublicSpeech;
        public bool actorDraftStartedWithoutUserCommand, actorDraftCorrection, actorOldOwnerRejected,
            waitingDidNotMoveBeforeSignal, waitingFiredOnce, waitingTriggeredActualMotion,
            headLimitRejected, headLimitNeverHeld, headLimitNoAdditionalRaise, sharedReplyRecoveredAction,
            intentPublishedBeforeSettle, acceptedSpeechRevokedWhileMotionContinues, conditionReceiptWaitedForSettle;
        public float raisedHeadDegrees, correctedHeadDegrees, heldSeconds, maximumRootTranslation,
            maximumRootRotationDegrees, finalHeadReturnDegrees;
        public float actorRightDegrees, actorRightSettleSeconds, actorCorrectionDegrees, waitingTriggeredDegrees,
            headLimitStartPitch, headLimitMaximumObservedPitch;
    }
    private static Report report;
    private static ChatSample chat;
    private static FastLoopIntegrationModel model;
    private static FastLoopRuntime runtime;
    private static NevaEyeTracking eyes;
    private static NevaSceneAttention attention;
    private static NevaSceneVisionCapture vision;
    private static FastLoopTask firstTask, correctedTask;
    private static FastLoopTask actorRightTask, actorLeftTask, headLimitTask;
    private static ActorDraft actorDraft, waitingDraft;
    private static ActorWaitingTaskRuntime waiting;
    private static ActorWaitingTask waitingTask;
    private static Vector3 rootPosition;
    private static Quaternion rootRotation, neutralHeadWorld, raisedHeadWorld;
    private static Quaternion actorRightWorld, waitingHeadWorld;
    private static int phase, lastFrame;
    private static float phaseAt, returnFromDegrees, actorRightStartedAt, originalMaximumUpDegrees;
    private static bool ordinaryTurnApplied;

    static FastLoopHeadPlayRegression()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) InitializeFixture();
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) Exit();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) &&
                !EditorApplication.isPlaying) Exit();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Use batch edit mode; this test creates its own unsaved Play scene.");
        SessionState.SetString(Key + "Output", Path.GetFullPath("Logs/fast-loop"));
        try
        {
            foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
                if (!string.IsNullOrEmpty(setup.path) && SceneManager.GetSceneByPath(setup.path).isDirty)
                    throw new InvalidOperationException("Refusing to replace a dirty saved scene.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 150f);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Finish(error); }
    }

    private static async void InitializeFixture()
    {
        try
        {
            report = new Report(); phase = 0; lastFrame = -1; ordinaryTurnApplied = false;
            const string assetPath = "Assets/Model/NeEEvA.vrm";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            GameObject actor;
            if (prefab != null) actor = UnityEngine.Object.Instantiate(prefab);
            else
            {
                string source = Path.GetFullPath(assetPath);
                if (!File.Exists(source)) throw new FileNotFoundException("The actual source avatar is required.", source);
                var imported = await Vrm10.LoadPathAsync(source,
                    controlRigGenerationOption: ControlRigGenerationOption.None, showMeshes: true);
                if (imported == null) throw new InvalidOperationException("VRM importer returned no avatar.");
                actor = imported.gameObject;
            }
            report.avatarAsset = assetPath;
            actor.name = "FastLoop isolated actual VRM";
            actor.transform.SetPositionAndRotation(new Vector3(1.5f, .2f, -1), Quaternion.Euler(0, 31, 0));
            foreach (var component in actor.GetComponentsInChildren<Behaviour>(true)) component.enabled = false;
            var vrm = actor.GetComponentInChildren<Vrm10Instance>(true);
            Require(vrm != null, "Imported avatar contains the actual VRM rig.");
            vrm.Runtime.SpringBone.Dispose();
            eyes = vrm.GetComponent<NevaEyeTracking>() ?? vrm.gameObject.AddComponent<NevaEyeTracking>();
            eyes.Configure(vrm); eyes.enabled = true;
            vision = vrm.GetComponent<NevaSceneVisionCapture>() ?? vrm.gameObject.AddComponent<NevaSceneVisionCapture>();
            vision.Configure(vrm, eyes); vision.SetObserving(false); vision.SetContinuousObservation(false); vision.enabled = true;
            attention = vrm.GetComponent<NevaSceneAttention>() ?? vrm.gameObject.AddComponent<NevaSceneAttention>();
            attention.Configure(eyes); attention.enabled = true;
            var gaze = new GameObject("FastLoop unchanged normal eye target").transform;
            gaze.position = eyes.EyeOrigin + eyes.HeadFrameRotation * Vector3.forward * 3f;
            eyes.gazeTarget = gaze;

            var host = new GameObject("FastLoop production ChatSample without external services");
            host.SetActive(false);
            chat = host.AddComponent<ChatSample>(); model = host.AddComponent<FastLoopIntegrationModel>();
            var buttonObject = new GameObject("Unused fixture submit", typeof(RectTransform), typeof(Button));
            buttonObject.transform.SetParent(host.transform, false);
            var textObject = new GameObject("Unused fixture subtitle", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            textObject.transform.SetParent(host.transform, false);
            Set("m_CommitMsgBtn", buttonObject.GetComponent<Button>()); Set("m_TextBack", textObject.GetComponent<Text>());
            Set("m_ChatSettings", new ChatSetting { m_ChatModel = model });
            Set("m_SceneVisionAvatar", vrm); Set("m_SceneVisionCapture", vision); Set("m_SceneAttention", attention);
            Set("m_Animator", vrm.GetComponent<Animator>());
            foreach (string field in new[] { "m_EnableFastTaskLoop", "m_EnableSceneActions", "m_EnableScreenVision",
                "m_MotionOutputEnabled", "m_RoomMotionOutputEnabled", "m_AgentEyesOpen", "m_SceneTimePaused" }) Set(field, true);
            foreach (string field in new[] { "m_AgentRunning", "m_AgentRoundInFlight", "m_EnableNeuralHumSVC", "m_AutoStartHumSVC",
                "m_EnableSingingVoiceSynthesis", "m_AutoStartSVS", "m_ContinuousSceneVision", "m_EnableSubtitleTranslation",
                "m_PersistSubtitleSettings", "m_IsVoiceMode", "m_EnableSceneObjectAwareness", "m_SceneVisionApplicationPaused",
                "m_UserSpeechActiveForAutonomy", "m_SceneActionActive" }) Set(field, false);
            Set("m_FormalResponseGeneration", 1000); Set("m_CurrentSpeechRoundId", 2000);
            Set("m_FastLoop", new FastLoopRuntime());
            host.SetActive(true);
            runtime = (FastLoopRuntime)Get("m_FastLoop");
            await System.Threading.Tasks.Task.Yield();
            await System.Threading.Tasks.Task.Yield();
            Require(chat.isActiveAndEnabled && eyes.IsInitialized && !vrm.enabled && chat.SceneEyesOpen,
                "Actual ChatSample owns a calibrated available rig without a competing full VRM writer.");
            Require(!vision.IsObserving && model.Pending.Count == 0, "Fixture starts no observation/model requests.");
            rootPosition = vrm.transform.position; rootRotation = vrm.transform.rotation;
            neutralHeadWorld = eyes.Head.rotation;
            firstTask = Propose("head.raise", "抬头");
            StartProductionTask(firstTask);
            runtime.BeginTurn(); Call("CancelSceneAction", "new-user-turn");
            report.startingSurvivedDialogue = runtime.HeadTask.Id == firstTask.Id &&
                runtime.HeadTask.Revision == firstTask.Revision && runtime.HeadTask.State == "starting";
            Require(report.startingSurvivedDialogue, "A real in-flight head movement survives dialogue cancellation.");
            phaseAt = Time.realtimeSinceStartup;
        }
        catch (Exception error) { Finish(Unwrap(error)); }
    }

    private static FastLoopTask Propose(string candidate, string evidence)
    {
        var previous = runtime.HeadTask;
        var decision = new FastLoopDecision { Version = FastLoopProtocol.CurrentVersion, Route = "head", Candidate = candidate,
            Evidence = evidence, Speech = "", Language = "zh" };
        Require(runtime.TryApply(decision, evidence, previous?.Id, previous?.Revision ?? 0,
            out var task, out string reason), "Production task proposal accepted: " + reason);
        return task;
    }

    private static void StartProductionTask(FastLoopTask task)
    {
        // Physical supervision is independent of dialogue generation. A stale
        // speech generation deliberately prevents public completion/TTS in this test.
        var routine = (IEnumerator)Call("RunFastHeadTask", task.Id, task.Revision, task.Candidate,
            model, "controlled head proposal", "zh", 999, false, runtime.OutputRevision);
        chat.StartCoroutine(routine);
    }

    private static void StartActorTask(ActorIntentDecision decision, ActorDraft draft)
    {
        const string unrelatedUser = "我们继续聊刚才的话题。";
        Set("m_LastUserMsg", unrelatedUser);
        Call("BeginActorHeadTask", decision, draft, model, unrelatedUser, "zh", 999, false,
            runtime.OutputRevision, "");
    }

    private static ActorIntentDecision ActorAct(string action, string evidence) => new ActorIntentDecision {
        Domain = "head", Kind = "act", Action = action, Condition = "none", Evidence = evidence, MotionScope = "head_preset"
    };

    private static void Tick()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        try
        {
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", 0))
                throw new TimeoutException("FastLoop actual head Play test timed out.");
            if (!EditorApplication.isPlaying || chat == null || firstTask == null || lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount;
            float elapsed = Time.realtimeSinceStartup - phaseAt;
            if (elapsed > 12f) throw new TimeoutException("Head phase " + phase + ": " + runtime.HeadTask.State + "/" + attention.State + "/" + attention.Status);
            TrackRoot();
            if (phase == 0 && runtime.HeadTask.State == "holding" && attention.IsSettled)
            {
                report.raisedHeadDegrees = Quaternion.Angle(neutralHeadWorld, eyes.Head.rotation);
                Require(report.raisedHeadDegrees > 6f && attention.OwnerId == Owner(firstTask),
                    "Production coroutine moves the actual head and confirms the matching physical owner.");
                raisedHeadWorld = eyes.Head.rotation;
                phase = 1; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 1)
            {
                Require(runtime.HeadTask.State == "holding" && attention.IsSettled && attention.OwnerId == Owner(firstTask),
                    "Independent production supervision retains the actual settled head.");
                if (elapsed > .8f && !ordinaryTurnApplied)
                {
                    ordinaryTurnApplied = true; runtime.BeginTurn();
                    Call("ResetSceneVision", "new-user-turn", false);
                    report.holdingSurvivedDialogue = runtime.HeadTask.Id == firstTask.Id &&
                        runtime.HeadTask.Revision == firstTask.Revision && attention.IsSettled;
                    Require(report.holdingSurvivedDialogue, "Actual dialogue reset preserves the held task and pose.");
                }
                if (elapsed < 3.2f) return;
                report.heldSeconds = elapsed; report.renewedBeyondLease = attention.IsSettled && attention.IsPersistent;
                Require(report.renewedBeyondLease && Quaternion.Angle(raisedHeadWorld, eyes.Head.rotation) < 1f,
                    "Coroutine renewals preserve the actual pose beyond the two-second lease.");
                correctedTask = Propose("head.left", "向左一点");
                report.sameTaskCorrection = correctedTask.Id == firstTask.Id && correctedTask.Revision > firstTask.Revision;
                Require(report.sameTaskCorrection, "A directional correction changes revision within the same logical task.");
                StartProductionTask(correctedTask);
                report.staleReleaseRejected = !attention.Release(Owner(firstTask), "late-old-coroutine-release") &&
                    attention.OwnerId == Owner(correctedTask);
                Require(report.staleReleaseRejected, "Late old-revision release cannot revoke the successor's actual owner.");
                phase = 2; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 2 && runtime.HeadTask.State == "holding" && attention.IsSettled)
            {
                report.correctedHeadDegrees = Quaternion.Angle(raisedHeadWorld, eyes.Head.rotation);
                Require(report.correctedHeadDegrees > 8f && attention.OwnerId == Owner(correctedTask),
                    "Corrected production task changes the actual head direction and reaches holding.");
                Require(!runtime.Confirm(firstTask.Id, firstTask.Revision), "Old task completion cannot revive the previous revision.");
                phase = 3; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 3 && elapsed > .5f)
            {
                Require(runtime.HeadTask.State == "holding" && attention.OwnerId == Owner(correctedTask) && attention.IsSettled,
                    "Old coroutine finally cannot release the corrected held pose on later frames.");
                returnFromDegrees = Quaternion.Angle(neutralHeadWorld, eyes.Head.rotation);
                Call("CancelFastHeadTask", "explicit-head-stop");
                Require(runtime.HeadTask.State == "cancelled" && attention.IsReturning,
                    "Production explicit cancellation stops the task and starts a scoped smooth return.");
                phase = 4; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 4)
            {
                float remaining = Quaternion.Angle(neutralHeadWorld, eyes.Head.rotation);
                if (remaining > .2f && remaining < returnFromDegrees - .2f) report.smoothReturnObserved = true;
                if (attention.IsReturning) return;
                report.finalHeadReturnDegrees = remaining;
                Require(report.smoothReturnObserved && remaining < .2f && attention.State == "idle" &&
                    runtime.HeadTask.State == "cancelled", "Head physically returns without task resurrection.");
                const string unrelatedUser = "我们继续聊刚才的话题。";
                const string replyDraft = "了解，这就把视线移向右边。";
                var beforeDraft = runtime.HeadTask;
                int generation = (int)Get("m_FormalResponseGeneration");
                Set("m_LastUserMsg", unrelatedUser);
                Set("m_EnableActorIntentCompiler", true);
                Require((bool)Call("TryDispatchFastTaskLoop", model, unrelatedUser, null, generation, false, null) &&
                    model.Pending.Count == 1 && model.ActorPending.Count == 0,
                    "The real fast-loop entry sends exactly one fixture provider request before judging a draft.");
                model.Pending[0].Answer(new FastLoopDecision {
                    Version = FastLoopProtocol.CurrentVersion, Route = "reply", Candidate = "none", Evidence = "", Speech = replyDraft, Language = "zh"
                }, "ok");
                Require(model.ActorPending.Count == 1 && model.ActorPending[0].Draft == replyDraft &&
                    model.ActorPending[0].CurrentUser == unrelatedUser && runtime.HeadTask.Id == beforeDraft.Id &&
                    runtime.HeadTask.Revision == beforeDraft.Revision && runtime.HeadTask.State == "cancelled" &&
                    NoQueuedSpeech(),
                    "A reply-route draft reaches the production actor judge without starting a task or publishing speech first.");
                actorRightStartedAt = Time.realtimeSinceStartup;
                model.ActorPending[0].Answer(ActorAct("head.right", "把视线移向右边"), "ok");
                actorRightTask = runtime.HeadTask;
                actorDraft = new ActorDraft { Id = actorRightTask.SourceId, Revision = actorRightTask.SourceRevision, Text = replyDraft };
                report.actorDraftStartedWithoutUserCommand = actorRightTask.State == "starting" &&
                    actorRightTask.Source == "actor_draft" && !string.IsNullOrEmpty(actorDraft.Id) && actorDraft.Revision == 1;
                Require(report.actorDraftStartedWithoutUserCommand,
                    "A reply-route actor judgement reaches the production physical adapter without a matching user action command.");
                report.intentPublishedBeforeSettle = !attention.IsSettled && !NoQueuedSpeech() &&
                    (bool)Call("ValidateFastHeadSpeech", (int)Get("m_CurrentSpeechRoundId"));
                Require(report.intentPublishedBeforeSettle,
                    "An actually accepted intention can queue speech while the physical controller is still moving.");
                // Revoke only this response's publication authority immediately;
                // its independent physical head task must still reach holding.
                Call("CancelFastLoopRequest", "user-started-speaking");
                report.acceptedSpeechRevokedWhileMotionContinues = NoQueuedSpeech() &&
                    runtime.HeadTask.State == "starting" && attention.OwnerId == Owner(actorRightTask);
                Require(report.acceptedSpeechRevokedWhileMotionContinues,
                    "Barge-in revokes accepted pending speech without cancelling the independent physical movement.");
                Set("m_FormalResponseGeneration", generation + 1);
                phase = 5; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 5 && runtime.HeadTask.State == "holding" && attention.IsSettled)
            {
                report.actorRightSettleSeconds = Time.realtimeSinceStartup - actorRightStartedAt;
                report.actorRightDegrees = Quaternion.Angle(neutralHeadWorld, eyes.Head.rotation);
                Require(report.actorRightDegrees > 8f && attention.OwnerId == Owner(actorRightTask),
                    "Role-authored right turn changes actual head rotation and holds the matching physical owner.");
                report.sharedReplyRecoveredAction = runtime.HeadTask.Source == "actor_draft" &&
                    runtime.HeadTask.SourceId == actorDraft.Id && model.Pending.Count == 1 && model.ActorPending.Count == 1;
                Require(report.sharedReplyRecoveredAction && NoQueuedSpeech(),
                    "The complete reply-to-judge-to-physical chain holds despite revoked speech generation and publishes no audio.");
                actorRightWorld = eyes.Head.rotation;
                actorDraft = new ActorDraft { Id = actorDraft.Id, Revision = 2, Text = "我再往左边转一点。" };
                StartActorTask(ActorAct("head.left", actorDraft.Text), actorDraft);
                actorLeftTask = runtime.HeadTask;
                report.actorDraftCorrection = actorLeftTask.Id == actorRightTask.Id && actorLeftTask.Revision > actorRightTask.Revision &&
                    actorLeftTask.Source == "actor_draft" && actorLeftTask.SourceId == actorDraft.Id && actorLeftTask.SourceRevision == 2;
                Require(report.actorDraftCorrection, "Actor correction preserves task identity and records its new draft revision.");
                report.actorOldOwnerRejected = !attention.Release(Owner(actorRightTask), "late-actor-draft-release") &&
                    attention.OwnerId == Owner(actorLeftTask);
                Require(report.actorOldOwnerRejected, "Old actor draft's physical owner cannot release the corrected head task.");
                phase = 6; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 6 && runtime.HeadTask.State == "holding" && attention.IsSettled)
            {
                report.actorCorrectionDegrees = Quaternion.Angle(actorRightWorld, eyes.Head.rotation);
                Require(report.actorCorrectionDegrees > 8f && attention.OwnerId == Owner(actorLeftTask),
                    "Actor correction reaches an actual different head pose, not just a new state label.");
                phase = 7; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 7 && elapsed > .5f)
            {
                Require(runtime.HeadTask.State == "holding" && attention.OwnerId == Owner(actorLeftTask),
                    "Actor correction survives late previous-coroutine cleanup.");
                Call("CancelFastHeadTask", "actor-fixture-finished");
                phase = 8; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 8 && !attention.IsReturning)
            {
                Require(runtime.HeadTask.State == "cancelled" && attention.State == "idle", "Actor task cancellation releases the actual head.");
                waiting = (ActorWaitingTaskRuntime)Get("m_ActorWaiting");
                waitingDraft = new ActorDraft { Id = "head-play-future-draft", Revision = 1, Text = "等你数到三，我再抬头。" };
                var decision = new ActorIntentDecision { Domain = "head", Kind = "wait", Action = "head.raise", Condition = "user_count_three", Evidence = waitingDraft.Text, MotionScope = "head_preset" };
                Require(waiting.Register(decision, waitingDraft, 100, out waitingTask, out string reason),
                    "Actual waiting runtime registers the role's explicit future intention: " + reason);
                Require(!waiting.Observe(waitingTask.Id, waitingTask.Revision, 100, "sequence_complete", out _) &&
                    !waiting.Observe(waitingTask.Id, waitingTask.Revision, 99, "sequence_complete", out _),
                    "Historical and same-input counting cannot retroactively trigger the new future task.");
                Require(!waiting.Observe(waitingTask.Id, waitingTask.Revision, 101, "one", out _) &&
                    !waiting.Observe(waitingTask.Id, waitingTask.Revision, 102, "two", out _),
                    "First and second new counting events only advance waiting progress.");
                waitingHeadWorld = eyes.Head.rotation;
                phase = 9; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 9 && elapsed > .8f)
            {
                report.waitingDidNotMoveBeforeSignal = waiting.Pending.State == "waiting" && waiting.Pending.Progress == 2 &&
                    runtime.HeadTask.State == "cancelled" && attention.State == "idle" && Quaternion.Angle(waitingHeadWorld, eyes.Head.rotation) < .1f;
                Require(report.waitingDidNotMoveBeforeSignal, "Pending future action produces no physical head movement before its condition completes.");
                Set("m_ActorInputSequence", 102L);
                int countGeneration = (int)Get("m_FormalResponseGeneration") + 1;
                Set("m_FormalResponseGeneration", countGeneration);
                Set("m_CurrentSpeechRoundId", (int)Get("m_CurrentSpeechRoundId") + 1);
                Require((bool)Call("TryDispatchFastTaskLoop", model, "三。", null, countGeneration, false, null),
                    "Counting input enters the production condition observer.");
                Require(model.SignalPending.Count == 1, "One new count input asks for one condition signal.");
                model.SignalPending[0].Answer("three", "ok");
                var triggered = runtime.HeadTask;
                Require(waiting.Pending.State == "triggered" && triggered.State == "starting" && NoQueuedSpeech(),
                    "A triggered action starts physically but its completed-hold receipt is withheld until settled.");
                model.SignalPending[0].Answer("three", "duplicate");
                report.waitingFiredOnce = !waiting.Observe(waitingTask.Id, waitingTask.Revision, 103, "three", out _) &&
                    !waiting.Observe(waitingTask.Id, waitingTask.Revision, 104, "sequence_complete", out _) &&
                    runtime.HeadTask.Id == triggered.Id && runtime.HeadTask.Revision == triggered.Revision;
                Require(report.waitingFiredOnce, "Duplicate or later condition notifications cannot repeat the future action.");
                phase = 10; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 10 && runtime.HeadTask.State == "holding" && attention.IsSettled)
            {
                report.waitingTriggeredDegrees = Quaternion.Angle(waitingHeadWorld, eyes.Head.rotation);
                report.waitingTriggeredActualMotion = report.waitingTriggeredDegrees > 6f &&
                    runtime.HeadTask.Source == "actor_draft" && runtime.HeadTask.SourceId == waitingDraft.Id &&
                    runtime.HeadTask.SourceRevision == waitingDraft.Revision;
                Require(report.waitingTriggeredActualMotion,
                    "Condition completion reaches the actual production actor adapter and physical head, with the original future-draft provenance.");
                report.conditionReceiptWaitedForSettle = !NoQueuedSpeech() &&
                    (bool)Call("ValidateFastHeadSpeech", (int)Get("m_CurrentSpeechRoundId"));
                Require(report.conditionReceiptWaitedForSettle,
                    "A true completed-hold receipt is queued only after the triggered task actually settles.");
                Call("CancelFastLoopRequest", "user-started-speaking");
                Set("m_FormalResponseGeneration", (int)Get("m_FormalResponseGeneration") + 1);
                originalMaximumUpDegrees = attention.maximumUpDegrees;
                report.headLimitStartPitch = attention.CurrentHeadAngles.y;
                report.headLimitMaximumObservedPitch = report.headLimitStartPitch;
                Require(report.headLimitStartPitch > 5f,
                    "The actual raised pose provides a valid upper-bound fixture above the controller's minimum limit.");
                attention.maximumUpDegrees = report.headLimitStartPitch;
                var limitedDraft = new ActorDraft {
                    Id = "head-play-upper-limit-draft", Revision = 1, Text = "我再抬高一点。" };
                StartActorTask(ActorAct("head.raise", limitedDraft.Text), limitedDraft);
                headLimitTask = runtime.HeadTask;
                report.headLimitRejected = headLimitTask.State == "failed" && headLimitTask.Reason == "head-limit-reached";
                Require(report.headLimitRejected,
                    "A production actor raise at the actual pitch limit fails with head-limit-reached before claiming completion.");
                phase = 11; phaseAt = Time.realtimeSinceStartup;
            }
            else if (phase == 11)
            {
                report.headLimitMaximumObservedPitch = Mathf.Max(report.headLimitMaximumObservedPitch, attention.CurrentHeadAngles.y);
                Require(runtime.HeadTask.Id == headLimitTask.Id && runtime.HeadTask.Revision == headLimitTask.Revision &&
                    runtime.HeadTask.State == "failed" && runtime.HeadTask.Reason == "head-limit-reached" &&
                    attention.OwnerId != Owner(headLimitTask),
                    "The rejected upper-limit revision never enters holding or acquires the actual head owner.");
                if (elapsed < .5f || attention.State != "idle") return;
                report.headLimitNeverHeld = true;
                report.headLimitNoAdditionalRaise = report.headLimitMaximumObservedPitch <= report.headLimitStartPitch + .15f;
                Require(report.headLimitNoAdditionalRaise,
                    "A rejected raise produces no additional physical pitch while the superseded owner's scoped cleanup returns the head.");
                attention.maximumUpDegrees = originalMaximumUpDegrees;
                report.fakeFastDecisionRequests = model.Pending.Count;
                report.fakeActorIntentRequests = model.ActorPending.Count;
                report.backendRequests = report.fakeFastDecisionRequests + report.fakeActorIntentRequests + model.SignalPending.Count +
                    model.FirstCalls + model.RoleCalls + model.FormalCalls;
                // FastLoopIntegrationModel is an in-memory callback fixture and
                // has no transport; these three provider calls cannot issue HTTP.
                report.networkRequests = 0;
                report.noPublicSpeech = NoQueuedSpeech() && model.m_DataList.Count == 0;
                Require(report.fakeFastDecisionRequests == 1 && report.fakeActorIntentRequests == 1 &&
                    model.SignalPending.Count == 1 && report.backendRequests == 3 && report.noPublicSpeech,
                    "Physical test used one fake fast, actor and condition request; all test speech was revoked before audio consumption, with no legacy request or history claim.");
                Require(report.maximumRootTranslation < .00001f && report.maximumRootRotationDegrees < .01f,
                    "User and actor head tasks never translate or rotate the avatar root.");
                Finish(null);
            }
        }
        catch (Exception error) { Finish(Unwrap(error)); }
    }

    private static void TrackRoot()
    {
        report.maximumRootTranslation = Mathf.Max(report.maximumRootTranslation, Vector3.Distance(rootPosition, eyes.Target.transform.position));
        report.maximumRootRotationDegrees = Mathf.Max(report.maximumRootRotationDegrees, Quaternion.Angle(rootRotation, eyes.Target.transform.rotation));
    }
    private static string Owner(FastLoopTask task) => task.Id + ":" + task.Revision;
    private static bool NoQueuedSpeech() => ((ICollection)Get("m_PendingChunks")).Count == 0 &&
        ((ICollection)Get("m_PendingClips")).Count == 0 && ((SpeechTextBuffer)Get("m_SentenceBuffer")).Length == 0;
    private static void Require(bool value, string message) { report.checks++; if (!value) throw new InvalidOperationException(message); }
    private static object Get(string name) => typeof(ChatSample).GetField(name, Flags).GetValue(chat);
    private static void Set(string name, object value) => typeof(ChatSample).GetField(name, Flags).SetValue(chat, value);
    private static object Call(string name, params object[] args)
    {
        var method = typeof(ChatSample).GetMethod(name, Flags);
        if (method == null) throw new MissingMethodException(typeof(ChatSample).Name, name);
        var parameters = method.GetParameters();
        if (args.Length < parameters.Length)
        {
            var supplied = args; args = new object[parameters.Length];
            Array.Copy(supplied, args, supplied.Length);
            for (int i = supplied.Length; i < args.Length; i++)
            {
                if (!parameters[i].IsOptional) throw new TargetParameterCountException(name + " requires " + parameters[i].Name);
                args[i] = parameters[i].DefaultValue;
            }
        }
        return method.Invoke(chat, args);
    }
    private static Exception Unwrap(Exception error) => error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        if (report == null) report = new Report();
        report.status = error == null ? "passed" : "failed"; report.error = error?.ToString();
        string directory = SessionState.GetString(Key + "Output", Path.GetFullPath("Logs/fast-loop"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "head-play.json"), JsonUtility.ToJson(report, true));
        Debug.Log("[FastLoop/HeadPlay] " + JsonUtility.ToJson(report));
        SessionState.SetBool(Key + "Finished", true); SessionState.SetInt(Key + "Exit", error == null ? 0 : 1);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else Exit();
    }
    private static void Exit() { SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(SessionState.GetInt(Key + "Exit", 1)); }
}
