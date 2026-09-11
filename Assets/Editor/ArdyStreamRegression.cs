using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Real service payload replay on two actual VRM rigs. It never starts chat or contacts a service.</summary>
public static class ArdyStreamRegression
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    private static readonly HumanBodyBones[] Human = {
        HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head,
        HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand
    };
    private static readonly int[] Core = { 1, 3, 4, 5, 6, 7, 8, 9, 10, 13, 14, 15, 16 };

    [Serializable] private sealed class Report
    {
        public bool passed;
        public int checks;
        public string unityVersion = Application.unityVersion;
        public string checkedAtUtc = DateTime.UtcNow.ToString("o");
        public string environment = "Real Unity Editor preview scenes; imported NEVA and NeEEvA VRM rigs; replay of actual live-qwen ARDY HTTP chunks";
        public string limitation = "Fixed idle baseline and response replay; no live network scheduling or subjective naturalness measurement";
        public List<Hash> fixtureHashes = new List<Hash>();
        public List<Hash> sourceHashes = new List<Hash>();
        public List<AvatarResult> avatars = new List<AvatarResult>();
    }
    [Serializable] private sealed class Hash { public string path, sha256; }
    [Serializable] private sealed class AvatarResult
    {
        public string asset;
        public int mappedBones, totalStreamFrames;
        public float historyReconstructionMaxDegrees, historyYawInvarianceMaxDegrees;
        public float appendPoseChangeDegrees, idleRecoveryMaxDegrees, underrunRecoveryMaxDegrees;
    }

    private sealed class Fixture : IDisposable
    {
        public Scene Scene;
        public GameObject Root;
        public Vrm10Instance Vrm;
        public ArdyMotionPlayer Player;
        public readonly Dictionary<Transform, Quaternion> Baseline = new Dictionary<Transform, Quaternion>();
        public readonly Dictionary<HumanBodyBones, Quaternion> Rest = new Dictionary<HumanBodyBones, Quaternion>();
        private readonly Dictionary<Transform, Vector3> positions = new Dictionary<Transform, Vector3>();
        private readonly Dictionary<Transform, Vector3> scales = new Dictionary<Transform, Vector3>();
        private readonly Dictionary<HumanBodyBones, Quaternion> protectedRotations = new Dictionary<HumanBodyBones, Quaternion>();

        public Fixture(string asset)
        {
            Scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset);
                Check(prefab != null, "Missing actual imported VRM: " + asset);
                Root = (GameObject)PrefabUtility.InstantiatePrefab(prefab, Scene);
                Root.transform.SetPositionAndRotation(new Vector3(0.4f, 0.1f, -0.2f), Quaternion.Euler(0, 31, 0));
                Vrm = Root.GetComponentInChildren<Vrm10Instance>(true);
                Check(Vrm != null, "Missing VRM instance");
                Vrm.enabled = false;
                foreach (var animator in Root.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
                for (int i = 0; i < Human.Length; i++)
                {
                    Transform bone = Bone(Human[i]);
                    Check(bone != null, "Missing upper body bone " + Human[i]);
                    Rest[Human[i]] = Quaternion.Inverse(Root.transform.rotation) * bone.rotation;
                }
                Player = Vrm.gameObject.AddComponent<ArdyMotionPlayer>();
                Player.Bind(Vrm);
                foreach (var transform in Root.GetComponentsInChildren<Transform>(true))
                {
                    Baseline[transform] = transform.localRotation;
                    positions[transform] = transform.localPosition;
                    scales[transform] = transform.localScale;
                }
                foreach (HumanBodyBones bone in new[] { HumanBodyBones.Hips, HumanBodyBones.LeftUpperLeg,
                    HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.RightUpperLeg,
                    HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot })
                    protectedRotations[bone] = Bone(bone).localRotation;
            }
            catch { Dispose(); throw; }
        }

        public Transform Bone(HumanBodyBones bone) => Vrm.Humanoid.GetBoneTransform(bone);
        public void Step(float delta)
        {
            Player.RestoreAnimatedPose();
            foreach (var pair in Baseline) pair.Key.localRotation = pair.Value;
            Player.Tick(delta);
            foreach (var pair in positions)
            {
                Check(Vector3.Distance(pair.Key.localPosition, pair.Value) < 0.00001f, "Motion wrote bone/root position");
                Check(Vector3.Distance(pair.Key.localScale, scales[pair.Key]) < 0.00001f, "Motion wrote bone/root scale");
            }
            foreach (var pair in protectedRotations)
                Check(Quaternion.Angle(Bone(pair.Key).localRotation, pair.Value) < 0.08f, "Upper-body stream moved root/legs");
            Check(Quaternion.Angle(Root.transform.localRotation, Baseline[Root.transform]) < 0.08f, "Stream moved root yaw");
        }
        public Dictionary<Transform, Quaternion> Pose() => Baseline.Keys.ToDictionary(t => t, t => t.localRotation);
        public float PoseError(Dictionary<Transform, Quaternion> expected) => expected.Max(p => Quaternion.Angle(p.Key.localRotation, p.Value));
        public float IdleError() => PoseError(Baseline);
        public void Dispose()
        {
            if (Root != null) Object.DestroyImmediate(Root);
            if (Scene.IsValid()) EditorSceneManager.ClosePreviewScene(Scene);
        }
    }

    [MenuItem("Tools/NeEEvA/Run ARDY Stream Regression")]
    public static void RunInteractive()
    {
        checks = 0;
        var report = new Report();
        string sourceRoot = Argument("-ardySourceRoot") ?? Path.GetFullPath(Path.Combine(Application.dataPath, "../../../../.."));
        string fixtureDir = Argument("-ardyLiveFixtureDir") ?? Path.Combine(sourceRoot, "Server/ARDY/runtime/live-service-validation/live");
        var responses = new ArdyGenerateResponse[3];
        for (int i = 0; i < responses.Length; i++)
        {
            string path = Path.Combine(fixtureDir, "chunk-" + i + ".json");
            responses[i] = JsonUtility.FromJson<ArdyGenerateResponse>(File.ReadAllText(path));
            report.fixtureHashes.Add(new Hash { path = Path.GetFullPath(path), sha256 = Sha(path) });
        }
        foreach (string relative in new[] {
            "Assets/AIChatTookit/Scripts/Motion/ArdyMotionPlayer.cs", "Assets/AIChatTookit/Scripts/Motion/ArdyMotionClip.cs",
            "Assets/AIChatTookit/Scripts/Motion/ArdyLiveProtocol.cs", "Assets/Editor/ArdyStreamRegression.cs" })
        {
            string actual = Path.GetFullPath(Path.Combine(Application.dataPath, "..", relative));
            string source = Path.Combine(sourceRoot, relative);
            Check(File.Exists(source) && Sha(actual) == Sha(source), "Isolated source differs from workspace: " + relative);
            report.sourceHashes.Add(new Hash { path = relative, sha256 = Sha(actual) });
        }
        CheckProtocol(responses);
        CheckRequestSerialization(responses);
        foreach (string asset in new[] { "Assets/Model/NEVA.vrm", "Assets/Model/NeEEvA.vrm" })
        using (var fixture = new Fixture(asset))
        {
            var result = new AvatarResult { asset = asset, mappedBones = fixture.Player.BoundBoneCount };
            CheckHistory(fixture, result);
            CheckTimeline(fixture, responses, result);
            report.avatars.Add(result);
        }
        report.passed = true;
        report.checks = checks;
        string output = Path.GetFullPath("Tools/MotionAdapter/reports/stream-regression.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output, JsonUtility.ToJson(report, true) + "\n");
        Debug.Log($"[ArdyStreamRegression] passed {checks} assertions on two actual VRM rigs; {output}");
    }

    public static void RunBatch()
    {
        try { RunInteractive(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    private static ArdyGenerateRequest RequestFor(ArdyGenerateResponse response) => new ArdyGenerateRequest {
        characterId = response.characterId, turnId = response.turnId, requestId = response.requestId,
        revision = response.revision, chunkIndex = response.chunkIndex,
        initialHistory = response.chunkIndex == 0 && response.historyFrames > 0
            ? new ArdyInitialHistory { conditioningFrames = response.historyFrames } : null
    };
    private static void CheckProtocol(ArdyGenerateResponse[] responses)
    {
        for (int i = 0; i < responses.Length; i++)
        {
            Check(responses[i].chunkIndex == i, "Fixture files are not ordered actual chunks");
            ArdyLiveProtocol.ValidateResponse(responses[i], RequestFor(responses[i]));
            Check(responses[i].provenance.mode == "dynamic-ardy-native", "Fixture is not native live ARDY");
        }
        foreach (Action<ArdyGenerateResponse> change in new Action<ArdyGenerateResponse>[] {
            r => r.characterId += "-wrong", r => r.turnId += "-wrong", r => r.requestId += "-wrong", r => r.revision++,
            r => r.chunkIndex++, r => r.startFrame++, r => r.provenance.featureContract = "wrong-contract",
            r => r.provenance.mode = "fixture", r => r.provenance.featureSource = "fixture", r => r.provenance.modelSha256 = "wrong-model",
            r => r.historyFrames = 0, r => r.final = true, r => r.clip.jointParents[6] = 0 })
        {
            var altered = Clone(responses[0]); change(altered);
            Reject(() => ArdyLiveProtocol.ValidateResponse(altered, RequestFor(responses[0])), "Invalid response passed protocol validation");
        }
        var followup = Clone(responses[1]);
        followup.provenance.featureSource = "live-qwen";
        Reject(() => ArdyLiveProtocol.ValidateResponse(followup, RequestFor(responses[1])), "Continuation did not require reused live condition");
        foreach (int count in new[] { 4, 8, 16 })
        {
            var shortFirst = Clone(responses[0]); shortFirst.historyFrames = count;
            var shortRequest = RequestFor(shortFirst);
            ArdyLiveProtocol.ValidateResponse(shortFirst, shortRequest);
            Check(shortRequest.initialHistory.conditioningFrames == count, "First-window history did not bind requested length");
            shortFirst.historyFrames = count == 16 ? 8 : 16;
            Reject(() => ArdyLiveProtocol.ValidateResponse(shortFirst, shortRequest), "Mismatched requested history length accepted");
            var shortContinuation = Clone(responses[1]); shortContinuation.historyFrames = count;
            if (count == 16) ArdyLiveProtocol.ValidateResponse(shortContinuation, RequestFor(responses[1]));
            else Reject(() => ArdyLiveProtocol.ValidateResponse(shortContinuation, RequestFor(responses[1])), "Short continuation history accepted");
        }
    }

    private static void CheckRequestSerialization(ArdyGenerateResponse[] responses)
    {
        var first = RequestFor(responses[0]);
        first.description = "A person turns the wrists \"outward\" near the chest.";
        first.initialHistory = new ArdyInitialHistory { frames = new[] { responses[0].clip.frames[0] } };
        string json = ArdyLiveProtocol.SerializeRequest(first);
        var objectJson = Newtonsoft.Json.Linq.JObject.Parse(json);
        Check(objectJson["initialHistory"] != null && objectJson["initialHistory"]["frames"].Count() == 1,
            "First request did not include explicitly supplied history");
        var decoded = JsonUtility.FromJson<ArdyGenerateRequest>(json);
        Check(decoded.description == first.description && decoded.initialHistory.frames.Length == 1,
            "Request serialization changed quoted description/history");
        Check(objectJson["initialHistory"]["conditioningFrames"] == null,
            "Default 16-frame request added a field rejected by the legacy strict server");
        Check(decoded.initialHistory.conditioningFrames == 16,
            "Legacy request without conditioningFrames lost its 16-frame default");
        foreach (int count in new[] { 4, 8 })
        {
            first.initialHistory.conditioningFrames = count;
            var shortJson = Newtonsoft.Json.Linq.JObject.Parse(ArdyLiveProtocol.SerializeRequest(first));
            Check((int)shortJson["initialHistory"]["conditioningFrames"] == count,
                "Explicit short-history experiment was not serialized");
            Check(JsonUtility.FromJson<ArdyGenerateRequest>(shortJson.ToString()).initialHistory.conditioningFrames == count,
                "Explicit short-history length did not survive request roundtrip");
        }
        foreach (int count in new[] { 0, 2, -4, 12, 32 })
        {
            first.initialHistory.conditioningFrames = count;
            Reject(() => ArdyLiveProtocol.SerializeRequest(first), "Invalid initial conditioning length serialized");
        }
        first.initialHistory.conditioningFrames = 16;
        for (int i = 1; i < 3; i++)
        {
            var continuation = RequestFor(responses[i]);
            continuation.description = first.description;
            json = ArdyLiveProtocol.SerializeRequest(continuation);
            objectJson = Newtonsoft.Json.Linq.JObject.Parse(json);
            Check(objectJson["initialHistory"] == null, "Continuation serialized null history as a default object");
            Check((string)objectJson["description"] == first.description && (int)objectJson["chunkIndex"] == i,
                "Continuation description/sequence failed JSON roundtrip");
        }
    }

    private static void CheckHistory(Fixture fixture, AvatarResult result)
    {
        // Set an independently specified canonical pose, through actual world-space humanoid bones.
        var expected = new Quaternion[Human.Length];
        for (int i = 0; i < Human.Length; i++) expected[i] = Quaternion.Euler(3 + i * 0.9f, -11 + i * 1.3f, 2 + i * 0.4f);
        for (int pass = 0; pass < 2; pass++)
        {
            fixture.Root.transform.rotation = Quaternion.Euler(0, pass == 0 ? 31 : -83, 0);
            for (int i = 0; i < Human.Length; i++)
                fixture.Bone(Human[i]).rotation = fixture.Root.transform.rotation * expected[i] * fixture.Rest[Human[i]];
            ArdyMotionFrame history = fixture.Player.CaptureUpperBodyHistoryFrame();
            for (int i = 0; i < Human.Length; i++)
            {
                float error = Quaternion.Angle(history.globalRotations[Core[i]], expected[i]);
                result.historyReconstructionMaxDegrees = Mathf.Max(result.historyReconstructionMaxDegrees, error);
                if (pass == 1) result.historyYawInvarianceMaxDegrees = Mathf.Max(result.historyYawInvarianceMaxDegrees, error);
                Check(error < 0.1f, "Rendered pose did not reconstruct canonical motion: " + Human[i]);
            }
            foreach (int core in new[] { 0, 19, 20, 21, 22, 23, 24, 25, 26 })
                Check(Quaternion.Angle(history.globalRotations[core], Quaternion.identity) < 0.05f, "History claimed observed root/legs");
            foreach (var pair in new[] { new[] { 11, 10 }, new[] { 12, 10 }, new[] { 17, 16 }, new[] { 18, 16 } })
                Check(Quaternion.Angle(history.globalRotations[pair[0]], history.globalRotations[pair[1]]) < 0.05f, "Hand endpoint did not follow hand");
            Check(Quaternion.Angle(history.globalRotations[2], Quaternion.Slerp(expected[0], expected[1], 0.5f)) < 0.1f,
                "Missing Spine1 projection did not interpolate adjacent observed links");
        }
        foreach (var pair in fixture.Baseline) pair.Key.localRotation = pair.Value;
    }

    private static void CheckTimeline(Fixture fixture, ArdyGenerateResponse[] responses, AvatarResult result)
    {
        fixture.Player.PlayStream(Clone(responses[0]).clip, ArdyMotionMask.UpperBody, false);
        fixture.Step(0.25f);
        float beforeTime = Get<float>(fixture.Player, "time"), beforeBlend = Get<float>(fixture.Player, "transitionTime");
        var before = fixture.Pose();
        Reject(() => fixture.Player.AppendStreamChunk(Clone(responses[1]).clip, 0, false), "Out-of-order chunk accepted");
        fixture.Player.AppendStreamChunk(Clone(responses[1]).clip, 40, false);
        Check(Get<float>(fixture.Player, "time") == beforeTime && Get<float>(fixture.Player, "transitionTime") == beforeBlend,
            "Appending a window restarted time or blend");
        result.appendPoseChangeDegrees = fixture.PoseError(before);
        Check(result.appendPoseChangeDegrees < 0.08f, "Appending window immediately changed pose");
        fixture.Step(0.4f);
        beforeTime = Get<float>(fixture.Player, "time"); beforeBlend = Get<float>(fixture.Player, "transitionTime");
        fixture.Player.AppendStreamChunk(Clone(responses[2]).clip, 80, true);
        var timeline = Get<ArdyMotionClip>(fixture.Player, "clip");
        result.totalStreamFrames = timeline.frames.Length;
        Check(result.totalStreamFrames == 120 && !fixture.Player.CanAppendStream, "Three windows did not form a closed 120-frame timeline");
        Check(Get<float>(fixture.Player, "time") == beforeTime && Get<float>(fixture.Player, "transitionTime") == beforeBlend,
            "Final append restarted playback");
        for (int window = 0; window < 3; window++)
            for (int frame = 0; frame < 40; frame++)
                for (int joint = 0; joint < 27; joint++)
                    Check(Quaternion.Angle(timeline.frames[window * 40 + frame].globalRotations[joint],
                        responses[window].clip.frames[frame].globalRotations[joint]) < 0.08f, "Timeline dropped/replayed/rewrote a source frame");
        Reject(() => fixture.Player.AppendStreamChunk(Clone(responses[2]).clip, 80, true), "Late append accepted after final chunk");
        for (int i = 0; i < 145; i++) fixture.Step(0.05f);
        result.idleRecoveryMaxDegrees = fixture.IdleError();
        Check(!fixture.Player.IsPlaying && result.idleRecoveryMaxDegrees < 0.1f, "Complete stream failed to return to idle");
        Reject(() => fixture.Player.AppendStreamChunk(Clone(responses[1]).clip, 40, false), "Expired stream accepted stale chunk");

        fixture.Player.PlayStream(Clone(responses[0]).clip, ArdyMotionMask.UpperBody, false);
        for (int i = 0; i < 50 && !Get<bool>(fixture.Player, "stopping"); i++) fixture.Step(0.05f);
        Check(Get<bool>(fixture.Player, "stopping") && !string.IsNullOrEmpty(fixture.Player.StreamFailure), "Underrun did not begin controlled recovery");
        before = fixture.Pose(); fixture.Player.Tick(0);
        Check(fixture.PoseError(before) < 0.1f, "Underrun snapped immediately to idle");
        float previousError = fixture.IdleError();
        for (int i = 0; i < 8; i++)
        {
            fixture.Step(0.05f);
            float error = fixture.IdleError();
            Check(error <= previousError + 0.1f, "Underrun recovery moved away from fixed idle");
            previousError = error;
        }
        result.underrunRecoveryMaxDegrees = fixture.IdleError();
        Check(!fixture.Player.IsPlaying && result.underrunRecoveryMaxDegrees < 0.1f, "Underrun failed to release bones");
        Reject(() => fixture.Player.AppendStreamChunk(Clone(responses[1]).clip, 40, false), "Underrun stream accepted expired append");
    }

    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Fields).GetValue(target);
    private static ArdyGenerateResponse Clone(ArdyGenerateResponse value) => JsonUtility.FromJson<ArdyGenerateResponse>(JsonUtility.ToJson(value));
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, message);
    }
    private static string Sha(string path)
    {
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static string Argument(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, key);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
