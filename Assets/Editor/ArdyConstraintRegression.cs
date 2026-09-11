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

/// <summary>Actual player-loop geometry/lifecycle acceptance for explicit constraints; never generates ARDY motion.</summary>
[InitializeOnLoad]
public static partial class ArdyConstraintRegression
{
    private const string Key = "NeEEvA.ARDY.ConstraintRegression.";
    private const string Controller = "Assets/AIChatTookit/Animation/Animator Controller.controller";
    private static string Output => Path.GetFullPath(Path.Combine(Application.dataPath, SessionState.GetBool(Key + "TempoOnly", false) ? "../Logs/ardy-tempo" : SessionState.GetBool(Key + "PalmOnly", false) ? "../Logs/ardy-palms" : "../Logs/ardy-constraints"));
    private static readonly HumanBodyBones[] Arms = {
        HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
        HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand };
    private static readonly HumanBodyBones[] Untouched = {
        HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder,
        HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck,
        HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes,
        HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.RightToes };
    private static readonly string[] Assets = { "Assets/Model/NEVA.vrm", "Assets/Model/NeEEvA.vrm", "Assets/Model/NEVA.vrm", "Assets/Model/NeEEvA.vrm" };
    private static readonly float[] Scales = { 1, .8f, 1.2f, 1 };

    [Serializable] private sealed class SavedScenes { public SavedScene[] scenes; }
    [Serializable] private sealed class SavedScene { public string path; public bool isLoaded, isActive; }
    [Serializable] private sealed class BoneValue { public string bone; public Vector3 localPosition, rootPosition; public Quaternion localRotation, rootRotation; }
    [Serializable] private sealed class Sample
    {
        public int frame;
        public float time, realtime, caseSeconds;
        public string phase, failure, observation;
        public bool constrained, holding;
        public float leftDirectionError, rightDirectionError, leftBend, rightBend, leftBendError, rightBendError;
        public float leftWristAngle, rightWristAngle, headAngle;
        public Vector3 leftShoulderInRoot, leftElbowInRoot, leftWristInRoot, rightShoulderInRoot, rightElbowInRoot, rightWristInRoot;
        public PalmSample palms;
    }
    [Serializable] private sealed class CaseReport
    {
        public string name, plan;
        public float startedAt, finishedAt;
        public int actingFrames, renderedFrames;
        public bool enteredActing, enteredHolding;
        public float maximumDirectionError, maximumBendError, maximumShoulderVariation, maximumElbowVariation, maximumArmRotationVariation;
        public float maximumAbsoluteShoulderTravel, maximumAbsoluteElbowTravel;
        public string stabilityBasis = "Shoulder residual against synchronized Animator control; shoulder-to-elbow vector and arm rotations in character frame, or chest frame for current. Absolute root travel retained separately.";
        public float leftWristMinimum = 999, leftWristMaximum = -999, rightWristMinimum = 999, rightWristMaximum = -999;
        public float headMinimum = 999, headMaximum = -999;
        public int leftWristSignChanges, rightWristSignChanges, headSignChanges;
        public float returnToAnimatorMaxErrorDegrees, holdDuration;
        public List<Sample> samples = new List<Sample>();
        public PalmCase palm;
    }
    [Serializable] private sealed class FixtureReport
    {
        public string id, avatar, branch, avatarSha256;
        public float yaw, scale, animatorTimeAdvance;
        public float untouchedBoneMaxErrorDegrees, untouchedPositionMaxError, expressionMaxError;
        public float mouthMinimum = 100, mouthMaximum, blinkMinimum = 100, blinkMaximum;
        public int renderedFrames;
        public List<string> pngSha256 = new List<string>();
        public List<CaseReport> cases = new List<CaseReport>();
    }
    [Serializable] private sealed class Report
    {
        public string status = "running", unityVersion, playerSha256, planSha256, sessionSha256, observerSha256, harnessSha256, controllerSha256, bridgeSha256, palmHarnessSha256, palmGeometrySha256, parserSha256;
        public string route = "Explicit geometric arm constraints plus bounded local curves; not native ARDY generation or adapter training.";
        public string execution = "Actual Animator / Player LateUpdate / VRM LateUpdate / observer12000 / independent test12500. No manual Tick, Animator.Update or VRM Process.";
        public string rendering = "Approximately20Hz actual live poses, each frame BakeMesh; exact frameCount/gameTime/realtime recorded. No resampling or pose corrections. Numeric checks run every actual frame; stored pose samples are capped at80Hz plus phase transitions.";
        public string expressions = "Real BlinkController and Audio2LipScript.Update on NEVA Humanoid; synthetic aa input only, no audio playback or network.";
        public string dialogueLifecycle = "Inactive ChatSample prevents Awake/Start/network; its actual dispatch and cancellation events run through the real Bridge and Controller into live constraints. User-speaking/hold/TTL/current/explicit-stop are verified on subsequent actual frames.";
        public string limitation = "Isolated imported models and actual update loops. Geometric/lifecycle acceptance does not automatically establish subjective naturalness or scene-specific collisions.";
        public int checks, actualFrames;
        public float wallSeconds;
        public bool manualTicksUsed, servicesStarted, originalSceneSaved;
        public List<FixtureReport> fixtures = new List<FixtureReport>();
    }

    private sealed partial class Fixture
    {
        public GameObject Root, Control;
        public Vrm10Instance Vrm, ControlVrm;
        public Animator Animator, ControlAnimator;
        public ArdyMotionPlayer Player;
        public FixtureReport Report;
        public ArdyControlPlan Plan;
        public CaseReport Current;
        public Vector3 StartPosition, StartScale;
        public Quaternion StartRotation;
        public Vector3 LeftGoal, RightGoal;
        public Vector3 LeftCurrentGoalInChest, RightCurrentGoalInChest;
        public float LeftExpectedBend, RightExpectedBend;
        public Quaternion LeftNeutral, RightNeutral, HeadNeutral;
        public Vector3[] StablePositions;
        public Vector3[] StableAbsolutePositions;
        public Quaternion[] StableArmRotations;
        public bool HadActing;
        public int LeftSign, RightSign, HeadSign;
        public float HoldingStarted = -1, AnimatorStart;
        public SkinnedMeshRenderer Face;
        public Audio2LipScript Lips;
        public OVRLipSync.Frame Visemes;
        public int MouthIndex, BlinkIndex;
        public HashSet<int> DynamicExpressionIndices;
        public float ExpectedMouth;
        public int InjectedFrame;
        public ArdyLiveMotionController Live;
        public ArdyDialogueMotionBridge Bridge;
        public ChatSample Chat;
        public int Generation;
        public Sample LatestSample;
        private float lastStoredTime = -999;
        private string lastStoredPhase;
        public Transform Bone(HumanBodyBones bone) => Vrm.Humanoid.GetBoneTransform(bone);
        public Transform ControlBone(HumanBodyBones bone) => ControlVrm.Humanoid.GetBoneTransform(bone);
        public Vector3 InRoot(Transform bone) => Root.transform.InverseTransformPoint(bone.position);
        public Quaternion RotationInRoot(Transform bone) => Quaternion.Inverse(Root.transform.rotation) * bone.rotation;
        public Transform Chest => Bone(HumanBodyBones.UpperChest) ?? Bone(HumanBodyBones.Chest) ?? Bone(HumanBodyBones.Spine);

