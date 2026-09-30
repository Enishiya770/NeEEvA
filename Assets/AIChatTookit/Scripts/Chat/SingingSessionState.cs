using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// A semantic session is separate from PCM ownership, playback permission and
// the lifetime of a model request. Only accepted final inputs can mutate it.
public sealed class SingingSessionUpdate
{
    public string Operation = "none", Take = "", Evidence = "";
    public JObject RequestFacts;
}

public sealed class SingingSessionState
{
    private readonly List<JObject> takes = new List<JObject>();
    private readonly List<JObject> events = new List<JObject>();
    private JObject pending, lastReceipt;
    private string focus = "";
    public long Revision { get; private set; }
    public bool HasState => takes.Count > 0 || pending != null;

    public JObject Facts(JObject input, JObject currentSource, bool enabled)
    {
        JObject focused = takes.FirstOrDefault(t => (string)t["take"] == focus);
        var facts = new JObject { ["version"] = 1, ["enabled"] = enabled,
            ["revision"] = Revision, ["input"] = input?.DeepClone(),
            ["currentSource"] = currentSource?.DeepClone(), ["focus"] = focused?.DeepClone(),
            ["pending"] = pending?.DeepClone(), ["takes"] = new JArray(takes.Select(t => t.DeepClone())),
            ["events"] = new JArray(events.Select(e => e.DeepClone())), ["lastReceipt"] = lastReceipt?.DeepClone(),
            ["scope"] = "Accepted semantic task/take state, not acoustic proof, a source verdict or playback permission. Current ordinary recordings do not automatically replace the focus. Deleted means excluded from this session; original PCM is retained." };
        if ((string)pending?["kind"] == "request_take" && pending["outcome"] == null && JToken.DeepEquals(pending["input"], input) &&
            focused != null && (string)focused["status"] != "deleted")
            facts["binding"] = new JObject { ["explicitRequest"] = true,
                ["take"] = focused["take"].DeepClone(), ["clip"] = focused["clip"].DeepClone(),
                ["capture"] = focused["capture"].DeepClone(), ["sessionRevision"] = Revision,
                ["input"] = input.DeepClone(), ["evidence"] = pending["evidence"].DeepClone() };
        return facts;
    }

    public bool Apply(SingingSessionUpdate update, JObject currentInput, string currentUser, out JObject receipt)
    {
        receipt = null;
        if (update == null || update.Operation == "none") return true;
        var facts = update.RequestFacts;
        if (facts == null || (long?)facts["revision"] != Revision || !JToken.DeepEquals(facts["input"], currentInput) ||
            !SingingSessionProtocol.Validate(update, facts, currentUser, out string failure))
        { receipt = Receipt(update, currentInput, false, "stale-or-invalid-session-proposal"); return false; }
        var existing = takes.FirstOrDefault(t => (string)t["take"] == update.Take);
        var source = facts["currentSource"] as JObject;
        if (update.Operation == "observe_take" || update.Operation == "observe_and_request_take" || update.Operation == "request_take")
        {
            if (existing == null)
            {
                if ((string)source?["clip"] != update.Take) { receipt = Receipt(update, currentInput, false, "take-not-retained"); return false; }
                existing = new JObject { ["take"] = update.Take, ["clip"] = update.Take,
                    ["capture"] = source["capture"].DeepClone(), ["status"] = "retained",
                    ["evidence"] = update.Evidence, ["observedInput"] = currentInput.DeepClone(),
                    ["transcript"] = source["transcript"]?.DeepClone(),
                    ["introduction"] = (string)pending?["kind"] == "expect_take" ? pending.DeepClone() : null };
                takes.Add(existing);
            }
            if ((string)existing["status"] == "deleted")
            { receipt = Receipt(update, currentInput, false, "take-deleted-from-session"); return false; }
            focus = update.Take;
            pending = update.Operation == "request_take" || update.Operation == "observe_and_request_take" ? new JObject { ["kind"] = "request_take", ["take"] = focus,
                ["input"] = currentInput.DeepClone(), ["evidence"] = update.Evidence } : null;
        }
        else if (update.Operation == "expect_take")
            pending = new JObject { ["kind"] = "expect_take", ["take"] = "", ["input"] = currentInput.DeepClone(), ["evidence"] = update.Evidence };
        else if (update.Operation == "cancel") pending = null;
        else if (update.Operation == "delete_take")
        {
            if (existing == null) { receipt = Receipt(update, currentInput, false, "take-not-in-session"); return false; }
            existing["status"] = "deleted";
            if (focus == update.Take) { focus = ""; pending = null; }
        }
        Revision++;
        receipt = Receipt(update, currentInput, true, update.Operation == "delete_take" ? "logically-deleted-original-retained" : "session-updated");
        lastReceipt = (JObject)receipt.DeepClone();
        events.Add((JObject)receipt.DeepClone());
        while (events.Count > 8) events.RemoveAt(0);
        return true;
    }

