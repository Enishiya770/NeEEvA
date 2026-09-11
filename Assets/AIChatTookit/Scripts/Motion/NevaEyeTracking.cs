using System;
using UniVRM10;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>VRoid-style eye bones following the player's desktop or tracked head camera.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(13000)]
    public sealed class NevaEyeTracking : MonoBehaviour
    {
        [SerializeField] private Vrm10Instance avatar;
        [Tooltip("Optional gaze target. When empty, use the active Main Camera (also the VR head camera).")]
        public Transform gazeTarget;
        [Min(0.01f)] public float smoothTime = 0.12f;
        [Min(0.01f)] public float minimumTargetDistance = 0.15f;
        [Header("VRoid ranges: input angle / eye angle")]
        [SerializeField] private CurveMapper horizontalInner = new CurveMapper(90f, 8f);
        [SerializeField] private CurveMapper horizontalOuter = new CurveMapper(90f, 12f);
        [SerializeField] private CurveMapper verticalUp = new CurveMapper(90f, 10f);
        [SerializeField] private CurveMapper verticalDown = new CurveMapper(90f, 10f);
        [SerializeField, HideInInspector] private Transform head, leftEye, rightEye;
        [SerializeField, HideInInspector] private Quaternion headFrameLocalRotation;
        [SerializeField, HideInInspector] private Quaternion leftEyeInFrame, rightEyeInFrame;
        [SerializeField, HideInInspector] private Quaternion leftRestLocalRotation, rightRestLocalRotation;
        [SerializeField, HideInInspector] private Vector3 originInHead;
        [SerializeField, HideInInspector] private bool calibrated;

        public bool IsInitialized => calibrated && avatar != null && head != null && leftEye != null && rightEye != null;
        public Vrm10Instance Target => avatar;
        public Transform Head => head;
        public Transform LeftEye => leftEye;
        public Transform RightEye => rightEye;
        public Vector2 CurrentInputAngles { get; private set; }
        public Vector3 EyeOrigin => head != null ? head.TransformPoint(originInHead) : transform.position;
        public Quaternion HeadFrameRotation => head != null ? head.rotation * headFrameLocalRotation : transform.rotation;
        public float InnerLimit => horizontalInner.CurveYRangeDegree;
        public float OuterLimit => horizontalOuter.CurveYRangeDegree;
        public float UpLimit => verticalUp.CurveYRangeDegree;
        public float DownLimit => verticalDown.CurveYRangeDegree;

        private Camera fallbackCamera;
        private bool ownsEyes, warnedAboutOwner;

        /// <summary>Calibrate in the avatar's neutral edit-mode pose. Repeated calls preserve the reference.</summary>
        public void Configure(Vrm10Instance target, Transform targetTransform = null)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            var humanoid = target.Humanoid;
            var newHead = humanoid.GetBoneTransform(HumanBodyBones.Head);
            var newLeft = humanoid.GetBoneTransform(HumanBodyBones.LeftEye);
            var newRight = humanoid.GetBoneTransform(HumanBodyBones.RightEye);
            if (newHead == null || newLeft == null || newRight == null)
                throw new InvalidOperationException("Eye tracking requires a head and both humanoid eye bones.");
            bool sameRig = IsInitialized && avatar == target && head == newHead && leftEye == newLeft && rightEye == newRight;
            RestoreEyes();
            avatar = target;
            head = newHead;
            leftEye = newLeft;
            rightEye = newRight;
            gazeTarget = targetTransform;
            if (!sameRig)
            {
                Quaternion referenceFrame = avatar.transform.rotation;
                headFrameLocalRotation = Quaternion.Inverse(head.rotation) * referenceFrame;
                leftEyeInFrame = Quaternion.Inverse(referenceFrame) * leftEye.rotation;
                rightEyeInFrame = Quaternion.Inverse(referenceFrame) * rightEye.rotation;
                leftRestLocalRotation = leftEye.localRotation;
                rightRestLocalRotation = rightEye.localRotation;
                var lookAt = avatar.Vrm != null ? avatar.Vrm.LookAt : null;
                originInHead = lookAt != null ? lookAt.OffsetFromHead :
                    head.InverseTransformPoint((leftEye.position + rightEye.position) * 0.5f);
                if (lookAt != null)
                {
                    horizontalInner = Copy(lookAt.HorizontalInner);
                    horizontalOuter = Copy(lookAt.HorizontalOuter);
                    verticalUp = Copy(lookAt.VerticalUp);
                    verticalDown = Copy(lookAt.VerticalDown);
                }
                calibrated = true;
            }
            CurrentInputAngles = Vector2.zero;
            fallbackCamera = null;
        }

        private static CurveMapper Copy(CurveMapper map) => new CurveMapper(map.CurveXRangeDegree, map.CurveYRangeDegree);

        private void OnEnable()
        {
            if (!Application.isPlaying) return;
            if (!IsInitialized)
            {
                var target = avatar != null ? avatar : GetComponent<Vrm10Instance>();
                if (target != null) Configure(target, gazeTarget);
            }
            CurrentInputAngles = Vector2.zero;
        }

        private Transform ResolveTarget()
        {
            if (gazeTarget != null) return gazeTarget.gameObject.activeInHierarchy ? gazeTarget : null;
            if (fallbackCamera == null || !fallbackCamera.isActiveAndEnabled || !fallbackCamera.CompareTag("MainCamera"))
                fallbackCamera = Camera.main;
            return fallbackCamera != null && fallbackCamera.isActiveAndEnabled ? fallbackCamera.transform : null;
        }

        private void LateUpdate()
        {
            if (!IsInitialized) return;
            // Never activate Vrm10Instance.Runtime: it would also own expressions and springs.
            if (avatar.isActiveAndEnabled)
            {
                if (!warnedAboutOwner)
                    Debug.LogWarning("[NEVA Eyes] Full VRM updates are enabled; independent eye tracking is paused to avoid two eye writers.", this);
                warnedAboutOwner = true;
                ownsEyes = false;
                return;
            }
            warnedAboutOwner = false;
            Vector2 wanted = Vector2.zero;
            var target = ResolveTarget();
            if (target != null)
            {
                Vector3 offset = target.position - EyeOrigin;
                float scale = Mathf.Max(0.01f, avatar.transform.lossyScale.magnitude / Mathf.Sqrt(3f));
                if (offset.sqrMagnitude >= minimumTargetDistance * minimumTargetDistance * scale * scale)
                {
                    Vector3 local = Quaternion.Inverse(HeadFrameRotation) * offset;
                    float coneAngle = Mathf.Acos(Mathf.Clamp(local.normalized.z, -1f, 1f)) * Mathf.Rad2Deg;
                    // Release gaze smoothly when the player moves behind the head.
                    float visibility = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(80f, 110f, coneAngle));
                    wanted = new Vector2(Mathf.Atan2(local.x, local.z),
                        Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude)) * Mathf.Rad2Deg * visibility;
                }
            }
            float blend = 1f - Mathf.Exp(-Time.unscaledDeltaTime / Mathf.Max(0.01f, smoothTime));
            CurrentInputAngles = Vector2.Lerp(CurrentInputAngles, wanted, blend);
            ApplyEyes(CurrentInputAngles);
            ownsEyes = true;
        }

        private void ApplyEyes(Vector2 input)
        {
            float leftYaw = input.x < 0f ? -horizontalOuter.Map(-input.x) : horizontalInner.Map(input.x);
            float rightYaw = input.x < 0f ? -horizontalInner.Map(-input.x) : horizontalOuter.Map(input.x);
            float pitch = input.y < 0f ? verticalDown.Map(-input.y) : -verticalUp.Map(input.y);
            Quaternion frame = HeadFrameRotation;
            // Use this frame's animated head orientation; never turn the neck or body.
            leftEye.rotation = frame * Quaternion.Euler(pitch, leftYaw, 0f) * leftEyeInFrame;
            rightEye.rotation = frame * Quaternion.Euler(pitch, rightYaw, 0f) * rightEyeInFrame;
        }

        private void RestoreEyes()
        {
            if (!ownsEyes || !IsInitialized || avatar.isActiveAndEnabled) return;
            leftEye.localRotation = leftRestLocalRotation;
            rightEye.localRotation = rightRestLocalRotation;
            ownsEyes = false;
        }

        private void OnDisable() { RestoreEyes(); CurrentInputAngles = Vector2.zero; }
        private void OnDestroy() { RestoreEyes(); }
    }
}
