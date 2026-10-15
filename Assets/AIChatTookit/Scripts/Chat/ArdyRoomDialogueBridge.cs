using System;
using System.Collections.Generic;
using NeEEvA.Motion;
using NeEEvA.Player;
using UniVRM10;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Opt-in, scene-owned conversation locomotion. The Editor window is only a setup tool.</summary>
[DisallowMultipleComponent, DefaultExecutionOrder(13000)]
public sealed partial class ArdyRoomDialogueBridge : MonoBehaviour
{
    public ChatSample chat;
    public Vrm10Instance avatar;
    public Transform environmentRoot;
    [Tooltip("Explicit user's head/view transform. No implicit main-camera or character-forward fallback.")]
    public Transform userHead;
    public ArdyLocomotionAvatarReference importedReference;
    public Transform[] excludedVisualRoots = Array.Empty<Transform>();
    public string serviceUrl = "http://127.0.0.1:8093";
    [Range(.15f, 1.2f)] public float walkingSpeed = .65f;
    [Range(.8f, 1.5f)] public float approachDistance = 1f;
    public bool connectOnStart = true;

    public bool IsConnected => subscribedChat != null && controller != null && navigation != null && navigation.IsBuilt;
    public bool ReservesBody => IsConnected && (activeAction && !StreamWaiting || controller.Player != null && controller.Player.HasPoseOwnership);
    private bool StreamWaiting => followActive && controller != null && controller.IsContinuousFollowActive && controller.Stage == "stream-waiting";
    private bool StreamRecovering => followActive && controller != null && controller.IsContinuousFollowActive && controller.StreamIsRecovering;
    public ArdyRoomLocomotionController Controller => controller;
    public ArdyRoomNavigation Navigation => navigation;
    public string Status => reason;
    public string Phase => phase;
    public string ActionId => actionId;
    public bool IsFollowing => followActive;
    public string TaskMode => sceneOrientActive ? "turn-in-place" : sceneTargetActive ? SceneTargetMotionKind : followActive ? "follow" : activeAction ? "approach" : "none";
    public string ActiveTaskId => followActive || activeAction ? actionId : "";
    public string RecentDiagnosticText => string.Join("\n", actionReports.ToArray());
    public string DiagnosticSummary => latestDiagnostic == null ? "尚无动作诊断" :
        $"动作 {latestDiagnostic.actionId}\n阶段 {latestDiagnostic.stage} · {latestDiagnostic.phase}\n{latestDiagnostic.reason}\n" +
        $"路线 {latestDiagnostic.routeLength:F2} 米；距用户 {latestDiagnostic.actualUserDistance:F2} 米；朝向差 {latestDiagnostic.facingError:F1}°\n" +
        (latestDiagnostic.support.found ? $"用户下方：{latestDiagnostic.support.objectName}；视点距其 {latestDiagnostic.support.headHeight:F3} 米\n" : "用户下方尚未确认支撑面\n") +
        $"地面位置变化 {latestDiagnostic.userDisplacementXZ:F3} 米；视点高度变化 {latestDiagnostic.userDisplacementY:F3} 米" +
        (followActive && controller != null ? $"\n跟随轨迹更新 {controller.CommittedTargetRevision}/{controller.MovingTargetRevision} · {controller.RetargetStatus}" : "") +
        (followActive ? $"\n用户估计速度 {followVelocity.Speed:F2} 米/秒；请求步速 {followRequestedSpeed:F2} 米/秒" +
            (followSpeedLimited ? "；已达到当前验证速度上限" : "") : "") +
        (controller != null && controller.Player != null && controller.Player.IsStoppingSmoothly ? "\n位移已停止，身体正在平滑回到待机。" : "");

    private GameObject host;
    private ArdyRoomNavigation navigation;
    private ArdyRoomLocomotionController controller;
    private ChatSample subscribedChat;
    private ArdyRoomApproachPlan plan;
    private ArdyRoomApproachObservation attemptObservation;
    private Transform boundUser;
    private string actionId = "", phase = "disconnected", reason = "尚未启用对话房间移动";
    private string actionIntent = "";
    private string lastBodyRejection = "";
    private bool activeAction;
    private int operationRevision;
    private float factTime;
    private float actionStartedAt;
    private bool hasControllerOperation;
    private RoomDiagnostic latestDiagnostic;
    private RoomRejectionEvidence lastObservedRejection;
    private readonly Queue<string> actionReports = new Queue<string>();
    private const float AssessmentLifetime = 3f;
    private int currentTargetRevision, lastAttemptTargetRevision = -1;
    private int assessmentBuildCount;
    private bool hasTargetSnapshot, snapshotConnected, snapshotUserValid;
    private Transform snapshotUser;
    private ArdyRoomNavigation snapshotNavigation;
    private Vector3 snapshotHead, snapshotRoot;
    private Quaternion snapshotRotation;
    private float snapshotDistance;
    private CurrentTargetAssessment currentAssessment;
    private string lastAttemptActionId = "", lastAttemptPhase = "", lastAttemptReason = "";
    private float lastAttemptTime;
    private bool followActive, followNeedsNewTarget;
    private Transform followUser;
    private Vector3 followObservedHead, followPausedHead;
    private float followGroundY, followLastDispatchAt = float.NegativeInfinity, followTargetUpdatedAt;
    private int followLegCount;
    private string followTargetUpdateReason = "";
    private readonly ArdyRoomUserVelocityEstimator followVelocity = new ArdyRoomUserVelocityEstimator();
    private float followRequestedSpeed, followRemainingPath, followLastCommandAt = float.NegativeInfinity;
    private float followLastGeometryAt = float.NegativeInfinity, followLastSpeedLogAt = float.NegativeInfinity;
    private bool followSpeedLimited, followResumeBodyRequested;
    private string followPauseCategory = "";
    private bool navigationRecoveryActive, navigationRecoveryAttempted;
    private string navigationRecoveryReason = "";
    private Vector3 navigationRecoveryUserGround, navigationRecoveryUserHead;
    private float navigationRecoveryUserClearanceRadius;

    /// <summary>Read-only evidence about the current scene target, never a promise of generated motion.</summary>
    [Serializable] public sealed class CurrentTargetAssessment
    {
        public string state = "unchecked", reason = "当前位置尚未检查。";
        public bool canPlanRoute, budgetChecked, executionGuaranteed;
        public float observedAtRealtime, expiresAtRealtime, routeLength = -1;
        public int buildCount;
        public Vector3 plannedGround;
        public ArdyRoomApproachObservation observation;
        public string scope = "User-approach geometry only. This does not assess a visible object or in-place observation. Generation duration, service capability, pose history and playback have not been validated; reachable does not mean movement executed or guaranteed.";
    }

    [Serializable] public sealed class CurrentTargetEvidence
    {
        public string executionTargetKind;
        public SceneTargetResult lastSceneTargetResult;
        public int currentTargetRevision, lastAttemptTargetRevision;
        public bool connected, userAnchorValid, userTrackingValid, userSupportVerified, bodyReserved, lastAttemptAppliesToCurrentTarget, currentlyNearAndFacingUser;
        public string attemptStatus, activeActionId;
        public float observedAtRealtime, desiredUserDistance, actualUserDistance, actualFacingErrorDegrees;
        public Vector3 actualRoot, userHeadWorld;
        public CurrentTargetAssessment assessment;
        public string taskMode, activeTaskId;
        public bool followActive;
        public int followLegCount, controllerOperationRevision;
        public float followTargetUpdatedAtRealtime, followNextPlanAtRealtime, followResumeDistance, followMovingResumeDistance;
        public int segmentIndex, segmentCount, preparedSegmentCount;
        public float plannedDurationSeconds, playedDurationSeconds;
        public bool waitingForSegment;
        public bool poseReturning, movingTargetNeedsRestart;
        public int movingTargetRevision, committedTargetRevision;
        public string movingTargetStatus;
        public bool continuousFollowActive, followUserMoving, followSpeedLimited;
        public Vector3 followEstimatedUserVelocity;
        public float followEstimatedUserSpeed, followRequestedSpeed, followRemainingPath, followMaximumSpeed;
        public string followVelocityState;
        public string activeBlockingCategory;
        public ArdyRoomUserSupportObservation currentUserSupport;
        public Vector3 currentUserGround;
        public float currentUserClearanceRadius;
        public ArdyRoomStreamTelemetry streamTelemetry;
        public RoomRejectionEvidence lastObservedRejection;
        public bool navigationRecoveryActive, navigationRecoveryAttempted;
        public string navigationRecoveryReason;
        public string approachRetargetStatus;
        public int approachRetargetCount, approachRetargetUpdates, approachRetargetRestarts;
        public float approachRetargetTravel;
        public string scope = "Current engine observations for the explicit user's target revision. assessment describes user approach, not a scene-object destination. executionTargetKind identifies the running/last task. no-attempt means no user approach has been submitted for this target; assessment is not an action result.";
    }

    [Serializable] public sealed class RoomRejectionEvidence
    {
        public string source, category, reason, actionId;
        public int operationRevision, targetRevision;
        public float observedAtRealtime;
        public bool hasPoint, appliesToCurrentTarget;
        public Vector3 point;
        public string scope = "Observed failed check or rejected candidate, not an executed position or an exhaustive reachability verdict. A changed target makes this historical evidence.";
    }

