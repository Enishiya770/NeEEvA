using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Deterministic stop/handoff checks with a real imported Humanoid and actual Animator evaluation.</summary>
public static class ArdyLocomotionSmoothStopRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string AnimatorPath = "Assets/AIChatTookit/Animation/Animator Controller.controller";
    private static int checks;
    [Serializable] private sealed class Report
    {
        public string status = "running", error, avatar;
        public string method = "Disposable Edit-mode avatar; actual Animator.Update with explicit production Update/LateUpdate and deterministic blend time. Synthetic Core27 trajectory, no ARDY or network.";
        public string limitation = "Verifies root freeze, pose interpolation, live animation target, ownership and stale timeline fencing. Does not claim automatic Unity/VRM scheduling or visual naturalness acceptance.";
        public int checks;
        public float maximumRootDrift, maximumPoseErrorDegrees, animatorTimeAdvance, maximumRenderedBoneStepDegrees;
        public int renderedReturnFrames;
        public bool liveGuardSmoothStop, liveGuardReasonPreserved, liveGuardClassifiedBlocked, liveGuardForeignOwnerPreserved;
        public int guardCallsAtRejection, guardCallsAfterReturn, guardRenderedReturnFrames;
        public float guardMaximumRootDrift, guardMaximumBoneStepDegrees;
    }
    private sealed class Pose
    {
        public Transform transform;
        public Vector3 position, scale;
        public Quaternion rotation;
    }

    /// <summary>Root's isolated connected fixture supplies a dormant player and an already generated native clip.</summary>
    public static IEnumerator RunRuntimeChecks(ArdyLocomotionPlayer player, ArdyLocomotionClip motion, string outputDirectory = null)
    {
        if (!Application.isPlaying || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0 ||
            player == null || player.Target == null || player.HasPoseOwnership || motion == null)
            throw new InvalidOperationException("Use an idle configured player and existing motion in the isolated runtime fixture.");
        checks = 0;
        var report = new Report { avatar = player.Target.name,
            method = "Actual Unity/VRM/Animator frames on the configured avatar with an existing generated clip; coroutine observations and explicit mid-walk stop, no manual animation/VRM updates or new inference.",
            limitation = "Playback and ownership integration only. Bone-step measurements are diagnostics, not human visual naturalness ratings." };
        string output = Path.Combine(outputDirectory ?? "Logs", "smooth-stop-runtime.json");
        Vector3 originalRoot = player.Target.transform.position; Quaternion originalYaw = player.Target.transform.rotation;
        var routine = RuntimeCore(player, motion, report);
        try
        {
            while (true)
            {
                bool next;
                try { next = routine.MoveNext(); }
                catch (Exception error) { report.status = "failed"; report.error = error.ToString(); throw; }
                if (!next) break;
                yield return routine.Current;
            }
            report.status = "passed";
        }
        finally
        {
            // This fixture's playback never belongs to the room controller. Restore its exact
            // supplied placement after observation, without persisting any scene changes.
            player.Stop(); player.Target.transform.SetPositionAndRotation(originalRoot, originalYaw);
            report.checks = checks;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            File.WriteAllText(output, JsonUtility.ToJson(report, true));
        }
    }

    private static IEnumerator RuntimeCore(ArdyLocomotionPlayer player, ArdyLocomotionClip motion, Report report)
    {
        var avatar = player.Target;
        var animator = avatar.GetComponent<Animator>();
        Check(animator != null && animator.enabled && animator.runtimeAnimatorController != null,
            "Runtime fixture needs its real running Animator before playback.");
        player.Play(motion);
        float start = Time.realtimeSinceStartup;
        float sampleTime = Mathf.Min(.9f, motion.LastSampleSeconds * .5f);
        while (player.TimeSeconds < sampleTime)
        {
            Check(player.IsPlaying && player.HasPoseOwnership && Time.realtimeSinceStartup - start < 8f,
                "Runtime walk did not reach the mid-motion stop sample.");
            yield return null;
        }
        var from = CaptureMapped(avatar);
        var root = avatar.transform.position; var yaw = avatar.transform.rotation;
        float frozenTime = player.TimeSeconds; long previousRevision = player.PlaybackRevision;
        player.RequestSmoothStop(.4f);
        Check(!player.IsPlaying && player.HasPoseOwnership && player.IsReturningToAnimation && player.IsStoppingSmoothly &&
            !player.CompletedNaturally && player.PlaybackRevision > previousRevision,
            "Runtime stop failed to revoke walking while retaining its pose handoff.");
        CheckPose(from, 0, report, "Runtime stop snapped the displayed Humanoid at submission");
        long stoppingRevision = player.PlaybackRevision;
        float animationTime = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
        var previousPose = from;
        start = Time.realtimeSinceStartup;
        while (player.HasPoseOwnership)
        {
            Check(Time.realtimeSinceStartup - start < 3f, "Runtime smooth stop failed to release pose ownership.");
            Check(player.RequestSmoothStop(.1f) && player.PlaybackRevision == stoppingRevision,
                "Repeated runtime stop restarted the handoff.");
            yield return null;
            report.renderedReturnFrames++;
            var pose = CaptureMapped(avatar);
            for (int i = 0; i < pose.Count; i++)
                report.maximumRenderedBoneStepDegrees = Mathf.Max(report.maximumRenderedBoneStepDegrees,
                    Quaternion.Angle(previousPose[i].rotation, pose[i].rotation));
            previousPose = pose;
            report.maximumRootDrift = Mathf.Max(report.maximumRootDrift, Vector3.Distance(root, avatar.transform.position));
            Check(report.maximumRootDrift < .00001f && Quaternion.Angle(yaw, avatar.transform.rotation) < .01f &&
                player.TimeSeconds == frozenTime && player.PlaybackRevision == stoppingRevision,
                "Runtime stop advanced root, clip time, or replaced ownership during animation return.");
        }
        report.animatorTimeAdvance = animator.GetCurrentAnimatorStateInfo(0).normalizedTime - animationTime;
        Check(report.renderedReturnFrames >= 3 && player.ReturnedToAnimation && !player.IsStoppingSmoothly &&
            !player.IsReturningToAnimation && animator.enabled && report.animatorTimeAdvance > .00001f,
            "Runtime return failed to span live Animator frames and release once.");
        for (int i = 0; i < 3; i++)
        {
            yield return null;
            Check(!player.HasPoseOwnership && !player.IsPlaying && Vector3.Distance(root, avatar.transform.position) < .00001f,
                "Old runtime clip resumed after body release.");
        }
        var guarded = RuntimeGuardStop(player, motion, report);
        while (guarded.MoveNext()) yield return guarded.Current;
        player.Play(motion);
        Check(player.IsPlaying && player.HasPoseOwnership && !player.IsStoppingSmoothly && !player.IsReturningToAnimation,
            "New motion could not start after the stopped body's ownership was released.");
        player.Stop();
        Check(!player.HasPoseOwnership && !player.IsPlaying, "Hard Stop no longer releases a replacement clip immediately.");
    }

    private static IEnumerator RuntimeGuardStop(ArdyLocomotionPlayer player, ArdyLocomotionClip motion, Report report)
    {
        const string rejected = "runtime-live-user-clearance-rejection";
        bool shouldReject = false;
        int calls = 0;
        List<Pose> actualFrom = null;
        Vector3 heldRoot = default;
        Quaternion heldYaw = default;
        player.Play(motion, (from, to) => {
            calls++;
            if (!shouldReject) return null;
            actualFrom = CaptureMapped(player.Target);
            heldRoot = player.Target.transform.position; heldYaw = player.Target.transform.rotation;
            return rejected;
        });
        float until = Time.realtimeSinceStartup + 5f;
        while (player.TimeSeconds < Mathf.Min(.6f, motion.LastSampleSeconds * .4f))
        { Check(player.IsPlaying && Time.realtimeSinceStartup < until, "Runtime guarded motion did not start."); yield return null; }
        long revokedRevision = player.PlaybackRevision;
        shouldReject = true;
        while (!player.IsStoppingSmoothly)
        { Check(Time.realtimeSinceStartup < until && player.HasPoseOwnership, "Live root guard did not retain a smooth return."); yield return null; }
        Check(actualFrom != null && player.LastBlockedReason == rejected && player.LastGuardBlockedPlaybackRevision == revokedRevision &&
            player.PlaybackRevision == revokedRevision + 1 && !player.IsPlaying && player.HasPoseOwnership && !player.CompletedNaturally,
            "Runtime root rejection lost its exact reason/revision or released the body abruptly.");
        CheckPose(actualFrom, 0, report, "Runtime live guard snapped the displayed pose at rejection");
        report.guardCallsAtRejection = calls;
        long stopRevision = player.PlaybackRevision;
        float frozenTime = player.TimeSeconds;
        var previous = CaptureMapped(player.Target);
        until = Time.realtimeSinceStartup + 2f;
        while (player.HasPoseOwnership)
        {
            Check(Time.realtimeSinceStartup < until, "Runtime guarded return did not complete.");
            yield return null;
            var current = CaptureMapped(player.Target);
            for (int i = 0; i < current.Count; i++) report.guardMaximumBoneStepDegrees = Mathf.Max(report.guardMaximumBoneStepDegrees,
                Quaternion.Angle(previous[i].rotation, current[i].rotation));
            previous = current;
            report.guardRenderedReturnFrames++;
            report.guardMaximumRootDrift = Mathf.Max(report.guardMaximumRootDrift, Vector3.Distance(heldRoot, player.Target.transform.position));
            Check(report.guardMaximumRootDrift < .00001f && Quaternion.Angle(heldYaw, player.Target.transform.rotation) < .01f &&
                player.PlaybackRevision == stopRevision && player.TimeSeconds == frozenTime && calls == report.guardCallsAtRejection,
                "Guarded handoff moved root, replayed the revoked route guard or changed stop identity.");
        }
        report.guardCallsAfterReturn = calls;
        Check(player.ReturnedToAnimation && !player.IsPlaying && player.LastBlockedReason == rejected && report.guardRenderedReturnFrames >= 3,
            "Runtime guard did not preserve the failure through actual Animator frames.");
        report.liveGuardSmoothStop = report.liveGuardReasonPreserved = true;
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Run only in the disposable unity-naturalness-validation batch project.");
        checks = 0;
        string output = Environment.GetEnvironmentVariable("ARDY_SMOOTH_STOP_OUTPUT") ?? "Logs/ardy-locomotion-smooth-stop.json";
        var report = new Report();
        var scene = EditorSceneManager.NewPreviewScene();
        GameObject root = null;
        try
        {
            report.avatar = AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith("Assets/Model/", StringComparison.Ordinal) &&
                p.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal).First(p =>
                AssetDatabase.LoadAssetAtPath<GameObject>(p)?.GetComponent<Vrm10Instance>()?.Humanoid != null);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(report.avatar);
            var reference = ArdyLocomotionAvatarReference.FromImportedPrefab(prefab.GetComponent<Vrm10Instance>(), report.avatar);
            root = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            root.transform.SetPositionAndRotation(new Vector3(2, .3f, -1), Quaternion.Euler(0, 37, 0));
            root.transform.localScale = Vector3.one * .9f;
            var vrm = root.GetComponent<Vrm10Instance>(); vrm.enabled = false;
            foreach (var a in root.GetComponentsInChildren<Animator>(true)) a.enabled = false;
            var animator = root.GetComponent<Animator>();
            animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AnimatorPath);
            Check(animator.runtimeAnimatorController != null && animator.avatar != null && animator.avatar.isHuman,
                "Fixture requires the real project Animator and imported Humanoid Avatar.");
            animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.enabled = true; animator.Rebind(); animator.Update(.2f);
            var upper = root.AddComponent<ArdyMotionPlayer>(); upper.enabled = true;
            var player = root.AddComponent<ArdyLocomotionPlayer>(); player.Bind(vrm, reference);
            var clip = SyntheticClip(81);
            int guardCalls = 0;
            player.Play(clip, (from, to) => { guardCalls++; return null; });
            player.SampleAt(.5f);
            // Make the actually held pose distinguishable from both the old idle snapshot and
            // the generated sample. A stop must start here, not resample a terminal clip frame.
            vrm.Humanoid.GetBoneTransform(HumanBodyBones.LeftHand).localRotation *= Quaternion.Euler(0, 0, 13);
            var fromPose = CaptureMapped(vrm);
            Vector3 stopRoot = root.transform.position; Quaternion stopYaw = root.transform.rotation;
            float stopTime = player.TimeSeconds; long oldRevision = player.PlaybackRevision;
            int callsAtStop = guardCalls;
            Check(player.RequestSmoothStop(.4f), "An active clip accepts smooth stop synchronously.");
            Check(!player.IsPlaying && !player.CompletedNaturally && player.IsStoppingSmoothly && player.IsReturningToAnimation &&
                player.HasPoseOwnership && animator.enabled && !upper.enabled && player.PlaybackRevision > oldRevision,
                "Stop revokes trajectory while retaining body ownership and resuming only Animator.");
            CheckPose(fromPose, 0, report, "Stop did not retain the measured pose");
            long stopRevision = player.PlaybackRevision;
            Check(player.RequestSmoothStop(.1f) && player.PlaybackRevision == stopRevision,
                "A repeated stop cannot restart or replace the handoff.");
            ExpectRejected(() => player.SampleAt(1f), "Manual sample resumed the cancelled clip");
            ExpectRejected(() => player.ExtendAuditedTimeline(Tail(clip, 101), 40, 100), "Late continuation resumed a cancelled timeline");
            ExpectRejected(() => player.Play(SyntheticClip(81)), "New clip force-restored the pose during smooth stop");
            Invoke(player, "LateUpdate");
            CheckPose(fromPose, 0, report, "Same-frame LateUpdate exposed an Animator binding pose");
            float animatorStart = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
            foreach (float elapsed in new[] { 0f, .1f, .2f, .3f, .4f })
            {
                Invoke(player, "Update"); animator.Update(.05f);
                var animationPose = CaptureMapped(vrm);
                Set(player, "returnElapsed", elapsed);
                Invoke(player, "LateUpdate");
                float weight = Mathf.SmoothStep(0, 1, elapsed / .4f);
                CheckBlended(fromPose, animationPose, weight, report);
                report.maximumRootDrift = Mathf.Max(report.maximumRootDrift, Vector3.Distance(stopRoot, root.transform.position));
                Check(report.maximumRootDrift < .00001f && Quaternion.Angle(stopYaw, root.transform.rotation) < .01f &&
                    player.TimeSeconds == stopTime && guardCalls == callsAtStop,
                    "Root, clip time or revoked route guard advanced during smooth stop.");
                if (elapsed < .4f) Check(player.HasPoseOwnership && !upper.enabled, "Another body producer resumed before the handoff ended.");
            }
            report.animatorTimeAdvance = animator.GetCurrentAnimatorStateInfo(0).normalizedTime - animatorStart;
            Check(report.animatorTimeAdvance > .00001f, "Return must track a running Animator, not a fixed pre-walk pose.");
            Check(!player.HasPoseOwnership && !player.IsReturningToAnimation && !player.IsStoppingSmoothly && player.ReturnedToAnimation &&
                !player.CompletedNaturally && upper.enabled && animator.enabled, "Smooth stop completion did not release ownership correctly.");
            var releasedPose = CaptureMapped(vrm); Invoke(player, "LateUpdate");
            CheckPose(releasedPose, 0, report, "Old clip resumed after release");
            Check(!player.RequestSmoothStop(), "Idle stop must not claim body ownership.");

            CheckLiveGuardStop(vrm, animator, upper, player, clip, report);

            // Revoked tail replacement may shorten or lengthen the old future, but cannot
            // change timing/basis of the exact samples already held by the player.
            player.Play(clip); player.SampleAt(.25f);
            var shorter = Tail(clip, 51); oldRevision = player.PlaybackRevision;
            var beforeTail = CaptureMapped(vrm); stopRoot = root.transform.position;
            player.ExtendAuditedTimeline(shorter, 30, 50);
            Check(player.Clip == shorter && player.PlaybackRevision > oldRevision && player.PlaybackEndSeconds == 2.5f &&
                Vector3.Distance(stopRoot, root.transform.position) < .00001f, "Shorter future tail changed current position or was refused.");
            CheckPose(beforeTail, 0, report, "Tail replacement changed the current pose");
            var longer = Tail(shorter, 101); player.ExtendAuditedTimeline(longer, 30, 100);
            Check(player.Clip == longer && player.PlaybackEndSeconds == 5f, "Longer future tail was refused.");
            var changedFps = Tail(longer, 111); changedFps.rotationClip.fps = 30;
            ExpectRejected(() => player.ExtendAuditedTimeline(changedFps, 30, 110), "Same frame references with changed fps were accepted");
            var changedBasis = Tail(longer, 111); changedBasis.rotationClip.restGlobalRotations[0] = Quaternion.Euler(0, 3, 0);
            ExpectRejected(() => player.ExtendAuditedTimeline(changedBasis, 30, 110), "Same frames with changed rotation basis were accepted");
            player.RequestSmoothStop(); stopRoot = root.transform.position; player.Stop();
            Check(!player.HasPoseOwnership && !player.IsStoppingSmoothly && !player.IsReturningToAnimation && animator.enabled && upper.enabled &&
                Vector3.Distance(stopRoot, root.transform.position) < .00001f, "Forced Stop lost immediate-release semantics.");

            // Converting an already active natural return must not re-capture its temporary
            // Update baseline or restart the remaining duration.
            player.Play(clip); player.SampleAt(clip.LastSampleSeconds); Invoke(player, "LateUpdate");
            player.ReturnToAnimation(.4f); Set(player, "returnElapsed", .2f);
            Check(player.RequestSmoothStop() && !player.CompletedNaturally && (float)Get(player, "returnElapsed") == .2f,
                "Explicit stop restarted an existing natural animation handoff.");
            player.Stop();
            report.status = "passed";
        }
        catch (Exception error) { report.status = "failed"; report.error = error.ToString(); Debug.LogException(error); }
        finally
        {
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            EditorSceneManager.ClosePreviewScene(scene);
            report.checks = checks;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            File.WriteAllText(output, JsonUtility.ToJson(report, true));
        }
        if (report.status != "passed") throw new InvalidOperationException(report.error);
        Debug.Log("ARDY smooth stop regression passed: " + checks + " checks; " + output);
    }

    private static void CheckLiveGuardStop(Vrm10Instance vrm, Animator animator, ArdyMotionPlayer upper,
        ArdyLocomotionPlayer player, ArdyLocomotionClip clip, Report report)
    {
        const string rejected = "fixture-live-user-clearance-rejection";
        bool reject = false; int guardCalls = 0;
        player.Play(clip, (from, to) => { guardCalls++; return reject ? rejected : null; });
        player.SampleAt(.5f);
        vrm.Humanoid.GetBoneTransform(HumanBodyBones.LeftHand).localRotation *= Quaternion.Euler(0, 0, 11);
        var fromPose = CaptureMapped(vrm);
        Vector3 root = vrm.transform.position; Quaternion yaw = vrm.transform.rotation;
        long priorRevision = player.PlaybackRevision;
        reject = true; player.SampleAt(.55f);
        report.guardCallsAtRejection = guardCalls;
        Check(player.IsStoppingSmoothly && player.IsReturningToAnimation && player.HasPoseOwnership && !player.IsPlaying &&
            !player.CompletedNaturally && animator.enabled && !upper.enabled && player.PlaybackRevision == priorRevision + 1 &&
            player.LastGuardBlockedPlaybackRevision == priorRevision && player.LastBlockedReason == rejected,
            "Actual root-step rejection did not retain its exact revision/reason and smooth body ownership.");
        CheckPose(fromPose, 0, report, "Live guard applied the rejected sample or snapped to a saved pose");
        long stopRevision = player.PlaybackRevision; float frozenTime = player.TimeSeconds;
        // Bind only the controller's private ownership ledger, leaving this fixture's actual
        // player and delegate rejection unchanged. No navigation, HTTP or new model is faked.
        var controller = vrm.gameObject.AddComponent<ArdyRoomLocomotionController>();
        Set(controller, "player", player); Set(controller, "ownedClip", clip); Set(controller, "ownedPlayerRevision", priorRevision);
        bool classified = (bool)controller.GetType().GetMethod("TryHandleLiveGuardStop", Private).Invoke(controller, null);
        Check(classified && controller.ExecutionState == ArdyRoomExecutionState.Blocked &&
            controller.ExecutionReason == ArdyRoomExecutionReason.GeneratedPathRejected && controller.Status.Contains(rejected) &&
            player.PlaybackRevision == stopRevision && player.HasPoseOwnership && player.IsStoppingSmoothly,
            "Controller confused its own guard stop with foreign ownership or restarted the pose return.");
        report.liveGuardClassifiedBlocked = true;
        foreach (float elapsed in new[] { 0f, .1f, .2f, .3f, .4f })
        {
            Invoke(player, "Update"); animator.Update(.05f);
            var target = CaptureMapped(vrm);
            Set(player, "returnElapsed", elapsed); Invoke(player, "LateUpdate");
            CheckBlended(fromPose, target, Mathf.SmoothStep(0, 1, elapsed / .4f), report);
            report.guardMaximumRootDrift = Mathf.Max(report.guardMaximumRootDrift, Vector3.Distance(root, vrm.transform.position));
            Check(report.guardMaximumRootDrift < .00001f && Quaternion.Angle(yaw, vrm.transform.rotation) < .01f &&
                guardCalls == report.guardCallsAtRejection && player.TimeSeconds == frozenTime && player.PlaybackRevision == stopRevision,
                "Guard return moved root, invoked the rejected guard again or changed its playback identity.");
        }
        report.guardCallsAfterReturn = guardCalls;
        Check(player.ReturnedToAnimation && !player.HasPoseOwnership && player.LastBlockedReason == rejected && animator.enabled && upper.enabled,
            "Live guard stop lost its diagnostic or failed to restore the live animation owners.");
        report.liveGuardSmoothStop = report.liveGuardReasonPreserved = true;
        var foreign = SyntheticClip(81); player.Play(foreign);
        long foreignRevision = player.PlaybackRevision;
        classified = (bool)controller.GetType().GetMethod("TryHandleLiveGuardStop", Private).Invoke(controller, null);
        controller.Cancel();
        Check(!classified && player.Clip == foreign && player.PlaybackRevision == foreignRevision && player.HasPoseOwnership && player.IsPlaying,
            "Stale guard-stop bookkeeping claimed or cancelled a foreign replacement clip.");
        report.liveGuardForeignOwnerPreserved = true;
        player.Stop(); UnityEngine.Object.DestroyImmediate(controller);
    }

    private static List<Pose> CaptureMapped(Vrm10Instance vrm)
    {
        var human = new[] { HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
            HumanBodyBones.Neck, HumanBodyBones.Head, HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm,
            HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm,
            HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg,
            HumanBodyBones.RightFoot, HumanBodyBones.RightToes };
        return human.Select(vrm.Humanoid.GetBoneTransform).Where(t => t != null).Select(t => new Pose {
            transform = t, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToList();
    }
    private static void CheckPose(List<Pose> poses, float unused, Report report, string label)
    {
        foreach (var p in poses)
            Check(Vector3.Distance(p.position, p.transform.localPosition) < .00001f &&
                Quaternion.Angle(p.rotation, p.transform.localRotation) < .05f && Vector3.Distance(p.scale, p.transform.localScale) < .00001f,
                label + ": " + p.transform.name);
    }
    private static void CheckBlended(List<Pose> from, List<Pose> animation, float weight, Report report)
    {
        for (int i = 0; i < from.Count; i++)
        {
            var p = from[i]; var live = animation[i];
            float rotationError = Quaternion.Angle(Quaternion.Slerp(p.rotation, live.rotation, weight), p.transform.localRotation);
            report.maximumPoseErrorDegrees = Mathf.Max(report.maximumPoseErrorDegrees, rotationError);
            Check(rotationError < .05f && Vector3.Distance(Vector3.Lerp(p.position, live.position, weight), p.transform.localPosition) < .00001f &&
                Vector3.Distance(Vector3.Lerp(p.scale, live.scale, weight), p.transform.localScale) < .00001f,
                "Body handoff did not blend measured pose into this frame's live Animator: " + p.transform.name + " weight=" + weight);
        }
    }
    private static ArdyLocomotionClip SyntheticClip(int count)
    {
        var c = new ArdyLocomotionClip { schema = 1, id = "synthetic-smooth-stop", sourceRootHeight = 1,
            frames = new ArdyLocomotionFrame[count], targets = new ArdyLocomotionTarget[count],
            source = new ArdyLocomotionSource { coordinateSystem = "unity-lh-y-up-z-forward", generation = "synthetic-regression-only" },
            rotationClip = new ArdyMotionClip { schema = 1, id = "synthetic-smooth-stop", text = "Synthetic stop fixture, not ARDY inference.", fps = 20,
                jointNames = (string[])ArdyLiveProtocol.JointNames.Clone(), jointParents = (int[])ArdyLiveProtocol.JointParents.Clone(),
                restGlobalRotations = Enumerable.Repeat(Quaternion.identity, 27).ToArray(), frames = new ArdyMotionFrame[count],
                source = new ArdyMotionSource { coordinateSystem = "unity-lh-y-up-z-forward", rootTranslationApplied = false } } };
        for (int i = 0; i < count; i++)
        {
            var rotations = Enumerable.Repeat(Quaternion.identity, 27).ToArray();
            for (int j = 23; j < 27; j++) rotations[j] = Quaternion.Euler(25, 0, 0);
            for (int j = 14; j < 19; j++) rotations[j] = Quaternion.Euler(0, 0, -45);
            c.frames[i] = new ArdyLocomotionFrame { rootPosition = new Vector3(0, 1, i * .02f), leftFootContact = 0, rightFootContact = 1 };
            c.targets[i] = new ArdyLocomotionTarget { rootPosition = new Vector3(0, 0, i * .02f) };
            c.rotationClip.frames[i] = new ArdyMotionFrame { globalRotations = rotations };
        }
        c.Validate(); return c;
    }
    private static ArdyLocomotionClip Tail(ArdyLocomotionClip source, int count)
    {
        var result = SyntheticClip(count);
        for (int i = 0; i < Mathf.Min(count, source.frames.Length); i++)
        { result.frames[i] = source.frames[i]; result.targets[i] = source.targets[i]; result.rotationClip.frames[i] = source.rotationClip.frames[i]; }
        return result;
    }
    private static object Get(object o, string name) => o.GetType().GetField(name, Private).GetValue(o);
    private static void Set(object o, string name, object value) => o.GetType().GetField(name, Private).SetValue(o, value);
    private static void Invoke(object o, string name) => o.GetType().GetMethod(name, Private).Invoke(o, null);
    private static void ExpectRejected(Action action, string reason)
    {
        try { action(); } catch (InvalidOperationException) { checks++; return; }
        throw new InvalidOperationException(reason);
    }
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
}
