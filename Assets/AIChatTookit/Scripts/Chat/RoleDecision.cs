using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Semantic data only. No capture ids, clip ranges, generations or raw tool strings.</summary>
public sealed class RoleDecision
{
    public int Version { get; internal set; } = 1;
    public string Route { get; internal set; } = "reply";
    public string UserEvidence { get; internal set; } = "";
    public string Task { get; internal set; } = "none";
    public string Thought { get; internal set; }
    public string Speech { get; internal set; }
    public string Language { get; internal set; }
    public string Attention { get; internal set; }
    /// <summary>Whether this decision depends on current visual evidence. This
    /// is semantic context only; it is never a room/tool permission.</summary>
    public bool ObservationRequired { get; internal set; }
    public List<RoleDecisionIntent> Intents { get; internal set; } = new List<RoleDecisionIntent>();
    public double? NextThoughtSeconds { get; internal set; }

    public bool HasBoundAttentionEvidence(string currentUserText) =>
        Attention == "unchanged" || (!string.IsNullOrWhiteSpace(UserEvidence) &&
        (currentUserText ?? "").Contains(UserEvidence));

    /// <summary>Execution intent is a routing proposal only. It never grants
    /// authority to call a tool; the existing planner still owns that decision.</summary>
    public bool HasExecutionIntent =>
        !string.Equals(Attention, "unchanged", StringComparison.Ordinal) ||
        Intents.Count > 0;

    public bool HasRouteIntent =>
        Intents.Any(intent => intent.Type == "room.request" ||
                              intent.Type == "singing.request" ||
                              intent.Type == "memory.request" ||
                              intent.Type == "tool.request");
}

public sealed class RoleDecisionIntent
{
    public string Type { get; internal set; }
    public double? AfterSeconds { get; internal set; }
}

public static class DecisionCompiler
{
    public const int MaximumCharacters = 16384;
    public const string FastLaneThoughtGuidance =
        "快速首回复补充约束：thought 是角色心理活动，不是模型推理过程；只能写一句简短心理活动，最多 80 个字符，不要解释理由、分段或换行；没有必要时使用空字符串。";
    public const string Contract =
        "本次输出协议为 RoleDecision v1，替代其他提示中的 XML/标签输出规则，但不改变人设及事实。" +
        "只填写一个完整 JSON 对象，不要代码围栏或解释：" +
        "{\"version\":1,\"thought\":\"心理活动，可为空\",\"speech\":{\"text\":\"公开台词\",\"language\":\"ja\"},\"attention\":\"unchanged\",\"observation\":{\"required\":false},\"intents\":[]}." +
        "thought 是一次不出声的 speech segment，不能进入 TTS；speech.text 可为空，language 只能为 zh/ja/en。attention 只能为 unchanged/visual_on/visual_off。" +
        "intents 只能是空数组、{\"type\":\"thinking.schedule\",\"after_seconds\":5}，或一个路由意图 {\"type\":\"room.request\"|\"singing.request\"|\"memory.request\"|\"tool.request\"}；兼容旧别名 \"room.request/tools\"。" +
        "thinking.schedule 的范围是1到3600秒；路由意图只表示交回现有规划器，绝不表示已经执行。" +
        "不得填写标签、内部日志、素材版本、时间片或程序标识；不要声称已执行动作。需要工具/动作/唱歌/记忆/视觉核验时 speech.text 必须为空。没有要说的话则 text 为空。";

