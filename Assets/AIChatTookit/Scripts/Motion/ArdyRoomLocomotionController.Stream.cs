using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace NeEEvA.Motion
{
    public sealed partial class ArdyRoomLocomotionController
    {
        [Serializable] private class StreamRequest
        {
            public int schema = 1;
            public string characterId, sessionId, requestId, previousRequestId;
            public int revision, stepIndex, startFrame, commitFrames = 8, controlRevision, timeoutMs = 5000, seed;
            public ArdyLocomotionTarget[] targets;
        }
        [Serializable] private sealed class InitialStreamRequest : StreamRequest
        {
            // Keep the evaluated locomotion caption; numeric future controls carry speed.
            public string description = "A person walks naturally along the indicated path and comes to a relaxed stop.";
            public History initialHistory;
        }
        [Serializable] private sealed class StreamResponse
        {
            public string characterId, sessionId, requestId;
            public int revision, stepIndex, startFrame, commitFrames, controlRevision;
            public StreamTimings timings;
        }
        [Serializable] private sealed class StreamCancellation
        {
            public string characterId, sessionId;
            public int revision;
        }
        private sealed class WarmPose
        {
            public float time;
            public Vector3 hips, root;
            public Quaternion rotation;
            public Transform parent;
            public float scale;
            public Quaternion[] worldRotations;
        }
        private sealed class StreamControlSample
        {
            public int frame, revision;
            public float receivedAt;
        }

        public bool IsContinuousFollowActive { get; private set; }
        public float StreamDesiredSpeed { get; private set; }
        public float StreamMeasuredSpeed { get; private set; }
        public int StreamStepCount { get; private set; }
        public int StreamUnderrunCount { get; private set; }
        public float StreamGenerationSeconds { get; private set; }
        public float StreamBufferSeconds => streamSessionId == null ? 0 : streamPreparedClip != null ? streamNextFrame / 20f :
            ownedClip == null || player == null ? 0 : Mathf.Max(0, (streamNextFrame - streamBaseFrame) / 20f - player.TimeSeconds);
        public int StreamRecoveryCount { get; private set; }
        public int StreamRecoveryLimit => 3;
        public bool StreamIsRecovering => streamRecovering;
        public int StreamControlRevision { get; private set; }
        public int StreamAppliedControlRevision { get; private set; }
        public int StreamAcceptedControlRevision { get; private set; }
        public float StreamControlLatencySeconds { get; private set; }
        public string StreamSessionId => streamSessionId;
        public int StreamTimelineBaseFrame => streamBaseFrame;
        public int StreamWireRevision => streamWireRevision;
        private readonly List<WarmPose> warmStreamPoses = new List<WarmPose>();
        private float warmStreamRootSpeed;
        private readonly Queue<StreamControlSample> streamControls = new Queue<StreamControlSample>();
        private string streamSessionId, streamPreviousRequestId;
        private int streamWireRevision, streamStepIndex, streamNextFrame, streamBaseFrame;
        private bool streamUserMoving;
        private Vector3 streamUserVelocity, streamUserGround, streamMeasuredRoot;
        private float streamMeasuredTime, streamDispatchTime;
        private float streamControlReceivedAt;
        private float streamCommittedSpeed;
        private readonly ArdyRoomStreamTrajectory.PathState streamControlPath = new ArdyRoomStreamTrajectory.PathState();
        private Capabilities streamCapabilities;
        private ArdyLocomotionClip streamPreparedClip;
        private bool streamRecovering;
        private int streamConsecutiveRecoveries;
        private float streamRecoverAfter, streamHealthyPlaySince;

        public void BeginContinuousFollow(ArdyRoomRoute route, Vector3 userGround, Vector3 facePoint,
            Vector3 userVelocity, float desiredSpeed, bool userMoving)
        {
            if (!Application.isPlaying || avatar == null || navigation == null || !isActiveAndEnabled)
                throw new InvalidOperationException("Bind a live room avatar before continuous following.");
            RejectForeignPlayerOwner();
            if (player.IsStoppingSmoothly) throw new InvalidOperationException("等待前一动作完成平滑交接。");
            ValidateStreamControl(route, userGround, facePoint, userVelocity, desiredSpeed);
            Cancel();
            if (player.IsStoppingSmoothly) throw new InvalidOperationException("已停止旧路线，等待身体平滑交接。");
            int token = ++revision; OperationRevision = token;
            IsContinuousFollowActive = true; IsBusy = true;
            StreamStepCount = StreamUnderrunCount = StreamControlRevision = StreamAppliedControlRevision = 0;
            StreamAcceptedControlRevision = 0; StreamControlLatencySeconds = 0;
            StreamGenerationSeconds = StreamMeasuredSpeed = 0;
            StreamRecoveryCount = streamConsecutiveRecoveries = 0; streamRecovering = false;
            StreamQualityRecoveryCount = 0; LastQualityFailureEvidencePath = null;
            StreamControlRecoveryCount = 0; LastControlFailureEvidencePath = null;
            controlFailureRequest = null; controlFailureOperation = -1;
            streamPreparedClip = null; streamCapabilities = null; streamRequestPending = false;
            streamRecentFramePeak = streamRecentStepCost = 0;
            streamRoundTrip = streamBackend = streamBackendQueue = streamFeature = streamPlanning = streamParse = streamAudit = 0;
            streamEventName = "begin"; streamLastFailureReason = streamLastFailureCategory = null;
            streamFailureOperationRevision = -1;
            streamFailureRequestId = streamFailureSessionId = streamFailureObservedUtc = null;
            streamLastHandoff = null; streamFailureStage = null;
            segmentPlan = null; ownedClip = null; nextSeamFrame = -1; timelineFinal = false;
            segmentEvidence.Clear(); DiscardedSegmentEvidenceCount = 0;
            LastErrorResponseJson = LastRequestJson = LastResponseJson = null;
            streamMeasuredRoot = avatar.transform.position; streamMeasuredTime = Time.realtimeSinceStartup;
            ApplyStreamControl(route, userGround, facePoint, userVelocity, desiredSpeed, userMoving);
            Stage = "stream-preparing";
            SetStatus("正在准备持续变速跟随", ArdyRoomExecutionState.Preparing);
            work = StartCoroutine(GuardedStreamRun(RunContinuousStream(token), token));
        }

        public bool TryUpdateContinuousFollow(ArdyRoomRoute route, Vector3 userGround, Vector3 facePoint,
            Vector3 userVelocity, float desiredSpeed, bool userMoving, out string reason)
        {
            reason = null;
            if (!IsContinuousFollowActive || !Current(OperationRevision)) { reason = "没有持续跟随会话"; return false; }
            try { ValidateStreamControl(route, userGround, facePoint, userVelocity, desiredSpeed); }
            catch (Exception error) { reason = error.Message; return false; }
            ApplyStreamControl(route, userGround, facePoint, userVelocity, desiredSpeed, userMoving);
            return true;
        }

        private static void ValidateStreamControl(ArdyRoomRoute route, Vector3 user, Vector3 face, Vector3 velocity, float speed)
        {
            if (route?.Corners == null || route.Corners.Length == 0 || !ArdyLocomotionClip.Finite(route.Length) ||
                route.Length < 0 || !ArdyLocomotionClip.Finite(user) || !ArdyLocomotionClip.Finite(face) ||
                !ArdyLocomotionClip.Finite(velocity) || !ArdyLocomotionClip.Finite(speed) || speed < 0 || speed > 1.2f)
                throw new ArgumentException("持续跟随需要有效的同层路线与 0–1.2 米/秒速度。");
            foreach (var corner in route.Corners)
                if (!ArdyLocomotionClip.Finite(corner)) throw new ArgumentException("无效路径坐标。");
        }

        private void ApplyStreamControl(ArdyRoomRoute route, Vector3 user, Vector3 face, Vector3 velocity, float speed, bool moving)
        {
            Route = route; TargetGround = route.Corners[route.Corners.Length - 1];
            WorldFacePoint = face; streamUserGround = user; streamUserVelocity = velocity;
            streamUserMoving = moving; StreamDesiredSpeed = speed; StreamControlRevision++;
            streamControlReceivedAt = Time.realtimeSinceStartup;
        }

        private IEnumerator RunContinuousStream(int token)
        {
            if (streamCapabilities == null)
            {
                pending = UnityWebRequest.Get(Url("/v1/locomotion/capabilities")); pending.timeout = 10;
                yield return pending.SendWebRequest();
                if (!Current(token)) yield break;
                streamCapabilities = JsonUtility.FromJson<Capabilities>(ReadAndDisposeResponse());
                ValidateCapabilities(streamCapabilities);
            }
            if (streamCapabilities.streamProtocol != "ardy-room-stream-v1" ||
                (streamCapabilities.streamGenerationFrames != 40 && streamCapabilities.streamGenerationFrames != 8) ||
                streamCapabilities.streamCommitFrames != 8)
                throw new InvalidOperationException("ARDY 服务尚未加载持续跟随接口，请重启本地 ARDY 服务。");
            sourceRootHeight = streamCapabilities.sourceRootHeight;
            while (Current(token) && IsContinuousFollowActive)
            {
                CheckContinuousStreamPlayerOwnership();
                if (ownedClip != null && player.IsReturningToAnimation) { yield return null; continue; }
                if (ownedClip != null && player.ReturnedToAnimation && !player.HasPoseOwnership) ownedClip = null;
                if (ownedClip == null) CheckContinuousStreamPlayerOwnership();
                if (streamRecovering && Time.realtimeSinceStartup < streamRecoverAfter) { yield return null; continue; }
                if (streamSessionId == null)
                {
                    if (!streamUserMoving && (StreamDesiredSpeed < .04f ||
                        HorizontalDistance(AvatarGround(), TargetGround) <= ArrivalTolerance))
                    { streamRecovering = false; IsBusy = false; Stage = "stream-waiting"; yield return null; continue; }
                    IsBusy = true;
                    if (!navigation.TryPlan(AvatarGround(), TargetGround, out var initialRoute, out string initialReason))
                        throw new ExecutionException(initialReason, ArdyRoomExecutionReason.NavigationRejected);
                    Route = initialRoute; PrepareAnchor();
                    motionScale = (reference.Get(HumanBodyBones.Hips).positionInRoot.y - reference.groundY) * worldScale / sourceRootHeight;
                    ArdyRoomHistoryFrame[] history = null;
                    Stage = "stream-history";
                    float historyDeadline = Time.realtimeSinceStartup + 3f;
                    while (Current(token) && !TryWarmStreamHistory(out history))
                    {
                        if (Time.realtimeSinceStartup > historyDeadline)
                            throw CreatePoseHistoryException();
                        yield return null;
                        if (!Current(token) || !IsContinuousFollowActive) yield break;
                        CheckContinuousStreamPlayerOwnership(); PrepareAnchor();
                    }
                    if (!Current(token)) yield break;
                    streamSessionId = Guid.NewGuid().ToString("N"); streamWireRevision = ++wireRevision;
                    streamControlPath.Clear();
                    streamStepIndex = streamNextFrame = streamBaseFrame = 0; streamPreviousRequestId = null;
                    streamPreparedClip = null;
                    foreach (var item in GenerateStreamStep(token, history)) yield return item;
                    continue;
                }
                if (streamPreparedClip != null)
                {
                    CheckWaitingAnchor();
                    if (!StreamPrefixReachesGoal() && streamNextFrame / 20f + .001f < StreamReadyTargetSeconds)
                    {
                        foreach (var item in GenerateStreamStep(token, null)) yield return item;
                        continue;
                    }
                    StartPreparedStream();
                    if (!Current(token)) yield break;
                    continue;
                }
                // Only the accepted short prefix is committed. Dispatch just before that
                // prefix runs out; the remaining prediction has not been acknowledged.
                string waitingSession = streamSessionId;
                int waitingWire = streamWireRevision;
                while (Current(token) && IsContinuousFollowActive && streamSessionId == waitingSession &&
                    streamWireRevision == waitingWire && StreamBufferSeconds > StreamLeadSeconds) yield return null;
                if (!Current(token) || !IsContinuousFollowActive) yield break;
                // LateUpdate may have completed arrival while this coroutine was
                // suspended. Re-enter the waiting/resume lifecycle, never append
                // to its deliberately closed timeline. Foreign owners still fail.
                CheckContinuousStreamPlayerOwnership();
                if (streamSessionId == null || streamSessionId != waitingSession || streamWireRevision != waitingWire) continue;
                if (StreamRenderedArrival()) { FinishStreamArrival(); continue; }
                if (StreamPrefixReachesGoal())
                {
                    // The accepted future prefix already reaches a stationary target.
                    // Consume it instead of replanning a centimetre-sized final turn while
                    // the rendered avatar is still catching up with that prefix.
                    Stage = "stream-arriving";
                    int waitingControl = StreamControlRevision;
                    RecordStreamArrivalBoundary("wait-enter", waitingControl);
                    while (Current(token) && IsContinuousFollowActive && streamSessionId == waitingSession &&
                        streamWireRevision == waitingWire && StreamPrefixReachesGoal() && !StreamRenderedArrival())
                        yield return null;
                    if (!Current(token) || !IsContinuousFollowActive) yield break;
                    CheckContinuousStreamPlayerOwnership();
                    if (streamSessionId == null || streamSessionId != waitingSession || streamWireRevision != waitingWire) continue;
                    RecordStreamArrivalBoundary(StreamRenderedArrival() ? "rendered-arrival" : "target-changed", waitingControl);
                    if (streamSessionId != null && StreamRenderedArrival()) FinishStreamArrival();
                    else if (streamSessionId != null && ArrivalRetargetBudgetInsufficient(StreamBufferSeconds, streamRecentStepCost, streamRecentFramePeak))
                    {
                        RecordStreamArrivalBoundary("retarget-insufficient-dispatch-budget", waitingControl);
                        if (!PrepareStreamRecovery("收尾时目标改变，剩余已审核片段不足以等待新动作，先平滑停稳再有限恢复。",
                            "motion-supply", "arrival-retarget", false)) yield break;
                    }
                    continue;
                }
                if (!streamUserMoving && StreamDesiredSpeed < .04f && StreamMeasuredSpeed < .10f)
                {
                    RecordStreamHandoff("stationary-wait");
                    CloseStreamWire();
                    if (ownedClip != null && player.RequestSmoothStop(ReturnToIdleSeconds)) ownedPlayerRevision = player.PlaybackRevision;
                    Stage = "stream-waiting"; IsBusy = false;
                    SetStatus("已停稳，持续等待用户移动", ArdyRoomExecutionState.Moving);
                    yield return null; continue;
                }
                foreach (var item in GenerateStreamStep(token, null)) yield return item;
            }
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        { a.y = b.y = 0; return Vector3.Distance(a, b); }

        private bool StreamPrefixReachesGoal()
        {
            if (streamUserMoving) return false;
            var clip = streamPreparedClip ?? ownedClip;
            int last = streamNextFrame - streamBaseFrame - 1;
            return clip != null && last >= 0 && last < clip.frames.Length &&
                HorizontalDistance(GeneratedRoot(clip, last), TargetGround) <= Mathf.Min(.05f, ArrivalTolerance);
        }

        private void FinishStreamArrival()
        {
            if (!StreamRenderedArrival()) return;
            RecordStreamHandoff("rendered-arrival");
            bool restartWait = streamRequestPending;
            if (restartWait)
            {
                EndStreamWireReceipt("rendered-arrival", true);
                if (work != null) { StopCoroutine(work); work = null; }
                if (pending != null) { pending.Abort(); pending.Dispose(); pending = null; }
                streamRequestPending = false;
                foreach (var evidence in segmentEvidence)
                    if (evidence.outcome == "generating") {
                        evidence.outcome = "stream-arrival-discarded";
                        evidence.completedAtRealtime = Time.realtimeSinceStartup;
                    }
            }
            // No unconfirmed frame is consumed and no root is projected to the goal.
            // The normal smooth stop preserves the currently rendered pose and position.
            try
            {
                CloseStreamWire();
                if (ownedClip != null && player.RequestSmoothStop(ReturnToIdleSeconds)) ownedPlayerRevision = player.PlaybackRevision;
            }
            catch (Exception error)
            {
                // This method also runs from LateUpdate, outside the coroutine fence.
                string reason = "抵达时身体交接失败 · " + error.Message;
                RecordStreamEventWithEvidence("failed", reason, "ownership", streamLastHandoff);
                Fail(reason, ArdyRoomExecutionReason.OwnershipLost);
                return;
            }
            Stage = "stream-waiting"; IsBusy = false;
            SetStatus("已接近目标，平滑停下并持续等待用户移动", ArdyRoomExecutionState.Moving);
            RecordStreamEvent("arriving");
            if (restartWait) work = StartCoroutine(GuardedStreamRun(RunContinuousStream(OperationRevision), OperationRevision));
        }

        private bool StreamRenderedArrival() => IsContinuousFollowActive && !streamUserMoving &&
            ownedClip != null && OwnsPlayingTimeline() &&
            HorizontalDistance(AvatarGround(), TargetGround) <= ArrivalTolerance;

        private static bool ArrivalRetargetBudgetInsufficient(float buffer, float recentRequest, float framePeak)
            => buffer < Mathf.Max(.15f, recentRequest) + framePeak * 2f + .05f;

        private void RecordStreamArrivalBoundary(string reason, int enteredControl)
        {
            Debug.Log("[ARDY room/arrival-boundary] " + JsonUtility.ToJson(new StreamArrivalBoundary {
                reason = reason, operationRevision = OperationRevision, enteredControlRevision = enteredControl,
                currentControlRevision = StreamControlRevision, bufferSeconds = StreamBufferSeconds,
                dispatchLeadSeconds = StreamLeadSeconds, targetDistance = HorizontalDistance(AvatarGround(), TargetGround),
                estimatedRequestSeconds = streamRecentStepCost, recentFramePeakSeconds = streamRecentFramePeak,
                recoveryCount = StreamRecoveryCount, recoveryLimit = StreamRecoveryLimit,
                arrivalTolerance = ArrivalTolerance, userMoving = streamUserMoving,
                prefixReachesGoal = StreamPrefixReachesGoal(), pending = streamRequestPending,
                observedUtc = DateTime.UtcNow.ToString("o") }), this);
        }
        [Serializable] private sealed class StreamArrivalBoundary
        {
            public string reason, observedUtc;
            public int operationRevision, enteredControlRevision, currentControlRevision;
            public int recoveryCount, recoveryLimit;
            public float estimatedRequestSeconds, recentFramePeakSeconds;
            public float bufferSeconds, dispatchLeadSeconds, targetDistance, arrivalTolerance;
            public bool userMoving, prefixReachesGoal, pending;
        }

        // IEnumerable permits the outer guarded coroutine to observe all exceptions; a nested
        // IEnumerator handed to Unity would bypass that cancellation/error fence.
        private IEnumerable<object> GenerateStreamStep(int token, ArdyRoomHistoryFrame[] initialHistory)
        {
            if (!Current(token) || !IsContinuousFollowActive || streamSessionId == null) yield break;
            string generationSession = streamSessionId;
            int generationWire = streamWireRevision;
            CheckContinuousStreamPlayerOwnership();
            float planningStarted = Time.realtimeSinceStartup;
            int start = streamNextFrame, localStart = start - streamBaseFrame;
            var timeline = streamPreparedClip ?? ownedClip;
            Vector3 sourcePosition; float heading, speed;
            if (initialHistory != null)
            {
                var last = initialHistory[15]; sourcePosition = last.rootPosition;
                var forward = last.globalRotations[0] * Vector3.forward;
                heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                // Idle pelvis sway belongs in the pose history, not in navigation velocity.
                // This speed comes from the same validated history's actual avatar root.
                speed = warmStreamRootSpeed;
            }
            else
            {
                if ((streamPreparedClip == null && !OwnsPlayingTimeline()) || timeline == null || localStart < 4 || localStart > timeline.frames.Length)
                    throw new StreamLifecycleException("持续跟随丢失已采用的姿态前缀。", CaptureStreamHandoff("invalid-adopted-prefix"));
                sourcePosition = timeline.frames[localStart - 1].rootPosition;
                var forward = timeline.rotationClip.SampleDelta(0, (localStart - 1) / 20f) * Vector3.forward;
                heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                // Preserve the velocity controller's accepted trajectory state. The 150ms
                // root derivative oscillates with footfall phase and is not a fresh speed command.
                speed = streamCommittedSpeed;
            }
            Vector3 seamGround = anchorGround + anchorRotation * new Vector3(sourcePosition.x * motionScale, 0, sourcePosition.z * motionScale);
            // Navigation admits the avatar root/body centre, while ARDY controls its pelvis.
            // Use the same yaw-dependent neutral offset as GeneratedRoot and playback.
            seamGround -= anchorRotation * Quaternion.Euler(0, heading, 0) * neutralHorizontal;
            seamGround.y = floorY;
            // Replan from the committed future pose. NavMesh remains responsible for the
            // corridor; prediction is accepted only when it has a valid same-floor route.
            Vector3 goal = TargetGround;
            float terminalSpeed = 0;
            if (streamUserMoving && streamUserVelocity.magnitude < 3f)
            {
                // Model conditioning covers two future seconds, not just the next committed
                // prefix. A moving user is a moving endpoint: repeatedly showing a stop 0.5s
                // ahead induces alternating starts/braking even when the short prefix rolls.
                Vector3 prediction = goal + Vector3.ClampMagnitude(streamUserVelocity * 2.4f, 3f);
                if (navigation.TryPlan(seamGround, prediction, out var predicted, out _))
                {
                    goal = predicted.Corners[predicted.Corners.Length - 1];
                    terminalSpeed = Mathf.Min(StreamDesiredSpeed, streamUserVelocity.magnitude);
                }
            }
            if (!navigation.TryPlan(seamGround, goal, out var route, out string reason))
                throw new ExecutionException("持续路径不可达 · " + reason, ArdyRoomExecutionReason.NavigationRejected);
            StreamRequest request = initialHistory == null ? new StreamRequest() :
                new InitialStreamRequest { initialHistory = new History { frames = initialHistory } };
            request.characterId = characterId; request.sessionId = streamSessionId;
            request.requestId = Guid.NewGuid().ToString("N"); request.previousRequestId = streamPreviousRequestId;
            request.revision = streamWireRevision; request.stepIndex = streamStepIndex; request.startFrame = start;
            // The first step also obtains Qwen features. Its cold-start budget is separate
            // from the tight cached-condition continuation budget; neither extends indefinitely.
            request.timeoutMs = streamStepIndex == 0 ? 20000 : 5000;
            request.controlRevision = StreamControlRevision;
            float controlReceivedAt = streamControlReceivedAt;
            requestId = request.requestId;
            LastResponseJson = null; LastErrorResponseJson = null;
            streamRequestPending = false;
            streamRoundTrip = streamBackend = streamBackendQueue = streamFeature = streamPlanning = streamParse = streamAudit = 0;
            LastRequestJson = JsonUtility.ToJson(request);
            var evidence = new SegmentEvidence { operationRevision = token, wireRevision = streamWireRevision,
                segment = streamStepIndex + 1, startFrame = start, requestFrames = streamCapabilities.streamGenerationFrames, targetRevision = StreamControlRevision,
                historyKind = initialHistory == null ? "native-committed-prefix" : "warm-rendered-history",
                requestId = requestId, requestJson = LastRequestJson, submittedAtRealtime = Time.realtimeSinceStartup };
            RecordSegmentEvidence(evidence);
            Stage = "stream-control";
            ArdyRoomRouteDiagnostic lastControlRejection = null;
            try
            {
                request.targets = ArdyRoomStreamTrajectory.Build(route, sourcePosition, heading, speed, StreamDesiredSpeed,
                    anchorRotation, anchorGround, motionScale, 40, terminalSpeed, neutralHorizontal,
                    (from, to) => {
                        string rejected = GuardStepDetailed(from, to, out var detail, false);
                        if (rejected != null) lastControlRejection = detail;
                        return rejected == null;
                    }, streamControlPath);
            }
            catch (InvalidOperationException error)
            {
                streamPlanning = Time.realtimeSinceStartup - planningStarted;
                var observation = lastControlRejection ?? new ArdyRoomRouteDiagnostic { stage = "control-target", code = "control-corridor-infeasible",
                    reason = error.Message, start = seamGround, target = goal, from = seamGround, to = seamGround };
                SaveControlFailure(error, route, sourcePosition, heading, speed, terminalSpeed, seamGround);
                string explanation = error is ArdyRoomStreamTrajectory.KinematicsException ?
                    "当前速度不能安全衔接这段转弯或停止目标；尚不能据此判断整个房间不可达 · " : "动作控制轨迹未通过审核 · ";
                var rejection = RejectGeneratedPrefix(null, -1, seamGround, seamGround, explanation + error.Message,
                    observation, error is ArdyRoomStreamTrajectory.KinematicsException ? "motion-control" : "navigation", evidence, route, "control-corridor");
                throw new ExecutionException(rejection.Message, rejection.Reason, error);
            }
            LastRequestJson = evidence.requestJson = JsonUtility.ToJson(request);
            Vector3 controlPrevious = seamGround;
            // Audit the complete conditioning horizon before HTTP. The future user's
            // position is unknown, so the user guard is reserved for confirmed prefixes
            // and live playback; static/dynamic room geometry is checked for all 40.
            for (int i = 0; i < request.targets.Length; i++)
            {
                var target = request.targets[i];
                Vector3 root = anchorGround + anchorRotation * (target.rootPosition * motionScale) -
                    anchorRotation * Quaternion.Euler(0, target.headingDegrees, 0) * neutralHorizontal;
                root.y = floorY;
                string blocked = GuardStepDetailed(controlPrevious, root, out var observation, false);
                if (blocked != null)
                {
                    streamPlanning = Time.realtimeSinceStartup - planningStarted;
                    throw RejectGeneratedPrefix(null, -1, controlPrevious, root, "动作控制目标未通过房间审核 · " + blocked,
                        observation, "navigation", evidence, route, "control-target", i);
                }
                controlPrevious = root;
            }
            streamPlanning = Time.realtimeSinceStartup - planningStarted;
            pending = JsonRequest("/v1/locomotion/stream/step", LastRequestJson, 6);
            streamDispatchTime = Time.realtimeSinceStartup;
            // These observations belong to this request ID. Do not attach the previous
            // request's backend duration to a still-pending request or its timeout.
            streamRequestPending = true;
            streamRoundTrip = streamBackend = streamBackendQueue = streamFeature = streamParse = streamAudit = 0;
            Stage = ownedClip == null ? (streamRecovering ? "stream-recovering" : "stream-prebuffering") : "stream-moving";
            RecordStreamEvent("request");
            BeginStreamWireReceipt();
            yield return pending.SendWebRequest();
            EndStreamWireReceipt("response-observed", false, request.requestId);
            if (!Current(token) || !IsContinuousFollowActive || streamSessionId != generationSession ||
                streamWireRevision != generationWire) yield break;
            CheckContinuousStreamPlayerOwnership();
            streamRoundTrip = Time.realtimeSinceStartup - streamDispatchTime; streamRequestPending = false;
            float parsingStarted = Time.realtimeSinceStartup;
            string json = ReadAndDisposeResponse(); LastResponseJson = json;
            StreamGenerationSeconds = streamRoundTrip;
            evidence.responseJson = json; evidence.completedAtRealtime = Time.realtimeSinceStartup; evidence.outcome = "stream-auditing";
            var identity = JsonUtility.FromJson<StreamResponse>(json);
            streamBackend = (identity.timings?.backendMs ?? 0) / 1000f;
            streamBackendQueue = ((identity.timings?.backendGateMs ?? 0) + (identity.timings?.queueMs ?? 0)) / 1000f;
            streamFeature = (identity.timings?.featureMs ?? 0) / 1000f;
            if (identity.characterId != characterId || identity.sessionId != streamSessionId || identity.requestId != request.requestId ||
                identity.revision != streamWireRevision || identity.stepIndex != streamStepIndex || identity.startFrame != start ||
                identity.commitFrames != 8 || identity.controlRevision != request.controlRevision)
                throw new InvalidOperationException("持续生成响应不属于当前会话和姿态前缀。");
            var candidate = ArdyLocomotionClip.Parse(json);
            if (candidate.frames.Length != streamCapabilities.streamGenerationFrames || candidate.rotationClip.fps != 20 ||
                Mathf.Abs(candidate.sourceRootHeight - sourceRootHeight) > .0001f ||
                candidate.sourceOrigin != Vector3.zero || candidate.sourceHeadingDegrees != 0)
                throw new InvalidOperationException("持续生成改变了固定的源坐标基准。");
            for (int j = 0; j < 27; j++)
                if (candidate.rotationClip.jointNames[j] != ArdyLiveProtocol.JointNames[j] ||
                    candidate.rotationClip.jointParents[j] != ArdyLiveProtocol.JointParents[j] ||
                    (timeline != null && Quaternion.Angle(candidate.rotationClip.restGlobalRotations[j], timeline.rotationClip.restGlobalRotations[j]) > .001f))
                    throw new InvalidOperationException("持续生成改变了骨骼或旋转基准。");
            streamParse = Time.realtimeSinceStartup - parsingStarted;
            float auditStarted = Time.realtimeSinceStartup;
            Vector3 previous = timeline == null ? anchorRootPosition : GeneratedRoot(timeline, localStart - 1);
            Vector3 previousSource = sourcePosition;
            var previousRotations = new Quaternion[27];
            for (int j = 0; j < 27; j++) previousRotations[j] = initialHistory != null ? initialHistory[15].globalRotations[j] :
                timeline.rotationClip.SampleDelta(j, (localStart - 1) / 20f);
            // Only these eight samples can be rendered or acknowledged. The other 32
            // remain an uncommitted prediction and never bypass a later prefix audit.
            for (int i = 0; i < request.commitFrames; i++)
            {
                Vector3 sourceRoot = candidate.frames[i].rootPosition;
                if (sourceRoot.y < sourceRootHeight * .72f || sourceRoot.y > sourceRootHeight * 1.25f)
                    throw RejectGeneratedPrefix(candidate, i, previous, GeneratedRoot(candidate, i),
                        "持续步态出现异常下蹲或抬升，已拒绝采用该短段。", category: "motion-quality", evidence: evidence, checkedRoute: route);
                if (Vector3.Distance(sourceRoot, previousSource) > .1f)
                    throw RejectGeneratedPrefix(candidate, i, previous, GeneratedRoot(candidate, i),
                        "持续步态出现根位移突变，已拒绝采用该短段。", category: "motion-quality", evidence: evidence, checkedRoute: route);
                for (int j = 0; j < 27; j++)
                {
                    Quaternion rotation = candidate.rotationClip.SampleDelta(j, i / 20f);
                    if (player.AppliesSourceJoint(j) && Quaternion.Angle(rotation, previousRotations[j]) > 35f)
                        throw RejectGeneratedPrefix(candidate, i, previous, GeneratedRoot(candidate, i),
                            "持续步态关节突变 · " + ArdyLiveProtocol.JointNames[j], category: "motion-quality", evidence: evidence, checkedRoute: route);
                    previousRotations[j] = rotation;
                }
                previousSource = sourceRoot;
                Vector3 next = GeneratedRoot(candidate, i);
                string rejected = GuardStepDetailed(previous, next, out var rejectedObservation);
                if (rejected != null) throw RejectGeneratedPrefix(candidate, i, previous, next,
                    "持续生成路径未通过房间审核 · " + rejected, rejectedObservation, evidence: evidence, checkedRoute: route);
                previous = next;
            }
            evidence.auditedFrames = request.commitFrames;
            if (ownedClip == null)
            {
                CheckContinuousStreamPlayerOwnership();
                CheckWaitingAnchor();
                // Commit native prefixes into a private buffer first. The player's body
                // clock must not start while only the first 0.4s prefix is available.
                streamPreparedClip = timeline == null ? candidate : ArdyRoomStreamTrajectory.Splice(timeline, localStart, candidate, 0,
                    out _, out _, player.AppliesSourceJoint);
            }
            else
            {
                int drop = Mathf.Max(0, localStart - 24);
                var combined = ArdyRoomStreamTrajectory.Splice(ownedClip, localStart, candidate, drop,
                    out float rootStep, out float jointStep, player.AppliesSourceJoint);
                player.ReplaceStreamingTail(combined, localStart, drop);
                ownedClip = combined; ownedPlayerRevision = player.PlaybackRevision;
                streamBaseFrame += drop;
                player.SetPoseRecordingIdentity(OperationRevision, streamWireRevision, streamSessionId, streamBaseFrame);
                LastSeamRootStep = evidence.seamSourceRootStep = rootStep;
                LastSeamMaximumJointStep = evidence.seamMaximumJointStep = jointStep;
            }
            LastClip = streamPreparedClip ?? ownedClip; streamPreviousRequestId = request.requestId;
            streamNextFrame = start + 8; streamStepIndex++; StreamStepCount++;
            streamCommittedSpeed = streamControlPath.CommittedSpeed;
            StreamAcceptedControlRevision = request.controlRevision;
            streamControls.Enqueue(new StreamControlSample { frame = start, revision = request.controlRevision, receivedAt = controlReceivedAt });
            streamAudit = Time.realtimeSinceStartup - auditStarted;
            float stepCost = Mathf.Max(0, streamRoundTrip - (initialHistory != null ? streamFeature : 0)) + streamParse + streamAudit + streamPlanning;
            streamRecentStepCost = Mathf.Max(streamRecentStepCost * .95f, stepCost);
            evidence.responseJson = json; evidence.completedAtRealtime = Time.realtimeSinceStartup; evidence.outcome = "stream-accepted";
            IsBusy = true;
            if (streamPreparedClip != null)
            {
                Stage = streamRecovering ? "stream-recovering" : "stream-prebuffering";
                SetStatus($"正在准备连续步态 · 已审核 {streamNextFrame / 20f:F1} / {StreamReadyTargetSeconds:F1} 秒", ArdyRoomExecutionState.Preparing);
            }
            else
            {
                Stage = "stream-moving";
                SetStatus($"持续跟随 · 期望 {StreamDesiredSpeed:F2} 米/秒 · 第 {StreamStepCount} 个短段", ArdyRoomExecutionState.Moving);
            }
            RecordStreamEvent("accepted");
        }

        private void StartPreparedStream()
        {
            CheckContinuousStreamPlayerOwnership(); CheckWaitingAnchor();
            // Recheck the whole committed prefix against the CURRENT room/user state.
            // The user may have moved while the initial requests were in flight.
            Vector3 previous = anchorRootPosition; float began = Time.realtimeSinceStartup;
            for (int i = 0; i < streamNextFrame; i++)
            {
                Vector3 next = GeneratedRoot(streamPreparedClip, i);
                string blocked = GuardStepDetailed(previous, next, out var observation);
                if (blocked != null) throw RejectGeneratedPrefix(streamPreparedClip, i, previous, next,
                    "起步动作未通过当前房间审核 · " + blocked, observation, checkPhase: "prebuffer-playback");
                previous = next;
            }
            var prepared = streamPreparedClip;
            player.Play(prepared, GuardStep, streamNextFrame - 1);
            player.SetPoseRecordingIdentity(OperationRevision, streamWireRevision, streamSessionId, streamBaseFrame);
            ownedClip = prepared; ownedPlayerRevision = player.PlaybackRevision; streamPreparedClip = null;
            if (TryHandleLiveGuardStop()) return;
            streamRecovering = false; streamHealthyPlaySince = Time.realtimeSinceStartup;
            streamAudit += Time.realtimeSinceStartup - began;
            Stage = "stream-moving";
            SetStatus("连续步态已准备，开始跟随", ArdyRoomExecutionState.Moving);
            RecordStreamEvent("playing");
        }

        private void TickContinuousStream()
        {
            float now = Time.realtimeSinceStartup, dt = now - streamMeasuredTime;
            streamRecentFramePeak = Mathf.Max(Time.unscaledDeltaTime, streamRecentFramePeak * Mathf.Exp(-Time.unscaledDeltaTime / 2f));
            if (dt > .05f)
            {
                Vector3 delta = avatar.transform.position - streamMeasuredRoot; delta.y = 0;
                StreamMeasuredSpeed = Mathf.Lerp(StreamMeasuredSpeed, delta.magnitude / dt, 1 - Mathf.Exp(-dt / .15f));
                streamMeasuredTime = now; streamMeasuredRoot = avatar.transform.position;
            }
            if (ownedClip == null)
            {
                try { CheckContinuousStreamPlayerOwnership(); }
                catch (ExecutionException error) { Fail(error.Message, error.Reason); }
                return;
            }
            if (player.Clip != ownedClip || player.PlaybackRevision != ownedPlayerRevision ||
                (!player.HasPoseOwnership && !player.ReturnedToAnimation))
            { Fail("持续跟随身体控制已被释放或替换", ArdyRoomExecutionReason.OwnershipLost); return; }
            if (player.IsReturningToAnimation || player.ReturnedToAnimation) return;
            // Arrival uses the rendered root and newest target, not a future
            // prefix's old endpoint. Do not turn a completed approach into a
            // supply failure while a centimetre-sized endpoint update arrives.
            if (streamSessionId != null && StreamRenderedArrival())
            { RecordStreamArrivalBoundary("rendered-arrival-before-supply-check", StreamControlRevision); FinishStreamArrival(); return; }
            if (streamConsecutiveRecoveries > 0 && now - streamHealthyPlaySince >= 10f)
            {
                streamConsecutiveRecoveries = 0;
                RecordStreamEvent("recovery-budget-restored");
            }
            int displayedFrame = streamBaseFrame + Mathf.FloorToInt(player.TimeSeconds * 20);
            while (streamControls.Count > 0 && streamControls.Peek().frame <= displayedFrame)
            {
                var control = streamControls.Dequeue();
                StreamAppliedControlRevision = control.revision;
                StreamControlLatencySeconds = now - control.receivedAt;
            }
            // Never play a stale unacknowledged prediction when generation or networking falls
            // behind. Stop from the currently rendered pose rather than replaying a distant goal.
            if (streamSessionId != null && streamStepIndex > 0 && StreamBufferSeconds < .065f)
            {
                if (Stage == "stream-arriving" && StreamPrefixReachesGoal() &&
                    HorizontalDistance(AvatarGround(), TargetGround) <= ArrivalTolerance)
                { FinishStreamArrival(); return; }
                StreamUnderrunCount++;
                RecoverStreamSupply();
            }
        }

        private void RecoverStreamSupply()
        {
            const string reason = "下一段已审核动作未及时接上，已停止位移并平滑衔接待机。";
            if (!PrepareStreamRecovery(reason, "motion-supply", "underrun", true)) return;
            int token = OperationRevision;
            work = StartCoroutine(GuardedStreamRun(RunContinuousStream(token), token));
        }

        private bool PrepareStreamRecovery(string reason, string category, string trigger, bool stopRunningCoroutine)
        {
            if (streamConsecutiveRecoveries >= StreamRecoveryLimit)
            {
                RecordStreamEvent("recovery-exhausted", reason + "连续恢复次数已达到上限，跟随已结束。", category);
                Fail(reason + "连续恢复次数已达到上限，跟随已结束。", ArdyRoomExecutionReason.GenerationFailed);
                return false;
            }
            streamConsecutiveRecoveries++; StreamRecoveryCount++;
            // Capture the pending request and buffer state BEFORE clearing that generation.
            RecordStreamEvent(trigger, reason, category);
            EndStreamWireReceipt("recovery:" + category, true);
            if (stopRunningCoroutine && work != null) { StopCoroutine(work); work = null; }
            if (pending != null) { pending.Abort(); pending.Dispose(); pending = null; }
            streamRequestPending = false;
            foreach (var item in segmentEvidence)
                if (item.outcome == "generating" || item.outcome == "stream-auditing")
                { item.outcome = "stream-recovery-discarded"; item.completedAtRealtime = Time.realtimeSinceStartup; }
            CloseStreamWire(); streamPreparedClip = null;
            if (ownedClip != null && player.Clip == ownedClip && player.PlaybackRevision == ownedPlayerRevision)
            {
                try
                {
                    if (player.RequestSmoothStop(ReturnToIdleSeconds)) ownedPlayerRevision = player.PlaybackRevision;
                    else ownedClip = null;
                }
                catch (Exception error)
                {
                    Fail("持续跟随恢复无法完成身体交接 · " + error.Message, ArdyRoomExecutionReason.OwnershipLost);
                    return false;
                }
            }
            streamRecovering = true; streamRecoverAfter = Time.realtimeSinceStartup + .35f * streamConsecutiveRecoveries;
            Stage = "stream-recovering"; IsBusy = true;
            SetStatus(category == "motion-quality" ? $"动作质量拒绝，正在从实际姿态有限恢复（{StreamQualityRecoveryCount}/{StreamQualityRecoveryLimit}）" :
                category == "motion-control" ? $"原续段无法安全衔接，停稳后从实际姿态重新检查（{StreamControlRecoveryCount}/{StreamControlRecoveryLimit}）" :
                $"动作供给暂时不足，平滑等待后重新准备（{streamConsecutiveRecoveries}/{StreamRecoveryLimit}）", ArdyRoomExecutionState.Preparing);
            RecordStreamEvent("recovering");
            // Same parent operation, but a fresh native session/history after the return.
            // Explicit Cancel increments revision and stops this work, so it cannot revive.
            return true;
        }

        private void ObserveWarmStreamPose()
        {
            if (avatar == null || player == null || !Application.isPlaying || AvatarUnavailableReason() != null)
            {
                warmStreamPoses.Clear(); warmHistoryCaptureError = AvatarUnavailableReason() ?? "角色、动作播放器或 Play 状态不可用。";
                RejectWarmHistory("capture-unavailable", warmHistoryCaptureError); return;
            }
            float now = Time.realtimeSinceStartup;
            if (warmStreamPoses.Count > 0 && now - warmStreamPoses[warmStreamPoses.Count - 1].time < .02f) return;
            try
            {
                var hips = avatar.Humanoid.GetBoneTransform(HumanBodyBones.Hips);
                warmStreamPoses.Add(new WarmPose { time = now, hips = hips.position, root = avatar.transform.position,
                    rotation = avatar.transform.rotation, parent = avatar.transform.parent, scale = avatar.transform.lossyScale.y,
                    worldRotations = player.CaptureFullBodyHistoryFrame(Quaternion.identity).globalRotations });
                warmHistoryCaptureError = null;
                while (warmStreamPoses.Count > 2 && warmStreamPoses[1].time < now - 1.05f) warmStreamPoses.RemoveAt(0);
            }
            catch (Exception error)
            {
                warmStreamPoses.Clear(); warmHistoryCaptureError = error.GetType().Name + ": " + error.Message;
                if (warmHistoryCaptureError.Length > 512) warmHistoryCaptureError = warmHistoryCaptureError.Substring(0, 512);
                RejectWarmHistory("capture-exception", "读取实际全身姿态失败 · " + warmHistoryCaptureError);
            }
        }

        private void CheckContinuousStreamPlayerOwnership()
        {
            try { RejectForeignPlayerOwner(); }
            catch (InvalidOperationException error)
            { throw new ExecutionException(error.Message, ArdyRoomExecutionReason.OwnershipLost); }
        }

        private bool TryWarmStreamHistory(out ArdyRoomHistoryFrame[] history)
        {
            history = null;
            if (warmStreamPoses.Count < 2) return RejectWarmHistory(
                warmHistoryCaptureError == null ? "sample-count" : "capture-unavailable",
                warmHistoryCaptureError == null ? "实际姿态样本不足 2 帧。" : "实际姿态采集未成功 · " + warmHistoryCaptureError,
                measured: warmStreamPoses.Count, limit: 2);
            var last = warmStreamPoses[warmStreamPoses.Count - 1];
            float start = last.time - .75f;
            if (warmStreamPoses[0].time > start) return RejectWarmHistory("window-too-short", "实际姿态历史尚未覆盖 0.75 秒。",
                measured: last.time - warmStreamPoses[0].time, limit: .75f);
            if (Time.realtimeSinceStartup - last.time > .15f) return RejectWarmHistory("latest-sample-stale", "最新实际姿态样本已超过 0.15 秒。",
                measured: Time.realtimeSinceStartup - last.time, limit: .15f);
            var values = new ArdyRoomHistoryFrame[16]; int cursor = 1;
            Vector3 velocityStart = default, velocityEnd = default;
            Quaternion inverse = Quaternion.Inverse(anchorRotation);
            for (int i = 0; i < 16; i++)
            {
                float time = start + i / 20f;
                while (cursor < warmStreamPoses.Count - 1 && warmStreamPoses[cursor].time < time) cursor++;
                var a = warmStreamPoses[cursor - 1]; var b = warmStreamPoses[cursor];
                if (b.time - a.time > .15f) return RejectWarmHistory("sample-gap", "连续实际姿态的采样间隔超过 0.15 秒。", i, b.time - a.time, .15f);
                if (a.parent != anchorParent || b.parent != anchorParent)
                    return RejectWarmHistory("parent-changed", "实际姿态历史中的角色父节点与本次动作基准不同。", i);
                float scaleError = Mathf.Max(Mathf.Abs(a.scale - worldScale), Mathf.Abs(b.scale - worldScale));
                if (scaleError > .0001f) return RejectWarmHistory("scale-changed", "实际姿态历史中的角色缩放与本次动作基准不同。", i, scaleError, .0001f);
                float distance = Vector3.Distance(a.root, b.root), maxDistance = 3f * (b.time - a.time) + .025f;
                if (distance > maxDistance) return RejectWarmHistory("root-position-jump", "实际姿态历史包含过大的角色根位置跳变。", i, distance, maxDistance);
                float heightStep = Mathf.Abs(a.root.y - b.root.y);
                if (heightStep > .035f) return RejectWarmHistory("root-height-jump", "实际姿态历史包含超过 0.035 米的角色根高度跳变。", i, heightStep, .035f);
                float rotationStep = Quaternion.Angle(a.rotation, b.rotation), maxRotation = 360f * (b.time - a.time) + 5f;
                if (rotationStep > maxRotation) return RejectWarmHistory("root-rotation-jump", "实际姿态历史包含过大的角色根朝向跳变。", i, rotationStep, maxRotation);
                float t = Mathf.InverseLerp(a.time, b.time, time);
                if (i == 12) velocityStart = Vector3.Lerp(a.root, b.root, t);
                if (i == 15) velocityEnd = Vector3.Lerp(a.root, b.root, t);
                var q = new Quaternion[27];
                for (int j = 0; j < 27; j++) q[j] = inverse * Quaternion.Slerp(a.worldRotations[j], b.worldRotations[j], t);
                Vector3 source = inverse * (Vector3.Lerp(a.hips, b.hips, t) - anchorGround) / motionScale;
                if (source.y < .25f || source.y > 2.5f) return RejectWarmHistory("pelvis-height", "实际骨盆映射到源坐标后的高度不在 0.25–2.5 米内。", i, source.y, source.y < .25f ? .25f : 2.5f);
                values[i] = new ArdyRoomHistoryFrame { rootPosition = source, globalRotations = q };
            }
            history = values;
            Vector3 rootVelocity = velocityEnd - velocityStart; rootVelocity.y = 0;
            warmStreamRootSpeed = rootVelocity.magnitude / .15f;
            RejectWarmHistory("ready", "连续实际姿态历史已就绪。"); warmHistoryObservation.success = true;
            return true;
        }

        private void CloseStreamWire()
        {
            EndStreamWireReceipt("session-closed", true);
            if (streamSessionId != null && characterId != null)
            {
                UnityWebRequest cancel = null;
                try
                {
                    cancel = JsonRequest("/v1/locomotion/stream/cancel", JsonUtility.ToJson(new StreamCancellation {
                        characterId = characterId, sessionId = streamSessionId, revision = streamWireRevision }), 5);
                    var owned = cancel;
                    string closedSession = streamSessionId; int closedWire = streamWireRevision;
                    cancel.SendWebRequest().completed += _ => {
                        Debug.Log("[ARDY room/wire-cancel] " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
                            sessionId = closedSession, revision = closedWire, observedUtc = DateTime.UtcNow.ToString("o"),
                            httpStatus = owned.responseCode, transportResult = owned.result.ToString(), error = owned.error,
                            response = owned.downloadHandler?.text,
                            scope = "Cancellation acknowledgement only; does not prove whether generation completed before cancellation."
                        }), this);
                        owned.Dispose();
                    };
                }
                catch { cancel?.Dispose(); }
            }
            streamSessionId = null; streamPreviousRequestId = null;
            streamControlPath.Clear();
            streamControls.Clear();
        }

        private void CancelContinuousStream()
        {
            if (IsContinuousFollowActive) RecordStreamEvent("cancelled");
            IsContinuousFollowActive = false;
            streamRequestPending = false; streamRecovering = false; streamPreparedClip = null;
            CloseStreamWire();
        }
    }
}
