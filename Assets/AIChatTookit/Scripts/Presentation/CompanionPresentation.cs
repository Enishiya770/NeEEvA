using System;
using System.Collections.Generic;
using NeEEvA.Player;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NeEEvA.Presentation
{
    /// <summary>Quiet desktop HUD and a summonable VR panel, sharing the existing conversation pipeline.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(1000)]
    public sealed class CompanionPresentation : MonoBehaviour
    {
        [SerializeField] private Font interfaceFont;
        [SerializeField] private Camera playerCamera;
        [SerializeField, Range(2f, 12f)] private float subtitleLinger = 6f;
        [SerializeField, Range(1.2f, 3f)] private float vrSubtitleDistance = 2f;
        [SerializeField, Range(1f, 2.5f)] private float vrPanelDistance = 1.5f;

        private static readonly Color Ink = new Color(.94f, .94f, .90f, 1f);
        private static readonly Color Muted = new Color(.65f, .70f, .69f, 1f);
        private static readonly Color Accent = new Color(.57f, .79f, .71f, 1f);
        private static readonly Color Surface = new Color(.075f, .105f, .112f, .96f);
        private static readonly Color Raised = new Color(.15f, .19f, .20f, 1f);
        private static readonly Color Clear = new Color(0f, 0f, 0f, 0f);
        [Serializable]
        private struct LegacyViewState
        {
            public Behaviour view;
            public bool wasEnabled;
        }
        // Persist the fallback separately: the scene itself must load with old rendering disabled.
        [SerializeField, HideInInspector] private List<LegacyViewState> legacyViews = new List<LegacyViewState>();
        private readonly List<GameObject> historyRows = new List<GameObject>();
        private ChatSample chat;
        private PlayerCameraController cameraController;
        private CompanionXRInteraction xr;
        private Canvas hudCanvas, panelCanvas;
        private RectTransform hudRoot, panelRoot, panelCard, subtitleCard, transcriptCard;
        private RectTransform historyContent;
        private GameObject desktopControls, panelBody, textPage, historyPage, settingsPage, recenterControl;
        private Text originalText, translationText, statusText, transcriptText, noticeText, realtimeText, panelHint;
        private Text speakerText, transcriptSpeakerText;
        private CompanionIcon statusIcon;
        private CanvasGroup subtitles, notices, transcript;
        private InputField composer;
        private ScrollRect historyScroll;
        private Button recordButton, sendButton, interruptButton;
        private Text recordLabel, composeHeading, composePlaceholder, speechHint;
        private readonly List<Button> tabButtons = new List<Button>();
        private readonly List<Button> subtitleButtons = new List<Button>();
        private readonly List<Button> noticeButtons = new List<Button>();
        private Dropdown languageDropdown;
        private float sourceChangedAt, transcriptChangedAt, nextHistoryRefresh;
        private string lastSource = string.Empty, lastTranslation = string.Empty, lastTranscript = string.Empty;
        private int lastHistoryVersion = -1, selectedPage;
        private bool built, vrActive, panelVisible, recording, preview;
        private bool editorScenePreview;
        private bool compositionWasActive;
        private string previewSource, previewTranslation;
        private Vector3 subtitleTargetPosition;
        private Quaternion subtitleTargetRotation;
        private int baseFontSize = 28;
        private int historyLimit = 80;
        private CompanionDesktopSubtitles desktopSubtitles;
        private bool desktopSubtitlesEnabled, desktopSubtitlesFailed;
        private Button desktopSubtitlesButton;
        private Text desktopSubtitlesLabel;
        private string projectedSource, projectedTranslation;
        private bool projectedOriginalVisible, projectedTranslationVisible;
        private int projectedFontSize;
        private float subtitleLayoutWidth = 790f;

        public bool IsPanelVisible => panelVisible;
        public bool IsVRPresentation => vrActive;

        private void Start()
        {
            if (built) return;
            chat = GetComponent<ChatSample>();
            if (chat == null) { enabled = false; SetLegacyViewsHidden(false); return; }
            if (playerCamera == null) playerCamera = Camera.main;
            if (playerCamera == null) { Debug.LogWarning("Companion UI needs the player camera.", this); enabled = false; SetLegacyViewsHidden(false); return; }
            Build();
            HideLegacyViews();
            if (desktopSubtitlesEnabled && WindowsDesktopSubtitleWindow.IsSupported)
                Application.runInBackground = true;
        }

        private void Build()
        {
            if (built) return;
            built = true;
            if (interfaceFont == null) interfaceFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            cameraController = playerCamera.GetComponent<PlayerCameraController>();
            baseFontSize = Mathf.Clamp(PlayerPrefs.GetInt("NeEEvA.UI.SubtitleSize", 28), 24, 34);
            desktopSubtitlesEnabled = PlayerPrefs.GetInt("NeEEvA.UI.DesktopSubtitles", 1) != 0;
            hudCanvas = CreateCanvas("Companion • Subtitles", 50, out hudRoot);
            panelCanvas = CreateCanvas("Companion • Controls", 60, out panelRoot);
            BuildHud();
            BuildPanel();
            if (!editorScenePreview)
            {
                xr = gameObject.AddComponent<CompanionXRInteraction>();
                xr.Configure(playerCamera, new[] { hudCanvas, panelCanvas });
                xr.TogglePanelRequested += TogglePanel;
                xr.RecenterRequested += RecenterPanel;
            }
            SetPanelVisible(false);
            ApplyPresentationMode(false);
        }

        private Canvas CreateCanvas(string label, int order, out RectTransform rect)
        {
            rect = Rect(label, null, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            // The camera can move freely; desktop UI and spatial UI never inherit scene scale.
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(rect.gameObject, gameObject.scene);
            var canvas = rect.gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = order;
            var scaler = rect.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600f, 900f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = .5f;
            rect.gameObject.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        private void BuildHud()
        {
            subtitleCard = Rect("Current sentence", hudRoot, new Vector2(.5f, 0f), new Vector2(.5f, 0f), new Vector2(0, 164), new Vector2(790, 132));
            var background = subtitleCard.gameObject.AddComponent<CompanionSurface>();
            background.color = new Color(.025f, .04f, .045f, .55f);
            background.radius = 16;
            subtitles = subtitleCard.gameObject.AddComponent<CanvasGroup>();
            subtitles.blocksRaycasts = false;
            subtitles.alpha = 0;
            speakerText = SpeakerLabel("Character name", subtitleCard, "角色", 18, Ink);
            originalText = Label("Original", subtitleCard, string.Empty, baseFontSize, Ink, TextAnchor.MiddleCenter);
            Place(originalText.rectTransform, new Vector2(0, .34f), Vector2.one, new Vector2(24, 0), new Vector2(-24, -12));
            originalText.lineSpacing = 1.12f;
            translationText = Label("Translation", subtitleCard, string.Empty, 21, Muted, TextAnchor.MiddleCenter);
            Place(translationText.rectTransform, Vector2.zero, new Vector2(1, .4f), new Vector2(24, 8), new Vector2(-24, 0));
            var noticeRect = Rect("Notice", hudRoot, new Vector2(.5f, 1), new Vector2(.5f, 1), new Vector2(0, -64), new Vector2(680, 62));
            var noticeSurface = noticeRect.gameObject.AddComponent<CompanionSurface>();
            noticeSurface.color = Surface;
            notices = noticeRect.gameObject.AddComponent<CanvasGroup>(); notices.alpha = 0; notices.blocksRaycasts = false;
            noticeText = Label("Notice text", noticeRect, string.Empty, 18, Ink, TextAnchor.MiddleCenter);
            Inset(noticeText.rectTransform, 20, 8);

            var controls = Rect("Desktop controls", hudRoot, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            desktopControls = controls.gameObject;
            var status = MakeButton("Speech status", controls, CompanionIconKind.Microphone, "实时对话关闭", ToggleRealtime, 220, 44);
            Anchor(status.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(142, 48));
            statusText = status.GetComponentInChildren<Text>(); statusText.fontSize = 16;
            statusIcon = status.GetComponentInChildren<CompanionIcon>();
            var history = MakeButton("Open history", controls, CompanionIconKind.History, "对话", () => OpenPage(1), 104, 44);
            Anchor(history.GetComponent<RectTransform>(), new Vector2(1, 0), new Vector2(-90, 48));
            var keyboard = MakeButton("Open text input", controls, CompanionIconKind.Keyboard, "文字", () => OpenPage(0), 104, 44);
            Anchor(keyboard.GetComponent<RectTransform>(), new Vector2(1, 0), new Vector2(-216, 48));
            var options = MakeButton("Open settings", controls, CompanionIconKind.More, string.Empty, () => OpenPage(2), 44, 44);
            Anchor(options.GetComponent<RectTransform>(), Vector2.one, new Vector2(-58, -46));

            transcriptCard = Rect("Live recognition", controls, Vector2.zero, Vector2.zero, new Vector2(266, 106), new Vector2(468, 42));
            var surface = transcriptCard.gameObject.AddComponent<CompanionSurface>(); surface.color = Surface; surface.radius = 12;
            transcript = transcriptCard.gameObject.AddComponent<CanvasGroup>(); transcript.blocksRaycasts = false; transcript.alpha = 0;
            transcriptSpeakerText = SpeakerLabel("User name", transcriptCard, "您", 16, Accent);
            var transcriptViewport = Rect("Recognition viewport", transcriptCard, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            Inset(transcriptViewport, 14, 4);
            transcriptViewport.gameObject.AddComponent<RectMask2D>();
            transcriptText = Label("Heard text", transcriptViewport, string.Empty, 17, Accent, TextAnchor.MiddleLeft);
            transcriptText.horizontalOverflow = HorizontalWrapMode.Overflow;
        }

        private void BuildPanel()
        {
            panelCard = Rect("Conversation panel", panelRoot, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-296, 365), new Vector2(520, 566));
            var bg = panelCard.gameObject.AddComponent<CompanionSurface>(); bg.color = Surface; bg.radius = 22; bg.raycastTarget = true;
            var shadow = panelCard.gameObject.AddComponent<Shadow>(); shadow.effectColor = new Color(0, 0, 0, .25f); shadow.effectDistance = new Vector2(0, -8);
            panelBody = panelCard.gameObject;
            var title = Label("Panel title", panelCard, "NeEEvA", 22, Ink, TextAnchor.MiddleLeft);
            Place(title.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(26, -66), new Vector2(-110, -20));
            var close = MakeButton("Close panel", panelCard, CompanionIconKind.Close, string.Empty, () => SetPanelVisible(false), 40, 40);
            Anchor(close.GetComponent<RectTransform>(), Vector2.one, new Vector2(-40, -42));
            var recenter = MakeButton("Recenter panel", panelCard, CompanionIconKind.Recenter, string.Empty, RecenterPanel, 40, 40);
            recenterControl = recenter.gameObject;
            Anchor(recenter.GetComponent<RectTransform>(), Vector2.one, new Vector2(-86, -42));
            var names = new[] { "说点什么", "对话记录", "偏好设置" };
            var icons = new[] { CompanionIconKind.Chat, CompanionIconKind.History, CompanionIconKind.Settings };
            for (int i = 0; i < 3; i++)
            {
                int page = i;
                var tab = MakeButton("Tab " + i, panelCard, icons[i], names[i], () => SelectPage(page), 148, 44);
                Anchor(tab.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(100 + i * 160, -100));
                tabButtons.Add(tab);
            }
            textPage = Page("Text and voice"); historyPage = Page("History"); settingsPage = Page("Preferences");
            BuildComposer(textPage.transform);
            BuildHistory(historyPage.transform);
            BuildSettings(settingsPage.transform);
            panelHint = Label("Panel hint", panelCard, "Esc 收起  ·  Enter 发送  ·  Shift + Enter 换行", 13, Muted, TextAnchor.MiddleLeft);
            Place(panelHint.rectTransform, Vector2.zero, new Vector2(1, 0), new Vector2(26, 16), new Vector2(-24, 44));
            SelectPage(0);
        }

        private GameObject Page(string name)
        {
            var page = Rect(name, panelCard, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            page.offsetMin = new Vector2(26, 56); page.offsetMax = new Vector2(-26, -138);
            return page.gameObject;
        }

        private void BuildComposer(Transform parent)
        {
            var eyebrow = Label("Compose heading", parent, "和她聊聊", 17, Muted, TextAnchor.UpperLeft);
            composeHeading = eyebrow;
            Top(eyebrow.rectTransform, 0, 30);
            var inputRect = Rect("Message input", parent, new Vector2(0, 1), Vector2.one, Vector2.zero, Vector2.zero);
            inputRect.offsetMin = new Vector2(0, -194); inputRect.offsetMax = new Vector2(0, -38);
            var inputSurface = inputRect.gameObject.AddComponent<CompanionSurface>(); inputSurface.color = Raised; inputSurface.radius = 14; inputSurface.raycastTarget = true;
            composer = inputRect.gameObject.AddComponent<InputField>(); composer.targetGraphic = inputSurface;
            var inputText = Label("Input text", inputRect, string.Empty, 21, Ink, TextAnchor.UpperLeft); Inset(inputText.rectTransform, 18, 14);
            inputText.supportRichText = false;
            composer.textComponent = inputText; composer.lineType = InputField.LineType.MultiLineNewline; composer.characterLimit = 2000;
            var placeholder = Label("Placeholder", inputRect, "在这里输入…", 21, Muted, TextAnchor.UpperLeft); Inset(placeholder.rectTransform, 18, 14);
            composePlaceholder = placeholder;
            composer.placeholder = placeholder; composer.selectionColor = new Color(Accent.r, Accent.g, Accent.b, .3f); composer.caretColor = Accent; composer.customCaretColor = true;
            sendButton = MakeButton("Send message", parent, CompanionIconKind.Send, "发送", Submit, 116, 44, true);
            Anchor(sendButton.GetComponent<RectTransform>(), Vector2.one, new Vector2(-58, -224));
            interruptButton = MakeButton("Interrupt speech", parent, CompanionIconKind.Stop, "停止回应", () => { if (chat != null) chat.Interrupt(); }, 142, 44);
            Anchor(interruptButton.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(71, -224));

            var rule = Rect("Divider", parent, new Vector2(0, 1), Vector2.one, Vector2.zero, Vector2.zero);
            rule.offsetMin = new Vector2(0, -272); rule.offsetMax = new Vector2(0, -271);
            var line = rule.gameObject.AddComponent<CompanionSurface>(); line.color = new Color(.5f, .6f, .6f, .15f); line.radius = 0;
            var realtime = MakeButton("Realtime conversation", parent, CompanionIconKind.Microphone, "开启实时对话", ToggleRealtime, 224, 46);
            Anchor(realtime.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(112, -310));
            realtimeText = realtime.GetComponentInChildren<Text>();
            recordButton = MakeButton("Push to talk", parent, CompanionIconKind.Microphone, "按住说话", null, 224, 46);
            Anchor(recordButton.GetComponent<RectTransform>(), Vector2.one, new Vector2(-112, -310));
            recordLabel = recordButton.GetComponentInChildren<Text>();
            var events = recordButton.gameObject.AddComponent<EventTrigger>();
            var down = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            down.callback.AddListener(_ => BeginRecord()); events.triggers.Add(down);
            var up = new EventTrigger.Entry { eventID = EventTriggerType.PointerUp };
            up.callback.AddListener(_ => EndRecord()); events.triggers.Add(up);
            var note = Label("Speech hint", parent, "实时对话开启后，直接说话即可。", 14, Muted, TextAnchor.UpperLeft);
            speechHint = note;
            Top(note.rectTransform, 346, 28);
        }

        private void BuildHistory(Transform parent)
        {
            var scroll = Rect("Messages", parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            historyScroll = scroll.gameObject.AddComponent<ScrollRect>(); historyScroll.horizontal = false; historyScroll.movementType = ScrollRect.MovementType.Clamped;
            var viewport = Rect("Viewport", scroll, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            viewport.gameObject.AddComponent<RectMask2D>();
            // A transparent graphic gives blank regions a surface for mouse-wheel and controller scrolling.
            var hit = viewport.gameObject.AddComponent<Image>(); hit.color = Clear; hit.raycastTarget = true;
            historyContent = Rect("Messages content", viewport, new Vector2(0, 1), Vector2.one, Vector2.zero, Vector2.zero);
            historyContent.pivot = new Vector2(.5f, 1);
            historyScroll.viewport = viewport; historyScroll.content = historyContent;
        }

        private void BuildSettings(Transform parent)
        {
            var heading = Label("Subtitle heading", parent, "字幕", 17, Muted, TextAnchor.UpperLeft); Top(heading.rectTransform, 0, 28);
            var modes = new[] { "关闭", "原文", "译文", "双语" };
            for (int i = 0; i < modes.Length; i++)
            {
                int mode = i;
                var button = MakeButton("Subtitle mode " + i, parent, CompanionIconKind.Subtitles, modes[i], () => { if (chat != null) chat.SetSubtitleDisplayMode(mode); RefreshSettings(); }, 108, 42);
                Anchor(button.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(54 + 120 * i, -57)); subtitleButtons.Add(button);
            }
            var language = Label("Language label", parent, "翻译语言", 17, Muted, TextAnchor.MiddleLeft); Top(language.rectTransform, 84, 42);
            languageDropdown = MakeDropdown(parent, new[] { "简体中文", "繁體中文", "日本語", "English", "한국어", "Français", "Deutsch", "Español" });
            Anchor(languageDropdown.GetComponent<RectTransform>(), Vector2.one, new Vector2(-120, -105));
            languageDropdown.onValueChanged.AddListener(value => { if (chat != null) chat.SetSubtitleTargetLanguage(value); });
            var size = Label("Size label", parent, "字幕字号", 17, Muted, TextAnchor.MiddleLeft); Top(size.rectTransform, 138, 42);
            var smaller = MakeButton("Smaller subtitles", parent, CompanionIconKind.Minimize, "小", () => SetSubtitleSize(baseFontSize - 2), 108, 42);
            Anchor(smaller.GetComponent<RectTransform>(), Vector2.one, new Vector2(-186, -159));
            var larger = MakeButton("Larger subtitles", parent, CompanionIconKind.Plus, "大", () => SetSubtitleSize(baseFontSize + 2), 108, 42);
            Anchor(larger.GetComponent<RectTransform>(), Vector2.one, new Vector2(-54, -159));
            var noticeLabel = Label("Notice label", parent, "系统提示", 17, Muted, TextAnchor.UpperLeft); Top(noticeLabel.rectTransform, 202, 28);
            // Existing enum is Hidden / ErrorsOnly / All; labels are checked against that contract.
            var options = new[] { "关闭", "仅错误", "全部" };
            for (int i = 0; i < 3; i++)
            {
                int mode = i;
                var b = MakeButton("Notice mode " + i, parent, CompanionIconKind.More, options[i], () => { if (chat != null) chat.SetSystemNoticeMode(mode); RefreshSettings(); }, 148, 42);
                Anchor(b.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(74 + 160 * i, -254)); noticeButtons.Add(b);
            }
            desktopSubtitlesButton = MakeButton("Desktop subtitles", parent, CompanionIconKind.Subtitles,
                "桌面悬浮字幕", ToggleDesktopSubtitles, 468, 42);
            desktopSubtitlesLabel = desktopSubtitlesButton.GetComponentInChildren<Text>();
            Anchor(desktopSubtitlesButton.GetComponent<RectTransform>(), new Vector2(.5f, 1), new Vector2(0, -316));
            var note = Label("Settings hint", parent, WindowsDesktopSubtitleWindow.IsSupported
                ? "切到其他应用时置顶显示，鼠标可穿透。" : "桌面悬浮字幕可在 Windows 电脑上使用。",
                14, Muted, TextAnchor.UpperLeft); Top(note.rectTransform, 346, 26);
        }

        private Dropdown MakeDropdown(Transform parent, string[] options)
        {
            var root = Rect("Translation language", parent, Vector2.one, Vector2.one, Vector2.zero, new Vector2(240, 42));
            var bg = root.gameObject.AddComponent<CompanionSurface>(); bg.color = Raised; bg.radius = 10; bg.raycastTarget = true;
            var dropdown = root.gameObject.AddComponent<Dropdown>(); dropdown.targetGraphic = bg;
            var label = Label("Selected language", root, options[0], 17, Ink, TextAnchor.MiddleLeft); Inset(label.rectTransform, 14, 4); label.rectTransform.offsetMax = new Vector2(-38, -4);
            Icon("Expand", root, CompanionIconKind.ChevronDown, new Vector2(1, .5f), new Vector2(-22, 0), 20, Muted);
            dropdown.captionText = label;
            var template = Rect("Template", root, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, -4), new Vector2(0, 240)); template.pivot = new Vector2(.5f, 1);
            var templateBg = template.gameObject.AddComponent<CompanionSurface>(); templateBg.color = Raised; templateBg.raycastTarget = true; templateBg.radius = 12;
            var scroll = template.gameObject.AddComponent<ScrollRect>(); scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped;
            var viewport = Rect("Viewport", template, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero); Inset(viewport, 4, 4); viewport.gameObject.AddComponent<RectMask2D>();
            var content = Rect("Content", viewport, new Vector2(0, 1), Vector2.one, Vector2.zero, new Vector2(0, 40)); content.pivot = new Vector2(.5f, 1);
            var item = Rect("Item", content, new Vector2(0, .5f), new Vector2(1, .5f), Vector2.zero, new Vector2(0, 40));
            var itemBg = item.gameObject.AddComponent<CompanionSurface>(); itemBg.color = Raised; itemBg.radius = 6; itemBg.raycastTarget = true;
            var toggle = item.gameObject.AddComponent<Toggle>(); toggle.targetGraphic = itemBg;
            var check = Icon("Selected", item, CompanionIconKind.Check, new Vector2(1, .5f), new Vector2(-20, 0), 18, Accent); toggle.graphic = check;
            var itemLabel = Label("Language", item, "", 17, Ink, TextAnchor.MiddleLeft); Inset(itemLabel.rectTransform, 12, 0); itemLabel.rectTransform.offsetMax = new Vector2(-36, 0);
            dropdown.itemText = itemLabel; dropdown.template = template;
            scroll.viewport = viewport; scroll.content = content;
            dropdown.AddOptions(new List<string>(options)); template.gameObject.SetActive(false);
            return dropdown;
        }

        private void Update()
        {
            if (!built) return;
            if (!preview)
            {
                bool active = cameraController != null && cameraController.IsVRActive;
                if (active != vrActive) ApplyPresentationMode(active);
                // Legacy components remain valid data sources but their old graphics cannot reappear.
                SetLegacyViewsHidden(true);
            }
            if (!vrActive) PositionDesktopSubtitles();
            UpdateSubtitles(); UpdateSpeechStatus();
            UpdateDesktopSubtitles();
            if (panelVisible && selectedPage == 1 && Time.unscaledTime >= nextHistoryRefresh)
            { nextHistoryRefresh = Time.unscaledTime + .3f; RefreshHistory(false); }
            var keyboard = Keyboard.current;
            bool compositionActive = !string.IsNullOrEmpty(Input.compositionString);
            if (keyboard != null && Application.isFocused)
            {
                if (!vrActive)
                {
                    if (keyboard.escapeKey.wasPressedThisFrame && panelVisible) SetPanelVisible(false);
                    else if (keyboard.tabKey.wasPressedThisFrame && !composer.isFocused) TogglePanel();
                }
                if (composer.isFocused && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) &&
                    !keyboard.shiftKey.isPressed && !compositionActive && !compositionWasActive) Submit();
            }
            compositionWasActive = compositionActive;
        }

        private void LateUpdate()
        {
            if (!built || !vrActive) return;
            Vector3 forward = HorizontalForward();
            Vector3 wanted = SubtitlePosition(forward);
            Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);
            // Comfortable dead zone: light subtitles follow only after a meaningful turn/step.
            if (Vector3.Distance(wanted, subtitleTargetPosition) > .35f || Quaternion.Angle(rotation, subtitleTargetRotation) > 18f)
            { subtitleTargetPosition = wanted; subtitleTargetRotation = rotation; }
            float t = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 6f);
            hudRoot.SetPositionAndRotation(Vector3.Lerp(hudRoot.position, subtitleTargetPosition, t), Quaternion.Slerp(hudRoot.rotation, subtitleTargetRotation, t));
        }

        private void UpdateSubtitles()
        {
            // Names are separate from the paged/streaming bodies, and refresh even when the text is unchanged.
            speakerText.text = !preview && chat != null ? chat.PresentationCharacterName : "角色";
            string source = preview ? previewSource ?? string.Empty : chat.PresentationSubtitleSource;
            if (source == "正在思考中..." || source == "正在思考中…") source = string.Empty;
            string translationValue = preview ? previewTranslation ?? string.Empty : chat.PresentationTranslation;
            if (source != lastSource || translationValue != lastTranslation)
            { sourceChangedAt = Time.unscaledTime; lastSource = source; lastTranslation = translationValue; }
            bool speaking = !preview && (chat.IsAISpeaking || chat.IsVoiceOutputPlaying);
            bool alive = (source.Length > 0 || translationValue.Length > 0) && (speaking || preview || Time.unscaledTime - sourceChangedAt < subtitleLinger);
            bool showOriginal = preview ? true : chat.PresentationShowOriginal;
            bool showTranslation = preview ? translationValue.Length > 0 : chat.PresentationShowTranslation;
            if (projectedSource != source || projectedTranslation != translationValue || projectedOriginalVisible != showOriginal ||
                projectedTranslationVisible != showTranslation || projectedFontSize != baseFontSize)
            {
                projectedSource = source; projectedTranslation = translationValue; projectedOriginalVisible = showOriginal;
                projectedTranslationVisible = showTranslation; projectedFontSize = baseFontSize;
                string nextOriginal = showOriginal ? CompanionSubtitlePager.CurrentPage(source, originalText) : string.Empty;
                string nextTranslation = showTranslation ? CompanionSubtitlePager.CurrentPage(translationValue, translationText) : string.Empty;
                originalText.text = nextOriginal; translationText.text = nextTranslation;
                originalText.gameObject.SetActive(nextOriginal.Length > 0); translationText.gameObject.SetActive(nextTranslation.Length > 0);
                float originalHeight = SubtitleHeight(originalText, nextOriginal);
                float translationHeight = SubtitleHeight(translationText, nextTranslation);
                float gap = originalHeight > 0 && translationHeight > 0 ? 6 : 0;
                subtitleCard.sizeDelta = new Vector2(subtitleLayoutWidth, originalHeight + translationHeight + gap + 24);
                Place(originalText.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(24, -12 - originalHeight), new Vector2(-24, -12));
                Place(translationText.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(24, -12 - originalHeight - gap - translationHeight), new Vector2(-24, -12 - originalHeight - gap));
                if (vrActive && panelVisible) subtitleTargetPosition = SubtitlePosition(HorizontalForward());
            }
            float wanted = alive && (originalText.text.Length > 0 || translationText.text.Length > 0) ? 1 : 0;
            subtitles.alpha = Mathf.MoveTowards(subtitles.alpha, wanted, Time.unscaledDeltaTime * 5f);
            string notice = preview ? string.Empty : chat.PresentationNotice;
            noticeText.text = notice;
            if (!preview) noticeText.color = chat.PresentationNoticeColor;
            notices.alpha = notice.Length > 0 ? 1 : 0;
            string heard = preview ? string.Empty : chat.PresentationTranscript;
            if (heard != lastTranscript) { lastTranscript = heard; transcriptChangedAt = Time.unscaledTime; }
            UpdateTranscriptText(heard);
            bool transcribing = recording || (!preview && chat.PresentationIsTranscribing);
            // Repeated/stable ASR hypotheses can pause while the user is still speaking.
            // Expire only after capture has ended, never in the middle of a live turn.
            if (transcribing) transcriptChangedAt = Time.unscaledTime;
            transcript.alpha = heard.Length > 0 && (transcribing || Time.unscaledTime - transcriptChangedAt < 5f) ? 1 : 0;
        }

        private string projectedTranscript;
        private float transcriptTextWidth;
        private void UpdateTranscriptText(string heard)
        {
            float width = transcriptText.rectTransform.rect.width;
            if (heard == projectedTranscript && Mathf.Approximately(width, transcriptTextWidth)) return;
            projectedTranscript = heard;
            transcriptTextWidth = width;
            // ASR hypotheses replace the whole current utterance and can revise punctuation
            // or earlier words. Sentence paging treated "你好。 …" as only the final "…".
            // Keep the full hypothesis; clip a single line and align its newest end in view.
            transcriptText.text = heard.Replace('\r', ' ').Replace('\n', ' ');
            var settings = transcriptText.GetGenerationSettings(new Vector2(10000f, 1000f));
            settings.scaleFactor = 1f;
            float preferredWidth = transcriptText.cachedTextGeneratorForLayout.GetPreferredWidth(transcriptText.text, settings);
            transcriptText.alignment = preferredWidth > width ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
        }
        private static float SubtitleHeight(Text text, string value)
        {
            if (value.Length == 0) return 0;
            var settings = text.GetGenerationSettings(new Vector2(Mathf.Max(1f, text.rectTransform.rect.width), 10000));
            settings.scaleFactor = 1f;
            return Mathf.Max(text.fontSize * 1.4f, text.cachedTextGeneratorForLayout.GetPreferredHeight(value, settings) + 4f);
        }

        private void UpdateSpeechStatus()
        {
            var realtime = !preview ? chat.PresentationRealtime : null;
            bool active = realtime != null && realtime.IsRealtimeEnabled;
            bool closing = realtime != null && realtime.IsRealtimeClosing;
            bool speaking = !preview && (chat.IsAISpeaking || chat.IsVoiceOutputPlaying);
            bool busy = !preview && chat.HasPendingConversationWork;
            bool listening = recording || (realtime != null && realtime.IsRecording);
            statusText.text = closing ? "正在结束本轮" : speaking ? "她在说话" : listening ? "正在听你说" : busy ? "正在回应" : active ? "实时对话已开启" : "实时对话关闭";
            statusText.color = active || listening ? Accent : Ink;
            statusIcon.kind = speaking ? CompanionIconKind.Volume : CompanionIconKind.Microphone;
            statusIcon.color = statusText.color;
            realtimeText.text = closing ? "结束中 · 点击恢复" : active ? "关闭实时对话" : "开启实时对话";
            recordButton.interactable = !active && !closing;
            recordLabel.text = recording ? "松开发送" : "按住说话";
            speechHint.text = active || closing ? "实时对话开启时，直接说话；关闭后可按住录音。" : "开启实时对话，或按住右侧按钮说话。";
            interruptButton.interactable = speaking || busy;
            sendButton.interactable = !string.IsNullOrWhiteSpace(composer.text);
        }

        private void HideLegacyViews()
        {
            CaptureLegacyViews();
            SetLegacyViewsHidden(true);
        }

        private bool CaptureLegacyViews()
        {
            if (chat == null) chat = GetComponent<ChatSample>();
            if (chat == null) return false;
            bool changed = false;
            foreach (var c in chat.PresentationLegacyCanvases)
            {
                if (c == null) continue;
                changed |= CaptureLegacyView(c);
                foreach (var raycaster in c.GetComponents<GraphicRaycaster>())
                    changed |= CaptureLegacyView(raycaster);
            }
            return changed;
        }

        private bool CaptureLegacyView(Behaviour view)
        {
            foreach (var state in legacyViews) if (state.view == view) return false;
            legacyViews.Add(new LegacyViewState { view = view, wasEnabled = view.enabled });
            return true;
        }

        private bool SetLegacyViewsHidden(bool hidden)
        {
            bool changed = false;
            foreach (var state in legacyViews)
            {
                if (state.view == null) continue;
                bool desired = !hidden && state.wasEnabled;
                if (state.view.enabled == desired) continue;
                state.view.enabled = desired;
                changed = true;
            }
            return changed;
        }

        private void ApplyPresentationMode(bool active)
        {
            vrActive = active;
            EndRecord();
            hudCanvas.renderMode = active ? RenderMode.WorldSpace : RenderMode.ScreenSpaceOverlay;
            panelCanvas.renderMode = active ? RenderMode.WorldSpace : RenderMode.ScreenSpaceOverlay;
            hudCanvas.worldCamera = playerCamera; panelCanvas.worldCamera = playerCamera;
            hudCanvas.GetComponent<CanvasScaler>().enabled = !active;
            panelCanvas.GetComponent<CanvasScaler>().enabled = !active;
            desktopControls.SetActive(!active);
            recenterControl.SetActive(active);
            composeHeading.text = active ? "语音交流，也可以连接键盘输入" : "和她聊聊";
            composePlaceholder.text = active ? "连接实体键盘后在这里输入…" : "在这里输入…";
            if (active)
            {
                hudRoot.sizeDelta = new Vector2(900, 260); hudRoot.localScale = Vector3.one * .00125f;
                panelRoot.sizeDelta = new Vector2(520, 566); panelRoot.localScale = Vector3.one * .0015f;
                Anchor(subtitleCard, new Vector2(.5f, .5f), Vector2.zero);
                subtitleLayoutWidth = 790f;
                subtitleCard.sizeDelta = new Vector2(subtitleLayoutWidth, subtitleCard.sizeDelta.y);
                projectedSource = null;
                Anchor(noticeText.transform.parent as RectTransform, new Vector2(.5f, 1), new Vector2(0, -22));
                Anchor(panelCard, new Vector2(.5f, .5f), Vector2.zero);
                Vector3 forward = HorizontalForward();
                subtitleTargetPosition = SubtitlePosition(forward);
                subtitleTargetRotation = Quaternion.LookRotation(forward, Vector3.up);
                hudRoot.SetPositionAndRotation(subtitleTargetPosition, subtitleTargetRotation);
                panelHint.text = "左手 Y：收起 / 呼出  ·  长按 Y：移到面前";
                RecenterPanel();
            }
            else
            {
                hudRoot.localScale = panelRoot.localScale = Vector3.one;
                hudRoot.rotation = panelRoot.rotation = Quaternion.identity;
                Anchor(subtitleCard, new Vector2(.5f, 0), new Vector2(0, 164));
                Anchor(noticeText.transform.parent as RectTransform, new Vector2(.5f, 1), new Vector2(0, -64));
                Anchor(panelCard, new Vector2(1, 0), new Vector2(-296, 365));
                panelHint.text = "Esc 收起  ·  Enter 发送  ·  Shift + Enter 换行";
            }
            xr?.SetVRActive(active);
            SetPanelVisible(false);
        }

        public void TogglePanel() => SetPanelVisible(!panelVisible);
        public void SetPanelVisible(bool visible)
        {
            if (!built) return;
            bool opening = visible && !panelVisible;
            if (!visible) EndRecord();
            panelVisible = visible; panelBody.SetActive(visible);
            if (!editorScenePreview && cameraController != null) cameraController.InteractionBlocked = visible && !vrActive;
            if (opening && vrActive) RecenterPanel();
            if (!visible && composer != null) composer.DeactivateInputField();
            xr?.SetPanelVisible(visible);
            if (visible) { RefreshSettings(); RefreshHistory(true); }
            if (vrActive)
            {
                Vector3 forward = HorizontalForward();
                subtitleTargetPosition = SubtitlePosition(forward);
                subtitleTargetRotation = Quaternion.LookRotation(forward, Vector3.up);
                hudRoot.SetPositionAndRotation(subtitleTargetPosition, subtitleTargetRotation);
            }
            else PositionDesktopSubtitles();
        }

        private void PositionDesktopSubtitles()
        {
            float canvasWidth = hudRoot.rect.width > 1f ? hudRoot.rect.width : 1600f;
            float availableWidth = panelVisible ? canvasWidth - panelCard.rect.width - 64f : canvasWidth;
            float width = Mathf.Min(790f, Mathf.Max(280f, availableWidth - 64f));
            if (!Mathf.Approximately(subtitleLayoutWidth, width))
            {
                subtitleLayoutWidth = width;
                subtitleCard.sizeDelta = new Vector2(width, subtitleCard.sizeDelta.y);
                projectedSource = null;
            }
            Vector2 position = new Vector2(availableWidth * .5f, 164f);
            if (subtitleCard.anchorMin != Vector2.zero || subtitleCard.anchorMax != Vector2.zero ||
                subtitleCard.anchoredPosition != position)
                Anchor(subtitleCard, Vector2.zero, position);
        }

        private Vector3 SubtitlePosition(Vector3 forward)
        {
            float down = .42f;
            if (panelVisible)
            {
                // Keep the caption below the panel's projected lower edge, even at a different depth.
                float panelBottom = .1f + panelRoot.rect.height * panelRoot.localScale.y * .5f;
                float captionHalfHeight = subtitleCard.rect.height * hudRoot.localScale.y * .5f;
                down = Mathf.Max(down, panelBottom * vrSubtitleDistance / vrPanelDistance + captionHalfHeight + .06f);
            }
            return playerCamera.transform.position + forward * vrSubtitleDistance + Vector3.down * down;
        }

        public void RecenterPanel()
        {
            if (!built || !vrActive) return;
            Vector3 forward = HorizontalForward();
            panelRoot.SetPositionAndRotation(playerCamera.transform.position + forward * vrPanelDistance + Vector3.down * .1f, Quaternion.LookRotation(forward, Vector3.up));
        }

        private Vector3 HorizontalForward()
        {
            Vector3 forward = Vector3.ProjectOnPlane(playerCamera.transform.forward, Vector3.up);
            return forward.sqrMagnitude > .001f ? forward.normalized : Vector3.forward;
        }

        private void OpenPage(int page)
        {
            SelectPage(page); SetPanelVisible(true);
            if (page == 0 && !vrActive) composer.ActivateInputField();
        }

        private void SelectPage(int page)
        {
            selectedPage = page;
            textPage.SetActive(page == 0); historyPage.SetActive(page == 1); settingsPage.SetActive(page == 2);
            if (page != 0) EndRecord();
            for (int i = 0; i < tabButtons.Count; i++) Tint(tabButtons[i], i == page);
            if (page == 1) RefreshHistory(true);
            if (page == 2) RefreshSettings();
        }

        private void Submit()
        {
            if (string.IsNullOrWhiteSpace(composer.text)) return;
            string value = composer.text.Trim();
            if (!preview && chat != null) chat.PresentationSubmit(value);
            composer.text = string.Empty;
            if (!vrActive) composer.ActivateInputField();
        }

        private void ToggleRealtime()
        {
            EndRecord();
            if (!preview && chat.PresentationRealtime != null) chat.PresentationRealtime.ToggleRealtimeMode();
        }

        private void BeginRecord()
        {
            if (preview || recording || !recordButton.interactable || chat == null) return;
            recording = true; chat.StartRecord();
        }

        private void EndRecord()
        {
            if (!recording) return;
            recording = false;
            if (chat != null) chat.StopRecord();
        }

        private void SetSubtitleSize(int value)
        {
            baseFontSize = Mathf.Clamp(value, 24, 34);
            originalText.fontSize = baseFontSize; translationText.fontSize = Mathf.RoundToInt(baseFontSize * .76f);
            if (!preview) PlayerPrefs.SetInt("NeEEvA.UI.SubtitleSize", baseFontSize);
        }

        private void ToggleDesktopSubtitles()
        {
            desktopSubtitlesEnabled = desktopSubtitlesFailed || !desktopSubtitlesEnabled;
            desktopSubtitlesFailed = false;
            if (!preview)
            {
                PlayerPrefs.SetInt("NeEEvA.UI.DesktopSubtitles", desktopSubtitlesEnabled ? 1 : 0);
                PlayerPrefs.Save();
                if (desktopSubtitlesEnabled && WindowsDesktopSubtitleWindow.IsSupported)
                    Application.runInBackground = true;
                else DisposeDesktopSubtitles();
            }
            RefreshSettings();
        }

        private void UpdateDesktopSubtitles()
        {
            if (preview) return;
            try
            {
                if (desktopSubtitles == null || desktopSubtitles.IsDisposed)
                {
                    desktopSubtitles = null;
                    // Editor layout previews and batch validation never open native desktop windows.
                    if (!desktopSubtitlesEnabled || desktopSubtitlesFailed || vrActive || !Application.isPlaying ||
                        Application.isBatchMode || !WindowsDesktopSubtitleWindow.IsSupported) return;
                    desktopSubtitles = new CompanionDesktopSubtitles(new WindowsDesktopSubtitleWindow(),
                        new WindowsDesktopSubtitleWindow(recognition: true));
                }
                desktopSubtitles.UpdateFrame(desktopSubtitlesEnabled, vrActive,
                    originalText.gameObject.activeSelf ? originalText.text : string.Empty,
                    translationText.gameObject.activeSelf ? translationText.text : string.Empty,
                    baseFontSize, subtitles.alpha, speakerText.text, transcriptText.text, transcript.alpha);
            }
            catch (Exception error)
            {
                desktopSubtitlesFailed = true;
                DisposeDesktopSubtitles();
                RefreshSettings();
                Debug.LogWarning("Desktop subtitles are unavailable: " + error.Message, this);
            }
        }

        private void DisposeDesktopSubtitles()
        {
            try { desktopSubtitles?.Dispose(); }
            catch (Exception error) { Debug.LogWarning("Desktop subtitle cleanup: " + error.Message, this); }
            finally { desktopSubtitles = null; }
        }

        private void RefreshSettings()
        {
            int mode = chat != null ? chat.PresentationSubtitleMode : 3;
            int noticeMode = chat != null ? chat.PresentationNoticeMode : 1;
            for (int i = 0; i < subtitleButtons.Count; i++) Tint(subtitleButtons[i], mode == i);
            for (int i = 0; i < noticeButtons.Count; i++) Tint(noticeButtons[i], noticeMode == i);
            if (languageDropdown != null) languageDropdown.SetValueWithoutNotify(chat != null ? chat.PresentationSubtitleLanguage : 0);
            if (desktopSubtitlesButton != null)
            {
                desktopSubtitlesButton.interactable = WindowsDesktopSubtitleWindow.IsSupported;
                desktopSubtitlesLabel.text = !WindowsDesktopSubtitleWindow.IsSupported ? "桌面悬浮字幕 · 仅 Windows" :
                    desktopSubtitlesFailed ? "桌面字幕暂不可用 · 点击重试" :
                    desktopSubtitlesEnabled ? "桌面悬浮字幕 · 已开启" : "桌面悬浮字幕 · 已关闭";
                Tint(desktopSubtitlesButton, desktopSubtitlesEnabled && !desktopSubtitlesFailed && WindowsDesktopSubtitleWindow.IsSupported);
            }
        }

        private void RefreshHistory(bool force)
        {
            if (historyContent == null) return;
            int version = chat != null ? chat.PresentationHistoryVersion : 0;
            if (!force && version == lastHistoryVersion) return;
            lastHistoryVersion = version;
            bool stayAtBottom = force || historyScroll.verticalNormalizedPosition < .08f;
            foreach (var row in historyRows) if (row != null) { row.SetActive(false); RemoveObject(row); }
            historyRows.Clear();
            float y = 0;
            if (preview)
            {
                AddHistoryRow("你", "今天有点累，想和你待一会儿。", true, ref y);
                AddHistoryRow("NeEEvA", "もちろん。ここで少し、ゆっくりしよう。\n当然可以。我们就在这里，慢慢休息一会儿吧。", false, ref y);
                AddHistoryRow("你", "嗯，我们能聊些什么呢？", true, ref y);
                AddHistoryRow("NeEEvA", "特別な話じゃなくてもいいよ。あなたの声、聞きたかったの。", false, ref y);
            }
            else if (chat != null)
            {
                var entries = chat.PresentationHistory;
                // Bound UI work during long sessions; full business history is retained by ChatSample.
                int first = Math.Max(0, entries.Count - historyLimit);
                if (first > 0)
                {
                    var earlier = MakeButton("Earlier messages", historyContent, CompanionIconKind.History, "加载更早对话", LoadEarlierHistory, 220, 42);
                    Anchor(earlier.GetComponent<RectTransform>(), new Vector2(.5f, 1), new Vector2(0, -21));
                    historyRows.Add(earlier.gameObject); y += 56;
                }
                for (int i = first; i < entries.Count; i++) AddHistoryRow(entries[i].IsUser ? "你" : "NeEEvA", entries[i].Text, entries[i].IsUser, ref y);
                if (entries.Count == 0) AddHistoryRow("", "对话会留在这里。\n现在，可以先和她打个招呼。", false, ref y);
            }
            historyContent.sizeDelta = new Vector2(0, y);
            Canvas.ForceUpdateCanvases();
            if (stayAtBottom) historyScroll.verticalNormalizedPosition = 0;
        }

        private void LoadEarlierHistory()
        {
            float previousHeight = historyContent.rect.height;
            Vector2 previousPosition = historyContent.anchoredPosition;
            historyLimit += 80;
            RefreshHistory(true);
            // Keep the currently-read message under the user's eyes after prepending older rows.
            historyContent.anchoredPosition = previousPosition + new Vector2(0, historyContent.rect.height - previousHeight);
        }

        private void AddHistoryRow(string speaker, string value, bool user, ref float y)
        {
            var row = Rect("Message", historyContent, new Vector2(0, 1), Vector2.one, Vector2.zero, Vector2.zero);
            row.pivot = new Vector2(.5f, 1);
            float inset = user ? 44 : 0;
            row.offsetMin = new Vector2(inset, 0); row.offsetMax = new Vector2(user ? 0 : -24, 0);
            var bg = row.gameObject.AddComponent<CompanionSurface>(); bg.color = user ? new Color(.15f, .23f, .22f, 1) : Raised; bg.radius = 12;
            var who = Label("Speaker", row, speaker, 13, user ? Accent : Muted, TextAnchor.UpperLeft);
            Place(who.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(16, -32), new Vector2(-16, -12));
            var body = Label("Message text", row, value, 19, Ink, TextAnchor.UpperLeft);
            body.lineSpacing = 1.12f;
            body.verticalOverflow = VerticalWrapMode.Overflow;
            float width = 468 - inset - (user ? 0 : 24) - 32;
            var settings = body.GetGenerationSettings(new Vector2(width, 10000));
            // Measure in stable logical UI units. The CanvasScaler can still be settling
            // when a page is opened; measuring at its interim pixel scale clipped CJK rows.
            settings.scaleFactor = 1f;
            float height = Mathf.Ceil(body.cachedTextGeneratorForLayout.GetPreferredHeight(value, settings)) + 4f;
            height = Mathf.Max(28, height);
            float total = height + 54;
            row.sizeDelta = new Vector2(row.sizeDelta.x, total); row.anchoredPosition = new Vector2(row.anchoredPosition.x, -y);
            Place(body.rectTransform, Vector2.zero, Vector2.one, new Vector2(16, 14), new Vector2(-16, -40));
            historyRows.Add(row.gameObject); y += total + 14;
        }

        private Text Label(string name, Transform parent, string value, int size, Color color, TextAnchor alignment)
        {
            var rect = Rect(name, parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var text = rect.gameObject.AddComponent<Text>(); text.font = interfaceFont; text.fontSize = size; text.color = color; text.alignment = alignment;
            text.text = value; text.supportRichText = false; text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private Text SpeakerLabel(string name, RectTransform card, string value, int size, Color color)
        {
            var text = Label(name, card, value, size, color, TextAnchor.MiddleLeft);
            Place(text.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(3, 4), new Vector2(-3, 30));
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 12;
            text.resizeTextMaxSize = size;
            var outline = text.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(.02f, .03f, .035f, .85f);
            outline.effectDistance = new Vector2(1, -1);
            return text;
        }

        private Button MakeButton(string name, Transform parent, CompanionIconKind icon, string text, UnityEngine.Events.UnityAction action, float width, float height, bool primary = false)
        {
            var rect = Rect(name, parent, new Vector2(.5f, .5f), new Vector2(.5f, .5f), Vector2.zero, new Vector2(width, height));
            var surface = rect.gameObject.AddComponent<CompanionSurface>(); surface.color = primary ? Accent : Raised; surface.radius = 11; surface.raycastTarget = true;
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = surface;
            var colors = button.colors; colors.normalColor = Color.white; colors.highlightedColor = new Color(1.18f, 1.18f, 1.18f); colors.pressedColor = new Color(.78f, .9f, .86f); colors.selectedColor = Color.white; colors.disabledColor = new Color(.7f, .7f, .7f, .45f); colors.fadeDuration = .12f; button.colors = colors;
            Color foreground = primary ? new Color(.08f, .15f, .13f, 1) : Ink;
            Icon(name + " icon", rect, icon, text.Length > 0 ? new Vector2(0, .5f) : new Vector2(.5f, .5f), text.Length > 0 ? new Vector2(23, 0) : Vector2.zero, 22, foreground);
            if (text.Length > 0)
            {
                var caption = Label("Label", rect, text, 16, foreground, TextAnchor.MiddleCenter);
                Place(caption.rectTransform, Vector2.zero, Vector2.one, new Vector2(42, 3), new Vector2(-10, -3));
            }
            if (action != null) button.onClick.AddListener(action);
            return button;
        }

        private static CompanionIcon Icon(string name, Transform parent, CompanionIconKind kind, Vector2 anchor, Vector2 offset, float size, Color color)
        {
            var rect = Rect(name, parent, anchor, anchor, offset, new Vector2(size, size));
            var icon = rect.gameObject.AddComponent<CompanionIcon>(); icon.kind = kind; icon.color = color; return icon;
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 min, Vector2 max, Vector2 position, Vector2 size)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false); rect.anchorMin = min; rect.anchorMax = max; rect.anchoredPosition = position; rect.sizeDelta = size;
            return rect;
        }
        private static void Place(RectTransform rect, Vector2 min, Vector2 max, Vector2 lower, Vector2 upper)
        { rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = lower; rect.offsetMax = upper; }
        private static void Inset(RectTransform rect, float x, float y) => Place(rect, Vector2.zero, Vector2.one, new Vector2(x, y), new Vector2(-x, -y));
        private static void Anchor(RectTransform rect, Vector2 anchor, Vector2 position)
        { rect.anchorMin = rect.anchorMax = anchor; rect.anchoredPosition = position; }
        private static void Top(RectTransform rect, float top, float height) => Place(rect, new Vector2(0, 1), Vector2.one, new Vector2(0, -top - height), new Vector2(0, -top));
        private static void Tint(Button button, bool selected)
        {
            button.targetGraphic.color = selected ? new Color(.20f, .32f, .29f, 1) : Raised;
            foreach (var text in button.GetComponentsInChildren<Text>()) text.color = selected ? Accent : Ink;
            foreach (var icon in button.GetComponentsInChildren<CompanionIcon>()) icon.color = selected ? Accent : Muted;
        }

        private void OnApplicationFocus(bool focused) { if (!focused) EndRecord(); }
        private void OnDisable()
        {
            DisposeDesktopSubtitles();
            EndRecord();
            if (!editorScenePreview && cameraController != null) cameraController.InteractionBlocked = false;
            if (hudCanvas != null) hudCanvas.gameObject.SetActive(false);
            if (panelCanvas != null) panelCanvas.gameObject.SetActive(false);
            if (xr != null) xr.SetVRActive(false);
            // Unity also calls OnDisable during domain reload. Only an actual opt-out restores old UI.
            SetLegacyViewsHidden(enabled && gameObject.activeInHierarchy);
        }
        private void OnEnable()
        {
            if (!built) return;
            hudCanvas.gameObject.SetActive(true); panelCanvas.gameObject.SetActive(true);
            SetLegacyViewsHidden(true);
            xr?.SetVRActive(vrActive); xr?.SetPanelVisible(panelVisible);
            if (!editorScenePreview && cameraController != null) cameraController.InteractionBlocked = panelVisible && !vrActive;
        }
        private void OnDestroy()
        {
            DisposeDesktopSubtitles();
            if (xr != null) { xr.TogglePanelRequested -= TogglePanel; xr.RecenterRequested -= RecenterPanel; RemoveObject(xr); }
            if (hudCanvas != null) RemoveObject(hudCanvas.gameObject);
            if (panelCanvas != null) RemoveObject(panelCanvas.gameObject);
        }

        private static void RemoveObject(UnityEngine.Object value)
        {
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }

#if UNITY_EDITOR
        /// <summary>Persist legacy suppression before saving or entering Play; no chat lifecycle runs.</summary>
        public bool PrepareEditorLegacyViews()
        {
            bool changed = CaptureLegacyViews();
            return SetLegacyViewsHidden(true) | changed;
        }

        public bool RestoreEditorLegacyViews() => SetLegacyViewsHidden(false);

        /// <summary>Editor-only visual copy. Never creates XR input, an EventSystem or chat services.</summary>
        public void ConfigureEditorScenePreview(Font font, Camera camera)
        {
            editorScenePreview = true;
            preview = true;
            interfaceFont = font;
            playerCamera = camera;
            previewSource = previewTranslation = string.Empty;
            Build();
            foreach (var canvas in new[] { hudCanvas, panelCanvas })
                foreach (var child in canvas.GetComponentsInChildren<Transform>(true))
                    child.gameObject.hideFlags = HideFlags.HideAndDontSave;
            TickEditorScenePreview();
        }

        public void TickEditorScenePreview()
        {
            if (!editorScenePreview || !built) return;
            PositionDesktopSubtitles();
            UpdateSubtitles();
            UpdateSpeechStatus();
            statusText.text = "编辑器预览";
            statusIcon.kind = CompanionIconKind.Eye;
            panelHint.text = "编辑器布局预览 · 进入 Play 后使用鼠标与语音";
        }

        public void DisposeEditorScenePreview()
        {
            if (!editorScenePreview) return;
            // Ordinary MonoBehaviours do not guarantee OnDestroy in edit mode.
            // The editor lease explicitly releases the two unparented, unsaved Canvas roots.
            if (hudCanvas != null) DestroyImmediate(hudCanvas.gameObject);
            if (panelCanvas != null) DestroyImmediate(panelCanvas.gameObject);
            hudCanvas = panelCanvas = null;
            built = false;
        }

        // An offline preview exercises the real visual tree without microphone, network or avatar services.
        public void ConfigurePreview(Font font, Camera camera, bool vr)
        {
            preview = true; interfaceFont = font; playerCamera = camera; chat = null;
            previewSource = "特別な話じゃなくてもいいよ。あなたの声、聞きたかったの。";
            previewTranslation = "聊些日常小事就好。只是，想听听你的声音。";
            Build(); ApplyPresentationMode(vr); UpdateSubtitles(); subtitles.alpha = 1;
        }
        public void SetPreviewPanel(string page)
        {
            if (page == "closed") SetPanelVisible(false);
            else OpenPage(page == "history" ? 1 : page == "settings" ? 2 : 0);
        }
        public void SetPreviewSubtitle(string source, string translation)
        {
            previewSource = source; previewTranslation = translation;
            if (!vrActive) PositionDesktopSubtitles();
            UpdateSubtitles();
            if (vrActive) hudRoot.position = SubtitlePosition(HorizontalForward());
            subtitles.alpha = string.IsNullOrEmpty(source) && string.IsNullOrEmpty(translation) ? 0 : 1;
        }
#endif
    }
}
