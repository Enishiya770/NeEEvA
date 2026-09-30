using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Motion;
using UnityEngine;

/// <summary>Real support colliders and the connected bridge; no source-name exceptions or fake ground.</summary>
public static class ArdyRoomFollowSupportRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static int Checks { get; private set; }
    [Serializable] private sealed class Report
    {
        public bool passed;
        public int checks;
        public string error;
        public List<string> cases = new List<string>();
        public string scope = "Connected isolated flat-room bridge with actual Physics support obstruction, bounded retries, missing floor, restored support, stop and replacement fencing. Runtime entry additionally uses real ARDY playback before obstruction and after fresh replanning.";
    }

    public static int RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge)
    {
        Checks = 0; var report = new Report();
        Check(Application.isPlaying && chat != null && !chat.enabled && bridge != null && bridge.IsConnected &&
            !bridge.ReservesBody, "Use the dormant connected isolated flat-room fixture.");
        Vector3 savedUser = bridge.userHead.position, root = bridge.avatar.transform.position;
        float savedDistance = bridge.approachDistance;
        GameObject obstacle = null;
        try
        {
            bridge.StopMovement(); bridge.approachDistance = 1f;
            Vector3 near = FindUser(bridge, 1f);
            bridge.userHead.position = near;
            Start(bridge, "support-transient");
            int operation = bridge.Controller.OperationRevision;
            obstacle = Obstacle(near);
            Check(!ArdyRoomApproachPlanner.TryGetUserGround(bridge.Navigation, bridge.userHead, out _, out _, out var support) &&
                support.found && support.objectName == obstacle.name,
                "An unregistered higher solid must block support, even when registered floor exists below it.");
            float now = Time.realtimeSinceStartup;
            Tick(bridge, now);
            Check(bridge.IsFollowing && bridge.Phase == "follow-paused" && !bridge.ReservesBody &&
                bridge.PeekCurrentTargetEvidence().followSupportCheckCount == 1,
                "A transient support failure must pause, release motion, and retain task identity.");
            for (int i = 1; i <= 20; i++) Tick(bridge, now + i * .01f);
            Check(bridge.PeekCurrentTargetEvidence().followSupportCheckCount == 1,
                "Support checks repeated every frame instead of observing their bounded cadence.");
            obstacle.SetActive(false); Physics.SyncTransforms();
            Tick(bridge, now + ArdyRoomFollowPolicy.SupportCheckInterval + .01f);
            Check(bridge.IsFollowing && bridge.Phase == "follow-waiting" &&
                !bridge.PeekCurrentTargetEvidence().followSupportRecoveryPending && bridge.Controller.OperationRevision == operation,
                "Restored same-level support failed to clear the pause, or a nearby target unnecessarily generated motion.");
            report.cases.Add("real-higher-solid-blocks-support-and-removal-restores-near-follow");

            obstacle.SetActive(true); Physics.SyncTransforms(); now += 2;
            Tick(bridge, now);
            for (int i = 1; i < ArdyRoomFollowPolicy.MaximumSupportChecks + 10; i++)
                Tick(bridge, now + i * (ArdyRoomFollowPolicy.SupportCheckInterval + .01f));
            var evidence = bridge.PeekCurrentTargetEvidence();
            Check(bridge.IsFollowing && evidence.followSupportWaitingForMovedTarget &&
                evidence.followSupportCheckCount == ArdyRoomFollowPolicy.MaximumSupportChecks &&
                evidence.activeBlockingCategory == "user-support" && !bridge.ReservesBody,
                "An unchanged unsupported target either ended follow or exhausted no finite retry budget.");
            Check(Vector3.Distance(root, bridge.avatar.transform.position) < .0001f && bridge.Controller.OperationRevision == operation,
                "Unsupported follow dispatched motion or changed the actor root.");
            obstacle.SetActive(false); Physics.SyncTransforms();
            Tick(bridge, now + 20);
            Check(bridge.PeekCurrentTargetEvidence().followSupportWaitingForMovedTarget,
                "An exhausted stationary target silently regained unlimited polling authority.");
            bridge.userHead.position = FindUser(bridge, 1f, near);
            Tick(bridge, now + 21);
            Check(bridge.IsFollowing && !bridge.PeekCurrentTargetEvidence().followSupportRecoveryPending && bridge.Phase == "follow-waiting",
                "A materially changed, independently verified same-floor target could not release the pause.");
            report.cases.Add("stationary-support-retries-are-bounded-and-new-position-renews-checks");

            bridge.userHead.position += Vector3.up * 10;
            Tick(bridge, now + 22);
            Check(bridge.IsFollowing && bridge.Phase == "follow-paused" && !bridge.ReservesBody &&
                !ArdyRoomApproachPlanner.TryGetUserGround(bridge.Navigation, bridge.userHead, out _, out _),
                "No real floor must never be converted into executable projected ground.");
            bridge.StopForTask("support-stop");
            bridge.userHead.position = near;
            for (int i = 0; i < 10; i++) Tick(bridge, now + 30 + i);
            Check(!bridge.IsFollowing && !bridge.PeekCurrentTargetEvidence().followSupportRecoveryPending &&
                bridge.Phase == "stopped" && bridge.ActionId == "support-stop" && bridge.Controller.OperationRevision == operation,
                "Explicit stop left a retry which restarted the old follow intent.");
            report.cases.Add("no-floor-stays-motionless-and-stop-clears-recovery");

            Start(bridge, "support-old-task");
            obstacle.SetActive(true); Physics.SyncTransforms(); Tick(bridge, now + 45);
            obstacle.SetActive(false); Physics.SyncTransforms();
            Start(bridge, "support-new-task");
            Check(bridge.IsFollowing && bridge.ActiveTaskId == "support-new-task" &&
                !bridge.PeekCurrentTargetEvidence().followSupportRecoveryPending,
                "A replacement task inherited the old support-recovery owner or budget.");
            report.cases.Add("replacement-task-clears-prior-support-recovery");
            report.passed = true;
        }
        catch (Exception error) { report.error = error.ToString(); throw; }
        finally
        {
            if (obstacle != null) { obstacle.SetActive(false); UnityEngine.Object.Destroy(obstacle); }
            bridge.StopMovement(); bridge.userHead.position = savedUser; bridge.approachDistance = savedDistance;
            bridge.InvalidateCurrentTargetAssessment(); Save(report, "room-follow-support-regression.json");
        }
        return Checks;
    }

    public static IEnumerator RunRuntimeChecks(ChatSample chat, ArdyRoomDialogueBridge bridge)
    {
        Checks = 0; var report = new Report();
        Check(Application.isPlaying && chat != null && !chat.enabled && bridge != null && bridge.IsConnected &&
            !bridge.ReservesBody, "Use the dormant connected flat-room fixture with real ARDY service.");
        Vector3 savedUser = bridge.userHead.position, savedRoot = bridge.avatar.transform.position;
        Quaternion savedRotation = bridge.avatar.transform.rotation;
        float savedDistance = bridge.approachDistance;
        GameObject obstacle = null;
        try
        {
            bridge.StopMovement(); bridge.approachDistance = 1f;
            bridge.userHead.position = FindUser(bridge, 3f);
            Start(bridge, "support-runtime");
            float deadline = Time.realtimeSinceStartup + 50;
            while (Vector3.Distance(savedRoot, bridge.avatar.transform.position) < .12f && bridge.IsFollowing && Time.realtimeSinceStartup < deadline)
                yield return null;
            Check(bridge.IsFollowing && bridge.Controller.IsContinuousFollowActive &&
                Vector3.Distance(savedRoot, bridge.avatar.transform.position) >= .12f,
                "Real native follow did not start before the support obstruction.");
            int oldOperation = bridge.Controller.OperationRevision;
            obstacle = Obstacle(bridge.userHead.position);
            deadline = Time.realtimeSinceStartup + 3;
            while ((bridge.Phase != "follow-paused" || bridge.ReservesBody) && bridge.IsFollowing && Time.realtimeSinceStartup < deadline)
                yield return null;
            Check(bridge.IsFollowing && bridge.Phase == "follow-paused" && !bridge.Controller.IsMoving &&
                !bridge.Controller.IsBusy && !bridge.Controller.IsContinuousFollowActive,
                "Support loss did not cancel the live native segment while preserving the follow task.");
            Vector3 pausedRoot = bridge.avatar.transform.position;
            deadline = Time.realtimeSinceStartup + .7f;
            while (Time.realtimeSinceStartup < deadline) yield return null;
            Check(Vector3.Distance(pausedRoot, bridge.avatar.transform.position) < .002f,
                "The actor continued translating while user support was unverified.");
            obstacle.SetActive(false); Physics.SyncTransforms();
            // A long pose-return may consume all short retries; moving the target safely renews them.
            if (bridge.PeekCurrentTargetEvidence().followSupportWaitingForMovedTarget)
                bridge.userHead.position = FindUser(bridge, 3f, bridge.userHead.position);
            deadline = Time.realtimeSinceStartup + 50;
            while (Vector3.Distance(pausedRoot, bridge.avatar.transform.position) < .12f && bridge.IsFollowing && Time.realtimeSinceStartup < deadline)
                yield return null;
            Check(bridge.IsFollowing && bridge.ActiveTaskId == "support-runtime" &&
                bridge.Controller.OperationRevision > oldOperation && Vector3.Distance(pausedRoot, bridge.avatar.transform.position) >= .12f,
                "Verified support did not resume the same intent through a fresh actual generated operation.");
            report.cases.Add("native-motion-stops-for-real-support-obstruction-and-resumes-after-fresh-validation");
            bridge.StopForTask("support-runtime-stop");
            Vector3 stopped = bridge.avatar.transform.position;
            deadline = Time.realtimeSinceStartup + 1;
            while (Time.realtimeSinceStartup < deadline) yield return null;
            Check(!bridge.IsFollowing && !bridge.PeekCurrentTargetEvidence().followSupportRecoveryPending &&
                Vector3.Distance(stopped, bridge.avatar.transform.position) < .002f,
                "Explicit stop after support recovery allowed stale motion to restart.");
            report.passed = true;
        }
        finally
        {
            if (obstacle != null) { obstacle.SetActive(false); UnityEngine.Object.Destroy(obstacle); }
            bridge.StopMovement();
            if (bridge.Controller.Player.HasPoseOwnership) bridge.Controller.Player.Stop();
            bridge.userHead.position = savedUser; bridge.approachDistance = savedDistance;
            bridge.avatar.transform.SetPositionAndRotation(savedRoot, savedRotation);
            bridge.InvalidateCurrentTargetAssessment(); Save(report, "room-follow-support-runtime-regression.json");
        }
    }

    private static GameObject Obstacle(Vector3 head)
    {
        var item = new GameObject("Unregistered support obstruction (no object-name exemption)");
        item.transform.position = head - Vector3.up * .7f;
        var box = item.AddComponent<BoxCollider>(); box.size = new Vector3(.4f, .1f, .4f);
        Physics.SyncTransforms(); return item;
    }
    private static void Start(ArdyRoomDialogueBridge bridge, string id)
    {
        var snapshot = bridge.ReadCurrentTargetEvidence(true);
        Check(bridge.FollowForTarget(id, snapshot.currentTargetRevision, out string failure) && bridge.IsFollowing,
            "Could not start the verified fixture follow: " + failure);
    }
    private static Vector3 FindUser(ArdyRoomDialogueBridge bridge, float radius, Vector3? avoid = null)
    {
        Transform user = bridge.userHead; Vector3 saved = user.position;
        Vector3 ground = bridge.avatar.transform.position + Vector3.up * bridge.importedReference.groundY * bridge.avatar.transform.lossyScale.y;
        try
        {
            for (int turn = 0; turn < 24; turn++)
            {
                user.position = ground + Quaternion.Euler(0, turn * 15, 0) * Vector3.forward * radius + Vector3.up * 1.6f;
                if (avoid.HasValue && !ArdyRoomFollowPolicy.TargetMoved(avoid.Value, user.position)) continue;
                if (ArdyRoomApproachPlanner.TryPlan(bridge.Navigation, ground, user, 1f, out _, out _)) return user.position;
            }
            throw new InvalidOperationException("No supported fixture target has a valid same-level route.");
        }
        finally { user.position = saved; }
    }
    private static void Tick(ArdyRoomDialogueBridge bridge, float now)
    {
        try { typeof(ArdyRoomDialogueBridge).GetMethod("TickFollow", Private).Invoke(bridge, new object[] { now }); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static void Check(bool ok, string message) { Checks++; if (!ok) throw new InvalidOperationException(message); }
    private static void Save(Report report, string file)
    {
        report.checks = Checks;
        string path = Path.GetFullPath("Tools/MotionAdapter/reports/" + file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, JsonUtility.ToJson(report, true) + "\n");
    }
}
