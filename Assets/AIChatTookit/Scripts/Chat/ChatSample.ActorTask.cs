using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private int m_ActorTaskLanguageGeneration = -1;
    private string m_ActorTaskLanguage;
    private readonly Dictionary<int, ActorTaskSpeechLease> m_ActorTaskSpeech = new Dictionary<int, ActorTaskSpeechLease>();

    private sealed class ActorTaskSpeechLease
    {
        public int Generation, Epoch, Round;
        public long OutputRevision;
        public string Intent, ActionId, Dispatch;
        public bool ActiveUserTask, HistoricalPlanReceipt;
        public RoomSpeechSnapshot Snapshot;
        public UnityEngine.Object Controller, Avatar;
        public Transform Environment, User;
    }

    private void RememberActorTaskLanguage(int generation, string language)
    {
        if (generation != m_FormalResponseGeneration ||
            (language != "zh" && language != "ja" && language != "en")) return;
        m_ActorTaskLanguageGeneration = generation;
        m_ActorTaskLanguage = language;
    }

    private string ActorTaskLanguageFor(int generation) =>
        m_ActorTaskLanguageGeneration == generation &&
        (m_ActorTaskLanguage == "zh" || m_ActorTaskLanguage == "ja" || m_ActorTaskLanguage == "en")
            ? m_ActorTaskLanguage : "ja";

    private sealed class RoomFollowIdentity
    {
        public ArdyRoomDialogueBridge Bridge;
        public UnityEngine.Object Controller, Avatar;
        public Transform Environment, User;
        public string TaskId;
    }

    private RoomFollowIdentity CaptureRoomFollowIdentity(ArdyRoomDialogueBridge bridge)
    {
        if (bridge == null || !bridge.HasCurrentFollowBinding ||
            string.IsNullOrEmpty(bridge.ActiveTaskId)) return null;
        return new RoomFollowIdentity { Bridge = bridge, Controller = bridge.Controller,
            Avatar = bridge.avatar, Environment = bridge.environmentRoot, User = bridge.userHead,
            TaskId = bridge.ActiveTaskId };
    }

    private bool RoomFollowIdentityCurrent(RoomFollowIdentity owner) => owner != null &&
        RoomMotionOutputEnabled && owner.Bridge != null && owner.Bridge == GetComponent<ArdyRoomDialogueBridge>() &&
        owner.Bridge.HasCurrentFollowBinding && owner.TaskId == owner.Bridge.ActiveTaskId &&
        owner.Controller == owner.Bridge.Controller && owner.Avatar == owner.Bridge.avatar &&
        owner.Environment == owner.Bridge.environmentRoot && owner.User == owner.Bridge.userHead;

    private sealed class ActorUserTaskBinding
    {
        public int Generation;
        public ArdyRoomDialogueBridge Bridge;
        public UnityEngine.Object Controller, Avatar, ControllerAvatar;
        public object Navigation;
        public Transform Environment, User, NavigationEnvironment;
        public ChatSample Chat;
        public string ActionId, ActiveTaskId;
        public bool Connected, Enabled, Ready;
    }

    // Capture before the asynchronous judge. The model names current_user, but
    // cannot supply this identity or move its authority to a replacement rig.
    private ActorUserTaskBinding CaptureActorUserTaskBinding(int generation)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        return new ActorUserTaskBinding { Generation = generation, Bridge = bridge,
            Controller = bridge?.Controller, Avatar = bridge?.avatar, Navigation = bridge?.Navigation,
            ControllerAvatar = bridge?.Controller?.Avatar, NavigationEnvironment = bridge?.Navigation?.EnvironmentRoot,
            Chat = bridge?.chat,
            ActionId = bridge?.ActionId, ActiveTaskId = bridge?.ActiveTaskId,
            Environment = bridge?.environmentRoot, User = bridge?.userHead,
            Connected = bridge?.IsConnected == true, Enabled = RoomMotionOutputEnabled,
            Ready = bridge?.IsConnected == true && bridge.avatar != null && bridge.userHead != null &&
                bridge.Controller.Avatar == bridge.avatar && bridge.Navigation.EnvironmentRoot == bridge.environmentRoot && bridge.chat == this };
    }

    private bool ActorUserTaskBindingCurrent(ActorUserTaskBinding owner, int generation)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        return owner != null && owner.Generation == generation && owner.Bridge == bridge &&
            owner.Enabled == RoomMotionOutputEnabled && owner.Connected == (bridge?.IsConnected == true) &&
            owner.Controller == bridge?.Controller && owner.Avatar == bridge?.avatar &&
            owner.ControllerAvatar == bridge?.Controller?.Avatar && owner.NavigationEnvironment == bridge?.Navigation?.EnvironmentRoot &&
            owner.Chat == bridge?.chat &&
            ReferenceEquals(owner.Navigation, bridge?.Navigation) &&
            owner.Environment == bridge?.environmentRoot && owner.User == bridge?.userHead;
    }

    // This is a finite compiler target, not a second interpretation of user text.
    // Only the judged, unpublished current role draft can select either task.
    private bool TryDispatchActorKnownTask(FastLoopTurnContext turn, ActorDraft draft,
        ActorIntentDecision decision, string language, int generation, ActorUserTaskBinding binding)
    {
        if (decision == null || decision.Kind != "replan" || decision.Domain != "other_action" ||
            decision.Coverage != "atomic" || decision.Condition != "none" || decision.RequiresFullReview ||
            decision.Target != "current_user" || (decision.Task != "approach" && decision.Task != "follow")) return false;
        if (!FastLoopTurnCurrent(turn) || turn.Generation != generation || turn.HandedOff ||
            m_FormalTransportDispatchGeneration == generation || draft == null ||
            string.IsNullOrWhiteSpace(draft.Id) || draft.Id.Length > 128 || draft.Revision <= 0 ||
            !ActorIntentProtocol.Validate(decision, draft.Text, out _) ||
            (language != "zh" && language != "ja" && language != "en")) return false;

        RememberActorTaskLanguage(generation, language);
        int revocation = m_RoomActorProposalRevocation, userTurn = m_ActionConstraints.CurrentUserTurn;
        var source = new RoomTaskProtocol.ActorProposalSource { Id = draft.Id, Revision = draft.Revision, Text = draft.Text, Purpose = "plan",
            Target = decision.Target, Coverage = decision.Coverage };
        bool claimed = false;
        int submissionSerial = ++m_ActorIntentRequestSerial;
        bool Current() => FastLoopTurnCurrent(turn) && ActorUserTaskBindingCurrent(binding, generation) && revocation == m_RoomActorProposalRevocation &&
            submissionSerial == m_ActorIntentRequestSerial &&
            userTurn == m_ActionConstraints.CurrentUserTurn &&
            (claimed || binding.ActionId == binding.Bridge?.ActionId && binding.ActiveTaskId == binding.Bridge?.ActiveTaskId) &&
            (!claimed || RoomTaskProtocol.SameActorProposal(source, RoomTaskProtocol.ReadActorProposal(turn.ActorProposal)));
        bool Recheck()
        {
            if (Current()) return true;
            // A same-generation scene-output cancellation must not leave the
            // formal waiter hanging. A new turn/model keeps all of its own flags.
            if (FastLoopTurnCurrent(turn) && submissionSerial == m_ActorIntentRequestSerial)
                CancelFastLoopRequest("actor-known-task-revoked");
            return false;
        }
        m_ActorIntentBackend = null;
        CancelActorSpeechPreparation("actor-known-task-receipt");
        if (!Recheck()) return true;

        // The provider boundary has returned with this draft still owning the
        // request. Claim before entering controller code; this task may then
        // outlive ordinary dialogue without acquiring a second dispatch.
        turn.HandedOff = true;
        turn.PlannerEntryClaimed = true;
        turn.ActorProposal = new JObject {
            ["source"] = "unpublished-current-role-draft", ["draftId"] = draft.Id,
            ["draftRevision"] = draft.Revision, ["text"] = draft.Text,
            ["domain"] = decision.Domain, ["evidence"] = decision.Evidence,
            ["purpose"] = "plan", ["task"] = decision.Task, ["target"] = decision.Target,
            ["coverage"] = "atomic", ["userText"] = FastLoopCurrentUserText(turn.Prompt),
            ["originalUserText"] = FastLoopCurrentUserText(turn.Prompt),
            ["language"] = language, ["generation"] = generation, ["epoch"] = turn.Epoch,
            ["outputRevision"] = turn.OutputRevision, ["executed"] = false, ["published"] = false
        };
        claimed = true;

        var roomDecision = new RoomTaskProtocol.Decision {
            intent = decision.Task, origin = "actor_draft", evidence = decision.Evidence,
            actorDraftId = draft.Id, actorDraftRevision = draft.Revision, SourceText = draft.Text
        };
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        m_RoomTaskGeneration = generation;
        m_RoomTaskIntent = decision.Task;
        m_RoomTaskDispatch = "not-submitted";
        m_RoomTaskActionId = "";
        m_RoomTaskTargetRevision = bridge?.ReadCurrentTargetEvidence(true).currentTargetRevision ?? 0;
        if (!Recheck()) return true;
        if (binding.Ready || m_ExplorationAutonomousSuppressed)
            SubmitRoomUserTask(roomDecision, bridge, generation, m_RoomTaskTargetRevision, Current);
        else m_RoomTaskDispatch = "not-submitted: room-control-unavailable";
        if (!Recheck()) return true;
        RecordWorkRoomTaskContext();
        m_ResponseTrace?.Mark("actor_task.submission", new JObject {
            ["generation"] = generation, ["draftId"] = draft.Id, ["draftRevision"] = draft.Revision,
            ["origin"] = "actor_draft", ["task"] = decision.Task, ["target"] = "current_user",
            ["dispatch"] = m_RoomTaskDispatch, ["actionId"] = m_RoomTaskActionId,
            ["targetRevision"] = m_RoomTaskTargetRevision, ["phase"] = bridge?.Phase,
            ["programBindingReady"] = binding.Ready,
            ["controllerOperation"] = bridge?.Controller?.OperationRevision,
            ["scope"] = "Actual bounded controller attempt, not proof of arrival or continuing physical progress."
        });
        Debug.Log($"[ActorTask/Submission] generation={generation} source=actor_draft task={decision.Task} dispatch={m_RoomTaskDispatch} action={m_RoomTaskActionId} phase={bridge?.Phase}", this);
        if (!Recheck()) return true;
        // This compiler target represents the complete atomic approach. Its
        // program-owned empty remainder cannot be replaced by a model copying
        // the same request back into another Room step. Real completion still
        // comes exclusively from the existing controller-result watcher.
        if (decision.Task == "approach" && m_RoomTaskDispatch == "attempt-submitted" &&
            !string.IsNullOrEmpty(m_RoomTaskActionId))
        {
            BeginActorPlan(turn, draft, decision);
            source = RoomTaskProtocol.ReadActorProposal(turn.ActorProposal);
            if (!Recheck()) return true;
            roomDecision.planId = m_ActorPlan.Id;
            roomDecision.planRevision = m_ActorPlan.Revision;
            roomDecision.planRemaining = "";
            if (AcceptActorPlanStep(roomDecision, generation, out _))
                return ConsumeActorPlanStep(roomDecision, true, generation);
            CancelActorPlan("known-task-plan-not-accepted");
        }
        // Navigation refusal may occur synchronously inside an accepted bridge
        // call. Build the receipt from its resulting facts, never its bool alone.
        string receipt = CurrentActorTaskReceipt(language);
        PublishActorTaskReceipt(turn, receipt, language, "known-task", true);
        return true;
    }

    // Shared by the finite actor compiler and the general room planner. Keep the
    // latter's self-initiated suppression, current target and navigation boundary.
    private void SubmitRoomUserTask(RoomTaskProtocol.Decision decision, ArdyRoomDialogueBridge bridge,
        int generation, int targetRevision, Func<bool> current = null)
    {
        if (current != null && !current()) return;
        if (!RoomMotionOutputEnabled || bridge == null || !bridge.IsConnected)
        { m_RoomTaskDispatch = "not-submitted: room-control-unavailable"; return; }
        if (decision.IsSelfInitiated && m_ExplorationAutonomousSuppressed)
        { m_RoomTaskDispatch = "not-submitted: autonomous-movement-stopped"; return; }
        var facts = bridge.ReadCurrentTargetEvidence();
        if (decision.intent == "follow" && bridge.HasCurrentFollowBinding)
        { m_RoomTaskDispatch = "follow-already-active"; m_RoomTaskActionId = bridge.ActiveTaskId; return; }
        // A completed approach and a newly adopted follow are different tasks.
        // Preserve suppression for the same task type (or an unknown old owner),
        // not just for sharing the user's target revision.
        bool sameTaskKind = !m_MotionActions.TryGetValue(bridge.ActionId ?? "", out var previous) ||
            previous.intent.Name == decision.intent;
        if (decision.IsSelfInitiated && facts.lastAttemptAppliesToCurrentTarget && sameTaskKind)
        { m_RoomTaskDispatch = "same-target-autonomous-retry-suppressed"; return; }
        CancelPersistentSceneAttention("new-body-task");
        if (decision.origin == "user" && m_SceneExploration.active)
            StopSceneExploration("new-user-spatial-task", true);
        if (current != null && !current()) return;
        var intent = new DialogueMotionIntent(decision.intent, "", generation, ++m_MotionSequence);
        RegisterMotionAction(ref intent);
        if (current != null && !current()) return;
        m_RoomTaskActionId = intent.ActionId;
        string failure;
        bool submitted = decision.intent == "follow"
            ? bridge.FollowForTarget(intent.ActionId, targetRevision, out failure)
            : bridge.ApproachForTarget(intent.ActionId, targetRevision, out failure);
        if (current != null && !current()) return;
        m_RoomTaskDispatch = submitted ? (decision.intent == "follow" ? "follow-submitted" : "attempt-submitted")
            : "not-submitted: " + failure;
    }

    private string CurrentActorTaskReceipt(string language)
    {
        string Text(string zh, string ja, string en) => language == "ja" ? ja : language == "en" ? en : zh;
        if (m_RoomTaskDispatch == "same-target-autonomous-retry-suppressed")
            return Text("这个位置刚才已经尝试过，我没有重复启动。", "この位置ではもう試したので、同じ動きは繰り返していないわ。", "I've already tried this position, so I haven't started the same move again.");
        if (m_RoomTaskDispatch == "not-submitted: autonomous-movement-stopped")
            return Text("刚才停止移动的限制还在，这次没有重新开始。", "さっき移動を止めた状態を守って、今回は再開していないわ。", "The previous stop is still in effect, so I haven't restarted movement.");
        if (m_RoomTaskDispatch == "not-submitted: room-control-unavailable")
            return Text("移动控制目前没有连接，这次没有开始。", "移動の操作が今はつながっていないので、今回は始めていないわ。", "Movement control is unavailable, so I haven't started this task.");
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (m_RoomTaskDispatch == "follow-already-active" && bridge != null && bridge.IsConnected &&
            bridge.IsFollowing && bridge.ActiveTaskId == m_RoomTaskActionId)
            return bridge.Phase == "follow-paused"
                ? Text("跟随任务仍然保留，但移动暂时受阻，我没有重复启动。", "ついていく動きは維持しているけれど、今は移動が一時的に止まっているわ。重ねて始めてはいないわ。", "The follow task is retained, but movement is temporarily blocked; I haven't restarted it.")
                : Text("跟随任务还在继续，我没有重复启动。", "ついていく動きは続けているから、重ねて始めてはいないわ。", "The follow task is still active; I haven't started it again.");
        if (m_RoomTaskIntent == "approach" && m_RoomTaskDispatch == "attempt-submitted" &&
            bridge != null && bridge.IsConnected && bridge.ActionId == m_RoomTaskActionId &&
            bridge.Phase == "arrived")
        {
            var arrival = bridge.LastApproachArrival;
            if (arrival?.terminalConfirmed == true && arrival.actionId == m_RoomTaskActionId)
                return Text("刚才已经走到本次选定的位置，并朝向当时的你了。",
                    "さっき今回選んだ位置に着いて、その時のあなたの位置へ向いたわ。",
                    "I reached the position selected for this attempt and faced where you were at that time.");
            return bridge.ReadCurrentTargetEvidence().currentlyNearAndFacingUser
                ? Text("我已经在你附近并朝向你了。", "今はあなたの近くで、そちらを向いているわ。", "I'm already near you and facing you.")
                : Text("刚才已经走到选定的位置了。", "さっき選んだ位置には着いたわ。", "I reached the selected position.");
        }
        return CurrentRoomActionReceipt(language) ?? Text(
            "这次移动还没有得到有效受理，我没有把它当作已经开始。",
            "今回の移動はまだ受理を確認できていないので、始まったとは言えないわ。",
            "This movement has not been confirmed as accepted, so I can't say it has started.");
    }

    private void PublishActorTaskReceipt(FastLoopTurnContext turn, string text, string language,
        string path, bool avoidedPlanner)
    {
        if (!FastLoopTurnCurrent(turn) || string.IsNullOrEmpty(text)) return;
        RememberDeviceReceiptSpeech(turn.Generation, turn.Round, turn.OutputRevision);
        m_FormalTransportDispatchGeneration = turn.Generation;
        m_RoomSpeechGuardGeneration = -1;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var snapshot = CaptureRoomSpeechSnapshot(bridge, null);
        m_ActorTaskSpeech[turn.Round] = new ActorTaskSpeechLease {
            Generation = turn.Generation, Epoch = turn.Epoch, Round = turn.Round,
            OutputRevision = turn.OutputRevision, Intent = m_RoomTaskIntent,
            ActionId = m_RoomTaskActionId, Dispatch = m_RoomTaskDispatch, Snapshot = snapshot,
            Controller = bridge?.Controller, Avatar = bridge?.avatar,
            Environment = bridge?.environmentRoot, User = bridge?.userHead,
            HistoricalPlanReceipt = path == "persistent-plan-closure" || path == "persistent-plan-result" &&
                ReferenceEquals(m_ActorPlanTurn, turn) && m_ActorPlan != null && m_ActorPlan.State != "ongoing",
            ActiveUserTask = (m_RoomTaskIntent == "approach" || m_RoomTaskIntent == "follow") &&
                !string.IsNullOrEmpty(m_RoomTaskActionId) && snapshot.activeTaskId == m_RoomTaskActionId &&
                ActorTaskActivePhase(m_RoomTaskIntent, snapshot.phase)
        };
        foreach (int oldRound in new List<int>(m_ActorTaskSpeech.Keys))
            if (oldRound != turn.Round) m_ActorTaskSpeech.Remove(oldRound);
        m_ResponseTrace?.Mark("speech.actor_task_receipt", new JObject {
            ["generation"] = turn.Generation, ["actionId"] = m_RoomTaskActionId,
            ["intent"] = m_RoomTaskIntent, ["dispatch"] = m_RoomTaskDispatch,
            ["language"] = language, ["path"] = path,
            ["avoidedPlanner"] = avoidedPlanner, ["avoidedDescriptionAndReview"] = true,
            ["scope"] = "Program-authored bounded receipt from current submission facts; no unverified draft or completion claim."
        });
        if (!FastLoopTurnCurrent(turn)) return;
        if (ActorPlanOwnerCurrent(m_ActorPlan, turn) &&
            (m_ActorPlan.State == "completed" || m_ActorPlan.State == "blocked" || m_ActorPlan.State == "cancelled"))
            SettleActorUserWork(turn.Generation, turn.Epoch, m_ActorPlan.State == "completed", m_ActorPlan.Reason);
        CompleteFastLoopReply(turn.Model, turn.Prompt, text, language, turn.Generation, turn.RecordHistory);
    }

    private static bool ActorTaskActivePhase(string intent, string phase) => phase == "preparing" ||
        phase == "generating" || phase == "moving" || intent == "follow" && phase == "follow-waiting";

    private bool ValidateActorTaskSpeech(int roundId)
    {
        if (!ValidateDeviceReceiptSpeech(roundId)) return false;
        if (!m_ActorTaskSpeech.TryGetValue(roundId, out var lease)) return true;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        bool current = lease.Generation == m_FormalResponseGeneration && lease.Epoch == m_WorkEpoch &&
            lease.Round == m_CurrentSpeechRoundId && lease.OutputRevision == m_FastLoop.OutputRevision &&
            m_RoomTaskGeneration == lease.Generation && m_RoomTaskIntent == lease.Intent &&
            m_RoomTaskActionId == lease.ActionId && m_RoomTaskDispatch == lease.Dispatch &&
            bridge == lease.Snapshot.bridge && bridge?.Controller == lease.Controller &&
            bridge?.avatar == lease.Avatar && bridge?.environmentRoot == lease.Environment && bridge?.userHead == lease.User;
        if (current && lease.ActiveUserTask)
            current = RoomMotionOutputEnabled && bridge != null && bridge.IsConnected &&
                bridge.ActionId == lease.ActionId && bridge.ActiveTaskId == lease.ActionId &&
                bridge.TaskMode == lease.Intent && ActorTaskActivePhase(lease.Intent, bridge.Phase);
        else if (current && bridge != null && !lease.HistoricalPlanReceipt)
            current = ChangedRoomSpeechFacts(lease.Snapshot, IsSceneVisualIntent(lease.Intent)
                ? RoomSpeechDependencies.SceneAction : RoomSpeechDependencies.UserTarget).Count == 0;
        if (current) return true;
        DiscardPendingSpeechForRound(roundId, "actor-task-fact-invalidated");
        m_ActorTaskSpeech.Remove(roundId);
        m_ResponseTrace?.Mark("actor_task.speech_revoked", new JObject {
            ["generation"] = lease.Generation, ["speechRound"] = roundId, ["actionId"] = lease.ActionId,
            ["reason"] = "actor-task-fact-invalidated" });
        return false;
    }

    // Complex targets still need their existing planner and target checks. Once
    // these have a bounded receipt, do not ask two more models to retell the facts.
    private bool TryDeliverActorPlannedTaskReceipt(RoomTaskProtocol.Decision decision, int generation)
    {
        var turn = m_FastLoopTurn;
        if (decision == null || decision.origin != "actor_draft" || !FastLoopTurnCurrent(turn) ||
            !turn.HandedOff || turn.Generation != generation || m_RoomTaskGeneration != generation ||
            m_FormalTransportDispatchGeneration == generation ||
            m_ActorTaskLanguageGeneration != generation || (string)turn.ActorProposal?["purpose"] != "plan") return false;
        var source = RoomTaskProtocol.ReadActorProposal(turn.ActorProposal);
        if (source == null || source.Id != decision.actorDraftId || source.Revision != decision.actorDraftRevision ||
            !string.Equals(source.Text, decision.SourceText, StringComparison.Ordinal)) return false;
        string text = (decision.intent == "approach" || decision.intent == "follow")
            ? CurrentActorTaskReceipt(m_ActorTaskLanguage) : CurrentRoomActionReceipt(m_ActorTaskLanguage);
        if (string.IsNullOrEmpty(text)) return false;
        PublishActorTaskReceipt(turn, text, m_ActorTaskLanguage, "planned-task", false);
        return true;
    }
}
