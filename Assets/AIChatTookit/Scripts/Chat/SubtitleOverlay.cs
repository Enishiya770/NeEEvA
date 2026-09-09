using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

public enum SubtitleDisplayMode
{
    Hidden = 0,
    OriginalOnly = 1,
    TranslationOnly = 2,
    Bilingual = 3,
}

public enum SubtitleLanguage
{
    ChineseSimplified = 0,
    ChineseTraditional = 1,
    Japanese = 2,
    English = 3,
    Korean = 4,
    French = 5,
    German = 6,
    Spanish = 7,
}

/// <summary>
/// One visual shell with three independent channels:
/// character original text, optional translation, and non-character system notices.
/// Translation is presentation-only and is never written to the character's history.
/// </summary>
[DisallowMultipleComponent]
public sealed class SubtitleOverlay : MonoBehaviour
{
    private const string k_DisplayModePreference = "NeEEvA.Subtitles.DisplayMode";
    private const string k_LastVisibleDisplayModePreference =
        "NeEEvA.Subtitles.LastVisibleDisplayMode";
    private const string k_TargetLanguagePreference = "NeEEvA.Subtitles.TargetLanguage";
    private const string k_NoticeModePreference = "NeEEvA.Subtitles.SystemNoticeMode";
    private const int k_MaxNoticeHistory = 64;
    private const int k_MaxTranslationCache = 128;
    // 翻译晚到时也不能整句跳出；以较快但仍可读的速度追赶当前语音位置。
    private const float k_TranslationCatchUpCharactersPerSecond = 36f;

    private sealed class TranslationWork
    {
        public int Generation;
        public int Sequence;
        public string Source;
        public SubtitleLanguage Language;
        public int SourceStartUnits;
        public int SourceUnits;
        public string Translation;
    }

    private Text m_OriginalText;
    private GameObject m_OriginalContainer;
    private Text m_TranslationText;
    private Text m_SystemNoticeText;
    private LLM m_Translator;
    private SubtitleDisplayMode m_DisplayMode = SubtitleDisplayMode.OriginalOnly;
    private SubtitleDisplayMode m_LastVisibleDisplayMode = SubtitleDisplayMode.OriginalOnly;
    private SubtitleLanguage m_TargetLanguage = SubtitleLanguage.ChineseSimplified;
    private SystemNoticeMode m_NoticeMode = SystemNoticeMode.ErrorsOnly;
    private bool m_EnableTranslation = true;
    private bool m_PersistSettings = true;
    private bool m_ShowNoticeCodes;

    private int m_CurrentGeneration = -1;
    private int m_TranslationSequence;
    private int m_NoticeVersion;
    private bool m_TranslationBusy;
    private bool m_TranslationFailureReported;
    private bool m_TranslationFallbackOriginalVisible;
    private int m_TotalTranslationSourceUnits;
    private int m_RevealedSourceUnits;
    private float m_TranslationRevealBudget;
    private readonly StringBuilder m_PendingSemanticSource = new StringBuilder();
    private readonly StringBuilder m_AuthorizedTranslationText = new StringBuilder();
    private readonly StringBuilder m_TranslatedText = new StringBuilder();
    private readonly Queue<TranslationWork> m_TranslationQueue = new Queue<TranslationWork>();
    private readonly List<TranslationWork> m_TranslationSegments =
        new List<TranslationWork>();
    private readonly Dictionary<string, string> m_TranslationCache =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly List<SystemNotice> m_NoticeHistory = new List<SystemNotice>();

    public SubtitleDisplayMode DisplayMode { get { return m_DisplayMode; } }
    public SubtitleLanguage TargetLanguage { get { return m_TargetLanguage; } }
    public SystemNoticeMode NoticeMode { get { return m_NoticeMode; } }
    public IList<SystemNotice> NoticeHistory { get { return m_NoticeHistory.AsReadOnly(); } }

