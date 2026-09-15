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
using UnityEngine;

/// <summary>Additional scene-action cases in the existing actual-room graphics/Chat integration fixture.</summary>
public static class SceneActionIntegrationRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    public static JObject LastReport { get; private set; }
    private static string directory;
    private static int checks;

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        directory = Path.Combine(output, "scene-actions"); Directory.CreateDirectory(directory); checks = 0;
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(), ["targets"] = new JArray(),
            ["scope"] = "Real imported room RGB/depth, calibrated real head/eyes and production autonomy→room→scene-action→formal dispatch. The in-process model deliberately selects two distinct supported visible renderer surfaces from fresh pixel samples. A controlled-fault case additionally replaces a real physical look's completion continuation with a deliberate exception to verify owner cleanup. No model understanding, native motion, audio or autonomous desire quality claim.",
            ["nativeRequests"] = 0, ["modelHttpRequests"] = 0 };
        LastReport["roomProtocolChecks"] = RoomTaskProtocolRegression.RunChecks();
        Save();
        Require(chat != null && bridge != null && bridge.IsConnected && model != null, "Existing actual-room fixture is connected.");
        var tracking = bridge.avatar.GetComponent<NevaEyeTracking>();
        Require(tracking != null && tracking.IsInitialized && tracking.isActiveAndEnabled,
            "The actual avatar must receive legitimate edit-mode eye calibration before Play; this test never invents a runtime neutral pose.");
        int originalOperation = bridge.Controller.OperationRevision;
        Vector3 originalRoot = bridge.avatar.transform.position;
        Quaternion originalRotation = bridge.avatar.transform.rotation;
        var seenSurfaces = new HashSet<string>();

        for (int index = 0; index < 2; index++)
        {
            ResetRound(chat);
            yield return Wait(() => IdleAttention(chat), "previous attention release");
            int probes = model.Probes.Count, decisions = model.Decisions.Count, speech = model.Speech.Count;
            string intention = index == 0 ? "我想安静看清眼前的一处可见表面。" : "我想换一处眼前的可见表面仔细观察。";
            Call(chat, "BeginAutonomyIntentProbe", "scene-action-controlled-" + index);
            yield return Wait(() => model.Probes.Count == probes + 1, "actual autonomous source capture");
            var probe = model.Probes.Last();
            var observation = chat.SceneVisionCapture.LatestObservation;
            Require(observation.HasImage && probe.Image == observation.ImageDataUrl && probe.Input.Contains(observation.CaptureId),
                "Autonomy receives actual matching source pixels and capture identity.");
            SceneGroundedTarget target = ChooseSurface(chat.SceneVisionCapture, observation, tracking, seenSurfaces);
            Require(target != null, "Fresh actual room image supplies another distinct supported surface inside the physical head-turn range.");
            seenSurfaces.Add(target.SurfaceId);
            probe.Complete(new JObject { ["proceed"] = true, ["work_status"] = "none", ["intent"] = intention,
                ["novelty"] = "这轮刚取得的新视野", ["wait_seconds"] = 45 }.ToString());
            yield return Wait(() => model.Decisions.Count == decisions + 1, "autonomous intention reaches actual room planner");
            var roomRequest = model.Decisions.Last();
            var input = JObject.Parse(roomRequest.Input);
            Require((bool?)input["autonomous"] == true && (string)input["currentUserText"] == "" &&
                (string)input["autonomousIntention"] == intention, "Actual preprobe intention is carried to room planning without replaying a user command.");
            Require(roomRequest.Image == observation.ImageDataUrl && roomRequest.Input.Contains(observation.CaptureId),
                "Planner uses the exact autonomous observation.");
            string prefix = "target-" + (index + 1);
            var exported = ExportCapture(prefix + "-source", observation);
            File.WriteAllText(Path.Combine(directory, prefix + "-room-request.json"), roomRequest.Wire + "\n");
            File.WriteAllText(Path.Combine(directory, prefix + "-room-input.json"), roomRequest.Input + "\n");
            string decisionJson = Decision(observation.CaptureId, target.ImagePoint, "autonomous", intention, "可见表面" + (index + 1));
            File.WriteAllText(Path.Combine(directory, prefix + "-controlled-decision.json"), decisionJson + "\n");
            roomRequest.Complete(true, decisionJson, "controlled-visible-surface");
            Require((bool)Get(chat, "m_SceneActionActive") && (string)Fact(chat)["phase"] == "looking",
                "Production room planning starts a physical look rather than claiming completed observation.");
            var attention = (NevaSceneAttention)Get(chat, "m_SceneAttention");
            string actionId = attention.OwnerId;
            Require(!string.IsNullOrEmpty(actionId) && tracking.AttentionOwnerId == actionId, "Head and eyes share the actual registered action owner.");
            SceneVisionObservation confirmed = null;
            float maximumHeadDegrees = 0f;
            Quaternion beforeFrame = observation.CameraRotation;
            float deadline = Time.realtimeSinceStartup + 9f;
            while (model.Speech.Count == speech)
            {
                if (Time.realtimeSinceStartup > deadline) Fail("Look action did not reach formal dispatch: " + Fact(chat));
                maximumHeadDegrees = Mathf.Max(maximumHeadDegrees, Quaternion.Angle(beforeFrame, tracking.HeadFrameRotation));
                var latest = chat.SceneVisionCapture.LatestObservation;
                if (latest.HasImage && latest.CaptureId != observation.CaptureId && (string)Fact(chat)["confirmedCaptureId"] == latest.CaptureId)
                    confirmed = latest;
                yield return null;
            }
            Require(model.Speech.Count == speech + 1 && confirmed != null, "Look produces one formal request after a genuinely new confirmed capture.");
            var fact = Fact(chat); var formal = model.Speech.Last();
            Require((string)fact["phase"] == "observed" && (bool?)fact["active"] == false && (bool?)fact["reached"] == false &&
                !(bool)Get(chat, "m_SceneActionActive"), "Completed look reports observed/inactive, never arrival.");
            Require((string)fact["actionId"] == actionId && (string)fact["surfaceId"] == target.SurfaceId &&
                (string)fact["captureId"] == observation.CaptureId && (string)fact["confirmedCaptureId"] == confirmed.CaptureId,
                "Source, confirmed surface and actual action retain their identity through the chain.");
            var heldAttention = (NevaSceneAttention)Get(chat, "m_SceneAttention");
            Require(maximumHeadDegrees > 3f && heldAttention.IsSettled && heldAttention.OwnerId == actionId && tracking.AttentionOwnerId == actionId,
                "Actual calibrated head visibly turned and retains the observed view for the following decision.");
            Require(formal.Image == confirmed.ImageDataUrl && formal.Context.Contains(confirmed.CaptureId) &&
                WireImages(formal.Wire).SequenceEqual(new[] { confirmed.ImageDataUrl }), "Formal wire uses only the newly focused capture and its metadata.");
            Require(!bridge.ReservesBody && !bridge.Controller.IsBusy && bridge.Controller.OperationRevision == originalOperation &&
                Vector3.Distance(originalRoot, bridge.avatar.transform.position) < .0001f && Quaternion.Angle(originalRotation, bridge.avatar.transform.rotation) < .01f,
                "Looking never obtains native locomotion ownership or moves the character root.");
            exported["confirmed"] = ExportCapture(prefix + "-confirmed", confirmed);
            exported["roomWire"] = prefix + "-room-request.json"; exported["roomInput"] = prefix + "-room-input.json";
            exported["controlledDecision"] = prefix + "-controlled-decision.json"; exported["surfaceId"] = target.SurfaceId;
            exported["imagePoint"] = JObject.Parse(JsonUtility.ToJson(target.ImagePoint));
            exported["maximumHeadTurnDegrees"] = maximumHeadDegrees; exported["actualResult"] = fact;
            ((JArray)LastReport["targets"]).Add(exported);
            File.WriteAllText(Path.Combine(directory, prefix + "-formal-request.json"), formal.Wire + "\n");
            Case("autonomous-intention-to-physical-look-distinct-surface-" + (index + 1));
        }
        Require(seenSurfaces.Count == 2, "The two successful looks concern different measured renderer surfaces.");

        foreach (string mode in new[] { "forged-capture", "scene-actions-disabled", "closed-eyes" })
        {
            ResetRound(chat); yield return Wait(() => IdleAttention(chat), "negative case prior return");
            SceneVisionObservation observation = null;
            yield return BeginUserPlanner(chat, model, value => observation = value);
            var pending = model.Decisions.Last(); int speech = model.Speech.Count;
            SceneGroundedTarget target = ChooseSurface(chat.SceneVisionCapture, observation, tracking, null);
            Require(target != null, "Negative case has a valid pixel target before applying its invalidation.");
            if (mode == "scene-actions-disabled") Set(chat, "m_EnableSceneActions", false);
            if (mode == "closed-eyes") Set(chat, "m_AgentEyesOpen", false);
            pending.Complete(true, Decision(mode == "forged-capture" ? observation.CaptureId + "-forged" : observation.CaptureId,
                target.ImagePoint, "user", "请看这里", "可见表面"), "controlled-" + mode);
            yield return Wait(() => model.Speech.Count == speech + 1, "rejected action formal acknowledgement");
            Require(!(bool)Get(chat, "m_SceneActionActive") && IdleAttention(chat) && (string)Fact(chat)["phase"] == "rejected",
                mode + " cannot start attention or remain active.");
            Require((string)Fact(chat)["reason"] == (mode == "forged-capture" ? "visual-reference-not-current" : "scene-action-disabled"),
                "Invalidation is reported by its concrete gate.");
            Require(bridge.Controller.OperationRevision == originalOperation && !bridge.ReservesBody,
                mode + " cannot start locomotion.");
            if (mode == "closed-eyes")
                Require(string.IsNullOrEmpty(model.Speech.Last().Image) && !WireImages(model.Speech.Last().Wire).Any(),
                    "Closed-eye rejection does not attach historical image pixels.");
            Case(mode);
        }

        ResetRound(chat); yield return Wait(() => IdleAttention(chat), "cancel case prior return");
        SceneVisionObservation cancellingObservation = null;
        yield return BeginUserPlanner(chat, model, value => cancellingObservation = value);
        var cancellingDecision = model.Decisions.Last(); int cancellingSpeech = model.Speech.Count;
        var cancellingTarget = ChooseSurface(chat.SceneVisionCapture, cancellingObservation, tracking, null);
        Require(cancellingTarget != null, "Cancellation case has a current supported surface.");
        string cancellingJson = Decision(cancellingObservation.CaptureId, cancellingTarget.ImagePoint, "user", "请看这里", "可见表面");
        cancellingDecision.Complete(true, cancellingJson, "controlled-before-cancel");
        var turning = (NevaSceneAttention)Get(chat, "m_SceneAttention");
        Require(turning != null && turning.State == "turning" && (bool)Get(chat, "m_SceneActionActive"), "Cancellation is applied during actual turning preparation.");
        yield return Wait(() => turning.CurrentHeadAngles.magnitude > 2f || turning.State != "turning", "visible partial turn before interruption");
        Require(turning.State == "turning" && turning.CurrentHeadAngles.magnitude > 2f,
            "The head has actually started turning, and has not settled, before cancellation.");
        string cancelledAction = turning.OwnerId;
        Call(chat, "InvalidateFormalResponse", "scene-action-regression-cancel-turn");
        // A duplicate delayed transport completion remains callable; production identity fences must reject it.
        cancellingDecision.Complete(true, cancellingJson, "controlled-late-duplicate");
        yield return Wait(() => IdleAttention(chat) && !(bool)Get(chat, "m_SceneActionActive"), "cancelled attention release");
        for (int i = 0; i < 4; i++) yield return null;
        Require(model.Speech.Count == cancellingSpeech && !bridge.ReservesBody && bridge.Controller.OperationRevision == originalOperation,
            "Cancelled turning and late old decision cannot dispatch formal speech or native movement.");
        Require((int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1 &&
            (string)Fact(chat)["actionId"] == cancelledAction && (string)Fact(chat)["phase"] == "cancelled", "Cancellation closes the same pending action identity.");
        Case("cancel-turning-and-late-decision-cannot-revive");

        // Fault injection uses a real autonomous source frame, planner round and physical head
        // action. Only the final preparation continuation is replaced by a deliberately throwing
        // callback. The old transport completion stays callable to verify the generation fence.
        ResetRound(chat); yield return Wait(() => IdleAttention(chat), "controlled fault prior return");
        ((Dictionary<string, float>)Get(chat, "m_SceneTargetRevisitAt")).Clear();
        const string faultMarker = "controlled-fault: scene-action-completion-continuation";
        int faultProbeCount = model.Probes.Count, faultDecisionCount = model.Decisions.Count;
        int faultSpeechCount = model.Speech.Count, faultCompletions = 0, expectedWarnings = 0, unexpectedFaultLogs = 0;
        bool callbackSawObserved = false, callbackSawIdleHead = false, callbackFormalBusy = false, callbackAgentBusy = false;
        Call(chat, "BeginAutonomyIntentProbe", "scene-action-controlled-fault");
        yield return Wait(() => model.Probes.Count == faultProbeCount + 1, "controlled fault actual autonomous observation");
        var faultObservation = chat.SceneVisionCapture.LatestObservation;
        var faultTarget = ChooseSurface(chat.SceneVisionCapture, faultObservation, tracking, null);
        Require(faultTarget != null, "Controlled fault has a fresh, physically visible target.");
        const string faultIntention = "我想安静看清眼前的一处可见表面。";
        model.Probes.Last().Complete(new JObject { ["proceed"] = true, ["work_status"] = "none", ["intent"] = faultIntention,
            ["novelty"] = "这轮刚取得的新视野", ["wait_seconds"] = 45 }.ToString());
        yield return Wait(() => model.Decisions.Count == faultDecisionCount + 1, "controlled fault actual planner owner");
        var oldFaultDecision = model.Decisions.Last();
        string faultDecisionJson = Decision(faultObservation.CaptureId, faultTarget.ImagePoint, "autonomous", faultIntention, "可见表面");
        Require(RoomTaskProtocol.TryDecision(faultDecisionJson, "", true, out var faultDecision), "Controlled fault decision passes the real closed-schema parser.");
        int faultGeneration = (int)Get(chat, "m_FormalResponseGeneration");
        Require((bool)Get(chat, "m_FormalResponseInFlight") && (bool)Get(chat, "m_AgentRoundInFlight"),
            "Controlled fault starts with a real busy formal and autonomous round, not pre-cleared flags.");
        string faultActionId = null;
        float faultMaximumHeadDegrees = 0f;
        void FaultLog(string message, string stackTrace, LogType type)
        {
            if (message == null || !message.Contains(faultMarker)) return;
            if (type == LogType.Warning && message.StartsWith("[Vision/Action] preparation failed; exact response owner released:", StringComparison.Ordinal))
                expectedWarnings++;
            else if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert) unexpectedFaultLogs++;
        }
        Application.logMessageReceived += FaultLog;
        try
        {
            Action throwingCompletion = () => {
                faultCompletions++;
                var before = Fact(chat);
                callbackSawObserved = (string)before["phase"] == "observed" && (string)before["actionId"] == faultActionId &&
                    !string.IsNullOrEmpty((string)before["confirmedCaptureId"]) && (bool?)before["reached"] == false;
                callbackSawIdleHead = ((NevaSceneAttention)Get(chat, "m_SceneAttention")).IsSettled && tracking.AttentionOwnerId == faultActionId;
                callbackFormalBusy = (bool)Get(chat, "m_FormalResponseInFlight");
                callbackAgentBusy = (bool)Get(chat, "m_AgentRoundInFlight");
                throw new InvalidOperationException(faultMarker);
            };
            Call(chat, "BeginSceneAction", faultDecision, faultObservation, faultGeneration, throwingCompletion);
            var faultAttention = (NevaSceneAttention)Get(chat, "m_SceneAttention");
            faultActionId = faultAttention != null ? faultAttention.OwnerId : null;
            Require(!string.IsNullOrEmpty(faultActionId) && (bool)Get(chat, "m_SceneActionActive") &&
                (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == faultGeneration,
                "Controlled fault acquires the real registered head and pending preparation owner.");
            float faultDeadline = Time.realtimeSinceStartup + 10f;
            while ((string)Fact(chat)["phase"] != "failed")
            {
                if (Time.realtimeSinceStartup > faultDeadline) Fail("Controlled fault did not reach its guarded failure: " + Fact(chat));
                faultMaximumHeadDegrees = Mathf.Max(faultMaximumHeadDegrees,
                    Quaternion.Angle(faultObservation.CameraRotation, tracking.HeadFrameRotation));
                yield return null;
            }
            Require(faultCompletions == 1 && callbackSawObserved && callbackSawIdleHead && faultMaximumHeadDegrees > 3f,
                "The injected exception happens once, after real head motion, focused capture confirmation and retained head view.");
            Require(callbackFormalBusy && callbackAgentBusy, "The throwing continuation really owns busy reply and agent state.");
            Require((int)Get(chat, "m_FormalResponseGeneration") == faultGeneration + 1 &&
                (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1 && !(bool)Get(chat, "m_FormalResponseInFlight") &&
                !(bool)Get(chat, "m_AgentRoundInFlight") && (bool)Get(chat, "m_StreamComplete") && (bool)Get(chat, "m_TTSSenderDone"),
                "Caught completion fault invalidates the exact response generation and releases pending/formal/agent/stream/TTS state.");
            yield return Wait(() => IdleAttention(chat), "fault smoothly releases retained head view");
            Require(!(bool)Get(chat, "m_SceneActionActive") && IdleAttention(chat) && string.IsNullOrEmpty(tracking.AttentionOwnerId) &&
                (string)Fact(chat)["actionId"] == faultActionId && (string)Fact(chat)["reason"] == faultMarker && (bool?)Fact(chat)["reached"] == false,
                "Fault result belongs to that exact inactive action and never claims arrival.");
            oldFaultDecision.Complete(true, faultDecisionJson, "controlled-fault-late-old-decision");
            oldFaultDecision.Complete(true, faultDecisionJson, "controlled-fault-late-old-decision-duplicate");
            for (int i = 0; i < 4; i++) yield return null;
            Require(faultCompletions == 1 && model.Speech.Count == faultSpeechCount &&
                !(bool)Get(chat, "m_SceneActionActive") && IdleAttention(chat) && !(bool)Get(chat, "m_FormalResponseInFlight") &&
                !(bool)Get(chat, "m_AgentRoundInFlight") && (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1 &&
                (string)Fact(chat)["actionId"] == faultActionId && (string)Fact(chat)["phase"] == "failed",
                "Late and duplicate old planner callbacks cannot revive the failed owner, head action or reply.");
            Require(expectedWarnings == 1 && unexpectedFaultLogs == 0,
                "Controlled fault is one expected guarded warning, not an unhandled Unity exception/error.");
            Require(!bridge.ReservesBody && bridge.Controller.OperationRevision == originalOperation,
                "Controlled completion fault never submits native locomotion.");
            LastReport["controlledFault"] = new JObject { ["kind"] = "real-look-completion-continuation-throws",
                ["marker"] = faultMarker, ["sourceCaptureId"] = faultObservation.CaptureId, ["actionId"] = faultActionId,
                ["responseGenerationBefore"] = faultGeneration, ["responseGenerationAfter"] = (int)Get(chat, "m_FormalResponseGeneration"),
                ["completionCalls"] = faultCompletions, ["expectedGuardWarnings"] = expectedWarnings, ["unhandledFaultLogs"] = unexpectedFaultLogs,
                ["maximumHeadTurnDegrees"] = faultMaximumHeadDegrees, ["formalBusyAtThrow"] = callbackFormalBusy,
                ["agentBusyAtThrow"] = callbackAgentBusy, ["actualResult"] = Fact(chat), ["oldCallbacksRevivedOwner"] = false };
            Case("controlled-fault-real-look-completion-releases-owner-and-fences-old-callbacks");
        }
        finally { Application.logMessageReceived -= FaultLog; }

        // Exercise re-observation through the real room callback path. Invalid points are measured
        // against the current GPU buffer; no collider/material/coordinate substitution is introduced.
        LastReport["pixelReselection"] = new JArray();
        foreach (string mode in new[] { "recovered", "still-unreliable", "cancel-repair" })
        {
            ResetRound(chat); yield return Wait(() => IdleAttention(chat), "pixel reselect prior return: " + mode);
            SceneVisionObservation firstObservation = null;
            yield return BeginUserPlanner(chat, model, value => firstObservation = value);
            var originalDecision = model.Decisions.Last();
            int beforeRepairRequests = model.Decisions.Count, beforeRepairSpeech = model.Speech.Count;
            Quaternion beforeRepairHead = tracking.HeadFrameRotation;
            Require(firstObservation.HasSurfaceGrounding && chat.SceneVisionCapture.IsCurrentViewCompatible(firstObservation) &&
                ChooseSurface(chat.SceneVisionCapture, firstObservation, tracking, null) != null,
                "Reselection source is a fresh, normal room capture with independently resolvable geometry.");
            Require(TryChooseUnreliablePixel(chat.SceneVisionCapture, firstObservation, out Vector2 firstPoint, out string firstFailure),
                "The actual image must supply an in-bounds pixel whose surface grounding really fails.");
            string originalJson = Decision(firstObservation.CaptureId, firstPoint, "user", "请看这里", "待确认的可见区域");
            originalDecision.Complete(true, originalJson, "controlled-unreliable-pixel-" + mode);
            Require((string)Fact(chat)["phase"] == "rejected" && (bool?)Fact(chat)["canReobserveTarget"] == true &&
                (string)Fact(chat)["reason"] == firstFailure && (string)Fact(chat)["captureId"] == firstObservation.CaptureId,
                "First rejection records the actual pixel failure and the only eligible re-observation cause.");
            Require(model.Decisions.Count == beforeRepairRequests && model.Speech.Count == beforeRepairSpeech &&
                (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == (int)Get(chat, "m_FormalResponseGeneration") &&
                !(bool)Get(chat, "m_SceneActionActive") && IdleAttention(chat) && string.IsNullOrEmpty(tracking.AttentionOwnerId),
                "An unreliable pixel requests a real new capture before another model decision, with no physical action or premature reply.");
            originalDecision.Complete(true, originalJson, "controlled-old-pixel-while-capturing");
            originalDecision.Complete(true, originalJson, "controlled-old-pixel-while-capturing-duplicate");
            yield return Wait(() => model.Decisions.Count == beforeRepairRequests + 1, "one repair planner request: " + mode);
            var repairDecision = model.Decisions.Last();
            var repairObservation = chat.SceneVisionCapture.LatestObservation;
            JObject repairInput = JObject.Parse(repairDecision.Input);
            JObject correction = repairInput["visualCorrection"] as JObject;
            Require(repairObservation.HasImage && repairObservation.HasSurfaceGrounding &&
                repairObservation.CaptureId != firstObservation.CaptureId && repairObservation.Frame > firstObservation.Frame &&
                (string)repairInput["visualObservation"]?["captureId"] == repairObservation.CaptureId &&
                repairDecision.Image == repairObservation.ImageDataUrl &&
                WireImages(repairDecision.Wire).SequenceEqual(new[] { repairObservation.ImageDataUrl }),
                "The single repair request carries newly rendered pixels and the matching new capture identity.");
            Require(correction != null && (int?)correction["attempt"] == 1 && (int?)correction["maximumAttempts"] == 1 &&
                (string)correction["measuredFailure"] == firstFailure &&
                (string)correction["previousDecision"]?["intent"] == "look-at" &&
                (string)correction["previousDecision"]?["visualTarget"]?["captureId"] == firstObservation.CaptureId &&
                Mathf.Abs((float)correction["previousDecision"]["visualTarget"]["x"] - firstPoint.x) < .000001f &&
                Mathf.Abs((float)correction["previousDecision"]["visualTarget"]["y"] - firstPoint.y) < .000001f,
                "Repair input retains the actual previous decision, measured failure and explicit one-attempt limit.");
            Require(!bridge.ReservesBody && bridge.Controller.OperationRevision == originalOperation &&
                Vector3.Distance(originalRoot, bridge.avatar.transform.position) < .0001f &&
                Quaternion.Angle(beforeRepairHead, tracking.HeadFrameRotation) < .25f && IdleAttention(chat),
                "Initial rejection and repair planning do not move the root or turn the actual head.");
            JObject factBeforeOldCallbacks = Fact(chat);
            originalDecision.Complete(true, originalJson, "controlled-old-pixel-after-repair-request");
            originalDecision.Complete(true, originalJson, "controlled-old-pixel-after-repair-request-duplicate");
            for (int i = 0; i < 3; i++) yield return null;
            Require(model.Decisions.Count == beforeRepairRequests + 1 && model.Speech.Count == beforeRepairSpeech &&
                JToken.DeepEquals(Fact(chat), factBeforeOldCallbacks) && !(bool)Get(chat, "m_SceneActionActive") && IdleAttention(chat),
                "Once repair is pending, duplicate original callbacks cannot overwrite its facts, dispatch speech or consume its authority.");

            string prefix = "pixel-reselect-" + mode;
            var evidence = new JObject { ["mode"] = mode, ["source"] = ExportCapture(prefix + "-source", firstObservation),
                ["repair"] = ExportCapture(prefix + "-repair", repairObservation), ["measuredFailure"] = firstFailure,
                ["rejectedPixel"] = JObject.Parse(JsonUtility.ToJson(firstPoint)), ["visualCorrection"] = correction.DeepClone() };
            File.WriteAllText(Path.Combine(directory, prefix + "-room-input.json"), repairDecision.Input + "\n");
            if (mode == "recovered")
            {
                var correctedTarget = ChooseSurface(chat.SceneVisionCapture, repairObservation, tracking, null);
                Require(correctedTarget != null, "The model correction fixture deliberately chooses a genuinely valid point from the new capture.");
                repairDecision.Complete(true, Decision(repairObservation.CaptureId, correctedTarget.ImagePoint,
                    "user", "请看这里", "已重新选择的可见表面"), "controlled-corrected-pixel");
                Require((bool)Get(chat, "m_SceneActionActive") && (string)Fact(chat)["phase"] == "looking",
                    "Only the fresh corrected decision starts real physical attention.");
                float maximumCorrectedTurn = 0f, correctedDeadline = Time.realtimeSinceStartup + 10f;
                while (model.Speech.Count == beforeRepairSpeech)
                {
                    if (Time.realtimeSinceStartup > correctedDeadline) Fail("Corrected pixel failed to finish actual look: " + Fact(chat));
                    maximumCorrectedTurn = Mathf.Max(maximumCorrectedTurn, Quaternion.Angle(beforeRepairHead, tracking.HeadFrameRotation));
                    yield return null;
                }
                Require(model.Speech.Count == beforeRepairSpeech + 1 && model.Decisions.Count == beforeRepairRequests + 1 &&
                    (string)Fact(chat)["phase"] == "observed" && (bool?)Fact(chat)["reached"] == false &&
                    (string)Fact(chat)["captureId"] == repairObservation.CaptureId &&
                    (string)Fact(chat)["surfaceId"] == correctedTarget.SurfaceId &&
                    !string.IsNullOrEmpty((string)Fact(chat)["confirmedCaptureId"]) &&
                    maximumCorrectedTurn > 3f && ((NevaSceneAttention)Get(chat, "m_SceneAttention")).IsSettled && !(bool)Get(chat, "m_SceneActionActive"),
                    "One corrected model decision yields a confirmed real look, retained view and one reply, never an arrival or third planner request.");
                evidence["maximumHeadTurnDegrees"] = maximumCorrectedTurn;
            }
            else if (mode == "still-unreliable")
            {
                Require(TryChooseUnreliablePixel(chat.SceneVisionCapture, repairObservation, out Vector2 badAgain, out string failureAgain),
                    "Second rejection uses another actually ungroundable point in the fresh capture.");
                string rejectedAgain = Decision(repairObservation.CaptureId, badAgain, "user", "请看这里", "仍待确认的区域");
                repairDecision.Complete(true, rejectedAgain, "controlled-repair-still-unreliable");
                yield return Wait(() => model.Speech.Count == beforeRepairSpeech + 1, "bounded second rejection acknowledgement");
                repairDecision.Complete(true, rejectedAgain, "controlled-repair-second-rejection-duplicate");
                for (int i = 0; i < 4; i++) yield return null;
                Require(model.Decisions.Count == beforeRepairRequests + 1 && model.Speech.Count == beforeRepairSpeech + 1 &&
                    (string)Fact(chat)["phase"] == "rejected" && (string)Fact(chat)["reason"] == failureAgain &&
                    (string)Fact(chat)["captureId"] == repairObservation.CaptureId &&
                    !(bool)Get(chat, "m_SceneActionActive") && IdleAttention(chat) &&
                    (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1,
                    "A second unreliable pixel produces a final rejection and one reply, with no third model request, recapture owner or physical action.");
                evidence["secondMeasuredFailure"] = failureAgain;
            }
            else
            {
                var lateTarget = ChooseSurface(chat.SceneVisionCapture, repairObservation, tracking, null);
                Require(lateTarget != null, "Cancelled repair retains an otherwise valid late correction to test its lost authority.");
                string lateJson = Decision(repairObservation.CaptureId, lateTarget.ImagePoint, "user", "请看这里", "过期修正表面");
                int beforeCancelGeneration = (int)Get(chat, "m_FormalResponseGeneration");
                Call(chat, "InvalidateFormalResponse", "scene-action-regression-cancel-pixel-repair");
                JObject cancelledFacts = Fact(chat);
                repairDecision.Complete(true, lateJson, "controlled-late-repair-after-cancel");
                repairDecision.Complete(true, lateJson, "controlled-late-repair-after-cancel-duplicate");
                originalDecision.Complete(true, originalJson, "controlled-original-after-repair-cancel");
                for (int i = 0; i < 4; i++) yield return null;
                Require((int)Get(chat, "m_FormalResponseGeneration") > beforeCancelGeneration &&
                    model.Decisions.Count == beforeRepairRequests + 1 && model.Speech.Count == beforeRepairSpeech &&
                    (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1 && !(bool)Get(chat, "m_FormalResponseInFlight") &&
                    !(bool)Get(chat, "m_SceneActionActive") && IdleAttention(chat) && JToken.DeepEquals(Fact(chat), cancelledFacts),
                    "Cancelling during repair revokes valid late repair and original callbacks; neither can create speech, head motion or new planning.");
            }
            Require(!bridge.ReservesBody && bridge.Controller.OperationRevision == originalOperation &&
                Vector3.Distance(originalRoot, bridge.avatar.transform.position) < .0001f,
                "Pixel repair checks never call native locomotion or move the avatar root.");
            evidence["actualResult"] = Fact(chat); evidence["repairRequests"] = model.Decisions.Count - beforeRepairRequests;
            evidence["formalRequests"] = model.Speech.Count - beforeRepairSpeech;
            ((JArray)LastReport["pixelReselection"]).Add(evidence);
            Case("pixel-reselect-" + mode + "-one-repair-and-old-callback-fences");
        }
        Require(Vector3.Distance(originalRoot, bridge.avatar.transform.position) < .0001f && Quaternion.Angle(originalRotation, bridge.avatar.transform.rotation) < .01f,
            "Entire action integration leaves actual room avatar root unchanged.");
        ResetRound(chat);
        LastReport["status"] = "passed"; Save();
    }

    private static IEnumerator BeginUserPlanner(ChatSample chat, NevaSceneVisionIntegrationModel model, Action<SceneVisionObservation> ready)
    {
        Call(chat, "RecordAcceptedMotionUserTurn", "请看这里");
        SceneVisionObservation observation = null;
        Call(chat, "RequestSceneObservation", "scene-action-user-fixture", new Action<SceneVisionObservation>(value => observation = value));
        yield return Wait(() => observation != null, "user planner source capture");
        Require(observation.HasImage, "User planner receives actual source capture.");
        int decisions = model.Decisions.Count;
        Call(chat, "StartStreaming", "请看这里", false, null, false, false, false, null, false, observation, true);
        yield return Wait(() => model.Decisions.Count == decisions + 1, "actual user room planner");
        ready(observation);
    }

    private static SceneGroundedTarget ChooseSurface(NevaSceneVisionCapture source, SceneVisionObservation observation,
        NevaEyeTracking tracking, HashSet<string> excluded)
    {
        foreach (float x in new[] { .25f, .35f, .65f, .75f, .15f, .85f, .45f, .55f })
            foreach (float y in new[] { .4f, .3f, .5f, .6f, .65f, .75f, .8f })
            {
                if (!source.TryResolve(observation, new Vector2(x, y), out var target, out _) ||
                    excluded != null && excluded.Contains(target.SurfaceId)) continue;
                Vector3 offset = target.WorldPoint - tracking.EyeOrigin;
                Vector3 local = Quaternion.Inverse(tracking.HeadFrameRotation) * offset;
                float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                float pitch = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
                if (offset.magnitude > .3f && Mathf.Abs(yaw) < 42f && pitch > -24f && pitch < 20f &&
                    new Vector2(yaw, pitch).magnitude > 8f) return target;
            }
        return null;
    }

    private static bool TryChooseUnreliablePixel(NevaSceneVisionCapture source, SceneVisionObservation observation,
        out Vector2 pixel, out string reason)
    {
        pixel = default; reason = null;
        if (observation == null || !observation.HasImage || !observation.HasSurfaceGrounding || !source.IsCurrentViewCompatible(observation)) return false;
        // A bounded scan of the actual image, including ordinary geometry edges/holes. Every
        // candidate is within [0,1]; global missing/stale capture errors are not test fixtures.
        foreach (float y in new[] { .025f, .975f, .1f, .9f, .25f, .75f, .4f, .6f, .5f, .15f, .85f, .35f, .65f })
            foreach (float x in new[] { .025f, .975f, .1f, .9f, .25f, .75f, .4f, .6f, .5f, .15f, .85f, .35f, .65f })
            {
                var candidate = new Vector2(x, y);
                if (source.TryResolve(observation, candidate, out _, out string failure) || string.IsNullOrEmpty(failure)) continue;
                if (!source.IsCurrentViewCompatible(observation) || Time.realtimeSinceStartup - observation.CapturedAtRealtime > SceneVisionGrounding.MaximumObservationAge) return false;
                pixel = candidate; reason = failure; return true;
            }
        return false;
    }

    private static string Decision(string id, Vector2 point, string origin, string evidence, string label) => new JObject {
        ["intent"] = "look-at", ["origin"] = origin, ["evidence"] = evidence,
        ["visualTarget"] = new JObject { ["captureId"] = id, ["x"] = point.x, ["y"] = point.y, ["label"] = label }
    }.ToString();
    private static JObject Fact(ChatSample chat) => JObject.Parse(JsonUtility.ToJson(Get(chat, "m_SceneActionFact")));
    private static bool IdleAttention(ChatSample chat)
    { var attention = (NevaSceneAttention)Get(chat, "m_SceneAttention"); return attention == null || !attention.IsBusy; }
    private static void ResetRound(ChatSample chat)
    {
        Call(chat, "CancelAutonomyIntentProbe"); Call(chat, "InvalidateFormalResponse", "scene-action-fixture-next"); chat.StopAllCoroutines(); Call(chat, "ClearScheduledAgentWake");
        foreach (string field in new[] { "m_AudioSource", "m_HumStreamSecondaryAudioSource" })
        { var source = (AudioSource)Get(chat, field); if (source != null) { source.playOnAwake = false; source.Stop(); source.clip = null; } }
        Set(chat, "m_PendingTickCo", null); Set(chat, "m_AgentRoundInFlight", false); Set(chat, "m_FormalResponseInFlight", false);
        Set(chat, "m_AgentCurrentRoundIsTick", false); Set(chat, "m_UserSpeechActiveForAutonomy", false); Set(chat, "m_StreamComplete", true);
        Set(chat, "m_ConsecutiveAITurns", 0); Set(chat, "m_AgentRunning", true); Set(chat, "m_AgentEyesOpen", true);
        Set(chat, "m_EnableScreenVision", true); Set(chat, "m_EnableSceneActions", true);
        Set(chat, "m_WorkReviewOpen", false); Set(chat, "m_WorkNoProgress", false); Set(chat, "m_UserTurnAwaitingReplySince", -1f);
        Set(chat, "m_LastUserMsg", ""); Set(chat, "m_LastUserEvidence", ""); Set(chat, "m_AgentGracefulShutdownPending", false);
        Set(chat, "m_MonologueBackstopTurns", 40); Set(chat, "m_MaxConsecutiveAITurns", 8);
    }
    private static JObject ExportCapture(string prefix, SceneVisionObservation observation)
    {
        byte[] bytes = Convert.FromBase64String(observation.ImageDataUrl.Substring(observation.ImageDataUrl.IndexOf(',') + 1));
        File.WriteAllBytes(Path.Combine(directory, prefix + ".jpg"), bytes);
        File.WriteAllText(Path.Combine(directory, prefix + ".metadata.json"), observation.MetadataJson + "\n");
        string hash; using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        return new JObject { ["captureId"] = observation.CaptureId, ["image"] = prefix + ".jpg", ["metadata"] = prefix + ".metadata.json", ["imageSha256"] = hash };
    }
    private static IEnumerable<string> WireImages(string wire) => JObject.Parse(wire)["messages"].OfType<JObject>()
        .SelectMany(message => message["content"] is JArray parts ? parts.OfType<JObject>() : Enumerable.Empty<JObject>())
        .Where(part => (string)part["type"] == "image_url").Select(part => (string)part["image_url"]?["url"]);
    private static IEnumerator Wait(Func<bool> ready, string label)
    { float deadline = Time.realtimeSinceStartup + 8f; while (!ready()) { if (Time.realtimeSinceStartup > deadline) Fail("Timeout: " + label); yield return null; } }
    private static void Require(bool value, string message) { checks++; if (!value) Fail(message); }
    private static void Fail(string message)
    { LastReport["status"] = "failed"; LastReport["error"] = message; Save(); throw new InvalidOperationException(message); }
    private static void Case(string name) { ((JArray)LastReport["cases"]).Add(name); Save(); }
    private static void Save() { LastReport["checks"] = checks; File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString() + "\n"); }
    private static object Call(object target, string name, params object[] args) => NevaSceneVisionIntegrationRegression.Call(target, name, args);
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
}
