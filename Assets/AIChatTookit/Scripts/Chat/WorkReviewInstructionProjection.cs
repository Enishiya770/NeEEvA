using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Only remove an exact producer-owned instruction, never quoted user evidence,
// the goal JSON, original audio chronology or unknown/custom instructions.
public static class WorkReviewInstructionProjection
{
    public const string OriginalReportScope = "Original reply's reporting criterion, not a fresh obligation. Compare expectedReport and owner-bound outcomes against " +
        "speech.lastPublicPlayback.completedPlaybackText and actual dialogue. A criterion already covered by completed playback is fulfilled; " +
        "other pending results do not make it unreported again. Continue only genuinely unfinished steps or distinct unreported results. " +
        "No action is authorized by this historical reporting contract.";
    public const string ReportScope = "Original reply criterion only: compare expectedReport/owned outcome with actual completed playback. Covered results need no repeat; separate unfinished steps/unreported results remain due. This grants no action.";
    public static void ProjectRecord(JObject record)
    {
        foreach(var property in record.Descendants().OfType<JProperty>().ToArray())
            if((property.Name=="resultReportRequirement" || property.Name=="movementReportRequirement") &&
                property.Value is JObject requirement && (string)requirement["scope"]==OriginalReportScope)
                requirement["scope"]=ReportScope;
    }
    public const string OriginalOpportunityInstructions="先审查上一用户事项：尚欠实际检查、执行或结果说明时，应继续该事项，不受新意要求限制。仅在上一事项已完成或正等待用户后，依据当前活动、实际停留时间与上次决定，判断是否继续观察、改变视向、移动、休息或表达。无声观察不必相对最近发言产生新话题。新意图可以来自你自己的新想法、刚浮现的记忆、环境变化、时间流逝后自然想换的话题，也可以是无声观察或记忆整理；不要求一定说出口。请用时间与活动事实判断已经停留多久，不要声称看见未采样时段发生的事件。\n角色目前可按自身兴趣看向当前图像中的物体/区域、向未知方向改变视向后取得新画面，或走到可见目标附近的合法地面；这可以是无语音的自主意图，不必等用户叫你移动。为看全同一物件可以退远，为读细节可以靠近，也可向左或右换观察角度，保持面向目标；一次小范围调整后看新的真实图片再判断是否达到目的。不要把所有看清需求都当成靠近，不要把到达或可见像素范围自动当成文字可读、全貌已见或遮挡消失。依据视觉关注与行动事实保持已选目标，完成后允许停留，不要重复探查同一失败表面；图中发光不证明设备状态，观察与猜测分开。\n下面这些不算新意图：重说或改写上一句话、再次回答已经回答过的用户发言、重复表示正在等待、重复追问用户尚未回答的问题、为用户没有听见的内部复读道歉。\nproceed=true 只表示值得让正式角色再判断一次说话/沉默/动作，不表示必须开口。如果既无未完成事项也无新东西，proceed=false，并给出下一次重新考虑的秒数。\n\n";
    public const string OpportunityInstructions="先按完整请求、真实执行和已播结果判断原事项；欠步骤/答案/交付时继续原事项，有工具在运行则等待。没有欠项时可以自主观察、回忆、转向、靠近、退远、换角度、休息、陪伴、歌唱或表达，不需新命令，也不必开口。按实际停留时间和采样判断，未采样期间未知。观察可以推测，但图中发光不证明设备状态，到达不证明看清/可读/无遮挡；保持当前目标，勿重复同一失败。下一次正式决策仍需当前目标与执行器检查。复述旧回答、重复等待/追问或为未播内部草稿道歉不是新意图。用户仍表达时仅私下思考，之后结合完整发言决定。proceed只请求下一次决策，不授权行动；无意图可以false，并给出下次考虑时间。\n\n";
    public const string ExecutionAndReportTense="\n[Execution and report tense] Judge each selected audio clause's own operation and tense before comparing it with the receipt. A completed receipt says what the executor did; it cannot change a spoken progress/intent clause into a completion report. Started/working/heading towards, even when audio ends after execution, remains progress-only for a completed result: unreported, requiring only a factual report, never repeating the action. Explicit completion already spoken settles that operation; later unrelated progress does not withdraw it. Apply this equally to inspection, retrieval, editing, generation, performance and movement.\n";
    public static string Project(string context, bool interestOnly = false)
    {
        if(context==null)return context;
        // Keep the historical producer contract stable for exact purpose-based
        // replacement. Stage analysis belongs only to delivery review, not the
        // smaller independent-interest request (including recorded old wires).
        if(!interestOnly && context.Contains("[Task progress runtime]") && !context.Contains("[Spoken report stage]"))
            context+=WorkResultDeliveryProtocol.SpokenStageContract;
        if(!interestOnly && context.Contains("[Task progress runtime]") && !context.Contains("[Execution and report tense]"))
            context+=ExecutionAndReportTense;
        if(interestOnly)context=context.Replace(OriginalOpportunityInstructions,OpportunityInstructions).Replace("只输出一个紧凑 JSON，不要 Markdown；同时填写next_intent_source、next_step_id、planning_focus：","只输出自主预判schema中的字段：");
        if(interestOnly)context=context.Replace(WorkNextIntentProtocol.Contract,
            "\n[自主机会与交付分开] 按当前完整原话、任务表、失败及已播证据判断。尚欠步骤/独立答案/结果说明时继续原事项，不能借自主意图绕过；真实工具进行中等待。实际完成与交付各自结算，失败告知也可结束。历史停止/替换不等于成功，未完成历史不自动重启。后来的位置变化不撤销一次性完成。已有禁止、停留、恢复次数及所有权限制仍有效。新兴趣、陪伴、观察、歌唱可以自主提出，不要求用户另行命令，不得伪造用户事件或重做完成步骤。proceed仅请求下一次当前决策，不执行、不发声、不消费交付记录。这里只输出自主预判schema；逐项交付仍由交付监控/工作复核核验真实音频。\n");
        if(!interestOnly && !context.Contains("[Runtime work review domain: general]\n"))return context;
        // A private opportunity cannot invoke these tool syntaxes. Exact known
        // idle-material instructions are folded into their semantic boundaries;
        // captures, ASR differences, quoted user text and custom additions stay.
        bool inactive=false;int firstGoal=context.IndexOf("\n[Sing/Goal] ",StringComparison.Ordinal);
        if(firstGoal>=0)try {
            using(var reader=new JsonTextReader(new StringReader(context.Substring(firstGoal+"\n[Sing/Goal] ".Length)))) {
                var state=JObject.Load(reader);inactive=state["goal"]?.Type==JTokenType.Null&&state["active_goal"]?.Type==JTokenType.Null&&(bool?)state["active_goal_unfinished"]==false;
            }
        }catch(JsonException) { }
        if(interestOnly&&inactive)foreach(string line in IdleMaterialInstructions)context=context.Replace(line,"");
        if(interestOnly&&inactive&&!context.Contains("[Material opportunity limits]"))context+="\n[Material opportunity limits] Captures/lyrics/similarity are not singing requests. Acoustic p is not modality proof. Audio ready is not singing/goal approval; pending source may be clarified or autonomously chosen without confirming it. Clip identity and full actual chronology remain. Partial playback is not complete playback. New performance needs the normal goal/range/material checks; autonomous singing soft budget remains 60s. Catalog entries are not this user's recording or guaranteed whole songs.\n";
        string contract=ChatSample.SingingGoalContract;
        int start=context.IndexOf(contract,StringComparison.Ordinal);
        if(start<0)return context;
        int goal=context.IndexOf("\n[Sing/Goal] ",start+contract.Length,StringComparison.Ordinal);
        if(goal<0 || !string.IsNullOrWhiteSpace(context.Substring(start+contract.Length,goal-start-contract.Length)))return context;
        try {
            using(var reader=new JsonTextReader(new StringReader(context.Substring(goal+"\n[Sing/Goal] ".Length)))) {
                reader.DateParseHandling=DateParseHandling.None;
                var state=JObject.Load(reader);
                if(state["goal"]?.Type!=JTokenType.Null || state["active_goal"]?.Type!=JTokenType.Null ||
                    (bool?)state["active_goal_unfinished"]!=false)return context;
            }
        } catch(JsonException) {return context;}
        return context.Remove(start,contract.Length).Insert(start,
            "\n[歌唱目标状态] 当前无待审草案或未完歌唱目标；下列真实用户时序和已保留素材仍可用于理解请求。没有草案不证明用户要求已完成；新的自主提议仍可在后续当前决策中产生。\n");
    }

