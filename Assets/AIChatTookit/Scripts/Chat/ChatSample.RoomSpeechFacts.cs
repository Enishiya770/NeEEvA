using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NeEEvA.Perception;
using UnityEngine;

public partial class ChatSample
{
    [Flags] private enum RoomSpeechDependencies { None = 0, UserTarget = 1, SceneAction = 2, VisualObservation = 4 }

    private sealed class RoomSpeechSnapshot
    {
        public ArdyRoomDialogueBridge bridge;
        public int controllerOperation, targetRevision, sceneEpoch, visionEpoch;
        public string actionId, phase;
        public JObject sceneAction;
        public SceneVisionObservation observation;
    }

    private bool TryGetPreparedRoomSpeechObservation(int generation, out SceneVisionObservation observation, out string source)
    {
        observation = null; source = "unavailable";
        if (generation != m_FormalResponseGeneration || !SceneEyesOpen || m_SceneVisionCapture == null) return false;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (bridge == null || bridge.avatar == null || m_SceneVisionCapture.Target != bridge.avatar) return false;
        var bound = GetFormalSceneObservation(generation);
        var latest = m_SceneVisionCapture.LatestObservation;
        // The cache is not an independent cross-round observation authority.
        // Reuse only a current-source frame at least as new as this round's
        // identified image, or the exact current action's real result image.
        bool currentActionImage = m_SceneActionFact != null && m_SceneActionFact.actionId == m_RoomTaskActionId &&
            !string.IsNullOrEmpty(m_RoomTaskActionId) && latest?.CaptureId == m_SceneActionFact.resultCaptureId;
        bool followsRoundImage = bound?.HasImage == true && latest != null &&
            latest.CapturedAtRealtime >= bound.CapturedAtRealtime;
        if ((followsRoundImage || currentActionImage) && IsFreshSceneObservation(latest))
        {
            observation = latest;
            source = latest.CaptureId == bound?.CaptureId ? "formal-current" : "existing-current-frame";
            return true;
        }
        if (!IsFreshSceneObservation(bound)) return false;
        observation = bound; source = "formal-current"; return true;
    }

    private RoomSpeechDependencies ResolveRoomSpeechDependencies(string intent, string reviewedScope)
    {
        // The model classifies the claims, but cannot drop the actual movement's
        // mandatory execution dependencies. Older two-field reviewers stay strict.
        RoomSpeechDependencies required = intent == "approach" || intent == "follow" || intent == "stop-moving" || intent == "unresolved"
            ? RoomSpeechDependencies.UserTarget
            : IsSceneVisualIntent(intent) ? RoomSpeechDependencies.SceneAction : RoomSpeechDependencies.None;
        // The terminal device claim always depends on the actual goal proof,
        // even when the last step was none or the reviewer labels it conversational.
        if (m_SpatialGoalTerminalReplyGeneration == m_FormalResponseGeneration || CurrentUserTurnRequiresIndependentSpatialRequest())
            required |= RoomSpeechDependencies.SceneAction;
        // A no-action spatial answer and a whole-search result cannot be downgraded
        // to generic chatter by a reviewer choosing factScope=none.
        bool evidenceOnly = ReadRoomEvidenceReplyFacts(m_FormalResponseGeneration) != null;
        if ((m_RoomTaskSpatialAnswer && !evidenceOnly) || intent == "search-scene") required |= RoomSpeechDependencies.SceneAction;
        if (m_RoomTaskSpatialAnswer && !evidenceOnly && SceneEyesOpen) required |= RoomSpeechDependencies.VisualObservation;
        if (reviewedScope == "legacy")
            return required | (intent == "inspect" ? RoomSpeechDependencies.UserTarget : RoomSpeechDependencies.None);
        switch (reviewedScope)
        {
            case "user-target": return required | RoomSpeechDependencies.UserTarget;
            case "scene-action": return required | RoomSpeechDependencies.SceneAction;
            case "visual-observation": return required | RoomSpeechDependencies.VisualObservation;
            case "combined": return required | RoomSpeechDependencies.UserTarget | RoomSpeechDependencies.SceneAction | RoomSpeechDependencies.VisualObservation;
            default: return required;
        }
    }

