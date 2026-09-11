using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NeEEvA.Motion
{
    public sealed partial class ArdyLiveMotionController
    {
        private sealed class ActionSession
        {
            public string id, parent, name, description, replayOf;
            public int responseGeneration, repairAttempt, playbackSerial;
            public ArdyMotionGoal goal;
            public ArdyMotionClip stagedClip;
            public ArdyMotionMask mask;
            public bool observationStarted, allFramesReceived;
        }
        private sealed class ReplayRecord
        {
            public string id, description, name, trajectorySha256;
            public ArdyMotionClip clip;
            public ArdyMotionMask mask;
            public ArdyMotionGoal goal;
            public ArdyMotionObservation observation;
        }
        private ActionSession actionSession;
        private readonly List<ReplayRecord> replayRecords = new List<ReplayRecord>();
        private ArdyMotionObserver motionObserver;
        private ArdyMotionExecutionFeedback lastExecutionFeedback;
        public event Action<ArdyMotionExecutionFeedback> MotionFeedback;
        public string CurrentActionId => actionSession?.id;
        public string LastReplayableActionId => replayRecords.Count == 0 ? null : replayRecords[replayRecords.Count - 1].id;

        private void BindIntentObservation()
        {
            actionSession = null;
            replayRecords.Clear();
            lastExecutionFeedback = null;
            motionObserver = target.GetComponent<ArdyMotionObserver>();
            if (motionObserver == null) motionObserver = target.gameObject.AddComponent<ArdyMotionObserver>();
            motionObserver.Bind(player);
        }

        private void BeginAction(string name, string description, int responseGeneration, string actionId,
            ArdyMotionGoal goal, string parentActionId, int repairAttempt, string replayOf = null)
        {
            actionSession = new ActionSession {
                id = string.IsNullOrWhiteSpace(actionId) ? characterId + "-" + revision : actionId,
                parent = parentActionId, name = name, description = description,
                responseGeneration = responseGeneration, repairAttempt = repairAttempt,
                goal = goal?.Copy(), replayOf = replayOf, playbackSerial = player.MotionPlaybackSerial };
            PublishFeedback("accepted", "Command accepted; no observed success yet.", false, null);
        }

        private void BeginActionObservation()
        {
            if (actionSession == null || actionSession.observationStarted) return;
            motionObserver?.BeginObservation(actionSession.id, actionSession.goal);
            actionSession.playbackSerial = player.MotionPlaybackSerial;
            actionSession.observationStarted = true;
            PublishFeedback("playing", "Playback started; goal is evaluated from actual humanoid samples.", false, null);
        }

        private static ArdyMotionClip CloneClip(ArdyMotionClip clip)
            => ArdyMotionClip.Parse(JsonUtility.ToJson(clip));

        private static string TrajectoryHash(ArdyMotionClip clip)
        {
            var data = JObject.Parse(JsonUtility.ToJson(clip));
            data.Remove("id"); data.Remove("text");
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(data.ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant();
        }

        private void CaptureReplayChunk(ArdyMotionClip clip, ArdyMotionMask mask, int startFrame, bool final)
        {
            if (actionSession == null) return;
            var copy = CloneClip(clip);
            if (startFrame == 0) actionSession.stagedClip = copy;
            else
            {
                var saved = actionSession.stagedClip;
                if (saved == null || startFrame != saved.frames.Length)
                    throw new InvalidOperationException("Replay archive has a non-contiguous window.");
                var frames = new ArdyMotionFrame[saved.frames.Length + copy.frames.Length];
                Array.Copy(saved.frames, frames, saved.frames.Length);
                Array.Copy(copy.frames, 0, frames, saved.frames.Length, copy.frames.Length);
                saved.frames = frames;
            }
            actionSession.mask = mask;
            actionSession.allFramesReceived = final;
        }

        private void FinishAction(string reason, bool cancelled)
        {
            var session = actionSession;
            if (session == null) return;
            ArdyMotionObservation observed = null;
            if (session.observationStarted && motionObserver != null)
            {
                observed = motionObserver.FinishObservation(session.id, reason, cancelled);
                if (observed == null)
                {
                    var previous = motionObserver.LastCompleted;
                    if (previous?.actionId == session.id) observed = previous;
                }
            }
            // Save the exact accepted clip, including semantically unsuccessful clips the user
            // may nonetheless like. Never call it a recording of rendered skin/cloth/Animator.
            bool naturallyPlayed = player != null && player.MotionPlaybackSerial == session.playbackSerial
                && player.MotionPlaybackCompletedNaturally && !player.MotionPlaybackWasInterrupted;
            if (!cancelled && session.allFramesReceived && session.stagedClip != null && naturallyPlayed)
            {
                replayRecords.Add(new ReplayRecord { id = session.id, name = session.name,
                    description = session.description, clip = CloneClip(session.stagedClip),
                    trajectorySha256 = TrajectoryHash(session.stagedClip),
                    mask = session.mask, goal = session.goal?.Copy(), observation = observed });
                while (replayRecords.Count > 4) replayRecords.RemoveAt(0);
            }
            bool unmet = !cancelled && session.goal != null && session.goal.HasTargets && observed?.status == "not-reached";
            var completedTrace = trace;
            PublishFeedback(cancelled ? "cancelled" : unmet ? "goal-unmet" : "completed", reason,
                unmet && session.name == "generate" && session.repairAttempt == 0 && string.IsNullOrEmpty(session.replayOf), observed);
            if (lastExecutionFeedback != null)
            {
                var fact = SerializeFeedback(lastExecutionFeedback);
                completedTrace?.Add("intent-outcome", fact.ToString(Formatting.None));
            }
            if (ReferenceEquals(actionSession, session)) actionSession = null;
        }

        private void PublishFeedback(string status, string reason, bool canRepair, ArdyMotionObservation observation)
        {
            var session = actionSession;
            if (session == null) return;
            var fact = new ArdyMotionExecutionFeedback { actionId = session.id, parentActionId = session.parent,
                name = session.name, description = session.description, responseGeneration = session.responseGeneration,
                repairAttempt = session.repairAttempt, goal = session.goal?.Copy(), replayOf = session.replayOf,
                status = status, reason = reason, observation = observation, canRepair = canRepair };
            lastExecutionFeedback = fact;
            EmitFeedback(fact);
        }

        private void EmitFeedback(ArdyMotionExecutionFeedback fact)
        {
            var callbacks = MotionFeedback;
            if (callbacks == null) return;
            foreach (Action<ArdyMotionExecutionFeedback> callback in callbacks.GetInvocationList())
            {
                try { callback(fact.Copy()); }
                catch (Exception error) { Debug.LogException(error, this); }
            }
        }

        public void ReportRejectedMotion(string actionId, string parentActionId, int responseGeneration,
            int repairAttempt, string name, string description, ArdyMotionGoal goal, string reason)
        {
            if (actionSession != null && actionSession.id == actionId)
            {
                if (actionSession.observationStarted) motionObserver?.FinishObservation(actionId, "rejected", true);
                actionSession = null;
            }
            var fact = new ArdyMotionExecutionFeedback { actionId = actionId, parentActionId = parentActionId,
                responseGeneration = responseGeneration, repairAttempt = repairAttempt, name = name,
                description = description, goal = goal?.Copy(), status = "rejected", reason = reason,
                canRepair = repairAttempt == 0 && allowGeneratedMotion && name != "replay" };
            lastExecutionFeedback = fact;
            EmitFeedback(fact);
        }

        public void RequestReplay(string referenceActionId, int responseGeneration, string actionId = null)
        {
            if (!IsBound) throw new InvalidOperationException("请先绑定动作角色");
            if (!allowGeneratedMotion) throw new InvalidOperationException("动作重放未启用");
            string reference = referenceActionId == "last" ? LastReplayableActionId : referenceActionId;
            var saved = replayRecords.Find(value => value.id == reference);
            if (saved == null) throw new InvalidOperationException("找不到已播放并保存的动作；不能用重新生成冒充重放。");
            var copy = CloneClip(saved.clip);
            Cancel("new-motion-intent");
            turnId = "response-" + responseGeneration;
            ReceivedChunks = 0;
            LastTimings = null;
            BeginAction("replay", saved.description, responseGeneration, actionId, saved.goal, null, 0, saved.id);
            player.PlayStream(copy, saved.mask, true);
            CaptureReplayChunk(saved.clip, saved.mask, 0, true);
            activeMotionName = "replay";
            activeMotionDescription = saved.description;
            RememberRequestedMotion(activeMotionName, activeMotionDescription);
            BeginActionObservation();
            Status = "重放已保存轨迹 · 从当前姿态平滑过渡";
        }

        private void AddIntentFacts(JObject json)
        {
            json["actionId"] = actionSession?.id ?? "";
            json["lastReplayableActionId"] = LastReplayableActionId ?? "";
            json["intentGoal"] = actionSession?.goal == null ? JValue.CreateNull() : JObject.Parse(JsonUtility.ToJson(actionSession.goal));
            var current = actionSession?.observationStarted == true ? motionObserver?.Observation : null;
            json["measuredGeneratedMotion"] = current == null ? JValue.CreateNull() : SerializeObservation(current);
            if (lastExecutionFeedback == null) json["lastExecutionFeedback"] = JValue.CreateNull();
            else
            {
                json["lastExecutionFeedback"] = SerializeFeedback(lastExecutionFeedback);
            }
            var saved = new JArray();
            foreach (var record in replayRecords)
                saved.Add(new JObject { ["actionId"] = record.id, ["requestedDescription"] = record.description,
                    ["frames"] = record.clip.frames.Length, ["seconds"] = record.clip.Duration,
                    ["trajectorySha256"] = record.trajectorySha256,
                    ["goalStatus"] = record.observation?.status ?? "unassessed",
                    ["source"] = "exact-stored-clip; fresh entry blend and current Animator still apply" });
            json["replayableActions"] = saved;
            json["capabilities"] = "stationary upper body; independent index/middle fingers, contact and leg/root translation unsupported";
            json["intentFeedbackContract"] = "motion-intent-feedback-v1";
        }

        private static JObject SerializeObservation(ArdyMotionObservation value)
        {
            var json = JObject.Parse(JsonUtility.ToJson(value));
            if (value.goal == null) json["goal"] = JValue.CreateNull();
            if (value.actual == null) json["actual"] = JValue.CreateNull();
            return json;
        }

        private static JObject SerializeFeedback(ArdyMotionExecutionFeedback value)
        {
            var json = JObject.Parse(JsonUtility.ToJson(value));
            if (value.goal == null) json["goal"] = JValue.CreateNull();
            json["observation"] = value.observation == null ? JValue.CreateNull() : SerializeObservation(value.observation);
            return json;
        }
    }
}
