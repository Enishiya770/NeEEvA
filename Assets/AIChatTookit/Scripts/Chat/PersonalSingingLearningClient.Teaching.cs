using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

// Teaching is a review of an acoustic hypothesis. Nothing here turns a model
// suggestion or a conversational correction into a saved human label by itself.
public sealed partial class PersonalSingingLearningClient
{
    public float[] WaveformSamples { get; private set; } = Array.Empty<float>();
    public int WaveformRate { get; private set; }
    public string ProposalSummary { get; private set; } = "开启教她后，照常说话或唱歌。我会先标出自己的判断。";
    public bool HasUnsavedEdits { get; private set; }
    public bool NeedsProfileChoice { get; private set; }
    public bool ProposalPending { get; private set; }
    public bool WaveformPending { get; private set; }
    public int PreviewSample => previewSourceOffset >= 0 && previewSource != null && previewSource.isPlaying ? previewSourceOffset + previewSource.timeSamples : -1;
    public int DraftRevision => draftEditRevision;
    public JObject CurrentProposal { get; private set; }
    private readonly Stack<JArray> draftUndo = new Stack<JArray>();
    private int draftEditRevision, selectionDataGeneration, teachingInitGeneration, previewSourceOffset = -1;
    private bool teachingInitializing, resumeTeachingAfterProfileChoice, reviewPinned;
    private string pendingLatestRecording, correctionText;
    private string latestCapturedRecordingId, spokenCorrectionTargetId, pendingCorrectionTarget, correctionSelectionAttempt;
    private PersonalSingingTeachingCorrection pendingCorrection;
    private readonly Dictionary<string, PendingDraft> pendingDrafts = new Dictionary<string, PendingDraft>();
    private sealed class PendingDraft { public JArray Regions; public JObject Proposal; public string Sha, Correction; public int HumanRevision; }
    private string SelectedDraftKey => serviceUrl + "/" + ProfileId + "/" + (string)Selected?["recording_id"];
    private UnityWebRequest waveformRequest, proposalRequest;
    private byte[] selectedOriginal;
    private int selectedOriginalOffset, selectedOriginalChannels;
    private string RememberedProfileKey => "NeEEvA.PersonalVoice." + PersonalSingingLearningProtocol.Sha256(Encoding.UTF8.GetBytes(serviceUrl)).Substring(0, 20);

    private void RememberProfile()
    {
        NeedsProfileChoice = false;
        if (!string.IsNullOrEmpty(ProfileId)) { PlayerPrefs.SetString(RememberedProfileKey, ProfileId); PlayerPrefs.Save(); }
    }

