using System;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEngine;

/// <summary>Review the same composable plan used by dialogue, without altering scene assets.</summary>
public sealed class ArdyConstraintPreviewWindow : EditorWindow
{
    private static readonly string[] Goals = { "none", "current", "forward", "outward", "up", "down", "forward-up", "outward-up" };
    private static readonly string[] Joints = { "none", "left-wrist", "right-wrist", "wrists", "head" };
    private static readonly string[] Axes = { "up", "right", "forward", "palm-normal" };
    private static readonly string[] GoalLabels = { "不控制", "保持当前", "前伸", "侧平举", "上举", "下放", "前上方", "侧上方" };
    private static readonly string[] JointLabels = { "无局部活动", "左手腕", "右手腕", "双手腕", "头部" };
    private static readonly string[] AxisLabels = { "竖直轴", "水平横轴", "前后轴", "掌面法线（保持掌向轻摆）" };
    private static readonly string[] Palms = { "keep", "partner", "up", "down", "inward", "outward" };
    private static readonly string[] PalmLabels = { "原有掌向", "朝交流对象", "朝上", "朝下", "朝身体内侧", "朝身体外侧" };
    private static readonly string[] Tempos = { "gentle", "natural", "brisk" };
    private static readonly string[] TempoLabels = { "缓慢", "正常交流", "轻快" };
    private Vrm10Instance avatar;
    private Transform interactionTarget;
    private ArdyLiveMotionController controller;
    private ArdyControlPlan plan = new ArdyControlPlan { left = "forward", right = "forward", joint = "wrists", end = "hold" };
    private string error;
    private int revision;
    [MenuItem("Tools/NeEEvA/Pose and Local Motion")]
    public static void Open() => GetWindow<ArdyConstraintPreviewWindow>("姿态与局部动作");
    private void OnInspectorUpdate() { if (Application.isPlaying) Repaint(); }
    private void OnGUI()
    {
        EditorGUILayout.HelpBox("先达到目标姿态，再执行局部运动。手臂方向以角色自身为准；目标未达标时会退出。", MessageType.Info);
        avatar = (Vrm10Instance)EditorGUILayout.ObjectField("运行中的角色", avatar, typeof(Vrm10Instance), true);
        interactionTarget = (Transform)EditorGUILayout.ObjectField("交流对象位置（可留空）", interactionTarget, typeof(Transform), true);
        plan.left = Choice("左臂目标", plan.left, Goals, GoalLabels);
        plan.right = Choice("右臂目标", plan.right, Goals, GoalLabels);
        plan.leftPalm = Choice("左掌朝向", plan.leftPalm, Palms, PalmLabels);
        plan.rightPalm = Choice("右掌朝向", plan.rightPalm, Palms, PalmLabels);
        plan.leftBendAuto = plan.leftPalm != "keep" && plan.left != "none" && plan.left != "current"
            && EditorGUILayout.Toggle("左肘按掌向自动求解", plan.leftBendAuto);
        plan.rightBendAuto = plan.rightPalm != "keep" && plan.right != "none" && plan.right != "current"
            && EditorGUILayout.Toggle("右肘按掌向自动求解", plan.rightBendAuto);
        using (new EditorGUI.DisabledScope(plan.leftBendAuto))
            plan.leftBend = EditorGUILayout.Slider("左肘弯曲角", plan.leftBend, 0, 110);
        using (new EditorGUI.DisabledScope(plan.rightBendAuto))
            plan.rightBend = EditorGUILayout.Slider("右肘弯曲角", plan.rightBend, 0, 110);
        plan.joint = Choice("局部活动关节", plan.joint, Joints, JointLabels);
        plan.axis = Choice("旋转轴（角色坐标）", plan.axis, Axes, AxisLabels);
        plan.amplitude = EditorGUILayout.Slider("局部幅度（度）", plan.amplitude, 1, plan.joint == "head" ? 12 : 20);
        plan.cycles = EditorGUILayout.IntSlider("往返次数", plan.cycles, 1, 3);
        bool adaptive = EditorGUILayout.Toggle("按动作幅度计算节奏", plan.timingPolicy == "adaptive-v1");
        plan.timingPolicy = adaptive ? "adaptive-v1" : "legacy";
        if (adaptive)
        {
            plan.tempo = Choice("节奏", plan.tempo, Tempos, TempoLabels);
            plan.timingDurationFixed = EditorGUILayout.Toggle("严格指定局部时长", plan.timingDurationFixed);
        }
        else plan.timingDurationFixed = false;
        if (!adaptive || plan.timingDurationFixed)
            plan.seconds = EditorGUILayout.Slider("局部运动时长（秒）", plan.seconds, 1, 6);
        plan.end = EditorGUILayout.Toggle("完成后保持（最多30秒）", plan.end == "hold") ? "hold" : "idle";
        if (plan.joint == "none" && plan.end == "hold")
            EditorGUILayout.HelpBox("没有局部活动时，到位后立即保持。", MessageType.None);
        using (new EditorGUI.DisabledScope(!Application.isPlaying || avatar == null || EditorUtility.IsPersistent(avatar)))
        {
            if (GUILayout.Button("执行组合"))
            {
                try
                {
                    plan.Validate();
                    controller = avatar.GetComponent<ArdyLiveMotionController>();
                    if (controller == null) controller = avatar.gameObject.AddComponent<ArdyLiveMotionController>();
                    controller.interactionTarget = interactionTarget;
                    if (!controller.IsBound) controller.Bind(avatar);
                    controller.RequestControlPlan(plan, ++revision);
                    error = null;
                }
                catch (Exception exception) { error = exception.Message; }
            }
            if (GUILayout.Button("停止并回待机"))
            {
                controller = avatar.GetComponent<ArdyLiveMotionController>();
                controller?.Cancel("explicit-preview-stop");
            }
        }
        if (controller != null) EditorGUILayout.LabelField("状态", controller.Status);
        if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Warning);
    }
    private static string Choice(string label, string value, string[] options, string[] labels) =>
        options[EditorGUILayout.Popup(label, Mathf.Max(0, Array.IndexOf(options, value)), labels)];
}
