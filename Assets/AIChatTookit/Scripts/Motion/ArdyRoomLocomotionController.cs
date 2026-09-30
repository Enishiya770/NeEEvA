using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UniVRM10;
using UnityEngine;
using UnityEngine.Networking;

namespace NeEEvA.Motion
{
    public enum ArdyRoomExecutionState { Idle, Preparing, Generating, Moving, Returning, Arrived, Cancelled, Blocked, Failed }
    public enum ArdyRoomExecutionReason
    {
        None, AlreadyAtTarget, Completed, CancelledByCaller, NavigationRejected,
        GeneratedPathRejected, GenerationFailed, TerrainBlocked, OwnershipLost, AvatarUnavailable
    }

    [Serializable] public sealed class ArdyRoomHistoryFrame
    {
        public Vector3 rootPosition;
        public Quaternion[] globalRotations;
    }

    /// <summary>
    /// Explicit room locomotion session. Owns only an opted-in avatar, cancels stale requests,
    /// captures rendered full-body history and checks the actual generated root against terrain.
    /// A navigation path is not authorization to play an unchecked generated trajectory.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(12000)]
    public sealed partial class ArdyRoomLocomotionController : MonoBehaviour
    {
        [Serializable] public sealed class SegmentEvidence
        {
            public int operationRevision, wireRevision, segment, totalSegments, startFrame, requestFrames;
            public int targetRevision;
            public int auditedFrames;
            public string requestId, historyKind, requestJson, responseJson, outcome = "generating";
            public float submittedAtRealtime, completedAtRealtime, seamSourceRootStep, seamMaximumJointStep;
        }
        [Serializable] private sealed class Capabilities
        {
            public bool ready;
            public float fps, sourceRootHeight;
            public int historyFrames, maxFrames;
            public string coordinateSystem;
            public string streamProtocol;
            public int streamCommitFrames, streamGenerationFrames;
            public string[] jointNames;
        }
        [Serializable] private sealed class History
        {
            public string kind = "unity-full-body-projection-v1";
            public float fps = 20;
            public string[] jointNames = ArdyLiveProtocol.JointNames;
            public ArdyRoomHistoryFrame[] frames;
        }
        [Serializable] private sealed class Request
        {
            public int schema = 1;
            public string characterId, requestId;
            public int revision, seed, timeoutMs = 20000;
            public string description = "A person walks naturally along the indicated path and comes to a relaxed stop.";
            public ArdyLocomotionTarget[] targets;
            public History initialHistory;
        }
        [Serializable] private sealed class ResponseIdentity
        {
            public string characterId, requestId;
            public int revision;
        }
        [Serializable] private sealed class Cancellation
        {
            public string characterId, requestId;
            public int revision;
        }
        private sealed class ExecutionException : InvalidOperationException
        {
            public readonly ArdyRoomExecutionReason Reason;
            public ExecutionException(string message, ArdyRoomExecutionReason reason) : base(message) { Reason = reason; }
        }

        public string Endpoint = "http://127.0.0.1:8093";
        [Range(.15f, 1.2f)] public float WalkingSpeed = .65f;
        [Range(.05f, .5f)] public float ArrivalTolerance = .2f;
        [Range(.1f, 1f)] public float ReturnToIdleSeconds = .4f;
        public Vrm10Instance Avatar => avatar;
        public ArdyLocomotionPlayer Player => player;
        public ArdyRoomNavigation Navigation => navigation;
        public ArdyRoomRoute Route { get; private set; }
        public ArdyLocomotionClip LastClip { get; private set; }
        public string LastRequestJson { get; private set; }
        public string LastResponseJson { get; private set; }
        public string LastErrorResponseJson { get; private set; }
        public bool IsBusy { get; private set; }
        public bool IsMoving => ownedClip != null && player != null && (player.IsPlaying || player.IsReturningToAnimation);
        /// <summary>Exact current clip/revision ownership, including audited stream continuations.</summary>
        public bool OwnsActivePlayerPose => ownedClip != null && player != null && player.Clip == ownedClip &&
            player.PlaybackRevision == ownedPlayerRevision && player.HasPoseOwnership;
        public bool IsReturningOwnedPose => ownedClip != null && player != null && player.Clip == ownedClip &&
            player.PlaybackRevision == ownedPlayerRevision && player.IsStoppingSmoothly && player.HasPoseOwnership;
        public string Status { get; private set; } = "尚未绑定房间角色";
        public string Stage { get; private set; } = "idle";
        public ArdyRoomExecutionState ExecutionState { get; private set; } = ArdyRoomExecutionState.Idle;
        public ArdyRoomExecutionReason ExecutionReason { get; private set; } = ArdyRoomExecutionReason.None;
        /// <summary>Latest invalidation token; changes whenever queued work is superseded or cancelled.</summary>
        public int Revision => revision;
        /// <summary>The MoveTo invocation responsible for the current result, including synchronous rejection/no-op.</summary>
        public int OperationRevision { get; private set; }
        public Vector3 TargetGround { get; private set; }
        public Vector3? WorldFacePoint { get; private set; }
        public float LastEndpointError { get; private set; }
        public float LastHeadingError { get; private set; }
        public int SegmentIndex { get; private set; }
        public int SegmentCount { get; private set; }
        public int PreparedSegmentCount { get; private set; }
        public float PlannedDurationSeconds { get; private set; }
        public float PlayedDurationSeconds => completedTimelineSeconds + (player == null ? 0 : player.TimeSeconds);
        public bool WaitingForSegment { get; private set; }
        public float LastSeamRootStep { get; private set; }
        public float LastSeamMaximumJointStep { get; private set; }
        public IReadOnlyList<SegmentEvidence> Segments => segmentEvidence.AsReadOnly();
        public int DiscardedSegmentEvidenceCount { get; private set; }
        public ArdyRoomArrivalTiming LastArrivalTiming { get; private set; }
        /// <summary>Runs after the terrain guard during full-clip audit and before every rendered root step.</summary>
        public Func<Vector3, Vector3, string> AdditionalStepGuard { get; set; }
        public event Action<string> StateChanged;

        private Vrm10Instance avatar;
        private ArdyLocomotionAvatarReference reference;
        private ArdyRoomNavigation navigation;
        private ArdyLocomotionPlayer player;
        private ArdyLocomotionClip ownedClip;
        private UnityWebRequest pending;
        private Coroutine work;
        private string characterId, requestId;
        private int revision, wireRevision;
        private int nextSeamFrame = -1, continuationRetry, timelineBaseSegment;
        private bool continuationLate, timelineFinal;
        private float completedTimelineSeconds;
        private float? requestedSignedInPlaceYaw;
        private bool requestedTargetFacingTranslation;
        public const float ViewPositionArrivalTolerance = .07f;
        private float EffectiveArrivalTolerance => requestedTargetFacingTranslation ? Mathf.Min(ArrivalTolerance, ViewPositionArrivalTolerance) : ArrivalTolerance;
        private ArdyRoomSegmentPlan segmentPlan;
        private readonly List<SegmentEvidence> segmentEvidence = new List<SegmentEvidence>();
        private long ownedPlayerRevision;
        private Vector3 anchorRootPosition, anchorGround, neutralHorizontal;
        private Quaternion anchorRotation;
        private Transform anchorParent;
        private float worldScale, motionScale, sourceRootHeight, floorY;
        private bool captureHistory;
        private bool boundVrmEnabled;
        private ArdyRoomHistoryFrame latestObservedFrame;
        private float latestObservedTime;

