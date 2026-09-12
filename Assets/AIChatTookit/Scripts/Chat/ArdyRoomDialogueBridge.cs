using System;
using System.Collections.Generic;
using NeEEvA.Motion;
using NeEEvA.Player;
using UniVRM10;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Opt-in, scene-owned conversation locomotion. The Editor window is only a setup tool.</summary>
[DisallowMultipleComponent, DefaultExecutionOrder(13000)]
public sealed class ArdyRoomDialogueBridge : MonoBehaviour
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
    public bool ReservesBody => IsConnected && activeAction;
    public ArdyRoomLocomotionController Controller => controller;
    public ArdyRoomNavigation Navigation => navigation;
    public string Status => reason;
    public string Phase => phase;
    public string ActionId => actionId;
    public string RecentDiagnosticText => string.Join("\n", actionReports.ToArray());
    public string DiagnosticSummary => latestDiagnostic == null ? "尚无动作诊断" :
        $"动作 {latestDiagnostic.actionId}\n阶段 {latestDiagnostic.stage} · {latestDiagnostic.phase}\n{latestDiagnostic.reason}\n" +
        $"路线 {latestDiagnostic.routeLength:F2} 米；距用户 {latestDiagnostic.actualUserDistance:F2} 米；朝向差 {latestDiagnostic.facingError:F1}°\n" +
        (latestDiagnostic.support.found ? $"用户下方：{latestDiagnostic.support.objectName}；视点距其 {latestDiagnostic.support.headHeight:F3} 米\n" : "用户下方尚未确认支撑面\n") +
        $"地面位置变化 {latestDiagnostic.userDisplacementXZ:F3} 米；视点高度变化 {latestDiagnostic.userDisplacementY:F3} 米";

    private GameObject host;
    private ArdyRoomNavigation navigation;
    private ArdyRoomLocomotionController controller;
    private ChatSample subscribedChat;
    private ArdyRoomApproachPlan plan;
    private ArdyRoomApproachObservation attemptObservation;
    private Transform boundUser;
    private string actionId = "", phase = "disconnected", reason = "尚未启用对话房间移动";
    private string lastBodyRejection = "";
    private bool activeAction;
    private int operationRevision;
    private float factTime;
    private float actionStartedAt;
    private bool hasControllerOperation;
    private RoomDiagnostic latestDiagnostic;
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

    /// <summary>Read-only evidence about the current scene target, never a promise of generated motion.</summary>
    [Serializable] public sealed class CurrentTargetAssessment
    {
        public string state = "unchecked", reason = "当前位置尚未检查。";
        public bool canPlanRoute, budgetChecked, executionGuaranteed;
        public float observedAtRealtime, expiresAtRealtime, routeLength = -1;
        public int buildCount;
        public Vector3 plannedGround;
        public ArdyRoomApproachObservation observation;
        public string scope = "Read-only current geometric route check. Generation duration, service capability, pose history and playback have not been validated; reachable does not mean movement executed or guaranteed.";
    }

    [Serializable] public sealed class CurrentTargetEvidence
    {
        public int currentTargetRevision, lastAttemptTargetRevision;
        public bool connected, userAnchorValid, bodyReserved, lastAttemptAppliesToCurrentTarget, currentlyNearAndFacingUser;
        public string attemptStatus, activeActionId;
        public float observedAtRealtime, desiredUserDistance, actualUserDistance, actualFacingErrorDegrees;
        public Vector3 actualRoot, userHeadWorld;
        public CurrentTargetAssessment assessment;
        public string scope = "Current engine observations for this target revision. no-attempt means no movement has been submitted for this target; assessment is not an action result.";
    }

    [Serializable] private sealed class LastActionResult
    {
        public string actionId, phase, reason;
        public int targetRevision;
        public bool appliesToCurrentTarget;
        public float observedAtRealtime;
        public string scope = "Historical result of the last actual room approach; do not reuse its rejection for a different current target. Read currentFacts.assessment for the present route.";
    }

    [Serializable] private sealed class RoomDiagnostic
    {
        public string actionId, phase, stage, reason, executionReason, observedUtc, userAnchor;
        public int operationRevision, navigationId;
        public float elapsedSeconds, routeLength = -1, actualUserDistance = -1, facingError = -1,
            userDisplacementXZ, userDisplacementY;
        public Vector3 avatarGround, userHead, sampledHead, plannedGround;
        public ArdyRoomUserSupportObservation support = new ArdyRoomUserSupportObservation();
        public ArdyRoomApproachObservation approachObservation;
    }

    [Serializable] private sealed class RoomFact
    {
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
        public string scope = "Same-level room approach to a sampled user position; not following or visual perception. Coordinates come from the engine, not the character's eyes.";
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
            SetFact("ready", "房间移动已连接，可以请求走近用户或停止移动。");
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
        actionId = id ?? "";
        lastAttemptActionId = actionId;
        lastAttemptTargetRevision = currentTargetRevision;
        lastAttemptPhase = "preparing"; lastAttemptReason = "正在验证本次实际走近请求。";
        lastAttemptTime = Time.realtimeSinceStartup;
        actionStartedAt = Time.realtimeSinceStartup;
        hasControllerOperation = false;
        plan = null;
        attemptObservation = null;
        lastBodyRejection = "";
        if (!ValidUser(out string failure)) { SetFact("unavailable", failure); return; }
        Vector3 ground = avatar.transform.position + Vector3.up * importedReference.groundY * avatar.transform.lossyScale.y;
        if (!ArdyRoomApproachPlanner.TryPlan(navigation, ground, userHead, approachDistance, out plan, out failure, out attemptObservation))
        { SetFact("unreachable", failure); return; }
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
            controller.MoveTo(plan.GroundTarget, plan.FacePoint);
            operationRevision = controller.OperationRevision;
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

    /// <summary>Program-owned stop identity, shared by structured tasks and the legacy motion event.</summary>
    public void StopForTask(string id)
    {
        actionId = id ?? "";
        StopMovement();
    }

    public void StopMovement(string result = "stopped", string explanation = "已明确停止移动，保留当前位置。")
    {
        if (activeAction && !string.IsNullOrEmpty(lastAttemptActionId))
        { lastAttemptPhase = result; lastAttemptReason = explanation; lastAttemptTime = Time.realtimeSinceStartup; }
        if (activeAction && controller != null) controller.Cancel();
        activeAction = false;
        SetFact(result, explanation);
    }

    private void OnIntent(DialogueMotionIntent intent)
    {
        if (!IsConnected) return;
        if (intent.Name == "approach") Approach(intent.ActionId);
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
        if (!activeAction || plan == null) return null;
        string failure = CheckUserSnapshot();
        if (failure != null) return failure;
        Vector3 currentGround = new Vector3(userHead.position.x, plan.UserGround.y, userHead.position.z);
        return ArdyRoomApproachPlanner.StepKeepsUserClearance(from, to, currentGround, plan.UserClearanceRadius)
            ? null : "用户已进入当前路线的身体净距范围，已停止移动。";
    }

    private string CheckUserSnapshot()
    {
        if (controller.Avatar != avatar || navigation.EnvironmentRoot != environmentRoot || chat != subscribedChat)
            return "空间会话绑定引用已改变，当前动作已停止；请重新连接。";
        if (!ValidUser(out string failure)) return failure;
        if (userHead != boundUser) return "用户视点绑定已改变，本次走近已停止；请重新确定目标。";
        float displacement = ArdyRoomApproachPlanner.UserGroundDisplacement(plan.FacePoint, userHead.position);
        if (displacement > .35f)
            return $"用户地面位置较本次目标快照移动了 {displacement:F3} 米，超过 0.350 米；本次走近已停止，请重新呼唤（连续跟随尚未启用）。";
        if (!ArdyRoomApproachPlanner.TryGetUserGround(navigation, userHead, out Vector3 ground, out failure)) return failure;
        if (Mathf.Abs(ground.y - plan.UserGround.y) > navigation.MaxRouteHeightVariation)
            return "用户已离开原来的同层支撑地面，本次走近已停止。";
        return null;
    }

    private void LateUpdate()
    {
        if (!activeAction || !IsConnected) return;
        string failure = CheckUserSnapshot();
        if (failure != null) { StopMovement("cancelled", failure); return; }
        ObserveExecution();
    }

    private void ObserveExecution()
    {
        if (!activeAction || controller == null) return;
        if (controller.OperationRevision != operationRevision)
        { activeAction = false; SetFact("cancelled", "空间执行已被另一目标替换，旧动作不会认领其结果。"); return; }
        switch (controller.ExecutionState)
        {
            case ArdyRoomExecutionState.Preparing: SetFact("preparing", controller.Status); break;
            case ArdyRoomExecutionState.Generating: SetFact("generating", controller.Status); break;
            case ArdyRoomExecutionState.Moving: SetFact("moving", controller.Status); break;
            case ArdyRoomExecutionState.Returning: SetFact("returning", controller.Status); break;
            case ArdyRoomExecutionState.Arrived:
                activeAction = false;
                MeasureUser(out float distance, out float facing);
                SetFact(distance >= approachDistance - .25f && distance <= approachDistance + .25f && facing >= 0 && facing <= 15f
                    ? "arrived" : "goal-not-reached", "行走已结束；是否已到身边并面对用户，以实际距离和朝向观测为准。");
                break;
            case ArdyRoomExecutionState.Blocked: activeAction = false; SetFact("blocked", controller.Status); break;
            case ArdyRoomExecutionState.Failed: activeAction = false; SetFact("failed", controller.Status); break;
            case ArdyRoomExecutionState.Cancelled: activeAction = false; SetFact("cancelled", controller.Status); break;
        }
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
    public CurrentTargetEvidence ReadCurrentTargetEvidence(bool refresh = false)
    {
        RefreshTargetSnapshot();
        float now = Time.realtimeSinceStartup;
        bool connected = snapshotConnected;
        bool valid = ValidUser(out string failure);
        CurrentTargetAssessment assessment;
        if (!connected)
            assessment = new CurrentTargetAssessment { state = "stale", reason = "房间会话未连接或绑定已改变；当前位置不能继续使用旧导航证据。", observedAtRealtime = now, expiresAtRealtime = now, buildCount = assessmentBuildCount };
        else if (activeAction)
            assessment = new CurrentTargetAssessment { state = "busy", reason = "当前已有房间动作执行中；不为预览重新规划或自动替换它。", observedAtRealtime = now, expiresAtRealtime = now, buildCount = assessmentBuildCount };
        else if (!valid)
            assessment = new CurrentTargetAssessment { state = "unreachable", reason = failure, observedAtRealtime = now, expiresAtRealtime = now, buildCount = assessmentBuildCount };
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
        if (connected && valid)
            supported = ArdyRoomApproachPlanner.TryGetUserGround(navigation, userHead, out Vector3 ground, out _) &&
                Mathf.Abs(ground.y - (avatar.transform.position.y + importedReference.groundY * avatar.transform.lossyScale.y)) <= navigation.MaxRouteHeightVariation;
        var value = new CurrentTargetEvidence {
            currentTargetRevision = currentTargetRevision, lastAttemptTargetRevision = lastAttemptTargetRevision,
            lastAttemptAppliesToCurrentTarget = lastAttemptTargetRevision == currentTargetRevision,
            attemptStatus = activeAction ? "active" : lastAttemptTargetRevision == currentTargetRevision ? "attempted" : "no-attempt",
            connected = connected, userAnchorValid = supported, bodyReserved = activeAction && connected,
            activeActionId = activeAction ? actionId : "", observedAtRealtime = now,
            desiredUserDistance = approachDistance, actualUserDistance = distance, actualFacingErrorDegrees = facing,
            currentlyNearAndFacingUser = supported && distance >= approachDistance - .25f && distance <= approachDistance + .25f && facing >= 0 && facing <= 15,
            actualRoot = avatar != null ? avatar.transform.position : Vector3.zero,
            userHeadWorld = valid ? userHead.position : Vector3.zero, assessment = assessment
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
        MeasureUser(out float distance, out float facing);
        var support = new ArdyRoomUserSupportObservation();
        bool supportedUser = ValidUser(out _) && IsConnected &&
            ArdyRoomApproachPlanner.TryGetUserGround(navigation, userHead, out Vector3 userGround, out _, out support) &&
            Mathf.Abs(userGround.y - (avatar.transform.position.y + importedReference.groundY * avatar.transform.lossyScale.y)) <= navigation.MaxRouteHeightVariation;
        return JsonUtility.ToJson(new RoomFact {
            phase = phase, actionId = actionId, reason = reason, lastBodyRejection = lastBodyRejection,
            connected = IsConnected, userAnchorValid = supportedUser, bodyReserved = ReservesBody,
            reachedPlannedStandpoint = phase == "arrived", observedAtRealtime = Time.realtimeSinceStartup,
            resultObservedAtRealtime = factTime,
            currentUserSupport = support, latestExecutionObservation = compactDiagnostic,
            currentTargetRevision = current.currentTargetRevision, lastAttemptTargetRevision = lastAttemptTargetRevision,
            lastAttemptAppliesToCurrentTarget = current.lastAttemptAppliesToCurrentTarget,
            currentAssessment = current.assessment, currentFacts = current,
            lastActionResults = new LastActionResult { actionId = lastAttemptActionId, phase = lastAttemptPhase,
                reason = lastAttemptReason, targetRevision = lastAttemptTargetRevision,
                appliesToCurrentTarget = current.lastAttemptAppliesToCurrentTarget, observedAtRealtime = lastAttemptTime },
            currentlyNearAndFacingUser = supportedUser && distance >= approachDistance - .25f && distance <= approachDistance + .25f && facing >= 0 && facing <= 15,
            desiredUserDistance = approachDistance, actualUserDistance = distance, actualFacingErrorDegrees = facing,
            actualRoot = avatar != null ? avatar.transform.position : Vector3.zero,
            userHeadWorld = userHead != null ? userHead.position : Vector3.zero,
            plannedGround = plan != null ? plan.GroundTarget : Vector3.zero });
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
            phase == "unreachable" || phase == "unavailable" || phase == "blocked" || phase == "failed" || phase == "cancelled" || phase == "stopped"))
        {
            actionReports.Enqueue(diagnostic);
            while (actionReports.Count > 12) actionReports.Dequeue();
        }
    }

    private RoomDiagnostic CaptureDiagnostic()
    {
        MeasureUser(out float distance, out float facing);
        var value = new RoomDiagnostic {
            actionId = actionId, phase = phase, reason = reason,
            stage = hasControllerOperation && controller != null ? controller.Stage : "user-target-validation",
            executionReason = hasControllerOperation && controller != null ? controller.ExecutionReason.ToString() : "NotSubmittedToController",
            observedUtc = DateTime.UtcNow.ToString("o"), elapsedSeconds = Mathf.Max(0, Time.realtimeSinceStartup - actionStartedAt),
            navigationId = navigation != null ? navigation.GetInstanceID() : 0,
            operationRevision = hasControllerOperation && controller != null ? controller.OperationRevision : 0,
            actualUserDistance = distance, facingError = facing,
            userAnchor = userHead != null ? userHead.name : "",
            userHead = userHead != null ? userHead.position : Vector3.zero,
            avatarGround = avatar != null ? avatar.transform.position : Vector3.zero,
            routeLength = plan != null && plan.Route != null ? plan.Route.Length : -1,
            sampledHead = plan != null ? plan.FacePoint : Vector3.zero,
            plannedGround = plan != null ? plan.GroundTarget : Vector3.zero
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
