using System;
using System.Collections.Generic;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private sealed class DeviceReceiptSpeechLease
    {
        public int Generation, Epoch, Round, SceneEpoch;
        public long OutputRevision;
        public string ActionId, EntityId, Phase;
        public SceneObjectLightSwitch Device;
        public JObject Result;
    }
    private readonly Dictionary<int, DeviceReceiptSpeechLease> m_DeviceReceiptSpeech =
        new Dictionary<int, DeviceReceiptSpeechLease>();

    // The model selects this narrow response mode while interpreting the complete
    // request. The engine supplies only the actual action receipt, never arbitrary
    // model prose or an inference that the viewing purpose has been achieved.
    private bool TryDeliverRoomActionReceipt(RoomTaskProtocol.Decision decision, int generation, bool recordHistory)
    {
        if (decision == null || !decision.RequestsActionReceipt || decision.origin != "user" ||
            m_AgentCurrentRoundIsTick || generation != m_FormalResponseGeneration ||
            m_RoomTaskGeneration != generation || m_RoomTaskIntent != decision.intent ||
            m_UserSpeechActiveForAutonomy || !isActiveAndEnabled || m_SceneTimePaused ||
            m_FormalTransportDispatchGeneration == generation) return false;
        string text = CurrentRoomActionReceipt(decision.replyLanguage);
        if (string.IsNullOrEmpty(text)) return false;
        var model = m_ChatSettings?.m_ChatModel;
        if (model == null) return false;
        RememberDeviceReceiptSpeech(generation, m_CurrentSpeechRoundId, m_FastLoop.OutputRevision);
        var observations = CaptureObservationReceipt();
        m_FormalTransportDispatchGeneration = generation;
        m_RoomSpeechGuardGeneration = -1;
        m_FormalResponseInFlight = false;
        if (recordHistory) model.m_DataList.Add(new LLM.SendData("assistant", text));
        m_ResponseTrace?.Child("room-action-receipt").Mark("speech.action_receipt_released", new JObject {
            ["generation"] = generation, ["actionId"] = m_RoomTaskActionId,
            ["intent"] = m_RoomTaskIntent, ["dispatch"] = m_RoomTaskDispatch,
            ["language"] = decision.replyLanguage,
            ["scope"] = "bounded-engine-receipt-selected-by-model; not free speech or proof of viewing-purpose completion",
            ["avoidedModelStages"] = new JArray("formal-speech-generation", "free-speech-review")
        });
        OnSpeechStreamDelta(new SpeechText(text, decision.replyLanguage, "room-action-receipt", SpeechActionDependency.Independent));
        AcknowledgeObservations(observations, generation, text);
        OnStreamComplete(text);
        return true;
    }

    private bool HasDefinitiveRoomActionFailure()
    {
        string dispatch = m_RoomTaskDispatch ?? "";
        if (m_RoomTaskIntent == "none") return false;
        if (CurrentRoomGazeReleased()) return true;
        if (dispatch.StartsWith("rejected:", StringComparison.Ordinal) ||
            dispatch.StartsWith("not-submitted:", StringComparison.Ordinal) ||
            dispatch == "decision-unavailable") return true;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (IsSceneVisualIntent(m_RoomTaskIntent))
            return m_SceneActionFact != null && !string.IsNullOrEmpty(m_RoomTaskActionId) &&
                m_SceneActionFact.actionId == m_RoomTaskActionId &&
                (m_SceneActionFact.phase == "rejected" || m_SceneActionFact.phase == "failed" ||
                 m_SceneActionFact.phase == "cancelled" || m_SceneActionFact.phase == "arrived-object-unconfirmed");
        return bridge != null && !string.IsNullOrEmpty(m_RoomTaskActionId) && bridge.ActionId == m_RoomTaskActionId &&
            (bridge.Phase == "failed" || bridge.Phase == "unreachable" || bridge.Phase == "follow-paused" ||
             bridge.Phase == "cancelled" || bridge.Phase == "unavailable" || bridge.Phase == "blocked" ||
             bridge.Phase == "goal-not-reached");
    }

    private bool CurrentRoomGazeReleased() => m_SceneActionFact != null &&
        !string.IsNullOrEmpty(m_RoomTaskActionId) && m_SceneActionFact.actionId == m_RoomTaskActionId &&
        m_SceneActionFact.gazeHold == "until-cancel" && m_PersistentSceneAttention != null &&
        m_PersistentSceneAttention.actionId == m_RoomTaskActionId && !m_PersistentSceneAttention.active;

    private string CurrentRoomActionReceipt(string language)
    {
        if (language != "ja" && language != "zh" && language != "en") return null;
        string Text(string zh, string ja, string en) => language == "ja" ? ja : language == "en" ? en : zh;
        if (TryReadDeviceReceiptState(out bool on, out bool changed))
            return changed
                ? on ? Text("已经把那盏灯打开了。", "あの灯りを点けたわ。", "I've switched that lamp on.")
                    : Text("已经把那盏灯关掉了。", "あの灯りを消したわ。", "I've switched that lamp off.")
                : on ? Text("那盏灯已经是开启状态，这次没有重复切换。", "あの灯りはもう点いていたから、切り替えてはいないわ。", "That lamp was already on, so I haven't switched it again.")
                    : Text("那盏灯已经是关闭状态，这次没有重复切换。", "あの灯りはもう消えていたから、切り替えてはいないわ。", "That lamp was already off, so I haven't switched it again.");
        if (CurrentRoomGazeReleased())
            return Text("刚才已经朝那里看过，但现在没有继续保持注视。", "さっきはそちらを向いたけれど、今は見続ける姿勢を保てていないわ。", "I did look that way, but I'm no longer holding the gaze.");
        if (m_SceneActionFact != null && !string.IsNullOrEmpty(m_RoomTaskActionId) &&
            m_SceneActionFact.actionId == m_RoomTaskActionId && m_SceneActionFact.intent == m_RoomTaskIntent &&
            m_SceneActionFact.phase == "arrived-object-unconfirmed" && m_SceneActionFact.groundArrived)
            return m_SceneActionFact.postArrivalObservationStatus == "gaze-hold-not-established"
                ? Text("已经移动到选定的位置，但持续注视还没能保持住。", "選んだ場所には着いたけれど、見続ける姿勢はまだ保てていないわ。", "I've reached the selected position, but haven't been able to hold the gaze.")
                : Text("已经移动到选定的位置，但还没能在新画面中确认目标。", "選んだ場所には着いたけれど、新しい映像で目標をまだ確認できていないわ。", "I've reached the selected position, but haven't confirmed the target in a new image.");
        if (HasDefinitiveRoomActionFailure())
            return Text("这次动作还没完成，目标或动作检查没有通过。", "今回は目標や動作の確認が通らず、まだできていないわ。", "This action hasn't completed; the target or movement checks did not pass.");
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (bridge == null || !bridge.IsConnected) return null;
        if (m_RoomTaskIntent == "stop-looking" && m_RoomTaskDispatch == "gaze-stop-submitted")
            return Text("好，持续注视已经取消了。", "わかったわ。見続けるのはやめたわ。", "Okay, I've cancelled the held gaze.");
        if (m_RoomTaskIntent == "stop-moving" && m_RoomTaskDispatch == "stop-submitted" &&
            bridge.ActionId == m_RoomTaskActionId && !bridge.ReservesBody && !bridge.IsFollowing)
            return Text("好，已经停止移动了。", "わかったわ。移動を止めたわ。", "Okay, I've stopped moving.");
        if (m_RoomTaskIntent == "explore" && m_RoomTaskDispatch == "exploration-active" &&
            m_SceneExploration.active && m_SceneExploration.goalId == m_RoomTaskActionId)
            return Text("好，我会试着在附近走走看看。", "ええ、近くを少し歩いて見てみるわ。", "Okay, I'll try looking around nearby.");
        if ((m_RoomTaskIntent == "follow" || m_RoomTaskIntent == "approach") &&
            HasCurrentRoomRequestReceipt(CaptureRoomSpeechSnapshot(bridge, null)))
            return m_RoomTaskIntent == "follow"
                ? Text("好，我试着跟上你。", "ええ、あなたについていけるようにしてみるわ。", "Okay, I'll try to follow you.")
                : Text("好，我试着走到你身边。", "ええ、あなたのそばへ行ってみるわ。", "Okay, I'll try to come over to you.");
        if (m_SceneActionFact == null || string.IsNullOrEmpty(m_RoomTaskActionId) ||
            m_SceneActionFact.actionId != m_RoomTaskActionId || m_SceneActionFact.intent != m_RoomTaskIntent) return null;
        if (m_SceneActionFact.phase == "observed" && (m_RoomTaskIntent == "look-at" || m_RoomTaskIntent == "orient-view"))
            return Text("已经朝那里看了。是否能看全，还需要继续确认。", "そちらを向いたわ。全体が見えるかは、まだ確かめる必要があるの。", "I've turned to look that way. Whether the whole target is visible still needs checking.");
        if (m_SceneActionActive && m_RoomTaskIntent == "reposition-view")
            return Text("好，我试着调整观察位置，再确认视野。", "ええ、見る位置を変えて、視界を確かめてみるわ。", "Okay, I'll try adjusting my position and then check the view.");
        if (m_SceneActionActive && (m_RoomTaskIntent == "approach-object" || m_RoomTaskIntent == "move-to-ground"))
            return Text("好，我试着走到选定的位置。", "ええ、選んだ場所へ行ってみるわ。", "Okay, I'll try moving to the selected position.");
        return null; // Unhandled or changing results retain the full response path.
    }

    // This is a device-state receipt, not a brightness judgment, hand action or
    // permission inferred from a model's prose. The scene executor owns every
    // target/visibility/distance check and records this result before continuation.
    private bool TryReadDeviceReceiptState(out bool on, out bool changed)
    {
        on = changed = false;
        var fact = m_SceneActionFact;
        var result = m_SceneObjectStateResult;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (m_RoomTaskIntent != "set-object-state" || fact == null || m_SceneActionActive ||
            fact.intent != m_RoomTaskIntent || string.IsNullOrEmpty(m_RoomTaskActionId) ||
            fact.actionId != m_RoomTaskActionId || !fact.objectConfirmed ||
            (fact.phase != "performed" && fact.phase != "already-satisfied") ||
            (fact.requestedState != "on" && fact.requestedState != "off") ||
            string.IsNullOrEmpty(fact.entityId) || string.IsNullOrEmpty(fact.resultCaptureId) ||
            bridge == null || !bridge.IsConnected || !RoomMotionOutputEnabled ||
            !m_EnableSceneActions || !m_EnableSceneObjectAwareness || !SceneResultBindingValid() ||
            m_SceneResultDevice.EntityId != fact.entityId || result == null ||
            (string)result["actionId"] != fact.actionId || (string)result["entityId"] != fact.entityId ||
            (bool?)result["completed"] != true || (bool?)result["partial"] == true ||
            (bool?)result["freshVisualObservation"] != true ||
            (string)result["resultCaptureId"] != fact.resultCaptureId) return false;
        on = fact.requestedState == "on";
        changed = fact.phase == "performed";
        if ((bool?)result["on"] != on || (bool?)result["changed"] != changed || fact.stateChanged != changed)
            return false;
        var state = m_SceneResultDevice.ReadState();
        return state.registered && state.entityId == fact.entityId && state.on == on &&
            state.lightEnabled == on && state.emissionEnabled == on &&
            JToken.DeepEquals(result["actual"], JObject.Parse(JsonUtility.ToJson(state)));
    }

    private void RememberDeviceReceiptSpeech(int generation, int round, long outputRevision)
    {
        if (!TryReadDeviceReceiptState(out _, out _)) return;
        m_DeviceReceiptSpeech.Clear();
        m_DeviceReceiptSpeech[round] = new DeviceReceiptSpeechLease {
            Generation = generation, Epoch = m_WorkEpoch, Round = round, SceneEpoch = m_SceneActionEpoch,
            OutputRevision = outputRevision, ActionId = m_RoomTaskActionId,
            EntityId = m_SceneActionFact.entityId, Phase = m_SceneActionFact.phase,
            Device = m_SceneResultDevice, Result = (JObject)m_SceneObjectStateResult.DeepClone()
        };
    }

    private bool ValidateDeviceReceiptSpeech(int round)
    {
        if (!m_DeviceReceiptSpeech.TryGetValue(round, out var lease)) return true;
        bool current = lease.Generation == m_FormalResponseGeneration && lease.Generation == m_RoomTaskGeneration &&
            lease.Epoch == m_WorkEpoch && lease.Round == m_CurrentSpeechRoundId &&
            lease.OutputRevision == m_FastLoop.OutputRevision && lease.SceneEpoch == m_SceneActionEpoch &&
            lease.ActionId == m_RoomTaskActionId && lease.Device == m_SceneResultDevice &&
            lease.EntityId == m_SceneActionFact?.entityId && lease.Phase == m_SceneActionFact?.phase &&
            JToken.DeepEquals(lease.Result, m_SceneObjectStateResult) && TryReadDeviceReceiptState(out _, out _);
        if (current) return true;
        DiscardPendingSpeechForRound(round, "device-receipt-fact-invalidated");
        m_DeviceReceiptSpeech.Remove(round);
        m_ResponseTrace?.Mark("device_receipt.speech_revoked", new JObject {
            ["generation"] = lease.Generation, ["speechRound"] = round,
            ["actionId"] = lease.ActionId, ["entityId"] = lease.EntityId,
            ["reason"] = "device-receipt-fact-invalidated" });
        return false;
    }
}
