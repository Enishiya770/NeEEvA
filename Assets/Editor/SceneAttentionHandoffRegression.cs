using System;
using System.Linq;
using System.Reflection;
using NeEEvA.Motion;
using NeEEvA.Perception;
using UniVRM10;
using UnityEngine;

/// <summary>Actual production gaze transfer, cancellation and head-overlay update using
/// controlled EditMode rig state. No network, source RGB, avatar import or microphone.</summary>
public static class SceneAttentionHandoffRegression
{
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;

    public static int RunChecks()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Run these controlled ownership checks in EditMode.");
        checks = 0;
        using (var f = new Fixture())
        {
            // Reproduce the source pose in the g73 trace: a completed ~31 degree
            // Room orient-view, followed by a new set-object-state action.
            Quaternion before = f.Head.rotation;
            var capture = (NevaSceneVisionCapture)Get(f.Chat, "m_SceneVisionCapture");
            Check(capture.TryGetCurrentCameraPose(out Pose beforeCamera), "The source pose comes from the actual calibrated camera method.");
            Check(f.HandOff("next"), "Completed Room view must transfer to accepted object preparation.");
            Check(f.Attention.OwnerId == "next" && f.Eyes.AttentionOwnerId == "next" &&
                f.Attention.LastOwnerId == "prior", "Head and eyes exchange the exact owner together.");
            Check(f.Attention.IsSettled && !f.Attention.IsReturning &&
                Quaternion.Angle(before, f.Head.rotation) < .001f,
                "Transfer cannot restore the overlay or start returning before the matcher samples its image.");
            for (int i = 0; i < 4; i++) Call(f.Attention, "LateUpdate");
            Check(Quaternion.Angle(before, f.Head.rotation) < .1f && f.Attention.IsSettled,
                "Real LateUpdate overlay logic preserves the held camera direction while matching waits.");
            Check(capture.TryGetCurrentCameraPose(out Pose afterCamera) &&
                Quaternion.Angle(beforeCamera.rotation, afterCamera.rotation) < .1f,
                "The resulting camera does not exhibit g73's 29.5-degree return while awaiting matching.");
            Check(!f.Attention.Release("prior", "old-match-finished") && f.Attention.OwnerId == "next",
                "An old match/verifier completion cannot release the successor.");
            Check((float)Get(f.Attention, "holdDeadline") > Time.realtimeSinceStartup + 20f &&
                (float)Get(f.Attention, "holdDeadline") <= Time.realtimeSinceStartup + 30.1f,
                "The preparation lease is one bounded 30-second hold, not an indefinite renewal.");
            Check(f.Attention.Release("next", "object-match-timeout") && f.Attention.IsReturning,
                "A timeout releases exactly the preparation owner and starts normal return.");
        }
        using (var f = new Fixture())
        {
            object task = f.Persistent();
            Check(f.HandOff("next"), "Persistent Room gaze may hand over its stable view.");
            Check(!(bool)Get(task, "active") && f.Attention.OwnerId == "next" && f.Attention.IsSettled,
                "The old persistent verifier is retired without returning the successor's head.");
            Call(f.Chat, "EndPersistentSceneAttention", task, "late-verifier-result");
            Check(f.Attention.OwnerId == "next" && f.Attention.IsSettled,
                "A late retired verifier cannot cancel the new matching owner.");
        }
        foreach (string failure in new[] { "old-generation", "speaking", "closed", "active-action", "body-step", "unrelated-owner", "failed-prior" })
        using (var f = new Fixture())
        {
            if (failure == "speaking") Set(f.Chat, "m_UserSpeechActiveForAutonomy", true);
            if (failure == "closed") Set(f.Chat, "m_AgentEyesOpen", false);
            if (failure == "active-action") Set(f.Chat, "m_SceneActionActive", true);
            if (failure == "unrelated-owner") Set(Get(f.Chat, "m_SceneActionFact"), "actionId", "other");
            if (failure == "failed-prior") Set(Get(f.Chat, "m_SceneActionFact"), "phase", "rejected");
            Check(!f.HandOff("next", failure == "old-generation" ? 4 : 5, failure == "body-step" ? "turn-in-place" : "set-object-state"),
                "A stale, non-object or invalid preceding view cannot transfer: " + failure);
            Check(f.Attention.OwnerId == "prior" && f.Eyes.AttentionOwnerId == "prior",
                "Rejected transfer does not rewrite current mechanical ownership: " + failure);
        }
        foreach (string failure in new[] { "wrong-head", "wrong-eye", "turning", "expired", "body-moved", "body-turned", "vrm-owner", "eyes-disabled" })
        using (var f = new Fixture())
        {
            if (failure == "wrong-eye") Set(f.Eyes, "attentionOwnerId", "other");
            if (failure == "turning") Set(f.Attention, "<State>k__BackingField", "turning");
            if (failure == "expired") Set(f.Attention, "holdDeadline", Time.realtimeSinceStartup - 1f);
            if (failure == "body-moved") f.Avatar.transform.position = Vector3.right * .03f;
            if (failure == "body-turned") f.Avatar.transform.rotation = Quaternion.Euler(0, 3f, 0);
            if (failure == "vrm-owner") f.Avatar.enabled = true;
            if (failure == "eyes-disabled") f.Eyes.enabled = false;
            Check(!f.Attention.TryTransferSettledView(failure == "wrong-head" ? "other" : "prior", "next", out _),
                "The mechanical transfer fails closed on changed rig/lease ownership: " + failure);
            Check(f.Attention.OwnerId == "prior", "Failure cannot rewrite the head owner: " + failure);
        }
        foreach (string reason in new[] { "user-started-speaking", "new-user-turn", "explicit-stop", "eyes-closed", "application-paused" })
        using (var f = new Fixture())
        {
            Check(f.HandOff("next"), "Current preparation starts before cancellation case.");
            Set(f.Chat, "m_SceneActionFact", Fact("next", "set-object-state", "preparing"));
            Set(f.Chat, "m_SceneActionActive", true);
            Call(f.Chat, "CancelSceneAction", reason);
            Check(f.Attention.IsReturning && !(bool)Get(f.Chat, "m_SceneActionActive"),
                "Cancellation during matching releases the in-flight preparation even for ordinary dialogue: " + reason);
        }
        foreach (string conflict in new[] { "body-moved", "body-turned", "different-eye-owner", "vrm-owner" })
        using (var f = new Fixture())
        {
            Check(f.HandOff("next"), "Current preparation starts before a competing-writer case.");
            if (conflict == "body-moved") f.Avatar.transform.position += Vector3.right * .03f;
            if (conflict == "body-turned") f.Avatar.transform.rotation = Quaternion.Euler(0, 3f, 0);
            if (conflict == "different-eye-owner") Set(f.Eyes, "attentionOwnerId", "other");
            if (conflict == "vrm-owner") f.Avatar.enabled = true;
            Call(f.Attention, "LateUpdate");
            Check(!f.Attention.IsBusy && f.Attention.OwnerId == null &&
                (conflict != "different-eye-owner" || f.Eyes.AttentionOwnerId == "other"),
                "An in-flight preparation yields to a changed body/rig without clearing another eye owner: " + conflict);
        }
        using (var f = new Fixture())
        {
            Check(f.HandOff("next"), "Bounded lease starts.");
            Set(f.Attention, "holdDeadline", Time.realtimeSinceStartup - 1f);
            Call(f.Attention, "LateUpdate");
            Check(f.Attention.IsReturning && f.Eyes.AttentionOwnerId == null,
                "If its supervisor disappears, the held preparation expires through the real LateUpdate path.");
        }
        using (var f = new Fixture())
        {
            int completed = 0;
            Call(f.Chat, "RecordSceneAction", Get(f.Chat, "m_SceneActionFact"), "observed", "controlled settled source");
            // No room bridge is attached: this intentionally rejects before creating
            // the next action. The real BeginSceneAction entry must not destroy the
            // preceding completed gaze before validation has accepted its successor.
            Call(f.Chat, "BeginSceneAction", new RoomTaskProtocol.Decision { intent = "set-object-state", objectState = "on" },
                null, 5, new Action(() => completed++));
            Check(completed == 1 && f.Attention.OwnerId == "prior" && f.Attention.IsSettled,
                "Rejected object proposal leaves its predecessor's bounded view intact.");
            Check(f.HandOff("corrected"),
                "A later valid proposal can still inherit the completed gaze after lastAction records the rejected proposal.");
        }
        return checks;
    }

    private static object Fact(string owner, string intent = "orient-view", string phase = "observed")
    {
        object fact = Activator.CreateInstance(typeof(ChatSample).GetNestedType("SceneActionFact", BindingFlags.NonPublic), true);
        Set(fact, "actionId", owner); Set(fact, "intent", intent); Set(fact, "phase", phase);
        return fact;
    }

    private sealed class Fixture : IDisposable
    {
        public readonly GameObject Host;
        public readonly ChatSample Chat;
        public readonly Vrm10Instance Avatar;
        public readonly NevaEyeTracking Eyes;
        public readonly NevaSceneAttention Attention;
        public readonly Transform Head;
        public Fixture()
        {
            Host = new GameObject("Room gaze handoff controlled fixture") { hideFlags = HideFlags.HideAndDontSave };
            Chat = Host.AddComponent<ChatSample>();
            Set(Chat, "m_ChatSettings", new ChatSetting());
            Set(Chat, "m_EnableScreenVision", true); Set(Chat, "m_AgentEyesOpen", true);
            Set(Chat, "m_SceneVisionApplicationPaused", false); Set(Chat, "m_FormalResponseGeneration", 5);
            Set(Chat, "m_SceneTimePaused", true); Set(Chat, "m_EnableSceneActions", true);
            Set(Chat, "m_RoomMotionOutputEnabled", true); Set(Chat, "m_MotionOutputEnabled", true);
            Avatar = Host.AddComponent<Vrm10Instance>(); Avatar.enabled = false;
            Head = new GameObject("controlled head").transform; Head.SetParent(Host.transform, false);
            var left = new GameObject("controlled left eye").transform; left.SetParent(Head, false);
            var right = new GameObject("controlled right eye").transform; right.SetParent(Head, false);
            Eyes = Host.AddComponent<NevaEyeTracking>();
            Set(Eyes, "avatar", Avatar); Set(Eyes, "head", Head); Set(Eyes, "leftEye", left); Set(Eyes, "rightEye", right);
            Set(Eyes, "calibrated", true); Set(Eyes, "headFrameLocalRotation", Quaternion.identity);
            var capture = Host.AddComponent<NevaSceneVisionCapture>();
            Set(capture, "avatar", Avatar); Set(capture, "eyeTracking", Eyes); Set(Chat, "m_SceneVisionCapture", capture);
            Attention = Host.AddComponent<NevaSceneAttention>(); Attention.Configure(Eyes);
            Set(Chat, "m_SceneAttention", Attention); Set(Chat, "m_SceneActionFact", Fact("prior"));
            Head.localRotation = Quaternion.Euler(0, 31.4f, 0);
            Set(Attention, "<OwnerId>k__BackingField", "prior"); Set(Attention, "<State>k__BackingField", "settled");
            Set(Attention, "currentAngles", new Vector2(31.4f, 0)); Set(Attention, "baselineLocal", Quaternion.identity);
            Set(Attention, "appliedLocal", Head.localRotation); Set(Attention, "hasApplied", true);
            Set(Attention, "requestRootPosition", Host.transform.position); Set(Attention, "requestRootRotation", Host.transform.rotation);
            Set(Attention, "holdDeadline", Time.realtimeSinceStartup + 20f);
            Set(Attention, "<TargetPoint>k__BackingField", Eyes.EyeOrigin + Head.forward * 3f);
            if (!Eyes.TrySetAttentionTarget("prior", Attention.TargetPoint)) throw new InvalidOperationException("Fixture eye owner unavailable.");
        }
        public bool HandOff(string next, int generation = 5, string intent = "set-object-state") =>
            (bool)Call(Chat, "TryHandOffSceneViewToObjectPreparation", Fact(next, intent, "preparing"), generation);
        public object Persistent()
        {
            var task = Activator.CreateInstance(typeof(ChatSample).GetNestedType("PersistentSceneAttentionTask", BindingFlags.NonPublic), true);
            Set(task, "actionId", "prior"); Set(Chat, "m_PersistentSceneAttention", task);
            Set(Attention, "<IsPersistent>k__BackingField", true); return task;
        }
        public void Dispose() => UnityEngine.Object.DestroyImmediate(Host);
    }

    private static object Get(object value, string field) => value.GetType().GetField(field, F).GetValue(value);
    private static void Set(object value, string field, object next) => value.GetType().GetField(field, F).SetValue(value, next);
    private static object Call(object value, string method, params object[] args)
    {
        try { return value.GetType().GetMethods(F).Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(value, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static void Check(bool value, string message) { ++checks; if (!value) throw new InvalidOperationException(message); }
}