        public void Configure(Vrm10Instance target, ArdyLocomotionAvatarReference importedReference, ArdyRoomNavigation nav)
        {
            if (!Application.isPlaying) throw new InvalidOperationException("房间移动仅在 Play 模式显式启用。");
            if (target == null || importedReference == null || nav == null) throw new ArgumentException("角色、导入静止参考和已构建的房间导航不可缺少。");
            RejectForeignPlayerOwner();
            Cancel();
            if (player != null && player.IsStoppingSmoothly) player.Stop();
            importedReference.Validate();
            avatar = target; reference = importedReference; navigation = nav;
            characterId = "unity-room-" + target.GetInstanceID() + "-" + Guid.NewGuid().ToString("N");
            player = GetComponent<ArdyLocomotionPlayer>();
            if (player == null) player = gameObject.AddComponent<ArdyLocomotionPlayer>();
            player.DiscardPreviewAnchor();
            player.Bind(target, importedReference);
            boundVrmEnabled = target.enabled;
            ArdyRoomPoseRecorder.EnsureAttached(this, reference);
            SetStatus("房间移动已绑定；选择同一地面上的目标", ArdyRoomExecutionState.Idle);
        }

        public void MoveTo(Vector3 worldGroundTarget) => MoveTo(worldGroundTarget, null);

        public void MoveTo(Vector3 worldGroundTarget, Vector3? worldFacePoint)
            => MoveToCore(worldGroundTarget, worldFacePoint, null);

        /// <summary>Bounded room observation movement with independent position and body-facing targets.</summary>
        public void MoveToFacingTarget(Vector3 worldGroundTarget, Vector3 worldFacePoint)
        {
            if (!ArdyLocomotionClip.Finite(worldGroundTarget) || !ArdyLocomotionClip.Finite(worldFacePoint) || avatar == null || reference == null)
                throw new ArgumentException("Target-facing movement requires a bound actor and finite engine targets.");
            Vector3 offset = worldGroundTarget - AvatarGround(); offset.y = 0;
            if (offset.magnitude < .175f || offset.magnitude > 1.225f)
                throw new ArgumentException("Observation movement must remain within the explicitly bounded short offset.");
            MoveToCore(worldGroundTarget, worldFacePoint, null, true);
        }

        /// <summary>One native in-place body operation with an explicit left/right turn.
        /// Used by scene orientation only; never changes the avatar transform directly.</summary>
        public void TurnInPlace(Vector3 worldGroundTarget, float signedYaw)
        {
            if (!ArdyLocomotionClip.Finite(worldGroundTarget) || !ArdyLocomotionClip.Finite(signedYaw) ||
                Mathf.Abs(signedYaw) < 5f || Mathf.Abs(signedYaw) > 180f || avatar == null || reference == null)
                throw new ArgumentException("Body turn requires a bound actor and a signed 5..180 degree yaw.");
            if (Vector3.Distance(AvatarGround(), worldGroundTarget) > .08f)
                throw new ArgumentException("In-place turning cannot move to a different ground position.");
            Vector3 direction = Quaternion.AngleAxis(signedYaw, Vector3.up) * avatar.transform.forward;
            direction.y = 0;
            MoveToCore(worldGroundTarget, worldGroundTarget + direction.normalized, signedYaw);
        }

        private void MoveToCore(Vector3 worldGroundTarget, Vector3? worldFacePoint, float? signedInPlaceYaw, bool targetFacingTranslation = false)
        {
            if (!Application.isPlaying || !isActiveAndEnabled || avatar == null || navigation == null)
                throw new InvalidOperationException("先在 Play 模式绑定角色与房间导航。");
            string avatarReason = AvatarUnavailableReason();
            if (avatarReason != null) throw new InvalidOperationException(avatarReason);
            if (!ArdyLocomotionClip.Finite(worldGroundTarget)) throw new ArgumentException("目标必须是有限的世界坐标。");
            if (worldFacePoint.HasValue && !ArdyLocomotionClip.Finite(worldFacePoint.Value)) throw new ArgumentException("面向点必须是有限的世界坐标。");
            RejectForeignPlayerOwner();
            if (player != null && player.IsStoppingSmoothly)
                throw new InvalidOperationException("上一次移动正在平滑释放身体控制；请等待停止完成再开始新路线。");
            Cancel();
            if (player != null && player.IsStoppingSmoothly)
                throw new InvalidOperationException("已请求平滑停止当前移动；等待停止完成后再提交新路线。");
            int token = ++revision;
            OperationRevision = token;
            requestedSignedInPlaceYaw = signedInPlaceYaw;
            requestedTargetFacingTranslation = targetFacingTranslation;
            WorldFacePoint = worldFacePoint;
            Route = null;
            TargetGround = worldGroundTarget;
            ResetMovingTarget(worldGroundTarget, worldFacePoint);
            LastErrorResponseJson = null;
            LastRequestJson = LastResponseJson = null;
            LastArrivalTiming = null;
            SegmentIndex = SegmentCount = PreparedSegmentCount = 0;
            segmentEvidence.Clear();
            DiscardedSegmentEvidenceCount = 0;
            PlannedDurationSeconds = completedTimelineSeconds = 0;
            continuationRetry = 0; segmentPlan = null; timelineFinal = true;
            nextSeamFrame = -1; continuationLate = WaitingForSegment = false;
            LastEndpointError = LastHeadingError = 0;
            Stage = "navigation";
            SetStatus("正在审核房间移动目标", ArdyRoomExecutionState.Preparing);
            if (!navigation.TryProjectGround(AvatarGround(), out Vector3 startGround, out string reason) ||
                !navigation.TryPlan(startGround, worldGroundTarget, out var route, out reason))
            { SetStatus("目标不可达 · " + reason, ArdyRoomExecutionState.Blocked, ArdyRoomExecutionReason.NavigationRejected); return; }
            if (targetFacingTranslation && !ArdyRoomViewPositionPlanner.RouteMatchesOffset(route, startGround, worldGroundTarget, out reason))
            { SetStatus(reason, ArdyRoomExecutionState.Blocked, ArdyRoomExecutionReason.NavigationRejected); return; }
            if (route.Length < .08f)
            {
                if (!NeedsFacing(startGround, worldFacePoint))
                { Route = route; TargetGround = startGround; SetStatus("角色已经位于目标附近", ArdyRoomExecutionState.Arrived, ArdyRoomExecutionReason.AlreadyAtTarget); return; }
                // The ground and footprint have been checked above. Turn using native pelvis
                // constraints at the current position, rather than snapping the avatar root.
                route = new ArdyRoomRoute { Corners = new[] { startGround }, GroundY = startGround.y, Length = 0 };
            }
            if (signedInPlaceYaw.HasValue && route.Length > .00001f)
            { SetStatus("原地转身的起点投影产生了非零路线，未执行位移。", ArdyRoomExecutionState.Blocked, ArdyRoomExecutionReason.NavigationRejected); return; }
            Route = route; TargetGround = route.Corners[route.Corners.Length - 1];
            requestId = Guid.NewGuid().ToString("N");
            IsBusy = true;
            work = StartCoroutine(GuardedRun(GenerateAndPlay(token), token));
        }

