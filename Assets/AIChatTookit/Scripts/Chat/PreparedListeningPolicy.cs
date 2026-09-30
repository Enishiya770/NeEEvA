using System;
using Newtonsoft.Json.Linq;

/// <summary>Limits silent preparation; it never decides whether a recorder may close.</summary>
public sealed class PreparedListeningPolicy
{
    public const float StableSeconds = 0.20f;
    public const float FastRefreshInterval = 1.0f;
    public const float NormalRefreshInterval = 4.0f;
    public const int MaximumRequestsPerCapture = 3;
    public const int MinimumTranscriptChars = 2;
    private int capture, inputRevision, count;
    private PreparedTurnBoundaryEvidence lastSource;
    private float lastRequestedAt = -100f;
    public int Count => count;

    public bool NeedsLatest(PreparedTurnBoundaryEvidence source, int revision)
    {
        if (!source.Recording || source.CaptureSerial <= 0 || source.Singing || source.MelodyActive ||
            !source.AsrHealthy || source.AudioMs <= 0 || string.IsNullOrWhiteSpace(source.Transcript) ||
            source.Transcript.Trim().Length < MinimumTranscriptChars) return false;
        if (source.CaptureSerial != capture || revision != inputRevision) return true;
        return count < MaximumRequestsPerCapture && (count == 0 ||
            source.TextRevision != lastSource.TextRevision ||
            source.ActivityRevision != lastSource.ActivityRevision ||
            !string.Equals(source.Transcript, lastSource.Transcript, StringComparison.Ordinal));
    }

    public bool TryDelay(PreparedTurnBoundaryEvidence source, int revision, float now,
        float latestWireAt, float configuredInterval, out float delay, out string reason)
    {
        delay = 0f;
        if (!NeedsLatest(source, revision))
        {
            reason = source.CaptureSerial == capture && revision == inputRevision &&
                count >= MaximumRequestsPerCapture ? "capture-budget-exhausted" : "no-new-eligible-evidence";
            return false;
        }
        bool sameCapture = source.CaptureSerial == capture && revision == inputRevision;
        int sent = sameCapture ? count : 0;
        float interval = sent <= 1 ? FastRefreshInterval : Math.Max(NormalRefreshInterval, configuredInterval);
        float earliest = Math.Max(latestWireAt + FastRefreshInterval,
            sameCapture && sent > 0 ? lastRequestedAt + interval : now);
        delay = Math.Max(StableSeconds, earliest - now);
        reason = sent == 0 ? "first-stable-input" : sent == 1 ? "bounded-latest-refresh" : "normal-latest-refresh";
        return true;
    }

    public bool MarkSent(PreparedTurnBoundaryEvidence source, int revision, float now)
    {
        if (!NeedsLatest(source, revision)) return false;
        if (source.CaptureSerial != capture || revision != inputRevision)
        { capture = source.CaptureSerial; inputRevision = revision; count = 0; }
        count++; lastSource = source; lastRequestedAt = now;
        return true;
    }
}

/// <summary>Small schema shared by the prompt, transport, and validation tests.</summary>
public static class PreparedListeningProtocol
{
    public const string ConditionalScope =
        "这是语义轮次边界的条件准备，不是当前用户已经说完的事实声明。" +
        "评估 quiet_boundary 时请采用明确假设：这份原文之后没有修改、没有新声音，且ASR已覆盖成熟静音尾部。" +
        "这个假设只提供声学条件，不会补全缺失的语义。先核对末尾是否仍有未完成的纠正、否定或条件，" +
        "必要行为、对象或主句尚未明确时应继续倾听，不得借停顿、前半句或旧意图代填。完整性有合理歧义时选 continue/open。" +
        "只有本段当前意图已经表达完整，才可 closed 交给回应/行动规划；不必预测用户以后是否还会另起一句。" +
        "closed 不代表实际发声、工具执行或任务成功。continue 指继续倾听未完成的本段话，不是继续执行动作。" +
        "是否停止录音仍由程序核验最新真实证据；本请求只输出包含嵌套 quiet_boundary 的准备 JSON，禁止实际发声、操作或记忆写入。";

    public const string InnerReactionInstruction =
        "inner_reaction 是可直接展示给用户的角色心里话：以当前角色自己的第一人称口吻，" +
        "写一句不超过40字的当下兴趣、疑惑或感受；可留空，不必每次表态。它不是对用户的答复：" +
        "不招呼或指挥用户，不确认受理，不答应下一步，不邀请用户开始，不写行动计划或用户已经说完的结论。" +
        "reaction_kind 为 tentative(当下仍在听的感受)、feeling(完整内容引起的感受)、interpretation(对完整内容的理解)或 none。" +
        "intention.state 为 open/uncertain 时只能用 tentative 或 none；不得猜完后半句、答应请求、决定下一步、" +
        "声称用户已经说完或把疑问起头当成确认。interpretation 必须意图完整、无缺失参数且 quiet_boundary.closed。" +
        "interpretation 也只是心中理解，不是向用户说出接受、拒绝或执行承诺。它不是推理过程、工具参数或动作指令，" +
        "不得声称任务或动作已经完成，也不得宣告已经开始执行。" +
        "inner_reaction 不影响 quiet_boundary 判断，不表示用户已说完；新文字到达后旧反应立即撤回。";

