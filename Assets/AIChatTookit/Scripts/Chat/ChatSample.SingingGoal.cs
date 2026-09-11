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

    private SingingGoal m_SingingGoal, m_PreviousSingingGoal;
    private SingingPlaybackEvidence m_PendingSingingGoalPlayback, m_LastSingingGoalPlayback;
    private readonly List<SingingPlaybackEvidence> m_RejectedSingingPlans = new List<SingingPlaybackEvidence>();
    private int m_SingingGoalTurn, m_SingingGoalVersion;
    private int m_SingingRepairFeedbackTurn = -1;
    private string m_ValidatedSingingRendering = "";
    private List<SingingMaterialFact> m_ValidatedSingingMaterials;
    private bool? m_SingingGoalOwnerAutonomous;

    private void ResetSingingGoalForUserTurn()
    {
        if (m_SingingGoal != null) m_PreviousSingingGoal = m_SingingGoal;
        m_SingingGoal = null;
        m_ValidatedSingingMaterials = null;
        m_SingingGoalOwnerAutonomous = null;
        m_SingingGoalTurn++;
        // Pending playback is fenced by its original turn, and retained until its real callback.
        // The last heard plan survives a follow-up such as "why?" or "check your skills".
    }

    private void ClearSingingGoalSession()
    {
        m_SingingGoal = m_PreviousSingingGoal = null;
        m_PendingSingingGoalPlayback = m_LastSingingGoalPlayback = null;
        m_RejectedSingingPlans.Clear(); m_SingingRepairFeedbackTurn = -1;
        m_ValidatedSingingRendering = ""; m_ValidatedSingingMaterials = null;
        m_SingingGoalOwnerAutonomous = null;
        m_SingingGoalTurn++; m_SingingGoalVersion++;
    }

    private const string SingingGoalContract =
        "\n[歌唱事项闭环] 先区分用户已发生的演唱、要求角色执行的动作与你的自主提议。用户唱过、分享歌词、说自己试唱都不构成回唱任务；可以只自然回应，不必登记目标。" +
        "确有角色演唱目标时才登记；origin=user_request 表示用户要求你唱，origin=autonomous 表示你自己的提议，不得记成用户欠办事项。为什么/检查技能等追问不自动取消已核对的原目标。" +
        "演唱前提交 <sing_goal origin=\"user_request\" refs=\"clip:真实身份,clip:真实身份\" range=\"current\" revisions=\"0,0\" intent=\"perform\" completion=\"playback\" expected=\"实际要唱的内容及顺序\" feedback=\"none\"/>。" +
        "refs顺序就是目标顺序；revision从事实复制，候选为pending；range=current/clean/expanded/window，window须成对start_seconds/end_seconds。" +
        "window仅是goal的范围类型：真正sing只填写成对start_seconds/end_seconds，不填写range=window或其它range。" +
        "intent=perform/repair/repeat；repair_scope=content/music/delivery（默认content，音高/速度修正用music）；completion=playback/user_confirmation；补漏内容须user_confirmation，播完不能证明缺失内容已经补回。" +
        "用户负面反馈用feedback=unsatisfied；明确认可用satisfied；这些反馈或repeat须evidence引用最新用户原话。" +
        "只记录反馈可用intent=observe且不填refs。不得把自己的道歉/声称成功作为用户反馈。" +
        "目标先由独立语义审查核对，approved后才能sing或修改其范围；同一回复登记后直接sing将被暂停并返回事实，不会偷偷执行。" +
        "已approved且用户未更新时，无需重写目标；改变refs/range/revision/完成条件需重新审查。" +
        "reason只是解释，expanded必须写入range。目标与真正工具参数都要落实用户修正，不能只把二者填成同一个错误方案。\n";

    private string BuildSingingGoalContext()
    {
        return "\n[Sing/Goal] " + JsonConvert.SerializeObject(new
        {
            user_turn = m_SingingGoalTurn,
            latest_user_request = TruncateForFrame(m_LastUserMsg ?? "", 3000),
            input_scope = "原始用户输入，不保证是执行请求；按完整时序区分用户表演与要求角色行动",
            goal = SingingGoalObservation(m_SingingGoal),
            previous_goal = SingingGoalObservation(m_PreviousSingingGoal),
            last_playback = m_LastSingingGoalPlayback,
            rejected_plans = m_RejectedSingingPlans,
            feedback_scope = "completed仅表示Unity实际播完所列refs/revision/raw范围；不证明用户听到或指定歌词/缺失内容满足。当前goal须独立语义审查，旧结果不能满足新目标。"
        }) + "\n";
    }

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
        if (!new[] { "perform", "repair", "repeat", "observe" }.Contains(goal.intent) ||
            !new[] { "none", "unsatisfied", "satisfied" }.Contains(goal.feedback) ||
            !new[] { "playback", "user_confirmation" }.Contains(goal.completion) ||
            !new[] { "content", "music", "delivery" }.Contains(goal.repairScope) ||
            !new[] { "current", "clean", "expanded", "window" }.Contains(goal.range))
        { RejectSingingGoal("目标枚举无效；未登记、修订或播放。"); return; }
        if ((goal.feedback != "none" || goal.intent == "repeat") &&
            (goal.evidence.Length == 0 || !(m_LastUserMsg ?? "").Contains(goal.evidence)))
        { RejectSingingGoal("用户反馈/重复演唱意图须引用最新用户原话作为evidence，再由独立语义审查核对；角色自己的话不能代替用户证据。"); return; }
        if (goal.intent != "observe")
        {
            if (goal.expected.Length == 0 || goal.refs.Length == 0 || goal.revisions.Length == 0)
            { RejectSingingGoal("目标须明确refs、revisions和expected；不能默认全部或最近一段。"); return; }
            string[] refs = goal.refs.Split(',').Select(s => s.Trim()).ToArray();
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
        m_SingingGoalOwnerAutonomous = goal.autonomous;
        m_SingingGoal = goal;
        RecordPracticeEditResult("歌唱目标已登记，review=pending；未修订/播放。需独立核对最新用户要求、refs顺序、范围、版本及反馈，再决定行动。", false);
        Debug.Log("[Sing/Goal] proposal=" + JsonConvert.SerializeObject(goal));
    }

    private static string ReadSingingGoalChoice(string attrs, string name, string fallback)
    {
        string value = ReadToolAttribute(attrs, name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
    }

    private void RejectSingingGoal(string failure)
    {
        m_SingingRejectedSpeechRound = m_CurrentSpeechRoundId;
        // A malformed correction must never silently fall back to a formerly approved target.
        if (m_SingingGoal != null) m_PreviousSingingGoal = m_SingingGoal;
        m_SingingGoal = null;
        RecordPracticeEditFailureForLlm(failure, "invalid_singing_goal", "重新依据最新用户完整语义登记sing_goal；不要执行旧目标。", "sing_goal");
    }

    private static bool SameSingingGoal(SingingGoal a, SingingGoal b)
    {
        return a != null && b != null && a.turn == b.turn && a.origin == b.origin && a.autonomous == b.autonomous && a.refs == b.refs && a.range == b.range &&
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
        if (expected == null || expected["refs"]?.Type != JTokenType.String || expected["range"]?.Type != JTokenType.String)
        { reason = "审查没有提供有效的独立目标refs/range；没有批准、改写或执行草案。"; return false; }
        string refs = string.Join(",", ((string)expected["refs"] ?? "").Split(',').Select(p => p.Trim()));
        string range = ((string)expected["range"] ?? "").Trim();
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
            bool inherited = m_PreviousSingingGoal?.origin == "user_request" &&
                m_PreviousSingingGoal.review == "approved" && quote == m_PreviousSingingGoal.requestQuote;
            if (quote.Length == 0 || ((m_LastUserMsg ?? "").IndexOf(quote, StringComparison.Ordinal) < 0 && !inherited))
            { reason = "用户演唱任务缺少实际用户请求引文；角色承诺、歌词和素材存在不构成要求角色演唱。"; return false; }
            if (goal.origin == "autonomous")
            { reason = "草案是自主提议，审核却称用户请求；须分别核对，不能混用任务来源。"; return false; }
        }
        else if (origin != "autonomous" || goal.origin != "autonomous")
        { reason = "没有独立确认的用户回唱要求；若角色自主想唱须明确origin=autonomous，不能冒充用户任务。"; return false; }
        if (refs != goal.refs || range != goal.range)
        {
            reason = $"审查独立解析的用户目标与实际草案不一致：expected_refs={refs} expected_range={range}；" +
                $"proposal_refs={goal.refs} proposal_range={goal.range}。refs顺序必须一致；口头解释不会修改实际草案。";
            return false;
        }
        if (range == "window")
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
        goal.origin = origin; goal.autonomous = origin == "autonomous"; goal.requestQuote = quote;
        return true;
    }

    private bool ValidateSingingGoalMaterials(SingingGoal goal, out string reason)
    {
        var errors = new List<string>();
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        if (sense == null) { reason = "当前没有录音素材接口。"; return false; }
        var refs = goal.refs.Split(','); var versions = goal.revisions.Split(',');
        for (int i = 0; i < refs.Length; i++)
        {
            var ready = sense.DescribePracticePhrases().Find(p => p.ClipRef == refs[i]);
            var pending = sense.DescribeQuarantinedSingingCandidates().Find(p => p.ClipRef == refs[i]);
            if (ready == null && pending == null) { errors.Add(refs[i] + "不存在"); continue; }
            string actual = ready == null ? "pending" : ready.Revision.ToString(CultureInfo.InvariantCulture);
            if (i >= versions.Length || versions[i] != actual)
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
        if (status == "revise")
            RecordSingingRejection("goal:" + SingingProposalKey() + SingingMaterialVersionKey(), evidence);
        if (status == "approved")
        {
            if (m_SingingGoal.feedback == "unsatisfied" && m_LastSingingGoalPlayback != null)
            {
                m_SingingRepairFeedbackTurn = m_SingingGoalTurn;
                if (!m_RejectedSingingPlans.Contains(m_LastSingingGoalPlayback)) m_RejectedSingingPlans.Add(m_LastSingingGoalPlayback);
                while (m_RejectedSingingPlans.Count > 12) m_RejectedSingingPlans.RemoveAt(0);
            }
            if (m_SingingGoal.feedback == "satisfied" && m_LastSingingGoalPlayback != null && m_LastSingingGoalPlayback.completed)
            {
                m_LastSingingGoalPlayback.userConfirmed = true;
                if (m_SingingGoal.intent == "observe") m_SingingGoal.outcome = "user_confirmed";
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
        failure = "";
        m_ValidatedSingingRendering = "";
        m_ValidatedSingingMaterials = null;
        // Legacy fixtures and a detached non-agent renderer have no user task contract.
        if (!m_AgentRunning && m_SingingGoal == null && m_SingingGoalTurn == 0) return true;
        var goal = m_SingingGoal;
        if (goal == null || goal.turn != m_SingingGoalTurn || goal.review != "approved")
        { failure = "当前用户轮的歌唱目标未独立核对；先登记sing_goal并等待进度审查。没有确认来源、修改范围或播放。"; return false; }
        if (goal.intent == "observe")
        { failure = "当前目标只登记用户反馈，没有演唱目标；不能据此播放旧素材。"; return false; }
        string requestRange = Finite(request.StartSeconds) ? "window" : request.Range;
        if (goal.refs != request.ClipRefs || goal.range != requestRange ||
            (requestRange == "window" && (Mathf.Abs(goal.start - request.StartSeconds) > .001f || Mathf.Abs(goal.end - request.EndSeconds) > .001f)))
        { failure = $"动作不符合已核对目标：expected_refs={goal.refs} range={goal.range}；actual_refs={request.ClipRefs} range={requestRange}。reason不会改变参数；没有修改或播放。"; return false; }
        var versions = goal.revisions.Split(',');
        var refs = request.ClipRefs.Split(',');
        var preview = new List<SingingMaterialFact>();
        for (int i = 0; i < refs.Length; i++)
        {
            var phrase = sense.DescribePracticePhrases().Find(p => p.ClipRef == refs[i]);
            var pending = sense.DescribeQuarantinedSingingCandidates().Find(p => p.ClipRef == refs[i]);
            string actualVersion = phrase == null ? "pending" : phrase.Revision.ToString(CultureInfo.InvariantCulture);
            if (versions[i] != actualVersion)
            { failure = $"目标版本已过期：{refs[i]} expected_revision={versions[i]} actual_revision={actualVersion}；请根据新事实更新目标，未修改或播放。"; return false; }
            if (phrase == null && pending == null) { failure = "目标素材已不存在；未修改或播放。"; return false; }
            float start = phrase != null ? (phrase.ActiveCapture == "expanded" ? phrase.ExpandedStartSeconds : phrase.CleanStartSeconds) + phrase.ActiveTrimHeadSeconds : pending.CurrentStartSeconds;
            float end = phrase != null ? (phrase.ActiveCapture == "expanded" ? phrase.ExpandedEndSeconds : phrase.CleanEndSeconds) - phrase.ActiveTrimTailSeconds : pending.CurrentEndSeconds;
            if (requestRange == "clean") { start = phrase != null ? phrase.CleanStartSeconds : pending.CleanStartSeconds; end = phrase != null ? phrase.CleanEndSeconds : pending.CleanEndSeconds; }
            if (requestRange == "expanded") { start = phrase != null ? phrase.ExpandedStartSeconds : pending.ExpandedStartSeconds; end = phrase != null ? phrase.ExpandedEndSeconds : pending.ExpandedEndSeconds; }
            if (requestRange == "window") { start = request.StartSeconds; end = request.EndSeconds; }
            preview.Add(new SingingMaterialFact { clip = refs[i], revision = phrase?.Revision ?? -1, start = start, end = end });
        }
        // Only a separately reviewed new user request for repetition permits a rejected plan.
        bool authorizedRepeat = goal.intent == "repeat" && m_SingingRepairFeedbackTurn != goal.turn && goal.feedback != "unsatisfied";
        string rendering = JsonConvert.SerializeObject(new { request.PitchPlan, request.Transpose, request.Key,
            request.PitchTarget, request.PitchDelta, request.PitchMatch, request.Pace, request.Expression });
        if (!authorizedRepeat && m_RejectedSingingPlans.Any(p => SameSingingMaterials(p.materials, preview) &&
            (goal.repairScope != "music" || p.rendering == rendering)))
        { failure = "这组refs和原录音范围与用户明确反馈未修好的方案相同（单独增加revision不算内容改变）；没有原样重播。请核查保留音频、调整实际范围/音乐参数或询问；改写reason/再次确认不构成修复。"; return false; }
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
        evidence.targetMatches = goal != null && goal.refs == string.Join(",", evidence.materials.Select(p => p.clip)) &&
            SameSingingMaterials(m_ValidatedSingingMaterials, evidence.materials);
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
        var goal = m_SingingGoal;
        if (goal == null || goal.turn != evidence.turn || goal.version != evidence.goalVersion) return;
        goal.outcome = !completed ? "interrupted_or_failed" : evidence.targetMatches
            ? "playback_complete_content_unverified" : "playback_goal_mismatch";
        Debug.Log("[Sing/GoalOutcome] " + JsonConvert.SerializeObject(new { goal.version, goal.outcome,
            actual = evidence.materials, content_satisfied = "unverified", user_heard = "unverified" }));
    }
}