    // Route precedes generated speech. This is a semantic classification by the
    // model; the program has no singing/cancellation keyword classifier.
    public const string FastLaneContract =
        "填写 RoleDecision v3 JSON：version=3，route，task，attention，user_evidence，thought，speech:{text,language}。" +
        "结合recentDialogue中的最近实际对话理解最新完整用户原话，先按完整目标选择route能力，再填写该route的task、attention与台词；上游Actor的other只是宽泛分类，不约束本次专用能力选择。" +
        "task=none/approach/follow/stop_moving/other，是空间或通用工具子任务：无该类附加任务、一次走近用户、持续跟随用户、停止移动、其他独立工具任务。录音专用能力自身的检查、裁剪、回唱均由route=recording_singing表达，task=none并不表示没有录音处理工作。" +
        "task只表示本轮新增或明确续接的空间/通用执行请求；roomExecution、lastRoomActionResult里的历史跟随/移动事实不是新命令。单独问当前看见什么是task=none，不附带follow。" +
        "attention=unchanged/visual_on/visual_off。用户要求实际睁眼、闭眼、启用或停用视觉是状态操作，不能靠speech台词完成；user_evidence逐字引用当前对应原话。" +
        "按vision.eyesOpen判断：当前true且用户要求闭眼，必须visual_off/tools；当前false且用户要求睁眼，必须visual_on/tools；两者speech.text都为空。已处于要求的状态且不需要新观察时才可unchanged。" +
        "用户要求实际看当前环境或问当前能看到什么，且视觉关闭或本次没有图像附件时，必须visual_on/tools先取得新图，不能用reply只回答‘眼睛闭着，看不到’。讨论词义、引用、明确不要观察则不触发取图。" +
        "visual_on先应用视觉状态并采图，然后同轮重新决策；新图到达后的复决策使用unchanged。未收到图不能宣称看见黑暗或具体物体；无图只表示没有观测。" +
        "reply=只需自然对话或依据已附图片回答，task必须none且attention必须unchanged；不能执行、承诺或臆测已完成新的身体动作/跟随/工具任务。" +
        "‘跟着我/一直跟在我后面/ついてきて’是持续follow任务，‘走到我身边’是approach，‘别再跟着我’是stop_moving；这些都选tools，speech.text为空。" +
        "用户把目的地选择交给角色，例如‘你自己在房间随便逛逛/四处走走探索一下’，仍是实际空间行动请求：task=other、route=tools，由空间规划器建立探索目标并选择可见地面或观察方向；不能因用户未点名物件或坐标就改成reply、只描述想法或要求逐步指挥。" +
        "要求走向当前图中的一处地面也用other/tools，不冒充approach去用户身边。用户要求停止正在进行的探索或走动时stop_moving/tools。" +
        "是否行动取决于完整意图：‘解释随便逛逛这个说法，不要真的动’、仅讨论探索或明确现在不要开始走动是none/reply；引用中的探索命令不是当前命令。不能按‘逛/探索/走’等词的出现触发动作。" +
        "‘解释“跟着我”这句话，不要真的动’是none/reply；当前否定、纠正、引用和尾句限制优先，不能由动作词的出现决定执行。" +
        "承接上一句的同意与指代也有完整意图：例如你刚问要不要听你唱，用户回答‘对，想听歌/そうそう、歌を聞きたい’表示请求演唱，不是只聊歌。" +
        "‘刚才的歌听到了吗/歌聞いた？’本身只是问听见与否；根据上文区分听见、评价、请求回唱。最近回复中的语言约定也应延续，当前用户明确改语言时更新。" +
        "recording_singing=本轮完整目标只涉及现有录音的歌声/旋律回唱、跟唱、重唱以及为此核对内容、确定讲话/歌声边界、裁剪范围；task=none。先确认边界再回唱的全部步骤由同一专用能力处理，不能仅因需要检查/修剪或出现先后步骤而改成tools。tools=空间、其他独立工具、记忆或超出录音能力的处理。" +
        "若完整目标还要求走近、跟随、转身、观察物体、开关设备或其他独立工具行为，保留全部步骤并选tools/task=other；不能只截取回唱而吞掉空间目标。仅讨论错误原因、不采纳修剪或回唱时可reply。" +
        "用户请求照刚才录音回唱时，素材是否存在、可用或完整交给recording_singing检查；即使materialCount/candidateCount为0或currentRecording为空，仍选此route且speech.text为空，不能抢先reply声称没录上、没有素材或要求重录。" +
        "要求唱指定歌曲或即兴唱、尚无录音的演唱请求都属于task=other/route=tools；需由工具核对可用素材与能力。不能用reply许诺清唱、即兴唱或把普通朗读当作已唱歌。" +
        "本轮混合歌词与回唱请求且voice.currentRecording证实当前录音确已留存也属于recording_singing，不因ASR不准或语言混合当作没有素材。" +
        "cancel_work=用户明确结束测试/取消待办/要求停止；user_evidence逐字引用当前取消原话。" +
        "recording_singing、tools、任何非none任务或attention变化的speech.text必须为空，不能代替工具说会试试、会跟着走、不能唱或已经唱过。" +
        "能力存在但素材或服务情况未知时交给相应工具，不要凭缺少信息推断没有能力。" +
        "用户自己唱过、提到歌名、讨论唱歌或说不要唱，不等于请求你唱；引用中的命令不是当前命令。" +
        "例如请求重复刚才的旋律、先区分录音里的讲话和歌声再回唱属于recording_singing；只评价用户唱得怎样属于reply；找新歌或其他独立音频处理属于tools。" +
        "用户结束本次测试并告别时cancel_work；说暂时别唱但继续聊天也可取消旧待办，不能关闭视觉。" +
        "心理活动放thought，不进入TTS；不写工具参数推演。speech.language为zh/ja/en，默认遵循人设；" +
        "当前图片可供所有route看见，看到东西不等于要移动或执行工具。只输出JSON，禁止标签。";

