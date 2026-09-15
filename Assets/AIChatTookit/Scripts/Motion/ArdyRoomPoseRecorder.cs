using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.LowLevel;

namespace NeEEvA.Motion
{
    /// <summary>Bounded, read-only observation of the source actually sampled and the final rendered Humanoid.</summary>
    [DisallowMultipleComponent]
    public sealed class ArdyRoomPoseRecorder : MonoBehaviour
    {
        public const int MaximumSamples = 200, MaximumSegments = 32;
        public const int MaximumRawPayloadBytes = 2 * 1024 * 1024, MaximumSinglePayloadBytes = 256 * 1024;
        public const int MaximumExportBytes = 16 * 1024 * 1024;
        public const float SampleIntervalSeconds = .05f, PostMotionSeconds = 1.5f;

        [Serializable] public sealed class TransformPose
        {
            public Vector3 worldPosition, localPosition, localScale, worldScale;
            public Quaternion worldRotation, localRotation;
        }
        [Serializable] public sealed class BoneDefinition
        {
            public HumanBodyBones human;
            public string name, path;
            public int depth;
            public bool hasImportedRest;
            public Vector3 importedPositionInRoot;
            public Quaternion importedRotationInRoot;
        }
        [Serializable] public sealed class ClipContext
        {
            public int ordinal, operationRevision, wireRevision, globalBaseFrame;
            public string clipId, sessionId, text;
            public bool continuousStream, identityKnown;
            public float fps, sourceRootHeight, motionScale;
            public Vector3 sourceOrigin, sourceZeroInWorld;
            public Quaternion sourceToWorldRotation;
            public string[] jointNames;
            public int[] jointParents;
            public Quaternion[] sourceRestGlobalRotations;
            public ArdyLocomotionPlayer.PoseRecordingBinding[] applicationBindings;
        }
        [Serializable] public sealed class AcceptedSegment
        {
            public string requestId, responseClipId, sessionId, historyKind, outcome, payloadOmission;
            public int operationRevision, wireRevision, controlRevision, startFrame, commitFrames, requestFrames;
            public float submittedAtRealtime, completedAtRealtime;
            public ArdyLocomotionTarget[] plannedSourceTargets;
            public string requestJson, responseJson;
            public int rawUtf8Bytes;
        }
        [Serializable] public sealed class PoseSample
        {
            public int unityFrame, appliedSourceUnityFrame, sequence, operationRevision, controllerRevision, clipOrdinal;
            public int desiredControlRevision, acceptedControlRevision, appliedControlRevision, recoveryCount, underrunCount;
            public long playbackRevision;
            public double realtime, unityTime;
            public float frameDeltaSeconds, bodyClockSeconds, appliedSourceSeconds, confirmedLastSourceSeconds;
            public string stage, executionState, executionReason, sourceApplication, requestA, requestB;
            public bool playing, poseOwned, returningToAnimation, stoppingSmoothly, sourceWithinConfirmedPrefix;
            public bool sourceAppliedThisFrame, staleClipAfterRelease, hasFacePoint;
            public int localFrameA, localFrameB, globalFrameA, globalFrameB;
            public float frameBlend, leftFootContact, rightFootContact, desiredSpeed, measuredSpeed, bufferSeconds;
            public Vector3 sourceRoot, sourcePlannedRoot, targetGround, facePoint;
            public float sourcePlannedHeadingDegrees;
            public Quaternion[] sampledSourceGlobalRotations, sampledSourceDeltas;
            public TransformPose avatarRoot;
            public TransformPose[] renderedHumanoid;
        }
        [Serializable] public sealed class Capture
        {
            public int schema = 1;
            public string capturedUtc, unityVersion, scene, avatar, importedReferencePath, lastRecorderError;
            public string capturePhase = "PostLateUpdate tail, after player LateUpdate (10000), VRM (11000), and controller (12000). One observation per rendered frame at most; no synthetic catch-up frames.";
            public string coordinates = "Unity left-handed Y-up Z-forward. Source root/targets are in source metres. Source global rotations and rest rotations are Core27, matching jointNames/jointParents. Sampled deltas = global * inverse(source rest). Application target = sourceToWorldRotation * sampledDelta * applicationRestRotation; ControlRig targets precede VRM retargeting. Rendered Humanoid world/local transforms are final observed transforms, not source predictions.";
            public string replay = "Use samples in realtime order; resolve clipOrdinal and bone definition indices. Replay renderedHumanoid WORLD transforms in increasing bone depth, with avatarRoot, to reproduce observed body poses independent of intermediate non-Humanoid parents. For source replay use interpolated Core27 rotations/root and clip bases. Animation handoff or released ownership is explicitly tagged; its final rendered pose is not claimed to equal the source pose. Fingers are observed Humanoid bones, not generated Core27 finger motion.";
            public string candidatePayloads = "For continuous stream responses, only commitFrames at startFrame were accepted from the full candidate; the remaining tail is NOT played. Normal audited-playing/audited-appended responses were audited as a whole, but their final playback may stop early. Per-sample source arrays exist only for a confirmed, successfully applied source time. Payload eviction does not erase per-sample source/actual poses; missing request payloads are not implied to be verified.";
            public string rejectedPayloads = "Recent rejected prefixes are historical safety observations, never accepted/rendered poses. Each has its own operation/session/request, realtime, exact checked segment, source/world anchor, route and navigation diagnostic. Collider bounds are from the rejecting query. Additional user guards expose no collider and do not invent one. Control-target checks can fail before a native response exists. Retained raw JSON is exact; explicit omission covers identity mismatch or size limits. History rejection is local pose acquisition, not a service failure.";
            public int maximumSamples = MaximumSamples, maximumSegments = MaximumSegments;
            public int maximumRawPayloadUtf8Bytes = MaximumRawPayloadBytes, rawPayloadUtf8Bytes, droppedSamples, evictedSegments;
            public float sampleIntervalSeconds = SampleIntervalSeconds, observedSpanSeconds, importedGroundY;
            public BoneDefinition[] bones;
            public ClipContext[] clips;
            public AcceptedSegment[] acceptedSegments;
            public ArdyRoomRejectedPrefixEvidence[] recentRejectedPrefixes;
            public ArdyRoomPoseHistoryObservation historyObservation, lastRejectedHistory;
            public string lastRejectedEvidenceError;
            public int maximumRejectedPrefixes = ArdyRoomLocomotionController.MaximumRejectedPrefixes;
            public int maximumRejectedRawUtf8Bytes = ArdyRoomLocomotionController.MaximumRejectedRawUtf8Bytes;
            public PoseSample[] samples;
        }
        [Serializable] private sealed class WireIdentity
        {
            public string sessionId, id;
            public int commitFrames;
            public ArdyLocomotionTarget[] targets;
        }

