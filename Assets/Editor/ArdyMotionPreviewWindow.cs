using System;
using System.Linq;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEngine;

/// <summary>Temporary controls for the avatar already running in the chat scene.</summary>
public sealed class ArdyMotionPreviewWindow : EditorWindow
{
    private Vrm10Instance target;
    private ArdyMotionPlayer player;
    private string message;

    [MenuItem("Tools/NeEEvA/ARDY Motion Preview")]
    public static void Open() => GetWindow<ArdyMotionPreviewWindow>("ARDY Motion");

    private void OnEnable() => EditorApplication.update += RefreshStatus;
    private void OnDisable()
    {
        EditorApplication.update -= RefreshStatus;
        if (player != null && Application.isPlaying) player.Stop();
    }

    private void RefreshStatus()
    {
        if (Application.isPlaying && player != null) Repaint();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("NeEEvA 动作预览", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("先正常运行聊天场景，再绑定角色。这里的播放器只在本次运行期间添加；结束运行后不会写入场景。", MessageType.Info);
        EditorGUILayout.HelpBox("左挥手为生成样例，右挥手为其镜像；轻点头、轻摇头是小幅往返后回正的受控动作。", MessageType.None);
        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            var selected = (Vrm10Instance)EditorGUILayout.ObjectField("VRM 角色", target, typeof(Vrm10Instance), true);
            if (selected != target)
            {
                if (player != null) player.Stop();
                player = null;
                target = selected;
            }
            if (GUILayout.Button("查找当前场景角色")) FindTarget();
            if (GUILayout.Button("绑定角色")) TryAction(BindTarget);

            using (new EditorGUI.DisabledScope(player == null))
            {
                EditorGUILayout.Space();
                EditorGUILayout.BeginHorizontal();
                ClipButton("左手挥手", "left-wave", ArdyMotionMask.LeftArm);
                ClipButton("右手挥手", "right-wave", ArdyMotionMask.RightArm);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                ClipButton("轻点头", "nod", ArdyMotionMask.Head);
                ClipButton("轻摇头", "shake-head", ArdyMotionMask.Head);
                EditorGUILayout.EndHorizontal();
                if (GUILayout.Button("停止并回到待机")) TryAction(() => player.Stop());
            }
        }
        if (!Application.isPlaying) EditorGUILayout.HelpBox("此面板不会自动进入运行模式，也不会启动聊天或网络服务。", MessageType.None);
        if (player != null)
        {
            EditorGUILayout.LabelField("状态", player.Status ?? string.Empty);
            EditorGUILayout.LabelField("动作播放", player.IsPlaying ? "进行中" : "待机");
            EditorGUILayout.LabelField("绑定骨骼", player.BoundBoneCount.ToString());
        }
        if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
    }

    private void ClipButton(string label, string resource, ArdyMotionMask mask)
    {
        if (!GUILayout.Button(label)) return;
        TryAction(() =>
        {
            var clip = Resources.Load<TextAsset>("ARDY/" + resource);
            if (clip == null) throw new InvalidOperationException("缺少动作文件：Resources/ARDY/" + resource);
            player.Play(clip, mask);
            message = label + "；可在播放中切换动作或停止。";
        });
    }

    private void FindTarget()
    {
        var candidates = FindObjectsOfType<Vrm10Instance>(true)
            .Where(v => v.gameObject.scene.IsValid() && v.gameObject.scene.isLoaded && v.gameObject.activeInHierarchy)
            .ToArray();
        if (candidates.Length == 1)
        {
            if (player != null && target != candidates[0]) player.Stop();
            target = candidates[0];
            player = target.GetComponent<ArdyMotionPlayer>();
            message = "找到 " + target.name + "，点击绑定角色。";
        }
        else message = candidates.Length == 0 ? "未找到活动的 VRM 角色。" : "场景中有多个 VRM，请把目标角色拖入上方字段。";
    }

    private void BindTarget()
    {
        if (target == null) FindTarget();
        if (target == null || !target.gameObject.scene.IsValid() || EditorUtility.IsPersistent(target))
            throw new InvalidOperationException("请选择场景中实际运行的角色实例。");
        player = target.GetComponent<ArdyMotionPlayer>();
        if (player == null) player = target.gameObject.AddComponent<ArdyMotionPlayer>();
        player.Bind(target);
        message = "已绑定 " + target.name + "。嘴型、眨眼和视线继续由原有系统驱动。";
    }

    private void TryAction(Action action)
    {
        try { action(); }
        catch (Exception error) { message = error.Message; Debug.LogException(error); }
    }
}