    public static JObject FastLaneResponseFormat()
    {
        var schema = JObject.Parse(@"{
          'type':'object','additionalProperties':false,
          'properties':{
            'version':{'type':'integer','enum':[3]},
            'route':{'type':'string','enum':['reply','recording_singing','tools','cancel_work']},
            'task':{'type':'string','enum':['none','approach','follow','stop_moving','other']},
            'attention':{'type':'string','enum':['unchanged','visual_on','visual_off']},
            'user_evidence':{'type':'string','maxLength':300},
            'thought':{'type':'string','maxLength':80},
            'speech':{'type':'object','additionalProperties':false,
              'properties':{'text':{'type':'string','maxLength':400},'language':{'type':'string','enum':['zh','ja','en']}},
              'required':['text','language']}},
          'required':['version','task','attention','route','user_evidence','thought','speech']}");
        var branches = new JArray();
        void Branch(string[] routes, string[] tasks, string[] attention, bool publicSpeech)
        {
            var branch = (JObject)schema.DeepClone();
            branch["properties"]["route"]["enum"] = new JArray(routes);
            branch["properties"]["task"]["enum"] = new JArray(tasks);
            branch["properties"]["attention"]["enum"] = new JArray(attention);
            if (!publicSpeech) branch["properties"]["speech"]["properties"]["text"]["maxLength"] = 0;
            if (attention.All(a => a != "unchanged") || routes.All(r => r == "cancel_work"))
                branch["properties"]["user_evidence"]["minLength"] = 1;
            branches.Add(branch);
        }
        Branch(new[] { "reply" }, new[] { "none" }, new[] { "unchanged" }, true);
        Branch(new[] { "cancel_work" }, new[] { "none" }, new[] { "unchanged" }, true);
        Branch(new[] { "tools" }, new[] { "none", "approach", "follow", "stop_moving", "other" }, new[] { "unchanged" }, false);
        Branch(new[] { "tools" }, new[] { "none", "approach", "follow", "stop_moving", "other" }, new[] { "visual_on", "visual_off" }, false);
        Branch(new[] { "recording_singing" }, new[] { "none" }, new[] { "unchanged" }, false);
        return new JObject { ["type"] = "json_schema", ["json_schema"] = new JObject {
            ["name"] = "role_decision_v3", ["strict"] = true,
            ["schema"] = new JObject { ["type"] = "object", ["anyOf"] = branches } } };
    }

