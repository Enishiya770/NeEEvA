#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NeEEvA.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// Render the actual learning page without running ChatSample, a microphone,
// a service client, or any user scene. No saved scene or player setting is edited.
public static class PersonalLearningPresentationRegression
{
    public static void RunBatch()
    {
        try
        {
            string output = Environment.GetEnvironmentVariable("NEEEVA_LEARNING_PREVIEW_OUTPUT");
            if (string.IsNullOrEmpty(output)) throw new ArgumentException("NEEEVA_LEARNING_PREVIEW_OUTPUT required");
            Directory.CreateDirectory(output);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            int checks = PersonalSingingLearningRegression.Run(projectRoot) + PersonalSingingLearningRegression.RunRuntime(projectRoot);
            checks += PersonalSingingLearningWaveformRegression.Run();
            var teachingTests = typeof(PersonalLearningPresentationRegression).Assembly.GetType("PersonalSingingLearningTeachingRegression");
            if (teachingTests != null)
                checks += (int)teachingTests.GetMethod("Run", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { projectRoot });
            string audioPath = Environment.GetEnvironmentVariable("NEEEVA_LEARNING_PREVIEW_WAV");
            float[] pcm = null; int audioRate = 16000;
            if (!string.IsNullOrEmpty(audioPath))
            {
                byte[] wav = File.ReadAllBytes(audioPath);
                if (!PersonalSingingLearningProtocol.TryWave(wav, out audioRate, out int channels, out int frames, out int offset, out _)) throw new InvalidDataException("Preview WAV invalid");
                pcm = new float[frames];
                for (int i = 0; i < frames; i++) pcm[i] = BitConverter.ToInt16(wav, offset + i * channels * 2) / 32768f;
            }
            var font = AssetDatabase.LoadAssetAtPath<Font>("Assets/AIChatTookit/Font/NotoSansCJK/NotoSansCJKsc-Regular.otf")
                ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var camera = (Camera)typeof(CompanionPresentationRegression).GetMethod("CameraForPreview", flags).Invoke(null, null);
            int pictures = 0;
            foreach (bool vr in new[] { false, true })
            {
                camera.orthographic = !vr; camera.fieldOfView = 60;
                var fixture = new GameObject("Personal learning preview");
                var view = fixture.AddComponent<CompanionPresentation>();
                view.ConfigurePreview(font, camera, vr);
                view.SetPreviewPanel("learning"); view.SetPreviewSubtitle("", "");
                string proposalPath = Environment.GetEnvironmentVariable("NEEEVA_LEARNING_PREVIEW_PROPOSAL");
                if (pcm != null) view.SetVoiceTeachingPreview(pcm, audioRate, !string.IsNullOrEmpty(proposalPath) ? (JArray)JObject.Parse(File.ReadAllText(proposalPath))["regions"] : new JArray(
                    new JObject { ["start_sample"] = 0, ["end_sample"] = (int)(3.7 * audioRate), ["label"] = "speech" },
                    new JObject { ["start_sample"] = (int)(3.7 * audioRate), ["end_sample"] = (int)(14 * audioRate), ["label"] = "singing" },
                    new JObject { ["start_sample"] = (int)(14 * audioRate), ["end_sample"] = pcm.Length, ["label"] = "uncertain" }));
                if (Object.FindObjectsOfType<PersonalSingingLearningClient>(true).Length != 0)
                    throw new InvalidOperationException("Preview created a live learning client");
                foreach (var canvas in Object.FindObjectsOfType<Canvas>(true))
                    if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                    { canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 2; }
                Canvas.ForceUpdateCanvases();
                if (!Object.FindObjectsOfType<Text>().Any(t => t.text != null && t.text.Contains("声音学习")))
                    throw new InvalidOperationException("Learning page is not reachable");
                foreach (float position in new[] { 1f, .42f, 0f })
                {
                    typeof(CompanionPresentation).GetMethod("SetLearningAdvanced", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(view, new object[] { position == 0 });
                    var wave = fixture.GetComponentInChildren<PersonalSingingLearningWaveform>();
                    // Presentation canvases are separate scene roots.
                    wave = Object.FindObjectOfType<PersonalSingingLearningWaveform>();
                    if (position == .42f) wave.ZoomSelection(); else wave.ShowAll();
                    string suffix = position == 1 ? "" : position == 0 ? "-bottom" : "-annotation";
                    CaptureLearning(view, camera, Path.Combine(output, (vr ? "learning-vr" : "learning-desktop") + suffix + ".png"), position);
                    pictures++;
                }
                foreach (var canvas in Object.FindObjectsOfType<Canvas>(true))
                    if (canvas.isRootCanvas) Object.DestroyImmediate(canvas.gameObject);
                Object.DestroyImmediate(fixture);
            }
            File.WriteAllText(Path.Combine(output, "result.json"), "{\"passed\":true,\"pictures\":" + pictures + ",\"client_checks\":" + checks + ",\"live_clients\":0}");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    private static void CaptureLearning(CompanionPresentation view, Camera camera, string path, float normalized)
    {
        var target = new RenderTexture(1920, 1080, 24) { antiAliasing = 4 };
        var previous = RenderTexture.active;
        var oldTarget = camera.targetTexture;
        Texture2D pixels = null;
        try
        {
            target.Create(); camera.targetTexture = target;
            Canvas.ForceUpdateCanvases();
            typeof(CompanionPresentation).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(view, null);
            Canvas.ForceUpdateCanvases();
            var scroll = Object.FindObjectsOfType<ScrollRect>().Single(s => s.name == "Personal voice learning");
            // Set the scroll position only AFTER the final render viewport and
            // CanvasScaler have settled, not against the batch editor's window.
            scroll.verticalNormalizedPosition = normalized;
            Canvas.ForceUpdateCanvases();
            camera.Render();
            string markerName = normalized == 1 ? "Title" : normalized == 0 ? "Learning results" : "Original metadata";
            var marker = scroll.content.GetComponentsInChildren<Text>().First(t => t.name == markerName);
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(scroll.viewport, marker.rectTransform);
            var viewport = scroll.viewport.rect;
            var geometry = new JObject { ["scroll"] = normalized, ["actual_scroll"] = scroll.verticalNormalizedPosition,
                ["content_height"] = scroll.content.rect.height, ["content_y"] = scroll.content.anchoredPosition.y,
                ["viewport_height"] = viewport.height, ["marker"] = markerName,
                ["marker_y_min"] = bounds.min.y, ["marker_y_max"] = bounds.max.y, ["marker_culled"] = marker.canvasRenderer.cull };
            File.WriteAllText(path + ".json", geometry.ToString());
            if (normalized != .42f && (bounds.max.y < viewport.yMin || bounds.min.y > viewport.yMax || marker.canvasRenderer.cull))
                throw new InvalidOperationException("Learning scroll marker is invisible: " + geometry);
            RenderTexture.active = target;
            pixels = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
            pixels.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); pixels.Apply();
            File.WriteAllBytes(path, pixels.EncodeToPNG());
        }
        finally
        {
            camera.targetTexture = oldTarget; RenderTexture.active = previous;
            if (pixels != null) Object.DestroyImmediate(pixels);
            target.Release(); Object.DestroyImmediate(target);
        }
    }
}
#endif
