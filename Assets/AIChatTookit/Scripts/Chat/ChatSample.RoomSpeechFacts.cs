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
        public string activeTaskId, taskMode;
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

    private static RoomSpeechDependencies ResolveRoomSpeechDependencies(string intent, string reviewedScope)
    {
        // The model classifies the claims, but cannot drop the actual movement's
        // mandatory execution dependencies. Older two-field reviewers stay strict.
        RoomSpeechDependencies required = intent == "approach" || intent == "follow" || intent == "stop-moving" || intent == "unresolved"
            ? RoomSpeechDependencies.UserTarget
            : IsSceneVisualIntent(intent) ? RoomSpeechDependencies.SceneAction : RoomSpeechDependencies.None;
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
        // Verification captures renew in the background; only actual task/pose
        // changes invalidate a statement that the gaze is still being held.
        result["persistentGaze"] = PickRoomFields(BuildPersistentSceneAttentionFacts(),
            "active", "physicallyHeld", "actionId", "entityId", "state", "reason");
        return result;
    }

    private RoomSpeechSnapshot CaptureRoomSpeechSnapshot(ArdyRoomDialogueBridge bridge, SceneVisionObservation observation)
        => new RoomSpeechSnapshot {
            bridge = bridge, controllerOperation = bridge?.Controller != null ? bridge.Controller.OperationRevision : -1,
            targetRevision = bridge != null ? bridge.ReadCurrentTargetEvidence().currentTargetRevision : -1,
            actionId = bridge != null ? bridge.ActionId : "", phase = bridge != null ? bridge.Phase : "disconnected",
            activeTaskId = bridge != null ? bridge.ActiveTaskId : "", taskMode = bridge != null ? bridge.TaskMode : "none",
            sceneEpoch = m_SceneActionEpoch, visionEpoch = m_SceneVisionEpoch,
            sceneAction = RoomSpeechSceneStamp(), observation = observation
        };

    private bool HasCurrentRoomRequestReceipt(RoomSpeechSnapshot snapshot)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (snapshot == null || bridge == null || bridge != snapshot.bridge || !bridge.IsConnected ||
            string.IsNullOrEmpty(m_RoomTaskActionId) || snapshot.actionId != m_RoomTaskActionId ||
            snapshot.activeTaskId != m_RoomTaskActionId || bridge.ActionId != m_RoomTaskActionId ||
            bridge.ActiveTaskId != m_RoomTaskActionId || snapshot.taskMode != m_RoomTaskIntent ||
            bridge.TaskMode != m_RoomTaskIntent) return false;
        if (m_RoomTaskIntent == "follow" && m_RoomTaskDispatch != "follow-submitted" ||
            m_RoomTaskIntent == "approach" && m_RoomTaskDispatch != "attempt-submitted" ||
            m_RoomTaskIntent != "follow" && m_RoomTaskIntent != "approach") return false;
        // A receipt survives ordinary target revisions and newly generated steps,
        // but not a stopped, paused, failed, replaced or completed execution. This
        // never proves current motion, arrival, route feasibility or visual facts.
        bool Healthy(string phase) => phase == "preparing" || phase == "generating" ||
            phase == "moving" || m_RoomTaskIntent == "follow" && phase == "follow-waiting";
        return Healthy(snapshot.phase) && Healthy(bridge.Phase);
    }

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
            if (!JToken.DeepEquals(before.sceneAction, RoomSpeechSceneStamp())) changes.Add("sceneActionResult");
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
            case "这次还没在画面里确认到你指的物件，因此没有执行开关。": return ja ? "今の視界では、あなたが指している物をまだ確かめられていないから、スイッチは操作していないわ。" : "I haven't confirmed the object you mean in this view, so I haven't switched it.";
            case "这次选中的物件没有可用开关接口，还不能确认选中了你说的那个物件。": return ja ? "今回選んだ物には使えるスイッチ操作がないわ。あなたが言っている物を正しく選べたか、まだ確認できていないの。" : "The object selected this time has no available switch control. I haven't confirmed that it is the object you meant.";
            case "我还没在当前房间确认这个物件的身份，需要重新看清。": return ja ? "この部屋でその物をまだ特定できていないから、もう一度よく見る必要があるわ。" : "I haven't identified that object in this room yet and need to look again.";
            case "我离这个物件太远，这次没有执行开关。": return ja ? "その物から離れすぎているから、今回はスイッチを操作していないわ。" : "I'm too far from that object, so I haven't operated its switch.";
            case "刚才的画面已不适合确认目标，我需要重新看一下。": return ja ? "さっきの視界では今の目標を確かめられないから、もう一度見る必要があるわ。" : "The earlier view is no longer suitable for confirming the target. I need to look again.";
            case "我已经走到那个目标附近的可站位置了。": return ja ? "あの目標の近くまで移動できたわ。" : "I've reached a standing position near that target.";
            case "我已经调整了观察位置，还需要确认这个角度能否看清目标。": return ja ? "見る位置を変えたわ。この角度で目標がよく見えるか、まだ確かめる必要があるの。" : "I've adjusted my viewing position. I still need to check whether I can see the target clearly from here.";
            case "我正在尝试调整观察位置，之后再确认能否看清。": return ja ? "見る位置を変えようとしているわ。そのあと、よく見えるか確かめるね。" : "I'm trying to adjust my viewing position, then I'll check whether the view is clearer.";
            case "我还没确认这次的移动意图，暂时没有提交新的动作。": return ja ? "今回どう移動するかをまだ確認できていないから、新しい動作はまだ指示していないわ。" : "I haven't confirmed this movement yet, so I haven't submitted a new action.";
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
