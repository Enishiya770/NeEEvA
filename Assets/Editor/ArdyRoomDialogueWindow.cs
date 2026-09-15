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
    [SerializeField] private bool showSceneDiagnostics = true, showTimingDetails, showLastObservation;
    private Vector2 scroll;
    private ArdyRoomDialogueBridge observedBridge;
    private ArdyRoomDialogueBridge.CurrentTargetEvidence currentEvidence;
    private double nextObservationAt;
    private string observationError;
    private string message;

    [MenuItem("Tools/NeEEvA/ARDY Room Dialogue")]
    public static void Open() => GetWindow<ArdyRoomDialogueWindow>("ARDY Room Chat");
    private void OnEnable() => SceneView.duringSceneGui += OnSceneGUI;
    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        currentEvidence = null; observedBridge = null;
        SceneView.RepaintAll();
    }
    private void OnInspectorUpdate()
    {
        if (!Application.isPlaying)
        { currentEvidence = null; observedBridge = null; return; }
        ObserveCurrentState();
        Repaint();
    }

    private void ObserveCurrentState(bool force = false)
    {
        var bridge = chat != null ? chat.GetComponent<ArdyRoomDialogueBridge>() : null;
        if (observedBridge != bridge)
        { currentEvidence = null; observationError = null; observedBridge = bridge; nextObservationAt = 0; }
        if (bridge == null || !force && EditorApplication.timeSinceStartup < nextObservationAt) return;
        nextObservationAt = EditorApplication.timeSinceStartup + .25;
        try
        {
            // The monitor never replans, submits a target, or changes body/navigation ownership.
            currentEvidence = bridge.PeekCurrentTargetEvidence();
            observationError = null;
        }
        catch (Exception error)
        { currentEvidence = null; observationError = "当前观测暂不可用：" + error.Message; }
        if (showSceneDiagnostics) SceneView.RepaintAll();
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.LabelField("房间呼唤、跟随与停止", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("选择聊天角色、房间和用户头部位置。配置保存在场景组件中，关闭窗口仍可运行。持续跟随会更新路径与步速，并检查同层地面和身体净距。", MessageType.Info);
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
                if (GUILayout.Button("开始跟随用户")) Try(() => {
                    var target = bridge.ReadCurrentTargetEvidence(true);
                    if (!bridge.FollowForTarget("editor-follow-" + Guid.NewGuid().ToString("N"), target.currentTargetRevision, out string failure))
                        throw new InvalidOperationException(failure);
                });
                if (GUILayout.Button("停止移动")) bridge.StopMovement();
                if (GUILayout.Button("断开房间呼唤")) bridge.Disconnect();
            }
            if (bridge != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("当前运行观测", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("任务", bridge.TaskMode + (bridge.IsFollowing ? " · 持续跟随仍启用" : ""));
                DrawCurrentDiagnostics(bridge);
                DrawPoseRecording(bridge);
                EditorGUI.BeginChangeCheck();
                showSceneDiagnostics = EditorGUILayout.ToggleLeft("在 Scene 显示空间诊断", showSceneDiagnostics);
                if (EditorGUI.EndChangeCheck()) SceneView.RepaintAll();
                EditorGUILayout.HelpBox("绿色：已验证的用户地面；青色：当前导航路线和角色目标；黄色：角色中心需避开的用户净距范围；红叉：实际记录的上次检查拒绝点。地面可用不代表已完成动作生成。", MessageType.None);
                using (new EditorGUI.DisabledScope(!bridge.IsConnected || bridge.ReservesBody))
                    if (GUILayout.Button("检查当前地面与路线（不移动角色）")) Try(() => {
                        bridge.ReadCurrentTargetEvidence(true);
                        ObserveCurrentState(true);
                    });
                showLastObservation = EditorGUILayout.Foldout(showLastObservation, "最近执行记录（历史）", true);
                if (showLastObservation)
                {
                    EditorGUILayout.HelpBox("以下是过去一次执行的记录；当前能否通行以上方当前观测为准。", MessageType.None);
                    EditorGUILayout.HelpBox(bridge.DiagnosticSummary, MessageType.None);
                    var lastStream = currentEvidence?.streamTelemetry;
                    if (lastStream != null && !string.IsNullOrEmpty(lastStream.lastFailureReason))
                        EditorGUILayout.HelpBox("最近动作流异常（历史） · " + FailureCategoryLabel(lastStream.lastFailureCategory) +
                            "\n" + lastStream.lastFailureReason, MessageType.None);
                    DrawLastRejection(currentEvidence);
                    if (GUILayout.Button("复制最近 12 次动作诊断"))
                        EditorGUIUtility.systemCopyBuffer = bridge.RecentDiagnosticText;
                }
            }
        }
        if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
        EditorGUILayout.EndScrollView();
    }

    private void DrawPoseRecording(ArdyRoomDialogueBridge bridge)
    {
        var recorder = bridge.Controller == null ? null : bridge.Controller.GetComponent<ArdyRoomPoseRecorder>();
        if (recorder == null) return;
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("走姿记录", EditorStyles.boldLabel);
        bool enabled = EditorGUILayout.ToggleLeft("保留最近约 10 秒的源动作与实际骨骼", recorder.RecordingEnabled);
        if (enabled != recorder.RecordingEnabled) recorder.RecordingEnabled = enabled;
        EditorGUILayout.LabelField("当前记录", $"{recorder.SampleCount} 帧 · 动作响应 {recorder.RawPayloadUtf8Bytes / 1024f:F0} KiB");
        EditorGUILayout.HelpBox("出现异常走姿或动作被拒绝后点击保存。会同时保留最近的拒绝原因、对应动作数据和姿态历史诊断；停止后的动作记录不会持续被待机覆盖。", MessageType.None);
        using (new EditorGUI.DisabledScope(recorder.SampleCount == 0 && bridge.Controller.LatestRejectedPrefixOrdinal == 0 &&
            bridge.Controller.LastRejectedStreamHistory == null))
            if (GUILayout.Button("保存最近异常走姿")) Try(() => {
                string path = recorder.ExportRecent();
                EditorGUIUtility.systemCopyBuffer = path;
                message = "已保存最近走姿，路径已复制：\n" + path;
            });
        if (!string.IsNullOrEmpty(recorder.LastError))
            EditorGUILayout.HelpBox("走姿记录已暂停：" + recorder.LastError + "。移动控制不受影响。", MessageType.Warning);
    }

    private void DrawCurrentDiagnostics(ArdyRoomDialogueBridge bridge)
    {
        if (observedBridge != bridge || currentEvidence == null)
        {
            EditorGUILayout.HelpBox(observationError ?? "等待当前房间观测。", MessageType.None);
            return;
        }
        var evidence = currentEvidence;
        var controller = bridge.Controller;
        var telemetry = evidence.streamTelemetry;
        bool currentStream = controller != null && telemetry != null &&
            telemetry.operationRevision == controller.OperationRevision && controller.IsContinuousFollowActive;
        string execution;
        MessageType executionType = MessageType.None;
        if (!evidence.connected) execution = "房间会话未连接。";
        else if (currentStream && telemetry.recovering)
        {
            execution = "动作供给不足 · 正在缓冲恢复\n角色保持平滑交接，跟随任务仍启用。";
            executionType = MessageType.Warning;
        }
        else if (currentStream && telemetry.prebuffering)
            execution = "正在准备连续动作缓冲；准备完成后开始移动。";
        else if (bridge.Phase == "follow-paused")
        {
            execution = (string.IsNullOrEmpty(evidence.activeBlockingCategory) ? "持续跟随已暂停" :
                FailureCategoryLabel(evidence.activeBlockingCategory) + " · 持续跟随已暂停") + "；用户位置变化后会重新检查。";
            executionType = MessageType.Warning;
        }
        else if (bridge.Phase == "follow-waiting") execution = "正在用户附近等待；持续跟随仍启用。";
        else if (controller != null && controller.IsReturningOwnedPose) execution = "正在平滑交回待机动画。";
        else if (controller != null && controller.IsBusy) execution = controller.Status;
        else if (evidence.followActive) execution = "持续跟随仍启用，等待可执行的目标。";
        else execution = "当前没有执行跟随；最近一次结果保存在历史记录中。";
        EditorGUILayout.HelpBox(execution, executionType);

        var support = evidence.currentUserSupport;
        string ground;
        if (!evidence.connected) ground = "尚未连接，不能使用旧地面证据。";
        else if (evidence.userAnchorValid) ground = "有效 · 用户位于可验证的同层支撑面上方。";
        else if (support != null && !string.IsNullOrEmpty(support.reason)) ground = "地面或路线无效 · " + support.reason;
        else ground = "地面或路线无效 · 用户当前支撑或同层位置尚未验证。";
        EditorGUILayout.LabelField("用户地面", ground, EditorStyles.wordWrappedLabel);
        if (support != null && support.found)
        {
            EditorGUILayout.LabelField("当前下方支撑", support.objectName + $" · 视点距其 {support.headHeight:F2} 米", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("首个命中位置", support.firstHit.ToString("F3"));
        }
        if (evidence.userAnchorValid)
        {
            EditorGUILayout.LabelField("用户地面坐标", evidence.currentUserGround.ToString("F3"));
            EditorGUILayout.LabelField("用户净距保护", $"角色中心需在半径 {evidence.currentUserClearanceRadius:F2} 米范围外", EditorStyles.wordWrappedLabel);
        }
        var assessment = evidence.assessment;
        if (HasCurrentRoute(bridge, evidence))
        {
            EditorGUILayout.LabelField("当前导航路线", $"{controller.Route.Length:F2} 米 · 使用运行中的同一导航实例", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("角色当前目标", controller.TargetGround.ToString("F3"));
        }
        else if (assessment != null && assessment.state == "busy")
            EditorGUILayout.LabelField("当前路线", "正在执行或交接身体控制；未另做预览规划。", EditorStyles.wordWrappedLabel);
        else if (assessment != null && assessment.expiresAtRealtime > Time.realtimeSinceStartup)
            EditorGUILayout.LabelField("当前路线检查", assessment.reason, EditorStyles.wordWrappedLabel);
        else EditorGUILayout.LabelField("当前路线检查", "暂无近期规划证据；可点击下方检查按钮。", EditorStyles.wordWrappedLabel);
        if (evidence.followActive)
        {
            EditorGUILayout.LabelField("用户 / 请求步速", $"{evidence.followEstimatedUserSpeed:F2} / {evidence.followRequestedSpeed:F2} 米/秒");
            if (evidence.followSpeedLimited)
                EditorGUILayout.HelpBox($"请求步速达到当前 {evidence.followMaximumSpeed:F2} 米/秒上限，角色可能暂时落后。", MessageType.None);
        }
        if (currentStream)
        {
            EditorGUILayout.LabelField("已确认动作缓冲", $"{telemetry.bufferSeconds:F2} 秒 · {telemetry.bufferedFrames} 帧");
            if (telemetry.prebuffering || telemetry.recovering)
                EditorGUILayout.LabelField("开始 / 恢复目标缓冲", $"{telemetry.readyTargetSeconds:F2} 秒");
            if (telemetry.pending)
                EditorGUILayout.LabelField("当前动作请求", $"等待 {telemetry.requestElapsedSeconds:F2} 秒");
        }
        DrawRejectionSummary(evidence);
        showTimingDetails = EditorGUILayout.Foldout(showTimingDetails, "动作请求耗时详情", true);
        if (showTimingDetails) DrawTimingDetails(telemetry, currentStream);
    }

    private static void DrawTimingDetails(ArdyRoomStreamTelemetry value, bool currentStream)
    {
        if (value == null) { EditorGUILayout.LabelField("尚无持续动作请求观测。"); return; }
        if (!currentStream)
            EditorGUILayout.HelpBox("以下属于最近的动作流记录，当前没有对应的活动动作流。", MessageType.None);
        EditorGUILayout.LabelField("请求往返 / 后端", $"{value.roundTripSeconds:F3} / {value.backendSeconds:F3} 秒");
        EditorGUILayout.LabelField("后端排队 / 文本特征", $"{value.backendQueueSeconds:F3} / {value.featureSeconds:F3} 秒");
        EditorGUILayout.LabelField("Unity 路径规划", $"{value.planningSeconds:F3} 秒");
        EditorGUILayout.LabelField("Unity 解析 / 路径检查", $"{value.parseSeconds:F3} / {value.auditSeconds:F3} 秒");
        EditorGUILayout.LabelField("当前 / 近期最长帧", $"{value.frameSeconds * 1000:F1} / {value.largestRecentFrameSeconds * 1000:F1} 毫秒");
        EditorGUILayout.LabelField("控制到播放", $"{value.controlLatencySeconds:F3} 秒");
        EditorGUILayout.LabelField("恢复次数 / 连续恢复", $"{value.recoveryCount} / {value.consecutiveRecoveries}");
        EditorGUILayout.LabelField("操作 / 段 / 控制版本", $"{value.operationRevision} / {value.stepIndex} / {value.controlRevision}");
        EditorGUILayout.LabelField("动作流", value.sessionId ?? "无", EditorStyles.wordWrappedLabel);
        EditorGUILayout.LabelField("请求", value.requestId ?? "无", EditorStyles.wordWrappedLabel);
        EditorGUILayout.LabelField("最后观测事件", (value.eventName ?? "无") + " · " + (value.stage ?? ""), EditorStyles.wordWrappedLabel);
        EditorGUILayout.HelpBox("耗时记录分别来自请求与 Unity 帧观测，部分时间存在包含关系；不能将这些数值直接相加。未产生记录的阶段显示 0。", MessageType.None);
    }

    private static void DrawRejectionSummary(ArdyRoomDialogueBridge.CurrentTargetEvidence evidence)
    {
        var rejection = evidence?.lastObservedRejection;
        if (rejection == null || string.IsNullOrEmpty(rejection.reason)) return;
        float age = Mathf.Max(0, Time.realtimeSinceStartup - rejection.observedAtRealtime);
        EditorGUILayout.LabelField("上次检查拒绝", $"{FailureCategoryLabel(rejection.category)} · {age:F1} 秒前" +
            (rejection.appliesToCurrentTarget ? " · 当时与当前为同一目标版本" : " · 旧目标记录"), EditorStyles.wordWrappedLabel);
        EditorGUILayout.LabelField("记录范围", rejection.hasPoint ? "已记录具体检查点；红叉不代表角色曾移动到该处。" : "未记录具体检查点，Scene 不绘制推测位置。", EditorStyles.wordWrappedLabel);
    }

    private static void DrawLastRejection(ArdyRoomDialogueBridge.CurrentTargetEvidence evidence)
    {
        var rejection = evidence?.lastObservedRejection;
        if (rejection == null || string.IsNullOrEmpty(rejection.reason)) return;
        EditorGUILayout.HelpBox(FailureCategoryLabel(rejection.category) + " · " + rejection.reason, MessageType.None);
        EditorGUILayout.LabelField("来源 / 操作 / 目标版本", $"{rejection.source} / {rejection.operationRevision} / {rejection.targetRevision}", EditorStyles.wordWrappedLabel);
        EditorGUILayout.LabelField("动作", rejection.actionId ?? "无", EditorStyles.wordWrappedLabel);
        if (rejection.hasPoint) EditorGUILayout.LabelField("检查拒绝点", rejection.point.ToString("F3"));
    }

    internal static string FailureCategoryLabel(string category)
    {
        switch (category)
        {
            case "motion-supply": return "动作供给不足";
            case "user-clearance": return "用户净距保护";
            case "navigation": return "地面或路线无效";
            case "service-request": return "动作服务请求失败";
            case "pose-history": return "本地姿态历史未就绪";
            case "motion-quality": return "生成动作未通过检查";
            case "ownership": return "身体控制已变化";
            default: return "其他执行原因";
        }
    }

    private static bool HasCurrentRoute(ArdyRoomDialogueBridge bridge, ArdyRoomDialogueBridge.CurrentTargetEvidence evidence)
    {
        var controller = bridge.Controller;
        return bridge.IsConnected && evidence != null && controller != null &&
            controller.OperationRevision == evidence.controllerOperationRevision &&
            controller.Route?.Corners != null && controller.Route.Corners.Length > 0 &&
            (controller.IsBusy || controller.IsContinuousFollowActive && controller.Stage != "stream-waiting");
    }

    private void OnSceneGUI(SceneView scene)
    {
        if (!showSceneDiagnostics || !Application.isPlaying || Event.current.type != EventType.Repaint ||
            currentEvidence == null || observedBridge == null || !observedBridge.IsConnected ||
            observedBridge != (chat != null ? chat.GetComponent<ArdyRoomDialogueBridge>() : null) ||
            Time.realtimeSinceStartup - currentEvidence.observedAtRealtime > 1f) return;
        var evidence = currentEvidence;
        Color before = Handles.color;
        try
        {
            var support = evidence.currentUserSupport;
            if (support != null && support.found)
            {
                Vector3 point = evidence.userAnchorValid ? evidence.currentUserGround : support.firstHit;
                Handles.color = evidence.userAnchorValid ? new Color(.2f, .95f, .4f) : new Color(1f, .45f, .1f);
                Handles.DrawDottedLine(support.head, point, 4f);
                Handles.DrawWireDisc(point + Vector3.up * .025f, Vector3.up, .13f);
                Handles.Label(point + Vector3.up * .14f, (evidence.userAnchorValid ? "用户有效地面 · " : "用户下方命中，位置未通过验证 · ") + support.objectName);
                if (evidence.userAnchorValid && evidence.currentUserClearanceRadius > 0)
                {
                    Handles.color = new Color(1f, .8f, .1f, .9f);
                    Handles.DrawWireDisc(point + Vector3.up * .035f, Vector3.up, evidence.currentUserClearanceRadius);
                    Handles.Label(point + Vector3.right * evidence.currentUserClearanceRadius + Vector3.up * .05f, "用户净距保护范围");
                }
            }
            if (HasCurrentRoute(observedBridge, evidence))
            {
                var controller = observedBridge.Controller;
                var corners = controller.Route.Corners;
                Handles.color = new Color(.1f, .85f, .95f);
                for (int i = 1; i < corners.Length; i++)
                    Handles.DrawAAPolyLine(4, corners[i - 1] + Vector3.up * .04f, corners[i] + Vector3.up * .04f);
                Handles.DrawWireDisc(controller.TargetGround + Vector3.up * .04f, Vector3.up, .17f);
                Handles.Label(controller.TargetGround + Vector3.up * .2f, "角色当前导航目标");
            }
            var rejection = evidence.lastObservedRejection;
            if (rejection != null && rejection.hasPoint && Finite(rejection.point))
            {
                Handles.color = new Color(1f, .25f, .25f);
                Vector3 point = rejection.point + Vector3.up * .055f;
                const float radius = .10f;
                Handles.DrawLine(point + new Vector3(-radius, 0, -radius), point + new Vector3(radius, 0, radius), 3f);
                Handles.DrawLine(point + new Vector3(-radius, 0, radius), point + new Vector3(radius, 0, -radius), 3f);
                float age = Mathf.Max(0, Time.realtimeSinceStartup - rejection.observedAtRealtime);
                Handles.Label(point + Vector3.up * .15f, "上次检查拒绝点 · " + FailureCategoryLabel(rejection.category) +
                    $" · {age:F1} 秒前" + (rejection.appliesToCurrentTarget ? "" : "（旧目标）"));
            }
        }
        finally { Handles.color = before; }
    }

    private static bool Finite(Vector3 value) =>
        !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
        !float.IsNaN(value.z) && !float.IsInfinity(value.z);

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
        else { bridge.Connect(); message = "本次运行已连接。可以说“过来，到我身边”“跟着我”或“停下”。"; }
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (Exception error) { message = error.Message; Debug.LogWarning("[Room/Setup] " + error.Message); }
    }
}