    [Serializable] private sealed class LastActionResult
    {
        public string actionId, phase, reason;
        public string targetKind;
        public int targetRevision;
        public bool appliesToCurrentTarget;
        public float observedAtRealtime;
        public string scope = "Historical result of the last actual room approach; targetKind distinguishes user and scene-object. Do not reuse its rejection for a different target. currentFacts.assessment checks only user approach; lastSceneTargetResult describes the actual object approach.";
    }

    [Serializable] private sealed class RoomDiagnostic
    {
        public string targetKind, captureId, sceneTargetId;
        public Vector3 sceneSurfacePoint;
        public ArdyRoomObjectApproachObservation sceneTargetObservation;
        public string actionId, intent, phase, stage, reason, executionReason, observedUtc, userAnchor;
        public int operationRevision, navigationId;
        public float elapsedSeconds, routeLength = -1, actualUserDistance = -1, facingError = -1,
            userDisplacementXZ, userDisplacementY;
        public Vector3 avatarGround, userHead, sampledHead, plannedGround;
        public ArdyRoomUserSupportObservation support = new ArdyRoomUserSupportObservation();
        public ArdyRoomApproachObservation approachObservation;
        public string taskMode;
        public bool followActive;
        public int followLegCount;
        public float followTargetUpdatedAtRealtime;
        public int movingTargetRevision, committedTargetRevision;
        public string movingTargetStatus;
        public bool poseReturning;
        public bool continuousFollowActive, followUserMoving, followSpeedLimited;
        public Vector3 followEstimatedUserVelocity;
        public float followEstimatedUserSpeed, followRequestedSpeed, followRemainingPath;
        public string followVelocityState;
        public ArdyRoomStreamTelemetry streamTelemetry;
        public RoomRejectionEvidence lastObservedRejection;
        public bool navigationRecoveryActive, navigationRecoveryAttempted;
        public string navigationRecoveryReason;
        public string approachRetargetStatus;
        public int approachRetargetCount, approachRetargetUpdates, approachRetargetRestarts;
        public float approachRetargetTravel;
    }

    [Serializable] private sealed class RoomFact
    {
        public string targetKind;
        public SceneTargetResult lastSceneTargetResult;
        public string source = "Unity-room-runtime", phase, actionId, reason, lastBodyRejection;
        public bool connected, userAnchorValid, bodyReserved, reachedPlannedStandpoint, currentlyNearAndFacingUser;
        public float observedAtRealtime, resultObservedAtRealtime, desiredUserDistance, actualUserDistance, actualFacingErrorDegrees;
        public Vector3 actualRoot, userHeadWorld, plannedGround;
        public ArdyRoomUserSupportObservation currentUserSupport;
        public RoomDiagnostic latestExecutionObservation;
        public int currentTargetRevision, lastAttemptTargetRevision;
        public bool lastAttemptAppliesToCurrentTarget;
        public CurrentTargetAssessment currentAssessment;
        public CurrentTargetEvidence currentFacts;
        public LastActionResult lastActionResults;
        public string legacyResultScope = "Top-level phase/actionId/reason/plannedGround/latestExecutionObservation describe the latest execution or session event, not current target feasibility. Use currentFacts and lastActionResults.";
        public string scope = "Same-level room approach, grounded scene-object approach, or explicitly requested continuous follow. targetKind identifies the actual goal; object arrival does not imply arrival near the user. Follow rechecks moved targets and may pause when blocked. Navigation coordinates come from engine validation; semantic object identity still requires visual evidence. Stairs and other floors are unsupported.";
    }

    private void Start()
    {
        if (!connectOnStart || IsConnected) return;
        try { Connect(); }
        catch (Exception error) { SetFact("unavailable", error.Message); Debug.LogWarning("[Room/Dialogue] " + error.Message, this); }
    }

    public void Connect()
    {
        if (!Application.isPlaying || !isActiveAndEnabled) throw new InvalidOperationException("在 Play 模式启用房间对话组件。");
        if (chat == null || avatar == null || environmentRoot == null || userHead == null || importedReference == null)
            throw new InvalidOperationException("需要聊天组件、角色、房间、明确的用户头部锚点和导入骨骼参考。");
        if (chat.gameObject != gameObject) throw new InvalidOperationException("房间对话组件必须与 ChatSample 位于同一对象，以统一身体控制。");
        if (!avatar.gameObject.activeInHierarchy || !environmentRoot.gameObject.activeInHierarchy ||
            avatar.gameObject.scene != environmentRoot.gameObject.scene || chat.gameObject.scene != avatar.gameObject.scene)
            throw new InvalidOperationException("聊天、角色和房间必须在同一已加载场景中启用。");
        if (userHead.IsChildOf(avatar.transform)) throw new InvalidOperationException("用户锚点不能是角色自己的骨骼或相机。");
        foreach (var other in FindObjectsOfType<ArdyRoomDialogueBridge>())
            if (other != this && other.avatar == avatar && other.IsConnected)
                throw new InvalidOperationException("该角色已经连接了另一份房间对话会话。");
        foreach (var other in FindObjectsOfType<ArdyRoomLocomotionController>())
            if (other != controller && other.isActiveAndEnabled && other.Avatar == avatar)
                throw new InvalidOperationException("请先释放旧 ARDY Room 点选会话，再启用聊天呼唤。");
        importedReference.Validate();
        if (importedReference.avatar != avatar.Vrm) throw new InvalidOperationException("导入骨骼参考与当前角色不匹配。");
        var geometry = environmentRoot.GetComponent<ArdyRoomGeometryReference>();
        if (geometry == null || geometry.environmentRoot != environmentRoot || geometry.entries == null || geometry.entries.Length == 0)
            throw new InvalidOperationException("请先在编辑模式准备房间原始几何。");
        Disconnect("room-reconnected");
        var gestures = chat.GetComponent<ArdyDialogueMotionBridge>();
        if (gestures == null) gestures = chat.gameObject.AddComponent<ArdyDialogueMotionBridge>();
        if (gestures.IsBound && gestures.Avatar != avatar) throw new InvalidOperationException("对话手势连接的是另一角色；请先断开。");
        gestures.enabled = true;
        gestures.interactionTarget = userHead;
        gestures.serviceUrl = serviceUrl;
        if (!gestures.IsBound) gestures.Bind(chat, avatar);
        try
        {
            host = new GameObject("ARDY conversation room session") { hideFlags = HideFlags.DontSave };
            SceneManager.MoveGameObjectToScene(host, avatar.gameObject.scene);
            navigation = host.AddComponent<ArdyRoomNavigation>();
            navigation.ExcludedVisualRoots = excludedVisualRoots ?? Array.Empty<Transform>();
            navigation.AgentHeight = Mathf.Max(1.1f,
                (importedReference.Get(HumanBodyBones.Head).positionInRoot.y - importedReference.groundY) * avatar.transform.lossyScale.y + .18f);
            if (!navigation.Build(environmentRoot, avatar.transform, out string failure)) throw new InvalidOperationException(failure);
            controller = host.AddComponent<ArdyRoomLocomotionController>();
            controller.Endpoint = serviceUrl;
            controller.WalkingSpeed = walkingSpeed;
            controller.Configure(avatar, importedReference, navigation);
            controller.AdditionalStepGuard = GuardUser;
            subscribedChat = chat;
            subscribedChat.MotionIntentRequested += OnIntent;
            subscribedChat.MotionCancelled += OnCancelled;
            subscribedChat.MotionStateContextRequested += DescribeRoomContext;
            subscribedChat.ConfigureRoomMotionOutput(true);
            SetFact("ready", "房间移动已连接，可以请求走近、持续跟随用户或停止移动。");
        }
        catch { Disconnect("room-connect-failed"); throw; }
    }

    public void Approach(string id)
    {
        if (!IsConnected) throw new InvalidOperationException("房间对话未连接。");
        if (controller.Avatar != avatar || navigation.EnvironmentRoot != environmentRoot || chat != subscribedChat)
            throw new InvalidOperationException("角色、房间或聊天引用已改变；请重新连接空间会话。");
        RefreshTargetSnapshot();
        StopMovement("replaced", "新空间目标替换旧目标。");
        ResetSceneTargetForUserTask();
        ResetNavigationRecovery();
        ResetApproachRetarget();
        BeginApproachLeg(id);
    }

