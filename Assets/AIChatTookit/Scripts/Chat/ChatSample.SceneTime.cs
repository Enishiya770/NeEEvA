using System;
using System.Collections.Generic;
using AIChat.Agent;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    [Header("私下观察与时间状态")]
    [SerializeField, Range(1, 12)] private int m_AutonomyReviewsPerMinute = 6;
    [SerializeField, Range(1f, 15f)] private float m_AutonomyReviewMinimumSpacing = 2f;
    private readonly SceneReviewClock m_SceneReviewClock = new SceneReviewClock();
    private readonly HashSet<string> m_ActuallySpokenRounds = new HashSet<string>();
    private readonly Queue<JObject> m_SceneTimeSamples = new Queue<JObject>();
    private double m_SceneActiveSeconds, m_SceneTimeLastRealtime = -1;
    private bool m_SceneTimePaused, m_SceneTimeSuspended = true;
    private int m_SceneTimeSession;
    private double m_SceneDecisionAt = -1, m_SceneSampleAt = -1, m_SceneMotionAt = -1, m_SceneGazeAt = -1;
    private double m_SceneActivityAt, m_SceneDecisionWait;
    private string m_SceneActivity = "unobserved", m_SceneActivityPurpose = "", m_SceneActivityAction = "";
    private string m_SceneLastCapture = "", m_SceneDecisionPurpose = "";
    private JObject m_SceneLastArrivalTime, m_SceneLastFailureTime;
    private double m_SceneLastUnknownGap;
    private double m_ScenePauseStartedRealtime = -1;
    private bool m_ScenePoseKnown;
    private Vector3 m_SceneTrackedRoot;
    private Quaternion m_SceneTrackedView;
    private bool m_QuietAutonomyRound;
    private int m_SceneActivityRevision, m_SceneReviewEventRevision, m_SceneActionReviewUserTurn = -1;

    public string SceneTimeContextJson => BuildSceneTimeContext().ToString(Newtonsoft.Json.Formatting.None);

    private double SceneMonotonicNow() => AdvanceSceneClock(Time.realtimeSinceStartupAsDouble);

    // Explicit monotonic input keeps pause-gap bookkeeping deterministic in regression.
    private double AdvanceSceneClock(double real)
    {
        double delta = m_SceneTimeLastRealtime < 0 ? 0 : Math.Max(0, real - m_SceneTimeLastRealtime);
        m_SceneTimeLastRealtime = real;
        if (m_SceneTimeSuspended || m_SceneTimePaused || Time.timeScale <= 0) return m_SceneActiveSeconds;
        // A long missing Update interval includes Editor pause/sleep. Do not invent continuous observation or standing.
        if (delta > 2.5)
        {
            m_SceneLastUnknownGap = delta; m_ScenePoseKnown = false;
            m_SceneMotionAt = m_SceneGazeAt = -1;
        }
        else m_SceneActiveSeconds += delta;
        return m_SceneActiveSeconds;
    }

    private void BeginSceneTimeSession()
    {
        m_SceneTimeSession++; m_SceneActiveSeconds = 0;
        m_SceneTimeLastRealtime = Time.realtimeSinceStartupAsDouble;
        m_SceneTimeSuspended = false; m_SceneTimeSamples.Clear();
        m_SceneDecisionAt = m_SceneSampleAt = m_SceneMotionAt = m_SceneGazeAt = -1;
        m_SceneLastCapture = ""; m_ScenePoseKnown = false; m_SceneLastUnknownGap = 0;
        m_SceneActivity = "resumed-unobserved"; m_SceneActivityPurpose = ""; m_SceneActivityAction = ""; m_SceneActivityAt = 0;
        m_SceneDecisionPurpose = ""; m_SceneDecisionWait = 0; m_QuietAutonomyRound = false;
        m_SceneReviewClock.Clear(true); m_ActuallySpokenRounds.Clear();
        ClearScenePlaybackReview("scene-session-started");
        m_SceneActivityRevision++; m_SceneReviewEventRevision++; m_SceneActionReviewUserTurn = -1;
    }

    // Called from existing disable/session boundary; no additional Unity OnDisable competes with motion ownership.
    private void SuspendSceneTime(string reason)
    {
        SceneMonotonicNow(); m_SceneTimeSuspended = true; m_SceneReviewClock.Clear();
        ClearScenePlaybackReview("scene-session-suspended");
        m_ScenePoseKnown = false; m_SceneMotionAt = m_SceneGazeAt = -1;
        m_SceneActivity = "suspended"; m_SceneActivityPurpose = reason ?? "session-boundary";
    }

    private void OnApplicationPause(bool paused)
    {
        SceneMonotonicNow(); m_SceneTimePaused = paused;
        m_SceneTimeLastRealtime = Time.realtimeSinceStartupAsDouble;
        if (paused)
        {
            m_PreparedPlaybackEvidence?.Revoke("application-paused");
            CancelSequenceContinuation("application-paused");
            CancelSceneSearchTask("application-paused");
            m_ScenePauseStartedRealtime = Time.realtimeSinceStartupAsDouble;
            PauseSceneAction();
            CancelFirstReplyForPause();
            CancelRoomSpeechReview("application-paused");
            CancelAutonomyIntentProbeForReason("application-paused"); m_ScenePoseKnown = false;
            m_SceneMotionAt = m_SceneGazeAt = -1;
        }
        else
        {
            if (m_ScenePauseStartedRealtime >= 0)
                m_SceneLastUnknownGap = Math.Max(0, Time.realtimeSinceStartupAsDouble - m_ScenePauseStartedRealtime);
            m_ScenePauseStartedRealtime = -1;
            if (m_AgentRunning) ScheduleNextTick(2f, "scene-resumed");
        }
    }

    private void UpdateSceneTimePose()
    {
        double now = SceneMonotonicNow();
        if (!m_AgentRunning || m_SceneTimeSuspended || m_SceneTimePaused || Time.timeScale <= 0) return;
        ObserveRoomTaskSequenceProgress();
        var avatar = m_SceneVisionCapture != null ? m_SceneVisionCapture.Target : null;
        if (avatar == null) { m_ScenePoseKnown = false; return; }
        var eyes = avatar.GetComponent<NevaEyeTracking>();
        Quaternion view = eyes != null && eyes.IsInitialized ? eyes.HeadFrameRotation : avatar.transform.rotation;
        Vector3 root = avatar.transform.position;
        if (!m_ScenePoseKnown || Vector3.Distance(root, m_SceneTrackedRoot) > .01f)
        { m_SceneMotionAt = now; m_SceneTrackedRoot = root; }
        if (!m_ScenePoseKnown || Quaternion.Angle(view, m_SceneTrackedView) > 2f)
        { m_SceneGazeAt = now; m_SceneTrackedView = view; }
        m_ScenePoseKnown = true;
    }

    private void RecordSceneObservationTime(SceneVisionObservation observation, string purpose)
    {
        if (observation?.HasImage != true || !m_AgentRunning || observation.CaptureId == m_SceneLastCapture) return;
        double now = SceneMonotonicNow();
        m_SceneTimeSamples.Enqueue(new JObject { ["captureId"] = observation.CaptureId,
            ["purpose"] = purpose ?? "", ["activeSeconds"] = Math.Round(now, 2), ["capturedAtUtc"] = observation.CapturedAtUtc,
            ["viewDirectionWorld"] = JObject.Parse(JsonUtility.ToJson(observation.CameraRotation * Vector3.forward)),
            ["gapSincePreviousSampleSeconds"] = Age(now, m_SceneSampleAt),
            ["scope"] = "one actual sampled image; intervening visual events unknown" });
        while (m_SceneTimeSamples.Count > 4) m_SceneTimeSamples.Dequeue();
        m_SceneSampleAt = now; m_SceneLastCapture = observation.CaptureId;
    }

    private void RecordSceneActivity(string actionId, string intent, string label, string phase, string reason)
    {
        if (!m_AgentRunning || m_SceneTimeSuspended) return;
        double now = SceneMonotonicNow();
        bool changed = m_SceneActivityAction != actionId || m_SceneActivity != phase;
        m_SceneActivityAction = actionId ?? ""; m_SceneActivity = phase ?? "unknown";
        m_SceneActivityPurpose = label ?? intent ?? "";
        if (changed) { m_SceneActivityAt = now; m_SceneActivityRevision++; }
        JObject fact = new JObject { ["actionId"] = actionId, ["intent"] = intent, ["targetLabel"] = label,
            ["phase"] = phase, ["reason"] = reason, ["session"] = m_SceneTimeSession,
            ["activeSeconds"] = now, ["utc"] = DateTime.UtcNow.ToString("O") };
        // Entity approach reports the physical station first, then verifies its new view.
        // A hidden target does not erase the actual arrival; later visual confirmation of the
        // same action must not restart the measured time spent at that station.
        if ((phase == "arrived" || phase == "arrival-observing") && (string)m_SceneLastArrivalTime?["actionId"] != actionId)
        {
            fact["scope"] = "Actual floor-station arrival time; semantic target identity, current visibility and physical contact are separate facts.";
            m_SceneLastArrivalTime = fact;
        }
        if (phase == "failed" || phase == "rejected" || phase == "blocked") m_SceneLastFailureTime = fact;
        if (changed && !SpatialGoalRunnerOwnsGeneration(m_FormalResponseGeneration) &&
            (phase == "arrived" || phase == "observed" || phase == "failed" || phase == "blocked" || phase == "rejected"))
        {
            m_SceneActionReviewUserTurn = m_ActionConstraints.CurrentUserTurn;
            ScheduleNextTick(2f, "scene-action-" + phase);
        }
    }

    private void RecordSceneDecision(AutonomyIntentDecision decision)
    {
        double now = SceneMonotonicNow(); m_SceneDecisionAt = now;
        m_SceneDecisionPurpose = TruncateForFrame(decision.intent ?? "", 180);
        m_SceneDecisionWait = decision.wait_seconds;
        // A declined next action does not erase where the character actually arrived.
        if (!decision.proceed && !SceneActionIsBusy)
        { m_SceneActivity = "quiet-observation-or-rest"; m_SceneActivityAt = now; }
    }

    private static JToken Age(double now, double then) => then < 0 ? JValue.CreateNull() : new JValue(Math.Round(Math.Max(0, now - then), 2));
    private JObject BuildSceneTimeContext()
    {
        double now = SceneMonotonicNow();
        var arrival = m_SceneLastArrivalTime?.DeepClone() as JObject;
        if (arrival != null) arrival["elapsedActiveSeconds"] = (int?)arrival["session"] == m_SceneTimeSession
            ? Age(now, (double)arrival["activeSeconds"]) : JValue.CreateNull();
        return new JObject { ["source"] = "Unity-runtime-time-and-action-facts", ["session"] = m_SceneTimeSession,
            ["activeSeconds"] = Math.Round(now, 2), ["wallClockUtc"] = DateTime.UtcNow.ToString("O"),
            ["suspended"] = m_SceneTimeSuspended || m_SceneTimePaused,
            ["activity"] = m_SceneActivity, ["purpose"] = m_SceneActivityPurpose,
            ["activityForSeconds"] = Age(now, m_SceneActivityAt), ["sinceLastDecisionSeconds"] = Age(now, m_SceneDecisionAt),
            ["stationaryForSeconds"] = m_ScenePoseKnown ? Age(now, m_SceneMotionAt) : JValue.CreateNull(),
            ["viewHeldForSeconds"] = m_ScenePoseKnown ? Age(now, m_SceneGazeAt) : JValue.CreateNull(),
            ["poseThresholds"] = "root translation 1 cm; head direction 2 degrees; breathing below thresholds is not a new activity",
            ["sinceLastSampleSeconds"] = Age(now, m_SceneSampleAt), ["lastDecisionIntent"] = m_SceneDecisionPurpose,
            ["modelRequestedRecheckSeconds"] = m_SceneDecisionWait,
            ["nextReviewInSeconds"] = m_SceneReviewClock.Pending ? Age(m_SceneReviewClock.Deadline, now) : JValue.CreateNull(),
            ["reviewDue"] = m_SceneReviewClock.IsDue(now), ["queuedWakeReason"] = m_SceneReviewClock.Reason,
            ["latestQueuedWakeReason"] = m_SceneReviewClock.LatestReason, ["coalescedWakeEvents"] = m_SceneReviewClock.CoalescedEvents,
            ["scenePlaybackReview"] = ScenePlaybackReviewContext(),
            ["reviewsInLastMinute"] = m_SceneReviewClock.RequestsInLastMinute(now),
            ["reviewBudgetPerMinute"] = m_AutonomyReviewsPerMinute,
            ["lastSuccessfulArrival"] = arrival, ["lastFailedAttempt"] = m_SceneLastFailureTime?.DeepClone(),
            ["samples"] = new JArray(m_SceneTimeSamples), ["lastUnobservedRuntimeGapSeconds"] = m_SceneLastUnknownGap,
            ["scope"] = "Only actual samples are seen. Between samples visual events are unknown; private model reasoning is intermittent. Pauses/session closure are not continuous experience. You may remain quiet, look elsewhere, approach or rest; no action is compulsory." };
    }

    private void RegisterActuallySpokenRound(int generation, int round)
    {
        if (!m_AgentRunning) return;
        if (m_ActuallySpokenRounds.Add(generation + ":" + round)) m_ConsecutiveAITurns++;
    }

    private bool SceneActionIsBusy => m_SceneActionActive;
}
