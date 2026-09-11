using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Motion;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// Optional palm acceptance shares the same real VRM/Animator fixtures and late observer.
public static partial class ArdyConstraintRegression
{
    [Serializable] private sealed class PalmSample
    {
        public Vector3 leftNormal, rightNormal, leftFingerForward, rightFingerForward;
        public Vector3 leftExpectedNormal, rightExpectedNormal, partnerSnapshot;
        public float leftFacingError, rightFacingError, leftTrackingError, rightTrackingError;
        public float leftWristSwing, rightWristSwing, leftWristTwist, rightWristTwist;
        public float leftForearmRoll, rightForearmRoll, leftFingerForearmAngle, rightFingerForearmAngle;
    }

    [Serializable] private sealed class PalmCase
    {
        public string independentMeasurement = "Actual Hand/IndexProximal/LittleProximal triangle normal, with left/right anatomical chirality; four proximal centroid for finger-forward. No solver desired normal is read.";
        public string sourceBinding, rejection, outcome, rejectionContext;
        public string wristMeasurement = "Actual hand-relative-forearm rotation against imported rest, swing/twist split about actual elbow-to-wrist axis. Forearm roll against rest-aligned actual elbow-to-wrist direction.";
        public bool expectedFailure, targetMovedAfterStart;
        public bool rejectionContextMatchedPlan, bindClearedRejectionContext;
        public Vector3 targetSnapshot;
        public float maximumFacingError, maximumTrackingError, maximumWristSwing, maximumAbsForearmRoll;
        public float maximumCurrentPointDrift, maximumCurrentKeepRotationDrift;
        public float firstActingAt = -1;
        public float leftResolvedBend = -1, rightResolvedBend = -1;
        public int measuredFrames;
    }

    [Serializable] private sealed class PublicPalmFixture
    {
        public string caseId, publicUserPrompt, rawMotionTag;
        public ArdyControlPlan plan;
    }
    // Inspect the wire JSON directly: JsonUtility can synthesize default nested
    // serializable objects for null references, which would invent control facts.
    private static bool JsonNull(JObject context, string field) => context[field] != null && context[field].Type == JTokenType.Null;
    private static bool ContextPlanMatches(JObject context, ArdyControlPlan plan) =>
        context["lastControlPlan"] is JObject && JToken.DeepEquals(context["lastControlPlan"], JObject.Parse(JsonUtility.ToJson(plan)));
    private static void CheckClearedPalmContext(JObject context)
    {
        Check(JsonNull(context, "controlPlan") && JsonNull(context, "measuredConstraintState")
            && JsonNull(context, "lastControlPlan") && JsonNull(context, "lastControlObservation")
            && string.IsNullOrEmpty((string)context["lastControlError"]), "Rebind retained or invented control plan/observation/error facts");
    }

    private sealed partial class Fixture
    {
        public Transform Partner;
        public Vector3 PartnerSnapshot;
        public bool HasPartnerPoint;
        public string PartnerSource = "character-forward-fallback";
        public bool ExpectPalmFailure;
        public Vector3 LeftHeldPalmInChest, RightHeldPalmInChest;
        public bool CapturedHeldPalm;
        public Vector3[] InitialPalmPoints;
        public Quaternion[] InitialPalmRotations;
        private ArdyAvatarRestPose palmRest;
        private bool ExpectInjectedPalmMismatch => SessionState.GetBool(Key + "PalmOnly", false) && injectedFailure && Report.id == "fixture-0";

        public void SetPartner(Vector3 rootOffset)
        {
            if (Partner == null) Partner = new GameObject("Actual interaction target " + Report.id).transform;
            Partner.position = Root.transform.TransformPoint(rootOffset);
            SetPlayerTarget(Partner);
        }

        public void SetPlayerTarget(Transform target)
        {
            var method = typeof(ArdyMotionPlayer).GetMethod("SetInteractionTarget", BindingFlags.Instance | BindingFlags.Public);
            Check(method != null, "Public Player.SetInteractionTarget missing");
            method.Invoke(Player, new object[] { target });
            // The actual controller/bridge binding is exercised for every positive palm request.
            if (Live != null)
            {
                var field = typeof(ArdyLiveMotionController).GetField("interactionTarget");
                if (field != null) field.SetValue(Live, target);
            }
            if (Bridge != null)
            {
                var field = typeof(ArdyDialogueMotionBridge).GetField("interactionTarget");
                if (field != null) field.SetValue(Bridge, target);
            }
        }

