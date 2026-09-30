using System;
using System.Linq;
using Newtonsoft.Json.Linq;
#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Reflection;
using NeEEvA.Motion;
using NeEEvA.Perception;
using UnityEngine;
#endif

/// <summary>Strict semantic protocol and actual dialogue cancellation lifecycle; no models or services.</summary>
public static class PersistentSceneAttentionRegression
{
    private static int checks;
    public static int RunChecks()
    {
        checks = 0;
        ProtocolCases();
#if UNITY_EDITOR
        LifecycleCases();
#endif
        return checks;
    }

    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new InvalidOperationException(message); }

    private static JObject Decision(string intent) => new JObject {
        ["intent"] = intent, ["origin"] = "user", ["evidence"] = "保持当前视向"
    };

    private static void ProtocolCases()
    {
        foreach (string intent in new[] { "look-at", "orient-view", "reposition-view" })
        {
            var value = Decision(intent);
            if (intent == "orient-view")
                value["viewChange"] = new JObject { ["captureId"] = "current", ["yaw"] = 0, ["pitch"] = 12 };
            else
                value["visualTarget"] = new JObject { ["captureId"] = "current", ["x"] = .5f, ["y"] = .2f, ["label"] = "屏幕" };
            if (intent == "reposition-view")
                value["viewPosition"] = new JObject { ["direction"] = "farther", ["distance"] = .4f, ["purpose"] = "see-whole" };
            Check(RoomTaskProtocol.TryDecision(value.ToString(), "请保持当前视向", false, out var ordinary) && !ordinary.RequestsPersistentGaze,
                "Ordinary valid visual action remains bounded without an explicit hold field: " + intent);
            value["gazeHold"] = "until-cancel";
            Check(RoomTaskProtocol.TryDecision(value.ToString(), "请保持当前视向", false, out var persistent) && persistent.RequestsPersistentGaze,
                "Semantic hold must survive strict decision parsing: " + intent);
            Check(!RoomTaskProtocol.TryDecision(value.ToString(), "停下，别再看了", false, out _),
                "A stale quoted request cannot re-establish a previous hold.");
            foreach (var invalid in new JToken[] { JValue.CreateNull(), new JValue("forever"), new JValue(true), new JObject() })
            {
                value["gazeHold"] = invalid;
                Check(!RoomTaskProtocol.TryDecision(value.ToString(), "请保持当前视向", false, out _, out var reason) && reason == "invalid-gaze-hold",
                    "Hold is a closed semantic field, not permissive metadata.");
            }
        }
        foreach (string intent in new[] { "none", "follow", "stop-moving", "stop-looking", "inspect", "explore" })
        {
            var value = Decision(intent); value["gazeHold"] = "until-cancel";
            Check(!RoomTaskProtocol.TryDecision(value.ToString(), "保持当前视向", false, out _, out var reason) && reason == "invalid-gaze-hold",
                "Non-gaze intent cannot carry a hidden persistent gaze: " + intent);
        }
        var stop = Decision("stop-looking"); stop["evidence"] = "不用保持抬头了";
        Check(RoomTaskProtocol.TryDecision(stop.ToString(), "不用保持抬头了，我们继续聊天。", false, out var stopped) &&
            stopped.IsSpatial && !stopped.IsVisual && !stopped.RequestsPersistentGaze,
            "Stopping gaze requires no picture and does not implicitly request a body movement.");
        var branches = (JArray)JObject.Parse(RoomTaskProtocol.DecisionSchema)["oneOf"];
        foreach (JObject branch in branches)
        {
            var intents = ((JArray)branch["properties"]["intent"]["enum"]).Values<string>().ToArray();
            bool allowsHold = intents.Any(i => i == "look-at" || i == "orient-view" || i == "reposition-view");
            Check((branch["properties"]["gazeHold"] != null) == allowsHold,
                "Provider grammar and runtime hold whitelist must agree: " + string.Join(",", intents));
        }
    }

