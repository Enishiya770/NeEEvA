using Newtonsoft.Json.Linq;
using System.Linq;

public partial class ChatSample
{
    private int m_WorkRoomTaskEpoch = -1, m_WorkSceneFeedbackGuardGeneration = -1;
    private string m_WorkRoomTaskIntent = "none", m_WorkRoomTaskActionId = "";
    private int m_WorkRoomTaskUserTurn = -1, m_WorkRoomStepReplyGeneration = -1;
    private string m_WorkRoomRequestText = "";

    private sealed class WorkRoomStepRequest
    {
        public int epoch, userTurn, continuationOrdinal;
        public string originalRequest, nextStepIntention, priorIntent, priorActionId, reviewedNextStepId;
        public JObject previousUnfinishedWork;
        public bool replyCommitted, reclassifyReadOnlyRequest;
    }

    // Called after the structured spatial decision and its preparation have settled.
    // A continuation has a new formal generation but remains in the same actual user-work epoch.
    private void RecordWorkRoomTaskContext()
    {
        m_WorkRoomTaskEpoch = m_WorkEpoch;
        m_WorkRoomTaskIntent = m_RoomTaskIntent;
        m_WorkRoomTaskActionId = m_RoomTaskActionId;
        m_WorkRoomTaskUserTurn = m_ActionConstraints.CurrentUserTurn;
        m_WorkRoomRequestText = m_ActionConstraints.CurrentUserText;
    }

    private JObject BuildWorkRoomCompletionObservation()
    {
        if (m_WorkRoomTaskEpoch != m_WorkEpoch || m_WorkRoomTaskUserTurn != m_ActionConstraints.CurrentUserTurn ||
            string.IsNullOrEmpty(m_WorkRoomTaskActionId)) return null;
        var room = ReadCompactRoomTaskFacts(GetComponent<ArdyRoomDialogueBridge>());
        var sequence = ReadRoomTaskSequence();
        var body = (sequence?["steps"] as JArray)?.OfType<JObject>().FirstOrDefault(s =>
            (string)s["actionId"] == (string)room.SelectToken("execution.actionId") &&
            new[] { "approach", "follow", "stop-moving" }.Contains((string)s["kind"]));
        string intent = body == null ? m_WorkRoomTaskIntent : (string)body["kind"];
        string action = body == null ? m_WorkRoomTaskActionId : (string)body["actionId"];
        return new JObject { ["intent"] = intent, ["actionId"] = action,
            ["currentRoomFacts"] = room,
            ["movementReportRequirement"] = RoomMovementResultPolicy.Requirement(room, intent, action),
            ["scope"] = "Read current matching execution before proposing continuation. Generated replies and an old attempt-submitted dispatch cannot override actual arrival. Later world changes do not undo a completed event. A new autonomous intention needs its own current decision and action identity; independently unfinished answers or requirements remain separate." };
    }

