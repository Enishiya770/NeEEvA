using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private sealed class FastTaskControlSnapshot
    {
        public ArdyRoomDialogueBridge Bridge;
        public UnityEngine.Object Controller, Avatar;
        public Transform Environment, User;
        public string TaskId;
        public int Operation, SceneEpoch;
        public bool Following;
    }

    private sealed class FastTaskControlSpeechLease
    {
        public FastTaskControlSnapshot Binding;
        public string StopId;
        public int Generation, Epoch, Round, Operation;
        public long OutputRevision;
    }
    private readonly Dictionary<int, FastTaskControlSpeechLease> m_FastTaskControlSpeech =
        new Dictionary<int, FastTaskControlSpeechLease>();

    private FastTaskControlSnapshot CaptureFastTaskControl()
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        return bridge == null ? null : new FastTaskControlSnapshot {
            Bridge = bridge, Controller = bridge.Controller, Avatar = bridge.avatar,
            Environment = bridge.environmentRoot, User = bridge.userHead,
            TaskId = bridge.ActiveTaskId, Operation = bridge.Controller?.OperationRevision ?? -1,
            Following = bridge.IsFollowing, SceneEpoch = m_SceneActionEpoch
        };
    }

    private bool FastTaskControlBindingCurrent(FastTaskControlSnapshot snapshot) =>
        snapshot != null && snapshot.Bridge != null && snapshot.Bridge.IsConnected &&
        snapshot.Bridge == GetComponent<ArdyRoomDialogueBridge>() &&
        snapshot.Controller == snapshot.Bridge.Controller && snapshot.Avatar == snapshot.Bridge.avatar &&
        snapshot.Environment == snapshot.Bridge.environmentRoot && snapshot.User == snapshot.Bridge.userHead &&
        snapshot.SceneEpoch == m_SceneActionEpoch;

    // The role writer is not the sole semantic authority to cancel an ongoing
    // task. A short independent classifier sees only the full accepted input and
    // the captured body state, never the proposed verdict or role's reply.
    private bool TryReviewFastTaskControl(FastLoopTurnContext turn, FastLoopDecision decision,
        string currentUser, string headId, int headRevision, ConversationTrace trace)
    {
        if (decision == null || decision.TaskControl == "none" || turn.TaskControlReviewed) return false;
        if (turn.TaskControlReviewPending) return true;
        var binding = turn.BodyControl;
        // Stale/wrong-domain targets already fail in the controller path; do not
        // spend a model request trying to rescue an invalid ownership proposal.
        if (!FastTaskControlBindingCurrent(binding) || string.IsNullOrEmpty(binding.TaskId) ||
            binding.TaskId != decision.ControlTargetId || binding.Bridge.ActiveTaskId != binding.TaskId) return false;
        var proposal = new FastLoopDecision {
            Version = decision.Version, Route = decision.Route, Candidate = decision.Candidate,
            Evidence = decision.Evidence, Speech = decision.Speech, Language = decision.Language,
            TaskControl = decision.TaskControl, ControlTargetId = decision.ControlTargetId, ControlEvidence = decision.ControlEvidence
        };
        turn.TaskControlReviewPending = true;
        bool consumed = false;
        float started = Time.realtimeSinceStartup;
        void Resolve(string verdict, string status)
        {
            if (consumed || !FastLoopTurnCurrent(turn) || !turn.TaskControlReviewPending) return;
            consumed = true;
            turn.TaskControlReviewPending = false;
            turn.TaskControlReviewed = true;
            if (status != "ok") verdict = null;
            // Both judgments must request ending this same captured body task.
            // The concrete stop kind follows the actual controller and the
            // independent verdict, not the role writer's possibly wrong subtype.
            bool agreed = verdict == (binding.Following ? "stop_follow" : "stop_movement");
            trace?.Mark("fast_loop.task_control_review", new JObject {
                ["proposal"] = proposal.TaskControl, ["verdict"] = verdict, ["agreed"] = agreed,
                ["status"] = status, ["seconds"] = Time.realtimeSinceStartup - started });
            if (!FastLoopTurnCurrent(turn)) return;
            if (agreed) proposal.TaskControl = verdict;
            if (!agreed)
            {
                turn.ControlResult = new JObject {
                    ["control"] = proposal.TaskControl, ["targetTaskId"] = proposal.ControlTargetId,
                    ["accepted"] = false, ["stopped"] = false,
                    ["reason"] = verdict == "none" ? "independent-control-review-declined" : "independent-control-review-unconfirmed",
                    ["scope"] = "No cancellation was authorized or submitted. Preserve the complete remaining user goal and actual running-task state."
                };
                proposal.TaskControl = "none"; proposal.ControlTargetId = ""; proposal.ControlEvidence = "";
                // Discard the unapproved acknowledgement and reconsider the
                // complete input once. Feedback about an ongoing task is not
                // necessarily another spatial goal.
                if (proposal.Route == "reply")
                {
                    if (TryRepairDeclinedFastControl(turn, proposal.Language, currentUser,
                        headId, headRevision)) return;
                    HandoffFastLoopToPlanner(turn.Model, turn.Generation, turn.OutputRevision,
                        null, null, "task-control-review-not-authorized");
                    return;
                }
            }
            ApplyFastLoopAnswer(turn, proposal, currentUser, headId, headRevision, trace);
        }
        if (!(turn.Model is IFastTaskControlBackend backend))
        { Resolve(null, "control-review-unavailable"); return true; }
        var context = new JObject { ["roomTask"] = new JObject {
            ["connected"] = true, ["activeTaskId"] = binding.TaskId,
            ["taskMode"] = binding.Following ? "follow" : binding.Bridge.TaskMode,
            ["followActive"] = binding.Following, ["phase"] = binding.Bridge.Phase
        } };
        try { backend.PostFastTaskControlDecision(context.ToString(Newtonsoft.Json.Formatting.None), currentUser,
            Resolve, trace?.Child("independent-task-control")); }
        catch (Exception error)
        {
            if (consumed) throw;
            Resolve(null, "control-review-exception:" + error.GetType().Name);
        }
        return true;
    }

    private void ApplyFastTaskControl(FastLoopTurnContext turn, FastLoopDecision decision)
    {
        if (!FastLoopTurnCurrent(turn) || turn.ControlResult != null) return;
        var binding = turn.BodyControl;
        string stopId = "fast-stop:" + Guid.NewGuid().ToString("N");
        string failure = "captured-task-no-longer-current";
        bool accepted = FastTaskControlBindingCurrent(binding) &&
            !string.IsNullOrEmpty(binding.TaskId) && decision.ControlTargetId == binding.TaskId &&
            (decision.TaskControl != "stop_follow" || binding.Following) &&
            binding.Bridge.StopForTaskIfCurrent(stopId, binding.TaskId, binding.Operation,
                out failure, binding.Following);
        if (!FastLoopTurnCurrent(turn)) return;
        bool stopped = accepted && HasConfirmedFastTaskStop(binding, stopId);
        turn.ControlResult = new JObject {
            ["control"] = decision.TaskControl, ["targetTaskId"] = decision.ControlTargetId,
            ["accepted"] = accepted, ["stopped"] = stopped, ["stopActionId"] = accepted ? stopId : "",
            ["reason"] = stopped ? "controller-confirmed-stopped" : failure ?? "stop-not-yet-confirmed",
            ["scope"] = "Controller facts only. Stopped means locomotion and follow are cancelled; posture may still blend to idle. Preserve the complete user's remaining goals; this result is not singing permission."
        };
        if (stopped && decision.Route == "reply")
            m_FastTaskControlSpeech[turn.Round] = new FastTaskControlSpeechLease {
                Binding = binding, StopId = stopId, Generation = turn.Generation, Epoch = turn.Epoch,
                Round = turn.Round, OutputRevision = turn.OutputRevision,
                Operation = binding.Bridge.Controller.OperationRevision
            };
        m_ResponseTrace?.Mark("fast_loop.task_control", (JObject)turn.ControlResult.DeepClone());
        Debug.Log($"[FastLoop/TaskControl] generation={turn.Generation} control={decision.TaskControl} accepted={accepted} stopped={stopped}", this);
    }

    // Returning the exact cancelled clip to idle is posture cleanup, not an
    // ongoing locomotion task. Use the same predicate at publication and play
    // time; another owner, a pending move or a new task never satisfies it.
    private bool HasConfirmedFastTaskStop(FastTaskControlSnapshot binding, string stopId)
    {
        if (!FastTaskControlBindingCurrent(binding) || string.IsNullOrEmpty(stopId)) return false;
        var bridge = binding.Bridge;
        var controller = bridge.Controller;
        return controller != null && bridge.ActionId == stopId && !bridge.IsFollowing &&
            string.IsNullOrEmpty(bridge.ActiveTaskId) && !controller.IsBusy &&
            (!controller.IsMoving || controller.IsReturningOwnedPose) &&
            (!bridge.ReservesBody || controller.IsReturningOwnedPose);
    }

    private void CompleteFastTaskControlReply(FastLoopTurnContext turn, string language)
    {
        bool stopped = (bool?)turn.ControlResult?["stopped"] == true;
        string text = language == "ja" ? (stopped ? "移動と追従を止めたよ。" : "今の動きが止まったことは、まだ確認できていないよ。") :
            language == "en" ? (stopped ? "I've stopped moving and following." : "I haven't confirmed that the current movement has stopped.") :
            stopped ? "已经停止移动和跟随了。" : "我还没有确认当前的移动已经停止。";
        CompleteFastLoopReply(turn.Model, turn.Prompt, text, language, turn.Generation, turn.RecordHistory);
    }

    private bool ValidateFastTaskControlSpeech(int round)
    {
        if (!m_FastTaskControlSpeech.TryGetValue(round, out var lease)) return true;
        var bridge = lease.Binding.Bridge;
        if (lease.Generation == m_FormalResponseGeneration && lease.Epoch == m_WorkEpoch &&
            lease.Round == m_CurrentSpeechRoundId && lease.OutputRevision == m_FastLoop.OutputRevision &&
            FastTaskControlBindingCurrent(lease.Binding) &&
            bridge.Controller.OperationRevision == lease.Operation &&
            HasConfirmedFastTaskStop(lease.Binding, lease.StopId)) return true;
        DiscardPendingSpeechForRound(round, "fast-task-stop-fact-invalidated");
        m_FastTaskControlSpeech.Remove(round);
        return false;
    }
}
