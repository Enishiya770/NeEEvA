using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Motion;
using UnityEngine;

/// <summary>Follow scheduling and real connected-bridge lifecycle without a model or motion request.</summary>
public static class ArdyRoomFollowRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    [Serializable] private sealed class Report
    {
        public bool passed;
        public int checks;
        public string failure, checkedAtUtc = DateTime.UtcNow.ToString("o");
        public string scope = "Fixed follow hysteresis cases and actual connected scene bridge: near-user waiting, ordinary speech, target/action/revision isolation, explicit stop, missing support, rebound user, no movement after cancellation. No generated follow leg or live ASR/LLM/TTS reliability claim.";
        public List<string> cases = new List<string>();
    }

    public static int RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge)
    {
        checks = 0; var report = new Report();
        Check(Application.isPlaying && chat != null && bridge != null && bridge.IsConnected && !chat.enabled,
            "Use the dormant ChatSample in the connected isolated Play-mode fixture.");
        Transform user = bridge.userHead;
        Vector3 savedUser = user.position, savedRoot = bridge.avatar.transform.position;
        Quaternion savedRotation = bridge.avatar.transform.rotation;
        float savedDistance = bridge.approachDistance;
        try
        {
            PolicyChecks(); report.cases.Add("bounded-distance-time-target-hysteresis-and-nonfinite-inputs");
            bridge.StopMovement(); bridge.approachDistance = 1f;
            user.position = FindNearUser(bridge);
            var snapshot = bridge.ReadCurrentTargetEvidence(true);
            int operation = bridge.Controller.OperationRevision;
            Check(bridge.FollowForTarget("follow-fixture-one", snapshot.currentTargetRevision, out string failure), failure);
            Check(bridge.IsFollowing && bridge.ActiveTaskId == "follow-fixture-one" && bridge.TaskMode == "follow" &&
                !bridge.ReservesBody && bridge.Phase == "follow-waiting" && bridge.Controller.OperationRevision == operation,
                "Near follow should wait with a persistent task identity and release body ownership, without starting generation.");
            for (int i = 0; i < 80; i++) Call(bridge, "TickFollow", Time.realtimeSinceStartup + i * .1f);
            Check(bridge.IsFollowing && bridge.Controller.OperationRevision == operation &&
                Vector3.Distance(savedRoot, bridge.avatar.transform.position) < .0001f,
                "A stationary nearby follow target repeatedly dispatched or translated the avatar.");
            var current = bridge.ReadCurrentTargetEvidence();
            Check(current.followActive && current.taskMode == "follow" && current.activeTaskId == "follow-fixture-one" &&
                !current.bodyReserved && current.attemptStatus == "monitoring" && current.followLegCount == 0 &&
                current.followResumeDistance == 1.45f && current.controllerOperationRevision == operation,
                "Read-only evidence conflated follow waiting with no task, movement, or a completed action.");
            Check(!current.poseReturning && current.movingTargetRevision == bridge.Controller.MovingTargetRevision &&
                current.committedTargetRevision == bridge.Controller.CommittedTargetRevision &&
                current.movingTargetNeedsRestart == bridge.Controller.RetargetNeedsRestart &&
                current.movingTargetStatus == bridge.Controller.RetargetStatus,
                "Read-only follow evidence did not preserve target preparation/commit and pose-return facts.");
            report.cases.Add("near-user-follow-persists-without-generation-or-body-reservation");

            foreach (string cause in new[] { "new-user-turn", "barge-in", "user-started-speaking" })
            {
                Call(bridge, "OnCancelled", 1, cause);
                Check(bridge.IsFollowing && bridge.ActiveTaskId == "follow-fixture-one", "Ordinary speech ended follow: " + cause);
            }
            report.cases.Add("ordinary-speech-does-not-cancel-persistent-follow");

            Check(!bridge.StopForTaskIfCurrent("bad-stop", "another-task", operation, out _) && bridge.IsFollowing,
                "A stop captured for another task cancelled follow.");
            Check(!bridge.StopForTaskIfCurrent("bad-stop", "follow-fixture-one", operation + 1, out _) && bridge.IsFollowing,
                "A stop captured for another controller operation cancelled follow.");
            Check(bridge.StopForTaskIfCurrent("follow-stop", "follow-fixture-one", operation, out failure), failure);
            Check(!bridge.IsFollowing && !bridge.ReservesBody && bridge.ActiveTaskId == "" && bridge.Phase == "stopped" &&
                bridge.ActionId == "follow-stop", "A matching fast stop did not end the whole persistent task.");
            for (int i = 0; i < 30; i++) Call(bridge, "TickFollow", Time.realtimeSinceStartup + 100 + i);
            Check(!bridge.IsFollowing && bridge.Controller.OperationRevision == operation && bridge.Phase == "stopped",
                "A future follow tick restarted the cancelled task.");
            report.cases.Add("stop-bound-to-task-and-operation-and-no-post-stop-restart");

            snapshot = bridge.ReadCurrentTargetEvidence(true);
            Check(bridge.FollowForTarget("follow-fixture-cross-leg", snapshot.currentTargetRevision, out failure), failure);
            var controllerOperation = bridge.Controller.GetType().GetProperty("OperationRevision");
            var ownedOperation = typeof(ArdyRoomDialogueBridge).GetField("operationRevision", Private);
            try
            {
                // Inject operation identities, not a fake body completion or a generation result.
                // The runtime harness separately checks actual rolling updates and arrival.
                controllerOperation.SetValue(bridge.Controller, operation + 2);
                Check(!bridge.StopForTaskIfCurrent("external-stop", "follow-fixture-cross-leg", operation, out _, true) && bridge.IsFollowing,
                    "A pending follow stop cancelled an external replacement controller operation.");
                Check(!bridge.StopForTaskIfCurrent("external-stop", "follow-fixture-cross-leg", operation + 2, out _, true) && bridge.IsFollowing,
                    "Matching an external controller version was mistaken for bridge-owned follow work.");
                ownedOperation.SetValue(bridge, operation + 2);
                Check(!bridge.StopForTaskIfCurrent("legacy-stop", "follow-fixture-cross-leg", operation, out _) && bridge.IsFollowing,
                    "The existing four-argument stop lost strict operation binding.");
                Check(!bridge.StopForTaskIfCurrent("wrong-task-stop", "prior-follow-task", operation, out _, true) && bridge.IsFollowing,
                    "Allowing internal follow legs also allowed cancelling another task identity.");
                Check(!bridge.StopForTaskIfCurrent("future-stop", "follow-fixture-cross-leg", operation + 3, out _, true) && bridge.IsFollowing,
                    "A future, never-observed operation was accepted as a prior follow leg.");
                Check(bridge.StopForTaskIfCurrent("cross-leg-stop", "follow-fixture-cross-leg", operation, out failure, true), failure);
                Check(!bridge.IsFollowing && bridge.Phase == "stopped" && bridge.ActionId == "cross-leg-stop",
                    "A stop captured for the same follow task was discarded after its owned leg advanced.");
            }
            finally
            {
                controllerOperation.SetValue(bridge.Controller, operation);
                ownedOperation.SetValue(bridge, operation);
                bridge.StopMovement();
            }
            report.cases.Add("follow-stop-survives-owned-leg-advance-but-rejects-external-operation-and-new-task");

            snapshot = bridge.ReadCurrentTargetEvidence(true);
            Check(bridge.FollowForTarget("follow-fixture-external-waiting", snapshot.currentTargetRevision, out failure), failure);
            Vector3 beforeExternalUser = user.position;
            int untouchedRevision = bridge.Controller.Revision;
            try
            {
                controllerOperation.SetValue(bridge.Controller, operation + 4);
                user.position += Vector3.forward * 2f;
                Call(bridge, "TickFollow", Time.realtimeSinceStartup + 3);
                Check(!bridge.IsFollowing && bridge.ActiveTaskId == "" && bridge.Phase == "cancelled" &&
                    bridge.Controller.OperationRevision == operation + 4 && bridge.Controller.Revision == untouchedRevision,
                    "A waiting follow reclaimed or cancelled an externally replaced controller operation after the user moved.");
            }
            finally
            {
                user.position = beforeExternalUser;
                controllerOperation.SetValue(bridge.Controller, operation);
                ownedOperation.SetValue(bridge, operation);
                bridge.StopMovement();
            }
            report.cases.Add("waiting-follow-yields-to-external-operation-without-cancelling-it");

            snapshot = bridge.ReadCurrentTargetEvidence(true);
            user.position += Vector3.up * .02f;
            Check(!bridge.FollowForTarget("stale-follow", snapshot.currentTargetRevision, out _) && !bridge.IsFollowing,
                "An obsolete target decision started following.");
            user.position -= Vector3.up * .02f;
            snapshot = bridge.ReadCurrentTargetEvidence(true);
            Check(bridge.FollowForTarget("follow-fixture-support", snapshot.currentTargetRevision, out failure), failure);
            user.position += Vector3.up * 10;
            Call(bridge, "TickFollow", Time.realtimeSinceStartup + 2);
            Check(!bridge.IsFollowing && bridge.Phase == "unavailable" && !bridge.ReservesBody,
                "Losing a supported user anchor left a follow task able to resume automatically.");
            report.cases.Add("stale-start-is-rejected-and-lost-support-ends-follow");

            user.position = FindNearUser(bridge);
            snapshot = bridge.ReadCurrentTargetEvidence(true);
            Check(bridge.FollowForTarget("follow-fixture-binding", snapshot.currentTargetRevision, out failure), failure);
            var rebound = new GameObject("Follow fixture replacement head");
            try
            {
                rebound.transform.position = user.position; bridge.userHead = rebound.transform;
                Call(bridge, "TickFollow", Time.realtimeSinceStartup + 3);
                Check(!bridge.IsFollowing && bridge.Phase == "cancelled", "Changing user binding silently retargeted persistent follow.");
            }
            finally { bridge.userHead = user; UnityEngine.Object.Destroy(rebound); }
            report.cases.Add("changed-user-binding-ends-follow");
            Check(bridge.Controller.OperationRevision == operation && !bridge.Controller.IsBusy &&
                Vector3.Distance(savedRoot, bridge.avatar.transform.position) < .0001f &&
                Quaternion.Angle(savedRotation, bridge.avatar.transform.rotation) < .001f,
                "Lifecycle regression unexpectedly generated a body task or changed root pose.");
            checks += ArdyRoomFollowFailureIdentityRegression.RunChecks(bridge);
            report.cases.Add("controller-failure-identity-bounded-recovery-parent-and-nonplanning-diagnostics");
            report.passed = true;
        }
        catch (Exception error) { report.failure = error.ToString(); throw; }
        finally
        {
            bridge.StopMovement(); bridge.userHead = user; user.position = savedUser;
            bridge.approachDistance = savedDistance;
            bridge.avatar.transform.SetPositionAndRotation(savedRoot, savedRotation);
            bridge.InvalidateCurrentTargetAssessment();
            report.checks = checks;
            string path = Path.GetFullPath("Tools/MotionAdapter/reports/room-follow-regression.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(report, true) + "\n");
        }
        Debug.Log("[ArdyRoomFollowRegression] passed " + checks + " checks.");
        return checks;
    }

    public static int RunPolicyChecks() { checks = 0; PolicyChecks(); return checks; }

    private static void PolicyChecks()
    {
        foreach (float distance in new[] { 0f, .5f, .75f, 1f, 1.25f, 1.44f, 1.45f })
            Check(!ArdyRoomFollowPolicy.NeedsLeg(distance, 1f), "Follow should hold inside its resume band: " + distance);
        foreach (float distance in new[] { 1.46f, 2f, 5f })
            Check(ArdyRoomFollowPolicy.NeedsLeg(distance, 1f), "Follow failed to resume outside its distance band.");
        Check(!ArdyRoomFollowPolicy.NeedsLeg(float.NaN, 1f) && !ArdyRoomFollowPolicy.NeedsLeg(float.PositiveInfinity, 1f),
            "Nonfinite distance became an executable follow trigger.");
        Check(!ArdyRoomFollowPolicy.TargetMoved(Vector3.zero, new Vector3(0, 2f, 0)), "Vertical head movement alone retargeted follow.");
        Check(!ArdyRoomFollowPolicy.TargetMoved(Vector3.zero, new Vector3(.34f, 0, 0)), "Subthreshold horizontal jitter retargeted follow.");
        Check(ArdyRoomFollowPolicy.TargetMoved(Vector3.zero, new Vector3(.36f, 0, 0)), "Large horizontal change did not request a target refresh.");
        Check(ArdyRoomFollowPolicy.TargetMoved(Vector3.zero, new Vector3(.25f, 0, .25f)), "Diagonal target movement ignored horizontal magnitude.");
        Check(!ArdyRoomFollowPolicy.TargetNeedsUpdate(Vector3.zero, new Vector3(.2f, 0, 0), .299f, 0),
            "A subthreshold moving target bypassed the quiet-period gate.");
        Check(ArdyRoomFollowPolicy.TargetNeedsUpdate(Vector3.zero, new Vector3(.2f, 0, 0), .30f, 0),
            "A settled target retained a meaningful final horizontal residual.");
        Check(!ArdyRoomFollowPolicy.TargetNeedsUpdate(Vector3.zero, new Vector3(.03f, 0, 0), 1f, 0),
            "Small settled head jitter repeatedly refreshed the target.");
        Check(!ArdyRoomFollowPolicy.TargetNeedsUpdate(Vector3.zero, new Vector3(0, 2f, 0), 1f, 0),
            "Vertical head motion triggered a settled horizontal target update.");
        Check(!ArdyRoomFollowPolicy.TargetNeedsUpdate(Vector3.zero, new Vector3(.06f, 0, 0), 1f, 0) &&
            ArdyRoomFollowPolicy.TargetNeedsUpdate(Vector3.zero, new Vector3(.0601f, 0, 0), 1f, 0),
            "The settled residual threshold did not preserve its strict boundary.");
        Check(ArdyRoomFollowPolicy.TargetNeedsUpdate(Vector3.zero, new Vector3(.36f, 0, 0), .01f, 0),
            "The settled-target refinement delayed an ordinary large moving-target update.");
        Check(!ArdyRoomFollowPolicy.MayDispatch(10.99f, 10f, false, 1f), "Follow dispatched before the minimum interval.");
        Check(ArdyRoomFollowPolicy.MayDispatch(11.25f, 10f, false, 0), "Follow remained blocked after the interval.");
        Check(ArdyRoomFollowPolicy.MayDispatch(1f, float.NegativeInfinity, false, 0), "First authorized follow could not dispatch.");
        Check(!ArdyRoomFollowPolicy.MayDispatch(100f, 0, true, .34f), "Blocked follow repeated a failed target just because time elapsed.");
        Check(ArdyRoomFollowPolicy.MayDispatch(100f, 0, true, .36f), "A genuinely moved blocked target could not be rechecked.");
        Check(!ArdyRoomFollowPolicy.MayDispatch(float.NaN, 0, false, 0) &&
            !ArdyRoomFollowPolicy.MayDispatch(100, float.NaN, false, 0) &&
            !ArdyRoomFollowPolicy.MayDispatch(100, 0, true, float.NaN), "Nonfinite scheduling input released follow work.");
    }

    private static Vector3 FindNearUser(ArdyRoomDialogueBridge bridge)
    {
        Transform user = bridge.userHead; Vector3 saved = user.position;
        Vector3 ground = bridge.avatar.transform.position + Vector3.up * bridge.importedReference.groundY * bridge.avatar.transform.lossyScale.y;
        foreach (float radius in new[] { 1f, .9f, 1.1f })
            for (int turn = 0; turn < 24; turn++)
            {
                Vector3 candidate = ground + Quaternion.Euler(0, turn * 15, 0) * Vector3.forward * radius + Vector3.up * 1.6f;
                user.position = candidate;
                if (ArdyRoomApproachPlanner.TryGetUserGround(bridge.Navigation, user, out Vector3 supported, out _) &&
                    Mathf.Abs(supported.y - ground.y) <= bridge.Navigation.MaxRouteHeightVariation)
                { user.position = saved; return candidate; }
            }
        user.position = saved;
        throw new InvalidOperationException("No nearby supported same-level user fixture is available.");
    }

    private static object Call(object target, string name, params object[] arguments)
    {
        var method = target.GetType().GetMethod(name, Private);
        if (method == null) throw new MissingMethodException(target.GetType().Name, name);
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static void Check(bool ok, string message) { checks++; if (!ok) throw new InvalidOperationException(message); }
}
