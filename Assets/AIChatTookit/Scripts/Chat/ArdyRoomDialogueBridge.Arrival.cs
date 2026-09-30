using System;
using NeEEvA.Motion;
using UnityEngine;

public sealed partial class ArdyRoomDialogueBridge
{
    [Serializable] public sealed class ApproachArrivalEvidence
    {
        public string actionId, observedUtc, completionUtc, invalidatedReason;
        public int operationRevision, targetRevision, sceneSession;
        public bool terminalConfirmed;
        public float observedAtRealtime, desiredDistance, sampledUserDistance, sampledFacingError, targetError;
        public Vector3 actualGround, sampledUserHead, plannedGround;
        public string scope = "Historical measured arrival at the accepted user snapshot. Not current user proximity, an active task or permission to resume. terminalConfirmed requires the same controller operation to finish successfully.";
    }

    private ApproachArrivalEvidence approachArrival;
    private ArdyRoomApproachPlan approachArrivalPlan;
    private Transform approachArrivalUser;
    private ArdyRoomApproachPlan approachSubmissionPlan;
    private Vector3 approachSubmissionTarget, approachSubmissionHead;
    private float approachSubmissionDistance;
    private int approachSubmissionOperation, approachSubmissionTargetRevision;
    private string approachSubmissionAction;
    private UniVRM10.Vrm10Instance approachSubmissionAvatar;
    private ArdyRoomLocomotionController approachSubmissionController;
    private ArdyRoomNavigation approachSubmissionNavigation;
    private Transform approachSubmissionUser, approachSubmissionEnvironment;
    private ChatSample approachSubmissionChat;
    private int approachSubmissionSession;
    public ApproachArrivalEvidence LastApproachArrival => approachArrival == null ? null :
        JsonUtility.FromJson<ApproachArrivalEvidence>(JsonUtility.ToJson(approachArrival));

    private void BindApproachSubmission()
    {
        approachSubmissionPlan = plan; approachSubmissionTarget = plan.GroundTarget;
        approachSubmissionHead = plan.FacePoint; approachSubmissionDistance = approachDistance;
        approachSubmissionOperation = operationRevision; approachSubmissionTargetRevision = lastAttemptTargetRevision;
        approachSubmissionAction = actionId;
        approachSubmissionAvatar = avatar; approachSubmissionController = controller;
        approachSubmissionNavigation = navigation; approachSubmissionEnvironment = environmentRoot;
        approachSubmissionUser = boundUser;
        approachSubmissionChat = chat; approachSubmissionSession = chat != null ? chat.ActionOutcomeSceneSession : -1;
    }

    private bool ApproachSubmissionCurrent() => plan != null && ReferenceEquals(plan, approachSubmissionPlan) &&
        plan.GroundTarget == approachSubmissionTarget && plan.FacePoint == approachSubmissionHead &&
        approachDistance == approachSubmissionDistance && operationRevision == approachSubmissionOperation &&
        lastAttemptTargetRevision == approachSubmissionTargetRevision && actionId == approachSubmissionAction &&
        avatar == approachSubmissionAvatar && controller == approachSubmissionController && navigation == approachSubmissionNavigation &&
        environmentRoot == approachSubmissionEnvironment && boundUser == approachSubmissionUser && userHead == approachSubmissionUser &&
        chat == approachSubmissionChat && (chat != null ? chat.ActionOutcomeSceneSession : -1) == approachSubmissionSession;

    // Measure the promised snapshot, not the user's later location during pose return.
    // Controller success alone is insufficient: target, facing and finite geometry are checked.
    internal static bool MeasuresApproachArrival(Vector3 ground, Vector3 forward, Vector3 target,
        Vector3 sampledHead, float desired, out float distance, out float facing, out float error)
    {
        distance = facing = error = -1;
        if (!Finite(ground) || !Finite(forward) || !Finite(target) || !Finite(sampledHead) ||
            float.IsNaN(desired) || float.IsInfinity(desired) || desired < .8f || desired > 1.5f) return false;
        Vector3 toUser = sampledHead - ground; toUser.y = 0; forward.y = 0;
        Vector3 offset = target - ground; offset.y = 0;
        if (forward.sqrMagnitude < .0001f || toUser.sqrMagnitude < .0001f) return false;
        distance = toUser.magnitude; facing = Vector3.Angle(forward, toUser); error = offset.magnitude;
        return error <= .25f && Mathf.Abs(target.y - ground.y) <= .25f &&
            distance >= desired - .25f && distance <= desired + .25f && facing <= 15f;
    }

