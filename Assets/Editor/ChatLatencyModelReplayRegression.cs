using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// Offline replay of a PUBLIC capture. No request is submitted and no response
// completion/action executor or audio consumer is started.
public static class ChatLatencyModelReplayRegression
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int checks;

    public static void RunBatch()
    {
        var report = new JObject {
            ["scope"] = "Actual captured public SSE bytes through production ChatQW handler, RoleOutputChannels and ChatSample first-chunk queue. Transport receive times are replayed observations; no new model request, private history, tools, TTS, physical speaker or user-end-to-audio measurement.",
            ["cases"] = new JArray()
        };
        try
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int option = Array.IndexOf(arguments, "-chatLatencyModelReport");
            if (option < 0 || option + 1 >= arguments.Length)
                throw new ArgumentException("Supply -chatLatencyModelReport with model-stream-capture.json from validate_chat_latency_stream.py.");
            string sourcePath = Path.GetFullPath(arguments[option + 1]);
            byte[] source = File.ReadAllBytes(sourcePath);
            var capture = JObject.Parse(System.Text.Encoding.UTF8.GetString(source).TrimStart('\uFEFF'));
            Check((bool?)capture["publicSyntheticInputsOnly"] == true && (bool?)capture["transportComplete"] == true,
                "Only a completed public synthetic model capture may be replayed.");
            Check(((JArray)capture["cases"]).Count == 6, "Expected all six public conversations.");
            report["source"] = sourcePath;
            using (var sha = SHA256.Create()) report["sourceSha256"] = BitConverter.ToString(sha.ComputeHash(source)).Replace("-", "");
            foreach (JObject sample in capture["cases"])
                ((JArray)report["cases"]).Add(Replay(sample));
            report["passed"] = true;
        }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); Debug.LogException(error); }
        report["checks"] = checks;
        string path = Path.GetFullPath("Tools/MotionAdapter/reports/chat-latency-model-replay-regression.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, report + "\n");
        EditorApplication.Exit((bool)report["passed"] ? 0 : 1);
    }

    private static JObject Replay(JObject sample)
    {
        var host = new GameObject("InactivePublicSseReplay_" + (string)sample["id"]); host.SetActive(false);
        object handler = null;
        try
        {
            var chat = host.AddComponent<ChatSample>(); chat.enabled = false;
            Set(chat, "m_AgentRunning", true); Set(chat, "m_CurrentSpeechRoundId", 100);
            Set(chat, "m_LogStreamTimings", false); Set(chat, "m_LogAgentLoop", false);
            Set(chat, "m_PersistSubtitleSettings", false); Set(chat, "m_EnableSubtitleTranslation", false);
            // Stress the permission boundary even for ordinary chat: an explicit
            // independent phase must stream when the singing skill remains loaded.
            ((ISet<string>)Get(chat, "m_ActiveSkillsThisRound")).Add("singing");
            var queue = (ICollection)Get(chat, "m_PendingChunks");
            Type type = typeof(ChatQW).GetNestedType("SSEDownloadHandler", Flags);
            double deliveredAt = 0;
            double? firstSpeechAt = null, firstQueuedAt = null;
            bool queuedBeforeModelFinish = false;
            var emitted = new List<SpeechText>();
            var channels = new RoleOutputChannels(part => {
                emitted.Add(part);
                if (!firstSpeechAt.HasValue && !string.IsNullOrWhiteSpace(part.Text)) firstSpeechAt = deliveredAt;
                typeof(ChatSample).GetMethod("OnSpeechStreamDelta", Flags).Invoke(chat, new object[] { part });
                if (!firstQueuedAt.HasValue && queue.Count > 0)
                {
                    firstQueuedAt = deliveredAt;
                    queuedBeforeModelFinish = string.IsNullOrEmpty((string)type.GetProperty("FinishReason", Flags).GetValue(handler));
                }
            });
            handler = Activator.CreateInstance(type, new object[] { new Action<string>(delta => channels.Push(delta)) });
            var wire = new MemoryStream();
            foreach (JObject packet in sample["wireChunks"])
            {
                deliveredAt = (double)packet["elapsedSeconds"];
                byte[] bytes = Convert.FromBase64String((string)packet["base64"]);
                wire.Write(bytes, 0, bytes.Length);
                type.GetMethod("ReceiveData", Flags).Invoke(handler, new object[] { bytes, bytes.Length });
            }
            string received = (string)type.GetMethod("GetFullContent", Flags).Invoke(handler, null);
            string finish = (string)type.GetProperty("FinishReason", Flags).GetValue(handler);
            int queuedBeforeParserFinish = queue.Count;
            channels.Finish();
            object metrics = type.GetField("Performance", Flags).GetValue(handler);
            var productionMetrics = (JObject)metrics.GetType().GetMethod("Snapshot", Flags).Invoke(metrics, null);
            Check(received == (string)sample["content"], "Production SSE content disagreed with capture for " + (string)sample["id"]);
            using (var sha = SHA256.Create())
                Check(BitConverter.ToString(sha.ComputeHash(wire.ToArray())).Replace("-", "").Equals((string)sample["responseSseSha256"], StringComparison.OrdinalIgnoreCase), "Captured wire hash mismatched.");
            Check(finish == "stop", "The production parser did not observe a normal model finish.");
            Check(channels.HasSpeech && channels.SpeechDependency == SpeechActionDependency.Independent, "Public ordinary reply did not explicitly select independent speech.");
            Check(!channels.HasInvalidSpeechPhase && !channels.HasSingingValidationActions, "Public conversation attempted an invalid phase or a dependent singing/material action.");
            var spokenParts = emitted.Where(p => !string.IsNullOrWhiteSpace(p.Text)).ToList();
            Check(!channels.HasInvalidLanguage && !channels.HasUndeclaredSpeech && spokenParts.All(p => p.LanguageCode == "ja"), "A spoken part did not declare the fixture's Japanese language.");
            Check(spokenParts.Count > 0 && spokenParts.All(p => p.ActionDependency == SpeechActionDependency.Independent), "An actual non-whitespace streamed speech part lost its independent phase.");
            Check(firstQueuedAt.HasValue && queuedBeforeParserFinish > 0 && queuedBeforeModelFinish,
                "The first usable speech chunk did not reach the production queue before the model finish event.");
            return new JObject {
                ["id"] = sample["id"], ["passed"] = true, ["speech"] = channels.Speech,
                ["privateCharacters"] = channels.PrivateCharacters, ["dependency"] = channels.SpeechDependency.ToString(),
                ["executableProjection"] = channels.ToExecutableText(true, true),
                ["firstParsedSpeechSeconds"] = firstSpeechAt, ["firstQueuedChunkSeconds"] = firstQueuedAt,
                ["queuedBeforeModelFinish"] = queuedBeforeModelFinish, ["queuedChunksBeforeParserFinish"] = queuedBeforeParserFinish,
                ["finishReason"] = finish, ["productionPerformance"] = productionMetrics,
                ["clientTimings"] = sample["clientTimings"].DeepClone()
            };
        }
        finally
        {
            if (handler != null) ((IDisposable)handler).Dispose();
            UnityEngine.Object.DestroyImmediate(host);
        }
    }

    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
    private static object Get(ChatSample chat, string name) => typeof(ChatSample).GetField(name, Flags).GetValue(chat);
    private static void Set(ChatSample chat, string name, object value) => typeof(ChatSample).GetField(name, Flags).SetValue(chat, value);
}