        public Fixture(int index)
        {
            Root = GameObject.Find("Constraint-Avatar-" + index);
            Control = GameObject.Find("Constraint-Control-" + index);
            Check(Root != null && Control != null, "Missing saved temporary fixtures");
            Vrm = Root.GetComponent<Vrm10Instance>(); ControlVrm = Control.GetComponent<Vrm10Instance>();
            Animator = Root.GetComponent<Animator>(); ControlAnimator = Control.GetComponent<Animator>();
            bool rig = index >= 2;
            foreach (var vrm in new[] { Vrm, ControlVrm })
            {
                if (rig)
                {
                    typeof(Vrm10Instance).GetField("m_useControlRig", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(vrm, true);
                    Check(vrm.Runtime.ControlRig != null, "Real ControlRig did not construct");
                    if (index == 3) vrm.transform.rotation = Quaternion.Euler(0, 65, 0);
                    vrm.enabled = true;
                }
                else vrm.enabled = false;
            }
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(Controller);
            foreach (var animator in new[] { Animator, ControlAnimator })
            {
                animator.runtimeAnimatorController = controller;
                animator.enabled = true; animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.SetInteger("state", 0); animator.Play("Base Layer.idle", 0, 0);
            }
            StartPosition = Root.transform.position; StartRotation = Root.transform.rotation; StartScale = Root.transform.localScale;
            Player = Root.AddComponent<ArdyMotionPlayer>();
            Player.Bind(Vrm);
            Root.AddComponent<ArdyConstraintObserver>().Bind(Player);
            Report = new FixtureReport { id = "fixture-" + index, avatar = Assets[index], avatarSha256 = Hash(Assets[index]),
                branch = rig ? "Real ControlRig with real Animator and VRM LateUpdate" : "Humanoid with disabled VRM and real Animator",
                yaw = Root.transform.eulerAngles.y, scale = Scales[index] };
            report.fixtures.Add(Report);
            AnimatorStart = Animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
            foreach (var mesh in Root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) mesh.updateWhenOffscreen = true;
            if (index == 0) SetUpExpressions();
        }

        private void SetUpExpressions()
        {
            string[] names = { "Fcl_MTH_A", "Fcl_MTH_I", "Fcl_MTH_U", "Fcl_MTH_E", "Fcl_MTH_O", "Fcl_EYE_Close" };
            Face = Root.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r => r.sharedMesh != null && names.All(n => r.sharedMesh.GetBlendShapeIndex(n) >= 0));
            Check(Face != null, "Actual NEVA mouth/blink blendshapes missing");
            int[] at = names.Select(n => Face.sharedMesh.GetBlendShapeIndex(n)).ToArray();
            DynamicExpressionIndices = new HashSet<int>(at);
            MouthIndex = at[0]; BlinkIndex = at[5];
            var audio = Root.AddComponent<AudioSource>(); audio.playOnAwake = false; audio.Stop(); audio.enabled = false;
            Lips = Root.AddComponent<Audio2LipScript>(); Lips.meshRenderer = Face;
            Lips.m_VisemeIndex = new Audio2LipScript.VisemeBlenderShapeIndexMap { A = at[0], I = at[1], U = at[2], E = at[3], O = at[4] };
            Visemes = (OVRLipSync.Frame)typeof(Audio2LipScript).GetField("frame", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Lips);
            uint context = (uint)typeof(Audio2LipScript).GetField("Context", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Lips);
            Check(context != 0 && Visemes != null, "Actual Audio2Lip native context unavailable");
            var blink = Root.AddComponent<BlinkController>(); blink.skinnedMeshRenderer = Face; blink.blinkBlendIndex = BlinkIndex;
            blink.blinkInterval = .6f; blink.blinkDuration = .1f;
            int sentinel = Enumerable.Range(0, Face.sharedMesh.blendShapeCount).First(i => !DynamicExpressionIndices.Contains(i));
            Face.SetBlendShapeWeight(sentinel, 17);
            string facePath = AnimationUtility.CalculateTransformPath(Face.transform, Root.transform);
            Control.transform.Find(facePath).GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(sentinel, 17);
            InjectExpression();
        }

        public void InjectExpression()
        {
            if (Lips == null) return;
            Array.Clear(Visemes.Visemes, 0, Visemes.Visemes.Length);
            float value = .5f + .45f * Mathf.Sin(Time.time * 2 * Mathf.PI / .8f);
            Visemes.Visemes[(int)OVRLipSync.Viseme.aa] = value;
            ExpectedMouth = (int)(100 * value); InjectedFrame = Time.frameCount;
        }

        public void Begin(string name, ArdyControlPlan plan, bool viaBridge = false, bool viaController = false)
        {
            PrepareConstraintCase(name, plan);
            DispatchConstraintCase(plan, viaBridge, viaController);
        }

        // Bookkeeping can follow a synchronous real semantic Chat dispatch without redispatching it.
        public void PrepareConstraintCase(string name, ArdyControlPlan plan)
        {
            Plan = plan.Copy(); HadActing = false; HoldingStarted = -1; LeftSign = RightSign = HeadSign = 0;
            lastStoredTime = -999; lastStoredPhase = null;
            LeftGoal = Goal(plan.left, true); RightGoal = Goal(plan.right, false);
            LeftCurrentGoalInChest = Chest.InverseTransformDirection(Root.transform.TransformDirection(LeftGoal));
            RightCurrentGoalInChest = Chest.InverseTransformDirection(Root.transform.TransformDirection(RightGoal));
            LeftExpectedBend = plan.left == "current" ? ActualBend(true) : plan.leftBend;
            RightExpectedBend = plan.right == "current" ? ActualBend(false) : plan.rightBend;
            Current = new CaseReport { name = name, plan = JsonUtility.ToJson(plan), startedAt = Time.time };
            SetUpPalmCase();
            Report.cases.Add(Current);
            CaptureNeutral();
        }

        private void DispatchConstraintCase(ArdyControlPlan plan, bool viaBridge, bool viaController)
        {
            if (viaBridge)
            {
                EnsureBridge(); Generation++;
                typeof(ChatSample).GetField("m_FormalResponseGeneration", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Chat, Generation);
                typeof(ChatSample).GetMethod("DispatchDialogueMotion", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Chat,
                    new object[] { (DialogueMotionIntent?)new DialogueMotionIntent("compose", "", Generation, Generation, plan), false });
                Check(Player.IsConstrained, "Actual Chat event / Bridge / Controller did not dispatch constraints");
            }
            else if (viaController) { EnsureBridge(); Live.RequestControlPlan(plan, ++Generation); }
            else Player.PlayConstrained(plan);
        }

