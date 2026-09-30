#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using NeEEvA.Presentation;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>Actual presentation PTT handlers and capture state with an offline native microphone boundary.</summary>
public static class CompanionPttOwnershipRegression
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly List<string> checks = new List<string>();

    public static int RunChecks()
    {
        if (!Application.isBatchMode ||
            !Application.dataPath.Replace('\\', '/').Contains("/work/vr-ptt-fix-20260923/unity-fixture/"))
            throw new InvalidOperationException("Run PTT ownership checks in the isolated batch fixture.");
        checks.Clear();
        using (var fixture = new Fixture())
        {
            Check(!fixture.Host.activeInHierarchy && !fixture.View.gameObject.activeInHierarchy &&
                  fixture.View.GetComponent<CompanionXRInteraction>() == null,
                "Offline fixture never activates chat lifecycle or real XR input.");

            for (int round = 0; round < 3; round++)
            {
                fixture.TriggerDown();
                fixture.ExpectRecording(true, "Trigger round " + round + " begins a real manual capture.");
                Call(fixture.View, "UpdateSpeechStatus");
                Check(Get<Text>(fixture.View, "recordLabel").text == "松开发送" &&
                      Get<Text>(fixture.View, "statusText").text == "正在听你说",
                    "Each successful hold visibly changes the button and listening status (round " + round + ").");
                fixture.TriggerUp();
                fixture.ExpectRecording(false, "Trigger round " + round + " releases both presentation and capture state.");
                Call(fixture.View, "UpdateSpeechStatus");
                Check(Get<Text>(fixture.View, "recordLabel").text == "按住说话",
                    "Each release restores the button for the next hold (round " + round + ").");
            }
            Check(fixture.Speech.Calls.FindAll(call => call == "start:<default>").Count == 1,
                "Repeated trigger turns reuse the selected native microphone session.");

            fixture.TriggerDown();
            fixture.Pointer(EventTriggerType.PointerDown, 41);
            fixture.Pointer(EventTriggerType.PointerUp, 41);
            fixture.Pointer(EventTriggerType.PointerUp, 99);
            fixture.ExpectRecording(true, "UI pointer releases cannot end a trigger-owned capture.");
            fixture.TriggerUp();
            fixture.ExpectRecording(false, "The owning trigger still ends the capture after interleaved UI input.");

            fixture.Pointer(EventTriggerType.PointerDown, 41);
            fixture.TriggerDown();
            fixture.TriggerUp();
            fixture.ExpectRecording(true, "Trigger release cannot end a pointer-owned capture.");
            fixture.Pointer(EventTriggerType.PointerDown, 42);
            fixture.Pointer(EventTriggerType.PointerUp, 42);
            fixture.ExpectRecording(true, "A second pointer cannot steal or release the first pointer's capture.");
            fixture.Pointer(EventTriggerType.PointerUp, 41, PointerEventData.InputButton.Right);
            fixture.ExpectRecording(true, "A right-button release cannot end a left-button hold on the same pointer.");
            fixture.Pointer(EventTriggerType.PointerUp, 41);
            fixture.ExpectRecording(false, "The original UI pointer ends its capture after other inputs interleave.");
            fixture.Pointer(EventTriggerType.PointerDown, 41, PointerEventData.InputButton.Right);
            fixture.ExpectRecording(false, "Right-button input does not start push-to-talk.");

            fixture.View.SetPanelVisible(true);
            fixture.TriggerDown();
            fixture.View.SetPanelVisible(false);
            fixture.ExpectRecording(true, "Hiding the panel does not end the physical trigger's capture.");
            Call(fixture.View, "SelectPage", 1);
            fixture.ExpectRecording(true, "Switching to history does not end the physical trigger's capture.");
            Call(fixture.View, "SelectPage", 2);
            fixture.ExpectRecording(true, "Switching to settings does not end the physical trigger's capture.");
            fixture.TriggerUp();
            fixture.ExpectRecording(false, "Trigger release still works after panel and page changes.");

            Call(fixture.View, "SelectPage", 0);
            fixture.View.SetPanelVisible(true);
            fixture.Pointer(EventTriggerType.PointerDown, 51);
            fixture.ExpectRecording(true, "The visible UI starts a pointer-owned hold before panel closure.");
            fixture.View.SetPanelVisible(false);
            fixture.ExpectRecording(false, "Hiding the UI ends a pointer-owned hold.");
            fixture.Pointer(EventTriggerType.PointerUp, 51);
            fixture.View.SetPanelVisible(true);
            fixture.Pointer(EventTriggerType.PointerDown, 52);
            fixture.ExpectRecording(true, "The UI starts another hold before changing pages.");
            Call(fixture.View, "SelectPage", 1);
            fixture.ExpectRecording(false, "Leaving the input page ends a pointer-owned hold.");
            fixture.Pointer(EventTriggerType.PointerUp, 52);
            Call(fixture.View, "SelectPage", 0);
            fixture.Pointer(EventTriggerType.PointerDown, 53);
            fixture.ExpectRecording(true, "Returning to the input page permits a fresh UI capture.");
            fixture.Pointer(EventTriggerType.PointerUp, 53);
            fixture.ExpectRecording(false, "UI input starts and releases a fresh turn after a page-close cancellation.");

            fixture.TriggerDown();
            Call(fixture.View, "OnApplicationFocus", false);
            fixture.ExpectRecording(true, "Losing Windows focus does not release the VR presentation's trigger hold.");
            Call(fixture.Speech, "OnApplicationFocus", false);
            fixture.ExpectRecording(true, "Losing Windows focus does not cancel the XR microphone capture.");
            Call(fixture.Speech, "OnApplicationFocus", true);
            Call(fixture.View, "OnApplicationFocus", true);
            fixture.TriggerUp();
            fixture.ExpectRecording(false, "Physical release remains effective after a VR focus round trip.");

            fixture.TriggerDown();
            Call(fixture.View, "ApplyPresentationMode", false);
            fixture.ExpectRecording(false, "Leaving VR ends the real pending capture even without a trigger release.");
            Call(fixture.View, "ApplyPresentationMode", true);
            fixture.TriggerDown();
            fixture.ExpectRecording(true, "Re-entering VR permits a new trigger capture.");
            Call(fixture.View, "OnDisable");
            fixture.ExpectRecording(false, "Disabling the presentation ends the owning capture.");
            Call(fixture.View, "OnEnable");
            fixture.TriggerDown();
            fixture.ExpectRecording(true, "Re-enabling the presentation permits another capture.");
            fixture.TriggerUp();

            // Exercise the microphone lifecycle independently, without entering
            // desktop VoiceInputs or activating the inactive ChatSample host.
            Check(fixture.Speech.BeginManualCapture(), "Direct microphone fixture capture starts.");
            Call(fixture.Speech, "OnDisable");
            Check(!fixture.Speech.IsManualCaptureActive,
                "Disabling the microphone component still cancels an XR hold.");
            Check(fixture.Speech.BeginManualCapture(), "Microphone capture can restart after cancellation.");
            fixture.Speech.XrActive = false;
            Call(fixture.Speech, "OnApplicationFocus", false);
            Check(!fixture.Speech.IsManualCaptureActive,
                "Desktop focus loss still cancels a manual microphone capture.");
            fixture.Speech.XrActive = true;

            fixture.TriggerDown();
            Set(fixture.View, "vrActive", false);
            Call(fixture.View, "OnApplicationFocus", false);
            fixture.ExpectRecording(false,
                "Desktop presentation focus loss still releases a pending hold through its original capture route.");
            Set(fixture.View, "vrActive", true);

            Set(fixture.Speech, "m_IsRecording", true);
            fixture.Pointer(EventTriggerType.PointerDown, 60);
            fixture.ExpectRecording(false, "A failed microphone start does not claim pointer ownership.");
            Set(fixture.Speech, "m_IsRecording", false);
            fixture.TriggerDown();
            fixture.Pointer(EventTriggerType.PointerUp, 60);
            fixture.ExpectRecording(true, "A stale release from the failed input cannot stop the next successful owner.");
            fixture.TriggerUp();

            Set(fixture.Speech, "m_AwakeState", true);
            fixture.TriggerDown();
            fixture.Pointer(EventTriggerType.PointerDown, 61);
            fixture.ExpectRecording(false, "Realtime listening blocks both PTT inputs using current business state.");
            Set(fixture.Speech, "m_AwakeState", false);
            Get<Button>(fixture.View, "recordButton").interactable = false;
            fixture.TriggerDown();
            fixture.ExpectRecording(true, "A stale disabled button frame does not block an eligible trigger hold.");
            fixture.TriggerUp();
            Get<Button>(fixture.View, "recordButton").interactable = true;

            fixture.Pointer(EventTriggerType.PointerDown, 71);
            fixture.ExpectRecording(true, "A final UI round starts after focus, disable and mode transitions.");
            fixture.Pointer(EventTriggerType.PointerUp, 71);
            fixture.ExpectRecording(false, "A final UI round ends without leftover ownership.");
            Check(!fixture.Host.activeInHierarchy && !fixture.View.gameObject.activeInHierarchy &&
                  Get<object>(fixture.Chat, "m_ChatSettings") == null,
                "All checks kept chat inactive and left model/service configuration unset.");
        }
        Debug.Log("[CompanionPttOwnershipRegression] passed " + checks.Count + " checks");
        return checks.Count;
    }

    private sealed class Fixture : IDisposable
    {
        public readonly GameObject Host;
        public readonly ChatSample Chat;
        public readonly RealtimeMicrophoneRegressionProbe Speech;
        public readonly CompanionPresentation View;
        private readonly GameObject cameraObject, eventObject;
        private readonly EventSystem eventSystem;
        private readonly EventTrigger pointerEvents;

        public Fixture()
        {
            Host = new GameObject("Inactive PTT ownership chat");
            Host.SetActive(false);
            Chat = Host.AddComponent<ChatSample>();
            Speech = Host.AddComponent<RealtimeMicrophoneRegressionProbe>();
            Speech.XrActive = true;
            // A stationary zero cursor and no pre-roll yield no clip to upload.
            // The real EndManualCapture short-input path still releases ownership.
            Speech.Position = 0;
            Speech.m_ManualCapturePreRollSeconds = 0f;
            Speech.m_ManualCaptureMinSeconds = 1f;
            Set(Speech, "m_ChatSample", Chat);
            Set(Chat, "m_RTSpeechHandler", Speech);
            Set(Chat, "m_PresentationRealtime", Speech);

            cameraObject = new GameObject("Inactive PTT ownership camera", typeof(Camera));
            cameraObject.SetActive(false);
            eventObject = new GameObject("Inactive PTT ownership event data");
            eventObject.SetActive(false);
            eventSystem = eventObject.AddComponent<EventSystem>();
            var viewObject = new GameObject("Inactive PTT ownership presentation");
            viewObject.SetActive(false);
            View = viewObject.AddComponent<CompanionPresentation>();
            View.ConfigureEditorScenePreview(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"),
                cameraObject.GetComponent<Camera>());
            Set(View, "chat", Chat);
            Set(View, "preview", false);
            Call(View, "ApplyPresentationMode", true);
            pointerEvents = Get<Button>(View, "recordButton").GetComponent<EventTrigger>();
            Check(pointerEvents != null && pointerEvents.triggers.Count == 2,
                "Tests invoke the real record button's pointer-down and pointer-up listeners.");
        }

        public void TriggerDown() => Call(View, "BeginTriggerRecord");
        public void TriggerUp() => Call(View, "EndTriggerRecord");
        public void Pointer(EventTriggerType type, int id,
            PointerEventData.InputButton button = PointerEventData.InputButton.Left)
        {
            var data = new PointerEventData(eventSystem) { pointerId = id, button = button };
            foreach (EventTrigger.Entry entry in pointerEvents.triggers)
                if (entry.eventID == type) entry.callback.Invoke(data);
        }

        public void ExpectRecording(bool expected, string reason)
        {
            Check(Speech.IsManualCaptureActive == expected && Get<bool>(View, "recording") == expected &&
                  Chat.IsPicoPushToTalkActive == expected, reason);
        }

        public void Dispose()
        {
            Call(View, "EndRecord");
            Call(Speech, "CancelManualCapture");
            View.DisposeEditorScenePreview();
            Object.DestroyImmediate(View.gameObject);
            foreach (AudioClip clip in Speech.Clips) if (clip != null) Object.DestroyImmediate(clip);
            Object.DestroyImmediate(Host);
            Object.DestroyImmediate(eventObject);
            Object.DestroyImmediate(cameraObject);
        }
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
        checks.Add(reason);
    }

    private static FieldInfo FindField(object target, string name)
    {
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, Members | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        throw new MissingFieldException(target.GetType().FullName, name);
    }

    private static T Get<T>(object target, string name) => (T)FindField(target, name).GetValue(target);
    private static void Set(object target, string name, object value) => FindField(target, name).SetValue(target, value);
    private static void Call(object target, string name, params object[] arguments)
    {
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            MethodInfo method = type.GetMethod(name, Members | BindingFlags.DeclaredOnly);
            if (method == null) continue;
            try { method.Invoke(target, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
            return;
        }
        throw new MissingMethodException(target.GetType().FullName, name);
    }
}
#endif
