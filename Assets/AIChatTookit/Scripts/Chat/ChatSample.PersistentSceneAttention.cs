using System.Collections;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private sealed class PersistentSceneAttentionTask
    {
        public string actionId, entityId, label, captureId, state = "holding", reason = "fresh-view-confirmed";
        public bool active = true;
        public bool refreshPending;
        public float lastConfirmedRealtime;
    }

    private PersistentSceneAttentionTask m_PersistentSceneAttention;

    // These are dialogue lifecycle events, not semantic instructions to stop looking.
    // The independent task still loses ownership immediately on a body writer or binding change.
    internal static bool IsDialogueOnlySceneCancellation(string reason) => reason == "new-user-turn" ||
        reason == "user-started-speaking" || reason == "barge-in";

    private JObject BuildPersistentSceneAttentionFacts()
    {
        var task = m_PersistentSceneAttention;
        bool held = task?.active == true && m_SceneAttention != null &&
            m_SceneAttention.OwnerId == task.actionId && m_SceneAttention.IsPersistent &&
            m_SceneAttention.IsSettled && SceneEyesOpen;
        return new JObject {
            ["active"] = task?.active == true, ["physicallyHeld"] = held,
            ["actionId"] = task?.actionId, ["entityId"] = task?.entityId,
            ["label"] = task?.label, ["state"] = task?.state ?? "idle", ["reason"] = task?.reason,
            ["refreshPending"] = task?.refreshPending == true,
            ["lastConfirmedCaptureId"] = task?.captureId,
            ["confirmationAgeSeconds"] = task == null ? -1f : Mathf.Max(0f, Time.realtimeSinceStartup - task.lastConfirmedRealtime),
            ["scope"] = "Until explicit stop/new target/body action or invalid scene, eyes, angle or visible target. " +
                "Normal conversation preserves the task. Held gaze and an entity visible in a fresh frame do not prove the whole object is visible."
        };
    }

    /// <summary>Called only after the action's fresh-image confirmation and settled stationary view.</summary>
    private bool RetainSceneAttention(SceneActionFact fact)
    {
        if (m_SceneAttention == null || fact == null) return false;
        if (fact.gazeHold != "until-cancel") return m_SceneAttention.RetainView(fact.actionId, 20f);
        if (!m_SceneAttention.RetainPersistentView(fact.actionId)) return false;
        // A superseded verifier may finish later but cannot release this new owner.
        if (m_PersistentSceneAttention != null) m_PersistentSceneAttention.active = false;
        var task = new PersistentSceneAttentionTask {
            actionId = fact.actionId, entityId = fact.objectConfirmed ? fact.entityId : null,
            label = fact.visualLabel, captureId = fact.resultCaptureId ?? fact.confirmedCaptureId,
            lastConfirmedRealtime = Time.realtimeSinceStartup
        };
        m_PersistentSceneAttention = task;
        Debug.Log("[Vision/Gaze] " + BuildPersistentSceneAttentionFacts().ToString(Newtonsoft.Json.Formatting.None), this);
        StartCoroutine(MaintainPersistentSceneAttention(task));
        return true;
    }

    private void EndPersistentSceneAttention(PersistentSceneAttentionTask task, string reason)
    {
        if (task == null || !task.active) return;
        task.active = false; task.refreshPending = false; task.state = "released"; task.reason = reason;
        m_SceneAttention?.Release(task.actionId, reason);
        if (ReferenceEquals(m_PersistentSceneAttention, task))
            Debug.Log("[Vision/Gaze] " + BuildPersistentSceneAttentionFacts().ToString(Newtonsoft.Json.Formatting.None), this);
    }

    private void CancelPersistentSceneAttention(string reason)
    {
        if (reason == "explicit-stop-looking" && m_SceneActionActive &&
            (m_SceneActionFact?.intent == "look-at" || m_SceneActionFact?.intent == "orient-view"))
        { CancelSceneAction("explicit-stop-looking-preparation"); return; }
        EndPersistentSceneAttention(m_PersistentSceneAttention, reason);
        // stop-looking also releases an ordinary completed stationary look; it must not
        // cancel an independent navigation task or its scoped locomotion focus.
        if (!m_SceneActionActive && m_SceneAttention != null && !m_SceneAttention.LocomotionFocusActive)
            m_SceneAttention.Cancel(reason);
    }

    private IEnumerator MaintainPersistentSceneAttention(PersistentSceneAttentionTask task)
    {
        var capture = m_SceneVisionCapture;
        var avatar = capture != null ? capture.Target : null;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var environment = bridge != null ? bridge.environmentRoot : null;
        int scene = gameObject.scene.handle;
        string Unavailable()
        {
            if (!ReferenceEquals(task, m_PersistentSceneAttention) || !task.active) return "gaze-task-replaced";
            if (!isActiveAndEnabled || !SceneEyesOpen || !m_EnableSceneActions || !RoomMotionOutputEnabled)
                return "gaze-output-or-eyes-unavailable";
            if (capture == null || capture != m_SceneVisionCapture || !capture.isActiveAndEnabled || capture.Target != avatar ||
                avatar == null || !avatar.gameObject.activeInHierarchy || avatar.gameObject.scene.handle != scene ||
                bridge == null || !bridge.IsConnected || bridge.avatar != avatar || bridge.environmentRoot != environment ||
                environment == null || !environment.gameObject.activeInHierarchy || environment.gameObject.scene.handle != scene)
                return "gaze-binding-or-scene-changed";
            if (bridge.ReservesBody || bridge.IsFollowing) return "gaze-yielded-to-body-task";
            if (m_SceneAttention == null || !m_SceneAttention.isActiveAndEnabled ||
                m_SceneAttention.Target != avatar || m_SceneAttention.OwnerId != task.actionId ||
                !m_SceneAttention.IsPersistent || !m_SceneAttention.IsBusy || m_SceneAttention.IsReturning)
                return "gaze-head-owner-or-angle-lost";
            return null;
        }
        bool Current() => Unavailable() == null;
        try
        {
            while (task.active && ReferenceEquals(m_PersistentSceneAttention, task))
            {
                float nextCheck = Time.realtimeSinceStartup + 2.5f;
                while (Current() && Time.realtimeSinceStartup < nextCheck) yield return null;
                string failure = Unavailable();
                if (failure != null) { EndPersistentSceneAttention(task, failure); yield break; }
                float requestedAt = Time.realtimeSinceStartup;
                SceneVisionObservation observation = null;
                bool received = false;
                float deadline = Mathf.Min(requestedAt + 4.5f, task.lastConfirmedRealtime + 7.5f);
                task.refreshPending = true;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    int captureEpoch = m_SceneVisionEpoch;
                    requestedAt = Time.realtimeSinceStartup;
                    RequestFreshSceneObservation("persistent-gaze-confirm", value => {
                        if (!Current() || captureEpoch != m_SceneVisionEpoch) return;
                        observation = value; received = true;
                    });
                    while (Current() && !received && Time.realtimeSinceStartup < deadline &&
                        captureEpoch == m_SceneVisionEpoch) yield return null;
                    // InvalidateFormalResponse cancels pending visual callbacks, even
                    // when the separate held-gaze task survives ordinary speech. Retry
                    // that cancelled capture once, under the ORIGINAL lease/deadline.
                    // No stale frame or dialogue restart renews a pose without evidence.
                    if (!Current() || Time.realtimeSinceStartup >= deadline || captureEpoch == m_SceneVisionEpoch) break;
                    // A callback may have completed just before the dialogue reset;
                    // that frame's binding is obsolete too, even if received is true.
                    received = false; observation = null;
                }
                task.refreshPending = false;
                failure = Unavailable();
                if (failure != null) { EndPersistentSceneAttention(task, failure); yield break; }
                if (!received || observation?.HasImage != true || observation.CapturedAtRealtime < requestedAt ||
                    observation.SceneHandle != scene || !IsFreshSceneObservation(observation))
                { EndPersistentSceneAttention(task, "gaze-fresh-observation-unavailable"); yield break; }
                if (!string.IsNullOrEmpty(task.entityId) &&
                    (!capture.TryFindObservedObject(observation, task.entityId, out var visible, out _) ||
                    !visible.VisibleConfirmed || !visible.TryValidate(out _)))
                { EndPersistentSceneAttention(task, "gaze-target-no-longer-visible"); yield break; }
                // For an unresolved image region or oriented view, only the held direction and
                // fresh camera are established. Never invent an object identity or hidden depth.
                if (!m_SceneAttention.RetainPersistentView(task.actionId))
                { EndPersistentSceneAttention(task, "gaze-not-settled-at-renewal"); yield break; }
                task.captureId = observation.CaptureId;
                task.lastConfirmedRealtime = observation.CapturedAtRealtime;
            }
        }
        finally
        {
            if (task.active) EndPersistentSceneAttention(task, "gaze-verifier-ended");
        }
    }
}
