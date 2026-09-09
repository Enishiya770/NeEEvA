using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Inspect generated replay samples through production parsing/queue/TTS builders, without audio or tools.</summary>
public static class SpeechLanguageReplayRegression
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void RunBatch()
    {
        try
        {
            SpeechLanguageRegression.RunInteractive();
            RunReplay();
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            EditorApplication.Exit(1);
        }
    }

    public static void RunIsolatedBatch()
    {
        try { RunReplay(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    [MenuItem("Tools/NeEEvA/Inspect Generated Speech Replay")]
    public static void RunReplay()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Run this offline inspection outside Play mode.");
        string projectRoot = Environment.GetEnvironmentVariable("NEEEVA_SPEECH_REPLAY_ROOT");
        if (string.IsNullOrEmpty(projectRoot)) projectRoot = Path.Combine(Application.dataPath, "..");
        string folder = Path.GetFullPath(Path.Combine(projectRoot, "Logs/speech-language-check"));
        var input = JObject.Parse(File.ReadAllText(Path.Combine(folder, "replay.json")));
        var results = new JArray();
        int requests = 0, rejected = 0;
        foreach (JObject record in (JArray)input["records"])
        {
            var host = new GameObject("SpeechReplayOfflineInspection");
            host.SetActive(false);
            try
            {
                var chat = host.AddComponent<ChatSample>();
                var tts = host.AddComponent<GPTSoVITSFASTAPI>();
                Set(chat, "m_LogStreamTimings", false);
                Set(chat, "m_LogSpeculativeListening", false);
                Set(tts, "m_LogLanguageChanges", false);
                Set(tts, "m_ReferWavPath", "offline-replay-reference.wav");
                tts.EnableAutoTargetLanguage();
                string raw = (string)record["raw"];
                var source = new List<SpeechText>();
                var chunks = new List<SpeechText>();
                var result = new JObject { ["id"] = record["id"], ["kind"] = record["kind"] };
                if ((string)record["kind"] == "draft")
                {
                    object draft = typeof(ChatSample).GetMethod("ParseSpeculativeDraft", Instance)
                        .Invoke(chat, new object[] { raw });
                    if (draft == null)
                    {
                        result["status"] = "rejected_by_production_json_parser";
                        results.Add(result);
                        rejected++;
                        continue;
                    }
                    string text = (string)Get(draft, "draft");
                    string code = (string)Get(draft, "language");
                    MethodInfo strip = typeof(ChatSample).GetMethod("StripAgentTagsForTTS", Static | Instance);
                    text = ((string)strip.Invoke(chat, new object[] { text })).Trim();
                    source.Add(new SpeechText(text, code));
                    // This verifies the parsed draft's text/language fields. Admission,
                    // confidence, truncation and bridge scheduling are not simulated.
                    if (text.Length > 0) chunks.Add(new SpeechText(text, code));
                    result["path"] = "parsed_draft_fields_to_tts_builder";
                }
                else
                {
                    var router = new RoleOutputChannels(part =>
                    {
                        source.Add(part);
                        typeof(ChatSample).GetMethod("OnSpeechStreamDelta", Instance)
                            .Invoke(chat, new object[] { part });
                    });
                    // Split inside both tags and prose, using production incremental parsing.
                    for (int at = 0; at < raw.Length; at += 7)
                        router.Push(raw.Substring(at, Math.Min(7, raw.Length - at)));
                    router.Finish();
                    typeof(ChatSample).GetMethod("FlushCompleteSentences", Instance)
                        .Invoke(chat, new object[] { true });
                    foreach (object chunk in (IEnumerable)Get(chat, "m_PendingChunks"))
                        chunks.Add(new SpeechText((string)Get(chunk, "Text"), (string)Get(chunk, "LanguageCode")));
                    result["invalid_language"] = router.HasInvalidLanguage;
                    result["undeclared_speech"] = router.HasUndeclaredSpeech;
                    result["path"] = "incremental_parser_to_chat_queue_to_tts_builder";
                }
                var sourceBuffer = new SpeechTextBuffer();
                foreach (SpeechText part in source) sourceBuffer.Append(part);
                var spans = new JArray();
                foreach (SpeechText part in sourceBuffer.Snapshot())
                    spans.Add(new JObject { ["text"] = part.Text, ["language"] = part.LanguageCode });
                result["source_segments"] = spans;
                var payloads = new JArray();
                foreach (SpeechText chunk in chunks)
                {
                    SpeechText resolved = tts.ResolveSpeech(chunk);
                    if (chunk.LanguageCode != null && resolved.LanguageCode != chunk.LanguageCode)
                        throw new Exception("Declared language changed: " + record["id"]);
                    if (resolved.Text.Contains("<lang"))
                        throw new Exception("Language markup reached TTS: " + record["id"]);
                    foreach (int mode in new[] { 0, 3 })
                    {
                        object data = typeof(GPTSoVITSFASTAPI).GetMethod("CreateSpeechRequest", Instance)
                            .Invoke(tts, new object[] { resolved.Text, resolved.LanguageCode, mode });
                        var json = JObject.Parse(JsonUtility.ToJson(data));
                        if ((string)json["text_lang"] != resolved.LanguageCode ||
                            (string)json["text"] != resolved.Text || (int)json["streaming_mode"] != mode)
                            throw new Exception("TTS payload lost text/language: " + record["id"]);
                        payloads.Add(new JObject { ["text"] = json["text"], ["text_lang"] = json["text_lang"],
                            ["source"] = resolved.LanguageSource, ["streaming_mode"] = mode });
                        requests++;
                    }
                }
                result["status"] = "routed_without_synthesis";
                result["tts_requests"] = payloads;
                results.Add(result);
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }
        File.WriteAllText(Path.Combine(folder, "replay-routing.json"), new JObject {
            ["input_sha256"] = Sha256(Path.Combine(folder, "replay.json")),
            ["records"] = results, ["tts_payloads"] = requests, ["rejected_drafts"] = rejected,
            ["limitations"] = "No TTS HTTP calls/audio playback; draft admission and bridge scheduling are not simulated."
        }.ToString());
        Debug.Log($"[SpeechLanguageReplay] inspected {results.Count} records; {requests} TTS payloads; {rejected} rejected drafts");
    }

    private static string Sha256(string path)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
    }

    private static object Get(object target, string field) => target.GetType().GetField(field, Instance).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Instance).SetValue(target, value);
}
