using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace NeEEvA.Presentation
{
    /// <summary>
    /// UI-only XRI rig. Configure with the existing player camera and the world canvas roots.
    /// Call SetVRActive from the camera controller's VR state, and SetPanelVisible when a
    /// panel is open. Left Y (secondaryButton) toggles on release; hold for 0.7 s to request
    /// panel recentering. Holding the left trigger emits push-to-talk start/end events;
    /// neither action touches the headset pose or the platform menu.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(-29995)]
    public sealed class CompanionXRInteraction : MonoBehaviour
    {
        public event Action TogglePanelRequested;
        public event Action RecenterRequested;
        public event Action PushToTalkStarted;
        public event Action PushToTalkEnded;
        public bool IsVRActive { get; private set; }

        private const float RecenterHoldSeconds = 0.7f;
        private const float TriggerPressThreshold = 0.55f;
        private const float TriggerReleaseThreshold = 0.4f;
        private readonly List<InputAction> actions = new List<InputAction>();
        private readonly List<Canvas> nestedCanvases = new List<Canvas>();
        private readonly Dictionary<Behaviour, bool> suspended = new Dictionary<Behaviour, bool>();
        private readonly Dictionary<GraphicRaycaster, bool> graphicStates = new Dictionary<GraphicRaycaster, bool>();
        private readonly List<TrackedDeviceGraphicRaycaster> ownedRaycasters = new List<TrackedDeviceGraphicRaycaster>();
        private Camera view;
        private Canvas[] worldRoots = Array.Empty<Canvas>();
        private EventSystem eventSystem;
        private XRUIInputModule xrModule;
        private bool ownsEventSystem, ownsXRModule, panelVisible, moduleWasEnabled;
        private XRUIInputModule.ActiveInputMode previousInputMode;
        private bool previousXRInput, previousMouseInput, previousFallback;
        private GameObject rig;
        private Hand right, left;
        private InputAction panelAction;
        private InputAction leftTriggerAction, leftTriggerButtonAction;
        private Material rayMaterial;
        private Material fallbackControllerMaterial;
        private float panelPressedAt = -1f, nextCanvasScan;
        private bool recenterSent;
        private bool legacyPanelPressed;
        private bool leftTriggerPressed;
        private bool leftTriggerOwnsPushToTalk;
        private bool leftTriggerUiClaimed;
        private string leftTriggerSource;
        private float inputDiagnosticAt = -1f;
        private bool inputDiagnosticLogged;

        private sealed class Hand
        {
            internal Transform transform;
            internal ActionBasedController controller;
            internal XRRayInteractor ray;
            internal LineRenderer line;
            internal InputAction tracked, trackingState, press;
        }

        public void Configure(Camera camera, Canvas[] worldCanvases)
        {
            view = camera;
            worldRoots = worldCanvases ?? Array.Empty<Canvas>();
            EnsureEventSystem();
            RefreshCanvasRaycasters();
        }

        public void SetVRActive(bool active)
        {
            if (active == IsVRActive) return;
            if (active && (view == null || !isActiveAndEnabled)) return;
            IsVRActive = active;
            panelPressedAt = -1f;
            recenterSent = false;
            legacyPanelPressed = false;
            leftTriggerUiClaimed = false;
            if (!active && leftTriggerOwnsPushToTalk)
            {
                leftTriggerPressed = false;
                leftTriggerOwnsPushToTalk = false;
                PushToTalkEnded?.Invoke();
            }
            else
            {
                leftTriggerPressed = false;
                leftTriggerOwnsPushToTalk = false;
            }
            inputDiagnosticLogged = false;
            inputDiagnosticAt = active ? Time.unscaledTime + 0.75f : -1f;
            if (active)
            {
                EnsureEventSystem();
                ActivateXRModule();
                BuildRig();
                if (rig != null) rig.SetActive(true);
                panelAction?.Enable();
                leftTriggerAction?.Enable();
                leftTriggerButtonAction?.Enable();
                RefreshCanvasRaycasters();
            }
            else
            {
                if (rig != null) rig.SetActive(false);
                panelAction?.Disable();
                leftTriggerAction?.Disable();
                leftTriggerButtonAction?.Disable();
                RestoreInput();
            }
        }

        /// <summary>Keep false for subtitle-only mode; Y still works when the rays are hidden.</summary>
        public void SetPanelVisible(bool visible)
        {
            panelVisible = visible;
            if (!visible)
            {
                SetHandEnabled(right, false);
                SetHandEnabled(left, false);
            }
            nextCanvasScan = 0f;
        }

        private void EnsureEventSystem()
        {
            if (eventSystem != null) return;
            eventSystem = EventSystem.current;
            if (eventSystem == null) eventSystem = FindObjectOfType<EventSystem>();
            if (eventSystem == null)
            {
                eventSystem = new GameObject("Companion EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
                ownsEventSystem = true;
            }
            // Preserve existing desktop modules. Only supply one when the scene has none.
            if (eventSystem.GetComponents<BaseInputModule>().Length == 0)
            {
                var desktopModule = eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
                desktopModule.AssignDefaultActions();
            }
        }

        private void ActivateXRModule()
        {
            xrModule = eventSystem.GetComponent<XRUIInputModule>();
            if (xrModule == null)
            {
                xrModule = eventSystem.gameObject.AddComponent<XRUIInputModule>();
                ownsXRModule = true;
            }
            moduleWasEnabled = !ownsXRModule && xrModule.enabled;
            previousInputMode = xrModule.activeInputMode;
            previousXRInput = xrModule.enableXRInput;
            previousMouseInput = xrModule.enableMouseInput;
            previousFallback = xrModule.enableBuiltinActionsAsFallback;
            xrModule.pointerDown -= OnUiPointerDown;
            xrModule.pointerDown += OnUiPointerDown;
            foreach (var module in eventSystem.GetComponents<BaseInputModule>())
            {
                if (module == xrModule) continue;
                suspended[module] = module.enabled;
                module.enabled = false;
            }
            // Additive scenes occasionally contain a second EventSystem. Suspend and restore it,
            // instead of creating another system or destroying someone else's input components.
            foreach (var other in FindObjectsOfType<EventSystem>())
            {
                if (other == eventSystem) continue;
                suspended[other] = other.enabled;
                other.enabled = false;
            }
            EventSystem.current = eventSystem;
            xrModule.activeInputMode = XRUIInputModule.ActiveInputMode.InputSystemActions;
            xrModule.enableXRInput = true;
            xrModule.enableMouseInput = false;
            xrModule.enableBuiltinActionsAsFallback = true;
            xrModule.enabled = true;
        }

        private void RestoreInput()
        {
            if (xrModule != null)
            {
                xrModule.pointerDown -= OnUiPointerDown;
                xrModule.enabled = false;
                xrModule.activeInputMode = previousInputMode;
                xrModule.enableXRInput = previousXRInput;
                xrModule.enableMouseInput = previousMouseInput;
                xrModule.enableBuiltinActionsAsFallback = previousFallback;
                xrModule.enabled = moduleWasEnabled;
            }
            foreach (var item in suspended)
                if (item.Key != null) item.Key.enabled = item.Value;
            suspended.Clear();
            foreach (var item in graphicStates)
                if (item.Key != null) item.Key.enabled = item.Value;
            graphicStates.Clear();
        }

        private void BuildRig()
        {
            if (rig != null) return;
            // PlayerCameraController already supplies the calibrated, unit-scale tracking origin.
            // Controller poses are local to that same origin, never children of the moving HMD.
            rig = new GameObject("Companion UI Controllers");
            rig.SetActive(false);
            rig.transform.SetParent(view.transform.parent, false);
            var manager = FindObjectOfType<XRInteractionManager>();
            if (manager == null) manager = rig.AddComponent<XRInteractionManager>();
            var shader = Shader.Find("UI/Default");
            if (shader != null)
            {
                rayMaterial = new Material(shader) { name = "Companion UI Ray" };
                rayMaterial.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            }
            right = CreateHand("RightHand", manager);
            left = CreateHand("LeftHand", manager);
            panelAction = NewAction("Companion panel", "<XRController>{LeftHand}/secondaryButton", "Button", InputActionType.Button);
            // Keep PTT independent from the UI ray's trigger action. The value is read
            // with hysteresis below so small analog fluctuations cannot split a phrase.
            leftTriggerAction = NewAction("Companion push to talk", "<XRController>{LeftHand}/trigger", "Axis", InputActionType.Value);
            leftTriggerButtonAction = NewAction("Companion push to talk button", "<XRController>{LeftHand}/triggerButton", "Button", InputActionType.Button);
        }

        private InputAction NewAction(string name, string binding, string controlType, InputActionType type = InputActionType.Value)
        {
            var action = new InputAction(name, type, binding, expectedControlType: controlType);
            actions.Add(action);
            return action;
        }

        private Hand CreateHand(string usage, XRInteractionManager manager)
        {
            var go = new GameObject("Companion " + usage + " UI Ray");
            go.transform.SetParent(rig.transform, false);
            string path = "<XRController>{" + usage + "}/";
            var hand = new Hand { transform = go.transform };
            hand.controller = go.AddComponent<ActionBasedController>();
            hand.controller.updateTrackingType = XRBaseController.UpdateType.UpdateAndBeforeRender;
            // PICO's package ships the real controller meshes as Resources. Keep the
            // lookup optional so PC Link still gets a visible proxy when the package
            // does not include those meshes in the player build.
            var controllerPrefab = Resources.Load<GameObject>(
                usage == "LeftHand" ? "Prefabs/LeftControllerModel" : "Prefabs/RightControllerModel");
            if (controllerPrefab == null && usage == "RightHand")
                controllerPrefab = Resources.Load<GameObject>("Prefabs/RightControllerModel ");
            if (controllerPrefab != null)
                hand.controller.modelPrefab = controllerPrefab.transform;
            // PICO's Android profile exposes pointerPosition/pointerRotation, while a
            // SteamVR/OpenXR desktop runtime often exposes only the generic position/
            // rotation controls. Keep both bindings on the same action so the ray works
            // with either runtime.
            var position = NewAction(usage + " aim position", path + "pointerPosition", "Vector3");
            position.AddBinding(path + "position");
            hand.controller.positionAction = new InputActionProperty(position);
            var rotation = NewAction(usage + " aim rotation", path + "pointerRotation", "Quaternion");
            rotation.AddBinding(path + "rotation");
            hand.controller.rotationAction = new InputActionProperty(rotation);
            hand.tracked = NewAction(usage + " tracked", path + "isTracked", "Button", InputActionType.Button);
            // A controller can already be tracked before this panel rig is created or re-enabled.
            hand.tracked.wantsInitialStateCheck = true;
            hand.trackingState = NewAction(usage + " tracking state", path + "trackingState", "Integer");
            hand.controller.isTrackedAction = new InputActionProperty(hand.tracked);
            hand.controller.trackingStateAction = new InputActionProperty(hand.trackingState);
            hand.press = NewAction(usage + " UI trigger", path + "trigger", "Button", InputActionType.Button);
            hand.controller.uiPressAction = new InputActionProperty(hand.press);
            hand.controller.uiScrollAction = new InputActionProperty(NewAction(usage + " UI scroll", path + "primary2DAxis", "Vector2"));
            hand.ray = go.AddComponent<XRRayInteractor>();
            hand.ray.interactionManager = manager;
            hand.ray.interactionLayers = 0; // UI only: never grab scene objects.
            hand.ray.raycastMask = ~0; // Runtime Dropdown graphics can retain the Default layer.
            hand.ray.lineType = XRRayInteractor.LineType.StraightLine;
            hand.ray.maxRaycastDistance = 5f;
            hand.ray.referenceFrame = rig.transform;
            hand.ray.enableUIInteraction = true;
            hand.ray.rayOriginTransform = go.transform;
            hand.ray.enabled = false;
            hand.line = go.AddComponent<LineRenderer>();
            hand.line.useWorldSpace = true;
            hand.line.positionCount = 2;
            hand.line.startWidth = 0.0015f;
            hand.line.endWidth = 0.003f;
            hand.line.numCapVertices = 4;
            hand.line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            hand.line.receiveShadows = false;
            if (rayMaterial != null) hand.line.sharedMaterial = rayMaterial;
            hand.line.enabled = false;
            return hand;
        }

        private static bool IsTracked(Hand hand)
        {
            if (hand == null || !hand.tracked.enabled || !hand.tracked.IsPressed()) return false;
            var required = InputTrackingState.Position | InputTrackingState.Rotation;
            // Some SteamVR/OpenXR bridges expose isTracked but omit trackingState.
            // In that case the pose actions are still valid and the ray should work.
            if (hand.trackingState == null || hand.trackingState.controls.Count == 0) return true;
            int state = hand.trackingState.ReadValue<int>();
            return state == 0 || ((InputTrackingState)state & required) == required;
        }

        private void Update()
        {
            if (!IsVRActive) return;
            if (!inputDiagnosticLogged && inputDiagnosticAt >= 0f && Time.unscaledTime >= inputDiagnosticAt)
            {
                inputDiagnosticLogged = true;
                var leftDevice = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
                string device = leftDevice.isValid
                    ? leftDevice.name + " / " + leftDevice.manufacturer + " / " + leftDevice.characteristics
                    : "invalid";
                string action = panelAction == null
                    ? "missing"
                    : "controls=" + panelAction.controls.Count + ", active=" +
                      (panelAction.activeControl != null ? panelAction.activeControl.path : "none");
                string joysticks = string.Join(",", UnityEngine.Input.GetJoystickNames());
                var xrDevices = new List<UnityEngine.XR.InputDevice>();
                InputDevices.GetDevices(xrDevices);
                var xrDeviceSummary = new List<string>();
                foreach (UnityEngine.XR.InputDevice xrDevice in xrDevices)
                    xrDeviceSummary.Add(xrDevice.name + "/" + xrDevice.characteristics);
                var inputSubsystems = new List<XRInputSubsystem>();
                SubsystemManager.GetInstances(inputSubsystems);
                var inputSubsystemSummary = new List<string>();
                foreach (var inputSubsystem in inputSubsystems)
                    inputSubsystemSummary.Add(inputSubsystem.SubsystemDescriptor != null
                        ? inputSubsystem.SubsystemDescriptor.id + ":running=" + inputSubsystem.running
                        : "unknown:running=" + inputSubsystem.running);
                var systemDevices = new List<string>();
                foreach (var systemDevice in UnityEngine.InputSystem.InputSystem.devices)
                    systemDevices.Add(systemDevice.layout + "/" + systemDevice.name + "/" +
                        string.Join("|", systemDevice.usages));
                Debug.Log("[CompanionXRInteraction] VR input: leftDevice=" + device +
                    ", panelAction=" + action + ", legacyAction=" +
                    ReadLegacyPanelButton() + ", joysticks=" + joysticks +
                    ", xrDevices=" + string.Join(",", xrDeviceSummary) +
                    ", inputSubsystems=" + string.Join(",", inputSubsystemSummary) +
                    ", inputSystemDevices=" + string.Join(",", systemDevices), this);
            }
            // XR input is delivered by the runtime even when the Unity desktop window is
            // not the foreground window. Pico Link normally leaves SteamVR focused, so
            // gating controller input on Application.isFocused makes Y and the UI ray
            // silently stop working in the headset.
            bool rightAvailable = IsTracked(right);
            // Exactly one ray owns hover/drag at a time. The left hand takes over on right tracking loss.
            SetHandEnabled(right, panelVisible && rightAvailable);
            SetHandEnabled(left, panelVisible && !rightAvailable && IsTracked(left));
            // Some Pico Link/OpenXR combinations expose controller buttons through the
            // legacy XRDevice feature API but do not create a matching Input System
            // control for the generic XRController binding. Keep both paths in sync.
            bool devicePressed = ReadLeftSecondaryButton();
            // Pico Link/SteamVR may expose the controller as a generic XR device with
            // no secondaryButton control. The project already maps this physical Y
            // button to the legacy XRI action (joystick button 3), so poll that path as
            // a desktop-runtime fallback.
            bool legacyInputPressed = ReadLegacyPanelButton();
            bool buttonPressed = devicePressed || legacyInputPressed;
            bool actionPressed = panelAction != null && panelAction.IsPressed();
            bool pressedThisFrame = (panelAction != null && panelAction.WasPressedThisFrame()) ||
                (buttonPressed && !legacyPanelPressed);
            bool releasedThisFrame = (panelAction != null && panelAction.WasReleasedThisFrame()) ||
                (!buttonPressed && legacyPanelPressed);
            legacyPanelPressed = buttonPressed;
            if (pressedThisFrame)
            {
                Debug.Log("[CompanionXRInteraction] Left Y press detected (panel action/device/legacy).", this);
                panelPressedAt = Time.unscaledTime;
                recenterSent = false;
            }
            if (panelPressedAt >= 0f && (actionPressed || buttonPressed) && !recenterSent &&
                Time.unscaledTime - panelPressedAt >= RecenterHoldSeconds)
            {
                recenterSent = true;
                RecenterRequested?.Invoke();
            }
            if (panelPressedAt >= 0f && releasedThisFrame)
            {
                panelPressedAt = -1f;
                if (!recenterSent)
                {
                    Debug.Log("[CompanionXRInteraction] Left Y short release -> toggle panel.", this);
                    TogglePanelRequested?.Invoke();
                }
            }

        }

        private void OnUiPointerDown(GameObject target, PointerEventData data)
        {
            if (left != null && data is TrackedDeviceEventData tracked && ReferenceEquals(tracked.interactor, left.ray) &&
                CanConsumeTriggerPress(target))
                leftTriggerUiClaimed = true;
        }

        private static bool CanConsumeTriggerPress(GameObject target)
        {
            return target != null &&
                (ExecuteEvents.GetEventHandler<IPointerDownHandler>(target) != null ||
                 ExecuteEvents.GetEventHandler<IPointerClickHandler>(target) != null ||
                 ExecuteEvents.GetEventHandler<IDragHandler>(target) != null);
        }

        private void UpdatePushToTalk(float triggerValue, bool uiOwnsTrigger)
        {
            // A UI may disappear before its PointerUp, or XRI may force PointerUp
            // while the trigger remains held. Only physical release clears this claim.
            if (triggerValue < TriggerReleaseThreshold) leftTriggerUiClaimed = false;
            bool triggerPressed = leftTriggerPressed
                ? triggerValue >= TriggerReleaseThreshold
                : triggerValue >= TriggerPressThreshold;
            if (triggerPressed != leftTriggerPressed)
            {
                leftTriggerPressed = triggerPressed;
                if (triggerPressed)
                {
                    // The fallback left ray uses this same physical trigger. Decide
                    // ownership on press and keep it until release, even if hover changes.
                    leftTriggerOwnsPushToTalk = !uiOwnsTrigger;
                    if (!leftTriggerOwnsPushToTalk)
                    {
                        Debug.Log("[CompanionXRInteraction] Left trigger press belongs to the UI ray; shortcut PTT skipped.", this);
                        return;
                    }
                    Debug.Log("[CompanionXRInteraction] Left trigger press -> push-to-talk start.", this);
                    PushToTalkStarted?.Invoke();
                }
                else
                {
                    bool endPushToTalk = leftTriggerOwnsPushToTalk;
                    leftTriggerOwnsPushToTalk = false;
                    if (endPushToTalk)
                    {
                        Debug.Log("[CompanionXRInteraction] Left trigger release -> push-to-talk end.", this);
                        PushToTalkEnded?.Invoke();
                    }
                }
            }
        }

        private static bool ReadLeftSecondaryButton()
        {
            var device = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            return device.isValid && device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out bool pressed) && pressed;
        }

        private static bool ReadLegacyPanelButton()
        {
            try
            {
                return UnityEngine.Input.GetButton("XRI_Left_SecondaryButton");
            }
            catch (ArgumentException)
            {
                // Keep the XR path usable if a stripped/player build removed the old
                // Input Manager entry.
                return false;
            }
        }

        private float ReadLeftTriggerValue()
        {
            // A bound modern input reporting zero is an authoritative release. Do not
            // let a stale digital/legacy path keep the aggregate pressed forever.
            if (HasUsableControl(leftTriggerAction))
                return SelectedTriggerValue("InputSystem axis", leftTriggerAction.ReadValue<float>());
            if (HasUsableControl(leftTriggerButtonAction))
                return SelectedTriggerValue("InputSystem button", leftTriggerButtonAction.IsPressed() ? 1f : 0f);

            var device = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (device.isValid && device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out float deviceValue))
                return SelectedTriggerValue("XR feature axis", deviceValue);
            if (device.isValid && device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out bool triggerButton))
                return SelectedTriggerValue("XR feature button", triggerButton ? 1f : 0f);

            // Old bridges may expose only joystick button 14 while the configured
            // analog entry always reads zero. Combine them only at this last fallback.
            float value = 0f;
            try
            {
                value = SafeAxisValue(UnityEngine.Input.GetAxisRaw("XRI_Left_Trigger"));
            }
            catch (ArgumentException)
            {
                // Player builds may omit the legacy Input Manager entry.
            }
            try
            {
                if (UnityEngine.Input.GetButton("XRI_Left_TriggerButton")) value = 1f;
            }
            catch (ArgumentException)
            {
                // Keep Input System and XR feature paths usable without legacy mappings.
            }
            return SelectedTriggerValue("legacy joystick fallback", value);
        }

        private static bool HasUsableControl(InputAction action)
        {
            if (action == null || !action.enabled) return false;
            foreach (var control in action.controls)
                if (control.device.added && control.device.enabled) return true;
            return false;
        }

        private float SelectedTriggerValue(string source, float value)
        {
            if (leftTriggerSource != source)
            {
                leftTriggerSource = source;
                Debug.Log("[CompanionXRInteraction] Left trigger input source: " + source + ".", this);
            }
            return SafeAxisValue(value);
        }

        private static float SafeAxisValue(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp01(value);
        }

        private static void SetHandEnabled(Hand hand, bool active)
        {
            if (hand == null) return;
            if (hand.ray.enabled != active) hand.ray.enabled = active;
            if (!active) hand.line.enabled = false;
        }

        private void EnsureControllerModels()
        {
            EnsureControllerModel(right, false);
            EnsureControllerModel(left, true);
        }

        private void EnsureControllerModel(Hand hand, bool leftHand)
        {
            if (hand == null || hand.controller == null || !hand.controller.isActiveAndEnabled) return;
            if (hand.controller.model != null)
            {
                // The PICO resource prefab can be present while its loader has no
                // device-specific mesh (common on PC Link). Keep the prefab dormant
                // and provide a visible proxy in that case.
                if (hand.controller.model.GetComponentsInChildren<Renderer>(true).Length > 0) return;
                hand.controller.model.gameObject.SetActive(false);
            }

            if (fallbackControllerMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("UI/Default");
                if (shader != null)
                {
                    fallbackControllerMaterial = new Material(shader) { name = "Companion Controller Proxy" };
                    fallbackControllerMaterial.color = new Color(.22f, .78f, .68f, 1f);
                }
            }

            var root = new GameObject(leftHand ? "Left PICO Controller (proxy)" : "Right PICO Controller (proxy)");
            root.transform.SetParent(hand.transform, false);
            root.transform.localPosition = new Vector3(0f, -0.025f, 0.035f);
            root.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Controller body";
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(.045f, .085f, .045f);
            body.transform.localPosition = Vector3.zero;
            var collider = body.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            if (fallbackControllerMaterial != null) body.GetComponent<Renderer>().sharedMaterial = fallbackControllerMaterial;
            hand.controller.model = root.transform;
        }

        private void LateUpdate()
        {
            if (!IsVRActive) return;
            // XRI processes this frame's raycasts and pointer events after our early
            // Update. Decide shortcut ownership now, not from last frame's hover hit.
            UpdatePushToTalk(ReadLeftTriggerValue(), leftTriggerUiClaimed);
            // XRBaseController instantiates its configured prefab during Update.
            // Only create the lightweight proxy after that pass has had a chance
            // to load the PICO mesh.
            EnsureControllerModels();
            DrawRay(right);
            DrawRay(left);
            if (panelVisible && Time.unscaledTime >= nextCanvasScan)
            {
                // Unity Dropdown creates nested canvases only when first opened.
                RefreshCanvasRaycasters();
                nextCanvasScan = Time.unscaledTime + 0.15f;
            }
        }

        private static void DrawRay(Hand hand)
        {
            if (hand == null || !hand.ray.enabled || !IsTracked(hand))
            {
                if (hand != null) hand.line.enabled = false;
                return;
            }
            bool hit = hand.ray.TryGetCurrentUIRaycastResult(out var result);
            hand.line.enabled = true;
            hand.line.SetPosition(0, hand.transform.position);
            hand.line.SetPosition(1, hit ? result.worldPosition : hand.transform.position + hand.transform.forward * 1.8f);
            var color = hit ? new Color(0.48f, 0.88f, 0.78f, 0.9f) : new Color(0.85f, 0.87f, 0.84f, 0.22f);
            hand.line.startColor = new Color(color.r, color.g, color.b, color.a * 0.25f);
            hand.line.endColor = color;
        }

        private void RefreshCanvasRaycasters()
        {
            foreach (var root in worldRoots)
            {
                if (root == null) continue;
                nestedCanvases.Clear();
                root.GetComponentsInChildren(true, nestedCanvases);
                foreach (var canvas in nestedCanvases)
                {
                    canvas.worldCamera = view;
                    if (!canvas.TryGetComponent<TrackedDeviceGraphicRaycaster>(out var tracked))
                    {
                        tracked = canvas.gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
                        tracked.checkFor2DOcclusion = false;
                        tracked.checkFor3DOcclusion = false;
                        ownedRaycasters.Add(tracked);
                    }
                    if (!IsVRActive) continue;
                    foreach (var standard in canvas.GetComponents<GraphicRaycaster>())
                    {
                        if (!graphicStates.ContainsKey(standard)) graphicStates.Add(standard, standard.enabled);
                        standard.enabled = false;
                    }
                }
            }
        }

        private void OnDisable()
        {
            if (IsVRActive) SetVRActive(false);
        }

        private void OnDestroy()
        {
            if (IsVRActive) SetVRActive(false);
            if (rig != null) { rig.SetActive(false); Destroy(rig); }
            foreach (var action in actions) action.Dispose();
            foreach (var raycaster in ownedRaycasters)
                if (raycaster != null) Destroy(raycaster);
            if (rayMaterial != null) Destroy(rayMaterial);
            if (fallbackControllerMaterial != null) Destroy(fallbackControllerMaterial);
            if (ownsEventSystem && eventSystem != null) Destroy(eventSystem.gameObject);
            else if (ownsXRModule && xrModule != null) Destroy(xrModule);
        }
    }
}
