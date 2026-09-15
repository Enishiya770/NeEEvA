using System;
using NeEEvA.Motion;
using UnityEngine;

public sealed partial class ArdyRoomDialogueBridge
{
    [Serializable] public sealed class SceneOrientResult
    {
        public string actionId, captureId, status, reason;
        public bool terminal, oriented;
        public float requestedYaw, actualYaw, actualRootDisplacement, actualFacingErrorDegrees;
        public Vector3 actualRoot;
        public int controllerOperationRevision;
        public string scope = "Actual native ARDY in-place turn on the actor's validated floor. Turning does not prove anything about the newly visible scene; a new image is required.";
    }
    public event Action<SceneOrientResult> SceneOrientCompleted;
    public SceneOrientResult LastSceneOrientResult => CopySceneOrientResult(lastSceneOrientResult);
    public bool IsOrientingScene => sceneOrientActive;
    private bool sceneOrientActive;
    private string sceneOrientId, sceneOrientCaptureId;
    private Vector3 sceneOrientRoot, sceneOrientGround, sceneOrientFace;
    private float sceneOrientYaw, sceneOrientClearance;
    private Vector3 sceneOrientObservedForward;
    private float sceneOrientActualYaw;
    private Transform sceneOrientUser;
    private Func<string> sceneOrientValidator;
    private SceneOrientResult lastSceneOrientResult;

