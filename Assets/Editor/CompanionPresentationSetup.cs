using System;
using System.Collections.Generic;
using System.IO;
using NeEEvA.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Installs the runtime owner and displays a disposable edit-mode copy of its visual tree.</summary>
[InitializeOnLoad]
public static class CompanionPresentationSetup
{
    public const string FontPath = "Assets/AIChatTookit/Font/NotoSansCJK/NotoSansCJKsc-Regular.otf";
    private static readonly Dictionary<int, PreviewLease> previews = new Dictionary<int, PreviewLease>();
    private static readonly List<int> stale = new List<int>();
    private static double nextRefresh;
    private static bool suspended;

    private sealed class PreviewLease
    {
        public CompanionPresentation owner, visual;
        public GameObject root;
        public Canvas[] legacy;
        public Behaviour[] legacyViews;
        public bool[] enabled;
        public string scenePath;

        public void HideLegacy()
        {
            if (owner != null) SyncLegacyState(owner, true);
        }

        public void Dispose()
        {
            // Clearing a visual preview is not opting out of the replacement UI. In particular,
            // domain/scene reload must never see the old canvases re-enabled before runtime Start.
            if (owner != null) SyncLegacyState(owner, owner.isActiveAndEnabled);
            else if (legacyViews != null)
            {
                for (int i = 0; i < legacyViews.Length; i++)
                {
                    if (legacyViews[i] == null || legacyViews[i].enabled == enabled[i]) continue;
                    legacyViews[i].enabled = enabled[i];
                    EditorUtility.SetDirty(legacyViews[i]);
                    EditorSceneManager.MarkSceneDirty(legacyViews[i].gameObject.scene);
                }
            }
            if (visual != null) visual.DisposeEditorScenePreview();
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
        }
    }

    [Serializable]
    private sealed class Receipt
    {
        public string scene;
        public string unityVersion;
        public string updatedUtc;
        public bool editMode;
        public bool runtimeComponentInstalled;
        public bool visualPreviewActive;
        public bool legacyCanvasesHidden;
        public int legacyCanvasCount;
    }

