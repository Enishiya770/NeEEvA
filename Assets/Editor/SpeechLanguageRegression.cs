using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>Behavioral checks for declared language, streaming boundaries, gates and audio identity.</summary>
public static class SpeechLanguageRegression
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;

    [MenuItem("Tools/NeEEvA/Run Speech Language Regression")]
    public static void RunInteractive()
    {
        RunOrThrow();
        Debug.Log($"[SpeechLanguageRegression] passed {checks} checks");
    }

    public static void RunBatch()
    {
        try { RunInteractive(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    private static void RunOrThrow()
    {
        RunLanguageRouting();
        // Preserve existing plain-speech, private channel, quoted-tool and malformed-tool behavior.
        foreach (string test in new[] { "RunRoleOutputChannelsRegression", "RunPlainSpeechPromptRegression" })
            typeof(SkillRoutingRegression).GetMethod(test, Static).Invoke(null, null);
    }

    // Isolates language routing from the broader LLM SSE/prompt regression suite.
    public static void RunLanguageRouting()
    {
        checks = 0;
        RunParser();
        RunQueue();
        RunCollectedReply();
        RunTts();
        RunSanitizedLanguageRetention();
        RunCacheIdentity();
        Debug.Log($"[SpeechLanguageRouting] passed {checks} checks");
    }

    private static string Signature(List<SpeechText> parts)
    {
        var buffer = new SpeechTextBuffer();
        foreach (SpeechText part in parts) buffer.Append(part);
        var result = new StringBuilder();
        foreach (SpeechText part in buffer.Snapshot())
            result.Append(part.LanguageCode ?? "auto").Append(':').Append(part.Text).Append('|');
        return result.ToString();
    }

    private static void RunParser()
    {
        var cases = new Dictionary<string, string>
        {
            { "<lang code=\"zh\"/>听到哦。<lang code=\"ja\"/>了解。アントネーワです。", "zh:听到哦。|ja:了解。アントネーワです。|" },
            { "<lang code='ja'/>了解。<lang code='zh'/>了解。", "ja:了解。|zh:了解。|" },
            { "<lang code=\"en\"/>Understood.<lang code=\"ja\"/>はい。", "en:Understood.|ja:はい。|" },
            { "<lang code=\"ja\"/>これは<lang code=\"en\"/>soundstage<lang code=\"ja\"/>の話ね。", "ja:これは|en:soundstage|ja:の話ね。|" },
            { "<lang code=\"ja\"/>もう一度、<lang code=\"zh\"/>「黑化肥，灰发会发黑」<lang code=\"ja\"/>よ。", "ja:もう一度、|zh:「黑化肥，灰发会发黑」|ja:よ。|" },
            { "<lang code=\"ja\"/>はい。<thought><lang code=\"zh\"/>秘密。</thought>了解。<silent/><lang code=\"en\"/>secret", "ja:はい。了解。|" },
            { "<lang code=\"ja\"/><say>了解。</say><next in=\"5s\"/>", "ja:了解。\n|" },
            { "<lang code=\"ja\"/>はい。<lang code=\"bad\"/>听到哦。", "ja:はい。|auto:听到哦。|" },
            { "<lang code=\"ja\"/>はい。<lang code=", "ja:はい。|" },
            { "听到哦。", "auto:听到哦。|" },
            { "<thought><lang code=\"zh\"/>秘密。</thought>了解。", "auto:了解。|" },
        };
        foreach (var test in cases)
        {
            // Every possible two-packet boundary, plus byte-sized text deltas below.
            for (int split = 0; split <= test.Key.Length; split++)
            {
                var parts = new List<SpeechText>();
                var router = new RoleOutputChannels(parts.Add);
                string plain = router.Push(test.Key.Substring(0, split));
                plain += router.Push(test.Key.Substring(split));
                plain += router.Finish();
                Check(Signature(parts) == test.Value, "Language/packet mismatch: " + test.Key + " at " + split);
                Check(plain == router.Speech && !plain.Contains("<lang"), "Metadata leaked into visible speech");
                Check(!router.ToExecutableText().Contains("<lang"), "Metadata reached executable actions");
            }
            var chars = new List<SpeechText>();
            var streamed = new RoleOutputChannels(chars.Add);
            foreach (char c in test.Key) streamed.Push(c.ToString());
            streamed.Finish();
            Check(Signature(chars) == test.Value, "Single-character streaming changed language");
        }
        var old = RoleOutputChannels.Parse("<lang code=\"ja\"/>了解。");
        var fresh = new List<SpeechText>();
        var nextRequest = new RoleOutputChannels(fresh.Add);
        nextRequest.Push("听到哦。");
        Check(Signature(fresh) == "auto:听到哦。|" && nextRequest.HasUndeclaredSpeech,
            "Language leaked into next model request");
        Check(RoleOutputChannels.Parse("<lang code=\"fr\"/>Bonjour.").HasInvalidLanguage,
            "Unsupported declaration was silently accepted");
        Check(RoleOutputChannels.Parse("<lang code=\"ja\"/>了解。<lang code=").HasInvalidLanguage,
            "Truncated declaration was not reported");
    }

    private static void RunQueue()
    {
        var cases = new Dictionary<string, string>
        {
            { "<lang code=\"zh\"/>听到哦。<lang code=\"ja\"/>了解。アントネーワです。<lang code=\"en\"/>Thank you.",
                "zh:听到哦。|ja:了解。アントネーワです。|en:Thank you.|" },
            { "<lang code=\"ja\"/>もう一度、<lang code=\"zh\"/>「黑化肥，灰发会发黑」<lang code=\"ja\"/>よ。",
                "ja:もう一度、|zh:「黑化肥，灰发会发黑」|ja:よ。|" },
        };
        foreach (var test in cases)
        foreach (bool held in new[] { false, true })
        foreach (int packet in new[] { 1, 2, 7, 19, test.Key.Length })
        {
            string raw = test.Key;
            var host = new GameObject("SpeechLanguageQueueRegression");
            host.SetActive(false);
            try
            {
                var chat = host.AddComponent<ChatSample>();
                Set(chat, "m_LogStreamTimings", false);
                Set(chat, "m_HoldSpeechForSongMemoryResult", held);
                var router = new RoleOutputChannels(part =>
                    typeof(ChatSample).GetMethod("OnSpeechStreamDelta", Instance).Invoke(chat, new object[] { part }));
                for (int at = 0; at < raw.Length; at += packet)
                    router.Push(raw.Substring(at, Math.Min(packet, raw.Length - at)));
                router.Finish();
                var queue = (IEnumerable)Get(chat, "m_PendingChunks");
                if (held) Check(!queue.GetEnumerator().MoveNext(), "Held speech escaped the tool gate");
                Set(chat, "m_HoldSpeechForSongMemoryResult", false);
                typeof(ChatSample).GetMethod("FlushCompleteSentences", Instance).Invoke(chat, new object[] { true });
                var parts = new List<SpeechText>();
                foreach (object entry in queue)
                    parts.Add(new SpeechText((string)Get(entry, "Text"), (string)Get(entry, "LanguageCode")));
                Check(Signature(parts) == test.Value,
                    "Chat queue merged languages or lost text, held=" + held + " packet=" + packet);
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }
    }

    private static void RunTts()
    {
        var host = new GameObject("SpeechLanguageTtsRegression");
        host.SetActive(false);
        try
        {
            var tts = host.AddComponent<GPTSoVITSFASTAPI>();
            Set(tts, "m_LogLanguageChanges", false);
            Set(tts, "m_ReferWavPath", "regression-reference.wav");
            tts.EnableAutoTargetLanguage();
            foreach (string code in new[] { "ja", "zh", "en" })
            {
                SpeechText resolved = tts.ResolveSpeech(new SpeechText("了解。", code));
                Check(resolved.LanguageCode == code, "Declared language was overridden by glyph detection");
                foreach (int mode in new[] { 0, 3 })
                {
                    object data = typeof(GPTSoVITSFASTAPI).GetMethod("CreateSpeechRequest", Instance)
                        .Invoke(tts, new object[] { resolved.Text, resolved.LanguageCode, mode });
                    var json = Newtonsoft.Json.Linq.JObject.Parse(JsonUtility.ToJson(data));
                    Check((string)json["text"] == "了解。" && (string)json["text_lang"] == code &&
                        (int)json["streaming_mode"] == mode, "Wrong synthesis payload");
                }
            }
            Check(tts.ResolveSpeech(new SpeechText("听到哦。")).LanguageCode == "zh", "Chinese fallback regressed");
            SpeechText kanaConflict = tts.ResolveSpeech(new SpeechText("わかりました。", "zh"));
            Check(kanaConflict.LanguageCode == "ja" && kanaConflict.LanguageSource == "declared-conflict-auto",
                "Explicit Chinese language was allowed to synthesize kana as Chinese");
            SpeechText simplifiedConflict = tts.ResolveSpeech(new SpeechText("我们现在开始。", "ja"));
            Check(simplifiedConflict.LanguageCode == "zh" && simplifiedConflict.LanguageSource == "declared-conflict-auto",
                "Explicit Japanese language was allowed to synthesize simplified Chinese text");
            // Captured production failure: a one-kana suffix overrode the entire zh
            // quotation. Prefetch and playback must produce identical request languages.
            CheckStableTtsLanguage(tts, "「黑化肥，灰发会发黑」よ。", "zh", "zh", "declared");
            CheckStableTtsLanguage(tts, "黑化肥，灰发会发黑よ。", "zh", "zh", "declared");
            CheckStableTtsLanguage(tts, "「黑化肥，灰发会发黑」です。", "zh", "zh", "declared");
            foreach (string quote in new[] { "「了解」", "『了解』", "“了解”", "\"了解\"" })
                CheckStableTtsLanguage(tts, quote + "です。", "zh", "zh", "declared");
            CheckStableTtsLanguage(tts, "我们现在开始吧。ね。", "zh", "zh", "declared");
            foreach (string code in new[] { "zh", "ja" })
                CheckStableTtsLanguage(tts, "「识时务者为俊杰（识时务なる者は俊杰なり", code, code, "declared");
            CheckStableTtsLanguage(tts, "わかりました。", "zh", "ja", "declared-conflict-auto");
            CheckStableTtsLanguage(tts, "今日はいい天気ですね。", "zh", "ja", "declared-conflict-auto");
            CheckStableTtsLanguage(tts, "了解です。", "zh", "ja", "declared-conflict-auto");
            CheckStableTtsLanguage(tts, "え？", "zh", "ja", "declared-conflict-auto");
            CheckStableTtsLanguage(tts, "我们现在开始。", "ja", "zh", "declared-conflict-auto");
            CheckStableTtsLanguage(tts, "了解。", "ja", "ja", "declared");
            CheckStableTtsLanguage(tts, "「了解」です。", "ja", "ja", "declared");
            CheckStableTtsLanguage(tts, "「黒い肥料」です。", "ja", "ja", "declared");
            CheckStableTtsLanguage(tts, "我们现在は。", null, "ja", "auto-fallback");
            SpeechText fallback = tts.ResolveSpeech(new SpeechText("听到哦。"));
            Check(fallback.LanguageSource == "auto-fallback" &&
                tts.ResolveSpeech(fallback).LanguageSource == "auto-fallback", "Resolved fallback was mislabeled as a model declaration");
            Check(tts.ResolveSpeech(new SpeechText("わかりました。")).LanguageCode == "ja", "Japanese fallback regressed");
            SpeechText privateFirst = tts.ResolveSpeech(new SpeechText("<think>日本語で分析</think>听到哦。"));
            Check(privateFirst.LanguageCode == "zh" && privateFirst.Text == "听到哦。", "Private analysis influenced fallback language detection");
            SpeechText queued = tts.ResolveSpeech(new SpeechText("了解。", "ja"));
            tts.ResolveSpeech(new SpeechText("听到哦。", "zh"));
            Check(tts.ResolveSpeech(queued).LanguageCode == "ja", "Another request changed queued language");
            tts.TrySetTargetLanguage("en");
            Check(tts.ResolveSpeech(new SpeechText("了解。", "ja")).LanguageCode == "en", "Explicit fixed mode lost priority");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void CheckStableTtsLanguage(GPTSoVITSFASTAPI tts, string text, string declared,
        string expected, string expectedSource)
    {
        var speech = new SpeechText(text, declared, actionDependency: SpeechActionDependency.AfterAction,
            suppressTts: true);
        for (int pass = 0; pass < 3; pass++)
        {
            speech = tts.ResolveSpeech(speech);
            Check(speech.Text == text && speech.LanguageCode == expected && speech.LanguageSource == expectedSource,
                "TTS language changed on resolution " + pass + ": " + declared + " / " + text);
            Check(speech.ActionDependency == SpeechActionDependency.AfterAction && speech.SuppressTts,
                "Language resolution discarded the speech segment's metadata");
            foreach (int mode in new[] { 0, 3 })
            {
                object data = typeof(GPTSoVITSFASTAPI).GetMethod("CreateSpeechRequest", Instance)
                    .Invoke(tts, new object[] { speech.Text, speech.LanguageCode, mode });
                var json = Newtonsoft.Json.Linq.JObject.Parse(JsonUtility.ToJson(data));
                Check((string)json["text"] == text && (string)json["text_lang"] == expected &&
                    (int)json["streaming_mode"] == mode, "Repeated resolution changed the synthesis payload");
            }
        }
    }

    private static void RunSanitizedLanguageRetention()
    {
        var source = new List<SpeechText> {
            new SpeechText("嗯，我在听哦。", "ja", "declared")
        };
        MethodInfo rebind = typeof(ChatSample).GetMethod(
            "RebindSanitizedSpeechLanguages", Static);
        var result = (List<SpeechText>)rebind.Invoke(null, new object[] {
            source, "嗯，我在听哦。"
        });
        Check(result.Count == 1 && result[0].LanguageCode == "ja" &&
            result[0].Text == "嗯，我在听哦。",
            "Non-streaming cleanup discarded the declared language");

        var host = new GameObject("SpeechLanguageFallbackIsolationRegression");
        host.SetActive(false);
        try
        {
            var tts = host.AddComponent<GPTSoVITSFASTAPI>();
            Set(tts, "m_LogLanguageChanges", false);
            Set(tts, "m_TargetTextLan", GPTSoVITSFASTAPI.Language.中文);
            tts.EnableAutoTargetLanguage();
            Check(tts.ResolveSpeech(new SpeechText("了解。")).LanguageCode == "zh",
                "Configured Han-only fallback was not used");
            Check(tts.ResolveSpeech(new SpeechText("わかりました。")).LanguageCode == "ja",
                "Kana fallback detection regressed");
            Check(tts.ResolveSpeech(new SpeechText("了解。")).LanguageCode == "zh",
                "Auto fallback leaked the previous request language");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void RunCollectedReply()
    {
        var host = new GameObject("SpeechLanguageCollectedRegression");
        host.SetActive(false);
        try
        {
            var llm = host.AddComponent<SpeechLanguageStubLLM>();
            llm.Response = "<lang code=\"zh\"/>听到哦。<lang code=\"ja\"/>了解。<thought>private</thought><next in=\"5s\"/>";
            bool completed = false;
            llm.PostSpeechMessage("fixture", (parts, full) =>
            {
                completed = true;
                Check(Signature(parts) == "zh:听到哦。|ja:了解。|", "Non-streaming collection lost language");
                Check(full == "听到哦。了解。<next in=\"5s\"/>", "Collected reply lost tools or exposed metadata/private text");
            });
            Check(completed, "Collected reply did not complete");
            llm.Response = "了解。";
            var next = new List<SpeechText>();
            llm.PostSpeechContinuationStream("fixture", next.Add, full => { });
            Check(Signature(next) == "auto:了解。|", "Continuation inherited an old declaration");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void RunCacheIdentity()
    {
        Type chunkType = typeof(ChatSample).GetNestedType("PendingSpeechChunk", BindingFlags.NonPublic);
        Type clipType = typeof(ChatSample).GetNestedType("PendingSpeechClip", BindingFlags.NonPublic);
        object chunk = Activator.CreateInstance(chunkType, true), clip = Activator.CreateInstance(clipType, true);
        foreach (object item in new[] { chunk, clip })
        {
            Set(item, "RoundId", 3); Set(item, "Text", "了解。"); Set(item, "LanguageCode", "ja");
        }
        MethodInfo matches = typeof(ChatSample).GetMethod("MatchesPreparedSpeech", Static);
        Check((bool)matches.Invoke(null, new[] { clip, chunk, "了解。" }), "Matching prefetch missed");
        Set(chunk, "LanguageCode", "zh");
        Check(!(bool)matches.Invoke(null, new[] { clip, chunk, "了解。" }), "Prefetch reused another language");
        Set(chunk, "LanguageCode", "ja"); Set(chunk, "RoundId", 4);
        Check(!(bool)matches.Invoke(null, new[] { clip, chunk, "了解。" }), "Cancelled round's audio was reused");

        var host = new GameObject("SpeechLanguageBridgeRegression");
        host.SetActive(false);
        AudioClip audio = AudioClip.Create("regression", 128, 1, 16000, false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            Set(chat, "m_PreparedSingingBridgeText", "了解。");
            Set(chat, "m_PreparedSingingBridgeLanguage", "ja");
            Set(chat, "m_PreparedSingingBridgeClip", audio);
            MethodInfo take = typeof(ChatSample).GetMethod("TakePreparedFormalReply", Instance);
            Check(take.Invoke(chat, new object[] { "了解。", "zh" }) == null, "Opener cache reused wrong language");
            Check(ReferenceEquals(take.Invoke(chat, new object[] { "了解。", "ja" }), audio), "Matching opener cache missed");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(audio); }
    }

    private static object Get(object target, string name) { return target.GetType().GetField(name, Instance).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, Instance).SetValue(target, value); }
}

public sealed class SpeechLanguageStubLLM : LLM
{
    public string Response;
    public override void PostMsgStream(string message, Action<string> onDelta, Action<string> onComplete,
        string imageDataUrl = null, bool recordAssistantHistory = true)
    {
        foreach (char c in Response) onDelta(c.ToString());
        onComplete(Response);
    }
}
