using System;
using System.Collections.Generic;
using NeEEvA.Motion;
using UniVRM10;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace NeEEvA.Perception
{
    /// <summary>Scene-owned, demand-driven RGB camera. Never moves a body or the user's camera.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(16000)]
    public sealed class NevaSceneVisionCapture : MonoBehaviour
    {
        [SerializeField] private Vrm10Instance avatar;
        [SerializeField] private NevaEyeTracking eyeTracking;
        [Range(320, 1280)] public int captureWidth = 960;
        [Range(180, 960)] public int captureHeight = 540;
        [Range(40, 100)] public float verticalFieldOfView = 65;
        [Range(1, 4)] public float maximumCapturesPerSecond = 2;
        [Range(50, 95)] public int jpegQuality = 80;
        [Tooltip("Scene geometry; ordinary UI is excluded. Does not change any existing camera mask.")]
        public LayerMask sceneLayers = ~(1 << 5);
        [SerializeField, HideInInspector] private bool neutralViewCalibrated;
        [SerializeField, HideInInspector] private Vector3 neutralOriginInHead;
        [SerializeField, HideInInspector] private Quaternion neutralFrameInHead = Quaternion.identity;
        [SerializeField, HideInInspector] private Transform calibratedHead;

        public Vrm10Instance Target => avatar;
        public SceneVisionObservation LatestObservation { get; private set; } =
            SceneVisionObservation.WithoutImage("closed", "场景视觉尚未开启。");
        public Texture PreviewTexture => preview;
        public bool IsObserving { get; private set; }
        /// <summary>When enabled, the capture publishes a rolling latest frame even without a one-shot callback.</summary>
        public bool IsContinuousObservation { get; private set; }
        public bool IsCapturePending => waiting.Count > 0 || inFlight != null && inFlight.generation == generation;
        public float LatestObservationAgeSeconds => LatestObservation != null && LatestObservation.HasImage
            ? Mathf.Max(0f, Time.realtimeSinceStartup - LatestObservation.CapturedAtRealtime)
            : float.PositiveInfinity;
        public string Status { get; private set; } = "场景视觉尚未开启。";
        public float PreparationMilliseconds { get; private set; }

        private const int CallbackLimit = 8;
        private readonly List<Action<SceneVisionObservation>> waiting = new List<Action<SceneVisionObservation>>();
        private Camera captureCamera;
        private Texture2D preview;
        private SceneVisionSelfView selfView;
        private Readback inFlight;
        private int generation;
        private string binding = Guid.NewGuid().ToString("N");
        private float nextCaptureAt;
        private bool rendering;
        private sealed class Readback
        {
            internal int generation, frame, width, height;
            internal string id, utc, sceneName, scenePath, sceneFingerprint;
            internal int sceneHandle;
            internal float time, fov;
            internal Pose pose;
            internal RenderTexture target;
            internal List<Action<SceneVisionObservation>> callbacks;
            internal SceneVisionGrounding.Pending groundingPending;
            internal SceneVisionGrounding.Frame grounding;
            internal byte[] pixels;
            internal string rgbError;
            internal bool rgbReady, groundingReady, groundingSubmitted, finished;
        }

        public void Configure(Vrm10Instance target, NevaEyeTracking tracking = null)
        {
            if (rendering) throw new InvalidOperationException("不能在角色相机渲染回调中重新绑定视觉。");
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (tracking != null && tracking.Target != target)
                throw new ArgumentException("The eye reference must belong to the same character.");
            CancelPending("角色视觉绑定已更新。");
            DisposeCamera();
            if (avatar != target) { neutralViewCalibrated = false; calibratedHead = null; }
            avatar = target;
            eyeTracking = tracking != null ? tracking : target.GetComponent<NevaEyeTracking>();
            if (!Application.isPlaying) CalibrateNeutralView();
            Status = "已绑定角色；采集使用校准头部视角，不使用用户相机。";
        }

        /// <summary>Call only on an explicitly neutral edit-mode character; never calibrate from a playing gesture.</summary>
        public void CalibrateNeutralView()
        {
            if (Application.isPlaying) throw new InvalidOperationException("请在非 Play 的中立姿态校准视点。");
            if (avatar == null) throw new InvalidOperationException("尚未绑定角色。");
            Transform head = avatar.Humanoid.GetBoneTransform(HumanBodyBones.Head);
            Transform left = avatar.Humanoid.GetBoneTransform(HumanBodyBones.LeftEye);
            Transform right = avatar.Humanoid.GetBoneTransform(HumanBodyBones.RightEye);
            if (head == null || left == null || right == null) throw new InvalidOperationException("需要头部和双眼骨骼。");
            neutralOriginInHead = head.InverseTransformPoint((left.position + right.position) * .5f);
            neutralFrameInHead = Quaternion.Inverse(head.rotation) * avatar.transform.rotation;
            calibratedHead = head; neutralViewCalibrated = true;
        }

        public bool TryGetCurrentCameraPose(out Pose pose)
        {
            pose = new Pose(Vector3.zero, Quaternion.identity);
            if (avatar == null || !avatar.gameObject.activeInHierarchy) return false;
            if (eyeTracking != null && eyeTracking.Target == avatar && eyeTracking.IsInitialized)
                pose = new Pose(eyeTracking.EyeOrigin, eyeTracking.HeadFrameRotation);
            else if (neutralViewCalibrated && calibratedHead != null &&
                calibratedHead == avatar.Humanoid.GetBoneTransform(HumanBodyBones.Head))
                pose = new Pose(calibratedHead.TransformPoint(neutralOriginInHead), calibratedHead.rotation * neutralFrameInHead);
            else return false;
            return Finite(pose.position.x) && Finite(pose.position.y) && Finite(pose.position.z) &&
                Finite(pose.rotation.x) && Finite(pose.rotation.y) && Finite(pose.rotation.z) && Finite(pose.rotation.w);
        }

        public bool IsCurrentViewCompatible(SceneVisionObservation observation, float maxTranslation = .25f, float maxAngle = 15f)
            => IsObservationBindingCurrent(observation) &&
                Finite(maxTranslation) && Finite(maxAngle) && maxTranslation >= 0 && maxAngle >= 0 &&
                TryGetCurrentCameraPose(out Pose pose) &&
                Vector3.Distance(pose.position, observation.CameraPosition) <= maxTranslation &&
                Quaternion.Angle(pose.rotation, observation.CameraRotation) <= maxAngle;

        [Serializable] public sealed class ViewCompatibility
        {
            public bool bindingCurrent, poseAvailable, limitsValid, compatible;
            public float translationMeters = -1f, rotationDegrees = -1f, ageSeconds = -1f;
            public float maximumTranslationMeters, maximumRotationDegrees;
            public string reason;
        }

        // Read-only diagnostics from the same pose and thresholds used by the gate.
        // A recent image can still fail due to motion; increasing its TTL cannot fix that.
        public ViewCompatibility DescribeViewCompatibility(SceneVisionObservation observation,
            float maxTranslation = .25f, float maxAngle = 15f)
        {
            var value = new ViewCompatibility {
                bindingCurrent = IsObservationBindingCurrent(observation),
                limitsValid = Finite(maxTranslation) && Finite(maxAngle) && maxTranslation >= 0 && maxAngle >= 0,
                maximumTranslationMeters = maxTranslation, maximumRotationDegrees = maxAngle,
                ageSeconds = observation != null && Finite(observation.CapturedAtRealtime)
                    ? Time.realtimeSinceStartup - observation.CapturedAtRealtime : -1f };
            if (!value.bindingCurrent) { value.reason = "source-binding-invalid"; return value; }
            if (!value.limitsValid) { value.reason = "view-limits-invalid"; return value; }
            value.poseAvailable = TryGetCurrentCameraPose(out Pose pose);
            if (!value.poseAvailable) { value.reason = "current-camera-pose-unavailable"; return value; }
            value.translationMeters = Vector3.Distance(pose.position, observation.CameraPosition);
            value.rotationDegrees = Quaternion.Angle(pose.rotation, observation.CameraRotation);
            bool translation = value.translationMeters <= maxTranslation, rotation = value.rotationDegrees <= maxAngle;
            value.compatible = translation && rotation;
            value.reason = value.compatible ? "compatible" : !translation && !rotation ? "translation-and-rotation-exceeded" :
                !translation ? "translation-exceeded" : "rotation-exceeded";
            return value;
        }

        internal bool IsObservationBindingCurrent(SceneVisionObservation observation)
        {
            return IsObserving && observation != null && observation.HasImage &&
                observation.CaptureId.StartsWith(binding + "-", StringComparison.Ordinal) &&
                avatar != null && avatar.gameObject.scene.handle == observation.SceneHandle &&
                avatar.gameObject.scene.path == observation.ScenePath &&
                observation.LoadedSceneFingerprint == CurrentLoadedSceneFingerprint();
        }

        public bool TryResolve(SceneVisionObservation observation, Vector2 imagePoint,
            out SceneGroundedTarget target, out string reason)
        {
            target = null;
            if (!IsCurrentViewCompatible(observation, .35f, 25f) || !SceneVisionGrounding.Fresh(observation))
            { reason = "定位图片已过期、视点改变或来源失效，请重新观察。"; return false; }
            return SceneVisionGrounding.TryResolve(this, observation, imagePoint, out target, out reason);
        }

        public bool TryConfirmVisible(SceneVisionObservation observation, SceneGroundedTarget previous,
            out SceneGroundedTarget refreshed, out string reason)
        {
            refreshed = null;
            if (previous == null) { reason = "没有待确认的可见表面。"; return false; }
            if (!previous.TryProject(observation, out Vector2 pixel, out reason) ||
                !TryResolve(observation, pixel, out var current, out reason)) return false;
            if (previous.entry.renderer != current.entry.renderer || Vector3.Distance(previous.WorldPoint, current.WorldPoint) > .08f)
            { reason = "新图片中的原目标位置被遮挡或表面变化，未续用旧定位。"; return false; }
            refreshed = current; return true;
        }

        /// <summary>Associates a current capture pixel with one local object. A successful lookup
        /// may be an unverified geometry candidate; callers must check VisibleConfirmed.</summary>
        public bool TryResolveObservedObject(SceneVisionObservation observation, Vector2 imagePoint,
            out SceneObservedObject value, out string reason)
        {
            value = null;
            if (!IsCurrentViewCompatible(observation, .35f, 25f) || !SceneVisionGrounding.Fresh(observation))
            { reason = "物件观测已过期、视点改变或来源失效，请重新观察。"; return false; }
            return SceneVisionGrounding.TryResolveObject(this, observation, imagePoint, out value, out reason);
        }

        /// <summary>Searches only this selected entity's already rendered ID pixels, for an actual
        /// opaque visible part. It never searches for a replacement entity or reads behind glass.</summary>
        public bool TryFindObservedObject(SceneVisionObservation observation, string entityId,
            out SceneObservedObject value, out string reason)
        {
            value = null;
            if (!IsCurrentViewCompatible(observation, .35f, 25f) || !SceneVisionGrounding.Fresh(observation))
            { reason = "寻找物件可见部件需要当前有效的新图片。"; return false; }
            return SceneVisionGrounding.TryFindObject(this, observation, entityId, out value, out reason);
        }

        /// <summary>Confirms the same entity in a new image. Animation after an earlier capture
        /// does not force its old world point into the new frame; the new opaque pixels own position.</summary>
        public bool TryConfirmObject(SceneVisionObservation observation, SceneObservedObject previous,
            out SceneObservedObject refreshed, out string reason)
        {
            refreshed = null; reason = "";
            if (previous == null || previous.source != this || observation == null || observation.CaptureId == previous.CaptureId)
            { reason = "物件复核需要同一视觉来源的旧实体和不同的新图片。"; return false; }
            if (!previous.TryValidate(out reason) ||
                !TryFindObservedObject(observation, previous.EntityId, out refreshed, out reason)) return false;
            if (!refreshed.VisibleConfirmed || refreshed.Root != previous.Root)
            { refreshed = null; reason = "新图片未确认同一实体根的可见不透明部件。"; return false; }
            return true;
        }

        /// <summary>Bounded visible candidates and annotated RGB/crop from this exact observation.
        /// No new capture, semantic identification, capability or action permission is produced.</summary>
        public bool TryBuildObjectCandidateView(SceneVisionObservation observation, Vector2 focusPixel,
            string preferredEntityId, out SceneObjectCandidateView view, out string reason) =>
            SceneObjectCandidateView.TryBuild(this, observation, focusPixel, preferredEntityId, out view, out reason);

        public void SetObserving(bool observing)
        {
            if (IsObserving == observing) return;
            IsObserving = observing;
            if (!observing) CancelPending("角色已闭眼。", "closed");
            else
            {
                LatestObservation = SceneVisionObservation.WithoutImage("waiting", "已睁眼，尚未取得新场景图片。");
                Status = LatestObservation.Reason;
            }
        }

        /// <summary>Keep sampling the character view in the background. Only the newest completed frame is retained.</summary>
        public void SetContinuousObservation(bool enabled)
        {
            IsContinuousObservation = enabled;
            if (enabled && IsObserving)
                nextCaptureAt = Mathf.Min(nextCaptureAt, Time.realtimeSinceStartup);
        }

        public void RequestCapture(Action<SceneVisionObservation> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            string state = !IsObserving ? "closed" : !Application.isPlaying || !isActiveAndEnabled ? "unavailable" : null;
            string reason = state == "closed" ? "角色已闭眼，没有采集图片。" : "场景视觉仅在已启用的 Play 组件中采集。";
            if (state == null && !TryGetCurrentCameraPose(out _))
            { state = "unavailable"; reason = "角色眼位尚未校准；未使用用户相机或动态姿态猜测视点。"; }
            if (state == null && !SystemInfo.supportsAsyncGPUReadback)
            { state = "unavailable"; reason = "当前图形后端不支持异步场景图像读回。"; }
            if (state == null && inFlight != null && Time.realtimeSinceStartup - inFlight.time > 3f)
            { state = "unavailable"; reason = "上一份 GPU 读回尚未释放，暂不排队更多图片。"; }
            if (state != null) { ClearPreview(); Publish(SceneVisionObservation.WithoutImage(state, reason), new[] { callback }); return; }
            if (waiting.Count >= CallbackLimit)
            { Invoke(callback, SceneVisionObservation.WithoutImage("busy", "场景视觉请求队列已满。")); return; }
            waiting.Add(callback);
            Status = "等待本帧角色姿态更新后采集场景。";
        }

        public void CancelPending(string reason = "cancelled") => CancelPending(reason, "cancelled");

        private void CancelPending(string reason, string state)
        {
            generation++; binding = Guid.NewGuid().ToString("N");
            var cancelled = SceneVisionObservation.WithoutImage(state, reason);
            var callbacks = new List<Action<SceneVisionObservation>>(waiting); waiting.Clear();
            if (inFlight != null) { callbacks.AddRange(inFlight.callbacks); inFlight.callbacks.Clear(); }
            ClearPreview(); Publish(cancelled, callbacks);
            // A pending readback still owns its RT. Its callback releases it, without
            // publishing old pixels or permitting a second in-flight GPU allocation.
        }

        private void LateUpdate()
        {
            if (inFlight != null && (inFlight.generation == generation || waiting.Count > 0) && Time.realtimeSinceStartup - inFlight.time > 3f)
                CancelPending("场景图片读回超时，未使用旧帧。", "error");
            if (!IsObserving || (!IsContinuousObservation && waiting.Count == 0) ||
                inFlight != null || Time.realtimeSinceStartup < nextCaptureAt) return;
            if (!TryGetCurrentCameraPose(out Pose pose))
            { CancelPending("角色眼位已失效，未采集图片。", "unavailable"); return; }
            var callbacks = new List<Action<SceneVisionObservation>>(waiting); waiting.Clear();
            Readback item = null;
            try
            {
                EnsureCamera();
                int width = Mathf.Clamp(captureWidth, 320, 1280), height = Mathf.Clamp(captureHeight, 180, 960);
                float fov = Mathf.Clamp(verticalFieldOfView, 40, 100);
                item = new Readback { generation = generation, id = binding + "-" + Guid.NewGuid().ToString("N"),
                    utc = DateTime.UtcNow.ToString("o"), time = Time.realtimeSinceStartup, frame = Time.frameCount,
                    pose = pose, fov = fov, width = width, height = height, callbacks = callbacks,
                    sceneHandle = avatar.gameObject.scene.handle, sceneName = avatar.gameObject.scene.name,
                    scenePath = avatar.gameObject.scene.path,
                    sceneFingerprint = CurrentLoadedSceneFingerprint(),
                    target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) {
                        name = "NeEEvA scene RGB readback", hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1 } };
                if (!item.target.Create()) throw new InvalidOperationException("无法创建场景视觉渲染目标。");
                captureCamera.transform.SetPositionAndRotation(pose.position, pose.rotation);
                captureCamera.fieldOfView = fov; captureCamera.aspect = (float)width / height;
                captureCamera.cullingMask = sceneLayers; captureCamera.targetTexture = item.target;
                // Own callbacks before Camera.Render invokes scene callbacks, so closing
                // or disabling from those callbacks still completes each request once.
                inFlight = item;
                RenderTexture previous = RenderTexture.active;
                try
                {
                    rendering = true; selfView.Apply(); captureCamera.Render();
                    // The same temporary first-person mesh visibility remains applied for both renders.
                    // Failure of this optional local buffer must not erase a usable RGB observation.
                    try { item.groundingPending = SceneVisionGrounding.Render(captureCamera); }
                    catch (Exception groundingError)
                    { item.grounding = new SceneVisionGrounding.Frame { failure = groundingError.Message }; item.groundingReady = true; }
                }
                finally
                {
                    selfView?.Restore(); rendering = false;
                    if (captureCamera != null) captureCamera.targetTexture = null;
                    RenderTexture.active = previous;
                    if (!isActiveAndEnabled) DisposeCamera();
                }
                if (!IsObserving || !isActiveAndEnabled || item.generation != generation)
                {
                    if (ReferenceEquals(inFlight, item)) inFlight = null;
                    item.groundingPending?.Dispose();
                    Release(item); return;
                }
                Readback owned = item;
                if (owned.groundingPending != null)
                {
                    try
                    {
                        owned.groundingPending.Request(frame => {
                            owned.grounding = frame; owned.groundingReady = true;
                            owned.groundingPending = null; TryCompleteReadback(this, owned);
                        });
                        owned.groundingSubmitted = true;
                    }
                    catch (Exception groundingError)
                    {
                        owned.groundingPending.Dispose(); owned.groundingPending = null;
                        owned.grounding = new SceneVisionGrounding.Frame { failure = groundingError.Message }; owned.groundingReady = true;
                    }
                }
                AsyncGPUReadback.Request(item.target, 0, TextureFormat.RGBA32, request => CompleteReadback(this, owned, request));
                nextCaptureAt = Time.realtimeSinceStartup + 1f / Mathf.Clamp(maximumCapturesPerSecond, 1, 4);
                Status = "场景已渲染，正在异步读取图片。";
            }
            catch (Exception error)
            {
                if (item != null)
                {
                    if (!item.groundingSubmitted) item.groundingPending?.Dispose();
                    if (ReferenceEquals(inFlight, item)) inFlight = null; Release(item);
                }
                ClearPreview(); Publish(SceneVisionObservation.WithoutImage("error", error.Message), callbacks);
            }
        }

        private void EnsureCamera()
        {
            if (captureCamera != null) return;
            if (GraphicsSettings.currentRenderPipeline != null)
                throw new InvalidOperationException("当前场景视觉采集器仅验证了内置渲染管线。");
            var host = new GameObject("NeEEvA character perception camera") { hideFlags = HideFlags.HideAndDontSave };
            captureCamera = host.AddComponent<Camera>();
            captureCamera.enabled = false; captureCamera.stereoTargetEye = StereoTargetEyeMask.None;
            captureCamera.nearClipPlane = .025f; captureCamera.farClipPlane = 60;
            captureCamera.allowHDR = false; captureCamera.allowMSAA = false;
            captureCamera.clearFlags = CameraClearFlags.Skybox;
            captureCamera.backgroundColor = Color.black;
            var preparation = System.Diagnostics.Stopwatch.StartNew();
            try { selfView = new SceneVisionSelfView(avatar); }
            catch { DisposeCamera(); throw; }
            finally { preparation.Stop(); PreparationMilliseconds = (float)preparation.Elapsed.TotalMilliseconds; }
        }

        private static void CompleteReadback(NevaSceneVisionCapture owner, Readback item, AsyncGPUReadbackRequest request)
        {
            try
            {
                if (request.hasError) item.rgbError = "GPU 场景图像读回失败。";
                else item.pixels = request.GetData<byte>().ToArray();
            }
            catch (Exception error) { item.rgbError = error.Message; }
            finally { item.rgbReady = true; Release(item); TryCompleteReadback(owner, item); }
        }

        private static void TryCompleteReadback(NevaSceneVisionCapture owner, Readback item)
        {
            if (item.finished || !item.rgbReady || !item.groundingReady) return;
            item.finished = true;
            try
            {
                if (owner == null || !owner.isActiveAndEnabled || !owner.IsObserving || item.generation != owner.generation) return;
                if (owner.avatar == null || owner.avatar.gameObject.scene.handle != item.sceneHandle ||
                    owner.avatar.gameObject.scene.path != item.scenePath || item.sceneFingerprint != CurrentLoadedSceneFingerprint())
                    throw new InvalidOperationException("角色所属场景已变化，未发布上一场景图片。");
                if (!string.IsNullOrEmpty(item.rgbError)) throw new InvalidOperationException(item.rgbError);
                byte[] pixels = item.pixels;
                if (pixels.Length != item.width * item.height * 4) throw new InvalidOperationException("场景图像字节数不匹配。");
                // Camera.Render -> RenderTexture -> RGBA32 readback already matches
                // Texture2D's CPU row order on the verified D3D11 path. The API's
                // shader UV convention is not a CPU readback flip instruction.
                // The asymmetric scene regression checks this end-to-end orientation.
                var texture = new Texture2D(item.width, item.height, TextureFormat.RGBA32, false, false) {
                    name = "NeEEvA current scene observation", hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    texture.LoadRawTextureData(pixels); texture.Apply(false, false);
                    byte[] jpeg = texture.EncodeToJPG(Mathf.Clamp(owner.jpegQuality, 50, 95));
                    if (jpeg == null || jpeg.Length == 0) throw new InvalidOperationException("场景图片编码失败。");
                    var result = new SceneVisionObservation(item.id, "ready", "", item.utc, item.time, item.frame,
                        item.pose, item.fov, item.width, item.height, "data:image/jpeg;base64," + Convert.ToBase64String(jpeg),
                        item.sceneHandle, item.sceneName, item.scenePath, item.sceneFingerprint, item.grounding);
                    owner.ClearPreview(); owner.preview = texture; texture = null;
                    var callbacks = new List<Action<SceneVisionObservation>>(item.callbacks); item.callbacks.Clear();
                    owner.Publish(result, callbacks);
                }
                finally { if (texture != null) SceneVisionSelfView.DestroyOwned(texture); }
            }
            catch (Exception error)
            {
                if (owner != null && item.generation == owner.generation)
                {
                    var callbacks = new List<Action<SceneVisionObservation>>(item.callbacks); item.callbacks.Clear();
                    owner.ClearPreview(); owner.Publish(SceneVisionObservation.WithoutImage("error", error.Message), callbacks);
                }
            }
            finally
            {
                item.callbacks.Clear();
                if (owner != null && ReferenceEquals(owner.inFlight, item)) owner.inFlight = null;
                Release(item);
            }
        }

        private static void Release(Readback item)
        {
            if (item.target == null) return;
            item.target.Release(); SceneVisionSelfView.DestroyOwned(item.target); item.target = null;
        }

        private void Publish(SceneVisionObservation observation, IEnumerable<Action<SceneVisionObservation>> callbacks)
        {
            LatestObservation = observation; Status = observation.HasImage ? "已取得角色当前场景观察。" : observation.Reason;
            // Callbacks can cancel/rebind this component, so do not enumerate the mutable queue.
            int publishedGeneration = generation;
            foreach (var callback in new List<Action<SceneVisionObservation>>(callbacks))
                Invoke(callback, publishedGeneration == generation ? observation : LatestObservation);
        }
        private static void Invoke(Action<SceneVisionObservation> callback, SceneVisionObservation observation)
        { try { callback?.Invoke(observation); } catch (Exception error) { Debug.LogException(error); } }
        private void ClearPreview() { if (preview != null) SceneVisionSelfView.DestroyOwned(preview); preview = null; }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static string CurrentLoadedSceneFingerprint()
        {
            // Scene identity only, never inspect geometry or publish an object inventory.
            // Handle changes distinguish unload/reload; sorting ignores active-scene order.
            var identities = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded) identities.Add(scene.handle + ":" + scene.path + ":" + scene.name);
            }
            identities.Sort(StringComparer.Ordinal);
            byte[] identity = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", identities));
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(identity)).Replace("-", "").ToLowerInvariant();
        }
        private void DisposeCamera()
        {
            selfView?.Dispose(); selfView = null;
            if (captureCamera != null) SceneVisionSelfView.DestroyOwned(captureCamera.gameObject);
            captureCamera = null;
        }
        private void OnDisable()
        {
            IsObserving = false; CancelPending("场景视觉组件已停用。", "closed");
            if (!rendering) DisposeCamera();
        }
        private void OnDestroy() { IsObserving = false; CancelPending("场景视觉组件已销毁。", "closed"); DisposeCamera(); }
    }
}
