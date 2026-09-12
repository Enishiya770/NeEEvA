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
    public sealed class ArdyRoomLocomotionController : MonoBehaviour
    {
        [Serializable] private sealed class Capabilities
        {
            public bool ready;
            public float fps, sourceRootHeight;
            public int historyFrames, maxFrames;
            public string coordinateSystem;
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
        private int revision;
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
            importedReference.Validate();
            avatar = target; reference = importedReference; navigation = nav;
            characterId = "unity-room-" + target.GetInstanceID() + "-" + Guid.NewGuid().ToString("N");
            player = GetComponent<ArdyLocomotionPlayer>();
            if (player == null) player = gameObject.AddComponent<ArdyLocomotionPlayer>();
            player.DiscardPreviewAnchor();
            player.Bind(target, importedReference);
            boundVrmEnabled = target.enabled;
            SetStatus("房间移动已绑定；选择同一地面上的目标", ArdyRoomExecutionState.Idle);
        }

        public void MoveTo(Vector3 worldGroundTarget) => MoveTo(worldGroundTarget, null);

        public void MoveTo(Vector3 worldGroundTarget, Vector3? worldFacePoint)
        {
            if (!Application.isPlaying || !isActiveAndEnabled || avatar == null || navigation == null)
                throw new InvalidOperationException("先在 Play 模式绑定角色与房间导航。");
            string avatarReason = AvatarUnavailableReason();
            if (avatarReason != null) throw new InvalidOperationException(avatarReason);
            if (!ArdyLocomotionClip.Finite(worldGroundTarget)) throw new ArgumentException("目标必须是有限的世界坐标。");
            if (worldFacePoint.HasValue && !ArdyLocomotionClip.Finite(worldFacePoint.Value)) throw new ArgumentException("面向点必须是有限的世界坐标。");
            RejectForeignPlayerOwner();
            Cancel();
            int token = ++revision;
            OperationRevision = token;
            WorldFacePoint = worldFacePoint;
            Route = null;
            TargetGround = worldGroundTarget;
            LastErrorResponseJson = null;
            LastRequestJson = LastResponseJson = null;
            LastArrivalTiming = null;
            LastEndpointError = LastHeadingError = 0;
            Stage = "navigation";
            SetStatus("正在审核房间移动目标", ArdyRoomExecutionState.Preparing);
            if (!navigation.TryProjectGround(AvatarGround(), out Vector3 startGround, out string reason) ||
                !navigation.TryPlan(startGround, worldGroundTarget, out var route, out reason))
            { SetStatus("目标不可达 · " + reason, ArdyRoomExecutionState.Blocked, ArdyRoomExecutionReason.NavigationRejected); return; }
            if (route.Length < .08f)
            {
                if (!NeedsFacing(startGround, worldFacePoint))
                { Route = route; TargetGround = startGround; SetStatus("角色已经位于目标附近", ArdyRoomExecutionState.Arrived, ArdyRoomExecutionReason.AlreadyAtTarget); return; }
                // The ground and footprint have been checked above. Turn using native pelvis
                // constraints at the current position, rather than snapping the avatar root.
                route = new ArdyRoomRoute { Corners = new[] { startGround }, GroundY = startGround.y, Length = 0 };
            }
            Route = route; TargetGround = route.Corners[route.Corners.Length - 1];
            requestId = Guid.NewGuid().ToString("N");
            IsBusy = true;
            work = StartCoroutine(GuardedRun(GenerateAndPlay(token), token));
        }

        public void Cancel() => CancelCore(true);

        private void CancelCore(bool notify)
        {
            int cancelledRevision = revision;
            string cancelledRequest = requestId;
            revision++;
            if (pending != null) { pending.Abort(); pending.Dispose(); pending = null; }
            if (work != null) { StopCoroutine(work); work = null; }
            if (ownedClip != null && player != null && player.Clip == ownedClip && player.PlaybackRevision == ownedPlayerRevision)
                player.Stop();
            ownedClip = null; IsBusy = false; captureHistory = false;
            if (!string.IsNullOrEmpty(characterId) && !string.IsNullOrEmpty(cancelledRequest))
                SendCancellation(new Cancellation { characterId = characterId, requestId = cancelledRequest, revision = cancelledRevision });
            requestId = null;
            if (notify && avatar != null) SetStatus("已取消移动并释放身体控制；保留当前位置", ArdyRoomExecutionState.Cancelled, ArdyRoomExecutionReason.CancelledByCaller);
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
            var targets = MakeTargets(Route, caps.maxFrames, initialHeading);
            var request = new Request {
                characterId = characterId, requestId = requestId, revision = token,
                seed = 0, targets = targets, initialHistory = new History { frames = frames.ToArray() }
            };
            Stage = "generation";
            SetStatus("正在生成审核路径上的完整步行动作", ArdyRoomExecutionState.Generating);
            LastRequestJson = JsonUtility.ToJson(request);
            pending = JsonRequest("/v1/locomotion/generate", LastRequestJson, 25);
            yield return pending.SendWebRequest();
            if (!Current(token)) yield break;
            string json = ReadAndDisposeResponse();
            LastResponseJson = json;
            CheckWaitingAnchor();
            var identity = JsonUtility.FromJson<ResponseIdentity>(json);
            if (identity == null || identity.characterId != characterId || identity.requestId != requestId || identity.revision != token)
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
            Stage = "trajectory-audit";
            Vector3[] auditedRoots = AuditGeneratedPath(parsed);
            LastArrivalTiming = ArdyRoomArrivalTiming.Select(parsed, auditedRoots, TargetGround, ArrivalTolerance);
            Vector3 playbackEnd = auditedRoots[LastArrivalTiming.PlaybackEndFrame];
            LastEndpointError = Vector2.Distance(new Vector2(playbackEnd.x, playbackEnd.z), new Vector2(TargetGround.x, TargetGround.z));
            CheckFinalHeading(parsed, LastArrivalTiming.PlaybackEndFrame);
            LastClip = parsed;
            Stage = "playback";
            player.Play(parsed, GuardStep, LastArrivalTiming.PlaybackEndFrame);
            if (!player.HasPoseOwnership) throw new InvalidOperationException(player.Status);
            ownedClip = parsed; ownedPlayerRevision = player.PlaybackRevision;
            IsBusy = false;
            SetStatus("正在房间内移动；逐帧检查障碍物和地面边界", ArdyRoomExecutionState.Moving);
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
            return ArdyRoomTargetBuilder.Build(route.Corners, route.Length, anchorRotation,
                anchorGround, motionScale, WalkingSpeed, maxFrames, initialHeading, FinalHeading());
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
            float? desired = FinalHeading();
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

        private Vector3[] AuditGeneratedPath(ArdyLocomotionClip value)
        {
            Vector3 previous = anchorRootPosition;
            var roots = new Vector3[value.frames.Length];
            for (int i = 0; i < value.frames.Length; i++)
            {
                Vector3 next = GeneratedRoot(value, i);
                roots[i] = next;
                string reason = GuardStep(previous, next);
                if (reason != null) throw new ExecutionException("生成路径未通过房间审核，第 " + i + " 帧：" + reason, ArdyRoomExecutionReason.GeneratedPathRejected);
                previous = next;
            }
            LastEndpointError = Vector2.Distance(new Vector2(previous.x, previous.z), new Vector2(TargetGround.x, TargetGround.z));
            if (LastEndpointError > ArrivalTolerance)
                throw new ExecutionException("生成步态未可靠到达目标（偏差 " + LastEndpointError.ToString("F2") + " 米）；本次未播放。", ArdyRoomExecutionReason.GeneratedPathRejected);
            CheckFinalHeading(value, value.frames.Length - 1);
            return roots;
        }

        private string GuardStep(Vector3 previous, Vector3 next)
        {
            if (navigation == null) return "房间导航已离开";
            string avatarReason = AvatarUnavailableReason();
            if (avatarReason != null) return avatarReason;
            Vector3 a = previous, b = next;
            a.y = b.y = floorY;
            if (!navigation.ValidateStep(a, b, out _, out string reason)) return reason;
            return AdditionalStepGuard?.Invoke(previous, next);
        }

        private void LateUpdate()
        {
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
            if (ownedClip == null || player == null) return;
            if (player.Clip == ownedClip && player.PlaybackRevision == ownedPlayerRevision && player.ReturnedToAnimation && !player.HasPoseOwnership)
            {
                ownedClip = null;
                Stage = "completed";
                SetStatus("已到达并停止 · 目标偏差 " + LastEndpointError.ToString("F2") + " 米；已平滑交回当前动画", ArdyRoomExecutionState.Arrived, ArdyRoomExecutionReason.Completed);
                return;
            }
            if (player.Clip != ownedClip || player.PlaybackRevision != ownedPlayerRevision || !player.HasPoseOwnership)
            {
                string reason = player.LastBlockedReason;
                ownedClip = null;
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
                    Stage = "returning";
                    SetStatus("已到达，正在平滑衔接当前待机动画", ArdyRoomExecutionState.Returning);
                }
                catch (Exception error) { Fail(error.Message); }
            }
        }

        private Vector3 AvatarGround()
        {
            Vector3 point = avatar.transform.position;
            point.y += reference.groundY * avatar.transform.lossyScale.y;
            return point;
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
                    throw new InvalidOperationException("ARDY 服务请求失败（" + request.responseCode + "）：" + request.error);
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

        private void OnDisable() => Cancel();
        private void OnDestroy() => Cancel();
    }
}
