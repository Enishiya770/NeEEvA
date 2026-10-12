using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Real user entry, reviewed authority, bounded search, native approach and device readback.
/// The fixture controls semantic replies only; no follow-up RoomTask decision supplies substeps.</summary>
public static partial class SpatialGoalRunnerRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string OffRequest = "请把眼前那盏小灯关掉，如果需要就先靠近。";
    private const string OnRequest = "请把刚才关掉的同一盏小灯重新打开。";
    private static int checks;
    private static string directory;
    private static HeldPlayback heldPlayback;
    public static JObject LastReport { get; private set; }

    private sealed class Run
    {
        public string name, text, state;
        public int generation, rooms, searches, matches, speech, probes, operation, sequence;
        public Vector3 root;
        public SceneVisionObservation source;
        public NevaSceneVisionIntegrationModel.Request initial, review;
        public readonly HashSet<string> factKeys = new HashSet<string>();
        public readonly HashSet<string> captureIds = new HashSet<string>();
        public readonly HashSet<string> pages = new HashSet<string>();
        public readonly JArray facts = new JArray(), choices = new JArray(), matchEvidence = new JArray();
        public float started;
        public bool concurrentReturn;
    }

    private sealed class HeldPlayback : IDisposable
    {
        private readonly AudioSource source;
        private readonly AudioClip previousClip, silentClip;
        private readonly bool previousLoop, previousPlaying;
        private readonly float previousVolume;
        public HeldPlayback(ChatSample chat)
        {
            source = (AudioSource)Get(chat, "m_AudioSource");
            Require(source != null, "Actual voice AudioSource exists.");
            previousClip = source.clip; previousLoop = source.loop; previousVolume = source.volume; previousPlaying = source.isPlaying;
            silentClip = AudioClip.Create("Unified device silent playback gate", 24000, 1, 24000, false);
            source.clip = silentClip; source.loop = true; source.volume = 0; source.Play();
        }
        public void Dispose()
        {
            source.Stop(); source.clip = previousClip; source.loop = previousLoop; source.volume = previousVolume;
            if (previousPlaying && previousClip != null) source.Play();
            UnityEngine.Object.Destroy(silentClip);
        }
    }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Unified device regression requires the existing isolated real-room fixture.");
        bool native = Argument("-nevaSpatialGoalRunnerNative", "0") == "1" || Argument("-nevaUnifiedDeviceNative", "0") == "1";
        checks = 0; directory = Path.Combine(output, "spatial-goal-runner"); Directory.CreateDirectory(directory);
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(), ["controller"] = "unified-device-task",
            ["nativeApproachRequested"] = native,
            ["scope"] = "Actual StartStreaming entry and production parser/goal review; controlled semantic search/match replies over actual rendered RGB. No quiet RoomTask model continuation, invented arrival, power result or actor teleport. Native focus additionally requires measured approach and same-entity return after an actual body turn. Semantic model accuracy is outside this controlled fixture." };
        string[] fields = { "m_EnableSceneObjectTargetMatching", "m_EnableSceneObjectAwareness", "m_EnableFirstReplyRouting", "m_SceneDeviceDistance", "m_AgentEyesOpen", "m_SceneTimePaused", "m_ReviewedGoalAnswerObligation" };
        var saved = fields.ToDictionary(key => key, key => Get(chat, key));
        bool previousSequences = model.TaskSequencesEnabled;
        SceneObjectLightSwitch lamp = null;
        var stack = new Stack<IEnumerator>();
        try
        {
            Require(SceneObjectLampCatalog.EnsureRegistered(bridge.environmentRoot, out lamp, out string reason), "Actual registered lamp: " + reason);
            LastReport["lampEntityId"] = lamp.EntityId; LastReport["originalState"] = JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));
            stack.Push(Exercise(chat, bridge, model, lamp, native));
            while (stack.Count > 0)
            {
                object current = null;
                try
                {
                    while (stack.Count > 0)
                    {
                        var routine = stack.Peek();
                        if (!routine.MoveNext()) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                        if (routine.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        current = routine.Current; break;
                    }
                }
                catch (Exception error) { LastReport["status"] = "failed"; LastReport["error"] = error.ToString(); throw; }
                if (stack.Count > 0) yield return current;
            }
            LastReport["status"] = "passed";
        }
        finally
        {
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            heldPlayback?.Dispose(); heldPlayback = null;
            Call(chat, "CancelSpatialGoalRunner", "unified-regression-cleanup"); Call(chat, "CancelSceneSearchTask", "unified-regression-cleanup");
            Call(chat, "CancelSpatialGoal", "unified-regression-cleanup"); Call(chat, "CancelSceneAction", "unified-regression-cleanup");
            Call(chat, "InvalidateFormalResponse", "unified-regression-cleanup"); Call(chat, "ClearScheduledAgentWake");
            Set(chat, "<IsAISpeaking>k__BackingField", false);
            foreach (var item in saved) Set(chat, item.Key, item.Value);
            model.TaskSequencesEnabled = previousSequences;
            if (lamp != null) { LastReport["restoredLamp"] = lamp.RestoreOriginal(out string restore); LastReport["restoreReason"] = restore; }
            if ((string)LastReport["status"] == "running") LastReport["status"] = "failed";
            Save();
        }
    }

    private static IEnumerator Exercise(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, SceneObjectLightSwitch lamp, bool native)
    {
        yield return RejectedAuthority(chat, bridge, model, lamp);
        float initialDistance = Vector3.Distance(bridge.avatar.transform.position, lamp.Root.position);
        float operationLimit = native ? Mathf.Clamp(initialDistance - .25f, 1.65f, 2.5f) : 2.5f;
        if (native) Require(initialDistance > operationLimit + .15f,
            "The actual native start is outside one unchanged feasible operation range; no fixture movement fabricates arrival.");
        Set(chat, "m_SceneDeviceDistance", operationLimit);
        LastReport["initialDistanceMeters"] = initialDistance; LastReport["operationLimitMeters"] = operationLimit;
        Run run = null;
        model.TaskSequencesEnabled = true;
        bool oldControl = model.ControlReviewsEnabled;
        model.ControlReviewsEnabled = !native;
        yield return Begin(chat, bridge, model, lamp, "reviewed-first-write", OffRequest + "然后回到我身边。", "off", "set-object-state", value => run = value);
        heldPlayback = new HeldPlayback(chat);
        yield return Wait(() => chat.IsVoiceOutputPlaying, 3, "real playback gate held");
        Set(chat, "<IsAISpeaking>k__BackingField", true);
        yield return Drive(chat, bridge, model, lamp, run);
        Require(chat.IsAISpeaking && chat.IsVoiceOutputPlaying && model.Probes.Count == run.probes,
            "Unified task progresses while speech/round gates are occupied, without intermediate work review.");
        Require(model.SceneSearchRequests.Count > run.searches && run.matchEvidence.Count >= 2,
            "The reviewed legacy first write enters independent search and a separate final operation match.");
        Require((string)Goal(chat)["boundEntityId"] == lamp.EntityId && (string)Goal(chat)["status"] == "satisfied" && !lamp.ReadState().on,
            "Only actual same-entity off readback completes the reviewed goal.");
        if (native)
        {
            var arrival = run.facts.OfType<JObject>().FirstOrDefault(f => (string)f["phase"] == "arrived");
            Require(arrival != null && (bool?)arrival["groundArrived"] == true && (bool?)arrival["objectReobserved"] == true &&
                Vector3.Distance(run.root, bridge.avatar.transform.position) > .05f && bridge.Controller.OperationRevision > run.operation &&
                Vector3.Distance(bridge.avatar.transform.position, lamp.Root.position) <= operationLimit,
                "Automatic native approach truly arrives and reobserves before power mutation, using an unchanged distance limit.");
            LastReport["actualNativeArrival"] = arrival.DeepClone();
            LastReport["nativeRootTranslation"] = Vector3.Distance(run.root, bridge.avatar.transform.position);
        }
        else Require(run.concurrentReturn && bridge.Controller.OperationRevision > run.operation,
            "Actual device completion submits the approved return before terminal prose is generated.");
        if (run.concurrentReturn) bridge.StopMovement("continuation-tested-end", "End controlled successor before remaining fixtures");
        yield return Audit(chat, model, lamp, run);
        model.ControlReviewsEnabled = oldControl;
        yield return SequenceContinuationChecks(chat, bridge, model, lamp, run);
        model.TaskSequencesEnabled = false;
        AddCase(run, "legacy-first-write-unified-search-to-readback", chat);

        JObject operated = ((JArray)Call(chat, "BuildSpatialGoalReferentSources")).OfType<JObject>()
            .FirstOrDefault(source => (string)source["kind"] == "operated-goal" && (string)source["entityId"] == lamp.EntityId);
        Require(operated != null, "The real off receipt supplies the next request's historical same-entity source.");
        string oldCurrentReference = (string)run.choices.OfType<JObject>().First(choice => (string)choice["choice"] == "current-candidate")["referenceId"];
        if (native) yield return TurnAway(chat, bridge, lamp);
        yield return Begin(chat, bridge, model, lamp, "same-entity-reopen", OnRequest, "on", "inspect-object", value => run = value, operated, spokenInput: native);
        if (native)
        {
            Call(chat, "BuildSceneVisibleCatalogContext", run.source);
            var oldSelection = new object[] { run.source, oldCurrentReference, null, null };
            Require(!(bool)Call(chat, "TryGetSceneVisibleCatalogCandidate", oldSelection),
                "A retained known point cannot resurrect an old current-candidate reference after real user-speech invalidation.");
            LastReport["voiceReopenBoundary"]["oldCurrentCandidateRejected"] = true;
            LastReport["voiceReopenBoundary"]["newCaptureId"] = run.source.CaptureId;
        }
        Require((string)Goal(chat)["boundEntityId"] == lamp.EntityId && (string)Goal(chat)["referentSourceId"] == (string)operated["sourceId"],
            "Typed operated-history review binds the new operation to the actually operated entity before searching.");
        yield return Drive(chat, bridge, model, lamp, run);
        Require(lamp.ReadState().on && (string)Goal(chat)["boundEntityId"] == lamp.EntityId &&
            (string)Goal(chat)["completionEvidence"]?["entityId"] == lamp.EntityId,
            "Historical recall reopens the same device only after current pixels and actual readback.");
        if (native)
        {
            var search = (JObject)Call(chat, "BuildSceneSearchContext");
            var receipts = search["receipts"] as JArray;
            Require(receipts?.OfType<JObject>().Any(receipt => (string)receipt["choice"] == "bound-historical-object" &&
                    (string)receipt["entityId"] == lamp.EntityId) == true &&
                receipts.OfType<JObject>().Any(receipt => (string)receipt["choice"] == "historical-object" &&
                    (bool?)receipt["observed"] == true && (string)receipt["sourceCaptureId"] != (string)receipt["resultCaptureId"]) &&
                run.matchEvidence.OfType<JObject>().Any(match => (string)match["entityId"] == lamp.EntityId) &&
                model.SceneSearchRequests.Count == run.searches,
                "The known entity is physically revisited from its frozen point, freshly and independently matched, without asking a model to guess another target.");
            var returnStep = receipts.OfType<JObject>().LastOrDefault(receipt =>
                (string)receipt["choice"] == "historical-object" && (string)receipt["intent"] == "turn-in-place");
            var oriented = bridge.LastSceneOrientResult;
            var returnedView = run.facts.OfType<JObject>().LastOrDefault(fact =>
                (string)fact["actionId"] == (string)returnStep?["actionId"] && (string)fact["phase"] == "observed");
            Require(returnStep != null && (bool?)returnStep["stationaryRootRebased"] == true &&
                (bool?)returnStep["observed"] == true && oriented != null && oriented.terminal && oriented.oriented &&
                oriented.actionId == (string)returnStep["actionId"] && oriented.captureId == (string)returnStep["sourceCaptureId"] &&
                oriented.controllerOperationRevision == (int?)returnStep["controllerOperationRevision"] &&
                (float?)returnStep["rootDisplacementFromActionStart"] >= 0f && (float?)returnStep["rootDisplacementFromActionStart"] <= .16f &&
                returnedView != null && (string)returnedView["captureId"] == (string)returnStep["sourceCaptureId"] &&
                !string.IsNullOrEmpty((string)returnStep["resultCaptureId"]) &&
                (string)returnedView["resultCaptureId"] == (string)returnStep["resultCaptureId"] &&
                (string)returnStep["sourceCaptureId"] != (string)returnStep["resultCaptureId"],
                "Only the same native oriented operation with a new observed RGB and bounded actual root displacement rebases the historical-return search station.");
            LastReport["nativeReturnRebase"] = new JObject { ["searchStep"] = returnStep.DeepClone(),
                ["nativeOrientResult"] = JObject.Parse(JsonUtility.ToJson(oriented)), ["observedAction"] = returnedView.DeepClone() };
        }
        yield return Audit(chat, model, lamp, run);
        AddCase(run, native ? "known-same-entity-actual-return-and-on" : "known-same-entity-current-recheck-and-on", chat);

        // Freshness tests consume wall-clock age from actual captures; no timestamp or result injection.
        yield return Begin(chat, bridge, model, lamp, "expired-first-room-frame", OffRequest, "off", "inspect-object", value => run = value, delayInitial: true);
        yield return Drive(chat, bridge, model, lamp, run);
        Require(run.captureIds.Count >= 3 && !lamp.ReadState().on && model.DeviceGoalReviews.Count(review => review.Input.Contains(OffRequest)) > 0,
            "A delayed first decision refreshes into the unified task without using its old target pixels.");
        yield return Audit(chat, model, lamp, run); AddCase(run, "actual-expired-first-frame-refreshed", chat);

        yield return Begin(chat, bridge, model, lamp, "expired-operation-match", OffRequest, "off", "inspect-object", value => run = value);
        yield return Drive(chat, bridge, model, lamp, run, delayFirstMatch: true);
        Require(run.matchEvidence.Count >= 3 && !lamp.ReadState().on,
            "An expired independent match is refreshed and rechecked, never reused directly as device authority.");
        yield return Audit(chat, model, lamp, run); AddCase(run, "actual-expired-match-finite-fresh-recovery", chat);

        yield return SearchIncomplete(chat, bridge, model, lamp);
        yield return NonDeviceCandidateRecovery(chat, bridge, model, lamp);
        foreach (string boundary in new[] { "new-user", "new-generation", "application-pause", "eyes-closed", "room-rebound" })
            yield return Interrupted(chat, bridge, model, lamp, boundary, false);
        yield return Interrupted(chat, bridge, model, lamp, "direct-stream-replacement", true);
        yield return DialogueContinuation(chat, bridge, model, lamp);
        yield return AnswerThenResumePlayback(chat, bridge, model, lamp, false);
        yield return AnswerThenResumePlayback(chat, bridge, model, lamp, true);
        yield return AnswerThenResumePlayback(chat, bridge, model, lamp, false, true);
        yield return LatestHumanPreface(chat, bridge, model, lamp, native);
        yield return InitialHumanPreface(chat, bridge, model, lamp);
        yield return BodyHandoff(chat, bridge, model, lamp);
        yield return SceneGoalReferentRegression.RunDialogueRoutingChecks(chat, bridge, model, Path.GetDirectoryName(directory));
    }

    private static IEnumerator Prepare(ChatSample chat, ArdyRoomDialogueBridge bridge, SceneObjectLightSwitch lamp, string text, string state, bool spokenInput = false)
    {
        Call(chat, "CancelSpatialGoalRunner", "unified-next"); Call(chat, "CancelSceneSearchTask", "unified-next");
        Call(chat, "CancelSpatialGoal", "unified-next"); Call(chat, "CancelSceneAction", "unified-next");
        yield return Wait(() => !bridge.ReservesBody && (!(Get(chat, "m_SceneAttention") is NevaSceneAttention attention) || !attention.IsBusy), 8, "previous body/head released");
        if (spokenInput)
        {
            int oldEpoch = (int)Get(chat, "m_SceneVisionEpoch");
            var oldImage = chat.SceneVisionCapture.LatestObservation;
            var before = ((JArray)Call(chat, "KnownSceneObjectHistory")).OfType<JObject>().Single(item => (string)item["entityId"] == lamp.EntityId);
            // Invoke the real recording-start cancellation, then provide controlled
            // already-accepted text below. This is not an ASR recognition test.
            chat.NotifyUserStartedSpeaking();
            var after = ((JArray)Call(chat, "KnownSceneObjectHistory")).OfType<JObject>().Single(item => (string)item["entityId"] == lamp.EntityId);
            Require((int)Get(chat, "m_SceneVisionEpoch") > oldEpoch && !(bool)Call(chat.SceneVisionCapture, "IsObservationBindingCurrent", oldImage) &&
                (bool?)after["positionMemoryValid"] == true && JToken.DeepEquals(before["pointAtCapture"], after["pointAtCapture"]),
                "Real speech start invalidates the previous vision/current-image lease while preserving the confirmed frozen same-device point.");
            LastReport["voiceReopenBoundary"] = new JObject { ["oldVisionEpoch"] = oldEpoch, ["newVisionEpoch"] = (int)Get(chat, "m_SceneVisionEpoch"),
                ["oldCaptureId"] = oldImage.CaptureId, ["oldImageBindingCurrent"] = false,
                ["knownBefore"] = before.DeepClone(), ["knownAfter"] = after.DeepClone(),
                ["scope"] = "Real NotifyUserStartedSpeaking cancellation and production accepted-text preparation. User text is controlled; ASR/audio transcription is not simulated." };
            // The ordinary input boundary already cancelled its requests; do not
            // add ResetRound's extra artificial visual invalidation on this path.
            Set(chat, "m_FormalResponseInFlight", false); Set(chat, "m_AgentRoundInFlight", false);
            Set(chat, "m_AgentCurrentRoundIsTick", false); Call(chat, "ClearScheduledAgentWake");
        }
        else typeof(SceneActionIntegrationRegression).GetMethod("ResetRound", Flags).Invoke(null, new object[] { chat });
        Set(chat, "m_EnableSceneObjectAwareness", true); Set(chat, "m_EnableSceneObjectTargetMatching", true);
        Set(chat, "m_EnableFirstReplyRouting", false); Set(chat, "m_SceneTimePaused", false);
        Set(chat, "m_PendingDeviceClarification", null); Set(chat, "m_IndependentSpatialRequestScope", null);
        Require(lamp.TrySetPower("unified-fixture-" + Guid.NewGuid().ToString("N"), state != "on", out _, out string reason),
            "Fixture starts the actual lamp opposite the requested state: " + reason);
        Call(chat, "RecordAcceptedMotionUserTurn", text); Call(chat, "ResetWorkReview", true);
        Set(chat, "m_LastUserMsg", text); Set(chat, "m_LastUserEvidence", text);
        if (spokenInput)
        {
            chat.PrepareUserTurn(text, text);
            Require(!(bool)Get(chat, "m_UserSpeechActiveForAutonomy"), "Production accepted-input preparation releases the actual speech-start hold.");
        }
    }

    private static IEnumerator Begin(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model,
        SceneObjectLightSwitch lamp, string name, string text, string state, string initialIntent, Action<Run> ready,
        JObject referent = null, bool delayInitial = false, bool holdApproval = false, bool spokenInput = false)
    {
        yield return Prepare(chat, bridge, lamp, text, state, spokenInput);
        if (name == "same-entity-reopen")
        {
            var history = (JArray)Call(chat, "ReadHistoricalDeviceEvents");
            Require(history.OfType<JObject>().Any(e => (string)e.SelectToken("goal.boundEntityId") == lamp.EntityId &&
                (string)e.SelectToken("goal.desiredState") == "off" && (string)e.SelectToken("lastAction.phase") == "performed" &&
                (int?)e["sourceUserTurn"] < CurrentTurn(chat)), "Actual off event survives the real new-user-turn/reset boundary.");
            Require((bool?)((JObject)Call(chat, "BuildMovementPriorResults"))?["historicalResult"] != true,
                "Past evidence does not become the new work's own completed prerequisite.");
            File.WriteAllText(Path.Combine(directory, "cross-user-device-history.json"), history.ToString(Formatting.Indented));
        }
        var run = new Run { name = name, text = text, state = state, rooms = RoomPlans(model), searches = model.SceneSearchRequests.Count,
            matches = model.Matches.Count, speech = model.Speech.Count, probes = model.Probes.Count,
            operation = bridge.Controller.OperationRevision, sequence = (int)Get(chat, "m_MotionSequence"),
            root = bridge.avatar.transform.position, started = Time.realtimeSinceStartup };
        Call(chat, "RequestSceneObservation", "unified-fixture-source", new Action<SceneVisionObservation>(value => run.source = value));
        yield return Wait(() => run.source?.HasImage == true, 8, "actual initial RGB/opaque ID");
        run.captureIds.Add(run.source.CaptureId);
        int reviews = model.DeviceGoalReviews.Count;
        Call(chat, "StartStreaming", text, false, null, false, false, false, null, false, run.source, true);
        Set(chat, "m_AgentRoundInFlight", true);
        yield return Wait(() => RoomPlans(model) == run.rooms + 1, 8, "original production RoomTask planner");
        run.generation = (int)Get(chat, "m_FormalResponseGeneration"); run.initial = model.Decisions.Last(r => r.Kind == "room");
        Export(name + "-initial", run.initial);
        // The former pixel substep is deliberately merely a proposal. The unified task must choose its own current target.
        var proposal = new JObject { ["intent"] = initialIntent, ["origin"] = "user", ["evidence"] = text,
            ["visualTarget"] = new JObject { ["captureId"] = run.source.CaptureId, ["x"] = .5f, ["y"] = .5f, ["label"] = "当前画面中心" },
            ["deviceGoal"] = new JObject { ["operation"] = "set", ["requestQuote"] = text,
                ["targetDescription"] = referent == null ? "眼前那盏小灯" : "刚才关掉的同一盏小灯", ["desiredState"] = state } };
        if (initialIntent == "set-object-state") proposal["objectState"] = state;
        if (initialIntent == "orient-view")
        {
            proposal.Remove("visualTarget"); proposal["evidence"] = "转头向左看看";
            proposal["viewChange"] = new JObject { ["captureId"] = run.source.CaptureId, ["yaw"] = -30, ["pitch"] = 0 };
        }
        Require(RoomTaskProtocol.TryDecision(proposal.ToString(), text, false, out _), "Initial fixture proposal passes the production parser.");
        if (model.TaskSequencesEnabled)
        {
            proposal["taskPlan"] = new JArray(
                new JObject { ["id"] = "s1", ["quote"] = "把眼前那盏小灯关掉", ["kind"] = "device-state", ["desiredState"] = "off", ["after"] = new JArray() },
                new JObject { ["id"] = "s2", ["quote"] = "回到我身边", ["kind"] = "approach", ["desiredState"] = null, ["after"] = new JArray("s1") });
            proposal["taskStep"] = "s1";
            proposal["taskCoverage"] = new JArray(new JObject { ["quote"] = text, ["kind"] = "action", ["stepIds"] = new JArray("s1", "s2") });
        }
        if (delayInitial) yield return Wait(() => Time.realtimeSinceStartup - run.source.CapturedAtRealtime > 12.1f, 15, "actual source expires before first planner callback");
        run.initial.Complete(true, proposal.ToString(), "controlled-unified-initial");
        yield return Wait(() => model.DeviceGoalReviews.Count == reviews + 1, 8, "independent original request authority review");
        run.review = model.DeviceGoalReviews.Last(); Export(name + "-goal-review", run.review);
        Require(model.SceneSearchRequests.Count == run.searches && model.Matches.Count == run.matches && Get(chat, "m_SpatialGoalRunner") == null &&
            lamp.ReadState().on == (state != "on"), "No search, old pixel action or device mutation precedes goal approval.");
        if (!holdApproval)
        {
            Approve(run.review, referent);
            yield return Wait(() => Get(chat, "m_SpatialGoalRunner") != null || model.Speech.Count > run.speech, 8, "reviewed goal is handed to the unified controller");
            Require((string)Runner(chat, run)["controller"] == "unified-device-task" && Get(chat, "m_SpatialGoalRunner") != null,
                "The first approved proposal enters the unified controller before any old substep dispatch.");
        }
        ready(run);
    }

    private static void Approve(NevaSceneVisionIntegrationModel.Request review, JObject referent)
    {
        if (referent != null)
        {
            var sources = JObject.Parse(review.Input)["referentSources"] as JArray;
            Require(sources != null && sources.OfType<JObject>().Any(source => (string)source["sourceId"] == (string)referent["sourceId"]),
                "The same-entity authorization cites a source actually offered in this current review.");
        }
        review.Complete(true, new JObject { ["verdict"] = "approved", ["relation"] = "new", ["requestAssessment"] = "requested",
            ["referentKind"] = referent == null ? "unbound" : (string)referent["kind"],
            ["referentSourceId"] = referent == null ? "none" : (string)referent["sourceId"],
            ["reason"] = "Controlled original user explicitly authorizes this target state; a source constrains identity only." }.ToString(), "controlled-unified-approved");
    }

    private static IEnumerator Drive(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model,
        SceneObjectLightSwitch lamp, Run run, bool delayFirstMatch = false, int consumedSearches = 0, int consumedMatches = 0)
    {
        int searches = run.searches + consumedSearches, matches = run.matches + consumedMatches;
        bool delayed = false;
        float until = Time.realtimeSinceStartup + 145f;
        while (model.Speech.Count == run.speech)
        {
            Observe(chat, run);
            Require(RoomPlans(model) == run.rooms + 1, "Approved task has no quiet RoomTask continuation or repair planner.");
            if (Time.realtimeSinceStartup > until) throw new TimeoutException("Unified task did not terminate: " + Runner(chat, run));
            if (model.SceneSearchRequests.Count > searches)
            {
                var request = model.SceneSearchRequests[searches++]; Export(run.name + "-search-" + (searches - run.searches), request);
                string reply = SearchChoice(request, run, lamp.EntityId);
                request.Complete(true, reply, "controlled-unified-search-selection");
            }
            if (model.Matches.Count > matches)
            {
                var request = model.Matches[matches++]; var input = JObject.Parse(request.Input);
                Export(run.name + "-match-" + (matches - run.matches), request);
                string capture = (string)input["candidates"]?["captureId"];
                if (delayFirstMatch && !delayed && (string)input["intent"] == "set-object-state")
                {
                    delayed = true; var observation = chat.SceneVisionCapture.LatestObservation;
                    Require(observation.CaptureId == capture && lamp.ReadState().on, "The held matcher owns a genuine current image before expiry.");
                    yield return Wait(() => Time.realtimeSinceStartup - observation.CapturedAtRealtime > 12.05f, 14, "actual independent match image expires");
                    Require(lamp.ReadState().on && model.Speech.Count == run.speech, "Expired pending semantics have not already operated or completed.");
                }
                CompleteMatch(request, lamp.EntityId, run);
            }
            if (Get(chat, "m_SequenceBodyContinuation") != null && model.ControlReviews.Count > 0 && !run.concurrentReturn)
            {
                var control = model.ControlReviews.Last();
                Require((string)JObject.Parse(control.Input)["roomControlReview"] == RoomControlReviewProtocol.Continuation &&
                    model.Speech.Count == run.speech && (bool?)Goal(chat)["completionEvidenceValid"] == true,
                    "Production device completion opens the bounded successor gate before result generation.");
                Export(run.name + "-automatic-continuation", control);
                run.concurrentReturn = true;
                control.Complete(true, "{\"target\":\"current-user\",\"readiness\":\"satisfied\",\"stepId\":\"s2\",\"evidence\":\"回到我身边\",\"reason\":\"Explicit immediate return after actual completion.\"}", "controlled-automatic-continuation");
                var ledger = (JObject)Call(chat, "ReadRoomTaskSequence");
                Require((string)ledger.SelectToken("steps[1].actionId") == bridge.ActionId &&
                    (string)ledger.SelectToken("steps[1].status") != "pending",
                    "Automatic continuation records the actual bridge action without another RoomTask plan.");
                LastReport["automaticSequenceContinuation"] = new JObject { ["sequence"] = ledger, ["deviceProof"] = Goal(chat)["completionEvidence"]?.DeepClone(),
                    ["resultGenerationStartedAfterSubmission"] = true, ["extraLargePlans"] = 0 };
            }
            yield return null;
        }
        Observe(chat, run);
        Require(model.Speech.Count == run.speech + 1 && model.Speech.Last().Stage == FormalResponseStage.SpatialGoalResult &&
            RoomPlans(model) == run.rooms + 1, "One short terminal result replaces all intermediate planning/reply turns.");
        Require((string)Goal(chat)["status"] == "satisfied" && (bool?)Goal(chat)["completionEvidence"]?["actual"]?["on"] == (run.state == "on") &&
            lamp.ReadState().on == (run.state == "on"), "Completion requires the requested actual lamp state and immutable runtime proof.");
        int completedGeneration = (int)Get(chat, "m_FormalResponseGeneration");
        var events = (JArray)Call(chat, "ReadHistoricalDeviceEvents");
        Require(events.Count > 0 && (string)events.Last().SelectToken("goal.boundEntityId") == lamp.EntityId &&
            (string)events.Last().SelectToken("lastAction.phase") == "performed", "Actual operation archived independently of work ownership.");
        events.Last()["goal"]["desiredState"] = "tampered";
        Require((string)((JArray)Call(chat, "ReadHistoricalDeviceEvents")).Last().SelectToken("goal.desiredState") == run.state,
            "Historical reporting cannot mutate the archive.");
        float eventSession = (float)Get(chat, "m_AgentSessionStartTime");
        Set(chat, "m_AgentSessionStartTime", eventSession + 1);
        try { Require(((JArray)Call(chat, "ReadHistoricalDeviceEvents")).Count == 0, "Session replacement cannot borrow device history."); }
        finally { Set(chat, "m_AgentSessionStartTime", eventSession); }
        try
        {
            Set(chat, "m_FormalResponseGeneration", completedGeneration + 1);
            var prior = (JObject)Call(chat, "BuildMovementPriorResults");
            File.WriteAllText(Path.Combine(directory, run.name + "-next-generation-history.json"), prior?.ToString(Formatting.Indented) ?? "null");
            Require((bool?)prior?["historicalResult"] == true && (string)prior?.SelectToken("goal.desiredState") == run.state &&
                (string)prior?.SelectToken("lastAction.phase") == "performed" &&
                (bool?)prior?.SelectToken("goal.operationSummary.operationAttempted") == true,
                "An actually performed device operation remains reportable after the return step advances the speech generation.");
            if (model.TaskSequencesEnabled && !run.concurrentReturn)
            {
                var sequence = (JObject)Call(chat, "ReadRoomTaskSequence");
                Require((string)sequence.SelectToken("steps[0].status") == "completed" &&
                    ((JArray)sequence["readyStepIds"]).Values<string>().SequenceEqual(new[] { "s2" }),
                    "Actual device completion unlocks only the return step in the production task ledger.");
                string next = new JObject { ["intent"] = "approach", ["origin"] = "user", ["evidence"] = "回到我身边", ["taskStep"] = "s2" }.ToString();
                Require(RoomTaskProtocol.TryDecision(RoomTaskSequenceProtocol.DecisionOnly(next), run.text, false, out var decision, out _),
                    "Return retains the complete same-user authorization.");
                object[] selection = { next, decision, null };
                Require((bool)Call(chat, "ValidateRoomTaskSequence", selection),
                    "The production submission guard now permits that return, without another device write.");
                File.WriteAllText(Path.Combine(directory, "completed-sequence-ready-return.json"), sequence.ToString(Formatting.Indented));
            }
            prior["goal"]["operationSummary"]["operationAttempted"] = false;
            Require((bool?)((JObject)Call(chat, "BuildMovementPriorResults")).SelectToken("goal.operationSummary.operationAttempted") == true,
                "A consumer cannot mutate the stored historical result.");
            int epoch = (int)Get(chat, "m_WorkEpoch"); Set(chat, "m_WorkEpoch", epoch + 1);
            try { Require((bool?)((JObject)Call(chat, "BuildMovementPriorResults"))?["historicalResult"] != true,
                "A replacement work owner cannot borrow the completed step as its own result."); }
            finally { Set(chat, "m_WorkEpoch", epoch); }
        }
        finally { Set(chat, "m_FormalResponseGeneration", completedGeneration); }
    }

    private static string SearchChoice(NevaSceneVisionIntegrationModel.Request request, Run run, string entity)
    {
        var input = JObject.Parse(request.Input); var current = input["visibleCatalog"]?["current"] as JObject;
        string capture = (string)input["currentCaptureId"]; if (capture != null) run.captureIds.Add(capture);
        Require((string)input["task"]?["originalUserRequest"] == run.text, "Search retains the complete reviewed user goal.");
        if ((string)input["imageRole"] == "historical")
        {
            var oldCandidate = (input["historicalCandidates"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(item => (string)item["entityId"] == entity);
            var recall = new JObject { ["choice"] = oldCandidate == null ? "done" : "historical-object", ["selectionRole"] = oldCandidate == null ? "exploration" : "target",
                ["referenceId"] = oldCandidate == null ? "" : (string)oldCandidate["referenceId"],
                ["reason"] = "Controlled recall from this actual earlier image; only an offered frozen object point is a useful target hint." };
            Require(!string.IsNullOrEmpty((string)input["historicalCaptureId"]) && !string.IsNullOrEmpty(request.Image) &&
                (oldCandidate == null || !string.IsNullOrEmpty((string)recall["referenceId"])),
                "Historical recall receives real earlier RGB and uses only that request's offered object reference.");
            run.choices.Add(recall.DeepClone()); return recall.ToString();
        }
        var candidate = (current?["candidates"] as JArray)?.OfType<JObject>().FirstOrDefault(item => (string)item["entityId"] == entity);
        string choice, reference;
        if (candidate != null) { choice = "current-candidate"; reference = (string)candidate["referenceId"]; }
        else
        {
            int page = (int?)current?["pageIndex"] ?? 0;
            string pageKey = capture + ":" + page; run.pages.Add(pageKey);
            var next = (current?["availablePages"] as JArray)?.OfType<JObject>().FirstOrDefault(item => !run.pages.Contains(capture + ":" + (int?)item["pageIndex"]));
            var historical = (input["visibleCatalog"]?["memory"] as JArray)?.OfType<JObject>().FirstOrDefault(item => (string)item["entityId"] == entity);
            if (next != null) { choice = "next-page"; reference = (string)next["referenceId"]; }
            else if (historical != null) { choice = "historical-object"; reference = (string)(historical["referenceId"] ?? historical["selectionId"]); }
            else throw new InvalidOperationException("Actual offered current pages/history contain no target; fixture cannot invent its position: " + input["visibleCatalog"]);
        }
        var result = new JObject { ["choice"] = choice, ["referenceId"] = reference, ["selectionRole"] = choice == "current-candidate" || choice == "historical-object" ? "target" : "exploration", ["reason"] = "Controlled semantic selection from this actual offered image/catalog." };
        Require(!string.IsNullOrEmpty(reference), "Selection uses an actually offered reference ID.");
        run.choices.Add(result.DeepClone()); return result.ToString();
    }

    private static void CompleteMatch(NevaSceneVisionIntegrationModel.Request request, string entity, Run run)
    {
        var input = JObject.Parse(request.Input);
        var selected = ((JArray)input["candidates"]["candidates"]).OfType<JObject>().SingleOrDefault(item => (string)item["entityId"] == entity);
        Require(selected != null, "Independent matcher receives the actual same-entity candidate in its OWN fresh numbered image.");
        run.matchEvidence.Add(new JObject { ["intent"] = input["intent"]?.DeepClone(), ["captureId"] = input["candidates"]["captureId"]?.DeepClone(),
            ["requestedEntityId"] = input["requestedEntityId"]?.DeepClone(), ["entityId"] = entity, ["candidateId"] = selected["candidateId"]?.DeepClone() });
        request.Complete(true, new JObject { ["verdict"] = "matched", ["candidateRole"] = "target", ["candidateId"] = selected["candidateId"]?.DeepClone(),
            ["reason"] = "Controlled independent current pixels confirm the complete requested same-entity target." }.ToString(), "controlled-unified-current-match");
    }

    private static IEnumerator Audit(ChatSample chat, NevaSceneVisionIntegrationModel model, SceneObjectLightSwitch lamp, Run run)
    {
        var terminal = model.Speech.Last(); int history = model.m_DataList.Count;
        Require(SpatialGoalResultContextPolicy.TryReadContext(terminal.Context, out string original, out var facts) && original == run.text &&
            (string)facts["goal"]?["status"] == "satisfied" && !terminal.RecordAssistantHistory, "Terminal generation receives proof and the original task.");
        bool noProgressRepair = run.name == "expired-first-room-frame";
        bool testRepair = run.name == "reviewed-first-write" || noProgressRepair;
        string text = testRepair ? "桌上的小灯已成功关闭。" : run.state == "on" ? "The requested lamp is now on." : "The requested lamp is now off.";
        terminal.SpeechCallback(new SpeechText(text, testRepair ? "ja" : "en", actionDependency: SpeechActionDependency.Independent));
        Require(model.m_DataList.Count == history && ((SpeechTextBuffer)Get(chat, "m_SentenceBuffer")).Length == 0,
            "Unaudited terminal speech is neither published nor queued.");
        int reviews = model.Decisions.Count(request => request.Kind == "room-review"); terminal.SpeechCompleteCallback(text);
        yield return Wait(() => model.Decisions.Count(request => request.Kind == "room-review") == reviews + 1, 8, "terminal result consistency audit");
        heldPlayback?.Dispose(); heldPlayback = null; Set(chat, "<IsAISpeaking>k__BackingField", false);
        var reviewer = model.Decisions.Last(request => request.Kind == "room-review");
        if (testRepair)
        {
            string proofBefore = Goal(chat)["completionEvidence"].ToString(); int writers = model.Speech.Count;
            string rejection = new JObject { ["verdict"] = "inconsistent", ["factScope"] = "scene-action", ["resultReport"] = run.state,
                ["actualLanguage"] = "zh", ["reason"] = "Chinese result was incorrectly tagged as Japanese." }.ToString();
            reviewer.Complete(true, rejection, "language-test:terminal-mismatch");
            yield return Wait(() => model.Speech.Count == writers + 1, 8, "one terminal language correction");
            var repair = model.Speech.Last(); var packet = JObject.Parse(repair.Input);
            Require(repair.Stage == FormalResponseStage.SpatialGoalResult && !repair.RecordAssistantHistory &&
                (string)packet["kind"] == "terminal-result-speech-repair" && (string)packet["requiredOutputLanguage"] == "ja" &&
                (string)packet["rejectedPublicSpeech"] == text && packet["humanInput"] == null && packet["boundReplyFacts"] == null,
                "Actual rejection callback dispatches the typed terminal correction without duplicating generic human/result context.");
            Require(SpatialGoalResultContextPolicy.TryReadContext(repair.Context, out string repairOriginal, out var repairFacts) &&
                repairOriginal == run.text && JToken.DeepEquals(SpatialGoalResultContextPolicy.ProjectResultFacts(repairFacts)?["goal"]?["completionEvidence"], facts["goal"]["completionEvidence"]),
                "Correction retains the complete user task and identical successful device proof.");
            var host = new GameObject("TerminalCorrectionSerializer"); host.SetActive(false);
            var assets = new[] { "persona", "language", "behavior" }.Select(name =>
                new TextAsset(File.ReadAllText("Assets/AIChatTookit/Prompts/" + name + ".txt")) { name = name }).ToArray();
            try
            {
                var provider = host.AddComponent<ChatQW>(); provider.m_Backend = ChatQW.BackendType.Local;
                typeof(LLM).GetField("m_PromptFiles", Flags).SetValue(provider, assets);
                string wire = (string)typeof(ChatQW).GetMethod("BuildSpatialGoalResultRequestJsonForMessages", Flags).Invoke(provider,
                    new object[] { model.m_DataList, true, repair.Context, repair.Input, repair.Image });
                Require(wire != null, "Actual runtime terminal repair context and current RGB fit the unchanged 6000 budget.");
                File.WriteAllText(Path.Combine(directory, "terminal-language-" + (noProgressRepair ? "no-progress" : "repair") + "-request.json"), wire);
                File.WriteAllText(Path.Combine(directory, "terminal-language-" + (noProgressRepair ? "no-progress" : "repair") + "-feedback.json"), repair.Input);
            }
            finally { UnityEngine.Object.DestroyImmediate(host); foreach (var asset in assets) UnityEngine.Object.DestroyImmediate(asset); }
            reviewer.Complete(true, rejection, "language-test:duplicate-terminal-mismatch");
            Require(model.Speech.Count == writers + 1 && model.m_DataList.Count == history &&
                Goal(chat)["completionEvidence"].ToString() == proofBefore && !lamp.ReadState().on,
                "Duplicate rejection cannot retry, release old speech, replay an action or change the actual device state.");
            if (noProgressRepair)
            {
                // Actual live-model failure: same Chinese words tagged ja.
                // One failed correction must settle truthfully, without another
                // generation or an invented context-budget/device failure.
                repair.SpeechCallback(new SpeechText(text, "ja", actionDependency: SpeechActionDependency.Independent));
                repair.SpeechCompleteCallback(text);
                string trusted = SpatialGoalResultContextPolicy.BuildTrustedReport(repairFacts, "ja");
                Require(model.m_DataList.Count == history + 1 && model.m_DataList.Last().content.Contains(trusted) &&
                    !model.m_DataList.Last().content.Contains(text) && !(bool)Get(chat, "m_FormalResponseInFlight") &&
                    model.Speech.Count == writers + 1 && model.Decisions.Count(request => request.Kind == "room-review") == reviews + 1,
                    "Identical wrong-language repair settles once with the verified Japanese result, without another model request.");
                Require(!(bool)Call(chat, "CurrentFormalGenerationFailed", (int)Get(chat, "m_FormalResponseGeneration")) &&
                    Goal(chat)["completionEvidence"].ToString() == proofBefore && !lamp.ReadState().on,
                    "A failed language rewrite creates neither a fake response-budget failure nor a failed/repeated device operation.");
                LastReport["terminalLanguageNoProgress"] = new JObject { ["boundedAttempts"] = 1,
                    ["actualCallback"] = true, ["published"] = model.m_DataList.Last().content, ["trustedResult"] = trusted };
                yield break;
            }
            string corrected = "テーブルの小さなランプを消しました。";
            repair.SpeechCallback(new SpeechText(corrected, "ja", actionDependency: SpeechActionDependency.Independent));
            repair.SpeechCompleteCallback(corrected);
            yield return Wait(() => model.Decisions.Count(request => request.Kind == "room-review") == reviews + 2, 8, "repaired terminal independently reviewed");
            reviewer = model.Decisions.Last(request => request.Kind == "room-review");
            LastReport["terminalLanguageRepair"] = new JObject { ["boundedAttempts"] = 1, ["actualCallback"] = true,
                ["resultProofUnchanged"] = true, ["request"] = "terminal-language-repair-request.json" };
        }
        reviewer.Complete(true, new JObject { ["verdict"] = "consistent", ["factScope"] = "scene-action", ["resultReport"] = run.state,
            ["reason"] = "Controlled exact runtime readback supports the public result." }.ToString(), "controlled-unified-result-audit");
        Require(model.m_DataList.Count == history + 1 && !(bool)Get(chat, "m_FormalResponseInFlight"), "Only the one audited terminal is committed.");
        string proof = Goal(chat)["completionEvidence"].ToString(); int speech = model.Speech.Count;
        Approve(run.review, null); reviewer.Complete(true, "{}", "late-duplicate-result");
        yield return null; yield return null;
        Require(model.Speech.Count == speech && Goal(chat)["completionEvidence"].ToString() == proof && lamp.ReadState().on == (run.state == "on"),
            "Late duplicate goal/review callbacks cannot reenter or mutate a completed device task.");
    }

    private static IEnumerator RejectedAuthority(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model, SceneObjectLightSwitch lamp)
    {
        Run run = null; yield return Begin(chat, bridge, model, lamp, "unapproved-authority", OffRequest, "off", "set-object-state", value => run = value, holdApproval: true);
        run.review.Complete(true, new JObject { ["verdict"] = "rejected", ["requestAssessment"] = "requested", ["relation"] = "new", ["referentKind"] = "unbound", ["referentSourceId"] = "none",
            ["reason"] = "Controlled negative authorization result." }.ToString(), "controlled-unified-no-authority");
        yield return Wait(() => model.Speech.Count > run.speech || RoomPlans(model) > run.rooms + 1, 8, "rejected proposal settles without search");
        Require(model.SceneSearchRequests.Count == run.searches && model.Matches.Count == run.matches && lamp.ReadState().on &&
            bridge.Controller.OperationRevision == run.operation && Get(chat, "m_SpatialGoalRunner") == null &&
            (bool?)((JObject)Call(chat, "BuildPendingDeviceClarificationContext"))["ownerCurrent"] != true,
            "Rejecting original operation authority cannot enter search/controller or write even if the proposed first action was a direct write.");
        AddCase(run, "unapproved-goal-no-search-or-operation", chat);
    }

    private static IEnumerator SearchIncomplete(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model, SceneObjectLightSwitch lamp)
    {
        Run run = null; yield return Begin(chat, bridge, model, lamp, "search-incomplete", OffRequest, "off", "inspect-object", value => run = value);
        yield return Wait(() => model.SceneSearchRequests.Count > run.searches, 8, "search request before controlled incomplete");
        int serviced = run.searches;
        var historicalCaptures = new HashSet<string>();
        while (model.Speech.Count == run.speech)
        {
            yield return Wait(() => model.SceneSearchRequests.Count > serviced || model.Speech.Count > run.speech, 8,
                "search incomplete services bounded real historical reviews before the terminal");
            if (model.Speech.Count > run.speech) break;
            var request = model.SceneSearchRequests[serviced++]; var input = JObject.Parse(request.Input);
            bool historical = (string)input["imageRole"] == "historical";
            bool recovery = (bool?)input["recovery"]?["onlyNewView"] == true;
            Require(serviced - run.searches <= 4 && (serviced == run.searches + 1 ? !historical : historical || recovery) &&
                (string)input["task"]?["originalUserRequest"] == run.text && !string.IsNullOrEmpty(request.Image),
                "Giving up examines current RGB, at most two old images, and one useful-view recovery within the same budget.");
            if (historical)
                Require(!string.IsNullOrEmpty((string)input["historicalCaptureId"]) &&
                    historicalCaptures.Add((string)input["historicalCaptureId"]), "Each historical image is reviewed once with its own capture identity.");
            Export(run.name + "-declined-view-" + (serviced - run.searches), request);
            if (recovery) request.Complete(false, "", "controlled-recovery-provider-unavailable");
            else request.Complete(true, "{\"choice\":\"done\",\"selectionRole\":\"exploration\",\"referenceId\":\"\",\"reason\":\"This actual image does not establish a useful hint for the complete requested target.\"}", "controlled-no-target");
        }
        var search = (JObject)Call(chat, "BuildSceneSearchContext");
        Require(model.Speech.Count == run.speech + 1 &&
            (search["reviewedHistoricalCaptureIds"] as JArray)?.Values<string>().OrderBy(value => value)
                .SequenceEqual(historicalCaptures.OrderBy(value => value)) == true,
            "All declined actual historical images have reviewed receipts before one honest incomplete terminal.");
        Require(lamp.ReadState().on && (string)Goal(chat)["status"] == "blocked" && Goal(chat)["completionEvidence"]?.Type != JTokenType.Object &&
            RoomPlans(model) == run.rooms + 1 && model.SceneSearchRequests.Count == serviced && model.Matches.Count == run.matches &&
            (string)search["reason"] == "search-stopped-without-target-or-complete-survey",
            "Internal search failure blocks execution rather than asking the user to clarify; no write or completion proof is invented.");
        var result = (JObject)Call(chat, "BuildSpatialGoalExecutionFacts");
        Require((bool?)result["goal"]?["searchSummary"]?["searchAttempted"] == true &&
            (int?)result["goal"]?["searchSummary"]?["latestSearchDecisions"] == serviced - run.searches &&
            (int?)result["goal"]?["searchSummary"]?["latestSearchMotionSteps"] == 0 &&
            (string)result["resultReportRequirement"]?["expectedReport"] == "incomplete",
            "Actual current-image examination is reported as attempted search without claiming movement, absence or unstarted work.");
        AddCase(run, "unconfirmed-search-blocks-without-false-clarification-or-write", chat);
    }

    private static IEnumerator NonDeviceCandidateRecovery(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, SceneObjectLightSwitch lamp)
    {
        Run run = null;
        yield return Begin(chat, bridge, model, lamp, "matched-non-device-recovery", OffRequest, "off", "inspect-object", value => run = value);
        yield return Wait(() => model.SceneSearchRequests.Count == run.searches + 1, 8, "actual current catalog for adversarial candidate selection");
        var request = model.SceneSearchRequests.Last(); var input = JObject.Parse(request.Input);
        var observation = chat.SceneVisionCapture.LatestObservation;
        Require(observation.CaptureId == (string)input["currentCaptureId"], "The wrong candidate is selected only from the actual current image.");
        JObject wrong = null;
        foreach (var item in ((JArray)input["visibleCatalog"]["current"]["candidates"]).OfType<JObject>())
            if ((string)item["entityId"] != lamp.EntityId &&
                chat.SceneVisionCapture.TryFindObservedObject(observation, (string)item["entityId"], out var observed, out _) &&
                observed.VisibleConfirmed && observed.Root != null && observed.Root.GetComponent<SceneObjectLightSwitch>() == null)
            { wrong = item; break; }
        Require(wrong != null, "The actual image provides a non-device opaque candidate, without finding a hidden object by name.");
        string wrongEntity = (string)wrong["entityId"];
        var choice = new JObject { ["choice"] = "current-candidate", ["selectionRole"] = "target", ["referenceId"] = wrong["referenceId"]?.DeepClone(),
            ["reason"] = "Controlled adversarial semantic selection: the visible non-device is incorrectly selected as the target." };
        run.choices.Add(choice.DeepClone()); request.Complete(true, choice.ToString(), "controlled-wrong-visible-candidate");
        yield return Wait(() => model.Matches.Count == run.matches + 1 || model.Speech.Count > run.speech, 8, "fresh independent review of the wrong visible candidate");
        Require(model.Speech.Count == run.speech && model.Matches.Count == run.matches + 1, "Selection alone cannot finish or write the device task.");
        CompleteMatch(model.Matches.Last(), wrongEntity, run);
        yield return Wait(() => model.SceneSearchRequests.Count == run.searches + 2 || model.Speech.Count > run.speech, 8,
            "missing device control yields another bounded search choice");
        var search = (JObject)Call(chat, "BuildSceneSearchContext");
        Require(model.Speech.Count == run.speech && model.SceneSearchRequests.Count == run.searches + 2 &&
            (search["receipts"] as JArray)?.OfType<JObject>().Any(receipt =>
                (string)receipt["reason"] == "search-selected-object-has-no-registered-control") == true &&
            string.IsNullOrEmpty((string)Goal(chat)["boundEntityId"]) && lamp.ReadState().on,
            "A false semantic match on a non-device keeps the original target unbound, records a specific reason and continues instead of empty-reason termination.");
        yield return Drive(chat, bridge, model, lamp, run, consumedSearches: 1, consumedMatches: 1);
        Require((string)Goal(chat)["boundEntityId"] == lamp.EntityId && !lamp.ReadState().on,
            "Recovering from a false candidate can complete only the actual requested registered device with fresh same-entity proof.");
        yield return Audit(chat, model, lamp, run);
        AddCase(run, "false-non-device-match-excluded-before-same-target-completion", chat);
    }

    private static IEnumerator Interrupted(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model,
        SceneObjectLightSwitch lamp, string boundary, bool holdMatcher)
    {
        Run run = null; yield return Begin(chat, bridge, model, lamp, "interrupt-" + boundary, OffRequest, "off", "inspect-object", value => run = value);
        yield return Wait(() => model.SceneSearchRequests.Count > run.searches, 8, "held unified search before " + boundary);
        var held = model.SceneSearchRequests.Last(); string reply = SearchChoice(held, run, lamp.EntityId);
        if (holdMatcher)
        {
            held.Complete(true, reply, "controlled-before-matcher-interruption");
            yield return Wait(() => model.Matches.Count > run.matches, 8, "held independent matcher before replacement");
            held = model.Matches.Last();
        }
        var environment = bridge.environmentRoot;
        int speech = model.Speech.Count;
        try
        {
            if (boundary == "new-user") Call(chat, "RecordAcceptedMotionUserTurn", "先停下，不要再关灯。");
            else if (boundary == "new-generation") Call(chat, "BeginFormalResponseGeneration");
            else if (boundary == "application-pause") Call(chat, "OnApplicationPause", true);
            else if (boundary == "eyes-closed") { Set(chat, "m_AgentEyesOpen", false); Call(chat, "ResetSceneVision", "eyes-closed", true); }
            else if (boundary == "room-rebound") bridge.environmentRoot = null;
            else
            {
                const string next = "先停下，我现在只想聊天。";
                int updates = model.DeviceGoalReviews.Count;
                Call(chat, "RecordAcceptedMotionUserTurn", next);
                Call(chat, "StartStreaming", next, false, null, false, false, false, null, false, chat.SceneVisionCapture.LatestObservation, true);
                yield return Wait(() => model.DeviceGoalReviews.Count == updates + 1, 8, "replacement independently reviews the unfinished old goal");
                var update = model.DeviceGoalReviews.Last(); var updateInput = JObject.Parse(update.Input);
                Require((string)updateInput["mode"] == "goal-user-update" && (string)updateInput["latestUserText"] == next &&
                    (string)updateInput["existingGoal"]?["goalId"] == (string)Goal(chat)["goalId"],
                    "The replacement's own review sees the exact latest request and existing logical goal.");
                update.Complete(true, new JObject { ["verdict"] = "approved", ["relation"] = "cancel", ["responseMode"] = "answer",
                    ["referentGoalId"] = (string)updateInput["existingGoal"]["goalId"], ["referentEntityId"] = "",
                    ["reason"] = "Controlled latest human asks to stop the unfinished task and only chat." }.ToString(), "controlled-replacement-goal-cancel");
                yield return Wait(() => RoomPlans(model) == run.rooms + 2, 8, "reviewed cancellation leaves the newest user's own planner pending");
                var replacementInput = JObject.Parse(model.Decisions.Last(request => request.Kind == "room").Input);
                Require((string)replacementInput["currentUserText"] == next && (bool?)replacementInput["currentRequestOnly"] == true &&
                    (string)Goal(chat)["status"] == "cancelled" &&
                    (bool?)((JObject)Get(chat, "m_SpatialGoalLatestReview"))["cancellationApplied"] == true,
                    "The new planner has current-request scope after actual old-goal cancellation.");
            }
            yield return Wait(() => Get(chat, "m_SpatialGoalRunner") == null && (bool?)((JObject)Call(chat, "BuildSceneSearchContext"))["active"] != true,
                3, "parent and search child cancel on " + boundary);
            int operations = bridge.Controller.OperationRevision, searches = model.SceneSearchRequests.Count, matches = model.Matches.Count, rooms = RoomPlans(model);
            int generation = (int)Get(chat, "m_FormalResponseGeneration"); string facts = chat.SceneActionContextJson;
            if (holdMatcher) { CompleteMatch(held, lamp.EntityId, run); CompleteMatch(held, lamp.EntityId, run); }
            else { held.Complete(true, reply, "late-unified-search"); held.Complete(true, reply, "duplicate-unified-search"); }
            yield return null; yield return null; yield return null;
            Require(lamp.ReadState().on && model.Speech.Count == speech && model.SceneSearchRequests.Count == searches && model.Matches.Count == matches &&
                RoomPlans(model) == rooms && bridge.Controller.OperationRevision == operations && chat.SceneActionContextJson == facts &&
                (int)Get(chat, "m_FormalResponseGeneration") == generation,
                "Cancelled child callback cannot write, move, speak, replan or steal the replacement generation: " + boundary);
            if (boundary == "direct-stream-replacement") Require(generation > run.generation && rooms == run.rooms + 2 && (bool)Get(chat, "m_FormalResponseInFlight"),
                "The replacement user input keeps its own pending planner after the old independent matcher returns.");
            AddCase(run, "late-child-rejected-" + boundary, chat);
        }
        finally
        {
            bridge.environmentRoot = environment; Set(chat, "m_AgentEyesOpen", true);
            if (boundary == "application-pause") Call(chat, "OnApplicationPause", false);
            Call(chat, "ClearScheduledAgentWake");
        }
    }

    private static IEnumerator LatestHumanPreface(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, SceneObjectLightSwitch lamp, bool native)
    {
        const string original = "请把那盏小灯关掉。";
        string latest = native ? "你先向右转身120度看看。" : "你先转头向左看看。";
        string latestQuote = native ? "向右转身120度" : "转头向左看看";
        string intent = native ? "turn-in-place" : "orient-view";
        yield return Prepare(chat, bridge, lamp, original, "off");
        var draft = new RoomTaskProtocol.Decision { intent = "none", origin = "user", evidence = original,
            deviceGoal = new RoomTaskProtocol.DeviceGoalProposal { operation = "set", requestQuote = original,
                targetDescription = "那盏小灯", desiredState = "off" } };
        Call(chat, "RememberPendingDeviceClarification", draft, original, new RoomTaskProtocol.DeviceGoalReview {
            verdict = "uncertain", requestAssessment = "target-unclear", reason = "Controlled unresolved original target before actual latest instruction.",
            relation = "new", referentKind = "unbound", referentSourceId = "none" }, (int)Get(chat, "m_FormalResponseGeneration"));
        string pendingId = (string)((JObject)Call(chat, "BuildPendingDeviceClarificationContext"))["pendingId"];
        Require(!string.IsNullOrEmpty(pendingId), "An unresolved original operation precedes the newest human instruction.");
        Call(chat, "RecordAcceptedMotionUserTurn", latest); Call(chat, "ResetWorkReview", true);
        SceneVisionObservation source = null;
        Call(chat, "RequestSceneObservation", "unified-latest-user-source", new Action<SceneVisionObservation>(value => source = value));
        yield return Wait(() => source?.HasImage == true, 8, "latest-user actual input image");
        var run = new Run { name = "latest-human-observation-preface", text = original, state = "off", source = source,
            rooms = RoomPlans(model), searches = model.SceneSearchRequests.Count, matches = model.Matches.Count, speech = model.Speech.Count,
            operation = bridge.Controller.OperationRevision, root = bridge.avatar.transform.position, started = Time.realtimeSinceStartup };
        int reviews = model.DeviceGoalReviews.Count;
        Call(chat, "StartStreaming", latest, false, null, false, false, false, null, false, source, true);
        Set(chat, "m_AgentRoundInFlight", true); run.generation = (int)Get(chat, "m_FormalResponseGeneration");
        yield return Wait(() => model.DeviceGoalReviews.Count == reviews + 1, 8, "actual combined pending clarification review");
        run.review = model.DeviceGoalReviews.Last();
        run.review.Complete(true, new JObject { ["verdict"] = "approved", ["relation"] = "clarify", ["pendingClarificationId"] = pendingId,
            ["referentKind"] = "unbound", ["referentSourceId"] = "none", ["reason"] = "Controlled user preserves the original off request and now asks for an observation first." }.ToString(), "controlled-unified-latest-human");
        yield return Wait(() => RoomPlans(model) == run.rooms + 1, 8, "one current-human interpretation before unified handoff");
        run.initial = model.Decisions.Last(request => request.Kind == "room"); var input = JObject.Parse(run.initial.Input);
        var current = chat.SceneVisionCapture.LatestObservation; Quaternion body = bridge.avatar.transform.rotation;
        Require((string)input["currentUserText"] == original && (string)input["latestUserText"] == latest &&
            (bool?)input["decisionContractFacts"]?["deviceGoalAlreadyReviewed"] == true &&
            (bool?)input["userObservationBeforeDeviceTask"] == true && current.CaptureId != source.CaptureId,
            "The latest human instruction is interpreted once using a new post-approval frame while the original off objective remains distinct.");
        var decision = new JObject { ["intent"] = intent, ["origin"] = "user", ["evidence"] = latestQuote,
            ["viewChange"] = new JObject { ["captureId"] = current.CaptureId, ["yaw"] = native ? 120 : -30, ["pitch"] = 0 } };
        Require(!RoomTaskProtocol.TryDecision(decision.ToString(), original, false, out _) &&
            RoomTaskProtocol.TryReviewedGoalStepDecision(decision.ToString(), original, latest, out var parsed, out _) &&
            (bool)Call(chat, "IsReviewedLatestUserObservation", parsed, latest),
            "The current-user preface accepts a genuine short quote within the newest human instruction.");
        var copiedOld = (JObject)decision.DeepClone(); copiedOld["evidence"] = original;
        Require(RoomTaskProtocol.TryReviewedGoalStepDecision(copiedOld.ToString(), original, latest, out var oldOnly, out _) &&
            !(bool)Call(chat, "IsReviewedLatestUserObservation", oldOnly, latest),
            "General reviewed-goal parsing may cite original context, but old-only evidence cannot authorize a current-user preface.");
        Export(run.name + "-initial", run.initial);
        run.initial.Complete(true, decision.ToString(), "controlled-unified-human-observation-preface");
        yield return Wait(() => model.SceneSearchRequests.Count > run.searches || model.Speech.Count > run.speech, native ? 95 : 25,
            "actual human observation precedes the unified search child");
        Observe(chat, run); var fact = JObject.Parse(chat.SceneActionContextJson)["lastAction"] as JObject;
        Require(fact != null && (string)fact["intent"] == intent && (string)fact["phase"] == "observed" &&
            (double?)fact["actualViewChangeDegrees"] > 10 && (string)fact["resultCaptureId"] != current.CaptureId &&
            (native ? Quaternion.Angle(body, bridge.avatar.transform.rotation) > 60f && bridge.Controller.OperationRevision > run.operation :
                Quaternion.Angle(body, bridge.avatar.transform.rotation) < .1f && bridge.Controller.OperationRevision == run.operation),
            "The exact new human head/body observation is physically measured with new RGB before searching.");
        Require(model.SceneSearchRequests.Count == run.searches + 1 && model.Speech.Count == run.speech &&
            model.Matches.Count == run.matches && RoomPlans(model) == run.rooms + 1 && lamp.ReadState().on &&
            (string)Goal(chat)["originalRequest"] == original && (string)Goal(chat)["latestUserText"] == latest &&
            (string)Goal(chat)["desiredState"] == "off" && (bool?)Goal(chat)["completionEvidenceValid"] != true &&
            (string)Runner(chat, run)["phase"] == "searching",
            "After the one human preface, the same unified owner starts search without another RoomTask model, premature device write or completion claim.");
        AddCase(run, native ? "approved-pending-latest-human-body-turn-before-unified-task" : "approved-pending-latest-human-head-turn-before-unified-task", chat);
        LastReport["latestHumanPrefaceScope"] = native ? "Actual requested right body turn is measured before the unified search begins." :
            "Actual requested left head turn is measured before the unified search begins; the body remains stationary.";
        Call(chat, "CancelSpatialGoalRunner", "latest-human-preface-fixture-complete");
    }

    private static IEnumerator DialogueContinuation(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, SceneObjectLightSwitch lamp)
    {
        foreach (string boundary in new[] { "work-reset", "vision-reset", "blocked", "unrelated-act-no-resume", "answer-only" })
        {
            string unrelated = boundary == "answer-only" ? "顺便问一句，你喜欢什么颜色？" : "先唱一小段歌。";
            Run run = null;
            yield return Begin(chat, bridge, model, lamp, "dialogue-continuation-" + boundary,
                OffRequest, "off", "inspect-object", value => run = value);
            yield return Wait(() => model.SceneSearchRequests.Count == run.searches + 1, 8, "original task search before unrelated dialogue");
            var oldSearch = model.SceneSearchRequests.Last(); string oldReply = SearchChoice(oldSearch, run, lamp.EntityId);
            string goalId = (string)Goal(chat)["goalId"];
            if (boundary == "blocked") Call(chat, "BlockSpatialGoal", "controlled-blocked-before-dialogue");
            Call(chat, "RecordAcceptedMotionUserTurn", unrelated);
            yield return Wait(() => Get(chat, "m_SpatialGoalRunner") == null, 3, "new human turn pauses the old task owner");
            var relation = new RoomTaskProtocol.DeviceGoalReview { verdict = "approved", relation = "unrelated", responseMode = boundary == "answer-only" ? "answer" : "act",
                referentGoalId = "", referentEntityId = "", reason = "Controlled independent relation preserves the old operation behind unrelated dialogue." };
            var args = new object[] { relation, unrelated, false, null };
            Require((bool)Call(chat, "TryApplySpatialGoalUserUpdate", args) && !(bool)args[2] &&
                (string)Goal(chat)["goalId"] == goalId && (bool?)Goal(chat)["executionCurrent"] == false,
                "The real approved-unrelated handler pauses this same logical objective without executing it.");
            Require(!(bool)Get(Get(chat, "m_SpatialDeviceGoal"), "resumeAfterDialogue"),
                "An unrelated action or question cannot authorize resuming the old goal, including when no typed request interpretation exists.");
            // This fixture starts at the existing dialogue-complete boundary. It
            // releases only conversation gates, never a scene result or arrival.
            Set(chat, "m_FormalResponseInFlight", false); Set(chat, "m_AgentRoundInFlight", false);
            Set(chat, "<IsAISpeaking>k__BackingField", false); Set(chat, "m_WorkReviewOpen", false);
            Call(chat, "ClearScheduledAgentWake");
            if (boundary == "work-reset") Call(chat, "ResetWorkReview", true);
            else if (boundary == "vision-reset") Call(chat, "ResetSceneVision", "unified-continuation-new-vision-owner", false);
            int generation = (int)Get(chat, "m_FormalResponseGeneration"), rooms = RoomPlans(model), searches = model.SceneSearchRequests.Count;
            int matches = model.Matches.Count, speech = model.Speech.Count, operation = bridge.Controller.OperationRevision;
            bool resumed = (bool)Call(chat, "TryResumeSpatialGoalAfterDialogue");
            Require(!resumed && (int)Get(chat, "m_FormalResponseGeneration") == generation &&
                Get(chat, "m_SpatialGoalRunner") == null && RoomPlans(model) == rooms && model.SceneSearchRequests.Count == searches,
                "Unrelated dialogue completion never grants continuation authority, with or without work/vision invalidation: " + boundary);
            oldSearch.Complete(true, oldReply, "late-pre-dialogue-search-choice");
            yield return null; yield return null;
            Require(lamp.ReadState().on && model.Matches.Count == matches && model.Speech.Count == speech &&
                bridge.Controller.OperationRevision == operation && RoomPlans(model) == rooms && model.SceneSearchRequests.Count == searches,
                "The pre-dialogue child never adopts resumed or invalidated authority: " + boundary);
            AddCase(run, "unrelated-dialogue-continuation-" + boundary, chat);
        }
        LastReport["dialogueContinuationScope"] = "Real goal registration and task interruption, production approved-unrelated handler and dialogue-complete entry. Every unrelated action/question remains paused without continuation authority. Conversation gates are released at that boundary; this fixture does not simulate unrelated TTS playback. AnswerThenResumePlayback separately covers explicitly reviewed continue + answer-then-resume with actual muted PCM and valid resumption.";
    }

    private static IEnumerator AnswerThenResumePlayback(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, SceneObjectLightSwitch lamp, bool interrupt, bool emptyRecovery = false)
    {
        Run run = null;
        yield return Begin(chat, bridge, model, lamp, "answer-playback-" + (emptyRecovery ? "empty-recovery" : interrupt ? "interrupt" : "resume"),
            OffRequest, "off", "inspect-object", value => run = value);
        yield return Wait(() => model.SceneSearchRequests.Count == run.searches + 1, 8, "original authorized search before answer request");
        var oldSearch = model.SceneSearchRequests.Last(); string oldReply = SearchChoice(oldSearch, run, lamp.EntityId);
        string goalId = (string)Goal(chat)["goalId"];
        const string latest = "刚才的任务进展怎样？请先回答，然后继续完成。";
        var settings = (ChatSetting)Get(chat, "m_ChatSettings"); var previousTts = settings.m_TextToSpeech;
        var audio = (AudioSource)Get(chat, "m_AudioSource"); float volume = audio.volume;
        var host = new GameObject("Unified answer playback fixture"); var tts = host.AddComponent<UnifiedDeviceAnswerPlaybackTTS>();
        settings.m_TextToSpeech = tts; audio.volume = 0;
        try
        {
            int updates = model.DeviceGoalReviews.Count, speech = model.Speech.Count, rooms = RoomPlans(model);
            Call(chat, "RecordAcceptedMotionUserTurn", latest); Set(chat, "m_LastUserMsg", latest); Set(chat, "m_LastUserEvidence", latest);
            Call(chat, "MarkUserTurnAwaitingReply");
            SceneVisionObservation current = null;
            Call(chat, "RequestSceneObservation", "answer-then-resume-user", new Action<SceneVisionObservation>(value => current = value));
            yield return Wait(() => current?.HasImage == true, 8, "fresh user-question frame");
            Call(chat, "StartStreaming", latest, false, null, false, false, false, null, false, current, true);
            yield return Wait(() => model.DeviceGoalReviews.Count == updates + 1, 8, "actual question-and-resume semantic review");
            var update = model.DeviceGoalReviews.Last(); Export(run.name + "-update", update);
            update.Complete(true, new JObject { ["verdict"] = "approved", ["relation"] = "continue",
                ["responseMode"] = "answer-then-resume", ["referentGoalId"] = goalId, ["referentEntityId"] = "",
                ["reason"] = "The current user explicitly requires an answer first and then continuation of this same task." }.ToString(), "controlled-answer-before-resume");
            yield return Wait(() => RoomPlans(model) == rooms + 1, 8, "one RoomTask decision for answering the current question");
            model.Decisions.Last(request => request.Kind == "room").Complete(true,
                "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}", "controlled-answer-needs-no-action");
            yield return Wait(() => model.Speech.Count == speech + 1, 8, "actual formal answer stream");
            var answer = model.Speech.Last(); int searches = model.SceneSearchRequests.Count, matches = model.Matches.Count;
            int operation = bridge.Controller.OperationRevision;
            Require(Get(chat, "m_SpatialGoalRunner") == null && !(bool)Call(chat, "TryResumeSpatialGoalAfterDialogue") && lamp.ReadState().on,
                "The answer obligation holds the task before any speech has completed.");
            if (!interrupt && !emptyRecovery)
            {
                const string filler = "嗯，让我确认一下。";
                Set(chat, "m_LatencyFillerPlayed", true); Set(chat, "m_LatencyFillerText", filler);
                Set(chat, "m_LatencyFillerStartedAt", Time.realtimeSinceStartup - .2f); Set(chat, "m_LatencyFillerDuration", .1f);
                Call(chat, "CommitPlayedLatencyFiller");
                Require(Get(chat, "m_AssistantHeardText").ToString().EndsWith(filler, StringComparison.Ordinal) &&
                    !(bool)Get(Get(chat, "m_ReviewedGoalAnswerObligation"), "delivered") &&
                    !(bool)Call(chat, "TryResumeSpatialGoalAfterDialogue"),
                    "The real filler accounting path records a heard opener without satisfying the still-undelivered answer.");
                LastReport["answerFillerScope"] = "Controlled completed-filler timing invokes production CommitPlayedLatencyFiller; this is a filler-accounting boundary test, not real filler playback. The subsequent complete answer uses actual muted PCM playback and must resume despite that heard prefix.";
                Save();
            }
            string text = "当前还没确认目标，任务尚未完成。";
            answer.SpeechCallback(new SpeechText(text, "zh", actionDependency: SpeechActionDependency.Independent));
            int reviews = model.Decisions.Count(request => request.Kind == "room-review"); answer.SpeechCompleteCallback(text);
            yield return Wait(() => model.Decisions.Count(request => request.Kind == "room-review") == reviews + 1, 8, "actual answer consistency review");
            model.Decisions.Last(request => request.Kind == "room-review").Complete(true,
                "{\"verdict\":\"consistent\",\"factScope\":\"scene-action\",\"resultReport\":\"not-applicable\",\"reason\":\"The answer accurately states the incomplete current task.\"}", "controlled-answer-reviewed");
            yield return Wait(() => tts.Started && chat.IsVoiceOutputPlaying, 8, "production direct TTS player starts real muted PCM");
            // Play() can become observable one frame before the production advancement gate
            // marks IsAISpeaking. Both the physical playback and the later speaking state
            // must independently prevent task restoration.
            AssertAnswerPlaybackBlocksResume(chat, bridge, model, lamp, tts, audio, run.name,
                "play-issued", searches, matches, operation);
            yield return Wait(() => chat.IsVoiceOutputPlaying && chat.IsAISpeaking &&
                (bool)Get(chat, "m_RealFirstAudioStarted") && audio.timeSamples > 0, 8,
                "production advancement gate observes actual PCM samples before marking the answer as speaking");
            AssertAnswerPlaybackBlocksResume(chat, bridge, model, lamp, tts, audio, run.name,
                "samples-advanced", searches, matches, operation);
            if (emptyRecovery)
            {
                int oldGeneration = (int)Get(chat, "m_FormalResponseGeneration"), oldTurn = CurrentTurn(chat),
                    oldEpoch = (int)Get(chat, "m_WorkEpoch"), oldVision = (int)Get(chat, "m_SceneVisionEpoch"),
                    oldUsers = model.m_DataList.Count(item => item.role == "user"), goalReviews = model.DeviceGoalReviews.Count;
                var recovery = new JObject { ["oldGeneration"] = oldGeneration, ["userTurn"] = oldTurn,
                    ["workEpoch"] = oldEpoch, ["oldVisionEpoch"] = oldVision, ["oldCaptureId"] = current.CaptureId };
                LastReport["answerEmptyRecovery"] = recovery; Save();
                // Actual VAD/empty-transcript entry. No new accepted user turn or
                // synthetic answer receipt is created by this fixture.
                chat.NotifyUserStartedSpeaking();
                Require((string)Get(chat, "m_InterruptedUserQuestion") == latest && !chat.IsVoiceOutputPlaying,
                    "Speech-start cancels the held answer and preserves its still-unanswered real user request.");
                Call(chat, "DealingTextCallback", "", false);
                yield return Wait(() => RoomPlans(model) == rooms + 2 || model.SceneSearchRequests.Count != searches, 8,
                    "empty ASR restores the answer obligation through a fresh current-user planner");
                int recoveredGeneration = (int)Get(chat, "m_FormalResponseGeneration");
                var recoveredPlan = model.Decisions.Last(request => request.Kind == "room");
                var recoveredInput = JObject.Parse(recoveredPlan.Input);
                var recoveredView = (SceneVisionObservation)Call(chat, "GetFormalSceneObservation", recoveredGeneration);
                var obligation = Get(chat, "m_ReviewedGoalAnswerObligation");
                recovery["generation"] = recoveredGeneration; recovery["visionEpoch"] = (int)Get(chat, "m_SceneVisionEpoch");
                recovery["captureId"] = recoveredView?.CaptureId; recovery["roomPlans"] = RoomPlans(model);
                recovery["runnerPresent"] = Get(chat, "m_SpatialGoalRunner") != null;
                recovery["searchCount"] = model.SceneSearchRequests.Count; Save();
                Require(RoomPlans(model) == rooms + 2 && Get(chat, "m_SpatialGoalRunner") == null && model.SceneSearchRequests.Count == searches,
                    "Empty ASR restores a fresh answer planner rather than binding or resuming the suspended device task.");
                Require(recoveredGeneration != oldGeneration && (int)Get(chat, "m_SceneVisionEpoch") != oldVision &&
                    recoveredView?.HasImage == true && recoveredView.CaptureId != current.CaptureId && recoveredPlan.Image == recoveredView.ImageDataUrl,
                    "Same-turn answer recovery uses a new generation, vision epoch and actual fresh RGB.");
                Require(CurrentTurn(chat) == oldTurn && (int)Get(chat, "m_WorkEpoch") == oldEpoch &&
                    model.DeviceGoalReviews.Count == goalReviews && model.m_DataList.Count(item => item.role == "user") == oldUsers &&
                    (bool?)recoveredInput["answerRequired"] == true && (bool?)recoveredInput["currentRequestOnly"] == true &&
                    obligation != null && !(bool)Get(obligation, "delivered") && (int)Get(obligation, "generation") == recoveredGeneration,
                    "The same human/work owner retains an undelivered answer obligation without duplicate user history or new device authority.");
                tts.DeliverLateCompletion(); oldSearch.Complete(true, oldReply, "late-search-after-empty-asr");
                answer.SpeechCallback(new SpeechText(text, "zh", actionDependency: SpeechActionDependency.Independent));
                answer.SpeechCompleteCallback(text); yield return null; yield return null;
                Require(!(bool)Get(obligation, "delivered") && !(bool)Call(chat, "TryResumeSpatialGoalAfterDialogue") &&
                    Get(chat, "m_SpatialGoalRunner") == null && model.SceneSearchRequests.Count == searches &&
                    model.Matches.Count == matches && bridge.Controller.OperationRevision == operation && lamp.ReadState().on &&
                    model.Speech.Count == speech + 1 && !chat.IsVoiceOutputPlaying,
                    "Late old-generation TTS, speech and search callbacks cannot satisfy the recovered answer or release any action.");
                Export(run.name + "-recovered-planner", recoveredPlan);
                recoveredPlan.Complete(true, "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}", "controlled-empty-asr-answer-only");
                yield return Wait(() => model.Speech.Count == speech + 2, 8, "recovered formal answer stream");
                var recoveredAnswer = model.Speech.Last();
                recoveredAnswer.SpeechCallback(new SpeechText(text, "zh", actionDependency: SpeechActionDependency.Independent));
                reviews = model.Decisions.Count(request => request.Kind == "room-review"); recoveredAnswer.SpeechCompleteCallback(text);
                yield return Wait(() => model.Decisions.Count(request => request.Kind == "room-review") == reviews + 1, 8,
                    "recovered answer requires its own public consistency review");
                model.Decisions.Last(request => request.Kind == "room-review").Complete(true,
                    "{\"verdict\":\"consistent\",\"factScope\":\"scene-action\",\"resultReport\":\"not-applicable\",\"reason\":\"The recovered answer accurately states the unfinished task.\"}", "controlled-recovered-answer-reviewed");
                yield return Wait(() => chat.IsVoiceOutputPlaying && chat.IsAISpeaking &&
                    (bool)Get(chat, "m_RealFirstAudioStarted") && audio.timeSamples > 0, 8, "recovered answer reaches actual PCM advancement");
                AssertAnswerPlaybackBlocksResume(chat, bridge, model, lamp, tts, audio, run.name,
                    "samples-advanced", searches, matches, operation);
            }
            if (interrupt)
            {
                chat.NotifyUserStartedSpeaking();
                Call(chat, "RecordAcceptedMotionUserTurn", "先别动，换个话题。" );
                chat.PrepareUserTurn("先别动，换个话题。", "先别动，换个话题。");
                tts.DeliverLateCompletion(); oldSearch.Complete(true, oldReply, "late-search-during-interrupted-answer");
                yield return null; yield return null;
                Require(!(bool)Call(chat, "TryResumeSpatialGoalAfterDialogue") && Get(chat, "m_SpatialGoalRunner") == null &&
                    model.SceneSearchRequests.Count == searches && model.Matches.Count == matches &&
                    bridge.Controller.OperationRevision == operation && lamp.ReadState().on,
                    "Speech interruption and a new accepted user turn revoke old answer completion and search callbacks.");
            }
            else
            {
                tts.ReleasePlayback = true;
                yield return Wait(() => tts.Completed && !chat.IsVoiceOutputPlaying && !chat.IsAISpeaking &&
                    (model.SceneSearchRequests.Count > searches ||
                        !(bool)Get(chat, "m_FormalResponseInFlight") && !(bool)Get(chat, "m_AgentRoundInFlight")), 8,
                    "real PCM naturally drains and production dialogue completion releases its gates");
                Call(chat, "ClearScheduledAgentWake");
                Require((bool)Get(Get(chat, "m_ReviewedGoalAnswerObligation"), "delivered"),
                    "Only the complete audited answer's actual playback satisfies its receipt, including a preceding committed filler.");
                Require(Get(chat, "m_SpatialGoalRunner") != null || (bool)Call(chat, "TryResumeSpatialGoalAfterDialogue"),
                    "Only completed playback and the same user owner permit restoration, either through the scheduled tick or the same production entry.");
                yield return Wait(() => model.SceneSearchRequests.Count == searches + 1, 8, "resumed task captures a new image and starts its child");
                Require(RoomPlans(model) == rooms + (emptyRecovery ? 2 : 1) && (string)Goal(chat)["goalId"] == goalId &&
                    (string)JObject.Parse(model.SceneSearchRequests.Last().Input)["currentCaptureId"] != current.CaptureId && lamp.ReadState().on,
                    "After answering, the same task resumes with fresh pixels and no extra RoomTask substep model or premature device write.");
                Call(chat, "CancelSpatialGoalRunner", "answer-playback-fixture-complete");
            }
            run.generation = (int)Get(chat, "m_FormalResponseGeneration");
            AddCase(run, emptyRecovery ? "empty-asr-restores-answer-before-task-resume" :
                interrupt ? "answer-audio-interruption-revokes-resume" : "answer-audio-completes-before-task-resume", chat);
            LastReport["answerPlaybackScope"] = "Real StartStreaming, relation parser, RoomTask none, formal speech review and production direct playback coroutine. Controlled TTS emits muted PCM on the actual AudioSource, held until release and naturally drained; no external synthesis service or human-audibility claim.";
        }
        finally
        {
            tts.CancelStreaming(); settings.m_TextToSpeech = previousTts; audio.volume = volume;
            UnityEngine.Object.Destroy(host);
        }
    }

    private static void AssertAnswerPlaybackBlocksResume(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, SceneObjectLightSwitch lamp,
        UnifiedDeviceAnswerPlaybackTTS tts, AudioSource audio, string name, string stage,
        int searches, int matches, int operation)
    {
        var evidence = new JObject { ["case"] = name, ["stage"] = stage,
            ["generation"] = (int)Get(chat, "m_FormalResponseGeneration"),
            ["isAISpeaking"] = chat.IsAISpeaking, ["voiceOutputPlaying"] = chat.IsVoiceOutputPlaying,
            ["realFirstAudioStarted"] = (bool)Get(chat, "m_RealFirstAudioStarted"),
            ["audioSourcePlaying"] = audio.isPlaying, ["timeSamples"] = audio.timeSamples,
            ["clipId"] = audio.clip != null ? audio.clip.GetInstanceID() : 0,
            ["ttsStarted"] = tts.Started, ["ttsCompleted"] = tts.Completed,
            ["releasePlayback"] = tts.ReleasePlayback,
            ["formalResponseInFlight"] = (bool)Get(chat, "m_FormalResponseInFlight"),
            ["agentRoundInFlight"] = (bool)Get(chat, "m_AgentRoundInFlight"),
            ["runnerPresentBeforeResumeAttempt"] = Get(chat, "m_SpatialGoalRunner") != null,
            ["expectedSearchCount"] = searches, ["expectedMatchCount"] = matches,
            ["expectedOperationRevision"] = operation };
        bool resumed = (bool)Call(chat, "TryResumeSpatialGoalAfterDialogue");
        evidence["resumeAccepted"] = resumed;
        evidence["runnerPresentAfterResumeAttempt"] = Get(chat, "m_SpatialGoalRunner") != null;
        evidence["searchCount"] = model.SceneSearchRequests.Count;
        evidence["matchCount"] = model.Matches.Count;
        evidence["operationRevision"] = bridge.Controller.OperationRevision;
        evidence["lampOn"] = lamp.ReadState().on;
        var history = LastReport["answerPlaybackEvidence"] as JArray;
        if (history == null) LastReport["answerPlaybackEvidence"] = history = new JArray();
        history.Add(evidence); Save();
        Require((bool)evidence["voiceOutputPlaying"] && (bool)evidence["audioSourcePlaying"],
            stage + ": real answer audio is playing on the production AudioSource.");
        Require(!resumed, stage + ": physical answer playback rejects task restoration.");
        Require(!(bool)evidence["runnerPresentBeforeResumeAttempt"] && !(bool)evidence["runnerPresentAfterResumeAttempt"],
            stage + ": no task runner exists before or after the premature restoration attempt.");
        Require((int)evidence["searchCount"] == searches, stage + ": playback dispatches no search.");
        Require((int)evidence["matchCount"] == matches, stage + ": playback dispatches no object matching.");
        Require((int)evidence["operationRevision"] == operation, stage + ": playback performs no controller action.");
        Require((bool)evidence["lampOn"], stage + ": playback performs no device write.");
        if (stage == "samples-advanced")
            Require((bool)evidence["isAISpeaking"] && (bool)evidence["realFirstAudioStarted"] && (int)evidence["timeSamples"] > 0,
                "The production speaking flags follow observed real PCM advancement.");
    }

    private static IEnumerator InitialHumanPreface(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, SceneObjectLightSwitch lamp)
    {
        const string original = "先转头向左看看，再把那盏小灯关掉。";
        Run run = null;
        yield return Begin(chat, bridge, model, lamp, "initial-human-head-preface", original, "off", "orient-view", value => run = value, holdApproval: true);
        Quaternion body = bridge.avatar.transform.rotation;
        Require((int)Get(chat, "m_MotionSequence") == run.sequence && !(bool)Get(chat, "m_SceneActionActive") &&
            model.SceneSearchRequests.Count == run.searches && model.Matches.Count == run.matches && lamp.ReadState().on,
            "An initial compound view/device proposal cannot turn, search or write before independent operation approval.");
        Approve(run.review, null);
        yield return Wait(() => model.SceneSearchRequests.Count > run.searches || model.Speech.Count > run.speech, 25,
            "approved initial compound request executes its real head observation before unified search");
        Observe(chat, run); var fact = JObject.Parse(chat.SceneActionContextJson)["lastAction"] as JObject;
        Require((string)Runner(chat, run)["controller"] == "unified-device-task" && (string)Runner(chat, run)["phase"] == "searching" &&
            fact != null && (string)fact["intent"] == "orient-view" && (string)fact["phase"] == "observed" &&
            (double?)fact["actualViewChangeDegrees"] > 10 && (string)fact["resultCaptureId"] != run.source.CaptureId &&
            Quaternion.Angle(body, bridge.avatar.transform.rotation) < .1f && bridge.Controller.OperationRevision == run.operation,
            "The first approved current-user short quote causes actual head motion and fresh RGB before the device search.");
        Require(RoomPlans(model) == run.rooms + 1 && model.SceneSearchRequests.Count == run.searches + 1 &&
            model.Matches.Count == run.matches && model.Speech.Count == run.speech && lamp.ReadState().on &&
            (string)Goal(chat)["originalRequest"] == original && (bool?)Goal(chat)["completionEvidenceValid"] != true,
            "The initial preface has one interpretation and one search handoff, with no intermediate room model, write or premature completion.");
        AddCase(run, "initial-approved-compound-head-observation-before-unified-search", chat);
        Call(chat, "CancelSpatialGoalRunner", "initial-human-preface-fixture-complete");
    }

    private static IEnumerator TurnAway(ChatSample chat, ArdyRoomDialogueBridge bridge, SceneObjectLightSwitch lamp)
    {
        Call(chat, "CancelSceneAction", "unified-history-test-turn");
        yield return Wait(() => !bridge.ReservesBody, 5, "prior body released before real history return case");
        const string turnText = "向右转120度。"; Call(chat, "RecordAcceptedMotionUserTurn", turnText);
        Call(chat, "BeginFormalResponseGeneration"); SceneVisionObservation source = null;
        Call(chat, "RequestSceneObservation", "unified-history-turn-source", new Action<SceneVisionObservation>(value => source = value));
        yield return Wait(() => source?.HasImage == true, 8, "actual turn source image");
        int generation = (int)Get(chat, "m_FormalResponseGeneration"); Call(chat, "BindFormalSceneObservation", source, generation);
        var wire = new JObject { ["intent"] = "turn-in-place", ["origin"] = "user", ["evidence"] = turnText,
            ["viewChange"] = new JObject { ["captureId"] = source.CaptureId, ["yaw"] = 120, ["pitch"] = 0 } };
        Require(RoomTaskProtocol.TryDecision(wire.ToString(), turnText, false, out var decision), "Actual fixture turn passes the production parser.");
        bool completed = false; Quaternion before = bridge.avatar.transform.rotation;
        Call(chat, "BeginSceneAction", decision, source, generation, new Action(() => completed = true));
        yield return Wait(() => completed && !bridge.ReservesBody, 95, "actual native body turn away from historical target");
        Require(Quaternion.Angle(before, bridge.avatar.transform.rotation) > 60f &&
            !chat.SceneVisionCapture.TryFindObservedObject(chat.SceneVisionCapture.LatestObservation, lamp.EntityId, out _, out _),
            "The historical return starts after measured native rotation with the lamp absent from actual current pixels.");
    }

    private static void Observe(ChatSample chat, Run run)
    {
        var fact = JObject.Parse(chat.SceneActionContextJson)["lastAction"] as JObject;
        if (fact != null && run.factKeys.Add((string)fact["actionId"] + ":" + (string)fact["phase"])) run.facts.Add(fact.DeepClone());
        string capture = chat.SceneVisionCapture.LatestObservation?.CaptureId; if (capture != null) run.captureIds.Add(capture);
    }
    private static void AddCase(Run run, string name, ChatSample chat)
    {
        ((JArray)LastReport["cases"]).Add(new JObject { ["name"] = name, ["generation"] = run.generation,
            ["elapsedSeconds"] = Time.realtimeSinceStartup - run.started, ["goal"] = Goal(chat), ["runner"] = Runner(chat, run),
            ["facts"] = run.facts, ["searchChoices"] = run.choices, ["independentMatches"] = run.matchEvidence, ["captureIds"] = new JArray(run.captureIds) }); Save();
    }
    private static void Export(string name, NevaSceneVisionIntegrationModel.Request request)
    {
        File.WriteAllText(Path.Combine(directory, name + ".input.json"), request.Input + "\n");
        if (!string.IsNullOrEmpty(request.Wire)) File.WriteAllText(Path.Combine(directory, name + ".request.json"), request.Wire + "\n");
        if (!string.IsNullOrEmpty(request.Image)) File.WriteAllText(Path.Combine(directory, name + ".image-data-url.txt"), request.Image);
    }
    private static int RoomPlans(NevaSceneVisionIntegrationModel model) => model.Decisions.Count(request => request.Kind == "room");
    private static int CurrentTurn(ChatSample chat) { object ledger = Get(chat, "m_ActionConstraints"); return (int)ledger.GetType().GetProperty("CurrentUserTurn", Flags).GetValue(ledger); }
    private static JObject Goal(ChatSample chat) => (JObject)Call(chat, "BuildSpatialGoalContext");
    private static JObject Runner(ChatSample chat, Run run) => Call(chat, "BuildSpatialGoalRunnerContext", run.generation) as JObject ?? new JObject();
    private static void Save() { LastReport["checks"] = checks; File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString() + "\n"); }
    private static IEnumerator Wait(Func<bool> ready, float seconds, string message)
    { float until = Time.realtimeSinceStartup + seconds; while (!ready()) { if (Time.realtimeSinceStartup > until) throw new TimeoutException(message); yield return null; } }
    private static string Argument(string key, string fallback) { var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, key); return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback; }
    private static void Require(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    private static object Call(object target, string name, params object[] args) => NevaSceneVisionIntegrationRegression.Call(target, name, args);
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
}