        public void Cancel() => CancelCore(true);

        private void CancelCore(bool notify)
        {
            requestedSignedInPlaceYaw = null;
            requestedTargetFacingTranslation = false;
            boundaryRecovery = null;
            boundaryRecoveryWaitingForRelease = false;
            CancelContinuousStream();
            int cancelledRevision = wireRevision;
            string cancelledRequest = requestId;
            revision++;
            if (pending != null) { pending.Abort(); pending.Dispose(); pending = null; }
            foreach (var item in segmentEvidence)
                if (item.outcome == "generating") { item.outcome = "cancelled"; item.completedAtRealtime = Time.realtimeSinceStartup; }
            if (work != null) { StopCoroutine(work); work = null; }
            if (ownedClip != null && player != null && player.Clip == ownedClip && player.PlaybackRevision == ownedPlayerRevision)
            {
                try
                {
                    if (player.RequestSmoothStop(ReturnToIdleSeconds)) ownedPlayerRevision = player.PlaybackRevision;
                    else ownedClip = null;
                }
                catch (Exception error)
                {
                    // The player has already withdrawn the clip and released conflicted
                    // ownership. Network cancellation and task facts must still complete.
                    ownedClip = null;
                    Debug.LogWarning("[ARDY room] 停止时身体控制已失效：" + error.Message, this);
                }
            }
            else ownedClip = null;
            IsBusy = false; captureHistory = false;
            movingTargetQueued = retargetWorkActive = false;
            nextSeamFrame = -1; continuationLate = WaitingForSegment = false; segmentPlan = null;
            if (!string.IsNullOrEmpty(characterId) && !string.IsNullOrEmpty(cancelledRequest))
                SendCancellation(new Cancellation { characterId = characterId, requestId = cancelledRequest, revision = cancelledRevision });
            requestId = null;
            if (notify && avatar != null) SetStatus(player != null && player.HasPoseOwnership ?
                "已停止位移，正在平滑衔接待机；保留当前位置" : "已取消移动并释放身体控制；保留当前位置",
                ArdyRoomExecutionState.Cancelled, ArdyRoomExecutionReason.CancelledByCaller);
        }