    private static bool TryParseFastLane(JObject value, out RoleDecision decision, out string error)
    {
        decision = null; error = "invalid-role-decision-fast-lane";
        int version = (int)value["version"];
        bool explicitTask = version == 3;
        if (!(explicitTask ? Fields(value, "version", "task", "attention", "route", "user_evidence", "thought", "speech") :
            Fields(value, "version", "route", "user_evidence", "thought", "speech")) ||
            value["route"]?.Type != JTokenType.String || value["user_evidence"]?.Type != JTokenType.String)
            return false;
        string route = (string)value["route"], evidence = (string)value["user_evidence"];
        string task = explicitTask && value["task"]?.Type == JTokenType.String ? (string)value["task"] : "none";
        string attention = explicitTask && value["attention"]?.Type == JTokenType.String ? (string)value["attention"] : "unchanged";
        if (explicitTask && (value["task"]?.Type != JTokenType.String || value["attention"]?.Type != JTokenType.String ||
            !new[] { "none", "approach", "follow", "stop_moving", "other" }.Contains(task) ||
            !new[] { "unchanged", "visual_on", "visual_off" }.Contains(attention) ||
            attention != "unchanged" && string.IsNullOrWhiteSpace(evidence))) return false;
        if (!new[] { "reply", "recording_singing", "tools", "cancel_work" }.Contains(route) || evidence.Length > 300 ||
            (route == "cancel_work" && string.IsNullOrWhiteSpace(evidence))) return false;
        // A semantic task/attention proposal cannot escape as a public promise,
        // even if a provider ignored the correlated schema and wrote reply.
        if (task != "none" || attention != "unchanged") route = "tools";
        var normalized = new JObject { ["version"] = 1, ["thought"] = value["thought"].DeepClone(),
            ["speech"] = value["speech"].DeepClone(), ["attention"] = attention, ["intents"] = new JArray() };
        // Never allow a route draft to escape as public speech. No tool has run.
        if (route == "recording_singing" || route == "tools")
        {
            if (!(normalized["speech"] is JObject speech) || !Fields(speech, "text", "language") ||
                speech["text"]?.Type != JTokenType.String || ((string)speech["text"]).Length > 4000) return false;
            speech["text"] = "";
            normalized["intents"] = new JArray(new JObject {
                ["type"] = route == "recording_singing" ? "singing.request" :
                    task == "approach" || task == "follow" || task == "stop_moving" ? "room.request" : "tool.request" });
        }
        if (!TryParse(normalized.ToString(Formatting.None), out decision, out error)) return false;
        decision.Version = version; decision.Route = route; decision.UserEvidence = evidence; decision.Task = task;
        return true;
    }

    public static bool TryCompile(string json, out string legacy, out string error)
    {
        legacy = ""; error = "invalid-role-decision";
        if (!TryParse(json, out var decision, out error)) return false;
        legacy = CompileLegacy(decision);
        if (string.IsNullOrEmpty(legacy)) { error = "compiled-role-decision-boundary-invalid"; return false; }
        error = null;
        return true;
    }