    /// <summary>Explicit opt-in. Uses the remembered person; never picks an arbitrary person from several profiles.</summary>
    public void BeginTeaching()
    {
        if (!isActiveAndEnabled || teachingInitializing) return;
        if (Busy) { resumeTeachingAfterProfileChoice = true; Change("正在准备，请稍等"); return; }
        if (!string.IsNullOrEmpty(ProfileId) && !string.IsNullOrEmpty(SessionId) && !string.IsNullOrEmpty(ContentGroup))
        { SetCollection(true); return; }
        StartCoroutine(BeginTeachingFlow(++teachingInitGeneration));
    }
    private IEnumerator BeginTeachingFlow(int generation)
    {
        teachingInitializing = true; resumeTeachingAfterProfileChoice = false;
        float deadline = Time.realtimeSinceStartup + 10f;
        while (!outboxReady && Time.realtimeSinceStartup < deadline) yield return null;
        if (!outboxReady) { teachingInitializing = false; Change("本地录音队列未就绪，请稍后再试"); yield break; }
        bool success = false;
        yield return JsonRequest("GET", "/learning/profiles", null, value => { Profiles = value["profiles"] as JArray ?? new JArray(); success = true; }, contextRevision);
        if (!success || generation != teachingInitGeneration) { teachingInitializing = false; yield break; }
        string chosen = ProfileId;
        if (!Profiles.Any(p => (string)p["profile_id"] == chosen)) chosen = PlayerPrefs.GetString(RememberedProfileKey, "");
        if (!Profiles.Any(p => (string)p["profile_id"] == chosen)) chosen = Profiles.Count == 1 ? (string)Profiles[0]["profile_id"] : null;
        if (string.IsNullOrEmpty(chosen) && Profiles.Count > 1)
        {
            NeedsProfileChoice = true; resumeTeachingAfterProfileChoice = true; teachingInitializing = false;
            Change("先选一下是谁在教我；之后我会记住，不用每次选择"); yield break;
        }
        if (string.IsNullOrEmpty(chosen))
        {
            chosen = "p-" + Guid.NewGuid().ToString("N"); success = false;
            yield return JsonRequest("POST", "/learning/profiles", new JObject { ["profile_id"] = chosen, ["display_name"] = "我的声音" }, value => { Profiles.Add(value); success = true; }, contextRevision);
            if (!success || generation != teachingInitGeneration) { teachingInitializing = false; yield break; }
        }
        if (ProfileId != chosen)
        {
            leases.SetEnabled(false); captureEndpoints.Clear(); contextRevision++;
            ProfileId = chosen; SessionId = ContentGroup = ContentGroupName = null;
            ResetTeachingProfileState();
            Recordings = new JArray(); ClearSelection(); trackedJob = null;
        }
        RememberProfile();
        string session = "s-" + Guid.NewGuid().ToString("N"), group = "g-" + Guid.NewGuid().ToString("N");
        success = false;
        yield return JsonRequest("POST", ProfileRoute + "/sessions", new JObject { ["session_id"] = session, ["content_group"] = group }, value => {
            SessionId = (string)value["session_id"]; ContentGroup = (string)value["content_group"]; ContentGroupName = "日常声音 " + DateTime.Now.ToString("M月d日 HH:mm"); success = true;
        }, contextRevision);
        teachingInitializing = false;
        if (!success || generation != teachingInitGeneration) yield break;
        SetCollection(true);
        Change("我准备好了，像平时一样交流就好。每段声音我会先判断；你打开这里时再一起检查。");
        RefreshRecordings();
    }
    private void UpdateTeaching()
    {
        if (resumeTeachingAfterProfileChoice && !NeedsProfileChoice && !Busy && !teachingInitializing)
        { resumeTeachingAfterProfileChoice = false; BeginTeaching(); }
        UpdatePendingCorrection();
        if (string.IsNullOrEmpty(pendingLatestRecording) || Busy || HasUnsavedEdits || reviewPinned || teachingInitializing || string.IsNullOrEmpty(ProfileId)) return;
        string id = pendingLatestRecording; pendingLatestRecording = null;
        int selection = SelectionRevision;
        StartInteractive("GET", ProfileRoute + "/recordings", null, value => {
            Recordings = value["recordings"] as JArray ?? new JArray();
            if (!HasUnsavedEdits && !reviewPinned && selection == SelectionRevision) SelectRecording(id);
        });
    }
    private void QueueLatestRecording(string id) { pendingLatestRecording = id; Revision++; }
    private void CancelTeachingRequests()
    {
        teachingInitGeneration++; teachingInitializing = false; resumeTeachingAfterProfileChoice = false;
        pendingCorrection = null; pendingCorrectionTarget = correctionSelectionAttempt = null;
        ClearTeachingSelection();
    }
    private void ResetTeachingProfileState()
    {
        pendingLatestRecording = latestCapturedRecordingId = spokenCorrectionTargetId = pendingCorrectionTarget = correctionSelectionAttempt = null;
        pendingCorrection = null; reviewPinned = false;
    }
    private void ClearTeachingSelection()
    {
        selectionDataGeneration++; waveformRequest?.Abort(); proposalRequest?.Abort(); waveformRequest = proposalRequest = null;
        WaveformPending = ProposalPending = false; WaveformSamples = Array.Empty<float>(); WaveformRate = 0;
        selectedOriginal = null; CurrentProposal = null; draftUndo.Clear(); HasUnsavedEdits = false; correctionText = null; draftEditRevision++;
        ProposalSummary = "我先听一遍，再把判断标在波形上。";
    }
    private void InitializeTeachingSelection()
    {
        if (Selected == null) return;
        bool confirmed = (int?)Selected["annotation_revision"] > 0;
        ProposalSummary = confirmed ? "这是你上次确认的判断；可以继续回听和纠正。" : "我正在听这段声音，稍后会标出我认为的说话、唱歌和环境音。";
        HasUnsavedEdits = false; draftUndo.Clear();
        if (pendingDrafts.TryGetValue(SelectedDraftKey, out var pending) && pending.Sha == (string)Selected["audio_sha256"] && pending.HumanRevision == ((int?)Selected["annotation_revision"] ?? 0))
        {
            Draft = (JArray)pending.Regions.DeepClone(); CurrentProposal = pending.Proposal == null ? null : (JObject)pending.Proposal.DeepClone();
            correctionText = pending.Correction; HasUnsavedEdits = true; ProposalSummary = "已恢复这段尚未确认的修改；确认后我才会记住。";
        }
        StartCoroutine(LoadWaveform(Selected, SelectionRevision, contextRevision, selectionDataGeneration));
        if (!confirmed && !HasUnsavedEdits) RequestProposal();
    }
    private void RememberSelectionDraft()
    {
        if (Selected == null || !HasUnsavedEdits) return;
        pendingDrafts[SelectedDraftKey] = new PendingDraft { Regions = (JArray)Draft.DeepClone(), Proposal = CurrentProposal == null ? null : (JObject)CurrentProposal.DeepClone(),
            Sha = (string)Selected["audio_sha256"], HumanRevision = (int?)Selected["annotation_revision"] ?? 0, Correction = correctionText };
    }
    private bool SameSelection(string id, int selection, int owner, int dataGeneration) =>
        isActiveAndEnabled && Selected != null && (string)Selected["recording_id"] == id && SelectionRevision == selection && contextRevision == owner && selectionDataGeneration == dataGeneration;

