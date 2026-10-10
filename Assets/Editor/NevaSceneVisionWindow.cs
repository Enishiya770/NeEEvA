using NeEEvA.Perception;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>Read-only preview of the runtime observation source; does not open eyes or own a camera.</summary>
public sealed class NevaSceneVisionWindow : EditorWindow
{
    [SerializeField] private NevaSceneVisionCapture source;
    private bool refreshing;
    private double nextRefresh;
    private string requestStatus = "";
    private Vector2 scroll;
    private SceneObjectCandidateView candidateView;
    private Texture2D candidateTexture;
    private bool showCandidates = true;
    private SceneObjectCandidateView searchView;
    private Texture2D searchTexture;
    private bool showSearch = true, showCatalog = true, showMemory;
    private double nextDiagnostics;
    private NevaSceneVisionCapture diagnosticsSource;
    private JObject diagnostics;

    private void OnDisable()
    {
        if (candidateTexture != null) DestroyImmediate(candidateTexture);
        if (searchTexture != null) DestroyImmediate(searchTexture);
        candidateTexture = searchTexture = null; candidateView = searchView = null;
        diagnostics = null; diagnosticsSource = null;
    }

    [MenuItem("Tools/NeEEvA/Character Scene Vision")]
    public static void Open() => GetWindow<NevaSceneVisionWindow>("角色场景视觉");

