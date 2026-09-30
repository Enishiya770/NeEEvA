#if UNITY_EDITOR
using System;
using System.Reflection;
using NeEEvA.Presentation;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Object = UnityEngine.Object;

/// <summary>Actual trigger reader and edge ownership with isolated simulated controls; no XR rig or microphone.</summary>
public static class CompanionXrPushToTalkRegression
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static int RunChecks()
    {
        int checks = 0;
        Action<bool, string> check = (passed, message) =>
        {
            if (!passed) throw new InvalidOperationException(message);
            checks++;
        };
        var host = new GameObject("Inactive PTT input regression");
        host.SetActive(false);
        var interaction = host.AddComponent<CompanionXRInteraction>();
        Gamepad device = InputSystem.AddDevice<Gamepad>("OfflinePttInput");
        var axis = new InputAction("Offline trigger axis", InputActionType.Value, device.leftTrigger.path);
        var button = new InputAction("Offline trigger button", InputActionType.Button, device.leftShoulder.path);
        InputSettings settings = InputSystem.settings;
        InputSettings.UpdateMode previousUpdateMode = settings.updateMode;
        const string editModeFeature = "RUN_PLAYER_UPDATES_IN_EDIT_MODE";
        bool previousEditModeUpdates = (bool)typeof(InputSettings).GetMethod("IsFeatureEnabled", Fields)
            .Invoke(settings, new object[] { editModeFeature });
        try
        {
            // Edit-mode Editor updates intentionally skip InputAction processing.
            // Use the package's test feature and player buffer without entering Play Mode.
            settings.SetInternalFeatureFlag(editModeFeature, true);
            settings.updateMode = InputSettings.UpdateMode.ProcessEventsInDynamicUpdate;
            Set(interaction, "leftTriggerAction", axis);
            Set(interaction, "leftTriggerButtonAction", button);
            axis.Enable(); button.Enable();
            check(axis.controls.Count == 1 && button.controls.Count == 1 &&
                  ReferenceEquals(axis.controls[0].device, device) && ReferenceEquals(button.controls[0].device, device),
                "Both test actions bind only the isolated simulated device.");
            InputSystem.QueueStateEvent(device, new GamepadState().WithButton(GamepadButton.LeftShoulder));
            UpdateGameInput();
            check(button.IsPressed(), "The independent fallback button is deliberately held throughout the analog release checks.");
            check(Read(interaction) == 0f,
                "A bound modern trigger at zero must override a stale pressed button instead of OR-latching PTT.");

            int starts = 0, ends = 0;
            interaction.PushToTalkStarted += () => starts++;
            interaction.PushToTalkEnded += () => ends++;
            for (int cycle = 1; cycle <= 4; cycle++)
            {
                InputSystem.QueueDeltaStateEvent(device.leftTrigger, .8f); UpdateGameInput();
                Edge(interaction, Read(interaction), false);
                Edge(interaction, Read(interaction), false);
                check(starts == cycle && ends == cycle - 1,
                    "A fresh analog press starts once after each previous release (cycle " + cycle + ").");
                InputSystem.QueueDeltaStateEvent(device.leftTrigger, .45f); UpdateGameInput();
                Edge(interaction, Read(interaction), false);
                check(ends == cycle - 1, "Analog hysteresis preserves an active hold above the release threshold.");
                InputSystem.QueueDeltaStateEvent(device.leftTrigger, 0f); UpdateGameInput();
                Edge(interaction, Read(interaction), false);
                check(ends == cycle && button.IsPressed(),
                    "Releasing the authoritative axis ends capture despite the stale digital button (cycle " + cycle + ").");
            }

            axis.Disable();
            check(Read(interaction) == 1f, "A usable modern button still works when the modern analog action is unavailable.");
            InputSystem.QueueStateEvent(device, new GamepadState()); UpdateGameInput();
            check(Read(interaction) == 0f, "The button-only modern fallback also reports its own release authoritatively.");
            axis.Enable(); UpdateGameInput();

            CheckUiConsumers(check);

            Edge(interaction, 1f, true);
            Edge(interaction, 1f, false);
            check(starts == 4 && ends == 4,
                "A press claimed by the fallback left UI ray cannot start shortcut PTT when hover moves away while held.");
            Edge(interaction, 0f, false);
            check(starts == 4 && ends == 4, "Releasing a UI-owned press does not end another PTT owner.");
            Edge(interaction, 1f, false);
            Edge(interaction, 1f, true);
            check(starts == 5 && ends == 4,
                "A shortcut-owned hold stays with PTT when its ray later moves onto UI.");
            Edge(interaction, 0f, true);
            check(starts == 5 && ends == 5, "The shortcut-owned press ends exactly once on physical release.");
            Edge(interaction, 1f, false);
            Set(interaction, "<IsVRActive>k__BackingField", true);
            interaction.SetVRActive(false);
            check(starts == 6 && ends == 6 && !(bool)Get(interaction, "leftTriggerPressed") &&
                  !(bool)Get(interaction, "leftTriggerOwnsPushToTalk"),
                "Leaving VR releases a shortcut-owned hold and clears both physical and PTT ownership state.");
            CheckUiEvents(interaction, device, check);
            check(!host.activeInHierarchy && host.transform.childCount == 0,
                "The regression never enables the controller component or builds an XR rig.");
            Debug.Log("[CompanionXrPushToTalkRegression] passed " + checks + " simulated-input checks; no headset, microphone or ASR execution.");
            return checks;
        }
        finally
        {
            axis.Dispose(); button.Dispose();
            InputSystem.RemoveDevice(device);
            settings.SetInternalFeatureFlag(editModeFeature, previousEditModeUpdates);
            settings.updateMode = previousUpdateMode;
            Object.DestroyImmediate(host);
        }
    }

    private static float Read(CompanionXRInteraction interaction) =>
        (float)typeof(CompanionXRInteraction).GetMethod("ReadLeftTriggerValue", Fields).Invoke(interaction, null);
    private static void Edge(CompanionXRInteraction interaction, float value, bool ui) =>
        typeof(CompanionXRInteraction).GetMethod("UpdatePushToTalk", Fields).Invoke(interaction, new object[] { value, ui });
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Fields).SetValue(target, value);
    private static object Get(object target, string name) => target.GetType().GetField(name, Fields).GetValue(target);

    private static void UpdateGameInput() =>
        typeof(InputSystem).GetMethod("Update", BindingFlags.Static | BindingFlags.NonPublic,
            null, new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });

    private static void CheckUiConsumers(Action<bool, string> check)
    {
        var background = new GameObject("Raycastable panel background", typeof(RectTransform), typeof(CompanionSurface));
        var button = new GameObject("UI button", typeof(RectTransform), typeof(Image), typeof(Button));
        var events = new GameObject("UI hold event", typeof(RectTransform), typeof(Image), typeof(EventTrigger));
        var scroll = new GameObject("UI scroll", typeof(RectTransform), typeof(ScrollRect));
        var viewport = new GameObject("Scroll viewport hit", typeof(RectTransform), typeof(Image));
        try
        {
            background.GetComponent<CompanionSurface>().raycastTarget = true;
            check(!Consumes(background), "A raycastable panel background without an input handler cannot swallow shortcut PTT.");
            check(Consumes(button), "A real Button can claim the physical press for UI.");
            var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            entry.callback.AddListener(_ => { });
            events.GetComponent<EventTrigger>().triggers.Add(entry);
            check(Consumes(events), "The actual EventTrigger used by the hold-to-talk UI can claim its pointer press.");
            viewport.transform.SetParent(scroll.transform, false);
            viewport.GetComponent<Image>().raycastTarget = true;
            check(Consumes(viewport), "A ScrollRect drag handler on a hit target's parent can claim UI input.");
            check(!Consumes(null), "No raycast target leaves the shortcut available.");
        }
        finally
        {
            Object.DestroyImmediate(background); Object.DestroyImmediate(button); Object.DestroyImmediate(events);
            Object.DestroyImmediate(scroll);
        }
    }

    private static bool Consumes(GameObject target) =>
        (bool)typeof(CompanionXRInteraction).GetMethod("CanConsumeTriggerPress", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { target });

    private static void CheckUiEvents(CompanionXRInteraction interaction, Gamepad device, Action<bool, string> check)
    {
        EventSystem previous = EventSystem.current;
        var eventsObject = new GameObject("Offline pointer events"); eventsObject.SetActive(false);
        var events = eventsObject.AddComponent<EventSystem>();
        var module = eventsObject.AddComponent<XRUIInputModule>();
        var leftObject = new GameObject("Inactive left UI pointer"); leftObject.SetActive(false);
        var rightObject = new GameObject("Inactive right UI pointer"); rightObject.SetActive(false);
        var left = leftObject.AddComponent<XRRayInteractor>();
        var right = rightObject.AddComponent<XRRayInteractor>();
        var target = new GameObject("UI event target", typeof(RectTransform), typeof(Image), typeof(Button));
        var background = new GameObject("Repeated background event", typeof(RectTransform), typeof(Image));
        int starts = 0, ends = 0;
        Action start = () => starts++, end = () => ends++;
        interaction.PushToTalkStarted += start; interaction.PushToTalkEnded += end;
        try
        {
            Type handType = typeof(CompanionXRInteraction).GetNestedType("Hand", BindingFlags.NonPublic);
            object hand = Activator.CreateInstance(handType, true);
            Set(hand, "ray", left); Set(hand, "line", leftObject.AddComponent<LineRenderer>());
            Set(interaction, "left", hand); Set(interaction, "eventSystem", events);
            Set(events, "m_CurrentInputModule", module);
            module.RegisterInteractor(left); module.RegisterInteractor(right);
            module.GetTrackedDeviceModel(left, out var leftModel); module.GetTrackedDeviceModel(right, out var rightModel);
            var leftEvent = new TrackedDeviceEventData(events) { pointerId = leftModel.pointerId };
            var rightEvent = new TrackedDeviceEventData(events) { pointerId = rightModel.pointerId };
            Call(interaction, "ActivateXRModule");
            check(UiCallback(module, "pointerDown")?.GetInvocationList().Length == 1,
                "Activating the XR module subscribes one ownership callback.");
            UiCallback(module, "pointerDown").Invoke(target, rightEvent);
            check(!(bool)Get(interaction, "leftTriggerUiClaimed"), "A right-hand pointer event never claims the physical left trigger.");
            UiCallback(module, "pointerDown").Invoke(target, leftEvent);
            check((bool)Get(interaction, "leftTriggerUiClaimed"), "The actual module pointerDown callback identifies its registered left interactor.");
            target.SetActive(false); interaction.SetPanelVisible(false);
            UiCallback(module, "pointerUp")?.Invoke(target, leftEvent);
            ((InputAction)Get(interaction, "leftTriggerAction")).Enable();
            Set(interaction, "<IsVRActive>k__BackingField", true);
            InputSystem.QueueDeltaStateEvent(device.leftTrigger, .5f); UpdateGameInput();
            Call(interaction, "LateUpdate");
            check((bool)Get(interaction, "leftTriggerUiClaimed") && !(bool)Get(interaction, "leftTriggerPressed"),
                "A UI press at its lower threshold retains ownership before the PTT threshold is reached.");
            UiCallback(module, "pointerDown").Invoke(background, leftEvent);
            check((bool)Get(interaction, "leftTriggerUiClaimed"),
                "A repeated background pointer event during a partial hold cannot revoke an existing UI claim.");
            InputSystem.QueueDeltaStateEvent(device.leftTrigger, .8f); UpdateGameInput();
            Call(interaction, "LateUpdate");
            check(starts == 0 && !(bool)Get(interaction, "leftTriggerOwnsPushToTalk"),
                "A same-frame UI hide/close preserves the already consumed UI press and cannot start shortcut PTT in LateUpdate.");
            check((bool)Get(interaction, "leftTriggerUiClaimed"), "A forced same-frame UI pointer-up cannot clear a still-held physical UI claim.");
            InputSystem.QueueDeltaStateEvent(device.leftTrigger, 0f); UpdateGameInput();
            Call(interaction, "LateUpdate");
            check(!(bool)Get(interaction, "leftTriggerUiClaimed") && starts == 0 && ends == 0,
                "Physical release clears the UI hold even if its target disappeared and no later pointer-up arrives.");
            InputSystem.QueueDeltaStateEvent(device.leftTrigger, .8f); UpdateGameInput();
            Call(interaction, "LateUpdate");
            InputSystem.QueueDeltaStateEvent(device.leftTrigger, 0f); UpdateGameInput();
            Call(interaction, "LateUpdate");
            check(starts == 1 && ends == 1, "The next physical shortcut press is available after UI ownership clears.");
            interaction.SetVRActive(false);
            check(UiCallback(module, "pointerDown") == null && UiCallback(module, "pointerUp") == null,
                "Restoring the module removes the ownership callback without any pointer-up subscription.");
            Call(interaction, "ActivateXRModule");
            check(UiCallback(module, "pointerDown")?.GetInvocationList().Length == 1,
                "Reactivating the module does not duplicate ownership callbacks.");
            Call(interaction, "RestoreInput");
        }
        finally
        {
            interaction.PushToTalkStarted -= start; interaction.PushToTalkEnded -= end;
            Call(interaction, "RestoreInput"); Set(interaction, "<IsVRActive>k__BackingField", false);
            Set(interaction, "left", null); Set(interaction, "xrModule", null); Set(interaction, "eventSystem", null);
            Object.DestroyImmediate(target); Object.DestroyImmediate(background);
            Object.DestroyImmediate(leftObject); Object.DestroyImmediate(rightObject);
            Object.DestroyImmediate(eventsObject); EventSystem.current = previous;
        }
    }

    private static Action<GameObject, PointerEventData> UiCallback(XRUIInputModule module, string name) =>
        (Action<GameObject, PointerEventData>)typeof(UIInputModule).GetField(name, Fields).GetValue(module);
    private static void Call(object target, string name) => target.GetType().GetMethod(name, Fields).Invoke(target, null);
}
#endif
