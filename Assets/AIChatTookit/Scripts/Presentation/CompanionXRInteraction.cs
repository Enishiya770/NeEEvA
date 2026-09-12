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
    /// panel recentering. Neither action touches the headset pose or the platform menu.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(-29995)]
    public sealed class CompanionXRInteraction : MonoBehaviour
    {
        public event Action TogglePanelRequested;
        public event Action RecenterRequested;
        public bool IsVRActive { get; private set; }

        private const float RecenterHoldSeconds = 0.7f;
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
        private Material rayMaterial;
        private float panelPressedAt = -1f, nextCanvasScan;
        private bool recenterSent;

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
            if (active)
            {
                EnsureEventSystem();
                ActivateXRModule();
                BuildRig();
                if (rig != null) rig.SetActive(true);
                panelAction?.Enable();
                RefreshCanvasRaycasters();
            }
            else
            {
                if (rig != null) rig.SetActive(false);
                panelAction?.Disable();
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
            hand.controller.positionAction = new InputActionProperty(NewAction(usage + " aim position", path + "pointerPosition", "Vector3"));
            hand.controller.rotationAction = new InputActionProperty(NewAction(usage + " aim rotation", path + "pointerRotation", "Quaternion"));
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
            return ((InputTrackingState)hand.trackingState.ReadValue<int>() & required) == required;
        }

        private void Update()
        {
            if (!IsVRActive) return;
            bool rightAvailable = Application.isFocused && IsTracked(right);
            // Exactly one ray owns hover/drag at a time. The left hand takes over on right tracking loss.
            SetHandEnabled(right, panelVisible && rightAvailable);
            SetHandEnabled(left, panelVisible && !rightAvailable && Application.isFocused && IsTracked(left));
            if (!Application.isFocused)
            {
                panelPressedAt = -1f;
                return;
            }
            if (panelAction != null && panelAction.WasPressedThisFrame())
            {
                panelPressedAt = Time.unscaledTime;
                recenterSent = false;
            }
            if (panelPressedAt >= 0f && panelAction.IsPressed() && !recenterSent &&
                Time.unscaledTime - panelPressedAt >= RecenterHoldSeconds)
            {
                recenterSent = true;
                RecenterRequested?.Invoke();
            }
            if (panelPressedAt >= 0f && panelAction.WasReleasedThisFrame())
            {
                panelPressedAt = -1f;
                if (!recenterSent) TogglePanelRequested?.Invoke();
            }
        }

        private static void SetHandEnabled(Hand hand, bool active)
        {
            if (hand == null) return;
            if (hand.ray.enabled != active) hand.ray.enabled = active;
            if (!active) hand.line.enabled = false;
        }

        private void LateUpdate()
        {
            if (!IsVRActive) return;
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
            if (ownsEventSystem && eventSystem != null) Destroy(eventSystem.gameObject);
            else if (ownsXRModule && xrModule != null) Destroy(xrModule);
        }
    }
}