    private void BeginApproachLeg(string id)
    {
        RefreshTargetSnapshot();
        actionId = id ?? "";
        actionIntent = followActive ? "follow" : "approach";
        lastAttemptActionId = actionId;
        lastAttemptTargetRevision = currentTargetRevision;
        lastAttemptPhase = "preparing"; lastAttemptReason = "正在验证本次实际走近请求。";
        lastAttemptTime = Time.realtimeSinceStartup;
        actionStartedAt = Time.realtimeSinceStartup;
        hasControllerOperation = false;
        plan = null;
        attemptObservation = null;
        lastBodyRejection = "";
        if (!ValidUser(out string failure)) { StopMovement("unavailable", failure); return; }
        Vector3 ground = avatar.transform.position + Vector3.up * importedReference.groundY * avatar.transform.lossyScale.y;
        if (!ArdyRoomApproachPlanner.TryPlan(navigation, ground, userHead, approachDistance, out plan, out failure, out attemptObservation))
        {
            RecordPlanningRejection(attemptObservation, failure);
            if (TryRecoverPlanStart(attemptObservation, failure)) return;
            if (followActive) PauseFollow("follow-paused", failure, true);
            else
            {
                activeAction = false;
                if (approachRetargetCount > 0) approachRetargetStatus = "route-rejected";
                SetFact("unreachable", failure);
            }
            return;
        }
        boundUser = userHead;
        // Invalidate a pending upper-body response before gathering the new full-body history.
        var live = avatar.GetComponent<ArdyLiveMotionController>();
        if (live != null) live.Cancel("room-approach");
        activeAction = true;
        SetFact("preparing", "已接受走近目标，正在准备实际全身动作。");
        try
        {
            controller.WalkingSpeed = walkingSpeed;
            hasControllerOperation = true;
            if (followActive)
            {
                followLastGeometryAt = Time.realtimeSinceStartup;
                UpdateFollowSpeed(followLastGeometryAt);
                controller.BeginContinuousFollow(plan.Route, plan.UserGround, plan.FacePoint,
                    followVelocity.Velocity, followRequestedSpeed, followVelocity.IsMoving);
            }
            else controller.MoveTo(plan.GroundTarget, plan.FacePoint);
            operationRevision = controller.OperationRevision;
            if (!followActive)
            {
                approachRetargetOperationBase = approachRetargetCount;
                controller.SetMovingTargetAttemptBudget(operationRevision, MaxApproachRetargets - approachRetargetCount);
            }
            ObserveExecution();
        }
        catch (Exception error) { StopMovement("failed", error.Message); }
    }

    /// <summary>Reject a stale model decision before cancelling or dispatching any body work.</summary>
    public bool ApproachForTarget(string id, int expectedTargetRevision, out string failure)
    {
        var evidence = ReadCurrentTargetEvidence(true);
        if (expectedTargetRevision != evidence.currentTargetRevision)
        { failure = "用户目标或角色规划起点在决策期间发生变化；旧决定未执行，请读取当前位置重新决定。"; return false; }
        if (!evidence.connected || evidence.assessment.state == "stale")
        { failure = evidence.assessment.reason; return false; }
        if (evidence.bodyReserved)
        { failure = "已有房间目标正在执行；本次决定未重复派发。"; return false; }
        // An unreachable current target is still an actual requested attempt: Approach records its
        // fresh precise rejection. A read-only check alone must never manufacture an action result.
        Approach(id);
        failure = null;
        return true;
    }