        [SerializeField] private bool recordingEnabled = true;
        public bool RecordingEnabled { get => recordingEnabled; set { recordingEnabled = value; nextSampleAt = 0; } }
        public int SampleCount => sampleCount;
        public int RawPayloadUtf8Bytes => rawBytes;
        public string LastExportPath { get; private set; }
        public string LastError { get; private set; }

        private ArdyRoomLocomotionController controller;
        private ArdyLocomotionAvatarReference importedReference;
        private readonly PoseSample[] samples = new PoseSample[MaximumSamples];
        private readonly List<AcceptedSegment> acceptedSegments = new List<AcceptedSegment>();
        private readonly HashSet<string> seenRequests = new HashSet<string>();
        private readonly Queue<string> seenRequestOrder = new Queue<string>();
        private readonly List<ClipContext> clips = new List<ClipContext>();
        private BoneDefinition[] boneDefinitions = Array.Empty<BoneDefinition>();
        private Transform[] observedBones = Array.Empty<Transform>();
        private ArdyLocomotionClip previousClip;
        private ClipContext currentClip;
        private int head, sampleCount, sequence, clipOrdinal, rawBytes, droppedSamples, evictedSegments, previousUnityFrame = -1;
        private double nextSampleAt, captureUntil;
        private bool everActive;
        private static readonly List<ArdyRoomPoseRecorder> Observers = new List<ArdyRoomPoseRecorder>();
        private sealed class PoseRecorderLoop { }