    public void Initialize(
        Text originalText,
        LLM translator,
        SubtitleDisplayMode displayMode,
        SubtitleLanguage targetLanguage,
        SystemNoticeMode noticeMode,
        bool enableTranslation,
        bool persistSettings,
        bool showNoticeCodes)
    {
        m_OriginalText = originalText;
        m_Translator = translator;
        m_DisplayMode = displayMode;
        m_TargetLanguage = targetLanguage;
        m_NoticeMode = noticeMode;
        m_EnableTranslation = enableTranslation;
        m_PersistSettings = persistSettings;
        m_ShowNoticeCodes = showNoticeCodes;

        if (m_PersistSettings)
        {
            m_DisplayMode = ClampDisplayMode(PlayerPrefs.GetInt(
                k_DisplayModePreference, (int)m_DisplayMode));
            m_LastVisibleDisplayMode = ClampVisibleDisplayMode(PlayerPrefs.GetInt(
                k_LastVisibleDisplayModePreference, (int)m_LastVisibleDisplayMode));
            m_TargetLanguage = ClampLanguage(PlayerPrefs.GetInt(
                k_TargetLanguagePreference, (int)m_TargetLanguage));
            m_NoticeMode = ClampNoticeMode(PlayerPrefs.GetInt(
                k_NoticeModePreference, (int)m_NoticeMode));
        }
        if (m_DisplayMode != SubtitleDisplayMode.Hidden)
            m_LastVisibleDisplayMode = ClampVisibleDisplayMode((int)m_DisplayMode);

        EnsureRuntimeViews();
        ApplyVisibility();
    }

    public void SetDisplayMode(SubtitleDisplayMode mode)
    {
        m_DisplayMode = ClampDisplayMode((int)mode);
        if (m_DisplayMode != SubtitleDisplayMode.Hidden)
        {
            m_LastVisibleDisplayMode = ClampVisibleDisplayMode((int)m_DisplayMode);
            SavePreference(
                k_LastVisibleDisplayModePreference, (int)m_LastVisibleDisplayMode);
        }
        SavePreference(k_DisplayModePreference, (int)m_DisplayMode);
        if (m_DisplayMode == SubtitleDisplayMode.TranslationOnly &&
            m_TranslationFailureReported && m_TranslatedText.Length == 0)
            m_TranslationFallbackOriginalVisible = true;
        ApplyVisibility();
        if (!ShouldShowTranslation(m_DisplayMode)) CancelTranslation(false);
    }

    public void SetTargetLanguage(SubtitleLanguage language)
    {
        language = ClampLanguage((int)language);
        if (m_TargetLanguage == language) return;
        m_TargetLanguage = language;
        SavePreference(k_TargetLanguagePreference, (int)m_TargetLanguage);
        m_TranslationFailureReported = false;
        m_TranslationFallbackOriginalVisible = false;
        CancelTranslation(true);
        ApplyVisibility();
    }

    public void SetNoticeMode(SystemNoticeMode mode)
    {
        m_NoticeMode = ClampNoticeMode((int)mode);
        SavePreference(k_NoticeModePreference, (int)m_NoticeMode);
        if (m_SystemNoticeText != null && m_NoticeMode == SystemNoticeMode.Hidden)
            m_SystemNoticeText.gameObject.SetActive(false);
    }

    public void SetSubtitlesVisible(bool visible)
    {
        SetDisplayMode(visible ? m_LastVisibleDisplayMode : SubtitleDisplayMode.Hidden);
    }

    public void BeginUtterance(int generation)
    {
        m_CurrentGeneration = generation;
        m_TranslationSequence = 0;
        m_TranslationFailureReported = false;
        m_TranslationFallbackOriginalVisible = false;
        CancelTranslation(true);
    }

    /// <summary>
    /// Advances the translation display by the same source character that the original
    /// typewriter just revealed. Translation generation may be ahead or behind this clock;
    /// only presentation follows the spoken/original-subtitle progress.
    /// </summary>
    public void ReportSourceCharacterRevealed(int generation, char character)
    {
        if (generation != m_CurrentGeneration || char.IsWhiteSpace(character)) return;
        m_RevealedSourceUnits++;
        RebuildAuthorizedTranslation();
    }

