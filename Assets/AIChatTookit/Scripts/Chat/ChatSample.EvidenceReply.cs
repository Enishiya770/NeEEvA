using NeEEvA.Perception;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private int m_RoomEvidenceReplyGeneration = -1;
    private JObject m_RoomEvidenceReplyFacts;
    private JObject m_LastRoomPlanningFailure;
    private ArdyRoomDialogueBridge m_LastRoomPlanningFailureBridge;
    private Object m_LastRoomPlanningFailureAvatar, m_LastRoomPlanningFailureEnvironment;
    private float m_LastRoomPlanningFailureSessionStartedAt = -1f;

    private void RememberRoomPlanningFailure(JObject failure)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        m_LastRoomPlanningFailure = (JObject)failure.DeepClone();
        m_LastRoomPlanningFailure["recordedAtUtc"] = System.DateTime.UtcNow.ToString("o");
        m_LastRoomPlanningFailureBridge = bridge;
        m_LastRoomPlanningFailureAvatar = bridge?.avatar;
        m_LastRoomPlanningFailureEnvironment = bridge?.environmentRoot;
        m_LastRoomPlanningFailureSessionStartedAt = m_AgentSessionStartTime;
    }

    private JObject ReadPreviousRoomPlanningFailure()
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (m_LastRoomPlanningFailure == null || bridge == null || bridge != m_LastRoomPlanningFailureBridge ||
            bridge.avatar != m_LastRoomPlanningFailureAvatar || bridge.environmentRoot != m_LastRoomPlanningFailureEnvironment ||
            m_AgentSessionStartTime != m_LastRoomPlanningFailureSessionStartedAt) return null;
        return new JObject {
            ["receipt"] = m_LastRoomPlanningFailure.DeepClone(),
            ["historical"] = true,
            ["scope"] = "A previous planning diagnostic in this scene. Its generation/userTurn/date identify the failed request; it is not a failure of the current answer, a persistent inability, an attempted movement, or permission to retry. Use only when relevant to the user's question."
        };
    }

    private JObject ReadRoomEvidenceReplyFacts(int generation) =>
        generation == m_FormalResponseGeneration && generation == m_RoomEvidenceReplyGeneration
            ? (JObject)m_RoomEvidenceReplyFacts?.DeepClone() : null;

    // The first-reply semantic gate selected an answer requiring evidence only.
    // This path has no planner call, body command, goal mutation or retry grant.
    private void DispatchEvidenceReply(string prompt, string imageUrl, int generation,
        bool recordHistory, string transientContext, JObject questionUnderstanding = null)
    {
        if (generation != m_FormalResponseGeneration || !isActiveAndEnabled || m_UserSpeechActiveForAutonomy) return;
        var model = m_ChatSettings?.m_ChatModel;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (model == null || bridge == null || !bridge.IsConnected || bridge.avatar == null)
        {
            string language = SpatialGoalReportLanguage(null);
            string unavailable = LocalizeRoomFallback("我暂时拿不到可靠的当前场景信息，不能确认眼前的情况。", language);
            if (recordHistory && model != null) model.m_DataList.Add(new LLM.SendData("assistant", unavailable));
            OnSpeechStreamDelta(new SpeechText(unavailable, language, "evidence-unavailable", SpeechActionDependency.Independent));
            m_FormalResponseInFlight = false;
            OnStreamComplete(unavailable);
            return;
        }
        var avatar = bridge.avatar;
        var environment = bridge.environmentRoot;
        int epoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn, visionEpoch = m_SceneVisionEpoch;
        int captureOwnerRevision = ++m_RoomSpeechReviewOwnerRevision;
        bool Current() => generation == m_FormalResponseGeneration && epoch == m_WorkEpoch &&
            captureOwnerRevision == m_RoomSpeechReviewOwnerRevision &&
            userTurn == m_ActionConstraints.CurrentUserTurn && visionEpoch == m_SceneVisionEpoch &&
            !m_UserSpeechActiveForAutonomy && !m_SceneTimePaused && isActiveAndEnabled &&
            model == m_ChatSettings?.m_ChatModel && bridge != null && bridge == GetComponent<ArdyRoomDialogueBridge>() &&
            bridge.IsConnected && avatar != null && (ReferenceEquals(environment, null) || environment != null) &&
            bridge.avatar == avatar && bridge.environmentRoot == environment;
        m_RoomTaskGeneration = generation;
        m_RoomTaskIntent = "none"; m_RoomTaskDispatch = "read-only-evidence"; m_RoomTaskActionId = "";
        m_RoomTaskSpatialAnswer = true;
        m_RoomTaskDecisionFailure = null;
        m_RoomTaskTargetRevision = bridge.ReadCurrentTargetEvidence().currentTargetRevision;
        m_RoomSpeechGuardGeneration = generation;
        m_RoomSpeechReviewModel = model;
        m_RoomEvidenceReplyGeneration = generation;
        FirstReplyProtocol.TryQuestionUnderstanding(questionUnderstanding, out var understanding);
        m_RoomEvidenceReplyFacts = new JObject {
            ["route"] = "evidence", ["actionAuthorization"] = false,
            ["currentUserText"] = m_ActionConstraints.CurrentUserText,
            ["questionUnderstanding"] = understanding == null ? null : new JObject {
                ["source"] = "independent-first-reply-semantic-review",
                ["generation"] = generation, ["workEpoch"] = epoch, ["userTurn"] = userTurn,
                ["interpretation"] = understanding,
                ["scope"] = "Model interpretation of the complete current input and actual prior dialogue, not original user words, a diagnostic/execution fact, or action authorization. Check it against primaryUtterance/fullInputEvidence and source-linked receipts; preserve unresolved ambiguity. It cannot create, resume, cancel or retry a task, establish a cause, or replace factual speech review."
            },
            ["previousPlanningFailure"] = ReadPreviousRoomPlanningFailure(),
            ["scope"] = "Answer-only route selected from the complete current request. No new planning request, search, movement, device operation or goal update was submitted. Existing action and memory facts keep their own identities and times. This answer cannot resume or cancel an unfinished task."
        };
        bool consumed = false;
        void ReleaseInvalidCaptureOwner()
        {
            if (consumed) return;
            consumed = true;
            if (generation != m_FormalResponseGeneration || captureOwnerRevision != m_RoomSpeechReviewOwnerRevision ||
                m_RoomSpeechGuardGeneration != generation) return;
            // Only settle this read-only reply. The shared room cancellation path
            // also pauses spatial goals/searches, which this capture never owned.
            ++m_RoomSpeechReviewOwnerRevision;
            m_RoomSpeechGuardGeneration = -1;
            m_RoomSpeechReviewModel = null;
            if (m_PendingSceneFormalCaptureGeneration == generation) m_PendingSceneFormalCaptureGeneration = -1;
            m_RoomEvidenceReplyGeneration = -1; m_RoomEvidenceReplyFacts = null;
            ++m_FormalResponseGeneration;
            m_FormalResponseInFlight = false; m_StreamComplete = true; m_TTSSenderDone = true;
            m_AgentRoundInFlight = false;
            ClearStagedFormalObservation();
            if (model != null) model.CancelActiveResponse();
            Debug.Log($"[Room/EvidenceReply] generation={generation} state=cancelled reason=capture-owner-invalidated", this);
        }
        void Send(SceneVisionObservation observation)
        {
            if (consumed) return;
            if (!Current()) { ReleaseInvalidCaptureOwner(); return; }
            consumed = true;
            m_PendingSceneFormalCaptureGeneration = -1;
            BindFormalSceneObservation(observation, generation);
            string context = (transientContext ?? "") + "\n[只读场景问答]\n" + new JObject {
                ["reply"] = ReadRoomEvidenceReplyFacts(generation),
                ["spatialReplyFacts"] = BuildSpatialAnswerFacts(observation),
                ["roomFacts"] = ReadCompactRoomTaskFacts(bridge),
                ["sceneActionFacts"] = ReadRoomSceneActionFacts()
            }.ToString(Formatting.None) +
                "\n直接回答用户完整问题。当前画面、历史记忆、身体朝向与相机视向要区分；没有画面或证据时如实保留未知。" +
                "本轮没有调用动作规划，不把之前的失败当成本轮失败，不输出工具、不承诺或声称新动作，也不自动续行旧任务。" +
                "只有用户在追问故障时才引用previousPlanningFailure的原始原因和所属轮次；普通看图聊天不重复故障提示。";
            Debug.Log($"[Room/EvidenceReply] generation={generation} userTurn={userTurn} capture={observation?.CaptureId ?? "none"} plannerRequested=false actionSubmitted=false", this);
            DispatchFormalStream(prompt, observation?.HasImage == true ? observation.ImageDataUrl : null,
                generation, recordHistory, context, true);
        }
        System.Collections.IEnumerator WatchCaptureOwner()
        {
            // RequestSceneObservation normally completes within four seconds.
            // Keep ownership bounded even if a cancelled capture never calls back.
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!consumed)
            {
                if (!Current()) { ReleaseInvalidCaptureOwner(); yield break; }
                if (Time.realtimeSinceStartup >= deadline) { Send(null); yield break; }
                yield return null;
            }
        }
        if (!SceneEyesOpen) Send(GetFormalSceneObservation(generation));
        else if (TryGetPreparedRoomSpeechObservation(generation, out var ready, out _)) Send(ready);
        else
        {
            m_PendingSceneFormalCaptureGeneration = generation;
            if (Application.isPlaying) StartCoroutine(WatchCaptureOwner());
            RequestSceneObservation("evidence-reply", Send);
        }
    }
}
