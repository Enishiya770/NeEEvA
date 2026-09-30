using System;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private Func<bool> CaptureSceneActorPlanLease(RoomTaskProtocol.Decision decision, int generation)
    {
        if (string.IsNullOrEmpty(decision?.planId)) return () => true;
        var plan = m_ActorPlan; var turn = m_ActorPlanTurn;
        int revision = decision.planRevision, epoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn;
        string id = decision.planId;
        return () => ActorPlanCurrent(plan, turn) && plan.Id == id && plan.Revision == revision &&
            turn.Generation == generation && epoch == m_WorkEpoch && userTurn == m_ActionConstraints.CurrentUserTurn;
    }

    private JArray BuildRoomObjectPrerequisites(SceneVisionObservation observation)
    {
        var result = new JArray();
        if (!m_EnableSceneObjectAwareness || !SceneEyesOpen || !IsFreshSceneObservation(observation) || m_SceneVisionCapture == null) return result;
        foreach (var item in m_KnownSceneObjects)
        {
            if (!KnownSceneObjectBinding(item.Key, out var known)) continue;
            if (m_SceneVisionCapture.TryFindObservedObject(observation, item.Key, out var seen, out _) &&
                seen.VisibleConfirmed && seen.Root == known.root && seen.TryValidate(out _))
            {
                var distance = BuildSceneDeviceOperationPrerequisite(seen);
                if (distance != null) result.Add(distance);
            }
            else if (TryRememberedSceneBearing(item.Key, observation, out _, out _))
                result.Add(new JObject { ["entityId"] = item.Key, ["captureId"] = observation.CaptureId,
                    ["visibleInCurrentImage"] = false, ["requiredNextStep"] = "reobserve-object",
                    ["lastSeenCaptureId"] = known.facts["captureId"]?.DeepClone(),
                    ["scope"] = "Previously observed identity only. A bounded turn toward its historical observed point can acquire new pixels. No current position, visibility, reachable route, state or device permission is inferred from memory." });
        }
        return result;
    }

    private bool TryGetRoomObjectPrerequisite(RoomTaskProtocol.Decision decision, SceneVisionObservation observation,
        int generation, out JObject prerequisite)
    {
        prerequisite = null;
        if (decision == null || decision.intent == "reobserve-object" || decision.RequestsTargetReselection ||
            !ActorPlanCurrent(m_ActorPlan, m_ActorPlanTurn) || m_ActorPlanTurn.Generation != generation ||
            decision.planId != m_ActorPlan.Id || decision.planRevision != m_ActorPlan.Revision ||
            !m_EnableSceneObjectAwareness || !SceneEyesOpen || !IsFreshSceneObservation(observation)) return false;
        if (decision.entityTarget == null)
        {
            // A current pixel can already resolve to the same previously known
            // device. Report its actual gate before accepting the set step; do
            // not bind the plan, rewrite the target or silently start navigation.
            // The next explicit Room decision still passes semantic matching.
            if (decision.intent != "set-object-state" || decision.visualTarget == null ||
                decision.visualTarget.captureId != observation.CaptureId || m_SceneVisionCapture == null ||
                !m_SceneVisionCapture.TryResolveObservedObject(observation,
                    new Vector2(decision.visualTarget.x, decision.visualTarget.y), out var selected, out _)) return false;
            if (selected != null && !selected.VisibleConfirmed &&
                m_SceneVisionCapture.TryFindObservedObject(observation, selected.EntityId, out var opaque, out _)) selected = opaque;
            if (selected?.VisibleConfirmed != true || !selected.TryValidate(out _) ||
                !KnownSceneObjectBinding(selected.EntityId, out var known) || selected.Root != known.root ||
                (!string.IsNullOrEmpty(m_ActorPlan.EntityId) && m_ActorPlan.EntityId != selected.EntityId)) return false;
            var distance = BuildSceneDeviceOperationPrerequisite(selected);
            if ((string)distance?["requiredNextStep"] != "approach-object") return false;
            prerequisite = distance;
            prerequisite["targetResolutionSource"] = "current-image-pixel-to-known-entity";
            return true;
        }
        if (decision.visualTarget != null || observation.CaptureId != decision.entityTarget.captureId) return false;
        foreach (JObject fact in BuildRoomObjectPrerequisites(observation))
            if ((string)fact["entityId"] == decision.entityTarget.entityId &&
                ((string)fact["requiredNextStep"] == "reobserve-object" ||
                 decision.intent == "set-object-state" && (string)fact["requiredNextStep"] == "approach-object"))
            { prerequisite = fact; return true; }
        return false;
    }

    private bool TryRememberedSceneBearing(string entityId, SceneVisionObservation observation, out float yaw, out string failure)
    {
        yaw = 0; failure = "remembered-entity-observation-unavailable";
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (!m_EnableSceneObjectAwareness || !SceneEyesOpen || !IsFreshSceneObservation(observation) || bridge?.avatar == null ||
            m_SceneVisionCapture == null || m_SceneVisionCapture.Target != bridge.avatar ||
            !m_SceneVisionCapture.IsObservationBindingCurrent(observation) ||
            !KnownSceneObjectBinding(entityId, out var known) || known.avatar != bridge.avatar ||
            !(known.facts["pointAtCapture"] is JObject point)) return false;
        bool Number(JToken value) => value?.Type == JTokenType.Float || value?.Type == JTokenType.Integer;
        if (!Number(point["x"]) || !Number(point["y"]) || !Number(point["z"])) return false;
        Vector3 historical = new Vector3((float)point["x"], (float)point["y"], (float)point["z"]);
        if (float.IsNaN(historical.x) || float.IsNaN(historical.y) || float.IsNaN(historical.z) ||
            float.IsInfinity(historical.x) || float.IsInfinity(historical.y) || float.IsInfinity(historical.z)) return false;
        Vector3 bearing = historical - bridge.avatar.transform.position; bearing.y = 0;
        Vector3 forward = bridge.avatar.transform.forward; forward.y = 0;
        if (bearing.sqrMagnitude < .01f || forward.sqrMagnitude < .01f) return false;
        yaw = Vector3.SignedAngle(forward, bearing, Vector3.up);
        failure = null; return true;
    }

    private void BeginSceneObjectReobservation(RoomTaskProtocol.Decision decision, SceneVisionObservation observation,
        int generation, Action completed)
    {
        if (!m_EnableSceneObjectAwareness)
        { m_RoomTaskDispatch = "not-submitted: scene-object-awareness-disabled"; completed(); return; }
        string failure = null; float yaw = 0;
        if (decision?.entityTarget == null || decision.entityTarget.captureId != observation?.CaptureId ||
            !ActorPlanCurrent(m_ActorPlan, m_ActorPlanTurn) || m_ActorPlanTurn.Generation != generation ||
            decision.planId != m_ActorPlan.Id || decision.planRevision != m_ActorPlan.Revision ||
            !TryRememberedSceneBearing(decision.entityTarget.entityId, observation, out yaw, out failure))
        {
            m_RoomTaskDispatch = "not-submitted: " + (failure ?? "reobservation-owner-invalid");
            completed(); return;
        }
        // This intent explicitly adopts a memory-directed observation, not the
        // pending device operation. The resulting pixels still must show the ID.
        decision.viewChange = new RoomTaskProtocol.ViewChange { captureId = observation.CaptureId, yaw = yaw, pitch = 0 };
        CancelPersistentSceneAttention("remembered-object-reobservation");
        BeginSceneViewChange(decision, observation, generation, completed);
    }
}