    static CompanionPresentationSetup()
    {
        EditorApplication.delayCall += UpgradeOpenChatScenes;
        EditorApplication.update += UpdatePreviews;
        AssemblyReloadEvents.beforeAssemblyReload += SuspendAndClear;
        EditorApplication.quitting += SuspendAndClear;
        EditorSceneManager.sceneOpened += (_, __) => QueueRefresh();
        EditorSceneManager.sceneClosing += (_, __) => ClearPreviews();
        // Save the suppressed state, while disposing the unsaved preview tree.
        EditorSceneManager.sceneSaving += (_, __) => SuspendAndClear();
        EditorSceneManager.sceneSaved += _ => QueueRefresh();
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingEditMode) SuspendAndClear();
            else if (state == PlayModeStateChange.EnteredEditMode) QueueRefresh();
        };
    }

    private static bool CanPreview => !Application.isBatchMode && !EditorApplication.isPlayingOrWillChangePlaymode &&
        !EditorApplication.isCompiling && !EditorApplication.isUpdating && !suspended;

    private static bool IsChatScene(Scene scene) => scene.isLoaded &&
        (scene.path == "Assets/AIChatTookit/Scene/chatSample.unity" ||
         scene.path == "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity");

    private static void QueueRefresh()
    {
        suspended = false;
        nextRefresh = 0;
        EditorApplication.delayCall += UpgradeOpenChatScenes;
    }

    [MenuItem("NeEEvA/Interface/Apply Companion UI to Open Chat Scenes")]
    public static void UpgradeOpenChatScenes()
    {
        if (!CanPreview) return;
        var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (font == null) return;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!IsChatScene(scene)) continue;
            foreach (var root in scene.GetRootGameObjects())
            foreach (var chat in root.GetComponentsInChildren<ChatSample>(true))
            {
                var presentation = chat.GetComponent<CompanionPresentation>();
                if (presentation == null)
                {
                    presentation = Undo.AddComponent<CompanionPresentation>(chat.gameObject);
                    var serialized = new SerializedObject(presentation);
                    serialized.FindProperty("interfaceFont").objectReferenceValue = font;
                    serialized.FindProperty("playerCamera").objectReferenceValue = FindPlayerCamera(scene);
                    serialized.ApplyModifiedProperties();
                    EditorSceneManager.MarkSceneDirty(scene);
                }
                if (!presentation.isActiveAndEnabled) { SyncLegacyState(presentation, false); continue; }
                if (previews.ContainsKey(presentation.GetInstanceID())) continue;
                CreatePreview(presentation, chat, font);
            }
        }
    }

    private static void CreatePreview(CompanionPresentation presentation, ChatSample chat, Font fallback)
    {
        var serialized = new SerializedObject(presentation);
        var font = serialized.FindProperty("interfaceFont").objectReferenceValue as Font ?? fallback;
        var camera = serialized.FindProperty("playerCamera").objectReferenceValue as Camera ?? FindPlayerCamera(chat.gameObject.scene);
        if (camera == null || font == null) return;
        var lease = new PreviewLease { owner = presentation, legacy = chat.PresentationLegacyCanvases, scenePath = chat.gameObject.scene.path };
        try
        {
            lease.root = new GameObject("Companion UI • Editor preview") { hideFlags = HideFlags.HideAndDontSave };
            SceneManager.MoveGameObjectToScene(lease.root, chat.gameObject.scene);
            lease.visual = lease.root.AddComponent<CompanionPresentation>();
            lease.visual.ConfigureEditorScenePreview(font, camera);
            lease.HideLegacy();
            // Retain a fallback copy for deleting the owner component in the editor.
            serialized.Update();
            var states = serialized.FindProperty("legacyViews");
            lease.legacyViews = new Behaviour[states.arraySize];
            lease.enabled = new bool[states.arraySize];
            for (int i = 0; i < states.arraySize; i++)
            {
                var state = states.GetArrayElementAtIndex(i);
                lease.legacyViews[i] = state.FindPropertyRelative("view").objectReferenceValue as Behaviour;
                lease.enabled[i] = state.FindPropertyRelative("wasEnabled").boolValue;
            }
            previews.Add(presentation.GetInstanceID(), lease);
            WriteReceipt(lease);
            RepaintGameViews();
        }
        catch (Exception error)
        {
            lease.Dispose();
            SyncLegacyState(presentation, false);
            Debug.LogException(error);
        }
    }

    private static void UpdatePreviews()
    {
        if (!CanPreview || EditorApplication.timeSinceStartup < nextRefresh) return;
        nextRefresh = EditorApplication.timeSinceStartup + .25;
        stale.Clear();
        foreach (var pair in previews)
        {
            var lease = pair.Value;
            if (lease.owner == null || !lease.owner.isActiveAndEnabled || lease.visual == null || !IsChatScene(lease.owner.gameObject.scene))
            { lease.Dispose(); stale.Add(pair.Key); continue; }
            lease.HideLegacy();
            lease.visual.TickEditorScenePreview();
        }
        foreach (int key in stale) previews.Remove(key);
        UpgradeOpenChatScenes();
        if (previews.Count > 0) RepaintGameViews();
    }

    private static void RepaintGameViews()
    {
        foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            if (window.GetType().Name == "GameView") window.Repaint();
    }

    private static void SyncLegacyState(CompanionPresentation owner, bool hidden)
    {
        bool changed = hidden ? owner.PrepareEditorLegacyViews() : owner.RestoreEditorLegacyViews();
        if (!changed) return;
        EditorUtility.SetDirty(owner);
        var serialized = new SerializedObject(owner);
        var states = serialized.FindProperty("legacyViews");
        for (int i = 0; i < states.arraySize; i++)
        {
            var view = states.GetArrayElementAtIndex(i).FindPropertyRelative("view").objectReferenceValue;
            if (view != null) EditorUtility.SetDirty(view);
        }
        EditorSceneManager.MarkSceneDirty(owner.gameObject.scene);
    }

    private static void SuspendAndClear() { suspended = true; ClearPreviews(); }
    private static void ClearPreviews()
    {
        foreach (var lease in previews.Values) lease.Dispose();
        previews.Clear();
    }

    [MenuItem("NeEEvA/Interface/Editor Preview/Compact HUD")]
    public static void PreviewHud() => ShowPreviewPage("closed");
    [MenuItem("NeEEvA/Interface/Editor Preview/Text Panel")]
    public static void PreviewText() => ShowPreviewPage("text");
    [MenuItem("NeEEvA/Interface/Editor Preview/Settings Panel")]
    public static void PreviewSettings() => ShowPreviewPage("settings");

    private static void ShowPreviewPage(string page)
    {
        UpgradeOpenChatScenes();
        foreach (var lease in previews.Values)
        {
            lease.visual.SetPreviewPanel(page);
            lease.visual.TickEditorScenePreview();
        }
        RepaintGameViews();
    }

    private static Camera FindPlayerCamera(Scene scene)
    {
        foreach (var root in scene.GetRootGameObjects())
        foreach (var camera in root.GetComponentsInChildren<Camera>(true))
            if (camera.CompareTag("MainCamera")) return camera;
        return null;
    }

    private static void WriteReceipt(PreviewLease lease)
    {
        bool hidden = true;
        foreach (var canvas in lease.legacy) if (canvas != null && canvas.enabled) hidden = false;
        var receipt = new Receipt
        {
            scene = lease.scenePath, unityVersion = Application.unityVersion, updatedUtc = DateTime.UtcNow.ToString("o"),
            editMode = !Application.isPlaying, runtimeComponentInstalled = lease.owner != null,
            visualPreviewActive = lease.root != null && lease.root.activeInHierarchy,
            legacyCanvasesHidden = hidden, legacyCanvasCount = lease.legacy.Length
        };
        string output = Path.Combine(Application.dataPath, "../Logs/CompanionPresentationValidation/live-editor-preview.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output, JsonUtility.ToJson(receipt, true));
    }
}
