using NeEEvA.Perception;
using UnityEditor;
using UnityEngine;

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

    private void OnDisable() { if (candidateTexture != null) DestroyImmediate(candidateTexture); candidateTexture = null; candidateView = null; }

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
                    EditorGUILayout.HelpBox("左侧为当时视野中的实际可见轮廓，右侧为同图局部放大。编号对应本次候选，不表示已经认对目标。", MessageType.None);
                    float width = Mathf.Max(240, position.width - 28);
                    EditorGUI.DrawPreviewTexture(GUILayoutUtility.GetRect(width, width * candidateTexture.height / candidateTexture.width), candidateTexture, null, ScaleMode.ScaleToFit);
                    foreach (var candidate in candidateView.Candidates)
                        EditorGUILayout.LabelField(candidate.candidateId + " / " + candidate.localHierarchy?.entityRootName,
                            candidate.localHierarchy == null ? candidate.sceneLabel : string.Join(" → ", candidate.localHierarchy.partToEntityRoot), EditorStyles.wordWrappedLabel);
                }
            }
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
}
