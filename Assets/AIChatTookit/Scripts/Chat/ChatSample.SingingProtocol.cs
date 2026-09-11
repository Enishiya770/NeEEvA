using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

// The model-facing protocol does not expose the renderer's echo/practice modes.
// Legacy tags remain adapters, not a second set of instructions for new replies.
public partial class ChatSample
{
    private float m_LastSingingRepairAt = -99999f;
    // Immutable preparation snapshot is published only after real completion.
    // Revisions, drops and later failed requests cannot rewrite what was heard.
    private string m_PendingPracticePlaybackFacts = "";
    private string m_LastCompletedPracticePlaybackFacts = "";
    private string m_LatestSingingOutcome = "none";
    private bool m_SingingRequestSubmittedSinceUserTurn;

    private string BuildSingingExecutionSnapshot()
    {
        bool active = IsSingingRuntimeActivityActive();
        string state = m_HumBackPlaying ? "playing" : m_SongSingInFlight ? "locating_library_audio" :
            m_HumStreamWaitForTextOutputDrain && m_HumStreamReadyClips.Count > 0 ? "buffered_waiting_for_speech" :
            m_HumBackPreparingCarrier || m_ActiveSVSRequest != null || m_ActiveHumSVCRequest != null ||
            m_HumStreamProducerCoroutine != null ? "preparing" : m_HumBackPending ? "queued" :
            active ? "active" : "idle";
        return $"\n[Sing/Execution] request_submitted_since_user_turn={m_SingingRequestSubmittedSinceUserTurn.ToString().ToLowerInvariant()} " +
            $"work_active={active.ToString().ToLowerInvariant()} state={state} " +
            $"continuations_without_work={m_NoSingingWorkContinuations}\n" +
            "以上只表示程序执行状态；提交过不等于成功，idle不表示素材丢失。口头说准备好、look、continue都不会启动演唱。是否行动、询问或安静仍由你决定。\n" + BuildSingingGoalContext();
    }

