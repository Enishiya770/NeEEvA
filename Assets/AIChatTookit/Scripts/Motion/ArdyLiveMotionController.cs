using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UniVRM10;
using UnityEngine;
using UnityEngine.Networking;

namespace NeEEvA.Motion
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10050)]
    public sealed partial class ArdyLiveMotionController : MonoBehaviour
    {
        public string serviceUrl = "http://127.0.0.1:8093";
        public bool allowGeneratedMotion = true;
        [Tooltip("Position of the conversation partner. Empty uses the main camera, then character forward.")]
        public Transform interactionTarget;
        public bool recordMotionDiagnostics = true;
        [Tooltip("Experimental first-window conditioning length: 4, 8 or 16. Continuations always retain 16 frames.")]
        public int initialConditioningFrames = ArdyLiveProtocol.HistoryFrames;
        [Min(0)] public int seed = 0;
        private ArdyMotionPlayer player;
        private Vrm10Instance target;
        private string characterId = Guid.NewGuid().ToString("N"), turnId;
        private long revision;
        private bool liveRequest;
        private Coroutine generation;
        private UnityWebRequest activeRequest;
        private readonly Queue<ArdyMotionFrame> history = new Queue<ArdyMotionFrame>();
        private float nextHistoryTime;
        private float previousMeasuredTime;
        private ArdyMotionFrame previousMeasuredPose;
        private ArdyMotionTrace trace;
        private string activeMotionName, activeMotionDescription;
        private string lastMotionName, lastMotionDescription, lastMotionEndReason;
        private ArdyControlPlan currentControlPlan;
        private ArdyControlPlan lastControlPlan;
        private ArdyConstraintObservation lastControlObservation;
        private string lastControlError;
        private ArdyControlTrace controlTrace;
        public string LastControlDiagnosticPath { get; private set; }
        public string LastDiagnosticPath { get; private set; }
        public bool IsBound => player != null && player.Target == target && player.BoundBoneCount > 0
            && (!Application.isPlaying || (isActiveAndEnabled && player.isActiveAndEnabled));
        public string Status { get; private set; } = "尚未连接角色";
        public ArdyMotionTimings LastTimings { get; private set; }
        public int ReceivedChunks { get; private set; }
        public long Revision => revision;

        [Serializable] private sealed class ActiveMotionFact { public string name, description, phase; }
        public string DescribeActiveMotion()
        {
            if (!IsBound || string.IsNullOrEmpty(activeMotionName) || (generation == null && !player.IsPlaying)) return "";
            return JsonUtility.ToJson(new ActiveMotionFact { name = activeMotionName,
                description = activeMotionDescription, phase = player.IsConstrained ? player.ConstraintPhase
                    : generation != null ? "generating-or-buffering" : "playing" });
        }

        [Serializable] private sealed class MotionContextFact
        {
            public string phase, name, description, lastRequestedName, lastRequestedDescription, lastEndReason;
            public string endPolicy = "return-to-current-Animator";
            public bool previousRequestedPoseHeld = false;
            public string observation = "Controller lifecycle only; requested poses are not measured success. Use current initialHistory for a new action.";
            public string executionSource;
            public ArdyControlPlan controlPlan;
            public ArdyConstraintObservation measuredConstraintState;
            public ArdyControlPlan lastControlPlan;
            public ArdyConstraintObservation lastControlObservation;
            public string lastControlError;
        }

        /// <summary>Keep lifecycle facts available after completion, without claiming a requested pose was achieved.</summary>
        public string DescribeMotionContext()
        {
            if (!IsBound) return "";
            bool hasActiveIntent = !string.IsNullOrEmpty(activeMotionName) && (generation != null || player.IsPlaying);
            var fact = new MotionContextFact {
                phase = hasActiveIntent ? (player.IsConstrained ? player.ConstraintPhase : generation != null ? "generating-or-buffering" : "playing")
                    : player.IsPlaying ? "returning-to-Animator" : "idle-Animator",
                name = hasActiveIntent ? activeMotionName : "",
                description = hasActiveIntent ? activeMotionDescription : "",
                lastRequestedName = lastMotionName ?? "",
                lastRequestedDescription = lastMotionDescription ?? "",
                lastEndReason = lastMotionEndReason ?? "",
                endPolicy = currentControlPlan != null && player.IsConstrained ? currentControlPlan.end + " (hold limit 30s)" : "return-to-current-Animator",
                previousRequestedPoseHeld = player.IsHoldingPose,
                executionSource = player.IsConstrained ? "procedural-constraints-v1" : "clip-or-Animator",
                controlPlan = player.IsConstrained ? currentControlPlan : null,
                measuredConstraintState = player.IsConstrained ? player.ConstraintObservation : null,
                lastControlPlan = lastControlPlan,
                lastControlObservation = lastControlObservation,
                lastControlError = lastControlError ?? "",
                observation = player.IsConstrained
                    ? "Measured humanoid geometry after VRM; goal errors are observable, naturalness is not automatically accepted."
                    : "See measuredGeneratedMotion and lastExecutionFeedback for post-VRM necessary-goal observations. Lifecycle completion and requested descriptions do not prove complete semantics or naturalness."
            };
            // Unity's inline serializer materializes default nested objects for null
            // fields. Those would invent a plan/observation that never existed.
            var json = JObject.Parse(JsonUtility.ToJson(fact));
            if (fact.controlPlan == null) json[nameof(fact.controlPlan)] = JValue.CreateNull();
            if (fact.measuredConstraintState == null) json[nameof(fact.measuredConstraintState)] = JValue.CreateNull();
            if (fact.lastControlPlan == null) json[nameof(fact.lastControlPlan)] = JValue.CreateNull();
            if (fact.lastControlObservation == null) json[nameof(fact.lastControlObservation)] = JValue.CreateNull();
            AddIntentFacts(json);
            return json.ToString(Formatting.None);
        }

        public void Bind(Vrm10Instance avatar)
        {
            if (avatar == null) throw new ArgumentNullException(nameof(avatar));
            Cancel("rebind");
            if (target != null && target != avatar) characterId = Guid.NewGuid().ToString("N");
            target = avatar;
            lastMotionName = lastMotionDescription = lastMotionEndReason = null;
            player = avatar.GetComponent<ArdyMotionPlayer>();
            if (player == null) player = avatar.gameObject.AddComponent<ArdyMotionPlayer>();
            player.Bind(avatar);
            player.SetInteractionTarget(interactionTarget);
            currentControlPlan = null;
            lastControlPlan = null;
            lastControlObservation = null;
            lastControlError = null;
            var observer = avatar.GetComponent<ArdyConstraintObserver>();
            if (observer == null) observer = avatar.gameObject.AddComponent<ArdyConstraintObserver>();
            observer.Bind(player);
            BindIntentObservation();
            history.Clear();
            previousMeasuredPose = player.CaptureUpperBodyHistoryFrame();
            history.Enqueue(previousMeasuredPose);
            previousMeasuredTime = Time.realtimeSinceStartup;
            nextHistoryTime = previousMeasuredTime + 0.05f;
            Status = "已连接 · 基础手势与实时上身动作就绪";
        }

        public void RequestMotion(string name, string description, int responseGeneration, string actionId = null,
            ArdyMotionGoal goal = null, string parentActionId = null, int repairAttempt = 0)
        {
            if (!IsBound) throw new InvalidOperationException("请先绑定动作角色");
            name = (name ?? "").Trim().ToLowerInvariant();
            if (name != "none" && name != "left-wave" && name != "right-wave" && name != "nod" && name != "shake-head" && name != "generate")
                throw new ArgumentException("未知动作意图");
            description = (description ?? "").Trim();
            if (name == "generate" && (description.Length == 0 || description.Length > 240))
                throw new ArgumentException("实时动作描述需为 1–240 字符");
            if (name == "generate" && seed < 0) throw new ArgumentException("动作随机种子不能为负数");
            if (name == "generate") ArdyMotionCapabilities.ValidateDescription(description);
            if (name == "generate") ArdyLiveProtocol.ValidateConditioningFrames(initialConditioningFrames);
            goal?.Validate();
            if (repairAttempt < 0 || repairAttempt > 1) throw new ArgumentOutOfRangeException(nameof(repairAttempt));
            if (name == "generate" && !allowGeneratedMotion) throw new InvalidOperationException("实时生成未启用");
            if (name == "generate") ValidateServiceUrl();
            Cancel("new-motion-intent");
            turnId = "response-" + responseGeneration;
            ReceivedChunks = 0;
            LastTimings = null;
            if (name == "none") return;
            BeginAction(name, description, responseGeneration, actionId, goal, parentActionId, repairAttempt);
            if (name != "generate")
            {
                var asset = Resources.Load<TextAsset>("ARDY/" + name);
                if (asset == null) throw new InvalidOperationException("缺少已验收动作资源: " + name);
                var mask = name == "left-wave" ? ArdyMotionMask.LeftArm : name == "right-wave" ? ArdyMotionMask.RightArm : ArdyMotionMask.Head;
                player.Play(asset, mask);
                CaptureReplayChunk(ArdyMotionClip.Parse(asset.text), mask, 0, true);
                BeginActionObservation();
                activeMotionName = name;
                activeMotionDescription = description;
                RememberRequestedMotion(name, description);
                Status = name == "right-wave" ? "右挥手 · 已验收镜像" : name == "nod" || name == "shake-head" ? "轻微头部动作 · 受控曲线" : "左挥手 · 已验收生成样例";
                return;
            }
            liveRequest = true;
            activeMotionName = name;
            activeMotionDescription = description;
            RememberRequestedMotion(name, description);
            if (recordMotionDiagnostics)
            {
                trace = new ArdyMotionTrace(characterId, turnId, revision, description, seed, serviceUrl, actionSession?.id, goal);
                LastDiagnosticPath = trace.Path;
            }
            var initial = new ArdyInitialHistory { frames = history.ToArray(), conditioningFrames = initialConditioningFrames };
            if (initial.frames.Length == 0) initial.frames = new[] { player.CaptureUpperBodyHistoryFrame() };
            generation = StartCoroutine(Generate(description, initial, revision, turnId));
        }

        public void Cancel(string reason)
            => CancelOwnedMotion(reason, true);

        private void CancelOwnedMotion(string reason, bool stopPlayer)
        {
            // A specifically requested, already-settled hold has no remaining dynamic
            // action or network job. Conversation alone does not erase that posture.
            // Explicit none/stop, replacement, timeout, rebind and faults still release it.
            if (player != null && player.IsHoldingPose &&
                (reason == "user-started-speaking" || reason == "barge-in" || reason == "new-user-turn"))
            {
                controlTrace?.Event("conversation-during-hold", reason);
                Status = "保持已到达的姿态 · 可继续组合动作，停止可回待机";
                return;
            }
            long previousRevision = revision++;
            string previousTurn = turnId;
            bool cancelRemote = liveRequest;
            FinishAction(reason ?? "cancelled", true);
            if (!string.IsNullOrEmpty(activeMotionName)) lastMotionEndReason = reason ?? "cancelled";
            if (currentControlPlan != null && player != null)
            {
                lastControlObservation = player.ConstraintObservation;
                lastControlError = player.ConstraintFailure;
            }
            if (trace != null) { trace.Finish(reason); trace = null; }
            if (controlTrace != null) { controlTrace.Finish(reason); controlTrace = null; }
            activeMotionName = activeMotionDescription = null;
            liveRequest = false;
            if (activeRequest != null) { activeRequest.Abort(); activeRequest.Dispose(); activeRequest = null; }
            if (generation != null) { StopCoroutine(generation); generation = null; }
            if (stopPlayer && player != null) player.Stop();
            currentControlPlan = null;
            if (cancelRemote && !string.IsNullOrEmpty(previousTurn))
                SendCancellation(previousTurn, previousRevision, reason);
            Status = "动作已取消 · 平滑回待机";
        }

        public void RequestControlPlan(ArdyControlPlan plan, int responseGeneration, string actionId = null,
            string parentActionId = null, int repairAttempt = 0)
        {
            if (!IsBound) throw new InvalidOperationException("请先绑定动作角色");
            if (!allowGeneratedMotion) throw new InvalidOperationException("组合动作未启用");
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var copy = plan.Copy();
            copy.Validate();
            Cancel("new-motion-intent");
            turnId = "response-" + responseGeneration;
            ReceivedChunks = 0;
            LastTimings = null;
            BeginAction("compose", copy.ToString(), responseGeneration, actionId, null, parentActionId, repairAttempt);
            lastControlPlan = copy.Copy();
            lastControlObservation = null;
            lastControlError = null;
            RememberRequestedMotion("compose", copy.ToString());
            if (recordMotionDiagnostics)
            {
                controlTrace = new ArdyControlTrace(characterId, turnId, revision, copy, target);
                LastControlDiagnosticPath = controlTrace.Path;
            }
            try
            {
                player.SetInteractionTarget(interactionTarget);
                player.PlayConstrained(copy);
            }
            catch (Exception error)
            {
                lastControlError = error.Message;
                lastMotionEndReason = "control-plan-rejected";
                controlTrace?.Event("rejected", error.Message);
                controlTrace?.Finish("control-plan-rejected");
                controlTrace = null;
                Status = "组合目标无法执行 · " + error.Message;
                throw;
            }
            currentControlPlan = copy;
            BeginActionObservation();
            activeMotionName = "compose";
            activeMotionDescription = copy.ToString();
            RememberRequestedMotion(activeMotionName, activeMotionDescription);
            Status = "准备目标姿态 · 达标后执行局部动作";
        }

        internal void RecordConstraintObservation()
        {
            if (currentControlPlan != null && player != null)
                lastControlObservation = player.ConstraintObservation;
            if (controlTrace != null && player != null)
                controlTrace.Sample(player.ConstraintPhase, player.ConstraintObservation);
        }

        private void RememberRequestedMotion(string name, string description)
        {
            lastMotionName = name;
            lastMotionDescription = description;
            lastMotionEndReason = "";
        }

        private IEnumerator Generate(string description, ArdyInitialHistory initial, long epoch, string requestTurn)
        {
            for (int chunk = 0; chunk < ArdyLiveProtocol.MaxChunks; chunk++)
            {
                if (epoch != revision || !IsBound) yield break;
                var request = new ArdyGenerateRequest {
                    characterId = characterId, turnId = requestTurn, revision = epoch,
                    requestId = Guid.NewGuid().ToString("N"), chunkIndex = chunk,
                    description = description, seed = seed,
                    initialHistory = chunk == 0 ? initial : null
                };
                Status = chunk == 0 ? "正在提取 Qwen 动作特征并生成首窗" : "实时播放 · 正在续接动作窗口";
                string json = ArdyLiveProtocol.SerializeRequest(request);
                trace?.Add("request", json);
                var web = Post("/v1/motion/generate", json, 22);
                activeRequest = web;
                yield return web.SendWebRequest();
                if (epoch != revision) yield break;
                activeRequest = null;
                trace?.Add("response", web.downloadHandler.text, web.responseCode);
                string error = null;
                bool accepted = false;
                try
                {
                    if (web.result != UnityWebRequest.Result.Success)
                        throw new InvalidOperationException(DescribeHttpFailure(web));
                    if (web.downloadedBytes > 2 * 1024 * 1024)
                        throw new InvalidOperationException("动作响应超过大小上限");
                    var response = JsonUtility.FromJson<ArdyGenerateResponse>(web.downloadHandler.text);
                    accepted = ApplyResponse(response, request, epoch);
                }
                catch (Exception exception) { error = exception.Message; }
                finally { web.Dispose(); }
                if (error != null)
                {
                    // Clear the routine before Cancel so its own failure path can finish cleanly.
                    generation = null;
                    bool ownsPlayer = OwnsActionPlayback();
                    CancelOwnedMotion("motion-error", ownsPlayer);
                    Status = error + (ownsPlayer ? " · 已回待机" : " · 旧动作已取消");
                    yield break;
                }
                if (!accepted) yield break;
            }
            generation = null;
            Status = "实时生成完成 · 正在播放并自动回待机";
        }

        private bool ApplyResponse(ArdyGenerateResponse response, ArdyGenerateRequest request, long epoch)
        {
            if (epoch != revision || !liveRequest || !IsBound) return false;
            // A coroutine may resume before LateUpdate notices another tool's Play/Stop.
            // Do not let an old first window replace that successor or append into it.
            if (!OwnsActionPlayback())
            {
                generation = null; // This response's generator exits on false below.
                CancelOwnedMotion("player-replaced-or-interrupted", false);
                return false;
            }
            ArdyLiveProtocol.ValidateResponse(response, request);
            if (request.chunkIndex == 0) player.PlayStream(response.clip, ArdyMotionMask.UpperBody, response.final);
            else player.AppendStreamChunk(response.clip, response.startFrame, response.final);
            CaptureReplayChunk(response.clip, ArdyMotionMask.UpperBody, response.startFrame, response.final);
            BeginActionObservation();
            LastTimings = response.timings;
            ReceivedChunks++;
            return true;
        }

        private bool OwnsActionPlayback() => actionSession != null && player != null && IsBound &&
            player.MotionPlaybackSerial == actionSession.playbackSerial &&
            (!actionSession.observationStarted || actionSession.name == "compose" || !player.MotionPlaybackWasInterrupted);

        private void LateUpdate()
        {
            // A different tool can replace or disable the Player without going through
            // this controller. Retire this request's authority without stopping its successor.
            if (actionSession != null && !OwnsActionPlayback())
                CancelOwnedMotion("player-replaced-or-interrupted", false);
            if (!IsBound) return;
            if (activeMotionName == "compose" && !string.IsNullOrEmpty(player.ConstraintFailure))
            {
                string failure = player.ConstraintFailure;
                Cancel("constraint-failed: " + failure);
                Status = "组合动作未达到约束 · " + failure;
            }
            else if (activeMotionName == "compose" && player.IsConstrained)
                Status = "约束动作 · " + player.ConstraintPhase;
            if (liveRequest && player.StreamFailure != null)
            {
                string error = player.StreamFailure;
                Cancel("buffer-underrun");
                Status = error;
            }
            if (generation == null && !player.IsPlaying && !string.IsNullOrEmpty(activeMotionName))
            {
                FinishAction("completed-and-returned-to-Animator", false);
                lastMotionEndReason = "completed-and-returned-to-Animator";
                activeMotionName = activeMotionDescription = null;
                if (trace != null) { trace.Finish("completed"); trace = null; }
                if (controlTrace != null) { controlTrace.Finish("completed-and-returned-to-Animator"); controlTrace = null; }
                currentControlPlan = null;
                Status = lastExecutionFeedback?.status == "goal-unmet"
                    ? "动作播放结束 · 可测目标未达到 · 已回待机"
                    : "动作播放结束 · 已回待机";
            }
            float now = Time.realtimeSinceStartup;
            try
            {
                var measured = player.CaptureUpperBodyHistoryFrame();
                if (previousMeasuredPose == null || now - previousMeasuredTime > 0.15f || now < previousMeasuredTime)
                {
                    // Do not interpolate across an Editor pause or a long stalled frame.
                    history.Clear();
                    history.Enqueue(measured);
                    nextHistoryTime = now + 0.05f;
                }
                else while (nextHistoryTime <= now)
                {
                    // Resample observed render poses on a fixed 20-Hz clock, rather than
                    // accumulating frame-scheduling drift (e.g. accidentally capturing at 15 Hz).
                    float t = Mathf.InverseLerp(previousMeasuredTime, now, nextHistoryTime);
                    var rotations = new Quaternion[ArdyLiveProtocol.JointNames.Length];
                    for (int i = 0; i < rotations.Length; i++)
                        rotations[i] = Quaternion.Slerp(previousMeasuredPose.globalRotations[i], measured.globalRotations[i], t);
                    history.Enqueue(new ArdyMotionFrame { globalRotations = rotations });
                    nextHistoryTime += 0.05f;
                }
                previousMeasuredPose = measured;
                previousMeasuredTime = now;
                while (history.Count > ArdyLiveProtocol.HistoryFrames) history.Dequeue();
            }
            catch (Exception exception)
            {
                Cancel("avatar-configuration-changed");
                history.Clear();
                previousMeasuredPose = null;
                target = null;
                Status = exception.Message + " · 请重新绑定";
            }
        }

        private UnityWebRequest Post(string path, string json, int timeoutSeconds)
        {
            var web = new UnityWebRequest(serviceUrl.TrimEnd('/') + path, UnityWebRequest.kHttpVerbPOST);
            web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            web.downloadHandler = new DownloadHandlerBuffer();
            web.SetRequestHeader("Content-Type", "application/json");
            web.timeout = timeoutSeconds;
            return web;
        }

        [Serializable] private sealed class ServiceFailure { public ErrorDetail error; }
        [Serializable] private sealed class ErrorDetail { public string code, message; public string[] fields; }
        private static string DescribeHttpFailure(UnityWebRequest web)
        {
            string detail = web.error;
            try
            {
                if (web.downloadedBytes < 8192)
                {
                    var failure = JsonUtility.FromJson<ServiceFailure>(web.downloadHandler.text);
                    if (failure?.error != null)
                        detail = failure.error.code + ": " + failure.error.message +
                            (failure.error.fields == null ? "" : " (" + string.Join(", ", failure.error.fields) + ")");
                }
            }
            catch (Exception) { /* Non-JSON proxy failures retain the transport message. */ }
            if (detail != null && detail.Length > 700) detail = detail.Substring(0, 700);
            return "动作服务请求失败（HTTP " + web.responseCode + "）：" + detail;
        }

        private void SendCancellation(string previousTurn, long previousRevision, string reason)
        {
            try
            {
                ValidateServiceUrl();
                var web = Post("/v1/motion/cancel", JsonUtility.ToJson(new ArdyCancelRequest {
                    characterId = characterId, turnId = previousTurn, revision = previousRevision,
                    requestId = Guid.NewGuid().ToString("N"), reason = reason ?? "cancelled"
                }), 3);
                // This also works when OnDisable runs and starting another coroutine is impossible.
                web.SendWebRequest().completed += _ => web.Dispose();
            }
            catch (Exception exception) { Debug.LogWarning("ARDY cancel: " + exception.Message, this); }
        }

        private void ValidateServiceUrl()
        {
            if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("动作服务 URL 无效");
        }

        private void OnDisable() => Cancel("controller-disabled");
        private void OnDestroy() => Cancel("controller-destroyed");
    }
}
