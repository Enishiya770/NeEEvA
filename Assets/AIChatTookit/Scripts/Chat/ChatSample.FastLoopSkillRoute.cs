using System;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private sealed class FastLoopSkillRouteAttempt
    {
        public FastLoopTurnContext Turn;
        public bool Pending;
    }
    private FastLoopSkillRouteAttempt m_FastLoopSkillRoute;

    // A legacy/complex fast handoff has already consumed FirstReply's turn fence.
    // A nonspatial role proposal selects its skill before room planning. Legacy
    // spatial requests retain their planner; a no-task result can still fall back
    // here. This probe grants no source/range/play authority.
    private bool TryDispatchFastLoopSkillRoute(string prompt, string imageUrl, int generation,
        bool recordHistory, string transientContext, bool roomTaskPrepared)
    {
        var turn = m_FastLoopTurn;
        if (!FastLoopTurnCurrent(turn) || generation != turn.Generation || !turn.HandedOff ||
            (string)turn.ActorProposal?["purpose"] == "verify") return false;
        if (m_FastLoopSkillRoute?.Turn == turn) return m_FastLoopSkillRoute.Pending;
        if (!turn.Model.SupportsRecordingSingingDecision || !turn.Model.SupportsRoleDecisionMessages ||
            IsActiveSingingGoalUnfinished()) return false;
        // A prior real spatial submission, rejection or object operation owns its
        // own continuation. Only a genuine no-task result may choose another IR.
        bool nonspatialProposal = (string)turn.ActorProposal?["domain"] == "other_action" &&
            (string)turn.ActorProposal?["task"] == "other" && !HasTypedSpatialProposal(turn.ActorProposal);
        if (roomTaskPrepared ? m_RoomTaskGeneration != generation || m_RoomTaskIntent != "none" ||
            !string.IsNullOrEmpty(m_RoomTaskActionId) || m_RoomTaskDispatch != "not-submitted"
            : m_RoomTaskGeneration == generation || HasRoomTaskPlanner && !nonspatialProposal) return false;

        var attempt = new FastLoopSkillRouteAttempt { Turn = turn, Pending = true };
        m_FastLoopSkillRoute = attempt;
        int revocation = m_RoomActorProposalRevocation, userTurn = m_ActionConstraints.CurrentUserTurn;
        int serial = m_ActorIntentRequestSerial;
        var trace = m_ResponseTrace?.Child("fast-loop-skill-route");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        bool Current() => ReferenceEquals(m_FastLoopSkillRoute, attempt) && attempt.Pending &&
            FastLoopTurnCurrent(turn) && serial == m_ActorIntentRequestSerial &&
            revocation == m_RoomActorProposalRevocation && userTurn == m_ActionConstraints.CurrentUserTurn;
        void FinishRevoked()
        {
            if (!ReferenceEquals(m_FastLoopSkillRoute, attempt) || !attempt.Pending) return;
            attempt.Pending = false;
            // A new draft or turn owns its own wait. A scene-only revocation of
            // this still-current handoff must release the old formal waiter.
            if (FastLoopTurnCurrent(turn) && serial == m_ActorIntentRequestSerial)
                CancelFastLoopRequest("fast-skill-route-revoked");
        }
        m_FormalResponseInFlight = true;
        string routeContext = BuildFirstReplyContext(null) +
            "\n[快速交接后的技能路由；只读选择阶段]\n" + new JObject {
                ["roomTaskPrepared"] = roomTaskPrepared, ["spatialTaskSelected"] = false,
                ["actorProposal"] = turn.ActorProposal?.DeepClone(),
                ["scope"] = "这里只选择后续决策阶段，不公开台词、不执行动作，不确认素材来源、不选录音范围、不授权演唱。仅在本轮语义请求现有录音回唱时选recording_singing；素材存在本身不是请求。普通讨论、评价、否定或其他工具目标保持原语义。当前用户全文优先，角色提案不是用户原话。"
            }.ToString(Newtonsoft.Json.Formatting.None) +
            "\n[本轮完整用户输入]\n" + (prompt ?? "");
        if (!Current()) { FinishRevoked(); return true; }
        trace?.Mark("fast_loop.skill_route_begin", new JObject {
            ["generation"] = generation, ["roomTaskPrepared"] = roomTaskPrepared,
            ["promptCharacters"] = routeContext.Length, ["executionAuthorized"] = false });
        void Resolve(bool accepted, string ignoredSpeech, string status)
        {
            if (!Current()) { FinishRevoked(); return; }
            attempt.Pending = false; // Fence before continuation/provider reentry.
            var decision = turn.Model.AcceptedRoleDecision;
            bool recording = accepted && string.Equals(status, "tools", StringComparison.Ordinal) &&
                decision?.Route == "recording_singing" && decision.Task == "none" &&
                decision.Attention == "unchanged" && string.IsNullOrWhiteSpace(decision.Speech) &&
                turn.Model.SupportsRecordingSingingDecision && !IsActiveSingingGoalUnfinished();
            trace?.Mark("fast_loop.skill_route_resolved", new JObject {
                ["generation"] = generation, ["accepted"] = accepted, ["status"] = status,
                ["selectedStage"] = recording ? "RecordingSingingDecision" : "General",
                ["seconds"] = timer.Elapsed.TotalSeconds, ["executionAuthorized"] = false,
                ["publicRouteSpeechDiscarded"] = true });
            if (!FastLoopTurnCurrent(turn)) return;
            if (recording && !ReleasePlanningOwnerForRecording(turn))
            {
                if (FastLoopTurnCurrent(turn))
                    CompleteActorInterpretationFailure(turn.Model, turn.Prompt, RoomFallbackLanguage(m_ActorTaskLanguage),
                        generation, recordHistory, "recording-planning-owner-busy");
                return;
            }
            m_FormalResponseInFlight = false;
            // A spatial attempt may transfer its explicit remainder to recording,
            // but cannot send the old goal back through General and repeat it.
            if (!recording && TryCloseUnroutedSpatialRemainder(turn)) return;
            // Nonspatial singing skips room planning; any other verdict retains
            // the spatial planner if it has not run. The probe has one owner.
            DispatchFormalStreamRouted(prompt, ResolveFormalSceneImage(generation), generation, recordHistory,
                transientContext, roomTaskPrepared: roomTaskPrepared || recording,
                skipRoomPlanner: roomTaskPrepared || recording, recordingSinging: recording, skillRoutePrepared: true);
        }
        try { turn.Model.PostRoleDecisionMsg(routeContext, imageUrl, Resolve, trace); }
        catch (Exception error)
        {
            // A synchronous provider failure has the same bounded fallback as a
            // failed callback. Do not swallow an exception in the next stage.
            if (!attempt.Pending) throw;
            Resolve(false, "", "provider-exception:" + error.GetType().Name);
        }
        return true;
    }
}
