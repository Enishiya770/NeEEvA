#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using NeEEvA.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>Isolated thought-view checks. No ChatSample lifecycle or service is started.</summary>
public static class CompanionThoughtBubbleRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunOrThrow()
    {
        Camera camera = CreateCamera();
        GameObject fixture = null;
        try
        {
            CompanionThoughtBubble view = CreateBubble(camera, out fixture);
            CanvasGroup group = Read<CanvasGroup>(view, "group");
            Check(!group.blocksRaycasts && !group.interactable && fixture.GetComponent<GraphicRaycaster>() == null,
                "The thought view never captures input.");
            foreach (Graphic graphic in fixture.GetComponentsInChildren<Graphic>())
                Check(!graphic.raycastTarget, "Thought graphics never capture input.");
            Text label = Read<Text>(view, "label");
            Check(!label.supportRichText, "Model prose cannot become UI markup.");
            label.text = new string('想', 40);
            view.SetPresentationMode(true);
            Check(Position(view, camera.ViewportToWorldPoint(new Vector3(.45f, .45f, 3f))), "A visible head is positioned.");
            Canvas.ForceUpdateCanvases();
            Check(label.preferredHeight <= label.rectTransform.rect.height + 1f,
                "Forty CJK characters remain fully visible in the bubble.");
            CheckWithinRoot(view);
            label.text = "他好像有点累……先听他说完吧。";
            typeof(CompanionThoughtBubble).GetField("layoutWidth", Private).SetValue(view, -1f);
            Position(view, camera.ViewportToWorldPoint(new Vector3(.45f, .45f, 3f)));
            label.cachedTextGenerator.Populate(label.text, label.GetGenerationSettings(label.rectTransform.rect.size));
            int lastLine = label.cachedTextGenerator.lineCount - 1;
            Check(lastLine >= 0 && label.text.Length - label.cachedTextGenerator.lines[lastLine].startCharIdx >= 3,
                "A short CJK reaction does not orphan its closing punctuation on a new line.");
            Position(view, camera.ViewportToWorldPoint(new Vector3(.99f, .99f, 3f)));
            CheckWithinRoot(view);
            Position(view, camera.ViewportToWorldPoint(new Vector3(.01f, .01f, 3f)));
            CheckWithinRoot(view);
            Check(!Position(view, camera.ViewportToWorldPoint(new Vector3(1.05f, .5f, 3f))), "Off-screen avatars hide the thought.");
            Check(!Position(view, camera.transform.position - camera.transform.forward), "Behind-camera avatars hide the thought.");
            group.alpha = 1f;
            view.SetDisplayEnabled(false);
            Check(group.alpha == 0f, "The preference hides an already visible thought immediately.");
            view.SetDisplayEnabled(true);
            group.alpha = 1f;
            fixture.SetActive(false);
            // This fixture stays in edit mode; invoke the runtime lifecycle hook
            // explicitly instead of relying on editor callbacks for a non-ExecuteAlways view.
            typeof(CompanionThoughtBubble).GetMethod("OnDisable", Private).Invoke(view, null);
            Check(group.alpha == 0f, "Disabling the view clears visible pixels.");
            fixture.SetActive(true);
            view.SetPresentationMode(false);
            Check(fixture.GetComponent<Canvas>().renderMode == RenderMode.ScreenSpaceOverlay,
                "Leaving VR restores the desktop overlay.");
            CheckSettings(camera);
            Debug.Log("COMPANION_THOUGHT_BUBBLE_VALIDATION_OK");
        }
        finally
        {
            if (fixture != null) Object.DestroyImmediate(fixture);
            Object.DestroyImmediate(camera.gameObject);
        }
    }

    public static void Capture(string output)
    {
        Directory.CreateDirectory(output);
        Camera camera = CreateCamera();
        GameObject fixture = null;
        GameObject presentationObject = null;
        RenderTexture target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
        RenderTexture previous = RenderTexture.active;
        try
        {
            target.Create();
            camera.targetTexture = target;
            CompanionThoughtBubble view = CreateBubble(camera, out fixture);
            Read<Text>(view, "label").text = "他好像有点累……先听他说完吧。";
            foreach (string sample in new[] { "desktop", "desktop-edge", "vr" })
            {
                view.SetPresentationMode(sample == "vr");
                Canvas canvas = fixture.GetComponent<Canvas>();
                if (sample != "vr")
                {
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = camera;
                    canvas.planeDistance = 1f;
                }
                Canvas.ForceUpdateCanvases();
                Vector3 point = sample == "desktop-edge" ? new Vector3(.97f, .90f, 3f) : new Vector3(.36f, .40f, 3f);
                Check(Position(view, camera.ViewportToWorldPoint(point)), "The capture head is visible.");
                Read<CanvasGroup>(view, "group").alpha = 1f;
                Canvas.ForceUpdateCanvases();
                CheckWithinRoot(view);
                Save(camera, target, Path.Combine(output, "thought-bubble-" + sample + ".png"));
            }
            Object.DestroyImmediate(fixture); fixture = null;
            presentationObject = new GameObject("Thought settings preview");
            var presentation = presentationObject.AddComponent<CompanionPresentation>();
            typeof(CompanionPresentation).GetField("editorScenePreview", Private).SetValue(presentation, true);
            presentation.ConfigurePreview(Font(), camera, false);
            presentation.SetPreviewPanel("settings");
            foreach (string field in new[] { "hudCanvas", "panelCanvas" })
            {
                Canvas canvas = Read<Canvas>(presentation, field);
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 1f;
            }
            presentation.SetPreviewSubtitle("", "");
            Canvas.ForceUpdateCanvases();
            Save(camera, target, Path.Combine(output, "thought-bubble-settings.png"));
            Debug.Log("COMPANION_THOUGHT_BUBBLE_CAPTURES_OK " + output);
        }
        finally
        {
            if (fixture != null) Object.DestroyImmediate(fixture);
            DisposePreview(presentationObject);
            camera.targetTexture = null;
            RenderTexture.active = previous;
            target.Release(); Object.DestroyImmediate(target);
            Object.DestroyImmediate(camera.gameObject);
        }
    }

    private static void CheckSettings(Camera camera)
    {
        const string key = "NeEEvA.UI.ThoughtBubble";
        bool existed = PlayerPrefs.HasKey(key);
        int value = PlayerPrefs.GetInt(key, 1);
        GameObject fixture = new GameObject("Thought settings regression");
        try
        {
            var view = fixture.AddComponent<CompanionPresentation>();
            typeof(CompanionPresentation).GetField("editorScenePreview", Private).SetValue(view, true);
            view.ConfigurePreview(Font(), camera, false);
            view.SetPreviewPanel("settings");
            Canvas.ForceUpdateCanvases();
            Button toggle = Read<Button>(view, "thoughtBubbleButton");
            RectTransform page = Read<GameObject>(view, "settingsPage").transform as RectTransform;
            Vector3[] corners = new Vector3[4];
            toggle.GetComponent<RectTransform>().GetWorldCorners(corners);
            foreach (Vector3 corner in corners)
            {
                Vector3 local = page.InverseTransformPoint(corner);
                Check(local.x >= page.rect.xMin - .1f && local.x <= page.rect.xMax + .1f &&
                    local.y >= page.rect.yMin - .1f && local.y <= page.rect.yMax + .1f,
                    "Thought preference fits inside settings.");
            }
            foreach (Text text in page.GetComponentsInChildren<Text>())
                if (text.name == "Settings hint")
                    Check(text.preferredHeight <= text.rectTransform.rect.height + 1f, "The CJK settings hint has enough line height.");
            bool before = Read<bool>(view, "thoughtBubbleEnabled");
            typeof(CompanionPresentation).GetField("preview", Private).SetValue(view, false);
            toggle.onClick.Invoke();
            Check(Read<bool>(view, "thoughtBubbleEnabled") != before, "The preference toggles its state.");
            Check(PlayerPrefs.GetInt(key, -1) == (before ? 0 : 1), "The preference is persisted.");
            string caption = Read<Text>(view, "thoughtBubbleLabel").text;
            Check(caption.Contains(before ? "已关闭" : "已开启"), "The preference label reflects its state.");
            typeof(CompanionPresentation).GetField("preview", Private).SetValue(view, true);
        }
        finally
        {
            DisposePreview(fixture);
            if (existed) PlayerPrefs.SetInt(key, value); else PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
        }
    }

    private static CompanionThoughtBubble CreateBubble(Camera camera, out GameObject fixture)
    {
        fixture = new GameObject("Thought bubble regression", typeof(RectTransform));
        var view = fixture.AddComponent<CompanionThoughtBubble>();
        view.Configure(null, camera, Font());
        return view;
    }

    private static void DisposePreview(GameObject fixture)
    {
        if (fixture == null) return;
        // Editor preview owns unparented canvases; release them explicitly because
        // ordinary MonoBehaviours do not guarantee OnDestroy in edit mode.
        var view = fixture.GetComponent<CompanionPresentation>();
        if (view != null) view.DisposeEditorScenePreview();
        Object.DestroyImmediate(fixture);
    }

    private static Camera CreateCamera()
    {
        var camera = new GameObject("Thought regression camera", typeof(Camera)).GetComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.09f, .13f, .15f, 1f);
        camera.nearClipPlane = .05f;
        camera.farClipPlane = 100f;
        camera.fieldOfView = 60f;
        camera.aspect = 16f / 9f;
        camera.allowHDR = false;
        return camera;
    }

    private static Font Font()
    {
        Font font = AssetDatabase.LoadAssetAtPath<Font>("Assets/AIChatTookit/Font/NotoSansCJK/NotoSansCJKsc-Regular.otf");
        Check(font != null, "The real CJK interface font is available.");
        return font;
    }

    private static bool Position(CompanionThoughtBubble view, Vector3 head)
    {
        return (bool)typeof(CompanionThoughtBubble).GetMethod("TryPosition", Private).Invoke(view, new object[] { head });
    }

    private static void CheckWithinRoot(CompanionThoughtBubble view)
    {
        RectTransform root = Read<RectTransform>(view, "root");
        foreach (string field in new[] { "card", "nearDot", "farDot" })
        {
            RectTransform child = Read<RectTransform>(view, field);
            Rect rect = new Rect(child.anchoredPosition - child.sizeDelta * .5f, child.sizeDelta);
            Check(rect.xMin >= root.rect.xMin && rect.xMax <= root.rect.xMax &&
                rect.yMin >= root.rect.yMin && rect.yMax <= root.rect.yMax,
                "Bubble and thought dots remain on screen: " + field);
        }
    }

    private static T Read<T>(object target, string field)
    {
        return (T)target.GetType().GetField(field, Private).GetValue(target);
    }

    private static void Save(Camera camera, RenderTexture target, string path)
    {
        camera.Render();
        RenderTexture.active = target;
        Texture2D image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
        try
        {
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply();
            File.WriteAllBytes(path, image.EncodeToPNG());
        }
        finally { Object.DestroyImmediate(image); }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
#endif
