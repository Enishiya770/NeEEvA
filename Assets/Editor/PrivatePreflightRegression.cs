using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NeEEvA.Perception;
using UnityEngine;

public static class PrivatePreflightRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    public static JObject Fixture() => JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/PrivatePreflight.latest-20261013.json"));
    public static object Goal(ChatSample chat, ArdyRoomDialogueBridge bridge, LLM model, JObject recorded)
    {
        var type = typeof(ChatSample).GetField("m_SpatialDeviceGoal", Flags).FieldType;
        object goal = Activator.CreateInstance(type, true);
        void Set(string name, object value) => type.GetField(name, Flags).SetValue(goal, value);
        Set("id", (string)recorded["goalId"]); Set("status", "satisfied");
        foreach (string name in new[] { "originalRequest", "requestQuote", "targetDescription", "latestUserText", "referentKind", "referentSourceId", "createdUtc", "reason" })
            Set(name, (string)recorded[name]);
        Set("referenceContext", recorded["referenceContext"] as JObject);
        Set("confirmedTarget", recorded["confirmedTarget"] as JObject);
        foreach (var pair in new[] { ("steps", "searchProgress.completedSteps"), ("views", "searchProgress.observedViews"), ("failures", "failedCandidates"), ("userUpdates", "userUpdates") })
            if (recorded.SelectToken(pair.Item2) is JArray rows)
                foreach (JObject row in rows) ((List<JObject>)type.GetField(pair.Item1, Flags).GetValue(goal)).Add((JObject)row.DeepClone());
        Set("environment", bridge.environmentRoot); Set("avatar", bridge.avatar); Set("model", model);
        Set("source", typeof(ChatSample).GetField("m_SceneVisionCapture", Flags).GetValue(chat));
        if (!SceneObjectLampCatalog.EnsureRegistered(bridge.environmentRoot, out var device, out string failure)) throw new InvalidOperationException(failure);
        if (!device.TrySetPower("isolated-recorded-state-"+Guid.NewGuid().ToString("N"),(string)recorded["desiredState"]=="on",out _,out failure))
            throw new InvalidOperationException(failure);
        Set("entityId", device.EntityId); Set("entityRoot", device.Root);
        Set("desiredState", device.ReadState().on ? "on" : "off");
        var evidence = (JObject)recorded["completionEvidence"].DeepClone();
        evidence["entityId"] = device.EntityId; evidence["actual"] = JObject.Parse(JsonUtility.ToJson(device.ReadState()));
        Set("completionEvidence", evidence);
        return goal;
    }
}
