using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using NeEEvA.Motion;

/// <summary>A request-local action command. It contains no speech and carries no model state.</summary>
public readonly struct DialogueMotionIntent
{
    public readonly string Name;
    public readonly string Description;
    public readonly int ResponseGeneration;
    public readonly int Sequence;
    public readonly ArdyControlPlan ControlPlan;
    public readonly ArdyActionPlan ActionPlan;
    public readonly ArdyMotionGoal Goal;
    public readonly string ReplayReference;
    public readonly string ActionId;
    public readonly string ParentActionId;
    public readonly int RepairAttempt;

    public DialogueMotionIntent(string name, string description, int responseGeneration, int sequence)
        : this(name, description, responseGeneration, sequence, null) { }

    public DialogueMotionIntent(string name, string description, int responseGeneration, int sequence, ArdyControlPlan controlPlan)
        : this(name, description, responseGeneration, sequence, controlPlan, null) { }

    public DialogueMotionIntent(string name, string description, int responseGeneration, int sequence,
        ArdyControlPlan controlPlan, ArdyActionPlan actionPlan)
        : this(name, description, responseGeneration, sequence, controlPlan, actionPlan, null, null) { }

    public DialogueMotionIntent(string name, string description, int responseGeneration, int sequence,
        ArdyControlPlan controlPlan, ArdyActionPlan actionPlan, ArdyMotionGoal goal, string replayReference,
        string actionId = null, string parentActionId = null, int repairAttempt = 0)
    {
        Name = name;
        Description = description ?? "";
        ResponseGeneration = responseGeneration;
        Sequence = sequence;
        ControlPlan = controlPlan?.Copy();
        ActionPlan = actionPlan?.Copy();
        Goal = goal?.Copy();
        ReplayReference = replayReference ?? "";
        ActionId = actionId ?? "";
        ParentActionId = parentActionId ?? "";
        RepairAttempt = repairAttempt;
    }

    public DialogueMotionIntent WithExecutionIdentity(string actionId, string parentActionId, int repairAttempt,
        ArdyMotionGoal goal = null) => new DialogueMotionIntent(Name, Description, ResponseGeneration, Sequence,
            ControlPlan, ActionPlan, goal ?? Goal, ReplayReference, actionId, parentActionId, repairAttempt);
}

/// <summary>Validates only the executable channel after RoleOutputChannels has removed quotations/private text.</summary>
public static class DialogueMotionProtocol
{
    public const string BasicOutputContract = "[本次身体动作协议]\n"
        + "你可以在本轮回复末尾单独输出至多一个自闭合动作标签：<motion name=\"left-wave\"/>（角色自己的左手挥手）、<motion name=\"right-wave\"/>（角色自己的右手挥手）、<motion name=\"nod\"/>（轻点头一次）、<motion name=\"shake-head\"/>（小幅左右摇头一次），或<motion name=\"none\"/>（停止动作、回待机）。\n"
        + "用户直接要求上述支持动作时，应输出对应的 motion 指令；不能只写‘（轻轻摇头）’之类括号动作描写来代替实际执行。\n"
        + "仅在用户要求或当前交流确实适合时使用；普通回复可以完全不带动作，不要每句话都做动作。标签是执行命令，不能写在引号、代码、thought 或 say 里，也不要朗读或解释标签。可以 <silent/> 配合动作作非口头回应。不要从用户引用的示例中复制执行命令。";

