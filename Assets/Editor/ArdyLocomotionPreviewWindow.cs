using System;
using System.Collections.Generic;
using System.IO;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEngine;

/// <summary>Full-body locomotion review on an isolated model clone; never changes a chat scene.</summary>
public sealed class ArdyLocomotionPreviewWindow : EditorWindow
{
    private GameObject model;
    private string clipPath = "", message;
    private PreviewRenderUtility preview;
    private GameObject instance;
    private ArdyLocomotionPlayer player;
    private ArdyLocomotionClip clip;
    private readonly List<Material> materials = new List<Material>();
    private bool running;
    private double lastUpdate;
    private float seconds, cameraYaw = 35, cameraPitch = 18, zoom = 1;
    private Vector3 center;
    private float viewSize = 2;

    [MenuItem("Tools/NeEEvA/ARDY Locomotion Preview")]
    public static void Open() => GetWindow<ArdyLocomotionPreviewWindow>("ARDY Locomotion");

    private void OnEnable()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += OnPlayMode;
        lastUpdate = EditorApplication.timeSinceStartup;
    }

    private void OnDisable()
    {
        EditorApplication.update -= Tick;
        EditorApplication.playModeStateChanged -= OnPlayMode;
        DisposePreview();
    }

    private void OnPlayMode(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode) DisposePreview();
    }

    private void Tick()
    {
        double now = EditorApplication.timeSinceStartup;
        float delta = (float)Math.Min(.1, now - lastUpdate);
        lastUpdate = now;
        if (!running || player == null || clip == null) return;
        TryAction(() =>
        {
            seconds = Mathf.Min(seconds + delta, clip.LastSampleSeconds);
            player.SampleAt(seconds);
            if (seconds >= clip.LastSampleSeconds) running = false;
            Repaint();
        });
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("ARDY 全身移动验证", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("在独立预览场景中克隆模型。橙色为实际生成路径，青色为目标路径；播放不会修改聊天角色或保存场景。", MessageType.Info);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
        {
            model = (GameObject)EditorGUILayout.ObjectField("导入的 VRM 模型", model, typeof(GameObject), false);
            EditorGUILayout.BeginHorizontal();
            clipPath = EditorGUILayout.TextField("生成结果 JSON", clipPath);
            if (GUILayout.Button("选择", GUILayout.Width(50)))
            {
                string selected = EditorUtility.OpenFilePanel("选择 ARDY locomotion JSON", Path.GetFullPath("Server/ARDY/runtime"), "json");
                if (!string.IsNullOrEmpty(selected)) clipPath = selected;
            }
            EditorGUILayout.EndHorizontal();
            if (GUILayout.Button("加载隔离预览")) TryAction(LoadPreview);
        }
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            EditorGUILayout.HelpBox("请退出 Play 模式后使用此独立预览。运行时的聊天角色不会被绑定。", MessageType.None);
        if (player != null && clip != null)
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(running ? "暂停" : "播放"))
            {
                if (seconds >= clip.LastSampleSeconds) { seconds = 0; player.SampleAt(0); }
                running = !running; lastUpdate = EditorApplication.timeSinceStartup;
            }
            if (GUILayout.Button("回到首帧")) { running = false; seconds = 0; TryAction(() => player.SampleAt(0)); }
            if (GUILayout.Button("释放预览")) DisposePreview();
            EditorGUILayout.EndHorizontal();
            if (player != null)
            {
                EditorGUI.BeginChangeCheck();
                float next = EditorGUILayout.Slider("时间（秒）", seconds, 0, clip.LastSampleSeconds);
                if (EditorGUI.EndChangeCheck()) { running = false; seconds = next; TryAction(() => player.SampleAt(seconds)); }
                cameraYaw = EditorGUILayout.Slider("观察方位", cameraYaw, -180, 180);
                zoom = EditorGUILayout.Slider("缩放", zoom, .4f, 2.5f);
                EditorGUILayout.LabelField("角色/源骨架比例", player.MotionScale.ToString("F3"));
                EditorGUILayout.LabelField("模型接触标记", "左 " + clip.SampleContact(true, seconds).ToString("F2") + " / 右 " + clip.SampleContact(false, seconds).ToString("F2"));
                Rect rect = GUILayoutUtility.GetRect(100, 10000, 240, 10000, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                if (Event.current.type == EventType.Repaint) Render(rect);
            }
        }
        if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.None);
    }

    private void LoadPreview()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Use the isolated preview outside Play mode.");
        if (model == null || !EditorUtility.IsPersistent(model)) throw new InvalidOperationException("请选择 Assets 中已导入的 VRM 模型资产。");
        string modelPath = AssetDatabase.GetAssetPath(model);
        var importedVrm = model.GetComponentInChildren<Vrm10Instance>(true);
        var reference = ArdyLocomotionAvatarReference.FromImportedPrefab(importedVrm, modelPath);
        var parsed = ArdyLocomotionClip.Parse(File.ReadAllText(Path.GetFullPath(clipPath)));
        DisposePreview();
        try
        {
            preview = new PreviewRenderUtility();
            preview.camera.clearFlags = CameraClearFlags.SolidColor;
            preview.camera.backgroundColor = new Color(.16f, .19f, .22f);
            preview.camera.orthographic = true;
            preview.camera.nearClipPlane = .01f; preview.camera.farClipPlane = 100;
            preview.lights[0].intensity = 1.1f; preview.lights[0].transform.rotation = Quaternion.Euler(35, 25, 0);
            preview.lights[1].intensity = .5f; preview.lights[1].transform.rotation = Quaternion.Euler(340, 210, 0);
            instance = Instantiate(model);
            instance.name = "ARDY isolated locomotion preview";
            instance.hideFlags = HideFlags.HideAndDontSave;
            preview.AddSingleGO(instance);
            var vrm = instance.GetComponentInChildren<Vrm10Instance>(true);
            vrm.enabled = false;
            foreach (var animator in instance.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
            foreach (var behaviour in instance.GetComponentsInChildren<ArdyMotionPlayer>(true)) behaviour.enabled = false;
            foreach (var behaviour in instance.GetComponentsInChildren<ArdyLiveMotionController>(true)) behaviour.enabled = false;
            player = vrm.gameObject.AddComponent<ArdyLocomotionPlayer>();
            player.Bind(vrm, reference);
            clip = parsed; player.Play(clip); player.SampleAt(0);
            seconds = 0; running = false;
            var bounds = new Bounds(player.SourceRootToWorld(clip.frames[0].rootPosition), Vector3.zero);
            for (int i = 0; i < clip.frames.Length; i++)
            {
                bounds.Encapsulate(player.SourceRootToWorld(clip.frames[i].rootPosition));
                bounds.Encapsulate(player.SourceRootToWorld(clip.targets[i].rootPosition));
            }
            float height = Vector3.Distance(vrm.Humanoid.GetBoneTransform(HumanBodyBones.Head).position,
                vrm.Humanoid.GetBoneTransform(HumanBodyBones.LeftFoot).position);
            center = bounds.center;
            viewSize = Mathf.Max(1, bounds.extents.magnitude + height * .65f);
            AddPath(false, new Color(1, .49f, .15f));
            AddPath(true, new Color(.12f, .83f, .87f));
            message = "真实源根位移与腿部旋转；未进行脚底 IK、路径吸附或姿态修正。末帧保持供检查。参考模型：" + modelPath;
        }
        catch { DisposePreview(); throw; }
    }

    private void AddPath(bool targets, Color color)
    {
        var go = new GameObject(targets ? "Target path" : "Generated path");
        go.hideFlags = HideFlags.HideAndDontSave;
        preview.AddSingleGO(go);
        var line = go.AddComponent<LineRenderer>();
        var material = new Material(Shader.Find("Unlit/Color")) { color = color, hideFlags = HideFlags.HideAndDontSave };
        materials.Add(material); line.sharedMaterial = material;
        line.widthMultiplier = .012f; line.useWorldSpace = true;
        line.positionCount = clip.frames.Length;
        float ground = player.SourceRootToWorld(new Vector3(0, 0, 0)).y;
        for (int i = 0; i < clip.frames.Length; i++)
        {
            Vector3 p = player.SourceRootToWorld(targets ? clip.targets[i].rootPosition : clip.frames[i].rootPosition);
            p.y = ground + (targets ? .008f : .018f); line.SetPosition(i, p);
        }
    }

    private void Render(Rect rect)
    {
        if (preview == null || rect.width < 1 || rect.height < 1) return;
        preview.BeginPreview(rect, GUIStyle.none);
        preview.camera.orthographicSize = viewSize / zoom;
        preview.camera.transform.rotation = Quaternion.Euler(cameraPitch, cameraYaw, 0);
        preview.camera.transform.position = center - preview.camera.transform.forward * 15;
        preview.Render();
        GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.StretchToFill, false);
    }

    private void DisposePreview()
    {
        running = false;
        if (player != null) player.RestorePreview();
        player = null; clip = null; instance = null;
        preview?.Cleanup(); preview = null;
        foreach (var material in materials) if (material != null) DestroyImmediate(material);
        materials.Clear();
    }

    private void TryAction(Action action)
    {
        try { action(); }
        catch (Exception error) { running = false; message = error.Message; Debug.LogException(error); }
    }
}
