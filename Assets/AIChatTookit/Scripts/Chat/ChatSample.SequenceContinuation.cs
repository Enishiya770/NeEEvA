using System;
using System.Collections;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // Separate execution lease: submitting a successor never changes the device
    // action/report owner or its immutable completion proof.
    private sealed class SequenceBodyContinuation
    {
        public int generation, epoch, turn, vision, input, operation;
        public string step, request, previousAction;
        public JObject sequence, candidate, review;
        public float started;
        public LLM model;
        public ArdyRoomDialogueBridge bridge;
        public UnityEngine.Object avatar, environment;
        public Action report;
        public bool finished, transport;
    }
    private SequenceBodyContinuation m_SequenceBodyContinuation;
    private JObject m_SequenceBodySubmission;
    private readonly System.Collections.Generic.HashSet<string> m_SequenceBodyAttempts = new System.Collections.Generic.HashSet<string>();

    private bool SequenceContinuationCurrent(SequenceBodyContinuation lease) => lease != null &&
        ReferenceEquals(lease, m_SequenceBodyContinuation) && !lease.finished &&
        lease.generation == m_FormalResponseGeneration && lease.epoch == m_WorkEpoch &&
        lease.turn == m_ActionConstraints.CurrentUserTurn && lease.request == m_ActionConstraints.CurrentUserText &&
        lease.vision == m_SceneVisionEpoch && lease.input == m_InputAudioRevision &&
        isActiveAndEnabled && m_AgentRunning && !m_AgentGracefulShutdownPending &&
        !m_UserSpeechActiveForAutonomy && !m_SceneTimePaused && !m_SceneTimeSuspended && Time.timeScale > 0 && SceneEyesOpen &&
        RoomTaskSequenceCurrent() && lease.model == m_ChatSettings?.m_ChatModel && lease.model.isActiveAndEnabled &&
        lease.bridge != null && lease.bridge == GetComponent<ArdyRoomDialogueBridge>() && lease.bridge.IsConnected &&
        lease.bridge.avatar == lease.avatar && lease.bridge.environmentRoot == lease.environment;

    private bool SequenceContinuationBodyAvailable(SequenceBodyContinuation lease) =>
        SequenceContinuationCurrent(lease) && lease.bridge.Controller != null &&
        lease.bridge.Controller.OperationRevision == lease.operation && lease.bridge.ActionId == lease.previousAction &&
        !lease.bridge.ReservesBody && !lease.bridge.IsFollowing && !lease.bridge.Controller.IsBusy &&
        !SceneActionIsBusy && !HasWorkToolInFlight() && !SceneReviewFreeBodyBusy() && !IsSingingRuntimeActivityActive() &&
        m_PendingMotionRepair == null && m_ActiveMotionRepair == null;

    private bool TryStartRoomSequenceContinuation(int generation, Action report)
    {
        var state = ReadRoomTaskSequence();
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var model = m_ChatSettings?.m_ChatModel;
        var steps = state?["steps"] as JArray;
        var preparation = m_RoomSpeechPreparation;
        string blocked = generation != m_FormalResponseGeneration ? "generation-changed" :
            m_SequenceBodyContinuation != null ? "continuation-already-pending" : steps == null ? "no-current-sequence" :
            bridge?.Controller == null ? "no-body-controller" : model?.SupportsRoomPreparationMessages != true ? "review-unavailable" :
            preparation != null && (!preparation.consumed || preparation.ownsTransport) ? "speech-preparation-pending" :
            m_SceneVisionStopPreparation?.ownsTransport == true ? "stop-review-pending" :
            !m_WorkReviewOpen || m_WorkNoProgress ? "work-not-continuable" :
            steps.OfType<JObject>().Any(s => (string)s["status"] == "running" || (string)s["status"] == "failed") ? "unsettled-or-failed-step" : null;
        bool Deferred(string reason) {
            Debug.Log("[Work/SequenceContinuationDeferred] " + new JObject { ["generation"] = generation,
                ["workEpoch"] = m_WorkEpoch, ["userTurn"] = m_ActionConstraints.CurrentUserTurn, ["reason"] = reason,
                ["preparationPresent"] = preparation != null, ["preparationConsumed"] = preparation?.consumed,
                ["preparationCompleted"] = preparation?.completed, ["preparationOwnsTransport"] = preparation?.ownsTransport,
                ["bodyReserved"] = bridge != null && bridge.ReservesBody, ["scope"] = "No successor submitted; existing reporting and bounded work review remain available." }.ToString(Formatting.None), this);
            return false;
        }
        if (blocked != null) return Deferred(blocked);
        var ready = RoomTaskSequenceProtocol.Ready(steps).Values<string>().ToArray();
        var selected = steps.OfType<JObject>().FirstOrDefault(s => ready.Contains((string)s["id"]));
        // These kinds already denote the tracked user. Object/visual/device
        // steps retain their full semantic binding and current-view pipeline.
        if (selected == null || !new[] { "approach", "follow" }.Contains((string)selected["kind"]) ||
            !(selected["after"] is JArray predecessors) || predecessors.Count == 0) return Deferred("no-ready-dependent-user-body-step");
        string key = m_WorkEpoch + ":" + m_ActionConstraints.CurrentUserTurn + ":" + (string)selected["id"];
        if (m_SequenceBodyAttempts.Contains(key)) return Deferred("attempt-already-used");
        var lease = new SequenceBodyContinuation {
            generation = generation, epoch = m_WorkEpoch, turn = m_ActionConstraints.CurrentUserTurn,
            vision = m_SceneVisionEpoch, input = m_InputAudioRevision, operation = bridge.Controller.OperationRevision,
            previousAction = bridge.ActionId, step = (string)selected["id"], request = m_ActionConstraints.CurrentUserText,
            model = model, bridge = bridge, avatar = bridge.avatar, environment = bridge.environmentRoot,
            sequence = state, candidate = (JObject)selected.DeepClone(), report = report, started = Time.realtimeSinceStartup
        };
        m_SequenceBodyContinuation = lease;
        if (!SequenceContinuationBodyAvailable(lease)) { m_SequenceBodyContinuation = null; return Deferred("body-or-owner-unavailable"); }
        var summaries = new JArray(steps.OfType<JObject>().Select(s => {
            var summary = PickRoomFields(s, "id", "quote", "kind", "desiredState", "after", "status");
            var receipt = s["receipt"] as JObject;
            summary["receipt"] = receipt?["goal"] is JObject goal ?
                PickRoomFields(goal, "goalId", "status", "desiredState", "completionEvidenceValid") :
                PickRoomFields(receipt, "actionId", "phase", "status", "intent");
            return summary;
        }));
        var input = new JObject { ["roomControlReview"] = RoomControlReviewProtocol.Continuation,
            ["currentUserText"] = lease.request, ["selectedStep"] = PickRoomFields(selected, "id", "quote", "kind", "after"),
            ["resumedParentRequest"] = state["resumedFrom"] is JObject ? state["originalRequest"]?.DeepClone() : null,
            ["steps"] = summaries, ["priorResultSpeechCompleted"] = false,
            ["scope"] = "Statuses are engine-recorded completion receipts. Body is available now. This executor can only track the current user; independently determine the requested target from the complete utterance. No new visual/object decision or user confirmation was performed." };
        string wireInput = input.ToString(Formatting.None);
        if (wireInput.Length > 8000) { m_SequenceBodyContinuation = null; return Deferred("continuation-input-budget"); }
        m_SequenceBodyAttempts.Add(key);
        lease.transport = true;
        StartCoroutine(WatchSequenceContinuation(lease));
        try { model.PostRoomPreparationMessage(wireInput, (ok, json, status) => {
            if (!SequenceContinuationCurrent(lease)) return;
            lease.transport = false;
            if (json?.Length <= 4096) {
                try { lease.review = PickRoomFields(JObject.Parse(json), "target", "readiness", "stepId", "reason"); }
                catch (JsonException) { }
            }
            string citationSource = lease.sequence["resumedFrom"] is JObject ? (string)lease.sequence["originalRequest"] : lease.request;
            bool approved = ok && RoomControlReviewProtocol.AcceptsContinuation(json, lease.step, citationSource, (string)lease.candidate["quote"]);
            if (!approved) { FinishSequenceContinuation(lease, false, "review-deferred:" + status); return; }
            if (!SequenceContinuationBodyAvailable(lease))
            { FinishSequenceContinuation(lease, false, "body-ownership-changed"); return; }
            var latest = ReadRoomTaskSequence();
            var liveSteps = (JArray)latest["steps"];
            if (!RoomTaskSequenceProtocol.Ready(liveSteps).Values<string>().Contains(lease.step) ||
                !JToken.DeepEquals(lease.sequence["steps"], latest["steps"]))
            { FinishSequenceContinuation(lease, false, "prerequisite-receipts-changed"); return; }
            // Geometry and user tracking are re-read now, after semantics. No
            // old image, model coordinate or stored user position is executable.
            var target = bridge.ReadCurrentTargetEvidence(true);
            string kind = (string)lease.candidate["kind"];
            var motion = new DialogueMotionIntent(kind, "", generation, ++m_MotionSequence);
            RegisterMotionAction(ref motion);
            string failure;
            bool submitted = kind == "follow" ? bridge.FollowForTarget(motion.ActionId, target.currentTargetRevision, out failure) :
                bridge.ApproachForTarget(motion.ActionId, target.currentTargetRevision, out failure);
            // Once the bridge recorded this actual attempt, retain its failure
            // as well as success. A denied target check creates no fake receipt.
            if (submitted || bridge.ActionId == motion.ActionId)
            {
                var step = ((JArray)m_RoomTaskSequence["steps"]).OfType<JObject>().First(s => (string)s["id"] == lease.step);
                step["status"] = "running"; step["actionId"] = motion.ActionId; step["intent"] = kind; step["generation"] = generation;
                m_SequenceBodySubmission = new JObject { ["generation"] = generation, ["workEpoch"] = lease.epoch,
                    ["userTurn"] = lease.turn, ["stepId"] = lease.step, ["actionId"] = motion.ActionId, ["intent"] = kind };
            }
            FinishSequenceContinuation(lease, submitted, submitted ? "submitted-before-result-speech" : failure);
        }); }
        catch (Exception error)
        {
            if (SequenceContinuationCurrent(lease)) {
                if (lease.transport) model.CancelRoomPreparationMessage();
                lease.transport = false;
                FinishSequenceContinuation(lease, false, "review-transport-exception:" + error.GetType().Name);
            }
        }
        return true;
    }

    private IEnumerator WatchSequenceContinuation(SequenceBodyContinuation lease)
    {
        float deadline = Time.realtimeSinceStartup + 2.5f;
        while (SequenceContinuationCurrent(lease) && Time.realtimeSinceStartup < deadline) yield return null;
        if (!ReferenceEquals(lease, m_SequenceBodyContinuation) || lease.finished) yield break;
        bool current = SequenceContinuationCurrent(lease);
        CancelSequenceContinuation("owner-changed-or-deadline");
        if (current) lease.report?.Invoke(); // Failure defers execution; reporting still proceeds.
    }

    private void FinishSequenceContinuation(SequenceBodyContinuation lease, bool submitted, string reason)
    {
        if (!SequenceContinuationCurrent(lease)) return;
        lease.finished = true; m_SequenceBodyContinuation = null;
        Debug.Log("[Work/SequenceContinuation] " + new JObject { ["generation"] = lease.generation,
            ["workEpoch"] = lease.epoch, ["userTurn"] = lease.turn, ["stepId"] = lease.step,
            ["submitted"] = submitted, ["reason"] = reason, ["whileResultSpeechPending"] = true,
            ["review"] = lease.review, ["elapsedSeconds"] = Time.realtimeSinceStartup - lease.started,
            ["execution"] = ReadCompactRoomTaskFacts(lease.bridge)["execution"] }.ToString(Formatting.None), this);
        lease.report?.Invoke();
    }

    private void CancelSequenceContinuation(string reason)
    {
        var lease = m_SequenceBodyContinuation; m_SequenceBodyContinuation = null;
        if (lease == null) return;
        lease.finished = true;
        if (lease.transport && lease.model != null) lease.model.CancelRoomPreparationMessage();
        lease.transport = false;
        Debug.Log($"[Work/SequenceContinuation] generation={lease.generation} cancelled={reason}; no late action or reply", this);
    }
}