    public void ReportSourceTextRevealed(
        int generation, string text, int startIndex = 0)
    {
        if (generation != m_CurrentGeneration || string.IsNullOrEmpty(text)) return;
        startIndex = Mathf.Clamp(startIndex, 0, text.Length);
        for (int i = startIndex; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i])) m_RevealedSourceUnits++;
        }
        RebuildAuthorizedTranslation();
    }

    /// <summary>
    /// Adds already-sanitized character text.  Hard TTS cuts may arrive with
    /// semanticBoundary=false; those pieces are accumulated before translation.
    /// </summary>
    public void QueueTranslationChunk(int generation, string text, bool semanticBoundary)
    {
        if (generation != m_CurrentGeneration || string.IsNullOrWhiteSpace(text)) return;
        if (!ShouldShowTranslation(m_DisplayMode)) return;
        if (!m_EnableTranslation)
        {
            ShowOriginalAsTranslationFallback();
            return;
        }
        if (m_Translator == null || !m_Translator.SupportsUtilityMessages)
        {
            ShowOriginalAsTranslationFallback();
            ReportTranslationFailureOnce(
                "The configured LLM provider does not support stateless utility messages.");
            return;
        }

        m_PendingSemanticSource.Append(text.Trim());
        if (!semanticBoundary) return;

        string source = m_PendingSemanticSource.ToString().Trim();
        m_PendingSemanticSource.Length = 0;
        if (source.Length == 0) return;

        var work = new TranslationWork
        {
            Generation = generation,
            Sequence = m_TranslationSequence++,
            Source = source,
            Language = m_TargetLanguage,
            SourceStartUnits = m_TotalTranslationSourceUnits,
            SourceUnits = CountSourceUnits(source),
            Translation = "",
        };
        m_TotalTranslationSourceUnits += work.SourceUnits;
        m_TranslationSegments.Add(work);
        m_TranslationQueue.Enqueue(work);
        RebuildAuthorizedTranslation();
        PumpTranslationQueue();
    }

    public void CancelUtterance(int generation, bool clearTranslation)
    {
        if (generation != m_CurrentGeneration) return;
        CancelTranslation(clearTranslation);
    }

    public void PublishSystemNotice(SystemNotice notice)
    {
        if (notice == null) return;
        m_NoticeHistory.Add(notice);
        while (m_NoticeHistory.Count > k_MaxNoticeHistory) m_NoticeHistory.RemoveAt(0);

        if (!ShouldShowNotice(m_NoticeMode, notice.Severity)) return;
        EnsureRuntimeViews();
        if (m_SystemNoticeText == null) return;

        string message = LocalizeNotice(notice.Code, notice.Message, m_TargetLanguage);
        if (m_ShowNoticeCodes && !string.IsNullOrWhiteSpace(notice.Code))
            message += "  [" + notice.Code + "]";
        m_SystemNoticeText.text = message;
        m_SystemNoticeText.color = NoticeColor(notice.Severity);
        m_SystemNoticeText.gameObject.SetActive(true);

        int version = ++m_NoticeVersion;
        float seconds = notice.Severity == SystemNoticeSeverity.Error
            ? 10f
            : notice.Severity == SystemNoticeSeverity.Warning ? 6f : 3f;
        StartCoroutine(HideNoticeAfter(version, seconds));
    }

    private void EnsureRuntimeViews()
    {
        if (m_OriginalText == null) return;
        if (m_OriginalContainer == null)
        {
            Transform parent = m_OriginalText.transform.parent;
            m_OriginalContainer = parent != null && parent.childCount == 1
                ? parent.gameObject
                : m_OriginalText.gameObject;
        }

        Transform overlayRoot = m_OriginalText.transform.parent;
        if (overlayRoot != null && overlayRoot.parent != null)
            overlayRoot = overlayRoot.parent;
        if (overlayRoot == null) overlayRoot = m_OriginalText.transform;

        if (m_TranslationText == null)
        {
            m_TranslationText = CreateSiblingText(
                overlayRoot, "TranslationSubtitle", m_OriginalText,
                Mathf.Max(18, Mathf.RoundToInt(m_OriginalText.fontSize * 0.74f)),
                new Color(0.72f, 0.88f, 1f, 0.96f));
        }
        if (m_SystemNoticeText == null)
        {
            m_SystemNoticeText = CreateSiblingText(
                overlayRoot, "SystemNotice", m_OriginalText,
                Mathf.Max(17, Mathf.RoundToInt(m_OriginalText.fontSize * 0.68f)),
                NoticeColor(SystemNoticeSeverity.Info));
            if (m_SystemNoticeText != null)
            {
                Outline outline = m_SystemNoticeText.gameObject.AddComponent<Outline>();
                outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
                outline.effectDistance = new Vector2(1f, -1f);
            }
        }
    }

    private static Text CreateSiblingText(
        Transform parent,
        string objectName,
        Text template,
        int fontSize,
        Color color)
    {
        if (parent == null || template == null) return null;
        GameObject go = new GameObject(
            objectName,
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Text),
            typeof(LayoutElement));
        go.layer = template.gameObject.layer;
        go.transform.SetParent(parent, false);

        Text text = go.GetComponent<Text>();
        text.font = template.font;
        text.fontSize = fontSize;
        text.fontStyle = template.fontStyle;
        text.lineSpacing = template.lineSpacing;
        text.supportRichText = false;
        text.alignment = template.alignment;
        text.alignByGeometry = template.alignByGeometry;
        text.resizeTextForBestFit = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.color = color;
        text.raycastTarget = false;
        text.text = "";

        LayoutElement layout = go.GetComponent<LayoutElement>();
        layout.preferredWidth = 700f;
        layout.flexibleWidth = 1f;
        layout.minHeight = fontSize + 6f;
        go.SetActive(false);
        return text;
    }

    private void ApplyVisibility()
    {
        EnsureRuntimeViews();
        bool showOriginal = ShouldShowOriginal(m_DisplayMode) ||
            (m_DisplayMode == SubtitleDisplayMode.TranslationOnly &&
             m_TranslationFallbackOriginalVisible);
        bool showTranslation = ShouldShowTranslation(m_DisplayMode);
        if (m_OriginalContainer != null) m_OriginalContainer.SetActive(showOriginal);
        if (m_TranslationText != null)
        {
            m_TranslationText.text = showTranslation ? m_TranslatedText.ToString() : "";
            m_TranslationText.gameObject.SetActive(showTranslation);
        }
    }

    private bool ShouldTranslateNow()
    {
        return m_EnableTranslation && ShouldShowTranslation(m_DisplayMode) &&
            m_Translator != null && m_Translator.SupportsUtilityMessages;
    }

    private void PumpTranslationQueue()
    {
        if (m_TranslationBusy || m_TranslationQueue.Count == 0) return;
        if (!ShouldTranslateNow())
        {
            m_TranslationQueue.Clear();
            return;
        }

        TranslationWork work = m_TranslationQueue.Dequeue();
        if (work.Generation != m_CurrentGeneration || work.Language != m_TargetLanguage)
        {
            PumpTranslationQueue();
            return;
        }

        string cacheKey = ((int)work.Language) + "\n" + NormalizeCacheText(work.Source);
        if (m_TranslationCache.TryGetValue(cacheKey, out string cached))
        {
            AppendTranslation(work, cached);
            PumpTranslationQueue();
            return;
        }

        m_TranslationBusy = true;
        string systemPrompt = BuildTranslationSystemPrompt(work.Language);
        m_Translator.PostUtilityMessage(
            systemPrompt,
            work.Source,
            (success, output, detail) =>
            {
                m_TranslationBusy = false;
                if (work.Generation != m_CurrentGeneration || work.Language != m_TargetLanguage)
                {
                    PumpTranslationQueue();
                    return;
                }

                string clean = CleanTranslation(output);
                if (success && clean.Length > 0)
                {
                    if (!m_TranslationFailureReported)
                    {
                        m_TranslationFallbackOriginalVisible = false;
                        ApplyVisibility();
                    }
                    if (m_TranslationCache.Count >= k_MaxTranslationCache)
                        m_TranslationCache.Clear();
                    m_TranslationCache[cacheKey] = clean;
                    AppendTranslation(work, clean);
                }
                else
                {
                    ShowOriginalAsTranslationFallback();
                    ReportTranslationFailureOnce(detail);
                }
                PumpTranslationQueue();
            });
    }

    private void AppendTranslation(TranslationWork work, string translated)
    {
        if (work.Generation != m_CurrentGeneration || string.IsNullOrWhiteSpace(translated)) return;
        work.Translation = translated.Trim();
        RebuildAuthorizedTranslation();
    }

    private void CancelTranslation(bool clearVisible)
    {
        m_TranslationQueue.Clear();
        m_PendingSemanticSource.Length = 0;
        m_TranslationBusy = false;
        if (m_Translator != null) m_Translator.CancelUtilityMessage();
        if (clearVisible)
        {
            m_TranslationSegments.Clear();
            m_TotalTranslationSourceUnits = 0;
            m_RevealedSourceUnits = 0;
            m_TranslationRevealBudget = 0f;
            m_AuthorizedTranslationText.Length = 0;
            m_TranslatedText.Length = 0;
            if (m_TranslationText != null) m_TranslationText.text = "";
        }
    }

    private void RebuildAuthorizedTranslation()
    {
        m_AuthorizedTranslationText.Length = 0;
        for (int i = 0; i < m_TranslationSegments.Count; i++)
        {
            TranslationWork segment = m_TranslationSegments[i];
            if (segment == null || string.IsNullOrEmpty(segment.Translation)) break;

            int sourceUnits = Mathf.Max(1, segment.SourceUnits);
            int revealed = Mathf.Clamp(
                m_RevealedSourceUnits - segment.SourceStartUnits, 0, sourceUnits);
            if (revealed <= 0) break;

            int authorizedChars = ComputeAuthorizedTranslationCharacters(
                sourceUnits, revealed, segment.Translation.Length);
            authorizedChars = SafePrefixLength(segment.Translation, authorizedChars);
            if (authorizedChars <= 0) break;

            if (m_AuthorizedTranslationText.Length > 0 &&
                UsesSpacesBetweenSentences(segment.Language))
                m_AuthorizedTranslationText.Append(' ');
            m_AuthorizedTranslationText.Append(
                segment.Translation, 0, authorizedChars);
            if (authorizedChars < segment.Translation.Length) break;
        }

        // A generation/language reset clears both builders. This guard only protects
        // against an unexpected non-monotonic authorization change.
        if (m_TranslatedText.Length > m_AuthorizedTranslationText.Length)
        {
            m_TranslatedText.Length = 0;
            m_TranslationRevealBudget = 0f;
        }
    }

    private void Update()
    {
        if (!ShouldShowTranslation(m_DisplayMode) ||
            m_TranslatedText.Length >= m_AuthorizedTranslationText.Length)
            return;

        m_TranslationRevealBudget +=
            Time.unscaledDeltaTime * k_TranslationCatchUpCharactersPerSecond;
        int available = Mathf.FloorToInt(m_TranslationRevealBudget);
        if (available <= 0) return;

        string authorized = m_AuthorizedTranslationText.ToString();
        int appended = 0;
        while (available > 0 && m_TranslatedText.Length < authorized.Length)
        {
            int index = m_TranslatedText.Length;
            int take = 1;
            if (char.IsHighSurrogate(authorized[index]) &&
                index + 1 < authorized.Length && char.IsLowSurrogate(authorized[index + 1]))
                take = 2;
            if (take > available) break;
            m_TranslatedText.Append(authorized, index, take);
            available -= take;
            appended += take;
        }
        m_TranslationRevealBudget = Mathf.Max(
            0f, m_TranslationRevealBudget - appended);

        if (appended > 0 && m_TranslationText != null)
        {
            m_TranslationText.text = m_TranslatedText.ToString();
            m_TranslationText.gameObject.SetActive(
                ShouldShowTranslation(m_DisplayMode));
        }
    }

    private static int CountSourceUnits(string text)
    {
        int count = 0;
        if (string.IsNullOrEmpty(text)) return count;
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i])) count++;
        }
        return count;
    }

    public static int ComputeAuthorizedTranslationCharacters(
        int sourceUnits, int revealedSourceUnits, int translationCharacters)
    {
        sourceUnits = Mathf.Max(1, sourceUnits);
        revealedSourceUnits = Mathf.Clamp(revealedSourceUnits, 0, sourceUnits);
        translationCharacters = Mathf.Max(0, translationCharacters);
        if (revealedSourceUnits >= sourceUnits) return translationCharacters;
        return Mathf.FloorToInt(
            translationCharacters * (revealedSourceUnits / (float)sourceUnits));
    }

    private static int SafePrefixLength(string text, int requested)
    {
        requested = Mathf.Clamp(requested, 0, string.IsNullOrEmpty(text) ? 0 : text.Length);
        if (requested > 0 && requested < text.Length &&
            char.IsHighSurrogate(text[requested - 1]) &&
            char.IsLowSurrogate(text[requested]))
            requested--;
        return requested;
    }

    private void ShowOriginalAsTranslationFallback()
    {
        if (m_DisplayMode != SubtitleDisplayMode.TranslationOnly) return;
        if (m_TranslationFallbackOriginalVisible) return;
        m_TranslationFallbackOriginalVisible = true;
        ApplyVisibility();
    }

    private void ReportTranslationFailureOnce(string detail)
    {
        if (m_TranslationFailureReported) return;
        m_TranslationFailureReported = true;
        PublishSystemNotice(new SystemNotice(
            "translation_unavailable",
            SystemNoticeSeverity.Warning,
            "翻译暂时不可用，原文字幕仍可正常显示。",
            detail,
            "SubtitleTranslation",
            true));
    }

    private IEnumerator HideNoticeAfter(int version, float seconds)
    {
        yield return new WaitForSecondsRealtime(seconds);
        if (version == m_NoticeVersion && m_SystemNoticeText != null)
            m_SystemNoticeText.gameObject.SetActive(false);
    }

    private void SavePreference(string key, int value)
    {
        if (!m_PersistSettings) return;
        PlayerPrefs.SetInt(key, value);
        PlayerPrefs.Save();
    }

    public static bool ShouldShowOriginal(SubtitleDisplayMode mode)
    {
        return mode == SubtitleDisplayMode.OriginalOnly || mode == SubtitleDisplayMode.Bilingual;
    }

    public static bool ShouldShowTranslation(SubtitleDisplayMode mode)
    {
        return mode == SubtitleDisplayMode.TranslationOnly || mode == SubtitleDisplayMode.Bilingual;
    }

    public static bool ShouldShowNotice(SystemNoticeMode mode, SystemNoticeSeverity severity)
    {
        return mode == SystemNoticeMode.All ||
            (mode == SystemNoticeMode.ErrorsOnly && severity == SystemNoticeSeverity.Error);
    }

    public static string BuildTranslationSystemPrompt(SubtitleLanguage language)
    {
        return "You are a subtitle translator. Translate the user text into " +
            LanguageName(language) +
            ". Preserve meaning, tone, names, hesitation and sentence boundaries. " +
            "Do not answer the text, explain it, add notes, use Markdown, or wrap it in quotes. " +
            "Output only the translation.";
    }

    public static string CleanTranslation(string text)
    {
        string clean = (text ?? "").Trim();
        if (clean.StartsWith("```", StringComparison.Ordinal))
        {
            int firstBreak = clean.IndexOf('\n');
            if (firstBreak >= 0) clean = clean.Substring(firstBreak + 1);
            int fence = clean.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) clean = clean.Substring(0, fence);
            clean = clean.Trim();
        }
        string[] prefixes = { "Translation:", "Translated text:", "翻译：", "翻譯：", "訳：" };
        for (int i = 0; i < prefixes.Length; i++)
        {
            if (clean.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(prefixes[i].Length).Trim();
                break;
            }
        }
        if (clean.Length >= 2 &&
            ((clean[0] == '"' && clean[clean.Length - 1] == '"') ||
             (clean[0] == '“' && clean[clean.Length - 1] == '”')))
            clean = clean.Substring(1, clean.Length - 2).Trim();
        return clean.Replace("\r", "").Trim();
    }

    private static string NormalizeCacheText(string text)
    {
        return (text ?? "").Trim().Replace("\r", "").Replace("\n", " ");
    }

    private static bool UsesSpacesBetweenSentences(SubtitleLanguage language)
    {
        return language == SubtitleLanguage.English || language == SubtitleLanguage.French ||
            language == SubtitleLanguage.German || language == SubtitleLanguage.Spanish;
    }

    private static string LanguageName(SubtitleLanguage language)
    {
        switch (language)
        {
            case SubtitleLanguage.ChineseTraditional: return "Traditional Chinese";
            case SubtitleLanguage.Japanese: return "Japanese";
            case SubtitleLanguage.English: return "English";
            case SubtitleLanguage.Korean: return "Korean";
            case SubtitleLanguage.French: return "French";
            case SubtitleLanguage.German: return "German";
            case SubtitleLanguage.Spanish: return "Spanish";
            default: return "Simplified Chinese";
        }
    }

    private static string LocalizeNotice(
        string code, string fallback, SubtitleLanguage language)
    {
        string key = (code ?? "").Trim().ToLowerInvariant();
        if (language == SubtitleLanguage.English)
        {
            if (key == "tool_correction_exhausted") return "The requested action could not be completed. Please try again.";
            if (key == "llm_response_failed") return "The character response could not be generated.";
            if (key == "tts_timeout") return "Voice synthesis timed out.";
            if (key == "tts_failed") return "Part of the voice response could not be played.";
            if (key == "translation_unavailable") return "Translation is temporarily unavailable; original subtitles still work.";
        }
        else if (language == SubtitleLanguage.Japanese)
        {
            if (key == "tool_correction_exhausted") return "要求された処理を完了できませんでした。もう一度お試しください。";
            if (key == "llm_response_failed") return "キャラクターの返答を生成できませんでした。";
            if (key == "tts_timeout") return "音声合成がタイムアウトしました。";
            if (key == "tts_failed") return "音声の一部を再生できませんでした。";
            if (key == "translation_unavailable") return "翻訳は一時的に利用できません。原文字幕は表示できます。";
        }
        else if (language == SubtitleLanguage.ChineseTraditional)
        {
            if (key == "tool_correction_exhausted") return "本輪操作未能完成，請重新詢問或稍後再試。";
            if (key == "llm_response_failed") return "角色回覆生成失敗，請稍後再試。";
            if (key == "tts_timeout") return "語音合成逾時。";
            if (key == "tts_failed") return "部分語音未能播放。";
            if (key == "translation_unavailable") return "翻譯暫時無法使用，原文字幕仍可正常顯示。";
        }
        else if (language == SubtitleLanguage.Korean)
        {
            if (key == "tool_correction_exhausted") return "요청한 작업을 완료하지 못했습니다. 다시 시도해 주세요.";
            if (key == "llm_response_failed") return "캐릭터의 응답을 생성하지 못했습니다.";
            if (key == "tts_timeout") return "음성 합성 시간이 초과되었습니다.";
            if (key == "tts_failed") return "음성 응답의 일부를 재생하지 못했습니다.";
            if (key == "translation_unavailable") return "번역을 일시적으로 사용할 수 없습니다. 원문 자막은 계속 표시됩니다.";
        }
        else if (language == SubtitleLanguage.French)
        {
            if (key == "tool_correction_exhausted") return "L’action demandée n’a pas pu être effectuée. Veuillez réessayer.";
            if (key == "llm_response_failed") return "La réponse du personnage n’a pas pu être générée.";
            if (key == "tts_timeout") return "La synthèse vocale a expiré.";
            if (key == "tts_failed") return "Une partie de la réponse vocale n’a pas pu être lue.";
            if (key == "translation_unavailable") return "La traduction est temporairement indisponible ; les sous-titres originaux restent affichés.";
        }
        else if (language == SubtitleLanguage.German)
        {
            if (key == "tool_correction_exhausted") return "Die angeforderte Aktion konnte nicht abgeschlossen werden. Bitte erneut versuchen.";
            if (key == "llm_response_failed") return "Die Antwort der Figur konnte nicht erzeugt werden.";
            if (key == "tts_timeout") return "Zeitüberschreitung bei der Sprachsynthese.";
            if (key == "tts_failed") return "Ein Teil der Sprachausgabe konnte nicht wiedergegeben werden.";
            if (key == "translation_unavailable") return "Die Übersetzung ist vorübergehend nicht verfügbar; die Originaluntertitel bleiben sichtbar.";
        }
        else if (language == SubtitleLanguage.Spanish)
        {
            if (key == "tool_correction_exhausted") return "No se pudo completar la acción solicitada. Inténtalo de nuevo.";
            if (key == "llm_response_failed") return "No se pudo generar la respuesta del personaje.";
            if (key == "tts_timeout") return "La síntesis de voz superó el tiempo de espera.";
            if (key == "tts_failed") return "No se pudo reproducir parte de la respuesta de voz.";
            if (key == "translation_unavailable") return "La traducción no está disponible temporalmente; los subtítulos originales siguen visibles.";
        }
        if (key == "tool_correction_exhausted") return "本轮操作未能完成，请重新询问或稍后再试。";
        if (key == "llm_response_failed") return "角色回复生成失败，请稍后再试。";
        if (key == "tts_timeout") return "语音合成超时。";
        if (key == "tts_failed") return "部分语音未能播放。";
        if (key == "translation_unavailable") return "翻译暂时不可用，原文字幕仍可正常显示。";
        return string.IsNullOrWhiteSpace(fallback) ? "程序状态发生变化。" : fallback.Trim();
    }

    private static Color NoticeColor(SystemNoticeSeverity severity)
    {
        if (severity == SystemNoticeSeverity.Error) return new Color(1f, 0.42f, 0.38f, 1f);
        if (severity == SystemNoticeSeverity.Warning) return new Color(1f, 0.78f, 0.28f, 1f);
        return new Color(0.65f, 0.86f, 1f, 1f);
    }

    private static SubtitleDisplayMode ClampDisplayMode(int value)
    {
        return (SubtitleDisplayMode)Mathf.Clamp(value, 0, 3);
    }

    private static SubtitleDisplayMode ClampVisibleDisplayMode(int value)
    {
        return (SubtitleDisplayMode)Mathf.Clamp(value, 1, 3);
    }

    private static SubtitleLanguage ClampLanguage(int value)
    {
        return (SubtitleLanguage)Mathf.Clamp(value, 0, 7);
    }

    private static SystemNoticeMode ClampNoticeMode(int value)
    {
        return (SystemNoticeMode)Mathf.Clamp(value, 0, 2);
    }

    private void OnDestroy()
    {
        if (m_Translator != null) m_Translator.CancelUtilityMessage();
    }
}
