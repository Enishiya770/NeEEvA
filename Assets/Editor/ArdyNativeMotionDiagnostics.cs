using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Capture observed Animator history, then render unchanged native generator clips in an isolated project.</summary>
[InitializeOnLoad]
public static class ArdyNativeMotionDiagnostics
{
    private const string Key = "NeEEvA.ARDY.NativeDiagnostics.";
    private static Vrm10Instance avatar;
    private static ArdyMotionPlayer player;
    private static Animator animator;
    private static float readyAt, nextAt;
    private static Quaternion hipsRest;
    private static readonly List<ArdyMotionFrame> history = new List<ArdyMotionFrame>();
    private static readonly List<ArdyMotionFrame> hipsRelative = new List<ArdyMotionFrame>();
    private static readonly List<float> observedAt = new List<float>();

    [Serializable] public sealed class PoseBone
    {
        public string path;
        public Quaternion rotation;
        public Vector3 position, scale;
    }
    [Serializable] public sealed class PoseCapture
    {
        public string avatar = "Assets/Model/NEVA.vrm";
        public string animator = "Assets/AIChatTookit/Animation/Animator Controller.controller";
        public string method = "Real Unity PlayMode Animator; observed at 20 Hz after idle warmup. No manual Tick or Animator.Update.";
        public string historyMeaning = "Core27 projection, real upper body; synthetic identity root/legs. Hips-relative candidate removes each frame's observed hips delta from upper global rotations.";
        public string unityVersion;
        public List<float> observedTimes;
        public List<PoseBone> bones;
        public Quaternion hipsRestInRoot, hipsCurrentInRoot;
        public float leftWristBelowShoulder, rightWristBelowShoulder;
        public string[] currentClips;
    }

    static ArdyNativeMotionDiagnostics()
    {
        EditorApplication.update += UpdateCapture;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) InitializeCapture();
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) FinishCapture();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && !EditorApplication.isPlaying && SessionState.GetBool(Key + "Finished", false)) FinishCapture();
        };
    }

    public static void CaptureAnimatorBatch()
    {
        RequireIsolated();
        string output = Argument("-ardyDiagnosticOutput") ?? Path.GetFullPath("Logs/native-motion-diagnostics");
        Directory.CreateDirectory(output);
        SessionState.SetString(Key + "Output", output);
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Model/NEVA.vrm");
        var animation = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/AIChatTookit/Animation/Animator Controller.controller");
        if (prefab == null || animation == null) throw new InvalidOperationException("Actual avatar and Animator assets are required.");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        root.name = "Native-Diagnostic-Avatar";
        root.GetComponent<Vrm10Instance>().enabled = false;
        var anim = root.GetComponent<Animator>();
        anim.runtimeAnimatorController = animation;
        anim.applyRootMotion = false;
        anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        anim.enabled = true;
        SessionState.SetBool(Key + "Active", true);
        SessionState.SetBool(Key + "Finished", false);
        SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 40);
        EditorApplication.isPlaying = true;
    }

    private static void InitializeCapture()
    {
        try
        {
            avatar = GameObject.Find("Native-Diagnostic-Avatar").GetComponent<Vrm10Instance>();
            animator = avatar.GetComponent<Animator>();
            animator.SetInteger("state", 0);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Model/NEVA.vrm").GetComponent<Vrm10Instance>();
            hipsRest = Quaternion.Inverse(prefab.transform.rotation) * prefab.Humanoid.GetBoneTransform(HumanBodyBones.Hips).rotation;
            player = avatar.gameObject.AddComponent<ArdyMotionPlayer>();
            player.Bind(avatar);
            history.Clear(); hipsRelative.Clear(); observedAt.Clear();
            readyAt = Time.realtimeSinceStartup + 1f;
            nextAt = readyAt;
        }
        catch (Exception error) { FailCapture(error); }
    }

    private static void UpdateCapture()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", 0))
        { FailCapture(new TimeoutException("Animator capture timed out")); return; }
        if (!Application.isPlaying || player == null || Time.realtimeSinceStartup < nextAt) return;
        try
        {
            var current = player.CaptureUpperBodyHistoryFrame();
            var hip = avatar.Humanoid.GetBoneTransform(HumanBodyBones.Hips);
            Quaternion hipDelta = Quaternion.Inverse(avatar.transform.rotation) * hip.rotation * Quaternion.Inverse(hipsRest);
            var relative = new Quaternion[27];
            for (int i = 0; i < relative.Length; i++) relative[i] = i > 0 && i < 19
                ? (Quaternion.Inverse(hipDelta) * current.globalRotations[i]).normalized : current.globalRotations[i];
            history.Add(current);
            hipsRelative.Add(new ArdyMotionFrame { globalRotations = relative });
            observedAt.Add(Time.realtimeSinceStartup);
            nextAt = readyAt + history.Count * 0.05f;
            if (history.Count < 16) return;
            string output = SessionState.GetString(Key + "Output", "");
            File.WriteAllText(Path.Combine(output, "animator-idle-history.json"), JsonUtility.ToJson(new ArdyInitialHistory { frames = history.ToArray() }, true));
            File.WriteAllText(Path.Combine(output, "animator-idle-history-hips-relative.json"), JsonUtility.ToJson(new ArdyInitialHistory { frames = hipsRelative.ToArray() }, true));
            var capture = new PoseCapture {
                unityVersion = Application.unityVersion, observedTimes = observedAt,
                hipsRestInRoot = hipsRest, hipsCurrentInRoot = Quaternion.Inverse(avatar.transform.rotation) * hip.rotation,
                bones = avatar.GetComponentsInChildren<Transform>(true).Select(t => new PoseBone {
                    path = AnimationUtility.CalculateTransformPath(t, avatar.transform), rotation = t.localRotation,
                    position = t.localPosition, scale = t.localScale }).ToList(),
                leftWristBelowShoulder = avatar.Humanoid.GetBoneTransform(HumanBodyBones.LeftUpperArm).position.y - avatar.Humanoid.GetBoneTransform(HumanBodyBones.LeftHand).position.y,
                rightWristBelowShoulder = avatar.Humanoid.GetBoneTransform(HumanBodyBones.RightUpperArm).position.y - avatar.Humanoid.GetBoneTransform(HumanBodyBones.RightHand).position.y,
                currentClips = animator.GetCurrentAnimatorClipInfo(0).Select(c => c.clip.name).ToArray()
            };
            File.WriteAllText(Path.Combine(output, "animator-idle-pose.json"), JsonUtility.ToJson(capture, true));
            Debug.Log("[ArdyNativeDiagnostics] Captured 16 real Animator frames: " + output);
            SessionState.SetBool(Key + "Finished", true);
            SessionState.SetInt(Key + "Exit", 0);
            EditorApplication.isPlaying = false;
        }
        catch (Exception error) { FailCapture(error); }
    }

    private static void FailCapture(Exception error)
    {
        Debug.LogException(error);
        SessionState.SetBool(Key + "Finished", true);
        SessionState.SetInt(Key + "Exit", 1);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else FinishCapture();
    }
    private static void FinishCapture()
    {
        SessionState.SetBool(Key + "Active", false);
        EditorApplication.Exit(SessionState.GetInt(Key + "Exit", 1));
    }
    private static void RequireIsolated()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Use only the isolated Unity validation project in batch mode.");
    }
    private static string Argument(string key)
    {
        string[] args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