    public static bool TryParse(string json, out RoleDecision decision, out string error)
    {
        decision = null; error = "invalid-role-decision";
        try
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumCharacters) return false;
            JObject value;
            using (var reader = new JsonTextReader(new StringReader(json)) { MaxDepth = 8, DateParseHandling = DateParseHandling.None })
            {
                value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) return false;
            }
            if (value["version"]?.Type == JTokenType.Integer && ((int)value["version"] == 2 || (int)value["version"] == 3))
                return TryParseFastLane(value, out decision, out error);
            if ((!RoleFields(value, false) && !RoleFields(value, true)) ||
                value["version"].Type != JTokenType.Integer || (int)value["version"] != 1 ||
                !(value["speech"] is JObject speech) || !Fields(speech, "text", "language") ||
                speech["text"].Type != JTokenType.String || speech["language"].Type != JTokenType.String ||
                value["attention"].Type != JTokenType.String || !(value["intents"] is JArray intents) || intents.Count > 1) return false;
            var thoughtToken = value["thought"];
            if (thoughtToken != null && thoughtToken.Type != JTokenType.String) return false;
            string thought = thoughtToken == null ? "" : (string)thoughtToken;
            var observationToken = value["observation"];
            if (observationToken != null && !(observationToken is JObject)) return false;
            var observation = observationToken as JObject;
            if (observation != null && (!Fields(observation, "required") || observation["required"].Type != JTokenType.Boolean)) return false;
            decision = new RoleDecision { Thought = thought, Speech = (string)speech["text"], Language = (string)speech["language"], Attention = (string)value["attention"], ObservationRequired = observation != null && (bool)observation["required"] };
            if (SpeechText.NormalizeLanguage(decision.Language) != decision.Language ||
                !new[] { "unchanged", "visual_on", "visual_off" }.Contains(decision.Attention) ||
                decision.Thought.Length > 4000 || decision.Speech.Length > 4000 ||
                decision.Thought.IndexOfAny(new[] { '<', '>', '`' }) >= 0 ||
                decision.Speech.IndexOfAny(new[] { '<', '>', '`' }) >= 0) return false;
            foreach (var intent in intents)
            {
                if (!(intent is JObject item) || item["type"]?.Type != JTokenType.String) return false;
                string type = NormalizeIntentType((string)item["type"]);
                if (type == "thinking.schedule")
                {
                    if (!Fields(item, "type", "after_seconds") ||
                        (item["after_seconds"].Type != JTokenType.Integer && item["after_seconds"].Type != JTokenType.Float)) return false;
                    double seconds = (double)item["after_seconds"];
                    if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 1 || seconds > 3600) return false;
                    decision.NextThoughtSeconds = seconds;
                    decision.Intents.Add(new RoleDecisionIntent { Type = type, AfterSeconds = seconds });
                }
                else if (type == "room.request" || type == "singing.request" || type == "memory.request" || type == "tool.request")
                {
                    if (!Fields(item, "type")) return false;
                    decision.Intents.Add(new RoleDecisionIntent { Type = type });
                }
                else return false;
            }
            // Scheduling and attention may accompany a short public draft. A
            // route proposal must remain speech-free so the formal path cannot
            // mistake a planner handoff for an ordinary public reply.
            if (decision.HasRouteIntent && !string.IsNullOrWhiteSpace(decision.Speech))
                return false;
            error = null; return true;
        }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static string CompileLegacy(RoleDecision decision)
    {
        var output = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(decision.Thought))
            output.Append("<thought>").Append(decision.Thought).Append("</thought>");
        if (!string.IsNullOrWhiteSpace(decision.Speech))
            output.Append("<lang code=\"").Append(decision.Language).Append("\"/><speech mode=\"independent\"/><say>").Append(decision.Speech).Append("</say>");
        else output.Append("<silent/>");
        if (decision.Attention == "visual_on") output.Append("<look/>");
        if (decision.Attention == "visual_off") output.Append("<unlook/>");
        if (decision.NextThoughtSeconds.HasValue)
            output.Append("<next in=\"").Append(decision.NextThoughtSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture)).Append("s\"/>");
        // Preflight the entire compiled output before it can reach a public callback.
        var channels = RoleOutputChannels.Parse(output.ToString(), RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
        if (channels.HasInvalidPublicBoundary || channels.HasMalformedTool || channels.HasInvalidSpeechPhase ||
            channels.HasInvalidLanguage || channels.HasUndeclaredSpeech) return "";
        return output.ToString();
    }
    private static string NormalizeIntentType(string type)
    {
        if (string.Equals(type, "room.request/tools", StringComparison.OrdinalIgnoreCase))
            return "room.request";
        return type ?? "";
    }

    private static bool Fields(JObject value, params string[] names) =>
        value.Count == names.Length && names.All(name => value.Property(name) != null);

    private static bool RoleFields(JObject value, bool withThought)
    {
        string[] names = withThought
            ? new[] { "version", "thought", "speech", "attention", "observation", "intents" }
            : new[] { "version", "speech", "attention", "observation", "intents" };
        if (Fields(value, names)) return true;
        names = withThought
            ? new[] { "version", "thought", "speech", "attention", "intents" }
            : new[] { "version", "speech", "attention", "intents" };
        return Fields(value, names);
    }
}
