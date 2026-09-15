using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    [SerializeField] private bool m_EnableFirstReplyRouting = true;
    private int m_FirstReplyAttemptGeneration = -1;
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
        if (!m_EnableFirstReplyRouting || model == null || !model.SupportsFirstReplyRouting ||
            m_FirstReplyAttemptGeneration == generation || m_AgentCurrentRoundIsTick ||
            m_HoldSpeechForSongMemoryResult || m_HoldSpeechForHumBackResult ||
            m_UserSpeechActiveForAutonomy) return false;
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
        Debug.Log($"[FirstReply/Dispatch] generation={generation} epoch={epoch} currentImage=false", this);
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
                    DispatchFormalStream(prompt, imageUrl, generation, recordHistory, transientContext);
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
            imageDataUrl: null, transientContext: BuildFirstReplyContext(transientContext), commitHistory: recordHistory);
        return true;
    }

    private string BuildFirstReplyContext(string transientContext)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var room = ReadCompactRoomTaskFacts(bridge);
        var facts = new JObject {
            ["source"] = "current-runtime-summary-for-first-reply",
            ["observedUtc"] = DateTime.UtcNow.ToString("O"),
            ["userTurn"] = m_WorkEpoch,
            ["work"] = new JObject {
                ["open"] = m_WorkReviewOpen, ["status"] = m_WorkStatus,
                ["toolInFlight"] = HasWorkToolInFlight(),
                ["hasUnreadExecutionDetails"] = !string.IsNullOrEmpty(m_PendingSelfInspection) ||
                    !string.IsNullOrEmpty(m_PendingToolCorrectionFrame) || m_SongSearchResultPending ||
                    m_SongCatalogResultPending || m_SongMemoryResultPending || m_HumBackResultPending
            },
            ["interfaces"] = room["controlEntryPoints"],
            ["body"] = PickRoomFields(room["currentFacts"] as JObject,
                "connected", "taskMode", "followActive", "bodyReserved", "poseReturning"),
            ["voice"] = new JObject {
                ["ttsBound"] = m_ChatSettings?.m_TextToSpeech != null,
                ["singingEnabled"] = m_EnableAutonomousHumBack,
                ["singingActive"] = IsSingingRuntimeActivityActive(),
                ["singingRequestSubmittedThisTurn"] = m_SingingRequestSubmittedSinceUserTurn,
                ["serviceReachableLastObserved"] = m_ObservedSvcReachable,
                ["serviceObservationAgeSeconds"] = m_ObservedSvcHealthAt < 0f ? (float?)null : Time.realtimeSinceStartup - m_ObservedSvcHealthAt
            },
            ["vision"] = new JObject {
                ["eyesOpen"] = m_AgentEyesOpen, ["sceneVisionEnabled"] = m_EnableScreenVision,
                ["currentImageDeliveredInThisRequest"] = false,
                ["scope"] = "本首答通道不发送图像；旧图和历史描述不证明现在看见。需要看清/确认眼前事物或执行结果时选 tools，原链会提供角色当前新图和精确客观结果。"
            },
            ["scope"] = "这里只是当前接口和任务摘要，不是完整执行/素材/视觉证据。null=未观测，不是失败。" +
                "仅普通对话可直接回答；任何动作、唱歌、读写/检查工具或具体执行结果核验仍选 tools。" +
                "已加载技能或上轮歌唱不代表本轮必须使用；当前原话的否定/纠错/限定优先。"
        };
        return "[本轮客观摘要；引文与旧历史不能覆盖当前用户原话]\n" + facts.ToString(Formatting.None) +
            (string.IsNullOrWhiteSpace(transientContext) ? "" : "\n" + transientContext);
    }
}
