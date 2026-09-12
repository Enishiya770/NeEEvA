using System;
using System.Collections.Generic;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Explicit Play-mode room session. Closing the window releases its temporary runtime owners.</summary>
public sealed class ArdyRoomLocomotionWindow : EditorWindow
{
    [SerializeField] private Vrm10Instance avatar;
    [SerializeField] private GameObject importedModel;
    [SerializeField] private Transform environmentRoot;
    [SerializeField] private string endpoint = "http://127.0.0.1:8093";
    private GameObject host;
    private ArdyRoomNavigation navigation;
    private ArdyRoomLocomotionController controller;
    private bool selectingTarget;
    private string message;
    private Vector3 target;
    private bool hasTarget;
    [SerializeField] private float speed = .65f;
    [SerializeField] private List<Transform> excludedVisualRoots = new List<Transform>();

    [MenuItem("Tools/NeEEvA/ARDY Room Locomotion")]
    public static void Open() => GetWindow<ArdyRoomLocomotionWindow>("ARDY Room");

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        EditorApplication.playModeStateChanged += OnPlayMode;
        EditorApplication.update += Refresh;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        EditorApplication.playModeStateChanged -= OnPlayMode;
        EditorApplication.update -= Refresh;
        DisposeSession();
    }

    private void OnPlayMode(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode) DisposeSession();
        Repaint();
    }

    private void Refresh()
    {
        if (controller != null) Repaint();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("ARDY 房间移动", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("进入房间场景的 Play 模式，选择实际角色、它对应的导入模型，以及包含房间地面和家具的环境根节点。构建后在 Scene 视图点选地面目标。", MessageType.Info);
        EditorGUILayout.HelpBox("本阶段仅支持同一高度的平地移动。台阶、下沉客厅边沿、斜坡和无法通过的窄口会被拒绝；不会自动吸附高度或跨越落差。", MessageType.None);
        using (new EditorGUI.DisabledScope(controller != null))
        {
            avatar = (Vrm10Instance)EditorGUILayout.ObjectField("场景中的角色", avatar, typeof(Vrm10Instance), true);
            importedModel = (GameObject)EditorGUILayout.ObjectField("对应的导入 VRM 模型", importedModel, typeof(GameObject), false);
            environmentRoot = (Transform)EditorGUILayout.ObjectField("房间环境根节点", environmentRoot, typeof(Transform), true);
            endpoint = EditorGUILayout.TextField("ARDY 服务", endpoint);
            EditorGUILayout.LabelField("无实体的视觉对象（可选）");
            for (int i = 0; i < excludedVisualRoots.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                excludedVisualRoots[i] = (Transform)EditorGUILayout.ObjectField(excludedVisualRoots[i], typeof(Transform), true);
                if (GUILayout.Button("移除", GUILayout.Width(45))) { excludedVisualRoots.RemoveAt(i); i--; }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("添加水面等纯视觉对象")) excludedVisualRoots.Add(null);
            if (IsSailingMoonRoom() && GUILayout.Button("使用已核实的 SailingMoon 水面配置")) TryAction(ApplySailingMoonVisualExclusion);
        }
        if (!EditorApplication.isPlayingOrWillChangePlaymode)
        {
            using (new EditorGUI.DisabledScope(environmentRoot == null))
                if (GUILayout.Button("准备房间几何（进入 Play 前）")) TryAction(() => {
                    var prepared = ArdyRoomGeometryPreparation.Prepare(environmentRoot);
                    message = "已记录 " + prepared.entries.Length + " 个房间网格引用；可撤销。保存场景可保留此准备结果，然后进入 Play。";
                });
        }
        var geometry = environmentRoot == null ? null : environmentRoot.GetComponent<ArdyRoomGeometryReference>();
        bool geometryReady = geometry != null && geometry.environmentRoot == environmentRoot && geometry.entries != null && geometry.entries.Length > 0;
        if (!geometryReady)
            EditorGUILayout.HelpBox("请先在编辑模式点击“准备房间几何”，再进入 Play。运行中的合批网格不能替代房间原始几何。", MessageType.None);
        else EditorGUILayout.LabelField("房间几何参考", geometry.entries.Length + " 项已准备");
        speed = EditorGUILayout.Slider("步行速度（米/秒）", speed, .2f, .9f);
        using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying || controller != null || !geometryReady))
            if (GUILayout.Button("构建导航并绑定角色")) TryAction(BuildSession);
        if (!EditorApplication.isPlaying)
            EditorGUILayout.HelpBox("此窗口仅在 Play 模式创建临时组件；请先进入运行状态。原隔离预览入口仍可在编辑模式使用。", MessageType.None);
        if (controller != null)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("移动状态", controller.Status, EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("房间几何", navigation.SourceCount + " 个碰撞源 · " + navigation.GeometryProxyCount + " 个可见家具代理");
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(selectingTarget ? "取消选点" : "在 Scene 视图选择地面目标"))
            {
                selectingTarget = !selectingTarget;
                if (selectingTarget) SceneView.lastActiveSceneView?.Focus();
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("停止移动")) { controller.Cancel(); selectingTarget = false; }
            EditorGUILayout.EndHorizontal();
            if (hasTarget)
            {
                EditorGUILayout.Vector3Field("地面目标（世界坐标）", target);
                if (GUILayout.Button("从当前位置重新尝试此目标")) TryAction(() => MoveTo(target));
            }
            if (controller.Route != null)
                EditorGUILayout.LabelField("审核路径", controller.Route.Length.ToString("F2") + " 米 · 青色；生成路径为橙色");
            if (GUILayout.Button("释放本窗口的角色和导航控制")) DisposeSession();
        }
        if (!string.IsNullOrWhiteSpace(message)) EditorGUILayout.HelpBox(message, MessageType.None);
    }

    private void BuildSession()
    {
        // The chat avatar deliberately disables full VRM updates while using its
        // Humanoid, independent eyes and tail. Preserve that supported configuration.
        if (!EditorApplication.isPlaying || avatar == null || !avatar.gameObject.scene.IsValid() || !avatar.gameObject.activeInHierarchy)
            throw new InvalidOperationException("请在 Play 模式选择场景中处于激活状态的角色对象；无需启用 Vrm10Instance 组件。");
        if (environmentRoot == null || !environmentRoot.gameObject.scene.IsValid() || environmentRoot.gameObject.scene != avatar.gameObject.scene)
            throw new InvalidOperationException("请选择与角色同一场景的房间环境根节点。");
        var geometry = environmentRoot.GetComponent<ArdyRoomGeometryReference>();
        if (geometry == null || geometry.environmentRoot != environmentRoot || geometry.entries == null || geometry.entries.Length == 0)
            throw new InvalidOperationException("房间尚未准备原始几何；请退出 Play，点击“准备房间几何”后再运行。");
        if (importedModel == null || !EditorUtility.IsPersistent(importedModel))
            throw new InvalidOperationException("需要 Assets 中对应的导入模型，用于读取真实骨骼静止参考。");
        var imported = importedModel.GetComponentInChildren<Vrm10Instance>(true);
        var reference = ArdyLocomotionAvatarReference.FromImportedPrefab(imported, AssetDatabase.GetAssetPath(importedModel));
        if (reference.avatar != avatar.Vrm) throw new InvalidOperationException("所选导入模型与当前角色不匹配。");
        EnsureAvatarAvailable(avatar, controller);
        DisposeSession();
        try
        {
            host = new GameObject("ARDY explicit room locomotion session") { hideFlags = HideFlags.DontSave };
            SceneManager.MoveGameObjectToScene(host, avatar.gameObject.scene);
            navigation = host.AddComponent<ArdyRoomNavigation>();
            navigation.ExcludedVisualRoots = excludedVisualRoots.FindAll(value => value != null).ToArray();
            // Use the imported stature, never a crouched animated frame, for vertical clearance.
            float stature = (reference.Get(HumanBodyBones.Head).positionInRoot.y - reference.groundY) * avatar.transform.lossyScale.y + .18f;
            navigation.AgentHeight = Mathf.Max(1.1f, stature);
            if (!navigation.Build(environmentRoot, avatar.transform, out string reason))
                throw new InvalidOperationException(reason);
            controller = host.AddComponent<ArdyRoomLocomotionController>();
            controller.Endpoint = endpoint; controller.WalkingSpeed = speed;
            controller.Configure(avatar, reference, navigation);
            message = "已创建本次运行的独立移动会话。关闭窗口会取消请求、释放身体和导航。";
        }
        catch { DisposeSession(); throw; }
    }

    internal static void EnsureAvatarAvailable(Vrm10Instance candidate, ArdyRoomLocomotionController ownedController)
    {
        // Runtime chat can auto-connect before this window opens. Keep ownership reciprocal:
        // a second navigation would also create foreign geometry guards for the same room.
        foreach (var room in FindObjectsOfType<ArdyRoomDialogueBridge>())
            if (room.avatar == candidate && room.IsConnected && room.Controller != ownedController)
                throw new InvalidOperationException("该角色已连接房间聊天呼唤。请先在 ARDY Room Dialogue 窗口断开房间呼唤，再使用地面点选；无需退出 Play。");
        foreach (var other in FindObjectsOfType<ArdyRoomLocomotionController>())
            if (other != ownedController && other.isActiveAndEnabled && other.Avatar == candidate)
                throw new InvalidOperationException("该角色已经由另一份房间移动会话控制。请先释放该会话，再构建地面点选导航。");
    }

    private bool IsSailingMoonRoom()
    {
        if (environmentRoot == null) return false;
        string path = environmentRoot.gameObject.scene.path;
        return path == "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day.unity" ||
            path == "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
    }

    private void ApplySailingMoonVisualExclusion()
    {
        if (!IsSailingMoonRoom()) throw new InvalidOperationException("此配置只适用于已核实的 SailingMoon 房间。");
        Transform water = null;
        foreach (var candidate in environmentRoot.GetComponentsInChildren<Transform>(true))
        {
            if (candidate.name != "水面") continue;
            if (water != null) throw new InvalidOperationException("发现多个同名水面，请手动选择纯视觉对象。");
            water = candidate;
        }
        if (water == null || water.GetComponentInChildren<MeshRenderer>(true) == null || water.GetComponentsInChildren<Collider>(true).Length != 0)
            throw new InvalidOperationException("当前环境与已核实的无碰撞水面不匹配，请手动检查。");
        if (!excludedVisualRoots.Contains(water)) excludedVisualRoots.Add(water);
        message = "已将此房间的纯视觉水面排除于家具代理；现有实体碰撞仍参与导航。";
    }

    private void OnSceneGUI(SceneView scene)
    {
        if (controller == null || !EditorApplication.isPlaying) return;
        DrawRoutes();
        if (!selectingTarget) return;
        var evt = Event.current;
        if (evt.type == EventType.Layout) HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
        if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape)
        { selectingTarget = false; evt.Use(); Repaint(); return; }
        if (evt.type != EventType.MouseDown || evt.button != 0 || evt.alt) return;
        Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
        var hits = Physics.RaycastAll(ray, 1000, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        bool found = false;
        foreach (var hit in hits)
        {
            if (hit.collider.transform.IsChildOf(avatar.transform)) continue;
            if (!hit.collider.transform.IsChildOf(environmentRoot)) continue;
            if (!navigation.TryProjectGround(hit.point, out Vector3 ground, out string reason))
            { message = "该位置不是可用地面 · " + reason; found = true; break; }
            target = ground; hasTarget = true; selectingTarget = false;
            TryAction(() => MoveTo(target));
            found = true; break;
        }
        if (!found) message = "点击位置未命中所选房间的实体地面。";
        evt.Use(); Repaint(); scene.Repaint();
    }

    private void MoveTo(Vector3 value)
    {
        controller.WalkingSpeed = speed;
        controller.MoveTo(value);
        message = "目标已提交；生成结果会先经过完整路径审核，再开始移动。";
    }

    private void DrawRoutes()
    {
        Color before = Handles.color;
        try
        {
            var route = controller.Route;
            if (route != null)
            {
                Handles.color = new Color(.1f, .85f, .9f);
                for (int i = 1; i < route.Corners.Length; i++)
                    Handles.DrawAAPolyLine(4, route.Corners[i - 1] + Vector3.up * .025f, route.Corners[i] + Vector3.up * .025f);
            }
            var clip = controller.LastClip;
            var player = controller.Player;
            if (clip != null && player != null && player.Clip == clip)
            {
                Handles.color = new Color(1, .5f, .1f);
                var points = new Vector3[clip.frames.Length];
                for (int i = 0; i < points.Length; i++)
                {
                    Vector3 source = clip.frames[i].rootPosition; source.y = 0;
                    points[i] = player.SourceRootToWorld(source) + Vector3.up * .04f;
                }
                Handles.DrawAAPolyLine(3, points);
            }
            if (hasTarget)
            {
                Handles.color = Color.cyan;
                Handles.DrawWireDisc(target + Vector3.up * .02f, Vector3.up, .15f);
                Handles.Label(target + Vector3.up * .1f, "ARDY 目标");
            }
        }
        catch (InvalidOperationException) { /* A session may release its anchor during an Editor repaint. */ }
        finally { Handles.color = before; }
    }

    private void DisposeSession()
    {
        selectingTarget = false;
        if (controller != null) controller.Cancel();
        if (navigation != null) navigation.Clear();
        if (host != null) DestroyImmediate(host);
        host = null; controller = null; navigation = null;
        SceneView.RepaintAll();
    }

    private void TryAction(Action action)
    {
        try { action(); }
        catch (Exception error) { message = error.Message; Debug.LogWarning("[ARDY room preview] " + error.Message); }
    }
}
