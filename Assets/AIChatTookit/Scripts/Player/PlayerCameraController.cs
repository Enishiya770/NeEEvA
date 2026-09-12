using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR;

namespace NeEEvA.Player
{
    /// <summary>Scene-style desktop flight and an OpenXR head camera sharing one view.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Camera))]
    public sealed class PlayerCameraController : MonoBehaviour
    {
        [Header("Desktop (hold right mouse button)")]
        [Min(0.01f)] public float moveSpeed = 2f;
        [Min(1f)] public float fastMultiplier = 3f;
        [Min(0.001f)] public float mouseSensitivity = 0.12f;
        [Range(1f, 89f)] public float pitchLimit = 89f;
        [Header("VR")]
        [Tooltip("Automatically hand the view to the headset while an XR display is running.")]
        public bool enableVR = true;

        public bool IsVRActive { get; private set; }
        public bool IsLooking { get; private set; }
        /// <summary>Whether the current world pose is usable as the explicit conversation partner anchor.</summary>
        public bool HasValidWorldPose
        {
            get
            {
                if (!isActiveAndEnabled || !gameObject.activeInHierarchy) return false;
                if (!IsVRActive) return true;
                if (awaitingHeadPose || headTracking == null || !headTracking.enabled) return false;
                var required = InputTrackingState.Position | InputTrackingState.Rotation;
                return ((InputTrackingState)headTracking.ReadValue<int>() & required) == required;
            }
        }
        /// <summary>UI panels temporarily reserve desktop pointer/keyboard input; head tracking is unaffected.</summary>
        public bool InteractionBlocked { get; set; }

        private readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        private readonly List<XRInputSubsystem> inputs = new List<XRInputSubsystem>();
        private Camera view;
        private Transform origin;
        private Transform originalParent;
        private TrackedPoseDriver headDriver;
        private InputAction headPosition, headRotation, headTracking;
        private Pose initialPose, desktopPose;
        private float yaw, pitch;
        private bool awaitingHeadPose;
        private CursorLockMode previousLock;
        private bool previousCursorVisible;

        private void Awake()
        {
            view = GetComponent<Camera>();
            view.orthographic = false;
            view.nearClipPlane = Mathf.Min(view.nearClipPlane, 0.03f);
            view.stereoTargetEye = StereoTargetEyeMask.Both;
            initialPose = new Pose(transform.position, transform.rotation);
            originalParent = transform.parent;

            // A unit-scale root keeps physical headset metres independent of room/avatar scale.
            origin = new GameObject("Player Camera Origin").transform;
            SceneManager.MoveGameObjectToScene(origin.gameObject, gameObject.scene);
            transform.SetParent(origin, true);
            var lifetime = origin.gameObject.AddComponent<PlayerCameraOriginLifetime>();
            lifetime.owner = this;
            lifetime.view = transform;
            lifetime.originalParent = originalParent;

            headPosition = new InputAction("Head position", InputActionType.Value,
                "<XRHMD>/centerEyePosition", expectedControlType: "Vector3");
            headRotation = new InputAction("Head rotation", InputActionType.Value,
                "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion");
            headTracking = new InputAction("Head tracking", InputActionType.Value,
                "<XRHMD>/trackingState", expectedControlType: "Integer");
            headDriver = gameObject.AddComponent<TrackedPoseDriver>();
            headDriver.enabled = false;
            headDriver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            headDriver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
            headDriver.positionInput = new InputActionProperty(headPosition);
            headDriver.rotationInput = new InputActionProperty(headRotation);
            headDriver.trackingStateInput = new InputActionProperty(headTracking);
            ReadDesktopAngles();
        }

        private void Update()
        {
            bool xrRunning = enableVR && HasRunningDisplay();
            if (xrRunning != IsVRActive) SetVRActive(xrRunning);
            if (IsVRActive)
            {
                if (awaitingHeadPose) AlignHeadToDesktopPose();
                // Tracking loss must never switch the user back to mouse-driven VR rotation.
                return;
            }
            UpdateDesktop();
        }

        private bool HasRunningDisplay()
        {
            displays.Clear();
            SubsystemManager.GetInstances(displays);
            foreach (var display in displays)
                if (display.running) return true;
            return false;
        }

        private void SetVRActive(bool active)
        {
            ReleaseCursor();
            IsVRActive = active;
            if (active)
            {
                desktopPose = new Pose(transform.position, transform.rotation);
                inputs.Clear();
                SubsystemManager.GetInstances(inputs);
                foreach (var input in inputs)
                    if (input.running) input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device);
                headPosition.Enable();
                headRotation.Enable();
                headTracking.Enable();
                awaitingHeadPose = true;
            }
            else
            {
                headDriver.enabled = false;
                headPosition.Disable();
                headRotation.Disable();
                headTracking.Disable();
                awaitingHeadPose = false;
                transform.SetPositionAndRotation(desktopPose.position, desktopPose.rotation);
                ReadDesktopAngles();
            }
        }

