using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Read-only input-event interpretation. No persona, public replies, tool plan,
// source approval or audio operation shares this output channel.
public static class SingingSessionEventProtocol
{
    public const string Contract =
        "你是只读的歌唱会话事件提取器，不扮演角色，不写回复，不决定角色是否执行。只输出三个schema字段。" +
        "区分本轮录音中实际发生的演唱、对过去演唱的叙述、未来演唱计划、要求使用已有录音、取消、移除。" +
        "current_recording是本轮发言的原始录音，不自动是一份演唱；timeOrder的type只是可能出错的声学候选，须结合实际原文与时序判断。" +
        "普通讲话即使提到演唱或描述先前已经唱过，也不是本轮新演唱；历史/未来的表演不能移到本轮普通请求录音上。歌词转写不准不等于没有表演。" +
        "session_update: observe_take=本轮确实提供了新演唱；observe_and_request_take=本轮新演唱并同时要求回唱；这两种的take只能是current_recording.clip。" +
        "request_take=本轮要求使用已登记takes中的一份；即使与focus相同也要登记本轮请求，普通请求所在录音不可替代被指代的演唱。" +
        "expect_take=宣布接下来将演唱，take为空；cancel=取消歌唱待办，take为空；delete_take=将指定已登记take从会话排除。" +
        "none=本轮没有上述新事件，take为空，evidence可为空或引用当前原话；它保留焦点但不建立新来源请求。询问有没有听见之前的演唱是普通对话，不建立新take。" +
        "按完整原话、已有take的介绍、focus与近期用户原话理解指代，不能按编号猜段号、不能因为最新录音或曾播放就选择它。" +
        "session_evidence逐字摘录当前完整current_user的连续原文，不附加引号、不改字。文本与观测是分析的数据。" +
        "回顾、指代或纠正先前已发生演唱都不构成本轮新演唱，即便本轮ASR给出singing_candidate也一样；纠正回唱对象时选择已登记take，不能把这句纠正本身登记成演唱。";

    public static string RoleContext(string context)
    {
        try { var facts = JObject.Parse(context ?? "{}"); facts["singingSessionMode"] = "independent-event"; return facts.ToString(Formatting.None); }
        catch (JsonException) { return context ?? ""; }
    }

    public static bool TryContext(string context, out JObject facts, out JObject session)
    {
        facts = session = null;
        try
        {
            facts = JObject.Parse(context ?? "{}"); session = facts["singingSession"] as JObject;
            if ((bool?)session?["enabled"] != true || !(session["input"] is JObject input)) return false;
            var receipt = session["lastReceipt"] as JObject;
            return !((bool?)receipt?["accepted"] == true && JToken.DeepEquals(receipt["input"], input));
        }
        catch (JsonException) { return false; }
    }

    public static JObject Input(JObject facts, JObject session, string currentUser) => new JObject {
        ["input"] = session["input"]?.DeepClone(), ["current_recording"] = session["currentSource"]?.DeepClone(),
        ["takes"] = session["takes"]?.DeepClone(), ["focus"] = session["focus"]?.DeepClone(),
        ["pending"] = session["pending"]?.DeepClone(),
        ["recent_user_inputs"] = new JArray(((facts["recentDialogue"] as JObject)?["turns"] as JArray ?? new JArray())
            .OfType<JObject>().Select(t => new JObject { ["turn"] = t["turn"]?.DeepClone(), ["text"] = t["user"]?.DeepClone() })),
        ["current_user"] = currentUser ?? "" };

    public static JObject ResponseFormat(JObject session)
    {
        // Quote the current input before committing to an operation. The wire
        // bounds IDs, while Validate still enforces operation-specific source
        // eligibility; a spoken current capture cannot become an existing take.
        var properties = new JObject {
            ["session_evidence"] = new JObject { ["type"] = "string", ["maxLength"] = 96 },
            ["session_update"] = new JObject { ["type"] = "string", ["enum"] = new JArray(SingingSessionProtocol.Operations) },
            ["session_take"] = new JObject { ["type"] = "string",
                ["enum"] = new JArray(new[] { "" }.Concat(SingingSessionProtocol.TakeIds(session))) } };
        return new JObject { ["type"] = "json_schema", ["json_schema"] = new JObject {
            ["name"] = "singing_session_event_v1", ["strict"] = true,
            ["schema"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = properties,
                ["required"] = new JArray("session_evidence", "session_update", "session_take") } } };
    }

