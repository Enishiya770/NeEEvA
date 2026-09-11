using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace NeEEvA.Player
{
    /// <summary>Displays a local Windows monitor on a dedicated TV surface, including PC VR.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(MeshRenderer))]
    public sealed class DesktopTvScreen : MonoBehaviour
    {
        [Header("Desktop source (Windows / PC VR)")]
        [Tooltip("0 = primary display; 1 and above = other displays sorted by desktop position.")]
        [Min(0)] public int monitorIndex;
        [Range(1, 30)] public int framesPerSecond = 12;
        [Range(320, 3840)] public int maxWidth = 1280;
        [Range(180, 2160)] public int maxHeight = 720;
        [Tooltip("Exclude Unity only from this TV's captured image. Screenshots and OBS remain unaffected. F8 toggles all active TVs. Currently requires Windows x64 with a single display.")]
        public bool excludeUnityWindows = true;
        [Header("Display")]
        [Min(0.1f)] public float screenAspect = 16f / 9f;
        public bool flipX;
        public bool flipY;
        [Range(0f, 2f)] public float brightness = 1f;

        public Texture2D DesktopTexture { get; private set; }
        public string Status { get; private set; } = "Starts when the scene enters Play Mode.";
        public int DisplayedFrames { get; private set; }
        public string ExclusionStatus => source != null ? source.ExclusionStatus : "TV-only window filtering starts in Play Mode.";
        public bool ExclusionHasError => source != null && runningExclude && !string.IsNullOrEmpty(source.LastError);
        public int ExcludedWindowCount => source != null ? source.ExcludedWindowCount : 0;

        private WindowsDesktopFrameSource source;
        private bool runningExclude;
        private static int lastExclusionHotkeyFrame = -1;
        private MeshRenderer screen;
        private MaterialPropertyBlock displayProperties, originalProperties;
        private int runningMonitor, runningFps, runningWidth, runningHeight;
        private static readonly int MainTexture = Shader.PropertyToID("_MainTex");
        private static readonly int SourceAspect = Shader.PropertyToID("_SourceAspect");
        private static readonly int ScreenAspect = Shader.PropertyToID("_ScreenAspect");
        private static readonly int FlipX = Shader.PropertyToID("_FlipX");
        private static readonly int FlipY = Shader.PropertyToID("_FlipY");
        private static readonly int Brightness = Shader.PropertyToID("_Brightness");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCaptureExclusion()
        {
            RestoreCaptureExclusion();
            lastExclusionHotkeyFrame = -1;
        }

        public static void RestoreCaptureExclusion()
        {
            string shutdownError = WindowsDesktopFrameSource.StopAll();
            if (!string.IsNullOrEmpty(shutdownError)) Debug.LogWarning("[Desktop TV] " + shutdownError);
            // Cleanup compatibility for the old global-affinity implementation only.
            // No TV creates a WindowsCaptureExclusion lease anymore.
            string error = WindowsCaptureExclusion.RestoreAll();
            if (!string.IsNullOrEmpty(error)) Debug.LogWarning("[Desktop TV] " + error);
        }

        private void OnEnable()
        {
            screen = GetComponent<MeshRenderer>();
            originalProperties = new MaterialPropertyBlock();
            displayProperties = new MaterialPropertyBlock();
            screen.GetPropertyBlock(originalProperties);
            screen.GetPropertyBlock(displayProperties);
            DisplayedFrames = 0;
            if (!WindowsDesktopFrameSource.IsSupported)
            {
                Status = "Local desktop capture requires Windows. A standalone headset requires a separate PC video stream.";
                Debug.LogWarning("[Desktop TV] " + Status, this);
                return;
            }
            if (screen.sharedMaterial == null || !screen.sharedMaterial.HasProperty(SourceAspect))
            {
                Status = "Assign the DesktopScreen material to this screen surface.";
                Debug.LogWarning("[Desktop TV] " + Status, this);
                return;
            }
            StartSource();
        }

        private void StartSource()
        {
            runningMonitor = Mathf.Max(0, monitorIndex);
            runningFps = Mathf.Clamp(framesPerSecond, 1, 30);
            runningWidth = Mathf.Clamp(maxWidth, 320, 3840);
            runningHeight = Mathf.Clamp(maxHeight, 180, 2160);
            runningExclude = excludeUnityWindows;
            source = new WindowsDesktopFrameSource(runningMonitor, runningWidth, runningHeight, runningFps, runningExclude);
            Status = "Waiting for the first desktop frame...";
        }

        private void RestartSource()
        {
            source?.Dispose();
            // Do not keep a frame from the previous mode if the new backend cannot start.
            if (DesktopTexture != null) Destroy(DesktopTexture);
            DesktopTexture = null;
            displayProperties.SetTexture(MainTexture, Texture2D.blackTexture);
            screen.SetPropertyBlock(displayProperties);
            StartSource();
        }

        private void Update()
        {
            if (source == null) return;
            UpdateExclusionHotkey();
            if (runningMonitor != Mathf.Max(0, monitorIndex) || runningFps != Mathf.Clamp(framesPerSecond, 1, 30) ||
                runningWidth != Mathf.Clamp(maxWidth, 320, 3840) || runningHeight != Mathf.Clamp(maxHeight, 180, 2160) ||
                runningExclude != excludeUnityWindows || source.IsDisposed)
            {
                RestartSource();
            }
            if (source.TryConsumeFrame(out byte[] pixels, out int width, out int height))
            {
                try
                {
                    if (DesktopTexture == null || DesktopTexture.width != width || DesktopTexture.height != height)
                    {
                        if (DesktopTexture != null) Destroy(DesktopTexture);
                        DesktopTexture = new Texture2D(width, height, TextureFormat.BGRA32, false, false)
                        {
                            name = "Live Windows Desktop",
                            wrapMode = TextureWrapMode.Clamp,
                            filterMode = FilterMode.Bilinear
                        };
                    }
                    DesktopTexture.LoadRawTextureData(pixels);
                    DesktopTexture.Apply(false, false);
                    DisplayedFrames++;
                    Status = "Live desktop " + width + " x " + height + " (display " + runningMonitor + ")";
                }
                finally { source.ReleaseFrame(pixels); }
            }
            else if (!string.IsNullOrEmpty(source.LastError)) Status = source.LastError;

            if (DesktopTexture == null) return;
            displayProperties.SetTexture(MainTexture, DesktopTexture);
            displayProperties.SetFloat(SourceAspect, (float)DesktopTexture.width / DesktopTexture.height);
            displayProperties.SetFloat(ScreenAspect, Mathf.Max(0.1f, screenAspect));
            displayProperties.SetFloat(FlipX, flipX ? 1f : 0f);
            displayProperties.SetFloat(FlipY, flipY ? 1f : 0f);
            displayProperties.SetFloat(Brightness, brightness);
            screen.SetPropertyBlock(displayProperties);
        }

        /// <summary>Can also be bound to a UI Toggle, including a future VR settings panel.</summary>
        public void SetUnityCaptureExclusion(bool exclude)
        {
            excludeUnityWindows = exclude;
            if (isActiveAndEnabled && source != null && runningExclude != exclude) RestartSource();
        }

        private void UpdateExclusionHotkey()
        {
            if (!Application.isFocused || lastExclusionHotkeyFrame == Time.frameCount) return;
#if ENABLE_INPUT_SYSTEM
            bool pressed = Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            bool pressed = Input.GetKeyDown(KeyCode.F8);
#else
            bool pressed = false;
#endif
            if (!pressed) return;
            lastExclusionHotkeyFrame = Time.frameCount;
            // F8 is a convenience shortcut; each TV still owns an independent capture filter.
            var displays = FindObjectsOfType<DesktopTvScreen>();
            bool anyEnabled = false;
            foreach (var display in displays)
                if (display.isActiveAndEnabled && display.source != null && display.excludeUnityWindows) anyEnabled = true;
            foreach (var display in displays)
                if (display.isActiveAndEnabled && display.source != null) display.SetUnityCaptureExclusion(!anyEnabled);
        }

        private void OnApplicationQuit() => RestoreCaptureExclusion();

        private void OnDisable()
        {
            source?.Dispose();
            source = null;
            if (screen != null && originalProperties != null) screen.SetPropertyBlock(originalProperties);
            if (DesktopTexture != null) Destroy(DesktopTexture);
            DesktopTexture = null;
            Status = "Desktop capture stopped.";
        }
    }
}