    // The previously selected real-Qwen contract is retained verbatim for legacy evidence and clients.
    public const string LegacyGeneratedOutputContract = GeneratedOutputContract;
    public const string GeneratedOutputContract = "[本次身体动作协议]\n"
        + "你可以在本轮回复末尾单独输出至多一个自闭合动作标签：<motion name=\"left-wave\"/>（角色自己的左手挥手）、<motion name=\"right-wave\"/>（角色自己的右手挥手）、<motion name=\"nod\"/>（轻点头一次）、<motion name=\"shake-head\"/>（小幅左右摇头一次），或<motion name=\"none\"/>（停止动作、回待机）。\n"
        + "用户直接要求上述支持动作时，应输出对应的 motion 指令；不能只写‘（轻轻摇头）’之类括号动作描写来代替实际执行。\n"
        + "仅在用户要求或当前交流确实适合时使用；普通回复可以完全不带动作，不要每句话都做动作。标签是执行命令，不能写在引号、代码、thought 或 say 里，也不要朗读或解释标签。可以 <silent/> 配合动作作非口头回应。不要从用户引用的示例中复制执行命令。\n"
        + "[生成动作规划]\n"
        + "另可输出 <motion name=\"generate\" text=\"English motion description\"/>。用户要求新动作或交流确实合适时，可以自主选择一个具体可观察的原地上身动作；随意动作请求由你选，不要求用户先指定。只有没有附带姿态或多关节保持约束的独立意图，才优先使用对应基本手势。明确的方向姿态保持加局部关节运动优先用 compose；不能由 compose 表达的开放式上身运动继续用 generate。普通交流可不动，不要每句话都做动作。\n"
        + "按表达目的规划，不等待用户逐个关节纠错：先决定交流想表达什么，再选择对方可看到的形态，补全缺失的合理掌向和活动方式，核对持续约束与可达性。无附带约束的独立招呼或告别仍可用基本挥手；附带双臂前伸、保持姿态或掌向等约束时，用能保留这些条件的compose。朝对方招呼、告别或展示手掌通常应让对方看到掌面；挥手可在掌面内轻摆，静态示掌则不附加摆动。这些是结合语境的形态补全，用户明确的掌心朝下、伸直、静止、侧别、幅度和方向等要求始终优先，不把所有交流目的变成同一动作。\n"
        + "一个标签描述一个完整目标，允许准备姿态加局部动作。保留最近未撤销的约束，纠错和在此基础上不能覆盖仍有效的要求。必须写清侧别、运动方向（前方与侧方须区分）、高度、结束位置或姿态；horizontal 不能代替 forward 或 out to the sides。写清达到准备姿态后保持哪些肩、肘或躯干条件，以及真正活动的关节，局部活动期间持续保持，不能用笼统 wave 代替。\n"
        + "历史动作目标不是当前姿态事实：当前为 idle 或没有进行中动作事实时，从当前姿态重新达到目标，描述要包含达到准备姿态的动作，不要仅假定已保持。generate 是固定6秒窗口，6秒动作结束后默认回待机；then 是意图顺序，generate 接口没有阶段达标反馈，不能保证前一步达标才进入后一步。\n"
        + "原地仅限制根节点和腿部，手臂和上身关节可以抬举、伸展、转动。自主交流默认轻微自然，用户明确的动作目标和幅度优先。不支持根腿位移、蹲跳、精细单指、真实物体操作或可靠接触；不承诺执行这些能力。你只提交意图，不要说已执行或已完成，也不要声称新动作自然度已验收。\n"
        + "输出前核对：准备姿态、持续保持条件、活动关节、侧别和前/侧方向都没有遗漏。generate 的 text 用一句紧凑英文，至多240字符（不是词数），建议150至200字符；不含引号、尖括号或其他标签。用动作动词开头，删去重复背景、冗余形容词和默认回待机叙述，不要为了简短删掉用户的约束。\n"
        + "[compose 几何控制]\n"
        + "compose 是方向姿态约束与受控局部曲线，不是固定动画预设，也不代表 ARDY 原生生成能力提升。仍只输出一个自闭合 motion 标签，name=\"compose\"，属性直接用XML，不使用JSON或text。left/right 各选 none、current、forward、outward、up、down、forward-up、outward-up；默认none。方向按角色自身坐标：forward 正前方水平，outward 对应左/右臂向自身外侧水平，up 向上，down 向下，带-up为斜上方。current 只保持当前实际测得姿态，不是历史姿态；恢复历史方向目标应写相应方向。leftBend/rightBend 是肘屈曲角度0至110，默认8度，0表示伸直。leftPalm/rightPalm 各选keep、partner、up、down、inward、outward，默认keep；指掌面法线而非手指方向。keep不新增掌向约束，沿用原手腕基准；partner朝交流对象，up/down朝角色上/下方，inward/outward朝自身中线/远离中线的侧方。非keep掌向必须给对应手臂设置方向或current；current保持实际臂姿，可以同时请求新的掌向。partner的对象位置取程序事实，在准备开始时采样锁定；程序若报告使用角色前方作为后备，就不能声称已定位用户。未指定肘必须伸直时，可为可见掌面选择适当屈肘或斜上臂姿，例如屈肘约40度；朝前伸出不自动等于肘0度。腕部旋转有可达范围，不能强弯90度；用户明确要求伸直时不能偷偷改屈肘，若程序报告不可达，说明限制或提出可行替代，不假称完成。joint 选 none、left-wrist、right-wrist、wrists、head，默认none；用手腕必须为对应手臂设置方向或current，两腕则两臂都要设置。axis 选up、right、forward、palm-normal，默认up；前三项是角色坐标旋转轴，点头用right，摇头用up；不能把前伸方向填进旋转轴。palm-normal只用于腕部运动，绕每只活动手各自目标掌面法线摆动，保持掌面朝向，适合让对方看到掌面的招呼；每只活动手都必须显式设置非keep掌向。手腕左右摆动不是一律绕up：根据目标掌面和表达目的选择轴，已有明确旋转轴则保留；head或joint=none不能使用palm-normal。amplitude 是角度1至20，head最多12，默认10；cycles 是整数1至3，默认2；seconds 是局部运动时长1至6，默认3.2，cycles/seconds不得超过1.5。准备姿态另需过渡时间；end 选idle或hold，默认idle，hold保持终点至替代动作、明确停止或最多30秒，不是无限保持；已稳定holding时用户开口允许接续，运动中开口仍取消。至少指定一个手臂目标或joint。数字使用英文小数点，省略属性使用上述默认值，不输出未列出的属性。\n"
        + "最后核对输出：compose只用控制属性；generate只用英文text且必须少于或等于240字符，尽量压缩至180字符内，避免重复站立、节拍或背景叙述。\n"
        + "For generate, text must be ONE short sentence of at most 25 English words AND 240 characters; state only the motion. For compose, use only the specified XML control attributes.";