    private void ObserveApproachArrival()
    {
        if (!activeAction || followActive || sceneTargetActive || sceneOrientActive || navigationRecoveryActive ||
            !hasControllerOperation || !ApproachSubmissionCurrent() || controller == null || controller.OperationRevision != operationRevision ||
            avatar == null || importedReference == null || userHead == null || userHead != boundUser ||
            (controller.ExecutionState != ArdyRoomExecutionState.Returning && controller.ExecutionState != ArdyRoomExecutionState.Arrived)) return;
        if (approachArrival != null) return;
        Vector3 ground = avatar.transform.position + Vector3.up * importedReference.groundY * avatar.transform.lossyScale.y;
        if (!MeasuresApproachArrival(ground, avatar.transform.forward, plan.GroundTarget, plan.FacePoint,
            approachDistance, out float distance, out float facing, out float error)) return;
        approachArrivalPlan = plan; approachArrivalUser = boundUser;
        approachArrival = new ApproachArrivalEvidence { actionId = actionId, operationRevision = operationRevision,
            targetRevision = lastAttemptTargetRevision, sceneSession = approachSubmissionSession, observedUtc = DateTime.UtcNow.ToString("O"),
            observedAtRealtime = Time.realtimeSinceStartup, desiredDistance = approachDistance,
            sampledUserDistance = distance, sampledFacingError = facing, targetError = error,
            actualGround = ground, sampledUserHead = plan.FacePoint, plannedGround = plan.GroundTarget };
    }

    private bool ConfirmApproachArrival()
    {
        ObserveApproachArrival();
        if (approachArrival == null || !string.IsNullOrEmpty(approachArrival.invalidatedReason) || !activeAction || !ApproachSubmissionCurrent() ||
            followActive || sceneTargetActive || sceneOrientActive || navigationRecoveryActive ||
            !ReferenceEquals(plan, approachArrivalPlan) || userHead != approachArrivalUser || boundUser != approachArrivalUser ||
            actionId != approachArrival.actionId || lastAttemptTargetRevision != approachArrival.targetRevision ||
            !hasControllerOperation || controller == null || controller.OperationRevision != operationRevision ||
            operationRevision != approachArrival.operationRevision || controller.ExecutionState != ArdyRoomExecutionState.Arrived ||
            (controller.ExecutionReason != ArdyRoomExecutionReason.Completed && controller.ExecutionReason != ArdyRoomExecutionReason.AlreadyAtTarget) ||
            Mathf.Abs(approachDistance - approachArrival.desiredDistance) > .001f ||
            plan.GroundTarget != approachArrival.plannedGround || plan.FacePoint != approachArrival.sampledUserHead ||
            avatar == null || importedReference == null || !Finite(userHead.position) ||
            ArdyRoomApproachPlanner.UserGroundDisplacement(plan.FacePoint, userHead.position) > .35f) return false;
        Vector3 ground = avatar.transform.position + Vector3.up * importedReference.groundY * avatar.transform.lossyScale.y;
        if (!MeasuresApproachArrival(ground, avatar.transform.forward, plan.GroundTarget, plan.FacePoint,
            approachArrival.desiredDistance, out _, out _, out _)) return false;
        approachArrival.terminalConfirmed = true;
        approachArrival.completionUtc = DateTime.UtcNow.ToString("O");
        return true;
    }

    private void InvalidateApproachArrival(string cause)
    {
        if (approachArrival != null && !approachArrival.terminalConfirmed)
            approachArrival.invalidatedReason = cause ?? "cancelled";
    }
}
