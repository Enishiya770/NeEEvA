using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace NeEEvA.Motion
{
    public sealed partial class ArdyRoomLocomotionController
    {
        public Vector3 RequestedTargetGround { get; private set; }
        public int MovingTargetRevision { get; private set; }
        public int CommittedTargetRevision { get; private set; }
        public string RetargetStatus { get; private set; } = "none";
        public bool RetargetNeedsRestart { get; private set; }
        private Vector3? requestedFacePoint;
        private bool movingTargetQueued, retargetWorkActive, retargetSettling;
        private float targetUpdatedRealtime;

        private void ResetMovingTarget(Vector3 ground, Vector3? face)
        {
            RequestedTargetGround = ground; requestedFacePoint = face;
            MovingTargetRevision = CommittedTargetRevision = 0;
            movingTargetQueued = retargetWorkActive = retargetSettling = RetargetNeedsRestart = false;
            RetargetStatus = "none";
        }

        /// <summary>
        /// Queue/coalesce the latest desired target without releasing the playing body or
        /// incrementing OperationRevision. False leaves the existing audited trajectory intact:
        /// no active operation, unavailable owner, returning/settling, invalid geometry, or no
        /// future seam with sufficient generation lead. True is preparation, never arrival.
        /// Preparing keeps its actual-history collection; in-flight results tolerate bounded
        /// target drift only after the current user/terrain guard accepts the complete candidate.
        /// </summary>
        public bool TryUpdateMovingTarget(Vector3 ground, Vector3? facePoint, out string reason)
        {
            reason = null;
            if (!Current(OperationRevision) || avatar == null || navigation == null ||
                (!IsBusy && !IsMoving)) { reason = "没有可以滚动更新的移动任务"; return false; }
            if (!ArdyLocomotionClip.Finite(ground) || (facePoint.HasValue && !ArdyLocomotionClip.Finite(facePoint.Value)))
            { reason = "目标不是有效世界坐标"; return false; }
            if (RetargetNeedsRestart || retargetSettling || (player != null && player.IsReturningToAnimation))
            { reason = "正在停稳或交回动画；完成后再从实际位置规划"; return false; }
            if (AvatarUnavailableReason() != null) { reason = AvatarUnavailableReason(); return false; }
            if (Vector3.Distance(ground, RequestedTargetGround) <= .15f && SameFace(facePoint, requestedFacePoint, .20f))
            {
                RetargetStatus = movingTargetQueued ? "coalesced-pending" : "coalesced-current";
                if (movingTargetQueued && ownedClip != null && work == null && !retargetWorkActive)
                    work = StartCoroutine(GuardedRun(RetargetAndPlay(OperationRevision), OperationRevision));
                return true;
            }
            Vector3 start = AvatarGround();
            if (ownedClip != null)
            {
                if (!OwnsPlayingTimeline()) { reason = "当前身体所有权不适合滚动续播"; return false; }
                if (!TryChooseMovingSeam(out int seam, out reason)) return false;
                start = GeneratedRoot(ownedClip, seam - 1); start.y = floorY;
            }
            if (!navigation.TryPlan(start, ground, out _, out reason)) return false;
            RequestedTargetGround = ground; requestedFacePoint = facePoint;
            MovingTargetRevision++; movingTargetQueued = true; targetUpdatedRealtime = Time.realtimeSinceStartup;
            RetargetStatus = ownedClip == null ? "queued-during-preparation" : "queued-future-tail";
            Debug.Log($"[ARDY room/target] operation={OperationRevision} targetRevision={MovingTargetRevision} status={RetargetStatus} goal={ground}", this);
            // The existing preparing/prefetch coroutine consumes the queued target itself.
            // A fully prepared single/last segment has no worker, so start a future-tail worker.
            if (ownedClip != null && work == null && !retargetWorkActive)
                work = StartCoroutine(GuardedRun(RetargetAndPlay(OperationRevision), OperationRevision));
            return true;
        }

        private bool OwnsPlayingTimeline() => player != null && ownedClip != null &&
            player.Clip == ownedClip && player.PlaybackRevision == ownedPlayerRevision &&
            player.HasPoseOwnership && player.IsPlaying && !player.IsReturningToAnimation;

        private bool TryChooseMovingSeam(out int seam, out string reason)
        {
            seam = -1; reason = null;
            if (!OwnsPlayingTimeline()) { reason = "已无可延续的已审核播放时间轴"; return false; }
            float lead = 1.5f;
            if (segmentEvidence.Count > 0)
            {
                var last = segmentEvidence[segmentEvidence.Count - 1];
                if (last.completedAtRealtime > last.submittedAtRealtime)
                    lead = Mathf.Clamp(last.completedAtRealtime - last.submittedAtRealtime + .35f, 1.5f, 3.5f);
            }
            seam = Mathf.Max(16, Mathf.CeilToInt((player.TimeSeconds + lead) * 20));
            if (seam >= ownedClip.frames.Length || seam / 20f + .10f >= player.PlaybackEndSeconds)
            { reason = "当前动作的安全未来窗口不足；保留已审核收尾，停稳后使用最新目标"; return false; }
            return true;
        }

        private static bool SameFace(Vector3? a, Vector3? b, float tolerance) =>
            a.HasValue == b.HasValue && (!a.HasValue || Vector3.Distance(a.Value, b.Value) <= tolerance);

        private bool MovingCandidateAcceptable(Vector3 candidate, Vector3? face, float tolerance) =>
            Vector3.Distance(candidate, RequestedTargetGround) <= tolerance && SameFace(face, requestedFacePoint, tolerance) &&
            Vector3.Distance(candidate, RequestedTargetGround) <= Vector3.Distance(AvatarGround(), RequestedTargetGround) + .1f;

        private bool MovingCandidateProgresses(Vector3 candidate) =>
            Vector3.Distance(candidate, RequestedTargetGround) <= 1.5f &&
            Vector3.Distance(candidate, RequestedTargetGround) + .15f < Vector3.Distance(AvatarGround(), RequestedTargetGround);

        private bool CanTemporarilyContinueCandidate(Vector3 goal, Vector3? face) =>
            MovingCandidateAcceptable(goal, face, .35f) || MovingCandidateProgresses(goal);

        private void ApplyPreparedMovingTarget()
        {
            if (!navigation.TryPlan(AvatarGround(), RequestedTargetGround, out var latest, out string reason))
                throw new ExecutionException("最新移动目标不可达：" + reason, ArdyRoomExecutionReason.NavigationRejected);
            Route = latest; TargetGround = latest.Corners[latest.Corners.Length - 1]; WorldFacePoint = requestedFacePoint;
            movingTargetQueued = false; RetargetStatus = "prepared-latest-target";
        }

        private void SettleForMovingTarget(string reason)
        {
            movingTargetQueued = false; nextSeamFrame = -1;
            continuationLate = WaitingForSegment = false; segmentPlan = null;
            RetargetStatus = "settling-for-latest-target";
            if (ownedClip != null && player != null && player.Clip == ownedClip && player.PlaybackRevision == ownedPlayerRevision &&
                player.RequestSmoothStop(ReturnToIdleSeconds))
            {
                ownedPlayerRevision = player.PlaybackRevision; retargetSettling = true;
                Stage = "retarget-settling";
                SetStatus("正在平滑停稳；" + reason, ArdyRoomExecutionState.Moving);
            }
            else
            {
                ownedClip = null; RetargetNeedsRestart = true;
                RetargetStatus = "requires-restart"; Stage = "retarget-stopped";
                SetStatus("需要从实际位置准备最新目标；" + reason, ArdyRoomExecutionState.Cancelled, ArdyRoomExecutionReason.CancelledByCaller);
            }
        }

        private IEnumerator RetargetAndPlay(int token)
        {
            retargetWorkActive = true;
            // A superseded fixed-route prefetch may have left an earlier deadline behind.
            // This worker owns its own candidate seam; that old timer cannot abort its HTTP.
            nextSeamFrame = -1; continuationLate = WaitingForSegment = false;
            int superseded = 0;
            try
            {
            LatestTarget:
                if (!Current(token)) yield break;
                // Brief coalescing is bounded. A steadily moving user cannot postpone the
                // first request indefinitely by extending a quiet-period timer every frame.
                float coalesceUntil = Time.realtimeSinceStartup + .10f;
                while (Current(token) && Time.realtimeSinceStartup < coalesceUntil) yield return null;
                if (!Current(token)) yield break;
                int targetVersion = MovingTargetRevision;
                Vector3 goal = RequestedTargetGround; Vector3? face = requestedFacePoint;
                if (!TryChooseMovingSeam(out int initialSeam, out string reason))
                { SettleForMovingTarget(reason); yield break; }
                Vector3 start = GeneratedRoot(ownedClip, initialSeam - 1); start.y = floorY;
                if (!navigation.TryPlan(start, goal, out var route, out reason))
                { SettleForMovingTarget("未来接缝至目标的路径被拒绝：" + reason); yield break; }
                ArdyLocomotionTarget[] timeline = null;
                ArdyRoomSegmentPlan planned = null;
                try
                {
                    var history = ArdyRoomSegmentPlan.GeneratedHistory(ownedClip, initialSeam);
                    timeline = ArdyRoomSegmentPlan.BuildMovingTimeline(route.Corners, route.Length, anchorRotation, anchorGround,
                        motionScale, WalkingSpeed, history, FinalHeadingFor(goal, face));
                    planned = ArdyRoomSegmentPlan.Build(timeline, 200);
                    planned.OffsetFrames(initialSeam);
                }
                catch (Exception e) { reason = e.Message; }
                if (planned == null) { SettleForMovingTarget("无法构造连续新路线：" + reason); yield break; }
                int baseSegment = SegmentIndex;
                for (int index = 0; index < planned.Segments.Count; index++)
                {
                    var segment = planned.Segments[index];
                    if (index > 0)
                    {
                        while (Current(token) && MovingTargetRevision == targetVersion &&
                            player.TimeSeconds < planned.Segments[index - 1].StartFrame / 20f) yield return null;
                    }
                    if (!Current(token)) yield break;
                    if (MovingTargetRevision != targetVersion && !CanTemporarilyContinueCandidate(goal, face))
                        goto LatestTarget;
                    if (!OwnsPlayingTimeline() || player.TimeSeconds >= (segment.StartFrame - 2) / 20f)
                    { SettleForMovingTarget("未能在未来接缝前准备好新轨迹"); yield break; }
                    var history = ArdyRoomSegmentPlan.GeneratedHistory(ownedClip, segment.StartFrame);
                    var targets = ArdyRoomSegmentPlan.ContinueHeading(segment.Targets, history[15]);
                    requestId = Guid.NewGuid().ToString("N");
                    int requestRevision = ++wireRevision;
                    var request = new Request { characterId = characterId, requestId = requestId, revision = requestRevision,
                        targets = targets, seed = 0, initialHistory = new History { kind = "audited-generated-full-body-v1", frames = history } };
                    LastRequestJson = JsonUtility.ToJson(request);
                    var evidence = new SegmentEvidence { operationRevision = token, wireRevision = requestRevision,
                        targetRevision = targetVersion, segment = baseSegment + index + 1, totalSegments = baseSegment + planned.Segments.Count,
                        startFrame = segment.StartFrame, requestFrames = targets.Length, requestId = requestId,
                        historyKind = request.initialHistory.kind, requestJson = LastRequestJson, submittedAtRealtime = Time.realtimeSinceStartup };
                    RecordSegmentEvidence(evidence);
                    RetargetStatus = "generating-future-tail";
                    pending = JsonRequest("/v1/locomotion/generate", LastRequestJson, 25);
                    Debug.Log($"[ARDY room/retarget] operation={token} targetRevision={targetVersion} wire={requestRevision} seam={segment.StartFrame} frames={targets.Length}", this);
                    var asyncRequest = pending.SendWebRequest();
                    while (Current(token) && !asyncRequest.isDone && OwnsPlayingTimeline() &&
                        player.TimeSeconds < (segment.StartFrame - 2) / 20f) yield return null;
                    if (!Current(token)) yield break;
                    if (!OwnsPlayingTimeline() || player.TimeSeconds >= (segment.StartFrame - 2) / 20f)
                    {
                        pending.Abort(); pending.Dispose(); pending = null;
                        SendCancellation(new Cancellation { characterId = characterId, requestId = requestId, revision = requestRevision });
                        evidence.outcome = "discarded-retarget-deadline"; evidence.completedAtRealtime = Time.realtimeSinceStartup;
                        SettleForMovingTarget("新轨迹未在安全接缝前就绪"); yield break;
                    }
                    if (MovingTargetRevision != targetVersion && !CanTemporarilyContinueCandidate(goal, face))
                    {
                        evidence.outcome = "superseded-moving-target"; evidence.completedAtRealtime = Time.realtimeSinceStartup;
                        pending.Dispose(); pending = null;
                        if (++superseded >= 3) { SettleForMovingTarget("目标持续变化，准备从最新位置继续"); yield break; }
                        goto LatestTarget;
                    }
                    string json = null;
                    try { json = ReadAndDisposeResponse(); }
                    catch (Exception e) { reason = e.Message; }
                    if (json == null) { evidence.outcome = "retarget-request-failed"; SettleForMovingTarget(reason); yield break; }
                    LastResponseJson = evidence.responseJson = json; evidence.completedAtRealtime = Time.realtimeSinceStartup;
                    ArdyLocomotionClip assembled = null;
                    int endFrame = 0;
                    ArdyRoomArrivalTiming arrival = null;
                    try
                    {
                        var identity = JsonUtility.FromJson<ResponseIdentity>(json);
                        if (identity == null || identity.characterId != characterId || identity.requestId != requestId || identity.revision != requestRevision)
                            throw new InvalidOperationException("滚动生成结果身份不匹配");
                        var parsed = ArdyLocomotionClip.Parse(json);
                        if (parsed.frames.Length != targets.Length || Mathf.Abs(parsed.sourceRootHeight - sourceRootHeight) > .0001f ||
                            parsed.sourceOrigin.sqrMagnitude > .000001f || Mathf.Abs(parsed.sourceHeadingDegrees) > .001f)
                            throw new InvalidOperationException("滚动轨迹尺度或源坐标发生变化");
                        for (int frame = 0; frame < targets.Length; frame++)
                            if ((parsed.targets[frame].rootPosition - targets[frame].rootPosition).sqrMagnitude > .000001f ||
                                Mathf.Abs(Mathf.DeltaAngle(parsed.targets[frame].headingDegrees, targets[frame].headingDegrees)) > .001f)
                                throw new InvalidOperationException("滚动生成没有对应当前审核目标");
                        Vector3 endpoint = anchorGround + anchorRotation * (targets[targets.Length - 1].rootPosition * motionScale);
                        endpoint.y = anchorRootPosition.y;
                        AuditGeneratedPath(parsed, GeneratedRoot(ownedClip, segment.StartFrame - 1), endpoint, false);
                        if (segment.Final) CheckHeading(parsed, parsed.frames.Length - 1, FinalHeadingFor(goal, face));
                        assembled = ArdyRoomSegmentPlan.Append(ownedClip, segment.StartFrame, parsed, out float step, out float joints);
                        evidence.seamSourceRootStep = LastSeamRootStep = step;
                        evidence.seamMaximumJointStep = LastSeamMaximumJointStep = joints;
                        endFrame = assembled.frames.Length - 1;
                        if (segment.Final)
                        {
                            var roots = new Vector3[assembled.frames.Length];
                            for (int frame = 0; frame < roots.Length; frame++) roots[frame] = GeneratedRoot(assembled, frame);
                            arrival = ArdyRoomArrivalTiming.Select(assembled, roots, goal, ArrivalTolerance);
                            endFrame = arrival.PlaybackEndFrame;
                            CheckHeading(assembled, endFrame, FinalHeadingFor(goal, face));
                        }
                        player.ExtendAuditedTimeline(assembled, segment.StartFrame, endFrame);
                        player.SetPoseRecordingIdentity(token, requestRevision, null, 0);
                    }
                    catch (Exception e) { reason = e.Message; assembled = null; }
                    if (assembled == null)
                    { evidence.outcome = "retarget-audit-rejected"; SettleForMovingTarget(reason); yield break; }
                    ownedClip = LastClip = assembled; ownedPlayerRevision = player.PlaybackRevision;
                    TargetGround = goal; WorldFacePoint = face; Route = route;
                    segmentPlan = planned; timelineBaseSegment = baseSegment;
                    PreparedSegmentCount = baseSegment + index + 1; SegmentCount = baseSegment + planned.Segments.Count;
                    PlannedDurationSeconds = completedTimelineSeconds + (initialSeam + timeline.Length) / 20f;
                    LastArrivalTiming = arrival; timelineFinal = segment.Final;
                    nextSeamFrame = -1; continuationLate = WaitingForSegment = false;
                    CommittedTargetRevision = targetVersion;
                    movingTargetQueued = targetVersion != MovingTargetRevision;
                    RetargetStatus = movingTargetQueued ? "committed-progress-pending-latest" : "committed";
                    evidence.outcome = "audited-retarget-appended";
                    Stage = "playback"; IsBusy = false;
                    SetStatus("正在连续跟随；已替换尚未播放的后续轨迹", ArdyRoomExecutionState.Moving);
                    // A useful, freshly collision-audited older candidate can maintain progress
                    // while the user walks. Its actual revision remains visible and the latest
                    // goal immediately gets a new future-tail request instead of false closure.
                    if (movingTargetQueued) goto LatestTarget;
                }
                if (movingTargetQueued) goto LatestTarget;
            }
            finally { retargetWorkActive = false; }
        }

        private float? FinalHeadingFor(Vector3 goal, Vector3? face)
        {
            if (!face.HasValue) return null;
            Vector3 direction = face.Value - goal; direction.y = 0;
            if (direction.sqrMagnitude < .0001f) return null;
            direction = Quaternion.Inverse(anchorRotation) * direction;
            return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        }
    }
}