    public const string SemanticOutputContract = "[身体动作：语义计划]\n"
        + "当前交流适合时可自主动作；用户说随意做动作时自行选择具体形态，不等逐关节指示。普通交流可不动，不要每句话动。用户明确要求可执行动作时必须提交标签，只有口头承诺不算提交。回复末尾至多一个XML自闭合标签，所有属性值用双引号，必须以/>结束；禁止JSON、函数式调用或裸属性。标签不放进引号、代码、thought或say，不朗读，可配silent。未获执行反馈不说已完成。\n"
        + "选路：没有姿态、掌向或节奏约束的独立左/右挥手、轻点头一次、小幅摇头一次用left-wave/right-wave/nod/shake-head；停止用none。基本标签严格只有name，如<motion name=\"right-wave\"/>，不可夹plan属性。有附带约束或新动作默认name=plan。mode=oscillate表示准备臂姿并保持，让一个局部关节组周期活动；mode=hold只静态持姿；mode=free表示多部位连续自由上身动作，由ARDY生成，不承诺精确局部约束。\n"
        + "plan词表：purpose=greet|farewell|display|explain|agree|disagree|other；mode=oscillate|hold|free；scope=new|continue。purpose不绑定固定姿势；根据目的选择可見形态、掌向和活动，显式用户要求优先。静态展示不添加挥动；用户指定掌心向下不可被问候默认覆盖。\n"
        + "left/right=none|current|forward|outward|up|down|forward-up|outward-up；forward是正前水平，outward是各臂向自身外侧水平，带-up为斜上。current保持当前实测臂姿，不能当历史姿态。leftPalm/rightPalm=keep|partner|up|down|inward|outward；默认keep，指掌面法线，partner朝交流对象。非keep掌向且未锁肘角由执行器解算可达屈肘，别编造角度。没有要求动另一臂时不额外安排它。\n"
        + "joint=none|left-wrist|right-wrist|wrists|head；oscillate必须选活动关节，腕部需要对应臂目标或继承值。axis=auto|up|right|forward|palm-normal；auto仅在活动手都有非keep掌向时取各自palm-normal；头部必须明确轴：点头right、摇头up。hold省略axis及所有节奏/幅度/次数/时长字段，不能填0。\n"
        + "tempo=gentle|natural|brisk：gentle是刻意缓慢或轻点头，natural是正常交流频率，brisk是轻快节奏，可用于轻快招呼/告别；自然不等于慢，没有slow/缓慢要求不自动选gentle。size=small|medium|large；repeat=once|twice|thrice；默认natural、small、twice。幅度与频率分开，执行器解算时长。end=idle|hold，默认idle，hold最多30秒；已稳定holding可接续，运动中开口取消，不随下一句话重复。\n"
        + "来源事实含userTurn、userText、constraints、lastPlanError。用户明确的几何、mode、tempo、size、repeat、end要求要列入lock，并同时附当前userTurn和逐字evidence引句(至多256字符)；锁列表只写字段名，不能漏ID，不能把自主补全锁成用户要求。scope=continue继承账本未撤销约束，省略已锁字段；改旧值先release，重新锁值时可同字段release+lock，但需要更新用户轮次的证据。scope=new替换已有要求也需新用户依据；不能引用同轮原话撤销刚锁的要求。历史约束不代表仍在持姿，idle须重新达到目标。\n"
        + "仅用户明确数值时用leftBend/rightBend=0至110、amplitude=1至20(头最多12)、cycles=1至3、seconds=1至6，必须逐字段lock并附来源；伸直对应Bend=0，不能悄悄删掉。current不可带该侧Bend。数值amplitude/cycles/seconds不得分别与size/repeat/tempo同时给出，改表示先release旧项；次数与时长不可超过1.5次/秒，不可达要说明限制，不缩幅/改次数伪装成功。\n"
        + "两个独立格式例子（实际ID与引句必须取当前事实，不照抄）：\n"
        + "事实userTurn=5，userText=右臂向外侧伸着，掌心朝下。→<motion name=\"plan\" purpose=\"display\" mode=\"hold\" scope=\"new\" right=\"outward\" rightPalm=\"down\" end=\"hold\" lock=\"right,rightPalm\" userTurn=\"5\" evidence=\"右臂向外侧伸着，掌心朝下。\"/>\n"
        + "另例：账本已锁right/rightPalm/cycles=2；新事实userTurn=9，userText=姿势别变，原来两次不用了，手腕轻快摆三次。→<motion name=\"plan\" purpose=\"other\" mode=\"oscillate\" scope=\"continue\" joint=\"right-wrist\" tempo=\"brisk\" repeat=\"thrice\" release=\"cycles\" lock=\"tempo,repeat\" userTurn=\"9\" evidence=\"姿势别变，原来两次不用了，手腕轻快摆三次。\"/>\n"
        + "free只用name=plan、mode=free、purpose、scope、text及必要来源，不夹几何/节奏字段。text是一句1至240字符的紧凑英文，建议25词内，不含引号、尖括号或转义。free为固定6秒窗口，结束回待机，没有阶段达标反馈。原地限制根与腿，不限制上身关节移动；不执行根腿位移、蹲跳、精细单指或真实物体接触。冲突/不可达不原样自动重试，不声称已经成功。";