#if UNITY_EDITOR
    private static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    public static IEnumerator RunCaptureChecks(ChatSample chat, ArdyRoomDialogueBridge bridge, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying || chat == null || bridge?.avatar == null)
            throw new InvalidOperationException("Persistent gaze capture checks require the isolated live avatar fixture.");
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        object Get(object instance, string field) => instance.GetType().GetField(field, flags).GetValue(instance);
        void Set(object instance, string field, object value) => instance.GetType().GetField(field, flags).SetValue(instance, value);
        object Call(string method, params object[] args) => NevaSceneVisionIntegrationRegression.Call(chat, method, args);
        int checkedCount = 0;
        void Require(bool value, string message)
        { checkedCount++; if (!value) throw new InvalidOperationException(message); }
        IEnumerator Wait(Func<bool> ready, string failure)
        {
            float deadline = Time.realtimeSinceStartup + 7f;
            while (!ready() && Time.realtimeSinceStartup < deadline) yield return null;
            Require(ready(), failure);
        }
        bool oldSceneActions = (bool)Get(chat, "m_EnableSceneActions");
        bool oldEyes = chat.SceneEyesOpen;
        object oldFact = Get(chat, "m_SceneActionFact");
        string oldIntent = (string)Get(chat, "m_RoomTaskIntent");
        string oldAction = (string)Get(chat, "m_RoomTaskActionId");
        string oldDispatch = (string)Get(chat, "m_RoomTaskDispatch");
        var eyes = bridge.avatar.GetComponent<NevaEyeTracking>();
        bool oldEyeTrackingEnabled = eyes != null && eyes.enabled;
        NevaSceneAttention attention = chat.GetComponent<NevaSceneAttention>();
        bool oldAttentionEnabled = attention != null && attention.enabled;
        try
        {
            Call("InvalidateFormalResponse", "persistent-capture-fixture-start");
            Set(chat, "m_SceneActionActive", false);
            Set(chat, "m_UserSpeechActiveForAutonomy", false);
            Set(chat, "m_EnableSceneActions", true);
            Require(eyes != null && eyes.IsInitialized && eyes.Target == bridge.avatar &&
                bridge.avatar.gameObject.activeInHierarchy && !bridge.avatar.isActiveAndEnabled,
                "Fixture has the calibrated head/eye rig without a competing full-VRM body writer.");
            eyes.enabled = true;
            Call("ApplySceneVisionAttention", true);
            Require(chat.SceneEyesOpen && bridge.IsConnected && !bridge.ReservesBody && !bridge.IsFollowing,
                "Fixture has open eyes and an available stationary body owner.");
            SceneVisionObservation initial = null;
            Call("RequestFreshSceneObservation", "persistent-fixture-initial", new Action<SceneVisionObservation>(value => initial = value));
            yield return Wait(() => initial?.HasImage == true, "Initial held-gaze fixture must have actual character-camera pixels.");
            attention = attention ?? chat.gameObject.AddComponent<NevaSceneAttention>();
            attention.enabled = true;
            Set(chat, "m_SceneAttention", attention);
            if (attention.Target != bridge.avatar) attention.Configure(eyes);
            attention.Cancel("persistent-fixture-old-head");
            yield return Wait(() => !attention.IsReturning, "Previous gaze must release before fixture hold.");
            string owner = "persistent-capture-fixture-" + Guid.NewGuid().ToString("N");
            Vector3 point = eyes.EyeOrigin + eyes.HeadFrameRotation * Quaternion.Euler(-8f, 5f, 0f) * Vector3.forward * 3f;
            Require(attention.TryLookAt(owner, point, out string failure), "Fixture can acquire the actual calibrated head: " + failure);
            yield return Wait(() => attention.IsSettled, "Fixture head must physically settle before retention.");
            SceneVisionObservation confirmed = null;
            Call("RequestFreshSceneObservation", "persistent-fixture-confirm", new Action<SceneVisionObservation>(value => confirmed = value));
            yield return Wait(() => confirmed?.HasImage == true, "Held view confirmation must contain actual new pixels.");
            var factType = typeof(ChatSample).GetNestedType("SceneActionFact", BindingFlags.NonPublic);
            object fact = Activator.CreateInstance(factType, true);
            Set(fact, "actionId", owner); Set(fact, "intent", "look-at"); Set(fact, "gazeHold", "until-cancel");
            Set(fact, "confirmedCaptureId", confirmed.CaptureId); Set(fact, "visualLabel", "fixture-held-direction");
            Set(fact, "phase", "observed"); Set(chat, "m_SceneActionFact", fact);
            Set(chat, "m_RoomTaskIntent", "look-at"); Set(chat, "m_RoomTaskActionId", owner); Set(chat, "m_RoomTaskDispatch", "observed");
            Require((bool)Call("RetainSceneAttention", fact), "Actual fresh, settled gaze must create the independent verifier.");
            object task = Get(chat, "m_PersistentSceneAttention");
            var heldStamp = ((JObject)Call("RoomSpeechSceneStamp")).DeepClone();
            Require(!(bool)Call("HasDefinitiveRoomActionFailure"), "A physically held matching gaze is not reported as a failed action.");
            float priorConfirmation = (float)Get(task, "lastConfirmedRealtime");
            yield return Wait(() => (bool)Get(task, "refreshPending"), "Persistent verifier must reach its real asynchronous fresh capture.");
            Call("InvalidateFormalResponse", "user-started-speaking");
            Require((bool)Get(task, "active") && attention.IsPersistent && attention.OwnerId == owner,
                "Full speech invalidation during a real capture preserves the independent gaze owner.");
            yield return Wait(() => !(bool)Get(task, "active") ||
                (!(bool)Get(task, "refreshPending") && (float)Get(task, "lastConfirmedRealtime") > priorConfirmation),
                "Cancelled visual capture must be replaced within the existing pose lease.");
            Require((bool)Get(task, "active") && attention.IsPersistent && attention.OwnerId == owner &&
                (string)Get(task, "captureId") != confirmed.CaptureId,
                "A genuinely new image renews gaze after dialogue reset; a cancelled or stale callback cannot renew it.");
            Require(JToken.DeepEquals(heldStamp, (JObject)Call("RoomSpeechSceneStamp")),
                "A fresh verification capture alone does not invalidate stable held-gaze speech dependencies.");
            Call("ApplySceneVisionAttention", false);
            Require(!(bool)Get(task, "active") && !attention.IsPersistent,
                "Closing eyes still releases the task immediately after ordinary dialogue survival.");
            Require(!JToken.DeepEquals(heldStamp, (JObject)Call("RoomSpeechSceneStamp")),
                "A released gaze changes the speech dependency stamp even while its historical action remains observed.");
            Require((bool)Call("HasDefinitiveRoomActionFailure"),
                "The same current hold task ending blocks an unchecked claim that the pose is still held.");
            string partial = (string)Call("CurrentRoomActionReceipt", "en");
            Require(partial != null && partial.Contains("did look") && partial.Contains("no longer holding"),
                "The receipt preserves the real earlier look and accurately reports that continued holding ended.");
            Set(chat, "m_RoomTaskActionId", owner + "-replacement");
            Require(!(bool)Call("HasDefinitiveRoomActionFailure") && Call("CurrentRoomActionReceipt", "en") == null,
                "A previous released hold cannot become the failure or receipt of a different action.");
        }
        finally
        {
            Call("InvalidateFormalResponse", "persistent-capture-fixture-finished");
            if (attention != null) { attention.enabled = false; attention.enabled = oldAttentionEnabled; }
            if (eyes != null) eyes.enabled = oldEyeTrackingEnabled;
            Set(chat, "m_EnableSceneActions", oldSceneActions);
            Call("ApplySceneVisionAttention", oldEyes);
            Set(chat, "m_SceneActionFact", oldFact);
            Set(chat, "m_RoomTaskIntent", oldIntent); Set(chat, "m_RoomTaskActionId", oldAction); Set(chat, "m_RoomTaskDispatch", oldDispatch);
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "persistent-gaze-capture.json"), new JObject {
                ["checks"] = checkedCount, ["scope"] = "Actual camera, calibrated head, persistent verifier and full dialogue cancellation; no network model or microphone."
            }.ToString());
        }
        yield return null;
    }

    private static void LifecycleCases()
    {
        var host = new GameObject("Persistent attention cancellation regression");
        host.SetActive(false); // Never initialize a real chat, microphone, camera or model.
        var chat = host.AddComponent<ChatSample>();
        var attention = host.AddComponent<NevaSceneAttention>();
        var type = typeof(ChatSample).GetNestedType("PersistentSceneAttentionTask", BindingFlags.NonPublic);
        object Task(string id)
        {
            object task = Activator.CreateInstance(type, true);
            type.GetField("actionId").SetValue(task, id);
            return task;
        }
        void Put(string id, object task)
        {
            typeof(ChatSample).GetField("m_PersistentSceneAttention", Hidden).SetValue(chat, task);
            typeof(ChatSample).GetField("m_SceneAttention", Hidden).SetValue(chat, attention);
            typeof(NevaSceneAttention).GetProperty("OwnerId").SetValue(attention, id);
            typeof(NevaSceneAttention).GetProperty("State").SetValue(attention, "settled");
            typeof(NevaSceneAttention).GetProperty("IsPersistent").SetValue(attention, true);
        }
        void Cancel(string reason) => typeof(ChatSample).GetMethod("CancelSceneAction", Hidden).Invoke(chat, new object[] { reason });
        try
        {
            foreach (string reason in new[] { "new-user-turn", "user-started-speaking", "barge-in" })
            {
                var task = Task("held"); Put("held", task); Cancel(reason);
                Check((bool)type.GetField("active").GetValue(task) && attention.IsSettled && attention.OwnerId == "held",
                    "Completed persistent gaze must survive actual dialogue cancellation: " + reason);
                int visionEpoch = (int)typeof(ChatSample).GetField("m_SceneVisionEpoch", Hidden).GetValue(chat);
                typeof(ChatSample).GetMethod("InvalidateFormalResponse", Hidden).Invoke(chat, new object[] { reason });
                Check((bool)type.GetField("active").GetValue(task) && attention.IsSettled && attention.OwnerId == "held" &&
                    (int)typeof(ChatSample).GetField("m_SceneVisionEpoch", Hidden).GetValue(chat) > visionEpoch,
                    "Full formal invalidation must clear visual response ownership while preserving held gaze: " + reason);
            }
            foreach (string reason in new[] { "explicit-stop", "eyes-closed", "application-paused", "chat-disabled", "room-output-disabled" })
            {
                var task = Task("held"); Put("held", task); Cancel(reason);
                Check(!(bool)type.GetField("active").GetValue(task) && attention.IsReturning && !attention.IsPersistent,
                    "Safety and explicit stops must release the actual task and head lease: " + reason);
            }
            var obsolete = Task("obsolete");
            var current = Task("current"); Put("current", current);
            typeof(ChatSample).GetMethod("EndPersistentSceneAttention", Hidden).Invoke(chat, new[] { obsolete, "late-old-observation" });
            Check(attention.OwnerId == "current" && attention.IsSettled && (bool)type.GetField("active").GetValue(current),
                "A delayed old verifier cannot release a replacement target.");
            typeof(ChatSample).GetMethod("CancelPersistentSceneAttention", Hidden).Invoke(chat, new object[] { "explicit-stop-looking" });
            Check(!(bool)type.GetField("active").GetValue(current) && attention.IsReturning,
                "Semantic stop-looking releases the exact current held gaze.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }
#endif
}