        private void SetUpPalmCase()
        {
            if (!SessionState.GetBool(Key + "PalmOnly", false)) return;
            CapturedHeldPalm = false;
            var active = Partner;
            HasPartnerPoint = active != null || Camera.main != null;
            PartnerSnapshot = active != null ? active.position : Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            PartnerSource = active != null ? "bound-interaction-target" : Camera.main != null ? "main-camera" : "character-forward-fallback";
            Current.palm = new PalmCase { targetSnapshot = PartnerSnapshot, sourceBinding = PartnerSource, expectedFailure = ExpectPalmFailure };
            InitialPalmPoints = PalmPoints();
            InitialPalmRotations = Arms.Where(b => b != HumanBodyBones.LeftShoulder && b != HumanBodyBones.RightShoulder)
                .Select(b => Quaternion.Inverse(Chest.rotation) * Bone(b).rotation).ToArray();
            if (palmRest == null) palmRest = ArdyAvatarRestPose.Find(Vrm);
            Check(palmRest != null, "Palm independent rest rotation reference missing");
        }

        private Vector3[] PalmPoints() => new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand }
            .Select(b => Chest.InverseTransformPoint(Bone(b).position)).ToArray();

        private Vector3 PalmNormal(bool left)
        {
            var wrist = Bone(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            var index = Bone(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            var little = Bone(left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);
            Check(wrist != null && index != null && little != null, "Independent palm observation requires actual finger mappings");
            var normal = Vector3.Cross(index.position - wrist.position, little.position - wrist.position);
            Check(normal.sqrMagnitude > 1e-10f, "Independent palm observation has a degenerate triangle");
            return (left ? normal : -normal).normalized;
        }

        private Vector3 FingerForward(bool left)
        {
            var ids = left ? new[] { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftLittleProximal }
                : new[] { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightRingProximal, HumanBodyBones.RightLittleProximal };
            return (ids.Select(b => Bone(b).position).Aggregate(Vector3.zero, (a, b) => a + b) / 4
                - Bone(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand).position).normalized;
        }

        private Vector3 PalmGoal(bool left)
        {
            string goal = left ? Plan.leftPalm : Plan.rightPalm;
            var root = Root.transform;
            float side = Mathf.Sign(InRoot(Bone(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm)).x
                - InRoot(Bone(left ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm)).x);
            switch (goal)
            {
                case "partner": return HasPartnerPoint ? (PartnerSnapshot - Bone(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand).position).normalized : root.forward;
                case "up": return root.up;
                case "down": return -root.up;
                case "inward": return root.right * -side;
                case "outward": return root.right * side;
                default: return PalmNormal(left);
            }
        }

        private void WristGeometry(bool left, out float swing, out float twist, out float roll)
        {
            var lowerId = left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm;
            var handId = left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
            var lower = Bone(lowerId); var hand = Bone(handId);
            var axisWorld = (hand.position - lower.position).normalized;
            var axisLocal = lower.InverseTransformDirection(axisWorld).normalized;
            var restRelative = Quaternion.Inverse(palmRest.Get(lowerId)) * palmRest.Get(handId);
            var delta = Quaternion.Inverse(lower.rotation) * hand.rotation * Quaternion.Inverse(restRelative);
            var projected = Vector3.Project(new Vector3(delta.x, delta.y, delta.z), axisLocal);
            var twistRotation = new Quaternion(projected.x, projected.y, projected.z, delta.w);
            float norm = Mathf.Sqrt(projected.sqrMagnitude + delta.w * delta.w);
            twistRotation = norm < 1e-7f ? Quaternion.identity : new Quaternion(twistRotation.x / norm, twistRotation.y / norm, twistRotation.z / norm, twistRotation.w / norm);
            swing = Quaternion.Angle(Quaternion.identity, delta * Quaternion.Inverse(twistRotation));
            twist = SignedAngle(twistRotation, axisLocal);
            var reference = Root.transform.rotation * palmRest.Get(lowerId);
            var baseRotation = Quaternion.FromToRotation(reference * axisLocal, axisWorld) * reference;
            roll = SignedAngle(lower.rotation * Quaternion.Inverse(baseRotation), axisWorld);
        }

        private void ObservePalm(Sample sample)
        {
            if (Current.palm == null) return;
            var p = new PalmSample { partnerSnapshot = PartnerSnapshot, leftNormal = PalmNormal(true), rightNormal = PalmNormal(false),
                leftFingerForward = FingerForward(true), rightFingerForward = FingerForward(false) };
            p.leftExpectedNormal = PalmGoal(true); p.rightExpectedNormal = PalmGoal(false);
            WristGeometry(true, out p.leftWristSwing, out p.leftWristTwist, out p.leftForearmRoll);
            WristGeometry(false, out p.rightWristSwing, out p.rightWristTwist, out p.rightForearmRoll);
            p.leftFingerForearmAngle = Vector3.Angle(p.leftFingerForward, Bone(HumanBodyBones.LeftHand).position - Bone(HumanBodyBones.LeftLowerArm).position);
            p.rightFingerForearmAngle = Vector3.Angle(p.rightFingerForward, Bone(HumanBodyBones.RightHand).position - Bone(HumanBodyBones.RightLowerArm).position);
            p.leftFacingError = Vector3.Angle(p.leftNormal, p.leftExpectedNormal); p.rightFacingError = Vector3.Angle(p.rightNormal, p.rightExpectedNormal);
            float angle = 0;
            if (sample.phase == "acting")
            {
                if (Current.palm.firstActingAt < 0) Current.palm.firstActingAt = Time.time;
                float progress = Mathf.Clamp01((Time.time - Current.palm.firstActingAt) / Plan.seconds);
                angle = Plan.amplitude * Mathf.SmoothStep(0, 1, Mathf.Clamp01(progress / .15f))
                    * Mathf.SmoothStep(0, 1, Mathf.Clamp01((1 - progress) / .15f)) * Mathf.Sin(2 * Mathf.PI * Plan.cycles * progress);
            }
            Vector3 fixedAxis = Plan.axis == "right" ? Root.transform.right : Plan.axis == "forward" ? Root.transform.forward : Root.transform.up;
            if (Plan.axis == "palm-normal")
            {
                sample.leftWristAngle = SignedAngle(RotationInRoot(Bone(HumanBodyBones.LeftHand)) * Quaternion.Inverse(LeftNeutral), Root.transform.InverseTransformDirection(p.leftExpectedNormal));
                sample.rightWristAngle = SignedAngle(RotationInRoot(Bone(HumanBodyBones.RightHand)) * Quaternion.Inverse(RightNeutral), Root.transform.InverseTransformDirection(p.rightExpectedNormal));
            }
            else
            {
                if (Plan.joint == "wrists" || Plan.joint == "left-wrist") p.leftExpectedNormal = Quaternion.AngleAxis(angle, fixedAxis) * p.leftExpectedNormal;
                if (Plan.joint == "wrists" || Plan.joint == "right-wrist") p.rightExpectedNormal = Quaternion.AngleAxis(angle, fixedAxis) * p.rightExpectedNormal;
            }
            bool firstHolding = sample.holding && !CapturedHeldPalm;
            float firstHoldingLeftError = Vector3.Angle(p.leftNormal, p.leftExpectedNormal);
            float firstHoldingRightError = Vector3.Angle(p.rightNormal, p.rightExpectedNormal);
            if (sample.holding)
            {
                if (!CapturedHeldPalm)
                {
                    LeftHeldPalmInChest = Quaternion.Inverse(Chest.rotation) * p.leftNormal;
                    RightHeldPalmInChest = Quaternion.Inverse(Chest.rotation) * p.rightNormal; CapturedHeldPalm = true;
                }
                p.leftExpectedNormal = Chest.rotation * LeftHeldPalmInChest; p.rightExpectedNormal = Chest.rotation * RightHeldPalmInChest;
            }
            p.leftTrackingError = firstHolding ? firstHoldingLeftError : Vector3.Angle(p.leftNormal, p.leftExpectedNormal);
            p.rightTrackingError = firstHolding ? firstHoldingRightError : Vector3.Angle(p.rightNormal, p.rightExpectedNormal);
            sample.palms = p;
            // This negative fixture deliberately overwrites actual arms at11900. Preserve
            // measurements, then require the production observer/controller to expose failure.
            if (ExpectInjectedPalmMismatch) return;
            var observation = Player.ConstraintObservation;
            if (observation != null && Plan.leftBendAuto)
            {
                if (Current.palm.leftResolvedBend < 0) Current.palm.leftResolvedBend = observation.leftResolvedBend;
                Check(Mathf.Abs(Current.palm.leftResolvedBend - observation.leftResolvedBend) < .001f, "Automatic left bend changed after plan initialization");
                Check(observation.leftResolvedBend >= 0 && observation.leftResolvedBend <= 110, "Automatic left bend left allowed range");
            }
            if (observation != null && Plan.rightBendAuto)
            {
                if (Current.palm.rightResolvedBend < 0) Current.palm.rightResolvedBend = observation.rightResolvedBend;
                Check(Mathf.Abs(Current.palm.rightResolvedBend - observation.rightResolvedBend) < .001f, "Automatic right bend changed after plan initialization");
                Check(observation.rightResolvedBend >= 0 && observation.rightResolvedBend <= 110, "Automatic right bend left allowed range");
            }
            if (sample.phase != "acting" && !sample.holding) return;
            Current.palm.measuredFrames++;
            foreach (bool left in new[] { true, false })
            {
                string arm = left ? Plan.left : Plan.right, palm = left ? Plan.leftPalm : Plan.rightPalm;
                if (palm != "keep")
                {
                    float tracking = left ? p.leftTrackingError : p.rightTrackingError, facing = left ? p.leftFacingError : p.rightFacingError;
                    float swing = left ? p.leftWristSwing : p.rightWristSwing, roll = left ? p.leftForearmRoll : p.rightForearmRoll;
                    Current.palm.maximumTrackingError = Mathf.Max(Current.palm.maximumTrackingError, tracking);
                    Current.palm.maximumFacingError = Mathf.Max(Current.palm.maximumFacingError, facing);
                    Current.palm.maximumWristSwing = Mathf.Max(Current.palm.maximumWristSwing, swing);
                    Current.palm.maximumAbsForearmRoll = Mathf.Max(Current.palm.maximumAbsForearmRoll, Mathf.Abs(roll));
                    Check(tracking <= 5, "Actual triangle palm does not face independently measured target: " + Report.id + " " + tracking);
                    Check(swing <= 80.05f, "Actual wrist exceeded 80-degree engineering swing bound: " + Report.id + " " + swing);
                }
                if (arm == "current")
                {
                    var points = PalmPoints(); int start = left ? 0 : 3;
                    for (int j = start; j < start + 3; j++)
                    {
                        float drift = Vector3.Distance(points[j], InitialPalmPoints[j]);
                        Current.palm.maximumCurrentPointDrift = Mathf.Max(Current.palm.maximumCurrentPointDrift, drift);
                        Check(drift < .005f, "Explicit palm changed current arm positions");
                    }
                    if (palm == "keep" && Plan.joint == "none")
                    {
                        var ids = left ? new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand } : new[] { HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand };
                        for (int j = 0; j < 3; j++)
                        {
                            float drift = Quaternion.Angle(Quaternion.Inverse(Chest.rotation) * Bone(ids[j]).rotation, InitialPalmRotations[start + j]);
                            Current.palm.maximumCurrentKeepRotationDrift = Mathf.Max(Current.palm.maximumCurrentKeepRotationDrift, drift);
                            Check(drift < .25f, "current/keep failed to preserve actual captured arm and hand rotations");
                        }
                    }
                }
            }
        }
    }