    private JObject Receipt(SingingSessionUpdate update, JObject input, bool accepted, string reason) => new JObject {
        ["operation"] = update.Operation, ["accepted"] = accepted, ["reason"] = reason,
        ["take"] = update.Take, ["evidence"] = update.Evidence, ["input"] = input?.DeepClone(), ["revision"] = Revision,
        ["audioExecuted"] = false, ["physicalDeletion"] = false };

    public bool Allows(string clip, JObject currentInput, out string reason)
    {
        reason = null;
        if (takes.Any(t => (string)t["clip"] == clip && (string)t["status"] == "deleted"))
        { reason = "singing-session-take-deleted"; return false; }
        if ((string)pending?["kind"] == "request_take" && JToken.DeepEquals(pending["input"], currentInput) &&
            (string)pending["take"] != clip)
        { reason = "singing-session-request-source-mismatch"; return false; }
        return true;
    }

    public void PlaybackOutcome(string clip, int requestTurn, bool completed)
    {
        if ((string)pending?["kind"] != "request_take" || (string)pending["take"] != clip ||
            (int?)pending["input"]?["turn"] != requestTurn || pending["outcome"] != null) return;
        pending["outcome"] = completed ? "completed" : "interrupted";
        Revision++;
    }

    // Independent rejection invalidates the source hypothesis, not the user's
    // goal. Keep its evidence available to bounded recovery and keep PCM/focus.
    public bool RejectBinding(JObject currentInput, string rejectedRefs, string reviewerReason)
    {
        if ((string)pending?["kind"] != "request_take" || pending["outcome"] != null ||
            !JToken.DeepEquals(pending["input"], currentInput) ||
            !string.Equals((rejectedRefs ?? "").Trim(), (string)pending["take"], StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(reviewerReason)) return false;
        pending["kind"] = "rejected_request";
        pending["rejection"] = reviewerReason.Length <= 320 ? reviewerReason : reviewerReason.Substring(0, 320);
        Revision++;
        var receipt = new JObject { ["operation"] = "independent_rejection", ["accepted"] = true,
            ["reason"] = pending["rejection"].DeepClone(), ["take"] = pending["take"].DeepClone(),
            ["input"] = currentInput.DeepClone(), ["revision"] = Revision, ["goalCancelled"] = false,
            ["audioExecuted"] = false, ["physicalDeletion"] = false };
        lastReceipt = (JObject)receipt.DeepClone(); events.Add(receipt);
        while (events.Count > 8) events.RemoveAt(0);
        return true;
    }
}

public static class SingingSessionProtocol
{
    public static readonly string[] Operations = { "none", "expect_take", "observe_take", "observe_and_request_take", "request_take", "cancel", "delete_take" };
    public const string Contract =
        "先填session_update/session_evidence/session_take登记本轮发生的歌唱会话事件，再决定route和角色如何回应。会话记忆不是角色执行任务，两者独立。" +
        "只依据完整current_user和已提供事件理解，不按声学标签、歌词识别好坏或最新录音自动换焦点。" +
        "none保留焦点，其余两字段为空。expect_take登记接下来将演唱的计划，take为空。" +
        "observe_take登记用户本轮已经提供的新演唱，只能引用currentSource.clip；听到了新演唱就必须登记，即使只想赞美或继续听也不能none。转写不准不表示尚未开唱。" +
        "observe_and_request_take表示同一已结束录音先有新演唱、又要求角色回唱，只能绑定currentSource.clip。" +
        "request_take仅从已经登记的takes选择本轮所指的录音，普通口头请求所在capture不是被要求演唱的素材，不能冒充新演唱。按当前原话、焦点及介绍事件选择；‘它/刚才重新唱的’结合新演唱事件，明确指旧段仍可选择旧take，不能因旧段播过/ready而换回旧段。" +
        "登记request不是执行许可；角色可拒绝/澄清用reply，决定采纳才用recording_singing继续。多份录音或尚不能唯一定位时保留session_update=none交recording_singing展开，不把整个目标缩为一个take。" +
        "cancel只取消歌唱待办，不停止移动或删除原录音；delete_take将指定take从当前歌唱任务排除，原录音仍留存。取消/删除只能配route=reply和task_control=none，其他混合目标交legacy且session_update=none。" +
        "所有非none操作的session_evidence必须逐字引用当前完整原话的连续片段，不附加引号；不从历史或模型补写。" +
        "用户宣布未来将演唱要登记expect_take，不因角色只reply等待而漏记。决定采纳移除某份候选则delete_take，不能只写删除承诺却none。" +
        "session状态变更也由程序受理后才成立；本轮没有新的演唱、计划、选材请求或取消/移除才选none。";