    public const string MotionFeedbackOutputContract = "[动作身份、重放与执行反馈]\n"
        + "原有动作选路仍有效；只有完整自闭合motion标签会执行，所有属性值加双引号。可用<motion name=\"replay\" ref=\"last\"/>原样重放最近实际保存的动作，或ref填程序给出的已存actionId；不是根据旧描述重新生成，不凭空编造ID。replay只接受name和ref。actionId、parentActionId、repairAttempt都由程序生成，不得写进标签。\n"
        + "generate仍用一句1至240字符的紧凑英文text，可另加leftGoal/rightGoal，各选any|down|forward|outward|near-head|above-head；默认any。目标是实际姿态观测要求，不是表达成功的文字：侧别、前方与外侧要分清，用户明确的终点应保留；不要为增加成功率写any。只有generate接受这些目标属性，不在基本手势、compose或replay上混用。\n"
        + "先检查能力再选路：当前不能独立控制单根手指，不能执行根腿移动或真实物体接触；遇到这些明确要求只说明限制，不把其文字塞入generate假装可执行，不自行换动作。replay的ref不存在时也只说明无法原样重放，不补另一手势或generate。\n"
        + "目标完整性：generate描述里要求哪一只手到可测区域，就为该侧写对应Goal；双手都要求就两侧都写，不能只靠英文text省略目标。修订时原有Goal必须全部原样写回。compose用left/right表达自身的方向约束，绝不夹leftGoal/rightGoal；使用哪条路线就只用该路线属性。\n"
        + "格式例（仅说明不同路线，具体形态仍按当前意图）：<motion name=\"generate\" text=\"Bring the right hand beside the head, then lower it smoothly.\" rightGoal=\"near-head\"/>；<motion name=\"compose\" left=\"outward\" joint=\"head\" axis=\"up\" amplitude=\"6\" cycles=\"1\" seconds=\"2\" end=\"hold\"/>。\n"
        + "程序反馈accepted/playing只表示受理或播放中；completed不等于所有文字语义已证明。目标观测reached仅表示所列必要几何目标达标，不保证全部text语义、掌向或自然度；unknown/unassessed没有可用判定。协议拒绝或goal-unmet时不能继续说已完成；同一动作最多自动修订一次，使用同一Qwen根据真实反馈修正动作描述，保留最初意图和所有原定goal，不能降低或撤销目标伪造成功。无法满足则如实说明，不反复尝试。普通说话不要求动作，也不会自动重复正在播放的动作。";

