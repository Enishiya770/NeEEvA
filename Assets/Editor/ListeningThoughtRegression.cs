using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Actual listening callbacks, withdrawal and side-effect isolation; no HTTP, microphone or TTS.</summary>
public static class ListeningThoughtRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const string Source = "今日は仕事で少し疲れていて";
    private const string Reaction = "少し心配だな。もう少し聞いていたい。";
    private static int checks;

    public static int RunOrThrow()
    {
        checks = 0;
        var host = new GameObject("Inactive listening thought regression");
        host.SetActive(false); // ChatSample.Awake intentionally requires a real scene's buttons.
        try
        {
            var chat = host.AddComponent<ChatSample>();
            var model = host.AddComponent<ListeningThoughtFixtureModel>();
            Set(chat, "m_ChatSettings", new ChatSetting { m_ChatModel = model });
            Set(chat, "m_InputAudioRevision", 4);
            Set(chat, "m_LogSpeculativeListening", false);
            Set(chat, "m_ChatHistory", new System.Collections.Generic.List<string>());
            int historyBefore = ((ICollection)Get(chat, "m_ChatHistory")).Count;
            Action<string> callback = Prepare(chat, model, 40);
            callback(Wire(Reaction));
            Check(model.ListeningCalls == 1 && model.EphemeralCalls == 0,
                "ordinary thought shares the single listening request");
            Check(Text(chat) == Reaction && Get(chat, "m_SpeculativeDraft") == null,
                "a real callback publishes inner_reaction even when draft is empty");
            Check(model.m_DataList.Count == 0 && ((ICollection)Get(chat, "m_ChatHistory")).Count == historyBefore &&
                ((ICollection)Get(chat, "m_PendingChunks")).Count == 0 && !chat.IsAISpeaking,
                "display never writes history or queues spoken audio");
            Check(chat.PresentationThoughtText == "", "disabled owner is not presented");

            int version = chat.PresentationThoughtVersion;
            callback(Wire("ゆっくり聞こう。"));
            Check(Text(chat) == "ゆっくり聞こう。" && chat.PresentationThoughtVersion > version,
                "a new reaction replaces the earlier reaction");
            callback(Wire(""));
            Check(Text(chat) == "", "empty reaction retracts instead of inventing a thought");
            callback(Wire(Reaction));
            Call(chat, "ReconcileListeningThought", Source + "、少し休みたい");
            Check(Text(chat) == "", "every appended phrase withdraws an earlier interpretation before semantics can change");
            var evidence = (PreparedTurnBoundaryEvidence)Get(chat, "m_PreparedBoundaryEvidence");
            evidence.Transcript = "明日の映画は何時から始まるの";
            evidence.TextRevision++;
            chat.ObservePreparedTurnBoundaryEvidence(evidence);
            Check(Text(chat) == "", "ASR correction withdraws an already completed preview with no request in flight");
            evidence.Transcript = "";
            chat.ObservePreparedTurnBoundaryEvidence(evidence);
            callback(Wire(Reaction));
            Check(Text(chat) == "", "empty raw ASR revision rejects a callback even while legacy streaming text is retained");

            callback = Prepare(chat, model, 41);
            callback(Wire(Reaction));
            Call(chat, "ReconcileListeningThought", "今日は仕事で");
            Check(Text(chat) == "", "ASR prefix rollback removes thoughts based on withdrawn words");
            callback(Wire(Reaction));
            Call(chat, "CancelSpeculativeRequestWithReason", "mature-eou");
            callback(Wire("遅れて届いた古い考え。"));
            Check(Text(chat) == "", "cancelled late provider callback cannot resurrect a thought");

            callback = Prepare(chat, model, 42);
            callback(Wire(Reaction));
            evidence = (PreparedTurnBoundaryEvidence)Get(chat, "m_PreparedBoundaryEvidence");
            evidence.CaptureSerial++;
            chat.ObservePreparedTurnBoundaryEvidence(evidence);
            callback(Wire("前の録音の考え。"));
            Check(Text(chat) == "", "new recorder capture invalidates preview and rejects old-capture callback");

            callback = Prepare(chat, model, 44);
            Call(chat, "InvalidateListeningThoughtPresentation");
            callback(Wire(Reaction));
            Check(Text(chat) == "", "disable epoch rejects callbacks from before disable/re-enable");

            callback = Prepare(chat, model, 45);
            callback(Wire(Reaction));
            Set(chat, "m_ListeningThoughtReceivedAt", Time.realtimeSinceStartup - 8f);
            Call(chat, "MaintainListeningThought");
            Check(Text(chat) == "", "unrefreshed reaction expires without another provider callback");
            callback(Wire(Reaction));
            typeof(ChatSample).GetProperty("IsAISpeaking", Flags).GetSetMethod(true).Invoke(chat, new object[] { true });
            Call(chat, "MaintainListeningThought");
            Check(Text(chat) == "", "spoken reply clears the preview");
            typeof(ChatSample).GetProperty("IsAISpeaking", Flags).GetSetMethod(true).Invoke(chat, new object[] { false });
            Check(Text(chat) == "", "preview stays cleared when speech ends");
            callback(Wire(Reaction));
            evidence = (PreparedTurnBoundaryEvidence)Get(chat, "m_PreparedBoundaryEvidence");
            evidence.Recording = false;
            chat.ObservePreparedTurnBoundaryEvidence(evidence);
            callback(Wire(Reaction));
            Check(Text(chat) == "", "closed capture removes preview and rejects late arrival");

            // Singing uses the same versioned lightweight preparation interface.
            PrepareEvidence(chat, 46);
            Set(chat, "m_StreamingTurnIsSinging", true);
            Set(chat, "m_StreamingSingingEvidence", "stable humming with no claimed song title");
            Set(chat, "m_LastDraftTranscript", "");
            var singing = (IEnumerator)Call(chat, "RequestSingingDraftAfterDelay", 1, "unused snapshot");
            while (singing.MoveNext()) { }
            Check(model.EphemeralCalls == 0, "singing preparation does not fall back to a full-history generic ephemeral request");
            // Avoid scheduling a fresh coroutine on this deliberately inactive fixture.
            Set(chat, "m_EnableSingingSpeculativeReaction", false);
            model.Prepared(Wire("このメロディー、もう少し聴いていたい。", "singing"));
            Check(Text(chat) == "このメロディー、もう少し聴いていたい。" &&
                ((ICollection)Get(chat, "m_PendingChunks")).Count == 0,
                "singing inner reaction displays independently of audible draft");

            foreach (string payload in new[] { "<say>台词</say>", "<motion name=\"wave\"/>",
                "{\"draft\":\"台词\"}", "```json", "[LLM/Decision] key=value", "[执行事实：已经完成]" })
                Check(Sanitize(payload) == "", "structured/control output cannot become a bubble: " + payload);
            Check(Sanitize("  少し\n\t気になる。 ") == "少し 気になる。", "plain multiline prose becomes a compact sentence");
            string longText = string.Concat(System.Linq.Enumerable.Repeat("🌸", 50));
            string bounded = Sanitize(longText);
            Check(new StringInfo(bounded).LengthInTextElements == 40 && bounded.EndsWith("…"),
                "overlong output is bounded without breaking surrogate pairs");
            return checks;
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static Action<string> Prepare(ChatSample chat, ListeningThoughtFixtureModel model, int capture)
    {
        PrepareEvidence(chat, capture);
        var coroutine = (IEnumerator)Call(chat, "RequestSpeculativeDraftAfterDelay", 1, Source);
        while (coroutine.MoveNext()) { }
        return model.Prepared ?? throw new InvalidOperationException("No preparation was dispatched");
    }

    private static void PrepareEvidence(ChatSample chat, int capture)
    {
        Set(chat, "m_SpeculativeRequestInFlight", false);
        Set(chat, "m_StreamingTurnIsSinging", false);
        Set(chat, "m_StreamingTranscriptVersion", 1);
        Set(chat, "m_StreamingTranscript", Source);
        Set(chat, "m_StreamingLatestAudioMs", 1000);
        chat.ObservePreparedTurnBoundaryEvidence(new PreparedTurnBoundaryEvidence {
            CaptureSerial = capture, TextRevision = 1, ActivityRevision = 1, AudioMs = 1000,
            Transcript = Source, SampledAt = Time.realtimeSinceStartup, Recording = true,
            AsrHealthy = true, FreshCoverage = true, CoversQuietTail = true, TailAvailable = true,
            MinimumConfidence = .78f, MinimumQuietSeconds = .35f,
        });
    }

    private static string Wire(string reaction, string mode = "speech") => new JObject {
        ["inner_reaction"] = reaction, ["draft"] = "", ["language"] = "ja", ["confidence"] = .8f,
        ["preparation_version"] = 1, ["reaction_kind"] = string.IsNullOrEmpty(reaction) ? "none" : "tentative",
        ["intention"] = new JObject { ["state"] = "open", ["domain"] = "conversation",
            ["goal"] = "今の疲れについて話している", ["target"] = "", ["missing"] = new JArray("続き"),
            ["evidence"] = new JArray(Source), ["segments"] = new JArray() },
        ["observed_mode"] = mode, ["mode_confidence"] = .9f,
        ["quiet_boundary"] = new JObject { ["action"] = "continue", ["confidence"] = .9f,
            ["mode"] = mode, ["source"] = "user", ["turn_state"] = "open", ["reason"] = "まだ聞いている" },
    }.ToString();
    private static string Text(ChatSample chat) => (string)Get(chat, "m_ListeningThoughtText");
    private static string Sanitize(string text) => (string)typeof(ChatSample).GetMethod("SanitizeListeningThought",
        BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { text });
    private static object Get(object target, string field) => target.GetType().GetField(field, Flags).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Flags).SetValue(target, value);
    private static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Flags).Invoke(target, args);
    private static void Check(bool passed, string label)
    { checks++; if (!passed) throw new InvalidOperationException("Listening thought: " + label); }
}

public sealed class ListeningThoughtFixtureModel : LLM
{
    public int ListeningCalls, EphemeralCalls;
    public Action<string> Prepared, Ephemeral;
    public override void PostListeningPreparationMsg(string prompt, Action<string> callback)
    { ListeningCalls++; Prepared = callback; }
    public override void PostEphemeralMsg(string prompt, Action<string> callback)
    { EphemeralCalls++; Ephemeral = callback; }
}