// Deterministic synthesis substitute; the production ChatSample streaming
// player owns its callbacks and the actual AudioSource playback lifecycle.
public sealed class UnifiedDeviceAnswerPlaybackTTS : TTS
{
    public bool Started, Completed, ReleasePlayback;
    private Coroutine routine;
    private AudioSource output;
    private AudioClip clip;
    private Action<bool, string, float> completion;
    private string spokenText;
    public override bool SupportsStreamingPlayback => true;
    public override void SpeakStreamingWithPlaybackGate(SpeechText speech, AudioSource target, Action<string> onStarted,
        Action<bool, string, float> onCompleted, Func<StreamingPlaybackPermission> gate)
    {
        CancelStreaming(); Started = Completed = ReleasePlayback = false;
        completion = onCompleted; spokenText = speech.Text; output = target;
        routine = StartCoroutine(Play(speech, onStarted, gate));
    }
    private IEnumerator Play(SpeechText speech, Action<string> onStarted, Func<StreamingPlaybackPermission> gate)
    {
        while (true)
        {
            var permission = gate == null ? StreamingPlaybackPermission.Play : gate();
            if (permission == StreamingPlaybackPermission.Cancel) yield break;
            if (permission == StreamingPlaybackPermission.Play) break;
            yield return null;
        }
        clip = AudioClip.Create("Unified device answer PCM", 12000, 1, 24000, false);
        output.clip = clip; output.loop = true; output.Play(); Started = true; onStarted?.Invoke(speech.Text);
        while (!ReleasePlayback && output != null && output.clip == clip && output.isPlaying) yield return null;
        if (output == null || output.clip != clip) yield break;
        output.loop = false;
        while (output != null && output.clip == clip && output.isPlaying) yield return null;
        Completed = true; routine = null; completion?.Invoke(true, speech.Text, .5f);
    }
    public void DeliverLateCompletion() => completion?.Invoke(true, spokenText, .5f);
    public override void CancelStreaming()
    {
        if (routine != null) StopCoroutine(routine); routine = null;
        if (output != null && output.clip == clip) { output.Stop(); output.loop = false; output.clip = null; }
        if (clip != null) UnityEngine.Object.Destroy(clip); clip = null;
    }
}
