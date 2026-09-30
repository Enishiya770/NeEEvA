using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    [SerializeField] private bool m_EnableFirstReplyRouting = true;
    [SerializeField, Tooltip("Realtime voice selects ordinary speech or a structured action handoff within one streaming first reply. It skips the extra RoleDecision request, never the action planner.")]
    private bool m_BypassFirstReplyRoutingInRealtime = true;
    private int m_FirstReplyAttemptGeneration = -1;
    // Set only when an accepted user turn arrives with the ASR raw-input channel.
    // A later typed repetition must not inherit the previous capture identity.
    private bool m_CurrentUserInputHasAudioEvidence;
    private int m_RoleDecisionAttentionGeneration = -1;
    private int m_RoleDecisionInFlightGeneration = -1;
    // The player owns failure completion even before its first PCM arrives.
    // IsAISpeaking is deliberately false during synthesis, so it cannot express
    // this ownership. The generation prevents late work from closing a new turn.
    private int m_FirstReplyFailureDrainGeneration = -1;

    // The role chooses ordinary/tools. No word list in the application decides
    // whether a negated request, reference or conversation needs an action.
    private bool TryDispatchFirstReply(string prompt, string imageUrl, int generation,
        bool recordHistory, string transientContext)
    {
        var model = m_ChatSettings?.m_ChatModel;
        if (!m_EnableFirstReplyRouting || model == null ||
            m_FirstReplyAttemptGeneration == generation || m_AgentCurrentRoundIsTick ||
            m_HoldSpeechForSongMemoryResult || m_HoldSpeechForHumBackResult ||
            m_UserSpeechActiveForAutonomy) return false;
        if (TryDispatchFastTaskLoop(model, prompt, imageUrl, generation, recordHistory, transientContext)) return true;
        bool realtimeStream = ShouldBypassFirstReplyRoutingInRealtime() && model.SupportsFirstReplyRouting;
        if (model.SupportsRoleDecisionMessages && !realtimeStream)
            return TryDispatchRoleDecision(model, prompt, imageUrl, generation, recordHistory, transientContext);
        if (!model.SupportsFirstReplyRouting) return false;
        m_FirstReplyAttemptGeneration = generation;
        int epoch = m_WorkEpoch;
        bool consumed = false;
        bool publicDeltaDelivered = false;
        bool Current() => !consumed && generation == m_FormalResponseGeneration &&
            epoch == m_WorkEpoch && isActiveAndEnabled && !m_UserSpeechActiveForAutonomy &&
            ReferenceEquals(model, m_ChatSettings?.m_ChatModel);

        PublishSpokenPrefixToLlm();
        m_FormalResponseInFlight = true;
        var started = Time.realtimeSinceStartup;
        Debug.Log($"[FirstReply/Dispatch] generation={generation} epoch={epoch} realtime={realtimeStream} currentImage={!string.IsNullOrEmpty(imageUrl)}", this);
        model.PostFirstReplyStream(prompt,
            part => {
                if (!Current() || string.IsNullOrEmpty(part.Text)) return;
                if (!string.IsNullOrWhiteSpace(part.Text)) publicDeltaDelivered = true;
                OnSpeechStreamDelta(part);
            },
            result => {
                if (!Current()) return;
                consumed = true;
                Debug.Log($"[FirstReply/Result] generation={generation} route={result?.Disposition.ToString() ?? "Failed"} " +
                    $"seconds={Time.realtimeSinceStartup-started:F3} publicDelta={result?.SpeechDelivered == true} failure={result?.Failure}", this);
                if (result?.Disposition == FirstReplyDisposition.Ordinary)
                {
                    // The provider returns public runs, never raw output. Project
                    // only this public text into the existing completion pipeline.
                    var channels = RoleOutputChannels.Parse(result.SafeOutput, RoleOutputBoundaryPolicy.ExplicitPublicSay);
                    if (!channels.HasInvalidPublicBoundary && !channels.HasActions && !channels.HasAnyActionTag &&
                        channels.HasSpeech && !channels.HasInvalidSpeechPhase && !channels.HasInvalidLanguage &&
                        !channels.HasUndeclaredSpeech && channels.SpeechDependency == SpeechActionDependency.Independent)
                    {
                        m_FormalResponseInFlight = false;
                        // This narrow context did not consume the full observation
                        // tree. Pending tool facts stay pending for the normal path.
                        ClearStagedFormalObservation();
                        OnStreamComplete(channels.ToExecutableText(false, true));
                        return;
                    }
                    result = FirstReplyCompletion.Failed("first-reply-completion-boundary-rejected", result.SpeechDelivered);
                }
                if (!publicDeltaDelivered && result?.SpeechDelivered != true)
                {
                    // The same original user turn enters the existing planner once.
                    // The provider commits neither a tools route nor a failed probe.
                    m_FormalResponseInFlight = false;
                    bool recording = result?.Disposition == FirstReplyDisposition.RecordingSinging &&
                        model.SupportsRecordingSingingDecision && !IsActiveSingingGoalUnfinished();
                    Debug.Log($"[FirstReply/Handoff] generation={generation} route={result?.Disposition.ToString() ?? "Failed"} " +
                        $"recordingDecision={recording} roomPlannerSkipped={recording}; no action submitted by route", this);
                    DispatchFormalStream(prompt, imageUrl, generation, recordHistory, transientContext,
                        skipRoomPlanner: recording, recordingSinging: recording);
                    return;
                }

                // A broken stream after public deltas is not a silent route probe.
                // Drop the unplayed queue, but let the already validated sentence
                // owned by the TTS player drain. Marking this round silenced makes
                // both players exit before their normal ownership-release path;
                // OnStreamComplete then sees IsAISpeaking=true and waits forever.
                // This does not accept the malformed completion or replay tools.
                m_FirstReplyFailureDrainGeneration = generation;
                m_SentenceBuffer.Length = 0;
                m_PendingChunks.Clear();
                m_PendingClips.Clear();
                m_FormalResponseInFlight = false;
                m_PendingToolCorrectionFrame = "[首答流未正常完成] " + (result?.Failure ?? "missing-completion") +
                    "；本轮未执行工具，未提交完整答复。到错误发生前已经完整播放的句子：" + m_AssistantHeardText +
                    "。当前已验证句子可能仍在合成或播放，之后以实际播放记录为准；排队的剩余内容已撤销。" +
                    "后续应依据本轮原话和事实处理未完成事项，不要重复已听前缀或宣称动作完成。";
                Debug.LogWarning("[FirstReply/Incomplete] " + m_PendingToolCorrectionFrame, this);
                OnStreamComplete("<silent/>");
            },
            imageDataUrl: imageUrl, transientContext: BuildFirstReplyContext(transientContext), commitHistory: recordHistory);
        return true;
    }

    /// <summary>
    /// Prefer the same-request streaming route in realtime. This bypasses only
    /// the auxiliary RoleDecision call; a tools route still enters the original
    /// structured planner before any dependent speech or execution.
    /// </summary>
    private bool ShouldBypassFirstReplyRoutingInRealtime()
    {
        if (!m_BypassFirstReplyRoutingInRealtime) return false;
        ResolveRealtimeSpeechHandler();
        bool handlerState = m_RTSpeechHandler != null &&
            (m_RTSpeechHandler.IsRealtimeEnabled || m_RTSpeechHandler.IsRealtimeClosing);
        bool turnMarker = m_RealtimeFormalTurnGeneration == m_FormalResponseGeneration;
        if (m_LogStreamTimings)
        {
            Debug.Log($"[Realtime/FastPathCheck] generation={m_FormalResponseGeneration} " +
                $"turnMarker={turnMarker} handlerNull={m_RTSpeechHandler == null} " +
                $"ownerMatch={m_RTSpeechHandler != null && m_RTSpeechHandler.ChatOwner == this} " +
                $"enabled={m_RTSpeechHandler != null && m_RTSpeechHandler.IsRealtimeEnabled} " +
                $"closing={m_RTSpeechHandler != null && m_RTSpeechHandler.IsRealtimeClosing}");
        }
        return turnMarker || handlerState;
    }

    /// <summary>
    /// Compact structured fast lane. It decides only ordinary speech versus a
    /// handoff to the existing planner. A successful ordinary decision is
    /// compiled back into the legacy speech envelope before it reaches TTS or
    /// the action parser; no new execution authority is introduced here.
    /// </summary>
    private bool TryDispatchRoleDecision(LLM model, string prompt, string imageUrl, int generation,
        bool recordHistory, string transientContext)
    {
        m_FirstReplyAttemptGeneration = generation;
        int epoch = m_WorkEpoch;
        int visualEpoch = m_SceneVisionEpoch;
        bool consumed = false;
        bool publicDeltaDelivered = false;
        bool Current() => !consumed && generation == m_FormalResponseGeneration &&
            epoch == m_WorkEpoch && visualEpoch == m_SceneVisionEpoch && !m_SceneVisionApplicationPaused &&
            isActiveAndEnabled && !m_UserSpeechActiveForAutonomy &&
            ReferenceEquals(model, m_ChatSettings?.m_ChatModel);

        PublishSpokenPrefixToLlm();
        m_FormalResponseInFlight = true;
        m_RoleDecisionInFlightGeneration = generation;
        var started = Time.realtimeSinceStartup;
        var trace = m_ResponseTrace?.Child("llm-role-decision");
        var contextTimer = System.Diagnostics.Stopwatch.StartNew();
        string compactPrompt = BuildFirstReplyContext(transientContext) +
            "\n[本轮用户原话；必须完整保留其否定、指代和尾句]\n" + (prompt ?? "");
        trace?.Mark("role_decision.context_ready", new JObject {
            ["elapsedMilliseconds"] = contextTimer.Elapsed.TotalMilliseconds,
            ["scope"] = "local runtime-summary construction before provider send; not model inference" });
        int imageCharacters = string.IsNullOrEmpty(imageUrl) ? 0 : imageUrl.Length;
        trace?.Mark("role_decision.dispatch", new JObject {
            ["generation"] = generation, ["epoch"] = epoch,
            ["promptCharacters"] = compactPrompt.Length, ["imageSent"] = !string.IsNullOrEmpty(imageUrl),
            ["imageCharacters"] = imageCharacters,
            ["scope"] = "compact route/ordinary decision; no tool authority"
        });
        Debug.Log($"[RoleDecision/Dispatch] generation={generation} epoch={epoch} promptChars={compactPrompt.Length} imageSent={!string.IsNullOrEmpty(imageUrl)} imageChars={imageCharacters}", this);

        model.PostRoleDecisionMsg(compactPrompt, imageUrl, (accepted, legacy, status) => {
            if (!Current()) return;
            consumed = true;
            m_RoleDecisionInFlightGeneration = -1;
            trace?.Mark(accepted ? "role_decision.callback_accepted" : "role_decision.callback_rejected",
                new JObject { ["status"] = status ?? "", ["compiledCharacters"] = legacy == null ? 0 : legacy.Length,
                    ["elapsedSeconds"] = (double)(Time.realtimeSinceStartup - started) });
            Debug.Log($"[RoleDecision/Result] generation={generation} status={status ?? "rejected"} " +
                $"seconds={Time.realtimeSinceStartup - started:F3} chars={(legacy ?? "").Length}", this);

            if (accepted && TryApplyRoleDecisionAttention(model, model.AcceptedRoleDecision,
                prompt, generation, epoch, recordHistory, transientContext, trace)) return;

            if (accepted && string.Equals(status, "tools", StringComparison.Ordinal))
            {
                // The original user turn is handed to the existing planner.
                // This call is fenced by m_FirstReplyAttemptGeneration, so the
                // role lane cannot recursively probe the same generation.
                m_FormalResponseInFlight = false;
                var decision = model.AcceptedRoleDecision;
                bool recording = decision?.Route == "recording_singing" && model.SupportsRecordingSingingDecision &&
                    !IsActiveSingingGoalUnfinished();
                DispatchFormalStream(prompt, imageUrl, generation, recordHistory, transientContext,
                    skipRoomPlanner: recording, recordingSinging: recording);
                return;
            }

            if (accepted && !string.IsNullOrEmpty(legacy))
            {
                var parts = new List<SpeechText>();
                var projected = new RoleOutputChannels(parts.Add, RoleOutputBoundaryPolicy.ExplicitPublicSay,
                    onDiagnostic: name => trace?.Mark(name),
                    onSilentSpeech: part => trace?.Mark("speech.silent_segment", new JObject {
                        ["channel"] = "thought", ["tts"] = false,
                        ["characters"] = part.Text == null ? 0 : part.Text.Length
                    }));
                projected.Push(legacy);
                projected.Finish();
                bool silentDecision = !projected.HasSpeech && legacy.IndexOf("<silent/>", StringComparison.Ordinal) >= 0;
                bool valid = !projected.HasInvalidPublicBoundary && !projected.HasActions &&
                    !projected.HasAnyActionTag && !projected.HasInvalidSpeechPhase &&
                    !projected.HasInvalidLanguage && !projected.HasUndeclaredSpeech &&
                    (projected.HasSpeech || silentDecision) &&
                    (!projected.HasSpeech || projected.SpeechDependency == SpeechActionDependency.Independent);
                if (valid && model.AcceptedRoleDecision?.Route == "cancel_work")
                    valid = ApplyRoleWorkCancellation(model.AcceptedRoleDecision.UserEvidence);
                if (valid)
                {
                    if (recordHistory)
                    {
                        // RoleDecision transport is deliberately history-free;
                        // commit the accepted semantic projection only now.
                        if (model.m_DataList.Count == 0 || model.m_DataList[model.m_DataList.Count - 1] == null ||
                            model.m_DataList[model.m_DataList.Count - 1].role != "user" ||
                            model.m_DataList[model.m_DataList.Count - 1].content != prompt)
                            model.m_DataList.Add(new LLM.SendData("user", prompt ?? ""));
                        model.m_DataList.Add(new LLM.SendData("assistant", legacy));
                        model.CheckHistory();
                    }
                    m_FormalResponseInFlight = false;
                    ClearStagedFormalObservation();
                    foreach (var part in parts)
                    {
                        if (!string.IsNullOrWhiteSpace(part.Text)) publicDeltaDelivered = true;
                        OnSpeechStreamDelta(part);
                    }
                    if (silentDecision) OnStreamDelta("<silent/>");
                    OnStreamComplete(projected.ToExecutableText(false, true));
                    return;
                }
                status = "compiled-boundary-rejected";
            }

            if (!publicDeltaDelivered)
            {
                // A transport or ordinary validation failure has no public
                // delivery. Route-shaped outputs are converted to status=tools
                // by the wire parser and still reach the planner above. A
                // genuinely invalid ordinary response goes straight to formal
                // speech so it does not pay for a speculative room HTTP call.
                m_FormalResponseInFlight = false;
                trace?.Mark("formal.fallback", new JObject {
                    ["reason"] = "role-decision-invalid",
                    ["status"] = status ?? "",
                    ["roomPlannerSkipped"] = true,
                    ["scope"] = "no valid route proposal; original user turn remains the authority"
                });
                DispatchFormalStream(prompt, imageUrl, generation, recordHistory, transientContext,
                    roomTaskPrepared: false, skipRoomPlanner: true);
                return;
            }
            m_FormalResponseInFlight = false;
            OnStreamComplete("<silent/>");
        }, m_ResponseTrace?.Child("role-decision-request"));
        return true;
    }

    // Scene reset can happen after an image was captured while its role request
    // is still in flight. Cancel that owner as well as pending capture owners.
    private void CancelRoleDecisionForSceneVisionChange(string reason)
    {
        if (m_RoleDecisionInFlightGeneration < 0 || m_RoleDecisionInFlightGeneration != m_FormalResponseGeneration) return;
        m_RoleDecisionInFlightGeneration = -1;
        m_ChatSettings?.m_ChatModel?.CancelRoleDecisionMsg();
        ++m_FormalResponseGeneration;
        m_FormalResponseInFlight = false;
        m_StreamComplete = true;
        m_TTSSenderDone = true;
        m_AgentRoundInFlight = false;
        ClearStagedFormalObservation();
        m_ResponseTrace?.Mark("role_decision.cancelled", new JObject { ["reason"] = reason,
            ["scope"] = "visual owner invalidated; old decision cannot publish or apply attention" });
    }

    private bool TryApplyRoleDecisionAttention(LLM model, RoleDecision decision, string prompt,
        int generation, int epoch, bool recordHistory, string context, ConversationTrace trace)
    {
        if (decision == null || decision.Attention == "unchanged") return false;
        bool Current() => generation == m_FormalResponseGeneration && epoch == m_WorkEpoch &&
            isActiveAndEnabled && !m_UserSpeechActiveForAutonomy &&
            ReferenceEquals(model, m_ChatSettings?.m_ChatModel);
        if (!Current()) return true;
        string failure = m_RoleDecisionAttentionGeneration == generation ? "attention-already-reconsidered" :
            !decision.HasBoundAttentionEvidence(m_LastUserMsg) ? "attention-evidence-not-current-input" : null;
        if (failure == null)
        {
            m_RoleDecisionAttentionGeneration = generation;
            if (!ApplySceneVisionAttention(decision.Attention == "visual_on")) failure = "attention-unavailable";
        }
        if (failure != null)
        {
            trace?.Mark("role_decision.attention_handoff", new JObject { ["reason"] = failure,
                ["attention"] = decision.Attention, ["scope"] = "no public promise; original input goes to existing planner" });
            m_FormalResponseInFlight = false;
            DispatchFormalStream(prompt, ResolveFormalSceneImage(generation), generation, recordHistory,
                context + "\n[视觉操作结果] " + failure + "；没有新增观测，不得凭草稿宣称已看见。");
            return true;
        }
        int visualEpoch = m_SceneVisionEpoch;
        bool resumed = false;
        m_FormalResponseInFlight = true;
        void Resume(NeEEvA.Perception.SceneVisionObservation observation)
        {
            if (resumed || !Current() || visualEpoch != m_SceneVisionEpoch) return;
            resumed = true;
            m_PendingSceneFormalCaptureGeneration = -1;
            BindFormalSceneObservation(observation, generation);
            trace?.Mark("role_decision.attention_applied", new JObject { ["attention"] = decision.Attention,
                ["generation"] = generation, ["captureId"] = observation?.CaptureId ?? "",
                ["image"] = observation?.HasImage == true,
                ["scope"] = "visual state applied; speech awaits one same-turn decision with actual observation" });
            // Do not re-enter the first-reply selector or change the user turn.
            // The pre-operation draft is discarded, including any visual claims.
            TryDispatchRoleDecision(model, prompt, ResolveFormalSceneImage(generation), generation,
                recordHistory, context + BuildSceneObservationContext(observation) +
                "\n[同轮视觉操作已处理] attention应为unchanged；按当前状态和实际附件决定回复或任务交接。没有附件不是看见黑暗。");
        }
        if (decision.Attention == "visual_on")
        {
            m_PendingSceneFormalCaptureGeneration = generation;
            RequestSceneObservation("role-attention-applied", Resume);
        }
        else Resume(null);
        return true;
    }

    private string BuildFirstReplyContext(string transientContext)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var room = ReadCompactRoomTaskFacts(bridge);
        var currentRecording = AcceptedCurrentRecordingFacts();
        var facts = new JObject {
            ["source"] = "current-runtime-summary-for-first-reply",
            ["recentDialogue"] = BuildRecentRoleDialogue(),
            ["observedUtc"] = DateTime.UtcNow.ToString("O"),
            ["userTurn"] = m_WorkEpoch,
            ["spokenPrefix"] = new JObject {
                ["text"] = m_ChatSettings?.m_ChatModel?.SpokenPrefix ?? "",
                ["alreadyPlayed"] = !string.IsNullOrWhiteSpace(m_ChatSettings?.m_ChatModel?.SpokenPrefix),
                ["instruction"] = "如果已有文本，视为本轮已经播放的开场；继续回应，不要重复它。"
            },
            ["work"] = new JObject {
                ["open"] = m_WorkReviewOpen, ["status"] = m_WorkStatus,
                ["toolInFlight"] = HasWorkToolInFlight(),
                ["hasUnreadExecutionDetails"] = !string.IsNullOrEmpty(m_PendingSelfInspection) ||
                    !string.IsNullOrEmpty(m_PendingToolCorrectionFrame) || m_SongSearchResultPending ||
                    m_SongCatalogResultPending || m_SongMemoryResultPending || m_HumBackResultPending
            },
            ["interfaces"] = room["controlEntryPoints"],
            ["body"] = PickRoomFields(room["currentFacts"] as JObject,
                "connected", "taskMode", "followActive", "bodyReserved", "poseReturning",
                "currentTargetRevision", "lastAttemptTargetRevision", "lastAttemptAppliesToCurrentTarget",
                "attemptStatus", "activeActionId", "observedAtRealtime", "currentlyNearAndFacingUser"),
            ["roomExecution"] = room["execution"],
            ["lastRoomActionResult"] = room["lastActionResults"],
            ["voice"] = new JObject {
                ["ttsBound"] = m_ChatSettings?.m_TextToSpeech != null,
                ["singingEnabled"] = m_EnableAutonomousHumBack,
                ["singingActive"] = IsSingingRuntimeActivityActive(),
                ["capability"] = "已有录音回唱/跟唱工具。缺少执行结果不等于不能唱；先路由，再核对具体素材和服务。",
                ["materialCount"] = (m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText)?.DescribePracticePhrases().Count ?? 0,
                ["candidateCount"] = (m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText)?.DescribeQuarantinedSingingCandidates().Count ?? 0,
                ["currentRecording"] = currentRecording == null ? null : JToken.FromObject(currentRecording),
                ["singingRequestSubmittedThisTurn"] = m_SingingRequestSubmittedSinceUserTurn,
                ["serviceReachableLastObserved"] = m_ObservedSvcReachable,
                ["serviceObservationAgeSeconds"] = m_ObservedSvcHealthAt < 0f ? (float?)null : Time.realtimeSinceStartup - m_ObservedSvcHealthAt
            },
            ["vision"] = new JObject {
                ["eyesOpen"] = SceneEyesOpen, ["sceneVisionEnabled"] = m_EnableScreenVision,
                ["currentImageDeliveredInThisRequest"] = HasFreshLatestSceneVisionImageForPrompt(),
                ["latestSummary"] = GetLatestSceneVisionSummaryForPrompt(),
                ["latestSummaryCaptureId"] = GetLatestSceneVisionSummaryCaptureIdForPrompt(),
                ["latestSummaryAgeSeconds"] = GetLatestSceneVisionSummaryAgeSecondsForPrompt(),
                ["scope"] = "本轮若有新鲜角色视野图像，首答请求会直接附图；持续视觉摘要只是加速索引，图片仍是最终证据。没有图像或摘要时不能补写具体场景事实。"
            },
            ["scope"] = "这里只是当前接口和任务摘要，不是完整执行/素材/视觉证据。null=未观测，不是失败。" +
                "仅普通对话可直接回答；现有录音的内容/讲话歌声边界检查、裁剪及回唱全部选recording_singing；空间行为或独立于该录音处理的其他工具目标选tools，混合目标保留全部步骤。" +
                "已加载技能或上轮歌唱不代表本轮必须使用；当前原话的否定/纠错/限定优先。"
        };
        return "[本轮客观摘要；引文与旧历史不能覆盖当前用户原话]\n" + facts.ToString(Formatting.None) +
            (string.IsNullOrWhiteSpace(transientContext) ? "" : "\n" + transientContext);
    }
}