        private IEnumerator GuardedRun(IEnumerator routine, int token)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!Current(token) || !routine.MoveNext()) break;
                    current = routine.Current;
                }
                catch (Exception error)
                {
                    if (Current(token)) Fail(error.Message, error is ExecutionException known ? known.Reason : ArdyRoomExecutionReason.GenerationFailed);
                    break;
                }
                yield return current;
            }
            (routine as IDisposable)?.Dispose();
            if (token == revision) work = null;
        }

        private IEnumerator GenerateAndPlay(int token)
        {
            Stage = "capabilities";
            SetStatus("正在读取 ARDY 全身生成能力", ArdyRoomExecutionState.Preparing);
            pending = UnityWebRequest.Get(Url("/v1/locomotion/capabilities"));
            pending.timeout = 10;
            yield return pending.SendWebRequest();
            if (!Current(token)) yield break;
            string capsJson = ReadAndDisposeResponse();
            var caps = JsonUtility.FromJson<Capabilities>(capsJson);
            ValidateCapabilities(caps);
            sourceRootHeight = caps.sourceRootHeight;
        Replan:
            IsBusy = true;
            continuationLate = WaitingForSegment = false; nextSeamFrame = -1;
            PrepareAnchor();
            motionScale = (reference.Get(HumanBodyBones.Hips).positionInRoot.y - reference.groundY) * worldScale / sourceRootHeight;
            Stage = "pose-history";
            SetStatus("正在采集角色实际全身姿态（0.75 秒历史）", ArdyRoomExecutionState.Preparing);
            var frames = new List<ArdyRoomHistoryFrame>(16);
            latestObservedFrame = null;
            captureHistory = true;
            // Capture in LateUpdate after VRM (11000). WaitForEndOfFrame stalls when an Editor user
            // switches from Game view to Scene view, which is exactly this tool's target-picking flow.
            while (latestObservedFrame == null && Current(token)) yield return null;
            if (!Current(token)) yield break;
            CheckWaitingAnchor();
            float firstTime = latestObservedTime;
            float previousTime = firstTime;
            var previous = latestObservedFrame;
            frames.Add(previous);
            while (frames.Count < 16)
            {
                yield return null;
                if (!Current(token)) yield break;
                CheckWaitingAnchor();
                float now = latestObservedTime;
                if (now <= previousTime) continue;
                if (now - previousTime > .15f) throw new InvalidOperationException("画面帧间隔过大，无法采集连续的全身历史；请恢复流畅运行后重试。");
                var next = latestObservedFrame;
                while (frames.Count < 16 && firstTime + frames.Count / 20f <= now)
                {
                    float fraction = Mathf.InverseLerp(previousTime, now, firstTime + frames.Count / 20f);
                    frames.Add(Interpolate(previous, next, fraction));
                }
                previous = next; previousTime = now;
            }
            captureHistory = false;
            Vector3 initialForward = frames[frames.Count - 1].globalRotations[0] * Vector3.forward;
            initialForward.y = 0;
            if (initialForward.sqrMagnitude < .0001f) throw new InvalidOperationException("当前骨盆朝向不适合地面行走。");
            float initialHeading = Mathf.Atan2(initialForward.x, initialForward.z) * Mathf.Rad2Deg;
            Stage = "target-budget";
            if (movingTargetQueued) ApplyPreparedMovingTarget();
            // Keep short routes byte-for-byte identical. Longer routes share one continuous
            // native target timeline; each HTTP request stays within the advertised 200 frames.
            var timeline = MakeTargets(Route, 2400, initialHeading);
            segmentPlan = ArdyRoomSegmentPlan.Build(timeline, caps.maxFrames);
            SegmentCount = SegmentIndex + segmentPlan.Segments.Count;
            PlannedDurationSeconds = completedTimelineSeconds + timeline.Length / 20f;
            int baseSegment = SegmentIndex; timelineBaseSegment = baseSegment;
            ArdyLocomotionClip assembled = null;
            for (int segmentNumber = 0; segmentNumber < segmentPlan.Segments.Count; segmentNumber++)
            {
                if (ownedClip != null && movingTargetQueued)
                {
                    var changing = RetargetAndPlay(token);
                    while (Current(token) && changing.MoveNext()) yield return changing.Current;
                    yield break;
                }
                var segment = segmentPlan.Segments[segmentNumber];
                if (segmentNumber > 1)
                {
                    // Keep only one future segment prepared, leaving a full active segment's
                    // playback time for generation instead of building an unbounded queue.
                    while (Current(token) && !movingTargetQueued && player.TimeSeconds < segmentPlan.Segments[segmentNumber - 1].StartFrame / 20f)
                        yield return null;
                    if (movingTargetQueued)
                    {
                        var changing = RetargetAndPlay(token);
                        while (Current(token) && changing.MoveNext()) yield return changing.Current;
                        yield break;
                    }
                }
                if (!Current(token)) yield break;
                if (segmentNumber > 0)
                {
                    frames = new List<ArdyRoomHistoryFrame>(ArdyRoomSegmentPlan.GeneratedHistory(assembled, segment.StartFrame));
                    nextSeamFrame = segment.StartFrame;
                }
                requestId = Guid.NewGuid().ToString("N");
                int requestRevision = ++wireRevision;
                int generatedTargetRevision = MovingTargetRevision;
                var targets = segmentNumber == 0 ? segment.Targets :
                    ArdyRoomSegmentPlan.ContinueHeading(segment.Targets, frames[frames.Count - 1]);
                var request = new Request {
                    characterId = characterId, requestId = requestId, revision = requestRevision,
                    seed = 0, targets = targets, initialHistory = new History {
                        kind = segmentNumber == 0 ? "unity-full-body-projection-v1" : "audited-generated-full-body-v1",
                        frames = frames.ToArray() }
                };
                if (requestedTargetFacingTranslation) request.description = TargetFacingDescription();
                if (segmentNumber == 0)
                { Stage = "generation"; SetStatus("正在生成审核路径上的步行动作", ArdyRoomExecutionState.Generating); }
                LastRequestJson = JsonUtility.ToJson(request);
                var evidence = new SegmentEvidence { operationRevision = token, wireRevision = requestRevision,
                    targetRevision = generatedTargetRevision,
                    segment = baseSegment + segmentNumber + 1, totalSegments = SegmentCount, startFrame = segment.StartFrame,
                    requestFrames = targets.Length, requestId = requestId, historyKind = request.initialHistory.kind,
                    requestJson = LastRequestJson, submittedAtRealtime = Time.realtimeSinceStartup };
                RecordSegmentEvidence(evidence);
                pending = JsonRequest("/v1/locomotion/generate", LastRequestJson, 25);
                Debug.Log($"[ARDY room/segment] operation={token} wire={requestRevision} segment={baseSegment + segmentNumber + 1}/{SegmentCount} start={segment.StartFrame} frames={targets.Length} history={request.initialHistory.kind}", this);
                yield return pending.SendWebRequest();
                if (!Current(token)) yield break;
                if (segmentNumber > 0 && pending.result != UnityWebRequest.Result.Success && !continuationLate)
                {
                    continuationLate = WaitingForSegment = true;
                    Debug.LogWarning("[ARDY room/segment] continuation request failed; using audited stop tail: " + pending.error, this);
                }
                if (continuationLate)
                {
                    evidence.outcome = "discarded-after-seam"; evidence.completedAtRealtime = Time.realtimeSinceStartup;
                    pending?.Dispose(); pending = null;
                    SendCancellation(new Cancellation { characterId = characterId, requestId = requestId, revision = requestRevision });
                    requestId = null;
                    // The predicted seam is no longer valid. Play the previously audited
                    // deceleration tail, return to actual animation, and replan from there.
                    while (Current(token) && player.HasPoseOwnership) yield return null;
                    if (!Current(token)) yield break;
                    if (player.Clip != ownedClip || player.PlaybackRevision != ownedPlayerRevision ||
                        !player.ReturnedToAnimation || !player.CompletedNaturally)
                        throw new ExecutionException("续段等待期间身体控制已被取消或替换；不再重新启动移动。", ArdyRoomExecutionReason.OwnershipLost);
                    completedTimelineSeconds += player.TimeSeconds;
                    ownedClip = null;
                    if (++continuationRetry > 2) throw new InvalidOperationException("续段连续两次未能及时就绪；角色已停稳，请稍后重试。");
                    if (!navigation.TryPlan(AvatarGround(), TargetGround, out var remainingRoute, out string remainingReason))
                        throw new ExecutionException("续段停稳后路径不可达：" + remainingReason, ArdyRoomExecutionReason.NavigationRejected);
                    Route = remainingRoute;
                    Stage = "continuation-replan";
                    SetStatus("续段未及时就绪；已停稳，正在从当前姿态重新规划", ArdyRoomExecutionState.Preparing);
                    goto Replan;
                }
                string json = ReadAndDisposeResponse(); LastResponseJson = json;
                evidence.responseJson = json; evidence.completedAtRealtime = Time.realtimeSinceStartup;
                if (ownedClip != null && movingTargetQueued)
                {
                    evidence.outcome = "superseded-moving-target";
                    var changing = RetargetAndPlay(token);
                    while (Current(token) && changing.MoveNext()) yield return changing.Current;
                    yield break;
                }
                if (segmentNumber == 0 && generatedTargetRevision != MovingTargetRevision &&
                    !MovingCandidateAcceptable(TargetGround, WorldFacePoint, .75f) && !MovingCandidateProgresses(TargetGround))
                {
                    evidence.outcome = "superseded-before-play";
                    RetargetNeedsRestart = true; RetargetStatus = "requires-restart-before-play";
                    Stage = "target-outdated"; IsBusy = false;
                    SetStatus("准备期间目标已明显改变，旧候选未播放；可从当前姿态准备最新目标", ArdyRoomExecutionState.Cancelled, ArdyRoomExecutionReason.CancelledByCaller);
                    yield break;
                }
                if (segmentNumber == 0) CheckWaitingAnchor();
                var identity = JsonUtility.FromJson<ResponseIdentity>(json);
                if (identity == null || identity.characterId != characterId || identity.requestId != requestId || identity.revision != requestRevision)
                    throw new InvalidOperationException("生成结果身份不匹配；已丢弃，未移动角色。");
                var parsed = ArdyLocomotionClip.Parse(json);
                if (Mathf.Abs(parsed.sourceRootHeight - sourceRootHeight) > .0001f || parsed.frames.Length != targets.Length ||
                    Mathf.Abs(parsed.rotationClip.fps - 20) > .001f || parsed.sourceOrigin.sqrMagnitude > .000001f ||
                    Mathf.Abs(parsed.sourceHeadingDegrees) > .001f)
                    throw new InvalidOperationException("生成轨迹的尺度、时长或坐标原点不符合请求。");
                for (int i = 0; i < targets.Length; i++)
                    if ((parsed.targets[i].rootPosition - targets[i].rootPosition).sqrMagnitude > .000001f ||
                        Mathf.Abs(Mathf.DeltaAngle(parsed.targets[i].headingDegrees, targets[i].headingDegrees)) > .001f)
                        throw new InvalidOperationException("生成结果未对应已审核的目标路径。");
                Vector3 endpoint = anchorGround + anchorRotation * (targets[targets.Length - 1].rootPosition * motionScale);
                endpoint.y = anchorRootPosition.y;
                Vector3 trajectoryPrevious = segmentNumber == 0 ? anchorRootPosition : GeneratedRoot(assembled, segment.StartFrame - 1);
                evidence.auditedFrames = AuditGeneratedPath(parsed, trajectoryPrevious, endpoint, segment.Final).Length;
                if (requestedSignedInPlaceYaw.HasValue && segment.Final) CheckSignedInPlaceTurn(parsed, targets);
                if (requestedTargetFacingTranslation) CheckTargetFacingTranslation(parsed, targets);
                if (segmentNumber == 0) assembled = parsed;
                else
                {
                    assembled = ArdyRoomSegmentPlan.Append(assembled, segment.StartFrame, parsed,
                        out float seamRoot, out float seamJoint);
                    LastSeamRootStep = seamRoot; LastSeamMaximumJointStep = seamJoint;
                    evidence.seamSourceRootStep = seamRoot; evidence.seamMaximumJointStep = seamJoint;
                    Debug.Log($"[ARDY room/seam] operation={token} segment={baseSegment + segmentNumber + 1} sourceRootStep={seamRoot:F5} maximumJointStep={seamJoint:F3}", this);
                }
                int endFrame = assembled.frames.Length - 1;
                timelineFinal = segment.Final;
                if (segment.Final)
                {
                    // The complete new response and its join were just audited; every retained
                    // prefix was audited before playback. Do not reject an already traversed
                    // prefix because an obstacle appeared behind the moving character.
                    var auditedRoots = new Vector3[assembled.frames.Length];
                    for (int i = 0; i < auditedRoots.Length; i++) auditedRoots[i] = GeneratedRoot(assembled, i);
                    if (boundaryRecovery != null) endFrame = boundaryRecoveryEndFrame;
                    else
                    {
                        LastArrivalTiming = ArdyRoomArrivalTiming.Select(assembled, auditedRoots, TargetGround, EffectiveArrivalTolerance);
                        endFrame = LastArrivalTiming.PlaybackEndFrame;
                    }
                    Vector3 playbackEnd = auditedRoots[endFrame];
                    LastEndpointError = Vector2.Distance(new Vector2(playbackEnd.x, playbackEnd.z), new Vector2(TargetGround.x, TargetGround.z));
                    CheckFinalHeading(assembled, endFrame);
                }
                LastClip = assembled; Stage = "playback";
                if (segmentNumber == 0) player.Play(assembled, GuardStep, endFrame);
                else player.ExtendAuditedTimeline(assembled, segment.StartFrame, endFrame);
                player.SetPoseRecordingIdentity(token, requestRevision, null, 0);
                if (!player.HasPoseOwnership) throw new InvalidOperationException(player.Status);
                ownedClip = assembled; ownedPlayerRevision = player.PlaybackRevision;
                if (TryHandleLiveGuardStop()) yield break;
                PreparedSegmentCount = baseSegment + segmentNumber + 1;
                CommittedTargetRevision = generatedTargetRevision;
                evidence.outcome = segmentNumber == 0 ? "audited-playing" : "audited-appended";
                nextSeamFrame = -1;
                IsBusy = false;
                SetStatus("正在房间内移动；逐帧检查障碍物和地面边界", ArdyRoomExecutionState.Moving);
            }
            if (ownedClip != null && movingTargetQueued)
            {
                var changing = RetargetAndPlay(token);
                while (Current(token) && changing.MoveNext()) yield return changing.Current;
            }
        }

        private void PrepareAnchor()
        {
            anchorRootPosition = avatar.transform.position; anchorRotation = avatar.transform.rotation;
            anchorParent = avatar.transform.parent; worldScale = avatar.transform.lossyScale.y;
            var neutral = reference.Get(HumanBodyBones.Hips).positionInRoot;
            neutralHorizontal = new Vector3(neutral.x, 0, neutral.z) * worldScale;
            floorY = anchorRootPosition.y + reference.groundY * worldScale;
            anchorGround = anchorRootPosition + anchorRotation * neutralHorizontal;
            anchorGround.y = floorY;
            if (Mathf.Abs(floorY - Route.GroundY) > .035f)
                throw new InvalidOperationException("模型脚底与导航地面没有对齐；请先正确放置角色，移动不会将角色吸附到地板。");
        }

        private void CheckWaitingAnchor()
        {
            CheckBoundaryRecoveryWaitingOwner();
            string avatarReason = AvatarUnavailableReason();
            if (avatarReason != null) throw new InvalidOperationException(avatarReason);
            if (avatar.transform.parent != anchorParent ||
                Vector3.Distance(avatar.transform.position, anchorRootPosition) > .005f ||
                Quaternion.Angle(avatar.transform.rotation, anchorRotation) > .25f ||
                Mathf.Abs(avatar.transform.lossyScale.y - worldScale) > .0001f)
                throw new InvalidOperationException("等待生成期间角色位置、朝向、尺寸或归属已改变；已丢弃旧动作，请从当前位置重试。");
        }

        private ArdyRoomHistoryFrame CaptureFrame()
        {
            var hips = avatar.Humanoid.GetBoneTransform(HumanBodyBones.Hips);
            Vector3 sourceRoot = Quaternion.Inverse(anchorRotation) * (hips.position - anchorGround) / motionScale;
            if (sourceRoot.y < .25f || sourceRoot.y > 2.5f || !ArdyLocomotionClip.Finite(sourceRoot))
                throw new InvalidOperationException("当前实际骨盆距支撑面 " + (hips.position.y - floorY).ToString("F3") +
                    " 米，换算后的源骨架高度 " + sourceRoot.y.ToString("F3") + " 超出可用范围；请检查角色绑定和初始姿态。未修改姿态，也未发送生成请求。");
            return new ArdyRoomHistoryFrame {
                rootPosition = sourceRoot,
                globalRotations = player.CaptureFullBodyHistoryFrame(anchorRotation).globalRotations
            };
        }

        private static ArdyRoomHistoryFrame Interpolate(ArdyRoomHistoryFrame a, ArdyRoomHistoryFrame b, float alpha)
        {
            var q = new Quaternion[27];
            for (int i = 0; i < q.Length; i++) q[i] = Quaternion.Slerp(a.globalRotations[i], b.globalRotations[i], alpha);
            return new ArdyRoomHistoryFrame { rootPosition = Vector3.Lerp(a.rootPosition, b.rootPosition, alpha), globalRotations = q };
        }

        private ArdyLocomotionTarget[] MakeTargets(ArdyRoomRoute route, int maxFrames, float initialHeading)
        {
            if (boundaryRecovery != null) return MakeBoundaryRecoveryTargets(initialHeading);
            return ArdyRoomTargetBuilder.Build(route.Corners, route.Length, anchorRotation,
                anchorGround, motionScale, WalkingSpeed, maxFrames, initialHeading, FinalHeading(), requestedSignedInPlaceYaw,
                requestedTargetFacingTranslation ? WorldFacePoint : null);
        }

        private float? FinalHeading()
        {
            if (!WorldFacePoint.HasValue) return null;
            Vector3 direction = WorldFacePoint.Value - TargetGround;
            direction.y = 0;
            if (direction.sqrMagnitude < .0001f) return null;
            direction = Quaternion.Inverse(anchorRotation) * direction;
            return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        }

        private bool NeedsFacing(Vector3 ground, Vector3? facePoint)
        {
            if (!facePoint.HasValue) return false;
            Vector3 direction = facePoint.Value - ground;
            direction.y = 0;
            if (direction.sqrMagnitude < .0001f) return false;
            // Match the calibrated root heading that the full-body player actually uses;
            // the imported Humanoid's raw hips forward can differ from avatar forward.
            var rotations = player.CaptureFullBodyHistoryFrame(Quaternion.identity).globalRotations;
            Vector3 forward = rotations[0] * Vector3.forward;
            forward.y = 0;
            return forward.sqrMagnitude < .0001f || Vector3.Angle(forward, direction) > 10f;
        }

        private void CheckFinalHeading(ArdyLocomotionClip value, int frame)
        {
            CheckHeading(value, frame, FinalHeading());
        }

        private void CheckHeading(ArdyLocomotionClip value, int frame, float? desired)
        {
            if (!desired.HasValue) return;
            Vector3 forward = value.rotationClip.SampleDelta(0, frame / value.rotationClip.fps) * Vector3.forward;
            forward.y = 0;
            if (forward.sqrMagnitude < .0001f)
                throw new ExecutionException("生成末端朝向无法用于面向目标。", ArdyRoomExecutionReason.GeneratedPathRejected);
            float actual = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            LastHeadingError = Mathf.Abs(Mathf.DeltaAngle(actual, desired.Value));
            if (LastHeadingError > 10f)
                throw new ExecutionException("生成动作未可靠面向目标（偏差 " + LastHeadingError.ToString("F1") + " 度）；本次未播放。", ArdyRoomExecutionReason.GeneratedPathRejected);
        }

        private Vector3 GeneratedRoot(ArdyLocomotionClip value, int frame)
        {
            Vector3 source = value.frames[frame].rootPosition;
            source.y = 0;
            Vector3 pelvis = anchorGround + anchorRotation * (source * motionScale);
            Vector3 forward = value.rotationClip.SampleDelta(0, frame / value.rotationClip.fps) * Vector3.forward;
            forward.y = 0;
            if (forward.sqrMagnitude < .0001f) throw new InvalidOperationException("生成骨盆朝向垂直，无法用于地面行走。");
            Quaternion yaw = anchorRotation * Quaternion.LookRotation(forward.normalized, Vector3.up);
            Vector3 root = pelvis - yaw * neutralHorizontal;
            root.y = anchorRootPosition.y;
            return root;
        }

        private Vector3[] AuditGeneratedPath(ArdyLocomotionClip value) =>
            AuditGeneratedPath(value, anchorRootPosition, TargetGround, true);

        private Vector3[] AuditGeneratedPath(ArdyLocomotionClip value, Vector3 previous, Vector3 endpoint, bool final)
        {
            if (boundaryRecovery != null) return AuditBoundaryRecovery(value, previous);
            var roots = new Vector3[value.frames.Length];
            for (int i = 0; i < value.frames.Length; i++)
            {
                Vector3 next = GeneratedRoot(value, i);
                roots[i] = next;
                string reason = GuardStep(previous, next);
                if (reason != null) throw new ExecutionException("生成路径未通过房间审核，第 " + i + " 帧：" + reason, ArdyRoomExecutionReason.GeneratedPathRejected);
                previous = next;
            }
            LastEndpointError = Vector2.Distance(new Vector2(previous.x, previous.z), new Vector2(endpoint.x, endpoint.z));
            if (LastEndpointError > EffectiveArrivalTolerance)
                throw new ExecutionException("生成步态未可靠到达目标（偏差 " + LastEndpointError.ToString("F2") + " 米）；本次未播放。", ArdyRoomExecutionReason.GeneratedPathRejected);
            if (final) CheckFinalHeading(value, value.frames.Length - 1);
            return roots;
        }

        private static void CheckSignedInPlaceTurn(ArdyLocomotionClip value, ArdyLocomotionTarget[] expectedTargets)
        {
            // Checking only the last facing point would accept the opposite half-circle.
            // Targets carry unwrapped headings; actual pelvis rotations are unwrapped
            // frame by frame so a wrong direction or extra revolution cannot alias them.
            float previous = expectedTargets[0].headingDegrees;
            float actualTurn = 0f;
            for (int i = 0; i < value.frames.Length; i++)
            {
                Vector3 forward = value.rotationClip.SampleDelta(0, i / value.rotationClip.fps) * Vector3.forward;
                forward.y = 0;
                if (forward.sqrMagnitude < .0001f)
                    throw new ExecutionException("生成转身的骨盆朝向无法核验，未播放。", ArdyRoomExecutionReason.GeneratedPathRejected);
                float heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                actualTurn += Mathf.DeltaAngle(previous, heading); previous = heading;
            }
            float requestedTurn = expectedTargets[expectedTargets.Length - 1].headingDegrees - expectedTargets[0].headingDegrees;
            if (Mathf.Abs(actualTurn - requestedTurn) > 20f)
                throw new ExecutionException($"生成转身未遵循请求的左右方向（目标 {requestedTurn:F1}°，实际累计 {actualTurn:F1}°），未播放。",
                    ArdyRoomExecutionReason.GeneratedPathRejected);
        }

        private string TargetFacingDescription()
        {
            Vector3 toward = WorldFacePoint.Value - anchorRootPosition; toward.y = 0; toward.Normalize();
            Vector3 move = TargetGround - anchorRootPosition; move.y = 0; move.Normalize();
            float alignment = Vector3.Dot(toward, move);
            string gait = alignment < -.6f ? "walks backward with small natural steps" : alignment > .6f ?
                "walks forward with small natural steps" : Vector3.Dot(move, Vector3.Cross(Vector3.up, toward)) > 0 ?
                "sidesteps to the right with small natural steps" : "sidesteps to the left with small natural steps";
            return "A person " + gait + ", keeps the torso facing the indicated target throughout, and comes to a relaxed stop.";
        }

        private void CheckTargetFacingTranslation(ArdyLocomotionClip value, ArdyLocomotionTarget[] expectedTargets)
        {
            Vector3 axis = TargetGround - anchorRootPosition; axis.y = 0;
            float length = axis.magnitude; axis.Normalize();
            for (int i = 0; i < value.frames.Length; i++)
            {
                Vector3 forward = value.rotationClip.SampleDelta(0, i / value.rotationClip.fps) * Vector3.forward;
                forward.y = 0;
                if (forward.sqrMagnitude < .0001f || Mathf.Abs(Mathf.DeltaAngle(
                    Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg, expectedTargets[i].headingDegrees)) > 20f)
                    throw new ExecutionException("生成动作没有在沿路保持请求的目标朝向；本次未播放。", ArdyRoomExecutionReason.GeneratedPathRejected);
                Vector3 actualRoot = GeneratedRoot(value, i);
                Vector3 plannedTravel = anchorGround + anchorRotation * (expectedTargets[i].rootPosition * motionScale) - Route.Corners[0];
                plannedTravel.y = 0;
                // Separately legal heading and path errors can add up close to a
                // target. Check actual body-to-target bearing during translation,
                // without forbidding the preparatory stationary turn itself.
                if (plannedTravel.sqrMagnitude > .0001f)
                {
                    Vector3 actualTargetDirection = WorldFacePoint.Value - actualRoot; actualTargetDirection.y = 0;
                    Vector3 actualForward = anchorRotation * forward; actualForward.y = 0;
                    if (actualTargetDirection.sqrMagnitude < .0001f || Vector3.Angle(actualForward, actualTargetDirection) > 25f)
                        throw new ExecutionException("生成身体的实际位置和朝向没有持续面向观察目标；本次未播放。", ArdyRoomExecutionReason.GeneratedPathRejected);
                }
                Vector3 delta = actualRoot - anchorRootPosition; delta.y = 0;
                float progress = Vector3.Dot(delta, axis);
                if (progress < -.07f || progress > length + .07f ||
                    (delta - axis * Mathf.Clamp(progress, 0, length)).magnitude > .12f)
                    throw new ExecutionException("生成动作偏离本次明确的观察站位调整范围；本次未播放。", ArdyRoomExecutionReason.GeneratedPathRejected);
            }
        }

        private string GuardStep(Vector3 previous, Vector3 next)
            => GuardStepDetailed(previous, next, out _);

        private void LateUpdate()
        {
            ObserveWarmStreamPose();
            PrepareWarmStreamHistoryAfterPose();
            if (IsBusy || ownedClip != null)
            {
                string avatarReason = AvatarUnavailableReason();
                if (avatarReason != null) { Fail(avatarReason, ArdyRoomExecutionReason.AvatarUnavailable); return; }
            }
            if (captureHistory)
            {
                try
                {
                    CheckWaitingAnchor();
                    latestObservedFrame = CaptureFrame();
                    latestObservedTime = Time.realtimeSinceStartup;
                }
                catch (Exception error) { Fail(error.Message); return; }
            }
            if (boundaryRecoveryWaitingForRelease) return;
            if (TryHandleLiveGuardStop()) return;
            if (IsContinuousFollowActive) { TickContinuousStream(); return; }
            if (ownedClip == null || player == null) return;
            if (segmentPlan != null)
            {
                int active = 0;
                for (int i = 0; i < segmentPlan.Segments.Count; i++)
                    if (player.TimeSeconds >= segmentPlan.Segments[i].StartFrame / 20f) active = i + 1;
                SegmentIndex = timelineBaseSegment + Mathf.Clamp(active, 0, Mathf.Max(1, PreparedSegmentCount - timelineBaseSegment));
            }
            if (nextSeamFrame >= 0 && player.TimeSeconds >= (nextSeamFrame - 2) / 20f && !continuationLate)
            {
                continuationLate = WaitingForSegment = true; pending?.Abort();
                Debug.LogWarning($"[ARDY room/segment] operation={OperationRevision} missed predicted seam; playing audited stop tail and invalidating continuation", this);
            }
            if (player.Clip == ownedClip && player.PlaybackRevision == ownedPlayerRevision && player.ReturnedToAnimation && !player.HasPoseOwnership)
            {
                if (ExecutionState == ArdyRoomExecutionState.Cancelled || ExecutionState == ArdyRoomExecutionState.Failed ||
                    ExecutionState == ArdyRoomExecutionState.Blocked)
                { ownedClip = null; return; }
                if (retargetSettling)
                {
                    retargetSettling = false; ownedClip = null; IsBusy = false;
                    RetargetNeedsRestart = true; RetargetStatus = "requires-restart-after-settling";
                    Stage = "retarget-stopped";
                    SetStatus("已平滑停稳；需要从当前位置准备最新跟随目标", ArdyRoomExecutionState.Cancelled, ArdyRoomExecutionReason.CancelledByCaller);
                    return;
                }
                if (continuationLate) return;
                ownedClip = null;
                if (boundaryRecovery != null)
                {
                    if (!navigation.TryValidatePlanStart(AvatarGround(), out _, out string recoveryReason, out _))
                    { Fail("边缘恢复结束，但实际起点仍不能重新规划：" + recoveryReason, ArdyRoomExecutionReason.NavigationRejected); return; }
                    boundaryRecovery = null;
                    Stage = "navigation-recovered";
                    SetStatus("已通过有限步态回到可规划范围；身体控制已平滑释放", ArdyRoomExecutionState.Arrived, ArdyRoomExecutionReason.Completed);
                    return;
                }
                Stage = "completed";
                SetStatus("已到达并停止 · 目标偏差 " + LastEndpointError.ToString("F2") + " 米；已平滑交回当前动画", ArdyRoomExecutionState.Arrived, ArdyRoomExecutionReason.Completed);
                return;
            }
            if (player.Clip != ownedClip || player.PlaybackRevision != ownedPlayerRevision || !player.HasPoseOwnership)
            {
                string reason = player.LastBlockedReason;
                CancelCore(false);
                SetStatus(string.IsNullOrEmpty(reason) ? "身体控制已被释放或替换；本次房间移动结束" : "地形守卫已停止移动 · " + reason,
                    string.IsNullOrEmpty(reason) ? ArdyRoomExecutionState.Cancelled : ArdyRoomExecutionState.Blocked,
                    string.IsNullOrEmpty(reason) ? ArdyRoomExecutionReason.OwnershipLost : ArdyRoomExecutionReason.TerrainBlocked);
                return;
            }
            if (player.CompletedNaturally && !player.IsReturningToAnimation)
            {
                try
                {
                    player.ReturnToAnimation(ReturnToIdleSeconds);
                    Stage = continuationLate ? "continuation-settling" : "returning";
                    SetStatus(continuationLate ? "续段未及时就绪，正在停稳后重新规划" : "已到达，正在平滑衔接当前待机动画",
                        continuationLate ? ArdyRoomExecutionState.Moving : ArdyRoomExecutionState.Returning);
                }
                catch (Exception error) { Fail(error.Message); }
            }
        }

        private bool TryHandleLiveGuardStop()
        {
            if (ownedClip == null || player == null || player.Clip != ownedClip || !player.HasPoseOwnership ||
                !player.IsStoppingSmoothly || string.IsNullOrEmpty(player.LastBlockedReason) ||
                player.LastGuardBlockedPlaybackRevision < 0 || ExecutionState == ArdyRoomExecutionState.Blocked ||
                ExecutionState == ArdyRoomExecutionState.Failed || ExecutionState == ArdyRoomExecutionState.Cancelled)
                return false;
            // Usually the guard just advanced the previously owned revision by one. A
            // rejection of Play's first sample is already assigned that stop revision.
            // Neither case permits claiming a foreign/replaced player or unrelated return.
            bool justStopped = player.LastGuardBlockedPlaybackRevision == ownedPlayerRevision &&
                player.PlaybackRevision == ownedPlayerRevision + 1;
            bool firstSampleStopped = player.PlaybackRevision == ownedPlayerRevision &&
                player.LastGuardBlockedPlaybackRevision == ownedPlayerRevision - 1;
            if (!justStopped && !firstSampleStopped) return false;
            ownedPlayerRevision = player.PlaybackRevision;
            Stage = "live-guard-blocked";
            Fail(player.LastBlockedReason, ArdyRoomExecutionReason.GeneratedPathRejected);
            return true;
        }

        private Vector3 AvatarGround()
        {
            Vector3 point = avatar.transform.position;
            point.y += reference.groundY * avatar.transform.lossyScale.y;
            return point;
        }

        private void RecordSegmentEvidence(SegmentEvidence evidence)
        {
            if (evidence == null) throw new ArgumentNullException(nameof(evidence));
            // Diagnostics must stay bounded during a continuous follow task. This queue is
            // independent of execution counters and always retains the active/newest request.
            if (segmentEvidence.Count >= 64)
            {
                segmentEvidence.RemoveAt(0);
                DiscardedSegmentEvidenceCount++;
            }
            segmentEvidence.Add(evidence);
        }

        private string AvatarUnavailableReason()
        {
            if (avatar == null) return "角色对象已被移除；请重新绑定场景角色。";
            if (!avatar.gameObject.scene.IsValid() || !avatar.gameObject.scene.isLoaded) return "角色所在场景已卸载；请重新绑定场景角色。";
            if (!avatar.gameObject.activeInHierarchy) return "角色对象或其父节点已停用；已停止房间移动。";
            // Disabled full VRM updates are a supported direct-Humanoid mode in the chat scene.
            // Preserve the bound mode, but never continue across a change of runtime ownership.
            if (avatar.enabled != boundVrmEnabled) return "VRM 更新模式在绑定后发生变化；请释放本窗口控制后重新绑定。";
            return null;
        }

        private bool Current(int token) => token == revision && avatar != null && isActiveAndEnabled;
        private void RejectForeignPlayerOwner()
        {
            if (player != null && player.HasPoseOwnership && (ownedClip == null || player.Clip != ownedClip || player.PlaybackRevision != ownedPlayerRevision))
                throw new InvalidOperationException("此全身播放器已被其他调用者接管；请先释放该控制再移动或重新绑定。");
        }
        private void SetStatus(string value, ArdyRoomExecutionState state, ArdyRoomExecutionReason reason = ArdyRoomExecutionReason.None)
        {
            ExecutionState = state; ExecutionReason = reason; Status = value;
            StateChanged?.Invoke(value);
        }
        private void Fail(string reason, ArdyRoomExecutionReason code = ArdyRoomExecutionReason.GenerationFailed)
        {
            // Frame-driven guard/ownership failures do not pass through the coroutine's
            // exception handler. Record the new cause before cancellation, retaining an
            // already recorded, more specific cause such as exhausted motion supply.
            if (IsContinuousFollowActive && streamLastFailureReason != reason)
                RecordStreamEvent("failed", reason, StreamFailureCategory(new ExecutionException(reason, code)));
            CancelCore(false);
            SetStatus("房间移动未完成 · " + reason,
                code == ArdyRoomExecutionReason.GeneratedPathRejected ? ArdyRoomExecutionState.Blocked : ArdyRoomExecutionState.Failed, code);
            Debug.LogWarning("[ARDY room] " + reason, this);
        }

        private string Url(string suffix)
        {
            if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new InvalidOperationException("ARDY 服务地址需要有效的 HTTP 地址。");
            return Endpoint.TrimEnd('/') + suffix;
        }

        private UnityWebRequest JsonRequest(string suffix, string json, int timeout)
        {
            var request = new UnityWebRequest(Url(suffix), "POST") {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(), timeout = timeout
            };
            request.SetRequestHeader("Content-Type", "application/json");
            return request;
        }

        private string ReadAndDisposeResponse()
        {
            var request = pending; pending = null;
            try
            {
                if (request.result != UnityWebRequest.Result.Success)
                {
                    LastErrorResponseJson = request.downloadHandler?.text;
                    lastServiceError = ReadServiceErrorObservation(LastErrorResponseJson, request.responseCode,
                        requestId, OperationRevision);
                    Debug.LogWarning("[ARDY room/service-error] " + JsonUtility.ToJson(lastServiceError), this);
                    throw new ServiceRequestException("ARDY 服务请求失败（" + request.responseCode + "）：" + request.error, LastErrorResponseJson);
                }
                if (request.downloadedBytes > 8 * 1024 * 1024) throw new InvalidOperationException("ARDY 响应超出本阶段大小限制。");
                return request.downloadHandler.text;
            }
            finally { request.Dispose(); }
        }

        private void SendCancellation(Cancellation value)
        {
            UnityWebRequest request = null;
            try
            {
                request = JsonRequest("/v1/locomotion/cancel", JsonUtility.ToJson(value), 5);
                var owned = request;
                request.SendWebRequest().completed += _ => owned.Dispose();
            }
            catch (Exception) { request?.Dispose(); }
        }

        private static void ValidateCapabilities(Capabilities caps)
        {
            if (caps == null || !caps.ready || caps.fps != 20 || caps.historyFrames != 16 || caps.maxFrames < 40 || caps.maxFrames > 200 ||
                !ArdyLocomotionClip.Finite(caps.sourceRootHeight) || caps.sourceRootHeight < .1f || caps.sourceRootHeight > 3 ||
                caps.coordinateSystem != "unity-lh-y-up-z-forward" || caps.jointNames == null || caps.jointNames.Length != 27)
                throw new InvalidOperationException("ARDY 全身生成服务尚未就绪或协议不兼容。");
            for (int i = 0; i < caps.jointNames.Length; i++)
                if (caps.jointNames[i] != ArdyLiveProtocol.JointNames[i]) throw new InvalidOperationException("ARDY Core27 关节顺序不匹配。");
        }

        private void ReleaseOnLifecycleExit()
        {
            Cancel();
            if (ownedClip != null && player != null && player.Clip == ownedClip && player.PlaybackRevision == ownedPlayerRevision)
                player.Stop();
            ownedClip = null;
        }
        private void OnDisable() => ReleaseOnLifecycleExit();
        private void OnDestroy() => ReleaseOnLifecycleExit();
    }
}
