using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // Semantic interpretation belongs to one complete, independently reviewed
    // goal. A new capture still has to prove the same opaque Unity entity.
    private sealed class SceneTargetIdentity
    {
        public LLM model;
        public Transform environment;
        public UniVRM10.Vrm10Instance avatar;
        public SceneObservedObject observed;
        public JObject goal, receipt;
        public int userTurn;
        public float session;
        public string request;
    }
    private SceneTargetIdentity m_SceneTargetIdentity;

    private void RememberSceneTargetIdentity(SceneObservedObject observed, JObject goal)
    {
        if (observed == null || (bool?)goal?["executionCurrent"] != true ||
            (string)goal["boundEntityId"] != observed.EntityId ||
            (bool?)m_SceneObjectMatchReceipt?["semanticTargetConfirmed"] != true) return;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        m_SceneTargetIdentity = new SceneTargetIdentity {
            model = m_ChatSettings.m_ChatModel, environment = bridge.environmentRoot, avatar = bridge.avatar,
            observed = observed, goal = (JObject)goal.DeepClone(), receipt = (JObject)m_SceneObjectMatchReceipt.DeepClone(),
            userTurn = m_ActionConstraints.CurrentUserTurn, request = m_ActionConstraints.CurrentUserText, session = m_AgentSessionStartTime
        };
    }

    private bool TryReuseSceneTargetIdentity(RoomTaskProtocol.Decision decision, SceneVisionObservation observation,
        SceneObservedObject observed, SceneActionFact fact)
    {
        var value = m_SceneTargetIdentity;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var goal = CurrentSceneObjectSpatialGoal();
        if (value == null || decision.IsObservationApproach || decision.RequestsTargetReselection || !string.IsNullOrEmpty(decision.observationGoalId) ||
            decision.entityTarget == null || decision.visualTarget != null || observed == null ||
            bridge == null || bridge.environmentRoot != value.environment || bridge.avatar != value.avatar ||
            m_ChatSettings.m_ChatModel != value.model || m_AgentSessionStartTime != value.session || m_ActionConstraints.CurrentUserTurn != value.userTurn ||
            m_ActionConstraints.CurrentUserText != value.request || (bool?)goal?["executionCurrent"] != true ||
            (bool?)goal["ownerCurrent"] != true ||
            (string)goal["goalId"] != (string)value.goal["goalId"] ||
            (string)goal["originalRequest"] != (string)value.goal["originalRequest"] ||
            (string)goal["desiredState"] != (string)value.goal["desiredState"] ||
            (string)goal["boundEntityId"] != value.observed.EntityId ||
            decision.entityTarget.entityId != value.observed.EntityId || observed.EntityId != value.observed.EntityId ||
            observed.Root != value.observed.Root || !value.observed.TryValidateFrozenGeometry(out _) ||
            !value.observed.source.IsObservationBindingCurrent(value.observed.observation) ||
            observation == null || observed.CaptureId != observation.CaptureId ||
            decision.entityTarget.captureId != observation.CaptureId || !IsFreshSceneObservation(observation) ||
            !observed.VisibleConfirmed || !observed.GeometryIsCurrent) return false;
        fact.targetPurpose = "direct-target"; fact.targetDescription = SceneObjectGoal(decision);
        fact.matchCaptureId = (string)value.receipt["captureId"];
        fact.targetMatchVerdict = "matched"; fact.targetMatchReason = "same-current-goal-reviewed-identity; fresh opaque same-entity confirmation";
        fact.semanticTargetConfirmed = true; fact.objectConfirmed = true; fact.entityId = observed.EntityId;
        fact.focusKind = "visible-object"; fact.intentFocusWorldPoint = observed.WorldPoint; fact.hasIntentFocus = true;
        fact.targetResolutionSource = "same-goal-identity-fresh-geometry";
        Debug.Log("[Vision/TargetIdentityReuse] " + new JObject {
            ["actionId"] = fact.actionId, ["goalId"] = goal["goalId"], ["userTurn"] = value.userTurn,
            ["entityId"] = observed.EntityId, ["semanticCaptureId"] = fact.matchCaptureId,
            ["freshCaptureId"] = observation.CaptureId, ["freshOpaqueConfirmed"] = true,
            ["scope"] = "Reuses only same-goal semantic identity. Fresh view, geometry, action ownership, permission, proximity and result readback remain required."
        }.ToString(Newtonsoft.Json.Formatting.None), this);
        return true;
    }
}