        public static ArdyRoomPoseRecorder EnsureAttached(ArdyRoomLocomotionController owner, ArdyLocomotionAvatarReference reference)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            var result = owner.GetComponent<ArdyRoomPoseRecorder>();
            if (result == null) result = owner.gameObject.AddComponent<ArdyRoomPoseRecorder>();
            result.Bind(owner, reference);
            return result;
        }

        public void Bind(ArdyRoomLocomotionController owner, ArdyLocomotionAvatarReference reference)
        {
            if (owner == null || owner.Avatar == null || owner.Player == null || reference == null)
                throw new ArgumentException("A configured room controller and imported rest reference are required.");
            if (controller == owner && ReferenceEquals(importedReference, reference) && observedBones.Length > 0) return;
            controller = owner; importedReference = reference;
            Array.Clear(samples, 0, samples.Length); sampleCount = head = sequence = clipOrdinal = rawBytes = droppedSamples = evictedSegments = 0;
            acceptedSegments.Clear(); seenRequests.Clear(); seenRequestOrder.Clear(); clips.Clear(); previousClip = null; currentClip = null;
            nextSampleAt = captureUntil = 0; previousUnityFrame = -1; everActive = false; LastError = null;
            var definitions = new List<BoneDefinition>(); var transforms = new List<Transform>();
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var human = (HumanBodyBones)i; var bone = owner.Avatar.Humanoid.GetBoneTransform(human);
                if (bone == null) continue;
                var definition = new BoneDefinition { human = human, name = Short(bone.name, 128),
                    path = BonePath(bone, owner.Avatar.transform), depth = Depth(bone) };
                foreach (var entry in reference.entries)
                    if (entry.bone == human) { definition.hasImportedRest = true; definition.importedPositionInRoot = entry.positionInRoot;
                        definition.importedRotationInRoot = entry.rotationInRoot; break; }
                definitions.Add(definition); transforms.Add(bone);
            }
            boneDefinitions = definitions.ToArray(); observedBones = transforms.ToArray();
        }

        private void OnEnable() { if (Application.isPlaying) Register(this); }
        private void OnDisable() { Observers.Remove(this); if (Observers.Count == 0) InstallObserver(false); }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetObservers() { Observers.Clear(); InstallObserver(false); }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RestoreObservers()
        {
            foreach (var observer in FindObjectsOfType<ArdyRoomPoseRecorder>())
                if (observer.isActiveAndEnabled) Register(observer);
        }
        private static void Register(ArdyRoomPoseRecorder observer)
        {
            if (!Observers.Contains(observer)) Observers.Add(observer);
            InstallObserver(true);
        }
        private static void InstallObserver(bool install)
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop(); Rewrite(ref loop); PlayerLoop.SetPlayerLoop(loop);
            void Rewrite(ref PlayerLoopSystem item)
            {
                if (item.subSystemList == null) return;
                var children = new List<PlayerLoopSystem>();
                foreach (var original in item.subSystemList)
                {
                    if (original.type == typeof(PoseRecorderLoop)) continue;
                    var child = original; Rewrite(ref child); children.Add(child);
                }
                if (install && item.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
                    children.Add(new PlayerLoopSystem { type = typeof(PoseRecorderLoop), updateDelegate = ObserveAll });
                item.subSystemList = children.ToArray();
            }
        }
        private static void ObserveAll()
        {
            for (int i = Observers.Count - 1; i >= 0; i--)
            {
                var observer = Observers[i];
                if (observer == null) { Observers.RemoveAt(i); continue; }
                try { observer.Observe(); }
                catch (Exception error)
                {
                    // Evidence must never break the player loop, stop locomotion, or spam the console.
                    observer.LastError = Short(error.GetType().Name + ": " + error.Message, 512);
                    observer.recordingEnabled = false;
                }
            }
        }

        private void Observe()
        {
            if (!recordingEnabled || controller == null || controller.Player == null || controller.Avatar == null) return;
            double now = Time.realtimeSinceStartupAsDouble;
            bool active = controller.IsBusy || controller.IsContinuousFollowActive || controller.Player.HasPoseOwnership;
            if (active) { everActive = true; captureUntil = now + PostMotionSeconds; }
            if (!everActive || now > captureUntil || now + .0001 < nextSampleAt || previousUnityFrame == Time.frameCount) return;
            previousUnityFrame = Time.frameCount;
            // Skip missed ticks, rather than fabricating source observations after a delayed Unity frame.
            nextSampleAt = nextSampleAt <= 0 ? now + SampleIntervalSeconds : nextSampleAt + SampleIntervalSeconds;
            if (nextSampleAt <= now) nextSampleAt = now + SampleIntervalSeconds;
            CaptureAcceptedSegments();
            CaptureFrame(now);
        }

        private void CaptureAcceptedSegments()
        {
            foreach (var evidence in controller.Segments)
            {
                if (evidence == null || (evidence.outcome != "stream-accepted" && evidence.outcome != "audited-playing" &&
                    evidence.outcome != "audited-appended") || string.IsNullOrEmpty(evidence.requestId)) continue;
                string key = evidence.wireRevision + ":" + evidence.requestId;
                if (!seenRequests.Add(key)) continue;
                seenRequestOrder.Enqueue(key);
                while (seenRequestOrder.Count > MaximumSegments * 4) seenRequests.Remove(seenRequestOrder.Dequeue());
                var item = new AcceptedSegment { requestId = Short(evidence.requestId, 128), historyKind = Short(evidence.historyKind, 128),
                    outcome = evidence.outcome, operationRevision = evidence.operationRevision, wireRevision = evidence.wireRevision,
                    controlRevision = evidence.targetRevision, startFrame = evidence.startFrame, requestFrames = evidence.requestFrames,
                    submittedAtRealtime = evidence.submittedAtRealtime, completedAtRealtime = evidence.completedAtRealtime };
                string request = evidence.requestJson ?? "", response = evidence.responseJson ?? "";
                int requestBytes = Encoding.UTF8.GetByteCount(request), responseBytes = Encoding.UTF8.GetByteCount(response);
                if (requestBytes <= MaximumSinglePayloadBytes && responseBytes <= MaximumSinglePayloadBytes)
                {
                    var identity = JsonUtility.FromJson<WireIdentity>(request);
                    var accepted = JsonUtility.FromJson<WireIdentity>(response);
                    item.sessionId = Short(identity?.sessionId, 128);
                    item.responseClipId = Short(accepted?.id, 128);
                    item.commitFrames = evidence.outcome == "stream-accepted" ? accepted?.commitFrames ?? 0 :
                        evidence.auditedFrames > 0 ? evidence.auditedFrames : evidence.requestFrames;
                    // This type discards unrelated fields, including arbitrary language/model metadata.
                    if (identity?.targets != null && identity.targets.Length <= 240) item.plannedSourceTargets = identity.targets;
                    item.requestJson = request; item.responseJson = response; item.rawUtf8Bytes = requestBytes + responseBytes;
                }
                else item.payloadOmission = "Payload exceeds the per-request/response byte bound; not truncated into invalid JSON.";
                AddAcceptedSegment(item);
            }
        }

        private void AddAcceptedSegment(AcceptedSegment item)
        {
            while (acceptedSegments.Count > 0 && (acceptedSegments.Count >= MaximumSegments || rawBytes + item.rawUtf8Bytes > MaximumRawPayloadBytes))
            { rawBytes -= acceptedSegments[0].rawUtf8Bytes; acceptedSegments.RemoveAt(0); evictedSegments++; }
            if (item.rawUtf8Bytes > MaximumRawPayloadBytes)
            { item.requestJson = item.responseJson = null; item.rawUtf8Bytes = 0; item.payloadOmission = "Payload exceeds total raw byte budget."; }
            acceptedSegments.Add(item); rawBytes += item.rawUtf8Bytes;
        }

        private void CaptureFrame(double now)
        {
            var player = controller.Player; var clip = player.Clip;
            if (clip != null && !ReferenceEquals(clip, previousClip))
            {
                var identity = player.GetPoseRecordingIdentity();
                bool streaming = identity != null && !string.IsNullOrEmpty(identity.sessionId);
                currentClip = new ClipContext { ordinal = ++clipOrdinal, clipId = Short(clip.id, 128),
                    operationRevision = identity?.operationRevision ?? -1, wireRevision = identity?.wireRevision ?? -1,
                    globalBaseFrame = identity?.globalBaseFrame ?? 0, sessionId = streaming ? Short(identity.sessionId, 128) : null,
                    identityKnown = identity != null,
                    continuousStream = streaming, fps = clip.rotationClip.fps, text = Short(clip.rotationClip.text, 384),
                    sourceRootHeight = clip.sourceRootHeight, sourceOrigin = clip.sourceOrigin,
                    motionScale = player.MotionScale, sourceZeroInWorld = player.SourceRootToWorld(Vector3.zero),
                    sourceToWorldRotation = player.SourceHeadingToWorld(0), jointNames = (string[])clip.rotationClip.jointNames.Clone(),
                    jointParents = (int[])clip.rotationClip.jointParents.Clone(),
                    sourceRestGlobalRotations = (Quaternion[])clip.rotationClip.restGlobalRotations.Clone(),
                    applicationBindings = player.GetPoseRecordingBindings() };
                clips.Add(currentClip); previousClip = clip;
            }
            var sample = new PoseSample { unityFrame = Time.frameCount, sequence = ++sequence, realtime = now,
                unityTime = Time.timeAsDouble, frameDeltaSeconds = Time.deltaTime, operationRevision = controller.OperationRevision,
                controllerRevision = controller.Revision, clipOrdinal = currentClip?.ordinal ?? 0,
                playbackRevision = player.PlaybackRevision, bodyClockSeconds = player.TimeSeconds,
                appliedSourceUnityFrame = player.LastAppliedSourceUnityFrame,
                appliedSourceSeconds = player.LastAppliedSourceTimeSeconds, confirmedLastSourceSeconds = player.PlaybackEndSeconds,
                desiredControlRevision = controller.StreamControlRevision, acceptedControlRevision = controller.StreamAcceptedControlRevision,
                appliedControlRevision = controller.StreamAppliedControlRevision, recoveryCount = controller.StreamRecoveryCount,
                underrunCount = controller.StreamUnderrunCount, stage = Short(controller.Stage, 128),
                executionState = controller.ExecutionState.ToString(), executionReason = controller.ExecutionReason.ToString(),
                playing = player.IsPlaying, poseOwned = player.HasPoseOwnership, returningToAnimation = player.IsReturningToAnimation,
                stoppingSmoothly = player.IsStoppingSmoothly, sourceApplication = !player.HasPoseOwnership ? "not-applied-owner-released" :
                    player.IsReturningToAnimation ? "frozen-source-to-live-animation-handoff" : "source-applied",
                staleClipAfterRelease = clip != null && !player.HasPoseOwnership,
                targetGround = controller.TargetGround, hasFacePoint = controller.WorldFacePoint.HasValue,
                facePoint = controller.WorldFacePoint ?? Vector3.zero, desiredSpeed = controller.StreamDesiredSpeed,
                measuredSpeed = controller.StreamMeasuredSpeed, bufferSeconds = controller.StreamBufferSeconds,
                avatarRoot = ReadTransform(controller.Avatar.transform), renderedHumanoid = new TransformPose[observedBones.Length] };
            for (int i = 0; i < observedBones.Length; i++) sample.renderedHumanoid[i] = ReadTransform(observedBones[i]);
            if (clip != null && currentClip != null && player.HasAppliedCurrentSource && player.HasPoseOwnership)
                ReadConfirmedSource(sample, clip, currentClip, player.LastAppliedSourceTimeSeconds, player.PlaybackEndSeconds,
                    !player.IsReturningToAnimation && player.LastAppliedSourceUnityFrame == Time.frameCount);
            AddSample(sample);
        }

        private void ReadConfirmedSource(PoseSample sample, ArdyLocomotionClip clip, ClipContext context, float seconds, float confirmedSeconds, bool applied)
        {
            float frame = seconds * clip.rotationClip.fps;
            int a = Mathf.FloorToInt(frame), b = frame > Mathf.Floor(frame) ? Mathf.FloorToInt(frame) + 1 : Mathf.FloorToInt(frame);
            int end = Mathf.FloorToInt(confirmedSeconds * clip.rotationClip.fps + .0001f);
            if (seconds < 0 || seconds > confirmedSeconds + .00001f || a < 0 || b > end || b >= clip.frames.Length) return;
            sample.sourceWithinConfirmedPrefix = true; sample.sourceAppliedThisFrame = applied;
            sample.localFrameA = a; sample.localFrameB = b; sample.globalFrameA = context.globalBaseFrame + a;
            sample.globalFrameB = context.globalBaseFrame + b; sample.frameBlend = Mathf.Clamp01(frame - a);
            sample.sourceRoot = clip.SampleRoot(seconds); sample.leftFootContact = clip.SampleContact(true, seconds);
            sample.rightFootContact = clip.SampleContact(false, seconds);
            sample.sampledSourceGlobalRotations = new Quaternion[27]; sample.sampledSourceDeltas = new Quaternion[27];
            for (int j = 0; j < 27; j++)
            {
                sample.sampledSourceGlobalRotations[j] = Quaternion.Slerp(clip.rotationClip.frames[a].globalRotations[j],
                    clip.rotationClip.frames[b].globalRotations[j], sample.frameBlend);
                sample.sampledSourceDeltas[j] = clip.rotationClip.SampleDelta(j, seconds);
            }
            if (clip.targets != null && b < clip.targets.Length)
            {
                sample.sourcePlannedRoot = Vector3.Lerp(clip.targets[a].rootPosition, clip.targets[b].rootPosition, sample.frameBlend);
                sample.sourcePlannedHeadingDegrees = Mathf.LerpAngle(clip.targets[a].headingDegrees, clip.targets[b].headingDegrees, sample.frameBlend);
            }
            foreach (var item in acceptedSegments)
            {
                if (!context.identityKnown || item.operationRevision != context.operationRevision) continue;
                if (context.continuousStream && (item.wireRevision != context.wireRevision || item.sessionId != context.sessionId)) continue;
                if (!context.continuousStream && (item.outcome == "stream-accepted" || item.wireRevision > context.wireRevision)) continue;
                if (sample.globalFrameA >= item.startFrame && sample.globalFrameA < item.startFrame + item.commitFrames) sample.requestA = item.requestId;
                if (sample.globalFrameB >= item.startFrame && sample.globalFrameB < item.startFrame + item.commitFrames) sample.requestB = item.requestId;
            }
        }

        private void AddSample(PoseSample sample)
        {
            while (sampleCount > 0 && sample.realtime - samples[head].realtime > MaximumSamples * SampleIntervalSeconds)
            { samples[head] = null; head = (head + 1) % MaximumSamples; sampleCount--; droppedSamples++; }
            if (sampleCount == MaximumSamples) { head = (head + 1) % MaximumSamples; droppedSamples++; }
            else sampleCount++;
            samples[(head + sampleCount - 1) % MaximumSamples] = sample;
            // Contexts carry bases only, never hold the rolling full candidate clips alive.
            if (clips.Count > 0)
            {
                int first = samples[head].clipOrdinal;
                while (clips.Count > 1 && clips[0].ordinal < first) clips.RemoveAt(0);
            }
        }

        public Capture ReadCapture()
        {
            var ordered = new PoseSample[sampleCount];
            for (int i = 0; i < sampleCount; i++) ordered[i] = samples[(head + i) % MaximumSamples];
            return new Capture { capturedUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
                scene = controller == null ? null : controller.gameObject.scene.name,
                avatar = controller?.Avatar == null ? null : Short(controller.Avatar.name, 128),
                importedReferencePath = Short(importedReference?.sourcePath, 512), importedGroundY = importedReference?.groundY ?? 0,
                lastRecorderError = LastError, bones = boneDefinitions, clips = clips.ToArray(), acceptedSegments = acceptedSegments.ToArray(),
                recentRejectedPrefixes = controller?.ReadRecentRejectedPrefixes() ?? Array.Empty<ArdyRoomRejectedPrefixEvidence>(),
                historyObservation = controller?.StreamHistoryObservation, lastRejectedHistory = controller?.LastRejectedStreamHistory,
                lastRejectedEvidenceError = controller?.LastRejectedEvidenceError,
                samples = ordered, rawPayloadUtf8Bytes = rawBytes, droppedSamples = droppedSamples, evictedSegments = evictedSegments,
                observedSpanSeconds = sampleCount < 2 ? 0 : (float)(ordered[sampleCount - 1].realtime - ordered[0].realtime) };
        }

        public string ExportRecent()
        {
            if (sampleCount == 0 && (controller == null || controller.ReadRecentRejectedPrefixes().Length == 0 && controller.LastRejectedStreamHistory == null))
                throw new InvalidOperationException("尚无走姿或拒绝记录；请先运行一次房间移动或跟随。");
            var capture = ReadCapture(); string json = JsonUtility.ToJson(capture, false);
            if (Encoding.UTF8.GetByteCount(json) > MaximumExportBytes)
                throw new InvalidOperationException("记录超过导出大小上限；未写出不完整文件。");
#if UNITY_EDITOR
            string root = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Server", "ARDY", "runtime", "room-pose-captures");
#else
            string root = Path.Combine(Application.persistentDataPath, "ARDY", "room-pose-captures");
#endif
            string folder = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "capture.json"), json, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "README.txt"),
                "ARDY synchronized room pose recording, schema 1.\n" + capture.coordinates + "\n\n" + capture.replay + "\n\n" +
                capture.candidatePayloads + "\n\nThe recorder observes only; it never corrects a pose, plans a route, requests motion, or changes ownership.\n" +
                capture.rejectedPayloads + "\n\n" +
                "Raw motion request/response payloads are capped at 2 MiB UTF-8 (up to 4 MiB of UTF-16 string storage), 32 accepted segments; poses at 200 samples.\n" +
                "Additional rejection evidence retains at most 4 prefixes and 4 MiB raw UTF-8 total, at most 1 MiB per request/response, 256 route corners per rejection. It survives a later operation and must be matched by operation/session/time. The complete capture stays within 16 MiB.\n" +
                "Requested capture frequency is 20 Hz; actual frame and realtime are authoritative if Unity misses a tick. Stopped sessions retain the last observations after a 1.5 s handoff post-roll.\n",
                new UTF8Encoding(false));
            LastExportPath = Path.Combine(folder, "capture.json"); return LastExportPath;
        }

        private static TransformPose ReadTransform(Transform bone) => bone == null ? null : new TransformPose {
            worldPosition = bone.position, worldRotation = bone.rotation, worldScale = bone.lossyScale,
            localPosition = bone.localPosition, localRotation = bone.localRotation, localScale = bone.localScale };
        private static string Short(string value, int limit) => value == null || value.Length <= limit ? value : value.Substring(0, limit);
        private static int Depth(Transform bone) { int result = 0; while (bone != null) { result++; bone = bone.parent; } return result; }
        private static string BonePath(Transform bone, Transform root)
        {
            string result = bone.name;
            while (bone.parent != null && bone.parent != root && result.Length < 512) { bone = bone.parent; result = bone.name + "/" + result; }
            return Short(result, 512);
        }
    }
}