    private string BuildSingingClipOverview(SenseVoiceSpeechToText sense)
    {
        var ready = sense.DescribePracticePhrases();
        var pending = sense.DescribeQuarantinedSingingCandidates();
        var rows = ready.Select(p => new { p.ClipRef, p.RecordingSequence,
            Source = p.PendingConfirmation ? "pending" : "confirmed_user", Playback = "ready", p.Seconds, p.Lyrics })
            .Concat(pending.Select(p => new { p.ClipRef, p.RecordingSequence, Source = p.SourceStatus,
                Playback = p.PlaybackStatus, p.Seconds,
                Lyrics = string.IsNullOrWhiteSpace(p.SingingSegmentText) ? p.Lyrics : p.SingingSegmentText }))
            .OrderBy(p => p.RecordingSequence).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"\n[Sing/Inventory] retained={rows.Count} confirmed_list={rows.Count(p => p.Source == "confirmed_user")} pending_list={rows.Count(p => p.Source == "pending")}；以下按录音先后并列，不是播放顺序。来源待确认不等于没有录到。");
        sb.AppendLine("最近留存的录音证据=" + (rows.LastOrDefault()?.ClipRef ?? "none") + "；只是录音时间事实，不替你选择或确认来源。");
        sb.AppendLine("playback=ready仅表示音频可执行，不是歌唱判定；结合整段转写、声学证据和语境选择或询问。shared_prefix表示联合识别复用了前段音频，不是又唱了一次，也不证明是同一说话人。");
        sb.AppendLine("录音先后（早→晚；不是角色播放顺序）见下列 recording 编号；不是练唱清单编号。");
        for (int i = 0; i < rows.Count; i++)
        {
            var p = rows[i];
            string lyric = TruncateForFrame(p.Lyrics, 64).Replace('\n', ' ').Replace('\r', ' ');
            sb.AppendLine($"[Sing/Clip] recording={p.RecordingSequence} ref={p.ClipRef} source={p.Source} playback={p.Playback} audio={p.Seconds:F2}s lyrics={lyric}" +
                sense.DescribeSingingClipObservation(p.ClipRef));
            string latest = sense.DescribeLatestSingingRecordingEvidence(p.ClipRef);
            if (!string.IsNullOrEmpty(latest)) sb.AppendLine(latest);
        }
        sb.AppendLine("[Sing/PlaybackFact] 角色最近完整播放（位置从左至右1起；不是清单编号）last_completed=" +
            (m_LastCompletedPracticePlaybackFacts.Length > 0 ? m_LastCompletedPracticePlaybackFacts : "none") +
            "; latest_outcome=" + m_LatestSingingOutcome);
        sb.AppendLine("清单中的每个引用只对应该录音；某次播放未列出的引用不在那次完整播放里。pending 可由你结合语境确认，也可先询问；程序不自动补选。以上是只读事实，不代表详细 Skill 已加载。");
        return sb.ToString();
    }

    private void CapturePracticePlaybackFacts(SenseVoiceSpeechToText sense, List<int> indices)
    {
        CaptureSingingGoalPlayback(sense, indices);
        var phrases = sense.DescribePracticePhrases();
        var sb = new StringBuilder();
        int position = 0;
        foreach (int index in indices ?? new List<int>())
        {
            var p = phrases.Find(item => item.Index == index);
            if (p == null) { m_PendingPracticePlaybackFacts = ""; return; }
            if (sb.Length > 0) sb.Append(" -> ");
            float start = (p.ActiveCapture == "expanded" ? p.ExpandedStartSeconds : p.CleanStartSeconds) + p.ActiveTrimHeadSeconds;
            float end = (p.ActiveCapture == "expanded" ? p.ExpandedEndSeconds : p.CleanEndSeconds) - p.ActiveTrimTailSeconds;
            sb.Append($"播放{++position}={p.ClipRef}(revision={p.Revision},source={p.ActiveCapture},raw=[{start:F2},{end:F2}],lyrics=\"{TruncateForFrame(p.Lyrics, 35)}\")");
        }
        m_PendingPracticePlaybackFacts = sb.ToString();
    }

    private void RecordPracticePlaybackOutcome(bool practice, bool completed)
    {
        CompleteSingingGoalPlayback(practice, completed);
        m_LatestSingingOutcome = (practice ? "practice" : "other") + (completed ? ":completed" : ":incomplete");
        if (practice && completed && m_PendingPracticePlaybackFacts.Length > 0)
        {
            m_LastCompletedPracticePlaybackFacts = m_PendingPracticePlaybackFacts;
            if (m_LogHumBack) Debug.Log("[Sing/Playback] 完整播放顺序已记录：" + m_LastCompletedPracticePlaybackFacts);
        }
        m_PendingPracticePlaybackFacts = "";
    }

    private void ExtractAndApplyClipReviseTags(ref string text, bool apply)
    {
        if (string.IsNullOrEmpty(text)) return;
        var pattern = new Regex(@"<clip_revise\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase);
        var matches = pattern.Matches(text);
        text = pattern.Replace(text, "");
        if (!apply || matches.Count == 0) return;
        var sense = m_ChatSettings != null ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText : null;
        foreach (Match match in matches)
        {
            string attrs = match.Groups["attrs"].Value;
            var request = ParseHumBackCompatibleAttributes(attrs);
            // Use exactly the same range semantics as sing, but do not schedule audio.
            ConfigureUnifiedSing(request, attrs + " refs=\"" + ReadToolAttribute(attrs, "ref") + "\"");
            string failure = "语音模块不可用。";
            if (sense == null || !TryValidateSingingGoalPreparation(request.ClipRefs, out failure) ||
                !TryResolveUnifiedSingCore(request, out failure, true))
            {
                RecordPracticeEditFailureForLlm("clip_revise 未完成；" + failure,
                    "clip_revision_failed", "只准备范围，不会播放；原录音保留，可询问用户。", "clip_revise");
                return;
            }
            RecordPracticeEditResult($"{request.ClipRefs} 当前版本已准备，played=false；ref不变。", false);
        }
    }

    private void ExtractAndApplyClipDropTags(ref string text, bool apply)
    {
        if (string.IsNullOrEmpty(text)) return;
        var pattern = new Regex(@"<clip_drop\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase);
        var matches = pattern.Matches(text);
        text = pattern.Replace(text, "");
        if (!apply || matches.Count == 0) return;
        var sense = m_ChatSettings != null ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText : null;
        var clips = new List<string>();
        foreach (Match match in matches)
        {
            string clip = ReadToolAttribute(match.Groups["attrs"].Value, "ref");
            if (sense == null || !sense.TryResolveSingingClip(clip, out _, out _))
            {
                RecordPracticeEditFailureForLlm("clip_drop 存在未知 ref，整批未删除。", "unknown_clip", "重新查看清单，不猜编号。", "clip_drop");
                return;
            }
            clip = sense.CanonicalSingingClipReference(clip);
            if (!clips.Contains(clip)) clips.Add(clip);
        }
        foreach (string clip in clips)
        {
            sense.TryResolveSingingClip(clip, out int stable, out int candidate);
            bool removed = candidate > 0 ? sense.DiscardQuarantinedSingingCandidate(candidate)
                : sense.DropPracticePhraseByStableId(stable, out _, out _, out _);
            if (!removed)
            { RecordPracticeEditFailureForLlm(clip + " 删除失败；已完成的删除不回滚。", "clip_drop_failed", "查看最新清单。", "clip_drop"); return; }
            m_PracticePitchStates.Remove(stable);
        }
        RecordPracticeDropResult("已移除 " + string.Join(",", clips) + "；其它 clip 身份不变，长期曲库未删除。", false);
    }

    private void ExtractAndApplyClipConfirmTags(ref string text, bool apply)
    {
        if (string.IsNullOrEmpty(text)) return;
        var pattern = new Regex(@"<clip_confirm\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase);
        var matches = pattern.Matches(text);
        text = pattern.Replace(text, "");
        if (!apply || matches.Count == 0) return;
        var sense = m_ChatSettings != null ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText : null;
        foreach (Match match in matches)
        {
            string attrs = match.Groups["attrs"].Value;
            string clip = ReadToolAttribute(attrs, "ref");
            if (sense == null || !sense.TryResolveSingingClip(clip, out int stable, out int candidate))
            {
                RecordPracticeEditFailureForLlm("clip_confirm 的 ref 不存在；没有改用同号候选或旧素材。", "unknown_clip", "复制素材清单的具体 ref，或询问用户。");
                return;
            }
            clip = sense.CanonicalSingingClipReference(clip);
            string range = ReadToolAttribute(attrs, "range");
            if (!string.IsNullOrWhiteSpace(range) && !TryValidateSingingGoalPreparation(clip, out string goalFailure))
            {
                RecordPracticeEditFailureForLlm(goalFailure, "singing_goal_needs_review", "先核对目标，再准备该范围。");
                return;
            }
            int oldRevision = sense.DescribePracticePhrases().Find(p => p.StableId == stable)?.Revision ?? -1;
            string failure = "";
            string status = "ready";
            bool success = !string.IsNullOrWhiteSpace(range)
                ? sense.TryPrepareSingingClip(clip, true, range, out stable, out failure)
                : sense.ConfirmSingingClipSource(clip, out status);
            if (!success)
            {
                RecordPracticeEditFailureForLlm(clip + " " + failure, "clip_preparation_failed", "原始录音保持；按边界证据选择范围或询问。");
                return;
            }
            if (!string.IsNullOrWhiteSpace(range) && oldRevision >= 0 &&
                sense.DescribePracticePhrases().Find(p => p.StableId == stable)?.Revision != oldRevision)
                m_PracticePitchStates.Remove(stable);
            RecordPracticeEditResult($"{clip} source=confirmed_user playback={status} confirmation_action_played=false；此次只确认/准备，历史播放记录未改变。" +
                (status == "ready" ? $"若决定演唱，用 <sing refs=\"{clip}\"/>。" :
                 "只确认了来源，仍需根据边界证据选择 sing 的 range=clean/expanded；也可询问。"), false);
        }
    }

    private static AgentSongSingRequest ExtractUnifiedLibrarySing(ref string text)
    {
        var match = Regex.Match(text, @"<sing\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        string attrs = match.Groups["attrs"].Value;
        string refs = (ReadToolAttribute(attrs, "refs") ?? "").Trim();
        if (!refs.StartsWith("song:", StringComparison.Ordinal)) return null;
        var request = new AgentSongSingRequest
        {
            UnifiedSing = true, Source = "library", Mode = "memory",
            SongId = refs.Substring(5), SegmentLyrics = ReadToolAttribute(attrs, "lyrics"),
            Reason = ReadToolAttribute(attrs, "reason")
        };
        if (!Regex.IsMatch(refs, @"^song:[0-9a-fA-F]{12}$"))
            request.SourceValidationError = "曲库 sing refs 需要单个 song:完整12位ID；不能省略或混入录音 clip。";
        foreach (string field in new[] { "source", "mode", "order", "capture", "range", "pitch_plan", "transpose", "key", "pitch_target", "pitch_delta", "pitch_match", "target_note", "align_to", "pace", "expression", "confirm_user", "trim_head_seconds", "trim_tail_seconds", "start_seconds", "end_seconds", "exclude_speech" })
            if (!string.IsNullOrWhiteSpace(ReadToolAttribute(attrs, field)))
                request.SourceValidationError = "当前曲库 sing 不支持 " + field + "；没有忽略参数或改用其它素材。分段音乐调整需选择录音 clip。";
        text = text.Remove(match.Index, match.Length).Trim();
        return request;
    }
    private static void ConfigureUnifiedSing(AgentHumBackRequest request, string attrs)
    {
        request.UnifiedSing = true;
        request.ClipRefs = (ReadToolAttribute(attrs, "refs") ?? "").Trim();
        request.ConfirmUser = ReadToolBoolAttribute(attrs, "confirm_user", false);
        request.Range = (ReadToolAttribute(attrs, "range") ?? "current").Trim().ToLowerInvariant();
        if (request.Range.Length == 0) request.Range = "current";
        request.StartSeconds = ReadToolFloatAttributeUnclamped(attrs, "start_seconds");
        request.EndSeconds = ReadToolFloatAttributeUnclamped(attrs, "end_seconds");
        request.Mode = "practice";
        request.Source = "practice";
        request.Order = "";
        request.Capture = "clean"; // Legacy composition's default is CURRENT WAV.
        request.SourceValidationError = "";
        foreach (string field in new[] { "source", "mode", "order", "capture", "trim_head_seconds", "trim_tail_seconds" })
            if (!string.IsNullOrWhiteSpace(ReadToolAttribute(attrs, field)))
                request.SourceValidationError = "sing 不接受 " + field +
                    "；只用 refs 选素材，range 选范围，音乐目标用 pitch_plan/transpose。未忽略该字段或替换素材。";
        if (string.IsNullOrWhiteSpace(request.ClipRefs))
            request.SourceValidationError = "sing 缺少 refs；请从素材清单复制具体引用，不默认最近一段或全部。";
        bool hasStart = !string.IsNullOrWhiteSpace(ReadToolAttribute(attrs, "start_seconds"));
        bool hasEnd = !string.IsNullOrWhiteSpace(ReadToolAttribute(attrs, "end_seconds"));
        if (hasStart != hasEnd || ((hasStart || hasEnd) &&
            (float.IsNaN(request.StartSeconds) || float.IsNaN(request.EndSeconds))))
            request.SourceValidationError = "start_seconds/end_seconds 必须成对提供有效数值（原录音坐标）。";
        if (hasStart && request.Range != "current")
            request.SourceValidationError = "明确起止秒数与 range 二选一；坐标均来自原录音，不混用。";
    }

    private bool TryResolveUnifiedSing(AgentHumBackRequest request, out string failure)
        => TryResolveUnifiedSingCore(request, out failure, false);

    private bool TryResolveUnifiedSingCore(AgentHumBackRequest request, out string failure, bool preparationOnly)
    {
        failure = request.SourceValidationError;
        if (!string.IsNullOrEmpty(failure)) return false;
        if (!string.IsNullOrEmpty(request.ValidationError))
        { failure = request.ValidationError; return false; }
        if (!CanExecuteSkillAction("singing", out failure)) return false;
        var sense = m_ChatSettings != null ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText : null;
        if (sense == null) { failure = "语音模块不可用。"; return false; }
        string[] refs = request.ClipRefs.Split(',').Select(r => r.Trim()).ToArray();
        // Validate ALL identities before any source confirmation or range revision.
        foreach (string clip in refs)
            if (!clip.StartsWith("clip:", StringComparison.Ordinal) ||
                !sense.TryResolveSingingClip(clip, out _, out _))
            {
                string available = string.Join(",", sense.DescribePracticePhrases().Select(p => p.ClipRef)
                    .Concat(sense.DescribeQuarantinedSingingCandidates().Select(p => p.ClipRef)));
                failure = $"refs 中的 {clip} 不是当前存在的 clip 引用；没有播放任何片段。" +
                    "曲库 ID 不能加 clip: 前缀变成本次录音。" +
                    (available.Length == 0 ? "当前没有已登记的录音引用；不能通过猜 ID 修复。" :
                        "当前真实录音引用=" + available + "；存在不等于已确认或可播放，请结合素材状态选范围，也可询问。");
                return false;
            }
        refs = refs.Select(sense.CanonicalSingingClipReference).ToArray();
        request.ClipRefs = string.Join(",", refs);
        if (refs.Length > 1 && !float.IsNaN(request.StartSeconds))
        { failure = "单组 start_seconds/end_seconds 只能指定一个 clip 的原录音坐标；本次多 refs 尚未准备或播放。" +
                "可逐段 clip_revise 后 sing 当前版本；统一 range=clean/expanded 可直接用于多 refs。"; return false; }
        if (request.Range != "current" && request.Range != "clean" && request.Range != "expanded")
        { failure = "range 只能为 current、clean、expanded；没有准备或播放任何片段。"; return false; }
        if (!preparationOnly && !TryValidateSingingGoalRequest(request, sense, out failure)) return false;
        var order = new List<string>();
        var prepared = new List<string>();
        foreach (string clip in refs)
        {
            var previous = sense.DescribePracticePhrases().Find(p => p.ClipRef == clip);
            int oldRevision = previous?.Revision ?? -1;
            int stableId;
            bool ready = !float.IsNaN(request.StartSeconds)
                ? sense.TrySelectSingingClipWindow(clip, request.ConfirmUser, request.StartSeconds,
                    request.EndSeconds, request.ExcludeSpeech, out stableId, out failure)
                : sense.TryPrepareSingingClip(clip, request.ConfirmUser, request.Range, out stableId, out failure, request.ExcludeSpeech);
            if (!ready)
            {
                failure = $"{clip} range={request.Range} 准备失败：" + failure;
                if (prepared.Count > 0) failure += " 已准备的素材保持：" + string.Join(",", prepared) + "；整次演唱未开始。";
                return false;
            }
            if (oldRevision >= 0 && sense.DescribePracticePhrases().Find(p => p.StableId == stableId)?.Revision != oldRevision)
                m_PracticePitchStates.Remove(stableId);
            order.Add("stable:" + stableId.ToString(CultureInfo.InvariantCulture));
            prepared.Add(clip);
        }
        // A concrete stable order pins the material before any asynchronous SVC work.
        request.Order = string.Join(",", order);
        request.Capture = "clean";
        if (!sense.TryValidatePracticeBoundarySelection(request.Order, false, 0f, 0f,
                request.ExcludeSpeech, out failure))
        {
            failure += " 所选范围已准备并保留，尚未播放；可根据冲突事实修订或询问。";
            return false;
        }
        Debug.Log($"[Sing/Resolve] refs={request.ClipRefs} -> order={request.Order} range={request.Range}; prepared_not_played");
        return true;
    }

    private string BuildUnifiedClipFacts(SenseVoiceSpeechToText sense)
    {
        var sb = new StringBuilder("\n[演唱素材主接口：sing refs=具体引用；不填 source/mode/order。clip 身份在确认、修订后不变；range默认current；起止秒数统一基于原始录音。]\n");
        sb.Append(SingingGoalContract);
        sb.Append(BuildSingingGoalContext());
        sb.Append(BuildSingingClipOverview(sense));
        var phrases = sense.DescribePracticePhrases();
        var pending = sense.DescribeQuarantinedSingingCandidates();
        sb.AppendLine("‘刚唱的录音’与‘你刚才唱的第几段’可分别参考上面两种顺序；歌名/相似歌词不等于同一次录音。指代仍由你判断，可询问。");
        foreach (var p in phrases)
        {
            sb.Append($"{p.ClipRef} source={(p.PendingConfirmation ? "pending" : "confirmed_user")} playback=ready evidence=current_version " +
                $"current={p.Seconds:F2}s revision={p.Revision} clean=[{p.CleanStartSeconds:F2},{p.CleanEndSeconds:F2}] expanded=[{p.ExpandedStartSeconds:F2},{p.ExpandedEndSeconds:F2}] " +
                $"recorded_ago={p.AgoSeconds:F1}s lyrics=\"{TruncateForFrame(p.Lyrics, 100)}\" " +
                $"recorded_center={p.PitchCenterNote} preceding=\"{TruncateForFrame(p.PrecedingSpeech, 100)}\"");
            sb.Append($" first_stable={p.FirstStablePitchNote} range_notes={p.PitchRangeNote} " +
                $"language={p.Language} take={p.TakeIndex}/{p.TakeTotal} song_hint={p.SongName}");
            float currentStart = (p.ActiveCapture == "expanded" ? p.ExpandedStartSeconds : p.CleanStartSeconds) + p.ActiveTrimHeadSeconds;
            float currentEnd = (p.ActiveCapture == "expanded" ? p.ExpandedEndSeconds : p.CleanEndSeconds) - p.ActiveTrimTailSeconds;
            sb.Append($" current_source={p.ActiveCapture} current_raw=[{currentStart:F2},{currentEnd:F2}]");
            if (string.IsNullOrEmpty(p.Lyrics))
                sb.Append($" recording_transcript_not_current_lyrics=\"{TruncateForFrame(p.RecordingTranscript, 180)}\"");
            if (m_PracticePitchStates.TryGetValue(p.StableId, out PracticePitchState pitch))
                sb.Append($" character_center={FormatPitchMidiPrecise(pitch.CenterMidi)} " +
                    $"target={FormatPitchMidiPrecise(pitch.RequestedCenterMidi)} " +
                    $"measurement={(pitch.Measured ? "output_measured" : "estimated")} fully_played={pitch.FullyPlayed}");
            sb.AppendLine();
            AppendExpansionEffect(sb, p.CleanStartSeconds, p.CleanEndSeconds,
                p.ExpandedStartSeconds, p.ExpandedEndSeconds);
            sb.AppendLine($"head_extra={p.HeadExtraSeconds:F2}s/{p.HeadExtraType}/\"{TruncateForFrame(p.HeadExtraText, 100)}\" " +
                $"tail_extra={p.TailExtraSeconds:F2}s/{p.TailExtraType}/\"{TruncateForFrame(p.TailExtraText, 100)}\" " +
                $"head_review={p.HeadExtraReviewRequired} tail_review={p.TailExtraReviewRequired}");
            AppendClipBoundaryWindows(sb, p.ExpandedStartSeconds, p.HeadExtraSegments);
            AppendClipBoundaryWindows(sb, p.ExpandedStartSeconds, p.TailExtraSegments);
            if (p.CleanLeadInUnverifiedSeconds > .1f)
                sb.AppendLine($"clean_lead_unverified={p.CleanLeadInUnverifiedSeconds:F2}s type={p.CleanLeadInEvidenceType} text={p.CleanLeadInEvidenceText}");
        }
        foreach (var p in pending)
        {
            AppendExpansionEffect(sb, p.CleanStartSeconds, p.CleanEndSeconds,
                p.ExpandedStartSeconds, p.ExpandedEndSeconds);
            sb.AppendLine($"{p.ClipRef} source={p.SourceStatus} playback={p.PlaybackStatus} evidence=current_version " +
                $"current={p.CurrentCapture} revision=pending " +
                (p.PlaybackStatus == "ready" ? $"current_raw=[{p.CurrentStartSeconds:F2},{p.CurrentEndSeconds:F2}] " : "") +
                $"raw={p.RawSeconds:F2}s clean=[{p.CleanStartSeconds:F2},{p.CleanEndSeconds:F2}] expanded=[{p.ExpandedStartSeconds:F2},{p.ExpandedEndSeconds:F2}] " +
                $"recorded_ago={p.AgoSeconds:F1}s lyrics=\"{TruncateForFrame(p.SingingSegmentText, 100)}\" " +
                $"whole=\"{TruncateForFrame(p.WholeTurnText, 200)}\" " +
                $"tail_extra={p.TailExtraSeconds:F2}s/{p.TailExtraType}/\"{TruncateForFrame(p.TailExtraText, 100)}\"");
            sb.AppendLine($"head_extra={p.HeadExtraSeconds:F2}s/{p.HeadExtraType}/\"{TruncateForFrame(p.HeadExtraText, 100)}\" " +
                $"p={p.SingingProbability:F2} stability={p.PitchStability:F2} " +
                $"melodic_island={p.MelodicIslandSeconds:F2}s head_review={p.HeadExtraReviewRequired} tail_review={p.TailExtraReviewRequired}");
            AppendClipBoundaryWindows(sb, p.ExpandedStartSeconds, p.HeadExtraSegments);
            AppendClipBoundaryWindows(sb, p.ExpandedStartSeconds, p.TailExtraSegments);
            if (p.CleanLeadInUnverifiedSeconds > .1f)
                sb.AppendLine($"clean_lead_unverified={p.CleanLeadInUnverifiedSeconds:F2}s " +
                    $"type={p.CleanLeadInEvidenceType} text={p.CleanLeadInEvidenceText}");
        }
        sb.AppendLine("pending：来源不确定，可询问或自主选已有音频；演唱不要求确认来源，也不自动确认来源。evidence_only：已录到但未选可播放范围。" +
            "多 refs 的 range 对各段独立采用其自身 clean/expanded；不同范围可先逐段修订。准备好不等于已唱。");
        sb.AppendLine($"素材当前总长={phrases.Sum(p => p.Seconds):F1}s；明确要求完整演唱可自然分块流式播放。" +
            $"自主发起的软预算={m_AutonomousSingingMaxSeconds:F0}s，由你选择范围，不在句中强制截断。" +
            "曲库不是本次录音：曲库快照中的完整id可写 sing refs=\"song:完整id\"；" +
            "不指定 lyrics 时表示该曲库条目的全部已存片段，不代表完整原曲。相似候选不证明用户要唱曲库版本。");
        return sb.ToString();
    }

    private static void AppendClipBoundaryWindows(StringBuilder sb, float expandedStart,
        SenseVoiceSpeechToText.SingingBoundarySubsegment[] windows)
    {
        if (windows == null) return;
        foreach (var window in windows)
            if (window != null)
                sb.Append($" raw_window=[{expandedStart + window.expanded_start_seconds:F2}," +
                    $"{expandedStart + window.expanded_end_seconds:F2}] type={window.type}");
        sb.AppendLine();
    }

    private static void AppendExpansionEffect(StringBuilder sb, float cleanStart, float cleanEnd,
        float expandedStart, float expandedEnd)
    {
        float head = Mathf.Max(0f, cleanStart - expandedStart);
        float tail = Mathf.Max(0f, expandedEnd - cleanEnd);
        sb.AppendLine($"expanded相对clean实际增加：头部={head:F2}s，尾部={tail:F2}s。" +
            (head > .05f && tail <= .05f ? "只增加前置音频，不会补回任何尾音。" :
             tail > .05f && head <= .05f ? "只增加尾部音频，不会改变起唱位置。" : ""));
    }
}
