using System;
using System.Linq;
using NeEEvA.Motion;
using NeEEvA.Player;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Prepare serialized runtime bindings; closing this window does not stop the scene-owned session.</summary>
public sealed class ArdyRoomDialogueWindow : EditorWindow
{
    [SerializeField] private ChatSample chat;
    [SerializeField] private Vrm10Instance avatar;
    [SerializeField] private GameObject importedModel;
    [SerializeField] private Transform environmentRoot, userHead;
    [SerializeField] private Transform[] excludedVisualRoots = Array.Empty<Transform>();
    [SerializeField] private string endpoint = "http://127.0.0.1:8093";
    [SerializeField] private float distance = 1, speed = .65f;
    private string message;

    [MenuItem("Tools/NeEEvA/ARDY Room Dialogue")]
    public static void Open() => GetWindow<ArdyRoomDialogueWindow>("ARDY Room Chat");
    private void OnInspectorUpdate() { if (Application.isPlaying) Repaint(); }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("房间呼唤与停止", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("选择聊天角色、房间和用户头部位置。配置保存在场景组件中，关闭窗口仍可运行。沿用已验证的同层地形；连续跟随尚未启用。", MessageType.Info);
        if (GUILayout.Button("查找当前场景配置")) Try(FindConfiguration);
        chat = (ChatSample)EditorGUILayout.ObjectField("聊天组件", chat, typeof(ChatSample), true);
        avatar = (Vrm10Instance)EditorGUILayout.ObjectField("场景角色", avatar, typeof(Vrm10Instance), true);
        importedModel = (GameObject)EditorGUILayout.ObjectField("对应导入 VRM", importedModel, typeof(GameObject), false);
        environmentRoot = (Transform)EditorGUILayout.ObjectField("房间环境根节点", environmentRoot, typeof(Transform), true);
        userHead = (Transform)EditorGUILayout.ObjectField("用户头部 / Player Camera", userHead, typeof(Transform), true);
        EditorGUILayout.HelpBox("这是用户视点，不是角色眼睛。程序会检查下方地面；悬空视点、其他楼层或丢失的 VR 追踪不能作为走近目标。", MessageType.None);
        endpoint = EditorGUILayout.TextField("ARDY 服务", endpoint);
        distance = EditorGUILayout.Slider("停在用户附近（米）", distance, .8f, 1.5f);
        speed = EditorGUILayout.Slider("行走速度（米/秒）", speed, .2f, .9f);
        if (excludedVisualRoots == null) excludedVisualRoots = Array.Empty<Transform>();
        for (int i = 0; i < excludedVisualRoots.Length; i++)
            excludedVisualRoots[i] = (Transform)EditorGUILayout.ObjectField("无实体视觉对象 " + (i + 1), excludedVisualRoots[i], typeof(Transform), true);
        if (GUILayout.Button("添加纯视觉对象")) Array.Resize(ref excludedVisualRoots, excludedVisualRoots.Length + 1);
        if (GUILayout.Button("使用已核实的 SailingMoon 水面配置")) Try(SelectWater);
        using (new EditorGUI.DisabledScope(Application.isPlaying))
        {
            if (GUILayout.Button("准备房间几何并配置自动连接")) Try(() => Configure(true));
        }
        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            if (GUILayout.Button("连接本次运行的聊天呼唤")) Try(() => Configure(false));
            var bridge = chat != null ? chat.GetComponent<ArdyRoomDialogueBridge>() : null;
            using (new EditorGUI.DisabledScope(bridge == null || !bridge.IsConnected))
            {
                if (GUILayout.Button("停止移动")) bridge.StopMovement();
                if (GUILayout.Button("断开房间呼唤")) bridge.Disconnect();
            }
            if (bridge != null)
            {
                EditorGUILayout.LabelField("空间状态", bridge.Phase + " · " + bridge.Status, EditorStyles.wordWrappedLabel);
                EditorGUILayout.HelpBox(bridge.DiagnosticSummary, MessageType.None);
                if (GUILayout.Button("复制最近 12 次动作诊断"))
                    EditorGUIUtility.systemCopyBuffer = bridge.RecentDiagnosticText;
                EditorGUILayout.HelpBox("诊断是当次执行观测；地面高度、命中物体和失败阶段会同时写入 Unity 日志。桌面相机需位于同层地面上方，垂直调整视点不会单独触发移动目标取消。", MessageType.None);
            }
        }
        if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
    }

    private void FindConfiguration()
    {
        var chats = FindObjectsOfType<ChatSample>(true).Where(x => x.gameObject.scene.IsValid() && x.gameObject.activeInHierarchy).ToArray();
        if (chats.Length == 1) chat = chats[0];
        var existing = chat != null ? chat.GetComponent<ArdyRoomDialogueBridge>() : null;
        if (existing != null)
        {
            avatar = existing.avatar; environmentRoot = existing.environmentRoot; userHead = existing.userHead;
            excludedVisualRoots = existing.excludedVisualRoots; endpoint = existing.serviceUrl;
            distance = existing.approachDistance; speed = existing.walkingSpeed;
        }
        var avatars = FindObjectsOfType<Vrm10Instance>(true).Where(x => x.gameObject.scene.IsValid() && x.gameObject.activeInHierarchy).ToArray();
        if (avatar == null && avatars.Length == 1) avatar = avatars[0];
        var heads = FindObjectsOfType<PlayerCameraController>(true).Where(x => x.gameObject.scene.IsValid() && x.gameObject.activeInHierarchy).ToArray();
        if (userHead == null && heads.Length == 1) userHead = heads[0].transform;
        var rooms = FindObjectsOfType<ArdyRoomGeometryReference>(true).Where(x => x.environmentRoot != null && x.gameObject.activeInHierarchy).ToArray();
        if (environmentRoot == null && rooms.Length == 1) environmentRoot = rooms[0].environmentRoot;
        if (importedModel == null && avatar != null)
        {
            string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(avatar.gameObject);
            if (!string.IsNullOrEmpty(path)) importedModel = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }
        message = "已填写唯一可识别的场景引用；空项请明确选择。";
    }

    private void SelectWater()
    {
        if (environmentRoot == null ||
            !(environmentRoot.gameObject.scene.path.EndsWith("/NeEEvA_SailingMoon_Day.unity", StringComparison.Ordinal) ||
              environmentRoot.gameObject.scene.path.EndsWith("/NeEEvA_SailingMoon_Day_Chat.unity", StringComparison.Ordinal)))
            throw new InvalidOperationException("此配置仅适用于已核实的 SailingMoon 房间。");
        var water = environmentRoot.GetComponentsInChildren<Transform>(true).Where(x => x.name == "水面").ToArray();
        if (water.Length != 1 || water[0].GetComponentInChildren<MeshRenderer>(true) == null || water[0].GetComponentsInChildren<Collider>(true).Length != 0)
            throw new InvalidOperationException("水面不符合已核实的无实体配置，请手动检查。");
        excludedVisualRoots = (excludedVisualRoots ?? Array.Empty<Transform>()).Where(x => x != null).Concat(water).Distinct().ToArray();
        message = "已选择该房间的纯视觉水面。";
    }

    private void Configure(bool persistent)
    {
        if (chat == null || avatar == null || importedModel == null || environmentRoot == null || userHead == null)
            throw new InvalidOperationException("请完整选择聊天、场景角色、导入模型、房间和用户头部。");
        if (EditorUtility.IsPersistent(chat) || EditorUtility.IsPersistent(avatar) || !EditorUtility.IsPersistent(importedModel))
            throw new InvalidOperationException("聊天与角色需要场景实例；导入模型需要 Assets 资产。");
        var imported = importedModel.GetComponentInChildren<Vrm10Instance>(true);
        var reference = ArdyLocomotionAvatarReference.FromImportedPrefab(imported, AssetDatabase.GetAssetPath(importedModel));
        if (reference.avatar != avatar.Vrm) throw new InvalidOperationException("导入模型与当前角色不匹配。");
        if (persistent) ArdyRoomGeometryPreparation.Prepare(environmentRoot);
        var bridge = chat.GetComponent<ArdyRoomDialogueBridge>();
        if (bridge == null) bridge = persistent ? Undo.AddComponent<ArdyRoomDialogueBridge>(chat.gameObject) : chat.gameObject.AddComponent<ArdyRoomDialogueBridge>();
        if (Application.isPlaying) bridge.Disconnect("room-configured");
        if (persistent) Undo.RecordObject(bridge, "Configure room conversation");
        bridge.chat = chat; bridge.avatar = avatar; bridge.environmentRoot = environmentRoot; bridge.userHead = userHead;
        bridge.importedReference = reference; bridge.excludedVisualRoots = excludedVisualRoots;
        bridge.serviceUrl = endpoint; bridge.approachDistance = distance; bridge.walkingSpeed = speed;
        bridge.connectOnStart = true; bridge.enabled = true;
        if (persistent)
        {
            EditorUtility.SetDirty(bridge);
            PrefabUtility.RecordPrefabInstancePropertyModifications(bridge);
            EditorSceneManager.MarkSceneDirty(chat.gameObject.scene);
            message = "已添加可撤销的运行时配置。保存场景后，每次 Play 会自动连接；无需再构建旧点选会话。";
        }
        else { bridge.Connect(); message = "本次运行已连接。可以说“过来，到我身边”或“停下”。"; }
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception error) { message = error.Message; Debug.LogWarning("[Room/Setup] " + error.Message); }
    }
}