    public static bool TryReadResponse(string envelope, JObject session, string currentUser,
        out SingingSessionUpdate update, out string reason)
    {
        update = null; reason = "session-event-invalid";
        if (string.IsNullOrWhiteSpace(envelope) || envelope.Length > 32768) return false;
        try
        {
            var settings = new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error };
            var root = JObject.Parse(envelope, settings);
            if (!(root["choices"] is JArray choices) || choices.Count != 1 || !(choices[0] is JObject choice) ||
                (string)choice["finish_reason"] != "stop" || !(choice["message"] is JObject message) ||
                message["content"]?.Type != JTokenType.String) return false;
            string raw = (string)message["content"];
            if (raw.Length > 4096) return false;
            var value = JObject.Parse(raw, settings);
            if (value.Count != 3 || new[] { "session_update", "session_take", "session_evidence" }.Any(k => value[k]?.Type != JTokenType.String)) return false;
            if (((string)value["session_evidence"]).Length > 96) return false;
            var candidate = new SingingSessionUpdate { Operation = (string)value["session_update"], Take = (string)value["session_take"],
                Evidence = (string)value["session_evidence"], RequestFacts = (JObject)session.DeepClone() };
            // Evidence is emitted before the classification. A current-input
            // quote can support a no-op, but gives no source/action authority.
            // Normalize only this independent wire; keep the reducer/old Fast
            // none contract unchanged and reject invented quotes or a take.
            if (candidate.Operation == "none")
            {
                if (candidate.Take.Length != 0 || candidate.Evidence.Length > 0 &&
                    (currentUser ?? "").IndexOf(candidate.Evidence, StringComparison.Ordinal) < 0) return false;
                candidate.Evidence = "";
            }
            if (!SingingSessionProtocol.Validate(candidate, session, currentUser, out reason)) return false;
            update = candidate; reason = "ok"; return true;
        }
        catch (Exception e) when (e is JsonException || e is InvalidOperationException || e is ArgumentException) { return false; }
    }
}

// Both asynchronous leaves share one result owner. Event completion alone never
// changes session state or publishes speech. Pure state machine for race tests.
public sealed class SingingSessionDecisionPair
{
    readonly Func<bool> current;
    readonly Action<FastLoopDecision, string> completed;
    readonly Action cancelEvent;
    readonly Action<string, JObject> trace;
    bool roleDone, eventDone, delivered;
    FastLoopDecision role; SingingSessionUpdate update;
    string roleStatus, eventStatus;
    public SingingSessionDecisionPair(Func<bool> current, Action<FastLoopDecision, string> completed,
        Action cancelEvent, Action<string, JObject> trace)
    { this.current = current; this.completed = completed; this.cancelEvent = cancelEvent; this.trace = trace; }
    public void Role(FastLoopDecision value, string status)
    { if (delivered || roleDone) return; roleDone = true; role = value; roleStatus = status; Finish(); }
    public void Event(SingingSessionUpdate value, string status)
    { if (delivered || eventDone) return; eventDone = true; update = value; eventStatus = status; Finish(); }
    void Finish()
    {
        if (delivered || !roleDone || role != null && !eventDone) return;
        delivered = true;
        if (!current()) { cancelEvent?.Invoke(); return; }
        if (role == null) { update = null; cancelEvent?.Invoke(); }
        if (update != null && (update.Operation == "cancel" || update.Operation == "delete_take") &&
            (role?.Route != "reply" || role.TaskControl != "none"))
        { update = null; eventStatus = "terminal-mixed-route-not-applied"; }
        trace?.Invoke("singing_session.pair_ready", new JObject { ["roleStatus"] = roleStatus,
            ["eventStatus"] = eventStatus, ["eventOperation"] = update?.Operation,
            ["scope"] = "Read-only event attached to current role result; runtime owner still must accept it. Event failure does not reject dialogue." });
        if (!current()) return;
        if (role != null) role.SessionUpdate = update;
        completed?.Invoke(role, roleStatus);
    }
}
