using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using NeEEvA.Motion;
using UnityEngine;

/// <summary>Storage/identity and actual-history admission checks only; no HTTP or generated test motion.</summary>
public static class ArdyRoomStreamDiagnosticsRegression
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static int checks;
    public static int RunServiceErrorChecks()
    {
        var describe = typeof(ArdyRoomLocomotionController).GetMethod("DescribeServiceFailure", BindingFlags.Static | BindingFlags.NonPublic);
        if (describe == null) throw new InvalidOperationException("Production service failure formatter missing.");
        string Format(string json) => (string)describe.Invoke(null, new object[] { json });
        string body = "{\"error\":{\"code\":\"deadline_exceeded\",\"details\":{\"streamDiagnostic\":{\"stage\":\"feature-wait\",\"elapsedMs\":5023,\"deadlineMs\":5000}}}}";
        string actual = Format(body);
        if (!actual.Contains("等待动作特征") || !actual.Contains("5.02") || actual.Contains("动作网络生成"))
            throw new InvalidOperationException("Feature waiting was lost or mislabeled as motion generation.");
        if (!Format(body.Replace("feature-wait", "generation")).Contains("动作网络生成"))
            throw new InvalidOperationException("Measured generation stage missing.");
        foreach (string invalid in new[] { null, "", "<html>gateway timeout</html>", "{}", new string('x', 32769),
            body.Replace("feature-wait", "UNTRUSTED_STAGE_TEXT"), body.Replace("5000", "-1"), body.Replace("5023", "-1") })
            if (Format(invalid) != "") throw new InvalidOperationException("Unverified error detail became a service-stage claim.");
        return 10;
    }
    [Serializable] private sealed class Wire
    {
        public string characterId = "diagnostic-character", sessionId = "diagnostic-session", requestId = "diagnostic-request", previousRequestId;
        public int revision = 12, startFrame = 8, stepIndex = 1, controlRevision = 2, commitFrames = 8;
        public ArdyLocomotionTarget[] targets = Enumerable.Range(0, 40).Select(_ => new ArdyLocomotionTarget()).ToArray();
        public string padding;
    }
    public static int RunChecks()
    {
        checks = 0;
        var host = new GameObject("ARDY diagnostics storage-only fixture");
        var controller = host.AddComponent<ArdyRoomLocomotionController>(); controller.enabled = false;
        try
        {
            Set(controller, "characterId", "diagnostic-character"); Set(controller, "streamSessionId", "diagnostic-session");
            Set(controller, "streamWireRevision", 12);
            typeof(ArdyRoomLocomotionController).GetProperty("OperationRevision").SetValue(controller, 7);
            StorageChecks(controller);
            HistoryChecks(controller);
            SlowIdleHistoryChecks(controller);
            PreparedHistoryChecks(controller);
            WarmRootSpeedChecks(controller);
            CommittedTurnSpeedChecks(controller);
            return checks;
        }
        finally
        {
            // This is identifier-only storage. Never send a cancellation for its fake session.
            Set(controller, "streamSessionId", null);
            UnityEngine.Object.DestroyImmediate(host);
        }
    }
    public static int RunArrivalChecks(ArdyRoomLocomotionController controller, ArdyLocomotionClip realClip)
    {
        checks = 0;
        Check(Application.isPlaying && controller != null && controller.Player != null && controller.Avatar != null &&
            !controller.IsBusy && !controller.IsContinuousFollowActive && !controller.Player.HasPoseOwnership &&
            Get(controller, "pending") == null && Get(controller, "work") == null &&
            string.IsNullOrEmpty(controller.StreamSessionId), "Arrival checks require the dormant, isolated real-VRM recorder fixture.");
        var player = controller.Player;
        var animator = controller.Avatar.GetComponent<Animator>();
        Check(animator != null && animator.enabled && !animator.applyRootMotion,
            "Arrival ownership regression requires the recorder fixture's enabled in-place Animator.");
        bool savedApplyRootMotion = animator.applyRootMotion;
        var controllerFields = MutableFields(controller); var playerFields = MutableFields(player);
        var transforms = controller.Avatar.GetComponentsInChildren<Transform>(true).ToList();
        foreach (var bone in (IEnumerable)Get(player, "bones"))
        { var transform = (Transform)Get(bone, "transform"); if (!transforms.Contains(transform)) transforms.Add(transform); }
        var localPositions = transforms.Select(t => t.localPosition).ToArray();
        var localRotations = transforms.Select(t => t.localRotation).ToArray();
        var localScales = transforms.Select(t => t.localScale).ToArray();
        try
        {
            // Replay the existing native first 8 frames. The last unconfirmed root alone
            // is shifted in this admission fixture; that altered tail is never played.
            var clip = ArdyLocomotionClip.Parse(JsonUtility.ToJson(realClip));
            clip.frames[clip.frames.Length - 1].rootPosition += Vector3.right * 3f;
            Vector3 ground = (Vector3)Call(controller, "AvatarGround");
            var route = new ArdyRoomRoute();
            typeof(ArdyRoomRoute).GetProperty("GroundY").SetValue(route, ground.y);
            Property(controller, "Route", route); Call(controller, "PrepareAnchor");
            Set(controller, "characterId", null); Set(controller, "requestId", null); // Never cancel a fictitious HTTP session.
            Set(controller, "streamBaseFrame", 0); Set(controller, "streamNextFrame", 8); Set(controller, "streamUserMoving", false);
            controller.ArrivalTolerance = .05f;
            player.Play(clip, null, 7); Set(controller, "motionScale", player.MotionScale);
            Set(controller, "ownedClip", clip); Set(controller, "ownedPlayerRevision", player.PlaybackRevision);
            Set(controller, "streamSessionId", "arrival-admission-fixture");
            Property(controller, "IsContinuousFollowActive", true); Property(controller, "IsBusy", true);
            Vector3 confirmed = (Vector3)Call(controller, "GeneratedRoot", clip, 7);
            Vector3 tail = (Vector3)Call(controller, "GeneratedRoot", clip, clip.frames.Length - 1);
            Property(controller, "TargetGround", tail);
            Check(!(bool)Call(controller, "StreamPrefixReachesGoal") && player.PlaybackEndSeconds == .35f,
                "An unconfirmed native tail was treated as arrival authority beyond the actual playback end.");
            Property(controller, "TargetGround", confirmed);
            Check((bool)Call(controller, "StreamPrefixReachesGoal"), "The last confirmed root at a stationary goal did not qualify for draining.");
            Set(controller, "streamUserMoving", true);
            Check(!(bool)Call(controller, "StreamPrefixReachesGoal"), "Moving-user updates did not withdraw stationary arrival authority.");
            Set(controller, "streamUserMoving", false); Property(controller, "TargetGround", confirmed + Vector3.right);
            Check(!(bool)Call(controller, "StreamPrefixReachesGoal"), "A new distant goal retained the old prefix's arrival authority.");
            Property(controller, "TargetGround", confirmed);
            Vector3 savedRoot = controller.Avatar.transform.position;
            controller.Avatar.transform.position = savedRoot + Vector3.right * 2;
            Vector3 farRoot = controller.Avatar.transform.position; long before = player.PlaybackRevision;
            Call(controller, "FinishStreamArrival");
            Check(player.IsPlaying && !player.IsStoppingSmoothly && player.PlaybackRevision == before &&
                controller.StreamSessionId == "arrival-admission-fixture" && controller.Avatar.transform.position == farRoot,
                "A future confirmed endpoint prematurely finished while the actual avatar was outside arrival tolerance.");
            controller.Avatar.transform.position = savedRoot;
            player.SampleAt(.35f); Vector3 reachedRoot = controller.Avatar.transform.position;
            Call(controller, "FinishStreamArrival");
            Check(player.IsStoppingSmoothly && player.IsReturningToAnimation && !player.IsPlaying &&
                string.IsNullOrEmpty(controller.StreamSessionId) && controller.Stage == "stream-waiting" &&
                controller.Avatar.transform.position == reachedRoot && player.LastAppliedSourceTimeSeconds <= .35f,
                "Actual confirmed arrival failed to use the real smooth handoff or consumed an unconfirmed tail/root snap.");

            player.RestorePreview(); player.Play(clip, null, 7); player.SampleAt(.35f);
            Set(controller, "ownedClip", clip); Set(controller, "ownedPlayerRevision", player.PlaybackRevision);
            Set(controller, "streamSessionId", "arrival-owner-change-fixture");
            Property(controller, "IsContinuousFollowActive", true); Property(controller, "IsBusy", true);
            Property(controller, "Stage", "stream-arriving"); Property(controller, "TargetGround", confirmed);
            Vector3 ownerFailureRoot = controller.Avatar.transform.position;
            Exception escaped = null;
            try
            {
                // This Animator was enabled when Play acquired it. Enabling root motion
                // while it is paused makes the real BeginAnimationReturn reject resumption.
                animator.applyRootMotion = true;
                Call(controller, "FinishStreamArrival");
            }
            catch (Exception error) { escaped = error is TargetInvocationException invocation ? invocation.InnerException : error; }
            finally { animator.applyRootMotion = savedApplyRootMotion; }
            Check(escaped == null,
                "Animator ownership loss escaped FinishStreamArrival into its LateUpdate caller: " + escaped);
            Check(controller.ExecutionState == ArdyRoomExecutionState.Failed && controller.ExecutionReason == ArdyRoomExecutionReason.OwnershipLost &&
                !controller.IsContinuousFollowActive && !player.HasPoseOwnership && string.IsNullOrEmpty(controller.StreamSessionId) &&
                controller.StreamTelemetry.lastFailureCategory == "ownership" && controller.Status.Contains("root motion") &&
                controller.Avatar.transform.position == ownerFailureRoot,
                "Arrival handoff lost the actual Animator root-motion failure reason or retained follow/body authority after ownership loss.");

            player.RestorePreview(); player.Play(clip, null, 7); player.SampleAt(.34f);
            Set(controller, "ownedClip", clip); Set(controller, "ownedPlayerRevision", player.PlaybackRevision);
            Set(controller, "streamSessionId", "shortage-admission-fixture"); Set(controller, "streamStepIndex", 1);
            Set(controller, "streamConsecutiveRecoveries", controller.StreamRecoveryLimit);
            Property(controller, "IsContinuousFollowActive", true); Property(controller, "IsBusy", true);
            Property(controller, "Stage", "stream-arriving");
            Property(controller, "TargetGround", confirmed + Vector3.right * 2);
            int underruns = controller.StreamUnderrunCount;
            Call(controller, "TickContinuousStream");
            Check(controller.StreamUnderrunCount == underruns + 1 && controller.ExecutionState == ArdyRoomExecutionState.Failed &&
                controller.ExecutionReason == ArdyRoomExecutionReason.GenerationFailed &&
                controller.StreamTelemetry.lastFailureCategory == "motion-supply" && !controller.IsContinuousFollowActive,
                "Exhausted supply for a changed/far goal was silently recast as arrival. This is an injected budget/state check, not a service outage test.");
            return checks;
        }
        finally
        {
            animator.applyRootMotion = savedApplyRootMotion;
            Set(controller, "streamSessionId", null); Set(controller, "characterId", null); Set(controller, "requestId", null);
            player.RestorePreview();
            foreach (var entry in playerFields) entry.Key.SetValue(player, entry.Value);
            foreach (var entry in controllerFields) entry.Key.SetValue(controller, entry.Value);
            for (int i = 0; i < transforms.Count; i++)
            { transforms[i].localPosition = localPositions[i]; transforms[i].localRotation = localRotations[i]; transforms[i].localScale = localScales[i]; }
        }
    }
    private static Dictionary<FieldInfo, object> MutableFields(object target) => target.GetType()
        .GetFields(Private | BindingFlags.Public).Where(field => !field.IsInitOnly).ToDictionary(field => field, field => field.GetValue(target));
    private static void Property(object target, string name, object value) => target.GetType().GetProperty(name).SetValue(target, value);
    private static void StorageChecks(ArdyRoomLocomotionController controller)
    {
        var request = new Wire(); string raw = JsonUtility.ToJson(request);
        var segment = new ArdyRoomLocomotionController.SegmentEvidence { operationRevision = 7, wireRevision = 12,
            startFrame = 8, auditedFrames = 8, requestId = request.requestId, requestJson = raw,
            responseJson = raw, completedAtRealtime = 1, outcome = "stream-accepted" };
        var candidate = new ArdyLocomotionClip { id = "storage-only-not-played", frames = Enumerable.Range(0, 40)
            .Select(i => new ArdyLocomotionFrame { rootPosition = new Vector3(i, 1, 0) }).ToArray() };
        var observed = new ArdyRoomRouteDiagnostic { stage = "execution-step", code = "body-overlap", hasSample = true,
            sample = new Vector3(4, 0, 2), colliderId = 123, colliderName = "fixture-observation", hasColliderBounds = true,
            colliderBoundsCenter = new Vector3(3, .5f, 2), colliderBoundsSize = Vector3.one };
        Reject(controller, candidate, 3, segment, observed);
        var item = controller.ReadRecentRejectedPrefixes().Single();
        Check(item.operationRevision == 7 && item.wireRevision == 12 && item.globalFrame == 11 && item.requestFrame == 3 &&
            item.requestId == request.requestId && item.sessionId == request.sessionId && item.requestSubmitted,
            "Rejected prefix lost its original operation, wire, request or raw response frame identity.");
        Check(item.requestJson == raw && item.responseJson == raw && item.rawUtf8Bytes == Encoding.UTF8.GetByteCount(raw) * 2 &&
            item.payloadIdentityMatched && item.navigation.colliderId == 123 && item.sourceRoot.x == 3,
            "Rejected request/response was not exact or its checked pose/collider was lost.");
        observed.colliderId = 456; item.navigation.colliderId = 789; item.requestJson = "viewer modification";
        Check(controller.ReadRecentRejectedPrefixes()[0].navigation.colliderId == 123 && controller.ReadRecentRejectedPrefixes()[0].requestJson == raw,
            "A later query/viewer mutation changed retained rejection evidence.");

        // StartPreparedStream audits the concatenated clip. Its local index is not the
        // raw response index; resolve the accepted segment that actually owns that frame.
        ((IList)Get(controller, "segmentEvidence")).Add(segment);
        var prebuffer = new ArdyLocomotionClip { id = "joined-buffer-not-played", frames = Enumerable.Range(0, 48)
            .Select(i => new ArdyLocomotionFrame { rootPosition = new Vector3(i, 1, 0) }).ToArray() };
        Reject(controller, prebuffer, 12, null, null);
        item = controller.ReadRecentRejectedPrefixes().Last();
        Check(item.globalFrame == 12 && item.candidateFrame == 12 && item.requestFrame == 4 && item.requestId == request.requestId && item.requestedFrames == 40,
            "Startup re-audit attached the last response instead of the segment owning that frame.");
        var wrong = new Wire { requestId = "unrelated-response" };
        segment.responseJson = JsonUtility.ToJson(wrong);
        Reject(controller, candidate, 3, segment, null);
        var retained = ((IList)Get(controller, "rejectedPrefixes")).Cast<ArdyRoomRejectedPrefixEvidence>().Last();
        Check(!retained.payloadIdentityMatched && retained.requestJson == null && retained.responseJson == null && retained.rawUtf8Bytes == 0,
            "Internal rejection storage retained a mismatched response: " + JsonUtility.ToJson(retained));
        item = controller.ReadRecentRejectedPrefixes().Last();
        // ReadRecentRejectedPrefixes deliberately makes a detached JsonUtility copy.
        // Unity serializes null string fields as empty strings; both mean no raw payload.
        Check(!item.payloadIdentityMatched && string.IsNullOrEmpty(item.requestJson) && string.IsNullOrEmpty(item.responseJson) &&
            item.rawUtf8Bytes == 0 && item.ordinal == retained.ordinal,
            "A detached rejection snapshot retained a stale response: " + JsonUtility.ToJson(item));
        segment.responseJson = null; segment.completedAtRealtime = 0;
        Reject(controller, null, -1, segment, null, "control-target");
        item = controller.ReadRecentRejectedPrefixes().Last();
        Check(!item.requestSubmitted && !item.responseAvailable && item.requestJson == raw && string.IsNullOrEmpty(item.responseJson) &&
            item.globalFrame == -1 && item.targetIndex == 5 && item.payloadOmission.Contains("not dispatched"),
            "Control preflight fabricated a native HTTP response or marked an undispatched request as sent.");

        request.padding = new string('中', 200000); raw = JsonUtility.ToJson(request);
        segment.requestJson = segment.responseJson = raw; segment.completedAtRealtime = 1;
        for (int i = 0; i < 40; i++) Reject(controller, candidate, 3, segment, null);
        var records = controller.ReadRecentRejectedPrefixes();
        Check(records.Length <= ArdyRoomLocomotionController.MaximumRejectedPrefixes &&
            records.Sum(r => r.rawUtf8Bytes) <= ArdyRoomLocomotionController.MaximumRejectedRawUtf8Bytes && records.Length > 0 &&
            records.Last().ordinal == controller.LatestRejectedPrefixOrdinal,
            "Repeated failed generations exceeded count/UTF-8 bounds or lost the newest rejection.");
        request.padding = new string('中', 400000); segment.requestJson = segment.responseJson = JsonUtility.ToJson(request);
        Reject(controller, candidate, 3, segment, null);
        item = controller.ReadRecentRejectedPrefixes().Last();
        Check(item.payloadIdentityMatched && item.rawUtf8Bytes == 0 && string.IsNullOrEmpty(item.requestJson) &&
            string.IsNullOrEmpty(item.responseJson) && item.payloadOmission.Contains("1 MiB"),
            "Oversized raw JSON was truncated into a misleading payload or kept above its bound.");
        var recorder = controller.gameObject.AddComponent<ArdyRoomPoseRecorder>(); recorder.RecordingEnabled = false;
        typeof(ArdyRoomPoseRecorder).GetField("controller", Private).SetValue(recorder, controller);
        Check(recorder.ReadCapture().recentRejectedPrefixes.Length == controller.ReadRecentRejectedPrefixes().Length,
            "The existing save-pose capture omitted rejected-prefix evidence.");
    }
    private static void HistoryChecks(ArdyRoomLocomotionController controller)
    {
        FillWarmHistory(controller);
        Check(TryHistory(controller) && controller.StreamHistoryObservation.success, "Continuous valid actual history was rejected by the diagnostic-only change.");
        foreach (var entry in new[] { "sample-count", "window-too-short", "latest-sample-stale", "sample-gap",
            "parent-changed", "scale-changed", "root-position-jump", "root-height-jump", "root-rotation-jump", "pelvis-height" })
        {
            FillWarmHistory(controller); var poses = (IList)Get(controller, "warmStreamPoses");
            if (entry == "sample-count") poses.Clear();
            if (entry == "window-too-short") while (poses.Count > 4) poses.RemoveAt(0);
            if (entry == "latest-sample-stale") foreach (var pose in poses) Set(pose, "time", (float)Get(pose, "time") - 1f);
            if (entry == "sample-gap") for (int i = 0; i < 4; i++) poses.RemoveAt(5);
            if (entry == "parent-changed") Set(poses[8], "parent", controller.transform);
            if (entry == "scale-changed") Set(poses[8], "scale", 1.5f);
            if (entry == "root-position-jump") Set(poses[8], "root", Vector3.right);
            if (entry == "root-height-jump") Set(poses[8], "root", Vector3.up * .04f);
            if (entry == "root-rotation-jump") Set(poses[8], "rotation", Quaternion.Euler(0, 90, 0));
            if (entry == "pelvis-height") foreach (var pose in poses) Set(pose, "hips", Vector3.up * .1f);
            Check(!TryHistory(controller) && controller.StreamHistoryObservation.code == entry,
                "Local history rejection did not expose its true failed check: " + entry);
            var error = (Exception)Call(controller, "CreatePoseHistoryException");
            var category = (string)typeof(ArdyRoomLocomotionController).GetMethod("StreamFailureCategory", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { error });
            Check(category == "pose-history" && error.Message.Contains(entry) && controller.LastRejectedStreamHistory.code == entry,
                "A local actual-history failure was mislabeled as a backend/service request: " + entry);
        }
        var lastFailure = controller.LastRejectedStreamHistory;
        FillWarmHistory(controller); Check(TryHistory(controller) && controller.StreamHistoryObservation.success &&
            controller.LastRejectedStreamHistory.code == lastFailure.code,
            "Ready history did not clear its live observation or erased a historical failure that must remain separately labeled.");
    }
    private static void FillWarmHistory(ArdyRoomLocomotionController controller)
    {
        Set(controller, "anchorRotation", Quaternion.identity); Set(controller, "anchorGround", Vector3.zero);
        Set(controller, "anchorParent", null); Set(controller, "motionScale", 1f); Set(controller, "worldScale", 1f);
        Set(controller, "warmHistoryCaptureError", null);
        var poses = (IList)Get(controller, "warmStreamPoses"); poses.Clear();
        var type = typeof(ArdyRoomLocomotionController).GetNestedType("WarmPose", BindingFlags.NonPublic);
        float last = Time.realtimeSinceStartup;
        for (int i = 0; i < 17; i++)
        {
            var pose = Activator.CreateInstance(type);
            Set(pose, "time", last - .8f + i * .05f); Set(pose, "hips", Vector3.up);
            Set(pose, "root", Vector3.zero); Set(pose, "rotation", Quaternion.identity); Set(pose, "scale", 1f);
            Set(pose, "worldRotations", Enumerable.Repeat(Quaternion.identity, 27).ToArray()); poses.Add(pose);
        }
    }
    private static void FillSlowIdleHistory(ArdyRoomLocomotionController controller)
    {
        FillWarmHistory(controller);
        var poses = (IList)Get(controller, "warmStreamPoses");
        while (poses.Count > 5) poses.RemoveAt(0);
        float last = Time.realtimeSinceStartup;
        for (int i = 0; i < poses.Count; i++)
        {
            Set(poses[i], "time", last - (poses.Count - 1 - i) * .27f);
            Set(poses[i], "unityFrame", 100 + i);
            Set(poses[i], "hips", Vector3.up + Vector3.right * (i * .001f));
        }
    }
    private static void SlowIdleHistoryChecks(ArdyRoomLocomotionController controller)
    {
        FillSlowIdleHistory(controller);
        Check(TryHistory(controller, out var history) && history.Length == 16,
            "Consecutive actual 270 ms idle frames could not seed the regular 20 Hz wire history.");
        foreach (string scenario in new[] { "skipped-frame", "long-stall", "root-motion", "turning", "pelvis-motion", "joint-motion", "stale", "parent", "scale" })
        {
            FillSlowIdleHistory(controller);
            var poses = (IList)Get(controller, "warmStreamPoses");
            if (scenario == "skipped-frame") Set(poses[2], "unityFrame", 999);
            if (scenario == "long-stall") Set(poses[2], "time", (float)Get(poses[2], "time") - .1f);
            if (scenario == "root-motion") Set(poses[2], "root", Vector3.right * .01f);
            if (scenario == "turning") Set(poses[2], "rotation", Quaternion.Euler(0, 1, 0));
            if (scenario == "pelvis-motion") Set(poses[2], "hips", Vector3.up + Vector3.right * .05f);
            if (scenario == "joint-motion") ((Quaternion[])Get(poses[2], "worldRotations"))[6] = Quaternion.Euler(25, 0, 0);
            if (scenario == "stale") foreach (var pose in poses) Set(pose, "time", (float)Get(pose, "time") - .2f);
            if (scenario == "parent") Set(poses[2], "parent", controller.transform);
            if (scenario == "scale") Set(poses[2], "scale", 1.1f);
            string expected = scenario == "stale" ? "latest-sample-stale" : scenario == "parent" ? "parent-changed" :
                scenario == "scale" ? "scale-changed" : "sample-gap";
            Check(!TryHistory(controller) && controller.StreamHistoryObservation.code == expected,
                "Slow-render idle exception admitted unsafe or missing actual pose evidence: " + scenario);
        }
    }
    private static void PreparedHistoryChecks(ArdyRoomLocomotionController controller)
    {
        FillWarmHistory(controller); Check(TryHistory(controller, out var accepted), "Fresh baseline history missing.");
        void Seed()
        {
            Set(controller, "preparedWarmStreamHistory", accepted);
            Set(controller, "preparedWarmStreamOperation", 17);
            Set(controller, "preparedWarmStreamFrame", Time.frameCount);
            Set(controller, "preparedWarmStreamAt", Time.realtimeSinceStartup);
        }
        bool Take(int operation)
        {
            var args = new object[] { operation, null };
            return (bool)typeof(ArdyRoomLocomotionController).GetMethod("TryTakePreparedWarmStreamHistory", Private).Invoke(controller, args);
        }
        Seed(); Check(Take(17) && !Take(17), "Fresh rendered snapshot was not consumed exactly once.");
        Seed(); Set(controller, "preparedWarmStreamFrame", Time.frameCount - 1);
        Set(controller, "preparedWarmStreamAt", Time.realtimeSinceStartup - .27f);
        Check(Take(17), "Next coroutine frame rejected the immediately preceding measured 270 ms idle snapshot.");
        Seed(); Check(!Take(18), "Another operation consumed the prepared pose history.");
        Seed(); Set(controller, "preparedWarmStreamFrame", Time.frameCount - 2);
        Check(!Take(17), "Prepared history survived a skipped rendered frame.");
        Seed(); Set(controller, "preparedWarmStreamAt", Time.realtimeSinceStartup - .5f);
        Check(!Take(17), "A long main-thread stall admitted an old prepared pose snapshot.");
        Seed();
        // Storage-only identities are not actual native sessions; never issue HTTP for them.
        Set(controller, "streamSessionId", null);
        Call(controller, "CancelContinuousStream");
        Check(!Take(17), "Cancellation retained prepared pose history.");
    }
    private static void CommittedTurnSpeedChecks(ArdyRoomLocomotionController controller)
    {
        // A nonzero imported pelvis offset moves in an arc during a root-in-place turn.
        // Exercise the same per-session state whose instantaneous 0.4s plan velocity
        // production adopts after accepting a prefix, then reuse it for the next build.
        Vector3 offset = new Vector3(.12f, 0, .07f);
        Set(controller, "anchorRotation", Quaternion.identity); Set(controller, "anchorGround", Vector3.zero);
        Set(controller, "neutralHorizontal", offset); Set(controller, "motionScale", 1f);
        var state = (ArdyRoomStreamTrajectory.PathState)Get(controller, "streamControlPath");
        state.Clear();
        var route = new ArdyRoomRoute();
        typeof(ArdyRoomRoute).GetProperty("Corners").SetValue(route, new[] { Vector3.zero, Vector3.right * 2 });
        typeof(ArdyRoomRoute).GetProperty("Length").SetValue(route, 2f);
        var first = ArdyRoomStreamTrajectory.Build(route, offset, 0, 0, .5f, Quaternion.identity, Vector3.zero, 1f,
            rootOffset: offset, continuity: state);
        float speed = state.CommittedSpeed;
        float pelvisSpeed = Vector3.Distance(first[7].rootPosition, first[6].rootPosition) * 20;
        Check(state.HasPlan && pelvisSpeed > .05f && speed < .00001f && first[7].headingDegrees > first[6].headingDegrees,
            "Accepting an in-place turn with a pelvis offset fabricated forward root velocity.");
        var second = ArdyRoomStreamTrajectory.Build(route, first[7].rootPosition, first[7].headingDegrees, speed, .5f,
            Quaternion.identity, Vector3.zero, 1f, rootOffset: offset, continuity: state);
        Check(state.HasPlan && second.Length == 40 && second[0].headingDegrees > first[7].headingDegrees &&
            state.CommittedSpeed < .00001f,
            "The next real control window could not continue its stationary turn using accepted root velocity.");
        bool oldStateRejected = false;
        try { ArdyRoomStreamTrajectory.Build(route, first[7].rootPosition, first[7].headingDegrees, pelvisSpeed, .5f,
            Quaternion.identity, Vector3.zero, 1f, rootOffset: offset); }
        catch (InvalidOperationException error) { oldStateRejected = error.Message.Contains("initial-turn"); }
        Check(oldStateRejected, "The regression did not exercise the former false-moving-state turning rejection.");
    }
    private static void WarmRootSpeedChecks(ArdyRoomLocomotionController controller)
    {
        FillWarmHistory(controller);
        Set(controller, "warmStreamRootSpeed", .75f);
        var poses = (IList)Get(controller, "warmStreamPoses");
        for (int i = 0; i < poses.Count; i++) Set(poses[i], "hips", Vector3.up + Vector3.right * (i * .004f));
        Check(TryHistory(controller, out var history) && history != null && history.Length == 16,
            "Stationary avatar-root history with normal pelvis sway was not admitted.");
        float pelvisSpeed = (history[15].rootPosition - history[12].rootPosition).magnitude / .15f;
        Check(pelvisSpeed > .05f && Mathf.Abs((float)Get(controller, "warmStreamRootSpeed")) < .00001f,
            "Native history erased pelvis sway, or startup controls misinterpreted it as avatar-root travel.");

        FillWarmHistory(controller); Set(controller, "motionScale", 2f);
        poses = (IList)Get(controller, "warmStreamPoses");
        for (int i = 0; i < poses.Count; i++)
        {
            // Known .4 world metres/second, with a harmless vertical change and extra
            // pelvis sway. Neither source scaling nor body sway may rescale root speed.
            Vector3 root = new Vector3(.4f * i * .05f, i * .0005f, 0);
            Set(poses[i], "root", root);
            Set(poses[i], "hips", root + Vector3.up + Vector3.right * (i * .003f));
        }
        Check(TryHistory(controller, out history) && history != null &&
            Mathf.Abs((float)Get(controller, "warmStreamRootSpeed") - .4f) < .0001f &&
            history[15].rootPosition.x - history[12].rootPosition.x > .033f,
            "Actual startup root velocity was lost or contaminated by pelvis sway, vertical change or source scale.");
        float acceptedSpeed = (float)Get(controller, "warmStreamRootSpeed");
        object requestBefore = Get(controller, "requestId"), pendingBefore = Get(controller, "pending");
        int acceptedSteps = controller.StreamStepCount;
        for (int i = 0; i < 4; i++) poses.RemoveAt(5);
        Check(!TryHistory(controller, out history) && history == null && controller.StreamHistoryObservation.code == "sample-gap" &&
            (float)Get(controller, "warmStreamRootSpeed") == acceptedSpeed &&
            Equals(Get(controller, "requestId"), requestBefore) && ReferenceEquals(Get(controller, "pending"), pendingBefore) &&
            controller.StreamStepCount == acceptedSteps,
            "Rejected startup history overwrote accepted root velocity, exposed generation history or changed request state.");
    }
    private static bool TryHistory(ArdyRoomLocomotionController controller)
        => TryHistory(controller, out _);
    private static bool TryHistory(ArdyRoomLocomotionController controller, out ArdyRoomHistoryFrame[] history)
    {
        var args = new object[] { null };
        bool ready = (bool)typeof(ArdyRoomLocomotionController).GetMethod("TryWarmStreamHistory", Private).Invoke(controller, args);
        history = (ArdyRoomHistoryFrame[])args[0]; return ready;
    }
    private static void Reject(ArdyRoomLocomotionController controller, ArdyLocomotionClip candidate, int frame,
        ArdyRoomLocomotionController.SegmentEvidence segment, ArdyRoomRouteDiagnostic diagnostic, string phase = "committed-prefix")
        => Call(controller, "RejectGeneratedPrefix", candidate, frame, Vector3.zero, Vector3.right,
            "fixture: generated prefix rejected", diagnostic, "navigation", segment, null, phase, phase == "control-target" ? 5 : -1);
    private static object Call(object target, string name, params object[] arguments) => target.GetType().GetMethod(name, Private).Invoke(target, arguments);
    private static object Get(object target, string name) => target.GetType().GetField(name, Private | BindingFlags.Public).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private | BindingFlags.Public).SetValue(target, value);
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
}
