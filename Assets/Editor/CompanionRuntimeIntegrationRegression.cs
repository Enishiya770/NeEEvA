using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Player;
using NeEEvA.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>
/// Exercises the production presentation Start/Update/lifecycle and real ChatSample
/// adapter on inactive runtime fixtures and edit-only disabled business objects. Does not invoke ConfigurePreview, chat
/// Awake/Start, microphone capture, network services, or a user's scene.
/// </summary>
public static class CompanionRuntimeIntegrationRegression
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly List<string> Checks = new List<string>();

    [Serializable]
    private sealed class Report
    {
        public bool passed;
        public string scope;
        public string error;
        public string unityVersion;
        public string[] checks;
    }

    public static void RunBatch()
    {
        string path = Application.dataPath.Replace('\\', '/');
        if (!Application.isBatchMode || !path.Contains("/Server/ARDY/runtime/unity-naturalness-validation/"))
            throw new InvalidOperationException("Run only in the isolated naturalness validation project.");
        var report = new Report
        {
            scope = "Production CompanionPresentation Start/Update/disable/enable with real ChatSample and legacy Canvas components; editor visual preview, reload handoff, saved legacy suppression, explicit-disable fallback and save exclusion. Runtime business fixtures stay inactive; edit fixtures use disabled ordinary MonoBehaviours outside Play Mode. No ConfigurePreview, microphone, network, private scene, Play Mode or headset execution.",
            unityVersion = Application.unityVersion
        };
        try
        {
            Checks.Clear();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            NormalStartupAndLifecycle();
            MissingDependenciesLeaveLegacyVisible();
            EditorPreviewAndSaveLifecycle();
            report.passed = true;
        }
        catch (Exception error)
        {
            report.error = error.ToString();
            Debug.LogException(error);
        }
        report.checks = Checks.ToArray();
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/companion-runtime-integration.json", JsonUtility.ToJson(report, true));
        Debug.Log("COMPANION_RUNTIME_INTEGRATION_" + (report.passed ? "OK " : "FAILED ") + Checks.Count);
        EditorApplication.Exit(report.passed ? 0 : 1);
    }

    public static void RunStreamingBatch()
    {
        string path = Application.dataPath.Replace('\\', '/');
        if (!Application.isBatchMode || !path.Contains("/Server/ARDY/runtime/unity-naturalness-validation/"))
            throw new InvalidOperationException("Run only in the isolated naturalness validation project.");
        var report = new Report
        {
            scope = "Real ChatSample partial updates and production CompanionPresentation Start/Update while an inactive RTSpeechHandler remains recording. Checks punctuation, revisions, long-line clipping, pauses and final-text fallback with a real CJK font and graphics render. No microphone, network, SendData, StopRecording or business Awake/Start execution.",
            unityVersion = Application.unityVersion
        };
        try
        {
            Checks.Clear();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            StreamingRecognitionRemainsVisible();
            report.passed = true;
        }
        catch (Exception error)
        {
            report.error = error.ToString();
            Debug.LogException(error);
        }
        report.checks = Checks.ToArray();
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/companion-streaming-integration.json", JsonUtility.ToJson(report, true));
        Debug.Log("COMPANION_STREAMING_INTEGRATION_" + (report.passed ? "OK " : "FAILED ") + Checks.Count);
        EditorApplication.Exit(report.passed ? 0 : 1);
    }

    private static void StreamingRecognitionRemainsVisible()
    {
        var host = Inactive("Offline streaming ChatSample");
        var speechHost = Inactive("Offline recording state");
        var legacy = LegacyCanvas("Legacy recognition data", true, true);
        var cameraObject = new GameObject("Streaming capture camera", typeof(Camera));
        var eventObject = new GameObject("Streaming EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        CompanionPresentation view = null;
        try
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(CompanionPresentationSetup.FontPath);
            Check(font != null, "the bundled Noto CJK font is loaded for streaming layout and rendering");
            var chat = host.AddComponent<ChatSample>(); chat.enabled = false;
            var speech = speechHost.AddComponent<RTSpeechHandler>(); speech.enabled = false;
            Set(speech, "m_ChatSample", chat);
            Set(speech, "m_IsRecording", true);
            Set(speech, "m_AwakeState", true);
            Set(chat, "m_PresentationRealtime", speech);
            Set(chat, "m_EnableSpeculativeListening", true);
            Set(chat, "m_ShowStreamingTranscript", true);
            Set(chat, "m_ChatSettings", null); // Partial UI updates must work without an LLM request.
            var history = new List<string> { "Existing user turn", "Existing assistant reply" };
            Set(chat, "m_ChatHistory", history);
            var source = TextView("Silent assistant source", legacy.transform, "");
            var recognition = TextView("Legacy ASR label", legacy.transform, "");
            Set(chat, "m_TextBack", source); Set(chat, "m_RecordTips", recognition);
            Set(chat, "m_ChatPanel", legacy.gameObject);
            view = host.AddComponent<CompanionPresentation>();
            Set(view, "interfaceFont", font);
            Set(view, "playerCamera", cameraObject.GetComponent<Camera>());
            Call(view, "Start");
            Check(Get<bool>(view, "built") && !Get<bool>(view, "preview"), "streaming fixture starts the real production view");
            Canvas.ForceUpdateCanvases();
            var text = Get<Text>(view, "transcriptText");
            var group = Get<CanvasGroup>(view, "transcript");
            int audioMs = 800;
            foreach (string partial in new[] { "你好", "你好。", "Hello.", "まだ話しています。", "被修正的结果。" })
            {
                chat.UpdateStreamingTranscript(new SenseVoiceSpeechToText.StreamingTranscript
                {
                    Text = partial, StableText = partial, AudioMs = audioMs, Revision = audioMs > 800
                });
                audioMs += 850;
                Check(recognition.text == partial + " …", "real partial reaches the retained ASR label: " + partial);
                Call(view, "Update");
                Check(text.text == partial && group.alpha > .99f,
                    "complete partial is visible before recording ends, including punctuation: " + partial);
                Check(Get<bool>(speech, "m_IsRecording") && chat.PresentationIsTranscribing,
                    "partial display leaves the ongoing recording state unchanged: " + partial);
            }

            recognition.text = ""; // An old final-label expiry must not erase this new live turn.
            Call(view, "Update");
            Check(text.text == "被修正的结果。" && group.alpha > .99f,
                "live ASR data survives expiration of the legacy recognition label");
            Set(view, "transcriptChangedAt", Time.unscaledTime - 12f);
            Call(view, "Update");
            Check(group.alpha > .99f && text.text == "被修正的结果。",
                "an unchanged partial stays visible during a recording pause longer than five seconds");

            const string longPartial = "这是一条还没有结束的实时语音识别长句。前面的标点不会让正在说的话消失，文字会持续更新，超出窗口时保留完整转写并显示最新的末尾。最后几个字应该清楚地留在右侧。";
            chat.UpdateStreamingTranscript(new SenseVoiceSpeechToText.StreamingTranscript
                { Text = longPartial, AudioMs = audioMs, Revision = true });
            Call(view, "Update"); Canvas.ForceUpdateCanvases(); Call(view, "Update");
            Check(text.text == longPartial, "long recognition text remains complete in the Text component");
            Check(text.horizontalOverflow == HorizontalWrapMode.Overflow && text.alignment == TextAnchor.MiddleRight,
                "long recognition follows the latest text on one horizontal line");
            Check(text.GetComponentInParent<RectMask2D>() != null, "a viewport mask clips long recognition text");
            CaptureStreamingRecognition(view, cameraObject.GetComponent<Camera>(), "companion-streaming-longpartial.png");

            const string revision = "其实是短句。";
            chat.UpdateStreamingTranscript(new SenseVoiceSpeechToText.StreamingTranscript
                { Text = revision, AudioMs = audioMs + 850, Revision = true });
            Call(view, "Update"); Canvas.ForceUpdateCanvases(); Call(view, "Update");
            Check(text.text == revision && text.alignment == TextAnchor.MiddleLeft,
                "a corrected shorter partial replaces the long text and restores left alignment");
            Check(history.Count == 2 && history[0] == "Existing user turn" && history[1] == "Existing assistant reply" &&
                  !chat.IsAISpeaking && !chat.HasPendingConversationWork,
                "partial display neither submits a user turn nor starts assistant work");
            Check(!host.activeInHierarchy && !speechHost.activeInHierarchy && !chat.enabled && !speech.enabled,
                "business and microphone components remain inactive throughout all streaming checks");

            // Model the final recognizer's state handoff without invoking microphone or LLM code.
            Set(speech, "m_IsRecording", false);
            Set(chat, "m_StreamingTranscript", "");
            recognition.text = "最终识别结果。";
            Call(view, "Update");
            Check(!chat.PresentationIsTranscribing && text.text == recognition.text && group.alpha > .99f,
                "after recording, the final ASR result replaces the live partial");
            Set(view, "transcriptChangedAt", Time.unscaledTime - 12f);
            Call(view, "Update");
            Check(group.alpha == 0, "the final result still expires after its normal idle linger");
        }
        finally
        {
            if (view != null) { Call(view, "OnDisable"); Call(view, "OnDestroy"); }
            Object.DestroyImmediate(host); Object.DestroyImmediate(speechHost);
            Object.DestroyImmediate(legacy.gameObject); Object.DestroyImmediate(cameraObject);
            Object.DestroyImmediate(eventObject);
        }
    }

    private static void CaptureStreamingRecognition(CompanionPresentation view, Camera camera, string fileName)
    {
        Check(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null,
            "streaming recognition verification uses an actual graphics backend");
        var target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
        var previousTarget = camera.targetTexture;
        var previousActive = RenderTexture.active;
        var roots = new[] { Get<Canvas>(view, "hudCanvas"), Get<Canvas>(view, "panelCanvas") };
        var text = Get<Text>(view, "transcriptText");
        string value = text.text;
        Texture2D image = null;
        try
        {
            foreach (var canvas in roots)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 2;
            }
            camera.orthographic = true; camera.orthographicSize = 5;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.045f, .06f, .065f, 1);
            target.Create(); camera.targetTexture = target;
            Canvas.ForceUpdateCanvases(); Call(view, "Update"); Canvas.ForceUpdateCanvases();
            camera.Render(); RenderTexture.active = target;
            image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
            Color32[] withText = image.GetPixels32();
            var userHeader = Get<Text>(view, "transcriptSpeakerText");
            string userHeaderValue = userHeader.text;
            Vector3[] headerCorners = new Vector3[4]; userHeader.rectTransform.GetWorldCorners(headerCorners);
            Vector3 headerLower = camera.WorldToScreenPoint(headerCorners[0]);
            Vector3 headerUpper = camera.WorldToScreenPoint(headerCorners[2]);
            int firstHeaderVertices = userHeader.cachedTextGenerator.vertexCount;
            Canvas.ForceUpdateCanvases(); camera.Render();
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
            Color32[] nextFrame = image.GetPixels32();
            userHeader.text = ""; Canvas.ForceUpdateCanvases(); camera.Render();
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
            Color32[] withoutHeader = image.GetPixels32();
            userHeader.text = userHeaderValue; Canvas.ForceUpdateCanvases(); camera.Render();
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
            Color32[] restoredHeader = image.GetPixels32();
            int firstHeaderPixels = CountHeaderGlyphs(withText, withoutHeader, target.width, target.height, headerLower, headerUpper);
            int nextHeaderPixels = CountHeaderGlyphs(nextFrame, withoutHeader, target.width, target.height, headerLower, headerUpper);
            int restoredHeaderPixels = CountHeaderGlyphs(restoredHeader, withoutHeader, target.width, target.height, headerLower, headerUpper);
            Debug.Log("COMPANION_STREAMING_HEADER first=" + firstHeaderPixels + " repeated=" + nextHeaderPixels +
                " rebuilt=" + restoredHeaderPixels + " firstVertices=" + firstHeaderVertices + " cull=" + userHeader.canvasRenderer.cull +
                " rect=" + userHeader.rectTransform.rect + " active=" + userHeader.gameObject.activeInHierarchy);
            Check(nextHeaderPixels > 20, "the user speaker header renders visible glyphs with long streaming recognition without a forced text rewrite");
            Check(restoredHeaderPixels > 20, "the user speaker header remains visible after a text mesh rebuild");
            withText = restoredHeader;
            Directory.CreateDirectory("Logs");
            File.WriteAllBytes(Path.Combine("Logs", fileName), image.EncodeToPNG());
            Vector3[] corners = new Vector3[4];
            Get<RectTransform>(view, "transcriptCard").GetWorldCorners(corners);
            Vector3 lower = camera.WorldToScreenPoint(corners[0]);
            Vector3 upper = camera.WorldToScreenPoint(corners[2]);
            text.text = ""; Canvas.ForceUpdateCanvases(); camera.Render();
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
            Color32[] withoutText = image.GetPixels32();
            int glyphPixels = 0, escapedPixels = 0;
            for (int y = 0; y < image.height; y++)
                for (int x = 0; x < image.width; x++)
                {
                    int i = y * image.width + x;
                    if (!PixelDiffers(withText[i], withoutText[i])) continue;
                    if (x >= lower.x - 1 && x <= upper.x + 1 && y >= lower.y - 1 && y <= upper.y + 1)
                        glyphPixels++;
                    else escapedPixels++;
                }
            Check(glyphPixels > 100, "long partial renders real readable glyph pixels inside the recognition card");
            Check(escapedPixels < 10, "the long partial renders no glyphs outside the masked recognition card");
        }
        finally
        {
            text.text = value;
            foreach (var canvas in roots) if (canvas != null) canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            camera.targetTexture = previousTarget; RenderTexture.active = previousActive;
            if (image != null) Object.DestroyImmediate(image);
            target.Release(); Object.DestroyImmediate(target);
        }
    }

    private static int CountHeaderGlyphs(Color32[] visible, Color32[] empty, int width, int height, Vector3 lower, Vector3 upper)
    {
        int count = 0;
        for (int y = Mathf.Max(0, Mathf.FloorToInt(lower.y) - 1); y < Mathf.Min(height, Mathf.CeilToInt(upper.y) + 1); y++)
            for (int x = Mathf.Max(0, Mathf.FloorToInt(lower.x) - 1); x < Mathf.Min(width, Mathf.CeilToInt(upper.x) + 1); x++)
                if (PixelDiffers(visible[y * width + x], empty[y * width + x])) count++;
        return count;
    }

    private static void NormalStartupAndLifecycle()
    {
        var host = Inactive("Offline real ChatSample");
        var legacyChat = LegacyCanvas("Legacy chat", true, true);
        var legacyHistory = LegacyCanvas("Legacy history", false, false);
        var unrelated = LegacyCanvas("Unrelated canvas", true, true);
        var cameraObject = new GameObject("Offline camera", typeof(Camera));
        var eventObject = new GameObject("Offline EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        CompanionPresentation presentation = null;
        try
        {
            var chat = host.AddComponent<ChatSample>();
            chat.enabled = false;
            var source = TextView("Original source", legacyChat.transform, "The current spoken sentence.");
            var recognition = TextView("Recognition source", legacyChat.transform, "A real user transcript");
            var input = new GameObject("Legacy input", typeof(RectTransform), typeof(InputField));
            input.transform.SetParent(legacyChat.transform, false);
            chat.m_InputWord = input.GetComponent<InputField>();
            chat.m_InputWord.text = "Unsubmitted draft";
            Set(chat, "m_ChatPanel", legacyChat.gameObject);
            Set(chat, "m_HistoryPanel", legacyHistory.gameObject);
            Set(chat, "m_TextBack", source);
            Set(chat, "m_RecordTips", recognition);
            Set(chat, "m_ChatHistory", new List<string> { "User sentence", "Assistant sentence" });
            presentation = host.AddComponent<CompanionPresentation>();
            Set(presentation, "playerCamera", cameraObject.GetComponent<Camera>());
            Set(presentation, "interfaceFont", Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));

            Check(!host.activeInHierarchy && !chat.enabled, "business component remains inactive and disabled before presentation Start");
            Call(presentation, "Start");
            Check(!Get<bool>(presentation, "preview"), "the production path runs without ConfigurePreview");
            Check(Get<bool>(presentation, "built"), "production Start completes building the actual presentation");
            Check(ReferenceEquals(chat, Get<ChatSample>(presentation, "chat")), "production Start resolves its real ChatSample owner");
            var hud = Get<Canvas>(presentation, "hudCanvas");
            var panel = Get<Canvas>(presentation, "panelCanvas");
            Check(hud != null && panel != null && hud.enabled && panel.enabled, "both replacement canvases exist and are enabled");
            Check(hud.gameObject.activeSelf && panel.gameObject.activeSelf, "replacement canvas roots are visible independently of the inactive business host");
            Check(!legacyChat.enabled && !legacyHistory.enabled, "production HideLegacyViews disables both legacy canvases");
            Check(!legacyChat.GetComponent<GraphicRaycaster>().enabled && !legacyHistory.GetComponent<GraphicRaycaster>().enabled, "legacy hit testing is disabled");
            Check(legacyChat.gameObject.activeSelf && legacyHistory.gameObject.activeSelf, "legacy source GameObjects remain active");
            Check(unrelated.enabled && unrelated.GetComponent<GraphicRaycaster>().enabled, "unrelated scene UI remains unchanged");
            Check(source.text == "The current spoken sentence." && chat.m_InputWord.text == "Unsubmitted draft", "startup preserves source text and an existing draft");
            Check(Object.FindObjectsOfType<EventSystem>().Length == 1, "startup reuses the existing EventSystem");
            Check(!presentation.IsPanelVisible && !presentation.IsVRPresentation, "desktop startup keeps complex controls collapsed");
            Call(presentation, "Update");
            Check(Get<Text>(presentation, "originalText").text == source.text, "real Update projects the legacy spoken source to the new subtitle");
            Check(Get<Text>(presentation, "transcriptText").text == recognition.text, "real Update projects recognition text from the retained source");
            source.text = "A changed spoken sentence.";
            legacyChat.enabled = true;
            Call(presentation, "Update");
            Check(!legacyChat.enabled, "Update prevents a legacy canvas being accidentally shown again");
            Check(Get<Text>(presentation, "originalText").text == source.text, "subsequent source changes reach the replacement subtitle");
            Call(presentation, "OpenPage", 1);
            Check(presentation.IsPanelVisible && Get<GameObject>(presentation, "historyPage").activeSelf, "the production history action opens the replacement history page");
            Check(Get<List<GameObject>>(presentation, "historyRows").Count == 2, "the production history page reads real ChatSample history");
            Check(!legacyChat.enabled && !legacyHistory.enabled, "opening history never switches back to old canvases");
            int canvases = Object.FindObjectsOfType<Canvas>(true).Length;
            Call(presentation, "Start");
            Check(Object.FindObjectsOfType<Canvas>(true).Length == canvases, "repeated startup does not duplicate canvases");

            Call(presentation, "OnDisable");
            Check(legacyChat.enabled && !legacyHistory.enabled, "disabling restores each legacy canvas's original enabled state");
            Check(legacyChat.GetComponent<GraphicRaycaster>().enabled && !legacyHistory.GetComponent<GraphicRaycaster>().enabled, "disabling restores each legacy raycaster's original state");
            Check(!hud.gameObject.activeSelf && !panel.gameObject.activeSelf, "disabling hides the replacement canvases");
            Call(presentation, "OnEnable");
            Check(!legacyChat.enabled && !legacyHistory.enabled && !legacyChat.GetComponent<GraphicRaycaster>().enabled,
                "re-enabling suppresses legacy rendering and hit testing immediately, before Update");
            Call(presentation, "Update");
            Check(hud.gameObject.activeSelf && panel.gameObject.activeSelf && !legacyChat.enabled, "re-enabling restores the new interface and hides the legacy interface");
            Check(!host.activeInHierarchy && !chat.enabled, "business objects never became active during the lifecycle checks");
            Call(presentation, "OnDisable");
            Call(presentation, "OnDestroy");
            Object.DestroyImmediate(presentation);
            presentation = null;
            Check(hud == null && panel == null, "destroying the presentation cleans up both runtime canvases");
            Check(legacyChat.enabled && !legacyHistory.enabled, "teardown leaves original canvas states restored");
        }
        finally
        {
            if (presentation != null)
            {
                Call(presentation, "OnDisable");
                Call(presentation, "OnDestroy");
            }
            Object.DestroyImmediate(host);
            Object.DestroyImmediate(legacyChat.gameObject);
            Object.DestroyImmediate(legacyHistory.gameObject);
            Object.DestroyImmediate(unrelated.gameObject);
            Object.DestroyImmediate(cameraObject);
            Object.DestroyImmediate(eventObject);
        }
    }

    private static void MissingDependenciesLeaveLegacyVisible()
    {
        var legacy = LegacyCanvas("Fallback legacy view", true, true);
        var host = Inactive("Missing ChatSample");
        try
        {
            var view = host.AddComponent<CompanionPresentation>();
            Call(view, "Start");
            Check(!view.enabled && !Get<bool>(view, "built") && legacy.enabled, "missing ChatSample declines initialization without hiding legacy UI");
        }
        finally { Object.DestroyImmediate(host); }
        host = Inactive("Missing camera");
        try
        {
            var chat = host.AddComponent<ChatSample>(); chat.enabled = false;
            Set(chat, "m_ChatPanel", legacy.gameObject);
            var view = host.AddComponent<CompanionPresentation>();
            view.PrepareEditorLegacyViews();
            Check(!legacy.enabled && !legacy.GetComponent<GraphicRaycaster>().enabled,
                "missing-camera fixture begins with persisted editor suppression");
            Check(Camera.main == null, "missing-camera fixture has no fallback main camera");
            Call(view, "Start");
            Check(!view.enabled && !Get<bool>(view, "built") && legacy.enabled, "missing camera declines initialization without hiding legacy UI");
            Check(legacy.GetComponent<GraphicRaycaster>().enabled,
                "missing camera restores legacy input from the captured editor state");
        }
        finally { Object.DestroyImmediate(host); Object.DestroyImmediate(legacy.gameObject); }
    }

    private static void EditorPreviewAndSaveLifecycle()
    {
        const string fixtureScene = "Assets/CompanionRuntimeIntegrationFixture.unity";
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var cameraObject = Inactive("Preview camera");
        var camera = cameraObject.AddComponent<Camera>();
        var cameraController = cameraObject.AddComponent<PlayerCameraController>();
        cameraController.enabled = false;
        cameraController.InteractionBlocked = true;
        camera.transform.position = new Vector3(2, 3, 4);
        var font = AssetDatabase.LoadAssetAtPath<Font>(CompanionPresentationSetup.FontPath);
        Check(font != null, "bundled CJK interface font is available for the actual editor render");
        var previewObject = new GameObject("Direct editor visual") { hideFlags = HideFlags.HideAndDontSave };
        SceneManager.MoveGameObjectToScene(previewObject, cameraObject.scene);
        try
        {
            int eventSystems = Object.FindObjectsOfType<EventSystem>(true).Length;
            int xrComponents = Object.FindObjectsOfType<CompanionXRInteraction>(true).Length;
            int cameras = Object.FindObjectsOfType<Camera>(true).Length;
            var visual = previewObject.AddComponent<CompanionPresentation>();
            visual.ConfigureEditorScenePreview(font, camera);
            visual.TickEditorScenePreview();
            Check(Object.FindObjectsOfType<EventSystem>(true).Length == eventSystems, "editor preview creates no EventSystem");
            Check(Object.FindObjectsOfType<CompanionXRInteraction>(true).Length == xrComponents, "editor preview creates no XR interaction component");
            Check(Get<CompanionXRInteraction>(visual, "xr") == null && visual.GetComponent<CompanionXRInteraction>() == null,
                "hidden editor preview owner has no invisible XR interaction component");
            Check(Object.FindObjectsOfType<Camera>(true).Length == cameras, "editor preview creates no camera");
            Check(camera.transform.position == new Vector3(2, 3, 4) && cameraController.InteractionBlocked, "editor preview preserves camera pose and existing input-block state");
            var hud = Get<Canvas>(visual, "hudCanvas");
            var panel = Get<Canvas>(visual, "panelCanvas");
            Check(hud.gameObject.activeInHierarchy && hud.enabled && Get<GameObject>(visual, "desktopControls").activeInHierarchy, "editor preview displays the actual compact desktop controls");
            Check(Get<Text>(visual, "statusText").text == "编辑器预览", "editor preview is labeled rather than pretending a voice session is active");
            Check(!visual.IsPanelVisible && Get<CanvasGroup>(visual, "subtitles").alpha == 0, "idle editor preview keeps large panels and example subtitles hidden");
            bool excluded = true;
            foreach (var root in new[] { hud, panel })
                foreach (var child in root.GetComponentsInChildren<Transform>(true))
                    excluded &= (child.gameObject.hideFlags & HideFlags.DontSaveInEditor) != 0;
            Check(excluded, "every preview visual GameObject is excluded from editor scene serialization");
            CaptureEditorPreview(visual, camera, "companion-editor-compact.png");
            visual.SetPreviewPanel("text"); visual.TickEditorScenePreview();
            Check(visual.IsPanelVisible && Get<GameObject>(visual, "textPage").activeInHierarchy, "editor preview can show the text layout without a chat session");
            CaptureEditorPreview(visual, camera, "companion-editor-text.png");
            Call(visual, "DisposeEditorScenePreview");
            Object.DestroyImmediate(previewObject); previewObject = null;
            Check(hud == null && panel == null, "explicit editor visual disposal removes both generated canvases before owner destruction");
            Check(cameraController.InteractionBlocked, "editor preview cleanup preserves the camera input-block state");

            // This remains outside Play Mode: neither class executes in edit mode.
            // An active owner is essential here because it is the reload/save handoff
            // whose legacy rendering must remain suppressed after the visual lease ends.
            Check(!Application.isPlaying &&
                  !Attribute.IsDefined(typeof(ChatSample), typeof(ExecuteAlways), true) &&
                  !Attribute.IsDefined(typeof(ChatSample), typeof(ExecuteInEditMode), true),
                "active editor fixture cannot run ChatSample business lifecycle");
            var host = new GameObject("Offline serialized ChatAgent");
            var chat = host.AddComponent<ChatSample>(); chat.enabled = false;
            var owner = host.AddComponent<CompanionPresentation>();
            Set(owner, "interfaceFont", font); Set(owner, "playerCamera", camera);
            var legacyChat = LegacyCanvas("Serializable legacy chat", true, true);
            var legacyHistory = LegacyCanvas("Serializable legacy history", false, false);
            AddLegacyVisibilityProbe(legacyChat);
            Set(chat, "m_ChatPanel", legacyChat.gameObject); Set(chat, "m_HistoryPanel", legacyHistory.gameObject);
            CallSetup("CreatePreview", owner, chat, font);
            Check(!legacyChat.enabled && !legacyHistory.enabled, "Setup preview lease hides both real legacy canvases in edit mode");
            Check(!legacyChat.GetComponent<GraphicRaycaster>().enabled && !legacyHistory.GetComponent<GraphicRaycaster>().enabled,
                "editor preparation also suppresses legacy hit testing");
            Check(owner.isActiveAndEnabled && !Get<bool>(owner, "built"),
                "the serialized active owner has not run production Start during editor preview");
            Check(!owner.PrepareEditorLegacyViews() && !owner.PrepareEditorLegacyViews(),
                "repeated editor preparation is idempotent after capturing original states");
            Check(SceneCanvasCount() == 4, "Setup creates exactly two replacement canvases");
            Call(owner, "OnDisable");
            Check(owner.isActiveAndEnabled && !legacyChat.enabled && !legacyHistory.enabled &&
                  !legacyChat.GetComponent<GraphicRaycaster>().enabled,
                "a reload-style OnDisable on an enabled active owner never exposes legacy UI");
            CallSetup("ClearPreviews");
            Check(!legacyChat.enabled && !legacyHistory.enabled && !legacyChat.GetComponent<GraphicRaycaster>().enabled,
                "clearing preview leaves legacy rendering and hit testing suppressed before production Start");
            Check(SceneCanvasCount() == 2, "clearing editor preview leaves no generated canvas objects");
            CaptureLegacyHandoff(legacyChat, camera);
            owner.RestoreEditorLegacyViews();
            Check(legacyChat.enabled && !legacyHistory.enabled && legacyChat.GetComponent<GraphicRaycaster>().enabled &&
                  !legacyHistory.GetComponent<GraphicRaycaster>().enabled,
                "repeated preparation preserves each distinct original canvas and raycaster state");
            owner.PrepareEditorLegacyViews();
            CallSetup("CreatePreview", owner, chat, font);
            Check(EditorSceneManager.SaveScene(host.scene, fixtureScene), "offline fixture scene saves successfully with editor preview present");
            Check(!legacyChat.enabled && !legacyHistory.enabled && !legacyChat.GetComponent<GraphicRaycaster>().enabled,
                "scene-saving handoff keeps the active owner's legacy rendering and hit testing hidden");
            string saved = File.ReadAllText(fixtureScene);
            Check(!saved.Contains("Companion UI • Editor preview") && !saved.Contains("Companion • Subtitles") && !saved.Contains("Companion • Controls"), "saved scene contains no disposable preview owner or canvas roots");
            Check(!chat.enabled && chat.GetComponent<SubtitleOverlay>() == null && !Get<bool>(owner, "built"),
                "editor preview and saving never invoke business Awake or production Start");
            EditorSceneManager.OpenScene(fixtureScene, OpenSceneMode.Single);
            host = GameObject.Find("Offline serialized ChatAgent");
            owner = host.GetComponent<CompanionPresentation>();
            chat = host.GetComponent<ChatSample>();
            legacyChat = GameObject.Find("Serializable legacy chat").GetComponent<Canvas>();
            legacyHistory = GameObject.Find("Serializable legacy history").GetComponent<Canvas>();
            Check(!legacyChat.enabled && !legacyHistory.enabled &&
                  !legacyChat.GetComponent<GraphicRaycaster>().enabled && !legacyHistory.GetComponent<GraphicRaycaster>().enabled,
                "reopening the saved scene keeps old rendering and hit testing hidden before any preview or Start");
            Check(owner.isActiveAndEnabled && !Get<bool>(owner, "built") && !chat.enabled && chat.GetComponent<SubtitleOverlay>() == null,
                "reopened owner has not started and its ChatSample lifecycle remains uninvoked");
            Check(SceneCanvasCount() == 2, "reopened scene has no leaked preview canvases");
            owner.RestoreEditorLegacyViews();
            Check(legacyChat.enabled && !legacyHistory.enabled && legacyChat.GetComponent<GraphicRaycaster>().enabled &&
                  !legacyHistory.GetComponent<GraphicRaycaster>().enabled,
                "serialized fallback states survive scene reload rather than being replaced by hidden states");
            owner.PrepareEditorLegacyViews();
            CallSetup("CreatePreview", owner, chat, font);
            owner.enabled = false;
            CallSetup("ClearPreviews");
            Check(legacyChat.enabled && !legacyHistory.enabled && legacyChat.GetComponent<GraphicRaycaster>().enabled &&
                  !legacyHistory.GetComponent<GraphicRaycaster>().enabled,
                "explicitly disabling the serialized owner restores the captured legacy UI and input");
            Check(EditorSceneManager.SaveScene(host.scene, fixtureScene), "disabled-owner fallback scene saves successfully");
            EditorSceneManager.OpenScene(fixtureScene, OpenSceneMode.Single);
            host = GameObject.Find("Offline serialized ChatAgent");
            owner = host.GetComponent<CompanionPresentation>();
            chat = host.GetComponent<ChatSample>();
            legacyChat = GameObject.Find("Serializable legacy chat").GetComponent<Canvas>();
            legacyHistory = GameObject.Find("Serializable legacy history").GetComponent<Canvas>();
            Check(!owner.enabled && legacyChat.enabled && !legacyHistory.enabled &&
                  legacyChat.GetComponent<GraphicRaycaster>().enabled && !legacyHistory.GetComponent<GraphicRaycaster>().enabled,
                "disabled-owner fallback remains usable after saving and reopening the scene");
            owner.enabled = true;
            owner.PrepareEditorLegacyViews();
            Check(!legacyChat.enabled && !legacyHistory.enabled && !legacyChat.GetComponent<GraphicRaycaster>().enabled,
                "re-enabling editor ownership suppresses legacy UI again without running production Start");
            CallSetup("CreatePreview", owner, chat, font);
            host.SetActive(false);
            CallSetup("ClearPreviews");
            Check(legacyChat.enabled && !legacyHistory.enabled && legacyChat.GetComponent<GraphicRaycaster>().enabled,
                "an explicitly inactive owner also returns the captured legacy fallback state");
        }
        finally
        {
            CallSetup("ClearPreviews");
            if (previewObject != null)
            {
                var visual = previewObject.GetComponent<CompanionPresentation>();
                if (visual != null) Call(visual, "DisposeEditorScenePreview");
                Object.DestroyImmediate(previewObject);
            }
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(fixtureScene);
        }
    }

    private static void CallSetup(string name, params object[] values)
    {
        try { typeof(CompanionPresentationSetup).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, values); }
        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
    }

    private static void AddLegacyVisibilityProbe(Canvas canvas)
    {
        var probe = new GameObject("Legacy visibility probe", typeof(RectTransform), typeof(Image));
        var rect = probe.GetComponent<RectTransform>();
        rect.SetParent(canvas.transform, false);
        rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
        rect.sizeDelta = new Vector2(360, 100);
        probe.GetComponent<Image>().color = Color.magenta;
    }

    private static void CaptureLegacyHandoff(Canvas legacy, Camera camera)
    {
        Check(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null,
            "legacy handoff verification has an actual graphics backend");
        var target = new RenderTexture(800, 450, 24, RenderTextureFormat.ARGB32);
        var priorTarget = camera.targetTexture;
        var priorActive = RenderTexture.active;
        var priorMode = legacy.renderMode;
        var priorCamera = legacy.worldCamera;
        bool priorEnabled = legacy.enabled;
        Texture2D image = null;
        try
        {
            legacy.renderMode = RenderMode.ScreenSpaceCamera;
            legacy.worldCamera = camera;
            legacy.planeDistance = 2;
            target.Create(); camera.targetTexture = target;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            Canvas.ForceUpdateCanvases(); camera.Render(); RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
            int hiddenPixels = CountLegacyProbePixels(image.GetPixels32());
            Directory.CreateDirectory("Logs");
            File.WriteAllBytes("Logs/companion-legacy-handoff-hidden.png", image.EncodeToPNG());
            // Positive control proves the same retained legacy objects could visibly flash
            // if a reload/preview disposer restored their Canvas.enabled flag.
            legacy.enabled = true;
            Canvas.ForceUpdateCanvases(); camera.Render();
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
            int visiblePixels = CountLegacyProbePixels(image.GetPixels32());
            Check(visiblePixels > 1000, "legacy probe is visibly rendered when the positive control enables its canvas");
            Check(hiddenPixels == 0, "the actual no-preview/pre-Start handoff frame renders zero legacy probe pixels");
        }
        finally
        {
            legacy.enabled = priorEnabled; legacy.renderMode = priorMode; legacy.worldCamera = priorCamera;
            camera.targetTexture = priorTarget; RenderTexture.active = priorActive;
            if (image != null) Object.DestroyImmediate(image);
            target.Release(); Object.DestroyImmediate(target);
        }
    }

    private static int CountLegacyProbePixels(Color32[] pixels)
    {
        int count = 0;
        foreach (var pixel in pixels)
            if (pixel.r > 180 && pixel.b > 180 && pixel.g < 80) count++;
        return count;
    }

    private static int SceneCanvasCount()
    {
        int count = 0;
        foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
            if (canvas.gameObject.scene == SceneManager.GetActiveScene() ||
                (!EditorUtility.IsPersistent(canvas) && canvas.name.StartsWith("Companion •", StringComparison.Ordinal))) count++;
        return count;
    }

    private static void CaptureEditorPreview(CompanionPresentation visual, Camera camera, string fileName)
    {
        Check(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null, "editor preview verification has an actual graphics backend");
        var target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
        var previousTarget = camera.targetTexture;
        var previousActive = RenderTexture.active;
        Texture2D image = null;
        var roots = new[] { Get<Canvas>(visual, "hudCanvas"), Get<Canvas>(visual, "panelCanvas") };
        try
        {
            foreach (var canvas in roots)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 2;
            }
            camera.orthographic = true;
            camera.orthographicSize = 5;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.045f, .06f, .065f, 1);
            target.Create(); camera.targetTexture = target;
            Canvas.ForceUpdateCanvases();
            visual.TickEditorScenePreview();
            Canvas.ForceUpdateCanvases();
            camera.Render();
            RenderTexture.active = target;
            image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
            var pixels = image.GetPixels32();
            Color32 background = pixels[(image.height - 100) * image.width + image.width / 2];
            int visiblePixels = 0;
            foreach (var pixel in pixels) if (PixelDiffers(pixel, background)) visiblePixels++;
            Check(visiblePixels > (visual.IsPanelVisible ? 10000 : 1000), "rendered editor " + (visual.IsPanelVisible ? "panel" : "HUD") + " produces substantial visible pixels");
            var statusRect = Get<Text>(visual, "statusText").transform.parent.GetComponent<RectTransform>();
            Vector3[] corners = new Vector3[4]; statusRect.GetWorldCorners(corners);
            var lower = camera.WorldToScreenPoint(corners[0]); var upper = camera.WorldToScreenPoint(corners[2]);
            int statusPixels = 0;
            for (int y = Mathf.Clamp(Mathf.CeilToInt(lower.y), 0, image.height - 1); y < Mathf.Clamp(Mathf.FloorToInt(upper.y), 0, image.height); y++)
                for (int x = Mathf.Clamp(Mathf.CeilToInt(lower.x), 0, image.width - 1); x < Mathf.Clamp(Mathf.FloorToInt(upper.x), 0, image.width); x++)
                    if (PixelDiffers(pixels[y * image.width + x], background)) statusPixels++;
            Check(statusPixels > 200, "editor status button and icon are actually drawn despite HideAndDontSave");
            Directory.CreateDirectory("Logs");
            File.WriteAllBytes(Path.Combine("Logs", fileName), image.EncodeToPNG());
        }
        finally
        {
            foreach (var canvas in roots) if (canvas != null) canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            camera.targetTexture = previousTarget; RenderTexture.active = previousActive;
            if (image != null) Object.DestroyImmediate(image);
            target.Release(); Object.DestroyImmediate(target);
        }
    }

    private static bool PixelDiffers(Color32 pixel, Color32 background)
    {
        return Mathf.Abs(pixel.r - background.r) + Mathf.Abs(pixel.g - background.g) + Mathf.Abs(pixel.b - background.b) > 24;
    }

    private static Canvas LegacyCanvas(string name, bool visible, bool raycasts)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.enabled = visible;
        go.GetComponent<GraphicRaycaster>().enabled = raycasts;
        return canvas;
    }

    private static Text TextView(string name, Transform parent, string value)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var text = go.GetComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.text = value;
        return text;
    }

    private static GameObject Inactive(string name) { var go = new GameObject(name); go.SetActive(false); return go; }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, Members).SetValue(target, value); }
    private static T Get<T>(object target, string name) { return (T)target.GetType().GetField(name, Members).GetValue(target); }
    private static void Call(object target, string name, params object[] values)
    {
        try { target.GetType().GetMethod(name, Members).Invoke(target, values); }
        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Checks.Add(name);
    }
}
