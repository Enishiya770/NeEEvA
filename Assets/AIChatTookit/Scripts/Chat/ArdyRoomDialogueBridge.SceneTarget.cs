using System;
using NeEEvA.Motion;
using UnityEngine;

public sealed partial class ArdyRoomDialogueBridge
{
    [Serializable]
    public sealed class SceneTargetResult
    {
        public string actionId, captureId, targetId, targetKind = "scene-object", status, reason;
        public string motionKind = "approach-object", requestedDirection = "", viewPurpose = "";
        public float requestedDistance;
        public Vector3 startGround, requestedGround, actualOffset;
        public bool terminal, reached;
        public int controllerOperationRevision;
        public float observedAtRealtime, actualStandpointError = -1, actualFacingErrorDegrees = -1;
        public Vector3 visibleSurfacePoint, plannedGround, actualRoot;
        public string scope = "Actual scene-object floor movement result; motionKind distinguishes approach from an explicit relative observation offset. Reached refers only to the validated floor station and facing; it does not prove semantic object identity, touching, a fresh view, or better visibility. User proximity is unrelated to this goal.";
    }

    public event Action<SceneTargetResult> SceneTargetCompleted;
    public SceneTargetResult LastSceneTargetResult => CopySceneTargetResult(lastSceneTargetResult);
    public bool IsApproachingSceneTarget => sceneTargetActive;
    public string TargetKind => executionTargetKind;
    public string SceneTargetMotionKind => string.IsNullOrEmpty(sceneViewDirection) ? "approach-object" : "reposition-view";

    private string executionTargetKind = "user";
    private bool sceneTargetActive;
    private int sceneTargetEpoch, sceneTargetOperationRevision;
    private string sceneTargetActionId = "", sceneCaptureId = "", sceneSurfaceId = "";
    private Vector3 sceneSurfacePoint;
    private string sceneViewDirection, sceneViewPurpose;
    private float sceneViewDistance;
    private Vector3 sceneStartGround;
    private Func<string> sceneTargetValidator;
    private ArdyRoomObjectApproachPlan sceneTargetPlan;
    private ArdyRoomObjectApproachObservation sceneTargetObservation;
    private SceneTargetResult lastSceneTargetResult;

    /// <summary>
    /// Accepts only an engine-grounded surface tied to an exact observation. The mandatory validator
    /// must verify its scene/object identity and unchanged geometry, not require continuous FOV while
    /// walking. This never changes userHead or converts a furniture surface into a foot destination.
    /// A submitted attempt produces exactly one terminal event, including synchronous rejection.
    /// </summary>
    public bool ApproachSceneTarget(string id, string captureId, string targetId, Vector3 visibleSurfacePoint,
        Func<string> validateTarget, out string failure, float standOff = .8f)
        => SubmitSceneTarget(id, captureId, targetId, visibleSurfacePoint, validateTarget, out failure, standOff, null, 0, null);

    public bool RepositionSceneTarget(string id, string captureId, string targetId, Vector3 visibleSurfacePoint,
        string direction, float distance, string purpose, Func<string> validateTarget, out string failure)
    {
        if ((direction != "farther" && direction != "closer" && direction != "left" && direction != "right") ||
            float.IsNaN(distance) || float.IsInfinity(distance) || distance < ArdyRoomViewPositionPlanner.MinimumDistance ||
            distance > ArdyRoomViewPositionPlanner.MaximumDistance ||
            (purpose != "see-whole" && purpose != "read-detail" && purpose != "avoid-occlusion"))
            return RejectSceneTarget(id, captureId, targetId, visibleSurfacePoint, "观察站位调整的方向、距离或目的无效。", out failure, direction ?? "", distance, purpose);
        return SubmitSceneTarget(id, captureId, targetId, visibleSurfacePoint, validateTarget, out failure, .8f, direction, distance, purpose);
    }

