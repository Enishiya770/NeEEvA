using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // Full event archive is separate from work ownership. Resetting the current
    // user task revokes its execution lease, not the fact that an operation ran.
    private readonly List<JObject> m_DeviceEvents = new List<JObject>();
    private LLM m_DeviceEventsModel;
    private ArdyRoomDialogueBridge m_DeviceEventsBridge;
    private Object m_DeviceEventsAvatar, m_DeviceEventsEnvironment;
    private float m_DeviceEventsSession = -1;

    private bool DeviceEventsOwnerCurrent()
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        return bridge != null && bridge == m_DeviceEventsBridge && bridge.avatar == m_DeviceEventsAvatar &&
            bridge.environmentRoot == m_DeviceEventsEnvironment && m_DeviceEventsModel == m_ChatSettings?.m_ChatModel &&
            m_DeviceEventsSession == m_AgentSessionStartTime;
    }

    private void ArchiveDeviceEvent(JObject facts)
    {
        if (facts == null) return;
        if (!DeviceEventsOwnerCurrent()) m_DeviceEvents.Clear();
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        m_DeviceEventsBridge = bridge; m_DeviceEventsAvatar = bridge?.avatar; m_DeviceEventsEnvironment = bridge?.environmentRoot;
        m_DeviceEventsModel = m_ChatSettings?.m_ChatModel; m_DeviceEventsSession = m_AgentSessionStartTime;
        var item = (JObject)facts.DeepClone();
        item["sourceUserTurn"] = m_ActionConstraints.CurrentUserTurn; item["sourceWorkEpoch"] = m_WorkEpoch;
        m_DeviceEvents.Add(item);
        if (m_DeviceEvents.Count > 8) m_DeviceEvents.RemoveAt(0);
    }

    private JArray ReadHistoricalDeviceEvents() => !DeviceEventsOwnerCurrent() ? new JArray() :
        new JArray(m_DeviceEvents.Skip(System.Math.Max(0, m_DeviceEvents.Count - 4)).Select(RoomTaskContextPolicy.DeviceEvent));
}
