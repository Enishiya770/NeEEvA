using System;
using System.Linq;
using UniVRM10;
using UnityEditor;
using UnityEngine;

/// <summary>Opt-in runtime wiring only. It does not edit or save the user's scene or start any service.</summary>
public sealed class ArdyDialogueMotionWindow : EditorWindow
{
    private ChatSample chat;
    private Vrm10Instance avatar;
    private Transform interactionTarget;
    private ArdyDialogueMotionBridge bridge;
    private bool generated = true;
    private bool semanticPlanning;
    private string serviceUrl = "http://127.0.0.1:8093";
    private string message;

    [MenuItem("Tools/NeEEvA/ARDY Dialogue Motion")]
    public static void Open() => GetWindow<ArdyDialogueMotionWindow>("ARDY Dialogue");

    private void OnInspectorUpdate() { if (Application.isPlaying) Repaint(); }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("对话动作", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("正常运行聊天场景后连接。Qwen 决定动作，台词继续走现有语音流程。用户开口会打断运动；明确请求的持姿可保留至后续动作、停止或超时。连接只在本次运行有效。", MessageType.Info);
        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            chat = (ChatSample)EditorGUILayout.ObjectField("聊天组件", chat, typeof(ChatSample), true);
            avatar = (Vrm10Instance)EditorGUILayout.ObjectField("VRM 角色", avatar, typeof(Vrm10Instance), true);
            interactionTarget = (Transform)EditorGUILayout.ObjectField("交流对象位置（可留空）", interactionTarget, typeof(Transform), true);
            EditorGUILayout.HelpBox("掌心朝向交流对象的位置。留空时使用主相机；无主相机时使用角色前方。", MessageType.None);
            generated = EditorGUILayout.Toggle("允许生成其他上身动作", generated);
            using (new EditorGUI.DisabledScope(!generated))
                semanticPlanning = EditorGUILayout.Toggle("实验：意图、约束与节奏规划", semanticPlanning);
            if (semanticPlanning && generated)
                EditorGUILayout.HelpBox("新规划仍会遗漏明确要求或误解掌向。本次连接启用实验模式，便于检查动作和后续纠正；尚未通过自然度与泛化验收。", MessageType.Info);
            serviceUrl = EditorGUILayout.TextField("动作服务", serviceUrl);
            if (GUILayout.Button("查找当前对话与角色")) FindTargets();
            if (GUILayout.Button("连接对话动作")) TryAction(Connect);
            using (new EditorGUI.DisabledScope(bridge == null))
            {
                if (GUILayout.Button("断开并回待机")) TryAction(() => bridge.Disconnect());
            }
        }
        EditorGUILayout.HelpBox("四种基本手势继续使用已验收版本。姿态与局部动作组合在本地执行；自由生成需要动作服务。此面板不会启动服务。", MessageType.None);
        if (bridge != null) EditorGUILayout.LabelField("状态", bridge.Status ?? "");
        if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
    }

    private void FindTargets()
    {
        var chats = FindObjectsOfType<ChatSample>().Where(c => c.gameObject.scene.IsValid()).ToArray();
        var avatars = FindObjectsOfType<Vrm10Instance>(true).Where(v => v.gameObject.scene.IsValid() &&
            v.gameObject.scene.isLoaded && v.gameObject.activeInHierarchy).ToArray();
        if (chats.Length == 1) chat = chats[0];
        if (avatars.Length == 1) avatar = avatars[0];
        message = chats.Length == 1 && avatars.Length == 1
            ? "已找到当前对话与角色，点击连接。" : "场景中的对话或角色不唯一，请手动选择。";
    }

    private void Connect()
    {
        if (chat == null || avatar == null) FindTargets();
        if (chat == null || avatar == null || EditorUtility.IsPersistent(chat) || EditorUtility.IsPersistent(avatar))
            throw new InvalidOperationException("请选择当前运行场景里的聊天组件与角色。");
        if (bridge != null) bridge.Disconnect("preview-reconnected");
        bridge = chat.GetComponent<ArdyDialogueMotionBridge>();
        if (bridge == null) bridge = chat.gameObject.AddComponent<ArdyDialogueMotionBridge>();
        bridge.enabled = true;
        bridge.allowGeneratedMotion = generated;
        bridge.serviceUrl = serviceUrl;
        bridge.interactionTarget = interactionTarget;
        chat.ConfigureSemanticMotionPlanning(generated && semanticPlanning);
        bridge.Bind(chat, avatar);
        message = semanticPlanning && generated
            ? "已连接实验规划；可测试不同动作及后续纠正，留意明确要求是否完整保留。"
            : "已连接原动作协议；可以直接描述交流意图，也可以指定姿态和掌向。";
    }

    private void TryAction(Action action)
    {
        try { action(); }
        catch (Exception error) { message = error.Message; Debug.LogException(error); }
    }
}