    public bool TryOrientScene(string id, string captureId, float yaw, Func<string> validate, out string failure)
    {
        failure = null;
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(captureId) || float.IsNaN(yaw) ||
            float.IsInfinity(yaw) || Mathf.Abs(yaw) < 5f || Mathf.Abs(yaw) > 180f || validate == null)
            failure = "原地转向缺少有效观测、动作标识或有界方向。";
        else if (!IsConnected || controller.Avatar != avatar || navigation.EnvironmentRoot != environmentRoot || chat != subscribedChat)
            failure = "原地转向的房间或角色连接不可用。";
        else if (activeAction || followActive || ReservesBody || controller.IsBusy || controller.IsMoving)
            failure = "已有身体任务，未替换它执行转身。";
        else failure = ValidateSceneTargetCallback(validate);
        if (failure != null) return false;
        Vector3 ground = avatar.transform.position + Vector3.up * importedReference.groundY * avatar.transform.lossyScale.y;
        if (!navigation.TryValidatePlanStart(ground, out var legal, out failure, out _) ||
            !navigation.TryPlan(legal, legal, out var route, out failure) ||
            !TryGetTrackedUserClearanceCenter(legal.y, out var userCenter, out failure)) return false;
        float radius = navigation.AgentRadius + ArdyRoomApproachPlanner.UserBodyRadius + ArdyRoomApproachPlanner.UserClearance;
        if (!ArdyRoomApproachPlanner.RouteKeepsUserClearance(route, userCenter, radius))
        { failure = "用户位于角色身体净距内，未开始原地转身。"; return false; }
        ResetNavigationRecovery();
        sceneOrientId = actionId = id; sceneOrientCaptureId = captureId; sceneOrientYaw = yaw;
        sceneOrientRoot = avatar.transform.position;
        sceneOrientObservedForward = avatar.transform.forward; sceneOrientActualYaw = 0f;
        sceneOrientGround = legal;
        Vector3 direction = Quaternion.AngleAxis(yaw, Vector3.up) * avatar.transform.forward;
        direction.y = 0; direction.Normalize(); sceneOrientFace = legal + direction;
        sceneOrientClearance = radius; sceneOrientUser = userHead; sceneOrientValidator = validate;
        executionTargetKind = "scene-view"; sceneOrientActive = true; activeAction = true;
        plan = null; attemptObservation = null; hasControllerOperation = false; lastBodyRejection = "";
        lastAttemptActionId = actionId; lastAttemptTargetRevision = -1;
        actionStartedAt = lastAttemptTime = Time.realtimeSinceStartup;
        lastAttemptPhase = "preparing"; lastAttemptReason = "正在准备原地转向观察。";
        var live = avatar.GetComponent<ArdyLiveMotionController>();
        if (live != null) live.Cancel("scene-view-turn");
        SetFact("preparing", "已接受原地转身，目标是当前合法地面及新的身体朝向。");
        try
        {
            controller.WalkingSpeed = walkingSpeed;
            controller.TurnInPlace(sceneOrientGround, yaw);
            operationRevision = controller.OperationRevision; hasControllerOperation = true;
            ObserveSceneOrient(); return true;
        }
        catch (Exception error) { failure = error.Message; StopMovement("failed", failure); return false; }
    }

    private string CheckSceneOrient()
    {
        if (!IsConnected || controller.Avatar != avatar || navigation.EnvironmentRoot != environmentRoot || chat != subscribedChat)
            return "原地转向的角色或房间绑定已改变。";
        if (userHead != sceneOrientUser) return "用户净距锚点已改变。";
        if (!TryGetTrackedUserClearanceCenter(sceneOrientGround.y, out var center, out string failure)) return failure;
        Vector3 offset = avatar.transform.position - center; offset.y = 0;
        if (offset.magnitude < sceneOrientClearance) return "用户已进入原地转向的身体净距内，停止本次转身。";
        return ValidateSceneTargetCallback(sceneOrientValidator);
    }
    private string GuardSceneOrientStep(Vector3 from, Vector3 to)
    {
        if (hasControllerOperation && controller.OperationRevision != operationRevision) return null;
        string failure = CheckSceneOrient(); if (failure != null) return failure;
        if (!TryGetTrackedUserClearanceCenter(sceneOrientGround.y, out var center, out failure)) return failure;
        if (!ArdyRoomApproachPlanner.StepKeepsUserClearance(from, to, center, sceneOrientClearance))
            return "用户已进入原地转向的身体净距内，停止本次转身。";
        if (Vector3.Distance(to, sceneOrientGround) > .16f) return "原地转向超出允许的站位偏移，已停止。";
        return null;
    }
    private void TickSceneOrient()
    {
        if (!sceneOrientActive || !activeAction) return;
        if (controller == null || controller.OperationRevision != operationRevision)
        {
            activeAction = false; hasControllerOperation = false;
            SetFact("cancelled", "原地转向已被另一执行替换；未取消或认领新任务。");
            FinishSceneOrient("cancelled", reason); return;
        }
        if (ControllerAlreadyTerminated()) { ObserveSceneOrient(); return; }
        string failure = CheckSceneOrient();
        if (failure != null) { StopMovement("cancelled", failure); return; }
        ObserveSceneOrient();
    }
    private void ObserveSceneOrient()
    {
        if (!sceneOrientActive || controller == null) return;
        ObserveSceneOrientYaw();
        switch (controller.ExecutionState)
        {
            case ArdyRoomExecutionState.Preparing: SetFact("preparing", controller.Status); break;
            case ArdyRoomExecutionState.Generating: SetFact("generating", controller.Status); break;
            case ArdyRoomExecutionState.Moving: SetFact("moving", controller.Status); break;
            case ArdyRoomExecutionState.Returning: SetFact("returning", controller.Status); break;
            case ArdyRoomExecutionState.Arrived:
                activeAction = false;
                bool oriented = Vector3.Distance(avatar.transform.position, sceneOrientRoot) <= .16f && FacingError() <= 15f &&
                    Mathf.Abs(sceneOrientActualYaw - sceneOrientYaw) <= 20f;
                string state = oriented ? "turned" : "orientation-not-reached";
                SetFact(state, oriented ? "已实际原地转身；需要新视野确认所见。" : "转向已结束但朝向或站位误差未通过。");
                FinishSceneOrient(state, reason, oriented); break;
            case ArdyRoomExecutionState.Blocked:
            case ArdyRoomExecutionState.Failed:
            case ArdyRoomExecutionState.Cancelled:
                activeAction = false;
                string phase = controller.ExecutionState.ToString().ToLowerInvariant();
                SetFact(phase, controller.Status); FinishSceneOrient(phase, reason); break;
        }
    }
    private float FacingError()
    {
        Vector3 expected = sceneOrientFace - sceneOrientGround; expected.y = 0;
        Vector3 actual = avatar.transform.forward; actual.y = 0;
        return Vector3.Angle(expected, actual);
    }
    private void ObserveSceneOrientYaw()
    {
        if (avatar == null) return;
        Vector3 forward = avatar.transform.forward;
        Vector3 previous = sceneOrientObservedForward; previous.y = 0; forward.y = 0;
        if (previous.sqrMagnitude > .0001f && forward.sqrMagnitude > .0001f)
            sceneOrientActualYaw += Vector3.SignedAngle(previous, forward, Vector3.up);
        sceneOrientObservedForward = forward;
    }
    private void FinishSceneOrient(string status, string message, bool oriented = false)
    {
        if (!sceneOrientActive) return;
        ObserveSceneOrientYaw();
        var value = new SceneOrientResult { actionId = sceneOrientId, captureId = sceneOrientCaptureId,
            status = status, reason = message, terminal = true, oriented = oriented, requestedYaw = sceneOrientYaw,
            actualRoot = avatar != null ? avatar.transform.position : sceneOrientRoot,
            actualRootDisplacement = avatar != null ? Vector3.Distance(avatar.transform.position, sceneOrientRoot) : -1f,
            actualFacingErrorDegrees = avatar != null ? FacingError() : -1f,
            actualYaw = sceneOrientActualYaw,
            controllerOperationRevision = operationRevision };
        sceneOrientActive = false; sceneOrientValidator = null; lastSceneOrientResult = value;
        Debug.Log("[Room/SceneOrient] " + JsonUtility.ToJson(value), this);
        var callbacks = SceneOrientCompleted; if (callbacks == null) return;
        foreach (Action<SceneOrientResult> callback in callbacks.GetInvocationList())
            try { callback(CopySceneOrientResult(value)); }
            catch (Exception error) { Debug.LogWarning("[Room/SceneOrient] 结果订阅者异常：" + error.Message, this); }
    }
    private static SceneOrientResult CopySceneOrientResult(SceneOrientResult value)
        => value == null ? null : JsonUtility.FromJson<SceneOrientResult>(JsonUtility.ToJson(value));
}
