using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>
/// Tests background subtitle decisions and the production ChatSample -> presentation
/// -> desktop bridge without creating native windows or starting business services.
/// </summary>
public static class CompanionDesktopSubtitlesRegression
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

    private sealed class WindowProbe : IDesktopSubtitleWindow
    {
        public bool background = true;
        public bool visible;
        public string original, translation, speaker;
        public int fontSize, presents, hides, disposals;
        public float opacity;
        public bool failPresent, failDispose;
        public bool IsUnityInBackground { get { return background; } }
        public void Present(string source, string translated, int size, float alpha, string speaker = "")
        {
            if (disposals != 0) throw new InvalidOperationException("Presentation after native window disposal.");
            if (failPresent) throw new InvalidOperationException("Simulated native presentation failure.");
            visible = true; original = source; translation = translated; this.speaker = speaker;
            fontSize = size; opacity = alpha; presents++;
        }
        public void Hide() { visible = false; hides++; }
        public void Dispose()
        {
            visible = false; disposals++;
            if (failDispose) throw new InvalidOperationException("Simulated native disposal failure.");
        }
    }

    public static void RunBatch()
    {
        RunChecks(false);
    }

    public static void RunNameBatch()
    {
        RunChecks(true);
    }

    private static void RunChecks(bool namesOnly)
    {
        string path = Application.dataPath.Replace('\\', '/');
        if (!Application.isBatchMode || !path.Contains("/Server/ARDY/runtime/unity-naturalness-validation/"))
            throw new InvalidOperationException("Run only in the isolated naturalness validation project.");
        var report = new Report
        {
            scope = "Fake native window boundary plus real inactive ChatSample, RTSpeechHandler, SubtitleOverlay and production CompanionPresentation Start/Update. Checks separate speaker headers, live model identity, independent desktop assistant/ASR windows, streaming revisions, background gating, current pages, opacity, VR suppression and disposal. Renders real Unity UI glyphs with a graphics backend. No microphone, network, business Awake/Start, real desktop window or user scene execution.",
            unityVersion = Application.unityVersion
        };
        try
        {
            Checks.Clear();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!namesOnly)
            {
                DriverVisibilityAndLifetime();
                IndependentAssistantAndRecognitionWindows();
                ProductionPresentationBridge();
            }
            ProductionSpeakerAndRecognitionBridge();
            report.passed = true;
        }
        catch (Exception error)
        {
            report.error = error.ToString();
            Debug.LogException(error);
        }
        finally { CompanionDesktopSubtitles.DisposeAll(); }
        report.checks = Checks.ToArray();
        Directory.CreateDirectory("Logs");
        File.WriteAllText(namesOnly ? "Logs/companion-character-name-integration.json" : "Logs/companion-desktop-subtitles-integration.json", JsonUtility.ToJson(report, true));
        Debug.Log((namesOnly ? "COMPANION_CHARACTER_NAME_" : "COMPANION_DESKTOP_SUBTITLES_") + (report.passed ? "OK " : "FAILED ") + Checks.Count);
        EditorApplication.Exit(report.passed ? 0 : 1);
    }

    private static void DriverVisibilityAndLifetime()
    {
        var window = new WindowProbe();
        var driver = new CompanionDesktopSubtitles(window);
        try
        {
            driver.UpdateFrame(true, false, "今日は", "", 28, 1);
            Check(window.visible && window.original == "今日は", "background original subtitles become visible");
            driver.UpdateFrame(true, false, "今日はいい天気ですね。", "今天天气不错。", 30, .65f);
            Check(window.original == "今日はいい天気ですね。" && window.translation == "今天天气不错。",
                "streamed source revisions and bilingual translation replace the desktop contents");
            Check(window.fontSize == 30 && Mathf.Approximately(window.opacity, .65f),
                "the subtitle size and live fade opacity reach the native boundary");
            window.background = false;
            driver.UpdateFrame(true, false, "source", "translation", 28, 1);
            Check(!window.visible, "returning to Unity hides the desktop overlay");
            window.background = true;
            driver.UpdateFrame(true, false, "source", "translation", 28, 1);
            Check(window.visible, "leaving Unity again resumes the same current subtitle");
            driver.UpdateFrame(true, true, "source", "translation", 28, 1);
            Check(!window.visible, "VR presentation suppresses the desktop overlay");
            driver.UpdateFrame(true, false, "source", "translation", 28, 1);
            driver.UpdateFrame(false, false, "source", "translation", 28, 1);
            Check(!window.visible, "the user's desktop subtitle opt-out immediately hides the overlay");
            foreach (var pair in new[] { new[] { "", "" }, new[] { " \r\n", "\t " }, new string[] { null, null } })
            {
                driver.UpdateFrame(true, false, "source", "", 28, 1);
                driver.UpdateFrame(true, false, pair[0], pair[1], 28, 1);
                Check(!window.visible, "empty, whitespace and null subtitle pairs never leave an empty desktop plate: " + Checks.Count);
            }
            driver.UpdateFrame(true, false, "source", "", 28, 1);
            driver.UpdateFrame(true, false, "source", "", 28, 0);
            Check(!window.visible, "an expired fully transparent subtitle hides the window");
            driver.UpdateFrame(true, false, "", "只显示翻译。", 28, 1);
            Check(window.visible && string.IsNullOrEmpty(window.original) && window.translation == "只显示翻译。",
                "translation-only subtitles can be shown without original text");
            driver.Dispose(); driver.Dispose();
            Check(!window.visible && window.disposals == 1, "repeated driver disposal closes its native resource exactly once");
            int presents = window.presents;
            driver.UpdateFrame(true, false, "late frame", "", 28, 1);
            Check(!window.visible && window.presents == presents, "a late frame cannot resurrect a disposed overlay");
        }
        finally { driver.Dispose(); }

        var first = new WindowProbe(); var second = new WindowProbe();
        var a = new CompanionDesktopSubtitles(first); var b = new CompanionDesktopSubtitles(second);
        a.UpdateFrame(true, false, "one", "", 28, 1);
        b.UpdateFrame(true, false, "two", "", 28, 1);
        CompanionDesktopSubtitles.DisposeAll(); CompanionDesktopSubtitles.DisposeAll();
        Check(first.disposals == 1 && second.disposals == 1 && !first.visible && !second.visible,
            "Play exit/domain reload cleanup closes every active overlay and is idempotent");
        var next = new WindowProbe();
        using (var replacement = new CompanionDesktopSubtitles(next))
        {
            replacement.UpdateFrame(true, false, "new play session", "", 28, 1);
            Check(next.visible, "a new Play session can create a fresh driver after global cleanup");
        }
        var broken = new WindowProbe { failDispose = true }; var healthy = new WindowProbe();
        new CompanionDesktopSubtitles(broken); new CompanionDesktopSubtitles(healthy);
        bool reported = false;
        try { CompanionDesktopSubtitles.DisposeAll(); }
        catch (AggregateException error) { reported = error.InnerExceptions.Count == 1; }
        Check(reported && broken.disposals == 1 && healthy.disposals == 1,
            "a failed native disposal is reported only after every other overlay has been cleaned up");
        CompanionDesktopSubtitles.DisposeAll();
        Check(broken.disposals == 1 && healthy.disposals == 1,
            "global cleanup does not retain or repeatedly dispose a failed native handle");
    }

    private static void IndependentAssistantAndRecognitionWindows()
    {
        var assistant = new WindowProbe();
        var recognition = new WindowProbe();
        var driver = new CompanionDesktopSubtitles(assistant, recognition);
        try
        {
            driver.UpdateFrame(true, false, "彼女の返事。", "她的回答。", 30, .65f, "Hiyori", "您正在说话", .35f);
            Check(assistant.visible && recognition.visible && assistant.speaker == "Hiyori" && recognition.speaker == "您",
                "assistant and recognition appear in separate windows with their own speaker labels");
            Check(assistant.original == "彼女の返事。" && assistant.translation == "她的回答。" &&
                  recognition.original == "您正在说话" && recognition.translation == "",
                "speaker labels remain outside both subtitle bodies and ASR does not enter the translation channel");
            Check(assistant.fontSize == 30 && recognition.fontSize == 17 &&
                  Mathf.Approximately(assistant.opacity, .65f) && Mathf.Approximately(recognition.opacity, .35f),
                "desktop assistant and recognition preserve independent font sizes and fade alpha");
            driver.UpdateFrame(true, false, "彼女の返事。", "她的回答。", 30, .65f, "Shirazu", "您正在说话", .35f);
            Check(assistant.speaker == "Shirazu" && assistant.original == "彼女の返事。" && recognition.speaker == "您",
                "changing the current character updates only the assistant header even when the body is unchanged");
            foreach (string partial in new[] { "你好", "你好。", "Hello.", "修订后的整句话。" })
            {
                driver.UpdateFrame(true, false, "", "", 28, 0, "Shirazu", partial, 1);
                Check(!assistant.visible && recognition.visible && recognition.original == partial && recognition.speaker == "您",
                    "live recognition renders independently without assistant subtitles: " + partial);
            }
            driver.UpdateFrame(true, false, "仍在回答", "", 28, 1, "Shirazu", "已识别", 0);
            Check(assistant.visible && !recognition.visible, "recognition expiry leaves active assistant subtitles visible");
            driver.UpdateFrame(true, false, "", "", 28, 0, "Shirazu", "仍在听", 1);
            Check(!assistant.visible && recognition.visible, "assistant expiry does not hide ongoing recognition");
            driver.UpdateFrame(true, false, "回答", "", 28, float.NaN, "Shirazu", "仍在听", 1);
            Check(!assistant.visible && recognition.visible, "invalid assistant opacity does not suppress valid recognition");
            driver.UpdateFrame(true, false, "回答", "", 28, 1, "Shirazu", "仍在听", float.NaN);
            Check(assistant.visible && !recognition.visible, "invalid recognition opacity does not suppress valid assistant subtitles");
            driver.UpdateFrame(true, true, "回答", "", 28, 1, "Shirazu", "仍在听", 1);
            Check(!assistant.visible && !recognition.visible, "VR presentation hides both desktop windows together");
            driver.UpdateFrame(true, false, "回答", "", 28, 1, "Shirazu", "仍在听", 1);
            assistant.background = recognition.background = false;
            driver.UpdateFrame(true, false, "回答", "", 28, 1, "Shirazu", "仍在听", 1);
            Check(!assistant.visible && !recognition.visible, "returning to the Unity foreground hides both windows");
            assistant.background = recognition.background = true;
            driver.UpdateFrame(false, false, "回答", "", 28, 1, "Shirazu", "仍在听", 1);
            Check(!assistant.visible && !recognition.visible, "the desktop subtitle preference disables both windows");
            driver.UpdateFrame(true, false, "回答", "", 28, 1, "Shirazu", "  \r\n", 1);
            Check(assistant.visible && !recognition.visible, "blank recognition never leaves an empty user-labelled window");
            driver.Dispose(); driver.Dispose();
            Check(assistant.disposals == 1 && recognition.disposals == 1 && !assistant.visible && !recognition.visible,
                "disposing a dual-window driver closes each native window exactly once");
        }
        finally { driver.Dispose(); }

        var broken = new WindowProbe { failDispose = true }; var healthy = new WindowProbe();
        var failureDriver = new CompanionDesktopSubtitles(broken, healthy);
        bool reported = false;
        try { failureDriver.Dispose(); }
        catch (Exception) { reported = true; }
        Check(reported && broken.disposals == 1 && healthy.disposals == 1,
            "a failed assistant-window disposal does not skip recognition-window cleanup");
    }

    private static void ProductionPresentationBridge()
    {
        var host = new GameObject("Offline desktop subtitle chat"); host.SetActive(false);
        var legacy = new GameObject("Retained subtitle data", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        var cameraObject = new GameObject("Desktop subtitle fixture camera", typeof(Camera));
        var eventObject = new GameObject("Desktop subtitle fixture EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        CompanionPresentation view = null;
        var window = new WindowProbe();
        var driver = new CompanionDesktopSubtitles(window);
        const string preference = "NeEEvA.UI.DesktopSubtitles";
        bool hadPreference = PlayerPrefs.HasKey(preference);
        int previousPreference = PlayerPrefs.GetInt(preference, 1);
        try
        {
            var chat = host.AddComponent<ChatSample>(); chat.enabled = false;
            var overlay = host.AddComponent<SubtitleOverlay>(); overlay.enabled = false;
            var source = TextView("Actual assistant original", legacy.transform, "今日も会えてうれしいです。");
            var translated = TextView("Actual assistant translation", legacy.transform, "今天也很高兴见到你。");
            Set(overlay, "m_OriginalText", source); Set(overlay, "m_OriginalContainer", source.gameObject);
            Set(overlay, "m_TranslationText", translated); Set(overlay, "m_DisplayMode", SubtitleDisplayMode.Bilingual);
            Set(overlay, "m_PersistSettings", false);
            Set(chat, "m_TextBack", source); Set(chat, "m_SubtitleOverlay", overlay);
            Set(chat, "m_ChatPanel", legacy);
            view = host.AddComponent<CompanionPresentation>();
            Set(view, "interfaceFont", AssetDatabase.LoadAssetAtPath<Font>(CompanionPresentationSetup.FontPath));
            Set(view, "playerCamera", cameraObject.GetComponent<Camera>());
            Call(view, "Start");
            CaptureSettings(view, cameraObject.GetComponent<Camera>());
            Check(Get<bool>(view, "built") && !Get<bool>(view, "preview"), "fixture uses the production presentation and actual ChatSample channels");
            Check(Get<object>(view, "desktopSubtitles") == null, "batch/editor initialization does not create a native desktop window");
            Set(view, "desktopSubtitles", driver); Set(view, "desktopSubtitlesEnabled", true);
            Get<CanvasGroup>(view, "subtitles").alpha = 1;
            Call(view, "Update");
            Check(window.visible && window.original == Get<Text>(view, "originalText").text &&
                  window.translation == Get<Text>(view, "translationText").text,
                "production Update forwards the same original and translation currently drawn by the Unity HUD");
            Check(window.original.Length > 0 && window.translation.Length > 0,
                "the bridge retains both live assistant subtitle channels");

            source.text = "第一句已经说过。第二句也已经说过。这是当前正在说的新句子，桌面应与游戏窗口使用相同的分页内容。";
            translated.text = "The new sentence is now being spoken.";
            Call(view, "Update");
            Check(window.original == Get<Text>(view, "originalText").text && window.original != source.text,
                "desktop forwarding reuses the current sentence page rather than dumping the full response history");
            Check(window.translation == Get<Text>(view, "translationText").text,
                "a revised translation reaches the desktop in the same frame as the HUD");
            Get<CanvasGroup>(view, "subtitles").alpha = .37f;
            Call(view, "UpdateDesktopSubtitles");
            Check(Mathf.Approximately(window.opacity, .37f), "desktop forwarding uses the live HUD fade alpha");
            Get<CanvasGroup>(view, "subtitles").alpha = 1;
            source.gameObject.SetActive(false);
            Call(view, "Update");
            Check(window.visible && string.IsNullOrEmpty(window.original) && window.translation.Length > 0,
                "translation-only mode never leaks the hidden original channel to the desktop");
            source.gameObject.SetActive(true); translated.gameObject.SetActive(false);
            Call(view, "Update");
            Check(window.visible && window.original.Length > 0 && string.IsNullOrEmpty(window.translation),
                "original-only mode never leaks the hidden translation channel to the desktop");
            source.gameObject.SetActive(false);
            Call(view, "Update");
            Check(!window.visible, "hiding both subtitle channels also hides the desktop window");
            source.gameObject.SetActive(true); translated.gameObject.SetActive(true);
            Call(view, "Update");
            Set(view, "vrActive", true); Call(view, "UpdateDesktopSubtitles");
            Check(!window.visible, "the production bridge propagates VR mode to desktop suppression");
            Set(view, "vrActive", false); Set(view, "desktopSubtitlesEnabled", false);
            Call(view, "UpdateDesktopSubtitles");
            Check(!window.visible, "the production bridge propagates the user's preference switch");
            Set(view, "desktopSubtitlesEnabled", true); Call(view, "UpdateDesktopSubtitles");
            Get<CanvasGroup>(view, "subtitles").alpha = 0; Call(view, "UpdateDesktopSubtitles");
            Check(!window.visible, "subtitle expiry removes the desktop overlay through the production bridge");
            Check(!host.activeInHierarchy && !chat.enabled && !overlay.enabled && !chat.IsAISpeaking && !chat.HasPendingConversationWork,
                "subtitle mirroring does not activate business objects, microphone capture or new assistant work");
            window.failPresent = true; Get<CanvasGroup>(view, "subtitles").alpha = 1;
            Call(view, "UpdateDesktopSubtitles");
            Check(window.disposals == 1 && !window.visible && Get<object>(view, "desktopSubtitles") == null,
                "a native presentation failure closes and detaches the failed desktop window");
            Check(Get<bool>(view, "desktopSubtitlesFailed") && Get<bool>(view, "desktopSubtitlesEnabled") &&
                  Get<Text>(view, "desktopSubtitlesLabel").text.Contains("重试"),
                "a native failure preserves the user's opt-in and offers an explicit retry");
            Call(view, "ToggleDesktopSubtitles");
            Check(!Get<bool>(view, "desktopSubtitlesFailed") && Get<bool>(view, "desktopSubtitlesEnabled") && PlayerPrefs.GetInt(preference) == 1,
                "retry clears the failure flag while keeping desktop subtitles enabled");
            window = new WindowProbe(); driver = new CompanionDesktopSubtitles(window);
            Set(view, "desktopSubtitles", driver); Call(view, "UpdateDesktopSubtitles");
            Check(window.visible, "the production bridge can show subtitles with a fresh window after retry");
            CompanionDesktopSubtitles.DisposeAll(); Call(view, "UpdateDesktopSubtitles");
            Check(driver.IsDisposed && window.disposals == 1 && Get<object>(view, "desktopSubtitles") == null,
                "global reload cleanup removes the owner's stale driver without creating a native window in batch mode");
            window = new WindowProbe(); driver = new CompanionDesktopSubtitles(window);
            Set(view, "desktopSubtitles", driver); Call(view, "UpdateDesktopSubtitles");
            Check(window.visible, "a retained presentation can use a fresh driver after domain-reload cleanup");
            Call(view, "OnDisable");
            Check(!window.visible, "disabling the presentation leaves no visible desktop overlay");
            Call(view, "OnDestroy"); view = null;
            Check(window.disposals == 1, "presentation destruction disposes its desktop driver exactly once");
        }
        finally
        {
            if (view != null) { Call(view, "OnDisable"); Call(view, "OnDestroy"); }
            driver.Dispose();
            if (hadPreference) PlayerPrefs.SetInt(preference, previousPreference); else PlayerPrefs.DeleteKey(preference);
            PlayerPrefs.Save();
            Object.DestroyImmediate(host); Object.DestroyImmediate(legacy);
            Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(eventObject);
        }
    }

    private static void ProductionSpeakerAndRecognitionBridge()
    {
        var host = new GameObject("Offline speaker identity and streaming chat"); host.SetActive(false);
        var avatar = new GameObject("Hiyori"); avatar.SetActive(false);
        var replacementAvatar = new GameObject("Shirazu"); replacementAvatar.SetActive(false);
        var sound = new GameObject("Inactive lip-sync audio binding"); sound.SetActive(false);
        UniVRM10.VRM10Object avatarMetadata = null, replacementMetadata = null;
        var legacy = new GameObject("Speaker fixture data", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        var cameraObject = new GameObject("Speaker fixture camera", typeof(Camera));
        var eventObject = new GameObject("Speaker fixture EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        CompanionPresentation view = null;
        var assistantWindow = new WindowProbe(); var recognitionWindow = new WindowProbe();
        var driver = new CompanionDesktopSubtitles(assistantWindow, recognitionWindow);
        try
        {
            var chat = host.AddComponent<ChatSample>(); chat.enabled = false;
            var speech = host.AddComponent<RTSpeechHandler>(); speech.enabled = false;
            var overlay = host.AddComponent<SubtitleOverlay>(); overlay.enabled = false;
            Set(speech, "m_ChatSample", chat); Set(speech, "m_IsRecording", true); Set(speech, "m_AwakeState", true);
            Set(chat, "m_PresentationRealtime", speech); Set(chat, "m_ShowStreamingTranscript", true);
            Set(chat, "m_EnableSpeculativeListening", true); Set(chat, "m_ChatSettings", null);
            Set(chat, "m_Animator", avatar.AddComponent<Animator>());
            var source = TextView("Speaker source", legacy.transform, "今日も会えてうれしいです。");
            var translated = TextView("Speaker translation", legacy.transform, "今天也很高兴见到你。");
            var recordTips = TextView("Recognition source", legacy.transform, "");
            Set(overlay, "m_OriginalText", source); Set(overlay, "m_OriginalContainer", source.gameObject);
            Set(overlay, "m_TranslationText", translated); Set(overlay, "m_DisplayMode", SubtitleDisplayMode.Bilingual);
            Set(overlay, "m_PersistSettings", false);
            Set(chat, "m_TextBack", source); Set(chat, "m_SubtitleOverlay", overlay);
            Set(chat, "m_RecordTips", recordTips); Set(chat, "m_ChatPanel", legacy);
            view = host.AddComponent<CompanionPresentation>();
            Set(view, "interfaceFont", AssetDatabase.LoadAssetAtPath<Font>(CompanionPresentationSetup.FontPath));
            Set(view, "playerCamera", cameraObject.GetComponent<Camera>());
            Call(view, "Start");
            Set(view, "desktopSubtitles", driver); Set(view, "desktopSubtitlesEnabled", true);
            Get<CanvasGroup>(view, "subtitles").alpha = 1;
            chat.UpdateStreamingTranscript(new SenseVoiceSpeechToText.StreamingTranscript
                { Text = "现在正在说话，识别会持续更新。", AudioMs = 850, Revision = false });
            Call(view, "Update"); Canvas.ForceUpdateCanvases(); Call(view, "Update");
            Check(chat.PresentationCharacterName == "Hiyori" && Get<Text>(view, "speakerText").text == "Hiyori" && assistantWindow.speaker == "Hiyori",
                "the current avatar name reaches both the separate Unity speaker header and desktop window");
            Check(Get<Text>(view, "transcriptSpeakerText").text == "您" && recognitionWindow.speaker == "您",
                "the recognition speaker is labelled 您 in both Unity and the desktop");
            Check(recognitionWindow.visible && recognitionWindow.original == "现在正在说话，识别会持续更新。" &&
                  Get<Text>(view, "transcriptText").text == recognitionWindow.original,
                "the real live ASR partial reaches its independent desktop window in the same update");
            string body = Get<Text>(view, "originalText").text;
            Check(body == "今日も会えてうれしいです。" && assistantWindow.original == body,
                "the character label is never prepended to the assistant subtitle body");
            Set(chat, "m_Animator", replacementAvatar.AddComponent<Animator>());
            Call(view, "Update");
            Check(chat.PresentationCharacterName == "Shirazu" && Get<Text>(view, "speakerText").text == "Shirazu" &&
                  assistantWindow.speaker == "Shirazu" && assistantWindow.original == body,
                "switching the live avatar immediately updates both speaker headers without changing the body");
            var avatarVrm = avatar.AddComponent<UniVRM10.Vrm10Instance>(); avatarVrm.enabled = false;
            var replacementVrm = replacementAvatar.AddComponent<UniVRM10.Vrm10Instance>(); replacementVrm.enabled = false;
            avatarMetadata = ScriptableObject.CreateInstance<UniVRM10.VRM10Object>(); avatarMetadata.Meta.Name = "NEVA";
            replacementMetadata = ScriptableObject.CreateInstance<UniVRM10.VRM10Object>(); replacementMetadata.Meta.Name = "另一个角色";
            avatarVrm.Vrm = avatarMetadata; replacementVrm.Vrm = replacementMetadata;
            var mesh = new GameObject("Lip-sync mesh"); mesh.transform.SetParent(avatar.transform, false);
            var replacementMesh = new GameObject("Replacement lip-sync mesh"); replacementMesh.transform.SetParent(replacementAvatar.transform, false);
            var audio = sound.AddComponent<AudioSource>();
            var lips = sound.AddComponent<Audio2LipScript>(); lips.enabled = false;
            lips.meshRenderer = mesh.AddComponent<SkinnedMeshRenderer>();
            Set(chat, "m_Animator", null); Set(chat, "m_AudioSource", audio);
            Call(view, "Update");
            Check(chat.PresentationCharacterName == "Hiyori" && Get<Text>(view, "speakerText").text == "Hiyori" && assistantWindow.speaker == "Hiyori",
                "a non-asset lip-sync model uses its hierarchy root name before embedded VRM metadata");
            avatarMetadata.Meta.Name = "元数据里的新名字"; Call(view, "Update");
            Check(assistantWindow.speaker == "Hiyori" && Get<Text>(view, "speakerText").text == assistantWindow.speaker && assistantWindow.original == body,
                "editing embedded metadata cannot replace the visible non-asset hierarchy name");
            avatar.name = "回退角色(Clone)"; Call(view, "Update");
            Check(assistantWindow.speaker == "回退角色", "a non-asset hierarchy rename updates immediately and removes the Clone suffix");
            avatar.name = ""; Call(view, "Update");
            Check(assistantWindow.speaker == "元数据里的新名字", "metadata remains the last fallback for an unnamed non-asset model");
            lips.meshRenderer = replacementMesh.AddComponent<SkinnedMeshRenderer>(); Call(view, "Update");
            Check(assistantWindow.speaker == "Shirazu" && Get<Text>(view, "speakerText").text == "Shirazu" && assistantWindow.original == body,
                "replacing the non-asset lip-sync mesh immediately switches to the new hierarchy identity");
            avatar.name = "NEVA"; avatarMetadata.Meta.Name = "NEVA";
            lips.meshRenderer = mesh.GetComponent<SkinnedMeshRenderer>(); Call(view, "Update");
            ProjectAssetRenameAndRuntimeBake(chat, view, assistantWindow, lips, body);
            lips.meshRenderer = mesh.GetComponent<SkinnedMeshRenderer>(); Call(view, "Update");
            CaptureSpeakerHeaders(view, cameraObject.GetComponent<Camera>());

            source.text = ""; translated.text = "";
            int audioMs = 1700;
            foreach (string partial in new[] { "第一版", "第一版。", "Actually, corrected.",
                "这是一段正在流式识别的长句。它会持续增长，超出识别窗口后仍然保留完整转写并显示最新末尾，桌面悬浮字幕应同步更新，不能等到停止录音才出现。",
                "最后修订的完整句子。" })
            {
                chat.UpdateStreamingTranscript(new SenseVoiceSpeechToText.StreamingTranscript
                    { Text = partial, AudioMs = audioMs, Revision = true });
                audioMs += 850;
                Call(view, "Update");
                Check(!assistantWindow.visible && recognitionWindow.visible && recognitionWindow.original == partial,
                    "production streaming recognition remains visible with no assistant text: " + partial);
            }
            recordTips.text = "";
            Set(view, "transcriptChangedAt", Time.unscaledTime - 12f); Call(view, "Update");
            Check(recognitionWindow.visible && recognitionWindow.original == "最后修订的完整句子。" && chat.PresentationIsTranscribing,
                "desktop ASR survives a stale label expiry and long unchanged partial while recording continues");
            Set(speech, "m_IsRecording", false); Set(chat, "m_StreamingTranscript", ""); recordTips.text = "最终结果。";
            Call(view, "Update");
            Check(recognitionWindow.visible && recognitionWindow.original == "最终结果。",
                "the final recognition result replaces the streaming partial in the independent desktop window");
            Set(view, "transcriptChangedAt", Time.unscaledTime - 12f); Call(view, "Update");
            Check(!assistantWindow.visible && !recognitionWindow.visible, "the production desktop ASR window closes after its normal final-result linger");
            Check(!host.activeInHierarchy && !chat.enabled && !speech.enabled && !overlay.enabled &&
                  !sound.activeInHierarchy && !lips.enabled && Get<uint>(lips, "Context") == 0 &&
                  !chat.IsAISpeaking && !chat.HasPendingConversationWork,
                "speaker and streaming mirroring leave all business and microphone components inactive");
            Call(view, "OnDisable"); Call(view, "OnDestroy"); view = null;
            Check(assistantWindow.disposals == 1 && recognitionWindow.disposals == 1,
                "production presentation cleanup closes both assistant and ASR native boundaries exactly once");
        }
        finally
        {
            if (view != null) { Call(view, "OnDisable"); Call(view, "OnDestroy"); }
            driver.Dispose(); Object.DestroyImmediate(host); Object.DestroyImmediate(avatar); Object.DestroyImmediate(replacementAvatar);
            Object.DestroyImmediate(sound);
            if (avatarMetadata != null) Object.DestroyImmediate(avatarMetadata);
            if (replacementMetadata != null) Object.DestroyImmediate(replacementMetadata);
            Object.DestroyImmediate(legacy); Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(eventObject);
        }
    }

    private static void ProjectAssetRenameAndRuntimeBake(ChatSample chat, CompanionPresentation view,
        WindowProbe assistantWindow, Audio2LipScript lips, string body)
    {
        const string fixturePrefix = "Assets/CompanionNameRegression_";
        string folder = fixturePrefix + Guid.NewGuid().ToString("N");
        string before = folder + "/BeforeRename.vrm";
        string after = folder + "/RenamedCharacter.vrm";
        var inactiveModels = new GameObject("Inactive asset rename fixtures"); inactiveModels.SetActive(false);
        var restoredChatHost = new GameObject("Inactive baked-name serialization fixture"); restoredChatHost.SetActive(false);
        UniVRM10.VRM10Object otherMetadata = null;
        var priorMesh = lips.meshRenderer;
        try
        {
            string source = AssetDatabase.GUIDToAssetPath("244ba07622b71b74cbc29c11d1dc893a");
            Check(!string.IsNullOrEmpty(source) && source.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase),
                "asset rename fixture uses the real imported VRM from the isolated validation project");
            Check(!string.IsNullOrEmpty(AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder))),
                "asset rename fixture owns a unique temporary Assets directory");
            Check(AssetDatabase.CopyAsset(source, before), "real VRM bytes and importer settings are copied into the temporary fixture");
            AssetDatabase.ImportAsset(before, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(before);
            Check(prefab != null && prefab.GetComponent<UniVRM10.Vrm10Instance>() != null,
                "the temporary VRM is imported by the production VRM importer");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, inactiveModels.transform);
            instance.name = "NEVA"; // Match the user's retained scene override after a Project rename.
            PrefabUtility.RecordPrefabInstancePropertyModifications(instance);
            var vrm = instance.GetComponent<UniVRM10.Vrm10Instance>();
            var renderer = instance.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Check(renderer != null && vrm.Vrm != null && vrm.Vrm.Meta.Name == "NEVA",
                "the real fixture preserves embedded NEVA metadata and a lip-sync mesh");
            lips.meshRenderer = renderer;
            Call(view, "Update");
            Check(chat.PresentationCharacterName == "BeforeRename" && Get<Text>(view, "speakerText").text == "BeforeRename" &&
                  assistantWindow.speaker == "BeforeRename" && assistantWindow.original == body,
                "the Project VRM file name wins over both old scene NEVA and embedded metadata in both subtitle views");
            chat.BakePresentationModelFileName();
            Check(ReadRuntimeModelFileName(chat, vrm) == "BeforeRename",
                "the baked model-file pair is readable with Editor asset lookup disabled");
            string guid = AssetDatabase.AssetPathToGUID(before);
            Check(string.IsNullOrEmpty(AssetDatabase.RenameAsset(before, "RenamedCharacter")),
                "the actual VRM asset can be renamed inside the fixture");
            AssetDatabase.ImportAsset(after, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            Check(AssetDatabase.AssetPathToGUID(after) == guid, "Project rename preserves the existing model asset GUID");
            Call(view, "Update");
            Debug.Log("COMPANION_RENAME_OBSERVATION root=" + instance.name + " resolved=" + chat.PresentationCharacterName +
                " hud=" + Get<Text>(view, "speakerText").text + " desktop=" + assistantWindow.speaker +
                " bodyUnchanged=" + (assistantWindow.original == body) + " alpha=" + Get<CanvasGroup>(view, "subtitles").alpha +
                " runtime=" + ReadRuntimeModelFileName(chat, vrm));
            Check(instance.name == "NEVA" && chat.PresentationCharacterName == "RenamedCharacter" &&
                  Get<Text>(view, "speakerText").text == "RenamedCharacter" && assistantWindow.speaker == "RenamedCharacter" &&
                  assistantWindow.original == body,
                "renaming the Project asset updates the retained NEVA scene instance, Unity header and desktop header without new dialogue");
            new CompanionModelNameBuildProcessor().OnProcessScene(chat.gameObject.scene, null);
            Check(ReadRuntimeModelFileName(chat, vrm) == "RenamedCharacter" && Get<string>(chat, "m_PresentationModelFileName") == "RenamedCharacter",
                "the production build-scene processor refreshes the serialized player name after a Project rename");
            var restoredChat = restoredChatHost.AddComponent<ChatSample>(); restoredChat.enabled = false;
            EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(chat), restoredChat);
            Check(ReadRuntimeModelFileName(restoredChat, vrm) == "RenamedCharacter",
                "serialized baked name and model identity survive component restoration without Editor path lookup");
            var renamedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(after);
            var freshInstance = (GameObject)PrefabUtility.InstantiatePrefab(renamedPrefab, inactiveModels.transform);
            freshInstance.name = "Old hierarchy override";
            lips.meshRenderer = freshInstance.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Call(view, "Update");
            Check(assistantWindow.speaker == "RenamedCharacter" && Get<Text>(view, "speakerText").text == "RenamedCharacter",
                "a fresh instance of the renamed Project model also ignores a stale hierarchy label");
            otherMetadata = ScriptableObject.CreateInstance<UniVRM10.VRM10Object>(); otherMetadata.Meta.Name = "Other metadata";
            var detachedModel = new GameObject("Runtime replacement"); detachedModel.transform.SetParent(inactiveModels.transform, false);
            var detachedVrm = detachedModel.AddComponent<UniVRM10.Vrm10Instance>(); detachedVrm.enabled = false; detachedVrm.Vrm = otherMetadata;
            lips.meshRenderer = detachedModel.AddComponent<SkinnedMeshRenderer>();
            Check(string.IsNullOrEmpty(ReadRuntimeModelFileName(chat, detachedVrm)),
                "a different model identity cannot reuse the previous avatar's baked file name");
            Call(view, "Update");
            Check(assistantWindow.speaker == "Runtime replacement" && Get<Text>(view, "speakerText").text == "Runtime replacement",
                "switching from an imported model to a non-asset model falls back to its current hierarchy name");
            chat.BakePresentationModelFileName();
            Check(Get<object>(chat, "m_PresentationNamedModel") == null && string.IsNullOrEmpty(Get<string>(chat, "m_PresentationModelFileName")),
                "baking a model without a Project asset clears the obsolete serialized name pair");
            Check(!inactiveModels.activeInHierarchy && !restoredChatHost.activeInHierarchy && Get<object>(vrm, "m_runtime") == null &&
                  Get<object>(freshInstance.GetComponent<UniVRM10.Vrm10Instance>(), "m_runtime") == null && Get<uint>(lips, "Context") == 0,
                "real VRM import and rename fixtures never activate an avatar Runtime or lip-sync audio context");
        }
        finally
        {
            lips.meshRenderer = priorMesh;
            Object.DestroyImmediate(restoredChatHost); Object.DestroyImmediate(inactiveModels);
            if (otherMetadata != null) Object.DestroyImmediate(otherMetadata);
            if (folder.StartsWith(fixturePrefix, StringComparison.Ordinal) && AssetDatabase.IsValidFolder(folder))
                Check(AssetDatabase.DeleteAsset(folder), "the test removes only its temporary VRM rename fixture directory");
        }
    }

    private static string ReadRuntimeModelFileName(ChatSample chat, UniVRM10.Vrm10Instance avatar)
    {
        return (string)typeof(ChatSample).GetMethod("ReadPresentationModelFileName", Members).Invoke(chat, new object[] { avatar, false });
    }

    private static void CaptureSpeakerHeaders(CompanionPresentation view, Camera camera)
    {
        var target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
        var previousTarget = camera.targetTexture; var previousActive = RenderTexture.active;
        var canvases = new[] { Get<Canvas>(view, "hudCanvas"), Get<Canvas>(view, "panelCanvas") };
        var headers = new[] { Get<Text>(view, "speakerText"), Get<Text>(view, "transcriptSpeakerText") };
        var bodies = new[] { Get<Text>(view, "originalText"), Get<Text>(view, "transcriptText") };
        var cards = new[] { Get<RectTransform>(view, "subtitleCard"), Get<RectTransform>(view, "transcriptCard") };
        var textValues = new[] { headers[0].text, headers[1].text };
        Texture2D image = null;
        try
        {
            Check(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null,
                "speaker-label captures use the real Unity graphics backend");
            foreach (var canvas in canvases)
            { canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 2; }
            camera.orthographic = true; camera.orthographicSize = 5;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.045f, .06f, .065f, 1);
            target.Create(); camera.targetTexture = target; Canvas.ForceUpdateCanvases(); Call(view, "Update"); Canvas.ForceUpdateCanvases();
            var bounds = new Rect[headers.Length];
            for (int i = 0; i < headers.Length; i++)
            {
                Rect header = ScreenBounds(headers[i].rectTransform, camera);
                Rect body = ScreenBounds(bodies[i].rectTransform, camera);
                Rect card = ScreenBounds(cards[i], camera);
                bounds[i] = header;
                Check(header.yMin >= body.yMax - 1f, "speaker header occupies a separate row above its body: " + i);
                Check(header.xMin >= card.xMin - 1f && header.xMax <= card.xMax + 1f && header.yMin >= card.yMax - 1f,
                    "speaker header aligns above the card without overlapping its background: " + i);
                Check(headers[i].GetComponentInParent<RectMask2D>() == null,
                    "speaker header remains outside the body's clipping viewport: " + i);
            }
            camera.Render(); RenderTexture.active = target;
            image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
            Color32[] withLabels = image.GetPixels32();
            Directory.CreateDirectory("Logs"); File.WriteAllBytes("Logs/companion-speaker-and-recognition-headers.png", image.EncodeToPNG());
            foreach (var header in headers) header.text = "";
            Canvas.ForceUpdateCanvases(); camera.Render();
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
            Color32[] withoutLabels = image.GetPixels32();
            var glyphCounts = new int[headers.Length]; int escaped = 0;
            for (int y = 0; y < image.height; y++)
                for (int x = 0; x < image.width; x++)
                {
                    int index = y * image.width + x;
                    var a = withLabels[index]; var b = withoutLabels[index];
                    if (Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b) < 20) continue;
                    bool inside = false;
                    for (int i = 0; i < bounds.Length; i++)
                    {
                        Rect inflated = Rect.MinMaxRect(bounds[i].xMin - 1, bounds[i].yMin - 1, bounds[i].xMax + 1, bounds[i].yMax + 1);
                        if (!inflated.Contains(new Vector2(x, y))) continue;
                        glyphCounts[i]++; inside = true;
                    }
                    if (!inside) escaped++;
                }
            for (int i = 0; i < glyphCounts.Length; i++)
                Check(glyphCounts[i] > 20, "speaker label produces visible glyph pixels in its own header row: " + i);
            Check(escaped < 10, "speaker-label glyphs do not escape their header rows or alter subtitle bodies");
        }
        finally
        {
            for (int i = 0; i < headers.Length; i++) headers[i].text = textValues[i];
            foreach (var canvas in canvases) { canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.worldCamera = null; }
            camera.targetTexture = previousTarget; RenderTexture.active = previousActive;
            if (image != null) Object.DestroyImmediate(image);
            target.Release(); Object.DestroyImmediate(target);
        }
    }

    private static Rect ScreenBounds(RectTransform rect, Camera camera)
    {
        var corners = new Vector3[4]; rect.GetWorldCorners(corners);
        var lower = camera.WorldToScreenPoint(corners[0]); var upper = camera.WorldToScreenPoint(corners[2]);
        return Rect.MinMaxRect(lower.x, lower.y, upper.x, upper.y);
    }

    private static void CaptureSettings(CompanionPresentation view, Camera camera)
    {
        Call(view, "OpenPage", 2);
        var target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
        var priorTarget = camera.targetTexture; var priorActive = RenderTexture.active;
        var canvases = new[] { Get<Canvas>(view, "hudCanvas"), Get<Canvas>(view, "panelCanvas") };
        Texture2D image = null;
        try
        {
            Check(SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null,
                "settings capture uses an actual graphics backend");
            foreach (var canvas in canvases)
            { canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 2; }
            camera.orthographic = true; camera.orthographicSize = 5;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.045f, .06f, .065f, 1);
            target.Create(); camera.targetTexture = target; Canvas.ForceUpdateCanvases();
            var page = Get<GameObject>(view, "settingsPage").GetComponent<RectTransform>();
            var button = Get<Button>(view, "desktopSubtitlesButton").GetComponent<RectTransform>();
            var hint = page.Find("Settings hint").GetComponent<RectTransform>();
            Vector3[] corners = new Vector3[4];
            button.GetWorldCorners(corners);
            foreach (var corner in corners)
            {
                Vector3 point = page.InverseTransformPoint(corner);
                Check(point.x >= page.rect.xMin - .5f && point.x <= page.rect.xMax + .5f &&
                      point.y >= page.rect.yMin - .5f && point.y <= page.rect.yMax + .5f,
                    "the desktop subtitle button remains inside the settings page: " + Checks.Count);
            }
            hint.GetWorldCorners(corners);
            foreach (var corner in corners)
            {
                Vector3 point = page.InverseTransformPoint(corner);
                Check(point.y >= page.rect.yMin - .5f && point.y <= page.rect.yMax + .5f,
                    "the desktop subtitle hint fits vertically without crossing the panel footer: " + Checks.Count);
            }
            camera.Render(); RenderTexture.active = target;
            image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
            Directory.CreateDirectory("Logs");
            File.WriteAllBytes("Logs/companion-desktop-subtitles-settings.png", image.EncodeToPNG());
        }
        finally
        {
            foreach (var canvas in canvases) { canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.worldCamera = null; }
            camera.targetTexture = priorTarget; RenderTexture.active = priorActive;
            if (image != null) Object.DestroyImmediate(image);
            target.Release(); Object.DestroyImmediate(target);
            Call(view, "SetPanelVisible", false);
        }
    }

    private static Text TextView(string name, Transform parent, string value)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text)); go.transform.SetParent(parent, false);
        var text = go.GetComponent<Text>(); text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); text.text = value;
        return text;
    }
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
