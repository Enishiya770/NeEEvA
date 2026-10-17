using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private sealed class ActivityOrigin
    {
        public string Adapter, Binding;
        public int WorkEpoch, UserTurn;
        public JObject Task, Source;
    }
    private readonly Dictionary<string, ActivityOrigin> m_ActivityOrigins = new Dictionary<string, ActivityOrigin>();
    private float m_ActivityOriginSession = float.MinValue;

    private void ForgetOtherActivityOrigins(string adapter, string currentId)
    {
        string keep = ActivityOriginKey(adapter, currentId);
        foreach (string key in m_ActivityOrigins.Where(p => p.Value.Adapter == adapter &&
            (currentId == null || p.Key != keep)).Select(p => p.Key).ToArray()) m_ActivityOrigins.Remove(key);
    }

    private static string ActivityOriginKey(string adapter, string id) => adapter.Length + ":" + adapter + id;

    private string ActivityOriginBinding(string adapter)
    {
        if (m_RegisteredActivities.TryGetValue(adapter, out var registered))
            return "registered:" + registered.Registration;
        var room = GetComponent<ArdyRoomDialogueBridge>();
        var motion = GetComponent<ArdyDialogueMotionBridge>();
        int Id(UnityEngine.Object value) => value != null ? value.GetInstanceID() : 0;
        return adapter + ":" + gameObject.scene.handle + ":" + Id(this) + ":" +
            (adapter == "room" ? Id(room) + ":" + Id(room?.Controller) + ":" + Id(room?.avatar) + ":" + Id(room?.environmentRoot) :
             adapter == "gesture" ? Id(motion) + ":" + Id(motion?.Avatar) : "builtin");
    }

    // Freeze before the request/sequence is replaced, even if no monitor tick ran.
    // Reading provenance neither submits nor stops the still-owned activity.
    private void SnapshotActivityOrigins()
    {
        foreach (var adapter in ActivityAdapters())
        {
            JObject state;
            try { state = adapter.Value.Read(); }
            catch (Exception error) { Debug.LogWarning("[Activity/OriginObservation] " + error.GetType().Name, this); continue; }
            if (string.IsNullOrEmpty((string)state?["id"])) { ForgetOtherActivityOrigins(adapter.Key, null); continue; }
            ActivityTaskContextWithOrigin(state, adapter.Key);
        }
    }

    private JObject ActivityTaskContextWithOrigin(JObject action, string adapter)
    {
        if (m_ActivityOriginSession != m_AgentSessionStartTime)
        {
            m_ActivityOrigins.Clear(); m_ActivityOriginSession = m_AgentSessionStartTime;
        }
        string key = ActivityOriginKey(adapter, (string)action["id"]), binding = ActivityOriginBinding(adapter);
        ForgetOtherActivityOrigins(adapter, (string)action["id"]);
        var current = ActivityTaskContext(action);
        if (!m_ActivityOrigins.TryGetValue(key, out var origin) || origin.Binding != binding)
        {
            origin = new ActivityOrigin { Adapter = adapter, Binding = binding, WorkEpoch = m_WorkEpoch,
                UserTurn = m_ActionConstraints.CurrentUserTurn };
            m_ActivityOrigins[key] = origin;
        }
        bool sameWork = origin.WorkEpoch == m_WorkEpoch && origin.UserTurn == m_ActionConstraints.CurrentUserTurn;
        if (sameWork && (origin.Source == null || current != null))
        {
            origin.Task = current == null ? null : (JObject)current.DeepClone();
            // Later autonomous deliberation in this work epoch must not relabel
            // an already accepted user action. Only unresolved origins may fill in.
            if (origin.Source == null || (string)origin.Source["initiative"] == "unknown")
            {
                string initiative = (string)current?["origin"]?["initiative"];
                if (string.IsNullOrEmpty(initiative)) initiative = m_WorkAutonomousHandoff ? "autonomous" :
                    current?["step"] is JObject ? "user-work" : m_AgentCurrentRoundIsTick ? "autonomous" : "unknown";
                origin.Source = new JObject { ["workEpoch"] = origin.WorkEpoch, ["userTurn"] = origin.UserTurn,
                    ["initiative"] = initiative, ["request"] = initiative == "user-work" ? (string)current?["origin"]?["request"] ?? m_ActionConstraints.CurrentUserText : null };
            }
        }
        var task = sameWork && current != null ? (JObject)current.DeepClone() :
            origin.Task != null ? (JObject)origin.Task.DeepClone() : new JObject { ["relationship"] = "independent-or-unresolved" };
        task["origin"] = origin.Source?.DeepClone();
        task["requestRelationship"] = sameWork ? "same-work" : "earlier-work-still-running";
        if (!sameWork)
        {
            // A historical step state is not the current phase or completion.
            (task["step"] as JObject)?.Remove("status");
            task["scope"] = "Earlier activity remains owned until an actual handoff/stop. Its original purpose is not the new request's step. A new request alone does not revoke it or grant new permission; use current constraints and exact live ownership.";
        }
        return task;
    }
}