    private static readonly string[] IdleMaterialInstructions={
        "[演唱素材主接口：sing refs=具体引用；不填 source/mode/order。clip 身份在确认、修订后不变；range默认current；起止秒数统一基于原始录音。]",
        "playback=ready仅表示音频可执行，不是歌唱判定；结合整段转写、声学证据和语境选择或询问。shared_prefix表示联合识别复用了前段音频，不是又唱了一次，也不证明是同一说话人。",
        "录音先后（早→晚；不是角色播放顺序）见下列 recording 编号；不是练唱清单编号。",
        "p 只是声学测量，实测它分不开演唱和噪声，不能当模态结论；结合这一轮用户原话判断。要用其中一段先 <clip_adopt ref=\"cap:…\" reason=\"…\"/> 收进清单，收进来仍是 source=pending，播放仍需 sing_goal 与独立审核。不收也不会删除，最近几轮之后自然淘汰。",
        "清单中的每个引用只对应该录音；某次播放未列出的引用不在那次完整播放里。pending 可由你结合语境确认，也可先询问；程序不自动补选。以上是只读事实，不代表详细 Skill 已加载。",
        "‘刚唱的录音’与‘你刚才唱的第几段’可分别参考上面两种顺序；歌名/相似歌词不等于同一次录音。指代仍由你判断，可询问。",
        "pending：来源不确定，可询问或自主选已有音频；演唱不要求确认来源，也不自动确认来源。evidence_only：已录到但未选可播放范围。多 refs 的 range 对各段独立采用其自身 clean/expanded；不同范围可先逐段修订。准备好不等于已唱。",
        "素材当前总长=0.0s；明确要求完整演唱可自然分块流式播放。自主发起的软预算=60s，由你选择范围，不在句中强制截断。曲库不是本次录音：曲库快照中的完整id可写 sing refs=\"song:完整id\"；不指定 lyrics 时表示该曲库条目的全部已存片段，不代表完整原曲。相似候选不证明用户要唱曲库版本。",
    };
    public static string ShareGoals(string context)
    {
        var seen=new System.Collections.Generic.HashSet<string>();
        return string.Join("\n",context.Split('\n').Select(line=>line.StartsWith("[Sing/Goal] {",StringComparison.Ordinal)&&
            !seen.Add(line)?"[Sing/Goal duplicate] Same complete goal state and user chronology as the preceding identical Sing/Goal document.":line));
    }
}
