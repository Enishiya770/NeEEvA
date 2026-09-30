using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace NeEEvA.Presentation
{
    public sealed partial class CompanionPresentation
    {
        private GameObject learningPage;
        private PersonalSingingLearningClient learningClient;
        private RectTransform learningContent;
        private PersonalSingingLearningWaveform learningWaveform;
        private InputField learningUrl, learningName, learningGroup;
        private Dropdown learningProfiles, learningRecordings, learningSource;
        private Text learningStatus, learningSelection, learningResults, learningHypothesis, learningRange;
        private Button learningEnable, learningConfirm, learningAdvancedToggle;
        private int learningVersion = -1;
        private string learningProfileSignature, learningRecordingSignature, learningRecordingId;
        private readonly List<string> learningProfileIds = new List<string>(), learningRecordingIds = new List<string>();
        private readonly List<GameObject> learningAdvancedObjects = new List<GameObject>();
        private bool refreshingLearning, buildingLearningAdvanced, learningAdvanced;
        private const float LearningWidth = 648;

        public void OpenVoiceTeaching() { if (built) OpenPage(3); }

        private void BuildLearningPage()
        {
            learningPage = Page("Personal voice learning");
            if (!preview && !editorScenePreview) learningClient = gameObject.AddComponent<PersonalSingingLearningClient>();
            var scroll = learningPage.AddComponent<ScrollRect>(); scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped;
            var viewport = Rect("Learning viewport", learningPage.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            viewport.gameObject.AddComponent<RectMask2D>(); var hit = viewport.gameObject.AddComponent<Image>(); hit.color = Clear;
            learningContent = Rect("Learning content", viewport, new Vector2(0, 1), Vector2.one, Vector2.zero, new Vector2(0, 590));
            learningContent.pivot = new Vector2(.5f, 1); scroll.viewport = viewport; scroll.content = learningContent;
            LearningText("Title", "教她听懂你的声音", 0, 42, 22);
            learningEnable = LearningButton("Capture toggle", "开始教她", 460, 0, 188, () => {
                if (learningClient == null) return;
                if (learningClient.CollectionEnabled) learningClient.SetCollection(false); else learningClient.BeginTeaching();
            });
            learningHypothesis = LearningText("Character hypothesis", "我会先判断你在说话还是唱歌。\n开启后照常交流，想纠正时再来这里。", 46, 48, 16);
            learningRecordings = LearningDropdown("Recordings", new[] { "开启后，最近的录音会出现在这里" }, 0, 102, 492);
            learningRecordings.onValueChanged.AddListener(i => { if (!refreshingLearning && learningClient != null && i > 0 && i < learningRecordingIds.Count) learningClient.SelectRecording(learningRecordingIds[i], true); });
            LearningButton("Refresh recordings", "刷新", 504, 102, 144, () => learningClient?.RefreshRecordings());
            var waveformRect = Rect("Original waveform", learningContent, new Vector2(0,1), new Vector2(0,1), new Vector2(324,-237), new Vector2(LearningWidth,174));
            learningWaveform = waveformRect.gameObject.AddComponent<PersonalSingingLearningWaveform>(); learningWaveform.Configure(interfaceFont);
            learningWaveform.SelectionChanged += UpdateLearningSelection;
            learningWaveform.BoundaryChanged += (a,b,c,d) => { learningClient?.ResizeRange(a,b,c,d); RefreshLearningPage(true); };
            LearningButton("Select whole", "选择整段", 0, 332, 140, () => learningWaveform.SelectAll(), 32);
            LearningButton("Zoom selection", "放大选区", 150, 332, 140, () => learningWaveform.ZoomSelection(), 32);
            LearningButton("Show all", "显示全段", 300, 332, 140, () => learningWaveform.ShowAll(), 32);
            learningSelection = LearningText("Original metadata", "等待原始录音", 372, 23, 13);
            learningRange = LearningText("Selected range", "点区间选中；拖动空白处框选，拖动两端调边界。", 398, 24, 14);
            LearningButton("Play current range", "听选中这段", 0, 428, 208, () => {
                if (learningWaveform.HasSelection) learningClient?.PlayOriginal(learningWaveform.SelectionStart.ToString(), learningWaveform.SelectionEnd.ToString(), false, false);
            });
            LearningButton("Play whole original", "听完整录音", 220, 428, 208, () => learningClient?.PlayOriginal("", "", true, true));
            LearningButton("Stop original", "停止回听", 440, 428, 208, () => learningClient?.StopPreview());
            string[] labels = { "speech", "singing", "environment", "uncertain" };
            for (int i = 0; i < labels.Length; i++)
            {
                string label = labels[i];
                var button = LearningButton("Label " + label, LabelName(label), i * 165, 478, 153, () => {
                    if (learningClient != null && learningWaveform.HasSelection)
                    { learningClient.ReplaceRange(learningWaveform.SelectionStart, learningWaveform.SelectionEnd, label); RefreshLearningPage(true); }
                });
                button.GetComponent<CompanionSurface>().color = Color.Lerp(Raised, PersonalSingingLearningWaveform.LabelColor(label), .22f);
            }
            LearningButton("Undo correction", "撤销修改", 0, 528, 144, () => learningClient?.UndoDraft());
            learningConfirm = LearningButton("Confirm human labels", "这是我的声音，确认并学习", 156, 528, 492, () => learningClient?.ConfirmAnnotations("user"));
            learningStatus = LearningText("Learning status", "只在你确认后学习。收起面板也能继续正常聊天。", 578, 44, 14);
            learningAdvancedToggle = LearningButton("Advanced toggle", "更多设置与学习进度 ▸", 0, 630, 300, () => SetLearningAdvanced(!learningAdvanced));
            buildingLearningAdvanced = true;
            LearningText("Advanced heading", "用户与采集", 690, 28, 18);
            learningProfiles = LearningDropdown("Profiles", new[] { "请选择用户档案" }, 0, 724, 492);
            learningProfiles.onValueChanged.AddListener(i => { if (!refreshingLearning && learningClient != null && i > 0 && i < learningProfileIds.Count) learningClient.SelectProfile(learningProfileIds[i]); });
            LearningButton("Refresh profiles", "刷新", 504, 724, 144, () => learningClient?.RefreshProfiles());
            learningName = LearningInput("Profile name", "", "第一次使用？填一个名字", 0, 774, 492);
            LearningButton("Create profile", "新建用户", 504, 774, 144, () => learningClient?.CreateProfile(learningName.text));
            learningGroup = LearningInput("Content name", "", "可选：歌曲或练习名称（同曲重唱用同名）", 0, 824, 492);
            LearningButton("New session", "换一轮", 504, 824, 144, () => learningClient?.NewSession(learningGroup.text));
            learningSource = LearningDropdown("Human source", new[] { "如果这段来自其他声源…", "外放声音", "混合声源", "未知声源" }, 0, 882, 386);
            LearningButton("Confirm other source", "按此声源保存", 398, 882, 250, () => {
                if (learningSource.value > 0) learningClient?.ConfirmAnnotations(new[] { "playback", "mixed", "unknown" }[learningSource.value - 1]);
            });
            LearningText("Advanced endpoint", "连接与维护", 942, 28, 18);
            learningUrl = LearningInput("Service URL", "http://192.168.114.163:9884", "学习服务地址", 0, 980, 492);
            LearningButton("Connect", "连接", 504, 980, 144, () => learningClient?.SetServiceUrl(learningUrl.text));
            LearningButton("Retry proposal", "重新判断", 0, 1030, 208, () => learningClient?.RequestProposal());
            LearningButton("Retry saved queue", "重试待传", 220, 1030, 208, () => learningClient?.RetryPending());
            LearningButton("View learning status", "查看进度", 440, 1030, 208, () => learningClient?.RefreshStatus());
            LearningButton("Train candidate", "重试学习", 0, 1080, 208, () => learningClient?.Train());
            LearningButton("Retry features", "重试声音分析", 220, 1080, 208, () => learningClient?.RetryFeatures());
            learningResults = LearningText("Learning results", "纠正保存后会在后台学习；已有记录会保留。\n这页的判断用于教学，尚未接管角色的正式歌唱裁切。", 1136, 70, 15);
            buildingLearningAdvanced = false;
            SetLearningAdvanced(false); RefreshLearningPage(true);
        }
        private void SetLearningAdvanced(bool value)
        {
            learningAdvanced = value; foreach (var obj in learningAdvancedObjects) obj.SetActive(value);
            learningAdvancedToggle.GetComponentInChildren<Text>().text = value ? "收起更多设置 ▾" : "更多设置与学习进度 ▸";
            ResizeLearningResults();
        }
        private void LearningTrack(GameObject obj) { if (buildingLearningAdvanced) learningAdvancedObjects.Add(obj); }
        private InputField LearningInput(string name, string initial, string placeholder, float x, float y, float width)
        {
            var rect = Rect(name, learningContent, new Vector2(0, 1), new Vector2(0, 1), new Vector2(x + width / 2, -y - 21), new Vector2(width, 42));
            var surface = rect.gameObject.AddComponent<CompanionSurface>(); surface.color = Raised; surface.radius = 9; surface.raycastTarget = true;
            var input = rect.gameObject.AddComponent<InputField>(); input.targetGraphic = surface; input.characterLimit = 200;
            var value = Label("Value", rect, "", 16, Ink, TextAnchor.MiddleLeft); Inset(value.rectTransform, 10, 4); input.textComponent = value;
            var hint = Label("Hint", rect, placeholder, 15, Muted, TextAnchor.MiddleLeft); Inset(hint.rectTransform, 10, 4); input.placeholder = hint;
            input.text = initial; LearningTrack(rect.gameObject); return input;
        }
        private Text LearningText(string name, string text, float y, float height, int size = 16)
        {
            var result = Label(name, learningContent, text, size, Ink, TextAnchor.UpperLeft); Top(result.rectTransform, y, height);
            result.horizontalOverflow = HorizontalWrapMode.Wrap; result.verticalOverflow = VerticalWrapMode.Truncate;
            LearningTrack(result.gameObject); return result;
        }
        private Button LearningButton(string name, string text, float x, float y, float width, UnityEngine.Events.UnityAction action, float height = 42)
        {
            var button = MakeButton(name, learningContent, CompanionIconKind.More, text, () => { if (!preview && !editorScenePreview) action(); }, width, height);
            Anchor(button.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(x + width / 2, -y - height / 2));
            // Words carry the meaning here; repeated ellipsis icons obscure short class buttons.
            button.GetComponentInChildren<CompanionIcon>().gameObject.SetActive(false);
            var caption = button.GetComponentInChildren<Text>(); caption.fontSize = 16; caption.alignment = TextAnchor.MiddleCenter; Inset(caption.rectTransform, 8, 3);
            LearningTrack(button.gameObject); return button;
        }
        private Dropdown LearningDropdown(string name, string[] options, float x, float y, float width)
        {
            var dropdown = MakeDropdown(learningContent, options); dropdown.name = name;
            var rect = dropdown.GetComponent<RectTransform>(); rect.sizeDelta = new Vector2(width, 42);
            Anchor(rect, new Vector2(0, 1), new Vector2(x + width / 2, -y - 21)); LearningTrack(dropdown.gameObject); return dropdown;
        }
        private void UpdateLearningSelection()
        {
            if (learningRange == null || learningWaveform == null) return;
            if (!learningWaveform.HasSelection) { learningRange.text = "点区间选中；拖动框选，拖动两端调边界。"; return; }
            double rate = learningClient != null ? Math.Max(1, learningClient.WaveformRate) : 16000;
            var selected = learningClient?.Draft.FirstOrDefault(x => (int)x["start_sample"] == learningWaveform.SelectionStart && (int)x["end_sample"] == learningWaveform.SelectionEnd);
            learningRange.text = "选中 " + (learningWaveform.SelectionStart / rate).ToString("0.00") + "–" + (learningWaveform.SelectionEnd / rate).ToString("0.00") + " 秒" + (selected == null ? " · 点下方类别纠正" : " · " + LabelName((string)selected["label"]) + " · 可拖动两端");
        }
        private void RefreshLearningPage(bool force = false)
        {
            if (learningWaveform != null && learningClient != null) learningWaveform.SetPlayhead(learningClient.PreviewSample);
            if (learningPage == null || learningClient == null || (!force && learningVersion == learningClient.Revision)) return;
            learningVersion = learningClient.Revision; refreshingLearning = true;
            try
            {
                learningStatus.text = learningClient.Status;
                learningEnable.GetComponentInChildren<Text>().text = learningClient.CollectionEnabled ? "暂停学习采集" : "开始教她";
                Tint(learningEnable, learningClient.CollectionEnabled);
                learningHypothesis.text = CompactLearningHypothesis();
                var profileSig = string.Join("|", learningClient.Profiles.Select(x => (string)x["profile_id"]));
                if (profileSig != learningProfileSignature)
                {
                    learningProfileSignature = profileSig; learningProfiles.ClearOptions(); learningProfileIds.Clear(); learningProfileIds.Add(null);
                    var options = new List<string> { "请选择用户档案" };
                    foreach (var p in learningClient.Profiles) { learningProfileIds.Add((string)p["profile_id"]); options.Add((string)p["display_name"] ?? "未命名用户"); }
                    learningProfiles.AddOptions(options);
                }
                learningProfiles.SetValueWithoutNotify(Math.Max(0, learningProfileIds.IndexOf(learningClient.ProfileId)));
                if (learningClient.NeedsProfileChoice && !learningAdvanced)
                {
                    SetLearningAdvanced(true);
                    learningContent.anchoredPosition = new Vector2(learningContent.anchoredPosition.x, 690);
                }
                var recordingSig = string.Join("|", learningClient.Recordings.Select(x => (string)x["recording_id"] + ":" + (string)x["annotation_revision"]));
                if (recordingSig != learningRecordingSignature)
                {
                    learningRecordingSignature = recordingSig; learningRecordings.ClearOptions(); learningRecordingIds.Clear(); learningRecordingIds.Add(null);
                    var options = new List<string> { "选择最近的一段录音" };
                    foreach (var row in learningClient.Recordings)
                    {
                        learningRecordingIds.Add((string)row["recording_id"]);
                        options.Add("第 " + (int?)row["capture_serial"] + " 段 · " + ((double?)row["duration_seconds"] ?? 0).ToString("0.0", CultureInfo.InvariantCulture) + " 秒 · " + ((int?)row["annotation_revision"] > 0 ? "已教过" : "等你检查"));
                    }
                    learningRecordings.AddOptions(options);
                }
                learningRecordings.SetValueWithoutNotify(Math.Max(0, learningRecordingIds.IndexOf((string)learningClient.Selected?["recording_id"])));
                var selected = learningClient.Selected;
                bool changed = learningRecordingId != (string)selected?["recording_id"];
                learningRecordingId = (string)selected?["recording_id"];
                learningWaveform.SetAudio(learningClient.WaveformSamples, learningClient.WaveformRate);
                learningWaveform.SetRegions(learningClient.Draft);
                if ((changed || !learningWaveform.HasSelection) && learningClient.Draft.Count > 0 && learningWaveform.SampleCount > 0)
                {
                    var region = learningClient.Draft.FirstOrDefault(x => (string)x["label"] == "singing") ?? learningClient.Draft.First;
                    learningWaveform.SelectSpan((int)region["start_sample"], (int)region["end_sample"]);
                }
                learningSelection.text = selected == null ? "原始波形会在录音后出现 · 蓝：讲话  金：歌唱  绿：环境  紫：待确认" :
                    ((double?)selected["duration_seconds"] ?? 0).ToString("0.00") + " 秒 · 蓝：讲话  金：歌唱  绿：环境  紫：待确认";
                learningConfirm.interactable = selected != null && learningClient.Draft.Count > 0 && !learningClient.Busy;
                learningConfirm.GetComponentInChildren<Text>().text = learningClient.HasUnsavedEdits ? "这是我的声音，确认这次纠正" : "这是我的声音，判断对了";
                learningResults.text = learningClient.Results + "\n待上传：" + learningClient.PendingCount + "\n教学判断尚未接管正式歌唱裁切。";
                UpdateLearningSelection(); ResizeLearningResults();
            }
            finally { refreshingLearning = false; }
        }
        private void ResizeLearningResults()
        {
            if (learningResults == null || learningContent == null) return;
            var settings = learningResults.GetGenerationSettings(new Vector2(LearningWidth, 0)); settings.scaleFactor = 1f;
            float height = Mathf.Max(70, Mathf.Ceil(learningResults.cachedTextGeneratorForLayout.GetPreferredHeight(learningResults.text, settings)) + 8f);
            Top(learningResults.rectTransform, 1136, height);
            learningContent.sizeDelta = new Vector2(learningContent.sizeDelta.x, learningAdvanced ? 1136 + height + 16 : 682);
        }
        private string CompactLearningHypothesis()
        {
            if (learningClient.CurrentProposal == null || learningClient.HasUnsavedEdits) return learningClient.ProposalSummary;
            int rate = (int?)learningClient.Selected?["sample_rate"] ?? 16000;
            var singing = learningClient.Draft.Where(x => (string)x["label"] == "singing").Take(2).ToArray();
            string guess = singing.Length == 0 ? "这次我还没听出明确的歌唱。" : "我觉得 " + string.Join("、", singing.Select(x => ((int)x["start_sample"] / (double)rate).ToString("0.0") + "–" + ((int)x["end_sample"] / (double)rate).ToString("0.0") + " 秒")) + "像在唱歌。";
            return guess + "\n各段判断已标在波形上；点错的那段，或拖动边界纠正我。";
        }
        private void ResizeLearningPanel()
        {
            bool teaching = selectedPage == 3;
            panelCard.sizeDelta = teaching ? new Vector2(700, 880) : new Vector2(520, 566);
            if (vrActive) { panelRoot.sizeDelta = panelCard.sizeDelta; Anchor(panelCard, new Vector2(.5f,.5f), Vector2.zero); }
            else Anchor(panelCard, new Vector2(1,0), teaching ? new Vector2(-370,450) : new Vector2(-296,365));
            if (panelHint != null) panelHint.text = teaching ? (vrActive ? "射线拖动波形选段 · 左手 Y 收起 · 确认后学习" : "拖动波形选段 · 确认后学习 · Esc 收起") : (vrActive ? "左手扳机：按住说话 · 左手 Y：收起 / 呼出" : "Esc 收起 · Enter 发送 · Shift + Enter 换行");
        }
        private static string LabelName(string label) => label == "speech" ? "讲话" : label == "singing" ? "歌唱" : label == "environment" ? "环境音" : "暂不确定";

        // Deterministic editor fixture; never constructs a service client or microphone.
        public void SetVoiceTeachingPreview(float[] pcm, int rate, JArray regions)
        {
            if (!preview && !editorScenePreview) return;
            learningWaveform.SetAudio(pcm, rate); learningWaveform.SetRegions(regions);
            var first = regions.FirstOrDefault(x => (string)x["label"] == "singing");
            if (first != null) learningWaveform.SelectSpan((int)first["start_sample"], (int)first["end_sample"]);
            learningHypothesis.text = "我觉得金色这段是在唱歌。你听一下，我分对了吗？\n点错的那段换个类别，或拖动边界后确认。";
            learningRecordings.ClearOptions(); learningRecordings.AddOptions(new List<string> { "最近一段 · " + (pcm.Length / (double)rate).ToString("0.00") + " 秒 · 等你检查" });
            learningSelection.text = "原始 PCM 波形 · 蓝：讲话  金：歌唱  绿：环境  紫：待确认";
        }
    }
}