    private bool SubmitSceneTarget(string id, string captureId, string targetId, Vector3 visibleSurfacePoint,
        Func<string> validateTarget, out string failure, float standOff, string viewDirection, float viewDistance, string viewPurpose)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(captureId) || string.IsNullOrEmpty(targetId) ||
            !Finite(visibleSurfacePoint) || validateTarget == null)
            return RejectSceneTarget(id, captureId, targetId, visibleSurfacePoint, "缺少有效视觉观测、已定位物体或当前目标有效性检查。", out failure, viewDirection, viewDistance, viewPurpose);
        if (!IsConnected || controller.Avatar != avatar || navigation.EnvironmentRoot != environmentRoot || chat != subscribedChat)
            return RejectSceneTarget(id, captureId, targetId, visibleSurfacePoint, "房间移动尚未连接或场景绑定已改变。", out failure, viewDirection, viewDistance, viewPurpose);
        if (activeAction || followActive || ReservesBody || controller.IsBusy || controller.IsMoving)
            return RejectSceneTarget(id, captureId, targetId, visibleSurfacePoint, "已有空间任务或身体姿态尚未释放；本次视觉目标没有替换它。", out failure, viewDirection, viewDistance, viewPurpose);
        string targetFailure = ValidateSceneTargetCallback(validateTarget);
        if (targetFailure != null)
            return RejectSceneTarget(id, captureId, targetId, visibleSurfacePoint, targetFailure, out failure, viewDirection, viewDistance, viewPurpose);
        if (!ValidUser(out failure))
            return RejectSceneTarget(id, captureId, targetId, visibleSurfacePoint, failure, out failure, viewDirection, viewDistance, viewPurpose);

        ResetNavigationRecovery();
        ResetApproachRetarget();
        executionTargetKind = "scene-object";
        sceneTargetEpoch++;
        sceneTargetActive = true;
        sceneTargetActionId = actionId = id;
        actionIntent = viewDirection == null ? "approach-object" : "reposition-view";
        sceneCaptureId = captureId; sceneSurfaceId = targetId; sceneSurfacePoint = visibleSurfacePoint;
        sceneViewDirection = viewDirection; sceneViewDistance = viewDistance; sceneViewPurpose = viewPurpose;
        sceneTargetValidator = validateTarget; sceneTargetPlan = null; sceneTargetObservation = null;
        sceneTargetOperationRevision = 0;
        plan = null; attemptObservation = null; hasControllerOperation = false; lastBodyRejection = "";
        lastAttemptActionId = actionId; lastAttemptTargetRevision = -1;
        actionStartedAt = lastAttemptTime = Time.realtimeSinceStartup;
        lastAttemptPhase = "preparing"; lastAttemptReason = "正在验证视觉目标附近的同层地面站位。";
        boundUser = userHead;
        Vector3 ground = avatar.transform.position + Vector3.up * importedReference.groundY * avatar.transform.lossyScale.y;
        sceneStartGround = ground;
        bool planned = viewDirection == null ? ArdyRoomObjectApproachPlanner.TryPlan(navigation, ground, visibleSurfacePoint, userHead, standOff,
            out sceneTargetPlan, out failure, out sceneTargetObservation) :
            ArdyRoomViewPositionPlanner.TryPlan(navigation, ground, visibleSurfacePoint, userHead, viewDirection, viewDistance,
                out sceneTargetPlan, out failure, out sceneTargetObservation);
        if (!planned)
        {
            string rejection = failure;
            SetFact("unreachable", rejection);
            FinishSceneTarget("unreachable", rejection);
            return false;
        }
        // A grounded target may be destroyed or edited while planning callbacks run.
        targetFailure = CheckSceneTargetSnapshot();
        if (targetFailure != null)
        {
            failure = targetFailure;
            SetFact("cancelled", targetFailure);
            FinishSceneTarget("cancelled", targetFailure);
            return false;
        }
        var live = avatar.GetComponent<ArdyLiveMotionController>();
        if (live != null) live.Cancel("scene-object-approach");
        activeAction = true;
        SetFact("preparing", viewDirection == null ? "已接受视觉目标，正在生成走到其附近地面并面向目标的动作。" :
            "已接受明确方向的观察站位调整，正在生成沿路面向目标的小范围移动；视野是否改善仍需新图确认。");
        try
        {
            controller.WalkingSpeed = walkingSpeed;
            if (viewDirection == null) controller.MoveTo(sceneTargetPlan.GroundTarget, sceneTargetPlan.FacePoint);
            else controller.MoveToFacingTarget(sceneTargetPlan.GroundTarget, sceneTargetPlan.FacePoint);
            operationRevision = controller.OperationRevision;
            sceneTargetOperationRevision = operationRevision;
            hasControllerOperation = true;
            ObserveSceneTargetExecution();
            failure = null;
            return true;
        }
        catch (Exception error)
        {
            failure = error.Message;
            StopMovement("failed", failure);
            return false;
        }
    }

    private bool RejectSceneTarget(string id, string captureId, string targetId, Vector3 point, string message, out string failure,
        string viewDirection = null, float viewDistance = 0, string viewPurpose = null)
    {
        failure = message;
        var value = new SceneTargetResult { actionId = id ?? "", captureId = captureId ?? "", targetId = targetId ?? "",
            motionKind = viewDirection == null ? "approach-object" : "reposition-view", requestedDirection = viewDirection ?? "",
            requestedDistance = float.IsNaN(viewDistance) || float.IsInfinity(viewDistance) ? 0 : viewDistance, viewPurpose = viewPurpose ?? "",
            status = "rejected", reason = message, terminal = true, reached = false, visibleSurfacePoint = Finite(point) ? point : Vector3.zero,
            observedAtRealtime = Time.realtimeSinceStartup, actualRoot = avatar != null ? avatar.transform.position : Vector3.zero };
        lastSceneTargetResult = value;
        Debug.Log("[Room/SceneTarget] " + JsonUtility.ToJson(value), this);
        EmitSceneTargetResult(value);
        return false;
    }

    private static string ValidateSceneTargetCallback(Func<string> validate)
    {
        if (validate == null) return "视觉目标有效性检查已失效。";
        try { return validate(); }
        catch (Exception error) { return "视觉目标检查失败：" + error.Message; }
    }

    private string CheckSceneTargetSnapshot()
    {
        if (!sceneTargetActive || sceneTargetPlan == null) return "当前物体接近任务缺少有效站位计划。";
        if (!IsConnected || controller.Avatar != avatar || navigation.EnvironmentRoot != environmentRoot || chat != subscribedChat)
            return "视觉接近的场景或角色绑定已改变。";
        if (userHead != boundUser) return "用户身体安全锚点绑定已改变。";
        if (!ValidUser(out string failure)) return failure;
        return ValidateSceneTargetCallback(sceneTargetValidator);
    }

    private string GuardSceneTargetStep(Vector3 from, Vector3 to)
    {
        // A foreign controller invocation owns its own route. The old task must neither audit nor cancel it.
        if (hasControllerOperation && controller.OperationRevision != operationRevision) return null;
        string failure = CheckSceneTargetSnapshot();
        if (failure != null) return failure;
        if (!TryGetTrackedUserClearanceCenter(from.y, out var currentUserCenter, out failure)) return failure;
        if (ArdyRoomApproachPlanner.StepKeepsUserClearance(from, to, currentUserCenter, sceneTargetPlan.UserClearanceRadius)) return null;
        const string message = "用户已进入视觉接近路线的身体净距范围，已停止移动。";
        RecordRejection("scene-target-user-clearance", "user-clearance", message, true, to);
        return message;
    }

    /// <summary>Shared by object approach and owned in-place orientation; never certifies user support.</summary>
    private bool TryGetTrackedUserClearanceCenter(float actorGroundY, out Vector3 center, out string failure)
    {
        center = default;
        if (!ValidUser(out failure)) return false;
        return ArdyRoomObjectApproachPlanner.TryGetUserClearanceCenter(userHead, actorGroundY, out center, out failure);
    }

    private void TickSceneTarget()
    {
        if (!sceneTargetActive || !activeAction) return;
        if (controller.OperationRevision != operationRevision)
        {
            activeAction = false;
            hasControllerOperation = false;
            SetFact("cancelled", "视觉接近已被另一执行替换，旧任务没有取消或认领新动作。");
            FinishSceneTarget("cancelled", reason);
            return;
        }
        // Preserve an already recorded real failure instead of replacing it with a later check.
        if (ControllerAlreadyTerminated()) { ObserveSceneTargetExecution(); return; }
        int epoch = sceneTargetEpoch;
        string failure = CheckSceneTargetSnapshot();
        if (!sceneTargetActive || epoch != sceneTargetEpoch) return;
        if (failure != null) { StopMovement("cancelled", failure); return; }
        ObserveSceneTargetExecution();
    }

    private void ObserveSceneTargetExecution()
    {
        if (!sceneTargetActive || controller == null) return;
        switch (controller.ExecutionState)
        {
            case ArdyRoomExecutionState.Preparing: SetFact("preparing", controller.Status); break;
            case ArdyRoomExecutionState.Generating: SetFact("generating", controller.Status); break;
            case ArdyRoomExecutionState.Moving: SetFact("moving", controller.Status); break;
            case ArdyRoomExecutionState.Returning: SetFact("returning", controller.Status); break;
            case ArdyRoomExecutionState.Arrived:
                activeAction = false;
                MeasureSceneTarget(out float distance, out float facing);
                float tolerance = sceneViewDirection == null ? controller.ArrivalTolerance + .03f : ArdyRoomLocomotionController.ViewPositionArrivalTolerance + .005f;
                bool reached = distance <= tolerance && facing >= 0 && facing <= 15f;
                string arrivedPhase = reached ? "arrived" : "goal-not-reached";
                string arrivedReason = reached ? (sceneViewDirection == null ? "已实际到达视觉目标附近的地面站位并面向目标；仍需新视野确认所见。" :
                    "已实际完成请求方向的站位调整并面向原目标；仍需新图确认是否看全、看清或避开遮挡。") :
                    "行走结束，但视觉目标站位或朝向误差尚未达到要求。";
                SetFact(arrivedPhase, arrivedReason);
                FinishSceneTarget(arrivedPhase, arrivedReason, reached);
                break;
            case ArdyRoomExecutionState.Blocked:
                activeAction = false; SetFact("blocked", controller.Status); FinishSceneTarget("blocked", controller.Status); break;
            case ArdyRoomExecutionState.Failed:
                activeAction = false; SetFact("failed", controller.Status); FinishSceneTarget("failed", controller.Status); break;
            case ArdyRoomExecutionState.Cancelled:
                activeAction = false; SetFact("cancelled", controller.Status); FinishSceneTarget("cancelled", controller.Status); break;
        }
    }

    private void MeasureSceneTarget(out float distance, out float facing)
    {
        distance = facing = -1;
        if (avatar == null || sceneTargetPlan == null || importedReference == null) return;
        Vector3 ground = avatar.transform.position + Vector3.up * importedReference.groundY * avatar.transform.lossyScale.y;
        distance = Vector3.Distance(ground, sceneTargetPlan.GroundTarget);
        Vector3 direction = sceneSurfacePoint - ground; direction.y = 0;
        Vector3 forward = avatar.transform.forward; forward.y = 0;
        if (direction.sqrMagnitude > .0001f && forward.sqrMagnitude > .0001f) facing = Vector3.Angle(forward, direction);
    }

    private void FinishSceneTarget(string status, string message, bool reached = false)
    {
        if (!sceneTargetActive) return;
        MeasureSceneTarget(out float distance, out float facing);
        var value = new SceneTargetResult { actionId = sceneTargetActionId, captureId = sceneCaptureId, targetId = sceneSurfaceId,
            motionKind = SceneTargetMotionKind, requestedDirection = sceneViewDirection ?? "", requestedDistance = sceneViewDistance,
            viewPurpose = sceneViewPurpose ?? "", startGround = sceneStartGround,
            requestedGround = sceneTargetPlan != null ? sceneTargetPlan.GroundTarget : sceneStartGround,
            actualOffset = avatar != null ? new Vector3(avatar.transform.position.x - sceneStartGround.x, 0, avatar.transform.position.z - sceneStartGround.z) : Vector3.zero,
            status = status, reason = message, terminal = true, reached = reached,
            controllerOperationRevision = sceneTargetOperationRevision,
            observedAtRealtime = Time.realtimeSinceStartup, actualStandpointError = distance, actualFacingErrorDegrees = facing,
            visibleSurfacePoint = sceneSurfacePoint, plannedGround = sceneTargetPlan != null ? sceneTargetPlan.GroundTarget : Vector3.zero,
            actualRoot = avatar != null ? avatar.transform.position : Vector3.zero };
        sceneTargetActive = false; sceneTargetValidator = null; sceneTargetEpoch++;
        lastSceneTargetResult = value;
        Debug.Log("[Room/SceneTarget] " + JsonUtility.ToJson(value), this);
        // Withdraw this task before callbacks, so a callback can safely inspect/queue a later task.
        EmitSceneTargetResult(value);
    }

    private void EmitSceneTargetResult(SceneTargetResult value)
    {
        var handlers = SceneTargetCompleted;
        if (handlers == null) return;
        foreach (Action<SceneTargetResult> handler in handlers.GetInvocationList())
        {
            try { handler(CopySceneTargetResult(value)); }
            catch (Exception error) { Debug.LogWarning("[Room/SceneTarget] 结果订阅者异常：" + error.Message, this); }
        }
    }

    private static SceneTargetResult CopySceneTargetResult(SceneTargetResult value)
        => value == null ? null : JsonUtility.FromJson<SceneTargetResult>(JsonUtility.ToJson(value));

    private void ResetSceneTargetForUserTask()
    {
        executionTargetKind = "user";
        sceneViewDirection = sceneViewPurpose = null; sceneViewDistance = 0;
        sceneTargetPlan = null; sceneTargetObservation = null;
        sceneCaptureId = sceneSurfaceId = sceneTargetActionId = "";
    }
}
