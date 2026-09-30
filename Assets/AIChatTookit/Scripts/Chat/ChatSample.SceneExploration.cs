using System;
using System.Collections.Generic;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // A spatial activity survives individual speech rounds. The model selects
    // every step from a fresh observation; this state never selects destinations.
    private sealed class SceneExplorationTask
    {
        public bool active;
        public int revision, startedUserTurn, steps, arrivals, consecutiveFailures;
        public string goalId = "", origin = "", purpose = "", phase = "inactive", reason = "", latestActionId = "";
        public string lastDecision = "none", lastDispatch = "not-submitted";
        public readonly Queue<JObject> recentResults = new Queue<JObject>();
        public readonly HashSet<string> terminalActions = new HashSet<string>();
    }

    private readonly SceneExplorationTask m_SceneExploration = new SceneExplorationTask();
    private bool m_ExplorationAutonomousSuppressed;
    private int m_ExplorationStepGeneration = -1, m_ExplorationStepRevision = -1;
    private SceneActionFact m_ExplorationLastFact;
    private float m_ExplorationNextAt = -1f;

    public string SceneExplorationContextJson => BuildSceneExplorationContext().ToString(Newtonsoft.Json.Formatting.None);

    private JObject BuildSceneExplorationContext() => new JObject {
        ["active"] = m_SceneExploration.active, ["goalId"] = m_SceneExploration.goalId,
        ["revision"] = m_SceneExploration.revision, ["origin"] = m_SceneExploration.origin,
        ["purpose"] = m_SceneExploration.purpose, ["startedUserTurn"] = m_SceneExploration.startedUserTurn,
        ["phase"] = m_SceneExploration.phase, ["reason"] = m_SceneExploration.reason,
        ["steps"] = m_SceneExploration.steps, ["arrivals"] = m_SceneExploration.arrivals,
        ["consecutiveFailures"] = m_SceneExploration.consecutiveFailures,
        ["latestActionId"] = m_SceneExploration.latestActionId,
        ["lastDecision"] = m_SceneExploration.lastDecision, ["lastDispatch"] = m_SceneExploration.lastDispatch,
        ["autonomousRestartSuppressed"] = m_ExplorationAutonomousSuppressed,
        ["recentResults"] = new JArray(m_SceneExploration.recentResults),
        ["scope"] = "A continuing spatial activity, not a new user instruction or a completed action. " +
            "Select one next step from the CURRENT attached image: move-to-ground for visible free floor, " +
            "approach-object for an interesting visible object, or look/turn to obtain a new view. " +
            "Actual arrivals and failures below are observations, not a map of unseen space. " +
            "Choose a different useful viewpoint after arrival; do not merely repeat explore, comment on the room, " +
            "or retry the same failed point. none allows a rest; stop-moving ends the activity. " +
            "Every destination still requires grounding and navigation. No random movement or teleportation."
    };

    private bool StartSceneExploration(RoomTaskProtocol.Decision decision, int generation)
    {
        if (decision == null || decision.intent != "explore" || generation != m_FormalResponseGeneration ||
            !HasRoomTaskPlanner || !m_EnableSceneActions || !SceneEyesOpen ||
            m_UserSpeechActiveForAutonomy) return false;
        // An explicit PTT/text task may run without enabling open-microphone or
        // spontaneous speech. Autonomous starts still require the Agent loop.
        if (!m_AgentRunning && decision.origin != "user") return false;
        if (decision.IsSelfInitiated && m_ExplorationAutonomousSuppressed) return false;
        bool replacingOwnTask = m_SceneExploration.active && decision.origin == "user" &&
            m_ActionConstraints.CurrentUserTurn != m_SceneExploration.startedUserTurn;
        if (m_SceneExploration.active && !replacingOwnTask) return true;
        if (replacingOwnTask) StopSceneExploration("user-revised-exploration");
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (bridge == null || bridge.IsFollowing || (!replacingOwnTask && (bridge.ReservesBody || SceneActionIsBusy))) return false;
        m_ExplorationAutonomousSuppressed = false;
        m_SceneExploration.active = true;
        ++m_SceneExploration.revision;
        m_SceneExploration.goalId = Guid.NewGuid().ToString("N");
        m_SceneExploration.origin = decision.origin;
        m_SceneExploration.purpose = decision.origin == "actor_draft" ? decision.SourceText :
            decision.origin == "user" ? m_ActionConstraints.CurrentUserText : m_SceneAutonomyIntention;
        if (string.IsNullOrWhiteSpace(m_SceneExploration.purpose)) m_SceneExploration.purpose = decision.evidence ?? "explore the visible room";
        m_SceneExploration.purpose = TruncateForFrame(m_SceneExploration.purpose, 400);
        m_SceneExploration.startedUserTurn = m_ActionConstraints.CurrentUserTurn;
        m_SceneExploration.steps = m_SceneExploration.arrivals = m_SceneExploration.consecutiveFailures = 0;
        m_SceneExploration.phase = "ready"; m_SceneExploration.reason = "";
        m_SceneExploration.latestActionId = "";
        m_SceneExploration.lastDecision = "none"; m_SceneExploration.lastDispatch = "not-submitted";
        m_SceneExploration.recentResults.Clear(); m_SceneExploration.terminalActions.Clear();
        m_ExplorationLastFact = null;
        if (m_SceneTimeSuspended) BeginSceneTimeSession();
        ScheduleSceneExplorationStep(Mathf.Max(2f, m_MinTickSec), "scene-exploration-started");
        Debug.Log("[Agent/Exploration] started " + SceneExplorationContextJson, this);
        return true;
    }

    private void StopSceneExploration(string reason, bool suppressAutonomous = false)
    {
        bool ownedStep = m_ExplorationStepGeneration >= 0 && m_ExplorationStepGeneration == m_FormalResponseGeneration;
        bool ownedAction = m_SceneExploration.active && !string.IsNullOrEmpty(m_SceneExploration.latestActionId) &&
            m_SceneActionFact?.actionId == m_SceneExploration.latestActionId;
        m_SceneExploration.active = false;
        ++m_SceneExploration.revision;
        m_SceneExploration.phase = "stopped"; m_SceneExploration.reason = reason ?? "stopped";
        m_ExplorationAutonomousSuppressed |= suppressAutonomous;
        m_ExplorationStepGeneration = m_ExplorationStepRevision = -1;
        m_ExplorationNextAt = -1f;
        if (ownedStep)
        {
            ++m_FormalResponseGeneration;
            m_FormalResponseInFlight = false; m_AgentRoundInFlight = false;
            m_PendingSceneFormalCaptureGeneration = -1;
            m_QuietAutonomyRound = false; m_AgentCurrentRoundIsTick = false;
        }
        if (ownedAction) CancelSceneAction("exploration-" + reason);
        if (ownedStep || ownedAction) Debug.Log("[Agent/Exploration] stopped reason=" + reason, this);
    }

    private bool TryDispatchSceneExplorationTick(string triggerReason)
    {
        if (!m_SceneExploration.active || m_AgentGracefulShutdownPending ||
            SceneExplorationInputPending || (m_AgentRunning && m_AgentAwaitingFirstAcceptedUserTurn) ||
            m_SceneTimePaused || Time.timeScale <= 0 ||
            !isActiveAndEnabled || IsAISpeaking || IsVoiceOutputPlaying || m_AgentRoundInFlight ||
            m_FormalResponseInFlight || m_AutonomyProbeInFlight || SceneActionIsBusy ||
            HasPendingSingingGoalReview() || HasWorkToolInFlight() || IsSingingRuntimeActivityActive() ||
            !string.IsNullOrEmpty(m_PendingSelfInspection)) return false;
        if (!HasRoomTaskPlanner || !SceneEyesOpen)
        {
            m_SceneExploration.phase = "waiting-for-perception";
            ScheduleSceneExplorationStep(10f, "scene-exploration-waiting");
            return true;
        }
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (bridge.ReservesBody || bridge.IsFollowing) return false;
        double now = SceneMonotonicNow();
        if (!m_SceneReviewClock.CanRequest(now, m_AutonomyReviewsPerMinute, m_AutonomyReviewMinimumSpacing, out double next))
        {
            ScheduleSceneExplorationStep((float)Math.Max(1, next - now), "scene-exploration-budget");
            return true;
        }
        m_SceneReviewClock.RecordRequest(now);
        int revision = m_SceneExploration.revision, workEpoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn;
        int generation = ++m_FormalResponseGeneration;
        m_ExplorationStepGeneration = generation; m_ExplorationStepRevision = revision;
        m_FormalResponseInFlight = true; m_AgentRoundInFlight = true; m_AgentCurrentRoundIsTick = true;
        m_QuietAutonomyRound = true;
        m_SceneAutonomyIntention = m_SceneExploration.purpose;
        m_SceneExploration.phase = "observing";
        ClearRoundParsed();
        m_PendingSceneFormalCaptureGeneration = generation;
        RequestFreshSceneObservation("exploration-next-step", observed => {
            if (generation != m_FormalResponseGeneration || revision != m_SceneExploration.revision ||
                !m_SceneExploration.active || workEpoch != m_WorkEpoch || userTurn != m_ActionConstraints.CurrentUserTurn ||
                SceneExplorationInputPending || !isActiveAndEnabled) return;
            m_PendingSceneFormalCaptureGeneration = -1;
            if (observed?.HasImage != true || !IsFreshSceneObservation(observed))
            {
                m_RoomTaskDispatch = "not-submitted: exploration-current-image-unavailable";
                m_SceneExploration.phase = "waiting-for-perception";
                CompleteSceneExplorationStep(generation);
                return;
            }
            BindFormalSceneObservation(observed, generation);
            m_SceneExploration.phase = "deciding";
            string prompt = "[持续探索的下一步；没有新用户消息。只决定一次空间行动，不生成台词。]\n" + SceneExplorationContextJson;
            if (!TryPlanRoomTask(prompt, observed.ImageDataUrl, generation, false, null))
            {
                m_RoomTaskDispatch = "not-submitted: exploration-planner-unavailable";
                CompleteSceneExplorationStep(generation);
            }
        });
        return true;
    }

    // The spatial planner already decided and submitted the action. Autonomous
    // exploration does not need a second language request to narrate each step.
    private bool CompleteSceneExplorationStep(int generation)
    {
        if (generation != m_ExplorationStepGeneration || generation != m_FormalResponseGeneration) return false;
        m_ExplorationStepGeneration = -1;
        m_FormalResponseInFlight = false; m_AgentRoundInFlight = false;
        m_QuietAutonomyRound = false; m_AgentCurrentRoundIsTick = false;
        m_StreamComplete = true; m_TTSSenderDone = true;
        m_PendingSceneFormalCaptureGeneration = -1;
        ClearRoundParsed();
        if (m_SceneExploration.active)
        {
            m_SceneExploration.lastDecision = m_RoomTaskIntent;
            m_SceneExploration.lastDispatch = m_RoomTaskDispatch;
            if (m_RoomTaskDispatch == "decision-unavailable")
            {
                m_SceneExploration.reason = "spatial-decision-unavailable";
                if (++m_SceneExploration.consecutiveFailures >= 3)
                {
                    m_SceneExploration.active = false; m_SceneExploration.phase = "blocked";
                    m_ExplorationAutonomousSuppressed = true;
                }
            }
            if (!SceneActionIsBusy && m_SceneExploration.phase == "deciding") m_SceneExploration.phase = "resting";
            ScheduleSceneExplorationStep(SceneActionIsBusy ? 2f : 10f, "scene-exploration-next-step");
        }
        Debug.Log("[Agent/Exploration] step-decided intent=" + m_RoomTaskIntent + " dispatch=" + m_RoomTaskDispatch +
            " speechGenerated=False " + SceneExplorationContextJson, this);
        return true;
    }

    private void ObserveExplorationAction(SceneActionFact fact)
    {
        if (!m_SceneExploration.active || fact == null) return;
        bool planningThisGoal = m_ExplorationStepGeneration == m_FormalResponseGeneration &&
            m_ExplorationStepRevision == m_SceneExploration.revision;
        if (planningThisGoal && !ReferenceEquals(m_ExplorationLastFact, fact))
        {
            m_ExplorationLastFact = fact;
            m_SceneExploration.latestActionId = fact.actionId ?? "";
            ++m_SceneExploration.steps;
        }
        if (!ReferenceEquals(m_ExplorationLastFact, fact)) return;
        m_SceneExploration.phase = fact.phase;
        m_SceneExploration.reason = fact.reason ?? "";
        bool failed = fact.phase == "failed" || fact.phase == "rejected" || fact.phase == "blocked";
        bool arrived = fact.phase == "arrived" || fact.phase == "arrival-observing" || fact.groundArrived;
        bool terminal = arrived || failed || fact.phase == "observed" || fact.phase == "cancelled";
        string key = (fact.actionId ?? "step-" + m_SceneExploration.steps) + (arrived ? ":arrival" : ":terminal");
        if (!terminal) return;
        if (!m_SceneExploration.terminalActions.Add(key))
        {
            // Arrival is measured before the post-arrival camera completes.
            // Refresh that receipt without counting the same arrival twice.
            foreach (var recorded in m_SceneExploration.recentResults)
                if ((int?)recorded["step"] == m_SceneExploration.steps && (bool?)recorded["groundArrived"] == arrived)
                {
                    // A preparation finally may report cancellation before its
                    // exception wrapper upgrades the same receipt to failure.
                    // Consume that failure once, even if later callbacks repeat it.
                    if (failed && (bool?)recorded["failureCounted"] != true)
                    {
                        recorded["failureCounted"] = true;
                        ++m_SceneExploration.consecutiveFailures;
                    }
                    recorded["phase"] = fact.phase; recorded["reason"] = fact.reason;
                    recorded["actualRoot"] = JObject.Parse(JsonUtility.ToJson(fact.actualRoot));
                    recorded["captureId"] = string.IsNullOrEmpty(fact.resultCaptureId) ? fact.captureId : fact.resultCaptureId;
                }
            if (m_SceneExploration.consecutiveFailures >= 3)
            {
                m_SceneExploration.active = false;
                m_SceneExploration.phase = "blocked";
                m_ExplorationAutonomousSuppressed = true;
            }
            return;
        }
        if (m_SceneExploration.terminalActions.Count > 64)
        {
            m_SceneExploration.terminalActions.Clear();
            m_SceneExploration.terminalActions.Add(key);
        }
        if (arrived) ++m_SceneExploration.arrivals;
        if (failed) ++m_SceneExploration.consecutiveFailures;
        else if (arrived || fact.phase == "observed") m_SceneExploration.consecutiveFailures = 0;
        m_SceneExploration.recentResults.Enqueue(new JObject {
            ["step"] = m_SceneExploration.steps,
            ["actionId"] = fact.actionId, ["intent"] = fact.intent, ["phase"] = fact.phase,
            ["targetLabel"] = fact.visualLabel, ["reason"] = fact.reason, ["failureCounted"] = failed,
            ["actualRoot"] = JObject.Parse(JsonUtility.ToJson(fact.actualRoot)),
            ["captureId"] = string.IsNullOrEmpty(fact.resultCaptureId) ? fact.captureId : fact.resultCaptureId, ["groundArrived"] = arrived
        });
        while (m_SceneExploration.recentResults.Count > 8) m_SceneExploration.recentResults.Dequeue();
        if (!m_AgentRunning) ScheduleSceneExplorationStep(2f, "scene-exploration-result");
        // Preserve failed evidence and return control to ordinary cognition; a
        // failed activity cannot repeatedly restart itself as a fresh goal.
        if (m_SceneExploration.consecutiveFailures >= 3)
        {
            m_SceneExploration.active = false;
            m_SceneExploration.phase = "blocked";
            m_ExplorationAutonomousSuppressed = true;
        }
    }

    private void ScheduleSceneExplorationStep(float wait, string reason)
    {
        m_ExplorationNextAt = Time.realtimeSinceStartup + Mathf.Max(1f, wait);
        if (m_AgentRunning) ScheduleNextTick(wait, reason);
    }

    private void UpdateSceneExploration()
    {
        if (m_AgentRunning || !m_SceneExploration.active || m_ExplorationNextAt < 0 ||
            Time.realtimeSinceStartup < m_ExplorationNextAt) return;
        // No separate model clock when the normal agent is running. This only
        // services a user-delegated spatial activity in PTT/text mode.
        if (TryDispatchSceneExplorationTick("scene-exploration-task"))
            m_ExplorationNextAt = Mathf.Max(m_ExplorationNextAt, Time.realtimeSinceStartup + 1f);
    }
}
