using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UnityEngine;

/// <summary>A visible conversation row, projected from the existing business history.</summary>
public readonly struct ChatPresentationHistoryEntry
{
    public readonly bool IsUser;
    public readonly string Role;
    public readonly string Text;

    public ChatPresentationHistoryEntry(bool isUser, string text)
    {
        IsUser = isUser;
        Role = isUser ? "user" : "assistant";
        Text = text ?? "";
    }
}

public partial class ChatSample
{
    private RTSpeechHandler m_PresentationRealtime;
    private float m_PresentationNextRealtimeLookup = -1f;
    private Canvas[] m_PresentationLegacyCanvases;
    private GameObject m_PresentationChatCanvasSource;
    private GameObject m_PresentationHistoryCanvasSource;
    private readonly List<string> m_PresentationHistorySource = new List<string>();
    private readonly List<ChatPresentationHistoryEntry> m_PresentationHistory =
        new List<ChatPresentationHistoryEntry>();
    private ReadOnlyCollection<ChatPresentationHistoryEntry> m_PresentationHistoryView;
    private int m_PresentationHistoryVersion;
    private string m_PresentationCharacterRawName;
    private string m_PresentationCharacterDisplayName = "角色";
    [SerializeField, HideInInspector] private UniVRM10.VRM10Object m_PresentationNamedModel;
    [SerializeField, HideInInspector] private string m_PresentationModelFileName;

    /// <summary>
    /// The avatar attached to this conversation, not the language model or an ASR speaker.
    /// Read the current bindings so replacing the animator or lip-sync mesh also updates the label.
    /// </summary>
    public string PresentationCharacterName
    {
        get
        {
            Transform source = PresentationAvatarSource();

            string rawName = null;
            if (source != null)
            {
                // Project asset renames do not change VRM's embedded metadata or existing
                // scene name overrides. Prefer the actual model file name for imported VRMs.
                UniVRM10.Vrm10Instance avatar = source.GetComponentInParent<UniVRM10.Vrm10Instance>(true);
                if (avatar != null)
                {
                    rawName = ReadPresentationModelFileName(avatar, true);
                    if (string.IsNullOrWhiteSpace(rawName)) rawName = avatar.gameObject.name;
                    if (string.IsNullOrWhiteSpace(rawName) || rawName == "VRM1" || rawName == "VRM1(Clone)")
                        rawName = avatar.Vrm != null && avatar.Vrm.Meta != null ? avatar.Vrm.Meta.Name : null;
                }
                else
                {
                    Animator animator = source.GetComponentInParent<Animator>(true);
                    if (animator != null) rawName = animator.gameObject.name;
                }
            }

            rawName = rawName ?? "";
            if (!string.Equals(rawName, m_PresentationCharacterRawName, StringComparison.Ordinal))
            {
                m_PresentationCharacterRawName = rawName;
                string displayName = rawName.Replace('\r', ' ').Replace('\n', ' ').Trim();
                while (displayName.EndsWith("(Clone)", StringComparison.Ordinal))
                    displayName = displayName.Substring(0, displayName.Length - "(Clone)".Length).TrimEnd();
                m_PresentationCharacterDisplayName = string.IsNullOrWhiteSpace(displayName) ? "角色" : displayName;
            }
            return m_PresentationCharacterDisplayName;
        }
    }

    private Transform PresentationAvatarSource()
    {
        if (m_Animator != null) return m_Animator.transform;
        if (m_AudioSource == null) return null;
        Audio2LipScript lips = m_AudioSource.GetComponent<Audio2LipScript>();
        return lips != null && lips.meshRenderer != null ? lips.meshRenderer.transform : null;
    }

