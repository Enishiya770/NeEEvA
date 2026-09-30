#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>Real presentation hierarchy and pixels with synthetic transcript sources; no chat lifecycle.</summary>
public static class CompanionVrTranscriptRegression
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly List<string> checks = new List<string>();

    [Serializable]
    private sealed class Report
    {
        public bool passed;
        public string scope = "Synthetic streaming/final source states through real ChatSample getters and CompanionPresentation UGUI layout/rendering. No physical microphone, ASR service, XR/headset or user-scene execution.";
        public string error;
        public string[] checks;
    }

    public static void RunBatch()
    {
        string project = Application.dataPath.Replace('\\', '/');
        if (!Application.isBatchMode || !project.Contains("/work/vr-asr-subtitles-20260922/unity-fixture/"))
            throw new InvalidOperationException("Run this check only in the isolated VR transcript fixture.");
        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../results"));
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "-vrTranscriptOutput") output = Path.GetFullPath(args[i + 1]);
        Directory.CreateDirectory(output);
        var report = new Report();
        checks.Clear();
        try { Run(output); report.passed = true; }
        catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); }
        report.checks = checks.ToArray();
        File.WriteAllText(Path.Combine(output, "vr-transcript-regression.json"), JsonUtility.ToJson(report, true));
        Debug.Log("[CompanionVrTranscriptRegression] " + (report.passed ? "passed " : "failed after ") + checks.Count + " checks");
        EditorApplication.Exit(report.passed ? 0 : 1);
    }

    private static void Run(string output)
    {
        Font font = AssetDatabase.LoadAssetAtPath<Font>("Assets/AIChatTookit/Font/NotoSansCJK/NotoSansCJKsc-Regular.otf");
        Check(font != null, "The actual Noto Sans CJK interface font is available.");
        var cameraObject = new GameObject("Offline transcript camera", typeof(Camera));
        Camera camera = cameraObject.GetComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.035f, .05f, .065f);
        camera.nearClipPlane = .03f; camera.farClipPlane = 20f;
        camera.fieldOfView = 70f; camera.allowHDR = false;
        var host = new GameObject("Inactive synthetic transcript data");
        host.SetActive(false);
        var viewObject = new GameObject("Offline transcript presentation");
        var view = viewObject.AddComponent<CompanionPresentation>();
        try
        {
            var chat = host.AddComponent<ChatSample>();
            var speech = host.AddComponent<RTSpeechHandler>();
            Set(speech, "m_ChatSample", chat);
            Set(chat, "m_PresentationRealtime", speech);
            Set(chat, "m_ShowStreamingTranscript", true);
            Text original = DataText(host.transform, font, "Original data");
            Text final = DataText(host.transform, font, "Final recognition data");
            Set(chat, "m_TextBack", original); Set(chat, "m_RecordTips", final);
            var overlay = host.AddComponent<SubtitleOverlay>();
            overlay.Initialize(original, null, SubtitleDisplayMode.Bilingual, SubtitleLanguage.ChineseSimplified,
                SystemNoticeMode.ErrorsOnly, false, false, false);
            Set(chat, "m_SubtitleOverlay", overlay);
            original.text = "あなたの声が聞こえています。今日あった出来事を、ゆっくり話してください。ここで聞いています。";
            Get<Text>(overlay, "m_TranslationText").text = "我能听到你的声音。慢慢说说今天发生的事情吧，我会在这里听你说。";
            Text notice = Get<Text>(overlay, "m_SystemNoticeText");
            notice.text = "离线验证 · 同时检查系统提示位置";
            notice.gameObject.SetActive(true);

            // The editor-preview builder skips XR/actions and business lifecycle. Only
            // after building do we attach real read-only chat state and leave preview mode.
            view.ConfigureEditorScenePreview(font, camera);
            Call(view, "SetSubtitleSize", 34);
            Set(view, "chat", chat); Set(view, "preview", false);
            Call(view, "ApplyPresentationMode", true);
            string partial = "我今天想聊聊刚才看到的风景，这是实时识别的中间结果。";
            Set(speech, "m_IsRecording", true);
            Set(chat, "m_StreamingTranscript", partial);
            Refresh(view);
            Check(!Get<GameObject>(view, "desktopControls").activeInHierarchy,
                "XR still hides desktop-only controls.");
            CheckVisible(view, partial, "XR live partial with panel closed");
            CheckVrLayout(view);
            Capture(camera, view, Path.Combine(output, "vr-realtime-panel-closed.png"));

            Set(view, "transcriptChangedAt", Time.unscaledTime - 8f);
            Refresh(view);
            Check(Get<CanvasGroup>(view, "transcript").alpha == 1f,
                "An unchanged live hypothesis stays visible beyond the final-text linger interval.");
            string revision = "我今天想聊聊窗外的风景，这是修订后的实时识别结果。";
            Set(chat, "m_StreamingTranscript", revision);
            Refresh(view);
            CheckVisible(view, revision, "XR revised live partial");
            view.SetPanelVisible(true);
            Refresh(view);
            CheckVisible(view, revision, "XR live partial with panel open");
            CheckVrLayout(view);
            Vector3 targetBeforeHeadStep = Get<Vector3>(view, "subtitleTargetPosition");
            camera.transform.position += new Vector3(.01f, 0f, .01f);
            Call(view, "UpdateSubtitles");
            Check(Get<Vector3>(view, "subtitleTargetPosition") == targetBeforeHeadStep,
                "Unchanged subtitles with the panel open do not bypass the small-head-movement dead zone.");
            camera.transform.position = Vector3.zero;
            Capture(camera, view, Path.Combine(output, "vr-realtime-panel-open.png"));
            view.SetPanelVisible(false);

            Set(speech, "m_IsRecording", false);
            final.text = "这是松开按住说话以后得到的最终识别字幕。";
            Refresh(view);
            Check(!chat.PresentationIsTranscribing, "The synthetic PTT result uses final text after capture ends.");
            CheckVisible(view, final.text, "XR push-to-talk final with panel closed");
            CheckVrLayout(view);
            Capture(camera, view, Path.Combine(output, "vr-ptt-final.png"));

            Call(view, "ApplyPresentationMode", false);
            Refresh(view);
            CheckVisible(view, final.text, "Return to desktop");
            RectTransform card = Get<RectTransform>(view, "transcriptCard");
            Check(card.anchorMin == Vector2.zero && card.anchorMax == Vector2.zero &&
                  card.anchoredPosition == new Vector2(266, 106) && card.sizeDelta == new Vector2(468, 42) &&
                  Get<Text>(view, "transcriptText").fontSize == 17,
                "Returning to desktop restores the existing recognition layout and font size.");
            Call(view, "ApplyPresentationMode", true);
            Refresh(view);
            CheckVisible(view, final.text, "Re-enter XR after desktop");
            CheckVrLayout(view);
            Capture(camera, view, Path.Combine(output, "vr-after-desktop-roundtrip.png"));

            original.text = "听到了。"; Get<Text>(overlay, "m_TranslationText").text = "I heard you.";
            Refresh(view);
            CheckVrLayout(view);
            Capture(camera, view, Path.Combine(output, "vr-short-role-subtitle.png"));
            Set(view, "transcriptChangedAt", Time.unscaledTime - 8f);
            Refresh(view);
            Check(Get<CanvasGroup>(view, "transcript").alpha == 0f,
                "Final recognition still expires after its existing linger interval.");
            final.text = ""; Refresh(view);
            Check(Get<Text>(view, "transcriptText").text == "" && Get<CanvasGroup>(view, "transcript").alpha == 0f,
                "Clearing final text removes the old recognition.");
            Check(!host.activeInHierarchy && viewObject.GetComponent<CompanionXRInteraction>() == null,
                "Chat/microphone hosts stayed inactive and no XR interaction component was created.");
        }
        finally
        {
            view.DisposeEditorScenePreview();
            Object.DestroyImmediate(viewObject); Object.DestroyImmediate(host); Object.DestroyImmediate(cameraObject);
        }
    }

    private static Text DataText(Transform parent, Font font, string name)
    {
        var value = new GameObject(name, typeof(RectTransform), typeof(Text));
        value.transform.SetParent(parent, false);
        Text text = value.GetComponent<Text>(); text.font = font;
        return text;
    }

    private static void Refresh(CompanionPresentation view)
    {
        Canvas.ForceUpdateCanvases();
        Call(view, "UpdateSubtitles");
        Get<CanvasGroup>(view, "subtitles").alpha = 1f; // Make the separate role caption visible without advancing game time.
        Canvas.ForceUpdateCanvases();
        view.SetPanelVisible(view.IsPanelVisible);
        Canvas.ForceUpdateCanvases();
    }

    private static void CheckVisible(CompanionPresentation view, string expected, string label)
    {
        Text text = Get<Text>(view, "transcriptText");
        Check(text.gameObject.activeInHierarchy, label + ": the actual recognition Text is active in its complete hierarchy.");
        Check(text.text == expected && Get<CanvasGroup>(view, "transcript").alpha == 1f,
            label + ": the real chat projection reaches visible recognition text.");
        Check(Get<Text>(view, "transcriptSpeakerText").gameObject.activeInHierarchy &&
              Get<Text>(view, "transcriptSpeakerText").text == "您", label + ": the user name remains visible.");
    }

    private static void CheckVrLayout(CompanionPresentation view)
    {
        RectTransform root = Get<RectTransform>(view, "hudRoot");
        Rect role = LocalBounds(root, Get<RectTransform>(view, "subtitleCard"));
        Rect roleName = LocalBounds(root, Get<Text>(view, "speakerText").rectTransform);
        Rect heard = LocalBounds(root, Get<RectTransform>(view, "transcriptCard"));
        Rect userName = LocalBounds(root, Get<Text>(view, "transcriptSpeakerText").rectTransform);
        Rect notice = LocalBounds(root, Get<Text>(view, "noticeText").transform.parent as RectTransform);
        Check(!heard.Overlaps(role) && !userName.Overlaps(role) && !notice.Overlaps(roleName) &&
              !notice.Overlaps(role) && !heard.Overlaps(notice),
            "VR role caption, speaker labels, recognition and notice occupy separate areas at the current caption height.");
        Check(Get<RectTransform>(view, "transcriptCard").parent == root,
            "Recognition shares the moving HUD root independently of panel and desktop controls.");
        Check(Get<Text>(view, "transcriptText").fontSize == 22 && heard.width > 600f,
            "VR recognition uses the wider spatial layout and readable font size.");
    }

    private static Rect LocalBounds(RectTransform root, RectTransform item)
    {
        Vector3[] corners = new Vector3[4]; item.GetWorldCorners(corners);
        Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        foreach (Vector3 corner in corners)
        {
            Vector2 point = root.InverseTransformPoint(corner);
            min = Vector2.Min(min, point); max = Vector2.Max(max, point);
        }
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private static void Capture(Camera camera, CompanionPresentation view, string path)
    {
        var target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
        RenderTexture previous = RenderTexture.active;
        Text text = Get<Text>(view, "transcriptText");
        Texture2D shown = null, hidden = null;
        try
        {
            target.Create(); camera.targetTexture = target;
            Canvas.ForceUpdateCanvases();
            Vector3[] corners = new Vector3[4]; text.rectTransform.GetWorldCorners(corners);
            foreach (Vector3 corner in corners)
            {
                Vector3 point = camera.WorldToViewportPoint(corner);
                Check(point.z > 0f && point.x >= 0f && point.x <= 1f && point.y >= 0f && point.y <= 1f,
                    "The actual recognition viewport fits inside the rendered VR view.");
            }
            shown = ReadPixels(camera, target);
            File.WriteAllBytes(path, shown.EncodeToPNG());
            text.enabled = false; Canvas.ForceUpdateCanvases();
            hidden = ReadPixels(camera, target);
            Color32[] on = shown.GetPixels32(), off = hidden.GetPixels32();
            int changed = 0;
            for (int i = 0; i < on.Length; i++)
                if (Math.Abs(on[i].r - off[i].r) > 12 || Math.Abs(on[i].g - off[i].g) > 12 || Math.Abs(on[i].b - off[i].b) > 12) changed++;
            Check(changed > 20, "Rendered CJK recognition glyphs contribute visible pixels: " + Path.GetFileName(path));
        }
        finally
        {
            text.enabled = true; camera.targetTexture = null; RenderTexture.active = previous;
            if (shown != null) Object.DestroyImmediate(shown);
            if (hidden != null) Object.DestroyImmediate(hidden);
            target.Release(); Object.DestroyImmediate(target);
        }
    }

    private static Texture2D ReadPixels(Camera camera, RenderTexture target)
    {
        camera.Render(); RenderTexture.active = target;
        var pixels = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
        pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply();
        return pixels;
    }

    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Fields).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Fields).SetValue(target, value);
    private static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Fields).Invoke(target, args);
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks.Add(name);
    }
}
#endif
