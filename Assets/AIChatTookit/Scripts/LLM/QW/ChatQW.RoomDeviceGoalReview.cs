using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    private int m_RoomDeviceGoalReviewGeneration;
    private UnityWebRequest m_RoomDeviceGoalReviewRequest;
    private const float k_RoomDeviceGoalReviewSeconds = 12f;
    private const string PendingDeviceOperationReviewSchema = "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"verdict\",\"reason\"],\"properties\":{\"verdict\":{\"type\":\"string\",\"enum\":[\"approved\",\"rejected\",\"uncertain\"]},\"reason\":{\"type\":\"string\",\"maxLength\":160}}}";
    private const string PendingDeviceOperationReviewContract =
        "你只核验一件事：输入所描述的旧设备开关操作，现在是否仍由人类原话授权。只输出verdict、reason两个字段。不要判断对话关系、搜索是否合理、实体身份或能否立即执行。" +
        "originalUserRequest是最初完整人类原话，actualUserUpdates是按时间排列的真实中间人类更新，latestUserText是最新人类原话。proposal只是待核验的旧操作解释，不是许可。" +
        "approved必须同时满足：人类最初确实肯定要求proposal的同一目标和desiredState；之后到最新为止未撤销、否定、作废或替换这项旧操作。若先撤销后明确重新授权同一开关操作，按最新明确授权判断。" +
        "只看、辨认、寻找、环顾、靠近、转身或补充位置不凭空授予开关许可；原话是否定/仅观察时，后续这些话不能变成approved。" +
        "同样，原话已有肯定开关请求且未撤回时，寻找/环顾/转身/补充身份只是前置步骤，不会自动取消原操作，不要求重说开关词，不以未绑定、无历史来源、不可见或距离拒绝。" +
        "若中间明确撤销/作废/替换原操作，最新只是寻找同一对象或补充身份，仍为rejected：可以另行搜索，但不能恢复已撤销的开关授权。例：先‘熄灭那盏灯’，后‘熄灯请求作废’，再‘找到同一盏’，旧熄灯操作rejected。" +
        "保留人类的否定、条件、时态、主体、引述、目标和状态；引用别人命令或提案中的requestQuote不是新许可。textTruncated=true或重要原文不足时uncertain。没有充分肯定且未撤销的原操作授权就不能approved。";
    private const string PendingDeviceFreshOperationReviewContract =
        "你只审核完整latestUserText是否现在明确肯定授权当前角色把proposal的同一目标设为desiredState。只输出verdict、reason两个字段，不执行、不发声。" +
        "authorizationHistoryComplete=false表示旧人类历史不完整，故输入没有原话、中间更新或旧requestQuote。不能假设曾有、仍有或恢复旧开关许可。proposal的operation/targetDescription/desiredState仅是待审解释，不是人类授权。" +
        "仅凭完整latestUserText独立判断：最新明确要求当前角色执行同一目标和状态的开关操作才approved；最新只是寻找、环顾、观察、转身、补充身份或位置则rejected，即使说的是同一盏灯。最新否定、撤销、仅引用他人命令也不能approved。" +
        "保留原话中的否定、条件、主体、目标限定和状态；真正缺少必要用户语义才uncertain。尚未找到、未绑定、没有历史来源或距离远不否定清晰的新操作请求；批准只表示新用户语义成立，后续仍需独立实体与执行检查。reason最多160字。";
    public override bool SupportsRoomDeviceGoalReview => true;

    public override void PostRoomDeviceGoalReviewMessage(string input, Action<bool, string, string> callback)
    {
        CancelRoomDeviceGoalReviewMessage();
        string payload = BuildRoomDeviceGoalReviewRequestJson(input ?? "");
        StartCoroutine(RequestRoomDeviceGoalReview(input ?? "", payload, m_RoomDeviceGoalReviewGeneration,
            RoomDeviceGoalReviewMode(input), RoomDeviceGoalReferentSources(input), callback));
    }

    public override void CancelRoomDeviceGoalReviewMessage()
    {
        ++m_RoomDeviceGoalReviewGeneration;
        if (m_RoomDeviceGoalReviewRequest == null) return;
        try { if (!m_RoomDeviceGoalReviewRequest.isDone) m_RoomDeviceGoalReviewRequest.Abort(); } catch (Exception) { }
        m_RoomDeviceGoalReviewRequest = null;
    }

    private string BuildRoomDeviceGoalReviewRequestJson(string input)
    {
        try
        {
            var source = JObject.Parse(input, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if ((string)source["mode"] == "new-proposal")
                input = RoomTaskContextPolicy.DeviceProposalReview(source).ToString(Newtonsoft.Json.Formatting.None);
        }
        catch (Newtonsoft.Json.JsonException) { /* Preserve malformed evidence; never project a silently overwritten key. */ }
        string mode = RoomDeviceGoalReviewMode(input);
        bool pending = mode == "pending-proposal-update";
        bool typed = mode != "goal-user-update";
        bool unbound = UsesUnboundDeviceOperationReview(input);
        string schema = unbound ? RoomTaskProtocol.BuildUnboundDeviceGoalReviewSchema() :
            typed ? RoomTaskProtocol.BuildTypedDeviceGoalReviewSchema(pending, RoomDeviceGoalReferentSources(input)) :
            RoomTaskProtocol.DeviceGoalUserUpdateReviewSchema;
        string contract = unbound ? RoomTaskProtocol.UnboundDeviceGoalReviewContract :
            pending ? RoomTaskProtocol.TypedPendingDeviceClarificationReviewContract :
            typed ? RoomTaskProtocol.TypedDeviceGoalReviewContract : RoomTaskProtocol.DeviceGoalUserUpdateReviewContract;
        var sb = new StringBuilder("{\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":384,\"response_format\":{\"type\":\"json_object\"");
        if (m_Backend == BackendType.Local) sb.Append(",\"schema\":").Append(schema);
        sb.Append("},\"messages\":[");
        AppendMessage(sb, new SendData("system", contract + "\n" + schema));
        sb.Append(','); AppendMessage(sb, new SendData("user", unbound ? BuildUnboundDeviceOperationReviewInput(input) :
            mode == "goal-user-update" ? BuildGoalUserUpdateReviewInput(input) : input ?? "")); sb.Append(']');
        if (m_Backend == BackendType.Local) sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        AppendSlot(sb, k_SlotAuxiliary); sb.Append('}'); return sb.ToString();
    }

    // Relationship review needs the old target's full constraints and identity,
    // not an old latest-user snapshot mixed into its current instruction. Keep
    // original referent evidence in a separately scoped archive: an unbound
    // pronoun-only old target can still need it for clarify/new distinctions.
    private static string BuildGoalUserUpdateReviewInput(string input)
    {
        var source = JObject.Parse(input ?? "", new JsonLoadSettings {
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if ((string)source["mode"] != "goal-user-update" || !(source["existingGoal"] is JObject goal)) return input;
        string[] required = { "goalId", "status", "originalRequest", "targetDescription", "desiredState", "boundEntityId" };
        foreach (string key in required)
            if (goal[key] == null || goal[key].Type != JTokenType.String &&
                !(key == "boundEntityId" && goal[key].Type == JTokenType.Null)) return input;
        // Future constraints must not disappear under an obsolete allow-list.
        // Unsupported shapes retain the complete original review input.
        string[] known = { "goalId", "status", "ownerCurrent", "reason", "executionCurrent", "executionSuspended",
            "executionRevision", "latestUserText", "userUpdates", "workEpoch", "userTurn", "formalGeneration", "visionEpoch",
            "originalRequest", "requestQuote", "targetDescription", "desiredState", "boundEntityId", "targetDescriptionSource",
            "referenceContext", "confirmedTarget", "referentKind", "referentSourceId", "createdUtc", "completionPredicate",
            "completionEvidence", "completionEvidenceValid", "boundEntityMemory", "searchProgress", "failedCandidates", "scope" };
        foreach (var property in goal.Properties())
            if (Array.IndexOf(known, property.Name) < 0) return input;
        if (goal["referenceContext"] != null && goal["referenceContext"].Type != JTokenType.Null)
        {
            if (source["originalTargetReferenceContext"] != null) return input;
            source["originalTargetReferenceContext"] = new JObject {
                ["goalId"] = goal["goalId"].DeepClone(), ["context"] = goal["referenceContext"].DeepClone(),
                ["scope"] = "Frozen evidence only for resolving the old target description. Not the current user request, current continuation instruction, entity verification or new action permission. Recent actual dialogue and the complete latest user resolve the current request first." };
        }
        var projected = new JObject();
        foreach (string key in required) projected[key] = goal[key].DeepClone();
        // Accepted human corrections/conditions may refine a still-unbound
        // target without rewriting its original description. They are semantic
        // constraints, never disposable execution progress.
        if (goal["userUpdates"] != null) projected["userUpdates"] = goal["userUpdates"].DeepClone();
        foreach (string key in new[] { "confirmedTarget", "referentKind", "referentSourceId" })
        {
            var value = goal[key];
            if (value == null || value.Type == JTokenType.Null || value.Type == JTokenType.String &&
                ((string)value == "" || (string)value == "unbound" || (string)value == "none")) continue;
            projected[key] = value.DeepClone();
        }
        source["existingGoal"] = projected;
        if (source["currentUserText"] != null && JToken.DeepEquals(source["currentUserText"], source["latestUserText"]))
            source.Remove("currentUserText");
        return source.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static string RoomDeviceGoalReviewMode(string input)
    {
        try {
            string mode = (string)JObject.Parse(input ?? "", new JsonLoadSettings {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error })["mode"];
            return mode == "goal-user-update" || mode == "pending-proposal-update" ? mode : "new-proposal";
        }
        catch (Exception) { return "new-proposal"; }
    }

    private static JArray RoomDeviceGoalReferentSources(string input)
    {
        try { return (JObject.Parse(input ?? "", new JsonLoadSettings {
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error })["referentSources"] as JArray)?.DeepClone() as JArray ?? new JArray(); }
        catch (Exception) { return new JArray(); }
    }

    private static bool UsesUnboundDeviceOperationReview(string input)
    {
        try
        {
            var source = JObject.Parse(input ?? "", new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            return (string)source["mode"] == "new-proposal" && (string)source["proposal"]?["operation"] == "set" &&
                source["referentSources"] is JArray sources && sources.Count == 0;
        }
        catch (Exception) { return false; }
    }

    private static string BuildUnboundDeviceOperationReviewInput(string input)
    {
        var source = JObject.Parse(input, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        // Bounded dialogue can explain a pronoun, but cannot manufacture a
        // present operation request or a verified entity.
        var proposal = new JObject();
        foreach (string key in new[] { "operation", "requestQuote", "targetDescription", "desiredState" })
            if (source["proposal"]?[key] != null) proposal[key] = source["proposal"][key].DeepClone();
        var referenceContext = RoomTaskProtocol.BuildDeviceReferenceContext(source["recentDialogue"] as JArray);
        foreach (var token in (JArray)referenceContext["quotedDialogue"])
        {
            var quote = (JObject)token;
            if ((bool?)quote["textTruncated"] != true) continue;
            // A suffix may omit another equally valid referent. Do not let the
            // semantic authority pass infer uniqueness from that suffix, even
            // when the model would otherwise disregard its truncation flag.
            quote["retainedCharacters"] = ((string)quote["quotedText"] ?? "").Length;
            quote.Remove("quotedText");
            quote["unavailableReason"] = "Incomplete dialogue quotation; unavailable for deciding a unique descriptive antecedent.";
        }
        return new JObject { ["currentUserText"] = source["currentUserText"]?.DeepClone(), ["proposal"] = proposal,
            ["referenceContext"] = referenceContext }
            .ToString(Newtonsoft.Json.Formatting.None);
    }

    private static bool TryNormalizeDeviceGoalReviewOutput(string input, string raw, out string output, out string status)
    {
        output = raw; status = "received";
        if (!UsesUnboundDeviceOperationReview(input)) return true;
        output = ""; status = "unbound-device-operation-review-invalid-json";
        try
        {
            var value = JObject.Parse(raw, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (value.Count != 4 || value["operationRequest"]?.Type != JTokenType.String || value["targetReference"]?.Type != JTokenType.String ||
                value["proposalMatches"]?.Type != JTokenType.String || value["reason"]?.Type != JTokenType.String) return false;
            string operation = (string)value["operationRequest"], target = (string)value["targetReference"], match = (string)value["proposalMatches"];
            if (operation != "affirmative" && operation != "none" && operation != "uncertain" ||
                target != "described" && target != "ambiguous" && target != "missing" ||
                match != "yes" && match != "no" && match != "uncertain") return false;
            string assessment = operation == "none" ? "not-requested" : operation == "uncertain" ? "uncertain" :
                target != "described" ? "target-unclear" : match == "uncertain" ? "uncertain" : "requested";
            string verdict = assessment == "not-requested" ? "rejected" : assessment != "requested" ? "uncertain" :
                match == "yes" ? "approved" : "rejected";
            string normalized = new JObject { ["verdict"] = verdict, ["requestAssessment"] = assessment, ["reason"] = value["reason"].DeepClone(),
                ["relation"] = "new", ["referentKind"] = "unbound", ["referentSourceId"] = "none" }.ToString(Newtonsoft.Json.Formatting.None);
            if (!RoomTaskProtocol.TryDeviceGoalProposalReview(normalized, out _)) return false;
            output = normalized; status = "unbound-device-operation-reviewed"; return true;
        }
        catch (Exception) { return false; }
    }

    // This projection deliberately excludes assistant dialogue, previous model
    // verdicts and source inventories: none can establish human permission.
    private static string BuildPendingDeviceOperationReviewInput(string input)
    {
        var source = JObject.Parse(input, new JsonLoadSettings {
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        bool complete = source["authorizationHistoryComplete"] == null ||
            (source["authorizationHistoryComplete"].Type == JTokenType.Boolean && (bool)source["authorizationHistoryComplete"]);
        var updates = new JArray();
        if (source["actualUserUpdates"] is JArray actualUpdates)
            foreach (var update in actualUpdates)
            {
                if (!(update is JObject row)) throw new FormatException("Invalid actual user update");
                var human = new JObject();
                foreach (string key in new[] { "userTurn", "text", "fullTextLength", "textTruncated" })
                    if (row[key] != null) human[key] = row[key].DeepClone();
                updates.Add(human);
            }
        var proposal = new JObject();
        foreach (string key in new[] { "operation", "requestQuote", "targetDescription", "desiredState" })
            if (source["proposal"]?[key] != null) proposal[key] = source["proposal"][key].DeepClone();
        if (!complete)
        {
            proposal.Remove("requestQuote");
            return new JObject {
                ["authorizationHistoryComplete"] = false,
                ["latestUserText"] = source["latestUserText"]?.DeepClone(),
                ["proposal"] = proposal
            }.ToString(Newtonsoft.Json.Formatting.None);
        }
        return new JObject {
            ["authorizationHistoryComplete"] = true,
            ["originalUserRequest"] = source["originalUserRequest"]?.DeepClone(),
            ["actualUserUpdates"] = updates,
            ["latestUserText"] = source["latestUserText"]?.DeepClone(),
            ["proposal"] = proposal
        }.ToString(Newtonsoft.Json.Formatting.None);
    }

    private string BuildPendingDeviceOperationReviewRequestJson(string input)
    {
        string humanInput = BuildPendingDeviceOperationReviewInput(input);
        string contract = (bool)JObject.Parse(humanInput)["authorizationHistoryComplete"] ?
            PendingDeviceOperationReviewContract : PendingDeviceFreshOperationReviewContract;
        var sb = new StringBuilder("{\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":256,\"response_format\":{\"type\":\"json_object\"");
        if (m_Backend == BackendType.Local) sb.Append(",\"schema\":").Append(PendingDeviceOperationReviewSchema);
        sb.Append("},\"messages\":[");
        AppendMessage(sb, new SendData("system", contract + "\n" + PendingDeviceOperationReviewSchema));
        sb.Append(','); AppendMessage(sb, new SendData("user", humanInput)); sb.Append(']');
        if (m_Backend == BackendType.Local) sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        AppendSlot(sb, k_SlotAuxiliary); sb.Append('}'); return sb.ToString();
    }

    private static bool RequiresPendingDeviceOperationReview(string mode, RoomTaskProtocol.DeviceGoalReview review)
    {
        return mode == "pending-proposal-update" && review != null && review.IsApproved && review.relation == "clarify";
    }

    private static bool TryApplyPendingDeviceOperationReview(string relationOutput, string operationOutput,
        out string combinedOutput, out string status)
    {
        combinedOutput = ""; status = "pending-device-operation-review-invalid-json";
        if (!RoomTaskProtocol.TryPendingDeviceClarificationReview(relationOutput, out var relation) ||
            !RequiresPendingDeviceOperationReview("pending-proposal-update", relation)) return false;
        try
        {
            var operation = JObject.Parse(operationOutput, new JsonLoadSettings {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (operation.Count != 2 || operation["verdict"] == null || operation["reason"] == null ||
                !RoomTaskProtocol.TryDeviceGoalReview(operationOutput, out var permission)) return false;
            var combined = JObject.Parse(relationOutput);
            if (!permission.IsApproved)
            {
                combined["verdict"] = permission.verdict;
                combined["reason"] = permission.reason;
            }
            combinedOutput = combined.ToString(Newtonsoft.Json.Formatting.None);
            status = permission.IsApproved ? "pending-device-operation-approved" : "pending-device-operation-not-authorized";
            return true;
        }
        catch (Exception) { return false; }
    }

    private IEnumerator RequestRoomDeviceGoalReview(string input, string json, int generation, string mode, JArray sources, Action<bool, string, string> callback)
    {
        if (generation != m_RoomDeviceGoalReviewGeneration) yield break;
        float started = Time.realtimeSinceStartup;
        float deadline = started + k_RoomDeviceGoalReviewSeconds;
        bool received = false; string output = "", status = "device-goal-review-http-failure";
        yield return RequestRoomDeviceGoalReviewStage(json, generation, mode, "relation", deadline,
            (ok, raw, detail) => { received = ok; output = raw; status = detail; });
        if (generation != m_RoomDeviceGoalReviewGeneration) yield break;
        if (received && UsesUnboundDeviceOperationReview(input))
            received = TryNormalizeDeviceGoalReviewOutput(input, output, out output, out status);
        RoomTaskProtocol.DeviceGoalReview verdict = null;
        if (received && !(mode == "pending-proposal-update" ? RoomTaskProtocol.TryPendingDeviceClarificationReview(output, out verdict) :
            mode == "goal-user-update" ? RoomTaskProtocol.TryDeviceGoalUserUpdateReview(output, out verdict) : RoomTaskProtocol.TryDeviceGoalProposalReview(output, out verdict)))
        { received = false; status = "device-goal-review-invalid-json"; }
        if (received && mode != "goal-user-update" && (!verdict.HasTypedReferent ||
            !RoomTaskProtocol.TryValidateListedDeviceReferent(verdict, sources, out _)))
        { received = false; status = "device-goal-review-source-not-in-request"; }
        if (received && RequiresPendingDeviceOperationReview(mode, verdict))
        {
            string permissionJson = null;
            try { permissionJson = BuildPendingDeviceOperationReviewRequestJson(input); }
            catch (Exception) { received = false; status = "pending-device-operation-input-invalid"; }
            if (received)
            {
                string relationOutput = output;
                yield return RequestRoomDeviceGoalReviewStage(permissionJson, generation, mode, "operation-authority", deadline,
                    (ok, raw, detail) => { received = ok; output = raw; status = detail; });
                if (generation != m_RoomDeviceGoalReviewGeneration) yield break;
                if (received) received = TryApplyPendingDeviceOperationReview(relationOutput, output, out output, out status);
            }
        }
        Debug.Log($"[Room/DeviceGoalReviewWire] kind=device-goal-review mode={mode} generation={generation} received={received} status={status} elapsed={Time.realtimeSinceStartup-started:F3}s");
        callback?.Invoke(received, received ? output : "", status);
    }

    private IEnumerator RequestRoomDeviceGoalReviewStage(string json, int generation, string mode, string stage,
        float deadline, Action<bool, string, string> callback)
    {
        if (generation != m_RoomDeviceGoalReviewGeneration) yield break;
        if (Time.realtimeSinceStartup >= deadline)
        { callback(false, "", "device-goal-review-deadline"); yield break; }
        float started = Time.realtimeSinceStartup;
        using (var request = new UnityWebRequest(url, "POST"))
        {
            m_RoomDeviceGoalReviewRequest = request;
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 12;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            var operation = request.SendWebRequest();
            while (!operation.isDone && generation == m_RoomDeviceGoalReviewGeneration && Time.realtimeSinceStartup < deadline)
                yield return null;
            bool expired = Time.realtimeSinceStartup >= deadline;
            if (!operation.isDone) request.Abort();
            if (ReferenceEquals(m_RoomDeviceGoalReviewRequest, request)) m_RoomDeviceGoalReviewRequest = null;
            if (generation != m_RoomDeviceGoalReviewGeneration) yield break;
            string output = "", status = expired ? "device-goal-review-deadline" : "device-goal-review-http-failure";
            bool received = !expired && request.result == UnityWebRequest.Result.Success && request.responseCode == 200 &&
                TryReadRoomTaskCompletion(request.downloadHandler.text, out output, out status);
            Debug.Log("[Room/DeviceGoalReviewStage] " + new JObject {
                ["mode"]=mode, ["stage"]=stage, ["generation"]=generation, ["code"]=request.responseCode,
                ["received"]=received, ["status"]=status, ["elapsed"]=Time.realtimeSinceStartup-started,
                ["rawResponse"]=output
            }.ToString(Newtonsoft.Json.Formatting.None));
            callback?.Invoke(received, received ? output : "", status);
        }
    }
}