    private sealed class DecodedWave
    {
        public float[] Mono; public int Rate, Channels, Frames, Offset;
    }
    private IEnumerator LoadWaveform(JObject recording, int selection, int owner, int dataGeneration)
    {
        string id = (string)recording["recording_id"], sha = (string)recording["audio_sha256"];
        WaveformPending = true; Revision++;
        using (var request = UnityWebRequest.Get(serviceUrl + ProfileRoute + "/recordings/" + Uri.EscapeDataString(id) + "/audio"))
        {
            waveformRequest = request; request.timeout = 20; yield return request.SendWebRequest();
            if (ReferenceEquals(waveformRequest, request)) waveformRequest = null;
            if (!SameSelection(id, selection, owner, dataGeneration)) yield break;
            if (request.result != UnityWebRequest.Result.Success)
            { WaveformPending = false; Change("波形暂未加载，可重新选择这段录音重试：" + Failure(request)); yield break; }
            byte[] raw = request.downloadHandler.data;
            var decode = Task.Run(() => {
                if (PersonalSingingLearningProtocol.Sha256(raw) != sha || !PersonalSingingLearningProtocol.TryWave(raw, out int rate, out int channels, out int frames, out int offset, out _)) return null;
                var mono = new float[frames];
                // Pick the signed maximum-magnitude channel for display; unlike
                // averaging, anti-phase stereo cannot disappear from the waveform.
                for (int i = 0; i < frames; i++)
                    for (int c = 0; c < channels; c++)
                    { float sample = BitConverter.ToInt16(raw, offset + (i * channels + c) * 2) / 32768f; if (Math.Abs(sample) > Math.Abs(mono[i])) mono[i] = sample; }
                return new DecodedWave { Mono = mono, Rate = rate, Channels = channels, Frames = frames, Offset = offset };
            });
            while (!decode.IsCompleted) yield return null;
            if (!SameSelection(id, selection, owner, dataGeneration)) yield break;
            WaveformPending = false;
            if (decode.IsFaulted || decode.Result == null || decode.Result.Frames != (int)recording["sample_count"] || decode.Result.Rate != (int)recording["sample_rate"])
            { Change("原音校验未通过，未显示波形"); yield break; }
            var decoded = decode.Result; selectedOriginal = raw; selectedOriginalOffset = decoded.Offset; selectedOriginalChannels = decoded.Channels;
            WaveformSamples = decoded.Mono; WaveformRate = decoded.Rate; Revision++;
        }
    }
    private bool PlayCachedOriginal(int start, int end)
    {
        if (selectedOriginal == null || WaveformRate <= 0 || start < 0 || end <= start || end > WaveformSamples.Length || previewSource == null) return false;
        var pcm = new float[(end - start) * selectedOriginalChannels];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = BitConverter.ToInt16(selectedOriginal, selectedOriginalOffset + (start * selectedOriginalChannels + i) * 2) / 32768f;
        ReleasePreviewAudio(); previewClip = AudioClip.Create("Teaching original preview", end - start, selectedOriginalChannels, WaveformRate, false);
        previewClip.SetData(pcm, 0); previewSource.clip = previewClip; previewSourceOffset = start; previewSource.Play(); Change("正在回听选中的原声"); return true;
    }

