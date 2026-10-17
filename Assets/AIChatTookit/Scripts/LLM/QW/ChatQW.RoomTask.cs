using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    private int m_RoomTaskRequestGeneration;
    private UnityWebRequest m_RoomTaskRequest;
    private int m_RoomPreparationGeneration;
    private UnityWebRequest m_RoomPreparationRequest;
    private string m_RoomPreparationRequestId;
    public override bool SupportsRoomTaskMessages => true;
    public override bool SupportsRoomTaskSequences => true;
    public override bool SupportsRoomPreparationMessages => true;
    public override bool SupportsRoomObservationHistory => true;
    public override void CancelRoomTaskPlanningMessage() { ClearSpeechClaimCache(); CancelRoomTaskMessage(); }

    public override void PostRoomPreparationMessage(string input, Action<bool, string, string> callback)
    {
        CancelRoomPreparationMessage();
        StartCoroutine(RequestRoomPreparation(input ?? "", m_RoomPreparationGeneration, callback));
    }

    public override void CancelRoomPreparationMessage()
    {
        ++m_RoomPreparationGeneration;
        if (m_RoomPreparationRequest == null) return;
        try { if (!m_RoomPreparationRequest.isDone) m_RoomPreparationRequest.Abort(); } catch (Exception) { }
        m_RoomPreparationRequest = null;
    }

    private IEnumerator RequestRoomPreparation(string input, int generation, Action<bool, string, string> callback)
    {
        if (generation != m_RoomPreparationGeneration) yield break;
        float startedAt = Time.realtimeSinceStartup;
        bool admitted = TryBudgetRoomDecisionRequest(BuildRoomPreparationRequestJson(input), false, out string bounded, out var budget);
        budget["generation"] = generation; budget["kind"] = "preparation"; budget["httpRequestSent"] = false;
        Debug.Log("[Room/DecisionBudget] " + budget.ToString(Newtonsoft.Json.Formatting.None));
        if (generation != m_RoomPreparationGeneration) yield break;
        if (!admitted)
        {
            callback?.Invoke(false, "", "room-task-http-context-budget;attempts=0;recovery=preflight-budget");
            yield break;
        }
        using (var request = new UnityWebRequest(url, "POST"))
        {
            m_RoomPreparationRequest = request;
            string requestId = Guid.NewGuid().ToString("N");
            m_RoomPreparationRequestId = requestId;
            string kind = RoomControlReviewProtocol.Kind(input) ?? "legacy";
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(bounded));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = 2;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("X-Request-ID", requestId);
            request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            double sentAt = Time.realtimeSinceStartupAsDouble;
            string sentUtc = DateTime.UtcNow.ToString("O");
            double? firstBytes = null;
            Debug.Log("[Room/ControlTiming] " + new JObject { ["requestId"] = requestId, ["generation"] = generation,
                ["kind"] = kind, ["state"] = "sent", ["slot"] = JObject.Parse(bounded)["id_slot"],
                ["sentUtc"] = sentUtc, ["wireBytes"] = Encoding.UTF8.GetByteCount(bounded), ["timeoutSeconds"] = 2 }.ToString(Newtonsoft.Json.Formatting.None));
            var operation = request.SendWebRequest();
            while (!operation.isDone && generation == m_RoomPreparationGeneration)
            {
                if (!firstBytes.HasValue && request.downloadedBytes > 0) firstBytes = Time.realtimeSinceStartupAsDouble - sentAt;
                yield return null;
            }
            if (!firstBytes.HasValue && request.downloadedBytes > 0) firstBytes = Time.realtimeSinceStartupAsDouble - sentAt;
            if (ReferenceEquals(m_RoomPreparationRequest, request)) m_RoomPreparationRequest = null;
            JObject envelope = null;
            try { envelope = JObject.Parse(request.downloadHandler.text ?? ""); } catch (Exception) { }
            Debug.Log("[Room/ControlTiming] " + new JObject { ["requestId"] = requestId, ["generation"] = generation,
                ["kind"] = kind, ["state"] = generation == m_RoomPreparationGeneration ? "completed" : "cancelled",
                ["sentUtc"] = sentUtc, ["httpSeconds"] = Time.realtimeSinceStartupAsDouble - sentAt,
                ["firstBytesSeconds"] = firstBytes, ["receivedBytes"] = request.downloadedBytes,
                ["httpStatus"] = request.responseCode, ["transport"] = request.result.ToString(), ["error"] = request.error,
                ["serverTimings"] = envelope?["timings"], ["usage"] = envelope?["usage"], ["serverQueueSeconds"] = null,
                ["scope"] = "First bytes are observed at Unity frame granularity. Server timings are reported values; missing queue timing is unknown. Cancellation never authorizes a callback." }.ToString(Newtonsoft.Json.Formatting.None));
            if (generation != m_RoomPreparationGeneration) yield break;
            string output = "", status = "preparation-http-failure";
            bool accepted = request.result == UnityWebRequest.Result.Success && request.responseCode == 200 &&
                TryReadRoomTaskCompletion(request.downloadHandler.text, out output, out status);
            Debug.Log($"[Room/PreparationWire] kind={RoomControlReviewProtocol.Kind(input) ?? "legacy"} code={request.responseCode} accepted={accepted} elapsed={Time.realtimeSinceStartup-startedAt:F3}s status={status} transport={request.result} error={request.error}");
            if (kind == RoomControlReviewProtocol.BodyStop)
            {
                bool replacement = (bool?)JObject.Parse(input).SelectToken("activeBody.allowReplacement") == true;
                string parseFailure = null;
                RoomTaskProtocol.Decision control = null;
                bool parsed = accepted && RoomControlReviewProtocol.TryBodyDecision(output,
                    (string)JObject.Parse(input)["currentUserText"], replacement, out control, out parseFailure);
                Debug.Log("[Room/ControlReview] " + new JObject { ["requestId"] = requestId, ["generation"] = generation,
                    ["allowReplacement"] = replacement, ["parsed"] = parsed, ["intent"] = control?.intent,
                    ["parseFailure"] = parseFailure, ["review"] = output.Length <= 1600 ? output : output.Substring(0, 1600),
                    ["executionAuthority"] = false }.ToString(Newtonsoft.Json.Formatting.None));
            }
            callback?.Invoke(accepted, output, status);
        }
    }

    private string BuildRoomPreparationRequestJson(string input)
    {
        string kind = RoomControlReviewProtocol.Kind(input);
        if (kind == null) return BuildRoomTaskRequestJson(input, false);
        string schema = RoomControlReviewProtocol.SchemaForInput(kind, input);
        var sb = new StringBuilder("{\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":256,\"response_format\":{\"type\":\"json_object\"");
        if (m_Backend == BackendType.Local) sb.Append(",\"schema\":").Append(schema);
        sb.Append("},\"messages\":[");
        bool allowReplacement = (bool?)JObject.Parse(input).SelectToken("activeBody.allowReplacement") == true;
        AppendMessage(sb, new SendData("system", RoomControlReviewProtocol.Contract(kind, allowReplacement) + "\n" + schema)); sb.Append(',');
        AppendMessage(sb, new SendData("user", input));
        if(kind==RoomControlReviewProtocol.BodyStop) {
            sb.Append(',');AppendMessage(sb,new SendData("system","先在reason中说明完整本轮最终要求做什么，是否仍要求当前activeBody所代表的身体任务。" +
                "质问为什么没做并重申刚才的要求，是当前催促执行；我刚才说去做某事这种催促中的复述不能一律判成历史转述。仅讨论过去、明确撤回或尚未满足条件时才不执行。" +
                "当前跟随中要求去处理另一任务是other-task/command-now，即使没有明确说停止，explicitStopNow也仍为false。" +
                "最后独立判断显式身体停止：只指停止activeBody，不包括取消一个未执行的物件任务。后来明确继续跟随时为continue-current，不能把取消旧物件操作解释成停止当前跟随。"));
        }
        sb.Append(']');
        if (m_Backend == BackendType.Local) sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        AppendSlot(sb, k_SlotAuxiliary); sb.Append('}');
        return sb.ToString();
    }

    public override void PostRoomTaskMessage(string input, bool reviewSpeech, Action<bool, string, string> callback)
        => PostRoomTaskMessage(input, null, reviewSpeech, callback);

    public override void PostRoomTaskMessage(string input, string imageDataUrl, bool reviewSpeech,
        Action<bool, string, string> callback)
    {
        if (!reviewSpeech) ClearSpeechClaimCache();
        CancelRoomTaskMessage();
        int generation = m_RoomTaskRequestGeneration;
        if (reviewSpeech)
        {
            try { RoomTaskProtocol.BuildSpeechReviewSchemaForInput(input); }
            catch (Exception)
            {
                Debug.LogWarning("[Room/SpeechReview] invalid input; refused schema downgrade before HTTP.");
                callback?.Invoke(false, "", "speech-review-input-invalid"); return;
            }
        }
        if (reviewSpeech && TryStartIndependentSpeechReview(input, imageDataUrl, generation, callback)) return;
        // Capture a complete payload now; no shared image/context field survives this call.
        string json = BuildRoomTaskRequestJsonWithImage(input ?? "", imageDataUrl, reviewSpeech);
        StartCoroutine(RequestRoomTaskPayload(json, reviewSpeech, generation,
            reviewSpeech ? callback : GroundedAutonomyCallback(input, imageDataUrl, callback)));
    }

    public override void PostRoomTaskMessageWithObservationHistory(string input, string imageDataUrl,
        IReadOnlyList<RoomObservationImage> history, Action<bool, string, string> callback)
    {
        CancelRoomTaskMessage();
        int generation = m_RoomTaskRequestGeneration;
        string json = BuildRoomTaskRequestJsonWithObservationHistory(input ?? "", imageDataUrl, history, false);
        StartCoroutine(RequestRoomTaskPayload(json, false, generation, GroundedAutonomyCallback(input, imageDataUrl, callback)));
    }

    private Action<bool, string, string> GroundedAutonomyCallback(string input, string image,
        Action<bool, string, string> callback)
    {
        if (!AutonomyPremiseProtocol.Required(input)) return callback;
        return (ok, output, status) => {
            if (!ok) { callback?.Invoke(false, output, status); return; }
            bool accepted = AutonomyPremiseProtocol.TryResolve(input, !string.IsNullOrEmpty(image), output, out string decision, out var premise);
            Debug.Log("[Room/AutonomyPremise] " + new JObject { ["accepted"] = accepted,
                ["premise"] = premise, ["actionAuthorization"] = false }.ToString(Newtonsoft.Json.Formatting.None));
            callback?.Invoke(accepted, accepted ? decision : "", accepted ? status : AutonomyPremiseProtocol.Rejected);
        };
    }

    private void CancelRoomTaskMessage()
    {
        ++m_RoomTaskRequestGeneration;
        if (m_RoomTaskRequest == null) return;
        try { if (!m_RoomTaskRequest.isDone) m_RoomTaskRequest.Abort(); } catch (Exception) { }
        m_RoomTaskRequest = null;
    }

    private IEnumerator RequestRoomTask(string input, bool review, int generation, Action<bool, string, string> callback)
        => RequestRoomTaskPayload(BuildRoomTaskRequestJson(input, review), review, generation, callback);

    private IEnumerator RequestRoomTaskPayload(string json, bool review, int generation, Action<bool, string, string> callback)
    {
        if (generation != m_RoomTaskRequestGeneration) yield break;
        float start = Time.realtimeSinceStartup;
        // Speech review has the same complete-payload ceiling as planning.
        // Candidate speech, user words and execution receipts remain mandatory.
        {
            bool admitted = TryBudgetRoomDecisionRequest(json, false, out string bounded, out var budget);
            budget["generation"] = generation; budget["httpRequestSent"] = false;
            Debug.Log((review ? "[Room/ReviewBudget] " : "[Room/DecisionBudget] ") + budget.ToString(Newtonsoft.Json.Formatting.None));
            if (generation != m_RoomTaskRequestGeneration) yield break;
            if (!admitted)
            {
                PublishRoomTaskFailure(generation, 0, review, start,
                    "room-task-http-context-budget;attempts=0;recovery=preflight-budget", callback);
                yield break;
            }
            json = bounded;
        }
        int attempts = 0;
        int currentBudgetCeiling = RoomDecisionBudget;
        while (generation == m_RoomTaskRequestGeneration && attempts < RoomTaskMaximumAttempts)
        {
            ++attempts;
            var requestDiagnostic = BuildRoomTaskRequestDiagnostic(json);
            requestDiagnostic["generation"] = generation; requestDiagnostic["attempt"] = attempts;
            requestDiagnostic["kind"] = review ? "speech-review" : "decision";
            if (!review) requestDiagnostic["budget"] = MeasureRoomDecisionBudget(json, currentBudgetCeiling);
            Debug.Log("[Room/TaskRequest] " + requestDiagnostic.ToString(Newtonsoft.Json.Formatting.None));
            string nextJson = json, stopReason = "not-eligible";
            float delay = 0;
            bool retry = false;
            using (var request = new UnityWebRequest(url, "POST"))
            {
                m_RoomTaskRequest = request;
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = Mathf.Max(1, Mathf.Min(12,
                    Mathf.FloorToInt(RoomTaskRecoveryBudgetSeconds - (Time.realtimeSinceStartup - start))));
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
                yield return request.SendWebRequest();
                if (ReferenceEquals(m_RoomTaskRequest, request)) m_RoomTaskRequest = null;
                // No logging, retries or publication can outlive the request owner.
                if (generation != m_RoomTaskRequestGeneration) yield break;
                bool success = request.result == UnityWebRequest.Result.Success && request.responseCode == 200;
                string envelope = request.downloadHandler.text;
                if (success)
                {
                    PublishRoomTaskCompletion(generation, true, request.responseCode, envelope, review, start, callback);
                    yield break;
                }
                var failure = BuildRoomTaskHttpFailure(request.responseCode, envelope, request.error);
                string category = (string)failure["category"];
                retry = TryPlanRoomTaskTransportRetry(review, attempts, generation == m_RoomTaskRequestGeneration,
                    RoomTaskRecoveryBudgetSeconds - (Time.realtimeSinceStartup - start), category,
                    request.GetResponseHeader("Retry-After"), json, out nextJson, out delay, out stopReason);
                if (retry && category == "context-limit") currentBudgetCeiling = RoomDecisionRecoveryBudget;
                failure["generation"] = generation; failure["attempt"] = attempts;
                failure["kind"] = review ? "speech-review" : "decision";
                failure["elapsedSeconds"] = Time.realtimeSinceStartup - start;
                failure["request"] = requestDiagnostic;
                failure["retryPlanned"] = retry;
                failure["retryDelaySeconds"] = retry ? new JValue(delay) : JValue.CreateNull();
                failure["recovery"] = retry ? "pending" : stopReason;
                Debug.LogWarning("[Room/TaskHttpFailure] " + failure.ToString(Newtonsoft.Json.Formatting.None));
                if (!retry)
                {
                    PublishRoomTaskFailure(generation, request.responseCode, review, start,
                        "room-task-http-" + category + ";attempts=" + attempts + ";recovery=" + stopReason, callback);
                    yield break;
                }
            }
            // Wait in-frame so cancellation/new speech can invalidate the retry.
            float readyAt = Time.realtimeSinceStartup + delay;
            while (Time.realtimeSinceStartup < readyAt)
            {
                if (generation != m_RoomTaskRequestGeneration) yield break;
                yield return null;
            }
            if (generation != m_RoomTaskRequestGeneration) yield break;
            if (Time.realtimeSinceStartup - start >= RoomTaskRecoveryBudgetSeconds - 1)
            {
                PublishRoomTaskFailure(generation, 0, review, start,
                    "room-task-http-timeout;attempts=" + attempts + ";recovery=budget-exhausted", callback);
                yield break;
            }
            json = nextJson;
        }
    }

    private void PublishRoomTaskFailure(int generation, long responseCode, bool review, float startedAt,
        string status, Action<bool, string, string> callback)
    {
        if (generation != m_RoomTaskRequestGeneration) return;
        Debug.Log($"[Room/TaskWire] kind={(review ? "speech-review" : "decision")} code={responseCode} accepted=False elapsed={Time.realtimeSinceStartup-startedAt:F3}s status={status}");
        callback?.Invoke(false, "", status);
    }

    private string BuildRoomTaskRequestJson(string input, bool review)
        => BuildRoomTaskRequestJsonWithImage(input, null, review);

    private string BuildRoomTaskRequestJsonWithImage(string input, string imageDataUrl, bool review)
        => BuildRoomTaskRequestJsonWithObservationHistory(input, imageDataUrl, null, review);

    private string BuildRoomTaskRequestJsonWithObservationHistory(string input, string imageDataUrl,
        IReadOnlyList<RoomObservationImage> history, bool review)
    {
        // Build the working set before serializing images/schema or sending HTTP.
        // The controller retains the complete task and observation records.
        if (!review) input = PrepareRoomPlanningWorkingMemory(input);
        else input = ApplySceneAttentionToInput(input);
        JObject purposeInput=null;
        try { purposeInput=ReadRoomTaskRecoveryJson(input); }
        catch(JsonException) { /* An invalid/legacy packet cannot select a purpose projection. */ }
        bool failureReview = review && RoomTaskContextPolicy.PlanningFailureReview(purposeInput);
        if (failureReview)
        {
            input = RoomTaskContextPolicy.ProjectPlanningFailureReview(purposeInput).ToString(Formatting.None);
            imageDataUrl = null; history = null;
        }
        if (!review) {
            try { input = RoomTaskContextPolicy.PlanningObservationView(ReadRoomTaskRecoveryJson(input), RoomDecisionUnknownObservationFields).ToString(Formatting.None); }
            catch (JsonException) { /* Invalid evidence stays invalid; do not replace it with an empty view. */ }
        }
        bool bodyFocused = false;
        if (!review && TryFocusCurrentUserBodyPlanning(input, out string focusedBodyInput))
        {
            input = focusedBodyInput; bodyFocused = true;
            imageDataUrl = null;
            history = null;
        }
        string schema = review ? RoomTaskProtocol.BuildSpeechReviewSchemaForInput(input) : BuildRoomDecisionSchemaForInput(input);
        if (failureReview) {
            var receiptSchema=JObject.Parse(schema);var fields=(JObject)receiptSchema["properties"];var ordered=new JObject();
            foreach(string key in new[]{"otherClaimsComparison","otherClaimsSupport","requestDisposition","planningFailureReport"})
                if(fields[key]!=null)ordered[key]=fields[key].DeepClone();
            foreach(var field in fields.Properties())if(ordered[field.Name]==null)ordered[field.Name]=field.Value.DeepClone();
            receiptSchema["properties"]=ordered;schema=receiptSchema.ToString(Formatting.None);
        }
        bool bodyContinuation = bodyFocused && RoomTaskContextPolicy.ReadyUserIntents(ReadRoomTaskRecoveryJson(input)) != null;
        if (review) input = RoomTaskProtocol.ProjectSpeechReviewInput(input);
        bool eventReview = review && string.IsNullOrEmpty(imageDataUrl) && RoomSpeechEventEvidenceProtocol.AutonomousConversation(ReadRoomTaskRecoveryJson(input));
        string contract = review ? (eventReview ? SpeechEventBindingProtocol.ReviewContract + RoomSpeechEventEvidenceProtocol.ReviewContract : RoomTaskProtocol.ReviewContract) : (bodyFocused ? (bodyContinuation ? BodyContinuationPlanningContract : BodyPlanningContract) : RoomTaskProtocol.DecisionContract) +
            (schema.Contains("\"taskStep\"") ? RoomTaskSequenceProtocol.Contract : "");
        if (failureReview) contract = RoomTaskProtocol.PlanningFailureReviewContract;
        if (review && schema.Contains("\"answerCoverage\"")) contract = ReplyAnswerObligationProtocol.ReviewContract +
            ReplyAnswerObligationProtocol.Contract + (schema.Contains("\"eventBindings\"") ? SpeechEventBindingProtocol.ReviewContract : "") +
            (failureReview ? RoomTaskProtocol.PlanningFailureReviewContract : "");
        if (review && !eventReview && schema.Contains("\"eventOrderConsistency\""))
            contract = RoomSpeechEventEvidenceProtocol.EventOrderContract + contract;
        if (!review && AutonomyPremiseProtocol.Required(input)) contract = AutonomyPremiseProtocol.Contract + "\n" + contract;
        if (review && ReadRoomTaskRecoveryJson(input)["replyPurpose"] is JObject purpose &&
            (string)purpose["source"] == "accepted-current-autonomous-decision")
            contract = "Review this NEW autonomous reply purpose. requestedUserText is historical context, not a new obligation to repeat a completed task. " +
                "Only current owned reporting requirements remain mandatory; preserve separately pending answers/results. " +
                "The intention is a proposal/topic, never evidence for factual claims about what the user said, felt or did. " +
                "Compare such claims with recentDialogue and actual observations. Persona, closeness, imagination and tool success cannot prove user events. " +
                "For autonomous-conversation without an owned action, requestDisposition=answer-only and taskRelation=not-applicable unless an actual new owned relation exists.\n" + contract;
        if (review && RoomSpeechPartsProtocol.Required(ReadRoomTaskRecoveryJson(input)))
            contract = RoomSpeechPartsProtocol.Contract + "\n\n" + contract;
        var sb = new StringBuilder("{\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":").Append(review ?
            (schema.Contains("\"eventBindings\"") || schema.Contains("\"answerCoverage\"") ? 640 : schema.Contains("\"targetConsistency\"") ? 480 : 240) : 640);
        sb.Append(",\"response_format\":{\"type\":\"json_object\"");
        if (m_Backend == BackendType.Local) sb.Append(",\"schema\":").Append(review ? schema : RoomTaskProtocol.CompactNativeDecisionSchema(schema));
        sb.Append("},\"messages\":[");
        // The local transport already submits every intent branch as the native grammar.
        // Give the model each field definition once; cloud providers still need the full
        // schema in text. The contract and runtime ownership checks remain authoritative.
        string schemaText = !review && m_Backend == BackendType.Local
            ? "[Native decision field guide]\n" + (bodyFocused ?
                "intent: approach|follow|stop-moving|none; origin: user|none; evidence: exact current-utterance quote, at most 160 characters; replyScope: ordinary|spatial for none. Task sequence fields, when required by the schema, still follow the full task coverage contract. Omit unrelated execution fields." :
                BuildRoomDecisionFieldGuide(schema)) : review && m_Backend == BackendType.Local ?
                RoomTaskProtocol.CompactDecisionFieldGuide(JObject.Parse(schema)) : schema;
        if (bodyFocused && !bodyContinuation) contract += "\n最后核对：currentRequestSemantics只是前置分类，即使requestKind=action也可能判断错；执行授权必须来自当前完整原话的外层真实意图。" +
            "复述、转述、引用或询问过去指令，不是现在要求执行引号内/被转述的动作。此时taskPlan=[]，taskCoverage逐字覆盖原话为context/answer，taskStep=null，intent=none，origin=none，evidence为空。" +
            "若确有当前身体动作要求，则其步骤必须实际列在taskPlan中，taskStep引用已列步骤；不能输出空计划却引用s1。先判断外层意图，再按当前仍有效的请求拆步。";
        AppendMessage(sb, new SendData("system", contract + "\n" + schemaText +
            (review ? "\n" + SpeechActScopeProtocol.ReviewContract : ""))); sb.Append(',');
        if (review && !eventReview && !failureReview && schema.Contains("\"targetComparison\""))
        { AppendMessage(sb, new SendData("system", RoomTaskProtocol.SpeechTargetComparisonContract)); sb.Append(','); }
        if (review && !eventReview && ReadRoomTaskRecoveryJson(input)["replyPurpose"] is JObject)
        {
            AppendMessage(sb,new SendData("system","这是新自主回复的事实检查。对话中的用户事件、说过的话、请求和情绪同样是硬事实，不能因为出现在亲昵/社交台词中就免查。" +
                "逐项核对independentSpeechClaim.otherClaims：若声称用户先前说过/做过某事，otherClaimsComparison必须引用recentDialogue中的对应原话及role，并解释原话如何支持该声称；" +
                "若没有相应原话，明确写证据缺失，otherClaimsSupport=unsupported, verdict=inconsistent。提案意图、人设、身体距离、动作完成和候选台词本身都不是用户事件证据。" +
                "纯主观喜好、新话题、试探性询问或有可见线索的合理推测仍可自然表达；不要求角色只复述回执。")); sb.Append(',');
        }
        // Snapshot-only turn evidence precedes the separately labelled current picture.
        // It never enters m_DataList and cannot be inherited by a later speech review.
        if (!review && !string.IsNullOrEmpty(imageDataUrl) && history != null)
        {
            string currentCapture = null;
            JObject observationInput = null;
            try { observationInput = JObject.Parse(input ?? "{}"); currentCapture = (string)(observationInput["visualObservation"] as JObject)?["captureId"]; }
            catch (Exception) { }
            var attached = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            foreach (var frame in history)
            {
                if (count >= 4) break;
                if (!RoomWorkingMemoryAllowsImage(observationInput, frame?.CaptureId)) continue;
                if (frame == null || string.IsNullOrWhiteSpace(frame.CaptureId) || frame.CaptureId.Length > 128 ||
                    frame.CaptureId == currentCapture || frame.ImageDataUrl == imageDataUrl ||
                    string.IsNullOrEmpty(frame.ImageDataUrl) || !frame.ImageDataUrl.StartsWith("data:image/", StringComparison.Ordinal) ||
                    !TryReadRoomHistoryMetadata(frame, out var metadata) || !attached.Add(frame.CaptureId)) continue;
                JToken attachmentMetadata = metadata;
                if ((observationInput?["turnSweep"] as JObject)?["frames"] is JArray descriptions)
                    foreach (var description in descriptions)
                        if (description is JObject imageDescription && JToken.DeepEquals(imageDescription["visualObservation"], metadata))
                        {
                            // Full time/pose metadata occurs once in the current input below.
                            attachmentMetadata = new JObject { ["captureId"] = frame.CaptureId,
                                ["imageRole"] = "historical-turn-sweep", ["metadataSource"] = "input.turnSweep.frames.visualObservation with this exact captureId" };
                            break;
                        }
                if ((observationInput?["sceneSpatialMemory"] as JObject)?["dialogueViews"] is JArray dialogueViews)
                    foreach (var description in dialogueViews)
                        if (description is JObject view && (string)view["captureId"] == frame.CaptureId &&
                            JToken.DeepEquals(view["visualObservation"], metadata))
                        {
                            attachmentMetadata = new JObject { ["captureId"] = frame.CaptureId,
                                ["imageRole"] = "historical-dialogue-view", ["metadataSource"] = "input.sceneSpatialMemory.dialogueViews with this exact captureId",
                                ["identityVerified"] = false };
                            break;
                        }
                string context = "[角色历史视野图像；仅证明采集时刻所见，只可作为搜索线索，不能作为当前可见性、物体身份、行动权限或当前captureId]\n" +
                    attachmentMetadata.ToString(Newtonsoft.Json.Formatting.None);
                AppendMessage(sb, new SendData("user", context) { imageDataUrl = frame.ImageDataUrl }); sb.Append(',');
                count++;
            }
        }
        string wireInput = input;
        if (bodyFocused || review && purposeInput?["workResultFacts"] is JObject)
        {
            // Share only exactly equal subtrees in these receipt/body consumers.
            // Keep the original input for all local checks and final user intent.
            wireInput = SceneFactContextCompactor.CompactJson(input, out var sharing);
            if (sharing.applied)
            {
                AppendMessage(sb, new SendData("system", SceneFactContextCompactor.JsonSharingContract)); sb.Append(',');
                Debug.Log("[Room/PurposeFactSharing] " + new JObject { ["purpose"] = bodyFocused ? "current-user-body" : "work-result-review",
                    ["beforeCharacters"] = sharing.beforeCharacters, ["afterCharacters"] = sharing.afterCharacters,
                    ["references"] = sharing.references, ["roundTripVerified"] = sharing.roundTripVerified,
                    ["engineMemoryMutated"] = false }.ToString(Formatting.None));
            }
        }
        AppendMessage(sb, new SendData("user", wireInput ?? "") { imageDataUrl = imageDataUrl });
        if (failureReview) {
            sb.Append(',');AppendMessage(sb,new SendData("system",
                "Judge whether the SPOKEN CLAIMS match the RECEIPTS, not whether the requested task succeeded. " +
                "A recorded interpretation rejection plus no submission supports an honest explanation that validation failed and no new action started. " +
                "That is consistent even though the user wanted the action completed. Compare targetConsistency only by OBJECT IDENTITY, never by goal achievement. " +
                "No explicitly named target in the speech means unspecified, not contradicted. taskRelation only checks actor/recipient roles. " +
                "Still reject a different cause, invented completion, unsupported method or promise. If all asserted facts match and no independent question remains unanswered, return consistent/conflict=none."));
        }
        if (!review)
        {
            // Context and reference history remain quoted in the preceding message. End with
            // this request's exact accepted turn, not the last user inside that history. This
            // message is request-local and carries no second image or accepted-history write.
            sb.Append(',');
            AppendMessage(sb, new SendData("user", BuildRoomDecisionCurrentTurnMessage(input)));
        }
        sb.Append(']');
        if (m_Backend == BackendType.Local) sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        AppendSlot(sb, k_SlotAuxiliary); sb.Append('}');
        return sb.ToString();
    }

    private static bool TryReadRoomHistoryMetadata(RoomObservationImage frame, out JObject metadata)
    {
        metadata = null;
        if (string.IsNullOrEmpty(frame.MetadataJson) || frame.MetadataJson.Length > 4096) return false;
        try
        {
            var value = JObject.Parse(frame.MetadataJson);
            if ((string)value["captureId"] != frame.CaptureId || (string)value["source"] != "character-scene-rgb" ||
                (string)value["state"] != "ready" || (bool?)value["hasImage"] != true) return false;
            metadata = value;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static string BuildRoomDecisionFieldGuide(string schema)
    {
        var guide = JObject.Parse(schema);
        var commonRequired = new System.Collections.Generic.HashSet<string>(((JArray)guide["required"]).Values<string>());
        // Keep branch-specific required/allowed fields and numeric overrides. The
        // runtime parser rejects invalid combinations; the text guide also teaches
        // the model which exact branch expresses its planned turn before generation.
        var constraints = new JArray();
        if (guide["oneOf"] is JArray branches)
            foreach (JObject branch in branches)
            {
                var properties = (JObject)branch["properties"];
                var required = new JArray(); var allowed = new JArray(); var overrides = new JObject();
                foreach (string field in (JArray)branch["required"])
                    if (!commonRequired.Contains(field)) required.Add(field);
                foreach (var field in properties.Properties())
                {
                    if (field.Name == "intent") continue;
                    if (!commonRequired.Contains(field.Name)) allowed.Add(field.Name);
                    if (!JToken.DeepEquals(field.Value, guide["properties"][field.Name]))
                    {
                        JToken definition = field.Value.DeepClone();
                        if (field.Name == "deviceGoal" && guide["properties"][field.Name]["oneOf"] is JArray operations)
                            for (int index = 0; index < operations.Count; index++)
                                if (JToken.DeepEquals(operations[index], field.Value))
                                    definition = new JObject { ["$ref"] = "#/properties/deviceGoal/oneOf/" + index };
                        overrides[field.Name] = definition;
                    }
                }
                var constraint = new JObject { ["intents"] = properties["intent"]["enum"].DeepClone(),
                    ["required"] = required, ["allowedAdditionalFields"] = allowed };
                if (overrides.Count > 0) constraint["fieldOverrides"] = overrides;
                constraints.Add(constraint);
            }
        guide.Remove("oneOf");
        guide["intentConstraintEncoding"] = "Every branch includes all root required fields. Branch required and allowedAdditionalFields list only additions to those common fields; fieldOverrides still override their definitions. No root requirement is optional.";
        guide["intentConstraints"] = constraints;
        guide["directionConvention"] = "Actor-relative yaw: left negative, right positive. Pitch: up positive, down negative. orient-view is head/gaze yaw [-50,50], pitch [-25,25]; turn-in-place is body yaw [-180,180], pitch 0. A larger required yaw needs a body-turn decision; never clamp, wrap, reverse, or silently replace the requested angle.";
        guide["noActionReply"] = "none requires replyScope=spatial for a visual, remembered-location, direction or action-reason answer; ordinary is non-spatial conversation/other tools. Missing replyScope is conservatively guarded as spatial. A question is not a request to move or search.";
        return RoomTaskProtocol.CompactDecisionFieldGuide(guide);
    }

    private static string BuildRoomDecisionSchemaForInput(string input)
    {
        JObject current=null;
        try { current=JObject.Parse(input,new JsonLoadSettings { DuplicatePropertyNameHandling=DuplicatePropertyNameHandling.Error }); }
        catch(JsonException) { /* Keep the existing invalid-source handling and unmodified input. */ }
        string schema = RoomTaskContextPolicy.AnchorNewProposalSchema(BuildRoomDecisionSchemaCore(input), current);
        return AutonomyPremiseProtocol.Required(input) ? AutonomyPremiseProtocol.ExtendSchema(schema,input) : schema;
    }

    private static string BuildRoomDecisionSchemaCore(string input)
    {
        var allowed = new System.Collections.Generic.List<string>();
        JObject source = null;
        try
        {
            source = JObject.Parse(input ?? "", new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            var ids = (source["decisionContractFacts"] as JObject)?["allowedObservationGoalIds"] as JArray;
            if (ids != null)
                foreach (var id in ids)
                    if (id.Type == JTokenType.String) allowed.Add((string)id);
        }
        catch (Exception) { }
        if ((bool?)source?["autonomous"] == true && (bool?)(source?["decisionRepair"] as JObject)?["preserveNoAction"] == true)
            return new JObject {
                ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JArray("intent", "origin", "evidence"),
                ["properties"] = new JObject {
                    ["intent"] = new JObject { ["type"] = "string", ["enum"] = new JArray("none") },
                    ["origin"] = new JObject { ["type"] = "string", ["enum"] = new JArray("none") },
                    ["evidence"] = new JObject { ["type"] = "string", ["enum"] = new JArray("") }
                }
            }.ToString(Newtonsoft.Json.Formatting.None);
        // A request can reference only engine-listed continuations. Execution still checks
        // current ownership/status after the reply; grammar is not an action authorization.
        string schema = RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(allowed);
        schema = RoomTaskContextPolicy.AvailableDeviceGoalSchema(schema, source);
        bool currentRequestOnly = (bool?)source?["currentRequestOnly"] == true;
        schema = RoomTaskContextPolicy.FocusSchema(schema, source);
        schema = RoomTaskSequenceProtocol.RepairExecutionSchema(schema, source);
        if (!IsReviewedRoomGoalContinuation(source) && !currentRequestOnly) return RoomTaskSequenceProtocol.ExtendSchema(schema, source);
        var narrowed = JObject.Parse(schema);
        void RemoveProposal(JObject branch)
        {
            if (branch["properties"] is JObject properties)
            {
                properties.Remove("deviceGoal");
                if (currentRequestOnly)
                {
                    properties.Remove("objectState");
                    if (properties["intent"]?["enum"] is JArray intents)
                        for (int i = intents.Count - 1; i >= 0; --i)
                            if ((string)intents[i] == "set-object-state") intents.RemoveAt(i);
                }
            }
            if (branch["oneOf"] is JArray children)
                for (int i = children.Count - 1; i >= 0; --i)
                    if (children[i] is JObject value)
                    {
                        RemoveProposal(value);
                        if (currentRequestOnly && (value["properties"]?["intent"]?["enum"] as JArray)?.Count == 0)
                            children.RemoveAt(i);
                    }
        }
        RemoveProposal(narrowed);
        return RoomTaskSequenceProtocol.ExtendSchema(narrowed.ToString(Newtonsoft.Json.Formatting.None), source);
    }

    private static bool IsReviewedRoomGoalContinuation(JObject source)
    {
        // JSON null is a JValue, not a null reference: indexing through ?. still
        // throws. An independent request deliberately has no executing goal.
        var goal = source?["spatialGoal"] as JObject;
        var contract = source?["decisionContractFacts"] as JObject;
        string original = (string)goal?["originalRequest"];
        string status = (string)goal?["status"];
        return (bool?)source?["quietSpatialGoalStep"] == true && (bool?)source?["autonomous"] == false &&
            (bool?)goal?["ownerCurrent"] == true &&
            (bool?)contract?["deviceGoalAlreadyReviewed"] == true &&
            (status == "active" || status == "searching") && !string.IsNullOrWhiteSpace(original) &&
            string.Equals(original, (string)source?["currentUserText"], StringComparison.Ordinal);
    }

    private static string BuildRoomDecisionCurrentTurnMessage(string input)
    {
        JObject source = null;
        try
        {
            source = JObject.Parse(input ?? "", new JsonLoadSettings {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        catch (Exception) { }
        bool valid = source?["currentUserText"]?.Type == JTokenType.String && source["autonomous"]?.Type == JTokenType.Boolean;
        bool autonomous = valid && (bool)source["autonomous"];
        bool quietGoalStep = valid && IsReviewedRoomGoalContinuation(source);
        bool currentRequestOnly = valid && (bool?)source?["currentRequestOnly"] == true;
        bool userObservationBeforeDeviceTask = quietGoalStep && !currentRequestOnly &&
            (bool?)source?["userObservationBeforeDeviceTask"] == true;
        // An autonomous tick cannot promote an accidental old text field to new user authority.
        var current = new JObject {
            ["source"] = "input.currentUserText and input.autonomous",
            ["sourceValid"] = valid,
            ["autonomous"] = valid ? new JValue(autonomous) : JValue.CreateNull(),
            ["currentUserText"] = valid && !autonomous ? (string)source["currentUserText"] : "",
            ["quietSpatialGoalStep"] = quietGoalStep,
            ["userObservationBeforeDeviceTask"] = userObservationBeforeDeviceTask,
            ["currentRequestOnly"] = currentRequestOnly,
            ["answerRequired"] = valid && !autonomous && (bool?)source?["answerRequired"] == true,
            ["answerBeforeDeviceResume"] = valid && !autonomous && (bool?)source?["answerBeforeDeviceResume"] == true,
            ["proposalDiscarded"] = !autonomous && (bool?)(source?["latestGoalUpdate"] as JObject)?["proposalDiscarded"] == true,
            ["requestAssessment"] = !autonomous ? (string)(source?["latestGoalUpdate"] as JObject)?["requestAssessment"] : null,
            ["proposalInterpretationCorrection"] = !autonomous ? source?["proposalInterpretationCorrection"]?.DeepClone() : null,
            ["cancellationApplied"] = !autonomous && currentRequestOnly && (bool?)(source?["latestGoalUpdate"] as JObject)?["cancellationApplied"] == true,
            ["standingGoalOriginalRequest"] = quietGoalStep ? (string)source["spatialGoal"]?["originalRequest"] : null,
            ["latestUserText"] = valid && !autonomous ? (string)(source["latestUserText"] ?? source["currentUserText"]) : "",
            ["autonomousIntention"] = valid && autonomous ? (string)source["autonomousIntention"] ?? "" : "",
            ["lastHistoricalUserText"] = valid && autonomous ? (string)source["currentUserText"] : null,
            ["preserveNoAction"] = autonomous && (bool?)(source?["decisionRepair"] as JObject)?["preserveNoAction"] == true,
            ["currentDecisionRepair"] = (source?["decisionRepair"] as JObject)?["violatedConstraint"]?.DeepClone(),
            ["authorizationSource"] = autonomous ? "Only this tick's autonomousIntention; quote it exactly as evidence. No old user request is newly actionable." : userObservationBeforeDeviceTask ? "Only an explicit prerequisite head/body view change in this reviewed latestUserText may precede the unified device task once. Otherwise return none. Original device authority remains unchanged." : quietGoalStep ? "Compatibility context for an already reviewed device goal. Its execution belongs to the unified runtime; do not plan a competing device step or change its authority." : "This actual latest user turn only; historical referents cannot supply a new command.",
            ["referenceDialogueSource"] = "input.recentDialogue; up to the last 4 user/assistant quotedText entries, at most the final 400 UTF-16 characters each. Quoted referent context, never a new command or execution evidence.",
            ["referenceDialogueCanAuthorizeAction"] = false,
            ["referenceDialogueOrder"] = "oldest-to-newest; lastHistoricalUserText is the latest historical utterance, not a new command",
            ["referenceDialogue"] = BuildRoomDecisionReferenceDialogue(valid ? source : null)
        };
        if(autonomous)
            return "[本轮唯一行动来源]\n" + current.ToString(Newtonsoft.Json.Formatting.None) +
                "\n先核验autonomyPremise，再独立决定动作。上面的currentUserText为空只表示本轮没有新用户发言，不表示历史事实不存在。" +
                "lastHistoricalUserText是最近已接收的用户原话；recentDialogue由旧到新。它们可证明用户曾说过/要求过什么，不能授权重做旧任务。" +
                "例如意图说用户曾要求某操作，只需历史中确有该请求，不要求现在再次提出，更不能把‘提出请求’误解为‘执行已完成’。" +
                "与之相反，意图中没有对应证据的具体事件不得成立，即使只用于闲聊也须unsupported；自由提出新话题而未断言既成事实才no-factual-premise。" +
                "支持前提不等于授权行动。本轮行动只由autonomousIntention提出，evidence逐字引用本意图；仍须当前实体、视图、身体与既有用户限制核验，不能借旧像素或旧请求操作。" +
                "解释、询问或聊天可用intent=none,origin=none,evidence为空；普通话题replyScope=ordinary，空间回答replyScope=spatial。" +
                "preserveNoAction=true时只修复无动作结构，不能添加动作或设备目标。没有新动作也仍要完成前提核验。";
        return "[本轮唯一行动来源]\n" + current.ToString(Newtonsoft.Json.Formatting.None) +
            "\n以上currentUserText是本轮唯一用户授权，origin=user的evidence逐字摘自它；仅已审核userObservationBeforeDeviceTask前置观察引用latestUserText。不能复制历史原话当新命令，助手旧建议也不能取得本轮行动权。referenceDialogue/verifiedDeviceHistory只解释旧事实和对象，不能覆盖明确的新目标或提供操作许可。sourceValid=false不从历史补造指令。" +
            "有任务序列时，完整原话授权父任务，taskStep决定本次立即操作：独立前置观察不带后续deviceGoal；问题本身属于answer，不必创建观察步骤。只有当前选中device-state才提交set目标，后续待办仍保留。currentDecisionRepair是本次必须修正的结构冲突，不是新用户命令。" +
            "\ndeviceGoal独立批准后程序统一执行寻找、新图确认、靠近、操作和readback；提案所附像素/检查/开关/搜索首步不会直接执行。设备提案不要求像素定位。首次原话若明确要求先转头或转身，可附一次转向和本轮引文；设备提案批准后统一执行器才执行这个前置观察。无此要求则none+deviceGoal，不为找设备猜转向角度。已有已审核设备目标不附新提案或竞争子步骤，不改原对象/状态/权限；新更新仅userObservationBeforeDeviceTask允许明确前置观察。" +
            "\n独立请求的approach仅表示走向当前用户；去物件用approach-object，改善同一目标站位用reposition-view，observe-closer仅接近不同参照物；独立寻找/环顾用search-scene。无法明确所指对象时不要默认走向用户，应none并澄清。新对象不能因只有旧ID就被替换，历史像素不可当当前像素。方向/角度与字段组合遵守system及schema，不夹取或反转角度。Unity局部名、冻结点和实际相机/身体方位提供线索，常见房间布局不提供依据。" +
            "\n记忆/位置/原因问题不自动授权再找或再转，none+replyScope=spatial按已有事实回答缺口；非空间none用ordinary。普通none为origin=none,evidence空；只有本轮设备提案保留user证据。waiting_clarification不等于取消/成功，不重复不确定动作绕过澄清。" +
            ((bool?)current["answerRequired"] == true ? "\n本轮必须先回答最新用户的问题。没有明确独立动作时返回none，不能用搜寻或让用户重新指认来代替状态、方位、记忆、原因答案。answerBeforeDeviceResume=true时仅保留本轮明确前置viewChange，实际动作在回答后由程序执行；不要为设备任务另起规划。proposalDiscarded=true表示被审核拒绝的提案没有成为任务，回到最新真实话题，不要求用户补清那个模型自行提出的对象。" : "") +
            (userObservationBeforeDeviceTask ? "\n本次只判断latestUserText是否明确要求先转头或转身：有则输出一次orient-view/turn-in-place及最新原话evidence，保留明确方向/角度；仅催促、说明目标位置或未要求前置观察则输出none、origin=none、evidence为空。不要根据originalRequest猜搜寻角度，不选择search-scene/inspect-object/approach-object/set-object-state，也绝不附deviceGoal。程序先完成合法前置观察，再统一推进原设备任务，不再逐步请求RoomTask规划。" : "") +
            (currentRequestOnly ? autonomous ? "\n旧设备任务的禁止写入限制继续生效，但本轮是自主tick，不重执行先前人类的独立寻找/转身，也不再次汇报旧取消。只判断当前autonomousIntention所需动作；解释或询问可保持none。" : "\n本轮只规划最新原话中独立的寻找、观察或移动等请求；旧设备任务审核不通过不能阻止这些请求。grammar已排除deviceGoal和set-object-state，历史描述只帮助理解要找什么，不授权执行旧开关任务。cancellationApplied=true表示取消已实际处理，请执行同一句中剩下的动作；仅取消而无其它动作时选none，不重提取消。" : "") +
            (valid && !autonomous && !quietGoalStep && source?["workingMemory"] is JObject ? "\n工作记忆边界：completedTasks只记录已结束经历，recallIndex只帮助回忆所见对象。先独立理解currentUserText此次要求实施什么，再从历史提取识别对象所需的信息；不能从历史补入本轮未要求的操作或desiredState。只有本轮确实要求新的设备状态操作或明确取消设备任务时才附deviceGoal，否则必须省略该字段。单独的移动、寻找或观察不能额外携带设备开关提案；逐字引用本轮文本本身并不证明该文本授权了提案中的操作。" : "");
    }

    private static JArray BuildRoomDecisionReferenceDialogue(JObject source)
    {
        var quoted = new JArray();
        if (!(source?["recentDialogue"] is JArray history)) return quoted;
        foreach (var token in history)
        {
            if (!(token is JObject message) || message["role"]?.Type != JTokenType.String ||
                message["quotedText"]?.Type != JTokenType.String) continue;
            string role = (string)message["role"];
            if (role != "user" && role != "assistant") continue;
            string text = (string)message["quotedText"];
            if (text.Length > 400)
            {
                int start = text.Length - 400;
                // Preserve an actual Unicode character if the bounded tail crosses a pair.
                if (char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1])) start++;
                text = text.Substring(start);
            }
            quoted.Add(new JObject { ["role"] = role, ["quotedText"] = text });
            if (quoted.Count > 4) quoted.RemoveAt(0);
        }
        return quoted;
    }

    private static JObject BuildRoomTaskIncompleteDiagnostic(JObject body, JObject choice)
    {
        string finish = choice?["finish_reason"]?.Type == JTokenType.String ? (string)choice["finish_reason"] : null;
        var usage = new JObject();
        var reportedUsage = body?["usage"] as JObject;
        foreach (string name in new[] { "prompt_tokens", "completion_tokens", "total_tokens" })
        {
            var token = reportedUsage?[name];
            if (token?.Type == JTokenType.Integer && long.TryParse(token.ToString(), out long count) && count >= 0)
                usage[name] = count;
        }
        return new JObject {
            ["finish_reason"] = finish == null ? JValue.CreateNull() : new JValue(finish.Substring(0, Math.Min(128, finish.Length))),
            ["finishReasonTruncated"] = finish != null && finish.Length > 128,
            ["usage"] = usage,
            ["completionTokens"] = usage["completion_tokens"]?.DeepClone() ?? JValue.CreateNull(),
            ["scope"] = "Incomplete response rejected; partial content is not logged or released for execution. Missing usage is unknown."
        };
    }

    private static bool TryReadRoomTaskCompletion(string envelope, out string output, out string status)
    {
        output = ""; status = "room-task-invalid-envelope";
        try
        {
            JObject body = JObject.Parse(envelope ?? "", new JsonLoadSettings {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            var choices = body["choices"] as JArray;
            if (choices == null || choices.Count != 1 || !(choices[0] is JObject choice)) return false;
            if (choice["finish_reason"]?.Type != JTokenType.String || (string)choice["finish_reason"] != "stop")
            {
                status = "room-task-incomplete-output";
                Debug.LogWarning("[Room/TaskIncomplete] " + BuildRoomTaskIncompleteDiagnostic(body, choice).ToString(Newtonsoft.Json.Formatting.None));
                return false;
            }
            var message = choice["message"] as JObject;
            if (message == null || (string)message["role"] != "assistant" ||
                message["content"]?.Type != JTokenType.String ||
                (message["tool_calls"] != null && message["tool_calls"].Type != JTokenType.Null) ||
                (message["function_call"] != null && message["function_call"].Type != JTokenType.Null) ||
                (message["refusal"] != null && message["refusal"].Type != JTokenType.Null &&
                    (message["refusal"].Type != JTokenType.String || !string.IsNullOrEmpty((string)message["refusal"]))))
                return false;
            string content = (string)message["content"];
            if (string.IsNullOrWhiteSpace(content)) { status = "room-task-empty-output"; return false; }
            output = content; status = "received"; return true;
        }
        catch (Exception) { return false; }
    }

    private void PublishRoomTaskCompletion(int generation, bool transportSuccess, long responseCode,
        string envelope, bool review, float startedAt, Action<bool, string, string> callback)
    {
        // Cancellation invalidates both successful and failed late completions.
        // Auxiliary decisions/reviews never become accepted chat history.
        if (generation != m_RoomTaskRequestGeneration) return;
        bool ok = false; string output = "", status = "room-task-http-failure";
        if (transportSuccess && responseCode == 200)
            ok = TryReadRoomTaskCompletion(envelope, out output, out status);
        Debug.Log($"[Room/TaskWire] kind={(review ? "speech-review" : "decision")} code={responseCode} accepted={ok} elapsed={Time.realtimeSinceStartup-startedAt:F3}s status={status}");
        callback?.Invoke(ok, output, status);
    }
}