    private JObject RoomSpeechSceneStamp()
    {
        var result = PickRoomFields(BuildSceneActionFacts()["lastAction"] as JObject,
            "actionId", "intent", "captureId", "confirmedCaptureId", "resultCaptureId", "surfaceId", "phase", "active", "reached", "reason",
            "entityId", "focusKind", "objectConfirmed", "stateChanged", "requestedEntityId", "requestedState", "requestedImagePoint", "targetResolutionSource",
            "targetPurpose", "targetDescription", "targetMatchVerdict", "targetMatchReason", "matchCaptureId", "semanticTargetConfirmed",
            "needsCloserObservation", "groundArrived", "objectReobserved", "postArrivalObservationStatus", "observationGoalId",
            "viewDirection", "viewPurpose", "viewPurposeStatus", "requestedViewDistance", "actualViewOffset", "viewBefore", "viewAfter");
        result["objectStateDependency"] = SceneObjectSpeechStamp();
        result["sceneSearchDependency"] = PickRoomFields(BuildSceneSearchContext(),
            "taskId", "status", "ownerCurrent", "generation", "mode", "originalUserRequest", "foundEntityId",
            "foundCaptureId", "boundEntityId", "deviceGoalId", "reason", "steps", "decisions", "searchAttempted",
            "candidatePageCoverage", "coveredHorizontalRegions", "allVisitedCandidatePagesReviewed");
        if (m_SpatialGoalTerminalReplyGeneration == m_FormalResponseGeneration || CurrentUserTurnRequiresIndependentSpatialRequest())
        {
            // An inspect can satisfy the goal without changing m_SceneResultDevice.
            // Read the owner-bound goal proof as well as the last operated device.
            // BuildSpatialGoalContext only revokes stale proof; it never grants
            // perception or completion from a hidden live state read.
            result["spatialGoalCompletionDependency"] = SpatialGoalCompletionSpeechStamp();
        }
        return result;
    }

    private JObject SpatialGoalCompletionSpeechStamp()
    {
        var stamp=PickRoomFields(BuildSpatialGoalContext(), "goalId", "status", "ownerCurrent", "reason",
            "executionRevision", "executionSuspended", "latestUserText", "boundEntityId", "desiredState", "completionEvidenceValid", "completionEvidence");
        stamp["pendingClarification"]=PickRoomFields(BuildPendingDeviceClarificationContext(),
            "pendingId", "status", "ownerCurrent", "latestUserText", "lastReviewedUserTurn");
        stamp["latestGoalReview"]=PickRoomFields(m_SpatialGoalLatestReview,
            "generation", "verdict", "reason", "registered", "relation", "pendingId",
            "reviewApplied", "cancellationApplied", "currentRequestOnly", "pendingRetained", "requestAssessment",
            "proposalDiscarded", "answerRequired", "responseMode");
        return stamp;
    }

    private RoomSpeechSnapshot CaptureRoomSpeechSnapshot(ArdyRoomDialogueBridge bridge, SceneVisionObservation observation)
        => new RoomSpeechSnapshot {
            bridge = bridge, controllerOperation = bridge?.Controller != null ? bridge.Controller.OperationRevision : -1,
            targetRevision = bridge != null ? bridge.ReadCurrentTargetEvidence().currentTargetRevision : -1,
            actionId = bridge != null ? bridge.ActionId : "", phase = bridge != null ? bridge.Phase : "disconnected",
            sceneEpoch = m_SceneActionEpoch, visionEpoch = m_SceneVisionEpoch,
            sceneAction = RoomSpeechSceneStamp(), observation = observation
        };