    private void OnInspectorUpdate()
    {
        if (refreshing && Application.isPlaying && source != null && source.IsObserving &&
            !source.IsCapturePending && EditorApplication.timeSinceStartup >= nextRefresh)
        {
            nextRefresh = EditorApplication.timeSinceStartup + 1;
            RequestPreview();
        }
        RefreshDiagnostics();
        Repaint();
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.LabelField("角色自己的房间视角", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("预览实际角色头部朝向的离屏 RGB。此窗口不操纵用户相机、不转动角色，也不会为主脑自动睁眼。", MessageType.Info);
        source = (NevaSceneVisionCapture)EditorGUILayout.ObjectField("运行中的视觉组件", source, typeof(NevaSceneVisionCapture), true);
        if (GUILayout.Button("查找当前角色视觉")) FindSource();
        if (source == null)
        {
            EditorGUILayout.HelpBox("进入已配置角色场景的 Play 模式，等待聊天组件连接视觉后查找。", MessageType.None);
            EditorGUILayout.EndScrollView(); return;
        }
        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.ObjectField("绑定角色", source.Target, typeof(UniVRM10.Vrm10Instance), true);
        EditorGUILayout.LabelField("感知状态", source.IsObserving ? "睁眼" : "闭眼");
        EditorGUILayout.LabelField("采集状态", source.Status, EditorStyles.wordWrappedLabel);
        EditorGUILayout.LabelField("首次自身网格准备", $"{source.PreparationMilliseconds:F1} 毫秒（同步一次）");
        using (new EditorGUI.DisabledScope(!Application.isPlaying || !source.IsObserving || source.IsCapturePending))
            if (GUILayout.Button("采集当前视野")) RequestPreview();
        refreshing = EditorGUILayout.Toggle("每秒刷新预览（睁眼时）", refreshing);
        if (!string.IsNullOrEmpty(requestStatus)) EditorGUILayout.LabelField(requestStatus, EditorStyles.wordWrappedLabel);
        var sceneChat = source.GetComponent<ChatSample>();
        if (sceneChat != null)
        {
            if (diagnosticsSource != source) RefreshDiagnostics();
            DrawObservationCatalog();
            var facts = Newtonsoft.Json.Linq.JObject.Parse(sceneChat.SceneActionContextJson);
            EditorGUILayout.LabelField("视觉目标动作", (bool)facts["active"] ? "执行中" : "可接受新目标");
            var last = facts["lastAction"];
            if (last is Newtonsoft.Json.Linq.JObject)
            {
                EditorGUILayout.LabelField("图像目标解释", (string)last["visualLabel"] ?? "", EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("执行阶段", (string)last["phase"] ?? "", EditorStyles.wordWrappedLabel);
                if (!string.IsNullOrEmpty((string)last["targetMatchVerdict"]))
                    EditorGUILayout.LabelField("目标核对", last["targetMatchVerdict"] + " / " + last["targetMatchReason"], EditorStyles.wordWrappedLabel);
                if ((string)last["intent"] == "approach-object")
                {
                    EditorGUILayout.LabelField("到达 / 同物重认", last["groundArrived"] + " / " + last["objectReobserved"], EditorStyles.wordWrappedLabel);
                    if (!string.IsNullOrEmpty((string)last["trackingVisibility"]))
                        EditorGUILayout.LabelField("行走中观察", last["trackingVisibility"] + " / " + last["trackingFocusStatus"], EditorStyles.wordWrappedLabel);
                }
                EditorGUILayout.LabelField("实际反馈", (string)last["reason"] ?? "", EditorStyles.wordWrappedLabel);
                EditorGUILayout.SelectableLabel((string)last["actionId"] ?? "", GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            var selectedObject = facts["objectContext"]?["selected"] as Newtonsoft.Json.Linq.JObject;
            if (selectedObject != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("最近关注的物件（观测快照）", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("场景标注", (string)selectedObject["sceneLabel"] ?? "未标注", EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("身份来源", (string)selectedObject["identitySource"] ?? "", EditorStyles.wordWrappedLabel);
                EditorGUILayout.SelectableLabel((string)selectedObject["entityId"] ?? "", GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.LabelField("观测时间 UTC", (string)selectedObject["observedUtc"] ?? "", EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("已登记能力", selectedObject["capabilities"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "", EditorStyles.wordWrappedLabel);
                var state = selectedObject["runtimeDeviceStateReadAfterObservation"];
                if (state is Newtonsoft.Json.Linq.JObject)
                    EditorGUILayout.LabelField("观测后读取的灯具状态", (string)state["emissionState"] ?? "未知", EditorStyles.wordWrappedLabel);
                var result = facts["objectContext"]?["stateResult"];
                if (result is Newtonsoft.Json.Linq.JObject)
                    EditorGUILayout.LabelField("开关结果 / 视觉确认", result["on"] + " / " + result["visualConfirmation"], EditorStyles.wordWrappedLabel);
                EditorGUILayout.HelpBox("这里只显示角色实际关注过的物件。标注来自场景，快照不代表持续可见；灯具开关不改变烘焙照明，也不表示伸手按了开关。", MessageType.None);
            }
            var nextCandidates = sceneChat.LastSceneObjectCandidateView;
            if (!ReferenceEquals(candidateView, nextCandidates))
            {
                if (candidateTexture != null) DestroyImmediate(candidateTexture);
                candidateTexture = null; candidateView = nextCandidates;
                if (candidateView != null)
                {
                    candidateTexture = new Texture2D(2, 2);
                    candidateTexture.LoadImage(System.Convert.FromBase64String(candidateView.ImageDataUrl.Substring(candidateView.ImageDataUrl.IndexOf(',') + 1)));
                }
            }
            if (candidateTexture != null)
            {
                showCandidates = EditorGUILayout.Foldout(showCandidates, "最近目标核对图（历史快照）", true);
                if (showCandidates)
                {
                    EditorGUILayout.LabelField(candidateView.CapturedAtUtc, EditorStyles.wordWrappedLabel);
                    EditorGUILayout.SelectableLabel(candidateView.CaptureId, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    EditorGUILayout.HelpBox("左侧为当时视野中的实际可见轮廓，右侧为同图局部放大。编号对应本次候选，不表示已经认对目标。", MessageType.None);
                    float width = Mathf.Max(240, position.width - 28);
                    EditorGUI.DrawPreviewTexture(GUILayoutUtility.GetRect(width, width * candidateTexture.height / candidateTexture.width), candidateTexture, null, ScaleMode.ScaleToFit);
                    foreach (var candidate in candidateView.Candidates)
                        EditorGUILayout.LabelField(candidate.candidateId + " / " + candidate.localHierarchy?.entityRootName,
                            candidate.localHierarchy == null ? candidate.sceneLabel : string.Join(" → ", candidate.localHierarchy.partToEntityRoot), EditorStyles.wordWrappedLabel);
                }
            }
            DrawSearchPreview(sceneChat);
        }
        var observation = source.LatestObservation;
        if (observation != null)
        {
            EditorGUILayout.LabelField("来源", observation.Source);
            EditorGUILayout.SelectableLabel(observation.CaptureId, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.LabelField("快照状态", observation.State);
            if (observation.HasImage)
            {
                EditorGUILayout.LabelField("采集时间 UTC", observation.CapturedAtUtc);
                EditorGUILayout.LabelField("采集场景", observation.SceneName);
                EditorGUILayout.LabelField("帧 / 距今", $"{observation.Frame} / {Mathf.Max(0, Time.realtimeSinceStartup - observation.CapturedAtRealtime):F2} 秒");
                EditorGUILayout.LabelField("视角仍相近", source.IsCurrentViewCompatible(observation) ? "是" : "否；这张图只代表采集时刻");
                if (source.PreviewTexture != null)
                {
                    float width = Mathf.Max(240, position.width - 28);
                    Rect imageRect = GUILayoutUtility.GetRect(width, width * observation.Height / observation.Width);
                    EditorGUI.DrawPreviewTexture(imageRect, source.PreviewTexture, null, ScaleMode.ScaleToFit);
                }
            }
            else EditorGUILayout.HelpBox("当前没有可用图片；未显示旧像素。" + observation.Reason, MessageType.None);
        }
        EditorGUILayout.EndScrollView();
    }

    private void FindSource()
    {
        var selected = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<NevaSceneVisionCapture>() : null;
        if (selected != null) { source = selected; return; }
        var sources = FindObjectsOfType<NevaSceneVisionCapture>();
        if (sources.Length == 1) source = sources[0];
        else requestStatus = sources.Length == 0 ? "当前运行场景还没有视觉组件。" : "检测到多个视觉组件，请手动选择角色对应的一份。";
    }

    private void RequestPreview()
    {
        var requestedSource = source;
        if (requestedSource == null) return;
        requestedSource.RequestCapture(value =>
        {
            if (this == null || source != requestedSource) return;
            requestStatus = value.HasImage ? "本次预览采集完成。" : value.Reason;
            Repaint();
        });
    }

    private void RefreshDiagnostics()
    {
        if (diagnosticsSource == source && EditorApplication.timeSinceStartup < nextDiagnostics) return;
        diagnosticsSource = source; nextDiagnostics = EditorApplication.timeSinceStartup + 1;
        var chat = source != null ? source.GetComponent<ChatSample>() : null;
        diagnostics = chat != null ? JObject.Parse(chat.GetSceneVisibleCatalogDiagnosticsJson()) : null;
    }

    private void DrawObservationCatalog()
    {
        if (diagnostics == null) return;
        EditorGUILayout.Space();
        EditorGUILayout.HelpBox("目录来自普通对话、自主观察或搜索实际采集的图；编号仅对应当页候选。Unity 名称是作者线索，模型描述和目标核对另行记录。每秒预览只刷新 RGB，不调用模型。", MessageType.None);
        bool current = (bool?)diagnostics["currentViewUsable"] == true;
        showCatalog = EditorGUILayout.Foldout(showCatalog,
            "当前观察目录（" + (current ? diagnostics["currentCatalogCandidates"] + " 个候选" : "暂无新鲜有效图") + "）", true);
        if (showCatalog && current)
        {
            EditorGUILayout.LabelField("采集 UTC", (string)diagnostics["currentCapturedAtUtc"], EditorStyles.wordWrappedLabel);
            EditorGUILayout.SelectableLabel((string)diagnostics["currentCaptureId"], GUILayout.Height(EditorGUIUtility.singleLineHeight));
            foreach (JObject item in (JArray)diagnostics["currentObjects"]) DrawObservedObject(item);
        }
        var memory = (JArray)diagnostics["retainedObjects"];
        showMemory = EditorGUILayout.Foldout(showMemory, "保留的观察记忆（" + memory.Count + "；含当前已见物件）", true);
        if (showMemory) foreach (JObject item in memory) DrawObservedObject(item);
    }

    private static void DrawObservedObject(JObject item)
    {
        var authored = item["authoredHint"] as JObject;
        string label = (string)authored?["sceneLabel"];
        if (string.IsNullOrEmpty(label)) label = (string)authored?["entityRootName"];
        if (string.IsNullOrEmpty(label)) label = (string)authored?["rendererName"];
        EditorGUILayout.LabelField(label ?? "未命名可见物件", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Unity 作者线索", authored == null ? "无" :
            (string)authored["rendererName"] + " / " + (string)authored["entityRootName"], EditorStyles.wordWrappedLabel);
        EditorGUILayout.SelectableLabel((string)item["entityId"], GUILayout.Height(EditorGUIUtility.singleLineHeight));
        EditorGUILayout.LabelField("记录层级", (string)item["semanticStatus"], EditorStyles.wordWrappedLabel);
        EditorGUILayout.LabelField("图中可见 / 冻结点有效", item["visibleInCurrentCapture"] + " / " + item["positionMemoryValid"]);
        EditorGUILayout.LabelField("观察 UTC", (string)item["capturedAtUtc"], EditorStyles.wordWrappedLabel);
        EditorGUILayout.SelectableLabel((string)item["sourceCaptureId"], GUILayout.Height(EditorGUIUtility.singleLineHeight));
        EditorGUILayout.LabelField("捕获时表面点", item["pointAtCapture"]?.ToString(Newtonsoft.Json.Formatting.None), EditorStyles.wordWrappedLabel);
        if (item["directionFromCurrentPose"] is JObject bearing)
        {
            var gaze = bearing["relativeToCurrentView"] as JObject;
            var body = bearing["relativeToCurrentBody"] as JObject;
            EditorGUILayout.LabelField("相对视线 / 身体转角（右为正）", gaze?["yawDegreesRight"] + "° / " + body?["yawDegrees"] + "°", EditorStyles.wordWrappedLabel);
        }
        if (!string.IsNullOrEmpty((string)item["modelDescription"]))
            EditorGUILayout.LabelField("模型描述（未核对）", (string)item["modelDescription"], EditorStyles.wordWrappedLabel);
        if (!string.IsNullOrEmpty((string)item["matchedOriginalTarget"]))
        {
            EditorGUILayout.LabelField("核对过的目标原文", (string)item["matchedOriginalTarget"], EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("核对图", (string)item["targetMatchCaptureId"], EditorStyles.wordWrappedLabel);
        }
    }

    private void DrawSearchPreview(ChatSample chat)
    {
        var next = chat.LastSceneSearchCandidateView;
        if (!ReferenceEquals(searchView, next))
        {
            if (searchTexture != null) DestroyImmediate(searchTexture);
            searchTexture = null; searchView = next;
            if (next != null)
            {
                searchTexture = new Texture2D(2, 2);
                searchTexture.LoadImage(System.Convert.FromBase64String(next.ImageDataUrl.Substring(next.ImageDataUrl.IndexOf(',') + 1)));
            }
        }
        if (searchTexture == null) return;
        showSearch = EditorGUILayout.Foldout(showSearch, "最近搜索图（历史快照 / " + chat.LastSceneSearchPreviewPurpose + "）", true);
        if (!showSearch) return;
        EditorGUILayout.LabelField(searchView.CapturedAtUtc, EditorStyles.wordWrappedLabel);
        EditorGUILayout.SelectableLabel(searchView.CaptureId, GUILayout.Height(EditorGUIUtility.singleLineHeight));
        EditorGUILayout.HelpBox("分页沿用同一张 RGB；换页不代表转头或新观察。编号只是该图候选，核对阶段也不等于核对成功。", MessageType.None);
        float width = Mathf.Max(240, position.width - 28);
        EditorGUI.DrawPreviewTexture(GUILayoutUtility.GetRect(width, width * searchTexture.height / searchTexture.width), searchTexture, null, ScaleMode.ScaleToFit);
    }
}