    private string ReadPresentationModelFileName(UniVRM10.Vrm10Instance avatar, bool allowEditorLookup)
    {
#if UNITY_EDITOR
        if (allowEditorLookup)
        {
            string path = UnityEditor.AssetDatabase.GetAssetPath(avatar.Vrm);
            if (!path.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase))
            {
                // Metadata can be remapped into a separate .asset; its own file name is not the model name.
                var source = UnityEditor.PrefabUtility.GetCorrespondingObjectFromOriginalSource(avatar.gameObject);
                path = UnityEditor.AssetDatabase.GetAssetPath(source);
            }
            if (!string.IsNullOrEmpty(path)) return System.IO.Path.GetFileNameWithoutExtension(path);
        }
#endif
        // The editor bakes this pair into the build scene. Never reuse a previous avatar's name.
        return avatar.Vrm != null && avatar.Vrm == m_PresentationNamedModel ? m_PresentationModelFileName : null;
    }

#if UNITY_EDITOR
    public void BakePresentationModelFileName()
    {
        Transform source = PresentationAvatarSource();
        var avatar = source != null ? source.GetComponentInParent<UniVRM10.Vrm10Instance>(true) : null;
        m_PresentationNamedModel = null;
        m_PresentationModelFileName = null;
        if (avatar == null) return;
        string fileName = ReadPresentationModelFileName(avatar, true);
        if (string.IsNullOrEmpty(fileName)) return;
        m_PresentationNamedModel = avatar.Vrm;
        m_PresentationModelFileName = fileName;
    }