    public const string IntentionInstruction =
        "intention 是当前原文支持的可撤销意图假设，不是已采纳请求或执行计划。" +
        "intention.state 与 quiet_boundary 是两项独立判断：state只问本句的言语行为是否表达清楚完整，" +
        "不问目标有没有执行完、是否还会有后续内容、角色是否应接话。完整的等待/保持/持续指令、完整条件指令、用户接下来要做事的宣告，都可以state=closed；" +
        "这时quiet_boundary仍可continue/open以继续听后续声音。state=open仅指当前表达缺必要成分或正在纠正却尚未说清；uncertain用于无法可靠判断。" +
        "domain分类的是当前表达所涉及的目标能力/主题，不是声音类型observed_mode，也不是是否准备马上执行。" +
        "conversation=闲聊、解释、问候或纯转述澄清；spatial=视觉观察、移动、姿态和物体交互；singing=演唱、回唱、歌声材料和边界处理；" +
        "mixed=多个不同领域；unknown=内容不足以知道目标领域。对某领域行为的停止、否定、延后仍属于那个领域；普通口语指令不能仅因是speech就填conversation。" +
        "目标尚未说完时保留已有领域/目标线索，不默认归为conversation；纯转述并明确不要求角色行动则描述转述本身。" +
        "goal 简述已知的具体请求/问题(最多80字)，保留谁执行或感知，不把用户自述和对角色的问题倒换主客体；target 只填已知对象(最多60字)，" +
        "missing 列出最多3个必要待补成分(每项32字)，evidence 摘录本快照中的最多3段精确原文(每段120字)。" +
        "missing只记录理解当前这句话所缺的语义，不列尚未发生的演示、未来内容、行动结果或工具待查状态。" +
        "evidence/segments.quote必须是原文里的连续子串，保持字形与标点，不翻译、不补写、不在内容两侧额外添加引号或书名号；JSON字符串自身的引号不属于摘录。" +
        "证据不得来自历史、声学描述或自己的回答。没有支持就 unknown/uncertain 并留空 goal/target/evidence。" +
        "未完成的纠正、否定、条件必须保留在 missing；不可凭旧任务补齐。closed 必须 goal明确、missing为空且有原文证据，" +
        "但仍只是待最终输入核验的假设。能力、素材、对象存在与执行成败都尚未核验。" +
        "segments 为可选语义分段候选列表，普通句默认[]；混合说话/歌词/指令时最多3段，按原文顺序取互不重叠的逐字quote(每段120字)，" +
        "kind=spoken_lead|lyrics_candidate|spoken_tail|instruction|uncertain。歌词候选不等于声学歌唱事实，不能据此裁音频或批准播放。";

