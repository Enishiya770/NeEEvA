using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // Spatial intent is committed once before its spoken description. The role's
    // ordinary prose/tool stream never obtains a second room-action authority.
    private int m_RoomTaskGeneration = -1, m_RoomSpeechGuardGeneration = -1;
    private string m_RoomTaskIntent = "none", m_RoomTaskDispatch = "not-submitted", m_RoomTaskActionId = "";
    private bool m_RoomTaskFormatError;
    private int m_RoomTaskTargetRevision;

    private bool HasRoomTaskPlanner => m_UseStreaming && m_IsVoiceMode && m_ChatSettings?.m_TextToSpeech != null &&
        RoomMotionOutputEnabled && m_ChatSettings?.m_ChatModel != null &&
        m_ChatSettings.m_ChatModel.SupportsRoomTaskMessages && GetComponent<ArdyRoomDialogueBridge>()?.IsConnected == true;

    private static JObject PickRoomFields(JObject source, params string[] fields)
    {
        var result = new JObject();
        foreach (string field in fields)
            if (source?[field] != null) result[field] = source[field].DeepClone();
        return result;
    }

    private JObject ReadCompactRoomTaskFacts(ArdyRoomDialogueBridge bridge)
    {
        if (bridge == null) return new JObject { ["connected"] = false };
        // Keep Unity's field serialization for vectors. Do not walk Vector3.normalized
        // with Newtonsoft, or repeat the legacy diagnostic/assessment tree in prompts.
        var full = JObject.Parse(bridge.DescribeRoomContext());
        var current = full["currentFacts"] as JObject;
        var facts = PickRoomFields(current, "currentTargetRevision", "lastAttemptTargetRevision",
            "lastAttemptAppliesToCurrentTarget", "attemptStatus", "connected", "userAnchorValid",
            "bodyReserved", "activeActionId", "observedAtRealtime", "currentlyNearAndFacingUser",
            "actualUserDistance", "actualFacingErrorDegrees", "desiredUserDistance", "actualRoot", "userHeadWorld");
        var assessment = current?["assessment"] as JObject;
        var compactAssessment = PickRoomFields(assessment, "state", "canPlanRoute", "budgetChecked",
            "executionGuaranteed", "routeLength", "reason", "observedAtRealtime", "expiresAtRealtime");
        var observation = assessment?["observation"] as JObject;
        compactAssessment["observation"] = PickRoomFields(observation, "stage", "code", "summary",
            "candidateCount", "acceptedCount", "startRejected", "targetRejected", "pathRejected",
            "userClearanceRejected", "otherRejected");
        facts["assessment"] = compactAssessment;
        var execution = PickRoomFields(full["latestExecutionObservation"] as JObject,
            "actionId", "phase", "stage", "reason", "executionReason", "operationRevision", "observedUtc");
        execution["matchesRequestedAction"] = !string.IsNullOrEmpty(m_RoomTaskActionId) &&
            (string)execution["actionId"] == m_RoomTaskActionId;
        return new JObject {
            ["currentFacts"] = facts,
            ["lastActionResults"] = PickRoomFields(full["lastActionResults"] as JObject,
                "actionId", "targetRevision", "appliesToCurrentTarget", "phase", "reason", "observedAtRealtime"),
            ["execution"] = execution,
            ["currentSupport"] = PickRoomFields(full["currentUserSupport"] as JObject,
                "found", "verified", "objectName", "headHeight", "reason"),
            ["scope"] = "currentFacts is the present target; assessment is geometry only, not execution. " +
                "lastActionResults and execution are identified action records, not a verdict on a new target."
        };
    }

    private string RecentRoomDialogue()
    {
        var model = m_ChatSettings.m_ChatModel;
        var recent = new List<object>();
        // Source text is quoted data, never authority. Bound this auxiliary request
        // and exclude system/persona/image payloads; the full role reply keeps them.
        foreach (var message in model.m_DataList.Where(x => x != null && (x.role == "user" || x.role == "assistant")).Reverse().Take(4).Reverse())
        {
            string text = message.content ?? "";
            if (text.Length > 1600) text = text.Substring(text.Length - 1600);
            recent.Add(new { role = message.role, quotedText = text });
        }
        return JsonConvert.SerializeObject(recent);
    }

    private bool TryPlanRoomTask(string prompt, string imageUrl, int generation, bool recordHistory, string context)
    {
        if (!HasRoomTaskPlanner) return false;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var model = m_ChatSettings.m_ChatModel;
        var evidence = bridge.ReadCurrentTargetEvidence(true);
        int workEpoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn;
        bool autonomous = m_AgentCurrentRoundIsTick;
        string userText = autonomous ? "" : m_ActionConstraints.CurrentUserText;
        m_FormalResponseInFlight = true;
        m_RoomTaskGeneration = generation;
        m_RoomTaskIntent = "none"; m_RoomTaskDispatch = "not-submitted"; m_RoomTaskActionId = "";
        m_RoomTaskTargetRevision = evidence.currentTargetRevision;
        bool consumed = false;
        var roomFacts = ReadCompactRoomTaskFacts(bridge);
        string input = JsonConvert.SerializeObject(new {
            currentUserText = userText, autonomous,
            recentDialogue = JArray.Parse(RecentRoomDialogue()), currentFacts = roomFacts["currentFacts"],
            lastActionResults = roomFacts["lastActionResults"], execution = roomFacts["execution"],
            currentSupport = roomFacts["currentSupport"], scope = roomFacts["scope"],
            autonomousContext = autonomous ? (prompt ?? "").Substring(0, Math.Min(4000, (prompt ?? "").Length)) : ""
        });
        model.PostRoomTaskMessage(input, false, (ok, json, status) => {
            if (consumed || generation != m_FormalResponseGeneration || workEpoch != m_WorkEpoch ||
                userTurn != m_ActionConstraints.CurrentUserTurn || m_UserSpeechActiveForAutonomy) return;
            consumed = true;
            bool valid = ok && RoomTaskProtocol.TryDecision(json, userText, autonomous, out _);
            RoomTaskProtocol.Decision decision = null;
            if (valid) RoomTaskProtocol.TryDecision(json, userText, autonomous, out decision);
            if (!valid)
            {
                m_RoomTaskIntent = "unresolved";
                m_RoomTaskDispatch = "decision-unavailable";
            }
            else
            {
                m_RoomTaskIntent = decision.intent;
                if (decision.intent == "approach")
                {
                    var current = bridge.ReadCurrentTargetEvidence();
                    // A scheduled tick cannot repeat the same completed/failed attempt.
                    if (autonomous && current.lastAttemptAppliesToCurrentTarget)
                        m_RoomTaskDispatch = "same-target-autonomous-retry-suppressed";
                    else
                    {
                        var intent = new DialogueMotionIntent("approach", "", generation, ++m_MotionSequence);
                        RegisterMotionAction(ref intent);
                        m_RoomTaskActionId = intent.ActionId;
                        m_RoomTaskDispatch = bridge.ApproachForTarget(intent.ActionId, evidence.currentTargetRevision, out string failure)
                            ? "attempt-submitted" : "not-submitted: " + failure;
                    }
                }
                else if (decision.intent == "stop-moving")
                {
                    var intent = new DialogueMotionIntent("stop-moving", "", generation, ++m_MotionSequence);
                    RegisterMotionAction(ref intent);
                    m_RoomTaskActionId = intent.ActionId;
                    bridge.StopForTask(intent.ActionId);
                    m_RoomTaskDispatch = "stop-submitted";
                }
                else if (decision.intent == "inspect")
                { bridge.ReadCurrentTargetEvidence(true); m_RoomTaskDispatch = "read-only-checked"; }
            }
            Debug.Log($"[Room/Task] generation={generation} userTurn={userTurn} targetRevision={evidence.currentTargetRevision} intent={m_RoomTaskIntent} dispatch={m_RoomTaskDispatch} action={m_RoomTaskActionId}", this);
            string facts = BuildRoomTaskReplyFacts();
            DispatchFormalStream(prompt, imageUrl, generation, recordHistory,
                (context ?? "") + "\n" + facts, true);
        });
        return true;
    }

    private string BuildRoomTaskReplyFacts()
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        return "[本轮空间任务；由同一模型的结构化意图及Unity执行事实共同确定]\n" +
            JsonConvert.SerializeObject(new {
                intent = m_RoomTaskIntent, dispatch = m_RoomTaskDispatch, actionId = m_RoomTaskActionId,
                targetRevision = m_RoomTaskTargetRevision,
                roomFacts = ReadCompactRoomTaskFacts(bridge)
            }) +
            "\n本轮的房间动作已由上述步骤决定，普通台词不再提交任何approach/stop-moving。" +
            "intent=none表示本轮没有空间任务，继续真实话题；inspect只检查不行走。" +
            "其他空间intent本轮只生成简短自然台词，不输出任何工具标签；不得以挥手/摇头替换走近。" +
            "attempt-submitted只是已尝试；先读当前阶段，拒绝不能说已完成。not-submitted是尚未尝试，不引用旧失败冒充本次结果。" +
            "approach=你靠近用户，用户自己往后走不等于你后退。台词方向、是否尝试和结果必须一致。" +
            "不要把候选空间检查推测成用户坐姿或主观心理；发声格式故障不能解释为走神。";
    }

    private bool ShouldGuardRoomSpeech(int generation) => m_RoomTaskGeneration == generation && m_RoomTaskIntent != "none";

    private void CompleteRoomSpeech(SpeechTextBuffer buffer, string candidateExecutable, int generation,
        bool recordHistory, ObservationReceipt observations)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var model = m_ChatSettings.m_ChatModel;
        int epoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn;
        string candidate = buffer.ToString();
        bool consumed = false;
        bool Fresh() => generation == m_FormalResponseGeneration && epoch == m_WorkEpoch &&
            userTurn == m_ActionConstraints.CurrentUserTurn && !m_UserSpeechActiveForAutonomy;
        void Finish(bool coherent, string reviewStatus)
        {
            if (consumed || !Fresh()) return;
            consumed = true;
            m_RoomSpeechGuardGeneration = -1;
            string text = coherent ? candidate : RoomTaskFallback(bridge);
            string language = coherent ? buffer.LanguageCode : "zh";
            // Only the audited public reply enters history and the action parser.
            // Raw candidates remain diagnostics; no discarded promises become memory.
            if (recordHistory) model.m_DataList.Add(new LLM.SendData("assistant", text));
            if (coherent)
                foreach (var part in buffer.Snapshot()) OnSpeechStreamDelta(part);
            else OnSpeechStreamDelta(new SpeechText(text, language, "room-task-fallback", SpeechActionDependency.Independent));
            AcknowledgeObservations(observations, generation, text);
            m_FormalResponseInFlight = false;
            Debug.Log($"[Room/SpeechReview] generation={generation} action={m_RoomTaskActionId} releasedCandidate={coherent} status={reviewStatus}", this);
            OnStreamComplete(text);
        }
        if (!RoomTaskProtocol.IsPlainSpeech(candidate) || m_RoomTaskFormatError ||
            buffer.Snapshot().Any(part => part.ActionDependency == SpeechActionDependency.AfterAction))
        { Finish(false, "missing-or-invalid-public-speech"); return; }
        string input = JsonConvert.SerializeObject(new {
            intendedAction = m_RoomTaskIntent, dispatch = m_RoomTaskDispatch, actionId = m_RoomTaskActionId,
            publicSpeech = candidate, requestedUserText = m_ActionConstraints.CurrentUserText,
            declaredMotion = ExtractRoomReviewMotion(candidateExecutable),
            currentRoomFacts = ReadCompactRoomTaskFacts(bridge)
        });
        string phaseAtReview = bridge != null ? bridge.Phase : "disconnected";
        string actionAtReview = bridge != null ? bridge.ActionId : "";
        int operationAtReview = bridge?.Controller != null ? bridge.Controller.OperationRevision : -1;
        int targetAtReview = bridge != null ? bridge.ReadCurrentTargetEvidence().currentTargetRevision : -1;
        model.PostRoomTaskMessage(input, true, (ok, output, status) => {
            if (!Fresh()) return;
            bool unchanged = bridge != null && bridge.IsConnected && bridge.Phase == phaseAtReview &&
                bridge.ActionId == actionAtReview && bridge.Controller != null && bridge.Controller.OperationRevision == operationAtReview &&
                bridge.ReadCurrentTargetEvidence().currentTargetRevision == targetAtReview;
            Finish(ok && unchanged && RoomTaskProtocol.ReviewAllows(output), unchanged ? status : "facts-changed-during-review");
        });
    }

    private static string ExtractRoomReviewMotion(string executable)
    {
        string copy = executable ?? "";
        return DialogueMotionProtocol.TryExtract(ref copy, 0, 0, out var intent, out var error, true)
            ? intent.Name : string.IsNullOrEmpty(error) ? "none" : "invalid-motion";
    }

    private string RoomTaskFallback(ArdyRoomDialogueBridge bridge)
    {
        if (bridge == null || !bridge.IsConnected) return "房间移动目前没有连接，我还不能确认这次动作的结果。";
        var current = bridge.ReadCurrentTargetEvidence();
        if (m_RoomTaskIntent == "stop-moving" && m_RoomTaskDispatch == "stop-submitted")
            return bridge.ActionId == m_RoomTaskActionId && !bridge.ReservesBody ? "我已经停止移动了。"
                : "刚才的停止请求已经处理，当前动作状态又发生了变化。";
        if (m_RoomTaskIntent == "unresolved") return "我还没确认这次的移动意图，暂时没有提交新的动作。";
        if (m_RoomTaskIntent == "inspect")
            return current.assessment.canPlanRoute ? "我重新检查了现在的位置，附近有可用路线；这次还没有开始移动。"
                : "我重新检查了现在的位置，暂时还没有确认可以走近的路线。";
        if (bridge.ActionId == m_RoomTaskActionId && bridge.Phase == "arrived" && current.currentlyNearAndFacingUser)
            return "我已经走到你身边了。";
        if (current.currentTargetRevision != m_RoomTaskTargetRevision)
            return "位置发生了变化，我需要按现在的位置重新确认，不能沿用刚才的结果。";
        if (bridge.ReservesBody) return bridge.ActionId == m_RoomTaskActionId ? "我正在尝试走近你。"
            : "当前还有房间动作在执行，这次没有另外启动新的走近。";
        if (m_RoomTaskDispatch != "attempt-submitted") return "这次还没有开始新的走近，我需要以现在的位置重新确认。";
        if (bridge.Controller != null && bridge.Controller.Stage == "target-budget") return "这段路线超过了我目前单次行走的时长限制，还没能走到你身边。";
        if (bridge.Phase == "unreachable") return "这次还没能走到你身边，当前位置的路线或空间检查没有通过。";
        return "这次走近还没有完成，我不能把它当作已经成功。";
    }
}