#endif

    public string PresentationSubtitleSource { get { return m_TextBack != null ? m_TextBack.text : ""; } }
    public string PresentationTranslation
    {
        get { return m_SubtitleOverlay != null ? m_SubtitleOverlay.PresentationTranslation : ""; }
    }
    public bool PresentationShowOriginal
    {
        get
        {
            return m_SubtitleOverlay != null ? m_SubtitleOverlay.PresentationShowOriginal
                : SubtitleOverlay.ShouldShowOriginal(m_SubtitleDisplayMode);
        }
    }
    public bool PresentationShowTranslation
    {
        get { return m_SubtitleOverlay != null && m_SubtitleOverlay.PresentationShowTranslation; }
    }
    public string PresentationNotice
    {
        get { return m_SubtitleOverlay != null ? m_SubtitleOverlay.PresentationNotice : ""; }
    }
    public Color PresentationNoticeColor
    {
        get { return m_SubtitleOverlay != null ? m_SubtitleOverlay.PresentationNoticeColor : Color.white; }
    }
    public int PresentationSubtitleMode
    {
        get { return (int)(m_SubtitleOverlay != null ? m_SubtitleOverlay.DisplayMode : m_SubtitleDisplayMode); }
    }
    public int PresentationSubtitleLanguage
    {
        get { return (int)(m_SubtitleOverlay != null ? m_SubtitleOverlay.TargetLanguage : m_SubtitleTargetLanguage); }
    }
    public int PresentationNoticeMode
    {
        get { return (int)(m_SubtitleOverlay != null ? m_SubtitleOverlay.NoticeMode : m_SystemNoticeMode); }
    }

    /// <summary>
    /// Show the live recognition buffer while recording. Final text retains the existing
    /// label lifetime; an older label-clear coroutine cannot erase a new live partial.
    /// </summary>
    public string PresentationTranscript
    {
        get
        {
            if (PresentationIsTranscribing && m_ShowStreamingTranscript)
                return m_StreamingTranscript ?? "";
            string value = m_RecordTips != null ? m_RecordTips.text : "";
            if (string.IsNullOrEmpty(value) || value == "录音结束，正在识别..." ||
                value == "正在进行语音识别..." || value == "正在听你哼唱…" ||
                value == "♪（哼唱片段）") return "";
            return value;
        }
    }

    public bool PresentationIsTranscribing
    {
        get
        {
            RTSpeechHandler realtime = PresentationRealtime;
            return realtime != null && realtime.IsRecording;
        }
    }

    public RTSpeechHandler PresentationRealtime
    {
        get
        {
            if (m_PresentationRealtime != null && m_PresentationRealtime.ChatOwner == this)
                return m_PresentationRealtime;
            m_PresentationRealtime = null;
            // Usually resolved once. Retry slowly if the speech object is created later.
            if (Time.unscaledTime < m_PresentationNextRealtimeLookup) return null;
            m_PresentationNextRealtimeLookup = Time.unscaledTime + 1f;
            foreach (RTSpeechHandler candidate in FindObjectsOfType<RTSpeechHandler>(true))
            {
                if (candidate.ChatOwner != this) continue;
                m_PresentationRealtime = candidate;
                break;
            }
            return m_PresentationRealtime;
        }
    }

    /// <summary>Cached legacy roots; the new view can hide them without rebinding business state.</summary>
    public Canvas[] PresentationLegacyCanvases
    {
        get
        {
            if (m_PresentationLegacyCanvases == null ||
                m_PresentationChatCanvasSource != m_ChatPanel ||
                m_PresentationHistoryCanvasSource != m_HistoryPanel)
            {
                var canvases = new List<Canvas>(2);
                Canvas chat = m_ChatPanel != null ? m_ChatPanel.GetComponent<Canvas>() : null;
                Canvas history = m_HistoryPanel != null ? m_HistoryPanel.GetComponent<Canvas>() : null;
                if (chat != null) canvases.Add(chat);
                if (history != null && history != chat) canvases.Add(history);
                m_PresentationLegacyCanvases = canvases.ToArray();
                m_PresentationChatCanvasSource = m_ChatPanel;
                m_PresentationHistoryCanvasSource = m_HistoryPanel;
            }
            return m_PresentationLegacyCanvases;
        }
    }

    /// <summary>Uses the normal typed-turn entry, including interruption and cancellation.</summary>
    public void PresentationSubmit(string text)
    {
        if (m_InputWord == null || string.IsNullOrWhiteSpace(text)) return;
        m_InputWord.text = text;
        SendData();
    }

    public IReadOnlyList<ChatPresentationHistoryEntry> PresentationHistory
    {
        get
        {
            RefreshPresentationHistory();
            if (m_PresentationHistoryView == null)
                m_PresentationHistoryView = m_PresentationHistory.AsReadOnly();
            return m_PresentationHistoryView;
        }
    }

    public int PresentationHistoryVersion
    {
        get { RefreshPresentationHistory(); return m_PresentationHistoryVersion; }
    }

    private void RefreshPresentationHistory()
    {
        int count = m_ChatHistory != null ? m_ChatHistory.Count : 0;
        bool changed = count != m_PresentationHistorySource.Count;
        if (!changed)
        {
            for (int i = 0; i < count; i++)
            {
                if (string.Equals(m_ChatHistory[i], m_PresentationHistorySource[i], StringComparison.Ordinal))
                    continue;
                changed = true;
                break;
            }
        }
        if (!changed) return;

        m_PresentationHistorySource.Clear();
        m_PresentationHistory.Clear();
        for (int i = 0; i < count; i++)
        {
            string source = m_ChatHistory[i];
            m_PresentationHistorySource.Add(source);
            bool isUser = i % 2 == 0;
            string visible = ProjectPresentationHistoryText(source, isUser);
            if (visible.Length > 0)
                m_PresentationHistory.Add(new ChatPresentationHistoryEntry(isUser, visible));
        }
        m_PresentationHistoryVersion++;
    }

    private static string ProjectPresentationHistoryText(string source, bool isUser)
    {
        if (string.IsNullOrWhiteSpace(source)) return "";
        string text = source.Trim();
        if (text.StartsWith("[内心]", StringComparison.Ordinal) ||
            text.StartsWith("[感知帧 ", StringComparison.Ordinal) ||
            text.StartsWith("[程序感知 tick", StringComparison.Ordinal) ||
            text.StartsWith("[歌曲记忆工具结果]", StringComparison.Ordinal) ||
            text == "<silent/>" || text == "<empty/>") return "";

        // User rows contain their original words; leave quotations and angle brackets
        // intact. Assistant rows use the same channel parser as the speech pipeline.
        if (isUser) return text;
        int innerTail = text.IndexOf("[内心]", StringComparison.Ordinal);
        if (innerTail >= 0) text = text.Substring(0, innerTail);
        return RoleOutputChannels.Parse(text).Speech.Trim();
    }
}