    private List<string> ChangedRoomSpeechFacts(RoomSpeechSnapshot before, RoomSpeechDependencies scope)
    {
        var changes = new List<string>();
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (before.bridge == null || bridge != before.bridge || !bridge.IsConnected) { changes.Add("roomBinding"); return changes; }
        if ((scope & RoomSpeechDependencies.UserTarget) != 0)
        {
            if (bridge.ReadCurrentTargetEvidence().currentTargetRevision != before.targetRevision) changes.Add("userTargetRevision");
            if (bridge.ActionId != before.actionId) changes.Add("userTaskActionId");
            if (bridge.Phase != before.phase) changes.Add("userTaskPhase");
            if (bridge.Controller == null || bridge.Controller.OperationRevision != before.controllerOperation) changes.Add("userTaskOperation");
        }
        if ((scope & RoomSpeechDependencies.SceneAction) != 0)
        {
            if (m_SceneActionEpoch != before.sceneEpoch) changes.Add("sceneActionEpoch");
            var current = RoomSpeechSceneStamp();
            if (!JToken.DeepEquals(before.sceneAction, current)) changes.Add("sceneActionResult");
            if (!JToken.DeepEquals(before.sceneAction?["spatialGoalCompletionDependency"], current["spatialGoalCompletionDependency"]))
                changes.Add("spatialGoalCompletion");
            if (bridge.Controller == null || bridge.Controller.OperationRevision != before.controllerOperation) changes.Add("bodyOperation");
        }
        if ((scope & RoomSpeechDependencies.VisualObservation) != 0)
        {
            if (m_SceneVisionEpoch != before.visionEpoch) changes.Add("visionEpoch");
            if (before.observation?.HasImage != true) changes.Add("noVisualEvidence");
            else if (!IsFreshSceneObservation(before.observation)) changes.Add("visualEvidenceExpiredOrViewChanged");
        }
        return changes;
    }

    private static string RoomFallbackLanguage(string language) => language == "ja" || language == "en" ? language : "zh";

