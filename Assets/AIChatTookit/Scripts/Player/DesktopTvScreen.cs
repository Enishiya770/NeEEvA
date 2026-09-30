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
        public enum FilterFallbackBehavior
        {
            HideUnityGlobally,
            KeepUnityVisible
        }

        [Header("Desktop source (Windows / PC VR)")]
        [Tooltip("0 = primary display; 1 and above = other displays sorted by desktop position.")]
        [Min(0)] public int monitorIndex;
        [Range(1, 30)] public int framesPerSecond = 12;
        [Range(320, 3840)] public int maxWidth = 1280;
        [Range(180, 2160)] public int maxHeight = 720;
        [Tooltip("Exclude Unity from this TV's image. The preferred filter leaves screenshots and OBS unaffected; an incompatible driver uses the selected fallback behavior below. F8 toggles all active TVs.")]
        public bool excludeUnityWindows = true;
        [Tooltip("When the local filter is unsupported: hide Unity from all Windows capture tools, or keep it visible for OBS and screenshots (the TV may show Unity recursively).")]
        public FilterFallbackBehavior filterFallbackBehavior = FilterFallbackBehavior.HideUnityGlobally;
        [Header("Display")]
        [Min(0.1f)] public float screenAspect = 16f / 9f;
        public bool flipX;
        public bool flipY;
        [Range(0f, 2f)] public float brightness = 1f;

        public Texture2D DesktopTexture { get; private set; }
        public string Status { get; private set; } = "Starts when the scene enters Play Mode.";
        public int DisplayedFrames { get; private set; }
        public string ExclusionStatus
        {
            get
            {
                if (fallbackActive)
                {
                    if (filterFallbackBehavior == FilterFallbackBehavior.KeepUnityVisible)
                        return "Compatibility fallback: Unity is included so OBS and screenshots can see it; TV-only filtering is unavailable.";
                    string status = globalExclusion != null ? globalExclusion.Status : "Global Unity window exclusion is unavailable.";
                    return "Legacy global-exclusion fallback: " + status;
                }
                return source != null ? source.ExclusionStatus : "TV-only window filtering starts in Play Mode.";
            }
        }
        public bool ExclusionHasError => fallbackActive
            ? filterFallbackBehavior == FilterFallbackBehavior.HideUnityGlobally &&
              (globalExclusion == null || globalExclusion.HasError)
            : source != null && runningExclude && !string.IsNullOrEmpty(source.LastError);
        public int ExcludedWindowCount => fallbackActive
            ? globalExclusion != null ? globalExclusion.ExcludedWindowCount : 0
            : source != null ? source.ExcludedWindowCount : 0;

        private WindowsDesktopFrameSource source;
        private WindowsCaptureExclusion globalExclusion;
        private bool runningExclude;
        private bool runningFiltered;
        private bool fallbackActive;
        private int blankFrameCount;
        private float nextExclusionRefresh;
        private FilterFallbackBehavior runningFallbackBehavior;
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
            // Also releases any fallback lease left behind during an interrupted domain reload.
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
            runningFiltered = excludeUnityWindows && !fallbackActive;
            runningFallbackBehavior = filterFallbackBehavior;
            source = new WindowsDesktopFrameSource(runningMonitor, runningWidth, runningHeight, runningFps, runningFiltered);
            blankFrameCount = 0;
            Status = runningFiltered
                ? "Waiting for the first desktop frame..."
                : fallbackActive
                    ? filterFallbackBehavior == FilterFallbackBehavior.KeepUnityVisible
                        ? "Starting compatibility desktop capture with Unity visible to OBS and screenshots..."
                        : "Starting compatibility desktop capture with Unity window exclusion..."
                    : "Waiting for the first desktop frame...";
        }

        private void RestartSource()
        {
            source?.Dispose();
            ReleaseGlobalExclusion();
            fallbackActive = false;
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
                runningExclude != excludeUnityWindows || runningFiltered != (excludeUnityWindows && !fallbackActive) ||
                (fallbackActive && runningFallbackBehavior != filterFallbackBehavior) || source.IsDisposed)
            {
                RestartSource();
            }
            if (fallbackActive && Time.realtimeSinceStartup >= nextExclusionRefresh)
            {
                globalExclusion?.Refresh();
                nextExclusionRefresh = Time.realtimeSinceStartup + 0.5f;
            }
            if (!fallbackActive && runningFiltered && !string.IsNullOrEmpty(source.LastError))
            {
                ActivateFallback(source.LastError);
            }
            if (source == null) return;
            if (source.TryConsumeFrame(out byte[] pixels, out int width, out int height))
            {
                bool blank = runningFiltered && IsBlankFrame(pixels);
                bool switchToFallback = false;
                try
                {
                    if (blank)
                    {
                        blankFrameCount++;
                        if (blankFrameCount >= 3)
                        {
                            Status = "TV-only filtering returned blank frames; switching to compatibility capture...";
                            switchToFallback = true;
                        }
                    }
                    else blankFrameCount = 0;
                    if (!switchToFallback && (DesktopTexture == null || DesktopTexture.width != width || DesktopTexture.height != height))
                    {
                        if (DesktopTexture != null) Destroy(DesktopTexture);
                        DesktopTexture = new Texture2D(width, height, TextureFormat.BGRA32, false, false)
                        {
                            name = "Live Windows Desktop",
                            wrapMode = TextureWrapMode.Clamp,
                            filterMode = FilterMode.Bilinear
                        };
                    }
                    if (!switchToFallback)
                    {
                        DesktopTexture.LoadRawTextureData(pixels);
                        DesktopTexture.Apply(false, false);
                        DisplayedFrames++;
                        Status = fallbackActive
                            ? filterFallbackBehavior == FilterFallbackBehavior.KeepUnityVisible
                                ? "Live desktop " + width + " x " + height + " (compatibility capture; Unity visible to OBS/screenshots; display " + runningMonitor + ")"
                                : "Live desktop " + width + " x " + height + " (compatibility capture with Unity exclusion; display " + runningMonitor + ")"
                            : "Live desktop " + width + " x " + height + " (display " + runningMonitor + ")";
                    }
                }
                finally { source.ReleaseFrame(pixels); }
                if (switchToFallback) ActivateFallback("The filtered capture returned consecutive blank frames.");
            }
            else if (!fallbackActive && !string.IsNullOrEmpty(source.LastError))
            {
                ActivateFallback(source.LastError);
            }

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
            if (isActiveAndEnabled && source != null && (runningExclude != exclude || fallbackActive)) RestartSource();
        }

        private void ActivateFallback(string filterError)
        {
            if (fallbackActive || !excludeUnityWindows || source == null) return;
            source.Dispose();
            source = null;
            fallbackActive = true;
            blankFrameCount = 0;
            if (DesktopTexture != null) Destroy(DesktopTexture);
            DesktopTexture = null;
            displayProperties.SetTexture(MainTexture, Texture2D.blackTexture);
            screen.SetPropertyBlock(displayProperties);
            if (filterFallbackBehavior == FilterFallbackBehavior.HideUnityGlobally)
            {
                try { globalExclusion = new WindowsCaptureExclusion(); }
                catch (System.Exception error)
                {
                    Debug.LogWarning("[Desktop TV] Could not enable global Unity window exclusion: " + error.Message, this);
                }
            }
            StartSource();
            Status = filterFallbackBehavior == FilterFallbackBehavior.KeepUnityVisible
                ? "TV-only filtering unavailable; Unity remains visible to OBS and screenshots. " + filterError
                : "TV-only filtering unavailable; using compatibility desktop capture with Unity exclusion. " + filterError;
        }

        private void ReleaseGlobalExclusion()
        {
            if (globalExclusion == null) return;
            globalExclusion.Dispose();
            globalExclusion = null;
        }

        private static bool IsBlankFrame(byte[] pixels)
        {
            if (pixels == null || pixels.Length < 4) return true;
            int stride = System.Math.Max(4, pixels.Length / 256);
            stride -= stride % 4;
            for (int i = 0; i < pixels.Length; i += stride)
                if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) return false;
            return true;
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
            ReleaseGlobalExclusion();
            fallbackActive = false;
            if (screen != null && originalProperties != null) screen.SetPropertyBlock(originalProperties);
            if (DesktopTexture != null) Destroy(DesktopTexture);
            DesktopTexture = null;
            Status = "Desktop capture stopped.";
        }
    }
}