    private WorkRoomStepRequest CreateWorkRoomStepRequest()
    {
        if (m_WorkReplyOnly) return null;
        var sequence = ReadRoomTaskSequence();
        if (sequence != null && !WorkNextIntentProtocol.HasReadyStep(sequence, m_WorkNextStepId)) return null;
        bool readyOwnedStep = WorkNextIntentProtocol.HasReadyStep(sequence, m_WorkNextStepId);
        bool recovering = IsCurrentReadOnlyWorkRequest() && m_WorkContinuations == 0;
        if (!HasRoomTaskPlanner || !m_WorkReviewOpen || m_WorkStatus != "continue" || m_WorkNoProgress ||
            m_WorkContinuations >= MaxWorkContinuations || (!recovering && (m_WorkRoomTaskEpoch != m_WorkEpoch ||
            m_WorkRoomTaskUserTurn != m_ActionConstraints.CurrentUserTurn || m_WorkRoomTaskIntent == "none" && !readyOwnedStep ||
            m_WorkRoomRequestText != m_ActionConstraints.CurrentUserText)) ||
            string.IsNullOrWhiteSpace(m_ActionConstraints.CurrentUserText) || m_UserSpeechActiveForAutonomy ||
            HasWorkToolInFlight() || SceneActionIsBusy) return null;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (bridge == null || bridge.ReservesBody || bridge.IsFollowing ||
            (bridge.Controller != null && (bridge.Controller.IsBusy || bridge.Controller.IsMoving))) return null;
        return new WorkRoomStepRequest {
            epoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn,
            continuationOrdinal = m_WorkContinuations + 1,
            originalRequest = m_ActionConstraints.CurrentUserText, nextStepIntention = m_WorkIntent,
            reviewedNextStepId = m_WorkNextStepId,
            previousUnfinishedWork = m_PreviousUnfinishedWork?.DeepClone() as JObject,
            priorIntent = recovering ? "none" : m_WorkRoomTaskIntent,
            priorActionId = recovering ? "" : m_WorkRoomTaskActionId, reclassifyReadOnlyRequest = recovering
        };
    }

    private bool IsCurrentReadOnlyWorkRequest() => m_WorkRequestSemantics != null &&
        (int?)m_WorkRequestSemantics["workEpoch"] == m_WorkEpoch &&
        (int?)m_WorkRequestSemantics["userTurn"] == m_ActionConstraints.CurrentUserTurn &&
        (string)m_WorkRequestSemantics["currentUserText"] == m_ActionConstraints.CurrentUserText &&
        (string)m_WorkRequestSemantics["effectiveRoute"] == "evidence" &&
        m_WorkRequestSemanticsModel == m_ChatSettings?.m_ChatModel;

    private bool IsCurrentWorkRoomStep(WorkRoomStepRequest step) => step != null &&
        step.epoch == m_WorkEpoch && step.userTurn == m_ActionConstraints.CurrentUserTurn &&
        step.originalRequest == m_ActionConstraints.CurrentUserText &&
        step.continuationOrdinal == m_WorkContinuations && m_WorkContinuations <= MaxWorkContinuations &&
        m_WorkReviewOpen && m_WorkStatus == "continue" && !m_WorkNoProgress &&
        !m_UserSpeechActiveForAutonomy && !m_SceneTimePaused && isActiveAndEnabled &&
        (step.replyCommitted || (step.reclassifyReadOnlyRequest ? IsCurrentReadOnlyWorkRequest() : (m_WorkRoomTaskEpoch == step.epoch &&
            m_WorkRoomTaskIntent == step.priorIntent && m_WorkRoomTaskActionId == step.priorActionId)));

    private string AddWorkRoomStepDecisionContext(string input, WorkRoomStepRequest step)
    {
        if (step == null) return input;
        var decisionInput = JObject.Parse(input);
        decisionInput["workContinuation"] = new JObject {
            ["source"] = step.reclassifyReadOnlyRequest ? "unfulfilled-read-only-request-reclassification" : "approved-same-user-work-next-step",
            ["reclassifyReadOnlyRequest"] = step.reclassifyReadOnlyRequest,
            ["workEpoch"] = step.epoch, ["userTurn"] = step.userTurn,
            ["continuationOrdinal"] = step.continuationOrdinal,
            ["originalRequest"] = step.originalRequest, ["nextStepIntention"] = step.nextStepIntention,
            ["reviewedNextStepId"] = step.reviewedNextStepId,
            ["previousUnfinishedWork"] = step.previousUnfinishedWork?.DeepClone(),
            ["priorIntent"] = step.priorIntent, ["priorActionId"] = step.priorActionId,
            ["feedbackOnly"] = false,
            ["scope"] = "Continue only the original currentUserText using current execution facts. " +
                "nextStepIntention is a review proposal, never new user evidence or a completed action. " +
                "When reclassifyReadOnlyRequest is true, independently decide whether the COMPLETE original request actually asks for action; the earlier read-only interpretation and review proposal may be wrong. Genuine informational questions, translations, negations and explicit only-answer restrictions do not authorize motion. A polite question or reminder can request completion of unfinished work; interpret it using the full historical request, while the current utterance's restrictions always take precedence. Historical unfinished work is reference, not permission. A speaker's unexecuted promise is not user evidence. Returning none is valid. " +
                "A completed prerequisite such as arrival does not complete the requested device state change. " +
                "Do not repeat an operation already satisfied by current facts; return none when the request is complete."
        };
        return decisionInput.ToString(Newtonsoft.Json.Formatting.None);
    }