    public void RequestProposal()
    {
        if (Selected == null) { Change("先录一段，或选择已有录音"); return; }
        if (HasUnsavedEdits) { Change("先确认当前纠正，避免新判断覆盖你的修改"); return; }
        if (ProposalPending) return;
        StartCoroutine(LoadProposal((string)Selected["recording_id"], SelectionRevision, contextRevision, selectionDataGeneration, draftEditRevision));
    }
    private IEnumerator LoadProposal(string id, int selection, int owner, int dataGeneration, int editRevision)
    {
        ProposalPending = true; Revision++;
        string path = ProfileRoute + "/recordings/" + Uri.EscapeDataString(id) + "/proposal", method = "POST";
        float deadline = Time.realtimeSinceStartup + 180f;
        while (Time.realtimeSinceStartup < deadline)
        {
            using (var request = new UnityWebRequest(serviceUrl + path, method))
            {
                proposalRequest = request; request.timeout = 20; request.downloadHandler = new DownloadHandlerBuffer();
                if (method == "POST") { request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}")); request.SetRequestHeader("Content-Type", "application/json"); }
                yield return request.SendWebRequest();
                if (ReferenceEquals(proposalRequest, request)) proposalRequest = null;
                if (!SameSelection(id, selection, owner, dataGeneration)) yield break;
                if (HasUnsavedEdits || draftEditRevision != editRevision) { ProposalPending = false; Revision++; yield break; }
                if (request.result != UnityWebRequest.Result.Success)
                { ProposalPending = false; ProposalSummary = "这次我还没能给出判断。可以点“重新判断”，也可以直接选中波形教我。"; Change("自动判断暂未完成：" + Failure(request)); yield break; }
                JObject reply = null; try { reply = JObject.Parse(request.downloadHandler.text); } catch { }
                if (reply == null) { ProposalPending = false; Change("判断响应无效，请重试"); yield break; }
                JObject proposal = reply["proposal"] as JObject;
                if (proposal == null && reply["result"] is JObject result && result["proposal_id"] != null) proposal = result;
                if (proposal == null && reply["proposal_id"] != null) proposal = reply;
                if (proposal != null)
                {
                    ProposalPending = false;
                    if (!ApplyProposal(proposal, editRevision)) Change("这份判断已不对应当前录音或纠正版本，请重新判断");
                    yield break;
                }
                string status = (string)reply["status"], job = (string)reply["job_id"];
                if (status == "failed" || status == "cancelled" || (status == "completed" && proposal == null))
                { ProposalPending = false; ProposalSummary = "这次我没能判断清楚，可以重试或直接在波形上教我。"; Change(ProposalSummary); yield break; }
                if (!string.IsNullOrEmpty(job)) { path = "/learning/jobs/" + Uri.EscapeDataString(job); method = "GET"; }
                else if (method == "POST") { ProposalPending = false; Change("判断任务未返回有效结果，请重试"); yield break; }
            }
            yield return new WaitForSecondsRealtime(.6f);
            if (!SameSelection(id, selection, owner, dataGeneration)) yield break;
        }
        ProposalPending = false; Change("自动判断仍在后台处理，可稍后点击重新判断");
    }
    private bool ApplyProposal(JObject proposal, int editRevision)
    {
        if (Selected == null || HasUnsavedEdits || editRevision != draftEditRevision ||
            (string)proposal["recording_id"] != (string)Selected["recording_id"] ||
            (string)proposal["audio_sha256"] != (string)Selected["audio_sha256"] ||
            (int?)proposal["annotation_revision"] != ((int?)Selected["annotation_revision"] ?? 0) ||
            (int?)proposal["sample_rate"] != (int?)Selected["sample_rate"] ||
            (int?)proposal["sample_count"] != (int?)Selected["sample_count"] ||
            !(proposal["regions"] is JArray ranges) || !PersonalSingingLearningProtocol.ValidateRanges(ranges, (int)Selected["sample_count"], true, out _)) return false;
        CurrentProposal = (JObject)proposal.DeepClone(); Draft = StripRegions(ranges); draftUndo.Clear();
        ProposalSummary = (string)proposal["summary"] ?? "这是我的初步判断，请帮我看看对不对。";
        HasUnsavedEdits = false; draftEditRevision++; Change("我先标好了。可以直接确认，或拖动边界、选一段改类别。"); return true;
    }
    private static JArray StripRegions(JArray ranges) => new JArray(ranges.Select(r => (object)new JObject {
        ["start_sample"] = r["start_sample"]?.DeepClone(), ["end_sample"] = r["end_sample"]?.DeepClone(),
        ["label"] = r["label"]?.DeepClone(), ["tolerance_samples"] = (int?)r["tolerance_samples"] ?? 0 }));
    private void RememberDraft() { if (draftUndo.Count >= 32) { var recent = draftUndo.Take(31).Reverse().ToArray(); draftUndo.Clear(); foreach (var value in recent) draftUndo.Push(value); } draftUndo.Push((JArray)Draft.DeepClone()); }
    private void MarkDraftEdited() { HasUnsavedEdits = true; draftEditRevision++; Revision++; }
    public bool ReplaceRange(int start, int end, string label)
    {
        if (Selected == null || !PersonalSingingLearningTeachingDraft.TryReplace(Draft, (int)Selected["sample_count"], start, end, label, out var result)) return false;
        RememberDraft(); Draft = result; MarkDraftEdited(); Change("这段已改好，确认后我才会记住"); return true;
    }
    public bool ResizeRange(int oldStart, int oldEnd, int newStart, int newEnd)
    {
        if (Selected == null || !PersonalSingingLearningTeachingDraft.TryResize(Draft, (int)Selected["sample_count"], oldStart, oldEnd, newStart, newEnd, out var result)) return false;
        RememberDraft(); Draft = result; MarkDraftEdited(); Change("边界已调整；新露出的空白保留为待判断，确认前可继续修改"); return true;
    }
    public void UndoDraft()
    {
        if (draftUndo.Count == 0) return; Draft = draftUndo.Pop(); MarkDraftEdited(); Change("已撤销上一次纠正");
    }
    private void AddTeachingProvenance(JObject body)
    {
        if (!string.IsNullOrEmpty((string)CurrentProposal?["proposal_id"])) body["proposal_id"] = CurrentProposal["proposal_id"].DeepClone();
        if (!string.IsNullOrEmpty(correctionText)) body["correction_text"] = correctionText;
    }
    private void TeachingConfirmed(int submittedRevision)
    {
        // A user can keep dragging while the save is in flight: save only clears
        // the version submitted, never those later edits.
        CurrentProposal = null;
        if (submittedRevision != draftEditRevision) return;
        HasUnsavedEdits = false; draftUndo.Clear(); correctionText = null;
        pendingDrafts.Remove(SelectedDraftKey);
        reviewPinned = false;
        ProposalSummary = "我记住你的这次确认了；以后会用这些例子调整判断。"; Revision++;
    }
    private void SnapshotSpokenCorrectionTarget()
    {
        // Freeze BEFORE recording the correction itself. Its upload must never
        // become the audio being corrected merely because ASR arrives later.
        spokenCorrectionTargetId = reviewPinned
            ? pendingCorrectionTarget ?? (string)Selected?["recording_id"] ?? latestCapturedRecordingId
            : latestCapturedRecordingId ?? (string)Selected?["recording_id"];
    }
    public bool TryApplySpokenCorrection(string text, bool fromAudio, out string modelReceipt)
    {
        modelReceipt = null;
        if (!PersonalSingingTeachingIntent.TryParse(text, out var intent)) return false;
        string target = fromAudio ? spokenCorrectionTargetId : (string)Selected?["recording_id"] ?? latestCapturedRecordingId;
        if (string.IsNullOrEmpty(target))
        { reviewPinned = true; modelReceipt = "已打开声音教学面板，但没有找到纠正前的录音。当前这句纠正本身不会当成待纠正素材；尚未保存标签。"; return true; }
        if (HasUnsavedEdits && (string)Selected?["recording_id"] != target)
        { reviewPinned = true; modelReceipt = "面板已有另一段尚未确认的修改，已保留。请先确认或切换目标录音，这次没有覆盖修改。"; return true; }
        pendingCorrection = intent; pendingCorrectionTarget = target; correctionSelectionAttempt = null; reviewPinned = true;
        if ((string)Selected?["recording_id"] == target && (!ProposalPending || intent.Mode != "ordered"))
        {
            modelReceipt = ApplyPendingCorrection();
        }
        else modelReceipt = "正在取出上一段录音和它的声学判断；这句纠正只会形成待确认的波形草稿，尚未保存标签。";
        return true;
    }
    private void UpdatePendingCorrection()
    {
        if (pendingCorrection == null || Busy || string.IsNullOrEmpty(ProfileId)) return;
        if ((string)Selected?["recording_id"] == pendingCorrectionTarget)
        {
            if (ProposalPending && pendingCorrection.Mode == "ordered") return;
            string receipt = ApplyPendingCorrection(); Change(receipt); return;
        }
        if (HasUnsavedEdits || correctionSelectionAttempt == pendingCorrectionTarget || outbox.Any(i => (string)i.Manifest?["recording_id"] == pendingCorrectionTarget)) return;
        correctionSelectionAttempt = pendingCorrectionTarget;
        SelectRecording(pendingCorrectionTarget); reviewPinned = true;
    }
    private string ApplyPendingCorrection()
    {
        var intent = pendingCorrection; pendingCorrection = null; pendingCorrectionTarget = correctionSelectionAttempt = null;
        if (intent == null || Selected == null) return "未找到待核对录音，没有修改标签。";
        if (intent.Mode == "review") return "已打开纠正前那段原录音的波形供核对；尚未修改或保存标签。";
        int count = (int)Selected["sample_count"], rate = (int)Selected["sample_rate"];
        bool changed = false;
        if (intent.Mode == "whole") changed = ReplaceRange(0, count, intent.FirstLabel);
        else if (intent.Mode == "range")
        {
            if (PersonalSingingLearningProtocol.TryRange(intent.StartSeconds.ToString("R", CultureInfo.InvariantCulture), intent.EndSeconds.ToString("R", CultureInfo.InvariantCulture), true, rate, count, out int a, out int b, out _)) changed = ReplaceRange(a, b, intent.FirstLabel);
        }
        else if (intent.Mode == "ordered")
        {
            // Anchor the requested order to an actual existing transition into
            // the second class, with evidence of a first part before it. A text
            // description cannot create a guessed timestamp halfway through.
            var second = Draft.FirstOrDefault(r => (string)r["label"] == intent.SecondLabel && (int)r["start_sample"] > 0 &&
                Draft.Any(p => (int)p["end_sample"] <= (int)r["start_sample"] && ((string)p["label"] == intent.FirstLabel || (string)p["label"] == "uncertain")));
            if (second != null)
            {
                int boundary = (int)second["start_sample"];
                var next = (JArray)Draft.DeepClone();
                foreach (JObject region in next)
                {
                    // Keep silence/environment holes rather than turning a
                    // trailing pause into singing from linguistic order alone.
                    if ((string)region["label"] == "environment" && intent.FirstLabel != "environment" && intent.SecondLabel != "environment") continue;
                    region["label"] = (int)region["end_sample"] <= boundary ? intent.FirstLabel : intent.SecondLabel;
                    region["tolerance_samples"] = 0;
                }
                RememberDraft(); Draft = next; MarkDraftEdited(); changed = true;
            }
        }
        if (changed)
        {
            correctionText = intent.UserText; ProposalSummary = "我按你刚才的说明调整了，请在波形上确认一下。";
            return intent.Mode == "ordered" ? "已沿现有声学边界调整前后类别，保留环境音间隙；仅形成待确认草稿，请用户检查分界并点击确认。尚未保存或训练。" : "已按用户明确说明调整波形草稿；请用户检查并点击确认，尚未保存或训练。";
        }
        ProposalSummary = "我知道你在纠正声音类别，但还不能确定边界。请在波形上选中那段教我。";
        return "已打开原录音，但现有声学判断没有足够的对应边界（或指定时间超出原音）；没有猜时间，也未保存标签。请用户在波形上选中片段纠正。";
    }
}
