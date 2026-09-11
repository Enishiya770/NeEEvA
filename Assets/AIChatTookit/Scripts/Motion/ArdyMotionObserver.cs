using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeEEvA.Motion
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(12100)]
    public sealed class ArdyMotionObserver : MonoBehaviour
    {
        private ArdyMotionPlayer player;
        private ArdyMotionObservation current, lastCompleted;
        private double beganAt, firstValidAt = -1, stableSince = -1, previousSampleAt = -1;
        private bool previousSampleKnown;
        public const double MaximumContinuousSampleGapSeconds = 0.15;
        private int lastSampleFrame = -1;
        private readonly Queue<ArdyMotionObservation> history = new Queue<ArdyMotionObservation>();
        public event Action<ArdyMotionObservation> ObservationFinished;
        public ArdyMotionObservation Observation => current?.Copy();
        public ArdyMotionObservation SampleSnapshot => current?.Copy();
        public ArdyMotionObservation LastCompleted => lastCompleted?.Copy();
        public bool IsObserving => current != null && current.active;
        public ArdyMotionObservation[] CompletedObservations
        {
            get { var values = history.ToArray(); for (int i = 0; i < values.Length; i++) values[i] = values[i].Copy(); return values; }
        }
        public void Bind(ArdyMotionPlayer value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (IsObserving) FinishObservation(current.actionId, "observer-rebound", true);
            player = value; current = lastCompleted = null; history.Clear(); lastSampleFrame = -1;
            enabled = true;
        }
        public void BeginObservation(string actionId, ArdyMotionGoal goal = null)
        {
            if (player == null || player.Target == null) throw new InvalidOperationException("Bind an actual player before observing motion.");
            if (string.IsNullOrWhiteSpace(actionId)) throw new ArgumentException("An observation needs an action identity.");
            goal?.Validate();
            if (IsObserving) FinishObservation(current.actionId, "observation-replaced", true);
            beganAt = Time.timeAsDouble; firstValidAt = stableSince = previousSampleAt = -1;
            previousSampleKnown = false; lastSampleFrame = -1;
            current = new ArdyMotionObservation { actionId = actionId, goal = goal?.Copy(), hasGoals = goal?.HasTargets ?? false,
                active = true, playbackSerial = player.MotionPlaybackSerial,
                status = goal?.HasTargets == true ? "pending" : "unassessed", reason = "waiting-for-actual-rendered-clip-frame" };
        }
        public void StartObservation(string actionId, ArdyMotionGoal goal = null) => BeginObservation(actionId, goal);
        public ArdyMotionObservation CompleteObservation(bool cancelled) => IsObserving
            ? FinishObservation(current.actionId, cancelled ? "cancelled" : "completed", cancelled) : LastCompleted;

        /// <summary>Seal accumulated post-VRM facts; never sample a Stop/idle pose here.</summary>
        public ArdyMotionObservation FinishObservation(string actionId, string reason, bool cancelled = false)
        {
            if (!IsObserving || !string.Equals(actionId, current.actionId, StringComparison.Ordinal)) return null;
            current.active = false; current.finished = true; current.cancelled = cancelled;
            current.finishReason = reason; current.elapsedSeconds = Math.Max(current.elapsedSeconds, Time.timeAsDouble - beganAt);
            if (!current.hasGoals) { current.status = "unassessed"; current.reason = "no-measurable-goal-specified"; }
            else if (current.everReached) { current.status = "reached"; current.reason = "necessary-regions-observed-for-required-duration"; }
            else if (cancelled) { current.status = "unknown"; current.reason = "cancelled-before-a-sustained-goal-was-observed"; }
            else if (current.observationGapCount > 0 || current.unavailableGoalSampleCount > 0)
            { current.status = "unknown"; current.reason = "incomplete-post-VRM-observation-coverage"; }
            else if (current.validGoalSampleCount < 2 || current.validCoverageSeconds < current.goal.requiredStableSeconds)
            { current.status = "unknown"; current.reason = "insufficient-valid-post-VRM-observation"; }
            else { current.status = "not-reached"; current.reason = "specified-regions-not-sustained-in-observed-motion"; }
            lastCompleted = current.Copy(); history.Enqueue(lastCompleted.Copy()); while (history.Count > 8) history.Dequeue();
            var handlers = ObservationFinished;
            if (handlers != null) foreach (Action<ArdyMotionObservation> handler in handlers.GetInvocationList())
            { try { handler(lastCompleted.Copy()); } catch (Exception error) { Debug.LogException(error, this); } }
            return lastCompleted.Copy();
        }
        private void LateUpdate()
        {
            if (!IsObserving || Time.frameCount == lastSampleFrame) return;
            if (player == null || player.MotionPlaybackSerial != current.playbackSerial)
            { FinishObservation(current.actionId, "player-or-playback-changed", true); return; }
            if (!player.TryCaptureActualRenderedMotion(out var sample))
            { current.skippedFrames++; stableSince = -1; current.stableSeconds = 0; previousSampleKnown = false; return; }
            RecordActualSample(sample);
        }

        // Kept separate from collection so temporal aggregation can be tested with exact
        // timestamps. Production samples enter only through the post-VRM LateUpdate above.
        private void RecordActualSample(ArdyMotionPoseSample sample)
        {
            double interval = previousSampleAt < 0 ? 0 : sample.timeSeconds - previousSampleAt;
            bool continuous = previousSampleAt >= 0 && interval > 0 && interval <= MaximumContinuousSampleGapSeconds;
            if (previousSampleAt >= 0 && !continuous)
            { current.observationGapCount++; stableSince = -1; current.stableSeconds = 0; }
            previousSampleAt = sample.timeSeconds;
            lastSampleFrame = sample.frame;
            current.actual = sample; current.lastFrame = sample.frame; current.sampleCount++;
            current.elapsedSeconds = Math.Max(0, sample.timeSeconds - beganAt);
            bool within = ArdyMotionGoalMeasurement.Evaluate(sample, current.goal, out bool known);
            if (known && previousSampleKnown && continuous) current.validCoverageSeconds += interval;
            previousSampleKnown = known;
            current.currentlyWithinGoal = current.hasGoals && known && within;
            if (!current.hasGoals) { current.status = "unassessed"; current.reason = "actual-pose-recorded-without-goal"; return; }
            if (!known)
            { current.unavailableGoalSampleCount++; current.status = current.everReached ? "reached" : "unknown"; current.reason = "required-actual-bones-unavailable"; stableSince = -1; current.stableSeconds = 0; return; }
            current.validGoalSampleCount++;
            if (firstValidAt < 0) firstValidAt = sample.timeSeconds;
            current.observedSpanSeconds = sample.timeSeconds - firstValidAt;
            if (within)
            {
                if (stableSince < 0) stableSince = sample.timeSeconds;
                current.stableSeconds = sample.timeSeconds - stableSince;
                current.maximumStableSeconds = Math.Max(current.maximumStableSeconds, current.stableSeconds);
                if (current.stableSeconds + 1e-7 >= current.goal.requiredStableSeconds && !current.everReached)
                { current.everReached = true; current.reachedAtSeconds = current.elapsedSeconds; }
            }
            else { stableSince = -1; current.stableSeconds = 0; }
            current.status = current.everReached ? "reached" : "pending";
            current.reason = current.everReached ? "necessary-regions-observed-for-required-duration" : within ? "inside-region-awaiting-continuity" : "outside-required-regions";
        }
        private void OnDisable() { if (IsObserving) FinishObservation(current.actionId, "observer-disabled", true); }
    }
}