    public static IEnumerable<JObject> ConstrainBranch(JObject branch, JObject facts)
    {
        string current = (string)(facts?["currentSource"] as JObject)?["clip"];
        var existing = ExistingTakeIds(facts).ToArray();
        foreach (string operation in Operations)
        {
            bool observed = operation == "observe_take" || operation == "observe_and_request_take";
            bool selected = operation == "request_take" || operation == "delete_take";
            if (observed && string.IsNullOrEmpty(current) || selected && existing.Length == 0) continue;
            if ((operation == "cancel" || operation == "delete_take") &&
                ((string)branch["properties"]["route"]["enum"][0] != "reply" ||
                 (string)branch["properties"]["task_control"]["enum"][0] != "none")) continue;
            var copy = (JObject)branch.DeepClone();
            var properties = new JObject {
                ["session_update"] = new JObject { ["type"] = "string", ["enum"] = new JArray(operation) },
                ["session_evidence"] = operation == "none" ? new JObject { ["type"] = "string", ["enum"] = new JArray("") }
                    : new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 300 },
                ["session_take"] = new JObject { ["type"] = "string", ["enum"] = new JArray(observed ? new[] { current } : selected ? existing : new[] { "" }) }
            };
            foreach (var field in ((JObject)copy["properties"]).Properties()) properties[field.Name] = field.Value.DeepClone();
            copy["properties"] = properties;
            var required = (JArray)copy["required"];
            required.Add("session_update"); required.Add("session_take"); required.Add("session_evidence");
            yield return copy;
        }
    }

    public static IEnumerable<string> ExistingTakeIds(JObject facts) =>
        ((facts?["takes"] as JArray)?.OfType<JObject>().Where(t => (string)t["status"] != "deleted").Select(t => (string)t["take"]) ?? Enumerable.Empty<string>())
        .Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal);

    public static IEnumerable<string> TakeIds(JObject facts) =>
        ExistingTakeIds(facts)
        .Concat(new[] { (string)(facts?["currentSource"] as JObject)?["clip"] }).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal);

    public static bool Validate(SingingSessionUpdate value, JObject facts, string user, out string reason)
    {
        reason = "invalid-session-update";
        if (value == null || !Operations.Contains(value.Operation) || value.Take == null || value.Evidence == null) return false;
        if (value.Operation == "none") return value.Take == "" && value.Evidence == "";
        if (value.Evidence.Length < 1 || value.Evidence.Length > 300 || (user ?? "").IndexOf(value.Evidence, StringComparison.Ordinal) < 0)
        { reason = "session-evidence-not-current"; return false; }
        if (value.Operation == "expect_take" || value.Operation == "cancel") return value.Take == "";
        bool observed = value.Operation == "observe_take" || value.Operation == "observe_and_request_take";
        if (!observed && !ExistingTakeIds(facts).Contains(value.Take, StringComparer.Ordinal))
        { reason = "session-take-not-registered"; return false; }
        if (observed && (string.IsNullOrEmpty(value.Take) || value.Take != (string)(facts?["currentSource"] as JObject)?["clip"]))
        { reason = "observe-take-not-current-capture"; return false; }
        if (value.Operation == "delete_take" && !((facts?["takes"] as JArray)?.OfType<JObject>().Any(t => (string)t["take"] == value.Take) ?? false)) return false;
        reason = null; return true;
    }

    public static bool ValidateSources(JObject session, JObject currentInput, IEnumerable<string> clips, out string reason)
    {
        reason = null;
        var selected = (clips ?? Enumerable.Empty<string>()).ToArray();
        var deleted = (session?["takes"] as JArray)?.OfType<JObject>().Where(t => (string)t["status"] == "deleted")
            .Select(t => (string)t["clip"]) ?? Enumerable.Empty<string>();
        if (selected.Any(c => deleted.Contains(c, StringComparer.Ordinal)))
        { reason = "singing-session-take-deleted"; return false; }
        if (!(session?["binding"] is JObject binding) || (bool?)binding["explicitRequest"] != true) return true;
        var input = binding["input"] as JObject;
        if (currentInput == null || input == null || new[] { "turn", "capture", "clip" }.Any(k => !JToken.DeepEquals(currentInput[k], input[k])) ||
            (long?)binding["sessionRevision"] != (long?)session["revision"])
        { reason = "singing-session-binding-stale"; return false; }
        if (selected.Length != 1 || selected[0] != (string)binding["clip"])
        { reason = "singing-session-request-source-mismatch"; return false; }
        return true;
    }
}