        private void AlignHeadToDesktopPose()
        {
            var tracking = (InputTrackingState)headTracking.ReadValue<int>();
            var required = InputTrackingState.Position | InputTrackingState.Rotation;
            if ((tracking & required) != required) return;

            Vector3 position = headPosition.ReadValue<Vector3>();
            Quaternion rotation = headRotation.ReadValue<Quaternion>();
            // Remove only the initial heading. Physical head pitch/roll always remain physical.
            Quaternion heading = Quaternion.Euler(0f,
                desktopPose.rotation.eulerAngles.y - rotation.eulerAngles.y, 0f);
            origin.SetPositionAndRotation(desktopPose.position - heading * position, heading);
            transform.SetLocalPositionAndRotation(position, rotation);
            awaitingHeadPose = false;
            headDriver.enabled = true;
        }

        private void UpdateDesktop()
        {
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            if (InteractionBlocked || !Application.isFocused || mouse == null || keyboard == null || IsEditingText())
            {
                ReleaseCursor();
                return;
            }
            if (keyboard.escapeKey.wasPressedThisFrame || !mouse.rightButton.isPressed)
            {
                ReleaseCursor();
                return;
            }
            if (mouse.rightButton.wasPressedThisFrame && !IsLooking)
            {
                Vector2 point = mouse.position.ReadValue();
                if (!view.pixelRect.Contains(point)) return;
                previousLock = Cursor.lockState;
                previousCursorVisible = Cursor.visible;
                IsLooking = true;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                ReadDesktopAngles();
                return; // Ignore the pointer warp on the frame that captures the mouse.
            }
            if (!IsLooking) return;
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                ReleaseCursor();
                return;
            }

            Vector3 direction = new Vector3(
                (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f),
                (keyboard.eKey.isPressed ? 1f : 0f) - (keyboard.qKey.isPressed ? 1f : 0f),
                (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f));
            ApplyDesktopMotion(mouse.delta.ReadValue(), direction, keyboard.shiftKey.isPressed, Time.unscaledDeltaTime);
        }

        private void ApplyDesktopMotion(Vector2 mouseDelta, Vector3 direction, bool fast, float deltaTime)
        {
            yaw += mouseDelta.x * mouseSensitivity;
            pitch = Mathf.Clamp(pitch - mouseDelta.y * mouseSensitivity, -pitchLimit, pitchLimit);
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            // Q/E move along world up; W/S fly towards/away from the current view direction.
            Vector3 world = transform.right * direction.x + Vector3.up * direction.y +
                transform.forward * direction.z;
            float speed = moveSpeed * (fast ? fastMultiplier : 1f);
            transform.position += Vector3.ClampMagnitude(world, 1f) * speed * deltaTime;
        }

        private static bool IsEditingText()
        {
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (selected == null) return false;
            var legacy = selected.GetComponent<InputField>();
            var tmp = selected.GetComponent<TMP_InputField>();
            return (legacy != null && legacy.isFocused) || (tmp != null && tmp.isFocused);
        }

        [ContextMenu("Reset View / Recenter Headset")]
        public void ResetView()
        {
            if (!Application.isPlaying) return;
            ReleaseCursor();
            if (IsVRActive)
            {
                headDriver.enabled = false;
                headPosition.Enable();
                headRotation.Enable();
                headTracking.Enable();
                desktopPose = initialPose;
                awaitingHeadPose = true;
            }
            else
            {
                transform.SetPositionAndRotation(initialPose.position, initialPose.rotation);
                ReadDesktopAngles();
            }
        }

        private void ReadDesktopAngles()
        {
            yaw = transform.eulerAngles.y;
            pitch = Mathf.DeltaAngle(0f, transform.eulerAngles.x);
        }

        private void ReleaseCursor()
        {
            if (!IsLooking) return;
            IsLooking = false;
            Cursor.lockState = previousLock;
            Cursor.visible = previousCursorVisible;
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused) ReleaseCursor();
        }

        private void OnDisable()
        {
            ReleaseCursor();
            if (IsVRActive) SetVRActive(false);
        }

        private void OnDestroy()
        {
            if (headDriver != null) Destroy(headDriver);
            headPosition?.Dispose();
            headRotation?.Dispose();
            headTracking?.Dispose();
        }
    }

    // Detach only after destruction has completed; Unity forbids reparenting inside Camera.OnDestroy.
    internal sealed class PlayerCameraOriginLifetime : MonoBehaviour
    {
        internal PlayerCameraController owner;
        internal Transform view, originalParent;

        private void LateUpdate()
        {
            if (owner != null) return;
            if (view != null) view.SetParent(originalParent, true);
            Destroy(gameObject);
        }
    }
}