    public static void RunPalmBatch() => BeginBatch(false, true);
    public static void RunPalmPrimaryBatch() => BeginBatch(false, true, true);
    private static bool palmTargetMoved;
    private static int palmStaticCase;
    private static Camera palmFallbackCamera;
    private static PublicPalmFixture publicPalmFixture;

    private static ArdyControlPlan PalmPlan(string leftPalm = "partner", string rightPalm = "partner", bool curve = true, float bend = 40) => new ArdyControlPlan {
        left = "forward", right = "forward", leftBend = bend, rightBend = bend, leftPalm = leftPalm, rightPalm = rightPalm,
        joint = curve ? "wrists" : "none", axis = curve ? "palm-normal" : "up", amplitude = 8, cycles = curve ? 2 : 1, seconds = curve ? 3.2f : 1, end = "hold" };

    private static void SamplePalmStages(float elapsed)
    {
        if (SessionState.GetBool(Key + "PalmPrimaryOnly", false)) { SamplePalmPrimary(elapsed); return; }
        switch (stage)
        {
            case 0:
                if (elapsed < .5f) return;
                CreatePalmMainCamera(new Vector3(100, 100, -100));
                foreach (var f in fixtures)
                {
                    f.EnsureBridge();
                    float shoulderY = f.InRoot(f.Bone(HumanBodyBones.LeftUpperArm)).y;
                    f.SetPartner(new Vector3(f.Report.id == "fixture-1" ? -.45f : .45f, shoulderY + .25f, 2));
                }
                BeginAll("palm-partner-greeting-bend40", PalmPlan(), true); Advance(); break;
            case 1:
                CapturePairIfDue(); CheckNoFailure();
                if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 7, "Partner greeting never held"); return; }
                foreach (var f in fixtures) f.EndCase(true);
                var current = PalmPlan("keep", "keep", false); current.left = current.right = "current";
                BeginAll("current-keep-preserves-actual-palm", current, true); Advance(); break;
            case 2:
                CheckNoFailure();
                if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 3, "current/keep never held"); return; }
                foreach (var f in fixtures) { f.EndCase(false); f.Live.Cancel("explicit-stop"); }
                Advance(); break;
            case 3:
                if (elapsed < .7f) return; CheckReturned();
                foreach (var f in fixtures)
                {
                    float shoulderY = f.InRoot(f.Bone(HumanBodyBones.LeftUpperArm)).y;
                    f.SetPartner(new Vector3(f.Report.id == "fixture-1" ? .7f : -.7f, shoulderY + .6f, 2));
                }
                palmTargetMoved = false; BeginAll("partner-new-plan-resamples-raised-opposite-target", PalmPlan(curve:false), true); Advance(); break;
            case 4:
                CheckNoFailure();
                if (elapsed > .2f && !palmTargetMoved)
                {
                    foreach (var f in fixtures) { f.Partner.position += f.Root.transform.right * 1.5f; f.Current.palm.targetMovedAfterStart = true; }
                    palmTargetMoved = true;
                }
                if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 3, "Snapshot-target palm never held"); return; }
                foreach (var f in fixtures) f.EndCase(false);
                var palmCurrent = PalmPlan("up", "down", false); palmCurrent.left = palmCurrent.right = "current";
                BeginAll("current-up-down-reorients-palm-without-moving-arm", palmCurrent, true); Advance(); break;
            case 5:
                CapturePairIfDue(); CheckNoFailure();
                if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 3, "current up/down never held"); return; }
                foreach (var f in fixtures)
                {
                    Check(f.Current.palm.maximumAbsForearmRoll > 90, "up/down rotation was not shared with forearm axial roll");
                    f.EndCase(false); f.Live.Cancel("explicit-stop");
                }
                Advance(); break;
            case 6:
                if (elapsed < .7f) return; CheckReturned();
                palmStaticCase = 0; BeginAll("palm-inward-outward", PalmPlan("inward", "outward", false), true); Advance(); break;
            case 7:
                CheckNoFailure();
                if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 3, "Static lateral palm never held"); return; }
                foreach (var f in fixtures) { f.EndCase(false); f.Live.Cancel("explicit-stop"); } Advance(); break;
            case 8:
                if (elapsed < .7f) return; CheckReturned();
                BeginAll("default-keep-forward-wrist-compatibility", new ArdyControlPlan { left = "forward", right = "forward", joint = "wrists", axis = "up", amplitude = 10, cycles = 2, seconds = 3.2f, end = "hold" }, true); Advance(); break;
            case 9:
                CheckNoFailure();
                if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 7, "Default keep compatibility never held"); return; }
                foreach (var f in fixtures) { f.EndCase(true); f.Live.Cancel("explicit-stop"); } Advance(); break;
            case 10:
                if (elapsed < .7f) return; CheckReturned();
                palmStaticCase = 0;
                foreach (var f in fixtures)
                {
                    f.SetPartner(new Vector3(0, f.InRoot(f.Bone(HumanBodyBones.LeftUpperArm)).y, 3));
                    var oldPolicy = ReadPublicPalmPlan(); oldPolicy.leftBendAuto = oldPolicy.rightBendAuto = false;
                    oldPolicy.leftBend = oldPolicy.rightBend = 8;
                    f.ExpectPalmFailure = false;
                    f.Begin("old-fixed8-policy-original-tag-horizon-target", oldPolicy, viaController:true);
                }
                Advance(); break;
            case 11:
                if (palmStaticCase == 0)
                {
                    // The archived first run measured79.45..79.75deg maximum swing here.
                    // This is a reachable low-margin boundary, not an automatic rejection.
                    CheckNoFailure();
                    if (fixtures.Any(f => f.Current.actingFrames < 20 || (f.Player.IsConstrained && !f.Player.IsHoldingPose)))
                    { Check(elapsed < 9, "Fixed8 boundary did not finish its actual curve"); return; }
                    foreach (var f in fixtures)
                    {
                        Check(!f.Plan.leftBendAuto && !f.Plan.rightBendAuto && f.Current.maximumBendError < 4, "Fixed8 request was silently replaced by automatic elbow geometry");
                        f.Current.palm.outcome = "reachable-fixed8-low-margin-boundary";
                        f.EndCase(true); f.Live.Cancel("fixed8-boundary-finished");
                        BeginPalmExpectedFailure(f, "explicit-zero-bend-remains-fixed-and-rejected", PalmPlan(curve:false, bend:0));
                    }
                    palmStaticCase = 1; stageStarted = Time.time; SaveReport(); return;
                }
                foreach (var f in fixtures) Check(!f.Current.enteredActing && !f.Current.enteredHolding, "An infeasible straight-arm palm was reported reached");
                if (elapsed < .35f || (elapsed < 3.4f && fixtures.Any(f => string.IsNullOrEmpty(f.Current.palm.rejection)))) return;
                foreach (var f in fixtures)
                {
                    string error = f.Current.palm.rejection ?? f.Player.ConstraintFailure;
                    Check(!string.IsNullOrEmpty(error) && error.Contains("palm-unreachable"), "Infeasible palm did not expose its geometric error");
                    f.Current.palm.rejection = error; f.Current.palm.outcome = "expected-unreachable-rejection";
                    f.Current.palm.rejectionContext = f.Live.DescribeMotionContext();
                    var context = JObject.Parse(f.Current.palm.rejectionContext);
                    Check(ContextPlanMatches(context, f.Plan) && !string.IsNullOrEmpty((string)context["lastControlError"])
                        && !(bool)context["previousRequestedPoseHeld"] && JsonNull(context, "controlPlan")
                        && JsonNull(context, "lastControlObservation"),
                        "Controller did not retain exact rejected plan/error as failure facts");
                    f.Current.palm.rejectionContextMatchedPlan = true;
                    f.Player.Stop(); f.EndCase(false); f.ExpectPalmFailure = false;
                }
                Advance(); break;
            case 12:
                if (elapsed < .7f) return; CheckReturned();
                foreach (var f in fixtures)
                {
                    f.Live.Bind(f.Vrm);
                    CheckClearedPalmContext(JObject.Parse(f.Live.DescribeMotionContext()));
                    f.Current.palm.bindClearedRejectionContext = true;
                }
                CheckPalmUnsupportedMappings();
                foreach (var f in fixtures) { f.Partner = null; f.SetPlayerTarget(null); }
                DestroyPalmMainCamera();
                Check(Camera.main == null, "Fallback fixture unexpectedly has a MainCamera");
                BeginAll("no-target-no-camera-explicit-character-forward-fallback", PalmPlan(curve:false), true); Advance(); break;
            case 13:
                CheckNoFailure();
                if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 3, "Character-forward fallback never held"); return; }
                foreach (var f in fixtures)
                {
                    string observed = JsonUtility.ToJson(f.Player.ConstraintObservation);
                    Check(observed.IndexOf("fallback", StringComparison.OrdinalIgnoreCase) >= 0, "Fallback was presented as an actual partner");
                    f.EndCase(false); f.Live.Cancel("explicit-stop");
                }
                Advance(); break;
            case 14:
                if (elapsed < .7f) return; CheckReturned();
                var cameraFixture = fixtures[0];
                CreatePalmMainCamera(cameraFixture.Root.transform.TransformPoint(new Vector3(0, cameraFixture.InRoot(cameraFixture.Bone(HumanBodyBones.Head)).y, 2)));
                cameraFixture.Begin("main-camera-fallback-actual-position", PalmPlan(curve:false), true);
                stage = 141; stageStarted = Time.time; SaveReport(); break;
            case 141:
                var cam = fixtures[0];
                Check(string.IsNullOrEmpty(cam.Player.ConstraintFailure), "Actual Camera fallback failed");
                if (!cam.Player.IsHoldingPose) { Check(elapsed < 3, "Camera fallback did not hold"); return; }
                Check(cam.Player.ConstraintObservation.partnerSource.IndexOf("camera", StringComparison.OrdinalIgnoreCase) >= 0,
                    "Camera fallback did not record its actual target source");
                Check(Vector3.Distance(cam.Current.palm.targetSnapshot, palmFallbackCamera.transform.position) < .0001f, "Camera fallback did not bind actual Camera position");
                cam.EndCase(false); cam.Live.Cancel("explicit-stop"); stage = 142; stageStarted = Time.time; SaveReport(); break;
            case 142:
                if (elapsed < .7f) return; CheckReturned(); DestroyPalmMainCamera();
                StartPublicPalmFixture(); stage = 15; stageStarted = Time.time; SaveReport(); break;
            case 15:
                CapturePairIfDue(); CheckNoFailure();
                foreach (var f in fixtures) Check(string.IsNullOrEmpty(f.Current.palm.rejection), "Positive public Qwen auto plan was rejected");
                if (elapsed < 9 && fixtures.Any(f => f.Player.IsConstrained && string.IsNullOrEmpty(f.Player.ConstraintFailure) && !f.Player.IsHoldingPose)) return;
                foreach (var f in fixtures)
                {
                    Check(f.Current.actingFrames >= 20 && (!f.Player.IsConstrained || f.Player.IsHoldingPose), "Positive public Qwen plan did not complete");
                    f.Current.palm.outcome = "actual-target-and-local-curve-observed";
                    f.EndCase(true); f.Live.Cancel("public-probe-finished");
                }
                Advance(); break;
            case 16:
                if (elapsed < .7f) return; CheckReturned();
                fixtures[0].Begin("runtime-palm-goal-loss-retains-controller-feedback", PalmPlan(curve:false), true); Advance(); break;
            case 17:
                Check(string.IsNullOrEmpty(fixtures[0].Player.ConstraintFailure), "Runtime failure fixture did not first reach its goal");
                if (!fixtures[0].Player.IsHoldingPose) { Check(elapsed < 3, "Runtime failure fixture never held"); return; }
                fixtures[0].Current.palm.expectedFailure = true; injectedFailure = true; Advance(); break;
            case 18:
                if (elapsed < .85f) return;
                var failed = fixtures[0];
                failed.Current.palm.rejectionContext = failed.Live.DescribeMotionContext();
                var failureContext = JObject.Parse(failed.Current.palm.rejectionContext);
                Check(!failed.Player.IsHoldingPose && JsonNull(failureContext, "controlPlan") && !(bool)failureContext["previousRequestedPoseHeld"]
                    && ContextPlanMatches(failureContext, failed.Plan) && !string.IsNullOrEmpty((string)failureContext["lastControlError"])
                    && failureContext["lastControlObservation"] is JObject,
                    "Actual late pose conflict did not become retained Controller failure facts");
                failed.Current.palm.rejection = (string)failureContext["lastControlError"]; failed.Current.palm.outcome = "expected-runtime-goal-loss";
                failed.Current.palm.rejectionContextMatchedPlan = true;
                injectedFailure = false; Advance(); break;
            case 19:
                if (elapsed < .7f) return; CheckReturned();
                fixtures[0].EndCase(false); fixtures[0].Live.Bind(fixtures[0].Vrm);
                CheckClearedPalmContext(JObject.Parse(fixtures[0].Live.DescribeMotionContext()));
                fixtures[0].Current.palm.bindClearedRejectionContext = true;
                Finish(null); break;
        }
    }

    private static void SamplePalmPrimary(float elapsed)
    {
        switch (stage)
        {
            case 0:
                if (elapsed < .5f) return;
                foreach (var f in fixtures) f.EnsureBridge();
                StartPublicPalmFixture(); Advance(); break;
            case 1:
                CapturePairIfDue(); CheckNoFailure();
                foreach (var f in fixtures) Check(string.IsNullOrEmpty(f.Current.palm.rejection), "Fixed public Qwen plan was rejected: " + f.Current.palm.rejection);
                if (fixtures.Any(f => f.Current.actingFrames < 20 || (f.Player.IsConstrained && !f.Player.IsHoldingPose)))
                { Check(elapsed < 9, "Fixed public Qwen plan did not complete its actual local curve"); return; }
                foreach (var f in fixtures)
                {
                    f.EndCase(true); f.Current.palm.outcome = "actual-target-and-local-curve-observed";
                    f.Live.Cancel("primary-finished");
                }
                Advance(); break;
            case 2:
                if (elapsed < .7f) { CapturePairIfDue(); return; }
                CheckReturned(); Finish(null); break;
        }
    }

    private static void BeginPalmExpectedFailure(Fixture f, string name, ArdyControlPlan plan)
    {
        f.ExpectPalmFailure = true;
        try { f.Begin(name, plan, viaController:true); }
        catch (InvalidOperationException error) { f.Current.palm.rejection = error.Message; }
    }

    private static void CheckPalmUnsupportedMappings()
    {
        foreach (var f in fixtures)
        {
            var humanoid = f.Vrm.Humanoid;
            var field = humanoid.GetType().GetField("m_LeftIndexProximal", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(field != null, "Actual humanoid finger map field missing for unsupported fixture");
            object original = field.GetValue(humanoid);
            try
            {
                field.SetValue(humanoid, null);
                bool rejected = false;
                try { f.Player.PlayConstrained(PalmPlan(curve:false)); }
                catch (InvalidOperationException error) { rejected = error.Message.Contains("palm-basis-unsupported"); }
                Check(rejected, "Missing actual finger mapping silently guessed a palm normal");
                var keep = PalmPlan("keep", "keep", false);
                f.Player.PlayConstrained(keep);
                Check(f.Player.IsConstrained, "keep unexpectedly required optional finger mappings");
                f.Player.Stop();
            }
            finally { field.SetValue(humanoid, original); }
            Vector3 scale = f.Root.transform.localScale;
            try
            {
                f.Root.transform.localScale = new Vector3(scale.x, scale.y * 1.1f, scale.z);
                bool rejected = false;
                try { f.Player.PlayConstrained(PalmPlan(curve:false)); }
                catch (InvalidOperationException error) { rejected = error.Message.Contains("palm-basis-unsupported"); }
                Check(rejected, "Nonuniform scale silently guessed a palm basis");
                f.Root.transform.localScale = new Vector3(-scale.x, scale.y, scale.z);
                rejected = false;
                try { f.Player.PlayConstrained(PalmPlan(curve:false)); }
                catch (InvalidOperationException error) { rejected = error.Message.Contains("palm-basis-unsupported"); }
                Check(rejected, "Negative scale silently guessed a palm basis");
            }
            finally { f.Root.transform.localScale = scale; }
        }
    }

    private static void CreatePalmMainCamera(Vector3 position)
    {
        DestroyPalmMainCamera();
        palmFallbackCamera = new GameObject("Independent fallback MainCamera").AddComponent<Camera>();
        palmFallbackCamera.gameObject.tag = "MainCamera"; palmFallbackCamera.transform.position = position;
        palmFallbackCamera.cullingMask = 0; palmFallbackCamera.targetTexture = new RenderTexture(1, 1, 0);
        Check(Camera.main == palmFallbackCamera, "Actual Camera.main did not resolve fallback fixture");
    }

    private static void DestroyPalmMainCamera()
    {
        if (palmFallbackCamera == null) return;
        var texture = palmFallbackCamera.targetTexture; palmFallbackCamera.targetTexture = null;
        UnityEngine.Object.DestroyImmediate(palmFallbackCamera.gameObject);
        if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
        palmFallbackCamera = null;
    }

    private static ArdyControlPlan ReadPublicPalmPlan()
    {
        string path = Environment.GetEnvironmentVariable("ARDY_PUBLIC_PALM_FIXTURE");
        Check(!string.IsNullOrEmpty(path) && File.Exists(path), "A fixed real Qwen public intent fixture is required; do not replace it with a hand-written plan");
        publicPalmFixture = JsonUtility.FromJson<PublicPalmFixture>(File.ReadAllText(path));
        Check(publicPalmFixture != null && !string.IsNullOrEmpty(publicPalmFixture.rawMotionTag), "Public Qwen fixture has no exact original action tag");
        string executable = publicPalmFixture.rawMotionTag;
        Check(DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out DialogueMotionIntent parsed, out string rejection), "Production parser rejected fixed public Qwen tag: " + rejection);
        Check(parsed.Name == "compose" && parsed.ControlPlan != null, "Public Qwen tag did not parse to compose");
        return parsed.ControlPlan.Copy();
    }

    private static void StartPublicPalmFixture()
    {
        string path = Environment.GetEnvironmentVariable("ARDY_PUBLIC_PALM_FIXTURE");
        Check(!string.IsNullOrEmpty(path) && File.Exists(path), "A fixed real Qwen public intent fixture is required; do not replace it with a hand-written plan");
        var currentParsedPlan = ReadPublicPalmPlan();
        publicPalmFixture.plan = currentParsedPlan;
        Check(publicPalmFixture.plan.leftBendAuto && publicPalmFixture.plan.rightBendAuto, "Original omitted elbow degrees did not derive automatic execution freedoms");
        File.Copy(path, Path.Combine(Output, "public-qwen-input.json"), true);
        File.WriteAllText(Path.Combine(Output, "public-qwen-current-parser-plan.json"), JsonUtility.ToJson(publicPalmFixture, true));
        foreach (var f in fixtures)
        {
            f.SetPartner(new Vector3(0, f.InRoot(f.Bone(HumanBodyBones.Head)).y, 2));
            f.ExpectPalmFailure = false;
            try { f.Begin("public-qwen-" + publicPalmFixture.caseId, publicPalmFixture.plan, true); }
            catch (InvalidOperationException error) { f.Current.palm.rejection = error.Message; }
        }
    }
}
