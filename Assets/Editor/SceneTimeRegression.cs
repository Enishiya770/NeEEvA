using System;
using System.IO;
using System.Reflection;
using AIChat.Agent;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Deterministic scheduling and production Chat hooks; no HTTP, audio, navigation or invented model choice.</summary>
public static class SceneTimeRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    public static int RunChecks()
    {
        checks = 0;
        var clock = new SceneReviewClock();
        clock.Schedule(10, 45, "autonomy-no-new-intent", false);
        Require(clock.Deadline == 55 && !clock.IsDue(54.99) && clock.IsDue(55), "One exact 45-second deadline; no second gate.");
        clock.Schedule(20, 2, "scene-action-arrived", true);
        clock.Schedule(21, 600, "default", false);
        Require(clock.Deadline == 22 && clock.Reason == "scene-action-arrived", "Formal completion cannot erase queued arrival.");
        clock.Schedule(21.5, 2, "scene-action-observed", true);
        Require(clock.Deadline == 22, "Repeated events coalesce at earliest due time.");
        Require(clock.Consume(30) == "scene-action-arrived" && !clock.Pending && clock.LastQueueDelay == 8 && clock.LastActualElapsed == 10,
            "Busy execution retains event; measured lateness differs from requested duration.");
        for (int i = 0; i < 6; i++) clock.RecordRequest(i * 5);
        Require(!clock.CanRequest(26, 6, 2, out double ready) && ready == 60, "Six requests consume the rolling minute budget.");
        Require(clock.CanRequest(60, 6, 2, out _) && clock.RequestsInLastMinute(60) == 5, "Budget opens only as oldest request expires.");
        clock.RecordRequest(60);
        Require(!clock.CanRequest(61, 6, 2, out ready) && ready == 65, "Spacing and rolling budget are both enforced.");
        clock.Clear(true); clock.RecordRequest(100);
        Require(!clock.CanRequest(101, 6, 2, out ready) && ready == 102, "One completed request cannot immediately start another.");
        clock.Schedule(100, 1, "scheduled", false); clock.Schedule(100.5, 2, "spike-pull-forward", true);
        Require(clock.Deadline == 101 && clock.PriorityEvent, "A wake event never postpones an earlier normal deadline.");

        var go = new GameObject("SceneTimeRegression-owned-chat") { hideFlags = HideFlags.HideAndDontSave };
        go.SetActive(false); // The isolated hook fixture never initializes ChatSample's scene UI/audio lifecycle.
        try
        {
            ChatSample chat = go.AddComponent<ChatSample>();
            Set(chat, "m_AgentRunning", true); Set(chat, "m_LogAgentLoop", false);
            Call(chat, "BeginSceneTimeSession"); Set(chat, "m_ConsecutiveAITurns", 4);
            Call(chat, "ScheduleNextTick", 45f, "autonomy-no-new-intent");
            var productionClock = (SceneReviewClock)Get(chat, "m_SceneReviewClock");
            Require(productionClock.RequestedSeconds == 45 && Math.Abs(productionClock.Deadline - productionClock.ScheduledAt - 45) < .001,
                "Actual Chat scheduler preserves 45 seconds despite four prior spoken rounds.");
            Require((float)Get(chat, "m_AutonomyProbeNotBefore") < 0, "Actual Chat has no hidden not-before wait.");
            Set(chat, "m_Urge", null); Set(chat, "m_BringForwardOnSpike", true);
            Call(chat, "OnEnvironmentSpike", .03f);
            Require(productionClock.Reason == "spike-pull-forward" && productionClock.Deadline < 10,
                "Non-Urge environmental wake uses the actual deadline, not the retired coroutine handle.");
            double firstSpikeDeadline = productionClock.Deadline;
            Call(chat, "OnEnvironmentSpike", .04f);
            Require(productionClock.Deadline == firstSpikeDeadline, "Repeated ambient sound does not continually reschedule one silence.");
            Call(chat, "ClearScheduledAgentWake");
            var urge = new UrgeModel(); Set(urge, "m_LogUrge", false); Set(urge, "m_U", 1.2f); Set(chat, "m_Urge", urge);
            string urgeReason = (string)Call(chat, "ConsumeSceneUrgeEvent");
            if (urgeReason != null) Call(chat, "ScheduleNextTick", 1f, urgeReason);
            Require(urge.Value == 0 && productionClock.Pending, "Threshold event consumes all pending urge; no inherited trigger remains.");
            double eventScheduledAt = productionClock.ScheduledAt;
            Require(Call(chat, "ConsumeSceneUrgeEvent") == null && productionClock.ScheduledAt == eventScheduledAt,
                "The actual Update event-consumer cannot repeat an already consumed urge event.");
            Call(chat, "ClearScheduledAgentWake");
            Call(chat, "RecordSceneActivity", "ground-first", "approach-object", "观察参照", "arrival-observing", "actual ground arrival; current object sight pending");
            object groundArrival = Get(chat, "m_SceneLastArrivalTime");
            var groundFacts = JObject.Parse(chat.SceneTimeContextJson);
            Require((string)groundFacts["lastSuccessfulArrival"]["actionId"] == "ground-first" &&
                (string)groundFacts["lastSuccessfulArrival"]["phase"] == "arrival-observing" &&
                ((string)groundFacts["lastSuccessfulArrival"]["scope"]).Contains("visibility"),
                "Actual floor arrival is recorded before independent object reobservation, with explicit limits.");
            Call(chat, "RecordSceneActivity", "ground-first", "approach-object", "观察参照", "arrived-object-unconfirmed", "object hidden in arrival image");
            Require(ReferenceEquals(groundArrival, Get(chat, "m_SceneLastArrivalTime")),
                "A hidden target retains the exact physical arrival event and time.");
            Call(chat, "RecordSceneActivity", "ground-first", "approach-object", "观察参照", "arrived", "same entity later confirmed");
            Require(ReferenceEquals(groundArrival, Get(chat, "m_SceneLastArrivalTime")),
                "Later same-action visual confirmation does not restart time spent at the station.");
            Call(chat, "ClearScheduledAgentWake");
            Call(chat, "RecordSceneActivity", "arrive-1", "approach-object", "书架", "arrived", "actual terminal");
            Call(chat, "ScheduleNextTick", 45f, "default");
            Require(productionClock.Reason == "scene-action-arrived", "Production action hook wins over normal round cleanup.");
            Call(chat, "RecordSceneActivity", "attempt-2", "approach-object", "盆栽", "rejected", "not grounded");
            JObject facts = JObject.Parse(chat.SceneTimeContextJson);
            Require((string)facts["lastSuccessfulArrival"]["actionId"] == "arrive-1" && (string)facts["lastFailedAttempt"]["actionId"] == "attempt-2",
                "A failed later attempt preserves the independent actual arrival.");
            Require((double)facts["lastSuccessfulArrival"]["elapsedActiveSeconds"] >= 0, "Current session arrival has measured active age.");
            Set(chat, "m_ConsecutiveAITurns", 0);
            Call(chat, "RegisterActuallySpokenRound", 7, 1); Call(chat, "RegisterActuallySpokenRound", 7, 1);
            Require((int)Get(chat, "m_ConsecutiveAITurns") == 1, "Multiple audio chunks in one actual spoken round count once.");
            Call(chat, "RegisterActuallySpokenRound", 7, 2);
            Require((int)Get(chat, "m_ConsecutiveAITurns") == 2, "A different actually audible round counts once.");
            // A pure no-proceed decision does not invoke playback and cannot increase spoken-turn fatigue.
            Type decisionType = typeof(ChatSample).GetNestedType("AutonomyIntentDecision", BindingFlags.NonPublic);
            object decision = Activator.CreateInstance(decisionType, true);
            decisionType.GetField("wait_seconds").SetValue(decision, 45f);
            Call(chat, "RecordSceneDecision", decision);
            Require((int)Get(chat, "m_ConsecutiveAITurns") == 2 && (double)JObject.Parse(chat.SceneTimeContextJson)["modelRequestedRecheckSeconds"] == 45,
                "Private silence records its intended recheck without being counted as speech.");
            Call(chat, "SuspendSceneTime", "test-disabled");
            Require(!productionClock.Pending && (bool)JObject.Parse(chat.SceneTimeContextJson)["suspended"], "Disable cancels future wake and marks suspended experience.");
            Call(chat, "BeginSceneTimeSession");
            facts = JObject.Parse(chat.SceneTimeContextJson);
            Require((string)facts["lastSuccessfulArrival"]["actionId"] == "arrive-1" && facts["lastSuccessfulArrival"]["elapsedActiveSeconds"].Type == JTokenType.Null,
                "Session resume retains historical arrival without claiming continuously standing during closure.");
            Set(chat, "m_SceneTimeLastRealtime", 100d);
            Set(chat, "m_ScenePoseKnown", true); Set(chat, "m_SceneMotionAt", 0d); Set(chat, "m_SceneGazeAt", 0d);
            Call(chat, "AdvanceSceneClock", 130d);
            facts = JObject.Parse(chat.SceneTimeContextJson);
            Require((double)facts["lastUnobservedRuntimeGapSeconds"] >= 29 && facts["stationaryForSeconds"].Type == JTokenType.Null && facts["viewHeldForSeconds"].Type == JTokenType.Null,
                "An injected monotonic 30-second Update gap is unknown, not fabricated continuous pose observation.");
            Require((string)facts["scope"] != null && ((string)facts["scope"]).Contains("intermittent"), "Model context explicitly distinguishes intermittent inference from continuous experience.");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
        return checks;
    }
    public static void RunBatch()
    {
        string[] args = Environment.GetCommandLineArgs(); string output = null;
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-nevaSceneTimeOutput") output = args[i + 1];
        if (string.IsNullOrEmpty(output)) throw new ArgumentException("-nevaSceneTimeOutput required");
        Directory.CreateDirectory(output);
        var report = new JObject { ["scope"] = "Deterministic scheduler and real Chat temporal hooks; no model/HTTP/graphics/locomotion" };
        int exit = 0;
        try { report["checks"] = RunChecks(); report["status"] = "passed"; }
        catch (Exception error) { report["status"] = "failed"; report["error"] = error.ToString(); exit = 1; }
        File.WriteAllText(Path.Combine(output, "report.json"), report.ToString());
        EditorApplication.Exit(exit);
    }
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
    private static void Require(bool condition, string reason) { checks++; if (!condition) throw new InvalidOperationException(reason); }
}
