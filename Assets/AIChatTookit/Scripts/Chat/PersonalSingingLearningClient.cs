using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

// Optional development client. It cannot change ASR, crop audio or promote a model.
[DisallowMultipleComponent]
public sealed partial class PersonalSingingLearningClient : MonoBehaviour
{
    [SerializeField] private string serviceUrl = "http://192.168.114.163:9884";
    public string ServiceUrl => serviceUrl;
    public string ProfileId { get; private set; }
    public string SessionId { get; private set; }
    public string ContentGroup { get; private set; }
    public string ContentGroupName { get; private set; }
    public bool CollectionEnabled => leases.Enabled;
    public bool Busy { get; private set; }
    public string Status { get; private set; } = "采集关闭；尚未连接声音学习服务";
    public string Results { get; private set; } = "模型仅作候选；未启用正式裁切。";
    public JObject LastReport { get; private set; }
    public JArray Profiles { get; private set; } = new JArray();
    public JArray Recordings { get; private set; } = new JArray();
    public JObject Selected { get; private set; }
    public JArray Draft { get; private set; } = new JArray();
    public int Revision { get; private set; }
    public int SelectionRevision { get; private set; }
    public int PendingCount => outbox.Count;
    public string PendingSummary => string.Join("\n", outbox.Select(x =>
        ((string)Profiles.FirstOrDefault(p => (string)p["profile_id"] == x.Profile)?["display_name"] ?? "其他用户档案") + " / 第 " + (int?)x.Manifest["capture_serial"] + " 段 · " + x.State +
        (string.IsNullOrEmpty(x.Error) ? "" : "：" + x.Error)));
    private const int MaximumItems = 8, MaximumWavBytes = 16 * 1024 * 1024;
    private const long MaximumOutboxBytes = 64L * 1024 * 1024;
    private readonly PersonalLearningCaptureLeases leases = new PersonalLearningCaptureLeases();
    private readonly List<OutboxItem> outbox = new List<OutboxItem>();
    private readonly Dictionary<string, string> captureEndpoints = new Dictionary<string, string>();
    private bool outboxReady, uploadRunning;
    private string outboxDirectory, trackedJob;
    private float nextJobPoll;
    private int contextRevision, requestGeneration, previewGeneration;
    private AudioSource previewSource;
    private AudioClip previewClip;
    private UnityWebRequest interactiveRequest, uploadRequest, previewRequest;
    private Task<List<OutboxItem>> restoreTask;
    private sealed class OutboxItem
    {
        public string Profile, Url, WavPath, MetaPath, State = "待保存", Error;
        public JObject Manifest;
        public byte[] Memory;
        public long Bytes;
        public Task<JObject> Save;
    }
    private void Awake()
    {
        outboxDirectory = Path.Combine(Application.persistentDataPath, "PersonalSingingLearning", "outbox");
        previewSource = gameObject.AddComponent<AudioSource>();
        previewSource.playOnAwake = false; previewSource.spatialBlend = 0; previewSource.loop = false;
    }
    private void OnEnable()
    {
        PersonalSingingLearningCapture.Started += CaptureBegin;
        PersonalSingingLearningCapture.Completed += CaptureComplete;
        PersonalSingingLearningCapture.Aborted += CaptureAbort;
        PersonalSingingLearningCapture.Readiness += ReadyForCapture;
        PersonalSingingLearningCapture.ActiveCapture += CaptureActive;
        PersonalSingingLearningCapture.PitchChunk += ReceivePitchChunk;
        PersonalSingingLearningCapture.PitchCancelled += CancelPitchFlow;
        if (!outboxReady) StartCoroutine(RestoreOutbox());
        foreach (var item in outbox.Where(x => x.State == "待保存" && x.Save != null)) StartCoroutine(ObserveSaved(item));
    }
    private void OnDisable()
    {
        CancelTeachingRequests();
        CancelAllPitchFlows();
        contextRevision++; CancelInteractive();
        leases.SetEnabled(false); captureEndpoints.Clear();
        PersonalSingingLearningCapture.Started -= CaptureBegin;
        PersonalSingingLearningCapture.Completed -= CaptureComplete;
        PersonalSingingLearningCapture.Aborted -= CaptureAbort;
        PersonalSingingLearningCapture.Readiness -= ReadyForCapture;
        PersonalSingingLearningCapture.ActiveCapture -= CaptureActive;
        PersonalSingingLearningCapture.PitchChunk -= ReceivePitchChunk;
        PersonalSingingLearningCapture.PitchCancelled -= CancelPitchFlow;
        uploadRequest?.Abort(); uploadRequest = null; uploadRunning = false;
        foreach (var item in outbox.Where(x => x.State == "上传中")) item.State = "待上传";
        StopPreview(); StopAllCoroutines();
    }
    private void OnDestroy()
    {
        if (previewSource != null) Destroy(previewSource);
        if (outbox.Count > 0) Debug.LogWarning("[PersonalLearning] 待上传 " + outbox.Count + " 份；其中未确认落盘 " + outbox.Count(x => x.Memory != null) + " 份。检查队列目录：" + outboxDirectory);
    }
    private void Update()
    {
        UpdateTeaching();
        if (CollectionEnabled && outboxReady && !uploadRunning)
        {
            var next = outbox.FirstOrDefault(x => x.State == "待上传" && x.Profile == ProfileId && x.Url == serviceUrl);
            if (next != null) StartCoroutine(Upload(next));
        }
        if (!string.IsNullOrEmpty(trackedJob) && !Busy && Time.unscaledTime >= nextJobPoll)
        { nextJobPoll = Time.unscaledTime + 3f; PollJob(); }
    }
    private void Change(string message) { Status = message; Revision++; }
    public void SetServiceUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        { Change("请输入有效 http/https 服务地址"); return; }
        Pause(); CancelInteractive(); serviceUrl = value.Trim().TrimEnd('/'); ProfileId = SessionId = ContentGroup = ContentGroupName = null;
        Profiles = new JArray(); Recordings = new JArray(); ClearSelection(); contextRevision++;
        ResetTeachingProfileState();
        RefreshProfiles();
    }
    public void SetCollection(bool value)
    {
        if (value && (Busy || !outboxReady || string.IsNullOrEmpty(ProfileId) || string.IsNullOrEmpty(SessionId) || string.IsNullOrEmpty(ContentGroup)))
        { Change("请先连接服务、选择个人档案并创建有同曲分组的采集会话"); return; }
        if (value && (outbox.Count >= MaximumItems || outbox.Sum(x => x.Bytes) >= MaximumOutboxBytes))
        { Change("待上传队列已满；先重试现有上传。原录音仍保留，不接受新采集。"); return; }
        leases.SetEnabled(value); if (!value) { captureEndpoints.Clear(); CancelAllPitchFlows(); }
        Change(value ? "采集已开启：仅接受此后新开始的完整录音；不会自动开麦" : "采集暂停：不接收新录音、不开始新上传；正在上传的原件可完成保存");
    }
    public void Pause() { leases.SetEnabled(false); captureEndpoints.Clear(); CancelAllPitchFlows(); Change("采集已暂停，待上传原音保留"); }
    private bool ReadyForCapture() => isActiveAndEnabled && CollectionEnabled && outboxReady && outbox.Count < MaximumItems && outbox.Sum(x => x.Bytes) < MaximumOutboxBytes;
    private bool CaptureActive(string root, int serial) => isActiveAndEnabled && leases.Contains(root, serial);
    private void CaptureBegin(string root, int serial)
    {
        SnapshotSpokenCorrectionTarget();
        if (!ReadyForCapture()) return;
        leases.Begin(root, serial, ProfileId, SessionId, ContentGroup);
        if (leases.Contains(root, serial)) { captureEndpoints[root + "/" + serial] = serviceUrl; BeginPitchFlow(root, serial); }
    }
    private void CaptureAbort(string root, int serial) { leases.Abort(root, serial); captureEndpoints.Remove(root + "/" + serial); AbortUnsealedPitch(root, serial); }
    private void CaptureComplete(byte[] wav, string root, int serial, string parentId, string lineageJson)
    {
        if (!leases.TrySeal(root, serial, out var lease)) return;
        captureEndpoints.TryGetValue(root + "/" + serial, out var endpoint); captureEndpoints.Remove(root + "/" + serial);
        if (wav.Length > MaximumWavBytes || outbox.Count >= MaximumItems || outbox.Sum(x => x.Bytes) + wav.LongLength > MaximumOutboxBytes)
        { Pause(); Change("本轮未加入学习：超过 16 MiB 或待上传上限。已有待传原音保留；可恢复后重录。"); return; }
        if (!PersonalSingingLearningProtocol.TryWave(wav, out int rate, out int channels, out int count, out _, out var error) || channels > 2 || (double)count / rate > 120)
        { Change("本轮未加入学习：" + (error ?? "超过 120 秒或不支持声道数")); return; }
        var raw = (byte[])wav.Clone(); string id = "r-" + Guid.NewGuid().ToString("N");
        latestCapturedRecordingId = id;
        string pitchStream = SealPitchFlow(root, serial, rate, channels, count);
        var item = new OutboxItem { Profile = lease.Profile, Url = endpoint ?? serviceUrl, Memory = raw, Bytes = raw.LongLength,
            WavPath = Path.Combine(outboxDirectory, id + ".wav"), MetaPath = Path.Combine(outboxDirectory, id + ".json"),
            Manifest = new JObject { ["recording_id"] = id, ["session_id"] = lease.Session, ["content_group"] = lease.Group,
                ["capture_serial"] = serial, ["source_kind"] = "unknown", ["sample_rate"] = rate, ["sample_count"] = count,
                ["pitch_stream_id"] = pitchStream,
                ["parent_recording_id"] = string.IsNullOrWhiteSpace(parentId) ? null : "p-" + PersonalSingingLearningProtocol.Sha256(Encoding.UTF8.GetBytes(parentId)).Substring(0, 48),
                ["lineage_json"] = string.IsNullOrWhiteSpace(lineageJson) ? null : lineageJson } };
        outbox.Add(item); Change("原始录音已入队，等待保存；尚无人工标签");
        item.Save = Task.Run(() => Persist(item));
        StartCoroutine(ObserveSaved(item));
    }
    private static JObject Persist(OutboxItem item)
    {
        // The background writer only edits its detached metadata. UI reads the
        // original manifest until ObserveSaved publishes this result on main.
        var manifest = (JObject)item.Manifest.DeepClone();
        Directory.CreateDirectory(Path.GetDirectoryName(item.WavPath));
        manifest["audio_sha256"] = PersonalSingingLearningProtocol.Sha256(item.Memory);
        File.WriteAllBytes(item.WavPath, item.Memory);
        var metadata = new JObject { ["profile"] = item.Profile, ["url"] = item.Url, ["bytes"] = item.Bytes, ["manifest"] = manifest.DeepClone() };
        File.WriteAllText(item.MetaPath + ".tmp", metadata.ToString(Formatting.None), Encoding.UTF8);
        if (File.Exists(item.MetaPath)) File.Delete(item.MetaPath);
        File.Move(item.MetaPath + ".tmp", item.MetaPath);
        return manifest;
    }
    private IEnumerator ObserveSaved(OutboxItem item)
    {
        while (!item.Save.IsCompleted) yield return null;
        if (item.Save.IsFaulted) { item.State = "本地保存失败"; item.Error = item.Save.Exception.GetBaseException().Message; Pause(); Change("待上传原音仍在内存，本地保存失败；请重试后再退出：" + item.Error); }
        else { item.Manifest = item.Save.Result; item.Memory = null; item.State = "待上传"; Change("原音已保存到本地待传队列；未标注"); }
    }
    private IEnumerator RestoreOutbox()
    {
        if (restoreTask == null) restoreTask = Task.Run(() => {
            var values = new List<OutboxItem>();
            if (!Directory.Exists(outboxDirectory)) return values;
            foreach (string file in Directory.GetFiles(outboxDirectory, "*.json"))
            {
                var json = JObject.Parse(File.ReadAllText(file)); string wav = Path.ChangeExtension(file, ".wav");
                if (!File.Exists(wav)) throw new IOException("待传索引缺少原 WAV：" + Path.GetFileName(file));
                values.Add(new OutboxItem { MetaPath = file, WavPath = wav, Profile = (string)json["profile"], Url = (string)json["url"],
                    Bytes = new FileInfo(wav).Length, Manifest = (JObject)json["manifest"], State = "待上传" });
            }
            return values;
        });
        var task = restoreTask;
        while (!task.IsCompleted) yield return null;
        if (outboxReady) yield break;
        if (task.IsFaulted) { Change("本地待传队列读取失败；采集保持关闭：" + task.Exception.GetBaseException().Message); yield break; }
        outbox.AddRange(task.Result); outboxReady = true;
        Change(outbox.Count == 0 ? "采集关闭；点击连接后选择个人档案" : "已恢复 " + outbox.Count + " 份待传原音；选择原档案后可重试");
    }
    public void RetryPending()
    {
        foreach (var item in outbox.Where(x => x.Profile == ProfileId && x.Url == serviceUrl))
        {
            if (item.State == "本地保存失败" && item.Memory != null)
            { item.State = "待保存"; item.Error = null; item.Save = Task.Run(() => Persist(item)); StartCoroutine(ObserveSaved(item)); }
            else if (item.State == "上传失败") { item.State = "待上传"; item.Error = null; }
        }
        // Retry is explicit; allow the existing saved queue even while new capture remains off.
        if (!uploadRunning) { var item = outbox.FirstOrDefault(x => x.Profile == ProfileId && x.Url == serviceUrl && x.State == "待上传"); if (item != null) StartCoroutine(Upload(item, true)); }
        Change("已重试当前档案的待传记录；新采集开关保持原状态");
    }
    private IEnumerator Upload(OutboxItem item, bool explicitRetry = false)
    {
        uploadRunning = true; item.State = "上传中"; Revision++;
        var load = Task.Run(() => File.ReadAllBytes(item.WavPath));
        while (!load.IsCompleted) yield return null;
        if (load.IsFaulted || PersonalSingingLearningProtocol.Sha256(load.Result) != (string)item.Manifest["audio_sha256"])
        { item.State = "上传失败"; item.Error = "本地原音读取或哈希校验失败"; uploadRunning = false; Change(item.Error); yield break; }
        if (!isActiveAndEnabled || (!explicitRetry && !CollectionEnabled) || item.Profile != ProfileId || item.Url != serviceUrl)
        { item.State = "待上传"; uploadRunning = false; Revision++; yield break; }
        var form = new WWWForm(); form.AddField("manifest", item.Manifest.ToString(Formatting.None));
        form.AddBinaryData("audio_file", load.Result, (string)item.Manifest["recording_id"] + ".wav", "audio/wav");
        using (var request = UnityWebRequest.Post(item.Url + "/learning/profiles/" + Uri.EscapeDataString(item.Profile) + "/recordings", form))
        {
            uploadRequest = request; request.timeout = 30; yield return request.SendWebRequest();
            if (ReferenceEquals(uploadRequest, request)) uploadRequest = null;
            JObject reply = null;
            if (request.result == UnityWebRequest.Result.Success) try { reply = JObject.Parse(request.downloadHandler.text); } catch { }
            if (reply == null || (string)reply["recording_id"] != (string)item.Manifest["recording_id"] || (string)reply["audio_sha256"] != (string)item.Manifest["audio_sha256"])
            { item.State = "上传失败"; item.Error = Failure(request); Change("上传未确认，原音保留，可重试：" + item.Error); }
            else
            {
                PitchOriginalUploaded(item);
                try { File.Delete(item.MetaPath); File.Delete(item.WavPath); outbox.Remove(item); }
                catch (Exception error) { item.State = "上传失败"; item.Error = "服务已保存，本地清理失败；幂等重试安全：" + error.Message; }
                Change("录音已保存，我会先判断；需要教我时再检查波形");
                if (item.Profile == ProfileId && item.Url == serviceUrl) QueueLatestRecording((string)item.Manifest["recording_id"]);
            }
        }
        uploadRunning = false; Revision++;
    }

    private static bool ValidId(string value) => !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$");
    private string ProfileRoute => "/learning/profiles/" + Uri.EscapeDataString(ProfileId ?? "");
    public void RefreshProfiles() => StartInteractive("GET", "/learning/profiles", null, value => { Profiles = value["profiles"] as JArray ?? new JArray(); Change("服务已连接；请选择或新建个人档案"); });
    public void CreateProfile(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) { Change("请输入个人档案名称"); return; }
        string id = "p-" + Guid.NewGuid().ToString("N");
        StartInteractive("POST", "/learning/profiles", new JObject { ["profile_id"] = id, ["display_name"] = displayName.Trim() }, value => {
            Profiles.Add(value); SelectProfile(id); Change("个人档案已建立；填写同曲分组并新建采集会话"); });
    }
    public void SelectProfile(string id)
    {
        if (!Profiles.Any(x => (string)x["profile_id"] == id)) return;
        Pause(); CancelInteractive(); contextRevision++; ProfileId = id; SessionId = ContentGroup = ContentGroupName = null; trackedJob = null;
        ResetTeachingProfileState();
        RememberProfile();
        Recordings = new JArray(); ClearSelection(); Results = "档案已切换；模型候选仍不参与正式裁切。";
        if (!Busy) RefreshRecordings();
    }
    public void NewSession(string group)
    {
        if (string.IsNullOrEmpty(ProfileId) || string.IsNullOrWhiteSpace(group) || group.Trim().Length > 120)
        { Change("先选用户档案；填写 1–120 字歌曲／练习名称，同一首歌重唱请用同名"); return; }
        string display = group.Trim().Normalize(NormalizationForm.FormC);
        group = "g-" + PersonalSingingLearningProtocol.Sha256(Encoding.UTF8.GetBytes(display));
        group = group.Substring(0, 64);
        Pause(); CancelInteractive(); contextRevision++; SessionId = ContentGroup = ContentGroupName = null;
        string id = "s-" + Guid.NewGuid().ToString("N");
        StartInteractive("POST", ProfileRoute + "/sessions", new JObject { ["session_id"] = id, ["content_group"] = group }, value => {
            SessionId = (string)value["session_id"]; ContentGroup = (string)value["content_group"]; ContentGroupName = display; Change("新一轮就绪：" + display + "；需手动开启采集"); });
    }
    public void RefreshRecordings()
    {
        if (string.IsNullOrEmpty(ProfileId)) { Change("请先选择个人档案"); return; }
        StartInteractive("GET", ProfileRoute + "/recordings", null, value => { Recordings = value["recordings"] as JArray ?? new JArray(); Change("录音列表已更新；不会自动播放或标注"); });
    }
    public void SelectRecording(string id, bool userReview = false)
    {
        if (Busy) { Change("正在打开录音，请稍后再切换；当前修改已保留"); return; }
        RememberSelectionDraft();
        reviewPinned = userReview;
        ClearSelection(); if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(ProfileId)) return;
        int selection = SelectionRevision;
        StartInteractive("GET", ProfileRoute + "/recordings/" + Uri.EscapeDataString(id), null, value => {
            if (selection != SelectionRevision) return;
            Selected = value; Draft = (value["annotation"] as JObject)?["regions"] is JArray ranges ? (JArray)ranges.DeepClone() : new JArray();
            SelectionRevision++; InitializeTeachingSelection(); Change("已打开录音；我先判断，你可以回听并纠正"); });
    }
    private void ClearSelection() { StopPreview(); ClearTeachingSelection(); Selected = null; Draft = new JArray(); SelectionRevision++; Revision++; }
    public bool AddRange(string start, string end, bool seconds, string label, string toleranceMs)
    {
        if (Selected == null) { Change("请先选择录音"); return false; }
        int rate = (int?)Selected["sample_rate"] ?? 0, count = (int?)Selected["sample_count"] ?? 0;
        if (Array.IndexOf(PersonalSingingLearningProtocol.Labels, label) < 0) { Change("请明确选择区间标签"); return false; }
        if (!PersonalSingingLearningProtocol.TryRange(start, end, seconds, rate, count, out int a, out int b, out var error)) { Change(error); return false; }
        if (!double.TryParse(toleranceMs, NumberStyles.Float, CultureInfo.InvariantCulture, out var margin) || double.IsNaN(margin) || double.IsInfinity(margin) || margin < 0 || margin > 120000)
        { Change("请明确填写边界容差毫秒；0 表示你确认精确边界"); return false; }
        var next = (JArray)Draft.DeepClone(); next.Add(new JObject { ["start_sample"] = a, ["end_sample"] = b, ["label"] = label, ["tolerance_samples"] = (int)Math.Ceiling(margin * rate / 1000) });
        if (!PersonalSingingLearningProtocol.ValidateRanges(next, count, false, out error)) { Change(error); return false; }
        RememberDraft(); Draft = new JArray(next.OrderBy(x => (int)x["start_sample"])); MarkDraftEdited(); Change("区间已加入草稿，尚未提交人工确认"); return true;
    }
    public void RemoveLastRange() { if (Draft.Count > 0) { RememberDraft(); Draft.RemoveAt(Draft.Count - 1); MarkDraftEdited(); } Change("最后一条草稿区间已移除，尚未提交"); }
    public void ConfirmAnnotations(string sourceKind)
    {
        if (Selected == null || Array.IndexOf(PersonalSingingLearningProtocol.Sources, sourceKind) < 0)
        { Change("请选录音，并单独确认声源；默认未知不会自动变成本人"); return; }
        if (!PersonalSingingLearningProtocol.ValidateRanges(Draft, (int)Selected["sample_count"], false, out var error)) { Change(error); return; }
        var body = new JObject { ["expected_revision"] = (int?)Selected["annotation_revision"] ?? 0,
            ["confirmed"] = true, ["source_kind"] = sourceKind, ["auto_train"] = true, ["regions"] = Draft.DeepClone() };
        AddTeachingProvenance(body);
        string id = (string)Selected["recording_id"]; int selection = SelectionRevision, edits = draftEditRevision;
        StartInteractive("PUT", ProfileRoute + "/recordings/" + Uri.EscapeDataString(id) + "/annotations", body, value => {
            if (selection == SelectionRevision && Selected != null) { Selected["annotation_revision"] = value["annotation_revision"]?.DeepClone(); Selected["annotation"] = value["annotation"]?.DeepClone(); }
            if (selection == SelectionRevision) TeachingConfirmed(edits);
            TrackJob((string)value["auto_train_job_id"]); Change((bool?)value["auto_train_deferred"] == true ? "这次纠正已记下，后台稍忙，可稍后重试学习。" : "这次纠正已记下，正在后台调整；你可以继续和我交流。"); });
    }
    public void Train() { if (string.IsNullOrEmpty(ProfileId)) return; StartInteractive("POST", ProfileRoute + "/train", new JObject(), value => { TrackJob((string)value["job_id"]); Change("学习任务已排队，模型仅作候选"); }); }
    public void Shadow()
    {
        if (Selected == null) { Change("请先选择录音"); return; }
        StartInteractive("POST", ProfileRoute + "/shadow", new JObject { ["recording_id"] = Selected["recording_id"].DeepClone() }, value => { TrackJob((string)value["job_id"]); Change("候选推理已排队；不会改写人工标签或裁切"); });
    }
    public void RefreshStatus()
    {
        if (string.IsNullOrEmpty(ProfileId)) return;
        StartInteractive("GET", ProfileRoute + "/status", null, value => { LastReport = (JObject)value.DeepClone(); Results = PersonalSingingLearningResults.Describe(value); Revision++; });
    }
    public void RetryFeatures()
    {
        if (Selected == null) return;
        StartInteractive("POST", ProfileRoute + "/recordings/" + Uri.EscapeDataString((string)Selected["recording_id"]) + "/features", new JObject(), value => { TrackJob((string)value["job_id"]); Change("特征重试已排队"); });
    }
    private void TrackJob(string id) { if (!string.IsNullOrEmpty(id)) { trackedJob = id; nextJobPoll = 0; } }
    private void PollJob()
    {
        string id = trackedJob;
        StartInteractive("GET", "/learning/jobs/" + Uri.EscapeDataString(id), null, value => {
            if (trackedJob != id) return;
            LastReport = (JObject)value.DeepClone(); Results = PersonalSingingLearningResults.Describe(value); Revision++;
            string status = (string)value["status"];
            if (status != "queued" && status != "running")
            {
                trackedJob = null;
                var result = value["result"] as JObject;
                if (status == "completed" && (string)result?["status"] == "candidate") Change("已按你确认的例子调整判断，下一段我会再试一次。");
                else if (status == "completed" && (string)result?["status"] == "insufficient_data")
                {
                    string missing = result?["reasons"] is JArray reasons && reasons.Any(r => (string)r == "need_confirmed_non_singing_example") ? "讲话或环境音" : "本人唱歌";
                    Change("这次纠正已记下。再教我一小段" + missing + "，我就能比较学习。");
                }
                else Change(status == "completed" ? "声音分析已完成，可以继续检查波形。" : "这次后台学习没完成，纠正仍已保存，可在更多设置里重试。");
            }
        });
    }
    private void StartInteractive(string method, string path, JObject body, Action<JObject> complete)
    {
        if (Busy) { Change("服务请求进行中，请稍后重试"); return; }
        if (!isActiveAndEnabled) return;
        StartCoroutine(JsonRequest(method, path, body, complete, contextRevision));
    }
    private void CancelInteractive() { requestGeneration++; interactiveRequest?.Abort(); interactiveRequest = null; Busy = false; }
    private IEnumerator JsonRequest(string method, string path, JObject body, Action<JObject> complete, int owner)
    {
        int generation = ++requestGeneration; Busy = true; Revision++;
        using (var request = new UnityWebRequest(serviceUrl + path, method))
        {
            interactiveRequest = request; request.timeout = 15; request.downloadHandler = new DownloadHandlerBuffer();
            if (body != null) { request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString(Formatting.None))); request.SetRequestHeader("Content-Type", "application/json"); }
            yield return request.SendWebRequest();
            if (ReferenceEquals(interactiveRequest, request)) interactiveRequest = null;
            if (generation != requestGeneration) yield break;
            Busy = false;
            if (owner != contextRevision || !isActiveAndEnabled) yield break;
            if (request.result != UnityWebRequest.Result.Success)
            { trackedJob = null; Change("声音学习服务暂不可用/请求未完成，可重试：" + Failure(request)); yield break; }
            JObject result;
            try { result = JObject.Parse(request.downloadHandler.text); }
            catch (Exception e) { Change("服务响应无效：" + e.Message); yield break; }
            complete?.Invoke(result); Revision++;
        }
    }
    private static string Failure(UnityWebRequest request)
    {
        string detail = request.downloadHandler?.text;
        if (!string.IsNullOrEmpty(detail) && detail.Length > 500) detail = detail.Substring(0, 500);
        return request.responseCode + " " + request.error + (string.IsNullOrEmpty(detail) ? "" : " " + detail);
    }
    public void StopPreview()
    {
        previewGeneration++; previewRequest?.Abort(); previewRequest = null;
        ReleasePreviewAudio();
    }
    private void ReleasePreviewAudio()
    {
        previewSourceOffset = -1;
        if (previewSource != null) { previewSource.Stop(); previewSource.clip = null; }
        if (previewClip != null) Destroy(previewClip); previewClip = null;
    }
    public void PlayOriginal(string start, string end, bool seconds, bool whole)
    {
        if (Selected == null) { Change("先选择录音"); return; }
        int rate = (int)Selected["sample_rate"], count = (int)Selected["sample_count"], a = 0, b = count;
        if (!whole && !PersonalSingingLearningProtocol.TryRange(start, end, seconds, rate, count, out a, out b, out var error)) { Change(error); return; }
        StopPreview();
        if (PlayCachedOriginal(a, b)) return;
        if (Busy) { Change("原音仍在加载，请稍后回听"); return; }
        StartCoroutine(Preview((string)Selected["recording_id"], (string)Selected["audio_sha256"], a, b, SelectionRevision, contextRevision, previewGeneration));
    }
    private IEnumerator Preview(string id, string sha, int start, int end, int selection, int owner, int previewOwner)
    {
        int generation = ++requestGeneration; Busy = true;
        using (var request = UnityWebRequest.Get(serviceUrl + ProfileRoute + "/recordings/" + Uri.EscapeDataString(id) + "/audio"))
        {
            interactiveRequest = previewRequest = request; request.timeout = 20; yield return request.SendWebRequest();
            if (ReferenceEquals(interactiveRequest, request)) interactiveRequest = null;
            if (ReferenceEquals(previewRequest, request)) previewRequest = null;
            if (generation != requestGeneration) yield break;
            Busy = false;
            if (!isActiveAndEnabled || selection != SelectionRevision || owner != contextRevision || previewOwner != previewGeneration) yield break;
            if (request.result != UnityWebRequest.Result.Success) { Change("原音下载失败：" + Failure(request)); yield break; }
            byte[] raw = request.downloadHandler.data;
            if (PersonalSingingLearningProtocol.Sha256(raw) != sha || !PersonalSingingLearningProtocol.TryWave(raw, out int rate, out int channels, out int count, out int offset, out _) || start < 0 || end > count || start >= end)
            { Change("原音身份或选区校验失败，不播放"); yield break; }
            var pcm = new float[(end - start) * channels];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = BitConverter.ToInt16(raw, offset + (start * channels + i) * 2) / 32768f;
            ReleasePreviewAudio(); previewClip = AudioClip.Create("Personal learning original preview", end - start, channels, rate, false);
            previewSourceOffset = start;
            previewClip.SetData(pcm, 0); previewSource.clip = previewClip; previewSource.Play();
            Change("正在按原样本选区回听；未生成角色回复或新标签");
        }
    }
}
