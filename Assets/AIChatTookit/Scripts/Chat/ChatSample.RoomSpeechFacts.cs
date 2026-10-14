using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NeEEvA.Perception;
using UnityEngine;

public partial class ChatSample
{
    [Flags] private enum RoomSpeechDependencies { None = 0, UserTarget = 1, SceneAction = 2, VisualObservation = 4, UserActivity = 8 }

    private sealed class RoomSpeechSnapshot
    {
        public ArdyRoomDialogueBridge bridge;
        public UnityEngine.Object controller, avatar;
        public Transform userHead;
        public bool targetTrackingValid;
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

    private bool TryGetPreparedRoomSpeechReviewObservation(int generation, out SceneVisionObservation observation, out string source)
    {
        if (!TryGetPreparedRoomSpeechObservation(generation, out observation, out source)) return false;
        // Recent production extraction + factual review took about five seconds.
        // Capture before paying for a review of pixels likely to expire while it
        // runs. This is only a preparation hint; post-review freshness still wins.
        float reserve = Mathf.Min(5f, Mathf.Max(0f, m_SceneObservationMaxAgeSeconds * 0.5f));
        float remaining = m_SceneObservationMaxAgeSeconds - (Time.realtimeSinceStartup - observation.CapturedAtRealtime);
        if (remaining >= reserve) return true;
        Debug.Log($"[Room/SpeechReviewView] generation={generation} source=refresh-before-expiry remaining={remaining:F3}s reserve={reserve:F3}s", this);
        observation = null; source = "capture-for-review-window"; return false;
    }