    // Match the same quoted boundary as RoleOutputChannels. A forbidden '>' inside a
    // description must be rejected as ONE command, never leave a suffix to be spoken.
    private static readonly Regex Tags = new Regex(
        "<\\s*motion\\b(?:\"[^\"]*\"|'[^']*'|[^'\">])*>", RegexOptions.IgnoreCase);
    private static readonly Regex Attributes = new Regex(
        "\\G\\s*(?<name>[A-Za-z_][A-Za-z0-9_-]*)\\s*=\\s*(?:\"(?<value>[^\"<>]*)\"|'(?<value>[^'<>]*)')");

    public static bool TryExtract(ref string executable, int generation, int sequence,
        out DialogueMotionIntent intent, out string rejection)
    {
        intent = default;
        rejection = "";
        if (string.IsNullOrEmpty(executable)) return false;
        MatchCollection matches = Tags.Matches(executable);
        if (matches.Count == 0) return false;
        executable = Tags.Replace(executable, "").Trim();
        if (matches.Count != 1)
        {
            rejection = "同一回复只能有一个 motion 指令，已忽略本组动作。";
            return false;
        }
        string token = matches[0].Value;
        Match body = Regex.Match(token, @"^<\s*motion\b(?<attrs>[\s\S]*?)/\s*>$", RegexOptions.IgnoreCase);
        if (!body.Success)
        {
            rejection = "motion 必须是完整的自闭合标签。";
            return false;
        }
        string attrs = body.Groups["attrs"].Value;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int at = 0;
        while (at < attrs.Length && !string.IsNullOrWhiteSpace(attrs.Substring(at)))
        {
            Match attr = Attributes.Match(attrs, at);
            if (!attr.Success || attr.Index != at || values.ContainsKey(attr.Groups["name"].Value))
            {
                rejection = "motion 属性格式错误或重复。";
                return false;
            }
            string key = attr.Groups["name"].Value.ToLowerInvariant();
            values.Add(key, attr.Groups["value"].Value.Trim());
            at += attr.Length;
        }
        if (!values.TryGetValue("name", out string name) ||
            !(name == "left-wave" || name == "right-wave" || name == "nod" ||
              name == "shake-head" || name == "none" || name == "generate" || name == "compose" || name == "plan" || name == "replay"))
        {
            rejection = "motion name 不在当前支持范围。可用 left-wave/right-wave/nod/shake-head/none/generate/compose/plan/replay；" +
                "其他动作需按当前已启用路线表达意图，不能自造动作名。";
            return false;
        }
        values.TryGetValue("text", out string description);
        description = description ?? "";
        if (name == "replay")
        {
            if (values.Count != 2 || !values.TryGetValue("ref", out string reference) ||
                !Regex.IsMatch(reference, @"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$"))
            {
                rejection = "replay 仅接受 name 和 ref，ref必须是程序提供的已存动作ID或last。";
                return false;
            }
            intent = new DialogueMotionIntent(name, "", generation, sequence, null, null, null, reference);
            return true;
        }
        if (name == "plan")
        {
            try
            {
                var action = new ArdyActionPlan();
                foreach (var attribute in values)
                {
                    if (attribute.Key == "name") continue;
                    if (attribute.Value.Length == 0) throw new ArgumentException("Omit unspecified plan attributes instead of sending empty values.");
                    action.SetValue(attribute.Key, attribute.Value);
                }
                action.ValidateRaw();
                intent = new DialogueMotionIntent(name, "", generation, sequence, null, action);
                return true;
            }
            catch (ArgumentException error)
            {
                rejection = "plan 语义计划无效：" + error.Message;
                return false;
            }
        }
        if (name == "compose")
        {
            try
            {
                var plan = new ArdyControlPlan();
                foreach (var attribute in values)
                {
                    switch (attribute.Key)
                    {
                        case "name": break;
                        case "left": plan.left = attribute.Value; break;
                        case "right": plan.right = attribute.Value; break;
                        case "leftbend": plan.leftBend = Number(attribute.Value); break;
                        case "rightbend": plan.rightBend = Number(attribute.Value); break;
                        case "leftpalm": plan.leftPalm = attribute.Value; break;
                        case "rightpalm": plan.rightPalm = attribute.Value; break;
                        case "joint": plan.joint = attribute.Value; break;
                        case "axis": plan.axis = attribute.Value; break;
                        case "amplitude": plan.amplitude = Number(attribute.Value); break;
                        case "cycles":
                            if (!int.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out plan.cycles))
                                throw new ArgumentException("cycles 必须是整数。");
                            break;
                        case "seconds": plan.seconds = Number(attribute.Value); break;
                        case "end": plan.end = attribute.Value; break;
                        default: throw new ArgumentException("compose 不接受属性 " + attribute.Key +
                            "。可用 name/left/right/leftBend/rightBend/leftPalm/rightPalm/joint/axis/amplitude/cycles/seconds/end；" +
                            "掌向分别用 leftPalm/rightPalm，left/right 为 none 时该侧掌向只能为 keep；不得丢弃原意或把不支持的动作假称已完成。");
                    }
                }
                if ((plan.left == "current" && values.ContainsKey("leftbend")) ||
                    (plan.right == "current" && values.ContainsKey("rightbend")))
                    throw new ArgumentException("保持current实际姿态时不能同时指定该侧新的肘角；需改变肘角时使用方向目标加数值Bend。");
                plan.leftBendAuto = plan.left != "none" && plan.left != "current" && plan.leftPalm != "keep" && !values.ContainsKey("leftbend");
                plan.rightBendAuto = plan.right != "none" && plan.right != "current" && plan.rightPalm != "keep" && !values.ContainsKey("rightbend");
                plan.Validate();
                intent = new DialogueMotionIntent(name, "", generation, sequence, plan);
                return true;
            }
            catch (ArgumentException error)
            {
                rejection = "compose 控制计划无效：" + error.Message;
                return false;
            }
        }
        foreach (string key in values.Keys)
        {
            if (key != "name" && key != "text" && !(name == "generate" && (key == "leftgoal" || key == "rightgoal")))
            {
                rejection = "当前动作包含未允许的属性；generate仅接受name、text、leftGoal、rightGoal。";
                return false;
            }
        }
        if (name == "generate")
        {
            if (string.IsNullOrWhiteSpace(description) || description.Length > 240 ||
                description.IndexOf('&') >= 0 || description.IndexOf('\n') >= 0 || description.IndexOf('\r') >= 0)
            {
                rejection = "生成动作需要1至240字符的单行 text，不能包含转义标签。";
                return false;
            }
            string unsupported = ArdyMotionCapabilities.UnsupportedReason(description);
            if (!string.IsNullOrEmpty(unsupported))
            {
                rejection = unsupported;
                return false;
            }
        }
        else if (values.ContainsKey("text"))
        {
            rejection = "基本手势不接受 text，避免默默忽略动作描述。";
            return false;
        }
        ArdyMotionGoal goal = null;
        if (name == "generate" && (values.ContainsKey("leftgoal") || values.ContainsKey("rightgoal")))
        {
            try
            {
                goal = new ArdyMotionGoal();
                if (values.TryGetValue("leftgoal", out string leftGoal)) goal.leftGoal = leftGoal;
                if (values.TryGetValue("rightgoal", out string rightGoal)) goal.rightGoal = rightGoal;
                goal.Validate();
            }
            catch (ArgumentException error)
            {
                rejection = "generate 可测目标无效：" + error.Message;
                return false;
            }
        }
        intent = new DialogueMotionIntent(name, description, generation, sequence, null, null, goal, null);
        return true;
    }

    /// <summary>Bounded data for a rejected executable command, never a permissive execution parser.</summary>
    public static void ReadRejectedContext(string executable, out string command, out string name,
        out string description, out ArdyMotionGoal goal)
    {
        command = name = description = ""; goal = null;
        var matches = Tags.Matches(executable ?? "");
        if (matches.Count != 1) return;
        string token = matches[0].Value;
        command = token.Length <= 4096 ? token : token.Substring(0, 4096);
        var body = Regex.Match(token, @"^<\s*motion\b(?<attrs>[\s\S]*?)/\s*>$", RegexOptions.IgnoreCase);
        if (!body.Success) return;
        string attrs = body.Groups["attrs"].Value;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int offset = 0;
        while (offset < attrs.Length)
        {
            if (string.IsNullOrWhiteSpace(attrs.Substring(offset))) break;
            var match = Attributes.Match(attrs, offset);
            if (!match.Success || match.Index != offset || values.ContainsKey(match.Groups["name"].Value)) return;
            values.Add(match.Groups["name"].Value, match.Groups["value"].Value);
            offset += match.Length;
        }
        if (values.TryGetValue("name", out string rawName)) name = rawName;
        if (values.TryGetValue("text", out string text)) description = text.Length <= 2048 ? text : text.Substring(0, 2048);
        if (name != "generate" || (!values.ContainsKey("leftGoal") && !values.ContainsKey("rightGoal"))) return;
        var candidate = new ArdyMotionGoal();
        if (values.TryGetValue("leftGoal", out string left)) candidate.leftGoal = left;
        if (values.TryGetValue("rightGoal", out string right)) candidate.rightGoal = right;
        try { candidate.Validate(); goal = candidate; }
        catch (ArgumentException) { /* The raw command still records the invalid requested goal. */ }
    }

    private static float Number(string value)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) ||
            float.IsNaN(number) || float.IsInfinity(number))
            throw new ArgumentException("控制数值必须是使用小数点的有限数字。");
        return number;
    }
}
