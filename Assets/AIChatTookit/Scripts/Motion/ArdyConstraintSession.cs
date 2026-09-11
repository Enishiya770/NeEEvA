using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>Measurements of the rendered humanoid, not a claim that a requested pose succeeded.</summary>
    [Serializable]
    public sealed class ArdyConstraintObservation
    {
        public string source = "procedural-constraints", phase = "preparing", failure, endReason;
        public bool observed, observationFresh, goalsReached, leftControlled, rightControlled;
        public int observedFrame = -1;
        public float elapsedSeconds, stableSeconds, actingSeconds, holdingSeconds, lostGoalSeconds, observationAgeSeconds;
        public float leftDirectionError, rightDirectionError, leftBend, rightBend;
        public float leftBendError, rightBendError, leftRotationError, rightRotationError;
        public float leftWristAngle, rightWristAngle, headAngle, commandedLocalAngle;
        public float leftWristPositionError, rightWristPositionError;
        public bool leftPalmControlled, rightPalmControlled, leftPalmBasisAvailable, rightPalmBasisAvailable;
        public string partnerSource = "not-requested", partnerTracking = "position-snapshot-at-start";
        public Vector3 partnerPosition, leftPalmNormal, rightPalmNormal, leftPalmTargetNormal, rightPalmTargetNormal;
        public float leftPalmError, rightPalmError, leftPalmTrackingError, rightPalmTrackingError;
        public float leftWristSwing, rightWristSwing, leftForearmRoll, rightForearmRoll;
        public float leftPlannedWristSwing, rightPlannedWristSwing, leftPlannedForearmRoll, rightPlannedForearmRoll;
        public bool leftBendAuto, rightBendAuto;
        public float leftResolvedBend, rightResolvedBend, leftWristMarginDegrees, rightWristMarginDegrees;
        public ArdyResolvedTiming timing;
        public ArdyConstraintObservation Copy()
        {
            var result = (ArdyConstraintObservation)MemberwiseClone();
            result.timing = timing?.Copy();
            return result;
        }
    }

    internal sealed class ArdyConstraintBone
    {
        public HumanBodyBones id;
        public Transform controlled, actual;
        public Quaternion restInRoot;
    }

    internal struct ArdyConstraintBonePose
    {
        public Quaternion controlledRotation, actualRotation;
        public Quaternion controlledParentRotation, actualParentRotation;
        public Vector3 controlledPosition, actualPosition;
    }

    /// <summary>One post-VRM sample. Copied into a session before Update can restore Animator bones.</summary>
    internal sealed class ArdyConstraintPose
    {
        public int frame;
        public Quaternion chestRotation, controlledChestRotation, rootRotation;
        public readonly Dictionary<HumanBodyBones, ArdyConstraintBonePose> bones =
            new Dictionary<HumanBodyBones, ArdyConstraintBonePose>();
    }

    /// <summary>
    /// Bounded geometric goals. This class never writes transforms: ArdyMotionPlayer remains
    /// the sole owner and applies these rotations between Animator and VRM evaluation.
    /// </summary>
    internal sealed class ArdyConstraintSession
    {
        public const float PreparationSeconds = 1.2f, PreparationTimeout = 3f;
        public const float RequiredStableSeconds = 0.15f, HoldSeconds = 30f;
        public const float ObservationTimeout = 0.5f;
        public const float DirectionTolerance = 5f, BendTolerance = 4f;
        private sealed class Arm
        {
            public string goal;
            public float bend, side, upperLength, lowerLength;
            public ArdyConstraintBone upper, lower, hand;
            public Vector3 upperAxis, lowerAxis, capturedDirectionInChest;
            public Quaternion upperInChest, lowerInChest, handInChest;
            public Quaternion upperActualToControl, lowerActualToControl, handActualToControl;
            public Quaternion measuredUpperGoal, measuredLowerGoal, measuredHandBase;
            public Vector3 direction, desiredWrist;
            public Vector3 heldDirectionInChest;
            public float heldBend;
            public string palm;
            public ArdyPalmGeometry.Basis palmBasis;
            public Quaternion prePalmLowerActual, wristNeutralActual;
            public Vector3 palmTarget, palmExpected, waveAxis;
            public float plannedSwing, plannedRoll;
        }

        private readonly ArdyControlPlan plan;
        private readonly ArdyResolvedTiming timing;
        private readonly Transform root, controlledChest;
        private readonly Vector3? partnerPosition;
        private readonly Quaternion chestActualToControl;
        private readonly Dictionary<HumanBodyBones, ArdyConstraintBone> bindings;
        private readonly Dictionary<HumanBodyBones, Quaternion> targets = new Dictionary<HumanBodyBones, Quaternion>();
        private readonly Dictionary<HumanBodyBones, Quaternion> heldInChest = new Dictionary<HumanBodyBones, Quaternion>();
        private readonly Dictionary<HumanBodyBones, Quaternion> actualToControl = new Dictionary<HumanBodyBones, Quaternion>();
        private readonly Arm left, right;
        private readonly ArdyConstraintBone head;
        private readonly Quaternion headInChest;
        private Quaternion measuredHeadBase;
        private float elapsed, stable, acting, holding, lostGoal, observationAge;
        private float localAngle;
        private bool waitingForFinalObservation;
        private float preparationSeconds = PreparationSeconds;
        public bool IsAdaptive => plan.timingPolicy == "adaptive-v1";
        public string Phase { get; private set; } = "preparing";
        public string Failure { get; private set; }
        public string EndReason { get; private set; }
        public bool WantsStop => Phase == "returning" || Phase == "failed";
        public bool IsHolding => Phase == "holding" && Observation.observed && observationAge <= ObservationTimeout;
        public float PreparationBlend => IsAdaptive
            ? (float)ArdyAdaptiveTiming.Quintic(elapsed / preparationSeconds)
            : Mathf.SmoothStep(0, 1, Mathf.Clamp01(elapsed / PreparationSeconds));
        public ArdyConstraintObservation Observation { get; private set; } = new ArdyConstraintObservation();

        public ArdyConstraintSession(ArdyControlPlan requested, Transform avatarRoot, Transform chest,
            Dictionary<HumanBodyBones, ArdyConstraintBone> boneBindings, ArdyConstraintPose rendered,
            Vector3? interactionPosition = null, string interactionSource = "not-requested")
        {
            if (requested == null) throw new ArgumentNullException(nameof(requested));
            plan = requested.Copy();
            plan.Validate();
            timing = ArdyAdaptiveTiming.Resolve(plan);
            root = avatarRoot;
            controlledChest = chest;
            bindings = boneBindings;
            partnerPosition = interactionPosition;
            Observation.partnerSource = interactionSource;
            Observation.partnerPosition = interactionPosition ?? Vector3.zero;
            if (root == null || controlledChest == null || rendered == null)
                throw new InvalidOperationException("A bound chest and rendered humanoid pose are required for constraints.");
            chestActualToControl = Quaternion.Inverse(rendered.chestRotation) * rendered.controlledChestRotation;
            if (plan.leftPalm != "keep" || plan.rightPalm != "keep") ArdyPalmGeometry.ValidateScale(root);
            left = MakeArm(true, plan.left, plan.leftBend, plan.leftPalm, rendered);
            right = MakeArm(false, plan.right, plan.rightBend, plan.rightPalm, rendered);
            if (plan.joint == "head")
            {
                head = Require(HumanBodyBones.Head);
                var pose = rendered.bones[head.id];
                headInChest = Quaternion.Inverse(rendered.chestRotation) * pose.actualRotation;
                actualToControl[head.id] = Quaternion.Inverse(pose.actualRotation) * pose.controlledRotation;
            }
            ResolveAutomaticBend(left, plan.leftBendAuto, plan.joint == "left-wrist" || plan.joint == "wrists");
            ResolveAutomaticBend(right, plan.rightBendAuto, plan.joint == "right-wrist" || plan.joint == "wrists");
            // Populate ownership before the first tick, including a head-only request.
            BuildTargets();
            if (IsAdaptive) ResolvePreparation(rendered);
            RefreshObservation();
        }

        public bool Owns(HumanBodyBones bone) => targets.ContainsKey(bone);
        public bool TryGetTarget(HumanBodyBones bone, out Quaternion rotation) => targets.TryGetValue(bone, out rotation);

        // During an adaptive transition each local joint approaches its final local goal.
        // Using the currently interpolated parent's world rotation instead would move the
        // child's interpolation endpoint every frame and invalidate its distance estimate.
        public bool TryGetPreparationLocalTarget(HumanBodyBones id, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (!IsAdaptive || Phase != "preparing" || !targets.TryGetValue(id, out var target)) return false;
            Transform parent = bindings[id].controlled.parent;
            Quaternion parentGoal = parent == null ? Quaternion.identity : parent.rotation;
            for (Transform ancestor = parent; ancestor != null; ancestor = ancestor.parent)
            {
                bool found = false;
                foreach (var pair in targets)
                {
                    var bone = bindings[pair.Key];
                    if (bone.controlled != ancestor) continue;
                    parentGoal = pair.Value * Quaternion.Inverse(ancestor.rotation) * parent.rotation;
                    found = true;
                    break;
                }
                if (found) break;
            }
            rotation = Quaternion.Inverse(parentGoal) * target;
            return true;
        }

        private void ResolvePreparation(ArdyConstraintPose rendered)
        {
            double required = ArdyAdaptiveTiming.MinimumPreparationSeconds;
            var distances = new Dictionary<HumanBodyBones, float>();
            foreach (var pair in targets)
            {
                var bone = bindings[pair.Key];
                var start = rendered.bones[pair.Key];
                Quaternion actualGoal = pair.Value * Quaternion.Inverse(actualToControl[pair.Key]);
                Quaternion parentGoal = start.actualParentRotation;
                for (Transform ancestor = bone.actual.parent; ancestor != null; ancestor = ancestor.parent)
                {
                    bool found = false;
                    foreach (var parent in targets)
                    {
                        if (bindings[parent.Key].actual != ancestor) continue;
                        Quaternion ancestorGoal = parent.Value * Quaternion.Inverse(actualToControl[parent.Key]);
                        parentGoal = ancestorGoal * Quaternion.Inverse(rendered.bones[parent.Key].actualRotation)
                            * start.actualParentRotation;
                        found = true;
                        break;
                    }
                    if (found) break;
                }
                float angle = Quaternion.Angle(Quaternion.Inverse(start.actualParentRotation) * start.actualRotation,
                    Quaternion.Inverse(parentGoal) * actualGoal);
                distances.Add(pair.Key, angle);
                bool isHead = pair.Key == HumanBodyBones.Head;
                double seconds = ArdyAdaptiveTiming.ResolvePreparation(angle,
                    isHead ? ArdyAdaptiveTiming.HeadVelocityLimit : ArdyAdaptiveTiming.WristVelocityLimit,
                    isHead ? ArdyAdaptiveTiming.HeadAccelerationLimit : ArdyAdaptiveTiming.WristAccelerationLimit);
                if (seconds > required) { required = seconds; timing.preparationLimitingJoint = pair.Key.ToString(); }
                timing.preparationAngularDistance = Mathf.Max(timing.preparationAngularDistance, angle);
            }
            preparationSeconds = (float)(Math.Ceiling(required * 100000) / 100000);
            timing.preparationSeconds = preparationSeconds;
            foreach (var pair in distances)
            {
                bool isHead = pair.Key == HumanBodyBones.Head;
                double speed = isHead ? ArdyAdaptiveTiming.HeadVelocityLimit : ArdyAdaptiveTiming.WristVelocityLimit;
                double acceleration = isHead ? ArdyAdaptiveTiming.HeadAccelerationLimit : ArdyAdaptiveTiming.WristAccelerationLimit;
                timing.preparationVelocityRatio = Mathf.Max(timing.preparationVelocityRatio,
                    (float)(ArdyAdaptiveTiming.QuinticVelocityMaximum * pair.Value / preparationSeconds / speed));
                timing.preparationAccelerationRatio = Mathf.Max(timing.preparationAccelerationRatio,
                    (float)(ArdyAdaptiveTiming.QuinticAccelerationMaximum * pair.Value / (preparationSeconds * (double)preparationSeconds) / acceleration));
            }
        }

        public void Advance(float deltaTime)
        {
            elapsed += deltaTime;
            observationAge += deltaTime;
            if ((Phase == "acting" || Phase == "holding") && observationAge > ObservationTimeout)
                Fail("observation-timeout: no post-VRM humanoid sample for 0.5 seconds");
            if (Phase == "preparing" && elapsed >= PreparationTimeout)
                Fail("prepare-timeout: rendered humanoid did not reach the arm goals for 0.15 seconds");
            else if (Phase == "acting")
            {
                if (IsAdaptive)
                {
                    acting = Mathf.Min(timing.actualSeconds, acting + deltaTime);
                    localAngle = (float)ArdyAdaptiveTiming.Evaluate(timing, acting).value;
                    if (acting >= timing.actualSeconds) { localAngle = 0; waitingForFinalObservation = true; }
                }
                else
                {
                    acting = Mathf.Min(plan.seconds, acting + deltaTime);
                    float progress = acting / plan.seconds;
                    float envelope = Mathf.SmoothStep(0, 1, Mathf.Clamp01(progress / 0.15f))
                        * Mathf.SmoothStep(0, 1, Mathf.Clamp01((1 - progress) / 0.15f));
                    localAngle = plan.joint == "none" ? 0 : plan.amplitude * envelope
                        * Mathf.Sin(2 * Mathf.PI * plan.cycles * progress);
                    if (acting >= plan.seconds) { localAngle = 0; waitingForFinalObservation = true; }
                }
            }
            else if (Phase == "holding")
            {
                holding += deltaTime;
                if (holding >= HoldSeconds) BeginReturn("hold-expired");
            }
            try { BuildTargets(); }
            catch (InvalidOperationException error) { Fail(error.Message); }
            RefreshObservation();
        }

        public void BeginReturn(string reason)
        {
            if (Phase == "completed") return;
            EndReason = EndReason ?? reason;
            if (Failure == null) Phase = "returning";
            RefreshObservation();
        }

        public void FinishReturn()
        {
            Phase = Failure == null ? "completed" : "failed";
            RefreshObservation();
        }

        public void Fail(string reason)
        {
            Failure = reason;
            EndReason = "failed";
            Phase = "failed";
            RefreshObservation();
        }

        /// <summary>Call once per real frame after VRM has mapped normalized bones to the actual mesh.</summary>
        public ArdyConstraintObservation Observe(ArdyConstraintPose rendered, float deltaTime)
        {
            var value = Observation;
            observationAge = 0;
            value.observed = true;
            value.observedFrame = rendered.frame;
            bool leftReached = MeasureArm(left, rendered, true, value);
            bool rightReached = MeasureArm(right, rendered, false, value);
            if (head != null)
                value.headAngle = SignedTwist(rendered.bones[head.id].actualRotation
                    * Quaternion.Inverse(measuredHeadBase), root.rotation * Axis(plan.axis));
            value.goalsReached = leftReached && rightReached;
            if (Phase == "acting" || Phase == "holding")
            {
                // Only the fixed arm goals are monitored; the deliberate wrist/head
                // oscillation must never be compared against a static orientation.
                bool lost = (left != null && (value.leftDirectionError > 12 || value.leftBendError > 12
                    || (left.goal == "current" && value.leftRotationError > 12)
                    || (left.palm != "keep" && (value.leftPalmTrackingError > 12 || value.leftWristSwing > 81))))
                    || (right != null && (value.rightDirectionError > 12 || value.rightBendError > 12
                    || (right.goal == "current" && value.rightRotationError > 12)
                    || (right.palm != "keep" && (value.rightPalmTrackingError > 12 || value.rightWristSwing > 81))));
                lostGoal = lost ? lostGoal + Mathf.Min(deltaTime, 0.1f) : 0;
                if (lostGoal >= 0.25f) Fail("goal-lost: rendered arm constraints were exceeded for 0.25 seconds");
            }
            if (Phase == "preparing")
            {
                stable = elapsed >= preparationSeconds && value.goalsReached
                    ? stable + Mathf.Min(deltaTime, 0.1f) : 0;
                if (stable >= RequiredStableSeconds)
                {
                    // A pure held pose is complete as soon as it has actually settled.
                    // Do not leave a visually finished pose in a cancellable empty acting phase.
                    if (plan.joint == "none" && plan.end == "hold") EnterHolding(rendered);
                    else
                    {
                        Phase = "acting";
                        acting = 0;
                        localAngle = 0;
                    }
                }
            }
            else if (Phase == "acting" && waitingForFinalObservation)
            {
                waitingForFinalObservation = false;
                if (plan.end == "hold") EnterHolding(rendered);
                else BeginReturn("completed");
            }
            RefreshObservation();
            return value.Copy();
        }

        private void EnterHolding(ArdyConstraintPose rendered)
        {
            // Both pure holds and the endpoint of a local curve use this real post-VRM sample.
            // No target or request is substituted for an achieved rendered pose.
            foreach (var pair in targets)
            {
                var pose = rendered.bones[pair.Key];
                heldInChest[pair.Key] = Quaternion.Inverse(rendered.chestRotation) * pose.actualRotation;
                actualToControl[pair.Key] = Quaternion.Inverse(pose.actualRotation) * pose.controlledRotation;
            }
            CaptureHeldArm(left, rendered);
            CaptureHeldArm(right, rendered);
            holding = 0;
            Phase = "holding";
        }

        private Arm MakeArm(bool isLeft, string goal, float bend, string palm, ArdyConstraintPose rendered)
        {
            if (goal == "none") return null;
            var arm = new Arm { goal = goal, bend = bend, palm = palm,
                upper = Require(isLeft ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm),
                lower = Require(isLeft ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm),
                hand = Require(isLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand) };
            var upper = rendered.bones[arm.upper.id];
            var lower = rendered.bones[arm.lower.id];
            var hand = rendered.bones[arm.hand.id];
            arm.upperLength = Vector3.Distance(upper.controlledPosition, lower.controlledPosition);
            arm.lowerLength = Vector3.Distance(lower.controlledPosition, hand.controlledPosition);
            if (arm.upperLength < 0.02f || arm.lowerLength < 0.02f)
                throw new InvalidOperationException("The avatar arm chain is too short or missing for geometric constraints.");
            arm.upperAxis = Quaternion.Inverse(upper.controlledRotation)
                * (lower.controlledPosition - upper.controlledPosition).normalized;
            arm.lowerAxis = Quaternion.Inverse(lower.controlledRotation)
                * (hand.controlledPosition - lower.controlledPosition).normalized;
            var inverseChest = Quaternion.Inverse(rendered.chestRotation);
            arm.upperInChest = inverseChest * upper.actualRotation;
            arm.lowerInChest = inverseChest * lower.actualRotation;
            arm.handInChest = inverseChest * hand.actualRotation;
            arm.capturedDirectionInChest = inverseChest * (hand.actualPosition - upper.actualPosition).normalized;
            arm.upperActualToControl = Quaternion.Inverse(upper.actualRotation) * upper.controlledRotation;
            arm.lowerActualToControl = Quaternion.Inverse(lower.actualRotation) * lower.controlledRotation;
            arm.handActualToControl = Quaternion.Inverse(hand.actualRotation) * hand.controlledRotation;
            actualToControl[arm.upper.id] = arm.upperActualToControl;
            actualToControl[arm.lower.id] = arm.lowerActualToControl;
            actualToControl[arm.hand.id] = arm.handActualToControl;
            // Anatomical sides follow the bound avatar, including an imported model's forward convention.
            var otherId = isLeft ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm;
            float lateral = rendered.bones.TryGetValue(otherId, out var other)
                ? (Quaternion.Inverse(rendered.rootRotation) * (upper.actualPosition - other.actualPosition)).x
                : (isLeft ? -1 : 1);
            arm.side = lateral < 0 ? -1 : 1;
            if (goal == "current")
                arm.bend = Vector3.Angle(lower.actualPosition - upper.actualPosition, hand.actualPosition - lower.actualPosition);
            if (palm != "keep")
            {
                ArdyPalmGeometry.ValidateScale(arm.upper.actual);
                ArdyPalmGeometry.ValidateScale(arm.lower.actual);
                ArdyPalmGeometry.ValidateScale(arm.hand.actual);
                arm.palmBasis = ArdyPalmGeometry.CreateBasis(rendered, isLeft);
            }
            return arm;
        }

        private void ResolveAutomaticBend(Arm arm, bool automatic, bool moveWrist)
        {
            if (!automatic) return;
            if (arm == null || arm.goal == "current" || arm.palm == "keep")
                throw new ArgumentException("Automatic elbow solving requires a geometric arm goal and an explicit palm goal.");
            bool found = false, foundMargin = false;
            float bestBend = 0, bestScore = float.PositiveInfinity;
            // A general free-degree-of-freedom search, independent of words such as greeting.
            // Prefer modest flexion with 10 degrees of wrist headroom. If no such solution
            // exists, retain the best reachable margin and report it explicitly.
            for (int candidate = 0; candidate <= 110; candidate++)
            {
                arm.bend = candidate;
                try { BuildArm(arm, ActualChest, moveWrist); }
                catch (InvalidOperationException error)
                {
                    if (error.Message.StartsWith("palm-unreachable:", StringComparison.Ordinal)
                        || error.Message.StartsWith("palm-target-undefined:", StringComparison.Ordinal)) continue;
                    throw;
                }
                bool margin = arm.plannedSwing <= ArdyPalmGeometry.MaximumWristSwing - 10;
                float score = margin ? Mathf.Abs(candidate - 40) + arm.plannedSwing * 0.02f
                    : arm.plannedSwing * 1000 + Mathf.Abs(candidate - 40);
                if (!found || (margin && !foundMargin) || (margin == foundMargin && score < bestScore))
                {
                    found = true; foundMargin = margin; bestBend = candidate; bestScore = score;
                }
            }
            if (!found)
                throw new InvalidOperationException("palm-unreachable: no automatic elbow bend in 0..110 degrees satisfies the requested arm direction, palm and wrist curve within the engineering limits");
            // Fixed for the entire session. Changes in the viewer/idle pose never re-search
            // or quietly alter an explicitly requested elbow angle during execution.
            arm.bend = bestBend;
            BuildArm(arm, ActualChest, moveWrist);
        }

        private ArdyConstraintBone Require(HumanBodyBones id)
        {
            if (!bindings.TryGetValue(id, out var result) || result.controlled == null || result.actual == null)
                throw new InvalidOperationException("Missing humanoid bone for constraints: " + id);
            return result;
        }

        private Quaternion ActualChest => controlledChest.rotation * Quaternion.Inverse(chestActualToControl);

        private static void CaptureHeldArm(Arm arm, ArdyConstraintPose rendered)
        {
            if (arm == null) return;
            var upper = rendered.bones[arm.upper.id];
            var lower = rendered.bones[arm.lower.id];
            var hand = rendered.bones[arm.hand.id];
            arm.heldDirectionInChest = Quaternion.Inverse(rendered.chestRotation)
                * (hand.actualPosition - upper.actualPosition).normalized;
            arm.heldBend = Vector3.Angle(lower.actualPosition - upper.actualPosition, hand.actualPosition - lower.actualPosition);
        }

        private void BuildTargets()
        {
            Quaternion chest = ActualChest;
            if (Phase == "holding")
            {
                foreach (var pair in heldInChest)
                    targets[pair.Key] = chest * pair.Value * actualToControl[pair.Key];
                UpdateHeldMeasurement(left, chest);
                UpdateHeldMeasurement(right, chest);
                if (head != null) measuredHeadBase = chest * heldInChest[head.id];
                return;
            }
            BuildArm(left, chest, plan.joint == "left-wrist" || plan.joint == "wrists");
            BuildArm(right, chest, plan.joint == "right-wrist" || plan.joint == "wrists");
            if (head != null)
            {
                measuredHeadBase = chest * headInChest;
                targets[head.id] = Quaternion.AngleAxis(localAngle, root.rotation * Axis(plan.axis))
                    * measuredHeadBase * actualToControl[head.id];
            }
        }

        private void UpdateHeldMeasurement(Arm arm, Quaternion chest)
        {
            if (arm == null) return;
            arm.waveAxis = root.rotation * Axis(plan.axis);
            arm.measuredUpperGoal = chest * heldInChest[arm.upper.id];
            arm.measuredLowerGoal = chest * heldInChest[arm.lower.id];
            arm.measuredHandBase = chest * heldInChest[arm.hand.id];
            arm.direction = chest * arm.heldDirectionInChest;
            arm.desiredWrist = arm.upper.controlled.position + targets[arm.upper.id] * arm.upperAxis * arm.upperLength
                + targets[arm.lower.id] * arm.lowerAxis * arm.lowerLength;
            if (arm.palm != "keep")
            {
                arm.palmExpected = arm.measuredHandBase * arm.palmBasis.normalInHand;
                arm.palmTarget = PalmDirection(arm, arm.desiredWrist);
                arm.wristNeutralActual = targets[arm.lower.id] * Quaternion.Inverse(arm.lower.restInRoot)
                    * arm.hand.restInRoot * Quaternion.Inverse(actualToControl[arm.hand.id]);
                arm.waveAxis = plan.axis == "palm-normal" ? arm.palmExpected : root.rotation * Axis(plan.axis);
            }
        }

        private void BuildArm(Arm arm, Quaternion chest, bool moveWrist)
        {
            if (arm == null) return;
            Quaternion upper, lower, hand;
            if (arm.goal == "current")
            {
                upper = chest * arm.upperInChest * arm.upperActualToControl;
                lower = chest * arm.lowerInChest * arm.lowerActualToControl;
                hand = chest * arm.handInChest * arm.handActualToControl;
                arm.direction = chest * arm.capturedDirectionInChest;
            }
            else
            {
                Vector3 direction = root.rotation * Direction(arm.goal, arm.side);
                arm.direction = direction;
                Vector3 pole = Vector3.ProjectOnPlane(root.rotation * (Vector3.down + Vector3.right * arm.side * 0.25f), direction);
                if (pole.sqrMagnitude < 0.001f)
                    pole = Vector3.ProjectOnPlane(root.rotation * Vector3.forward, direction);
                pole.Normalize();
                float aLength = arm.upperLength, bLength = arm.lowerLength;
                float distance = Mathf.Sqrt(aLength * aLength + bLength * bLength
                    + 2 * aLength * bLength * Mathf.Cos(arm.bend * Mathf.Deg2Rad));
                float along = (aLength * aLength - bLength * bLength + distance * distance) / (2 * distance);
                float perpendicular = Mathf.Sqrt(Mathf.Max(0, aLength * aLength - along * along));
                Vector3 elbow = direction * along + pole * perpendicular;
                Vector3 wrist = direction * distance;
                upper = Align(arm.upper, arm.upperAxis, elbow.normalized);
                lower = Align(arm.lower, arm.lowerAxis, (wrist - elbow).normalized);
                // A new geometric goal uses the model's rest wrist relative to the forearm.
                hand = lower * Quaternion.Inverse(arm.lower.restInRoot) * arm.hand.restInRoot;
            }
            // First locate the requested elbow/wrist. Axial forearm roll below leaves both
            // positions unchanged; a new palm request never silently changes arm geometry.
            arm.desiredWrist = arm.upper.controlled.position + upper * arm.upperAxis * arm.upperLength
                + lower * arm.lowerAxis * arm.lowerLength;
            arm.waveAxis = root.rotation * Axis(plan.axis);
            if (arm.palm != "keep")
            {
                arm.palmTarget = PalmDirection(arm, arm.desiredWrist);
                if (plan.axis == "palm-normal") arm.waveAxis = arm.palmTarget;
                arm.prePalmLowerActual = lower * Quaternion.Inverse(arm.lowerActualToControl);
                // Explicit palm goals use the model's neutral hand/forearm relationship
                // even with current, so an already bent wrist cannot hide excessive strain.
                Quaternion neutralHand = lower * Quaternion.Inverse(arm.lower.restInRoot) * arm.hand.restInRoot;
                var solved = ArdyPalmGeometry.Solve(lower, neutralHand, arm.handActualToControl,
                    lower * arm.lowerAxis, arm.palmBasis, arm.palmTarget, arm.waveAxis,
                    moveWrist ? plan.amplitude : 0);
                lower = solved.forearm;
                hand = solved.hand;
                arm.wristNeutralActual = solved.neutralHandActual;
                arm.plannedSwing = solved.maximumCurveSwing;
                arm.plannedRoll = solved.roll;
                arm.palmExpected = Quaternion.AngleAxis(moveWrist ? localAngle : 0, arm.waveAxis) * arm.palmTarget;
            }
            arm.measuredUpperGoal = upper * Quaternion.Inverse(arm.upperActualToControl);
            arm.measuredLowerGoal = lower * Quaternion.Inverse(arm.lowerActualToControl);
            arm.measuredHandBase = hand * Quaternion.Inverse(arm.handActualToControl);
            // Predict the hand position from the controlled chain without writing any transforms.
            arm.desiredWrist = arm.upper.controlled.position + upper * arm.upperAxis * arm.upperLength
                + lower * arm.lowerAxis * arm.lowerLength;
            targets[arm.upper.id] = upper;
            targets[arm.lower.id] = lower;
            targets[arm.hand.id] = moveWrist
                ? Quaternion.AngleAxis(localAngle, arm.waveAxis) * hand : hand;
        }

        private Vector3 PalmDirection(Arm arm, Vector3 wrist)
        {
            switch (arm.palm)
            {
                case "partner":
                    if (!partnerPosition.HasValue) return root.forward;
                    Vector3 direction = partnerPosition.Value - wrist;
                    if (direction.sqrMagnitude < 1e-6f)
                        throw new InvalidOperationException("palm-target-undefined: interaction target overlaps the wrist");
                    return direction.normalized;
                case "up": return root.up;
                case "down": return -root.up;
                case "inward": return -root.right * arm.side;
                case "outward": return root.right * arm.side;
                default: throw new InvalidOperationException("Unsupported explicit palm goal: " + arm.palm);
            }
        }

        private Quaternion Align(ArdyConstraintBone bone, Vector3 localAxis, Vector3 direction)
        {
            var reference = root.rotation * bone.restInRoot;
            return Quaternion.FromToRotation(reference * localAxis, direction) * reference;
        }

        private bool MeasureArm(Arm arm, ArdyConstraintPose rendered, bool isLeft, ArdyConstraintObservation result)
        {
            if (arm == null) return true;
            var upper = rendered.bones[arm.upper.id];
            var lower = rendered.bones[arm.lower.id];
            var hand = rendered.bones[arm.hand.id];
            float directionError = Vector3.Angle(hand.actualPosition - upper.actualPosition, arm.direction);
            float bend = Vector3.Angle(lower.actualPosition - upper.actualPosition, hand.actualPosition - lower.actualPosition);
            float bendError = Mathf.Abs(bend - (Phase == "holding" ? arm.heldBend : arm.bend));
            float rotationError = Mathf.Max(Quaternion.Angle(upper.actualRotation, arm.measuredUpperGoal),
                Quaternion.Angle(lower.actualRotation, arm.measuredLowerGoal));
            float wristAngle = SignedTwist(hand.actualRotation * Quaternion.Inverse(arm.measuredHandBase), arm.waveAxis);
            float wristPositionError = Vector3.Distance(hand.actualPosition, arm.desiredWrist);
            bool palmReached = true;
            if (arm.palm != "keep")
            {
                ArdyPalmGeometry.Measure(rendered, isLeft, out var normal, out var longitudinal);
                Vector3 targetNormal = PalmDirection(arm, hand.actualPosition);
                float facingError = Vector3.Angle(normal, targetNormal);
                float trackingError = Vector3.Angle(normal, arm.palmExpected);
                Quaternion actualNeutralHand = lower.actualRotation * arm.lowerActualToControl
                    * Quaternion.Inverse(arm.lower.restInRoot) * arm.hand.restInRoot
                    * Quaternion.Inverse(arm.handActualToControl);
                float wristSwing = ArdyPalmGeometry.SwingDegrees(hand.actualRotation * Quaternion.Inverse(actualNeutralHand),
                    (hand.actualPosition - lower.actualPosition).normalized);
                float forearmRoll = SignedTwist(lower.actualRotation * Quaternion.Inverse(arm.prePalmLowerActual),
                    (hand.actualPosition - lower.actualPosition).normalized);
                if (isLeft)
                {
                    result.leftPalmNormal = normal; result.leftPalmTargetNormal = targetNormal;
                    result.leftPalmError = facingError; result.leftPalmTrackingError = trackingError;
                    result.leftWristSwing = wristSwing; result.leftForearmRoll = forearmRoll;
                }
                else
                {
                    result.rightPalmNormal = normal; result.rightPalmTargetNormal = targetNormal;
                    result.rightPalmError = facingError; result.rightPalmTrackingError = trackingError;
                    result.rightWristSwing = wristSwing; result.rightForearmRoll = forearmRoll;
                }
                palmReached = trackingError <= 5 && wristSwing <= 80.01f;
            }
            if (isLeft)
            {
                result.leftDirectionError = directionError; result.leftBend = bend; result.leftBendError = bendError;
                result.leftRotationError = rotationError; result.leftWristAngle = wristAngle;
                result.leftWristPositionError = wristPositionError;
            }
            else
            {
                result.rightDirectionError = directionError; result.rightBend = bend; result.rightBendError = bendError;
                result.rightRotationError = rotationError; result.rightWristAngle = wristAngle;
                result.rightWristPositionError = wristPositionError;
            }
            return directionError <= DirectionTolerance && bendError <= BendTolerance && palmReached
                && (arm.goal != "current" || rotationError <= DirectionTolerance);
        }

        private void RefreshObservation()
        {
            Observation.phase = Phase; Observation.failure = Failure; Observation.endReason = EndReason;
            Observation.leftControlled = left != null; Observation.rightControlled = right != null;
            Observation.leftPalmControlled = left != null && left.palm != "keep";
            Observation.rightPalmControlled = right != null && right.palm != "keep";
            Observation.leftPalmBasisAvailable = Observation.leftPalmControlled;
            Observation.rightPalmBasisAvailable = Observation.rightPalmControlled;
            Observation.leftPlannedWristSwing = left == null ? 0 : left.plannedSwing;
            Observation.rightPlannedWristSwing = right == null ? 0 : right.plannedSwing;
            Observation.leftPlannedForearmRoll = left == null ? 0 : left.plannedRoll;
            Observation.rightPlannedForearmRoll = right == null ? 0 : right.plannedRoll;
            Observation.leftBendAuto = plan.leftBendAuto; Observation.rightBendAuto = plan.rightBendAuto;
            Observation.leftResolvedBend = left == null ? 0 : left.bend;
            Observation.rightResolvedBend = right == null ? 0 : right.bend;
            Observation.leftWristMarginDegrees = Observation.leftPalmControlled ? 80 - left.plannedSwing : 0;
            Observation.rightWristMarginDegrees = Observation.rightPalmControlled ? 80 - right.plannedSwing : 0;
            Observation.elapsedSeconds = elapsed; Observation.stableSeconds = stable;
            Observation.actingSeconds = acting; Observation.holdingSeconds = holding;
            Observation.lostGoalSeconds = lostGoal;
            Observation.observationAgeSeconds = observationAge;
            Observation.observationFresh = Observation.observed && observationAge <= ObservationTimeout
                && (Phase == "preparing" || Phase == "acting" || Phase == "holding");
            if (!Observation.observationFresh) Observation.goalsReached = false;
            Observation.commandedLocalAngle = localAngle;
            Observation.timing = timing.Copy();
        }

        private static Vector3 Direction(string goal, float side)
        {
            switch (goal)
            {
                case "forward": return Vector3.forward;
                case "outward": return Vector3.right * side;
                case "up": return Vector3.up;
                case "down": return Vector3.down;
                case "forward-up": return (Vector3.forward + Vector3.up).normalized;
                case "outward-up": return (Vector3.right * side + Vector3.up).normalized;
                default: throw new ArgumentException("Unknown geometric arm direction: " + goal);
            }
        }

        private static Vector3 Axis(string axis) => axis == "up" ? Vector3.up : axis == "right" ? Vector3.right : Vector3.forward;

        private static float SignedTwist(Quaternion delta, Vector3 axis)
        {
            delta = delta.normalized;
            if (delta.w < 0) delta = new Quaternion(-delta.x, -delta.y, -delta.z, -delta.w);
            float projection = Vector3.Dot(new Vector3(delta.x, delta.y, delta.z), axis.normalized);
            return Mathf.DeltaAngle(0, 2 * Mathf.Atan2(projection, delta.w) * Mathf.Rad2Deg);
        }
    }
}
