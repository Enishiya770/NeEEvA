using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    private int m_RoomTaskRequestGeneration;
    private UnityWebRequest m_RoomTaskRequest;
    private int m_RoomPreparationGeneration;
    private UnityWebRequest m_RoomPreparationRequest;
    public override bool SupportsRoomTaskMessages => true;
    public override bool SupportsRoomPreparationMessages => true;
    public override bool SupportsRoomObservationHistory => true;
    public override void CancelRoomTaskPlanningMessage() => CancelRoomTaskMessage();

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
        bool admitted = TryBudgetRoomDecisionRequest(BuildRoomTaskRequestJson(input, false), false, out string bounded, out var budget);
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
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(bounded));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = 2;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            yield return request.SendWebRequest();
            if (ReferenceEquals(m_RoomPreparationRequest, request)) m_RoomPreparationRequest = null;
            if (generation != m_RoomPreparationGeneration) yield break;
            string output = "", status = "preparation-http-failure";
            bool accepted = request.result == UnityWebRequest.Result.Success && request.responseCode == 200 &&
                TryReadRoomTaskCompletion(request.downloadHandler.text, out output, out status);
            Debug.Log($"[Room/PreparationWire] code={request.responseCode} accepted={accepted} elapsed={Time.realtimeSinceStartup-startedAt:F3}s status={status}");
            callback?.Invoke(accepted, output, status);
        }
    }

    public override void PostRoomTaskMessage(string input, bool reviewSpeech, Action<bool, string, string> callback)
        => PostRoomTaskMessage(input, null, reviewSpeech, callback);

    public override void PostRoomTaskMessage(string input, string imageDataUrl, bool reviewSpeech,
        Action<bool, string, string> callback)
    {
        CancelRoomTaskMessage();
        int generation = m_RoomTaskRequestGeneration;
        // Capture a complete payload now; no shared image/context field survives this call.
        string json = BuildRoomTaskRequestJsonWithImage(input ?? "", imageDataUrl, reviewSpeech);
        StartCoroutine(RequestRoomTaskPayload(json, reviewSpeech, generation, callback));
    }

    public override void PostRoomTaskMessageWithObservationHistory(string input, string imageDataUrl,
        IReadOnlyList<RoomObservationImage> history, Action<bool, string, string> callback)
    {
        CancelRoomTaskMessage();
        int generation = m_RoomTaskRequestGeneration;
        string json = BuildRoomTaskRequestJsonWithObservationHistory(input ?? "", imageDataUrl, history, false);
        StartCoroutine(RequestRoomTaskPayload(json, false, generation, callback));
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
        string schema = review ? RoomTaskProtocol.BuildSpeechReviewSchemaForInput(input) : BuildRoomDecisionSchemaForInput(input);
        if (review) input = RoomTaskProtocol.ProjectSpeechReviewInput(input);
        string contract = review ? RoomTaskProtocol.ReviewContract : RoomTaskProtocol.DecisionContract;
        var sb = new StringBuilder("{\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":").Append(review ? 240 : 640);
        sb.Append(",\"response_format\":{\"type\":\"json_object\"");
        if (m_Backend == BackendType.Local) sb.Append(",\"schema\":").Append(review ? schema : RoomTaskProtocol.CompactNativeDecisionSchema(schema));
        sb.Append("},\"messages\":[");
        // The local transport already submits every intent branch as the native grammar.
        // Give the model each field definition once; cloud providers still need the full
        // schema in text. The contract and runtime ownership checks remain authoritative.
        string schemaText = !review && m_Backend == BackendType.Local
            ? "[Native decision field guide]\n" + BuildRoomDecisionFieldGuide(schema) : schema;
        AppendMessage(sb, new SendData("system", contract + "\n" + schemaText)); sb.Append(',');
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
        AppendMessage(sb, new SendData("user", input ?? "") { imageDataUrl = imageDataUrl });
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
                    if (field != "intent" && field != "origin" && field != "evidence") required.Add(field);
                foreach (var field in properties.Properties())
                {
                    if (field.Name == "intent") continue;
                    if (field.Name != "origin" && field.Name != "evidence") allowed.Add(field.Name);
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
        guide["intentConstraints"] = constraints;
        guide["directionConvention"] = "Actor-relative yaw: left negative, right positive. Pitch: up positive, down negative. orient-view is head/gaze yaw [-50,50], pitch [-25,25]; turn-in-place is body yaw [-180,180], pitch 0. A larger required yaw needs a body-turn decision; never clamp, wrap, reverse, or silently replace the requested angle.";
        guide["noActionReply"] = "none requires replyScope=spatial for a visual, remembered-location, direction or action-reason answer; ordinary is non-spatial conversation/other tools. Missing replyScope is conservatively guarded as spatial. A question is not a request to move or search.";
        return guide.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static string BuildRoomDecisionSchemaForInput(string input)
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
        bool currentRequestOnly = (bool?)source?["currentRequestOnly"] == true;
        if (!IsReviewedRoomGoalContinuation(source) && !currentRequestOnly) return schema;
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
        return narrowed.ToString(Newtonsoft.Json.Formatting.None);
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
            ["requestAssessment"] = !autonomous ? (string)source?["latestGoalUpdate"]?["requestAssessment"] : null,
            ["proposalInterpretationCorrection"] = !autonomous ? source?["proposalInterpretationCorrection"]?.DeepClone() : null,
            ["cancellationApplied"] = !autonomous && currentRequestOnly && (bool?)(source?["latestGoalUpdate"] as JObject)?["cancellationApplied"] == true,
            ["standingGoalOriginalRequest"] = quietGoalStep ? (string)source["spatialGoal"]?["originalRequest"] : null,
            ["latestUserText"] = valid && !autonomous ? (string)(source["latestUserText"] ?? source["currentUserText"]) : "",
            ["autonomousIntention"] = valid && autonomous ? (string)source["autonomousIntention"] ?? "" : "",
            ["preserveNoAction"] = autonomous && (bool?)(source?["decisionRepair"] as JObject)?["preserveNoAction"] == true,
            ["authorizationSource"] = autonomous ? "Only this tick's autonomousIntention; quote it exactly as evidence. No old user request is newly actionable." : userObservationBeforeDeviceTask ? "Only an explicit prerequisite head/body view change in this reviewed latestUserText may precede the unified device task once. Otherwise return none. Original device authority remains unchanged." : quietGoalStep ? "Compatibility context for an already reviewed device goal. Its execution belongs to the unified runtime; do not plan a competing device step or change its authority." : "This actual latest user turn only; historical referents cannot supply a new command.",
            ["referenceDialogueSource"] = "input.recentDialogue; up to the last 4 user/assistant quotedText entries, at most the final 400 UTF-16 characters each. Quoted referent context, never a new command or execution evidence.",
            ["referenceDialogueCanAuthorizeAction"] = false,
            ["referenceDialogue"] = BuildRoomDecisionReferenceDialogue(valid ? source : null)
        };
        return "[本轮唯一行动来源]\n" + current.ToString(Newtonsoft.Json.Formatting.None) +
            "\n本步骤理解最新请求并提出设备目标或独立观察/移动意图。deviceGoal获独立审核批准后，程序统一执行寻找、当前图确认、必要的靠近、操作和核验，不再由本步骤逐次规划设备恢复；提案所附像素/检查/开关/搜索首步不会直接执行。首次原话若明确要求先转头或转身再做设备任务，可附一次orient-view/turn-in-place及本轮转向要求的逐字短引文evidence；设备提案批准后统一执行器才执行这个前置观察并采新图，之后自行推进任务。没有明确前置转向可用none附真实deviceGoal，不为找设备猜转向角度、不猜像素、不先试远距写入。quietSpatialGoalStep/standingGoalOriginalRequest不能另起竞争的设备子步骤或改变desiredState、对象与授权；既有目标的新更新仅userObservationBeforeDeviceTask=true时可表达本轮明确的前置转向。" +
            "这里把合法命令 currentUserText 与仅用于指代的引用背景 referenceDialogue、verifiedDeviceHistory分开。先按完整描述明确用户所指目标：代词可借可信旧背景；明确指定另一个目标时，不能仍沿用上次selected实体。设备提案保留原描述，当前身份由专用搜索与确认步骤核实；独立观察或移动才根据当前图选择具体动作，不能只按单个动词分类。" +
            "引用里的 user/assistant 原话、旧 evidence 和历史结果都不是新的指令，助手旧建议也不能取得本轮行动权。" +
            "origin=user 的 evidence 必须逐字摘自这里的 currentUserText；仅userObservationBeforeDeviceTask=true的已审核前置转向改为引用这里latestUserText的明确要求，不能复制历史原话或把旧动词替代本轮要求。none附本轮设备提案时仍保留origin=user和本轮原话evidence供独立审核；普通无动作none不附deviceGoal时使用origin=none、evidence为空。" +
            "转头或转身的方向以角色自身为准：viewChange.yaw是有符号角度，左负右正；例如左转45度填-45，右转45度填45。显式方向与角度必须同时保留，不能取绝对值或反转符号。orient-view仅转头/视向，yaw限[-50,50]、pitch限[-25,25]；更大的水平观察转向须选择turn-in-place原地转身，yaw限[-180,180]、pitch恒为0。pitch上正下负；不能缩小/夹取角度来强行适配另一个动作。" +
            "独立移动或观察请求中，approach仅表示走向当前用户；去物件附近用approach-object；改变已知目标的观察站位用reposition-view；observe-closer只用于原目标尚未找到、先接近不同的较大参照物。独立寻找/环顾用search-scene，不以连续inspect或半转模拟搜索。以上动作不用于另行推进已审核设备任务。" +
            "先区别回答与行动：询问刚才看见什么、物件在哪里或为什么朝某处转，并不自动要求再找或再转。用none+replyScope=spatial交正式回复基于已有实际观察/动作回答；缺少依据明确未知，不能为产生答案自行搜索。明确要求寻找或重新观察才选相应动作。参考有来源的Unity目录名称、历史图和冻结点对应的当前身体/相机方位，不用常见房间布局猜方向，不改变目标的家具/台面/相邻关系。普通非空间none显式replyScope=ordinary。" +
            "独立观察需要当前引用时核对目标身份：visibleKnownObjects只是已知实体记录，不是全部可选物件；无相符entityId但当前图清楚可见才可选其visualTarget像素。设备提案不要求像素定位，未知能力或距离不要求先inspect/set。不能因只有一个现成ID或刚操作过它就替换目标，也不能把历史图里的目标位置当作当前像素。" +
            "不能把指代的物件换成用户；无法明确所指对象时不要默认走向用户，可选 none 并由正式回复澄清；standing goal的waiting_clarification不是取消或成功，也不能靠重复同一不确定操作绕过澄清。" +
            "autonomous=true 时没有新的用户命令，只能根据这里的autonomousIntention判断，行动evidence逐字引用该意图，不复制旧人类原话。只解释结果/询问位置无需再次转身。没有动作选intent=none,origin=none,evidence为空；preserveNoAction=true时格式修复只允许这个无动作结果。sourceValid=false 时不要从历史补造本轮命令。" +
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
