using System;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // Only called for a previously known entity re-confirmed in the current image.
    // This describes the existing executor's distance gate; it neither changes a
    // user's intent nor dispatches navigation, and never enumerates unseen devices.
    private JObject BuildSceneDeviceOperationPrerequisite(SceneObservedObject current)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (!SceneEyesOpen || current == null || m_SceneVisionCapture == null ||
            current.source != m_SceneVisionCapture || !IsFreshSceneObservation(current.observation) ||
            !current.GeometryIsCurrent || bridge?.avatar == null ||
            bridge.avatar != m_SceneVisionCapture.Target || bridge.environmentRoot != m_SceneObjectEnvironment ||
            !KnownSceneObjectBinding(current.EntityId, out var known) || known.root != current.Root ||
            !TryGetSceneObjectPowerCapability(current, bridge.environmentRoot, out var device, out _)) return null;
        float distance = Vector3.Distance(bridge.avatar.transform.position, device.Root.position);
        float limit = Mathf.Clamp(m_SceneDeviceDistance, .5f, 3f);
        if (float.IsNaN(distance) || float.IsInfinity(distance) || float.IsNaN(limit) || float.IsInfinity(limit)) return null;
        return new JObject {
            ["entityId"] = current.EntityId, ["captureId"] = current.CaptureId,
            ["measuredAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["distanceMeters"] = distance, ["maximumDistanceMeters"] = limit,
            ["withinDistance"] = distance <= limit,
            ["requiredNextStep"] = distance <= limit ? "none" : "approach-object",
            ["scope"] = "Current engine root-to-device-root distance for this same freshly visible registered entity, using the exact runtime device gate. This is a capability prerequisite suggestion, not a new user intent, reachable route, physical contact, state read or permission to switch. Room must explicitly plan any approach while preserving the requested power goal, then obtain a fresh same-entity view and recheck all operation gates."
        };
    }
}
