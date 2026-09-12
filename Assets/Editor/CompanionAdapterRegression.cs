using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Offline checks for the projection from real chat state to the new UI.</summary>
public static class CompanionAdapterRegression
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly List<string> checks = new List<string>();

    [Serializable]
    private sealed class Report
    {
        public bool passed;
        public string scope;
        public string error;
        public string[] checks;
    }

    public static void RunBatch()
    {
        string normalized = Application.dataPath.Replace('\\', '/');
        if (!Application.isBatchMode || !normalized.Contains("/Server/ARDY/runtime/unity-naturalness-validation/"))
            throw new InvalidOperationException("Run CompanionAdapterRegression in the isolated naturalness validation project.");
        var report = new Report
        {
            scope = "Actual ChatSample, SubtitleOverlay and RTSpeechHandler presentation getters with inactive offline fixtures. No microphone, network, private scene, headset or visual claims."
        };
        try
        {
            checks.Clear();
            History(); Channels(); Realtime();
            report.passed = true;
        }
        catch (Exception error) { report.error = error.ToString(); Debug.LogException(error); }
        report.checks = checks.ToArray();
        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/companion-adapter-regression.json", JsonUtility.ToJson(report, true));
        Debug.Log("[CompanionAdapterRegression] " + (report.passed ? "passed " : "failed after ") + checks.Count + " checks");
        EditorApplication.Exit(report.passed ? 0 : 1);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks.Add(name);
    }

    private static void Set(object target, string field, object value)
    {
        FieldInfo member = target.GetType().GetField(field, Fields);
        if (member == null) throw new MissingFieldException(target.GetType().Name, field);
        member.SetValue(target, value);
    }

    private static T Get<T>(object target, string field)
    {
        return (T)target.GetType().GetField(field, Fields).GetValue(target);
    }

    private static GameObject InactiveHost(string name)
    {
        var host = new GameObject(name);
        host.SetActive(false);
        return host;
    }

    private static Text TextView(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var view = go.GetComponent<Text>();
        view.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        return view;
    }

    private static void History()
    {
        var host = InactiveHost("Offline presentation history");
        try
        {
            var chat = host.AddComponent<ChatSample>();
            var source = new List<string>
            {
                "用户引用 <motion name=\"nod\"/>",
                "<thought>私有内容</thought><lang code=\"ja\"/>こんばんは。<motion name=\"nod\"/><silent/>保密尾段",
                "[程序感知 tick；不是新的用户发言。]", "[内心] 不应显示",
                "[感知帧 12:00:00] 内部观察", "<silent/>",
                "[歌曲记忆工具结果] 已完成", "说出的内容。[内心] 私人尾段",
                "第二条用户发言", "<empty/>"
            };
            Set(chat, "m_ChatHistory", source);
            var rows = chat.PresentationHistory;
            int version = chat.PresentationHistoryVersion;
            Check(rows.Count == 4, "internal frames, tools, silent and private history are hidden");
            Check(rows[0].IsUser && rows[0].Role == "user" && rows[0].Text == source[0], "user quotations are preserved");
            Check(!rows[1].IsUser && rows[1].Role == "assistant" && rows[1].Text == "こんばんは。", "assistant spoken channel excludes private tags and tools");
            Check(rows[2].Text == "说出的内容。" && !rows[2].IsUser, "inline private tail is hidden without shifting source roles");
            Check(rows[3].Text == source[8] && rows[3].IsUser, "roles retain original source index after filtering");
            Check(ReferenceEquals(rows, chat.PresentationHistory) && version == chat.PresentationHistoryVersion, "unchanged history reuses snapshot without version churn");
            Check(source.Count == 10 && source[1].Contains("私有内容"), "UI projection leaves business history intact");
            source[8] = "原位置被更新";
            Check(chat.PresentationHistory[3].Text == source[8] && chat.PresentationHistoryVersion > version, "same-count history edits invalidate the projection");
            source.Clear();
            Check(chat.PresentationHistory.Count == 0 && ReferenceEquals(rows, chat.PresentationHistory), "clearing history keeps stable read-only collection identity");
            bool rejectsMutation = false;
            try { ((IList<ChatPresentationHistoryEntry>)rows).Add(new ChatPresentationHistoryEntry(true, "bad")); }
            catch (NotSupportedException) { rejectsMutation = true; }
            Check(rejectsMutation, "history consumers cannot mutate projected rows");
            var input = new GameObject("Input", typeof(RectTransform), typeof(InputField)); input.transform.SetParent(host.transform);
            chat.m_InputWord = input.GetComponent<InputField>(); chat.m_InputWord.text = "existing draft";
            chat.PresentationSubmit("   ");
            Check(chat.m_InputWord.text == "existing draft", "empty submission leaves the existing input untouched");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void Channels()
    {
        var host = InactiveHost("Offline presentation channels");
        var legacy = new GameObject("Legacy canvas", typeof(RectTransform), typeof(Canvas));
        var historyCanvas = new GameObject("Legacy history", typeof(RectTransform), typeof(Canvas));
        try
        {
            var chat = host.AddComponent<ChatSample>();
            var overlay = host.AddComponent<SubtitleOverlay>();
            var container = new GameObject("Original channel", typeof(RectTransform)); container.transform.SetParent(legacy.transform, false);
            var original = TextView("Original", container.transform); original.text = "現在の台詞。";
            var transcript = TextView("Recognition", legacy.transform);
            Set(chat, "m_TextBack", original); Set(chat, "m_SubtitleOverlay", overlay); Set(chat, "m_RecordTips", transcript);
            Set(chat, "m_ChatPanel", legacy); Set(chat, "m_HistoryPanel", historyCanvas);
            overlay.Initialize(original, null, SubtitleDisplayMode.Bilingual, SubtitleLanguage.ChineseSimplified,
                SystemNoticeMode.ErrorsOnly, false, false, false);
            Get<Text>(overlay, "m_TranslationText").text = "当前台词。";
            legacy.GetComponent<Canvas>().enabled = false;
            legacy.SetActive(false);
            Check(chat.PresentationShowOriginal && chat.PresentationShowTranslation, "channel visibility survives a hidden legacy canvas");
            Check(chat.PresentationSubtitleSource == original.text && chat.PresentationTranslation == "当前台词。", "hidden canvas still supplies original and revealed translation");
            Check(chat.PresentationSubtitleMode == 3 && chat.PresentationSubtitleLanguage == 0 && chat.PresentationNoticeMode == 1, "preferences expose exact existing enum values");
            overlay.SetDisplayMode(SubtitleDisplayMode.Hidden);
            Check(!chat.PresentationShowOriginal && !chat.PresentationShowTranslation, "hidden subtitle mode hides both channels");
            overlay.SetDisplayMode(SubtitleDisplayMode.TranslationOnly);
            overlay.BeginUtterance(17); overlay.QueueTranslationChunk(17, "你好。", true);
            Check(chat.PresentationShowOriginal && chat.PresentationShowTranslation, "translation fallback retains original even under hidden legacy canvas");
            var notice = Get<Text>(overlay, "m_SystemNoticeText");
            notice.text = "服务暂时不可用"; notice.color = Color.yellow; notice.gameObject.SetActive(true);
            Check(chat.PresentationNotice == notice.text && chat.PresentationNoticeColor == Color.yellow, "current notice is separate and readable under hidden ancestors");
            Set(overlay, "m_NoticeVersion", 7);
            var expiry = (IEnumerator)typeof(SubtitleOverlay).GetMethod("HideNoticeAfter", Fields).Invoke(overlay, new object[] { 7, 1f });
            expiry.MoveNext(); expiry.MoveNext();
            Check(chat.PresentationNotice == "", "actual notice expiry removes the presentation notice");
            transcript.text = "录音结束，正在识别...";
            Check(chat.PresentationTranscript == "", "recognition progress is not attributed to user speech");
            transcript.text = "正在听你哼唱…";
            Check(chat.PresentationTranscript == "", "wordless singing progress is not a transcript");
            transcript.text = "我今天想聊聊音乐 …";
            Check(chat.PresentationTranscript == transcript.text, "current recognition remains available with hidden legacy UI");
            transcript.text = "";
            Check(chat.PresentationTranscript == "", "cleared recognition does not replay stale text");
            var canvases = chat.PresentationLegacyCanvases;
            Check(canvases.Length == 2 && ReferenceEquals(canvases, chat.PresentationLegacyCanvases), "legacy canvas lookup is cached and includes chat plus history");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
            UnityEngine.Object.DestroyImmediate(legacy);
            UnityEngine.Object.DestroyImmediate(historyCanvas);
        }
    }

    private static void Realtime()
    {
        var host = InactiveHost("Offline realtime state");
        try
        {
            var chat = host.AddComponent<ChatSample>(); var speech = host.AddComponent<RTSpeechHandler>();
            Set(speech, "m_ChatSample", chat);
            Check(!speech.IsRealtimeEnabled && !speech.IsRealtimeClosing && !speech.IsRecording, "initial speech state is read without microphone startup");
            Set(speech, "m_AwakeState", true); Set(speech, "m_IsRecording", true);
            Check(speech.IsRealtimeEnabled && speech.IsRecording && speech.ChatOwner == chat, "speech state getters reflect the real state machine");
            Check(chat.PresentationRealtime == speech, "realtime lookup binds to its owning chat");
            Set(speech, "m_AwakeState", false); Set(speech, "m_GracefulDisablePending", true); Set(speech, "m_IsRecording", false);
            Check(!speech.IsRealtimeEnabled && speech.IsRealtimeClosing && !speech.IsRecording, "graceful shutdown differs from active recording");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }
}
