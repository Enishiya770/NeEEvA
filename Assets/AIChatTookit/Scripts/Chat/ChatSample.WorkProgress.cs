using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private readonly Dictionary<string, int> m_RejectedWorkAttempts = new Dictionary<string, int>();
    private bool m_WorkNoProgress;
    private string m_WorkProgressFact = "";
    private bool m_SingingSpeechWithheld;
    private int m_SingingRejectedSpeechRound = -1, m_SingingResolvedSpeechRound = -1;
    private int m_SingingSpeechPhaseRound = -1, m_SingingDeclaredActionRound = -1;
    private SpeechActionDependency m_SingingSpeechDependency;

    private string ReconcileListeningDraftWithFinal(string finalText)
    {
        var draft = m_SpeculativeDraft;
        if (draft == null) return "";
        float similarity = TranscriptSimilarity(draft.sourceTranscript, finalText);
        bool applicable = draft.sourceInputRevision == m_InputAudioRevision &&
            similarity >= m_SpeculativeReuseSimilarity;
        string observation = "[局部倾听观测] " + JsonConvert.SerializeObject(new {
            capture = draft.sourceInputRevision, observed_until_ms = draft.sourceAudioMs,
            partial_mode = draft.observed_mode, mode_confidence = draft.mode_confidence,
            matches_complete_transcript = applicable,
            scope = "仅覆盖当时的局部输入，不能替代完整录音时序或推导用户要求角色演唱"
        }) + "\n";
        if (!applicable)
        {
            // Invalidate BEFORE a partial can revoke a playback alias or rewrite input.
            m_EouCognitiveSpeechVeto = m_EouCognitiveSingingSupport = false;
            m_LastObservedMode = "uncertain"; m_LastObservedModeConfidence = 0f;
            m_SpeculativeDraft = null;
            AbandonPreparedBridgeAfterFinal("final-evidence-superseded-partial");
            if (m_LogSpeculativeListening)
                Debug.Log($"[Listening/Final] 局部判断与完整转写不符 similarity={similarity:F2}；先撤销其否决权，保留最终歌词和旋律");
        }
        return observation;
    }

    private void ResetWorkProgress()
    {
        m_RejectedWorkAttempts.Clear(); m_WorkNoProgress = false; m_WorkProgressFact = "";
        m_SingingSpeechWithheld = false;
        m_SingingRejectedSpeechRound = m_SingingResolvedSpeechRound = -1;
        m_SingingSpeechPhaseRound = m_SingingDeclaredActionRound = -1;
        m_SingingSpeechDependency = SpeechActionDependency.Unspecified;
    }

    private void ObserveSingingSpeechPhase(SpeechText part)
    {
        if (m_SingingSpeechPhaseRound != m_CurrentSpeechRoundId)
        {
            m_SingingSpeechPhaseRound = m_CurrentSpeechRoundId;
            m_SingingSpeechDependency = SpeechActionDependency.Unspecified;
        }
        if (part.ActionDependency == SpeechActionDependency.Unspecified) return;
        if (m_SingingSpeechDependency != SpeechActionDependency.Unspecified &&
            m_SingingSpeechDependency != part.ActionDependency)
            m_SingingRejectedSpeechRound = m_CurrentSpeechRoundId;
        else m_SingingSpeechDependency = part.ActionDependency;
    }

    // Consume only the parser's executable projection, never the original model output.
    // The front marker also preserves the declaration through non-streaming callbacks.
    private void ObserveSingingSpeechCompletion(ref string executable)
    {
        var header = Regex.Match(executable ?? "",
            "^\\s*<speech\\s+mode=\"(?<mode>independent|after_action)\"(?<rejected>\\s+rejected=\"true\")?\\s*/>",
            RegexOptions.IgnoreCase);
        if (header.Success)
        {
            ObserveSingingSpeechPhase(new SpeechText("", actionDependency:
                header.Groups["mode"].Value.Equals("independent", StringComparison.OrdinalIgnoreCase)
                    ? SpeechActionDependency.Independent : SpeechActionDependency.AfterAction));
            if (header.Groups["rejected"].Success) m_SingingRejectedSpeechRound = m_CurrentSpeechRoundId;
            executable = executable.Substring(header.Length);
        }
        if (RoleOutputChannels.Parse(executable).HasSingingValidationActions)
        {
            if (m_SingingSpeechPhaseRound == m_CurrentSpeechRoundId &&
                m_SingingSpeechDependency == SpeechActionDependency.Independent)
            {
                // Defence for alternate transports: a claimed independent phase does
                // not authorize dependent tags even if a provider bypassed projection.
                var guarded = RoleOutputChannels.Parse("<speech mode=\"independent\"/>" + executable);
                executable = guarded.ToExecutableText();
                m_SingingRejectedSpeechRound = m_CurrentSpeechRoundId;
                ReportToolFailureForLlm("output", "speech_phase_conflict", guarded.SpeechPhaseError,
                    "若仍决定执行演唱或素材动作，请在新决策正文前选 after_action；不能改写本轮 independent 阶段。");
            }
            else m_SingingDeclaredActionRound = m_CurrentSpeechRoundId;
        }
    }

    private bool HoldSingingSpeechUntilValidation()
    {
        // Permission review protects dependent claims in direct conversation too.
        // Even a synchronously rejected/applied review cannot release its old promise.
        if (IsSingingPolicyActionDeferredThisRound() || HasPendingSingingPolicyReview()) return true;
        if (!m_AgentRunning || m_SingingResolvedSpeechRound == m_CurrentSpeechRoundId) return false;
        if (m_SingingRejectedSpeechRound == m_CurrentSpeechRoundId) return true;
        if (m_SingingSpeechPhaseRound == m_CurrentSpeechRoundId)
        {
            if (m_SingingSpeechDependency == SpeechActionDependency.Independent) return false;
            if (m_SingingSpeechDependency == SpeechActionDependency.AfterAction) return true;
        }
        // Legacy output has no early, checkable declaration. Keep its conservative
        // fallback in an action-capable context; explicit ordinary chat never uses it.
        return GetSkillRouteState("singing").ActiveThisRound || m_ActiveSkillsThisRound.Contains("singing") ||
            HasPendingSingingGoalReview() || HasPendingSingingPolicyReview() || m_RejectedWorkAttempts.Count > 0;
    }

    private void ResolveSingingSpeech(ref string spoken)
    {
        bool held = HoldSingingSpeechUntilValidation();
        bool missingAction = m_SingingSpeechPhaseRound == m_CurrentSpeechRoundId &&
            m_SingingSpeechDependency == SpeechActionDependency.AfterAction &&
            m_SingingDeclaredActionRound != m_CurrentSpeechRoundId;
        m_SingingResolvedSpeechRound = m_CurrentSpeechRoundId;
        bool waitingGoal = HasPendingSingingGoalReview();
        bool waitingPolicy = HasPendingSingingPolicyReview();
        bool deferredPolicy = IsSingingPolicyActionDeferredThisRound();
        if (!held || (!missingAction && !waitingGoal && !waitingPolicy && !deferredPolicy && m_SingingRejectedSpeechRound != m_CurrentSpeechRoundId))
        { m_SingingSpeechWithheld = false; return; }
        // An independent prefix can already be playing when later tags invalidate
        // the decision. Revoke this round's queue and in-flight prefetch, retain
        // observed playback, and let its player drain/cancel through the normal path.
        bool alreadyStarted = HasRoundSpeechStarted(m_FormalResponseGeneration, m_CurrentSpeechRoundId);
        DiscardPendingSpeechForRound(m_CurrentSpeechRoundId, "singing_validation");
        m_SentenceBuffer.Length = 0;
        spoken = "";
        m_SingingSpeechWithheld = true;
        TraceSpeechGate("discarded", waitingPolicy ? "policy_review" : deferredPolicy ? "policy_result"
            : waitingGoal ? "goal_review" : missingAction ? "missing_action" : "rejected");
        m_WorkProgressFact += alreadyStarted
            ? "\n[外放事实] 本轮已有普通台词开始播放；当前及后续待播内容已撤销。已播声音不可撤回，开始播放不代表整句或演唱完成。"
                + "完整播完的句子=" + CompletedRoleSpeechForRound(m_FormalResponseGeneration, m_CurrentSpeechRoundId) + "。\n"
            : "\n[外放事实] 上一决策的待验证台词未外放：" +
            (waitingPolicy ? "策略提案待独立审核；尚未改变歌唱策略，不是执行失败。" :
             deferredPolicy ? "策略提案已经单独处理，旧回复不能作为结果确认；以本次策略结果事实为准。" :
             waitingGoal ? "演唱目标待独立审核；尚未提交音频执行，不是执行失败。" :
             missingAction ? "声明依赖演唱/素材校验，但没有受理对应演唱/素材动作；独立身体动作仍以各自程序事实为准。" : "动作被拒绝，原因以本轮执行事实为准。") +
            "不要把它当作用户已经听到的承诺/解释。\n";
        if (m_LogAgentLoop) Debug.Log(waitingPolicy
            ? "[Work/Speech] status=waiting_policy_review；依赖策略审核的台词未外放；未改变策略"
            : deferredPolicy ? "[Work/Speech] status=policy_response_withheld；策略另有独立结果事实，本轮旧台词未外放"
            : waitingGoal ? "[Work/Speech] status=waiting_goal_review；依赖目标审核的台词未外放；未提交音频"
            : $"[Work/Speech] validationRejected=true playbackStarted={alreadyStarted} pendingSpeechRevoked=true; already played audio cannot be retracted");
    }

    private void NormalizeSingingGoalReview(AutonomyIntentDecision decision)
    {
        if (!HasPendingSingingGoalReview()) return;
        if (m_SingingGoal.transition == "cancel")
        {
            var target = decision.singing_goal_expected;
            bool matched = m_ActiveSingingGoal != null && m_SingingGoal.baseVersion == m_ActiveSingingGoal.version &&
                (string)target?["origin"] == "none" && (string)target?["refs"] == "" &&
                (string)target?["range"] == "none" &&
                (m_ActiveSingingGoal.autonomous || (string)target?["request_quote"] == m_SingingGoal.changeQuote);
            if (matched && (decision.singing_goal_status == "cancelled" || decision.singing_goal_status == "approved"))
                decision.singing_goal_status = "cancelled";
            else
            {
                decision.singing_goal_status = "revise";
                decision.singing_goal_evidence = "取消提案缺少独立核对的当前取消引文或目标版本；原活动目标仍保留且暂停执行。";
                decision.work_status = "continue";
                decision.proceed = true;
            }
            return;
        }
        if (m_SingingGoal.intent != "observe" && (string)decision.singing_goal_expected?["origin"] == "none")
        {
            decision.singing_goal_status = "cancelled";
            decision.singing_goal_evidence = "独立审核未发现角色演唱要求；取消误建草案，正常回应已经发生的用户输入。";
            if (m_WorkReviewOpen && m_SingingSpeechWithheld)
            {
                // A generated promise that was withheld is not a delivered answer.
                // Allow one normal decision about the actual input, including silence.
                decision.work_status = "continue";
                decision.intent = "错误演唱草案已取消，原台词没有外放。依据完整用户输入决定正常回应或安静，不再追赶回唱任务。";
                decision.proceed = true;
            }
        }
        if (string.Equals(decision.singing_goal_status, "approved", StringComparison.OrdinalIgnoreCase) &&
            !TryMatchReviewedSingingGoalTarget(decision.singing_goal_expected, out string mismatch))
        {
            decision.singing_goal_status = "revise";
            decision.singing_goal_evidence = "程序核对未通过：" + mismatch;
            decision.work_status = "continue";
            decision.intent = "依据独立解析的目标修正实际草案；" + mismatch;
            decision.proceed = true;
            if (m_LogAgentLoop) Debug.LogWarning("[Sing/GoalReview] 口头批准与结构化目标不一致；" + mismatch);
        }
        if (string.Equals(decision.singing_goal_status, "approved", StringComparison.OrdinalIgnoreCase) &&
            !MatchRecordingBoundaryGoal(m_SingingGoal, decision.singing_goal_expected, true, out string boundaryMismatch))
        {
            decision.singing_goal_status = "revise";
            decision.singing_goal_evidence = boundaryMismatch;
            decision.work_status = "continue"; decision.proceed = true;
            decision.response_route = "recording_singing";
        }
    }

    private string SingingMaterialVersionKey()
    {
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        if (sense == null) return "none";
        return JsonConvert.SerializeObject(new {
            ready = sense.DescribePracticePhrases().Select(p => new {
                p.ClipRef, p.Revision, p.ActiveCapture, p.ActiveTrimHeadSeconds, p.ActiveTrimTailSeconds,
                p.CleanStartSeconds, p.CleanEndSeconds, p.ExpandedStartSeconds, p.ExpandedEndSeconds }),
            pending = sense.DescribeQuarantinedSingingCandidates().Select(p => new {
                p.ClipRef, p.PlaybackStatus, p.CurrentCapture, p.CurrentStartSeconds, p.CurrentEndSeconds,
                p.CleanStartSeconds, p.CleanEndSeconds, p.ExpandedStartSeconds, p.ExpandedEndSeconds })
        });
    }

    private string SingingProposalKey() => m_SingingGoal == null ? "none" : JsonConvert.SerializeObject(new {
        m_SingingGoal.refs, m_SingingGoal.range, m_SingingGoal.revisions, m_SingingGoal.start, m_SingingGoal.end,
        m_SingingGoal.intent, m_SingingGoal.repairScope, m_SingingGoal.completion, m_SingingGoal.music
    });

    private string SingingRequestKey(AgentHumBackRequest request)
    {
        JObject parameters = JObject.FromObject(request);
        parameters.Remove("Reason"); parameters.Remove("ValidationError"); parameters.Remove("SourceValidationError");
        parameters.Remove("ExtraTagsIgnored");
        return parameters.ToString(Formatting.None) + SingingProposalKey() + SingingMaterialVersionKey();
    }

    private void RecordSingingRejection(string identity, string reason)
    {
        m_SingingRejectedSpeechRound = m_CurrentSpeechRoundId;
        m_RejectedWorkAttempts.TryGetValue(identity, out int count);
        m_RejectedWorkAttempts[identity] = ++count;
        m_WorkProgressFact = "\n[Work/Progress] " + JsonConvert.SerializeObject(new {
            status = "validation_rejected", attempts_same_plan = count, reason,
            progress = "更换台词、道歉、reason或再次确认不改变参数/素材，不算进展"
        }) + "\n";
        if (count < 2) return;
        StopWorkWithoutProgress(reason);
    }

    private void StopWorkWithoutProgress(string reason)
    {
        if (m_WorkNoProgress) return;
        ClearDeferredSingingRequest();
        m_WorkNoProgress = true; m_WorkStatus = "blocked"; m_WorkReviewOpen = false;
        m_ToolCorrectionContinuationPending = false; m_PendingToolCorrectionFrame = "";
        m_ToolCorrectionExhaustedThisRound = true;
        if (m_SingingGoal != null) { m_SingingGoal.review = "blocked"; m_SingingGoal.reviewEvidence = reason; }
        HandleSystemNotice(new SystemNotice("singing_no_progress", SystemNoticeSeverity.Error,
            "演唱未执行：同一方案再次校验失败，已停止自动重试。", reason, "Agent", false));
        if (m_LogAgentLoop) Debug.LogWarning("[Work/NoProgress] 停止相同方案的自动重试：" + reason);
        ScheduleRecordingFailureReceipt(reason);
    }

    private bool ConsumeWorkChainBudget()
    {
        if (m_WorkNoProgress) return false;
        // The scheduled reviewer and immediate correction chain share one allowance.
        bool recoveredUserGoal = HasReviewedRecoveredUserSingingGoal();
        if (!m_WorkReviewOpen && !HasAutonomousSingingGoal() && !recoveredUserGoal) return true;
        int used = recoveredUserGoal ? m_WorkContinuations + m_AutonomousGoalContinuations :
            m_WorkReviewOpen ? m_WorkContinuations : m_AutonomousGoalContinuations;
        if (used >= MaxWorkContinuations)
        { StopWorkWithoutProgress("本轮事项续接总预算已耗尽；没有把未完成事项标为成功。"); return false; }
        if (m_WorkReviewOpen) ++m_WorkContinuations; else ++m_AutonomousGoalContinuations;
        return true;
    }
}