    private bool ShouldGuardWorkSceneFeedback(int generation) =>
        generation == m_FormalResponseGeneration && m_WorkRoomTaskEpoch == m_WorkEpoch &&
        (m_WorkRoomTaskIntent != "none" || ReadRoomTaskSequence() != null) && m_WorkRoomTaskIntent == m_RoomTaskIntent &&
        m_WorkRoomTaskActionId == m_RoomTaskActionId &&
        m_ChatSettings?.m_ChatModel?.SupportsRoomTaskMessages == true;

    private bool IsWorkSceneFeedbackGuard(int generation) =>
        generation == m_WorkSceneFeedbackGuardGeneration &&
        (ShouldGuardWorkSceneFeedback(generation) || generation == m_WorkRoomStepReplyGeneration);

    private string BuildWorkSceneFeedbackContext()
    {
        var scope = new JObject {
            ["source"] = "same-user-work-spatial-result-feedback",
            ["workEpoch"] = m_WorkEpoch,
            ["intendedAction"] = m_WorkRoomTaskIntent,
            ["actionId"] = m_WorkRoomTaskActionId,
            ["feedbackOnly"] = true,
            ["newSpatialActionSubmittedByThisFeedback"] = false,
            ["evidenceLimit"] = "Resolve failure proves only that the intended visible entity was not confirmed. " +
                "A selected object's missing registered capability does not prove the intended lamp is unplugged, unpowered, broken, or incapable. " +
                "Engine component enablement and runtime-device registration are not measurements of physical mains power or wiring. " +
                "Historical successful off/on results keep their own entity and time; another selected renderer cannot overwrite that identity. " +
                "runtimeDeviceControl describes the software interface separately from target confirmation, user authorization and actual execution. " +
                "No approved goal or no physical hand does not mean an enabled connected runtime control interface is absent. " +
                "Previously confirmed observations retain their original source and time even when the target is outside the current view."
        };
        return "\n[同一用户事项的空间执行结果续报；候选台词核对后才外放]\n" +
            scope.ToString(Newtonsoft.Json.Formatting.None) + "\n" + BuildRoomTaskReplyFacts() +
            "\n这次只是读取已有空间执行事实并汇报，没有重新提交开关、走近或手部操作。" +
            "只解释这次实际可证实的结果与限制；目标未确认或选中物件没有登记控制能力，不能推断目标灯具没电、没接电源、损坏或只是不可控摆件。" +
            "runtimeDeviceControl的接口与功能事实不等于本轮目标、授权或结果；接口已连接且功能开启时，不能因为没有手、目标未确认或尚未批准就说没有关灯能力，并让用户自己按开关或拔插头。" +
            "保留有真实来源的先前观察，当前没看到不等于从未看到；也不能把历史状态当成当前状态。" +
            "围绕用户原事项自然地说明这次实际卡点和尚未完成部分；确有语义缺口时问能补清它的具体问题，不重复已明确的信息，不索要逐步寻找指令。道歉、轻笑或安慰可以伴随说明，不能代替它；内部审核术语或泛泛的结果未确认也不能代替具体原因。" +
            "如果仍需重新定位/重新观察，可以明确说明尚未完成；不能用将会操作的承诺或普通motion标签冒充已提交的新设备操作。";
    }
}