    public static JObject CreateSchema() => new JObject
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["properties"] = new JObject
        {
            ["quiet_boundary"] = new JObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["action"] = Enum("continue", "take_turn", "ask_user", "complete"),
                    ["confidence"] = Probability(), ["mode"] = Enum("speech", "singing", "uncertain"),
                    ["source"] = Enum("user", "background", "uncertain"),
                    ["turn_state"] = Enum("open", "closed", "uncertain"),
                    ["reason"] = new JObject { ["type"] = "string", ["maxLength"] = 16 },
                },
                ["required"] = new JArray("action", "confidence", "mode", "source", "turn_state", "reason"),
            },
            ["observed_mode"] = Enum("speech", "singing", "uncertain"),
            ["mode_confidence"] = Probability(),
            ["preparation_version"] = new JObject { ["type"] = "integer", ["enum"] = new JArray(IncrementalListeningCandidate.ProtocolVersion) },
            ["intention"] = new JObject {
                ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JObject {
                    ["state"] = Enum("open", "closed", "uncertain"),
                    ["domain"] = Enum("conversation", "spatial", "singing", "mixed", "unknown"),
                    ["goal"] = BoundedText(80), ["target"] = BoundedText(60),
                    ["missing"] = TextArray(3, 32), ["evidence"] = TextArray(3, 120),
                    ["segments"] = new JObject { ["type"] = "array", ["maxItems"] = 3,
                        ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false,
                            ["properties"] = new JObject { ["kind"] = Enum("spoken_lead", "lyrics_candidate", "spoken_tail", "instruction", "uncertain"),
                                ["quote"] = BoundedText(120) }, ["required"] = new JArray("kind", "quote") } },
                },
                ["required"] = new JArray("state", "domain", "goal", "target", "missing", "evidence", "segments"),
            },
            ["reaction_kind"] = Enum("feeling", "tentative", "interpretation", "none"),
            ["inner_reaction"] = new JObject { ["type"] = "string", ["maxLength"] = 40 },
            ["draft"] = new JObject { ["type"] = "string", ["maxLength"] = 0 },
            ["language"] = Enum("ja", "zh", "en"), ["confidence"] = Probability(),
        },
        ["required"] = new JArray("quiet_boundary", "observed_mode", "mode_confidence", "preparation_version", "intention",
            "reaction_kind", "inner_reaction", "draft", "language", "confidence"),
    };

    public static string BuildPrompt(string transcript, string listeningMode = "speech", string acousticEvidence = "") =>
        "[内部增量倾听：同次请求准备语义边界与结构化意图；本请求不发声、不执行动作、不写历史]\n" +
        "截至采样时已识别的原文快照:\n" + transcript + "\n\n" +
        (listeningMode == "singing" ? "[声学观察：不是用户指令，也不是意图原文证据]\n" +
            acousticEvidence + "\n可能是歌唱或有旋律的说话，请独立判断模态，不能据此猜歌词、歌名或用户目标。\n" : "") +
        "先输出 quiet_boundary，采用假设：这份原文之后没有修改、没有新声音，ASR已覆盖成熟静音尾部。" +
        "判断分两步：先检查末尾有没有尚未补完的自我纠正、否定、条件或其他必要成分。" +
        "已经声明要改口，不等于修正后的意图已经说清；否定态度已经出现，也不等于否定的行为/对象已经明确。" +
        "若需替用户猜测后续才能知道修正后的请求，就 continue/open；不能用前半句的完整性或旧意图覆盖未完成的更正。" +
        "静音假设不会把残句变完整，完整性有合理歧义就继续听。" +
        "通过上述检查后，再判断这是否是一个独立完整的言语行为：问候、回答、请求本来可以很短。" +
        "语义清楚完整且没有未完成修正时，才可 closed；需要工具核验、请求停止动作或无需口头回答并不妨碍语义闭合。" +
        "这是对本段语义的判断，不是对目前是否录音或用户以后会不会另起一句的预测。" +
        "closed 时可选择 complete/take_turn，表示交给后续处理，不代表已经执行请求或必须立即发声。" +
        "continue 专指继续倾听用户尚未表达完整的本段话，不是让动作继续。" +
        "仍缺少必要内容、纠错/否定停在半句、换气前后语义未完、歌唱中途或完整性不确定时，应 continue/open。" +
        "完整的否定请求也可以闭合；不能根据单个词或标点判断。禁止 interrupt_user。" +
        "observed_mode 依据本轮真实输入，过去的跟唱约定不是此刻歌唱的证据。" +
        IntentionInstruction + InnerReactionInstruction +
        "draft 必须为空：本阶段准备意图而非未来答复，不生成承诺或预合成台词。" +
        "language在写完inner_reaction后按实际句子的语言填写，不是把默认人设语言照抄过来：中文句子zh、日文句子ja、英文句子en；" +
        "短引文的语言不覆盖整句语言。inner_reaction为空时才用人设默认语言。" +
        "先填写结构化意图，再据此写可空的心里话，最后判断实际语言；只输出紧凑 JSON，不要 Markdown 或控制标签，字段顺序如下：" +
        "{\"quiet_boundary\":{\"action\":\"continue|take_turn|ask_user|complete\",\"confidence\":0.0," +
        "\"mode\":\"speech|singing|uncertain\",\"source\":\"user|background|uncertain\"," +
        "\"turn_state\":\"open|closed|uncertain\",\"reason\":\"不超过16字\"}," +
        "\"observed_mode\":\"speech|singing|uncertain\",\"mode_confidence\":0.0," +
        "\"preparation_version\":1," +
        "\"intention\":{\"state\":\"open|closed|uncertain\",\"domain\":\"unknown|conversation|spatial|singing|mixed\"," +
        "\"goal\":\"\",\"target\":\"\",\"missing\":[],\"evidence\":[],\"segments\":[]}," +
        "\"reaction_kind\":\"tentative|feeling|interpretation|none\",\"inner_reaction\":\"\",\"draft\":\"\"," +
        "\"language\":\"ja|zh|en\",\"confidence\":0.0}";

    private static JObject Enum(params string[] values) => new JObject { ["type"] = "string", ["enum"] = new JArray(values) };
    private static JObject Probability() => new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 };
    private static JObject BoundedText(int length) => new JObject { ["type"] = "string", ["maxLength"] = length };
    private static JObject TextArray(int count, int length) => new JObject {
        ["type"] = "array", ["maxItems"] = count, ["items"] = BoundedText(length) };
}
