using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // A proposal is authored by the role, then checked by a separate semantic review.
    // No user-keyword matching and no inference from an action's reason field occurs here.
    private sealed class SingingGoal
    {
        public int turn;
        public int version;
        public int baseVersion;
        public int sourceUserTurn;
        public int proposalSpeechRound;
        public int proposalWorkEpoch;
        public bool targetReviewed;
        public bool originCorrectedFromAutonomous;
        public string transition = "create", changeQuote = "";
        public bool autonomous;
        public string origin = "unspecified", requestQuote = "";
        public string refs, range, revisions, intent, completion, expected, feedback, evidence, repairScope;
        public float start = float.NaN, end = float.NaN;
        public string review = "pending", reviewEvidence = "";
        public string outcome = "not_submitted";
    }

    private sealed class SingingMaterialFact
    {
        public string clip;
        public int revision;
        public float start, end;
    }

    private sealed class SingingPlaybackEvidence
    {
        public int turn, goalVersion;
        public string goalRefs, expected, rendering;
        public List<SingingMaterialFact> materials = new List<SingingMaterialFact>();
        public bool completed, userConfirmed, targetMatches;
    }

    private sealed class SingingUserInputEvidence
    {
        public int turn;
        public string text;
    }

    private SingingGoal m_SingingGoal, m_PreviousSingingGoal, m_ActiveSingingGoal;
    private SingingPlaybackEvidence m_PendingSingingGoalPlayback, m_LastSingingGoalPlayback;
    private readonly List<SingingPlaybackEvidence> m_RejectedSingingPlans = new List<SingingPlaybackEvidence>();
    private readonly List<SingingUserInputEvidence> m_SingingUserInputs = new List<SingingUserInputEvidence>();
    private int m_SingingGoalTurn, m_SingingGoalVersion;
    private int m_SingingRepairFeedbackTurn = -1;
    private string m_ValidatedSingingRendering = "";
    private List<SingingMaterialFact> m_ValidatedSingingMaterials;
    private bool? m_SingingGoalOwnerAutonomous;

    private sealed class DeferredSingingRequest
    {
        public AgentHumBackRequest request;
        public int userTurn, workEpoch, formalGeneration, goalVersion;
        public LLM model;
    }
    private DeferredSingingRequest m_DeferredSingingRequest;

    private void ClearDeferredSingingRequest() => m_DeferredSingingRequest = null;

    // Only the explicit executable sing from the same valid after_action response
    // may wait for this exact proposal. A goal alone never creates a performance.
    private bool TryDeferSingingUntilGoalReview(AgentHumBackRequest request,
        out SingingResolutionFailure failure)
    {
        failure = null;
        var goal = m_SingingGoal;
        if (request == null || !request.UnifiedSing || goal == null || goal.review != "pending" ||
            goal.turn != m_SingingGoalTurn || goal.proposalSpeechRound != m_CurrentSpeechRoundId ||
            goal.intent == "observe" || goal.transition == "cancel")
            return RejectSingingResolution(out failure, "goal_review_pending", "goal",
                "该显式请求不属于同一回复中的待审目标；没有保存或执行。", "等待目标审核后按当前事实重新决定。", true);
        if (m_SingingSpeechPhaseRound != m_CurrentSpeechRoundId ||
            m_SingingSpeechDependency != SpeechActionDependency.AfterAction ||
            m_SingingRejectedSpeechRound == m_CurrentSpeechRoundId ||
            m_SingingDeclaredActionRound != m_CurrentSpeechRoundId ||
            !string.IsNullOrEmpty(request.SourceValidationError) || !string.IsNullOrEmpty(request.ValidationError) ||
            request.ExtraTagsIgnored > 0)
            return RejectSingingResolution(out failure, "invalid_deferred_request", "request",
                "只有同一合法after_action回复中的单个显式sing可等待审核；本次未保存。", "修正公开输出和参数后重新决定，不能从拒绝的原文恢复动作。");
        string refs = string.Join(",", (request.ClipRefs ?? "").Split(',').Select(p => p.Trim()));
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        if (sense != null) refs = string.Join(",", refs.Split(',').Select(sense.CanonicalSingingClipReference));
        if (refs == goal.refs) AdoptReviewedSingingRange(request, goal);
        string range = Finite(request.StartSeconds) ? "window" : request.Range;
        // refs 不同仍然拒绝：那是"要唱哪几段"，两份写法冲突时程序不能替她选。
        if (refs != goal.refs)
            return RejectSingingResolution(out failure, "goal_target_mismatch", "goal",
                $"待审sing的refs与同回复目标不同（目标 refs={goal.refs}，sing refs={refs}）；没有保存或修改目标。",
                "让 sing 的 refs 与目标一致，或直接只写 sing 由程序补目标。");
        // 范围不同则以 sing 为准更新这份尚未审核的目标，而不是双双作废。两者出自同一条
        // 回复、同一个模型，作废只会让她猜该改哪一半——实测 s18 L20590 她猜错了那一半：
        // 目标里正确的 range=current 被她改成 expanded，第二段的尾部说话就此回来。
        if (range != goal.range ||
            (range == "window" && (Mathf.Abs(request.StartSeconds - goal.start) > .001f ||
                Mathf.Abs(request.EndSeconds - goal.end) > .001f)))
        {
            if (range == "window" && (!Finite(request.EndSeconds) || request.EndSeconds <= request.StartSeconds ||
                    refs.Split(',').Length != 1))
                return RejectSingingResolution(out failure, "goal_target_mismatch", "goal",
                    "sing 给了起止秒数，但它只对单个 clip 有效且须成对有效；没有保存或修改目标。",
                    "多段要各自范围时先逐段 clip_revise，再用 range=\"current\"。");
            Debug.Log($"[Sing/Goal] 同回复的 sing 范围与目标不同，按 sing 更新待审目标：" +
                $"{goal.range} → {range}；refs 与成功条件未变。");
            goal.range = range;
            goal.start = range == "window" ? request.StartSeconds : float.NaN;
            goal.end = range == "window" ? request.EndSeconds : float.NaN;
        }
        if (m_DeferredSingingRequest != null)
            return RejectSingingResolution(out failure, "goal_request_already_waiting", "goal",
                "本目标已有一个待审显式请求；没有替换参数或额外排队。", "等待这次独立审核。", true);
        var copy = JsonConvert.DeserializeObject<AgentHumBackRequest>(JsonConvert.SerializeObject(request));
        copy.ClipRefs = refs;
        m_DeferredSingingRequest = new DeferredSingingRequest { request = copy,
            userTurn = m_SingingGoalTurn, workEpoch = m_WorkEpoch,
            formalGeneration = m_FormalResponseGeneration, goalVersion = goal.version,
            model = m_ChatSettings?.m_ChatModel };
        if (m_LogAgentLoop) Debug.Log($"[Sing/Wait] status=waiting_goal_review version={goal.version} explicit_request_saved=true; no_audio_submitted=true");
        return true;
    }

    /// 没有当轮目标时，用 sing 自己的参数补一份最小目标，而不是拒绝。
    ///
    /// 原先必须把同一件事写两遍且逐字对齐：sing_goal 一遍、sing 一遍。实测 s11–s18 的
    /// 49 次纠错里，goal_required 9 次、goal_target_mismatch 2 次全部出自这个分离，
    /// 而它们拦下的都不是"会唱错东西"，只是两份参数没对齐。目标本身仍然登记、仍然
    /// 走独立审核（授权由 origin/requestQuote 那一关把守），只是不再要求她手写两遍。
    private bool TryAutoRegisterSingingGoalFromSing(AgentHumBackRequest request, out string failure)
    {
        failure = "";
        if (request == null || !request.UnifiedSing || m_SingingGoal != null) return false;
        if (m_WorkNoProgress || string.IsNullOrWhiteSpace(request.ClipRefs)) return false;
        bool autonomous = request.GoalOrigin == "autonomous";
        // 自主演唱必须她自己写 origin="autonomous"；不写就按用户请求处理，而用户请求
        // 需要有原话可依托——这条来源区分不由程序代劳，审核那关还会核对引文。
        if (!autonomous && string.IsNullOrWhiteSpace(m_LastUserMsg)) return false;
        if (m_SingingGoalOwnerAutonomous == true && !autonomous) return false;
        string intent = request.GoalIntent.Length > 0 ? request.GoalIntent : "perform";
        string feedback = request.GoalFeedback.Length > 0 ? request.GoalFeedback : "none";
        string completion = request.GoalCompletion.Length > 0 ? request.GoalCompletion : "playback";
        if (!new[] { "perform", "repair", "repeat", "observe" }.Contains(intent))
        { failure = "sing 的 intent 只能是 perform/repair/repeat/observe，实际=\"" + intent + "\"；未执行。"; return false; }
        if (!new[] { "none", "unsatisfied", "satisfied" }.Contains(feedback))
        { failure = "sing 的 feedback 只能是 none/unsatisfied/satisfied，实际=\"" + feedback + "\"；未执行。"; return false; }
        if (!new[] { "playback", "user_confirmation" }.Contains(completion))
        { failure = "sing 的 completion 只能是 playback/user_confirmation，实际=\"" + completion + "\"；未执行。"; return false; }
        if (request.GoalOrigin.Length > 0 && !new[] { "user_request", "autonomous" }.Contains(request.GoalOrigin))
        { failure = "sing 的 origin 只能是 user_request/autonomous，实际=\"" + request.GoalOrigin + "\"；未执行。"; return false; }
        // 和手写目标同一条规则：说用户不满意或要求重唱，必须引用用户这一轮的原话。
        if ((feedback != "none" || intent == "repeat") &&
            (request.GoalEvidence.Length == 0 || !(m_LastUserMsg ?? "").Contains(request.GoalEvidence)))
        { failure = "sing 声明了 feedback/repeat，就要在 evidence 里逐字引用用户这一轮的原话；" +
            "角色自己的话不能代替用户证据。未执行。"; return false; }
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        var refs = request.ClipRefs.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        if (refs.Length == 0 || refs.Any(s => !s.StartsWith("clip:", StringComparison.Ordinal))) return false;
        if (sense != null) refs = refs.Select(sense.CanonicalSingingClipReference).ToArray();
        bool window = Finite(request.StartSeconds);
        // window 仍然只对单段有效，和手写目标同一条规则；多段带坐标交回原有报错。
        if (window && (refs.Length != 1 || !Finite(request.EndSeconds) || request.EndSeconds <= request.StartSeconds))
            return false;
        var goal = new SingingGoal
        {
            turn = m_SingingGoalTurn,
            proposalSpeechRound = m_CurrentSpeechRoundId,
            proposalWorkEpoch = m_WorkEpoch,
            origin = autonomous ? "autonomous" : "user_request",
            autonomous = autonomous,
            refs = string.Join(",", refs),
            range = window ? "window" : request.Range,
            revisions = BindCurrentSingingMaterialVersions(refs),
            intent = intent,
            completion = completion,
            expected = string.IsNullOrWhiteSpace(request.Reason)
                ? "按 sing 指定的素材与范围演唱：" + string.Join(",", refs)
                : request.Reason.Trim(),
            feedback = feedback,
            evidence = request.GoalEvidence,
            repairScope = "content",
            start = window ? request.StartSeconds : float.NaN,
            end = window ? request.EndSeconds : float.NaN
        };
        var revisions = goal.revisions.Split(',');
        if (revisions.Length != refs.Length) return false;
        // 必须走和手写目标同一条 transition/baseVersion 绑定，不能自己推。实测 s19：
        // 漏了这一步的版本 baseVersion 恒为 0，一旦本轮已有 active_goal（version>0），
        // 审核就以"活动目标版本在审查期间已经变化"连判两次 revise，turn 10 卡死在这里。
        if (!BindSingingGoalTransition(goal, "", out string transitionFailure))
        { failure = "按 sing 补目标失败：" + transitionFailure; return false; }
        if (request.GoalChangeQuote.Length > 0) goal.changeQuote = request.GoalChangeQuote;
        goal.version = ++m_SingingGoalVersion;
        ClearDeferredSingingRequest();
        m_SingingGoalOwnerAutonomous = false;
        m_SingingGoal = goal;
        RecordPracticeEditResult("本轮没有单独登记 sing_goal，已按这次 sing 的 refs/范围补一份目标，" +
            "review=pending；未修订/播放。需独立核对最新用户要求后才会执行。" +
            "想要不同的成功条件或范围，下次直接写 sing_goal。", false);
        Debug.Log("[Sing/Goal] auto-from-sing proposal=" + JsonConvert.SerializeObject(goal));
        return true;
    }

    // The caller is the current, active review callback. Taking is single-use and is
    // not approval to render: QueueHumBack must run the full resolver again.
    private bool TryTakeReviewedSingingRequest(out AgentHumBackRequest request)
    {
        request = null;
        var pending = m_DeferredSingingRequest;
        if (pending == null || m_SingingGoal?.review == "pending") return false;
        ClearDeferredSingingRequest();
        if (!m_AgentRunning || m_AgentGracefulShutdownPending || !enabled || m_UserSpeechActiveForAutonomy ||
            pending.userTurn != m_SingingGoalTurn || pending.workEpoch != m_WorkEpoch ||
            pending.formalGeneration != m_FormalResponseGeneration ||
            !ReferenceEquals(pending.model, m_ChatSettings?.m_ChatModel) ||
            m_SingingGoal == null || m_SingingGoal.review != "approved" || !m_SingingGoal.targetReviewed ||
            m_SingingGoal.version != pending.goalVersion || m_ActiveSingingGoal?.version != pending.goalVersion)
            return false;
        request = pending.request;
        if (m_LogAgentLoop) Debug.Log($"[Sing/Wait] status=goal_review_approved version={pending.goalVersion} explicit_request_consumed=true; requires_full_resolver=true");
        return true;
    }

    private void ResetSingingGoalForUserTurn()
    {
        ClearDeferredSingingRequest();
        if (m_SingingGoal != null) m_PreviousSingingGoal = m_SingingGoal;
        m_SingingGoal = null;
        m_ValidatedSingingMaterials = null;
        m_SingingGoalOwnerAutonomous = null;
        m_SingingGoalTurn++;
        if (!string.IsNullOrWhiteSpace(m_LastUserMsg))
        {
            m_SingingUserInputs.Add(new SingingUserInputEvidence { turn = m_SingingGoalTurn,
                text = TruncateForFrame(m_LastUserMsg, 1024) });
            while (m_SingingUserInputs.Count > 16) m_SingingUserInputs.RemoveAt(0);
        }
        // Pending playback is fenced by its original turn, and retained until its real callback.
        // A new utterance fences executable proposals, not the outstanding objective.
        // The model must explicitly continue/change/cancel it before further singing.
    }

    private void ClearSingingGoalSession()
    {
        ClearDeferredSingingRequest();
        m_SingingGoal = m_PreviousSingingGoal = m_ActiveSingingGoal = null;
        m_PendingSingingGoalPlayback = m_LastSingingGoalPlayback = null;
        m_RejectedSingingPlans.Clear(); m_SingingRepairFeedbackTurn = -1;
        m_SingingUserInputs.Clear();
        m_ValidatedSingingRendering = ""; m_ValidatedSingingMaterials = null;
        m_SingingGoalOwnerAutonomous = null;
        m_SingingGoalTurn++; m_SingingGoalVersion++;
    }

    internal const string SingingGoalContract =
        "\n[歌唱事项闭环] 先区分用户已发生的演唱、要求角色执行的动作与你的自主提议。用户唱过、分享歌词、说自己试唱都不构成回唱任务；可以只自然回应，不必登记目标。" +
        "确有角色演唱目标时才登记；origin=user_request 表示用户要求你唱，origin=autonomous 表示你自己的提议，不得记成用户欠办事项。为什么/检查技能等追问不自动取消已核对的原目标。" +
        "演唱前提交 <sing_goal origin=\"user_request\" refs=\"clip:真实身份,clip:真实身份\" range=\"current\" intent=\"perform\" completion=\"playback\" expected=\"实际要唱的内容及顺序\" feedback=\"none\"/>。" +
        "refs顺序就是目标顺序；revisions省略时程序绑定这些refs当前的实际版本；显式提供须从清单复制，不能猜数字，候选为pending；range=current/clean/expanded/window，window须成对start_seconds/end_seconds。" +
        "window仅是goal的范围类型：真正sing只填写成对start_seconds/end_seconds，不填写range=window或其它range。" +
        "intent=perform/repair/repeat；repair_scope=content/music/delivery（默认content，音高/速度修正用music）；completion=playback/user_confirmation；补漏内容须user_confirmation，播完不能证明缺失内容已经补回。" +
        "用户负面反馈用feedback=unsatisfied；明确认可用satisfied；这些反馈或repeat须evidence引用最新用户原话。" +
        "只记录反馈可用intent=observe且不填refs。不得把自己的道歉/声称成功作为用户反馈。" +
        "目标先由独立语义审查核对，approved后才能sing或修改其范围。同一合法after_action回复可以先sing_goal再显式sing；该sing会绑定本次目标等待审核，通过后仍全量检查范围/版本/权限才执行一次；拒绝、新用户输入或取消会撤销它。只登记sing_goal不自动演唱。" +
        "已approved且用户未更新时，无需重写目标；改变refs/range/revision/完成条件需重新审查。" +
        "新用户轮只暂停执行，不清空active_goal。追问失败原因或没听到时，原refs顺序和成功条件仍在；确认要继续可用<sing_goal transition=\"continue\"/>，程序绑定原目标和版本，不默认最近录音。" +
        "修改原任务用transition=revise，换成独立新演唱任务用replace，明确放弃原任务用cancel；用户事项须change_quote逐字引用最新用户改变/取消要求，并由独立审查确认其语义。" +
        "有未完成active_goal时省略transition等于continue，不能借新proposal缩减原目标。revise/replace仍须完整refs/range/expected/completion；revisions可省略由程序绑定当前素材，提供时必须匹配。" +
        "登记时还是候选的素材绑定revisions=pending；之后来源被确认，程序会把它解析成实际版本，不需要你改目标，也不算改变内容。" +
        "continue不能改素材/范围/完成条件；只解释或回答追问可以不发工具，保留待办但不会自动演唱。cancel只取消事项，不删除录音。" +
        "reason只是解释，expanded必须写入range。目标与真正工具参数都要落实用户修正，不能只把二者填成同一个错误方案。\n";

    private string BuildSingingGoalContext()
    {
        return "\n[Sing/Goal] " + JsonConvert.SerializeObject(new
        {
            user_turn = m_SingingGoalTurn,
            latest_user_request = TruncateForFrame(m_LastUserMsg ?? "", 3000),
            recent_actual_user_inputs = m_SingingUserInputs,
            input_scope = "原始用户输入，不保证是执行请求；按完整时序区分用户表演与要求角色行动",
            review_field_scope = "requestQuote/sourceUserTurn由独立审核依据实际用户输入产生，不是sing_goal提案的必填参数；尚未核定时省略。已存在的值是前次审核保存的来源，可供continue核对。审核员在本次singing_goal_expected.request_quote中引用真实请求。",
            next_step = BuildSingingGoalNextStep(),
            goal = SingingGoalObservation(m_SingingGoal),
            active_goal = SingingGoalObservation(m_ActiveSingingGoal),
            active_goal_unfinished = IsActiveSingingGoalUnfinished(),
            previous_goal = SingingGoalObservation(m_PreviousSingingGoal),
            last_playback = m_LastSingingGoalPlayback,
            rejected_plans = m_RejectedSingingPlans,
            feedback_scope = "completed仅表示Unity实际播完所列refs/revision/raw范围；不证明用户听到或指定歌词/缺失内容满足。active_goal是持久成功条件；goal是当前轮待审核/执行草案，null不代表原任务已取消或完成。没有当前轮动作授权不能自动续唱。"
        }) + "\n";
    }

    private object BuildSingingGoalNextStep()
    {
        var goal = m_SingingGoal;
        if (goal == null || goal.turn != m_SingingGoalTurn)
            return new { stage = IsActiveSingingGoalUnfinished() ? "goal_continuation_required" : "goal_required",
                action = IsActiveSingingGoalUnfinished() ? "<sing_goal transition=\"continue\"/>" : "",
                scope = "未授权演唱。依据完整实际用户输入和素材自行决定目标；后续询问不代表原请求已取消。" };
        if (goal.review != "approved")
            return new { stage = "goal_" + goal.review, action = "",
                scope = goal.review == "pending" ? "正在独立审核；不要重复提案或提交另一份演唱。" : "根据审核证据修正或询问；没有开始演唱。" };
        string action = goal.intent == "observe" || goal.outcome == "playback_complete_content_unverified" || goal.outcome == "user_confirmed"
            ? "" : "<sing refs=\"" + goal.refs + "\"" + (goal.range == "window"
                ? " start_seconds=\"" + goal.start.ToString("R", CultureInfo.InvariantCulture) + "\" end_seconds=\"" + goal.end.ToString("R", CultureInfo.InvariantCulture) + "\""
                : " range=\"" + goal.range + "\"") + "/>";
        return new { stage = action.Length == 0 ? "no_new_performance" : "goal_approved", action,
            scope = "程序仅按已审目标列出精确参数，不是新的行动决定。已保存的同回复显式请求最多消费一次；没有请求时仍由角色决定是否演唱。" };
    }

    private bool IsActiveSingingGoalUnfinished() => m_ActiveSingingGoal != null &&
        m_ActiveSingingGoal.review != "cancelled" && m_ActiveSingingGoal.outcome != "cancelled" &&
        m_ActiveSingingGoal.outcome != "user_confirmed" &&
        (m_ActiveSingingGoal.outcome != "playback_complete_content_unverified" ||
            m_ActiveSingingGoal.completion == "user_confirmation");

    private static JObject SingingGoalObservation(SingingGoal goal)
    {
        if (goal == null) return null;
        var observation = JObject.FromObject(goal);
        // Non-window targets select an existing retained range; NaN is only an internal
        // sentinel and must not look like a missing required coordinate to the reviewer.
        if (goal.range != "window")
        {
            observation.Remove("start");
            observation.Remove("end");
        }
        // These are results of independent source verification, not proposal inputs.
        // Keep a reviewed source on continuation, but do not present uninitialized
        // review output as a missing field the role must fill before this review.
        if (string.IsNullOrEmpty(goal.requestQuote)) observation.Remove("requestQuote");
        if (goal.sourceUserTurn <= 0) observation.Remove("sourceUserTurn");
        return observation;
    }

    private bool IsSingingGoalAwaitingExecution()
    {
        return m_SingingGoal != null && m_SingingGoal.intent != "observe" &&
            m_SingingGoal.review != "cancelled" && m_SingingGoal.review != "waiting_user" && m_SingingGoal.review != "blocked" &&
            m_SingingGoal.outcome != "playback_complete_content_unverified" &&
            m_SingingGoal.outcome != "user_confirmed";
    }

    private bool HasPendingSingingGoalReview() => m_SingingGoal != null && m_SingingGoal.review == "pending";

    private bool HasAutonomousSingingGoal() => m_SingingGoal != null && m_SingingGoal.autonomous;

    private bool IsSingingGoalAwaitingConfirmation() => m_SingingGoal != null &&
        m_SingingGoal.completion == "user_confirmation" &&
        m_SingingGoal.outcome == "playback_complete_content_unverified";

    private void ExtractSingingGoalTags(ref string text, bool apply)
    {
        if (string.IsNullOrEmpty(text)) return;
        var pattern = new Regex(@"<sing_goal\b(?<attrs>[^>]*)/>", RegexOptions.IgnoreCase);
        var matches = pattern.Matches(text);
        text = pattern.Replace(text, "");
        if (!apply || matches.Count == 0) return;
        if (m_WorkNoProgress) { m_SingingRejectedSpeechRound = m_CurrentSpeechRoundId; return; }
        if (matches.Count != 1)
        { RejectSingingGoal("一次只能提交一个完整目标；没有选择其中某个替代用户要求。"); return; }
        string attrs = matches[0].Groups["attrs"].Value;
        var goal = new SingingGoal
        {
            turn = m_SingingGoalTurn,
            proposalSpeechRound = m_CurrentSpeechRoundId,
            proposalWorkEpoch = m_WorkEpoch,
            origin = ReadSingingGoalChoice(attrs, "origin", "user_request"),
            autonomous = ReadSingingGoalChoice(attrs, "origin", "unspecified") == "autonomous",
            refs = (ReadToolAttribute(attrs, "refs") ?? "").Trim(),
            range = ReadSingingGoalChoice(attrs, "range", "current"),
            revisions = (ReadToolAttribute(attrs, "revisions") ?? "").Trim(),
            intent = ReadSingingGoalChoice(attrs, "intent", "perform"),
            completion = ReadSingingGoalChoice(attrs, "completion", "playback"),
            expected = (ReadToolAttribute(attrs, "expected") ?? "").Trim(),
            feedback = ReadSingingGoalChoice(attrs, "feedback", "none"),
            evidence = (ReadToolAttribute(attrs, "evidence") ?? "").Trim(),
            repairScope = ReadSingingGoalChoice(attrs, "repair_scope", "content"),
            start = ReadToolFloatAttributeUnclamped(attrs, "start_seconds"),
            end = ReadToolFloatAttributeUnclamped(attrs, "end_seconds")
        };
        if (!BindSingingGoalTransition(goal, attrs, out string transitionFailure))
        { RejectSingingGoal(transitionFailure); return; }
        // 这五个字段原本共用一句"目标枚举无效"。实测 s12/s14：她三次写出
        // range="expanded,clean" / "clean,expanded" / "current,current" 这类逐段取值，
        // 每次都只收到那一句，无从判断错在哪个字段——于是转去怀疑素材，把已经调对的
        // 第二段又改坏了。报文必须点名字段、给出它自己的合法值。
        var enumErrors = new List<string>();
        void CheckGoalChoice(string field, string value, params string[] allowed)
        {
            if (Array.IndexOf(allowed, value ?? "") >= 0) return;
            enumErrors.Add(field + "=\"" + value + "\"（只能是 " + string.Join("/", allowed) + "）");
        }
        CheckGoalChoice("intent", goal.intent, "perform", "repair", "repeat", "observe");
        CheckGoalChoice("feedback", goal.feedback, "none", "unsatisfied", "satisfied");
        CheckGoalChoice("completion", goal.completion, "playback", "user_confirmation");
        CheckGoalChoice("repair_scope", goal.repairScope, "content", "music", "delivery");
        CheckGoalChoice("range", goal.range, "current", "clean", "expanded", "window");
        if (enumErrors.Count > 0)
        {
            // 逗号是她在尝试逐段范围。程序只接受统一取值，但现成的替代路径是存在的，
            // 直接给出来，免得她再花几轮反推。
            string perRefHint = (goal.range ?? "").Contains(",")
                ? " range 对全部 refs 统一生效，不接受逐段列表；要各段不同范围，先逐段 clip_revise，再用 range=\"current\"。"
                : "";
            RejectSingingGoal("目标枚举无效：" + string.Join("；", enumErrors) +
                "。未登记、修订或播放。" + perRefHint);
            return;
        }
        if ((goal.feedback != "none" || goal.intent == "repeat") &&
            (goal.evidence.Length == 0 || !(m_LastUserMsg ?? "").Contains(goal.evidence)))
        { RejectSingingGoal("用户反馈/重复演唱意图须引用最新用户原话作为evidence，再由独立语义审查核对；角色自己的话不能代替用户证据。"); return; }
        if (goal.intent != "observe")
        {
            if (goal.expected.Length == 0 || goal.refs.Length == 0)
            { RejectSingingGoal("目标须明确refs和expected；不能默认全部或最近一段。"); return; }
            string[] refs = goal.refs.Split(',').Select(s => s.Trim()).ToArray();
            if (goal.revisions.Length == 0)
                goal.revisions = BindCurrentSingingMaterialVersions(refs);
            string[] revisions = goal.revisions.Split(',').Select(s => s.Trim()).ToArray();
            if (refs.Length != revisions.Length || refs.Any(s => !s.StartsWith("clip:", StringComparison.Ordinal)) ||
                revisions.Any(s => s != "pending" && (!int.TryParse(s, out int n) || n < 0)))
            { RejectSingingGoal("录音目标refs与revisions必须一一对应；版本为整数或pending，顺序不可丢失。"); return; }
            goal.refs = string.Join(",", refs); goal.revisions = string.Join(",", revisions);
            if (goal.range == "window" && (refs.Length != 1 || !Finite(goal.start) || !Finite(goal.end) || goal.end <= goal.start))
            { RejectSingingGoal("window目标仅一个clip，须有效成对原录音起止秒数。"); return; }
            if (goal.range != "window" && (Finite(goal.start) || Finite(goal.end)))
            { RejectSingingGoal("range与明确秒数不能混用。"); return; }
        }
        if (!new[] { "unspecified", "user_request", "autonomous" }.Contains(goal.origin))
        { RejectSingingGoal("origin仅为user_request或autonomous；用户输入不必然包含回唱要求。"); return; }
        if (goal.autonomous && m_SingingGoalOwnerAutonomous == false)
        { RejectSingingGoal("不能将同轮用户事项改名为自主提议以获得另一份续接预算。"); return; }
        if (SameSingingGoal(m_SingingGoal, goal))
        {
            if (m_SingingGoal.review == "revise")
            {
                RecordSingingRejection("goal:" + SingingProposalKey() + SingingMaterialVersionKey(), m_SingingGoal.reviewEvidence);
                RecordPracticeEditFailureForLlm("目标与被要求修正的proposal完全相同；review=revise，尚未执行。原因=" + m_SingingGoal.reviewEvidence,
                    "singing_goal_unchanged", "根据最新用户原话修正实际refs/范围/版本/完成条件，或提出具体问题；不要重复相同proposal。", "sing_goal");
            }
            return;
        }
        if (goal.autonomous && m_SingingGoalOwnerAutonomous == null && m_WorkStatus == "blocked")
        {
            // A user task that exhausted its budget cannot gain a fresh autonomous
            // allowance merely because it had not produced a valid proposal yet.
            m_SingingGoalOwnerAutonomous = false;
            RejectSingingGoal("本轮用户事项已达到续接上限；没有建立新的自主歌唱事项。等待用户新指示，不能把未完成要求改称自主提议继续重试。");
            return;
        }
        goal.version = ++m_SingingGoalVersion;
        ClearDeferredSingingRequest();
        m_SingingGoalOwnerAutonomous = goal.autonomous;
        m_SingingGoal = goal;
        // 只登记目标不会发出任何声音。实测 s20：她连着两轮只写 sing_goal，目标登记了、
        // 审核还批了，程序一声没出也没提示，用户等到第三轮才问"没听到你唱出来啊"。
        // 现在把这件事直接写进回执，而不是让她从沉默里反推。
        // 用本轮实际发出的标签名判断，它在任何提取器动手之前就测好了（ChatSample.cs:5148），
        // 不依赖发声阶段校验的先后顺序。
        bool singingThisReply = ("、" + m_RoundEmittedTagNames + "、").Contains("、sing、") ||
            ("、" + m_RoundEmittedTagNames + "、").Contains("、hum_back、");
        RecordPracticeEditResult("歌唱目标已登记，review=pending；未修订/播放。" +
            (singingThisReply
                ? "同回复的 sing 已保留，审核通过后会重新核验素材并提交一次。"
                : "本条回复没有 sing，所以不会发出任何声音——登记目标本身从不触发演唱。" +
                  "想现在就唱，在同一条回复里补一个 <sing refs=\"…\"/>；" +
                  "只想先登记、看到审核事实再决定，那就保持现状，等下一轮再唱。") +
            "需独立核对最新用户要求、refs顺序及反馈，再决定行动。", false);
        Debug.Log("[Sing/Goal] proposal=" + JsonConvert.SerializeObject(goal));
    }

    private string BindCurrentSingingMaterialVersions(string[] refs)
    {
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        if (sense == null) return "";
        var ready = sense.DescribePracticePhrases();
        var pending = sense.DescribeQuarantinedSingingCandidates();
        return string.Join(",", refs.Select(clip => {
            var material = ready.Find(p => p.ClipRef == clip);
            return material != null ? material.Revision.ToString(CultureInfo.InvariantCulture) :
                pending.Any(p => p.ClipRef == clip) ? "pending" : "missing";
        }));
    }

    // <sing> falls back to range="current" when the attribute is absent, while an
    // objective for a clip whose current version is not selectable has to name
    // clean/expanded. That makes the protocol's own two defaults disagree, and the
    // action is then rejected over a parameter the character never wrote. The reviewed
    // objective is the authority and its refs already matched, so an omitted range
    // follows it instead of a static default — the same treatment revisions already
    // gets. An explicitly written range that differs is still a real mismatch.
    private static void AdoptReviewedSingingRange(AgentHumBackRequest request, SingingGoal goal)
    {
        if (request == null || goal == null || !request.UnifiedSing || request.RangeExplicit) return;
        if (Finite(request.StartSeconds) || Finite(request.EndSeconds)) return;
        if (goal.range != "current" && goal.range != "clean" && goal.range != "expanded") return;
        if (goal.range == request.Range) return;
        Debug.Log($"[Sing/Resolve] sing 省略 range，按已审目标绑定 {request.Range} → {goal.range}；" +
            "refs 与成功条件未变，这不是新的范围选择。");
        request.Range = goal.range;
    }

    // A clip still in quarantine binds revisions="pending". Confirming its source later
    // resolves the same audio to a concrete revision: the recording did not change, and
    // neither did what was promised. Treating the two as different material deadlocks an
    // objective against its own stale binding, so a pending→concrete promotion is the
    // same material here. A real re-cut bumps the revision and still needs re-registering.
    private static bool SameSingingMaterialVersion(string bound, string actual) =>
        bound == actual || (bound == "pending" && actual != "pending" && actual != "missing");

    // Follow that promotion on the stored objective so every later comparison, and the
    // version the character is shown, reflect the material that actually exists.
    private string PromoteResolvedSingingMaterialVersions(string refsCsv, string revisionsCsv)
    {
        if (string.IsNullOrEmpty(refsCsv) || string.IsNullOrEmpty(revisionsCsv)) return revisionsCsv;
        var refs = refsCsv.Split(',').Select(s => s.Trim()).ToArray();
        var bound = revisionsCsv.Split(',').Select(s => s.Trim()).ToArray();
        if (refs.Length != bound.Length) return revisionsCsv;
        var actual = BindCurrentSingingMaterialVersions(refs).Split(',');
        if (actual.Length != refs.Length) return revisionsCsv;
        bool promoted = false;
        for (int i = 0; i < bound.Length; i++)
        {
            if (bound[i] != "pending" || actual[i] == "pending" || actual[i] == "missing") continue;
            bound[i] = actual[i]; promoted = true;
        }
        if (!promoted) return revisionsCsv;
        string resolved = string.Join(",", bound);
        Debug.Log($"[Sing/Goal] 素材来源已确认，目标版本随之解析 {revisionsCsv} → {resolved}；" +
            "refs、范围与成功条件未变，这不是内容改动，也不构成新的演唱授权。");
        return resolved;
    }

    // The rejection names the mechanism but not the material, and change_quote is matched
    // verbatim against exactly this string. Omitting transition defaults to continue, so a
    // genuinely new user request reads as an illegal edit of the old objective and she is
    // left guessing both the transition and the quote. Hand her the text instead.
    private string DescribeSingingGoalChangeQuoteHint()
    {
        string latest = (m_LastUserMsg ?? "").Trim().Replace('\n', ' ').Replace('\r', ' ');
        if (latest.Length == 0) return "";
        if (latest.Length > 140) latest = latest.Substring(0, 140) + "…";
        return " 若这确实是用户提出的新要求，用 transition=\"revise\"，并把 change_quote 设为下面这句话里的逐字片段" +
            "（原样截取，不要加引号或改写）：" + latest;
    }

    // Syntax identifies an intended transition; semantic authority remains the separate
    // review. Missing/invalid edits cannot remove the previously committed objective.
    private bool BindSingingGoalTransition(SingingGoal goal, string attrs, out string failure)
    {
        failure = "";
        goal.transition = ReadSingingGoalChoice(attrs, "transition", goal.intent == "observe" ? "observe" :
            IsActiveSingingGoalUnfinished() ? "continue" : "create");
        goal.changeQuote = (ReadToolAttribute(attrs, "change_quote") ?? "").Trim();
        if (!new[] { "create", "continue", "revise", "replace", "cancel", "observe" }.Contains(goal.transition))
        { failure = "transition无效；原活动任务仍保留，未执行。"; return false; }
        var active = m_ActiveSingingGoal;
        string suppliedBase = ReadToolAttribute(attrs, "base_version");
        goal.baseVersion = active?.version ?? 0;
        if (!string.IsNullOrEmpty(suppliedBase) &&
            (!int.TryParse(suppliedBase, out int parsed) || parsed != goal.baseVersion))
        { failure = "base_version已过期或无效；未覆盖活动目标。请读取active_goal后重新决定。"; return false; }
        if (goal.transition == "observe")
        {
            if (goal.intent != "observe")
            { failure = "observe仅记录反馈，不能声明演唱动作。"; return false; }
            return true;
        }
        if (goal.transition == "create")
        {
            if (IsActiveSingingGoalUnfinished())
            { failure = "仍有未完成active_goal；新草案不能隐式替换其成功条件。请明确continue/revise/replace/cancel。"; return false; }
            return true;
        }
        if (active == null || active.review == "cancelled" || active.outcome == "cancelled")
        { failure = "没有可继续或修改的活动目标；请按实际新要求登记create，未回退到旧录音。"; return false; }
        if (goal.transition == "continue")
        {
            // Resolve a quarantined clip's binding before comparing. Without this the
            // objective is unreachable once its source is confirmed: omitting revisions
            // inherits the stale "pending" and fails material validation, while supplying
            // the resolved value reads as changing the objective. Only the material
            // version moves; refs, range and the success condition are still pinned.
            active.revisions = PromoteResolvedSingingMaterialVersions(active.refs, active.revisions);
            string Supplied(string field) => (ReadToolAttribute(attrs, field) ?? "").Trim();
            bool Different(string field, string bound) => Supplied(field).Length > 0 && Supplied(field) != bound;
            // Copying the objective's pre-promotion "pending" back from an older frame
            // still names the same recording, so it is not a changed objective either.
            bool DifferentRevisions()
            {
                string supplied = Supplied("revisions");
                if (supplied.Length == 0 || supplied == active.revisions) return false;
                var a = supplied.Split(',').Select(s => s.Trim()).ToArray();
                var b = active.revisions.Split(',').Select(s => s.Trim()).ToArray();
                if (a.Length != b.Length) return true;
                for (int i = 0; i < a.Length; i++)
                    if (!SameSingingMaterialVersion(a[i], b[i]) && !SameSingingMaterialVersion(b[i], a[i])) return true;
                return false;
            }
            if (Different("refs", active.refs) || Different("range", active.range) ||
                DifferentRevisions() || Different("expected", active.expected) ||
                Different("completion", active.completion) || Different("intent", active.intent) ||
                Different("repair_scope", active.repairScope) || Different("origin", active.origin) ||
                (Supplied("start_seconds").Length > 0 && !goal.start.Equals(active.start)) ||
                (Supplied("end_seconds").Length > 0 && !goal.end.Equals(active.end)))
            { failure = "continue草案改变了活动目标的refs/顺序/范围/版本/成功条件；原目标完整保留。确有用户新要求才用revise/replace和change_quote，不能将追问缩为最近一段。" +
                DescribeSingingGoalChangeQuoteHint(); return false; }
            if (!IsActiveSingingGoalUnfinished() || active.outcome == "playback_complete_content_unverified")
            { failure = "活动目标已有完整播放事实，不能因追问原样再播。明确重复请求应登记repeat的新目标。"; return false; }
            goal.refs = active.refs; goal.range = active.range; goal.revisions = active.revisions;
            goal.intent = active.intent; goal.completion = active.completion; goal.expected = active.expected;
            goal.repairScope = active.repairScope; goal.start = active.start; goal.end = active.end;
            goal.origin = active.origin; goal.autonomous = active.autonomous; goal.requestQuote = active.requestQuote;
            goal.sourceUserTurn = active.sourceUserTurn;
            return true;
        }
        // User-owned obligations cannot be changed by a model's own promise. A quote
        // is necessary evidence, not a keyword classifier or sufficient approval.
        if (!active.autonomous && (goal.changeQuote.Length == 0 ||
            (m_LastUserMsg ?? "").IndexOf(goal.changeQuote, StringComparison.Ordinal) < 0))
        { failure = "修改/替换/取消用户活动目标须change_quote引用最新用户改变要求，随后独立审查；原任务未丢弃。" +
            DescribeSingingGoalChangeQuoteHint(); return false; }
        if (!active.autonomous && goal.autonomous)
        { failure = "用户事项不能借autonomous改名来取消或缩减；请明确处理用户目标。"; return false; }
        if (goal.transition == "cancel")
        {
            goal.intent = "observe"; goal.refs = ""; goal.revisions = "";
            goal.expected = "取消活动目标"; goal.feedback = "none";
        }
        return true;
    }

    private static string ReadSingingGoalChoice(string attrs, string name, string fallback)
    {
        string value = ReadToolAttribute(attrs, name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
    }

    private void RejectSingingGoal(string failure)
    {
        ClearDeferredSingingRequest();
        m_SingingRejectedSpeechRound = m_CurrentSpeechRoundId;
        // A malformed correction must never silently fall back to a formerly approved target.
        if (m_SingingGoal != null) m_PreviousSingingGoal = m_SingingGoal;
        m_SingingGoal = null;
        RecordPracticeEditFailureForLlm(failure, "invalid_singing_goal", "原活动目标和成功条件仍保留；修正当前提案后再执行。不要默认最近录音，也不要把无效改写当成任务取消。", "sing_goal");
    }

    private static bool SameSingingGoal(SingingGoal a, SingingGoal b)
    {
        return a != null && b != null && a.turn == b.turn &&
            ((a.baseVersion == b.baseVersion && a.transition == b.transition && a.changeQuote == b.changeQuote) ||
                (a.review == "approved" && b.transition == "continue")) && a.origin == b.origin && a.autonomous == b.autonomous && a.refs == b.refs && a.range == b.range &&
            a.revisions == b.revisions && a.intent == b.intent && a.completion == b.completion &&
            a.expected == b.expected && a.feedback == b.feedback && a.evidence == b.evidence && a.repairScope == b.repairScope &&
            a.start.Equals(b.start) && a.end.Equals(b.end);
    }

    // Compare the reviewer's independently extracted user target with the actual
    // proposal. An "approved" label or prose claiming expanded cannot rewrite current.
    private bool TryMatchReviewedSingingGoalTarget(JObject expected, out string reason)
    {
        reason = "";
        var goal = m_SingingGoal;
        if (goal == null || goal.review != "pending")
        { reason = "没有待核对的歌唱草案；审查结果不能批准另一份或已经结束的目标。"; return false; }
        if (expected == null || expected["refs"]?.Type != JTokenType.String)
        { reason = "审查没有提供有效的独立目标refs；没有批准、改写或执行草案。"; return false; }
        string refs = string.Join(",", ((string)expected["refs"] ?? "").Split(',').Select(p => p.Trim()));
        string range = ((string)expected["range"] ?? "").Trim();
        if (goal.baseVersion != (m_ActiveSingingGoal?.version ?? 0))
        { reason = "活动目标版本在审查期间已经变化；旧审核不能覆盖新成功条件。"; return false; }
        if (goal.transition == "continue" &&
            (m_ActiveSingingGoal?.outcome == "playback_complete_content_unverified" || m_ActiveSingingGoal?.outcome == "user_confirmed"))
        { reason = "原目标在续接审核前已完成播放；当前追问不能使它再执行一次。需要重唱须明确新请求。"; return false; }
        if (goal.intent == "observe")
        {
            if (refs.Length == 0 && range == "none") return true;
            reason = $"观察反馈草案没有演唱动作，但审查独立目标为refs={refs}, range={range}；不能把两者当作同一目标。";
            return false;
        }
        string origin = (string)expected["origin"] ?? "";
        string quote = ((string)expected["request_quote"] ?? "").Trim();
        if (origin == "user_request")
        {
            bool inherited = goal.transition == "continue" && m_ActiveSingingGoal?.origin == "user_request" &&
                quote == m_ActiveSingingGoal.requestQuote;
            bool retainedRequest = m_SingingUserInputs.Any(input => input.text.IndexOf(quote, StringComparison.Ordinal) >= 0);
            if (quote.Length == 0 || ((m_LastUserMsg ?? "").IndexOf(quote, StringComparison.Ordinal) < 0 && !inherited && !retainedRequest))
            { reason = "用户演唱任务缺少实际用户请求引文；角色承诺、歌词和素材存在不构成要求角色演唱。"; return false; }
            if (goal.origin == "autonomous" && goal.transition != "create")
            { reason = "既有自主任务不能改称用户请求；须明确核对任务转换，不能混用任务来源。"; return false; }
            if ((goal.transition == "revise" || goal.transition == "replace") && quote != goal.changeQuote)
            { reason = "独立审核的改变要求引文与change_quote不同；不能用失败追问默默替换原目标。"; return false; }
            if (goal.transition == "continue" && m_ActiveSingingGoal != null &&
                refs != m_ActiveSingingGoal.refs)
            { reason = "继续原任务必须满足active_goal的完整有序refs，不能只审核缩水草案。"; return false; }
        }
        else if (origin != "autonomous" || goal.origin != "autonomous")
        { reason = "没有独立确认的用户回唱要求；若角色自主想唱须明确origin=autonomous，不能冒充用户任务。"; return false; }
        // refs 仍然逐字核对——用户确实会说"第一段和第二段"，那是可核对的事实。
        // range 不再由审核裁决：clean/expanded/current/window 是素材内部的选段术语，
        // 用户几乎从不提及，审核只能猜。实测 s18 turn=9：用户原话是"把第一段和第二段
        // 连起来唱"，只字未提范围；审核猜 expanded，而草案里正确的 current（第二段此前
        // 已被修订成 window[0,10.17]，用户刚确认过"这次没问题"）被判不符、强制 revise。
        // 她照审核改成 expanded，第二段的尾部说话随即回来，下一轮用户就说"你又把第二段
        // 我说话的部分复读出来了"。范围该由素材状态决定，不该由对用户原话的猜测决定。
        if (refs != goal.refs)
        {
            reason = $"审查独立解析的用户目标与实际草案不一致：expected_refs={refs}；" +
                $"proposal_refs={goal.refs}。refs顺序必须一致；口头解释不会修改实际草案。";
            return false;
        }
        if (range.Length > 0 && range != goal.range && m_LogAgentLoop)
            Debug.Log($"[Sing/GoalReview] 审核独立解析的 range={range} 与草案 range={goal.range} 不同；" +
                "范围由素材状态决定，不作为否决依据，仅记录。");
        // 坐标是否需要核对，取决于草案本身用的是不是 window，而不是审核猜的 range。
        if (goal.range == "window")
        {
            if (!TryReadReviewedSingingCoordinate(expected["start_seconds"], out double start) ||
                !TryReadReviewedSingingCoordinate(expected["end_seconds"], out double end) ||
                !Finite(goal.start) || !Finite(goal.end) || end <= start)
            { reason = "审查window目标须成对提供有限、有效的原录音起止秒数；没有批准草案。"; return false; }
            if (Math.Abs(start - goal.start) > .001 || Math.Abs(end - goal.end) > .001)
            {
                reason = $"审查独立目标window=[{start.ToString(CultureInfo.InvariantCulture)},{end.ToString(CultureInfo.InvariantCulture)}]" +
                    $" 与实际草案window=[{goal.start.ToString(CultureInfo.InvariantCulture)},{goal.end.ToString(CultureInfo.InvariantCulture)}]不同；未更改音频范围。";
                return false;
            }
        }
        if (!ValidateSingingGoalMaterials(goal, out reason)) return false;
        // A mistaken source label on an initial candidate is mechanical bookkeeping.
        // Only the independent review's actual-user quote plus an exact target match
        // can correct it. No refs, ordering, range, revision or success condition changes.
        goal.originCorrectedFromAutonomous = goal.transition == "create" && goal.origin == "autonomous" && origin == "user_request";
        goal.origin = origin; goal.autonomous = origin == "autonomous"; goal.requestQuote = quote;
        if (origin == "user_request" && goal.sourceUserTurn == 0)
            goal.sourceUserTurn = m_SingingUserInputs.LastOrDefault(input => input.text.IndexOf(quote, StringComparison.Ordinal) >= 0)?.turn ?? m_SingingGoalTurn;
        goal.targetReviewed = true;
        m_SingingGoalOwnerAutonomous = goal.autonomous;
        return true;
    }

    private bool ValidateSingingGoalMaterials(SingingGoal goal, out string reason)
    {
        var errors = new List<string>();
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        if (sense == null) { reason = "当前没有录音素材接口。"; return false; }
        goal.revisions = PromoteResolvedSingingMaterialVersions(goal.refs, goal.revisions);
        var refs = goal.refs.Split(','); var versions = goal.revisions.Split(',');
        for (int i = 0; i < refs.Length; i++)
        {
            var ready = sense.DescribePracticePhrases().Find(p => p.ClipRef == refs[i]);
            var pending = sense.DescribeQuarantinedSingingCandidates().Find(p => p.ClipRef == refs[i]);
            if (ready == null && pending == null) { errors.Add(refs[i] + "不存在"); continue; }
            string actual = ready == null ? "pending" : ready.Revision.ToString(CultureInfo.InvariantCulture);
            if (i >= versions.Length || !SameSingingMaterialVersion(versions[i].Trim(), actual))
                errors.Add(refs[i] + "版本不符，实际revision=" + actual);
            if (goal.range == "current" && ready == null && pending.PlaybackStatus != "ready")
                errors.Add(refs[i] + " current尚未选定；须据真实边界选择clean/expanded/window");
            float start = goal.range == "window" ? goal.start : goal.range == "expanded"
                ? ready?.ExpandedStartSeconds ?? pending.ExpandedStartSeconds : goal.range == "clean"
                ? ready?.CleanStartSeconds ?? pending.CleanStartSeconds : ready != null
                ? (ready.ActiveCapture == "expanded" ? ready.ExpandedStartSeconds : ready.CleanStartSeconds) + ready.ActiveTrimHeadSeconds : pending.CurrentStartSeconds;
            float end = goal.range == "window" ? goal.end : goal.range == "expanded"
                ? ready?.ExpandedEndSeconds ?? pending.ExpandedEndSeconds : goal.range == "clean"
                ? ready?.CleanEndSeconds ?? pending.CleanEndSeconds : ready != null
                ? (ready.ActiveCapture == "expanded" ? ready.ExpandedEndSeconds : ready.CleanEndSeconds) - ready.ActiveTrimTailSeconds : pending.CurrentEndSeconds;
            float limit = ready != null ? Mathf.Max(ready.CleanEndSeconds, ready.ExpandedEndSeconds) : pending.RawSeconds;
            if (!Finite(start) || !Finite(end) || start < 0f || end <= start || end > limit + .001f)
                errors.Add(refs[i] + "目标范围无效或超出留存音频");
        }
        reason = string.Join("；", errors);
        return errors.Count == 0;
    }

    private static bool TryReadReviewedSingingCoordinate(JToken token, out double value)
    {
        value = double.NaN;
        return token != null && (token.Type == JTokenType.Float || token.Type == JTokenType.Integer) &&
            double.TryParse(token.ToString(Formatting.None), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private void ApplySingingGoalReview(string status, string evidence)
    {
        if (m_SingingGoal == null || m_SingingGoal.review != "pending") return;
        if (!new[] { "approved", "revise", "waiting_user", "cancelled" }.Contains(status) || string.IsNullOrWhiteSpace(evidence)) return;
        m_SingingGoal.review = status;
        m_SingingGoal.reviewEvidence = evidence;
        // A line that promised an action the review did not approve stays unspoken.
        if (status != "approved") { ClearDeferredSingingRequest(); ClearWithheldSingingSpeech("审核结果=" + status); }
        if (status == "cancelled" && m_SingingGoal.transition == "cancel" && m_ActiveSingingGoal != null &&
            m_SingingGoal.baseVersion == m_ActiveSingingGoal.version)
        {
            m_ActiveSingingGoal.review = "cancelled";
            m_ActiveSingingGoal.outcome = "cancelled";
            m_ActiveSingingGoal.reviewEvidence = evidence;
            m_SingingGoal.outcome = "cancelled";
        }
        if (status == "revise")
            RecordSingingRejection("goal:" + SingingProposalKey() + SingingMaterialVersionKey(), evidence);
        if (status == "approved")
        {
            if (m_SingingGoal.intent != "observe")
            {
                // Promotion is the single commit point. A malformed/rejected candidate
                // and an unrelated follow-up never overwrite the active success target.
                m_ActiveSingingGoal = m_SingingGoal;
            }
            if (m_SingingGoal.feedback == "unsatisfied" && m_LastSingingGoalPlayback != null)
            {
                m_SingingRepairFeedbackTurn = m_SingingGoalTurn;
                if (!m_RejectedSingingPlans.Contains(m_LastSingingGoalPlayback)) m_RejectedSingingPlans.Add(m_LastSingingGoalPlayback);
                while (m_RejectedSingingPlans.Count > 12) m_RejectedSingingPlans.RemoveAt(0);
            }
            if (m_SingingGoal.feedback == "satisfied" && m_LastSingingGoalPlayback != null && m_LastSingingGoalPlayback.completed)
            {
                m_LastSingingGoalPlayback.userConfirmed = true;
                if (m_SingingGoal.intent == "observe")
                {
                    m_SingingGoal.outcome = "user_confirmed";
                    if (m_ActiveSingingGoal != null && m_LastSingingGoalPlayback.goalVersion == m_ActiveSingingGoal.version &&
                        m_LastSingingGoalPlayback.targetMatches)
                        m_ActiveSingingGoal.outcome = "user_confirmed";
                }
            }
        }
        if (status == "revise" && !m_WorkNoProgress)
            m_WorkProgressFact += "\n[Sing/NextStep] 当前是revise，不是pending：审核已经结束并要求修改草案。根据reviewEvidence与素材事实，在本轮提交修正后的sing_goal；不要先sing。可以只输出工具标签。next/continue只排定思考，不会修改目标，也不会重新审核未修改的草案。若信息不足则明确询问，无法推进则如实说明。\n";
        Debug.Log($"[Sing/GoalReview] turn={m_SingingGoalTurn} version={m_SingingGoal.version} status={status} evidence={evidence}");
    }

    private bool TryValidateSingingGoalPreparation(string refs, out string failure)
    {
        failure = "";
        if (m_SingingGoal == null) return true; // Standalone edits remain explicit tools.
        if (m_SingingGoal.review != "approved")
        { failure = "歌唱目标尚未独立核对，review=" + m_SingingGoal.review + "；没有修改素材。"; return false; }
        if (refs.Split(',').Any(s => !m_SingingGoal.refs.Split(',').Contains(s.Trim())))
        { failure = "准备范围含目标之外的refs；没有修改素材。"; return false; }
        return true;
    }

    private bool TryValidateSingingGoalRequest(AgentHumBackRequest request, SenseVoiceSpeechToText sense, out string failure)
    {
        bool valid = TryValidateSingingGoalRequestDetailed(request, sense, out var detail);
        failure = detail?.message ?? "";
        return valid;
    }

    private bool TryValidateSingingGoalRequestDetailed(AgentHumBackRequest request,
        SenseVoiceSpeechToText sense, out SingingResolutionFailure detail)
    {
        detail = null;
        string failure;
        m_ValidatedSingingRendering = "";
        m_ValidatedSingingMaterials = null;
        // Legacy fixtures and a detached non-agent renderer have no user task contract.
        if (!m_AgentRunning && m_SingingGoal == null && m_SingingGoalTurn == 0) return true;
        var goal = m_SingingGoal;
        if (goal == null)
        {
            // 实测 s13：她在上一个用户轮登记了目标，审核还没回，用户就说了下一句；
            // ResetSingingGoalForUserTurn 把它挪进 m_PreviousSingingGoal 并清空当前轮。
            // 原文案只说"先登记"，对刚登记过的她等于没说，于是连着三轮裸发 sing 撞同一堵墙。
            // 只在那个目标确实属于紧邻的上一轮时说，免得翻出几轮前的旧目标反而误导。
            var discardedGoal = m_PreviousSingingGoal;
            string discarded = discardedGoal == null || discardedGoal.turn != m_SingingGoalTurn - 1 ? ""
                : "你在上一个用户轮登记的 version=" + discardedGoal.version + " 目标" +
                  (discardedGoal.review == "approved"
                      ? "已随那一轮结束"
                      : "在 review=" + discardedGoal.review + " 时因新用户轮到达而作废") +
                  "，它不覆盖当前轮；登记过不等于当前轮有目标。";
            return RejectSingingResolution(out detail, "goal_required", "goal",
                "当前用户轮的歌唱目标未独立核对；先登记sing_goal并等待进度审查。没有确认来源、修改范围或播放。" + discarded,
                "依据原用户请求登记sing_goal；有未完成active_goal时可用transition=continue。素材可播放不代表目标已授权。");
        }
        if (goal.turn != m_SingingGoalTurn)
            return RejectSingingResolution(out detail, "goal_stale_turn", "goal", "该目标属于旧用户轮；没有修改或播放。", "按当前用户轮继续/改变/取消原目标并独立审核。");
        if (goal.review != "approved")
            return RejectSingingResolution(out detail, goal.review == "pending" ? "goal_review_pending" : "goal_review_" + goal.review,
                "goal", "当前用户轮的歌唱目标未独立核对；review=" + goal.review + "；没有确认来源、修改范围或播放。",
                goal.review == "pending" ? "等待独立审核，不重提相同目标或调用执行器。" : "根据reviewEvidence修正草案或询问，未进入音频生成。", goal.review == "pending");
        if (goal.intent == "observe")
        return RejectSingingResolution(out detail, "goal_observation_only", "goal", "当前目标只登记用户反馈，没有演唱目标；不能据此播放旧素材。", "反馈记录没有演唱授权。");
        if (m_ActiveSingingGoal == null || m_ActiveSingingGoal.version != goal.version ||
            goal.refs != m_ActiveSingingGoal.refs || goal.completion != m_ActiveSingingGoal.completion)
        return RejectSingingResolution(out detail, "goal_version_mismatch", "goal", "当前动作没有绑定活动目标的完整成功条件；未执行。", "读取活动目标版本后重新决定，原目标仍保留。");
        if (goal.refs == request.ClipRefs) AdoptReviewedSingingRange(request, goal);
        string requestRange = Finite(request.StartSeconds) ? "window" : request.Range;
        if (goal.refs != request.ClipRefs || goal.range != requestRange ||
            (requestRange == "window" && (Mathf.Abs(goal.start - request.StartSeconds) > .001f || Mathf.Abs(goal.end - request.EndSeconds) > .001f)))
        { failure = $"动作不符合已核对目标：expected_refs={goal.refs} range={goal.range}；actual_refs={request.ClipRefs} range={requestRange}。reason不会改变参数；没有修改或播放。";
            return RejectSingingResolution(out detail, "goal_target_mismatch", "goal", failure, "按已审目标的精确refs/范围执行；需要改变时先重新审核。"); }
        goal.revisions = PromoteResolvedSingingMaterialVersions(goal.refs, goal.revisions);
        var versions = goal.revisions.Split(',');
        var refs = request.ClipRefs.Split(',');
        var preview = new List<SingingMaterialFact>();
        for (int i = 0; i < refs.Length; i++)
        {
            var phrase = sense.DescribePracticePhrases().Find(p => p.ClipRef == refs[i]);
            var pending = sense.DescribeQuarantinedSingingCandidates().Find(p => p.ClipRef == refs[i]);
            string actualVersion = phrase == null ? "pending" : phrase.Revision.ToString(CultureInfo.InvariantCulture);
            if (!SameSingingMaterialVersion(versions[i].Trim(), actualVersion))
            { failure = $"目标版本已过期：{refs[i]} expected_revision={versions[i]} actual_revision={actualVersion}；请根据新事实更新目标，未修改或播放。";
                return RejectSingingResolution(out detail, "material_version_mismatch", "material", failure, "保持原任务范围，核对最新素材版本后重新审核。"); }
            if (phrase == null && pending == null) return RejectSingingResolution(out detail, "material_reference_missing", "material", "目标素材已不存在；未修改或播放。", "报告缺失事实，不自动选另一份录音。");
            float start = phrase != null ? (phrase.ActiveCapture == "expanded" ? phrase.ExpandedStartSeconds : phrase.CleanStartSeconds) + phrase.ActiveTrimHeadSeconds : pending.CurrentStartSeconds;
            float end = phrase != null ? (phrase.ActiveCapture == "expanded" ? phrase.ExpandedEndSeconds : phrase.CleanEndSeconds) - phrase.ActiveTrimTailSeconds : pending.CurrentEndSeconds;
            if (requestRange == "clean") { start = phrase != null ? phrase.CleanStartSeconds : pending.CleanStartSeconds; end = phrase != null ? phrase.CleanEndSeconds : pending.CleanEndSeconds; }
            if (requestRange == "expanded") { start = phrase != null ? phrase.ExpandedStartSeconds : pending.ExpandedStartSeconds; end = phrase != null ? phrase.ExpandedEndSeconds : pending.ExpandedEndSeconds; }
            if (requestRange == "window") { start = request.StartSeconds; end = request.EndSeconds; }
            preview.Add(new SingingMaterialFact { clip = refs[i], revision = phrase?.Revision ?? -1, start = start, end = end });
        }
        if (goal.outcome == "playback_complete_content_unverified" || goal.outcome == "user_confirmed")
        return RejectSingingResolution(out detail, "goal_already_played", "playback", "该目标版本已经完整播放；追问不会重复执行。明确要求重唱时登记repeat的新目标。", "区分未听到反馈与新重复授权；原播放事实保持。");
        // Only a separately reviewed new user request for repetition permits a rejected plan.
        bool authorizedRepeat = goal.intent == "repeat" && m_SingingRepairFeedbackTurn != goal.turn && goal.feedback != "unsatisfied";
        string rendering = JsonConvert.SerializeObject(new { request.PitchPlan, request.Transpose, request.Key,
            request.PitchTarget, request.PitchDelta, request.PitchMatch, request.Pace, request.Expression });
        if (!authorizedRepeat && m_RejectedSingingPlans.Any(p => SameSingingMaterials(p.materials, preview) &&
            (goal.repairScope != "music" || p.rendering == rendering)))
        { failure = "这组refs和原录音范围与用户明确反馈未修好的方案相同（单独增加revision不算内容改变）；没有原样重播。请核查保留音频、调整实际范围/音乐参数或询问；改写reason/再次确认不构成修复。";
            return RejectSingingResolution(out detail, "unchanged_rejected_plan", "content", failure, "核对实际未满足的内容和范围，不能原样重复被否定方案。"); }
        goal.outcome = "validated_not_submitted";
        m_ValidatedSingingRendering = rendering;
        m_ValidatedSingingMaterials = preview;
        return true;
    }

    private static bool SameSingingMaterials(List<SingingMaterialFact> a, List<SingingMaterialFact> b)
    {
        if (a == null || b == null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            // Bumping a revision without changing the retained source window is not a
            // content repair. Version freshness is checked separately before this test.
            if (a[i].clip != b[i].clip ||
                Mathf.Abs(a[i].start - b[i].start) > .001f || Mathf.Abs(a[i].end - b[i].end) > .001f) return false;
        return true;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private bool BindPreparedSingingGoalMaterials(SenseVoiceSpeechToText sense, out string failure)
    {
        failure = "";
        if (m_ValidatedSingingMaterials == null) return true;
        var ready = sense.DescribePracticePhrases();
        var bound = new List<SingingMaterialFact>();
        foreach (var planned in m_ValidatedSingingMaterials)
        {
            var actual = ready.Find(p => p.ClipRef == planned.clip);
            if (actual == null) { failure = "准备后缺少目标素材；没有开始播放。"; return false; }
            bound.Add(new SingingMaterialFact { clip = actual.ClipRef, revision = actual.Revision,
                start = (actual.ActiveCapture == "expanded" ? actual.ExpandedStartSeconds : actual.CleanStartSeconds) + actual.ActiveTrimHeadSeconds,
                end = (actual.ActiveCapture == "expanded" ? actual.ExpandedEndSeconds : actual.CleanEndSeconds) - actual.ActiveTrimTailSeconds });
        }
        if (!SameSingingMaterials(m_ValidatedSingingMaterials, bound))
        { failure = "准备后素材范围与已核对的完整目标不一致；没有开始播放。"; return false; }
        // Range selection may legitimately issue a new material revision. Bind that
        // actual revision once, before asynchronous synthesis/playback can observe it.
        m_ValidatedSingingMaterials = bound;
        return true;
    }

    private void CaptureSingingGoalPlayback(SenseVoiceSpeechToText sense, List<int> indices)
    {
        var goal = m_SingingGoal;
        var evidence = new SingingPlaybackEvidence { turn = m_SingingGoalTurn, goalVersion = goal?.version ?? -1,
            goalRefs = goal?.refs ?? "", expected = goal?.expected ?? "", rendering = m_ValidatedSingingRendering };
        var phrases = sense.DescribePracticePhrases();
        foreach (int index in indices ?? new List<int>())
        {
            var p = phrases.Find(item => item.Index == index);
            if (p == null) { m_PendingSingingGoalPlayback = null; return; }
            evidence.materials.Add(new SingingMaterialFact { clip = p.ClipRef, revision = p.Revision,
                start = (p.ActiveCapture == "expanded" ? p.ExpandedStartSeconds : p.CleanStartSeconds) + p.ActiveTrimHeadSeconds,
                end = (p.ActiveCapture == "expanded" ? p.ExpandedEndSeconds : p.CleanEndSeconds) - p.ActiveTrimTailSeconds });
        }
        evidence.targetMatches = goal != null && m_ActiveSingingGoal?.version == goal.version &&
            m_ActiveSingingGoal.refs == string.Join(",", evidence.materials.Select(p => p.clip)) &&
            SameSingingMaterials(m_ValidatedSingingMaterials, evidence.materials) &&
            m_ValidatedSingingMaterials.Select(p => p.revision).SequenceEqual(evidence.materials.Select(p => p.revision));
        m_PendingSingingGoalPlayback = evidence;
        if (goal != null) goal.outcome = "prepared_not_played";
    }

    private void CompleteSingingGoalPlayback(bool practice, bool completed)
    {
        var evidence = m_PendingSingingGoalPlayback;
        m_PendingSingingGoalPlayback = null;
        if (!practice || evidence == null) return;
        evidence.completed = completed;
        m_LastSingingGoalPlayback = evidence;
        var goal = m_ActiveSingingGoal;
        if (goal == null || goal.turn != evidence.turn || goal.version != evidence.goalVersion) return;
        goal.outcome = !completed ? "interrupted_or_failed" : evidence.targetMatches
            ? "playback_complete_content_unverified" : "playback_goal_mismatch";
        Debug.Log("[Sing/GoalOutcome] " + JsonConvert.SerializeObject(new { goal.version, goal.outcome,
            actual = evidence.materials, content_satisfied = "unverified", user_heard = "unverified" }));
    }
}