    private static string LocalizeRoomFallback(string text, string language)
    {
        if (language == "zh") return text;
        // A failed audit needs a small factual acknowledgement, not improvised
        // details about another task. Preserve the candidate's supported language.
        bool ja = language == "ja";
        switch (text)
        {
            case "我暂时拿不到可靠的当前场景信息，不能确认眼前的情况。": return ja ? "今は確かな場面の情報を取得できなくて、目の前の様子を確認できないわ。" : "I can't access reliable scene information right now, so I can't confirm the current view.";
            case "这次我没能准确回答你的问题。": return ja ? "今回は、あなたの質問にきちんと答えられなかったわ。" : "I wasn't able to answer your question accurately this time.";
            case "我还不能根据已有观察确认你问的空间情况，这次没有执行新的移动或搜索。": return ja ? "今までの観察だけでは、その位置や状況をまだ確かめられないわ。今回は新しく移動したり探したりしていないの。" : "I can't confirm that spatial detail from the observations I have. I haven't moved or started a new search this turn.";
            case "这次已经确认并重新看见了你要找的目标；这不代表执行了设备操作。": return ja ? "探していた対象を確認して、新しい視界でも見直せたわ。機器の操作をしたという意味ではないの。" : "I confirmed the target and saw it again in a fresh view. That does not mean I operated the device.";
            case "这次环顾已完成程序记录的观察范围；遮挡或未能辨认的内容仍不能确认。": return ja ? "今回記録された範囲の見回しは終わったわ。隠れたものや見分けられなかったものは、まだ確認できていないの。" : "This survey completed its recorded observation coverage. Occluded or unidentified contents remain unconfirmed.";
            case "这次搜索已经中止，还不能确认整个寻找任务完成。": return ja ? "今回の探索は中止したわ。探す作業が完了したとはまだ言えないの。" : "This search was stopped. I haven't confirmed that the whole search task is complete.";
            case "这次搜索还没有确认整个目标或观察任务完成，不能仅凭看过的范围说目标不存在。": return ja ? "今回の探索では、対象の確認や観察全体の完了にはまだ至っていないわ。見た範囲だけで対象がないとは言えないの。" : "This search has not confirmed the target or completed the whole observation task. The checked area does not prove the target is absent.";
            case "这次还没有完成有效搜索，目标和观察范围仍未确认。": return ja ? "今回はまだ有効な探索ができていないわ。対象と観察範囲は未確認なの。" : "I haven't completed a valid search this time. The target and observation coverage remain unconfirmed.";
            case "这次还没有执行新的动作。": return ja ? "今回はまだ、新しい動作は実行していないわ。" : "I haven't performed a new action this time.";
            case "这盏灯当前的开关状态已符合你的要求。": return ja ? "この灯りは今、あなたが望んだスイッチの状態になっているわ。" : "This lamp's current switch state matches what you requested.";
            case "这次还没有对那盏灯执行开关操作。": return ja ? "今回はまだ、その灯りを操作していないわ。" : "I haven't operated that lamp this time.";
            case "我还没有确认这次灯的操作已经完成，需要重新检查目标和当前状态。": return ja ? "今回の灯りの操作が完了したか、まだ確かめられていないわ。対象と今の状態をもう一度確認する必要があるの。" : "I haven't confirmed that the lamp task is complete. I need to check the target and its current state again.";
            case "我还没确认这次设备任务的最终结果，不能把它说成已经完成。": return ja ? "今回の機器操作の最終結果はまだ確認できていないから、完了したとは言えないわ。" : "I haven't confirmed the final result of this device task, so I can't say it is complete.";
            case "这次还没在画面里确认到你指的物件，因此没有执行开关。": return ja ? "今の視界では、あなたが指している物をまだ確かめられていないから、スイッチは操作していないわ。" : "I haven't confirmed the object you mean in this view, so I haven't switched it.";
            case "这次选中的物件没有可用开关接口，还不能确认选中了你说的那个物件。": return ja ? "今回選んだ物には使えるスイッチ操作がないわ。あなたが言っている物を正しく選べたか、まだ確認できていないの。" : "The object selected this time has no available switch control. I haven't confirmed that it is the object you meant.";
            case "我还没在当前房间确认这个物件的身份，需要重新看清。": return ja ? "この部屋でその物をまだ特定できていないから、もう一度よく見る必要があるわ。" : "I haven't identified that object in this room yet and need to look again.";
            case "我离这个物件太远，这次没有执行开关。": return ja ? "その物から離れすぎているから、今回はスイッチを操作していないわ。" : "I'm too far from that object, so I haven't operated its switch.";
            case "刚才的画面已不适合确认目标，我需要重新看一下。": return ja ? "さっきの視界では今の目標を確かめられないから、もう一度見る必要があるわ。" : "The earlier view is no longer suitable for confirming the target. I need to look again.";
            case "我已经走到那个目标附近的可站位置了。": return ja ? "あの目標の近くまで移動できたわ。" : "I've reached a standing position near that target.";
            case "我已经调整了观察位置，还需要确认这个角度能否看清目标。": return ja ? "見る位置を変えたわ。この角度で目標がよく見えるか、まだ確かめる必要があるの。" : "I've adjusted my viewing position. I still need to check whether I can see the target clearly from here.";
            case "我正在尝试调整观察位置，之后再确认能否看清。": return ja ? "見る位置を変えようとしているわ。そのあと、よく見えるか確かめるね。" : "I'm trying to adjust my viewing position, then I'll check whether the view is clearer.";
            case "这次生成动作计划的服务请求失败了，没有启动新的动作。": return ja ? "動作を計画するサービスへのリクエストが失敗したので、新しい動作は始めていないわ。" : "The request to plan the action failed, so I haven't started a new action.";
            case "这次规划要处理的信息太多，没能生成动作计划，还没有开始新的动作。": return ja ? "動作を考えるための情報が多すぎて計画を作れなかったので、まだ新しい動作は始めていないわ。" : "There was too much information to process for this action plan, so I haven't started a new action.";
            case "这次没有得到可执行的动作计划，没有启动新的动作。": return ja ? "今回は実行できる動作計画が得られなかったので、新しい動作は始めていないわ。" : "I didn't obtain an executable action plan, so I haven't started a new action.";
            case "我已经看向那里确认了一下。": return ja ? "そちらを向いて、見直したわ。" : "I've looked that way and checked the view.";
            case "我已经停止移动了。": return ja ? "移動を止めたわ。" : "I've stopped moving.";
            case "我已经走到你身边了。": return ja ? "あなたのそばまで来たわ。" : "I've reached your side.";
            case "我正在尝试走近你。": return ja ? "あなたのそばへ向かっているわ。" : "I'm trying to move closer to you.";
            case "我正在跟着你移动。": return ja ? "あなたについて移動しているわ。" : "I'm following you.";
            case "我在你附近，会继续跟着你。": return ja ? "近くで待っているわ。また動いたらついていくね。" : "I'm waiting nearby and will keep following when you move.";
            default: return ja ? "今の状態をまだ確認できていないわ。できたとは言えないの。" : "I haven't confirmed the current result yet, so I can't say it's completed.";
        }
    }
}
