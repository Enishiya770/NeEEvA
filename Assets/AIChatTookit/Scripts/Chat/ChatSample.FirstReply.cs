using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    [SerializeField] private bool m_EnableFirstReplyRouting = true;
    private int m_FirstReplyAttemptGeneration = -1;
    private int m_FirstReplyActiveGeneration = -1;
    private LLM m_FirstReplyActiveModel;
    private JObject m_FirstReplyRequestSemantics;
    private LLM m_FirstReplyRequestSemanticsModel;

    // A request interpretation is bound to the complete accepted human turn.
    // It constrains routing/reporting only; it never resumes or authorizes work.
    private JObject ReadCurrentRequestSemantics(int generation)
    {
        var value = m_FirstReplyRequestSemantics;
        if (value == null || generation != m_FormalResponseGeneration ||
            (int?)value["generation"] != generation || (int?)value["workEpoch"] != m_WorkEpoch ||
            (int?)value["userTurn"] != m_ActionConstraints.CurrentUserTurn ||
            (string)value["currentUserText"] != m_ActionConstraints.CurrentUserText ||
            !ReferenceEquals(m_FirstReplyRequestSemanticsModel, m_ChatSettings?.m_ChatModel) ||
            !isActiveAndEnabled || m_SceneTimePaused || m_UserSpeechActiveForAutonomy) return null;
        return (JObject)value.DeepClone();
    }

    private bool CurrentRequestRequiresActionOrControl(int generation) =>
        (bool?)ReadCurrentRequestSemantics(generation)?["requiresActionOrControl"] == true;

    private JObject ReadCompactCurrentRequestSemantics(int generation)
    {
        var current = ReadCurrentRequestSemantics(generation);
        return current == null ? null : PickRoomFields(current, "source", "generation", "workEpoch", "userTurn",
            "requestKind", "planningFocus", "questionUnderstanding", "currentTurnEvidence", "requiresActionOrControl", "effectiveRoute", "actionAuthorization");
    }

    private void RememberFirstReplyRequestSemantics(FirstReplyCompletion result, int generation, LLM model)
    {
        m_FirstReplyRequestSemantics = null;
        m_FirstReplyRequestSemanticsModel = null;
        if (result?.Succeeded != true || !FirstReplyProtocol.IsRequestKind(result.RequestKind)) return;
        m_FirstReplyRequestSemantics = new JObject {
            ["source"] = "independent-first-reply-current-request-review",
            ["generation"] = generation, ["workEpoch"] = m_WorkEpoch,
            ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
            ["currentUserText"] = m_ActionConstraints.CurrentUserText,
            ["requestKind"] = result.RequestKind,
            ["questionUnderstanding"] = result.QuestionUnderstanding?.DeepClone(),
            ["planningFocus"] = new JObject { ["kind"] = result.PlanningFocus ?? "general",
                ["currentUserText"] = result.PlanningFocus == "current-user-body" || result.PlanningFocus == "current-view" ? new JValue(m_ActionConstraints.CurrentUserText) : JValue.CreateNull() },
            ["currentTurnEvidence"] = result.CurrentTurnEvidence?.DeepClone(),
            ["responseDemand"] = result.ResponseDemand,
            ["requiresActionOrControl"] = FirstReplyProtocol.RequiresActionOrControl(result.RequestKind),
            ["effectiveRoute"] = result.Disposition.ToString().ToLowerInvariant(),
            ["actionAuthorization"] = false,
            ["humanInput"] = BuildCommittedReplyHumanInput(generation),
            ["scope"] = "Model interpretation of this exact complete human turn, not a user quote, execution fact, target relation or action permission. Original input and downstream authorization checks remain authoritative."
        };
        m_FirstReplyRequestSemanticsModel = model;
        m_WorkRequestSemantics = (JObject)m_FirstReplyRequestSemantics.DeepClone();
        m_WorkRequestSemanticsModel = model;
        if (result.Disposition == FirstReplyDisposition.Evidence) ScheduleReadOnlyWorkRecheck(generation);
    }
    // The player owns failure completion even before its first PCM arrives.
    // IsAISpeaking is deliberately false during synthesis, so it cannot express
    // this ownership. The generation prevents late work from closing a new turn.
    private int m_FirstReplyFailureDrainGeneration = -1;

    // The provider reviews an ordinary candidate before releasing any speech.
    // The role chooses ordinary/evidence/tools. No word list in the application decides
    // whether a negated request, reference or conversation needs an action.
    private bool TryDispatchFirstReply(string prompt, string imageUrl, int generation,
        bool recordHistory, string transientContext)
    {
        var model = m_ChatSettings?.m_ChatModel;
        if (model != null) model.OngoingSpeechMonitoring = ActivityMonitoringAvailable;
        if (!m_EnableFirstReplyRouting || model == null || !model.SupportsFirstReplyRouting ||
            m_FirstReplyAttemptGeneration == generation || m_AgentCurrentRoundIsTick ||
            m_HoldSpeechForSongMemoryResult || m_HoldSpeechForHumBackResult ||
            m_UserSpeechActiveForAutonomy) return false;
        m_FirstReplyAttemptGeneration = generation;
        int epoch = m_WorkEpoch;
        bool consumed = false;
        bool publicDeltaDelivered = false;
        bool Current() => !consumed && generation == m_FormalResponseGeneration &&
            epoch == m_WorkEpoch && isActiveAndEnabled && !m_SceneTimePaused && !m_UserSpeechActiveForAutonomy &&
            ReferenceEquals(model, m_ChatSettings?.m_ChatModel);

        PublishSpokenPrefixToLlm();
        m_FormalResponseInFlight = true;
        m_FirstReplyActiveGeneration = generation;
        m_FirstReplyActiveModel = model;
        var started = Time.realtimeSinceStartup;
        Debug.Log($"[FirstReply/Dispatch] generation={generation} epoch={epoch} currentImage=false", this);
        model.PostFirstReplyStream(prompt,
            part => {
                if (!Current() || string.IsNullOrEmpty(part.Text)) return;
                if (!string.IsNullOrWhiteSpace(part.Text)) publicDeltaDelivered = true;
                OnSpeechStreamDelta(part);
            },
            result => {
                if (!Current()) return;
                consumed = true;
                m_FirstReplyActiveGeneration = -1;
                m_FirstReplyActiveModel = null;
                RememberFirstReplyRequestSemantics(result, generation, model);
                Debug.Log($"[FirstReply/Result] generation={generation} route={result?.Disposition.ToString() ?? "Failed"} " +
                    $"seconds={Time.realtimeSinceStartup-started:F3} publicDelta={result?.SpeechDelivered == true} failure={result?.Failure}", this);
                if (result?.Disposition == FirstReplyDisposition.Ordinary)
                {
                    // The provider returns public runs, never raw output. Project
                    // only this public text into the existing completion pipeline.
                    var channels = RoleOutputChannels.Parse(result.SafeOutput, RoleOutputBoundaryPolicy.ExplicitPublicSay);
                    if (!channels.HasInvalidPublicBoundary && !channels.HasActions && !channels.HasAnyActionTag &&
                        channels.HasSpeech && !channels.HasInvalidSpeechPhase && !channels.HasInvalidLanguage &&
                        !channels.HasUndeclaredSpeech && channels.SpeechDependency == SpeechActionDependency.Independent)
                    {
                        BindActivityOrdinaryHistory(generation, model);
                        m_FormalResponseInFlight = false;
                        // This narrow context did not consume the full observation
                        // tree. Pending tool facts stay pending for the normal path.
                        ClearStagedFormalObservation();
                        OnStreamComplete(channels.ToExecutableText(false, true));
                        return;
                    }
                    result = FirstReplyCompletion.Failed("first-reply-completion-boundary-rejected", result.SpeechDelivered);
                }
                if (!publicDeltaDelivered && result?.SpeechDelivered != true)
                {
                    if(result == null || result.Disposition == FirstReplyDisposition.Failed) {
                        // An unchecked classifier is not a request for tools or
                        // a whole-scene planner. Its bounded recovery already
                        // ran in the provider. Keep the accepted turn and expose
                        // this failure without granting or replaying any action.
                        m_FormalResponseInFlight=false;
                        ClearStagedFormalObservation();
                        string language=SpatialGoalReportLanguage(null);
                        RecordFirstReplyFailure(generation,epoch,result?.Failure??"missing-completion");
                        string message=FirstReplyFailureText(m_FirstReplyFailure,language);
                        m_PendingToolCorrectionFrame="[首答分类未完成] "+(result?.Failure??"missing-completion")+
                            "；本轮未由此派发新动作，原用户输入与任务记录保留。这不是执行失败、取消或任务完成。";
                        Debug.LogWarning("[FirstReply/Unresolved] "+new JObject{["generation"]=generation,["workEpoch"]=epoch,
                            ["reason"]=result?.Failure,["actionAuthorized"]=false,["scenePlannerDispatched"]=false,
                            ["scope"]="Retained accepted human turn; failed classification is not an ordinary/tools verdict."}.ToString(Formatting.None),this);
                        if(recordHistory)model.m_DataList.Add(new LLM.SendData("assistant",message));
                        BindActivityOrdinaryHistory(generation,model);
                        OnSpeechStreamDelta(new SpeechText(message,language,"first-reply-unresolved",SpeechActionDependency.Independent));
                        OnStreamComplete(message);return;
                    }
                    if (result?.Disposition == FirstReplyDisposition.Evidence)
                    {
                        // This reviewed handoff only reads facts. It cannot approve,
                        // revise or execute a pending spatial goal or a new command.
                        DispatchEvidenceReply(prompt, imageUrl, generation, recordHistory, transientContext, result.QuestionUnderstanding);
                        return;
                    }
                    // The same original user turn enters the existing planner once.
                    // The provider commits neither a tools route nor a failed probe.
                    DispatchFormalStream(prompt, imageUrl, generation, recordHistory, transientContext);
                    return;
                }

                // A broken stream after public deltas is not a silent route probe.
                // Drop the unplayed queue, but let the already validated sentence
                // owned by the TTS player drain. Marking this round silenced makes
                // both players exit before their normal ownership-release path;
                // OnStreamComplete then sees IsAISpeaking=true and waits forever.
                // This does not accept the malformed completion or replay tools.
                m_FirstReplyFailureDrainGeneration = generation;
                m_SentenceBuffer.Length = 0;
                ClearPendingSpeechChunks("queue-cleared");
                ClearPendingSpeechClips("queue-cleared");
                m_FormalResponseInFlight = false;
                m_PendingToolCorrectionFrame = "[首答流未正常完成] " + (result?.Failure ?? "missing-completion") +
                    "；本轮未执行工具，未提交完整答复。到错误发生前已经完整播放的句子：" + m_AssistantHeardText +
                    "。当前已验证句子可能仍在合成或播放，之后以实际播放记录为准；排队的剩余内容已撤销。" +
                    "后续应依据本轮原话和事实处理未完成事项，不要重复已听前缀或宣称动作完成。";
                Debug.LogWarning("[FirstReply/Incomplete] " + m_PendingToolCorrectionFrame, this);
                OnStreamComplete("<silent/>");
            },
            imageDataUrl: null, transientContext: BuildFirstReplyContext(transientContext), commitHistory: recordHistory);
        return true;
    }

    // The first reply has no room-speech owner yet. Pausing must revoke this
    // owner and finish the round, not merely make its late callbacks inert.
    private void CancelFirstReplyForPause()
    {
        int owner = m_FirstReplyActiveGeneration;
        var model = m_FirstReplyActiveModel;
        m_FirstReplyActiveGeneration = -1;
        m_FirstReplyActiveModel = null;
        if (owner < 0 || owner != m_FormalResponseGeneration) return;
        InvalidateFormalResponse("first-reply-application-paused");
        if (model != null && !ReferenceEquals(model, m_ChatSettings?.m_ChatModel)) model.CancelActiveResponse();
        m_SentenceBuffer.Length = 0;
        ClearPendingSpeechChunks("queue-cleared");
        ClearPendingSpeechClips("queue-cleared");
        m_StreamComplete = true;
        m_TTSSenderDone = true;
        m_AgentRoundInFlight = false;
        Debug.Log($"[FirstReply/Cancelled] generation={owner} reason=application-paused", this);
    }

    private string BuildFirstReplyContext(string transientContext)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var room = ReadCompactRoomTaskFacts(bridge);
        var facts = new JObject {
            ["source"] = "current-runtime-summary-for-first-reply",
            ["observedUtc"] = DateTime.UtcNow.ToString("O"),
            ["generation"] = m_FormalResponseGeneration,
            ["workEpoch"] = m_WorkEpoch, ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
            ["humanInput"] = BuildCommittedReplyHumanInput(m_FormalResponseGeneration),
            ["work"] = new JObject {
                ["open"] = m_WorkReviewOpen, ["status"] = m_WorkStatus,
                ["toolInFlight"] = HasWorkToolInFlight(),
                ["hasUnreadExecutionDetails"] = !string.IsNullOrEmpty(m_PendingSelfInspection) ||
                    !string.IsNullOrEmpty(m_PendingToolCorrectionFrame) || m_SongSearchResultPending ||
                    m_SongCatalogResultPending || m_SongMemoryResultPending || m_HumBackResultPending
            },
            ["previousUnfinishedWork"] = TaskContinuationContextPolicy.RoutingHistory(m_PreviousUnfinishedWork),
            ["previousRequestFailure"] = ReadPreviousRoomPlanningFailure(),
            ["interfaces"] = room["controlEntryPoints"],
            ["body"] = PickRoomFields(room["currentFacts"] as JObject,
                "connected", "taskMode", "followActive", "bodyReserved", "poseReturning",
                "observedAtRealtime", "currentlyNearAndFacingUser", "actualUserDistance",
                "actualFacingErrorDegrees", "activeActionId", "executionTargetKind"),
            ["lastActionResults"] = TaskContinuationContextPolicy.Outcome(room["lastActionResults"] as JObject),
            ["spatialGoal"] = PickRoomFields(BuildSpatialGoalContext(),
                "goalId", "status", "ownerCurrent", "originalRequest", "boundEntityId", "executionSuspended", "registered"),
            ["pendingObservationGoal"] = PickRoomFields(SceneObjectTargetContext()?["observationGoal"] as JObject,
                "goalId", "parentSpatialGoalId", "status", "originalRequest", "targetDescription", "question", "entityId"),
            ["voice"] = new JObject {
                ["ttsBound"] = m_ChatSettings?.m_TextToSpeech != null,
                ["singingEnabled"] = m_EnableAutonomousHumBack,
                ["singingActive"] = IsSingingRuntimeActivityActive(),
                ["singingRequestSubmittedThisTurn"] = m_SingingRequestSubmittedSinceUserTurn,
                ["serviceReachableLastObserved"] = m_ObservedSvcReachable,
                ["serviceObservationAgeSeconds"] = m_ObservedSvcHealthAt < 0f ? (float?)null : Time.realtimeSinceStartup - m_ObservedSvcHealthAt
            },
            ["vision"] = new JObject {
                ["eyesOpen"] = m_AgentEyesOpen, ["sceneVisionEnabled"] = m_EnableScreenVision,
                ["currentImageDeliveredInThisRequest"] = false,
                ["scope"] = "本首答通道不发送图像；旧图和历史描述不证明现在看见。只需依据当前所见/空间记忆或已有执行故障事实回答时选evidence，后续只读通道会提供已有或原位新鲜观察，不派发转头移动。实际要求换视角搜索或操作仍选tools。"
            },
            ["scope"] = "这里只是当前接口和任务摘要，不是完整执行/素材/视觉证据。null=未观测，不是失败。" +
                "普通对话可直接回答；需要图像、空间记忆或已有执行故障详情的纯问答选evidence；任何新动作、唱歌、读写或真正需要操作工具获取新结果仍选tools。" +
                "已有上述可靠的同目标结果或用户距离/朝向摘要足以回答时，可以确认已知状态，不重复执行；旧台词不是动作证据。" +
                "spatialGoal/pendingObservationGoal只说明已有任务；确认继续取消修改它们属于权限或控制更新，须走tools原审核，不能由evidence回答自动生效。单纯询问状态/方向/原因不更新目标。" +
                "已加载技能或上轮歌唱不代表本轮必须使用；当前原话的否定/纠错/限定优先。"
        };
        // Keep the producer typed end-to-end: the independent gate reads
        // humanInput.primaryUtterance separately from acoustic/background text.
        if (!string.IsNullOrWhiteSpace(transientContext)) facts["additionalContext"] = transientContext;
        return facts.ToString(Formatting.None);
    }
}