    /// <summary>Start one explicitly authorized follow task; continuous motion retains its operation while targets change.</summary>
    public bool FollowForCurrentUser(string id, int previousRevision, Transform expectedUser, out string failure)
    {
        failure = "用户绑定已变化，当前跟随授权不能转交给另一个目标。";
        if (expectedUser == null || userHead != expectedUser || !IsConnected) return false;
        // Called once, synchronously, after the chat validates the complete user/turn/room lease.
        // Recompute support and geometry; no old path is reused and no model retry is needed.
        var current = ReadCurrentTargetEvidence(true);
        if (userHead != expectedUser) return false;
        if (current.bodyReserved) { failure = "已有房间目标正在执行；本次跟随决定未重复派发。"; return false; }
        bool submitted = FollowForTarget(id, current.currentTargetRevision, out failure);
        if (previousRevision != current.currentTargetRevision)
            Debug.Log("[Room/FollowRefresh] " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
                actionId = id, previousRevision, currentRevision = current.currentTargetRevision, submitted, failure,
                scope = "Same tracked user and current user command; one fresh geometry validation, never reuse an old path." }), this);
        return submitted;
    }

    public bool FollowForTarget(string id, int expectedTargetRevision, out string failure)
    {
        var evidence = ReadCurrentTargetEvidence(true);
        if (expectedTargetRevision != evidence.currentTargetRevision)
        { failure = "用户目标或角色规划起点在决策期间发生变化；旧跟随决定未执行。"; return false; }
        if (!evidence.connected || evidence.assessment.state == "stale")
        { failure = evidence.assessment.reason; return false; }
        if (evidence.bodyReserved)
        { failure = "已有房间目标正在执行；本次跟随决定未重复派发。"; return false; }
        StopMovement("replaced", "新的持续跟随任务替换旧空间任务。");
        ResetSceneTargetForUserTask();
        ResetNavigationRecovery();
        ResetApproachRetarget();
        actionId = id ?? "";
        actionIntent = "follow";
        lastAttemptActionId = actionId; lastAttemptTargetRevision = currentTargetRevision;
        actionStartedAt = Time.realtimeSinceStartup; lastAttemptTime = actionStartedAt;
        plan = null; attemptObservation = null; hasControllerOperation = false;
        if (!ValidUser(out failure) ||
            !ArdyRoomApproachPlanner.TryGetUserGround(navigation, userHead, out Vector3 userGround, out failure))
        { SetFact("unavailable", failure); failure = null; return true; }
        float avatarGround = avatar.transform.position.y + importedReference.groundY * avatar.transform.lossyScale.y;
        if (Mathf.Abs(userGround.y - avatarGround) > navigation.MaxRouteHeightVariation)
        { SetFact("unreachable", "用户不在角色当前同层支撑地面，持续跟随未启动。"); failure = null; return true; }
        followActive = true; followNeedsNewTarget = false; followUser = userHead;
        // A near-user follow can wait without submitting a leg. Bind that idle controller
        // operation too, so a later external MoveTo cannot be mistaken for this follow task.
        operationRevision = controller.OperationRevision;
        followGroundY = userGround.y; followObservedHead = followPausedHead = userHead.position;
        followTargetUpdatedAt = Time.realtimeSinceStartup; followLegCount = 0;
        followLastDispatchAt = float.NegativeInfinity;
        followTargetUpdateReason = "";
        ResetFollowSpeed();
        followVelocity.Observe(userGround, Time.realtimeSinceStartup);
        TickFollow(Time.realtimeSinceStartup);
        failure = null;
        return true;
    }

    /// <summary>Program-owned stop identity, shared by structured tasks and the legacy motion event.</summary>
    public void StopForTask(string id)
    {
        actionId = id ?? "";
        actionIntent = "stop-moving";
        StopMovement();
    }

    /// <summary>
    /// A fast stop binds to the accepted task. One-shot callers also bind to their exact leg;
    /// an explicitly captured follow may advance its own legs while the stop decision is pending.
    /// </summary>
    public bool StopForTaskIfCurrent(string stopActionId, string expectedTaskId, int expectedOperationRevision,
        out string failure, bool expectedFollowing = false)
    {
        bool sameOwnedFollow = IsConnected && followActive && controller.OperationRevision == operationRevision &&
            controller.OperationRevision >= expectedOperationRevision &&
            (activeAction ? hasControllerOperation : controller.IsReturningOwnedPose ||
                !controller.IsBusy && !controller.IsMoving && (controller.Player == null || !controller.Player.HasPoseOwnership));
        if (!IsConnected || string.IsNullOrEmpty(expectedTaskId) || ActiveTaskId != expectedTaskId ||
            (expectedFollowing ? !sameOwnedFollow : controller.OperationRevision != expectedOperationRevision))
        { failure = "空间任务或执行段已改变；过期的快速停止结果未影响新任务。"; return false; }
        StopForTask(stopActionId); failure = null; return true;
    }

    public bool StopMonitoredActivity(string expectedTaskId,string expectedVersion,string reason)
    {
        if(!IsConnected || string.IsNullOrEmpty(expectedTaskId) || ActiveTaskId!=expectedTaskId ||
            controller.OperationRevision.ToString()!=expectedVersion || controller.OperationRevision!=operationRevision)return false;
        StopMovement("cancelled","process-monitor: "+reason);
        return true;
    }

    public void StopMovement(string result = "stopped", string explanation = "已明确停止移动，保留当前位置。")
    {
        string stoppedOrientId = sceneOrientActive ? sceneOrientId : null;
        if (navigationRecoveryActive) navigationRecoveryReason = explanation;
        if ((activeAction || followActive) && !string.IsNullOrEmpty(lastAttemptActionId))
        { lastAttemptPhase = result; lastAttemptReason = explanation; lastAttemptTime = Time.realtimeSinceStartup; }
        if (activeAction && controller != null && ((!sceneTargetActive && !sceneOrientActive) || controller.OperationRevision == operationRevision) && !ControllerAlreadyTerminated())
        { controller.Cancel(); operationRevision = controller.OperationRevision; }
        activeAction = false; followActive = false; followNeedsNewTarget = false; followUser = null;
        approachRetargetPending = approachRetargetSettling = false;
        navigationRecoveryActive = false;
        ResetFollowSpeed();
        SetFact(result, explanation);
        FinishSceneTarget(result, explanation);
        if (stoppedOrientId != null && sceneOrientId == stoppedOrientId) FinishSceneOrient(result, explanation);
    }

    private void OnIntent(DialogueMotionIntent intent)
    {
        if (!IsConnected) return;
        if (intent.Name == "approach") Approach(intent.ActionId);
        else if (intent.Name == "follow")
        {
            var current = ReadCurrentTargetEvidence(true);
            if (!FollowForTarget(intent.ActionId, current.currentTargetRevision, out string failure))
                Debug.LogWarning("[Room/Dialogue] 跟随请求未提交：" + failure, this);
        }
        else if (intent.Name == "stop-moving")
            StopForTask(intent.ActionId);
    }

    private void OnCancelled(int generation, string cause)
    {
        // A voice turn interrupts speech/gestures. A committed spatial goal has its own lifetime.
        if (cause == "new-user-turn" || cause == "barge-in" || cause == "user-started-speaking") return;
        StopMovement("cancelled", cause);
    }

    public void RejectBodyRequest(DialogueMotionIntent intent)
    {
        lastBodyRejection = "房间移动占用身体，本次 " + intent.Name + " 未执行；可先用 stop-moving 停止，再请求上身动作。";
        subscribedChat?.NotifyMotionFeedback(new ArdyMotionExecutionFeedback {
            actionId = intent.ActionId, parentActionId = intent.ParentActionId, responseGeneration = intent.ResponseGeneration,
            repairAttempt = intent.RepairAttempt, name = intent.Name, description = intent.Description, goal = intent.Goal,
            status = "rejected", reason = lastBodyRejection, canRepair = false });
        Debug.Log("[Room/Dialogue] action=" + intent.ActionId + " " + lastBodyRejection, this);
    }

    private bool ValidUser(out string failure)
    {
        failure = null;
        if (userHead == null || !userHead.gameObject.activeInHierarchy || !userHead.gameObject.scene.IsValid() || !userHead.gameObject.scene.isLoaded ||
            !Finite(userHead.position)) failure = "用户位置不可用，不能规划或继续移动。";
        else if (avatar == null || userHead.IsChildOf(avatar.transform)) failure = "用户锚点不能是角色自身。";
        else
        {
            var cameraController = userHead.GetComponent<PlayerCameraController>();
            if (cameraController != null && !cameraController.HasValidWorldPose) failure = "用户视点或 VR 追踪暂不可用，已停止空间行动。";
        }
        return failure == null;
    }

    private static bool Finite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);

    private string GuardUser(Vector3 from, Vector3 to)
    {
        if (activeAction && sceneOrientActive) return GuardSceneOrientStep(from, to);
        if (activeAction && sceneTargetActive) return GuardSceneTargetStep(from, to);
        if (!activeAction || plan == null && !navigationRecoveryActive) return null;
        string failure = CheckUserSnapshot();
        if (failure != null) return failure;
        Vector3 ground = navigationRecoveryActive ? navigationRecoveryUserGround : plan.UserGround;
        float radius = navigationRecoveryActive ? navigationRecoveryUserClearanceRadius : plan.UserClearanceRadius;
        Vector3 currentGround = new Vector3(userHead.position.x, ground.y, userHead.position.z);
        if (ArdyRoomApproachPlanner.StepKeepsUserClearance(from, to, currentGround, radius)) return null;
        const string reason = "用户已进入当前路线的身体净距范围，已停止移动。";
        RecordRejection("user-clearance-check", "user-clearance", reason, true, to);
        return reason;
    }

    private string CheckUserSnapshot()
    {
        if (controller.Avatar != avatar || navigation.EnvironmentRoot != environmentRoot || chat != subscribedChat)
            return "空间会话绑定引用已改变，当前动作已停止；请重新连接。";
        if (!ValidUser(out string failure)) return failure;
        if (userHead != boundUser) return "用户视点绑定已改变，本次走近已停止；请重新确定目标。";
        if (plan == null && !navigationRecoveryActive) return "本次空间动作缺少有效目标快照，已停止移动。";
        Vector3 expectedHead = navigationRecoveryActive ? navigationRecoveryUserHead : plan.FacePoint;
        Vector3 expectedGround = navigationRecoveryActive ? navigationRecoveryUserGround : plan.UserGround;
        float displacement = ArdyRoomApproachPlanner.UserGroundDisplacement(expectedHead, userHead.position);

        if (!ArdyRoomApproachPlanner.TryGetUserGround(navigation, userHead, out Vector3 ground, out failure)) return failure;
        if (Mathf.Abs(ground.y - expectedGround.y) > navigation.MaxRouteHeightVariation)
            return "用户已离开原来的同层支撑地面，本次走近已停止。";
        return NoticeApproachTargetShift(displacement);
    }

    private void LateUpdate()
    {
        if (sceneOrientActive && !IsConnected) { StopMovement("unavailable", "场景转向期间房间导航连接失效。"); return; }
        if (sceneTargetActive && !IsConnected) { StopMovement("unavailable", "视觉接近期间房间导航连接失效。"); return; }
        if (!IsConnected) return;
        if (sceneOrientActive) { TickSceneOrient(); return; }
        if (sceneTargetActive) { TickSceneTarget(); return; }
        if (followActive) { TickFollow(Time.realtimeSinceStartup); return; }
        if (!activeAction) return;
        if (controller.OperationRevision != operationRevision) { ObserveExecution(); return; }
        // Until the parent commits arrival, recheck a target that changed while
        // the final controller frame was finishing. Committed arrivals have
        // activeAction=false and can never enter this recovery path again.
        string failure = CheckUserSnapshot();
        if (failure != null) { StopMovement(approachRetargetStatus == "exhausted" ? "blocked" : "cancelled", failure); return; }
        if (TickApproachRetarget()) return;
        ObserveExecution();
    }

    private void TickFollow(float now)
    {
        if (!followActive || !IsConnected) return;
        if (controller.OperationRevision != operationRevision)
        {
            // A waiting follow has no authority to cancel an external MoveTo either.
            // Drop only this parent task; leave the newer controller/body owner untouched.
            activeAction = false; followActive = false; followNeedsNewTarget = false; followUser = null;
            navigationRecoveryActive = false;
            ResetFollowSpeed();
            SetFact("cancelled", "空间执行已被另一目标替换，旧跟随任务不会取消或接管新动作。");
            return;
        }
        if (activeAction && ControllerAlreadyTerminated())
        {
            // Consume the completed execution result before a fresh geometry check can
            // replace its explanation with a different, later user-position observation.
            ObserveExecution();
            return;
        }
        if (controller.Avatar != avatar || navigation.EnvironmentRoot != environmentRoot || chat != subscribedChat || userHead != followUser)
        { StopMovement("cancelled", "持续跟随的角色、房间或用户绑定已改变；请重新连接并下达任务。"); return; }
        if (!ValidUser(out string failure) ||
            !ArdyRoomApproachPlanner.TryGetUserGround(navigation, userHead, out Vector3 userGround, out failure))
        { StopMovement("unavailable", failure); return; }
        if (Mathf.Abs(userGround.y - followGroundY) > navigation.MaxRouteHeightVariation)
        { StopMovement("cancelled", "用户已离开持续跟随开始时的同层支撑地面，跟随已停止。"); return; }
        if (ArdyRoomApproachPlanner.UserGroundDisplacement(followObservedHead, userHead.position) > .01f)
        { followObservedHead = userHead.position; followTargetUpdatedAt = now; }
        followVelocity.Observe(userGround, now);
        if (followVelocity.Discontinuity)
        { followRequestedSpeed = 0; followLastGeometryAt = float.NegativeInfinity; }
        if (navigationRecoveryActive)
        {
            failure = CheckUserSnapshot();
            if (failure != null) { StopMovement("cancelled", failure); return; }
            ObserveExecution();
            return;
        }
        if (activeAction)
        {
            failure = CheckUserSnapshot();
            if (failure != null) { StopMovement("cancelled", failure); return; }
            if (now - followLastGeometryAt >= ArdyRoomFollowMotionPolicy.GeometryInterval)
            {
                followLastGeometryAt = now;
                Vector3 start = avatar.transform.position + Vector3.up * importedReference.groundY * avatar.transform.lossyScale.y;
                if (!ArdyRoomApproachPlanner.TryPlan(navigation, start, userHead, approachDistance,
                    out var updatedPlan, out failure, out var observation))
                {
                    attemptObservation = observation;
                    RecordPlanningRejection(observation, failure);
                    if (TryRecoverPlanStart(observation, failure)) return;
                    PauseFollow("follow-paused", "最新用户站位暂不可达，已停止位移并平滑收回姿态。" + failure, true); return;
                }
                plan = updatedPlan; attemptObservation = observation;
            }
            if (now - followLastCommandAt >= ArdyRoomFollowMotionPolicy.CommandInterval)
            {
                UpdateFollowSpeed(now);
                PrepareWaitingFollowResume(followVelocity.IsMoving, followRequestedSpeed);
                if (!controller.TryUpdateContinuousFollow(plan.Route, userGround, userHead.position,
                    followVelocity.Velocity, followRequestedSpeed, followVelocity.IsMoving, out string updateReason))
                {
                    if (followTargetUpdateReason != updateReason)
                    {
                        followTargetUpdateReason = updateReason;
                        Debug.Log("[Room/FollowTarget] action=" + actionId + " operation=" + operationRevision +
                            " deferred=" + updateReason, this);
                    }
                }
                else followTargetUpdateReason = "";
                if (now - followLastSpeedLogAt >= 2f)
                {
                    followLastSpeedLogAt = now;
                    Debug.Log($"[Room/FollowSpeed] action={actionId} operation={operationRevision} user={followVelocity.Speed:F3} requested={followRequestedSpeed:F3} path={followRemainingPath:F3} limited={followSpeedLimited} state={followVelocity.State}", this);
                }
            }
            ObserveExecution();
            return;
        }
        // A stopped task no longer moves the root, but its pose still owns the body while
        // returning to the live Animator. Do not collect history or restart during that fade.
        if (controller.Player != null && controller.Player.HasPoseOwnership) return;
        float displacement = ArdyRoomApproachPlanner.UserGroundDisplacement(followPausedHead, userHead.position);
        MeasureUser(out float distance, out _);
        if (!ArdyRoomFollowMotionPolicy.ShouldBegin(distance, approachDistance,
            userGround - avatar.transform.position, followVelocity.Velocity, followVelocity.IsMoving))
        {
            // Holding position is intentional. Do not retreat if the user comes closer.
            if (phase != "follow-paused") SetFact("follow-waiting", "持续跟随已启用；当前距离无需继续走动，等待用户移远。");
            return;
        }
        if (!ArdyRoomFollowPolicy.MayDispatch(now, followLastDispatchAt, followNeedsNewTarget, displacement)) return;
        followLastDispatchAt = now; followNeedsNewTarget = false; followPausedHead = userHead.position;
        followLegCount++;
        BeginApproachLeg(actionId);
    }

    private void PauseFollow(string nextPhase, string message, bool waitForMovedTarget)
    {
        followPauseCategory = CurrentPauseCategory();
        if (activeAction && controller != null && !ControllerAlreadyTerminated())
        { controller.Cancel(); operationRevision = controller.OperationRevision; }
        activeAction = false;
        followRequestedSpeed = 0;
        followLastCommandAt = float.NegativeInfinity;
        followNeedsNewTarget = waitForMovedTarget;
        followPausedHead = userHead != null ? userHead.position : Vector3.zero;
        SetFact(nextPhase, message);
    }

    private void ResetNavigationRecovery()
    {
        navigationRecoveryActive = navigationRecoveryAttempted = false;
        navigationRecoveryReason = "";
    }

    private static ArdyRoomRouteDiagnostic PlanStartBoundary(ArdyRoomApproachObservation observation)
    {
        bool Matches(ArdyRoomRouteDiagnostic check) => check != null && !check.success &&
            check.stage == "start" && check.code == "navmesh-boundary";
        if (Matches(observation?.preflightFailure)) return observation.preflightFailure;
        if (Matches(observation?.stationaryAttempt)) return observation.stationaryAttempt;
        // Retained older observations may have mixed target failures before the common start check.
        if (observation?.candidates != null)
            foreach (var candidate in observation.candidates)
                if (!candidate.accepted && Matches(candidate.navigation)) return candidate.navigation;
        return null;
    }

    /// <summary>Only a precise invalid-start observation can consume the parent task's one recovery attempt.</summary>
    private bool TryRecoverPlanStart(ArdyRoomApproachObservation observation, string planningFailure)
    {
        if (PlanStartBoundary(observation) == null) return false;
        if (navigationRecoveryAttempted)
        {
            navigationRecoveryReason = "角色当前位置再次未通过导航起点检查；本次任务的有限恢复已使用，已结束任务。" + planningFailure;
            StopMovement("blocked", navigationRecoveryReason);
            return true;
        }
        if (!ValidUser(out string failure) ||
            !ArdyRoomApproachPlanner.TryGetUserGround(navigation, userHead, out Vector3 userGround, out failure))
        { StopMovement("unavailable", failure); return true; }
        float currentGroundY = avatar.transform.position.y + importedReference.groundY * avatar.transform.lossyScale.y;
        if (Mathf.Abs(userGround.y - currentGroundY) > navigation.MaxRouteHeightVariation)
        { StopMovement("unreachable", "用户不在角色当前同层支撑地面，未启动起点恢复。"); return true; }

        bool previousActiveAction = activeAction;
        navigationRecoveryAttempted = true;
        navigationRecoveryActive = true;
        navigationRecoveryUserGround = userGround;
        navigationRecoveryUserHead = userHead.position;
        navigationRecoveryUserClearanceRadius = navigation.AgentRadius + ArdyRoomApproachPlanner.UserBodyRadius + ArdyRoomApproachPlanner.UserClearance;
        boundUser = userHead;
        activeAction = true;
        followRequestedSpeed = 0;
        navigationRecoveryReason = "角色当前位置不满足导航起点条件，正在尝试一次经过地形和身体净距检查的短距离返回。";
        try
        {
            // The controller validates a bounded recovery certificate and handles only its own
            // operation/body handoff. Do not cancel a gesture or move the root from the bridge.
            if (!controller.TryBeginNavigationRecovery(userHead.position, out failure))
            {
                navigationRecoveryActive = false;
                activeAction = previousActiveAction;
                navigationRecoveryReason = "角色当前位置不可规划，未能安全启动有限恢复；本次任务已结束。" + failure;
                StopMovement("blocked", navigationRecoveryReason);
                return true;
            }
            hasControllerOperation = true;
            operationRevision = controller.OperationRevision;
            SetFact("nav-recovering", navigationRecoveryReason);
            ObserveExecution();
        }
        catch (Exception error)
        {
            navigationRecoveryActive = false;
            if (!hasControllerOperation && controller.OperationRevision == operationRevision) activeAction = previousActiveAction;
            navigationRecoveryReason = "角色起点恢复未完成，本次任务已结束。" + error.Message;
            StopMovement("failed", navigationRecoveryReason);
        }
        return true;
    }

    private void ObserveNavigationRecovery()
    {
        switch (controller.ExecutionState)
        {
            case ArdyRoomExecutionState.Arrived:
                // Arrived is issued only after the controller verifies normal NavMesh start
                // admission at the executed endpoint and completes its body return.
                if (controller.Player != null && controller.Player.HasPoseOwnership) return;
                navigationRecoveryActive = false;
                activeAction = false;
                navigationRecoveryReason = "角色已返回可规划起点，接下来检查当前用户目标。";
                if (followActive)
                {
                    followNeedsNewTarget = false;
                    followPausedHead = userHead.position;
                    followLastDispatchAt = float.NegativeInfinity;
                    followLastGeometryAt = followLastCommandAt = float.NegativeInfinity;
                    SetFact("follow-replanning", navigationRecoveryReason);
                }
                else BeginApproachLeg(actionId);
                return;
            case ArdyRoomExecutionState.Blocked:
            case ArdyRoomExecutionState.Failed:
            case ArdyRoomExecutionState.Cancelled:
                navigationRecoveryReason = "角色起点恢复未完成，本次任务已结束；不会反复重试。" + controller.Status;
                StopMovement(controller.ExecutionState == ArdyRoomExecutionState.Cancelled ? "cancelled" :
                    controller.ExecutionState == ArdyRoomExecutionState.Blocked ? "blocked" : "failed", navigationRecoveryReason);
                return;
            default:
                navigationRecoveryReason = "角色当前位置不可规划，正在执行一次有限的起点恢复。" + controller.Status;
                SetFact("nav-recovering", navigationRecoveryReason);
                return;
        }
    }

    private void PrepareWaitingFollowResume(bool userMoving, float requestedSpeed)
    {
        // The native session wakes on userMoving even while the distance policy still
        // requests zero speed. Cancel the waiting gesture before sending that wakeup,
        // so its real history is sampled after cancellation rather than after prebuffering.
        bool requestsResume = userMoving || requestedSpeed >= .04f;
        if (!StreamWaiting || StreamRecovering || !requestsResume)
        { followResumeBodyRequested = false; return; }
        if (controller.Player != null && controller.Player.HasPoseOwnership) return;
        if (followResumeBodyRequested) return;
        followResumeBodyRequested = true;
        var live = avatar.GetComponent<ArdyLiveMotionController>();
        if (live != null) live.Cancel("room-follow-resumed");
    }

    private string CurrentPauseCategory()
    {
        if (ControllerAlreadyTerminated() && controller.ExecutionState == ArdyRoomExecutionState.Blocked)
        {
            // Native stream diagnostics intentionally retain the last fault after recovery.
            // A later live terrain guard is a new event and cannot inherit that old category.
            if (controller.Stage == "live-guard-blocked")
                return CurrentRejectionMatches(controller.Player != null ? controller.Player.LastBlockedReason : null)
                    ? lastObservedRejection.category : "navigation";
            if (controller.ExecutionReason == ArdyRoomExecutionReason.NavigationRejected ||
                controller.ExecutionReason == ArdyRoomExecutionReason.TerrainBlocked) return "navigation";
            var telemetry = controller.StreamTelemetry;
            if (telemetry != null && telemetry.failureAppliesToCurrentOperation &&
                telemetry.failureOperationRevision == controller.OperationRevision && !string.IsNullOrEmpty(telemetry.lastFailureReason) &&
                controller.Status.EndsWith(telemetry.lastFailureReason, StringComparison.Ordinal) &&
                (telemetry.lastFailureCategory == "navigation" || telemetry.lastFailureCategory == "user-clearance" ||
                 telemetry.lastFailureCategory == "motion-quality" || telemetry.lastFailureCategory == "motion-control")) return telemetry.lastFailureCategory;
            return CurrentRejectionMatches(controller.Status) ? lastObservedRejection.category : "navigation";
        }
        return lastObservedRejection != null && lastObservedRejection.actionId == actionId &&
            lastObservedRejection.operationRevision == operationRevision &&
            Time.realtimeSinceStartup - lastObservedRejection.observedAtRealtime < .1f
            ? lastObservedRejection.category : "navigation";
    }

    private bool CurrentRejectionMatches(string currentReason) =>
        !string.IsNullOrEmpty(currentReason) && lastObservedRejection != null &&
        !string.IsNullOrEmpty(lastObservedRejection.reason) && lastObservedRejection.actionId == actionId &&
        lastObservedRejection.operationRevision == operationRevision &&
        currentReason.EndsWith(lastObservedRejection.reason, StringComparison.Ordinal);

    private void ResetFollowSpeed()
    {
        followVelocity.Reset(); followRequestedSpeed = followRemainingPath = 0; followSpeedLimited = followResumeBodyRequested = false;
        followPauseCategory = "";
        followLastCommandAt = followLastGeometryAt = followLastSpeedLogAt = float.NegativeInfinity;
    }

    private void UpdateFollowSpeed(float now)
    {
        Vector3 ground = avatar.transform.position + Vector3.up * importedReference.groundY * avatar.transform.lossyScale.y;
        followRemainingPath = ArdyRoomFollowMotionPolicy.RemainingPath(ground, plan.Route.Corners, out Vector3 direction);
        if (direction.sqrMagnitude < .0001f) direction = plan.UserGround - ground;
        MeasureUser(out float distance, out _);
        float requested = ArdyRoomFollowMotionPolicy.DesiredSpeed(followRemainingPath, direction, followVelocity.Velocity,
            distance, approachDistance, followVelocity.IsMoving, out followSpeedLimited);
        float dt = float.IsNegativeInfinity(followLastCommandAt) ? ArdyRoomFollowMotionPolicy.CommandInterval : now - followLastCommandAt;
        followRequestedSpeed = ArdyRoomFollowMotionPolicy.SmoothSpeed(followRequestedSpeed, requested, dt);
        if (distance <= approachDistance - .12f) followRequestedSpeed = 0;
        followLastCommandAt = now;
    }

    private void ObserveExecution()
    {
        if (!activeAction || controller == null) return;
        if (controller.OperationRevision != operationRevision)
        { activeAction = false; followActive = false; navigationRecoveryActive = false; SetFact("cancelled", "空间执行已被另一目标替换，旧动作不会认领其结果。"); return; }
        if (navigationRecoveryActive) { ObserveNavigationRecovery(); return; }
        if (approachRetargetSettling) return;
        if (StreamRecovering)
        {
            // The controller owns bounded recovery, including its pose return and restart.
            // Do not cancel again, release the parent authorization, or acquire a gesture here.
            SetFact("follow-recovering", controller.Status);
            return;
        }
        if (followActive && controller.IsContinuousFollowActive &&
            controller.ExecutionState != ArdyRoomExecutionState.Failed && controller.ExecutionState != ArdyRoomExecutionState.Blocked &&
            controller.ExecutionState != ArdyRoomExecutionState.Cancelled)
        {
            // Near-user rest returns the pose, not the parent task or its controller operation.
            if (controller.Stage == "stream-waiting")
            { SetFact("follow-waiting", "持续跟随已启用；正在用户附近等待，移动时会继续调整步速。"); return; }
            if (controller.ExecutionState == ArdyRoomExecutionState.Returning)
            { SetFact("returning", controller.Status); return; }
            if (controller.ExecutionState == ArdyRoomExecutionState.Arrived)
            { SetFact("follow-waiting", "已到达当前交互距离，持续跟随仍保持启用。"); return; }
        }
        if (followActive && controller.RetargetNeedsRestart && !controller.IsBusy &&
            (controller.Player == null || !controller.Player.HasPoseOwnership))
        {
            activeAction = false; followNeedsNewTarget = false; followPausedHead = userHead.position;
            SetFact("follow-replanning", "旧轨迹的安全衔接窗口已结束；身体已释放，接下来从实际位置检查最新目标。" + controller.RetargetStatus);
            return;
        }
        switch (controller.ExecutionState)
        {
            case ArdyRoomExecutionState.Preparing: SetFact("preparing", controller.Status); break;
            case ArdyRoomExecutionState.Generating: SetFact("generating", controller.Status); break;
            case ArdyRoomExecutionState.Moving: SetFact("moving", controller.Status); break;
            case ArdyRoomExecutionState.Returning: SetFact("returning", controller.Status); break;
            case ArdyRoomExecutionState.Arrived:
                activeAction = false;
                MeasureUser(out float distance, out float facing);
                if (followActive)
                {
                    followNeedsNewTarget = false; followPausedHead = userHead.position;
                    SetFact("follow-waiting", "本段行走已结束，持续跟随仍启用；实际距离足够时等待用户移远。");
                }
                else SetFact(ArdyRoomApproachPlanner.GoalSatisfied(distance, facing, approachDistance)
                        ? "arrived" : "goal-not-reached", "行走已结束；是否已到身边并面对用户，以实际距离和朝向观测为准。");
                break;
            case ArdyRoomExecutionState.Blocked:
                if (followActive) PauseFollow("follow-paused", "本段受阻，跟随已暂停；用户移到新的同层位置后再检查。" + controller.Status, true);
                else { activeAction = false; SetFact("blocked", controller.Status); }
                break;
            case ArdyRoomExecutionState.Failed:
                if (followActive) StopMovement("failed", "本段生成或执行失败，持续跟随已结束。" + controller.Status);
                else { activeAction = false; SetFact("failed", controller.Status); }
                break;
            case ArdyRoomExecutionState.Cancelled:
                if (followActive) StopMovement("cancelled", "本段被取消，持续跟随已结束。" + controller.Status);
                else { activeAction = false; SetFact("cancelled", controller.Status); }
                break;
        }
    }

    private bool ControllerAlreadyTerminated()
    {
        // Fail/guard paths already invalidated their worker and initiated any required return.
        // A second caller cancellation would erase the actual reason in RoomTrace and facts.
        return controller != null && !StreamRecovering &&
            (controller.ExecutionState == ArdyRoomExecutionState.Failed ||
             controller.ExecutionState == ArdyRoomExecutionState.Blocked ||
             controller.ExecutionState == ArdyRoomExecutionState.Cancelled);
    }

    private void MeasureUser(out float distance, out float facing)
    {
        distance = facing = -1;
        if (avatar == null || !ValidUser(out _)) return;
        Vector3 direction = userHead.position - avatar.transform.position;
        direction.y = 0;
        distance = direction.magnitude;
        Vector3 forward = avatar.transform.forward; forward.y = 0;
        if (direction.sqrMagnitude > .0001f && forward.sqrMagnitude > .0001f) facing = Vector3.Angle(forward, direction);
    }

    private void RefreshTargetSnapshot()
    {
        bool connected = IsConnected && controller.Avatar == avatar && navigation.EnvironmentRoot == environmentRoot && chat == subscribedChat;
        bool valid = ValidUser(out _);
        Vector3 head = valid ? userHead.position : Vector3.zero;
        Vector3 root = avatar != null ? avatar.transform.position : Vector3.zero;
        Quaternion rotation = avatar != null ? avatar.transform.rotation : Quaternion.identity;
        bool changed = !hasTargetSnapshot || snapshotConnected != connected || snapshotUserValid != valid ||
            snapshotUser != userHead || snapshotNavigation != navigation || Vector3.Distance(snapshotHead, head) > .01f ||
            Mathf.Abs(snapshotDistance - approachDistance) > .001f;
        // Progress along an accepted route is not a new goal. Once idle, a changed root makes a
        // former rejection an old starting-point observation instead of a verdict on this plan.
        if (!activeAction && (Vector3.Distance(snapshotRoot, root) > .01f || Quaternion.Angle(snapshotRotation, rotation) > 1f)) changed = true;
        if (!changed) return;
        hasTargetSnapshot = true;
        snapshotConnected = connected; snapshotUserValid = valid; snapshotUser = userHead; snapshotNavigation = navigation;
        snapshotHead = head; snapshotRoot = root; snapshotRotation = rotation; snapshotDistance = approachDistance;
        currentTargetRevision++;
        currentAssessment = null;
    }

    /// <summary>Invalidate cached geometry after an explicit room edit; this does not move or cancel the avatar.</summary>
    public void InvalidateCurrentTargetAssessment()
    {
        currentTargetRevision++;
        currentAssessment = null;
    }

    /// <summary>
    /// Called on a decision/context read, never from Update. Reuses a three-second snapshot; refresh
    /// forces a read-only check for a new user request. The mutable DTO returned is a detached copy.
    /// </summary>
    public CurrentTargetEvidence ReadCurrentTargetEvidence(bool refresh = false) => BuildCurrentTargetEvidence(refresh, true);

    /// <summary>Live monitor snapshot: rechecks user support, but never starts route planning.</summary>
    public CurrentTargetEvidence PeekCurrentTargetEvidence() => BuildCurrentTargetEvidence(false, false);

    private CurrentTargetEvidence BuildCurrentTargetEvidence(bool refresh, bool allowPlanning)
    {
        RefreshTargetSnapshot();
        float now = Time.realtimeSinceStartup;
        bool connected = snapshotConnected;
        bool valid = ValidUser(out string failure);
        CurrentTargetAssessment assessment;
        if (!connected)
            assessment = new CurrentTargetAssessment { state = "stale", reason = "房间会话未连接或绑定已改变；当前位置不能继续使用旧导航证据。", observedAtRealtime = now, expiresAtRealtime = now, buildCount = assessmentBuildCount };
        else if (ReservesBody)
            assessment = new CurrentTargetAssessment { state = "busy", reason = "当前已有房间动作执行中；不为预览重新规划或自动替换它。", observedAtRealtime = now, expiresAtRealtime = now, buildCount = assessmentBuildCount };
        else if (!valid)
            assessment = new CurrentTargetAssessment { state = "unreachable", reason = failure, observedAtRealtime = now, expiresAtRealtime = now, buildCount = assessmentBuildCount };
        else if (!allowPlanning && (currentAssessment == null || now >= currentAssessment.expiresAtRealtime))
            assessment = new CurrentTargetAssessment { state = currentAssessment == null ? "unchecked" : "stale",
                reason = currentAssessment == null ? "当前目标尚无路线检查；监视面板不会自动规划。" : "此前路线检查已过期；可主动刷新当前地面与路线。",
                observedAtRealtime = now, expiresAtRealtime = now, buildCount = assessmentBuildCount };
        else
        {
            if (refresh || currentAssessment == null || now >= currentAssessment.expiresAtRealtime)
            {
                Vector3 ground = avatar.transform.position + Vector3.up * importedReference.groundY * avatar.transform.lossyScale.y;
                bool reachable = ArdyRoomApproachPlanner.TryPlan(navigation, ground, userHead, approachDistance,
                    out var candidate, out failure, out var observation);
                var next = new CurrentTargetAssessment {
                    state = reachable ? "reachable" : "unreachable", canPlanRoute = reachable,
                    reason = reachable ? "当前几何路线可规划；尚未执行，生成时长与服务检查仍待实际请求验证。" : failure,
                    observedAtRealtime = now, expiresAtRealtime = now + AssessmentLifetime,
                    buildCount = ++assessmentBuildCount, observation = observation,
                    routeLength = reachable ? candidate.Route.Length : -1,
                    plannedGround = reachable ? candidate.GroundTarget : Vector3.zero
                };
                // A moved obstacle can change the current target without moving either actor.
                if (currentAssessment != null && (currentAssessment.state != next.state ||
                    currentAssessment.reason != next.reason || Mathf.Abs(currentAssessment.routeLength - next.routeLength) > .02f ||
                    Vector3.Distance(currentAssessment.plannedGround, next.plannedGround) > .01f)) currentTargetRevision++;
                currentAssessment = next;
            }
            assessment = currentAssessment;
        }
        MeasureUser(out float distance, out float facing);
        bool supported = false;
        var userSupport = new ArdyRoomUserSupportObservation();
        Vector3 currentUserGround = Vector3.zero;
        if (connected && valid)
            supported = ArdyRoomApproachPlanner.TryGetUserGround(navigation, userHead, out currentUserGround, out _, out userSupport) &&
                Mathf.Abs(currentUserGround.y - (avatar.transform.position.y + importedReference.groundY * avatar.transform.lossyScale.y)) <= navigation.MaxRouteHeightVariation;
        var value = new CurrentTargetEvidence {
            executionTargetKind = executionTargetKind, lastSceneTargetResult = LastSceneTargetResult,
            currentTargetRevision = currentTargetRevision, lastAttemptTargetRevision = lastAttemptTargetRevision,
            lastAttemptAppliesToCurrentTarget = lastAttemptTargetRevision == currentTargetRevision,
            attemptStatus = activeAction && !StreamWaiting ? "active" : followActive ? "monitoring" : lastAttemptTargetRevision == currentTargetRevision ? "attempted" : "no-attempt",
            connected = connected, userAnchorValid = supported, userTrackingValid = valid, userSupportVerified = supported, bodyReserved = ReservesBody,
            activeActionId = activeAction ? actionId : "", observedAtRealtime = now,
            desiredUserDistance = approachDistance, actualUserDistance = distance, actualFacingErrorDegrees = facing,
            currentlyNearAndFacingUser = supported && ArdyRoomApproachPlanner.GoalSatisfied(distance, facing, approachDistance),
            actualRoot = avatar != null ? avatar.transform.position : Vector3.zero,
            userHeadWorld = valid ? userHead.position : Vector3.zero, assessment = assessment,
            taskMode = TaskMode, activeTaskId = ActiveTaskId, followActive = followActive,
            followLegCount = followLegCount, controllerOperationRevision = controller != null ? controller.OperationRevision : 0,
            followTargetUpdatedAtRealtime = followTargetUpdatedAt,
            followNextPlanAtRealtime = followActive ? Mathf.Max(now, activeAction && controller.IsContinuousFollowActive
                ? followLastGeometryAt + ArdyRoomFollowMotionPolicy.GeometryInterval
                : followLastDispatchAt + ArdyRoomFollowPolicy.MinimumDispatchInterval) : 0,
            followResumeDistance = approachDistance + ArdyRoomFollowPolicy.ResumeDistanceMargin,
            followMovingResumeDistance = approachDistance + .08f,
            segmentIndex = controller != null ? controller.SegmentIndex : 0,
            segmentCount = controller != null ? controller.SegmentCount : 0,
            preparedSegmentCount = controller != null ? controller.PreparedSegmentCount : 0,
            plannedDurationSeconds = controller != null ? controller.PlannedDurationSeconds : 0,
            playedDurationSeconds = controller != null ? controller.PlayedDurationSeconds : 0,
            waitingForSegment = controller != null && controller.WaitingForSegment,
            poseReturning = controller != null && controller.Player != null && controller.Player.IsReturningToAnimation,
            movingTargetRevision = controller != null ? controller.MovingTargetRevision : 0,
            committedTargetRevision = controller != null ? controller.CommittedTargetRevision : 0,
            movingTargetNeedsRestart = controller != null && controller.RetargetNeedsRestart,
            movingTargetStatus = controller != null ? controller.RetargetStatus : "",
            continuousFollowActive = controller != null && controller.IsContinuousFollowActive,
            followEstimatedUserVelocity = followVelocity.Velocity, followEstimatedUserSpeed = followVelocity.Speed,
            followUserMoving = followVelocity.IsMoving, followRequestedSpeed = followRequestedSpeed,
            followRemainingPath = followRemainingPath, followSpeedLimited = followSpeedLimited,
            followMaximumSpeed = ArdyRoomFollowMotionPolicy.MaximumSpeed, followVelocityState = followVelocity.State,
            activeBlockingCategory = followActive && phase == "follow-paused" ? followPauseCategory : "",
            currentUserSupport = userSupport, currentUserGround = currentUserGround,
            currentUserClearanceRadius = connected ? navigation.AgentRadius + ArdyRoomApproachPlanner.UserBodyRadius + ArdyRoomApproachPlanner.UserClearance : 0,
            streamTelemetry = ReadStreamTelemetry(), lastObservedRejection = ReadRejectionEvidence(),
            navigationRecoveryActive = navigationRecoveryActive, navigationRecoveryAttempted = navigationRecoveryAttempted,
            navigationRecoveryReason = navigationRecoveryReason,
            approachRetargetStatus = approachRetargetStatus, approachRetargetCount = approachRetargetCount, approachRetargetTravel = approachRetargetTravel,
            approachRetargetUpdates = approachRetargetUpdates, approachRetargetRestarts = approachRetargetRestarts
        };
        var detached = JsonUtility.FromJson<CurrentTargetEvidence>(JsonUtility.ToJson(value));
        // Models need the bounded verdict and counts. Individual candidate colliders are retained
        // in execution diagnostics, not repeated in every conversational context.
        detached.assessment.observation?.candidates?.Clear();
        return detached;
    }

    public string DescribeRoomContext()
    {
        var current = ReadCurrentTargetEvidence();
        RoomDiagnostic compactDiagnostic = latestDiagnostic == null ? null :
            JsonUtility.FromJson<RoomDiagnostic>(JsonUtility.ToJson(latestDiagnostic));
        compactDiagnostic?.approachObservation?.candidates?.Clear();
        compactDiagnostic?.sceneTargetObservation?.candidates?.Clear();
        MeasureUser(out float distance, out float facing);
        var support = new ArdyRoomUserSupportObservation();
        bool supportedUser = ValidUser(out _) && IsConnected &&
            ArdyRoomApproachPlanner.TryGetUserGround(navigation, userHead, out Vector3 userGround, out _, out support) &&
            Mathf.Abs(userGround.y - (avatar.transform.position.y + importedReference.groundY * avatar.transform.lossyScale.y)) <= navigation.MaxRouteHeightVariation;
        return JsonUtility.ToJson(new RoomFact {
            targetKind = executionTargetKind, lastSceneTargetResult = LastSceneTargetResult,
            phase = phase, actionId = actionId, reason = reason, lastBodyRejection = lastBodyRejection,
            connected = IsConnected, userAnchorValid = supportedUser, bodyReserved = ReservesBody,
            reachedPlannedStandpoint = phase == "arrived", observedAtRealtime = Time.realtimeSinceStartup,
            resultObservedAtRealtime = factTime,
            currentUserSupport = support, latestExecutionObservation = compactDiagnostic,
            currentTargetRevision = current.currentTargetRevision, lastAttemptTargetRevision = lastAttemptTargetRevision,
            lastAttemptAppliesToCurrentTarget = current.lastAttemptAppliesToCurrentTarget,
            currentAssessment = current.assessment, currentFacts = current,
            lastActionResults = new LastActionResult { actionId = lastAttemptActionId, phase = lastAttemptPhase,
                targetKind = executionTargetKind,
                reason = lastAttemptReason, targetRevision = lastAttemptTargetRevision,
                appliesToCurrentTarget = current.lastAttemptAppliesToCurrentTarget, observedAtRealtime = lastAttemptTime },
            currentlyNearAndFacingUser = supportedUser && ArdyRoomApproachPlanner.GoalSatisfied(distance, facing, approachDistance),
            desiredUserDistance = approachDistance, actualUserDistance = distance, actualFacingErrorDegrees = facing,
            actualRoot = avatar != null ? avatar.transform.position : Vector3.zero,
            userHeadWorld = userHead != null ? userHead.position : Vector3.zero,
            plannedGround = executionTargetKind == "scene-object" && sceneTargetPlan != null ? sceneTargetPlan.GroundTarget :
                plan != null ? plan.GroundTarget : Vector3.zero });
    }

    private void SetFact(string nextPhase, string message)
    {
        if (phase == nextPhase && reason == message && latestDiagnostic != null && latestDiagnostic.actionId == actionId) return;
        phase = nextPhase; reason = message ?? ""; factTime = Time.realtimeSinceStartup;
        if (!string.IsNullOrEmpty(lastAttemptActionId) && actionId == lastAttemptActionId &&
            phase != "ready" && phase != "disconnected" && phase != "replaced")
        { lastAttemptPhase = phase; lastAttemptReason = reason; lastAttemptTime = factTime; }
        Debug.Log("[Room/Dialogue] action=" + actionId + " phase=" + phase + " " + reason, this);
        latestDiagnostic = CaptureDiagnostic();
        string diagnostic = JsonUtility.ToJson(latestDiagnostic);
        Debug.Log("[Room/Trace] " + diagnostic, this);
        if (!string.IsNullOrEmpty(actionId) && (phase == "arrived" || phase == "goal-not-reached" ||
            phase == "unreachable" || phase == "unavailable" || phase == "blocked" || phase == "failed" || phase == "cancelled" || phase == "stopped" ||
            phase == "follow-waiting" || phase == "follow-paused" || phase == "follow-replanning" || phase == "follow-recovering" || phase == "nav-recovering"))
        {
            actionReports.Enqueue(diagnostic);
            while (actionReports.Count > 12) actionReports.Dequeue();
        }
    }

    private RoomDiagnostic CaptureDiagnostic()
    {
        MeasureUser(out float distance, out float facing);
        var value = new RoomDiagnostic {
            targetKind = executionTargetKind, captureId = sceneCaptureId, sceneTargetId = sceneSurfaceId,
            sceneSurfacePoint = sceneSurfacePoint, sceneTargetObservation = sceneTargetObservation,
            actionId = actionId, intent = actionIntent, phase = phase, reason = reason,
            stage = hasControllerOperation && controller != null ? controller.Stage : executionTargetKind == "scene-object" ? "scene-target-validation" : "user-target-validation",
            executionReason = hasControllerOperation && controller != null ? controller.ExecutionReason.ToString() : "NotSubmittedToController",
            observedUtc = DateTime.UtcNow.ToString("o"), elapsedSeconds = Mathf.Max(0, Time.realtimeSinceStartup - actionStartedAt),
            navigationId = navigation != null ? navigation.GetInstanceID() : 0,
            operationRevision = hasControllerOperation && controller != null ? controller.OperationRevision : 0,
            actualUserDistance = distance, facingError = facing,
            userAnchor = userHead != null ? userHead.name : "",
            userHead = userHead != null ? userHead.position : Vector3.zero,
            avatarGround = avatar != null ? avatar.transform.position : Vector3.zero,
            routeLength = executionTargetKind == "scene-object" && sceneTargetPlan != null ? sceneTargetPlan.Route.Length :
                plan != null && plan.Route != null ? plan.Route.Length : -1,
            sampledHead = plan != null ? plan.FacePoint : Vector3.zero,
            plannedGround = executionTargetKind == "scene-object" && sceneTargetPlan != null ? sceneTargetPlan.GroundTarget :
                plan != null ? plan.GroundTarget : Vector3.zero,
            taskMode = TaskMode, followActive = followActive, followLegCount = followLegCount,
            followTargetUpdatedAtRealtime = followTargetUpdatedAt,
            movingTargetRevision = controller != null ? controller.MovingTargetRevision : 0,
            committedTargetRevision = controller != null ? controller.CommittedTargetRevision : 0,
            movingTargetStatus = controller != null ? controller.RetargetStatus : "",
            poseReturning = controller != null && controller.Player != null && controller.Player.IsReturningToAnimation,
            continuousFollowActive = controller != null && controller.IsContinuousFollowActive,
            followEstimatedUserVelocity = followVelocity.Velocity, followEstimatedUserSpeed = followVelocity.Speed,
            followUserMoving = followVelocity.IsMoving, followRequestedSpeed = followRequestedSpeed,
            followRemainingPath = followRemainingPath, followSpeedLimited = followSpeedLimited, followVelocityState = followVelocity.State,
            streamTelemetry = ReadStreamTelemetry(), lastObservedRejection = ReadRejectionEvidence(),
            navigationRecoveryActive = navigationRecoveryActive, navigationRecoveryAttempted = navigationRecoveryAttempted,
            navigationRecoveryReason = navigationRecoveryReason,
            approachRetargetStatus = approachRetargetStatus, approachRetargetCount = approachRetargetCount, approachRetargetTravel = approachRetargetTravel,
            approachRetargetUpdates = approachRetargetUpdates, approachRetargetRestarts = approachRetargetRestarts
        };
        value.approachObservation = attemptObservation;
        if (avatar != null && importedReference != null)
            value.avatarGround += Vector3.up * importedReference.groundY * avatar.transform.lossyScale.y;
        if (userHead != null)
            for (Transform parent = userHead.parent; parent != null; parent = parent.parent)
                value.userAnchor = parent.name + "/" + value.userAnchor;
        if (plan != null && userHead != null)
        {
            value.userDisplacementXZ = ArdyRoomApproachPlanner.UserGroundDisplacement(plan.FacePoint, userHead.position);
            value.userDisplacementY = userHead.position.y - plan.FacePoint.y;
        }
        if (IsConnected && ValidUser(out _))
            ArdyRoomApproachPlanner.TryGetUserGround(navigation, userHead, out _, out _, out value.support);
        return value;
    }

    private ArdyRoomStreamTelemetry ReadStreamTelemetry()
    {
        if (controller == null) return null;
        var value = controller.StreamTelemetry;
        return value == null ? null : JsonUtility.FromJson<ArdyRoomStreamTelemetry>(JsonUtility.ToJson(value));
    }

    private RoomRejectionEvidence ReadRejectionEvidence()
    {
        if (lastObservedRejection == null) return null;
        var value = JsonUtility.FromJson<RoomRejectionEvidence>(JsonUtility.ToJson(lastObservedRejection));
        value.appliesToCurrentTarget = value.targetRevision == currentTargetRevision &&
            value.actionId == actionId && controller != null && value.operationRevision == controller.OperationRevision;
        return value;
    }

    private void RecordRejection(string source, string category, string message, bool hasPoint, Vector3 point)
    {
        RefreshTargetSnapshot();
        lastObservedRejection = new RoomRejectionEvidence { source = source, category = category, reason = message,
            actionId = actionId, operationRevision = controller != null ? controller.OperationRevision : 0,
            targetRevision = currentTargetRevision, observedAtRealtime = Time.realtimeSinceStartup,
            hasPoint = hasPoint, point = point };
    }

    private void RecordPlanningRejection(ArdyRoomApproachObservation observation, string message)
    {
        ArdyRoomRouteDiagnostic check = PlanStartBoundary(observation) ?? observation?.preflightFailure;
        if (check == null || !check.hasSample) check = observation?.stationaryAttempt;
        if (check == null || !check.hasSample)
            if (observation?.candidates != null)
                foreach (var candidate in observation.candidates)
                    if (!candidate.accepted && candidate.navigation != null && candidate.navigation.hasSample)
                    { check = candidate.navigation; break; }
        RecordRejection("approach-planning-check", "navigation", message,
            check != null && check.hasSample, check != null && check.hasSample ? check.sample : Vector3.zero);
    }

    public void Disconnect(string cause = "room-disconnected")
    {
        StopMovement("disconnected", cause);
        if (subscribedChat != null)
        {
            subscribedChat.ConfigureRoomMotionOutput(false);
            subscribedChat.MotionIntentRequested -= OnIntent;
            subscribedChat.MotionCancelled -= OnCancelled;
            subscribedChat.MotionStateContextRequested -= DescribeRoomContext;
            subscribedChat = null;
        }
        if (controller != null) { controller.AdditionalStepGuard = null; controller.enabled = false; }
        if (navigation != null) navigation.Clear();
        if (host != null) { host.SetActive(false); Destroy(host); }
        host = null; controller = null; navigation = null; plan = null; boundUser = null;
    }

    private void OnDisable() => Disconnect("room-bridge-disabled");
    private void OnDestroy() => Disconnect("room-bridge-destroyed");
}