    private RoomSpeechDependencies ResolveRoomSpeechDependencies(string intent, string reviewedScope)
    {
        // The model classifies the claims, but cannot drop the actual movement's
        // mandatory execution dependencies. Older two-field reviewers stay strict.
        RoomSpeechDependencies required = intent == "approach" || intent == "follow" || intent == "stop-moving" || intent == "unresolved"
            ? RoomSpeechDependencies.UserTarget
            : IsSceneVisualIntent(intent) ? RoomSpeechDependencies.SceneAction : RoomSpeechDependencies.None;
        if (UseWorkResultReplyPacket(m_FormalResponseGeneration) &&
            (bool?)CurrentSpeechMovementRequirement(m_FormalResponseGeneration)?["required"] == true)
            required |= RoomSpeechDependencies.UserTarget;
        // The terminal device claim always depends on the actual goal proof,
        // even when the last step was none or the reviewer labels it conversational.
        if (m_SpatialGoalTerminalReplyGeneration == m_FormalResponseGeneration || CurrentUserTurnRequiresIndependentSpatialRequest())
            required |= RoomSpeechDependencies.SceneAction;
        // A no-action spatial answer and a whole-search result cannot be downgraded
        // to generic chatter by a reviewer choosing factScope=none.
        bool evidenceOnly = ReadRoomEvidenceReplyFacts(m_FormalResponseGeneration) != null || UseFocusedReplyPacket(m_FormalResponseGeneration) ||
            UseWorkResultReplyPacket(m_FormalResponseGeneration);
        if ((string)ReadAutonomousReplyPurpose(m_FormalResponseGeneration)?["evidenceFocus"] == "current-user-body")
            required |= RoomSpeechDependencies.UserTarget;
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

    private RoomSpeechDependencies ResolveReviewedRoomSpeechDependencies(string intent, string reviewedScope, JObject verdict)
    {
        var dependencies = ResolveRoomSpeechDependencies(intent, reviewedScope);
        var words = verdict?["movementDependencies"] as JObject;
        if ((dependencies & RoomSpeechDependencies.UserTarget) != 0 &&
            (string)verdict?["verdict"] == "consistent" &&
            (string)words?["source"] == "independent-public-words" &&
            (bool?)words["currentGeometryRequired"] == false &&
            (string)words["currentGeometryQuote"] == "" && !string.IsNullOrWhiteSpace((string)words["ongoingActivityQuote"]) &&
            new[] { "preparing", "moving", "recovering", "replanning", "returning" }.Contains((string)verdict["resultReport"]))
            dependencies = (dependencies & ~RoomSpeechDependencies.UserTarget) | RoomSpeechDependencies.UserActivity;
        return dependencies;
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
            controller = bridge != null ? bridge.Controller : null, avatar = bridge != null ? bridge.avatar : null,
            userHead = bridge != null ? bridge.userHead : null,
            targetTrackingValid = bridge != null && bridge.ReadCurrentTargetEvidence().userAnchorValid && bridge.ReadCurrentTargetEvidence().userTrackingValid,
            sceneEpoch = m_SceneActionEpoch, visionEpoch = m_SceneVisionEpoch,
            sceneAction = RoomSpeechSceneStamp(), observation = observation
        };

    private List<string> ChangedRoomSpeechFacts(RoomSpeechSnapshot before, RoomSpeechDependencies scope)
    {
        var changes = new List<string>();
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (before.bridge == null || bridge != before.bridge || !bridge.IsConnected) { changes.Add("roomBinding"); return changes; }
        if ((scope & (RoomSpeechDependencies.UserTarget | RoomSpeechDependencies.UserActivity)) != 0)
        {
            if (bridge.userHead != before.userHead || bridge.avatar != before.avatar || bridge.Controller != before.controller)
                changes.Add("userTaskBinding");
            var target = bridge.ReadCurrentTargetEvidence();
            if ((target.userAnchorValid && target.userTrackingValid) != before.targetTrackingValid) changes.Add("userTrackingValidity");
            if ((scope & RoomSpeechDependencies.UserTarget) != 0 &&
                bridge.ReadCurrentTargetEvidence().currentTargetRevision != before.targetRevision) changes.Add("userTargetRevision");
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
            // A different body operation does not revoke an already observed or
            // performed scene result. Its own action/proof stamps above remain
            // mandatory; current movement and pixels have separate dependencies.
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
            case "我已经停止移动了，但后续动作的信息超出规划预算，还没有开始执行。": return ja ? "移動は止めたわ。ただ、続きの動作は情報量が計画の処理枠を超えて、まだ始められていないの。" : "I stopped moving, but the remaining actions exceeded the planning budget and have not started.";
            case "我已经停止移动了，但后续动作的规划请求失败，还没有开始执行。": return ja ? "移動は止めたわ。ただ、続きの動作を計画するリクエストが失敗して、まだ始められていないの。" : "I stopped moving, but the request to plan the remaining actions failed, so they have not started.";
            case "我已经停止移动了，但后续没有得到可执行的动作计划，还没有开始执行。": return ja ? "移動は止めたわ。ただ、続きの動作には実行できる計画が得られず、まだ始められていないの。" : "I stopped moving, but no executable plan was obtained for the remaining actions, so they have not started.";
            case "这次规划要处理的信息太多，没能生成动作计划，还没有开始新的动作。": return ja ? "動作を考えるための情報が多すぎて計画を作れなかったので、まだ新しい動作は始めていないわ。" : "There was too much information to process for this action plan, so I haven't started a new action.";
            case "这次没有得到可执行的动作计划，没有启动新的动作。": return ja ? "今回は実行できる動作計画が得られなかったので、新しい動作は始めていないわ。" : "I didn't obtain an executable action plan, so I haven't started a new action.";
            case "这次对新操作要求的核验没有通过，因此没有开始新操作。": return ja ? "今回の新しい操作の解釈を確認できなかったので、新しい操作は始めていないわ。" : "I couldn't validate my interpretation of the new operation request, so I haven't started a new operation.";
            case "我已经看向那里确认了一下。": return ja ? "そちらを向いて、見直したわ。" : "I've looked that way and checked the view.";
            case "我已经停止移动了。": return ja ? "移動を止めたわ。" : "I've stopped moving.";
            case "我已经走到你身边了。": return ja ? "あなたのそばまで来たわ。" : "I've reached your side.";
            case "我正在尝试走近你。": return ja ? "あなたのそばへ向かっているわ。" : "I'm trying to move closer to you.";
            case "我正在跟着你移动。": return ja ? "あなたについて移動しているわ。" : "I'm following you.";
            case "我已经接到跟随要求，正在准备行走动作，还没有开始走。": return ja ? "ついていく指示は受け取ったわ。歩く動作を準備していて、まだ歩き始めていないの。" : "I've received your follow request and am preparing to walk. I haven't started moving yet.";
            case "走近的请求已经提交，正在准备行走动作，还没有开始走。": return ja ? "そばへ行くリクエストは送ったわ。歩く動作を準備中で、まだ歩き始めていないの。" : "The approach request has been submitted. I'm preparing to walk and haven't started moving yet.";
            case "跟随任务仍然开启，行走暂时中断，系统正在尝试恢复，还没有恢复正常移动。": return ja ? "追従は有効なままだけれど、歩行が一時中断して復旧を試みているわ。まだ通常の移動には戻っていないの。" : "Following remains enabled, but walking was interrupted and recovery is in progress. Normal movement hasn't resumed yet.";
            case "当前位置没有通过导航检查，正在尝试有限的位置恢复，还没有完成原来的移动任务。": return ja ? "今の位置が移動経路の確認を通らず、位置の復旧を限られた範囲で試みているわ。元の移動はまだ完了していないの。" : "My current position failed navigation checks. A bounded position recovery is in progress; the original movement task is still incomplete.";
            case "跟随仍然开启，正在重新检查当前位置和路线，还没有恢复行走。": return ja ? "追従は有効なまま、今の位置と経路を確認し直しているわ。歩行はまだ再開していないの。" : "Following remains enabled while my position and route are checked again. Walking hasn't resumed yet.";
            case "现在的路线检查没有通过，我先停在这里，等位置变化后再确认。": return ja ? "今の経路は確認を通らなかったので、ここで一旦止まっているわ。位置が変わってから確認し直すの。" : "The current route failed its checks, so I'm paused here pending a position change and another check.";
            case "这段移动正在收尾并恢复姿态，还不能据此确认整个任务完成。": return ja ? "この移動を終えて姿勢を戻しているところよ。これだけではタスク全体の完了は確認できないわ。" : "This movement segment is winding down and my pose is returning. That alone doesn't confirm the whole task is complete.";
            case "这次移动已经取消。": return ja ? "今回の移動は取り消されたわ。" : "This movement was cancelled.";
            case "距离或朝向还需要调整，我正在重新检查路线，这次走近尚未完成。": return ja ? "距離や向きを調整する必要があるので、経路を確認し直しているわ。今回の移動はまだ完了していないの。" : "The distance or facing still needs adjustment. I'm checking the route again; this approach is incomplete.";
            case "这次有限的路线调整已用完，距离或朝向仍未满足。我先停下，这次走近还没有完成。": return ja ? "今回の経路調整は上限に達したけれど、必要な距離や向きにはまだ届いていないわ。いったん止まるね。今回の移動は未完了なの。" : "This attempt's route adjustments reached their limit, but the required distance or facing is still unmet. I've stopped; this approach is incomplete.";
            case "调整后的路线检查没有通过。我先停下，这次还没走到你身边。": return ja ? "調整後の経路の確認が通らなかったわ。いったん止まるね。まだそばには着いていないの。" : "The adjusted route did not pass the checks. I've stopped and haven't reached you yet.";
            case "这段行走已经结束，但没有达到要求的距离或朝向，这次走近还没有完成。": return ja ? "歩く動作は終わったけれど、必要な距離や向きにはまだ届いていないわ。今回の移動は未完了なの。" : "The walking segment ended, but the required distance or facing was not reached. This approach is still incomplete.";
            case "这次移动没有完成，路线或空间检查没有通过。": return ja ? "経路か空間の確認を通らず、今回の移動は完了していないわ。" : "This movement is incomplete because route or space checks failed.";
            case "这次移动请求已提交，但执行失败了，还没有完成。": return ja ? "移動リクエストは送ったけれど、実行に失敗して完了していないわ。" : "The movement request was submitted, but execution failed and it hasn't completed.";
            case "这次跟随请求已提交，但动作服务等待超时，跟随没有完成。": return ja ? "追従のリクエストは送ったけれど、動作サービスの待機が時間切れになり、追従は完了していないわ。" : "The follow request was submitted, but the motion service timed out. The follow task is incomplete.";
            case "跟随任务仍然开启，但当前行走状态尚未确认。": return ja ? "追従は有効だけれど、今の歩行状態はまだ確認できていないわ。" : "Following remains enabled, but the current walking state is unconfirmed.";
            case "当前移动请求已提交，但还不能确认执行进度。": return ja ? "今回の移動リクエストは送ったけれど、実行の進み具合はまだ確認できていないわ。" : "The current movement request was submitted, but its execution progress is unconfirmed.";
            case "这次跟随尚未继续执行，需要确认当前任务状态。": return ja ? "今回の追従が続いていることはまだ確認できていないわ。現在のタスク状態を確認する必要があるの。" : "I haven't confirmed that this follow task is continuing. Its current state needs to be checked.";
            case "房间移动目前没有连接，我还不能确认这次动作的结果。": return ja ? "部屋の移動機能が今は接続されていないので、この動作の結果を確認できないわ。" : "Room movement isn't connected, so I can't confirm this action's result.";
            case "这次还没有执行新的视觉目标动作。": return ja ? "今回は新しい視覚目標への動作をまだ実行していないわ。" : "I haven't performed a new visual-target action this turn.";
            case "灯具本来就处于你要求的状态，这次没有再次切换。": return ja ? "灯りは元から希望の状態だったので、今回は切り替えていないわ。" : "The lamp was already in the requested state, so I didn't switch it again.";
            case "灯具的运行状态已更新，我也重新看了一眼；房间原有的烘焙照明可能仍然保留。": return ja ? "灯りの動作状態を更新して見直したわ。部屋に焼き込まれた照明は残っているかもしれないの。" : "The lamp's runtime state was updated and I looked again. Baked room lighting may remain.";
            case "灯具状态已执行更新，但我还没有完成这次的确认。": return ja ? "灯りの状態は更新されたけれど、今回の確認はまだ完了していないわ。" : "The lamp's state was updated, but I haven't completed confirmation yet.";
            case "我正在尝试靠近刚才看到的目标。": return ja ? "さっき見えた目標に近づこうとしているわ。" : "I'm trying to approach the target I saw earlier.";
            case "我正在调整视向，等看清后再确认。": return ja ? "視線を調整しているわ。よく見えてから確認するの。" : "I'm adjusting my view and will confirm once I can see clearly.";
            case "这次还没完成，视觉目标或移动检查没有通过。": return ja ? "視覚目標か移動の確認を通らず、今回はまだ完了していないわ。" : "This isn't complete; visual-target or movement checks failed.";
            case "刚才的停止请求已经处理，当前动作状态又发生了变化。": return ja ? "停止のリクエストは処理したけれど、その後に動作状態が変わったわ。" : "The stop request was processed, and the action state has since changed.";
            case "我重新检查了现在的位置，附近有可用路线；这次还没有开始移动。": return ja ? "今の位置を調べ直して、近くに使える経路を確認したわ。今回はまだ移動を始めていないの。" : "I checked the current position again and found a usable nearby route. I haven't started moving this turn.";
            case "我重新检查了现在的位置，暂时还没有确认可以走近的路线。": return ja ? "今の位置を調べ直したけれど、近づける経路はまだ確認できていないわ。" : "I checked the current position again but haven't confirmed a route to approach.";
            case "位置发生了变化，我需要按现在的位置重新确认，不能沿用刚才的结果。": return ja ? "位置が変わったので、今の位置で確認し直す必要があるわ。さっきの結果はそのまま使えないの。" : "The position changed, so I need a current check; the earlier result no longer applies.";
            case "当前还有房间动作在执行，这次没有另外启动新的走近。": return ja ? "別の部屋の動作が実行中なので、今回は新しく近づく動作を始めていないわ。" : "Another room action is running; no separate new approach was started this turn.";
            case "这次还没有开始新的走近，我需要以现在的位置重新确认。": return ja ? "今回はまだ新しく近づき始めていないわ。今の位置で確認する必要があるの。" : "I haven't started a new approach this turn. The current position needs checking.";
            case "这段路线超过了我目前单次行走的时长限制，还没能走到你身边。": return ja ? "この経路は一回の歩行時間の上限を超えていて、まだそばへ行けていないわ。" : "This route exceeds my current single-walk duration limit, so I haven't reached your side.";
            case "这次还没能走到你身边，当前位置的路线或空间检查没有通过。": return ja ? "今の位置の経路か空間の確認を通らず、まだそばへ行けていないわ。" : "I haven't reached your side; route or space checks at the current position failed.";
            case "这次走近还没有完成，我不能把它当作已经成功。": return ja ? "今回の接近はまだ完了していないので、成功したとは言えないわ。" : "This approach is still incomplete, so I can't report success.";
            case "我还没有确认当前画面中的情况。": return ja ? "今の画面の状況はまだ確認できていないわ。" : "I haven't confirmed the current view yet.";
            case "我现在停着等待，跟随仍然开启，会根据你的位置继续调整。": return ja ? "今は止まって待っているわ。追従は有効なままで、あなたの位置に合わせて動くの。" : "I'm stopped and waiting. Following remains enabled and will adjust to your position.";
            default: return ja ? "今の状態をまだ確認できていないわ。できたとは言えないの。" : "I haven't confirmed the current result yet, so I can't say it's completed.";
        }
    }
}
