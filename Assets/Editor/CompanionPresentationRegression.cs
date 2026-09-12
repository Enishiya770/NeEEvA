#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using NeEEvA.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>
/// Isolated rendering and geometry checks. This fixture never starts ChatSample,
/// voice capture, a service connection, or the user's chat scene.
/// </summary>
public static class CompanionPresentationRegression
{
    private static int s_Checks;
    private static readonly Color Ink = new Color(0.92f, 0.91f, 0.88f, 1f);
    private static readonly Color Accent = new Color(0.43f, 0.83f, 0.74f, 1f);

    public static void RunIconCatalogBatch()
    {
        Run(false);
    }

    public static void RunBatch()
    {
        Run(true);
    }

    private static void Run(bool includePresentation)
    {
        try
        {
            s_Checks = 0;
            string output = OutputDirectory();
            Directory.CreateDirectory(output);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Font font = AssetDatabase.LoadAssetAtPath<Font>(
                "Assets/AIChatTookit/Font/NotoSansCJK/NotoSansCJKsc-Regular.otf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Check(font != null, "A preview font is available.");
            ValidateMeshes();
            Camera camera = CameraForPreview();
            Canvas catalog = BuildCatalog(camera, font);
            Capture(camera, Path.Combine(output, "companion-icon-catalog.png"));
            Object.DestroyImmediate(catalog.gameObject);
            if (includePresentation) RenderPresentation(camera, font, output);
            string summary = "{\"passed\":true,\"checks\":" + s_Checks +
                ",\"includesPresentation\":" + (includePresentation ? "true" : "false") +
                ",\"unityVersion\":\"" + Application.unityVersion + "\"}";
            File.WriteAllText(Path.Combine(output, includePresentation ? "presentation-result.json" : "icons-result.json"), summary);
            Debug.Log("COMPANION_PRESENTATION_VALIDATION_OK " + summary);
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    private static string OutputDirectory()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-companionOutput") return Path.GetFullPath(args[i + 1]);
        return Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/CompanionPresentationValidation"));
    }

    private static void ValidateMeshes()
    {
        GameObject fixture = new GameObject("Icon geometry fixture", typeof(RectTransform));
        CompanionIcon icon = fixture.AddComponent<CompanionIcon>();
        icon.color = Accent;
        Check(fixture.GetComponent<CanvasRenderer>() != null, "Icons create their CanvasRenderer before the canvas rebuild.");
        Check(!icon.raycastTarget, "Icons leave raycasts to button hit areas.");
        RectTransform rect = icon.rectTransform;
        Vector2[] sizes = { new Vector2(24f, 24f), new Vector2(96f, 40f) };
        foreach (Vector2 size in sizes)
        {
            rect.sizeDelta = size;
            foreach (CompanionIconKind kind in Enum.GetValues(typeof(CompanionIconKind)))
            {
                icon.kind = kind;
                Mesh mesh = ReadMesh(icon);
                Check(mesh.vertexCount > 0 && mesh.triangles.Length > 0, kind + " has geometry.");
                CheckMeshBounds(mesh, rect.rect, kind.ToString());
                Color32 expected = icon.color;
                foreach (Color32 tint in mesh.colors32)
                    Check(tint.Equals(expected), kind + " uses Graphic.color.");
                Object.DestroyImmediate(mesh);
            }
        }
        icon.strokeWidth = -4f;
        Check(icon.strokeWidth > 0f, "Negative icon widths are clamped.");
        Object.DestroyImmediate(fixture);

        fixture = new GameObject("Surface geometry fixture", typeof(RectTransform));
        CompanionSurface surface = fixture.AddComponent<CompanionSurface>();
        surface.rectTransform.sizeDelta = new Vector2(180f, 52f);
        surface.color = new Color(0.12f, 0.15f, 0.17f, 0.8f);
        surface.radius = 18f;
        Mesh surfaceMesh = ReadMesh(surface);
        CheckMeshBounds(surfaceMesh, surface.rectTransform.rect, "Rounded surface");
        bool hasTransparentEdge = false;
        bool hasSolidInterior = false;
        Color32 surfaceColor = surface.color;
        foreach (Color32 tint in surfaceMesh.colors32)
        {
            hasTransparentEdge |= tint.a == 0;
            hasSolidInterior |= tint.a == surfaceColor.a;
        }
        Check(hasTransparentEdge && hasSolidInterior, "Surface feather has a transparent rim and solid interior.");
        Object.DestroyImmediate(surfaceMesh);
        surface.radius = 1000f;
        surface.antiAliasing = false;
        surfaceMesh = ReadMesh(surface);
        CheckMeshBounds(surfaceMesh, surface.rectTransform.rect, "Clamped surface");
        foreach (Color32 tint in surfaceMesh.colors32)
            Check(tint.Equals(surfaceColor), "Disabling AA keeps the entire surface solid.");
        Object.DestroyImmediate(surfaceMesh);
        Object.DestroyImmediate(fixture);
    }

    private static Mesh ReadMesh(Graphic graphic)
    {
        MethodInfo populate = graphic.GetType().GetMethod("OnPopulateMesh",
            BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(VertexHelper) }, null);
        Check(populate != null, graphic.GetType().Name + " exposes its mesh generator.");
        using (VertexHelper helper = new VertexHelper())
        {
            populate.Invoke(graphic, new object[] { helper });
            Mesh mesh = new Mesh();
            helper.FillMesh(mesh);
            return mesh;
        }
    }

    private static void CheckMeshBounds(Mesh mesh, Rect rect, string label)
    {
        Vector3[] vertices = mesh.vertices;
        foreach (Vector3 point in vertices)
        {
            Check(!float.IsNaN(point.x) && !float.IsInfinity(point.x) &&
                !float.IsNaN(point.y) && !float.IsInfinity(point.y), label + " vertices are finite.");
            Check(point.x >= rect.xMin - 0.001f && point.x <= rect.xMax + 0.001f &&
                point.y >= rect.yMin - 0.001f && point.y <= rect.yMax + 0.001f,
                label + " stays inside its RectTransform.");
        }
        foreach (int index in mesh.triangles)
            Check(index >= 0 && index < vertices.Length, label + " indices are valid.");
    }

    private static Camera CameraForPreview()
    {
        GameObject cameraObject = new GameObject("Companion preview camera", typeof(Camera));
        Camera camera = cameraObject.GetComponent<Camera>();
        camera.transform.position = new Vector3(0f, 0f, -10f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.045f, 0.060f, 0.066f, 1f);
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 100f;
        camera.orthographic = true;
        camera.orthographicSize = 5.625f;
        camera.allowHDR = false;
        return camera;
    }

    private static Canvas BuildCatalog(Camera camera, Font font)
    {
        GameObject root = new GameObject("Companion icon catalog", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = camera;
        canvas.planeDistance = 2f;
        CanvasScaler scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        TextElement(root.transform, "NEEEVA / COMPANION UI", font, 18, Accent, 112f, 52f, 800f, 36f);
        TextElement(root.transform, "留给陪伴的空间", font, 40, Ink, 110f, 94f, 1100f, 70f);
        TextElement(root.transform, "统一线性图标 · 自适应尺寸 · 桌面与 VR 共用", font, 19,
            new Color(0.59f, 0.65f, 0.66f), 112f, 174f, 1500f, 36f);
        CompanionIconKind[] kinds = (CompanionIconKind[])Enum.GetValues(typeof(CompanionIconKind));
        for (int i = 0; i < kinds.Length; i++)
        {
            int row = i / 5;
            int col = i % 5;
            GameObject card = RectObject("Card " + kinds[i], root.transform,
                112f + col * 344f, 242f + row * 151f, 324f, 132f);
            CompanionSurface surface = card.AddComponent<CompanionSurface>();
            surface.radius = 18f;
            surface.raycastTarget = false;
            surface.color = new Color(0.085f, 0.108f, 0.116f, 1f);
            GameObject glyph = RectObject(kinds[i].ToString(), card.transform, 24f, 36f, 60f, 60f);
            CompanionIcon icon = glyph.AddComponent<CompanionIcon>();
            icon.kind = kinds[i];
            icon.color = i == 0 || i == 3 || i == 17 ? Accent : Ink;
            TextElement(card.transform, kinds[i].ToString(), font, 20, Ink, 106f, 40f, 208f, 52f);
        }
        return canvas;
    }

    private static GameObject RectObject(string name, Transform parent,
        float x, float y, float width, float height)
    {
        GameObject result = new GameObject(name, typeof(RectTransform));
        RectTransform rect = result.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
        return result;
    }

    private static void TextElement(Transform parent, string value, Font font, int size,
        Color color, float x, float y, float width, float height)
    {
        Text text = RectObject(value, parent, x, y, width, height).AddComponent<Text>();
        text.font = font;
        text.fontSize = size;
        text.text = value;
        text.color = color;
        text.alignment = TextAnchor.MiddleLeft;
        text.raycastTarget = false;
    }

    private static void RenderPresentation(Camera camera, Font font, string output)
    {
        Type presentationType = typeof(CompanionIcon).Assembly.GetType("NeEEvA.Presentation.CompanionPresentation");
        Check(presentationType != null, "CompanionPresentation is compiled into the preview fixture.");
        MethodInfo preview = presentationType.GetMethod("ConfigurePreview",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(Font), typeof(Camera), typeof(bool) }, null);
        Check(preview != null, "CompanionPresentation has a deterministic preview entry point.");
        MethodInfo setPanel = presentationType.GetMethod("SetPreviewPanel");
        MethodInfo setSubtitle = presentationType.GetMethod("SetPreviewSubtitle");
        Check(setPanel != null && setSubtitle != null, "Presentation exposes panel and subtitle preview controls.");
        for (int i = 0; i < 2; i++)
        {
            bool vr = i == 1;
            camera.orthographic = !vr;
            camera.fieldOfView = 60f;
            GameObject fixture = new GameObject("Presentation preview " + (vr ? "VR" : "desktop"));
            Component view = fixture.AddComponent(presentationType);
            preview.Invoke(view, new object[] { font, camera, vr });
            foreach (Canvas canvas in Object.FindObjectsOfType<Canvas>(true))
            {
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 2f;
            }
            string[] pages = { "closed", "history", "settings", "text" };
            foreach (string page in pages)
            {
                setPanel.Invoke(view, new object[] { page });
                Canvas.ForceUpdateCanvases();
                setSubtitle.Invoke(view, new object[] {
                    "特別な話じゃなくてもいいよ。あなたの声、聞きたかったの。",
                    "聊些日常小事就好。只是，想听听你的声音。" });
                Capture(camera, Path.Combine(output, "companion-" + (vr ? "vr-" : "desktop-") + page + ".png"));
                if (page == "history")
                    foreach (Text body in Object.FindObjectsOfType<Text>())
                        if (body.name == "Message text") Check(body.rectTransform.rect.height + 1f >= body.preferredHeight,
                            "History body reserves enough height for every wrapped or bilingual line.");
            }
            MethodInfo setSize = presentationType.GetMethod("SetSubtitleSize", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(setSize != null, "Large subtitle size can be exercised in the offline preview.");
            setSize.Invoke(view, new object[] { 34 });
            setPanel.Invoke(view, new object[] { "settings" });
            setSubtitle.Invoke(view, new object[] {
                "今日は特別な話をしなくても大丈夫だからここで一緒に少しずつゆっくり休んであなたの好きなことや今日あった出来事を聞かせてくれたらうれしいと思っているの",
                "今天不用准备什么特别的话题我们就在这里慢慢休息一会儿你可以告诉我今天发生的小事或者最近喜欢的东西我都会认真听你说也很高兴能够这样陪着你" });
            Capture(camera, Path.Combine(output, "companion-" + (vr ? "vr" : "desktop") + "-large-bilingual.png"));
            if (!vr)
            {
                Capture(camera, Path.Combine(output, "companion-desktop-4x3-large-bilingual.png"), 1440, 1080);
                setPanel.Invoke(view, new object[] { "closed" });
                Capture(camera, Path.Combine(output, "companion-desktop-4x3-closed.png"), 1440, 1080);
            }
            // The production view owns root canvases instead of parenting them to the camera.
            // Remove these before its owner so editor-mode runs cannot leak into the next fixture.
            foreach (Canvas canvas in Object.FindObjectsOfType<Canvas>(true))
                if (canvas.isRootCanvas) Object.DestroyImmediate(canvas.gameObject);
            Object.DestroyImmediate(fixture);
        }
    }

    private static void Capture(Camera camera, string path, int width = 1920, int height = 1080)
    {
        RenderTexture target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        target.antiAliasing = 4;
        RenderTexture previous = RenderTexture.active;
        RenderTexture previousCameraTarget = camera.targetTexture;
        Texture2D pixels = null;
        try
        {
            target.Create();
            camera.targetTexture = target;
            Canvas.ForceUpdateCanvases();
            RefreshPresentationForCapture(camera);
            Canvas.ForceUpdateCanvases();
            camera.Render();
            CheckPresentationBounds(camera);
            RenderTexture.active = target;
            pixels = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            pixels.ReadPixels(new Rect(0f, 0f, target.width, target.height), 0, 0);
            pixels.Apply();
            if (Path.GetFileName(path) == "companion-icon-catalog.png") CheckCatalogPixels(pixels);
            File.WriteAllBytes(path, pixels.EncodeToPNG());
            Check(new FileInfo(path).Length > 10000, "Rendered PNG contains visible UI: " + Path.GetFileName(path));
        }
        finally
        {
            camera.targetTexture = previousCameraTarget;
            RenderTexture.active = previous;
            if (pixels != null) Object.DestroyImmediate(pixels);
            target.Release();
            Object.DestroyImmediate(target);
        }
    }

    private static void RefreshPresentationForCapture(Camera camera)
    {
        foreach (MonoBehaviour view in Object.FindObjectsOfType<MonoBehaviour>())
        {
            Type type = view.GetType();
            if (type.FullName != "NeEEvA.Presentation.CompanionPresentation") continue;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            string source = (string)type.GetField("previewSource", flags).GetValue(view);
            string translation = (string)type.GetField("previewTranslation", flags).GetValue(view);
            type.GetMethod("SetPreviewSubtitle").Invoke(view, new object[] { source, translation });
            // Only the presentation Update runs. ChatSample.Start and microphone code never run.
            type.GetMethod("Update", flags).Invoke(view, null);
        }
    }

    private static void CheckPresentationBounds(Camera camera)
    {
        foreach (MonoBehaviour view in Object.FindObjectsOfType<MonoBehaviour>())
        {
            Type type = view.GetType();
            if (type.FullName != "NeEEvA.Presentation.CompanionPresentation") continue;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            RectTransform subtitle = (RectTransform)type.GetField("subtitleCard", flags).GetValue(view);
            RectTransform panel = (RectTransform)type.GetField("panelCard", flags).GetValue(view);
            bool visible = (bool)type.GetProperty("IsPanelVisible").GetValue(view);
            bool vr = (bool)type.GetProperty("IsVRPresentation").GetValue(view);
            Rect captionBounds = ScreenBounds(camera, subtitle);
            Check(captionBounds.xMin >= -1f && captionBounds.xMax <= camera.pixelWidth + 1f &&
                captionBounds.yMin >= -1f && captionBounds.yMax <= camera.pixelHeight + 1f,
                "Subtitles stay within the render viewport.");
            if (visible) Check(!captionBounds.Overlaps(ScreenBounds(camera, panel)), "Open panels leave subtitles unobscured.");
            else Check(Mathf.Abs(captionBounds.center.x - camera.pixelWidth * .5f) < 2f,
                "Closed-panel subtitles are horizontally centered at the final render resolution.");
            GameObject recenter = (GameObject)type.GetField("recenterControl", flags).GetValue(view);
            Check(recenter.activeSelf == vr, "Panel recenter is offered only in VR.");
            foreach (Text text in subtitle.GetComponentsInChildren<Text>())
            {
                Check(text.rectTransform.rect.height + 1f >= text.preferredHeight,
                    "Every current subtitle line has sufficient height, including large bilingual CJK text.");
                Check(text.cachedTextGenerator.lineCount <= 2, "Current subtitle pages use at most two lines per language.");
            }
        }
    }

    private static Rect ScreenBounds(Camera camera, RectTransform rect)
    {
        Vector3[] corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        foreach (Vector3 corner in corners)
        {
            Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, corner);
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private static void CheckCatalogPixels(Texture2D image)
    {
        for (int i = 0; i < Enum.GetValues(typeof(CompanionIconKind)).Length; i++)
        {
            int x = 136 + (i % 5) * 344;
            int y = 278 + (i / 5) * 151;
            int visiblePixels = 0;
            for (int py = y; py < y + 60; py++)
                for (int px = x; px < x + 60; px++)
                    if (image.GetPixel(px, image.height - 1 - py).grayscale > 0.45f) visiblePixels++;
            Check(visiblePixels > 50, "Icon " + i + " actually renders visible strokes, not only mesh data.");
        }
        Color card = image.GetPixel(130, image.height - 1 - 310);
        Check(card.grayscale > 0.085f, "Rounded card surfaces actually render behind the icons.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        s_Checks++;
    }
}
#endif