        public void EnsureBridge(bool semantic = false)
        {
            if (Bridge != null) return;
            var chatObject = new GameObject("Inactive no-network constraint chat " + Report.id); chatObject.SetActive(false);
            Chat = chatObject.AddComponent<ChatSample>();
            Chat.ConfigureSemanticMotionPlanning(semantic);
            Bridge = Root.AddComponent<ArdyDialogueMotionBridge>(); Bridge.Bind(Chat, Vrm);
            Live = Root.GetComponent<ArdyLiveMotionController>(); Live.recordMotionDiagnostics = true;
            Check(Bridge.IsBound && Live.IsBound && !chatObject.activeInHierarchy, "Isolated no-network bridge did not bind");
        }

        public void Conversation(string reason) => typeof(ChatSample).GetMethod("CancelDialogueMotion", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Chat, new object[] { reason });
        private float ActualBend(bool left)
        {
            var shoulder = Bone(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            var elbow = Bone(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            var wrist = Bone(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            return 180 - Vector3.Angle(shoulder.position - elbow.position, wrist.position - elbow.position);
        }

        private Vector3 ExpectedDirection(bool left)
        {
            if ((left ? Plan?.left : Plan?.right) != "current") return left ? LeftGoal : RightGoal;
            return Root.transform.InverseTransformDirection(Chest.TransformDirection(left ? LeftCurrentGoalInChest : RightCurrentGoalInChest));
        }

        private Vector3 ShoulderResidual(bool left)
        {
            var bone = left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm;
            return InRoot(Bone(bone)) - Control.transform.InverseTransformPoint(ControlBone(bone).position);
        }

        private Vector3 ElbowVector(bool left)
        {
            Vector3 vector = Bone(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm).position - Bone(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm).position;
            return (left ? Plan.left : Plan.right) == "current" ? Chest.InverseTransformDirection(vector) / Report.scale : Root.transform.InverseTransformVector(vector);
        }

        private Vector3 Goal(string goal, bool left)
        {
            Vector3 current = InRoot(Bone(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand)) - InRoot(Bone(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm));
            float lateral = InRoot(Bone(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm)).x - InRoot(Bone(left ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm)).x;
            Vector3 outward = lateral < 0 ? Vector3.left : Vector3.right;
            switch (goal)
            {
                case "current": return current.normalized;
                case "forward": return Vector3.forward;
                case "outward": return outward;
                case "up": return Vector3.up;
                case "down": return Vector3.down;
                case "forward-up": return (Vector3.forward + Vector3.up).normalized;
                case "outward-up": return (outward + Vector3.up).normalized;
                default: return current.normalized;
            }
        }

        private void CaptureNeutral()
        {
            LeftNeutral = RotationInRoot(Bone(HumanBodyBones.LeftHand)); RightNeutral = RotationInRoot(Bone(HumanBodyBones.RightHand)); HeadNeutral = RotationInRoot(Bone(HumanBodyBones.Head));
        }

        public Sample Observe()
        {
            Sample sample = new Sample { frame = Time.frameCount, time = Time.time, realtime = Time.realtimeSinceStartup,
                caseSeconds = Current == null ? 0 : Time.time - Current.startedAt, phase = Player.ConstraintPhase,
                failure = Player.ConstraintFailure, constrained = Player.IsConstrained, holding = Player.IsHoldingPose,
                observation = JsonUtility.ToJson(Player.ConstraintObservation) };
            sample.leftShoulderInRoot = InRoot(Bone(HumanBodyBones.LeftUpperArm)); sample.leftElbowInRoot = InRoot(Bone(HumanBodyBones.LeftLowerArm)); sample.leftWristInRoot = InRoot(Bone(HumanBodyBones.LeftHand));
            sample.rightShoulderInRoot = InRoot(Bone(HumanBodyBones.RightUpperArm)); sample.rightElbowInRoot = InRoot(Bone(HumanBodyBones.RightLowerArm)); sample.rightWristInRoot = InRoot(Bone(HumanBodyBones.RightHand));
            sample.leftDirectionError = Vector3.Angle(sample.leftWristInRoot - sample.leftShoulderInRoot, ExpectedDirection(true));
            sample.rightDirectionError = Vector3.Angle(sample.rightWristInRoot - sample.rightShoulderInRoot, ExpectedDirection(false));
            sample.leftBend = 180 - Vector3.Angle(sample.leftShoulderInRoot - sample.leftElbowInRoot, sample.leftWristInRoot - sample.leftElbowInRoot);
            sample.rightBend = 180 - Vector3.Angle(sample.rightShoulderInRoot - sample.rightElbowInRoot, sample.rightWristInRoot - sample.rightElbowInRoot);
            var constraint = Player.ConstraintObservation;
            if (Plan != null && constraint != null)
            {
                if (Plan.leftBendAuto) LeftExpectedBend = constraint.leftResolvedBend;
                if (Plan.rightBendAuto) RightExpectedBend = constraint.rightResolvedBend;
            }
            sample.leftBendError = Plan == null ? 0 : Mathf.Abs(sample.leftBend - LeftExpectedBend);
            sample.rightBendError = Plan == null ? 0 : Mathf.Abs(sample.rightBend - RightExpectedBend);
            LatestSample = sample;
            if (Current == null) return sample;
            if (sample.phase == "preparing") CaptureNeutral();
            Vector3 axis = Plan.axis == "right" ? Vector3.right : Plan.axis == "forward" ? Vector3.forward : Vector3.up;
            sample.leftWristAngle = SignedAngle(RotationInRoot(Bone(HumanBodyBones.LeftHand)) * Quaternion.Inverse(LeftNeutral), axis);
            sample.rightWristAngle = SignedAngle(RotationInRoot(Bone(HumanBodyBones.RightHand)) * Quaternion.Inverse(RightNeutral), axis);
            sample.headAngle = SignedAngle(RotationInRoot(Bone(HumanBodyBones.Head)) * Quaternion.Inverse(HeadNeutral), axis);
            ObservePalm(sample);
            if (Time.time - lastStoredTime >= 1f / 80 || sample.phase != lastStoredPhase)
            {
                Current.samples.Add(sample); lastStoredTime = Time.time; lastStoredPhase = sample.phase;
            }
            if (sample.phase == "acting")
            {
                if (!HadActing)
                {
                    StablePositions = new[] { ShoulderResidual(true), ShoulderResidual(false), ElbowVector(true), ElbowVector(false) };
                    StableAbsolutePositions = new[] { sample.leftShoulderInRoot, sample.rightShoulderInRoot, sample.leftElbowInRoot, sample.rightElbowInRoot };
                    StableArmRotations = StableRotations(); HadActing = true;
                }
                Current.enteredActing = true; Current.actingFrames++;
                if (!ExpectInjectedPalmMismatch) Geometry(sample);
                var positions = new[] { ShoulderResidual(true), ShoulderResidual(false), ElbowVector(true), ElbowVector(false) };
                float shoulder = Mathf.Max(positions[0].magnitude, positions[1].magnitude);
                float elbow = Mathf.Max(Plan.left == "none" ? 0 : Vector3.Distance(positions[2], StablePositions[2]), Plan.right == "none" ? 0 : Vector3.Distance(positions[3], StablePositions[3]));
                Current.maximumAbsoluteShoulderTravel = Mathf.Max(Current.maximumAbsoluteShoulderTravel, Vector3.Distance(sample.leftShoulderInRoot, StableAbsolutePositions[0]), Vector3.Distance(sample.rightShoulderInRoot, StableAbsolutePositions[1]));
                Current.maximumAbsoluteElbowTravel = Mathf.Max(Current.maximumAbsoluteElbowTravel, Vector3.Distance(sample.leftElbowInRoot, StableAbsolutePositions[2]), Vector3.Distance(sample.rightElbowInRoot, StableAbsolutePositions[3]));
                float armRotation = StableRotations().Select((rotation, i) =>
                    ((i < 2 && Plan.left == "none") || (i >= 2 && Plan.right == "none") || (i == 1 && Plan.leftPalm != "keep") || (i == 3 && Plan.rightPalm != "keep")) ? 0 : Quaternion.Angle(rotation, StableArmRotations[i])).Max();
                Current.maximumShoulderVariation = Mathf.Max(Current.maximumShoulderVariation, shoulder);
                Current.maximumElbowVariation = Mathf.Max(Current.maximumElbowVariation, elbow);
                Current.maximumArmRotationVariation = Mathf.Max(Current.maximumArmRotationVariation, armRotation);
                if (!ExpectInjectedPalmMismatch)
                {
                    Check(shoulder <= .005f && elbow <= .008f, "Shoulder/elbow moved during local curve: " + Report.id);
                    Check(armRotation <= 2, "Upper/lower arm rotated during local curve: " + Report.id);
                }
                Track(sample.leftWristAngle, ref Current.leftWristMinimum, ref Current.leftWristMaximum, ref LeftSign, ref Current.leftWristSignChanges);
                Track(sample.rightWristAngle, ref Current.rightWristMinimum, ref Current.rightWristMaximum, ref RightSign, ref Current.rightWristSignChanges);
                Track(sample.headAngle, ref Current.headMinimum, ref Current.headMaximum, ref HeadSign, ref Current.headSignChanges);
            }
            if (sample.holding)
            {
                if (HoldingStarted < 0) HoldingStarted = Time.time;
                Current.enteredHolding = true; Current.holdDuration = Time.time - HoldingStarted;
                if (!ExpectInjectedPalmMismatch) Geometry(sample);
            }
            return sample;
        }

        private Quaternion[] StableRotations() => new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm }
            .Select((b, i) => (i < 2 ? Plan.left : Plan.right) == "current" ? Quaternion.Inverse(Chest.rotation) * Bone(b).rotation : RotationInRoot(Bone(b))).ToArray();
        private void Geometry(Sample sample)
        {
            Current.maximumDirectionError = Mathf.Max(Current.maximumDirectionError, sample.leftDirectionError, sample.rightDirectionError);
            Current.maximumBendError = Mathf.Max(Current.maximumBendError, sample.leftBendError, sample.rightBendError);
            if (Plan.left != "none") Check(sample.leftDirectionError <= 5 && sample.leftBendError <= 4, "Left target failed on actual mesh bones: " + Report.id);
            if (Plan.right != "none") Check(sample.rightDirectionError <= 5 && sample.rightBendError <= 4, "Right target failed on actual mesh bones: " + Report.id);
        }

        public void EndCase(bool checkCurve)
        {
            Current.finishedAt = Time.time;
            if (!checkCurve) return;
            Check(Current.enteredActing && Current.actingFrames >= 20, "No sufficiently sampled actual acting phase");
            int expected = Plan.cycles * 2 - 1;
            if (Plan.joint == "wrists" || Plan.joint == "left-wrist") Curve(Current.leftWristMinimum, Current.leftWristMaximum, Current.leftWristSignChanges, Plan.amplitude, expected, "left wrist");
            if (Plan.joint == "wrists" || Plan.joint == "right-wrist") Curve(Current.rightWristMinimum, Current.rightWristMaximum, Current.rightWristSignChanges, Plan.amplitude, expected, "right wrist");
            if (Plan.joint == "head") Curve(Current.headMinimum, Current.headMaximum, Current.headSignChanges, Plan.amplitude, expected, "head", 2.5f);
        }

        public void CheckUntouched(bool afterStop)
        {
            Check(Animator.enabled && ControlAnimator.enabled, "Constraint disabled original Animator");
            Check(Vector3.Distance(Root.transform.position, StartPosition) < .0001f && Quaternion.Angle(Root.transform.rotation, StartRotation) < .01f && Vector3.Distance(Root.transform.localScale, StartScale) < .0001f, "Constraint changed root transform");
            foreach (var human in Arms.Concat(Untouched).Concat(new[] { HumanBodyBones.Head }))
            {
                var bone = Bone(human); var mate = ControlBone(human); if (bone == null || mate == null) continue;
                Quaternion q = bone.localRotation;
                float norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                Check(!float.IsNaN(norm) && !float.IsInfinity(norm) && Mathf.Abs(norm - 1) < .001f, "Constraint emitted a nonfinite/nonunit rotation");
                Check(Vector3.Distance(bone.localPosition, mate.localPosition) < .0001f, "Constraint changed a bone's local translation");
            }
            foreach (var human in Untouched.Concat(afterStop ? Arms.Concat(new[] { HumanBodyBones.Head }) : Enumerable.Empty<HumanBodyBones>()))
            {
                var bone = Bone(human); var control = ControlBone(human); if (bone == null || control == null) continue;
                float error = Quaternion.Angle(bone.localRotation, control.localRotation), displacement = Vector3.Distance(bone.localPosition, control.localPosition);
                Report.untouchedBoneMaxErrorDegrees = Mathf.Max(Report.untouchedBoneMaxErrorDegrees, error);
                Report.untouchedPositionMaxError = Mathf.Max(Report.untouchedPositionMaxError, displacement);
                if (afterStop && Current != null) Current.returnToAnimatorMaxErrorDegrees = Mathf.Max(Current.returnToAnimatorMaxErrorDegrees, error);
                Check(error < .4f && displacement < .0001f, "Constraint changed untouched/returned Animator bone: " + Report.id + "/" + human + " " + error);
            }
            if (Face != null && Time.frameCount > InjectedFrame)
            {
                float mouth = Face.GetBlendShapeWeight(MouthIndex), blink = Face.GetBlendShapeWeight(BlinkIndex);
                Report.expressionMaxError = Mathf.Max(Report.expressionMaxError, Mathf.Abs(mouth - ExpectedMouth));
                Check(Mathf.Abs(mouth - ExpectedMouth) < .0001f, "Constraint overwrote actual Audio2Lip Update output");
                Report.mouthMinimum = Mathf.Min(Report.mouthMinimum, mouth); Report.mouthMaximum = Mathf.Max(Report.mouthMaximum, mouth);
                Report.blinkMinimum = Mathf.Min(Report.blinkMinimum, blink); Report.blinkMaximum = Mathf.Max(Report.blinkMaximum, blink);
            }
            foreach (var mesh in Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string path = AnimationUtility.CalculateTransformPath(mesh.transform, Root.transform);
                var mateTransform = path.Length == 0 ? Control.transform : Control.transform.Find(path);
                var mate = mateTransform == null ? null : mateTransform.GetComponent<SkinnedMeshRenderer>();
                if (mesh.sharedMesh == null || mate == null) continue;
                for (int i = 0; i < mesh.sharedMesh.blendShapeCount; i++)
                {
                    if (mesh == Face && DynamicExpressionIndices.Contains(i)) continue;
                    float error = Mathf.Abs(mesh.GetBlendShapeWeight(i) - mate.GetBlendShapeWeight(i));
                    Report.expressionMaxError = Mathf.Max(Report.expressionMaxError, error);
                    Check(error < .0001f, "Constraint changed unrelated expression channel");
                }
            }
            Report.animatorTimeAdvance = Animator.GetCurrentAnimatorStateInfo(0).normalizedTime - AnimatorStart;
        }

        public void CleanupExpression()
        {
            if (Lips == null) return;
            Lips.enabled = false;
            var field = typeof(Audio2LipScript).GetField("Context", BindingFlags.NonPublic | BindingFlags.Instance);
            uint context = (uint)field.GetValue(Lips); if (context != 0) { OVRLipSync.DestroyContext(context); field.SetValue(Lips, (uint)0); }
        }
    }

    private static Report report;
    private static Fixture[] fixtures;
    private static Camera camera;
    private static int stage, lastFrame, directionCase;
    private static float started, stageStarted, nextCapture;
    private static bool injectedFailure;
    private static bool missingObserverDetected;
    private static readonly ArdyControlPlan[] DirectionPlans = {
        new ArdyControlPlan { left = "forward", right = "outward", leftBend = 0, rightBend = 110, joint = "none", seconds = 1, cycles = 1, end = "idle" },
        new ArdyControlPlan { left = "up", right = "down", leftBend = 110, rightBend = 0, joint = "none", seconds = 1, cycles = 1, end = "idle" },
        new ArdyControlPlan { left = "forward-up", right = "outward-up", leftBend = 0, rightBend = 110, joint = "none", seconds = 1, cycles = 1, end = "idle" } };

    static ArdyConstraintRegression()
    {
        EditorApplication.update += EditorUpdate;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) SessionState.SetString(Key + "Stage", "ready");
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetString(Key + "Stage", "") == "exiting") EditorApplication.delayCall += FinalizeBatch;
        };
    }

    public static void RunBatch() => BeginBatch(false);
    public static void RunPrimaryBatch() => BeginBatch(true);
    private static void BeginBatch(bool primaryOnly, bool palmOnly = false, bool palmPrimaryOnly = false, bool tempoOnly = false)
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Run this exiting test only in the isolated validation project");
        try
        {
            Check(!EditorApplication.isPlayingOrWillChangePlaymode, "PlayMode already active");
            for (int i = 0; i < SceneManager.sceneCount; i++) Check(!SceneManager.GetSceneAt(i).isDirty, "Unsaved scene must not be replaced");
            SessionState.SetString(Key + "Scenes", JsonUtility.ToJson(new SavedScenes { scenes = EditorSceneManager.GetSceneManagerSetup().Select(s => new SavedScene { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray() }));
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(Controller);
            Check(controller != null, "Actual Animator controller missing");
            for (int i = 0; i < 4; i++)
            for (int pair = 0; pair < 2; pair++)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Assets[i]); Check(prefab != null, "Real VRM prefab missing " + Assets[i]);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.name = (pair == 0 ? "Constraint-Avatar-" : "Constraint-Control-") + i;
                instance.transform.SetPositionAndRotation(new Vector3(i * 5, 0, pair * -4), Quaternion.Euler(0, i == 0 ? 25 : i == 3 ? 0 : 65, 0));
                instance.transform.localScale = Vector3.one * Scales[i];
                var vrm = instance.GetComponent<Vrm10Instance>(); vrm.enabled = false; vrm.UpdateType = Vrm10Instance.UpdateTypes.LateUpdate;
                var animator = instance.GetComponent<Animator>(); animator.runtimeAnimatorController = i < 2 ? controller : null;
                animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.enabled = i < 2;
                foreach (var bone in instance.GetComponentsInChildren<Transform>(true)) bone.gameObject.layer = 30;
            }
            SessionState.SetBool(Key + "Active", true); SessionState.SetString(Key + "Stage", "entering");
            SessionState.SetBool(Key + "PrimaryOnly", primaryOnly);
            SessionState.SetBool(Key + "PalmOnly", palmOnly);
            SessionState.SetBool(Key + "PalmPrimaryOnly", palmPrimaryOnly);
            SessionState.SetBool(Key + "TempoOnly", tempoOnly);
            SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 240);
            EditorApplication.isPlaying = true;
        }
        catch (Exception error) { Debug.LogException(error); SessionState.SetBool(Key + "Active", false); EditorApplication.Exit(1); }
    }

    private static void EditorUpdate()
    {
        if (!SessionState.GetBool(Key + "Active", false)) return;
        string state = SessionState.GetString(Key + "Stage", "");
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", float.MaxValue)) { Finish(new TimeoutException("Constraint player-loop regression timed out")); return; }
        if (state == "exiting") return;
        if (EditorApplication.isPlaying && state == "ready" && report == null && Time.time >= .5f)
        {
            try { Initialize(); } catch (Exception error) { Finish(error); }
        }
    }

    private static void Initialize()
    {
        Directory.CreateDirectory(Output);
        report = new Report { unityVersion = Application.unityVersion, playerSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyMotionPlayer.cs"),
            planSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyControlPlan.cs"), observerSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyConstraintObserver.cs"), harnessSha256 = Hash("Assets/Editor/ArdyConstraintRegression.cs") };
        report.sessionSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyConstraintSession.cs");
        report.controllerSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyLiveMotionController.cs");
        report.bridgeSha256 = Hash("Assets/AIChatTookit/Scripts/Chat/ArdyDialogueMotionBridge.cs");
        report.palmHarnessSha256 = Hash("Assets/Editor/ArdyConstraintPalmRegression.cs");
        report.palmGeometrySha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyPalmGeometry.cs");
        report.parserSha256 = Hash("Assets/AIChatTookit/Scripts/Chat/DialogueMotionIntent.cs");
        fixtures = Enumerable.Range(0, 4).Select(i => new Fixture(i)).ToArray();
        var runner = new GameObject("Constraint acceptance late probes");
        runner.AddComponent<ArdyConstraintRegressionFault>().callback = FaultLate;
        runner.AddComponent<ArdyConstraintRegressionSample>().callback = SampleLate;
        camera = new GameObject("Constraint capture camera").AddComponent<Camera>(); camera.enabled = false;
        camera.cullingMask = 1 << 31; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.78f, .83f, .87f);
        camera.orthographic = true; camera.nearClipPlane = .01f; camera.farClipPlane = 30;
        for (int i = 0; i < 2; i++) { var light = new GameObject("Constraint light " + i).AddComponent<Light>(); light.type = LightType.Directional; light.intensity = i == 0 ? .7f : .25f; light.cullingMask = 1 << 31; light.transform.rotation = Quaternion.Euler(i == 0 ? 30 : 340, i == 0 ? 200 : 25, 0); }
        started = Time.realtimeSinceStartup; stageStarted = Time.time; stage = 0; lastFrame = Time.frameCount;
        SessionState.SetString(Key + "Stage", "running");
    }

    private static void FaultLate()
    {
        if (!injectedFailure || fixtures == null) return;
        var fixture = fixtures[0];
        foreach (var human in Arms) { var bone = fixture.Bone(human); var control = fixture.ControlBone(human); if (bone != null && control != null) bone.localRotation = control.localRotation; }
    }

    private static void SampleLate()
    {
        if (report == null || SessionState.GetString(Key + "Stage", "") != "running" || Time.frameCount == lastFrame) return;
        lastFrame = Time.frameCount; report.actualFrames++;
        try
        {
            foreach (var fixture in fixtures) { fixture.Observe(); fixture.CheckUntouched(false); }
            float elapsed = Time.time - stageStarted;
            if (SessionState.GetBool(Key + "TempoOnly", false))
            {
                SampleTempoStages(elapsed);
                foreach (var f in fixtures) f.InjectExpression();
                return;
            }
            if (SessionState.GetBool(Key + "PalmOnly", false))
            {
                SamplePalmStages(elapsed);
                foreach (var f in fixtures) f.InjectExpression();
                return;
            }
            switch (stage)
            {
                case 0:
                    if (elapsed < .5f) break;
                    BeginAll("forward-wrists-two-cycles", new ArdyControlPlan { left = "forward", right = "forward", joint = "wrists", axis = "up", amplitude = 10, cycles = 2, seconds = 3.2f, end = "hold" }); Advance(); nextCapture = Time.time; break;
                case 1:
                    if (Time.time >= nextCapture) { Capture(fixtures[0]); Capture(fixtures[1]); nextCapture = Time.time + .05f; }
                    CheckNoFailure();
                    if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 8, "Forward constraints did not reach hold"); break; }
                    foreach (var f in fixtures) { f.EndCase(true); f.Player.Stop(); } Advance(); break;
                case 2:
                    if (Time.time >= nextCapture) { Capture(fixtures[0]); Capture(fixtures[1]); nextCapture = Time.time + .05f; }
                    if (elapsed < .7f) break;
                    CheckReturned();
                    if (SessionState.GetBool(Key + "PrimaryOnly", false))
                    {
                        Check(fixtures[0].Report.mouthMaximum - fixtures[0].Report.mouthMinimum > 30 && fixtures[0].Report.blinkMaximum - fixtures[0].Report.blinkMinimum > 50, "Actual expression updates did not continue during primary constraints");
                        Finish(null); break;
                    }
                    BeginAll("outward-arms-head-right", new ArdyControlPlan { left = "outward", right = "outward", joint = "head", axis = "right", amplitude = 8, cycles = 1, seconds = 1.6f, end = "hold" }); Advance(); break;
                case 3:
                    CapturePairIfDue();
                    CheckNoFailure();
                    if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 7, "Outward/head did not reach hold"); break; }
                    foreach (var f in fixtures) f.EndCase(true);
                    BeginAll("hold-to-current-left-forward-right", new ArdyControlPlan { left = "current", right = "forward", joint = "left-wrist", axis = "up", amplitude = 10, cycles = 1, seconds = 1.6f, end = "hold" }); Advance(); break;
                case 4:
                    CapturePairIfDue();
                    CheckNoFailure();
                    if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 7, "Current/forward transition did not hold"); break; }
                    foreach (var f in fixtures)
                    {
                        f.EndCase(true); f.EnsureBridge(); f.Player.enabled = false;
                        MustReject(() => f.Player.PlayConstrained(f.Plan), "Disabled Player accepted a queued constraint");
                        MustReject(() => f.Live.RequestControlPlan(f.Plan, 90), "Controller accepted constraints for disabled Player");
                    }
                    Advance(); break;
                case 5:
                    if (elapsed < .7f) break;
                    CheckReturned();
                    foreach (var f in fixtures) { f.Player.enabled = true; f.Player.Bind(f.Vrm); }
                    stage = 51; stageStarted = Time.time; break;
                case 51:
                    if (elapsed < .15f) break;
                    CheckReturned();
                    BeginAll("controller-hold-thirty-second-expiry", new ArdyControlPlan { left = "forward", right = "forward", joint = "none", seconds = 1, cycles = 1, end = "hold" }, true);
                    stage = 6; stageStarted = Time.time; SaveReport(); break;
                case 6:
                    CheckNoFailure();
                    foreach (var f in fixtures) if (f.HoldingStarted >= 0 && Time.time - f.HoldingStarted < 29) Check(f.Player.IsHoldingPose, "Hold expired before thirty seconds");
                    if (fixtures.Any(f => f.HoldingStarted < 0 || Time.time - f.HoldingStarted < 31)) { Check(elapsed < 36, "Hold expiry did not complete"); break; }
                    CheckReturned(); foreach (var f in fixtures) f.EndCase(false);
                    fixtures[0].Begin("unreached-preparation-must-fail", new ArdyControlPlan { left = "forward", right = "forward", joint = "wrists", end = "idle" });
                    injectedFailure = true; Advance(); break;
                case 7:
                    Check(!fixtures[0].Current.enteredActing, "Constraint entered acting despite late observed preparation failure");
                    if (elapsed < 3.25f) break;
                    Check(!string.IsNullOrEmpty(fixtures[0].Player.ConstraintFailure), "Preparation did not report failure by three-second deadline");
                    injectedFailure = false; Advance(); break;
                case 8:
                    if (elapsed < .7f) break;
                    CheckReturned(); fixtures[0].EndCase(false);
                    BeginAll("stop-during-local-motion", new ArdyControlPlan { left = "forward", right = "forward", joint = "wrists", seconds = 3.2f, cycles = 2, end = "idle" }); Advance(); break;
                case 9:
                    CheckNoFailure();
                    if (!fixtures.All(f => f.Current.actingFrames >= 8)) { Check(elapsed < 5, "Mid-action stop case never acted"); break; }
                    foreach (var f in fixtures) f.Player.Stop(); Advance(); break;
                case 10:
                    if (elapsed < .7f) break;
                    CheckReturned(); foreach (var f in fixtures) f.EndCase(false);
                    Check(fixtures[0].Report.mouthMaximum - fixtures[0].Report.mouthMinimum > 30 && fixtures[0].Report.blinkMaximum - fixtures[0].Report.blinkMinimum > 50, "Actual expression Update did not continue during constraints");
                    foreach (var f in fixtures) Check(f.Report.animatorTimeAdvance > 1, "Animator did not continue across constraint session");
                    directionCase = 0; BeginAll("direction-boundaries-0", DirectionPlans[0]); Advance(); break;
                case 11:
                    CheckNoFailure();
                    if (fixtures.Any(f => f.Player.IsConstrained) || elapsed < 2.8f) { Check(elapsed < 6, "Static directional case did not finish"); break; }
                    CheckReturned(); foreach (var f in fixtures) { Check(f.Current.actingFrames >= 10, "Direction case did not sample stable static pose"); f.EndCase(false); }
                    directionCase++;
                    if (directionCase < DirectionPlans.Length) { BeginAll("direction-boundaries-" + directionCase, DirectionPlans[directionCase]); stageStarted = Time.time; break; }
                    BeginAll("bridge-pure-hold-before-user-speaking", new ArdyControlPlan { left = "forward", right = "forward", joint = "none", seconds = 6, cycles = 1, end = "hold" }, true);
                    Advance(); break;
                case 12:
                    CheckNoFailure();
                    if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 2, "Pure hold waited for empty acting duration after preparation"); break; }
                    foreach (var f in fixtures)
                    {
                        Check(f.Current.actingFrames == 0 && f.Player.ConstraintObservation.goalsReached, "Pure hold did not enter holding directly from measured preparation");
                        long revision = f.Live.Revision;
                        f.Conversation("user-started-speaking"); f.Conversation("barge-in"); f.Conversation("new-user-turn");
                        Check(f.Player.IsHoldingPose && f.Live.Revision == revision, "Actual Chat cancellation event erased a settled requested pose");
                        Check(f.Live.DescribeMotionContext().Contains("\"previousRequestedPoseHeld\":true"), "Controller omitted actual held-pose fact");
                    }
                    Advance(); break;
                case 13:
                    if (elapsed < .35f) break;
                    foreach (var f in fixtures) { Check(f.Player.IsHoldingPose, "Conversation erased hold on a later player-loop frame"); f.EndCase(false); }
                    BeginAll("bridge-held-pose-to-current-wrists", new ArdyControlPlan { left = "current", right = "current", joint = "wrists", axis = "up", amplitude = 10, seconds = 3.2f, cycles = 2, end = "hold" }, true);
                    Advance(); break;
                case 14:
                    CapturePairIfDue(); CheckNoFailure();
                    if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 7, "Bridge did not complete current-pose wrist continuation"); break; }
                    foreach (var f in fixtures) { f.EndCase(true); f.Conversation("explicit-stop"); }
                    Advance(); break;
                case 15:
                    if (elapsed < .7f) break;
                    CheckReturned();
                    BeginAll("observer-loss-must-release-held-pose", new ArdyControlPlan { left = "forward", right = "forward", joint = "none", seconds = 1, cycles = 1, end = "hold" }); Advance(); break;
                case 16:
                    CheckNoFailure();
                    if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 2, "Observer fault fixture did not first establish hold"); break; }
                    foreach (var f in fixtures) f.Root.GetComponent<ArdyConstraintObserver>().enabled = false;
                    missingObserverDetected = false; Advance(); break;
                case 17:
                    if (fixtures.All(f => (f.Player.ConstraintFailure ?? "").Contains("observation-timeout")))
                    {
                        missingObserverDetected = true;
                        foreach (var f in fixtures) Check(!f.Player.ConstraintObservation.observationFresh, "Timed-out observation remained fresh");
                    }
                    if (elapsed < .9f) break;
                    Check(missingObserverDetected, "Missing real observer did not report timeout");
                    foreach (var f in fixtures)
                    {
                        Check(!f.Player.IsHoldingPose, "Missing observation left pose held");
                        f.Root.GetComponent<ArdyConstraintObserver>().enabled = true;
                    }
                    Advance(); break;
                case 18:
                    if (elapsed < .7f) break;
                    CheckReturned(); foreach (var f in fixtures) { Check(!f.Player.IsHoldingPose, "Restored observer resurrected failed hold"); f.EndCase(false); }
                    BeginAll("bridge-user-speaking-interrupts-active-curve", new ArdyControlPlan { left = "forward", right = "forward", joint = "wrists", seconds = 3.2f, cycles = 2, end = "idle" }, true); Advance(); break;
                case 19:
                    CheckNoFailure();
                    if (!fixtures.All(f => f.Current.actingFrames >= 8)) { Check(elapsed < 5, "Bridge interruption did not reach acting"); break; }
                    foreach (var f in fixtures) f.Conversation("user-started-speaking"); Advance(); break;
                case 20:
                    if (elapsed < .7f) break;
                    CheckReturned(); foreach (var f in fixtures) { f.EndCase(false); f.Bridge.Disconnect("regression-finished"); }
                    Finish(null); break;
            }
            foreach (var f in fixtures) f.InjectExpression();
        }
        catch (Exception error) { Finish(error); }
    }

    private static void BeginAll(string name, ArdyControlPlan plan, bool viaBridge = false) { foreach (var f in fixtures) f.Begin(name, plan, viaBridge); nextCapture = Time.time; }
    private static void CapturePairIfDue() { if (Time.time < nextCapture) return; Capture(fixtures[0]); Capture(fixtures[1]); nextCapture = Time.time + .05f; }
    private static void MustReject(Action action, string message) { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, message); }
    private static void Advance() { stage++; stageStarted = Time.time; SaveReport(); }
    private static void CheckNoFailure() { foreach (var f in fixtures) Check(string.IsNullOrEmpty(f.Player.ConstraintFailure), "Constraint execution failed: " + f.Report.id + " " + f.Player.ConstraintFailure); }
    private static void CheckReturned() { foreach (var f in fixtures) { Check(!f.Player.IsConstrained && !f.Player.IsHoldingPose, "Constraint remained active after stop/expiry/disable"); f.CheckUntouched(true); } }
    private static float SignedAngle(Quaternion value, Vector3 axis)
    {
        value = value.normalized;
        if (value.w < 0) value = new Quaternion(-value.x, -value.y, -value.z, -value.w);
        float along = Vector3.Dot(new Vector3(value.x, value.y, value.z), axis.normalized);
        return Mathf.DeltaAngle(0, 2 * Mathf.Atan2(along, value.w) * Mathf.Rad2Deg);
    }
    private static void Track(float value, ref float minimum, ref float maximum, ref int sign, ref int changes)
    {
        minimum = Mathf.Min(minimum, value); maximum = Mathf.Max(maximum, value);
        int next = value > .5f ? 1 : value < -.5f ? -1 : 0;
        if (next != 0) { if (sign != 0 && next != sign) changes++; sign = next; }
    }
    private static void Curve(float minimum, float maximum, int changes, float amplitude, int expected, string label, float tolerance = 1.5f)
    {
        Check(Mathf.Abs(minimum + amplitude) <= tolerance && Mathf.Abs(maximum - amplitude) <= tolerance, label + " did not reach the requested positive/negative local amplitude: " + minimum + "," + maximum);
        Check(changes == expected, label + " did not make the requested number of cycles: sign changes=" + changes + " expected=" + expected);
    }

    private static void Capture(Fixture fixture)
    {
        string directory = Path.Combine(Output, fixture.Report.id + "-" + fixture.Current.name); Directory.CreateDirectory(directory);
        int frame = fixture.Current.renderedFrames;
        string file = "frame-" + frame.ToString("D4");
        foreach (var child in fixture.Root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 31;
        float scale = fixture.Report.scale;
        Vector3 hips = fixture.Bone(HumanBodyBones.Hips).position, head = fixture.Bone(HumanBodyBones.Head).position;
        Vector3 center = Vector3.Lerp(hips, head, .55f) + fixture.Root.transform.forward * .10f * scale;
        float bodyHeight = Vector3.Distance(hips, head);
        float armReach = Vector3.Distance(fixture.Bone(HumanBodyBones.LeftUpperArm).position, fixture.Bone(HumanBodyBones.LeftLowerArm).position) + Vector3.Distance(fixture.Bone(HumanBodyBones.LeftLowerArm).position, fixture.Bone(HumanBodyBones.LeftHand).position);
        camera.orthographicSize = Mathf.Max(bodyHeight * .80f, armReach * 1.15f);
        camera.transform.position = center + fixture.Root.transform.rotation * new Vector3(1.5f, .3f, 3.6f) * scale;
        camera.transform.LookAt(center);
        var baked = new List<(SkinnedMeshRenderer original, GameObject view, Mesh mesh)>();
        var texture = new RenderTexture(768, 768, 24, RenderTextureFormat.ARGB32);
        var image = new Texture2D(768, 768, TextureFormat.RGB24, false); var previous = RenderTexture.active;
        try
        {
            foreach (var renderer in fixture.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.sharedMesh == null) continue;
                var mesh = new Mesh(); renderer.BakeMesh(mesh);
                var view = new GameObject("Constraint actual baked mesh"); view.layer = 31; view.transform.SetParent(renderer.transform, false);
                view.AddComponent<MeshFilter>().sharedMesh = mesh; view.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
                baked.Add((renderer, view, mesh)); renderer.enabled = false;
            }
            camera.targetTexture = texture; camera.Render(); RenderTexture.active = texture;
            image.ReadPixels(new Rect(0, 0, 768, 768), 0, 0); image.Apply(false, false);
            byte[] bytes = image.EncodeToPNG(); File.WriteAllBytes(Path.Combine(directory, file + ".png"), bytes);
            using (var sha = SHA256.Create()) fixture.Report.pngSha256.Add(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant());
            File.WriteAllText(Path.Combine(directory, file + ".json"), JsonUtility.ToJson(fixture.LatestSample, true));
            fixture.Report.renderedFrames++;
            fixture.Current.renderedFrames++;
        }
        finally
        {
            camera.targetTexture = null; RenderTexture.active = previous;
            foreach (var item in baked) { item.original.enabled = true; Object.DestroyImmediate(item.view); Object.DestroyImmediate(item.mesh); }
            Object.DestroyImmediate(image); Object.DestroyImmediate(texture);
            foreach (var child in fixture.Root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 30;
        }
    }

    private static void Check(bool condition, string message) { if (report != null) report.checks++; if (!condition) throw new InvalidOperationException(message); }
    private static string Hash(string relative) { string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", relative)); if (!File.Exists(path)) return "missing"; using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static void SaveReport() { if (report != null) { report.wallSeconds = Time.realtimeSinceStartup - started; File.WriteAllText(Path.Combine(Output, "constraint-regression.json"), JsonUtility.ToJson(report, true)); if (SessionState.GetBool(Key + "TempoOnly", false)) SaveTempoReport(); } }
    private static void Finish(Exception error)
    {
        if (SessionState.GetString(Key + "Stage", "") == "exiting") return;
        injectedFailure = false;
        if (report == null) report = new Report();
        report.status = error == null ? "passed" : "failed: " + error.Message;
        if (error != null) Debug.LogException(error);
        if (fixtures != null) foreach (var f in fixtures) f.CleanupExpression();
        Directory.CreateDirectory(Output); SaveReport();
        SessionState.SetInt(Key + "ExitCode", error == null ? 0 : 1); SessionState.SetString(Key + "Stage", "exiting");
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else EditorApplication.delayCall += FinalizeBatch;
    }
    private static void FinalizeBatch()
    {
        if (!SessionState.GetBool(Key + "Active", false)) return;
        SessionState.SetBool(Key + "Active", false); int code = SessionState.GetInt(Key + "ExitCode", 1);
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var saved = JsonUtility.FromJson<SavedScenes>(SessionState.GetString(Key + "Scenes", "{}"));
            if (saved?.scenes != null && saved.scenes.Length > 0 && saved.scenes.All(s => !string.IsNullOrEmpty(s.path)))
                EditorSceneManager.RestoreSceneManagerSetup(saved.scenes.Select(s => new SceneSetup { path = s.path, isLoaded = s.isLoaded, isActive = s.isActive }).ToArray());
        }
        catch (Exception error) { Debug.LogException(error); code = 1; }
        Debug.Log("[ArdyConstraintRegression] Exit " + code + "; " + Output); EditorApplication.Exit(code);
    }
}

// Test-only late overwrite simulates a real post-player ownership conflict.
[DefaultExecutionOrder(11900)]
public sealed class ArdyConstraintRegressionFault : MonoBehaviour
{
    public Action callback;
    private void LateUpdate() => callback?.Invoke();
}

// Read actual bones after both the real VRM runtime and the production observer.
[DefaultExecutionOrder(12500)]
public sealed class ArdyConstraintRegressionSample : MonoBehaviour
{
    public Action callback;
    private void LateUpdate() => callback?.Invoke();
}
